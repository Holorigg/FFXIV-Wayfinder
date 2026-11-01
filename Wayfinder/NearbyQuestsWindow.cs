using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Wayfinder;

// "Quests nearby": side quests the player can pick up around here, with what they pay, so a useful one can be
// chosen and walked to. Opened from the main window or /way sides.
internal sealed class NearbyQuestsWindow : Window
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);
    private static readonly Vector4 Upgrade = new(0.55f, 0.95f, 0.55f, 1f);
    private static readonly Vector4 Dim = new(0.7f, 0.7f, 0.7f, 1f);
    private static readonly Vector4 Gold = new(1f, 0.85f, 0.45f, 1f);

    private readonly Plugin plugin;
    private List<Plugin.NearbyQuest> rows = [];
    private DateTime nextRefresh;
    private int reach;
    private int sort;
    private bool upgradesOnly;

    public NearbyQuestsWindow(Plugin plugin)
        : base("Wayfinder###WayfinderSides")
    {
        this.plugin = plugin;
        Size = new Vector2(820, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        WindowName = $"{Loc.T("sides.title")}###WayfinderSides";
        ImGui.TextUnformatted(Loc.T("sides.reach"));
        ImGui.SameLine();
        var changed = ImGui.RadioButton(Loc.T("sides.reach0"), ref reach, 0);
        ImGui.SameLine();
        changed |= ImGui.RadioButton(Loc.T("sides.reach1"), ref reach, 1);
        ImGui.SameLine();
        changed |= ImGui.RadioButton(Loc.T("sides.reach2"), ref reach, 2);

        ImGui.TextUnformatted(Loc.T("sides.sort"));
        ImGui.SameLine();
        changed |= ImGui.RadioButton(Loc.T("sides.sort-near"), ref sort, 0);
        ImGui.SameLine();
        changed |= ImGui.RadioButton(Loc.T("sides.sort-exp"), ref sort, 1);
        ImGui.SameLine();
        changed |= ImGui.RadioButton(Loc.T("sides.sort-gil"), ref sort, 2);
        ImGui.SameLine(0, 24);
        changed |= ImGui.Checkbox(Loc.T("sides.upgrades"), ref upgradesOnly);

        if (changed || DateTime.UtcNow >= nextRefresh)
        {
            nextRefresh = DateTime.UtcNow + RefreshEvery;
            rows = plugin.NearbyQuests(reach);
            if (upgradesOnly)
                rows = rows.FindAll(r => r.Upgrade != null);
            rows = sort switch
            {
                1 => [.. rows.OrderByDescending(r => r.Experience)],
                2 => [.. rows.OrderByDescending(r => r.Gil)],
                _ => [.. rows.OrderBy(r => r.Hops).ThenBy(r => r.Distance)],
            };
        }

        ImGui.Separator();
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(Loc.T("sides.none"));
            return;
        }
        ImGui.TextDisabled(Loc.T("sides.count", rows.Count));

        if (!ImGui.BeginTable("sides", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(Loc.T("sides.col-quest"), ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn(Loc.T("sides.col-level"), ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn(Loc.T("sides.col-reward"), ImGuiTableColumnFlags.WidthStretch, 2.6f);
        ImGui.TableSetupColumn(Loc.T("sides.col-where"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            var quest = row.Quest;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(quest.Name);
            ImGui.TextColored(Dim, quest.Issuer.Length > 0 ? $"{quest.Genre} · {quest.Issuer}" : quest.Genre);
            if (quest.Unlocks != null)
                ImGui.TextColored(Gold, Loc.T(quest.Unlocks));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(quest.Level.ToString());

            ImGui.TableNextColumn();
            var money = new List<string>();
            if (row.Experience > 0)
                money.Add(Loc.T("sides.exp", row.Experience));
            if (quest.Gil > 0)
                money.Add(Loc.T("sides.gil", quest.Gil));
            if (money.Count > 0)
                ImGui.TextUnformatted(string.Join(" · ", money));
            if (row.Upgrade is { } up)
                ImGui.TextColored(Upgrade, Loc.T("sides.upgrade", up.Name, up.From, up.To));
            if (quest.Items.Count > 0)
                ImGui.TextWrapped(Loc.T("sides.items", string.Join(", ", quest.Items.Select(i => ItemText(i)))));
            if (quest.Choice.Count > 0)
                ImGui.TextWrapped(Loc.T("sides.choice", string.Join(" / ", quest.Choice.Select(i => ItemText(i)))));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(quest.Zone);
            ImGui.TextColored(Dim, $"X {quest.MapX:0.0}  Y {quest.MapY:0.0}");
            ImGui.TextColored(Dim, row.Hops == 0 ? Loc.T("sides.distance", row.Distance) : Loc.T("sides.hops", row.Hops));

            ImGui.TableNextColumn();
            if (ImGui.Button($"{Loc.T("sides.go")}##{quest.Id}"))
                plugin.GoToQuestGiver(quest);
        }
        ImGui.EndTable();
    }

    private string ItemText((uint Item, uint Count) item)
    {
        var name = plugin.ItemName(item.Item);
        return item.Count > 1 ? $"{name} x{item.Count}" : name;
    }
}
