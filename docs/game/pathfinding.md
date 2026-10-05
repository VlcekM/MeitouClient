# Pathfinding and movement

**Sources.** Reverse engineering of `kenshi_x64.exe` (Ghidra dump `MeitouClient-re/ghidra/dump/kenshi-named`, RTTI, strings,
call graph), the FCS data (`fcs.def`, `fcs_enums.def`, probe tables for RACE, BUILDING, FACTION_CAMPAIGN), the shipped
`data/newland/land/navtiles/*.hkt` files (scanned with a script, not decompiled) and the game's own `Havok.log`. Function
addresses are image-relative VAs as in the Ghidra dump. Each claim is marked **Verified** (checked against files or
a consistent pair of functions), **Observed** (read from decompiled code, not run) or **Unknown**. Catalog:
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
- **Inputs** to a generation job (**Observed**): terrain heights from the heightmap, building physics shapes by the FCS BUILDING
  `path mode`, building carvers, door painters, foliage mesh flags and a water quad.
  - BUILDING `path mode` ints: counts 92/84/304/123 for values 0/1/2/3 (**Verified**, probe over the base game's 603 BUILDING
    records). The names in the FCS tooltip (fcs.def) are IGNORE, PROJECTED, NAVMESH_OBSTACLE (the default, also the most common
    value 2) and WALKABLE; the number-to-name order follows that listing and sample names (value 0: signs, tents, farms; value 1:
    tables, barrels; value 3: stairs, walls, shacks), and value 3 is tested explicitly in the generator input pass
    (FUN_1403cbfa0) (**Observed**). Other BUILDING fields that matter: `destroyed navmesh`, `door navmesh axis`,
    `max slope`, build threshold (probe table `probes/pathfinding/bld.tsv`).
  - Buildings whose object kind is 0xc become carvers (FUN_1403c4390, "N building carvers"); doors become painter volumes with a
    20-unit margin (FUN_1403c4520, "door painters"). FOLIAGE_MESH fields `walkable` and `navmesh cutter` feed the same pass.
  - A water quad at the water-plane Y (FUN_1403c0250, node DAT_142135c48), triangle material 3.
- Generation settings (FUN_1403c4a10) are raw constants whose Havok field names are not decoded (**Unknown**): 1.8 at +0x10
  (very likely character height), 40 degrees as radians (0x3f32b8c3) at +0x34 (max walkable slope), then 0.5, 0.2, 0.02,
  0.1, 0.9, 1.0, 4.0, 1e8, 0.4 (values re-read **Observed**). The max slope of about 40 degrees is the only slope rule in movement (see Speed below).
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

## Unknowns

- How the game uses `InteriorFilter` (its admit test is "uid < 0x10000", the same side the `ExteriorFilter` requires) and the
  composition of interior instance uids.
- Which numeric job types the five job adders create, and the names of the generation settings (FUN_1403c4a10).
- FCS MoveSpeed enum names other than NO_SPEED_CHANGE; steering rules for formation slots and the full avoidance parameters
  (the repulsion's absolute magnitude).
- Whether face data values other than 3, 4, 5 and the gate nibble exist; how building construction and destruction
  update faces (path mode switches at runtime).
- Havok tagfile field layouts for the face/edge arrays beyond sizes (16 / 20 bytes).
- Whether player-faction characters default to speed mode 2 and how the faction field +0x250 is defined; the +0x2f8 / +0x3d4
  fields of the water state function.

## Implementation outline

1. Parse tagfiles read-only (clean-room) to get faces, edges, face data, mesh/graph/AABB tree; or build our own navmesh from
   terrain and buildings using the same path-mode rules and treat tiles as an optional cache keyed by the zone hash.
2. Keep per-zone navmesh instances at 4608-unit zones at scale 0.1, with a floating origin; load a 3x3 ring around the player.
   Exterior instance uid = (Z << 8) | X.
3. Implement A* over faces with a pluggable cost modifier (water multiplier f = a + 1 or 1/(1 - a) from RACE `water avoidance`,
   halved for the player's faction; open door +5) and filters (edge/water/door/exterior/interior) over face data 3/4/5; use a
   ray-cast shortcut first; use the road graph for destinations beyond 1000 units and after repeated failures.
4. Run queries on a dedicated thread with a priority queue and asynchronous results, and invalidate paths on navmesh change.
5. Implement CharMovement from S (decimetres/s) with the speed mode caps, water states, acceleration 15/s, and the own
   separation steering; do not port Havok's avoidance.
6. Drive off-screen squads with the UnloadedPlatoon stand-in speeds.
