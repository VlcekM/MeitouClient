using System.Numerics;

namespace Meitou.Simulation.Construction;

/// <summary>A material entry of a site: the ITEM record, the amount needed and the amount delivered.</summary>
public readonly record struct MaterialView(string Item, float Required, float Delivered);

/// <summary>
/// A player-built building or owned town-house repair as the host needs it after a tick (docs/simulation.md "Buildings"). <see cref="Id"/> is the instance id (<c>&lt;n&gt;-zoneX.Y-INGAME</c>), or a town house's zone-qualified key; also the
/// render placement id and the navmesh instance id. Progress and <see cref="Target"/> are in material units.
/// </summary>
public sealed record BuildingSnapshot(string Id, string Record, Vector3 Position, Quaternion Rotation, int Floor, string? Parent, int Owner,
    float Progress, float Target, IReadOnlyList<MaterialView> Materials, bool Complete, bool Paused)
{
    public Meitou.Data.World.BuildingPlacement? LevelPlacement { get; init; }
    public string Name { get; init; } = "";
    public bool Destroyed { get; init; }
    public bool NeedsWork => Destroyed || !Complete || Progress < Target;
    /// <summary>A builder is dismantling it.</summary>
    public bool Dismantling { get; init; }
    /// <summary>The wall this short piece, connector or wedge is attached to (<see cref="BuildSite.WallParent"/>): its orders and materials are that wall's.</summary>
    public string? WallParent { get; init; }
    /// <summary>Open to the public (<see cref="BuildSite.Public"/>), and the name of the player town it belongs to (null: none).</summary>
    public bool Public { get; init; } = true;
    public string? TownName { get; init; }
}
