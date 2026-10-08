using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The placed fog volumes of <c>fogfeatures.dat</c> (docs/formats/fogfeatures.md): the swamp's ground fog, the Fog Islands', the Vain's red
/// haze and the rest. Every block is one row of a small float texture (<c>uFogVolumes</c>, uploaded once); each frame <see cref="Update"/>
/// picks the blocks near the eye (at most <see cref="FogVolumeShaders.MaxActive"/>, farthest first) and the light, which every world shader
/// reads through <see cref="FogVolumeShaders"/>. Spheres and beams (three in the base game, none in the swamp) are not drawn.
/// </summary>
internal sealed class FogVolumes : IDisposable
{
    /// <summary>The lowest height a block is measured at (the selection and the shader's box): the sea is at 100 (<see cref="WorldWater.Height"/>),
    /// with room for the sea floor seen through the water.</summary>
    public const float SectionHeight = -1000f;
    /// <summary>The highest height the shader's box reaches for a block open upwards (far above the game's camera).</summary>
    public const float HighHeight = 30000f;
    /// <summary>How far (in x, z) from the eye a block's box may be and still be drawn: a volume whose near side is past 22000 takes the haze's
    /// colour there with the haze's alpha, and past 30000 the haze is complete, so it only repeats what the hazed scene already shows.</summary>
    public const float Range = 30000f;

    readonly SampledImage? texture;
    readonly Block[] blocks;
    readonly Vector4[] select = new Vector4[2];
    Vector4 eyeLight;

    readonly record struct Block(int Row, Vector2 Min, Vector2 Max, Vector2 Centre);

    /// <summary>Off: no volume is drawn (<c>--no-fog-volumes</c>; also while the simple sky is on).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The volumes picked in the last <see cref="Update"/>, farthest first (statistics, tests).</summary>
    public List<string> Active { get; } = [];
    public IReadOnlyList<FogFeature> Features { get; }

    public FogVolumes(GpuContext gpu, IReadOnlyList<FogFeature> features)
    {
        Features = features;
        var rows = features.Where(f => f.Type == FogFeatureType.Block).ToList();
        var list = new List<Block>();
        if (rows.Count > 0)
        {
            var data = new Vector4[FogVolumeShaders.RowTexels * rows.Count];
            for (int r = 0; r < rows.Count; r++)
            {
                var f = rows[r];
                int o = r * FogVolumeShaders.RowTexels;
                for (int k = 0; k < 7; k++) data[o + k] = f.Planes[k];
                data[o + 7] = new Vector4(f.Colour, f.Density);
                data[o + 8] = new Vector4(f.EdgeBlur, 0, 0, 0);
                // The box: the sections at the lowest and the highest height (a convex block's sections in between lie within both boxes), up to its
                // highest corner, or the highest height when it is open upwards ("Volcano01"). An empty section at both: a box no ray meets.
                var low = f.SectionBounds(SectionHeight);
                var high = f.SectionBounds(HighHeight);
                var corners = f.Corners();
                float top = high is not null ? HighHeight : corners.Count > 0 ? corners.Max(c => c.Y) : HighHeight;
                if ((low ?? high) is { } first)
                {
                    var (min, max) = first;
                    if (high is { } h) { min = Vector2.Min(min, h.Min); max = Vector2.Max(max, h.Max); }
                    list.Add(new Block(r, min, max, (min + max) * 0.5f));
                    data[o + 9] = new Vector4(min.X, SectionHeight, min.Y, 0);
                    data[o + 10] = new Vector4(max.X, MathF.Max(top, SectionHeight), max.Y, 0);
                }
                else
                {
                    data[o + 9] = new Vector4(1e9f, 1e9f, 1e9f, 0);
                    data[o + 10] = new Vector4(1e9f, 1e9f, 1e9f, 0);
                }
            }
            texture = SampledImage.Rgba32F(gpu, data, FogVolumeShaders.RowTexels, rows.Count, "fog volumes");
        }
        blocks = [.. list];
        Names = [.. rows.Select(f => f.Name)];
        var g = gpu.Globals;
        g.Publish("uFogVolumes", () => texture is { } t ? t.Sampled() : default);
        g.PublishUniform("uFogVolumeEye", () => eyeLight);
        g.PublishUniform("uFogVolumeSelect0", () => select[0]);
        g.PublishUniform("uFogVolumeSelect1", () => select[1]);
    }

    readonly string[] Names;

    /// <summary>The game's light on the volumes (post/fog.hlsl <c>fog_planes_fs</c>): <c>max(0.08, sunColour.w · 1.6 · saturate(3 sunY + 0.2))</c>.</summary>
    public static float Light(float sunY) => MathF.Max(0.08f, KenshiLighting.Daylight(sunY) * 1.6f * Math.Clamp(sunY * 3f + 0.2f, 0f, 1f));

    /// <summary>Picks this frame's volumes around <paramref name="eye"/> and the light for the sun's height; <paramref name="on"/> false draws none.</summary>
    public void Update(Vector3 eye, float sunY, bool on)
    {
        Active.Clear();
        Span<float> rows = stackalloc float[FogVolumeShaders.MaxActive];
        rows.Clear();
        if (on && Enabled && texture is not null)
        {
            var eyeXz = new Vector2(eye.X, eye.Z);
            var near = new List<(Block B, float Gap)>();
            foreach (var b in blocks)
            {
                var gap = Vector2.Max(Vector2.Max(b.Min - eyeXz, eyeXz - b.Max), Vector2.Zero).Length();
                if (gap <= Range) near.Add((b, gap));
            }
            // The nearest ones, then drawn farthest first (the game's transparent objects are sorted back to front).
            var picked = near.OrderBy(n => n.Gap).Take(FogVolumeShaders.MaxActive)
                .OrderByDescending(n => Vector2.DistanceSquared(n.B.Centre, eyeXz)).ToList();
            for (int i = 0; i < picked.Count; i++)
            {
                rows[i] = picked[i].B.Row + 1;
                Active.Add(Names[picked[i].B.Row]);
            }
        }
        select[0] = new Vector4(rows[0], rows[1], rows[2], rows[3]);
        select[1] = new Vector4(rows[4], rows[5], rows[6], rows[7]);
        eyeLight = new Vector4(eye, Light(sunY));
    }

    public void Dispose() => texture?.Dispose();
}
