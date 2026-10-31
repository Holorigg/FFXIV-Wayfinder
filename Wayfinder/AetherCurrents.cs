using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Wayfinder;

// One aether current: a field one has a spot in the world, a quest one is handed out by finishing QuestId.
internal readonly record struct Current(uint Id, uint Territory, Vector3? Position, uint QuestId);

internal readonly record struct CurrentProgress(int Collected, int Total, List<Current> FieldLeft, List<uint> QuestsLeft);

// Aether currents per zone (AetherCurrentCompFlgSet). Field currents are EObjs whose Data points at the
// AetherCurrent row; their placement is a Level row of Type 45 for that EObj.
internal sealed class AetherCurrents
{
    private readonly Dictionary<uint, List<Current>> byZone = [];

    public AetherCurrents(IDataManager data)
    {
        var objectFor = new Dictionary<uint, uint>();
        var currentRows = data.GetExcelSheet<AetherCurrent>();
        foreach (var obj in data.GetExcelSheet<EObj>())
        {
            if (obj.Data.RowId != 0 && currentRows.HasRow(obj.Data.RowId))
                objectFor[obj.RowId] = obj.Data.RowId;
        }
        var positions = new Dictionary<uint, Vector3>();
        foreach (var level in data.GetExcelSheet<Level>())
        {
            if (level.Type == 45 && objectFor.TryGetValue(level.Object.RowId, out var current))
                positions[current] = new Vector3(level.X, level.Y, level.Z);
        }

        foreach (var set in data.GetExcelSheet<AetherCurrentCompFlgSet>())
        {
            var territory = set.Territory.RowId;
            if (territory == 0)
                continue;
            var list = new List<Current>();
            foreach (var current in set.AetherCurrents)
            {
                if (current.RowId == 0 || current.ValueNullable is not { } row)
                    continue;
                Vector3? position = positions.TryGetValue(current.RowId, out var p) ? p : null;
                list.Add(new Current(current.RowId, territory, position, row.Quest.RowId));
            }
            if (list.Count > 0)
                byZone[territory] = list;
            Count += list.Count;
        }
    }

    public int Count { get; }

    public bool HasCurrents(uint territory) => byZone.ContainsKey(territory);

    public unsafe CurrentProgress? Progress(uint territory)
    {
        var state = PlayerState.Instance();
        if (state == null || !byZone.TryGetValue(territory, out var list))
            return null;
        var collected = 0;
        var field = new List<Current>();
        var quests = new List<uint>();
        foreach (var current in list)
        {
            if (state->IsAetherCurrentUnlocked(current.Id))
            {
                collected++;
                continue;
            }
            if (current.QuestId != 0)
                quests.Add(current.QuestId);
            else if (current.Position != null)
                field.Add(current);
        }
        return new CurrentProgress(collected, list.Count, field, quests);
    }
}
