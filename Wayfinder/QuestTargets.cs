using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Wayfinder;

internal readonly record struct QuestEntry(ushort QuestId, string Name, bool Accepted, bool MainScenario);

// Area = the Level row is a search area (Type 51, "speak to three people around here"), not a placed NPC or object.
internal readonly record struct QuestLocation(uint Territory, Vector3 Position, bool Area = false);

// Quest objectives come from the Quest sheet: each TodoParams entry lists the Level rows (world positions)
// that belong to the step whose sequence equals ToDoCompleteSeq. A quest not yet accepted points at its issuer.
internal sealed class QuestTargets
{
    private const uint QuestRowOffset = 0x10000;
    private readonly ExcelSheet<Quest> quests;

    public QuestTargets(IDataManager data) => quests = data.GetExcelSheet<Quest>();

    public unsafe List<QuestEntry> List()
    {
        var result = new List<QuestEntry>();
        var manager = QuestManager.Instance();
        if (manager == null)
            return result;

        var msq = MainScenarioQuest();
        if (msq != 0 && !QuestManager.IsQuestComplete(msq))
            result.Add(new QuestEntry(msq, Name(msq), manager->IsQuestAccepted(msq), true));

        foreach (ref var work in manager->NormalQuests)
        {
            if (work.QuestId == 0 || work.IsHidden || work.QuestId == msq)
                continue;
            result.Add(new QuestEntry(work.QuestId, Name(work.QuestId), true, false));
        }
        return result;
    }

    public unsafe List<QuestLocation> Locations(QuestEntry quest)
    {
        var result = new List<QuestLocation>();
        if (!quests.TryGetRow(QuestRowOffset + quest.QuestId, out var row))
            return result;

        if (!quest.Accepted)
        {
            AddLevel(result, row.IssuerLocation);
            return result;
        }

        var sequence = QuestManager.GetQuestSequence(quest.QuestId);
        foreach (var todo in row.TodoParams)
        {
            if (todo.ToDoCompleteSeq != sequence)
                continue;
            foreach (var level in todo.ToDoLocation)
                AddLevel(result, level);
        }
        return result;
    }

    public static unsafe byte Sequence(QuestEntry quest) =>
        quest.Accepted ? QuestManager.GetQuestSequence(quest.QuestId) : (byte)0;

    private static void AddLevel(List<QuestLocation> result, RowRef<Level> level)
    {
        if (level.RowId == 0 || level.ValueNullable is not { } value || value.Territory.RowId == 0)
            return;
        var position = new Vector3(value.X, value.Y, value.Z);
        // Several objectives of one step can share a spot ("speak to X" three times); that is one place to go.
        if (result.Exists(l => l.Territory == value.Territory.RowId && Vector3.Distance(l.Position, position) < 2f))
            return;
        result.Add(new QuestLocation(value.Territory.RowId, position, value.Type == AreaLevelType));
    }

    private const byte AreaLevelType = 51;

    // Every NPC and event object the quest script names (ACTORn, EOBJECTn). Inside a search area the ones the
    // player still has to deal with carry a quest marker over their heads.
    public HashSet<uint> Actors(QuestEntry quest)
    {
        var result = new HashSet<uint>();
        if (!quests.TryGetRow(QuestRowOffset + quest.QuestId, out var row))
            return result;
        foreach (var param in row.QuestParams)
        {
            var instruction = param.ScriptInstruction.ExtractText();
            if (param.ScriptArg != 0 && (instruction.StartsWith("ACTOR", StringComparison.Ordinal) || instruction.StartsWith("EOBJECT", StringComparison.Ordinal)))
                result.Add(param.ScriptArg);
        }
        return result;
    }

    public string NameOfRow(uint row) => Name((ushort)(row & 0xFFFF));

    // The "choose one" rewards of a quest: item row and how many.
    public List<(uint Item, uint Count)> OptionalRewards(QuestEntry quest)
    {
        var result = new List<(uint, uint)>();
        if (!quests.TryGetRow(QuestRowOffset + quest.QuestId, out var row))
            return result;
        for (var i = 0; i < row.OptionalItemReward.Count; i++)
        {
            var item = row.OptionalItemReward[i].RowId;
            if (item != 0)
                result.Add((item, i < row.OptionalItemCountReward.Count ? Math.Max(1u, row.OptionalItemCountReward[i]) : 1u));
        }
        return result;
    }

    private string Name(ushort questId) =>
        quests.TryGetRow(QuestRowOffset + questId, out var row) ? row.Name.ExtractText() : $"Quest {questId}";

    private static unsafe ushort MainScenarioQuest()
    {
        var agent = AgentScenarioTree.Instance();
        if (agent == null || agent->Data == null)
            return 0;
        var ids = agent->Data->MainScenarioQuestIds;
        if (ids.Length == 0)
            return 0;
        var index = agent->Data->MSQPathIndex < ids.Length ? agent->Data->MSQPathIndex : 0;
        var id = ids[index] != 0 ? ids[index] : ids[0];
        return (ushort)id;
    }
}
