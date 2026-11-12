using Meitou.Simulation.Player;

namespace Meitou.Simulation.Combat;

/// <summary>
/// One candidate of a fight target choice, as <see cref="TargetScore.Score"/> weighs it.
/// </summary>
public readonly record struct TargetCandidate
{
    /// <summary>
    /// The chooser's current target.
    /// </summary>
    public bool Current { get; init; }

    /// <summary>
    /// Playing a stumble clip.
    /// </summary>
    public bool Staggered { get; init; }

    /// <summary>
    /// How many fight it, and whether the chooser is one of them.
    /// </summary>
    public int Attackers { get; init; }

    /// <summary>
    /// Whether the chooser is one of its attackers.
    /// </summary>
    public bool ChooserAttacks { get; init; }

    /// <summary>
    /// Its Taunt order is on: +2, and the crowd term x0.35.
    /// </summary>
    public bool Taunt { get; init; }

    /// <summary>
    /// Its Passive order is on: -1.
    /// </summary>
    public bool Passive { get; init; }

    /// <summary>
    /// It is on one of the chooser's side who does not fight it back, and fewer than two fight it.
    /// </summary>
    public bool Protect { get; init; }

    /// <summary>
    /// Ground distance from the chooser.
    /// </summary>
    public float Distance { get; init; }

    /// <summary>
    /// Its fight is on.
    /// </summary>
    public bool Fighting { get; init; }

    /// <summary>
    /// It mans a turret.
    /// </summary>
    public bool Gunner { get; init; }

    /// <summary>
    /// Crawling or unable to fight.
    /// </summary>
    public bool Crippled { get; init; }

    /// <summary>
    /// Its fight is off and its intended aggression is 0 or less (Meitou: it does not count the chooser's faction an enemy).
    /// </summary>
    public bool Peaceful { get; init; }

    /// <summary>
    /// Getting up after a knockout (Character +0x278).
    /// </summary>
    public bool GettingUp { get; init; }
}

/// <summary>
/// The fight target score (<c>AI::chooseBestAttackTarget</c>, docs/game/combat.md "Attack slots and circling", Target score): the CharStats weights
/// are the same constants for every character (<b>Verified</b> literals of <c>CharStats::init</c>). The candidate lists and the Hold post test are
/// the caller's.
/// </summary>
public static class TargetScore
{
    /// <summary>
    /// The CharStats weights: Stubbornness (the current target's bonus), Professionalness (the stagger malus), HoldingPosition (unused),
    /// Immediacy and ImmediacyRange (the range term's weight and default reach) and Manliness (the crowd term per attacker).
    /// </summary>
    public const float Stubbornness = 5, Professionalness = 1, HoldingPosition = 2, Immediacy = 2, ImmediacyRange = 200, Manliness = 1;
    /// <summary>
    /// Added for a target that is not fighting (the score is then halved for a peaceful one).
    /// </summary>
    public const float Murderous = -2;

    /// <summary>
    /// A player-faction chooser drops candidates beyond this (its crossbow range with Ranged on).
    /// </summary>
    public const float PlayerRange = 400;

    /// <summary>
    /// On Hold the score is x0.01 beyond this.
    /// </summary>
    public const float HoldNear = 40;

    /// <summary>
    /// The range term's reach by the chooser's manoeuvre mode (<c>AI::scoreTargetRange</c>, <b>Verified</b>): HOLD_GROUND 2 x (meiDistance +
    /// tolerance), STAY_CLOSE meiDistance + 200 + tolerance, ROAM_FAR 200; the weight is 2 in every mode.
    /// </summary>
    public static float RangeOf(ManoeuvreMode mode) => mode switch
    {
        ManoeuvreMode.HoldGround => 2 * (Orders.MeiDistance + Orders.MeiTolerance),
        ManoeuvreMode.StayClose => Orders.MeiDistance + Orders.StayCloseExtra + Orders.MeiTolerance,
        _ => ImmediacyRange,
    };

    /// <summary>
    /// The score of <paramref name="c"/> (higher is better), null when dropped. <paramref name="prone"/>: the chooser crawls (stubbornness x4, range
    /// term x20). The range term is the chooser's manoeuvre mode's (<see cref="RangeOf"/>).
    /// </summary>
    public static float? Score(in TargetCandidate c, bool player, bool hold, bool prone, bool animal, ManoeuvreMode mode = ManoeuvreMode.RoamFar)
    {
        float s = 0;
        if (c.Current) s += Stubbornness * (prone ? 4 : 1);
        if (c.Staggered) s -= Professionalness;

        float crowd = -Manliness * (c.Attackers - (c.ChooserAttacks ? 1 : 0));
        if (c.Taunt)
        {
            s += 2;
            crowd *= 0.35f;
        }

        s += crowd;
        if (c.Passive) s -= 1;
        if (c.Protect) s += 10;
        if (player && c.Distance > PlayerRange) return null;
        if (hold && c.Distance > HoldNear) s *= 0.01f;

        s += (1 - c.Distance / RangeOf(mode)) * Immediacy * (prone ? 20 : 1);
        if (!c.Fighting)
        {
            s += Murderous;
            if (c.Peaceful) s *= 0.5f;
        }

        if (c.GettingUp) s *= 0.1f;
        if (c.Gunner && !animal) s *= 3;
        if (c.Crippled) s -= 10;

        return s;
    }
}
