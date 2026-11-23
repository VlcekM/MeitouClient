namespace Meitou.Simulation.Bodies;

/// <summary>
/// What changed a character's health (<see cref="HealthChange.Kind"/>); room for more kinds.
/// </summary>
public enum HealthChangeKind : byte
{
    /// <summary>
    /// A change no step names (a debug command, a dialogue effect, an off-screen recovery, a code mod's own change, the paused interface), found
    /// by the world's sweep.
    /// </summary>
    Other,
    /// <summary>
    /// A melee blow, a bolt, an execution blow, or a martial artist's punch on its own limb.
    /// </summary>
    Hit,
    /// <summary>
    /// The body's own update: bleeding, blood regained, healing (flesh up, bandage turned into flesh, stun recovering), a wound worsening.
    /// </summary>
    Body,
    /// <summary>
    /// First aid: bandaging, closing a wound, a splint.
    /// </summary>
    FirstAid,
    /// <summary>
    /// The weather and the ground: acid water, acid ground, acid rain, burning, gas, a dust storm.
    /// </summary>
    Environment,
    /// <summary>
    /// Eaten alive by a captor.
    /// </summary>
    Eaten,
    /// <summary>
    /// Stretched on a rack.
    /// </summary>
    Rack,
}

/// <summary>
/// Which health value changed (<see cref="HealthChange.Value"/>).
/// </summary>
public enum HealthValue : byte
{
    /// <summary>
    /// A part's flesh HP (<see cref="HealthPart.Flesh"/>).
    /// </summary>
    Flesh,
    /// <summary>
    /// A part's stun damage (<see cref="HealthPart.Stun"/>), where blunt damage lands.
    /// </summary>
    Stun,
    /// <summary>
    /// A part's treated damage (<see cref="HealthPart.Bandage"/>).
    /// </summary>
    Bandage,
    /// <summary>
    /// A part's splint buffer (<see cref="HealthPart.Rig"/>).
    /// </summary>
    Rig,
    /// <summary>
    /// The character's blood (<see cref="MedicalState.Blood"/>; <see cref="HealthChange.Part"/> is -1).
    /// </summary>
    Blood,
}

/// <summary>
/// One change of one health value of a character in the world (<see cref="IHealthChangeObserver"/>): the tick it is told in, the character, what
/// changed it, the value, the part's index in <see cref="MedicalState.Parts"/> (-1 for blood), and the value before and after.
/// </summary>
public readonly record struct HealthChange(long Tick, CharacterId Character, HealthChangeKind Kind, HealthValue Value, int Part, float Before,
    float After)
{
    /// <summary>
    /// After minus before.
    /// </summary>
    public float Delta => After - Before;
}

/// <summary>
/// A system told of every change to the health of a character in the world (docs/modding.md "Health changes"): each part's flesh, stun, bandage and
/// splint and the blood, whatever changed them, with its kind. Called serially, never during a parallel phase, once per tick after the slow world
/// (and the deaths' sweep), in slot order, each character's changes in the order its steps made them (a step that changes a value twice tells one
/// change), or as the character leaves the world. The character is still in the world's table during the call. It may read the world and change
/// its own hashed state, and must not change characters or other world state (gameplay changes go through commands). One that throws is disabled
/// for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IHealthChangeObserver : ITickSystem
{
    /// <summary>
    /// A health value changed.
    /// </summary>
    void HealthChanged(World world, in HealthChange change);
}
