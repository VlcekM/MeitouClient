using System.Numerics;

namespace Meitou.Simulation.Combat;

/// <summary>Timing constants of the duel loop: the verified ones say so, the rest are engine choices where the research stops (docs/simulation.md "Combat").</summary>
public static class CombatTuning
{
    /// <summary>
    /// Seconds a fighter waits after its attack by how its last blow ended (<b>Verified</b>, FUN_14060a2f0, docs/game/combat.md "Pacing"): 0.6 after a hit, 0.3 blocked,
    /// 0.2 dodged; a swing that finds nobody in its attack zone keeps the 0.5 s deciding set (+0x148). A hit that finds no reaction clip still cancels the swing and waits <see cref="InterruptPause"/>.
    /// </summary>
    public const float HitPause = 0.6f, BlockedPause = 0.3f, DodgedPause = 0.2f, MissedPause = 0.5f, InterruptPause = 0.2f;
    /// <summary>
    /// The clip progress past which a swing is finished and the wait after it starts (<b>Verified</b>, 0.98 read from the binary: <c>FUN_14060a590</c> arms +0x144 once <c>FUN_1405b4c00</c> reports the clip done); the wait
    /// runs only while no clip is busy (<c>FUN_1405b34b0</c>: progress under the technique's <c>acceptable end time</c>, a 0 there is never busy), so it starts at the later of the two. A data 0 (many mods) does not end the swing at once.
    /// </summary>
    public const float SwingFinished = 0.98f;
    /// <summary>Seconds between a staggered fighter's chances to break out of its hit reaction into a block (<b>Verified</b>, the original's +0x124).</summary>
    public const float BreakOutSeconds = 1.2f;
    /// <summary>The swing progress window of the attackers a stumble break-out is rolled against (<b>Verified</b> constants 0.1 and 0.99, <c>FUN_1406085e0</c>).</summary>
    public const float BreakOutFrom = 0.1f, BreakOutTo = 0.99f;
    /// <summary>Seconds without any defence after a defence roll found nothing (<b>Verified</b>, the original's +0x14c), and the wait after a block ends with nothing coming (+0x148, 0.2 s).</summary>
    public const float NoDefenceSeconds = 1.2f, AfterBlockPause = 0.2f;
    /// <summary>
    /// A defence starts once the swing furthest on is this far to its blow (<b>Verified</b>, <c>FUN_140609290</c>); before that it tries again in EarlyDefenceSeconds (+0x14c 0.1 s). A started
    /// defence sets +0x14c to DefenceStartedSeconds, and the defence update looks at the blow's side again every DefenceRecheckSeconds (+0x274, <c>FUN_1408862c0</c> 1.0).
    /// </summary>
    public const float DefenceStartProgress = 0.1f, EarlyDefenceSeconds = 0.1f, DefenceStartedSeconds = 0.2f, DefenceRecheckSeconds = 1f;
    /// <summary>An animal's defence start takes no technique and sets +0x14c to this (<b>Verified</b>, <c>FUN_140609290</c>): animals never block or dodge.</summary>
    public const float AnimalNoDefenceSeconds = 2f;
    /// <summary>A swing is given up to defend while it is under this far through and a blow coming at the fighter is past this far (<b>Verified</b>, FUN_1406083d0(0.5, 0.5)).</summary>
    public const float AbortOwnProgress = 0.5f, AbortIncomingProgress = 0.5f;
    /// <summary>
    /// Seconds a fighter that lost the initiative roll leaves its opponent free to swing at it (<b>Verified</b>, FUN_14060c8b0: the loser waits its deadTimer, at most 0.5 s, and
    /// decides again; the winner turns on it at once).
    /// </summary>
    public const float YieldSeconds = 0.5f;
    /// <summary>How far (world units) a defender looks for blows coming at it.</summary>
    public const float ReactionScanRadius = 60;
    /// <summary>Seconds a clip plays at when its length is not known.</summary>
    public const float DefaultClipSeconds = 1;
    /// <summary>A fighter closes in until the gap between bodies is this (units), when it moves by itself.</summary>
    public const float CloseInGap = 3;
    /// <summary>
    /// A fighter runs at its target out of the combat stance (plain run clips, full speed) once the gap between the two bodies is over <see cref="RunGap"/>, and goes back
    /// to its footwork once it is under <see cref="FootworkGap"/> (<b>Observed</b>, the fight steering FUN_1402af1e0, docs/game/combat.md "Combat stance").
    /// </summary>
    public const float RunGap = 90, FootworkGap = 70;
    /// <summary>The top speed (units per second) of a fighter in its footwork, closing in on its target (<b>Observed</b>, FUN_1402af1e0).</summary>
    public const float FootworkSpeed = 18;
    /// <summary>A target that is not fighting and moves faster than this (units per second) for <see cref="FleeingSeconds"/> on end is run after at any gap (<b>Observed</b>, FUN_1402ae0e0).</summary>
    public const float FleeingSpeed = 9, FleeingSeconds = 1.5f;
    /// <summary>
    /// After a fleeing target a fighter aims at the target's position plus its velocity times (1 − r) <see cref="LeadSlope"/> + <see cref="LeadBase"/> seconds, r its attack
    /// play rate (<b>Observed</b>, FUN_1402af1e0 / FUN_1404354d0, the rate is CharStats +0x188; docs/game/combat.md "Combat stance").
    /// </summary>
    public const float LeadBase = 1.2f, LeadSlope = 2;
    /// <summary>A chaser's lead point is put this many times the two footprints beside the runner's track, so that solid bodies pass instead of pushing (engine choice).</summary>
    public const float PassWidth = 1.25f;

    /// <summary>The footprint radius of a human, used when a character has no race data.</summary>
    public const float DefaultFootprint = 4;

    /// <summary>The radius a body counts with when the gap between two bodies is taken: the race's <c>pathfind footprint radius</c>.</summary>
    internal static float Footprint(CharacterCold? c) => c?.Race?.PathfindFootprintRadius ?? DefaultFootprint;

    /// <summary>Seconds an attacker leads a target that is not fighting when it measures the gap for its technique (<b>Observed</b>, <c>FUN_1402b4570</c> → <c>FUN_1404354d0</c>).</summary>
    public const float AimLeadSeconds = 1.2f;

    /// <summary>
    /// The gap a technique is chosen by (<b>Observed</b>, <c>FUN_1402b4570</c>; docs/game/combat.md "Attack technique choice"): the distance from the attacker to
    /// <paramref name="aim"/> less 1.5 x its own footprint x its size k and lerp(k, 0.45, 1) x the target's footprint, at least 0.
    /// </summary>
    internal static float Gap(Vector3 from, Vector3 aim, CharacterCold? me, CharacterCold? target)
    {
        float k = me?.Medical?.ScaleK ?? 1;
        return MathF.Max(0, Vector3.Distance(from, aim) - 1.5f * Footprint(me) * k - WeaponStats.Lerp(k, 0.45f, 1) * Footprint(target));
    }

    /// <summary>
    /// The gap between the bodies (footprints) a fighter walks in to (<b>Observed</b>, <c>FUN_1402b4570</c> moving to <c>FUN_14060cd20</c> through the steering's
    /// <c>FUN_1402ae320</c>): its longest reach (<see cref="CombatSlot.Reach"/>) less a quarter of its weapon's length, at least 6 (the steering keeps the far end 5 beyond its near
    /// end of 1), +<see cref="RunningGap"/> while it runs (<c>FUN_1402aebf0</c> in <c>CircleStateAI</c>); <see cref="CloseInGap"/> before its first choice.
    /// </summary>
    public static float StandGap(float reach, float weaponLength, bool running) => reach > 0 ? MathF.Max(6, reach - weaponLength * 0.25f + (running ? RunningGap : 0)) : CloseInGap;

    /// <summary>A running fighter stops this much farther out (<b>Verified</b> literal, <c>CircleStateAI</c> FUN_1402b3180).</summary>
    public const float RunningGap = 12;

    /// <summary>How far (world units) around a target the fighters on it are counted for its attack slots and the fighters it lets close in (engine choice).</summary>
    public const float EngageScanRadius = 300;

    /// <summary>
    /// The band a fighter its target does not let close in keeps (<b>Observed</b>, <c>WaitingStateAI</c> <c>FUN_1402b3800</c>, through <see cref="CombatSteering.Band"/>): near
    /// n / 4 + <see cref="CircleNear"/>, far 3 n + <see cref="CircleFar"/>, n the fighters at the target. The side-step speed the state draws is never read by the steering:
    /// it does not orbit (docs/game/combat.md "Fight steering").
    /// </summary>
    public const float CircleNear = 15, CircleFar = 25;
}