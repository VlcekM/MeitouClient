using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.FirstAid;

/// <summary>
/// The medic finders of the original (docs/game/ai-tasks.md "Medic jobs"): <c>FUN_14099f690</c> for the Medic (58) and Robotics (57) jobs and the NPC
/// <c>Medic</c> goal, <c>FUN_14099e240</c> for Splint Rigging (250). Both pick the patient with the highest score = need x nearness (x the relation
/// factor, x 0.05 for a patient another medic already works on); the medic itself is a candidate at its plain need. The give-up list
/// (<c>AITaskSystem.pathFailures</c>, +0x218: targets the AI could not path to, 20 s) is <see cref="CharacterCold.Ignored"/>, filled by the combat
/// chase and by the walk to a patient (<see cref="Schedule"/>). Engine stand-ins, because Meitou has no sensed list and no town walls: "sensed" is
/// the medic's sight range, "seen or heard" always holds, the blackboard's marked-for-death cage is never set, "the same town and building" is
/// <see cref="SightMap.LocationOf"/>.
/// </summary>
public sealed partial class FirstAidSystem
{
    #region The sweep

    /// <summary>
    /// Leave a full cell of slack for each actor: both the medic and candidate can move between the grid snapshot and the positions scored by
    /// this finder.
    /// </summary>
    const float PatientGridMargin = 200;
    /// <summary>
    /// The most a character may have moved since the grid snapshot for the margin to hold (each of the medic and the patient: 50 + 50 inside the
    /// 200).
    /// </summary>
    const float PatientGridJump = 50;
    /// <summary>
    /// A scratch list of the grid's patient candidates.
    /// </summary>
    readonly List<int> _patientCandidates = [];

    // What one automatic sweep (SlowWorld) works out once instead of for every medic: nothing it reads changes while the sweep runs (a treatment only
    // sets a task).
    /// <summary>
    /// Whether a sweep is running (<see cref="BeginSweep"/> to <see cref="EndSweep"/>).
    /// </summary>
    bool _sweeping;
    /// <summary>
    /// The factions that own a cage, found once per sweep.
    /// </summary>
    readonly HashSet<int> _sweepCageFactions = [];

    /// <summary>
    /// Every character's organic and robot need (AidNeed) by slot * 2 + robot, and the slots with either above 0 in slot order.
    /// </summary>
    float[] _sweepNeeds = [];
    /// <summary>
    /// The slots with a need above 0, in slot order (see <see cref="_sweepNeeds"/>).
    /// </summary>
    readonly List<int> _sweepNeedy = [];

    /// <summary>
    /// Starts a sweep: every character's first-aid needs and the ones with a need (the passes skip a patient whose <see cref="KitNeed"/> is 0, so
    /// these are a medic's only candidates: one pass over the table instead of a grid query per medic) and the factions that own a cage (one pass
    /// over the fixtures instead of one per medic).
    /// </summary>
    internal void BeginSweep(World world)
    {
        var table = world.Characters;
        if (_sweepNeeds.Length < table.HighWater * 2) _sweepNeeds = new float[table.HighWater * 2];
        var next = table.Next;
        _sweepNeedy.Clear();
        for (int s = 0; s < table.HighWater; s++)
        {
            float organic = 0, robot = 0;
            if (next[s].Alive && table.Cold(s) is { } c) (organic, robot) = AidNeeds(c);
            (_sweepNeeds[s * 2], _sweepNeeds[s * 2 + 1]) = (organic, robot);
            if (organic > 0 || robot > 0) _sweepNeedy.Add(s);
        }

        _sweepCageFactions.Clear();
        for (int k = 0; k < world.Fixtures.All.Count; k++)
            if (world.Fixtures.All[k] is { Kind: FixtureKind.Cage } cage) _sweepCageFactions.Add(cage.Owner);
        _sweeping = true;
    }

    /// <summary>
    /// Ends the sweep: the needs are worked out per medic again.
    /// </summary>
    internal void EndSweep() => _sweeping = false;

    /// <summary>
    /// Collects the slots a grid query finds.
    /// </summary>
    struct CandidateCollector(List<int> slots)
    {
        /// <summary>
        /// The slots found.
        /// </summary>
        public List<int> Slots = slots;
        /// <summary>
        /// Adds a found slot.
        /// </summary>
        public static void Add(int slot, ref CandidateCollector collector) => collector.Slots.Add(slot);
    }

    #endregion

    #region The pieces

    /// <summary>
    /// The square of the distance at which a patient weighs nothing (<c>FUN_140998b90</c>: 1 - d^2 / 3397386.5, about 1843 units, <b>Verified</b>
    /// constant, clamped to [floor, 1]); the floor is 0.4 for a patient in the medic's own building and 0 otherwise.
    /// </summary>
    public const float PatientFalloff2 = 3397386.5f;

    /// <summary>
    /// The nearness floor for a patient in the medic's building (<c>DAT_141696c90</c>, <b>Verified</b>).
    /// </summary>
    const float SameBuildingNearness = 0.4f;
    /// <summary>
    /// The hold-position leash: a medic on Hold weighs a patient farther than this (squared, <c>DAT_1416d4a28</c> 1600) from its hold point at 0.
    /// </summary>
    const float HoldLeash2 = 1600;
    /// <summary>
    /// The least score another faction's patient needs (<c>DAT_14168a318</c> as a double, <b>Verified</b>).
    /// </summary>
    const float StrangerMinScore = 0.1f;
    /// <summary>
    /// The weight of a patient another medic already works on (<c>DAT_141696c74</c>, <b>Verified</b>).
    /// </summary>
    const float TakenWeight = 0.05f;
    /// <summary>
    /// The least score at which a patient already on the sub-target list as this medic's own is taken at once (<c>DAT_14168c9f8</c> as a double,
    /// <b>Verified</b>).
    /// </summary>
    const float StickyMinScore = 0.001f;
    /// <summary>
    /// The floor of the score returned for such a patient (<c>DAT_1416bc15c</c> 0.95f, <b>Verified</b>).
    /// </summary>
    const float StickyFloor = 0.95f;
    /// <summary>
    /// The weight of a caged prisoner of the pass for owned cages (<c>DAT_141696c80</c>, <b>Verified</b>).
    /// </summary>
    const float CagedWeight = 0.25f;
    /// <summary>
    /// The splint finder's need weight: <c>needsSplint</c> (<c>FUN_140646400</c>) returns 0.1 (<c>DAT_14167fad0</c>) when a part qualifies, -0.1 for
    /// a dead patient, else 0 (<b>Verified</b> by disassembly).
    /// </summary>
    const float SplintWeight = 0.1f;

    /// <summary>
    /// The live relation table; null: no faction is allied, hostile or related to another (a patient of another faction scores the 0.05 minimum).
    /// </summary>
    public FactionRelations? Relations { get; set; }

    /// <summary>
    /// The squared speed of a character on the ground plane.
    /// </summary>
    static float Speed2(in CharacterHot a) => a.Velocity.X * a.Velocity.X + a.Velocity.Z * a.Velocity.Z;

    /// <summary>
    /// "A regards B as friendly" (Character slot 126, <c>isAlly</c> <c>FUN_1407923d0</c>): the dialogue system's rule (same faction, squad memory
    /// tags, faction allies); without it the same faction or allied factions.
    /// </summary>
    bool Allied(World world, int a, int b)
    {
        if (world.SystemOf<Dialogue.DialogueSystem>() is { } dialogue)
            return CharacterFacts.RegardsFriendly(dialogue, world, a, b);

        var (ac, bc) = (world.Characters.Cold(a)!, world.Characters.Cold(b)!);
        return ac.Faction == bc.Faction || Relations is { } r && r.Contains(ac.Faction) && r.Contains(bc.Faction) && r.IsAlly(ac.Faction, bc.Faction);
    }

    /// <summary>
    /// "A regards B as an enemy" (slot 125, <c>isEnemy</c> <c>FUN_14079c950</c>): the squad memory's TEMP_ENEMY / TEMP_ALLY tags first, else another
    /// faction hostile to it (relation -30 or less).
    /// </summary>
    bool EnemyOf(World world, int a, int b)
    {
        var (ac, bc) = (world.Characters.Cold(a)!, world.Characters.Cold(b)!);
        if (ac.Faction == bc.Faction) return false;
        if (world.SystemOf<Dialogue.DialogueSystem>()?.EnemyMemory(world, a, b) is { } memory and not 0)
            return memory > 0;

        return Relations is { } r && r.Contains(ac.Faction) && r.Contains(bc.Faction) && AggroSystem.IsEnemy(r, ac.Faction, bc.Faction);
    }

    /// <summary>
    /// The medic's <c>FUN_14059a670</c> (DC 55 I_SHOULD_HELP_THIS_GUY): same NPC class unless a bandit or traitorous, friendly, both honourable,
    /// loved; never hated.
    /// </summary>
    bool Helps(World world, int a, int b)
    {
        if (world.SystemOf<Dialogue.DialogueSystem>() is { } dialogue) return dialogue.Helps(world, a, b);
        var (ac, bc) = (world.Characters.Cold(a)!, world.Characters.Cold(b)!);
        return ac.NpcClass == bc.NpcClass && ac.NpcClass != FundamentalType.Bandit || Allied(world, a, b);
    }

    /// <summary>
    /// The medic squad's blackboard has the cage marked for death (<c>Blackboard.cageMarkedForDeath</c>, tested by <c>FUN_14032eb40</c>): a Kill or
    /// Eat job of one of the squad's members runs on it (<see cref="Kidnap.CaptiveSystem.Jobs"/>).
    /// </summary>
    static bool MarkedForDeath(World world, int squad, string cage)
    {
        if (squad < 0 || world.SystemOf<Kidnap.CaptiveSystem>() is not { } captives) return false;
        foreach (var j in captives.Jobs)
        {
            if (j.Kind is Kidnap.CaptiveJobKind.Kill or Kidnap.CaptiveJobKind.Eat && j.Cage == cage
                    && world.Characters.Cold(j.Worker.Slot) is { } worker && worker.SquadId == squad)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A chained slave of the medic's faction (<c>FUN_14032e2a0</c>: chained, owner faction the medic's).
    /// </summary>
    internal static bool OwnedChained(CharacterCold medic, CharacterCold patient) => patient.SlaveState == 1
            && LockSystem.WornShackles(patient) is not null && patient.SlaveOwner == medic.Faction;

    /// <summary>
    /// A patient chained to this very medic (<c>FUN_14032e1e0</c>: the chain flag and the chain's owner is the medic; Meitou's owner is
    /// <see cref="Kidnap.ObedientSlave.OwnerOf"/>).
    /// </summary>
    static bool ChainedTo(World world, CharacterId medic, CharacterCold patient) =>
        LockSystem.WornShackles(patient) is not null && Kidnap.ObedientSlave.OwnerOf(world, patient) == medic;

    /// <summary>
    /// A chained slave on the medic squad's slave list (pass 3's candidates, <c>FUN_14099f690</c>: the platoon's ownerships): a slave member of the
    /// squad or one that obeys it (<see cref="CharacterCold.SlaveMaster"/>). Stand-in: <c>SlaveMaster</c> is runtime only, so a slave with no known
    /// master (after a load) counts for every squad of its owner faction.
    /// </summary>
    static bool SquadSlave(CharacterCold medic, CharacterCold slave) =>
        slave.SlaveState == 1 && LockSystem.WornShackles(slave) is not null
        && (medic.SquadId >= 0 && (slave.SlaveMaster == medic.SquadId || slave.SquadId == medic.SquadId) || slave.SlaveMaster < 0
                && slave.SlaveOwner == medic.Faction);

    /// <summary>
    /// The patient's need score for a kit type (<see cref="MedicalState.FirstAidNeed"/>); 0 for a body without a medical state.
    /// </summary>
    (float Organic, float Robot) AidNeeds(CharacterCold c) =>
        c.Medical is { } med && c.Race is { } race
            ? med.FirstAidNeeds(race,
                    MathF.Max(MedicalState.BloodCapacity(race, c.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1),
                            Constants.ExtraBloodLossFromBodyparts)
            : (0, 0);

    /// <summary>
    /// A character's first-aid need for an organic or a robot kit (<paramref name="robotKit"/>); 0 without a medical state or race.
    /// </summary>
    float AidNeed(CharacterCold c, bool robotKit) =>
        c.Medical is { } med && c.Race is { } race
            ? med.FirstAidNeed(race, robotKit,
                    MathF.Max(MedicalState.BloodCapacity(race, c.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1),
                            Constants.ExtraBloodLossFromBodyparts)
            : 0;

    /// <summary>
    /// What a patient needs of this medic (<c>FUN_140998200</c>, <b>Verified</b>): 0 while the patient fights; the organic need counts with an
    /// organic kit, the robot need with a robot kit (both without kits when the medic is not of the player's and the patient is imprisoned or its
    /// chained slave); a patient of another faction x 0.8 when allied, else x relation / 100, at least 0.05 (the original's further floor of 0.01 for
    /// a player medic needing a current task sits behind that minimum and never applies).
    /// </summary>
    float Need(World world, int medicSlot, int patientSlot, bool organicKit, bool robotKit)
    {
        CharacterCold medic = world.Characters.Cold(medicSlot)!, patient = world.Characters.Cold(patientSlot)!;
        if (patient.InCombat) return 0;
        float need = KitNeed(medic, patientSlot, patient, organicKit, robotKit);
        if (medic.Faction != patient.Faction)
        {
            float factor = Allied(world, medicSlot, patientSlot) ? 0.8f : Relations is { } r && r.Contains(medic.Faction)
                    && r.Contains(patient.Faction) ? r.Get(medic.Faction, patient.Faction) * 0.01f : 0;
            need *= MathF.Max(factor, TakenWeight);
        }

        return need;
    }

    /// <summary>
    /// <see cref="Need"/> before the fight test and the faction factor (at least 0.05, so it never turns a need to 0): the passes test it before the
    /// relation tests, which cost far more and drop nobody that a need of 0 would keep. A sweep reads each patient's two needs once.
    /// </summary>
    float KitNeed(CharacterCold medic, int patientSlot, CharacterCold patient, bool organicKit, bool robotKit)
    {
        bool kitless = !medic.IsPlayer && (patient.IsImprisoned || OwnedChained(medic, patient));
        float need = 0;
        if (kitless || organicKit) need += SweepNeed(patientSlot, patient, false);
        if (kitless || robotKit) need += SweepNeed(patientSlot, patient, true);
        return need;
    }

    /// <summary>
    /// A character's need for the kit: the sweep's when a sweep is running, else worked out.
    /// </summary>
    float SweepNeed(int slot, CharacterCold patient, bool robotKit)
    {
        return _sweeping ? _sweepNeeds[slot * 2 + (robotKit ? 1 : 0)] : AidNeed(patient, robotKit);
    }

    /// <summary>
    /// The nearness of a patient (<c>FUN_140998b90</c>, <b>Verified</b>): 0 when the medic holds position and the patient is over 40 units from its
    /// hold point; else 1 - d^2 / <see cref="PatientFalloff2"/> (3D), at most 1, at least 0.4 when both are in one building and 0 otherwise.
    /// </summary>
    float Nearness(World world, int medic, CharacterCold medicCold, int patient)
    {
        var next = world.Characters.Next;
        var d = next[medic].Position - next[patient].Position;
        float d2 = d.LengthSquared();
        if ((medicCold.Orders & CharacterOrders.Hold) != 0 && d2 > HoldLeash2)
        {
            var hold = Orders.HoldPointOf(medicCold, next[medic].Position);
            if (Vector3.DistanceSquared(hold, next[patient].Position) > HoldLeash2) return 0;
        }

        float floor = world.Sight.PlaceOf(medic) is >= 0 and var place && place == world.Sight.PlaceOf(patient) ? SameBuildingNearness : 0;
        return MathF.Max(MathF.Min(1 - d2 / PatientFalloff2, 1), floor);
    }

    /// <summary>
    /// The squad mates working on a patient other than the medic itself and the patient (<c>Blackboard::howManyGuysWorkingOnThisSubTarget</c>,
    /// <c>FUN_1402690e0</c>, <see cref="SquadGoals"/>).
    /// </summary>
    static int OthersOn(CharacterTable table, SquadGoals taken, int medic, int patient) =>
        taken.Working(table.Cold(medic)!.SquadId, table.IdOf(medic), GoalTarget.Of(table.IdOf(patient)));

    /// <summary>
    /// The patient is being eaten (<c>isGettingEaten</c>, +0x279 &amp; 7): passes 1, 2 and 4 of the medic finder skip it; pass 3, pass 5 and the
    /// splint finder do not.
    /// </summary>
    static bool Eaten(CharacterCold patient) => patient.Medical is { EatenCountdown: > 0 };

    /// <summary>
    /// The patients being treated now, in the squad's goal list (<see cref="SquadGoals"/>).
    /// </summary>
    void IGoalSource.Publish(World world, SquadGoals goals)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Treat && table.Cold(i) is { TreatTarget.IsNone: false } c
                    && table.TryResolveNext(c.TreatTarget, out int busy))
                goals.Add(c.SquadId, table.IdOf(i), GoalTarget.Of(table.IdOf(busy)));
        }
    }

    #endregion

    #region The finder

    /// <summary>
    /// The Medic / Robotics finder (<c>FUN_14099f690</c>; 58 passes <paramref name="robotics"/> false, 57 true) and the Medic button's one look.
    /// Gates: awake, not carried and not fighting; an NPC also waits while its squad fights or it carries someone (stand-ins for the goal's desire
    /// <c>FUN_140998410</c>: 0.01 with enemies about, 0.1 while carrying someone, else 1). Returns the best patient's slot and score, -1 for none. A
    /// patient whose (medic, patient) pair is already on the sub-target list (<paramref name="taken"/>) is returned at once from pass 1 or 2 with a
    /// score of at least 0.95; passes 1, 2 and 4 skip a patient being eaten.
    /// <list type="number">
    /// <item><b>Allies</b> (sensed allies, not dead): not a robot race unless robotics; not carried by an enemy of the medic; not fighting; an NPC
    /// medic skips the maimed (<see cref="MedicalState.Maimed"/>). Score =
    /// <see cref="Need"/> x <see cref="Nearness"/>; the same faction always counts, another one at 0.1 and, for a player medic, only with Heal
    /// Allies; x 0.05 when another medic works on it. The medic itself scores its plain need (a robot race only for Robotics). The highest wins.</item>
    /// <item><b>Strangers</b>, when pass 1 found nobody: sensed neutral, knocked-out, imprisoned or crawling ones that are no enemy; not an animal or
    /// a robot race, not of a not-real faction, not fighting. A free one needs the medic's faction to <c>heal strangers</c>, to be down and to pass
    /// <c>FUN_14059a670</c> (same NPC class unless bandits, or allied); a prisoner needs a cage of the medic's faction and not to be a slave; a
    /// chained slave of the medic's faction counts too. Same building and town; a patient another medic works on is skipped. Meitou-only: a player
    /// medic with Heal Neutrals takes any free downed stranger not carried by an enemy, with Heal All enemies too.</item>
    /// <item><b>The squad's chained slaves</b> in sight (<see cref="SquadSlave"/>), <b>carried</b> patients of the medic's faction (outdoors), then
    /// <b>prisoners in the cages</b> of the medic's faction in its place at a flat 0.25.</item>
    /// </list>
    /// </summary>
    (int Slot, float Score) FindPatient(World world, int i, SquadGoals taken, bool robotics, bool ordered = false)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (!MayLook(world, cold, ordered)) return (-1, 0);

        bool organic = HoldsKit(cold, ItemInfo.FirstAidKitFunction), robotic = HoldsKit(cold, ItemInfo.RobotKitFunction);
        bool healAllies = (world.Player.SquadAi & SquadAi.Heal) != 0;
        bool healAll = cold.IsPlayer && (world.Player.SquadAi & SquadAi.HealAll) != 0, healNeutrals = healAll || cold.IsPlayer
                && (world.Player.SquadAi & SquadAi.HealNeutrals) != 0;
        bool heals = Relations is { } rel && rel.Contains(cold.Faction) && rel.Factions[cold.Faction].HealsStrangers;
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight), range2 = range * range;

        // the range gate first in both passes: it is the cheapest test and drops nearly every character of the big save before the path, ally and
        // enemy tests
        var origin = next[i].Position;
        var look = new MedicLook(world, i, cold, organic, robotic, robotics, origin, range2, Candidates(world, i, origin, range));
        int best = -1;
        float bestScore = 0;

        // Pass 1: allies.
        if ((organic || robotic) && Allies(look, taken, healAllies, ref best, ref bestScore)) return (best, bestScore);

        float self = cold.Race!.IsRobot && !robotics ? 0 : Need(world, i, i, organic, robotic);
        if (bestScore < self)
        {
            best = i;
            bestScore = self;
        }

        if (best >= 0) return (best, bestScore);

        // Pass 2: strangers, prisoners, chained slaves.
        bool ownsCages = OwnsCages(world, cold);
        if ((heals || ownsCages || organic || robotic) && Strangers(look, taken, heals, healAll, healNeutrals, ref best, ref bestScore))
            return (best, bestScore);

        if (best >= 0) return (best, bestScore);

        // Pass 3: the squad's own chained slaves in sight.
        SquadSlaves(look, ref best, ref bestScore);

        // Pass 4: someone of its faction carries a patient, outdoors only.
        if (best < 0 && world.Sight.PlaceOf(i) < 0) CarriedPatients(look, ref best, ref bestScore);

        // Pass 5 (FUN_14099f690, Verified): in its squad's home building (its location, on top included), the prisoners in that building's cages,
        // flat weight; no other test on them.
        CagedPrisoners(look, ref best, ref bestScore);

        return best >= 0 ? (best, bestScore) : (-1, 0);
    }

    /// <summary>
    /// One finder run's medic and what its passes share: its kits, its sight range (squared) and the characters to test.
    /// </summary>
    readonly record struct MedicLook(World World, int Slot, CharacterCold Cold, bool Organic, bool Robotic, bool Robotics, Vector3 Origin,
        float Range2, List<int> Candidates);

    /// <summary>
    /// The finder's gates on the medic: able, not fighting (unless ordered), free; an NPC also not while its squad fights or it carries someone.
    /// </summary>
    bool MayLook(World world, CharacterCold cold, bool ordered)
    {
        var table = world.Characters;
        // A character carried awake treats no one, itself included, as with orders (engine choice, docs/simulation.md).
        if (!cold.CarriedBy.IsNone) return false;

        if (cold.Medical is not { Incapacitated: false } own || cold.Race is null || cold.Stats is null || cold.InCombat && !ordered
                || cold.IsImprisoned || !TaskStart.Eligible(world, cold, ordered ? TaskType.FirstAidOrder : TaskType.JobMedic))
            return false;

        // No fight in the squad: an NPC's stand-in for its goal's desire (FUN_140998410 is 0.01 with enemies about). The finder has no such test and
        // the job list keeps any job whose desire is above 0 (FUN_14050e5b0), so a player's medic job and Medic order treat a downed mate while the
        // squad fights.
        if (!cold.IsPlayer && cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && CharacterFacts.SquadFighting(table, squad))
            return false;

        // Nor while it carries someone (the goal's 0.1 then): the carrying goal (a kidnap, a rescue) keeps it, so a carrier does not walk off after a
        // patient with the body.
        if (!cold.IsPlayer && !cold.Carrying.IsNone) return false;

        return true;
    }

    /// <summary>
    /// The characters the passes test: the sweep's needy, else the grid's near ones (with those it cannot place), else every slot.
    /// </summary>
    List<int> Candidates(World world, int i, Vector3 origin, float range)
    {
        var table = world.Characters;
        var next = table.Next;
        List<int> candidates = _patientCandidates;
        var previous = table.Previous;

        // The grid is last tick's snapshot: a medic that spawned or jumped since (a teleport into a cage) is searched by the full scan.
        bool useGrid = previous[i].Alive && (previous[i].Position - origin).LengthSquared() <= PatientGridJump * PatientGridJump;

        // A sweep's candidates are the characters with a need (in slot order, as the grid's): the passes test the range on each.
        if (_sweeping) candidates = _sweepNeedy;
        else if (useGrid)
        {
            _patientCandidates.Clear();
            var collector = new CandidateCollector(_patientCandidates);
            world.Grid.Query(previous[i].Position.X, previous[i].Position.Z, range + PatientGridMargin, ref collector, CandidateCollector.Add);

            // What the snapshot cannot place: a character spawned, removed or moved farther than the margin allows since it was taken (spawns,
            // teleports into a cage or an interior) is added by its slot; every other one moved less than PatientGridJump, inside the margin.
            for (int s = 0; s < previous.Length; s++)
            {
                if (previous[s].Alive != next[s].Alive
                        || (previous[s].Position - next[s].Position).LengthSquared() > PatientGridJump * PatientGridJump)
                    _patientCandidates.Add(s);
            }

            _patientCandidates.Sort();
            for (int k = _patientCandidates.Count - 1; k > 0; k--)
                if (_patientCandidates[k] == _patientCandidates[k - 1]) _patientCandidates.RemoveAt(k);
        }
        else
        {
            _patientCandidates.Clear();
            for (int s = 0; s < table.HighWater; s++) _patientCandidates.Add(s);
        }

        return candidates;
    }

    /// <summary>
    /// Whether a character is out of every pass: the medic itself, dead or gone, without a race; or one the medic found no path to lately
    /// (AITaskSytem::isPathImpossible, <c>pathFailures</c> +0x218, FUN_1405b0290).
    /// </summary>
    static bool Skip(in MedicLook look, int s, CharacterCold other) => s == look.Slot || !look.World.Characters.Next[s].Alive
        || PursuitSystem.PathImpossible(look.Cold, look.World.Characters.IdOf(s), look.World.Tick) || other.Medical is not { Dead: false }
        || other.Race is null;

    /// <summary>
    /// The squared distance from the medic to a character.
    /// </summary>
    static float Dist2(in MedicLook look, int s)
    {
        var table = look.World.Characters;
        var d = table.Next[s].Position - table.Next[look.Slot].Position;
        return d.X * d.X + d.Y * d.Y + d.Z * d.Z;
    }

    /// <summary>
    /// Whether a character is carried by an enemy of the medic.
    /// </summary>
    bool CarriedByEnemy(in MedicLook look, CharacterCold other) =>
        !other.CarriedBy.IsNone && look.World.Characters.TryResolveNext(other.CarriedBy, out int c) && look.World.Characters.Cold(c) is { } carrier
            && EnemyOf(look.World, look.Slot, c);

    /// <summary>
    /// Pass 1, the allies; true when the running pair was found, which is then the pick.
    /// </summary>
    bool Allies(in MedicLook look, SquadGoals taken, bool healAllies, ref int best, ref float bestScore)
    {
        var world = look.World;
        var table = world.Characters;
        var next = table.Next;
        int i = look.Slot;
        var cold = look.Cold;
        bool organic = look.Organic;
        bool robotic = look.Robotic;
        bool robotics = look.Robotics;
        float range2 = look.Range2;
        var origin = look.Origin;
        var candidates = look.Candidates;

        foreach (int s in candidates)
        {
            if (!next[s].Alive || (next[s].Position - origin).LengthSquared() > range2 || table.Cold(s) is not { } other || Skip(look, s, other))
                continue;

            if (KitNeed(cold, s, other, organic, robotic) <= 0) continue;   // Need's 0: before the relation tests
            if (!Allied(world, i, s) || EnemyOf(world, i, s) || other.Race!.IsRobot && !robotics || other.InCombat || Eaten(other)
                    || CarriedByEnemy(look, other) || Dist2(look, s) > range2)
                continue;

            if (!cold.IsPlayer && other.Medical!.Maimed) continue;

            // an NPC medic in a building needs the patient seen or heard (FUN_14099f690 :123-128)
            if (!cold.IsPlayer && world.Sight.PlaceOf(i) >= 0 && !SeenOrHeard(world, i, s)) continue;
            float need = Need(world, i, s, organic, robotic);
            if (need <= 0) continue;
            float score = need * Nearness(world, i, cold, s);
            bool same = other.Faction == cold.Faction;
            if (!same && !(score >= StrangerMinScore && (!cold.IsPlayer || healAllies))) continue;

            // the running pair: taken at once, nothing else is looked at
            if (score > StickyMinScore && taken.IsMine(table.IdOf(i), GoalTarget.Of(table.IdOf(s))))
            {
                best = s;
                bestScore = MathF.Max(score, StickyFloor);
                return true;
            }

            if (OthersOn(table, taken, i, s) > 0) score *= TakenWeight;
            if (score > bestScore)
            {
                best = s;
                bestScore = score;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the medic's faction owns a cage (the sweep's set, else a look through the fixtures).
    /// </summary>
    bool OwnsCages(World world, CharacterCold cold)
    {
        bool ownsCages = false;
        if (_sweeping) ownsCages = _sweepCageFactions.Contains(cold.Faction);
        else
        {
            for (int k = 0; k < world.Fixtures.All.Count; k++)
            {
                if (world.Fixtures.All[k] is { Kind: FixtureKind.Cage } cage && cage.Owner == cold.Faction)
                {
                    ownsCages = true;
                    break;
                }
            }
        }

        return ownsCages;
    }

    /// <summary>
    /// Pass 2, the strangers, prisoners and chained slaves; true when the running pair was found, which is then the pick.
    /// </summary>
    bool Strangers(in MedicLook look, SquadGoals taken, bool heals, bool healAll, bool healNeutrals, ref int best, ref float bestScore)
    {
        var world = look.World;
        var table = world.Characters;
        var next = table.Next;
        int i = look.Slot;
        var cold = look.Cold;
        bool organic = look.Organic;
        bool robotic = look.Robotic;
        bool robotics = look.Robotics;
        float range2 = look.Range2;
        var origin = look.Origin;
        var candidates = look.Candidates;

        foreach (int s in candidates)
        {
            if (!next[s].Alive || (next[s].Position - origin).LengthSquared() > range2 || table.Cold(s) is not { } other || Skip(look, s, other))
                continue;

            if (KitNeed(cold, s, other, organic, robotic) <= 0) continue;   // Need's 0: before the relation tests
            if (other.Race!.IsRobot && !robotics || other.Animal is not null || Eaten(other) || other.InCombat || Dist2(look, s) > range2)
                continue;

            bool enemy = EnemyOf(world, i, s);
            if (enemy && !healAll) continue;
            if (Relations is { } r2 && r2.Contains(other.Faction) && r2.Factions[other.Faction].NotReal) continue;
            bool down = other.Medical!.Incapacitated || !other.CarriedBy.IsNone;
            if (Allied(world, i, s) && !down && !other.IsImprisoned) continue;   // sensed as an ally only: pass 1's
            bool ok;

            // Heal Neutrals / Heal All (Meitou-only): no faction, class or relation test
            if (!other.IsImprisoned && healNeutrals && down && !CarriedByEnemy(look, other)) ok = true;
            else if (enemy) continue;
            else if (!other.IsImprisoned)
            {
                if (other.NpcClass == FundamentalType.Bandit && other.NpcClass != cold.NpcClass) continue;
                if (!Helps(world, i, s)) continue;   // FUN_14059a670
                ok = heals && down;
            }
            else
            {
                var (cage, owned) = CageOwnedBy(world, s, cold.Faction, HomeKey(world, cold));
                ok = other.SlaveState != 1 && !(cage is not null && MarkedForDeath(world, cold.SquadId, cage)) && (cage is null || owned);
            }

            // Chained to this very medic: accepted below slave state 2 whatever the rules above said; any other needs them
            // (FUN_14099f690:273-283, Verified).
            if (ChainedTo(world, table.IdOf(i), other) ? other.SlaveState >= 2 : !ok) continue;
            if (!other.IsImprisoned && !SeenOrHeard(world, i, s)) continue;   // a free stranger must be seen or heard (:247-248)

            // the location hands, on top included (FUN_14099f690, Verified)
            if (world.Sight.LocationOf(i) != world.Sight.LocationOf(s)) continue;
            float need = Need(world, i, s, organic, robotic);
            if (need <= 0) continue;
            float score = need * Nearness(world, i, cold, s);
            if (score > StickyMinScore && taken.IsMine(table.IdOf(i), GoalTarget.Of(table.IdOf(s))))
            {
                best = s;
                bestScore = MathF.Max(score, StickyFloor);
                return true;
            }

            if (OthersOn(table, taken, i, s) > 0 || score <= bestScore) continue;
            best = s;
            bestScore = score;
        }

        return false;
    }

    /// <summary>
    /// Pass 3, the squad's own chained slaves in sight.
    /// </summary>
    void SquadSlaves(in MedicLook look, ref int best, ref float bestScore)
    {
        var world = look.World;
        var table = world.Characters;
        var next = table.Next;
        int i = look.Slot;
        var cold = look.Cold;
        bool organic = look.Organic;
        bool robotic = look.Robotic;
        bool robotics = look.Robotics;
        float range2 = look.Range2;
        var candidates = look.Candidates;

        foreach (int s in candidates)
        {
            if (!next[s].Alive || Dist2(look, s) > range2 || table.Cold(s) is not { } other || Skip(look, s, other) || !SquadSlave(cold, other))
                continue;

            if (KitNeed(cold, s, other, organic, robotic) <= 0) continue;
            if (other.Race!.IsRobot && !robotics || other.InCombat) continue;
            float need = Need(world, i, s, organic, robotic);
            if (need <= 0) continue;
            float score = need * Nearness(world, i, cold, s);
            if (score > bestScore)
            {
                best = s;
                bestScore = score;
            }
        }
    }

    /// <summary>
    /// Pass 4, the patients someone of the medic's faction carries (the caller holds it to outdoors).
    /// </summary>
    void CarriedPatients(in MedicLook look, ref int best, ref float bestScore)
    {
        var world = look.World;
        var table = world.Characters;
        var next = table.Next;
        int i = look.Slot;
        var cold = look.Cold;
        bool organic = look.Organic;
        bool robotic = look.Robotic;
        bool robotics = look.Robotics;
        float range2 = look.Range2;
        var candidates = look.Candidates;

        foreach (int s in candidates)
        {
            if (!next[s].Alive || Dist2(look, s) > range2 || table.Cold(s) is not { } other || Skip(look, s, other) || other.CarriedBy.IsNone
                    || other.Animal is not null || Eaten(other))
                continue;

            if (KitNeed(cold, s, other, organic, robotic) <= 0) continue;
            if (other.Race!.IsRobot && !robotics) continue;
            if (!table.TryResolveNext(other.CarriedBy, out int c) || table.Cold(c)?.Faction != cold.Faction)
                continue;

            if (!SeenOrHeard(world, i, s)) continue;   // :363-365
            float need = Need(world, i, s, organic, robotic);
            if (need <= 0) continue;
            float score = need * Nearness(world, i, cold, s);
            if (score > bestScore)
            {
                best = s;
                bestScore = score;
            }
        }
    }

    /// <summary>
    /// Pass 5, the prisoners in the cages of the squad's home building while the medic is in it, at a flat weight.
    /// </summary>
    void CagedPrisoners(in MedicLook look, ref int best, ref float bestScore)
    {
        var world = look.World;
        var table = world.Characters;
        int i = look.Slot;
        var cold = look.Cold;
        bool organic = look.Organic;
        bool robotic = look.Robotic;

        if (HomeKey(world, cold) is { } homeKey && world.Sight.LocationOf(i) is >= 0 and var at && world.Sight.PlaceKey(at) == homeKey)
        {
            foreach (var f in world.Fixtures.OfBuilding(homeKey))
            {
                if (f.Kind != FixtureKind.Cage || FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s)
                        || table.Cold(s) is null)
                    continue;

                float need = Need(world, i, s, organic, robotic) * CagedWeight;
                if (need > bestScore)
                {
                    best = s;
                    bestScore = need;
                }
            }
        }
    }

    /// <summary>
    /// The sensed entry's seen or heard flag (+0x38, +0x39): the medic's sensed list holds the patient and it was sensed at the last look
    /// (<see cref="CharacterFacts.Perceives"/>).
    /// </summary>
    static bool SeenOrHeard(World world, int medic, int patient) => CharacterFacts.Perceives(world, medic, patient, now: true);

    /// <summary>
    /// Whether the patient sits in a cage, and whether that cage stands in the medic squad's home building <paramref name="homeKey"/>
    /// (<c>FUN_14099f690</c>, <b>Verified</b>: the cage's building against the squad's home hand). A squad without a home (the player's: its shared
    /// home is <b>Unknown</b>) falls back to the cage's owner being <paramref name="faction"/>.
    /// </summary>
    static (string? Cage, bool Owned) CageOwnedBy(World world, int patient, int faction, string? homeKey)
    {
        var id = world.Characters.IdOf(patient);
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind == FixtureKind.Cage && f.Occupant == id)
                return (f.Id, homeKey is not null ? f.Building == homeKey : f.Owner == faction);
        }

        return (null, false);
    }

    /// <summary>
    /// The key of the medic squad's home building (the one it was given, else its squad's); null without one.
    /// </summary>
    static string? HomeKey(World world, CharacterCold cold) =>
        world.Squads.Find(cold.SquadId) is { } squad ? (squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key : null;

    /// <summary>
    /// Starts the Medic / Robotics job's (<paramref name="job"/>), an NPC's or the button's treatment of the best patient the finder gives; false
    /// when none. The button (<see cref="TaskSource.Order"/>, row 25) makes a fighting medic look too, and one that finds a patient leaves its fight
    /// (<see cref="TaskStart.Start"/>).
    /// </summary>
    bool HealSomeone(World world, int i, SquadGoals taken, bool robotics, TaskSource source, int job = -1)
    {
        bool ordered = source == TaskSource.Order;
        var (patient, _) = FindPatient(world, i, taken, robotics, ordered);
        if (patient < 0) return false;
        return StartJob(world, i, patient, taken,
                new TaskRequest(CharacterTask.Treat, ordered ? TaskType.FirstAidOrder : robotics ? TaskType.JobRepairRobot : TaskType.JobMedic,
                        source) { Job = job });
    }

    /// <summary>
    /// The Splint Rigging finder (<c>FUN_14099e240</c>, <b>Verified</b>; a pure read): X = min(medic skill, the quality of the splint kit it takes:
    /// its own inventory or backpack, the fewest charges; none: nothing); sensed allies not of a robot race, not carried by an enemy, of the medic's
    /// own faction only, whose <c>needsSplint</c>(X) holds weigh 0.1 x <see cref="Nearness"/> (x 0.05 when another medic works on it); the medic
    /// itself 0.1. A patient the medic already treats (a pair on the sub-target list) is taken at once. Returns the patient's slot, -1 for none.
    /// </summary>
    int FindSplint(World world, int i, SquadGoals taken)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (!cold.CarriedBy.IsNone) return -1;


        // no combat test: the original's finder has none (FUN_14099e240)
        if (cold.Medical is not { Incapacitated: false } own || cold.Race is not { } ownRace || cold.Stats is not { } stats) return -1;
        if (OwnKit(cold, ItemInfo.SplintKitFunction) is not { } kit) return -1;   // the medic's own kit, as the order and the splint use

        // No fight test in the original finder (FUN_14099e240) and the job keeps a positive desire (FUN_140998410): a player's splint job runs during
        // a fight, as the Medic job does.
        if (!cold.IsPlayer && cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && CharacterFacts.SquadFighting(table, squad))
            return -1;

        float skill = MathF.Min(stats[StatsEnumerated.Medic] * own.StatMultiplier(StatsEnumerated.Medic), Items.Info(kit.Item.Record)?.Quality ?? 0);
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight);
        int best = -1;
        float bestScore = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == i || !next[s].Alive || PursuitSystem.PathImpossible(cold, table.IdOf(s), world.Tick)
                    || table.Cold(s) is not { Medical: { Dead: false } med, Race: { } race } other)
                continue;

            if (other.Faction != cold.Faction || race.IsRobot || !med.NeedsSplint(race, skill)) continue;
            if (!other.CarriedBy.IsNone && table.TryResolveNext(other.CarriedBy, out int c) && table.Cold(c) is { } carrier && EnemyOf(world, i, c))
                continue;

            var d = next[s].Position - next[i].Position;
            if (d.LengthSquared() > range * range) continue;
            float score = SplintWeight * Nearness(world, i, cold, s);
            if (score > StickyMinScore && taken.IsMine(table.IdOf(i), GoalTarget.Of(table.IdOf(s)))) return s;   // the running pair is taken at once
            if (OthersOn(table, taken, i, s) > 0) score *= TakenWeight;
            if (score <= bestScore) continue;
            best = s;
            bestScore = score;
        }

        if (bestScore < SplintWeight && own.NeedsSplint(ownRace, skill)) best = i;
        return best;
    }

    /// <summary>
    /// State 22 <c>needsFirstAid</c> (<c>FUN_14099d8f0</c>, <b>Verified</b>): a non-robot patient whose organic first aid need is above
    /// <see cref="MedicalState.AidNeedFloor"/>.
    /// </summary>
    public static bool NeedsFirstAid(World world, CharacterCold c) =>
        c is { Race: { IsRobot: false } race, Medical: { } med } && med.FirstAidNeed(race, false,
            MathF.Max(MedicalState.BloodCapacity(race, c.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1),
                    world.SystemOf<BodySystem>()?.Constants.ExtraBloodLossFromBodyparts ?? 1) > MedicalState.AidNeedFloor;

    /// <summary>
    /// The planner's first step ahead of a lift that wants <c>!22</c> (row 25 First aid, the provider of the unmet state; its list B needs a first
    /// aid kit, state 23, and an arm, 38): the carrier treats the patient, the lift follows once the need is gone. False when it holds no kit or has
    /// no working arm, so the plan fails.
    /// </summary>
    internal static bool TreatBeforeLift(World world, int carrier, int patient)
    {
        var table = world.Characters;
        if (table.Cold(carrier) is not { Medical: { } arms } cold || !HoldsKit(cold, ItemInfo.FirstAidKitFunction)
                || !(arms.ArmWorks(left: true) || arms.ArmWorks(left: false)))
            return false;

        return StartJob(world, carrier, patient, SquadGoals.Of(world), new TaskRequest(CharacterTask.Treat, TaskType.FirstAidOrder, TaskSource.Goal));
    }

    /// <summary>
    /// The Splint Rigging job's treatment (row 250).
    /// </summary>
    static TaskRequest Splinting(int job) => new(CharacterTask.Treat, TaskType.SplintJob, TaskSource.Job) { Job = job };

    /// <summary>
    /// Starts a treatment of <paramref name="patient"/> (<paramref name="request"/> without its target); an NPC medic goes back afterwards to its
    /// standing task (<see cref="TaskStart.Standing"/>).
    /// </summary>
    static bool StartJob(World world, int i, int patient, SquadGoals taken, TaskRequest request)
    {
        var next = world.Characters.Next;
        var resume = TaskStart.ResumeOf(world, i, CharacterTask.Treat, TaskStart.Standing(world.Characters.Cold(i)!, (CharacterTask)next[i].Task));
        if (TaskStart.Start(world, i,
                request with { Target = new CharacterId(patient, next[patient].Generation), Resume = resume }) != StartResult.Started)
            return false;

        var table = world.Characters;
        if (table.Cold(i) is { } medic) taken.Add(medic.SquadId, table.IdOf(i), GoalTarget.Of(table.IdOf(patient)));
        return true;
    }

    #endregion
}
