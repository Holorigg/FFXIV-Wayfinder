using System;
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
                Add(new ZoneExit(from, to, xz, name));
            }
        }

        EntranceCount = AddEntrances(data);
    }

    public int Count => exits.Count;

    public int EntranceCount { get; }

    private void Add(ZoneExit exit)
    {
        if (!exits.TryGetValue(exit.From, out var list))
            exits[exit.From] = list = [];
        list.Add(exit);
    }

    // Interiors such as the Waking Sands have an exit arrow on their own map but no arrow leading in: the way in is
    // a door object outside ("Waking Sands entrance" in Western Thanalan). For every one-way link inside -> outside,
    // look in the outside zone for an EObj placed in the Level sheet whose name contains the interior's name.
    private int AddEntrances(IDataManager data)
    {
        var missing = new List<(uint Inside, uint Outside, string Name)>();
        var territories = data.GetExcelSheet<TerritoryType>();
        foreach (var (inside, list) in exits)
        {
            foreach (var exit in list)
            {
                var outside = exit.To;
                if (exits.TryGetValue(outside, out var back) && back.Exists(e => e.To == inside))
                    continue;
                if (missing.Exists(m => m.Inside == inside && m.Outside == outside))
                    continue;
                var name = territories.TryGetRow(inside, out var zone) ? zone.PlaceName.ValueNullable?.Name.ExtractText() : null;
                if (string.IsNullOrEmpty(name))
                    continue;
                if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
                    name = name[4..];
                missing.Add((inside, outside, name));
            }
        }
        if (missing.Count == 0)
            return 0;

        var wanted = new HashSet<uint>();
        foreach (var m in missing)
            wanted.Add(m.Outside);

        var objectNames = data.GetExcelSheet<EObjName>();
        var added = 0;
        foreach (var level in data.GetExcelSheet<Level>())
        {
            // Type 45 = event object placed in the world.
            if (level.Type != 45 || !wanted.Contains(level.Territory.RowId) || !objectNames.TryGetRow(level.Object.RowId, out var obj))
                continue;
            var objectName = obj.Singular.ExtractText();
            if (string.IsNullOrEmpty(objectName))
                continue;
            foreach (var m in missing)
            {
                if (m.Outside != level.Territory.RowId || !objectName.Contains(m.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var placeName = territories.TryGetRow(m.Inside, out var zone) ? zone.PlaceName.ValueNullable?.Name.ExtractText() ?? m.Name : m.Name;
                Add(new ZoneExit(m.Outside, m.Inside, new Vector2(level.X, level.Z), placeName));
                added++;
            }
        }
        return added;
    }

    // Zone transitions from `from` to every territory reachable on foot within `limit` hops (itself = 0).
    public Dictionary<uint, int> DistancesFrom(uint from, int limit)
    {
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
                if (depth.TryAdd(exit.To, depth[zone] + 1))
                    queue.Enqueue(exit.To);
            }
        }
        return depth;
    }

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
