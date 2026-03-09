namespace VenueStatusAndGreet.Models;

public sealed class VenueAddressSnapshot
{
    public string DataCenter { get; init; } = string.Empty;

    public string Server { get; init; } = string.Empty;

    public string District { get; init; } = string.Empty;

    public int? Ward { get; init; }

    public int? Plot { get; init; }

    public bool IsSubdivision { get; init; }

    public bool IsHousingArea => this.Ward.HasValue && this.Plot.HasValue && !string.IsNullOrWhiteSpace(this.District);

    public string ToAddressString()
    {
        var division = this.IsSubdivision ? "Subdivision" : "Main Division";
        var ward = this.Ward?.ToString() ?? "?";
        var plot = this.Plot?.ToString() ?? "?";
        return $"{this.DataCenter} | {this.Server} | {this.District} | Ward {ward} | Plot {plot} ({division})";
    }
}
