using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.FirstAid;

/// <summary>
/// The medic finders of the original (docs/game/ai-tasks.md "Medic jobs"): <c>FUN_14099f690</c> for the Medic (58) and Robotics (57) jobs and the NPC <c>Medic</c> goal, <c>FUN_14099e240</c> for
/// Splint Rigging (250). Both pick the patient with the highest score = need x nearness (x the relation factor, x 0.05 for a patient another medic already works on); the medic itself is a candidate
/// at its plain need. Engine stand-ins, all because Meitou has no sensed list, no path-failure list and no town walls: "sensed" is the medic's sight range, "seen or heard" always holds,
/// the give-up list (<c>AITaskSystem.pathFailures</c>, +0x218: targets the AI could not path to, kept 20 s at a time while still unreachable) is empty, the blackboard's marked-for-death cage is never
/// set, "the same town and building" is <see cref="SightMap.PlaceOf"/>.
/// </summary>
public sealed partial class FirstAidSystem
{
    // Leave a full cell of slack for each actor: both the medic and candidate can move between
    // the grid snapshot and the positions scored by this finder.
    const float PatientGridMargin = 200;
    /// <summary>The most a character may have moved since the grid snapshot for the margin to hold (each of the medic and the patient: 50 + 50 inside the 200).</summary>
    const float PatientGridJump = 50;
    readonly List<int> patientCandidates = [];

    // What one automatic sweep (SlowWorld) works out once instead of for every medic: nothing it reads changes while the sweep runs (a treatment only sets a task).
    bool sweeping;
    readonly HashSet<int> sweepCageFactions = [];
    // Every character's organic and robot need (AidNeed) by slot * 2 + robot, and the slots with either above 0 in slot order.
    float[] sweepNeeds = [];
    readonly List<int> sweepNeedy = [];

    /// <summary>
    /// Starts a sweep: every character's first-aid needs and the ones with a need (the passes skip a patient whose <see cref="KitNeed"/> is 0, so these are a medic's only
    /// candidates: one pass over the table instead of a grid query per medic) and the factions that own a cage (one pass over the fixtures instead of one per medic).
    /// </summary>
    internal void BeginSweep(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        if (sweepNeeds.Length < table.HighWater * 2) sweepNeeds = new float[table.HighWater * 2];
        sweepNeedy.Clear();
        for (int s = 0; s < table.HighWater; s++)
        {
            float organic = 0, robot = 0;
            if (next[s].Alive && table.Cold(s) is { } c) (organic, robot) = (AidNeed(c, false), AidNeed(c, true));
            (sweepNeeds[s * 2], sweepNeeds[s * 2 + 1]) = (organic, robot);
            if (organic > 0 || robot > 0) sweepNeedy.Add(s);
        }
        sweepCageFactions.Clear();
        for (int k = 0; k < world.Fixtures.All.Count; k++)
            if (world.Fixtures.All[k] is { Kind: FixtureKind.Cage } cage) sweepCageFactions.Add(cage.Owner);
        sweeping = true;
    }

    internal void EndSweep() => sweeping = false;

    struct CandidateCollector(List<int> slots)
    {
        public List<int> Slots = slots;
        public static void Add(int slot, ref CandidateCollector collector) => collector.Slots.Add(slot);
    }

    /// <summary>The live relation table; null: no faction is allied, hostile or related to another (a patient of another faction scores the 0.05 minimum).</summary>
    public FactionRelations? Relations { get; set; }

    /// <summary>
    /// The square of the distance at which a patient weighs nothing (<c>FUN_140998b90</c>: 1 - d^2 / 3397386.5, about 1843 units, <b>Verified</b> constant, clamped to [floor, 1]); the floor is 0.4 for a
    /// patient in the medic's own building and 0 otherwise.
    /// </summary>
    public const float PatientFalloff2 = 3397386.5f;

    /// <summary>The nearness floor for a patient in the medic's building (<c>DAT_141696c90</c>, <b>Verified</b>).</summary>
    const float SameBuildingNearness = 0.4f;
    /// <summary>The hold-position leash: a medic on Hold weighs a patient farther than this (squared, <c>DAT_1416d4a28</c> 1600) from its hold point at 0.</summary>
    const float HoldLeash2 = 1600;
    /// <summary>The least score another faction's patient needs (<c>DAT_14168a318</c> as a double, <b>Verified</b>).</summary>
    const float StrangerMinScore = 0.1f;
    /// <summary>The weight of a patient another medic already works on (<c>DAT_141696c74</c>, <b>Verified</b>).</summary>
    const float TakenWeight = 0.05f;
    /// <summary>The least score at which a patient already on the sub-target list as this medic's own is taken at once (<c>DAT_14168c9f8</c> as a double, <b>Verified</b>).</summary>
    const float StickyMinScore = 0.001f;
    /// <summary>The floor of the score returned for such a patient (<c>DAT_1416bc15c</c> 0.95f, <b>Verified</b>).</summary>
    const float StickyFloor = 0.95f;
    /// <summary>The weight of a caged prisoner of the pass for owned cages (<c>DAT_141696c80</c>, <b>Verified</b>).</summary>
    const float CagedWeight = 0.25f;
    /// <summary>The splint finder's need weight: <c>needsSplint</c> (<c>FUN_140646400</c>) returns 0.1 (<c>DAT_14167fad0</c>) when a part qualifies, -0.1 for a dead patient, else 0 (<b>Verified</b> by disassembly).</summary>
    const float SplintWeight = 0.1f;

    static float Speed2(in CharacterHot a) => a.Velocity.X * a.Velocity.X + a.Velocity.Z * a.Velocity.Z;

    // ------------------------------------------------------------------ the pieces

    /// <summary>"A regards B as friendly" (Character slot 126, <c>isAlly</c> <c>FUN_1407923d0</c>): the dialogue system's rule (same faction, squad memory tags, faction allies); without it the same faction or allied factions.</summary>
    bool Allied(World world, int a, int b)
    {
        if (world.SystemOf<Dialogue.DialogueSystem>() is { } dialogue) return CharacterFacts.RegardsFriendly(dialogue, world, a, b);
        var (ac, bc) = (world.Characters.Cold(a)!, world.Characters.Cold(b)!);
        return ac.Faction == bc.Faction || Relations is { } r && r.Contains(ac.Faction) && r.Contains(bc.Faction) && r.IsAlly(ac.Faction, bc.Faction);
    }

    /// <summary>"A regards B as an enemy" (slot 125, <c>isEnemy</c> <c>FUN_14079c950</c>): the squad memory's TEMP_ENEMY / TEMP_ALLY tags first, else another faction hostile to it (relation -30 or less).</summary>
    bool EnemyOf(World world, int a, int b)
    {
        var (ac, bc) = (world.Characters.Cold(a)!, world.Characters.Cold(b)!);
        if (ac.Faction == bc.Faction) return false;
        if (world.SystemOf<Dialogue.DialogueSystem>()?.EnemyMemory(world, a, b) is { } memory and not 0) return memory > 0;
        return Relations is { } r && r.Contains(ac.Faction) && r.Contains(bc.Faction) && AggroSystem.IsEnemy(r, ac.Faction, bc.Faction);
    }

    /// <summary>The medic's <c>FUN_14059a670</c> (DC 55 I_SHOULD_HELP_THIS_GUY): same NPC class unless a bandit or traitorous, friendly, both honourable, loved; never hated.</summary>
    bool Helps(World world, int a, int b)
    {
        if (world.SystemOf<Dialogue.DialogueSystem>() is { } dialogue) return dialogue.Helps(world, a, b);
        var (ac, bc) = (world.Characters.Cold(a)!, world.Characters.Cold(b)!);
        return ac.NpcClass == bc.NpcClass && ac.NpcClass != FundamentalType.Bandit || Allied(world, a, b);
    }

    /// <summary>
    /// The medic squad's blackboard has the cage marked for death (<c>Blackboard.cageMarkedForDeath</c>, tested by <c>FUN_14032eb40</c>): a Kill or Eat job of one of the squad's members runs on it
    /// (<see cref="Kidnap.CaptiveSystem.Jobs"/>).
    /// </summary>
    static bool MarkedForDeath(World world, int squad, string cage)
    {
        if (squad < 0 || world.SystemOf<Kidnap.CaptiveSystem>() is not { } captives) return false;
        foreach (var j in captives.Jobs)
            if (j.Kind is Kidnap.CaptiveJobKind.Kill or Kidnap.CaptiveJobKind.Eat && j.Cage == cage && world.Characters.Cold(j.Worker.Slot) is { } worker && worker.SquadId == squad) return true;
        return false;
    }

    /// <summary>A chained slave of the medic's faction (<c>FUN_14032e2a0</c>: chained, owner faction the medic's).</summary>
    internal static bool OwnedChained(CharacterCold medic, CharacterCold patient) => patient.SlaveState == 1 && patient.Shackles is not null && patient.SlaveOwner == medic.Faction;

    /// <summary>
    /// A chained slave on the medic squad's slave list (pass 3's candidates, <c>FUN_14099f690</c>: the platoon's ownerships): a slave member of the squad or one that obeys it (<see cref="CharacterCold.SlaveMaster"/>).
    /// Stand-in: <c>SlaveMaster</c> is runtime only, so a slave with no known master (after a load) counts for every squad of its owner faction.
    /// </summary>
    static bool SquadSlave(CharacterCold medic, CharacterCold slave) =>
        slave.SlaveState == 1 && slave.Shackles is not null
        && (medic.SquadId >= 0 && (slave.SlaveMaster == medic.SquadId || slave.SquadId == medic.SquadId) || slave.SlaveMaster < 0 && slave.SlaveOwner == medic.Faction);

    /// <summary>The patient's need score for a kit type (<see cref="MedicalState.FirstAidNeed"/>); 0 for a body without a medical state.</summary>
    float AidNeed(CharacterCold c, bool robotKit) =>
        c.Medical is { } med && c.Race is { } race
            ? med.FirstAidNeed(race, robotKit, MathF.Max(MedicalState.BloodCapacity(race, c.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1), Constants.ExtraBloodLossFromBodyparts)
            : 0;

    /// <summary>
    /// What a patient needs of this medic (<c>FUN_140998200</c>, <b>Verified</b>): 0 while the patient fights; the organic need counts with an organic kit, the robot need with a robot kit (both without
    /// kits when the medic is not of the player's and the patient is imprisoned or its chained slave); a patient of another faction x 0.8 when allied, else x relation / 100, at least 0.05
    /// (the original's further floor of 0.01 for a player medic needing a current task sits behind that minimum and never applies).
    /// </summary>
    float Need(World world, int medicSlot, int patientSlot, bool organicKit, bool robotKit)
    {
        CharacterCold medic = world.Characters.Cold(medicSlot)!, patient = world.Characters.Cold(patientSlot)!;
        if (patient.InCombat) return 0;
        float need = KitNeed(medic, patientSlot, patient, organicKit, robotKit);
        if (medic.Faction != patient.Faction)
        {
            float factor = Allied(world, medicSlot, patientSlot) ? 0.8f : Relations is { } r && r.Contains(medic.Faction) && r.Contains(patient.Faction) ? r.Get(medic.Faction, patient.Faction) * 0.01f : 0;
            need *= MathF.Max(factor, TakenWeight);
        }
        return need;
    }

    /// <summary>
    /// <see cref="Need"/> before the fight test and the faction factor (at least 0.05, so it never turns a need to 0): the passes test it before the relation tests, which cost far more and
    /// drop nobody that a need of 0 would keep. A sweep reads each patient's two needs once.
    /// </summary>
    float KitNeed(CharacterCold medic, int patientSlot, CharacterCold patient, bool organicKit, bool robotKit)
    {
        bool kitless = !medic.IsPlayer && (patient.IsImprisoned || OwnedChained(medic, patient));
        float need = 0;
        if (kitless || organicKit) need += SweepNeed(patientSlot, patient, false);
        if (kitless || robotKit) need += SweepNeed(patientSlot, patient, true);
        return need;
    }

    float SweepNeed(int slot, CharacterCold patient, bool robotKit)
    {
        return sweeping ? sweepNeeds[slot * 2 + (robotKit ? 1 : 0)] : AidNeed(patient, robotKit);
    }

    /// <summary>
    /// The nearness of a patient (<c>FUN_140998b90</c>, <b>Verified</b>): 0 when the medic holds position and the patient is over 40 units from its hold point; else 1 - d^2 / <see cref="PatientFalloff2"/>
    /// (3D), at most 1, at least 0.4 when both are in one building and 0 otherwise.
    /// </summary>
    float Nearness(World world, int medic, CharacterCold medicCold, int patient)
    {
        var next = world.Characters.Next;
        var d = next[medic].Position - next[patient].Position;
        float d2 = d.LengthSquared();
        if ((medicCold.Orders & CharacterOrders.Hold) != 0 && d2 > HoldLeash2)
        {
            var hold = Orders.HoldPointOf(medicCold, next[medic].Position);
            if (Vector2.DistanceSquared(hold, new Vector2(next[patient].Position.X, next[patient].Position.Z)) > HoldLeash2) return 0;
        }
        float floor = world.Sight.PlaceOf(medic) is >= 0 and var place && place == world.Sight.PlaceOf(patient) ? SameBuildingNearness : 0;
        return MathF.Max(MathF.Min(1 - d2 / PatientFalloff2, 1), floor);
    }

    /// <summary>The medics working on a patient other than itself and the patient (<c>Blackboard::howManyGuysWorkingOnThisSubTarget</c>, <c>FUN_1402690e0</c>).</summary>
    static int OthersOn(List<(int Medic, int Patient)> taken, int medic, int patient)
    {
        int n = 0;
        foreach (var (m, p) in taken)
            if (p == patient && m != medic && m != patient) n++;
        return n;
    }

    /// <summary>The patient is being eaten (<c>isGettingEaten</c>, +0x279 &amp; 7): passes 1, 2 and 4 of the medic finder skip it; pass 3, pass 5 and the splint finder do not.</summary>
    static bool Eaten(CharacterCold patient) => patient.Medical is { EatenCountdown: > 0 };

    /// <summary>The patient's injuries make an NPC medic pass it by (<c>FUN_140644170</c>, <b>Verified</b>): a stump on a leg, or on both arms.</summary>
    static bool Maimed(MedicalState med)
    {
        int arms = 0;
        foreach (var p in med.Parts)
        {
            if (p.Limb != LimbState.Stump) continue;
            if (p.Template.Type == BodyPartType.Leg) return true;
            if (p.Template.Type == BodyPartType.Arm) arms++;
        }
        return arms >= 2;
    }

    /// <summary>The patients being treated now as (medic, patient) slot pairs (the blackboard's sub-target list).</summary>
    static List<(int Medic, int Patient)> Treated(CharacterTable table)
    {
        var taken = new List<(int, int)>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Treat && table.TryResolveNext(table.Cold(i)!.TreatTarget, out int busy)) taken.Add((i, busy));
        return taken;
    }

    // ------------------------------------------------------------------ the finder

    /// <summary>
    /// The Medic / Robotics finder (<c>FUN_14099f690</c>; 58 passes <paramref name="robotics"/> false, 57 true) and the Medic button's one look. Gates: awake and not fighting; an NPC medic
    /// also waits while its squad fights or it carries someone (engine stand-ins for the goal's desire <c>FUN_140998410</c>: 0.01 with enemies about, 0.1 while carrying someone, else 1). Returns the best patient's slot and score, -1 for none.
    /// A patient whose (medic, patient) pair is already on the sub-target list (<paramref name="taken"/>) is returned at once from pass 1 or 2 with a score of at least 0.95; passes 1, 2 and 4 skip a patient being eaten.
    /// <list type="number">
    /// <item><b>Allies</b> (sensed allies, not dead): not a robot race unless robotics; not carried by an enemy of the medic; not fighting; an NPC medic skips the maimed (<see cref="Maimed"/>). Score =
    /// <see cref="Need"/> x <see cref="Nearness"/>; the same faction always counts, another one at 0.1 and, for a player medic, only with Heal Allies; x 0.05 when another medic works on it.
    /// The medic itself scores its plain need. The highest wins.</item>
    /// <item><b>Strangers</b>, when pass 1 found nobody: sensed neutral, knocked-out, imprisoned or crawling ones that are no enemy; not an animal or a robot race, not of a not-real faction, not fighting.
    /// A free one needs the medic's faction to <c>heal strangers</c>, to be down and to pass <c>FUN_14059a670</c> (same NPC class unless bandits, or allied); a prisoner needs a cage of the medic's
    /// faction and not to be a slave; a chained slave of the medic's faction counts too. Same building and town; a patient another medic works on is skipped.
    /// Meitou-only: a player medic with Heal Neutrals takes any free downed stranger not carried by an enemy, with Heal All enemies too.</item>
    /// <item><b>The squad's chained slaves</b> in sight (<see cref="SquadSlave"/>), <b>carried</b> patients of the medic's faction (outdoors), then <b>prisoners in the cages</b> of the medic's faction in its place at a flat 0.25.</item>
    /// </list>
    /// </summary>
    (int Slot, float Score) FindPatient(World world, int i, List<(int Medic, int Patient)> taken, bool robotics, bool ordered = false)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (cold.Medical is not { Incapacitated: false } own || cold.Race is null || cold.Stats is null || cold.InCombat && !ordered || cold.IsImprisoned || !TaskStart.Eligible(world, cold, ordered ? TaskType.FIRST_AID_ORDER : TaskType.JOB_MEDIC)) return (-1, 0);
        // No fight in the squad: an NPC's stand-in for its goal's desire (FUN_140998410 is 0.01 with enemies about). The finder has no such test and the job list keeps any job
        // whose desire is above 0 (FUN_14050e5b0), so a player's medic job and Medic order treat a downed mate while the squad fights.
        if (!cold.IsPlayer && cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && CharacterFacts.SquadFighting(table, squad)) return (-1, 0);
        // Nor while it carries someone (the goal's 0.1 then): the carrying goal (a kidnap, a rescue) keeps it, so a carrier does not walk off after a patient with the body.
        if (!cold.IsPlayer && !cold.Carrying.IsNone) return (-1, 0);
        bool organic = cold.Inventory is { } inv && Best(inv, ItemInfo.FirstAidKitFunction) is not null, robotic = cold.Inventory is { } inv2 && Best(inv2, ItemInfo.RobotKitFunction) is not null;
        bool healAllies = (world.Player.SquadAi & SquadAi.Heal) != 0;
        bool healAll = cold.IsPlayer && (world.Player.SquadAi & SquadAi.HealAll) != 0, healNeutrals = healAll || cold.IsPlayer && (world.Player.SquadAi & SquadAi.HealNeutrals) != 0;
        bool heals = Relations is { } rel && rel.Contains(cold.Faction) && rel.Factions[cold.Faction].HealsStrangers;
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight), range2 = range * range;
        var origin = next[i].Position;   // the range gate first in both passes: it is the cheapest test and drops nearly every character of the big save before the path, ally and enemy tests
        List<int> candidates = patientCandidates;
        var previous = table.Previous;
        // The grid is last tick's snapshot: a medic that spawned or jumped since (a teleport into a cage) is searched by the full scan.
        bool useGrid = previous[i].Alive && (previous[i].Position - origin).LengthSquared() <= PatientGridJump * PatientGridJump;
        // A sweep's candidates are the characters with a need (in slot order, as the grid's): the passes test the range on each.
        if (sweeping) candidates = sweepNeedy;
        else if (useGrid)
        {
            patientCandidates.Clear();
            var collector = new CandidateCollector(patientCandidates);
            world.Grid.Query(previous[i].Position.X, previous[i].Position.Z, range + PatientGridMargin, ref collector, CandidateCollector.Add);
            // What the snapshot cannot place: a character spawned, removed or moved farther than the margin allows since it was taken (spawns, teleports into a
            // cage or an interior) is added by its slot; every other one moved less than PatientGridJump, inside the margin.
            for (int s = 0; s < previous.Length; s++)
                if (previous[s].Alive != next[s].Alive || (previous[s].Position - next[s].Position).LengthSquared() > PatientGridJump * PatientGridJump) patientCandidates.Add(s);
            patientCandidates.Sort();
            for (int k = patientCandidates.Count - 1; k > 0; k--)
                if (patientCandidates[k] == patientCandidates[k - 1]) patientCandidates.RemoveAt(k);
        }
        else
        {
            patientCandidates.Clear();
            for (int s = 0; s < table.HighWater; s++) patientCandidates.Add(s);
        }
        int best = -1;
        float bestScore = 0;

        // A patient the medic found no path to lately is left out (AITaskSytem::isPathImpossible, <c>pathFailures</c> +0x218, FUN_1405b0290).
        bool Skip(int s, CharacterCold other) => s == i || !table.Next[s].Alive || PursuitSystem.PathImpossible(cold, table.IdOf(s), world.Tick) || other.Medical is not { Dead: false } || other.Race is null;
        float Dist2(int s) { var d = table.Next[s].Position - table.Next[i].Position; return d.X * d.X + d.Y * d.Y + d.Z * d.Z; }
        bool CarriedByEnemy(CharacterCold other) => !other.CarriedBy.IsNone && table.TryResolveNext(other.CarriedBy, out int c) && table.Cold(c) is { } carrier && EnemyOf(world, i, c);

        // Pass 1: allies.
        if (organic || robotic)
            foreach (int s in candidates)
            {
                if (!next[s].Alive || (next[s].Position - origin).LengthSquared() > range2 || table.Cold(s) is not { } other || Skip(s, other)) continue;
                if (KitNeed(cold, s, other, organic, robotic) <= 0) continue;   // Need's 0: before the relation tests
                if (!Allied(world, i, s) || EnemyOf(world, i, s) || other.Race!.IsRobot && !robotics || other.InCombat || Eaten(other) || CarriedByEnemy(other) || Dist2(s) > range2) continue;
                if (!cold.IsPlayer && Maimed(other.Medical!)) continue;
                float need = Need(world, i, s, organic, robotic);
                if (need <= 0) continue;
                float score = need * Nearness(world, i, cold, s);
                bool same = other.Faction == cold.Faction;
                if (!same && !(score >= StrangerMinScore && (!cold.IsPlayer || healAllies))) continue;
                if (score > StickyMinScore && taken.Contains((i, s))) return (s, MathF.Max(score, StickyFloor));   // the running pair: taken at once, nothing else is looked at
                if (OthersOn(taken, i, s) > 0) score *= TakenWeight;
                if (score > bestScore)
                {
                    best = s;
                    bestScore = score;
                }
            }
        float self = Need(world, i, i, organic, robotic);
        if (bestScore < self)
        {
            best = i;
            bestScore = self;
        }
        if (best >= 0) return (best, bestScore);

        // Pass 2: strangers, prisoners, chained slaves.
        bool ownsCages = false;
        if (sweeping) ownsCages = sweepCageFactions.Contains(cold.Faction);
        else
            for (int k = 0; k < world.Fixtures.All.Count; k++)
                if (world.Fixtures.All[k] is { Kind: FixtureKind.Cage } cage && cage.Owner == cold.Faction) { ownsCages = true; break; }
        if (heals || ownsCages || organic || robotic)
            foreach (int s in candidates)
            {
                if (!next[s].Alive || (next[s].Position - origin).LengthSquared() > range2 || table.Cold(s) is not { } other || Skip(s, other)) continue;
                if (KitNeed(cold, s, other, organic, robotic) <= 0) continue;   // Need's 0: before the relation tests
                if (other.Race!.IsRobot && !robotics || other.Animal is not null || Eaten(other) || other.InCombat || Dist2(s) > range2) continue;
                bool enemy = EnemyOf(world, i, s);
                if (enemy && !healAll) continue;
                if (Relations is { } r2 && r2.Contains(other.Faction) && r2.Factions[other.Faction].NotReal) continue;
                bool down = other.Medical!.Incapacitated || !other.CarriedBy.IsNone;
                if (Allied(world, i, s) && !down && !other.IsImprisoned) continue;   // sensed as an ally only: pass 1's
                bool ok;
                if (!other.IsImprisoned && healNeutrals && down && !CarriedByEnemy(other)) ok = true;   // Heal Neutrals / Heal All (Meitou-only): no faction, class or relation test
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
                if (!ok && !OwnedChained(cold, other)) continue;
                if (world.Sight.PlaceOf(i) != world.Sight.PlaceOf(s)) continue;
                float need = Need(world, i, s, organic, robotic);
                if (need <= 0) continue;
                float score = need * Nearness(world, i, cold, s);
                if (score > StickyMinScore && taken.Contains((i, s))) return (s, MathF.Max(score, StickyFloor));
                if (OthersOn(taken, i, s) > 0 || score <= bestScore) continue;
                best = s;
                bestScore = score;
            }
        if (best >= 0) return (best, bestScore);

        // Pass 3: the squad's own chained slaves in sight.
        foreach (int s in candidates)
        {
            if (!next[s].Alive || Dist2(s) > range2 || table.Cold(s) is not { } other || Skip(s, other) || !SquadSlave(cold, other)) continue;
            if (KitNeed(cold, s, other, organic, robotic) <= 0) continue;
            if (other.Race!.IsRobot && !robotics || other.InCombat) continue;
            float need = Need(world, i, s, organic, robotic);
            if (need <= 0) continue;
            float score = need * Nearness(world, i, cold, s);
            if (score > bestScore) { best = s; bestScore = score; }
        }
        // Pass 4: someone of its faction carries a patient, outdoors only.
        if (best < 0 && world.Sight.PlaceOf(i) < 0)
            foreach (int s in candidates)
            {
                if (!next[s].Alive || Dist2(s) > range2 || table.Cold(s) is not { } other || Skip(s, other) || other.CarriedBy.IsNone || other.Animal is not null || Eaten(other)) continue;
                if (KitNeed(cold, s, other, organic, robotic) <= 0) continue;
                if (other.Race!.IsRobot && !robotics) continue;
                if (!table.TryResolveNext(other.CarriedBy, out int c) || table.Cold(c)?.Faction != cold.Faction) continue;
                float need = Need(world, i, s, organic, robotic);
                if (need <= 0) continue;
                float score = need * Nearness(world, i, cold, s);
                if (score > bestScore) { best = s; bestScore = score; }
            }

        // Pass 5 (FUN_14099f690, Verified): standing inside its squad's home building, the prisoners in that building's cages, flat weight; no other test on them.
        if (HomeKey(world, cold) is { } homeKey && world.Sight.BuildingOf(i) == homeKey)
            foreach (var f in world.Fixtures.OfBuilding(homeKey))
            {
                if (f.Kind != FixtureKind.Cage || FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s) || table.Cold(s) is null) continue;
                float need = Need(world, i, s, organic, robotic) * CagedWeight;
                if (need > bestScore) { best = s; bestScore = need; }
            }
        return best >= 0 ? (best, bestScore) : (-1, 0);
    }

    /// <summary>
    /// Whether the patient sits in a cage, and whether that cage stands in the medic squad's home building <paramref name="homeKey"/> (<c>FUN_14099f690</c>, <b>Verified</b>: the cage's
    /// building against the squad's home hand). A squad without a home (the player's: its shared home is <b>Unknown</b>) falls back to the cage's owner being <paramref name="faction"/>.
    /// </summary>
    static (string? Cage, bool Owned) CageOwnedBy(World world, int patient, int faction, string? homeKey)
    {
        var id = world.Characters.IdOf(patient);
        foreach (var f in world.Fixtures.All)
            if (f.Kind == FixtureKind.Cage && f.Occupant == id) return (f.Id, homeKey is not null ? f.Building == homeKey : f.Owner == faction);
        return (null, false);
    }

    /// <summary>The key of the medic squad's home building (the one it was given, else its squad's); null without one.</summary>
    static string? HomeKey(World world, CharacterCold cold) =>
        world.Squads.Find(cold.SquadId) is { } squad ? (squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key : null;

    /// <summary>
    /// Starts the Medic / Robotics job's (<paramref name="job"/>), an NPC's or the button's treatment of the best patient the finder gives; false when none. The button (<see cref="TaskSource.Order"/>, row
    /// 25) makes a fighting medic look too, and one that finds a patient leaves its fight (<see cref="TaskStart.Start"/>).
    /// </summary>
    bool HealSomeone(World world, int i, List<(int Medic, int Patient)> taken, bool robotics, TaskSource source, int job = -1)
    {
        bool ordered = source == TaskSource.Order;
        var (patient, _) = FindPatient(world, i, taken, robotics, ordered);
        if (patient < 0) return false;
        return StartJob(world, i, patient, taken, new TaskRequest(CharacterTask.Treat, ordered ? TaskType.FIRST_AID_ORDER : robotics ? TaskType.JOB_REPAIR_ROBOT : TaskType.JOB_MEDIC, source) { Job = job });
    }

    /// <summary>
    /// The Splint Rigging finder (<c>FUN_14099e240</c>, <b>Verified</b>; a pure read): X = min(medic skill, the quality of the splint kit it takes: its own inventory or backpack, the fewest charges; none: nothing); sensed allies not of a robot
    /// race, not carried by an enemy, of the medic's own faction only, whose <c>needsSplint</c>(X) holds weigh 0.1 x <see cref="Nearness"/> (x 0.05 when another medic works on it); the medic itself 0.1. A patient the medic already treats (a pair on the sub-target list) is taken at once.
    /// Returns the patient's slot, -1 for none.
    /// </summary>
    int FindSplint(World world, int i, List<(int Medic, int Patient)> taken)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (cold.Medical is not { Incapacitated: false } own || cold.Race is not { } ownRace || cold.Stats is not { } stats || cold.InCombat) return -1;
        if (OwnKit(cold, ItemInfo.SplintKitFunction) is not { } kit) return -1;   // the medic's own kit, as the order and the splint use
        // No fight test in the original finder (FUN_14099e240) and the job keeps a positive desire (FUN_140998410): a player's splint job runs during a fight, as the Medic job does.
        if (!cold.IsPlayer && cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && CharacterFacts.SquadFighting(table, squad)) return -1;
        float skill = MathF.Min(stats[StatsEnumerated.Medic] * own.StatMultiplier(StatsEnumerated.Medic), Items.Info(kit.Item.Record)?.Quality ?? 0);
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight);
        int best = -1;
        float bestScore = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == i || !next[s].Alive || PursuitSystem.PathImpossible(cold, table.IdOf(s), world.Tick) || table.Cold(s) is not { Medical: { Dead: false } med, Race: { } race } other) continue;
            if (other.Faction != cold.Faction || race.IsRobot || !med.NeedsSplint(race, skill)) continue;
            if (!other.CarriedBy.IsNone && table.TryResolveNext(other.CarriedBy, out int c) && table.Cold(c) is { } carrier && EnemyOf(world, i, c)) continue;
            var d = next[s].Position - next[i].Position;
            if (d.LengthSquared() > range * range) continue;
            float score = SplintWeight * Nearness(world, i, cold, s);
            if (score > StickyMinScore && taken.Contains((i, s))) return s;   // the running pair is taken at once
            if (OthersOn(taken, i, s) > 0) score *= TakenWeight;
            if (score <= bestScore) continue;
            best = s;
            bestScore = score;
        }
        if (bestScore < SplintWeight && own.NeedsSplint(ownRace, skill)) best = i;
        return best;
    }

    /// <summary>The Splint Rigging job's treatment (row 250).</summary>
    static TaskRequest Splinting(int job) => new(CharacterTask.Treat, TaskType.SPLINT_JOB, TaskSource.Job) { Job = job };

    /// <summary>Starts a treatment of <paramref name="patient"/> (<paramref name="request"/> without its target); an NPC medic goes back afterwards to its standing task (<see cref="TaskStart.Standing"/>).</summary>
    static bool StartJob(World world, int i, int patient, List<(int Medic, int Patient)> taken, TaskRequest request)
    {
        var next = world.Characters.Next;
        var resume = TaskStart.ResumeOf(world, i, CharacterTask.Treat, TaskStart.Standing(world.Characters.Cold(i)!, (CharacterTask)next[i].Task));
        if (TaskStart.Start(world, i, request with { Target = new CharacterId(patient, next[patient].Generation), Resume = resume }) != StartResult.Started) return false;
        taken.Add((i, patient));
        return true;
    }

}
