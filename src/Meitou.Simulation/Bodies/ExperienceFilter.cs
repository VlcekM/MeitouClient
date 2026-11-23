using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// What earned an experience gain (<see cref="ExperienceGain"/>), by the activity of the <see cref="XpService"/> entry the game called.
/// </summary>
public enum ExperienceSource
{
    /// <summary>
    /// A direct <see cref="CharacterStats.Gain"/> call outside the game's own gains (a mod's).
    /// </summary>
    Other,
    /// <summary>
    /// A melee or bolt hit's events (dealt, glancing, defended, taken), a dodge, and mass combat.
    /// </summary>
    Combat,
    /// <summary>
    /// Toughness from being knocked out, losing a limb, a martial artist's own punch, getting up on the player's order near enemies, and being
    /// eaten alive.
    /// </summary>
    Injury,
    /// <summary>
    /// Crossbow reloading and aiming, and friendly fire.
    /// </summary>
    Ranged,
    /// <summary>
    /// Manning a turret.
    /// </summary>
    Turret,
    /// <summary>
    /// Athletics from moving, strength from carrying, swimming.
    /// </summary>
    Movement,
    /// <summary>
    /// Sneaking and the hearing checks.
    /// </summary>
    Stealth,
    /// <summary>
    /// A stealth knock-out swing and kidnapping.
    /// </summary>
    Assassination,
    /// <summary>
    /// Stealing and fencing.
    /// </summary>
    Thievery,
    /// <summary>
    /// Picking a lock.
    /// </summary>
    Lockpicking,
    /// <summary>
    /// Building and repairing.
    /// </summary>
    Building,
    /// <summary>
    /// Operating a machine (farming, cooking, crafting, research and the other work stats).
    /// </summary>
    Work,
    /// <summary>
    /// A training dummy.
    /// </summary>
    Training,
    /// <summary>
    /// First aid (the medic stats, which the game adds straight, past the core curve).
    /// </summary>
    FirstAid,
}

/// <summary>
/// An experience gain about to be added (<see cref="IExperienceFilter"/>): the world's tick, the character, the stat it trains, the amount (after
/// the Squad XP and Others' XP setting, before the core curve; for <see cref="ExperienceSource.FirstAid"/> and mass combat the amount added
/// straight) and its source; with the stat's stored level and the gain's cap.
/// </summary>
public readonly record struct ExperienceGain(long Tick, CharacterId Character, StatsEnumerated Skill, float Amount, ExperienceSource Source)
{
    /// <summary>
    /// The stat's stored level before the gain (no modifiers).
    /// </summary>
    public float Level { get; init; }

    /// <summary>
    /// The gain's cap. For a gain through the core curve, the curve's top: <see cref="CharacterStats.XpCap"/> (101), or a training dummy's level;
    /// the curve adds amount x ((cap - level) / cap)^2. For an amount added straight, the level from which no raise is taken (first aid's 100,
    /// mass combat's infinity).
    /// </summary>
    public float Cap { get; init; } = CharacterStats.XpCap;
}

/// <summary>
/// A system that scales or cancels experience gains (docs/modding.md "Experience"): asked for every gain the game makes to one of a character's own
/// stats in the world (not for a code mod's skill, also when a weapon naming it trains it), from whatever thread made it (a tick's parallel phases,
/// its serial steps, or the paused interface's commands between ticks on the host's thread, with the coming tick's number), in system order, each
/// filter seeing the amount as the earlier ones left it, until one answers 0. It must be thread-safe, quick and deterministic: a pure read of the
/// gain and of world state the tick has settled; it must not grant experience itself (that would ask it again, without end). A filter that throws
/// or answers an amount that is negative or not a finite number leaves that gain as it came; it is still asked for the rest of that tick and not
/// from the next, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IExperienceFilter : ITickSystem
{
    /// <summary>
    /// The amount to add for <paramref name="gain"/> (0 cancels it).
    /// </summary>
    float Gain(World world, in ExperienceGain gain);

    /// <summary>
    /// The gain to make of <paramref name="gain"/>: its <see cref="ExperienceGain.Amount"/> and <see cref="ExperienceGain.Cap"/> are taken (a cap
    /// above 0, infinity allowed; nothing else of the answer is read). By default <see cref="Gain"/>'s amount with the cap as it came; a filter that
    /// moves the curve's top (a level cap above 101, a dummy trained past its level) overrides it.
    /// </summary>
    ExperienceGain Adjust(World world, in ExperienceGain gain) => gain with { Amount = Gain(world, gain) };
}
