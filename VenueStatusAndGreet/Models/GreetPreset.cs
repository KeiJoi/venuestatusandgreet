namespace VenueStatusAndGreet.Models;

public sealed class GreetPreset
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Line1 { get; set; } = string.Empty;

    public string Line2 { get; set; } = string.Empty;

    public string Line3 { get; set; } = string.Empty;

    public string Line4 { get; set; } = string.Empty;

    public IReadOnlyList<string> MessageLines => new[] { this.Line1, this.Line2, this.Line3 }
        .Where(static x => !string.IsNullOrWhiteSpace(x))
        .ToList();

    public bool HasActions => this.MessageLines.Count > 0 || !string.IsNullOrWhiteSpace(this.Line4);
}
