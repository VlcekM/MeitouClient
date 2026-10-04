# FCS game data: `.base` / `.mod`

**Verified 2026-10-04**: a probe parser following exactly this layout reads all four base-game files
(`gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`) and stops exactly at the end of
each file. The *meaning* of fields marked Unknown is still a guess.

All values little-endian. `string` = `int32 length` + `length` bytes (encoding **Unknown**; assume
Windows-1252 until a non-ASCII sample says otherwise).

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

- **Verified**: `19` = `DIALOGUE_LINE` (record names are `DIALOGUE_LINE<n>`).
- Most common types: gamedata.base `19, 31, 29, 0, 18, 60, 84, 44, 1, 52`; Dialogue.mod `19, 31, 18`.
- To do: derive the mapping from sample records and their field names; cross-check with
  OpenConstructionSet (an existing C# FCS library).

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
- String encoding.
- Whether save games use this record format.
