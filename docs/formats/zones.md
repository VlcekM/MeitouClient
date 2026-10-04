# Zones and level files: `.zone`, `.level`, `features.dat`

Examined 2026-10-04 on the Steam install. Readers: `Meitou.Data.World.LevelFile` (one file),
`WorldLevelData` (all layers merged, placements), `MapFeatureFile` (`features.dat`), `WorldObjectLayout`
(placements to meshes). Checks:
`meitou-tools world` and `tests/Meitou.Tests/World/LevelFileTests.cs`, `MapFeatureFileTests.cs`. World
size and the zone grid are in [terrain.md](terrain.md#world-coordinates).

## Summary

World state is stored as **FCS game data** ([fcs-mod.md](fcs-mod.md)): the same record layout as `.mod`
files, holding records of the GAMESTATE_* types (the state of a placed building, town, inventory) plus
INSTANCE_COLLECTION / ROAD records whose *instances* are the placements. A zone file holds the buildings of
one 4608 × 4608 cell of the zone grid; `leveldata.level` holds towns and roads.

## Files

715 files (701 `.zone`, 14 `.level`). **Verified**: all parse to their exact end, and all 709 non-legacy
files are rewritten byte for byte (`LevelFileTests.Every_base_game_level_file_parses_to_its_end_and_round_trips`).

| Folder (under `data/`) | Files | FCS type | Contents |
| --- | ---: | --- | --- |
| `newland/leveldata.level` | 1 | 15 | One empty GAMESTATE_TOWN_INSTANCE_LIST |
| `newland/leveldata/` | 2 | 15 | `zone.31.45.zone`, `interiors.level` |
| `newland/leveldata/Newwworld/` | 334 | 15 | 330 zones, `leveldata.level`, `interiors.level`, `interiors_old.level`, `leveldata - Copy.level`, `leveldata - Copy (2).level` |
| `newland/leveldata/Dialogue/` | 30 | 15 | 28 zones, `leveldata.level`, `interiors.level` |
| `newland/leveldata/rebirth/` | 316 | 15, 16 | 314 zones (29 of them type 16), `leveldata.level`, `interiors.level` |
| `newland/leveldata/platoon/` | 0 | | empty (the exe names this folder) |
| `leveldata/` | 28 | 10, 13, 15 | Legacy: an older world (zones 8..19, see below) |
| `leveldata/shader_ball.mod/` | 2 | 15 | Test scene |
| `leveldata.level`, `roads.level` | 2 | 15, 13 | Legacy: a 20-entry town list; an empty file |

The sub-folders of `newland/leveldata/` are named after the base game-data files (`Newwworld.mod`,
`Dialogue.mod`, `rebirth.mod`); `gamedata.base` has none, the folder itself acts as the base. The
`- Copy` and `_old` files are leftovers (Observed: not names the exe uses; `WorldLevelData` ignores them).

## File layout

**FCS type 15** (680 files) is type 16 without the header: `int32 type (15)`, `int32 nextId`,
`int32 recordCount`, records. **Verified** from the editor (FCS `GameData.load` treats types 1–15 as
header-less, reads the next id and the record count) and by the round-trip. Type 16 zone files (29, all in
`rebirth/`) have the normal type 16 header with version 1 and empty author, description, dependencies and
references. Records use the type 16 record layout ([fcs-mod.md](fcs-mod.md#record)), flags are 0 except
918 records in `rebirth/` with `0x80000002`.

Two differences from `.mod` files (**Verified** by the round-trip):

- **Record `byteSize`** is often wrong: in 25 files some records store a non-zero size that is not their
  real size (e.g. 282 for a 54,535-byte record). `FcsWriter.Write(..., keepByteSizes: true)` writes the
  stored value so these files stay byte-exact. What the value means is **Unknown** (stale or uninitialized).
- **Trailer**: after the records, most files (657 of 715) have `int32 count` + `count` × `int32`.
  Observed: always ascending positive ints, often exactly 1..n, and frequently larger than the file's
  `nextId`; they are not the numeric parts of the file's own record or instance ids (match in 3 of 648
  files). Meaning **Unknown**; `LevelFile.Trailer` keeps it raw. Files without one: all 29 type 16 files,
  the 5 `interiors.level`, 19 type 15 zones, the two type 15 town lists `newland/leveldata.level` and `data/leveldata.level`, and the 3 legacy files of types 10 and 13 without one. The reader requires the trailer
  to end the file exactly.

**Legacy types 10 and 13** (6 files, only under `data/leveldata/` and `data/roads.level`): the old record
layout, implemented in `LevelFile` from the editor's rules (FCS `Item.load` for file versions < 15):
no record flags; from version 11 a per-record list of `(string key, bool)` field tags instead; instance
ids and state ids are `int32` instead of strings (the editor turns them into `"<n>-<file>"` and
`"<n>-<file>-INGAME"`; we keep the decimal number); references have three ints from version 10. The
format 13 trailer has 8-byte entries (`int32` id + a 32-bit value that looks random). **Verified**: all 6
parse to their end; not writable.

## Ids

- Records created in the game's world editor have string ids `"<n>-<source>-INGAME"`, where `<source>`
  is a game-data file name without extension (`Newwworld`, `rebirth`, `Dialogue`, `base`, ...) or a
  working name (`zone24.22`, `__Working Level`, `Boneyard Wolf`, `RoadWorks`, ...). Observed.
- A state record's id is its placement's instance id + `-S` + **the state record's type in hex**: `-S23`
  for GAMESTATE_BUILDING (35), `-S5e` for GAMESTATE_TOWN (94). **Verified** on all 31,849 records whose id
  ends in `-S<hex>`.
- Placement targets are ordinary FCS ids from the game data (`"1076-gamedata.base"`, ...).

## Layers and merging

`WorldLevelData.Load` applies, for each file name, the base folder first, then each sub-folder in load
order (`Newwworld`, `Dialogue`, `rebirth`), with the FCS merge rules of `GameDatabase` (fields per key,
instances per id, an empty target removes an instance). Observed: 4,047 of 69,883 records reappear in a
later layer; 3,053 of those are plain definitions (flags 0) of an id an earlier layer already defined, so
the editor's "new vs. modified" flag is not used to mark overrides here. The game reads the core files' `newland/leveldata/[<core file>/]leveldata.level` with its ordinary record loader, which accepts file types 8–17 (Observed by the lead in `kenshi_x64.exe`, mod-loading analysis; see [fcs-mod.md](fcs-mod.md)). That it merges zone layers with exactly these rules, and how mods add level data, is **Unknown**.

Result for the base game: 500 zones have data (of 4,096), X 2..62, Y 0..62.

## Zone files: `zone.X.Y.zone`

X and Y are the zone grid indices ([terrain.md](terrain.md#world-coordinates)): X along world +X, Y along
world +Z.

- **`0-buildinglist`** (INSTANCE_COLLECTION): one instance per placed object.
  - `id`: instance id; `target`: a BUILDING record of the game data; position, rotation (w first);
    `states`: one id, the object's GAMESTATE_BUILDING record in the same file.
  - Position X and Z are absolute world coordinates. **Verified**: all 11,714 placed buildings lie in the
    zone of their file.
  - Position **Y is the height above the terrain**, not absolute. Observed: the state's `world Y pos` minus
    (terrain height + instance Y) has median 0.02 and quartiles −0.26 / +0.72 over 11,693 buildings, while
    `world Y pos` minus terrain alone has median 7.2 and a long tail (buildings on stilts, walls on
    slopes, stacked parts). Tested in `LevelFileTests.Base_game_heights_match_the_heightmap`.
  - Besides buildings, 1,335 entries sit at (0, 0, 0) and target an INVENTORY_STATE record of the same file
    (**Verified**). Purpose Unknown (container inventories?). `WorldLevelData.Buildings()` leaves them out;
    `BuildingListEntries()` has all.
  - Targets: all 11,714 resolve to BUILDING records except 12 placements of 3 ids
    (`97576`–`97578-Newwworld.mod`) that no base file defines (**Verified**).
- **GAMESTATE_BUILDING** (14,636 in zone files): the building's state. Fields seen (Observed): bools
  `is complete`, `is public day/night`, `is inside building`, `destroyed`, `foliage`, `broken`,
  `power on`, `gets battery`; floats `world Y pos` (absolute height), `construction progress`,
  `0construction mats`, `player furniture Y offset`, `batterycharge`; ints `version` (270), `floornum`,
  `flags`, and five-part object handles `handle*`, `town*`, `residents*`, `isInsideBuilding*`, `mounted*`
  with suffixes `TYPE`, `C`, `CS`, `I`, `S` (TYPE is a record type number, e.g. `townTYPE` 13 = TOWN);
  strings `name`, `owner faction ID` (a FACTION id; all resolve except `58697-rebirth.mod`, 28 times),
  `squad template`, `residents template`.
- INVENTORY_STATE / INVENTORY_ITEM_STATE records: inventories; not analyzed.

## `leveldata.level`

- **Town list** (GAMESTATE_TOWN_INSTANCE_LIST, id `0-townlist`): one instance per town. `target` is a TOWN
  record (all 304 resolve, **Verified**), position absolute (Y included: median 2 units from the
  terrain), rotation unused (0, 0, 0, 0), `states` one GAMESTATE_TOWN id ending `-S5e`.
- **GAMESTATE_TOWN** (1,296 over the layers): ints `zones_count`, `zone_x_<i>`, `zone_y_<i>` (the zones the
  town covers), handle `hand*`; string `instance` = the town-list instance id. **Verified**: for each of
  the 224 towns whose state lists zones, the town's position lies in one of them; 80 list none.
- **ROAD** (2,109 after merging): bool `hidden`, floats `width` (46..200), `length`, `weight`, ints
  `start`, `end` (node numbers, Unknown meaning). Instances are the road's points in order (ids `0`, `1`,
  ...; target `-`): position absolute (**Verified** on the terrain: median Y − terrain −0.29 over 20,944
  points); the rotation field is reused: w = 1, y = z = 0 and x runs 0..1 (Observed; probably the point's
  position along the road).
- **`5-roadfoliage`** (INSTANCE_COLLECTION): 2,666 points (target `-`), rotation x, y, z = 0 and w a size
  of 0.1..3590 (Observed; probably a clearing radius for foliage along roads).

## `interiors.level`

Per layer, the furnished insides of buildings: GAMESTATE_BUILDING (28,857), ITEM_PLACEMENT_GROUP (8,692),
INVENTORY_ITEM_STATE, INVENTORY_STATE, INSTANCE_COLLECTION (490), many with ids `"<n>-[basedata]-INGAME"`.
`WorldLevelData.Load(..., includeInteriors: true)` merges them. After merging there are 410 INSTANCE_COLLECTION
records: **building layouts**, described under ["Building layouts"](#building-layouts) below. ITEM_PLACEMENT_GROUP and the
inventory records were not analyzed.

## Other placement files

### `data/newland/land/features.dat` (`MapFeatureFile`)

**Verified** layout (parses to the exact end; blocks are contiguous):

```
char[4] magic        "MF01"
int32   zoneCount    4096 (64 × 64)
{uint32 offset, uint32 size}[zoneCount]   offset/size of each zone's block; 0/0 = no features
block:  int32 count, count ×
          uint8  length, char[length] stringId   a MAP_FEATURES record
          float32 x, y, z                        absolute world position
          float32 sx, sy, sz                     scale
          float32 qw, qx, qy, qz                 rotation, w first
```

Slot `i` is zone (`i % 64`, `i / 64`): 1,816 of the 1,821 features lie in their slot's zone (**Verified**;
5 lie just outside). The rotation order is **Verified** by shape: read w first, 1,125 are pure yaw
(x = z = 0) and all but one are unit length; read x first, none are. 1,810 targets are MAP_FEATURES
records, 11 (7 ids) are not in the base game data. Y is absolute and mostly below the terrain (median
37 units under it: large features are sunk in).

### `data/newland/land/fogfeatures.dat` (Observed, not decoded)

Magic `FF02`, `int32` 28 (entry count), then entries starting with a `uint8`-length name such as
`Swamp_N-W-Mid  (-46488, 37343)` (names carry world X/Z), followed by floats (colours, distances,
positions). Entry layout **Unknown**.

### `data/globalPathing.path` (Observed, not decoded)

1,225,412 bytes: `int32` 3, `int32` 2587, `int32` 2587, `int32` 1, then repeating records containing
rectangle corners in world units (e.g. −147456, −129024), counts and float costs. Probably the coarse
global pathfinding graph. **Unknown**.

## Legacy world: `data/leveldata/` (Observed)

28 zone files with indices 8..19 (formats 10, 13, 15), plus `data/leveldata.level` (a town list) and
`data/roads.level`. Same record types (GAMESTATE_BUILDING, INSTANCE_COLLECTION, ITEM_PLACEMENT_GROUP,
INVENTORY_STATE). Presumably the pre-Newland world that goes with `data/land/`; `WorldLevelData` does not
read it. Whether the game still reads it is **Unknown** (the exe contains the string `./data/leveldata/`).

## From placements to meshes

How the game builds a placed building, and how `Meitou.Data.World.WorldObjectLayout` (with `BuildingRandom`,
`BuildingTowns`) reproduces it for `meitou-viewer --world` ([../viewer.md](../viewer.md#world-mode)). Counts are
over the 11,714 base-game building placements (252 distinct BUILDING records) and 1,810 resolvable
`features.dat` entries. Sources: decompilation of `kenshi_x64.exe` (Steam build of 2026-10, Ghidra 12.1.4) with
the labels of [runtime-materials.md](runtime-materials.md): **Verified (decompiled)** = read in the function
named; functions are named by entry address. The game's own name for the builder is `Building::createPhysical`
(FUN_1405609e0, from its log messages).

### Choosing the parts

- **Not stored.** The parts are chosen again every time a building is created; nothing about the choice is
  saved in the GAMESTATE_BUILDING or the zone instance (**Verified (decompiled)**: FUN_1405609e0 reseeds the
  generator and rolls; the state fields listed above hold no part ids. The `exterior layout name` / `interior
  layout name` strings, e.g. `Robotics Sign` / `Robotics Shop`, name building layouts of `interiors.level`, not
  part choices: see ["Building layouts"](#building-layouts)).
- **Generator**: the C runtime's `rand()` of MSVCR100.DLL (imported; a linear congruential generator,
  `x = x × 214013 + 2531011`, result `(x >> 16) & 0x7FFF`), so the result is reproducible (**Verified
  (decompiled)** that it is the import; the sequence itself is the documented MSVC one, checked in
  `WorldObjectLayoutTests.Random_is_the_msvc_rand_sequence`). Two helpers scale it (**Verified (decompiled)**,
  FUN_1409b3b40 / FUN_1409b1c80): int in [min, max] = min + trunc(rand / 32768 × (max − min + 1)) in single
  precision, clamped; float = rand / 32768 × (max − min) + min.
- **Seed** (FUN_14056d030, **Verified (decompiled)**): the building's world X and Z, each truncated toward zero to
  a 64-bit integer, XOR `0xDEADBEEF`, low 32 bits taken: a (from X), b (from Z); seed =
  `((a >> 2) + 0x9E3779B9 + (a << 6) + b) XOR a` (boost's `hash_combine`). `srand(seed)` runs right before the
  exterior parts are created and again before the `interior` parts. So the same building at the same spot always
  looks the same, and two buildings at one spot (a door and its house) share the seed.
- **Choice per `parts` list** (FUN_140559b80, **Verified (decompiled)**; fcs.def describes it loosely). Reference
  val0 is the group, val1 the chance. Walking the list in order:
  - group 0: chance 0 → always kept, no roll; otherwise one int roll 0..99 and the entry is kept if the roll is
    below the chance. The roll is made even for chance ≥ 100 and for targets that do not exist.
  - other groups: entries with chance > 0 and an existing target are collected per group in a `std::map<float, …>`
    keyed by the running total (single precision; the key is the total after adding, passed in RDX: Verified,
    disassembly at 0x140559d0e).
  - then, per group in the iteration order of the game's `boost::unordered_map<int, …>` (17 buckets: the first
    prime ≥ the default 11 in boost's table at 0x141680760, which starts 17, 29, 37...; key mod 17; a key in an
    empty bucket is linked at the front of the list, a key whose bucket is in use just before that bucket's first
    key: **Verified (decompiled)**, insert FUN_14057ab80): a group with one entry keeps it with no
    roll; with more, one float roll in [0, total) and the first entry whose running total exceeds it is kept.
    An empty group adds a null entry (not drawn).
  - output: the group 0 entries in list order, then one per group. In the base data no group 0 entry has chance
    0; 716 part references, 76 in groups other than 0; no record has more than 17 groups (rehashing is not
    modelled).
- **Recursion and roll order** (FUN_1405607f0, **Verified (decompiled)**): choose from the holder's list, then for
  each chosen part create it (below) and recurse into the part's own `parts`; after the loop, the holder's
  instances (`lights`, `nodes`) are created. Rolls drawn on the way, which shift later choices:
  - a rotating part (its own or its holder's `rotation function` > 0, and no collision file): one roll for a
    speed variation (×0.95..1.05 of `rotation speed max`) unless the function is ROTATION_WIND_SPEED, and one
    float roll for a **start angle** in [0, 2π) about `rotation axis` (0 X, 1 Y, 2 Z) when it has an entity
    (FUN_140556250, **Verified (decompiled)**). A part with function 0 under a rotating holder counts as 6.
  - a LIGHT instance: one roll (brightness variance, FUN_140553be0, **Verified (decompiled)**).
  - an instance targeting an `is node` BUILDING: the node object gets a handle with a random serial, one roll
    (FUN_14057c860 → FUN_1400d1c20; **Observed**: the branch depends on the node's handle type, assumed to be 11).
  - EFFECT instances: an effect can create lights (path FUN_14040a3a0 → … → FUN_140553be0); not modelled
    (**Unknown**, counted as no roll).
  - part materials that are collections may roll when the mesh finishes loading (a callback); whether that
    happens inside the seeded sequence is **Unknown** (not modelled).

  All rolls of the top-level `parts` list happen before any part is created, so **the building's own choice is
  exact**; only choices inside parts (Fish Drying Rack, Robot_Parts_Shredder) can be shifted by the unmodelled
  cases. No in-game comparison was made.

### Placing a part

- **Mesh** (FUN_14055f6e0, **Verified (decompiled)**): `phs or mesh`; a part whose file name is empty or does not
  contain `.mesh` (case-sensitive) creates nothing. The mesh is looked up by bare file name (FUN_1409b4610). All
  placed parts name a `.mesh` or nothing (Observed).
- **Scene nodes** (FUN_14055e150, FUN_140449e60, **Verified (decompiled)**): the building has a node at its
  position and rotation (no scale). Each part entity gets a child node of its parent: the building's node for a
  top-level part, the parent part's node for a sub-part (the building's again when the parent made no entity).
  The node's position is (`offset X`, `offset Y`, `offset Z`) × the BUILDING `scale` (no offset when `is for
  position marker`), its scale the BUILDING `scale`, its orientation identity (then the start angle for rotating
  parts). So **the offset is scaled with the building**: world = position + rotation × (scale × (vertex +
  offset)) for a top-level part. A sub-part also inherits its parent node's scale (Ogre's default; no base-game
  building has both `scale` ≠ 1 and sub-parts, so this compounding is untested).
- **Orientation and height**: the instance quaternion (stored w, x, y, z) is an Ogre orientation with no axis
  change: **Verified** by screenshot (walls and gates of The Hub join; a wrong convention tilts or scatters them).
  Height: the state's `world Y pos` (all 130 placements around The Hub have one), else terrain height +
  instance Y. Meshes are modelled in building space (**Verified** by screenshot, 2026-10-04).
- The viewer applies the start angle for rotation functions 1–4 and 6 (windmills, fans, drills, spinners) but not
  for ROTATION_TARGET (5, turrets), whose aiming code takes over in the game (not traced); the rolls are drawn
  either way.

### Doors

Doors are not placed in the zone files (no DOORS-category placement exists). `createPhysical` creates one door
building per `doors` entry of the BUILDING, at the building's position and with its rotation, parented to it
(FUN_1405609e0 → FUN_14057cc70, **Verified (decompiled)**), only for an ordinary, unparented building that is not
destroyed. The door's own parts follow the rules above (same seed, same spot). 1,261 placements list doors; 990 draw
one (the other 271 are destroyed).
**Verified** by screenshot: the door of a Small Shack in Stack fills its frame (2026-10-04). Doors are drawn
closed; opening (`door move dist` along `door axis`) is a run-time state (not traced).

### States that change what is drawn

From the GAMESTATE_BUILDING (counts over the 11,824 zone states):

- `destroyed` (276 true, e.g. 21 in The Hub): every part with a `destroyed mesh` uses it; a part without one keeps
  its mesh on floor 0 (`building floor`) and loses its entity on upper floors; stairs (`is stairs`) without one
  are removed (FUN_14055f6e0, **Verified (decompiled)**; fcs.def says the same). A destroyed building gets no
  doors. A part without an entity still rolls (rotation speed) and its children hang under the building's node.
- `is complete` is true for all 11,824 placed states, so no placed building shows the construction shader
  (scaffolding, FUN_140557be0).
- `interior` parts (chosen after the second `srand(seed)`) are loaded only when the player is inside; upper floors
  are hidden by the roof logic only then. An exterior view draws the `parts` tree (Observed from fcs.def; the
  hiding code was not traced).
- `exterior layout name` (325 placed buildings): the building also gets that exterior layout's objects (signs,
  banners), see below. `interior layout name` (772): its furniture layout.

### Building layouts

A building layout is an INSTANCE_COLLECTION of `interiors.level` whose instances are BUILDINGs placed relative to
another building: an **exterior layout** holds the signs and banners on the outside of a building (shop signs, bar signs,
faction banners), an **interior layout** its furniture. Examined 2026-10-05 over the merged base-game layers.

- **Records** (Verified: all 410 after merging carry `associated building`): fields `is exterior` (bool), `associated
  building` (string, a BUILDING id); seen on exterior layouts (Observed) also `is shop`, `is bar`, `is recruitment`,
  `flophouse`, `public` (ints) and on some `associated interior` (the name of the interior layout that goes with it, e.g.
  `trade`). 165 are exterior. Ids are `EXT-<associated building>-<name>` for exterior layouts and `<associated building>-<name>` for
  interior ones, where `<name>` is the record's name (Verified for all 245 interior and 163 of the 165 exterior ones; the
  other two, `EXT-3793-TwoStorey.mod-Slave Sign` and `EXT-3384-TwoStorey.mod-Barracks`, are associated with 3384 and 3795,
  so the `associated building` field, not the id, is what links a layout to its building).
- **Instances**: target a BUILDING (all 471 exterior instances resolve; of 1,135 placed through layouts, 545 are `is node`
  markers such as "Node Shopkeeper"), position and rotation in the frame of the building the layout is used on (local
  offsets up to 500 units, Y the height above the building's origin), one GAMESTATE_BUILDING state each (in `interiors.level`,
  with an absolute `world Y pos` of the building the layout was authored on, so not usable for another building).
  Two exterior layouts are empty.
- **Which layout** (Observed; the game's lookup was not traced): of the 325 exterior names, 262 match the id
  `EXT-<building>-<name>` exactly. `BuildingLayouts.Find` looks among the layouts whose `associated building` is the building,
  then among those of the buildings in its `shares interiors with` list (e.g. Old Storm House → Storm House), each with an exact
  name before a case-insensitive one (`banners`, `Mechanical SIgn`): 278 resolve on the building itself, 34 through a shared
  one, 22 of the 312 only ignoring case. 13 stay unresolved (Watchtower `Barracks` 4, Longhouse `Banners` 4 and `banners` 2,
  Outpost s-IV `Banners` 2, L-House `Trade Sign` 1: no layout of that name for the building or a shared one); whether the game
  then takes another building's layout of that name is **Unknown** (the viewer draws nothing, as the positions fit only the
  building they were made for).
- **Placement**: world = the building's node (position with the state's `world Y pos`, rotation, no scale) × the item's
  rotation and position, i.e. the item's offset is rotated by the building's rotation and not scaled by the building's
  `scale`; the item is then built like any BUILDING (parts, its own `scale`, seed from its own world X/Z). **Observed** by
  screenshot (2026-10-05): the BAR sign of the Storm House in The Hub sits flat on the wall beside the door at lintel height,
  and the travel sign of a Swamp Shack (`scale` 0.77) hangs from its bracket at the house's corner; with the offset scaled it
  would sit inside the wall. That the game composes them this way, and the seed it uses for the item's parts, are not traced.
- Destroyed buildings: the viewer adds no exterior layout (**Unknown** what the game does). A squad's `layout exterior` /
  `layout interior` (SQUAD_TEMPLATE, fcs.def "Layout name to use for the specified building") can set the layout at run time;
  not modelled.
- Interior layouts are not drawn by the viewer (hidden by walls and roofs from outside; Unknown whether the game creates
  their furniture before the player enters).

### Materials ("the local town material")

The part material lookup (FUN_14054cd30, called when the part's mesh has loaded; **Verified (decompiled)**):

1. The part's **first** `material` reference (MATERIAL_SPEC). Only the first reference of each list is ever used;
   a `material match` list is read only by the building preview / icon code (FUN_140848950).
2. Else the building's **base material**, set once in `createPhysical`: the BUILDING's first `material`, else the
   material of the building's **town**.
3. Else `360-gamedata.base` (severe10 - blue), with the log warning "building with no material collection
   assigned".

Every chosen record goes through the collection rule (FUN_140580310 / FUN_14057fd40): a MATERIAL_SPEC with a
non-empty `material` list is replaced by one of its entries, weighted by val0 (0 counts as 100: **Verified
(decompiled)**, a `CMOVZ` with 100), skipping entries whose `world state` does not hold. The roll uses the
unseeded generator (the base material is picked before `srand`), so **the game's pick from a collection is not
reproducible**; the viewer seeds its own pick by the town instance or placement id.

- **Town material** (town setup FUN_1409353c0, **Verified (decompiled)**): the TOWN's first `material`, else
  `742-gamedata.base` (moor1-main), through the collection rule. In the base game 10 of 346 TOWN records name a
  material, all "moor mats COLLECTION" (moor1-main 100, moor1-squared 20, wall concrete dots 100); every other
  town uses moor1-main. 207 of the 252 placed BUILDING types (9,837 placements) have no material of their own
  and so take the town's; no placed building names a collection; 32 parts do.
- **Which town** (FUN_140296940 → FUN_1409f9180, **Verified (decompiled)**): the building's town handle (state
  `townTYPE` 13 = TOWN with `townC`, `townCS`, `townI`, `townS`), else the nearest town by X/Z of any type except
  TOWN_NEST_MARKER (8), with no distance limit (FUN_140927f10). In the level files a state's (`townC`, `townCS`)
  matches a GAMESTATE_TOWN's (`handC`, `handCS`) for 2,320 of 9,734 states with a handle; those towns lie a
  median 707 units away, and the nearest town is the same one for 2,305 of them (**Observed**; matching on `C`
  alone gives towns a median 75,000 units away, so `C` alone is not an id). The viewer uses the matched town,
  else the nearest one. Which live town an unmatched handle resolves to in the game is **Unknown**.

### Buildings with no parts

- **Iron Resource** (1,241) and **Copper Resource** (94) are `is foliage` BUILDINGs (state `foliage` true,
  `resource mult`), the production side of mineable rocks. The game draws nothing for the building: the visible
  rock is a FOLIAGE_MESH whose `building type` names it (ResourceRock-IRON02..06, Pyrite, Motor parts, TechJunk...:
  24 FOLIAGE_MESH records), placed by the foliage system, which creates or finds the building at the rock's
  position (FUN_1406d2410, **Verified (decompiled)**: "Foliage object … with associated building …"). The placer
  is in foliage.md; the stored placements do not match the current records (foliage.md, "Not verifiable"), and
  the stored rotation is identity. The viewer draws nothing for the buildings (counted in its log); the rocks
  come from its foliage placement.
- **Ramp** (1 placement, zone 25.34): no parts, nothing drawn.
- Also not drawn: `is node` buildings (invisible markers), `distant mesh` (low-poly town batches), the
  INVENTORY_STATE entries at the origin.

### Nest debris (not drawn)

TOWN records can scatter objects at run time (fcs.def): `debris building` (BUILDING; val0 the count "+50% randomisation",
val1/val2 the min/max cluster range around `num centrepoints` centre points, "for nests only" unless `spawn in town
centre`), `debris` (NEST_ITEM) and `loot spawn`. Of the 304 placed towns, 12 list `debris building` (e.g. Deadhive Overrun:
prisoner poles and torch posts; Dust King Tower and Cannibal Village: torch posts, camp beds, campfires, cooking pit) and none
list `debris`; 6 placed towns set `spawn in town centre` (Observed, base game). Nothing of it is stored in the level files, and
the scatter code was not traced, so whether it is seeded (reproducible) is **Unknown**; the viewer draws none of it.

### Map features

The MAP_FEATURES `mesh` (all 1,810 are `.mesh`), scaled by the entry's per-axis scale (non-uniform for some, e.g.
1.77 × 1.12 × 1.77), rotated, moved to its absolute position; `hidden` ones are skipped. Texture modes (fcs.def
`MapFeatureMode`): UV_MAPPED 475, TRIPLANAR 65, TERRAIN 820, DUAL_TEXTURE 409, FOLIAGE 25, DUAL_TRIPLANAR 16.
TERRAIN mode "uses textures from the current biome": Kenshi's `mapfeature_fs` runs the terrain layer model
without the road layer (Observed, terrainfp4.hlsl); the viewer draws them with its terrain shader the same way.
Their materials come from the model viewer's resolver ([../viewer.md](../viewer.md#how-mesh-textures-are-resolved)).

What is not drawn (Observed, base game, 2026-10-05):
- 44 placements are `hidden` (fcs.def: "markers to attach effects"): Volk-Cloud Placer 24, Volc-small-steamers Placer 18,
  Permanent-dust-storm Placer 1, Marker-Attractor 1. The placers carry an EFFECT as an instance of the feature record (volcano
  plumes, steam, a dust storm; [weather.md](weather.md#effect-placers-on-the-map-verified-records-and-featuresdat)), which the
  viewer does not draw (no effects).
- 11 placements (7 ids, e.g. `2332-Newwworld.mod`, `2332-D-Newwworld.mod`) name records no base file defines.
- 1 record names a mesh that is not in the install: Fractal_Distiller_Feature (`52165-Newwworld.mod`,
  `data/newland/Assets/Buildings/Fractal_Distiller01.mesh`).
- Other MAP_FEATURES fields: `distance` (fcs.def "Visibility distance for feature", default 2000; 18 distinct values from 80 to
  8000 in the base game) is not applied by the viewer, which draws features to its object distance; how the game uses it
  (an Ogre rendering distance or a load radius) is **Unknown**. `local coordinates` (86 records) only changes triplanar and
  terrain texture mapping ("Use local coordinates instead of global coordinates", fcs.def). `effects`, `sounds` and `bird
  attractor` attach non-mesh things.

### Draw distance and distant towns

How far placed objects are drawn. Constants are in `ObjectRanges`; the sources are the game's settings and its object/building
setup code, read in the decompiled `kenshi_x64.exe` for facts only.

- **Settings** (Verified, settings.cfg readers): `objects view range` (default 3000; a value of 1000 or less is replaced by 3000),
  `feature range` (default 2, 0 to 6; zones around the camera map features are loaded for) and `distant town range` (default 6,
  0 to 10; zones around the camera distant towns are shown for).
- **Rendering distance of a part** (Verified for the 3000 and the radius test, Observed for the function numbers): every building part gets
  the objects view range as its Ogre rendering distance, except that a part whose mesh's local bounding radius is above 100 is
  left unlimited, a generator building (`BuildingFunction` 5) gets none, and a turret (12) gets half of the range. Which virtual of
  the building returns the function is not traced, hence Observed. The distance Ogre compares against is not decompiled
  (Unknown; the viewer uses camera to bounds centre minus radius).
- **Distant towns** (Verified: file survey and the game's town code): a TOWN record with the `distant mesh` flag has one mesh
  `data/meshes/distant/distant_<n>.mesh`, where `n` is the `handC` of the town's GAMESTATE_TOWN. Its vertices are relative to the town's
  placement position (Y included). The mesh is the merge of the `distant mesh` fields of the town's buildings: for The Hub it matches
  the building distants exactly (same triangles), for towns 75 and 88 the triangle counts are equal (`ObjectLodTests`, install-gated).
  A building's `distant mesh` is a path in the install (`.\data\...`); some have vertex colours that the `DistantTown` material
  (`data/buildings/distant/distant_diffuse.dds`) multiplies by 1.5 with the texture (Observed in the shader's text).
- The files exist in the install for 88 towns (Verified: directory listing). The game can also generate them (a setting, "generate
  distant towns"); the viewer does not generate, but draws a town without a baked mesh as one instance of each building's
  own `distant mesh` (equivalent to what generation batches; the game with generation off shows nothing there).
- **Unknown**: the exact zone load radius for real objects, the hash function used to pick the file number from the town state
  (`FUN_1400d6260` in the project's analysis, not decoded), and the real BuildingFunction mapping.

## Open questions

- Meaning of the trailer ints and of the stored record sizes.
- The game's layer merge rules, and where mods put level data.
- The five-part handle fields (`C`, `CS`, `I`, `S`, `TYPE`): serialized object references (Observed: a
  building's `town*` handle matches a town state's `hand*` by `C` and `CS`, see "Materials" above; the game gives
  a new handle a random serial from `rand()`, FUN_1400d1c20). Why most town handles match no town state.
- What the INVENTORY_STATE entries in the building list are for; INVENTORY_* and ITEM_PLACEMENT_GROUP
  contents; the game's building layout lookup (the 13 unresolved exterior names) and nest debris scatter.
- ROAD `start`/`end`, the road point x parameter, road foliage w.
- `fogfeatures.dat`, `globalPathing.path` layouts (`blendinfo.dat`: see terrain.md).
- Building assembly leftovers: rolls drawn by EFFECT instances and by part-material collections inside the
  seeded sequence; which foliage mesh stands at an Iron/Copper Resource placement; door open state; turret aiming.
