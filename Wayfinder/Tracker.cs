using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Wayfinder;

// Reads the quest tracker on the right of the screen (_ToDoList). The game keeps objective progress in per-quest
// script variables with no common layout, but the tracker shows it for every quest: each objective line is a
// component whose text node (id 6) holds the line ("Obtain brownie brushes. 2/3") and whose image node (id 8)
// shows part 1, a checkmark, once the objective is done, or part 2, an arrow, while it is open.
internal static unsafe class Tracker
{
    private const uint TextNodeId = 6;
    private const uint IconNodeId = 8;
    private const ushort CheckmarkPart = 1;

    // Objective lines currently in the tracker: line text (without a trailing "2/3" count) -> done.
    public static Dictionary<string, bool> Lines()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>("_ToDoList");
        if (addon == null || !addon->IsVisible)
            return result;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->IsVisible())
                continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null)
                continue;
            AtkTextNode* text = null;
            AtkImageNode* icon = null;
            for (var j = 0; j < component->UldManager.NodeListCount; j++)
            {
                var child = component->UldManager.NodeList[j];
                if (child == null)
                    continue;
                if (child->NodeId == TextNodeId && child->Type == NodeType.Text)
                    text = (AtkTextNode*)child;
                else if (child->NodeId == IconNodeId && child->Type == NodeType.Image)
                    icon = (AtkImageNode*)child;
            }
            if (text == null || icon == null || !icon->AtkResNode.IsVisible())
                continue;
            var line = Key(text->NodeText.ToString());
            if (line.Length > 0)
                result[line] = icon->PartId == CheckmarkPart;
        }
        return result;
    }

    // Whether the tracker lists this objective and, if so, whether it is ticked.
    public static bool? Done(Dictionary<string, bool> lines, string objective)
    {
        return lines.TryGetValue(Key(objective), out var done) ? done : null;
    }

    // Long lines come wrapped in the tracker ("...planar fissure at the" / "Zanr'ak encampment."), with line-break
    // codes in the node text, so lines are compared by their letters and digits only.
    private static string Key(string line)
    {
        line = StripCount(line);
        var key = new System.Text.StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (char.IsLetterOrDigit(c))
                key.Append(char.ToLowerInvariant(c));
        }
        return key.ToString();
    }

    private static string StripCount(string line)
    {
        line = line.Trim();
        var space = line.LastIndexOf(' ');
        // "Obtain brownie brushes. 2/3" -> "Obtain brownie brushes."
        if (space > 0 && line[(space + 1)..].Contains('/') && char.IsDigit(line[^1]))
            line = line[..space].TrimEnd();
        return line;
    }
}
