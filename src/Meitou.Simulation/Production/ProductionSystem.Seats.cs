using System.Numerics;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Production;

public sealed partial class ProductionSystem
{
    static bool SeatOccupied(World world, BuildSite site)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Rest && table.Cold(i)?.Ai?.SitClip is not null
                && Vector3.DistanceSquared(table.Next[i].Position, site.Position) < 1) return true;
        return false;
    }
    readonly Dictionary<string, (SeatSpot Source, BuildSite Site)> seatSites = new(StringComparer.Ordinal);
    BuildSite? Seat(World world, string id)
    {
        if (world.Fixtures.FindSeat(id) is not { } seat) return null;
        if (seatSites.TryGetValue(id, out var cached) && ReferenceEquals(cached.Source, seat)) return cached.Site;
        var site = new BuildSite
        {
            Id = id, Record = seat.Record, Name = db.Find(seat.Record)?.Name ?? seat.Record,
            Position = seat.Position, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, seat.Yaw),
            Owner = seat.Owner, Complete = true, Materials = [],
        };
        seatSites[id] = (seat, site);
        return site;
    }
}
