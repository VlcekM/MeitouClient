# FCS game data: `.base` / `.mod`

Sources:
- **Round-trip (Verified 2026-10-04)**: `Meitou.Data.Fcs.FcsReader` follows exactly this layout, and
  `FcsWriter` writes all four base-game files (`gamedata.base`, `Newwworld.mod`, `Dialogue.mod`,
  `rebirth.mod`) back **byte-for-byte identical** (`FcsRoundTripTests.Base_game_file_round_trips_byte_for_byte`).
- **FCS**: the official editor, `forgotten construction set.exe`, is a .NET program. Its load/save code
  (class `GameData`: `load`, `readHeader`, `Item.load`, `Item.save`, `save`, `writeHeader`) was
  decompiled for interoperability and is the source of field meanings below. Caveat: that is the
  *editor's* behaviour; the game (`kenshi_x64.exe`, native) has its own loader.
- **Game**: `kenshi_x64.exe` was disassembled/decompiled (Ghidra, for interoperability). Its loader
  is described in [Game loader](#game-loader); where it differs from FCS, **the game is what we
  implement**. Function addresses refer to the current Steam build.

All values little-endian. `string` = `int32 byteLength` + UTF-8 bytes (FCS reads and writes UTF-8;
also Verified on base files: 92 non-ASCII strings, all valid UTF-8). The reader **rejects** invalid
UTF-8 with `FcsFormatException` rather than silently changing bytes.

## File

```
int32   fileType            16 or 17 (see below)
  [17 only] uint32 headerLength   bytes from here to the end of the header
int32   version             1 in all base files ("Version" in the editor's mod header)
string  author
string  description
string  dependencies        comma-separated, e.g. "gamedata.base,Newwworld.mod" ("" = none)
string  references          comma-separated ("" = none); the editor drops names already in dependencies
  [17 only, if headerLength leaves room]
  uint32  saveCounter       incremented on every save
  uint32  lastMergeResolve
  uint8   mergedCount       × (string file, uint32, uint32)
  [17 only, if headerLength still leaves room]
  uint8   deleteCount       × (string file, uint32 version, string items)  items ':'-separated string ids
int32   nextId              editor's id counter (number part of the next "<n>-<file>" id); 0x4C67BE in all base files
int32   recordCount
Record  records[recordCount]
```

- FCS also accepts file types 1–15 (old formats without this header, and with different record
  layouts); it rejects anything else. The game accepts **8–17** (see [Game loader](#game-loader));
  type 15 is the header-less layout used by the world `.zone`/`.level` files. Our reader supports 15, 16 and 17
  only. Old mods are rare: since game version 0.92 mods are re-saved as 17 (Observed: of 50 Steam
  workshop mods, 25 are type 16 and 25 type 17).
- FCS always writes type 17 with both bookkeeping sections. `Dialogue.mod` (9 bytes: save counter 1,
  no merges, no delete section) was presumably written by an older editor (Observed from the byte
  count); `rebirth.mod` (save counter 9) has both, empty.
- FCS reads `mergedCount` entries but stops early if an entry's string length is > 256 (defensive
  quirk; not seen in data).
- Merge / delete bookkeeping is used when one mod was merged into another in the editor: on load,
  records named in a delete request are reverted unless changed since `version`. The game never
  reads it (Verified: it skips the whole type-17 header by `headerLength`, see
  [Game loader](#game-loader)).

## Record

```
uint32  byteSize            size of this record in bytes, including this field. Newer FCS writes it,
                            older wrote 0 (base files except rebirth.mod); FCS ignores it on load.
int32   type                FcsRecordType (the editor's itemType enum)
int32   id                  numeric id; only used by format < 7 files to build the string id
string  name
string  stringId            unique key "<n>-<file that created it>", e.g. "1292-gamedata.base".
                            Format < 7 files have no string id; it's built as "<id>-<filename>".
uint32  flags               bit 0 MODIFIED, bit 1 RENAMED, bits 4+ save counter (below)

7 property lists, each: int32 count, then count × (string key, value):
  bool       1 byte
  float      float32
  int        int32          ints, enums (as their int value), colours (0xRRGGBB)
  vec3       3 × float32
  vec4       4 × float32    a quaternion, x y z w
  string     string
  filename   string

int32   listCount           reference lists
  string  listName          e.g. "conditions", "dialogue package"
  int32   count
    string  targetStringId
    int32   v0, v1, v2      the "(0, 24, 0)" values in fcs.def; all three int.MaxValue = removed

int32   instanceCount       placed instances (e.g. lights and nodes inside a building)
  string  id                instance id (unique within the record)
  string  target            stringId of the placed record; "" = removed
  float32 x, y, z
  float32 qw, qx, qy, qz    rotation, **w first** (unlike vec4 properties)
  int32   stateCount
    string state            stringId of a state record (the instance's "states" list)
```

Verified by the round-trip: bools are always 0/1, no record repeats a key within one list or a
reference list name, and the `byteSize` values in rebirth.mod equal the real record sizes (the writer
recomputes them and the output is identical).

## Merging

Files load in order (see [overview.md](overview.md#load-order)) into one table keyed by `stringId`.
`Meitou.Data.GameDatabase` implements the game's rules (`LoadOrder` builds the order, `meitou-tools load`
summarizes the result). Each rule says what FCS does and what the game does (**Game**, Verified by
decompilation; details and addresses in [Game loader](#game-loader)). Where they differ, the game wins.

**Verified on the base game** (`GameDatabaseTests`): the four base files merge into 54,951 records
(3,841 of them changed by a later file) with **no issues**: no modifying record lacks its base, no id is
defined twice, no type changes, and no reference in the result points at a missing record. Iron Rock
(`14520-rebirth.mod`) ends up with rebirth.mod's 7 values, gamedata.base's other fields and its
`building` reference. None of the game-vs-FCS differences below changes the base-game result (Observed
by a one-off scan of the four files: no key in two property lists of one record, no key over 64 bytes,
no string over 4096 bytes or containing NUL, no name or id over 512 bytes, no partial
reference-removal values, no name change without the RENAMED flag, no instance re-listed with states).

- **New vs. modifying** (`flags` bit 0). FCS: bit 0 clear defines the record, set changes a record from
  an earlier file; it normalizes first (bit 0 clear clears bit 1). Base files store
  `0x80000002`/`0x80000001`/`0x80000003` (save counter 0x8000000, so bit 0 decides), newer files `0x10`
  (new, counter 1), `0x11` (modified), `0x13` (modified + renamed), `0x90`, ...
  **Game**: bit 0 only decides whether a warning is logged when the record has no base. Otherwise
  "new" and "modifying" records are applied the same way: if the id exists, the fields merge into it;
  if not, a record is created.
- **Fields merge one by one.** Loading a record writes each listed key into the existing record's
  values (a later file overrides earlier ones per key); keys not listed keep their values. When saving,
  FCS writes only the keys that differ from the base. Example: `14520-rebirth.mod` ("Iron Rock") is a
  full record in `gamedata.base`, and `rebirth.mod` re-lists it with flags `0x11` and only 7 fields.
  (Ids keep their origin: that record was created in rebirth.mod and later baked into gamedata.base.)
  **Game**: same, per key and **per kind**: a record has seven separate tables (bool, float, int, vec3,
  vec4, string, filename), so one key can exist once in each, and a value only replaces the same key
  in the same table.
- **Renaming** (`flags` bit 1). FCS: with bit 0, the record's name becomes `name`; without it a
  modifying record's `name` is ignored. **Game**: an existing record is renamed when bit 1 is set
  (bit 0 is not checked); a newly created record always takes `name`.
- **A string property only overrides a string** (FCS skips a string value whose key already holds a
  different type). **Game**: no such rule; the case can't arise because each kind has its own table.
- **Removing a record**: bool property `REMOVED` = true. The record is dropped (FCS keeps it, flagged,
  only while editing the mod that removes it). FCS writes REMOVED with bit 0 set. **Game**: after
  applying a record it looks up `REMOVED` in the record's merged bool table; if true, the record is
  taken out of every index. A later file listing the same id creates a fresh record (no old fields).
  `REMOVED = false` stays as an ordinary field.
- **References** merge per (list, target): a later file adds the reference or overwrites its values.
  FCS removes when `v2 == int.MaxValue`. **Game**: removes only when **all three** values are
  0x7FFFFFFF. A new reference is appended at the end of its list (list order = file order); an
  existing one keeps its position and gets the new values. References whose target doesn't exist stay
  in the list (they resolve to nothing). In memory a reference holds three ints at offsets 0, 4, 8, then the
  target id (Verified, spawn code); that they are v0, v1, v2 in file order is Observed (the data patterns match
  fcs.def, [characters.md](../characters.md#generating-a-character)).
- **Instances** merge per instance id: later files overwrite position, rotation and target. FCS adds
  states. **Game**: states are **appended** to the instance's existing states, with no duplicate check.
  An empty `target` does not delete the entry: the instance stays with an empty target and no states
  (a later file can fill it again).
- A record changing **type** between files is an error in FCS (`ChangedItemType`). **Game**: no check;
  the type of the first definition is kept and the later record's fields are merged anyway.
- A **new** record whose id an earlier file already defines is an error in FCS (`ItemAlreadyDefined`),
  but its fields are still merged into the existing record. **Game**: merged silently, exactly like a
  modifying record.
- A modifying record whose id no earlier file defines is "missing" in FCS (error `ModifiedItemNotFound`);
  the editor can skip such records. **Game**: logs `[Mods] Item <name> (<id>) modified by <file> does
  not exist` and then **creates** the record from it (its type, name and only the fields it lists).
  Observed: 14 such records in 2 of 50 workshop mods (presumably records of other mods; ids not
  checked); if the mod they depend on is missing or loads later, the game ends up with a partial record.

## Game loader

How `kenshi_x64.exe` reads and applies `.base` / `.mod` files. All **Verified by decompilation** unless
marked otherwise; addresses are functions in the current Steam build. The order of files is in
[overview.md](overview.md#load-order).

- **Load sequence** `FUN_140870ae0` (runs once): the four core files, then the core translation file,
  then the mods from `data/mods.cfg` in order, then each mod's translation, then two post-passes
  (`FUN_1406c34f0`, `FUN_1406bde00`, below), then the core files' world `leveldata.level` files (same
  format, into a separate table). Finally the `CONSTANTS` record (type 27) named `GLOBAL CONSTANTS` is
  looked up **by name and type** (`FUN_1406bfda0`) and becomes the game's constants.
- **One file** `FUN_1406c0b50` (`GameDataContainer::load`, named in its own log text). Opens the file
  in binary mode; a file that can't be opened fails. Reads `fileType` and accepts **8–17**; anything
  else logs `[GameDataContainer::load] Invalid data file '<path>' (Version: <n>)` and fails.
  - Type 17: reads `headerLength` and skips that many bytes, so version, author, description,
    dependencies, references, save counter, merge and delete bookkeeping are **never read**.
  - Type 16: reads `version`, then skips the four strings.
  - Types 8–15: no header. Then `nextId` and `recordCount` for every type; the game keeps the largest
    `nextId` of all files (for records created in game, whose ids end in `-INGAME`).
  - Per record: `byteSize` (ignored), type, numeric id, name, stringId, then `flags` only if the file
    type is 15 or more. Only flag bits 0 and 1 are used (bit 31 is masked off, the save counter unused).
  - Lookup by stringId is exact (case-sensitive). Found: renamed if bit 1 (`FUN_1406bfb70`), type left
    alone. Not found: warning if bit 0, then a new record with this record's type and name is added
    (`FUN_1406bf780`).
  - Property lists in file order bool, float, int, vec3, vec4, string, filename (vec3/vec4 lists only
    from file type 9), each into its own table (`FUN_1406c9320`, `FUN_1406ca320`, `FUN_1406c9990`,
    `FUN_1406ca670`, `FUN_1406ca9e0`, `FUN_1406c9ce0` twice); a key overwrites the same key in that table.
  - References (`FUN_1400b61b0` set or append, `FUN_1406cb110` remove), instances (`FUN_1406cae60`
    set, `FUN_1406cafe0` clear), REMOVED (`FUN_1406bf680`): rules in [Merging](#merging).
  - A record remembers the index of the file that **created** it; an instance remembers both the file
    that created it and the last file that set or cleared it (core files 0–3; all mods share one
    index, the number of core files; translations −1). What uses it is Unknown (probably saving).
- **Truncation** (fixed buffers; values are cut silently, and also at the first NUL byte): property
  keys **64 bytes**; name, stringId, reference list name, reference target, instance id and instance
  target **512 bytes**; string and filename values **4096 bytes**. Observed: neither base-game nor
  workshop data (50 mods) comes near these limits.
- **Older file types** (Verified only this far): flags exist from type 15 (before that every found
  record takes the new name); before 15, instance ids and states are ints that get turned into
  string ids; before 11 a reference has one value; types 11, 13 and 14 carry an extra per-record
  list (string + byte) that filters which keys and references are applied; a `CONSTANTS` record in a type ≤ 13 file triggers a "mod is out of date, re-save it in the
  construction set" warning. The details are Unknown and not needed until such files turn up.
- **Post-passes** after all files: `FUN_1406bde00` resolves every reference target to its record
  (missing targets stay as empty links). `FUN_1406c34f0`: for every BUILDING (type 0) whose int
  `link type` is not 3, each building in its `wall subsections` list gets a `wall master` reference
  (values 0) back to it. That reference is derived; no file stores it.
- **`.translation` files** use the same format and loader (`FUN_1406c29a0` calls `FUN_1406c0b50`), so a
  translation is just another layer of record changes. The core one (chosen by the language setting)
  is applied after the four core files; a mod's own is looked for under `<mod folder>/locale/`
  (`FUN_14086f240`) and applied after all mods. How the language folder is chosen is not analyzed.
- **Errors don't stop loading**: a failed core file shows "Failed to load the core '<file>' file";
  failed mods are collected into one "Mod error(s) (Ignored files)" dialog. What happens with a
  truncated or corrupt file mid-record is **Unknown** (no checks were seen; the stream just runs out).

## Record types

`FcsRecordType` is the editor's `itemType` enum (consecutive from 0, 113 values; source FCS). The base
game uses 78 of them. Before the editor was decompiled, the names were derived independently by
matching each type's field keys against `fcs.def` sections (`meitou-tools fcs-types`); every match
agreed with the editor's numbering, and the test
`FcsSchemaTests.Record_type_names_match_fcs_def_against_base_game` keeps checking it. The evidence:

"Fields" = distinct keys used by records of that type; "found" = how many the matching fcs.def
section declares. Field kinds agree too: every field found in the schema is stored in the list its
fcs.def default implies.

| Type | Name | Records | Fields found | Runner-up | Notes |
| ---: | --- | ---: | --- | --- | --- |
| 0 | BUILDING | 1148 | 77/111 | CONTAINER 6 | many extra fields not in fcs.def (`sound x`, `is door`, ...) |
| 1 | CHARACTER | 988 | 53/61 | ANIMAL_CHARACTER 24 | |
| 2 | WEAPON | 68 | 40/43 | CROSSBOW 26 | |
| 3 | ARMOUR | 257 | 73/80 | CONTAINER 29 | |
| 4 | ITEM | 416 | 32/36 | MAP_ITEM 25 | |
| 5 | ANIMAL_ANIMATION | 68 | 28/28 | ANIMATION 28 | tie; ANIMATION is clearly 24 |
| 6 | ATTACHMENT | 103 | 17/19 | HEAD 2 | |
| 7 | RACE | 99 | 104/110 | STATS 2 | |
| 10 | FACTION | 161 | 74/78 | TOWN 5 | |
| 13 | TOWN | 436 | 28/30 | FACTION 4 | |
| 16 | LOCATIONAL_DAMAGE | 11 | 12/13 | CHARACTER_PHYSICS_ATTACHMENT 1 | |
| 17 | COMBAT_TECHNIQUE | 60 | 40/42 | ANIMATION 12 | |
| 18 | DIALOGUE | 2203 | 19/22 | WORD_SWAPS 16 | |
| 19 | DIALOGUE_LINE | 23889 | 76/80 | WORD_SWAPS 26 | |
| 21 | RESEARCH | 275 | 23/24 | NEW_GAME_STARTOFF 2 | |
| 22 | AI_TASK | 143 | 4/5 | — | |
| 24 | ANIMATION | 197 | 63/64 | ANIMAL_ANIMATION 29 | |
| 25 | STATS | 62 | 33/50 | ANIMATION 5 | extra stat fields (`xp`, `endurance`, ...) not in fcs.def |
| 26 | PERSONALITY | 34 | 17/17 | — | all looped fields |
| 27 | CONSTANTS | 3 | 93/96 | WEAPON 1 | |
| 28 | BIOMES | 137 | 74/77 | BIOME_GROUP 3 | |
| 29 | BUILDING_PART | 1464 | 30/39 | BUILDING 2 | |
| 31 | DIALOG_ACTION | 21225 | — | — | no fcs.def section; the editor has dedicated dialogue condition/effect code instead |
| 43 | REPEATABLE_BUILDING_PART_SLOT | 4 | 1/1 | BUILDING_PART 1 | |
| 44 | MATERIAL_SPEC | 259 | 12/13 | FOLIAGE_MESH 8 | |
| 45 | MATERIAL_SPECS_COLLECTION | 11 | 1/1 | CHARACTER_PHYSICS_ATTACHMENT 1 | |
| 46 | CONTAINER | 28 | 35/36 | ARMOUR 26 | |
| 47 | MATERIAL_SPECS_CLOTHING | 170 | 7/8 | MATERIAL_SPEC 4 | |
| 49 | VENDOR_LIST | 187 | 15/16 | ITEM_PLACEMENT_GROUP 8 | |
| 50 | MATERIAL_SPECS_WEAPON | 49 | 12/12 | WEAPON 7 | |
| 51 | WEAPON_MANUFACTURER | 18 | 8/8 | FACTION_TEMPLATE 1 | |
| 52 | SQUAD_TEMPLATE | 1166 | 55/59 | UNIQUE_SQUAD_TEMPLATE 39 | |
| 53 | ROAD | 3 | — | — | no fcs.def section; `imagefile`, `color channel`, `spawns`, altitudes |
| 55 | COLOR_DATA | 70 | 2/2 | — | |
| 56 | CAMERA | 1 | — | — | no fcs.def section; one leftover record ("grass1") with grass-like fields |
| 59 | FOLIAGE_LAYER | 330 | 4/8 | FOLIAGE_MESH 1 | `lod range`, `lod levels`, `page size`, `uses foliage system` not in fcs.def |
| 60 | FOLIAGE_MESH | 945 | 42/43 | MAP_FEATURES 10 | |
| 61 | GRASS | 42 | 19/19 | FOLIAGE_MESH 6 | |
| 62 | BUILDING_FUNCTIONALITY | 136 | 29/31 | RESEARCH 1 | |
| 63 | DAY_SCHEDULE | 3 | — | — | no fcs.def section; `layout interior/exterior`, `building` ref |
| 64 | NEW_GAME_STARTOFF | 23 | 12/13 | RESEARCH 2 | |
| 68 | WILDLIFE_BIRDS | 8 | 15/16 | MAP_FEATURES 2 | |
| 69 | MAP_FEATURES | 329 | 15/15 | FOLIAGE_MESH 10 | |
| 70 | DIPLOMATIC_ASSAULTS | 5 | 1/1 | — | |
| 71 | SINGLE_DIPLOMATIC_ASSAULT | 11 | 11/11 | WORD_SWAPS 1 | |
| 72 | AI_PACKAGE | 275 | 10/13 | WEATHER 2 | |
| 73 | DIALOGUE_PACKAGE | 385 | 3/3 | WORD_SWAPS 1 | |
| 74 | GUN_DATA | 11 | 15/15 | CROSSBOW 15 | tie; CROSSBOW is clearly 107 |
| 76 | ANIMAL_CHARACTER | 62 | 36/37 | CHARACTER 20 | |
| 77 | UNIQUE_SQUAD_TEMPLATE | 2 | 26/27 | SQUAD_TEMPLATE 23 | |
| 78 | FACTION_TEMPLATE | 4 | 19/19 | CHARACTER 2 | |
| 80 | WEATHER | 77 | 26/27 | EFFECT 3 | |
| 81 | SEASON | 59 | 4/4 | — | |
| 82 | EFFECT | 126 | 31/34 | FOLIAGE_MESH 4 | |
| 83 | ITEM_PLACEMENT_GROUP | 30 | 8/8 | VENDOR_LIST 4 | |
| 84 | WORD_SWAPS | 317 | 9/10 | DIALOGUE 7 | |
| 86 | NEST_ITEM | 20 | 26/26 | ITEM 20 | |
| 87 | CHARACTER_PHYSICS_ATTACHMENT | 4 | 5/5 | MATERIAL_SPECS_COLLECTION 1 | |
| 88 | LIGHT | 76 | 12/14 | EFFECT_FOG_VOLUME 2 | |
| 89 | HEAD | 41 | 4/4 | MATERIAL_SPECS_CLOTHING 2 | |
| 92 | FOLIAGE_BUILDING | 2 | — | — | no fcs.def section; FOLIAGE_MESH fields plus a `building` reference |
| 93 | FACTION_CAMPAIGN | 295 | 33/35 | WORD_SWAPS 2 | |
| 95 | BIOME_GROUP | 92 | 17/18 | BIOMES 1 | |
| 96 | EFFECT_FOG_VOLUME | 10 | 13/13 | LIGHT 2 | |
| 97 | FARM_DATA | 39 | 23/23 | ENVIRONMENT_RESOURCES 3 | |
| 98 | FARM_PART | 18 | 8/8 | MAP_ITEM 2 | |
| 99 | ENVIRONMENT_RESOURCES | 26 | 19/19 | FARM_DATA 3 | |
| 100 | RACE_GROUP | 11 | 2/3 | NEST_ITEM 2 | |
| 101 | ARTIFACTS | 1 | 6/6 | VENDOR_LIST 4 | |
| 102 | MAP_ITEM | 18 | 27/27 | ITEM 25 | |
| 103 | BUILDINGS_SWAP | 40 | 2/2 | — | |
| 104 | ITEMS_CULTURE | 12 | 4/4 | — | |
| 105 | ANIMATION_EVENT | 18 | 7/7 | — | |
| 107 | CROSSBOW | 11 | 60/63 | WEAPON 25 | |
| 109 | AMBIENT_SOUND | 27 | 6/6 | ANIMAL_ANIMATION 1 | |
| 110 | WORLD_EVENT_STATE | 155 | 6/6 | — | |
| 111 | LIMB_REPLACEMENT | 48 | 43/46 | ARMOUR 24 | |
| 112 | ANIMATION_FILE | 1 | 3/3 | — | |

- Fields stored but not declared in fcs.def (e.g. BUILDING `sound x`, CHARACTER `is military`) are
  real data; the editor just doesn't show them. The reader keeps them.
- `AI_SCHEDULE` (79) is the only fcs.def section with no records in the base game.

## Schema: `fcs.def` (Verified by `FcsSchema` parsing the whole file)

`Meitou.Data.Fcs.FcsSchema` parses it. Text, one section per record type:

```
[AI_PACKAGE]
signal func:     BlackboardSignalFunctions.SIGNAL_NONE "description..."
Leader AI Goals: AI_TASK (0, 24,0) "...val0 and val1 is start and finish time..."
```

- `[A,B]` headers: fields shared by several types (e.g. `[ANIMAL_ANIMATION,ANIMATION]`). A type's
  fields are the union of every section naming it. Spaces after commas are allowed.
- `name: default "description"`. The default's form gives the kind:

  | Default | Kind | Stored in |
  | --- | --- | --- |
  | `True` / `False` | bool | bool list |
  | `100` | int | int list |
  | `1.0` | float | float list |
  | `EnumName.VALUE` | enum (from `fcs_enums.def`) | int list |
  | `#FFFFFF` | colour | int list (`0xRRGGBB`) |
  | `""` / `"text"` | string | string list |
  | `"Ogre mesh\|*.mesh"` (editor file filter) | filename | filename list |
  | `TEXTURE_ANY` / `TEXTURE_DDS` | texture | filename list |
  | `TYPE` / `TYPE (v0, v1, v2)` | reference list to records of TYPE, default values | references |
  | `TYPE (x,y,z) (w,x,y,z)` | placed instances of TYPE (position, rotation) | instances |

  `interjection: null` (DIALOGUE_LINE, "do not edit") is the one odd default; it's stored as a bool.
- `name:` with nothing after it is an editor category heading, not a field.
- `looped` after the default: a numbered series. Declared once as `text0`, stored as `text0`,
  `text1`, ... (Verified: DIALOGUE_LINE `text1`–`text6`, PERSONALITY `tags common1`..., RACE
  `stats good1`...). `multiline` is another modifier (editor-only, presumably).
- `OWNED: conditions` (not a field): the reference list `conditions` holds child records owned by
  this record. Used by DIALOGUE / DIALOGUE_LINE for `conditions`, `lines` and `effects` (their
  DIALOG_ACTION and DIALOGUE_LINE children).
- `CONDITIONS:` heading followed by `condition "field" if "other" is VALUE` lines: when the editor
  shows a field. Not needed by the engine.
- `TRANSLATE: ALL | NAME | FIELDS ...` lines at the end: which types/fields are translatable.
- Enum names refer to `fcs_enums.def` (`enum Name { A, B=5, ... }`; not parsed yet). The editor
  also has many enums built in (e.g. `DialogConditionEnum`, `DialogActionEnum`) that aren't in the
  .def files; extract them when dialogue is implemented.

## Open questions

- ~~Does the game apply the editor's merge rules?~~ Answered in [Game loader](#game-loader) and
  [Merging](#merging): a modifying record without a base is created, a second "new" definition is
  merged silently, and delete requests (the v17 bookkeeping) are never read.
- ~~Mod load order in the game, including Steam workshop folders~~: see
  [overview.md](overview.md#load-order).
- The game's behaviour on a truncated or corrupt file, and the exact layouts of file types 8–14.
- Save games: probably the same record format with the GAMESTATE_* / *_STATE types; not checked.
- Non-UTF-8 strings in third-party mods: currently rejected. The game doesn't care: it copies string
  bytes as they are (Verified, `FUN_1406c0b50`, `FUN_1406bc480`), so lossless handling means keeping
  the raw bytes.
