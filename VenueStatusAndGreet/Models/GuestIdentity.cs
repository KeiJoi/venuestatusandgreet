namespace VenueStatusAndGreet.Models;

public readonly record struct GuestIdentity(string Name, string HomeWorld)
{
    public string DisplayName => string.IsNullOrWhiteSpace(this.HomeWorld)
        ? this.Name
        : $"{this.Name}@{this.HomeWorld}";

    public string Key => $"{this.Name.Trim().ToLowerInvariant()}@{this.HomeWorld.Trim().ToLowerInvariant()}";
}
