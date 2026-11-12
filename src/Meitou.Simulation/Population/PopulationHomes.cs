using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// Town residents in their buildings (docs/game/factions-squads-towns.md "Town residents"): which building each resident entry's squads get, and
/// where in it they are made. Pure given the seed (runs on the build thread).
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>
    /// NodeType values of the spots a resident squad is made at.
    /// </summary>
    const int NodeShopkeeper = 0, NodeGeneral = 1, NodeGuardPost = 2;

    /// <summary>
    /// BuildingDesignation BD_BAR.
    /// </summary>
    const int DesignationBar = 3;

    /// <summary>
    /// Houses the town's resident entries in its buildings (<see cref="ResidentHomes.Assign"/>) and builds their squads into
    /// <paramref name="squads"/>, each building with its template into <paramref name="homes"/>; returns the town's bars (for the bar squads). Each
    /// housed template's squads: its <c>housemates</c> (v0, or v0..v1 when v1 &gt; 0), then itself. The buildings in <paramref name="playerOwned"/>
    /// (the player's) house nobody.
    /// </summary>
    List<TownBuilding> HouseResidents(TownSite site, FactionData? faction, int factionIndex, ulong seed, IWalkability walk,
        List<BuiltSquad> squads, List<(TownBuilding Building, SquadTemplate Template)> homes, IReadOnlySet<string>? playerOwned)
    {
        string? player = _data.PlayerFaction >= 0 ? _data.Factions[_data.PlayerFaction].Id : null;
        var housed = ResidentHomes.Assign(_data.Db, site.Town, faction, site.Buildings, player, playerOwned);
        for (int h = 0; h < housed.Count; h++)
        {
            var (b, t) = housed[h];
            int n = 0;
            foreach (var mate in t.Housemates)
            {
                if (_data.Db.Find(mate.Id) is not { } mr || !SquadFactory.IsTemplate(mr)) continue;

                var mt = SquadTemplate.From(mr);
                int count = mate.V1 > 0 && mate.V1 > mate.V0
                    ? mate.V0 + Rng.Int(
                        Rng.Hash(seed, (ulong)site.Index << 20 ^ (ulong)h, RngPurpose.Spawn, SeedSalts.Housemates + (ulong)n), mate.V1 - mate.V0 + 1)
                    : mate.V0;
                for (int k = 0; k < count; k++) Make(mt, n++);
            }

            Make(t, n);

            void Make(SquadTemplate template, int index)
            {
                ulong key = Rng.Mix(SeedSalts.ResidentHome ^ (ulong)site.Index << 40 ^ (ulong)h << 12 ^ (uint)index);
                var (at, nearY) = SpotIn(b, seed, key);
                if (BuildOne(template, factionIndex, faction, key, seed, at) is { } one)
                    squads.Add(one with { NearY = nearY, Home = b });
            }
        }

        homes.AddRange(housed);
        return [.. housed.Where(x => x.Template.BuildingDesignation == DesignationBar || x.Building.IsBar).Select(x => x.Building)];
    }

    /// <summary>
    /// A floor of <paramref name="home"/> for a resident whose spot at height <paramref name="y"/> has none near it (engine choice; null: none, the
    /// spot's height stands): at a node, the nearest storey below it on the mesh (an upper room the mesh does not reach, Observed: Stack's Longhouse
    /// <c>4-Newwworld-INGAME</c>); at the origin of a building without nodes, its lowest floor more than <see cref="RaisedFloor"/> above the terrain
    /// (raised houses, whose floor is 25..55 above their origin, Observed: the Swamp Houses and Cannibal Shacks). Only buildings with an interior (an
    /// <c>interior mask</c>, not a gateway), so gate guards stay on the ground under the walkway.
    /// </summary>
    Vector3? HomeFloor(IWalkability walk, TownBuilding home, float x, float z, float y)
    {
        if (_data.Db.Find(home.BuildingId) is not { } record || record.GetBool("is gateway") || !record.GetReferences("interior mask").Any())
            return null;

        float terrain = walk.TerrainHeight(x, z);
        if (HasNode(home))
        {
            for (float h = y - FloorScanStep; h > terrain + RaisedFloor; h -= FloorScanStep)
            {
                if (FloorAround(walk, x, z, h) is { } below) return below;
            }

            return null;
        }

        for (float h = terrain + RaisedFloor; h <= MathF.Max(y, terrain) + FloorScanReach; h += FloorScanStep)
        {
            if (FloorAround(walk, x, z, h) is { } floor) return floor;
        }

        return null;
    }

    /// <summary>
    /// The nearest point within <see cref="FloorScanRadius"/> of (x, z) on rings 4 apart (8 directions each) whose floor is within half a scan step of
    /// <paramref name="h"/> and more than <see cref="RaisedFloor"/> above the terrain there (not the ground beside the house).
    /// </summary>
    static Vector3? FloorAround(IWalkability walk, float x, float z, float h)
    {
        for (float r = 0; r <= FloorScanRadius; r += FloorScanStep)
        {
            for (int k = 0; k < (r == 0 ? 1 : 8); k++)
            {
                float px = x + r * MathF.Cos(k * MathF.PI / 4), pz = z + r * MathF.Sin(k * MathF.PI / 4);
                if (walk.TryStand(px, pz, h, out float g) && MathF.Abs(g - h) <= FloorScanStep / 2 && g > walk.TerrainHeight(px, pz) + RaisedFloor)
                    return new Vector3(px, g, pz);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="b"/> has a node its residents are made at (<see cref="SpotIn"/>; without one, its origin).
    /// </summary>
    static bool HasNode(TownBuilding b) => b.Spots.Any(s => s.NodeType is NodeGeneral or NodeShopkeeper or NodeGuardPost);

    /// <summary>
    /// <see cref="HomeFloor"/>'s numbers (engine choices): a floor more than 12 above the terrain is a raised one (the walkers' step-down
    /// limit), scanned in 4-unit steps up to 60 above the origin (or the terrain, when it is buried), within 40 of the spot (a shack's middle is
    /// often furniture).
    /// </summary>
    const float RaisedFloor = 12, FloorScanStep = 4, FloorScanReach = 60, FloorScanRadius = 40;

    /// <summary>
    /// The platoon point of a resident squad of <paramref name="b"/> (<c>FUN_14057f820</c>:86-103, <c>FUN_140583a10</c>:413-420, Verified): a random
    /// free node of type 0 (shopkeeper), else 2 (guard post), else 1 (general), else its origin; the height to find the floor from (its Y plus
    /// <see cref="SpotLift"/>). The members are placed apart in the original, each at a random general node, else a shopkeeper node
    /// (<see cref="MemberSpot"/>).
    /// </summary>
    static (Vector2 At, float? NearY) SpotIn(TownBuilding b, ulong seed, ulong key)
    {
        foreach (int type in (ReadOnlySpan<int>)[NodeShopkeeper, NodeGuardPost, NodeGeneral])
        {
            int n = 0;
            foreach (var s in b.Spots)
                if (s.NodeType == type) n++;
            if (n == 0) continue;

            int pick = Rng.Int(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.HomeSpot), n);
            foreach (var s in b.Spots)
            {
                if (s.NodeType == type && pick-- == 0) return (new Vector2(s.Position.X, s.Position.Z), s.Position.Y + SpotLift);
            }
        }

        return (new Vector2(b.Position.X, b.Position.Z), b.Position.Y + SpotLift);
    }

    /// <summary>
    /// The spot of one member of a resident squad of <paramref name="b"/> (<c>FUN_14057f820</c>:86-103, Verified): a random general (1) node, else a
    /// shopkeeper (0) node, at the node plus <see cref="SpotLift"/>; null: the building has neither and the member stands at the platoon point. One
    /// draw per member, from <paramref name="salt"/>.
    /// </summary>
    internal static Vector3? MemberSpot(TownBuilding b, ulong seed, ulong salt)
    {
        foreach (int type in (ReadOnlySpan<int>)[NodeGeneral, NodeShopkeeper])
        {
            int n = 0;
            foreach (var s in b.Spots)
                if (s.NodeType == type) n++;
            if (n == 0) continue;

            int pick = Rng.Int(Rng.Hash(seed, salt, RngPurpose.Spawn, SeedSalts.MemberSpot), n);
            foreach (var s in b.Spots)
            {
                if (s.NodeType == type && pick-- == 0) return s.Position + new Vector3(0, SpotLift, 0);
            }
        }

        return null;
    }
}
