using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game.Ui;

/// <summary>
/// What a right-click menu is for (<see cref="UiRegistration.AddMenuVerbs"/>); more kinds may follow.
/// </summary>
public enum UiMenuTargetKind
{
    /// <summary>
    /// A character in the world or on the squad bar (<see cref="UiMenuTarget.Character"/>).
    /// </summary>
    Character,

    /// <summary>
    /// A loose item on the ground (<see cref="UiMenuTarget.GroundItem"/>).
    /// </summary>
    GroundItem,

    /// <summary>
    /// A container: a storage with an inventory (<see cref="UiMenuTarget.Id"/> is its id).
    /// </summary>
    Container,

    /// <summary>
    /// A door (<see cref="UiMenuTarget.Id"/> is its id).
    /// </summary>
    Door,

    /// <summary>
    /// A player-built building, placed piece or piece of furniture with a site (<see cref="UiMenuTarget.Id"/> is the site's building id).
    /// </summary>
    Building,

    /// <summary>
    /// The ground: a point with nothing else under the cursor, which a quick click orders a move to.
    /// </summary>
    WorldPoint,

    /// <summary>
    /// A piece of furniture that is no player-built site: a town's or camp's cage (<see cref="UiMenuTarget.Id"/> is its fixture id,
    /// <see cref="UiMenuTarget.Record"/> its BUILDING). More furniture menus may follow.
    /// </summary>
    Furniture,
}

/// <summary>
/// The target of a right-click menu: its kind, the menu's title (empty for the ground) and its world position; the character, ground item, or the
/// id and record of a container, door or building, by kind.
/// </summary>
public sealed record UiMenuTarget(UiMenuTargetKind Kind, string Name, Vector3 Position)
{
    /// <summary>
    /// The character (<see cref="UiMenuTargetKind.Character"/>); null for the other kinds.
    /// </summary>
    public CharacterSnapshot? Character { get; init; }

    /// <summary>
    /// The ground item (<see cref="UiMenuTargetKind.GroundItem"/>; its <c>Id</c> names it in commands); null for the other kinds.
    /// </summary>
    public GroundItemSnapshot? GroundItem { get; init; }

    /// <summary>
    /// The container's, door's, building's or furniture's id, as commands name it; null for the other kinds.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// The container's, building's or furniture's record string id; null when unknown or for the other kinds.
    /// </summary>
    public string? Record { get; init; }

    /// <summary>
    /// A character target.
    /// </summary>
    internal static UiMenuTarget Of(CharacterSnapshot c) => new(UiMenuTargetKind.Character, c.Name, c.Position) { Character = c };
}

/// <summary>
/// What an extension's menu rows get (<see cref="UiRegistration.AddMenuVerbs"/>): the target, the selected characters and the cursor when the menu
/// was built; Shift and the tick read when asked; and the command queue.
/// </summary>
public sealed class UiMenuTargetContext(UiMenuTarget target, IReadOnlyList<CharacterId> selected, Vector2 cursor, Func<bool> shift,
    Func<long> tick, Action<SimCommand> enqueue)
{
    /// <summary>
    /// What the menu is for.
    /// </summary>
    public UiMenuTarget Target { get; } = target;

    /// <summary>
    /// The selected characters when the menu was built (never empty: a menu is built only with a selection).
    /// </summary>
    public IReadOnlyList<CharacterId> Selected { get; } = selected;

    /// <summary>
    /// The cursor (screen pixels) when the menu was built.
    /// </summary>
    public Vector2 Cursor { get; } = cursor;

    /// <summary>
    /// Whether Shift is held now.
    /// </summary>
    public bool Shift => shift();

    /// <summary>
    /// The world's tick now, for a command's <see cref="SimCommand.Tick"/>.
    /// </summary>
    public long Tick => tick();

    /// <summary>
    /// Queues a command for the world.
    /// </summary>
    public void Enqueue(SimCommand command) => enqueue(command);
}

/// <summary>
/// The extensions' right-click menu row sources for any target (docs/modding.md "Right-click menu rows").
/// </summary>
sealed class MenuTargetVerbs
{
    /// <summary>
    /// The sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiMenuTargetContext, IEnumerable<UiMenuVerb>> Verbs)> _sources = [];

    /// <summary>
    /// Whether any extension adds rows.
    /// </summary>
    public bool Any => _sources.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source.
    /// </summary>
    public void Add(string owner, Func<UiMenuTargetContext, IEnumerable<UiMenuVerb>> verbs) => _sources.Add((owner, verbs));

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources.
    /// </summary>
    public void Remove(string owner) => _sources.RemoveAll(s => s.Owner == owner);

    /// <summary>
    /// The rows for <paramref name="context"/>, in registration order (a copy of the sources is walked), each row's action guarded by
    /// <paramref name="guard"/>, its caption shown through <paramref name="text"/> (the owner's translation; null: as written). A source that throws,
    /// or gives a row without a caption or action, is reported once and dropped.
    /// </summary>
    public IReadOnlyList<UiMenuVerb> For(UiMenuTargetContext context, Func<string, UiMenuVerb, Action> guard, Action<UiDiagnostic>? report,
        Func<string, string, string>? text = null)
    {
        var rows = new List<UiMenuVerb>();
        foreach (var entry in _sources.ToArray())
        {
            try
            {
                var added = entry.Verbs(context).ToList();
                if (added.Any(v => v is null || string.IsNullOrWhiteSpace(v.Caption) || v.Choose is null))
                    throw new InvalidOperationException("A menu row needs a caption and an action.");

                string Shown(string caption) => text?.Invoke(entry.Owner, caption) ?? caption;
                rows.AddRange(added.Select(v => v with { Caption = Shown(v.Caption), Choose = guard(entry.Owner, v) }));
            }
            catch (Exception e)
            {
                _sources.Remove(entry);
                report?.Invoke(new UiDiagnostic(entry.Owner, "menu verbs (dropped)", e));
            }
        }

        return rows;
    }
}

/// <summary>
/// One row of a right-click menu as it opens, for an extension's row overrides (<see cref="UiRegistration.AddMenuRowOverrides"/>): the menu's
/// title, the row's caption (a game row's own label, untranslated, as "Pick Up" or "Steal"; a mod row's caption), its chance column (null: none),
/// its index in the menu, the menu's target (null for a menu without one, such as a turret's or a town machine's) and the selection.
/// </summary>
public sealed record UiMenuRowContext(string Title, string Caption, float? Chance, int Index, UiMenuTarget? Target,
    IReadOnlyList<CharacterId> Selected);

/// <summary>
/// What an extension changes on one row of a right-click menu (<see cref="UiRegistration.AddMenuRowOverrides"/>); each member left null keeps the
/// row's own. <see cref="Caption"/> replaces its label (translated as the game's, its chance column kept), <see cref="TextColour"/> its caption's
/// colour (RGB, 0 to 1 each; alpha not used) and <see cref="Visible"/> false leaves the row out.
/// </summary>
public sealed record UiMenuRowOverride
{
    /// <summary>
    /// The label the row shows instead of its own; null keeps it.
    /// </summary>
    public string? Caption { get; init; }

    /// <summary>
    /// The caption's colour (red, green, blue, each 0 to 1); null keeps it. An unavailable row stays greyed.
    /// </summary>
    public Vector4? TextColour { get; init; }

    /// <summary>
    /// False leaves the row out of the menu; null or true keeps it.
    /// </summary>
    public bool? Visible { get; init; }
}

/// <summary>
/// The extensions' right-click menu row overrides (docs/modding.md "Right-click menu rows"): each source is asked for every row of a menu as the
/// menu is built, game rows and mod rows alike, in registration order, each seeing the last one's changes.
/// </summary>
sealed class MenuRowOverrides
{
    /// <summary>
    /// The longest caption an override may set.
    /// </summary>
    public const int MaxCaption = 200;

    /// <summary>
    /// The sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiMenuRowContext, UiMenuRowOverride?> Override)> _sources = [];

    /// <summary>
    /// Whether any extension overrides menu rows.
    /// </summary>
    public bool Any => _sources.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source.
    /// </summary>
    public void Add(string owner, Func<UiMenuRowContext, UiMenuRowOverride?> over) => _sources.Add((owner, over));

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources.
    /// </summary>
    public void Remove(string owner) => _sources.RemoveAll(s => s.Owner == owner);

    /// <summary>
    /// Asks each source about <paramref name="row"/> in turn (each seeing the last one's changes) and answers the changes together; null when no
    /// source changes it. A source that throws or answers a caption too long or blank, or a colour not finite, is reported once and dropped.
    /// </summary>
    public UiMenuRowOverride? For(UiMenuRowContext row, Action<UiDiagnostic>? report)
    {
        UiMenuRowOverride? result = null;
        foreach (var entry in _sources.ToArray())
        {
            try
            {
                var context = result?.Caption is { } caption ? row with { Caption = caption } : row;
                if (entry.Override(context) is not { } over) continue;

                Validate(over);
                result = Merge(result, over);
            }
            catch (Exception e)
            {
                _sources.Remove(entry);
                report?.Invoke(new UiDiagnostic(entry.Owner, "menu row overrides (dropped)", e));
            }
        }

        return result;
    }

    /// <summary>
    /// Throws for an override whose caption is blank or too long, or whose colour is not finite.
    /// </summary>
    static void Validate(UiMenuRowOverride over)
    {
        if (over.Caption is { } caption && (string.IsNullOrWhiteSpace(caption) || caption.Length > MaxCaption))
            throw new InvalidOperationException($"A menu row override's caption is not blank and at most {MaxCaption} characters.");
        if (over.TextColour is { } c && !float.IsFinite(c.X + c.Y + c.Z))
            throw new InvalidOperationException("A menu row override's colour is finite.");
    }

    /// <summary>
    /// <paramref name="later"/>'s changes over <paramref name="earlier"/>'s.
    /// </summary>
    static UiMenuRowOverride Merge(UiMenuRowOverride? earlier, UiMenuRowOverride later) => new()
    {
        Caption = later.Caption ?? earlier?.Caption,
        TextColour = later.TextColour ?? earlier?.TextColour,
        Visible = later.Visible ?? earlier?.Visible,
    };
}
