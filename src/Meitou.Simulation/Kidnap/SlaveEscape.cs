using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// A slave runs once its masters are beaten or gone (the AI update <c>FUN_1405a4ae0</c>, <b>Verified</b>; docs/game/carry-loot.md "A slave whose
/// masters are beaten escapes"). Serial, in slot order, on the spotting cadence of <see cref="Crime.ArrestSystem"/> (engine choice: the original
/// runs it in every AI update).
/// </summary>
static class SlaveEscape
{
    /// <summary>
    /// How long a slave's owner may be out of the world before the slave escapes (<c>DAT_1416a1b84</c>, 45 s of the original's wall-clock
    /// stopwatch; here simulation seconds, engine choice).
    /// </summary>
    const float MasterLostSeconds = 45;

    /// <summary>
    /// EV_SLAVE_ESCAPE_OPPORTUNITY_SAVIOR (54), said at the owner's defeater, and EV_SLAVE_ESCAPE_OPPORTUNITY_ALONE (55), said at nobody.
    /// </summary>
    const int WithSavior = 54, Alone = 55;

    /// <summary>
    /// Each slave (state 1) not caged and not down (<see cref="CharacterFacts.Down"/>) whose master squad is known. A slave with none is the far
    /// rule's (<see cref="Crime.ArrestSystem"/>, which frees an ownerless one at once), and so is one whose saved owner is still to load.
    /// </summary>
    public static void Step(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { SlaveState: 1, SlaveMaster: >= 0, IsImprisoned: false } c) continue;
            if (CharacterFacts.Down(c) || c.Save is { Slaver.IsNull: false }) continue;

            Check(world, s, c);
        }
    }

    /// <summary>
    /// The master squad out of the world starts the stamp (<see cref="MasterGone"/>). Otherwise the stamp stops; every member of the squad beaten
    /// (<see cref="SquadIntact.AllBeaten"/>) frees the slave with its savior; else the owner (Meitou's stand-in: the squad's leader,
    /// <see cref="ObedientSlave.OwnerOf"/>) down or gone with no member standing to take its place (<see cref="Stands"/>) frees it alone. A leader
    /// gone from a squad still here is read as down: the original's owner was handed to a standing member when it went down.
    /// </summary>
    static void Check(World world, int s, CharacterCold c)
    {
        var table = world.Characters;
        if (world.Squads.Find(c.SlaveMaster) is not { } master)
        {
            MasterGone(world, s, c);
            return;
        }

        c.MasterLostTick = -1;
        bool owner = table.TryResolveNext(master.Leader, out int o);
        if (SquadIntact.AllBeaten(table, master))
        {
            Escape(world, s, c, owner ? Savior(world, s, o) : CharacterId.None);
            return;
        }

        if ((owner && !CharacterFacts.Down(table.Cold(o)!)) || Stands(world, master)) return;

        Escape(world, s, c, CharacterId.None);
    }

    /// <summary>
    /// The master squad is not in the world: the first pass stamps the time, a later one more than <see cref="MasterLostSeconds"/> on frees the slave alone.
    /// The stamp is not cleared by the escape, as the original's (AI <c>+0x2f0</c>) is not.
    /// </summary>
    static void MasterGone(World world, int s, CharacterCold c)
    {
        if (c.MasterLostTick < 0)
        {
            c.MasterLostTick = world.Tick;
            return;
        }

        if ((world.Tick - c.MasterLostTick) * world.TickSeconds > MasterLostSeconds) Escape(world, s, c, CharacterId.None);
    }

    /// <summary>
    /// Whether the squad's effective leader (<c>FUN_140792b90</c>) is not down. The original then hands the slave's owner handle to that member
    /// (<c>FUN_14032e430</c>); Meitou's owner stays the master squad, so nothing changes. A world without a population (no active zones) takes the
    /// first member not down.
    /// </summary>
    static bool Stands(World world, Squad master)
    {
        var table = world.Characters;
        if (world.SystemOf<PopulationSystem>() is { } population) return !SquadIntact.Down(table, population.EffectiveLeader(world, master));

        return master.Members.Exists(m => !SquadIntact.Down(table, m));
    }

    /// <summary>
    /// The savior of a slave whose masters are all beaten: the owner's defeater (<see cref="CharacterCold.DefeatedBy"/>, Character <c>+0x2c0</c>)
    /// when it resolves, its faction is real and the slave knows it (<c>FUN_140855960</c> on the slave's senses); none otherwise.
    /// </summary>
    static CharacterId Savior(World world, int s, int owner)
    {
        var table = world.Characters;
        var by = table.Cold(owner)!.DefeatedBy;
        if (!table.TryResolveNext(by, out int b)) return CharacterId.None;
        if (world.SystemOf<Crime.CrimeSystem>()?.Factions.NotReal(table.Cold(b)!.Faction) == true) return CharacterId.None;

        return CharacterFacts.Knows(world, s, by) ? by : CharacterId.None;
    }

    /// <summary>
    /// The escape: slave state 2 through the setter (<see cref="FixtureRegistry.TagEscaped"/>: job 187 gone, wanted for 250 s, the hour stamped),
    /// then event 54 at the savior or 55 at nobody through the cut-in sender (<c>FUN_140685660</c>), whose lines give the slave its escaping package.
    /// </summary>
    static void Escape(World world, int s, CharacterCold c, CharacterId savior)
    {
        FixtureRegistry.TagEscaped(world, c);
        world.SystemOf<Dialogue.DialogueSystem>()?.CutIn(world, s, savior, savior.IsNone ? Alone : WithSavior);
    }
}
