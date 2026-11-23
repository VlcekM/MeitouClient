namespace Meitou.Simulation;

/// <summary>
/// A system told once that its world is ready to run (docs/modding.md "World ready"): after a save has loaded (the mods' saved state read back
/// and settings of mods no longer running dropped) or a new game has placed the player's characters, between ticks, before the first. Called on
/// the tick thread in system order (mods in load order); it may change the world as a serial phase does, for example set a mod's runtime
/// settings again. One that throws is logged and the others are still told. Engine API, not in the original.
/// </summary>
public interface IWorldReady : ITickSystem
{
    /// <summary>
    /// The world is ready; <paramref name="fromSave"/>: it came from a save (an import too), else a new game.
    /// </summary>
    void Ready(World world, bool fromSave);
}

/// <summary>
/// How a world came to be (<see cref="World.Start"/>).
/// </summary>
public enum WorldStartKind
{
    /// <summary>
    /// Not told: a world the game host did not start (a test, a tool), or before <see cref="World.TellReady(WorldStart)"/>.
    /// </summary>
    Unknown,

    /// <summary>
    /// A new game.
    /// </summary>
    NewGame,

    /// <summary>
    /// A save loaded (the menu's Load, the quickload, Continue, <c>--load</c>).
    /// </summary>
    Load,

    /// <summary>
    /// A save imported into a new world (docs/formats/save.md "Importing a game").
    /// </summary>
    Import,
}

/// <summary>
/// How a world came to be and the save folder it came from (docs/modding.md "World ready"): read in <see cref="IWorldReady.Ready"/>, so a mod
/// finds a file of its own (or the original mod's) beside the save. <paramref name="SaveFolder"/> is the folder's full path, null for a new game
/// or an unknown start. Not saved, not hashed: the simulation never reads it.
/// </summary>
public readonly record struct WorldStart(WorldStartKind Kind, string? SaveFolder = null)
{
    /// <summary>
    /// Whether the world came from a save (loaded or imported).
    /// </summary>
    public bool FromSave => Kind is WorldStartKind.Load or WorldStartKind.Import;
}
