namespace VenueStatusAndGreet.Models;

public sealed class NightSummary
{
    public DateOnly NightDate { get; set; }

    public int CurrentGuests { get; set; }

    public int MaxGuests { get; set; }

    public int MinGuests { get; set; }

    public int UniqueGuests { get; set; }

    public int TotalVisits { get; set; }

    public TimeSpan TotalGuestTime { get; set; }

    public TimeSpan AverageGuestTime => this.UniqueGuests == 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(this.TotalGuestTime.TotalSeconds / this.UniqueGuests);
}
