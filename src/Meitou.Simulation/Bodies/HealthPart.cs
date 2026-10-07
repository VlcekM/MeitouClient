using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary><c>LimbState</c> of a body part (docs/game/character-stats.md, <b>Verified</b>: the editor enum and the tests in the medical code).</summary>
public enum LimbState
{
    Original = 0,
    Stump = 1,
    Replaced = 2,
    Crushed = 3,
}

/// <summary>
/// One body part's state (<c>MedicalSystem::HealthPartStatus</c>, docs/game/character-stats.md "Per-part state"). Field names follow the save keys
/// <c>flesh</c>, <c>hit</c>, <c>bandage</c>, <c>rig</c>, <c>stun</c>, <c>wear</c>, <c>hitmult</c> and <c>sid</c> (docs/formats/save.md); in the formulas
/// they are h, s, b, extra and w. Owned by one <see cref="MedicalState"/>.
/// </summary>
public sealed class HealthPart(BodyPartTemplate template, int hitWeight, float baseHp)
{
    /// <summary>The LOCATIONAL_DAMAGE record; its string id is the save's <c>sid&lt;k&gt;</c>.</summary>
    public BodyPartTemplate Template { get; } = template;

    /// <summary>Save key <c>hit&lt;k&gt;</c>: the relative chance to be struck (the race's anatomy weight).</summary>
    public int HitWeight { get; set; } = hitWeight;

    /// <summary>Save key <c>hitmult&lt;k&gt;</c>: the transient hit-focus multiplier, 1 at rest, +1 per hit up to 4, decaying back.</summary>
    public float HitMult { get; set; } = 1;

    /// <summary>Save key <c>flesh&lt;k&gt;</c>: flesh HP, h. May go negative, down to -3 times the maximum.</summary>
    public float Flesh { get; set; }

    /// <summary>Save key <c>stun&lt;k&gt;</c>: s. Blunt damage lands here and recovers.</summary>
    public float Stun { get; set; }

    /// <summary>Save key <c>bandage&lt;k&gt;</c>: b, damage that has been treated and waits to be healed.</summary>
    public float Bandage { get; set; }

    /// <summary>Save key <c>rig&lt;k&gt;</c>: the extra buffer used by the health fraction.</summary>
    public float Rig { get; set; }

    /// <summary>Save key <c>wear&lt;k&gt;</c>: w, the permanent loss of maximum (robots and fitted limbs).</summary>
    public float Wear { get; set; }

    /// <summary>The race's base HP for this part (100 for humans), before the stump and replacement rules.</summary>
    public float BaseHp { get; set; } = baseHp;

    /// <summary>The HP multiplier (the character's scale slot; 1.0 for humans).</summary>
    public float HpMultiplier { get; set; } = 1;

    /// <summary>The body scale (the character's scale slot; 1.0 for humans).</summary>
    public float BodyScale { get; set; } = 1;

    /// <summary>RACE <c>self healing</c>: untreated damage turns slowly into treated damage while the owner is conscious.</summary>
    public bool SelfHealing { get; set; }

    public LimbState Limb { get; set; }

    /// <summary>The fitted prosthetic when <see cref="Limb"/> is <see cref="LimbState.Replaced"/>.</summary>
    public LimbReplacement? Replacement { get; set; }

    /// <summary>The prosthetic's quality interpolation parameter in [0, 1].</summary>
    public float ReplacementQuality { get; set; }

    /// <summary>The part's own state flag for edge detection of <see cref="MedicalEventKind.PartDown"/> events.</summary>
    internal bool WasDown { get; set; }

    /// <summary>Base HP in force: 5 for a stump, the prosthetic's HP when one is fitted, else the race's.</summary>
    public float EffectiveBaseHp => Limb switch
    {
        LimbState.Stump => 5,
        LimbState.Replaced when Replacement is not null => Replacement.Hp(ReplacementQuality),
        _ => BaseHp,
    };

    /// <summary>M = base x multiplier x scale - wear: the effective maximum HP.</summary>
    public float MaxHp => EffectiveBaseHp * HpMultiplier * BodyScale - Wear;

    /// <summary>The health fraction <c>((h - s) + extra) / M</c> (<c>+0x60</c>); -1 when the maximum has worn away.</summary>
    public float Fraction => MaxHp > 0 ? (Flesh - Stun + Rig) / MaxHp : -1;

    /// <summary>The part is down when h - s + extra &lt; 0: its collapse mask applies, and a vital part knocks the character out.</summary>
    public bool IsDown => Flesh - Stun + Rig < 0;

    /// <summary>A part is destroyed in the hit path when h - s + extra &lt; -M.</summary>
    public bool IsDestroyed => Flesh - Stun + Rig < -MaxHp;

    public bool IsVital => Template.Vital;

    /// <summary>Parts of robots, and parts with a prosthetic, heal by repair, wear and do not degenerate (unless vital).</summary>
    public bool IsRobotic(RaceData race) => race.IsRobot || Limb == LimbState.Replaced;
}
