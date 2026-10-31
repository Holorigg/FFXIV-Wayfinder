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

    public Teleports(IDataManager data) => aetherytes = data.GetExcelSheet<Aetheryte>();

    // Prefers an aetheryte in the goal's own zone; otherwise the one fewest zone transitions away
    // (Limsa's Upper Decks has no aetheryte of its own, the Lower Decks one is a single hop).
    public TeleportOption? Best(uint territory, Vector2 goal, ZoneLinks links, uint here)
    {
        RefreshIfDue();
        TeleportOption? best = null;
        var bestHops = int.MaxValue;
        var bestDistance = float.MaxValue;
        foreach (var entry in unlocked)
        {
            // Teleporting into the zone you are standing in only costs gil.
            if (entry.Territory == here || !aetherytes.TryGetRow(entry.Aetheryte, out var row))
                continue;
            var hops = links.Hops(entry.Territory, territory, 2);
            if (hops < 0 || hops > bestHops)
                continue;
            var distance = hops == 0 && row.Level.Count > 0 && row.Level[0].ValueNullable is { } level
                ? Vector2.Distance(new Vector2(level.X, level.Z), goal)
                : float.MaxValue / 2;
            if (hops == bestHops && distance >= bestDistance)
                continue;
            bestHops = hops;
            bestDistance = distance;
            best = new TeleportOption(entry.Aetheryte, entry.SubIndex, row.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Aetheryte {entry.Aetheryte}", entry.Cost);
        }
        return best;
    }

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
