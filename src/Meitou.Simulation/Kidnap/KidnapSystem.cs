using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Kidnap;

/// <summary>Settings of the <see cref="KidnapSystem"/>; engine choices unless a doc label says otherwise.</summary>
public sealed record KidnapSettings
{
    /// <summary>Each kidnapper looks for victims and cages once in this many ticks, on its slot's turn.</summary>
    public int LookEveryTicks { get; init; } = 15;
    /// <summary>A kidnapper looks for a victim within its sight (the rescue finder's range; the sensed list's size is <b>Unknown</b>).</summary>
    public float Sight { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>A carrier counts as in a town of its faction within this multiple of the town's radius (it then looks for the town's cages).</summary>
    public float TownSlack { get; init; } = 1.5f;
    /// <summary>A carrier puts the captive in once within the cage's <c>use range</c> plus this; a walk that ended within <see cref="CageSlack"/> puts it in too (a cage inside a building).</summary>
    public float CageReach { get; init; } = 15;
    public float CageSlack { get; init; } = 200;
    /// <summary>Walks to cages before the carrier gives up and drops the captive; lifts that fail before it lets the victim be.</summary>
    public int CageWalks { get; init; } = 3;
    public int LiftTries { get; init; } = 3;
    /// <summary>Game hours a victim put down for want of a cage is left alone.</summary>
    public double RefuseHours { get; init; } = 2;
    /// <summary>Game seconds an AI squad's carrier waits for a free cage to appear before it drops the captive.</summary>
    public float PatienceSeconds { get; init; } = 60;
    /// <summary>Keep a <see cref="KidnapSystem.Log"/> (tests).</summary>
    public bool RecordLog { get; init; }
}

public enum KidnapPhase : byte { Lift, Carry, Walk }

/// <summary>One kidnapper's hold on one victim (serial state of the <see cref="KidnapSystem"/>): lifting it, carrying it home, walking it to a cage.</summary>
public sealed class Abduction
{
    public required CharacterId Carrier { get; init; }
    public required CharacterId Victim { get; init; }
    public required int Faction { get; init; }
    public KidnapPhase Phase { get; set; }
    /// <summary>Game seconds in the phase (the wait for a cage).</summary>
    public float Time { get; set; }
    public int Tries { get; set; }
    /// <summary>The cage chosen (reserved while the abduction lasts).</summary>
    public string? Cage { get; set; }
    /// <summary>The kidnapper was a campaign squad's when it lifted: it marches home with its squad and keeps the victim until a town of its faction (the campaign ending on the way does not drop it).</summary>
    public bool Marching { get; init; }
    /// <summary>The town a marching carrier gives up in when no cage there is free (a site index; it cages in any town of its faction on the way): its campaign's home, or the home town of an AI squad that retreats with its captive; -1: any town of its faction.</summary>
    public int HomeSite { get; init; } = -1;

    internal void Hash(ref StateHasher h)
    {
        h.Add(Carrier); h.Add(Victim); h.Add(Faction); h.Add((int)Phase); h.Add(Time); h.Add(Tries); h.Add(Marching); h.Add(HomeSite); h.Add(Cage is { } c ? Rng.StableHash(c) : 0UL);
    }
}

public enum KidnapEventKind { Lifted, Caged, Dropped, GaveUp }

public readonly record struct KidnapEvent(long Tick, KidnapEventKind Kind, CharacterId Carrier, CharacterId Victim, int Faction);

/// <summary>
/// Raiders kidnapping the downed (docs/game/factions-squads-towns.md "Campaigns", carry-loot.md 3.2; the original's goal 254 "Find and kidnap (with cages) urgent": find a downed
/// character, lift it, put it in a cage of the kidnapper's faction). Everything runs in <c>SlowWorld</c>, serially, after the carry system.
/// <list type="bullet">
/// <item><b>Duty</b>: the members of a squad whose package has goal 254 in the member's role list and hour window (<see cref="PopulationSystem.KidnapDuty"/>): a loaded campaign squad in the phase
/// whose package lists it (the retreat package of the raids; the cannibal assault), or an AI squad whose running package does (cannibals on patrol and at home).</item>
/// <item><b>Victim</b> (the finder of goal 132, <b>Verified</b>; <see cref="IsVictim"/>): the nearest character within sight that is alive, down, of a <c>carriable</c> race, not carried, not
/// in a cage or a bed, not an animal, not already someone's pick; the sensed-list classes (<b>Unknown</b> bits) are read as "not of the kidnapper's faction or an ally". A free member
/// (awake, not fighting, able to carry) lifts it (<see cref="CarrySystem.BeginPickUp"/>).</item>
/// <item><b>Carry</b>: a campaign carrier goes on with its squad (following its leader, else wandering home, <see cref="ResumeOf"/>) and, within a town of its faction (<see cref="KidnapSettings.TownSlack"/> x its radius), looks for a free
/// cage there (its faction's or an ally's, <see cref="CageFor"/>); an AI squad's member starts only when such a cage is in range. At the cage it lets go and imprisons the victim (<see cref="CharacterCold.Kidnapped"/>: no sentence, no bail). No
/// free cage in the home town, or three failed walks: it drops the victim (<b>engine choice</b>; the original's give-up rule is Unknown).</item>
/// </list>
/// A marching carrier (a campaign's, or a squad's that lists a cannibal retreat package, which then needs no cage in range to lift) cages in the first town of its faction with a free cage and gives up only in its home town (<see cref="Abduction.HomeSite"/>).
/// What the captors then do with the captive is <see cref="CaptiveSystem"/>'s. Not built: the witnesses' kidnapping crime.
/// </summary>
public sealed class KidnapSystem(PopulationData data, PopulationSystem population, CarrySystem carry, KidnapSettings? settings = null) : ITickSystem, IStateHashed
{
    readonly List<Abduction> abductions = [];
    readonly List<KidnapEvent> log = [];
    readonly List<int> victims = [];
    readonly HashSet<CharacterId> reserved = [];
    // Victims a carrier put down for want of a cage: left alone until this game hour (a body would otherwise be lifted and put down in turn). In the order they were put down.
    readonly List<(CharacterId Victim, double Until)> refused = [];

    public KidnapSettings Settings { get; } = settings ?? new KidnapSettings();
    /// <summary>The relation event of a caging (<see cref="FactionDeeds.Caged"/>); null: none.</summary>
    public FactionDeeds? Deeds { get; set; }
    public IReadOnlyList<Abduction> Abductions => abductions;
    public IReadOnlyList<KidnapEvent> Log => log;

    // ------------------------------------------------------------------ the finder

    /// <summary>
    /// Whether the character in <paramref name="slot"/> may be kidnapped by one of <paramref name="faction"/> (the finder of goal 132, carry-loot.md 3.2, <c>FUN_1408cd2d0</c> with the
    /// bed allowed, <b>Verified</b>): alive, of a carriable race, not carried, not an animal, and either lying in a bed (awake or not: the lift is then a kidnap that rolls) or down and
    /// not caged; and not of the faction or an ally of it (<b>engine reading</b> of the sensed-class masks).
    /// </summary>
    public bool IsVictim(World world, int slot, int faction)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Cold(slot) is not { Race: { Carriable: true }, Medical: { Dead: false } medical } c) return false;
        if (c.IsImprisoned || !c.CarriedBy.IsNone || c.Fighter is { IsAnimal: true }) return false;
        if (c.InSomething == HeldState.Bed ? !CarrySystem.CanBeKidnapped(table, slot) && !medical.Incapacitated : !medical.Incapacitated || c.InSomething != HeldState.None) return false;
        var relations = data.Relations;
        return c.Faction != faction && !(relations.Contains(faction) && relations.Contains(c.Faction) && relations.IsAlly(faction, c.Faction));
    }

    /// <summary>A member that may start a kidnapping: awake, not fighting or fleeing or held, able to carry, doing nothing but walking with its squad.</summary>
    static bool FreeKidnapper(World world, int slot)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Next[slot].Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow or (byte)CharacterTask.GoTo)) return false;
        return table.Cold(slot) is { IsPlayer: false, Fleeing: false, Engaged: false } c && CarrySystem.CanCarry(world, c, TaskType.KIDNAP_ORDER);
    }

    /// <summary>
    /// Has the member in <paramref name="slot"/> lift <paramref name="victim"/> and carry it to a cage of its faction or an ally in range (the carrying and caging of goals 167 and 168, which the
    /// <see cref="CaptiveSystem"/> starts once it has cuffed or found the captive; the original's goal 112 puts the carried one in a cage). False when the member cannot carry, the victim
    /// is someone's pick already, or no free cage is in range.
    /// </summary>
    public bool Carry(World world, int slot, CharacterId victim)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(victim, out int v) || !FreeKidnapper(world, slot) || Busy(table.IdOf(slot))) return false;
        foreach (var a in abductions) if (a.Victim == victim) return false;
        var cold = table.Cold(slot)!;
        if (CageFor(world, table.Next[v].Position, cold, null) is null) return false;
        var task = (CharacterTask)table.Next[slot].Task;
        if (!carry.BeginPickUp(world, slot, victim, TaskStart.Standing(cold, task), kidnap: true, source: TaskSource.Goal, row: TaskType.CARRY_WOUNDED_SLAVES)) return false;
        cold.Engaged = true;
        abductions.Add(new Abduction { Carrier = table.IdOf(slot), Victim = victim, Faction = cold.Faction });
        return true;
    }

    // ------------------------------------------------------------------ the tick

    public void SlowWorld(World world)
    {
        for (int k = 0; k < abductions.Count;)
        {
            if (Step(world, abductions[k])) k++;
            else abductions.RemoveAt(k);
        }
        // Each kidnapper looks once in LookEveryTicks, on its slot's turn (slot mod the period): the looks spread over the ticks (docs/simulation.md "Spread sweeps").
        int every = Math.Max(1, Settings.LookEveryTicks);
        Look(world, every, (int)(world.Tick % every));
    }

    void Look(World world, int every, int phase)
    {
        var table = world.Characters;
        victims.Clear();
        reserved.Clear();
        foreach (var a in abductions) reserved.Add(a.Victim);
        refused.RemoveAll(r => r.Until <= world.GameHours);
        foreach (var r in refused) reserved.Add(r.Victim);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Next[s].Alive && table.Next[s].Task == (byte)CharacterTask.PickUp && table.Cold(s)!.CarryTarget is { IsNone: false } target) reserved.Add(target);
            if (table.Next[s].Alive && table.Cold(s) is { Medical.Dead: false, Race.Carriable: true } c && !c.IsImprisoned && c.CarriedBy.IsNone
                && (c.Medical.Incapacitated || c.InSomething == HeldState.Bed)) victims.Add(s);
        }
        if (victims.Count == 0) return;
        Span<int> duty = stackalloc int[5];
        foreach (var squad in world.Squads.All)
        {
            if (squad.Members.Count == 0) continue;
            duty.Fill(-1);
            foreach (var member in squad.Members)
            {
                if (!table.TryResolveNext(member, out int slot) || slot % every != phase || !FreeKidnapper(world, slot)) continue;
                var cold = table.Cold(slot)!;
                int role = (int)Ai.AiSystem.RoleOf(squad, member, cold);
                if (duty[role] < 0) duty[role] = population.KidnapDuty(world, squad, (AiRole)role) ? 1 : 0;
                if (duty[role] == 0 || Busy(member)) continue;
                int victim = Nearest(world, slot, cold);
                if (victim < 0) continue;
                var victimId = table.IdOf(victim);
                // An AI squad has no march home: it starts only with a free cage of its faction in range (the original's "all cages full" state stops the goal).
                var inst = squad.PlatoonId >= 0 ? world.Platoons.Find(squad.PlatoonId)?.Campaign : null;
                bool campaign = inst is not null;
                // A squad with a cannibal retreat package (signals 1, 25) carries its captive home: the package starts once a member carries someone outside a town of its faction.
                bool retreats = !campaign && squad.Ai is { } board && board.Packages.Any(p => PackageClasses.Of(p.Package.Signal) == PackageClass.CannibalRetreat);
                if (!campaign && !retreats && CageFor(world, table.Next[victim].Position, cold, null) is null) continue;
                int homeSite = campaign ? (inst!.Home.Site is >= 0 and var h && h < data.SiteFactions.Count && data.SiteFactions[h] == cold.Faction ? h : -1)
                    : retreats ? squad.Ai!.Town : -1;
                if (!carry.BeginPickUp(world, slot, victimId, ResumeOf(table.Next[slot].Task, campaign || retreats), kidnap: true, source: TaskSource.Goal, row: TaskType.FIND_AND_KIDNAP)) continue;
                cold.Engaged = true;
                reserved.Add(victimId);
                abductions.Add(new Abduction { Carrier = member, Victim = victimId, Faction = cold.Faction, Marching = campaign || retreats, HomeSite = homeSite });
            }
        }
    }

    /// <summary>The nearest victim within sight of the kidnapper, among the downed of the look not picked by anyone (-1: none).</summary>
    int Nearest(World world, int slot, CharacterCold cold)
    {
        var next = world.Characters.Next;
        float range = AggroSystem.RangeOf(cold, Settings.Sight), best2 = range * range;
        int best = -1;
        foreach (int v in victims)
        {
            if (v == slot || reserved.Contains(world.Characters.IdOf(v)) || !IsVictim(world, v, cold.Faction)) continue;
            float d2 = Vector2.DistanceSquared(new Vector2(next[slot].Position.X, next[slot].Position.Z), new Vector2(next[v].Position.X, next[v].Position.Z));
            if (d2 <= best2) (best, best2) = (v, d2);
        }
        return best;
    }

    bool Busy(CharacterId id)
    {
        foreach (var a in abductions) if (a.Carrier == id) return true;
        return false;
    }

    // ------------------------------------------------------------------ one abduction

    /// <summary>One tick of an abduction; false when it is over.</summary>
    bool Step(World world, Abduction a)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        if (!table.TryResolveNext(a.Carrier, out int g) || table.Cold(g) is not { Medical.Incapacitated: false } gc) return End(world, a, KidnapEventKind.GaveUp);
        if (!table.TryResolveNext(a.Victim, out int t) || table.Cold(t) is not { } tc || tc.Medical is { Dead: true })
        {
            // A victim that died on the way stays on the shoulder of a carrier whose package lists 281: the town duty cages the corpse (TownDutySystem.LookDeadCarriers).
            bool keeps = gc.Carrying == a.Victim && table.TryResolveNext(a.Victim, out _) && world.Squads.Find(gc.SquadId) is { } squad
                && population.GoalDuty(world, squad, AiSystem.RoleOf(squad, a.Carrier, gc), TaskType.FIND_CAGE_AND_PUT_DEADGUY_IN);
            if (!keeps) carry.LetGo(world, g);
            return End(world, a, KidnapEventKind.GaveUp);
        }
        if (tc.IsImprisoned) return End(world, a, KidnapEventKind.GaveUp);
        a.Time += dt;
        ref var gn = ref table.Next[g];
        switch (a.Phase)
        {
            case KidnapPhase.Lift:
            {
                if (gc.Carrying == a.Victim)
                {
                    Note(world, KidnapEventKind.Lifted, a);
                    if (tc.IsPlayer) world.Post($"{gc.Name} is carrying {tc.Name} away");
                    a.Phase = KidnapPhase.Carry;
                    a.Time = 0;
                    a.Tries = 0;
                    return true;
                }
                if (gn.Task == (byte)CharacterTask.PickUp) return true;
                // The lift ended without the body (fought off, pushed away): again while it is down and free, a few times.
                if (++a.Tries > Settings.LiftTries || !tc.CarriedBy.IsNone || !IsVictim(world, t, a.Faction) || !carry.BeginPickUp(world, g, a.Victim, ResumeOf(gn.Task, a.Marching), kidnap: true, source: TaskSource.Goal, row: TaskType.FIND_AND_KIDNAP)) return End(world, a, KidnapEventKind.GaveUp);
                return true;
            }
            case KidnapPhase.Carry:
            {
                if (gc.Carrying != a.Victim) return End(world, a, KidnapEventKind.GaveUp);   // dropped (hit, the arm lost): the body lies there for the next one
                // Every look the best free cage of its faction in range. A marching carrier looks only within a town of its faction (any on the way: the original's cage finder weighs
                // the home x100 but does not require it) and drops the captive when its home town (any, without a HomeSite) has no free cage; any other carrier waits a while for one
                // (engine choices; the original's give-up rule is Unknown).
                if ((world.Tick + g) % Math.Max(1, Settings.LookEveryTicks) != 0) return true;
                int town = TownOf(world, gn.Position, gc.Faction);
                if (a.Marching && town < 0) return true;
                if (CageFor(world, gn.Position, gc, a) is { } found) return ToCage(world, a, g, found);
                bool giveUp = a.Marching ? a.HomeSite < 0 || town == a.HomeSite : a.Time > Settings.PatienceSeconds;
                return giveUp ? Drop(world, a, g) : true;
            }
            case KidnapPhase.Walk:
            {
                if (gc.Carrying != a.Victim) return End(world, a, KidnapEventKind.GaveUp);
                var cage = a.Cage is { } id ? world.Fixtures.Find(id) : null;
                if (cage is null || !FixtureRegistry.IsFree(table, cage)) return Retry(world, a, g);
                float d = Vector2.Distance(new Vector2(gn.Position.X, gn.Position.Z), new Vector2(cage.Position.X, cage.Position.Z));
                bool walking = gn.Task == (byte)CharacterTask.GoTo;
                if (d <= cage.UseRange + Settings.CageReach || !walking && d <= Settings.CageSlack)
                {
                    TaskStart.Stand(table, g);
                    carry.LetGo(world, g);
                    if (!world.Fixtures.Imprison(world, a.Victim, cage, 1, a.Carrier)) return Retry(world, a, g);
                    if (table.TryResolveNext(a.Victim, out int caged)) Deeds?.Caged(world, g, caged);
                    tc.Kidnapped = true;
                    if (tc.IsPlayer) world.Post($"{tc.Name} has been kidnapped by {data.Factions[a.Faction].Name}");
                    return End(world, a, KidnapEventKind.Caged);
                }
                if (!walking)
                {
                    if (++a.Tries > Settings.CageWalks)
                    {
                        world.Fixtures.MarkUnreachable(world, cage, a.Faction);
                        return Retry(world, a, g);
                    }
                    TaskStart.Walk(world, g, new Vector2(cage.Position.X, cage.Position.Z), SpeedMode.Free, TaskType.FIND_CAGE_AND_PUT_IN, TaskSource.Goal);
                }
                return true;
            }
        }
        return true;
    }

    /// <summary>
    /// The task a carrier goes back to once it has the body: its wandering or following, else idle; a marching carrier wanders, also from a fight's idle, as it walks home by milling round its
    /// own town (<see cref="PopulationSystem"/>'s retreating column leaves carriers alone; idle it would stand there with the captive).
    /// </summary>
    static CharacterTask ResumeOf(byte task, bool marching) =>
        task is (byte)CharacterTask.Wander or (byte)CharacterTask.Follow ? (CharacterTask)task : marching ? CharacterTask.Wander : CharacterTask.Idle;

    /// <summary>Whether a free cage <paramref name="carrier"/> may use is in range of <paramref name="at"/> (see <see cref="CageFor"/>).</summary>
    public bool HasCage(World world, Vector3 at, CharacterCold carrier) => CageFor(world, at, carrier, null) is not null;

    /// <summary>
    /// A free cage <paramref name="carrier"/> may use among the candidates of its town (<see cref="FixtureRegistry.CageCandidates"/>: the town it stands in, else its faction's nearest, plus its home's),
    /// not another abduction's (<see cref="FixtureRegistry.FindCage"/>: the original's weights, its own faction's x100, any other faction's only when <see cref="FixtureRegistry.CanUse"/> passes).
    /// </summary>
    Fixture? CageFor(World world, Vector3 at, CharacterCold carrier, Abduction? self)
    {
        var candidate = world.Fixtures.CageCandidates(world, data, carrier, at);
        return world.Fixtures.FindCage(world, carrier, at, null, f => !candidate(f) || Reserved(f, self));
    }

    bool Reserved(Fixture f, Abduction? self)
    {
        foreach (var a in abductions) if (!ReferenceEquals(a, self) && a.Cage == f.Id) return true;
        return false;
    }

    /// <summary>The town of <paramref name="faction"/> a point is in (within <see cref="KidnapSettings.TownSlack"/> x its radius; the nearest centre), else -1.</summary>
    int TownOf(World world, Vector3 at, int faction)
    {
        int best = -1;
        float bestD2 = float.MaxValue;
        foreach (var site in data.Sites)
        {
            if (site.Index >= data.SiteFactions.Count || data.SiteFactions[site.Index] != faction || site.Town.Type is TownType.NestMarker or TownType.Null) continue;
            float r = site.Town.SizeRadius * site.Town.TownRadiusMult * Settings.TownSlack;
            float d2 = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(site.Position.X, site.Position.Z));
            if (r > 0 && d2 <= r * r && d2 < bestD2) (best, bestD2) = (site.Index, d2);
        }
        return best;
    }

    bool ToCage(World world, Abduction a, int g, Fixture cage)
    {
        a.Phase = KidnapPhase.Walk;
        a.Tries = 0;
        a.Time = 0;
        a.Cage = cage.Id;
        TaskStart.Walk(world, g, new Vector2(cage.Position.X, cage.Position.Z), SpeedMode.Free, TaskType.FIND_CAGE_AND_PUT_IN, TaskSource.Goal);
        return true;
    }

    /// <summary>The cage was taken or cannot be reached: another one (the walks are counted), else the captive is dropped.</summary>
    bool Retry(World world, Abduction a, int g)
    {
        var table = world.Characters;
        a.Cage = null;
        if (++a.Tries > Settings.CageWalks || CageFor(world, table.Next[g].Position, table.Cold(g)!, a) is not { } cage) return Drop(world, a, g);
        return ToCage(world, a, g, cage);
    }

    bool Drop(World world, Abduction a, int g)
    {
        TaskStart.Stand(world.Characters, g);
        carry.LetGo(world, g);
        refused.Add((a.Victim, world.GameHours + Settings.RefuseHours));
        return End(world, a, KidnapEventKind.Dropped);
    }

    /// <summary>Ends an abduction: the carrier goes back to its squad (following its leader) or its routine (the AI takes it again).</summary>
    bool End(World world, Abduction a, KidnapEventKind kind)
    {
        var table = world.Characters;
        if (table.TryResolveNext(a.Carrier, out int g) && table.Cold(g) is { } gc)
        {
            gc.Engaged = false;
            if (table.Next[g].Task == (byte)CharacterTask.GoTo)
            {
                // A campaign squad's follower walks on after its leader again.
                if (world.Squads.Find(gc.SquadId) is { } squad && squad.Leader != a.Carrier)
                    TaskStart.Become(world, g, CharacterTask.Follow, TaskType.FOLLOW_SQUADLEADER, TaskSource.Goal);
                else TaskStart.Stand(table, g);
            }
        }
        Note(world, kind, a);
        return false;
    }

    void Note(World world, KidnapEventKind kind, Abduction a)
    {
        if (Settings.RecordLog) log.Add(new KidnapEvent(world.Tick, kind, a.Carrier, a.Victim, a.Faction));
    }

    public void Hash(ref StateHasher hasher)
    {
        if (abductions.Count == 0 && refused.Count == 0) return;   // only while one runs, so worlds without kidnappings hash as before
        hasher.Add(0x4B64704E);
        hasher.Add(abductions.Count);
        foreach (var a in abductions) a.Hash(ref hasher);
        hasher.Add(refused.Count);
        foreach (var (victim, until) in refused) { hasher.Add(victim); hasher.Add(until); }
    }
}
