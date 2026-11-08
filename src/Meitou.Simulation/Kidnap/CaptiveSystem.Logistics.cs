using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The slavers' logistics (docs/simulation.md "Captives"; facts in docs/game/ai-tasks.md "Slave rows" and docs/game/ai.md "Prisoner and slaver packages"):
/// <list type="bullet">
/// <item><b>Gather</b> (173 "Gathering slaves", 245 "Gathering unholy slaves", wanted while the squad has at most 3 slaves of its own up and uncaged, AI slot 63 <c>FUN_14059b940</c>;
/// 217 "Gathering slaves for shipping", wanted in a town or outpost, AI slot 64 <c>FUN_140597b80</c>; <b>Observed</b>): the nearest occupied cage of the town the gatherer stands in (173, 245:
/// of its faction; 217: of any faction but the player's; 245 only a woman or one of another race than the gatherer, <c>FUN_1408d1410</c>). The gatherer walks there and takes the captive
/// (<c>Task_GetSlave</c>, 174, whose effect is TARGET_IS_MY_SLAVE): <b>engine reading</b> of its queued message (<b>Unknown</b>): the cage is opened, the captive becomes the gatherer's
/// faction's slave and joins the gatherer's squad as a slave member, whose Slave AI Goals then follow the squad (171). Player characters are not gathered (engine choice).</item>
/// <item><b>Deliver</b> (192 "Delivering slaves", wanted at 0.25 per slave, AI slot 65; then 194 / 218 "Selling slaves", 218 only in a prison or slave camp, AI slot 66): the leader walks to
/// the shipping package's town (<see cref="Blackboard.MissionTown"/>); there it says event 39 SLAVE_DELIVERY to the nearest member of an allied squad of the town and hands its slaves
/// over: each leaves the squad for a squad of its own, owned by the town's faction and put into a free cage of the town (<b>engine choice</b>: the original's handover goes through
/// that dialogue, and no base-game dialogue answers event 39, <b>Unknown</b>).</item>
/// <item><b>Get in the cage</b> (172 "Getting in my cage", a Slave AI Goal; <c>FUN_1408caaf0</c>, <b>Observed</b>): a slave not held walks to the nearest free cage of its owner's faction
/// in the 3x3 zone cells round it, in any town, and gets in (<c>Task_UseCage</c>).</item>
/// </list>
/// </summary>
public sealed partial class CaptiveSystem
{
    /// <summary>
    /// A squad's slave list (the original's platoon ownerships, <c>platoon vfunc 0x98 +0x10</c>, docs/game/ai-tasks.md "Pass 3, owned slaves"): its live slave
    /// members in slave state 1, then the live slaves of other squads that obey it (<see cref="CharacterCold.SlaveMaster"/>), as a town's slave squads obey
    /// their workmasters.
    /// </summary>
    public static IEnumerable<int> SlavesOf(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && table.Cold(s) is { SlaveState: 1, Role: (int)SquadRole.Slave, Medical.Dead: false }) yield return s;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { SlaveState: 1, Medical.Dead: false } c) continue;
            if (c.SlaveMaster == squad.Id && c.SquadId != squad.Id) yield return s;
        }
    }

    /// <summary>The town whose radius holds <paramref name="at"/> (the nearest such centre), -1: none.</summary>
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

    CaptiveJob? TryStartLogistics(World world, Squad squad, Blackboard board, int slot, CharacterCold cold, TaskType type)
    {
        var table = world.Characters;
        var worker = table.IdOf(slot);
        switch (type)
        {
            case TaskType.GatherSlavesFromCages:
            case TaskType.GatherSlavesFromCagesIfFemaleOrBeast:
            case TaskType.GatherSlavesFromCagesIfItsAnExportTown:
                {
                    int site = TownAt(data, table.Next[slot].Position);
                    bool export = type == TaskType.GatherSlavesFromCagesIfItsAnExportTown;
                    if (export ? !ExportTown(world, cold, site) : SlavesOf(world, squad).Count(s => table.Cold(s)!.InSomething == HeldState.None && table.Cold(s)!.Medical is not { Incapacitated: true }) > 3) return null;
                    var cages = export ? CagesOf(world, site) : NearbyCages(world, site, table.Next[slot].Position, cold.Faction);
                    return GatherTarget(world, slot, cold, cages, type) is { } cage
                        ? new CaptiveJob { Kind = CaptiveJobKind.Gather, Worker = worker, Captive = cage.Occupant, Cage = cage.Id, Faction = cold.Faction, Site = cage.Site }
                        : null;
                }
            case TaskType.GoToSomewhereForDeliveringSlaves:
                {
                    if (board.MissionTown < 0 || squad.Leader != worker || DeliverDesire(world, squad) <= 0) return null;
                    return new CaptiveJob { Kind = CaptiveJobKind.Deliver, Worker = worker, Captive = CharacterId.None, Cage = "", Faction = cold.Faction, Site = board.MissionTown };
                }
            case TaskType.SlaveGetInMyMastersCage:
                {
                    if (cold.SlaveState != 1 || cold.SlaveOwner < 0 || cold.InSomething != HeldState.None || cold.IsPlayer) return null;
                    // FUN_1408caaf0: the owner faction's free cages in the 3x3 zone cells round the slave, whatever the town (the finder's town argument is 0).
                    var at = table.Next[slot].Position;
                    Fixture? best = null;
                    float bestD = float.MaxValue;
                    foreach (var f in NearbyCages(world, -1, at, cold.SlaveOwner))
                    {
                        if (!FixtureRegistry.IsFree(table, f) || Taken(f.Id)) continue;
                        float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
                        if (d < bestD) (best, bestD) = (f, d);
                    }
                    return best is null ? null : new CaptiveJob { Kind = CaptiveJobKind.CageSelf, Worker = worker, Captive = worker, Cage = best.Id, Faction = cold.Faction, Site = best.Site };
                }
        }
        return null;
    }

    /// <summary>The desire of 217 (AI slot 64, <c>FUN_140597b80</c>): the gatherer stands in a town or outpost, and when its owner is the player's faction or an anti-slavery one, is an ally of it.</summary>
    bool ExportTown(World world, CharacterCold cold, int site)
    {
        if (site < 0 || data.Sites[site].Town.Type is not (TownType.Town or TownType.Outpost)) return false;
        int owner = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        return owner < 0 || !(owner == world.Player.Faction || owner < data.Factions.Count && data.Factions[owner].AntiSlavery) || Allied(cold.Faction, owner);
    }

    /// <summary>
    /// The cages 173 and 245 search (<c>FUN_1409fc450</c> with the town the gatherer stands in, <b>Verified</b>): those of the gatherer's faction in the 3x3 zone cells round it, of that town
    /// when it is in one, of any town when not (the finder's town is only a filter, null passes everything). Only fixtures of towns already registered exist.
    /// </summary>
    IEnumerable<Fixture> NearbyCages(World world, int site, Vector3 at, int faction)
    {
        IEnumerable<Fixture> all = site >= 0 ? CagesOf(world, site) : world.Fixtures.All.Where(f => f.Kind == FixtureKind.Cage);
        foreach (var f in all)
        {
            if (f.Owner == faction && WorldLayout.WithinOneZone(at.X, at.Z, f.Position.X, f.Position.Z)) yield return f;
        }
    }

    /// <summary>The cage a gatherer takes a captive from (see the class remarks): 173 and 217 the nearest (<c>BuildingFinder</c> slot 1), 245 the first in the finder's order
    /// whose occupant is a woman or of another race (<c>FUN_1408d1410</c>, <b>Verified</b>: no distance sort, and no test of the occupant being a player character, down or busy).</summary>
    Fixture? GatherTarget(World world, int slot, CharacterCold cold, IEnumerable<Fixture> cages, TaskType type)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        Fixture? best = null;
        float bestD = float.MaxValue;
        foreach (var f in cages)
        {
            if (f.Owner == world.Player.Faction && world.Player.Faction >= 0 || !GatherCage(world, slot, cold, f, type, out var c)) continue;
            if (type == TaskType.GatherSlavesFromCagesIfFemaleOrBeast) return f;
            float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
            if (d < bestD) (best, bestD) = (f, d);
        }
        return best;
    }

    /// <summary>Whether <paramref name="f"/> and its occupant pass the finder's tests of <paramref name="type"/>.</summary>
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

        // 217 (FUN_1408cc600): dropped for a down or crippled occupant, or one two squad mates already work.
        if (export)
        {
            return !(c.IsPlayer || c.Medical is { Incapacitated: true } || CombatSystem.IsCrippled(c)
                || goals!.Working(cold.SquadId, asker, GoalTarget.Of(table.IdOf(s))) >= 2);
        }
        if (unholy) return c.Appearance is { Female: true } || c.Race?.StringId != cold.Race?.StringId;
        return !(c.IsPlayer || c.Medical is { Incapacitated: true });
    }

    /// <summary>The cage and the captive of every job, in the squad's goal list the finders ask (<see cref="SquadGoals"/>).</summary>
    void IGoalSource.Publish(World world, SquadGoals goals)
    {
        foreach (var j in _jobs)
        {
            if (!world.Characters.TryResolveNext(j.Worker, out int w) || world.Characters.Cold(w) is not { } c) continue;
            goals.Add(c.SquadId, j.Worker, GoalTarget.At(j.Cage));
            goals.Add(c.SquadId, j.Worker, GoalTarget.Of(j.Captive));
        }
    }

    bool StepLogistics(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        switch (j.Kind)
        {
            case CaptiveJobKind.Gather:
                {
                    var cage = world.Fixtures.Find(j.Cage);
                    if (cage is null || !table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { IsImprisoned: true, Medical.Dead: false } cc || cc.InWhat != cage.Id)
                        return End(world, j, CaptiveEventKind.GaveUp);
                    if (!Walk(world, j, w, cage, out bool arrived)) return End(world, j, CaptiveEventKind.GaveUp);
                    if (!arrived) return true;
                    TaskStart.Stand(table, w);
                    if (world.Squads.Find(wc.SquadId) is not { } squad) return End(world, j, CaptiveEventKind.GaveUp);
                    if (cage.Lock is { } l) l.Locked = false;
                    world.Fixtures.LiftOut(world, j.Captive, w);
                    cc.Escaped = false;
                    FixtureRegistry.Enslave(world, cc, wc.Faction, wc.SquadId);
                    Join(world, j.Captive, cc, squad, SquadRole.Slave);
                    Note(world, CaptiveEventKind.Gathered, j);
                    return End(world, j, null);
                }
            case CaptiveJobKind.CageSelf:
                {
                    var cage = world.Fixtures.Find(j.Cage);
                    if (cage is null || !FixtureRegistry.IsFree(table, cage) || wc.InSomething != HeldState.None) return End(world, j, CaptiveEventKind.GaveUp);
                    if (!Walk(world, j, w, cage, out bool arrived)) return End(world, j, CaptiveEventKind.GaveUp);
                    if (!arrived) return true;
                    TaskStart.Stand(table, w);
                    wc.Engaged = false;
                    bool caged = world.Fixtures.Imprison(world, j.Worker, cage);
                    if (caged) Note(world, CaptiveEventKind.Caged, j);
                    return End(world, j, caged ? null : CaptiveEventKind.GaveUp);
                }
            case CaptiveJobKind.Deliver:
                return StepDeliver(world, j, w, wc);
        }
        return End(world, j, CaptiveEventKind.GaveUp);
    }

    /// <summary>Goal 192's desire per slave (<c>DAT_141696c80</c>, read from the exe: 0.25).</summary>
    public const float DeliverDesirePerSlave = 0.25f;

    /// <summary>
    /// Goal 192's desire (AI slot 65, <c>FUN_14059a0a0</c>, <b>Verified</b>): 0.25 per entry of the platoon's slave list; 0 with no slaves. The sweep starts the goal when it is above 0 and takes the first goal in
    /// its hours (with one candidate, the usual package, the scoring against the member's other goals changes nothing observable, so no scorer is modelled).
    /// </summary>
    public float DeliverDesire(World world, Squad squad) => DeliverDesirePerSlave * SlavesOf(world, squad).Count();

    /// <summary>The leader walks to the shipping town (re-sent whenever its walk ended outside it, at most <see cref="CaptiveSettings.Walks"/> times plus one per 1000 units), then hands its slaves over.</summary>
    bool StepDeliver(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        if (world.Squads.Find(wc.SquadId) is not { Ai: { } board } squad || board.MissionTown != j.Site || !SlavesOf(world, squad).Any()) return End(world, j, CaptiveEventKind.GaveUp);
        var site = data.Sites[j.Site];
        var centre = new Vector2(site.Position.X, site.Position.Z);
        ref var n = ref table.Next[w];
        float r = MathF.Max(site.Town.Radius, 100);
        if (Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), centre) > r)
        {
            if (WalksOwn(table, w, j)) return true;
            if (++j.Walks > Settings.Walks + 1 + (int)(Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), centre) / 1000)) return End(world, j, CaptiveEventKind.GaveUp);
            TaskStart.Walk(world, w, centre, SpeedMode.Walk, RowOf(j.Kind), TaskSource.Goal);
            return true;
        }
        if (CharacterFacts.CrouchingOrLying(wc)) return true;   // row 192's B list needs !26: it does nothing while its leader crouches or lies
        TaskStart.Stand(table, w);
        // 218 only in a prison or a slave camp; 194 anywhere (AI slot 66 FUN_140597c70, 65).
        int hour = world.Hour;
        var roles = board.Roles[(int)AiSystem.RoleOf(squad, j.Worker, wc)];
        bool hands = false;
        foreach (var g in roles)
            if (g.InWindow(hour) && (g.Task.Type == TaskType.GiveAllMySlavesTo
                || g.Task.Type == TaskType.GiveAllMySlavesToIfItsAnImportTown && site.Town.Type is TownType.Prison or TownType.SlaveCamp)) hands = true;
        if (!hands) return End(world, j, CaptiveEventKind.GaveUp);
        int owner = j.Site < data.SiteFactions.Count && data.SiteFactions[j.Site] >= 0 ? data.SiteFactions[j.Site] : wc.Faction;
        // The receiver finder (FUN_1408ca950) finding nobody makes the goal unwanted: nothing is handed over, the next sweep retries.
        if (Receiver(world, w, owner, centre, r) is not { } to) return End(world, j, CaptiveEventKind.GaveUp);
        world.Defer(new Dialogue.DialogueEvent(j.Worker, to, SlaveDelivery) { Tick = world.Tick + 1 });
        int master = table.Cold(to.Slot) is { } rc ? rc.SquadId : -1;
        foreach (int s in SlavesOf(world, squad).ToList())
        {
            var sc = table.Cold(s)!;
            var id = table.IdOf(s);
            Join(world, id, sc, NewSquad(world, sc, table.Next[s].Position), SquadRole.Leader);
            FixtureRegistry.Enslave(world, sc, owner, master);
            Fixture? best = null;
            float bestD = float.MaxValue;
            foreach (var f in CagesOf(world, j.Site))
            {
                if (f.Owner != owner && !Allied(owner, f.Owner) || !FixtureRegistry.IsFree(table, f) || Taken(f.Id)) continue;
                float d = Vector2.DistanceSquared(new Vector2(table.Next[s].Position.X, table.Next[s].Position.Z), new Vector2(f.Position.X, f.Position.Z));
                if (d < bestD) (best, bestD) = (f, d);
            }
            if (best is not null) world.Fixtures.Imprison(world, id, best);
            if (Settings.RecordLog) _log.Add(new CaptiveEvent(world.Tick, CaptiveEventKind.Delivered, j.Kind, j.Worker, id));
        }
        return End(world, j, null);
    }

    /// <summary>The live NPC of <paramref name="faction"/> or an ally within <paramref name="radius"/> of the shipping town's <paramref name="centre"/> nearest to the leader
    /// (the original's designated-building finder, <c>FUN_1408ca950</c>, reduced to its owner hand; its acceptance test is <b>Unknown</b>, s07-d083).</summary>
    CharacterId? Receiver(World world, int w, int faction, Vector2 centre, float radius)
    {
        var table = world.Characters;
        var at = table.Next[w].Position;
        int best = -1;
        float bestD = float.MaxValue;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == w || !table.Next[s].Alive || table.Cold(s) is not { IsPlayer: false, SlaveState: not (1 or 2), Medical.Incapacitated: false } c || c.Faction != faction && !Allied(faction, c.Faction)) continue;
            var p = table.Next[s].Position;
            if (Vector2.Distance(new Vector2(p.X, p.Z), centre) > radius) continue;
            float d = Vector3.DistanceSquared(at, p);
            if (d < bestD) (best, bestD) = (s, d);
        }
        return best < 0 ? null : table.IdOf(best);
    }

    /// <summary>A squad of its own for a handed-over slave (a "Wandering Squad" of its faction, as a slave bought free, <see cref="SquadManagement.DismissTemplate"/>).</summary>
    Squad NewSquad(World world, CharacterCold c, Vector3 at)
    {
        var template = data.Db.Find(SquadManagement.DismissTemplate);
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = template?.StringId ?? SquadManagement.DismissTemplate,
            Name = template?.Name ?? "",
            Faction = c.Faction,
            Position = at,
            HomeCentre = new Vector2(at.X, at.Z),
            HomeRadius = 100,
            SpawnSize = 1,
        };
        world.Squads.Add(squad);
        return squad;
    }

    /// <summary>Moves a character into <paramref name="squad"/> in <paramref name="role"/>; its AI mind is made again for the new squad on the AI's next package cadence.</summary>
    static void Join(World world, CharacterId id, CharacterCold c, Squad squad, SquadRole role)
    {
        if (world.Squads.Find(c.SquadId) is { } old)
        {
            old.Members.Remove(id);
            if (old.Leader == id) old.Leader = SquadRegistry.Successor(old.Members, world.Characters);
        }
        c.SquadId = squad.Id;
        c.Role = (int)role;
        c.Ai = null;
        c.Engaged = false;
        c.Kidnapped = false;
        squad.Members.Add(id);
        if (squad.Leader.IsNone || role == SquadRole.Leader) squad.Leader = id;
    }
}