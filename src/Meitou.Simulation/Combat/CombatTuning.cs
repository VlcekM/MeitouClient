namespace Meitou.Simulation.Combat;

/// <summary>Engine choices of the duel loop where the research stops (each is labelled <b>Unknown</b> in docs/simulation.md "Combat").</summary>
public static class CombatTuning
{
    /// <summary>Seconds a fighter waits before an attack after being ordered, and between attacks (a uniform draw between the two).</summary>
    public const float PauseMin = 0.15f, PauseMax = 0.6f;
    /// <summary>Seconds a heavy hit (a damage sum above the stumble threshold) staggers its target; a light hit does not.</summary>
    public const float HeavyHitStun = 0.5f;
    /// <summary>How far (world units) a defender looks for blows coming at it.</summary>
    public const float ReactionScanRadius = 60;
    /// <summary>A blow lands if the gap between the two bodies is still within the reach it started with plus this slack; else it misses.</summary>
    public const float ReachSlack = 6;
    /// <summary>Seconds a clip plays at when its length is not known.</summary>
    public const float DefaultClipSeconds = 1;
    /// <summary>A fighter closes in until the gap between bodies is this (units), when it moves by itself.</summary>
    public const float CloseInGap = 3;

    /// <summary>The footprint radius of a human, used when a character has no race data.</summary>
    public const float DefaultFootprint = 4;

    /// <summary>The radius a body counts with when the gap between two bodies is taken: the race's <c>pathfind footprint radius</c>.</summary>
    internal static float Footprint(CharacterCold? c) => c?.Race?.PathfindFootprintRadius ?? DefaultFootprint;
}
