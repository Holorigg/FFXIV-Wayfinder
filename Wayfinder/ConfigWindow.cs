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

        ImGui.TextUnformatted(Loc.T("ui.nearest"));
        var places = Enum.GetValues<LandmarkKind>();
        for (var i = 0; i < places.Length; i++)
        {
            var kind = places[i];
            if (i % 4 != 0)
                ImGui.SameLine();
            var active = config.Mode == WayMode.Nearest && config.Place == kind;
            if (active)
                ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            if (ImGui.Button($"{Loc.T($"ui.place.{kind}")}###place{kind}", new System.Numerics.Vector2(130, 0)))
                plugin.SetPlace(kind);
            if (active)
                ImGui.PopStyleColor();
            Tip($"ui.place-tip.{kind}");
        }

        ImGui.Separator();
        ImGui.TextUnformatted(Loc.T("ui.lead"));
        Mode(WayMode.Flag, "ui.mode.flag");
        ImGui.SameLine();
        Mode(WayMode.Quest, "ui.mode.quest");
        ImGui.SameLine();
        Mode(WayMode.Nearest, "ui.mode.nearest");
        ImGui.SameLine();
        Mode(WayMode.AetherCurrent, "ui.mode.currents");
        Tip("ui.currents-tip");
        Mode(WayMode.AllQuests, "ui.mode.all");
        Tip("ui.all-tip");
        ImGui.SameLine();
        Mode(WayMode.Party, "ui.mode.party");
        Tip("ui.party-tip");
        ImGui.SameLine();
        Mode(WayMode.Npc, "ui.mode.npc");

        if (config.Mode == WayMode.Flag)
            ImGui.TextDisabled(Loc.T("ui.flag-tip"));
        else if (config.Mode == WayMode.Quest)
            DrawQuestList();
        else if (config.Mode == WayMode.AetherCurrent)
            DrawCurrents();
        else if (config.Mode == WayMode.AllQuests)
            ImGui.TextDisabled(Loc.T("ui.all-count", plugin.QuestsInJournal));
        else if (config.Mode == WayMode.Party)
            DrawParty();
        else if (config.Mode == WayMode.Npc)
            DrawNpcSearch();

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

    private void DrawParty()
    {
        var names = plugin.PartyNames();
        if (names.Count == 0)
        {
            ImGui.TextDisabled(Loc.T("ui.party-none"));
            return;
        }
        foreach (var name in names)
        {
            if (ImGui.RadioButton($"{name}##party", config.FollowName == name) && config.FollowName != name)
                plugin.Follow(name);
        }
    }

    private string npcQuery = "";
    private string lastQuery = "";
    private System.Collections.Generic.List<NpcPlace> npcResults = [];

    private void DrawNpcSearch()
    {
        ImGui.SetNextItemWidth(220);
        ImGui.InputText(Loc.T("ui.npc-search"), ref npcQuery, 64);
        Tip("ui.npc-hint");
        if (npcQuery != lastQuery)
        {
            lastQuery = npcQuery;
            npcResults = plugin.SearchNpcs(npcQuery);
        }
        if (npcQuery.Trim().Length >= 2 && npcResults.Count == 0)
            ImGui.TextDisabled(Loc.T("ui.npc-none"));
        foreach (var npc in npcResults)
        {
            var label = npc.Title.Length > 0 ? $"{npc.Name} <{npc.Title}> — {npc.Zone}" : $"{npc.Name} — {npc.Zone}";
            if (ImGui.Selectable($"{label}##npc{npc.Level}", config.NpcLevel == npc.Level))
                plugin.GoToNpc(npc);
        }
    }

    private void Mode(WayMode mode, string key)
    {
        if (ImGui.RadioButton(Loc.T(key), config.Mode == mode) && config.Mode != mode)
            plugin.SetMode(mode, config.QuestId);
    }

    private void DrawCurrents()
    {
        if (plugin.CurrentsHere is not { } progress)
        {
            ImGui.TextDisabled(Loc.T("ui.currents-none"));
            return;
        }
        ImGui.TextUnformatted(Loc.T("ui.currents-progress", progress.Collected, progress.Total));
        ImGui.TextUnformatted(Loc.T("ui.currents-field", progress.FieldLeft.Count));
        if (progress.QuestsLeft.Count == 0)
            return;
        ImGui.TextUnformatted(Loc.T("ui.currents-quests"));
        foreach (var quest in progress.QuestsLeft)
            ImGui.BulletText(plugin.QuestName(quest));
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
        if (plugin.SpotsLeft > 1)
        {
            ImGui.TextUnformatted(Loc.T("ui.spots-left", plugin.SpotsLeft));
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("ui.skip")))
                plugin.SkipSpot();
            Tip("ui.skip-tip");
        }
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

        changed |= ImGui.Checkbox(Loc.T("ui.transports"), ref config.UseTransports);
        Tip("ui.transports-tip");
        changed |= ImGui.Checkbox(Loc.T("ui.fly"), ref config.FlyWhenAble);
        Tip("ui.fly-tip");
        changed |= ImGui.Checkbox(Loc.T("ui.mount-hint"), ref config.MountHint);
        Tip("ui.mount-hint-tip");
        if (config.MountHint)
            changed |= ImGui.SliderFloat(Loc.T("ui.mount-min"), ref config.MountHintDistance, 50f, 600f, "%.0f");

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
        changed |= ImGui.Checkbox(Loc.T("ui.eta"), ref config.ShowEta);
        changed |= ImGui.Checkbox(Loc.T("ui.minimap"), ref config.ShowOnMinimap);
        Tip("ui.minimap-tip");
        changed |= ImGui.SliderFloat(Loc.T("ui.panel-scale"), ref config.PanelScale, 0.6f, 2.5f, "%.1f");
        changed |= ImGui.SliderFloat(Loc.T("ui.panel-x"), ref config.PanelX, 0f, 1f, "%.2f");
        changed |= ImGui.SliderFloat(Loc.T("ui.panel-y"), ref config.PanelY, 0f, 0.95f, "%.2f");
        changed |= ImGui.Checkbox(Loc.T("ui.reward"), ref config.RewardHint);
        Tip("ui.reward-tip");
        changed |= ImGui.Checkbox(Loc.T("ui.combat"), ref config.HideInCombat);
        Tip("ui.combat-tip");
        changed |= ImGui.Checkbox(Loc.T("ui.duties"), ref config.HideInDuties);
        if (changed)
            save();
    }
}
