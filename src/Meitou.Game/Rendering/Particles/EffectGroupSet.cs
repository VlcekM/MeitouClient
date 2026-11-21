using System.Numerics;
using Meitou.Data.Particles;

namespace Meitou.Game.Rendering.Particles;

/// <summary>
/// The <see cref="ParticleRenderer"/>'s effect groups: the weather's (<see cref="WeatherGroups"/>), then the map's effect placers with the bursts and
/// the buildings' effects, and the new ones waiting for their start-up. The renderer waits for its simulation before it changes them.
/// </summary>
sealed class EffectGroupSet(ParticleLibrary library, EffectWorld world)
{
    /// <summary>
    /// The map's placers, the bursts and the buildings' effects, always on whatever the weather.
    /// </summary>
    readonly List<EffectGroup> _placerGroups = [];

    /// <summary>
    /// The groups made since the last <see cref="Step"/>: prewarmed by it before their first step.
    /// </summary>
    readonly List<EffectGroup> _toWarm = [];

    /// <summary>
    /// The burst groups by particle system (<see cref="BurstGroup"/>).
    /// </summary>
    readonly Dictionary<string, BurstEffectGroup> _bursts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The building effects' groups by key (<see cref="SetBuildingEffects"/>).
    /// </summary>
    readonly Dictionary<string, List<EffectGroup>> _buildingEffects = new(StringComparer.Ordinal);

    /// <summary>
    /// The EFFECT records read so far.
    /// </summary>
    readonly Dictionary<Data.GameRecord, EffectRecord> _effectRecords = [];

    /// <summary>
    /// The burst emitters' moves waiting for the next <see cref="ApplyBurstMoves"/>.
    /// </summary>
    readonly List<(EffectUnit Unit, Vector3 Position, Quaternion Orientation)> _burstMoves = [];

    /// <summary>
    /// Squared ground distance within which a neighbouring region starts its spatial weather groups (2 cells = 9216 units; dropped past 12000).
    /// </summary>
    const float RegionEnterRadiusSq = 9216f * 9216f;

    /// <summary>
    /// Shared warming thread for all per-region weather groups.
    /// </summary>
    readonly EffectWarmer _regionWarmer = new();

    /// <summary>
    /// Active nearby regions' spatial weather groups, keyed by region StringId.
    /// </summary>
    readonly Dictionary<string, RegionSlot> _regionWeathers = new(StringComparer.Ordinal);

    /// <summary>
    /// Flat list of all active and retiring weather groups (camera + nearby regions), rebuilt on <see cref="Advance"/>.
    /// </summary>
    readonly List<EffectGroup> _weatherGroups = [];

    /// <summary>
    /// Region ids seen in the latest <see cref="SetRegionWeathers"/> call.
    /// </summary>
    readonly HashSet<string> _seenRegions = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether at least one frame has stepped: regions discovered later warm in the background rather than stalling a frame.
    /// </summary>
    bool _hasStepped;

    /// <summary>
    /// One nearby region's world copy and spatial weather groups.
    /// </summary>
    sealed class RegionSlot(EffectWorld world, WeatherGroups groups)
    {
        /// <summary>
        /// The region's own world (same heights and density as the renderer's, with its own <see cref="EffectWorld.Area"/>).
        /// </summary>
        public EffectWorld World { get; } = world;
        /// <summary>
        /// The region's spatial weather groups.
        /// </summary>
        public WeatherGroups Groups { get; } = groups;
    }

    /// <summary>
    /// The weather's groups and their rebuilding.
    /// </summary>
    public WeatherGroups Weather { get; } = new(library, world);

    /// <summary>
    /// All active and retiring weather groups (camera + nearby regions).
    /// </summary>
    public IReadOnlyList<EffectGroup> WeatherGroups => _weatherGroups;

    /// <summary>
    /// The placers' groups (not the weather's).
    /// </summary>
    public IReadOnlyList<EffectGroup> Placers => _placerGroups;

    /// <summary>
    /// The weather's groups, then the placers': indexed so the per-frame walks allocate nothing (a Concat and the units' boxed enumerators did).
    /// </summary>
    public int Count => _weatherGroups.Count + _placerGroups.Count;

    /// <summary>
    /// The group at <paramref name="i"/> of <see cref="Count"/>.
    /// </summary>
    public EffectGroup At(int i) => i < _weatherGroups.Count ? _weatherGroups[i] : _placerGroups[i - _weatherGroups.Count];

    /// <summary>
    /// The keys with building effects (<see cref="SetBuildingEffects"/>) and their groups, one group per effect of a key.
    /// </summary>
    public (int Buildings, int Groups) BuildingEffectCount => (_buildingEffects.Count, _buildingEffects.Values.Sum(g => g.Count));

    /// <summary>
    /// The map's effect placers, replacing the previous list (the bursts and the building effects stay).
    /// </summary>
    public void SetPlacers(IReadOnlyList<MapEffectPlacement> placements)
    {
        _toWarm.RemoveAll(_placerGroups.Contains);
        _placerGroups.Clear();
        _placerGroups.AddRange(MapEffectPlacers.Groups(placements, library, world, Weather.Seed));
        _toWarm.AddRange(_placerGroups);
        _placerGroups.AddRange(_bursts.Values);
        foreach (var groups in _buildingEffects.Values) _placerGroups.AddRange(groups);
    }

    /// <summary>
    /// The EFFECT instances of a building under <paramref name="key"/>, replacing what the key had. Returns the new groups (null: none), whose
    /// textures the renderer starts decoding.
    /// </summary>
    public List<EffectGroup>? SetBuildingEffects(string key, IReadOnlyList<Data.World.PlacedEffect> effects)
    {
        RemoveBuildingEffects(key);
        if (effects.Count == 0) return null;

        var groups = new List<EffectGroup>();
        int seed = Weather.Seed + 7000 + (int)(Data.World.WorldObjectLayout.Seed(key) & 0xFFFFFF);
        foreach (var byEffect in effects.GroupBy(e => e.Effect))
        {
            if (!_effectRecords.TryGetValue(byEffect.Key, out var record)) _effectRecords[byEffect.Key] = record = EffectRecord.From(byEffect.Key);
            if (library.FindSystem(record.ParticleSystem) is not { } system) continue;

            groups.Add(new PlacerEffectGroup(record, system, byEffect.Select(e => (e.Position, e.Orientation)), seed + groups.Count, world));
        }
        if (groups.Count == 0) return null;

        _buildingEffects[key] = groups;
        _placerGroups.AddRange(groups);
        _toWarm.AddRange(groups);
        return groups;
    }

    /// <summary>
    /// Drops the building effects of <paramref name="key"/>.
    /// </summary>
    public void RemoveBuildingEffects(string key)
    {
        if (!_buildingEffects.Remove(key, out var old)) return;

        _placerGroups.RemoveAll(old.Contains);
        _toWarm.RemoveAll(old.Contains);
    }

    /// <summary>
    /// The burst group of a particle system, made the first time (null: an unknown system).
    /// </summary>
    public BurstEffectGroup? BurstGroup(string system)
    {
        if (_bursts.TryGetValue(system, out var group)) return group;
        if (library.FindSystem(system) is not { } def) return null;

        _bursts[system] = group = new BurstEffectGroup(def, Weather.Seed + 5000 + _bursts.Count, world);
        _placerGroups.Add(group);
        return group;
    }

    /// <summary>
    /// Moves a burst's emitter from the next <see cref="ApplyBurstMoves"/>.
    /// </summary>
    public void MoveBurst(EffectUnit unit, Vector3 position, Quaternion orientation) => _burstMoves.Add((unit, position, orientation));

    /// <summary>
    /// The burst emitters' moves since the last frame.
    /// </summary>
    public void ApplyBurstMoves()
    {
        foreach (var (unit, position, orientation) in _burstMoves) (unit.Position, unit.Orientation) = (position, orientation);
        _burstMoves.Clear();
    }

    /// <summary>
    /// Updates the nearby regions' spatial weather inputs, creating groups for regions entering range and retiring those that leave.
    /// </summary>
    public void SetRegionWeathers(IReadOnlyList<RegionWeatherInput> regions, double now)
    {
        _seenRegions.Clear();
        foreach (var r in regions)
        {
            _seenRegions.Add(r.RegionId);
            if (_regionWeathers.TryGetValue(r.RegionId, out var slot))
            {
                slot.World.Area = r.Area;
                slot.Groups.Want(r.Input, now);
            }
            else if (r.MinDistanceSq <= RegionEnterRadiusSq && r.Input.Effects.Count > 0)
            {
                var regionWorld = SyncWorld(new EffectWorld { Area = r.Area });
                var groups = new WeatherGroups(library, regionWorld, _regionWarmer)
                {
                    Seed = Weather.Seed + (r.RegionIndex + 1) * 1009,
                    PrewarmSeconds = Weather.PrewarmSeconds,
                    Background = Weather.Background,
                    SyncFirst = !_hasStepped,
                    FadeSeconds = Weather.FadeSeconds,
                };
                groups.Want(r.Input, now);
                _regionWeathers[r.RegionId] = new RegionSlot(regionWorld, groups);
            }
        }
        foreach (var (id, slot) in _regionWeathers)
            if (!_seenRegions.Contains(id))
                slot.Groups.Want(WeatherEffectInput.None, now);
    }

    /// <summary>
    /// Advances the camera and region weather groups, rebuilds the flat <see cref="WeatherGroups"/> list and collects any groups whose textures
    /// should be preloaded.
    /// </summary>
    public bool Advance(in EffectCamera camera, double now, List<EffectGroup> preloadTargets)
    {
        preloadTargets.Clear();
        bool changed = Weather.Advance(camera, now);
        if (Weather.Building) preloadTargets.AddRange(Weather.BuildingGroups);
        else if (changed) preloadTargets.AddRange(Weather.Groups);

        List<string>? toRemove = null;
        foreach (var (id, slot) in _regionWeathers)
        {
            SyncWorld(slot.World);
            slot.Groups.PrewarmSeconds = Weather.PrewarmSeconds;
            slot.Groups.Background = Weather.Background;
            slot.Groups.FadeSeconds = Weather.FadeSeconds;
            bool regionChanged = slot.Groups.Advance(camera, now);
            if (regionChanged) changed = true;
            if (slot.Groups.Building) preloadTargets.AddRange(slot.Groups.BuildingGroups);
            else if (regionChanged) preloadTargets.AddRange(slot.Groups.Groups);
            if (!_seenRegions.Contains(id) && !slot.Groups.HasActivity) (toRemove ??= []).Add(id);
        }
        if (toRemove is not null)
        {
            foreach (var id in toRemove)
            {
                _regionWeathers[id].Groups.Dispose();
                _regionWeathers.Remove(id);
            }
        }

        _weatherGroups.Clear();
        _weatherGroups.AddRange(Weather.Groups);
        _weatherGroups.AddRange(Weather.RetiringGroups);
        foreach (var slot in _regionWeathers.Values)
        {
            _weatherGroups.AddRange(slot.Groups.Groups);
            _weatherGroups.AddRange(slot.Groups.RetiringGroups);
        }
        return changed;
    }

    /// <summary>
    /// Copies the renderer's shared world settings (heights, density, active radius) onto a region's <see cref="EffectWorld"/>.
    /// </summary>
    EffectWorld SyncWorld(EffectWorld target)
    {
        target.GroundHeight = world.GroundHeight;
        target.FreezeGroundHeight = world.FreezeGroundHeight;
        target.ActiveRadius = world.ActiveRadius;
        target.FallbackRadius = world.FallbackRadius;
        target.GroundColour = world.GroundColour;
        target.Density = world.Density;
        return target;
    }

    /// <summary>
    /// One frame of the groups: the new ones prewarmed (<see cref="WeatherGroups.PrewarmSeconds"/>, else each system's longest particle life capped
    /// at <see cref="WeatherGroups.MaxAutoPrewarm"/>), then every group stepped by <paramref name="dt"/>.
    /// </summary>
    public void Step(float dt, EffectCamera view)
    {
        _hasStepped = true;
        var input = Weather.Input;
        if (_toWarm.Count > 0)
        {
            foreach (var g in _toWarm.ToArray())
                g.Prewarm(Weather.PrewarmSeconds ?? Math.Min(ParticleSimulation.LongestLife(g.System), Meitou.Data.Particles.WeatherGroups.MaxAutoPrewarm), view, input);
            _toWarm.Clear();
        }
        Weather.Step(dt, view);
        foreach (var slot in _regionWeathers.Values) slot.Groups.Step(dt, view);
        foreach (var g in _placerGroups) g.Update(dt, view, input);
    }

    /// <summary>
    /// Disposes all weather groups and the shared region warmer.
    /// </summary>
    public void Dispose()
    {
        Weather.Dispose();
        foreach (var slot in _regionWeathers.Values) slot.Groups.Dispose();
        _regionWeathers.Clear();
        _regionWarmer.Dispose();
    }
}
