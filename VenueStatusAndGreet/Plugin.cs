using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using VenueStatusAndGreet.Models;
using VenueStatusAndGreet.Services;
using VenueStatusAndGreet.Windows;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace VenueStatusAndGreet;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/vsg";

    private readonly WindowSystem windowSystem = new("VenueStatusAndGreet");
    private readonly MainWindow mainWindow;
    private readonly bool ecommonsInitialized;
    private DateTime lastAutoAddressCheckUtc = DateTime.MinValue;

    internal IDalamudPluginInterface PluginInterface { get; }
    internal ICommandManager CommandManager { get; }
    internal IFramework Framework { get; }
    internal IObjectTable ObjectTable { get; }
    internal IClientState ClientState { get; }
    internal IDataManager DataManager { get; }
    internal IPlayerState PlayerState { get; }
    internal ITargetManager TargetManager { get; }
    internal IPluginLog Log { get; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IObjectTable objectTable,
        IClientState clientState,
        IDataManager dataManager,
        IPlayerState playerState,
        ITargetManager targetManager,
        IPluginLog log)
    {
        this.PluginInterface = pluginInterface;
        this.CommandManager = commandManager;
        this.Framework = framework;
        this.ObjectTable = objectTable;
        this.ClientState = clientState;
        this.DataManager = dataManager;
        this.PlayerState = playerState;
        this.TargetManager = targetManager;
        this.Log = log;

        try
        {
            ECommonsMain.Init(this.PluginInterface, this);
            this.ecommonsInitialized = true;
        }
        catch (Exception ex)
        {
            this.Log.Error(ex, "ECommons failed to initialize. Continuing without ECommons runtime helpers.");
            this.ecommonsInitialized = false;
        }

        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (string.IsNullOrWhiteSpace(this.Configuration.ExportDirectory))
        {
            this.Configuration.ExportDirectory = Path.Combine(PluginInterface.ConfigDirectory.FullName, "exports");
        }

        // Always come up closed after a reload so a stale open state never resumes automatically.
        this.Configuration.IsVenueOpen = false;

        var dbPath = Path.Combine(PluginInterface.ConfigDirectory.FullName, "VenueStatusAndGreet.db");
        this.Database = new DatabaseService(dbPath, Log);
        this.Database.Initialize();
        this.Database.EnsureHotbarSlots();

        this.Tracker = new VenueTrackerService(this.Database, ObjectTable, ClientState, Log);
        this.Tracker.SetVenueInfo(this.Configuration.VenueName, this.Configuration.VenueAddress, DateTime.UtcNow);
        this.ApplyTrackingFilters(DateTime.UtcNow);
        this.Tracker.FirstVisitTonightDetected += this.OnFirstVisitTonightDetected;

        this.Greeter = new GreeterService(Log, this.Tracker.IsCurrentlyPresent, this.ExecuteChatCommand);
        this.Greeter.GreetingCompleted += this.OnGreetingCompleted;
        this.SetActivePreset(this.Configuration.ActivePresetId);

        this.Exporter = new ExportService(this.Database, Log);
        this.AddressService = new VenueAddressService(ClientState, PlayerState, DataManager, Log);
        this.TryAutoDetectVenueAddress(force: true);

        this.mainWindow = new MainWindow(this);
        this.windowSystem.AddWindow(this.mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Toggle the Venue Status and Greet window.",
        });

        PluginInterface.UiBuilder.Draw += this.DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleMainUi;
        Framework.Update += this.OnFrameworkUpdate;
        ClientState.TerritoryChanged += this.OnTerritoryChanged;

        this.SaveConfiguration();
    }

    public string Name => "Venue Status and Greet";

    internal Configuration Configuration { get; }

    internal DatabaseService Database { get; }

    internal VenueTrackerService Tracker { get; }

    internal GreeterService Greeter { get; }

    internal ExportService Exporter { get; }

    internal VenueAddressService AddressService { get; }

    internal VenueAddressSnapshot? LastDetectedAddress { get; private set; }

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        ClientState.TerritoryChanged -= this.OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw -= this.DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);

        this.Tracker.FirstVisitTonightDetected -= this.OnFirstVisitTonightDetected;
        this.Greeter.GreetingCompleted -= this.OnGreetingCompleted;

        this.windowSystem.RemoveWindow(this.mainWindow);
        this.mainWindow.Dispose();

        this.Database.Dispose();
        this.SaveConfiguration();
        if (this.ecommonsInitialized)
        {
            ECommonsMain.Dispose();
        }
    }

    internal void SaveConfiguration()
    {
        PluginInterface.SavePluginConfig(this.Configuration);
    }

    internal void ToggleMainUi()
    {
        this.mainWindow.IsOpen = !this.mainWindow.IsOpen;
    }

    internal void SetActivePreset(int? presetId)
    {
        this.Configuration.ActivePresetId = presetId;
        this.SaveConfiguration();

        if (presetId is int id)
        {
            this.Greeter.SetActivePreset(this.Database.GetPresetById(id));
        }
        else
        {
            this.Greeter.SetActivePreset(null);
        }
    }

    internal void SetVenueOpen(bool isOpen, DateTime nowUtc)
    {
        if (isOpen)
        {
            this.StartNewOpening(nowUtc);
            return;
        }

        this.PauseOpening(nowUtc);
    }

    internal void StartNewOpening(DateTime nowUtc)
    {
        this.Greeter.ResetQueue("Venue opened");
        this.Configuration.IsVenueOpen = true;
        this.Tracker.StartVenueSession(nowUtc);
        this.SaveConfiguration();
    }

    internal void PauseOpening(DateTime nowUtc)
    {
        this.Greeter.ResetQueue("Venue paused");
        this.Configuration.IsVenueOpen = false;
        this.Tracker.PauseVenueSession(nowUtc);
        this.SaveConfiguration();
    }

    internal bool ResumeOpening(long sessionId, DateTime nowUtc)
    {
        this.Greeter.ResetQueue("Venue resumed");
        var resumed = this.Tracker.ResumeVenueSession(sessionId, nowUtc);
        this.Configuration.IsVenueOpen = resumed;
        this.SaveConfiguration();
        return resumed;
    }

    internal bool CloseOpening(DateTime nowUtc)
    {
        this.Greeter.ResetQueue("Venue closed");
        var closed = this.Tracker.CloseVenueSession(nowUtc);
        this.Configuration.IsVenueOpen = false;
        this.SaveConfiguration();
        return closed;
    }

    internal bool CloseOpening(long sessionId, DateTime nowUtc)
    {
        this.Greeter.ResetQueue("Venue closed");
        var closed = this.Tracker.CloseVenueSession(sessionId, nowUtc);
        this.Configuration.IsVenueOpen = false;
        this.SaveConfiguration();
        return closed;
    }

    internal void ApplyTrackingFilters(DateTime nowUtc)
    {
        this.Configuration.TrackingPollIntervalSeconds = Math.Clamp(this.Configuration.TrackingPollIntervalSeconds, 5, 3600);
        this.Tracker.SetFilters(
            this.Configuration.LockToOpenTerritory,
            this.Configuration.UseDistanceFilter,
            this.Configuration.VenueRadiusYalms,
            this.Configuration.TrackingPollIntervalSeconds,
            nowUtc);
        this.SaveConfiguration();
    }

    internal bool TryTargetVisitor(VisitorNightSummary visitor)
    {
        if (this.Tracker.TryGetLiveObject(visitor, out var gameObject) && gameObject is not null)
        {
            TargetManager.Target = gameObject;
            this.Log.Information($"Targeted visitor {visitor.Identity.DisplayName} via live object.");
            return true;
        }

        var safeTarget = BuildCommandTargetName(visitor.Identity);
        try
        {
            var command = $"/target \"{safeTarget}\"";
            CommandManager.ProcessCommand(command);
            this.Log.Information($"Target fallback command issued: {command}");
        }
        catch (Exception ex)
        {
            this.Log.Warning(ex, $"Failed to issue target fallback for {safeTarget}.");
        }

        return false;
    }

    internal void ExamineVisitor(VisitorNightSummary visitor)
    {
        this.Log.Information($"Examine requested for {visitor.Identity.DisplayName}.");
        if (this.TryGetLivePlayer(visitor, out var player) && this.TryOpenExamineViaAgent(visitor, player))
        {
            return;
        }

        this.ExecuteVisitorCommandByName(visitor, "/check");
    }

    internal void ShowAdventurePlate(VisitorNightSummary visitor)
    {
        this.Log.Information($"Adventure plate requested for {visitor.Identity.DisplayName}.");
        if (this.TryGetLivePlayer(visitor, out var player) && this.TryOpenCharaCardViaAgent(visitor, player))
        {
            return;
        }

        this.ExecuteVisitorCommandByName(visitor, "/plate");
    }

    internal bool QueueManualGreet(VisitorNightSummary visitor)
    {
        if (this.IsLocalPlayer(visitor.Identity))
        {
            this.Log.Warning("Manual greet request ignored for local player.");
            return false;
        }

        var queued = this.Greeter.QueueGreeting(visitor.Identity);
        this.Log.Information($"Manual greet request for {visitor.Identity.DisplayName}: queued={queued}.");
        return queued;
    }

    private void OnCommand(string command, string args)
    {
        this.ToggleMainUi();
    }

    private void DrawUi()
    {
        this.windowSystem.Draw();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var nowUtc = framework.LastUpdateUTC;
        if ((nowUtc - this.lastAutoAddressCheckUtc).TotalSeconds >= 2)
        {
            this.lastAutoAddressCheckUtc = nowUtc;
            this.TryAutoDetectVenueAddress(force: false);
        }

        this.Tracker.Tick(nowUtc);
        this.Greeter.Tick(nowUtc);
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        this.TryAutoDetectVenueAddress(force: true);
    }

    private void OnFirstVisitTonightDetected(GuestIdentity guest)
    {
        if (this.IsLocalPlayer(guest))
        {
            return;
        }

        if (!this.Configuration.AutoGreetEnabled)
        {
            return;
        }

        _ = this.Greeter.QueueGreeting(guest);
    }

    private void OnGreetingCompleted(GuestIdentity guest)
    {
        this.Database.MarkVisitorGreeted(guest, true, DateTime.UtcNow, this.Tracker.VenueName, this.Tracker.VenueAddress);
    }

    private bool IsLocalPlayer(GuestIdentity guest)
    {
        var localName = this.PlayerState.CharacterName?.Trim() ?? string.Empty;
        var localWorld = this.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText().Trim()
            ?? this.PlayerState.CurrentWorld.ValueNullable?.Name.ExtractText().Trim()
            ?? string.Empty;
        var guestWorld = NormalizeWorldName(guest.HomeWorld);
        if (string.IsNullOrWhiteSpace(localName) || string.IsNullOrWhiteSpace(localWorld))
        {
            return false;
        }

        return string.Equals(localName, guest.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(localWorld, guestWorld, StringComparison.OrdinalIgnoreCase);
    }

    internal bool TryAutoDetectVenueAddress(bool force)
    {
        if (!force && !this.Configuration.AutoDetectVenueAddress)
        {
            return false;
        }

        if (!this.AddressService.TryGetCurrentAddress(out var snapshot))
        {
            return false;
        }

        this.LastDetectedAddress = snapshot;
        if (!snapshot.IsHousingArea)
        {
            return true;
        }

        var detectedAddress = snapshot.ToAddressString();
        if (string.Equals(this.Configuration.VenueAddress, detectedAddress, StringComparison.Ordinal))
        {
            return true;
        }

        this.Configuration.VenueAddress = detectedAddress;
        this.Tracker.SetVenueInfo(this.Configuration.VenueName, this.Configuration.VenueAddress, DateTime.UtcNow);
        this.SaveConfiguration();
        return true;
    }

    private bool TryGetLivePlayer(VisitorNightSummary visitor, out IPlayerCharacter player)
    {
        player = null!;
        if (this.Tracker.TryGetLiveObject(visitor, out var gameObject) && gameObject is IPlayerCharacter livePlayer)
        {
            player = livePlayer;
            return true;
        }

        return false;
    }

    private bool TryOpenExamineViaAgent(VisitorNightSummary visitor, IPlayerCharacter player)
    {
        try
        {
            unsafe
            {
                var agent = AgentInspect.Instance();
                if (agent is null)
                {
                    this.Log.Warning($"AgentInspect unavailable for {visitor.Identity.DisplayName}; falling back to text command.");
                    return false;
                }

                agent->ExamineCharacter(player.EntityId, false);
                this.Log.Information($"Examine opened via AgentInspect for {visitor.Identity.DisplayName} (entityId={player.EntityId}).");
                return true;
            }
        }
        catch (Exception ex)
        {
            this.Log.Warning(ex, $"AgentInspect examine failed for {visitor.Identity.DisplayName}; falling back to text command.");
            return false;
        }
    }

    private bool TryOpenCharaCardViaAgent(VisitorNightSummary visitor, IPlayerCharacter player)
    {
        try
        {
            unsafe
            {
                var agent = AgentCharaCard.Instance();
                if (agent is null)
                {
                    this.Log.Warning($"AgentCharaCard unavailable for {visitor.Identity.DisplayName}; falling back to text command.");
                    return false;
                }

                var gameObject = (CsGameObject*)player.Address;
                if (gameObject is null)
                {
                    this.Log.Warning($"Live player pointer unavailable for {visitor.Identity.DisplayName}; falling back to text command.");
                    return false;
                }

                agent->OpenCharaCard(gameObject);
                this.Log.Information($"Adventure plate opened via AgentCharaCard for {visitor.Identity.DisplayName}.");
                return true;
            }
        }
        catch (Exception ex)
        {
            this.Log.Warning(ex, $"AgentCharaCard open failed for {visitor.Identity.DisplayName}; falling back to text command.");
            return false;
        }
    }

    private void ExecuteVisitorCommandByName(VisitorNightSummary visitor, string command)
    {
        var safeDisplayName = BuildCommandTargetName(visitor.Identity);
        var direct = $"{command} \"{safeDisplayName}\"";

        try
        {
            this.CommandManager.ProcessCommand(direct);
            this.Log.Information($"Issued fallback command: {direct}");
        }
        catch (Exception ex)
        {
            this.Log.Warning(ex, $"Failed fallback command for {visitor.Identity.DisplayName}: {direct}");
        }
    }

    private static string BuildCommandTargetName(GuestIdentity guest)
    {
        var safeName = guest.Name.Replace("\"", "'", StringComparison.Ordinal).Trim();
        var safeWorld = NormalizeWorldName(guest.HomeWorld);

        if (!string.IsNullOrWhiteSpace(safeName) && !string.IsNullOrWhiteSpace(safeWorld))
        {
            return $"{safeName}@{safeWorld}";
        }

        if (!string.IsNullOrWhiteSpace(safeName))
        {
            return safeName;
        }

        return guest.DisplayName.Replace("\"", "'", StringComparison.Ordinal).Trim();
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

    private bool ExecuteChatCommand(string command)
    {
        var text = command.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Preferred path: ECommons helper (matches user expectation and keeps behavior close to typed chat).
        try
        {
            Chat.ExecuteCommand(text);
            return true;
        }
        catch (Exception ex)
        {
            this.Log.Debug(ex, $"ECommons chat execution failed for command: {text}");
        }

        // Fallback path: execute directly through RaptureShellModule.
        try
        {
            unsafe
            {
                var uiModule = UIModule.Instance();
                var shellModule = RaptureShellModule.Instance();
                if (uiModule is null || shellModule is null)
                {
                    this.Log.Warning($"RaptureShellModule unavailable; command not sent: {text}");
                    return false;
                }

                var utf8 = Utf8String.FromString(text);
                if (utf8 is null)
                {
                    this.Log.Warning($"Could not allocate Utf8String for command: {text}");
                    return false;
                }

                try
                {
                    shellModule->ExecuteCommandInner(utf8, uiModule);
                    return true;
                }
                finally
                {
                    utf8->Dtor();
                    IMemorySpace.Free(utf8);
                }
            }
        }
        catch (Exception ex)
        {
            this.Log.Warning(ex, $"RaptureShellModule command execution failed: {text}");
            return false;
        }
    }
}




