# World placements

Zone/level/feature research from FCS loader, exact file round-trips, merged records and binary
building assembly. [FCS](fcs-mod.md), [terrain](terrain.md), [collision](collision.md),
[materials](runtime-materials.md), [objects](../render-objects.md).

## Containers and IDs

**Verified** usual FCS type15 header(type,nextId,count), type16 mod-style metadata where present;
record layout16. Preserve stored byteSize even when incorrect. Most type15 files have count+int32
trailer; exact meaning unknown, require exact file end. Legacy10/13 have old tags/integer IDs,
three reference ints from10 and13 trailers of8-byte entries; read-only support.
State ID=placementID+'-S'+hex type (35→S23,94→S5e), verified31849 records. Definition target IDs
join merged game data, not names. Base placements/layers and save-state overrides are distinct.
**Observed** WorldLevelData merges fields per key/instances per ID, empty target deletes;
flags0 may still override. Exact original zone/mod merge behaviour remains partly unknown.

## Interior masks

**Observed** Hub `Storm_House_01_Interior.mesh` wall triangle centres can lie about0.09 units
outside the building's convex interior-mask hull (OgreMeshReader positions transformed into
world space and compared with InteriorVolume footprint planes). A mask is not an exact visible-surface boundary.

**Verified** (probe: placed part bounds, zone 20.33 in the `Newwworld` and `rebirth` layers, which agree): the Hub's two
`Defensive Wall IV` (`14`, `66`) end in the Shinobi Thieves `Watchtower` (`2`, Storm House 09), the wall's corner: `66` runs
about 150 inside its east side, `14` overlaps its north edge (bounds boxes). No part rule hides them and building parts take no interior cut
([interior cut](../render-interior-cut.md)), so the game shows them inside the tower too (**Observed**: not seen in the game; issue #111).

## Placement files and assembly

Zones indexed floor(coord/4608)+32; `.zone` contains building/loose-item instances, town and state
links. Leveldata/interiors/roads have distinct collections. MF01 layout below:4096 directory
entries offset/size, zero pair empty; block count, u8 ID, position/scale/quaternion w-first.
**Verified** slot=(X=i%64,Z=i/64); some features lie outside own slot; absolute Y can be submerged.
Fogfeatures is decoded in [fogfeatures](fogfeatures.md); globalPathing remains incompletely decoded.

Buildings resolve part lists with seeded variants and count/slot rules. Compose root/part transforms
in correct axis/scale/order; random/state-driven parts, doors, interiors, destroyed/ruined state,
layout furniture/lights and town material replacements are independent. Preserve exterior/interior
visibility and floor checks. Single-mesh/no-part buildings and MAP_FEATURES follow their own
paths. A MAP_FEATURES record draws its mesh only while `hidden` is false, and makes every EFFECT
instance at its node whatever `hidden` says (**Verified** (decompiled), FUN_14039c7e0, FUN_14039b130); an empty texture map
is `white.dds`, an empty normal map `flat.dds` (**Verified** (decompiled), FUN_140841a50 via FUN_140843920). The `hidden` test
is skipped while global `DAT_142134690+0x298` is set, which then draws `athene.mesh` for a feature with no mesh (**Verified**
(decompiled), FUN_14039c7e0); that this flag is editor mode is **Observed** (inferred, not traced). Nest debris is population state.
Materials select local town/biome/climate inputs; do not substitute a material solely by mesh name.

Original part view distances differ from whole-object reach. Distant towns use stand-ins/LOD and
separate fade; Meitou extended reach is a selectable engine choice. Source/table expressions
retain decoded part/layout/placement values and unknowns, not a blanket visual parity claim.

## Placement keys

**Verified** `LevelFileTests.Base_game_heights_match_the_heightmap`: zone indices X/Y map to
world +X/+Z. `0-buildinglist` INSTANCE_COLLECTION entries target BUILDING, quaternion w-first,
one state ID; X/Z absolute, Y height above terrain (**Observed** median difference0.02 over
11693 buildings). Zero-position INVENTORY_STATE targets are separate entries, purpose Unknown.
12 placements target undefined97576–97578-Newwworld.mod; other11702 building targets resolve.

**Observed** GAMESTATE_BUILDING keys: bool `is complete`, `is public day/night`, `is inside
building`, `destroyed`, `foliage`, `broken`, `power on`, `gets battery`; float `world Y pos`
(absolute), `construction progress`, `0construction mats`, `player furniture Y offset`,
`batterycharge`; int `version`270, `floornum`, `flags`; handles `handle*`, `town*`, `residents*`,
`isInsideBuilding*`, `mounted*`, each suffix TYPE/C/CS/I/S. Strings `name`, `owner faction ID`,
`squad template`, `residents template`. Faction58697-rebirth.mod is unresolved in28 states.
A turret's `mounted` handle (TYPE BUILDING) names its parent by C/I = that building state's
`handleC`/`handleI` in the same zone (**Verified**: Shark's harpoon turret `7523` names the
`Residential` tower it stands on, zone 21.42).

Building state writer/reader (**Verified** by code, no player-built building in the sample saves): Building slot 17
`FUN_140552320` writes, slot 18 `FUN_140552b70` and slot 112 `FUN_140555650` read; the shared part is slot 111
`FUN_1405547f0`. Keys a writer must produce for a player-built building:

| Key | Type | Value |
|---|---|---|
| `name` | string | building name |
| `residents` handle, `residents template` | handle, string | null / empty for a new building |
| `floornum` | int | floor |
| `destroyed` | bool | +0x1a1 |
| `town` | handle | its town (outpost), only when the building is not inside another one (**Observed** condition) |
| `flags` | int | bit0 is outside (+0x240), bit1 foliage, bit2 +0x241 (**Unknown**); old saves use bools `is outside` / `foliage` |
| `serialised interior` | int 1 | when not in interior edit mode, followed by the interior layout records; on load 1 keeps the saved furniture, anything else makes the building generate its interior (**Verified** by code, [zone files](save.md#zone-files)) |
| `construction progress` | float | progress in material units |
| `is complete` | bool | absent means complete |
| `is public day` | bool | |
| `pause` | bool | construction paused |
| `<i>construction mats` | float | delivered amount of material entry i, i from 0 in entry order |
| `owner faction ID` | string | owner FACTION id |
| `handle` | handle | own handle |
| `interior layout name`, `exterior layout name` | string | only with an interior |
| `player furniture Y offset` | float | height above the parent building's origin for furniture, else 0 |
| `version` | int | |
| `world Y pos` | float | absolute height |
| `foliage` | bool | |
| `squad template` | string | |
| `isInsideBuilding` | handle | parent building |
| `internalbuildings-<i>` | handle | buildings inside it |
| `is inside building` | bool | |
| `usagenode_<i>` | | usage node state |

New instance ids are `<n>-<container name>-INGAME`, n from the container's id counter (**Verified**, `FUN_140386770`;
e.g. `19765-zone39.16-INGAME`). Instance ids are unique only within one zone file: **Observed** (base load order, scratch probe over
`WorldLevelData.Buildings()`) 8,754 of 11,714 placements share their id with one in another zone, 580 ids within two zones of each other
(`2-Newwworld-INGAME` is a Y-House in 19.29 and another house in 19.28). Meitou names a building at run time by `BuildingKey`
`"<zone x>.<zone z>:<id>"` (doors, fixtures, renderer placements, saved-state lookup); the raw id stays in files and in the material seed. Their instance Y equals the state's `world Y pos` (**Observed**: 11405 of 11405 in the sample saves). On import, non-player buildings are rebuilt complete
([construction](../game/buildings-production.md#construction-tick)). A `creates player town` building makes a TOWN
"Your Outpost" ([placement](../game/buildings-production.md#placement)), saved in `quick.save`'s town list, not in a
zone file ([player outposts](save.md#player-outposts)); the buildings' `town` handle names it.

`0-townlist` GAMESTATE_TOWN_INSTANCE_LIST: target TOWN, absolute XYZ, unused zero quaternion,
GAMESTATE_TOWN state `-S5e`. **Verified**304 targets resolve;224 town states listing zones contain
their town,80 list none. Keys `zones_count`, `zone_x_<i>`, `zone_y_<i>`, handle `hand*`, string
`instance`=list entry ID. ROAD keys: `hidden`, `width`46..200, `length`, `weight`, `start`, `end`
(junction numbers, [roads](../game/factions-squads-towns.md#off-screen-world)); ordered instances target `-`, absolute positions, rotation w1/y0/z0,
x0..1 (**Observed**, purpose Unknown). `5-roadfoliage` target- points use quaternion w as
size0.1..3590 (**Observed**, likely clearing radius). Furnished interiors use GAMESTATE_BUILDING,
ITEM_PLACEMENT_GROUP, inventory states and collections, often `<n>-[basedata]-INGAME` IDs;
`WorldLevelData.Load(includeInteriors:true)` merges them. Item groups: [loose items](../game/factions-squads-towns.md#loose-items-in-buildings); inventory semantics Unknown.

### Files

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

### `data/newland/land/features.dat` (`MapFeatureFile`) — layout/example

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

## Town membership

**Observed** (building import `FUN_14057cc70`, position lookup `FUN_140928890`, `Town::vfunc_78` / `vfunc_84`; Meitou `TownBuildings.MemberOf`):
- The state's `town*` handle is kept only for a player town; for any other town it is dropped and the building is placed by position.
- By position: the nearest town whose radius holds it (X/Z), searching the 3 x 3 area sectors round it, nest markers left out. The radius is
  [the type table](../game/buildings-production.md#placement) (town 2500, village 937.5, ruins 875, POI 437.5, others 1250, x `town radius mult`); `size radius`
  is read only by the nest debris scatter (`FUN_140938440`). Meitou does not model the sector window.
- Else, a record with `interior` references and no parent building takes the nearest non-player town within 10000 (any type); else no town.
- Meitou with this rule (probe over the level data, 2026-10-20): Sho-Battai claims 169 buildings, The Hub 128, Squin 45, Stack 137, all inside each town's
  GAMESTATE_TOWN zones; 261 towns hold 9766 buildings. Residents: Squin 37 (save 36), Stack 94 (save 93-95).

## Open questions

- Meaning of the trailer ints and of the stored record sizes.
- The game's layer merge rules, and where mods put level data.
- The five-part handle fields (`C`, `CS`, `I`, `S`, `TYPE`): serialized object references (Observed: a
  building's `town*` handle matches a town state's `hand*` by `C` and `CS`, see "Materials" above; the game gives
  a new handle a random serial from `rand()`, FUN_1400d1c20). Why most town handles match no town state.
- What the INVENTORY_STATE entries in the building list are for; INVENTORY_*
  contents; the game's building layout lookup (the 13 unresolved exterior names) and nest debris scatter.
- ROAD `start`/`end`, the road point x parameter, road foliage w.
- `fogfeatures.dat`, `globalPathing.path` layouts (`blendinfo.dat`: see terrain.md).
- Building assembly leftovers: rolls drawn by part-material collections inside the seeded sequence (EFFECT
  instances draw none, lights.md "Effects on buildings"); which foliage mesh stands at an Iron/Copper Resource placement; door open state; turret aiming.

## Expression reference

- `data/newland/land/features.dat`: ` char[4] magic "MF01" int32 zoneCount 4096 (64 × 64) {uint32 offset, uint32 size}[zoneCount] offset/size of each zone's block; 0/0 = no features block: int32 count, count × uint8 length, char[length] stringId a MAP_FEATURES record float32 x, y, z absolute world position float32 sx, sy, sz scale float32 qw, qx, qy, qz rotation, w first `
- Choosing the parts: `x = x × 214013 + 2531011`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- `data/newland/land/fogfeatures.dat` (Observed, not decoded)
- `data/globalPathing.path` (Observed, not decoded)
- Legacy world: `data/leveldata/` (Observed)
