using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Game.Rendering.Overlays;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.Editor;

/// <summary>
/// The interior editor (Meitou's take on the original's level-editor Layout window, docs/game/interior-editor.md), opened from the F10 menu's Tools.
/// Read only for now: it shows the layouts of the selected building's type (interior: furniture, exterior: signs and NPCs, as the original's two
/// lists), the parts of one layout, and marks them on the screen. Up/Down pick, Tab switches interior / exterior, Enter opens a layout's parts, C
/// centres the camera on the picked part (the original's "->"), Esc goes back and closes. While open it takes every key; a left click still selects
/// another building, which the editor then shows.
/// </summary>
sealed class InteriorEditorHost(GameHost host)
{
    /// <summary>
    /// Whether the interior editor is open (it then takes every key).
    /// </summary>
    internal bool InteriorEditorOpen;
    /// <summary>
    /// Whether the exterior layouts (signs and NPCs) are listed rather than the interior ones (furniture); Tab switches.
    /// </summary>
    bool _interiorEditorExterior;
    /// <summary>
    /// The picked row of the layout list.
    /// </summary>
    int _interiorEditorRow;
    /// <summary>
    /// The layout whose parts are listed (null: the layout list is shown) and the part picked in it.
    /// </summary>
    GameRecord? _interiorEditorLayout;
    /// <summary>
    /// The picked part in the open layout's list.
    /// </summary>
    int _interiorEditorPart;
    /// <summary>
    /// The placement key of the building shown; another selected building starts over at its layout list.
    /// </summary>
    string? _interiorEditorKey;
    /// <summary>
    /// The level data's buildings by key, for the level data they were read from.
    /// </summary>
    (WorldLevelData Levels, int Revision, Dictionary<string, BuildingPlacement> ByKey)? _interiorEditorPlacements;

    /// <summary>
    /// The rows a list shows at once, round the picked one.
    /// </summary>
    const int InteriorEditorRows = 18;

    /// <summary>
    /// The level building the editor shows: the selected one, with its state record (null for a player's site or nothing selected).
    /// </summary>
    (ResolvedBuilding Building, GameRecord? State)? InteriorEditorBuilding()
    {
        if (host.Player.BuildingSelection.SelectedBuildingKey is not { } key || host.Scene.Objects is not { } objects) return null;
        if (_interiorEditorPlacements?.Levels != objects.Levels || _interiorEditorPlacements?.Revision != objects.Levels.EditRevision)
        {
            _interiorEditorPlacements = (objects.Levels, objects.Levels.EditRevision, objects.Levels.Buildings()
                .GroupBy(b => b.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        }
        if (!_interiorEditorPlacements.Value.ByKey.TryGetValue(key, out var placement)) return null;
        if (BuildingPlacements.Resolve(placement, objects.Database, objects.Levels,
                (x, z) => host.Gpu.Terrain.HeightAt((float)x, (float)z)) is not { } resolved)
            return null;
        var state = placement.StateId is { } id && objects.Levels.Zones.TryGetValue(placement.Zone, out var zone) ? zone.Find(id) : null;
        return (resolved, state);
    }

    /// <summary>
    /// The layouts of the building's type (its own, then those of the buildings it shares interiors with), interior or exterior.
    /// </summary>
    internal List<GameRecord> InteriorEditorLayouts(GameRecord building, bool exterior) =>
        host.Scene.Objects is not { } objects ? []
        : [.. objects.Layouts.Names(building, exterior).Distinct(StringComparer.Ordinal).Select(n => objects.Layouts.Find(building, n, exterior))
            .OfType<GameRecord>().Distinct()];

    /// <summary>
    /// The open layout's parts; empty without one or without world objects.
    /// </summary>
    List<LayoutItem> InteriorEditorParts() =>
        _interiorEditorLayout is { } layout && host.Scene.Objects is { } objects ? [.. objects.Layouts.Items(layout)] : [];

    /// <summary>
    /// Opens the interior editor from the F10 menu's Tools, closing the menu.
    /// </summary>
    internal void OpenInteriorEditor()
    {
        host.DebugMenu.DebugMenuOpen = false;
        InteriorEditorOpen = true;
    }

    /// <summary>
    /// A key while the editor is open: Up/Down pick, Tab switches interior / exterior, Enter opens a layout's parts, C centres the camera on the
    /// picked part, Esc goes back and closes.
    /// </summary>
    internal void InteriorEditorKey(EngineKey key)
    {
        var shown = InteriorEditorBuilding();
        int count = _interiorEditorLayout is not null ? InteriorEditorParts().Count
            : shown is { } s ? InteriorEditorLayouts(s.Building.Record, _interiorEditorExterior).Count
            : 0;
        ref int row = ref _interiorEditorLayout is not null ? ref _interiorEditorPart : ref _interiorEditorRow;
        switch (key)
        {
            case EngineKey.Escape when _interiorEditorLayout is not null:
                _interiorEditorLayout = null;
                break;
            case EngineKey.Escape:
                InteriorEditorOpen = false;
                break;
            case EngineKey.Up when count > 0:
                row = (row + count - 1) % count;
                break;
            case EngineKey.Down when count > 0:
                row = (row + 1) % count;
                break;
            case EngineKey.PageUp:
                row = Math.Max(0, row - InteriorEditorRows);
                break;
            case EngineKey.PageDown when count > 0:
                row = Math.Min(count - 1, row + InteriorEditorRows);
                break;
            case EngineKey.Tab or EngineKey.Left or EngineKey.Right when _interiorEditorLayout is null:
                _interiorEditorExterior = !_interiorEditorExterior;
                _interiorEditorRow = 0;
                break;
            case EngineKey.Enter when _interiorEditorLayout is null && shown is { } open && row < count:
                _interiorEditorLayout = InteriorEditorLayouts(open.Building.Record, _interiorEditorExterior)[row];
                _interiorEditorPart = 0;
                break;
            case EngineKey.C when shown is { } centre:
                var at = _interiorEditorLayout is not null && InteriorEditorParts() is { Count: > 0 } parts
                    ? BuildingLayouts.Place(centre.Building.Position, centre.Building.Placement.Rotation,
                        parts[Math.Min(_interiorEditorPart, parts.Count - 1)]).Position
                    : centre.Building.Position;
                host.Session.Camera.MovePivot(new Vector2(at.X, at.Z));
                host.Player.Follow = null;
                break;
        }
    }

    /// <summary>
    /// Draws the editor's panel: the selected building's layout list, or the open layout's parts with their markers on the screen.
    /// </summary>
    internal void DrawInteriorEditor(DebugOverlay o, int width, int height, float top)
    {
        var shown = InteriorEditorBuilding();
        // Another building was selected: start over at its layout list.
        string? key = shown?.Building.Placement.Key;
        if (key != _interiorEditorKey)
        {
            _interiorEditorKey = key;
            _interiorEditorLayout = null;
            _interiorEditorRow = 0;
        }
        var lines = new List<(string Text, bool Picked)>();
        string title, hint;
        if (shown is not { } s)
        {
            title = "Interior editor";
            lines.Add((host.Player.BuildingSelection.SelectedBuildingKey is null ? "Click a building to show its layouts."
                : "Not a building of the level data (a site of yours has no layouts).", false));
            hint = "Esc close";
        }
        else
        {
            var record = s.Building.Record;
            string inUse = s.State?.GetString(_interiorEditorExterior ? "exterior layout name" : "interior layout name") ?? "";
            title = $"Interior editor   {record.Name} ({record.StringId})";
            if (_interiorEditorLayout is not { } layout)
            {
                ListLayouts(record, inUse, lines);
                hint = "Up/Down pick   Enter parts   Tab interior/exterior   C centre   Esc close";
            }
            else
            {
                var parts = ListParts(layout, inUse, lines);
                hint = "Up/Down pick   C centre on part   Esc back";
                DrawInteriorMarkers(o, width, height, s.Building, parts);
            }
        }
        DrawEditorPanel(o, width, height, top, title, hint, lines);
    }

    /// <summary>
    /// The layout list's rows: the side shown and the layout in use, then the layouts of the building's type around the picked one.
    /// </summary>
    void ListLayouts(GameRecord record, string inUse, List<(string Text, bool Picked)> lines)
    {
        var layouts = InteriorEditorLayouts(record, _interiorEditorExterior);
        _interiorEditorRow = Math.Clamp(_interiorEditorRow, 0, Math.Max(0, layouts.Count - 1));
        string side = _interiorEditorExterior ? "EXTERIOR (signs and NPCs)" : "INTERIOR (furniture)";
        lines.Add(($"{side}   this building uses: {(inUse.Length > 0 ? inUse : "none")}", false));
        if (layouts.Count == 0) lines.Add(("  no layouts for this building type", false));
        foreach (int i in Window(_interiorEditorRow, layouts.Count))
        {
            var l = layouts[i];
            string owner = l.GetString("associated building") is var a && a != record.StringId ? $"   shared from {RecordName(a)}" : "";
            string flags = (l.GetBool("is bar") ? " bar" : "") + (l.GetBool("is shop") ? " shop" : "");
            int count = l.Instances.Values.Count(x => !x.IsCleared);
            lines.Add(($"{(l.Name == inUse ? "*" : " ")} {l.Name,-28} {count,4} parts{flags}{owner}", i == _interiorEditorRow));
        }
    }

    /// <summary>
    /// The open layout's rows: its name, then its parts around the picked one with their floor, place relative to the building and yaw; returns
    /// the parts.
    /// </summary>
    List<LayoutItem> ListParts(GameRecord layout, string inUse, List<(string Text, bool Picked)> lines)
    {
        var parts = InteriorEditorParts();
        _interiorEditorPart = Math.Clamp(_interiorEditorPart, 0, Math.Max(0, parts.Count - 1));
        string used = layout.Name == inUse ? "   in use here" : "";
        lines.Add(($"{layout.Name} ({layout.StringId}){used}   {parts.Count} parts, positions relative to the building", false));
        if (parts.Count == 0) lines.Add(("  no parts", false));
        foreach (int i in Window(_interiorEditorPart, parts.Count))
        {
            var p = parts[i];
            float yaw = 2 * MathF.Atan2(p.Rotation.Y, p.Rotation.W) * 180 / MathF.PI;
            string text = $"{p.InstanceId,-6} {Fit(p.Building.Name, 30),-30} floor {p.Floor}  "
                + $"{F(p.Position.X),7} {F(p.Position.Y),6} {F(p.Position.Z),7}  yaw {F(yaw),4}";
            lines.Add((text, i == _interiorEditorPart));
        }
        return parts;
    }

    /// <summary>
    /// The editor's panel at the top left: the title, the rows (the picked one highlighted) and the key hint.
    /// </summary>
    static void DrawEditorPanel(DebugOverlay o, int width, int height, float top, string title, string hint, List<(string Text, bool Picked)> lines)
    {
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float pad = 10, x0 = 16, y0 = top + 16;
        int chars = Math.Max(Math.Max(title.Length, hint.Length), lines.Max(l => l.Text.Length) + 2);
        float w = chars * o.CharWidth + 2 * pad, h = (lines.Count + 4) * o.LineHeight + 2 * pad;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        float y = y0 + pad;
        o.Text(title, x0 + pad, y, DebugOverlay.TextColour);
        y += 1.5f * o.LineHeight;
        foreach (var (text, picked) in lines)
        {
            if (picked) o.Rect(x0 + 4, y, x0 + w - 4, y + o.LineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            o.Text((picked ? "> " : "  ") + text, x0 + pad, y, DebugOverlay.TextColour);
            y += o.LineHeight;
        }
        o.Text(hint, x0 + pad, y + 0.5f * o.LineHeight, dim);
        o.Flush(width, height);
    }

    /// <summary>
    /// A record's name, its id when the data has no such record.
    /// </summary>
    string RecordName(string id) => host.Scene.Database?.Find(id)?.Name ?? id;

    /// <summary>
    /// A coordinate to one decimal.
    /// </summary>
    static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="s"/> cut to <paramref name="n"/> characters, a cut one ending in ~.
    /// </summary>
    static string Fit(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";

    /// <summary>
    /// The rows of a list of <paramref name="count"/> shown round the <paramref name="picked"/> one, at most <see cref="InteriorEditorRows"/>.
    /// </summary>
    static IEnumerable<int> Window(int picked, int count)
    {
        int first = Math.Clamp(picked - InteriorEditorRows / 2, 0, Math.Max(0, count - InteriorEditorRows));
        return Enumerable.Range(first, Math.Min(InteriorEditorRows, count - first));
    }

    /// <summary>
    /// Each part of the open layout as a dot where it stands on the screen, the picked one as a bigger square with its name.
    /// </summary>
    void DrawInteriorMarkers(DebugOverlay o, int width, int height, ResolvedBuilding building, List<LayoutItem> parts)
    {
        var view = host.Camera.View * host.Camera.Projection(width / (float)Math.Max(height, 1), host.Camera.Near, host.Camera.ViewDistance);
        for (int i = 0; i < parts.Count; i++)
        {
            var at = BuildingLayouts.Place(building.Position, building.Placement.Rotation, parts[i]).Position;
            var clip = Vector4.Transform(new Vector4(at, 1), view);
            if (clip.W <= 0.1f) continue;
            float x = (clip.X / clip.W * 0.5f + 0.5f) * width, y = (0.5f - clip.Y / clip.W * 0.5f) * height;
            if (x < -10 || y < -10 || x > width + 10 || y > height + 10) continue;
            if (i != _interiorEditorPart)
            {
                o.Rect(x - 3, y - 3, x + 3, y + 3, new Vector4(1f, 0.75f, 0.2f, 0.8f));
                continue;
            }
            o.Rect(x - 7, y - 7, x + 7, y + 7, new Vector4(1f, 0.15f, 0.1f, 0.95f));
            o.Rect(x - 4, y - 4, x + 4, y + 4, new Vector4(1f, 1f, 1f, 1f));
            o.Text(parts[i].Building.Name, x + 10, y - o.LineHeight / 2, new Vector4(1f, 0.9f, 0.4f, 1));
        }
    }
}