namespace VenueStatusAndGreet.Models;

public sealed class GuestSampleRow
{
    public DateTime SampleTimeLocal { get; set; }

    public int GuestCount { get; set; }

    public DateOnly NightDate { get; set; }
}
