using System.Numerics;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// When a change of the top speed S (injuries, hunger, encumbrance, gear, sneaking, crawling, the water state) reaches the stats and the mover
/// (docs/game/pathfinding.md "Speed and orders", <b>Verified</b> decompile): S (CharStats <c>+0x17c</c>) is recomputed by CharStats
/// <c>periodicUpdate</c>, from each periodic update (<c>FUN_1405d00b0</c>) and, for a character in view, from each medical turn
/// (<c>FUN_1405c7d40</c>), never while unconscious or dead (<c>FUN_140883b70</c>); the periodic update first hands the S from before its recompute
/// to the animation and then the animation's value to the mover (<c>FUN_140646860</c>, <c>FUN_140660dc0</c>). Only the stats' init
/// (<c>FUN_14064cb30</c>) and a load (<c>Character::loadFromSerialise</c> <c>FUN_140626f50</c> → <c>CharStats::updateStats</c> <c>FUN_14064b6a0</c>)
/// recompute S at once; a race change (<c>setRace</c> <c>FUN_1405c93d0</c>) or a stat's growth waits for the chain like any change. The body step
/// gives <see cref="CharacterCold.TopSpeedNow"/> (what a recompute would give now); <see cref="MedicalTimeBudget"/> gives the turns.
/// </summary>
public static class TopSpeedDelay
{
    /// <summary>
    /// A new mover's top speed until its first periodic update (<b>Verified</b>, the CharMovement constructor <c>FUN_140664ec0</c> writes
    /// <c>+0xb4</c> = 70).
    /// </summary>
    public const float NewMoverSpeed = 70;

    /// <summary>
    /// A medical turn (<c>threadedUpdate4</c>): a character in view recomputes S.
    /// </summary>
    public static void MedicalTurn(World world, CharacterCold cold, Vector3 position)
    {
        if (InView(world, position)) Recompute(cold);
    }

    /// <summary>
    /// A periodic update (<c>threadedUpdatePeriodic</c>): the mover takes the S from before this update's recompute, then S is recomputed.
    /// </summary>
    public static void PeriodicTurn(CharacterCold cold)
    {
        cold.MoverTopSpeed = cold.StatsTopSpeed ?? cold.TopSpeedNow;
        Recompute(cold);
    }

    /// <summary>
    /// The stats' S (the information panel's run speed); <paramref name="now"/> for a character that has none yet (the init's recompute).
    /// </summary>
    public static float Stats(CharacterCold cold, float now) => cold.StatsTopSpeed ??= now;

    /// <summary>
    /// The mover's top speed (<see cref="CharacterHot.MaxSpeed"/>); <see cref="NewMoverSpeed"/> before its first periodic update.
    /// </summary>
    public static float Mover(CharacterCold cold) => cold.MoverTopSpeed ?? NewMoverSpeed;

    /// <summary>
    /// Forgets the speeds of a character leaving the world: one coming back is a new character, with a new mover and stats.
    /// </summary>
    public static void Forget(CharacterCold cold)
    {
        cold.TopSpeedNow = null;
        cold.StatsTopSpeed = null;
        cold.MoverTopSpeed = null;
    }

    /// <summary>
    /// CharStats <c>periodicUpdate</c>'s recompute, skipped while unconscious or dead (medical <c>+0x161</c>, <c>+0x164</c>) and before the first
    /// body step.
    /// </summary>
    static void Recompute(CharacterCold cold)
    {
        if (cold.Medical is { Unconscious: true } or { Dead: true } || cold.TopSpeedNow is not { } now) return;

        cold.StatsTopSpeed = now;
    }

    /// <summary>
    /// Character <c>+0x1a9</c>, in view (docs/game/game-loop.md "The character update budget"): on screen for the host's camera, else within
    /// <see cref="MedicalTimeBudget.NpcRange"/> of <see cref="World.ViewPosition"/>; without a camera nobody is.
    /// </summary>
    static bool InView(World world, Vector3 position) =>
        world.View is { } view ? MedicalClock.OnScreen(view, position)
            : world.ViewPosition is { } eye && Vector3.DistanceSquared(position, eye) <= MedicalTimeBudget.NpcRange * MedicalTimeBudget.NpcRange;
}
