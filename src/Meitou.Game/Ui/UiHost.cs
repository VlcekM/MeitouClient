using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Localisation;

namespace Meitou.Game.Ui;

/// <summary>
/// A UI extension's failure: the extension, what it was doing and the exception.
/// </summary>
public sealed record UiDiagnostic(string Extension, string Operation, Exception Error);

/// <summary>
/// A marker an extension puts on the world map (<see cref="UiRegistration.AddMapMarkers"/>): the world point it stands on (X, Z), its image group
/// in the game's map marker set (<c>Kenshi_MapMarkers</c>: Town, Village, Outpost, Ruin, Camp, Nest, Workcamp, SmallPlace, Character, Movement,
/// NewDiscovered), its tint (RGBA, 0 to 1), a label under it from zoom level 1 (null: none), its tooltip (null: none), the least zoom level it
/// shows at (0, the whole map, to 2) and its size against the group's image.
/// </summary>
public sealed record UiMapMarker(System.Numerics.Vector2 At, string Group, System.Numerics.Vector4 Colour, string? Label = null,
    string? Tooltip = null, int FromLevel = 0, float Scale = 1)
{
    /// <summary>
    /// An image file drawn in place of the group's picture: a path relative to the mod's folder (<c>"camp.png"</c>, <c>"icons/camp.png"</c>), a PNG or
    /// DDS file of at most 8 MB and 1024 pixels a side, tinted by <see cref="Colour"/>; null draws the group. It is stretched to the group's size
    /// when <see cref="Group"/> names one of the set (empty: none), else to 32 by 32 pixels, times <see cref="Scale"/>.
    /// </summary>
    public string? Image { get; init; }
}

/// <summary>
/// What an extension's map markers get: the snapshot the map is drawn from and its zoom level (0, the whole map, to 2).
/// </summary>
public sealed class UiMapContext(Simulation.WorldSnapshot snapshot, int level)
{
    /// <summary>
    /// The snapshot the map is drawn from (the one on screen).
    /// </summary>
    public Simulation.WorldSnapshot Snapshot { get; } = snapshot;

    /// <summary>
    /// The map's zoom level: 0 shows the whole map, 2 is the closest.
    /// </summary>
    public int Level { get; } = level;
}

/// <summary>
/// A tick button an extension adds to the squad overview's AI tab (<see cref="UiRegistration.AddSquadAiOptions"/>): its caption, its tooltip,
/// whether it shows ticked, and what pressing it does (on the host's thread).
/// </summary>
public sealed record UiSquadAiOption(string Caption, string Tooltip, bool Ticked, Action Press);

/// <summary>
/// What an extension's AI tab options get: the snapshot the tab is laid out from and, read when asked, the tick; and the command queue, through which
/// a press changes the world.
/// </summary>
public sealed class UiSquadAiContext(Simulation.WorldSnapshot snapshot, Func<long> tick, Action<Simulation.SimCommand> enqueue)
{
    /// <summary>
    /// The snapshot the tab is laid out from (the one on screen).
    /// </summary>
    public Simulation.WorldSnapshot Snapshot { get; } = snapshot;

    /// <summary>
    /// The world's tick now, for a command's <see cref="Simulation.SimCommand.Tick"/>.
    /// </summary>
    public long Tick => tick();

    /// <summary>
    /// Queues a command for the world.
    /// </summary>
    public void Enqueue(Simulation.SimCommand command) => enqueue(command);
}

/// <summary>
/// A button an extension adds to the selected building's panel (<see cref="UiRegistration.AddBuildingActions"/>): its caption, its tooltip (null:
/// none) and what pressing it does (on the host's thread; it changes the world only by queueing commands).
/// </summary>
public sealed record UiBuildingAction(string Caption, string? Tooltip, Action Press);

/// <summary>
/// What an extension's building panel buttons get: the selected building or placed piece, the snapshot the panel is laid out from, the tick read
/// when asked, and the command queue, through which a press changes the world.
/// </summary>
public sealed class UiBuildingContext(UiSelectedBuilding building, Simulation.WorldSnapshot snapshot, Func<long> tick,
    Action<Simulation.SimCommand> enqueue)
{
    /// <summary>
    /// The building or piece the panel shows.
    /// </summary>
    public UiSelectedBuilding Building { get; } = building;

    /// <summary>
    /// The snapshot the panel is laid out from (the one on screen).
    /// </summary>
    public Simulation.WorldSnapshot Snapshot { get; } = snapshot;

    /// <summary>
    /// The world's tick now, for a command's <see cref="Simulation.SimCommand.Tick"/>.
    /// </summary>
    public long Tick => tick();

    /// <summary>
    /// Queues a command for the world.
    /// </summary>
    public void Enqueue(Simulation.SimCommand command) => enqueue(command);
}

/// <summary>
/// The building a click selected, as its panel shows it: <paramref name="Instance"/> is the key the simulation knows it by (a player-built
/// building's or placed piece's id, a town building's placement key, a piece of furniture's id;
/// <see cref="Simulation.Player.PlayerState.InspectedBuilding"/> holds the same), <paramref name="Record"/> its BUILDING or item record's string id
/// (null when unknown), <paramref name="SiteId"/> the player's building id (also a rock's or a town machine's site) or null, <paramref name="Name"/>
/// the panel's name, and <paramref name="Owned"/> whether the player's faction owns it (its own building, or a town building it bought), which is
/// when the panel shows the game's own buttons.
/// </summary>
public sealed record UiSelectedBuilding(string Instance, string? Record, string? SiteId, string Name, bool Owned);

/// <summary>
/// A line an extension adds to the HUD's town panel under the game's own (<see cref="UiRegistration.AddTownPanelLines"/>): <paramref name="Label"/>
/// in the panel's label column; <paramref name="Value"/> (null or blank: none) in its value column, as text, or as the caption of a button there
/// when the line has a <paramref name="Press"/>; a line with a press and no value is one button across the line, captioned with the label.
/// <paramref name="Tooltip"/> (null: none) shows over the line. A press runs on the host's thread and changes the world only by queueing commands.
/// </summary>
public sealed record UiTownLine(string Label, string? Value = null, string? Tooltip = null, Action? Press = null)
{
    /// <summary>
    /// The line's colours and icon (<see cref="UiLineStyle"/>); null: the panel's own look.
    /// </summary>
    public UiLineStyle? Style { get; init; }
}

/// <summary>
/// What an extension's town panel lines get: the outpost the panel shows, whether the player's faction owns it, the snapshot the panel is laid out
/// from, the tick read when asked, and the command queue, through which a press changes the world.
/// </summary>
public sealed class UiTownContext(Simulation.Production.OutpostSnapshot town, bool owned, Simulation.WorldSnapshot snapshot, Func<long> tick,
    Action<Simulation.SimCommand> enqueue)
{
    /// <summary>
    /// The outpost the panel shows: its <see cref="Simulation.Production.OutpostSnapshot.Number"/> is the
    /// <see cref="Simulation.Construction.PlayerTown.Number"/> commands name it by (<c>world.Buildings.FindTown</c>), its name, centre, radius,
    /// owner, Open to public and power.
    /// </summary>
    public Simulation.Production.OutpostSnapshot Town { get; } = town;

    /// <summary>
    /// Whether the player's faction owns the outpost, as the panel decides when it lets the player rename it and tick Open to public (true for every
    /// outpost the panel shows today: it shows only the player's).
    /// </summary>
    public bool Owned { get; } = owned;

    /// <summary>
    /// The snapshot the panel is laid out from (the one on screen).
    /// </summary>
    public Simulation.WorldSnapshot Snapshot { get; } = snapshot;

    /// <summary>
    /// The world's tick now, for a command's <see cref="Simulation.SimCommand.Tick"/>.
    /// </summary>
    public long Tick => tick();

    /// <summary>
    /// Queues a command for the world.
    /// </summary>
    public void Enqueue(Simulation.SimCommand command) => enqueue(command);
}

/// <summary>
/// What an action handler gets: the frame's data and the control's argument.
/// </summary>
public sealed record UiActionContext(UiFrameContext Frame, string Argument)
{
    /// <summary>
    /// A text box's text after the change that ran the action (<see cref="UiNodeKind.TextBox"/>); empty for a click.
    /// </summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// What ran the action: the press (as every action has always been run), a text change, or one of the pointer events the node asks for
    /// (<see cref="UiNode.Pointer"/>).
    /// </summary>
    public UiEventKind Kind { get; init; }

    /// <summary>
    /// The id of the node the event is for (the one that asked for it); empty for a control the host made.
    /// </summary>
    public string Node { get; init; } = "";

    /// <summary>
    /// The pointer, screen pixels, for a pointer event.
    /// </summary>
    public System.Numerics.Vector2 Position { get; init; }

    /// <summary>
    /// Where the left button was pressed, for a press, a drag and a release.
    /// </summary>
    public System.Numerics.Vector2 Start { get; init; }

    /// <summary>
    /// How far the pointer moved since the last drag event (the press for the first), for a drag.
    /// </summary>
    public System.Numerics.Vector2 Delta { get; init; }

    /// <summary>
    /// The wheel's steps, for a wheel event: above 0 away from the player (up), below 0 towards.
    /// </summary>
    public float Wheel { get; init; }

    /// <summary>
    /// For a drag or a release: the id of the topmost overlay node under the pointer (empty: none), the drop target of a drag and drop.
    /// </summary>
    public string Over { get; init; } = "";

    /// <summary>
    /// The button of a press: the left for every press but those <see cref="UiPointerEvents.Buttons"/> asks for.
    /// </summary>
    public Host.Input.MouseButton Button { get; init; }
}

/// <summary>
/// What an extension's right-click menu rows get (<see cref="UiRegistration.AddCharacterMenuVerbs"/>): the character the menu is for, the selected
/// characters and the cursor when the menu was built; Shift (it turns the game's own orders into jobs) and the tick read when asked, so a row's
/// action sees them as the player chooses it; and the command queue.
/// </summary>
public sealed class UiMenuContext(Simulation.CharacterSnapshot target, IReadOnlyList<Simulation.CharacterId> selected,
    System.Numerics.Vector2 cursor, Func<bool> shift, Func<long> tick, Action<Simulation.SimCommand> enqueue)
{
    /// <summary>
    /// The character the menu is for.
    /// </summary>
    public Simulation.CharacterSnapshot Target { get; } = target;

    /// <summary>
    /// The selected characters when the menu was built.
    /// </summary>
    public IReadOnlyList<Simulation.CharacterId> Selected { get; } = selected;

    /// <summary>
    /// The cursor (screen pixels) when the menu was built.
    /// </summary>
    public System.Numerics.Vector2 Cursor { get; } = cursor;

    /// <summary>
    /// Whether Shift is held now.
    /// </summary>
    public bool Shift => shift();

    /// <summary>
    /// The world's tick now, for a command's <see cref="Simulation.SimCommand.Tick"/>.
    /// </summary>
    public long Tick => tick();

    /// <summary>
    /// Queues a command for the world.
    /// </summary>
    public void Enqueue(Simulation.SimCommand command) => enqueue(command);
}

/// <summary>
/// A row an extension adds to a character's right-click menu: its caption and what choosing it does (on the host's thread).
/// </summary>
public sealed record UiMenuVerb(string Caption, Action Choose)
{
    /// <summary>
    /// The selection cannot carry it out now (for example it lacks the item it needs). With the Interface setting "Unavailable menu rows" on
    /// Greyed (the default) the row is drawn greyed, a plain click does nothing and a Shift click still chooses it (a mod row reads Shift itself,
    /// so it can set up a job for later); on Faithful the row is drawn and chosen as any other.
    /// </summary>
    public bool Unavailable { get; init; }

    /// <summary>
    /// The caption's colour (RGB, 0 to 1 each, clamped; alpha not used); null: the menu's own. An unavailable row stays greyed.
    /// </summary>
    public Vector4? Colour { get; init; }
}

/// <summary>
/// Read-only presentation data. UI extensions cannot mutate UiBindings or receive the simulation world.
/// </summary>
public sealed class UiFrameContext
{
    /// <summary>
    /// The host the frame's controls come from.
    /// </summary>
    readonly UiHost _host;

    /// <summary>
    /// The bound texts by key.
    /// </summary>
    readonly Dictionary<string, string> _text;

    /// <summary>
    /// The disabled keys.
    /// </summary>
    readonly HashSet<string> _disabled;

    /// <summary>
    /// The frame's data from <paramref name="bindings"/> (copied): mode, viewport, texts and disabled keys.
    /// </summary>
    internal UiFrameContext(UiHost host, UiMode mode, int width, int height, UiBindings bindings)
    {
        this._host = host;
        Mode = mode;
        Viewport = new(0, 0, width, height);
        _text = new(bindings.Text, StringComparer.Ordinal);
        _disabled = new(bindings.Disabled, StringComparer.Ordinal);
    }

    /// <summary>
    /// The UI mode the frame is drawn in.
    /// </summary>
    public UiMode Mode { get; }

    /// <summary>
    /// The whole screen, from 0, 0.
    /// </summary>
    public UiRect Viewport { get; }

    /// <summary>
    /// The bound text of <paramref name="key"/>, else <paramref name="fallback"/>.
    /// </summary>
    public string Text(string key, string fallback = "") => _text.GetValueOrDefault(key) ?? fallback;

    /// <summary>
    /// Whether <paramref name="key"/> is not disabled.
    /// </summary>
    public bool Enabled(string key) => !_disabled.Contains(key);

    /// <summary>
    /// A registered control <paramref name="id"/>, the last override's if any.
    /// </summary>
    public UiNode CreateControl(string id) => _host.CreateControl(id, this);

    /// <summary>
    /// A registered control <paramref name="id"/> as its owner made it, without overrides.
    /// </summary>
    public UiNode CreateBaseControl(string id) => _host.CreateControl(id, this, useOverrides: false);

    /// <summary>
    /// The size in pixels <paramref name="text"/> takes as a node's caption in font resource <paramref name="font"/> at text scale
    /// <paramref name="scale"/> (a node's <see cref="UiNode.TextScale"/>, finite and above 0; 1 is a style's default): its widest line and its lines'
    /// height, line breaks kept, colour tags measured as drawn. Zero without an interface that draws text (a headless host). Host thread.
    /// </summary>
    public Vector2 Measure(string text, string font = "Kenshi_StandardFont_Medium", float scale = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(font);
        if (!float.IsFinite(scale) || scale <= 0 || scale > UiHost.MaxTextScale)
            throw new ArgumentOutOfRangeException(nameof(scale));

        return text.Length == 0 || _host.Measurer is not { } measure ? Vector2.Zero : measure(font, text, scale);
    }
}

/// <summary>
/// Platform-independent native UI extension host. Access on the UI thread only; no DLL discovery/loading.
/// </summary>
public sealed partial class UiHost
{
    #region State

    /// <summary>
    /// The extension API version the host provides; an extension must ask for it.
    /// </summary>
    public const int ApiVersion = 1;

    /// <summary>
    /// The property that puts an overlay control on another layer than the HUD's (<see cref="UiNode.Layer"/>): <c>Back</c> or <c>Popup</c>.
    /// </summary>
    public const string LayerProperty = "ModLayer";

    /// <summary>
    /// Measures a caption (font resource, text, text scale) as the interface's painter draws it (<see cref="UiFrameContext.Measure"/>); null
    /// without one.
    /// </summary>
    internal Func<string, string, float, Vector2>? Measurer { get; set; }

    /// <summary>
    /// The styles by id, with their owners.
    /// </summary>
    readonly Dictionary<string, (string Owner, UiStyle Style)> _styles = new(StringComparer.Ordinal);

    /// <summary>
    /// The control factories by id, with their owners.
    /// </summary>
    readonly Dictionary<string, (string Owner, Func<UiFrameContext, UiNode> Factory)> _controls = new(StringComparer.Ordinal);

    /// <summary>
    /// The action handlers by id, with their owners.
    /// </summary>
    readonly Dictionary<string, (string Owner, Action<UiActionContext> Handler)> _actions = new(StringComparer.Ordinal);

    /// <summary>
    /// The style overrides in registration order (the last wins).
    /// </summary>
    readonly List<(string Owner, string Target, UiStyle Style)> _overrides = [];

    /// <summary>
    /// The control overrides in registration order (the last wins).
    /// </summary>
    readonly List<(string Owner, string Target, Func<UiFrameContext, UiNode> Factory)> _controlOverrides = [];

    /// <summary>
    /// The squad AI tab option sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiSquadAiContext, IEnumerable<UiSquadAiOption>> Options)> _squadAiOptions = [];

    /// <summary>
    /// The building panel button sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiBuildingContext, IEnumerable<UiBuildingAction>> Actions)> _buildingActions = [];

    /// <summary>
    /// The most lines the extensions together add to the town panel: as many as the game's own, so the panel at most doubles.
    /// </summary>
    public const int MaxTownLines = 4;

    /// <summary>
    /// The most characters a town panel line's label or value keeps; the panel cuts what does not fit its column.
    /// </summary>
    public const int MaxTownLineText = 64;

    /// <summary>
    /// The most characters a town panel line's tooltip keeps (the tooltip wraps it).
    /// </summary>
    public const int MaxTownLineTooltip = 1000;

    /// <summary>
    /// The extensions' item tooltip line sources (<see cref="UiRegistration.AddItemTooltipLines"/>).
    /// </summary>
    internal ItemTooltipLines ItemTips { get; } = new();

    /// <summary>
    /// The extensions' building panel bar sources (<see cref="UiRegistration.AddBuildingRows"/>).
    /// </summary>
    internal BuildingPanelRows BuildingBars { get; } = new();

    /// <summary>
    /// The town panel line sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiTownContext, IEnumerable<UiTownLine>> Lines)> _townLines = [];

    /// <summary>
    /// The extensions whose town panel lines were left out past <see cref="MaxTownLines"/>, each reported once.
    /// </summary>
    readonly HashSet<string> _townLinesCut = new(StringComparer.Ordinal);

    /// <summary>
    /// The extensions' character panel line and job panel button sources (<see cref="UiRegistration.AddCharacterPanelLines"/>,
    /// <see cref="UiRegistration.AddJobPanelButtons"/>).
    /// </summary>
    internal CharacterPanelHooks CharacterPanel { get; } = new();

    /// <summary>
    /// The extensions' controls in the faithful screens and the HUD (<see cref="UiRegistration.AddScreenControls"/>).
    /// </summary>
    internal ScreenControls Screens { get; } = new();

    /// <summary>
    /// The extensions' changes to the faithful screens' and the HUD's own widgets (<see cref="UiRegistration.AddScreenOverrides"/>).
    /// </summary>
    internal ScreenOverrides ScreenOverrides { get; } = new();

    /// <summary>
    /// The extensions' player replies in conversations (<see cref="UiRegistration.AddDialogueReplies"/>).
    /// </summary>
    internal DialogueReplies Replies { get; } = new();

    /// <summary>
    /// The extensions' right-click menu rows for any target (<see cref="UiRegistration.AddMenuVerbs"/>).
    /// </summary>
    internal MenuTargetVerbs TargetVerbs { get; } = new();

    /// <summary>
    /// The extensions' changes to right-click menu rows (<see cref="UiRegistration.AddMenuRowOverrides"/>).
    /// </summary>
    internal MenuRowOverrides MenuRows { get; } = new();

    /// <summary>
    /// The extensions' world map input handlers (<see cref="UiRegistration.AddMapInput"/>).
    /// </summary>
    internal MapInputs Map { get; } = new();

    /// <summary>
    /// The extensions' interface key handlers (<see cref="UiRegistration.AddKeyInput"/>).
    /// </summary>
    internal KeyInputs KeyInput { get; } = new();

    /// <summary>
    /// The translations of each extension's code mod (<see cref="Register(IUiExtension, string?, Localisation.ModTranslations?)"/>), by extension id.
    /// </summary>
    readonly Dictionary<string, Localisation.ModTranslations> _texts = [];

    /// <summary>
    /// The contributions: owner, slot, mode (null: every mode), order and factory.
    /// </summary>
    readonly List<(string Owner, string Slot, UiMode? Mode, int Order, Func<UiFrameContext, UiNode> Factory)> _contributions = [];

    /// <summary>
    /// The right-click menu row sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiMenuContext, IEnumerable<UiMenuVerb>> Verbs)> _menuVerbs = [];

    /// <summary>
    /// The registered extensions' ids.
    /// </summary>
    readonly HashSet<string> _extensions = new(StringComparer.Ordinal);

    /// <summary>
    /// Where the extensions' map marker images may come from, and the image problems already reported.
    /// </summary>
    readonly MarkerImagePaths _markerImages = new();

    /// <summary>
    /// The world map marker sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiMapContext, IEnumerable<UiMapMarker>> Markers)> _mapMarkers = [];

    /// <summary>
    /// The data of the last arranged frame (the actions get it); null before the first.
    /// </summary>
    UiFrameContext? _frame;

    /// <summary>
    /// An extension failed (a contribution's build, an action, a map marker source, a right-click menu row source or a row's action, a squad
    /// AI option source or press, a building panel button source or press, a town panel line, character panel line or job panel button source or
    /// press, or lines or buttons past a panel's limit).
    /// </summary>
    public event Action<UiDiagnostic>? Diagnostic;

    /// <summary>
    /// Routes the text sources' failures to the interface that added them.
    /// </summary>
    static UiHost() => ShownText.Dropped += ReportDroppedSource;

    /// <summary>
    /// Reports a text source that threw (<see cref="ShownText.Dropped"/>) as its interface's <see cref="Diagnostic"/>.
    /// </summary>
    static void ReportDroppedSource(object host, string owner, ShownTextContext context, Exception error) =>
        (host as UiHost)?.Diagnostic?.Invoke(new UiDiagnostic(owner, $"shown text ({context.Kind} {context.Key}, dropped)", error));

    #endregion

    #region Registration

    /// <summary>
    /// Registers <paramref name="extension"/> (a valid id, the host's API version, not registered yet); its scope's disposal removes everything it
    /// added.
    /// </summary>
    public IDisposable Register(IUiExtension extension) => Register(extension, null);

    /// <summary>
    /// Registers <paramref name="extension"/> as <see cref="Register(IUiExtension)"/> does, with the folder of the code mod it belongs to (null:
    /// none), under which its map marker images lie.
    /// </summary>
    public IDisposable Register(IUiExtension extension, string? folder) => Register(extension, folder, null);

    /// <summary>
    /// Registers <paramref name="extension"/> as <see cref="Register(IUiExtension, string?)"/> does, with its code mod's translations (null: none),
    /// through which its node texts, menu rows, squad AI options and building panel buttons are shown (docs/modding.md "Translations").
    /// </summary>
    public IDisposable Register(IUiExtension extension, string? folder, Localisation.ModTranslations? texts)
    {
        ArgumentNullException.ThrowIfNull(extension);

        string id = extension.Id;
        ValidateName(id);
        if (extension.ApiVersion != ApiVersion)
            throw new NotSupportedException($"UI extension {id} requires API {extension.ApiVersion}; host provides {ApiVersion}.");
        if (!_extensions.Add(id)) throw new InvalidOperationException($"UI extension already registered: {id}");
        if (folder is not null) _markerImages.SetFolder(id, folder);
        if (texts is not null) _texts[id] = texts;

        var scope = new UiRegistration(this, id, folder);
        try
        {
            extension.Register(scope);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Throws for a name that is blank or holds anything but ASCII letters, digits, '.', '-' and '_'.
    /// </summary>
    internal static void ValidateName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException($"Invalid UI registration name: {id}");
    }

    /// <summary>
    /// Adds style <paramref name="id"/> (its padding checked) for <paramref name="owner"/>.
    /// </summary>
    internal void AddStyle(string owner, string id, UiStyle style)
    {
        UiInsets.Parse(style.TextPadding.ToString());
        _styles.Add(id, (owner, style));
    }

    /// <summary>
    /// Adds control factory <paramref name="id"/> for <paramref name="owner"/>.
    /// </summary>
    internal void AddControl(string owner, string id, Func<UiFrameContext, UiNode> factory) => _controls.Add(id, (owner, factory));

    /// <summary>
    /// Adds action handler <paramref name="id"/> for <paramref name="owner"/>.
    /// </summary>
    internal void AddAction(string owner, string id, Action<UiActionContext> action) => _actions.Add(id, (owner, action));

    /// <summary>
    /// Overrides the registered style <paramref name="id"/> (its padding checked) for <paramref name="owner"/>.
    /// </summary>
    internal void Override(string owner, string id, UiStyle style)
    {
        UiInsets.Parse(style.TextPadding.ToString());
        if (!_styles.ContainsKey(id)) throw new KeyNotFoundException($"Unknown UI style: {id}");

        _overrides.Add((owner, id, style));
    }

    /// <summary>
    /// Overrides the registered control <paramref name="id"/> for <paramref name="owner"/>.
    /// </summary>
    internal void OverrideControl(string owner, string id, Func<UiFrameContext, UiNode> factory)
    {
        if (!_controls.ContainsKey(id)) throw new KeyNotFoundException($"Unknown UI control: {id}");

        _controlOverrides.Add((owner, id, factory));
    }

    /// <summary>
    /// Adds a squad AI tab option source for <paramref name="owner"/>.
    /// </summary>
    internal void AddSquadAiOptions(string owner, Func<UiSquadAiContext, IEnumerable<UiSquadAiOption>> options) =>
        _squadAiOptions.Add((owner, options));

    /// <summary>
    /// Adds a building panel button source for <paramref name="owner"/>.
    /// </summary>
    internal void AddBuildingActions(string owner, Func<UiBuildingContext, IEnumerable<UiBuildingAction>> actions) =>
        _buildingActions.Add((owner, actions));

    /// <summary>
    /// Adds a town panel line source for <paramref name="owner"/>.
    /// </summary>
    internal void AddTownLines(string owner, Func<UiTownContext, IEnumerable<UiTownLine>> lines) => _townLines.Add((owner, lines));

    /// <summary>
    /// Adds a contribution to <paramref name="slot"/> for <paramref name="owner"/>.
    /// </summary>
    internal void Contribute(string owner, string slot, UiMode? mode, int order, Func<UiFrameContext, UiNode> factory) =>
        _contributions.Add((owner, slot, mode, order, factory));

    /// <summary>
    /// Adds a world map marker source for <paramref name="owner"/>.
    /// </summary>
    internal void AddMapMarkers(string owner, Func<UiMapContext, IEnumerable<UiMapMarker>> markers) => _mapMarkers.Add((owner, markers));

    /// <summary>
    /// Adds a right-click menu row source for <paramref name="owner"/>.
    /// </summary>
    internal void AddMenuVerbs(string owner, Func<UiMenuContext, IEnumerable<UiMenuVerb>> verbs) => _menuVerbs.Add((owner, verbs));

    /// <summary>
    /// Removes everything <paramref name="owner"/> registered.
    /// </summary>
    internal void Remove(string owner)
    {
        _squadAiOptions.RemoveAll(o => o.Owner == owner);
        _buildingActions.RemoveAll(a => a.Owner == owner);
        _townLines.RemoveAll(l => l.Owner == owner);
        ItemTips.Remove(owner);
        ShownText.Remove(this, owner);
        BuildingBars.Remove(owner);
        CharacterPanel.Remove(owner);
        Screens.Remove(owner);
        ScreenOverrides.Remove(owner);
        Replies.Remove(owner);
        TargetVerbs.Remove(owner);
        MenuRows.Remove(owner);
        Map.Remove(owner);
        KeyInput.Remove(owner);
        _texts.Remove(owner);
        foreach (string id in _styles.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _styles.Remove(id);
        foreach (string id in _controls.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _controls.Remove(id);
        foreach (string id in _actions.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _actions.Remove(id);
        _overrides.RemoveAll(o => o.Owner == owner);
        _controlOverrides.RemoveAll(o => o.Owner == owner);
        _contributions.RemoveAll(c => c.Owner == owner);
        _mapMarkers.RemoveAll(m => m.Owner == owner);
        _markerImages.Remove(owner);
        _menuVerbs.RemoveAll(v => v.Owner == owner);
        RemovePointerState(owner);
        _extensions.Remove(owner);
    }

    #endregion

    #region Lookup

    /// <summary>
    /// Whether any extension adds rows to the right-click menu (<see cref="CharacterMenuVerbs"/>).
    /// </summary>
    public bool HasMenuVerbs => _menuVerbs.Count > 0;

    /// <summary>
    /// The rows the extensions add to a character's right-click menu, in registration order (a copy of the sources is walked, so a source may
    /// register or dispose). A source that throws, or names a row without a caption or action, is reported once as a <see cref="Diagnostic"/> and
    /// dropped (it is asked every frame a character is under the cursor). A row's action that throws is reported each time.
    /// </summary>
    public IReadOnlyList<UiMenuVerb> CharacterMenuVerbs(UiMenuContext context)
    {
        var rows = new List<UiMenuVerb>();
        foreach (var entry in _menuVerbs.ToArray())
        {
            try
            {
                var added = entry.Verbs(context).ToList();
                if (added.Any(v => v is null || string.IsNullOrWhiteSpace(v.Caption) || v.Choose is null))
                    throw new InvalidOperationException("A menu row needs a caption and an action.");

                rows.AddRange(added.Select(v => v with { Caption = ModText(entry.Owner, v.Caption), Choose = Guarded(entry.Owner, v) }));
            }
            catch (Exception e)
            {
                _menuVerbs.Remove(entry);
                Diagnostic?.Invoke(new UiDiagnostic(entry.Owner, "menu verbs (dropped)", e));
            }
        }

        return rows;
    }

    /// <summary>
    /// Whether any extension adds rows to the right-click menus of any target (<see cref="MenuVerbs"/>).
    /// </summary>
    public bool HasTargetMenuVerbs => TargetVerbs.Any;

    /// <summary>
    /// The rows the extensions add to the right-click menu of <paramref name="context"/>'s target (<see cref="MenuTargetVerbs.For"/>), with the
    /// character rows' rules: a failing source reported once and dropped, a row's action that throws reported each time.
    /// </summary>
    public IReadOnlyList<UiMenuVerb> MenuVerbs(UiMenuTargetContext context) => TargetVerbs.For(context, Guarded, Diagnostic, ModText);

    /// <summary>
    /// Whether any extension changes right-click menu rows (<see cref="MenuRowOverride"/>).
    /// </summary>
    public bool HasMenuRowOverrides => MenuRows.Any;

    /// <summary>
    /// The extensions' changes to <paramref name="row"/> (<see cref="MenuRowOverrides.For"/>); null for none.
    /// </summary>
    public UiMenuRowOverride? MenuRowOverride(UiMenuRowContext row) => MenuRows.For(row, Diagnostic);

    /// <summary>
    /// Whether any extension handles world map input (<see cref="MapInput"/>).
    /// </summary>
    public bool HasMapInput => Map.Any;

    /// <summary>
    /// Hands a world map input to the extensions (<see cref="MapInputs.Handle"/>); true when one took it.
    /// </summary>
    public bool MapInput(UiMapInput input) => Map.Handle(input, Diagnostic);

    /// <summary>
    /// Whether any extension handles interface keys (<see cref="KeyInputTaken"/>).
    /// </summary>
    public bool HasKeyInput => KeyInput.Any;

    /// <summary>
    /// Hands a key going down to the extensions (<see cref="KeyInputs.Handle"/>); true when one took it.
    /// </summary>
    public bool KeyInputTaken(UiKeyInput input) => KeyInput.Handle(input, Diagnostic);

    /// <summary>
    /// A text extension <paramref name="owner"/> shows (a node's, a menu row's, an option's): in its code mod's translation, else as written.
    /// </summary>
    internal string ModText(string owner, string text) => _texts.TryGetValue(owner, out var texts) ? texts.Text(text) : text;

    /// <summary>
    /// <see cref="ModText"/> of a text that may be null.
    /// </summary>
    string? ModTextOrNull(string owner, string? text) => text is null ? null : ModText(owner, text);

    /// <summary>
    /// A menu row's action that reports a throw as a <see cref="Diagnostic"/> instead of passing it on to the input, and does nothing once its
    /// extension is removed.
    /// </summary>
    Action Guarded(string owner, UiMenuVerb row) => () =>
    {
        if (!_extensions.Contains(owner)) return;

        try
        {
            row.Choose();
        }
        catch (Exception e)
        {
            Diagnostic?.Invoke(new UiDiagnostic(owner, $"menu row {row.Caption}", e));
        }
    };

    /// <summary>
    /// Style <paramref name="id"/>, the last override's if any; the default for an empty id.
    /// </summary>
    public UiStyle Style(string id)
    {
        if (id.Length == 0) return new();
        if (!_styles.TryGetValue(id, out var entry)) throw new KeyNotFoundException($"Unknown UI style: {id}");

        return _overrides.LastOrDefault(o => o.Target == id).Style ?? entry.Style;
    }

    /// <summary>
    /// Makes control <paramref name="id"/> for the frame (with <paramref name="useOverrides"/>, the last override's factory).
    /// </summary>
    internal UiNode CreateControl(string id, UiFrameContext context, bool useOverrides = true)
    {
        if (!_controls.TryGetValue(id, out var entry)) throw new KeyNotFoundException($"Unknown UI control: {id}");

        var factory = useOverrides ? _controlOverrides.LastOrDefault(o => o.Target == id).Factory ?? entry.Factory : entry.Factory;
        return factory(context);
    }

    /// <summary>
    /// Whether any extension adds world map markers (<see cref="MapMarkers"/>).
    /// </summary>
    public bool HasMapMarkers => _mapMarkers.Count > 0;

    /// <summary>
    /// The markers the extensions add to the world map, with their owners, in registration order (a copy of the sources is walked, so a source may
    /// register or dispose). A source that throws, or gives a marker whose group <paramref name="knownGroup"/> does not know, with a point, tint or
    /// size not finite, a size not above 0 or a zoom level outside 0 to 2, is reported once as a <see cref="Diagnostic"/> and dropped (it is asked
    /// every frame the map is shown). Markers with an image are left out here.
    /// </summary>
    public IReadOnlyList<(string Owner, UiMapMarker Marker)> MapMarkers(UiMapContext context, Func<string, bool> knownGroup) =>
        MapMarkers(context, knownGroup, null);

    /// <summary>
    /// The markers as the two-argument overload gives them, and those with an image file: the image is resolved under the owner's mod folder
    /// (<see cref="MarkerImagePaths"/>) and judged by <paramref name="imageProblem"/> (null: fine, else why not), and the marker comes back with the
    /// full path. A marker whose image cannot be shown is left out alone, its source kept, and each such problem is reported once as a
    /// <see cref="Diagnostic"/>; an image marker's group must be empty or known.
    /// </summary>
    public IReadOnlyList<(string Owner, UiMapMarker Marker)> MapMarkers(UiMapContext context, Func<string, bool> knownGroup,
        Func<string, string?>? imageProblem)
    {
        var markers = new List<(string Owner, UiMapMarker Marker)>();
        foreach (var entry in _mapMarkers.ToArray())
        {
            try
            {
                var added = entry.Markers(context).ToList();
                int bad = added.FindIndex(m => !ValidMarker(m, knownGroup));
                if (bad >= 0)
                {
                    throw new InvalidOperationException(
                        $"A map marker needs a known group or an image, a finite point and tint, and a size: {added[bad]}.");
                }

                foreach (var m in added)
                    if (Shown(entry.Owner, m, imageProblem) is { } shown) markers.Add((entry.Owner, shown));
            }
            catch (Exception e)
            {
                _mapMarkers.Remove(entry);
                Diagnostic?.Invoke(new UiDiagnostic(entry.Owner, "map markers (dropped)", e));
            }
        }

        return markers;
    }

    /// <summary>
    /// Whether a map marker's group is known (with an image: empty or known; without, not blank and known) and it has a finite point and tint, a
    /// finite size above 0 and a zoom level from 0 to 2.
    /// </summary>
    static bool ValidMarker(UiMapMarker? m, Func<string, bool> knownGroup) =>
        m is not null && KnownGroup(m, knownGroup) && float.IsFinite(m.At.X) && float.IsFinite(m.At.Y)
        && float.IsFinite(m.Colour.X + m.Colour.Y + m.Colour.Z + m.Colour.W)
        && float.IsFinite(m.Scale) && m.Scale > 0 && m.FromLevel is >= 0 and <= 2;

    /// <summary>
    /// Whether a marker's group is known: without an image it must name one of the set; with one it only sizes the image, so it may be empty.
    /// </summary>
    static bool KnownGroup(UiMapMarker m, Func<string, bool> knownGroup) =>
        m.Image is null ? !string.IsNullOrWhiteSpace(m.Group) && knownGroup(m.Group) : string.IsNullOrEmpty(m.Group) || knownGroup(m.Group);

    /// <summary>
    /// <paramref name="m"/> as shown: as it is without an image, with its image's full path, or null (reported once) when the image cannot be shown.
    /// </summary>
    UiMapMarker? Shown(string owner, UiMapMarker m, Func<string, string?>? imageProblem)
    {
        if (m.Image is null) return m;
        if (_markerImages.Resolve(owner, m.Image, imageProblem, out string full) is not { } problem) return m with { Image = full };

        if (_markerImages.FirstReport(owner, m.Image, problem))
            Diagnostic?.Invoke(new UiDiagnostic(owner, "map marker image (left out)", new InvalidOperationException($"{m.Image}: {problem}")));
        return null;
    }

    /// <summary>
    /// Whether any extension adds squad AI tab options (<see cref="SquadAiOptions"/>).
    /// </summary>
    public bool HasSquadAiOptions => _squadAiOptions.Count > 0;

    /// <summary>
    /// The tick buttons the extensions add to the squad AI tab, in registration order (a copy of the sources is walked, so a source may register or
    /// dispose). A source that throws, or names an option without a caption or press, is reported once as a <see cref="Diagnostic"/> and dropped
    /// (it is asked on every layout pass while the tab is open). An option's press that throws is reported each time.
    /// </summary>
    public IReadOnlyList<UiSquadAiOption> SquadAiOptions(UiSquadAiContext context)
    {
        var options = new List<UiSquadAiOption>();
        foreach (var entry in _squadAiOptions.ToArray())
        {
            try
            {
                var added = entry.Options(context).ToList();
                if (added.Any(o => o is null || string.IsNullOrWhiteSpace(o.Caption) || o.Press is null))
                    throw new InvalidOperationException("A squad AI option needs a caption and a press.");

                options.AddRange(added.Select(o => o with
                {
                    Caption = ModText(entry.Owner, o.Caption),
                    Tooltip = ModText(entry.Owner, o.Tooltip ?? ""),
                    Press = GuardedPress(entry.Owner, $"squad AI option {o.Caption}", o.Press),
                }));
            }
            catch (Exception e)
            {
                _squadAiOptions.Remove(entry);
                Diagnostic?.Invoke(new UiDiagnostic(entry.Owner, "squad AI options (dropped)", e));
            }
        }

        return options;
    }

    /// <summary>
    /// Whether any extension adds building panel buttons (<see cref="BuildingActions"/>).
    /// </summary>
    public bool HasBuildingActions => _buildingActions.Count > 0;

    /// <summary>
    /// The buttons the extensions add to the selected building's panel, in registration order (a copy of the sources is walked, so a source may
    /// register or dispose). A source that throws, or names a button without a caption or press, is reported once as a <see cref="Diagnostic"/>
    /// and dropped (it is asked whenever the panel is laid out). A press that throws is reported each time.
    /// </summary>
    public IReadOnlyList<UiBuildingAction> BuildingActions(UiBuildingContext context)
    {
        var actions = new List<UiBuildingAction>();
        foreach (var entry in _buildingActions.ToArray())
        {
            try
            {
                var added = entry.Actions(context).ToList();
                if (added.Any(a => a is null || string.IsNullOrWhiteSpace(a.Caption) || a.Press is null))
                    throw new InvalidOperationException("A building panel button needs a caption and a press.");

                actions.AddRange(added.Select(a => a with
                {
                    Caption = ModText(entry.Owner, a.Caption),
                    Tooltip = ModTextOrNull(entry.Owner, a.Tooltip),
                    Press = GuardedPress(entry.Owner, $"building panel button {a.Caption}", a.Press),
                }));
            }
            catch (Exception e)
            {
                _buildingActions.Remove(entry);
                Diagnostic?.Invoke(new UiDiagnostic(entry.Owner, "building panel buttons (dropped)", e));
            }
        }

        return actions;
    }

    /// <summary>
    /// The lines the extensions add to an item's tooltip (<see cref="ItemTooltipLines.For"/>); a failing source is reported as a
    /// <see cref="Diagnostic"/> and dropped.
    /// </summary>
    public IReadOnlyList<UiItemTooltipLine> ItemTooltipLines(UiItemTooltipContext item) => ItemTips.For(item, Diagnostic);

    /// <summary>
    /// The name the interface shows for an item of <paramref name="record"/> named <paramref name="name"/> with tag <paramref name="modTag"/>: the
    /// extensions' text sources' (<see cref="ShownText.Record"/>, the item record's name), else <paramref name="name"/>.
    /// </summary>
    public string ItemName(string record, string name, string modTag = "") =>
        ShownText.Record(record, name, ShownText.RecordName, modTag);

    /// <summary>
    /// Whether any extension adds building panel bars (<see cref="BuildingRows"/>).
    /// </summary>
    public bool HasBuildingRows => BuildingBars.Any;

    /// <summary>
    /// The bars the extensions add under the selected building's Condition (<see cref="BuildingPanelRows.For"/>); a failing source is reported as a
    /// <see cref="Diagnostic"/> and dropped.
    /// </summary>
    public IReadOnlyList<UiBuildingRow> BuildingRows(UiBuildingContext context) => BuildingBars.For(context, Diagnostic);

    /// <summary>
    /// Whether any extension adds character panel lines (<see cref="CharacterPanelLines"/>).
    /// </summary>
    public bool HasCharacterPanelLines => CharacterPanel.AnyLines;

    /// <summary>
    /// The lines the extensions add to the character panel (<see cref="CharacterPanelHooks.Lines"/>), with the town panel lines' rules: at most
    /// <see cref="CharacterPanelHooks.MaxLines"/>, texts cut, a failing source reported as a <see cref="Diagnostic"/> once and dropped.
    /// </summary>
    public IReadOnlyList<UiCharacterLine> CharacterPanelLines(UiCharacterContext context) =>
        CharacterPanel.Lines(context, GuardedPress, Diagnostic);

    /// <summary>
    /// Whether any extension adds job panel buttons (<see cref="JobPanelButtons"/>).
    /// </summary>
    public bool HasJobPanelButtons => CharacterPanel.AnyButtons;

    /// <summary>
    /// The buttons the extensions add beside the jobs list (<see cref="CharacterPanelHooks.Buttons"/>), with the town panel lines' rules: at most
    /// <see cref="CharacterPanelHooks.MaxButtons"/>, captions cut, a failing source reported as a <see cref="Diagnostic"/> once and dropped.
    /// </summary>
    public IReadOnlyList<UiJobButton> JobPanelButtons(UiCharacterContext context) => CharacterPanel.Buttons(context, GuardedPress, Diagnostic);

    /// <summary>
    /// Whether any extension adds controls to a screen (<see cref="ArrangeScreenControls"/>).
    /// </summary>
    public bool HasScreenControls => Screens.Any;

    /// <summary>
    /// Whether any extension changes a screen's own widgets (<see cref="ArrangeScreenControls"/>).
    /// </summary>
    public bool HasScreenOverrides => ScreenOverrides.Any;

    /// <summary>
    /// Changes <paramref name="view"/>'s screen's own widgets in its arranged <paramref name="controls"/> (<see cref="ScreenOverrides.Apply"/>), then
    /// adds the extensions' controls (<see cref="ScreenControls.Arrange"/>); a failing source is reported as a <see cref="Diagnostic"/> and dropped.
    /// </summary>
    internal void ArrangeScreenControls(ScreenView view, List<UiControl> controls, UiMode mode, int width, int height)
    {
        var frame = new UiFrameContext(this, mode, width, height, new UiBindings());
        _frame ??= frame;
        if (ScreenOverrides.Any) ScreenOverrides.Apply(view, controls, frame, Diagnostic);
        Screens.Arrange(this, view, controls, frame, Diagnostic);
    }

    /// <summary>
    /// A press on a screen's extension control: runs its action (<see cref="Dispatch(UiControl)"/>); false for any other control.
    /// </summary>
    public bool DispatchScreen(string screen, string widget) => Screens.Shown(screen, widget) is { } control && Dispatch(control);

    /// <summary>
    /// Whether any extension adds dialogue replies (<see cref="DialogueReplies"/>).
    /// </summary>
    public bool HasDialogueReplies => Replies.Any;

    /// <summary>
    /// The replies the extensions add to a conversation's choices (<see cref="Ui.DialogueReplies.For"/>): at most
    /// <see cref="Ui.DialogueReplies.MaxReplies"/>, texts cut, a failing source reported as a <see cref="Diagnostic"/> once and dropped, a press
    /// that throws reported each time.
    /// </summary>
    public IReadOnlyList<UiDialogueReply> DialogueReplies(UiDialogueContext context) => Replies.For(context, GuardedPress, Diagnostic);

    /// <summary>
    /// Whether any extension adds town panel lines (<see cref="TownLines"/>).
    /// </summary>
    public bool HasTownLines => _townLines.Count > 0;

    /// <summary>
    /// The lines the extensions add to the town panel, in registration order, at most <see cref="MaxTownLines"/> of all sources together (a copy of
    /// the sources is walked, so a source may register or dispose). A label or value is cut to <see cref="MaxTownLineText"/> characters and put on
    /// one line, a blank value is none, and a tooltip is cut to <see cref="MaxTownLineTooltip"/>. A source that throws, or names a line without a
    /// label, is reported once as a <see cref="Diagnostic"/> and dropped (it is asked whenever the panel is laid out); one whose lines pass the limit
    /// keeps the lines that fit, the rest left out and reported once. A press that throws is reported each time.
    /// </summary>
    public IReadOnlyList<UiTownLine> TownLines(UiTownContext context)
    {
        var lines = new List<UiTownLine>();
        foreach (var entry in _townLines.ToArray())
        {
            try
            {
                var added = entry.Lines(context)
                    .Take(MaxTownLines + 1)
                    .ToList();
                if (added.Any(l => l is null || string.IsNullOrWhiteSpace(l.Label)))
                    throw new InvalidOperationException("A town panel line needs a label.");

                int room = MaxTownLines - lines.Count;
                if (added.Count > room) ReportTownLinesCut(entry.Owner);
                lines.AddRange(added
                    .Take(room)
                    .Select(l => ShownTownLine(entry.Owner, l)));
            }
            catch (Exception e)
            {
                _townLines.Remove(entry);
                Diagnostic?.Invoke(new UiDiagnostic(entry.Owner, "town panel lines (dropped)", e));
            }
        }

        return lines;
    }

    /// <summary>
    /// <paramref name="line"/> as the panel shows it: its label and value cut and on one line each (<see cref="OneLine"/>), a blank value none, its
    /// tooltip cut to <see cref="MaxTownLineTooltip"/> characters, its press guarded under the shown label.
    /// </summary>
    UiTownLine ShownTownLine(string owner, UiTownLine line)
    {
        string label = OneLine(line.Label);
        string? value = string.IsNullOrWhiteSpace(line.Value) ? null : OneLine(line.Value);
        string? tooltip = line.Tooltip is null ? null : Cut(line.Tooltip, MaxTownLineTooltip);
        var press = line.Press is null ? null : GuardedPress(owner, "town panel line " + label, line.Press);
        return new UiTownLine(label, value, tooltip, press) { Style = UiLineStyle.Shown(line.Style) };
    }

    /// <summary>
    /// <paramref name="text"/> cut to <see cref="MaxTownLineText"/> characters first (so a long text costs no more than a short one), then with its
    /// line breaks, a backslash followed by <c>n</c> (which the painter draws as a break) and other control characters as spaces.
    /// </summary>
    internal static string OneLine(string text)
    {
        string line = Cut(text, MaxTownLineText)
            .ReplaceLineEndings(" ")
            .Replace(@"\n", " ", StringComparison.Ordinal);
        var chars = line.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i])) chars[i] = ' ';
        }

        return new string(chars);
    }

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="max"/> characters, never between the two halves of a character outside the basic plane
    /// (an emoji).
    /// </summary>
    internal static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;

        int length = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..length];
    }

    /// <summary>
    /// Reports once per extension that some of its town panel lines were left out past <see cref="MaxTownLines"/>.
    /// </summary>
    void ReportTownLinesCut(string owner)
    {
        if (!_townLinesCut.Add(owner)) return;

        var error = new InvalidOperationException($"The town panel holds {MaxTownLines} lines of all extensions together; the rest are left out.");
        Diagnostic?.Invoke(new UiDiagnostic(owner, "town panel lines (past the limit, left out)", error));
    }

    /// <summary>
    /// An extension's press that reports a throw as a <see cref="Diagnostic"/> (<paramref name="what"/> names it) instead of passing it on to the
    /// input, and does nothing once its extension is removed.
    /// </summary>
    Action GuardedPress(string owner, string what, Action press) => () =>
    {
        if (!_extensions.Contains(owner)) return;

        try
        {
            press();
        }
        catch (Exception e)
        {
            Diagnostic?.Invoke(new UiDiagnostic(owner, what, e));
        }
    };

    #endregion

    #region Layout

    /// <summary>
    /// A text box's skin when its style names none (the faithful screens' one-line boxes).
    /// </summary>
    const string TextBoxSkin = "Kenshi_EditBox";

    /// <summary>
    /// Arrange contributions into named parent rectangles in stable order. A failed contribution is omitted.
    /// </summary>
    public IReadOnlyList<UiControl> Arrange(UiMode mode, int width, int height, UiBindings bindings, IReadOnlyDictionary<string, UiRect> slots)
    {
        _frame = new(this, mode, width, height, bindings);
        _last = new UiArrangement();
        var result = new List<UiControl>();
        if (width <= 0 || height <= 0) return result;

        ArrangeContributions(mode, slots, _frame, _last, result, hudHidden: false);
        return result;
    }

    /// <summary>
    /// The overlay drawn while the interface is hidden (F7): the overlay contributions whose root asks for it (<see cref="UiNode.ShowWithHudHidden"/>),
    /// laid out over the whole screen with no bindings. They take no input: the last <see cref="Arrange"/> keeps its controls for that.
    /// </summary>
    public IReadOnlyList<UiControl> ArrangeHudHidden(UiMode mode, int width, int height)
    {
        var result = new List<UiControl>();
        if (width <= 0 || height <= 0 || !_contributions.Any(c => c.Slot == UiSlots.Overlay)) return result;

        var frame = new UiFrameContext(this, mode, width, height, new UiBindings());
        var slots = new Dictionary<string, UiRect> { [UiSlots.Overlay] = new(0, 0, width, height) };
        ArrangeContributions(mode, slots, frame, new UiArrangement(), result, hudHidden: true);
        return result;
    }

    /// <summary>
    /// Lays out each contribution of <paramref name="mode"/> whose slot is in <paramref name="slots"/> (with <paramref name="hudHidden"/>, only
    /// those asking to be shown with the interface hidden) into <paramref name="result"/>, recorded in <paramref name="into"/>; one that fails is
    /// left out whole and reported.
    /// </summary>
    void ArrangeContributions(UiMode mode, IReadOnlyDictionary<string, UiRect> slots, UiFrameContext frame, UiArrangement into,
        List<UiControl> result, bool hudHidden)
    {
        foreach (var contribution in _contributions.OrderBy(c => c.Order).ToArray())
        {
            if (contribution.Mode is { } only && only != mode || !slots.TryGetValue(contribution.Slot, out var parent)) continue;

            int kept = into.Nodes.Count;
            try
            {
                var root = contribution.Factory(frame);
                if (hudHidden && root?.ShowWithHudHidden != true) continue;

                into.Owner = contribution.Owner;
                var local = new List<UiControl>();
                ArrangeNode(root!, parent, new NodeScope(parent, true, 1, -1), into, local);
                result.AddRange(contribution.Slot == UiSlots.Overlay ? OnLayer(local, root!.Layer) : local);
            }
            catch (Exception e)
            {
                into.Truncate(kept);
                Diagnostic?.Invoke(new(contribution.Owner, $"build {contribution.Slot}", e));
            }
        }
    }

    /// <summary>
    /// <paramref name="controls"/> marked for <paramref name="layer"/>; as they are on the HUD's layer.
    /// </summary>
    static IEnumerable<UiControl> OnLayer(List<UiControl> controls, UiOverlayLayer layer)
    {
        if (layer == UiOverlayLayer.Hud) return controls;

        string name = layer.ToString();
        return controls.Select(c =>
            c with { Properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal) { [LayerProperty] = name } });
    }

    /// <summary>
    /// The layer an arranged overlay control is drawn on (<see cref="LayerProperty"/>).
    /// </summary>
    public static UiOverlayLayer LayerOf(UiControl c) =>
        c.Properties.TryGetValue(LayerProperty, out string? name) && Enum.TryParse(name, out UiOverlayLayer layer) ? layer : UiOverlayLayer.Hud;

    /// <summary>
    /// What a node is laid out within: the clip its parent leaves, whether its parent takes clicks, the opacity it inherits and its parent's index
    /// in the arrangement (-1: a root).
    /// </summary>
    readonly record struct NodeScope(UiRect Clip, bool Enabled, float Opacity, int Parent);

    /// Lays out <paramref name="node"/> in <paramref name="parent"/>, clipped to <paramref name="clip"/>, into <paramref name="result"/> (a screen
    /// control's tree, by the overlay's rules).
    /// </summary>
    internal void ArrangeInto(UiNode node, UiRect parent, UiRect clip, List<UiControl> result, List<ScreenNode>? nodes = null, string owner = "")
    {
        var into = new UiArrangement { Owner = owner };
        ArrangeNode(node, parent, new NodeScope(clip, true, 1, -1), into, result);
        if (nodes is null) return;

        foreach (var e in into.Nodes)
            nodes.Add(new(owner, e.Node.Id, e.Control.Action, e.Control.Argument, e.Part == ScrollPart.None ? e.Node.Pointer : 0, e.Control.Enabled,
                e.Parent));
    }

    /// <summary>
    /// Lays out <paramref name="node"/> in <paramref name="parent"/> (or at <paramref name="placed"/>), clipped, and its children by its flow;
    /// nothing for a hidden or clipped-out node.
    /// </summary>
    void ArrangeNode(UiNode node, UiRect parent, NodeScope scope, UiArrangement into, List<UiControl> result, UiRect? placed = null)
    {
        if (!node.Visible) return;

        var b = placed ?? node.Placement.Resolve(parent);
        if (!float.IsFinite(b.X) || !float.IsFinite(b.Y) || !float.IsFinite(b.Width) || !float.IsFinite(b.Height))
            throw new InvalidOperationException($"Non-finite UI layout: {node.Id}");

        var clip = scope.Clip;
        float x = Math.Max(b.X, clip.X), y = Math.Max(b.Y, clip.Y);
        var visible = new UiRect(x, y, Math.Max(0, Math.Min(b.X + b.Width, clip.X + clip.Width) - x),
            Math.Max(0, Math.Min(b.Y + b.Height, clip.Y + clip.Height) - y));
        if (visible.Width <= 0 || visible.Height <= 0) return;

        ValidateLooks(node);
        bool enabled = scope.Enabled && node.Enabled;
        float opacity = scope.Opacity * Math.Clamp(node.Opacity, 0, 1);
        var control = Control(node, b, visible, enabled, opacity, into.Owner);
        result.Add(control);
        int index = into.Add(node, control, scope.Parent);

        var content = new UiRect(
            b.X + node.Padding, b.Y + node.Padding, Math.Max(0, b.Width - node.Padding * 2), Math.Max(0, b.Height - node.Padding * 2));
        var inner = new NodeScope(visible, enabled, opacity, index);
        if (node.Kind == UiNodeKind.ScrollView) ArrangeScrollView(node, content, inner, into, result);
        else ArrangeChildren(node, content, inner, into, result);
    }

    /// <summary>
    /// Lays out <paramref name="node"/>'s children in <paramref name="content"/> by its flow.
    /// </summary>
    void ArrangeChildren(UiNode node, UiRect content, NodeScope scope, UiArrangement into, List<UiControl> result)
    {
        foreach (var (child, rect) in ChildRects(node, content)) ArrangeNode(child, content, scope, into, result, rect);
    }

    /// <summary>
    /// <paramref name="node"/>'s shown children, each with its rectangle in <paramref name="content"/> when the node's flow places it (null: its
    /// own placement).
    /// </summary>
    static IEnumerable<(UiNode Child, UiRect? Rect)> ChildRects(UiNode node, UiRect content)
    {
        float cursor = 0;
        foreach (var child in node.Children)
        {
            if (!child.Visible) continue;

            UiRect? childRect = null;
            if (node.Flow != UiFlow.Absolute)
            {
                var size = child.Placement.Resolve(content);
                childRect = new(
                    content.X + (node.Flow == UiFlow.Row ? cursor : 0),
                    content.Y + (node.Flow == UiFlow.Column ? cursor : 0),
                    size.Width,
                    size.Height);
                cursor += (node.Flow == UiFlow.Row ? size.Width : size.Height) + node.Gap;
            }

            yield return (child, childRect);
        }
    }

    /// <summary>
    /// <paramref name="node"/>'s control: its <paramref name="visible"/> part, or for an image the whole of <paramref name="bounds"/> clipped to
    /// it (a picture is cut off, not squeezed); the node's own looks (<see cref="AddLooks"/>) over the style's; its text (not a text box's) in the
    /// translation of <paramref name="owner"/>'s mod.
    /// </summary>
    UiControl Control(UiNode node, UiRect bounds, UiRect visible, bool enabled, float opacity, string owner)
    {
        var style = Style(node.Style);
        bool image = node.Kind == UiNodeKind.Image;
        var properties = new Dictionary<string, string>
        {
            ["TextAlign"] = style.TextAlign,
            ["FontName"] = style.FontName,
            ["TextPadding"] = style.TextPadding.ToString(),
        };
        if (image && node.PortraitOf is { } live) properties[RecordPortraits.LiveProperty] = live.ToString();
        else if (image && node.Portrait.Length > 0) properties[RecordPortraits.Property] = node.Portrait;
        else if (image && node.Image.Length > 0) properties["ImageTexture"] = Rendering.Ui.UiPainter.IconTexture(node.Image);

        bool textBox = node.Kind == UiNodeKind.TextBox;
        if (textBox) AddTextBoxProperties(node, properties);
        AddLooks(node, opacity, properties);

        string skin = textBox && style.Skin.Length == 0 ? TextBoxSkin : style.Skin;
        string text = textBox ? node.Text : ModText(owner, node.Text);
        return new(node.Id, ControlType(node.Kind), image ? bounds : visible, text, node.Action, skin, enabled, properties,
            style.Background, node.TextColour ?? style.Text, style.Hover, style.Active, style.Disabled, node.Pickable, node.Argument,
            node.TextScale ?? style.TextScale, image ? visible : null);
    }

    /// <summary>
    /// A text box's edit properties: the mark that routes it through the screens' edit focus (<see cref="MyGuiScreenHost.OverlayEditProperty"/>)
    /// and its <c>MaxTextLength</c>.
    /// </summary>
    static void AddTextBoxProperties(UiNode node, Dictionary<string, string> properties)
    {
        properties[MyGuiScreenHost.OverlayEditProperty] = "true";
        if (node.Focus) properties[MyGuiScreenHost.FocusRequestProperty] = "true";
        if (node.MaxLength > 0) properties["MaxTextLength"] = node.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The MyGUI widget type a node kind is drawn as.
    /// </summary>
    static string ControlType(UiNodeKind kind) => kind switch
    {
        UiNodeKind.Button => "Button",
        UiNodeKind.Label => "TextBox",
        UiNodeKind.Image => "ImageBox",
        UiNodeKind.TextBox => "EditBox",
        _ => "Widget",
    };

    #endregion

    #region Dispatch

    /// <summary>
    /// Returns false for an unregistered action; disabled controls consume clicks without invoking handlers.
    /// </summary>
    public bool Dispatch(UiControl control) => Dispatch(control, "", UiEventKind.Press);

    /// <summary>
    /// A text event of the screens' edit focus: a text box's text changed, so its action runs with the text (disabled or actionless: nothing). False
    /// for an event of a screen, not of a text box.
    /// </summary>
    public bool DispatchText(MyGuiEvent e)
    {
        if (e.Screen != MyGuiScreenHost.OverlayScreen) return false;

        var kind = e.Action switch
        {
            "text" => UiEventKind.Text,
            MyGuiScreenHost.SubmitAction => UiEventKind.Submit,
            _ => (UiEventKind?)null,
        };
        if (kind is null) return true;

        if ((_last.TextBoxes.GetValueOrDefault(e.Widget) ?? Screens.TextBox(e.Widget)) is { } box) Dispatch(box, e.Text, kind.Value);
        return true;
    }

    /// <summary>
    /// Runs <paramref name="control"/>'s action with <paramref name="text"/> (<see cref="UiActionContext.Text"/>) as <paramref name="kind"/>.
    /// </summary>
    bool Dispatch(UiControl control, string text, UiEventKind kind) =>
        Run(control.Enabled, control.Action, frame => new(frame, control.Argument) { Text = text, Kind = kind });

    /// <summary>
    /// Runs action <paramref name="id"/> with the context <paramref name="context"/> makes from the last frame; a throw is reported as a
    /// <see cref="Diagnostic"/>. Not <paramref name="enabled"/>: consumed without running; false for an unregistered action or before a frame.
    /// </summary>
    bool Run(bool enabled, string id, Func<UiFrameContext, UiActionContext> context)
    {
        if (!enabled) return true;
        if (_frame is null || !_actions.TryGetValue(id, out var action)) return false;

        try
        {
            action.Handler(context(_frame));
        }
        catch (Exception e)
        {
            Diagnostic?.Invoke(new(action.Owner, $"action {id}", e));
        }

        return true;
    }

    #endregion
}

/// <summary>
/// Owned registration scope. Local IDs are prefixed with the extension ID to prevent collisions.
/// </summary>
public sealed class UiRegistration : IDisposable
{
    /// <summary>
    /// The host the scope registers into.
    /// </summary>
    readonly UiHost _host;

    /// <summary>
    /// The scope was disposed.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// The folder of the code mod the extension belongs to; null for none.
    /// </summary>
    readonly string? _folder;

    /// <summary>
    /// A scope for extension <paramref name="id"/> of the code mod in <paramref name="folder"/> (null: none).
    /// </summary>
    internal UiRegistration(UiHost host, string id, string? folder = null)
    {
        this._host = host;
        Id = id;
        _folder = folder;
    }

    /// <summary>
    /// The extension's id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// <paramref name="localName"/> (validated) prefixed with the extension's id.
    /// </summary>
    public string Key(string localName)
    {
        UiHost.ValidateName(localName);
        return $"{Id}:{localName}";
    }

    /// <summary>
    /// Throws once the scope is disposed.
    /// </summary>
    void Check() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Registers a style; returns its key.
    /// </summary>
    public string RegisterStyle(string name, UiStyle style)
    {
        Check();
        ArgumentNullException.ThrowIfNull(style);

        string key = Key(name);
        _host.AddStyle(Id, key, style);
        return key;
    }

    /// <summary>
    /// Adds font file <paramref name="path"/> (a TrueType or OpenType font with outlines; a relative path is in the mod's folder) to the fonts
    /// every interface text draws a character from when its own font lacks it, after those added before (docs/modding.md, "Fallback fonts").
    /// False when the file does not exist or was added already. For the life of the process: disposing the scope does not remove it.
    /// </summary>
    public bool AddFallbackFont(string path)
    {
        Check();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!Path.IsPathRooted(path))
        {
            if (_folder is null) return false;
            path = Path.Combine(_folder, path);
        }

        return File.Exists(path) && Rendering.Ui.UiFontFallbacks.Add(path);
    }

    /// <summary>
    /// Registers a control factory; returns its key.
    /// </summary>
    public string RegisterControl(string name, Func<UiFrameContext, UiNode> factory)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);

        string key = Key(name);
        _host.AddControl(Id, key, factory);
        return key;
    }

    /// <summary>
    /// Registers an action handler; returns its key.
    /// </summary>
    public string RegisterAction(string name, Action<UiActionContext> action)
    {
        Check();
        ArgumentNullException.ThrowIfNull(action);

        string key = Key(name);
        _host.AddAction(Id, key, action);
        return key;
    }

    /// <summary>
    /// Overrides a registered style.
    /// </summary>
    public void OverrideStyle(string target, UiStyle style)
    {
        Check();
        ArgumentNullException.ThrowIfNull(style);
        _host.Override(Id, target, style);
    }

    /// <summary>
    /// Overrides a registered control.
    /// </summary>
    public void OverrideControl(string target, Func<UiFrameContext, UiNode> factory)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);
        _host.OverrideControl(Id, target, factory);
    }

    /// <summary>
    /// Adds tick buttons to the squad overview's AI tab, after the game's own (docs/modding.md "Registration surface"): <paramref name="options"/>
    /// runs on the host's thread every frame the tab is open, so it must be cheap, and while a tick may be in flight, so it must not touch the world;
    /// a pressed option changes the world through commands (<see cref="UiSquadAiContext.Enqueue"/>).
    /// </summary>
    public void AddSquadAiOptions(Func<UiSquadAiContext, IEnumerable<UiSquadAiOption>> options)
    {
        Check();
        ArgumentNullException.ThrowIfNull(options);
        _host.AddSquadAiOptions(Id, options);
    }

    /// <summary>
    /// Adds buttons to the panel of a selected building or placed piece, after the game's own lines (docs/modding.md "Building panel buttons"):
    /// <paramref name="actions"/> runs on the host's thread whenever the panel is arranged, which can be every frame, so it must be cheap, and while
    /// a tick may be in flight, so it must not touch the world; it reads the context, and a pressed button changes the world through commands
    /// (<see cref="UiBuildingContext.Enqueue"/>), which must check that the building still exists when they apply.
    /// </summary>
    public void AddBuildingActions(Func<UiBuildingContext, IEnumerable<UiBuildingAction>> actions)
    {
        Check();
        ArgumentNullException.ThrowIfNull(actions);
        _host.AddBuildingActions(Id, actions);
    }

    /// <summary>
    /// Adds lines to the HUD's town panel, under the game's own (docs/modding.md "Town panel lines"): <paramref name="lines"/> runs on the host's
    /// thread whenever the panel is arranged, which can be every frame, so it must be cheap, and while a tick may be in flight, so it must not touch
    /// the world; it reads the context, and a pressed line, never the source itself, changes the world through commands
    /// (<see cref="UiTownContext.Enqueue"/>), which must check that the outpost still exists when they apply.
    /// </summary>
    public void AddTownPanelLines(Func<UiTownContext, IEnumerable<UiTownLine>> lines)
    {
        Check();
        ArgumentNullException.ThrowIfNull(lines);
        _host.AddTownLines(Id, lines);
    }

    /// <summary>
    /// Adds lines to item tooltips, after the game's own rows and before the description (docs/modding.md "Item tooltip lines"):
    /// <paramref name="lines"/> runs on the host's thread whenever a tooltip is built, so it must be cheap, and while a tick may be in flight, so it
    /// reads only its context (the item's record, name, code-mod tag, quantity, level and quality).
    /// </summary>
    public void AddItemTooltipLines(Func<UiItemTooltipContext, IEnumerable<UiItemTooltipLine>> lines)
    {
        Check();
        ArgumentNullException.ThrowIfNull(lines);
        _host.ItemTips.Add(Id, lines);
    }

    /// <summary>
    /// Changes the text the game shows (docs/modding.md "Shown text"): the interface's and the notices' messages, items' names and dialogue lines.
    /// <paramref name="source"/> gets the kind of text, its key (a message's English source text, an item's record, a dialogue line's record), the
    /// text so far (the game's, or an earlier extension's) and a tag, and returns the text to show (null or blank: keep it). Display only: records,
    /// saves and the game's decisions keep the game's text. It runs the first time a text is shown, the answer kept until
    /// <see cref="RefreshText"/>; it may be asked on any thread (the simulation's messages and dialogue), one call at a time, so it must read only
    /// its context and the mod's own state, safely across threads.
    /// </summary>
    public void AddText(Func<ShownTextContext, string?> source)
    {
        Check();
        ArgumentNullException.ThrowIfNull(source);
        ShownText.Add(_host, Id, source);
    }

    /// <summary>
    /// Asks every text source again (<see cref="AddText"/>): call it when the mod's answers change, e.g. a word it teaches was learned. Text
    /// already handed out (a dialogue line said, a notice posted) keeps what it was given.
    /// </summary>
    public void RefreshText()
    {
        Check();
        ShownText.Refresh();
    }

    /// <summary>
    /// Adds bars under the Condition bar of a selected building, placed piece or piece of furniture (docs/modding.md "Building panel bars"):
    /// <paramref name="rows"/> runs on the host's thread whenever the panel's bars are bound, which can be every frame, so it must be cheap, and
    /// while a tick may be in flight, so it must not touch the world; it reads the context. Bars show state only; buttons go through
    /// <see cref="AddBuildingActions"/>.
    /// </summary>
    public void AddBuildingRows(Func<UiBuildingContext, IEnumerable<UiBuildingRow>> rows)
    {
        Check();
        ArgumentNullException.ThrowIfNull(rows);
        _host.BuildingBars.Add(Id, rows);
    }

    /// <summary>
    /// Adds lines to the HUD's character panel, under its stats lines (docs/modding.md "Character panel lines and job panel buttons"):
    /// <paramref name="lines"/> runs on the host's thread whenever the panel is arranged, which can be every frame, so it must be cheap, and while a
    /// tick may be in flight, so it must not touch the world; it reads the context, and a pressed line changes the world through commands
    /// (<see cref="UiCharacterContext.Enqueue"/>).
    /// </summary>
    public void AddCharacterPanelLines(Func<UiCharacterContext, IEnumerable<UiCharacterLine>> lines)
    {
        Check();
        ArgumentNullException.ThrowIfNull(lines);
        _host.CharacterPanel.AddLines(Id, lines);
    }

    /// <summary>
    /// Adds buttons beside the HUD's jobs list (docs/modding.md "Character panel lines and job panel buttons"): <paramref name="buttons"/> runs on
    /// the host's thread whenever the HUD is bound, which can be every frame, so it must be cheap and must not touch the world; a press runs for
    /// the character the buttons were shown for and changes the world through commands (<see cref="UiCharacterContext.Enqueue"/>).
    /// </summary>
    public void AddJobPanelButtons(Func<UiCharacterContext, IEnumerable<UiJobButton>> buttons)
    {
        Check();
        ArgumentNullException.ThrowIfNull(buttons);
        _host.CharacterPanel.AddButtons(Id, buttons);
    }

    /// <summary>
    /// Adds buttons beside the HUD's jobs list as <see cref="AddJobPanelButtons(Func{UiCharacterContext, IEnumerable{UiJobButton}})"/> does, placed
    /// by <paramref name="layout"/> (rows above the list or columns beside it): the first registered source with a layout sets it for all.
    /// </summary>
    public void AddJobPanelButtons(Func<UiCharacterContext, IEnumerable<UiJobButton>> buttons, UiJobButtonLayout layout)
    {
        Check();
        ArgumentNullException.ThrowIfNull(buttons);
        ArgumentNullException.ThrowIfNull(layout);
        _host.CharacterPanel.AddButtons(Id, buttons, layout);
    }

    /// <summary>
    /// Adds controls to a faithful screen or the HUD (docs/modding.md "Screen controls"): <paramref name="build"/> makes a node tree (null: nothing)
    /// laid out against the widget <paramref name="anchorWidget"/> of <paramref name="screen"/> (a screen id or kind, or <see cref="UiScreens.Hud"/>;
    /// <see cref="UiScreens.ItemCells"/> builds once per item cell) by <paramref name="placement"/>, drawn over the screen; a node's
    /// <see cref="UiNode.Action"/> names an action registered with <see cref="RegisterAction"/>. Nothing shows while the screen is closed or its
    /// layout lacks the widget (a UI mod's). It runs on the host's thread each time the screen is arranged, which can be every frame, so it must be
    /// cheap, and while a tick may be in flight, so it must not touch the world; an action changes the world through commands
    /// (<see cref="UiScreenContext.Enqueue"/>).
    /// </summary>
    public void AddScreenControls(string screen, string anchorWidget, UiAnchorPlacement placement, Func<UiScreenContext, UiNode?> build)
    {
        Check();
        ArgumentNullException.ThrowIfNull(build);
        _host.Screens.Add(Id, screen, anchorWidget, placement, build);
    }

    /// <summary>
    /// The offset of the extension's overlay scroll view <paramref name="node"/> (its node id), pixels from the top of its content, as last laid
    /// out or set (docs/modding.md "Interface"); 0 for one never scrolled. Host thread.
    /// </summary>
    public float ScrollOffset(string node)
    {
        Check();
        ArgumentNullException.ThrowIfNull(node);
        return _host.ScrollOffset(Id, node);
    }

    /// <summary>
    /// Scrolls the extension's overlay scroll view <paramref name="node"/> to <paramref name="offset"/> pixels from the top of its content (finite;
    /// below 0 is 0); the next layout clamps it to the content, as the wheel's. Host thread.
    /// </summary>
    public void ScrollTo(string node, float offset)
    {
        Check();
        ArgumentNullException.ThrowIfNull(node);
        if (!float.IsFinite(offset)) throw new ArgumentOutOfRangeException(nameof(offset), "A scroll offset is finite.");
        _host.ScrollTo(Id, node, offset);
    }

    /// <summary>
    /// Changes the own widgets of a faithful screen or the HUD (docs/modding.md "Screen overrides"): <paramref name="over"/> is asked for each
    /// arranged widget of <paramref name="screen"/> (a screen id or kind, or <see cref="UiScreens.Hud"/>) whose name matches
    /// <paramref name="widgetPattern"/> (a name, or <c>*</c> and <c>?</c> wildcards), with the widget and the screen's state
    /// (<see cref="UiScreenContext.Widget"/>, <see cref="UiScreenContext.State"/>), and answers its caption, text colour or visibility (null: keep it).
    /// It runs on the host's thread each time the screen is arranged, so it must be cheap and must not touch the world.
    /// </summary>
    public void AddScreenOverrides(string screen, string widgetPattern, Func<UiScreenContext, UiWidgetOverride?> over)
    {
        Check();
        ArgumentNullException.ThrowIfNull(over);
        _host.ScreenOverrides.Add(Id, screen, widgetPattern, over);
    }

    /// <summary>
    /// Adds rows to the right-click menu of any target (docs/modding.md "Right-click menu rows"): <paramref name="verbs"/> gets the target
    /// (<see cref="UiMenuTarget"/>: a character, ground item, container, door, building or the ground) whenever the game builds a menu for one with a
    /// selection, which can be each frame the target is under the cursor, so it must be cheap and must not touch the world; a chosen row queues
    /// commands (<see cref="UiMenuTargetContext.Enqueue"/>).
    /// </summary>
    public void AddMenuVerbs(Func<UiMenuTargetContext, IEnumerable<UiMenuVerb>> verbs)
    {
        Check();
        ArgumentNullException.ThrowIfNull(verbs);
        _host.TargetVerbs.Add(Id, verbs);
    }

    /// <summary>
    /// Changes the rows of right-click menus, the game's own and the mods' (docs/modding.md "Right-click menu rows"): <paramref name="over"/> is
    /// asked for each row of a menu as it is built (<see cref="UiMenuRowContext"/>: the title, the row's own label, the target), which can be
    /// each frame a target is under the cursor, so it must be cheap and must not touch the world; it answers the row's caption, colour or
    /// visibility (null: keep).
    /// </summary>
    public void AddMenuRowOverrides(Func<UiMenuRowContext, UiMenuRowOverride?> over)
    {
        Check();
        ArgumentNullException.ThrowIfNull(over);
        _host.MenuRows.Add(Id, over);
    }

    /// <summary>
    /// Handles input on the world map (docs/modding.md "World map input"): <paramref name="input"/> gets presses over the map, keys while it shows
    /// and its closing, with the map as drawn (<see cref="UiWorldMapView"/>), on the host's thread; true takes the press or key from the game (no
    /// pan, no move order). It must not touch the world; it queues commands.
    /// </summary>
    public void AddMapInput(Func<UiMapInput, bool> input)
    {
        Check();
        ArgumentNullException.ThrowIfNull(input);
        _host.Map.Add(Id, input);
    }

    /// <summary>
    /// Handles interface keys (docs/modding.md "Interface keys"): <paramref name="input"/> gets each key going down to the game's interface, Esc
    /// included, before the interface and the game's bindings use it, on the host's thread; true takes it from them (no window closes, no hotkey,
    /// no binding). It must not touch the world; it queues commands.
    /// </summary>
    public void AddKeyInput(Func<UiKeyInput, bool> input)
    {
        Check();
        ArgumentNullException.ThrowIfNull(input);
        _host.KeyInput.Add(Id, input);
    }

    /// <summary>
    /// Adds player replies to conversations, after the game's own choices (docs/modding.md "Dialogue replies"): <paramref name="replies"/> gets the
    /// conversation (the NPC's and the player's snapshots: record, faction) whenever its line or choices change and answers the replies to offer
    /// (none: the conversation is as the game's); a chosen reply runs on the host's thread and changes the world only by queueing commands.
    /// </summary>
    public void AddDialogueReplies(Func<UiDialogueContext, IEnumerable<UiDialogueReply>> replies)
    {
        Check();
        ArgumentNullException.ThrowIfNull(replies);
        _host.Replies.Add(Id, replies);
    }

    /// <summary>
    /// Contributes a node to <paramref name="slot"/> (in <paramref name="order"/>; only in <paramref name="mode"/> when given).
    /// </summary>
    public void Contribute(string slot, Func<UiFrameContext, UiNode> factory, int order = 0, UiMode? mode = null)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);
        _host.Contribute(Id, slot, mode, order, factory);
    }

    /// <summary>
    /// Adds markers to the world map (docs/modding.md "Map markers"): <paramref name="markers"/> is asked on the host's thread each frame the map is
    /// shown, after the places and before the player's characters, so it must not touch the world; it reads the snapshot it is given.
    /// </summary>
    public void AddMapMarkers(Func<UiMapContext, IEnumerable<UiMapMarker>> markers)
    {
        Check();
        ArgumentNullException.ThrowIfNull(markers);
        _host.AddMapMarkers(Id, markers);
    }

    /// <summary>
    /// Adds rows to a character's right-click menu, after the game's own (docs/modding.md "Registration surface"): <paramref name="verbs"/> runs
    /// on the host's thread whenever the game asks for the menu, which includes every frame a character is under the cursor, so it must be cheap,
    /// and while a tick may be in flight, so it must not touch the world; it reads the context and a chosen row queues commands
    /// (<see cref="UiMenuContext.Enqueue"/>). Shift is the row's own to read: the game's order-to-job switch does not apply.
    /// </summary>
    public void AddCharacterMenuVerbs(Func<UiMenuContext, IEnumerable<UiMenuVerb>> verbs)
    {
        Check();
        ArgumentNullException.ThrowIfNull(verbs);
        _host.AddMenuVerbs(Id, verbs);
    }

    /// <summary>
    /// Removes everything the extension registered (once).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _host.Remove(Id);
    }
}