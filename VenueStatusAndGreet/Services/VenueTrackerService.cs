using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using VenueStatusAndGreet.Models;

namespace VenueStatusAndGreet.Services;

public sealed class VenueTrackerService
{
    private readonly DatabaseService database;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private readonly HashSet<string> currentlyPresent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GuestIdentity> identityLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> objectIdLookup = new(StringComparer.OrdinalIgnoreCase);

    private DateTime lastScanUtc = DateTime.MinValue;
    private DateTime lastSampleBucketUtc = DateTime.MinValue;

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

    public float VenueRadiusYalms { get; private set; } = 35f;

    public uint? LockedTerritoryId { get; private set; }

    public bool TrackingTerritoryMatches => this.LockedTerritoryId is null || this.clientState.TerritoryType == this.LockedTerritoryId.Value;

    public void SetVenueInfo(string venueName, string venueAddress, DateTime nowUtc)
    {
        this.VenueName = venueName.Trim();
        this.VenueAddress = venueAddress.Trim();
        this.database.SetVenueInfo(this.VenueName, this.VenueAddress, nowUtc);
    }

    public void SetFilters(bool lockToOpenTerritory, bool useDistanceFilter, float venueRadiusYalms, DateTime nowUtc)
    {
        this.LockToOpenTerritory = lockToOpenTerritory;
        this.UseDistanceFilter = useDistanceFilter;
        this.VenueRadiusYalms = Math.Clamp(venueRadiusYalms, 5f, 150f);

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

    public void SetVenueOpen(bool isOpen, DateTime nowUtc)
    {
        this.VenueOpen = isOpen;
        if (isOpen && this.LockToOpenTerritory)
        {
            this.LockedTerritoryId = this.clientState.TerritoryType;
        }
        else if (!isOpen)
        {
            this.LockedTerritoryId = null;
        }

        this.database.SetVenueOpen(isOpen, this.VenueName, this.VenueAddress, nowUtc);
        if (!isOpen)
        {
            this.currentlyPresent.Clear();
            this.identityLookup.Clear();
            this.objectIdLookup.Clear();
        }
        else
        {
            // Defer scanning to Tick (framework update on main thread).
            // Plugin constructor can run off-thread and cannot touch ObjectTable.LocalPlayer safely.
            this.lastSampleBucketUtc = DateTime.MinValue;
            this.lastScanUtc = DateTime.MinValue;
        }
    }

    public void Tick(DateTime nowUtc)
    {
        if (!this.VenueOpen)
        {
            return;
        }

        if ((nowUtc - this.lastScanUtc).TotalMilliseconds < 1000)
        {
            return;
        }

        this.lastScanUtc = nowUtc;

        if (!this.TrackingTerritoryMatches)
        {
            this.MarkAllAsAbsent(nowUtc);
            return;
        }

        this.ScanPlayerObjects(nowUtc, suppressFirstVisitNotifications: false);
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

            if (this.UseDistanceFilter && localPlayer is not null)
            {
                var distance = Vector3.Distance(localPlayer.Position, player.Position);
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

            var world = this.GetWorldName(player);

            var guest = new GuestIdentity(name, world);
            var key = guest.Key;
            presentNow.Add(key);
            this.identityLookup[key] = guest;
            activeObjectIds[key] = player.GameObjectId;

            var change = this.database.MarkVisitorPresent(guest, nowUtc, player.GameObjectId, this.VenueName, this.VenueAddress);
            if (change.BecamePresent)
            {
                this.currentlyPresent.Add(key);
            }

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
