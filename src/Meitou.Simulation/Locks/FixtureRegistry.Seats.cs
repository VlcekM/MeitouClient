using System.Globalization;
using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Locks;

/// <summary>A town chair or throne, in world space, for picking and the player's Use order.</summary>
public sealed record SeatSpot(string Id, string Building, string Record, Vector3 Position, int Owner, float Yaw)
{
    /// <summary>The furniture's physical facing; <see cref="Yaw"/> is the sitter's facing.</summary>
    public float PlacementYaw { get; init; } = Yaw;
    /// <summary>The sitter's facing as the game has it (<see cref="HomeSpot.FaithfulYaw"/>).</summary>
    public float FaithfulYaw { get; init; } = Yaw;
}

public sealed partial class FixtureRegistry
{
    readonly Dictionary<string, SeatSpot> seatSpots = new(StringComparer.Ordinal);
    IReadOnlyList<SeatSpot>? publishedSeats;
    public IReadOnlyList<SeatSpot> SeatSpots => publishedSeats ??= [.. seatSpots.Values];
    public SeatSpot? FindSeat(string id) => seatSpots.GetValueOrDefault(id);

    /// <summary>The original furniture a player-built seat in this town house can face.</summary>
    public IReadOnlyList<Vector3> TablesOfBuilding(string building) => properties.TryGetValue(building, out var p) ? p.Building.Tables : [];

    void RegisterSeats(TownBuilding building, int owner)
    {
        int index = 0;
        foreach (var spot in building.Spots.Concat(building.Furniture))
        {
            if (spot.Kind is not (HomeSpotKind.Chair or HomeSpotKind.ThroneSeat) || spot.Record is null) continue;
            string id = $"{building.Key}@seat{(index++).ToString(CultureInfo.InvariantCulture)}";
            seatSpots[id] = new(id, building.Key, spot.Record, spot.Position, owner, spot.Yaw) { PlacementYaw = spot.PlacementYaw, FaithfulYaw = spot.FaithfulYaw };
        }
        publishedSeats = null;
    }
    void RemoveSeats(string building)
    {
        foreach (var id in seatSpots.Values.Where(s => s.Building == building).Select(s => s.Id).ToArray()) seatSpots.Remove(id);
        publishedSeats = null;
    }
}
