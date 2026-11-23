using System.Numerics;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Modding;

/// <summary>
/// What a placement rule is asked about (docs/modding.md "Placement rules"): the building to place, its position and turn, and where it stands.
/// </summary>
/// <param name="Building">The BUILDING's build data (its record id, furniture flags and the rest).</param>
/// <param name="Position">The ghost's absolute position.</param>
/// <param name="Yaw">The ghost's turn about the vertical, radians.</param>
/// <param name="Parent">The building it stands in or on (a site's id or a level building's instance id); null outdoors.</param>
/// <param name="Floor">The floor it stands on inside its parent (0 outdoors).</param>
/// <param name="WallPiece">A piece of a wall line being laid.</param>
public readonly record struct PlacementAsk(BuildingBuildData Building, Vector3 Position, float Yaw, string? Parent, int Floor, bool WallPiece);

/// <summary>
/// A code mod's placement rule (<see cref="ModContext.AddPlacementRule"/>). Both parts are optional.
/// </summary>
public sealed class PlacementRule
{
    /// <summary>
    /// Whether to lift the game's own reason for refusing the ghost (true: the check goes on to its next step). Never asked about
    /// the town block, which the simulation applies again.
    /// </summary>
    public Func<PlacementAsk, PlacementFailure, bool>? Waive { get; init; }

    /// <summary>
    /// The text to refuse a ghost the game allows with, shown as build mode's message; null to let it stand.
    /// </summary>
    public Func<PlacementAsk, string?>? Refuse { get; init; }
}

/// <summary>
/// A mod's placement rule as registered; faulted once it threw.
/// </summary>
sealed class ModPlacementRule(string owner, PlacementRule rule)
{
    /// <summary>
    /// The id of the mod that registered it.
    /// </summary>
    public string Owner => owner;

    /// <summary>
    /// The rule.
    /// </summary>
    public PlacementRule Rule => rule;

    /// <summary>
    /// Set once it threw; a faulted rule is not asked again.
    /// </summary>
    public bool Faulted { get; set; }
}
