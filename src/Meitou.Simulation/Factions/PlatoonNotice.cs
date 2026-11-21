namespace Meitou.Simulation.Factions;

/// <summary>
/// What happened to a platoon (<see cref="PlatoonNotice"/>).
/// </summary>
public enum PlatoonEvent : byte
{
    /// <summary>
    /// It entered the world's platoons: made by a town's pool, a campaign, a split or a code mod, a town's squads kept on as stand-ins when the town
    /// leaves the world, or a town's squad kept loaded (not the platoons a save brings, which are the world's start).
    /// </summary>
    Added,

    /// <summary>
    /// It left the world's platoons for good (<see cref="PlatoonNotice.Gone"/> says why); the platoon object is no longer in
    /// <see cref="World.Platoons"/>. A loaded platoon removed is told only this, with no <see cref="Unloaded"/> first.
    /// </summary>
    Removed,

    /// <summary>
    /// Its characters were made in the world.
    /// </summary>
    Loaded,

    /// <summary>
    /// Its characters left the world; it goes on as a stand-in.
    /// </summary>
    Unloaded,

    /// <summary>
    /// A held platoon's stand-in reached the goal its mod sent it to (<c>PopulationSystem.SendPlatoon</c>): within the roam arrival radius of the
    /// point, or of the route goal toward the followed character (which follows it within 200, so the character may be that much further). Told once
    /// per sending, off screen only.
    /// </summary>
    Arrived,
}

/// <summary>
/// Why a platoon left the world (<see cref="PlatoonEvent.Removed"/>).
/// </summary>
public enum PlatoonGone : byte
{
    /// <summary>
    /// Not removed (every other event).
    /// </summary>
    None,

    /// <summary>
    /// The engine let it go: a platoon the game does not keep leaving the world, its orphan timer running out, a stand-in of a town version
    /// that changed, or a cancelled campaign start's new squads.
    /// </summary>
    Despawned,

    /// <summary>
    /// It lost an off-screen fight (<see cref="PlatoonNotice.By"/> is the winner).
    /// </summary>
    LostFight,

    /// <summary>
    /// It had nobody left: all dead, removed or split off, or nobody to make when it came in.
    /// </summary>
    NoMembers,

    /// <summary>
    /// A save's stand-in a town replaced with residents of its own.
    /// </summary>
    Dropped,

    /// <summary>
    /// A code mod despawned it (<c>PopulationSystem.DespawnPlatoon</c>).
    /// </summary>
    ByMod,

    /// <summary>
    /// Removed by a caller that named no reason (<see cref="PlatoonRegistry.Remove(int)"/>).
    /// </summary>
    Other,
}

/// <summary>
/// A change to a platoon (docs/modding.md "Platoon notices"): the tick it was told on, what happened, the platoon (to read, not change: the
/// population's API changes it), why it left the world, and for <see cref="PlatoonGone.LostFight"/> the id of the platoon that beat it (else -1).
/// </summary>
public readonly record struct PlatoonNotice(long Tick, PlatoonEvent Kind, Platoon Platoon, PlatoonGone Gone, int By);

/// <summary>
/// A change noted by the registry and not yet told (<see cref="PlatoonNotice"/> without its tick).
/// </summary>
internal readonly record struct PlatoonChange(PlatoonEvent Kind, Platoon Platoon, PlatoonGone Gone, int By);

/// <summary>
/// A system told of the platoons' changes (docs/modding.md "Platoon notices"): every platoon added, removed (with why), loaded, unloaded, and a
/// held platoon's arrival. Called serially on the tick thread once per tick after the slow world, in system order, with the tick's changes in the
/// order they happened, then the loads and unloads in id order. It may read the world, change its own state and call the population's serial
/// engine API (spawn, hold, send, despawn); what that changes is told the next tick. One that throws is disabled for that world, and the fault
/// enters the state hash. Engine API, not in the original.
/// </summary>
public interface IPlatoonObserver : ITickSystem
{
    /// <summary>
    /// A platoon changed.
    /// </summary>
    void PlatoonChanged(World world, in PlatoonNotice notice);
}
