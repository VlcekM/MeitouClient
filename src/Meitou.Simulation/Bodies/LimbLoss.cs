namespace Meitou.Simulation.Bodies;

/// <summary>
/// A body part's <see cref="LimbState"/> changing (<see cref="ILimbObserver"/>): the tick, the character, the part's index in
/// <see cref="MedicalState.Parts"/>, the part's template name, and its state before and after (severed to <see cref="LimbState.Stump"/>, crushed to
/// <see cref="LimbState.Crushed"/>, or fitted/removed prosthetic <see cref="LimbState.Replaced"/>).
/// </summary>
public readonly record struct LimbChange(long Tick, CharacterId Character, int Part, string PartName, LimbState Before, LimbState After)
{
    /// <summary>
    /// The limb was just severed or crushed (entered <see cref="LimbState.Stump"/> or <see cref="LimbState.Crushed"/>).
    /// </summary>
    public bool Lost => After is LimbState.Stump or LimbState.Crushed && Before is not (LimbState.Stump or LimbState.Crushed);
}

/// <summary>
/// A system told whenever a character's body part changes its <see cref="LimbState"/> (docs/modding.md "Character observers"): severed, crushed,
/// or prosthetic fitted/removed. Called serially in slot order once per tick after the slow world, or as the character leaves the world. It may
/// read the world and update its own hashed state, and must not mutate characters or other world state. One that throws is disabled for that world,
/// and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface ILimbObserver : ITickSystem
{
    /// <summary>
    /// A body part's limb state changed.
    /// </summary>
    void LimbChanged(World world, in LimbChange change);
}
