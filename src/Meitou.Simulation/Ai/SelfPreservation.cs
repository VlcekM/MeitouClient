using Meitou.Simulation.Aggro;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Self preservation (goal 32, docs/game/ai-tasks.md "Self preservation"), every character's urgent goal: its desire is the threat level
/// (<see cref="Combat.CombatSystem.ThreatLevel"/>) and it is achieved by the first provider of <c>!isAggressionTowardsMe</c> the planner reaches: 13
/// "Attacking attackers of" (which leads to 4 Attacking; Meitou: the AI's serial step starts the fight on its own attacker, <see cref="AiSystem"/>)
/// or 35 "Running away" (<see cref="FleeSystem"/> runs). This holds the two rows' desires and the planner's choice between them, which depends only
/// on the character's body. The player's characters are the player's to drive (the original's veto for them, which wants no working arm, is not
/// modelled).
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
    /// Row 35's desire (AI slot 55, <c>FUN_1405a34e0</c>, <b>Verified</b>): 1 for a crippled character (Character <c>isCrippled</c>, the medical
    /// tick's flag: a leg down, so it crawls), else 0.001. 0 for a character shut in behind a locked gate of town walls (<paramref name="walled"/>,
    /// <b>Verified</b>: <c>FUN_1402eb070</c> to code 0 is 3); the building half of the location test <c>FUN_1405a2710</c> (no open door) is not
    /// modelled. The player-faction branch is not needed: a player's character has 0.
    /// </summary>
    public static float RunDesire(CharacterCold cold, bool walled = false) =>
        cold.IsPlayer || cold.Medical is not { } medical ? 0 : medical.Crawling ? 1 : walled ? 0 : RunFallback;

    /// <summary>
    /// Whether the character is shut in behind a locked gate of town walls (<see cref="Locks.LockSystem.WallsLocked"/>): row 35's fallback desire is
    /// 0 then.
    /// </summary>
    public static bool Walled(World world, int slot) =>
        world.SystemOf<Locks.LockSystem>() is { } locks && locks.WallsLocked(world, world.Characters.Previous[slot].Position);

    /// <summary>
    /// Row 4's desire as the chain 13 to 4 meets it (AI slot 53, <c>FUN_1405a4260</c>, <b>Verified</b>): 0 with no working arm; a crippled NPC 0.1
    /// while a threat is within <see cref="RetaliationSystem.ImmediateReach"/> (<paramref name="immediate"/>), else 0 (a unique character 0.1, which
    /// changes nothing: running is worth 1); any other 1. The factors the original also applies (x0.1 with Ranged on, x0.5 for a squad not intact)
    /// never bring it near the running desire of 0.001 and are left out.
    /// </summary>
    public static float AttackDesire(CharacterCold cold, bool immediate) =>
        cold.Medical is not { } medical || !medical.ArmWorks(left: true) && !medical.ArmWorks(left: false)
            ? 0
            : medical.Crawling ? immediate ? CrippledAttack : 0 : 1;

    /// <summary>
    /// Whether row 35 can run: its list B wants <c>!IS_CROUCHING_OR_LYING</c> (26, <c>FUN_140599e90</c>, <b>Verified</b>), which a crippled character
    /// never is (the state answers false for it), and which a character staying low is (it would have to stand first, which is not modelled). The
    /// other entry (60, a location it can get to) is held true.
    /// </summary>
    static bool CanRun(CharacterCold cold) => cold.Medical is { } medical && (medical.Crawling || !medical.Prone);

    /// <summary>
    /// The planner's choice under goal 32 at threat level <paramref name="level"/> (docs/game/ai-tasks.md "Planner": best first by g + h, g the least
    /// desire on the way down from the goal's own, h the node's): true when 35 runs, false when the chain through 13 fights. A character that cannot
    /// fight (no arm, or crippled with nothing close) runs; a crippled one runs even when it can fight, for 35 is worth 1 against 4's 0.1; any other
    /// fights, 35 being worth 0.001.
    /// </summary>
    public static bool Runs(CharacterCold cold, float level, bool walled = false)
    {
        float run = RunDesire(cold, walled);
        if (run <= 0 || !CanRun(cold)) return false;
        float fight = AttackDesire(cold, level >= 1);
        return fight <= 0 || Value(level, run) > Value(level, fight);
    }

    /// <summary>
    /// The value of the goal at a threat <paramref name="level"/>: the smaller of the level and the desire, plus the desire.
    /// </summary>
    static float Value(float level, float desire) => MathF.Min(level, desire) + desire;

    /// <summary>
    /// Whether this character flees under Self preservation when threatened: an NPC with an AI whose body takes the choice to 35
    /// (<see cref="Runs"/>).
    /// </summary>
    public static bool Flees(CharacterCold cold, float level, bool walled = false) =>
        !cold.IsPlayer && cold.Ai is not null && Runs(cold, level, walled);
}