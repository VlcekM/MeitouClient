# FCS game data: `.base` / `.mod`

**Verified 2026-10-04**: `Meitou.Data.Fcs.FcsReader` follows exactly this layout, and `FcsWriter`
writes all four base-game files (`gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`)
back **byte-for-byte identical** (test `FcsRoundTripTests.Base_game_file_round_trips_byte_for_byte`).
The *meaning* of fields marked Unknown is still a guess.

Also Verified by that round-trip on the base files:
- Bool values are always the byte 0 or 1.
- No record repeats a property key within one list, or a reference category.
- Dependency / reference lists are plain comma-joined names (no empty entries or spaces).

All values little-endian. `string` = `int32 length` + `length` bytes, **UTF-8** (Verified on base
files: 92 non-ASCII strings in Dialogue.mod and rebirth.mod, all valid UTF-8, round-trip exact;
gamedata.base and Newwworld.mod are pure ASCII). Mods written by older tools could still contain
other encodings: the reader currently **rejects** invalid UTF-8 with `FcsFormatException` rather than
corrupting it. If real mods turn up with Windows-1252 text we need a lossless fallback.

## File

```
int32   fileType            16 = old format, 17 = new format
  [17 only] int32 headerLength   bytes from here to the end of the header
int32   version             1 in all base files
string  author              empty in base files
string  description         empty in base files
string  dependencies        comma-separated, e.g. "gamedata.base,Newwworld.mod"
string  references          comma-separated, e.g. "rebirth.mod"
  [17 only] byte[] tail     Unknown. Dialogue.mod: 01 00000000 00000000 (9 bytes),
                            rebirth.mod: 09 00 00000000 00000000 (10 bytes). Skip via headerLength.
int32   unknown             0x004C67BE in all four files (editor version? id counter?)
int32   recordCount
Record  records[recordCount]
```

## Record

```
int32   unknown             0 in gamedata.base, Newwworld.mod, Dialogue.mod;
                            varies (90..5272) in rebirth.mod
int32   type                record kind (see "Record types")
int32   id                  0 in samples seen
string  name                display name, e.g. "Mercenary heavy"
string  stringId            unique key "<number>-<file that created it>", e.g. "1292-gamedata.base"
uint32  flags               see "Flags"

7 property lists, each: int32 count, then count × (string key, value):
  bool       1 byte
  float      float32
  int        int32
  vec3       3 × float32
  vec4       4 × float32
  string     string
  filename   string

int32   categoryCount       reference lists
  string  category          e.g. "conditions", "dialogue package"
  int32   count
    string  targetStringId
    int32   v0, v1, v2      the "(0, 24, 0)" values in fcs.def, e.g. AI_TASK start hour, end hour, weight

int32   instanceCount       placements of other records (e.g. inside buildings, towns)
  string  id
  string  target            stringId of the placed record (Observed)
  float32 position[3]
  float32 rotation[4]       quaternion, component order Unknown
  int32   stateCount
    string state
```

Totals from the probe (references / instances): gamedata.base 12,559 / 656; Newwworld.mod
4,337 / 827; Dialogue.mod 49,208 / 0; rebirth.mod 25,082 / 416.

## Flags (Observed)

| File | Values (count) |
| --- | --- |
| gamedata.base | `0x80000002` ×9399 |
| Newwworld.mod | `0x80000002` ×1944, `0x80000001` ×548, `0x80000003` ×19 |
| Dialogue.mod | `0x10` ×37990, `0x11` ×989, `0x13` ×98 |
| rebirth.mod | `0x10` ×5806, `0x11` ×2460, `0x13` ×256, `0x90` ×14, `0x60` ×10, `0x41` ×9, `0x43` ×3, `0x81` ×3 |

Hypothesis (**Unknown**): low bits = new / changed / deleted relative to the dependencies (needed
for override merging); `0x80000000` in format 16 vs `0x10` in format 17 is a format marker. Confirm
by making tiny mods in FCS that add, edit and delete a record, and diffing the output.

## Overrides (Observed)

A file can contain a record whose `stringId` belongs to an earlier file, holding **only the fields
it changes**. Example: `14520-rebirth.mod` (FOLIAGE_MESH "Iron Rock") is a full record (flags
`0x80000002`, 18 fields, a `building` reference) in `gamedata.base`, and appears again in
the later-loading `rebirth.mod` with flags `0x11` and just 7 fields (`clustered`, `cluster radius max/min`,
`max/min slope`, `cluster num max/min`) and no references. So `0x11` looks like "changed record,
partial fields", merged field by field. (Note the base file holds a record whose id says it was
created in rebirth.mod: ids keep their origin even after records are baked into gamedata.base.)
How a delta removes a field or a reference is still Unknown.

A bool property named `REMOVED` appears on records of 28 types (`meitou-tools fcs-types` lists them),
probably marking a deleted record. **Unknown** whether it's ever false, and how it relates to `flags`.

## Record types (Verified against fcs.def)

The number-to-name mapping isn't written down anywhere in the data (probably hardcoded in the exe),
so it was derived: for each type number, collect every field key its records use in the four base
files, and find the `fcs.def` section declaring the most of them (`meitou-tools fcs-types`).
`Meitou.Data.Fcs.FcsRecordType` holds the result, and the test
`FcsSchemaTests.Record_type_names_match_fcs_def_against_base_game` checks that each enum name is still
(one of) the best-matching sections. Field kinds agree too: every field found in the schema is
stored in the list its fcs.def default implies (bool → bool, int / enum / colour → int, float →
float, string → string, filename / texture → filename, reference → reference; no exceptions).

"Fields" = distinct keys used by records of that type; "found" = how many the chosen section declares.

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
| 31 | DIALOG_ACTION | 21225 | — | — | no fcs.def section; named by its records (`DIALOG_ACTION<n>`) and by `effects: DIALOG_ACTION` in DIALOGUE_LINE |
| 43 | REPEATABLE_BUILDING_PART_SLOT | 4 | 1/1 | BUILDING_PART 1 | **tentative**, one field |
| 44 | MATERIAL_SPEC | 259 | 12/13 | FOLIAGE_MESH 8 | |
| 45 | MATERIAL_SPECS_COLLECTION | 11 | 1/1 | CHARACTER_PHYSICS_ATTACHMENT 1 | **tentative**, one field |
| 46 | CONTAINER | 28 | 35/36 | ARMOUR 26 | |
| 47 | MATERIAL_SPECS_CLOTHING | 170 | 7/8 | MATERIAL_SPEC 4 | |
| 49 | VENDOR_LIST | 187 | 15/16 | ITEM_PLACEMENT_GROUP 8 | |
| 50 | MATERIAL_SPECS_WEAPON | 49 | 12/12 | WEAPON 7 | |
| 51 | WEAPON_MANUFACTURER | 18 | 8/8 | FACTION_TEMPLATE 1 | |
| 52 | SQUAD_TEMPLATE | 1166 | 55/59 | UNIQUE_SQUAD_TEMPLATE 39 | |
| 53 | ? | 3 | — | — | no section; fields `imagefile`, `color channel`, `spawns`, `min/max altitude`, `population amount`; names "Northern desert", "UC West" — a spawn region painted on a map image |
| 55 | COLOR_DATA | 70 | 2/2 | — | |
| 56 | ? | 1 | — | — | no section; "grass1" with `grass`, `Mesh`, `Collision`, `lod range` — looks like a legacy grass type |
| 59 | FOLIAGE_LAYER | 330 | 4/8 | FOLIAGE_MESH 1 | **tentative**; `lod range`, `lod levels`, `page size`, `uses foliage system` not in fcs.def |
| 60 | FOLIAGE_MESH | 945 | 42/43 | MAP_FEATURES 10 | |
| 61 | GRASS | 42 | 19/19 | FOLIAGE_MESH 6 | |
| 62 | BUILDING_FUNCTIONALITY | 136 | 29/31 | RESEARCH 1 | |
| 63 | ? | 3 | — | — | no section; `layout interior`, `layout exterior`, `building` ref; names "bar", "police", "civilian default" |
| 64 | NEW_GAME_STARTOFF | 23 | 12/13 | RESEARCH 2 | |
| 68 | WILDLIFE_BIRDS | 8 | 15/16 | MAP_FEATURES 2 | |
| 69 | MAP_FEATURES | 329 | 15/15 | FOLIAGE_MESH 10 | |
| 70 | DIPLOMATIC_ASSAULTS | 5 | 1/1 | — | **tentative**, one field |
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
| 92 | ? | 2 | — | — | FOLIAGE_MESH fields plus a `building` reference; "Iron Rock". Probably a foliage-placed building; no section |
| 93 | FACTION_CAMPAIGN | 295 | 33/35 | WORD_SWAPS 2 | |
| 95 | BIOME_GROUP | 92 | 17/18 | BIOMES 1 | |
| 96 | EFFECT_FOG_VOLUME | 10 | 13/13 | LIGHT 2 | |
| 97 | FARM_DATA | 39 | 23/23 | ENVIRONMENT_RESOURCES 3 | |
| 98 | FARM_PART | 18 | 8/8 | MAP_ITEM 2 | |
| 99 | ENVIRONMENT_RESOURCES | 26 | 19/19 | FARM_DATA 3 | |
| 100 | RACE_GROUP | 11 | 2/3 | NEST_ITEM 2 | **tentative**; tie on fields, name "Fishman" fits |
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

- Numbers 8, 9, 11, 12, 14, 15, 20, 23, 30, 32–42, 48, 54, 57, 58, 65–67, 75, 79, 85, 90, 91, 94,
  106 and 108 aren't used by the base game. Their names are Unknown (mods may use some).
- `AI_SCHEDULE` is the only fcs.def section with no records in the base game, so its number is Unknown.
- `DAY_SCHEDULE` and `NULL_ITEM` are referenced in fcs.def but have no section of their own.
- Fields stored but not declared in fcs.def (e.g. BUILDING `sound x`, CHARACTER `is military`) are
  real data the engine reads; the editor just doesn't show them. The reader keeps them.

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
  | `#FFFFFF` | colour | int list |
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
- Enum names refer to `fcs_enums.def` (`enum Name { A, B=5, ... }`; not parsed yet).

## Open questions

- The record-level `unknown` int (non-zero only in rebirth.mod).
- `flags` semantics; how deleted records, fields and references are encoded (`REMOVED` bool?).
- Merge rules when several files touch one `stringId` (fields merge, per the Iron Rock example; are
  reference lists replaced or appended? how is a reference removed?).
- Names of types 53, 56, 63, 92, `AI_SCHEDULE`'s number, and the 4 tentative matches.
- Format-17 header tail bytes; the `0x4C67BE` constant.
- Whether save games use this record format.
- Non-UTF-8 strings in third-party mods: currently rejected; decide on lossless handling once samples exist.
