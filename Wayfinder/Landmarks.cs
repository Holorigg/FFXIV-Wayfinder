using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Wayfinder;

// Stored in the configuration as a number: append new kinds at the end.
public enum LandmarkKind
{
    MarketBoard,
    Mender,
    SummoningBell,
    Inn,
    Mail,
    HuntBoard,
    Chocobokeep,
    Guild,
    // Not a map icon: aetherytes and aethernet shards not attuned yet (see Attunement).
    Unattuned,
}

internal readonly record struct Landmark(uint Territory, Vector2 Xz, bool Housing, string Label);

// Fixed services drawn as icons on the area maps. A stand (several boards, a mender with an assistant) shows as
// icons close together; any of them is a fine destination.
internal sealed class Landmarks
{
    // Map icon ids, each checked against the NPC or object standing next to the icon in the starting cities:
    // 60570 market board, 60434 mender, 60425 summoning bell, 60436 innkeep, 60551 delivery moogle,
    // 60571 hunt board, 60311 chocobokeep.
    private static readonly Dictionary<ushort, LandmarkKind> Icons = new()
    {
        [60570] = LandmarkKind.MarketBoard,
        [60434] = LandmarkKind.Mender,
        [60425] = LandmarkKind.SummoningBell,
        [60436] = LandmarkKind.Inn,
        [60551] = LandmarkKind.Mail,
        [60571] = LandmarkKind.HuntBoard,
        [60311] = LandmarkKind.Chocobokeep,
    };

    // Each guild has its own icon in this range; the marker's subtext names it ("Arrzaneth Ossuary (Thaumaturges' Guild)").
    private const ushort GuildIconFirst = 60314;
    private const ushort GuildIconLast = 60364;

    // TerritoryIntendedUse of the residential wards (Mist, The Goblet, Lavender Beds, ...).
    private const uint HousingWard = 13;

    private readonly Dictionary<LandmarkKind, List<Landmark>> places = [];

    public Landmarks(IDataManager data)
    {
        var markers = data.GetSubrowExcelSheet<MapMarker>();
        var territories = data.GetExcelSheet<TerritoryType>();
        foreach (var map in data.GetExcelSheet<Map>())
        {
            var territory = map.TerritoryType.RowId;
            if (territory == 0 || map.MapMarkerRange == 0 || !markers.TryGetRow(map.MapMarkerRange, out var rows))
                continue;
            var scale = map.SizeFactor / 100f;
            var housing = territories.TryGetRow(territory, out var zone) && zone.TerritoryIntendedUse.RowId == HousingWard;
            foreach (var marker in rows)
            {
                LandmarkKind kind;
                var label = "";
                if (Icons.TryGetValue(marker.Icon, out var known))
                {
                    kind = known;
                }
                else if (marker.Icon is >= GuildIconFirst and <= GuildIconLast)
                {
                    kind = LandmarkKind.Guild;
                    label = marker.PlaceNameSubtext.ValueNullable?.Name.ExtractText() ?? "";
                    if (string.IsNullOrEmpty(label))
                        continue;
                }
                else
                {
                    continue;
                }
                var xz = new Vector2((marker.X - 1024f) / scale - map.OffsetX, (marker.Y - 1024f) / scale - map.OffsetY);
                if (!places.TryGetValue(kind, out var list))
                    places[kind] = list = [];
                list.Add(new Landmark(territory, xz, housing, label));
            }
        }
    }

    public int Count(LandmarkKind kind) => places.TryGetValue(kind, out var list) ? list.Count : 0;

    // Nearest place: in this zone by straight line, otherwise in the zone the fewest exits away.
    // A residential ward only wins when nothing in a public zone is reachable: it sits behind a gate next to the
    // city and is just as few exits away, but it is never where you want to go.
    // `filter` narrows the kind further (the guild of the player's class).
    public Landmark? Nearest(LandmarkKind kind, uint here, Vector2 player, ZoneLinks links, Func<Landmark, bool>? filter = null)
    {
        if (!places.TryGetValue(kind, out var list))
            return null;
        if (filter != null)
            list = list.FindAll(p => filter(p));
        var hops = links.DistancesFrom(here, 6);
        return Nearest(list, hops, player, here, housing: false) ?? Nearest(list, hops, player, here, housing: true);
    }

    private static Landmark? Nearest(List<Landmark> list, Dictionary<uint, int> hops, Vector2 player, uint here, bool housing)
    {
        Landmark? best = null;
        var bestHops = int.MaxValue;
        var bestDistance = float.MaxValue;
        foreach (var place in list)
        {
            if (place.Housing != housing && place.Territory != here)
                continue;
            if (!hops.TryGetValue(place.Territory, out var h) || h > bestHops)
                continue;
            var distance = h == 0 ? Vector2.Distance(place.Xz, player) : 0f;
            if (h == bestHops && distance >= bestDistance)
                continue;
            best = place;
            bestHops = h;
            bestDistance = distance;
        }
        return best;
    }
}
