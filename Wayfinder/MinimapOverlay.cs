using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Wayfinder;

// Draws the route over the game's minimap (_NaviMap) and the area map (AreaMap).
//
// One yalm is SizeFactor/100 pixels of the 2048-pixel map texture. Measured with /way mapdebug:
// - minimap: a component node (176x176, origin at its centre, rotated when north is not locked) holds the map
//   texture as a 2048x2048 image whose scale is the zoom and whose origin is the player's map pixel, so the player
//   sits at the component's centre;
// - area map: the player pin node marks the player; Atk2DAreaMap.MapScale is the zoom.
// Node ScreenX/Y already include the addon's scale; sizes do not.
internal sealed class MinimapOverlay
{
    private const float MapTexture = 2048f;
    // Visible radius of the minimap disc inside the 176-pixel component, in unscaled pixels.
    private const float MinimapRadius = 76f;
    private readonly Configuration config;

    public MinimapOverlay(Configuration config) => this.config = config;

    // Logged on change so a wrong scale can be calibrated from dalamud.log.
    public string? Debug { get; private set; }

    public void Draw(Vector3 player, IReadOnlyList<Vector3> route, Vector3 target, float sizeFactor)
    {
        var minimap = DrawMinimap(player, route, target, sizeFactor);
        var areaMap = DrawAreaMap(player, route, target, sizeFactor);
        Debug = $"{minimap}; {areaMap}";
    }

    private unsafe string DrawMinimap(Vector3 player, IReadOnlyList<Vector3> route, Vector3 target, float sizeFactor)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>("_NaviMap");
        if (addon == null || !addon->IsVisible)
            return "minimap hidden";
        if (!FindMinimapTexture(addon, out var component, out var image))
            return "minimap: no map texture node";

        var scale = addon->Scale;
        var node = &component->AtkResNode;
        var centre = new Vector2(node->ScreenX, node->ScreenY) + new Vector2(node->OriginX, node->OriginY) * scale;
        var pixelsPerYalm = sizeFactor / 100f * image->AtkResNode.ScaleX * scale;
        var rotation = node->Rotation;
        var radius = MinimapRadius * scale;

        var (sin, cos) = MathF.SinCos(rotation);
        Vector2 ToScreen(Vector3 world)
        {
            var d = new Vector2(world.X - player.X, world.Z - player.Z) * pixelsPerYalm;
            return centre + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
        }

        bool Inside(Vector2 p) => Vector2.DistanceSquared(p, centre) <= radius * radius;
        Vector2 Clip(Vector2 inside, Vector2 outside) => ClipToCircle(inside, outside, centre, radius);
        DrawRoute(route, target, ToScreen, Inside, Clip, centre, radius, 2.5f);
        return $"minimap: centre {centre.X:0},{centre.Y:0} zoom {image->AtkResNode.ScaleX:0.###} hud {scale:0.##} {pixelsPerYalm:0.###} px/y rot {rotation:0.##}";
    }

    // The minimap's map is the only 2048x2048 image, inside a component node.
    private static unsafe bool FindMinimapTexture(AtkUnitBase* addon, out AtkComponentNode* component, out AtkImageNode* image)
    {
        component = null;
        image = null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (int)node->Type < 1000)
                continue;
            var candidate = (AtkComponentNode*)node;
            if (candidate->Component == null)
                continue;
            var children = candidate->Component->UldManager;
            for (var j = 0; j < children.NodeListCount; j++)
            {
                var child = children.NodeList[j];
                if (child != null && child->Type == NodeType.Image && child->Width == MapTexture && child->Height == MapTexture)
                {
                    component = candidate;
                    image = (AtkImageNode*)child;
                    return true;
                }
            }
        }
        return false;
    }

    private unsafe string DrawAreaMap(Vector3 player, IReadOnlyList<Vector3> route, Vector3 target, float sizeFactor)
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonAreaMap>("AreaMap");
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return "area map hidden";
        // Only when the map on screen is the one the player stands on.
        var agent = AgentMap.Instance();
        if (agent == null || agent->SelectedMapId != agent->CurrentMapId)
            return "area map shows another map";
        var pin = addon->AreaMap.PlayerPin;
        var map = addon->ComponentMap;
        if (pin == null || map == null)
            return "area map: no pin";

        var scale = addon->AtkUnitBase.Scale;
        // The pin turns with the camera and its ScreenX/Y is its rotated top-left corner, so the player (the pin's
        // pivot) is the corner plus the rotated origin. Checked across camera turns: one fixed point.
        var pinNode = &pin->AtkResNode;
        var (pinSin, pinCos) = MathF.SinCos(pinNode->Rotation);
        var origin = new Vector2(pinNode->OriginX, pinNode->OriginY) * scale;
        var centre = new Vector2(pinNode->ScreenX, pinNode->ScreenY)
                     + new Vector2(origin.X * pinCos - origin.Y * pinSin, origin.X * pinSin + origin.Y * pinCos);
        var zoom = addon->AreaMap.MapScale;
        var pixelsPerYalm = sizeFactor / 100f * zoom * scale;
        if (pixelsPerYalm <= 0f)
            return "area map: zero scale";

        var frame = &map->AtkComponentBase.OwnerNode->AtkResNode;
        var min = new Vector2(frame->ScreenX, frame->ScreenY);
        var max = min + new Vector2(frame->Width, frame->Height) * scale;

        Vector2 ToScreen(Vector3 world) => centre + new Vector2(world.X - player.X, world.Z - player.Z) * pixelsPerYalm;
        bool Inside(Vector2 p) => p.X >= min.X && p.Y >= min.Y && p.X <= max.X && p.Y <= max.Y;
        Vector2 Clip(Vector2 inside, Vector2 outside) => ClipToRect(inside, outside, min, max);

        // The area map is a window: drawn on the foreground list it would cover other windows, so it is clipped
        // to the map frame.
        var drawList = ImGui.GetForegroundDrawList();
        drawList.PushClipRect(min, max, false);
        DrawRoute(route, target, ToScreen, Inside, Clip, null, 0f, 3f);
        drawList.PopClipRect();
        return $"area map: player {centre.X:0},{centre.Y:0} zoom {zoom:0.###} offset {addon->AreaMap.MapOffsetX:0.#},{addon->AreaMap.MapOffsetY:0.#} centre {addon->AreaMap.MapCenterX:0.#},{addon->AreaMap.MapCenterY:0.#} hud {scale:0.##} {pixelsPerYalm:0.###} px/y";
    }

    private void DrawRoute(IReadOnlyList<Vector3> route, Vector3 target, Func<Vector3, Vector2> toScreen, Func<Vector2, bool> inside,
                           Func<Vector2, Vector2, Vector2> clip, Vector2? rimCentre, float rimRadius, float width)
    {
        var drawList = ImGui.GetForegroundDrawList();
        var c = config.Color;
        var line = ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, 0.95f));
        var shadow = ImGui.GetColorU32(new Vector4(0, 0, 0, 0.55f));

        // A segment with one end outside is cut where it leaves the map.
        Vector2? previous = null;
        foreach (var point in route)
        {
            var p = toScreen(point);
            if (previous is { } a && (inside(a) || inside(p)))
            {
                var s = inside(a) ? a : clip(p, a);
                var e = inside(p) ? p : clip(a, p);
                drawList.AddLine(s, e, shadow, width + 2f);
                drawList.AddLine(s, e, line, width);
            }
            previous = p;
        }

        var goal = toScreen(target);
        if (!inside(goal))
        {
            if (rimCentre is not { } rc)
                return;
            // Off the minimap: pin the goal to the rim in its direction.
            goal = rc + Vector2.Normalize(goal - rc) * rimRadius;
        }
        drawList.AddCircleFilled(goal, 5f, shadow);
        drawList.AddCircleFilled(goal, 3.5f, ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.55f, 1f)));
    }

    // Point where the segment from `inside` towards `outside` crosses the circle.
    private static Vector2 ClipToCircle(Vector2 inside, Vector2 outside, Vector2 centre, float radius)
    {
        var d = outside - inside;
        var f = inside - centre;
        var a = Vector2.Dot(d, d);
        if (a < 1e-6f)
            return inside;
        var b = 2f * Vector2.Dot(f, d);
        var c = Vector2.Dot(f, f) - radius * radius;
        var t = (-b + MathF.Sqrt(MathF.Max(0f, b * b - 4f * a * c))) / (2f * a);
        return inside + d * Math.Clamp(t, 0f, 1f);
    }

    private static Vector2 ClipToRect(Vector2 inside, Vector2 outside, Vector2 min, Vector2 max)
    {
        var d = outside - inside;
        var t = 1f;
        if (d.X > 0) t = MathF.Min(t, (max.X - inside.X) / d.X);
        if (d.X < 0) t = MathF.Min(t, (min.X - inside.X) / d.X);
        if (d.Y > 0) t = MathF.Min(t, (max.Y - inside.Y) / d.Y);
        if (d.Y < 0) t = MathF.Min(t, (min.Y - inside.Y) / d.Y);
        return inside + d * Math.Clamp(t, 0f, 1f);
    }
}
