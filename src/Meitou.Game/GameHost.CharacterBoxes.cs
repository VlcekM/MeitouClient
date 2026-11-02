using System.Numerics;
using Meitou.Rendering;
using Meitou.Rendering.Characters;

namespace Meitou.Game;

/// <summary>
/// F11's character boxes (Meitou only): a checkbox on the statistics' characters line that outlines, over everything, each character its "drawn" count
/// counts (<see cref="CharacterRenderer.CountedBoxes"/>: in the colour view's frustum and not hidden by fog, whether or not anything covers it).
/// </summary>
sealed partial class GameHost
{
    bool characterBoxes;
    // Where the checkbox was drawn last (x0, y0, x1, y1), null while the statistics are hidden.
    Vector4? characterBoxesToggle;
    readonly List<(string Line, Vector4 Rect)> statsLaidOut = [];
    readonly List<Vector4> countedBoxes = [];

    static readonly Vector4 CharacterBoxColour = new(1f, 0.85f, 0.2f, 0.9f);

    /// <summary>Ticks the statistics' checkbox as the switch stands (the lines are built once a second).</summary>
    void TickCharacterBoxes(List<string> stats)
    {
        string from = characterBoxes ? WorldStats.CharacterBoxesOff : WorldStats.CharacterBoxesOn;
        string to = characterBoxes ? WorldStats.CharacterBoxesOn : WorldStats.CharacterBoxesOff;
        for (int i = 0; i < stats.Count; i++)
            if (stats[i].Contains(from, StringComparison.Ordinal)) stats[i] = stats[i].Replace(from, to, StringComparison.Ordinal);
    }

    /// <summary>The checkbox's rectangle in the panel just drawn (from its laid-out lines), or null when it is not there.</summary>
    Vector4? FindCharacterBoxesToggle(float charWidth)
    {
        string marker = characterBoxes ? WorldStats.CharacterBoxesOn : WorldStats.CharacterBoxesOff;
        foreach (var (line, rect) in statsLaidOut)
        {
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            float x0 = rect.X + at * charWidth;
            return new Vector4(x0, rect.Y, x0 + marker.Length * charWidth, rect.W);
        }
        return null;
    }

    /// <summary>A left click on the checkbox flips the boxes; true when it took the click.</summary>
    bool CharacterBoxesClick(Vector2 at)
    {
        if (characterBoxesToggle is not { } t || at.X < t.X || at.X > t.Z || at.Y < t.Y || at.Y > t.W) return false;
        characterBoxes = !characterBoxes;
        return true;
    }

    /// <summary>The counted characters' outlines over the whole picture, seen through the world camera at the display size.</summary>
    void DrawCharacterBoxes(DebugOverlay overlay, CharacterRenderer characters, WorldCamera camera, int width, int height)
    {
        var viewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        characters.CountedBoxes(viewProjection, width, height, countedBoxes);
        const float line = 1.5f;
        foreach (var b in countedBoxes)
        {
            overlay.Rect(b.X, b.Y, b.Z, b.Y + line, CharacterBoxColour);
            overlay.Rect(b.X, b.W - line, b.Z, b.W, CharacterBoxColour);
            overlay.Rect(b.X, b.Y, b.X + line, b.W, CharacterBoxColour);
            overlay.Rect(b.Z - line, b.Y, b.Z, b.W, CharacterBoxColour);
        }
        overlay.Text($"{countedBoxes.Count} counted", 16, height - overlay.LineHeight - 8, CharacterBoxColour);
        overlay.Flush(width, height);
    }
}
