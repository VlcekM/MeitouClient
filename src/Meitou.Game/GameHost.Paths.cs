using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// F11's path view (Meitou only; docs/game/pathfinding.md "Meitou debug view"): a checkbox on the statistics' characters line that draws, over everything and
/// still once F11 is closed, the path each selected or inspected character follows (<see cref="CharacterSnapshot.Path"/>): from its feet to the next waypoint,
/// then the navmesh part, then the provisional long-range part (<see cref="CharacterSnapshot.PathExact"/>), the next waypoint and the end marked.
/// </summary>
sealed partial class GameHost
{
    bool walkPaths;

    /// <summary>How far above the waypoints the path is drawn, so the ground does not swallow it (the navmesh view's lift).</summary>
    const float PathLift = NavOverlayLift;

    static readonly Vector4 PathColour = new(0.2f, 1f, 0.35f, 0.95f);
    static readonly Vector4 ProvisionalColour = new(1f, 0.55f, 0.1f, 0.95f);
    static readonly Vector4 NextWaypointColour = new(1f, 1f, 1f, 1f);
    static readonly Vector4 PathEndColour = new(1f, 0.25f, 0.25f, 1f);

    /// <summary>The paths of the characters the snapshot details, seen through the world camera at the display size.</summary>
    void DrawWalkPaths(DebugOverlay overlay, WorldSnapshot snapshot, WorldCamera camera, int width, int height)
    {
        var viewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        foreach (var c in snapshot.Characters)
            if (c.Path.Count > 0) DrawWalkPath(overlay, c, viewProjection, width, height);
        overlay.Flush(width, height);
    }

    /// <summary>The paths' count in a small panel at the top left, below <paramref name="panelsBottom"/>; returns the new bottom (as the fps line does).</summary>
    static float DrawWalkPathsLabel(DebugOverlay overlay, WorldSnapshot snapshot, int width, int height, float panelsBottom)
    {
        int shown = 0, points = 0;
        foreach (var c in snapshot.Characters)
        {
            if (c.Path.Count == 0) continue;
            shown++;
            points += c.Path.Count;
        }
        string label = shown == 0 ? "paths: select a walking character" : $"{shown} path{(shown == 1 ? "" : "s")}, {points} waypoints left";

        float w = label.Length * overlay.CharWidth + 12, h = overlay.LineHeight + 8, top = panelsBottom + 8;
        overlay.Rect(8, top, 8 + w, top + h, DebugOverlay.PanelColour);
        overlay.Text(label, 14, top + 4, PathColour);
        overlay.Flush(width, height);
        return top + h - 8;   // the panels add their own 16 px margin
    }

    /// <summary>One character's path: the legs (navmesh green, provisional orange), a square at each waypoint, the next one white and the end red.</summary>
    static void DrawWalkPath(DebugOverlay overlay, CharacterSnapshot c, Matrix4x4 viewProjection, int width, int height)
    {
        var lift = Vector3.UnitY * PathLift;
        var from = c.Position + lift;
        for (int i = 0; i < c.Path.Count; i++)
        {
            var to = c.Path[i] + lift;
            var colour = i >= c.PathExact ? ProvisionalColour : PathColour;
            if (Clip(from, to, viewProjection, width, height) is var (a, b)) overlay.Line(a.X, a.Y, b.X, b.Y, 2.5f, colour);

            var mark = i == 0 ? NextWaypointColour : i == c.Path.Count - 1 ? PathEndColour : colour;
            float half = i == 0 || i == c.Path.Count - 1 ? 5 : 3;
            if (Screen(to, viewProjection, width, height) is { } at) overlay.Rect(at.X - half, at.Y - half, at.X + half, at.Y + half, mark);
            from = to;
        }
    }

    /// <summary>A world point in pixels, or null behind the eye.</summary>
    static Vector2? Screen(Vector3 p, Matrix4x4 viewProjection, int width, int height)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), viewProjection);
        return clip.W <= NearW ? null : Pixel(clip, width, height);
    }

    /// <summary>A segment in pixels, cut where it passes behind the eye; null when all of it is behind.</summary>
    static (Vector2, Vector2)? Clip(Vector3 a, Vector3 b, Matrix4x4 viewProjection, int width, int height)
    {
        var ca = Vector4.Transform(new Vector4(a, 1), viewProjection);
        var cb = Vector4.Transform(new Vector4(b, 1), viewProjection);
        if (ca.W <= NearW && cb.W <= NearW) return null;
        if (ca.W <= NearW) ca = Vector4.Lerp(ca, cb, (NearW - ca.W) / (cb.W - ca.W));
        else if (cb.W <= NearW) cb = Vector4.Lerp(cb, ca, (NearW - cb.W) / (ca.W - cb.W));
        return (Pixel(ca, width, height), Pixel(cb, width, height));
    }

    const float NearW = 0.01f;

    static Vector2 Pixel(Vector4 clip, int width, int height) =>
        new((clip.X / clip.W * 0.5f + 0.5f) * width, (0.5f - clip.Y / clip.W * 0.5f) * height);
}
