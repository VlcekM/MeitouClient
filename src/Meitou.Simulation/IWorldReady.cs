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
