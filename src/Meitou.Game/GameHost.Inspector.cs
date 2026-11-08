using System.Numerics;
using Meitou.Game.Rendering.Overlays;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game;

/// <summary>
/// The pick & collision inspector's menu (Meitou only, docs/viewer.md "Pick & collision inspector"). The F10 debug menu's Tools row opens it;
/// it works like the F10 menu: Up/Down pick a row, Enter or Space switch it, Esc or Enter on Close closes, and while it is open it takes every key. What was switched on stays on
/// after it closes, so the camera can fly about with the hulls drawn.
/// </summary>
sealed partial class GameHost
{
    static readonly string[] InspectorLabels =
    [
        "Info panel (under the cursor, what a click selects)",
        "Pick hull (what a click is tested against)",
        "Collision shapes (each shape's own hull)",
        "Collision mesh (the real triangles, selected building)",
        "Navmesh (off / Meitou's / original / both)",
        "Close",
    ];

    bool inspectorMenuOpen;
    int inspectorRow;

    bool InspectorValue(int row) => row switch
    {
        0 => player.InspectorPanel,
        1 => player.InspectorHull,
        2 => player.InspectorShapes,
        3 => player.InspectorMesh,
        _ => false,
    };

    void InspectorMenuKey(EngineKey key)
    {
        switch (key)
        {
            case EngineKey.Escape: inspectorMenuOpen = false; break;
            case EngineKey.Up: inspectorRow = (inspectorRow + InspectorLabels.Length - 1) % InspectorLabels.Length; break;
            case EngineKey.Down: inspectorRow = (inspectorRow + 1) % InspectorLabels.Length; break;
            case EngineKey.Enter or EngineKey.Space or EngineKey.Left or EngineKey.Right: InspectorSwitch(inspectorRow); break;
        }
    }

    void InspectorSwitch(int row)
    {
        switch (row)
        {
            case 0: player.InspectorPanel = !player.InspectorPanel; break;
            case 1: player.InspectorHull = !player.InspectorHull; break;
            case 2: player.InspectorShapes = !player.InspectorShapes; break;
            case 3: player.InspectorMesh = !player.InspectorMesh; break;
            case 4: ToggleNavOverlay(); break;
            default: inspectorMenuOpen = false; break;
        }
        player.InspectorChanged();
    }

    void DrawInspectorMenu(DebugOverlay o, int width, int height, float top)
    {
        var lines = new List<string>();
        for (int i = 0; i < InspectorLabels.Length; i++)
        {
            string state = i < 4 ? (InspectorValue(i) ? "[x] " : "[ ] ") : i == 4 ? $"[{navView.ToString().ToLowerInvariant()}] " : "    ";
            lines.Add((i == inspectorRow ? "> " : "  ") + state + InspectorLabels[i]);
        }
        const string Hint = "Up/Down pick   Enter/Space switch   Esc close (switches stay on)";
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float pad = 10, x0 = 16, y0 = top + 16;
        int chars = Math.Max(lines.Max(l => l.Length), Hint.Length);
        float w = chars * o.CharWidth + 2 * pad, h = (lines.Count + 4) * o.LineHeight + 2 * pad;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        float y = y0 + pad;
        o.Text("Pick & collision inspector", x0 + pad, y, DebugOverlay.TextColour);
        y += 1.5f * o.LineHeight;
        for (int i = 0; i < lines.Count; i++, y += o.LineHeight)
        {
            if (i == inspectorRow) o.Rect(x0 + 4, y, x0 + w - 4, y + o.LineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            o.Text(lines[i], x0 + pad, y, DebugOverlay.TextColour);
        }
        o.Text(Hint, x0 + pad, y + 0.5f * o.LineHeight, dim);
        o.Flush(width, height);
    }
}
