using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Wayfinder;

// While a quest reward window is open (JournalAccept when taking a quest, JournalResult when turning it in): which
// optional reward suits the current class best, and which sells for the most. The quest is recognised by its
// reward item ids appearing among the window's values, looked up over every quest in the game (the quest may
// not be in the journal yet); nothing is clicked or selected.
internal sealed class RewardHint
{
    private static readonly string[] Windows = ["JournalResult", "JournalAccept"];

    private readonly ExcelSheet<Item> items;
    private readonly ExcelSheet<ClassJobCategory> categories;
    private readonly ExcelSheet<ClassJob> jobs;
    private readonly ExcelSheet<BaseParam> baseParams;
    private readonly Configuration config;
    // Optional reward lists of every quest, and which of them each item appears in.
    private readonly List<List<(uint Item, uint Count)>> rewardLists = [];
    private readonly Dictionary<uint, List<int>> byItem = [];
    private string? cachedKey;

    // What the last open reward window looked like, for the log.
    public string? Diagnostics { get; private set; }
    private List<string> lines = [];

    public RewardHint(IDataManager data, Configuration config)
    {
        items = data.GetExcelSheet<Item>();
        categories = data.GetExcelSheet<ClassJobCategory>();
        jobs = data.GetExcelSheet<ClassJob>();
        baseParams = data.GetExcelSheet<BaseParam>();
        this.config = config;
        foreach (var quest in data.GetExcelSheet<Quest>())
        {
            var list = new List<(uint, uint)>();
            for (var i = 0; i < quest.OptionalItemReward.Count; i++)
            {
                var id = quest.OptionalItemReward[i].RowId;
                if (id != 0)
                    list.Add((id, i < quest.OptionalItemCountReward.Count ? Math.Max(1u, quest.OptionalItemCountReward[i]) : 1u));
            }
            if (list.Count < 2)
                continue;
            foreach (var (id, _) in list)
            {
                if (!byItem.TryGetValue(id, out var owners))
                    byItem[id] = owners = [];
                owners.Add(rewardLists.Count);
            }
            rewardLists.Add(list);
        }
    }

    public unsafe void Draw(IPlayerCharacter? player, QuestTargets quests)
    {
        AtkUnitBase* addon = null;
        foreach (var name in Windows)
        {
            var candidate = Plugin.GameGui.GetAddonByName<AtkUnitBase>(name);
            if (candidate != null && candidate->IsVisible)
                addon = candidate;
        }
        if (player == null || addon == null)
        {
            cachedKey = null;
            return;
        }

        var values = new HashSet<uint>();
        foreach (var value in addon->AtkValuesSpan)
        {
            if (value.Type is AtkValueType.UInt or AtkValueType.Int)
                values.Add(value.UInt);
        }

        // The reward list with the most of its items among the window's values.
        var votes = new Dictionary<int, int>();
        foreach (var value in values)
        {
            // Ids below 20 are gil and crystals, which also stand for counts and flags among the values.
            if (value < 20 || !byItem.TryGetValue(value, out var owners))
                continue;
            foreach (var owner in owners)
                votes[owner] = votes.GetValueOrDefault(owner) + 1;
        }
        Diagnostics = $"reward window: {values.Count} values, {votes.Count} reward lists matched";
        if (votes.Count == 0)
            return;
        // Every option of the right list is in the window: prefer complete matches, then the longest list.
        var best = votes.OrderByDescending(v => v.Value == rewardLists[v.Key].Count).ThenByDescending(v => v.Value).First();
        if (best.Value < 2)
            return;
        Diagnostics += $", picked {string.Join(',', rewardLists[best.Key].Select(r => r.Item))}";
        var rewards = rewardLists[best.Key];

        var key = $"{player.ClassJob.RowId}:{string.Join(',', rewards)}";
        if (key != cachedKey)
        {
            cachedKey = key;
            lines = Evaluate(player, rewards);
        }
        if (lines.Count == 0)
            return;

        // Above the window's top-left corner.
        var origin = new Vector2(addon->X, addon->Y);
        var drawList = ImGui.GetForegroundDrawList();
        var height = 0f;
        var width = 0f;
        foreach (var line in lines)
        {
            var size = ImGui.CalcTextSize(line);
            height += size.Y + 2f;
            width = MathF.Max(width, size.X);
        }
        var min = origin - new Vector2(0, height + 14f);
        var max = min + new Vector2(width + 16f, height + 8f);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.05f, 0.07f, 0.09f, 0.85f)), 6f);
        drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.4f, 0.5f)), 6f);
        var y = min.Y + 4f;
        foreach (var line in lines)
        {
            drawList.AddText(new Vector2(min.X + 8f, y), ImGui.GetColorU32(new Vector4(1f, 0.93f, 0.7f, 1f)), line);
            y += ImGui.CalcTextSize(line).Y + 2f;
        }
    }

    private List<string> Evaluate(IPlayerCharacter player, List<(uint Item, uint Count)> rewards)
    {
        var result = new List<string>();
        string? valuable = null;
        var bestPrice = 0u;
        foreach (var (id, count) in rewards)
        {
            if (!items.TryGetRow(id, out var item))
                continue;
            var price = item.PriceLow * count;
            if (price > bestPrice)
            {
                bestPrice = price;
                valuable = Loc.T("reward.sell", item.Name.ExtractText(), price);
            }
        }

        var job = player.ClassJob.ValueNullable;
        var statName = job is { } j && baseParams.TryGetRow(j.PrimaryStat, out var stat) ? stat.Name.ExtractText() : "";
        if (Pick(player, rewards.ConvertAll(r => r.Item)) is { } pick)
        {
            result.Add(pick.MainStat > 0
                ? Loc.T("reward.upgrade-stat", pick.Name, pick.From, pick.To, statName, pick.MainStat)
                : pick.OthersHaveStat
                    ? Loc.T("reward.upgrade", pick.Name, pick.From, pick.To)
                    : Loc.T("reward.no-stat", statName, pick.Name, pick.From, pick.To));
        }
        else
        {
            result.Add(Loc.T("reward.no-upgrade", job?.Name.ExtractText() ?? ""));
        }
        if (valuable != null)
            result.Add(valuable);
        return result;
    }

    private readonly record struct Choice(string Name, int From, int To, int MainStat, bool OthersHaveStat);

    // Among items the current class can wear now: those with its main stat (Intelligence for a black mage) first,
    // then the biggest item-level gain over what is worn in that slot. Null when nothing is an improvement.
    private Choice? Pick(IPlayerCharacter player, IEnumerable<uint> ids)
    {
        var job = player.ClassJob.ValueNullable;
        var abbreviation = job?.Abbreviation.ExtractText() ?? "";
        var primary = job?.PrimaryStat ?? 0;
        var equipped = EquippedLevels();
        var candidates = new List<(string Name, int From, int To, int Stat)>();
        foreach (var id in ids)
        {
            if (!items.TryGetRow(id, out var item))
                continue;
            var slot = item.EquipSlotCategory.RowId;
            if (slot == 0 || item.LevelEquip > player.Level || !UsableBy(item.ClassJobCategory.RowId, abbreviation))
                continue;
            var stat = 0;
            for (var i = 0; i < item.BaseParam.Count; i++)
            {
                if (item.BaseParam[i].RowId == primary && primary != 0)
                    stat += item.BaseParamValue[i];
            }
            var level = (int)item.LevelItem.RowId;
            var current = equipped.TryGetValue(slot, out var worn) ? worn : 0;
            if (level > current || stat > 0)
                candidates.Add((item.Name.ExtractText(), current, level, stat));
        }
        if (candidates.Count == 0)
            return null;
        var best = candidates.OrderByDescending(c => c.Stat > 0).ThenByDescending(c => c.To - c.From).ThenByDescending(c => c.Stat).First();
        return new Choice(best.Name, best.From, best.To, best.Stat, candidates.Exists(c => c.Stat > 0));
    }

    // The best item for the current class among these, for the quests-nearby window.
    internal (string Name, int From, int To)? BestUpgrade(IPlayerCharacter player, IEnumerable<uint> ids) =>
        Pick(player, ids) is { } pick && (pick.MainStat > 0 || pick.To > pick.From) ? (pick.Name, pick.From, pick.To) : null;

    // ClassJobCategory has one boolean column per class/job abbreviation (THM, BLM, ...).
    internal bool UsableBy(uint category, string abbreviation)
    {
        if (abbreviation.Length == 0 || !categories.TryGetRow(category, out var row))
            return false;
        var property = typeof(ClassJobCategory).GetProperty(abbreviation);
        return property?.GetValue(row) is true;
    }

    // Item level worn per equip slot category.
    private unsafe Dictionary<uint, int> EquippedLevels()
    {
        var result = new Dictionary<uint, int>();
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return result;
        var container = inventory->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null)
            return result;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0 || !items.TryGetRow(slot->ItemId, out var item))
                continue;
            var category = item.EquipSlotCategory.RowId;
            var level = (int)item.LevelItem.RowId;
            if (!result.TryGetValue(category, out var existing) || level < existing)
                result[category] = level;
        }
        return result;
    }
}
