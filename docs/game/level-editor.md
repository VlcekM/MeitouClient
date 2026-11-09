# Level editor

The original's in-game editor for the level data (placed buildings, towns, roads, fog) and Meitou's version.
Level data: [zones](../formats/zones.md). Its Layout window: [interior editor](interior-editor.md).

## Original

- **Opening**: `editor_toggle`, Shift+F12 ([keys](ui-input.md); **Observed** in the install's `controls.cfg` and the changelog's "toggle the level editor with shift + F12"); how a release build enables it is **Unknown**. Meitou opens and closes it from the F10 menu's Tools only (upstream moved the VRAM pie to Shift+F12, so the original key is not bound). The game keeps
  running under it, the normal HUD shows while no tool panel is open (**Observed**, the user's screenshots
  `Meitou/docs/captures/original-level-editor/`).
- **Top bar** (`Kenshi_LevelEditor.layout`, **Verified** by reading the file; the screenshots show it centred at the top):
  `towns` Town Placement, `populate` Reset Population, `buildings` Buildings, `items` Items, `snaps` (layout caption
  "Update Snaps", shown as "Fix stuff": the exe sets the caption), `navmesh` Navmesh Tools, `features` Terrain Features,
  `fogfeatures` Terrain Fog, `mod` (combo box), `save` Save Mod, `exit` Exit.
- **Buildings tool** uses the build window (`Kenshi_BuildWindow.layout`: CATEGORIES, BUILDINGS, picture, description,
  CONFIRM, UNDO, the hammer, COMMANDS, the floor arrows). **Observed** (screenshot): categories are every BUILDING
  `building category` of the loaded data, mods' too, in ordinal order ("(DEBUG)", "AA[ID] Tables", ..., "CRAFTING", "Cheating").
  Whether the list is limited in any way is **Unknown**.
- **Commands box** (exe strings next to `PlayerBuildMode`, shown in this order in the screenshot): "Place Building: Left-Click",
  "Clear Building: Right-Click", "Rotate: {1} or {2} keys" (< > or , .), "Change Height: + and -", "Change Slope: [ and ]",
  "Undo: Backspace", "Cancel All: Esc", "Press [Confirm] when finished". Whether player build mode shows the same text is **Unknown**.
- **Fix stuff** reports "Fixed N duplicated buildings", "Snapped N buildings", "Nothing needed fixing. Yay!",
  "Updated interior usage nodes" (**Observed**, exe strings; flow not traced). Road tools: "Place road marker",
  "Start road", "Stop road", "Validate road network", "Project roads".
- **Mod list and saving** (Save Mod `FUN_140777ce0`, **Observed** by disassembly over the function): asks "Are you sure you want to
  save the mod?" (with a warning that saving player-built towns into a mod can cause weird issues), writes
  `fogfeatures.dat`, `leveldata.level` and `zone.<x>.<y>.zone` files (with the zone's `0-buildinglist`; "Moved building
  ... to a zone ..." when one changed zones), stores the mod name as `editor` in [settings.cfg](../formats/settings.md) and
  reports "Saved mod <name>". The list holds the active mods; with none: "No mods are active. To save level changes to a
  mod, you must create a mod with the Forgotten Construction Set and activate it in the game launcher." The folder is the
  mod's level data folder of the [layer list](../formats/zones.md) (**Observed**: the save calls into the game-data list at
  `0x142134110`); that it is `<mod folder>/leveldata/` is **Unknown** in detail.
- **What a mod's zone file holds** (**Observed**, `mods/*/leveldata/` of the sample install, e.g. True_chaos_raids
  `zone.27.22.zone`): type 15, GAMESTATE_BUILDING states (`-S23`), then the `0-buildinglist` INSTANCE_COLLECTION (type 30, name
  "0") with only some of the zone's buildings, each with its state; instance ids keep the base game's
  (`1821-Newwworld-INGAME`). Whether Save Mod writes only changed buildings is **Unknown**.

## Meitou

F10 → Tools → "Level editor..." (`LevelEditorHost`, `LevelEditorPanel`, `Building/LevelEdits.cs`).

- The top bar is the original layout, centred at the top. BUILDINGS, SAVE MOD and EXIT work; the other tools say
  "not in Meitou yet". The mod box shows the one mod Meitou saves into. Esc or EXIT closes the bar.
- BUILDINGS opens build mode's window in editor mode: every BUILDING, categories in ordinal order, no placement checks.
  Left click places a ghost, `,` `.` rotate, `-` `=` height, `[` `]` slope (tilt), Backspace undo, Esc closes the window and drops the
  ghosts, CONFIRM places them as level buildings (complete, no construction). Right click drops a queued ghost under the
  cursor, else clears the building the ray meets first: one placed in this session is removed, a level-data one is cleared.
- **Dropping the placement choice** (the user's rule for Meitou; how the original stops placing without leaving the editor is **Unknown**,
  its Commands box lists only "Cancel All: Esc"): a right click on nothing (no queued ghost, building or item under the cursor) drops the
  chosen row: no ghost follows the cursor and the list shows no choice; queued ghosts stay. Meitou also drops it with Esc (a further Esc
  closes the window as above, then the bar) and with a click on the chosen row; any row or category click chooses again.
  The player's build mode (Build button, same list and ghost) drops it with a right click on empty ground and with a click on the chosen row
  (queued ghosts stay; a laid wall line is dropped first); its Esc is unchanged (the original's Esc cancels all and leaves build mode, [buildings-production](buildings-production.md)).
- **Edits are level data at once**: each placement and each clearing is merged into the running world's level data as a layer
  (`WorldLevelData.ApplyEdit`, the same records the saved mod holds; `EditRevision` counts them). Every consumer that keeps an index of
  the level data's buildings checks the revision and makes it again: the picking footprints, the navmesh's zone gatherer and its
  building-by-key map, the interior editor and the clear lookup. A placed building is therefore a level building for picking (click
  selects it), the building panel and the interior editor, a cleared one is gone from them. The renderer (runtime placements named by
  the placement key, hidden keys), the navmesh (zones built again), the lamps and the building effects (the world's were read from
  the level files at load, so the host adds a placed building's own, keyed by its instance id, and silences a cleared building's) are
  told as well. **Observed** in play: not yet; read from the code.
- Placed buildings get ids `<n>-zone<x>.<z>-INGAME` (n from the zone's `nextId`) and state ids `<id>-S23`.
- **SAVE MOD** writes `<Kenshi>/mods/Meitou Level Editor/leveldata/zone.<x>.<y>.zone` for each edited zone, merged over an
  earlier save there: placed buildings as building-list entries with a state (`is complete`, `destroyed`, `world Y pos`,
  `version` 270, `floornum` 0, `flags` 1, `name`), cleared ones as entries of the same id with no target. The zone's slot
  list is written as the trailer. The folder gets `meitou-level-editor.txt` and an empty `Meitou Level Editor.mod`, so the
  launcher can activate it; Meitou never writes into a folder of that name without the marker, nor into `data/`, another mod or
  `settings.cfg`. `leveldata.level` and `fogfeatures.dat` are not written (towns and fog are not edited).
- Meitou reads each active mod's `leveldata/` as a layer in load order ([zones](../formats/zones.md), "Level data layers"), so after a
  restart with the mod `Meitou Level Editor` active the saved buildings are level buildings read at load, as the session's were.
- **Simulation side**: a building placed or cleared in the session is a town building at once, as one the level data holds at load. The host makes the placed building's `TownBuilding` with
  `TownBuildingBuilder`, the code the boot pass (`TownBuildings.Build`) runs for every level building (town by radius, owner from the state or the town, doors, beds, seats, containers, item slots, interior layouts),
  and queues `DebugPlaceBuilding` (cleared: `DebugClearBuilding`) with the current tick. The simulation applies it in the serial phase (`TownEdits`): the town's list in `PopulationData.Sites[].Buildings` is replaced by
  a changed copy (copy on write, `PopulationData.BuildingsRevision` counts it for the host's caches), and a town the registries already hold gets the building registered (`FixtureRegistry.RegisterAdded`: doors with their
  locks, own lock, cages, containers, beds, seats, property, `SightMap` room and lamps; `ItemSlots.AddBuilding`); any other town takes it from the list when it is first registered. Clearing (placed or loaded building) takes it out of the
  list and the registries (`RemoveBuilding`: sleepers leave their beds, caged ones are let out, fixtures, property, public flag, room and lamps go; item-slot items stay lying). The doors table follows through `SyncDoors`.
  A building no town takes (`MemberOf`: no town radius holds it and it has no interior near a town, **Observed**, docs/formats/zones.md "Town membership") is in no list, as a loaded one outside every town is: no doors, beds or owner in Meitou.
  A placed building has the state `LevelEdits` writes (no owner, no interior layout name, no residents template): it belongs to its town's faction and takes the default layout, as a loaded building with such a state does.
  Not done / **Unknown**: residents for a building placed in a town that has spawned already (squads are housed when the town is made, so only a town made later houses its residents there; what the original does for an editor-placed
  building in a running town is **Unknown**); the original's own owner choice for an editor-placed building (**Unknown**; Meitou uses the town's faction); a purchased or player-repaired building cleared in the session keeps its construction site.
- **In the game save** (Meitou's own, `meitou-level-edits/zone.<x>.<y>.zone` in the save folder, `SaveGame.LevelEdits`; the original ignores the folder, a save without it loads as before): a game save (F5, autosave, the menu) holds the session's placed and
  cleared buildings in the form of the saved mod's zone files (`LevelEdits.Snapshot`: placed buildings with their state, cleared ones as entries with no target), so SAVE MOD is only the way to export edits as a mod. A load puts them back
  through the live path (`ReplayLevelEdits`, run by `UpdateBuildings` when it meets the new world, after the renderer's runtime placements were dropped): placements go through `ApplyPlaced` (level data, renderer, navmesh, lights,
  `DebugPlaceBuilding`), cleared level buildings through the clear a click makes; both are among the session's edits again, so the next save and SAVE MOD keep them. The same step takes the previous world's edits back out first
  (level data, navmesh, renderer and the shared town lists outlive a world), also for a new game. A building the level data has already (the mod written by SAVE MOD is active) is noted only, not placed twice.
  Why a placed building vanished before: the save's zone file is made from the level data but nothing read it back as level data, and the loaded world dropped the edits (**Verified** by code); the mod's files are read only while the
  launcher has `Meitou Level Editor` active (Meitou never edits `mods.cfg`), so SAVE MOD alone does not bring the building back on a plain reload. **Observed** in play: not yet.
- Level data is shared with worker threads: an edit is merged on the main thread while zones may be read elsewhere (editor only).
- Edits of a world are dropped when another loads (a loaded save brings its own back, see above). Not done: the other top-bar tools, undo after CONFIRM, moving a building,
  floors, the mod choice, confirming the save, residents for a building placed in a running town (see Simulation side).

## Unknowns

- How the editor is enabled in a release build; the original's placement checks (if any) in the editor.
- The exact save folder, which buildings Save Mod writes, and whether the game needs more state keys than Meitou writes.
- Whether an entry without a target removes a building when the original loads a mod's zone file (Meitou's merge does).
