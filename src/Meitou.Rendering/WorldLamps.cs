using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The game's point and spot lights on the world (docs/render-lights.md): the outdoor lamps of every placed building (<see cref="WorldLights"/>,
/// loaded on a worker), binned each frame into a grid of <see cref="Cells"/>² cells of <see cref="CellSize"/> around the eye and uploaded as three
/// textures, which <c>kenshiLight</c> (<see cref="LampShaders"/>) and the probe trace read. Interior lights are left out: the game gives its
/// lights no shadows, so they would shine through the walls. Render thread only.
/// </summary>
internal sealed class WorldLamps : IDisposable
{
    public const int Cells = 64, IndexWidth = 1024, IndexRows = 16, MaxLamps = 1024;
    public const float CellSize = 128;

    readonly GpuContext ctx;
    readonly Task<WorldLight[]> loading;
    WorldLight[]? lamps;
    readonly Texture[] cells, index, data;
    readonly Sampler nearest;
    readonly float[] cellValues = new float[Cells * Cells * 2];
    readonly float[] indexValues = new float[IndexWidth * IndexRows];
    readonly Vector4[] dataValues = new Vector4[MaxLamps * 4];
    readonly List<int> near = [];
    readonly int[] counts = new int[Cells * Cells];
    int slot;
    Vector4 grid;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Whether the lamps light the world (<c>--lights off</c>, the bench's <c>lights</c> switch).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The lamps of the world (outdoor, lit), once loaded; and those in this frame's grid.</summary>
    public int Count => lamps?.Length ?? 0;
    public int InGrid { get; private set; }

    public WorldLamps(GpuContext ctx, Func<IReadOnlyList<WorldLight>> load)
    {
        this.ctx = ctx;
        loading = Task.Run(() => load().Where(l => !l.Interior && l.Intensity > 0 && l.Radius > 0).ToArray());
        int frames = ctx.Device.Frames.Count;
        cells = new Texture[frames];
        index = new Texture[frames];
        data = new Texture[frames];
        const TextureUse use = TextureUse.Sampled | TextureUse.TransferDst;
        for (int i = 0; i < frames; i++)
        {
            cells[i] = Texture.Create(ctx, new TextureDesc(Format.R32G32Sfloat, Cells, Cells, Use: use, Name: "lamp cells"));
            index[i] = Texture.Create(ctx, new TextureDesc(Format.R32Sfloat, IndexWidth, IndexRows, Use: use, Name: "lamp index"));
            data[i] = Texture.Create(ctx, new TextureDesc(Format.R32G32B32A32Sfloat, 4, MaxLamps, Use: use, Name: "lamp data"));
        }
        nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        var g = ctx.Globals;
        g.PublishUniform("uLightGrid", () => grid);
        g.Publish("uLightCells", () => Sampled(cells[slot]));
        g.Publish("uLightIndex", () => Sampled(index[slot]));
        g.Publish("uLightData", () => Sampled(data[slot]));
    }

    SampledTexture Sampled(Texture t) => new(nearest, t.View(), t.Image);

    /// <summary>The textures of this frame's slot, for a compute pass that binds them itself (the probe trace).</summary>
    internal (Texture Cells, Texture Index, Texture Data) Current => (cells[slot], index[slot], data[slot]);
    internal Sampler NearestSampler => nearest;
    /// <summary>This frame's <c>uLightGrid</c> (w 0: no lamps).</summary>
    internal Vector4 Grid => grid;

    /// <summary>Bins the lamps around <paramref name="eye"/> for this frame (the pulsing and shimmering ones at their power now).</summary>
    public void Update(Vector3 eye)
    {
        slot = ctx.Frame.Slot;
        grid = default;
        InGrid = 0;
        if (!Enabled) return;
        if (lamps is null)
        {
            if (!loading.IsCompleted) return;
            if (loading.IsFaulted) { Console.Error.WriteLine($"lamps     not loaded: {loading.Exception?.InnerException?.Message}"); lamps = []; return; }
            lamps = loading.Result;
            Console.WriteLine($"lamps     {lamps.Length} outdoor lights");
        }
        if (lamps.Length == 0) return;

        float x0 = MathF.Floor(eye.X / CellSize) * CellSize - Cells / 2 * CellSize, z0 = MathF.Floor(eye.Z / CellSize) * CellSize - Cells / 2 * CellSize;
        float x1 = x0 + Cells * CellSize, z1 = z0 + Cells * CellSize;
        near.Clear();
        for (int i = 0; i < lamps.Length && near.Count < MaxLamps; i++)
        {
            var l = lamps[i];
            if (l.Position.X + l.Radius < x0 || l.Position.X - l.Radius > x1 || l.Position.Z + l.Radius < z0 || l.Position.Z - l.Radius > z1) continue;
            near.Add(i);
        }
        if (near.Count == 0) return;

        // The lamps' rows: position and radius; radiance (the game's power, its effect now) and type; spot direction, cos of the half cones, falloff.
        float t = (float)(clock.Elapsed.TotalSeconds * 60);
        for (int k = 0; k < near.Count; k++)
        {
            var l = lamps[near[k]];
            float power = l.Intensity * WorldLights.EffectFactor(l.Effect, t);
            float inner = MathF.Min(l.InnerAngle, l.OuterAngle), outer = MathF.Max(l.InnerAngle, l.OuterAngle);
            dataValues[k * 4] = new Vector4(l.Position, l.Radius);
            dataValues[k * 4 + 1] = new Vector4(l.Colour * power, l.Type == WorldLightType.Spot ? 1 : 0);
            dataValues[k * 4 + 2] = new Vector4(l.Direction, MathF.Cos(outer * 0.5f));
            dataValues[k * 4 + 3] = new Vector4(MathF.Cos(inner * 0.5f), l.Falloff > 0 ? l.Falloff : 1, 0, 0);
        }

        // Each cell lists the lamps whose reach overlaps it (counted, then placed).
        Array.Clear(counts);
        for (int pass = 0; pass < 2; pass++)
        {
            int total = 0;
            if (pass == 1)
            {
                for (int c = 0; c < counts.Length; c++)
                {
                    cellValues[c * 2] = total;
                    cellValues[c * 2 + 1] = 0;
                    total += counts[c];
                }
                if (total > IndexWidth * IndexRows) { grid = default; return; }
            }
            for (int k = 0; k < near.Count; k++)
            {
                var l = lamps[near[k]];
                int cx0 = Math.Max((int)MathF.Floor((l.Position.X - l.Radius - x0) / CellSize), 0), cx1 = Math.Min((int)MathF.Floor((l.Position.X + l.Radius - x0) / CellSize), Cells - 1);
                int cz0 = Math.Max((int)MathF.Floor((l.Position.Z - l.Radius - z0) / CellSize), 0), cz1 = Math.Min((int)MathF.Floor((l.Position.Z + l.Radius - z0) / CellSize), Cells - 1);
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int c = cz * Cells + cx;
                        if (pass == 0) { counts[c]++; continue; }
                        int at = (int)cellValues[c * 2] + (int)cellValues[c * 2 + 1];
                        indexValues[at] = k;
                        cellValues[c * 2 + 1]++;
                    }
            }
        }

        var full = new Rect2D(new Offset2D(0, 0), new Extent2D(Cells, Cells));
        ctx.Uploads.Write(cells[slot], 0, 0, full, MemoryMarshal.AsBytes(cellValues.AsSpan()));
        ctx.Uploads.Write(index[slot], 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(IndexWidth, IndexRows)), MemoryMarshal.AsBytes(indexValues.AsSpan()));
        ctx.Uploads.Write(data[slot], 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(4u, (uint)near.Count)), MemoryMarshal.AsBytes(dataValues.AsSpan(0, near.Count * 4)));
        grid = new Vector4(x0, z0, CellSize, Cells);
        InGrid = near.Count;
    }

    public string Describe() => lamps is null ? "loading" : $"{lamps.Length} outdoor lamps, {InGrid} around the eye" + (Enabled ? "" : ", off");

    public void Dispose()
    {
        foreach (var t in cells.Concat(index).Concat(data)) t.Dispose();
    }
}
