using System.Numerics;

namespace Meitou.Simulation.Construction;

/// <summary>
/// A material entry of a site: the ITEM record, the amount needed and the amount delivered.
/// </summary>
public readonly record struct MaterialView(string Item, float Required, float Delivered);

/// <summary>
/// A player-built building or owned town-house repair as the host needs it after a tick (docs/simulation.md "Buildings"). <see cref="Id"/> is the
/// instance id (<c>&lt;n&gt;-zoneX.Y-INGAME</c>), or a town house's zone-qualified key; also the render placement id and the navmesh instance id.
/// Progress and <see cref="Target"/> are in material units.
/// </summary>
public sealed record BuildingSnapshot(
    string Id,
    string Record,
    Vector3 Position,
    Quaternion Rotation,
    int Floor,
    string? Parent,
    int Owner,
    float Progress,
    float Target,
    IReadOnlyList<MaterialView> Materials,
    bool Complete,
    bool Paused)
{
    /// <summary>
    /// The level-data placement the site repairs (null: player-built).
    /// </summary>
    public Meitou.Data.World.BuildingPlacement? LevelPlacement { get; init; }

    /// <summary>
    /// The BUILDING's name.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The name the player gave it (<see cref="BuildSite.CustomName"/>); null for none.
    /// </summary>
    public string? CustomName { get; init; }

    /// <summary>
    /// Whether it is destroyed.
    /// </summary>
    public bool Destroyed { get; init; }

    /// <summary>
    /// Whether a parented building sits outside its parent (such as an exterior wall light snapped to a building's light node).
    /// </summary>
    public bool Outside { get; init; }

    /// <summary>
    /// A camp building's town (<see cref="Population.NestBuildings"/>: the nest or town that stood it up), for the owner its name bar shows; none
    /// for any other.
    /// </summary>
    public Locks.FixtureTown CampTown { get; init; } = Locks.FixtureTown.None;

    /// <summary>
    /// Whether it wants work: destroyed, incomplete or below its target.
    /// </summary>
    public bool NeedsWork => Destroyed || !Complete || Progress < Target;

    /// <summary>
    /// A builder is dismantling it.
    /// </summary>
    public bool Dismantling { get; init; }

    /// <summary>
    /// The wall this short piece, connector or wedge is attached to (<see cref="BuildSite.WallParent"/>): its orders and materials are that wall's.
    /// </summary>
    public string? WallParent { get; init; }

    /// <summary>
    /// Open to the public (<see cref="BuildSite.Public"/>), and the name of the player town it belongs to (null: none).
    /// </summary>
    public bool Public { get; init; } = true;

    /// <summary>
    /// The name of the town it belongs to: the player's outpost, or a world town's for a building of a town (see <see cref="Public"/>).
    /// </summary>
    public string? TownName { get; init; }

    /// <summary>
    /// The TOWN record of the world town <see cref="TownName"/> names, for showing it through the mods' text sources; null for the player's outpost.
    /// </summary>
    public string? TownRecord { get; init; }
}
