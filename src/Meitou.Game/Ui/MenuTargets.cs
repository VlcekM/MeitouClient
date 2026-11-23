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
    /// The container's, door's or building's id, as commands name it; null for the other kinds.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// The container's or building's record string id; null when unknown or for the other kinds.
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
    /// <paramref name="guard"/>. A source that throws, or gives a row without a caption or action, is reported once and dropped.
    /// </summary>
    public IReadOnlyList<UiMenuVerb> For(UiMenuTargetContext context, Func<string, UiMenuVerb, Action> guard, Action<UiDiagnostic>? report)
    {
        var rows = new List<UiMenuVerb>();
        foreach (var entry in _sources.ToArray())
        {
            try
            {
                var added = entry.Verbs(context).ToList();
                if (added.Any(v => v is null || string.IsNullOrWhiteSpace(v.Caption) || v.Choose is null))
                    throw new InvalidOperationException("A menu row needs a caption and an action.");

                rows.AddRange(added.Select(v => v with { Choose = guard(entry.Owner, v) }));
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
