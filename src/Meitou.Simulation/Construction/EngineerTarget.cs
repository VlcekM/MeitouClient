using System.Numerics;

namespace Meitou.Simulation.Construction;

/// <summary>
/// What the engineer job works (<see cref="ConstructionSystem.EngineerWork"/>): a site (<paramref name="Site"/>) or a door fixture of a complete one
/// (<paramref name="Site"/> null), by its id, at <paramref name="Position"/>.
/// </summary>
/// <param name="Id">The site's or the door fixture's id: the work's <see cref="CharacterCold.BuildSite"/>.</param>
/// <param name="Position">Where it is scored and its job range measured from.</param>
/// <param name="Site">The site, null for a door.</param>
public readonly record struct EngineerTarget(string Id, Vector3 Position, BuildSite? Site);
