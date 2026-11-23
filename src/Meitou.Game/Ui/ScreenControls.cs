using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// Where an extension's screen controls are laid out against their anchor widget (<see cref="UiRegistration.AddScreenControls"/>): in its
/// rectangle, or in a rectangle of its size just above, below, left or right of it.
/// </summary>
public enum UiAnchorPlacement
{
    /// <summary>
    /// In the anchor's own rectangle, clipped to it.
    /// </summary>
    Inside,

    /// <summary>
    /// In a rectangle of the anchor's size just above it.
    /// </summary>
    Above,

    /// <summary>
    /// In a rectangle of the anchor's size just below it.
    /// </summary>
    Below,

    /// <summary>
    /// In a rectangle of the anchor's size just left of it.
    /// </summary>
    Left,

    /// <summary>
    /// In a rectangle of the anchor's size just right of it.
    /// </summary>
    Right,
}

/// <summary>
/// Screen and anchor names with a meaning of their own for <see cref="UiRegistration.AddScreenControls"/>.
/// </summary>
public static class UiScreens
{
    /// <summary>
    /// The HUD (its layout <c>Kenshi_MainPanel.layout</c>: the time controls are <c>TimeMoneyPanel</c>, the speed buttons
    /// <c>TimeSpeedButton1</c> and on).
    /// </summary>
    public const string Hud = "hud";

    /// <summary>
    /// An anchor that stands for every item cell a character window shows (inventory, bag, limbs, loot): the controls are built once per cell,
    /// with <see cref="UiScreenContext.Item"/> set.
    /// </summary>
    public const string ItemCells = "*item-cells";

    /// <summary>
    /// An anchor of the HUD that stands for every squad bar portrait shown (its picture, <c>portrait-N</c>): the controls are built once per
    /// portrait, with <see cref="UiScreenContext.Character"/> the member.
    /// </summary>
    public const string Portraits = "*portraits";
}

/// <summary>
/// An item a screen shows in a cell: who holds it (a command names it by holder and <see cref="InventoryItemView.Index"/>) and its view.
/// </summary>
public sealed record UiScreenItem(Simulation.Items.ItemOwner Owner, InventoryItemView Item);

/// <summary>
/// What a screen shows, for its extensions' controls: its id and kind, the character and building it is about, how its item cells map to items,
/// the snapshot, the tick and the command queue.
/// </summary>
internal sealed record ScreenView(string Screen, string Kind, CharacterSnapshot? Character, string? Building, Func<UiControl, UiScreenItem?>? ItemOf,
    WorldSnapshot Snapshot, Func<long> Tick, Action<SimCommand> Enqueue)
{
    /// <summary>
    /// The squad member a HUD portrait control shows; null for any other control (none off the HUD).
    /// </summary>
    public Func<UiControl, CharacterSnapshot?>? PortraitOf { get; init; }

    /// <summary>
    /// The world map as drawn, while the overview shows it; null otherwise.
    /// </summary>
    public UiWorldMapView? Map { get; init; }
}

/// <summary>
/// What an extension's screen controls get (<see cref="UiRegistration.AddScreenControls"/>): the screen, the anchor and its arranged rectangle, the
/// character, building and item the screen shows, the snapshot, the tick, the command queue, and the frame (registered controls, styles).
/// </summary>
public sealed class UiScreenContext
{
    /// <summary>
    /// The screen's view.
    /// </summary>
    readonly ScreenView _view;

    /// <summary>
    /// The squad member a portrait build is for; null otherwise.
    /// </summary>
    readonly CharacterSnapshot? _member;

    /// <summary>
    /// The context for one build against <paramref name="anchor"/>.
    /// </summary>
    internal UiScreenContext(ScreenView view, string anchorWidget, UiRect anchor, UiScreenItem? item, UiFrameContext frame,
        CharacterSnapshot? member = null)
    {
        _view = view;
        _member = member;
        AnchorWidget = anchorWidget;
        Anchor = anchor;
        Item = item;
        Frame = frame;
    }

    /// <summary>
    /// The screen's id as the interface registers it (a pinned inventory's own id, e.g. <c>inventory-pinned-1</c>).
    /// </summary>
    public string Screen => _view.Screen;

    /// <summary>
    /// The screen's kind: a pinned inventory or bag is <c>inventory</c> or <c>bag</c>; any other screen its id.
    /// </summary>
    public string Kind => _view.Kind;

    /// <summary>
    /// The anchor widget's name (<see cref="UiScreens.ItemCells"/> for an item cell).
    /// </summary>
    public string AnchorWidget { get; }

    /// <summary>
    /// The anchor's arranged rectangle on screen (the cell's for an item cell).
    /// </summary>
    public UiRect Anchor { get; }

    /// <summary>
    /// The character the screen shows (a character window's, the stats window's, the HUD's selected, the NPC talked to; for a portrait the
    /// member); null for none.
    /// </summary>
    public CharacterSnapshot? Character => _member ?? _view.Character;

    /// <summary>
    /// The world map as drawn, on the overview while its Map tab shows; null otherwise. Anchored to <c>MapImage</c>, <see cref="Anchor"/> is the
    /// whole map image (it pans and zooms) and an inside placement is clipped to the visible viewport.
    /// </summary>
    public UiWorldMapView? Map => _view.Map;

    /// <summary>
    /// The building the screen shows (a machine or storage window's site id); null for none.
    /// </summary>
    public string? Building => _view.Building;

    /// <summary>
    /// The item cell the controls are built for (anchor <see cref="UiScreens.ItemCells"/>); null otherwise.
    /// </summary>
    public UiScreenItem? Item { get; }

    /// <summary>
    /// The snapshot the screen is laid out from (the one on screen).
    /// </summary>
    public WorldSnapshot Snapshot => _view.Snapshot;

    /// <summary>
    /// The world's tick now, for a command's <see cref="SimCommand.Tick"/>.
    /// </summary>
    public long Tick => _view.Tick();

    /// <summary>
    /// Queues a command for the world (from an action only).
    /// </summary>
    public void Enqueue(SimCommand command) => _view.Enqueue(command);

    /// <summary>
    /// The frame: registered controls and styles.
    /// </summary>
    public UiFrameContext Frame { get; }
}

/// <summary>
/// The extensions' controls in the faithful screens and the HUD (docs/modding.md "Screen controls"): each built against a named widget of the
/// screen's arranged controls, laid out by the node rules of the overlay and drawn over the screen; a press runs the node's registered action.
/// </summary>
sealed class ScreenControls
{
    /// <summary>
    /// The most controls the extensions together add to one screen in a frame.
    /// </summary>
    public const int MaxControls = 256;

    /// <summary>
    /// The most item cells one source builds for in a frame.
    /// </summary>
    public const int MaxCells = 128;

    /// <summary>
    /// A source: its owner, screen, anchor, placement and builder.
    /// </summary>
    sealed record Entry(string Owner, string Screen, string Anchor, UiAnchorPlacement Placement, Func<UiScreenContext, UiNode?> Build);

    /// <summary>
    /// The sources, in registration order.
    /// </summary>
    readonly List<Entry> _entries = [];

    /// <summary>
    /// The controls each screen last showed, by name, for its presses.
    /// </summary>
    readonly Dictionary<string, Dictionary<string, UiControl>> _shown = new(StringComparer.Ordinal);

    /// <summary>
    /// The text boxes the screens last showed, by name, for their text events.
    /// </summary>
    readonly Dictionary<string, UiControl> _textBoxes = new(StringComparer.Ordinal);

    /// <summary>
    /// The screens whose controls were cut at <see cref="MaxControls"/>, each reported once.
    /// </summary>
    readonly HashSet<string> _cut = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether any extension adds screen controls.
    /// </summary>
    public bool Any => _entries.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source (screen and anchor names checked: not blank).
    /// </summary>
    public void Add(string owner, string screen, string anchor, UiAnchorPlacement placement, Func<UiScreenContext, UiNode?> build)
    {
        if (string.IsNullOrWhiteSpace(screen) || string.IsNullOrWhiteSpace(anchor))
            throw new ArgumentException("Screen controls need a screen and an anchor widget.");
        if (!Enum.IsDefined(placement)) throw new ArgumentOutOfRangeException(nameof(placement));

        _entries.Add(new(owner, screen, anchor, placement, build));
    }

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources and the controls they showed.
    /// </summary>
    public void Remove(string owner)
    {
        _entries.RemoveAll(e => e.Owner == owner);
        foreach (string name in _textBoxes.Where(p => p.Value.Properties.GetValueOrDefault(OwnerProperty) == owner).Select(p => p.Key).ToArray())
            _textBoxes.Remove(name);
        foreach (var shown in _shown.Values)
        {
            foreach (string name in shown.Where(p => p.Value.Properties.GetValueOrDefault(OwnerProperty) == owner).Select(p => p.Key).ToArray())
                shown.Remove(name);
        }
    }

    /// <summary>
    /// The property naming a control's extension.
    /// </summary>
    internal const string OwnerProperty = "ModOwner";

    /// <summary>
    /// The control <paramref name="name"/> the extensions last showed on <paramref name="screen"/>; null for none.
    /// </summary>
    public UiControl? Shown(string screen, string name) =>
        _shown.TryGetValue(screen, out var shown) ? shown.GetValueOrDefault(name) : null;

    /// <summary>
    /// The text box <paramref name="name"/> a screen last showed; null for none.
    /// </summary>
    public UiControl? TextBox(string name) => _textBoxes.GetValueOrDefault(name);

    /// <summary>
    /// Builds the sources of <paramref name="view"/>'s screen (or its kind) against their anchors in <paramref name="controls"/>, in registration
    /// order, and adds their controls after the screen's own, at most <see cref="MaxControls"/>. A source whose anchor is missing adds nothing; one
    /// that throws or lays out a node not finite is reported once and dropped.
    /// </summary>
    public void Arrange(UiHost host, ScreenView view, List<UiControl> controls, UiFrameContext frame, Action<UiDiagnostic>? report)
    {
        if (_shown.Remove(view.Screen, out var before))
        {
            foreach (string name in before.Keys) _textBoxes.Remove(name);
        }
        var added = new List<UiControl>();
        foreach (var entry in _entries.ToArray())
        {
            if (entry.Screen != view.Screen && entry.Screen != view.Kind) continue;

            var local = new List<UiControl>();
            try
            {
                BuildEntry(host, entry, view, controls, frame, local);
                added.AddRange(local);
            }
            catch (Exception e)
            {
                _entries.Remove(entry);
                report?.Invoke(new UiDiagnostic(entry.Owner, $"screen controls {entry.Screen}/{entry.Anchor} (dropped)", e));
            }
        }

        Commit(view.Screen, controls, added, report);
    }

    /// <summary>
    /// <paramref name="entry"/>'s controls: once against its anchor, or once per item cell for <see cref="UiScreens.ItemCells"/> or per portrait
    /// for <see cref="UiScreens.Portraits"/>.
    /// </summary>
    static void BuildEntry(UiHost host, Entry entry, ScreenView view, List<UiControl> controls, UiFrameContext frame, List<UiControl> local)
    {
        if (entry.Anchor == UiScreens.Portraits)
        {
            BuildPortraits(host, entry, view, controls, frame, local);
            return;
        }

        if (entry.Anchor != UiScreens.ItemCells)
        {
            if (controls.FirstOrDefault(c => c.Name == entry.Anchor) is { } anchor) Build(host, entry, view, anchor, null, frame, local);
            return;
        }

        if (view.ItemOf is null) return;

        var cells = controls
            .Select(c => (Control: c, Item: view.ItemOf(c)))
            .Where(c => c.Item is not null)
            .Take(MaxCells)
            .ToList();
        foreach (var (cell, item) in cells) Build(host, entry, view, cell, item, frame, local);
    }

    /// <summary>
    /// <paramref name="entry"/>'s controls once per squad bar portrait shown.
    /// </summary>
    static void BuildPortraits(UiHost host, Entry entry, ScreenView view, List<UiControl> controls, UiFrameContext frame, List<UiControl> local)
    {
        if (view.PortraitOf is null) return;

        var portraits = controls
            .Select(c => (Control: c, Member: view.PortraitOf(c)))
            .Where(c => c.Member is not null)
            .ToList();
        foreach (var (portrait, member) in portraits) Build(host, entry, view, portrait, null, frame, local, member);
    }

    /// <summary>
    /// One build of <paramref name="entry"/> against <paramref name="anchor"/>, its controls named after the extension and the anchor.
    /// </summary>
    static void Build(UiHost host, Entry entry, ScreenView view, UiControl anchor, UiScreenItem? item, UiFrameContext frame, List<UiControl> local,
        CharacterSnapshot? member = null)
    {
        var node = entry.Build(new UiScreenContext(view, entry.Anchor, anchor.Bounds, item, frame, member));
        if (node is null) return;

        var (parent, clip) = Parent(anchor, entry.Placement, frame.Viewport);
        var arranged = new List<UiControl>();
        host.ArrangeInto(node, parent, clip, arranged);
        foreach (var c in arranged)
        {
            var properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal) { [OwnerProperty] = entry.Owner };
            local.Add(c with { Name = $"{entry.Owner}:{anchor.Name}/{c.Name}", Properties = properties });
        }
    }

    /// <summary>
    /// The rectangle a node is laid out in for <paramref name="placement"/> against <paramref name="anchor"/>, and its clip: inside, the anchor
    /// itself within its own clip; outside, the screen (an anchor's clip is its frame, which the room beside it usually lies outside).
    /// </summary>
    static (UiRect Parent, UiRect Clip) Parent(UiControl anchor, UiAnchorPlacement placement, UiRect viewport)
    {
        var a = anchor.Bounds;
        return placement switch
        {
            UiAnchorPlacement.Above => (a with { Y = a.Y - a.Height }, viewport),
            UiAnchorPlacement.Below => (a with { Y = a.Y + a.Height }, viewport),
            UiAnchorPlacement.Left => (a with { X = a.X - a.Width }, viewport),
            UiAnchorPlacement.Right => (a with { X = a.X + a.Width }, viewport),
            _ => (a, a.Intersect(anchor.Clip ?? viewport)),
        };
    }

    /// <summary>
    /// Adds <paramref name="added"/> (cut at <see cref="MaxControls"/>, reported once per screen) to the screen and keeps them for its presses.
    /// </summary>
    void Commit(string screen, List<UiControl> controls, List<UiControl> added, Action<UiDiagnostic>? report)
    {
        if (added.Count == 0) return;

        if (added.Count > MaxControls)
        {
            if (_cut.Add(screen))
            {
                var error = new InvalidOperationException($"A screen holds {MaxControls} controls of all extensions together; the rest are left out.");
                report?.Invoke(new UiDiagnostic(added[MaxControls].Properties[OwnerProperty], $"screen controls {screen} (past the limit)", error));
            }

            added.RemoveRange(MaxControls, added.Count - MaxControls);
        }

        controls.AddRange(added);
        var shown = new Dictionary<string, UiControl>(StringComparer.Ordinal);
        foreach (var c in added.Where(c => c.Action.Length > 0)) shown[c.Name] = c;
        foreach (var c in added.Where(c => c.Type == "EditBox")) _textBoxes[c.Name] = c;
        _shown[screen] = shown;
    }
}
