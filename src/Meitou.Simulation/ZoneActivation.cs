using Meitou.Simulation.Items;
using Meitou.Simulation.Bodies;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>Which zones a focus keeps active (docs/game/game-loop.md "Zones", Verified for the ring, Observed for the box).</summary>
public static class ZoneActivation
{
    /// <summary>Half the side of the box round the focus whose overlapping zones are activated.</summary>
    public const float Box = 340;

    /// <summary>Every zone overlapping the box of +-<see cref="Box"/> round <paramref name="p"/>, plus <paramref name="ring"/> more zones on every side (0 or 1, the <c>Fast zone hopping</c> setting).</summary>
    public static HashSet<ZoneCoordinate> ZonesAround(Vector3 p, int ring)
    {
        var set = new HashSet<ZoneCoordinate>();
        AddZonesAround(p, ring, set);
        return set;
    }

    /// <summary>Adds the zones of <see cref="ZonesAround"/> to <paramref name="set"/>, in the same order, without making a set of their own.</summary>
    public static void AddZonesAround(Vector3 p, int ring, HashSet<ZoneCoordinate> set)
    {
        var lo = WorldLayout.ZoneOf(p.X - Box, p.Z - Box);
        var hi = WorldLayout.ZoneOf(p.X + Box, p.Z + Box);
        for (int x = lo.X - ring; x <= hi.X + ring; x++)
            for (int y = lo.Y - ring; y <= hi.Y + ring; y++)
                set.Add(new ZoneCoordinate(x, y));
    }
}
