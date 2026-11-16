namespace Meitou.Simulation.Navigation;

/// <summary>
/// The codes of what stands inside a building (docs/game/pathfinding.md "Gate codes"): a character inside a building takes the building's own code
/// (Character slot 35, <c>FUN_140792200</c>), whatever the face under it holds, and a building's own code is <c>getGateCode</c> at its first door's
/// outer marker, or at its position when it has no door (<c>FUN_140555fd0</c>). Meitou reads an object's code from its position, so every interior
/// polygon of a building takes that building's code, in every zone its interior reaches.
/// </summary>
internal static class GateInteriorCodes
{
    /// <summary>
    /// The codes of the interior polygons that belong to a building, by polygon key: their building's code at its <see cref="BuildingCodePoint"/>,
    /// -1 when no loaded zone holds that building's point.
    /// </summary>
    public static Dictionary<long, int> Of(NavWorld world, Dictionary<long, int> codes)
    {
        var own = BuildingCodes(world, codes);
        var inside = new Dictionary<long, int>();
        for (int s = 0; s < world.ZoneCount; s++)
        {
            var m = world.Mesh(s);
            if (m.BuildingIds is not { } ids || m.BuildingOf is not { } of) continue;

            for (int p = Math.Min(m.InteriorStart, m.PolygonCount); p < m.PolygonCount; p++)
            {
                if (!m.Kept[p] || of[p] < 0) continue;

                inside[new NavRef(s, p).Key] = own.GetValueOrDefault(ids[of[p]], -1);
            }
        }

        return inside;
    }

    /// <summary>
    /// Each loaded building's own code: <c>getGateCode</c> at its code point.
    /// </summary>
    static Dictionary<string, int> BuildingCodes(NavWorld world, Dictionary<long, int> codes)
    {
        var own = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int s = 0; s < world.ZoneCount; s++)
        {
            foreach (var point in world.Mesh(s).CodePoints ?? [])
                own[point.Building] = GateFaces.CodeAt(world, codes, point.At);
        }

        return own;
    }
}
