using Meitou.Data.Gameplay;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Perception;

/// <summary>
/// The crime events of the sight pass (<c>FUN_140859680</c>, <b>Verified</b>, docs/game/dialogue.md "Sight"), in the original's order, each fired
/// as a cut-in (<see cref="DialogueSystem.CutIn"/>): 45 at an escaped prisoner; then, for a target the looker does not regard as an enemy
/// (slot 125), 36 / 40 at escaping slaves and ex-slaves (<see cref="Slaves"/>), 17 at a trespasser (with the pending crime) or a stranger in a
/// closed town, and 9 at a player character in the looker's closed home. The state they write (the crime, the slave tag, the intruder memory) does
/// not wait for a line to be said.
/// </summary>
static class SightCrimes
{
    /// <summary>
    /// EV_SHOO_FROM_MY_BUILDING, EV_INTRUDER_FOUND, EV_ESCAPED_EX_SLAVE_SPOTTED.
    /// </summary>
    const int ShooFromMyBuilding = 9, IntruderFound = 17, EscapedExSlaveSpotted = 40;

    /// <summary>
    /// The crime events of one reaction; true when one of them ends it (the original returns before the peaceful and fight branches).
    /// </summary>
    public static bool React(World world, DialogueSystem talk, PopulationData data, int w, CharacterCold lc, int t, CharacterCold tc)
    {
        if (EscapedPrisoner(world, data, w, lc, t, tc)) talk.CutIn(world, w, t, ArrestSystem.EscapedPrisonerSpotted);
        if (RegardsEnemy(world, talk, data, w, t, lc, tc)) return false;
        if (Slaves(world, talk, data, w, lc, t, tc)) return true;

        var crimes = world.SystemOf<CrimeSystem>();
        if (OfPlayerFaction(world, tc) && crimes?.Trespasses(world, w, t) == true)
        {
            crimes.SetPending(world, t, CrimeCode.Trespassing, lc.Faction, world.Characters.IdOf(w), CrimeSystem.MinTimer);
            Intruder(world, talk, w, t, lc);
            return true;
        }
        if (TownIntruder(world, data, w, lc, t, tc))
        {
            Intruder(world, talk, w, t, lc);
            return true;
        }
        if (!HomeIntruder(world, talk, w, lc, t, tc)) return false;

        talk.CutIn(world, w, t, ShooFromMyBuilding);
        return true;
    }

    /// <summary>
    /// Event 17 INTRUDER_FOUND, then the looker's squad remembers the target with the long-term tag 1 MY_INTRUDER (<c>FUN_140673a20</c>).
    /// </summary>
    static void Intruder(World world, DialogueSystem talk, int w, int t, CharacterCold lc)
    {
        talk.CutIn(world, w, t, IntruderFound);
        if (lc.SquadId >= 0) talk.Memory.Remember(world, lc.SquadId, world.Characters.IdOf(t), MemoryTag.Intruder);
    }

    /// <summary>
    /// Event 45's test: an escaped prisoner that shows (<see cref="ArrestSystem.EscapeShows"/>: blown cooldown running or no disguise level) and is
    /// not caged, seen by a looker standing in its own town (ownerships <c>+0x30</c>, <see cref="ArrestSystem.InOwnTown"/>) while the target stands
    /// in a town: of a bandit town's own faction, else police-like (<c>FUN_140599600</c>).
    /// </summary>
    static bool EscapedPrisoner(World world, PopulationData data, int w, CharacterCold lc, int t, CharacterCold tc)
    {
        if (!ArrestSystem.EscapeShows(tc) || tc.IsImprisoned) return false;
        if (world.SystemOf<ArrestSystem>() is not { } arrest || !arrest.InOwnTown(world, w)) return false;
        if (TownOf(world, data, t, tc) is not { IsNone: false } town) return false;

        int owner = town.Site >= 0 && town.Site < data.SiteFactions.Count ? data.SiteFactions[town.Site] : world.Player.Faction;
        bool banditTown = data.Relations.Contains(owner) && data.Relations.Factions[owner].FundamentalType == FundamentalType.Bandit;
        return banditTown && lc.Faction == owner || arrest.Crimes.IsPoliceLike(world, lc);
    }

    /// <summary>
    /// The town the character stands in for its status (<c>+0x88</c>, <see cref="CharacterCold.StatusTown"/>), else the one its position is in.
    /// </summary>
    static CampaignTown TownOf(World world, PopulationData data, int slot, CharacterCold c) =>
        !c.StatusTown.IsNone ? c.StatusTown : Ai.AiSystem.TownAt(data, world, world.Characters.Next[slot].Position);

    /// <summary>
    /// Slot 125 (<c>isEnemy</c>, <c>FUN_14079c950</c>): the squad memory's TEMP_ENEMY / TEMP_ALLY tags first, else another faction hostile to it.
    /// </summary>
    static bool RegardsEnemy(World world, DialogueSystem talk, PopulationData data, int w, int t, CharacterCold lc, CharacterCold tc)
    {
        if (lc.Faction == tc.Faction) return false;
        if (talk.EnemyMemory(world, w, t) is var memory and not 0) return memory > 0;

        var relations = data.Relations;
        return relations.Contains(lc.Faction) && relations.Contains(tc.Faction)
            && Aggro.AggroSystem.IsEnemy(relations, lc.Faction, tc.Faction);
    }

    /// <summary>
    /// The slave events, for a looker that is no animal, of a faction that is not the player's and not <c>anti slavery</c> (faction <c>+8</c>), at
    /// a target not caged, seen with a spot value above <see cref="EscapingSlave.SpotValue"/>: a slave (state 1) owned by the looker's faction or by
    /// none that escapes (<see cref="ArrestSystem.Escapes"/>) gets 36 and is tagged escaping (state 2, blown 250 s); an escaping one still blown gets
    /// 36 and, owned by the looker's faction or none, the tag again; both end the reaction. An ex-slave (state 3) still blown gets 40.
    /// </summary>
    static bool Slaves(World world, DialogueSystem talk, PopulationData data, int w, CharacterCold lc, int t, CharacterCold tc)
    {
        if (SquadRegistry.IsAnimal(lc) || lc.Faction == world.Player.Faction || tc.IsImprisoned) return false;
        if (!data.Relations.Contains(lc.Faction) || data.Factions[lc.Faction].AntiSlavery) return false;
        if (world.SystemOf<ArrestSystem>() is not { } arrest) return false;

        bool ownOrNone = tc.SlaveOwner < 0 || tc.SlaveOwner == lc.Faction;
        bool escapes = tc.SlaveState == 1 && ownOrNone && arrest.Escapes(world, w, t, tc, world.Characters.Next[t].Position);
        if (escapes && Seen())
        {
            talk.CutIn(world, w, t, ArrestSystem.EscapingSlaveSpotted);
            FixtureRegistry.TagEscaped(world, tc);
            return true;
        }
        if (tc.SlaveState == 2 && tc.RecentlyWanted > 0 && Seen())
        {
            talk.CutIn(world, w, t, ArrestSystem.EscapingSlaveSpotted);
            if (ownOrNone) FixtureRegistry.TagEscaped(world, tc);
            return true;
        }
        if (tc.SlaveState == 3 && tc.RecentlyWanted > 0 && Seen()) talk.CutIn(world, w, t, EscapedExSlaveSpotted);
        return false;

        bool Seen() => arrest.SpotValue(world, w, t) > EscapingSlave.SpotValue;
    }

    /// <summary>
    /// <c>FUN_140852240</c>: a target standing in the looker's own town (ownerships <c>+0x30</c> against the target's status town <c>+0x88</c>)
    /// inside its walls (<c>amInsideTownWalls</c>, the gate code not 0; unknown counts), when that town is not public (<c>Town::isPublic</c>,
    /// TOWN <c>is public</c>), the target is no slave (state 1), does not look like one to the looker (<c>FUN_1405a6bb0</c>) and is not caged. Any
    /// target, not only the player's.
    /// </summary>
    static bool TownIntruder(World world, PopulationData data, int w, CharacterCold lc, int t, CharacterCold tc)
    {
        if (lc.SquadId < 0 || world.Squads.Find(lc.SquadId) is not { TownOwn: true, Town: >= 0 } squad) return false;
        if (TownOf(world, data, t, tc).Site != squad.Town || squad.Town >= data.Sites.Count || data.Sites[squad.Town].Town.IsPublic) return false;
        if (world.SystemOf<LockSystem>()?.Regions?.CodeAt(world.Characters.Next[t].Position) is 0) return false;

        bool looksSlave = world.SystemOf<Kidnap.CaptiveSystem>()?.LooksLikeASlave(lc, tc) == true;
        return tc.SlaveState != 1 && !looksSlave && !tc.IsImprisoned;
    }

    /// <summary>
    /// <c>FUN_1408525c0</c>: a player character standing in the looker's home building (ownerships <c>+0x38</c>), not chained to the looker, not
    /// regarded as friendly, no animal and in no bed or cage (<c>+0x2f8</c> 0), when the home is not public (Building vfunc <c>0x290</c>).
    /// </summary>
    static bool HomeIntruder(World world, DialogueSystem talk, int w, CharacterCold lc, int t, CharacterCold tc)
    {
        if (lc.SquadId < 0 || world.Squads.Find(lc.SquadId) is not { } squad || CrimeSystem.HomeKey(squad) is not { } home) return false;
        if (world.Sight.BuildingOf(t) != home || CrimeSystem.ChainedTo(world, world.Characters.IdOf(w), tc)) return false;
        if (talk.RegardsFriendly(world, w, t) || SquadRegistry.IsAnimal(tc) || tc.InSomething != HeldState.None) return false;

        return OfPlayerFaction(world, tc) && !CrimeSystem.HomePublic(world, squad, home);
    }

    /// <summary>
    /// Of the player's faction (the faction's <c>+0x250</c>).
    /// </summary>
    static bool OfPlayerFaction(World world, CharacterCold c) => world.Player.Faction >= 0 && c.Faction == world.Player.Faction;
}
