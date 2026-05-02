using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using VenueStatusAndGreet.Models;

namespace VenueStatusAndGreet.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string venueNameBuffer;
    private string venueAddressBuffer;
    private string exportDirectoryBuffer;

    private string presetNameBuffer = string.Empty;
    private string presetLine1Buffer = string.Empty;
    private string presetLine2Buffer = string.Empty;
    private string presetLine3Buffer = string.Empty;
    private string presetLine4Buffer = string.Empty;

    private int? selectedPresetId;
    private List<GreetPreset> cachedPresets = [];
    private Dictionary<int, int?> hotbarAssignments = new();
    private DateTime lastPresetRefresh = DateTime.MinValue;
    private DateTime lastSessionRefresh = DateTime.MinValue;
    private List<VenueSessionEntry> cachedSessions = [];
    private long? selectedSessionId;
    private string exportStatus = string.Empty;

    public MainWindow(Plugin plugin)
        : base("Venue Status and Greet###VenueStatusAndGreet")
    {
        this.plugin = plugin;
        this.Size = new Vector2(1200, 700);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(920, 580),
            MaximumSize = new Vector2(2800, 2000),
        };

        this.venueNameBuffer = this.plugin.Configuration.VenueName;
        this.venueAddressBuffer = this.plugin.Configuration.VenueAddress;
        this.exportDirectoryBuffer = this.plugin.Configuration.ExportDirectory;
        this.selectedPresetId = this.plugin.Configuration.ActivePresetId;
        this.RefreshPresetCache();
        this.RefreshSessionCache();
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        this.RefreshPresetCacheIfStale();
        this.RefreshSessionCacheIfStale();

        if (!ImGui.BeginTabBar("##vsg_tab_bar"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Venue Status"))
        {
            this.DrawVenueStatusTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Greet"))
        {
            this.DrawGreetTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawVenueStatusTab()
    {
        var childRegion = ImGui.GetContentRegionAvail();
        var leftWidth = childRegion.X * 0.44f;
        var rightWidth = childRegion.X - leftWidth - 8f;

        this.DrawStatusLeftPane(leftWidth);
        ImGui.SameLine();
        this.DrawStatusRightPane(rightWidth);
    }

    private void DrawStatusLeftPane(float width)
    {
        if (!ImGui.BeginChild("##vsg_status_left", new Vector2(width, -1f), true))
        {
            ImGui.EndChild();
            return;
        }

        if (this.plugin.Configuration.AutoDetectVenueAddress &&
            !string.Equals(this.venueAddressBuffer, this.plugin.Configuration.VenueAddress, StringComparison.Ordinal))
        {
            this.venueAddressBuffer = this.plugin.Configuration.VenueAddress;
        }

        ImGui.TextUnformatted("Venue Details");
        ImGui.Separator();
        ImGui.InputText("Venue Name", ref this.venueNameBuffer, 100);
        ImGui.InputText("In-Game Address", ref this.venueAddressBuffer, 180);
        var autoDetect = this.plugin.Configuration.AutoDetectVenueAddress;
        if (ImGui.Checkbox("Auto-Detect Address In-Game", ref autoDetect))
        {
            this.plugin.Configuration.AutoDetectVenueAddress = autoDetect;
            _ = this.plugin.TryAutoDetectVenueAddress(force: true);
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        if (ImGui.Button("Detect Now"))
        {
            _ = this.plugin.TryAutoDetectVenueAddress(force: true);
            this.venueAddressBuffer = this.plugin.Configuration.VenueAddress;
        }

        var detected = this.plugin.LastDetectedAddress;
        if (detected is not null)
        {
            ImGui.TextUnformatted($"DC: {detected.DataCenter}");
            ImGui.TextUnformatted($"Server: {detected.Server}");
            ImGui.TextUnformatted($"District: {detected.District}");
            ImGui.TextUnformatted($"Ward: {(detected.Ward?.ToString() ?? "-")}  Plot: {(detected.Plot?.ToString() ?? "-")}");
        }

        if (ImGui.Button("Apply Venue Details"))
        {
            this.plugin.Configuration.VenueName = this.venueNameBuffer.Trim();
            this.plugin.Configuration.VenueAddress = this.venueAddressBuffer.Trim();
            this.plugin.Tracker.SetVenueInfo(this.plugin.Configuration.VenueName, this.plugin.Configuration.VenueAddress, DateTime.UtcNow);
            this.plugin.SaveConfiguration();
        }

        var activeSessionId = this.ResolveSelectedSessionId();
        var selectedSession = this.cachedSessions.FirstOrDefault(x => x.SessionId == activeSessionId);
        var selectedSessionIsResumable = selectedSession?.IsResumable == true;

        var isVenueOpen = this.plugin.Configuration.IsVenueOpen;
        if (ImGui.Checkbox("Venue Open", ref isVenueOpen))
        {
            if (isVenueOpen)
            {
                if (selectedSessionIsResumable && activeSessionId is long resumeId)
                {
                    _ = this.plugin.ResumeOpening(resumeId, DateTime.UtcNow);
                }
                else
                {
                    this.plugin.StartNewOpening(DateTime.UtcNow);
                }
            }
            else
            {
                this.plugin.PauseOpening(DateTime.UtcNow);
            }

            this.RefreshSessionCache();
            activeSessionId = this.ResolveSelectedSessionId();
            selectedSession = this.cachedSessions.FirstOrDefault(x => x.SessionId == activeSessionId);
            selectedSessionIsResumable = selectedSession?.IsResumable == true;
        }

        ImGui.SameLine();
        var statusColor = isVenueOpen
            ? new Vector4(0.3f, 0.9f, 0.3f, 1f)
            : selectedSessionIsResumable
                ? new Vector4(1f, 0.8f, 0.25f, 1f)
                : new Vector4(0.95f, 0.25f, 0.25f, 1f);
        var statusText = isVenueOpen ? "OPEN" : selectedSessionIsResumable ? "PAUSED / RESUMABLE" : "CLOSED";
        ImGui.TextColored(statusColor, statusText);

        if (!isVenueOpen)
        {
            if (ImGui.Button("Start New Opening"))
            {
                this.plugin.StartNewOpening(DateTime.UtcNow);
                this.RefreshSessionCache();
                activeSessionId = this.ResolveSelectedSessionId();
                selectedSession = this.cachedSessions.FirstOrDefault(x => x.SessionId == activeSessionId);
                selectedSessionIsResumable = selectedSession?.IsResumable == true;
            }

            ImGui.SameLine();
            if (!selectedSessionIsResumable)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("Resume Selected") && activeSessionId is long resumeSelectedId)
            {
                _ = this.plugin.ResumeOpening(resumeSelectedId, DateTime.UtcNow);
                this.RefreshSessionCache();
                activeSessionId = this.ResolveSelectedSessionId();
                selectedSession = this.cachedSessions.FirstOrDefault(x => x.SessionId == activeSessionId);
                selectedSessionIsResumable = selectedSession?.IsResumable == true;
            }

            if (!selectedSessionIsResumable)
            {
                ImGui.EndDisabled();
            }
        }
        else
        {
            if (ImGui.Button("Pause Opening"))
            {
                this.plugin.PauseOpening(DateTime.UtcNow);
                this.RefreshSessionCache();
                activeSessionId = this.ResolveSelectedSessionId();
                selectedSession = this.cachedSessions.FirstOrDefault(x => x.SessionId == activeSessionId);
                selectedSessionIsResumable = selectedSession?.IsResumable == true;
            }
        }

        ImGui.SameLine();
        var canCloseOpening = isVenueOpen || selectedSessionIsResumable;
        if (!canCloseOpening)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Close Opening"))
        {
            if (isVenueOpen)
            {
                _ = this.plugin.CloseOpening(DateTime.UtcNow);
            }
            else if (activeSessionId is long closeSessionId)
            {
                _ = this.plugin.CloseOpening(closeSessionId, DateTime.UtcNow);
            }

            this.RefreshSessionCache();
            activeSessionId = this.ResolveSelectedSessionId();
            selectedSession = this.cachedSessions.FirstOrDefault(x => x.SessionId == activeSessionId);
            selectedSessionIsResumable = selectedSession?.IsResumable == true;
        }

        if (!canCloseOpening)
        {
            ImGui.EndDisabled();
        }

        var selectedSessionLabel = this.cachedSessions.FirstOrDefault(x => x.SessionId == activeSessionId)?.Label ?? "(No openings yet)";
        if (ImGui.BeginCombo("Openings", selectedSessionLabel))
        {
            foreach (var session in this.cachedSessions)
            {
                var isSelected = this.selectedSessionId == session.SessionId;
                if (ImGui.Selectable(session.Label, isSelected))
                {
                    this.selectedSessionId = session.SessionId;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (activeSessionId is long deleteSessionId && ImGui.Button("Delete Opening"))
        {
            if (this.plugin.Database.DeleteSession(deleteSessionId))
            {
                this.selectedSessionId = null;
                this.RefreshSessionCache();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Tracking Filters");
        ImGui.Separator();
        var lockToTerritory = this.plugin.Configuration.LockToOpenTerritory;
        if (ImGui.Checkbox("Lock To Territory Opened In", ref lockToTerritory))
        {
            this.plugin.Configuration.LockToOpenTerritory = lockToTerritory;
            this.plugin.ApplyTrackingFilters(DateTime.UtcNow);
        }

        var useDistanceFilter = this.plugin.Configuration.UseDistanceFilter;
        if (ImGui.Checkbox("Use Venue Radius", ref useDistanceFilter))
        {
            this.plugin.Configuration.UseDistanceFilter = useDistanceFilter;
            this.plugin.ApplyTrackingFilters(DateTime.UtcNow);
        }

        var radius = this.plugin.Configuration.VenueRadiusYalms;
        if (ImGui.SliderFloat("Venue Radius (yalms)", ref radius, 5f, 150f))
        {
            this.plugin.Configuration.VenueRadiusYalms = radius;
            this.plugin.ApplyTrackingFilters(DateTime.UtcNow);
        }

        var pollSeconds = this.plugin.Configuration.TrackingPollIntervalSeconds;
        if (ImGui.InputInt("Stats Poll Interval (seconds)", ref pollSeconds))
        {
            this.plugin.Configuration.TrackingPollIntervalSeconds = Math.Clamp(pollSeconds, 5, 3600);
            this.plugin.ApplyTrackingFilters(DateTime.UtcNow);
        }

        ImGui.TextDisabled($"Greeting detection runs every second. This timer only controls statistics sampling. Current: {this.plugin.Configuration.TrackingPollIntervalSeconds / 60.0:F1} minutes.");

        var territoryMatch = this.plugin.Tracker.TrackingTerritoryMatches;
        ImGui.TextUnformatted($"Locked Territory: {this.plugin.Tracker.LockedTerritoryId?.ToString() ?? "None"}");
        ImGui.TextColored(
            territoryMatch ? new Vector4(0.3f, 0.9f, 0.3f, 1f) : new Vector4(1f, 0.5f, 0.2f, 1f),
            territoryMatch ? "Tracking Territory: MATCH" : "Tracking Territory: MISMATCH");

        ImGui.Spacing();
        ImGui.TextUnformatted("Tonight Summary");
        ImGui.Separator();
        var tonight = activeSessionId is long summarySessionId
            ? this.plugin.Database.GetSummaryForSession(summarySessionId)
            : new NightSummary { NightDate = DateOnly.FromDateTime(DateTime.Now) };
        ImGui.TextUnformatted($"Date: {tonight.NightDate:yyyy-MM-dd}");
        ImGui.TextUnformatted($"Current Guests: {tonight.CurrentGuests}");
        ImGui.TextUnformatted($"Max Guests: {tonight.MaxGuests}");
        ImGui.TextUnformatted($"Min Guests: {tonight.MinGuests}");
        ImGui.TextUnformatted($"Unique Guests: {tonight.UniqueGuests}");
        ImGui.TextUnformatted($"Total Visits: {tonight.TotalVisits}");
        ImGui.TextUnformatted($"Average Time / Guest: {FormatDuration(tonight.AverageGuestTime)}");

        ImGui.Spacing();
        var days = Math.Clamp(this.plugin.Configuration.StatsRangeDays, 1, 30);
        if (ImGui.SliderInt("Comparison Days", ref days, 1, 30))
        {
            this.plugin.Configuration.StatsRangeDays = days;
            this.plugin.SaveConfiguration();
        }

        var toDate = DateOnly.FromDateTime(DateTime.Now);
        var fromDate = toDate.AddDays(-(days - 1));
        var daily = this.plugin.Database.GetDailyStats(fromDate, toDate);

        ImGui.TextUnformatted("Max/Min Guest Comparison");
        ImGui.Separator();
        var maxSeries = daily.Select(x => (float)x.MaxGuests).ToArray();
        var minSeries = daily.Select(x => (float)x.MinGuests).ToArray();
        this.DrawLineChart(
            "##daily_line_chart",
            maxSeries,
            new Vector4(0.20f, 0.85f, 0.95f, 1f),
            minSeries,
            new Vector4(0.95f, 0.55f, 0.20f, 1f),
            170f,
            "Max Guests / Min Guests");

        if (ImGui.BeginTable("##daily_summary_table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Date");
            ImGui.TableSetupColumn("Max");
            ImGui.TableSetupColumn("Min");
            ImGui.TableSetupColumn("Unique");
            ImGui.TableHeadersRow();
            foreach (var row in daily)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(row.NightDate.ToString("MM-dd"));
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.MaxGuests.ToString());
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(row.MinGuests.ToString());
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(row.UniqueGuests.ToString());
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Export");
        ImGui.Separator();
        ImGui.InputText("Export Folder", ref this.exportDirectoryBuffer, 260);
        if (ImGui.Button("Export Range to Excel"))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(this.exportDirectoryBuffer))
                {
                    this.exportDirectoryBuffer = Path.Combine(this.plugin.PluginInterface.ConfigDirectory.FullName, "exports");
                }

                this.plugin.Configuration.ExportDirectory = this.exportDirectoryBuffer.Trim();
                this.plugin.SaveConfiguration();

                var path = this.plugin.Exporter.ExportRangeToExcel(fromDate, toDate, this.exportDirectoryBuffer.Trim());
                this.exportStatus = $"Exported: {path}";
            }
            catch (Exception ex)
            {
                this.exportStatus = $"Export failed: {ex.Message}";
            }
        }

        if (!string.IsNullOrWhiteSpace(this.exportStatus))
        {
            ImGui.TextWrapped(this.exportStatus);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("5-Minute Guest Samples (Tonight)");
        ImGui.Separator();
        var samples = activeSessionId is long sampleSessionId
            ? this.plugin.Database.GetSamplesForSession(sampleSessionId, 72)
            : [];
        var sampleSeries = samples.Select(x => (float)x.GuestCount).ToArray();
        this.DrawLineChart(
            "##five_minute_chart",
            sampleSeries,
            new Vector4(0.35f, 1.0f, 0.35f, 1f),
            [],
            new Vector4(0f, 0f, 0f, 0f),
            150f,
            "Guests Per 5 Minutes");

        if (ImGui.BeginTable("##sample_table", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Time");
            ImGui.TableSetupColumn("Guests");
            ImGui.TableHeadersRow();

            foreach (var sample in samples.TakeLast(20))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(sample.SampleTimeLocal.ToString("HH:mm"));
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(sample.GuestCount.ToString());
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private void DrawStatusRightPane(float width)
    {
        if (!ImGui.BeginChild("##vsg_status_right", new Vector2(width, -1f), true))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.TextUnformatted("Tonight Visitors");
        ImGui.Separator();
        var activeSessionId = this.ResolveSelectedSessionId();
        var visitors = activeSessionId is long sessionId
            ? this.plugin.Database.GetVisitorsForSession(sessionId)
            : [];
        if (!ImGui.BeginTable(
                "##vsg_visitors_table",
                4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, -1f)))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Home World");
        ImGui.TableSetupColumn("Visits");
        ImGui.TableSetupColumn("Total Time");
        ImGui.TableHeadersRow();

        foreach (var visitor in visitors)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            var color = visitor.Greeted
                ? new Vector4(0.35f, 1.0f, 0.35f, 1.0f)
                : visitor.IsPresent
                    ? new Vector4(1.0f, 0.3f, 0.3f, 1.0f)
                    : new Vector4(1f, 1f, 1f, 1f);
            var displayName = visitor.IsPresent ? visitor.CharacterName : $"({visitor.CharacterName})";
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            var namePosition = ImGui.GetCursorScreenPos();
            ImGui.TextUnformatted(displayName);
            if (visitor.IsPresent)
            {
                ImGui.GetWindowDrawList().AddText(namePosition + new Vector2(0.8f, 0f), ImGui.GetColorU32(color), displayName);
            }
            ImGui.PopStyleColor();

            if (ImGui.BeginPopupContextItem($"##visitor_ctx_{visitor.Identity.Key}"))
            {
                if (ImGui.MenuItem("Target"))
                {
                    _ = this.plugin.TryTargetVisitor(visitor);
                }

                if (ImGui.MenuItem("Examine"))
                {
                    this.plugin.ExamineVisitor(visitor);
                }

                if (ImGui.MenuItem("Adventure Plate"))
                {
                    this.plugin.ShowAdventurePlate(visitor);
                }

                if (ImGui.MenuItem("Send Greet Now"))
                {
                    _ = this.plugin.QueueManualGreet(visitor);
                }

                if (ImGui.MenuItem(visitor.Greeted ? "Mark Not Greeted" : "Mark Greeted"))
                {
                    this.plugin.Database.MarkVisitorGreeted(
                        visitor.Identity,
                        !visitor.Greeted,
                        DateTime.UtcNow,
                        this.plugin.Tracker.VenueName,
                        this.plugin.Tracker.VenueAddress);
                }

                ImGui.EndPopup();
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(visitor.HomeWorld);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(visitor.Visits.ToString());

            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(FormatDuration(visitor.TotalTime));
        }

        ImGui.EndTable();
        ImGui.EndChild();
    }

    private void DrawGreetTab()
    {
        var autoGreet = this.plugin.Configuration.AutoGreetEnabled;
        if (ImGui.Checkbox("Auto Greet First Visit Tonight", ref autoGreet))
        {
            this.plugin.Configuration.AutoGreetEnabled = autoGreet;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"Active Preset: {this.plugin.Greeter.ActivePresetName}");
        ImGui.TextUnformatted($"Pending Greeting Queue: {this.plugin.Greeter.PendingGreetingCount}");
        var queuedGuests = this.plugin.Greeter.GetQueueSnapshot();
        if (queuedGuests.Count > 0)
        {
            ImGui.TextUnformatted("New Entry Queue:");
            foreach (var queuedGuest in queuedGuests)
            {
                ImGui.BulletText(queuedGuest.DisplayName);
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Hot Buttons");
        ImGui.Separator();
        for (var slot = 1; slot <= 5; slot++)
        {
            var assignedPresetId = this.hotbarAssignments.TryGetValue(slot, out var value) ? value : null;
            var assignedPresetName = assignedPresetId is int id
                ? this.cachedPresets.FirstOrDefault(x => x.Id == id)?.Name ?? "(Missing)"
                : "(Empty)";

            if (ImGui.Button($"[{slot}] {assignedPresetName}"))
            {
                if (assignedPresetId is int hotbarPresetId)
                {
                    this.selectedPresetId = hotbarPresetId;
                    this.plugin.SetActivePreset(hotbarPresetId);
                }
            }

            ImGui.SameLine();
            if (ImGui.BeginCombo($"Assign##slot_{slot}", assignedPresetName))
            {
                if (ImGui.Selectable("(Empty)", assignedPresetId is null))
                {
                    this.hotbarAssignments[slot] = null;
                    this.plugin.Database.SetHotbarAssignment(slot, null);
                }

                foreach (var preset in this.cachedPresets)
                {
                    var isSelected = assignedPresetId == preset.Id;
                    if (ImGui.Selectable(preset.Name, isSelected))
                    {
                        this.hotbarAssignments[slot] = preset.Id;
                        this.plugin.Database.SetHotbarAssignment(slot, preset.Id);
                    }
                }

                ImGui.EndCombo();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Preset Management");
        ImGui.Separator();
        var selectedPresetName = this.cachedPresets.FirstOrDefault(x => x.Id == this.selectedPresetId)?.Name ?? "(None)";
        if (ImGui.BeginCombo("Saved Presets", selectedPresetName))
        {
            foreach (var preset in this.cachedPresets)
            {
                var isSelected = this.selectedPresetId == preset.Id;
                if (ImGui.Selectable(preset.Name, isSelected))
                {
                    this.selectedPresetId = preset.Id;
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("Load Selected Preset"))
        {
            this.LoadSelectedPresetIntoEditor();
        }

        ImGui.SameLine();
        if (ImGui.Button("Set Selected as Active") && this.selectedPresetId is int activePresetId)
        {
            this.plugin.SetActivePreset(activePresetId);
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete Selected") && this.selectedPresetId is int deleteId)
        {
            this.plugin.Database.DeletePreset(deleteId);
            if (this.plugin.Configuration.ActivePresetId == deleteId)
            {
                this.plugin.SetActivePreset(null);
            }

            this.selectedPresetId = null;
            this.RefreshPresetCache();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Editor");
        ImGui.Separator();
        ImGui.InputText("Preset Name", ref this.presetNameBuffer, 64);
        ImGui.InputText("Line 1", ref this.presetLine1Buffer, 220);
        ImGui.InputText("Line 2", ref this.presetLine2Buffer, 220);
        ImGui.InputText("Line 3", ref this.presetLine3Buffer, 220);
        ImGui.InputText("Line 4 / Emote Command", ref this.presetLine4Buffer, 220);
        ImGui.TextDisabled("Lines 1-3 are tells. Line 4 runs as a raw chat command after the tell lines, with the same 2-second spacing.");

        if (ImGui.Button("Save / Update Preset"))
        {
            if (!string.IsNullOrWhiteSpace(this.presetNameBuffer))
            {
                var id = this.plugin.Database.SavePreset(
                    this.presetNameBuffer,
                    this.presetLine1Buffer,
                    this.presetLine2Buffer,
                    this.presetLine3Buffer,
                    this.presetLine4Buffer);
                this.selectedPresetId = id;
                this.RefreshPresetCache();
            }
        }
    }

    private void LoadSelectedPresetIntoEditor()
    {
        if (this.selectedPresetId is not int presetId)
        {
            return;
        }

        var preset = this.cachedPresets.FirstOrDefault(x => x.Id == presetId);
        if (preset is null)
        {
            return;
        }

        this.presetNameBuffer = preset.Name;
        this.presetLine1Buffer = preset.Line1;
        this.presetLine2Buffer = preset.Line2;
        this.presetLine3Buffer = preset.Line3;
        this.presetLine4Buffer = preset.Line4;
    }

    private void RefreshPresetCacheIfStale()
    {
        if ((DateTime.UtcNow - this.lastPresetRefresh).TotalSeconds < 2)
        {
            return;
        }

        this.RefreshPresetCache();
    }

    private void RefreshPresetCache()
    {
        this.cachedPresets = this.plugin.Database.GetGreetPresets();
        this.hotbarAssignments = this.plugin.Database.GetHotbarAssignments();
        this.lastPresetRefresh = DateTime.UtcNow;
    }

    private void RefreshSessionCacheIfStale()
    {
        if ((DateTime.UtcNow - this.lastSessionRefresh).TotalSeconds < 2)
        {
            return;
        }

        this.RefreshSessionCache();
    }

    private void RefreshSessionCache()
    {
        this.cachedSessions = this.plugin.Database.GetRecentSessions(120);
        var currentSessionId = this.plugin.Database.GetCurrentSessionId();
        if (this.selectedSessionId is long selected && this.cachedSessions.Any(x => x.SessionId == selected))
        {
            // Preserve manual selection while browsing history.
        }
        else if (currentSessionId is long current && this.cachedSessions.Any(x => x.SessionId == current))
        {
            this.selectedSessionId = current;
        }
        else if (this.cachedSessions.Count > 0)
        {
            this.selectedSessionId = this.cachedSessions[0].SessionId;
        }

        this.lastSessionRefresh = DateTime.UtcNow;
    }

    private long? ResolveSelectedSessionId()
    {
        if (this.selectedSessionId is long selected && this.cachedSessions.Any(x => x.SessionId == selected))
        {
            return selected;
        }

        var current = this.plugin.Database.GetCurrentSessionId();
        if (current is long currentId)
        {
            this.selectedSessionId = currentId;
            return currentId;
        }

        if (this.cachedSessions.Count > 0)
        {
            this.selectedSessionId = this.cachedSessions[0].SessionId;
            return this.selectedSessionId;
        }

        return null;
    }

    private void DrawLineChart(
        string id,
        IReadOnlyList<float> primaryValues,
        Vector4 primaryColor,
        IReadOnlyList<float> secondaryValues,
        Vector4 secondaryColor,
        float height,
        string legend)
    {
        var size = new Vector2(Math.Max(120f, ImGui.GetContentRegionAvail().X), height);
        ImGui.InvisibleButton(id, size);

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.10f, 0.10f, 0.12f, 0.55f)), 4f);
        drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.15f)), 4f);
        drawList.AddText(new Vector2(min.X + 8f, min.Y + 6f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.85f)), legend);

        var combined = primaryValues.Concat(secondaryValues).ToList();
        if (combined.Count == 0)
        {
            drawList.AddText(new Vector2(min.X + 8f, min.Y + 28f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.65f)), "No data yet");
            return;
        }

        var minValue = Math.Min(0f, combined.Min());
        var maxValue = Math.Max(1f, combined.Max());
        this.DrawSeries(drawList, min, max, primaryValues, minValue, maxValue, ImGui.GetColorU32(primaryColor), 2.2f);
        if (secondaryValues.Count > 0)
        {
            this.DrawSeries(drawList, min, max, secondaryValues, minValue, maxValue, ImGui.GetColorU32(secondaryColor), 2f);
        }
    }

    private void DrawSeries(
        ImDrawListPtr drawList,
        Vector2 graphMin,
        Vector2 graphMax,
        IReadOnlyList<float> values,
        float minValue,
        float maxValue,
        uint color,
        float thickness)
    {
        if (values.Count == 0)
        {
            return;
        }

        var left = graphMin.X + 8f;
        var right = graphMax.X - 8f;
        var top = graphMin.Y + 24f;
        var bottom = graphMax.Y - 10f;
        var width = Math.Max(1f, right - left);
        var height = Math.Max(1f, bottom - top);
        var span = Math.Max(1f, maxValue - minValue);

        Vector2? prev = null;
        for (var i = 0; i < values.Count; i++)
        {
            var x = left + ((values.Count == 1 ? 0f : i / (float)(values.Count - 1)) * width);
            var normalized = (values[i] - minValue) / span;
            var y = bottom - (normalized * height);
            var point = new Vector2(x, y);
            if (prev is Vector2 p)
            {
                drawList.AddLine(p, point, color, thickness);
            }

            drawList.AddCircleFilled(point, 2f, color);
            prev = point;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
        }

        return $"{duration.Minutes}m {duration.Seconds:D2}s";
    }
}





