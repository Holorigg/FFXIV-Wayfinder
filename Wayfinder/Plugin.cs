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
using FFXIVClientStructs.FFXIV.Client.Game.Control;
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

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Loc.Current = config.Language;
        questTargets = new QuestTargets(DataManager);
        teleports = new Teleports(DataManager);
        aethernet = new Aethernet(DataManager);
        landmarks = new Landmarks(DataManager);
        Log.Information("[Wayfinder] {Boards} market boards, {Menders} menders loaded", landmarks.Count(LandmarkKind.MarketBoard), landmarks.Count(LandmarkKind.Mender));
        Log.Information("[Wayfinder] {Count} aethernet shards loaded", aethernet.Count);
        configWindow = new ConfigWindow(config, SaveConfig, this);
        windows.AddWindow(configWindow);
        renderer = new PathRenderer(config);

        links = new ZoneLinks(DataManager);
        Log.Information("[Wayfinder] zone links loaded for {Count} territories, {Entrances} door entrances", links.Count, links.EntranceCount);

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
                SetMode(WayMode.MarketBoard, config.QuestId);
                return;
            case "repair" or "mender":
                SetMode(WayMode.Mender, config.QuestId);
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

    private string DescribeMode()
    {
        if (config.Mode == WayMode.Flag)
            return Loc.T("chat.flag");
        if (config.Mode == WayMode.MarketBoard)
            return Loc.T("chat.market");
        if (config.Mode == WayMode.Mender)
            return Loc.T("chat.mender");
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

    internal string? Hint => string.Join('\n', new[] { Step, TeleportHint }.Where(s => !string.IsNullOrEmpty(s)));

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

        renderer.Draw(from, points, to, targetLabel, Hint, markerLabel);
    }

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

        if (config.Mode is WayMode.MarketBoard or WayMode.Mender)
        {
            var (kind, name, missing) = config.Mode == WayMode.MarketBoard
                ? (LandmarkKind.MarketBoard, "market", "status.no-market")
                : (LandmarkKind.Mender, "mender", "status.no-mender");
            if (landmarks.Nearest(kind, ClientState.TerritoryType, new Vector2(from.X, from.Z), links) is not { } place)
            {
                status = Loc.T(missing);
                Trace($"no {kind} reachable on foot");
                return null;
            }
            return new Goal(place.Territory, place.Xz, null, Loc.T(name), false);
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

        // Several spots for one step (e.g. "speak to three people"): nearest one in this zone, else the first.
        var best = locations[0];
        var bestDistance = float.MaxValue;
        foreach (var location in locations)
        {
            if (location.Territory != ClientState.TerritoryType)
                continue;
            var d = Vector3.Distance(location.Position, from);
            if (d < bestDistance)
            {
                best = location;
                bestDistance = d;
            }
        }
        var label = q.Accepted ? q.Name : Loc.T("pick-up", q.Name);
        return new Goal(best.Territory, new Vector2(best.Position.X, best.Position.Z), best.Position, label, false);
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
        float Cost(ZoneExit e) => config.ExitByPathLength && exitLengths.TryGetValue(e, out var length)
            ? length
            : Vector2.Distance(e.Xz, here) * 1.3f;

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
                floor = pointOnFloor.InvokeFunc(new Vector3(hop.Xz.X, 1024f, hop.Xz.Y), false, 5f)
                        ?? nearestPoint.InvokeFunc(new Vector3(hop.Xz.X, from.Y, hop.Xz.Y), 10f, 50f);
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
        Teleport = !sameZone && config.ShowTeleportHint ? teleports.Best(destination.Territory, destination.Xz, links, ClientState.TerritoryType) : null;
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
            Step = Loc.T("step.exit", exit.Name);
            marker = exit.Name;

            // A teleport for a short walk is noise: only suggest it past the configured walking distance.
            if (exitCost + CrossZoneAllowance < config.TeleportMinWalk)
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
            // Map icons and flags only have X/Z. Dropping from the sky lands on the topmost surface, which in
            // Ul'dah is a balcony or roof above the NPC, so first look for floor at the player's own level:
            // straight down from a little above their head, then sideways around that height.
            floor ??= pointOnFloor.InvokeFunc(new Vector3(goal.X, from.Y + 3f, goal.Y), false, 4f);
            floor ??= nearestPoint.InvokeFunc(new Vector3(goal.X, from.Y, goal.Y), 6f, 8f);
            if (sameZone && destination.IsFlag)
                floor ??= flagToPoint.InvokeFunc();
            floor ??= pointOnFloor.InvokeFunc(new Vector3(goal.X, 1024f, goal.Y), false, 5f);
            floor ??= nearestPoint.InvokeFunc(new Vector3(goal.X, from.Y, goal.Y), 10f, 50f);
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

        // Keep the route fixed while the player follows it: re-asking vnavmesh on every step reshapes the curve
        // slightly each time, which reads as stutter.
        var offRoute = path.Count > 0 && renderer.OffRoute > OffRouteDistance;
        var changed = lastTo is not { } prev || Vector3.DistanceSquared(prev, to) > 0.25f;
        if (!changed && !offRoute && path.Count > 0)
            return;

        // A new target invalidates the old route; leaving the route keeps it on screen until the new one arrives.
        if (changed)
            path = [];
        lastTo = to;
        try
        {
            pendingSince = DateTime.UtcNow;
            pending = pathfind.InvokeFunc(from, to, false).ContinueWith(t =>
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
}
