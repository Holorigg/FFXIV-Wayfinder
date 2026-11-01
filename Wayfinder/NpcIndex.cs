using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Wayfinder;

// MapX/MapY are the coordinates the game shows on its map; Quests are the quests this NPC hands out (name, level).
internal readonly record struct NpcPlace(uint Level, uint Npc, string Name, string Title, uint Territory, string Zone, Vector3 Position,
                                         float MapX, float MapY, List<(string Name, int Level)> Quests);

// Every NPC with a fixed spot in the Level sheet (Type 8), searchable by name. NPCs that only exist in layout files
// (some quest NPCs) are not here; quest search areas find those live instead.
// Many different NPCs share a name ("Lalai" is eleven of them), so each entry carries its map coordinates and the
// quests it gives, which is what tells them apart.
internal sealed class NpcIndex
{
    private readonly List<NpcPlace> places = [];

    public NpcIndex(IDataManager data)
    {
        var residents = data.GetExcelSheet<ENpcResident>();
        var territories = data.GetExcelSheet<TerritoryType>();

        var quests = new Dictionary<uint, List<(string, int)>>();
        foreach (var quest in data.GetExcelSheet<Quest>())
        {
            var issuer = quest.IssuerStart.RowId;
            var name = quest.Name.ExtractText();
            if (issuer == 0 || name.Length == 0)
                continue;
            if (!quests.TryGetValue(issuer, out var list))
                quests[issuer] = list = [];
            list.Add((name, quest.ClassJobLevel[0]));
        }

        var zones = new Dictionary<uint, (string Name, float Scale, float OffsetX, float OffsetY)>();
        var byName = new Dictionary<(uint, string), List<int>>();
        foreach (var level in data.GetExcelSheet<Level>())
        {
            if (level.Type != 8 || !residents.TryGetRow(level.Object.RowId, out var npc))
                continue;
            var name = npc.Singular.ExtractText();
            var territory = level.Territory.RowId;
            if (name.Length == 0 || territory == 0)
                continue;
            if (!zones.TryGetValue(territory, out var zone))
            {
                zone = territories.TryGetRow(territory, out var row) && row.Map.ValueNullable is { } map
                    ? (row.PlaceName.ValueNullable?.Name.ExtractText() ?? "", map.SizeFactor / 100f, map.OffsetX, map.OffsetY)
                    : ("", 1f, 0f, 0f);
                zones[territory] = zone;
            }
            if (zone.Name.Length == 0)
                continue;

            var position = new Vector3(level.X, level.Y, level.Z);
            var given = quests.TryGetValue(level.Object.RowId, out var list) ? list : [];
            // The same person placed again nearby (story variants, separate NPC rows): one entry, keeping whichever
            // gives quests.
            var display = char.ToUpperInvariant(name[0]) + name[1..];
            if (!byName.TryGetValue((territory, display), out var same))
                byName[(territory, display)] = same = [];
            var match = same.FindIndex(i => Vector3.Distance(places[i].Position, position) < 6f);
            var twin = match >= 0 ? same[match] : -1;
            if (twin >= 0)
            {
                if (places[twin].Quests.Count == 0 && given.Count > 0)
                    places[twin] = places[twin] with { Quests = given };
                continue;
            }
            same.Add(places.Count);
            places.Add(new NpcPlace(level.RowId, level.Object.RowId, display, npc.Title.ExtractText(), territory, zone.Name, position,
                MapCoordinate(level.X, zone.Scale, zone.OffsetX), MapCoordinate(level.Z, zone.Scale, zone.OffsetY), given));
        }
    }

    // World position to the coordinate printed on the game's map (1 to about 42).
    private static float MapCoordinate(float world, float scale, float offset) =>
        41f / scale * ((world + offset) * scale + 1024f) / 2048f + 1f;

    public int Count => places.Count;

    public NpcPlace? ByLevel(uint level) => places.Find(p => p.Level == level) is { Level: not 0 } found ? found : null;

    // The quest this NPC gives that suits the player's level best, for telling namesakes apart.
    public static string? BestQuest(NpcPlace npc, int playerLevel)
    {
        string? best = null;
        var bestGap = int.MaxValue;
        foreach (var (name, level) in npc.Quests)
        {
            // Quests above the player's level rank after the ones they can take now.
            var gap = level <= playerLevel ? playerLevel - level : 100 + level - playerLevel;
            if (gap < bestGap)
            {
                bestGap = gap;
                best = name;
            }
        }
        return best;
    }

    // Name matches, reachable ones first: this zone, then by zone transitions; within that, quest givers first.
    public List<NpcPlace> Search(string text, uint here, ZoneLinks links, int limit, ISet<string> loaded)
    {
        var result = new List<NpcPlace>();
        var query = text.Trim();
        if (query.Length < 2)
            return result;
        var hops = links.DistancesFrom(here, 8);
        foreach (var place in places)
        {
            if (place.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                result.Add(place);
        }
        // Standing nearby right now (loaded in this zone) beats every stored spot.
        int Rank(NpcPlace p) => p.Territory == here && loaded.Contains(p.Name) ? -1
            : (hops.TryGetValue(p.Territory, out var h) ? h : 100) * 2 + (p.Quests.Count > 0 ? 0 : 1);
        result.Sort((a, b) => Rank(a) != Rank(b) ? Rank(a).CompareTo(Rank(b)) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (result.Count > limit)
            result.RemoveRange(limit, result.Count - limit);
        return result;
    }
}
