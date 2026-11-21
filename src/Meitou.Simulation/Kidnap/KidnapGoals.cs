using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Goal 254 "Find and kidnap (with cages) urgent" as a goal of the one chooser (docs/simulation.md "Kidnapping", "Engine goals"): plan
/// <c>[1, 68, 132, 254]</c> on 132's victim (254's list B wants 153, a carried victim with a cage desire, and 154, carrying nothing: only 153 is unmet
/// while the goal is wanted, as its desire is 0 while carrying). A serial look keeps each member's victim for the think; a chosen goal's act-out is
/// the lift and is done when the lift ends. The carried victim is then the carrier's own goals' (112 / 281 put it in a cage).
/// </summary>
internal sealed class KidnapGoals(KidnapSystem kidnap, CarrySystem carry, PopulationData data)
{
    #region Picks

    /// <summary>
    /// What a member's look found: who looked, its victim and where, and 254's desire factor of its town (<see cref="TownFactor"/>).
    /// </summary>
    readonly record struct Pick(CharacterId Id, CharacterId Victim, Vector3 At, float TownFactor);

    /// <summary>
    /// The victim each member that weighs 254 found at its last look (132's finder <c>FUN_1408cd5c0</c>), by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _picks = [];

    /// <summary>
    /// The sets one look builds once, when a finder first needs them.
    /// </summary>
    struct LookScratch
    {
        /// <summary>
        /// The slots of the downed that may be lifted (or lie in a bed).
        /// </summary>
        public List<int>? Downed;
        /// <summary>
        /// The characters someone lifts now.
        /// </summary>
        public HashSet<CharacterId>? Claimed;
    }

    /// <summary>
    /// The finder's look (serial, the kidnap step), for each character on its slot's turn every <paramref name="every"/> ticks: a member with a
    /// mind of its own that weighs 254 (<see cref="AiSystem.WeighsGoal"/>) keeps the victim 132's finder finds and its town factor; the think's
    /// desire, finder and states read the picks (engine: the original's finder runs at each score, so a pick is at most a look period old).
    /// </summary>
    internal void Look(World world, int every)
    {
        var table = world.Characters;
        var scratch = new LookScratch();
        for (int s = 0; s < table.HighWater; s++)
            if (world.Turn(s, every)) LookAt(world, s, ref scratch);

        Prune(table);
    }

    /// <summary>
    /// The pick of the character at <paramref name="s"/>, or none.
    /// </summary>
    void LookAt(World world, int s, ref LookScratch scratch)
    {
        var table = world.Characters;
        var cold = table.Next[s].Alive ? table.Cold(s) : null;
        if (cold is not { IsPlayer: false, Ai: { Player: false, Squad.Ai: { } board } }
            || !AiSystem.WeighsGoal(world, s, TaskType.FinalKidnapperCageJob))
        {
            _picks.Remove(s);
            return;
        }

        scratch.Downed ??= Downed(table);
        scratch.Claimed ??= Claimed(table);
        int victim = Nearest(world, s, cold, scratch.Downed, scratch.Claimed);
        if (victim < 0)
        {
            _picks.Remove(s);
            return;
        }

        var at = table.Next[victim].Position;
        _picks[s] = new Pick(table.IdOf(s), table.IdOf(victim), at, TownFactor(world, s, cold, board));
    }

    /// <summary>
    /// The slots of the living downed of a carriable race that nobody carries or cages, and of those lying in a bed.
    /// </summary>
    static List<int> Downed(CharacterTable table)
    {
        var downed = new List<int>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Next[s].Alive && table.Cold(s) is { Medical.Dead: false, Race.Carriable: true, IsImprisoned: false } c && c.CarriedBy.IsNone
                && (c.Medical.Incapacitated || c.InSomething == HeldState.Bed))
                downed.Add(s);
        }

        return downed;
    }

    /// <summary>
    /// The characters someone is picking up now (a running pick-up's target) or lifts for 254.
    /// </summary>
    HashSet<CharacterId> Claimed(CharacterTable table)
    {
        var claimed = new HashSet<CharacterId>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Next[s].Alive && table.Next[s].Task == (byte)CharacterTask.PickUp && table.Cold(s)!.CarryTarget is { IsNone: false } target)
                claimed.Add(target);
        }

        foreach (var act in _acting.Values) claimed.Add(act.Victim);
        return claimed;
    }

    /// <summary>
    /// 132's finder (<c>FUN_1408cd2d0</c> with the bed allowed, <b>Verified</b>): the nearest victim within sight (<see cref="KidnapSystem.IsVictim"/>)
    /// that nobody else picks up; -1 for none.
    /// </summary>
    int Nearest(World world, int slot, CharacterCold cold, List<int> downed, HashSet<CharacterId> claimed)
    {
        var next = world.Characters.Next;
        float range = AggroSystem.RangeOf(cold, kidnap.Settings.Sight), best2 = range * range;
        int best = -1;
        foreach (int v in downed)
        {
            if (v == slot || claimed.Contains(world.Characters.IdOf(v)) || !kidnap.IsVictim(world, v, cold.Faction)) continue;

            float d2 = Vector2.DistanceSquared(Flat(next[slot].Position), Flat(next[v].Position));
            if (d2 <= best2) (best, best2) = (v, d2);
        }

        return best;
    }

    /// <summary>
    /// 254's desire factor of the town the member stands in (<c>FUN_1405a1280</c>, <b>Verified</b>): in its squad's home town the cage desire of an
    /// empty target (<c>FUN_1405a0f10</c>: 0 while the running package's mission town is set and is not this town,
    /// <see cref="TownDutySystem.OtherMissionTown"/>; else 1.0) times the free-cage finder (<c>FUN_1408d0d30</c>: 1.0 with a free cage it may use,
    /// <see cref="KidnapSystem.HasCage"/>, else 0); anywhere else 1.0.
    /// </summary>
    float TownFactor(World world, int slot, CharacterCold cold, Blackboard board)
    {
        var at = world.Characters.Next[slot].Position;
        var here = AiSystem.TownAt(data, world, at);
        if (here.IsNone || here != board.HomeTown) return 1;
        if (TownDutySystem.OtherMissionTown(world, cold.SquadId, here)) return 0;

        return kidnap.HasCage(world, at, cold) ? 1 : 0;
    }

    /// <summary>
    /// Drops the picks of slots that no longer hold the character that looked.
    /// </summary>
    void Prune(CharacterTable table)
    {
        List<int>? stale = null;
        foreach (var (slot, pick) in _picks)
            if (!table.TryResolveNext(pick.Id, out int at) || at != slot) (stale ??= []).Add(slot);

        if (stale is not null) foreach (int slot in stale) _picks.Remove(slot);
    }

    /// <summary>
    /// The pick of the asker; null for none or another character's.
    /// </summary>
    Pick? PickOf(in DutyAsk ask) => _picks.TryGetValue(ask.Slot, out var pick) && pick.Id == ask.Id ? pick : null;

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    #endregion

    #region Act-outs

    /// <summary>
    /// A member 254 holds: who, the victim it lifts and where the plan had it.
    /// </summary>
    readonly record struct ActOut(CharacterId Id, CharacterId Victim, Vector3 At)
    {
        /// <summary>
        /// The act-out's target as the plan holds it.
        /// </summary>
        public PlanTarget Target => PlanTarget.OfCharacter(Victim, At);
    }

    /// <summary>
    /// The members 254 holds, by slot: added at the start, removed at the end or when done (hashed).
    /// </summary>
    readonly SortedDictionary<int, ActOut> _acting = [];

    /// <summary>
    /// The engine goals of the world (the AI's), resolved at the first step.
    /// </summary>
    DutyGoals? _duties;

    /// <summary>
    /// Scratch: the act-outs done in a step.
    /// </summary>
    readonly List<int> _done = [];

    /// <summary>
    /// Registers 254 with the rows of its plan the act-out carries out: 132 (its finder) and 68 Lifting (<see cref="DutyGoals.Register"/>).
    /// </summary>
    internal void Register(DutyGoals goals) =>
        goals.Register(TaskType.FinalKidnapperCageJob, new Kidnapping(this), TaskType.FindAndKidnap, TaskType.LiftPerson);

    /// <summary>
    /// The act-outs' step (serial, the kidnap step): each member 254 still holds whose lift has ended (the body on its shoulder, or the lift given
    /// up) is done (<see cref="DutyGoals.Done"/>), and one whose walk to the victim no path reaches (<see cref="CarrySystem.LiftWalkFails"/>) is a
    /// lost walk on the victim (<see cref="DutyGoals.LostWalk"/>); one the goal no longer holds is left to the AI's sweep, which ends it. Then the
    /// lifts are pruned.
    /// </summary>
    internal void Step(World world)
    {
        PruneLifts(world);
        if (_acting.Count == 0) return;

        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        _done.Clear();
        foreach (var (slot, act) in _acting)
        {
            if (_duties?.Runs(world, slot, TaskType.FinalKidnapperCageJob) != true) continue;
            if (world.Characters.Next[slot].Task != (byte)CharacterTask.PickUp || carry.LiftWalkFails(world, slot)) _done.Add(slot);
        }

        foreach (int slot in _done) Finish(world, slot);
    }

    /// <summary>
    /// A held member's lift is over: a lift still walking found no path (a lost walk on the victim), else the lift ended and the goal is done.
    /// </summary>
    void Finish(World world, int slot)
    {
        var act = _acting[slot];
        _acting.Remove(slot);
        if (world.Characters.Next[slot].Task == (byte)CharacterTask.PickUp)
        {
            _duties!.LostWalk(world, slot, TaskType.FinalKidnapperCageJob, IgnoreKey.Of(act.Victim));
            return;
        }

        Lifted(world, slot, act);
        _duties!.Done(world, slot, TaskType.FinalKidnapperCageJob);
    }

    /// <summary>
    /// The act-out's start (serial): the lift (row 68) of the plan's victim, back to standing; false when the victim is no longer one, another
    /// member's act-out works it, or the lift cannot start. A sleeper in a bed is lifted through the kidnap roll
    /// (<see cref="CarrySystem.BeginPickUp"/> with <c>kidnap</c>).
    /// </summary>
    bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        if (VictimOf(plan) is not { Character: { } victim } target || Worked(ask.Slot, victim)) return false;

        var table = world.Characters;
        if (!table.TryResolveNext(victim, out int v) || !kidnap.IsVictim(world, v, table.Cold(ask.Slot)!.Faction)) return false;
        if (!carry.BeginPickUp(world, ask.Slot, victim, CharacterTask.Idle, kidnap: true, source: TaskSource.Goal, row: TaskType.LiftPerson))
            return false;

        _acting[ask.Slot] = new ActOut(ask.Id, victim, target.Position);
        return true;
    }

    /// <summary>
    /// The victim in the plan: the target of its first step on a character (the walk to it, the lift).
    /// </summary>
    static PlanTarget? VictimOf(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
            if (step.Target.Kind == PlanTarget.Found && step.Target.Character is not null) return step.Target;
        return null;
    }

    /// <summary>
    /// Whether another member's act-out lifts <paramref name="victim"/>.
    /// </summary>
    bool Worked(int slot, CharacterId victim)
    {
        foreach (var (other, act) in _acting)
            if (other != slot && act.Victim == victim) return true;

        return false;
    }

    /// <summary>
    /// The goal ended (displaced, an order, a replan, taken, gone): the member is no longer held. A body already on its shoulder stays there (and
    /// counts as lifted, <see cref="Lifted"/>), as the original's carrier keeps it whatever it chooses next; the lift still walking is stopped by the
    /// foundation or replaced by what ended the goal.
    /// </summary>
    void End(World world, int slot, CharacterId id)
    {
        if (!_acting.TryGetValue(slot, out var act) || act.Id != id) return;

        _acting.Remove(slot);
        if (world.Characters.IdOf(slot) == id) Lifted(world, slot, act);
    }

    /// <summary>
    /// 132's finder for the asker: its act-out's victim while 254 holds it, else its pick while the victim is still down and free (the original's
    /// finder runs at each score); factor 1.0 with one (<c>DAT_14167c308</c>), else 0.
    /// </summary>
    float Find(World world, in DutyAsk ask, out PlanTarget target)
    {
        if (_acting.TryGetValue(ask.Slot, out var act) && act.Id == ask.Id) target = act.Target;
        else if (PickOf(ask) is { } pick && StillDown(world, pick.Victim)) target = PlanTarget.OfCharacter(pick.Victim, pick.At);
        else target = default;

        return target.IsNone ? 0 : 1;
    }

    /// <summary>
    /// Whether a picked victim is still alive, uncarried, uncaged and down or in a bed (cold state only serial steps write).
    /// </summary>
    static bool StillDown(World world, CharacterId victim)
    {
        var table = world.Characters;
        return table.IsAlive(victim) && table.Cold(victim.Slot) is { IsImprisoned: false, Medical.Dead: false } c && c.CarriedBy.IsNone
            && (c.Medical.Incapacitated || c.InSomething == HeldState.Bed);
    }

    #endregion

    #region Desires and states

    /// <summary>
    /// The desires of 254 and its rows for the asker (parallel: its own cold state, its squad mates' carry links, its pick).
    /// </summary>
    float Desire(World world, in DutyAsk ask)
    {
        if (world.Characters.Cold(ask.Slot) is not { } cold) return 0;

        return ask.Row switch
        {
            TaskType.LiftPerson => LiftDesire(world, ask, cold),
            TaskType.FindAndKidnap => CarriesOrMatesDo(world, ask.Squad, cold) ? 0 : 1,
            _ => CarriesOrMatesDo(world, ask.Squad, cold) ? 0 : PickOf(ask)?.TownFactor ?? 1,
        };
    }

    /// <summary>
    /// The test 254's and 132's desires share (<c>FUN_1405a1280</c>, <c>FUN_14059a600</c>, <b>Verified</b>): the member carries someone (Character
    /// <c>+0x348</c>), or two members of its squad already do.
    /// </summary>
    internal static bool CarriesOrMatesDo(World world, Squad squad, CharacterCold cold)
    {
        if (!cold.Carrying.IsNone) return true;

        var table = world.Characters;
        int carriers = 0;
        foreach (var member in squad.Members)
        {
            if (table.IsAlive(member) && table.Cold(member.Slot) is { Carrying.IsNone: false } && ++carriers > 1) return true;
        }

        return false;
    }

    /// <summary>
    /// 68 Lifting's desire, AI slot 59 <c>FUN_14059e1a0</c> (<b>Verified</b>): 0 while the member carries someone; 0.5 for a target that is no
    /// character; 0 for one it carries already; else 1.
    /// </summary>
    static float LiftDesire(World world, in DutyAsk ask, CharacterCold cold)
    {
        if (!cold.Carrying.IsNone) return 0;
        if (ask.Target.Character is not { } body || !world.Characters.IsAlive(body)) return 0.5f;

        return world.Characters.Cold(body.Slot)!.CarriedBy == ask.Id ? 0 : 1;
    }

    /// <summary>
    /// The states of the rows for the asker (parallel: <c>Previous</c> places, cold state only serial steps write): for the victim 1 within the lift's
    /// reach (<see cref="CarrySystem.AtBody"/>), 7 down (<see cref="CharacterFacts.TargetDown"/>, a sleeper in a bed included), 48 carried by the
    /// asker; for the goal's own row 153 and 154 (<see cref="RootState"/>); null for the rest.
    /// </summary>
    bool? State(World world, in DutyAsk ask, StateType state)
    {
        var target = ask.Target;
        if (target.Kind != PlanTarget.Found) return RootState(world, ask.Slot, state);
        if (target.Character is not { } victim) return null;

        var table = world.Characters;
        bool alive = table.IsAlive(victim);
        return state switch
        {
            StateType.IsAtLocation or StateType.IsAtLocation46 => alive && carry.AtBody(world, ask.Slot, victim.Slot),
            StateType.IsTargetDown => CharacterFacts.TargetDown(alive ? table.Cold(victim.Slot) : null),
            StateType.IsCarryingTarget => table.Cold(ask.Slot)?.Carrying == victim,
            _ => null,
        };
    }

    /// <summary>
    /// 254's list B states: 153 FINAL_GOT_A_KIDNAP_VICTIM (<c>FUN_1405a3980</c>, <b>Verified</b>: the put-in-cage desire of the carried body,
    /// <c>FUN_1405a1140</c>, above 0: <see cref="TownDutySystem.CageDesire"/> of a carried character) and 154 FINAL_FOUND_AND_PUT_CARRIED_IN_A_CAGE
    /// (<c>140598d00</c>, <b>Verified</b>: the carried hand is empty); null for the rest.
    /// </summary>
    static bool? RootState(World world, int slot, StateType state)
    {
        var table = world.Characters;
        if (state is not (StateType.FinalGotAKidnapVictim or StateType.FinalFoundAndPutCarriedInACage) || table.Cold(slot) is not { } cold)
            return null;
        if (state == StateType.FinalFoundAndPutCarriedInACage) return cold.Carrying.IsNone;

        return table.IsAlive(cold.Carrying) && world.SystemOf<TownDutySystem>() is { } duties && duties.CageDesire(world, slot, cold.Carrying.Slot) > 0;
    }

    #endregion

    #region Lifts

    /// <summary>
    /// A victim a 254 act-out lifted, while its carrier carries it: the carrier, the victim and the carrier's faction.
    /// </summary>
    readonly record struct Lift(CharacterId Carrier, CharacterId Victim, int Faction);

    /// <summary>
    /// The lifted victims by their carrier's slot (hashed): the caging that follows marks the victim kidnapped (<see cref="PruneLifts"/>).
    /// </summary>
    readonly SortedDictionary<int, Lift> _lifts = [];

    /// <summary>
    /// Scratch: the lifts that went stale in a step.
    /// </summary>
    readonly List<int> _stale = [];

    /// <summary>
    /// A done act-out whose carrier holds its victim: the lift is logged, told for a player victim and kept for the caging.
    /// </summary>
    void Lifted(World world, int slot, in ActOut act)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } carrier || carrier.Carrying != act.Victim) return;

        kidnap.Note(world, KidnapEventKind.Lifted, act.Id, act.Victim, carrier.Faction);
        if (table.TryResolveNext(act.Victim, out int v) && table.Cold(v) is { IsPlayer: true } victim)
            world.Post($"{carrier.Name} is carrying {victim.Name} away");

        _lifts[slot] = new Lift(act.Id, act.Victim, carrier.Faction);
    }

    /// <summary>
    /// Drops the lifts whose carrier is gone or no longer carries its victim; a victim that left the shoulder into a cage since the last step (the
    /// carrier's own 112 / 281 put it there) is caged as kidnapped (<see cref="Caged"/>).
    /// </summary>
    void PruneLifts(World world)
    {
        if (_lifts.Count == 0) return;

        var table = world.Characters;
        _stale.Clear();
        foreach (var (slot, lift) in _lifts)
        {
            if (!table.TryResolveNext(lift.Carrier, out int at) || at != slot || table.Cold(slot)?.Carrying != lift.Victim) _stale.Add(slot);
        }

        foreach (int slot in _stale)
        {
            Caged(world, _lifts[slot]);
            _lifts.Remove(slot);
        }
    }

    /// <summary>
    /// A lifted victim now held in a cage: held without a sentence in a cage of its kidnapper's own faction (<see cref="CharacterCold.Kidnapped"/>;
    /// in another faction's cage, a public barracks' the carrier may use, it is that faction's prisoner, whose jailers free it once served, #628), a
    /// player victim is told and the caging logged. Nothing for a victim that is not in a cage.
    /// </summary>
    void Caged(World world, in Lift lift)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(lift.Victim, out int v) || table.Cold(v) is not { IsImprisoned: true, InWhat: { } id } victim) return;
        if (world.Fixtures.Find(id) is not { Kind: FixtureKind.Cage } cage) return;

        victim.Kidnapped = cage.Owner == lift.Faction;
        if (victim.IsPlayer) world.Post($"{victim.Name} has been kidnapped by {data.Factions[lift.Faction].Name}");
        kidnap.Note(world, KidnapEventKind.Caged, lift.Carrier, lift.Victim, lift.Faction);
    }

    #endregion

    #region The goal

    /// <summary>
    /// Goal 254 FINAL_KIDNAPPER_CAGE_JOB as the chooser weighs it (tier and weight from the package's AI_TASK record; no commitment): plan
    /// <c>[1, 68, 132, 254]</c>. Its own row neither acts nor has an effect, so it is planned afresh at each think and ends by displacement or
    /// <see cref="DutyGoals.Done"/>.
    /// </summary>
    sealed class Kidnapping(KidnapGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 254's desire <c>FUN_1405a1280</c>, 132's <c>FUN_14059a600</c> (1.0 unless the shared test) and 68's (<see cref="KidnapGoals.Desire"/>).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => goals.Desire(world, ask);

        /// <summary>
        /// 132's finder (<see cref="KidnapGoals.Find"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(world, ask, out target);

        /// <summary>
        /// The victim's states and 254's own (<see cref="KidnapGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.State(world, ask, state);

        /// <summary>
        /// Lifts the plan's victim.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask, plan);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(world, slot, id);
    }

    #endregion

    /// <summary>
    /// Adds the picks, the act-outs and the lifts to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_picks.Count == 0 && _acting.Count == 0 && _lifts.Count == 0) return;

        hasher.Add(0x4B476F6C);   // "KGol"
        hasher.Add(_picks.Count);
        foreach (var (slot, pick) in _picks)
        {
            hasher.Add(slot);
            hasher.Add(pick.Id);
            hasher.Add(pick.Victim);
            hasher.Add(pick.At);
            hasher.Add(pick.TownFactor);
        }

        HashActing(ref hasher);
        HashLifts(ref hasher);
    }

    /// <summary>
    /// Adds the act-outs to the state hash.
    /// </summary>
    void HashActing(ref StateHasher hasher)
    {
        hasher.Add(_acting.Count);
        foreach (var (slot, act) in _acting)
        {
            hasher.Add(slot);
            hasher.Add(act.Id);
            hasher.Add(act.Victim);
            hasher.Add(act.At);
        }
    }

    /// <summary>
    /// Adds the lifts to the state hash.
    /// </summary>
    void HashLifts(ref StateHasher hasher)
    {
        hasher.Add(_lifts.Count);
        foreach (var (slot, lift) in _lifts)
        {
            hasher.Add(slot);
            hasher.Add(lift.Carrier);
            hasher.Add(lift.Victim);
            hasher.Add(lift.Faction);
        }
    }
}
