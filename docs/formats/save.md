# Save games

A save is a folder of FCS containers plus portrait PNG, not one blob. **Verified:** sample `wade`
(70 files, Kenshi1.0.68) and three later base-game saves round-trip through FcsReader/Writer; (checked when written; the real-save tests were removed).
Early-sample absence does not establish absence in later/modded saves. Sources: SaveManager,
SaveFileSystem, GameDataContainer and named binary functions in the evidence/table references.

## File format

Usual header: int32 type15, nextId, recordCount, ordinary FCS records; no mod metadata/checksum.
The CAMERA `version` string is game version. Type17 editor-written platoon files also occur;
accept their real header/sizes/flags rather than forcing type15. [FCS layout](fcs-mod.md).

**Verified/Observed in later saves:** preserve invalid UTF-8 bytes with LosslessUtf8 (byte maps
to lone U+DC00+byte), and exact NaN/denormal float bits. TERRAIN_DECALS IDs can be binary int32
in a string slot. Record string IDs reflect quick/platoon/zone origin and `-INGAME-S<hex type>`;
do not infer numeric ID universally from StringId. Handles have C/CS/I/S/type components;
join by handle and instance slot, not row position. Slots can be sparse; char-count can lag.

`SaveGame.ChildCountSize`: byteSize=child count for instanced INVENTORY_STATE/INVENTORY_ITEM_STATE,
instanced ITEM_PLACEMENT_GROUP, quick.save INSTANCE_COLLECTION and zone1-itemlist; zero for
platoon collections, zone0-buildinglist, BIOMES, town/list and decals. Editor-written type17
uses actual byte sizes. Preserve trailers and unknown records/fields/instances losslessly.

## Folder and state mapping

User location `%LOCALAPPDATA%/kenshi/save/<name>` when User-save-location1, otherwise install/save.
`quick.save`: CAMERA/global clock/camera, factions/wars/relations, town state, research, biomes,
decals, platoon index and redirect records. `platoon/<faction>_<counter>.platoon`: squad and
character state, stats/medical/appearance/inventory, equipment and AI. `zone/zone.X.Y.zone`:
buildings/loose items layered over base placements by instance ID. `portraits_texture.png`:
2048² RGBA, 128px cells; exact serial/index selection remains unknown.

**Verified:** later CAMERA zone lists use hexadecimal keys in X-outer grid order; folders can
contain an extra zone not listed. Wars pair with factions by `faction`/gamedata StringId, not
position. Content-file `platoon/` alone means no platoon file, not an empty filename error.
Every GAMESTATE_BUILDING uses its zone container C, CS11111 and a listed slot. Overlay retains
base placements and saved targets for IDs present in both.

**Research** (RESEARCH record in `quick.save`, **Verified**: five sample saves): float `num finished`, strings `finished<k>` (RESEARCH ids in the
order finished; every sample holds only `finished0` = `5359-gamedata.base`), float `num currents` (0); in progress, strings `current<k>` and floats
`current prog<k>`, and `paid<k>` (**Observed**, [research](../game/buildings-production.md#research)). Meitou (`Saving/ResearchSave`) round-trips all of them;
progress of research taken off the queue is not saved (as the original).

**Blueprint items** (**Verified**: 1954 INVENTORY_ITEM_STATEs in the owner's saves): `base data sid` is the look ITEM (`BLUEPRINT_ITEM`,
`BLUEPRINT_ITEM_ARMOUR`, ...), `company sid` = `material sid` = the RESEARCH id, `item function` 11. Meitou (`SavedKit`) rebuilds them from the research.

**Item function** saved is the constructed value, not the record's: a weapon 5, armour 6, container 4, food of one charge 0 ([item construction](../game/economy.md#units-stacks-and-traders), **Verified** in the constructors, not checked against a save).

**Terrain decals** (TERRAIN_DECALS in `quick.save`, **Verified**: two saves, writer `FUN_1408de5d0`): int `total`; one instance per cell copy of a decal
([blood decals](../game/blood-decals.md)), id = the 1-based counter as raw int32 bytes, target `-`, the 24-byte `DecalInfo` in the seven position/rotation
floats as raw bits: x = local X | local Z << 16 (shorts, 1/1000 of the 288-unit cell), y = half width | half height << 16 (size × texels × 10),
z = rotation (short, rad × 1000), qw = colour ABGR, qx = cell key, qy = type | variant << 8, qz = life (a real float); the upper 16 bits of z and qy are 0 in both.
Meitou (`SaveDecals`) reads it into the blood store on load and writes the store back on save, bit-identical for both saves (**Verified**, read → write → read in a
scratch harness; pinned by `SaveReadTests`); with `Blood` off the loaded record is kept as it was. Meitou-only keys on the same record hold the `bloodsurfaces` marks (`SaveSurfaceDecals`): per mark i from 0, vec3 `meitou sd<i>` (centre), vec4 `meitou sdS<i>` (angle, size, life left, 0), ints `meitou sdK<i>` (type | variant << 8 | floor << 16) and `meitou sdC<i>` (colour ABGR); read until a centre is missing; the original ignores them (keys are read by name).

**Player-built buildings** (Meitou `Saving/BuildingSave`): an instance in the zone's `0-buildinglist` with id `<n>-zone<X>.<Y>-INGAME` and a
GAMESTATE_BUILDING `<id>-S23` carrying `name`, `owner faction ID`, `floornum`, `destroyed`, `flags`, `is complete`, `pause`, `construction progress`,
`<i>construction mats`, `world Y pos`, `handle`, `isInsideBuilding` (key set from the original's writer `FUN_1405547f0`, **Verified** by code; no
sample save holds a player-built building, so a real file was not compared). A finished one with doors also gets the door keys of [locks](../game/locks.md#save-keys)
(the original saves doors the same way for every building, **Observed** by code). Meitou-only `meitou parent` (instance id) names a parent the handle cannot.

**Town-house repair** (Meitou): the original zone instance's GAMESTATE_BUILDING stores `destroyed`, `is complete`,
`pause`, `construction progress`, `<i>construction mats` and ownership. Bool `meitou town construction` keeps it distinct
from a new player-built instance, including after completion; the loader registers its construction state when the town activates.
No extra instance or outpost is created. **Verified** by `HouseRepairTests` against The Hub's original ruined Storm House
and a save/read round trip. Persistent job targets use its zone-qualified identity; queued work orders are saved as the [order queue](#order-queue).

Save runtime state, derive definitions/race/gear/material/terrain from merged base/mod data,
regenerate unsaved foliage/weather particles. Reused declared type IDs do not imply declared
field schema: ITEM, ITEM_PLACEMENT_GROUP, STATS, RESEARCH and BIOMES have saved-state keys.
Record tables below define the relationships and surveyed keys; use [character-stats](../game/character-stats.md),
[economy](../game/economy.md), [AI](../game/ai.md) for field semantics.

## Save/load contract

SaveFileSystem writes through working folders and queues file work; save completion and load
must synchronize with it. Save requests, autosave/quicksave/import and ordered global/platoon/zone
loading are scoped by the binary references. Meitou: `Meitou.Data.Save` folder readers/writers,
`SaveGame.ChildCountSize`, `SaveZone.Overlay`, `Meitou.Simulation.Saving` capture/load mapping.
Capture at a boundary; read/write on workers; replace the active world only after successful load.
[Simulation save coverage](../simulation.md#saves) lists un-restored state.

## Compatibility exceptions

Do not normalize string bytes, NaNs, zero/non-size byteSize, sparse slots or unknown key sets.
Optional character keys include shaved/psts/TI/dat/sentence/sheath/floor; optional platoon keys
include mission/owned/campaign/tags/inventory-refresh-time/slave. Optional town/nest and MEDICAL
limbs keys vary. Test record keys against surveyed saves, retain extension fields, and preserve
editor-authored files. Original acceptance of a save synthesized from scratch is unverified.

### Where saves live

| Setting / path | Meaning |
| --- | --- |
| `%LOCALAPPDATA%\kenshi\save\<name>\` | **Verified** (`save.log` paths, and the sample is there). Used when `settings.cfg` `User save location=1` (default 1) |
| `<install>\save\<name>\` | Legacy location, when the key is 0. **Observed** (`FUN_1403e84f0`): if the key is missing from `settings.cfg` and the install-relative save folder already holds saves (`FUN_14036b680` lists saves; presumably in the install-relative folder, since the setting is not applied yet) the game keeps using the install folder (sets 0); otherwise 1. The UI text: "Save games to user directory instead of the kenshi install path" |
| `settings.cfg` `continue=<name>` | The save the "Continue" button loads (`FUN_140914c40`; the name is read at start-up, `FUN_1403e84f0`, and emptied when its `quick.save` is missing, `FUN_14047af50`); written after every save request that succeeds (kind 1 in `FUN_14047c310`, whatever the silent flag in +0xA4): the Save window, the quicksave key (`FUN_140788100` case `0xe`, name "quicksave") and the autosave (`FUN_14047be70`, "autosave0".."autosave2") all set it, so it names the last save the game wrote. The dispatcher copies the name into the manager and writes `continue=` only for kind 1, so a load, import or new game request does not (**Verified**, `FUN_14047c310`); whether `loadGame` itself (`FUN_140373f00`) changes the manager's in-memory name is **Unknown**. Sample: `continue=wade` |
| `settings.cfg` `autosaveindex` | Autosave slot counter (see [Autosave](#autosave-fun_14047be70)); absent in the sample (no autosave happened) |
| `<install>\save.log` | Log of the save file system (**Verified**: the sample's saves are all logged). In the install folder even when saves are in the user folder |

### Folder layout

| Item | Status |
| --- | --- |
| `.save`, `.platoon`, `.zone` all parse with `FcsReader` as file type 15 | **Verified** (70/70 files of the sample, to the exact end of records; `.zone` and `quick.save` have a trailer, below) |
| `portraits_texture.png` | **Verified** 2048 x 2048, 8-bit RGBA (PNG header). **Observed** (`FUN_140414380`): cells of 128 px, 16 x 16 portraits, class `PortraitImage`; written with the save. GAMESTATE_CHARACTER `portrait_serial` / `portrait_index` select the cell (**Unknown** exactly how) |
| Platoon file name | **Verified** (56/56): `<faction name>_<n>.platoon` where `<n>` is a per-faction counter; the PLATOON record's `content file` is `platoon/<platoon stringID>.platoon` |
| Zone file name | **Verified** (13/13 and by `FUN_14036c070`): `zone.<X>.<Y>.zone`, X and Y the zone grid indices ([terrain.md](terrain.md#world-coordinates)) |

### Folder layout — layout/example

```
<name>/
  quick.save                 global state                      (1 file, 2.26 MB in the sample)
  portraits_texture.png      character portrait atlas          (2048 x 2048 RGBA)
  platoon/<faction>_<n>.platoon   one file per squad           (56 files, 6-120 KB)
  zone/zone.<X>.<Y>.zone     one file per loaded zone          (13 files, 2 KB - 1.4 MB)
```

### Save requests (`FUN_14047c310`)

A queued request (kind at +0xA0 of the request object) is run on the next frame; **Verified** (`FUN_14047c310`): a countdown at +0xA8 (decremented
per call, request untouched) and a save/load thread still running (state +0x1E0 of the progress window object `DAT_14212ec18` above 0; 1 running, 2 done, 0 idle,
`FUN_1404742b0`) make the call return without consuming the request, which is reset to 0 only after handling. A request made during a save or load therefore
waits and runs when it ends (Meitou: `HandleNewGame` keeps `newGameRequested` while `Busy()`). **Observed**:

| Kind | Action |
| --- | --- |
| 1 | `SaveManager::saveGame` (`FUN_140375360`), then write `continue=<name>` to `settings.cfg` |
| 2 | `SaveManager::loadGame` (`FUN_140373f00`) |
| 3 | `SaveManager::importGame` (`FUN_140378a30`, flags in +0xA4): new world, keeps the player squads and parts of the chosen save ([import](#importing-a-game-savemanagerimportgame-fun_140378a30)) |
| 4 | New game: sets the version string to "1.0.68", reloads the game data and starts the chosen NEW_GAME_STARTOFF |

### Autosave (`FUN_14047be70`)

Every frame from `GameWorld::vfunc_0` (**Verified**, decompile): while `Auto save` is on, no zone is loading and `DAT_142134690+0x298`
is null, real dt adds to a timer (paused time not counted). Past `Auto save time (minutes)` x 60 it shows "Autosaving...", and the next
frame saves slot `(autosaveindex + 1) mod 3` as `autosave<N>` and stores `autosaveindex` in `settings.cfg`. Meitou (`GameHost.Autosave`):
the same, with the index in Meitou's user config and the slots beside its quicksave; the `+0x298` gate is not modelled.

### Importing a game (`SaveManager::importGame`, `FUN_140378a30`)

**Verified** (decompile of `FUN_140378a30`, `FUN_140377710` (zone files), `FUN_14047bad0` (the window's press) and the callees named below).
Import is a new game that brings part of an old save. The window's ticks are the flag bits ([window](../game/ui-screens.md#title-screen-and-its-windows)):
Reset squad positions 0x1, Buildings 0x4, Research 0x8, Dead NPCs 0x10, Relations 0x20; 0x2 is always added and nothing tests it. In order:

1. `<save>/quick.save` must exist, else the request fails. The version string becomes "1.0.68" and the whole world is reset as a new game does
   (`FUN_14036cb80`: factions, platoons, research, unique table, player name = the `204-gamedata.base` faction's name or "Nameless"). Factions are made from the
   FACTION records with **no** saved faction states. While background loading is busy the request is retried next frame.
2. The old `quick.save` is read into the save container. The clock hour is set to 10, then step 8 overwrites the clock.
3. Towns come from the level data as in a new game. **Buildings**: the save's towns of the player faction that were `discovered` and are not nests are loaded
   (the outposts the player made are in `GAMESTATE_TOWN_INSTANCE_LIST`, `FUN_140935120`, `FUN_1403722d0`); a player town inside another player town's radius is removed
   (`FUN_14092ae80`, also run by `loadGame`, **Verified** by decompile: in town-list order, each town owned by a player faction is set to the player faction, then the nearest
   other town (XZ) that is not a NEST_MARKER (type 8) and is owned by the player faction is found; when this town's centre lies inside that town's radius (slot 0x2a0) it logs
   "Town too close. Removing" and "Player town '...' withing boundaries of ...", is deleted and leaves the list, so of two such towns the earlier one goes. What becomes of its
   buildings is **Unknown**).
4. Always (`FUN_140372560`): every non-nest town of the save gives its `discovered` and `explored` to the new town; player towns are set discovered and explored. **Dead NPCs**
   also applies the save's `replacementTown`. No town at all logs "No towns loaded." Then the towns get their [artifacts](../game/economy.md#artifacts) as in a new game.
5. **Buildings**, zone files (`FUN_140377710`; a save whose version is below 0x245 uses `FUN_140377d10`, not read): each `zone/*.zone` keeps only the building instances that are removal entries
   (empty target) or whose state is owned by the player faction; the others, the zone's loose items and every other record are not copied. A zone with a kept instance gets the
   root bool `imported` = true and its file in the new working folder; at load that bool sets `ZoneMapContent::newGameFirstTimeLoaded` (`+0xa8`, `FUN_1409ffad0`), so the zone
   counts as fresh despite its file and its towns make their residents ([zone files](#zone-files)). The owner test is on the state's owner string
   (`FUN_140387dd0`; the exact key **Observed** as `owner faction ID`).
6. **Dead NPCs**: the unique table is read again (`FUN_1409aa270`) keeping the entries that are not alive (state 0 dead, 2 imprisoned) with their player flag and handle; a
   non-player imprisoned entry whose handle test passes is unclaimed (polarity **Unknown**). Without the tick the table is empty at this point.
7. The player's squads: the save's PLATOON records whose `faction stringID` is the player faction and whose name is not `__DEAD_SQUAD__`. The `squad index` is erased; with
   **Reset** the platoon `position` is first (500, 0, 51000). The squads are loaded (`FUN_1407f88c0`; one that does not resolve shows "One of your squads has somehow loaded off
   the map, reload your game with [reset positions] selected..."), each squad's platoon file is copied whole from the save (only if absent), and every `unique` CHARACTER in it is
   entered in the unique table, claimed, with a null handle. Members, inventories, medical state and stats are therefore the old files'.
8. No player squad left: `platoon/Nameless_0.platoon` of the new working folder is loaded, "Load game error! Your squad has been salvaged. Awarded 10000 cats as compensation." and +10000.
9. **Research**: off, research is reset and `_Default Start` is completed; on, the first RESEARCH record is read (`FUN_1408360f0`). Then from CAMERA: `time day/hour/minute` and the sky
   transition (`FUN_14066db50`), money = max(0, `player money`) (+10000 when salvaged), the faction name = `pfaction name` (empty: as step 1), the camera pose
   (`FUN_1407ade60`) and the squad option bools (`FUN_1403ebf50`).
10. **Reset**: the camera goes to (-43500, 0, -6625) and `FUN_14036b370` puts every player squad at that point + (i, 0, i), i the squad's index in the list (an inactive one is
    activated there). What lies there is **Unknown**.
11. **Relations**: each GAMESTATE_FACTION of the save is loaded into its faction's relation object with the import flag set (`FUN_1409ef4e0`; which fields the flag skips is
    **Unknown**). Without the tick the factions keep their FACTION record values.
12. Last, `FUN_1406ba3a0` (per faction, activates one squad failing a 100-unit test; meaning **Unknown**) and a rescan of the save folder.

Not carried (**Verified** by absence): the NEW_GAME_STARTOFF (only request kind 4 applies one), every other faction's platoons and NPCs, nests, others' buildings and zone items, the
BIOMES weather (whether it is regenerated elsewhere is **Unknown**), the war states. The difficulty is not read from the save: `importGame` never calls the reader (only `loadGame`
does), so the game runs with the nine difficulty globals as they stand, which are the save's values only if the Advanced options window was opened with that save selected
(`FUN_14047ab70` copies the row's block, `FUN_140912f80`) and whatever the sliders then hold.

Meitou (`SaveImport`, `GameHost.BeginImport`): the loaded save is cut down to the same parts in memory (`SaveImport.Filter`) and then loaded by `SaveLoader` as any save, so what the loader
makes is what a load of that smaller save makes. Engine choices: the squads' `squad index` is renumbered from 0 in their old order; with Reset the members stand on a line 2 units apart at
their squad's point, on the ground there (`PlaceOnGround`); a save without a player squad is refused instead of salvaged (where the new working folder's `Nameless_0.platoon`
comes from with no start applied is **Unknown**); imprisoned uniques not in the player's squads are dropped from the table
(their characters are not carried) instead of tested by handle; the finished research of "off" is `_Default Start` only; the weather and decals start fresh; war states are dropped with
the NPC platoons. A save older than 0.90.0 drops the reset, buildings, research and dead-NPC ticks (`SaveImport.AllowedFor`; the original's window disables and unticks them,
`FUN_14047cee0`, comparing the CAMERA `version` packed as a << 24 | b << 12 | c, unparsable = 0; Meitou's window does the same on row selection, `PlayerInterface.OldImportSave`). Not built: the "too close" player-town removal (a new outpost is made only with no player town within 5000, [outposts](../game/buildings-production.md#placement), far outside
any radius, so only an edited save reaches it) and the `imported` marker (the NPC platoons are left out, so every town makes its residents anyway). The relation table is shared by the
data, so an import without Relations first sets it back to the FACTION values (`FactionRelations.Reset`).

### Object handles and slot lists

| Part | Meaning | Status |
| --- | --- | --- |
| `TYPE` | `FcsRecordType` of the object's class: 0 BUILDING, 1 CHARACTER, 13 TOWN, 34 PLATOON, 85 NEST; **11 (NULL_ITEM) = the null handle**. The null *object* the reader substitutes is `{type 11, 0, 0, 0, 0}`, but a written null handle may keep stale `CS` and/or `S` values (never `C` or `I`): 620 of the 7687 type-11 handles in the sample have a non-zero other part (always `CS` and/or `S`) (e.g. `TI town`, `residents`, `slaver`, `ownedby`) | **Verified**: object C++ class `hand` (RTTI); data at `141e3a600` holds ints 11,0,0,0,0 (read from the exe image); `hand::vfunc_0` (`140031b88`) is a validity test on `type != 11` only (decompile); the reader `FUN_1406bd000` returns that object when `<prefix>I` is absent. Value census over all 70 files: types 0, 1, 11, 13, 34, 85 only |
| `C` | the **container** id the object lives in | **Verified** for buildings: `handleC = Y * 64 + X + 1` for the zone file the building is in (1496 of 1496 GAMESTATE_BUILDING in 13 zone files); for characters `C` is the container id of the platoon: all characters of a `.platoon` file share the PLATOON record's `handleC` and `handleCS` (56 of 56) |
| `CS` | the container's **serial** | **Verified**: 11111 for all zone containers (1496 of 1496); for platoons a random 32-bit number shared by the platoon's characters |
| `I` | slot index of the object in its container | **Verified** within a zone: building `handleI` unique per file (0 duplicates in 1496) and always in that zone's slot list. Platoon members count 1, 2, 3... (**Verified**: the `I` values of the characters of each of the 56 files are exactly 1..n); the platoon's own handle has `I = 0`, `S = 0` (**Verified**, 56 of 56) |
| `S` | the object's random **serial** | **Observed** (`FUN_1400d1c20`, catalogued earlier as `Handle_NewRandomSerial`): set at allocation to `int(rand01 * 4e9)`, hence the negative values seen; used to detect a stale handle |

**Base buildings keep the level data's handle.** **Verified** (census of saves 2, wade, 1): every saved base-building state whose level-data
GAMESTATE_BUILDING (the zone files of `leveldata`) has a `handle` carries exactly that handle (3345, 2994 and 178 of as many; the rest of the saved
states are generated interiors and buildings without a level state), and every platoon `homebuilding` names one of them (339, 313, 20 and save 3's 22
of as many equal the level handle). So a building of a zone the save never wrote is named by its level state's handle. A handle allocated in play is
not predictable (**Verified** by code: `I` from a rotating free-slot cursor `FUN_1400cd210`, `S` from `rand()` before the zone's `srand`).

### CAMERA (type 56, id 1051): the world record

| Fields | Meaning |
| --- | --- |
| `version` (string) | "1.0.68", the game version that wrote the save (**Verified** equals `currentVersion.txt`); compared by `FUN_14047acf0` |
| `time day`, `time hour`, `time minute` (ints) | **Observed**: in-game clock (sample: day 1, 13:39); how the clock advances and wraps is in [game-loop.md](../game/game-loop.md#frame-clocks) (**Verified** there). Written by `FUN_14066d7d0` |
| `sky update current time`, `sky update total time`, `sky updating`, `sky ambient color mult`, `sky ambient color mult speed`, `sky clouds density`, `sky clouds density speed` | state of the camera's 30 s sky transition ([sky.md](sky.md)); **Observed**. The regions' weather is in [BIOMES](#biomes-type-28-quicksave-weather-per-region) |
| `player money` (int), `squads` (1), `members` (1), `floor num`, `formation` | player squad summary (`FUN_140375360` reads them from the player object) |
| `pfaction name` ("Nameless") | the player's faction name; **Verified** it equals the name of the GAMESTATE_FACTION whose `gamedata stringID` is `204-gamedata.base` (record 670) |
| `pos` (vec3), `rot` (vec4 x y z w), `zoom`, `alt` | camera pivot position, rotation and zoom ([camera.md](camera.md)); **Observed** |
| `selected_gui_object*`, `selected_character*`, `selected_characters<k>*` (handles) | current selection; the sample selects the player's character (handle C 1, I 1, TYPE 1) |
| `tracking*` (handle) | the character the camera follows (null in the sample) |
| `area` (string), `biome_text_groud` (BIOME_GROUP id), `biome_text_timer` (float) | the region name banner state; **Verified** `biome_text_groud` resolves to a BIOME_GROUP, `area` is "Okran's Gulf". `biome_text_timer` is the banner's 30 s cooldown, `biome_text_groud` the group last tracked (`FUN_140728af0`, empty for none), `area` the name of the group under the camera pivot, empty for `NONE` (`FUN_140375360`); [Biome banner](../game/ui-screens.md#biome-banner). Meitou writes and reads all three |
| `usedUniques<k>` (CHARACTER id), `num usedUniques` (int), `usedUniques<k>handle{TYPE,C,CS,I,S}` (ints), `usedUniquesState<k>` (int), `usedUniquesPlayer<k>` (bool) | the unique-NPC table ([unique characters](../game/factions-squads-towns.md#unique-characters)): state 1 alive, 0 dead, 2 imprisoned; player = player involved; handle = the live character. Written for every entry except unclaimed state-2 ones (the `prisoners` uniques, re-derived at load); on load an entry whose id does not resolve is dropped. Import (`importGame`) skips state-1 entries and unclaims non-player state-2 ones by their handle test (polarity **Unknown**). **Verified** by code (`FUN_1409a8660`, `FUN_1409aa270`) and 9 saves (302 entries, all state 1, player false) |
| bools `bl ditch eject ep feed heal help rescue share shootfirst sit sleep stay`, floats `bs cod gdm ht nnm ps rs`, `formation` | squad behaviour toggles and short-key game toggles (`FUN_1403eb710`, `FUN_1403ee910`): **Observed**; the floats `bs cod gdm ht nnm ps rs` and bools `ep bl heal` are the new-game advanced options (**Verified**: saver `FUN_1403ee910`, loader `FUN_1403eef20`; keys, defaults and load rules in [character-stats](../game/character-stats.md#new-game-advanced-options-that-change-these-mechanics)); Meitou reads and writes them; the squad toggles' keys and the int `formation` (the player faction's squad formation) are in [squad AI options](../game/ai-tasks.md#squad-ai-options) |
| reference list `zones` | one entry per zone **that has a file**, written in grid order (X outer, Y inner; `FUN_140375360` loops 64 x 64): target = the list index in **hexadecimal** ("0".."9", "a", "b", "c" for 13 entries), values `(x, y, 1)` = (zone X, zone Y, has-file flag). **Verified**: the 13 entries equal the 13 files of `zone/` (probe), and the key spelling is seen in the sample |
| `meitou seed` (string) | Meitou-only: the world seed in decimal, written on every Meitou save and restored before the world's first tick on load (so the nests it did not write roll again where they were, [Nests](#nests-town-list)); a save without it keeps the launch seed. The original's keys are read by name, so it is ignored (as the other Meitou-only keys) |
| reference list `mods` | one entry per loaded data file, target = the file's name without extension for the `.mod` files (`Newwworld`, `Dialogue`, `rebirth`) and `base` for `gamedata.base`, values `(-1, 0, 0)`; **Verified** (probe, `GameDatabase.Files`) that the four entries are in the load order of the base game (`gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`). **Unknown** whether `loadGame` compares it with the current mod list (no such string seen) |

### BIOMES (type 28, `quick.save`): weather per region

Written per region by `FUN_1408fad60` (region list walk `FUN_1408faf40`), read by `FUN_1408fae70` (walk `FUN_1408fafc0`); the weather part by
`FUN_1409ddfe0` / `FUN_1409de200`, the weather instance by `FUN_1409dd250` / `FUN_1409dd5e0`. **Verified** by code and by a census of the owner's six
saves (71 regions each, mods included):

| Key (`p` = prefix) | Meaning |
| --- | --- |
| string `<BIOME_GROUP id>` = `p` | the region's prefix: its index in the save's walk of the region table. The loader finds a region by its id and reads `p`'s keys; a region the record lacks keeps a fresh schedule. So `p` only pairs keys within one save |
| int `p_nestcount` | the BIOME_GROUP's `num nests` (equal in 71 of 71 regions, all saves) |
| string `p_w_s_id`, int `p_w_s_end` | current SEASON id and the day it ends (absolute day count). An id the region does not list: season 0 from now, a new weather |
| string `p_w_w_id` | current WEATHER id; one the season does not list: a new weather is chosen |
| int `p_w_wi_start`, `p_w_wi_end` | weather start / end in game minutes, `day × 1440 + hour × 60 + minute` (start ≤ now < end in 426 of 426) |
| float `p_w_wi_str`; int `p_w_wi_ef_str` | strength; the WEATHER's `effect strength min`, truncated to int (426 of 426) |
| float `p_w_wi_speed`, vec3 `p_w_wi_dir` | current wind speed and direction; direction = `(cos a, 0, −sin a)` of the angle below (rotation about Y of +X; every settled region) |
| int `p_w_wi_udp` | game minute of the next wind update |
| int `p_w_wi_bu_t_s` / `_t_e`, float `_bu_s_s` / `_s_e`, `_bu_a_s` / `_a_e` | the last wind change: start/end minute, speed and angle (radians) at each end, lerped linearly. On load one not running at the current minute is marked done and its times zeroed |
| float `p_w_wi_time` | game-speed seconds the weather has run (dt × speed, `FUN_1409dc0e0`); **Observed** = (now − start) × 20/11 in 57 of 71 regions of the later saves, larger for a new game's first weathers |
| float `p_w_fog_a` | written for the camera's region only (one per save, 0 in every sample): a fog value read from and restored into the fog manager; meaning **Unknown** |
| `<k>_<effect group id>_count` / `_start` (int), `_respawn` (float) | weather effect groups, not nests (**Verified** by code, `EffectGroup::vfunc_0/1` `FUN_140103700` / `FUN_140104290`): the id is the group's record (`40561-weather_otto1.mod` = `weather_rain1`), `_start` its first `E<n>` instance, `_count` how many follow, `_respawn` its timer; the writer puts `_respawn` in the float map and the loader reads the int map, so it always loads 0 |

Meitou (`Data/Save/SaveWeather`, the host's `WorldWeather.Restore`): reads every region's season, weather, strength and wind into `WeatherRegionSnapshot`
(heading = −angle; the fog fade is not saved, so a restored weather starts faded in) and writes them back at save time, with `_w_wi_time` as
(now − start) × 20/11 and a new region's `_nestcount` from its BIOME_GROUP. Effect-group keys, `_w_fog_a` and the `E<n>` effect instances are carried as read.
The CAMERA's sky transition is written as finished (`sky updating` false, both times 30, the current sky multiplier and cloud density); the loader snaps the sky.

### Saved, derived and regenerated

| Content | Where | Status |
| --- | --- | --- |
| Game time, clock, sky transition, weather and season per region | `quick.save` CAMERA, BIOMES | **Verified** present |
| Factions: relations, trust, prosperity, rank, war campaigns | `quick.save` | **Verified** (present in the sample); Meitou-only `meitou global trust`, `meitou war<k>` ([trust and war](#faction-trust-war-flags-bed-rent-and-turrets)); int `rank` on each non-player faction ([faction rank](../game/factions-squads-towns.md#32-faction-rank)); campaigns: [WAR_SAVESTATE](#war_savestate-type-9-faction-campaigns). Special squads in GAMESTATE_FACTION: `sq<i>` (UNIQUE_SQUAD_TEMPLATE id), float `sqtime<i>` (replacement timer), handle list `<i>:sq list` (live UniquePlatoons) (`FUN_1402deea0` / `FUN_1402df0c0`; `sq`/`sqtime` **Verified** in 9 saves: United Cities and Dust Bandits, `sqtime0` 0; `:sq list` from code only, empty or absent in every sample); PLATOON bool `special` marks a UniquePlatoon, which adds `missionTimer` and `missionRepeatsCount` ([special squads](../game/factions-squads-towns.md#unique-characters)) |
| Town state (discovery, population, trade goods, [artifacts](../game/economy.md#artifacts), nests) | `quick.save` | **Verified** (present in the sample); town placements, names and shop lists come from `leveldata.level` |
| Squads and all character state (stats, wounds, inventory, appearance, AI) | `.platoon` | **Verified** (present in the sample) |
| Buildings' state, generated interiors, loose items, item placement groups ([keys](../game/factions-squads-towns.md#loose-items-in-buildings)) | `.zone` | **Verified** for visited zones |
| Locks and doors (`lock lvl`, `locked`, `state<i>`, `wants lock<i>`, ...), cage/bed occupancy (`in something`, `in what`) | `.zone`, `.platoon` | **Verified** keys: [locks](../game/locks.md#save-keys) |
| Research, unique characters, selection, camera | `quick.save` | **Verified** (present in the sample) |
| Terrain, heightmap, foliage, grass, roads, base building placements, map textures, building and character definitions | data files | **Not in the save** (**Verified**: no such record types; foliage and terrain are rebuilt from the world data, see [foliage.md](foliage.md)) |
| Zones never loaded | nothing | **Observed**: absent from the `zones` list; the game loads them from the data files and generates their interiors and populations when first visited (`FUN_1409ffad0`) |
| Population of unvisited areas | nothing | **Observed**: 56 platoons for the start area only |

### Character appearance (CHARACTER_APPEARANCE in `.platoon`)

**Verified** (dump of save 2's `Nameless_0.platoon`): the same key set as a `.bod2` body file ([characters](../characters.md#body-files-bod2)):
bools `sex female`, `in editor`; ints `Age`, `body version` (2); floats: every body/face slider of the race's editor limits (`Height`, `Waist`...), the template's
other sliders at 100 (`Brow height`, `Cheekbone`... not in the human XML), face pose weights by pose name (`wide_nose` 0.02, `close_eyes` -0.64...), `Hair Colour`,
`Hair Saturation`, `Hair Brightness` (HSB x 100), `Posture`, a float `Age` besides the int; vec3 `Skin Tone`; strings `head` (HEAD), `hair style`, `beard`
(ATTACHMENTs, mod records allowed), `idle stance`; reference `race`. Meitou (`Saving/SavedKit`) draws every loaded character (player and NPC) from it: the record
is the look's appearance, worn items its clothes; a character with no saved items keeps clothes rolled from its serial but the saved body and face. A record
with only race and flags (no head, no floats) is not a look and the look is rolled. The record is written back only for a new character (the generated look) or
one changed in the character editor (its keys replaced by the edited copy's, so the key set and order stay the original's).

### Limbs (MEDICAL_STATE `limbs`)

**Verified** (writer `FUN_1400cfb50`, reader `FUN_1400d0190`; METARU and quicksave: 6 records): int `limbs` = state of limb slot k (0 L arm,
1 R arm, 2 L leg, 3 R leg) in bits 2k..2k+1, states 0 original, 1 stump, 2 replaced, 3 crushed (`160` both legs replaced, `16` L leg stump,
`4` R arm stump). Only when some slot is 2, a reference list `limbs`: per replaced slot, value 0 = the slot, target = an INVENTORY_ITEM_STATE in
the same file (`section` `left_arm_replace` / `right_arm_replace` / `left_leg_replace` / `right_leg_replace`, `base data sid` the
LIMB_REPLACEMENT, `level`, `quality`, `dam`, `wear`, `stun`). These items are in no INVENTORY_STATE. Reader: state 1, or 2 without a resolvable
item, becomes a stump; 2 with an item fits it; 3 is kept. Meitou: `SaveLimbs` (data), `Saving/SavedLimbs` (medical state both ways).
Meitou-only bool `meitou decapitated` on MEDICAL_STATE, written only when true ([decapitation](../game/character-stats.md#decapitation-meitou)); the original ignores it.
A removed prosthetic in an inventory keeps its `dam`, `wear`, `stun` floats (`SaveItem.Damage`/`Wear`/`Stun`; written when non-zero or
already in the record, an engine choice: whether vanilla writes them for every item is **Unknown**).

### Player platoons (Meitou mapping)

One PLATOON per player squad, `squad index` its place in the squad order (**Observed**: 0 on the samples' one player platoon). Its `platoon stringID`
(also the record's string id and the file's name) stays the faction's name and counter (`Nameless_0`); a rename writes the record's name (the squad box
copies its text into the platoon's record name, `FUN_14048df00`, **Observed**; 962 PLATOON records of two saves all have name = `platoon stringID`, none
renamed). Meitou keeps each loaded squad on its own file (`Squad.SaveName`) and writes a squad made since as a new platoon, empty squads included; a
character that changed squads is written anew in its new platoon and leaves the old file; a removed squad's platoon is deleted.

### NPC platoons (Meitou mapping)

Every saved NPC platoon with a `basetown` naming a placed town is restored (a characterless town-bound one is skipped); town-bound ones (template does not travel) are pinned to their town, and a town holding any
is made of them alone: it makes no residents, default residents, housemates or bar squads (**engine rule**, after the original's: a zone with a save file makes no town
population, [Zone files](#zone-files) Load; Meitou keys it on the saved platoons). A town Meitou made and unloaded before saving keeps its squads as stored
platoons, which the save writes like the town's squads in the world, so a Meitou save holds the residents of every town it made a zone file for
(**Observed**, headless: a new game at The Hub and Squin, and base save 2 at Stack/Mongrel and Shark/Admag, each town left so it unloaded, then save → load → save → load:
the same characters by name, record and stats in each town). A count per
template would not do: housing spends a building on a slot whose rolled squad is empty (68 of 200 `hub squatters` plans), so a town's saved squads are fewer than its entries' `v0`, and the rest rolled
again at load makes new squads (**Observed**, headless new game with town buildings: The Hub 4 of 7 `hub squatters` slots, 36 NPCs; reloaded 42, three more). Each platoon is made with its town (when the town's zone is active, as the
town's fresh squads are). On save, a restored platoon is rewritten
only once loaded (its characters from the world), otherwise only its position/package; a squad the population made is written as a new PLATOON with the
`SaveBuilder` key set plus `basetown` (town id), `hometown` (the town state's `hand`), `homebuilding` (below), `is resident`, `sqt`, `map area sid` / `map area name`
(the BIOME_GROUP of its cell) and `currentPackage`. Dialogue-given AI (`AiSave`, every written or rewritten platoon but a wild one): `contractjob` (the contract
AI_PACKAGE id, "" none), `jobhr` (float, the game hours it ends at: the start time + its hours, 10 min under 1 h; `FUN_140272240`), `replacementAI` (the `change AI` package,
"" the template's), read and written by `FUN_140272c20` / `FUN_14026c2d0` (**Observed**); the contract loads with no target, as the original's. **Observed** (headless load of saves 1, 2,
wade): NPCs within 5 units of their saved positions (save 2: 4 of 107 differ in Y only, flat test ground); a Meitou save → load restores them
exactly with no fresh squads for save 2 and a new game (with town buildings: 36 NPCs after two save → load cycles for a new game at The Hub; saves 1, 2, 3, wade keep
86, 116, 18, 114). A pinned platoon is homed in the building its `homebuilding` handle names (state `<id>-S23`
of zone C-1, X = c % 64, Y = c / 64; ids repeat across zones; a zone without a file: the building whose level state has that handle); that building is taken
out of the town's free homes. **Unknown:** how the original reuses the saved package.

PLATOON keys (**Verified**):
- `sqt` is the town pool the platoon counts against (platoon +0xa8; by code: written by `FUN_140372ef0`, read by `FUN_1407ed0f0`, setter `FUN_1409c44b0` makes 0 into 1
  and 3+ into 2, default 2): 1 residents (and bar squads), 2 roaming; every wild platoon of the samples has 2 (nests have only pool 2).
- `persistent` (platoon +0x115, [persistent platoons](../game/factions-squads-towns.md#1-object-model)) is true on every `sqt` 1 platoon (census of saves
  1, 2, 3, wade, new, quicksave and three autosaves: residents, bar squads and the player's, 0 exceptions) and false on most `sqt` 2 ones (some `sqt` 2
  town platoons are persistent, why **Unknown**; an occupation sets it, [ai.md](../game/ai.md#package-classes)). Meitou writes true with `sqt` 1 and false
  with 2 but while the platoon's package is an occupation; written false, the original would delete a resident squad when it leaves the loaded area.
- `is resident` (platoon +0x116, written by `FUN_140372ef0` from the object) is set exactly on the platoons with a `homebuilding` in the census of saves 1, 2, 3, wade
  (0 exceptions); whether those saves hold never-activated platoons was not checked, so the rule is known for made platoons only. Meitou writes both for a squad with a home.
  **Observed** exception on one save (a new game saved at once, unmodded; not a rule of fresh saves in general): `sqt` 1, `persistent` platoons with a `homebuilding`,
  `is resident` false, `never been activated` true and no platoon file. The two of them with a `char count` in the camera block's zones next to the player's
  appeared 4 to 6 simulated s after load (5 of 5 runs), one in the player's zone at once; their sizes varied by run.
- `never been activated` is platoon +0x114 (**Verified**, by code). Activation (`FUN_1404ffad0`) builds the platoon's squad from its template into the same platoon object
  (`FUN_140583a10`) when +0x114 is set (also when it has no characters and an empty string at its active object's +0x18, likely the file name, **Unknown**; both gated
  by a pointer at +0x250 of its vfunc-0x58 object being null, **Unknown** what), else loads its file; it then clears +0x114 (a `regenerates` remake, +0x118 = 1, sets it
  first, **Verified** `FUN_1404ffad0`). The saver (`FUN_140372ef0`) writes a loaded platoon with `never been activated` 0, `content file` `platoon/<name>.platoon` and
  `char count` its members, an unloaded one with +0x114 and its kept `char count`, so a made platoon is never written as never activated. A declared-dead one (`dead`
  +0x1f0, a platoon of a faction whose vfunc-0x58 object has +0x250 null, likely not the player's, **Observed**) is not written (dropped from the save) unless it is
  loaded and a member is alive (+0x5bc clear) or carried (+0x3d4), or a platoon of the list from `FUN_1407f2490` (that it is the player's: **Observed**, inferred) is within 500 on X/Z (250000 squared); then it
  is written as above with its dead (**Verified**, decompile). A wiped `regenerates` squad is not declared dead and is written as usual. Meitou (`SaveLoader.Unmade`, engine rule): only a file-less, never-activated town platoon with `char count` > 0 and a BUILDING
  `homebuilding` becomes a stand-in made from its template on activation (in the player's box at load, else after its 5 s dwell, `PopulationSystem.ActivatesAtLoad`);
  other file-less town platoons stay in the save as they are. Its pool counts `char count` until it is made, and a save then writes its squad into the same record
  (name, handle, `is resident`, `sqt`, homes kept) with `never been activated` false; making it clears the flag on its record at once. Wiped out before a save, it
  follows the rule above (a dead one dropped, else written with its dead or, unloaded, with none: Meitou stores no corpses), so it is never made again at a later load. A town that makes its own residents (pool not `started`, no other squad of the
  save) drops its unmade stand-ins instead, so they are not doubled.
- `hometown` (handle) puts the platoon into that town's or nest's pool at load (`FUN_1407ec8d0`; the platoon's faction and player status must match the town's);
  the pool counts living characters, `char count` while the platoon is not active (`FUN_1408f6a40`). `basetown` is only kept as a TOWN record pointer.
  Meitou rewrites `char count` from the platoon file when it saves, and keeps the save's value for a platoon with no file.
- `homebuilding` (handle) is bound through the game's per-frame system-message queue (`FUN_1407b65b0`): only if the building resolves that frame, else the message is
  dropped (**Unknown** whether something rebinds it later). Meitou writes the handle of the building's state in the save's zone file (a Meitou save writes a file for
  every visited zone, [Zone files](#zone-files)), else its level-data handle ([handles](#object-handles-and-slot-lists)).
- `inventory refresh time` (float) is a trader platoon's next restock in absolute game hours (day x 24 + hour) (**Observed**: METARU's 107 and its quicksave's 117
  trader platoons; the largest is 200.3 at clock 177.4 and 207.7 at 186.6, about the clock + a 24 h `vendors refresh time`; three or four are 0). Meitou writes it for every
  shop with Meitou's own `meitou shop` (string: the stock as JSON, `[]` when empty, the container format of [locks](../game/locks.md)) and `meitou shop fills` (int, the next
  fill's roll key), and restores the shop from them when it makes the platoon; without `meitou shop` the shop is filled anew. Items' stolen marks are not kept. **Observed**
  (headless, real data): two squad shops (one with an item taken out) come back with the same items, cells, purse, fill count and restock tick (±1); a building shop
  (merged grid of its home's containers, wider than 18 when they are) is restored by code only, and loses the cells past 18 if its home does not resolve.

### Wild and nest platoons (Meitou mapping)

A wild platoon is one whose `basetown` is no placed town: its `hometown` naming a nest of the [town list](#nests-town-list) makes it that nest's, no `hometown` and no
placed `basetown` makes it homeless (in the samples `basetown` is the nest's TOWN or empty). **Observed** census: save 2 holds 6 nest and 5 homeless platoons
(49 characters, 8 dead), wade 0 and 4 (10), save 1 4 and 3 (67), all `sqt` 2. Meitou (`SaveLoader.LoadWildPlatoon`, `PopulationWild.cs`) loads each as a stand-in
at its saved place that never travels and is made as its saved characters when its zone is active: a nest's counts its living members against the nest's pool (as the
original), a homeless one counts as one of the squads of the sector it stands in (the original files a platoon in the sector of its position, **Verified** by code
`FUN_1408f6c80`, but a loaded one starts in the sector at the origin and is moved later, so for a while its real sector may spawn beside it; engine choice: bound at once).
A nest squad more than 3 nest radii from its nest wanders where it stands (engine choice: saves hold nest squads kilometres away). On save the wild squads the
population made are written as new platoons: a nest's with `basetown` = its TOWN and `hometown` = the nest's handle, a homeless one with neither, `sqt` 2.
NPC squads of no town, platoon or wild site (a dismissed character's, a slave bought free or handed over, one separated by dialogue) are written the same way as homeless
platoons with `persistent` true (as the original's dismissed platoon; engine choice for the others), so they load as homeless wild platoons of the sector they stand in
(counted among its squads); a save's platoon with `persistent` set is never despawned off screen (`PopulationRoaming.Kept`, as the original keeps persistent platoons).
**Observed** (headless, saves 2, wade, 1, the player moved next to each wild platoon): 41 of 49, 10 of 10, 67 of 67 saved wild characters made (the rest dead in the
save), each at its saved position (0 units off); a held nest or sector made only its remainder; a Meitou save → load gives the same wild characters at the same places
(78, 10, 96) and no fresh wild squads.

### Nests (town list)

**Verified** by code (`FUN_140935120` from `loadGame`, factory `FUN_140581770`, `Nest` constructor `FUN_140934ab0`): each GAMESTATE_TOWN_INSTANCE_LIST entry whose target
TOWN has `type` 0 becomes a nest at the entry's position; its handle is the `hand` of the entry's state (a null one gets a new random handle); no other key of the
`Nest state` is read. Entries (**Observed**, saves 1, 2, wade: 13, 150, 150): id `<n>-quick.save-INGAME`, zero quaternion, state `<id>-S5e` named
`Nest state <TOWN name> <k>` (`k` a rising number with gaps, origin **Unknown**), keys `hand` (type 85, a town container `C`), `started`, `explored`,
`recently discovered`, `discovered`, `ded`, `is nest` true, `faction`, `artifacts count`, floats `popd2` (= the TOWN's `nest resident population` in every sample) and
`pop2` (≤ it), `popd`, `pop`, `tod` (0); some have `zzX0` / `zzY0`. The states sit before the list. A save holds only the nests made so far (save 1: 13; the original
adds them as areas tick). Meitou takes a save's nests first and rolls the rest of each group's count from the world seed (CAMERA `meitou seed`, restored on load).
A Meitou save likewise holds only the nests made so far: the save's own plus the rolled ones that have loaded (a squad made, or a saved one at it), at the spot they
loaded at, with its pool's `pop2` / `popd2` / `ded` / `tod` (a new nest: `popd2` = `nest resident population`, `pop2` 0.7 of it, grown since), `started` true and the player's knowledge as `discovered` / `explored`. On load the roll runs in its
fixed order and leaves out each roll whose spot (or marker) a saved nest holds, without counting it against the group's count, so whichever subset was saved the
unwritten nests come out at their old places and no nest is rolled twice (the saved one takes over that roll's key for its squads); a rolled nest's place uses a
random stream apart from its TOWN pick. **Observed** (headless, base saves 1, 2, wade and a new game, the player sent to 3-4 nests): saved nests = base (13, 150,
150, 0) + the started ones (3, 3, 4, 4); reloaded with another launch seed, all 325 nests (TOWN and position) and every nest squad's nest equal those before.
A rolled nest that loads but makes no squad is not written (rolled again). Older Meitou saves' `meitou-nest:` camp states are still read.

### Zone files

**When** (**Verified** by code): `ZoneMapContent::deactivate` (`FUN_1409fe980`) saves a zone (`FUN_14036dde0`) into the save file system's working folder when it
unloads (single-zone unload `FUN_140a0a4f0` passes its flag on; unloading every zone, `FUN_140a0a7b0`, does not save), and `saveGame` saves every loaded zone
(`FUN_14036e450`). The zone's container gets a `0-buildinglist` (type 30, name "0") from its buildings, then `1-itemlist` from its loose items; a list left empty is
deleted; a container with no record left deletes the zone file, else it is written, and the zone's has-file flag (the CAMERA `zones` value) follows. So a save holds a
file for every zone loaded in the playthrough that has a building or item (save 1: 11 files, 4 without a building). "Delaying saving zone" (`FUN_1403c8cb0`) is not this:
it writes the navmesh tile cache (`FUN_1403a6f40`, `data/newland/land/navtiles/`) and waits, retrying on a later call with no timer, while a game save runs or its lock is held (**Verified** by code). Platoon files are written only by `saveGame` (`FUN_140372ef0`), every platoon of both faction lists, active and unloaded, with no dirty filter (**Verified** by code), so a save holds the residents of every town ever loaded ([persistent platoons](../game/factions-squads-towns.md#1-object-model)).

**Load** (**Verified** by code, `FUN_1409ffad0`): the level layers' zone files are loaded into the zone's container, then the save's file into the same container
(by the layers' merge rules, not read in the code: a key the file lacks keeps the level value). No save file means a fresh zone, whose towns make their residents. A file makes it not fresh, unless a town of the zone
has an override world state (its lists are then replaced) or `0-buildinglist` holds `imported`.

**Content** (**Observed**, census of saves 1, 2, 3, wade against the merged level files, 275 zone files):
- `0-buildinglist`: every placed building of the zone, the level ones (6688) with the level id, position and rotation (the target differs in 179, see [Unknowns](#unknowns)), the ones made
  in play with `<n>-zone<X>.<Y>-INGAME` ids; an empty-target, zero-position entry with no state for each level id a later layer clears (107 of 107; never for a layer's removal
  of an id no earlier layer places, 0 of 147); an entry whose building cannot be made (undefined target) is gone. Instance states: `[<inventory state>, <id>-S23]`, the inventory
  only when it holds items (a new `-zoneX.Y-INGAME` INVENTORY_STATE).
- A level building's GAMESTATE_BUILDING has the level state's keys with: `serialised interior` 1 added (all 36561 saved states); `is public night` and `is outside` never
  written; always `pause`, `destroyed`, `name`, `flags`, `residents template`, `squad template`; a lock's `hardness` and each door i's `hardness<i>` = the
  BUILDING's `hardness` (3093 of 3093, 614 of 614) and `pause<i>` false; `internalbuildings-<k>` naming the generated furniture (43010 of the level states' 43031 such handles
  name no level building); `town` the town the building belongs to (3726 of 6743 name another town than the level state; the town states keep the level `hand`, 304 of 304 in
  saves 1 and 2). Record flags 0, name "0".
- `serialised interior` = 1 stops the building factory (`FUN_14057cc70`) from generating the interior (**Verified** by code); absent or another value, it is generated.
- Slot list: the last level layer's list (a layer without one empties it), plus slots allocated in play (the lists Meitou makes this way are within the saved ones in 181 of 183 files, the other two lack
  one slot each). `nextId` can be above the highest record id.
- Nest buildings are not saved (no type-85 `town` handle in save 2's 133 files); nest debris are `1-itemlist` ITEMs whose `persist` handle is the nest (222 in save 2).
  Meitou places neither, so writes neither.
- Shop stock (**Observed**, METARU): 9025 of its 9038 `backpack_content` INVENTORY_ITEM_STATEs are in zone files (the shop buildings' containers), 13 in platoon
  files; no trader character carries its stock. Meitou cannot address those containers (unmapped interior ids) and saves a shop on its PLATOON instead
  ([NPC platoons](#npc-platoons-meitou-mapping)).
- `1-itemlist` (**Observed**, METARU: 285 items in its zone files): INSTANCE_COLLECTION, name "0", flags 0; entries numbered "1", "2", ... with no states, the item's position and a
  full (physics-settled) quaternion, target an ITEM record (type 4, name "0", flags 0) of the same file, id `<n>-zone<X>.<Y>-INGAME` of the zone it was made in (an item
  moved to another zone keeps it). Its keys are INVENTORY_ITEM_STATE's with `in inventory` false and `section` "": `base data sid`, `material sid`, `company sid`,
  `color sid`, `item function`, `level`, `quantity`, `charges`, `quality`, `inventory x/y` 0, `death` ([death item](../game/animals.md#death-items)), null-able `ownedby` and `insideBuilding` handles; `uniform` on 49, a
  `persist` handle (type 85, a town container) on 284 (the nest debris). The one severed limb (`limbs.mod`, `item function` 17) has no `persist` and adds `skin` and `blood`
  ints, a `scale` vector and a `timeout` float (131.7; meaning, units and whether it removes the item **Unknown**).

**Meitou** (`Saving/ZoneSave`, `Data/Save/SaveZoneBuilder`): every zone the population had active, and every zone of a town it made but another town's centre zone (engine choice:
a town is made and saved whole; three such zones, e.g. Black Desert City's buildings in Scraphouse's 34.28), is visited (`World.VisitedZones`, not hashed). A save keeps the loaded save's zone files (locks, doors and player buildings are written into them) and makes
the missing ones from the level data as above, with `town` set to the save's town state `hand` and a new save's town states taking their level `hand`; it writes no
`serialised interior`, no furniture or inventories (not modelled), so the original makes each interior as for a fresh zone (only the residents differ: the
saved platoons). **Observed** (headless new game, the player moved to Squin and Stack): 8 visited zones, 8 files, every handle in its slot list, 300 of 300 `town` handles
name a town state; key set = the real saves' minus the runtime-only keys (`serialised interior`, `internalbuildings`, farm, craft and lock keys of buildings that have none
here) plus `meitou lock <i>`; doors and an interior chest locked before the save are locked after reload, and a second save writes the same zone files. Base saves 1, 3,
wade and 2 (moved to Admag) keep every zone file byte for byte except door keys added to buildings whose saved state has none (3 files in save 2, 1 in save 1).
Loose items (`Saving/GroundItemSave`, `Data/Save/SaveGroundItems`): a load puts every `1-itemlist` item the data can make on the ground at its saved position and rotation;
a save writes each zone's list from the items lying in it, a read item with its own record (unmodelled keys kept), a new one with the key set above minus `uniform`,
`persist` and the limb keys, id `<n>-zone<X>.<Y>-INGAME` above every such number of the file; entries whose item the data lacks are written back as read; a zone whose
list is unchanged is left as it is, an empty list is not written, a file left empty goes. **Observed** (headless, METARU with the install's load order): 285 of 285 items
loaded; a capture with nothing changed keeps all 123 zone files byte for byte; an item dropped, saved and reloaded lies where it was, and a second save writes the
same 124 files.

### Locks, cages and sentences

Key types **Verified** by a census of the owner's five saves (every platoon and zone file): building `lock lvl` int, `locked` / `broken` bool, `hardness`
float; door i `state<i>` int (0/1), `doa<i>` float, `broken<i>` / `locked<i>` / `wants lock<i>` bool, `gatecode<i>` int (-1..6), `lock lvl<i>` int,
`hardness<i>` float; character `in something` int (0 8135, 1 1159, 2 218), `in what` handle (type 0 BUILDING when set, else 11; prisoners' point at
generated interior buildings, e.g. `19765-zone39.16-INGAME-S23`), `sentence` int and `psts` float (0 in every sample); platoon `imprisoned` bool (+0x1f1: whether a member was caged when the platoon last unloaded, read
back at load; **Verified**, `FUN_1407ef4b0`, `FUN_140372ef0`, `FUN_1407ed0f0`); platoon `dead` bool (+0x1f0, declared dead: all members dead, `FUN_140797090`) and `intact` bool (+0x100, the
squad-intact flag) are written by `FUN_140372ef0` and read by `FUN_1407ed0f0` (**Verified**). Meitou reads and writes both (`Platoon.Dead`, `Platoon.IntactFlag`).
Meanings: [locks](../game/locks.md#save-keys), [economy 11.5](../game/economy.md#115-bail-and-sentence) (`sentence` = start in game hours, `psts` = length).
Dialogue locks (**Observed**, `Character::serialise` writer `FUN_140679900`, reader `FUN_14067f780`): character pairs `lok0`, `lok1`, ..., string = DIALOGUE
string id, bool of the same key = locked ([dialogue](../game/dialogue.md#memory-and-saves)); Meitou reads and writes them (`Saving/DialogueSave`), and reads `personality` when above 0.
Slave state (**Observed**, `FUN_140626580` writes, `FUN_140626830` reads, in the character's state record with `personality`): `slavestate` int (status `+0`: 0 free, 1 slave, 2 escaped, 3 ex-slave; the reader stores any int unchecked, **Verified** `FUN_140626830`; Meitou models 0 to 2 and loads 3 or anything else as free),
`escap` bool (`+0xe8`, the escaped-prisoner flag), `disguiseblown` float (`+0x110`, the [disguise](../game/perception.md#disguise) cooldown and recently-wanted marker; Meitou `CrimeSave`) and two short-keyed bools (`kidn` `+0xe9`; `tn` `+0xea`, the freed flag, names from `DAT_1416fa808` / `DAT_1416fa804`, **Verified**); the owner is the `slaver` handle. Meitou (`Saving/SlaveSave`)
reads and writes `slavestate`, and the owner as `meitou slave owner` (FACTION string id). `CrimeSave` reads and writes `tn` (`CharacterCold.Freed`, written always).

Carry links (**Observed**, decompile: `FUN_1406280a0` writes, `FUN_140627b30` reads): the character state record has a bool `carrying` (true on the carrier) and a handle `carrying`
(the partner, null when none), both always written. The reader links them when the partner resolves, else keeps the handle and links it later from the per-frame `FUN_140797ec0`
(deferred until the partner is loaded). Meitou (`SaveCapture`, `CarrySystem.RestoreLinks`): the same keys; the link is made without a lift, checked every 20 ticks.

Meitou (`Saving/HeldSave`): reads building keys from the zones' GAMESTATE_BUILDING records when the town registers and writes them back into
them (the visited zones' files, [Zone files](#zone-files)); the save ids of generated interiors are not mapped, so interior cages and
beds keep rolled state. `in what` resolves only to a placed building; Meitou-only keys `meitou in something` (int) / `meitou in what` (fixture id)
carry the rest and win on load. Unchanged held state keeps the original keys as read; a changed one without a building handle writes `in something` 0
and the null handle. `sentence`/`psts` are written while a sentence is set or the save had them; Meitou's start equals `World.GameHours` (day x 24 + hour).

Job lists (`Saving/PermaJobSave`): the original's GAMESTATE_AI (`ai`) keys `jobs`, `pjob<i>`, `pjobT<i>` ([perma-jobs](../game/ai-tasks.md#perma-jobs)), plus
Meitou's string `meitou pjob<i>` (the job's building id, which wins on load over the handle). The squad AI options are CAMERA bools (**Verified**, writer
`FUN_1403eb710`; the key of each option and new-game values in [squad AI options](../game/ai-tasks.md#squad-ai-options)); Meitou reads and writes nine
(not `stay`, `eject`), an absent one keeps its default.

### Order queue

A player character's order queue (the deque at `AITaskSytem` +0x40, [order queue](../simulation.md#order-queue)) is in its GAMESTATE_AI record `ai`, after the job list. Meitou: `Saving/OrderSave`.
- **Keys** (**Verified**, `AITaskSytem` vfuncs 1 / 2: writer `FUN_1405090b0`, loader `FUN_1405092d0`): per order i from 0, front first, int `tjob<i>` (the entry's TaskData task type), vec3 `tjobP<i>`
  (the entry's point) and the handle `tjobT<i>` (five ints `TYPE`, `C`, `CS`, `I`, `S`, the entry's target, as `pjobT<i>`; the null handle, `TYPE` 11, when it has none). Nothing else of an
  entry is saved: not whether it started, not its failure text. The writer and the loader act only for a character whose faction has the player-faction field (+0x250) set.
- **Observed** (60 of the owner's 511 saves, 191 characters, all original writes): queues of 1 order (186) or 2 (5), so the running front is saved; rows 29 move (163; null handle, point set),
  258 `USE_BED_ORDER` (11; a BUILDING handle, point set), 16 `ATTACK_ENEMIES` (11) and 5 (5) with a CHARACTER handle and a zero point (a queue of two is 5 then 16 on one target), 60 / 201 / 26 / 65 / 87
  once each. Meitou's Sleep order is the row 98 it starts, so it is written and read as 258. Why 5 is followed by 16 is **Unknown**.
- **Load** (**Verified**): the loader reads `tjob<i>` until one is missing and adds each through the add function `FUN_1405086d0` as a one-shot order (it lands in the second list +0x70,
  [precedence](../game/ai-tasks.md#precedence), not in the deque; a row with a job form is replaced by it), then the planner is re-run. Meitou instead restores the queue as saved: a load reads the
  orders into `CharacterCold.SavedOrders`; the decision step makes them the queue (nothing started) once every character and building exists, and the front starts through source 1.
  An order is dropped when no system owns its row (so 16, 60 and the other rows Meitou has no order for), its target character or building is not in the world, or its handle names nothing the
  save holds. A building handle is the building's instance (a build site, or a fixture whose id is the building's own, a bed too); a CHARACTER handle finds the character by its saved handle.
- **Meitou keys**: string `meitou tjob<i>` (the order's site id: a build site, fixture or rock, which wins on load over a building handle, as `meitou pjob<i>`), int `meitou tjobK<i>` (the owner's
  detail code, `IOrderOwner.SaveKind`: a loot that opens the window, a bed named by a put-in order, a walk only to a container, a lock order done in place), each written only when set. Ground-item
  pick-ups (a runtime item id) and the Medic / Rescue buttons' looks are not saved. **Unknown**: how the original consumes the second list after a load.

Meitou-only crime keys (`Saving/CrimeSave`, `LockSystem`, `FixtureRegistry`; the original's keys for these facts are **Unknown**), each written only while set and removed otherwise:
- GAMESTATE_CHARACTER float `age`: the character's age (1 for people, an animal's growth, [animals](../game/animals.md#ageing)); **Verified** in vanilla saves.
- GAMESTATE_CHARACTER `meitou escaped` (bool, escaped-prisoner flag), `meitou kidnapped` (bool, held by a kidnapper: no sentence, no bail), `meitou recognition` (float 0..1, bounty recognition meter). Older Meitou saves wrote the recently-wanted seconds as `meitou recently wanted`; they are read when `disguiseblown` is missing and dropped on save.
- GAMESTATE_BUILDING `meitou house locked` (bool, a lock-up round locked this home and nothing unlocked it since).
- GAMESTATE_BUILDING `meitou lock <layout index>` (int level x 4 + broken x 2 + locked) for each interior cage or container whose lock is locked or broken (their ids are not mapped, see above).

### Player outposts

Towns made in play are saved in `quick.save` only, as entries of its one GAMESTATE_TOWN_INSTANCE_LIST (type 38, name "0",
id `<n>-quick.save-INGAME`), written from the town manager's player-town list (+0x68) and nest list (+0x80)
(`FUN_14092a6c0`). The level data's towns are not in it: their GAMESTATE_TOWN states are written separately
(`FUN_14036b710`). **Observed**: the owner's five saves' lists hold only nests (127 and 150 entries), each with a
`Nest state <name>` state (type-85 handle, no `instance` key); no save has an outpost.

An outpost's entry (**Verified** by code, `Town::vfunc_17` `14036eb20` with `FUN_140386d20`): id = the town's stored
instance id, or a new `<n>-quick.save-INGAME` (kept on the town, so later saves reuse it); target = the town's TOWN
record (`player_town_template`); position = the town's, absolute; quaternion (0, 0, 0, 0); state `<id>-S5e`. Its
GAMESTATE_TOWN (`Town::vfunc_85` `140376b80` and the TownBase writer) is named `Town state <name>` and has the base
towns' keys: `instance` = the entry id, `is nest` false, `building material`, `public` (Town +0x380, the [town panel](../game/ui-screens.md#town-panel)'s Open to public, false when made; **Verified**, `param_1[0x70]`; Meitou round-trips it as `PlayerTown.Public`), `plevel`,
`replacementTown` (the string id of the TOWN in the town's place, "" none: [town overrides](../game/factions-squads-towns.md#town-overrides)), `trade goods`, `faction` = the player faction, `factions here<i>`, `discovered` / `explored` /
`recently discovered`, `artifacts count`, the handle `hand*` (type 13 TOWN) and the population floats; plus int
`override` (Town+0x51c, the player town's [reassessed radius](../game/buildings-production.md#placement); 0 when made), written only for a town
whose faction is the player's. How the float is stored under an int key is **Unknown**; Meitou writes 0 and reassesses on load.
Buildings name it by their `town` handle. Whether the loader takes the name from the record name is **Unknown**.

Meitou writes the level towns' pool keys (`pop`, `popd`, `pop2`, `popd2`, `tod`, `started`, `ded`) for every town it made or loaded pools for, and
reads them back ([off-screen tier](../simulation.md#off-screen-tier)). `tod` is Meitou's game hours since the world was started or loaded (a loaded save restarts at 0); the original's unit is
**Unknown** (no sampled state has a nonzero `tod`).

### WAR_SAVESTATE (type 9): faction campaigns

One per faction in `quick.save` (103 of 103 in every owner save), name "0", tied to its faction only by the string `faction` (FACTION
StringId). Writer `FUN_1409c6890`, reader `FUN_1409c9dc0`; meanings in [campaigns](../game/factions-squads-towns.md#campaigns).
Numbered keys append the index with no separator (`req sid3`, `p2`); handles as in [object handles](#object-handles-and-slot-lists).
**Verified** by code (both functions, same keys) and the saves' key sets.

| Key | Kind | Meaning |
| --- | --- | --- |
| `faction` | string | FACTION StringId |
| `fwc id` | int | next campaign id; on load raised to the largest `id c<i>` + 1 |
| `updatetime` | float | next roll, absolute game hours (day × 24 + hour); read only when present |
| `num actives`; `dat c<i>` string (FACTION_CAMPAIGN), `target c<i>` handle, `enemy c<i>` string (FACTION, may be ""), `leader c<i>` handle, `home c<i>` handle, `phase c<i>` int (0 travel, 1 at target, 2 ended), `id c<i>` int, `rd c<i>` bool (leader has been in the target town), `trig c<i>` bool (follow-up queued) | | running campaigns |
| `num requests`; `req sid<i>` string, `req enemysid<i>` string (FACTION), `req time<i>` float (absolute due hour), `req targ<i>` handle, `req home<i>` handle | | queued campaigns |
| `num poss`; `poss sid<i>` string, `poss time<i>` float | | every campaign the faction knows with its last start (the `repeat limit` clock) |
| `num plats`; `p<i>` handle, `forces<i>` int | | campaign platoons; `forces` = the campaign's `id c` |

Reader rules (**Verified** by code): a running campaign whose `dat c` does not resolve is skipped; an unknown campaign joins the faction's
list; phase 1 becomes 2 when the campaign has no `pt2 AI leader`; **phase timers are not saved** (the phase starts again at load, so the
4 / 12 / 1 / 3 h windows restart); men count, target position and speeds come from the data. A request whose `req sid` or `req enemysid`
does not resolve is skipped. All `poss time` are zeroed, then set by `poss sid`. `p<i>` is taken only for TYPE 1 or 34 and when it
resolves. Missing keys read as 0 / "" / false (**Observed**).

**Observed** (owner saves 1, 2, 3, wade, quicksave, autosave0-2, new; up to day 8): no running campaign, request or platoon in any;
every `fwc id` and all 77 `poss time` are 0 (no campaign ever started; no save has a player outpost, which rolls need). Each faction's
`poss` list is its merged FACTION `campaigns` list in order (103 of 103). `updatetime` lies in (clock, clock + 6], spread over about
0.6 h (all 40 in the two day-1 saves). Meitou's `SaveBuilder` writes `updatetime` 40 and zero counts, as a new game; a captured
world writes its war managers over the loaded state (unknown keys kept, numbered keys past the new counts removed), and a campaign squad
never made is written as a PLATOON with content file `platoon/` (no file) so its `p<i>` handle resolves. Meitou also writes floats
`meitou phase start c<i>`, `meitou fight start c<i>`, `meitou arrived c<i>`, `meitou announced c<i>` (absolute game hours; absent:
not started) and, per campaign platoon, `meitou carry start p<i>` (beside `p<i>`; a cannibal assault's carrier stamp), and reads them back (a missing phase start is the load's hour, as the original; the
older campaign-wide `meitou carry start c<i>` is read onto each platoon and dropped on the next write). **Observed** (headless, real data): the four campaign floats come back equal.

Campaign platoons in a game save need not have a placed `basetown`: in the owner's modded day-397 save, a cannibal raid's platoon has a nest `hometown`
and no file, and a nomad trader's has an empty `basetown` and one character (**Observed**). Meitou loads every platoon a `p<i>` names as its campaign's travelling
stand-in, homed at the campaign's `home c<i>` town when its `basetown` is no placed town, never as a [wild platoon](#wild-and-nest-platoons-meitou-mapping); a
campaign left without platoons is defeated at once ([campaigns](../game/factions-squads-towns.md#campaigns)). **Observed** (headless, that save): all 5 resolving
campaigns keep their platoon after 600 ticks; before, the two on the wild path were defeated on the first update.

### Squad memory

A platoon's memory of characters (`SquadMemory`, [dialogue](../game/dialogue.md#squad-memory)). **Observed** (writer `FUN_1406789e0`, reader `FUN_140678cb0`; the key
prefix `tags` read from the exe bytes at `1416ffef4`): for entry `i` from 0, the character's handle `tags<i>*`, ints `taglong<i>` / `tagshort<i>` (tag bit sets), float
`tagtime<i>`; the reader stops at the first missing `taglong<i>`. Meitou (`Saving/MemorySave`) writes the same keys on the PLATOON (time in game hours), plus
`meitou tags player long` / `short` (int) / `time` (float) for the squad's shared player-faction entry (the original's key for it is **Unknown**) and bool
`meitou temp ally` (the TEMP ALLY flag for the player, not written by the original). A character the capture wrote no handle for, or a handle that names no character made
yet, is left out. Round trip checked with a throwaway probe (write, read, clear), not with a game save: **Observed** only.

### Faction trust, war flags, bed rent and turrets

- **Global trust**: the original writes no `+0x18` (the float DC_REPUTATION reads and DA_AFFECT_REPUTATION raises). Its player table writes `global trust` from `+0x14`
  (`FUN_1406b3c80`, read by `FUN_1406b5190`), whose link to `+0x18` is **Unknown**; the relation table writes `relation`, `trust`, `trustNeg` and `relationSID` per entry and
  `rank` (`FUN_1406b37b0`, `FUN_1406b3300`, **Observed**). Meitou saves `+0x18` of every faction as `meitou global trust` (float on the GAMESTATE_FACTION, only while not 0).
- **War flag**: the entry's byte `+2` has no key in the original (**Observed**: the relation writer reads none), so a war declared by a dialogue is forgotten on load. Meitou
  writes bool `meitou war<k>` beside `relation<k>` of the faction's table, only while set.
- **Bed rent**: the global map of last payments (`FUN_1403576c0`, [locks](../game/locks.md#beds)) is referenced by no other function (**Verified**: references to its
  address are the payment function and the static constructor and destructor), so the original forgets it on load. Meitou writes the payments less than 24 game hours old on
  the CAMERA as `meitou rent bed<i>` (string, the bed's fixture id) and `meitou rent hours<i>` (float, absolute game hour) (`Saving/BedSave`).
- **Turrets**: a turret building saves the building base, the machine keys (`batterycharge`, `gets battery`, `power on`, `broken`; `FUN_1402a14b0` / `FUN_1402a1750`)
  and the `mounted` handle (the wall it stands on; `FUN_1402a1c80` / `FUN_1402a1e30`, vtable slots 17 / 18 of `TurretBuilding`), which then calls its gun's slot 20 / 21
  (`GunClass::serialise` `FUN_1404390a0` / `loadFromSerialise` `FUN_140439140`): int `current shots` (the gun's `+0x20` loaded harpoons) into the same state, read back as
  min(saved, `+0x1c` the gun's shots), a missing key as 0 (**Verified** by decompile). The reload timer (the gunner's controller), aim and rotation error are **not** saved,
  so a loaded turret keeps its harpoons, starts a fresh reload and re-acquires its target. Meitou writes `current shots` for each turret manned since loading (the others keep the
  loaded value; `CombatSystem.WriteTurretShots`) and a turret's first manning reads it (capped at max(`num shots`, 1)); it writes no `mounted` link and adds no key.

### Cross-checks against base data (the sample vs `GameDatabase` of the four base files)

| Check | Result |
| --- | --- |
| Every GAMESTATE_FACTION `gamedata stringID` is a FACTION | 103 of 103 base FACTION records have a GAMESTATE_FACTION (`gamedata stringID`); `relationSID` 10,506 of 10,506 resolve |
| `GAMESTATE_BUILDING.owner faction ID` | 1496 of 1496 resolve to FACTION (one value is `nofac`, which is itself a FACTION record id), plus 25 suffixed `owner faction ID<k>` keys, all resolving |
| INSTANCE_COLLECTION targets in zones | BUILDING 1496, ITEM_PLACEMENT_GROUP 526 (the 1496 equals the GAMESTATE_BUILDING count) |
| `INVENTORY_ITEM_STATE.base data sid` | all 2181 resolve: ARMOUR 891, ITEM 1018, WEAPON 230, CROSSBOW 14, CONTAINER 26, MAP_ITEM 2 |
| `INVENTORY_ITEM_STATE.color sid` | the instance's COLOR_DATA or ""; how it is set: [clothing colours](../characters.md#clothing-colours) |
| `MEDICAL_STATE.sid<k>` | 1610 of 1610 resolve to LOCATIONAL_DAMAGE |
| `TOWN` states | 304 of 304 instances match base town placements; `trade goods` are 75 ITEM records |
| Strings that do **not** resolve | `company sid` / `material sid` values with a `.TECH.<n>` suffix (17: research the engine makes at start, [blueprints](../game/buildings-production.md#blueprints)), `GAMESTATE_TOWN.instance` (instance ids, not records), BIOMES `E<n>` instance targets (numbers) |

### Functions

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
| `FUN_14092a6c0`, `FUN_14036b710`, `FUN_14092ae80` | town list write ([outposts](#player-outposts)), level town states write, player-town check on load |
| `FUN_140552320` / `FUN_140552b70`, `FUN_140555650` | building state write / read ([keys](zones.md#placement-keys)); `FUN_14057cc70` (catalogued as `Zones_CreateDoor`) is the building factory |

## Unknowns

- What `byteSize` encodes beyond "child count for some record classes", and whether the game ever reads it (it does not at load).
- The two long slot lists (2503 and 2969 ints) at the end of `quick.save`; the `S` serial's use when a handle goes stale (what happens on a mismatch).
- The roles of BIOMES `E<n>` instances (EffectHandler state fields), `_w_fog_a`, `TI day/dat0`, `towntime`, `ssct`, and whether MEDICAL `fed` / `KO` are the stomach buffer and the KO timer (the other MEDICAL floats are explained above).
- Whether `loadGame` rejects or adapts to a `mods` list that differs from the current load order (nothing in the sample: single base-game save).
- Prisoners, dead characters, bounties, trade and mission state, and player outposts: not present in an early-game sample. Player-built building and outpost keys are known from the code only ([zones](zones.md#placement-keys), [outposts](#player-outposts)).
- What `DAT_142134690+0x298` is (the autosave gate).
- How relations, trust and `trustNeg` change during play (only the fresh-world values are explained), what the player faction (no relation table, but a `known` list (the [factions met](../game/factions-squads-towns.md#36-factions-met)) and `global trust`) uses instead, and how the player table's `global trust` (`+0x14`) relates to the `+0x18` dialogue reads ([trust](#faction-trust-war-flags-bed-rent-and-turrets)). Which key the squad memory's shared player entry is saved under ([squad memory](#squad-memory)).
- Why level buildings have a different target in the save than in the level list (179 of 6743 in four saves; in zone 19.29 two placements have swapped targets, same positions: a random variant pick is a guess).
- Saves made by other game versions or with mods: layout of `mods` and unknown record types would show.

### Implementation (as built, stage 10)

| Type | Role |
| --- | --- |
| `SaveFile` | One file: an FCS file (any type, strings lossless) and its slot lists; writes back the same bytes |
| `Hand` | `{Type, C, CS, I, S}` by key prefix; `Null` is type 11 and `IsNull` tests the type only; `Prefixes` finds the handles of a record |
| `SaveFolder` | The layered lookup (a later layer hides an earlier one), names with `/`, the `platoon/` and `zone/` listing, zone file names |
| `SaveGame` | `Load(folder)`: `quick.save`, the platoon files named by the PLATOON records, every zone file, the portrait atlas; typed views (`SaveCamera`, `SaveFaction` with its war record and relation table, `SaveTown`, `SavePlatoon` with its `SaveCharacter`s and their six state records, `SaveZone` with `Overlay`), the zone-file check as `Problems`, `BuildHandles()` (`SaveHandles`: platoon, character, town and building lookups), `Clone()`, `UpdateDerived()` and `Write(folder)` |
| `SaveBuilder` | Makes the records of a new save (CAMERA, factions with war state, towns, platoons, characters with their six states) with the key sets above, the id patterns and seeded random serials; can also add to an existing save; `RemoveCharacter` |
| `SavePortraits` | The blank 2048 x 2048 atlas of a new save |

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Save requests (`FUN_14047c310`)
- What `saveGame` writes (`FUN_140375360`)
- `loadGame` (`FUN_140373f00`)
