using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Map = Lumina.Excel.Sheets.Map;

namespace Wayfinder;

internal readonly record struct Shard(uint Id, byte Group, uint Territory, Vector2 Xz, string Name);

internal readonly record struct AethernetPlan(Shard Entry, Shard Exit, float Cost);

// City aethernet: every Aetheryte row with an AethernetGroup belongs to one city network (the big aetheryte and
// its shards). Travel inside a network is free, so walking to a shard and hopping can beat a long walk.
//
// The Aetheryte sheet's Level references for city shards are empty, so positions come from the map icons:
// MapMarker DataType 3 points at the Aetheryte row, DataType 4 at its AethernetName (PlaceName) row.
internal sealed class Aethernet
{
    // Streets are not straight lines; walking estimates are inflated by this factor.
    private const float Winding = 1.3f;
    private readonly List<Shard> shards = [];

    public Aethernet(IDataManager data)
    {
        var byId = new Dictionary<uint, Aetheryte>();
        var byName = new Dictionary<uint, Aetheryte>();
        foreach (var row in data.GetExcelSheet<Aetheryte>())
        {
            if (row.AethernetGroup == 0 || row.Invisible || row.Territory.RowId == 0)
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
                // A shard shows on every sub-map of its zone; keep the one drawn on its own territory's map.
                if (map.TerritoryType.RowId != row.Territory.RowId || !seen.Add(row.RowId))
                    continue;

                var xz = new Vector2((marker.X - 1024f) / scale - map.OffsetX, (marker.Y - 1024f) / scale - map.OffsetY);
                var name = row.AethernetName.ValueNullable?.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    name = row.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Shard {row.RowId}";
                shards.Add(new Shard(row.RowId, row.AethernetGroup, row.Territory.RowId, xz, name));
            }
        }
    }

    public int Count => shards.Count;

    // Cheapest walk-to-shard + hop + walk-from-shard plan, or null (with the reason) when this zone has no
    // attuned shard on the same network as one in the goal's zone.
    public unsafe AethernetPlan? Plan(uint here, Vector2 player, uint goalTerritory, Vector2 goal, float overhead, out string why)
    {
        why = "";
        var ui = UIState.Instance();
        if (ui == null)
            return null;

        AethernetPlan? best = null;
        int shardsHere = 0, attunedHere = 0;
        foreach (var entry in shards)
        {
            if (entry.Territory != here)
                continue;
            shardsHere++;
            if (!ui->IsAetheryteUnlocked(entry.Id))
                continue;
            attunedHere++;
            var toEntry = Vector2.Distance(player, entry.Xz) * Winding;
            foreach (var exit in shards)
            {
                if (exit.Group != entry.Group || exit.Id == entry.Id || exit.Territory != goalTerritory || !ui->IsAetheryteUnlocked(exit.Id))
                    continue;
                var cost = toEntry + overhead + Vector2.Distance(exit.Xz, goal) * Winding;
                if (best is not { } b || cost < b.Cost)
                    best = new AethernetPlan(entry, exit, cost);
            }
        }

        if (best == null)
        {
            why = shardsHere == 0 ? $"no aethernet shards in zone {here}"
                : attunedHere == 0 ? $"none of the {shardsHere} shards in zone {here} are attuned"
                : $"no attuned shard on the same network in zone {goalTerritory}";
        }
        return best;
    }

    public static float WalkEstimate(Vector2 player, Vector2 goal) => Vector2.Distance(player, goal) * Winding;
}
