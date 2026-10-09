using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The Meitou water's CPU side (docs/render-water.md): the breakers on the game clock, the polar grid and the foam bake.</summary>
public class MeitouWaterTests
{
    [Fact]
    public void Waves_stand_still_while_the_game_clock_does()
    {
        var w = new WaveSet();
        w.Step(1.0, Vector2.UnitX, 30);
        w.Step(1.01, Vector2.UnitX, 30);
        var shore = w.Shore.X;
        w.Step(1.01, Vector2.UnitX, 30);   // paused: the same hour again
        Assert.Equal(shore, w.Shore.X);
        w.Step(1.02, Vector2.UnitX, 30);
        Assert.NotEqual(shore, w.Shore.X);
    }

    [Fact]
    public void Breakers_advance_with_the_clock_and_grow_with_the_wind_gradually()
    {
        var w = new WaveSet();
        w.Step(0, Vector2.UnitX, 0);
        w.Step(0.5 / WaveSet.SecondsPerGameHour, Vector2.UnitX, 0);   // half a game second at speed 1
        Assert.Equal(0.5f / WaveSet.BreakerPeriod, w.Shore.X, 4);
        float calm = w.Shore.W;
        w.Step(1.5 / WaveSet.SecondsPerGameHour, Vector2.UnitX, 90);   // a gale one second later
        Assert.True(w.Shore.W > calm && w.Shore.W < calm * 1.2f, "one second does not raise the breakers fully");
        for (int s = 2; s < 120; s++) w.Step(s / WaveSet.SecondsPerGameHour, Vector2.UnitX, 90);
        Assert.True(w.Shore.W > calm * 2, "two minutes do");
    }

    [Fact]
    public void Wave_count_runs_on_past_one_cycle_and_wraps_at_the_period_the_shader_noise_repeats_in()
    {
        var w = new WaveSet();
        w.Step(0, Vector2.UnitX, 0);
        float before = w.Shore.X;   // later frames add at most a second each
        for (int s = 1; s <= 40; s++) w.Step(s / WaveSet.SecondsPerGameHour, Vector2.UnitX, 0);
        Assert.True(w.Shore.X > before + 4, "40 seconds are five more waves, not a fraction of one");
        // Starting the clock at a time (--water-seconds) lands on the count that moment has, modulo the wrap.
        double seconds = (WaveSet.PhaseWrap + 12.5) * WaveSet.BreakerPeriod;
        var late = new WaveSet();
        late.Step(seconds / WaveSet.SecondsPerGameHour, Vector2.UnitX, 0);
        Assert.Equal(12.5f, late.Shore.X, 2);
        Assert.InRange(late.Shore.X, 0f, (float)WaveSet.PhaseWrap);
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
