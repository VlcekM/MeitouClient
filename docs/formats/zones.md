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
**Observed** (`--data-check`, unmodded 1.0.68): later layers define 4047 earlier-layer ids again (all zone, level and interiors
files; 957 in `rebirth/interiors.level`) and nothing else; that is how layers replace placements, so it is not reported as a problem.

## Interior masks

**Verified** (probe of the `interior mask` parts' `xml collision` files): 17 of the trigger files hold more than one shape (boxes, capsules,
convex meshes), among them the L-House, Y-House, Longhouse, Saloon, Snailhouse and Watchtower ones. The Watchtower's
(`Storm_House_09_Interior_TRIGGER.xml`) is the tower body (a convex mesh) and a capsule at the roof, about 284 up. Along the building's X axis
on one side the body reaches about 96 from the center and the outer wall (`Storm09-WallCOL.xml`) about 101, while the capsule reaches about 144.

Meitou: `InteriorVolume.Contains` tests each shape's own footprint and height range, so ground between shapes, or below a roof shape, is not
indoors; the volume's `Footprint` stays the convex hull of all of them. `ContainsGround` ignores height and holds a point under any shape's
footprint, as a ray cast down from above hits the shape.

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
(decompiled), FUN_14039c7e0); that this flag is editor mode is **Observed** (inferred, not traced). Nest debris is population state ([nest debris](#nest-debris-and-loot)).
Materials select local town/biome/climate inputs; do not substitute a material solely by mesh name.

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
| `name` | string | display name; housing replaces it ([town residents](../game/factions-squads-towns.md#town-residents)) |
| `residents` handle, `residents template` | handle, string | the platoon whose home it is and that platoon's SQUAD_TEMPLATE ([which](../game/factions-squads-towns.md#whose-template-a-home-carries)); null / empty with no residents. The reader `FUN_140552b70` drops a non-empty `residents template` (level data's is never used) |
| `floornum` | int | floor |
| `destroyed` | bool | +0x1a1 |
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
| `world Y pos` | float | absolute height the building stood at; written, never read ([placement height](#placement-height)) |
| `foliage` | bool | |
| `isInsideBuilding` | handle | parent building |
| `internalbuildings-<i>` | handle | buildings inside it |
| `is inside building` | bool | |
| `usagenode_<i>` | | usage node state |

New instance ids are `<n>-<container name>-INGAME`, n from the container's id counter (**Verified**, `FUN_140386770`;
e.g. `19765-zone39.16-INGAME`). Instance ids are unique only within one zone file: **Observed** (base load order, scratch probe over
`WorldLevelData.Buildings()`) 8,754 of 11,714 placements share their id with one in another zone, 580 ids within two zones of each other
(`2-Newwworld-INGAME` is a Y-House in 19.29 and another house in 19.28). Meitou names a building at run time by `BuildingKey`
`"<zone x>.<zone z>:<id>"` (doors, fixtures, renderer placements, saved-state lookup); the raw id stays in files and in the material seed. In saves the instance Y stays the height above ground ([placement height](#placement-height)). On import, non-player buildings are rebuilt complete
([construction](../game/buildings-production.md#construction-tick)). A `creates player town` building makes a TOWN
"Your Outpost" ([placement](../game/buildings-production.md#placement)), saved in `quick.save`'s town list, not in a
zone file ([player outposts](save.md#player-outposts)); the buildings' `town` handle names it.

### Placement height

- **Verified** by code (1.0.68): `world Y pos` occurs once in the exe, used only by the writer `Building::_saveBuildingState` (`FUN_1405547f0`), which
  stores the building's current Y (`getPosition`, Building+0x48). Nothing reads it.
- `RootObjectFactory::createBuilding` (`FUN_14057cc70`) makes a building at its instance position and skips one whose instance Y is below -1000
  ("Deleted a ... under the ground"). The constructor (`FUN_14055ca90`) keeps the instance Y as the height above ground (Building+0x278); a nonzero
  `player furniture Y offset` replaces it on load (`FUN_140555650`).
- `Building::createPhysical` (`FUN_1405609e0`): with no `isInsideBuilding` or `mounted` handle and not layout furniture, a non-door building stands at
  building ground + height above ground; with a parent, at the parent's Y + height above ground. `WallBuilding::createPhysical` (`FUN_140562260`) runs it and then only moves
  the position marker.
- Building ground (`UtilityT::getTerrainWithWaterHeight`, `FUN_1409b3720`): max(100, the zone's height grid at X/Z). The grid (`ZoneMap::getTerrainHeight`,
  `FUN_1407eac60`, filled by `FUN_1407eab40` from `Terrain::getRawData`) holds every other heightmap sample, 129 x 129 per zone, 36 units apart, read
  bilinearly; with no grid the terrain plugin's own height is used. It is up to ~16 units off the full heightmap on steep ground.
- **Verified** against original saves (scratch probe; Beep Fan Club 1, METARU, autosave1): every level placement with its level instance Y (3205, 3412,
  3268; 364-408 of them inside or mounted on a building of their zone, at the parent's height + instance Y) and every game-made one (269) has saved
  `world Y pos` = that height within 0.05, none off; the four mounted lights of 22.26 moved with their wall. Stale level values are written back at that height
  (46.16 `677-Newwworld-INGAME` 23.6 higher, 24.40 `901`/`903-rebirth-INGAME`). Pontoon Bridges 27.40 `6`/`10-Newwworld-INGAME` (instance Y -92726,
  -86557) are missing from METARU's zone file, which keeps the zone's other three.
- Meitou: `WorldLevelData.Load(install)` stands each outdoor level building at `TerrainHeightmap.BuildingGroundAt` + its height above, one inside or
  mounted on another of its zone at that one's height + its height above (`PlacementHeights`); the height above is a nonzero `player furniture Y offset`,
  else the instance Y. A child whose parent is in another zone keeps its state's height; one under -1000 is left out.

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

- The state's `town*` handle is kept only for a player town; for any other town it is dropped and the building is placed by position.
- Else, a record with `interior` references and no parent building takes the nearest non-player town within 10000 (any type); else no town.
- Meitou with this rule (probe over the level data, 2026-10-20): Sho-Battai claims 169 buildings, The Hub 128, Squin 45, Stack 137, all inside each town's
  GAMESTATE_TOWN zones; 261 towns hold 9766 buildings. Residents: Squin 37 (save 36), Stack 94 (save 93-95).

## Nest debris and loot

- Persistence: the items are saved with the nest as their `persist` handle ([save](save.md)); the buildings are not saved.

## Open questions

- The game's layer merge rules, and where mods put level data.

## Expression reference

- `data/newland/land/features.dat`: ` char[4] magic "MF01" int32 zoneCount 4096 (64 × 64) {uint32 offset, uint32 size}[zoneCount] offset/size of each zone's block; 0/0 = no features block: int32 count, count × uint8 length, char[length] stringId a MAP_FEATURES record float32 x, y, z absolute world position float32 sx, sy, sz scale float32 qw, qx, qy, qz rotation, w first `
- Choosing the parts: `x = x × 214013 + 2531011`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Legacy world: `data/leveldata/` (Observed)
