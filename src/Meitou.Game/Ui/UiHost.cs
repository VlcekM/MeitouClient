using Meitou.Data.Ui;

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
/// What an action handler gets: the frame's data and the control's argument.
/// </summary>
public sealed record UiActionContext(UiFrameContext Frame, string Argument);

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
public sealed record UiMenuVerb(string Caption, Action Choose);

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
}

/// <summary>
/// Platform-independent native UI extension host. Access on the UI thread only; no DLL discovery/loading.
/// </summary>
public sealed class UiHost
{
    #region State

    /// <summary>
    /// The extension API version the host provides; an extension must ask for it.
    /// </summary>
    public const int ApiVersion = 1;

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
    /// AI option source or press, or a building panel button source or press).
    /// </summary>
    public event Action<UiDiagnostic>? Diagnostic;

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
    public IDisposable Register(IUiExtension extension, string? folder)
    {
        ArgumentNullException.ThrowIfNull(extension);

        string id = extension.Id;
        ValidateName(id);
        if (extension.ApiVersion != ApiVersion)
            throw new NotSupportedException($"UI extension {id} requires API {extension.ApiVersion}; host provides {ApiVersion}.");
        if (!_extensions.Add(id)) throw new InvalidOperationException($"UI extension already registered: {id}");
        if (folder is not null) _markerImages.SetFolder(id, folder);

        var scope = new UiRegistration(this, id);
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
        foreach (string id in _styles.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _styles.Remove(id);
        foreach (string id in _controls.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _controls.Remove(id);
        foreach (string id in _actions.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _actions.Remove(id);
        _overrides.RemoveAll(o => o.Owner == owner);
        _controlOverrides.RemoveAll(o => o.Owner == owner);
        _contributions.RemoveAll(c => c.Owner == owner);
        _mapMarkers.RemoveAll(m => m.Owner == owner);
        _markerImages.Remove(owner);
        _menuVerbs.RemoveAll(v => v.Owner == owner);
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

                rows.AddRange(added.Select(v => v with { Choose = Guarded(entry.Owner, v) }));
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
                    Tooltip = o.Tooltip ?? "",
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

                actions.AddRange(added.Select(a => a with { Press = GuardedPress(entry.Owner, $"building panel button {a.Caption}", a.Press) }));
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
    /// Arrange contributions into named parent rectangles in stable order. A failed contribution is omitted.
    /// </summary>
    public IReadOnlyList<UiControl> Arrange(UiMode mode, int width, int height, UiBindings bindings, IReadOnlyDictionary<string, UiRect> slots)
    {
        _frame = new(this, mode, width, height, bindings);
        var result = new List<UiControl>();
        if (width <= 0 || height <= 0) return result;

        foreach (var contribution in _contributions.OrderBy(c => c.Order).ToArray())
        {
            if (contribution.Mode is { } only && only != mode || !slots.TryGetValue(contribution.Slot, out var parent)) continue;

            try
            {
                var local = new List<UiControl>();
                ArrangeNode(contribution.Factory(_frame), parent, parent, true, local);
                result.AddRange(local);
            }
            catch (Exception e)
            {
                Diagnostic?.Invoke(new(contribution.Owner, $"build {contribution.Slot}", e));
            }
        }

        return result;
    }

    /// <summary>
    /// Lays out <paramref name="node"/> in <paramref name="parent"/> (or at <paramref name="placed"/>), clipped, and its children by its flow;
    /// nothing for a hidden or clipped-out node.
    /// </summary>
    void ArrangeNode(UiNode node, UiRect parent, UiRect clip, bool parentEnabled, List<UiControl> result, UiRect? placed = null)
    {
        if (!node.Visible) return;

        var b = placed ?? node.Placement.Resolve(parent);
        if (!float.IsFinite(b.X) || !float.IsFinite(b.Y) || !float.IsFinite(b.Width) || !float.IsFinite(b.Height))
            throw new InvalidOperationException($"Non-finite UI layout: {node.Id}");

        float x = Math.Max(b.X, clip.X), y = Math.Max(b.Y, clip.Y);
        var visible = new UiRect(x, y, Math.Max(0, Math.Min(b.X + b.Width, clip.X + clip.Width) - x),
            Math.Max(0, Math.Min(b.Y + b.Height, clip.Y + clip.Height) - y));
        if (visible.Width <= 0 || visible.Height <= 0) return;

        bool enabled = parentEnabled && node.Enabled;
        result.Add(Control(node, b, visible, enabled));

        var content = new UiRect(
            b.X + node.Padding, b.Y + node.Padding, Math.Max(0, b.Width - node.Padding * 2), Math.Max(0, b.Height - node.Padding * 2));
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

            ArrangeNode(child, content, visible, enabled, result, childRect);
        }
    }

    /// <summary>
    /// <paramref name="node"/>'s control: its <paramref name="visible"/> part, or for an image the whole of <paramref name="bounds"/> clipped to
    /// it (a picture is cut off, not squeezed).
    /// </summary>
    UiControl Control(UiNode node, UiRect bounds, UiRect visible, bool enabled)
    {
        var style = Style(node.Style);
        bool image = node.Kind == UiNodeKind.Image;
        var properties = new Dictionary<string, string>
        {
            ["TextAlign"] = style.TextAlign,
            ["FontName"] = style.FontName,
            ["TextPadding"] = style.TextPadding.ToString(),
        };
        if (image && node.Portrait.Length > 0) properties[RecordPortraits.Property] = node.Portrait;
        else if (image && node.Image.Length > 0) properties["ImageTexture"] = Rendering.Ui.UiPainter.IconTexture(node.Image);

        return new(node.Id, ControlType(node.Kind), image ? bounds : visible, node.Text, node.Action, style.Skin, enabled, properties,
            style.Background, style.Text, style.Hover, style.Active, style.Disabled, node.Pickable, node.Argument, style.TextScale,
            image ? visible : null);
    }

    /// <summary>
    /// The MyGUI widget type a node kind is drawn as.
    /// </summary>
    static string ControlType(UiNodeKind kind) => kind switch
    {
        UiNodeKind.Button => "Button",
        UiNodeKind.Label => "TextBox",
        UiNodeKind.Image => "ImageBox",
        _ => "Widget",
    };

    #endregion

    #region Dispatch

    /// <summary>
    /// Returns false for an unregistered action; disabled controls consume clicks without invoking handlers.
    /// </summary>
    public bool Dispatch(UiControl control)
    {
        if (!control.Enabled) return true;
        if (_frame is null || !_actions.TryGetValue(control.Action, out var action)) return false;

        try
        {
            action.Handler(new(_frame, control.Argument));
        }
        catch (Exception e)
        {
            Diagnostic?.Invoke(new(action.Owner, $"action {control.Action}", e));
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
    /// A scope for extension <paramref name="id"/>.
    /// </summary>
    internal UiRegistration(UiHost host, string id)
    {
        this._host = host;
        Id = id;
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
    /// <paramref name="actions"/> runs on the host's thread whenever the panel is laid out, so it must be cheap, and while a tick may be in flight, so
    /// it must not touch the world; it reads the context, and a pressed button changes the world through commands
    /// (<see cref="UiBuildingContext.Enqueue"/>).
    /// </summary>
    public void AddBuildingActions(Func<UiBuildingContext, IEnumerable<UiBuildingAction>> actions)
    {
        Check();
        ArgumentNullException.ThrowIfNull(actions);
        _host.AddBuildingActions(Id, actions);
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