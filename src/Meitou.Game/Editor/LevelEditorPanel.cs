using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Game.Building;
using Meitou.Game.Ui;

namespace Meitou.Game.Editor;

/// <summary>
/// What the level editor needs from the host: placing and clearing level buildings, what it placed so far, and the save.
/// </summary>
public sealed class LevelEditorServices
{
    /// <summary>
    /// Places the confirmed ghosts as level buildings (not construction sites).
    /// </summary>
    public required Action<IReadOnlyList<QueuedGhost>> Place { get; init; }
    /// <summary>
    /// The buildings placed in this session, which the right click can clear.
    /// </summary>
    public required Func<IReadOnlyCollection<BuildingPlacement>> Added { get; init; }
    /// <summary>
    /// Clears the building of a key (placed here or of the level data); false when there is none.
    /// </summary>
    public required Func<string, bool> Clear { get; init; }
    public required Func<string, bool> IsCleared { get; init; }
    /// <summary>
    /// Writes the edits into the editor's mod; returns the message to show.
    /// </summary>
    public required Func<string> Save { get; init; }
    public required string ModName { get; init; }
}

/// <summary>
/// The level editor (docs/game/level-editor.md): the original's top bar (<c>Kenshi_LevelEditor</c>) and its BUILDINGS tool, which is build mode's
/// window in an editor mode: every BUILDING of the game data under its category (categories in ordinal order), no placement checks, CONFIRM places
/// level buildings, a right click clears the building under the cursor. The other top-bar tools are not done yet and say so.
/// </summary>
sealed class LevelEditorPanel(PlayerInterface player)
{
    internal LevelEditorServices? Services;
    internal bool LevelEditor, LevelBuild;
    HashSet<string>? _levelEveryBuilding;

    public LevelEditorServices? LevelHost { get => Services; set => Services = value; }
    public bool LevelEditorOpen => LevelEditor;

    /// <summary>
    /// Opens the top bar (the F10 menu's Tools); the game keeps running under it.
    /// </summary>
    public void OpenLevelEditor()
    {
        if (Services is null) return;
        LevelEditor = true;
        player.FaithfulScreens.Open("leveleditor");
    }

    /// <summary>
    /// The original's <c>editor_toggle</c> (Shift+F12): opens the level editor, or closes it.
    /// </summary>
    public void ToggleLevelEditor()
    {
        if (LevelEditor) CloseLevelEditor();
        else OpenLevelEditor();
    }

    void CloseLevelEditor()
    {
        if (LevelBuild) player.Build.SetBuildMode(false);
        LevelEditor = false;
        player.FaithfulScreens.Close("leveleditor");
    }

    /// <summary>
    /// Once a frame, before build mode's update: Esc may have closed the bar.
    /// </summary>
    internal void UpdateLevelEditor()
    {
        if (LevelEditor && !player.FaithfulScreens.IsOpen("leveleditor")) CloseLevelEditor();
        if (LevelBuild && !player.Build.Active) LevelBuild = false;
    }

    /// <summary>
    /// Stops placing without leaving the editor or build mode: the list's choice and the ghost go, the queued ghosts stay (Meitou's choice, the
    /// original's way is <b>Unknown</b>).
    /// </summary>
    internal void DropPlacementChoice()
    {
        player.Build.BuildDiscarded = true;
        player.Build.BuildMember = null;
        player.Build.Ghost = null;
        player.Build.WallLine = null;
        player.Build.WallPieces.Clear();
        player.FaithfulScreens.ClearSelection("build", "BuildingsList");
    }

    /// <summary>
    /// Esc in the editor's build window drops the placement choice first; true when there was one (a second Esc closes the window as before).
    /// </summary>
    internal bool LevelEscape()
    {
        if (!LevelBuild || !player.Build.Active || player.Build.CurrentBuilding() is null || player.FaithfulScreens.OpenScreens[^1] != "build")
            return false;
        DropPlacementChoice();
        return true;
    }

    /// <summary>
    /// Every BUILDING counts as available in the editor.
    /// </summary>
    internal IReadOnlySet<string> LevelEveryBuilding() => _levelEveryBuilding ??= player.Build.Services is { } host
        ? [.. host.Menu.Catalog.Select(b => b.Id)] : [];

    /// <summary>
    /// The top bar, centred at the top of the screen as in the original (its layout puts it at the left).
    /// </summary>
    internal static MyGuiLayout LevelEditorBar(MyGuiLayout layout) =>
        layout with { Roots = [.. layout.Roots.Select(r => r.RealPosition
            ? r with { Position = r.Position with { X = (1 - r.Position.Width) / 2 } } : r)] };

    /// <summary>
    /// The top bar's tools in layout order: the BUILDINGS tool, Save Mod and Exit work; the rest are not done.
    /// </summary>
    static readonly string[] LevelEditorButtons = ["towns", "populate", "buildings", "items", "snaps", "navmesh", "features",
        "fogfeatures", "save", "exit"];

    internal void BindLevelEditorScreen(UiBindings b)
    {
        foreach (string name in LevelEditorButtons)
        {
            b.Disabled.Remove(name);
            b.Actions[name] = name;
        }
        b.Text["snaps"] = "Fix stuff";   // the exe sets this caption over the layout's "Update Snaps"
        b.Text["mod"] = Services?.ModName ?? "";
    }

    internal void LevelEditorAction(MyGuiEvent e)
    {
        switch (e.Widget)
        {
            case "buildings":
                if (LevelBuild)
                {
                    player.Build.SetBuildMode(false);
                    break;
                }
                if (player.Build.Active) player.Build.SetBuildMode(false);
                LevelBuild = true;
                player.Build.SetBuildMode(true);
                if (!player.Build.Active) LevelBuild = false;
                break;
            case "save": if (Services is { } host) player.Notice(host.Save()); break;
            case "exit": CloseLevelEditor();
            break;
            default:
                string caption = e.Widget == "snaps" ? "Fix stuff"
                    : player.FaithfulScreens.Layout("leveleditor").Find(e.Widget)?.Properties.GetValueOrDefault("Caption") ?? e.Widget;
                player.Notice($"{caption}: not in Meitou yet");
                break;
        }
    }

    /// <summary>
    /// The COMMANDS box in the editor: the original's lines (exe strings; the screenshot shows them in this order) with Meitou's keys.
    /// </summary>
    internal static readonly string[] LevelCommandLines =
    [
        "Place Building: Left-Click", "Clear Building: Right-Click", "Rotate: < > or , . keys", "Change Height: + and -", "Change Slope: [ and ]",
        "Undo: Backspace", "Cancel All: Esc",
        "Press [Confirm] when finished",
    ];

    internal void LevelConfirm()
    {
        if (Services is null || player.Build.BuildQueue.Count == 0) return;
        player.InterfaceSounds.Sound("Building_Confirm");
        Services.Place([.. player.Build.BuildQueue]);
        player.Build.BuildQueue.Clear();
    }

    /// <summary>
    /// A right click with the editor's BUILDINGS tool open: drops the queued ghost under the cursor, else clears the nearest building the ray meets
    /// before the ground (one placed in this session or a level-data one), and a right click on nothing drops the placement choice. True: the click
    /// is the editor's.
    /// </summary>
    internal bool LevelClearAt(Vector2 p)
    {
        if (!LevelBuild || Services is not { } host || player.Ray(p) is not { } ray) return false;
        var groundPoint = player.Picking.GroundHit(ray.Origin, ray.Direction);
        if (groundPoint is { } g
                && player.Build.BuildQueue.FindLastIndex(q => q.Hull is { } hull && Inside(hull, new Vector2(g.X, g.Z))) is >= 0 and var queued)
        {
            player.InterfaceSounds.Sound("Building_Undo");
            player.Build.BuildQueue.RemoveAt(queued);
            return true;
        }
        float bestT = groundPoint is { } hit ? Vector3.Distance(ray.Origin, hit) : 6000;
        string? best = null;
        foreach (var b in host.Added())
            if (player.Build.Services?.Placement.Footprint(b.BuildingId) is { } footprint && b.WorldY is { } y
                && player.Picking.BuildingRayT(ray, footprint.Place(b.Position, b.Rotation), footprint.YMin, footprint.YMax, b.BuildingId, b.Position,
                    y, b.Rotation) is { } t && t < bestT)
                (bestT, best) = (t, b.Key);
        if (best is null && player.BuildingSelection.PickBuilding(p) is { SiteId: null, Record: not null } level) best = level.Instance;
        if (best is null) DropPlacementChoice();
        else if (host.Clear(best))
        {
            if (player.BuildingSelection.Selected?.Instance == best) player.BuildingSelection.SetBuilding(null);
            player.InterfaceSounds.Sound("Building_Undo");
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