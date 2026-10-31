using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace Wayfinder;

// Draws the route in world space (projected quads on the floor) so it reads as part of the scene.
internal sealed class PathRenderer
{
    private const float SampleStep = 0.5f;
    private const float VisCell = 1.5f;
    private const float AlwaysVisibleNear = 6f;
    private const float OccludeDelay = 0.3f;
    private const float ChevronSpacing = 2.6f;
    private const float FlowSpeed = 2.2f;
    private static readonly Vector3 Up = Vector3.UnitY;

    private readonly Configuration config;
    private readonly Dictionary<(int, int, int), VisState> visibility = [];
    private readonly Dictionary<(int, int, int), bool> rayCache = [];
    private float time;
    private int frame;

    private sealed class VisState
    {
        public float Alpha;
        public float OccludedFor;
        public int Frame;
    }

    private readonly record struct Sample(Vector3 Pos, Vector3 Dir, float Arc);

    public PathRenderer(Configuration config) => this.config = config;

    // Distance from the player to the drawn route; the plugin asks vnavmesh for a new path when it grows.
    public float OffRoute { get; private set; }

    // Walking length left along the drawn route, as of the last frame.
    public float Remaining { get; private set; }

    // `speed` (yalms per second) adds a travel time to the panel when above zero; `others` are the remaining
    // spots of a quest step with several targets, drawn as small markers.
    public void Draw(Vector3 player, IReadOnlyList<Vector3> path, Vector3 target, string label, string? hint, string markerLabel,
                     float speed, IReadOnlyList<Vector3> others)
    {
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        time += dt;
        frame++;
        rayCache.Clear();

        Prepare(path);
        if (curve.Count < 2)
            return;

        // The curve stays fixed in the world; only where it starts moves as the player walks along it.
        var start = ProjectOnCurve(player, out var offRoute);
        OffRoute = offRoute;
        var end = MathF.Min(arcs[^1], start + config.Range + 2f);
        var camera = Plugin.CameraPosition();

        var samples = new List<Sample>();
        samples.Add(SampleAt(start, start));
        for (var arc = MathF.Ceiling(start / SampleStep) * SampleStep; arc < end; arc += SampleStep)
        {
            if (arc - start > 0.05f)
                samples.Add(SampleAt(arc, start));
        }
        samples.Add(SampleAt(end, start));
        if (samples.Count < 2)
            return;

        var alphas = new float[samples.Count];
        for (var i = 0; i < samples.Count; i++)
            alphas[i] = SampleAlpha(samples[i], camera, dt);

        var drawList = ImGui.GetBackgroundDrawList();
        if (config.ShowRibbon)
            DrawRibbon(drawList, samples, alphas);
        if (config.ShowChevrons)
            DrawChevrons(drawList, start, end, camera, dt);
        if (Vector3.Distance(player, target) < config.Range + 20f)
            DrawDestination(drawList, target, markerLabel, camera, dt);
        foreach (var other in others)
        {
            if (Vector3.Distance(player, other) < config.Range + 20f)
                DrawSpot(drawList, other, camera, dt);
        }
        Remaining = arcs[^1] - start + offRoute;
        if (config.ShowHud)
        {
            var text = Loc.T("panel", label, Remaining);
            if (speed > 0f && Remaining > 15f)
                text += Loc.T("panel.eta", Eta(Remaining / speed));
            DrawPill(text, ArrowAngle(player, start), hint);
        }

        PruneVisibility();
    }

    // ---------- geometry ----------

    private IReadOnlyList<Vector3>? source;
    private List<Vector3> curve = [];
    private float[] arcs = [];

    private void Prepare(IReadOnlyList<Vector3> path)
    {
        if (ReferenceEquals(path, source))
            return;
        source = path;
        curve = Smooth([.. path]);
        arcs = new float[curve.Count];
        for (var i = 1; i < curve.Count; i++)
            arcs[i] = arcs[i - 1] + Vector3.Distance(curve[i - 1], curve[i]);
    }

    private Vector3 PositionAt(float arc)
    {
        arc = Math.Clamp(arc, 0f, arcs[^1]);
        var lo = 0;
        var hi = arcs.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (arcs[mid] <= arc)
                lo = mid;
            else
                hi = mid;
        }
        var span = arcs[hi] - arcs[lo];
        return span < 1e-5f ? curve[lo] : Vector3.Lerp(curve[lo], curve[hi], (arc - arcs[lo]) / span);
    }

    private Vector3 DirectionAt(float arc)
    {
        var d = PositionAt(arc + 0.4f) - PositionAt(arc - 0.4f);
        var flat = new Vector3(d.X, 0, d.Z);
        return flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitZ;
    }

    private Sample SampleAt(float arc, float start) => new(PositionAt(arc), DirectionAt(arc), arc - start);

    private float ProjectOnCurve(Vector3 p, out float distance)
    {
        var bestArc = 0f;
        distance = float.MaxValue;
        for (var i = 0; i < curve.Count - 1; i++)
        {
            var a = curve[i];
            var ab = curve[i + 1] - a;
            var lengthSq = ab.LengthSquared();
            var t = lengthSq < 1e-6f ? 0f : Math.Clamp(Vector3.Dot(p - a, ab) / lengthSq, 0f, 1f);
            var d = Vector3.Distance(p, a + ab * t);
            if (d < distance)
            {
                distance = d;
                bestArc = arcs[i] + (arcs[i + 1] - arcs[i]) * t;
            }
        }
        return bestArc;
    }

    // Chaikin corner cutting: vnavmesh corners are sharp, this turns them into curves.
    private static List<Vector3> Smooth(List<Vector3> points)
    {
        for (var pass = 0; pass < 2 && points.Count >= 3; pass++)
        {
            var result = new List<Vector3>(points.Count * 2) { points[0] };
            for (var i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                if (i > 0)
                    result.Add(Vector3.Lerp(a, b, 0.25f));
                if (i < points.Count - 2)
                    result.Add(Vector3.Lerp(a, b, 0.75f));
            }
            result.Add(points[^1]);
            points = result;
        }
        return points;
    }

    // ---------- visibility ----------

    private float SampleAlpha(Sample s, Vector3? camera, float dt)
    {
        var distanceFade = Math.Clamp((config.Range - s.Arc) / (config.Range * 0.35f), 0f, 1f);
        // Start past the character's feet: the overlay is drawn on top of everything, including the player model.
        var nearFade = Math.Clamp((s.Arc - 1.4f) / 2.2f, 0f, 1f);
        var vis = 1f;
        if (config.HideBehindWalls && s.Arc > AlwaysVisibleNear && camera is { } cam)
            vis = Visibility(Cell(s.Pos), cam, s.Pos, dt);
        return config.Opacity * vis * distanceFade * nearFade;
    }

    private static (int, int, int) Cell(Vector3 p) =>
        ((int)MathF.Floor(p.X / VisCell), (int)MathF.Floor(p.Y / VisCell), (int)MathF.Floor(p.Z / VisCell));

    // Smoothed visibility per world cell: fades in quickly, and only fades out after staying hidden
    // for OccludeDelay, so a lamp post or a passing pillar does not make the path blink.
    private float Visibility((int, int, int) cell, Vector3 camera, Vector3 point, float dt)
    {
        if (!rayCache.TryGetValue(cell, out var clear))
        {
            clear = IsClear(camera, point + Up * 0.3f) || IsClear(camera, point + Up * 1.3f);
            rayCache[cell] = clear;
        }

        if (!visibility.TryGetValue(cell, out var state))
            visibility[cell] = state = new VisState { Alpha = clear ? 0.4f : 0f };
        if (state.Frame == frame)
            return state.Alpha;
        state.Frame = frame;

        if (clear)
        {
            state.OccludedFor = 0;
            state.Alpha = MathF.Min(1f, state.Alpha + dt * 6f);
        }
        else
        {
            state.OccludedFor += dt;
            if (state.OccludedFor > OccludeDelay)
                state.Alpha = MathF.Max(0f, state.Alpha - dt * 5f);
        }
        return state.Alpha;
    }

    // Starts the ray a little in front of the camera so the camera sitting inside foliage or a prop
    // does not count as a wall.
    private static bool IsClear(Vector3 camera, Vector3 point)
    {
        var toPoint = point - camera;
        var distance = toPoint.Length();
        if (distance < 2f)
            return true;
        var direction = toPoint / distance;
        const float skip = 0.8f;
        var remaining = distance - skip;
        return !BGCollisionModule.RaycastMaterialFilter(camera + direction * skip, direction, out var hit, remaining)
               || hit.Distance >= remaining - 0.45f;
    }

    private void PruneVisibility()
    {
        if (frame % 120 != 0)
            return;
        var stale = new List<(int, int, int)>();
        foreach (var (cell, state) in visibility)
        {
            if (frame - state.Frame > 120)
                stale.Add(cell);
        }
        foreach (var cell in stale)
            visibility.Remove(cell);
    }

    // ---------- drawing ----------

    private static bool Project(Vector3 world, out Vector2 screen) => Plugin.GameGui.WorldToScreen(world, out screen, out _);

    private uint Colour(float alpha, float whiten = 0f)
    {
        var c = config.Color;
        var rgb = Vector3.Lerp(new Vector3(c.X, c.Y, c.Z), Vector3.One, whiten);
        return ImGui.GetColorU32(new Vector4(rgb, Math.Clamp(alpha, 0f, 1f)));
    }

    private void DrawRibbon(ImDrawListPtr drawList, List<Sample> samples, float[] alphas)
    {
        var half = config.Width * 0.5f;
        var lift = Up * 0.05f;
        for (var i = 0; i < samples.Count - 1; i++)
        {
            var alpha = MathF.Min(alphas[i], alphas[i + 1]);
            if (alpha < 0.01f)
                continue;
            var a = samples[i];
            var b = samples[i + 1];
            var ra = Vector3.Cross(a.Dir, Up);
            var rb = Vector3.Cross(b.Dir, Up);

            // Stacked bands fake a soft falloff from the centre line to the edges.
            foreach (var (widthScale, opacity, whiten) in Bands)
                DrawBand(drawList, a.Pos + lift, b.Pos + lift, ra * half * widthScale, rb * half * widthScale, Colour(alpha * opacity, whiten));
        }
    }

    private static readonly (float Width, float Opacity, float Whiten)[] Bands =
    [
        (1.00f, 0.035f, 0f),
        (0.78f, 0.045f, 0f),
        (0.56f, 0.06f, 0.05f),
        (0.36f, 0.08f, 0.15f),
        (0.18f, 0.12f, 0.3f),
        (0.07f, 0.16f, 0.5f),
    ];

    private static void DrawBand(ImDrawListPtr drawList, Vector3 a, Vector3 b, Vector3 ra, Vector3 rb, uint colour)
    {
        if (Project(a - ra, out var p1) && Project(b - rb, out var p2) && Project(b + rb, out var p3) && Project(a + ra, out var p4))
            drawList.AddQuadFilled(p1, p2, p3, p4, colour);
    }

    // Chevrons sit at fixed spacing along the world curve and slide continuously with time, so they glide
    // instead of hopping between samples.
    private void DrawChevrons(ImDrawListPtr drawList, float start, float end, Vector3? camera, float dt)
    {
        var scale = config.Width / 0.7f;
        var length = 0.45f * scale;
        var spread = 0.36f * scale;
        var thickness = 0.13f * scale;
        var lift = Up * 0.07f;
        var phase = config.Animate ? time * FlowSpeed % ChevronSpacing : 0f;

        for (var arc = MathF.Floor(start / ChevronSpacing) * ChevronSpacing + phase; arc < end; arc += ChevronSpacing)
        {
            if (arc < start)
                continue;
            var s = SampleAt(arc, start);
            var alpha = SampleAlpha(s, camera, dt) * 0.7f;
            if (alpha < 0.01f)
                continue;
            var right = Vector3.Cross(s.Dir, Up);
            var centre = s.Pos + lift;
            var tip = centre + s.Dir * (length * 0.5f);
            var back = centre - s.Dir * (length * 0.5f);
            var glow = Colour(alpha * 0.25f, 0.1f);
            DrawArm(drawList, tip + s.Dir * 0.05f, back + right * (spread * 1.15f), s.Dir * (thickness * 2f), glow);
            DrawArm(drawList, tip + s.Dir * 0.05f, back - right * (spread * 1.15f), s.Dir * (thickness * 2f), glow);
            var colour = Colour(alpha, 0.3f);
            DrawArm(drawList, tip, back + right * spread, s.Dir * thickness, colour);
            DrawArm(drawList, tip, back - right * spread, s.Dir * thickness, colour);
        }
    }

    private static void DrawArm(ImDrawListPtr drawList, Vector3 tip, Vector3 end, Vector3 thickness, uint colour)
    {
        if (Project(tip, out var p1) && Project(end, out var p2) && Project(end - thickness, out var p3) && Project(tip - thickness, out var p4))
            drawList.AddQuadFilled(p1, p2, p3, p4, colour);
    }

    private void DrawDestination(ImDrawListPtr drawList, Vector3 target, string label, Vector3? camera, float dt)
    {
        var vis = 1f;
        if (config.HideBehindWalls && camera is { } cam)
            vis = Visibility((int.MinValue, 0, 0), cam, target + Up * 1.2f, dt);
        var alpha = config.Opacity * vis;
        if (alpha < 0.01f)
            return;

        var lift = Up * 0.06f;
        var pulse = 0.85f + 0.12f * MathF.Sin(time * 3f);
        DrawRing(drawList, target + lift, 0.9f * pulse, Colour(alpha * 0.9f, 0.3f), 2f);

        var ping = time % 1.6f / 1.6f;
        DrawRing(drawList, target + lift, 0.6f + ping * 1.4f, Colour(alpha * (1f - ping) * 0.6f), 1.5f);

        var height = 2.4f + 0.15f * MathF.Sin(time * 2f);
        if (!Project(target + lift, out var ground) || !Project(target + Up * height, out var top))
            return;
        drawList.AddLine(ground, top, Colour(alpha * 0.35f), 1.5f);

        var size = Math.Clamp(Vector2.Distance(ground, top) * 0.12f, 5f, 12f);
        var d1 = top + new Vector2(0, -size * 1.3f);
        var d2 = top + new Vector2(size, 0);
        var d3 = top + new Vector2(0, size * 1.3f);
        var d4 = top + new Vector2(-size, 0);
        drawList.AddQuadFilled(d1, d2, d3, d4, Colour(alpha, 0.25f));
        drawList.AddQuad(d1, d2, d3, d4, ImGui.GetColorU32(new Vector4(0, 0, 0, alpha * 0.5f)), 1.5f);

        if (!string.IsNullOrEmpty(label))
        {
            var textSize = ImGui.CalcTextSize(label);
            var pos = new Vector2(top.X - textSize.X * 0.5f, d1.Y - textSize.Y - 4f);
            drawList.AddText(pos + Vector2.One, ImGui.GetColorU32(new Vector4(0, 0, 0, alpha * 0.8f)), label);
            drawList.AddText(pos, ImGui.GetColorU32(new Vector4(1, 1, 1, alpha)), label);
        }
    }

    // A quieter marker for the other targets of the same quest step.
    private void DrawSpot(ImDrawListPtr drawList, Vector3 spot, Vector3? camera, float dt)
    {
        var vis = 1f;
        if (config.HideBehindWalls && camera is { } cam)
            vis = Visibility(Cell(spot + Up * 1.2f), cam, spot + Up * 1.2f, dt);
        var alpha = config.Opacity * vis * 0.7f;
        if (alpha < 0.01f)
            return;
        DrawRing(drawList, spot + Up * 0.06f, 0.7f, Colour(alpha, 0.2f), 1.5f);
        if (!Project(spot + Up * 0.06f, out var ground) || !Project(spot + Up * 1.8f, out var top))
            return;
        drawList.AddLine(ground, top, Colour(alpha * 0.3f), 1f);
        drawList.AddCircleFilled(top, 4f, Colour(alpha, 0.3f));
    }

    internal static string Eta(float seconds)
    {
        if (seconds < 60f)
            return Loc.T("eta.seconds", MathF.Max(5f, MathF.Round(seconds / 5f) * 5f));
        var minutes = (int)(seconds / 60f);
        var rest = (int)(seconds % 60f / 10f) * 10;
        return rest == 0 ? Loc.T("eta.minutes", minutes) : Loc.T("eta.minutes-seconds", minutes, rest);
    }

    private static void DrawRing(ImDrawListPtr drawList, Vector3 centre, float radius, uint colour, float thickness)
    {
        const int segments = 40;
        Vector2? previous = null;
        for (var i = 0; i <= segments; i++)
        {
            var angle = i * MathF.Tau / segments;
            var world = centre + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
            if (!Project(world, out var screen))
            {
                previous = null;
                continue;
            }
            if (previous is { } p)
                drawList.AddLine(p, screen, colour, thickness);
            previous = screen;
        }
    }

    // Screen-space angle (0 = up) of the route a few yalms ahead, for the arrow in the panel.
    private float? ArrowAngle(Vector3 player, float start)
    {
        var ahead = PositionAt(start + 4f);
        if (Vector3.DistanceSquared(ahead, player) < 0.25f)
            return null;
        if (!Project(player, out var from))
            return null;
        if (!Project(ahead, out var to))
            return MathF.PI;
        var v = to - from;
        return v.LengthSquared() < 1f ? null : MathF.Atan2(v.X, -v.Y);
    }

    public static void DrawPill(string text, float? arrowAngle, string? hint = null)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        var screen = ImGui.GetIO().DisplaySize;
        var textSize = ImGui.CalcTextSize(text);
        const float pad = 10f;
        var arrowSpace = arrowAngle.HasValue ? 26f : 0f;
        var size = new Vector2(textSize.X + pad * 2 + arrowSpace, textSize.Y + pad * 1.2f);
        var min = new Vector2((screen.X - size.X) * 0.5f, screen.Y * 0.075f);
        var max = min + size;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.05f, 0.07f, 0.09f, 0.6f)), size.Y * 0.5f);
        drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.12f)), size.Y * 0.5f, ImDrawFlags.None, 1f);

        if (!string.IsNullOrEmpty(hint))
        {
            // First line is the instruction for right now (brighter), the rest are alternatives.
            var y = max.Y + 5f;
            var first = true;
            foreach (var line in hint.Split('\n'))
            {
                var lineSize = ImGui.CalcTextSize(line);
                var linePos = new Vector2((screen.X - lineSize.X) * 0.5f, y);
                var colour = first ? new Vector4(1f, 0.9f, 0.6f, 1f) : new Vector4(0.85f, 0.8f, 0.65f, 0.75f);
                drawList.AddText(linePos + Vector2.One, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.85f)), line);
                drawList.AddText(linePos, ImGui.GetColorU32(colour), line);
                y += lineSize.Y + 2f;
                first = false;
            }
        }

        if (arrowAngle is { } angle)
        {
            var centre = new Vector2(min.X + pad + 9f, (min.Y + max.Y) * 0.5f);
            var (sin, cos) = MathF.SinCos(angle);
            Vector2 Rot(float x, float y) => centre + new Vector2(x * cos - y * sin, x * sin + y * cos);
            var colour = ImGui.GetColorU32(new Vector4(0.75f, 0.93f, 1f, 1f));
            drawList.AddTriangleFilled(Rot(0, -8), Rot(-6, 6), Rot(0, 2), colour);
            drawList.AddTriangleFilled(Rot(0, -8), Rot(0, 2), Rot(6, 6), colour);
        }

        var textPos = new Vector2(min.X + pad + arrowSpace, min.Y + pad * 0.6f);
        drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(0.92f, 0.97f, 1f, 1f)), text);
    }
}
