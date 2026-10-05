# Save games: `save/<name>/`

Examined 2026-10-05 on game version 1.0.68 (Steam install, `currentVersion.txt`: "Kenshi 1.0.68 - x64 (Newland)").

Sources:
- **Sample save**: one real save, `wade`, found under `%LOCALAPPDATA%\kenshi\save\wade\` (70 files, 6.6 MB; made by a
  new game on the base game files only, saved three times on 2026-10-04 at 21:48, 22:04 and 22:15 per `save.log`; early in the game,
  in-game day 1 13:39, one player character). Every
  **Verified** below that says "the sample" was checked on these 70 files; with a single save, anything about
  rarer content (player-built bases, long play, mods, dead characters, prisoners) is at most **Observed**. The
  install's own `save/` folder holds only empty template folders.
- **Logs**: `save.log` next to `kenshi_x64.exe` (the game's own log of the save file system), `settings.cfg`, `kenshi.log`.
- **Binary** `kenshi_x64.exe`, decompiled (Ghidra dump, class names from MSVC RTTI): `SaveManager` (`saveGame`,
  `loadGame`, `importGame`, named by the game's own log strings), `SaveFileSystem` (a `ThreadClass`), `hand` (object
  handles), `GameDataContainer` (the FCS record container), `ZoneManager`, `EffectHandler*`. Function addresses are in
  [Functions](#functions); roles of the many small writer/reader pairs come from their callers and from which side of
  a record they assign (**Observed**).
- **Readers**: `Meitou.Data.Fcs.FcsReader` parses every file of the sample (see [Format](#file-format)); probes in
  `MeitouClient-re/probes/save` checked counts and cross-references; base data through `GameDatabase.Load(LoadOrder.BaseGame)`.

Related: [fcs-mod.md](fcs-mod.md) (record layout), [zones.md](zones.md) (the same files for the base world),
[weather.md](weather.md), [characters.md](../characters.md), [terrain.md](terrain.md#world-coordinates) (zone grid).
The game systems whose state is saved here: [factions-squads-towns.md](../game/factions-squads-towns.md) (factions, relations, platoons, towns),
[character-stats.md](../game/character-stats.md) (STATS, MEDICAL_STATE), [combat.md](../game/combat.md) (body part state), [ai.md](../game/ai.md) (jobs, packages, Blackboard fields),
[economy.md](../game/economy.md) (money, trade goods, bounties), [buildings-production.md](../game/buildings-production.md) (building and research state), [game-loop.md](../game/game-loop.md) (clock, autosave timer, pause)
and [ui-screens.md](../game/ui-screens.md) (load, save and import windows).

## Summary

A save is a **folder of FCS game-data files** (file type 15, the header-less layout the world `.zone` files use),
not a single blob. `quick.save` holds the global state (time, weather, factions, towns); one `.platoon` file per
squad holds its characters in full (stats, wounds, inventory, appearance, AI); one `.zone` file per zone that was
ever loaded holds the buildings and loose items of that zone. Everything else (terrain, foliage, the base towns'
placements, character and building definitions) is **not saved**: it comes from the `.base`/`.mod` files again, and
the saved records only carry the *state* of things placed by those files, joined by string ids and five-part object
handles. Most record types are ones `fcs.def` does not declare (the game defines them itself): the `GAMESTATE_*` types, `*_STATE` types, PLATOON, WAR_SAVESTATE, STATE,
SAVED_STATE, CAMERA and TERRAIN_DECALS. Five reuse a declared type number: ITEM (4) and ITEM_PLACEMENT_GROUP (83) (**Verified** with `fcs_fields.tsv` against the saved keys: of 31 saved ITEM keys only `item function` is a declared ITEM field; none of the 9 saved ITEM_PLACEMENT_GROUP keys is declared), and STATS (**Verified**: the 33 skill fields `fcs.def` declares for STATS, e.g. `strength`,
`katana`, `toughness2`, all occur as float keys of the saved STATS; the other 7 declared names are editor category headings; the save adds `xp`, `free attribute points`,
`warrior spirit` and 8 more skills), and RESEARCH and BIOMES (**Verified**: the saved keys, `num finished`, `finished<k>`, `<k>_w_wi_*` ..., are not among their declared fields; the type
number is reused for game state).

## Where saves live

| Setting / path | Meaning |
| --- | --- |
| `%LOCALAPPDATA%\kenshi\save\<name>\` | **Verified** (`save.log` paths, and the sample is there). Used when `settings.cfg` `User save location=1` (default 1) |
| `<install>\save\<name>\` | Legacy location, when the key is 0. **Observed** (`FUN_1403e84f0`): if the key is missing from `settings.cfg` and the install-relative save folder already holds saves (`FUN_14036b680` lists saves; presumably in the install-relative folder, since the setting is not applied yet) the game keeps using the install folder (sets 0); otherwise 1. The UI text: "Save games to user directory instead of the kenshi install path" |
| `settings.cfg` `continue=<name>` | The save the "Continue" button loads; written after every save (kind 1 in `FUN_14047c310`). Sample: `continue=wade` |
| `settings.cfg` `autosaveindex` | Autosave slot counter (see [Autosave](#autosave-and-quicksave)); absent in the sample (no autosave happened) |
| `<install>\save.log` | Log of the save file system (**Verified**: the sample's saves are all logged). In the install folder even when saves are in the user folder |

The install's `save/current/{platoon,zone/import}` and `save/quicksave/{characters,platoon,zone/import}` are empty
(created with the install, **Observed**). `zone/import` is presumably used by the Import Game feature (`FUN_140377710` reads zone files of an imported save, string "imported"; **Unknown**). `characters/<name>.mesh`: **Observed** that the game can write exported character meshes there
(`FUN_1405eab00`, Ogre `MeshSerializer`); not in the sample, purpose **Unknown**.

Save folder names: free text typed in the save dialog, `quicksave` (the Quicksave hotkey, string `quicksave` in
`FUN_1403f0260`, which [ui-input.md](../game/ui-input.md#10-settingscfg-and-the-options-window) identifies as the options-tab builder, not an input handler; the quicksave and quickload events 0xe / 0xf both use the save name `quicksave`, [ui-input.md](../game/ui-input.md#6-event-dispatch)), `autosave0` .. `autosave2`. **Observed** (strings and `FUN_14047be70`).

## Folder layout

```
<name>/
  quick.save                 global state                      (1 file, 2.26 MB in the sample)
  portraits_texture.png      character portrait atlas          (2048 x 2048 RGBA)
  platoon/<faction>_<n>.platoon   one file per squad           (56 files, 6-120 KB)
  zone/zone.<X>.<Y>.zone     one file per loaded zone          (13 files, 2 KB - 1.4 MB)
```

| Item | Status |
| --- | --- |
| `.save`, `.platoon`, `.zone` all parse with `FcsReader` as file type 15 | **Verified** (70/70 files of the sample, to the exact end of records; `.zone` and `quick.save` have a trailer, below) |
| `portraits_texture.png` | **Verified** 2048 x 2048, 8-bit RGBA (PNG header). **Observed** (`FUN_140414380`): cells of 128 px, 16 x 16 portraits, class `PortraitImage`; written with the save. GAMESTATE_CHARACTER `portrait_serial` / `portrait_index` select the cell (**Unknown** exactly how) |
| Platoon file name | **Verified** (56/56): `<faction name>_<n>.platoon` where `<n>` is a per-faction counter; the PLATOON record's `content file` is `platoon/<platoon stringID>.platoon` |
| Zone file name | **Verified** (13/13 and by `FUN_14036c070`): `zone.<X>.<Y>.zone`, X and Y the zone grid indices ([terrain.md](terrain.md#world-coordinates)) |

There is **no** header, version field or checksum in any file. The version is the string `version` ("1.0.68") in the
CAMERA record of `quick.save`.

## Writing and loading

### The save file system (`SaveFileSystem`)

**Observed** (decompiled `FUN_140471de0`, `FUN_1404711e0`, `FUN_140470fc0`, `FUN_140471d10`, `FUN_1404746e0`,
`FUN_140473c30`, `FUN_140473720`, `FUN_140472ac0`) and **Verified** against `save.log` and the folder:

- `SaveFileSystem` is a worker thread ("Saving") with a **layered virtual folder**. A read of `quick.save`,
  `platoon/X.platoon` or `zone/zone.X.Y.zone` is resolved through a map "relative name -> layer folder", built by
  recursively scanning each layer ("Scanning ...", only files, `\` turned into `/`). The loaded save folder is the base layer;
  later layers override it. Writes always go to a **temporary working folder** `save\_current<N>` (the first
  `N` = 1, 2, ... whose folder does not exist), created lazily at the first write, with `platoon` and `zone`
  sub-folders ("Creating temporary folder ..."). At start-up the game scans for residual `_current*` folders
  (the log's `scanForResidualTempFolders` warning when the save root does not exist yet).
- **Committing a save** (the save thread, `FUN_140473720`): log `Saving <folder>`, copy the working folder over the
  named folder ("Copying A to B", one "Copied file ..." per file and directory, "Saving complete: copied in N ms"),
  then rescan the named folder as the new base layer. **Verified**: the 22:15:52 save of the sample logged 73 copies,
  exactly the 56 platoon files + `quick.save` + the PNG + 13 zone files + the 2 directories now in `wade`; files of the
  earlier 22:04 save that no longer exist in the game (e.g. `Tech Hunters_0.platoon`) are gone, so the named folder
  ends up a copy of the working folder. Zone files are copied unchanged in content (the file times are those of the write into the working folder: six zone files, `zone.20.32`, `zone.20.33`, `zone.21.31`, `zone.21.32`, `zone.21.33` and `zone.22.32`, are dated 22:06, between the 22:04 and 22:15 saves, the other seven and all 56 platoon files 22:15).
- A copy that cannot find the source logs "Source folder ... is missing" and shows "Could not find the save folder ... Your new
  save will likely be missing stuff". Save size is small enough that the whole copy took 28-99 ms in the sample.
- The game does not save while a load is in progress; failures show "Error saving game. Please try again."
  (the `FUN_140375360` result, `FUN_14047c310`) or "Failed to save game" / "Failed to load game" / "Failed to import game" (`FUN_14047c310`).

### Save requests (`FUN_14047c310`)

A queued request (kind at +0xA0 of the request object) is run on the next frame; **Observed**:

| Kind | Action |
| --- | --- |
| 1 | `SaveManager::saveGame` (`FUN_140375360`), then write `continue=<name>` to `settings.cfg` |
| 2 | `SaveManager::loadGame` (`FUN_140373f00`) |
| 3 | `SaveManager::importGame` (`FUN_140378a30`, flags in +0xA4): new world, keeps the squad of the chosen save |
| 4 | New game: sets the version string to "1.0.68", reloads the game data and starts the chosen NEW_GAME_STARTOFF |

### What `saveGame` writes (`FUN_140375360`)

Fixed order, **Observed** (the decompile's call order) and **Verified** against the sample's record inventory:

1. Clear the in-memory `GameDataContainer` ("the save container"; `FUN_1406bf5c0`, which differs from the plain clear `FUN_1406bf510` only by not deleting PLATOON records, **Observed**). Write the **player's faction**: an INSTANCE_COLLECTION record
   holding the player faction's GAMESTATE_FACTION (`FUN_140375100` -> `FUN_140372a60`). The faction writer also writes the faction's WAR_SAVESTATE
   (`FUN_1409c6890`), the relation tables, `platoonIDs`, and then calls **virtual slot 0xB8 (save) of each platoon in its two platoon lists**: that slot is `FUN_140372ef0`
   for the RTTI class `Platoon` and `FUN_140377530` for `UniquePlatoon` (**Verified**: both are slot 23 = byte offset 0xB8 of those vtables, `rtti.tsv`, and `FUN_140372a60` calls offset 0xB8 on each platoon of its two lists; the matching loader is slot 24, **Observed**). It writes the PLATOON record and
   `platoon/<name>.platoon` (`FUN_14036bad0`; the characters' six records come from `FUN_1406280a0`, **Observed**).
2. Write **all zones that are loaded in memory** (`FUN_14036e450` -> `FUN_14036dde0` -> `FUN_14036c070`), each to `zone/zone.X.Y.zone`.
   Zones that were unloaded earlier were already written to the working folder at that moment: **Verified** (decompile) that `ZoneManager`'s
   `ZoneMapContent::deactivate` (`FUN_1409fe980`, named by its own string) calls `Zone::save` when its save flag is set; consistent with the unchanged modification times above.
   (`Zone::save` has one more caller, `FUN_14036ec10`, a mod-editor warning path: "The following buildings where saved with player's faction".)
3. Write the **towns**: the GAMESTATE_TOWN_INSTANCE_LIST record (`FUN_14092a6c0`) and one GAMESTATE_TOWN per town (`FUN_14036b710`, virtual slot 0x2A8 of the town).
4. Create the CAMERA record (type 56, name `0`) and fill it, in this order (**Verified** against the decompile of `FUN_140375360` read twice, call order and string arguments, and the sample's key set): camera tracking (`FUN_1406b0740`), `player money`, `pfaction name`, `version`, `squads`,
   `members`, `floor num`, the `area` string (the name of the biome group at the camera position, replaced by a one-character placeholder when it equals a certain 4-character literal; the rule is not traced, **Observed**), selection (`FUN_1407f3e60`), biome text (`FUN_140728af0`), decals (`FUN_1408de5d0`, only when the decal manager exists), one more
   writer `FUN_140387b40` (not read), squad toggles/orders
   (`FUN_1403ee910`, `FUN_1403eb710`), time and sky (`FUN_14066d7d0`), unique characters (`FUN_1409a8660`). Then RESEARCH (type 21, `FUN_1408310b0`), BIOMES
   (type 28, `FUN_1408faf40`) and, only when the redirect table is not empty, SAVED_STATE (type 40) named `5-redirect`.
5. Fill the CAMERA reference list `mods` (one entry per loaded data file), then run the **zone-file check** `FUN_140a0bef0` (it runs at save time too, the same "Zone file
   missing / Extra zone file" logic as on load), then fill the `zones` list from the zone manager's 64 x 64 grid (every zone whose "has a file" flag at +0x24 is set).
6. Write the **other factions** (`FUN_140375210`: one INSTANCE_COLLECTION with every faction that is not the player's, each with its WAR_SAVESTATE and platoons), then the portrait atlas
   (`FUN_140414c80`), then write `quick.save` through `FUN_140471d10` (path) and `FUN_1406bd570` (with the slot-list trailer), and finally commit (above). The log line
   "Save write time N ms (T: .., F: ..)" gives the total and two sub-timings (**Observed**: the call sites).

### `loadGame` (`FUN_140373f00`)

**Observed** (decompile of `FUN_140373f00`, call order read twice), the order of effects:

1. `FUN_14036c5f0`: the folder must contain `quick.save`. The container is cleared (plain clear `FUN_1406bf510`, PLATOONs included), the folder becomes the base layer of the save file system and `quick.save` is loaded through it.
2. CAMERA record (type 56, looked up as the one record of that type): time and sky first (`FUN_14066db50`).
3. The **factions and town lists**: `FUN_1409ef230` (reads every GAMESTATE_FACTION through `FUN_14036f2e0`), then `FUN_1403728c0`, `FUN_140935120`, `FUN_1403722d0` and `FUN_14092ae80`; afterwards a town count of 0 logs "[SaveManager::loadGame] No towns loaded.". The platoon objects come in with the factions (the loader is `Platoon` vtable slot 24 = `FUN_1407ed0f0`, **Observed**: its strings are the PLATOON fields and slot 23 is the writer); the exact call chain from the faction reader is not traced.
4. Back to CAMERA: the `version` string (turned into a number below), the camera (`FUN_1407ade60`), `pfaction name` (stored as the player faction's name; if there is no player faction object yet the game looks up `204-gamedata.base` and uses its name, else "Nameless"), then `player money` and `floor num`.
5. BIOMES (`FUN_1408fafc0`).
6. The `5-redirect` record (if present): pairs of old and new character handles, put in a global redirect table.
7. The zone-of-the-camera lookup, unique characters (`FUN_1409aa270`), squad toggles (`FUN_1403eef20`, `FUN_140387c50`, `FUN_1403ebf50`), `FUN_1407f3530`.
8. RESEARCH (`FUN_1408360f0`).
9. The `zones` list: for each entry (x, y, v2) the zone's "has a save file" flag (+0x24 of the zone object) is set to
   `v2 != 0`; then `FUN_140a0bef0` compares the list with the files in `zone/` and logs "[Bad save data] Zone file missing: ..." /
   "Extra zone file: ..."; a mismatch shows the warning "This save is missing zone files. This will likely cause issues such as
   missing player buildings".
10. The portrait atlas is loaded (`FUN_140412f90`), then WAR_SAVESTATE records (type 9) are applied through `FUN_1409c9dc0` to the faction named by each record's `faction` string.
11. Selection (`FUN_1407f3fb0`), biome text (`FUN_140728c10`) and decals (`FUN_1408dc350`).
12. Version check: the CAMERA `version` is turned into a number `major << 24 | minor << 12 | patch` (`FUN_14047acf0`, `sscanf` of `%d.%d.%d`; **Verified** by reading the packing in the function and the "%d.%d.%d" string);
   a save below **0.99.99** (the constant string is compared the same way) shows "Kenshi has now been updated to v{1}. You should go back to the main menu and press
   [Import Game] to start a fresh new game world with your existing squad."; a separate check `FUN_14047d210` (not on `loadGame`'s path: its callers are the menu code `FUN_14047d540` and `FUN_140914c40`) shows "Old save version" for a number below **0.99.0** (constant `"0.99.0"` in that function).
   (A version that does not parse as `%d.%d.%d` is number 0.)

Whether every key the readers look for is present in the sample's records was checked only for the keys listed in this doc, not exhaustively (**Observed**); nothing was run.

### Autosave and quicksave

- **Observed** (`FUN_14047be70`, called every frame): the `Auto save` setting (read by `FUN_1403e84f0`) gates everything; with it off the timer is held at 0. With it on, and while the zone manager reports nothing loading and the player object's field at +0x298 is empty, a
  timer accumulates frame time (the PlayerInterface field at +0x298 is the build placement ghost, [ui-input.md](../game/ui-input.md#7-mouse-and-selection), so the timer waits while a building is being placed; **Observed**, the object identity is matched by offset only). When it exceeds `Auto save time (minutes)` x 60 (settings.cfg, default 10) it is set to the sentinel -1 and "Autosaving..." is shown; on the next call the sentinel
  triggers the save: it reads `autosaveindex` from `settings.cfg` (0 when absent), uses `(index + 1) mod 3` as the new index, stores it back, resets the timer and queues a save request named `autosave<index>` (`FUN_14047b920` with flag 1; presumably kind 1 with `+0xA4` = 1, **Observed** only by its arguments). There are three rotating autosave folders; the quicksave hotkey saves as `quicksave`.
  Every queued request (also an autosave) writes `continue=<name>` to `settings.cfg` after a successful save (`FUN_14047c310`), so after an autosave the "Continue" button points at it; a request resets the autosave timer.
- Autosave never ran in the sample (no `autosaveindex` key). **Unknown**: the meaning of the flag byte (+0x11c) that also holds the timer back (copied from a global each frame).

## File format

All files are FCS **file type 15** ([fcs-mod.md](fcs-mod.md#file)): `int32 15`, `int32 nextId`, `int32 recordCount`, then
the records in the ordinary record layout. **Verified** by `FcsReader` on all 70 files; and by `FUN_1406bd570`, which
**Observed** always writes the type 15 header (the writer writes the constant 15, then `nextId`, the record count and
the records).

Differences from `.mod` files, all **Verified** on the sample unless noted:

- **Record string ids** are `<n>--INGAME` (quick.save and platoon files), `<n>-zone<X>.<Y>-INGAME` for runtime-created zone records, with `-S<hex type>` appended on state records
  (`...-INGAME-S23`, `-S5e`), and base-derived `<id>-Newwworld-INGAME-S23` for states of base placements (**Verified**, pattern counts over all 6,577 records). The numeric `id` equals the number in the string id for 4,969 of 6,577 records;
  it differs for the 56 PLATOON records (string id = the platoon name, e.g. `Starving Bandits_3`), the 7 STATE records (`nest<n>-S27`), the 33 nest GAMESTATE_TOWN records, the 16 `0-buildinglist`/`1-itemlist` collections and the 1496 GAMESTATE_BUILDING states (probe). `flags` are 0 in every record (**Verified**: 0 non-zero in 6,577 records of 70 files; the writer `FUN_1406bd570` writes a literal 0 in that slot). Record names are `0` for most types (`ai` for GAMESTATE_AI, the character's
  name for STATS, `Town state <name>` / `Nest state <name>` for GAMESTATE_TOWN, a faction name for GAMESTATE_FACTION).
- **Record order** is hash-table order, not sorted (**Verified** on files such as a platoon file with ids 20, 18, 17, 16, 15, 19, 14, 13, 11).
- **`byteSize` is not a byte size**: in the sample it is 0 for every record except some that have instances, where it
  equals the instance count (INSTANCE_COLLECTION 9 of 74, INVENTORY_STATE 473 of 498, INVENTORY_ITEM_STATE 26 of 2181 holding a
  nested inventory). The writer fills the slot at +0xB0 of the in-memory record (**Observed**, `FUN_1406bd570`); the loader ignores it.
  GAMESTATE_TOWN_INSTANCE_LIST and BIOMES have instances but byteSize 0, so the rule is "a child count kept by some record
  classes". Meaning for the engine: write 0 or the instance count; both load.
- **`nextId`** in the header is the **highest numeric id** in the file in 64 of 70 files (probe: `nextId - max id` is 0 for 64 files and 1 for 6), i.e. the last id handed out, not the next free one. The loader's use is not traced; write max id (or max id + 1: harmless, **Unknown** whether the game cares).
- **Trailer** after the records ("slot lists", next section). `.platoon` files have none. This is the same trailer the base-game
  `.zone` files have ([zones.md](zones.md#file-layout): "meaning Unknown"; see [Handles](#object-handles-and-slot-lists) for what it is).
- Strings are UTF-8 (the sample has plain ASCII ids and names).

### Object handles and slot lists

Nearly every reference between live objects is a **five-part handle** stored as five ints with a shared key prefix:
`<name>TYPE`, `<name>C`, `<name>CS`, `<name>I`, `<name>S` (examples: `handleC`, `ownedbyTYPE`, `isInsideBuildingS`,
`hometownCS`; in numbered series `internalbuildings-12I`, `usedUniques0handleI`).

| Part | Meaning | Status |
| --- | --- | --- |
| `TYPE` | `FcsRecordType` of the object's class: 0 BUILDING, 1 CHARACTER, 13 TOWN, 34 PLATOON, 85 NEST; **11 (NULL_ITEM) = the null handle**. The null *object* the reader substitutes is `{type 11, 0, 0, 0, 0}`, but a written null handle may keep stale `CS` and/or `S` values (never `C` or `I`): 620 of the 7687 type-11 handles in the sample have a non-zero other part (always `CS` and/or `S`) (e.g. `TI town`, `residents`, `slaver`, `ownedby`) | **Verified**: object C++ class `hand` (RTTI); data at `141e3a600` holds ints 11,0,0,0,0 (read from the exe image); `hand::vfunc_0` (`140031b88`) is a validity test on `type != 11` only (decompile); the reader `FUN_1406bd000` returns that object when `<prefix>I` is absent. Value census over all 70 files: types 0, 1, 11, 13, 34, 85 only |
| `C` | the **container** id the object lives in | **Verified** for buildings: `handleC = Y * 64 + X + 1` for the zone file the building is in (1496 of 1496 GAMESTATE_BUILDING in 13 zone files); for characters `C` is the container id of the platoon: all characters of a `.platoon` file share the PLATOON record's `handleC` and `handleCS` (56 of 56) |
| `CS` | the container's **serial** | **Verified**: 11111 for all zone containers (1496 of 1496); for platoons a random 32-bit number shared by the platoon's characters |
| `I` | slot index of the object in its container | **Verified** within a zone: building `handleI` unique per file (0 duplicates in 1496) and always in that zone's slot list. Platoon members count 1, 2, 3... (**Verified**: the `I` values of the characters of each of the 56 files are exactly 1..n); the platoon's own handle has `I = 0`, `S = 0` (**Verified**, 56 of 56) |
| `S` | the object's random **serial** | **Observed** (`FUN_1400d1c20`, catalogued earlier as `Handle_NewRandomSerial`): set at allocation to `int(rand01 * 4e9)`, hence the negative values seen; used to detect a stale handle |

`FUN_1400d1c20` allocates a handle from a container: `C` and `CS` from the container (+0x24, +0x28), `I` = the first free slot, `TYPE`
from the object's virtual, `S` random, and stores the handle back in the object. The record reader `FUN_1406bd000` reads the five ints of a
handle by prefix and returns the null handle when `<prefix>I` is absent; the writer `FUN_1406bca80` writes them, and when asked to (character handles) first maps
the handle through the **redirect table** (see `5-redirect`).

**Slot lists (the trailer).** After the last record a file may hold `int32 count` + `count` ascending `int32`s, repeated:

- **Zone files**: one list. **Verified** (13 of 13): it contains the `handleI` of every GAMESTATE_BUILDING of the file (and of every interior building
  referenced as `internalbuildings-<k>`), plus further indices (e.g. zone 19.28: 1058 entries for 109 buildings), so it is the list of **occupied slots of the zone's object container**
  (includes slots of objects that have no state record in the file). Zone files without buildings (`zone.21.28`: 5 entries, nextId 6) list slots too (**Observed**: 5 entries for the 4 loose ITEM records plus the collection; their link to the items' handles was not checked, since ITEM records carry no own handle in the saved keys).
  The same trailer in the base-game `.zone` files ([zones.md](zones.md#file-layout)) is therefore the slot list of the placed objects (**Observed**: same writer).
- **`quick.save`**: four lists, all ascending (sizes in the sample 56, 338, 2503, 2969). **Verified**: list 0 equals the sorted `handleC` of the 56 PLATOON records;
  list 1 contains the `C` of every referenced CHARACTER, TOWN and NEST handle and of every GAMESTATE_TOWN `handC` (337 of 337) plus one more (container 511). Lists 2 and 3
  (2503 and 2969 entries, values 1 - 4082, 2433 in both; list 3 contains all 13 saved zone containers, list 2 contains 9 of them) are **Unknown**: probably the other
  global registries (zone-level or interior object containers).
  The lists are written by the object handed to `GameDataContainer::save` as its third argument (the global at `142133f90`, called through its first vtable slot).

## `quick.save`: global state

613 records in the sample (type histogram: WAR_SAVESTATE 103, GAMESTATE_FACTION 103, GAMESTATE_TOWN 337, PLATOON 56, STATE 7,
INSTANCE_COLLECTION 2, GAMESTATE_TOWN_INSTANCE_LIST 1, CAMERA 1, BIOMES 1, RESEARCH 1, TERRAIN_DECALS 1). Field lists are what the sample contains
(**Verified** by counting over all records of the type; optional fields are marked).

### CAMERA (type 56, id 1051): the world record

The record misnamed "camera" holds everything that is not a collection. Floats, ints, bools, strings, vec3/4 and two reference lists:

| Fields | Meaning |
| --- | --- |
| `version` (string) | "1.0.68", the game version that wrote the save (**Verified** equals `currentVersion.txt`); compared by `FUN_14047acf0` |
| `time day`, `time hour`, `time minute` (ints) | **Observed**: in-game clock (sample: day 1, 13:39); how the clock advances and wraps is in [game-loop.md](../game/game-loop.md#the-clock) (**Verified** there). Written by `FUN_14066d7d0` |
| `sky update current time`, `sky update total time`, `sky updating`, `sky ambient color mult`, `sky ambient color mult speed`, `sky clouds density`, `sky clouds density speed` | state of the sky transition ([sky.md](sky.md)); **Observed** |
| `player money` (int), `squads` (1), `members` (1), `floor num`, `formation` | player squad summary (`FUN_140375360` reads them from the player object) |
| `pfaction name` ("Nameless") | the player's faction name; **Verified** it equals the name of the GAMESTATE_FACTION whose `gamedata stringID` is `204-gamedata.base` (record 670) |
| `pos` (vec3), `rot` (vec4 x y z w), `zoom`, `alt` | camera pivot position, rotation and zoom ([camera.md](camera.md)); **Observed** |
| `selected_gui_object*`, `selected_character*`, `selected_characters<k>*` (handles) | current selection; the sample selects the player's character (handle C 1, I 1, TYPE 1) |
| `tracking*` (handle) | the character the camera follows (null in the sample) |
| `area` (string), `biome_text_groud` (BIOME_GROUP id), `biome_text_timer` (float) | the region name banner state; **Verified** `biome_text_groud` resolves to a BIOME_GROUP, `area` is "Okran's Gulf" |
| `usedUniques<k>` (CHARACTER ids), `num usedUniques`, `usedUniques<k>handle*`, `usedUniquesState<k>`, `usedUniquesPlayer<k>` | unique (story) characters already spawned; **Verified** the 3 ids resolve to CHARACTER records |
| bools `bl ditch eject ep feed heal help rescue share shootfirst sit sleep stay`, floats `bs cod gdm ht nnm ps rs`, `formation` | squad behaviour toggles and short-key game toggles (`FUN_1403eb710`, `FUN_1403ee910`): **Observed**; meaning of the short keys **Unknown** |
| reference list `zones` | one entry per zone **that has a file**, written in grid order (X outer, Y inner; `FUN_140375360` loops 64 x 64): target = the list index in **hexadecimal** ("0".."9", "a", "b", "c" for 13 entries), values `(x, y, 1)` = (zone X, zone Y, has-file flag). **Verified**: the 13 entries equal the 13 files of `zone/` (probe), and the key spelling is seen in the sample |
| reference list `mods` | one entry per loaded data file, target = the file's name without extension for the `.mod` files (`Newwworld`, `Dialogue`, `rebirth`) and `base` for `gamedata.base`, values `(-1, 0, 0)`; **Verified** (probe, `GameDatabase.Files`) that the four entries are in the load order of the base game (`gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`). **Unknown** whether `loadGame` compares it with the current mod list (no such string seen) |

### Factions (GAMESTATE_FACTION type 37, WAR_SAVESTATE type 9, two INSTANCE_COLLECTIONs)

The in-memory relation table (entry layout, thresholds -30 / 50, how relations change) is in [factions-squads-towns.md](../game/factions-squads-towns.md#3-relations); the three saved floats per entry are the ones described there.

- One INSTANCE_COLLECTION (id 1055) with 102 instances and one (id 669) with 1 instance (the player's faction). **Verified**: every instance
  targets a GAMESTATE_FACTION record (102 + 1 = 103), position 0, identity rotation, no states.
- **GAMESTATE_FACTION** (all 103 base FACTION records are present, **Verified**: 103 = `OfType(FACTION)`): string `gamedata stringID` (the
  FACTION id), float `prosperity` (1000 at the start, falls below), int `platoonIDs`, int `rank` (102 of them), `known` reference
  list (the player faction only: 11 factions it has met, values `(0,0,0)`), `sq<k>` / `sqtime<k>` (2: unique squad templates),
  `global trust` (`known` and `global trust` occur only on the player faction, `rank` on the other 102, **Verified** by counting; the `sq<k>` / `sqtime<k>` pairs are on United Cities and Dust Bandits and name UNIQUE_SQUAD_TEMPLATE records). Then the **relation matrix**, for k = 0 .. 102 (one entry per faction, including itself): float `relation<k>`, `trust<k>`,
  `trustNeg<k>` and string `relationSID<k>` = the FACTION id the entry is about. **Verified** (probe): 102 factions have 103 entries each = 10,506 `relationSID`, all resolving to FACTION records; the 103rd faction, the player's (`Nameless`, `204-gamedata.base`), has **no** relation table; every other faction's table contains an entry about it.
  `k` is a per-faction counter, not a global faction index.
  - `platoonIDs` is the faction's **platoon name counter**: the next `<n>` for `<faction>_<n>.platoon` (**Verified** greater than the highest
    suffix present for all 14 factions that have platoons; not equal to the number of platoons: only existing platoons are saved).
  - Initial relation values (sample taken early in a new game): explicit entries of the base FACTION `relations` list are kept
    exactly (**Verified**: 223 of 223 explicit entries of the 102 factions that have a table, compared with `GameDatabase`; the base lists hold 224 explicit entries, the extra one belongs to the player faction, which has no table); for a pair A -> B with no explicit A -> B entry the value is `min(default relation of A, default relation of B)` (**Verified** for 10,181 of 10,181 such pairs
    with a probe over every saved entry and the base `default relation` field; an explicit B -> A entry has no effect on A -> B), and A -> A is 100 (**Verified**, 102 of 102 entries). 102 + 223 + 10,181 = 10,506, every saved entry, so the saved matrix of a fresh world is derived entirely from the base FACTION records (a changed relation after play is not covered: **Unknown**). The saved values
    are -100 .. 100 (distinct values -100, -50, -10, 0, 15, 20, 50, 60, 70, 75, 100).
- **WAR_SAVESTATE** (103): string `faction`, float `updatetime` (40), ints `num plats`, `num poss`, `num requests`, `num actives`, `fwc id`
  (all 0 in the sample except `num poss`, 0-16) and `poss sid<k>` (FACTION_CAMPAIGN ids, 77 resolve: the faction's possible campaigns) with `poss time<k>`.
  **Observed**; semantics beyond the names **Unknown**.

### Towns (GAMESTATE_TOWN type 94, GAMESTATE_TOWN_INSTANCE_LIST type 38, STATE type 39)

Town records, nests and the two population pools (`pop`, `popd`, `pop2`, `popd2`) are explained in [factions-squads-towns.md](../game/factions-squads-towns.md#7-towns) and [section 6.4 there](../game/factions-squads-towns.md#64-town-residents-and-roaming-squads).

- **GAMESTATE_TOWN**: 337 records = 304 towns + 33 nests. **Verified**: the 304 town states' `instance` strings (e.g. `9-rebirth-INGAME`) equal the
  instance ids of the base game's town list (304 of 304, `WorldLevelData.Towns()`), i.e. **the town placements themselves are not saved**, only their
  state. The 33 nest states (`is nest` true, name "Nest state Wolf Den 33" ..., handle TYPE 85, empty `instance`) are created by the game at world start: the
  quick.save TOWN_INSTANCE_LIST record (id 673) holds exactly these 33 as instances (target TOWN id such as `16842-gamedata.base`, absolute position,
  state id `<instance id>-S5e`, the `-S<hex type>` rule of [zones.md](zones.md#ids)).
- Fields: bools `discovered explored started recently discovered ded is nest public`, floats `pop pop2 popd popd2 tod`, ints `plevel`, `artifacts count`, handle `hand*`
  (`handTYPE` 13, or 85 for a nest; `handI` = 0), string `faction` (owner, resolves to FACTION), `building material` (MATERIAL_SPEC), `replacementTown`;
  `zzX0`, `zzY0` (the zone the town is in, 227 towns); `artifact_<k>_itm / _lvl / _qty / _man / _mat` (77 towns, resolve to
  weapon/armour/limb-replacement ITEM records, manufacturers, materials); `factions here<k>`; and the reference list **`trade goods`**: 75 ITEM ids, the
  same 75 in the same order in every one of the 304 towns, with `v0` between 7,028 and 12,978 and `v1 = v2 = 0` (**Verified**). `v0` is the town's random market price factor for that item times 10,000 (factors 0.70 to 1.30, `trade profit margins` 0.3 around 1): **Observed** in [economy.md](../game/economy.md#72-market-factor-a-second-random-per-town-price-multiplier), where the Town writer `FUN_140376b80` stores `trunc(factor x 10000)` and the loader `FUN_1403715c0` divides by 10000.
- **STATE** (type 39, 7 records with string ids `nest<n>-S27`, name `0`): ints `BTYPE BC BCS BI BS` = a building handle (TYPE 0); the instance state of nest-owned buildings
  listed on 7 GAMESTATE_TOWN records (**Observed**).

### Research, biomes, decals

- **RESEARCH** (type 21; the research system is described in [buildings-production.md](../game/buildings-production.md#research-and-tech)): float `num finished`, `num currents`, strings `finished<k>` (RESEARCH ids; sample: 1, `5359-gamedata.base`), `current<k>` + `current prog<k>` while researching (the research progress unit is game seconds of bench work, **Observed** in [buildings-production.md](../game/buildings-production.md#research-and-tech); whether `current prog<k>` uses it is **Unknown**).
- **TERRAIN_DECALS** (type 108): int `total` (0 in the sample); decal placements are instances when present (**Observed**, `FUN_1408de5d0`).
- **BIOMES** (type 28, id 1054): the state of the weather and the ambient effects, per biome group.
  - For every BIOME_GROUP in memory (hash order, ordinal `k` = 0, 1, 2, ...; `FUN_1408faf40` -> `FUN_1408fad60`): string key `<BIOME_GROUP id>` = `"<k>"`,
    int `<k>_nestcount`, and its weather controller's state ([weather.md](weather.md)): `<k>_w_s_id` (SEASON id, 65 resolve), `<k>_w_s_end` (the day the season ends),
    `<k>_w_w_id` (WEATHER id, 71 resolve), `<k>_w_fog_a`, wind `<k>_w_wi_dir` (vec3), `_speed`, `_str`, `_time`, `_start`, `_end`, `_udp`, `_ef_str`, `_bu_a_s/_a_e/_s_s/_s_e/_t_s/_t_e` (the
    build-up/transition ramps); and per spawn-table entry `<k>_<record id>_respawn / _count / _start` (respawn timers of nests/animals).
    The `.mod` weather doc says "saves keep the season id and end day" (weather.md): this is where. **Observed** names via `FUN_1409dd250`, `FUN_1409ddfe0`.
  - **Instances** `E<n>` (1463 in the sample, targets "0", "1" or "5" (**Verified**, distinct targets listed), position/rotation reused for state) and keys `E<n>_affected`, `E<n>_affectTimerDelay`, `E<n>_extra`: the saved
    state of **EffectHandler** objects (ambient particle effects: classes `EffectHandlerGlobal`, `Camera`, `Point`, `GlobalPoint`, `Wandering`; the writers are
    their virtual slot 9, e.g. `EffectHandlerWandering::vfunc_9` = `FUN_140104ec0`). **Observed** from the RTTI names; field meaning **Unknown**.

### `5-redirect` (SAVED_STATE, type 40)

Written only when the redirect table is non-empty (absent in the sample, **Observed** in `FUN_140375360` / `FUN_140373f00`): int `count`, then for each
entry `i` a character handle `<i>` (old) and `R<i>` (new). Written character handles are mapped through it so that references to replaced characters
(e.g. after a squad member was re-created) stay valid. **Unknown**: when entries are added.

## `.platoon` files: squads and their characters

One file per PLATOON, named `<faction name>_<n>.platoon`; the PLATOON record in `quick.save` points to it (`content file`).

**PLATOON** (type 34, 56 in the sample, id order unrelated to the files). How squads are created, which roles `squad mem type` stands for (0 SQUAD_1, 1 SQUAD_2, 2 SQUAD_LEADER, 4 SQUAD_SLAVE) and how platoons are stepped is in [factions-squads-towns.md](../game/factions-squads-towns.md#5-creating-a-squad) and [game-loop.md](../game/game-loop.md#factions-and-squads); the AI side of the saved keys (`currentPackage`, `contractjob`, `jobhr`, `towntime`, `homelok`, `runningAwayMode`, `replacementAI`) is the Blackboard of [ai.md](../game/ai.md#jobs-and-player-orders); `money` is the platoon purse of [economy.md](../game/economy.md#2-money-cats): 

- strings `platoon stringID` (= `<faction name>_<n>`, **Verified** 56/56), `faction name`, `faction stringID` (FACTION id, resolves 56/56), `squad template` (SQUAD_TEMPLATE, 56/56),
  `basetown` (TOWN, 51), `currentPackage` (AI_PACKAGE, 55), `map area name` / `map area sid` (BIOME_GROUP, 6), `contractjob`, `mission data`, `replacementAI` (all empty), and, when the platoon is part of a faction campaign, `campaign` (the writer `FUN_140372ef0` has the key behind a campaign lookup; **absent** from all 56 sample records);
- bools `canref dead homelok imprisoned intact is resident never been activated persistent runningAwayMode special`; ints `squad index` (position in the faction's squad
  list: **not** the file suffix, equal for 18 of 56), `sqt` (1 or 2), `char count` (**Verified** equals the number of characters in the file, the file's collection `char count`, and its
  instance count, 56 of 56), `money`, `owned stuff count` + `owned<k>*` (building handles), `slave count`, `towntime` (float), `jobhr`;
- vec3 `position` (world position of the squad), handles `handle*` (TYPE 34), `currenttown*`, `hometown*`, `homebuilding*`, `occupied*`, `separated*`, `target town*`, `mission employer*`, `mission target*`, `mission town*`
  (TOWN/NEST/BUILDING handles or null);
- optional trader data: float `inventory refresh time` (16 of 56); a key `is trader` is read by the loader `FUN_1407ed0f0` and written by `FUN_140372ef0` but is absent from the sample (**Observed** in the two functions).
- `UniquePlatoon` (story squads) saves through `FUN_140377530` (`UniquePlatoon` vtable slot 23), which wraps the normal platoon writer and adds the keys `missionTimer` and `missionRepeatsCount` (**Observed**: strings of that function; no unique platoon in the sample's PLATOON records was checked for them).
- Which platoons are saved: only those that exist in the world at save time. **Observed**: a fresh world had 56 platoons of 14 factions early in the game (the squads of the zones
  around the start); the rest of the population is created later by the game, not stored.

**File contents** (**Verified** for all 56 files): a single INSTANCE_COLLECTION (`char count` int) with one instance per character, then for each character exactly six state records in this
order (**Verified**, 230 of 230): `GAMESTATE_CHARACTER`, `GAMESTATE_AI`, `INVENTORY_STATE`, `MEDICAL_STATE`, `STATS`, `CHARACTER_APPEARANCE`.

- Instance: `target` = the base CHARACTER (194) or ANIMAL_CHARACTER (36) record id (**Verified**: all 230 resolve); `position`, `rotation` (w first) = the character's world position and facing;
  `states` = the six ids above. Instance ids `<n>--INGAME`.
- **GAMESTATE_CHARACTER** (type 36): string `name`, `owner faction ID` (FACTION id, 230/230), `sheath`; bools `is leader carrying escap kidn stealth tn shaved`, `defensive mode / chase mode / non combat mode / passive mode / ranged mode / taunt mode`
  (the player's character only: the stance toggles); floats `age` (0.12 - 1), `decay`, `disguiseblown`, `psts`, `ssct`; ints `speed mode`, `personality`, `squad mem type` (0, 1, 2),
  `slavestate`, `sentence`, `floor`, `in something`, `portrait_serial`, `portrait_index` (1 character), `TI day`, `TI dat0`; handles `handle*` (TYPE 1, C = platoon container), `carrying*`, `isindoors*`,
  `in what*` (the building the character is in), `slaver*`, `TI town*`; player-only `bountyfac0`, `bountyexp0`, `amount0`, `crimes0`, `claim0`.
- **GAMESTATE_AI** (type 67, name `ai`): bool `jobs`; optional `tjob0`, handle `tjobT0*`, vec3 `tjobP0` (a pending target job and its position). When the character has player jobs the AI code also writes `pjobT<i>` entries ([ai.md](../game/ai.md#jobs-and-player-orders), **Observed**; none in the sample).
- **STATS** (type 25, name = the character's name): 44 float fields: the skill levels (names as the base CHARACTER stat fields: `strength`, `dexterity`, `toughness2`, `perception`, `athletics`, `attack`, `defence`, `dodge`, `katana`,
  `sabres`, `heavy weapons`, `poles`, `blunt`, `unarmed`, `bow`, `turrets`, `lockpicking`, `thievery`, `stealth`, `assassin`, `medic`, `engineer`, `robotics`, `science`, `weapon smith`, `armour smith`, `bow smith`,
  `cooking`, `farming`, `labouring`, `tracking`, `swimming`, `climbing`, `endurance`, `mass combat`, `survival`, `ff`, `hackers`, `bluff`, `doctor`, `arrow defence`, ...), `xp`, `free attribute points`, `warrior spirit`.
  Values are floats (e.g. 17.31): the *current* trained levels (stat numbers, offsets and the XP curve: [character-stats.md](../game/character-stats.md#stats)). The writer is `FUN_14064a8f0` (also cited in [animation.md](../animation.md) for the muscle formula).
- **MEDICAL_STATE** (type 57): bools `coma dead unconcious incapacitated`; floats `blood` (humans 50 - 100.7 with 100 = full; the 36 animals 120.6 - 228.8, so the full value is per race, **Observed** over the 230 characters; the capacity also scales with strength, [character-stats.md](../game/character-stats.md#blood-and-bleeding)), `bleeding`, `hung` (hunger, 1.7 - 3 in the sample), `fed`, `KO`; and for each of the character's
  7 limbs `k` = 0 .. 6 the floats `flesh<k>`, `hit<k>`, `bandage<k>`, `rig<k>`, `stun<k>`, `wear<k>`, `hitmult<k>` and string `sid<k>` = the LOCATIONAL_DAMAGE record of the limb (the part model is in [character-stats.md](../game/character-stats.md#body-parts-and-injuries) and [combat.md](../game/combat.md#the-hit-pipeline-medicalsystem)).
  **Verified**: 230 of 230 characters have exactly 7 limbs; for 183 the order is Head `32-gamedata.quack`, Stomach `100`, Chest `101`, Left Arm `28`, Right Arm `29`, Left Leg `30`, Right Leg `31` (names from the base records, all `-gamedata.quack`; 26 + 10 = 36 characters, the animals, have the two arm slots replaced by `4019-gamedata.base` (Left Foreleg) and `4018-gamedata.base` (Right Foreleg); in 11 + 10 = 21 characters Chest `101` comes before Stomach `100`; all four combinations counted by a probe, 183 + 26 + 11 + 10 = 230). All 1610 `sid<k>` resolve to LOCATIONAL_DAMAGE. **Observed** meaning of the per-part floats (the in-memory `HealthPartStatus` offsets, [character-stats.md](../game/character-stats.md#per-part-state-medicalsystemhealthpartstatus), and the save writer `14064d390` / loader `14064d600` use the same offsets, [combat.md](../game/combat.md#the-hit-pipeline-medicalsystem)): `hit<k>` is the part's hit weight (the relative chance to be struck; the sample values 20-150 are the RACE `combat anatomy` weights), `flesh<k>` its flesh HP (sample 59-176), `stun<k>` the stun, `bandage<k>` the bandaged pool, `rig<k>` the extra buffer of the health fraction, `wear<k>` robot wear and `hitmult<k>` the transient hit-focus multiplier. `hung` is the hunger level 0 to 3 (3 = full; **Verified** thresholds in [character-stats.md](../game/character-stats.md#hunger-and-starvation)); that `fed` is the stomach buffer and `KO` the KO timer is **Unknown** (inferred from names only).
- **CHARACTER_APPEARANCE** (type 66): reference list `race` (one RACE id, e.g. `17-gamedata.quack`); strings `head` (HEAD), `hair style` / `beard` (ATTACHMENT, may be empty), `idle stance`; vec3 `Skin Tone`; ints `Age`, `body version`
  (2), `morph index`; bools `sex female`, `in editor`, `from file`; and up to 81 float sliders per character (141 distinct names over the sample; 44 to 81 per record depending on the body type), named like the editor's (`Height`, `Frame`, `Arm bulk`, `Breast size`, `Neck length`, ...; the morph targets
  `high_brow`, `wide_nose`, ... 43 lower-case names, values -0.89 .. 0.72 in the sample; the capitalised sliders (50 names) 0 .. 147 with 100 as the neutral value, **Observed**; 48 `bone_*` / `stick_*` names, -0.41 .. 1.08, for the non-human bodies). This is what [characters.md](../characters.md) and [animation.md](../animation.md#body-shape-sliders)
  consume; the writer is `FUN_140072c50` (its defaults are 100 for each slider), the loader `FUN_14052e6c0`.
- **INVENTORY_STATE** (type 41): no fields; instances (position 0, identity rotation) whose targets are INVENTORY_ITEM_STATE records; instance ids count from 1. Exactly one per character
  (230 inventories, 1212 items directly in them in the sample, **Verified** by a probe walking the instance and state links; platoon files hold 237 INVENTORY_STATE and 1213 INVENTORY_ITEM_STATE, the 1 extra item being inside a backpack). A container item (backpack) has an INVENTORY_ITEM_STATE whose single instance targets another INVENTORY_STATE (the contents): the
  7 INVENTORY_STATE not referenced by a character are the contents of 7 backpacks (**Verified**: 7 unreferenced states and 7 nested inventories reached through items).
- **INVENTORY_ITEM_STATE** (type 42): string `base data sid` (the item's record: ARMOUR 891, ITEM 1018, WEAPON 230, CROSSBOW 14, CONTAINER 26, MAP_ITEM 2 over platoon and zone files: all 2181 resolve in the sample, **Verified** by a probe against `GameDatabase`), `material sid`
  (MATERIAL_SPECS_CLOTHING / WEAPON), `color sid` (COLOR_DATA), `company sid` (WEAPON_MANUFACTURER, also a RESEARCH id for blueprints; 17 are "<id>.TECH.1"-style strings that are
  not records), `section` (**Verified** values: `main 263, legs 194, armour 178, boots 139, back 135, head 120, shirt 120, hip 56, backpack_attach 7, backpack_content 1` among the platoon items),
  `uniform` (FACTION id for faction uniforms; empty otherwise); ints `inventory x`, `inventory y` (cell in the section grid; `back` with `y > 0` is the second back slot, see characters.md), `quantity`, `level`, `item function`;
  floats `quality`, `charges`; bools `in inventory` (always true here), `death`, optional `unique`; handles `ownedby*`, `insideBuilding*` (null for carried items).

### Which part of a character comes from base data

Saved: everything above. **Not saved**: the CHARACTER record's definition (the instance only names it), meshes, animations, the dialogue. Because `STATS`, `CHARACTER_APPEARANCE` and the inventory are saved in full, a
save keeps characters exactly even if the base CHARACTER record changes (a later mod change to the record does not alter an existing character). **Observed**.

## `.zone` files: buildings and loose items of a zone

Only zones that have been loaded get a file (13 of 4096 in the sample; the starting area). The file holds the zone's whole placed-object list, not a diff:

- **`0-buildinglist`** (INSTANCE_COLLECTION, absent when the zone has no building): one instance per building, same layout as the base game's ([zones.md](zones.md#zone-files-zonexyzone)):
  `id`, `target` = BUILDING (or ITEM_PLACEMENT_GROUP) record, absolute X and Z, Y above terrain, rotation w first, `states` = the GAMESTATE_BUILDING id.
  - **Verified** against `WorldLevelData` for 9 zones with a list: the save list contains **every** base placement (0 only-in-base) with identical position (0 differences, a base entry that a later layer cleared, e.g. `2014-Newwworld-INGAME` in zone 19.29, is cleared in the save too), and the same target for all but
    6 placements of the sample (1 - 3 per zone; in zone 19.29 two placements have exchanged their targets at the same positions, so a random variant pick is as likely as destruction: **Unknown**). It adds the placements the game generated at runtime: e.g. zone 19.29: base 78 live placements (+1 cleared), save 1065;
    zone 20.28: base 5, save 224. The generated ones are the **interiors**: **Verified** that 1228 of the 1228 GAMESTATE_BUILDING with `is inside building` true have a runtime id `<n>-zone<X>.<Y>-INGAME`, while of the 268 other states only 1 has one;
    the item placement groups use such ids too. Only 23 of the 1496 saved states and 0 of the 526 ITEM_PLACEMENT_GROUP have an id that also exists in a base `interiors.level`
    (`WorldLevelData.Load(..., includeInteriors: true)`), so the saved interior records are created afresh when the zone first loads, not copied from `interiors.level` (**Observed**: the generator is not traced).
  - Base placement state ids keep their base names (`5339-Newwworld-INGAME-S23`: instance id + `-S` + hex type 0x23 = GAMESTATE_BUILDING); the runtime-created ones use the zone name.
- **`1-itemlist`** (INSTANCE_COLLECTION): loose items lying in the zone: instances target the `ITEM` records of the same file (20 in zone 20.32, 4 in zone 21.28 which has nothing else).
  Base-game zone files have no such collection (zones.md lists only `0-buildinglist`), so it is save-specific (**Observed**; `FUN_1409ffad0` looks `1-itemlist` up by name and empties it in the branch that generates a town's content).
- **GAMESTATE_BUILDING** (type 35; 1496 records in the sample, `destroyed` true for 23: 21 in zone 20.32, 1 in 19.29, 1 in 20.29): see [zones.md](zones.md#zone-files-zonexyzone) for the common fields. In saves: bools `destroyed foliage is complete
  is inside building is public day pause broken power on gets battery locked repeat`, floats `world Y pos` (absolute height), `construction progress`, `0construction mats`, `1construction mats`, `player furniture Y offset`,
  `batterycharge`, `hardness`, `production amount`, `resource mult`, `num crafts`, ints `version` (270, the building state version), `flags`, `floornum`, `serialised interior` (1), `lock lvl`, `current shots`, `craft partial item size`,
  handles `handle*` (TYPE 0, C zone container), `town*` (TYPE 13, the owning town), `residents*` (TYPE 1 / 34 / null), `isInsideBuilding*` (the parent building for interior pieces, null otherwise), `mounted*` (key present on 184, non-null on 22: what is mounted on it),
  `internalbuildings-<k>*` (up to 170 interior buildings), strings `name`, `owner faction ID` (FACTION id or `nofac`; 1495 + 1 in the sample), `residents template` / `squad template` (SQUAD_TEMPLATE, 48),
  `exterior layout name` / `interior layout name` (119), `usagenode_<k>` (452, names of the user nodes in use). 25 states carry a second set of fields with suffix `0` (`handle0*`, `doa0`, `gatecode0`, `hardness0`, `lock lvl0`, `state0`, ...): the
  second part of a gate/door building (**Observed**).
- **ITEM_PLACEMENT_GROUP** (type 83; 526): string `base data sid` (ITEM_PLACEMENT_GROUP record, 526/526), bool `is inside building`, float `respawn time`, int `floornum`, handle `isInsideBuilding*` (TYPE 0): a furniture/item spawn group inside a building and when it next respawns.
- **ITEM** (type 4; 41): a loose item: `base data sid` (ITEM 35, WEAPON 6), `material sid`, `company sid`, `quality`, `charges`, `quantity`, `level`, `item function`, `inventory x/y` (0), bools `bare weapon`, `death`, `in inventory` (false),
  handles `persist*` (the object that owns it for respawn: TYPE 13 TOWN or 85 NEST), `ownedby*`, `insideBuilding*`.
- **INVENTORY_STATE / INVENTORY_ITEM_STATE** in zone files: the contents of chests, shop shelves and containers in buildings (1,335 entries at (0,0,0) in the base files target INVENTORY_STATE, see zones.md); in the sample 968 of
  the 2181 INVENTORY_ITEM_STATE (and 261 of the 498 INVENTORY_STATE) are in zone files, 967 of them in section `backpack_content`, 1 in `out`; the other 1213 / 237 are in platoon files (**Verified**, counted).

## Saved, derived and regenerated

| Content | Where | Status |
| --- | --- | --- |
| Game time, clock, sky transition, weather and season per region | `quick.save` CAMERA, BIOMES | **Verified** present |
| Factions: relations, trust, prosperity, war campaigns | `quick.save` | **Verified** (present in the sample) |
| Town state (discovery, population, trade goods, artifacts, nests) | `quick.save` | **Verified** (present in the sample); town placements, names and shop lists come from `leveldata.level` |
| Squads and all character state (stats, wounds, inventory, appearance, AI) | `.platoon` | **Verified** (present in the sample) |
| Buildings' state, generated interiors, loose items, item placement groups | `.zone` | **Verified** for visited zones |
| Research, unique characters, selection, camera | `quick.save` | **Verified** (present in the sample) |
| Terrain, heightmap, foliage, grass, roads, base building placements, map textures, building and character definitions | data files | **Not in the save** (**Verified**: no such record types; foliage and terrain are rebuilt from the world data, see [foliage.md](foliage.md)) |
| Zones never loaded | nothing | **Observed**: absent from the `zones` list; the game loads them from the data files and generates their interiors and populations when first visited (`FUN_1409ffad0`) |
| Population of unvisited areas | nothing | **Observed**: 56 platoons for the start area only |

Because a visited zone's file stores the full building list, a zone later changed by a mod keeps the saved list: the base layers are loaded first and the save's zone file on top
(**Observed**, `FUN_1409ffad0` loads every data file's zone file and then `zone/zone.X.Y.zone` into the same container; an instance id present in both is overwritten, which matches the FCS merge rules).

## Cross-checks against base data (the sample vs `GameDatabase` of the four base files)

| Check | Result |
| --- | --- |
| Every GAMESTATE_FACTION `gamedata stringID` is a FACTION | 103 of 103 base FACTION records have a GAMESTATE_FACTION (`gamedata stringID`); `relationSID` 10,506 of 10,506 resolve |
| `GAMESTATE_BUILDING.owner faction ID` | 1496 of 1496 resolve to FACTION (one value is `nofac`, which is itself a FACTION record id), plus 25 suffixed `owner faction ID<k>` keys, all resolving |
| INSTANCE_COLLECTION targets in zones | BUILDING 1496, ITEM_PLACEMENT_GROUP 526 (the 1496 equals the GAMESTATE_BUILDING count) |
| `INVENTORY_ITEM_STATE.base data sid` | all 2181 resolve: ARMOUR 891, ITEM 1018, WEAPON 230, CROSSBOW 14, CONTAINER 26, MAP_ITEM 2 |
| `MEDICAL_STATE.sid<k>` | 1610 of 1610 resolve to LOCATIONAL_DAMAGE |
| `TOWN` states | 304 of 304 instances match base town placements; `trade goods` are 75 ITEM records |
| Strings that do **not** resolve | `company sid` / `material sid` values with a `.TECH.<n>` suffix (17), `GAMESTATE_TOWN.instance` (instance ids, not records), BIOMES `E<n>` instance targets (numbers) |

## Functions

Catalogued in `MeitouClient-re/ghidra/catalog-save.tsv`. Main entry points (all in `kenshi_x64.exe`):

| Address | Role |
| --- | --- |
| `FUN_140375360` | `SaveManager::saveGame` |
| `FUN_140373f00` | `SaveManager::loadGame` |
| `FUN_140378a30` | `SaveManager::importGame` |
| `FUN_14047c310` | per-frame request dispatcher (save/load/import/new game) |
| `FUN_14047be70` | autosave timer and slot rotation |
| `FUN_14047acf0` | "major.minor.patch" -> `major<<24 | minor<<12 | patch` |
| `FUN_140471de0`, `FUN_1404711e0`, `FUN_140470fc0`, `FUN_140471d10`, `FUN_140473c30`, `FUN_140473720`, `FUN_140472ac0` | `SaveFileSystem` (construct, temp folder, read path, write path, scan, commit, copy) |
| `FUN_1406bd570` | `GameDataContainer::save` (writes type 15 files); load is `FUN_1406c0b50` ([fcs-mod.md](fcs-mod.md#game-loader)) |
| `FUN_1406bd000`, `FUN_1406bca80`, `FUN_1400d1c20`, `140031b88` | handle read, handle write, handle allocation, `hand::vfunc_0` (type != 11 test) |
| `FUN_14036dde0`, `FUN_14036c070`, `FUN_14036e450`, `FUN_1409ffad0`, `FUN_140a0bef0` | zone save, zone file write, save all zones, zone load, zone-file check |
| `FUN_140372a60` / `FUN_14036f2e0`, `FUN_1409c6890` / `FUN_1409c9dc0` | faction write / read, war state write / read |
| `FUN_140372ef0` / `FUN_1407ed0f0`, `FUN_1406280a0` / `FUN_140626f50` | platoon write / read, character write / read |
| `FUN_140552320` / `FUN_14057cc70` | building state write / read (the reader is catalogued elsewhere as `Zones_CreateDoor`: it also creates doors) |

## Unknowns

- What `byteSize` encodes beyond "child count for some record classes", and whether the game ever reads it (it does not at load).
- The two long slot lists (2503 and 2969 ints) at the end of `quick.save`; the `S` serial's use when a handle goes stale (what happens on a mismatch).
- The roles of BIOMES `E<n>` instances (EffectHandler state fields), the short-key CAMERA floats and bools (`bs cod gdm ht nnm ps rs`, `bl ep`), `TI day/dat0`, `sqt`, `towntime`, `jobhr`, `psts`, `ssct`, and whether MEDICAL `fed` / `KO` are the stomach buffer and the KO timer (the other MEDICAL floats are explained above).
- Whether `loadGame` rejects or adapts to a `mods` list that differs from the current load order (nothing in the sample: single base-game save).
- Player-built buildings, prisoners, dead characters, bounties, trade and mission state: not present in an early-game sample. Their records are the same types (GAMESTATE_BUILDING in `0-buildinglist` with `<n>-zone<X>.<Y>-INGAME` ids is presumed) but untested.
- Exactly when platoon files are written to the working folder between saves (only at save time is certain) and which zones are written on unload ("Delaying saving zone").
- The condition list of the autosave pause, and how `Import Game` selects squads (`FUN_140378a30`, `FUN_140377710`).
- How relations, trust and `trustNeg` change during play (only the fresh-world values are explained), and what the player faction (no relation table, but a `known` list and `global trust`) uses instead.
- Why 6 building placements of the sample have a different target in the save than in the base list (in zone 19.29 two placements have swapped targets, same positions: a random variant pick is a guess).
- Saves made by other game versions or with mods: layout of `mods` and unknown record types would show.

## Implementation outline

1. **Read**: `FcsReader` already reads all three file kinds. Add a `SaveFolder` type in `Meitou.Data` that opens `quick.save`, the `platoon/` files (via PLATOON `content file`) and `zone/` files (via the CAMERA `zones` list)
   through a layered folder lookup (save folder over game data), and a trailer reader (`count + ints`, repeated; zones have 1 list, `quick.save` 4). `LevelFile.Trailer` in the world code already parses the zone case.
2. **Handles**: a `Hand` struct `{ Type, C, CS, I, S }` with `Null` = type 11 (test the type only: stale `CS`/`S` values on null handles are legal), read/write by key prefix; resolve against registries: zone container id `Y*64+X+1`, platoon/town containers by `handleC` (+`CS`).
3. **World state first**: time/sky/weather from CAMERA and BIOMES feeding the existing weather and sky code ([weather.md](weather.md)); factions and the relation matrix (a fresh world is derived from the base FACTION `relations` and `default relation` fields: explicit entry, else min of the two defaults, self 100); town states joined to `WorldLevelData.Towns()` by `instance`.
4. **Zones**: load base zone (`WorldLevelData.Zones`) then overlay the saved zone file by instance id (FCS merge rules, already in `GameDatabase.Apply`); read GAMESTATE_BUILDING, ITEM_PLACEMENT_GROUP, loose items.
5. **Characters**: create characters from each platoon file's collection (instance + six states); STATS and CHARACTER_APPEARANCE feed `Meitou.Data.Characters` (the appearance sliders are the ones the character code already uses).
6. **Write**: `FcsWriter` writes type 15; add the `5-redirect`-free subset first (CAMERA, factions, towns, platoons, zones), generate ids `<n>--INGAME` (state records `-S<hex type>`; PLATOONs are named by platoon name), write trailers from slot occupancy, keep `byteSize` = child count and `nextId` = highest id; write the CAMERA `zones` list in hex-indexed grid order and `mods` as in the load order; write into a working folder and commit by copying
   (the same temp-folder discipline, so a crash never leaves a half-written save). Put saves in `%LOCALAPPDATA%\kenshi\save` so the original game and Meitou can share them (the original needs the same layout; do not rely on a different version string: use "1.0.68").
7. **Test** against the sample-type saves with a skip when no save is present (`Assert.SkipWhen`), e.g. a round trip of every file, handle-prefix validation, and the zone/platoon/CAMERA cross-checks above.
