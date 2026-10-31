using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Wayfinder;

internal readonly record struct NpcPlace(uint Level, string Name, string Title, uint Territory, string Zone, Vector3 Position);

// Every NPC with a fixed spot in the Level sheet (Type 8), searchable by name. NPCs that only exist in layout files
// (some quest NPCs) are not here; quest search areas find those live instead.
internal sealed class NpcIndex
{
    private readonly List<NpcPlace> places = [];

    public NpcIndex(IDataManager data)
    {
        var residents = data.GetExcelSheet<ENpcResident>();
        var territories = data.GetExcelSheet<TerritoryType>();
        var zoneNames = new Dictionary<uint, string>();
        var seen = new HashSet<(uint, uint)>();
        foreach (var level in data.GetExcelSheet<Level>())
        {
            if (level.Type != 8 || !residents.TryGetRow(level.Object.RowId, out var npc))
                continue;
            var name = npc.Singular.ExtractText();
            var territory = level.Territory.RowId;
            // One entry per NPC and zone: story variants place the same NPC several times in one zone.
            if (name.Length == 0 || territory == 0 || !seen.Add((level.Object.RowId, territory)))
                continue;
            if (!zoneNames.TryGetValue(territory, out var zone))
            {
                zone = territories.TryGetRow(territory, out var row) ? row.PlaceName.ValueNullable?.Name.ExtractText() ?? "" : "";
                zoneNames[territory] = zone;
            }
            if (zone.Length == 0)
                continue;
            places.Add(new NpcPlace(level.RowId, char.ToUpperInvariant(name[0]) + name[1..], npc.Title.ExtractText(), territory, zone,
                new Vector3(level.X, level.Y, level.Z)));
        }
    }

    public int Count => places.Count;

    public NpcPlace? ByLevel(uint level) => places.Find(p => p.Level == level) is { Level: not 0 } found ? found : null;

    // Name matches, reachable ones first: this zone, then by zone transitions; unreachable ones last.
    public List<NpcPlace> Search(string text, uint here, ZoneLinks links, int limit)
    {
        var result = new List<NpcPlace>();
        if (text.Trim().Length < 2)
            return result;
        var hops = links.DistancesFrom(here, 8);
        foreach (var place in places)
        {
            if (place.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
                result.Add(place);
        }
        int Rank(NpcPlace p) => hops.TryGetValue(p.Territory, out var h) ? h : 100;
        result.Sort((a, b) => Rank(a) != Rank(b) ? Rank(a).CompareTo(Rank(b)) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (result.Count > limit)
            result.RemoveRange(limit, result.Count - limit);
        return result;
    }
}
