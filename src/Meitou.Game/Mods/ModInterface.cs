using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// A game screen a mod opens or reads (<see cref="ModInterface.OpenScreen"/>, docs/modding.md "Interface actions"). New kinds are added at the end.
/// </summary>
public enum ModScreen
{
    /// <summary>
    /// The character editor for <see cref="ScreenTarget.Characters"/>, opened as a recruit's edit (free, race and gender kept).
    /// </summary>
    CharacterEditor,

    /// <summary>
    /// The overview window on tab <see cref="ScreenTarget.Tab"/>: 0 map, 1 faction, 2 research, 3 crafting, 4 squads, as the game's keys number them.
    /// </summary>
    Overview,

    /// <summary>
    /// The build window (build mode on, as the B key).
    /// </summary>
    Build,
}

/// <summary>
/// What a screen opens on: the characters an editor edits, the tab an overview shows. A kind reads only its own fields.
/// </summary>
public readonly record struct ScreenTarget(IReadOnlyList<CharacterId>? Characters = null, int Tab = 0)
{
    /// <summary>
    /// A target of the characters given.
    /// </summary>
    public static ScreenTarget Of(params CharacterId[] characters) => new(characters);
}

/// <summary>
/// One building the build window lists, in the window's own order: its BUILDING string id and name, its category, its group's name and the group's
/// index in that category's list.
/// </summary>
public sealed record UiBuildEntry(string Building, string Name, string Category, string Group, int GroupIndex);

/// <summary>
/// The interface's side of <see cref="ModInterface"/>: the player interface, attached when it is made. Host thread only.
/// </summary>
interface IModInterfaceHost
{
    /// <summary>
    /// Opens a screen; false when it cannot open now.
    /// </summary>
    bool Open(ModScreen kind, ScreenTarget target);

    /// <summary>
    /// Whether a screen shows now.
    /// </summary>
    bool IsOpen(ModScreen kind);

    /// <summary>
    /// Where a window shows on screen as last placed, in pixels; null when it does not show or has no window.
    /// </summary>
    UiRect? Bounds(ModScreen kind);

    /// <summary>
    /// The build window's buildings while it shows (else none).
    /// </summary>
    IReadOnlyList<UiBuildEntry> BuildEntries();

    /// <summary>
    /// The building the build window holds chosen; null without one or while it does not show.
    /// </summary>
    string? BuildChoice { get; }

    /// <summary>
    /// Chooses a listed building in the build window as clicks on its category, group and variant would; false when it does not show or lists none
    /// of that id.
    /// </summary>
    bool ChooseBuild(string building);

    /// <summary>
    /// The game clock the speed buttons and keys set (the session's).
    /// </summary>
    Simulation.Session.SimulationClock Clock { get; }
}

/// <summary>
/// The interface the mods' <see cref="ModInterface"/>s share: the attached host, its thread and the log lines already written.
/// </summary>
sealed class ModInterfaceHub
{
    /// <summary>
    /// The player interface; null until one is made (a headless run has none).
    /// </summary>
    IModInterfaceHost? _host;
    /// <summary>
    /// The thread the interface was attached on (the host's).
    /// </summary>
    volatile int _hostThread;
    /// <summary>
    /// The (mod, problem) pairs logged already, so a mod calling every frame logs once.
    /// </summary>
    readonly HashSet<(string Mod, bool OffThread)> _logged = [];

    /// <summary>
    /// The mods' holds on the pause (<see cref="ModInterface.HoldPause"/>).
    /// </summary>
    internal ModPauseHolds Holds { get; } = new();

    /// <summary>
    /// Attaches the interface made on this thread; a new interface replaces the old and drops the pause holds on the old one's clock.
    /// </summary>
    internal void Attach(IModInterfaceHost host)
    {
        Holds.Drop();
        _host = host;
        _hostThread = Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// The interface when called on its thread; else null, logged once per mod and problem.
    /// </summary>
    internal IModInterfaceHost? Host(string mod)
    {
        if (_host is null) return Refuse(mod, offThread: false);
        if (Environment.CurrentManagedThreadId != _hostThread) return Refuse(mod, offThread: true);

        return _host;
    }

    /// <summary>
    /// Logs a refused call once per mod and problem (no interface, or a call off the host's thread); null.
    /// </summary>
    IModInterfaceHost? Refuse(string mod, bool offThread)
    {
        bool first;
        lock (_logged) first = _logged.Add((mod, offThread));
        if (!first) return null;

        Console.Error.WriteLine($"mod       {mod}: interface call refused: {(offThread ? "off the host's thread (a tick system?)" : "no interface yet")}");
        return null;
    }
}

/// <summary>
/// The game's screens for one mod (docs/modding.md "Interface actions"): open the character editor, the overview or the build window, see what
/// shows, and read and choose the build window's buildings. Host thread only (a UI action, a key action, a hit feed, a menu row's choice): a call
/// off it, or without an interface, does nothing and answers false, empty or null, logged once per mod and problem. Display state only: the
/// simulation never reads it, so nothing here enters the state hash or a save.
/// </summary>
public sealed class ModInterface
{
    /// <summary>
    /// The interface the mods share.
    /// </summary>
    readonly ModInterfaceHub _hub;
    /// <summary>
    /// The mod's id, for log lines.
    /// </summary>
    readonly string _mod;

    /// <summary>
    /// The facade of mod <paramref name="mod"/> over <paramref name="hub"/>.
    /// </summary>
    internal ModInterface(ModInterfaceHub hub, string mod)
    {
        _hub = hub;
        _mod = mod;
    }

    /// <summary>
    /// Opens <paramref name="kind"/> on <paramref name="target"/>; false when it cannot open now: the editor for no character in the snapshot (one
    /// made this tick reaches it a frame or more later: try again next frame), with the editor already open, a tab out of 0 to 4, or over a modal
    /// screen; and for any kind off the host's thread.
    /// </summary>
    public bool OpenScreen(ModScreen kind, ScreenTarget target = default) => _hub.Host(_mod)?.Open(kind, target) ?? false;

    /// <summary>
    /// Whether <paramref name="kind"/> shows now.
    /// </summary>
    public bool IsOpen(ModScreen kind) => _hub.Host(_mod)?.IsOpen(kind) ?? false;

    /// <summary>
    /// Where <paramref name="kind"/>'s window shows, in screen pixels as last placed (a mod's window placed beside it); null while it does not show.
    /// </summary>
    public UiRect? ScreenBounds(ModScreen kind) => _hub.Host(_mod)?.Bounds(kind);

    /// <summary>
    /// The build window's buildings in its own order (each listed category, its groups, each group's members) while it shows; none otherwise.
    /// </summary>
    public IReadOnlyList<UiBuildEntry> BuildEntries() => _hub.Host(_mod)?.BuildEntries() ?? [];

    /// <summary>
    /// The BUILDING the build window holds chosen; null without one or while it does not show.
    /// </summary>
    public string? BuildChoice => _hub.Host(_mod)?.BuildChoice;

    /// <summary>
    /// Chooses <paramref name="building"/> (a BUILDING string id the window lists) in the build window, as clicks on its category, its group and
    /// then its variant; false when the window does not show or does not list it.
    /// </summary>
    public bool ChooseBuild(string building)
    {
        ArgumentNullException.ThrowIfNull(building);
        return _hub.Host(_mod)?.ChooseBuild(building) ?? false;
    }

    /// <summary>
    /// The game speed (0 while paused by the player; a menu's or the loading pause does not change it); 0 when refused.
    /// </summary>
    public double Speed => _hub.Host(_mod)?.Clock.Speed ?? 0;

    /// <summary>
    /// Whether no ticks run: paused by the player, a menu or the loading pause; false when refused.
    /// </summary>
    public bool Paused => _hub.Host(_mod)?.Clock.IsPaused ?? false;

    /// <summary>
    /// The speeds the game's speed buttons set (1, then the player's two fast ones); none when refused.
    /// </summary>
    public IReadOnlyList<double> Speeds => _hub.Host(_mod)?.Clock.Speeds ?? [];

    /// <summary>
    /// The highest speed <see cref="SetSpeed"/> takes (the top of the third speed's range).
    /// </summary>
    public const double MaxSpeed = Simulation.Session.SimulationClock.MaxFastestSpeed;

    /// <summary>
    /// Sets the game speed as a speed button does: 0 pauses (as the pause button, which the pause key does not resume at a stored speed); any other
    /// in (0, <see cref="MaxSpeed"/>] runs at it. False for a speed out of range or a refused call.
    /// </summary>
    public bool SetSpeed(double speed)
    {
        if (!(speed >= 0 && speed <= MaxSpeed) || _hub.Host(_mod) is not { } host) return false;

        host.Clock.SetSpeed(speed);
        return true;
    }

    /// <summary>
    /// The pause key's pause (<paramref name="paused"/>: the speed is stored, then 0) or resume (the stored speed; only while the speed is 0). No
    /// change when already so. False when refused.
    /// </summary>
    public bool Pause(bool paused)
    {
        if (_hub.Host(_mod) is not { } host) return false;

        var clock = host.Clock;
        if (paused && clock.Speed != 0) clock.Pause();
        else if (!paused && clock.Speed == 0) clock.PauseKey();

        return true;
    }

    /// <summary>
    /// Holds the game paused while a mod's window shows: the first hold of all the mods keeps the player's speeds, each hold pauses the game when it
    /// runs, and releasing the last one puts the kept speeds back if the game is still paused (a speed the player chose meanwhile stays). A new
    /// session drops the holds. Null when refused.
    /// </summary>
    public ModPauseHold? HoldPause()
    {
        if (_hub.Host(_mod) is not { } host) return null;

        _hub.Holds.Hold(host.Clock);
        return new ModPauseHold(this, _hub.Holds.Generation);
    }

    /// <summary>
    /// Whether holds of <paramref name="generation"/> still count.
    /// </summary>
    internal bool Holds(int generation) => _hub.Holds.Generation == generation;

    /// <summary>
    /// Releases one hold of <paramref name="generation"/>; false when the call is refused.
    /// </summary>
    internal bool Release(int generation)
    {
        if (_hub.Host(_mod) is null) return false;

        _hub.Holds.Release(generation);
        return true;
    }
}
