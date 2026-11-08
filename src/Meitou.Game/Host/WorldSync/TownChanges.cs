using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Game.Host.WorldSync;

/// <summary>The town buildings the world changed under the renderer: the houses the player bought, and the ruins a town override made or undid.</summary>
sealed class TownChanges(GameHost host)
{
    // The town houses the player bought (WorldSnapshot.EmptiedHouses, docs/simulation.md "Town house purchases"): the purchase destroyed their furniture, so
    // each replaces its level placement in the renderer's runtime placements, the navmesh overlay and the lamps, by its key, as standing and with
    // BuildingPlacement.WithoutInterior set. A bought ruin is a construction site instead, whose own placement says it.
    long _emptiedRevision = -1;
    readonly HashSet<string> _shownEmptied = new(StringComparer.Ordinal);

    /// <summary>Once a frame, with the ruins.</summary>
    internal void UpdateEmptiedHouses(WorldSnapshot snapshot)
    {
        if (snapshot.EmptiedHousesRevision == _emptiedRevision) return;
        _emptiedRevision = snapshot.EmptiedHousesRevision;
        var current = new Dictionary<string, TownBuilding>(StringComparer.Ordinal);
        foreach (var b in snapshot.EmptiedHouses) current[b.Key] = b;
        foreach (string key in _shownEmptied.Where(k => !current.ContainsKey(k)).ToArray()) HideEmptied(key);
        foreach (var b in current.Values)
            if (!_shownEmptied.Contains(b.Key)) ShowEmptied(b);
    }

    void ShowEmptied(TownBuilding building)
    {
        if (building.Resolved is not { } resolved) return;
        var placement = resolved.Placement with { DestroyedOverride = false, WithoutInterior = true };
        host.ActiveObjects?.AddRuntime(placement);
        if (host.Nav is not null && host.BuildingSync.BuildData().TryGetValue(resolved.Record.StringId, out var data) && data.PathMode != 0) host.Nav.AddRuntimeBuilding(placement);
        if (host.ActiveLamps is { } lamps && host.Scene.Database is { } db && host.Scene.Objects is { } objects)
            lamps.SetRuntime(building.Key, WorldLights.ForBuilding(db, objects.Levels, resolved with { Placement = placement, Destroyed = false }, objects.Layouts));
        _shownEmptied.Add(building.Key);
    }

    void HideEmptied(string key)
    {
        if (!_shownEmptied.Remove(key)) return;
        if (host.BuildingSync.ShownBuildings.ContainsKey(key) || ShownRuins.ContainsKey(key)) return;   // another overlay draws it now
        host.ActiveObjects?.RemoveRuntime(key);
        host.Nav?.RemoveRuntimeBuilding(key);
        host.ActiveLamps?.RemoveRuntime(key);
    }

    /// <summary>A new world: the old one's bought houses leave the navmesh overlay (the renderer and lamps forget their runtime placements with the sites').</summary>
    internal void ForgetEmptiedHouses()
    {
        foreach (string key in _shownEmptied) host.Nav?.RemoveRuntimeBuilding(key);
        _shownEmptied.Clear();
        _emptiedRevision = -1;
    }

    // The town buildings a town override ruined or stood up again, or that were laid out afresh (WorldSnapshot.Ruins, docs/game/buildings-production.md
    // "Ruined by residents", docs/game/factions-squads-towns.md "Town residents"): each replaces its level placement in the renderer's runtime placements, the navmesh
    // overlay and the lamps, by its key, with BuildingPlacement.DestroyedOverride set and its layout names; a ruin's particle effects go, a standing one's come.
    long _ruinsRevision = -1;
    internal readonly Dictionary<string, RuinedBuilding> ShownRuins = new(StringComparer.Ordinal);
    // Set while the world's building effects are still being read: they would put a ruin's back, so the ruins' are set again once they are in.
    bool _ruinEffectsPending;

    /// <summary>Once a frame, before the player's sites are diffed (a ruin the player buys becomes one of them).</summary>
    internal void UpdateRuins(WorldSnapshot snapshot)
    {
        if (_ruinEffectsPending && host.Gpu?.BuildingEffects is null)
        {
            _ruinEffectsPending = false;
            foreach (var r in ShownRuins.Values) RuinEffects(r.Building, r.Destroyed);
        }
        if (snapshot.RuinsRevision == _ruinsRevision) return;
        _ruinsRevision = snapshot.RuinsRevision;
        var current = new Dictionary<string, RuinedBuilding>(StringComparer.Ordinal);
        foreach (var r in snapshot.Ruins) current[r.Building.Key] = r;
        foreach (string key in ShownRuins.Keys.Where(k => !current.ContainsKey(k)).ToArray()) HideRuin(key);
        foreach (var r in current.Values)
        {
            bool same = ShownRuins.TryGetValue(r.Building.Key, out var shown) && shown.Destroyed == r.Destroyed && ReferenceEquals(shown.Building, r.Building);
            if (!same) ShowRuin(r);
        }
    }

    void ShowRuin(RuinedBuilding ruin)
    {
        if (ruin.Building.Resolved is not { } resolved) return;
        string key = ruin.Building.Key;
        if (ShownRuins.ContainsKey(key)) HideRuin(key);
        var placement = resolved.Placement with { DestroyedOverride = ruin.Destroyed };
        host.ActiveObjects?.AddRuntime(placement);
        if (host.Nav is not null && host.BuildingSync.BuildData().TryGetValue(resolved.Record.StringId, out var data) && data.PathMode != 0) host.Nav.AddRuntimeBuilding(placement);
        if (host.ActiveLamps is { } lamps && host.Scene.Database is { } db && host.Scene.Objects is { } objects)
            lamps.SetRuntime(key, WorldLights.ForBuilding(db, objects.Levels, resolved with { Placement = placement, Destroyed = ruin.Destroyed }, objects.Layouts));
        RuinEffects(ruin.Building, ruin.Destroyed);
        ShownRuins[key] = ruin;
    }

    void HideRuin(string key)
    {
        if (!ShownRuins.Remove(key, out var ruin)) return;
        if (host.BuildingSync.ShownBuildings.ContainsKey(key) || _shownEmptied.Contains(key)) return;   // the player's site or bought house now: its placement is its own
        RuinEffects(ruin.Building, ruin.Building.Destroyed);   // as the level data has it
        host.ActiveObjects?.RemoveRuntime(key);
        host.Nav?.RemoveRuntimeBuilding(key);
        host.ActiveLamps?.RemoveRuntime(key);
    }

    /// <summary>
    /// The building's particle effects (its own and its exterior layout's, under its key and <c>key/item</c>, as the world's are laid out, <see cref="BuildingEffects.ForBuilding"/>):
    /// none for a ruin, else those it has standing.
    /// </summary>
    void RuinEffects(TownBuilding building, bool destroyed)
    {
        if (host.Gpu?.Particles is not { } particles || building.Resolved is not { } resolved || host.Scene.Database is not { } db || host.Scene.Objects is not { } objects) return;
        if (host.Gpu.BuildingEffects is not null) _ruinEffectsPending = true;
        var standing = BuildingEffects.ForBuilding(db, objects.Levels, resolved with { Placement = resolved.Placement with { DestroyedOverride = false }, Destroyed = false }, objects.Layouts);
        foreach (string id in standing.Select(e => e.PlacementId).Append(building.Key).Distinct()) particles.RemoveBuildingEffects(id);
        if (destroyed) return;
        foreach (var group in standing.GroupBy(e => e.PlacementId)) particles.SetBuildingEffects(group.Key, [.. group]);
    }

    /// <summary>A new world: the old one's ruins leave the navmesh overlay (the renderer and lamps forget their runtime placements with the sites').</summary>
    internal void ForgetRuins()
    {
        foreach (string key in ShownRuins.Keys) host.Nav?.RemoveRuntimeBuilding(key);
        ShownRuins.Clear();
        _ruinsRevision = -1;
    }

    /// <summary>Whether the level data has the placement as a ruin (its state's <c>destroyed</c>).</summary>
    internal bool LevelDestroyed(BuildingPlacement placement) =>
        host.Scene.Objects?.Levels is { } levels && placement.StateId is { } id && levels.Zones.TryGetValue(placement.Zone, out var zone) && zone.Find(id)?.GetBool("destroyed") == true;
}