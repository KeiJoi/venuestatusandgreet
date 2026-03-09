using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using VenueStatusAndGreet.Models;

namespace VenueStatusAndGreet.Services;

public sealed class VenueAddressService
{
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, string> districtNameCache = [];

    public VenueAddressService(
        IClientState clientState,
        IPlayerState playerState,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.log = log;
    }

    public bool TryGetCurrentAddress(out VenueAddressSnapshot snapshot)
    {
        snapshot = new VenueAddressSnapshot();
        var world = this.playerState.CurrentWorld.ValueNullable;
        if (!world.HasValue)
        {
            return false;
        }

        var server = Clean(world.Value.Name.ExtractText());
        var dc = Clean(world.Value.DataCenter.ValueNullable?.Name.ExtractText());
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(dc))
        {
            return false;
        }

        var territoryId = this.clientState.TerritoryType;
        var districtTerritoryId = territoryId;
        var ward = default(int?);
        var plot = default(int?);
        var subdivision = false;

        try
        {
            unsafe
            {
                var housingManager = HousingManager.Instance();
                if (housingManager is not null)
                {
                    if (housingManager->IsInside())
                    {
                        var original = HousingManager.GetOriginalHouseTerritoryTypeId();
                        if (original > 0 && original <= ushort.MaxValue)
                        {
                            districtTerritoryId = (ushort)original;
                        }
                    }

                    var wardRaw = housingManager->GetCurrentWard();
                    if (wardRaw >= 0)
                    {
                        ward = wardRaw + 1;
                    }

                    var plotRaw = housingManager->GetCurrentPlot();
                    if (plotRaw >= 0)
                    {
                        plot = plotRaw + 1;
                    }

                    subdivision = housingManager->GetCurrentDivision() == 1;
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "Housing manager lookup failed while auto-detecting venue address.");
        }

        var district = this.ResolveDistrictName(districtTerritoryId);
        if (string.IsNullOrWhiteSpace(district))
        {
            district = this.ResolveDistrictName(territoryId);
        }

        snapshot = new VenueAddressSnapshot
        {
            DataCenter = dc,
            Server = server,
            District = string.IsNullOrWhiteSpace(district) ? "Unknown District" : district,
            Ward = ward,
            Plot = plot,
            IsSubdivision = subdivision,
        };

        return true;
    }

    private string ResolveDistrictName(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
        {
            return string.Empty;
        }

        if (this.districtNameCache.TryGetValue(territoryTypeId, out var cached))
        {
            return cached;
        }

        var sheet = this.dataManager.GetExcelSheet<TerritoryType>();
        var row = sheet?.GetRow(territoryTypeId);
        if (!row.HasValue)
        {
            return string.Empty;
        }

        var placeName = Clean(row.Value.PlaceName.ValueNullable?.Name.ExtractText());
        if (string.IsNullOrWhiteSpace(placeName))
        {
            placeName = Clean(row.Value.PlaceNameZone.ValueNullable?.Name.ExtractText());
        }

        if (string.IsNullOrWhiteSpace(placeName))
        {
            placeName = Clean(row.Value.PlaceNameRegion.ValueNullable?.Name.ExtractText());
        }

        this.districtNameCache[territoryTypeId] = placeName;
        return placeName;
    }

    private static string Clean(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
    }
}
