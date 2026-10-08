using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>
/// What <c>flowmap.png</c> is, measured against the heightmap (docs/formats/terrain.md "Flow map"). Needs the install.
/// Set MEITOU_ANALYSIS_DIR to also write a picture (the flow as colour over the water mask).
/// </summary>
[Slow]
public class FlowMapTests(Xunit.ITestOutputHelper output)
{
    const float WaterY = WorldWater.Height;

    static float Al(float fx, float fy, float ax, float ay, float mag) => mag > 0.02f ? MathF.Abs(fx * ax + fy * ay) / mag : float.NaN;

    [Fact]
    public void Flow_map_against_the_terrain()
    {
        Assert.SkipWhen(InstallData.Install is null, "no Kenshi install");
        var install = InstallData.Install!;
        var flow = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, WorldWater.FlowMap)));
        int n = flow.Width;
        Assert.Equal(flow.Height, n);
        float texel = (float)WorldLayout.WorldSize / n;
        output.WriteLine($"flow map {n}^2, {texel} units a texel");

        // Heights at the flow texels' centres: the heightmap point-sampled every `step` samples is a grid of n + 1 corners.
        using var map = TerrainHeightmap.Open(install);
        int step = (map.Size - 1) / n;
        Assert.Equal(map.Size - 1, step * n);
        var raw = map.Downsample(step, out int g);
        Assert.Equal(n + 1, g);
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                h[j * n + i] = WorldLayout.RawToHeight((ushort)((raw[j * g + i] + raw[j * g + i + 1] + raw[(j + 1) * g + i] + raw[(j + 1) * g + i + 1]) / 4));

        // Distance to land (chamfer, in texels) for the water texels.
        var wet = new bool[n * n];
        for (int k = 0; k < wet.Length; k++) wet[k] = h[k] < WaterY;
        var d = new float[n * n];
        for (int k = 0; k < d.Length; k++) d[k] = wet[k] ? 1e6f : 0f;
        void Pass(int x0, int x1, int dx, int y0, int y1, int dy)
        {
            for (int y = y0; y != y1; y += dy)
                for (int x = x0; x != x1; x += dx)
                {
                    int k = y * n + x;
                    if (d[k] == 0) continue;
                    float v = d[k];
                    foreach (var (ox, oy, c) in new[] { (-dx, 0, 1f), (0, -dy, 1f), (-dx, -dy, 1.414f), (dx, -dy, 1.414f) })
                    {
                        int xx = x + ox, yy = y + oy;
                        if (xx < 0 || yy < 0 || xx >= n || yy >= n) { v = Math.Min(v, 1e5f); continue; }
                        v = Math.Min(v, d[yy * n + xx] + c);
                    }
                    d[k] = v;
                }
        }
        Pass(0, n, 1, 0, n, 1);
        Pass(n - 1, -1, -1, n - 1, -1, -1);
        Pass(n - 1, -1, -1, 0, n, 1);
        Pass(0, n, 1, n - 1, -1, -1);

        // Local half-width: the largest distance-to-land within 5 texels (a river is narrow, a lake or the sea is not).
        const int R = 5;
        var local = new float[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (!wet[y * n + x]) continue;
                float m = 0;
                for (int dy = -R; dy <= R; dy++)
                    for (int dx = -R; dx <= R; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= n || yy >= n || dx * dx + dy * dy > R * R) continue;
                        m = Math.Max(m, d[yy * n + xx]);
                    }
                local[y * n + x] = m;
            }

        var rows = new List<(string Name, Func<int, bool> In)>
        {
            ("river   (water, half-width <= 3 texels)", k => wet[k] && local[k] <= 3f),
            ("channel (water, half-width 3..6 texels)", k => wet[k] && local[k] > 3f && local[k] <= 6f),
            ("lake/sea(water, half-width > 6 texels) ", k => wet[k] && local[k] > 6f),
            ("land    (above the water)              ", k => !wet[k]),
        };
        var acc = rows.ToDictionary(r => r.Name, _ => new List<(float Mag, float B, float Align, float Down, float A1, float A2, float A3)>());
        for (int y = 3; y < n - 3; y++)
            for (int x = 3; x < n - 3; x++)
            {
                int k = y * n + x;
                float fx = flow.Pixels[k * 4] / 255f * 2 - 1, fy = flow.Pixels[k * 4 + 1] / 255f * 2 - 1, b = flow.Pixels[k * 4 + 2] / 255f;
                float mag = MathF.Sqrt(fx * fx + fy * fy);
                // The channel axis: perpendicular to the principal gradient direction of the wet mask (structure tensor over 5 x 5 texels).
                float sxx = 0, sxy = 0, syy = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int q = (y + dy) * n + x + dx;
                        float gx = (wet[q + 1] ? 1f : 0f) - (wet[q - 1] ? 1f : 0f), gy = (wet[q + n] ? 1f : 0f) - (wet[q - n] ? 1f : 0f);
                        sxx += gx * gx; sxy += gx * gy; syy += gy * gy;
                    }
                float theta = 0.5f * MathF.Atan2(2 * sxy, sxx - syy);
                float ax = -MathF.Sin(theta), ay = MathF.Cos(theta);
                float align = mag > 0.02f ? MathF.Abs(fx * ax + fy * ay) / mag : float.NaN;
                // Downhill of the bed (5 x 5 summed gradient).
                float hx = 0, hy = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++) { int q = (y + dy) * n + x + dx; hx += h[q + 1] - h[q - 1]; hy += h[q + n] - h[q - n]; }
                float hm = MathF.Sqrt(hx * hx + hy * hy);
                float down = mag > 0.02f && hm > 1e-3f ? -(fx * hx + fy * hy) / (mag * hm) : float.NaN;
                foreach (var r in rows) if (r.In(k)) acc[r.Name].Add((mag, b, align, down, Al(fy, fx, ax, ay, mag), Al(fx, -fy, ax, ay, mag), Al(-fx, fy, ax, ay, mag)));
            }
        foreach (var r in rows)
        {
            var a = acc[r.Name];
            if (a.Count == 0) { output.WriteLine($"{r.Name}: none"); continue; }
            float Mean(IEnumerable<float> v) { var l = v.Where(float.IsFinite).ToList(); return l.Count == 0 ? float.NaN : l.Average(); }
            var mags = a.Select(t => t.Mag).OrderBy(v => v).ToArray();
            var aligned = a.Where(t => float.IsFinite(t.Align)).ToList();
            var downs = a.Where(t => float.IsFinite(t.Down)).ToList();
            output.WriteLine($"{r.Name}: {a.Count} texels, |RG| mean {Mean(a.Select(t => t.Mag)):0.000} p10 {mags[mags.Length / 10]:0.000} p50 {mags[mags.Length / 2]:0.000} p90 {mags[mags.Length * 9 / 10]:0.000}, B mean {Mean(a.Select(t => t.B)):0.00}, "
                + $"|cos(flow, channel axis)| mean {Mean(a.Select(t => t.Align)):0.00} (random 0.64), share > 0.9: {aligned.Count(t => t.Align > 0.9f) / (float)Math.Max(aligned.Count, 1):0.00}, "
                + $"other readings (RG swapped, G flipped, R flipped) {Mean(a.Select(t => t.A1)):0.00} {Mean(a.Select(t => t.A2)):0.00} {Mean(a.Select(t => t.A3)):0.00}, "
                + $"cos(flow, downhill) mean {Mean(a.Select(t => t.Down)):0.00}, share downhill: {downs.Count(t => t.Down > 0) / (float)Math.Max(downs.Count, 1):0.00}");
        }

        var baked = Meitou.Rendering.RiverFlowBake.Bake(raw, n, (ushort)Math.Ceiling(WaterY * ushort.MaxValue / WorldLayout.MaxHeight));
        int withWeight = 0, strong = 0;
        for (int k = 0; k < n * n; k++) { if (baked[k * 4 + 3] > 0) withWeight++; if (baked[k * 4 + 3] > 200) strong++; }
        output.WriteLine($"river map: {withWeight} texels with weight, {strong} above 0.8 (narrow water texels in this analysis: {acc[rows[0].Name].Count + acc[rows[1].Name].Count})");
        // Places for screenshots: the strongest river texel in each 256-texel block, with its flow.
        for (int by = 0; by < n; by += 256)
            for (int bx = 0; bx < n; bx += 256)
            {
                int best = -1, count = 0;
                for (int y = by; y < by + 256; y++)
                    for (int x = bx; x < bx + 256; x++)
                    {
                        int k = y * n + x;
                        if (baked[k * 4 + 3] < 230 || !wet[k]) continue;
                        count++;
                        if (best < 0 || (x * 31 + y * 17) % 97 == 0) best = k;
                    }
                if (best >= 0 && count > 150)
                    output.WriteLine($"river place: {count} strong texels in block, e.g. world ({(best % n + 0.5f) * texel - WorldLayout.HalfWorldSize:0}, {(best / n + 0.5f) * texel - WorldLayout.HalfWorldSize:0}) flow ({baked[best * 4] / 255f * 2 - 1:0.0}, {baked[best * 4 + 1] / 255f * 2 - 1:0.0})");
            }

        if (Environment.GetEnvironmentVariable("MEITOU_ANALYSIS_DIR") is { Length: > 0 } outDir)
        {
            var vis = new byte[n * n * 4];
            for (int k = 0; k < n * n; k++)
            {
                bool w = wet[k];
                byte a = baked[k * 4 + 3];
                vis[k * 4] = a > 0 ? baked[k * 4] : (byte)(w ? 0 : 60);
                vis[k * 4 + 1] = a > 0 ? baked[k * 4 + 1] : (byte)(w ? 0 : 60);
                vis[k * 4 + 2] = a > 0 ? (byte)(a / 2 + 100) : (byte)(w ? 150 : 60);
                vis[k * 4 + 3] = 255;
            }
            PngWriter.Write(Path.Combine(outDir, "river-map.png"), n, n, vis);
        }

        // Claims recorded in docs/formats/terrain.md: in the narrow water RG does not follow the channel under any reading of its two channels
        // (random directions score 0.64), and B (the scum amount) is higher there than in open water.
        float M(string name, Func<(float Mag, float B, float Align, float Down, float A1, float A2, float A3), float> f) =>
            acc[name].Select(f).Where(float.IsFinite).Average();
        var river = rows[0].Name;
        Assert.InRange(M(river, t => t.Align), 0f, 0.75f);
        Assert.InRange(M(river, t => t.A1), 0f, 0.75f);
        Assert.InRange(M(river, t => t.A2), 0f, 0.75f);
        Assert.InRange(M(river, t => t.A3), 0f, 0.75f);
        Assert.InRange(M(river, t => t.Down), -0.2f, 0.2f);
        Assert.True(M(river, t => t.B) > 3 * M(rows[2].Name, t => t.B));

        // The north-west river (x 650..790, y 500..900 in texels): each fifth river texel's flow, and the bed height 3 texels either side.
        int printed = 0;
        for (int y = 520; y < 900 && printed < 40; y += 6)
            for (int x = 650; x < 790; x++)
            {
                int k = y * n + x;
                if (!wet[k] || local[k] > 3f) continue;
                float fx = flow.Pixels[k * 4] / 255f * 2 - 1, fy = flow.Pixels[k * 4 + 1] / 255f * 2 - 1;
                output.WriteLine($"river texel ({x},{y}) world ({(x + 0.5f) * texel - WorldLayout.HalfWorldSize:0},{(y + 0.5f) * texel - WorldLayout.HalfWorldSize:0}) flow ({fx:0.00},{fy:0.00}) bed {h[k]:0} dx+3 {h[k + 3] - h[k]:0} dx-3 {h[k - 3] - h[k]:0} dy+3 {h[k + 3 * n] - h[k]:0} dy-3 {h[k - 3 * n] - h[k]:0}");
                printed++;
                break;
            }

        if (Environment.GetEnvironmentVariable("MEITOU_ANALYSIS_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            var img = new byte[n * n * 4];
            for (int k = 0; k < n * n; k++)
            {
                byte r = flow.Pixels[k * 4], gg = flow.Pixels[k * 4 + 1];
                if (wet[k]) { img[k * 4] = r; img[k * 4 + 1] = gg; img[k * 4 + 2] = (byte)(local[k] <= 3 ? 255 : local[k] <= 6 ? 160 : 60); }
                else { byte v = (byte)Math.Min(255, 40 + h[k] / 12f); img[k * 4] = v; img[k * 4 + 1] = v; img[k * 4 + 2] = v; }
                img[k * 4 + 3] = 255;
            }
            PngWriter.Write(Path.Combine(dir, "flow-over-water.png"), n, n, img);
            var blocks = new List<(int Count, float X, float Z)>();
            for (int by = 0; by < n; by += 64)
                for (int bx = 0; bx < n; bx += 64)
                {
                    int c = 0;
                    for (int y = by; y < by + 64; y++) for (int x = bx; x < bx + 64; x++) if (wet[y * n + x] && local[y * n + x] <= 3f) c++;
                    blocks.Add((c, (bx + 32) * texel - WorldLayout.HalfWorldSize, (by + 32) * texel - WorldLayout.HalfWorldSize));
                }
            foreach (var b in blocks.OrderByDescending(b => b.Count).Take(15)) output.WriteLine($"narrow water block: {b.Count} texels at ({b.X:0}, {b.Z:0})");
        }
    }
}
