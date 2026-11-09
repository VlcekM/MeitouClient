using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// The heights the original gives one zone's level buildings when it makes them (docs/formats/zones.md "Placement height"): one with no parent stands on the
/// building ground plus its height above it; one inside or mounted on another (its <c>isInsideBuilding</c> handle, else its <c>mounted</c> one) on that
/// building's height plus that. The height above is a nonzero <c>player furniture Y offset</c>, else the instance Y. The level state's <c>world Y pos</c>
/// plays no part. A child whose parent is not in the zone is left out.
/// </summary>
sealed class PlacementHeights(ZoneCoordinate zone, GameDatabase db, GameRecord list, Func<double, double, float> ground,
    Dictionary<string, float> heights)
{
    readonly List<(string Key, GameInstance Instance, GameRecord? State)> _placed = [];
    readonly Dictionary<(int C, int I), int> _byHandle = [];
    readonly HashSet<int> _visiting = [];

    public void Resolve()
    {
        Collect();
        for (int i = 0; i < _placed.Count; i++)
            if (ParentOf(_placed[i].State) is null) StandOnGround(_placed[i]);
        for (int i = 0; i < _placed.Count; i++) HeightOf(i);
    }

    void Collect()
    {
        foreach (var (id, instance) in list.Instances)
        {
            if (instance.IsCleared || instance.Position == Vector3.Zero) continue;
            var state = instance.States.Select(db.Find).FirstOrDefault(s => s?.Type == FcsRecordType.GamestateBuilding);
            if (state is not null && state.Ints.TryGetValue("handleI", out int handle))
                _byHandle.TryAdd((state.GetInt("handleC"), handle), _placed.Count);
            _placed.Add((BuildingKey.Of(zone, id), instance, state));
        }
    }

    void StandOnGround((string Key, GameInstance Instance, GameRecord? State) p) =>
        heights[p.Key] = ground(p.Instance.Position.X, p.Instance.Position.Z) + Above(p.Instance, p.State);

    /// <summary>
    /// Its height above the ground or the parent: a nonzero <c>player furniture Y offset</c>, else the instance Y.
    /// </summary>
    static float Above(GameInstance instance, GameRecord? state) =>
        state?.Floats.GetValueOrDefault("player furniture Y offset") is { } offset && offset != 0 ? offset : instance.Position.Y;

    /// <summary>
    /// The height of placement <paramref name="i"/>, resolving its parents first; null when a parent is missing or the chain loops.
    /// </summary>
    float? HeightOf(int i)
    {
        var (key, instance, state) = _placed[i];
        if (heights.TryGetValue(key, out float known)) return known;
        if (!_visiting.Add(i)) return null;
        float? parentY = ParentOf(state) is { } h && _byHandle.TryGetValue(h, out int parent) ? HeightOf(parent) : null;
        _visiting.Remove(i);
        if (parentY is not { } y) return null;
        return heights[key] = y + Above(instance, state);
    }

    /// <summary>
    /// The handle of the building it is inside of, else the one it is mounted on (C and I); null when both are null or missing.
    /// </summary>
    static (int C, int I)? ParentOf(GameRecord? state)
    {
        if (state is null) return null;
        foreach (var name in (ReadOnlySpan<string>)["isInsideBuilding", "mounted"])
            if (state.Ints.TryGetValue(name + "TYPE", out int type) && type != (int)FcsRecordType.NullItem)
                return (state.GetInt(name + "C"), state.GetInt(name + "I"));
        return null;
    }
}