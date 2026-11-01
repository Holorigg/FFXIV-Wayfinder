using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Wayfinder;

// While the quest reward window (JournalResult) is open: which optional reward is an upgrade for the current
// class, and which sells for the most. The quest is recognised by its reward item ids appearing in the window's
// values; nothing is clicked or selected.
internal sealed class RewardHint
{
    private readonly ExcelSheet<Item> items;
    private readonly ExcelSheet<ClassJobCategory> categories;
    private readonly Configuration config;
    private string? cachedKey;
    private List<string> lines = [];

    public RewardHint(IDataManager data, Configuration config)
    {
        items = data.GetExcelSheet<Item>();
        categories = data.GetExcelSheet<ClassJobCategory>();
        this.config = config;
    }

    public unsafe void Draw(IPlayerCharacter? player, QuestTargets quests)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>("JournalResult");
        if (player == null || addon == null || !addon->IsVisible)
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

        List<(uint Item, uint Count)>? rewards = null;
        foreach (var quest in quests.List())
        {
            var options = quests.OptionalRewards(quest);
            if (options.Count > 1 && options.Exists(o => values.Contains(o.Item)))
            {
                rewards = options;
                break;
            }
        }
        if (rewards == null)
            return;

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

    private unsafe List<string> Evaluate(IPlayerCharacter player, List<(uint Item, uint Count)> rewards)
    {
        var result = new List<string>();
        var job = player.ClassJob.ValueNullable;
        var abbreviation = job?.Abbreviation.ExtractText() ?? "";
        var equipped = EquippedLevels();

        string? upgrade = null;
        var bestGain = 0;
        string? valuable = null;
        var bestPrice = 0u;
        foreach (var (id, count) in rewards)
        {
            if (!items.TryGetRow(id, out var item))
                continue;
            var name = item.Name.ExtractText();
            var price = item.PriceLow * count;
            if (price > bestPrice)
            {
                bestPrice = price;
                valuable = Loc.T("reward.sell", name, price);
            }

            var slot = item.EquipSlotCategory.RowId;
            if (slot == 0 || item.LevelEquip > player.Level || !UsableBy(item.ClassJobCategory.RowId, abbreviation))
                continue;
            var level = (int)item.LevelItem.RowId;
            var current = equipped.TryGetValue(slot, out var worn) ? worn : 0;
            if (level - current > bestGain)
            {
                bestGain = level - current;
                upgrade = Loc.T("reward.upgrade", name, current, level);
            }
        }

        if (upgrade != null)
            result.Add(upgrade);
        else
            result.Add(Loc.T("reward.no-upgrade", job?.Name.ExtractText() ?? ""));
        if (valuable != null)
            result.Add(valuable);
        return result;
    }

    // The biggest item-level gain over what is worn, among items the current class can equip now.
    internal (string Name, int From, int To)? BestUpgrade(IPlayerCharacter player, IEnumerable<uint> ids)
    {
        var abbreviation = player.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "";
        var equipped = EquippedLevels();
        (string, int, int)? best = null;
        var bestGain = 0;
        foreach (var id in ids)
        {
            if (!items.TryGetRow(id, out var item))
                continue;
            var slot = item.EquipSlotCategory.RowId;
            if (slot == 0 || item.LevelEquip > player.Level || !UsableBy(item.ClassJobCategory.RowId, abbreviation))
                continue;
            var level = (int)item.LevelItem.RowId;
            var current = equipped.TryGetValue(slot, out var worn) ? worn : 0;
            if (level - current > bestGain)
            {
                bestGain = level - current;
                best = (item.Name.ExtractText(), current, level);
            }
        }
        return best;
    }

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
