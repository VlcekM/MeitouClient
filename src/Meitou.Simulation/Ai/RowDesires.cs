using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What a planner asks about a row on the way: the character (slot, cold state, mind, squad and blackboard, any of the last three null for a
/// character without them) and the row's target.
/// </summary>
internal readonly record struct RowAsk(int Slot, CharacterCold Cold, AiMind? Mind, Squad? Squad, Blackboard? Board, TaskType Row, PlanTarget Target);

/// <summary>
/// The desire of a TaskData row on the way of a plan (the planner's child nodes, docs/game/ai-tasks.md "Planner": a node of desire 0 or less is a
/// dead end, the open node of the highest g + h is taken first): the row's registered desire member (<see cref="TaskDesires"/>), a constant, AI slot
/// 48's roll or its own function, each function below. A row that no planner can ask (its list A empty, not a provider, or no row needing what it
/// provides) is a goal only, weighed by the goal scorer (<see cref="AiSystem.Desire"/>); every other function row has its function here, checked
/// when the class loads; one that reads the mind or the blackboard answers 0 for a character without it (it weighs no goals). Parallel think: reads
/// the asker's own state, other characters' cold state and <c>Previous</c> places.
/// </summary>
internal sealed class RowDesires(AiSystem ai)
{
    #region Dispatch

    /// <summary>
    /// The AI system whose desires several rows share.
    /// </summary>
    readonly AiSystem _ai = ai;

    /// <summary>
    /// The function rows an order owner takes (<see cref="World.OrderOwnerOf"/>) that no planner asks: the order step weighs them by their desire.
    /// </summary>
    static readonly TaskType[] Ordered = [TaskType.Aoeu, TaskType.DefeatSquadLimitChaseRange, TaskType.OperateAutomaticMachinery];

    /// <summary>
    /// The function of each row a planner can ask or an order owner takes, by type (after <see cref="Ordered"/>, which it reads); null for a
    /// constant, random or goal-only row.
    /// </summary>
    static readonly Func<RowDesires, World, IPlanWorld?, RowAsk, float>?[] Functions = BuildFunctions();

    /// <summary>
    /// The desire of row <paramref name="ask"/>.Row, the states it reads asked of <paramref name="plan"/> (null for an order: the order step asks
    /// no planner, so those states are held).
    /// </summary>
    public float Of(World world, IPlanWorld? plan, in RowAsk ask)
    {
        if (TaskDesires.Of(ask.Row) is not { } desire) return 0;

        return desire.Kind switch
        {
            TaskDesireKind.Constant => desire.Value,
            TaskDesireKind.Random => AiSystem.RandomDesire(world, world.Characters.IdOf(ask.Slot), ask.Row),
            _ => Functions[(int)ask.Row] is { } function
                ? function(this, world, plan, ask)
                : throw new InvalidOperationException($"Row {(int)ask.Row} {ask.Row}: a goal-only row's desire was asked on the way"),
        };
    }

    /// <summary>
    /// The desire of row <paramref name="row"/> on the way of a serial planner (the lock system's door and gate plans) for the character in slot
    /// <paramref name="slot"/>. A world without the AI system has no minds to weigh rows for: there a constant row's value, any other 1.
    /// </summary>
    internal static float OnTheWay(World world, IPlanWorld plan, int slot, CharacterCold cold, TaskType row, in PlanTarget target)
    {
        if (world.SystemOf<AiSystem>() is not { } system) return TaskDesires.Of(row) is { Kind: TaskDesireKind.Constant } d ? d.Value : 1;

        var squad = world.Squads.Find(cold.SquadId);
        return system.Rows.Of(world, plan, new RowAsk(slot, cold, cold.Ai, squad, squad?.Ai, row, target));
    }

    /// <summary>
    /// Whether a planner can ask <paramref name="type"/>'s desire: its list A holds a state some row's list B or effect wants, and it is a provider.
    /// </summary>
    internal static bool Asked(TaskType type)
    {
        if (TaskDataTable.Of(type) is not { } row || row.Is(TaskDataFlags.NotProvider)) return false;

        foreach (var state in row.Done)
            foreach (var user in TaskDataTable.Rows)
                if ((user.Category & row.Category) != 0 && (Array.IndexOf(user.Needs, state) >= 0 || user.Effect == state)) return true;

        return false;
    }

    /// <summary>
    /// Builds <see cref="Functions"/> and checks that every function row a planner can ask or an order owner takes has one.
    /// </summary>
    static Func<RowDesires, World, IPlanWorld?, RowAsk, float>?[] BuildFunctions()
    {
        var a = new Func<RowDesires, World, IPlanWorld?, RowAsk, float>?[TaskBehaviours.TypeCount];
        foreach (var (types, function) in FunctionRows()) foreach (var type in types) a[(int)type] = function;

        foreach (var row in TaskDataTable.Rows)
        {
            bool weighed = Asked(row.Type) || Array.IndexOf(Ordered, row.Type) >= 0;
            if (TaskDesires.Of(row.Type) is { Kind: TaskDesireKind.Function } && weighed && a[(int)row.Type] is null)
                throw new InvalidOperationException($"Row {(int)row.Type} {row.Type}: its own desire function is not built");
        }

        return a;
    }

    /// <summary>
    /// The functions of the rows a planner can ask, each with the rows that register it (<b>Verified</b>, the registrations' stubs,
    /// <see cref="TaskDesires"/>).
    /// </summary>
    static IEnumerable<(TaskType[] Types, Func<RowDesires, World, IPlanWorld?, RowAsk, float> Function)> FunctionRows() =>
    [
        ([TaskType.Pickup], static (_, _, _, a) => CharacterFacts.Down(a.Cold) ? 0 : 1),
        ([TaskType.MeleeAttack, TaskType.MeleeAttackAnimal], static (r, w, p, a) => r.MeleeDesire(w, p, a)),
        ([TaskType.RangedAttack], static (_, w, _, a) => ShootDesire(w, a)),
        ([TaskType.SelfPreservation], static (r, w, _, a) => a.Mind is { } mind ? r._ai.SelfPreservationDesire(w, a.Slot, mind) : 0),
        ([TaskType.RunAway], static (r, w, _, a) => r.RunDesire(w, a)),
        ([TaskType.StandUp], static (_, _, _, a) => CanGetUp(a.Cold) ? 1 : 0),
        ([TaskType.GetReadyForAction], static (_, _, _, a) => ReadyDesire(a.Cold)),
        ([TaskType.GetUp], static (_, w, _, a) => CharacterFacts.PostureZero(a.Cold) || CharacterFacts.GettingUp(a.Cold, w.Tick) ? 1 : 0),
        ([TaskType.GetOutOfBedIfItsEmergency], static (r, w, _, a) => a.Board is { } b ? r._ai.SleepDanger(w, b, a.Slot, a.Cold) : 0),
        ([TaskType.GetOutOfBedOnceHealed], static (r, w, _, a) => r.HealedDesire(w, a)),
        ([TaskType.UseBed, TaskType.SleepOnFloor, TaskType.SleepOnFloorFakeAmbush],
            static (r, w, _, a) => a.Board is { } b ? r._ai.SleepingDesire(w, b, a.Slot) : 0),
        ([TaskType.GetNearTo], static (r, w, _, a) => r._ai.FollowDesireOf(w, a.Row, FollowedOf(w, a))),
        ([TaskType.ManATurret, TaskType.ManATurretOnBuilding, TaskType.ManATurretPlayerJob, TaskType.UseTurret],
            static (r, w, _, a) => r._ai.TurretDesire(w, a.Slot)),
        ([TaskType.RunAwayHometown], static (r, w, _, a) => r.GoHomeDesire(w, a)),
        ([TaskType.LiftPerson, TaskType.LiftObjectButHealFirst], static (_, w, _, a) => OrderDesire.LiftDesire(w, a.Slot, a.Cold, CharacterOf(a))),
        ([TaskType.FindCageAndPutIn, TaskType.FindCageAndPutDeadguyIn], static (_, w, _, a) => CageDesire(w, a)),
        ([TaskType.FindAndKidnap], static (_, w, _, a) => a.Squad is { } s && Kidnap.KidnapGoals.CarriesOrMatesDo(w, s, a.Cold) ? 0 : 1),
        ([TaskType.FindADeadGuy], static (_, _, _, a) => (a.Cold.Orders & CharacterOrders.Hold) != 0 || !a.Cold.Carrying.IsNone ? 0 : 1),
        ([TaskType.ArrestTarget], static (_, w, _, a) => Crime.ArrestGoals.ArrestsHere(w, a.Slot, a.Cold) ? 1 : 0),
        ([TaskType.GetOutOfCageOpportunistic], static (_, w, _, a) => Locks.EscapeGoals.OpportunityDesire(w, a.Slot)),
        ([TaskType.FirstAidOrder], static (_, w, _, a) => TreatDesire(w, a, robot: false)),
        ([TaskType.FirstAidRobot], static (_, w, _, a) => TreatDesire(w, a, robot: true)),
        ([TaskType.SplintOrder], static (_, w, _, a) => w.SystemOf<FirstAid.FirstAidSystem>()?.SplintOrderDesire(w, a.Slot, CharacterOf(a)) ?? 0),
        ([TaskType.EatFoodOnGround], static (_, w, _, a) => Items.FeedSystem.EatDesire(w, a.Slot, a.Cold)),
        ([TaskType.CollectOutputResource], static (r, w, p, a) => r.CollectDesire(w, p, a)),
        ([TaskType.CollectOutputResourceBuildMats], static (r, _, _, a) => r.RoomDesire(a)),
        ([TaskType.UnlockDoorHere], static (_, w, _, a) => DestinationAccess.PlayerSide(w, a.Cold) ? 0 : 1),
        ([TaskType.BashDoor, TaskType.BashDoorHere], static (_, w, _, a) => BashDesire(w, a)),
        ([TaskType.BashGate, TaskType.SmashBuilding], static (_, w, _, a) => GateBashDesire(BashDesire(w, a), MatesBashing(w, a))),
        ([TaskType.AssaultFortificationsPreferGates], static (r, w, _, a) => r.AssaultTownDesire(w, a)),
        ([TaskType.LockAllMyDoors], static (_, w, _, a) => Locks.WardenGoals.LockingUpDesire(w, a.Slot, a.Squad)),
        ([TaskType.EnterBuilding], static (_, _, p, a) => Inside(p, a) ? 0 : 1),
        ([TaskType.LeaveBuilding], static (_, _, p, a) => Inside(p, a) ? 1 : 0),
        ([TaskType.Aoeu], static (_, w, _, a) => LyingOut(a.Cold) && !CharacterFacts.GettingUp(a.Cold, w.Tick) ? 0 : 1),
        ([TaskType.DefeatSquadLimitChaseRange], static (_, w, _, a) => Crime.ArrestGoals.ArrestsHere(w, a.Slot, a.Cold) ? 1 : 0),
        ([TaskType.OperateAutomaticMachinery], static (r, w, _, a) => r.AutomaticDesire(w, a)),
        ([TaskType.ShooStrangersOutOfMyBuildingIfPrivate],
            static (_, _, _, a) => a.Squad is { HomeBuilding: not null, Ai.HomePublic: false } ? 1 : 0),
    ];

    /// <summary>
    /// The desire of the order <paramref name="order"/> for the character in slot <paramref name="slot"/> (serial): its row's registered desire for
    /// the order's target, a character, else its site, else its point.
    /// </summary>
    internal float ForOrder(World world, int slot, Player.OrderEntry order)
    {
        var at = order.Point;
        PlanTarget target = order.Target.IsNone
            ? order.Site is { } site ? PlanTarget.OfFixture(site, at) : PlanTarget.OfPlace(at)
            : PlanTarget.OfCharacter(order.Target, at);
        var cold = world.Characters.Cold(slot)!;
        var squad = world.Squads.Find(cold.SquadId);
        return Of(world, null, new RowAsk(slot, cold, cold.Ai, squad, squad?.Ai, order.Row, target));
    }

    /// <summary>
    /// State 107 <c>I_AM_INSIDE_TARGET_BUILDING</c> as the planner reads it for the target (an order: not inside).
    /// </summary>
    static bool Inside(IPlanWorld? plan, in RowAsk ask) =>
        plan?.Holds(new TaskCondition(StateType.AmInsideTargetBuilding, true), ask.Target) == true;

    /// <summary>
    /// The character a row's target names (none for a place).
    /// </summary>
    static CharacterId CharacterOf(in RowAsk ask) => ask.Target.Character ?? CharacterId.None;

    #endregion

    #region Body

    /// <summary>
    /// <c>canGetUp</c> (<c>FUN_140598c20</c>, <b>Verified</b>; row 28's desire <c>FUN_140317bd0</c> is 1.0 while it holds, else 0): the medical
    /// knocked-out and dead bytes clear, not carried, and the starvation knock-out test false, which Meitou's medical tick folds into the knock-out
    /// itself (<c>FUN_140644320</c> passes the character out as it answers true).
    /// </summary>
    internal static bool CanGetUp(CharacterCold cold) => cold.Medical is not { Unconscious: true } and not { Dead: true } && cold.CarriedBy.IsNone;

    /// <summary>
    /// Row 52's posture test (<c>FUN_140598cc0</c>, <b>Verified</b>: 0 for a posture above 2 while not getting up, Character <c>+0x278</c>):
    /// playing dead (3) or out (<b>Observed</b>, the postures above 2).
    /// </summary>
    static bool LyingOut(CharacterCold cold) => cold.Medical is { PlayingDead: true } or { Incapacitated: true };

    /// <summary>
    /// Row 59 Get ready (AI slot 34 <c>FUN_14059a260</c>, <b>Verified</b>): 0 unless it can get up (<see cref="CanGetUp"/>); then 1.0 with state 38
    /// <c>hasOneWorkingArm</c> (<c>FUN_140998470</c>: an animal, or an arm usable), else 0.05 (<c>DAT_141696c74</c>).
    /// </summary>
    static float ReadyDesire(CharacterCold cold)
    {
        if (!CanGetUp(cold)) return 0;

        return CharacterFacts.Animal(cold) || cold.Medical is not { } medical || SelfPreservation.OneArm(medical) ? 1 : 0.05f;
    }

    /// <summary>
    /// The rested level past which 257 adds 1 (the float at <c>0x14168a25c</c>).
    /// </summary>
    const float HealedPast = 0.9f;

    /// <summary>
    /// Row 257 Getting out of bed once healed (AI slot 29 <c>FUN_140599160</c>, <b>Verified</b>): 0 unless it lies in a bed (Character
    /// <c>+0x2f8</c> 1); then the bed danger (AI slot 28, <see cref="AiSystem.SleepDanger"/>) plus 1.0 when its medical <c>+0xb0</c>
    /// (<see cref="MedicalState.RestedLevel"/>) is above 0.9. Any faction (no player gate, unlike state 72).
    /// </summary>
    float HealedDesire(World world, in RowAsk ask)
    {
        if (ask.Cold.InSomething != Locks.HeldState.Bed) return 0;

        float danger = ask.Board is { } board ? _ai.SleepDanger(world, board, ask.Slot, ask.Cold) : 0;
        bool healed = ask.Cold is { Medical: { } medical, Race: { } race } && medical.RestedLevel(race) > HealedPast;
        return healed ? danger + 1 : danger;
    }

    #endregion

    #region Fights

    /// <summary>
    /// How far a holding character's attack target may be for row 4 (<c>FUN_1405a3080</c> with <c>DAT_14168dcf4</c>, flat).
    /// </summary>
    const float HoldTargetReach = 500;

    /// <summary>
    /// Row 4's desire for a crippled character in Block or Hold with no threat within 40 (<c>DAT_14169c3c4</c>).
    /// </summary>
    const float CrippledStance = 0.0001f;

    /// <summary>
    /// Rows 4 and 227 Attacking (AI slot 53 <c>FUN_1405a4260</c>, <b>Verified</b>), in its order: 0 against a caged target (<c>+0x2f8</c> 2); 0
    /// with no usable arm (<c>FUN_140644150</c>); a crippled character 0.1 with a threat within 40, else 0.0001 in Block or Hold, else 0.1 for one of
    /// the player's faction or a unique character (the record's <c>unique</c>, <c>FUN_140506960</c>) and 0 for the rest; one holding
    /// (<see cref="Orders.Holding"/>) only for a character target within 500 flat that is itself, or whose destination and location are accessible
    /// (states 59 and 60 as the planner reads them, <c>FUN_140597030</c>, <c>FUN_1405a2710</c>); then 1, 0.1 with Ranged on, x0.5 while its squad
    /// is not intact.
    /// </summary>
    float MeleeDesire(World world, IPlanWorld? plan, in RowAsk ask)
    {
        var cold = ask.Cold;
        if (Caged(world, ask.Target) || cold.Medical is not { } medical || !SelfPreservation.OneArm(medical)) return 0;
        if (medical.Crawling) return CrippledMelee(world, ask);
        if (Orders.Holding(cold) && !HoldReaches(world, plan, ask)) return 0;

        float desire = Orders.RangedMode(cold) ? 0.1f : 1;
        return ask.Squad?.Ai is { Intact: false } ? desire * 0.5f : desire;
    }

    /// <summary>
    /// <see cref="MeleeDesire"/>'s crippled branch.
    /// </summary>
    float CrippledMelee(World world, in RowAsk ask)
    {
        var cold = ask.Cold;
        if (Immediate(world, ask.Slot)) return SelfPreservation.CrippledAttack;

        bool block = Orders.Has(cold, CharacterOrders.Block) && CharacterFacts.PostureZero(cold);
        if (block || Orders.Holding(cold)) return CrippledStance;
        bool unique = _ai.Data.Db.Find(cold.RecordId)?.GetBool("unique") == true;
        return DestinationAccess.PlayerSide(world, cold) || unique ? SelfPreservation.CrippledAttack : 0;
    }

    /// <summary>
    /// <see cref="MeleeDesire"/>'s Hold test of the target.
    /// </summary>
    static bool HoldReaches(World world, IPlanWorld? plan, in RowAsk ask)
    {
        if (ask.Target.Character is not { } who || !world.Characters.TryResolveNext(who, out int t)) return false;

        var prev = world.Characters.Previous;
        var d = new Vector2(prev[t].Position.X - prev[ask.Slot].Position.X, prev[t].Position.Z - prev[ask.Slot].Position.Z);
        if (d.Length() >= HoldTargetReach) return false;
        if (t == ask.Slot) return true;

        return plan is null || plan.Holds(new TaskCondition(StateType.DestinationIsAccessible, true), ask.Target)
            && plan.Holds(new TaskCondition(StateType.LocationIsAccessible, true), ask.Target);
    }

    /// <summary>
    /// Row 262 Shooting (AI slot 54 <c>FUN_140598260</c>, <b>Verified</b>): 0 with Ranged off, no usable arm, crippled or no crossbow at back or hip;
    /// 0.1 with a threat within 40; 0 against a caged target or without its bolts; else 1, 0.5 while its squad is not intact
    /// (<see cref="SelfPreservation.ShootDesire"/> and the target test).
    /// </summary>
    static float ShootDesire(World world, in RowAsk ask)
    {
        float desire = SelfPreservation.ShootDesire(ask.Cold, Immediate(world, ask.Slot), ask.Squad?.Ai is not { Intact: false });
        return desire >= 0.5f && Caged(world, ask.Target) ? 0 : desire;
    }

    /// <summary>
    /// Row 35 Running away (AI slot 55 <c>FUN_1405a34e0</c>, <b>Verified</b>): a player-faction mind's player branch, else the body's
    /// (<see cref="SelfPreservation.RunDesire"/>), as the goal scorer weighs it.
    /// </summary>
    float RunDesire(World world, in RowAsk ask) => ask.Mind is { PlayerSide: true }
        ? _ai.PlayerSideRunDesire(world, ask.Slot)
        : SelfPreservation.RunDesire(ask.Cold, SelfPreservation.Walled(world, ask.Slot));

    /// <summary>
    /// Whether the target is a character in a cage (Character <c>+0x2f8</c> 2).
    /// </summary>
    static bool Caged(World world, in PlanTarget target) =>
        target.Character is { } who && world.Characters.IsAlive(who) && world.Characters.Cold(who.Slot) is { IsImprisoned: true };

    /// <summary>
    /// Whether a melee attacker is within 40 of the character (Character <c>+0x2b0</c>).
    /// </summary>
    static bool Immediate(World world, int slot) =>
        world.SystemOf<CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, slot);

    #endregion

    #region Carrying and following

    /// <summary>
    /// Rows 112 and 281 (<c>FUN_1405a1140</c>, <b>Verified</b>): 0 unless the character carries someone (<c>+0x348</c>), else the cage desire for
    /// that body (<see cref="Kidnap.TownDutySystem.CageDesire"/>).
    /// </summary>
    static float CageDesire(World world, in RowAsk ask)
    {
        if (!world.Characters.TryResolveNext(ask.Cold.Carrying, out int body)) return 0;

        return world.SystemOf<Kidnap.TownDutySystem>()?.CageDesire(world, ask.Slot, body) ?? 0;
    }

    /// <summary>
    /// The character row 40 follows: its target's, else the one the character follows (its slave owner, else its squad leader).
    /// </summary>
    static CharacterId FollowedOf(World world, in RowAsk ask)
    {
        if (ask.Target.Character is { } who) return who;

        return Kidnap.ObedientSlave.OwnerOf(world, ask.Cold) ?? ask.Squad?.Leader ?? CharacterId.None;
    }

    /// <summary>
    /// Rows 25 First aid (<c>FUN_14099e0a0</c>) and 60 Repairing (<c>FUN_14099df10</c>) for the target patient
    /// (<see cref="FirstAid.FirstAidSystem.TreatOrderDesire"/>, <b>Verified</b>).
    /// </summary>
    static float TreatDesire(World world, in RowAsk ask, bool robot) =>
        world.SystemOf<FirstAid.FirstAidSystem>()?.TreatOrderDesire(world, ask.Slot, CharacterOf(ask), robot) ?? 0;

    /// <summary>
    /// Row 127 Retreating fast (AI slot 56 <c>FUN_1405a3710</c>, <b>Verified</b>), as the goal scorer weighs it: 0 in its home town, else
    /// <see cref="AiSystem.GoHomeDesire"/>.
    /// </summary>
    float GoHomeDesire(World world, in RowAsk ask)
    {
        var self = world.Characters.IdOf(ask.Slot);
        if (ask.Squad is { } squad && ask.Board is { } board && _ai.AtHomeTown(world, squad, board, self)) return 0;

        return AiSystem.GoHomeDesire(world, self);
    }

    #endregion

    #region Doors and towns

    /// <summary>
    /// Rows 81 and 287 Bashing door (AI slot 36, <c>scoreAggressiveAction</c> <c>FUN_14079d1a0</c>, <see cref="Locks.DoorPlan.AggressiveTowards"/>)
    /// towards the building of the target: the door the character works (a door step's target), else the building at the target's point.
    /// </summary>
    static float BashDesire(World world, in RowAsk ask)
    {
        var at = ask.Target.Kind == AiSystem.Targets.Door && ask.Cold.Bashing is { } id && world.Fixtures.Find(id) is { } door
            ? door.Position
            : ask.Target.Position;
        string building = DestinationAccess.BuildingAt(world, at) ?? "";
        return Locks.DoorPlan.AggressiveTowards(world, world.SystemOf<CombatSystem>(), ask.Cold, building, at);
    }

    /// <summary>
    /// The most mates on one sub-target row 123's desire takes (<c>FUN_1405992f0</c>: 9 or more give 0).
    /// </summary>
    const int MostMates = 8;

    /// <summary>
    /// Rows 123 Bashing gate and 272 Smashing (AI slot 37 <c>FUN_1405992f0</c>, <b>Verified</b>): with fewer than 2 squad mates working on the target
    /// (<c>howManyGuysWorkingOnThisSubTarget</c> <c>FUN_1402690e0</c>) the aggression (slot 36); with 2 to 8 the aggression shared among them; with
    /// 9 or more 0.
    /// </summary>
    internal static float GateBashDesire(float aggression, int mates)
    {
        if (mates > MostMates) return 0;

        return mates < 2 ? aggression : aggression / mates;
    }

    /// <summary>
    /// The squad mates of the character working on its door step's door (<see cref="CharacterCold.Bashing"/>), itself not counted.
    /// </summary>
    static int MatesBashing(World world, in RowAsk ask)
    {
        if (ask.Squad is not { } squad || ask.Cold.Bashing is not { } id) return 0;

        return Locks.LockSystem.MatesBashing(world, squad, id, ask.Slot);
    }

    /// <summary>
    /// Row 270 Assaulting town (AI slot 68 <c>FUN_140597a20</c>, <b>Verified</b>): 1.0 when the town the character stands in (its status town) is
    /// owned by the player's faction and that faction is no ally of the character's (<c>FUN_1406b2630</c>), else 0.
    /// </summary>
    float AssaultTownDesire(World world, in RowAsk ask)
    {
        var at = world.Characters.Previous[ask.Slot].Position;
        var town = ask.Cold.StatusTown.IsNone ? AiSystem.StatusTownAt(_ai.Data, world, at, CampaignTown.None) : ask.Cold.StatusTown;
        if (town.IsNone) return 0;

        int owner = town.Owner(world, _ai.Data);
        int player = world.Player.Faction >= 0 ? world.Player.Faction : _ai.Data.PlayerFaction;
        if (owner < 0 || owner != player) return 0;

        var relations = _ai.Data.Relations;
        bool ally = ask.Cold.Faction >= 0 && relations.Contains(ask.Cold.Faction) && relations.IsAlly(ask.Cold.Faction, owner);
        return ally ? 0 : 1;
    }

    #endregion

    #region Items

    /// <summary>
    /// Row 92 Collecting resource (AI slot 22 <c>FUN_1405a6860</c>, <b>Verified</b>): of a loose item 1.0 while it lies on the ground (item
    /// <c>+0xd8</c> clear); of a machine its output item (the stored one; with none stored, of a machine not of the character's faction, what it
    /// produces), 0 without one or with no room for a unit (<c>FUN_1405cb520</c>); with room 1.0 outside a town (Character vfunc <c>0x78</c>, the
    /// status town), else 1.0 when a storage takes it (the ditch finder <c>FUN_1405a59a0</c> over the machine's output,
    /// <see cref="Kidnap.TownDutySystem.OutputStore"/>), else 0 (0.1 for a building of function 6, which no machine is). Any other target 0.
    /// </summary>
    float CollectDesire(World world, IPlanWorld? plan, in RowAsk ask)
    {
        if (ask.Target.Kind == AiSystem.Targets.Loose) return world.GroundItems.Find(ask.Target.Item) is null ? 0 : 1;
        if ((plan as AiSystem.PlanContext)?.MachineAt(ask.Target) is not { } machine) return 0;
        if (OutputRecord(world, machine, ask.Cold) is not { } record || !RoomFor(ask.Cold, record)) return 0;

        var at = world.Characters.Previous[ask.Slot].Position;
        if (ask.Cold.StatusTown.IsNone && AiSystem.StatusTownAt(_ai.Data, world, at, CampaignTown.None).IsNone) return 1;

        var duties = world.SystemOf<Kidnap.TownDutySystem>();
        return ask.Squad is { } squad && duties?.OutputStore(world, squad, ask.Cold, at, record) is not null ? 1 : 0;
    }

    /// <summary>
    /// The item row 92 would take from <paramref name="machine"/>: its stored output, else, for a machine not of the character's faction, what it
    /// produces; null for neither.
    /// </summary>
    static string? OutputRecord(World world, Production.Machine machine, CharacterCold cold)
    {
        if (machine.Output is { Count: > 0 } output) return output.Recipe.Item;

        return machine.Output is { } made && HeldItemTheft.OwnerOf(world, machine.Site) != cold.Faction ? made.Recipe.Item : null;
    }

    /// <summary>
    /// Room for a unit of <paramref name="record"/> in the character's inventory or worn backpack (<c>FUN_1405cb520</c>, <b>Verified</b>; state 82
    /// reads the same).
    /// </summary>
    internal bool RoomFor(CharacterCold cold, string record) =>
        cold.Inventory is { } bag && _ai.Data.Items.Create(record, 1) is { } unit && bag.HasRoomFor(unit);

    /// <summary>
    /// Row 152 Operating automatic machine (AI slot 21 <c>FUN_14059df00</c>, <b>Verified</b>): of a machine, 1.0 when it takes the character as an
    /// operator (vfunc <c>0x500</c> <c>FUN_1400f7c90</c>: already one, or fewer than its <c>max operators</c>), its output is full (slot 175, state
    /// 63), the character carries one of its inputs (vfunc <c>0x178</c>) or row 94's finder finds one (<c>FUN_14059d9b0</c> through the AI's cache
    /// <c>FUN_14060f1d0</c>, <see cref="Production.FillSources.Find"/>); else 0. 0 for any other target.
    /// </summary>
    float AutomaticDesire(World world, in RowAsk ask)
    {
        if (ask.Target.Fixture is not { } site || world.Machines.Find(site) is not { } machine) return 0;
        if (Production.ProductionSystem.TakesOperator(world, ask.Cold, machine) || machine.OutputBlocked) return 1;
        if (Production.ProductionSystem.CarriesInputOf(ask.Cold, machine)) return 1;

        var at = world.Characters.Previous[ask.Slot].Position;
        return Production.FillSources.Find(world, _ai.Data, machine, ask.Cold, at, []) is not null ? 1 : 0;
    }

    /// <summary>
    /// Row 247 Collecting resource (AI slot 23 <c>FUN_14059a110</c>, <b>Verified</b>): 1.0 when the character has room (<c>FUN_1405cb520</c>) for a
    /// unit of the target's item (an item's record; a building's first stored item, which Meitou's storage targets name), else 0.
    /// </summary>
    float RoomDesire(in RowAsk ask) =>
        ask.Target.Record is { } record && RoomFor(ask.Cold, record) ? 1 : 0;

    #endregion
}
