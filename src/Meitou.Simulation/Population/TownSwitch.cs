namespace Meitou.Simulation.Population;

/// <summary>
/// A town that changed version (docs/modding.md "Town overrides"): the world's tick, the town (a position in the population's site list), the TOWN it
/// was and the one it is now (null: its own record), the unloaded platoons of the old version left homeless (unless one moves them with
/// <see cref="PopulationSystem.RehomePlatoon"/>, they go as soon as the observers have heard, or with <see cref="PopulationSettings.OffscreenOverrides"/>
/// off are refused at their activation) and whether it changed off screen
/// (<see cref="PopulationSettings.OffscreenOverrides"/>, a code mod's call) rather than at its zone's load.
/// </summary>
public readonly record struct TownSwitch(long Tick, int Site, string? From, string? To, IReadOnlyList<int> Homeless, bool Offscreen);

/// <summary>
/// A system that hears of every town that changes version, by its <c>override town</c> list or a code mod's call (docs/modding.md "Town
/// overrides"). Called serially on the tick thread, in system order, once the town has changed and before its old platoons go; it may read the world,
/// change its own state and move those platoons to other towns (<see cref="PopulationSystem.RehomePlatoon"/>): the ones still homeless when every
/// observer has heard go. One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface ITownSwitchObserver : ITickSystem
{
    /// <summary>
    /// A town changed version.
    /// </summary>
    void TownSwitched(World world, in TownSwitch change);
}
