using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Wayfinder;

// /way mapdebug: writes the node tree of the minimap and the area map to the log, to calibrate the overlays.
internal static unsafe class MapDebug
{
    public static void Dump(Action<string> log)
    {
        foreach (var name in new[] { "_ToDoList" })
        {
            var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon == null)
            {
                log($"{name}: not loaded");
                continue;
            }
            log($"{name}: visible {addon->IsVisible} pos {addon->X},{addon->Y} scale {addon->Scale} nodes {addon->UldManager.NodeListCount}");
            DumpList(log, addon->UldManager, "  ", 0);
        }
        var todo = Plugin.GameGui.GetAddonByName<AtkUnitBase>("_ToDoList");
        if (todo != null)
        {
            log($"_ToDoList: {todo->AtkValuesCount} values");
            for (var i = 0; i < todo->AtkValuesCount; i++)
            {
                var value = todo->AtkValues[i];
                var text = value.Type switch
                {
                    AtkValueType.Int => value.Int.ToString(),
                    AtkValueType.UInt => value.UInt.ToString(),
                    AtkValueType.Bool => value.Byte.ToString(),
                    AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8 => value.String.ToString() ?? "",
                    _ => $"({value.Type})",
                };
                if (text.Length > 0 && text != "0")
                    log($"  [{i}] {value.Type}: {text}");
            }
        }
        // The tracker's lines live in string array 25 (found with a full scan that crashed: only touch arrays that
        // exist, within their size). Number arrays around it are dumped to find the checkmarks.
        var stage = AtkStage.Instance();
        if (stage != null && stage->AtkArrayDataHolder != null)
        {
            var holder = stage->AtkArrayDataHolder;
            if (holder->StringArrayCount > 25 && holder->StringArrays[25] != null)
            {
                var strings = holder->StringArrays[25];
                for (var i = 0; i < Math.Min(strings->AtkArrayData.Size, 40); i++)
                    log($"string array 25 [{i}] {strings->StringArray[i]}");
            }
            for (var a = 22; a <= 27 && a < holder->NumberArrayCount; a++)
            {
                var array = holder->NumberArrays[a];
                if (array == null)
                    continue;
                var parts = new System.Collections.Generic.List<string>();
                for (var i = 0; i < Math.Min(array->AtkArrayData.Size, 120); i++)
                    parts.Add(array->IntArray[i].ToString());
                log($"number array {a}: {string.Join(",", parts)}");
            }
        }
        var agent = AgentMap.Instance();
        if (agent != null)
            log($"AgentMap: current map {agent->CurrentMapId} territory {agent->CurrentTerritoryId}, selected map {agent->SelectedMapId} size {agent->SelectedMapSizeFactor} offset {agent->SelectedOffsetX},{agent->SelectedOffsetY}, current size {agent->CurrentMapSizeFactor} offset {agent->CurrentOffsetX},{agent->CurrentOffsetY}");
    }

    private static void DumpList(Action<string> log, AtkUldManager manager, string indent, int depth)
    {
        for (var i = 0; i < manager.NodeListCount; i++)
        {
            var node = manager.NodeList[i];
            if (node == null)
                continue;
            log($"{indent}[{i}] id {node->NodeId} type {node->Type} vis {node->IsVisible()} screen {node->ScreenX:0.#},{node->ScreenY:0.#} pos {node->X:0.#},{node->Y:0.#} size {node->Width}x{node->Height} scale {node->ScaleX:0.###} rot {node->Rotation:0.###} origin {node->OriginX:0.#},{node->OriginY:0.#}");
            if (node->Type == NodeType.Text)
                log($"{indent}    text \"{((AtkTextNode*)node)->NodeText}\" colour {((AtkTextNode*)node)->TextColor.R},{((AtkTextNode*)node)->TextColor.G},{((AtkTextNode*)node)->TextColor.B},{((AtkTextNode*)node)->TextColor.A}");
            if (node->Type == NodeType.Image)
                log($"{indent}    image part {((AtkImageNode*)node)->PartId}");
            if (depth < 4 && (int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    DumpList(log, component->UldManager, indent + "    ", depth + 1);
            }
        }
    }
}
