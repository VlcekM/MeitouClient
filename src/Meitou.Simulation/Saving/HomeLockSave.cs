using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The squad's "house locked" flag (the original's Blackboard <c>+0xe8</c>, docs/game/locks.md "Doors"), on its PLATOON record as the bool
/// <c>homelok</c> (written by <c>FUN_14026c2d0</c>, read by <c>FUN_140272c20</c>, <b>Verified</b>, docs/formats/save.md "Platoon blackboard").
/// Meitou keeps the flag on the squad's home (<see cref="LockSystem.IsHouseLocked"/>, saved with the building as
/// <see cref="LockSystem.HouseLockedKey"/>).
/// </summary>
public static class HomeLockSave
{
    /// <summary>
    /// The key.
    /// </summary>
    const string Key = "homelok";

    /// <summary>
    /// Writes the flag of the squad's home; a squad without a home keeps the record's.
    /// </summary>
    public static void Write(FcsRecord platoon, World world, Squad squad)
    {
        if (Home(squad) is not { } home || world.SystemOf<LockSystem>() is not { } locks) return;

        platoon.Bools[Key] = locks.IsHouseLocked(world, home);
    }

    /// <summary>
    /// Sets the flag on the home of a squad made from <paramref name="platoon"/> when the record holds it set (serial: the squad is being made).
    /// </summary>
    public static void Read(FcsRecord platoon, World world, Squad squad)
    {
        if (!platoon.Bools.GetValueOrDefault(Key) || Home(squad) is not { } home) return;

        world.SystemOf<LockSystem>()?.MarkHouseLocked(home);
    }

    /// <summary>
    /// The building id of the squad's home (its blackboard's own, else its assigned one); null without one.
    /// </summary>
    static string? Home(Squad squad) => (squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key;
}
