using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The Meitou water's CPU side (docs/viewer.md "Water"): the wave set on the game clock, the polar grid and the foam bake.</summary>
public class MeitouWaterTests
{
    [Fact]
    public void Waves_stand_still_while_the_game_clock_does()
    {
        var w = new WaveSet();
        w.Step(1.0, Vector2.UnitX, 30);
        w.Step(1.01, Vector2.UnitX, 30);
        var (phase, shore) = (w.Phase, w.Shore.X);
        w.Step(1.01, Vector2.UnitX, 30);   // paused: the same hour again
        Assert.Equal(phase, w.Phase);
        Assert.Equal(shore, w.Shore.X);
        w.Step(1.02, Vector2.UnitX, 30);
        Assert.NotEqual(phase, w.Phase);
    }

    [Fact]
    public void Phases_advance_by_the_deep_water_dispersion()
    {
        var w = new WaveSet();
        w.Step(0, Vector2.UnitX, 0);
        double hours = 0.5 / WaveSet.SecondsPerGameHour;   // half a game second at speed 1
        w.Step(hours, Vector2.UnitX, 0);
        for (int i = 0; i < 4; i++)
            Assert.Equal(MathF.Sqrt(WaveSet.Gravity * w.K[i]) * 0.5f, w.Phase[i], 3);
        Assert.Equal(0.5f / WaveSet.BreakerPeriod, w.Shore.X, 4);
    }

    [Fact]
    public void Waves_never_fold_and_grow_with_the_wind()
    {
        foreach (float speed in new[] { 0f, 30f, 60f, 100f, 500f })
        {
            var w = new WaveSet();
            w.Step(0, new Vector2(0.3f, -1), speed);
            float fold = 0;
            for (int i = 0; i < 4; i++) fold += w.Steepness[i] * w.K[i] * w.Amplitude[i];
            Assert.True(fold <= 0.8f + 1e-4f, $"wind {speed}: Σ q k a = {fold}");
            Assert.Equal(1f, new Vector2(w.DirX[0], w.DirZ[0]).Length(), 4);
        }
        var calm = new WaveSet();
        calm.Step(0, Vector2.UnitX, 0);
        var windy = new WaveSet();
        windy.Step(0, Vector2.UnitX, 60);
        Assert.True(windy.Amplitude.X > calm.Amplitude.X * 2 && windy.Shore.W > calm.Shore.W);
    }

    [Fact]
    public void The_wind_turns_the_waves_gradually()
    {
        var w = new WaveSet();
        w.Step(0, Vector2.UnitX, 60);
        w.Step(1.0 / WaveSet.SecondsPerGameHour, Vector2.UnitY, 60);   // one second later the wind is from +Z
        Assert.True(w.DirX[0] > 0.8f, "one second does not turn the waves round");
        for (int s = 2; s < 120; s++) w.Step(s / WaveSet.SecondsPerGameHour, Vector2.UnitY, 60);
        Assert.True(w.DirZ[0] > 0.99f, "two minutes do");
    }

    [Fact]
    public void Polar_grid_covers_the_disc_with_valid_triangles()
    {
        foreach (int segments in new[] { 128, 256 })
        {
            var (positions, indices) = WaterRenderer.PolarGrid(segments);
            int vertices = positions.Length / 2;
            Assert.Equal(0, indices.Length % 3);
            Assert.All(indices, i => Assert.InRange(i, 0u, (uint)vertices - 1));
            float outer = 0, detail = 0;
            for (int i = 0; i < vertices; i++)
            {
                float r = new Vector2(positions[i * 2], positions[i * 2 + 1]).Length();
                if (r > 1e5f) outer = r; else detail = MathF.Max(detail, r);
            }
            Assert.Equal(WaterRenderer.GridDetailRadius, detail, 1);
            Assert.True(outer > 1e5f, "the outer ring is marked for the extent");
            // Counter-clockwise seen from above (+Y): with x right and z down the screen that is a negative cross product in (x, z).
            for (int t = 0; t < indices.Length; t += 3)
            {
                Vector2 P(uint i) => new(positions[i * 2], positions[i * 2 + 1]);
                Vector2 a = P(indices[t]), b = P(indices[t + 1]), c = P(indices[t + 2]);
                float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                Assert.True(cross < 0, $"triangle {t / 3} is wound the other way or degenerate");
            }
        }
    }

    [Fact]
    public void Foam_bake_is_deterministic_and_tiles()
    {
        var a = WaterFoam.Bake();
        Assert.Equal(a, WaterFoam.Bake());
        int n = WaterFoam.Size;
        // Across the wrap the pattern continues as smoothly as anywhere inside: compare the edge step with the mean inner step.
        double inner = 0, edge = 0;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x + 1 < n; x++) inner += Math.Abs(a[(y * n + x) * 4 + 1] - a[(y * n + x + 1) * 4 + 1]);
            edge += Math.Abs(a[(y * n + n - 1) * 4 + 1] - a[(y * n) * 4 + 1]);
        }
        inner /= n * (n - 1);
        edge /= n;
        Assert.True(edge < inner * 3 + 2, $"patches: edge step {edge:0.0} vs inner {inner:0.0}");
        Assert.Contains(a.Where((_, i) => i % 4 == 0), v => v > 200);   // the lace has rims
    }
}
