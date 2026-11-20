using Meitou.Data.Fcs;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The running package's own keys on its PLATOON record, after the blackboard's (docs/formats/save.md "AI state"; vfuncs <c>+0x20</c> / <c>+0x28</c>
/// of the package, <b>Verified</b>): <c>Package_OccupyTown</c> writes the bool <c>persist</c> (<c>FUN_140295f90</c> / <c>FUN_140296070</c>: its
/// start made a platoon that was not persistent so, and its end makes it not persistent again); <c>Package_PassingByTownAssault</c> the bool
/// <c>done</c> (spent: it never starts again), the float <c>started</c> (the game hour it started) and the string <c>enemy</c> (the faction it
/// attacks, by its id; written only with one) (<c>FUN_1402963a0</c> / <c>FUN_1402965a0</c>). The load starts the package and then puts these over
/// what its start set.
/// </summary>
public sealed record PackageSave(bool Persist, bool Done, float Started, string? Enemy)
{
    /// <summary>
    /// The keys: the occupation's <c>persist</c>, the passing-by assault's <c>done</c>, <c>started</c> and <c>enemy</c>.
    /// </summary>
    const string PersistKey = "persist", DoneKey = "done", StartedKey = "started", EnemyKey = "enemy";

    /// <summary>
    /// Writes the keys of the package <paramref name="board"/> runs, and removes those of a class it does not run (the record may hold an older
    /// package's). <paramref name="madePersistent"/>: only the occupation makes the platoon persistent (a roaming one not persistent on its own,
    /// <see cref="OwnPersistent"/>; a town's platoon is so already, so its occupation's <c>persist</c> is false).
    /// </summary>
    public static void Write(FcsRecord platoon, Blackboard? board, PopulationData data, bool madePersistent)
    {
        var kind = board?.Current is null ? (PackageClass?)null : board.CurrentClass;
        if (kind == PackageClass.OccupyTown) platoon.Bools[PersistKey] = madePersistent;
        else platoon.Bools.Remove(PersistKey);

        if (kind != PackageClass.PassingAssault)
        {
            platoon.Bools.Remove(DoneKey);
            platoon.Floats.Remove(StartedKey);
            platoon.Strings.Remove(EnemyKey);
            return;
        }

        platoon.Bools[DoneKey] = board!.AssaultDone;
        platoon.Floats[StartedKey] = (float)board.AssaultStart;
        if (data.FactionId(board.AssaultEnemy) is { } enemy) platoon.Strings[EnemyKey] = enemy;
        else platoon.Strings.Remove(EnemyKey);
    }

    /// <summary>
    /// Whether the platoon is persistent on its own: its flag, or its save's <c>persistent</c> that no occupation set (one with <c>persist</c> is
    /// the occupation's, which its end takes off again, <c>FUN_140296070</c>).
    /// </summary>
    public static bool OwnPersistent(Platoon p)
    {
        if (p.Persistent) return true;
        if (p.Saved?.Source.Record is not { } record) return false;

        return record.Bools.GetValueOrDefault("persistent") && !record.Bools.GetValueOrDefault(PersistKey);
    }

    /// <summary>
    /// The keys the record holds; null when it holds none of them.
    /// </summary>
    public static PackageSave? Read(FcsRecord platoon)
    {
        bool any = platoon.Bools.ContainsKey(PersistKey) || platoon.Bools.ContainsKey(DoneKey) || platoon.Floats.ContainsKey(StartedKey);
        if (!any) return null;

        string? enemy = platoon.Strings.GetValueOrDefault(EnemyKey) is { Length: > 0 } id ? id : null;
        return new PackageSave(platoon.Bools.GetValueOrDefault(PersistKey), platoon.Bools.GetValueOrDefault(DoneKey),
            platoon.Floats.GetValueOrDefault(StartedKey), enemy);
    }

    /// <summary>
    /// Puts the passing-by assault's keys over what its start set (<c>FUN_1402965a0</c>); an enemy the save names that the data lacks leaves the
    /// start's. The occupation's <c>persist</c> is not kept: Meitou makes a roaming platoon persistent while it occupies, and not after.
    /// </summary>
    public void ApplyAssault(Blackboard board, PopulationData data)
    {
        board.AssaultDone = Done;
        board.AssaultStart = Started;
        if (Enemy is not null && data.FactionIndex(Enemy) is >= 0 and var enemy) board.AssaultEnemy = enemy;
    }
}
