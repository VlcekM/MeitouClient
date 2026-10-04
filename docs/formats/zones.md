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
Not analyzed beyond counts; `WorldLevelData.Load(..., includeInteriors: true)` merges them.

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

How `Meitou.Data.World.WorldObjectLayout` turns placements into meshes for `meitou-viewer --world`
([../viewer.md](../viewer.md#world-mode)). Counts are over the 11,714 base-game building placements (252
distinct BUILDING records) and 1,810 resolvable `features.dat` entries.

- **Building = its parts** (fcs.def, Observed in the data): a BUILDING draws the BUILDING_PART records of its
  `parts` list. Reference val0 is a group, val1 a chance: group 0 entries are each kept with val1 as an absolute
  percentage, other groups contribute one entry chosen by val1 weight. 716 part references, 76 in groups other
  than 0; 25 parts have `parts` of their own (drawn too). The game rolls these at random; the viewer seeds the
  roll with the placement id so a building always looks the same. Whether the game stores the result in the
  GAMESTATE_BUILDING is **Unknown**.
- **Part mesh**: `phs or mesh`. Every part of a placed building names a `.mesh` (712) or nothing (4); no `.phs`
  (Observed). Part meshes are modelled in building space: placed at the building's origin, The Hub's walls,
  gates and towers line up (**Verified** by screenshot, 2026-10-04). `offset X/Y/Z` (37 parts) moves a part
  unless `is for position marker` is set; whether the offset is applied before or after the building's
  `scale` is **Unknown** (the viewer adds it before scaling).
- **Placement transform**: mesh space → scale by BUILDING `scale` (117 placed types ≠ 1) → rotate by the instance
  quaternion → move to the position. The quaternion (stored w, x, y, z) is used as an Ogre orientation with no
  axis change: **Verified** by screenshot (walls and gates of The Hub join; a wrong convention tilts or
  scatters them). Height: the state's `world Y pos` (all 130 placements around The Hub have one), else
  terrain height + instance Y.
- Not drawn: `is node` buildings (invisible markers), the `interior` part lists (loaded only when inside),
  `destroyed mesh`, `distant mesh` (low-poly town batches), records with no parts (Iron Resource, Copper
  Resource, Ramp: 1,336 placements), the INVENTORY_STATE entries at the origin.
- **Map features**: the MAP_FEATURES `mesh` (all 1,810 are `.mesh`), scaled by the entry's per-axis scale
  (non-uniform for some, e.g. 1.77 × 1.12 × 1.77), rotated, moved to its absolute position; `hidden` ones are
  skipped. Texture modes (fcs.def `MapFeatureMode`): UV_MAPPED 475, TRIPLANAR 65, TERRAIN 820, DUAL_TEXTURE 409,
  FOLIAGE 25, DUAL_TRIPLANAR 16. TERRAIN mode "uses textures from the current biome": Kenshi's `mapfeature_fs`
  runs the terrain layer model without the road layer (Observed, terrainfp4.hlsl); the viewer draws them with
  its terrain shader the same way.
- Materials come from the model viewer's resolver ([../viewer.md](../viewer.md#how-mesh-textures-are-resolved));
  parts without a `material` take their building's. "If blank will use the local town material" (fcs.def,
  BUILDING `material`): which record that is, is **Unknown**, so such buildings fall back to the mesh's script
  material or stay untextured.

## Open questions

- Meaning of the trailer ints and of the stored record sizes.
- The game's layer merge rules, and where mods put level data.
- The five-part handle fields (`C`, `CS`, `I`, `S`, `TYPE`): probably serialized object references.
- What the INVENTORY_STATE entries in the building list are for; INVENTORY_* and ITEM_PLACEMENT_GROUP
  contents; `interiors.level` structure.
- ROAD `start`/`end`, the road point x parameter, road foliage w.
- `fogfeatures.dat`, `globalPathing.path` layouts (`blendinfo.dat`: see terrain.md).
- How the game picks building parts (RNG, seed, stored or not), the order of part offset and building scale, and
  the "local town material".
