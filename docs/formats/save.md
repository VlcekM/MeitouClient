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

**Terrain decals** (TERRAIN_DECALS in `quick.save`, **Verified**: two saves, writer `FUN_1408de5d0`): int `total`; one instance per cell copy of a decal
([blood decals](../game/blood-decals.md)), id = the 1-based counter as raw int32 bytes, target `-`, the 24-byte `DecalInfo` in the seven position/rotation
floats as raw bits: x = local X | local Z << 16 (shorts, 1/1000 of the 288-unit cell), y = half width | half height << 16 (size × texels × 10),
z = rotation (short, rad × 1000), qw = colour ABGR, qx = cell key, qy = type | variant << 8, qz = life (a real float); the upper 16 bits of z and qy are 0 in both.
Meitou (`SaveDecals`) reads it into the blood store on load and writes the store back on save, bit-identical for both saves (**Verified**, read → write → read in a
scratch harness; pinned by `SaveReadTests`); with `Blood` off the loaded record is kept as it was. Meitou-only keys on the same record hold the `bloodsurfaces` marks (`SaveSurfaceDecals`): per mark i from 0, vec3 `meitou sd<i>` (centre), vec4 `meitou sdS<i>` (angle, size, life left, 0), ints `meitou sdK<i>` (type | variant << 8 | floor << 16) and `meitou sdC<i>` (colour ABGR); read until a centre is missing; the original ignores them (keys are read by name).

**Player-built buildings** (Meitou `Saving/BuildingSave`): an instance in the zone's `0-buildinglist` with id `<n>-zone<X>.<Y>-INGAME` and a
GAMESTATE_BUILDING `<id>-S23` carrying `name`, `owner faction ID`, `floornum`, `destroyed`, `flags`, `is complete`, `pause`, `construction progress`,
`<i>construction mats`, `world Y pos`, `handle`, `isInsideBuilding` (key set from the original's writer `FUN_1405547f0`, **Verified** by code).
The list is not always the position's zone: a vanilla save listed a building in the zone of its id (20.32) while it stood in 20.33 (**Observed**,
#493). Meitou writes each site in its position's zone and drops its entry from any other; on load a copy outside the position's zone loses
to one inside it.
**Observed**, native 1.0.68 authored-fixture load/save: an outdoor player placement's raw instance Y is a terrain offset (0 places it on
the ground), while `world Y pos` is absolute; supplying the terrain height in both fields doubles its loaded height.
Indoors, `player furniture Y offset` is child world Y minus the parent's origin Y (**Verified**, native writer; **Observed**, 185 saved
interiors). Zero makes the loader use raw instance Y as the parent-relative offset instead (**Observed**, eight authored placements
crossing raw encodings and offsets on two floors). Nonzero offsets preserve placement independently of raw instance Y; zero with an
absolute raw Y raises furniture by that absolute value. Meitou writes both indoor height fields parent-relative, outdoor instance Y
terrain-relative, and reads absolute `world Y pos` back for its own buildings; the original never reads it ([placement height](zones.md#placement-height)). Native save output can have absolute indoor instance Y.
**Observed**, native 1.0.68 load/save of eight placements written by Meitou: offsets 0, -1.5, 8 and 32 all retained their exact parent-relative heights.
A finished one with doors also gets the door keys of [locks](../game/locks.md#save-keys)
(the original saves doors the same way for every building, **Observed** by code). Meitou-only `meitou parent` (instance id) names a parent the handle cannot.

**Rock machines** (Meitou `Saving/RockMachineSave`, #596): a worked rock (a natural site, [rocks](../game/buildings-production.md#resources)) has no zone state, so its machine is Meitou-only keys on CAMERA: string `meitou rock<i>` (the natural site id), bool `meitou rock<i> own` (claimed by the player's faction), int `meitou rock<i> town` (player town number, absent: none), and the town machines' `st`, `out`, `outn`, `in<k>`, `lv<k>`. Read back before the first tick; the machine waits for the foliage to place its rock again. An id naming no rock BUILDING is dropped.

**Building record** (Meitou keys on a registered town building's GAMESTATE_BUILDING, [locks](../game/locks.md#building-record)): int `meitou designation`, string `meitou template` (the last assigned SQUAD_TEMPLATE id, absent: none) and the stored public byte as `is public day`; a state without them is a building with no record yet.

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

### Where saves live

| Setting / path | Meaning |
| --- | --- |
| `%LOCALAPPDATA%\kenshi\save\<name>\` | **Verified** (`save.log` paths, and the sample is there). Used when `settings.cfg` `User save location=1` (default 1) |
| `settings.cfg` `autosaveindex` | Autosave slot counter (see [Autosave](#autosave-fun_14047be70)); absent in the sample (no autosave happened) |
| `<install>\save.log` | Log of the save file system (**Verified**: the sample's saves are all logged). In the install folder even when saves are in the user folder |

### Folder layout

| Item | Status |
| --- | --- |
| `.save`, `.platoon`, `.zone` all parse with `FcsReader` as file type 15 | **Verified** (70/70 files of the sample, to the exact end of records; `.zone` and `quick.save` have a trailer, below) |
| Platoon file name | **Verified** (56/56): `<faction name>_<n>.platoon` where `<n>` is a per-faction counter; the PLATOON record's `content file` is `platoon/<platoon stringID>.platoon` |
| Zone file name | **Verified** (13/13 and by `FUN_14036c070`): `zone.<X>.<Y>.zone`, X and Y the zone grid indices ([terrain.md](terrain.md#world-coordinates)) |

### Folder layout — layout/example

```
<name>/
  quick.save                 global state                      (1 file, 2.26 MB in the sample)
  portraits_texture.png      character portrait atlas          (2048 x 2048 RGBA)
  platoon/<faction>_<n>.platoon   one file per squad           (56 files, 6-120 KB)
  zone/zone.<X>.<Y>.zone     one file per loaded zone          (13 files, 2 KB - 1.4 MB)
  meitou-mods/<id>.dat       Meitou only: code-mod state       (see Mod data)
  meitou-mods/environment.json  Meitou only: code mods in use  (see Mod data)
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

The save-file state machine's per-frame check (`FUN_1404742b0`:21-41, state +0x1e0 == 1, worker pending) is log-only: when the worker handle's held check returns 0 it logs 'Thread seems to have stopped running' and returns with the state unchanged (**Verified**; 'dead worker' is not established). Meitou's save is one `Task.Run` whose exceptions are reported, so it has no such case.

### Importing a game (`SaveManager::importGame`, `FUN_140378a30`)

**Verified** (decompile of `FUN_140378a30`, `FUN_140377710` (zone files), `FUN_14047bad0` (the window's press) and the callees named below).
Import is a new game that brings part of an old save. The window's ticks are the flag bits ([window](../game/ui-screens.md#title-screen-and-its-windows)):
Reset squad positions 0x1, Buildings 0x4, Research 0x8, Dead NPCs 0x10, Relations 0x20; 0x2 is always added and nothing tests it. In order:

1. `<save>/quick.save` must exist, else the request fails. The version string becomes "1.0.68" and the whole world is reset as a new game does
   (`FUN_14036cb80`: factions, platoons, research, unique table, player name = the `204-gamedata.base` faction's name or "Nameless"). Factions are made from the
   FACTION records with **no** saved faction states. While background loading is busy the request is retried next frame.
2. The old `quick.save` is read into the save container. The clock hour is set to 10, then step 8 overwrites the clock.
4. Always (`FUN_140372560`): every non-nest town of the save gives its `discovered` and `explored` to the new town; player towns are set discovered and explored. **Dead NPCs**
   also applies the save's `replacementTown`. No town at all logs "No towns loaded." Then the towns get their [artifacts](../game/economy.md#artifacts) as in a new game.
7. The player's squads: the save's PLATOON records whose `faction stringID` is the player faction and whose name is not `__DEAD_SQUAD__`. The `squad index` is erased; with
   **Reset** the platoon `position` is first (500, 0, 51000). The squads are loaded (`FUN_1407f88c0`; one that does not resolve shows "One of your squads has somehow loaded off
   the map, reload your game with [reset positions] selected..."), each squad's platoon file is copied whole from the save (only if absent), and every `unique` CHARACTER in it is
   entered in the unique table, claimed, with a null handle. Members, inventories, medical state and stats are therefore the old files'.
8. No player squad left: `platoon/Nameless_0.platoon` of the new working folder is loaded, "Load game error! Your squad has been salvaged. Awarded 10000 cats as compensation." and +10000.
9. **Research**: off, research is reset and `_Default Start` is completed; on, the first RESEARCH record is read (`FUN_1408360f0`). Then from CAMERA: `time day/hour/minute` and the sky
   transition (`FUN_14066db50`), money = max(0, `player money`) (+10000 when salvaged), the faction name = `pfaction name` (empty: as step 1), the camera pose
   (`FUN_1407ade60`) and the squad option bools (`FUN_1403ebf50`).

### Object handles and slot lists

| Part | Meaning | Status |
| --- | --- | --- |
| `TYPE` | `FcsRecordType` of the object's class: 0 BUILDING, 1 CHARACTER, 13 TOWN, 34 PLATOON, 85 NEST; **11 (NULL_ITEM) = the null handle**. The null *object* the reader substitutes is `{type 11, 0, 0, 0, 0}`, but a written null handle may keep stale `CS` and/or `S` values (never `C` or `I`): 620 of the 7687 type-11 handles in the sample have a non-zero other part (always `CS` and/or `S`) (e.g. `TI town`, `residents`, `slaver`, `ownedby`) | **Verified**: object C++ class `hand` (RTTI); data at `141e3a600` holds ints 11,0,0,0,0 (read from the exe image); `hand::vfunc_0` (`140031b88`) is a validity test on `type != 11` only (decompile); the reader `FUN_1406bd000` returns that object when `<prefix>I` is absent. Value census over all 70 files: types 0, 1, 11, 13, 34, 85 only |
| `C` | the **container** id the object lives in | **Verified** for buildings: `handleC = Y * 64 + X + 1` for the zone file the building is in (1496 of 1496 GAMESTATE_BUILDING in 13 zone files); for characters `C` is the container id of the platoon: all characters of a `.platoon` file share the PLATOON record's `handleC` and `handleCS` (56 of 56) |
| `CS` | the container's **serial** | **Verified**: 11111 for all zone containers (1496 of 1496); for platoons a random 32-bit number shared by the platoon's characters |
| `I` | slot index of the object in its container | **Verified** within a zone: building `handleI` unique per file (0 duplicates in 1496) and always in that zone's slot list. Platoon members count 1, 2, 3... (**Verified**: the `I` values of the characters of each of the 56 files are exactly 1..n); the platoon's own handle has `I = 0`, `S = 0` (**Verified**, 56 of 56) |

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
| `player money` (int), `squads` (1), `members` (1), `floor num`, `formation` | player squad summary (`FUN_140375360` reads them from the player object) |
| `pfaction name` ("Nameless") | the player's faction name; **Verified** it equals the name of the GAMESTATE_FACTION whose `gamedata stringID` is `204-gamedata.base` (record 670) |
| `selected_gui_object*`, `selected_character*`, `selected_characters<k>*` (handles) | current selection; the sample selects the player's character (handle C 1, I 1, TYPE 1) |
| `tracking*` (handle) | the character the camera follows (null in the sample) |
| `area` (string), `biome_text_groud` (BIOME_GROUP id), `biome_text_timer` (float) | the region name banner state; **Verified** `biome_text_groud` resolves to a BIOME_GROUP, `area` is "Okran's Gulf". `biome_text_timer` is the banner's 30 s cooldown, `biome_text_groud` the group last tracked (`FUN_140728af0`, empty for none), `area` the name of the group under the camera pivot, empty for `NONE` (`FUN_140375360`); [Biome banner](../game/ui-screens.md#biome-banner). Meitou writes and reads all three |
| bools `bl ditch eject ep feed heal help rescue share shootfirst sit sleep stay`, floats `bs cod gdm ht nnm ps rs`, `formation` | squad behaviour toggles and short-key game toggles (`FUN_1403eb710`, `FUN_1403ee910`): **Observed**; the floats `bs cod gdm ht nnm ps rs` and bools `ep bl heal` are the new-game advanced options (**Verified**: saver `FUN_1403ee910`, loader `FUN_1403eef20`; keys, defaults and load rules in [character-stats](../game/character-stats.md#new-game-advanced-options-that-change-these-mechanics)); Meitou reads and writes them; the squad toggles' keys and the int `formation` (the player faction's squad formation) are in [squad AI options](../game/ai-tasks.md#squad-ai-options) |
| reference list `zones` | one entry per zone **that has a file**, written in grid order (X outer, Y inner; `FUN_140375360` loops 64 x 64): target = the list index in **hexadecimal** ("0".."9", "a", "b", "c" for 13 entries), values `(x, y, 1)` = (zone X, zone Y, has-file flag). **Verified**: the 13 entries equal the 13 files of `zone/` (probe), and the key spelling is seen in the sample |
| `meitou seed` (string) | Meitou-only: the world seed in decimal, written on every Meitou save and restored before the world's first tick on load (so the nests it did not write roll again where they were, Nests); a save without it keeps the launch seed. The original's keys are read by name, so it is ignored (as the other Meitou-only keys) |

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

### Character appearance (CHARACTER_APPEARANCE in `.platoon`)

**Verified** (dump of save 2's `Nameless_0.platoon`): the same key set as a `.bod2` body file ([characters](../characters.md#body-files-bod2)):
bools `sex female`, `in editor`; ints `Age`, `body version` (2); floats: every body/face slider of the race's editor limits (`Height`, `Waist`...), the template's
other sliders at 100 (`Brow height`, `Cheekbone`... not in the human XML), face pose weights by pose name (`wide_nose` 0.02, `close_eyes` -0.64...), `Hair Colour`,
`Hair Saturation`, `Hair Brightness` (HSB x 100), `Posture`, a float `Age` besides the int; vec3 `Skin Tone`; strings `head` (HEAD), `hair style`, `beard`
(ATTACHMENTs, mod records allowed), `idle stance`; reference `race`. Meitou (`Saving/SavedKit`) draws every loaded character (player and NPC) from it: the record
is the look's appearance, worn items its clothes; a character with no saved items keeps clothes rolled from its serial but the saved body and face. A record
with only race and flags (no head, no floats) is not a look and the look is rolled. The record is written back only for a new character (the generated look) or
one changed in the character editor (its keys replaced by the edited copy's, so the key set and order stay the original's).

### NPC platoons (Meitou mapping)

PLATOON keys (**Verified**):
- `sqt` is the town pool the platoon counts against (platoon +0xa8; by code: written by `FUN_140372ef0`, read by `FUN_1407ed0f0`, setter `FUN_1409c44b0` makes 0 into 1
  and 3+ into 2, default 2): 1 residents (and bar squads), 2 roaming; every wild platoon of the samples has 2 (nests have only pool 2).
- `hometown` (handle) puts the platoon into that town's or nest's pool at load (`FUN_1407ec8d0`; the platoon's faction and player status must match the town's);
  the pool counts living characters, `char count` while the platoon is not active (`FUN_1408f6a40`). `basetown` is only kept as a TOWN record pointer.
  Meitou rewrites `char count` from the platoon file when it saves, and keeps the save's value for a platoon with no file.

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
  Meitou stands the nest buildings and debris meshes up again each load ([nest furniture](../game/factions-squads-towns.md)), writing neither; a nest's
  real items are loose items of the zone's list (`GroundItem.Camp`, kept past the dropped-item limit), written with the nest's `persist` handle (type 85; a
  `spawn in town centre` town's: its type-13 state `hand`, as the original copies the town's own handle onto debris and loot alike, `FUN_140937510`, `FUN_140938440`). The item's `persist` is
  its `hand` at +0x198: written as `persist` unless it is the item's own (`persistant` true) or null (`FUN_1407609e0`, read by `FUN_140761550`; **Verified** by code).
  **Observed** (headless, two Beak Thing nests loaded, 1074 drops past the limit, then a capture): their 22 eggs and loot kept, 12 and 10 with their own nest's state handle.

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
Slave state (**Observed**, `FUN_140626580` writes, `FUN_140626830` reads, in the character's state record with `personality`): `slavestate` int (status `+0`: 0 free, 1 slave, 2 escaped, 3 ex-slave; the reader stores any int unchecked, **Verified** `FUN_140626830`; Meitou keeps 0 to 3; each rule tests the state as the original's function does (`== 0`, `!= 0`, `== 1`, `== 2`, `< 1`: the ex-slave 3 counts as a slave where the original tests non-zero, e.g. `FUN_1405a0f10`, `FUN_140852350`, `FUN_140680560`'s "Free!") and loads anything else as free), `ssct` float (`+0x8`, the game hours of the last change to state 2 or 3, [lie low](../game/carry-loot.md#escaped-slaves-lie-low)),
`escap` bool (`+0xe8`, the escaped-prisoner flag), `disguiseblown` float (`+0x110`, the [disguise](../game/perception.md#disguise) cooldown and recently-wanted marker; Meitou `CrimeSave`) and two short-keyed bools (`kidn` `+0xe9`; `tn` `+0xea`, the freed flag, names from `DAT_1416fa808` / `DAT_1416fa804`, **Verified**); the owner is the `slaver` handle, on an NPC slave a CHARACTER handle naming its owner (**Observed** on one
Rebirth save, read with a scratch FCS reader: all 32 state-1 slaves name a saved Holy Sentinel of the owner faction; the two escaped player characters hold the null handle).
Meitou (`Saving/SlaveSave`) reads and writes `slavestate`, `ssct` and the `shaved` bool (`CharacterCold.Shaved`, the shaven head of
[processing](../game/ai-tasks.md#slave-work-shout-and-capture); all 32 NPC slaves of the Rebirth save have it set), and the owner as `meitou slave owner` (FACTION string id). It reads a CHARACTER `slaver` handle: once that character is
in the world, its faction is the owner when the save has no owner key, and its squad is the master (`CharacterCold.SlaveMaster`, `CaptiveSystem.RestoreSlavers`, every 20 ticks);
a squad handle is not mapped. The read keys are written back unchanged until the slave is freed or handed to another master, then as the null handle. `CrimeSave` reads and writes `tn` (`CharacterCold.Freed`, written always).

Job lists (`Saving/PermaJobSave`): the original's GAMESTATE_AI (`ai`) keys `jobs`, `pjob<i>`, `pjobT<i>` ([perma-jobs](../game/ai-tasks.md#perma-jobs)), plus
Meitou's string `meitou pjob<i>` (the job's building id, which wins on load over the handle); a job on a character writes that character's handle once
every character has one, as `tjobT<i>`, so one new to the save is named too. The squad AI options are CAMERA bools (**Verified**, writer
`FUN_1403eb710`; the key of each option and new-game values in [squad AI options](../game/ai-tasks.md#squad-ai-options)); Meitou reads and writes nine
(not `stay`, `eject`), an absent one keeps its default.

### Order queue

A player character's order queue (the deque at `AITaskSytem` +0x40, [order queue](../simulation.md#order-queue)) is in its GAMESTATE_AI record `ai`, after the job list. Meitou: `Saving/OrderSave`.
- **Keys** (**Verified**, `AITaskSytem` vfuncs 1 / 2: writer `FUN_1405090b0`, loader `FUN_1405092d0`): per order i from 0, front first, int `tjob<i>` (the entry's TaskData task type), vec3 `tjobP<i>`
  (the entry's point) and the handle `tjobT<i>` (five ints `TYPE`, `C`, `CS`, `I`, `S`, the entry's target, as `pjobT<i>`; the null handle, `TYPE` 11, when it has none). Nothing else of an
  entry is saved: not whether it started, not its failure text. The writer and the loader act only for a character whose faction has the player-faction field (+0x250) set.

- GAMESTATE_CHARACTER float `age`: the character's age (1 for people, an animal's growth, [animals](../game/animals.md#ageing)); **Verified** in vanilla saves.
- GAMESTATE_CHARACTER `meitou kidnapped` (bool, held by a kidnapper: no sentence, no bail; not the original's `kidn`), `meitou recognition` (float 0..1, bounty recognition meter). The escaped and [kidnap victim](../game/carry-loot.md#the-kidnap-victim-flag) flags are the original's `escap` and `kidn` (above; `CrimeSave` writes them always). Older Meitou saves wrote `meitou escaped` and the recently-wanted seconds as `meitou recently wanted`; each is read when the original's key (`escap`, `disguiseblown`) is missing and dropped on save.
- GAMESTATE_BUILDING `meitou house locked` (bool, a lock-up round locked this home and nothing unlocked it since).
- GAMESTATE_BUILDING `meitou lock <layout index>` (int level x 4 + broken x 2 + locked) for each interior cage or container whose lock is locked or broken (their ids are not mapped, see above).

### Player outposts

An outpost's entry (**Verified** by code, `Town::vfunc_17` `14036eb20` with `FUN_140386d20`): id = the town's stored
instance id, or a new `<n>-quick.save-INGAME` (kept on the town, so later saves reuse it); target = the town's TOWN
record (`player_town_template`); position = the town's, absolute; quaternion (0, 0, 0, 0); state `<id>-S5e`. Its
full runtime GAMESTATE_TOWN (`Town::vfunc_85` `140376b80` and the TownBase writer) is labeled `Town state <name>` and stores the display name in string `name` (restored by `FUN_14036fc30`, **Observed**). It has the base
towns' keys: `instance` = the entry id, `is nest` false, `building material`, `public` (Town +0x380, the [town panel](../game/ui-screens.md#town-panel)'s Open to public, false when made; **Verified**, `param_1[0x70]`; Meitou round-trips it as `PlayerTown.Public`), `plevel`,
`replacementTown` (the string id of the TOWN in the town's place, "" none: [town overrides](../game/factions-squads-towns.md#town-overrides)), `trade goods`, optionally `faction` = the player faction, `factions here<i>`, `discovered` / `explored` /
`recently discovered`, `artifacts count`, the handle `hand*` (type 13 TOWN) and the population floats; plus float
`override` (Town+0x51c, the player town's [reassessed radius](../game/buildings-production.md#placement); 0 when made), written only for a town
whose resolved faction has the player flag. **Verified** (native float-map access in `FUN_140376b80` / `FUN_1403715c0` and independently decoded Beep Fan Club 1): Drycoom stores 2652.530029296875 and Goonrock 2186.80908203125, with no integer `override`. Meitou reads/writes that float and removes an obsolete integer shadow field; an integer `override` from earlier Meitou builds is read only when no float exists, and a name falls back to the record label untrimmed (the original reads the string `name` as is, **Observed**, `FUN_14036fc30`, `FUN_1403715c0`).
Buildings name it by their `town` handle. **Verified** (`FUN_140555650`, the building-state load path; `FUN_1402a14b0` / `FUN_1402a1750`, building state hooks; `FUN_1406bd000`, the handle reader): the town reference is resolved through the saved handle and an unresolved reference becomes the canonical null handle, not a nearest-town lookup. **Verified** (`FUN_14092ae80`): removing an overlapping town does not rewrite those handles. A later town reassessment is not part of `loadGame` after this removal, so a building whose handle no longer resolves remains without a player-town assignment; Meitou maps that unresolved handle to town 0, as it does valid NPC-house furniture. A town-0 machine joins the power grid of the nearest town (**Observed**, [buildings-production](../game/buildings-production.md)); town 0 does not gate player-owned research. A player building whose handle a non-player building or town state of the save carries (a level town's, or NO_TOWN's: **Observed**, the original's Wind Generator II 1099 built outside every radius) resolves in the original and stays in that town: Meitou reads it as `BuildSite.ForeignTown` (town 0, never given the nearest outpost, written back as read; see [buildings-production](../game/buildings-production.md)). The obsolete Meitou-only unresolved bool is removed when writing.

**Verified** against independently decoded `Beep Fan Club 1/quick.save` (2026-11-12): Drycoom and Goonrock each have a sparse linked state named `0` with instance/handle metadata and no faction, plus a separate full runtime state with the same dynamic instance id, player faction, string `name`, public=true and float radius. These are two state roles for the same outpost, not evidence of a separate level-town placement.

**Verified** (native 1.0.68, `FUN_140376b80`, `FUN_1403715c0`): `public` is a town-state bool. `override` uses the float map and is written for the resolved player faction, not for an arbitrary nonnull faction.

**Verified** (native 1.0.68, `FUN_140373f00`, `FUN_1403728c0`, `FUN_1403722d0`, `FUN_14036b710`): the GAMESTATE_TOWN pass is independent of GAMESTATE_TOWN_INSTANCE_LIST. Without a town list, a state still loads when its `instance` resolves to a level/runtime town and it passes the nest and discovery checks.

**Verified** (native 1.0.68, `FUN_1403728c0`, `FUN_1403722d0`): the loader considers every matching GAMESTATE_TOWN record associated with an instance, not only the instance's first state id. The original sparse instance-linked state carries the instance, handle and zone metadata; a separate full runtime state carries name, ownership, public flag, radius and level (**Observed**, `FUN_140371370` / `FUN_140376b80`). Meitou retains its all-state synchronization policy to avoid stale secondary-state values in existing saves: it writes these live properties, including string `name`, to every matching state. This is an engine-specific representation, not exact original writer-role parity.

**Verified** (`FUN_140373f00`, `FUN_1403728c0`, `FUN_140935120`, `FUN_1403722d0`, `FUN_14092ae80`, `FUN_1400f40e0`, `FUN_140581770`, `FUN_1403715c0`): the original's town list order is the towns of the level data first, then the save's list entries in file order, and overlaps are normalised in that order (so of a level town and a dynamic outpost each inside the other's radius the level town, the earlier, goes). Meitou reads the level towns in `PopulationData.Sites` order first (a level town that has a list entry is read from the entry at that place), then the other entries; the town numbers follow. An instance keeps its attached states in a map by record type, so only the LAST listed GAMESTATE_TOWN applies in the attached pass (Meitou applies only that one; the type-39 STATE preference is not read). `public` is assigned by every application (a missing key is false) and for a non-player owner replaced by the template's `is public`; `override` is read only while the owner is the player and only when the key is present (Meitou tracks the owner per applied state).

**Verified** (native 1.0.68, `FUN_14092ae80`, `FUN_140373f00`): overlap normalisation uses squared XZ distance and a strict `<` comparison against the radius squared of the nearest other non-nest town that is owned by the player faction (a candidate with another owner is skipped, so an NPC town never removes a player town). It removes the current town (the one iterated, which is the earlier of two in list order) and leaves the other; this function does not rewrite building `town` handles. `loadGame` calls this normalisation after the town-state passes and does not call the placement-time reassessment/merge path afterward. A building whose handle no longer resolves therefore remains unassigned; Meitou maps that unresolved handle to town 0, as it does valid NPC-house furniture. A town-0 machine joins the power grid of the nearest town (**Observed**, [buildings-production](../game/buildings-production.md)); town 0 does not gate player-owned research. The obsolete Meitou-only unresolved bool is removed when writing.

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

### Faction trust, war flags, bed rent and turrets

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

- Whether `loadGame` rejects or adapts to a `mods` list that differs from the current load order (nothing in the sample: single base-game save).
- Prisoners, dead characters, bounties, trade and mission state, and player outposts: not present in an early-game sample. Player-built building and outpost keys are known from the code only ([zones](zones.md#placement-keys), [outposts](#player-outposts)).
- How relations, trust and `trustNeg` change during play (only the fresh-world values are explained), what the player faction (no relation table, but a `known` list (the [factions met](../game/factions-squads-towns.md#36-factions-met)) and `global trust`) uses instead, and how the player table's `global trust` (`+0x14`) relates to the `+0x18` dialogue reads ([trust](#faction-trust-war-flags-bed-rent-and-turrets)). Which key the squad memory's shared player entry is saved under (squad memory).

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Save requests (`FUN_14047c310`)
- What `saveGame` writes (`FUN_140375360`)
- `loadGame` (`FUN_140373f00`)
