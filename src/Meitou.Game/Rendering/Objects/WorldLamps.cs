using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Objects;

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

    readonly IGpuContext _ctx;
    readonly Task<WorldLight[]> _loading;
    // The lamps binned each frame: the world's (once loaded) and those of buildings placed during play, by building id.
    WorldLight[]? _world, _lamps;
    readonly Dictionary<string, WorldLight[]> _runtime = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> _runtimePower = new(StringComparer.Ordinal);
    bool _runtimeChanged;
    // The world's lamps at the start of lamps (those of no replaced building); the placed buildings' follow.
    int _worldCount;
    readonly GpuTexture[] _cells, _index, _data;
    readonly GpuSampler _nearest;
    readonly float[] _cellValues = new float[Cells * Cells * 2];
    readonly float[] _indexValues = new float[IndexWidth * IndexRows];
    readonly Vector4[] _dataValues = new Vector4[MaxLamps * DataWidth];
    readonly List<(int Lamp, InteriorVolume? Room)> _near = [];
    readonly Dictionary<string, uint> _emitters = new(StringComparer.Ordinal);
    readonly int[] _counts = new int[Cells * Cells];
    int _slot;
    Vector4 _grid;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Whether the lamps light the world (the bench's <c>lights</c> switch).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The <c>interiorlamps</c> switch's Meitou side: night-only lamps of interior layouts shine day and night (the game: night only).</summary>
    public bool InteriorAllDay { get; set; } = true;
    /// <summary>The lamps of the world, once loaded; and those in this frame's grid.</summary>
    public int Count => _lamps?.Length ?? 0;
    public int InGrid { get; private set; }

    public WorldLamps(IGpuContext ctx, Func<IReadOnlyList<WorldLight>> load)
    {
        this._ctx = ctx;
        _loading = Task.Run(() => load().Where(l => l.Intensity > 0 && l.Radius > 0).ToArray());
        int frames = ctx.FramesInFlight;
        _cells = new GpuTexture[frames];
        _index = new GpuTexture[frames];
        _data = new GpuTexture[frames];
        const TextureUse use = TextureUse.Sampled | TextureUse.TransferDst;
        for (int i = 0; i < frames; i++)
        {
            _cells[i] = ctx.CreateTexture(new TextureDesc(InternalFormat.Rg32F, Cells, Cells, Use: use, Name: "lamp cells"));
            _index[i] = ctx.CreateTexture(new TextureDesc(InternalFormat.R32F, IndexWidth, IndexRows, Use: use, Name: "lamp index"));
            _data[i] = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba32F, DataWidth, MaxLamps, Use: use, Name: "lamp data"));
        }
        _nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        var g = ctx.Globals;
        g.PublishUniform("uLightGrid", () => _grid);
        g.Publish("uLightCells", () => Sampled(_cells[_slot]));
        g.Publish("uLightIndex", () => Sampled(_index[_slot]));
        g.Publish("uLightData", () => Sampled(_data[_slot]));
    }

    SampledTexture Sampled(GpuTexture t) => new(_nearest, t.View());

    /// <summary>The textures of this frame's slot, for a compute pass that binds them itself (the probe trace).</summary>
    internal (GpuTexture Cells, GpuTexture Index, GpuTexture Data) Current => (_cells[_slot], _index[_slot], _data[_slot]);
    internal GpuSampler NearestSampler => _nearest;
    /// <summary>This frame's <c>uLightGrid</c> (w 0: no lamps).</summary>
    internal Vector4 Grid => _grid;
    internal uint Emitter(string placementId) => _emitters.GetValueOrDefault(placementId);

    /// <summary>Shows the lamps of a building placed during play, replacing any it had (the host calls it when the building is complete).</summary>
    public void SetRuntime(string building, IEnumerable<WorldLight> lights)
    {
        WorldLight[] shown = [.. lights.Where(l => l.Intensity > 0 && l.Radius > 0)];
        if (_runtime.TryGetValue(building, out var old) && old.AsSpan().SequenceEqual(shown)) return;   // unchanged: no rebuild of the lamp list
        _runtime[building] = shown;
        _runtimeChanged = true;
    }

    /// <summary>
    /// The factor on a placed building's lamps (docs/formats/lights.md "When a light is on": the game's per-update power scale, the building's power share);
    /// at 0 or below they are hidden. 1 until set.
    /// </summary>
    public void SetRuntimePower(string building, float factor) => _runtimePower[building] = factor;

    public void RemoveRuntime(string building)
    {
        _runtimeChanged |= _runtime.Remove(building);
        _runtimePower.Remove(building);
    }

    public void ClearRuntime()
    {
        _runtimeChanged |= _runtime.Count > 0;
        _runtime.Clear();
        _runtimePower.Clear();
    }

    /// <summary>The power factor of lamp <paramref name="i"/>: 1 for the world's, the building's factor for a placed building's (its placement id is the building id).</summary>
    float PowerFactor(int i) => i < _worldCount || i >= _lamps!.Length ? 1 : _runtimePower.GetValueOrDefault(_lamps[i].PlacementId, 1);

    /// <summary>Whether a world lamp belongs to a building whose lamps <see cref="SetRuntime"/> replaced (a repaired town house): its key, or a prefix before a "/".</summary>
    bool Replaced(WorldLight l)
    {
        var id = l.PlacementId.AsSpan();
        var lookup = _runtime.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.ContainsKey(id)) return true;
        for (int i = 0; i < id.Length; i++)
            if (id[i] == '/' && lookup.ContainsKey(id[..i])) return true;
        return false;
    }

    // The lamps' indices by placement, rebuilt with the lamp list, and the daylight of the last update: for EmissiveDark.
    Dictionary<string, List<int>> _byPlacement = [];
    float _switchDaylight;

    /// <summary>
    /// Whether the EMISSIVE parts of placement <paramref name="placement"/> are dark now: it has lamps and none is switched on. The game's lights switch (Building slot 140,
    /// <c>FUN_14029aa10</c>) sets every emissive part's <c>brightness</c> to 1 while on and 0 while off, and a building with no lights keeps 1 (docs/formats/lights.md,
    /// "Emissive parts"). Night lamps are switched on when it is not daytime (daylight factor 0, slot 164), not at the light pass's 0.1.
    /// </summary>
    public bool EmissiveDark(string placement)
    {
        if (_lamps is null || !_byPlacement.TryGetValue(placement, out var list)) return false;
        foreach (int i in list)
        {
            var l = _lamps[i];
            if (WorldLights.IsLit(l, l.Interior && InteriorAllDay ? 0 : _switchDaylight > 0 ? 1 : 0) && PowerFactor(i) > 0) return false;
        }
        return true;
    }

    /// <summary>Lamp <paramref name="i"/> of this frame: the world's and placed buildings', then <see cref="Moving"/>.</summary>
    WorldLight Lamp(int i) => i < _lamps!.Length ? _lamps[i] : Moving[i - _lamps.Length];

    /// <summary>This frame's moving lamps (lanterns carried by characters), filled by the frame before <see cref="Update"/>.</summary>
    public List<WorldLight> Moving { get; } = [];

    /// <summary>
    /// Bins the lamps around <paramref name="eye"/> for this frame (the pulsing and shimmering ones at their power now). <paramref name="daylight"/> is the game's daylight
    /// factor (<see cref="WorldLights.DaylightFactor"/>): night-only lamps are left out above <see cref="WorldLights.NightDaylightLimit"/>
    /// (interior ones only without <see cref="InteriorAllDay"/>).
    /// </summary>
    public void Update(Vector3 eye, WorldObjectRenderer? objects = null, float daylight = 0)
    {
        _slot = _ctx.Frame.Slot;
        _switchDaylight = daylight;
        if (objects is not null) objects.EmissiveDark ??= EmissiveDark;
        _grid = default;
        InGrid = 0;
        _emitters.Clear();
        if (!Enabled) return;
        if (_world is null)
        {
            if (!_loading.IsCompleted) return;
            if (_loading.IsFaulted) Console.Error.WriteLine($"lamps     not loaded: {_loading.Exception?.InnerException?.Message}");
            _world = _loading.IsFaulted ? [] : _loading.Result;
            Console.WriteLine($"lamps     {_world.Length} lights ({_world.Count(l => l.Interior)} interior)");
            _runtimeChanged = true;
        }
        if (_runtimeChanged || _lamps is null)
        {
            WorldLight[] kept = _runtime.Count == 0 ? _world : [.. _world.Where(l => !Replaced(l))];
            _worldCount = kept.Length;
            _lamps = _runtime.Count == 0 ? _world : [.. kept, .. _runtime.Values.SelectMany(r => r)];
            _runtimeChanged = false;
            var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < _lamps.Length; i++)
            {
                if (!index.TryGetValue(_lamps[i].PlacementId, out var list)) index[_lamps[i].PlacementId] = list = [];
                list.Add(i);
            }
            _byPlacement = index;
        }
        int count = _lamps.Length + Moving.Count;
        if (count == 0) return;

        float x0 = MathF.Floor(eye.X / CellSize) * CellSize - Cells / 2 * CellSize, z0 = MathF.Floor(eye.Z / CellSize) * CellSize - Cells / 2 * CellSize;
        float x1 = x0 + Cells * CellSize, z1 = z0 + Cells * CellSize;
        _near.Clear();
        for (int i = 0; i < count && _near.Count < MaxLamps; i++)
        {
            var l = Lamp(i);
            if (l.Position.X + l.Radius < x0 || l.Position.X - l.Radius > x1 || l.Position.Z + l.Radius < z0 || l.Position.Z - l.Radius > z1) continue;
            if (!WorldLights.IsLit(l, l.Interior && InteriorAllDay ? 0 : daylight) || PowerFactor(i) <= 0) continue;
            var room = l.Interior ? objects?.InteriorFor(l.PlacementId) : null;
            // An unloaded or unrepresentable room must not turn its lights into unbounded outdoor lamps.
            if (l.Interior && (room is null || room.Footprint.Length is < 3 or > MaxRoomEdges)) continue;
            _near.Add((i, room));
            if (room is not null && !_emitters.ContainsKey(l.PlacementId)) _emitters.Add(l.PlacementId, (uint)_emitters.Count + 1);
        }
        if (_near.Count == 0) return;

        // The lamps' rows: position and radius; radiance (the game's power, its effect now) and type; spot direction, cos of the half cones, falloff.
        float t = (float)(_clock.Elapsed.TotalSeconds * 60);
        for (int k = 0; k < _near.Count; k++)
        {
            var (lamp, room) = _near[k];
            var l = Lamp(lamp);
            float power = l.Intensity * WorldLights.EffectFactor(l.Effect, t) * PowerFactor(lamp);
            float inner = MathF.Min(l.InnerAngle, l.OuterAngle), outer = MathF.Max(l.InnerAngle, l.OuterAngle);
            int row = k * DataWidth;
            _dataValues[row] = new Vector4(l.Position, l.Radius);
            _dataValues[row + 1] = new Vector4(l.Colour * power, l.Type == WorldLightType.Spot ? 1 : 0);
            _dataValues[row + 2] = new Vector4(l.Direction, MathF.Cos(outer * 0.5f));
            _dataValues[row + 3] = new Vector4(MathF.Cos(inner * 0.5f), l.Falloff > 0 ? l.Falloff : 1, room?.Footprint.Length ?? 0, Emitter(l.PlacementId));
            if (room is null) continue;
            _dataValues[row + 4] = new Vector4(room.YMin - RoomSurfaceMargin, room.YMax + RoomSurfaceMargin, 0, 0);
            for (int e = 0; e < room.Footprint.Length; e++)
            {
                var at = room.Footprint[e];
                var edge = room.Footprint[(e + 1) % room.Footprint.Length] - at;
                var inward = Vector2.Normalize(new Vector2(-edge.Y, edge.X));
                _dataValues[row + 5 + e] = new Vector4(inward, -Vector2.Dot(inward, at) + RoomSurfaceMargin, 0);
            }
        }

        // Each cell lists the lamps whose reach overlaps it (counted, then placed).
        Array.Clear(_counts);
        for (int pass = 0; pass < 2; pass++)
        {
            int total = 0;
            if (pass == 1)
            {
                for (int c = 0; c < _counts.Length; c++)
                {
                    _cellValues[c * 2] = total;
                    _cellValues[c * 2 + 1] = 0;
                    total += _counts[c];
                }
                if (total > IndexWidth * IndexRows) { _grid = default; return; }
            }
            for (int k = 0; k < _near.Count; k++)
            {
                var l = Lamp(_near[k].Lamp);
                int cx0 = Math.Max((int)MathF.Floor((l.Position.X - l.Radius - x0) / CellSize), 0), cx1 = Math.Min((int)MathF.Floor((l.Position.X + l.Radius - x0) / CellSize), Cells - 1);
                int cz0 = Math.Max((int)MathF.Floor((l.Position.Z - l.Radius - z0) / CellSize), 0), cz1 = Math.Min((int)MathF.Floor((l.Position.Z + l.Radius - z0) / CellSize), Cells - 1);
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int c = cz * Cells + cx;
                        if (pass == 0) { _counts[c]++; continue; }
                        int at = (int)_cellValues[c * 2] + (int)_cellValues[c * 2 + 1];
                        _indexValues[at] = k;
                        _cellValues[c * 2 + 1]++;
                    }
            }
        }

        var full = new PixelRect(0, 0, Cells, Cells);
        using (var batch = _ctx.Uploads.Begin())
        {
            batch.Write(_cells[_slot], 0, 0, full, MemoryMarshal.AsBytes(_cellValues.AsSpan()));
            batch.Write(_index[_slot], 0, 0, new PixelRect(0, 0, IndexWidth, IndexRows), MemoryMarshal.AsBytes(_indexValues.AsSpan()));
            batch.Write(_data[_slot], 0, 0, new PixelRect(0, 0, DataWidth, (uint)_near.Count), MemoryMarshal.AsBytes(_dataValues.AsSpan(0, _near.Count * DataWidth)));
        }
        _grid = new Vector4(x0, z0, CellSize, Cells);
        InGrid = _near.Count;
    }

    public string Describe() => _lamps is null ? "loading" : $"{_lamps.Length} lamps, {InGrid} around the eye" + (Enabled ? "" : ", off");

    public void Dispose()
    {
        foreach (var t in _cells.Concat(_index).Concat(_data)) t.Dispose();
    }
}