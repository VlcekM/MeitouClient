using Meitou.Game.Host.Input;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// What a key going down to the interface is (<see cref="UiRegistration.AddKeyInput"/>); more kinds may follow.
/// </summary>
public enum UiKeyInputKind
{
    /// <summary>
    /// A key went down (<see cref="UiKeyInput.Key"/>).
    /// </summary>
    Down,
}

/// <summary>
/// A key an extension gets before the interface and the game's bindings use it: the key, the modifiers held, the game screens open, the snapshot,
/// the tick and the command queue (docs/modding.md "Interface keys").
/// </summary>
public sealed class UiKeyInput(UiKeyInputKind kind, Key key, IReadOnlyList<string> openScreens, WorldSnapshot snapshot, Func<long> tick,
    Action<SimCommand> enqueue)
{
    /// <summary>
    /// What happened.
    /// </summary>
    public UiKeyInputKind Kind { get; } = kind;

    /// <summary>
    /// The key.
    /// </summary>
    public Key Key { get; } = key;

    /// <summary>
    /// The key is Esc: the player's cancel, which the game uses next to close the top window or open the game menu.
    /// </summary>
    public bool Cancel => Key == Key.Escape;

    /// <summary>
    /// Whether either Ctrl key is held.
    /// </summary>
    public bool Ctrl { get; init; }

    /// <summary>
    /// Whether either Shift key is held.
    /// </summary>
    public bool Shift { get; init; }

    /// <summary>
    /// Whether either Alt key is held.
    /// </summary>
    public bool Alt { get; init; }

    /// <summary>
    /// The game screens open now by their layout ids, the front one last (as <c>ModFrame.OpenScreens</c>).
    /// </summary>
    public IReadOnlyList<string> OpenScreens { get; } = openScreens;

    /// <summary>
    /// The modifiers held as a chord's (<see cref="Binding.With"/>), for a mod capturing a key for its key action.
    /// </summary>
    public KeyModifiers Modifiers => InputBindings.Modifiers(Ctrl, Shift, Alt);

    /// <summary>
    /// The snapshot on screen.
    /// </summary>
    public WorldSnapshot Snapshot { get; } = snapshot;

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
/// The extensions' interface key handlers (docs/modding.md "Interface keys").
/// </summary>
sealed class KeyInputs
{
    /// <summary>
    /// The handlers, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiKeyInput, bool> Handle)> _handlers = [];

    /// <summary>
    /// Whether any extension handles interface keys.
    /// </summary>
    public bool Any => _handlers.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s handler.
    /// </summary>
    public void Add(string owner, Func<UiKeyInput, bool> handle) => _handlers.Add((owner, handle));

    /// <summary>
    /// Removes <paramref name="owner"/>'s handlers.
    /// </summary>
    public void Remove(string owner) => _handlers.RemoveAll(h => h.Owner == owner);

    /// <summary>
    /// Hands <paramref name="input"/> to the handlers in registration order until one takes it (true). A handler that throws is reported once and
    /// dropped.
    /// </summary>
    public bool Handle(UiKeyInput input, Action<UiDiagnostic>? report)
    {
        foreach (var entry in _handlers.ToArray())
        {
            try
            {
                if (entry.Handle(input)) return true;
            }
            catch (Exception e)
            {
                _handlers.Remove(entry);
                report?.Invoke(new UiDiagnostic(entry.Owner, "key input (dropped)", e));
            }
        }

        return false;
    }
}
