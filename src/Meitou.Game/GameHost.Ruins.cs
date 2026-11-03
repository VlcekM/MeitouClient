using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// The town buildings a town override ruined or stood up again (<see cref="WorldSnapshot.Ruins"/>, docs/game/buildings-production.md "Ruined by residents"): each replaces its
/// level placement in the renderer's runtime placements, the navmesh overlay and the lamps, by its key, with <see cref="BuildingPlacement.DestroyedOverride"/> set.
/// </summary>
sealed partial class GameHost
{
    long ruinsRevision = -1;
    readonly Dictionary<string, RuinedBuilding> shownRuins = new(StringComparer.Ordinal);

    /// <summary>Once a frame, before the player's sites are diffed (a ruin the player buys becomes one of them).</summary>
    void UpdateRuins(WorldSnapshot snapshot)
    {
        if (snapshot.RuinsRevision == ruinsRevision) return;
        ruinsRevision = snapshot.RuinsRevision;
        var current = new Dictionary<string, RuinedBuilding>(StringComparer.Ordinal);
        foreach (var r in snapshot.Ruins) current[r.Building.Key] = r;
        foreach (string key in shownRuins.Keys.Where(k => !current.ContainsKey(k)).ToArray()) HideRuin(key);
        foreach (var r in current.Values)
            if (!shownRuins.TryGetValue(r.Building.Key, out var shown) || shown.Destroyed != r.Destroyed) ShowRuin(r);
    }

    void ShowRuin(RuinedBuilding ruin)
    {
        if (ruin.Building.Resolved is not { } resolved) return;
        string key = ruin.Building.Key;
        if (shownRuins.ContainsKey(key)) HideRuin(key);
        var placement = resolved.Placement with { DestroyedOverride = ruin.Destroyed };
        ActiveObjects?.AddRuntime(placement);
        if (nav is not null && BuildData().TryGetValue(resolved.Record.StringId, out var data) && data.PathMode != 0) nav.AddRuntimeBuilding(placement);
        if (ActiveLamps is { } lamps && scene.Database is { } db && scene.Objects is { } objects)
            lamps.SetRuntime(key, WorldLights.ForBuilding(db, objects.Levels, resolved with { Placement = placement, Destroyed = ruin.Destroyed }, objects.Layouts));
        shownRuins[key] = ruin;
    }

    void HideRuin(string key)
    {
        shownRuins.Remove(key);
        if (shownBuildings.ContainsKey(key)) return;   // the player's site now: its placement is its own
        ActiveObjects?.RemoveRuntime(key);
        nav?.RemoveRuntimeBuilding(key);
        ActiveLamps?.RemoveRuntime(key);
    }

    /// <summary>A new world: the old one's ruins leave the navmesh overlay (the renderer and lamps forget their runtime placements with the sites').</summary>
    void ForgetRuins()
    {
        foreach (string key in shownRuins.Keys) nav?.RemoveRuntimeBuilding(key);
        shownRuins.Clear();
        ruinsRevision = -1;
    }

    /// <summary>Whether the level data has the placement as a ruin (its state's <c>destroyed</c>).</summary>
    bool LevelDestroyed(BuildingPlacement placement) =>
        scene.Objects?.Levels is { } levels && placement.StateId is { } id && levels.Zones.TryGetValue(placement.Zone, out var zone) && zone.Find(id)?.GetBool("destroyed") == true;
}
