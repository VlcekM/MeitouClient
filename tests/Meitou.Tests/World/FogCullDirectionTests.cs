using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Tests.World;

/// <summary>The fog cull's bound by ray direction (<see cref="FogVolumes.HideDistanceByDirection"/>, docs/formats/fogfeatures.md "Fog cull by direction").</summary>
public class FogCullDirectionTests
{
    /// <summary>The seven planes of "#140805Swamp[SOUTH]#140805" (fogfeatures.dat) as the shader gets them: tilted roof facets round Shark.</summary>
    static readonly Vector4[] SwampSouth =
    [
        new(0.20565185f, 0.97862524f, 3.7252903E-08f, -5256.4976f), new(0.09747819f, 0.99523765f, 3.7252903E-08f, -1703.7759f),
        new(-4.4703484E-08f, 0.9629717f, 0.26960266f, 17746.258f), new(-0.1075602f, 0.99361765f, 0.033982262f, 11004.5f),
        new(3.7252903E-08f, 0.9810374f, -0.19381878f, -4637.3975f), new(0.0961941f, 0.99070346f, 0.09619436f, 6299.591f),
        new(-0.1990686f, 0.9595537f, -0.19906867f, 4663.836f),
    ];
    const float SwampEdge = 0.001234568f, SwampDensity = 0.00022222222f;

    /// <summary>The wide test block of <see cref="FogCullTests"/>: x and z within 50000, floor -2000, ceiling 3000, a tilted plane.</summary>
    static readonly Vector4[] Wide =
    [
        new(0, 1, 0, 3000), new(1, 0, 0, 50000), new(-1, 0, 0, 50000), new(0, 0, 1, 50000), new(0, 0, -1, 50000), new(0.0f, 0.1f, 0.995f, 60000), new(0, -1, 0, 2000),
    ];
    const float WideEdge = 1f / 800, WideDensity = 1f / 4500;

    static readonly Vector3 BenchEye = new(-48540.98f, 1120.9402f, 49003.13f), LowEye = new(-46841.47f, 148.84274f, 48482.668f);

    [Fact]
    public void At_shark_the_direction_bound_finds_the_fog_distance_where_the_plain_bound_finds_none()
    {
        var benchInside = SwampSouth.Select(p => p.W - (p.X * BenchEye.X + p.Y * BenchEye.Y + p.Z * BenchEye.Z)).ToArray();
        Assert.Null(FogVolumes.HideDistance(SwampEdge, SwampDensity, benchInside, 50000));
        foreach (var eye in new[] { BenchEye, LowEye })
        {
            float r = FogVolumes.HideDistanceByDirection(SwampSouth, eye, FogVolumes.DirectionSolveMargin, SwampEdge, SwampDensity, 50000, FogVolumes.DirectionSolveBudget)!.Value;
            // Sampling the shader's formula finds paths of 4453 to 4454 under 0.9998 (the swamp's density distance 4500 times 0.99).
            Assert.InRange(r, 4455f, 4600f);
        }
    }

    /// <summary>The shader's <c>fogVolumeBlock</c> alpha for an eye inside (near = 0), along unit ray <paramref name="d"/> to a fragment <paramref name="dist"/> away.</summary>
    static float Alpha(Vector4[] planes, float edgeBlur, float density, Vector3 eye, Vector3 d, float dist)
    {
        float far = dist;
        foreach (var p in planes)
        {
            float dn = p.X * d.X + p.Y * d.Y + p.Z * d.Z, s = p.W - (p.X * eye.X + p.Y * eye.Y + p.Z * eye.Z);
            if (MathF.Abs(dn) < 1e-6f) continue;
            if (dn > 0) far = MathF.Min(far, s / dn);
        }
        if (far <= 0) return 0;
        float blur = edgeBlur * Math.Clamp(1f / (far * 0.00006f), 0f, 1f);
        var middle = eye + d * (far * 0.5f);
        float edge = 1;
        foreach (var p in planes) edge *= Math.Clamp((p.W - (p.X * middle.X + p.Y * middle.Y + p.Z * middle.Z)) * blur, 0f, 1f);
        edge *= 1 + MathF.Abs(d.Y) * 0.9f;
        float a = Math.Clamp(far * density * edge, 0f, 1f);
        return a < 0.5f ? 2 * a * a : 1 - 2 * (a - 1) * (a - 1);
    }

    [Fact]
    public void Every_path_past_the_distance_from_any_eye_within_the_margin_is_covered_by_the_shaders_formula()
    {
        var rng = new Random(11);
        int solved = 0, rays = 0;
        foreach (var (planes, edge, density, lowY, highY, spread) in new[] { (SwampSouth, SwampEdge, SwampDensity, -300, 2400, 8000), (Wide, WideEdge, WideDensity, -1500, 2900, 40000) })
            for (int trial = 0; trial < 60; trial++)
            {
                var centre = planes == SwampSouth
                    ? new Vector3(-47500 + rng.Next(-spread, spread), rng.Next(lowY, highY), 49000 + rng.Next(-spread, spread))
                    : new Vector3(rng.Next(-spread, spread), rng.Next(lowY, highY), rng.Next(-spread, spread));
                float far = rng.Next(8000, 60000);
                if (FogVolumes.HideDistanceByDirection(planes, centre, FogVolumes.DirectionSolveMargin, edge, density, far, FogVolumes.DirectionSolveBudget) is not { } r) continue;
                solved++;
                for (int k = 0; k < 400; k++)
                {
                    var offset = new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1);
                    if (offset.Length() > 1) continue;
                    var eye = centre + offset * FogVolumes.DirectionSolveMargin;
                    if (planes.Any(p => p.W - (p.X * eye.X + p.Y * eye.Y + p.Z * eye.Z) <= 1)) continue;
                    var d = Vector3.Normalize(new Vector3(rng.NextSingle() * 2 - 1, (rng.NextSingle() * 2 - 1) * (k % 2 == 0 ? 0.05f : 1), rng.NextSingle() * 2 - 1));
                    // Fragments from R out to the far clip: the shader's own exit caps the path.
                    foreach (float t in new[] { 0f, 0.001f, 0.01f, 0.1f, 0.5f, 1f })
                    {
                        float dist = r + (far - r) * t;
                        if (dist < r) continue;
                        float exit = far;
                        foreach (var p in planes)
                        {
                            float dn = p.X * d.X + p.Y * d.Y + p.Z * d.Z;
                            if (dn > 1e-6f) exit = MathF.Min(exit, (p.W - (p.X * eye.X + p.Y * eye.Y + p.Z * eye.Z)) / dn);
                        }
                        if (exit < r) break;   // the ray leaves the block before R: nothing on it is hidden
                        rays++;
                        float alpha = Alpha(planes, edge, density, eye, d, MathF.Min(dist, far));
                        Assert.True(alpha >= 0.9998f - 1e-4f, $"alpha {alpha} eye {eye} d {d} dist {dist} R {r} far {far}");
                    }
                }
            }
        Assert.True(solved > 40 && rays > 20000, $"{solved} solved, {rays} rays: the test does not exercise the bound");
    }

    [Fact]
    public void No_distance_at_a_plane_or_without_room_and_the_budget_gives_up()
    {
        var eye = new Vector3(0, 2999.5f, 0);   // half a unit under the wide block's ceiling
        Assert.Null(FogVolumes.HideDistanceByDirection(Wide, eye, 0, WideEdge, WideDensity, 50000));
        Assert.Null(FogVolumes.HideDistanceByDirection(SwampSouth, BenchEye, 0, SwampEdge, SwampDensity, 50000, budget: 10));
        var r = FogVolumes.HideDistanceByDirection(Wide, new Vector3(0, 150, 0), 0, WideEdge, 1f / 60000, 50000, FogVolumes.DirectionSolveBudget);
        Assert.True(r is null || r >= 50000 * 0.98f, $"R {r}");   // thinner than the far clip can cover
    }

    [Fact]
    public void The_frames_cull_uses_the_smaller_of_the_two_bounds()
    {
        var block = new Meitou.Data.World.FogFeature("swamp", Meitou.Data.World.FogFeatureType.Block, new Vector3(0.59f, 0.56f, 0.5f), 1, 4500, 810, Vector3.Zero, Vector3.Zero, 0, [.. SwampSouth]);
        var on = new FogVolumes([block]) { SolveInline = true };
        on.Update(BenchEye, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 50000, 0.5f, on: true);
        var off = new FogVolumes([block]) { SolveInline = true, DirectionBound = false };
        off.Update(BenchEye, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 50000, 0.5f, on: true);
        Assert.Null(off.CullBlock);
        Assert.NotNull(on.CullBlock);
        Assert.InRange(on.CullRadius, 4455f, 4600f);
    }
}
