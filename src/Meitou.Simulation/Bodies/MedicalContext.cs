using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>What the character is lying or standing on, which changes healing.</summary>
public enum BedKind
{
    None = 0,
    /// <summary>A bed-like object of kind 6: the degeneration multiplier is forced to 0 and organic healing is multiplied by <see cref="MedicalContext.BedFactor"/>.</summary>
    Bed = 6,
    /// <summary>A repair bed of kind 0x19 (a Skeleton Bed): degeneration 0, robotic healing and wear repair multiplied by <see cref="MedicalContext.BedFactor"/>.</summary>
    RepairBed = 0x19,
}

/// <summary>
/// What the medical tick needs to know about its owner and the world. A plain value assembled by the caller (the world) per character and tick; nothing
/// in it is shared or mutated. The effective stats (toughness, strength) are passed in so the medical code does not depend on <see cref="CharacterStats"/>.
/// </summary>
public readonly struct MedicalContext(GameConstants constants, BodyOptions options, RaceData race)
{
    public GameConstants Constants { get; } = constants;
    public BodyOptions Options { get; } = options;
    public RaceData Race { get; } = race;

    /// <summary>The toughness stat (times the scale slot at <c>CharStats+0x34</c>, 1.0 for humans).</summary>
    public float Toughness { get; init; } = 1;

    /// <summary>The strength stat, for the blood capacity.</summary>
    public float Strength { get; init; } = 50;

    /// <summary>The owner has a stats object (false for simple creatures): the hit path severs limbs only for characters that have one.</summary>
    public bool HasStats { get; init; } = true;

    /// <summary>The owner is exempt from wear and limb loss (the object at character slot <c>+0x248</c>; meaning <b>Unknown</b>).</summary>
    public bool ExemptFromWear { get; init; }

    /// <summary>Character state 1: resting. Hunger uses <c>bed hunger rate</c> and blood recovers at <c>resting heal rate mult</c>.</summary>
    public bool Resting { get; init; }

    /// <summary>Using a machine: the hunger rate factor is 0.8 (probably the BUILDING's <c>hunger rate</c>).</summary>
    public bool UsingMachine { get; init; }

    /// <summary>The encumbrance factor e in [0, 1] (<see cref="Encumbrance.Factor"/>).</summary>
    public float EncumbranceFactor { get; init; } = 1;

    public BedKind Bed { get; init; }

    /// <summary>The bed's own healing value (virtual <c>+0x3c0</c>; the BUILDING field is <b>Unknown</b>).</summary>
    public float BedFactor { get; init; } = 1;

    /// <summary>An extra healing multiplier (the original heals twice as fast in its state 2).</summary>
    public float HealMult { get; init; } = 1;

    /// <summary>The repair rate when at a repair object, for wear repair (0 = none).</summary>
    public float RepairRate { get; init; }

    /// <summary>
    /// Multiplies <c>dt</c> for the body-part and blood rates (not hunger or the KO timer). 1 is the documented behaviour, where <c>dt</c> is game hours; the documented
    /// rates (a 30-point stun taking about 2400 hours to clear) look slow for hours, so the original may feed those updates a larger <c>dt</c>. An engine knob until
    /// a game session settles the unit (character-stats.md, Unknowns).
    /// </summary>
    public float BodyTimeScale { get; init; } = 1;

    /// <summary>The world seed and the character's key (<see cref="Rng.Key"/>), for the rolls of hits, severed limbs and KO timers.</summary>
    public ulong Seed { get; init; }
    public ulong CharacterKey { get; init; }
}
