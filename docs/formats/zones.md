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
flags0 may still override. The game merges level layers with its game-data loader (**Verified**, [Mods](#mods)).
**Observed** (`--data-check`, unmodded 1.0.68): later layers define 4047 earlier-layer ids again (all zone, level and interiors
files; 957 in `rebirth/interiors.level`) and nothing else; that is how layers replace placements, so it is not reported as a problem.

## Interior masks

**Observed** Hub `Storm_House_01_Interior.mesh` wall triangle centres can lie about0.09 units
outside the building's convex interior-mask hull (OgreMeshReader positions transformed into
world space and compared with InteriorVolume footprint planes). A mask is not an exact visible-surface boundary.

**Verified** (probe of the `interior mask` parts' `xml collision` files): 17 of the trigger files hold more than one shape (boxes, capsules,
convex meshes), among them the L-House, Y-House, Longhouse, Saloon, Snailhouse and Watchtower ones. The Watchtower's
(`Storm_House_09_Interior_TRIGGER.xml`) is the tower body (a convex mesh) and a capsule at the roof, about 284 up. Along the building's X axis
on one side the body reaches about 96 from the center and the outer wall (`Storm09-WallCOL.xml`) about 101, while the capsule reaches about 144.

**Observed** (original 1.0.68, plain game): a player character on the sand at the Sho-Battai Watchtower's outer foot, below the capsule and
outside the body, leaves the tower closed.

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
| `name` | string | display name; housing replaces it, and vacating resets it to the building's ([town residents](../game/factions-squads-towns.md#town-residents)) |
| `residents` handle, `residents template` | handle, string | the platoon whose home it is and that platoon's SQUAD_TEMPLATE ([which](../game/factions-squads-towns.md#whose-template-a-home-carries)); null / empty with no residents. The reader `FUN_140552b70` drops a non-empty `residents template` (level data's is never used) |
| `floornum` | int | floor |
| `destroyed` | bool | +0x1a1 |
| `town` | handle | its town (outpost), only when the building is not inside another one (**Observed** condition) |
| `flags` | int | bit0 is outside (+0x240), bit1 foliage, bit2 +0x241 ; old saves use bools `is outside` / `foliage` |
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
| `squad template` | string | the same id as `residents template` (**Observed**, every saved state) |
| `isInsideBuilding` | handle | parent building |
| `internalbuildings-<i>` | handle | buildings inside it |
| `is inside building` | bool | |
| `usagenode_<i>` | | usage node state |

New instance ids are `<n>-<container name>-INGAME`, n from the container's id counter (**Verified**, `FUN_140386770`;
e.g. `19765-zone39.16-INGAME`). **Verified** (decompile): the counter is the zone data's own (`GameData +0x8`), shared with the int ids of its
records: each step takes the next number not used as a record's int id (`FUN_1406bc560`), and every record a load adds takes one too (the
loader makes it with int id -1, `FUN_14066a920`, and `FUN_1406bf780` numbers such a record; the file's own int ids are not kept). Loading a
file restarts it at 0, numbers the file's new records, then raises it to the file's `nextId` (`FUN_1406c0b50`); a zone loads each layer's zone file
in load order, then the save's (`FUN_1409ffad0`; a missing file changes nothing), so the last file read sets it. An id is retried
with the next number while `FUN_1406c2e40` refuses it (every caller of `FUN_140386770` loops so); it refuses an id the building list already holds
with a non-empty target, so a cleared entry is taken over (**Verified**: the list is a `std::map` whose `operator[]`, `FUN_140100670`, returns node +0x40
as the entry; `FUN_1406c2e40` stores the target's stringID at entry +0x20; `FUN_1400fe350` reads a string's length at +0x10; so the tested node
+0x70 is the target's length).

**Verified** (decompile) when numbers are taken: only `FUN_140386770` and `FUN_1406bf780` compose `-INGAME` ids. `FUN_140386770`'s callers are the
serialise context `FUN_140386d20` (Building, item, character serialise), TownBase/Nest vtable slot 17 (`FUN_14036e8f0`) and Town slot 85
(`FUN_140376b80`); none is the building factory. A building gets its id when first serialised: `FUN_140386d20` keeps the building's own id
(Building +0x100, empty from the constructor `FUN_14055ca90`) or takes a new one and writes it back. The zone save (`FUN_14036dde0`, run by
`saveGame` and by every zone unload, [zone files](save.md#zone-files)) empties the container without resetting the counter (`FUN_1406bf510`,
`FUN_1406bf1a0`), makes `0-buildinglist` (`FUN_1406c0750`: one number), then serialises each building of the zone into it (`FUN_14036c9e0`, slot 17,
`FUN_140552320`): a new id is checked against that fresh list, then the `<id>-S23` state record (`FUN_140387610`) takes the next number. Every record
the save makes takes one, and `GameDataContainer::save` (`FUN_1406bd570`) writes the counter as the file's `nextId`. **Observed** (original saves
bench-bases-2026-10-27, METARU, Barkeeper-test-1; scratch probe of their 326 non-empty zone files): every file's highest record int id is `nextId` or
`nextId` - 1; every gap in its run of record ids is the number of a `-zoneX.Y-INGAME` building id of its list (38 of 38 files with gaps); in
Barkeeper-test-1 each such building's `-S23` state has its id's number + 1 (1314 of 1314); counters reach 378329 (bench-bases zone 20.32). **Observed**
(merged base-game level data, probe of the `0-buildinglist` records): 107 building-list
entries in 28 zones have `-zoneX.Y-INGAME` ids (one, `2-zone52.55-INGAME`, cleared); some lie above their zone's `nextId` (zone 14.56: `2459`,
`nextId` 1949, rebirth's 1940). Instance ids are unique only within one zone file: **Observed** (base load order, scratch probe over
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
- **Observed**: 1904 outdoor placements of the base load order have a `world Y pos` more than 0.05 off building ground + instance Y, many from rebirth.mod: the Cannibal
  Village (16.8, 17.10) and Cannibal Capital (18.7) about 25 lower, the 24 Spiked Cages of 16.8 18-28 lower. The original's baked navmesh on the six
  Cannibal Shacks of 16.8 lies 52-54 above building ground + instance Y and 72-78 above `world Y pos`.
- Meitou: `WorldLevelData.Load(install)` stands each outdoor level building at `TerrainHeightmap.BuildingGroundAt` + its height above, one inside or
  mounted on another of its zone at that one's height + its height above (`PlacementHeights`); the height above is a nonzero `player furniture Y offset`,
  else the instance Y. A child whose parent is in another zone keeps its state's height; one under -1000 is left out.

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

### Mods

Level data layers (**Verified**, decompile of 1.0.68: core list `FUN_14086ab20`, mod discovery `FUN_14086f7a0`, load `FUN_140870ae0`,
readers below): every game-data file the game loaded is one layer, in its load order, with a level folder:
- `gamedata.base`: `data/newland/leveldata/`; `Newwworld`, `Dialogue`, `rebirth` (a fixed list): `data/newland/leveldata/<name>/`. The
  base file's layer is listed even when it fails to load (the game reports it); a core `.mod` only when it loaded.
- A mod, after the core files in `mods.cfg` order: `<mod folder>/leveldata/`, the folder holding its `.mod` (`mods/<name>/`, or its
  Workshop item's folder), set when its `.mod` loaded ("[Mods] Loaded mod"); a mod that failed is not listed and adds no level data.
  The readers use no other folder for a mod.
- Readers walk the list front to back and read the exact file name in each folder: `leveldata.level` once at game-data load
  (`FUN_140870ae0`), `interiors.level` once (`FUN_14054c330`, a flag stops a second read), `zone.<x>.<y>.zone` each time the zone
  loads (`FUN_1409ffad0`), then the save's `zone/zone.<x>.<y>.zone` over them. All go through the game-data loader that reads `.mod`
  files (`FUN_1406c0b50`, `GameDataContainer::load`), so a later layer's record merges over an earlier one's by the FCS rules
  ([fcs-mod](fcs-mod.md)). The loader stores the layer's index on the records, references and instances it adds (the save's file
  passes -1 and stores none).

A mod's folder is flat: zone files, `leveldata.level`, `interiors.level` (**Observed**: all 179 such folders in a 1368-mod install are flat
and lower-case). Meitou (`WorldLevelData.Load`) builds the same list from the load order: the base folder, the core files' sub-folders,
then each mod's `leveldata` folder when it exists, leaving out a core `.mod` or mod that fails to load
([fcs-mod](fcs-mod.md#files-that-fail-to-load)), merged with
`GameDatabase.Apply`. **Verified** (`meitou-tools objects`, Workshop mod GuildQuests in the load order): its `leveldata/zone.20.33.zone`
places the Adventurers Guild, its sign and banners. A file the reader refuses is left out with a `LevelFileNotLoaded` issue (**Observed**: Great Frontiers - World
Overhaul's `interiors.level` has every zero byte turned to a space; the game plays with it enabled). **Observed** (that install, 1174
mods enabled): 54 473 placed buildings in 1222 zones against 11 714 in 913 without the mod layers.

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

**Verified** (decompiled, `FUN_14039a4d0` and `FUN_14039a820`): the game reads the magic, the count and the table once (another magic or a count
other than 4096: no map features at all), then each zone's block when the zone loads, by seeking to its offset (0: none). The size is never
read, so blocks may lie anywhere in the file, in any order, and bytes after a block are ignored. Each placement read is compared with the last entry of
the zone's feature list before its record is looked up: one at exactly the same position (`Vector3 ==`) is skipped. A record the data lacks is logged
("not found") and a placement whose position lies in another zone is logged ("Map feature in wrong zone") after its object is made; neither enters the
zone's list. **Observed** (a count over the install's base `features.dat` with Meitou's reader, the base data's records; the 11 below include the
2 of the 5 that also lie outside their zone, so 1819 less 14 is 1805): 2 placements repeat the one
before them exactly (a First Civ_WreckBit06 in zone 52.31 and a Barge WreckPiece02 in 46.33, each the same record, scale and rotation); of the
other 1819, 5 lie outside their zone (FloodedForest_Processor04, BlockyCubeCliff01, First Civ_Full-Wreck and 2 naming records the data lacks) and
11 name records the data lacks, so the game's zone lists hold 1805. Meitou (`MapFeatureFile.Read`) skips a placement at the position of the last
one it read in the zone that lies in its zone, keeps the 1819, and draws a placement lying outside its zone (engine choice).

## Town membership

**Observed** (building import `FUN_14057cc70`, position lookup `FUN_140928890`, `Town::vfunc_78` / `vfunc_84`; Meitou `TownBuildings.MemberOf`):
- The state's `town*` handle is kept only for a player town; for any other town it is dropped and the building is placed by position.
- By position: the nearest town whose radius holds it (X/Z), searching the 3 x 3 area sectors round it, nest markers left out. The radius is
  [the type table](../game/buildings-production.md#placement) (town 2500, village 937.5, ruins 875, POI 437.5, others 1250, x `town radius mult`); `size radius`
  is read only by the nest debris scatter (`FUN_140938440`). Meitou does not model the sector window.
- Else, a record with `interior` references and no parent building takes the nearest non-player town within 10000 (any type); else no town.
- Meitou with this rule (probe over the level data, 2026-10-20): Sho-Battai claims 169 buildings, The Hub 128, Squin 45, Stack 137, all inside each town's
  GAMESTATE_TOWN zones; 261 towns hold 9766 buildings. Residents: Squin 37 (save 36), Stack 94 (save 93-95).

## Nest debris and loot

A TOWN with `debris`, `debris building` or `loot spawn` lists lays them out when its zone is populated: the TOWN virtual +0x388 (`FUN_140938440`; reached by the vtable of
TownBase, Town, Nest and TownNull), which seeds `rand()` from the town's position xor `0xdeadbeef` (a layout fixed by the place). **Verified** (decompile):
- `debris` (NEST_ITEM link, v0..v1 the count, v2 the scale per cent, 0 = 1): `FUN_140937510`. An item **with a `physics file`** is a real item (3 records in the base game: Beak Thing
  Egg, Crab Egg, Gurgler Egg, each `trade item`, values 7000 / 400 / 400): for each of the count, `cluster min`..`cluster max` copies, each at the centre plus a random direction
  times 0..`cluster range`, on the ground; only when the flag byte +0xa8 of the object the zone's slot 25 returns allows it (Meitou: always). Any other (13 records:
  bones, dung, trash, carpets, awnings; `trade item` mostly set, value 0..25) is a mesh node from `ground mesh` (else `mesh`; with `number of meshes` above 1 a numbered
  variant) at a random yaw within `size radius` of the centre, plus cluster copies; the item's own `cluster` links then repeat this with their v0..v1 and v2. Meitou does not draw these.
- `loot spawn` (63 links in the base game, all VENDOR_LIST): v0..v1 times a point of the nest where the height is at least 1, an item rolled from the list as an interior layout's slot rolls
  ([loose items](../game/factions-squads-towns.md#loose-items-in-buildings), `FUN_1409540d0`), put there as a real item, only with the same flag. Meitou uses `size radius`.
- `debris building` (BUILDING link: Prisoner Pole, Camp Bed, Torch Post, Campfire, Sitting spot, Trophy ...): for each of `num centrepoints` a point round the centre
  (`FUN_140927600`), pushed away from earlier points, then per entry a count (v0; v0 = 0 gives one on a chance; above 1 it varies by +-0.5 x v0) of buildings
  at the point plus a random direction times v1..v2 (**Observed**: the data reads as a distance range), kept off earlier ones by a spacing, on valid ground, laid to the ground's
  slope up to the record's `max slope`, at a random yaw, made by the building factory (`FUN_140930120`, `FUN_14057cc70`) with their +0x241 flag set. **Not modelled**: a nest's
  Prisoner Poles (cages) are what the Fog Islands' fogmen carry their victims to ([#132](https://github.com/VlcekM/MeitouClient/issues/132)).
  - Standing on the home (**Observed**, decompile): a resident nest's spot names its home (`FUN_14057f1f0` hands the town the nest, the home's position and the home,
    `FUN_140929340`, list +0x500). Each piece's height comes from a ray cast straight down from 10000 above it (mask 0x204: the terrain and group 9, a building's
    ordinary floor-0 parts, [collision groups](collision.md#collision-groups-observed-fun_14055f6e0-and-the-loaders)); when it first hits that
    home, the piece is made as the home's child at the hit's height, tilted to the hit's normal up to its `max slope`, and goes into the home's
    interior objects (`FUN_14054f3d0`); else it stands on its own. **Observed** (1.0.65 through KenshiHarness, the Wanderer save at The Hub,
    2026-11-17): the Stationhouse ruin's campfire and its squatters stand on its floor-0 base at 1627, under the upper storey's collision at 1705,
    which the ray does not meet. Repairing a ruin removes none of them: its first build step restores it through `FUN_140557280` with 0, which
    empties the interior only when ruining, and the completion (`FUN_140562340`) destroys nothing. Buying the home destroys them with its interior
    (`FUN_14054f4d0` with 1 keeps only buildings whose slot 135 handle is set, `LightBuilding`'s; the original's save of a house bought there holds no lamps of its old interior, [save](save.md#zone-files)) and drops the town's nest spots naming the home
    (a handle compare) and its debris meshes round it ([purchase](../game/ui-screens.md#selected-building)); the home's pieces on the ground
    beside it stay. **Observed** (1.0.65 through KenshiHarness, the same save, 2026-11-17, bought paused with Re_Dev's money): the Stationhouse's
    campfire, stools and camp bed and the debris round it vanish at the purchase, its squatters stay. The building's bounding box there is the
    merge of its part entities' world boxes (`FUN_140549480`); a part placed for its collision only (`FUN_14055f6e0` mode 2) makes no entity.
- Persistence: the items are saved with the nest as their `persist` handle ([save](save.md)); the buildings are not saved.
- A resident template's `nest` (a TOWN link on SQUAD_TEMPLATE) has its `loot spawn` laid at the town that lists the template in `residents` (**Observed**, METARU: The Old
  Prison holds 16 loose items with the town's own `persist` handle, all from the lists of `nest armoury ruin`, the `nest` of its resident `ruin- tech loot`; the Infested Lab's 17
  match `nest spiders`, the `nest` of `Ancient Lab n.5 empty spiders`; neither TOWN has lists of its own). Its `debris building` and `debris` meshes stand round each home too (**Observed**, 1.0.65 through KenshiHarness, a Wanderer save at The
  Hub, 2026-11-15: the four `hub squatters` ruins, Bughouse, Old Storm House, Stationhouse and Snailhouse, hold `nest housecamp`'s campfire, stools, camp beds and trash
  after a load, and their squatters stand at the campfire or stools for three game hours in three runs). Base data (gamedata.base, Newwworld, Dialogue, rebirth): 47 templates name a `nest`, e.g.
  the Old Empire Watchtowers' `ruin- tech loot` (issue #347).
  - Place (**Observed**, three original saves, 12 towns, 403 items): each lies within the nest's `size radius` of a building housing the template, on its floor or the ground
    beside it; none round the town's centre unless a home stands there. One roll per home is Meitou's reading (the totals fit: 7 Hub homes x 0..7). The Hub: 19 to 22 items in the homes of `hub squatters` (`nest housecamp`, radius 60), 320 to
    1630 from the centre; The Old Prison's 16 within 349 of the Broken Watchtower (`nest armoury ruin`, 350). Measured against Meitou's boot housing; 5 items of 403 lie further.
Meitou (`PopulationSystem.StandCamp`, **Engine choices**: its own random stream, loot reach): the first load of a nest the save does not name lays the eggs and the loot as ground
items kept apart from the ground item limit; a nest loaded again, and one a save names, lay nothing more. A town stands its residents' nests round each home whose template
names one, as its own camp's (`PopulationSystem.ResidentNests`): the buildings and meshes every time it is made, the items once and only round a home whose zone the save has
no file of. A building of the player's gets none (housing passes it over, [town residents](../game/factions-squads-towns.md#town-residents), so nothing hands its nest to the town). The pieces that are chairs, dummies or workstations are that home's furniture for staying home (`NestBuildings.FurnitureOf`). A resident template no building of
the town houses (5 base sites: 4 one-building Lost Library placements, Police Barracks) lays its nest's items round the centre. A home's piece stands on the home when the
placement ray meets the home first (`PopulationSystem.HomeHit`): the home's highest group-9 surface over the spot (as a ruin or standing, `GroundShapes`) is not below the
terrain, and no other town building, finished player building (or one built past its visual threshold) or camp piece laid before has a group-9 surface above it there. It then
stands at that height, tilted to that surface's slope up to its `max slope` (`NestPiece.OnHome`). Buying a home whose nest stands takes the pieces on it, its furniture and
its camp's debris meshes within its bounding box's radius (`BuildingShape.BoundsRadius`: its drawn parts' render-mesh bounds, as it was when bought) of its position away
(`FixtureRegistry.ClearCamp`, `NestBuildings.ClearHome`); the pieces and meshes its nest laid beside it go when the town unloads (`PopulationSystem.FoldBoughtCamp`,
`NestBuildings.DropHomes`): the original's pieces are not saved, its purchase drops the home's nest spots and its housing passes a player's
building over. **Verified** headless on issue #857 (probe
`layout 20.32:4-Newwworld-INGAME buy <folder>`, The Hub's Stationhouse under `hub squatters`, bounds radius 347): its 9 pieces, all on its base at 1626 and tilted 0.8
degrees with it, and 68 meshes go with the purchase while the town's 35 other pieces stay, and a save written with it rebuilt lays none round it on load. With that house
rebuilt in the navmesh the top surface at its position is the upper storey, 1705 over its ground floor's 1627: a camp laid round it would take its height from that surface.

## Expression reference

- `data/newland/land/features.dat`: ` char[4] magic "MF01" int32 zoneCount 4096 (64 × 64) {uint32 offset, uint32 size}[zoneCount] offset/size of each zone's block; 0/0 = no features block: int32 count, count × uint8 length, char[length] stringId a MAP_FEATURES record float32 x, y, z absolute world position float32 sx, sy, sz scale float32 qw, qx, qy, qz rotation, w first `
- Choosing the parts: `x = x × 214013 + 2531011`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- `data/newland/land/fogfeatures.dat` (Observed, not decoded)
- `data/globalPathing.path` (Observed, not decoded)
- Legacy world: `data/leveldata/` (Observed)
