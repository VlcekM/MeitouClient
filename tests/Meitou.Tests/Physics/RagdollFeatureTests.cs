using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Navigation;

namespace Meitou.Tests.Physics;

/// <summary>What ragdolls land on (<see cref="RagdollGeometry"/>): map features' collision joins the buildings' per zone (docs/game/pathfinding.md "Map features").</summary>
[Slow]
public class RagdollFeatureTests
{
    [Fact]
    public void Rebirths_ragdoll_triangles_include_the_statue_foundations_top()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        using var gatherer = new ZoneGeometryGatherer(install!, InstallData.FullLoadOrder!, InstallData.LevelsWithInteriors!, new CollisionCache(install!)) { IncludeFoliage = false };
        var triangles = new List<Vector3>();
        gatherer.RagdollTriangles(new ZoneCoordinate(21, 19), triangles);
        // Huge_Stone_Emperor_Statue's foundation top at -47503,-56300 is at 755.8 (terrain 724): an upward face of the zone's ragdoll triangles.
        bool top = false;
        for (int t = 0; t + 2 < triangles.Count && !top; t += 3)
        {
            Vector3 a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if (!NavGeometry.Barycentric(-47503, -56300, a, b, c, out float u, out float v, out float w) || u < 0 || v < 0 || w < 0) continue;
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            top = MathF.Abs(u * a.Y + v * b.Y + w * c.Y - 755.8f) < 2 && MathF.Abs(n.Y) > 0.9f;
        }
        Assert.True(top, $"no face at the statue foundation's top ~755.8 among the {triangles.Count / 3} ragdoll triangles of 21.19");
    }
}
