using System.Numerics;

namespace Meitou.Simulation.Population;

/// <summary>
/// How <see cref="PopulationSystem.SpawnIntoPlayerSquad(World, int, string, Vector2, SpawnOptions)"/> makes its character (engine API, not in the
/// original): <see cref="Age"/> an ANIMAL_CHARACTER's age (1 the adult; a CHARACTER ignores it), and <see cref="IgnoreCaps"/> to add it past
/// <c>max squad size</c> and <c>max faction size</c>. More options may come.
/// </summary>
public sealed record SpawnOptions(float Age = 1, bool IgnoreCaps = false);
