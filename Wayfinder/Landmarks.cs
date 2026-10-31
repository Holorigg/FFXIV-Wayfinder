using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Wayfinder;

internal readonly record struct Landmark(uint Territory, Vector2 Xz);

// Fixed services drawn as icons on the area maps. A board stand shows as several icons close together;
// any of them is a fine destination.
internal sealed class Landmarks
{
    private const ushort MarketBoardIcon = 60570;
    private readonly List<Landmark> marketBoards = [];

    public Landmarks(IDataManager data)
    {
        var markers = data.GetSubrowExcelSheet<MapMarker>();
        foreach (var map in data.GetExcelSheet<Map>())
        {
            var territory = map.TerritoryType.RowId;
            if (territory == 0 || map.MapMarkerRange == 0 || !markers.TryGetRow(map.MapMarkerRange, out var rows))
                continue;
            var scale = map.SizeFactor / 100f;
            foreach (var marker in rows)
            {
                if (marker.Icon != MarketBoardIcon)
                    continue;
                var xz = new Vector2((marker.X - 1024f) / scale - map.OffsetX, (marker.Y - 1024f) / scale - map.OffsetY);
                marketBoards.Add(new Landmark(territory, xz));
            }
        }
    }

    public int MarketBoardCount => marketBoards.Count;

    // Nearest board: in this zone by straight line, otherwise in the zone the fewest exits away.
    // Housing wards are left out naturally: their map links have no exit arrow, so they are never reachable on foot.
    public Landmark? NearestMarketBoard(uint here, Vector2 player, ZoneLinks links)
    {
        var hops = links.DistancesFrom(here, 6);
        Landmark? best = null;
        var bestHops = int.MaxValue;
        var bestDistance = float.MaxValue;
        foreach (var board in marketBoards)
        {
            if (!hops.TryGetValue(board.Territory, out var h) || h > bestHops)
                continue;
            var distance = h == 0 ? Vector2.Distance(board.Xz, player) : 0f;
            if (h == bestHops && distance >= bestDistance)
                continue;
            best = board;
            bestHops = h;
            bestDistance = distance;
        }
        return best;
    }
}
