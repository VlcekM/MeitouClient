namespace Meitou.Simulation.Bodies;

/// <summary>The <c>Dismemberment</c> setting of <c>settings.cfg</c> (docs/game/character-stats.md, <b>Verified</b>; the install has 1).</summary>
public enum Dismemberment
{
    /// <summary>No limb is ever cut off.</summary>
    Never = 0,
    /// <summary>Only when one hit takes a part below its negative maximum (and the owner has a stats object).</summary>
    Rare = 1,
    /// <summary>Whenever a part's HP is below its negative maximum, degeneration included.</summary>
    Frequent = 2,
}

/// <summary>
/// The new-game options and settings that change the body mechanics (docs/game/character-stats.md "New-game advanced options"). They are not part of the
/// CONSTANTS record, so they are passed beside it. The defaults of the original are <b>Unknown</b> (zero in the exe image); 1 is the neutral value.
/// </summary>
public sealed record BodyOptions
{
    public static BodyOptions Default { get; } = new();

    /// <summary><b>Hunger time</b> (0.25 to 8): multiplies <c>starvation time</c>; higher means slower hunger.</summary>
    public float HungerTime { get; init; } = 1;
    /// <summary><b>Chance of death</b> (0.5 to 4): multiplies the bleed rate and the wound degeneration. Not starvation.</summary>
    public float ChanceOfDeath { get; init; } = 1;
    /// <summary><b>Global damage multiplier</b> (0.5 to 4): scales combat XP (and damage, combat.md).</summary>
    public float GlobalDamageMultiplier { get; init; } = 1;
    public Dismemberment Dismemberment { get; init; } = Dismemberment.Rare;
}
