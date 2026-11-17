using Meitou.Data;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The two tests the original pairs wherever an animal and a building meet (docs/game/pathfinding.md "Squads travelling together"): the animal's
/// CharacterAnimal slot 101 (<see cref="MayEnter"/>) and the building's <c>allow animals</c> (<see cref="Allows"/>). The formation aim, state 59
/// and the Move order keep an animal out when neither lets it in (<see cref="KeptOut"/>); the patrol draw lets one in only when both do.
/// </summary>
public static class AnimalEntry
{
    /// <summary>
    /// The size under which an animal may enter a building that is not <c>cavernous</c> (<c>FUN_1405c81d0</c>, <c>DAT_1416e2bc4</c>,
    /// <b>Verified</b>): its <c>pathfind footprint radius</c> x its size.
    /// </summary>
    public const float Radius = 4.5f;

    /// <summary>
    /// CharacterAnimal slot 101 for a building (<c>FUN_1405c81d0</c>, <b>Verified</b>): its race lacks <c>cant enter buildings</c>
    /// (<see cref="Data.Gameplay.Bodies.RaceData.CanEnterBuildings"/>) and either its radius (<see cref="CombatSteering.Radius"/>) is under
    /// <see cref="Radius"/> or the building is <c>cavernous</c>.
    /// </summary>
    public static bool MayEnter(CharacterCold c, bool cavernous) =>
        c.Race?.CanEnterBuildings != false && (CombatSteering.Radius(c) < Radius || cavernous);

    /// <summary>
    /// <c>FUN_140547500</c> (<b>Verified</b>): byte <c>+0xe0</c> of the building's interior, which <c>FUN_140551660</c> copies from its
    /// <c>allow animals</c>; a record without <c>interior</c> references gets no interior (<c>FUN_140551b20</c>) and reads false.
    /// </summary>
    public static bool Allows(GameRecord building) => building.GetReferences("interior").Count > 0 && building.GetBool("allow animals");

    /// <summary>
    /// Whether a building keeps a character out as the formation aim (<c>FUN_1403335c0</c>), state 59 (<c>FUN_140597030</c>) and the Move order
    /// (<c>FUN_1407f9e20</c>) test it: an animal (a person's slot 101 is 1) whose slot 101 fails and whose building does not
    /// <paramref name="allows"/> animals.
    /// </summary>
    public static bool KeptOut(CharacterCold c, bool allows, bool cavernous) => SquadRegistry.IsAnimal(c) && !allows && !MayEnter(c, cavernous);

    /// <summary>
    /// <see cref="KeptOut(CharacterCold, bool, bool)"/> by a door of the building, which carries the building's flags
    /// (<see cref="Fixture.AdmitsAnimals"/>, <see cref="Fixture.Cavernous"/>).
    /// </summary>
    public static bool KeptOut(CharacterCold c, Fixture door) => KeptOut(c, door.AdmitsAnimals, door.Cavernous);
}
