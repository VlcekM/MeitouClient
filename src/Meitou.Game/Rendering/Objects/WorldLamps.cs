using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The game's point and spot lights on the world (docs/render-lights.md): the lamps of every placed building (<see cref="WorldLights"/>, loaded on a
/// worker), binned each frame into a grid of <see cref="Cells"/>² cells of <see cref="CellSize"/> around the eye and uploaded as three textures,
/// which <c>kenshiLight</c> (<see cref="LampShaders"/>) and the probe trace read. Interior lamps are clipped to their building's mask; the probe
/// trace also shadows their bounce contribution. Buildings placed during play add theirs with <see cref="SetRuntime"/>. Render thread only.
/// </summary>
internal sealed class WorldLamps : IDisposable
{
    #region Setup

    /// <summary>
    /// The grid's size in cells per side, the index texture's width and rows (lamp numbers, every cell's list back to back), and the most lamps one
    /// frame bins.
    /// </summary>
    public const int Cells = 64, IndexWidth = 1024, IndexRows = 16, MaxLamps = 1024;

    /// <summary>
    /// A grid cell's side (world units).
    /// </summary>
    public const float CellSize = 128;

    /// <summary>
    /// The most footprint edges an interior lamp's room may have, and the texels of a lamp's row in the data texture (six, then the edges).
    /// </summary>
    internal const int MaxRoomEdges = 32, DataWidth = 6 + MaxRoomEdges;

    /// <summary>
    /// How far a room's clip planes and height band are widened: the small offset between collision masks and the visible wall and floor surfaces;
    /// and the margin while the lamps are traced (their shadows stop the light instead).
    /// </summary>
    internal const float RoomSurfaceMargin = 0.5f, TracedRoomMargin = 100000f;

    /// <summary>
    /// How far past an interior lamp's room hull its light still reaches (set each frame): <see cref="RoomSurfaceMargin"/>, or
    /// <see cref="TracedRoomMargin"/> when the lamps are traced.
    /// </summary>
    public float RoomMargin { get; set; } = RoomSurfaceMargin;

    /// <summary>
    /// The context the textures are made and uploaded with.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// The world's lamps loading on a worker (those with power and reach).
    /// </summary>
    readonly Task<WorldLight[]> _loading;

    /// <summary>
    /// The lamps binned each frame: the world's (once loaded), and the list with the placed buildings' (rebuilt when they change).
    /// </summary>
    WorldLight[]? _world, _lamps;

    /// <summary>
    /// The lamps of buildings placed during play, by building id.
    /// </summary>
    readonly Dictionary<string, WorldLight[]> _runtime = new(StringComparer.Ordinal);

    /// <summary>
    /// The power factor of each placed building's lamps, by building id (1 when not set).
    /// </summary>
    readonly Dictionary<string, float> _runtimePower = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="_runtime"/> changed: the lamp list is rebuilt at the next update.
    /// </summary>
    bool _runtimeChanged;

    /// <summary>
    /// How many of the world's lamps start the lamp list (those of no replaced building); the placed buildings' follow.
    /// </summary>
    int _worldCount;

    /// <summary>
    /// The cell, index and lamp-row textures, one of each per frame in flight.
    /// </summary>
    readonly GpuTexture[] _cells, _index, _data;

    /// <summary>
    /// A one-texel stand-in for the lamp tiles while there are none.
    /// </summary>
    readonly GpuTexture _noTiles;

    /// <summary>
    /// The lamps' visibility cache while the lamps are shadowed; null: none (each lamp's tile reads as none).
    /// </summary>
    internal Gi.LampTiles? Tiles { get; set; }

    /// <summary>
    /// The nearest-filtering, edge-clamped sampler the shaders read the textures with.
    /// </summary>
    readonly GpuSampler _nearest;

    /// <summary>
    /// This frame's cells: each one's first entry in the index and its lamp count.
    /// </summary>
    readonly float[] _cellValues = new float[Cells * Cells * 2];

    /// <summary>
    /// This frame's index: the lamp numbers of every cell's list back to back.
    /// </summary>
    readonly float[] _indexValues = new float[IndexWidth * IndexRows];

    /// <summary>
    /// This frame's lamp rows (<see cref="DataWidth"/> texels each).
    /// </summary>
    readonly Vector4[] _dataValues = new Vector4[MaxLamps * DataWidth];

    /// <summary>
    /// This frame's binned lamps and the rooms of the interior ones.
    /// </summary>
    readonly List<(int Lamp, InteriorVolume? Room)> _near = [];

    /// <summary>
    /// This frame's emitter ids by building (1 on, one per interior building with a binned lamp).
    /// </summary>
    readonly Dictionary<string, uint> _emitters = new(StringComparer.Ordinal);

    /// <summary>
    /// The lamps per cell while binning (the first pass counts them).
    /// </summary>
    readonly int[] _counts = new int[Cells * Cells];

    /// <summary>
    /// The frame-in-flight slot of this frame's textures.
    /// </summary>
    int _slot;

    /// <summary>
    /// This frame's grid (x0, z0, cell size, cells per side; 0: no lamps) as <c>uLightGrid</c>.
    /// </summary>
    Vector4 _grid;

    /// <summary>
    /// The clock pulsing and shimmering lamps take their time from (60 steps a second).
    /// </summary>
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Whether the lamps light the world (the bench's <c>lights</c> switch).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The <c>interiorlamps</c> switch's Meitou side: night-only lamps of interior layouts shine day and night (the game: night only).
    /// </summary>
    public bool InteriorAllDay { get; set; } = true;

    /// <summary>
    /// The lamps of the world, once loaded; and those in this frame's grid.
    /// </summary>
    public int Count => _lamps?.Length ?? 0;

    /// <summary>
    /// The lamps binned in this frame's grid.
    /// </summary>
    public int InGrid { get; private set; }

    /// <summary>
    /// Debug (the console's <c>lamps tiles</c>): the cached tiles, or the per-pixel rays when off.
    /// </summary>
    public bool TilesEnabled { get => Gi.LampTiles.Enabled; set => Gi.LampTiles.Enabled = value; }

    /// <summary>
    /// Debug: the cache's state.
    /// </summary>
    public string TilesDescription => Tiles?.Describe() ?? "none";

    /// <summary>
    /// Debug (the console's <c>lamps carry</c>): a lamp treated as a carried one, wherever the console last put it.
    /// </summary>
    public WorldLight? DebugCarried { get; set; }

    /// <summary>
    /// Debug (the console's <c>lamps refview</c>): the lamp whose visibility is shown over the scene, as its tile gives it or as the truth of many
    /// rays.
    /// </summary>
    public (int Lamp, int Truth)? DebugReference { get; set; }

    /// <summary>
    /// How far from the eye the lamps can hold a tile: the traced scene's range.
    /// </summary>
    public float TileReach { get; set; } = float.MaxValue;

    /// <summary>
    /// Debug (the console's <c>lamps</c>): the lamps left out of the grid, by index.
    /// </summary>
    public HashSet<int> DebugOff { get; } = [];

    /// <summary>
    /// The row of this frame's data that holds lamp <paramref name="lamp"/>, if any.
    /// </summary>
    internal int? RowOf(int lamp)
    {
        for (int k = 0; k < InGrid; k++)
            if (_near[k].Lamp == lamp) return k;
        return null;
    }

    /// <summary>
    /// Debug: the lamps whose reach touches <paramref name="at"/>, within <paramref name="radius"/> more, with their indices.
    /// </summary>
    public IEnumerable<(int Index, WorldLight Light)> Around(Vector3 at, float radius)
    {
        int count = (_lamps?.Length ?? 0) + Moving.Count;
        for (int i = 0; i < count; i++)
        {
            var l = Lamp(i);
            if (new Vector2(l.Position.X - at.X, l.Position.Z - at.Z).Length() <= l.Radius + radius) yield return (i, l);
        }
    }

    /// <summary>
    /// Starts loading the world's lamps on a worker, makes the per-frame textures and publishes them and the grid as the shaders' globals.
    /// </summary>
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
        _nearest = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));
        var g = ctx.Globals;
        g.PublishUniform("uLightGrid", () => _grid);
        g.Publish("uLightCells", () => Sampled(_cells[_slot]));
        g.Publish("uLightIndex", () => Sampled(_index[_slot]));
        g.Publish("uLightData", () => Sampled(_data[_slot]));

        _noTiles = _ctx.CreateTexture(new TextureDesc(InternalFormat.R16, 1, 1, Use: use, Name: "lamp tiles none"));
        using (var batch = _ctx.Uploads.Begin())
        {
            batch.Write(_noTiles, 0, 0, new PixelRect(0, 0, 1, 1), stackalloc byte[2]);
        }
        g.Publish("uLampTiles", () => Sampled(Tiles?.Atlas ?? _noTiles));
    }

    /// <summary>
    /// A texture with the nearest sampler.
    /// </summary>
    SampledTexture Sampled(GpuTexture t) => new(_nearest, t.View());

    /// <summary>
    /// The textures of this frame's slot, for a compute pass that binds them itself (the probe trace).
    /// </summary>
    internal (GpuTexture Cells, GpuTexture Index, GpuTexture Data) Current => (_cells[_slot], _index[_slot], _data[_slot]);

    /// <summary>
    /// The sampler for <see cref="Current"/>'s textures.
    /// </summary>
    internal GpuSampler NearestSampler => _nearest;

    /// <summary>
    /// The lamp in row <paramref name="k"/> of this frame's data (<see cref="InGrid"/> rows).
    /// </summary>
    internal WorldLight RowLamp(int k) => Lamp(_near[k].Lamp);

    /// <summary>
    /// The lamps' tiles, for a compute pass that binds them itself.
    /// </summary>
    internal GpuTexture TilesTexture => Tiles?.Atlas ?? _noTiles;

    /// <summary>
    /// This frame's <c>uLightGrid</c> (w 0: no lamps).
    /// </summary>
    internal Vector4 Grid => _grid;

    /// <summary>
    /// The emitter id of an interior building's lamps this frame (0: none binned).
    /// </summary>
    internal uint Emitter(string placementId) => _emitters.GetValueOrDefault(placementId);

    #endregion

    #region Runtime buildings

    /// <summary>
    /// Shows the lamps of a building placed during play, replacing any it had (the host calls it when the building is complete).
    /// </summary>
    public void SetRuntime(string building, IEnumerable<WorldLight> lights)
    {
        WorldLight[] shown = [.. lights.Where(l => l.Intensity > 0 && l.Radius > 0)];
        if (_runtime.TryGetValue(building, out var old) && old.AsSpan().SequenceEqual(shown)) return;   // unchanged: no rebuild of the lamp list
        _runtime[building] = shown;
        _runtimeChanged = true;
    }

    /// <summary>
    /// The factor on a placed building's lamps (docs/formats/lights.md "When a light is on": the game's per-update power scale, the building's power
    /// share); at 0 or below they are hidden. 1 until set.
    /// </summary>
    public void SetRuntimePower(string building, float factor) => _runtimePower[building] = factor;

    /// <summary>
    /// Hides the lamps of a building placed during play (it was removed).
    /// </summary>
    public void RemoveRuntime(string building)
    {
        _runtimeChanged |= _runtime.Remove(building);
        _runtimePower.Remove(building);
    }

    /// <summary>
    /// Hides every placed building's lamps (a loaded save replaces the world).
    /// </summary>
    public void ClearRuntime()
    {
        _runtimeChanged |= _runtime.Count > 0;
        _runtime.Clear();
        _runtimePower.Clear();
    }

    /// <summary>
    /// The power factor of lamp <paramref name="i"/>: 1 for the world's, the building's factor for a placed building's (its placement id is the
    /// building id).
    /// </summary>
    float PowerFactor(int i) => i < _worldCount || i >= _lamps!.Length ? 1 : _runtimePower.GetValueOrDefault(_lamps[i].PlacementId, 1);

    /// <summary>
    /// Whether a world lamp belongs to a building whose lamps <see cref="SetRuntime"/> replaced (a repaired town house): its key, or a prefix before
    /// a "/".
    /// </summary>
    bool Replaced(WorldLight l)
    {
        var id = l.PlacementId.AsSpan();
        var lookup = _runtime.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.ContainsKey(id)) return true;
        for (int i = 0; i < id.Length; i++)
            if (id[i] == '/' && lookup.ContainsKey(id[..i])) return true;
        return false;
    }

    #endregion

    #region Emissive parts

    /// <summary>
    /// The lamps' indices by placement, rebuilt with the lamp list, for <see cref="EmissiveDark"/>.
    /// </summary>
    Dictionary<string, List<int>> _byPlacement = [];

    /// <summary>
    /// The daylight factor of the last update, for <see cref="EmissiveDark"/>.
    /// </summary>
    float _switchDaylight;

    /// <summary>
    /// Whether the EMISSIVE parts of placement <paramref name="placement"/> are dark now: it has lamps and none is switched on. The game's lights
    /// switch (Building slot 140,
    /// <c>FUN_14029aa10</c>) sets every emissive part's <c>brightness</c> to 1 while on and 0 while off, and a building with no lights keeps 1
    /// (docs/formats/lights.md, "Emissive parts"). Night lamps are switched on when it is not daytime (daylight factor 0, slot 164), not at the light
    /// pass's 0.1.
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

    #endregion

    #region Lamp list

    /// <summary>
    /// Lamp <paramref name="i"/> of this frame: the world's and placed buildings', then <see cref="Moving"/>.
    /// </summary>
    WorldLight Lamp(int i) => i < _lamps!.Length ? _lamps[i] : Moving[i - _lamps.Length];

    /// <summary>
    /// This frame's moving lamps (lanterns carried by characters), filled by the frame before <see cref="Update"/>.
    /// </summary>
    public List<WorldLight> Moving { get; } = [];

    /// <summary>
    /// Takes the world's lamps off the worker once it has finished (null until then; a faulted load leaves none).
    /// </summary>
    WorldLight[]? LoadWorld()
    {
        if (!_loading.IsCompleted) return null;
        if (_loading.IsFaulted) Console.Error.WriteLine($"lamps     not loaded: {_loading.Exception?.InnerException?.Message}");
        _world = _loading.IsFaulted ? [] : _loading.Result;
        Console.WriteLine($"lamps     {_world.Length} lights ({_world.Count(l => l.Interior)} interior)");
        _runtimeChanged = true;
        return _world;
    }

    /// <summary>
    /// Rebuilds the lamp list (the world's lamps that no placed building replaced, then the placed buildings') and its placement index.
    /// </summary>
    WorldLight[] RebuildLamps(WorldLight[] world)
    {
        WorldLight[] kept = _runtime.Count == 0 ? world : [.. world.Where(l => !Replaced(l))];
        _worldCount = kept.Length;
        _lamps = _runtime.Count == 0 ? world : [.. kept, .. _runtime.Values.SelectMany(r => r)];
        _runtimeChanged = false;
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < _lamps.Length; i++)
        {
            if (!index.TryGetValue(_lamps[i].PlacementId, out var list)) index[_lamps[i].PlacementId] = list = [];
            list.Add(i);
        }

        _byPlacement = index;
        return _lamps;
    }

    #endregion

    #region Binning

    /// <summary>
    /// Bins the lamps around <paramref name="eye"/> for this frame (the pulsing and shimmering ones at their power now). <paramref name="daylight"/>
    /// is the game's daylight factor (<see cref="WorldLights.DaylightFactor"/>): night-only lamps are left out above
    /// <see cref="WorldLights.NightDaylightLimit"/> (interior ones only without <see cref="InteriorAllDay"/>).
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

        WorldLight[]? world = _world ?? LoadWorld();
        if (world is null) return;

        WorldLight[] lamps = _runtimeChanged || _lamps is null ? RebuildLamps(world) : _lamps;
        int count = lamps.Length + Moving.Count;
        if (count == 0) return;

        float x0 = MathF.Floor(eye.X / CellSize) * CellSize - Cells / 2 * CellSize;
        float z0 = MathF.Floor(eye.Z / CellSize) * CellSize - Cells / 2 * CellSize;
        CollectNear(x0, z0, count, objects, daylight);
        if (_near.Count == 0) return;

        FillLampRows();
        if (!BinIntoCells(x0, z0))
        {
            _grid = default;
            return;
        }

        UploadGrid();
        _grid = new Vector4(x0, z0, CellSize, Cells);
        InGrid = _near.Count;
    }

    /// <summary>
    /// The lamps that reach the grid of <paramref name="count"/> lamps around the eye (from <paramref name="x0"/>, <paramref name="z0"/>) and are lit
    /// now; an interior lamp is kept only with its room, and each interior building gets an emitter id.
    /// </summary>
    void CollectNear(float x0, float z0, int count, WorldObjectRenderer? objects, float daylight)
    {
        float x1 = x0 + Cells * CellSize, z1 = z0 + Cells * CellSize;
        _near.Clear();
        for (int i = 0; i < count && _near.Count < MaxLamps; i++)
        {
            if (DebugOff.Contains(i)) continue;
            var l = Lamp(i);
            if (l.Position.X + l.Radius < x0 || l.Position.X - l.Radius > x1
                || l.Position.Z + l.Radius < z0 || l.Position.Z - l.Radius > z1) continue;
            if (!WorldLights.IsLit(l, l.Interior && InteriorAllDay ? 0 : daylight) || PowerFactor(i) <= 0) continue;
            var room = l.Interior ? objects?.InteriorFor(l.PlacementId) : null;

            // An unloaded or unrepresentable room must not turn its lights into unbounded outdoor lamps.
            if (l.Interior && (room is null || room.Footprint.Length is < 3 or > MaxRoomEdges)) continue;
            _near.Add((i, room));
            if (room is not null && !_emitters.ContainsKey(l.PlacementId)) _emitters.Add(l.PlacementId, (uint)_emitters.Count + 1);
        }
    }

    /// <summary>
    /// Writes the rows of the lamps in <see cref="_near"/>: position and radius; radiance and type; spot direction and cones; an interior lamp's room.
    /// </summary>
    void FillLampRows()
    {
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
            float falloff = l.Falloff > 0 ? l.Falloff : 1;
            _dataValues[row + 3] = new Vector4(MathF.Cos(inner * 0.5f), falloff, room?.Footprint.Length ?? 0, Emitter(l.PlacementId));

            // The lamp's place in the cache: a carried lamp's is traced whole each frame.
            var tile = Tiles is not { } shadows
                ? default
                : lamp < _lamps!.Length
                    ? shadows.Place(l, TileReach)
                    : shadows.PlaceMoving(l);
            _dataValues[row + 4] = new Vector4(room is null ? 0 : room.YMin - RoomMargin, room is null ? 0 : room.YMax + RoomMargin, tile.X, tile.Y);
            _dataValues[row + 5] = new Vector4(Gi.LampTiles.SourceRadius(l), 0, 0, 0);
            if (room is not null) FillRoomEdges(row, room);
        }
    }

    /// <summary>
    /// The height band of an interior lamp's room and the inward normal and offset of each footprint edge, from row <paramref name="row"/> on.
    /// </summary>
    void FillRoomEdges(int row, InteriorVolume room)
    {
        for (int e = 0; e < room.Footprint.Length; e++)
        {
            var at = room.Footprint[e];
            var edge = room.Footprint[(e + 1) % room.Footprint.Length] - at;
            var inward = Vector2.Normalize(new Vector2(-edge.Y, edge.X));
            _dataValues[row + 6 + e] = new Vector4(inward, -Vector2.Dot(inward, at) + RoomMargin, 0);
        }
    }

    /// <summary>
    /// Lists each grid cell's lamps into the index: counted, then placed. False when they do not fit the index.
    /// </summary>
    bool BinIntoCells(float x0, float z0)
    {
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

                if (total > IndexWidth * IndexRows) return false;
            }

            for (int k = 0; k < _near.Count; k++)
            {
                var l = Lamp(_near[k].Lamp);
                int cx0 = Math.Max((int)MathF.Floor((l.Position.X - l.Radius - x0) / CellSize), 0);
                int cx1 = Math.Min((int)MathF.Floor((l.Position.X + l.Radius - x0) / CellSize), Cells - 1);
                int cz0 = Math.Max((int)MathF.Floor((l.Position.Z - l.Radius - z0) / CellSize), 0);
                int cz1 = Math.Min((int)MathF.Floor((l.Position.Z + l.Radius - z0) / CellSize), Cells - 1);
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        int c = cz * Cells + cx;
                        if (pass == 0)
                        {
                            _counts[c]++;
                            continue;
                        }

                        int at = (int)_cellValues[c * 2] + (int)_cellValues[c * 2 + 1];
                        _indexValues[at] = k;
                        _cellValues[c * 2 + 1]++;
                    }
            }
        }

        return true;
    }

    /// <summary>
    /// Uploads this frame's cell, index and lamp-row textures.
    /// </summary>
    void UploadGrid()
    {
        var full = new PixelRect(0, 0, Cells, Cells);
        using (var batch = _ctx.Uploads.Begin())
        {
            batch.Write(_cells[_slot], 0, 0, full, MemoryMarshal.AsBytes(_cellValues.AsSpan()));
            batch.Write(_index[_slot], 0, 0, new PixelRect(0, 0, IndexWidth, IndexRows), MemoryMarshal.AsBytes(_indexValues.AsSpan()));
            var rows = new PixelRect(0, 0, DataWidth, (uint)_near.Count);
            batch.Write(_data[_slot], 0, 0, rows, MemoryMarshal.AsBytes(_dataValues.AsSpan(0, _near.Count * DataWidth)));

        }
    }

    #endregion

    /// <summary>
    /// A line for the log: the lamps and those around the eye.
    /// </summary>
    public string Describe() => _lamps is null ? "loading" : $"{_lamps.Length} lamps, {InGrid} around the eye" + (Enabled ? "" : ", off");

    /// <summary>
    /// Frees the per-frame textures.
    /// </summary>
    public void Dispose()
    {
        foreach (var t in _cells.Concat(_index).Concat(_data)) t.Dispose();
        _noTiles.Dispose();
    }
}