using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The game's point and spot lights on the world (docs/render-lights.md): the lamps of every placed building (<see cref="WorldLights"/>,
/// loaded on a worker), binned each frame into a grid of <see cref="Cells"/>² cells of <see cref="CellSize"/> around the eye and uploaded as three
/// textures, which <c>kenshiLight</c> (<see cref="LampShaders"/>) and the probe trace read. Interior lamps are clipped to their building's
/// mask; the probe trace also shadows their bounce contribution. Buildings placed during play add theirs with <see cref="SetRuntime"/>. Render thread only.
/// </summary>
internal sealed class WorldLamps : IDisposable
{
    public const int Cells = 64, IndexWidth = 1024, IndexRows = 16, MaxLamps = 1024;
    public const float CellSize = 128;
    internal const int MaxRoomEdges = 32, DataWidth = 5 + MaxRoomEdges;
    // Allow for the small offset between collision masks and visible wall/floor surfaces.
    const float RoomSurfaceMargin = 0.5f;

    readonly GpuContext ctx;
    readonly Task<WorldLight[]> loading;
    // The lamps binned each frame: the world's (once loaded) and those of buildings placed during play, by building id.
    WorldLight[]? world, lamps;
    readonly Dictionary<string, WorldLight[]> runtime = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> runtimePower = new(StringComparer.Ordinal);
    bool runtimeChanged;
    readonly Texture[] cells, index, data;
    readonly Sampler nearest;
    readonly float[] cellValues = new float[Cells * Cells * 2];
    readonly float[] indexValues = new float[IndexWidth * IndexRows];
    readonly Vector4[] dataValues = new Vector4[MaxLamps * DataWidth];
    readonly List<(int Lamp, InteriorVolume? Room)> near = [];
    readonly Dictionary<string, uint> emitters = new(StringComparer.Ordinal);
    readonly int[] counts = new int[Cells * Cells];
    int slot;
    Vector4 grid;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Whether the lamps light the world (the bench's <c>lights</c> switch).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The <c>interiorlamps</c> switch's Meitou side: night-only lamps of interior layouts shine day and night (the game: night only).</summary>
    public bool InteriorAllDay { get; set; } = true;
    /// <summary>The lamps of the world, once loaded; and those in this frame's grid.</summary>
    public int Count => lamps?.Length ?? 0;
    public int InGrid { get; private set; }

    public WorldLamps(GpuContext ctx, Func<IReadOnlyList<WorldLight>> load)
    {
        this.ctx = ctx;
        loading = Task.Run(() => load().Where(l => l.Intensity > 0 && l.Radius > 0).ToArray());
        int frames = ctx.Device.Frames.Count;
        cells = new Texture[frames];
        index = new Texture[frames];
        data = new Texture[frames];
        const TextureUse use = TextureUse.Sampled | TextureUse.TransferDst;
        for (int i = 0; i < frames; i++)
        {
            cells[i] = Texture.Create(ctx, new TextureDesc(Format.R32G32Sfloat, Cells, Cells, Use: use, Name: "lamp cells"));
            index[i] = Texture.Create(ctx, new TextureDesc(Format.R32Sfloat, IndexWidth, IndexRows, Use: use, Name: "lamp index"));
            data[i] = Texture.Create(ctx, new TextureDesc(Format.R32G32B32A32Sfloat, DataWidth, MaxLamps, Use: use, Name: "lamp data"));
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
    internal uint Emitter(string placementId) => emitters.GetValueOrDefault(placementId);

    /// <summary>Shows the lamps of a building placed during play, replacing any it had (the host calls it when the building is complete).</summary>
    public void SetRuntime(string building, IEnumerable<WorldLight> lights)
    {
        runtime[building] = [.. lights.Where(l => l.Intensity > 0 && l.Radius > 0)];
        runtimeChanged = true;
    }

    /// <summary>
    /// The factor on a placed building's lamps (docs/formats/lights.md "When a light is on": the game's per-update power scale, the building's power share);
    /// at 0 or below they are hidden. 1 until set.
    /// </summary>
    public void SetRuntimePower(string building, float factor) => runtimePower[building] = factor;

    public void RemoveRuntime(string building)
    {
        runtimeChanged |= runtime.Remove(building);
        runtimePower.Remove(building);
    }

    public void ClearRuntime()
    {
        runtimeChanged |= runtime.Count > 0;
        runtime.Clear();
        runtimePower.Clear();
    }

    /// <summary>The power factor of lamp <paramref name="i"/>: 1 for the world's, the building's factor for a placed building's (its placement id is the building id).</summary>
    float PowerFactor(int i) => i >= lamps!.Length ? 1 : runtimePower.GetValueOrDefault(lamps[i].PlacementId, 1);

    /// <summary>Lamp <paramref name="i"/> of this frame: the world's and placed buildings', then <see cref="Moving"/>.</summary>
    WorldLight Lamp(int i) => i < lamps!.Length ? lamps[i] : Moving[i - lamps.Length];

    /// <summary>This frame's moving lamps (lanterns carried by characters), filled by the frame before <see cref="Update"/>.</summary>
    public List<WorldLight> Moving { get; } = [];

    /// <summary>
    /// Bins the lamps around <paramref name="eye"/> for this frame (the pulsing and shimmering ones at their power now). <paramref name="daylight"/> is the game's daylight
    /// factor (<see cref="WorldLights.DaylightFactor"/>): night-only lamps are left out above <see cref="WorldLights.NightDaylightLimit"/>
    /// (interior ones only without <see cref="InteriorAllDay"/>).
    /// </summary>
    public void Update(Vector3 eye, WorldObjectRenderer? objects = null, float daylight = 0)
    {
        slot = ctx.Frame.Slot;
        grid = default;
        InGrid = 0;
        emitters.Clear();
        if (!Enabled) return;
        if (world is null)
        {
            if (!loading.IsCompleted) return;
            if (loading.IsFaulted) Console.Error.WriteLine($"lamps     not loaded: {loading.Exception?.InnerException?.Message}");
            world = loading.IsFaulted ? [] : loading.Result;
            Console.WriteLine($"lamps     {world.Length} lights ({world.Count(l => l.Interior)} interior)");
            runtimeChanged = true;
        }
        if (runtimeChanged || lamps is null)
        {
            lamps = runtime.Count == 0 ? world : [.. world.Where(l => !runtime.ContainsKey(l.PlacementId)
                && !runtime.Keys.Any(id => l.PlacementId.StartsWith(id + "/", StringComparison.Ordinal))), .. runtime.Values.SelectMany(r => r)];
            runtimeChanged = false;
        }
        int count = lamps.Length + Moving.Count;
        if (count == 0) return;

        float x0 = MathF.Floor(eye.X / CellSize) * CellSize - Cells / 2 * CellSize, z0 = MathF.Floor(eye.Z / CellSize) * CellSize - Cells / 2 * CellSize;
        float x1 = x0 + Cells * CellSize, z1 = z0 + Cells * CellSize;
        near.Clear();
        for (int i = 0; i < count && near.Count < MaxLamps; i++)
        {
            var l = Lamp(i);
            if (l.Position.X + l.Radius < x0 || l.Position.X - l.Radius > x1 || l.Position.Z + l.Radius < z0 || l.Position.Z - l.Radius > z1) continue;
            if (!WorldLights.IsLit(l, l.Interior && InteriorAllDay ? 0 : daylight) || PowerFactor(i) <= 0) continue;
            var room = l.Interior ? objects?.InteriorFor(l.PlacementId) : null;
            // An unloaded or unrepresentable room must not turn its lights into unbounded outdoor lamps.
            if (l.Interior && (room is null || room.Footprint.Length is < 3 or > MaxRoomEdges)) continue;
            near.Add((i, room));
            if (room is not null && !emitters.ContainsKey(l.PlacementId)) emitters.Add(l.PlacementId, (uint)emitters.Count + 1);
        }
        if (near.Count == 0) return;

        // The lamps' rows: position and radius; radiance (the game's power, its effect now) and type; spot direction, cos of the half cones, falloff.
        float t = (float)(clock.Elapsed.TotalSeconds * 60);
        for (int k = 0; k < near.Count; k++)
        {
            var (lamp, room) = near[k];
            var l = Lamp(lamp);
            float power = l.Intensity * WorldLights.EffectFactor(l.Effect, t) * PowerFactor(lamp);
            float inner = MathF.Min(l.InnerAngle, l.OuterAngle), outer = MathF.Max(l.InnerAngle, l.OuterAngle);
            int row = k * DataWidth;
            dataValues[row] = new Vector4(l.Position, l.Radius);
            dataValues[row + 1] = new Vector4(l.Colour * power, l.Type == WorldLightType.Spot ? 1 : 0);
            dataValues[row + 2] = new Vector4(l.Direction, MathF.Cos(outer * 0.5f));
            dataValues[row + 3] = new Vector4(MathF.Cos(inner * 0.5f), l.Falloff > 0 ? l.Falloff : 1, room?.Footprint.Length ?? 0, Emitter(l.PlacementId));
            if (room is null) continue;
            dataValues[row + 4] = new Vector4(room.YMin - RoomSurfaceMargin, room.YMax + RoomSurfaceMargin, 0, 0);
            for (int e = 0; e < room.Footprint.Length; e++)
            {
                var at = room.Footprint[e];
                var edge = room.Footprint[(e + 1) % room.Footprint.Length] - at;
                var inward = Vector2.Normalize(new Vector2(-edge.Y, edge.X));
                dataValues[row + 5 + e] = new Vector4(inward, -Vector2.Dot(inward, at) + RoomSurfaceMargin, 0);
            }
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
                var l = Lamp(near[k].Lamp);
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
        ctx.Uploads.Write(data[slot], 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(DataWidth, (uint)near.Count)), MemoryMarshal.AsBytes(dataValues.AsSpan(0, near.Count * DataWidth)));
        grid = new Vector4(x0, z0, CellSize, Cells);
        InGrid = near.Count;
    }

    public string Describe() => lamps is null ? "loading" : $"{lamps.Length} lamps, {InGrid} around the eye" + (Enabled ? "" : ", off");

    public void Dispose()
    {
        foreach (var t in cells.Concat(index).Concat(data)) t.Dispose();
    }
}
