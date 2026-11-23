using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// The world map as last drawn (the overview's Map tab): the whole map image's rectangle on screen (it pans and zooms), the viewport it is clipped
/// to, the zoom level (0, the whole map, to 2) and the UI scale; with conversions between screen pixels, map points (0..1 each way over the image)
/// and world points (X, Z; the image spans the world's 294912 units, world 0, 0 at its centre).
/// </summary>
public sealed record UiWorldMapView(UiRect Image, UiRect Viewport, int Level, float UiScale)
{
    /// <summary>
    /// Half the world's width the map spans, in world units.
    /// </summary>
    internal const float Extent = 147456;

    /// <summary>
    /// The map point (0..1 each way, not clamped) under screen pixel <paramref name="screen"/>.
    /// </summary>
    public Vector2 ScreenToMap(Vector2 screen) => new((screen.X - Image.X) / Image.Width, (screen.Y - Image.Y) / Image.Height);

    /// <summary>
    /// The screen pixel of map point <paramref name="map"/>.
    /// </summary>
    public Vector2 MapToScreen(Vector2 map) => new(Image.X + map.X * Image.Width, Image.Y + map.Y * Image.Height);

    /// <summary>
    /// The world point (X, Z) of map point <paramref name="map"/>.
    /// </summary>
    public static Vector2 MapToWorld(Vector2 map) => map * (2 * Extent) - new Vector2(Extent);

    /// <summary>
    /// The map point of world point <paramref name="world"/> (X, Z).
    /// </summary>
    public static Vector2 WorldToMap(Vector2 world) => (world + new Vector2(Extent)) / (2 * Extent);

    /// <summary>
    /// The world point (X, Z) under screen pixel <paramref name="screen"/>.
    /// </summary>
    public Vector2 ScreenToWorld(Vector2 screen) => MapToWorld(ScreenToMap(screen));

    /// <summary>
    /// The screen pixel of world point <paramref name="world"/> (X, Z).
    /// </summary>
    public Vector2 WorldToScreen(Vector2 world) => MapToScreen(WorldToMap(world));

    /// <summary>
    /// Whether <paramref name="screen"/> lies in the visible viewport.
    /// </summary>
    public bool Shows(Vector2 screen) => Viewport.Contains(screen.X, screen.Y);
}

/// <summary>
/// What happened on the world map (<see cref="UiRegistration.AddMapInput"/>); more kinds may follow.
/// </summary>
public enum UiMapInputKind
{
    /// <summary>
    /// A mouse button went down over the map image or one of its markers (<see cref="UiMapInput.Button"/>, <see cref="UiMapInput.Point"/>).
    /// </summary>
    Press,

    /// <summary>
    /// A key went down while the map shows and no window's text box took it (<see cref="UiMapInput.Key"/>).
    /// </summary>
    Key,

    /// <summary>
    /// The map stopped showing (its tab changed, or the overview closed).
    /// </summary>
    Closed,
}

/// <summary>
/// A world map input an extension gets: its kind, the button or key, the pointer and the modifiers, whether a text box has the keys, the map as
/// drawn (null for <see cref="UiMapInputKind.Closed"/>), the snapshot, the tick and the command queue.
/// </summary>
public sealed class UiMapInput(UiMapInputKind kind, UiWorldMapView? view, WorldSnapshot snapshot, Func<long> tick, Action<SimCommand> enqueue)
{
    /// <summary>
    /// What happened.
    /// </summary>
    public UiMapInputKind Kind { get; } = kind;

    /// <summary>
    /// The button pressed (<see cref="UiMapInputKind.Press"/>).
    /// </summary>
    public MouseButton Button { get; init; }

    /// <summary>
    /// The key pressed (<see cref="UiMapInputKind.Key"/>).
    /// </summary>
    public Key Key { get; init; }

    /// <summary>
    /// The pointer in screen pixels.
    /// </summary>
    public Vector2 Point { get; init; }

    /// <summary>
    /// Whether Shift is held.
    /// </summary>
    public bool Shift { get; init; }

    /// <summary>
    /// Whether Ctrl is held.
    /// </summary>
    public bool Ctrl { get; init; }

    /// <summary>
    /// Whether Alt is held.
    /// </summary>
    public bool Alt { get; init; }

    /// <summary>
    /// Whether a text box (a window's or a mod's) has the keys.
    /// </summary>
    public bool Typing { get; init; }

    /// <summary>
    /// The map as last drawn; null once it closed.
    /// </summary>
    public UiWorldMapView? View { get; } = view;

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
/// The extensions' world map input handlers (docs/modding.md "World map input").
/// </summary>
sealed class MapInputs
{
    /// <summary>
    /// The handlers, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiMapInput, bool> Handle)> _handlers = [];

    /// <summary>
    /// Whether any extension handles map input.
    /// </summary>
    public bool Any => _handlers.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s handler.
    /// </summary>
    public void Add(string owner, Func<UiMapInput, bool> handle) => _handlers.Add((owner, handle));

    /// <summary>
    /// Removes <paramref name="owner"/>'s handlers.
    /// </summary>
    public void Remove(string owner) => _handlers.RemoveAll(h => h.Owner == owner);

    /// <summary>
    /// Hands <paramref name="input"/> to the handlers in registration order until one takes it (true); every handler sees
    /// <see cref="UiMapInputKind.Closed"/>. A handler that throws is reported once and dropped.
    /// </summary>
    public bool Handle(UiMapInput input, Action<UiDiagnostic>? report)
    {
        foreach (var entry in _handlers.ToArray())
        {
            try
            {
                if (entry.Handle(input) && input.Kind != UiMapInputKind.Closed) return true;
            }
            catch (Exception e)
            {
                _handlers.Remove(entry);
                report?.Invoke(new UiDiagnostic(entry.Owner, "map input (dropped)", e));
            }
        }

        return false;
    }
}
