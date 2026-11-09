using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>
/// How a blow ended (docs/game/combat.md "Hit outcome", <c>FUN_140665fa0</c>).
/// </summary>
public enum BlowOutcome
{
    /// <summary>
    /// The blow reached the body.
    /// </summary>
    Hit,

    /// <summary>
    /// The defender's block technique faced the blow and was past half its animation.
    /// </summary>
    Blocked,

    /// <summary>
    /// The defender's dodge technique was within its window.
    /// </summary>
    Dodged,

    /// <summary>
    /// The target was no longer in reach (engine rule): nothing happens.
    /// </summary>
    Missed,
}

/// <summary>
/// The outcome rule: which of a blow's endings the defender's reaction at that moment gives.
/// </summary>
public static class HitOutcomes
{
    /// <summary>
    /// A block must be past this much of its animation (<b>Observed</b>, <c>FUN_140665fa0</c>).
    /// </summary>
    public const float BlockProgress = 0.5f;

    /// <summary>
    /// A dodge counts within this window of its animation (<b>Observed</b>).
    /// </summary>
    public const float DodgeFrom = 0.1f, DodgeTo = 0.98f;

    /// <summary>
    /// The direction a blow from <paramref name="behind"/> the defender is met as (<b>Verified</b>, <c>FUN_1404356c0</c>, used by the defence pick
    /// and the outcome): down (1) becomes the rear up block 7, left and right (2, 3) the rear side 9; the others are unchanged.
    /// </summary>
    public static int Facing(int direction, bool behind) => !behind ? direction : direction switch { 1 => 7, 2 or 3 => 9, _ => direction };

    /// <summary>
    /// Whether an attacker at <paramref name="attacker"/> is behind a defender at <paramref name="defender"/> facing <paramref name="yaw"/> (more
    /// than 90 degrees off its heading).
    /// </summary>
    public static bool Behind(System.Numerics.Vector3 attacker, System.Numerics.Vector3 defender, float yaw) =>
        (attacker.X - defender.X) * MathF.Sin(yaw) + (attacker.Z - defender.Z) * MathF.Cos(yaw) < 0;

    /// <summary>
    /// Blocked (code 5) if the defender's technique is a block, its direction equals the blow's and its animation is past 50 per cent; dodged (code
    /// 0) if it is a dodge and the progress is in [0.1, 0.98]; otherwise a hit (code 2). A technique flagged both (the base game's "Dodge back" rows)
    /// is a dodge: a block needs <c>is dodge</c> 0 (<b>Verified</b>, docs/game/combat.md).
    /// </summary>
    public static BlowOutcome Decide(CombatTechnique? reaction, float progress, int blowDirection)
    {
        if (reaction is null) return BlowOutcome.Hit;
        if (reaction.IsBlock && !reaction.IsDodge && reaction.AttackDirection1 == blowDirection && progress > BlockProgress) return BlowOutcome.Blocked;
        if (reaction.IsDodge && progress >= DodgeFrom && progress <= DodgeTo) return BlowOutcome.Dodged;

        return BlowOutcome.Hit;
    }
}
