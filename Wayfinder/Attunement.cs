using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Map = Lumina.Excel.Sheets.Map;

namespace Wayfinder;

internal readonly record struct AttunePoint(uint Id, uint Territory, Vector2 Xz, string Name, bool Shard);

// Aetherytes and city aethernet shards the player has not attuned to yet. Attuning makes teleports to that spot
// possible and adds aethernet shortcuts. Positions come from the map icons (MapMarker DataType 3 = Aetheryte row,
// DataType 4 = AethernetName row), the same way Aethernet and Teleports find them.
internal sealed class Attunement
{
    private readonly List<AttunePoint> points = [];

    public Attunement(IDataManager data)
    {
        var byId = new Dictionary<uint, Aetheryte>();
        var byName = new Dictionary<uint, Aetheryte>();
        foreach (var row in data.GetExcelSheet<Aetheryte>())
        {
            if (row.Invisible || row.Territory.RowId == 0 || (!row.IsAetheryte && row.AethernetGroup == 0))
                continue;
            byId[row.RowId] = row;
            if (row.AethernetName.RowId != 0)
                byName.TryAdd(row.AethernetName.RowId, row);
        }

        var seen = new HashSet<uint>();
        var markers = data.GetSubrowExcelSheet<MapMarker>();
        foreach (var map in data.GetExcelSheet<Map>())
        {
            if (map.MapMarkerRange == 0 || !markers.TryGetRow(map.MapMarkerRange, out var rows))
                continue;
            var scale = map.SizeFactor / 100f;
            foreach (var marker in rows)
            {
                Aetheryte row;
                if (marker.DataType == 3 && byId.TryGetValue(marker.DataKey.RowId, out var main))
                    row = main;
                else if (marker.DataType == 4 && byName.TryGetValue(marker.DataKey.RowId, out var shard))
                    row = shard;
                else
                    continue;
                if (map.TerritoryType.RowId != row.Territory.RowId || !seen.Add(row.RowId))
                    continue;
                var xz = new Vector2((marker.X - 1024f) / scale - map.OffsetX, (marker.Y - 1024f) / scale - map.OffsetY);
                var name = row.IsAetheryte ? row.PlaceName.ValueNullable?.Name.ExtractText() : row.AethernetName.ValueNullable?.Name.ExtractText();
                points.Add(new AttunePoint(row.RowId, row.Territory.RowId, xz, string.IsNullOrEmpty(name) ? $"#{row.RowId}" : name, !row.IsAetheryte));
            }
        }
    }

    public int Count => points.Count;

    // Nearest one not attuned yet: in this zone by straight line, otherwise in the zone fewest transitions away.
    public unsafe AttunePoint? Nearest(uint here, Vector2 player, ZoneLinks links)
    {
        var ui = UIState.Instance();
        if (ui == null)
            return null;
        var hops = links.DistancesFrom(here, 6);
        AttunePoint? best = null;
        var bestHops = int.MaxValue;
        var bestDistance = float.MaxValue;
        foreach (var point in points)
        {
            if (!hops.TryGetValue(point.Territory, out var h) || h > bestHops || ui->IsAetheryteUnlocked(point.Id))
                continue;
            var distance = h == 0 ? Vector2.Distance(point.Xz, player) : 0f;
            if (h == bestHops && distance >= bestDistance)
                continue;
            best = point;
            bestHops = h;
            bestDistance = distance;
        }
        return best;
    }
}
