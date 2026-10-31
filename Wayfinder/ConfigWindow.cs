using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Wayfinder;

internal sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly Plugin plugin;

    public ConfigWindow(Configuration config, Action save, Plugin plugin)
        : base("Wayfinder###WayfinderConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        this.save = save;
        this.plugin = plugin;
    }

    public override void Draw()
    {
        if (ImGui.Checkbox(Loc.T("ui.show"), ref config.Enabled))
            save();
        ImGui.SameLine(0, 30);
        ImGui.TextUnformatted(Loc.T("ui.language"));
        ImGui.SameLine();
        if (ImGui.RadioButton("RU", config.Language == Language.Russian))
            SetLanguage(Language.Russian);
        ImGui.SameLine();
        if (ImGui.RadioButton("EN", config.Language == Language.English))
            SetLanguage(Language.English);

        if (ImGui.Button(Loc.T("ui.market-button")))
            plugin.SetMode(WayMode.MarketBoard, config.QuestId);
        Tip("ui.market-tip");

        ImGui.Separator();
        ImGui.TextUnformatted(Loc.T("ui.lead"));
        if (ImGui.RadioButton(Loc.T("ui.mode.flag"), config.Mode == WayMode.Flag) && config.Mode != WayMode.Flag)
            plugin.SetMode(WayMode.Flag, config.QuestId);
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.T("ui.mode.quest"), config.Mode == WayMode.Quest) && config.Mode != WayMode.Quest)
            plugin.SetMode(WayMode.Quest, config.QuestId);
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.T("ui.mode.market"), config.Mode == WayMode.MarketBoard) && config.Mode != WayMode.MarketBoard)
            plugin.SetMode(WayMode.MarketBoard, config.QuestId);

        if (config.Mode == WayMode.Flag)
            ImGui.TextDisabled(Loc.T("ui.flag-tip"));
        else if (config.Mode == WayMode.Quest)
            DrawQuestList();

        ImGui.Separator();
        var now = plugin.CurrentTarget;
        ImGui.TextUnformatted(string.IsNullOrEmpty(now) ? Loc.T("ui.now-none") : Loc.T("ui.now", now));
        if (plugin.Step is { Length: > 0 } step)
            ImGui.TextWrapped(step);
        if (plugin.Teleport is { } teleport)
        {
            ImGui.TextUnformatted(Loc.T("ui.teleport-row", teleport.Name, teleport.Cost));
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("ui.teleport-button")))
                plugin.TeleportNow();
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader($"{Loc.T("ui.routing")}###routing"))
            DrawRouting();
        if (ImGui.CollapsingHeader($"{Loc.T("ui.look")}###look"))
            DrawLook();
    }

    private void SetLanguage(Language language)
    {
        if (config.Language == language)
            return;
        config.Language = language;
        Loc.Current = language;
        save();
    }

    private void DrawQuestList()
    {
        var quests = plugin.Quests();
        var msq = quests.Find(q => q.MainScenario);
        var auto = msq.QuestId != 0
            ? Loc.T("ui.msq-auto-named", msq.Name) + (msq.Accepted ? "" : Loc.T("ui.pick-up-suffix"))
            : Loc.T("ui.msq-auto");
        if (ImGui.RadioButton($"{auto}###msq", config.QuestId == 0) && config.QuestId != 0)
            plugin.SetMode(WayMode.Quest, 0);

        foreach (var quest in quests)
        {
            if (quest.MainScenario)
                continue;
            if (ImGui.RadioButton($"{quest.Name}##{quest.QuestId}", config.QuestId == quest.QuestId) && config.QuestId != quest.QuestId)
                plugin.SetMode(WayMode.Quest, quest.QuestId);
        }

        if (quests.Count == 0)
            ImGui.TextDisabled(Loc.T("ui.no-quests"));
        ImGui.TextDisabled(Loc.T("ui.next-tip"));
    }

    private void DrawRouting()
    {
        var changed = false;
        changed |= ImGui.Checkbox(Loc.T("ui.exit-by-path"), ref config.ExitByPathLength);
        Tip("ui.exit-by-path-tip");

        changed |= ImGui.Checkbox(Loc.T("ui.aethernet"), ref config.UseAethernet);
        Tip("ui.aethernet-tip");
        if (config.UseAethernet)
        {
            changed |= ImGui.SliderFloat(Loc.T("ui.aethernet-min"), ref config.AethernetMinSaving, 20f, 300f, "%.0f");
            changed |= ImGui.SliderFloat(Loc.T("ui.aethernet-cost"), ref config.AethernetOverhead, 0f, 150f, "%.0f");
            Tip("ui.aethernet-cost-tip");
        }

        changed |= ImGui.Checkbox(Loc.T("ui.teleport-hint"), ref config.ShowTeleportHint);
        Tip("ui.teleport-hint-tip");
        if (config.ShowTeleportHint)
        {
            changed |= ImGui.SliderFloat(Loc.T("ui.teleport-min"), ref config.TeleportMinWalk, 100f, 2000f, "%.0f");
            Tip("ui.teleport-min-tip");
        }
        if (changed)
            save();
    }

    private static void Tip(string key)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.T(key));
    }

    private void DrawLook()
    {
        var changed = false;
        changed |= ImGui.ColorEdit4(Loc.T("ui.colour"), ref config.Color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha);
        changed |= ImGui.SliderFloat(Loc.T("ui.opacity"), ref config.Opacity, 0.2f, 1f, "%.2f");
        changed |= ImGui.SliderFloat(Loc.T("ui.range"), ref config.Range, 20f, 150f, "%.0f");
        changed |= ImGui.SliderFloat(Loc.T("ui.width"), ref config.Width, 0.3f, 1.5f, "%.2f");
        changed |= ImGui.Checkbox(Loc.T("ui.ribbon"), ref config.ShowRibbon);
        changed |= ImGui.Checkbox(Loc.T("ui.arrows"), ref config.ShowChevrons);
        changed |= ImGui.Checkbox(Loc.T("ui.animate"), ref config.Animate);
        changed |= ImGui.Checkbox(Loc.T("ui.walls"), ref config.HideBehindWalls);
        changed |= ImGui.Checkbox(Loc.T("ui.panel"), ref config.ShowHud);
        changed |= ImGui.Checkbox(Loc.T("ui.combat"), ref config.HideInCombat);
        Tip("ui.combat-tip");
        changed |= ImGui.Checkbox(Loc.T("ui.duties"), ref config.HideInDuties);
        if (changed)
            save();
    }
}
