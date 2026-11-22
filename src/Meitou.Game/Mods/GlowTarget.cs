using Meitou.Data.Characters;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// What a <see cref="GlowTarget"/> lights.
/// </summary>
public enum GlowTargetKind
{
    /// <summary>
    /// The whole drawn character: body, worn items and what it holds.
    /// </summary>
    Character,
    /// <summary>
    /// The blade of the melee weapon the character holds drawn.
    /// </summary>
    Weapon,
    /// <summary>
    /// The character's parts in one attach slot (its worn items there, or a carried weapon's for <see cref="AttachSlot.Weapon"/>).
    /// </summary>
    Worn,
}

/// <summary>
/// What a code mod's display-only glow lights (docs/modding.md "Glow"): made with <see cref="Character"/>, <see cref="Weapon"/> or
/// <see cref="Worn"/>. A character holds one glow per target; on a part that several of its targets cover, the weapon's or the slot's wins over
/// the whole character's.
/// </summary>
public readonly record struct GlowTarget
{
    /// <summary>
    /// What it lights.
    /// </summary>
    public GlowTargetKind Kind { get; init; }

    /// <summary>
    /// The character.
    /// </summary>
    public CharacterId On { get; init; }

    /// <summary>
    /// A <see cref="GlowTargetKind.Worn"/> target's slot (the game's <c>ATTACH_*</c>).
    /// </summary>
    public AttachSlot Slot { get; init; }

    /// <summary>
    /// The whole drawn character.
    /// </summary>
    public static GlowTarget Character(CharacterId on) => new() { Kind = GlowTargetKind.Character, On = on };

    /// <summary>
    /// The blade of the melee weapon the character holds drawn (from about 0.3 of the weapon to the tip; not sheathed, ranged or worn).
    /// </summary>
    public static GlowTarget Weapon(CharacterId on) => new() { Kind = GlowTargetKind.Weapon, On = on };

    /// <summary>
    /// The character's parts in <paramref name="slot"/> (e.g. <see cref="AttachSlot.Hat"/>, <see cref="AttachSlot.Body"/>).
    /// </summary>
    public static GlowTarget Worn(CharacterId on, AttachSlot slot) => new() { Kind = GlowTargetKind.Worn, On = on, Slot = slot };
}
