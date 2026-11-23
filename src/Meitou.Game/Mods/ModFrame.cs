using System.Numerics;
using Meitou.Game.Host.Input;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// What the host shows this frame, for a code mod's host-side code (docs/modding.md "Frame"): the snapshot on screen, the selection, the
/// character under the pointer, the modifiers held, the game speed, the screen, the camera, the projection of world points, the open screens and
/// the player's bindings, read when asked; and the interface's notice line. Given to <see cref="ModContext.AddFrame"/> callbacks once a frame
/// and to key presses (<see cref="ModKeyContext.Frame"/>). Host thread only, valid during the call that got it: a tick may be in flight, so a
/// mod must not touch the world, and keeps copies, not the frame.
/// </summary>
public sealed class ModFrame
{
    /// <summary>
    /// The host the reads come from.
    /// </summary>
    readonly Game.GameHost _host;

    /// <summary>
    /// A frame of <paramref name="host"/>'s interface.
    /// </summary>
    internal ModFrame(Game.GameHost host) => _host = host;

    /// <summary>
    /// The interface.
    /// </summary>
    Game.PlayerInterface Ui => _host.Player;

    /// <summary>
    /// The world snapshot on screen.
    /// </summary>
    public WorldSnapshot Snapshot => _host.Session.CurrentSnapshot;

    /// <summary>
    /// The tick the next command is due at.
    /// </summary>
    public long Tick => _host.Session.World.Tick;

    /// <summary>
    /// The selected characters, the first the lead.
    /// </summary>
    public IReadOnlyList<CharacterId> Selected => Ui.SelectedIds();

    /// <summary>
    /// The character the HUD shows: an inspected one, else the first selected; null without either.
    /// </summary>
    public CharacterId? Shown => Ui.Lead()?.Id;

    /// <summary>
    /// The character under the pointer as the last pick found it; null for none.
    /// </summary>
    public CharacterId? Hovered => _host.Session.World.Player.Pointed is { IsNone: false } id ? id : null;

    /// <summary>
    /// The pointer in screen pixels.
    /// </summary>
    public Vector2 Pointer => Ui.Mouse;

    /// <summary>
    /// Either Ctrl key is held now.
    /// </summary>
    public bool Ctrl => Held(Key.LeftControl, Key.RightControl);

    /// <summary>
    /// Either Shift key is held now.
    /// </summary>
    public bool Shift => Held(Key.LeftShift, Key.RightShift);

    /// <summary>
    /// Either Alt key is held now.
    /// </summary>
    public bool Alt => Held(Key.LeftAlt, Key.RightAlt);

    /// <summary>
    /// The Highlight key (Alt by default) as the interface last saw it: what lets an order steal or pick a lock and turns the pointer over a downed
    /// character into the loot one.
    /// </summary>
    public bool Highlight => Ui.AltHeld;

    /// <summary>
    /// Whether the game is paused.
    /// </summary>
    public bool Paused => _host.Session.Simulation.IsPaused;

    /// <summary>
    /// The game speed (0 while paused).
    /// </summary>
    public double Speed => _host.Session.Simulation.Speed;

    /// <summary>
    /// The screen's size in pixels.
    /// </summary>
    public Vector2 ScreenSize => new(Ui.Width, Ui.Height);

    /// <summary>
    /// The interface's scale: the screen's height over the 1080 lines the layouts are made for, times the player's interface scale.
    /// </summary>
    public float UiScale => Ui.UiScale;

    /// <summary>
    /// Whether the interface shows (not hidden by its key, a photo mode or the title).
    /// </summary>
    public bool UiVisible => Ui.UiVisible && !Ui.TitleOpen;

    /// <summary>
    /// The camera's eye in the world.
    /// </summary>
    public Vector3 CameraEye => _host.Session.Camera.Current.Eye;

    /// <summary>
    /// The point the camera looks at.
    /// </summary>
    public Vector3 CameraTarget => _host.Session.Camera.Current.Target;

    /// <summary>
    /// The screen pixel of world point <paramref name="point"/> as the last frame was drawn; null behind the camera.
    /// </summary>
    public Vector2? Project(Vector3 point) => Ui.Project(point);

    /// <summary>
    /// The game screens open now by their layout ids (<c>inventory</c>, <c>overview</c>, <c>build</c>, <c>editor</c>, <c>trader</c>...), the
    /// front one last.
    /// </summary>
    public IReadOnlyList<string> OpenScreens => [.. Ui.FaithfulScreens.OpenIds];

    /// <summary>
    /// Whether game screen <paramref name="id"/> is open.
    /// </summary>
    public bool IsOpen(string id) => Ui.FaithfulScreens.IsOpen(id);

    /// <summary>
    /// The player's keys and buttons for a game action (or a mod's key action), as the Controls page binds them.
    /// </summary>
    public IReadOnlyList<Binding> Bindings(InputAction action) => _host.Session.Bindings.Get(action);

    /// <summary>
    /// Shows <paramref name="text"/> on the interface's notice line, as the game's own notices (a message of the game's catalog translated;
    /// <paramref name="error"/>: the error tone).
    /// </summary>
    public void Notify(string text, bool error = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Ui.Notice(text, error);
    }

    /// <summary>
    /// Whether either of two keys is held.
    /// </summary>
    bool Held(Key left, Key right) => _host.Session.Input.IsHeld(left) || _host.Session.Input.IsHeld(right);
}
