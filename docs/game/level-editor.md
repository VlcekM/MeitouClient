# Level editor

The original's in-game editor for the level data (placed buildings, towns, roads, fog) and Meitou's version.
Level data: [zones](../formats/zones.md). Its Layout window: [interior editor](interior-editor.md).

## Original

- **Top bar** (`Kenshi_LevelEditor.layout`, **Verified** by reading the file; the screenshots show it centred at the top):
  `towns` Town Placement, `populate` Reset Population, `buildings` Buildings, `items` Items, `snaps` (layout caption
  "Update Snaps", shown as "Fix stuff": the exe sets the caption), `navmesh` Navmesh Tools, `features` Terrain Features,
  `fogfeatures` Terrain Fog, `mod` (combo box), `save` Save Mod, `exit` Exit.

## Meitou

F10 → Tools → "Level editor..." (`LevelEditorHost`, `LevelEditorPanel`, `Building/LevelEdits.cs`).

- The top bar is the original layout, centred at the top. BUILDINGS, SAVE MOD and EXIT work; the other tools say
  "not in Meitou yet". The mod box shows the one mod Meitou saves into. Esc or EXIT closes the bar.
- BUILDINGS opens build mode's window in editor mode: every BUILDING, categories in ordinal order, no placement checks.
  Left click places a ghost, `,` `.` rotate, `-` `=` height, `[` `]` slope (tilt), Backspace undo, Esc closes the window and drops the
  ghosts, CONFIRM places them as level buildings (complete, no construction). Right click drops a queued ghost under the
  cursor, else clears the building the ray meets first: one placed in this session is removed, a level-data one is cleared.
- Placed buildings get ids `<n>-zone<x>.<z>-INGAME` (n from the zone's `nextId`) and state ids `<id>-S23`.
- **SAVE MOD** writes `<Kenshi>/mods/Meitou Level Editor/leveldata/zone.<x>.<y>.zone` for each edited zone, merged over an
  earlier save there: placed buildings as building-list entries with a state (`is complete`, `destroyed`, `world Y pos`,
  `version` 270, `floornum` 0, `flags` 1, `name`), cleared ones as entries of the same id with no target. The zone's slot
  list is written as the trailer. The folder gets `meitou-level-editor.txt` and an empty `Meitou Level Editor.mod`, so the
  launcher can activate it; Meitou never writes into a folder of that name without the marker, nor into `data/`, another mod or
  `settings.cfg`. `leveldata.level` and `fogfeatures.dat` are not written (towns and fog are not edited).
- Meitou reads each active mod's `leveldata/` as a layer in load order ([zones](../formats/zones.md), "Level data layers"), so after a
  restart with the mod `Meitou Level Editor` active the saved buildings are level buildings read at load, as the session's were.
- **In the game save** (Meitou's own, `meitou-level-edits/zone.<x>.<y>.zone` in the save folder, `SaveGame.LevelEdits`; the original ignores the folder, a save without it loads as before): a game save (F5, autosave, the menu) holds the session's placed and
  cleared buildings in the form of the saved mod's zone files (`LevelEdits.Snapshot`: placed buildings with their state, cleared ones as entries with no target), so SAVE MOD is only the way to export edits as a mod. A load puts them back
  through the live path (`ReplayLevelEdits`, run by `UpdateBuildings` when it meets the new world, after the renderer's runtime placements were dropped): placements go through `ApplyPlaced` (level data, renderer, navmesh, lights,
  `DebugPlaceBuilding`), cleared level buildings through the clear a click makes; both are among the session's edits again, so the next save and SAVE MOD keep them. The same step takes the previous world's edits back out first
  (level data, navmesh, renderer and the shared town lists outlive a world), also for a new game. A building the level data has already (the mod written by SAVE MOD is active) is noted only, not placed twice.
  Why a placed building vanished before: the save's zone file is made from the level data but nothing read it back as level data, and the loaded world dropped the edits (**Verified** by code); the mod's files are read only while the
  launcher has `Meitou Level Editor` active (Meitou never edits `mods.cfg`), so SAVE MOD alone does not bring the building back on a plain reload. **Observed** in play: not yet.
- Level data is shared with worker threads: an edit is merged on the main thread while zones may be read elsewhere (editor only).
