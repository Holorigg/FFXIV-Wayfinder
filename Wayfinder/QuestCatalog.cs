using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Wayfinder;

internal sealed record CatalogQuest(
    ushort Id, string Name, int Level, string Genre, uint Territory, string Zone, Vector3 Position, float MapX, float MapY,
    string Issuer, uint ClassCategory, uint[] Previous, bool PreviousAny, uint[] Locks, uint GrandCompany,
    uint Gil, int ExpFactor, List<(uint Item, uint Count)> Items, List<(uint Item, uint Count)> Choice, string? Unlocks);

// Every one-off quest with an issuer on the map, for "quests nearby": where to pick it up and what it pays.
// Availability is decided from the player's state at the time of asking (level, class, finished prerequisites,
// Grand Company); repeatable, seasonal, tribe and main scenario quests are left out.
internal sealed class QuestCatalog
{
    private const uint QuestRowOffset = 0x10000;
    // EventIconType of main scenario quests; they are followed by the quest mode already.
    private const uint MainScenarioIcon = 3;

    private readonly List<CatalogQuest> quests = [];
    private readonly Lumina.Excel.ExcelSheet<ParamGrow> grow;
    private readonly Lumina.Excel.ExcelSheet<Item> items;

    public QuestCatalog(IDataManager data)
    {
        grow = data.GetExcelSheet<ParamGrow>();
        items = data.GetExcelSheet<Item>();
        var residents = data.GetExcelSheet<ENpcResident>();
        var territories = data.GetExcelSheet<TerritoryType>();
        foreach (var q in data.GetExcelSheet<Quest>())
        {
            var name = q.Name.ExtractText();
            if (name.Length == 0 || q.IsRepeatable || q.Festival.RowId != 0 || q.BeastTribe.RowId != 0
                || q.EventIconType.RowId == MainScenarioIcon || q.IssuerLocation.ValueNullable is not { } level || level.Territory.RowId == 0)
                continue;
            if (!territories.TryGetRow(level.Territory.RowId, out var zone) || zone.Map.ValueNullable is not { } map)
                continue;
            var scale = map.SizeFactor / 100f;
            float Coordinate(float world, float offset) => 41f / scale * ((world + offset) * scale + 1024f) / 2048f + 1f;

            var fixedItems = new List<(uint, uint)>();
            for (var i = 0; i < q.Reward.Count; i++)
            {
                var id = q.Reward[i].RowId;
                if (id != 0 && items.HasRow(id))
                    fixedItems.Add((id, i < q.ItemCountReward.Count ? Math.Max(1u, q.ItemCountReward[i]) : 1u));
            }
            var choice = new List<(uint, uint)>();
            for (var i = 0; i < q.OptionalItemReward.Count; i++)
            {
                var id = q.OptionalItemReward[i].RowId;
                if (id != 0)
                    choice.Add((id, i < q.OptionalItemCountReward.Count ? Math.Max(1u, q.OptionalItemCountReward[i]) : 1u));
            }

            string? unlocks = null;
            if (q.InstanceContentUnlock.RowId != 0)
                unlocks = "sides.unlock-duty";
            else if (q.ActionReward.RowId != 0 || q.GeneralActionReward.Count > 0 && q.GeneralActionReward[0].RowId != 0)
                unlocks = "sides.unlock-action";
            else if (q.EmoteReward.RowId != 0)
                unlocks = "sides.unlock-emote";

            var previous = new List<uint>();
            foreach (var p in q.PreviousQuest)
            {
                if (p.RowId != 0)
                    previous.Add(p.RowId);
            }
            var locks = new List<uint>();
            foreach (var l in q.QuestLock)
            {
                if (l.RowId != 0)
                    locks.Add(l.RowId);
            }

            var issuer = residents.TryGetRow(q.IssuerStart.RowId, out var npc) ? npc.Singular.ExtractText() : "";
            if (issuer.Length > 0)
                issuer = char.ToUpperInvariant(issuer[0]) + issuer[1..];
            quests.Add(new CatalogQuest(
                (ushort)(q.RowId - QuestRowOffset), name, q.ClassJobLevel[0], q.JournalGenre.ValueNullable?.Name.ExtractText() ?? "",
                level.Territory.RowId, zone.PlaceName.ValueNullable?.Name.ExtractText() ?? "", new Vector3(level.X, level.Y, level.Z),
                Coordinate(level.X, map.OffsetX), Coordinate(level.Z, map.OffsetY), issuer, q.ClassJobCategory0.RowId,
                [.. previous], q.PreviousQuestJoin == 2, [.. locks], q.GrandCompany.RowId, q.GilReward, q.ExpFactor,
                fixedItems, choice, unlocks));
        }
    }

    public int Count => quests.Count;

    public CatalogQuest? Find(ushort id) => quests.Find(q => q.Id == id);

    public string ItemName(uint id) => items.TryGetRow(id, out var item) ? item.Name.ExtractText() : $"#{id}";

    // Quest experience: (45 + 5 x level) x ExpFactor x ParamGrow.QuestExpModifier / 100. Matches the journal for
    // A Realm Reborn levels; later expansions add bonuses on top, so the window shows it as approximate.
    public int Experience(CatalogQuest quest)
    {
        if (!grow.TryGetRow((uint)Math.Max(1, quest.Level), out var row))
            return 0;
        return (45 + 5 * quest.Level) * quest.ExpFactor * row.QuestExpModifier / 100;
    }

    // Quests the player can pick up now in the given zones.
    public unsafe List<CatalogQuest> Available(IPlayerCharacter player, ISet<uint> zones, RewardHint classCheck)
    {
        var result = new List<CatalogQuest>();
        var manager = QuestManager.Instance();
        var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (manager == null || state == null)
            return result;
        var job = player.ClassJob.ValueNullable;
        var abbreviation = job?.Abbreviation.ExtractText() ?? "";
        // A job also counts as its base class (class quests and gear for THM are open to a black mage).
        var parent = job?.ClassJobParent.ValueNullable?.Abbreviation.ExtractText() ?? abbreviation;

        foreach (var quest in quests)
        {
            if (!zones.Contains(quest.Territory) || quest.Level > player.Level)
                continue;
            if (QuestManager.IsQuestComplete(quest.Id) || manager->IsQuestAccepted(quest.Id))
                continue;
            if (quest.ClassCategory > 1 && !classCheck.UsableBy(quest.ClassCategory, abbreviation) && !classCheck.UsableBy(quest.ClassCategory, parent))
                continue;
            if (quest.GrandCompany != 0 && quest.GrandCompany != state->GrandCompany)
                continue;
            if (quest.Previous.Length > 0)
            {
                var done = 0;
                foreach (var p in quest.Previous)
                {
                    if (QuestManager.IsQuestComplete((ushort)(p & 0xFFFF)))
                        done++;
                }
                if (quest.PreviousAny ? done == 0 : done < quest.Previous.Length)
                    continue;
            }
            var locked = false;
            foreach (var l in quest.Locks)
            {
                if (QuestManager.IsQuestComplete((ushort)(l & 0xFFFF)) || manager->IsQuestAccepted((ushort)(l & 0xFFFF)))
                    locked = true;
            }
            if (!locked)
                result.Add(quest);
        }
        return result;
    }
}
