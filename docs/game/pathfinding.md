# Pathfinding and movement

**Sources.** Reverse engineering of `kenshi_x64.exe` (Ghidra dump `MeitouClient-re/ghidra/dump/kenshi-named`, RTTI, strings,
call graph), the FCS data (`fcs.def`, `fcs_enums.def`, probe tables for RACE, BUILDING, FACTION_CAMPAIGN), the shipped
`data/newland/land/navtiles/*.hkt` files (scanned with a script, not decompiled) and the game's own `Havok.log`. Function
addresses are image-relative VAs as in the Ghidra dump. Each claim is marked **Verified** (checked against files or
a consistent pair of functions), **Observed** (read from decompiled code, not run) or **Unknown**. The generator section also uses the Havok reflection tables (hkClassMember / hkClassEnum arrays) embedded in the executable, read with the probe `MeitouClient-re/probes/walk/hkmembers.js`, and the collision-file probe of [../formats/collision.md](../formats/collision.md). Catalog:
`MeitouClient-re/ghidra/catalog-pathfinding.tsv`. Related docs: [game-loop.md](game-loop.md) (AI thread, character
update budget, zone activation, unloaded platoons), [character-stats.md](character-stats.md) (speed stat S, encumbrance,
RACE fields), [../formats/terrain.md](../formats/terrain.md), [../formats/zones.md](../formats/zones.md), [ai.md](ai.md) and [ai-tasks.md](ai-tasks.md) (the `Task_Move` family that asks for paths),
[factions-squads-towns.md](factions-squads-towns.md) (squad spawning, `squad formation`, the area-sector grid), [ui-input.md](ui-input.md) (move orders, `cycle_run_speed`, the rebuild-navmesh key),
[buildings-production.md](buildings-production.md) (`max slope`, construction) and [../formats/save.md](../formats/save.md#platoon-files-squads-and-their-characters) (the saved `speed mode`).

## Overview

- Navigation uses **Havok AI 2014.2.0-r1** (hkai): a navmesh per zone, stored as prebuilt tiles and patched at runtime. The
  whole library is linked, but Kenshi calls only `hkaiWorld`, `hkaiNavMeshInstance`, `hkaiPathfindingUtil` (findPath,
  raycast, graph path), the generation utilities and the query mediator. **Verified** by a call-graph scan of every edge
  from Kenshi code into the Havok range 140ce0000..140ed0000: all callers lie in the navigation block 1402ec..1403e2, i.e.
  the `NavMesh` and `NavMeshGenerator` classes, the `Gates` functions (FUN_1402ec580 calls `findPath`; FUN_1402ec920 and
  FUN_1402ed460 use the graph search) and two small helpers (FUN_1402fc790, FUN_1403e20a0). No movement, AI or character code
  calls Havok directly, so **path following, steering and local avoidance are Kenshi's own code** (**Verified** by that
  absence of callers; that none of the called Havok functions is an `hkaiCharacter` / `hkaiBehavior` / avoidance-solver
  entry point was not checked function by function, **Observed**).
- Three classes do the work: `NavMesh` (manager, thread "NavMeshMain"), `NavMeshGenerator` (thread "NavMeshGenerator")
  and `CharMovement` (per character, runs in the AI update). A fourth, `Gates`, answers base-wall accessibility questions.

## Havok space and the NavMesh manager

- Havok space = Kenshi units x 0.1 (1 Kenshi unit = 1 dm). **Verified** by two independent places: the speed tooltip
  converts S with the factor 0.22369362 = dm/s to mph (FUN_140888fb0), and the manager multiplies every Kenshi position by 0.1
  when it moves it into Havok space (FUN_1403ae490, FUN_1403a6a00). All navmesh data, the wrapper's velocities and the 500/30
  unit search radii below are in Havok units unless stated.
- `NavMesh` init (FUN_1403ac5a0, run at the start of the manager thread): creates `Havok.log`, one `hkaiWorld`, the default
  `EdgeFilter`, the generator object (FUN_1403c8a20) and stores the zone size 4608.0 at +0x1d4 (**Observed**). It also stores
  a lower-bound pair of -147456 - 9 at +0x1cc/+0x1d0 (147456 = 32 zones x 4608). The area-sector population grid of [factions-squads-towns.md](factions-squads-towns.md#62-area-sectors-homeless--roaming-squads-and-the-biome-population) uses the same 9-unit offset (cell = trunc((coordinate + 147465) / 4608), **Observed** in both docs). A floating origin (+0x1d8, vec4) is shifted by
  FUN_1403abcc0 (calls FUN_140cefa10) so Havok coordinates stay small (**Observed**).
- Instance uid (**Verified** from `Havok.log` and the tile names): an exterior zone instance has uid `(Z << 8) | X`, e.g.
  the log line "Create zone instance 21,33 [2115]" belongs to `tile21.33.hkt` (X = 0x15 = 21, Z = 0x21 = 33), so exterior uids
  are below 0x10000. Interior instances have larger uids (the log shows "Adding navmesh instance 60855", "301807d6", "Job
  added: Interior [20754]"). This matches the filters, which treat uids >= 0x10000 as interiors (see Filters below). The
  composition of the interior uid is **Unknown**.
- Face key (**Observed**, FUN_1403a6a00): bits 22..31 = navmesh instance (section) index in the world, bits 0..21 = face index.

### Manager thread

- Thread "NavMeshMain" (slot 1 of the class, FUN_1403af0f0) loops FUN_1403ae490 and `Sleep(10)` when it did nothing
  (**Verified**: the loop reads exactly so). It owns the Havok world mutation: other threads post messages (**Observed**: the
  hkaiWorld calls come from FUN_1403ae490's callees; the `Gates` functions also call hkai graph/path functions, from a thread
  that was not determined).
- Messages (**Observed**, FUN_1403ae490 switch): 0 load zone (FUN_1403ae070, logs "Deferred load zone" if the generator is
  busy), 1 unload sector (FUN_1403ad300), 2 (FUN_1403ad740), 3 open a door and 4 close a door (both FUN_1403a6a00, the third
  argument is 1 for message 3 and 0 for message 4). Instances are added at most 32 per pass ("Adding navmesh instance", "N
  sections added") followed by a stitch job (FUN_1403a8b30, FUN_1403ab9e0 deletes).
- The zone request (FUN_1403ac040) carries the zone hash and the list of buildings (0x68-byte entries) in the zone.
  Interiors are handled by FUN_1403ad940.
- The game's own `Havok.log` shows the pattern (**Verified**, real log in the install): the 3x3 zones around the player are
  loaded at once (the nine uids 0x1f13..0x2115 form a 3x3 block), then "Create zone instance X,Z [uid]" for each as the player
  moves, stitching with neighbours, runtime generation lines "Navmesh generation took N ms" (33 lines: min 2.3 ms, median 38 ms,
  max 3.4 s), "Navmesh <uid> stitching took" (median 10 ms, max 0.5 s), runtime "Job added: Interior [uid]" lines, and door
  toggles of 0..13 polys (0 on the first close).

## Tile files (`navtiles/tile<X>.<Z>.hkt`)

- 3995 files in `data/newland/land/navtiles/` (plus `seeds.def`), X and Z in 0..63 (the 64x64 zone grid, 4608 units per zone,
  zone index = floor(coord / 4608) + 32). 101 grid cells have no file (**Verified**, directory listing checked against all
  4096 cells): 61 of the 64 cells of the Z = 63 row, `0.60`..`0.62` and `1.60`..`1.62`, and Z = 0 and 1 for X = 44..49 and
  53..63. A missing tile makes the manager queue a Full generation job (**Observed**). The path is built by FUN_1403a6370
  through the resource/mod "zone/" path so mods can override tiles (**Observed**; the log prints
  `./data/newland//land/navtiles/tile21.33.hkt`).
- Each file is a **Havok binary tagfile** (**Verified**): magic `1E0DB0CA CEFA11D0`, SDK string `hk_2014.2.0-r1`, root
  `hkRootLevelContainer`. Classes inside: `hkaiNavMesh` (faces of 16 bytes, edges of 20 bytes), `hkaiStreamingSet`
  (mesh, graph and volume connections), `hkaiDirectedGraphExplicitCost`, `hkaiStaticTreeNavMeshQueryMediator`,
  `hkcdStaticAabbTree`. Clean-room parsing needs a tagfile reader; the layout is Havok's, not Kenshi's.
- Named variants in order (**Verified**, scan of all tiles): `NavMesh`, `Graph`, `Mediator`, `Hash` (hex string), then for
  every interior in the zone `NavMesh`, `Graph`, `Mediator`, `Info` (string `<uidhex>;<x>;<y>;<z>` with the absolute
  world position) and `Hash`. Writer FUN_1403a6f40 (writes `./tmp.hkt`, logs "Saved navmesh for zone"); reader FUN_1403a8050
  (logs "Loaded N navmeshes").
- Tile scan (**Verified**, re-run with an independent probe over all files): 294 tiles hold interiors, 1249 interior
  `Info` strings in total, 527 MB in total, median tile 116,463 bytes, max 778,539. 1164 of 1249 interior `Info` positions lie
  inside their own tile's zone; the other 85 are building origins up to 572 units outside it.
- Tile coordinates are zone-local at scale 0.1: tile0.0 is a flat quad 0..460.8 at Y = 10 (water level 100 / 10)
  (**Verified**: the floats 10.0 and 460.8 occur in the 1814-byte file). Interior Info positions are absolute world units.
- **Hash** (**Observed**, FUN_1403a1cb0 over FUN_1403a1a80): a boost-style `hash_combine` fold, `h ^= v + 0x9e3779b9 + (h<<6) + (h>>2)`
  over 32-bit values, over the zone's buildings: per building the x and z positions cast to int64 XOR 0xdeadbeef, the four
  quaternion components, and one more step (combining the value 1) when a building attribute holds. Only buildings that pass
  a filter are hashed (a kind check against the value 0xb of the building's type object, an enabled flag at +0x19c, and
  one more predicate); the zone sub-object's int at +4 is folded in last, and a zone without that sub-object hashes to 0. An
  empty zone with a zero int therefore hashes to 0x9e3779b9, which is the `Hash` string in tile0.0 (**Verified**: the tile
  file contains `9e3779b9`). On load, a hash that differs from the expected one logs "Hash mismatch" and queues a generation
  job (below), so tiles are a cache that is regenerated when buildings differ (e.g. mods that add or move buildings).
- `data/newland/land/navtiles/seeds.def` (**Verified**, 55,020 bytes = 4585 records of float3, 12 bytes, no header; writer
  FUN_1403c3760 and reader FUN_1403c5050 agree and both use that path): seed points that tell the generator which regions
  are walkable (reachable from a seed; the generator's message "All regions are below the area threshold and too far from a
  seed point. Keeping the largest region." and "building interior has no seed point" back this, **Observed**). They are
  authored in the FCS ("Add seed point", FUN_1403c5220); Y = -99 when no height is given (**Observed**, constant 0xc2c60000).
  The generator constructor (FUN_1403c8a20) loads them.

## Generator (runtime and tool-time navmesh building)

- Thread "NavMeshGenerator" (FUN_1403ce430): sleeps 100 ms when idle, works only when the manager's queues are empty
  (**Verified**: the thread reads the manager's counters and only then calls the job dispatcher). Jobs are dispatched by
  FUN_1403ce170 on a type in `job+0x58 & 7` (**Observed**): 0 gather inputs (FUN_1403cbfa0, "Starting generation task") then
  stitch (FUN_1403cb840); 1 gather inputs, build the mesh (FUN_1403ca3d0, logs vertices/faces/edges and "Stitched N
  additional edges") then stitch; 2 interior (FUN_1403c7560, FUN_1403c9cb0) then stitch; 3 an interior with a given AABB:
  gather inputs and stitch with FUN_1403cbbf0; 4 stitch only (FUN_1403cb840 for exterior uids < 0xffff, FUN_1403cbbf0
  otherwise; FUN_1403cb280 stitches with completed tasks and existing or unloaded zones). Job adders: FUN_1403c6820 "Full",
  1403c6a20 "Partial", 1403c6fd0 "Interior", 1403c71c0 "Stitch", 1403c6b60; the log prints "Job added: ...". Which adder
  produces which numeric type is **Unknown**.
- **Inputs** (the gather pass FUN_1403cbfa0, **Observed** unless marked; collision files, placement and groups in
  [../formats/collision.md](../formats/collision.md)). The job carries a zone (or, for interiors, a building), an AABB in
  Kenshi units and an origin; everything is put into one `hkGeometry` (triangles with a material int each) relative to
  the origin and multiplied by 0.1. The job type (`job+0x58 & 7`) chooses the branch: 3 builds an interior, 0 and 1 an
  exterior box.
  - **Terrain** (FUN_1403c16c0): the zone's height grid (FUN_140a08a20) as an N × N vertex grid over the zone, two
    triangles per cell, material −1. The grid resolution N was not traced (**Unknown**).
  - **Water** (FUN_1403c0250): one quad over the zone at the water level (from the node DAT_142135c48, 100 in the base game,
    [../formats/terrain.md](../formats/terrain.md)), material 3, only when the water level is above the lowest terrain
    vertex of the zone.
  - **Building and foliage shapes** (FUN_1403c8660): every collision shape in the AABB whose PhysX group is in the mask
    0x809de40 = groups 6, 9, 10, 11, 12, 14, 15, 16, 19, 27 (**Verified**: mask bits counted). So floor 0..3 parts,
    stairs on floors 0 and 1, floor-0 furniture, unwalkable roofs, both kinds of foliage collision; not doors (5),
    interior hulls (13), upper stairs and furniture (17, 18, 20..22), passable / IGNORE / unfinished parts (23..26 and
    above). Per shape, with the owner found through the shape's handle (only handles of type BUILDING resolve):
    - no owner: group 14 (walkable foliage) → material −1; group 6 (other foliage collision) with an empty handle →
      material 0; anything else is skipped.
    - owner a building with `path mode` IGNORE (0): skipped (and dropped from the list).
    - `path mode` WALKABLE (3): material 1, except 0 for an `is unwalkable roof` shape (group 27), and 0 for shapes outside
      groups 9 and 19 when a building sub-object flag (+0x1f0 → +0x2c, meaning **Unknown**) is clear. When that flag is
      set and the building is neither a child nor a door, its upper-floor contents are added too: shapes in groups 17, 18,
      20, 21, 22 (mask 0x760000) through the interior collector below.
    - PROJECTED (1) and OBSTACLE (2): material 0. None of the nine functions that read `path mode` compares it with 1
      (**Verified** by reading each comparison), so **PROJECTED behaves exactly like OBSTACLE**; the tooltip's "2d
      projection" has no counterpart in the code.
    - other owners (not buildings, not doors, not the building's own interior parts) are skipped by flags of the owner
      (+0x1a0 door flag, +0x1f0 sub-object), not decoded further.
    - shapes become triangles as in [collision.md](../formats/collision.md#shape-to-triangles-observed-fun_1403c1cf0)
      (box, capsule as a 16-sided prism, convex hull, triangle mesh; planes and spheres give nothing).
  - **Carvers** (FUN_1403c4390, "N building carvers"): shapes of group 13 (the `interior mask` trigger hull) whose building
    has `BuildingClassType` 12 = BCTYPE_SHELL_WITH_INTERIOR (the class at +0x198, virtual slot 0x358; enum order from the
    FCS) and the +0x1f0 → +0x2c flag clear; each becomes a carver (FUN_1403c1ae0 builds the volume, FUN_140dd9e80 wraps it as an `hkaiCarver`
    with flags 0, i.e. without CARVER_ERODE_EDGES, the only flag). A carver removes the navmesh inside it, so the exterior navmesh has a hole where a house's interior is; the interior is built separately.
  - **Door painters** (FUN_1403c4520): door shapes (group 5) of door buildings, searched in the AABB grown by 20 units on
    every side (the 20 is a search margin, not a painter size); the door shape's hull, its pose shifted by 5 units along
    one local axis, becomes a convex painter volume with material 4 (open door), so faces under a door get face data 4.
    Doors are therefore **painted, not obstacles**: group 5 is absent from the shape mask, and material 4 is the face data
    the door toggle and the cost modifier use (**Verified** as a consistent pair: mask bits, painter material and the
    toggle's 4/5 test).
  - **Foliage cutters** (FUN_1403bfa10): every foliage instance with a FOLIAGE_MESH `navmesh cutter` radius r (168 of 748
    meshes, **Verified** count; 37 of them also have a `collision` file, and fcs.def says the radius is then unused, which
    was not traced) becomes an axis-aligned box carver, centre ± r horizontally and
    ± 100 units (10 Havok) vertically (FUN_1406cd7f0 collects them as (x, y, z, r)). Square, not round.
  - **Seeds** (`regionSeedPoints`, Havok space). Exterior jobs:
    - doors (exterior jobs of both types): for each building in the zone with a door whose outer marker point (door
      building +0x3bc) is inside the box and not below the water level, that point. `door navmesh axis` (FUN_14029cc90) picks the local axis of the door
      shape along which two marker points are set on either side of the door and dropped to the ground with a ray: the
      outer one is this exterior seed, the inner one (+0x3c8) the interior seed (10 BUILDING records use axis 1, the
      rest 0, **Verified** count).
    - job type 0 (whole zone): the zone's `seeds.def` points (bucketed per zone when loaded, FUN_1403c5050; 4,585 points
      in 563 zones, 31 with Y = −99, the rest at Y ≥ 100, **Verified**); each is first tested with a ray down from
      Y 9000 against floor-0/1 building parts (FUN_1403d4dd0, groups 9 and 10): a point with no height that hits a
      building is moved onto it; a point with a height that hits a building is dropped; others are kept as stored, so a Y = −99 point
      that hits nothing goes in at Y −9.9 Havok (what Havok makes of it is **Unknown**). A
      zone with no `seeds.def` points gets a 3 × 3 grid of ground seeds over its box instead. Then the midpoints of the
      open border edges of each already built neighbour zone (FUN_1403c9820, the four neighbours) so regions connect
      across zone borders; points of a global list (DAT_14212f470, not identified) inside the box; and linked wall
      sections (class 9 WALL with `link length` > 0): three seeds on top of WALKABLE walls (ray hits) and, for walls that
      leave the zone, three ground seeds clamped into the zone box.
    - job type 1: two ground seeds on each edge of the job's box (corner table DAT_1416ccd30).
    - ground seeds (FUN_1403d8ac0): terrain height at (x, z), raised to the water level, then a ray down against
      groups 2, 6, 9, 14, 19 (mask 0x84244); the seed is added only when the ray finds nothing (or a hit of one
      particular kind, **Unknown**).
    - interior jobs: the door's inner marker point, else the first node of the building with type 0 or 1; otherwise the
      log says "Warning: building interior has no seed point".
  - **Interiors** (FUN_1403c79b0, job type 3): shapes in the building's AABB with mask 0x87fbe00 (groups 9..13, 15..22,
    27) or 0x82600 (groups 9, 10, 13, 19) when the building is destroyed. The building's own shapes: the points of
    its group-13 shapes (the `interior mask` hull) become one convex volume with `isInverted` set (hkaiConvexVolume
    +0x50, **Verified** against the reflection table), added as a carver, so everything **outside** the hull is
    removed; its other shapes give material 1, or 0 for group 27. Shapes of other buildings that belong to it (furniture: its children or buildings inside it) give material
    1 for WALKABLE and 0 otherwise; IGNORE ones are skipped. Vertices closer than 0.05 (squared, Havok) are merged. A
    destroyed building adds the triangle meshes of its `destroyed boundary` XML to the geometry (fcs.def calls it the
    "Navmesh cutter for destroyed interior"; the material it gets was not traced). 23 of the 68 buildings with `interior` parts have collision on them; `interior terrain` is set on 3
    (**Verified** counts); what `interior terrain` ("building interior includes terrain for generating navmesh") changes
    in the interior pass was not traced (**Unknown**).
  - BUILDING `path mode` counts 92/84/304/123 for 0/1/2/3 (**Verified**, 603 records). Enum names (FCS `PathMode`, the
    editor's enum): 0 NAVMESH_IGNORE, 1 NAVMESH_PROJECTED, 2 NAVMESH_OBSTACLE (default), 3 NAVMESH_WALKABLE (**Verified**).
    Other fields: `destroyed navmesh` (6 buildings, walls; the "Wall termites" accessibility check FUN_1402ef1f0 reads it),
    `build threshold` (19 buildings: below it a part is in a +14 group and drops out of the shape mask), `is gateway`
    (13), `max slope` (placement only, [buildings-production.md](buildings-production.md)).
- **Generation settings** (FUN_1403c4a10 on an `hkaiNavMeshGenerationSettings`, 0x220 bytes, built by Havok's constructor
  FUN_140dda8a0). Offsets **Verified** against the Havok reflection tables in the executable (hkClassMember arrays: name,
  type and offset per member, read with a probe; class `hkaiNavMeshGenerationSettings` and its `EdgeMatchingParameters`,
  `RegionPruningSettings`, `hkaiNavMeshSimplificationUtils::Settings` and `OverrideSettings`); values re-read from the
  constants (**Verified**). Havok units (Kenshi units × 0.1):

  | Offset | Field | Kenshi | Havok default | Meaning / Recast counterpart |
  |---|---|---|---|---|
  | +0x10 | characterHeight | **1.8** (18 units) | 1.75 | clearance above a walkable face; `walkableHeight` |
  | +0x20 | up | (0, 1, 0) | (0, 0, 1) | Y up |
  | +0x34 | maxWalkableSlope | **40°** (0.6981 rad) | 60° | `walkableSlopeAngle` (per-material overrides below) |
  | +0x4c | edgeMatchingParams.maxStepHeight | **0.5** (5 units) | | step between faces that still connects; ~`walkableClimb` |
  | +0x50 | edgeMatchingParams.maxSeparation | **0.2** | | max gap between matched edges |
  | +0x68 | edgeMatchingParams.edgeTraversibilityHorizontalEpsilon | **0.02** | | |
  | +0x90 | regionPruningSettings.minRegionArea | **1e8** | | every region is "too small", so only seeded regions survive |
  | +0x94 | regionPruningSettings.minDistanceToSeedPoints | **0.4** (4 units) | | a region is kept if a seed is within 0.4 of it |
  | +0x98 | regionPruningSettings.borderPreservationTolerance | 0 | | |
  | +0xa0 | regionPruningSettings.regionSeedPoints | cleared, then filled per job | | the seeds above |
  | +0xd0 | boundsAabb | the job's box | | |
  | +0x118 | defaultConstructionProperties | 3 (default kept) | 3 | MATERIAL_WALKABLE_AND_CUTTING for unmapped materials (−1, 3, 4) |
  | +0x120 | materialMap | 0 → CUTTING (2); 1 → WALKABLE_AND_CUTTING (3); 2 → WALKABLE_AND_CUTTING (3) | empty | material 0 shapes cut holes and are never walkable; material 1 shapes are walkable |
  | +0x140 | weldInputVertices | true | true | (weldThreshold default 0.01) |
  | +0x148 | minCharacterWidth | **0.9** (9 units) | 0 | narrowest passage kept |
  | +0x14c | characterWidthUsage | 1 BLOCK_EDGES | 1 | narrow passages are marked blocked; the mesh is not shrunk (2 = SHRINK_NAV_MESH not used) |
  | +0x14d | enableSimplification | true | true | |
  | +0x150 | simplification.maxBorderSimplifyArea | 1.0 | | |
  | +0x16c | simplification.maxBorderHeightError | (0.1 in job type 1) | | |
  | +0x170 | simplification.maxBorderDistanceError | 1.0 | | ~`maxSimplificationError` |
  | +0x178 | simplification.useHeightPartitioning | false | | |
  | +0x17c | simplification.maxPartitionHeightError | 4.0 | | |
  | +0x190 | simplification.boundaryEdgeFilterThreshold | 0.1 | | |
  | +0x208 | overrideSettings | 4 entries, below | | per-material or per-volume overrides |

  Other defaults left unchanged: quantizationGridSize 1/128, triangleWinding CCW, degenerateWidthThreshold 0.005,
  convexThreshold 0.1, maxNumEdgesPerFace 255, edgeMatchingMetric ORDER_BY_DISTANCE, edgeConnectionIterations 2,
  fixupOverlappingTriangles true. `overrideSettings` (0xf0 each: volume, material, characterWidthUsage, maxWalkableSlope,
  edgeMatchingParams, simplificationSettings), all copies of the base settings with edgeMatchingParams.cosPlanarAlignmentAngle
  0.6 and maxBorderSimplifyArea, maxConcaveBorderSimplifyArea and maxLoopShrinkFraction set to 0:
  - materials 1 and 2 (walkable building shapes; no pass found produces 2): maxWalkableSlope **60°**,
    maxBorderDistanceError 0;
  - material 3 (water): maxWalkableSlope **90°**, maxBorderDistanceError 0.1;
  - material 4 (door faces): maxWalkableSlope 60°, maxBorderDistanceError 0.1.

  Per job (FUN_1403cbfa0): type 0 adds a fifth override with material −1 (in Havok's OverrideSettings presumably "any
  material", **Unknown**) for the volume of the zone box shrunk by 1.0 Havok (10 units) on every side: maxBorderSimplifyArea 0.1, maxBorderHeightError 0.1, maxBorderDistanceError 0.3,
  plus an extra-vertex setting of 0.0003 when the zone's world cell holds a particular object (+0x268 slot, probably a
  town; **Unknown**). Type 1 sets minCharacterWidth 0.1, maxBorderSimplifyArea 0.1, maxBorderHeightError 0.1,
  maxBorderDistanceError 0.02 and minDistanceToSeedPoints 0.1. The generation call is FUN_140e0bec0 (Havok).
- **What the settings mean for walkability** (**Observed**, from the Havok field semantics above):
  - Terrain is walkable up to 40°; walkable building shapes (WALKABLE path mode) and door faces up to 60°; water faces at
    any slope. Steeper faces are not part of the mesh.
  - OBSTACLE / PROJECTED shapes and unwalkable roofs (material 0, CUTTING) cut the terrain and nothing is walkable on them.
    Being "cutting", every walkable face under or inside them is removed, with no character-radius erosion: Havok cuts
    the exact footprint, and clearance comes from BLOCK_EDGES (passages narrower than 0.9 = 9 units are blocked) and from
    the path query's diameter (2 × footprint radius, FindPathInput below).
  - A face needs 1.8 (18 units) of free height above it (characterHeight), so low overhangs cut the mesh under them.
  - **Only seeded regions survive**: with minRegionArea 1e8 every connected region is below the area threshold and is
    discarded unless a seed point lies within 0.4 of it; if none qualifies the generator keeps the largest region (log
    "All regions are below the area threshold and too far from a seed point. Keeping the largest region."). Rooftops,
    enclosed yards and plateaus with no seed are therefore not walkable.
- Result: the navmesh instance is stored in a tile when saved from the FCS/tool path (FUN_1403a6f40). The shipped tiles are
  therefore generator output; at runtime only Partial, Interior and Stitch jobs normally run (Hash mismatch otherwise;
  **Observed**, and the real `Havok.log` shows runtime "Interior" jobs).

### Face data (per-face material, **Observed** except where marked)

| value | meaning |
|---|---|
| low nibble 1 | gate region marker used by `Gates` and `GateFilter` |
| 3 | water surface |
| 4 | open door (cost +5 in WaterCostModifier) |
| 5 | closed door (blocked by every filter) |

Face data is the generator's triangle material: 3 comes from the water quad and 4 from the door painters, so every door
is generated open and the toggle below closes it (**Observed**: painter material 4 and the toggle's 4/5 test).

Door toggling (FUN_1403a6a00, **Verified** by the loop plus its consistency with the filters and the cost modifier): for every
face found in the door's AABB it acts only on faces whose data is 4 or 5 and differs from the wanted state: "open" (message
3) turns closed 5 faces into 4 and "close" (message 4) turns open 4 faces into 5 (writes via FUN_140d0afd0/FUN_140d0b7c0, which
update the instance's face data and mark the face dirty; the written value itself is hidden in the decompile, but 5 is what
every filter blocks and 4 is the cost-modifier's open door). It then logs "Door state open at / closed at <pos+2.5 up> N polys
updated". FUN_1403a66c0 splits the door AABB across zone cells and calls FUN_1403c6a20 (queue a job) when it spans one zone.

## Path queries

- A request goes to the NavMesh thread through a priority queue (+0x1b8, sorted by a priority int at +0x2c); the request object
  is 0xa0 bytes; results are appended to a list at +0xf8 and delivered once per frame by FUN_1406624f0 (**Observed**). The
  Havok world is guarded by a lock word at +0x200 (**Observed**). A result with code -10 (field +0x90 of the result, kind
  0xb) is queued by the manager after it added instances and means "Navmesh changed - invalidating paths": the receiver
  (FUN_1406624f0, string present) invalidates every character's current path (FUN_14065e790 clears the wrapper's path and the
  failure counter at +0x368) (**Verified**: both ends read; strings in the dump). FUN_140785da0 triggers the check
  (game-loop.md).
- Steps in FUN_1403ae490 (**Observed**, constants re-read: 500.0 = 0x43fa0000 and 30.0 = 0x41f00000):
  1. Find the start face within 500 Havok units of the start point (FUN_1403a1df0, closest face over the mediator).
  2. Try a direct line: ray cast with the WaterEdgeFilter (FUN_1403aaa90, wrapper FUN_1403a5460). If it reaches the goal,
     no A* is needed (result 0).
  3. Otherwise find the goal face within about 30 units (FUN_1403a46b0, search radius = footprint radius + 0.05), check
     start-goal connectivity on the graph level (FUN_1403a5c40 using `hkaiPathfindingUtil::findGraphPath`, FUN_140ce55a0
     "LtFindGraphPath") and run FUN_1403aad30, which fills the `FindPathInput` and calls `findPath` (FUN_140ce65a0 "LtFindPath").
- FindPathInput (FUN_1403aad30, **Observed**): diameter = 2 x the request's footprint radius, max iterations 100000, edge
  filter = the default `EdgeFilter` (+0x288), cost modifier = `WaterCostModifier` only when the water factor differs from 1.0.
- Result codes (**Observed**): 0 success (also when the direct ray reaches the goal), 1 no start face, 2 no goal face, 3 goal
  face not connected, 4 path failed. The game logs "Path Error" and "[Movement] Edge path impossible" for the failures
  (strings in FUN_140661270).
- Filters and costs (vtables at FUN_1403b57e0..1403b5f30; function bodies re-read, **Observed**):
  - `EdgeFilter` blocks face data 5. `WaterEdgeFilter` blocks 3 and 5. `DoorHitFilter` blocks 5. `DoorAndWaterHitFilter` blocks 3
    and 5. `ExteriorFilter` blocks 3 and 5 and every instance whose uid is >= 0x10000 (so only exterior zone instances).
    `InteriorFilter::vfunc_4` admits only instances with uid < 0x10000 (its other slots are the door filter's; how the game
    uses that slot is **Unknown**). `TerrainOnlyFilter`, `GateFilter`, `HkStuckCallback` (FUN_1403a4e80, FUN_1403b8390) and
    `AccessibilityCallback` exist too.
  - `WaterCostModifier` (vfunc 6 at FUN_1403b57e0): face data 3 multiplies the four cost components by the water factor,
    face data 4 (open door) adds 5.0 to each. The factor is per character: **water factor f** = a + 1 for RACE
    `water avoidance` a >= 0, and 1 / (1 - a) for a < 0 (**Verified** across the RACE loader FUN_14042efa0 `Chars_RaceDataLoad`,
    which stores the transformed value at race +0x6c, and the nav wrapper init FUN_140661f90, which hands it on via
    FUN_140144040 to wrapper +0x74; the request builder FUN_140145cf0 copies it into the request (+0x34), FUN_1403ae490 passes
    it to FUN_1403aad30 which uses it as the modifier's four multipliers). The wrapper init multiplies it by 0.5 for
    characters whose faction object has a non-null +0x250 (the player's faction, **Observed**: the same field gates a "Demo
    limitation" message). Example: Greenlander a = 6 gives f = 7 (3.5 for the player's characters); a = -10 (raptors) gives 1/11.
  - Water is therefore not a hard obstacle for any character (the filter used is `EdgeFilter`, only the cost grows); the
    ray-cast shortcut uses `WaterEdgeFilter` so a direct line never crosses water.
- Helper queries: raycast FUN_1403a5460, findPath-like FUN_1403a56e0 (callers FUN_14060cdd0 and `CombatClassAI::vfunc_9`),
  nearest point FUN_1403a3b00 (caller `CharMovement::vfunc_18`), face lookup FUN_1403a2fa0, portal list FUN_1403a4fd0.

### Gates and base walls

- `Gates` (FUN_1402ec350 gate query, max 16 goals (**Observed**, a count check against 16); FUN_1402ec760; FUN_1402ec920 town
  floodfill and accessibility; FUN_1402ed460 floodfill; FUN_1402ef4a0 gate codes; FUN_1402ef1f0; FUN_1402f03e0) assigns region
  codes per face and tests that a town is reachable. Strings (**Verified**, in strings.tsv): " is inaccessible with marker at ",
  "Wall termites! - Your base became inaccessible.  You need at least 1 gate in your walls.", "A gate got lost somehow".
  The Havok.log also prints "Gates - FloodFill N took ... for K faces".

## Movement

### CharMovement update

- The AI side (**Observed**): movement orders are `Task_Move` objects ([ai-tasks.md](ai-tasks.md#tasktype-to-task-class)); their scheduling and the per-frame update budget are in [game-loop.md](game-loop.md#the-character-update-budget) and [ai.md](ai.md#the-per-character-decision-loop); the player's move command comes from the click handling in [ui-input.md](ui-input.md#7-mouse-and-selection).
- `CharMovement::vfunc_11` (FUN_14065ffa0) runs three modes selected at +0x378 (**Observed**): 0 path following through the
  navigation wrapper (FUN_140148ac0, which also builds the path requests via FUN_140145cf0), 1 direct steering (FUN_1402af1e0
  follow/seek, FUN_1402aefe0 for the other variant, avoid and cap, scaled by the combat movement multiplier), 2 other. It
  ends in `Movement_Speed`. The wrapper (400 bytes, ctor FUN_1401468a0) works in Havok metres (positions x 0.1). Ground height
  comes from FUN_14065f160, a PhysX ray 3000 units down. Swimmers' Y is forced to 90 in water (the 90.0 and 3000.0 constants
  re-read, **Observed**).
- `CharMovement::vfunc_18` (FUN_140661270) is "set destination" (**Observed**): resets the path and then, for distances over
  1000 units (squared distance > 1e6) and when no road path is active, first tries the **road network** (FUN_14065f940) and
  returns if a road route was accepted. Separately, after repeated navmesh failures (a counter at +0x368 above 16) it logs
  "Path Error (...): Edge target to high - trying road network" and switches to the road route; if a road path is already
  active it logs "Completely stuck!" and "[Movement] Edge path impossible". The road path is an A* over road edges
  (FUN_14045da60, log "Road path failed"); FUN_14065f940 accepts it only when the weighted road route is shorter than the
  straight line, the weight being the character's road preference clamped to 0.2..0.9 (CharMovement +0x100 = max(0, 1 -
  faction +0x1d4), set in FUN_140661f90), with a 20000-unit road search radius at each end (constants re-read).
- Nav wrapper init (FUN_140661f90): applies the water factor described above (x0.5 for the player's faction); the footprint
  radius is read from the race's `pathfind footprint radius` into CharMovement +0x364 (**Observed**).

### Speed (the stat S, see character-stats.md)

- S ([character-stats.md](character-stats.md#derived-values-pointers-only) has the same product) = (V - 11) x leg x hunger x replaced-leg x encumbrance x (CharStats+0x18) + 11; V = lerp(`speed min` skill, `speed max`
  skill, athletics x 0.01) x (0.5 + 0.5 x slot115) (existing doc; FUN_140883db0, FUN_140886460 builds the product and adds 11,
  floored at 11, **Observed**). Units are **decimetres per second**; displayed mph = S x 0.22369362 (FUN_140888fb0,
  **Verified**, constant read and consistent with 1 unit = 1 dm above).
- **Water state** w (FUN_1405c7fd0, **Observed**): 0 when dry or when Character +0x2f8 is non-zero; if that field is zero and
  a flag at +0x3d4 is set the function re-evaluates for the referenced object (+0x380, probably a carrier or mount). With the
  character at Y: if Y + 2 <= 100 then 3 when Y + slot114 x 15 <= 100 (deep), 2 when Y + slot114 x 6 <= 100 (waist), 1
  if the race `swims` flag is set (shallow), else 0. Slot 114 (vfunc 0x390) is 1.0 for humans and scales with size for animals (**Unknown**
  exact rule).
- **Per water state** (FUN_140886460, **Observed**): state 1 caps S at 45. Non-swimmers (RACE `swims` false, they walk the
  lake bottom) in state 2 or 3 get S = 2 x the race's walk speed. Swimmers: state 2 gives S = walk speed, state 3 gives the
  swim speed (FUN_140885eb0) = lerp(6, 25, swimSpeedMult x swimming stat x 0.02) x a size/leg term (x0.6 for each of two
  limb flags that is off) x limb factors x hunger and encumbrance factors, minimum 4, and 1.0 if both limb flags are off.
  Slot 109 (vfunc 0x368) == 1 forces S = 7; a body flag at +0x160 forces S = 5; a KO-like state sets 7 or 5.
- **Sneaking** (the check at the end of the product, before the +11 and the floor): with X = (V - 11) x factors, X becomes
  min(X, lerp(12, X, clamp((stealth term - 0.1) / 0.8))), then S = X + 11, floored at 11 (**Observed**). FUN_14088f9a0
  builds the stealth tooltip ("Crawling: +100%"). FUN_140895350 the athletics tooltip ("Run speed:", "Full speed bonus:"
  when S > 0.8 V). FUN_14088e780 shows "Max possible speed:" and "Total speed:".
- **Combat multiplier** `CharMovement +0x35c` = RACE `combat move speed mult`, applied in FUN_14065dba0 to the steering
  velocity, times 0.75, 0.5 or 0.4 for water state 1, 2 or 3 (**Observed**, constants re-read). It is read only there, so it
  applies always (**Observed**).
- **Speed mode** (CharMovement +0x20, saved as "speed mode"; `AbstractMovementBase::vfunc_21` FUN_140665150, **Observed**,
  constants re-read): mode 0 caps at the value in +0xc0, mode 1 caps at 55.0, modes 2 and 3 at 999.0 (no cap). Mode 3 follows
  the slowest member of the group (FUN_1407f5740, applied in `CharMovement::vfunc_11`). Player-faction characters default to
  mode 2 (**Unknown**: not re-checked). The hotkey `cycle_run_speed` has speed_1..speed_3 (strings, **Verified**; NUM6 by default, [ui-input.md](ui-input.md#4-action-table)).
  The FCS MoveSpeed data ints are 0..4 with 3 = NO_SPEED_CHANGE (**Verified**: it is the fcs.def default and by far the most
  common value, 846 of 959 SQUAD_TEMPLATE `force speed`; the Platoon constructor FUN_1407ee2d0 remaps 3 to 4, **Observed**);
  the other enum names are **Unknown**.
- **Effective cap** (**Observed**): the effective max speed handed to the wrapper in `CharMovement::vfunc_11` is
  min(+0xb4, +0xbc); +0xb4 is the animation's max speed (initial 70), +0xbc the mode cap (initial 45). In the follow
  steering (FUN_1402af1e0) the speed ramps up by 15 per second of AI dt to a cap that is the smaller of a field of the
  steering object and the character's stat value at CharStats +0x17c, 55 for a forced waypoint. Other caps in that function:
  18 when close to the target or turning, 16 when moving backwards. Arrival hysteresis thresholds 70 and 90, and 160 for
  mode 6 (**Observed**).
- **RACE fields** (**Verified**, probe table `probes/pathfinding/race_table.tsv` re-read with an independent probe):
  `swims`, `pathfind acceleration` (20 for humanoids, 4 Garru and bulls, 3 Leviathan), `pathfind footprint radius`
  (4 humans, 7 Garru, 40 Leviathan), `hull size` (5/17/5 humans), `water avoidance` (-10..10, human Greenlander 6),
  `combat move speed mult`.
- **Terrain slope**: no speed modifier was found (**Observed**, absence). Slope enters only through the navmesh (about 40 degrees
  walkable limit at generation time) and the building `max slope`. **Injuries** and encumbrance act through the stat S above
  (limb factors, character-stats.md); there is no separate path-time penalty.

### Local avoidance and formation (Kenshi's own code)

- Steering object at CharMovement +0x118 (FUN_1402ae1b0, 1402aefe0, 1402aed10, 1402af1e0). **Separation**
  (FUN_1402e9740 over the character's list of nearby characters, stopping after 26 contributions, FUN_1402e9420): the
  repulsion is a horizontal vector (Y zeroed) along self - other that is non-zero only while d < R, with magnitude proportional
  to 100 x (1 - d/R), R = neighbour radius + margin (the final scaling is hidden in the decompile, so the exact magnitude is
  **Observed** only up to that proportionality). FUN_1402e9530 sidesteps: when moving against the neighbour (or when forced)
  it replaces the vector with a perpendicular one of the same length; a constant 45.0 is used to build a quaternion in
  FUN_1402e9740 (unit unconfirmed). FUN_1402e93c0 returns the signed overlap -(1 - d/R) x 100 (**Observed**).
- **Formation**: FCS faction `squad formation` { RANDOM, CARAVAN, MILITARY } (fcs_enums.def, **Verified**); UI "Follow
  Formation" (FUN_140494ce0, FUN_1403eb710, FUN_1403ebf50). A small `Formation` class (28 bytes, vfuncs FUN_1402aea20,
  1402ae990, 1402ae5e0) with two instances per CharMovement at +0x310 and +0x318, created in FUN_1406628e0 (**Observed**). The
  slot assignment rules are **Unknown**.

## Off-screen and far characters

- Unloaded squads are `UnloadedPlatoon` objects and do not use navmeshes or CharMovement: see [game-loop.md](game-loop.md#factions-and-squads) and [ai.md](ai.md#unloaded-squads) (stand-in speeds
  25/55/60/90, FUN_1407ee790). Faction campaigns use FACTION_CAMPAIGN `travel speed loaded/unloaded` (read in FUN_1409d4770)
  as MoveSpeed ints 0..4 (**Verified**, re-counted: loaded/unloaded pairs 0/0 x8, 1/1 x18, 1/2 x16, 2/2 x4, 3/1 x1, 4/1 x109,
  4/2 x17, 4/4 x6). SQUAD_TEMPLATE `force speed` counts 0:3, 1:4, 2:10, 3:846, 4:96 (**Verified**, re-counted; the two
  UNIQUE_SQUAD_TEMPLATE records have 3).
- Loaded characters near the player use the full pipeline (character update budget in game-loop.md; `npc range` 3250, zone
  activation +-340 box).
- `data/globalPathing.path` exists in the install (1,225,412 bytes) but is never referenced by name in the executable
  (`grep -a -c globalPathing` = 0, **Observed**), so it is probably legacy; the long-distance route is the road network
  above.

## What our builder needs

Decision 3 of [../simulation.md](../simulation.md#owner-decisions) is our own Recast-style builder. To walk where Kenshi
walks it has to reproduce the generator's input and rules above; Recast's own parameters only approximate Havok's
(exact-geometry, not voxel) generator, so the mapping below is a recommendation (**Observed** semantics, values **Verified**
as in the settings table). Units: Havok = Kenshi × 0.1; the Recast values are in Kenshi units.

- **Geometry per zone** (box = the 4608-unit zone, plus a margin for border continuity):
  - terrain from the heightmap ([../formats/terrain.md](../formats/terrain.md)), walkable up to 40°. The game's own grid
    resolution N is **Unknown**; the full heightmap (18-unit spacing) is the safe choice.
  - water: a quad at Y 100 over the zone when any terrain is below it, its own area type (walkable at any slope; the
    path cost and the ray-cast filter treat it specially).
  - building and foliage collision from the PhysX XML files, placed and grouped as in
    [../formats/collision.md](../formats/collision.md), triangulated as the game does (box, 16-sided capsule prism,
    convex hull, triangle mesh; no planes or spheres). Include exactly the groups of mask 0x809de40; skip IGNORE
    buildings, passable parts, unfinished parts below `build threshold`, upper-floor stairs and furniture, doors and
    interior hulls.
  - per triangle, walkable or not: walkable when its owner is a WALKABLE building (not an unwalkable roof) or it is
    walkable foliage (`walkable`, group 14); everything else (OBSTACLE and PROJECTED alike, rocks, unwalkable roofs) is
    a non-walkable obstacle. Slope limit 60° for walkable building triangles, 40° for terrain and walkable foliage.
    Recast's slope filter is global, so mark walkable triangles ourselves (DotRecast lets us set the area per triangle
    before rasterising).
  - carvers, removing the mesh inside: the interior-mask hulls of SHELL_WITH_INTERIOR buildings (exterior), foliage
    `navmesh cutter` boxes (centre ± r, ± 100 vertically). Recast: mark the convex volume as unwalkable area.
  - door painters: the door shapes' hulls mark an area type "door" (face data 4), toggled to "closed door" (5) at run
    time; doors never block in the generator.
- **Parameters** (Recast, Kenshi units): agent height 18 (characterHeight 1.8); max climb 5 (maxStepHeight 0.5); max slope
  40° base, 60° on walkable building triangles; agent radius **0 or small**: Havok does not erode the mesh
  (BLOCK_EDGES, not SHRINK_NAV_MESH), it blocks passages narrower than 9 units (minCharacterWidth 0.9) and every path
  query passes the character's diameter. So build the mesh unshrunk and check portal widths against 2 × the footprint
  radius in our own A* (which we write anyway; Detour's single agent radius cannot serve a human (4), a Garru (7) and a
  Leviathan (40) from one mesh). Cell size and height are ours to choose (Havok has none): about 1 to 1.5 units
  horizontally keeps 9-unit passages.
- **Region pruning by seeds** (no Recast counterpart; a post pass on the polygon graph): keep only polygons connected to a
  region that lies within 4 units (0.4 Havok; how Havok measures the distance was not checked) of a seed point; when no seed qualifies keep the largest region.
  Seeds as listed above: `seeds.def` per zone (with the building-ray rule), else a 3 × 3 ground grid; neighbour-border
  midpoints; door outer markers; linked walls. Without this pass rooftops, closed yards and cliff tops become walkable.
- **Interiors**: a separate mesh per building with an `interior mask`: its own and its furniture's shapes, mask 0x87fbe00,
  everything outside the mask hull removed, seeded from the door's inner marker; joined to the exterior through the door
  faces (stitching, below).
- **Clearance at query time**: the footprint radius is the RACE `pathfind footprint radius` times a per-character size
  factor (slot 114 = virtual 0x390 of the object at CharMovement +0x3a8, 1.0 for humans, see Water state; FUN_14065dc30,
  **Observed**): humans 4, Garru 7, Leviathan
  40 units. FindPathInput gets 2 × the request radius as the diameter (above); whether the
  request converts it to Havok units was not traced (**Unknown**, though 0.9 ≥ 2 × 0.4 for humans fits).
- **Doors at run time**: open/close messages flip faces with data 4 and 5 inside the door's AABB (above); the mesh is not
  rebuilt. Gates (`is wall gate`, "a wall linked building with a door in it") are doors in this sense, and `Gates`
  ([Gates and base walls](#gates-and-base-walls)) flood-fills the finished mesh; nothing gate-specific is needed in the
  builder.
- **Rebuild triggers**: a zone's building hash (above) keys the cache; construction crossing `build threshold`, destroyed
  walls with `destroyed navmesh`, and player buildings queue Partial jobs (job type 1: a box patch seeded on its own
  border, minCharacterWidth 0.1, finer simplification) (**Observed** from the settings each type sets; which adder makes
  which type is still **Unknown**).
- **Zone borders**: neighbouring zones connect because seeds sit on the open border edges of already built neighbours
  and Havok stitches instances (stitch jobs). With Recast tiles of one zone each, border vertices match by construction;
  the seed rule still has to see across the border.

### Comparing with the shipped tiles (feasibility)

The 3,995 `navtiles/tile<X>.<Z>.hkt` are the original generator's output and the natural reference. Findings (probe
scripts `MeitouClient-re/probes/walk/tilescan*.js`, **Observed**):

- The trivial tile0.0 stores its four vertices as plain float32 triplets in zone-local Havok space ((0, 10, 0) ..
  (460.75, 10, 460.75); note 460.75, not 460.8, a low-precision float), so a value-pattern scan finds them.
- Real tiles do not: a scan of tile21.33 and tile30.30 for runs of zone-local float triplets (12- or 16-byte stride) finds
  nothing longer than a few dozen entries. The binary tagfile writer packs arrays in its own encoding, described by the
  type section at the start of the file (class and member names are visible there).
- So a comparison needs a clean-room reader for the 2014 binary tagfile (magic `1E0DB0CA CEFA11D0`): its type section,
  the object records and the array encodings, enough to read `hkaiNavMesh` `faces`, `edges`, `vertices` and
  `faceData`. That is a self-contained piece of work for a probe or a test-only helper (Havok's format, so not linked
  from Havok); it is not needed by the game.
- A useful test then: for a set of zones (open desert, a town with walls and gates, a cliff area, a lake shore), sample
  points on a grid in Havok space and compare "on the navmesh" (point within 0.5 vertically of a face) between our mesh
  and the tile both ways, reporting coverage in % per zone; and compare reachability: for random seed pairs, whether both
  meshes connect them. Exact face equality is not a goal (different algorithms).
- Without a reader, cheaper checks remain: the `Hash` strings (our hash of the zone's buildings must equal the tile's,
  checked against tile0.0's `9e3779b9`), the interior `Info` count and positions per tile, and file size as a rough
  complexity measure.

## Unknowns

- How the game uses `InteriorFilter` (its admit test is "uid < 0x10000", the same side the `ExteriorFilter` requires) and the
  composition of interior instance uids.
- Which numeric job types the five job adders create (the settings each type uses are now known: 0 whole zone, 1 box
  patch, 3 interior).
- The terrain grid resolution of the generator (FUN_140a08a20); the material of `destroyed boundary` triangles; the
  building flag at +0x1f0 → +0x2c that gates carvers and part of the WALKABLE rule; the global seed list DAT_14212f470;
  the ray-hit kind that still allows a ground seed; whether OverrideSettings material −1 means "any material".
- Whether the path request converts the footprint radius to Havok units.
- FCS MoveSpeed enum names other than NO_SPEED_CHANGE; steering rules for formation slots and the full avoidance parameters
  (the repulsion's absolute magnitude).
- Whether face data values other than 3, 4, 5 and the gate nibble exist; how building construction and destruction
  update faces (path mode switches at runtime).
- Havok tagfile field layouts for the face/edge arrays beyond sizes (16 / 20 bytes).
- Whether player-faction characters default to speed mode 2 and how the faction field +0x250 is defined; the +0x2f8 / +0x3d4
  fields of the water state function.

## Implementation outline

1. Build our own navmesh (decision 3) from terrain, collision files, carvers, door painters, water and seeds as in
   [What our builder needs](#what-our-builder-needs), cached per zone and keyed by the zone hash; the shipped tiles only
   as a test reference.
2. Keep per-zone navmesh instances at 4608-unit zones at scale 0.1, with a floating origin; load a 3x3 ring around the player.
   Exterior instance uid = (Z << 8) | X.
3. Implement A* over faces with a pluggable cost modifier (water multiplier f = a + 1 or 1/(1 - a) from RACE `water avoidance`,
   halved for the player's faction; open door +5) and filters (edge/water/door/exterior/interior) over face data 3/4/5; use a
   ray-cast shortcut first; use the road graph for destinations beyond 1000 units and after repeated failures.
4. Run queries on a dedicated thread with a priority queue and asynchronous results, and invalidate paths on navmesh change.
5. Implement CharMovement from S (decimetres/s) with the speed mode caps, water states, acceleration 15/s, and the own
   separation steering; do not port Havok's avoidance.
6. Drive off-screen squads with the UnloadedPlatoon stand-in speeds.

## Findings from our builder (`Meitou.Navigation`)

- **Observed** (The Hub): the gate buildings have an interior-mask part; carving it closes the gates, so it is skipped when the building is `is gateway`.
- **Observed**: the prune needs seeds inside the walls; The Hub's `seeds.def` points alone left the interior pruned until door-painter
  midpoints were added as seeds (our rule, not known to be the original's).
- **Observed**: with these rules a path from outside west to outside east passes the town through its gates and around buildings.
- **Unknown**: the flag at building shape offset +0x1f0 (assumed clear, so only groups 9 and 19 are walkable on WALKABLE buildings), and
  the exact `BCTYPE_SHELL_WITH_INTERIOR` test (approximated as interior mask present and not a gateway).
- **Observed** (The Hub, default world): the west wall has no collision between z 2626 and the gate at z 2780 because `Defensive Gate IV` is
  `destroyed` there; a path into the town exists even with every door closed. Gaps in walls come from the world data, not from the builder.
- **Observed**: the Hub's three door painters are a shack door, a door in a wall, and one that covers no walkable cell; none is a town gate.
  A door leaf is thin, so the painter has to be grown by about a cell to paint a closed band of cells.
