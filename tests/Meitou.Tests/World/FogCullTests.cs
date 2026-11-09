using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Tests.World;

/// <summary>
/// The fog cull (docs/formats/fogfeatures.md "In Meitou"): a box inside the block the eye is in, far enough, is hidden; anything else is not.
/// </summary>
public class FogCullTests
{
    /// <summary>
    /// A wide low block: x and z within 50000, floor at -2000, ceiling at 3000; density distance 4500, edge 800.
    /// </summary>
    internal static FogFeature Block() => new("wide", FogFeatureType.Block, new Vector3(0.6f, 0.55f, 0.5f), 1, 4500, 800, Vector3.Zero, Vector3.Zero,
            0,
    [
        new Vector4(0, 1, 0, 3000), new Vector4(1, 0, 0, 50000), new Vector4(-1, 0, 0, 50000), new Vector4(0, 0, 1, 50000), new Vector4(0, 0, -1,
                50000),
        new Vector4(0.0f, 0.1f, 0.995f, 60000), new Vector4(0, -1, 0, 2000),
    ]);

    static Vector3 B(float x, float y, float z) => new(x, y, z);

    /// <summary>
    /// The shader's <c>fogVolumeBlock</c> alpha for an eye inside (near = 0), along unit ray <paramref name="d"/> to a fragment
    /// <paramref name="dist"/> away.
    /// </summary>
    internal static float Alpha(FogFeature f, Vector3 eye, Vector3 d, float dist)
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
                        // Fragments are at or behind the box point (the sky at the far clip, water and terrain behind it).
                        if (length < dist) continue;
                        float alpha = Alpha(f, eye, d, MathF.Min(length, far));
                        Assert.True(alpha >= 0.9998f - 1e-4f, $"alpha {alpha} eye {eye} box {min} {max} p {p} length {length} far {far}");
                    }
                }
            }
        }
        Assert.True(hidden > 50, $"only {hidden} boxes hidden: the test does not exercise the bound");
    }

    static float Lerp(Random r, float a, float b) => a + (b - a) * (float)r.NextDouble();
}
