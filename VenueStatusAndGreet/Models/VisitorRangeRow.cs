namespace VenueStatusAndGreet.Models;

public sealed class VisitorRangeRow
{
    public DateOnly NightDate { get; set; }

    public string CharacterName { get; set; } = string.Empty;

    public string HomeWorld { get; set; } = string.Empty;

    public int Visits { get; set; }

    public TimeSpan TotalTime { get; set; }

    public bool Greeted { get; set; }
}
