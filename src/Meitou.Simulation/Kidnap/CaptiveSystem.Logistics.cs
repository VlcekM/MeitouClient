using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The slavers' logistics (docs/simulation.md "Captives"; facts in docs/game/ai-tasks.md "Slave rows" and docs/game/ai.md "Prisoner and slaver
/// packages"):
/// <list type="bullet">
/// <item><b>Gather</b> (173 "Gathering slaves", 245 "Gathering unholy slaves", wanted while the squad has at most 3 slaves of its own up and uncaged,
/// AI slot 63 <c>FUN_14059b940</c>;
/// 217 "Gathering slaves for shipping", wanted in a town or outpost, AI slot 64 <c>FUN_140597b80</c>; <b>Observed</b>): the nearest occupied cage of
/// the town the gatherer stands in (173, 245:
/// of its faction; 217: of any faction but the player's; 245 only a woman or one of another race than the gatherer, <c>FUN_1408d1410</c>). The
/// gatherer walks there and takes the captive
/// (<c>Task_GetSlave</c>, 174, whose effect is TARGET_IS_MY_SLAVE): its queued message is replayed as <c>setChainedMode</c> only
/// (<c>FUN_1407b65b0</c> kind 5 -> <c>FUN_14032e590</c>, <b>Verified</b>): the captive becomes the gatherer's faction's slave, owned by the
/// gatherer, and stays in its own squad, planning with the owner squad's Slave AI Goals (job 187, <see cref="Ai.AiSystem.ObeyedRoles"/>). The
/// cage is opened (<b>engine reading</b>: which task does it in the original is <b>Unknown</b>). A player character is gathered too.</item>
/// <item><b>Deliver</b> (192 "Delivering slaves", wanted at 0.25 per slave, AI slot 65): the leader walks to the shipping package's town
/// (<see cref="Blackboard.MissionTown"/>). <b>Sell</b> (194 / 218 "Selling slaves", 218 only in a prison or slave camp, AI slot 66): in the town it
/// stands in it says event 39 SLAVE_DELIVERY to the keeper of a slave pen there, whose line DA 24 hands its slaves over (<see cref="GiveSlaves"/>).</item>
/// <item><b>Get in the cage</b> (172 "Getting in my cage", a Slave AI Goal; <c>FUN_1408caaf0</c>, <b>Observed</b>): a slave not held walks to the
/// nearest free cage of its owner's faction in the 3x3 zone cells round it, in any town, and gets in (<c>Task_UseCage</c>).</item>
/// </list>
/// </summary>
public sealed partial class CaptiveSystem
{
    /// <summary>
    /// A squad's slave list (the original's platoon ownerships, <c>platoon vfunc 0x98 +0x10</c>, docs/game/ai-tasks.md "Pass 3, owned slaves"): its
    /// live slave members in slave state 1, then the live slaves of other squads that obey it (<see cref="CharacterCold.SlaveMaster"/>), as a town's
    /// slave squads obey their workmasters.
    /// </summary>
    public static IEnumerable<int> SlavesOf(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
        {
            if (table.TryResolveNext(m, out int s) && table.Cold(s) is { SlaveState: 1, Role: (int)SquadRole.Slave, Medical.Dead: false })
                yield return s;
        }

        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { SlaveState: 1, Medical.Dead: false } c) continue;
            if (c.SlaveMaster == squad.Id && c.SquadId != squad.Id) yield return s;
        }
    }

    /// <summary>
    /// The town whose radius holds <paramref name="at"/> (the nearest such centre), -1: none.
    /// </summary>
    public static int TownAt(PopulationData data, Vector3 at)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var site in data.Sites)
        {
            float r = site.Town.Radius, d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(site.Position.X, site.Position.Z));
            if (r > 0 && d <= r * r && d < bestD) (best, bestD) = (site.Index, d);
        }

        return best;
    }

    /// <summary>
    /// The finder of 172 Getting in my cage (<c>FUN_1408caaf0</c>, <b>Verified</b>): for a character with an owner, whatever its slave state (the
    /// finder tests the owner handle only, so an escaped or ex-slave that keeps its owner finds a cage too), not held in anything, the nearest free
    /// cage of the owner's faction in the 3x3 zone cells round it, whatever the town (the finder's town argument is 0), that no job heads for; null:
    /// none.
    /// </summary>
    internal Fixture? OwnCage(World world, int slot, CharacterCold cold)
    {
        if (cold.SlaveState == 0 || cold.SlaveOwner < 0 || cold.InSomething != HeldState.None) return null;

        var table = world.Characters;
        var at = table.Next[slot].Position;
        Fixture? best = null;
        float bestD = float.MaxValue;
        foreach (var f in NearbyCages(world, -1, at, cold.SlaveOwner))
        {
            if (!FixtureRegistry.IsFree(table, f) || Taken(f.Id)) continue;
            float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
            if (d < bestD) (best, bestD) = (f, d);
        }

        return best;
    }

    /// <summary>
    /// The desire of 217 (AI slot 64, <c>FUN_140597b80</c>): the gatherer stands in a town or outpost, and when its owner is the player's faction or
    /// an anti-slavery one, is an ally of it.
    /// </summary>
    internal bool ExportTown(World world, CharacterCold cold, int site)
    {
        if (site < 0 || data.Sites[site].Town.Type is not (TownType.Town or TownType.Outpost)) return false;
        int owner = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        return owner < 0 || !(owner == world.Player.Faction || owner < data.Factions.Count && data.Factions[owner].AntiSlavery)
                || Allied(cold.Faction, owner);
    }

    /// <summary>
    /// The cages 173 and 245 search (<c>FUN_1409fc450</c> with the town the gatherer stands in, <b>Verified</b>): those of the gatherer's faction in
    /// the 3x3 zone cells round it, of that town when it is in one, of any town when not (the finder's town is only a filter, null passes
    /// everything). Only fixtures of towns already registered exist.
    /// </summary>
    internal IEnumerable<Fixture> NearbyCages(World world, int site, Vector3 at, int faction)
    {
        IEnumerable<Fixture> all = site >= 0 ? CagesOf(world, site) : world.Fixtures.All.Where(f => f.Kind == FixtureKind.Cage);
        foreach (var f in all)
        {
            if (f.Owner == faction && WorldLayout.WithinOneZone(at.X, at.Z, f.Position.X, f.Position.Z)) yield return f;
        }
    }

    /// <summary>
    /// The cage a gatherer takes a captive from (see the class remarks): 173 and 217 the nearest (<c>BuildingFinder</c> slot 1), 245 the first in the
    /// finder's order whose occupant is a woman or of another race (<c>FUN_1408d1410</c>, <b>Verified</b>: no distance sort, and no test of the
    /// occupant being a player character, down or busy).
    /// </summary>
    internal Fixture? GatherTarget(World world, int slot, CharacterCold cold, IEnumerable<Fixture> cages, TaskType type)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        Fixture? best = null;
        float bestD = float.MaxValue;
        foreach (var f in cages)
        {
            if (f.Owner == world.Player.Faction && world.Player.Faction >= 0 || !GatherCage(world, slot, cold, f, type, out var c))
                continue;

            if (type == TaskType.GatherSlavesFromCagesIfFemaleOrBeast) return f;
            float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
            if (d < bestD) (best, bestD) = (f, d);
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="f"/> and its occupant pass the finder's tests of <paramref name="type"/>.
    /// </summary>
    bool GatherCage(World world, int slot, CharacterCold cold, Fixture f, TaskType type, out CharacterCold c)
    {
        var table = world.Characters;
        c = cold;
        bool export = type == TaskType.GatherSlavesFromCagesIfItsAnExportTown, unholy = type == TaskType.GatherSlavesFromCagesIfFemaleOrBeast;
        var goals = export ? SquadGoals.Of(world) : null;
        var asker = table.IdOf(slot);

        // 173 claims nothing (FUN_1408db510 with the finder limit 0x100): gatherers of any squad may head for the same cage, the first lifts.
        if (export ? goals!.Working(cold.SquadId, asker, GoalTarget.At(f.Id)) > 0 : unholy && Taken(f.Id)) return false;
        if (!export && f.Owner != cold.Faction || OccupantSlot(world, f) is not (>= 0 and var s)) return false;
        c = table.Cold(s)!;
        if (Spared(cold, c)) return false;

        // 217 (FUN_1408cc600): dropped for a down or crippled occupant, or one two squad mates already work.
        if (export)
        {
            return !(c.IsPlayer || c.Medical is { Incapacitated: true } || CombatSystem.IsCrippled(c)
                || goals!.Working(cold.SquadId, asker, GoalTarget.Of(table.IdOf(s))) >= 2);
        }

        if (unholy) return c.Appearance is { Female: true } || c.Race?.StringId != cold.Race?.StringId;
        return c.Medical is not { Incapacitated: true };
    }

    /// <summary>
    /// The cage and the captive of every job, in the squad's goal list the finders ask (<see cref="SquadGoals"/>).
    /// </summary>
    void IGoalSource.Publish(World world, SquadGoals goals)
    {
        foreach (var j in _jobs)
        {
            if (!world.Characters.TryResolveNext(j.Worker, out int w) || world.Characters.Cold(w) is not { } c)
                continue;

            goals.Add(c.SquadId, j.Worker, GoalTarget.At(j.Cage));
            goals.Add(c.SquadId, j.Worker, GoalTarget.Of(j.Captive));
        }
    }

    /// <summary>
    /// One tick of a logistics job; false when it is over.
    /// </summary>
    bool StepLogistics(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        switch (j.Kind)
        {
            case CaptiveJobKind.Gather:
                {
                    var cage = world.Fixtures.Find(j.Cage);
                    if (cage is null || !table.TryResolveNext(j.Captive, out int c)
                            || table.Cold(c) is not { IsImprisoned: true, Medical.Dead: false } cc || cc.InWhat != cage.Id)
                        return End(world, j, CaptiveEventKind.GaveUp);

                    if (!Walk(world, j, w, cage, out bool arrived)) return End(world, j, CaptiveEventKind.GaveUp);
                    if (!arrived) return true;
                    TaskStart.Stand(table, w);
                    if (world.Squads.Find(wc.SquadId) is not { } squad) return End(world, j, CaptiveEventKind.GaveUp);
                    if (cage.Lock is { } l) l.Locked = false;
                    world.Fixtures.LiftOut(world, j.Captive, w, stepOut: true);
                    cc.Escaped = false;
                    FixtureRegistry.Enslave(world, cc, wc.Faction, squad.Id);
                    cc.Kidnapped = false;
                    Note(world, CaptiveEventKind.Gathered, j);
                    return End(world, j, null);
                }
            case CaptiveJobKind.CageSelf:
                {
                    var cage = world.Fixtures.Find(j.Cage);
                    if (cage is null || !FixtureRegistry.IsFree(table, cage) || wc.InSomething != HeldState.None)
                        return End(world, j, CaptiveEventKind.GaveUp);

                    if (!Walk(world, j, w, cage, out bool arrived)) return End(world, j, CaptiveEventKind.GaveUp);
                    if (!arrived) return true;
                    TaskStart.Stand(table, w);
                    bool caged = world.Fixtures.Imprison(world, j.Worker, cage);
                    if (caged) Note(world, CaptiveEventKind.Caged, j);
                    return End(world, j, caged ? null : CaptiveEventKind.GaveUp);
                }
            case CaptiveJobKind.Deliver:
                return StepDeliver(world, j, w);
            case CaptiveJobKind.Sell:
                return StepSell(world, j, w, wc);
        }

        return End(world, j, CaptiveEventKind.GaveUp);
    }

    /// <summary>
    /// Goal 192's desire per slave (<c>DAT_141696c80</c>, read from the exe: 0.25).
    /// </summary>
    public const float DeliverDesirePerSlave = 0.25f;

    /// <summary>
    /// Goal 192's desire (AI slot 65, <c>FUN_14059a0a0</c>, <b>Verified</b>): 0.25 per entry of the platoon's slave list; 0 with no slaves. 188 and
    /// 194 share it; 218 has it only in a prison or slave camp (AI slot 66).
    /// </summary>
    public float DeliverDesire(World world, Squad squad) => DeliverDesirePerSlave * SlavesOf(world, squad).Count();

    /// <summary>
    /// 192's act-out, its plan's 53 Travelling (<c>Task_TravelToTargetTown</c>, the only provider of state 36; <see cref="TownTravel.StepGoal"/>): the
    /// leader walks to the squad's town (the shipping package's, <see cref="SquadTown.Of"/>) at the travel functor's pace and is done once state 36
    /// holds, where the goal's plan is its own row alone and 194 / 218 hand the slaves over. A walk with no path to the town is a lost walk (the task
    /// asks a replan and calls the package's path-impossible slot, nothing for the shipping classes); a squad with no town starts its package again.
    /// </summary>
    bool StepDeliver(World world, CaptiveJob j, int w)
    {
        if (world.SystemOf<AiSystem>() is not { } ai || world.Characters.Cold(w)?.Ai is not { Squad: { Ai: { } board } squad })
            return End(world, j, null);

        switch (ai.Travel.StepGoal(ai, world, w, squad, board, RowOf(j.Kind), j.Walks > 0))
        {
            case GoalTravel.Arrived:
                return End(world, j, null);
            case GoalTravel.NoTown:
                ai.RestartPackage(world, squad);
                return End(world, j, CaptiveEventKind.GaveUp);
            case GoalTravel.NoPath:
                ai.PathImpossible(world, squad);
                Lost(j, null);
                return End(world, j, CaptiveEventKind.GaveUp);
        }

        j.Walks = 1;
        return true;
    }

    /// <summary>
    /// The BuildingDesignation of the slave pens: the <c>building designation</c> of the Cage Warden, Slaver Shop and cage guard templates.
    /// </summary>
    const int PenDesignation = 10;

    /// <summary>
    /// The salt of the receiver finder's draw.
    /// </summary>
    const ulong ReceiverSalt = 0x5245_4345_4956_4552;

    /// <summary>
    /// The receiver 194 / 218 hand the slaves to (<c>FUN_1408ca950</c>, <b>Verified</b>; it keeps a target it has, as the look keeps the job's):
    /// <c>BuildingFinderNearestAllies_Designated</c> with mask 0x80000 and designation 10 over the 3 x 3 zone cells round the leader
    /// (<c>FUN_1409fc450</c>) in its status town (any town with none): the houses (function key 19) of designation 10 (slave pens, <see cref="Pens"/>)
    /// not on the leader's ignore list, of its faction or one its squad may use (<c>FUN_1408ca5d0</c>, <see cref="FixtureRegistry.CanUse"/>), one
    /// drawn at random (<c>FUN_1409fb020</c>), then its owner squad's effective leader (<c>FUN_14079aea0</c>). Null: none, and the goal is unwanted.
    /// </summary>
    internal CharacterId? ReceiverHere(World world, int w, CharacterCold cold, out int site)
    {
        var at = world.Characters.Next[w].Position;
        var town = AiSystem.StatusTownAt(data, world, at, cold.StatusTown);
        var pens = Pens(world, w, cold, town);
        site = -1;
        if (pens.Count == 0) return null;

        var roll = Rng.Hash(world.Seed, (ulong)Rng.Key(world.Characters.IdOf(w)) ^ (ulong)world.Tick, RngPurpose.Slow, ReceiverSalt);
        (var owner, site) = pens[Rng.Int(roll, pens.Count)];
        var lead = world.SystemOf<PopulationSystem>()?.EffectiveLeader(world, owner) ?? owner.Leader;
        return world.Characters.IsAlive(lead) && lead != world.Characters.IdOf(w) ? lead : null;
    }

    /// <summary>
    /// The slave pens <see cref="ReceiverHere"/> draws from, each by its owner squad and town: the placed towns' houses
    /// (<see cref="TownWatchFinders.IsHouse"/>) within one zone of the leader whose record's designation is 10, of <paramref name="town"/> (every
    /// placed town when none), not ignored; a pen's owner is the live squad with the lowest id housed in it (engine reading of the building's owner
    /// hand), whose faction is the building's.
    /// </summary>
    List<(Squad Owner, int Site)> Pens(World world, int w, CharacterCold cold, CampaignTown town)
    {
        var at = world.Characters.Next[w].Position;
        var owners = new List<(Squad, int)>();
        foreach (var site in data.Sites)
        {
            if (!town.IsNone && town.Site != site.Index) continue;

            foreach (var b in site.Buildings)
            {
                if (!TownWatchFinders.IsHouse(b) || !WorldLayout.WithinOneZone(at.X, at.Z, b.Position.X, b.Position.Z)) continue;
                if (world.Fixtures.RecordOf(b.Key)?.Designation != PenDesignation || cold.IsIgnored(new IgnoreKey(b.Key, default), world.Tick)) continue;

                if (PenOwner(world, b.Key) is { } owner && MayGiveTo(world, cold, owner, site.Index, b.Key)) owners.Add((owner, site.Index));
            }
        }

        return owners;
    }

    /// <summary>
    /// The live squad with the lowest id whose home is the building <paramref name="key"/>; null: none.
    /// </summary>
    static Squad? PenOwner(World world, string key)
    {
        Squad? owner = null;
        foreach (var s in world.Squads.All)
        {
            if (s.HomeBuilding?.Key != key || owner is not null && owner.Id < s.Id || !s.Members.Exists(world.Characters.IsAlive)) continue;
            owner = s;
        }

        return owner;
    }

    /// <summary>
    /// The pen test of <c>FUN_1408ca5d0</c>: the owner is of the leader's faction, or the leader's squad may use the building
    /// (<c>canIUseThisBuilding</c>, <see cref="FixtureRegistry.CanUse"/>).
    /// </summary>
    static bool MayGiveTo(World world, CharacterCold cold, Squad owner, int site, string key) =>
        owner.Faction == cold.Faction
        || world.Fixtures.CanUse(world, cold, owner.Faction, new FixtureRegistry.UsedBuilding(CampaignTown.OfSite(site), site, key, false, false));

    /// <summary>
    /// 194 / 218's act-out (159 Talking on the receiver, <c>Task_TalktoPlayer</c> with event 39 SLAVE_DELIVERY): the leader walks to within
    /// <see cref="CaptiveSettings.ShoutReach"/> of the receiver and says the event; the slaves change hands only through the line the event's
    /// dialogue says (DA 24, <see cref="GiveSlaves"/>), and the job is done once the leader's dialogue is free again, as the task ends
    /// (docs/game/ai-tasks.md "Shout"). Nothing while it crouches or lies (row 192's !26). With the event lines off (a headless world without
    /// dialogue) the leader hands the slaves over itself, as DA 24 would.
    /// </summary>
    bool StepSell(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        if (j.Phase == CaptivePhase.Work) return SaysDelivery(world, j);
        if (world.Squads.Find(wc.SquadId) is not { } squad || !SlavesOf(world, squad).Any() || !table.TryResolveNext(j.Captive, out int r))
            return End(world, j, CaptiveEventKind.GaveUp);

        if (!AtReceiver(world, j, w, r, out bool gaveUp)) return !gaveUp || End(world, j, CaptiveEventKind.GaveUp);
        if (CharacterFacts.CrouchingOrLying(wc)) return true;

        TaskStart.Stand(table, w);
        if (world.SystemOf<Dialogue.DialogueSystem>() is not { Settings.AmbientEvents: true })
        {
            GiveSlaves(world, squad, table.Cold(r)!);
            return End(world, j, null);
        }

        world.Defer(new Dialogue.DialogueEvent(j.Worker, j.Captive, SlaveDelivery) { Tick = world.Tick + 1 });
        j.Phase = CaptivePhase.Work;
        j.Time = 0;
        j.Blows = 0;
        return true;
    }

    /// <summary>
    /// Whether the leader stands within <see cref="CaptiveSettings.ShoutReach"/> of the receiver at <paramref name="r"/>; else it walks there
    /// (<paramref name="gaveUp"/>: a lost walk, or too many walks).
    /// </summary>
    bool AtReceiver(World world, CaptiveJob j, int w, int r, out bool gaveUp)
    {
        var table = world.Characters;
        var to = new Vector2(table.Next[r].Position.X, table.Next[r].Position.Z);
        gaveUp = false;
        if (Vector2.Distance(new Vector2(table.Next[w].Position.X, table.Next[w].Position.Z), to) <= Settings.ShoutReach) return true;

        if (WalksOwn(table, w, j) && Vector2.Distance(table.Next[w].Goal, to) <= 5) return false;
        if (!WalksOwn(table, w, j) && Lost(j, IgnoreKey.Of(j.Captive)) || ++j.Walks > Settings.Walks)
        {
            gaveUp = true;
            return false;
        }

        TaskStart.Walk(world, w, to, SpeedMode.Walk, RowOf(j.Kind), TaskSource.Goal);
        return false;
    }

    /// <summary>
    /// The talk of a Sell job once event 39 went out: the job holds while the leader's dialogue runs (<c>Task_TalktoPlayer</c> ends when the dialogue
    /// is free) and ends after it, or at once when the event said nothing (the deferred event is in by the job's second step since it went out).
    /// </summary>
    bool SaysDelivery(World world, CaptiveJob j)
    {
        bool speaking = (_talk ??= world.SystemOf<Dialogue.DialogueSystem>())?.Engages(j.Worker) == true;
        if (speaking || ++j.Blows < 2) return true;

        return End(world, j, null);
    }

    /// <summary>
    /// <c>DA_GIVE_TARGET_MY_SLAVES</c> (24, <c>FUN_140680560</c>, <b>Verified</b>): each slave on the speaker squad's slave list (<see cref="SlavesOf"/>)
    /// that is being carried is put down by its carrier first (the carry flag <c>+0x3d4</c> and partner <c>+0x380</c>, then the put-down unlink
    /// <c>FUN_1405ce1e0</c> on the carrier), then gets the target as its owner (<c>FUN_14032e430</c>): it leaves the old owner's squad's slave list
    /// for the target squad's (<see cref="CharacterCold.SlaveMaster"/>), and its owner faction is the target's. Nothing else: the slave keeps its
    /// own squad, its slave state and where it is (no cage), so the receiver's squad's Slave AI Goals take it on (job 187); then the speaker's list is
    /// cleared. The 5 DA 24 lines of the base data are all under event-39 roots.
    /// </summary>
    public static void GiveSlaves(World world, Squad speaker, CharacterCold target)
    {
        var table = world.Characters;
        foreach (int s in SlavesOf(world, speaker).ToList())
        {
            var slave = table.Cold(s)!;
            if (table.TryResolveNext(slave.CarriedBy, out int carrier)) world.SystemOf<CarrySystem>()?.LetGo(world, carrier);

            FixtureRegistry.Enslave(world, slave, target.Faction, target.SquadId);
        }
    }
}
