using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Wayfinder;

internal sealed record ZoneExit(uint From, uint To, Vector2 Xz, string Name);

// Zone transitions come from the red map-link arrows: MapMarker rows with DataType 1 and an icon point at another Map.
internal sealed class ZoneLinks
{
    private readonly Dictionary<uint, List<ZoneExit>> exits = [];

    public ZoneLinks(IDataManager data)
    {
        var maps = data.GetExcelSheet<Map>();
        var markers = data.GetSubrowExcelSheet<MapMarker>();
        foreach (var map in maps)
        {
            var from = map.TerritoryType.RowId;
            if (from == 0 || map.MapMarkerRange == 0 || !markers.TryGetRow(map.MapMarkerRange, out var rows))
                continue;

            var scale = map.SizeFactor / 100f;
            foreach (var marker in rows)
            {
                // Real exits carry the red arrow icon; icon-less links are just labels pointing at a neighbouring
                // map (e.g. a closed door in New Gridania labelled "Central Shroud") and cannot be walked through.
                if (marker.DataType != 1 || marker.Icon == 0 || !maps.TryGetRow(marker.DataKey.RowId, out var dest))
                    continue;
                var to = dest.TerritoryType.RowId;
                if (to == 0 || to == from)
                    continue;

                var xz = new Vector2((marker.X - 1024f) / scale - map.OffsetX, (marker.Y - 1024f) / scale - map.OffsetY);
                var name = dest.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{to}";
                if (!exits.TryGetValue(from, out var list))
                    exits[from] = list = [];
                list.Add(new ZoneExit(from, to, xz, name));
            }
        }
    }

    public int Count => exits.Count;

    // Number of zone transitions between two territories, or -1 when they are not connected on foot.
    public int Hops(uint from, uint goal, int limit = 4)
    {
        if (from == goal)
            return 0;
        var depth = new Dictionary<uint, int> { [from] = 0 };
        var queue = new Queue<uint>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var zone = queue.Dequeue();
            if (depth[zone] >= limit || !exits.TryGetValue(zone, out var list))
                continue;
            foreach (var exit in list)
            {
                if (!depth.TryAdd(exit.To, depth[zone] + 1))
                    continue;
                if (exit.To == goal)
                    return depth[exit.To];
                queue.Enqueue(exit.To);
            }
        }
        return -1;
    }

    // Exits out of `from` that start a shortest (fewest zones) route to `goal`.
    public List<ZoneExit> FirstHops(uint from, uint goal)
    {
        var previous = new Dictionary<uint, uint> { [from] = from };
        var queue = new Queue<uint>();
        queue.Enqueue(from);
        while (queue.Count > 0 && !previous.ContainsKey(goal))
        {
            var zone = queue.Dequeue();
            if (!exits.TryGetValue(zone, out var list))
                continue;
            foreach (var exit in list)
            {
                if (previous.TryAdd(exit.To, zone))
                    queue.Enqueue(exit.To);
            }
        }

        if (!previous.ContainsKey(goal))
            return [];

        var next = goal;
        while (previous[next] != from)
            next = previous[next];
        return exits[from].FindAll(e => e.To == next);
    }
}
