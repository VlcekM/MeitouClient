using System.Numerics;

namespace Meitou.Game;

sealed partial class GameHost
{
    /// <summary>
    /// The GroundType of the building floor under a character's feet, -1 when the ground is not a building's (<see cref="Meitou.Game.Audio.CharacterSounds.Floor"/>, docs/audio.md "Surface"):
    /// the building's <c>interior material</c> when the character stands inside a building's indoor hull, else its <c>exterior material</c>.
    /// </summary>
    int FloorSurface(Vector3 feet) =>
        ActiveObjects is { } objects ? objects.FloorMaterialUnder(feet, objects.InteriorAt(feet, InsideMargin) is not null) : -1;
}
