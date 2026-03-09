using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using VenueStatusAndGreet.Models;

namespace VenueStatusAndGreet.Services;

public sealed class GreeterService
{
    private readonly IPluginLog log;
    private readonly Func<GuestIdentity, bool> canGreetNow;
    private readonly Func<string, bool> executeChatCommand;

    private readonly ConcurrentQueue<GuestIdentity> queue = new();
    private readonly HashSet<string> queuedKeys = new(StringComparer.OrdinalIgnoreCase);

    private GreetPreset? activePreset;
    private GreetingJob? currentJob;

    public GreeterService(IPluginLog log, Func<GuestIdentity, bool> canGreetNow, Func<string, bool> executeChatCommand)
    {
        this.log = log;
        this.canGreetNow = canGreetNow;
        this.executeChatCommand = executeChatCommand;
    }

    public event Action<GuestIdentity>? GreetingCompleted;

    public string ActivePresetName => this.activePreset?.Name ?? "None";

    public int PendingGreetingCount => this.queue.Count + (this.currentJob is null ? 0 : 1);

    public IReadOnlyList<GuestIdentity> GetQueueSnapshot()
    {
        var list = new List<GuestIdentity>();
        if (this.currentJob is not null)
        {
            list.Add(this.currentJob.Guest);
        }

        list.AddRange(this.queue.ToArray());
        return list;
    }

    public void SetActivePreset(GreetPreset? preset)
    {
        this.activePreset = preset;
    }

    public bool QueueGreeting(GuestIdentity guest)
    {
        if (this.activePreset is null || this.activePreset.NonEmptyLines.Count == 0)
        {
            this.log.Warning($"QueueGreeting rejected for {guest.DisplayName}: no active preset.");
            return false;
        }

        if (!this.canGreetNow(guest))
        {
            this.log.Warning($"QueueGreeting rejected for {guest.DisplayName}: guest not currently present.");
            return false;
        }

        lock (this.queuedKeys)
        {
            if (this.queuedKeys.Contains(guest.Key))
            {
                this.log.Debug($"QueueGreeting ignored for {guest.DisplayName}: already queued.");
                return false;
            }

            this.queue.Enqueue(guest);
            this.queuedKeys.Add(guest.Key);
            this.log.Information($"Queued greeting for {guest.DisplayName}.");
            return true;
        }
    }

    public void Tick(DateTime nowUtc)
    {
        if (this.currentJob is null)
        {
            if (!this.queue.TryDequeue(out var nextGuest))
            {
                return;
            }

            if (this.activePreset is null || this.activePreset.NonEmptyLines.Count == 0)
            {
                this.DropGuest(nextGuest, "No active preset when greeting started.");
                return;
            }

            if (!this.canGreetNow(nextGuest))
            {
                this.DropGuest(nextGuest, "Guest not present when greeting started.");
                return;
            }

            this.currentJob = new GreetingJob(nextGuest, this.activePreset.NonEmptyLines.ToArray(), nowUtc);
            this.log.Information($"Starting greeting job for {nextGuest.DisplayName}.");
        }

        if (nowUtc < this.currentJob.NextSendUtc)
        {
            return;
        }

        if (this.currentJob.LineIndex >= this.currentJob.Lines.Length)
        {
            this.OnGreetingFinished(this.currentJob.Guest);
            return;
        }

        if (!this.canGreetNow(this.currentJob.Guest))
        {
            this.DropGuest(this.currentJob.Guest, "Guest left before greeting line could be sent.");
            this.currentJob = null;
            return;
        }

        var line = this.currentJob.Lines[this.currentJob.LineIndex];
        if (!this.SendTell(this.currentJob.Guest, line))
        {
            this.DropGuest(this.currentJob.Guest, "Tell command failed to process.");
            this.currentJob = null;
            return;
        }

        this.currentJob.LineIndex++;

        if (this.currentJob.LineIndex >= this.currentJob.Lines.Length)
        {
            this.OnGreetingFinished(this.currentJob.Guest);
            return;
        }

        this.currentJob.NextSendUtc = nowUtc.AddSeconds(2);
    }

    private bool SendTell(GuestIdentity guest, string message)
    {
        var cleanMessage = SanitizeMessage(message);
        if (string.IsNullOrWhiteSpace(cleanMessage))
        {
            this.log.Warning($"Skipping empty greeting line after sanitization for {guest.DisplayName}.");
            return true;
        }

        cleanMessage = ReplaceTargetPlaceholder(cleanMessage, guest);
        var commands = this.BuildTellCommandCandidates(guest, cleanMessage);
        foreach (var command in commands)
        {
            try
            {
                var processed = this.executeChatCommand(command);
                this.log.Information($"Sending tell command: {command} (processed={processed})");
                if (processed)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                this.log.Warning(ex, $"Failed to process tell variant for {guest.DisplayName}: {command}");
            }
        }

        this.log.Warning($"All tell command variants failed to process for {guest.DisplayName}.");
        return false;
    }

    private IReadOnlyList<string> BuildTellCommandCandidates(GuestIdentity guest, string message)
    {
        var commands = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safeName = guest.Name.Replace("\"", "'", StringComparison.Ordinal).Trim();
        var safeWorld = NormalizeWorldName(guest.HomeWorld);
        if (!string.IsNullOrWhiteSpace(safeName))
        {
            var fullTarget = string.IsNullOrWhiteSpace(safeWorld) ? safeName : $"{safeName}@{safeWorld}";
            this.AddCommand(commands, seen, $"/tell {fullTarget} {message}");
            this.AddCommand(commands, seen, $"/tell \"{fullTarget}\" {message}");
            this.AddCommand(commands, seen, $"/t {fullTarget} {message}");
        }

        if (commands.Count == 0)
        {
            var fallback = guest.DisplayName.Replace("\"", "'", StringComparison.Ordinal).Trim();
            this.AddCommand(commands, seen, $"/tell {fallback} {message}");
            this.AddCommand(commands, seen, $"/tell \"{fallback}\" {message}");
        }

        return commands;
    }

    private static string SanitizeMessage(string message)
    {
        var clean = message.Replace("\"", "'", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return string.Empty;
        }

        clean = StripTellPrefix(clean);

        if (clean.StartsWith("<t>", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[3..].TrimStart();
        }

        return clean.Trim();
    }

    private static string StripTellPrefix(string value)
    {
        var remaining = value;
        for (var i = 0; i < 2 && remaining.StartsWith("/tell", StringComparison.OrdinalIgnoreCase); i++)
        {
            remaining = remaining[5..].TrimStart();
            if (string.IsNullOrWhiteSpace(remaining))
            {
                return string.Empty;
            }

            if (remaining.StartsWith("\"", StringComparison.Ordinal))
            {
                var quoteEnd = remaining.IndexOf('"', 1);
                if (quoteEnd < 0)
                {
                    return string.Empty;
                }

                remaining = remaining[(quoteEnd + 1)..].TrimStart();
                continue;
            }

            if (remaining.StartsWith("<t>", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining[3..].TrimStart();
                continue;
            }

            var firstSpace = remaining.IndexOf(' ');
            if (firstSpace < 0)
            {
                return string.Empty;
            }

            remaining = remaining[(firstSpace + 1)..].TrimStart();
        }

        return remaining;
    }

    private static string ReplaceTargetPlaceholder(string message, GuestIdentity guest)
    {
        var safeName = guest.Name.Replace("\"", "'", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return message;
        }

        return message.Replace("<t>", safeName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWorldName(string world)
    {
        var clean = world.Replace("\"", "'", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return string.Empty;
        }

        if (clean.Contains("Lumina.Excel.RowRef", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return clean;
    }

    private void AddCommand(List<string> commands, HashSet<string> seen, string command)
    {
        var clean = command.Trim();
        if (seen.Add(clean))
        {
            commands.Add(clean);
        }
    }

    private void OnGreetingFinished(GuestIdentity guest)
    {
        lock (this.queuedKeys)
        {
            this.queuedKeys.Remove(guest.Key);
        }

        this.log.Information($"Completed greeting job for {guest.DisplayName}.");
        this.GreetingCompleted?.Invoke(guest);
        this.currentJob = null;
    }

    private void DropGuest(GuestIdentity guest, string reason)
    {
        lock (this.queuedKeys)
        {
            this.queuedKeys.Remove(guest.Key);
        }

        this.log.Debug($"Dropping greeting entry for {guest.DisplayName}: {reason}");
    }

    private sealed class GreetingJob
    {
        public GreetingJob(GuestIdentity guest, string[] lines, DateTime nowUtc)
        {
            this.Guest = guest;
            this.Lines = lines;
            this.NextSendUtc = nowUtc;
        }

        public GuestIdentity Guest { get; }

        public string[] Lines { get; }

        public int LineIndex { get; set; }

        public DateTime NextSendUtc { get; set; }
    }
}
