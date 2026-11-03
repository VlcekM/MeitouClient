using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// The town buildings a town override ruined or stood up again (<see cref="WorldSnapshot.Ruins"/>, docs/game/buildings-production.md "Ruined by residents"): each replaces its
/// level placement in the renderer's runtime placements, the navmesh overlay and the lamps, by its key, with <see cref="BuildingPlacement.DestroyedOverride"/> set; a
/// ruin's particle effects go, a standing one's come.
/// </summary>
sealed partial class GameHost
{
    long ruinsRevision = -1;
    readonly Dictionary<string, RuinedBuilding> shownRuins = new(StringComparer.Ordinal);
    // Set while the world's building effects are still being read: they would put a ruin's back, so the ruins' are set again once they are in.
    bool ruinEffectsPending;

    /// <summary>Once a frame, before the player's sites are diffed (a ruin the player buys becomes one of them).</summary>
    void UpdateRuins(WorldSnapshot snapshot)
    {
        if (ruinEffectsPending && gpu?.BuildingEffects is null)
        {
            ruinEffectsPending = false;
            foreach (var r in shownRuins.Values) RuinEffects(r.Building, r.Destroyed);
        }
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
        RuinEffects(ruin.Building, ruin.Destroyed);
        shownRuins[key] = ruin;
    }

    void HideRuin(string key)
    {
        if (!shownRuins.Remove(key, out var ruin)) return;
        if (shownBuildings.ContainsKey(key)) return;   // the player's site now: its placement is its own
        RuinEffects(ruin.Building, ruin.Building.Destroyed);   // as the level data has it
        ActiveObjects?.RemoveRuntime(key);
        nav?.RemoveRuntimeBuilding(key);
        ActiveLamps?.RemoveRuntime(key);
    }

    /// <summary>
    /// The building's particle effects (its own and its exterior layout's, under its key and <c>key/item</c>, as the world's are laid out, <see cref="BuildingEffects.ForBuilding"/>):
    /// none for a ruin, else those it has standing.
    /// </summary>
    void RuinEffects(TownBuilding building, bool destroyed)
    {
        if (gpu?.Particles is not { } particles || building.Resolved is not { } resolved || scene.Database is not { } db || scene.Objects is not { } objects) return;
        if (gpu.BuildingEffects is not null) ruinEffectsPending = true;
        var standing = BuildingEffects.ForBuilding(db, objects.Levels, resolved with { Placement = resolved.Placement with { DestroyedOverride = false }, Destroyed = false }, objects.Layouts);
        foreach (string id in standing.Select(e => e.PlacementId).Append(building.Key).Distinct()) particles.RemoveBuildingEffects(id);
        if (destroyed) return;
        foreach (var group in standing.GroupBy(e => e.PlacementId)) particles.SetBuildingEffects(group.Key, [.. group]);
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
