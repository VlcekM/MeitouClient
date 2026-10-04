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
other encodings; `meitou-tools fcs <file>` reports invalid UTF-8.

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

## Record types (mostly Unknown)

The numeric `type` to name mapping isn't in `fcs.def` / `fcs_enums.def` (probably hardcoded in the
exe). `fcs.def` has 114 `[SECTION]` schemas named after the types (`AI_PACKAGE`, `AI_TASK`,
`DIALOGUE`, ...).

Every type number used in the four base files, with the total record count and the first record's
name (from `meitou-tools fcs`). The **guess** column comes only from names and needs confirming
against the field names in `fcs.def` (and against OpenConstructionSet, an existing C# FCS library).
Only `19` = `DIALOGUE_LINE` is Verified (record names are `DIALOGUE_LINE<n>`); `31` = `DIALOG_ACTION`
is very likely for the same reason.

| Type | Records | Example name | Guess |
| ---: | ---: | --- | --- |
| 0 | 1148 | sign2- -clothes | BUILDING |
| 1 | 988 | Mercenary heavy | CHARACTER |
| 2 | 68 | Katana | WEAPON |
| 3 | 257 | Mask 2 | ARMOUR |
| 4 | 416 | Steel bars | ITEM |
| 5 | 68 | bull walk | ANIMAL_ANIMATION |
| 6 | 103 | Haircut10-oldman | ATTACHMENT / hair |
| 7 | 99 | Garru | RACE |
| 10 | 161 | Medics Guild | FACTION |
| 13 | 436 | Bark | town / location? |
| 16 | 11 | Head | body part? |
| 17 | 60 | Cut left static | ANIMATION |
| 18 | 2203 | Law enforcement defeats squad (arrest them) | DIALOGUE |
| 19 | 23889 | DIALOGUE_LINE6071 | DIALOGUE_LINE (Verified) |
| 21 | 275 | Heavy Building Foundations | RESEARCH |
| 22 | 143 | get out of cage - escape | AI_TASK? |
| 24 | 197 | idle_stand_relax | ANIMATION_EVENT? |
| 25 | 62 | hire medic | STATS / service? |
| 26 | 34 | bandit types | PERSONALITY? |
| 27 | 3 | GLOBAL CONSTANTS | CONSTANTS |
| 28 | 137 | desert | BIOMES |
| 29 | 1464 | basic wall gate A | BUILDING_PART |
| 31 | 21225 | DIALOG_ACTION4205 | DIALOG_ACTION |
| 43 | 4 | hatches | |
| 44 | 259 | signs3_material | MATERIAL_SPEC |
| 45 | 11 | base buildings tiled | |
| 46 | 28 | Small Backpack | CONTAINER |
| 47 | 170 | mask3 | MATERIAL_SPECS_CLOTHING |
| 49 | 187 | weapon vendor Outposts | VENDOR_LIST |
| 50 | 49 | Edge Type 1 | MATERIAL_SPECS_WEAPON |
| 51 | 18 | Truth Two | WEAPON_MANUFACTURER |
| 52 | 1166 | Cannibal home guards | SQUAD_TEMPLATE |
| 53 | 3 | Northern desert | ROAD? |
| 55 | 70 | super black | COLOR_DATA |
| 56 | 1 | grass1 | |
| 59 | 330 | Grass Spikey | GRASS |
| 60 | 945 | FOLIAGE_DUNE-Bouldersmall21 | FOLIAGE_MESH |
| 61 | 42 | Basic Grass | |
| 62 | 136 | armour chain | |
| 63 | 3 | bar | |
| 64 | 23 | Rock Bottom | |
| 68 | 8 | BowlBirdsTEST | |
| 69 | 329 | UpthrustRocks01 | |
| 70 | 5 | Dust bandits | |
| 71 | 11 | raid filler | |
| 72 | 275 | Diplomat Mission (running) | AI_PACKAGE |
| 73 | 385 | Npc Basic TOUGH | STATS? |
| 74 | 11 | Turret double | |
| 76 | 62 | Beak Thing | ANIMAL_CHARACTER |
| 77 | 2 | UNIQUE bandit test | |
| 78 | 4 | Ninja TEMPLATE | |
| 80 | 77 | WEATHER | WEATHER |
| 81 | 59 | SEASON | SEASON |
| 82 | 126 | weather_volcano_smoke1 | EFFECT |
| 83 | 30 | Food Ingridients | |
| 84 | 317 | DANG | WORD_SWAPS? |
| 86 | 20 | Beak Thing Egg | |
| 87 | 4 | Packbeast lantern | |
| 88 | 76 | white light | LIGHT |
| 89 | 41 | HumanMale03 | HEAD |
| 92 | 2 | Iron Rock | |
| 93 | 295 | BASE- standard cannibal raid | |
| 95 | 92 | The Desert | |
| 96 | 10 | EFFECT_FOG_VOLUME | EFFECT_FOG_VOLUME |
| 97 | 39 | Wheat Farm Type | FARM_DATA |
| 98 | 18 | Crop_Base_part | FARM_PART |
| 99 | 26 | None | |
| 100 | 11 | Fishman | |
| 101 | 1 | ARTIFACTS | |
| 102 | 18 | Map of the Border Zone | MAP_ITEM |
| 103 | 40 | no carpets | |
| 104 | 12 | swamper | |
| 105 | 18 | Attack | |
| 107 | 11 | Ranger | |
| 109 | 27 | Auto Mine | |
| 110 | 155 | Player killed Phoenix | |
| 111 | 48 | Human Left Arm Stump | LIMB_REPLACEMENT? |
| 112 | 1 | base animations | |

## Schema: `fcs.def` (Observed)

Text, one section per record type:

```
[AI_PACKAGE]
signal func:     BlackboardSignalFunctions.SIGNAL_NONE "description..."
Leader AI Goals: AI_TASK (0, 24,0) "...val0 and val1 is start and finish time..."
```

- `[A,B]` headers: a section shared by several types (e.g. `[ANIMAL_ANIMATION,ANIMATION]`).
- `key: default "description"`: the default's form gives the property list (`False` bool, `1.0`
  float, integer int, `""` string, `EnumName.VALUE` stored as int).
- `key: TYPE (a, b, c) "desc"`: a reference list to records of `TYPE`, with default v0..v2.
- Enum names refer to `fcs_enums.def` (`enum Name { A, B=5, ... }`).

## Open questions

- The record-level `unknown` int (non-zero only in rebirth.mod).
- `flags` semantics; how deleted records, fields and references are encoded.
- Merge rules when several files touch one `stringId` (per field? reference lists replaced or appended?).
- Format-17 header tail bytes; the `0x4C67BE` constant.
- Whether save games use this record format.
