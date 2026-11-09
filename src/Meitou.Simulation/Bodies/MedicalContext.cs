using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// What the character is lying or standing on, which changes healing.
/// </summary>
public enum BedKind
{
    /// <summary>
    /// On the ground or standing: no bed.
    /// </summary>
    None = 0,
    /// <summary>
    /// A bed-like object of kind 6: the degeneration multiplier is forced to 0 and organic healing is multiplied by
    /// <see cref="MedicalContext.BedFactor"/>.
    /// </summary>
    Bed = 6,
    /// <summary>
    /// A repair bed of kind 0x19 (a Skeleton Bed): degeneration 0, robotic healing and wear repair multiplied by
    /// <see cref="MedicalContext.BedFactor"/>.
    /// </summary>
    RepairBed = 0x19,
}

/// <summary>
/// What the medical tick needs to know about its owner and the world. A plain value assembled by the caller (the world) per character and tick;
/// nothing in it is shared or mutated. The effective stats (toughness, strength) are passed in so the medical code does not depend on
/// <see cref="CharacterStats"/>.
/// </summary>
public readonly struct MedicalContext(GameConstants constants, BodyOptions options, RaceData race)
{
    /// <summary>
    /// The CONSTANTS the medical rates read.
    /// </summary>
    public GameConstants Constants { get; } = constants;
    /// <summary>
    /// The body options (engine choices and settings).
    /// </summary>
    public BodyOptions Options { get; } = options;
    /// <summary>
    /// The owner's race (heal and hunger rates, blood, parts).
    /// </summary>
    public RaceData Race { get; } = race;

    /// <summary>
    /// The toughness stat times the stats multiplier (CharStats <c>+0x90</c> x <c>+0x34</c>: 1 for people, the age for animals), as every toughness
    /// read of the medical code (<c>FUN_140643ae0</c>, <c>FUN_1406447d0</c>, <c>FUN_140644980</c>, <c>FUN_140652000</c>, <c>FUN_140657620</c>).
    /// </summary>
    public float Toughness { get; init; } = 1;

    /// <summary>
    /// The strength stat, for the blood capacity.
    /// </summary>
    public float Strength { get; init; } = 50;

    /// <summary>
    /// The owner has a stats object (false for simple creatures): the hit path severs limbs only for characters that have one.
    /// </summary>
    public bool HasStats { get; init; } = true;

    /// <summary>
    /// The owner is exempt from wear and limb loss (the object at character slot <c>+0x248</c>; meaning <b>Unknown</b>).
    /// </summary>
    public bool ExemptFromWear { get; init; }

    /// <summary>
    /// Character state 1: in a bed. Hunger uses <c>bed hunger rate</c> and blood recovers at <c>resting heal rate mult</c>.
    /// </summary>
    public bool Resting { get; init; }

    /// <summary>
    /// Neither moving nor fighting (<c>FUN_1408837a0</c>'s first test): the hunger rate factor is <see cref="UseHungerRate"/>, the idle 0.8 or 1.
    /// Otherwise (the default) it is 1 + (1 - e) x <c>encumbrance hunger rate</c>.
    /// </summary>
    public bool Still { get; init; }

    /// <summary>
    /// The original's <c>CharBody::amIdle</c> (KenshiLib 1ce4a16; <see cref="CharacterCold.AmIdle"/>): with <see cref="Still"/>, the hunger rate
    /// factor is <see cref="MedicalState.IdleHungerRate"/> (<c>FUN_1408837a0</c>, <b>Verified</b> in game, #134), unless <see cref="UseHungerRate"/>
    /// is set.
    /// </summary>
    public bool Idle { get; init; }

    /// <summary>
    /// Not moving or fighting on a machine or a chair: its functionality's <c>hunger rate</c> is the factor (CharStats <c>+0x178</c>,
    /// <c>FUN_1408837a0</c>, <b>Verified</b>); null: none, and a character standing still that is not idle takes that field's default 1.
    /// </summary>
    public float? UseHungerRate { get; init; }

    /// <summary>
    /// The second add of the frame time to the medical time (<c>FUN_1405cfd90</c>, <see cref="MedicalTimeBudget"/>): seconds the update budget gave
    /// since the last step. The part, blood and KO-timer rates run on dt plus this; hunger runs on dt alone (the game clock).
    /// </summary>
    public float BudgetSeconds { get; init; }

    /// <summary>
    /// Not safe to get up (<c>Character::isItSafeToGetUp</c> failing): a knockout that ends keeps the character down, playing dead
    /// (<see cref="MedicalState.PlayingDead"/>).
    /// </summary>
    public bool PlayDead { get; init; }

    /// <summary>
    /// The get-up test could not be asked: the character's senses faded while it was knocked out (engine: the original asks on the waking frame,
    /// Meitou's look is staggered). A knockout that ends keeps it down, still unconscious, until it has looked and <see cref="PlayDead"/> is decided
    /// (<see cref="BodySystem.Schedule"/>).
    /// </summary>
    public bool GetUpUnasked { get; init; }

    /// <summary>
    /// The hunger level the character is kept at for free (character slot <c>0x348</c>, <c>FUN_1405c9ca0</c>, read by the hunger update
    /// <c>FUN_14064da70</c>; docs/game/character-stats.md "Free food"): below it, with an empty stomach, the stomach gets min(floor - hunger, 0.04).
    /// -10 (the default) for the player's characters: they must eat.
    /// </summary>
    public float HungerFloor { get; init; } = NoHungerFloor;

    /// <summary>
    /// The <see cref="HungerFloor"/> of a character nobody feeds (the player's faction, and prisoners of the player): -10.
    /// </summary>
    public const float NoHungerFloor = -10;

    /// <summary>
    /// The encumbrance factor e in [0, 1] (<see cref="Encumbrance.Factor"/>).
    /// </summary>
    public float EncumbranceFactor { get; init; } = 1;

    /// <summary>
    /// What the character lies on (<see cref="BedKind.None"/> outside a bed).
    /// </summary>
    public BedKind Bed { get; init; }

    /// <summary>
    /// The bed's own healing value (Building <c>+0x3c0</c>: its <c>output rate</c> x the functionality's <c>production mult</c> x the research
    /// production multiplier).
    /// </summary>
    public float BedFactor { get; init; } = 1;

    /// <summary>
    /// A multiplier of the organic heal rate: 2 in character state 2 (held in a cage or on a pole), else 1.
    /// </summary>
    public float HealMult { get; init; } = 1;

    /// <summary>
    /// The repair rate when at a repair object, for wear repair (0 = none).
    /// </summary>
    public float RepairRate { get; init; }

    /// <summary>
    /// Multiplies <c>dt</c> for the body-part and blood rates (not hunger or the KO timer): a Meitou knob for tests and tuning; 1 is the original.
    /// </summary>
    public float BodyTimeScale { get; init; } = 1;

    /// <summary>
    /// The world seed and the character's key (<see cref="Rng.Key"/>), for the rolls of hits, severed limbs and KO timers.
    /// </summary>
    public ulong Seed { get; init; }
    /// <summary>
    /// The character's key (<see cref="Rng.Key"/>), the other half of the roll streams' key.
    /// </summary>
    public ulong CharacterKey { get; init; }
}