# FCS base/mod data

**Verified** FcsRoundTripTests: four base files read/write byte-identically. FCS GameData/Item
load/save supplies editor format facts; native Kenshi loader is authoritative where behaviour
differs. Little-endian; strings=int32 UTF-8 byte count+bytes. Strict reader rejects invalid UTF-8;
saves opt into lossless bytes ([save](save.md)). Layouts retained below, [load order](overview.md).

## Merging

**Verified original loader** table keyed exact case-sensitive StringId, files in load order.
Bit0 only controls missing-base warning; both new/modifying records merge or create. Retain
first definition's type even if later record differs. New takes name; existing renames when bit1,
independent of bit0. Each key merges within its own bool/float/int/vec3/vec4/string/filename table;
same spelling in other kinds remains distinct. Missing fields remain. REMOVED=true in merged
bool table removes every index; later same ID creates fresh record, no old fields.

References merge by(list,target), append new at end, replace existing in place. Remove only when
ALL three values=int.MaxValue (editor removes on v2 alone); unresolved targets remain. Instances
merge by ID and overwrite target/position/rotation; append states WITHOUT dedup. Empty target
keeps cleared entry/no states, can later refill (editor differs). FCS duplicate/type/missing-base
errors are not game rejection rules. Never normalize through editor rules to load game mods.

**Verified** GameDatabaseTests base merge54951 records,3841 changed, no unresolved issues; exotic
editor/game differences absent from base data, so base round-trip alone does not test them.
Source native load1406c0b50; reference set/remove1400b61b0/1406cb110, instances1406cae60/1406cafe0,
remove1406bf680, rename1406bfb70. These are facts, not translated binary logic.

## Game loader

Native accepts8..17;17 skips declared header,16 reads version/skips4strings,8..15 headerless.
Keep maximum nextId. byteSize ignored; flags from15,vec3/vec4 from9. Only flag0/1 affect merge.
Silently truncate at NUL and byte limits: keys64, names/IDs/ref/instance identities512,
string/filename4096. Prior types have integer IDs/extra tags and older refs; partial support is
not a complete old-format specification. Error mid-record behaviour unverified.

Core files→core translation→ordered mods→first mod translation→postpasses→world leveldata.
GLOBAL CONSTANTS lookup by name AND type27. Postpass resolves refs. **Verified** (`FUN_1406c34f0`, `GameDatabase.AddWallMasters`):
each BUILDING with `link type` != 3 sets-or-appends on every loaded record of its `wall subsections` a `wall master`
ref (0,0,0) back to itself; missing subsections are skipped. Translation files are ordinary record layers;
language selection: [localisation](localisation.md). Game skips editor merge/delete bookkeeping; provenance file
indices track creation/last update but their full uses unknown.

## Record types and schema

`src/Meitou.Data/Fcs/FcsRecordType.cs` is the authoritative113-value editor itemType mapping.
**Verified** FcsSchemaTests.Record_type_names_match_fcs_def_against_base_game checks78 used types;
record census/runner-up matching statistics are not additional format rules. fcs.def declares
sections/field defaults/types/ranges/enums/category headings; undeclared saved keys may still be
valid. Infer kind from schema declaration, not an item's display name. Schema details follow.

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
  .def files; the dialogue ones are in [dialogue](../game/dialogue.md#enums).

## Item categories

What tells item records apart, for lists of every item (the debug menu's Drop item picker, `DebugItemCatalog`). The record type is the first level;
the second is one int field per type (names from the editor's built-in enums, **Verified** by decompiling `forgotten construction set.exe`: they are not in
`fcs_enums.def`; the item counts are **Observed** on the vanilla install, 568 named item records):

| Type | Field | Values |
| --- | --- | --- |
| WEAPON | `skill category` (`WeaponCategory`) | 0 Katanas, 1 Sabres, 2 Blunt, 3 Heavy, 4 Hackers, 5 Unarmed, 6 Bow, 7 Turret, 8 Polearms, 9-20 creature attacks, 21 none |
| ARMOUR | `slot` (`AttachSlot`, `fcs_enums.def`; default 5 Body) | 0 Weapon, 1 Back, 2 Hair, 3 Hat, 4 Eyes, 5 Body, 6 Legs, 7 None, 8 Shirt, 9 Boots, 10 Gloves, 11 Neck, 12 Backpack, 13 Beard, 14 Belt |
| ITEM | `item function` (`ItemFunction`) | 0 none, 1 first aid, 2 med rigging (splints), 3 food, 4 container, 5 weapon, 6 clothing, 7 unused, 8 narcotic, 9 tool, 10 anything, 11 blueprint, 12 robot repair, 13 book, 14 money, 15 restricted food (raw meat), 16 ammo, 17 severed limb |
| LIMB_REPLACEMENT | `slot` (`LimbSlot`) | 50 left arm, 51 right arm, 52 left leg, 53 right leg |

CONTAINER, CROSSBOW, BLUEPRINT and MAP_ITEM have no such field. Quality (`DebugGiveItem.QualityTypes`): armour, crossbows and limbs are graded 5 / 20 /
40 / 60 / 80 / 95 (Prototype to Masterwork, `ArmourTooltip.GradeNames`); a weapon is graded by a MATERIAL_SPECS_WEAPON model that a WEAPON_MANUFACTURER lists in `weapon models` (value 0 = level; **Observed** on the vanilla install with Rebirth: the player's `PLAYER_WEAPONS` maker lists levels 5 to 80, and the Meitou grade, level 100, is only in a Rebirth maker's list).

## Open questions

- The game's behaviour on a truncated or corrupt file, and the exact layouts of file types 8–14.
- Save games: the same record format, file type 15, with GAMESTATE_* / *_STATE types; see [save.md](save.md).
- Non-UTF-8 strings in third-party mods: currently rejected. The game doesn't care: it copies string
  bytes as they are (Verified, `FUN_1406c0b50`, `FUN_1406bc480`), so lossless handling means keeping
  the raw bytes.
