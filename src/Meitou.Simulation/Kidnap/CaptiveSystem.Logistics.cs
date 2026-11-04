using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Locks;

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
/// in its town and gets in (<c>Task_UseCage</c>).</item>
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
            case TaskType.GATHER_SLAVES_FROM_CAGES:
            case TaskType.GATHER_SLAVES_FROM_CAGES_IF_FEMALE_OR_BEAST:
            case TaskType.GATHER_SLAVES_FROM_CAGES_IF_ITS_AN_EXPORT_TOWN:
            {
                int site = TownAt(data, table.Next[slot].Position);
                if (site < 0) return null;
                if (type == TaskType.GATHER_SLAVES_FROM_CAGES_IF_ITS_AN_EXPORT_TOWN)
                {
                    if (data.Sites[site].Town.Type is not (TownType.Town or TownType.Outpost)) return null;
                    // The desire (AI slot 64): a town of the player's faction or of an anti-slavery one is gathered from by its allies only (FUN_140597b80).
                    int owner = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
                    if (owner >= 0 && (owner == world.Player.Faction || owner < data.Factions.Count && data.Factions[owner].AntiSlavery) && !Allied(cold.Faction, owner)) return null;
                }
                else if (SlavesOf(world, squad).Count(s => !table.Cold(s)!.IsImprisoned && table.Cold(s)!.Medical is not { Incapacitated: true }) > 3) return null;
                return GatherTarget(world, slot, cold, site, type) is { } cage
                    ? new CaptiveJob { Kind = CaptiveJobKind.Gather, Worker = worker, Captive = cage.Occupant, Cage = cage.Id, Faction = cold.Faction, Site = site }
                    : null;
            }
            case TaskType.GO_TO_SOMEWHERE_FOR_DELIVERING_SLAVES:
            {
                if (board.MissionTown < 0 || squad.Leader != worker || !SlavesOf(world, squad).Any()) return null;
                return new CaptiveJob { Kind = CaptiveJobKind.Deliver, Worker = worker, Captive = CharacterId.None, Cage = "", Faction = cold.Faction, Site = board.MissionTown };
            }
            case TaskType.SLAVE_GET_IN_MY_MASTERS_CAGE:
            {
                if (cold.SlaveState != 1 || cold.SlaveOwner < 0 || cold.InSomething != HeldState.None || cold.IsPlayer) return null;
                int site = board.Town >= 0 ? board.Town : TownAt(data, table.Next[slot].Position);
                if (site < 0) return null;
                var at = table.Next[slot].Position;
                Fixture? best = null;
                float bestD = float.MaxValue;
                foreach (var f in CagesOf(world, site))
                {
                    if (f.Owner != cold.SlaveOwner || !FixtureRegistry.IsFree(table, f) || Taken(f.Id)) continue;
                    float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
                    if (d < bestD) (best, bestD) = (f, d);
                }
                return best is null ? null : new CaptiveJob { Kind = CaptiveJobKind.CageSelf, Worker = worker, Captive = worker, Cage = best.Id, Faction = cold.Faction, Site = site };
            }
        }
        return null;
    }

    /// <summary>The cage a gatherer takes a captive from (see the class remarks); the nearest (<c>BuildingFinder</c> slot 1).</summary>
    Fixture? GatherTarget(World world, int slot, CharacterCold cold, int site, TaskType type)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        Fixture? best = null;
        float bestD = float.MaxValue;
        foreach (var f in CagesOf(world, site))
        {
            if (f.Owner == world.Player.Faction && world.Player.Faction >= 0 || Taken(f.Id)) continue;
            if (type != TaskType.GATHER_SLAVES_FROM_CAGES_IF_ITS_AN_EXPORT_TOWN && f.Owner != cold.Faction) continue;
            if (OccupantSlot(world, f) is not (>= 0 and var s) || Busy(table.IdOf(s))) continue;
            var c = table.Cold(s)!;
            if (c.IsPlayer || c.Medical is { Incapacitated: true }) continue;
            if (type == TaskType.GATHER_SLAVES_FROM_CAGES_IF_FEMALE_OR_BEAST && c.Appearance is not { Female: true } && c.Race?.StringId == cold.Race?.StringId) continue;
            float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
            if (d < bestD) (best, bestD) = (f, d);
        }
        return best;
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
            if (n.Task == (byte)CharacterTask.GoTo) return true;
            if (++j.Walks > Settings.Walks + 1 + (int)(Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), centre) / 1000)) return End(world, j, CaptiveEventKind.GaveUp);
            TaskStart.Walk(world, w, centre, SpeedMode.Walk, RowOf(j.Kind), TaskSource.Goal);
            return true;
        }
        TaskStart.Stand(table, w);
        // 218 only in a prison or a slave camp; 194 anywhere (AI slot 66 FUN_140597c70, 65).
        int hour = world.Hour;
        var roles = board.Roles[(int)AiSystem.RoleOf(squad, j.Worker, wc)];
        bool hands = false;
        foreach (var g in roles)
            if (g.InWindow(hour) && (g.Task.Type == TaskType.GIVE_ALL_MY_SLAVES_TO
                || g.Task.Type == TaskType.GIVE_ALL_MY_SLAVES_TO_IF_ITS_AN_IMPORT_TOWN && site.Town.Type is TownType.Prison or TownType.SlaveCamp)) hands = true;
        if (!hands) return End(world, j, CaptiveEventKind.GaveUp);
        int owner = j.Site < data.SiteFactions.Count && data.SiteFactions[j.Site] >= 0 ? data.SiteFactions[j.Site] : wc.Faction;
        var receiver = Receiver(world, w, owner);
        if (receiver is { } to) world.Defer(new Dialogue.DialogueEvent(j.Worker, to, SlaveDelivery) { Tick = world.Tick + 1 });
        int master = receiver is { } by && table.Cold(by.Slot) is { } rc ? rc.SquadId : -1;
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
            if (Settings.RecordLog) log.Add(new CaptiveEvent(world.Tick, CaptiveEventKind.Delivered, j.Kind, j.Worker, id));
        }
        return End(world, j, null);
    }

    /// <summary>The nearest live NPC of <paramref name="faction"/> or an ally within the town's reach of the leader (the original's designated-building finder, <c>FUN_1408ca950</c>, reduced).</summary>
    CharacterId? Receiver(World world, int w, int faction)
    {
        var table = world.Characters;
        var at = table.Next[w].Position;
        int best = -1;
        float bestD = AggroSystem.OutdoorSight * AggroSystem.OutdoorSight;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == w || !table.Next[s].Alive || table.Cold(s) is not { IsPlayer: false, SlaveState: 0, Medical.Incapacitated: false } c || c.Faction != faction && !Allied(faction, c.Faction)) continue;
            float d = Vector3.DistanceSquared(at, table.Next[s].Position);
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
