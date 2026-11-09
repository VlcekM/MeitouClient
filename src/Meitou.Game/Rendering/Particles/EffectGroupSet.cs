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
    /// The weather's groups and their rebuilding.
    /// </summary>
    public WeatherGroups Weather { get; } = new(library, world);

    /// <summary>
    /// The placers' groups (not the weather's).
    /// </summary>
    public IReadOnlyList<EffectGroup> Placers => _placerGroups;

    /// <summary>
    /// The weather's groups, then the placers': indexed so the per-frame walks allocate nothing (a Concat and the units' boxed enumerators did).
    /// </summary>
    public int Count => Weather.Groups.Count + _placerGroups.Count;

    /// <summary>
    /// The group at <paramref name="i"/> of <see cref="Count"/>.
    /// </summary>
    public EffectGroup At(int i) => i < Weather.Groups.Count ? Weather.Groups[i] : _placerGroups[i - Weather.Groups.Count];

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
    /// One frame of the groups: the new ones prewarmed (<see cref="WeatherGroups.PrewarmSeconds"/>, else each system's longest particle life capped
    /// at <see cref="WeatherGroups.MaxAutoPrewarm"/>), then every group stepped by <paramref name="dt"/>.
    /// </summary>
    public void Step(float dt, EffectCamera view)
    {
        var input = Weather.Input;
        if (_toWarm.Count > 0)
        {
            foreach (var g in _toWarm.ToArray())
                g.Prewarm(Weather.PrewarmSeconds ?? Math.Min(ParticleSimulation.LongestLife(g.System), WeatherGroups.MaxAutoPrewarm), view, input);
            _toWarm.Clear();
        }
        foreach (var g in Weather.Groups) g.Update(dt, view, input);
        foreach (var g in _placerGroups) g.Update(dt, view, input);
    }
}
