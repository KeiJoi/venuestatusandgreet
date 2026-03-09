# Venue Status and Greet

Dalamud API Level 14 plugin scaffold for FFXIV.

Core features included:
- `/vsg` command to toggle the main window.
- Two-tab UI: `Venue Status` and `Greet`.
- SQLite persistence for nights, visitors, greet flags, 5-minute samples, and presets.
- Auto-greet queue (`/tell`) with 2-second pauses between up to 3 lines.
- First-visit-only greeting behavior per night.
- Excel export (`.xlsx`) for daily stats, visitors, and samples.

Project:
- [VenueStatusAndGreet.csproj](/c:/FFXIVplugs/venuestatusandgreet/VenueStatusAndGreet/VenueStatusAndGreet.csproj)
