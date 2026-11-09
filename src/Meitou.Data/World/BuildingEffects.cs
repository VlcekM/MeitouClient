using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// The EFFECT instances of the world's placed buildings and of their exterior layout objects (docs/formats/lights.md, "Effects on buildings"),
/// placed by <see cref="WorldObjectLayout.Building(GameDatabase, GameRecord, string, Vector3, Quaternion, BuildingState?, List{PlacedLight}?, List{PlacedEffect}?)"/>.
/// Interior layouts are left out.
/// </summary>
public static class BuildingEffects
{
    /// <summary>
    /// The effects of every placed building of the world that is neither destroyed nor switched off.
    /// </summary>
    public static List<PlacedEffect> ForWorld(GameDatabase db, WorldLevelData levels, Func<double, double, float> terrainHeight, BuildingLayouts? layouts = null)
    {
        var result = new List<PlacedEffect>();
        foreach (var b in levels.Buildings())
            if (BuildingPlacements.Resolve(b, db, levels, terrainHeight) is { } resolved)
                result.AddRange(ForBuilding(db, levels, resolved, layouts));
        return result;
    }

    /// <summary>
    /// The effects of one resolved placement: none when it is destroyed or its state's <c>power on</c> is false (lights and effects switch together).
    /// </summary>
    public static List<PlacedEffect> ForBuilding(GameDatabase db, WorldLevelData levels, ResolvedBuilding building, BuildingLayouts? layouts = null)
    {
        var result = new List<PlacedEffect>();
        if (building.Destroyed) return result;
        var b = building.Placement;
        var state = b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
        if (state is not null && !state.GetBool("power on", true)) return result;
        WorldObjectLayout.Building(db, building.Record, b.InstanceId, building.Position, b.Rotation, new BuildingState { Identity = b.Key }, null, result);
        var name = b.LayoutName(state, exterior: true);
        if (layouts is null || name.Length == 0 || layouts.Find(building.Record, name, exterior: true) is not { } layout) return result;
        foreach (var item in layouts.Items(layout, b))
        {
            var (at, rotation) = BuildingLayouts.Place(building.Position, b.Rotation, item);
            WorldObjectLayout.Building(db, item.Building, $"{b.InstanceId}/{item.InstanceId}", at, rotation,
                new BuildingState { Identity = $"{b.Key}/{item.InstanceId}" }, null, result);
        }
        return result;
    }
}