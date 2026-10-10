using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.World;

/// <summary>The fog cull (docs/formats/fogfeatures.md "In Meitou"): a box inside the block the eye is in, far enough, is hidden; anything else is not.</summary>
public class FogCullTests
{
    /// <summary>A wide low block: x and z within 50000, floor at -2000, ceiling at 3000; density distance 4500, edge 800.</summary>
    static FogFeature Block() => new("wide", FogFeatureType.Block, new Vector3(0.6f, 0.55f, 0.5f), 1, 4500, 800, Vector3.Zero, Vector3.Zero, 0,
    [
        new Vector4(0, 1, 0, 3000), new Vector4(1, 0, 0, 50000), new Vector4(-1, 0, 0, 50000), new Vector4(0, 0, 1, 50000), new Vector4(0, 0, -1, 50000),
        new Vector4(0.0f, 0.1f, 0.995f, 60000), new Vector4(0, -1, 0, 2000),
    ]);

    static FogVolumes Frame(Vector3 eye, float far = 50000, bool cull = true)
    {
        var volumes = new FogVolumes([Block()]) { CullEnabled = cull, SolveInline = true };
        volumes.Update(eye, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, far, 0.5f, on: true);
        return volumes;
    }

    static Vector3 B(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void A_box_inside_the_block_far_from_the_eye_is_hidden_and_near_or_crossing_ones_are_not()
    {
        var v = Frame(B(0, 150, 0));
        Assert.NotNull(v.CullBlock);
        Assert.True(v.CullRadius > 4400 && v.CullRadius < 12000, $"radius {v.CullRadius}");
        var k = FogVolumes.CullKind.Terrain;
        Assert.True(v.Hidden(B(20000, 0, 0), B(20500, 400, 500), k));
        Assert.False(v.Hidden(B(500, 0, 0), B(1000, 400, 500), k));                 // near
        Assert.False(v.Hidden(B(20000, 2500, 0), B(20500, 3500, 500), k));         // crosses the ceiling
        Assert.False(v.Hidden(B(49800, 0, 0), B(50300, 400, 500), k));             // crosses a wall
        Assert.False(v.Hidden(B(60000, 0, 0), B(60500, 400, 500), k));             // outside the block
        Assert.False(v.Hidden(B(-10000, -3000, 0), B(-9000, 0, 500), k));          // reaches under the floor
        Assert.Equal(1, v.Culled[(int)k]);
    }

    [Fact]
    public void Off_when_disabled_or_the_eye_is_outside_or_a_bound_does_not_hold()
    {
        var far = (B(20000, 0, 0), B(20500, 400, 500));
        Assert.False(Frame(B(0, 150, 0), cull: false).Hidden(far.Item1, far.Item2, FogVolumes.CullKind.Objects));
        Assert.False(Frame(B(0, 3500, 0)).Hidden(far.Item1, far.Item2, FogVolumes.CullKind.Objects));          // above the ceiling
        Assert.False(Frame(B(0, 150, 0), far: 3000).Hidden(far.Item1, far.Item2, FogVolumes.CullKind.Objects)); // the sky would be nearer than the hide distance
        Assert.False(Frame(B(0, 2995, 0)).Hidden(far.Item1, far.Item2, FogVolumes.CullKind.Objects));         // at the ceiling: the edge term is not saturated
    }

    /// <summary>The shader's <c>fogVolumeBlock</c> alpha for an eye inside (near = 0), along unit ray <paramref name="d"/> to a fragment <paramref name="dist"/> away.</summary>
    static float Alpha(FogFeature f, Vector3 eye, Vector3 d, float dist)
    {
        float far = dist;
        foreach (var p in f.Planes)
        {
            float dn = Vector3.Dot(new Vector3(p.X, p.Y, p.Z), d), s = p.W - Vector3.Dot(new Vector3(p.X, p.Y, p.Z), eye);
            if (MathF.Abs(dn) < 1e-6f) continue;
            if (dn > 0) far = MathF.Min(far, s / dn);
        }
        if (far <= 0) return 0;
        float blur = f.EdgeBlur * Math.Clamp(1f / (far * 0.00006f), 0f, 1f);
        var middle = eye + d * (far * 0.5f);
        float edge = 1;
        foreach (var p in f.Planes) edge *= Math.Clamp((p.W - Vector3.Dot(new Vector3(p.X, p.Y, p.Z), middle)) * blur, 0f, 1f);
        edge *= 1 + MathF.Abs(d.Y) * 0.9f;
        float a = Math.Clamp(far * f.Density * edge, 0f, 1f);
        return a < 0.5f ? 2 * a * a : 1 - 2 * (a - 1) * (a - 1);
    }

    [Fact]
    public void Everything_the_cull_hides_is_covered_by_at_least_0_9998_alpha_by_the_shaders_own_formula()
    {
        var f = Block();
        var rng = new Random(7);
        int hidden = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            var eye = B(rng.Next(-30000, 30000), rng.Next(-500, 2900), rng.Next(-30000, 30000));
            float far = rng.Next(8000, 60000);
            var v = new FogVolumes([f]) { SolveInline = true };
            v.Update(eye, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, far, 0.5f, on: true);
            for (int k = 0; k < 30; k++)
            {
                var min = B(rng.Next(-60000, 60000), rng.Next(-1000, 3500), rng.Next(-60000, 60000));
                var max = min + B(rng.Next(10, 3000), rng.Next(10, 1500), rng.Next(10, 3000));
                if (!v.Hidden(min, max, FogVolumes.CullKind.Other)) continue;
                hidden++;
                for (int s = 0; s < 40; s++)
                {
                    var p = B(Lerp(rng, min.X, max.X), Lerp(rng, min.Y, max.Y), Lerp(rng, min.Z, max.Z));
                    var to = p - eye;
                    float dist = to.Length();
                    var d = to / dist;
                    foreach (float length in new[] { dist, far, dist * 1.7f, MathF.Min(far, dist + 12345) })
                    {
                        if (length < dist) continue;   // fragments are at or behind the box point (the sky at the far clip, water and terrain behind it)
                        float alpha = Alpha(f, eye, d, MathF.Min(length, far));
                        Assert.True(alpha >= 0.9998f - 1e-4f, $"alpha {alpha} eye {eye} box {min} {max} p {p} length {length} far {far}");
                    }
                }
            }
        }
        Assert.True(hidden > 50, $"only {hidden} boxes hidden: the test does not exercise the bound");
    }

    static float Lerp(Random r, float a, float b) => a + (b - a) * (float)r.NextDouble();

    [Fact]
    public void The_hide_distance_grows_with_the_density_distance_and_vanishes_without_room()
    {
        float[] inside = [4000, 50000, 50000, 50000, 50000, 50000, 4000];
        float near = FogVolumes.HideDistance(1f / 800, 1f / 2000, inside, 50000)!.Value;
        float farther = FogVolumes.HideDistance(1f / 800, 1f / 9000, inside, 50000)!.Value;
        Assert.True(near < farther);
        Assert.InRange(near, 0.98f * 2000, 1.2f * 2000);
        Assert.Null(FogVolumes.HideDistance(1f / 800, 1f / 50000, inside, 20000));   // thinner than the far clip can cover
        Assert.Null(FogVolumes.HideDistance(1f / 800, 1f / 2000, [1, 50000, 50000, 50000, 50000, 50000, 50000], 50000));   // the eye on a plane's edge
    }

    // ---- the weather fog's cull (SkyRenderer.FogCullDistance, FogVolumes.AtmosphereDistance) ----

    /// <summary>The uniforms the distance reads: sky mode, weather fog (weight, start, end), the far clip D, the Kenshi haze on, the altitude weight.</summary>
    static SkyRenderer.AtmosphereUniforms Atmosphere(float weight = 1, float fogStart = 0, float fogEnd = 3000, float far = 50000, bool game = true, bool kenshi = true, float altitude = 0) =>
        new(default, new Vector4(game ? 1 : 0, 50000, 1, 0), default, default, default, Vector3.One, new Vector4(fogStart, fogEnd, weight, far), Vector3.One, new Vector4(0, 0, 0, 5000),
            new Vector4(kenshi ? 1 : 0, 3000, 30000, weight > 0 && fogEnd > 1 ? 1f / fogEnd : 0), default, new Vector4(kenshi ? altitude : 1, 0.93f, altitude, 0), default, default);

    /// <summary>The shader's weather term: ease-in-out of dist / fog distance, times the weight (atmoKenshiHaze).</summary>
    static float FogCurve(float dist, float fogDistance, float weight)
    {
        float a = Math.Clamp(dist / fogDistance, 0, 1);
        return (a < 0.5f ? 2 * a * a : 1 - 2 * (a - 1) * (a - 1)) * weight;
    }

    [Fact]
    public void The_weather_fog_distance_is_where_the_fog_term_is_complete()
    {
        float d = SkyRenderer.FogCullDistanceOf(Atmosphere())!.Value;
        Assert.InRange(d, 2960f, 2975f);   // 0.99 of the fog distance
        Assert.True(FogCurve(d, 3000, 1) >= 0.9998f - 1e-5f);
        Assert.True(FogCurve(d * 0.995f, 3000, 1) < 0.9998f);   // and it is the least such distance
        foreach (float fog in new[] { 500f, 3000f, 25000f })
        {
            float far = SkyRenderer.FogCullDistanceOf(Atmosphere(fogEnd: fog, far: 50000))!.Value;
            Assert.True(FogCurve(far, fog, 1) >= 0.9998f - 1e-5f, $"fog {fog}");
        }
    }

    [Fact]
    public void There_is_no_weather_fog_distance_without_a_complete_fog_over_the_sky_too()
    {
        Assert.Null(SkyRenderer.FogCullDistanceOf(Atmosphere(weight: 0)));              // fog off
        Assert.Null(SkyRenderer.FogCullDistanceOf(Atmosphere(weight: 0.5f)));           // half a fog leaves half the surface
        Assert.Null(SkyRenderer.FogCullDistanceOf(Atmosphere(weight: 0.9990f)));
        Assert.Null(SkyRenderer.FogCullDistanceOf(Atmosphere(game: false)));            // the simple sky
        Assert.Null(SkyRenderer.FogCullDistanceOf(Atmosphere(fogEnd: 190000)));         // the sky pass reaches only part of the way at the far clip
        Assert.NotNull(SkyRenderer.FogCullDistanceOf(Atmosphere(fogEnd: 50000)));
    }

    [Fact]
    public void The_physical_haze_ends_at_its_linear_ramp_and_a_blend_takes_the_larger()
    {
        float physical = SkyRenderer.FogCullDistanceOf(Atmosphere(kenshi: false, fogStart: 1000, fogEnd: 3000))!.Value;
        Assert.InRange(physical, 2999f, 3000f);
        float kenshi = SkyRenderer.FogCullDistanceOf(Atmosphere(fogStart: 1000, fogEnd: 3000))!.Value;
        Assert.True(kenshi < physical);
        Assert.Equal(physical, SkyRenderer.FogCullDistanceOf(Atmosphere(fogStart: 1000, fogEnd: 3000, altitude: 0.4f))!.Value);
    }

    [Fact]
    public void The_weather_fog_hides_boxes_by_the_nearest_point_but_not_next_to_a_placed_volume_or_when_off()
    {
        float distance = SkyRenderer.FogCullDistanceOf(Atmosphere())!.Value;
        var open = new FogVolumes([]) { CullEnabled = true };
        open.Update(Vector3.Zero, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 50000, 0.5f, on: true, atmosphereCull: distance);
        Assert.Equal(distance, open.AtmosphereDistance);
        var k = FogVolumes.CullKind.Objects;
        Assert.True(open.Hidden(B(distance + 1, 0, 0), B(distance + 500, 100, 100), k));
        Assert.False(open.Hidden(B(distance - 1, 0, 0), B(distance + 500, 100, 100), k));   // its nearest point is inside the distance
        Assert.True(open.Hidden(B(distance * 0.75f, distance * 0.75f, 0), B(distance, distance, 50), k));   // diagonal: the Euclidean distance counts
        var buffer = new Vector4[FogVolumes.CullVectors];
        Assert.True(open.WriteCull(buffer));
        Assert.Equal(distance * distance, buffer[0].W);
        Assert.Equal(0f, buffer[1].W);   // no block
        var off = new FogVolumes([]) { CullEnabled = false };
        off.Update(Vector3.Zero, Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 50000, 0.5f, on: true, atmosphereCull: distance);
        Assert.Null(off.AtmosphereDistance);
        Assert.False(off.Hidden(B(distance + 1, 0, 0), B(distance + 500, 100, 100), k));
        // A placed volume in view: the fog volumes pass reads the depth, so nothing is left out.
        var withBlock = new FogVolumes([Block()]) { CullEnabled = true };
        withBlock.Update(B(0, 150, 0), Vector3.UnitX, 50 * MathF.PI / 180, 16f / 9, 50000, 0.5f, on: true, atmosphereCull: distance);
        Assert.Null(withBlock.AtmosphereDistance);
    }
}
