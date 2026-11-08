using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Game.Building;
using Meitou.Game.Ui;

namespace Meitou.Game;

/// <summary>What the level editor needs from the host: placing and clearing level buildings, what it placed so far, and the save.</summary>
public sealed class LevelEditorServices
{
    /// <summary>Places the confirmed ghosts as level buildings (not construction sites).</summary>
    public required Action<IReadOnlyList<QueuedGhost>> Place { get; init; }
    /// <summary>The buildings placed in this session, which the right click can clear.</summary>
    public required Func<IReadOnlyCollection<BuildingPlacement>> Added { get; init; }
    /// <summary>Clears the building of a key (placed here or of the level data); false when there is none.</summary>
    public required Func<string, bool> Clear { get; init; }
    public required Func<string, bool> IsCleared { get; init; }
    /// <summary>Writes the edits into the editor's mod; returns the message to show.</summary>
    public required Func<string> Save { get; init; }
    public required string ModName { get; init; }
}

/// <summary>
/// The level editor (docs/game/level-editor.md): the original's top bar (<c>Kenshi_LevelEditor</c>) and its BUILDINGS tool, which is build mode's window in an editor mode: every
/// BUILDING of the game data under its category (categories in ordinal order), no placement checks, CONFIRM places level buildings, a right click clears the building under the cursor.
/// The other top-bar tools are not done yet and say so.
/// </summary>
sealed partial class PlayerInterface
{
    LevelEditorServices? levelHost;
    bool levelEditor, levelBuild;
    HashSet<string>? levelEveryBuilding;

    public LevelEditorServices? LevelHost { get => levelHost; set => levelHost = value; }
    public bool LevelEditorOpen => levelEditor;

    /// <summary>Opens the top bar (the F10 menu's Tools); the game keeps running under it.</summary>
    public void OpenLevelEditor()
    {
        if (levelHost is null) return;
        levelEditor = true;
        faithfulScreens.Open("leveleditor");
    }

    /// <summary>The original's <c>editor_toggle</c> (Shift+F12): opens the level editor, or closes it.</summary>
    public void ToggleLevelEditor()
    {
        if (levelEditor) CloseLevelEditor();
        else OpenLevelEditor();
    }

    void CloseLevelEditor()
    {
        if (levelBuild) SetBuildMode(false);
        levelEditor = false;
        faithfulScreens.Close("leveleditor");
    }

    /// <summary>Once a frame, before build mode's update: Esc may have closed the bar.</summary>
    void UpdateLevelEditor()
    {
        if (levelEditor && !faithfulScreens.IsOpen("leveleditor")) CloseLevelEditor();
        if (levelBuild && !buildMode) levelBuild = false;
    }

    /// <summary>Stops placing without leaving the editor or build mode: the list's choice and the ghost go, the queued ghosts stay (Meitou's choice, the original's way is <b>Unknown</b>).</summary>
    void DropPlacementChoice()
    {
        buildDiscarded = true;
        buildMember = null;
        ghost = null;
        wallLine = null;
        wallPieces.Clear();
        faithfulScreens.ClearSelection("build", "BuildingsList");
    }

    /// <summary>Esc in the editor's build window drops the placement choice first; true when there was one (a second Esc closes the window as before).</summary>
    bool LevelEscape()
    {
        if (!levelBuild || !buildMode || CurrentBuilding() is null || faithfulScreens.OpenScreens[^1] != "build") return false;
        DropPlacementChoice();
        return true;
    }

    /// <summary>Every BUILDING counts as available in the editor.</summary>
    IReadOnlySet<string> LevelEveryBuilding() => levelEveryBuilding ??= buildHost is { } host ? [.. host.Menu.Catalog.Select(b => b.Id)] : [];

    /// <summary>The top bar, centred at the top of the screen as in the original (its layout puts it at the left).</summary>
    static MyGuiLayout LevelEditorBar(MyGuiLayout layout) =>
        layout with { Roots = [.. layout.Roots.Select(r => r.RealPosition ? r with { Position = r.Position with { X = (1 - r.Position.Width) / 2 } } : r)] };

    /// <summary>The top bar's tools in layout order: the BUILDINGS tool, Save Mod and Exit work; the rest are not done.</summary>
    static readonly string[] LevelEditorButtons = ["towns", "populate", "buildings", "items", "snaps", "navmesh", "features", "fogfeatures", "save", "exit"];

    void BindLevelEditorScreen(UiBindings b)
    {
        foreach (string name in LevelEditorButtons) { b.Disabled.Remove(name); b.Actions[name] = name; }
        b.Text["snaps"] = "Fix stuff";   // the exe sets this caption over the layout's "Update Snaps"
        b.Text["mod"] = levelHost?.ModName ?? "";
    }

    void LevelEditorAction(MyGuiEvent e)
    {
        switch (e.Widget)
        {
            case "buildings":
                if (levelBuild) { SetBuildMode(false); break; }
                if (buildMode) SetBuildMode(false);
                levelBuild = true;
                SetBuildMode(true);
                if (!buildMode) levelBuild = false;
                break;
            case "save": if (levelHost is { } host) Notice(host.Save()); break;
            case "exit": CloseLevelEditor(); break;
            default:
                string caption = e.Widget == "snaps" ? "Fix stuff" : faithfulScreens.Layout("leveleditor").Find(e.Widget)?.Properties.GetValueOrDefault("Caption") ?? e.Widget;
                Notice($"{caption}: not in Meitou yet");
                break;
        }
    }

    /// <summary>The COMMANDS box in the editor: the original's lines (exe strings; the screenshot shows them in this order) with Meitou's keys.</summary>
    static readonly string[] LevelCommandLines =
    [
        "Place Building: Left-Click", "Clear Building: Right-Click", "Rotate: < > or , . keys", "Change Height: + and -", "Change Slope: [ and ]", "Undo: Backspace", "Cancel All: Esc",
        "Press [Confirm] when finished",
    ];

    void LevelConfirm()
    {
        if (levelHost is null || buildQueue.Count == 0) return;
        Sound("Building_Confirm");
        levelHost.Place([.. buildQueue]);
        buildQueue.Clear();
    }

    /// <summary>
    /// A right click with the editor's BUILDINGS tool open: drops the queued ghost under the cursor, else clears the nearest building the ray meets before the ground (one placed in this
    /// session or a level-data one), and a right click on nothing drops the placement choice. True: the click is the editor's.
    /// </summary>
    bool LevelClearAt(Vector2 p)
    {
        if (!levelBuild || levelHost is not { } host || Ray(p) is not { } ray) return false;
        var groundPoint = GroundHit(ray.Origin, ray.Direction);
        if (groundPoint is { } g && buildQueue.FindLastIndex(q => q.Hull is { } hull && Inside(hull, new Vector2(g.X, g.Z))) is >= 0 and var queued)
        {
            Sound("Building_Undo");
            buildQueue.RemoveAt(queued);
            return true;
        }
        float bestT = groundPoint is { } hit ? Vector3.Distance(ray.Origin, hit) : 6000;
        string? best = null;
        foreach (var b in host.Added())
            if (buildHost?.Placement.Footprint(b.BuildingId) is { } footprint && b.WorldY is { } y
                && BuildingRayT(ray, footprint.Place(b.Position, b.Rotation), footprint.YMin, footprint.YMax, b.BuildingId, b.Position, y, b.Rotation) is { } t && t < bestT)
                (bestT, best) = (t, b.Key);
        if (best is null && PickBuilding(p) is { SiteId: null, Record: not null } level) best = level.Instance;
        if (best is null) DropPlacementChoice();
        else if (host.Clear(best))
        {
            if (selectedBuilding?.Instance == best) SetBuilding(null);
            Sound("Building_Undo");
        }
        return true;

        static bool Inside(Vector2[] polygon, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                if ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)
                    && point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                    inside = !inside;
            return inside;
        }
    }
}
