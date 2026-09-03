using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using VenueStatusAndGreet.Models;

namespace VenueStatusAndGreet.Services;

public sealed class VenueTrackerService
{
    private static readonly TimeSpan PresenceScanInterval = TimeSpan.FromSeconds(1);

    private readonly DatabaseService database;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private readonly HashSet<string> currentlyPresent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GuestIdentity> identityLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> objectIdLookup = new(StringComparer.OrdinalIgnoreCase);

    private DateTime lastPresenceScanUtc = DateTime.MinValue;
    private DateTime lastStatsPollUtc = DateTime.MinValue;
    private DateTime lastSampleBucketUtc = DateTime.MinValue;
    private bool suppressNextScanNotifications;

    public VenueTrackerService(DatabaseService database, IObjectTable objectTable, IClientState clientState, IPluginLog log)
    {
        this.database = database;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.log = log;
    }

    public event Action<GuestIdentity>? FirstVisitTonightDetected;

    public string VenueName { get; private set; } = "My Venue";

    public string VenueAddress { get; private set; } = string.Empty;

    public bool VenueOpen { get; private set; }

    public bool LockToOpenTerritory { get; private set; } = true;

    public bool UseDistanceFilter { get; private set; } = true;

    public bool UseOutdoorVenueArea { get; private set; }

    public float VenueRadiusYalms { get; private set; } = 35f;

    public Vector3? OutdoorVenueCenter { get; private set; }

    public int TrackingPollIntervalSeconds { get; private set; } = 900;

    public uint? LockedTerritoryId { get; private set; }

    public bool TrackingTerritoryMatches => this.LockedTerritoryId is null || this.clientState.TerritoryType == this.LockedTerritoryId.Value;

    public void SetVenueInfo(string venueName, string venueAddress, DateTime nowUtc)
    {
        this.VenueName = venueName.Trim();
        this.VenueAddress = venueAddress.Trim();
        this.database.SetVenueInfo(this.VenueName, this.VenueAddress, nowUtc);
    }

    public void SetFilters(bool lockToOpenTerritory, bool useDistanceFilter, bool useOutdoorVenueArea, float venueRadiusYalms, int trackingPollIntervalSeconds, DateTime nowUtc)
    {
        var switchingToOutdoorArea = useOutdoorVenueArea && !this.UseOutdoorVenueArea;
        this.LockToOpenTerritory = lockToOpenTerritory;
        this.UseDistanceFilter = useDistanceFilter;
        this.UseOutdoorVenueArea = useOutdoorVenueArea;
        this.VenueRadiusYalms = Math.Clamp(venueRadiusYalms, 5f, 150f);
        this.TrackingPollIntervalSeconds = Math.Clamp(trackingPollIntervalSeconds, 5, 3600);

        if (!this.UseOutdoorVenueArea)
        {
            this.OutdoorVenueCenter = null;
        }
        else if (switchingToOutdoorArea && this.VenueOpen)
        {
            this.CaptureOutdoorVenueCenter();
        }

        if (!this.LockToOpenTerritory)
        {
            this.LockedTerritoryId = null;
            return;
        }

        if (this.VenueOpen)
        {
            this.LockedTerritoryId = this.clientState.TerritoryType;
            this.MarkAllAsAbsent(nowUtc);
        }
    }

    public void StartVenueSession(DateTime nowUtc)
    {
        this.database.StartVenueSession(this.VenueName, this.VenueAddress, nowUtc);
        this.VenueOpen = true;
        this.LockedTerritoryId = this.LockToOpenTerritory ? this.clientState.TerritoryType : null;
        this.CaptureOutdoorVenueCenter();
        this.PrepareForOpen();
    }

    public bool ResumeVenueSession(long sessionId, DateTime nowUtc)
    {
        var resumed = this.database.ResumeVenueSession(sessionId, this.VenueName, this.VenueAddress, nowUtc);
        if (!resumed)
        {
            return false;
        }

        this.VenueOpen = true;
        this.LockedTerritoryId = this.LockToOpenTerritory ? this.clientState.TerritoryType : null;
        this.CaptureOutdoorVenueCenter();
        this.PrepareForOpen();
        return true;
    }

    public void PauseVenueSession(DateTime nowUtc)
    {
        _ = this.database.PauseVenueSession(this.VenueName, this.VenueAddress, nowUtc);
        this.VenueOpen = false;
        this.LockedTerritoryId = null;
        this.ApplyClosedState();
    }

    public bool CloseVenueSession(DateTime nowUtc)
    {
        var closed = this.database.CloseVenueSession(this.VenueName, this.VenueAddress, nowUtc);
        this.VenueOpen = false;
        this.LockedTerritoryId = null;
        this.ApplyClosedState();
        return closed;
    }

    public bool CloseVenueSession(long sessionId, DateTime nowUtc)
    {
        var closed = this.database.CloseVenueSession(sessionId, this.VenueName, this.VenueAddress, nowUtc);
        this.VenueOpen = false;
        this.LockedTerritoryId = null;
        this.ApplyClosedState();
        return closed;
    }

    public void Tick(DateTime nowUtc)
    {
        if (!this.VenueOpen)
        {
            return;
        }

        if (!this.TrackingTerritoryMatches)
        {
            if (this.currentlyPresent.Count > 0)
            {
                this.MarkAllAsAbsent(nowUtc);
            }

            return;
        }

        if ((nowUtc - this.lastPresenceScanUtc) < PresenceScanInterval)
        {
            return;
        }

        this.lastPresenceScanUtc = nowUtc;
        var suppressNotifications = this.suppressNextScanNotifications;
        this.suppressNextScanNotifications = false;

        this.ScanPlayerObjects(nowUtc, suppressNotifications);

        if ((nowUtc - this.lastStatsPollUtc).TotalSeconds < this.TrackingPollIntervalSeconds)
        {
            return;
        }

        this.lastStatsPollUtc = nowUtc;
        this.SampleGuestCount(nowUtc);
    }

    public bool TryGetLiveObject(VisitorNightSummary visitor, out IGameObject? gameObject)
    {
        gameObject = null;
        if (!visitor.IsPresent)
        {
            return false;
        }

        if (!this.objectIdLookup.TryGetValue(visitor.Identity.Key, out var objectId))
        {
            objectId = visitor.LastObjectId;
        }

        if (objectId == 0)
        {
            return false;
        }

        gameObject = this.objectTable.FirstOrDefault(x => x.GameObjectId == objectId);
        return gameObject is not null;
    }

    public bool IsCurrentlyPresent(GuestIdentity guest)
    {
        return this.currentlyPresent.Contains(guest.Key);
    }

    private void PrepareForOpen()
    {
        this.lastPresenceScanUtc = DateTime.MinValue;
        this.lastStatsPollUtc = DateTime.MinValue;
        this.lastSampleBucketUtc = DateTime.MinValue;
        this.suppressNextScanNotifications = true;
    }

    private void CaptureOutdoorVenueCenter()
    {
        this.OutdoorVenueCenter = this.UseOutdoorVenueArea
            ? this.objectTable.LocalPlayer?.Position
            : null;

        if (this.UseOutdoorVenueArea && this.OutdoorVenueCenter is null)
        {
            this.log.Warning("Outdoor venue area is enabled, but no local player position is available to set the radius center.");
        }
    }

    private void ApplyClosedState()
    {
        this.currentlyPresent.Clear();
        this.identityLookup.Clear();
        this.objectIdLookup.Clear();
        this.OutdoorVenueCenter = null;
        this.suppressNextScanNotifications = false;
        this.lastPresenceScanUtc = DateTime.MinValue;
    }

    private void ScanPlayerObjects(DateTime nowUtc, bool suppressFirstVisitNotifications)
    {
        var localPlayer = this.objectTable.LocalPlayer;
        var presentNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeObjectIds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        foreach (var objectHandle in this.objectTable)
        {
            if (objectHandle is not IPlayerCharacter player)
            {
                continue;
            }

            var radiusCenter = this.UseOutdoorVenueArea
                ? this.OutdoorVenueCenter
                : this.UseDistanceFilter ? localPlayer?.Position : null;
            if (this.UseOutdoorVenueArea && radiusCenter is null)
            {
                continue;
            }

            if (radiusCenter is Vector3 center)
            {
                var distance = Vector3.Distance(center, player.Position);
                if (distance > this.VenueRadiusYalms)
                {
                    continue;
                }
            }

            var name = player.Name.TextValue?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var guest = new GuestIdentity(name, this.GetWorldName(player));
            var key = guest.Key;
            presentNow.Add(key);
            this.identityLookup[key] = guest;
            activeObjectIds[key] = player.GameObjectId;

            if (this.currentlyPresent.Contains(key))
            {
                continue;
            }

            var change = this.database.MarkVisitorPresent(guest, nowUtc, player.GameObjectId, this.VenueName, this.VenueAddress);
            this.currentlyPresent.Add(key);

            if (change.IsFirstVisitTonight && !suppressFirstVisitNotifications)
            {
                this.FirstVisitTonightDetected?.Invoke(guest);
            }
        }

        var departedKeys = this.currentlyPresent.Where(key => !presentNow.Contains(key)).ToList();
        foreach (var departedKey in departedKeys)
        {
            if (this.identityLookup.TryGetValue(departedKey, out var guest))
            {
                this.database.MarkVisitorAbsent(guest, nowUtc, this.VenueName, this.VenueAddress);
            }

            _ = this.currentlyPresent.Remove(departedKey);
            _ = this.objectIdLookup.Remove(departedKey);
        }

        foreach (var item in activeObjectIds)
        {
            this.objectIdLookup[item.Key] = item.Value;
        }
    }

    private void MarkAllAsAbsent(DateTime nowUtc)
    {
        var keys = this.currentlyPresent.ToList();
        foreach (var key in keys)
        {
            if (this.identityLookup.TryGetValue(key, out var guest))
            {
                this.database.MarkVisitorAbsent(guest, nowUtc, this.VenueName, this.VenueAddress);
            }
        }

        this.currentlyPresent.Clear();
        this.objectIdLookup.Clear();
    }

    private void SampleGuestCount(DateTime nowUtc)
    {
        var bucket = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, (nowUtc.Minute / 5) * 5, 0, DateTimeKind.Utc);
        if (bucket == this.lastSampleBucketUtc)
        {
            return;
        }

        this.lastSampleBucketUtc = bucket;
        var guestCount = this.currentlyPresent.Count;
        this.database.RecordGuestSample(guestCount, nowUtc, this.VenueName, this.VenueAddress);
    }

    private string GetWorldName(IPlayerCharacter player)
    {
        var homeWorld = player.HomeWorld.ValueNullable?.Name.ExtractText().Trim();
        if (!string.IsNullOrWhiteSpace(homeWorld))
        {
            return homeWorld;
        }

        var currentWorld = player.CurrentWorld.ValueNullable?.Name.ExtractText().Trim();
        if (!string.IsNullOrWhiteSpace(currentWorld))
        {
            return currentWorld;
        }

        return "Unknown";
    }
}
