using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.Textures;
using Meitou.Data.Ui;
using Meitou.Game.Building;
using Meitou.Game.DevTools;
using Meitou.Game.DevTools.Agents;
using Meitou.Game.Editor;
using Meitou.Game.Effects;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Title;
using Meitou.Game.Tutorials;
using Meitou.Game.Ui;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The player's hands and the minimal HUD (docs/game/ui-input.md): click or box select, right click move orders (shift queues; holding the
/// button steers: the order follows the cursor),
/// the squad keys, the clock and speed buttons. Everything the player does becomes a <see cref="SimCommand"/> at the world's
/// current tick; this class never touches the simulation's state, it reads the published snapshot.
/// Positions are framebuffer pixels, top-left origin. The host sets <see cref="ViewProjection"/>, <see cref="Eye"/>, <see cref="Width"/>
/// and <see cref="Height"/> each frame (events arrive on the same thread, between frames).
/// </summary>
sealed partial class PlayerInterface(WorldSession session, Func<float, float, float> ground, Func<CharacterSnapshot, Vector3> where)
{
    #region Features

    /// <summary>
    /// The session whose snapshots the interface reads and whose world takes its commands.
    /// </summary>
    internal readonly WorldSession Session = session;
    /// <summary>
    /// The ground height at a point.
    /// </summary>
    internal readonly Func<float, float, float> Ground = ground;
    /// <summary>
    /// Where a character is drawn (between ticks).
    /// </summary>
    internal readonly Func<CharacterSnapshot, Vector3> Where = where;

    /// <summary>
    /// The message panel (the original's MessageRoller, docs/game/ui-screens.md "Messages").
    /// </summary>
    internal MessageRoller MessageRoller => field ??= new(this);
    /// <summary>
    /// The Overview's DIALOGUE log (docs/game/ui-screens.md "Dialogue log").
    /// </summary>
    internal DialogueLog DialogueLog => field ??= new(this);
    /// <summary>
    /// The HUD's text panels as the original fills them (docs/game/ui-screens.md section 4).
    /// </summary>
    internal HudPanels HudPanels => field ??= new(this);
    /// <summary>
    /// The HUD's orders panel (docs/game/ui-screens.md section 4).
    /// </summary>
    internal OrdersPanel OrdersPanel => field ??= new(this);
    /// <summary>
    /// The movement marker (docs/game/ui-input.md "Movement marker").
    /// </summary>
    internal MoveMarker MoveMarker => field ??= new(this);
    /// <summary>
    /// The inventory windows' geometry.
    /// </summary>
    internal InventoryLayout InventoryLayout => field ??= new(this);
    /// <summary>
    /// The scrollbars of scrolled windows and HUD lists (Kenshi_ScrollBarV).
    /// </summary>
    internal ScrollBars ScrollBars => field ??= new(this);
    /// <summary>
    /// The item cells of the inventory, trader and machine grids.
    /// </summary>
    internal ItemCells ItemCells => field ??= new(this);
    /// <summary>
    /// Routes the faithful screens to the features that own them.
    /// </summary>
    internal ScreenRouter ScreenRouter => field ??= new(this);
    /// <summary>
    /// The context menu (the original's ContextMenuGUI, Kenshi_ContextMenu).
    /// </summary>
    internal ContextMenuWindow ContextMenuWindow => field ??= new(this);
    /// <summary>
    /// The front-end windows (docs/game/ui-screens.md).
    /// </summary>
    internal FrontendScreens Frontend => field ??= new(this);
    /// <summary>
    /// The world's picking: what the pointer is over.
    /// </summary>
    internal WorldPicking Picking => field ??= new(this);
    /// <summary>
    /// The mouse pointer and the hover outline.
    /// </summary>
    internal PointerView PointerPanel => field ??= new(this);
    /// <summary>
    /// The character editor (CharacterEditWindow, layout Kenshi_CharacterEditor).
    /// </summary>
    internal CharacterEditorPanel CharacterEditor => field ??= new(this);
    /// <summary>
    /// Production in the UI (docs/simulation.md "Production", docs/game/ui-screens.md "Selected building").
    /// </summary>
    internal ProductionWindows Production => field ??= new(this);
    /// <summary>
    /// Build mode (docs/game/buildings-production.md "Placement" and "Build menu", docs/simulation.md "Buildings").
    /// </summary>
    internal BuildMode Build => field ??= new(this);
    /// <summary>
    /// Clicking a building (docs/game/ui-input.md "Building picking and selection", docs/game/ui-screens.md "Selected building").
    /// </summary>
    internal BuildingSelection BuildingSelection => field ??= new(this);
    /// <summary>
    /// What a click does with the furniture and fixtures of a building.
    /// </summary>
    internal FurnitureUse Furniture => field ??= new(this);
    /// <summary>
    /// What a right click does with a town building that is a machine (a farm, a mine).
    /// </summary>
    internal TownMachineUse TownMachines => field ??= new(this);
    /// <summary>
    /// Squad management (docs/game/ui-screens.md "Squads tab", the original's SquadManagementScreen).
    /// </summary>
    internal SquadPanel SquadWindows => field ??= new(this);
    /// <summary>
    /// The Overview's faction tab (FactionsScreen, docs/game/ui-screens.md "Faction tab").
    /// </summary>
    internal FactionWindow FactionWindow => field ??= new(this);
    /// <summary>
    /// The squad bar's portraits (both UI modes).
    /// </summary>
    internal PortraitBar PortraitBar => field ??= new(this);

    /// <summary>
    /// The portraits of CHARACTER records that code mods show.
    /// </summary>
    internal RecordPortraits RecordPortraits => field ??= new(this);
    /// <summary>
    /// The inventory windows (docs/game/ui-screens.md section 6).
    /// </summary>
    internal InventoryWindows InventoryWindows => field ??= new(this);
    /// <summary>
    /// The tooltip runtime (docs/game/ui-screens.md "Tooltips").
    /// </summary>
    internal TooltipRuntime TooltipRuntime => field ??= new(this);
    /// <summary>
    /// The game view's hotkeys (docs/game/ui-input.md sections 4 and 6).
    /// </summary>
    internal GameHotkeys GameHotkeys => field ??= new(this);
    /// <summary>
    /// Conversations (docs/game/dialogue.md "UI", docs/simulation.md "Dialogue"), the same in both UI modes.
    /// </summary>
    internal DialogueWindow DialogueWindow => field ??= new(this);
    /// <summary>
    /// The character's stats window (docs/game/ui-screens.md).
    /// </summary>
    internal StatsWindow StatsWindow => field ??= new(this);
    /// <summary>
    /// The interface's sounds (docs/audio.md "Interface").
    /// </summary>
    internal InterfaceSounds InterfaceSounds => field ??= new(this);
    /// <summary>
    /// The biome banner (docs/game/ui-screens.md "Biome banner", MainBarGUI::vfunc_10).
    /// </summary>
    internal BiomeBanner BiomeBanner => field ??= new(this);
    /// <summary>
    /// The HUD's town panel (TownPanel of Kenshi_MainPanel, docs/game/ui-screens.md "Town panel").
    /// </summary>
    internal TownPanel TownPanel => field ??= new(this);
    /// <summary>
    /// Loose items under the pointer (docs/simulation.md "Ground items").
    /// </summary>
    internal GroundItemPicks GroundItemPicks => field ??= new(this);
    /// <summary>
    /// The New game window's advanced options (docs/game/character-stats.md).
    /// </summary>
    internal AdvancedOptions AdvancedOptions => field ??= new(this);
    /// <summary>
    /// The interface's side of the agent control channel (docs/procedures/agent-play-api.md).
    /// </summary>
    internal AgentOrders AgentOrders => field ??= new(this);
    /// <summary>
    /// The pick &amp; collision inspector (Meitou only).
    /// </summary>
    internal PickInspector Inspector => field ??= new(this);
    /// <summary>
    /// Meitou's feedback button, always at the top right in both UI modes, and the form it opens.
    /// </summary>
    internal FeedbackPanel FeedbackPanel => field ??= new(this);
    /// <summary>
    /// The level editor (docs/game/level-editor.md).
    /// </summary>
    internal LevelEditorPanel LevelEditorPanel => field ??= new(this);
    /// <summary>
    /// The tutorial panel and window (TutorialGUI, docs/game/ui-screens.md "Tutorials").
    /// </summary>
    internal TutorialPanel TutorialWindow => field ??= new(this);
    /// <summary>
    /// The Meitou title's main menu, after the Meitou UI kit (docs/meitou-ui.md, "Layout and states").
    /// </summary>
    internal TitleMenu TitleMenu => field ??= new(this);
    /// <summary>
    /// Speech bubbles over the characters that speak (docs/game/dialogue.md "Ambient conversations", the bubble).
    /// </summary>
    internal SpeechBubbles SpeechBubbles => field ??= new(this);
    /// <summary>
    /// Damage and healing numbers over the characters (docs/game/ui-screens.md "Damage floaters").
    /// </summary>
    internal DamageFloaters DamageFloaters => field ??= new(this);
    /// <summary>
    /// The Prospect button and the Prospecting Results window (docs/game/ui-screens.md "Prospecting").
    /// </summary>
    internal ProspectingPanel Prospecting => field ??= new(this);
    /// <summary>
    /// The stealth indicators of a sneaking player character (docs/game/perception.md "Spotting a sneaker").
    /// </summary>
    internal StealthView StealthView => field ??= new(this);
    /// <summary>
    /// The world map (Overview's MAP tab), after the original's MapScreen (docs/game/ui-screens.md "Map").
    /// </summary>
    internal WorldMap WorldMap => field ??= new(this);
    /// <summary>
    /// Trading (docs/simulation.md "Trade", docs/game/ui-screens.md section 6), shown while the world publishes a trade (TradeView), the same in both
    /// UI modes.
    /// </summary>
    internal TradeWindows TradeWindows => field ??= new(this);

    #endregion

    #region View

    /// <summary>
    /// The original's standard text at 1080 p: <c>Kenshi_StandardFont_Medium</c>, 20 pt at 50 dpi (docs/game/ui-screens.md).
    /// </summary>
    internal const float StandardTextPixels = 20 * 50 / 72f;
    /// <summary>
    /// The camera's view-projection, set by the host each frame.
    /// </summary>
    public Matrix4x4 ViewProjection = Matrix4x4.Identity;
    /// <summary>
    /// The camera's eye, set by the host each frame.
    /// </summary>
    public Vector3 Eye;
    /// <summary>
    /// The framebuffer's size in pixels, set each frame.
    /// </summary>
    public int Width = 1, Height = 1;

    /// <summary>
    /// The picking ray through a pixel: the near-plane point and the unit direction.
    /// </summary>
    public (Vector3 Origin, Vector3 Direction)? Ray(Vector2 pixel)
    {
        if (!Matrix4x4.Invert(ViewProjection, out var inverse)) return null;
        Vector3 At(float z)
        {
            var p = Vector4.Transform(new Vector4(pixel.X / Width * 2 - 1, 1 - pixel.Y / Height * 2, z, 1), inverse);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
        var near = At(DepthConvention.NearZ);
        var far = At(DepthConvention.FarZ);
        return (near, Vector3.Normalize(far - near));
    }

    /// <summary>
    /// A pixel for a world point, or null behind the camera.
    /// </summary>
    public Vector2? Project(Vector3 p)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), ViewProjection);
        if (clip.W <= 0.1f) return null;
        return new Vector2((clip.X / clip.W * 0.5f + 0.5f) * Width, (0.5f - clip.Y / clip.W * 0.5f) * Height);
    }

    /// <summary>
    /// Whether a building seen from outside, or a shown floor or wall of one in the interior view, stands on the ray before
    /// <paramref name="distance"/>: what lies inside, behind or below it is not pointed at.
    /// </summary>
    internal bool Hidden((Vector3 Origin, Vector3 Direction) ray, float distance) =>
        Picking.ClosedBuildingHit?.Invoke(ray.Origin, ray.Direction, distance) is not null
            || Picking.ShownInteriorHit?.Invoke(ray.Origin, ray.Direction, distance) is not null;

    /// <summary>
    /// The HUD's height at the bottom of the screen, pixels.
    /// </summary>
    public float HudBottom => 48;

    /// <summary>
    /// Whether a point is over the interface.
    /// </summary>
    internal bool OverHud(Vector2 p) => ContainsUi(p);

    /// <summary>
    /// The standard text's em pixels at the window's height: the Faithful mode's as the original sizes its font at that scale
    /// (<see cref="UiPainter.StandardTextPixels"/>, smaller than linear above 1080 lines), Meitou's the 1080-line size scaled.
    /// </summary>
    internal float StandardPixels => UiMode == UiMode.Faithful && UiPainter is { } painter
        ? painter.StandardTextPixels(Height)
        : StandardTextPixels * UiScale;

    /// <summary>
    /// The interface's scale: the window's height over the 1080 lines the layouts' pixel sizes and fonts are made for, times
    /// <see cref="InterfaceScale"/>.
    /// </summary>
    internal float UiScale => Height / 1080f * InterfaceScale;

    /// <summary>
    /// An inventory cell's size, pixels: 25 at 1080 lines.
    /// </summary>
    internal float InventoryCell => 25 * UiScale;

    #endregion

    #region Settings

    /// <summary>
    /// The value behind <see cref="FontSize"/>.
    /// </summary>
    float _fontSize = KenshiFontSize.DefaultSetting;

    /// <summary>
    /// The value behind <see cref="InterfaceScale"/>.
    /// </summary>
    float _interfaceScale = 1;

    /// <summary>
    /// The "Show damage floaters" option. Off hides the damage numbers only: the original's texts (a pick roll, a stealth knock-out, "Ate", a machine
    /// label) read no option (<b>Verified</b>: of the label creator's callers only the hit's <c>FUN_1406508d0</c> reads it). Meitou keeps a door's
    /// damage number with the option too.
    /// </summary>
    public FloaterMode Floaters { get; set; } = FloaterMode.Simple;

    /// <summary>
    /// The button during play (<see cref="UserConfig.FeedbackButton"/>); off, it still shows on the title and with the game menu open.
    /// </summary>
    public bool FeedbackButtonInPlay { get; set; } = true;

    /// <summary>
    /// The original's <c>Show names</c> option (<c>FUN_1406e2fd0</c>): off, the name tag keeps only its icons.
    /// </summary>
    public bool ShowNames { get; set; } = true;

    /// <summary>
    /// The <c>font size2</c> option: points added to every GUI font's default height (applied to the painter once it exists).
    /// </summary>
    public float FontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = value;
            if (UiPainter is not null) UiPainter.FontSizeSetting = value;
        }
    }

    /// <summary>
    /// The Interface scale setting: the UI's size over the window height's own (1 = 100%; applied to the painter once it exists).
    /// </summary>
    public float InterfaceScale
    {
        get => _interfaceScale;
        set
        {
            _interfaceScale = value;
            if (UiPainter is not null) UiPainter.InterfaceScale = value;
        }
    }

    /// <summary>
    /// The unit of the run speed in the information panel (Interface settings; the game shows mph).
    /// </summary>
    public SpeedUnit SpeedUnit { get; set; }

    /// <summary>
    /// The renderer of the movement marker and the stealth arrows (null: none).
    /// </summary>
    public MarkerRenderer? Markers { get; set; }

    /// <summary>
    /// The `Movement marker` option: off, no move order puts the marker down (the original tests it where it shows the marker, <c>FUN_1407f2660</c>,
    /// <c>FUN_140800250</c>).
    /// </summary>
    public bool MovementMarker { get; set; } = true;

    /// <summary>
    /// The `Rotation marker` option: off, the marker on the pivot never shows (tested in the camera update <c>FUN_1406b0f90</c>).
    /// </summary>
    public bool RotationMarker { get; set; } = true;

    #endregion

    #region Messages

    /// <summary>
    /// Queues a message for the message panel ("Discovered {1}", "Saved", or an error text in red); each shows for 4 s in turn.
    /// <paramref name="logText"/>: a longer text for the message log (the overview's messages) when the panel's line is a summary of it.
    /// </summary>
    public void Notice(string text, bool error = false, string? logText = null) => MessageRoller.QueueNotice(text, error, logText);

    /// <summary>
    /// A message box with only an Ok button.
    /// </summary>
    internal void MessageBox(string title, string text)
    {
        Frontend.PendingConfirmation = new(TitleMenu.UpperTr(title), Loc.Tr(text), () => { },
            TitleMenu.UpperTr("Ok")) { OkOnly = true };
        FaithfulScreens.Open("message");
    }

    /// <summary>
    /// The Faithful title screen is open.
    /// </summary>
    public bool TitleOpen => FaithfulScreens.IsOpen("title");

    /// <summary>
    /// A confirmation box: <paramref name="action"/> on <paramref name="yes"/>, an optional middle button, and <paramref name="cancel"/>.
    /// </summary>
    internal void Confirm(string title, string text, Action action, string yes = "YES",
        (string Caption, Action Act)? middle = null, string cancel = "CANCEL")
    {
        Frontend.PendingConfirmation = new(TitleMenu.UpperTr(title), Loc.Tr(text), action, TitleMenu.UpperTr(yes),
            middle is { } m ? (TitleMenu.UpperTr(m.Caption), m.Act) : null, TitleMenu.UpperTr(cancel));
        FaithfulScreens.Open("message");
    }

    #endregion

    #region Drawing

    /// <summary>
    /// Finds a game file by name (set by the host); null before.
    /// </summary>
    internal Func<string, string?>? UiResolve;
    /// <summary>
    /// Kenshi_UI.png, decoded on first use (<see cref="UiAtlas"/>).
    /// </summary>
    RgbaImage? _uiAtlas;
    /// <summary>
    /// The stats panel's labels (MARTIAL ARTS, STRENGTH, ...): the grey of the orders buttons' text, not Main (docs/game/ui-screens.md).
    /// </summary>
    internal static readonly string StatLabelColour = "0.4 0.380392 0.356863";

    /// <summary>
    /// The game's Kenshi_UI.png, decoded once and shared by the title menu, tooltips and settings; null when missing or unreadable.
    /// </summary>
    internal RgbaImage? UiAtlas()
    {
        if (_uiAtlas is not null || UiResolve?.Invoke("Kenshi_UI.png") is not { } path) return _uiAtlas;
        try
        {
            return _uiAtlas = TextureLoader.LoadFile(path, allMips: false).Levels[0];
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gives the interface a texture drawn at run time (the portrait atlas) under <paramref name="name"/>.
    /// </summary>
    public void SetUiTexture(string name, GpuTexture texture, SampledTexture sampled) => UiPainter?.SetTexture(name, texture, sampled);

    /// <summary>
    /// The HUD and the drag box, over the finished picture.
    /// </summary>
    public void Draw(DebugOverlay o, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        InterfaceSounds.PollUiSounds();
        Width = width;
        Height = height;
        BiomeBanner.PollBiome();
        MessageRoller.PollNotice();
        Prospecting.PollProspect();
        TutorialWindow.PollTutorials();
        StealthView.UpdateStealthArrows();
        DamageFloaters.DrawFloaters(o);
        MoveMarker.FadeMarker();

        // The box.
        if (Dragging && _dragFrom is { } from)
        {
            var edge = new Vector4(0.6f, 1f, 0.6f, 1);
            o.Rect(MathF.Min(from.X, Mouse.X), MathF.Min(from.Y, Mouse.Y), MathF.Max(from.X, Mouse.X), MathF.Max(from.Y, Mouse.Y),
                new Vector4(0.4f, 1f, 0.5f, 0.12f));
            o.Line(from.X, from.Y, Mouse.X, from.Y, 1, edge);
            o.Line(Mouse.X, from.Y, Mouse.X, Mouse.Y, 1, edge);
            o.Line(Mouse.X, Mouse.Y, from.X, Mouse.Y, 1, edge);
            o.Line(from.X, Mouse.Y, from.X, from.Y, 1, edge);
        }

        DrawGameUi(o, width, height, pass);
        o.Flush(width, height, null, pass);
    }

    /// <summary>
    /// A MyGUI "r g b" colour as a <c>#rrggbb</c> tag.
    /// </summary>
    internal static string Tag(string rgb)
    {
        var c = MyGuiColours.Parse(rgb, Vector4.One);
        return $"#{(int)MathF.Round(c.X * 255):x2}{(int)MathF.Round(c.Y * 255):x2}{(int)MathF.Round(c.Z * 255):x2}";
    }

    // The palette of data/gui/colours/kenshi_colours.xml (Verified): Main, Secondary (the text default), Title, Bad, Good, Alert; a mod's file
    // replaces it (MyGuiColours.LoadPalette).
    /// <summary>
    /// The palette's Main.
    /// </summary>
    internal static string MainColour => MyGuiColours.Named("Main", "afa68b");
    /// <summary>
    /// The palette's Title.
    /// </summary>
    internal static string TitleColour => MyGuiColours.Named("Title", "492620");
    /// <summary>
    /// The palette's Bad.
    /// </summary>
    internal static string BadColour => MyGuiColours.Named("Bad", "59231a");
    /// <summary>
    /// The palette's Good.
    /// </summary>
    internal static string GoodColour => MyGuiColours.Named("Good", "a8b774");
    /// <summary>
    /// The palette's Greyed.
    /// </summary>
    internal static string GreyColour => MyGuiColours.Named("Greyed", "444444");
    /// <summary>
    /// The palette's BadBright: the editor's race stats below 1 (<c>FUN_1405ee0c0</c>, <c>#bf4834</c>).
    /// </summary>
    internal static string BadBrightColour => MyGuiColours.Named("BadBright", "bf4834");
    /// <summary>
    /// The palette's Alert.
    /// </summary>
    internal static string AlertColour => MyGuiColours.Named("Alert", "000000");

    /// <summary>
    /// A line of a text panel: label and value, each with its colour (null: the text default). With <paramref name="Action"/> the value is a
    /// <c>Kenshi_Button2</c> button (the DataPanel's button lines): over the right <paramref name="ButtonWidth"/> of the row (the original's width
    /// argument), or across the whole row when there is no label. <paramref name="TooltipRows"/> is a key / value tooltip (the original's
    /// <c>lektor&lt;StringPair&gt;</c> lists, each text with an optional leading colour tag) in place of <paramref name="Tooltip"/>.
    /// </summary>
    internal readonly record struct HudLine(string Label, string Value, string? LabelColour = null, string? ValueColour = null,
        string? Tooltip = null, string? Action = null, float ButtonWidth = 0.5f, IReadOnlyList<(string Key, string Value)>? TooltipRows = null)
    {
        /// <summary>
        /// With a value, the line is a progress line (<see cref="ProgressLine"/>) filled by it: the value centred over the bar, no label.
        /// </summary>
        public float? Fill { get; init; }


        /// <summary>
        /// The line's tooltip user strings (the HUD's Fixed tooltip), null for none; a text tooltip wins over rows.
        /// </summary>
        public Dictionary<string, string>? TipProperties => Tooltip is { Length: > 0 } tip ? UiTooltips.TextProperties(Loc.Tr(tip), "fixed")
            : TooltipRows is { Count: > 0 } rows ? UiTooltips.RowProperties(rows, "fixed") : null;
    }

    /// <summary>
    /// A medical panel row: its name, the green, yellow and red layers' fractions, the number, the arrows, the robot layer and the row's tooltip (the
    /// <c>LifeBarNTooltip</c> widget).
    /// </summary>
    internal readonly record struct MedicalRow(string Name, float Green, float Yellow, float Red, string Value, string Arrows,
        bool Robot, string? Tooltip = null)
    {
        /// <summary>
        /// The splint layer's fraction (the green plus the splint, at most 1); shown only where it is beyond the green.
        /// </summary>
        public float White { get; init; }
        /// <summary>
        /// The Crushed layer at full width: a stump or a crushed limb (the row setter <c>FUN_140721a70</c>'s limb states 1 and 3).
        /// </summary>
        public bool Crushed { get; init; }
        /// <summary>
        /// A key / value tooltip in place of <see cref="Tooltip"/> (the Hunger row's).
        /// </summary>
        public IReadOnlyList<(string Key, string Value)>? TooltipRows { get; init; }
    }

    /// <summary>
    /// Adds a text panel's lines over the arranged widget <paramref name="panel"/>: one row per line from <paramref name="top"/> down, the label
    /// from 3 % of the width, the value right-aligned to 96 %; the value takes <paramref name="valueFont"/> at <paramref name="valuePixels"/> when
    /// given.
    /// </summary>
    internal void AddLines(List<UiControl> controls, string panel, IReadOnlyList<HudLine> lines, float rowHeight, float? fontPixels = null,
        string font = "Kenshi_StandardFont_Medium", string? valueFont = null, float? valuePixels = null, float top = 0)
    {
        if (controls.FirstOrDefault(c => c.Name == panel) is not { } host) return;
        var area = host.Bounds;
        var labelProperties = Face(font, fontPixels);
        var valueProperties = valueFont is null ? labelProperties : Face(valueFont, valuePixels);
        for (int i = 0; i < lines.Count; i++)
        {
            var tipProperties = lines[i].TipProperties;
            var row = new UiRect(area.X + area.Width * 0.03f, area.Y + top + i * rowHeight, area.Width * 0.93f, rowHeight);
            if (lines[i].Fill is { } fill)
            {
                AddProgressLine(controls, $"{panel}-progress-{i}", row, lines[i], fill, valueProperties, host.Clip);
                continue;
            }

            if (lines[i].Action is { } action)
            {
                // The button holds the value; the row's label (if any) stays text on its left.
                float width = Math.Clamp(lines[i].ButtonWidth, 0.1f, 1);
                var button = lines[i].Label.Length == 0 ? row : row with { X = row.X + row.Width * (1 - width), Width = row.Width * width };
                var buttonProperties = new Dictionary<string, string>(valueProperties) { ["TextAlign"] = "Center",
                    ["TextColour"] = lines[i].ValueColour ?? MyGuiColours.InkText };
                if (lines[i].TipProperties is { } buttonTip) foreach (var pair in buttonTip) buttonProperties[pair.Key] = pair.Value;

                // Kenshi_Button2 is a template (kenshi_templates.xml) whose root widget has the Kenshi_Button1Skin skin.
                controls.Add(new($"{panel}-button-{i}", "Button", button with { Y = button.Y + 1, Height = button.Height - 2 },
                    Loc.Tr(lines[i].Value), action, "Kenshi_Button1Skin", true, buttonProperties, Vector4.Zero, Clip: host.Clip));
                if (lines[i].Label.Length == 0) continue;
            }
            var columns = new[]
            {
                ("label", lines[i].Label, lines[i].LabelColour, "Left Top", labelProperties),
                ("value", lines[i].Action is null ? lines[i].Value : "", lines[i].ValueColour, "Right Top", valueProperties),
            };
            foreach (var (part, text, colour, align, properties) in columns)
                if (text.Length > 0)
                {
                    var textProperties = new Dictionary<string, string>(properties) { ["TextAlign"] = align,
                        ["TextColour"] = colour ?? MyGuiColours.TemplateInk(UiResources, "Kenshi_TextboxStandardText") };

                    // A line with a tooltip is a mouse target (the HUD's own tooltip, anchored on its panel).
                    if (tipProperties is not null) foreach (var pair in tipProperties) textProperties[pair.Key] = pair.Value;

                    // A value beside a label keeps out of the label's column: a long one (a French weather notice) is cut short instead of running
                    // over the label. A label beside a button keeps to its left: added after the button, a full-row label with a tooltip would take
                    // its clicks.
                    var box = part == "value" && lines[i].Label.Length > 0 ? row with { X = row.X + row.Width * 0.35f, Width = row.Width * 0.65f }
                        : lines[i].Action is not null ? row with { Width = row.Width * (1 - Math.Clamp(lines[i].ButtonWidth, 0.1f, 1)) } : row;
                    controls.Add(new($"{panel}-{part}-{i}", "TextBox", box, Loc.Tr(text), "", "Kenshi_TextboxStandardText", true,
                        textProperties, Vector4.Zero, Pickable: tipProperties is not null, Clip: host.Clip));
                }
        }

        static Dictionary<string, string> Face(string name, float? pixels)
        {
            var properties = new Dictionary<string, string> { ["FontName"] = name };
            if (pixels is { } p)
                properties["FontPixels"] = Math.Max(8, (int)MathF.Round(p)).ToString(CultureInfo.InvariantCulture);
            return properties;
        }
    }

    /// <summary>
    /// A progress line over <paramref name="row"/>: the bar filled by <paramref name="fill"/>, then the line's value centred over it.
    /// </summary>
    void AddProgressLine(List<UiControl> controls, string name, UiRect row, HudLine line, float fill, Dictionary<string, string> face, UiRect? clip)
    {
        ProgressLine.Add(controls, UiResources, Width, Height, row, fill, name + "-", clip);

        var textProperties = new Dictionary<string, string>(face) { ["TextAlign"] = "HCenter Top",
            ["TextColour"] = line.ValueColour ?? MyGuiColours.TemplateInk(UiResources, "Kenshi_TextboxStandardText") };
        controls.Add(new(name + "-value", "TextBox", row, Loc.Tr(line.Value), "", "Kenshi_TextboxStandardText", true, textProperties, Vector4.Zero,
            Pickable: false, Clip: clip));
    }

    #endregion

    #region Pointer and orders

    /// <summary>
    /// How far from the camera a box select still takes characters.
    /// </summary>
    public const float BoxSelectRange = 7500;
    /// <summary>
    /// A person's height (the renderer's sphere takes 20) for the box select's aim point and the pick capsule of a character the renderer does not
    /// draw.
    /// </summary>
    public const float CharacterHeight = 18;
    /// <summary>
    /// Pixels the pointer moves with the left button down before it is a box select.
    /// </summary>
    internal const float DragThreshold = 5;
    /// <summary>
    /// Where the left button went down in the world; null when it is not down there.
    /// </summary>
    Vector2? _dragFrom;
    /// <summary>
    /// The pointer's position, pixels.
    /// </summary>
    internal Vector2 Mouse;
    /// <summary>
    /// The left button's drag is a box select (past <see cref="DragThreshold"/>).
    /// </summary>
    internal bool Dragging;
    // The held right button's move order: where it goes now and the tick it was given.
    /// <summary>
    /// Whether the right button is held after a move order, which then follows the pointer (<see cref="Steer"/>).
    /// </summary>
    internal bool Steering;
    /// <summary>
    /// Where the held move order goes now.
    /// </summary>
    Vector3 _steerTarget;
    /// <summary>
    /// The tick the held move order was last given.
    /// </summary>
    long _steerTick;
    /// <summary>
    /// The squad member whose portrait the right button went down on; its release focuses it.
    /// </summary>
    CharacterId? _memberPress;
    // The last number key and when it was pressed: the same key again within DoubleTapSeconds centres on the character (docs/game/ui-input.md, events
    // 100..120).
    /// <summary>
    /// The last number key.
    /// </summary>
    internal InputAction? LastHotkey;
    /// <summary>
    /// When <see cref="LastHotkey"/> was pressed (a <see cref="System.Diagnostics.Stopwatch"/> timestamp).
    /// </summary>
    internal long LastHotkeyAt;
    /// <summary>
    /// The hotkey events this frame; the first one of a frame is the press.
    /// </summary>
    internal int HotkeyEventsThisFrame;
    /// <summary>
    /// The double tap's window, seconds.
    /// </summary>
    internal const double DoubleTapSeconds = 0.5;
    /// <summary>
    /// Ctrl and the Highlight key (Alt by default) as the host last saw them: they turn the pointer over a downed character into the carry and loot
    /// pointers, and Highlight lets an order steal or pick a lock.
    /// </summary>
    public bool CtrlHeld, AltHeld;
    /// <summary>
    /// Counts the input the interface took (keys, clicks, text, the wheel): any of it can change what the base shows.
    /// </summary>
    internal int UiInput;

    /// <summary>
    /// Shift as the host last saw it: the slow modifier of the ghost's up and down move.
    /// </summary>
    public bool SlowModifier { get; set; }

    /// <summary>
    /// The character the strategy camera follows (a double tap of its number key, F, or a double click on its squad button); panning the camera lets
    /// go.
    /// </summary>
    public CharacterId? Follow { get; set; }

    /// <summary>
    /// The host's camera flight to a double-clicked squad member (Advanced settings "Character flights"); false: centre on it at once.
    /// </summary>
    public Func<CharacterId, bool>? FlyTo { get; set; }
    /// <summary>
    /// A third press (a single one while the flight to that member is under way): the host ends the flight on it at once.
    /// </summary>
    public Action<CharacterId>? SkipFlight { get; set; }

    /// <summary>
    /// Sends a command to the world (and plays its sound).
    /// </summary>
    internal void Say(SimCommand command)
    {
        InterfaceSounds.SoundForCommand(command);
        Session.World.Commands.Enqueue(command);
    }

    /// <summary>
    /// Selects characters (<paramref name="additive"/>: added to the selection) through a <see cref="SelectCommand"/>; selecting a character ends
    /// the building's selection.
    /// </summary>
    /// <param name="toggle">A single character with shift: one already selected leaves the selection (<see cref="SelectCommand.Toggle"/>).</param>
    public void Select(IReadOnlyList<CharacterId> ids, bool additive, bool toggle = false)
    {
        if (ids.Count > 0) BuildingSelection.SetBuilding(null);   // selecting a character ends the building's selection
        Say(new SelectCommand(ids, additive, toggle && additive) { Tick = Session.World.Tick });
    }

    /// <summary>
    /// Orders the selected characters to walk to <paramref name="target"/>; <paramref name="queued"/> adds it after their orders.
    /// </summary>
    public void Move(Vector3 target, bool queued) =>
        Say(new MoveOrder([], target) { Tick = Session.World.Tick, Queued = queued });


    /// <summary>
    /// Returns true when the click belonged to the HUD or the interface (the camera input should not see it).
    /// </summary>
    public bool MouseDown(MouseButton button, Vector2 p, bool shift, bool ctrl = false, bool alt = false)
    {
        Mouse = p;
        UiInput++;
        (InventoryWindows.PressShift, InventoryWindows.PressCtrl) = (shift, ctrl);
        TooltipRuntime.HideTooltip();   // a press captures the mouse, which hides a tooltip (MyGUI)
        if (button == MouseButton.Left && FeedbackPanel.FeedbackButtonDown(p)) return true;
        if (CharacterEditor.EditorPointerDown(button, p)) return true;
        if (Session.Bindings.Get(InputAction.RotateItem).Contains(Binding.Of(button)) && InventoryWindows.RotateAt(p)) return true;
        if (button == MouseButton.Right && InventoryWindows.CancelInventoryDrag()) return true;
        if (button == MouseButton.Right && !OverHud(p) && Build.WallCancel()) return true;
        if (button == MouseButton.Right && !OverHud(p) && LevelEditorPanel.LevelClearAt(p)) return true;
        if (button == MouseButton.Right && !OverHud(p) && Build.BuildDiscard()) return true;
        if (button == MouseButton.Left) return LeftDown(p);
        if (button == MouseButton.Right && WorldMap.InventoryRightClick(p, shift)) return true;
        if (button == MouseButton.Right && ContextMenuWindow.CloseContextMenu()) return true;
        if (button == MouseButton.Right && WorldMap.MapRightDown(p)) return true;

        // A right press on a squad bar portrait held 0.25 s opens the member's context menu; a quicker release over it selects the member and
        // focuses the camera on it, as the double click (FUN_140413d80, FUN_140415630, docs/game/ui-input.md "Squad bar portraits").
        if (button == MouseButton.Right && PortraitBar.SquadBarMemberAt(p) is { } member) return PortraitRightDown(p, member, shift);
        if (button == MouseButton.Middle && UiVisible && WorldMap.MapPointerDown(p)) return true;
        if (button == MouseButton.Right && !OverHud(p)) return WorldRightDown(p, shift, ctrl, alt);
        return OverHud(p);
    }

    /// <summary>
    /// A left press past the HUD's own handlers: the interface, the debug world click, a ground item's label, the build mode, else the start of
    /// a selection drag; always taken.
    /// </summary>
    bool LeftDown(Vector2 p)
    {
        // An open menu takes no press: its rows act on the release, a press beside it does nothing (FUN_1407a7f10).
        if (ContextMenuWindow.OpenMenu is not null) return true;
        if (UiMouseDown(p)) return true;
        if (OverHud(p)) return true;

        // Debug "Explode on click": the host takes the press on the ground (the point under the cursor, if any) instead of the selection.
        if (Picking.WorldClick is { } worldClick && !WorldMap.MapTabShown
                && worldClick(Ray(p) is { } wr ? Picking.MoveHit(wr.Origin, wr.Direction) : null))
            return true;

        // A ground item's name label (shown while Highlight is held) takes the left press; its release over it is the item's pick-up
        // (FUN_1401638f0).
        if (GroundItemPicks.LabelAt(p) is { } label)
        {
            GroundItemPicks.LabelPress = label;
            return true;
        }
        if (Build.BuildClick()) return true;
        _dragFrom = p;
        Dragging = false;
        return true;
    }

    /// <summary>
    /// A right press on squad bar portrait <paramref name="member"/>: the context press on it, else the press a quick release selects; always
    /// taken.
    /// </summary>
    bool PortraitRightDown(Vector2 p, CharacterId member, bool shift)
    {
        if (Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == member) is { } shown
            && ContextMenuWindow.ContextPressOn(p, at => ContextMenuWindow.Menu(shown.Name, at, ContextMenuWindow.Verbs(shown)),
                () => FocusMember(member, shift), ContextMenuWindow.PortraitHoldSeconds)) return true;
        _memberPress = member;
        return true;
    }

    /// <summary>
    /// A right press on the world: the Alt / Ctrl orders on a downed character, else the click on what is under the cursor; always taken.
    /// </summary>
    bool WorldRightDown(Vector2 p, bool shift, bool ctrl, bool alt)
    {
        // Alt + right click on a downed or dead character: the nearest selected loots it (take all). Ctrl + right click: picks it up, or puts it
        // down when a selected character carries it. A plain right click on one of your own downed characters that needs no first aid picks it up
        // too (the original's quick right click). A ground item's name label (shown while Alt is held) is drawn over everything: a click on it is
        // the item's, never the body lying beside it.
        var downed = (alt || ctrl) && GroundItemPicks.LabelAt(p) is null ? Picking.PickDowned(p) : null;
        if (alt && downed is { } loot && SelectedIds() is { Count: > 0 })
            Say(new Simulation.Carrying.LootOrder([], loot) { Tick = Session.World.Tick });
        else if (ctrl && downed is { } body && Selected().Where(c => c.Carrying == body).Select(c => c.Id).ToArray() is { Length: > 0 } carriers)
            Say(new Simulation.Carrying.PutDownOrder(carriers) { Tick = Session.World.Tick });
        else if (ctrl && downed is { } lift && SelectedIds() is { Count: > 0 })
            Say(new Simulation.Carrying.PickUpOrder([], lift) { Tick = Session.World.Tick });
        else
            PickedRightDown(p, shift, alt);
        return true;
    }

    /// <summary>
    /// A right press on the nearest target along the ray (PickAt): its own click, else a move to the ground under the cursor.
    /// </summary>
    void PickedRightDown(Vector2 p, bool shift, bool alt)
    {
        // On a character: the quick order (trade with a trader, attack anyone else, treat a hurt ally, pick up a downed one of yours, loot anyone
        // else's) is given when the button comes up before the context menu's hold time; held longer, the menu opens instead (ContextMenuWindow).
        var kind = Picking.PickAt(p).Kind;
        if (kind == WorldPicking.Picked.Character)
        {
            var quick = QuickOrder(p)?.Do;
            if (!ContextMenuWindow.ContextPress(p, quick)) quick?.Invoke();
            return;
        }
        if (kind == WorldPicking.Picked.GroundItem && GroundItemPicks.GroundItemClick(p)) return;
        if (kind == WorldPicking.Picked.Container && Furniture.ContainerClick(p, alt)) return;
        if (kind == WorldPicking.Picked.Door && Furniture.DoorClick(p, alt)) return;
        if (kind == WorldPicking.Picked.Bed && Furniture.BedPress(p)) return;
        if (kind == WorldPicking.Picked.Seat && Furniture.SeatPress(p)) return;
        if (kind == WorldPicking.Picked.Cage && Furniture.CageClick(p)) return;
        if (kind == WorldPicking.Picked.Dummy && Furniture.DummyPress(p)) return;
        if (kind == WorldPicking.Picked.TownTurret && Furniture.TownTurretPress(p)) return;
        if (kind == WorldPicking.Picked.TownMachine && TownMachines.Press(p)) return;
        if (kind == WorldPicking.Picked.Rock && Furniture.RockPress(p)) return;
        if (kind is WorldPicking.Picked.Site or WorldPicking.Picked.None && Build.SitePress(p)) return;
        if (Ray(p) is { } ray && Picking.MoveHit(ray.Origin, ray.Direction) is { } hit)
        {
            Move(hit, shift);
            MoveMarker.PlaceMarker(hit);
            (Steering, _steerTarget, _steerTick) = (!shift, hit, Session.World.Tick);
        }
    }

    /// <summary>
    /// The order a quick right click on a character gives and the mouse pointer that shows it (PointerView), or null when there is none: the click
    /// does nothing then (the original treats any character hit as handled, so it does not move either). The target is the nearest character under
    /// the cursor, as the original's pick takes it; with nothing selected the pointer is still given (<c>Do</c> is null). See
    /// <see cref="QuickOrderFor"/>.
    /// </summary>
    internal (string Pointer, CharacterId Target, Action? Do)? QuickOrder(Vector2 p)
    {
        if (Picking.PickWhereAt(p, _ => true) is not { } hit
                || Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == hit.Id) is not { } target)
            return null;
        return QuickOrderFor(target, SelectedIds());
    }

    /// <summary>
    /// The talk pointer: <c>s_talk</c> while the character holds a line for the player (Dialogue <c>playerInterruptionDialog</c> +0xc8,
    /// <c>FUN_1407fb410</c>), else <c>talk</c>.
    /// </summary>
    string TalkPointer(CharacterSnapshot c) => Session.World.WantsToTalk(c.Id) ? "s_talk" : "talk";

    /// <summary>
    /// The quick right click on <paramref name="c"/> (<c>FUN_1407fb410</c>, docs/game/ui-input.md "Quick right click"). The lead is the selected
    /// character nearest to it (with nothing selected the nearest of the player's characters, which gives the pointer only). Own characters: dead,
    /// loot; needing first aid (organic need above 0.01) 25, else robot repair 60 (all the selection); down and not carried, pick up (the nearest);
    /// carried by the lead, put down; carried by another, nothing. Others carried by a non-player: nothing. Hostile ones
    /// (<see cref="PointerView.Hostile(CharacterSnapshot)"/>): down, loot; standing and the lead sneaking, stealth KO (or loot of a slave); fighting
    /// or without a talk, attack (with a gunner on a turret, Shoot At); a prisoner, loot. Neutral ones: down, loot (<c>search</c> pointer for a
    /// bandit, slave or not-real faction, else <c>scavenge</c>); standing and the lead sneaking, pick the shackles of a prisoner, loot a slave, else
    /// stealth KO; else talk when it has something to say (a trader trades), otherwise nothing (<c>green</c> pointer when allied to the lead).
    /// </summary>
    internal (string Pointer, CharacterId Target, Action? Do)? QuickOrderFor(CharacterSnapshot c, IReadOnlyList<CharacterId> selection)
    {
        var characters = Session.CurrentSnapshot.Characters;
        CharacterSnapshot? lead = null;
        float nearest = float.MaxValue;
        foreach (var o in characters)
        {
            if (selection.Count > 0 ? !selection.Contains(o.Id) : !o.IsPlayer) continue;
            float d = Vector3.DistanceSquared(o.Position, c.Position);
            if (d < nearest) (lead, nearest) = (o, d);
        }
        if (lead is null) return null;
        var world = Session.World;
        long tick = world.Tick;
        bool give = selection.Count > 0;
        Action? Do(Action a) => give ? a : null;
        bool carried = !c.CarriedBy.IsNone;
        if (carried && characters.FirstOrDefault(o => o.Id == c.CarriedBy) is not { IsPlayer: true }) return null;
        bool dead = c.Body is { Dead: true }, down = c.Down;
        var loot = Do(() => Say(new Simulation.Carrying.OpenLootOrder([], c.Id) { Tick = tick }));
        var id = c.Id;
        if (c.IsPlayer)
        {
            if (dead) return ("search", id, loot);
            if (c.Body is { NeedsAid: true })
            {
                bool robot = c.Body is { NeedsRobotAid: true };
                return ("med", id,
                    Do(() => SquadWindows.OrderOrJob(robot ? Data.Gameplay.Ai.TaskType.FirstAidRobot : Data.Gameplay.Ai.TaskType.FirstAidOrder, id,
                    () => Say(new Simulation.FirstAid.TreatOrder(selection, id) { Tick = tick }))));
            }
            if (!down) return null;
            if (!carried) return lead.Id == id ? null : ("lift", id, Do(() => Say(new Simulation.Carrying.PickUpOrder([], id) { Tick = tick })));
            return lead.Carrying == id ? ("lift", id, Do(() => Say(new Simulation.Carrying.PutDownOrder([lead.Id]) { Tick = tick }))) : null;
        }
        bool sneaking = c.CanStealthKo;
        if (PointerPanel.Hostile(c))
        {
            if (down) return ("search", id, loot);
            if (sneaking)
                return c.Slave is { State: 1 } ? ("scavenge", id, loot)
                    : ("knockout", id, Do(() => Say(new Simulation.Carrying.StealthKoOrder([lead.Id], id) { Tick = tick })));
            if (c.Prison is not null) return ("search", id, loot);
            if (c.InCombat || !(c.WarParty || c.Slave is { State: 1 }) || !world.CanTalk(id, lead.Id))
            {
                // Fighting back at someone attacking the squad is defence, not an assault on its faction.
                bool defence = c.FightsPlayer;
                return ("attk", id, Do(() =>
                {
                    if (lead.Turret is not null) Say(new Simulation.Combat.ShootAtOrder(selection, id) { Tick = tick });
                    else SquadWindows.OrderOrJob(Data.Gameplay.Ai.TaskType.MeleeAttack, id,
                        () => Say(new Simulation.Combat.AttackOrder(selection, id) { Tick = tick, Picked = true, Defensive = defence }));
                }));
            }
            return (TalkPointer(c), id, Do(() => Say(new Simulation.Dialogue.TalkOrder(lead.Id, id) { Tick = tick })));
        }
        if (down) return (c.NoLootGuilt ? "search" : "scavenge", id, loot);
        if (sneaking)
        {
            if (c.Prison is not null)
                return ("lockpick", id, Do(() =>
                {
                    Say(new Simulation.Locks.PickLockOrder([lead.Id], id) { Tick = tick });
                    Say(new Simulation.Locks.ShackleOrder([lead.Id], id, Simulation.Locks.ShackleWork.Pick) { Tick = tick });
                }));
            return c.Slave is { State: 1 } ? ("scavenge", id, loot)
                : ("knockout", id, Do(() => Say(new Simulation.Carrying.StealthKoOrder([lead.Id], id) { Tick = tick })));
        }

        // Order 12 (Task_PlayerTalkto) walks over and starts the conversation, a trader's included: its trade window is a reply of that conversation
        // (DA_TRADE).
        if (world.CanTalk(id, lead.Id)) return (TalkPointer(c), id, Do(() => Say(new Simulation.Dialogue.TalkOrder(lead.Id, id) { Tick = tick })));
        // engine: a shop whose keeper has no line to say
        if (c.IsTrader) return ("talk", id, Do(() => Say(new Simulation.Trading.OpenTrade(lead.Id, id) { Tick = tick })));
        return world.RegardsFriendly(id, lead.Id) ? ("green", id, null) : null;   // slot 126: the target regards the lead as friendly
    }

    /// <summary>
    /// A pointer move: to the first that takes it (the character editor, a scroll thumb, the map, the screens), else a drag past the threshold starts
    /// the box select.
    /// </summary>
    public void MouseMove(Vector2 p)
    {
        Mouse = p;
        if (CharacterEditor.EditorPointerMove(p)) return;
        if (ScrollBars.ScrollThumbMove(p)) return;
        if (WorldMap.MapPointerMove(p)) return;
        if (UiVisible && FaithfulScreens.PointerMove(p)) return;
        if (_dragFrom is { } from && !Dragging && Vector2.Distance(from, p) >= DragThreshold) Dragging = true;
    }

    /// <summary>
    /// A button release: to the context menu, a ground item's label, the squad windows, the map, a portrait's press (focuses the member) and a held
    /// move order, else the left button's box select or click ends.
    /// </summary>
    public void MouseUp(MouseButton button, Vector2 p, bool shift)
    {
        Mouse = p;
        UiInput++;
        CharacterEditor.EditorPointerUp(button);
        if (ContextMenuWindow.ContextMenuRelease(button, p)) return;
        if (button == MouseButton.Left && GroundItemPicks.LabelPress is { } labelled)
        {
            GroundItemPicks.LabelPress = null;
            if (GroundItemPicks.LabelAt(p) == labelled) GroundItemPicks.LabelPickUp(labelled);
            return;
        }
        if (button == MouseButton.Left)
        {
            WorldMap.MapPickUp(p);
            InventoryWindows.InventoryPointerUp(p);
            OrdersPanel.JobDragEnd(p);
            SquadWindows.SquadPointerUp(p, shift);
            FaithfulScreens.PointerUp();
            WorldMap.MapDrag = null;
        }
        if (button == MouseButton.Middle) WorldMap.MapDrag = null;
        if (button == MouseButton.Right && _memberPress is { } pressed)
        {
            _memberPress = null;
            if (PortraitBar.SquadBarMemberAt(p) == pressed) FocusMember(pressed, shift);
            return;
        }
        if (button == MouseButton.Right && WorldMap.MapRightUp(p)) return;
        if (button == MouseButton.Right)
        {
            ContextMenuWindow.ContextRelease();
            Build.SiteRelease();
            Furniture.RockRelease();
            Furniture.BedRelease();
            Furniture.DummyRelease();
            Furniture.SeatRelease();
        }
        if (button == MouseButton.Right && Steering)
        {
            Steer(final: true);
            Steering = false;
        }
        if (button != MouseButton.Left || _dragFrom is not { } from) return;
        _dragFrom = null;
        if (Dragging)
        {
            Dragging = false;
            var boxed = Picking.InBox(from, p);
            Select(boxed, shift);
            if (boxed.Count > 0) Inspect(CharacterId.None);
        }
        else
        {
            // A click on another faction's character shows it in the information and medical panels and keeps the squad's selection
            // (docs/game/ui-screens.md section 4). Shift keeps the selection, a click on your own or on the ground clears the inspection.
            // Without Shift the nearest character under the cursor takes the click, the player's or another's, as the original's one ray does
            // (docs/game/ui-input.md "Character picking"): a body carried on a squad member's shoulder is in front of its carrier.
            var nearest = Picking.PickWhere(p, _ => true);
            CharacterId? nearestOwn = nearest is { } first && Own(first) ? first : null;
            var hit = shift ? Picking.Pick(p) : nearestOwn;
            if (hit is { } own && DeadOwn(own))
            {
                Inspect(own);
                return;
            }

            var other = hit is null && !shift ? nearest : null;

            // A building under the cursor (and no character) is selected and the characters keep their selection (docs/game/ui-input.md "Building
            // picking and selection").
            if (hit is null && nearest is null && BuildingSelection.PickBuilding(p) is { } building)
            {
                BuildingSelection.SetBuilding(building);
                Inspect(CharacterId.None);
                return;
            }
            if (hit is null && other is null && BuildingSelection.DeselectBuildings)
                BuildingSelection.SetBuilding(null);
            Select(hit is { } id ? new[] { id } : [], shift, toggle: true);
            if (other is { } stranger) Inspect(stranger);
            else if (!shift || hit is not null) Inspect(CharacterId.None);
        }
    }

    /// <summary>
    /// A click on one of your own dead characters, with or without Shift, keeps the squad's selection and shows the corpse in the HUD panels as
    /// another faction's character: the original's select skips the clear for the dead and puts them in the object slot instead
    /// (<c>FUN_1407f9000</c>, <c>FUN_1407f7f20</c>, docs/game/ui-input.md "Selection").
    /// </summary>
    bool DeadOwn(CharacterId id) => Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { Body.Dead: true };

    /// <summary>
    /// Whether the character is one of the player's.
    /// </summary>
    bool Own(CharacterId id) => Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { IsPlayer: true };

    /// <summary>
    /// Shows <paramref name="target"/> (another faction's character or your own dead one; <see cref="CharacterId.None"/> for nobody) in the HUD
    /// panels.
    /// </summary>
    internal void Inspect(CharacterId target)
    {
        if (!target.IsNone) BuildingSelection.SetBuilding(null);
        Say(new InspectCommand(target) { Tick = Session.World.Tick });
    }

    /// <summary>
    /// Once a frame: while the right button is held after a move order, the order is given again at the floor or terrain point under the cursor once
    /// every world tick (also when the camera moves and the cursor does not). <paramref name="final"/> (the button is let go) gives the last point
    /// unless this tick already has the same one.
    /// </summary>
    public void Steer(bool final = false)
    {
        if (!final) HotkeyEventsThisFrame = 0;
        ContextMenuWindow.UpdateContextMenu();
        FactionWindow.UpdateFactionName();
        TownPanel.UpdateTownName();
        Furniture.UpdateBed();
        Furniture.UpdateDummy();
        Furniture.UpdateSeat();
        WorldMap.UpdateMap();
        if (!final) Build.UpdateBuild();
        if (!Steering || OverHud(Mouse)) return;
        if (Ray(Mouse) is not { } ray || Picking.MoveHit(ray.Origin, ray.Direction) is not { } hit) return;
        MoveMarker.PlaceMarker(hit);   // the marker follows the cursor every frame; the order only once a tick
        if (!final && Session.World.Tick == _steerTick) return;
        if (final && Session.World.Tick == _steerTick && hit == _steerTarget) return;
        Move(hit, queued: false);
        (_steerTarget, _steerTick) = (hit, Session.World.Tick);
    }

    /// <summary>
    /// A portrait's right click: selects the member (Shift toggles) and focuses the camera on it, as the double click.
    /// </summary>
    void FocusMember(CharacterId member, bool shift)
    {
        Select([member], shift, toggle: true);
        if (FlyTo?.Invoke(member) != true) CentreOn(member);
    }

    /// <summary>
    /// Selects the squad's <paramref name="index"/>th member (<paramref name="shift"/> adds); the second press within half a second centres the
    /// camera on it and follows.
    /// </summary>
    internal void SelectMember(int index, bool shift, bool doubleTap)
    {
        var members = ActiveSquadMembers();
        if (index < 0 || index >= members.Count) return;
        Select([members[index]], shift, toggle: true);
        if (!doubleTap) SkipFlight?.Invoke(members[index]);
        else if (FlyTo?.Invoke(members[index]) != true) CentreOn(members[index]);
    }

    /// <summary>
    /// Where the first selected character stands, or null with none selected.
    /// </summary>
    public Vector3? SelectedPosition => Selected().FirstOrDefault() is { } lead ? lead.Position : null;

    /// <summary>
    /// Centres the camera on the lead selected character and follows it (after a load, which drops the follow).
    /// </summary>
    public void FollowSelected()
    {
        if (Selected().FirstOrDefault() is { } lead) CentreOn(lead.Id);
    }

    /// <summary>
    /// The character the camera centres on for <paramref name="c"/>: a carried body is centred where its carrier stands, however deep the chain.
    /// </summary>
    CharacterSnapshot Carrier(CharacterSnapshot c)
    {
        for (int depth = 0; depth < 8 && !c.CarriedBy.IsNone; depth++)
        {
            if (Session.CurrentSnapshot.Characters.FirstOrDefault(k => k.Id == c.CarriedBy) is not { } carrier) break;

            c = carrier;
        }

        return c;
    }

    /// <summary>
    /// Places the camera's pivot on a character, keeping the view, and follows it (unless <paramref name="follow"/> is false).
    /// </summary>
    internal void CentreOn(CharacterId id, bool follow = true)
    {
        if (Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } c) return;
        var view = Session.Camera.Current;
        c = Carrier(c);
        Session.Camera.Place(new Vector2(c.Position.X, c.Position.Z), view.Yaw, view.Pitch, view.Distance);
        Follow = follow ? id : null;
    }

    /// <summary>
    /// Drops the transient state of the mouse (a drag box, a steering right click): the world it belonged to is gone.
    /// </summary>
    public void Reset()
    {
        DamageFloaters.Floaters.Clear();
        _dragFrom = null;
        Dragging = false;
        Steering = false;
        WorldMap.MapDrag = null;
        (InventoryWindows.Held, InventoryWindows.LiftPress, InventoryWindows.Lifting, InventoryWindows.HeldSerial) = (null, null, null, 0);
        _selectedFor = null;
        _selected = [];
        BuildingSelection.Selected = null;
        Follow = null;
        ContextMenuWindow.CloseContextMenu();
    }

    #endregion

    #region Selection

    /// <summary>
    /// The snapshot <see cref="_selected"/> was found for.
    /// </summary>
    WorldSnapshot? _selectedFor;
    /// <summary>
    /// The selected characters of <see cref="_selectedFor"/>.
    /// </summary>
    IReadOnlyList<CharacterSnapshot> _selected = [];
    /// <summary>
    /// The inspected character of <see cref="_selectedFor"/>; null without one.
    /// </summary>
    CharacterSnapshot? _inspected;

    /// <summary>
    /// The selected characters' ids.
    /// </summary>
    internal IReadOnlyList<CharacterId> SelectedIds() => [.. Selected().Select(c => c.Id)];

    /// <summary>
    /// The ids of the player's characters in the snapshot.
    /// </summary>
    internal IReadOnlyList<CharacterId> PlayerCharacters() =>
        [.. Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).Select(c => c.Id)];

    /// <summary>
    /// The active squad's members in squad order, the first leading (the squad bar's; the number keys and select_all pick from them, the original's
    /// <c>UiSelectSquadMember</c> / <c>UiSelectAllInSquad</c>). The squad of the first selected character, else the first squad, when the active one
    /// is unknown; every player character in a world with no squad list.
    /// </summary>
    internal IReadOnlyList<CharacterId> ActiveSquadMembers()
    {
        var snapshot = Session.CurrentSnapshot;

        // No cached squad: the first selected character's squad, else the first squad of the roster, else nothing (FUN_1407f5520).
        var firstSelected = Selected().Count > 0 ? Selected()[0].Id : CharacterId.None;
        var squad = snapshot.PlayerSquads.FirstOrDefault(s => s.Id == snapshot.ActiveSquad)
            ?? snapshot.PlayerSquads.FirstOrDefault(s => s.Members.Contains(firstSelected)) ?? snapshot.PlayerSquads.FirstOrDefault();
        // A world with no squad list at all (tests, bare sessions) keeps every player character.
        if (squad is null) return snapshot.PlayerSquads.Count == 0 ? PlayerCharacters() : [];
        var present = PlayerCharacters().ToHashSet();
        return [.. squad.Members.Where(present.Contains)];
    }


    /// <summary>
    /// The selected characters of the current snapshot, found once per snapshot (the HUD box is asked for on every hit test).
    /// </summary>
    internal IReadOnlyList<CharacterSnapshot> Selected()
    {
        var snapshot = Session.CurrentSnapshot;
        if (!ReferenceEquals(snapshot, _selectedFor))
        {
            _selected = [.. snapshot.Characters.Where(c => c.Selected)];
            _inspected = snapshot.Characters.FirstOrDefault(c => c.Inspected);
            _selectedFor = snapshot;
        }
        return _selected;
    }

    /// <summary>
    /// The character the HUD panels describe: the other faction's character last clicked, else the first selected one (docs/game/ui-screens.md
    /// section 4). The selection stays whoever is inspected.
    /// </summary>
    internal CharacterSnapshot? Lead() => Selected().FirstOrDefault() is { } first ? _inspected ?? first : _inspected;

    #endregion

    #region Data services

    /// <summary>
    /// Resolves a faction (a position in the world's faction list) to its name; the host sets it.
    /// </summary>
    public Func<int, string?>? FactionName { get; set; }

    /// <summary>
    /// The ARMOUR record of an item, null for anything else (the host sets it).
    /// </summary>
    public Func<string, ArmourData?>? ArmourOf { get; set; }
    /// <summary>
    /// An item's Value and Sell value with no trader, and the Avg. Price of a trade item (null for others); the host sets it. Null: no prices for
    /// this item.
    /// </summary>
    public Func<TooltipRuntime.ItemTip, (int Value, int Sell, int? Average)?>? PricesOf { get; set; }
    /// <summary>
    /// The player faction's name (what the dialogue token PLAYERFACTION expands to); the host sets it.
    /// </summary>
    public Func<string>? PlayerFactionName { get; set; }
    /// <summary>
    /// The name of a record by string id (a body part of an armour's coverage); the host sets it.
    /// </summary>
    public Func<string, string?>? RecordName { get; set; }


    #endregion
}