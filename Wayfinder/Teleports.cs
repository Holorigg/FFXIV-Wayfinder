using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Wayfinder;

internal readonly record struct TeleportOption(uint AetheryteId, byte SubIndex, string Name, uint Cost);

// Reads the player's own teleport list (unlocked aetherytes with their current gil cost) and picks the
// aetheryte in the destination zone that lands closest to the goal.
internal sealed class Teleports
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);
    private readonly ExcelSheet<Aetheryte> aetherytes;
    private readonly List<(uint Aetheryte, byte SubIndex, uint Territory, uint Cost)> unlocked = [];
    private DateTime nextRefresh;
    // Where each aetheryte stands. The Aetheryte sheet's Level reference is empty for them (Horizon has none), so
    // positions come from the map icon that points at the aetheryte (MapMarker DataType 3).
    private readonly Dictionary<uint, Vector2> positions = [];

    public Teleports(IDataManager data)
    {
        aetherytes = data.GetExcelSheet<Aetheryte>();
        var markers = data.GetSubrowExcelSheet<MapMarker>();
        foreach (var map in data.GetExcelSheet<Lumina.Excel.Sheets.Map>())
        {
            if (map.MapMarkerRange == 0 || !markers.TryGetRow(map.MapMarkerRange, out var rows))
                continue;
            var scale = map.SizeFactor / 100f;
            foreach (var marker in rows)
            {
                if (marker.DataType != 3 || !aetherytes.TryGetRow(marker.DataKey.RowId, out var row)
                    || row.Territory.RowId != map.TerritoryType.RowId || positions.ContainsKey(row.RowId))
                    continue;
                positions[row.RowId] = new Vector2((marker.X - 1024f) / scale - map.OffsetX, (marker.Y - 1024f) / scale - map.OffsetY);
            }
        }
    }

    public int PositionCount => positions.Count;

    private Vector2? PositionOf(uint aetheryte, Aetheryte row) =>
        positions.TryGetValue(aetheryte, out var xz) ? xz
        : row.Level.Count > 0 && row.Level[0].ValueNullable is { } level ? new Vector2(level.X, level.Z) : null;

    // Prefers an aetheryte in the goal's own zone; otherwise the one fewest zone transitions away
    // (Limsa's Upper Decks has no aetheryte of its own, the Lower Decks one is a single hop).
    // Only aetherytes that land fewer zone transitions from the goal than the player already is: next door to the
    // Waking Sands, a teleport to Ul'dah would only move them further away.
    public TeleportOption? Best(uint territory, Vector2 goal, ZoneLinks links, uint here)
    {
        RefreshIfDue();
        TeleportOption? best = null;
        var hopsFromHere = links.Hops(here, territory, 4);
        var bestHops = hopsFromHere > 0 ? hopsFromHere - 1 : int.MaxValue;
        var bestDistance = float.MaxValue;
        foreach (var entry in unlocked)
        {
            // Teleporting into the zone you are standing in only costs gil.
            if (entry.Territory == here || !aetherytes.TryGetRow(entry.Aetheryte, out var row))
                continue;
            var hops = links.Hops(entry.Territory, territory, 2);
            if (hops < 0 || hops > bestHops)
                continue;
            var distance = hops == 0 && PositionOf(entry.Aetheryte, row) is { } at
                ? Vector2.Distance(at, goal)
                : float.MaxValue / 2;
            if (hops == bestHops && distance >= bestDistance)
                continue;
            bestHops = hops;
            bestDistance = distance;
            best = new TeleportOption(entry.Aetheryte, entry.SubIndex, row.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Aetheryte {entry.Aetheryte}", entry.Cost);
        }
        return best;
    }

    // A teleport inside the zone the player is in: worth it when an aetheryte here lands much closer to the goal
    // (across Western Thanalan to Horizon, next to the Waking Sands). Straight lines, inflated like walking.
    public TeleportOption? InZone(uint here, Vector2 player, Vector2 goal, float minSaving, out float saving)
    {
        RefreshIfDue();
        saving = 0;
        TeleportOption? best = null;
        var walk = Vector2.Distance(player, goal) * Winding;
        foreach (var entry in unlocked)
        {
            if (entry.Territory != here || !aetherytes.TryGetRow(entry.Aetheryte, out var row) || PositionOf(entry.Aetheryte, row) is not { } at)
                continue;
            var left = Vector2.Distance(at, goal) * Winding;
            if (walk - left < minSaving || walk - left <= saving)
                continue;
            saving = walk - left;
            best = new TeleportOption(entry.Aetheryte, entry.SubIndex, row.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Aetheryte {entry.Aetheryte}", entry.Cost);
        }
        return best;
    }

    private const float Winding = 1.3f;

    public static unsafe bool Teleport(TeleportOption option)
    {
        var telepo = Telepo.Instance();
        return telepo != null && telepo->Teleport(option.AetheryteId, option.SubIndex);
    }

    private unsafe void RefreshIfDue()
    {
        var now = DateTime.UtcNow;
        if (now < nextRefresh)
            return;
        nextRefresh = now + RefreshEvery;

        var telepo = Telepo.Instance();
        if (telepo == null)
            return;
        telepo->UpdateAetheryteList();
        unlocked.Clear();
        foreach (var info in telepo->TeleportList)
        {
            // Skip housing entries (wards, plots, apartments): only public aetherytes.
            if (info.Ward != 0 || info.Plot != 0 || info.IsApartment || info.IsSharedHouse)
                continue;
            unlocked.Add((info.AetheryteId, info.SubIndex, info.TerritoryId, info.GilCost));
        }
    }
}
