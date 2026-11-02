using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Production;

public sealed partial class ProductionSystem
{
    // Town-layout seats already carry their effective facing. A player-built chair must resolve its current house/outpost's furniture at use time.
    float SeatFacing(World world, BuildSite site, Machine m)
    {
        if (world.Fixtures.FindSeat(site.Id) is { } seat) return world.BenchFacingFix ? seat.Yaw : seat.FaithfulYaw;
        float yaw = TownBuildings.YawOf(site.Rotation);
        if (m.Data.Function != TownBuildings.ChairFunction || m.Data.FunctionFlags is not (1 or 2) || world.Buildings.Find(site.Id) is null) return yaw;
        return TownBuildings.SeatYaw(site.Position, yaw, m.Data.FunctionFlags, Targets(), faithful: !world.BenchFacingFix);

        IEnumerable<Vector3> Targets()
        {
            if (site.Parent is { } parent)
                foreach (var at in world.Fixtures.TablesOfBuilding(parent)) yield return at;
            foreach (var other in world.Buildings.All)
                if (other.Complete && !other.Destroyed && other.Parent == site.Parent && (site.Parent is not null || other.Town == site.Town)
                    && Data(other.Record)?.Function is TownBuildings.TableFunction or TownBuildings.CorpseDisposalFunction or TownBuildings.RefineryFunction)
                    yield return other.Position;
        }
    }

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
