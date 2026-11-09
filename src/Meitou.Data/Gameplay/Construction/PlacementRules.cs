using System.Numerics;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>
/// Why a ghost may not be placed: one value per message of docs/game/buildings-production.md "Placement", plus the data checks of the engine.
/// </summary>
public enum PlacementFailure : byte
{
    /// <summary>
    /// Nothing fails: the ghost may be placed.
    /// </summary>
    None,
    /// <summary>
    /// "Can't build too close to another town.": a non-player town is within its block radius.
    /// </summary>
    TooCloseToTown,
    /// <summary>
    /// "Can't place in a building you don't own.": the town block with the ghost inside or on a building.
    /// </summary>
    NotOwnedBuilding,
    /// <summary>
    /// "No resources at this location, mining would be pointless.": the resource value is under 0.01.
    /// </summary>
    NoResources,
    /// <summary>
    /// "This crop will not grow in this environment.": the crop yield is under 0.01.
    /// </summary>
    CropWillNotGrow,
    /// <summary>
    /// "Cannot build inside incomplete buildings."
    /// </summary>
    InsideIncomplete,
    /// <summary>
    /// "Must be placed outside or on a roof."
    /// </summary>
    MustBeOutside,
    /// <summary>
    /// "Must be placed inside a building."
    /// </summary>
    MustBeInside,
    /// <summary>
    /// "Can only be placed on certain locations (marked by blue nodes).": a snapping ghost not on a snap node.
    /// </summary>
    NeedsSnapNode,
    /// <summary>
    /// "Too close to another building.": a footprint node box overlaps physics shapes or another queued ghost.
    /// </summary>
    TooCloseToBuilding,
    /// <summary>
    /// "No valid usage nodes.": every usage node is invalid.
    /// </summary>
    NoValidUsageNodes,
    /// <summary>
    /// "There are characters in the way."
    /// </summary>
    CharactersInTheWay,
    /// <summary>
    /// "The building blocks another building use nodes."
    /// </summary>
    BlocksUseNodes,
    /// <summary>
    /// "The building has one or more parts on invalid ground.": a LAND part's node centre is under the water level.
    /// </summary>
    InvalidGround,
    /// <summary>
    /// "Invalid placing floor number. ...": a node overlaps floor collision above floor 0, or a ceiling ghost finds no ceiling.
    /// </summary>
    InvalidFloor,
    /// <summary>
    /// "Ground needs to be more level.": the vertical margins or the slope.
    /// </summary>
    NotLevel,
    /// <summary>
    /// Engine: the record is not a BUILDING or not research-unlocked.
    /// </summary>
    NotAvailable,
}

/// <summary>
/// A ghost's resource reading (<see cref="PlacementRules.Readout"/>): the MiningResource, its value 0..1, and for a farm the crop yield and the
/// environment's names (empty indoors).
/// </summary>
public sealed record PlacementReadout(int Resource, float Value, float? CropYield = null, IReadOnlyList<string>? Environment = null);

/// <summary>
/// A placed town as the placement rules see it.
/// </summary>
public readonly record struct PlacementTown(TownData Town, Vector2 Position);

/// <summary>
/// The deterministic half of the placement checks (docs/simulation.md "Buildings"): pure functions of the data, run by the host for the ghost and
/// again by the simulation on <c>PlaceBuildings</c> (the town block; <see cref="ResourceBlock"/> is the host's only). The geometric half lives in
/// the host.
/// </summary>
public static class PlacementRules
{
    /// <summary>
    /// A building that creates a player town keeps at least this far from any town centre (<b>Verified</b>, <c>FUN_140926eb0</c>).
    /// </summary>
    public const float OutpostMinimumDistance = 2000;

    /// <summary>
    /// The block radius multiple of a building that creates a player town: 2.5 of the town's radius, 1 for ruins and points of interest.
    /// </summary>
    public const float OutpostRadiusFactor = 2.5f;

    /// <summary>
    /// The minimum resource value or crop yield below which a mine or farm is refused (<b>Verified</b>,
    /// docs/game/buildings-production.md "Placement").
    /// </summary>
    public const float MinimumYield = 0.01f;

    /// <summary>
    /// The town block of docs/game/buildings-production.md "Placement" (Towns): a town of another faction blocks within its radius
    /// (<see cref="TownData.Radius"/>, 0 for a null town which then blocks only outposts); a building that creates a player town within 2.5 x that
    /// radius (1 x for ruins and points of interest) or <see cref="OutpostMinimumDistance"/> of the centre. Nest markers (never a candidate,
    /// <c>FUN_140927f10</c>) and towns of the player's faction never block, nor does any town for interior furniture or a node standing in a building
    /// it may be built in (<paramref name="inOwnedBuilding"/>, <see cref="MayBuildIn"/>; <c>FUN_1404dba40</c>).
    /// <see cref="PlacementFailure.NotOwnedBuilding"/> (the second text of the first row) is the host's: it needs the building under the ghost.
    /// </summary>
    public static PlacementFailure TownBlock(
        BuildingBuildData building, Vector2 at, IReadOnlyList<PlacementTown> towns, string playerFactionId, bool inOwnedBuilding = false)
    {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(towns);
        if (inOwnedBuilding && (building.IsInteriorFurniture || building.IsNode)) return PlacementFailure.None;

        foreach (var placed in towns)
        {
            var town = placed.Town;
            if (town.Type == TownType.NestMarker) continue;
            if (playerFactionId.Length > 0 && town.Faction == playerFactionId) continue;

            float limit = OutpostLimit(building, town);
            if (Vector2.DistanceSquared(at, placed.Position) < limit * limit) return PlacementFailure.TooCloseToTown;
        }

        return PlacementFailure.None;
    }

    /// <summary>
    /// The distance a town blocks at for a building: its radius, or for an outpost the larger of its scaled radius and
    /// <see cref="OutpostMinimumDistance"/>.
    /// </summary>
    static float OutpostLimit(BuildingBuildData building, TownData town)
    {
        float radius = town.Radius;
        if (!building.CreatesPlayerTown) return radius;

        float factor = town.Type is TownType.Ruins or TownType.PointOfInterest ? 1 : OutpostRadiusFactor;
        return MathF.Max(factor * radius, OutpostMinimumDistance);
    }

    /// <summary>
    /// Whether a ghost may stand in a level building (<b>Verified</b> by decompile, <c>FUN_1404d69c0</c>): one a player faction owns; else, for a
    /// ghost that does not create a player town, a ruin or one whose owner faction is <c>not real</c> (a building without an owner has "nofac",
    /// which is).
    /// </summary>
    public static bool MayBuildIn(BuildingBuildData building, bool playerOwned, bool destroyed, bool ownerNotReal)
    {
        ArgumentNullException.ThrowIfNull(building);
        return playerOwned || !building.CreatesPlayerTown && (destroyed || ownerNotReal);
    }

    /// <summary>
    /// What the ghost of a mine, well or farm reads at <paramref name="at"/> (docs/game/buildings-production.md "Placement", the resource popup): the
    /// resource's value clamped to 0..1 (<see cref="ResourceField.Yield"/>; a farm's the fertility factor of the ground's), and for a farm the crop
    /// yield (fertility factor x crop match) and its environment. Null for other buildings, without an areas map, or for IRON, COPPER and CARBON
    /// without known rocks (nothing is shown or refused then).
    /// </summary>
    public static PlacementReadout? Readout(MachineData? machine, ResourceField field, Vector3 at)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (machine is null || !field.HasMap) return null;

        if (machine.Farm is { } farm)
        {
            float ground = Math.Clamp(field.Value(MachineData.GroundResource, at.X, at.Y, at.Z), 0, 1);
            float fertility = farm.FertilityFactor(ground);
            float cropYield = fertility * field.CropMatch(farm, at.X, at.Z, farm.Indoor);
            IReadOnlyList<string>? environment = farm.Indoor ? [] : Environment(field.Coverage(at.X, at.Z));
            return new(MachineData.GroundResource, fertility, cropYield, environment);
        }

        if (machine.Resource == 0 || machine.Function == MachineData.NaturalMineFunction) return null;

        bool rockResource = machine.Resource is ResourceField.IronResource or ResourceField.CopperResource or ResourceField.CarbonResource;
        if (!field.HasNodes && rockResource) return null;

        return new(machine.Resource, Math.Clamp(field.Yield(machine.Resource, at), 0, 1));
    }

    /// <summary>
    /// The environment line's names (<b>Observed</b>, <c>FUN_1404d9ea0</c>): of Green, Arid and Swamp (map keys 1, 2, 3) the first with the largest
    /// coverage, and the next with the same coverage after it; "Dead" when the largest is below 0.1.
    /// </summary>
    static string[] Environment((float Green, float Arid, float Swamp) c)
    {
        (string Name, float Value)[] all = [("Green", c.Green), ("Arid", c.Arid), ("Swamp", c.Swamp)];
        int top = 0;
        for (int i = 1; i < all.Length; i++)
            if (all[i].Value > all[top].Value) top = i;

        if (all[top].Value < 0.1f) return ["Dead"];

        for (int i = top + 1; i < all.Length; i++)
            if (all[i].Value == all[top].Value) return [all[top].Name, all[i].Name];

        return [all[top].Name];
    }

    /// <summary>
    /// The resource row of docs/game/buildings-production.md "Placement": a farm whose fertility or crop yield, or another building whose resource
    /// value, is below <see cref="MinimumYield"/>. None without a readout.
    /// </summary>
    public static PlacementFailure ResourceBlock(PlacementReadout? readout) => readout switch
    {
        { CropYield: { } crop } when readout.Value < MinimumYield || crop < MinimumYield => PlacementFailure.CropWillNotGrow,
        { CropYield: null } when readout.Value < MinimumYield => PlacementFailure.NoResources,
        _ => PlacementFailure.None,
    };

    /// <summary>
    /// The text the original shows for a failure ("" for <see cref="PlacementFailure.None"/>); the engine's own for
    /// <see cref="PlacementFailure.NotAvailable"/>.
    /// </summary>
    public static string Message(PlacementFailure failure) => failure switch
    {
        PlacementFailure.None => "",
        PlacementFailure.TooCloseToTown => "Can't build too close to another town.",
        PlacementFailure.NotOwnedBuilding => "Can't place in a building you don't own.",
        PlacementFailure.NoResources => "No resources at this location, mining would be pointless.",
        PlacementFailure.CropWillNotGrow => "This crop will not grow in this environment.",
        PlacementFailure.InsideIncomplete => "Cannot build inside incomplete buildings.",
        PlacementFailure.MustBeOutside => "Must be placed outside or on a roof.",
        PlacementFailure.MustBeInside => "Must be placed inside a building.",
        PlacementFailure.NeedsSnapNode => "Can only be placed on certain locations (marked by blue nodes).",
        PlacementFailure.TooCloseToBuilding => "Too close to another building.",
        PlacementFailure.NoValidUsageNodes => "No valid usage nodes.",
        PlacementFailure.CharactersInTheWay => "There are characters in the way.",
        PlacementFailure.BlocksUseNodes => "The building blocks another building use nodes.",
        PlacementFailure.InvalidGround => "The building has one or more parts on invalid ground.",
        PlacementFailure.InvalidFloor => "Invalid placing floor number.",
        PlacementFailure.NotLevel => "Ground needs to be more level.",
        PlacementFailure.NotAvailable => "That building is not available yet.",
        _ => "",
    };
}
