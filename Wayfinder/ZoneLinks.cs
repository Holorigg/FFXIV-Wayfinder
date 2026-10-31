using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Wayfinder;

// An NPC that carries you to another zone for a fare: ferry skipper, airship ticketer, lift attendant, gatekeep.
// Option is the line to pick in their menu ("Purchase Passage to Aleport").
internal sealed record Transport(string Npc, string Option, uint Gil, uint[] Quests, bool AnyQuest);

internal sealed record ZoneExit(uint From, uint To, Vector2 Xz, string Name, Transport? Via = null);

// Zone transitions come from the red map-link arrows: MapMarker rows with DataType 1 and an icon point at another Map.
internal sealed class ZoneLinks
{
    // TerritoryIntendedUse: 0 = city, 1 = open field. Transports to anything else (inn rooms, housing, the Gold
    // Saucer, instanced areas) are not travel between public zones.
    private static readonly HashSet<uint> PublicZones = [0, 1];

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
        TransportCount = AddTransports(data);
    }

    public int Count => exits.Count;

    public int EntranceCount { get; }

    public int TransportCount { get; }

    // Which exits may be used right now; transports are dropped while their quest is not done or the option is off.
    public Func<ZoneExit, bool> Usable { get; set; } = _ => true;

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

    // Ferries, airships, lifts and gates are NPC menus: an ENpc placed in the Level sheet (Type 8) whose event
    // list holds Warp rows (handler type 2 in the high word). A Warp names the destination zone, the menu line,
    // the fare and the quests that unlock it.
    private int AddTransports(IDataManager data)
    {
        var territories = data.GetExcelSheet<TerritoryType>();
        var residents = data.GetExcelSheet<ENpcResident>();
        var warps = data.GetExcelSheet<Warp>();

        bool IsPublic(uint territory) => territories.TryGetRow(territory, out var zone) && PublicZones.Contains(zone.TerritoryIntendedUse.RowId);

        // Every NPC whose event list holds a named Warp into a public zone, whether or not the Level sheet places it:
        // some (Ul'dah's lift attendant Nanahomi) only exist in the zone layout and are picked up live.
        foreach (var npc in data.GetExcelSheet<ENpcBase>())
        {
            List<(uint, Transport)>? list = null;
            foreach (var handler in npc.ENpcData)
            {
                if (handler.RowId >> 16 != 2 || !warps.TryGetRow(handler.RowId, out var warp))
                    continue;
                var to = warp.TerritoryType.RowId;
                var option = warp.Name.ExtractText();
                // Unnamed warps are chocobo porters and innkeepers.
                if (string.IsNullOrEmpty(option) || !IsPublic(to))
                    continue;

                var condition = warp.WarpCondition.ValueNullable;
                var quests = new List<uint>();
                if (condition is { } c)
                {
                    foreach (var quest in new[] { c.RequiredQuest1.RowId, c.RequiredQuest2.RowId, c.RequiredQuest3.RowId, c.RequiredQuest4.RowId })
                    {
                        if (quest != 0)
                            quests.Add(quest);
                    }
                }
                var npcName = residents.TryGetRow(npc.RowId, out var resident) ? resident.Singular.ExtractText() : "";
                if (npcName.Length > 0)
                    npcName = char.ToUpperInvariant(npcName[0]) + npcName[1..];
                // CompleteParam 2: any one of the listed quests (the airship pass comes from either of two quests).
                list ??= [];
                list.Add((to, new Transport(npcName, option, condition?.Gil ?? 0, [.. quests], condition?.CompleteParam == 2)));
            }
            if (list != null)
                warpNpcs[npc.RowId] = list;
        }

        var seen = new HashSet<(uint, uint)>();
        var added = 0;
        foreach (var level in data.GetExcelSheet<Level>())
        {
            // Type 8 = NPC placed in the world.
            if (level.Type != 8 || !warpNpcs.TryGetValue(level.Object.RowId, out var list))
                continue;
            var from = level.Territory.RowId;
            if (!IsPublic(from))
                continue;
            foreach (var (to, via) in list)
            {
                // Same-zone warps are lifts to a landing in the same zone.
                if (to == from || !seen.Add((level.Object.RowId, to)))
                    continue;
                Add(new ZoneExit(from, to, new Vector2(level.X, level.Z), ZoneName(territories, to), via));
                added++;
            }
        }
        foreach (var row in territories)
            zoneNames[row.RowId] = row.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{row.RowId}";
        return added;
    }

    private static string ZoneName(Lumina.Excel.ExcelSheet<TerritoryType> territories, uint id) =>
        territories.TryGetRow(id, out var zone) ? zone.PlaceName.ValueNullable?.Name.ExtractText() ?? $"#{id}" : $"#{id}";

    private readonly Dictionary<uint, List<(uint To, Transport Via)>> warpNpcs = [];
    private readonly Dictionary<uint, string> zoneNames = [];

    // Transport NPCs currently loaded around the player (ENpc id, position). Ones without a Level row become
    // exits of this zone until the player leaves it.
    public void UpdateLive(uint zone, IEnumerable<(uint Npc, Vector2 Xz)> loaded)
    {
        if (!exits.TryGetValue(zone, out var list))
            exits[zone] = list = [];
        foreach (var (npc, xz) in loaded)
        {
            if (!warpNpcs.TryGetValue(npc, out var warps))
                continue;
            foreach (var (to, via) in warps)
            {
                if (to == zone || list.Exists(e => e.To == to && e.Via?.Npc == via.Npc && Vector2.Distance(e.Xz, xz) < 8f))
                    continue;
                list.Add(new ZoneExit(zone, to, xz, zoneNames.TryGetValue(to, out var name) ? name : $"#{to}", via));
                LiveAdded++;
            }
        }
    }

    public int LiveAdded { get; private set; }

    // Zone transitions from `from` to every territory reachable within `limit` hops (itself = 0).
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
                if (Usable(exit) && depth.TryAdd(exit.To, depth[zone] + 1))
                    queue.Enqueue(exit.To);
            }
        }
        return depth;
    }

    // Number of zone transitions between two territories, or -1 when they are not connected.
    public int Hops(uint from, uint goal, int limit = 4)
    {
        if (from == goal)
            return 0;
        var depth = DistancesFrom(from, limit);
        return depth.TryGetValue(goal, out var hops) ? hops : -1;
    }

    // Where you come out in `zone` when arriving from `from`: its own exit leading back there, if it has one.
    public Vector2? ArrivalIn(uint zone, uint from)
    {
        if (!exits.TryGetValue(zone, out var list))
            return null;
        var back = list.Find(e => e.To == from && e.Via == null);
        return back?.Xz;
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
                if (Usable(exit) && previous.TryAdd(exit.To, zone))
                    queue.Enqueue(exit.To);
            }
        }

        if (!previous.ContainsKey(goal))
            return [];

        var next = goal;
        while (previous[next] != from)
            next = previous[next];
        return exits[from].FindAll(e => e.To == next && Usable(e));
    }
}
