namespace VenueStatusAndGreet.Models;

public sealed class VisitorNightSummary
{
    public DateOnly NightDate { get; set; }

    public string CharacterName { get; set; } = string.Empty;

    public string HomeWorld { get; set; } = string.Empty;

    public int Visits { get; set; }

    public TimeSpan TotalTime { get; set; }

    public bool IsPresent { get; set; }

    public bool Greeted { get; set; }

    public ulong LastObjectId { get; set; }

    public GuestIdentity Identity => new(this.CharacterName, this.HomeWorld);
}
