using Meitou.Simulation.Aggro;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Self preservation (goal 32, docs/game/ai-tasks.md "Self preservation"), every character's urgent goal: its desire is the threat level
/// (<see cref="Combat.CombatSystem.ThreatLevel"/>) and it is achieved by the first provider of <c>!isAggressionTowardsMe</c> the planner reaches: 13
/// "Attacking attackers of" (which leads to 4 Attacking; Meitou: the AI's serial step starts the fight on its own attacker, <see cref="AiSystem"/>)
/// or 35 "Running away" (<see cref="FleeSystem"/> runs), or 234 "Turret guard" through 146 on a turret. This holds the rows' desires and the
/// planner's search over them (<see cref="Choose"/>; row 35 of a player-faction mind has the vetoes of <c>FUN_1405a34e0</c>'s player branch, and
/// the turret rows' inputs come from the AI).
/// </summary>
public static class SelfPreservation
{
    /// <summary>
    /// Goal 32's desire with a threat of positive aggression within <see cref="RetaliationSystem.SelfDefenceReach"/> and none within 40
    /// (<c>FUN_140599d20</c>'s <c>DAT_141696c70</c>).
    /// </summary>
    public const float Attacking = 0.7f;

    /// <summary>
    /// Row 35's desire for a character that can get out and is not crippled (<c>DAT_14168a2bc</c>, 0.001: the fallback behind 13).
    /// </summary>
    public const float RunFallback = 0.001f;

    /// <summary>
    /// Row 4's desire for a crippled fighter under attack within <see cref="RetaliationSystem.ImmediateReach"/> (<c>DAT_14167fad0</c>, 0.1).
    /// </summary>
    public const float CrippledAttack = 0.1f;

    /// <summary>
    /// Row 4's factor with Ranged on (<c>DAT_14167fad0</c>, 0.1).
    /// </summary>
    const float RangedFactor = 0.1f;

    /// <summary>
    /// Row 35's desire (AI slot 55, <c>FUN_1405a34e0</c>, <b>Verified</b>): 1 for a crippled character (Character <c>isCrippled</c>, the medical
    /// tick's flag: a leg down, so it crawls), else 0.001. 0 for a character shut in behind a locked gate of town walls (<paramref name="walled"/>,
    /// <b>Verified</b>: <c>FUN_1402eb070</c> to code 0 is 3); the building half of the location test <c>FUN_1405a2710</c> (no open door) is not
    /// modelled. A player's character has 0 here; a player-faction mind's 35 (a slave's goal, a node under 32) takes the player-faction branch
    /// (<see cref="AiSystem"/>'s player-side run desire), which adds its vetoes to <see cref="BodyRunDesire"/>.
    /// </summary>
    public static float RunDesire(CharacterCold cold, bool walled = false) => cold.IsPlayer ? 0 : BodyRunDesire(cold, walled);

    /// <summary>
    /// Row 35's desire after the player-faction vetoes (<see cref="RunDesire"/>): 1 crippled, else 0.001, 0 when walled in or without a body.
    /// </summary>
    public static float BodyRunDesire(CharacterCold cold, bool walled = false) =>
        cold.Medical is not { } medical ? 0 : medical.Crawling ? 1 : walled ? 0 : RunFallback;

    /// <summary>
    /// Whether the character is shut in behind a locked gate of town walls (<see cref="Locks.LockSystem.WallsLocked"/>): row 35's fallback desire is
    /// 0 then.
    /// </summary>
    public static bool Walled(World world, int slot) =>
        world.SystemOf<Locks.LockSystem>() is { } locks && locks.WallsLocked(world, world.Characters.Previous[slot].Position);

    /// <summary>
    /// Row 4's desire as the chain 13 to 4 meets it (AI slot 53, <c>FUN_1405a4260</c>, <b>Verified</b>): 0 with no working arm; a crippled NPC 0.1
    /// while a threat is within <see cref="RetaliationSystem.ImmediateReach"/> (<paramref name="immediate"/>), else 0 (a unique character 0.1, which
    /// changes nothing: running is worth 1); any other 1, before the factors <see cref="MeleeDesire"/> applies.
    /// </summary>
    public static float AttackDesire(CharacterCold cold, bool immediate) =>
        cold.Medical is not { } medical || !medical.ArmWorks(left: true) && !medical.ArmWorks(left: false)
            ? 0
            : medical.Crawling ? immediate ? CrippledAttack : 0 : 1;

    /// <summary>
    /// Row 4's desire under 13 (AI slot 53 <c>FUN_1405a4260</c>, <b>Verified</b>): a crippled character's is its body's
    /// (<see cref="AttackDesire"/>; a player-faction one with a working arm 0.1 whether or not a threat is close) and returns before the factors;
    /// anyone else's 1, x0.1 with Ranged on (every NPC, <see cref="Orders.RangedMode"/>; CharStats <c>+0x129</c>) and x0.5 while its squad is not
    /// intact (<c>Platoon::isIntact</c> <c>FUN_1407ac7b0</c>). The Hold branch's target tests are the engine's post test.
    /// </summary>
    public static float MeleeDesire(CharacterCold cold, bool immediate, bool playerSide, bool intact)
    {
        float body = AttackDesire(cold, immediate);
        if (cold.Medical is not { } medical) return 0;
        if (medical.Crawling) return body > 0 || !playerSide || !OneArm(medical) ? body : CrippledAttack;
        if (body <= 0) return 0;

        if (Orders.RangedMode(cold)) body *= RangedFactor;
        return intact ? body : body * 0.5f;
    }

    /// <summary>
    /// Row 262's desire under 13 (AI slot 54 <c>FUN_140598260</c>, <b>Verified</b>; docs/game/combat.md "Melee or shooting"): 0 with Ranged off,
    /// crippled, without both arms (its list B 161 <c>hasTwoWorkingArm</c>) or without a crossbow; 0.1 with an immediate threat; 0 without its
    /// bolts; else 1, 0.5 while the squad is not intact. Its list B's <c>!me swim</c> is the caller's.
    /// </summary>
    public static float ShootDesire(CharacterCold cold, bool immediate, bool intact)
    {
        if (!Orders.RangedMode(cold) || cold.Medical is not { Crawling: false } medical) return 0;
        if (!medical.ArmWorks(left: true) || !medical.ArmWorks(left: false) || cold.Fighter?.Crossbow is not { } bow) return 0;
        if (immediate) return CrippledAttack;
        if (!bow.InfiniteAmmo && Ammunition.FirstCarried(cold.Inventory, bow.Data) < 0) return 0;

        return intact ? 1 : 0.5f;
    }

    /// <summary>
    /// Whether either arm works (list B 38 <c>hasOneWorkingArm</c>).
    /// </summary>
    public static bool OneArm(Bodies.MedicalState medical) => medical.ArmWorks(left: true) || medical.ArmWorks(left: false);

    /// <summary>
    /// Whether row 35 can run: its list B wants <c>!IS_CROUCHING_OR_LYING</c> (26, <c>FUN_140599e90</c>, <b>Verified</b>), which a crippled character
    /// never is (the state answers false for it), and which a character staying low is (it would have to stand first, which is not modelled). The
    /// other entry (60, a location it can get to) is held true.
    /// </summary>
    static bool CanRun(CharacterCold cold) => cold.Medical is { } medical && (medical.Crawling || !medical.Prone);

    /// <summary>
    /// Whether a 35 node of desire <paramref name="run"/> is made and accepted (<c>FUN_14051bae0</c>: a node of desire 0 is never made; then it can
    /// run, <see cref="CanRun"/>): the planner's plan under 32 when the chain through 13 dead-ends.
    /// </summary>
    public static bool CanRunAt(CharacterCold cold, float run) => run > 0 && CanRun(cold);

    /// <summary>
    /// The planner's choice under goal 32 at threat level <paramref name="level"/> (docs/game/ai-tasks.md "Planner": best first by g + h, g the least
    /// desire on the way down from the goal's own, h the node's): true when 35 runs, false when the chain through 13 fights. A character that cannot
    /// fight (no arm, or crippled with nothing close) runs; a crippled one runs even when it can fight, for 35 is worth 1 against 4's 0.1; any other
    /// fights, 35 being worth 0.001. One in deep water runs too: the chain through 13 is a dead end there, its providers of state 7 (4 and 262)
    /// needing <c>!me swim</c>, which no row provides (<see cref="Combat.CombatSystem.SwimBarred"/>); an animal's 227 has no such test.
    /// </summary>
    public static bool Runs(CharacterCold cold, float level, bool walled = false) => RunsAt(cold, level, RunDesire(cold, walled));

    /// <summary>
    /// <see cref="Runs"/> with row 35's desire <paramref name="run"/> given (a player-faction mind's, <c>FUN_1405a34e0</c>'s player branch).
    /// </summary>
    public static bool RunsAt(CharacterCold cold, float level, float run)
    {
        if (!CanRunAt(cold, run)) return false;

        bool swimming = cold is { WaterState: 3, InSomething: Locks.HeldState.None } && cold.Fighter?.IsAnimal != true;
        float fight = swimming ? 0 : AttackDesire(cold, level >= 1);
        return fight <= 0 || Value(level, run) > Value(level, fight);
    }

    /// <summary>
    /// The value of the goal at a threat <paramref name="level"/>: the smaller of the level and the desire, plus the desire.
    /// </summary>
    static float Value(float level, float desire) => MathF.Min(level, desire) + desire;

    #region Search

    /// <summary>
    /// The rows of goal 32's search: its providers 13, 35 and 234, 13's children 4 and 262, and 234's child 146.
    /// </summary>
    const int Row13 = 13, Row35 = 35, Row234 = 234, Row4 = 4, Row262 = 262, Row146 = 146;

    /// <summary>
    /// An open node: its row, g (the least desire on the way down) and h (its row's desire).
    /// </summary>
    readonly record struct Node(int Row, float G, float H);

    /// <summary>
    /// The planner's search under goal 32 (docs/game/ai-tasks.md "Combat goals", <b>Verified</b>: <c>FUN_14050f800</c>, <c>FUN_14051bae0</c>; the
    /// open list <c>FUN_140519730</c> gives the highest g + h, the first on a tie, children appended): the root's providers in ascending type (13
    /// at slot 19's 1, 35, 234; a provider of desire 0 is not made), each with g = min(its desire, the parent's g) and h = its desire, the root's g
    /// the threat level. 13 with an attacker appends 4 and 262 (either accepted when taken out: a fight); 35 is accepted when it can run; 234 with a
    /// working arm and a turret found appends 146, accepted when <c>!158</c> holds (no immediate threat). A fight on a refused attacker (the engine's own refusals of
    /// <see cref="AiSystem"/>'s self-defence pick) fails the plan, as before.
    /// </summary>
    public static SelfPlan Choose(in SelfNodes n)
    {
        Span<Node> open = stackalloc Node[6];
        int count = 0;
        Add(open, ref count, Row13, 1, n.Level);
        Add(open, ref count, Row35, n.Run, n.Level);
        Add(open, ref count, Row234, n.Guard, n.Level);

        while (count > 0)
        {
            var node = TakeBest(open, ref count);
            if (node.Row is Row4 or Row262 && n.Refused) return SelfPlan.None;
            if (Accepted(n, node.Row) is { } plan) return plan;

            Expand(n, open, ref count, node);
        }

        return SelfPlan.None;
    }

    /// <summary>
    /// What a node taken out plans when it is accepted (4 and 262 fight, 35 runs when it can, 146 mans when <c>!158</c> holds); null to expand it.
    /// </summary>
    static SelfPlan? Accepted(in SelfNodes n, int row) => row switch
    {
        Row4 or Row262 => SelfPlan.Fight,
        Row35 => n.CanRun ? SelfPlan.Run : null,
        Row146 => n.GunnerRuns ? SelfPlan.Turret : null,
        _ => null,
    };

    /// <summary>
    /// Appends a node's children: 13's 4 and 262 when it has an attacker, 234's 146 when it has a working arm and found a turret.
    /// </summary>
    static void Expand(in SelfNodes n, Span<Node> open, ref int count, Node node)
    {
        if (node.Row == Row13 && n.Attacker)
        {
            Add(open, ref count, Row4, n.Melee, node.G);
            Add(open, ref count, Row262, n.Shoot, node.G);
        }
        else if (node.Row == Row234 && n.GuardArm && n.TurretFound) Add(open, ref count, Row146, n.Gunner, node.G);
    }

    /// <summary>
    /// Makes a node of <paramref name="desire"/> under a parent of g <paramref name="parent"/> (none for a desire not above 0).
    /// </summary>
    static void Add(Span<Node> open, ref int count, int row, float desire, float parent)
    {
        if (desire <= 0) return;

        open[count++] = new Node(row, MathF.Min(desire, parent), desire);
    }

    /// <summary>
    /// Takes out the open node of the highest g + h, the first of equals.
    /// </summary>
    static Node TakeBest(Span<Node> open, ref int count)
    {
        int best = 0;
        for (int k = 1; k < count; k++)
            if (open[k].G + open[k].H > open[best].G + open[best].H) best = k;

        var node = open[best];
        open[(best + 1)..count].CopyTo(open[best..]);
        count--;
        return node;
    }

    #endregion
}

/// <summary>
/// What goal 32's search plans (<see cref="SelfPreservation.Choose"/>): nothing, 13's fight, 35's run or 146's turret.
/// </summary>
public enum SelfPlan : byte
{
    /// <summary>
    /// No plan: the next goal is tried.
    /// </summary>
    None,

    /// <summary>
    /// [4 or 262, 13, 32]: the fight on its own attacker.
    /// </summary>
    Fight,

    /// <summary>
    /// [35, 32]: the run.
    /// </summary>
    Run,

    /// <summary>
    /// [146, 234, 32]: manning the turret 234's finder took.
    /// </summary>
    Turret,
}

/// <summary>
/// The inputs of goal 32's search (<see cref="SelfPreservation.Choose"/>): the threat level and each node's desire and tests.
/// </summary>
public readonly record struct SelfNodes
{
    /// <summary>
    /// The threat level, the root's g and h.
    /// </summary>
    public float Level { get; init; }

    /// <summary>
    /// 13's finder took an attacker; <see cref="Refused"/>: it took one the engine refuses (the plan fails).
    /// </summary>
    public bool Attacker { get; init; }

    /// <summary>
    /// 13's attacker is one the engine refuses (deep water, Hold's post, a gunner's shooter).
    /// </summary>
    public bool Refused { get; init; }

    /// <summary>
    /// Row 4's desire (AI slot 53), 0 when it cannot run.
    /// </summary>
    public float Melee { get; init; }

    /// <summary>
    /// Row 262's desire (AI slot 54), 0 when it cannot run.
    /// </summary>
    public float Shoot { get; init; }

    /// <summary>
    /// Row 35's desire (AI slot 55).
    /// </summary>
    public float Run { get; init; }

    /// <summary>
    /// Whether row 35 can run (<see cref="SelfPreservation.CanRunAt"/>).
    /// </summary>
    public bool CanRun { get; init; }

    /// <summary>
    /// Row 234's desire (AI slot 35 on the character itself; 0 when the repertoire has no 234).
    /// </summary>
    public float Guard { get; init; }

    /// <summary>
    /// Row 234's list B 38 <c>hasOneWorkingArm</c>.
    /// </summary>
    public bool GuardArm { get; init; }

    /// <summary>
    /// 234's finder (<c>FUN_1408ccfa0</c>) took a turret.
    /// </summary>
    public bool TurretFound { get; init; }

    /// <summary>
    /// Row 146's desire (AI slot 35 on that turret).
    /// </summary>
    public float Gunner { get; init; }

    /// <summary>
    /// Row 146's list B <c>!158</c> holds (no immediate threat).
    /// </summary>
    public bool GunnerRuns { get; init; }
}