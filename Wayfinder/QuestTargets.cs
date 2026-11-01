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
// Objective = index of the step's todo entry this spot belongs to (several todos = several objectives in the tracker).
internal readonly record struct QuestLocation(uint Territory, Vector3 Position, bool Area = false, int Objective = 0, uint Object = 0);

// Quest objectives come from the Quest sheet: each TodoParams entry lists the Level rows (world positions)
// that belong to the step whose sequence equals ToDoCompleteSeq. A quest not yet accepted points at its issuer.
internal sealed class QuestTargets
{
    private const uint QuestRowOffset = 0x10000;
    private readonly ExcelSheet<Quest> quests;
    private readonly IDataManager data;

    public QuestTargets(IDataManager data)
    {
        this.data = data;
        quests = data.GetExcelSheet<Quest>();
    }

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
        var objective = 0;
        foreach (var todo in row.TodoParams)
        {
            if (todo.ToDoCompleteSeq != sequence)
                continue;
            foreach (var level in todo.ToDoLocation)
                AddLevel(result, level, objective);
            objective++;
        }
        return result;
    }

    // A step with several objectives (separate todo entries) ticks them off in the quest's 4th work variable, one
    // bit per objective from the top: 0x80 the first, 0x40 the second... "A Hero in the Making" with Gridania and
    // Ul'dah done read C0, matching the two checkmarks in the tracker.
    public static unsafe bool ObjectiveDone(ushort questId, int objective)
    {
        var manager = QuestManager.Instance();
        if (manager == null || objective is < 0 or > 7)
            return false;
        foreach (ref var work in manager->NormalQuests)
        {
            if (work.QuestId == questId)
                return (work.Variables[3] & (0x80 >> objective)) != 0;
        }
        return false;
    }

    // The quest's work variables as hex, for the log: multi-spot steps keep their progress in them.
    public static unsafe string Variables(ushort questId)
    {
        var manager = QuestManager.Instance();
        if (manager == null)
            return "";
        foreach (ref var work in manager->NormalQuests)
        {
            if (work.QuestId == questId)
                return $"{Convert.ToHexString(work.Variables)} flags {work.Flags:X2}";
        }
        return "";
    }

    public static unsafe byte Sequence(QuestEntry quest) =>
        quest.Accepted ? QuestManager.GetQuestSequence(quest.QuestId) : (byte)0;

    private static void AddLevel(List<QuestLocation> result, RowRef<Level> level, int objective = 0)
    {
        if (level.RowId == 0 || level.ValueNullable is not { } value || value.Territory.RowId == 0)
            return;
        var position = new Vector3(value.X, value.Y, value.Z);
        // Several objectives of one step can share a spot ("speak to X" three times); that is one place to go.
        if (result.Exists(l => l.Territory == value.Territory.RowId && Vector3.Distance(l.Position, position) < 2f))
            return;
        result.Add(new QuestLocation(value.Territory.RowId, position, value.Type == AreaLevelType, objective,
            value.Type is 8 or 45 ? value.Object.RowId : 0));
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

    private readonly Dictionary<ushort, Dictionary<int, string>> todoTexts = [];

    // The tracker line of every todo entry ("Slay a gall gnat."), keyed by the todo's index in the quest. The text
    // lives in the quest's own text sheet (quest/<3 digits>/<Id>) under TEXT_<ID>_TODO_<nn>.
    public Dictionary<int, string> TodoTexts(ushort questId)
    {
        if (todoTexts.TryGetValue(questId, out var cached))
            return cached;
        var result = new Dictionary<int, string>();
        todoTexts[questId] = result;
        if (!quests.TryGetRow(QuestRowOffset + questId, out var row))
            return result;
        var id = row.Id.ExtractText();
        var underscore = id.IndexOf('_');
        if (underscore < 0 || id.Length < underscore + 4)
            return result;
        try
        {
            var sheet = data.GetExcelSheet<RawRow>(name: $"quest/{id.Substring(underscore + 1, 3)}/{id}");
            foreach (var line in sheet)
            {
                var key = line.ReadStringColumn(0).ExtractText();
                var marker = key.LastIndexOf("_TODO_", StringComparison.Ordinal);
                if (marker < 0 || !int.TryParse(key[(marker + 6)..], out var index))
                    continue;
                var text = line.ReadStringColumn(1).ExtractText();
                if (text.Length > 0)
                    result[index] = text;
            }
        }
        catch (Exception)
        {
            // Some quests have no text sheet in this client; objective names are then unknown.
        }
        return result;
    }

    // The global todo indices of the current step, in order: QuestLocation.Objective counts within the step,
    // this maps it back to the todo entry (and so to its text).
    public unsafe List<int> StepTodos(QuestEntry quest)
    {
        var result = new List<int>();
        if (!quests.TryGetRow(QuestRowOffset + quest.QuestId, out var row) || !quest.Accepted)
            return result;
        var sequence = QuestManager.GetQuestSequence(quest.QuestId);
        for (var i = 0; i < row.TodoParams.Count; i++)
        {
            if (row.TodoParams[i].ToDoCompleteSeq == sequence)
                result.Add(i);
        }
        return result;
    }

    // Whether an objective line names this thing: "Slay a gall gnat." / "Obtain brownie brushes." against the
    // singular names the game gives objects and monsters.
    public static bool Mentions(string text, string name)
    {
        if (name.Length < 3)
            return false;
        var t = text.ToLowerInvariant();
        var n = name.ToLowerInvariant();
        if (t.Contains(n, StringComparison.Ordinal) || t.Contains(n + "s", StringComparison.Ordinal) || t.Contains(n + "es", StringComparison.Ordinal))
            return true;
        return n.EndsWith('y') && t.Contains(n[..^1] + "ies", StringComparison.Ordinal);
    }

    private readonly Dictionary<ushort, List<(uint Object, string Name, uint Territory, Vector3 Position)>> objectSpots = [];

    // Where the quest's event objects (EOBJECTn: "brownie brush") are placed, from Level rows of Type 45.
    public List<(uint Object, string Name, uint Territory, Vector3 Position)> ObjectSpots(QuestEntry quest)
    {
        if (objectSpots.TryGetValue(quest.QuestId, out var cached))
            return cached;
        var result = new List<(uint, string, uint, Vector3)>();
        objectSpots[quest.QuestId] = result;
        if (!quests.TryGetRow(QuestRowOffset + quest.QuestId, out var row))
            return result;
        var names = data.GetExcelSheet<EObjName>();
        var wanted = new Dictionary<uint, string>();
        foreach (var param in row.QuestParams)
        {
            if (param.ScriptArg != 0 && param.ScriptInstruction.ExtractText().StartsWith("EOBJECT", StringComparison.Ordinal)
                && names.TryGetRow(param.ScriptArg, out var name))
                wanted[param.ScriptArg] = name.Singular.ExtractText();
        }
        if (wanted.Count == 0)
            return result;
        foreach (var level in data.GetExcelSheet<Level>())
        {
            if (level.Type == 45 && wanted.TryGetValue(level.Object.RowId, out var name))
                result.Add((level.Object.RowId, name, level.Territory.RowId, new Vector3(level.X, level.Y, level.Z)));
        }
        return result;
    }

    // Monsters the quest asks for (ENEMYn): small values are BNpcBase rows, matched against a battle NPC's base id.
    // Large values are layout instance ids of scripted fights and are left out.
    public HashSet<uint> Enemies(QuestEntry quest)
    {
        var result = new HashSet<uint>();
        if (!quests.TryGetRow(QuestRowOffset + quest.QuestId, out var row))
            return result;
        foreach (var param in row.QuestParams)
        {
            if (param.ScriptArg is > 0 and < 1_000_000 && param.ScriptInstruction.ExtractText().StartsWith("ENEMY", StringComparison.Ordinal))
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
