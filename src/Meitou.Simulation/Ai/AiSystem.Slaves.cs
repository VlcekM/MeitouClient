using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The town data a player character's Obedient Slave job (187, Kidnap/ObedientSlave.cs) works with: the worker goals' finder (189 / 190) over the
/// town a point is in.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// Whether the town at <paramref name="at"/> has a mine, research bench, refinery or crafting bench to work (the finders of 189 / 190 return 0
    /// without one).
    /// </summary>
    internal bool HasWorkplaces(Vector3 at) => TownAt(at) is >= 0 and var site && Places(site).Workplaces.Count > 0;

    /// <summary>
    /// The building a worker works at in the town at <paramref name="at"/>, the clip it plays there and the building's functionality
    /// (<see cref="PickWork"/>: a random workplace, the roll the character's and its <paramref name="epoch"/> of picks); null when the town has none.
    /// </summary>
    internal (TownBuilding Building, string Clip, string? Functionality)? WorkAt(World world, CharacterId self, Vector3 at, int epoch) =>
        TownAt(at) is >= 0 and var site && PickWork(world, self, site, epoch) is { } building
            ? (building, _work.ClipOf(building), _work.FunctionalityOf(building))
            : null;
}