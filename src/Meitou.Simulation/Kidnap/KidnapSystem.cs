using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Settings of the <see cref="KidnapSystem"/>; engine choices unless a doc label says otherwise.
/// </summary>
public sealed record KidnapSettings
{
    /// <summary>
    /// Each kidnapper's finder looks once in this many ticks, on its slot's turn; a haul looks for a cage at the same rate.
    /// </summary>
    public int LookEveryTicks { get; init; } = 15;
    /// <summary>
    /// A kidnapper looks for a victim within its sight (the rescue finder's range; the sensed list's size is <b>Unknown</b>).
    /// </summary>
    public float Sight { get; init; } = AggroSystem.OutdoorSight;
    /// <summary>
    /// Walks to cages before a haul gives up the cage (a slave's) or drops the captive.
    /// </summary>
    public int CageWalks { get; init; } = 3;
    /// <summary>
    /// Lifts that fail before a haul lets the captive be.
    /// </summary>
    public int LiftTries { get; init; } = 3;
    /// <summary>
    /// Game seconds a carry to a cage (<see cref="KidnapSystem.Carry"/> without hauling) waits for a free cage to appear before it drops the captive.
    /// </summary>
    public float PatienceSeconds { get; init; } = 60;
    /// <summary>
    /// Keep a <see cref="KidnapSystem.Log"/> (tests).
    /// </summary>
    public bool RecordLog { get; init; }
}

/// <summary>
/// Where a carry to a cage is: lifting the captive, carrying it, walking it to a cage.
/// </summary>
public enum KidnapPhase : byte
{
    /// <summary>
    /// Lifting the captive.
    /// </summary>
    Lift,
    /// <summary>
    /// Carrying it.
    /// </summary>
    Carry,
    /// <summary>
    /// Walking it to a cage.
    /// </summary>
    Walk
}

/// <summary>
/// One carry of a captive to a cage that the <see cref="CaptiveSystem"/> asked for (<see cref="KidnapSystem.Carry"/>; serial state of the
/// <see cref="KidnapSystem"/>): lifting it, carrying it, walking it to a cage.
/// </summary>
public sealed class Abduction
{
    /// <summary>
    /// The carrier.
    /// </summary>
    public required CharacterId Carrier { get; init; }
    /// <summary>
    /// The captive.
    /// </summary>
    public required CharacterId Victim { get; init; }
    /// <summary>
    /// The carrier's faction.
    /// </summary>
    public required int Faction { get; init; }
    /// <summary>
    /// Where the carry is.
    /// </summary>
    public KidnapPhase Phase { get; set; }
    /// <summary>
    /// Game seconds in the phase (the wait for a cage).
    /// </summary>
    public float Time { get; set; }
    /// <summary>
    /// The lifts or cage walks tried in the phase.
    /// </summary>
    public int Tries { get; set; }
    /// <summary>
    /// The cage chosen (reserved while the carry lasts).
    /// </summary>
    public string? Cage { get; set; }
    /// <summary>
    /// A slaver's carry of its downed slave (168): it goes on with its squad and cages the slave once a cage is found, never dropping it for want of
    /// one (goal 112 finds no target).
    /// </summary>
    public bool Hauls { get; init; }

    /// <summary>
    /// Adds the carry to the world's state hash.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(Carrier);
        h.Add(Victim);
        h.Add(Faction);
        h.Add((int)Phase);
        h.Add(Time);
        h.Add(Tries);
        h.Add(Cage is { } c ? Rng.StableHash(c) : 0UL);
        if (Hauls) h.Add(0x48617573);   // only when set, so the other carries hash as before
    }
}

/// <summary>
/// What happened in a kidnapping (<see cref="KidnapEvent"/>).
/// </summary>
public enum KidnapEventKind
{
    /// <summary>
    /// The victim was lifted.
    /// </summary>
    Lifted,
    /// <summary>
    /// The victim was put in a cage.
    /// </summary>
    Caged,
    /// <summary>
    /// The carrier put the victim down for want of a cage.
    /// </summary>
    Dropped,
    /// <summary>
    /// The carry ended otherwise.
    /// </summary>
    GaveUp
}

/// <summary>
/// One kidnapping event of the log (<see cref="KidnapSystem.Log"/>): the tick, what happened, the carrier, the victim and the faction.
/// </summary>
public readonly record struct KidnapEvent(long Tick, KidnapEventKind Kind, CharacterId Carrier, CharacterId Victim, int Faction);

/// <summary>
/// Raiders kidnapping the downed (docs/game/factions-squads-towns.md "Campaigns", carry-loot.md 3.2; docs/simulation.md "Kidnapping"), in
/// <c>SlowWorld</c>, serially, after the carry system:
/// <list type="bullet">
/// <item><b>Goal 254</b> "Find and kidnap (with cages) urgent" is a goal of the one chooser (<see cref="KidnapGoals"/>): a member whose lists hold it
/// weighs it, and once chosen lifts the victim 132's finder found (<see cref="IsVictim"/>). The lift ends the goal; the carrier's own goals (112 / 281)
/// cage the victim, which is then <see cref="CharacterCold.Kidnapped"/> in a cage of the kidnapper's faction.</item>
/// <item><b>Carries to a cage</b> the <see cref="CaptiveSystem"/> asks for (<see cref="Carry"/>, a slave's haul for goal 168): lift the captive, carry it
/// with the squad, walk it to a free cage of the carrier's faction or an ally (<see cref="CageFor"/>) and imprison it.</item>
/// </list>
/// What the captors then do with the captive is <see cref="CaptiveSystem"/>'s. Not built: the witnesses' kidnapping crime.
/// </summary>
public sealed class KidnapSystem : ITickSystem, IStateHashed, IDutyRelease, IDutyGoalOwner
{
    #region State

    /// <summary>
    /// The population's data (relations, factions, prison time).
    /// </summary>
    readonly PopulationData _data;
    /// <summary>
    /// The population system (the carriers' package goals).
    /// </summary>
    readonly PopulationSystem _population;
    /// <summary>
    /// The carry system that lifts and lets go.
    /// </summary>
    readonly CarrySystem _carry;
    /// <summary>
    /// Goal 254: the finder's picks, the lifts it acts out and the victims lifted (<see cref="KidnapGoals"/>).
    /// </summary>
    readonly KidnapGoals _goals;
    /// <summary>
    /// The carries to a cage running, in the order they started.
    /// </summary>
    readonly List<Abduction> _abductions = [];
    /// <summary>
    /// The events since the world began, kept only with <see cref="KidnapSettings.RecordLog"/>.
    /// </summary>
    readonly List<KidnapEvent> _log = [];

    /// <summary>
    /// The kidnapping settings.
    /// </summary>
    public KidnapSettings Settings { get; }
    /// <summary>
    /// The relation event of a caging (<see cref="FactionDeeds.Caged"/>); null: none.
    /// </summary>
    public FactionDeeds? Deeds { get; set; }
    /// <summary>
    /// The carries to a cage running (<see cref="Carry"/>).
    /// </summary>
    public IReadOnlyList<Abduction> Abductions => _abductions;
    /// <summary>
    /// The events kept (<see cref="KidnapSettings.RecordLog"/>).
    /// </summary>
    public IReadOnlyList<KidnapEvent> Log => _log;

    /// <summary>
    /// Makes the system over the population's data, with the carry system that lifts and the settings (null: the defaults).
    /// </summary>
    public KidnapSystem(PopulationData data, PopulationSystem population, CarrySystem carry, KidnapSettings? settings = null)
    {
        _data = data;
        _population = population;
        _carry = carry;
        Settings = settings ?? new KidnapSettings();
        _goals = new KidnapGoals(this, carry, data);
    }

    #endregion

    #region The victim

    /// <summary>
    /// Whether the character in <paramref name="slot"/> may be kidnapped by one of <paramref name="faction"/> (the finder of goal 132, carry-loot.md
    /// 3.2, <c>FUN_1408cd2d0</c> with the bed allowed, <b>Verified</b>): alive, of a carriable race, not carried, not an animal, and either lying in
    /// a bed (awake or not: the lift is then a kidnap that rolls) or down and not caged; and not of the faction or an ally of it (<b>engine
    /// reading</b> of the sensed-class masks).
    /// </summary>
    public bool IsVictim(World world, int slot, int faction)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive || table.Cold(slot) is not { Race: { Carriable: true }, Medical: { Dead: false } medical } c)
            return false;

        if (c.IsImprisoned || !c.CarriedBy.IsNone || c.Fighter is { IsAnimal: true }) return false;
        if (c.InSomething == HeldState.Bed ? !CarrySystem.CanBeKidnapped(table, slot) && !medical.Incapacitated : !medical.Incapacitated
                || c.InSomething != HeldState.None)
            return false;

        var relations = _data.Relations;
        return c.Faction != faction && !(relations.Contains(faction) && relations.Contains(c.Faction) && relations.IsAlly(faction, c.Faction));
    }

    #endregion

    #region Carries to a cage

    /// <summary>
    /// Has the member in <paramref name="slot"/> lift <paramref name="victim"/> and carry it to a cage of its faction or an ally in range (the
    /// carrying and caging of goals 167 and 168, which the <see cref="CaptiveSystem"/> starts once it has cuffed or found the captive; the original's
    /// goal 112 puts the carried one in a cage). False when the member cannot carry, the victim is someone's pick already, or no free cage is in
    /// range. With <paramref name="hauls"/> (a slave, goal 168) no cage is needed: see <see cref="Abduction.Hauls"/>.
    /// </summary>
    public bool Carry(World world, int slot, CharacterId victim, bool hauls = false)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(victim, out int v) || !CanHaul(world, slot) || Busy(table.IdOf(slot))) return false;

        foreach (var a in _abductions)
            if (a.Victim == victim) return false;

        var cold = table.Cold(slot)!;
        if (!hauls && CageFor(world, table.Next[v].Position, cold, null) is null) return false;

        var task = (CharacterTask)table.Next[slot].Task;
        var resume = hauls ? HaulResume(world, slot, cold) : TaskStart.Standing(cold, task);
        if (!_carry.BeginPickUp(world, slot, victim, resume, kidnap: true, source: TaskSource.Goal, row: TaskType.CarryWoundedSlaves))
            return false;

        // The haul holds its carrier (goal 168's act-out is this carry until 168 is a chooser goal).
        cold.Engaged = true;
        _abductions.Add(new Abduction { Carrier = table.IdOf(slot), Victim = victim, Faction = cold.Faction, Hauls = hauls });
        return true;
    }

    /// <summary>
    /// A member that may start a carry: awake, not fighting or fleeing or held, able to carry, doing nothing but walking with its squad.
    /// </summary>
    static bool CanHaul(World world, int slot)
    {
        var table = world.Characters;
        if (!table.Next[slot].Alive
                || table.Next[slot].Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow
                        or (byte)CharacterTask.GoTo))
            return false;

        return table.Cold(slot) is { IsPlayer: false, Fleeing: false, Engaged: false } c && CarrySystem.CanCarry(world, c, TaskType.KidnapOrder)
            && !DecisionSystem.Ordered(c);
    }

    /// <summary>
    /// Whether <paramref name="id"/> carries a captive to a cage.
    /// </summary>
    bool Busy(CharacterId id)
    {
        foreach (var a in _abductions)
            if (a.Carrier == id) return true;
        return false;
    }

    /// <summary>
    /// What a hauling carrier does once it has the slave: a squad member follows its leader (goal 141, the next of its list), the leader stands.
    /// </summary>
    static CharacterTask HaulResume(World world, int slot, CharacterCold cold) =>
        world.Squads.Find(cold.SquadId) is { } squad && squad.Leader != world.Characters.IdOf(slot) ? CharacterTask.Follow : CharacterTask.Idle;

    /// <summary>
    /// Whether a free cage <paramref name="carrier"/> may use is in range of <paramref name="at"/> (see <see cref="CageFor"/>).
    /// </summary>
    public bool HasCage(World world, Vector3 at, CharacterCold carrier) => CageFor(world, at, carrier, null) is not null;

    /// <summary>
    /// A free cage <paramref name="carrier"/> may use among the candidates of its town (<see cref="FixtureRegistry.CageCandidates"/>: the town it
    /// stands in, else its faction's nearest, plus its home's), not another carry's (<see cref="FixtureRegistry.FindCage"/>: the original's
    /// weights, its own faction's x100, any other faction's only when <see cref="FixtureRegistry.CanUse"/> passes).
    /// </summary>
    Fixture? CageFor(World world, Vector3 at, CharacterCold carrier, Abduction? self)
    {
        var candidate = world.Fixtures.CageCandidates(world, _data, carrier, at);
        return world.Fixtures.FindCage(world, carrier, at, FixtureRegistry.HomeOf(world, carrier), f => !candidate(f) || Reserved(f, self));
    }

    /// <summary>
    /// Whether another carry chose cage <paramref name="f"/>.
    /// </summary>
    bool Reserved(Fixture f, Abduction? self)
    {
        foreach (var a in _abductions)
            if (!ReferenceEquals(a, self) && a.Cage == f.Id) return true;
        return false;
    }

    /// <summary>
    /// An order takes the carrier (<see cref="IDutyRelease"/>): its carry ends, the captive let go where it stands (<see cref="Drop"/>'s unlink) and
    /// its walk stood rather than turned into a follow, which the order's own start replaces.
    /// </summary>
    void IDutyRelease.ReleaseForOrder(World world, int slot)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        for (int k = _abductions.Count - 1; k >= 0; k--)
        {
            var a = _abductions[k];
            if (a.Carrier != id) continue;

            bool carries = table.Cold(slot)?.Carrying == a.Victim;
            if (carries) _carry.LetGo(world, slot);
            if (table.Next[slot].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, slot);
            End(world, a, carries ? KidnapEventKind.Dropped : KidnapEventKind.GaveUp);
            _abductions.RemoveAt(k);
        }
    }

    #endregion

    #region The tick

    /// <summary>
    /// Registers goal 254 (<see cref="KidnapGoals.Register"/>).
    /// </summary>
    void IDutyGoalOwner.RegisterGoals(DutyGoals goals) => _goals.Register(goals);

    /// <summary>
    /// The kidnapping step: 254's lifts move on (a lift that ended is done), each carry to a cage moves on (an ended one is dropped), then the
    /// kidnappers on their turn look for victims (each once in <see cref="KidnapSettings.LookEveryTicks"/>, docs/simulation.md "Spread sweeps").
    /// </summary>
    public void SlowWorld(World world)
    {
        _goals.Step(world);
        for (int k = 0; k < _abductions.Count;)
        {
            if (Step(world, _abductions[k])) k++;
            else _abductions.RemoveAt(k);
        }

        _goals.Look(world, world.Every(Settings.LookEveryTicks));
    }

    #endregion

    #region One carry

    /// <summary>
    /// One tick of a carry to a cage; false when it is over.
    /// </summary>
    bool Step(World world, Abduction a)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(a.Carrier, out int g) || table.Cold(g) is not { Medical.Incapacitated: false } gc)
            return End(world, a, KidnapEventKind.GaveUp);

        if (!table.TryResolveNext(a.Victim, out int t) || table.Cold(t) is not { } tc || tc.Medical is { Dead: true }) return VictimDied(world, a, g, gc);
        if (tc.IsImprisoned) return End(world, a, KidnapEventKind.GaveUp);

        // A stumble or a get-up (an order on the body, which lets no duty go) holds the carrier a moment: the carry waits for it.
        if (PlayerOrders.BodyHolds(gc)) return true;

        a.Time += world.TickSeconds;
        ref var gn = ref table.Next[g];
        return a.Phase switch
        {
            KidnapPhase.Lift => StepLift(world, a, g, t, gc, tc, ref gn),
            KidnapPhase.Carry => StepCarry(world, a, g, gc, ref gn),
            _ => StepWalk(world, a, g, gc, tc, ref gn),
        };
    }

    /// <summary>
    /// The captive died on the way: it stays on the shoulder of a carrier whose package lists 281, whose town duty cages the corpse
    /// (<see cref="TownDutySystem"/>); any other carrier lets it go. The carry is over.
    /// </summary>
    bool VictimDied(World world, Abduction a, int g, CharacterCold gc)
    {
        var table = world.Characters;
        bool keeps = gc.Carrying == a.Victim && table.TryResolveNext(a.Victim, out _) && world.Squads.Find(gc.SquadId) is { } squad
            && _population.GoalDuty(world, squad, AiSystem.RoleOf(squad, a.Carrier, gc), TaskType.FindCageAndPutDeadguyIn);
        if (!keeps) _carry.LetGo(world, g);

        return End(world, a, KidnapEventKind.GaveUp);
    }

    /// <summary>
    /// The lift: once the carrier has the body it carries it; a lift that ended without it is tried again a few times.
    /// </summary>
    bool StepLift(World world, Abduction a, int g, int t, CharacterCold gc, CharacterCold tc, ref CharacterHot gn)
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
        var resume = a.Hauls ? HaulResume(world, g, gc) : CharacterTask.Idle;
        if (++a.Tries > Settings.LiftTries || !tc.CarriedBy.IsNone || !IsVictim(world, t, a.Faction)
                || !_carry.BeginPickUp(world, g, a.Victim, resume, kidnap: true, source: TaskSource.Goal, row: TaskType.CarryWoundedSlaves))
            return End(world, a, KidnapEventKind.GaveUp);

        return true;
    }

    /// <summary>
    /// The carry: every look the best free cage; a haul goes on with its squad without one, a carry to a cage waits a while and then drops the
    /// captive (engine choices); a dropped body ends it.
    /// </summary>
    bool StepCarry(World world, Abduction a, int g, CharacterCold gc, ref CharacterHot gn)
    {
        // dropped (hit, the arm lost): the body lies there for the next one
        if (gc.Carrying != a.Victim) return End(world, a, KidnapEventKind.GaveUp);
        if ((world.Tick + g) % world.Every(Settings.LookEveryTicks) != 0) return true;

        if (CageFor(world, gn.Position, gc, a) is { } found) return ToCage(world, a, g, found);
        if (a.Hauls || a.Time <= Settings.PatienceSeconds) return true;

        return Drop(world, a, g);
    }

    /// <summary>
    /// The walk to the cage: at it the captive is caged; a cage gone, taken or out of reach is a retry.
    /// </summary>
    bool StepWalk(World world, Abduction a, int g, CharacterCold gc, CharacterCold tc, ref CharacterHot gn)
    {
        if (gc.Carrying != a.Victim) return End(world, a, KidnapEventKind.GaveUp);

        var cage = a.Cage is { } id ? world.Fixtures.Find(id) : null;
        if (cage is null || !FixtureRegistry.IsFree(world.Characters, cage)) return Retry(world, a, g);

        bool walking = gn.Task == (byte)CharacterTask.GoTo;
        if (FixtureRegistry.AtCage(cage, gn.Position, walking)) return PutIn(world, a, g, tc, cage);
        if (walking) return true;

        if (++a.Tries > Settings.CageWalks)
        {
            world.Fixtures.MarkUnreachable(world, cage, a.Faction);
            return Retry(world, a, g);
        }

        TaskStart.Walk(world, g, cage.Position, SpeedMode.Free, TaskType.FindCageAndPutIn, TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// At the cage: the carrier lets go and imprisons the captive, held without a sentence in a cage of its own faction (#628: in another faction's
    /// cage, a public barracks' it may use, it is that faction's prisoner, whose jailers free it once served).
    /// </summary>
    bool PutIn(World world, Abduction a, int g, CharacterCold tc, Fixture cage)
    {
        var table = world.Characters;
        TaskStart.Stand(table, g);
        _carry.LetGo(world, g);
        if (!world.Fixtures.Imprison(world, a.Victim, cage, _data.Bodies.Constants.PrisonTime, a.Carrier)) return Retry(world, a, g);

        if (table.TryResolveNext(a.Victim, out int caged)) Deeds?.Caged(world, g, caged);
        tc.Kidnapped = cage.Owner == a.Faction;
        if (tc.IsPlayer) world.Post($"{tc.Name} has been kidnapped by {_data.Factions[a.Faction].Name}");
        return End(world, a, KidnapEventKind.Caged);
    }

    /// <summary>
    /// Sends the carrier to <paramref name="cage"/>, which the carry reserves.
    /// </summary>
    bool ToCage(World world, Abduction a, int g, Fixture cage)
    {
        a.Phase = KidnapPhase.Walk;
        a.Tries = 0;
        a.Time = 0;
        a.Cage = cage.Id;
        TaskStart.Walk(world, g, cage.Position, SpeedMode.Free, TaskType.FindCageAndPutIn, TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// The cage was taken or cannot be reached: another one (the walks are counted), else a haul goes on with its squad and a carry to a cage drops
    /// the captive.
    /// </summary>
    bool Retry(World world, Abduction a, int g)
    {
        var table = world.Characters;
        a.Cage = null;
        if (++a.Tries > Settings.CageWalks || CageFor(world, table.Next[g].Position, table.Cold(g)!, a) is not { } cage)
            return a.Hauls ? CarryOn(world, a, g) : Drop(world, a, g);

        return ToCage(world, a, g, cage);
    }

    /// <summary>
    /// A hauling carrier with no cage to go to keeps its slave and goes back to its squad (<see cref="Abduction.Hauls"/>).
    /// </summary>
    bool CarryOn(World world, Abduction a, int g)
    {
        a.Phase = KidnapPhase.Carry;
        a.Tries = 0;
        a.Time = 0;
        var table = world.Characters;
        if (table.Next[g].Task != (byte)CharacterTask.GoTo) return true;

        if (world.Squads.Find(table.Cold(g)!.SquadId) is { } squad && squad.Leader != a.Carrier)
            TaskStart.Become(world, g, CharacterTask.Follow, TaskType.FollowSquadleader, TaskSource.Goal);
        else TaskStart.Stand(table, g);
        return true;
    }

    /// <summary>
    /// The carrier puts the captive down for want of a cage.
    /// </summary>
    bool Drop(World world, Abduction a, int g)
    {
        TaskStart.Stand(world.Characters, g);
        _carry.LetGo(world, g);
        return End(world, a, KidnapEventKind.Dropped);
    }

    /// <summary>
    /// Ends a carry: the carrier is let go and goes back to its squad (following its leader) or its routine (the AI takes it again).
    /// </summary>
    bool End(World world, Abduction a, KidnapEventKind kind)
    {
        var table = world.Characters;
        if (table.TryResolveNext(a.Carrier, out int g) && table.Cold(g) is { } gc)
        {
            gc.Engaged = false;
            if (table.Next[g].Task == (byte)CharacterTask.GoTo)
            {
                // A squad's follower walks on after its leader again.
                if (world.Squads.Find(gc.SquadId) is { } squad && squad.Leader != a.Carrier)
                    TaskStart.Become(world, g, CharacterTask.Follow, TaskType.FollowSquadleader, TaskSource.Goal);
                else TaskStart.Stand(table, g);
            }
        }

        Note(world, kind, a);
        return false;
    }

    #endregion

    #region Log and hash

    /// <summary>
    /// Logs an event of a carry (<see cref="KidnapSettings.RecordLog"/>).
    /// </summary>
    void Note(World world, KidnapEventKind kind, Abduction a) => Note(world, kind, a.Carrier, a.Victim, a.Faction);

    /// <summary>
    /// Logs a kidnapping event (<see cref="KidnapSettings.RecordLog"/>) and writes it to the event log.
    /// </summary>
    internal void Note(World world, KidnapEventKind kind, CharacterId carrier, CharacterId victim, int faction)
    {
        if (Settings.RecordLog) _log.Add(new KidnapEvent(world.Tick, kind, carrier, victim, faction));

        string by = EventLog.Who(world, carrier), whom = EventLog.Who(world, victim);
        world.Events.Line(world, "kidnap", $"{kind}: {whom} by {by} ({EventLog.FactionName(world, faction)})");
    }

    /// <summary>
    /// Adds 254's records and the running carries to the world's state hash, while there are any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        _goals.Hash(ref hasher);
        if (_abductions.Count == 0) return;   // only while one runs, so worlds without carries hash as before

        hasher.Add(0x4B64704E);
        hasher.Add(_abductions.Count);
        foreach (var a in _abductions) a.Hash(ref hasher);
    }

    #endregion
}
