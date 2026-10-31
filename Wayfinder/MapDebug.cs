using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Wayfinder;

// /way mapdebug: writes the node tree of the minimap and the area map to the log, to calibrate the overlays.
internal static unsafe class MapDebug
{
    public static void Dump(Action<string> log)
    {
        foreach (var name in new[] { "_NaviMap", "AreaMap" })
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
            if (depth < 2 && (int)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    DumpList(log, component->UldManager, indent + "    ", depth + 1);
            }
        }
    }
}
