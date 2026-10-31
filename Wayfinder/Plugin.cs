using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Wayfinder;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/way";
    private static readonly TimeSpan QueryInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PathfindTimeout = TimeSpan.FromSeconds(3);
    private const float OffRouteDistance = 2.5f;
    private const float ExitReevaluateDistance = 40f;
    private const float AtShardDistance = 10f;
    // Rough yalms for the walk on the far side of a zone line, which cannot be measured from here.
    private const float CrossZoneAllowance = 150f;
    private const float AtNpcDistance = 6f;
    // A quest spot counts as visited once the player has stood this close to it.
    private const float VisitedDistance = 4f;
    // Rough yalms per second: running, and a mount before its speed is upgraded.
    private const float RunSpeed = 6f;
    private const float MountSpeed = 9f;
    // A fare is worth a short walk: paid transports are compared as if this many yalms longer.
    private const float FarePenalty = 30f;

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;

    private readonly Configuration config;
    private readonly WindowSystem windows = new("Wayfinder");
    private readonly ConfigWindow configWindow;
    private readonly PathRenderer renderer;
    private readonly ZoneLinks links;
    private readonly QuestTargets questTargets;
    private readonly Teleports teleports;
    private readonly Aethernet aethernet;
    private readonly Landmarks landmarks;
    private readonly AetherCurrents currents;
    private readonly MinimapOverlay minimap;

    private readonly ICallGateSubscriber<bool> navReady;
    private readonly ICallGateSubscriber<Vector3?> flagToPoint;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>> pathfind;

    private string status = "";
    private string targetLabel = "";
    // Name floating over the marker: the shard or exit being walked to, else the goal itself.
    private string markerLabel = "";
    private Vector3? target;
    private volatile List<Vector3> path = [];
    private Task? pending;
    private DateTime pendingSince;
    private DateTime nextQuery;
    private Vector3? lastTo;
    // Written from pathfind continuations on worker threads too.
    private readonly ConcurrentDictionary<string, string> lastTrace = new();
    private ZoneExit? currentExit;
    // Whether the last path was asked for in the air, so a mount/dismount re-plans it.
    private bool pathFlying;
    private DateTime nextMapTrace;
    private DateTime nextGroundTrace;
    // Spots of the current quest step already walked to, keyed by quest and step.
    private (ushort Quest, byte Sequence) visitedKey;
    private readonly HashSet<int> visited = [];
    private int chosenSpot = -1;
    private List<Vector3> otherSpots = [];

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (config.Mode == WayMode.LegacyMender)
        {
            config.Mode = WayMode.Nearest;
            config.Place = LandmarkKind.Mender;
        }
        Loc.Current = config.Language;
        questTargets = new QuestTargets(DataManager);
        teleports = new Teleports(DataManager);
        aethernet = new Aethernet(DataManager);
        landmarks = new Landmarks(DataManager);
        currents = new AetherCurrents(DataManager);
        Log.Information("[Wayfinder] landmarks: {List}", string.Join(", ", Enum.GetValues<LandmarkKind>().Select(k => $"{k} {landmarks.Count(k)}")));
        Log.Information("[Wayfinder] {Count} aethernet shards, {Currents} aether currents, {Aetherytes} aetheryte positions loaded", aethernet.Count, currents.Count, teleports.PositionCount);
        configWindow = new ConfigWindow(config, SaveConfig, this);
        windows.AddWindow(configWindow);
        renderer = new PathRenderer(config);
        minimap = new MinimapOverlay(config);

        links = new ZoneLinks(DataManager);
        links.Usable = CanUse;
        Log.Information("[Wayfinder] zone links loaded for {Count} territories, {Entrances} door entrances, {Transports} transports",
            links.Count, links.EntranceCount, links.TransportCount);

        navReady = PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        flagToPoint = PluginInterface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");
        pointOnFloor = PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        nearestPoint = PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        pathfind = PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind");

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Wayfinder window. Shortcuts: /way flag, /way quest [name], /way next, /way on|off|toggle.",
        });
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        windows.RemoveAllWindows();
        CommandManager.RemoveHandler(Command);
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(config);

    private void ToggleConfig() => configWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith("quest ", StringComparison.Ordinal))
        {
            var search = trimmed[6..].Trim();
            var match = questTargets.List().Find(q => q.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
            if (match.QuestId == 0)
            {
                Chat.Print($"[Wayfinder] {Loc.T("chat.no-match", search)}");
                return;
            }
            FollowQuest(match.MainScenario ? (ushort)0 : match.QuestId);
            return;
        }

        switch (lower)
        {
            case "" or "config" or "settings":
                ToggleConfig();
                return;
            case "toggle":
                config.Enabled = !config.Enabled;
                break;
            case "market" or "mb":
                SetPlace(LandmarkKind.MarketBoard);
                return;
            case "repair" or "mender":
                SetPlace(LandmarkKind.Mender);
                return;
            case "bell":
                SetPlace(LandmarkKind.SummoningBell);
                return;
            case "inn":
                SetPlace(LandmarkKind.Inn);
                return;
            case "mail":
                SetPlace(LandmarkKind.Mail);
                return;
            case "hunt":
                SetPlace(LandmarkKind.HuntBoard);
                return;
            case "chocobo":
                SetPlace(LandmarkKind.Chocobokeep);
                return;
            case "guild":
                SetPlace(LandmarkKind.Guild);
                return;
            case "currents" or "aether":
                SetMode(WayMode.AetherCurrent, config.QuestId);
                return;
            case "skip":
                SkipSpot();
                return;
            case "mapdebug":
                MapDebug.Dump(msg => Log.Information("[Wayfinder] {Message}", msg));
                return;
            case "flag":
                SetMode(WayMode.Flag, config.QuestId);
                return;
            case "quest":
                FollowQuest(0);
                return;
            case "next":
                FollowQuest(NextQuestId());
                return;
            case "on":
                config.Enabled = true;
                break;
            case "off":
                config.Enabled = false;
                break;
            default:
                Chat.Print($"[Wayfinder] {Loc.T("chat.help")}");
                return;
        }
        SaveConfig();
        Chat.Print($"[Wayfinder] {Loc.T(config.Enabled ? "chat.on" : "chat.off")}");
        if (!config.Enabled)
            ClearPath();
    }

    private void FollowQuest(ushort questId) => SetMode(WayMode.Quest, questId);

    // Called from commands and from the window.
    internal void SetMode(WayMode mode, ushort questId)
    {
        config.Mode = mode;
        config.QuestId = questId;
        config.Enabled = true;
        SaveConfig();
        ClearPath();
        currentExit = null;
        nextQuery = DateTime.MinValue;
        Chat.Print($"[Wayfinder] {DescribeMode()}");
    }

    internal void SetPlace(LandmarkKind kind)
    {
        config.Place = kind;
        SetMode(WayMode.Nearest, config.QuestId);
    }

    // The next spot of a quest step with several targets, for when the player was done with this one from afar.
    internal void SkipSpot()
    {
        if (chosenSpot >= 0)
            visited.Add(chosenSpot);
        nextQuery = DateTime.MinValue;
    }

    internal int SpotsLeft => config.Mode == WayMode.Quest ? otherSpots.Count + (chosenSpot >= 0 ? 1 : 0) : 0;

    private string DescribeMode()
    {
        if (config.Mode == WayMode.Flag)
            return Loc.T("chat.flag");
        if (config.Mode == WayMode.Nearest)
            return Loc.T("chat.nearest", Loc.T($"place.{config.Place}"));
        if (config.Mode == WayMode.AetherCurrent)
            return Loc.T("chat.currents");
        if (config.QuestId == 0)
            return SelectedQuest() is { } msq ? Loc.T("chat.msq", msq.Name) : Loc.T("chat.msq-none");
        return SelectedQuest() is { } q ? Loc.T("chat.quest", q.Name) : Loc.T("chat.quest-gone");
    }

    // Cycles main scenario (auto) -> each other accepted quest -> back to auto.
    private ushort NextQuestId()
    {
        var ids = new List<ushort> { 0 };
        foreach (var q in questTargets.List())
        {
            if (!q.MainScenario)
                ids.Add(q.QuestId);
        }
        var index = config.Mode == WayMode.Quest ? ids.IndexOf(config.QuestId) : -1;
        return ids[(index + 1) % ids.Count];
    }

    internal List<QuestEntry> Quests() => questTargets.List();

    internal TeleportOption? Teleport { get; private set; }

    internal string? TeleportHint => Teleport is { } t ? Loc.T("step.teleport", t.Name, t.Cost) : null;

    // What to do right now (walk to a shard / pick a destination / head for the exit), then the paid alternative.
    internal string? Step { get; private set; }

    // "Get on your mount", when it would help and the player has one.
    internal string? MountTip { get; private set; }

    internal string? Hint => string.Join('\n', new[] { Step, MountTip, TeleportHint }.Where(s => !string.IsNullOrEmpty(s)));

    // Aether currents of the current zone, for the window.
    internal CurrentProgress? CurrentsHere => currents.Progress(ClientState.TerritoryType);

    internal string QuestName(uint questRow) => questTargets.NameOfRow(questRow);

    internal string CurrentTarget => target != null ? targetLabel : status;

    internal void TeleportNow()
    {
        if (Teleport is { } t && !Teleports.Teleport(t))
            Chat.Print($"[Wayfinder] {Loc.T("chat.teleport-failed")}");
    }

    private void ClearPath()
    {
        Teleport = null;
        Step = null;
        MountTip = null;
        otherSpots = [];
        path = [];
        target = null;
        lastTo = null;
    }

    private void Draw()
    {
        if (!config.Enabled)
            return;
        // Floor graphics would sit on top of the game's own AoE markers mid-fight.
        if (config.HideInCombat && Condition[ConditionFlag.InCombat])
            return;
        if (config.HideInDuties && Condition[ConditionFlag.BoundByDuty])
            return;
        var player = ObjectTable.LocalPlayer;
        if (player == null)
            return;

        var from = player.Position;
        var now = DateTime.UtcNow;
        if (pending != null && now - pendingSince > PathfindTimeout)
        {
            pending = null;
            Trace("pathfind timed out", "path");
        }
        if (now >= nextQuery && pending == null)
        {
            nextQuery = now + QueryInterval;
            Refresh(from);
        }

        if (target is not { } to)
        {
            if (config.ShowHud)
                PathRenderer.DrawPill(!string.IsNullOrEmpty(status) ? status
                    : config.Mode == WayMode.Flag ? Loc.T("status.place-flag") : Loc.T("status.no-quest-target"), null);
            return;
        }

        var points = path;
        if (points.Count < 2)
        {
            if (config.ShowHud)
                PathRenderer.DrawPill(string.IsNullOrEmpty(status) ? Loc.T("status.looking") : status, null, Hint);
            return;
        }

        var speed = config.ShowEta ? (Condition[ConditionFlag.Mounted] ? MountSpeed : RunSpeed) : 0f;
        renderer.SetFlying(pathFlying);
        renderer.Draw(from, points, to, targetLabel, Hint, markerLabel, speed, otherSpots);
        if (now >= nextGroundTrace)
        {
            nextGroundTrace = now + TimeSpan.FromSeconds(3);
            Trace($"ground: {renderer.GroundDebug}", "ground");
        }
        if (config.ShowOnMinimap)
        {
            minimap.Draw(from, points, to, MapSizeFactor());
            // The figures move with the player; a sample every few seconds is enough to calibrate from.
            if (minimap.Debug is { } debug && now >= nextMapTrace)
            {
                nextMapTrace = now + TimeSpan.FromSeconds(5);
                Trace(debug, "minimap");
            }
        }
    }

    private float MapSizeFactor() =>
        DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(ClientState.TerritoryType, out var zone)
        && zone.Map.ValueNullable is { } map ? map.SizeFactor : 100f;

    private bool ZoneAllowsMount() =>
        DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(ClientState.TerritoryType, out var zone) && zone.Mount;

    private static unsafe bool HasMount()
    {
        var state = PlayerState.Instance();
        return state != null && state->NumOwnedMounts > 0;
    }

    private static unsafe bool CanFlyHere()
    {
        var state = PlayerState.Instance();
        return state != null && state->CanFly;
    }

    // Transports wait for the quest that opens them (the airship pass) and can be switched off.
    private bool CanUse(ZoneExit exit)
    {
        if (exit.Via is not { } via)
            return true;
        if (!config.UseTransports)
            return false;
        if (via.Quests.Length == 0)
            return true;
        return via.AnyQuest ? via.Quests.Any(QuestDone) : via.Quests.All(QuestDone);
    }

    // Warp conditions hold Quest sheet rows (0x10000 + id).
    private static bool QuestDone(uint questRow) => QuestManager.IsQuestComplete((ushort)(questRow & 0xFFFF));

    private static unsafe (bool Has, uint Territory, Vector2 Xz) ReadFlag()
    {
        var map = AgentMap.Instance();
        if (map == null || map->FlagMarkerCount == 0)
            return (false, 0, default);
        var marker = map->FlagMapMarkers[0];
        return (true, marker.TerritoryId, new Vector2(marker.XFloat, marker.YFloat));
    }

    internal static unsafe Vector3? CameraPosition()
    {
        var manager = CameraManager.Instance();
        if (manager == null)
            return null;
        var camera = manager->GetActiveCamera();
        return camera == null ? null : camera->CameraBase.SceneCamera.Object.Position;
    }

    private void Trace(string message, string kind = "state")
    {
        if (lastTrace.TryGetValue(kind, out var last) && last == message)
            return;
        lastTrace[kind] = message;
        Log.Information("[Wayfinder] {Message}", message);
    }

    // Where we are ultimately heading: the flag, or the current step of the selected quest.
    // Exact is null for a flag (map flags only have X/Z).
    private readonly record struct Goal(uint Territory, Vector2 Xz, Vector3? Exact, string Label, bool IsFlag);

    private Goal? ResolveGoal(Vector3 from)
    {
        if (config.Mode == WayMode.Flag)
        {
            var flag = ReadFlag();
            if (!flag.Has)
            {
                status = "";
                Trace("no flag on the map");
                return null;
            }
            return new Goal(flag.Territory, flag.Xz, null, Loc.T("flag"), true);
        }

        if (config.Mode == WayMode.Nearest)
        {
            var kind = config.Place;
            var name = Loc.T($"place.{kind}");
            Func<Landmark, bool>? filter = null;
            if (kind == LandmarkKind.Guild)
            {
                // A job (black mage) trains at its base class's guild (thaumaturge).
                var job = ObjectTable.LocalPlayer?.ClassJob.ValueNullable;
                var parent = job?.ClassJobParent.ValueNullable ?? job;
                var className = parent?.Name.ExtractText() ?? "";
                if (className.Length == 0)
                    return null;
                filter = p => p.Label.Contains(className, StringComparison.OrdinalIgnoreCase);
                name = Loc.T("place.Guild.named", char.ToUpperInvariant(className[0]) + className[1..]);
            }
            if (landmarks.Nearest(kind, ClientState.TerritoryType, new Vector2(from.X, from.Z), links, filter) is not { } place)
            {
                status = Loc.T(kind == LandmarkKind.Guild ? "status.no-guild" : "status.no-place", name);
                Trace($"no {kind} reachable");
                return null;
            }
            return new Goal(place.Territory, place.Xz, null, name, false);
        }

        if (config.Mode == WayMode.AetherCurrent)
        {
            if (currents.Progress(ClientState.TerritoryType) is not { } progress)
            {
                status = Loc.T("status.no-currents");
                Trace("no aether currents in this zone");
                return null;
            }
            if (progress.FieldLeft.Count == 0)
            {
                status = progress.QuestsLeft.Count == 0
                    ? Loc.T("status.currents-done", progress.Collected, progress.Total)
                    : Loc.T("status.currents-quests", progress.Collected, progress.Total, progress.QuestsLeft.Count);
                Trace("no field aether currents left here");
                return null;
            }
            var nearest = progress.FieldLeft.MinBy(c => Vector3.Distance(c.Position!.Value, from));
            var spot = nearest.Position!.Value;
            return new Goal(nearest.Territory, new Vector2(spot.X, spot.Z), spot, Loc.T("current", progress.Collected, progress.Total), false);
        }

        var quest = SelectedQuest();
        if (quest is not { } q)
        {
            status = config.QuestId == 0 ? Loc.T("status.no-msq") : Loc.T("status.quest-gone");
            Trace("no quest to follow");
            return null;
        }

        var locations = questTargets.Locations(q);
        if (locations.Count == 0)
        {
            status = Loc.T("status.no-location", q.Name);
            Trace($"quest {q.QuestId} step {QuestTargets.Sequence(q)}: no locations");
            return null;
        }

        // Several spots for one step (e.g. "speak to three people"): lead to the nearest one not yet walked to,
        // mark it visited on arrival and move on to the next. The game does not say which ones are done, so
        // standing at a spot is taken as having dealt with it (/way skip moves on by hand).
        var key = (q.QuestId, QuestTargets.Sequence(q));
        if (key != visitedKey)
        {
            visitedKey = key;
            visited.Clear();
        }
        var here = ClientState.TerritoryType;
        if (locations.Count > 1)
        {
            for (var i = 0; i < locations.Count; i++)
            {
                var spot = locations[i];
                if (spot.Territory == here && Vector2.Distance(new Vector2(spot.Position.X, spot.Position.Z), new Vector2(from.X, from.Z)) < VisitedDistance
                    && MathF.Abs(spot.Position.Y - from.Y) < 5f)
                    visited.Add(i);
            }
            // Every spot visited but the step still open: start over rather than lead nowhere.
            if (visited.Count >= locations.Count)
                visited.Clear();
        }

        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < locations.Count; i++)
        {
            if (visited.Contains(i))
                continue;
            // Spots in other zones rank after every spot in this one.
            var d = locations[i].Territory == here ? Vector3.Distance(locations[i].Position, from) : float.MaxValue / 2;
            if (best < 0 || d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        chosenSpot = locations.Count > 1 ? best : -1;
        otherSpots = [];
        for (var i = 0; i < locations.Count; i++)
        {
            if (i != best && !visited.Contains(i) && locations[i].Territory == here)
                otherSpots.Add(locations[i].Position);
        }

        var next = locations[best];
        var label = q.Accepted ? q.Name : Loc.T("pick-up", q.Name);

        // A search area: once nearby, lead to the quest's own NPCs around it that still carry a quest marker.
        if (next.Area && next.Territory == here
            && Vector2.Distance(new Vector2(next.Position.X, next.Position.Z), new Vector2(from.X, from.Z)) < AreaSearchDistance)
        {
            var marked = MarkedActors(questTargets.Actors(q), next.Position);
            if (marked.Count > 0)
            {
                marked.Sort((a, b) => Vector3.Distance(a.Position, from).CompareTo(Vector3.Distance(b.Position, from)));
                otherSpots = marked.Skip(1).Select(m => m.Position).ToList();
                chosenSpot = -1;
                Trace($"quest area: {marked.Count} marked NPCs, nearest {marked[0].Name}", "area");
                return new Goal(here, new Vector2(marked[0].Position.X, marked[0].Position.Z), marked[0].Position,
                    Loc.T("area-target", q.Name, marked[0].Name, marked.Count), false);
            }
            Trace("quest area: no marked NPCs in view", "area");
        }

        if (locations.Count > 1)
            label += Loc.T("spots", visited.Count + 1, locations.Count);
        return new Goal(next.Territory, new Vector2(next.Position.X, next.Position.Z), next.Position, label, false);
    }

    // Search areas are wide (the Level radius of "speak to three people" covers Vesper Bay); NPCs further than this
    // from its centre are not looked at.
    private const float AreaSearchDistance = 200f;

    private static unsafe List<(Vector3 Position, string Name)> MarkedActors(HashSet<uint> actors, Vector3 centre)
    {
        var result = new List<(Vector3, string)>();
        if (actors.Count == 0)
            return result;
        foreach (var obj in ObjectTable)
        {
            if (!actors.Contains(obj.BaseId) || obj.Address == nint.Zero)
                continue;
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            if (native->NamePlateIconId == 0)
                continue;
            if (Vector2.Distance(new Vector2(obj.Position.X, obj.Position.Z), new Vector2(centre.X, centre.Z)) > AreaSearchDistance)
                continue;
            result.Add((obj.Position, obj.Name.TextValue));
        }
        return result;
    }

    private QuestEntry? SelectedQuest()
    {
        var list = questTargets.List();
        if (config.QuestId == 0)
            return list.Find(q => q.MainScenario) is { QuestId: not 0 } msq ? msq : null;
        return list.Find(q => q.QuestId == config.QuestId) is { QuestId: not 0 } chosen ? chosen : null;
    }

    // ---------- aethernet ----------

    private AethernetPlan? currentShortcut;

    private AethernetPlan? PlanAethernet(Vector3 from, Goal destination, bool sameZone, List<ZoneExit> hops, out float saving)
    {
        saving = 0;
        if (!config.UseAethernet)
        {
            currentShortcut = null;
            return null;
        }

        var here = new Vector2(from.X, from.Z);
        if (aethernet.Plan(ClientState.TerritoryType, here, destination.Territory, destination.Xz, config.AethernetOverhead, out var why) is not { } plan)
        {
            currentShortcut = null;
            Trace($"aethernet: {why}", "aethernet");
            return null;
        }

        // Walking estimate to compare against: straight to the goal in this zone, or to the nearest exit plus
        // a flat allowance for the unknown part of the walk on the other side.
        float walk;
        if (sameZone)
        {
            walk = Aethernet.WalkEstimate(here, destination.Xz);
        }
        else
        {
            walk = float.MaxValue;
            foreach (var hop in hops)
                walk = MathF.Min(walk, Aethernet.WalkEstimate(here, hop.Xz) + CrossZoneAllowance);
        }

        saving = walk - plan.Cost;
        var sticky = currentShortcut is { } kept && kept.Entry.Id == plan.Entry.Id && kept.Exit.Id == plan.Exit.Id;
        var needed = sticky ? config.AethernetMinSaving * 0.5f : config.AethernetMinSaving;
        if (saving < needed)
        {
            currentShortcut = null;
            Trace($"aethernet {plan.Entry.Name} > {plan.Exit.Name} not worth it", "aethernet");
            return null;
        }

        currentShortcut = plan;
        // No saving figure in the trace: it changes every step and would flood the log.
        Trace($"aethernet {plan.Entry.Name} > {plan.Exit.Name}", "aethernet");
        return plan;
    }

    // ---------- zone exits ----------

    private readonly ConcurrentDictionary<ZoneExit, float> exitLengths = new();
    private Vector3 exitEvalFrom;
    private uint exitEvalGoal;
    private int exitEvalPending;
    private DateTime exitEvalStarted;

    private ZoneExit ChooseExit(Vector3 from, List<ZoneExit> hops, uint goalTerritory, out float cost)
    {
        var here = new Vector2(from.X, from.Z);
        if (config.ExitByPathLength && hops.Count > 1)
        {
            if (exitEvalPending > 0 && DateTime.UtcNow - exitEvalStarted > PathfindTimeout)
                exitEvalPending = 0;
            var stale = exitEvalGoal != goalTerritory || Vector3.Distance(from, exitEvalFrom) > ExitReevaluateDistance;
            if (stale && exitEvalPending == 0)
                MeasureExits(from, hops, goalTerritory);
        }

        // Measured walking length when we have it, straight line (inflated) until then.
        float Cost(ZoneExit e) => (config.ExitByPathLength && exitLengths.TryGetValue(e, out var length)
            ? length
            : Vector2.Distance(e.Xz, here) * 1.3f) + (e.Via is { Gil: > 0 } ? FarePenalty : 0f);

        var exit = hops[0];
        foreach (var hop in hops)
        {
            if (Cost(hop) < Cost(exit))
                exit = hop;
        }
        // Stick with the exit we were heading to unless another one is clearly better, so it doesn't flip-flop.
        if (currentExit is { } kept && hops.Contains(kept) && Cost(kept) < Cost(exit) + 20f)
            exit = kept;
        currentExit = exit;
        cost = Cost(exit);
        return exit;
    }

    private void MeasureExits(Vector3 from, List<ZoneExit> hops, uint goalTerritory)
    {
        exitLengths.Clear();
        exitEvalFrom = from;
        exitEvalGoal = goalTerritory;
        exitEvalStarted = DateTime.UtcNow;
        foreach (var hop in hops)
        {
            Vector3? floor;
            try
            {
                floor = FloorNear(hop.Xz, from);
            }
            catch (Exception)
            {
                return;
            }
            if (floor is not { } point)
                continue;

            Interlocked.Increment(ref exitEvalPending);
            var exit = hop;
            pathfind.InvokeFunc(from, point, false).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { Count: > 0 } route)
                {
                    var length = Vector3.Distance(from, route[0]);
                    for (var i = 1; i < route.Count; i++)
                        length += Vector3.Distance(route[i - 1], route[i]);
                    exitLengths[exit] = length;
                    Trace($"exit {exit.Name} at {exit.Xz.X:0},{exit.Xz.Y:0}: {length:0} y walking", $"exit-{exit.Xz}");
                }
                Interlocked.Decrement(ref exitEvalPending);
            });
        }
    }

    private void Refresh(Vector3 from)
    {
        if (ResolveGoal(from) is not { } destination)
        {
            ClearPath();
            return;
        }

        var goal = destination.Xz;
        var exact = destination.Exact;
        var sameZone = destination.Territory == ClientState.TerritoryType;
        // Walking is always drawn; a teleport is only suggested, the player decides whether the gil is worth it.
        Teleport = !config.ShowTeleportHint ? null
            : !sameZone ? teleports.Best(destination.Territory, destination.Xz, links, ClientState.TerritoryType)
            // In a big field zone an aetheryte next to the goal can save most of the walk.
            : teleports.InZone(ClientState.TerritoryType, new Vector2(from.X, from.Z), destination.Xz, config.TeleportMinWalk * 0.6f, out _);
        Trace(Teleport is { } tp ? $"teleport hint: {tp.Name} ({tp.Cost} gil)" : "no teleport hint", "teleport");
        var label = destination.Label;
        Step = null;
        var marker = label;
        var hops = sameZone ? [] : links.FirstHops(ClientState.TerritoryType, destination.Territory);

        if (PlanAethernet(from, destination, sameZone, hops, out var saving) is { } shortcut)
        {
            // Lead to the entry shard; once the player hops, the next refresh starts from the exit shard.
            goal = shortcut.Entry.Xz;
            exact = null;
            marker = shortcut.Entry.Name;
            var atShard = Vector2.Distance(new Vector2(from.X, from.Z), shortcut.Entry.Xz) < AtShardDistance;
            Step = atShard
                ? Loc.T("step.shard.here", shortcut.Exit.Name)
                : Loc.T("step.shard.walk", shortcut.Entry.Name, shortcut.Exit.Name, MathF.Min(saving, 9999f));
            Teleport = null;
        }
        else if (!sameZone)
        {
            if (hops.Count == 0)
            {
                ClearPath();
                status = Loc.T("status.no-route");
                Trace($"no zone route from {ClientState.TerritoryType} to {destination.Territory}");
                return;
            }

            var exit = ChooseExit(from, hops, destination.Territory, out var exitCost);
            goal = exit.Xz;
            exact = null;
            if (exit.Via is { } via)
            {
                // Ferry, airship, lift: walk to the NPC, then pick the line in their menu.
                var atNpc = Vector2.Distance(new Vector2(from.X, from.Z), exit.Xz) < AtNpcDistance;
                var fare = via.Gil > 0 ? Loc.T("step.fare", via.Gil) : "";
                Step = Loc.T(atNpc ? "step.transport.here" : "step.transport.walk", via.Npc, via.Option, fare);
                marker = via.Npc;
            }
            else
            {
                Step = Loc.T("step.exit", exit.Name);
                marker = exit.Name;
            }

            // A teleport for a short walk is noise: only suggest it past the configured walking distance. The walk
            // is the way to this exit plus what lies behind it: into the next zone and across to the goal when
            // that is the goal's zone (Ul'dah's gate is close, Horizon is a long way into Western Thanalan),
            // a flat allowance per zone otherwise.
            var hopsLeft = links.Hops(ClientState.TerritoryType, destination.Territory, 6);
            var beyond = hopsLeft == 1 && links.ArrivalIn(destination.Territory, ClientState.TerritoryType) is { } arrival
                ? Vector2.Distance(arrival, destination.Xz) * 1.3f
                : CrossZoneAllowance * Math.Max(1, hopsLeft);
            if (exitCost + beyond < config.TeleportMinWalk)
                Teleport = null;
        }

        Vector3? floor = null;
        try
        {
            // Right after a zone change vnavmesh is still loading or building the mesh; every query fails until then.
            if (!navReady.InvokeFunc())
            {
                ClearPath();
                status = Loc.T("status.mesh-loading");
                Trace("navmesh not ready");
                return;
            }
            // Quest spots have a real height; snapping near it avoids landing on a roof above a multi-floor city.
            if (exact is { } spot)
                floor = nearestPoint.InvokeFunc(spot, 4f, 6f);
            if (floor == null && sameZone && destination.IsFlag && goal == destination.Xz)
                floor = FloorNear(goal, from, flagFallback: true);
            floor ??= FloorNear(goal, from);
        }
        catch (Exception e)
        {
            ClearPath();
            status = Loc.T("status.no-vnavmesh");
            Trace($"vnavmesh IPC failed: {e.GetType().Name}: {e.Message}");
            return;
        }

        if (floor is not { } to)
        {
            ClearPath();
            status = Loc.T("status.off-mesh");
            Trace($"{label} x={goal.X:0.0} z={goal.Y:0.0} (goal territory {destination.Territory}, here {ClientState.TerritoryType}): no floor point");
            return;
        }

        target = to;
        targetLabel = label;
        markerLabel = marker;
        status = "";
        Trace($"{label} x={goal.X:0.0} z={goal.Y:0.0} (goal territory {destination.Territory}, here {ClientState.TerritoryType}) -> floor {to}");

        UpdateMountTip();

        // Mounted in a zone where flying is unlocked: plan through the air (vnavmesh's flight volume).
        var fly = config.FlyWhenAble && Condition[ConditionFlag.Mounted] && CanFlyHere();
        // In the air the drawn route starts above the floor; allow more slack before calling it "off route".
        var offRoute = path.Count > 0 && renderer.OffRoute > (fly ? OffRouteDistance * 3f : OffRouteDistance);
        var changed = lastTo is not { } prev || Vector3.DistanceSquared(prev, to) > 0.25f || fly != pathFlying;
        // Keep the route fixed while the player follows it: re-asking vnavmesh on every step reshapes the curve
        // slightly each time, which reads as stutter.
        if (!changed && !offRoute && path.Count > 0)
            return;

        // A new target invalidates the old route; leaving the route keeps it on screen until the new one arrives.
        if (changed)
            path = [];
        lastTo = to;
        pathFlying = fly;
        Trace(fly ? "planning a flight path" : "planning a walking path", "fly");
        try
        {
            pendingSince = DateTime.UtcNow;
            pending = pathfind.InvokeFunc(from, to, fly).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { Count: > 0 } result)
                {
                    path = result;
                    Trace("path found", "path");
                }
                else
                {
                    if (path.Count == 0)
                        status = Loc.T("status.no-path");
                    Trace($"pathfind failed: {t.Exception?.GetBaseException().Message ?? "empty result"}", "path");
                }
                pending = null;
            });
        }
        catch (Exception e)
        {
            status = Loc.T("status.pathfind-failed");
            Log.Debug(e, "Pathfind failed");
            pending = null;
        }
    }

    // Floor under a map position that only has X/Z (map icons, flags, exits, NPCs from the Level sheet).
    // Dropping from the sky lands on the topmost surface, which in Ul'dah is a balcony or roof above the NPC, so
    // first look for floor at the player's own level: straight down from a little above their head, then
    // sideways around that height. Throws when vnavmesh is missing; callers handle that.
    private Vector3? FloorNear(Vector2 xz, Vector3 from, bool flagFallback = false)
    {
        var floor = pointOnFloor.InvokeFunc(new Vector3(xz.X, from.Y + 3f, xz.Y), false, 4f);
        floor ??= nearestPoint.InvokeFunc(new Vector3(xz.X, from.Y, xz.Y), 6f, 8f);
        if (flagFallback)
            floor ??= flagToPoint.InvokeFunc();
        floor ??= pointOnFloor.InvokeFunc(new Vector3(xz.X, 1024f, xz.Y), false, 5f);
        floor ??= nearestPoint.InvokeFunc(new Vector3(xz.X, from.Y, xz.Y), 10f, 50f);
        // Zone exit arrows sit on the map edge, often a little past where the walkable mesh ends and far above or
        // below the player (Western Thanalan's way to Central Thanalan: y 94 against the player's 21).
        floor ??= pointOnFloor.InvokeFunc(new Vector3(xz.X, 1024f, xz.Y), false, 20f);
        floor ??= nearestPoint.InvokeFunc(new Vector3(xz.X, from.Y, xz.Y), 25f, 300f);
        return floor;
    }

    // Suggests the mount for a long walk in a zone that allows it. Players without a mount yet (the chocobo comes
    // with the Grand Company quest) are never nagged.
    private void UpdateMountTip()
    {
        MountTip = null;
        if (!config.MountHint || Condition[ConditionFlag.Mounted] || Condition[ConditionFlag.InCombat] || !ZoneAllowsMount() || !HasMount())
            return;
        var walk = path.Count > 0 ? renderer.Remaining : 0f;
        if (walk < config.MountHintDistance)
            return;
        MountTip = config.ShowEta
            ? Loc.T("step.mount.eta", PathRenderer.Eta(walk / MountSpeed), PathRenderer.Eta(walk / RunSpeed))
            : Loc.T("step.mount");
    }
}
