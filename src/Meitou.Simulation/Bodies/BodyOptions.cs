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
/// CONSTANTS record, so they are passed beside it. The defaults are the original's (1, its reset <c>FUN_1403e75d0</c>).
/// </summary>
public sealed record BodyOptions
{
    /// <summary>A new instance each time: <see cref="Dismemberment"/> is changed on the running world's instance, which must not be a shared one.</summary>
    public static BodyOptions Default => new();

    /// <summary><b>Hunger time</b> (0.25 to 8): multiplies <c>starvation time</c>; higher means slower hunger.</summary>
    public float HungerTime { get; init; } = 1;
    /// <summary><b>Chance of death</b> (0.5 to 4): multiplies the bleed rate and the wound degeneration. Not starvation.</summary>
    public float ChanceOfDeath { get; init; } = 1;
    /// <summary><b>Global damage multiplier</b> (0.5 to 4): scales combat XP (and damage, combat.md). Settable: the debug menu changes it in a running world (<see cref="DebugDamageMultiplier"/>).</summary>
    public float GlobalDamageMultiplier { get; set; } = 1;
    /// <summary>Settable: the original reads its global when a part is damaged, so a change on the Gameplay page reaches the running world at once.</summary>
    public Dismemberment Dismemberment { get; set; } = Dismemberment.Rare;
    /// <summary>
    /// Meitou (on; Faithful off, the original never cuts a head off): a blow that destroys the head also cuts it off, under the rule a limb is cut off by
    /// (<see cref="MedicalState.Decapitated"/>). Settable, as <see cref="Dismemberment"/>.
    /// </summary>
    public bool Decapitation { get; set; } = true;
    /// <summary>
    /// Meitou (on; Faithful off, the original starts a ragdoll at rest with hard joint limits): a new whole-body ragdoll goes on with the character's motion and the downing blow's push
    /// (its arms and legs spun by it), its joints get soft limits and a limp body a relaxed pull (docs/game/ragdolls.md "Advanced ragdolls (Meitou)"), and a cut-off
    /// head is thrown with it (<see cref="Ragdolls.RagdollSystem"/>, <c>CombatSystem.Knockdown</c>). Settable, as <see cref="Dismemberment"/>.
    /// </summary>
    public bool AdvancedRagdolls { get; set; } = true;
    /// <summary>
    /// Debug (F10 "Force hits to", <see cref="DebugForceHits"/>): every blow lands on the part of this template string id, else of this name, when the victim has it and a blow
    /// could pick it; else as usual. Null: off. Not saved.
    /// </summary>
    public (string Id, string Name)? ForcedHitPart { get; set; }
}
