using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The town houses the player bought (<see cref="WorldSnapshot.EmptiedHouses"/>, docs/simulation.md "Town house purchases"): the purchase destroyed their furniture, so
/// each replaces its level placement in the renderer's runtime placements, the navmesh overlay and the lamps, by its key, as standing and with
/// <see cref="BuildingPlacement.WithoutInterior"/> set. A bought ruin is a construction site instead, whose own placement says it.
/// </summary>
sealed partial class GameHost
{
    long emptiedRevision = -1;
    readonly HashSet<string> shownEmptied = new(StringComparer.Ordinal);

    /// <summary>Once a frame, with the ruins.</summary>
    void UpdateEmptiedHouses(WorldSnapshot snapshot)
    {
        if (snapshot.EmptiedHousesRevision == emptiedRevision) return;
        emptiedRevision = snapshot.EmptiedHousesRevision;
        var current = new Dictionary<string, TownBuilding>(StringComparer.Ordinal);
        foreach (var b in snapshot.EmptiedHouses) current[b.Key] = b;
        foreach (string key in shownEmptied.Where(k => !current.ContainsKey(k)).ToArray()) HideEmptied(key);
        foreach (var b in current.Values)
            if (!shownEmptied.Contains(b.Key)) ShowEmptied(b);
    }

    void ShowEmptied(TownBuilding building)
    {
        if (building.Resolved is not { } resolved) return;
        var placement = resolved.Placement with { DestroyedOverride = false, WithoutInterior = true };
        ActiveObjects?.AddRuntime(placement);
        if (nav is not null && BuildData().TryGetValue(resolved.Record.StringId, out var data) && data.PathMode != 0) nav.AddRuntimeBuilding(placement);
        if (ActiveLamps is { } lamps && scene.Database is { } db && scene.Objects is { } objects)
            lamps.SetRuntime(building.Key, WorldLights.ForBuilding(db, objects.Levels, resolved with { Placement = placement, Destroyed = false }, objects.Layouts));
        shownEmptied.Add(building.Key);
    }

    void HideEmptied(string key)
    {
        if (!shownEmptied.Remove(key)) return;
        if (shownBuildings.ContainsKey(key) || shownRuins.ContainsKey(key)) return;   // another overlay draws it now
        ActiveObjects?.RemoveRuntime(key);
        nav?.RemoveRuntimeBuilding(key);
        ActiveLamps?.RemoveRuntime(key);
    }

    /// <summary>A new world: the old one's bought houses leave the navmesh overlay (the renderer and lamps forget their runtime placements with the sites').</summary>
    void ForgetEmptiedHouses()
    {
        foreach (string key in shownEmptied) nav?.RemoveRuntimeBuilding(key);
        shownEmptied.Clear();
        emptiedRevision = -1;
    }
}
