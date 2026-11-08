using System.Numerics;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game;

/// <summary>
/// The character boxes (Meitou only; a checkbox on the F11 statistics' characters line, and the F10 Overlays row "Character boxes", one shared switch) that outline, over everything, each character its "drawn" count
/// counts (<see cref="CharacterRenderer.CountedBoxes"/>: in the colour view's frustum and not hidden by fog, whether or not anything covers it).
/// </summary>
sealed partial class GameHost
{
    bool characterBoxes;
    // Where the checkboxes were drawn last (x0, y0, x1, y1), null while the statistics are hidden.
    Vector4? characterBoxesToggle, walkPathsToggle;
    readonly List<(string Line, Vector4 Rect)> statsLaidOut = [];
    readonly List<Vector4> countedBoxes = [];

    static readonly Vector4 CharacterBoxColour = new(1f, 0.85f, 0.2f, 0.9f);

    /// <summary>Ticks the statistics' checkboxes as their switches stand (the lines are built once a second).</summary>
    void TickStatsToggles(List<string> stats)
    {
        TickStatsToggle(stats, characterBoxes, WorldStats.CharacterBoxesOff, WorldStats.CharacterBoxesOn);
        TickStatsToggle(stats, walkPaths, WorldStats.PathsOff, WorldStats.PathsOn);
    }

    static void TickStatsToggle(List<string> stats, bool on, string off, string ticked)
    {
        string from = on ? off : ticked;
        string to = on ? ticked : off;
        for (int i = 0; i < stats.Count; i++)
            if (stats[i].Contains(from, StringComparison.Ordinal)) stats[i] = stats[i].Replace(from, to, StringComparison.Ordinal);
    }

    /// <summary>Where the checkboxes are in the panel just drawn (from its laid-out lines).</summary>
    void FindStatsToggles(float charWidth)
    {
        characterBoxesToggle = FindStatsToggle(characterBoxes ? WorldStats.CharacterBoxesOn : WorldStats.CharacterBoxesOff, charWidth);
        walkPathsToggle = FindStatsToggle(walkPaths ? WorldStats.PathsOn : WorldStats.PathsOff, charWidth);
    }

    /// <summary>A checkbox's rectangle in the panel just drawn, or null when it is not there.</summary>
    Vector4? FindStatsToggle(string marker, float charWidth)
    {
        foreach (var (line, rect) in statsLaidOut)
        {
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            float x0 = rect.X + at * charWidth;
            return new Vector4(x0, rect.Y, x0 + marker.Length * charWidth, rect.W);
        }
        return null;
    }

    /// <summary>A left click on a checkbox flips its switch; true when one took the click.</summary>
    bool StatsToggleClick(Vector2 at)
    {
        if (Inside(characterBoxesToggle, at)) { characterBoxes = !characterBoxes; return true; }
        if (Inside(walkPathsToggle, at)) { walkPaths = !walkPaths; return true; }
        return false;
    }

    static bool Inside(Vector4? rect, Vector2 at) => rect is { } t && at.X >= t.X && at.X <= t.Z && at.Y >= t.Y && at.Y <= t.W;

    /// <summary>The counted characters' outlines over the whole picture, seen through the world camera at the display size.</summary>
    void DrawCharacterBoxes(DebugOverlay overlay, CharacterRenderer characters, WorldCamera camera, int width, int height)
    {
        var viewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        characters.CountedBoxes(viewProjection, width, height, countedBoxes);
        const float line = 1.5f;
        foreach (var whole in countedBoxes)
        {
            // A third of the bounding sphere's box each way, round its centre: the whole box spans weapons and ragdoll reach and hides the crowd.
            var mid = new Vector2(whole.X + whole.Z, whole.Y + whole.W) * 0.5f;
            var half = new Vector2(whole.Z - whole.X, whole.W - whole.Y) / 6;
            var b = new Vector4(mid - half, (mid + half).X, (mid + half).Y);
            overlay.Rect(b.X, b.Y, b.Z, b.Y + line, CharacterBoxColour);
            overlay.Rect(b.X, b.W - line, b.Z, b.W, CharacterBoxColour);
            overlay.Rect(b.X, b.Y, b.X + line, b.W, CharacterBoxColour);
            overlay.Rect(b.Z - line, b.Y, b.Z, b.W, CharacterBoxColour);
        }
        overlay.Text($"{countedBoxes.Count} counted", 16, height - overlay.LineHeight - 8, CharacterBoxColour);
        overlay.Flush(width, height);
    }
}
