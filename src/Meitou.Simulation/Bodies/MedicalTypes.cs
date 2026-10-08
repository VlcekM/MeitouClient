using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public enum MedicalEventKind
{
    KnockedOut,
    WokeUp,
    /// <summary>A part went below zero (<see cref="MedicalEvent.Part"/>); a vital one has already knocked the character out.</summary>
    PartDown,
    PartSevered,
    Died,
}

public enum DeathCause
{
    None,
    /// <summary>A vital part destroyed.</summary>
    VitalPart,
    /// <summary>Blood at or below minus the capacity.</summary>
    BloodLoss,
    /// <summary>Hunger at 0.</summary>
    Starvation,
}

/// <summary>Something that happened to a character in a tick or a hit, for the caller to act on (XP, drops, messages, the commit phase).</summary>
public readonly record struct MedicalEvent(MedicalEventKind Kind, int Part = -1, DeathCause Cause = DeathCause.None);

/// <summary>A blow as the medical system takes it, after armour and toughness (docs/game/combat.md "The hit pipeline", step 5).</summary>
/// <param name="Cut">Cut damage.</param>
/// <param name="Blunt">Blunt damage; with <c>blunt permanent organ damage</c> 0 it only stuns.</param>
/// <param name="Pierce">Pierce damage.</param>
/// <param name="ExtraStun">Stun added on top.</param>
/// <param name="Scale">The hit scale of the blood-loss and wound terms.</param>
public readonly record struct HitDamage(float Cut, float Blunt, float Pierce, float ExtraStun = 0, float Scale = 1);
