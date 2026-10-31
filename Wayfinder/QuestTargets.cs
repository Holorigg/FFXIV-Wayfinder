using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Wayfinder;

internal readonly record struct QuestEntry(ushort QuestId, string Name, bool Accepted, bool MainScenario);

internal readonly record struct QuestLocation(uint Territory, Vector3 Position);

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
        result.Add(new QuestLocation(value.Territory.RowId, new Vector3(value.X, value.Y, value.Z)));
    }

    public string NameOfRow(uint row) => Name((ushort)(row & 0xFFFF));

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
