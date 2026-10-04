using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// Which town a placed building belongs to, for its material (docs/formats/zones.md, "From placements to meshes"): the town
/// its GAMESTATE_BUILDING's <c>town*</c> handle names (matched to a GAMESTATE_TOWN's <c>hand*</c> by the C and CS parts),
/// else the nearest town by X/Z, ignoring TOWN_NEST_MARKER towns (the game's lookup by position).
/// </summary>
public sealed class BuildingTowns
{
    /// <summary>TownType.TOWN_NEST_MARKER, skipped by the game's nearest-town lookup.</summary>
    public const int NestMarkerType = 8;

    readonly GameDatabase db;
    readonly Dictionary<(int C, int CS), TownPlacement> byHandle = [];
    readonly List<(TownPlacement Town, Vector2 At)> towns = [];
    readonly Dictionary<string, GameRecord?> materials = new(StringComparer.Ordinal);

    public BuildingTowns(GameDatabase db, WorldLevelData world)
    {
        this.db = db;
        var placements = new Dictionary<string, TownPlacement>(StringComparer.Ordinal);
        foreach (var t in world.Towns())
        {
            placements[t.InstanceId] = t;
            if (db.Find(t.TownId) is { } record && record.GetInt("type") != NestMarkerType)
                towns.Add((t, new Vector2(t.Position.X, t.Position.Z)));
        }
        foreach (var state in world.Level.OfType(FcsRecordType.GAMESTATE_TOWN))
            if (state.Ints.ContainsKey("handC") && placements.TryGetValue(state.GetString("instance"), out var t))
                byHandle[(state.GetInt("handC"), state.GetInt("handCS"))] = t;
    }

    /// <summary>The town of a building with state <paramref name="state"/> (or none) at <paramref name="position"/>, or null when there are no towns.</summary>
    public TownPlacement? TownOf(GameRecord? state, Vector3 position) => TownByHandle(state) ?? NearestTown(position);

    /// <summary>The town a GAMESTATE_BUILDING's <c>town*</c> handle names, or null when it has none or no town state matches.</summary>
    public TownPlacement? TownByHandle(GameRecord? state) =>
        state is not null && state.GetInt("townTYPE") == (int)FcsRecordType.TOWN && state.Ints.ContainsKey("townC") &&
        byHandle.TryGetValue((state.GetInt("townC"), state.GetInt("townCS")), out var town) ? town : null;

    /// <summary>The nearest town by X/Z that is not a nest marker.</summary>
    public TownPlacement? NearestTown(Vector3 position)
    {
        var at = new Vector2(position.X, position.Z);
        TownPlacement? best = null;
        float bestDistance = float.MaxValue;
        foreach (var (t, p) in towns)
        {
            float d = Vector2.DistanceSquared(p, at);
            if (d < bestDistance) { bestDistance = d; best = t; }
        }
        return best;
    }

    /// <summary>The material of a town (cached per town placement), see <see cref="WorldObjectLayout.TownMaterial"/>.</summary>
    public GameRecord? MaterialOf(TownPlacement? town)
    {
        if (town is null) return null;
        if (!materials.TryGetValue(town.InstanceId, out var m))
            materials[town.InstanceId] = m = db.Find(town.TownId) is { } record ? WorldObjectLayout.TownMaterial(db, record, town.InstanceId) : null;
        return m;
    }
}
