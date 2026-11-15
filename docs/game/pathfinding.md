# Pathfinding and movement

Kenshi NavMesh/Havok/CharMovement research, scoped binary/log/file evidence below. Runtime uses
our DotRecast builder (`Meitou.Simulation.Navigation`), not proprietary Havok SDK. [Collision](../formats/collision.md),
[terrain](../formats/terrain.md), [stats](character-stats.md), simulation.

## Space and tile layout

**Verified** Havok coordinates=Kenshi*0.1;1 game unit=dm, speed*mph factor0.22369362.
Zone4608,64² grid; exterior UID=(Z<<8)|X, interiors>=0x10000 with unknown composition.
**Observed** face key upper10 bits instance, lower22 face. Floating origin keeps Havok small.
NavMeshMain owns mutation/messages, sleeps10ms idle; generator sleeps100ms and dispatches queued
full/partial/interior/stitch jobs. The NavMeshMain thread (`FUN_1403af0f0`, **Verified**) steps `FUN_1403ae490` in a loop with the flush-to-zero bit set and calls Sleep(10) only when a step found nothing to do. Meitou's `NavMeshService` / `PathService` block on a `BlockingCollection` and wake at once: an engine choice (up to ~10 ms less latency), not copied. 3x3 active-zone loads and runtime stitching observed in logs.

**Verified** navtiles:3995 Havok tagfiles, magic1E0DB0CA CEFA11D0, SDK hk_2014.2.0-r1,
hkRootLevelContainer; hkaiNavMesh faces16bytes/edges20, graph/streaming/mediator/static-tree objects.
Named variants NavMesh,Graph,Mediator,Hash then interior groups with Info=`uidhex;x;y;z`.
294 tiles contain1249 interiors; Info positions are absolute world units, possibly outside tile.
**Observed** (tile20.32, Storm House `600815`): an interior group is NavMesh,Graph,Mediator,Info,Hash; its vertices are
0.1-scale offsets from the Info point, which lies below and outside the min corner of the building's interior hull (~55 in X/Z, 66 in Y).
Tile vertices zone-local at0.1 scale (tile0.0 waterY10, width460.8). Seeds.def is4585 float3,
55020 bytes/no header; Y=-99 unspecified height. Hash mismatch/missing tile triggers generation.
**Observed** hash_combine over building positions/rotation/filter fields, zone value last;
empty tile hash9e3779b9. Mods override through zone resource path.

## Geometry and queries

Original generator gathers terrain, waterY100 material3, collision-group mask0x809de40,
placed building/foliage/interior geometry, seeds and material/face flags. Relative-origin input
scales0.1. Walkability/slope, clearance, floors/doors and stitch transitions differ; do not infer
navigation geometry from render meshes alone. Source collision decoding: [collision](../formats/collision.md).
Runtime face/edge filters include doors/base walls, water-avoidance and interior restrictions.
Havok paths, local steering and unloaded-platoon movement are distinct code paths.

## Speed and orders

A speed order (standing orders 0-2 and 16, `FUN_1405caa50`) writes the mover's own mode (CharMovement `+0x20`) and Task_Move's update applies
that mode every frame (`FUN_1405c68a0`), so a change takes effect during the walk (**Verified**, decompile). Meitou: `Orders.SetRunSpeed` gives
the new mode to a walk under way in the old one (#691); a walk a task started in another mode keeps it (engine choice).

### Bodies in the way

Which character states block or push a living character's movement (path walking, the animation-driven root-motion step, the fight repulsion). Soft avoidance only: the nav agent holds a list of the
characters round its owner and tests each as a circle (its footprint radius plus the agent's); the same loop ends a walk short when another body covers the goal.

- **Verified** (decompile; chain read function by function): the agent `FUN_1401476f0` (called from the velocity setter `FUN_140147ca0`, and from `FUN_140147fd0` when the agent is in
  external-velocity mode 3, which `FUN_14065ffa0` sets for the animation-driven move and for the fight steering; ordinary path walking goes through `FUN_140146a60`, not read) walks the vector at its `+0x100` +0x68 / +0x70 (0x28-byte hands);
  `FUN_1401468a0` stores that owner object from `FUN_140661f90`, which takes it from Character vfunc `0x68` (`FUN_1405c8700`, the object `FUN_1402e9740` reads as the fight's flocking list). The
  list is filled by `FUN_14085a070` ([combat](combat.md), "Deciding"): every other character except one for which `FUN_1407d1440` holds (**ragdolled** by a WHOLE/CARRY_MODE request, or
  **carried** `+0x3d4`, [ragdolls](ragdolls.md#ragdollpart-and-requests)), in a **bed or cage** (`+0x2f8` != 0), or **sitting** (status `+0xb8`, `isSitting`), within the two radii + 40 horizontally and 20 up.
  Each entry is then re-tested by `FUN_1401476f0`: status `+0xb8` clear again, Character `+0x1a9` set (the character is near the camera or an active zone: a level-of-detail flag from `FUN_1405c9f60`) and not carried.

| State | In the avoidance list (blocks, pushes, covers a goal) | Meitou |
|---|---|---|
| Standing, walking, fighting | yes | yes |
| Crawling / staying low (not ragdolled) | yes (`FUN_1407d1440` is false) | yes |
| Knocked out or dead (ragdolled) | no | no: separation, contact, covered goal, fight steering (`MovementSystem`, `AnimationSystem.Relocate`, `CombatSteering.OutOfFlock`) |
| Carried | no | no |
| In a bed or cage | no | no for the fight steering; engine choice: still solid to a walker's contact (a bedded body is never moved) |
| Sitting (resting on a seat) | no | no for the fight steering; engine choice: a walker steers past a sitter (separation) but is not blocked |

### Walls

Every move of the nav agent, the animation-driven one (root motion, mode 3) included, is a circle swept against the walls round it.

- **Verified** (decompile): after the velocity is chosen, `FUN_140147fd0` moves the agent by velocity x dt through `FUN_1401445d0`, which sweeps a circle against a list of
  wall segments (0x20 bytes each, gathered by `FUN_1403a4fd0` within the agent radius `+0x70` + 6 Havok units, re-gathered when the target leaves 0.8 of that range) and returns
  the first contact's fraction and normal. Only segments the move closes on count (dot below -1e-5), and only those within 1 Havok unit (10) of the agent's height. The agent moves to
  the contact, the rest of the step is turned along the wall and swept again; with nothing gained it logs "stuck at". A step longer than 3 Havok units (30) is cut into several sweeps (their count: Observed).
- <a id="blocked-walkers"></a>**Verified** (decompile, `FUN_140147fd0`): the stuck branch only posts the red (`0xff0000`, `FUN_1406e1e00`) " stuck at <pos>" message and sweeps a scaled
  step again. In path mode (`+0x88` 2) the agent's polygon (`+0x14`, -1 off the mesh) is looked for among the path's, from the corner before the cursor to 4 past it; time
  outside them adds up (`+0x84`, the step's dt; 0 again on one of them) and past 5 s (`DAT_141685340`) state 1 turns 6, so the next step asks for the whole way again from where
  it stands (`FUN_140145cf0`). Nothing counts these asks; a walker blocked on its path's own polygons never asks.
- **Verified** (decompile): agent state `+0x90`: 4/5 asked (`FUN_140145cf0`), 1 following and 3 no path (the answer, `FUN_140144cd0`), 6 ask again, 0 none (`FUN_140144400`).
  CharMovement vslot `0x48` (`FUN_14065e800`) is false and `0x50` (`FUN_14065e830`) true only in state 3; `0x50` stays false during the destination setter's goal-offset retries
  (`FUN_140661270`: `+0x370` set, `+0x368` under 16; at the 17th a player-faction walker logs "Edge target to high - Completely stuck!", an NPC tries the road network).
  `Task_Move`'s check (vslot 5 `FUN_140332150`; rows 1, 29, 40, 67) ends the task only on `0x50` (with `notifyPathImpossible`) or on its chase addon (`+0x78`, `FUN_14033a800`,
  NPCs with a target only); `AITaskSystem::update` `FUN_14050d970` sets its replan flag `+0x26d` on `0x50` away from the target, and `FUN_14050d920` -> `FUN_14050d080` then deletes
  the order deque's front when it is the running task (**Observed**).
- **Verified** (disassembly of `FUN_140147fd0`): the sweep's radius is the agent's `+0x70` (passed in XMM3 to `FUN_1401445d0`; the gather range is `+0x70` + 6). `FUN_1401476f0`, the only body test in this
  step, yields a push-out direction and strength from characters overlapping the agent (nothing when none do); it does not cut the move short, and the original has no separate "end point must have a surface" test:
  the sweep alone keeps the agent on the mesh.
- Meitou: `IWalkability.Slide` (`NavmeshWalkability.Slide`) for root motion (`AnimationSystem.Relocate`) and the combat approach step (`CombatSystem`): the walls are the edges that lead
  nowhere (no neighbour or tile link, not the zone border, not into a closed door), within `SnapRange` (16) of the walker's height (engine choice: the mesh's own error on stairs);
  the radius is RACE `pathfind footprint radius`. Before, only the end point was tested, and with the 2-unit edge tolerance a step could cross a thin wall (Observed: on the Hub's
  navmesh about 3% of 1-unit random moves crossed a wall edge, none with the sweep).

### Water states

**Verified** (`FUN_1405c7fd0`, `FUN_140886460`, `FUN_140885eb0`; Meitou `Speed.WaterState/InWater/Swim`, `MovementSystem.WaterStateAt`):
- State of a character at height Y with body size g (`vtable 0x390`, 1 for humans): 0 when Y+2 is above 100 (the water level). Else 3 (deep) when 15g+Y <= 100,
  2 (waist) when 6g+Y <= 100, else 1 (shallow) only for a race with `swims` (race +0x79), otherwise 0. A carried character uses its carrier's.
- Top speed S replaces the run speed: state 1 caps it at 45; in 2/3 a race that does **not** swim (skeletons, fishmen, robots) walks the bottom at 2 x race `walk speed`; a swimmer's
  state-2 speed is its `walk speed`, its state-3 speed the swim speed.
- Swim speed = lerp(`swim speed mult` x swimming x 0.02, 6, 25) (`FUN_140015b63` is (c - b) x a + b: 6 + 19 x, not clamped; 25 at swimming 50, 44 at 100) x leg x hunger x limb-item (all four limbs, stat 23) x encumbrance factors x 0.6 per unusable arm, at least 4; 1 with no usable
  arm.
- Training: in state 3 swimming gains 5 XP (10 carrying) per second instead of athletics.
- Animation: category 4 (SWIMMING) records (`swim`, `swim carry`, `swim carry left`, `swim idle`) are the swimming clips; getting up in deep water plays `swim idle`.

### Water costs

**Engine choice** (not the original's, which charges water faces one flat factor, RACE `water avoidance` a + 1, or 1 / (1 − a) below 0, halved for the player
faction): the own builder bands terrain water triangles by their shallowest corner's depth (`Speed.WetStateOfDepth`, size 1) into areas shallow 2, waist 4, deep 5
(`NavArea`). A path request's `WaterCost` per state is max(avoidance factor, land top speed / `Speed.InWater` speed); a non-swimmer's shallows take its land speed. A human
swimming at 6 with top speed 40 pays 6.7 in deep water, at least the flat factor everywhere. `CoarseRoute` bands its cells by centre depth the same way, never below 1.
Bands are at size 1, so larger races (Garru, Leviathan) are charged as if they swam where they wade.

## Squads travelling together

Source: kenshi_x64.exe 1.0.68; names from KenshiLib 1ce4a16. Constants are **Verified** (bytes at the loads in the cited functions);
the control flow is read from decompiles (**Observed**). The leader is never slowed for its squad; cohesion comes from the followers
aiming ahead of it.

**Slots** (`Blackboard::buildFormation` `14026db30`). Each platoon's blackboard keeps a map character → slot offset (+0x268) and a
rebuild flag (+0x260). `getFormationPositionOffset` (`1402703f0`) rebuilds when the flag is set; a character missing from the map gets
(0,0,0) and sets the flag. `Character::checkPlayerOrderForProblems` sets it when a follow order targets a squad mate. The head is the
squad leader, or, when the leader is on task 31 (player Follow) behind a member of the same platoon, that member. Every member except
role SQUAD_SLAVE (`AITaskSystem` +0x25c, enum SQUAD_1 0, SQUAD_2 1, SQUAD_LEADER 2, SIGNALS 3, SLAVE 4) gets a record: radius
r = 1.5 × movement radius, animal flag, and the slaves whose `slaveOwner` is that member. Offsets are in a frame where the head
faces −X, so +X is behind. The layout is FACTION `squad formation` (`fcs_enums.def`: RANDOM 0, CARAVAN 1, MILITARY 2; the file
literally reads `CARAVAN. MILITARY`). Random draws are uniform (`rand()`).

- MILITARY: humans and animals are split; per row c = ceil(n / round(√n)) with n = max(2, humans). Blocks in order: all slaves,
  animals with r < 10, animals with r ≥ 10, humans. Each block is rows of c, centred across, column and row step 2R + 10
  (R = largest r in the block), short last row centred; the first block's first row at x = 25 + R, each next block's at its R + the
  previous block's last row + 20 (`FUN_140269960`).
- CARAVAN: a single file behind the head. Each entry sits at x = previous edge + gap + r (edge = x + r): the head's slaves, then each
  SQUAD_1 member (gap 10..20, also after the head) followed by its own slaves (gap 5..10). If any member is SQUAD_2, the other
  non-animals take a random x near the front (head r + 5..10 up to 20..40) and step out to one random side by 10..50 until they
  touch nobody; the other animals start diagonally beside column entry i (± the radius sum) and step 2..5 outward until free.
- RANDOM: everyone at angle 2πu, distance u × 10√n (n = records), then pushed out of earlier circles with a 5 gap (`FUN_140268b70`);
  slaves (k) the same within √(50k) around (front-most x + √(50k), 0); then up to ceil(n/5) non-animals from the end of the list
  move to x = front-most x + 25 + r as a rear guard.

## Own builder

### Generator (runtime and tool-time navmesh building)

  - **Water** (FUN_1403c0250): one quad over the zone at the water level (from the node DAT_142135c48, 100 in the base game,
    [../formats/terrain.md](../formats/terrain.md)), material 3, only when the water level is above the lowest terrain
    vertex of the zone.
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
  - **Interiors** (FUN_1403c79b0, job type 3): shapes in the building's AABB with mask 0x87fbe00 (groups 9..13, 15..22,
    27) or 0x82600 (groups 9, 10, 13, 19) when the building is destroyed. The building's own shapes: the points of
    its group-13 shapes (the `interior mask` hull) become one convex volume with `isInverted` set (hkaiConvexVolume
    +0x50, **Verified** against the reflection table), added as a carver, so everything **outside** the hull is
    removed; its other shapes give material 1, or 0 for group 27. Shapes of other buildings that belong to it (furniture: its children or buildings inside it) give material
    1 for WALKABLE and 0 otherwise; IGNORE ones are skipped. Vertices closer than 0.05 (squared, Havok) are merged. A
    destroyed building (virtual slot 0x330, below) adds the triangle meshes of its `destroyed boundary` XML to the geometry (fcs.def calls it the
    "Navmesh cutter for destroyed interior") with material 0, cutting (**Verified**: the triangle adder FUN_1403c0e80 writes its fifth argument
    as each triangle's material; the boundary call at 0x1403c84a8 passes 0). 23 of the 68 buildings with `interior` parts have collision on them; `interior terrain` is set on 3
    (**Verified** counts; Metal Warehouse, Ancient Factory, Ash Dome Ruin). The generator never reads `interior terrain` ("building interior includes
    terrain for generating navmesh", **Verified**: its only reader `FUN_14029e0c0` stores it at Building +0xc1, whose readers are character and ray code);
    the three are built as exterior because their first `interior` part is blank (above, building shapes; **Observed** tile 34.28: no interior group).
  - **Destroyed at run time** (**Verified**). Virtual slot 0x330 of `Building` (vtable 0x1416ea4a8) returns +0x1a1, the flag `Building::setDestroyed`
    (FUN_140557280) writes. Destroying removes the doors ("door in setDestroyed"; rebuilding makes them again from `doors`); with `destroyed navmesh`
    it also rebuilds the navmesh in the building's box, its max corner raised by 5 (FUN_1403a66c0); then a building with an interior object that is no gateway or door
    gets an Interior job (FUN_1403abf20 → 1403c6fd0) when its zone's navmesh is present (FUN_1403ac950). The interior hash (FUN_1403ab140) is the
    building's hash (FUN_1403a1a80: hash_combine `h ^= v + 0x9e3779b9 + (h << 6) + (h >> 2)` over world x and z, each truncated to an integer and
    xored with 0xdeadbeef, then the orientation's x, y, z, w truncated the same way, so 0 unless ±1; then 1 if a door or gateway is destroyed; no height,
    no record), then each item of the interior object's contents (FUN_140016185) by the same hash while the building's own `path mode` field is
    present, and +0x100 when destroyed (**Verified**: computed from our placements it equals the tile group's `Hash` for all 19 Hub ruins in 20.32 and
    14 unfurnished rebirth placements in 18.9, 24.18 and 43.49; furnished ones miss, our contents list or order differs); the
    zone hash (FUN_1403a1cb0) uses FUN_1403a1a80, so destroying a plain building leaves the exterior tile valid. At zone load FUN_1403ad940 looks up each
    building's interior hash among the tile's interior groups (`Hash`) and queues an Interior job ("No valid interior navmesh for ...") when none matches:
    a ruin whose tile was built intact gets its interior rebuilt as a ruin (the job above: groups 9, 10, 13, 19, the inverted hull, the boundary cutting,
    the seed rules) and stitched to the tile's exterior, still carved by its hull (the carver tests class and +0x2c; setDestroyed's interior calls
    FUN_14054f4d0 and FUN_140548580 do not write +0x2c).
  - **Stitch** (FUN_1403c5830, **Verified**; every stitch goes through it: FUN_1403cb280 "Stitching with completed task / existing zone / unloaded
    zone", interiors' FUN_1403cbbf0 for each zone their box overlaps): edges of two meshes are matched with their own `EdgeMatchingParameters`, not the
    generation settings': maxStepHeight **1.0** (10 units), maxSeparation **0.2** (2 units), maxOverhang 0.05, cosPlanarAlignmentAngle 0.9,
    minEdgeOverlap 0.02, up (0, 1, 0) (member offsets 0, 4, 8, 0x10, 0x18 from the reflection table); the stitch input's +0x38 is 3.0 (Havok default
    0.01; member not named). Doors play no part: an interior joins wherever its open edges meet the exterior's within these.
  - BUILDING `path mode` counts 92/84/304/123 for 0/1/2/3 (**Verified**, 603 records). Enum names (FCS `PathMode`, the
    editor's enum): 0 NAVMESH_IGNORE, 1 NAVMESH_PROJECTED, 2 NAVMESH_OBSTACLE (default), 3 NAVMESH_WALKABLE (**Verified**).
    Other fields: `destroyed navmesh` (6 buildings, walls; read by `Building::setDestroyed` `FUN_140557280`, which rebuilds the navmesh in the building's bounds when set,
    **Verified**, and by the dead "Wall termites" check FUN_1402ef1f0: [buildings-production](buildings-production.md#walls)), `allow animals` (10 true; `Building::allowAnimals`, reader not traced),
    `enforce ceiling` ([perception](perception.md#sight)), `inventory content name` (the title of its storage section in `BuildingContainerInventoryLayout::setupSections`, **Verified**),
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

### Face data (per-face material, **Observed** except where marked)

| value | meaning |
|---|---|
| low nibble 1 | gate region marker used by `Gates` and `GateFilter` |
| 3 | water surface |
| 4 | open door (cost +5 in WaterCostModifier) |
| 5 | closed door (blocked by every filter) |

### Meitou debug view

F11's `[ ] paths` checkbox (`DebugOverlays`) draws, over everything and still with F11 closed, the path of each selected or inspected character
(`CharacterSnapshot.Path`, from its feet): navmesh legs green, the provisional long-range part (`PathExact`) orange, the next waypoint white, the end red.

### Dropped orders

A player's move order dropped by the movement system is logged (`order` lines in `log.txt`, `MovementSystem.LogDropped`): no path found (first request or
asking again), with the position, goal and waypoint. One blocked past `ProvisionalBlockedLimit` / `BlockedRepathLimit` is kept ([walls](#blocked-walkers)) and logged
once when it starts holding (`MovementSystem.LogHeld`).

## Gate codes

The original labels the walkable ground by the gates (**Verified** structure, decompile of 1.0.68):
- **Gate states** (`FUN_1402eebb0`, `FUN_1402eb070`): a map keyed by the pair (smaller code | larger << 8); several gates between one pair keep the lowest
  state (**Verified**); routes through several gates are as bad as their worst gate, best route kept (**Observed**). Same code → 0, either -1 → -1.
  Gate state 1 open or broken, 2 closed, 3 locked (**Observed**). `FUN_1402ebdd0` finds the gate a route between two codes leaves by.
- **Readers in play**: the spawn-inside-walls repair of a squad's activation (`FUN_1404ffad0`, **Verified**, [factions](factions-squads-towns.md#town-residents)); DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS ([gates](locks.md#cutting-and-breaking-285--185-cut-286--186-break)), the run-away score
  ([combat](combat.md)). Wall termites reads the pass's town list but is disabled ([walls](buildings-production.md#walls)).

## Unknowns

- Whether the path request converts the footprint radius to Havok units.
- The full avoidance parameters (the repulsion's absolute magnitude); how movement caps a follower's desired speed by its speed mode.
- Whether face data values other than 3, 4, 5 and the gate nibble exist; how building construction and destruction
  update faces (path mode switches at runtime).

## Expression reference

- Tile files: `h ^= v + 0x9e3779b9 + (h<<6) + (h>>2)`

## Evidence index

Evidence scopes below apply only to their named sources/checks.
