using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Game.Building;
using Meitou.Game.Rendering.Objects;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The level editor's host side (docs/game/level-editor.md), opened from the F10 menu's Tools: every edit is merged into the running world's level data as if a mod's zone file had been read
/// at load (<see cref="WorldLevelData.ApplyEdit"/>), so a placed building is a level building for the picking, the building panel, the interior and the lights, and a cleared one is gone from
/// them; the renderer and the navmesh are told too (runtime placements, hidden keys, zones built again). Save Mod writes the edits as zone files of Meitou's own mod (<see cref="LevelEdits"/>).
/// </summary>
sealed partial class GameHost
{
    readonly LevelEdits levelEdits = new();
    // The world the edits belong to, and the level data's buildings by key for clearing.
    World? levelEditsWorld;
    // The edits of the game save just loaded, until the host has met the new world.
    IReadOnlyDictionary<ZoneCoordinate, byte[]>? pendingLevelEdits;
    (WorldLevelData Levels, int Revision, Dictionary<string, BuildingPlacement> ByKey)? levelPlacements;

    void OpenLevelEditor()
    {
        debugMenuOpen = false;
        player.OpenLevelEditor();
    }

    /// <summary>Shift+F12, the original's <c>editor_toggle</c>.</summary>
    void ToggleLevelEditor()
    {
        debugMenuOpen = false;
        player.ToggleLevelEditor();
    }

    LevelEditorServices MakeLevelEditorServices() => new()
    {
        ModName = LevelEdits.ModName,
        Place = LevelPlace,
        Added = () => { LevelEditsFollowWorld(); return levelEdits.Added; },
        Clear = LevelClear,
        IsCleared = key => ReferenceEquals(levelEditsWorld, session.World) && levelEdits.IsCleared(key),
        Save = LevelSave,
    };

    /// <summary>
    /// A loaded save or a new game replaces the world: the edits of the old one are taken out of the level data, the renderer, the navmesh and the town lists again (their runtime placements
    /// go with <see cref="ForgetBuildings"/>). Run by <see cref="UpdateBuildings"/> as soon as the world is another, and by every editor action; then <see cref="ReplayLevelEdits"/>
    /// puts back the loaded save's own.
    /// </summary>
    void LevelEditsFollowWorld()
    {
        if (ReferenceEquals(levelEditsWorld, session.World)) return;
        levelEditsWorld = session.World;
        // The level data (and the town lists of the shared population data) outlive the world: take the edits back out of them.
        if (scene.Objects is { } objects)
        {
            var townBuilder = MakeTownBuilder(objects);
            foreach (var added in levelEdits.Added)
            {
                objects.Levels.ApplyEdit(added.Zone, LevelEdits.ClearedFile(added), LevelEdits.ModName);
                nav?.LevelChanged(added.Position);
                ActiveLamps?.RemoveRuntime(added.InstanceId);
                gpu.Particles?.RemoveBuildingEffects(added.InstanceId);
                session.World.Commands.Enqueue(new DebugClearBuilding(added.Key) { Tick = session.World.Tick });
            }
            foreach (var cleared in levelEdits.Cleared)
            {
                objects.Levels.ApplyEdit(cleared.Zone, LevelEdits.RestoredFile(cleared), LevelEdits.ModName);
                nav?.LevelChanged(cleared.Position);
                ActiveLamps?.RemoveRuntime(cleared.InstanceId);   // the world's own lamps of it shine again
                TownPlaced(townBuilder, cleared);
            }
        }
        foreach (var cleared in levelEdits.Cleared) gpu.Objects?.SetCleared(cleared, false);
        levelEdits.Forget();
    }

    /// <summary>The builder of town buildings from placements (the boot pass's own), null without the population or the database.</summary>
    TownBuildings.Builder? MakeTownBuilder(WorldObjects objects) =>
        popData is null || scene.Database is not { } database ? null
            : new TownBuildings.Builder(database, objects.Levels, objects.Layouts, (x, z) => gpu.Terrain.HeightAt((float)x, (float)z));

    /// <summary>
    /// The edits a game save holds (<see cref="SaveGame.LevelEdits"/>), applied to the world just loaded as the live edits were: Called once the host has met the new world
    /// (<see cref="UpdateBuildings"/>). Placed buildings join the level data, the renderer, the navmesh, the lights and the town simulation as a placement does, cleared ones are cleared
    /// as a click does; both go among <see cref="levelEdits"/> again, so the next save and Save Mod hold them. A building the level data has already (the mod written by Save Mod
    /// is active) is only noted.
    /// </summary>
    void ReplayLevelEdits(IReadOnlyDictionary<ZoneCoordinate, byte[]> files)
    {
        if (scene.Objects is not { } objects || files.Count == 0) return;
        LevelEditsFollowWorld();
        var townBuilder = MakeTownBuilder(objects);
        int placed = 0, cleared = 0;
        foreach (var (zone, bytes) in files)
        {
            var (adds, clearKeys) = LevelEdits.ReadSnapshot(zone, bytes);
            foreach (var placement in adds)
            {
                bool known = LevelBuildingsByKey(objects).TryGetValue(placement.Key, out var existing) && existing.BuildingId == placement.BuildingId;
                levelEdits.Restore(placement);
                if (!known) ApplyPlaced(objects, placement, BuildData().TryGetValue(placement.BuildingId, out var data) ? data.PathMode : 2, townBuilder);
                placed++;
            }
            foreach (string key in clearKeys)
                if (LevelClear(key)) cleared++;
        }
        Console.WriteLine($"level     replayed {placed} placed and {cleared} cleared buildings from the save");
    }

    /// <summary>Puts the session's edits into a game save being captured (the world they belong to is the one saved); a save of a session with none holds none.</summary>
    void WriteLevelEdits(SaveGame game)
    {
        game.LevelEdits.Clear();
        if (!ReferenceEquals(levelEditsWorld, session.World) || levelEdits.IsEmpty || scene.Objects is not { } objects) return;
        foreach (var (zone, bytes) in levelEdits.Snapshot(objects.Levels, id => scene.Database?.Find(id)?.Name ?? id)) game.LevelEdits[zone] = bytes;
    }

    /// <summary>The level data's buildings by key (made again when the level data changed).</summary>
    Dictionary<string, BuildingPlacement> LevelBuildingsByKey(WorldObjects objects)
    {
        if (levelPlacements?.Levels != objects.Levels || levelPlacements?.Revision != objects.Levels.EditRevision)
            levelPlacements = (objects.Levels, objects.Levels.EditRevision, objects.Levels.Buildings().GroupBy(b => b.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
        return levelPlacements.Value.ByKey;
    }

    // The layout names the next LevelPlace gives its buildings (the place-building command's --interior / --exterior); null: the building's own.
    string? placeInterior, placeExterior;

    void LevelPlace(IReadOnlyList<QueuedGhost> ghosts)
    {
        if (scene.Objects is not { } objects) return;
        LevelEditsFollowWorld();
        var townBuilder = MakeTownBuilder(objects);
        foreach (var ghost in ghosts)
        {
            var placement = levelEdits.Add(ghost.Building.Id, ghost.Position, ghost.Rotation, gpu.Terrain.HeightAt(ghost.Position.X, ghost.Position.Z), objects.Levels, placeInterior, placeExterior);
            ApplyPlaced(objects, placement, ghost.Building.PathMode, townBuilder);
        }
    }

    /// <summary>A placement (made in the session, or read from a game save) into the level data, the renderer, the navmesh, the lights and the town simulation.</summary>
    void ApplyPlaced(WorldObjects objects, BuildingPlacement placement, int pathMode, TownBuildings.Builder? townBuilder)
    {
        objects.Levels.ApplyEdit(placement.Zone, LevelEdits.PlacedFile(placement, scene.Database?.Find(placement.BuildingId)?.Name ?? placement.BuildingId), LevelEdits.ModName);
        gpu.Objects?.AddRuntime(placement);
        if (pathMode != 0) nav?.AddRuntimeBuilding(placement);
        else nav?.LevelChanged(placement.Position);
        LightPlaced(objects, placement);
        TownPlaced(townBuilder, placement);
    }

    /// <summary>
    /// The simulation side of a placed building: made a <see cref="TownBuilding"/> as the boot pass makes every level building and handed to the simulation, which adds it to its town's
    /// list and registers its doors, beds, fixtures, item slots and sight like a loaded one's. A building no town takes is in no list, as a loaded one outside every town is not.
    /// </summary>
    void TownPlaced(TownBuildings.Builder? builder, BuildingPlacement placement)
    {
        if (builder?.Build(placement) is not var (town, building)) return;
        session.World.Commands.Enqueue(new DebugPlaceBuilding(town, building) { Tick = session.World.Tick });
    }

    /// <summary>The lamps and effects of a placed building: the world's were read from the level files once at load, so a building made since adds its own (keyed by its instance id, which names its lights).</summary>
    void LightPlaced(WorldObjects objects, BuildingPlacement placement)
    {
        if (scene.Database is not { } db || BuildingPlacements.Resolve(placement, db, objects.Levels, (x, z) => gpu.Terrain.HeightAt((float)x, (float)z)) is not { } resolved) return;
        ActiveLamps?.SetRuntime(placement.InstanceId, WorldLights.ForBuilding(db, objects.Levels, resolved, objects.Layouts));
        gpu.Particles?.SetBuildingEffects(placement.InstanceId, BuildingEffects.ForBuilding(db, objects.Levels, resolved, objects.Layouts));
    }

    bool LevelClear(string key)
    {
        if (scene.Objects is not { } objects) return false;
        LevelEditsFollowWorld();
        var placement = levelEdits.Added.FirstOrDefault(p => p.Key == key);
        if (placement is null)
        {
            if (!LevelBuildingsByKey(objects).TryGetValue(key, out placement)) return false;
        }
        var result = levelEdits.Clear(placement);
        if (result is not null) session.World.Commands.Enqueue(new DebugClearBuilding(placement.Key) { Tick = session.World.Tick });   // out of its town's list and registries
        switch (result)
        {
            case true:
                // Placed in this session: it leaves the level data again (an entry with no target), the renderer and the navmesh.
                objects.Levels.ApplyEdit(placement.Zone, LevelEdits.ClearedFile(placement), LevelEdits.ModName);
                // The renderer's and the navmesh's runtime placements are named by the key.
                gpu.Objects?.RemoveRuntime(placement.Key);
                if (nav is not null && nav.Runtime.All().Any(p => p.Key == placement.Key)) nav.RemoveRuntimeBuilding(placement.Key);
                else nav?.LevelChanged(placement.Position);
                ActiveLamps?.RemoveRuntime(placement.InstanceId);
                gpu.Particles?.RemoveBuildingEffects(placement.InstanceId);
                return true;
            case false:
                // A level building: gone from the level data (the picking, the panel, the lights), hidden in the renderer, and the navmesh zones it shaped are built again without it.
                objects.Levels.ApplyEdit(placement.Zone, LevelEdits.ClearedFile(placement), LevelEdits.ModName);
                gpu.Objects?.SetCleared(placement, true);
                nav?.LevelChanged(placement.Position);
                ActiveLamps?.SetRuntime(placement.InstanceId, []);   // replaces the world's lamps of it with none
                return true;
            default:
                return false;
        }
    }

    /// <summary>Save Mod: the zone files go into <c>mods/&lt;LevelEdits.ModName&gt;/leveldata/</c>, a folder Meitou made (it carries <see cref="LevelEdits.MarkerFile"/>).</summary>
    string LevelSave()
    {
        if (scene.Objects is not { } objects) return "Level editor: no level data loaded";
        LevelEditsFollowWorld();
        if (levelEdits.IsEmpty) return "Level editor: nothing to save";
        string mod = Path.Combine(install.ModsDirectory, LevelEdits.ModName);
        string marker = Path.Combine(mod, LevelEdits.MarkerFile);
        if (Directory.Exists(mod) && !File.Exists(marker)) return $"Level editor: mods/{LevelEdits.ModName} exists and is not Meitou's; nothing saved";
        try
        {
            Directory.CreateDirectory(mod);
            if (!File.Exists(marker)) File.WriteAllText(marker, "Written by Meitou's level editor (docs/game/level-editor.md). Meitou only writes into this folder while this file is here.\n");
            // The mod's own game-data file, empty, so the launcher lists it and the game loads its leveldata/ once it is active.
            string data = Path.Combine(mod, LevelEdits.ModName + ".mod");
            if (!File.Exists(data)) Meitou.Data.Fcs.FcsWriter.WriteFile(new Meitou.Data.Fcs.FcsFile { Author = "Meitou", Description = "Buildings placed with Meitou's level editor" }, data);
            var zones = levelEdits.Save(Path.Combine(mod, "leveldata"), objects.Levels, id => scene.Database?.Find(id)?.Name ?? id);
            Console.WriteLine($"level     saved {levelEdits.Added.Count} placed and {levelEdits.Cleared.Count} cleared buildings in {zones.Count} zones to {mod}");
            return $"Saved mod {LevelEdits.ModName}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Level editor: saving failed: {e.Message}";
        }
    }
}
