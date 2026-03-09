namespace VenueStatusAndGreet.Models;

public sealed class DailyStatRow
{
    public DateOnly NightDate { get; set; }

    public int MaxGuests { get; set; }

    public int MinGuests { get; set; }

    public int UniqueGuests { get; set; }

    public int TotalVisits { get; set; }

    public TimeSpan TotalGuestTime { get; set; }
}
