using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>The terrain's screen-space error LOD (<see cref="TerrainLod"/>) and the CDLOD tree it drives.</summary>
public class TerrainLodTests
{
    static readonly float Scale1080 = TerrainLod.ProjectionScale(1080, 50 * MathF.PI / 180);

    /// <summary>Rules to check: near only, with a far ramp, the reflection's half scale, a very fine and a very coarse one.</summary>
    public static TheoryData<float, float, float, float> Rules => new()
    {
        { Scale1080, 8, 8, 7500 },
        { Scale1080, 8, 24, 7500 },
        { Scale1080 / 2, 8, 24, 7500 },
        { Scale1080, 2, 2, 7500 },
        { Scale1080, 32, 200, 3000 },
    };

    [Fact]
    public void Threshold_is_the_near_error_up_to_the_ramp_and_the_far_error_from_twice_it()
    {
        var lod = new TerrainLod(Scale1080, 5, 35, 7500);
        Assert.Equal(5, lod.Threshold(0));
        Assert.Equal(5, lod.Threshold(7500));
        Assert.Equal(35, lod.Threshold(15000), 3);
        Assert.Equal(35, lod.Threshold(1e6f), 3);
        // The game's form: the metric is multiplied by 1 − t + t × near / far, so halfway the threshold is 5 / (1 − 0.5 + 0.5 / 7) = 8.75.
        Assert.Equal(8.75f, lod.Threshold(11250), 3);
        float last = 0;
        for (float d = 0; d < 20000; d += 250) { Assert.True(lod.Threshold(d) >= last); last = lod.Threshold(d); }
        // Far error not above the near one: no ramp.
        Assert.Equal(8, new TerrainLod(Scale1080, 8, 4, 7500).Threshold(1e6f));
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void Split_distance_is_where_the_projected_error_meets_the_threshold(float scale, float near, float far, float ramp)
    {
        var lod = new TerrainLod(scale, near, far, ramp);
        float previous = 0;
        foreach (double error in new[] { 1.0, 10, 36, 72, 144, 576, 2304, 9216 })
        {
            float d = lod.SplitDistance(error);
            Assert.True(d > previous, $"split distance {d} for error {error} is not beyond {previous}");
            previous = d;
            Assert.Equal(1, scale * error / d / lod.Threshold(d), 3);
        }
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void Ranges_grow_by_at_least_the_gap_and_the_root_has_none(float scale, float near, float far, float ramp)
    {
        var q = new TerrainQuadtree(18, 64, new TerrainLod(scale, near, far, ramp));
        Assert.Equal(float.MaxValue, q.Ranges[^1]);
        Assert.True(q.Ranges[0] >= TerrainLod.MinimumGap * q.NodeSize(0) - 1e-3);
        for (int l = 1; l + 1 < q.LevelCount; l++)
        {
            Assert.True(q.Ranges[l] - q.Ranges[l - 1] >= TerrainLod.MinimumGap * q.NodeSize(l - 1) * (1 - 1e-6) - q.Ranges[l] * 1e-6, $"levels {l - 1} and {l}: {q.Ranges[l - 1]}, {q.Ranges[l]}");
            Assert.True(q.MorphStart[l] >= q.Ranges[l - 1] && q.MorphStart[l] < q.MorphEnd[l] && q.MorphEnd[l] == q.Ranges[l]);
        }
    }

    [Fact]
    public void Ranges_follow_the_rendered_resolution_and_the_error()
    {
        var full = new TerrainLod(Scale1080, 4, 4, 7500).Ranges(9, 1152, 64);
        var half = new TerrainLod(Scale1080 / 2, 4, 4, 7500).Ranges(9, 1152, 64);
        var coarse = new TerrainLod(Scale1080, 8, 8, 7500).Ranges(9, 1152, 64);
        // Where the gap does not bind, half the pixels (or twice the error) is half the range.
        Assert.Equal(full[0] / 2, half[0], 1);
        Assert.Equal(half, coarse);
        Assert.All(Enumerable.Range(0, 8), l => Assert.True(half[l] <= full[l]));
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void Drawn_level_never_gets_finer_with_distance(float scale, float near, float far, float ramp)
    {
        var q = new TerrainQuadtree(18, 64, new TerrainLod(scale, near, far, ramp));
        var eye = new Vector3(1000, 400, -2000);
        var nodes = new List<TerrainNode>();
        q.Select(eye, Bounds(Flat), null, nodes);
        foreach (var direction in new[] { new Vector2(1, 0), new Vector2(0.6f, -0.8f), new Vector2(-0.3f, 0.95f) })
        {
            int last = -1;
            for (float d = 0; d < 140000; d += 97)
            {
                var p = new Vector2(eye.X, eye.Z) + direction * d;
                if (Math.Abs(p.X) >= WorldLayout.HalfWorldSize || Math.Abs(p.Y) >= WorldLayout.HalfWorldSize) break;
                int level = Squares(nodes).Where(s => p.X >= s.X && p.X < s.X + s.S && p.Y >= s.Z && p.Y < s.Z + s.S).Single().Level;
                Assert.True(level >= last, $"level {level} at {d} after {last}");
                last = level;
            }
        }
    }

    static float Flat(double x, double z) => 0;
    // Hills about 1000 high over 6000 to 9000 units, slopes up to about 0.9: steeper than most of the map over a whole node.
    static float Hills(double x, double z) => (float)(1200 + 500 * Math.Sin(x / 1100.0) * Math.Cos(z / 1400.0) + 300 * Math.Sin((x + z) / 700.0));

    const int Samples = 1025;   // 288 units per sample
    static readonly double Cell = (double)WorldLayout.WorldSize / (Samples - 1);

    static ushort Raw(Func<double, double, float> height, int i, int j) =>
        (ushort)Math.Round(height(i * Cell - WorldLayout.HalfWorldSize, j * Cell - WorldLayout.HalfWorldSize) / WorldLayout.MaxHeight * ushort.MaxValue);

    static TerrainHeightBounds Bounds(Func<double, double, float> height)
    {
        var raw = new ushort[Samples * Samples];
        for (int j = 0; j < Samples; j++)
            for (int i = 0; i < Samples; i++)
                raw[j * Samples + i] = Raw(height, i, j);
        return new TerrainHeightBounds(raw, Samples);
    }

    /// <summary>The height the shaders would read: the samples' bilinear interpolation (inside the bounds).</summary>
    static float Height(Func<double, double, float> height, double x, double z)
    {
        double fx = Math.Clamp((x + WorldLayout.HalfWorldSize) / Cell, 0, Samples - 1.0001), fz = Math.Clamp((z + WorldLayout.HalfWorldSize) / Cell, 0, Samples - 1.0001);
        int i = (int)fx, j = (int)fz;
        double tx = fx - i, tz = fz - j;
        double v = Raw(height, i, j) * (1 - tx) * (1 - tz) + Raw(height, i + 1, j) * tx * (1 - tz) + Raw(height, i, j + 1) * (1 - tx) * tz + Raw(height, i + 1, j + 1) * tx * tz;
        return (float)(v * WorldLayout.MaxHeight / ushort.MaxValue);
    }

    static IEnumerable<(int Level, double X, double Z, double S)> Squares(List<TerrainNode> nodes) =>
        nodes.Select(n => n.Quadrant < 0 ? (n.Level, n.X0, n.Z0, n.Size) : (n.Level, n.X0 + (n.Quadrant & 1) * n.Size / 2, n.Z0 + (n.Quadrant >> 1) * n.Size / 2, n.Size / 2));

    public static TheoryData<float, float, float, float, bool, float, float, float> Views()
    {
        var data = new TheoryData<float, float, float, float, bool, float, float, float>();
        var eyes = new[] { new Vector3(0, 300, 0), new Vector3(12345, 2600, -70000), new Vector3(-50979, 3100, 2932), new Vector3(146000, 1300, 146000), new Vector3(3000, 30000, 4000) };
        foreach (var (scale, near, far, ramp) in new[] { (Scale1080, 8f, 8f, 7500f), (Scale1080, 8f, 24f, 7500f), (Scale1080 / 2, 8f, 24f, 7500f), (Scale1080, 32f, 200f, 3000f) })
            foreach (bool hills in new[] { false, true })
                foreach (var e in eyes)
                    data.Add(scale, near, far, ramp, hills, e.X, e.Y, e.Z);
        return data;
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Touching_squares_differ_by_one_level_and_meet_without_cracks(float scale, float near, float far, float ramp, bool hills, float x, float y, float z)
    {
        Func<double, double, float> height = hills ? Hills : Flat;
        var q = new TerrainQuadtree(18, 64, new TerrainLod(scale, near, far, ramp));
        var eye = new Vector3(x, y, z);
        var nodes = new List<TerrainNode>();
        q.Select(eye, Bounds(height), null, nodes);
        var squares = Squares(nodes).ToList();
        Assert.Equal((double)WorldLayout.WorldSize * WorldLayout.WorldSize, squares.Sum(s => s.S * s.S), 1);
        float Distance(double px, double pz) => Vector3.Distance(eye, new Vector3((float)px, Height(height, px, pz), (float)pz));
        // Only squares near enough to the eye for their edges to matter are compared pairwise (the rest are coarse and few).
        var byLevel = squares.OrderBy(s => s.Level).ToList();
        foreach (var a in byLevel)
            foreach (var b in byLevel)
            {
                if (a.Level >= b.Level) continue;
                bool touchX = (Math.Abs(a.X + a.S - b.X) < 1e-6 || Math.Abs(b.X + b.S - a.X) < 1e-6) && a.Z < b.Z + b.S && b.Z < a.Z + a.S;
                bool touchZ = (Math.Abs(a.Z + a.S - b.Z) < 1e-6 || Math.Abs(b.Z + b.S - a.Z) < 1e-6) && a.X < b.X + b.S && b.X < a.X + a.S;
                if (!touchX && !touchZ) continue;
                Assert.True(b.Level - a.Level == 1, $"levels {a.Level} and {b.Level} touch");
                // On the shared edge the finer square's odd vertices have slid fully onto the coarser grid ...
                double spacing = q.Spacing(a.Level);
                for (int k = 1; k < 2 * q.GridCells * a.S / q.NodeSize(a.Level); k += 2)
                {
                    double px, pz;
                    if (touchX) { px = Math.Abs(a.X + a.S - b.X) < 1e-6 ? a.X + a.S : a.X; pz = a.Z + k * spacing; if (pz <= b.Z || pz >= b.Z + b.S) continue; }
                    else { pz = Math.Abs(a.Z + a.S - b.Z) < 1e-6 ? a.Z + a.S : a.Z; px = a.X + k * spacing; if (px <= b.X || px >= b.X + b.S) continue; }
                    Assert.Equal(1f, q.Morph(a.Level, Distance(px, pz)));
                }
                // ... while the coarser one's vertices there have not started sliding to the next level yet.
                double coarse = q.Spacing(b.Level);
                for (int k = 1; k < 2 * q.GridCells; k += 2)
                {
                    double px, pz;
                    if (touchX) { px = Math.Abs(a.X + a.S - b.X) < 1e-6 ? b.X : b.X + b.S; pz = b.Z + k * coarse; if (pz <= a.Z || pz >= a.Z + a.S) continue; }
                    else { pz = Math.Abs(a.Z + a.S - b.Z) < 1e-6 ? b.Z : b.Z + b.S; px = b.X + k * coarse; if (px <= a.X || px >= a.X + a.S) continue; }
                    Assert.Equal(0f, q.Morph(b.Level, Distance(px, pz)));
                }
            }
    }
}
