namespace Meitou.Data.World;

/// <summary>
/// The run-time name of a placed building: its zone and its zone-file instance id, <c>"x.y:id"</c>. Instance ids are only unique within one zone file
/// (the original names a building by a handle whose container is the zone, docs/formats/save.md), so maps, door states, fixtures and the renderer's
/// placements key by this; the raw id stays where a file is read or written (the save's building lists) and where it seeds a choice (material
/// collections).
/// </summary>
public static class BuildingKey
{
    /// <summary>
    /// The key of instance <paramref name="instanceId"/> of <paramref name="zone"/>'s file.
    /// </summary>
    public static string Of(ZoneCoordinate zone, string instanceId) => $"{zone.X}.{zone.Y}:{instanceId}";
}