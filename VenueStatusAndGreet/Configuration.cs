using Dalamud.Configuration;

namespace VenueStatusAndGreet;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string VenueName { get; set; } = "My Venue";

    public string VenueAddress { get; set; } = string.Empty;

    public bool IsVenueOpen { get; set; }

    public bool AutoGreetEnabled { get; set; } = true;

    public int StatsRangeDays { get; set; } = 7;

    public int? ActivePresetId { get; set; }

    public string ExportDirectory { get; set; } = string.Empty;

    public bool LockToOpenTerritory { get; set; } = true;

    public bool UseDistanceFilter { get; set; } = true;

    public float VenueRadiusYalms { get; set; } = 35f;

    public bool AutoDetectVenueAddress { get; set; } = true;

    public int TrackingPollIntervalSeconds { get; set; } = 900;
}
