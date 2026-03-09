namespace VenueStatusAndGreet.Models;

public sealed class VenueSessionEntry
{
    public long SessionId { get; set; }

    public DateTime OpenedAtLocal { get; set; }

    public DateTime? ClosedAtLocal { get; set; }

    public DateOnly NightDate { get; set; }

    public string VenueName { get; set; } = string.Empty;

    public string Label =>
        this.ClosedAtLocal is DateTime closed
            ? $"{this.NightDate:yyyy-MM-dd} {this.OpenedAtLocal:HH:mm} - {closed:HH:mm}"
            : $"{this.NightDate:yyyy-MM-dd} {this.OpenedAtLocal:HH:mm} - (open)";
}
