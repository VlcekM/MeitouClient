# FCS base/mod data

**Verified** FcsRoundTripTests: four base files read/write byte-identically. FCS GameData/Item
load/save supplies editor format facts; native Kenshi loader is authoritative where behaviour
differs. Little-endian; strings=int32 byte count+bytes, any bytes ([Strings](#strings)). Layouts retained below,
[load order](overview.md).

## Merging

**Verified original loader** table keyed exact case-sensitive StringId, files in load order.
Bit0 only controls missing-base warning; both new/modifying records merge or create. Retain
first definition's type even if later record differs. New takes name; existing renames when bit1,
independent of bit0. Each key merges within its own bool/float/int/vec3/vec4/string/filename table;
same spelling in other kinds remains distinct. Missing fields remain; an empty string/filename overwrites like any value
(**Observed** `FUN_1406c9ce0`: no empty test; its per-key filter is filled only for types 11, 13, 14). REMOVED=true in merged
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

**Verified** (decompile 1.0.68: loader `FUN_1406c0b50`, its table readers `FUN_1406c9320`, `FUN_1406ca320`, `FUN_1406c9990`, `FUN_1406ca670`,
`FUN_1406ca9e0`, `FUN_1406c9ce0`, filter test `FUN_1406c8f50`; entry list `FUN_14086ab20`). The layout of every type the game loads, file
type T = first int, 8..17 (anything else fails, [below](#files-that-fail-to-load)):
- Header: T 17 a uint length, then skipped; T 16 the version int, then four strings skipped by their lengths; T 8..15 none. Then `nextId`
  (the data's counter restarts at 0 per file and ends at least at it, [ids](zones.md#containers-and-ids)) and the record count.
- Record: uint byteSize (ignored), int type, int id, name, string id; uint flags from T 15 (bit 0 MODIFIED only logs a missing base, bit 1
  RENAMED renames an existing record). Below 15 there are no flags: no log, and an existing record is always renamed.
- Tag block, T 11, 13, 14 only: int count × (string key, byte), filling a per-record filter (a repeated key overwrites). A non-empty filter
  keeps a value only when its key has a non-zero byte, and a reference only when `-ref-<target>` has; a removal reference passes regardless.
  A filter is emptied when the file's path contains `.base`. A GLOBAL_CONSTANTS (type 27) record with tags in T 11 or 13 fills no filter and
  queues box "Mod Warning": "The mod named '<path>' is out of date. Re-save it in the construction set.\nModifications to the
  GLOBAL_CONSTANTS will be lost, you'll have to set them again, sorry!", untranslated (`FUN_140742130` → `FUN_1406e7230` sets title and text
  without the translation call the other boxes use), in the same queue as the failure boxes.
- Tables in file order: bool (1 byte, non-zero is true), float, int, vec3 and vec4 (only from T 9), string, filename; each pair overwrites.
- References: int lists × (list name, int n × (target, int v0, from T 11 int v1, v2)); below 11 v1 = v2 = 0. A repeated list name adds to it;
  an empty list touches nothing.
- Instances: int n × (id, target, 3 floats position, 4 floats rotation w x y z, int states × state). From T 15 id and states are strings.
  Below, the id is an int k, giving `<k>-<entry name>`, and a state is an int k: the string id of this file's earlier record with numeric id
  k, else `<k>-<container name>-INGAME`, which a later record of the file with id k then takes as its string id. Both are printed into
  their buffer ([strings](#strings)) with a 512-byte bound, unterminated when that long (the print at `1406c2238` gets the state's int).
- Entry name: `base` for gamedata.base, the mod's name (file stem) for a `.mod`: core entries `FUN_14086ab20`, discovered ones
  `FUN_14086f7a0`, which `FUN_1408693a0` looks up by the `mods.cfg` stem; a translation file (wrapper `FUN_1406c29a0`) has none.
  The container name is the entry name, else the path after its last `/` or `\`.

No installed file has a type below 16 (**Observed**: 553 `.mod`/`.base`/`.translation` files of the install, its Workshop folder and
downloaded mods; types 16 and 17 only). The editor (FCS `GameData.load`) differs: it reads the tag block in T 12 too, v1/v2 from T 10, and
names old instances `<k>-<file name with extension>`; the game is followed. FCS always saves T 17 with a fresh header for a file that had
none (version 1, empty strings, save counter 1, nothing merged, no delete requests; `GameData.save`, `writeHeader`).

Meitou (`FcsGameLoader`, used by `GameDatabase` for data and translation files; `FcsReader` stays the lossless reader of 15..17 for saves,
levels and tools): reads all of the above. A record below 15 gets flags RENAMED, which loads the same. `FcsWriter` writes a file of a type
below 15 as FCS does. **Observed** (scratch comparison of both readers over the 553 files): identical records, except that the game loader
drops the position and states of a removed instance and creates no list from an empty one.

### Files that fail to load

**Verified** (decompile 1.0.68: loader `FUN_1406c0b50`, game-data load `FUN_140870ae0` called from `FUN_14082b370` and
`FUN_140872d70`, which ignore its result; `mods.cfg` reader `FUN_1408693a0`):
- The loader fails only when the file does not open or its first int (the type) is outside 8..17; the latter logs
  `[GameDataContainer::load] Invalid data file '<path>' (Version: <n>)`. Past that check it has no failure exit and never tests the stream,
  so a truncated or damaged file counts as loaded, silently ([damaged files](#damaged-files)).
- `gamedata.base` failing: log `[Mods] Failed to load gamedata.base.`, box "Error!" / "Failed to load the core '{1}' file." with
  `gamedata.base`; it stays first in the file list (its level folder is still read). A core `.mod` failing: log
  `[Mods] Failed to load core mod: <name>`, the same box with `<name>.mod`, left out of the list. Loading goes on either way.
- A mod failing: log `[Mods] Failed to load mod: <name>`, ` (*)` appended for a Workshop mod; left out of the list (no records, no level
  data). After all mods, one box "Mod error(s) (Ignored files)": "Failed to load the following mod(s):" and a line per name. Loading goes on.
- A `mods.cfg` name with no mod on disk is logged (`[Mods] Mod '<name>...`) and skipped before loading; it is not in the box.

Meitou (`GameDatabase.LoadDataFile`, `FcsGameLoader.Opens`, `DataLoadErrors`): a file that does not open or has a type outside 8..17 is left
out (`GameDatabase.NotLoaded`, issue `DataFileNotLoaded`) and has no level layer; a file shorter than 4 bytes too, as
its type is never read (Meitou choice: the original would go on with whatever its variable held). The log lines go to the console log
(`log.txt`) when the world loads; the boxes, "Mod Warning" ones included (issue `OutdatedConstants`), open one after another in load order the
first time the title screen opens.

### Damaged files

**Verified** (MSVCP100 `basic_istream::read`, `ignore`, `seekg` and OgreMain `Quaternion()`, `Vector3()` disassembled; frame layouts of the
loader functions in the decompile):
- A read at the end copies the bytes left into its target and fails the stream; on a failed stream every read, skip and seek does nothing and
  leaves its target as it was. A skip reaching the end marks it, which fails the next read; a seek before the start fails the stream.
- So past the end the loader repeats the values it last read: the loader's own variables, the three name buffers and the shared value buffer
  keep their contents (a value's length variable is set to 0 before each read, so a fully unread value is `""`). Remaining list entries thus
  set the last pair, reference or instance again, and an instance's remaining states add the last state again. A record's rotation starts as
  w 1, x y z 0; its position keeps the last one read. The state length variable holds 2 before the first state is read (1 for a file
  without an entry name): its stack slot keeps a flag stored at the start (`1406c0d5c`), only the register copy is cleared.
- Each table reader keeps its count, key length, key and value in its own frame, never set before their reads. A table read after the stream
  failed (always the bool table of the next record) therefore runs on leftover stack contents, and so does any loader variable or buffer byte
  that no read of this file filled: what follows is not decided by the file.

Meitou (`FcsGameLoader`, `LoaderStream`, `LoaderValue`, `LoaderBuffer`): models the stream, the variables and the buffers as above, applying
each record as it is read (a record counts from its fixed fields on, as the loader merges it from there), and stops at the first use of a
value no read filled, keeping everything applied before (Meitou choice: there is nothing the file decides after it). The value buffer starts
zeroed for each load order and is carried through its files. Issue `DataFileDamaged` says whether it stopped.

Core files→core translation→ordered mods→first mod translation→postpasses→world leveldata.
GLOBAL CONSTANTS lookup by name AND type27. Postpass resolves refs. **Verified** (`FUN_1406c34f0`, `GameDatabase.AddWallMasters`):
each BUILDING with `link type` != 3 sets-or-appends on every loaded record of its `wall subsections` a `wall master`
ref (0,0,0) back to itself; missing subsections are skipped. Translation files are ordinary record layers;
language selection: [localisation](localisation.md). Game skips editor merge/delete bookkeeping; provenance file
indices track creation/last update but their full uses unknown.

## Strings

- **Verified** (`FUN_1406c0b50`, `FUN_1406bc480`): the game copies a string's bytes into its `std::string` as they are, with no
  encoding check, so a mod saved in another encoding (Latin-1, Windows-1252) loads. Record tables and lookups compare those bytes exactly.
- **Verified** (`kenshi_x64.exe`'s byte-string to `MyGUI::UString` imports are the `std::string` and `const char*` constructors and
  `assign(std::string)`): text reaches the UI through MyGUI's UTF-8 reading, which shows a byte that does not start a well-formed sequence
  as the code point of its value ([mygui](mygui.md#text-of-game-strings)). A Latin-1 name therefore shows as Latin-1; Windows-1252 bytes 0x80 to 0x9F become C1 controls.
- **Verified** (frame and global layouts in the decompile of the functions named in [Game loader](#game-loader) and `FUN_1406bc480`): each
  string is copied into a fixed buffer and then taken up to its first NUL. Buffers: 64 bytes per table call for value keys; one 4096-byte
  global for string and filename values; three adjacent 512-byte frame buffers, A (names, tag keys, list names, instance ids), B right after
  it (string ids, reference targets, instance targets) and C after B (states). A length L read as unsigned: L up to the size reads L bytes and
  puts the terminator at L; a larger L (a negative one too) reads size bytes, seeks over the rest (back for a negative L) and cuts at size - 1.
  L equal to the size puts the terminator one past the buffer: A's on B's first byte and B's on C's, so the string runs on into its
  neighbour's contents when taken (a 512-byte name continues with its string id, a list name with each reference's target, an instance id with
  its target, a target with its instance's last state); C's and every key buffer's on the stack cookie, so the /GS check ends the process when
  that function returns, unless the cookie byte was 0; the value buffer's on an unused global byte, keeping the 4096 bytes.
- Meitou (`FcsGameLoader`): the same buffers and rules; for a 64-byte key and a 512-byte state, where the original dies, it keeps the string
  whole, as the original reads it when it lives. **Observed** (the 553 files above): no string reaches a limit or holds a NUL.
- Meitou (`FcsReader`, `FcsWriter`, `LosslessUtf8`): every string is decoded as UTF-8, a byte outside a valid sequence kept as the lone
  surrogate U+DC00 + byte, and encoded back to the same bytes. The mapping is one-to-one, so ordinal comparison is the game's byte comparison;
  `boost::hash` orders (`BoostHashOrder.StringHash`) hash the stored bytes. Text is drawn through `MyGuiText.Runes`, MyGUI's reading.
  **Observed** (scratch program, synthetic strings: Latin-1, lone continuation bytes, overlong, CESU surrogate, 5-byte form, truncated
  sequence): a file read and written again is byte-identical, and `MyGuiText` gives the code points of a byte-level model of the
  disassembled MyGUI routines; `GameDatabase.Apply` keeps names and string values byte-exact. No installed mod has such a string (77 mod
  and translation files scanned).

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
  type 15 is the header-less layout used by the world `.zone`/`.level` files. `FcsReader` supports 15, 16 and 17,
  `FcsGameLoader` 8 to 17. Old mods are rare: since game version 0.92 mods are re-saved as 17 (Observed: of 50 Steam
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
