using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The squad's "house locked" flag (the original's Blackboard <c>+0xe8</c>, docs/game/locks.md "Doors"), on its PLATOON record as the bool
/// <c>homelok</c> (written by <c>FUN_14026c2d0</c>, read by <c>FUN_140272c20</c>, <b>Verified</b>, docs/formats/save.md "Platoon blackboard").
/// Meitou keeps the flag on the squad's home (<see cref="LockSystem.IsHouseLocked"/>, saved with the building as
/// <see cref="LockSystem.HouseLockedKey"/>): the home its save names (<see cref="Squad.HomeBuilding"/>); a squad without one keeps the record's.
/// </summary>
public static class HomeLockSave
{
    /// <summary>
    /// The key.
    /// </summary>
    const string Key = "homelok";

    /// <summary>
    /// Writes the flag of the squad's home.
    /// </summary>
    public static void Write(FcsRecord platoon, World world, Squad squad) => Write(platoon, world, squad.HomeBuilding?.Key);

    /// <summary>
    /// Writes the flag of the home of a save's platoon that is out of the world: the building its <c>homebuilding</c> names, as its squad was given
    /// (<see cref="SavedCharacters.SavedHome"/>).
    /// </summary>
    public static void Write(FcsRecord platoon, World world, PopulationData data, SavedPlatoon sp, Platoon p)
    {
        if (!sp.Pinned || p.Origin < 0 || p.Origin >= data.Sites.Count) return;

        Write(platoon, world, SavedCharacters.SavedHome(world, sp, data.Sites[p.Origin])?.Key);
    }

    /// <summary>
    /// Sets the flag on the home of a squad made from <paramref name="platoon"/> when the record holds it set (serial: the squad is being made).
    /// </summary>
    public static void Read(FcsRecord platoon, World world, Squad squad)
    {
        if (!platoon.Bools.GetValueOrDefault(Key) || squad.HomeBuilding?.Key is not { } home) return;

        world.SystemOf<LockSystem>()?.MarkHouseLocked(home);
    }

    /// <summary>
    /// Writes <paramref name="home"/>'s flag; nothing without a home.
    /// </summary>
    static void Write(FcsRecord platoon, World world, string? home)
    {
        if (home is null || world.SystemOf<LockSystem>() is not { } locks) return;

        platoon.Bools[Key] = locks.IsHouseLocked(world, home);
    }
}
