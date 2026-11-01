# Pathfinding and movement

Kenshi NavMesh/Havok/CharMovement research, scoped binary/log/file evidence below. Runtime uses
our DotRecast builder (`Meitou.Navigation`), not proprietary Havok SDK. [Collision](../formats/collision.md),
[terrain](../formats/terrain.md), [stats](character-stats.md), [simulation](../simulation.md#navmesh).

## Space and tile layout

**Verified** Havok coordinates=Kenshi*0.1;1 game unit=dm, speed*mph factor0.22369362.
Zone4608,64² grid; exterior UID=(Z<<8)|X, interiors>=0x10000 with unknown composition.
**Observed** face key upper10 bits instance, lower22 face. Floating origin keeps Havok small.
NavMeshMain owns mutation/messages, sleeps10ms idle; generator sleeps100ms and dispatches queued
full/partial/interior/stitch jobs. 3x3 active-zone loads and runtime stitching observed in logs.

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

**Observed** S=max(11,(V-11)*leg*hunger*replacement*encumbrance*statScale+11),
V=lerp(speedMin,speedMax,athletics/100)*(0.5+0.5*slot115). After the water states `FUN_140886460` overrides S: 7 while
the posture is 1 (staying low), then 5 when medical `+0x160` (a leg down) is set (**Observed** literals; Meitou `Speed.Run`).
**Verified** (nav agent `FUN_140147ca0` / `FUN_140147fd0`, `FUN_140144440`): the agent's velocity moves toward the wanted one by at most
a × dt, a = RACE `pathfind acceleration` (Havok units, ×10 in world units; 24 for people, so about 0.2 s to full speed; 3 Murloc, 200 wasps),
lerp(age, 3a, a); it brakes before corners and its last point to sqrt(2 a d) and snaps onto the goal, arriving within 1 unit; a goal another
body covers ends the walk short. A re-issued order within 2 units of the last goal is ignored. Facing is instantaneous; the drawn node turns after it at a limited rate ([animation](../animation.md):
20 rad/s, 4 in the combat stance), animals `turn rate` × 0.1. Meitou: `MovementSystem.PathAcceleration`,
`FinalArrive`, `GoalOccupied`. A walker out of combat keeps the agent's velocity vector (previous tick's `Velocity`), moving it toward path direction x wanted speed by at most a x dt, so a turn costs the speed the change of direction takes (no cos cut, no turning on the spot); its facing is its velocity's direction (the path direction while it starts; engine reading: facing the path direction while the velocity lags makes it slide sideways through turns) and the drawn body follows it ([animation](../animation.md), drawn facing), and a corner the velocity has carried past counts as reached. Fighters and direct followers keep the older turn cut (engine choice). Not yet: corner braking (its exact condition is Unknown) and animal `turn rate`. A reached waypoint is passed in the same tick.
Combat/follow steering ramps15/s, with turning/backward/arrival caps. Combat-move-speed multiplier
and water factors 0.75/0.5/0.4 apply separately (`FUN_14065dba0`, by water state 1/2/3; Meitou scales the fight steering by the water factors, `MovementSystem`). No independent slope-speed term was found.

### Force speed

The squad template's `force speed` is copied onto its platoon (`+0xb0`, `FUN_1407ee2d0`; **Verified**; absent key taken as 4, **Unknown** whether the original's lookup gives 0): 3 (grouped, the value of 848 of 961 base templates) becomes 4 = no change; 0 walk, 1 jog (cap 55), 2 run (cap 999) are
used by 17 templates (mostly rebirth.mod: wolf, beak and landbat packs, borderguards). Two readers, both **Verified**: the speed selector `FUN_140342a30` of the patrol, go-somewhere, travel-to-town and go-home tasks replaces
the requested mode outright with it (after the AI package's mode and the carrier rule, before the slave rule `FUN_1405c8aa0` = 2 that forces run), and the move-task start `FUN_140332390` replaces only the package's mode, then
takes the larger of the result and an explicit task mode when both are not 4 (so it never lowers an explicit mode); the carrier rule raises walk to jog (mode 0 to 1) in both. Meitou (`MovementSystem`, `Squad.ForceSpeed`): the raise
every NPC member's walk or jog mode gets (the rule both readers share); lowering by the selector is not applied (**Unknown** which of the two a given walk uses in Meitou's single mode). The slave rule is not modelled here.

### Bodies in the way

Which character states block or push a living character's movement (path walking, the animation-driven root-motion step, the fight repulsion). Soft avoidance only: the nav agent holds a list of the
characters round its owner and tests each as a circle (its footprint radius plus the agent's); the same loop ends a walk short when another body covers the goal.

- **Verified** (decompile; chain read function by function): the agent `FUN_1401476f0` (called from the velocity setter `FUN_140147ca0`, and from `FUN_140147fd0` when the agent is in
  external-velocity mode 3, which `FUN_14065ffa0` sets for the animation-driven move and for the fight steering; ordinary path walking goes through `FUN_140146a60`, not read) walks the vector at its `+0x100` +0x68 / +0x70 (0x28-byte hands);
  `FUN_1401468a0` stores that owner object from `FUN_140661f90`, which takes it from Character vfunc `0x68` (`FUN_1405c8700`, the object `FUN_1402e9740` reads as the fight's flocking list). The
  list is filled by `FUN_14085a070` ([combat](combat.md), "Deciding"): every other character except one for which `FUN_1407d1440` holds (**ragdolled** by a WHOLE/CARRY_MODE request, or
  **carried** `+0x3d4`, [ragdolls](ragdolls.md#ragdollpart-and-requests)), in a **bed or cage** (`+0x2f8` != 0), or **sitting** (status `+0xb8`, `isSitting`), within the two radii + 40 horizontally and 20 up.
  Each entry is then re-tested by `FUN_1401476f0`: status `+0xb8` clear again, Character `+0x1a9` set (the character is near the camera or an active zone: a level-of-detail flag from `FUN_1405c9f60`) and not carried.
- **Observed**: a living character is a `PhysicsHullT` box ("hull size X/Y/Z" of the race, `FUN_140661a70` -> `FUN_1404ccb50`, in the physics manager's hull list), not a PhysX actor; a ragdoll's
  shapes are group 3/4, which skip groups 0 and 1 ([ragdolls](ragdolls.md#collision-groups)). Nothing found makes a body physically stop a walker, and the root-motion step goes through the same agent.
  Whether the hull is read by anything that blocks movement is **Unknown**.

| State | In the avoidance list (blocks, pushes, covers a goal) | Meitou |
|---|---|---|
| Standing, walking, fighting | yes | yes |
| Crawling / staying low (not ragdolled) | yes (`FUN_1407d1440` is false) | yes |
| Knocked out or dead (ragdolled) | no | no: root motion, separation, contact, covered goal, fight steering (`MovementSystem`, `AnimationSystem.Relocate`, `CombatSteering.OutOfFlock`) |
| Carried | no | no |
| In a bed or cage | no | no for root motion and the fight steering; engine choice: still solid to a walker's contact (a bedded body is never moved) |
| Sitting (resting on a seat) | no | no for root motion and the fight steering; engine choice: a walker steers past a sitter (separation) but is not blocked |
| Beyond the LOD range (`+0x1a9` clear) | no | not modelled (depends on the camera) |

Not modelled: a conscious ragdolled character (a stagger that ragdolls a limb or the whole body) is left out in the original; Meitou keeps it solid unless it is incapacitated.

### Walls

Every move of the nav agent, the animation-driven one (root motion, mode 3) included, is a circle swept against the walls round it.

- **Verified** (decompile): after the velocity is chosen, `FUN_140147fd0` moves the agent by velocity x dt through `FUN_1401445d0`, which sweeps a circle against a list of
  wall segments (0x20 bytes each, gathered by `FUN_1403a4fd0` within the agent radius `+0x70` + 6 Havok units, re-gathered when the target leaves 0.8 of that range) and returns
  the first contact's fraction and normal. Only segments the move closes on count (dot below -1e-5), and only those within 1 Havok unit (10) of the agent's height. The agent moves to
  the contact, the rest of the step is turned along the wall and swept again; with nothing gained it logs "stuck at". A step longer than 3 Havok units (30) is cut into several sweeps (their count: Observed).
- **Observed**: the segments are the navmesh's boundary edges (the swept radius is passed in a register the decompile drops; taken as the agent radius).
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

**Observed/engine choice:** the original's Y is the height it stands at, in water the seabed; Meitou keeps a character on the navmesh's flat water surface (Y about 100), so the depth is read from
the terrain under it while Y is within 2 of the water level (a deck above that is dry), and size is taken as 1. Only the drawn height follows the original's idea (`World.DrawnPosition`,
`Speed.WadingHeight`): the seabed, and for a swimmer never below 100 - 15g = 85, exactly where the deep state begins, so wading and swimming join without a jump. **Observed** (`male_skeleton`): the swim clips' `Bip01` sits
about 12 above the origin (`swim2` +2 over the 9.99 binding height), so a swimmer whose origin is on the surface floats; at 85 its pelvis is about 3 under the surface. A deck within 2 of the water level counts as water (engine limit).
**Unknown:** what `+0x2f8` (nonzero returns 0) and `+0x3d4` (carried) of the character are exactly; swim offset (`+0x68`) use; the HUD/AI `me swim` gating
([ai-tasks.md](ai-tasks.md)) and animals' swimming.

Disabled legs,
body states and stealth modify distinct branches. Movement modes0 walk,1 cap55,2/3 cap999;
mode3 follows group's slowest. The HUD sets them as character orders 2/1/0/0x10 → modes 0/1/2/3
(Walk/Jog/Run/Group; **Verified**, character vtable slot 0x308 FUN_1405caa50, [ui-screens.md](ui-screens.md)). "Group"
is the selection, not the squad: [speed groups](#squads-travelling-together). Meitou keys the group to the
selection given the order (`CharacterCold.SpeedGroup`); NPCs in mode3 take their squad's slowest. FCS force-speed3 means NO_SPEED_CHANGE and remaps to4, not runtime
mode3. FCS `MoveSpeed` (editor enum, **Verified**): WALK 0, JOG 1, RUN 2, GROUPED 3, NO_SPEED_CHANGE 4. Follow/avoidance/formation use character footprint and previous neighbour state.
Squad formations: [squads travelling together](#squads-travelling-together). Off-screen arrival remains unknown. Whether living characters block each other beyond the
separation steering is **Unknown**; sitting characters are never pushed (**Observed** in play) (Meitou adds solid body contact: [simulation](../simulation.md#movement-and-paths)).

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

**Following** (`Task_Follow` update `14033b350`, shared by 141 Task_FollowLeader, 31 and the other follow tasks). Task 141 follows
the slave owner if the character has one, else its squad leader. Formation mode applies when the target is the follower's slave
owner or in its platoon (`14033b160`); otherwise the aim is target + normalise(me − target) × D, D = 20..40 drawn when the task is made
(`14032ebd0`). Formation aim (`1403335c0`): heading H (task +0xa0) eases toward the leader's XZ direction (−X if zero) while the leader
moves, H += (dir − H) × 0.5 × dt (`GameLoop_DtScaled`), aim = leader position + H × leader speed + slot rotated from −X onto H, so
followers aim one second ahead of the leader. When the leader is inside a building the follower cannot enter, aim = A + (A − B) × 1.1 +
rotated slot × 0.2 (A, B: the door's two points, its leaf centre ± 15, [ai-tasks](ai-tasks.md) gate guards; 200 LEAVE_BUILDING walks to the same A + (A − B) × 1.1, so A is
the outer one, **Observed**: the aim is 33 past the doorway outside).

Each update, with d the XZ distance to the aim: under 1 the desired speed (movement +0xbc) is 0. While the leader moves it is
d × max(0.1, 0.5 + 0.5 × dot(leader direction, own direction)): a follower that lags runs to catch up, one facing away slows. With the
leader standing: under 20 → 0, 20-50 → 10-20, 50-200 → 20-30, 200-500 → 30-999 (each linear in d², clamped). Speed 0 stops. Under 50
it steers straight (movement mode 2 MOVE_DIRECTION, desired motion toward the aim, move limit √d); from 50 to 100 it keeps that only
while it moved more than its speed² since the last update; otherwise it paths to the aim. Followers copy an allied leader's sneaking.
Stacked floors are **Unknown**. Engine choice: a follower whose own floor under the leader is over 20 off the leader's height paths to
the leader on its floor, d plus the height gap (**Observed**: "guys b stuck" save, Dust Bandits squad under its boss on the Hub Storm
House roof storey, stood "Following leader" on the floors below).

**Speed groups** (Group speed, mode 3). `PlayerInterface::assignSpeedGroup` / `useSpeedGroup` give the selected characters one shared
`SpeedGroup`; nothing else creates one (**Observed**: no other callers), so NPC squads never use it. `SpeedGroup::getSpeed` (`1407f5740`),
once per frame: speed = the lowest max speed of members not down and not carried, centre = their mean position, direction = the sum
of their velocities. A member ahead of the centre (positive dot with the direction) gets speed × max(0.8, 1 − 0.001 × XZ distance to
the centre); others the plain group speed.

## Own builder

DotRecast: cells2,height1,tile48 cells,agent18/climb5,ground/water/door areas. Build asynchronously,
cache tiles and gate population on readiness. Query A* through portal clearance (radius per wall end; tile-border, seam
and area-change ends are open) and water/door costs. **Observed** (zone 18.39, `meitou-tools navmesh`): a Swamp Village
shack's stair straddles the tile line x−62208 and its foot came out as 6..8-unit portals, so a 2*radius rule kept radius-4
agents under the house; the west half's foot meets the walkboard in a near-miss T, joined as an inner seam.
A start polygon with no exit that clears the radius is left through its narrow exits (engine choice: the walker already stands
in it). **Observed** (zone 39.16, Stoat, scratch probe from ground-level points round its 25 doors): slivers beside door steps
(Y-House `16-` 35046..35064,−71590..−71582, 4..6 wide, one 6-unit wall-ended exit; Y-House `18-`; Old Storm House `20-`) held
8 of 1999 starts with no path at all, so a walker stopped or pushed there stayed stuck at the door; with the rule all 1999 path.
A walker off the mesh (engine choices): its path starts at its own position, then the nearest mesh point, which it walks to; if the nearest start polygon gives no path, or
one whose first 10 units it cannot stand along, up to 4 next-nearest starts are tried. Movement turns early for the next waypoint (within 5) only where it can stand along
the first 10 units of the straight line to it, else it walks to within 2 of the corner. A step blocked on the navmesh part asks for the path again from where the walker
stands, up to 3 times since its last waypoint or order (`BlockedRepathLimit`), before the path and a GoTo order are dropped. **Observed** (zone 22.10, save "stuck", scratch sim): a walker on the terrain at
Y392 beside the Old Storm House's (`6-rebirth-INGAME`) door stair snapped to the stair's corner (Recast corner vertices take the highest neighbouring cell, 408 over
ground at 395); that polygon's only exits were narrower than the radius, the next start's path was cut up the stair's side, and the walker stood still. With the three rules it enters in 3 s.
A goal that cannot be reached is replaced by the reachable point nearest it in 3D (engine choice). **Observed** (Hub north wall, zone 20.32, scratch probe, issue #59):
the walkway keeps corner pockets of 8..64 square units whose only exits are 3..8 wide between wall ends; nearest on the X/Z plane, a click there
sent the walker to the ground beside the wall, 115..138 below; in 3D all such goals end on the walkway within 10. Cache key and unload rules: [simulation](../simulation.md#navmesh). Original Havok tiles are
reference evidence, not a runtime dependency or proof of DotRecast polygon parity.
**Terrain** as the original: every second heightmap sample (36 units), slope 40° per triangle. The cell diagonal runs (i, j) to (i+1, j+1), in our grid's column/row order. **Observed** (`meitou-tools navmesh --compare`, zones 42.22, 20.32, 18.39): against the original tiles, the other diagonal left 7003 / 7052 / 5638 area units that only the original walks, this one 1991 / 4720 / 5387 (a per-parity checkerboard was worse in all three). The wrong diagonal cut the 40° steep-slope test into ragged one-triangle strips, with 2..7-wide portals between wall ends along slope bands and tile seams. **Observed** (zone 42.22, 200 goals within 700 of 49217,-42879, radius-4 `NavQuery.FindPath`): 72 reachable before, 162 after (radius 0: 178); starts 49700,-42500 and 49400,-43100 (150 goals each): 76 and 75 before, 140 and 102 after. The original tile edges carry no blocked flags (all `flags` 4, `edgeData` 0), and A* on the original mesh with the same clearance rule loses nothing at radius 4 (same goals reached at 0 and 4). The query's clearance rule is unchanged. The original's width limit is the build's `minCharacterWidth` 9 (table above); how its path request passes the footprint radius stays **Unknown**. **Observed** (zone 20.32, `meitou-tools navmesh`):
at 18 units each right triangle (legs 18) near 40° was walkable or not on its own, and 2-unit cells kept every one, so cliff fringes were saw teeth
and spikes; at 36 the outline matches `tile20.32.hkt`, terrain triangles drop 139k → 35k, polygons 15250 → 13602.

**Furniture** comes from building layouts (`BuildingLayouts`), none when destroyed. **Verified**
(Hub, `meitou-tools navmesh`): parts with collision 189→309.
**Stairs under floors** (engine choice): the builder connects two neighbouring cells over a shared free space (over the higher floor, under the lower
ceiling) of the agent height less the climb (13), with the per-cell headroom 18, as the original connects faces by step height and asks the clearance per
face. Recast's default, the full agent height for the shared space, drops a line of cells where a ramp climbing out from under a floor slab, a roof edge or a
leaning wall rises per cell by more than the margin, although each cell has the headroom on its own. **Observed** (`meitou-tools navmesh --interior`):
Sho-Battai's Longhouse `43.11:5565-Newwworld-INGAME` ramp under its upper floor, Black Scratch's bar `50.33:324-` and Squin's Watchtower
`18.35:29-rebirth-INGAME` (placed rolled 12°, as the original's tile `18.35` has it too, its walls leaning over the spiral ramp) keep their upper storeys
with the rule and lose them with Recast's default (issues #139, #142, #147). **Interiors are one tile** (engine choice): in the zone's 48-cell tiles a stair across a tile line inside a building comes out cut (Stack's Longhouse
`19.29:6-`; the two tiles' contours put their vertices at different heights along the ramp). **Interior coverage**: `meitou-tools navmesh --compare-interiors`
counts an original tile's interior face covered when a kept polygon within 12 of its centre's height lies over the centre or within 3 units of it
(`OriginalNavTiles.CoverInteriors`; the original's slivers along a mesh end sit a unit or two past ours). **Observed** over the centre only: 18.35 68→86 %,
19.29 62→68 %, 50.33 69→71 %, 47.16 77→81 %, 20.32 88→90 % with the two rules; within 3: 18.35 96.6 %, 19.29 80.6 %, 50.33 87.2 %, 47.16 93.7 %, 20.32 98.1 %,
43.11 90.2 %, 26.13 92.9 %, 48.45 99.4 % (`InteriorCoverageTests` holds each a point under). The rest is not lost storeys (**Observed**, `--compare-interiors`):
furniture: without it (`--own-only` in every interior) 99.3..99.8 % but 19.29 (89.5 %) and 47.16 (83.4 %: own-only also drops the buildings placed inside a hull). Per building the tile either has holes under our layout furniture
(Squin: 0..1 of 23..63 pieces per interior stand on an original face) or floor under it (World's End L-House `26.13:748-`: 34 of 34; Black Scratch
`50.33:324-`: 13 of 26), so those tiles were built with another or no interior layout (`--verbose` lists it). 19.29: its Bughouse `8-` and Longhouses `4-`,
`3324-` reach their upper storey by stairs in 19.28/20.29, so alone (the border rule keeps ground-level regions only) it is pruned; with its neighbours
(`--around 1`), as loaded in play, 19.29 is 91.2 %.
**Shells and open structures**: a building is a shell with an interior (the original's class 12; its mask carves the exterior, its inside is an interior
**Shells and open structures**: as the original (**Verified** rule, "Generator" below: class, `interior` list), a building is a shell with an interior (its
mask carves the exterior, its inside is an interior job) when its `interior` list is real, it is a plain building (class 12) and it has an interior mask
with collision (`ZoneGeometryGatherer.IsShellWithInterior`, `InteriorOf`). A building with a blank interior has all its WALKABLE shapes walkable and its
upper-floor contents (groups 17, 18, 20..22) gathered, unless it is a wall or wall gate; one without an `interior` list has all its WALKABLE shapes walkable
only. **Verified** against the original tiles (probe of every placed building with an `interior` list or a mask, base game: a tile interior group over the
building, and for 47.16 / 26.13 / 34.28 the exterior polygons inside its hull): every placement of a shell by this rule has an interior group (Ancient Lab
17/17, Ancient Citadel 6/7, Ash Dome 6/6, houses all but 1..3 per type), none of a blank-interior one (Ancient Factory, Metal Warehouse, Ash Dome Ruin,
Cannibal Shack, Swamp Platform Round; walls and gates only where a neighbour's group overlaps, under 4 %); the original's exterior has 0 polygons inside the Ancient Lab's hull at 47.16 and 499 inside the
Ancient Factory's at 34.28. `cavernous` (Building +0xc2, read only outside the generator), `interior terrain` and `is gateway` play no part. Heft's Ancient
Lab Ramp is an `is wall gate` (class 7) whose mask is the gate's trigger box; as a shell would carve its own ramp out of the exterior and cut the lab off (#140). The Ancient Lab sites (28.60, 42.22, 47.16, 41.35; reports #117, #129, #153) path from the street to every storey under this rule (**Observed**, `--path`).
**Observed**: Heft's Shield Bridge (47.16 `2134-Newwworld-INGAME`, WALKABLE, one `is stairs` part) is walkable only by this rule (#141). Its deck is where the
original's is (**Verified**, probe: of the original exterior's 211 polygons over the walkboard's footprint, 30 have their centre at local `y` 145.9, the
walkboard mesh's deck height, to 0.1; 142 lie on the terrain under the bridge, 1040..1180, the rest on the end ramps), and paths run along it at
`y` 1292..1305 as the original's deck polygons do.
**Observed** (zone 13.12, `--probe`): the Metal Warehouse `14-rebirth-INGAME` has a blank interior, so it is open; the ground of its aisle under the
roof is kept and the original's tile has it too (issue #157, filed before the shell rule).
**Interior parts**: the `interior` list (laid out like `parts` after the same position-seeded reseed, as drawn) is collision too,
none when destroyed; it holds interior stairs and upper floors. **Verified** (tile20.32 interior `600815`, the Hub Storm House
`4933-Newwworld-INGAME`): the original mesh ramps +4→+68 over (+30..+65, −45..−5) from the origin to the roof storey at +69..+72,
inside the bounds of `Storm_House_01_Interior-stairs` (`is stairs`, only in `interior`). **Observed** (scratch NavSystem probe):
without the list that storey and the Watchtower `2-Newwworld-INGAME` floors +130/+190/+290 were unkept; with it paths from the door seed reach them.
**Destroyed interiors**: a part with a `destroyed mesh` but no `destroyed collision` keeps its intact `xml collision` (**Verified**,
`FUN_14055f6e0`; with no `phs or mesh` its `xml collision` is cleared instead). The original rebuilds a ruin's interior at load or when it is destroyed
and stitches it to the tile's exterior (**Verified**, Generator, "Destroyed at run time"); Meitou builds it the same way. A ruin has no doors, so its seed
is a node, else the original's ray from above its origin onto its floor-0 part (`ZoneGeometryGatherer.OriginRaySeed`). **Observed** (original tiles
20.32, 18.35, 19.29, 20.29, built with these buildings destroyed; interior groups matched to ours by box): the original keeps a mesh on the ground floor
only (e.g. Storm House `32-Newwworld-INGAME` 6 faces at Y1571..1578, Bughouse `4937-` 32 faces at Y1608..1619). Engine choice: also keep every region
whose lowest point is within `SeedPruner.DestroyedFloorBand` (40) of the interior's lowest polygon (upper storeys are 60+ higher): our mesh can split a
floor the original keeps whole (Hub L-House `4645-`: the seed's region covers 20 of the tile's 24 faces, with the band 24).
**Destroyed boundary** (**Observed**, data): a ruin's walls (`Exterior_UPPER_Destroyed`) have no collision and its roof part, which holds the
walls' collision, is not laid out; its `destroyed boundary` is a ring of vertical faces (L-House `Storm_House_04_interior_destroyed_COL`: 40 faces,
local Y −47..86) round the floor, the doorway and the steps outside it; the original adds it as cutting triangles (Generator, interiors). Rasterised as
faces it only drew thin cuts, so the floor under the walls stayed meshed and walkers crossed the walls. Engine choice: the ring cut halfway up outlines
the floor; polygons whose centre is outside it are dropped from the interior (so neither the border closure nor a join keeps them).
**Ruin joins** (engine choice after the original's stitch, which joins open edges within 2 apart and 10 in height, Generator): every ruin's open edges
link to the exterior's along its hull within `NavInteriors.RuinHullJoinDistance` (5; both are cut on the hull, ours each up to a cell and the 1.3-cell
contour error off it). **Observed** (`--compare-interiors`, 24 ruins whose tiles were built intact, before: none reached): reached now 6 (Old Storm
House `9.56:12-`, Outpost s-I `14.5:6-`, Ancient Lab `18.15:130-`, Longhouse `18.35:23-`, Old Storm House `29.28:52-`, Outpost s-III `50.24:66-`:
their floor meets the exterior on the hull, our edges 2.6..4.3 apart, height 0.1..1); the other 18 stay unjoined: within 10 in height their nearest
exterior edge is 9.0..135 away or none (Outposts s-II/s-IV and Watchtowers: the boundary ring stands 9..11 inside the hull and the exterior ends ~4
outside it, `14.5:2-` probe: neither mesh at 2 outside the hull, both at 4), so the original's stitch cannot join them either (**Observed**: our build of
the original's inputs; its run-time mesh is not in the tiles). Hub: all 19 ruins have a boundary; floor points inside each outline are reached from the
street (L-House `4645-Newwworld-INGAME` 39/39), as walkers enter it in the original (user report).
**Doors**: every part of a `doors`-list building is group 5. **Observed** (35 door records): only
`Defensive Gate level 4/5 Door` lack `is door`, yet open in game; without the rule Squin was sealed.
**Entrance seeds** (engine choice): cross the door's width axis. An exterior seed stands on the highest walkable surface at its marker at most
`DoorSeedRise` (5) above the door box's base (the original drops the marker with a ray), else on the terrain or water; an interior seed on the lowest
collision floor with agent-height headroom. **Observed** (Defensive Gate IV `2934-Walls2.mod`, zone 24.27, "this gate is fucked" save): its walkable
convex foundation spans Y 65..157 over ground at 138; seeds at the terrain height matched the ground inside the foundation (19 headroom) and kept it,
and walkers stepped down onto it at the slab's edge. `tile24.27.hkt` has only the slab and its ramps there.
**Solids** (engine choice): Recast rasterises surfaces, so ground inside a closed solid survives as floor. Every convex shape (box, capsule, hull), walkable
or obstacle, becomes a `NavSolid`; a walkable span whose floor, at its cell's middle, is inside every face plane (1 below the upward ones) is removed.
A prism over an obstacle's footprint would cut the ground under a leaning shape whose volume is in the air: **Observed** Shark's `Giant_BigWood`, one
tilted box 2,000 units tall, as a prism cuts a 1,500-unit hexagon over a hemp farm that `tile21.42.hkt` covers (issue #155), as a solid leaves it; 21.42's
points the original covers and we do not fall 6.9 → 1.8 %, ours-only 3.0 → 3.6 % (18.35: 5.1 → 6.1 %, a hillside by its Wind Generator II that joins the
town as a solid; the original has no mesh there). The height surfaces (`NavSurfaces`) drop the same insides: **Observed** (that gate) its stair is a tilted box
whose top face runs on inside the foundation, from 151 down to ~124 under the slab, and a walker's height followed it under the floor. **Observed** (`meitou-tools navmesh`, zones 24.27, 20.32, 21.32, 18.35, 19.29, 21.42, 21.31, 10.31, 18.39, 20.29, 23.22):
unpruned polygons drop 1..24 %, kept area sampled against the original tiles changes under 0.3 %; the gate's under-floor layer is gone; interior area
drops only in ruins (Hub Stationhouse `4-`, Bughouse `4937-`: floor strips inside the thick walls and fallen slabs). The door and stair paths listed
in this section still resolve. **Observed** (zone 34.46, `--probe`): Mourn's Swamp Platforms (Large `1251-`, Round `115588-`, Small `8882-`) have a
solid body box from the deck down into the ground, and the original's tile has no ground under them, only the deck (issue #154: as the original).
**Underground** (engine choice): the terrain is solid below its surface, so an exterior walkable span more than the agent height (18) under the
heightmap at its cell's middle is removed. **Observed** (zone 24.27 by the Bad Teeth gate): a buried part's top face at 120 under ground at 159.
**Map features** (`features.dat`, MAP_FEATURES `collision`: rocks, cliff blocks, Rebirth's statue, gravel piles, trestle bridges) are gathered from the zone
and its eight neighbours, placed by their position, rotation and scale, walkable up to the terrain's 40°. **Verified** (`FUN_140399ac0`, the `MapFeature` collision creator, RTTI of its vtable): a
feature with a `collision` file gets one physics loader object (`FUN_1400f3ce0`, as building parts) at its position, rotation and scale, group
**14**, handle of the feature itself (type 69 MAP_FEATURES, `FUN_140594300`); the zone load (`FUN_140a0e570` → `FUN_14039c4a0`) creates it for
every feature of the zone, with no `hidden` test (only the render entity, `FUN_14039c7e0`, skips `hidden` outside the editor). So the gather
pass (`FUN_1403c8660`) takes features as owner-less group 14 (a non-BUILDING handle resolves no owner): material −1, the walkable-foliage path,
not the building one (groups 9, 10, 13, 19 and `path mode` play no part). **Observed** (`meitou-tools navmesh --probe`, zone 21.19): the
original's tile walks the top of `Huge_Stone_Emperor_Statue`'s foundation (y 755.8, terrain 735) and has no ground inside the gravel piles, also those placed
scaled (1.41/1.88); without features Meitou walked the terrain through both (issue #161). Their collision is a triangle mesh, mostly open at the bottom
(gravel piles: 24 open edges; Hub cliff blocks closed). Engine choice: a feature's triangle mesh is a `NavMeshSolid`; a walkable span is removed when the
nearest of its triangles above the span's floor (more than 1 over it, at the cell's middle) faces up, so the span sees its back; an overhang's underside faces
down and keeps the ground under it. `hidden` features keep their collision (above), as in Meitou. **Observed** (`--compare`,
before → after): ours-only area 21.19 19.1 → 10.7 %, 23.22 10.6 → 6.6 %, 34.46 3.7 → 2.6 %, 13.12 3.6 → 1.9 %, 21.32 5.8 → 4.8 %, 20.32 3.8 → 3.4 %;
original-only 20.32 1.6 → 0.6 %, 18.35 1.1 → 0.3 %, 21.19 1.4 → 0.3 %; 19.29, 21.42, 21.31, 10.31, 20.29 unchanged. **Observed**
(`--feature-tops`: samples every 8 units where a feature's top face is walkable and 12+ over the terrain, with a polygon within 8): the original walks
feature tops reached from the ground (statue 3,281 of 4,026; Gravel-Pile02 2,144 of 2,551 and 1,912 of 2,132), but on Mourn's `Vast_Land Tube 01` (34.46)
its 1,278 of 42,908 all lie where the defensive walls' walkway (y ~1,049, kept in its tile) coincides with the tube; Meitou kept 5,958, the flat band at
z 66,200..66,500 reached where the tube's 24° top rises out of the wall walkway (`Defensive Wall IV 34.46:20-`). Why the original does not join them is
**Unknown** (both walkable; Havok joins faces by edge matching, not by voxel steps). Engine choice: where a feature's upward surface runs inside a building's
solid, its surface within 2 cells (`FeatureSeamCells`) is removed, so the walkway and the feature are not neighbours; the tube then keeps 1,328 samples,
34.46's floors under floors 5,183 → 544 (510 before features), other zones within 0.1 %. Features are obstacles of the long routes too (below) and ragdolls land
on them ([simulation](../simulation.md)).
**Foliage meshes**: the same inside test applies to the triangle meshes of walkable foliage (FOLIAGE_MESH `walkable`), not to obstacle foliage (engine choice
from the tiles). **Observed** (`--compare`): in 18.39 (Swamp Village; 371 of 383 foliage shapes are meshes, open below) the water and ground under walkable
foliage domes (e.g. -60348,34444: water 100 under a face at 205) are ours only; with the test ours-only falls 6.6 → 5.0 %, floors under floors 395 → 286,
original-only unchanged; 21.42 within 0.1 %. Obstacle foliage meshes are canopies open below: in 18.35 (Squin) the test removed ground the tile keeps
(original-only 0.3 → 1.2 %, e.g. -60300,15908, terrain 421 under a face at 472), and closed obstacle meshes changed nothing in 18.39, 18.35, 21.42, 23.22.
**Compare** (`meitou-tools navmesh --compare x,z ...`): samples each zone every 8 units against `tileX.Y.hkt`: points on both, only ours, only the
original's, ours-only points in a building footprint, and points where ours has more layers than the original with one 10+ under our top (floors under
floors). Interiors are not read from the tiles, so our interior floors count as ours-only; the original's big polygons float up to ~20 over hollows, so
heights are not compared. **Observed** (the 11 zones above): ours-only 1.7..10.9 % and original-only 0.9..7.5 % of the original's area; the underground
rule takes extra-layer points from 221 to 65 (24.27) and 507 to 194 (20.32); what is left is mostly walls and walkways the original prunes.
**Interior seeds**: without a door seed the building's first shopkeeper or general node of its interior layout is the seed (the original's rule, below);
Meitou also uses it when the door seeds find no polygon (engine choice). **Observed** (`meitou-tools navmesh`): doorless Hive Commune Huts (10.31
`50-Newwworld-INGAME`, clipped at the zone border) kept nothing before and 75 of 102 polygons with it; Stack's Old Storm House `14-` (19.29), whose
door seed misses its floor, 0 → 73 of 113 (its door still has no join).
**Observed** (35 base door records and their box collision extents): `door navmesh axis` 0 selects exporter X width, 1 exporter Y width;
using that width as the crossing direction leaves Shark's Swamp House `321-Newwworld-INGAME` room unseeded.
Its platform has overlapping upward faces near Y122/133; the lower face lacks headroom. A zone21.42 route from
(-47010,133,48970) to (-46960,133,49005) crosses its doorway with the corrected markers/floor selection (`meitou-tools navmesh`).
**Door joins** (engine choice): an interior's door polygons link to the exterior's door polygons of the same door; when none face them,
to any kept exterior polygon facing their open edges. **Observed** (zone18.35): the L-House `13-rebirth-INGAME` and `15-rebirth-INGAME`
door leaves sit 5..14 inside the interior hull, so the hull-carved exterior got no door polygons and the rooms were sealed.
When neither joins, the interior polygons connected to the door polygons within 32 units of them join the exterior polygons their open
edges face: a raised sill reached by steps (~47..52°) that cross the hull, whose lower part is the exterior's (cut at the hull: Y351 for
`29-rebirth-INGAME`, door Y365..380; Y418 for `27-`, door Y430..441) and upper part the interior's, meeting on the hull's edge.
**Observed** (zone 18.35, scratch probe): both rooms reached from the street; no other door in 18.35, 19.29, 20.29, 20.32, 21.32, 21.42 needs it.
**Door painters** (engine choice): in an interior a door painter that does not reach `DoorHullDepth` (4) into the hull is swept toward the door's inner
marker, at most `DoorHullReach` (12), until it does (`ZoneGeometryGatherer.ReachIntoHull`; the original shifts its painters 5 along one local axis,
which way is **Unknown**, Generator below). **Observed** (`meitou-tools navmesh`): Heft's bar, the Outpost s-III `47.16:241-Newwworld-INGAME`, has its
leaf 2..3 outside the hull; its interior had no door polygons and no join, an island (issue #130), and now paths from the street reach its ramp and
upper floor. Polygon winding (`NavGeometry.SignedArea`) is summed in double from the first vertex: in float at world coordinates the products round by
more than a painter's area, so a painter's winding came out at random and its own door polygons fell 16..19 "outside" it, without a door, hence no join
(Sho-Battai Longhouses `43.11:12-`, `5563-`, `5565-`; Storm House Lagoon `48.45:9-`). Left unjoined in 18.35, 19.29, 20.32, 23.22, 26.13, 43.11,
47.16, 48.13, 48.45, 50.33 (`door ... joins nothing` in `--verbose`): the pitched Small Shack `48.13:12-rebirth-INGAME`, as in the original;
interiors over a zone border join in the zone holding the door. That shack stands sunk into the slope: at its door the floor is at 992..995 and the
terrain outside at 1004..1010 (`--probe`: terrain 1007, the base's outer face 989 under it), so the door line itself steps 12..15, not a misplaced
painter (it spans 981..1028) or a centre-to-centre test. **Verified** (`tile48.13.hkt`, the interior groups' `streamingSets`, format doc): the original
has the same two edges there (interior 992..995, exterior 1004..1010, 2..6 apart) and no connection: its group `150371` has no streaming set, while every
other interior in 18.35, 19.29, 20.32, 26.13, 43.11, 47.16, 48.45, 50.33 has one (paired edges mostly within 2 in height). **Observed**: the
original leaves Small Shack `48.13:8-` (`110371`) unconnected too; Meitou joins it.
**Join sweep**: `meitou-tools navmesh --compare-interiors x,z ... | towns` (`towns`: every zone holding a town placement) reports per original
interior group whether the original connects it (mesh connections in its streaming set) and whether ours, the interior whose hull holds most of its
faces, is reached from the street (over kept polygons, joins and links from the exterior's kept polygons once the zone's kept set is resolved alone or,
with `--around n`, with its neighbours; a polygon its own pruning kept must be reached; in its zone or a neighbour's); SEALED marks connected
in the original but not reached in ours. **Observed** (225 zones, 1090 groups, 1066 connected): 25 sealed (18 ruins, 7 intact), 6 reached in ours only; **Verified** (`InteriorJoinSweepTests`): none intact sealed in the interior coverage zones, 28.43, 37.35 and 54.59 (87 connected intact matched);
`--compare` 20.32, 18.35, 21.42 unchanged by the rules below. The ruins' original groups mostly hold the intact interior (14.5 Outposts: our ruins covered
13 % of their faces), so those tiles were built with the building standing; the original rebuilds them as ruins at load ("Ruin joins" above; coverage
rises in 14.5 13→40 %, 16.3 8→23 %, 18.45 0→89 %, 19.41 74→78 %, 53.26 62→74 %). The intact ones, by cause (**Observed**, `--interior`, `--probe`, the original's connection edges): unseeded doorless
interiors (Ancient Labs `28.43:2-`, `37.35:2-`, Ash Domes `54.59:2-`, `4-`: no door, no node, so nothing of theirs was kept and a join took only kept
polygons) are joined by the rule below (coverage 54.59 51→98 %, 28.43 0→48 %, 37.35 35→77 %); Hive Commune Huts `39.54:44..54-` stood in a village whose ground was pruned: 6 of its 9 `seeds.def`
points were dropped by the seed ray rule, which tested every building triangle, though their hits were roofs and domes 17..68 above them (the slope
outside the huts is 22..40° in the original's polygons and ours, not too steep); the rule now tests floor-0/1 parts (groups 9 and 10) only, as the
original (Generator, seeds), 4 are dropped, the zone keeps 6609 of 8122 polygons (778 before) and all six huts are reached; Storm House `27.22:1821-`'s steps (a 43.7° ramp to 524, continuous as in the original's faces 511..525) met its door landing on an edge
whose west end took the height of the raised cheek block beside the steps (525 against the ramp's 519), so the seam was not joined (rule below). Not fixed: seven whose tile interior differs from our build. **Verified** (interior hash, Generator; ours computed from our
placement equals the tile group's `Hash`): Old Storm House `18.9:13-`, Small Shack `27-`, Stephouse `29-`, Outpost s-I `24.18:8-`, Outpost s-II
`43.49:2-` match, so the original uses the shipped interior and its connections unchanged at run time (no rebuild, the stitch does not apply).
Why their tile meshes differ from ours stays **Unknown** (the hash has no height, record or real rotation, so it cannot tell): `18.9`'s groups are
small patches at 1620, ~20 above our floors at 1600 and without our upper storeys (`48.13:12-`'s group matches our floor at 993, hence no join there);
`24.18:8-` is placed rotated ~174° about a near-horizontal axis, origin y −56 under the water at 100, its door painting at −84..−62 inside the hull,
while the original's groups lie at 90..140 like our pruned floors and connect along the hull at 100; `43.49:2-`'s exterior polygon at 52305,82100
lies ~23 inside our hull. Old Storm House `22.11:2-` and Outpost s-II `24.18:15-` have furniture (24 and 74 pieces in our layouts), whose part of
the hash we do not reproduce (furnished houses that do match, `20.32:24-` and `4933-`, miss too), so whether they are rebuilt is **Unknown**.
**Observed**: `22.11:2-`'s group lies ~65 higher against our hull than the other houses' (Info − hull minimum y +22, others −24..−67; x/z as theirs),
i.e. its connected edge at 783 above our hull's top 773, our door at 696: a different height, which the hash would not notice; `24.18:15-`: the
original's exterior has the door landing at ~121 outside the hull, ours only a pruned sliver at 121..123 beside deep water. Were either rebuilt, the
original's stitch (10 in height, 2 apart, Generator) could not join our geometry either: their door steps are 12..17, as in `48.13:12-` (step 12..15),
which the original leaves unconnected.
**Unseeded interiors** (engine choice): an interior is joined whatever its own pruning kept, to exterior polygons the exterior's pruning kept, and
every region joined to a kept polygon is kept (`NavInteriors.CarryKeep`); exterior polygons stay limited to kept ones, else a door joined to a pruned
landing satisfied the door and skipped the doorway's kept ground (Storm House Lagoon `40.41:10-`, Metal Wallhouse `51.43:1281-Dialogue-INGAME`).
**Doorless interiors** (engine choice): an intact building with an interior mask and no `doors` (an open doorway) links its interior's open edges
to the exterior's that run along them within `NavInteriors.HullJoinDistance` (2.5; both are cut on the hull's line); ruins likewise ("Ruin joins").
**Observed** (zone 7.30, Hive Village, `meitou-tools navmesh`, issue #71): the Hive Commune Huts had no door join, so no path from the street
reached their floors; with it all five in 7.30 (and `48-` with 7.31 loaded) are reached and left through the hut's doorway; no join in 20.32, 21.32,
18.35, 19.29, 24.27, 9 in 10.31 (Hive Commune Huts). How the original joins them is **Unknown** (its interior jobs end in a stitch, below).
Joined edges may lie up to 6 apart (`NavInteriors.JoinDistance`), so an A* portal across a link takes the overlap with that gap. **Observed** (zone 21.31,
`meitou-tools navmesh`): the Rebel Base Storm House `3384-TwoStorey`'s exterior door edge slants 2.0..2.3 off the interior's (z −1156.3); with a
2.0 gap the portal was 0.2 wide and radius-4 agents never got in; with 6, (-50620,-1080) → (-50617,-1232) `--to-y 1080` walks in, and back out.
**Inner seams** (engine choice): Recast welds polygon vertices only within 2 height cells, and a step's corner height can come out
differently on each side, so a stair foot and the ground beside it can share an X/Z edge without being neighbours. The stitcher
joins open edges of one tile that coincide reversed and are within the climb at both ends. **Observed** (zone 21.42, `meitou-tools navmesh`):
Swamp House `48-Newwworld-INGAME`'s stair foot met the ground at Y124 vs 127 and kept only a 4-unit tile-border link, so radius-4
agents had no way up; with the seam, ground (-47000,125,48100) reaches the room (-47229,178,47969) (the dome `805-rebirth-INGAME`,
`6-` and `4-` were already reachable). Its deck (Y~178) was never pruned: the red there in `--png` is roofs, beams under the deck and interior fragments.
An edge pair also joins when the stretch of it within the climb is at least `SeamClimbCells` (2 cells) long, the other end being higher (a corner takes
its highest neighbouring cell's height; engine choice; Storm House `27.22:1821-`, above). Contour simplification also leaves near-miss T seams (one edge against two whose middle vertex is off its line). Open edges of one tile left
unjoined are linked (`Links`, not `Neighbours`) when they run opposite, share one end in X/Z (so floors either side of a thin wall stay apart),
lie within 1.5 of each other's line at both ends of the overlap, and are within climb + 0.5 there; A* portals for such links take the overlap
with the door joins' 6 gap when no facing edge is within 0.2. **Observed** (zone 18.39, `meitou-tools navmesh`): the Swamp Village stair foot's edge
(-62220,108,34088 → -62208,105,34086) against two edges meeting at (-62216,106,34086) joins both; the path (-62218,34080) → (-62216,34148)
`--to-y 140` climbs through that vertex (length 83) instead of detouring to the tile line x−62208 (97). **Observed** (after the terrain diagonal change, the same path from z 34080 exactly on a polygon edge): 153, the funnel from a start on its first portal's line swung out to that portal's end; `NavQuery.Pull` drops a first portal the start lies on, 82.
**Tile seams** (engine choice): tiles are rasterised with a 3-cell border from their neighbours (Recast `borderSize`), so contour
vertices on a tile's edge are kept and those edges come out as portals. Without it Recast treats the edge as a wall and simplifies across
it (error 1.3 cells): a strip one cell deep along the edge loses its edge vertex and the stitcher finds no edge on the line. **Observed**
(zone 19.29, scratch probe): the Longhouse `4-Newwworld-INGAME`'s outside ramp ends on the tile line z−13152 one row short of its
doorstep, which was cut off from the street and its door never joined; with the border the path from the street reaches the room.
Kept area grows by 0.04..1.1% (zones 20.32, 21.32, 20.29, 18.35) while polygons drop 20..45%.
**Zone borders** (engine choice): each zone is pruned by its own seeds and cached that way; when the loaded set changes, every loaded zone's kept set is
resolved again (`ZoneKeptClosure.Resolve`): its own, plus every region with an open edge on the zone's border line at ground level (edge middle within
`SeedPruner.BorderGroundSlack` 30 of the terrain, or the water height over it; roofs and walkways crossing the border stay pruned) toward a side whose
neighbour is not loaded, plus what the loaded neighbours' kept border edges reach (points on them seed the neighbour's full mesh, and back, until nothing
grows; interiors cut by a border need it). The original seeds a new zone from already built neighbours' border midpoints (order dependent) and ships every
tile built, so a region going on into the next zone is kept wherever its seed is; Meitou loads zones alone (only the loaded zones are active: the camera's
3 x 3 block and the 340 box of the player's characters, [corpses](character-stats.md#corpses)), so it cannot wait for the neighbours. **Observed** (scratch probe, original tile
polygons vs Meitou's unpruned polygons sampled every 25 units): with own seeds alone Meitou kept 3.1 % of the shared area in 14.26
(1 seed), 15.5 % in 25.13, 42 % in 26.14, 94..100 % elsewhere; with the border rule 98.9..99.9 % in all of 14.26, 13.26, 18.35, 19.29,
20.32, 21.32, 25.13, 26.14, while area kept where the original has none rises to 3..5 % of the zone. Once the neighbour is loaded the border rule no longer
applies on that side, so ground enclosed by a building straddling the line is kept only if either zone's seeds reach it: **Observed** (World's End, issue
#144) the terrain inside the Ancient Citadel's hollow concrete base (zones 26.13/26.12, 124 polygons, nothing in the original's tile) was such an island
and NPCs spawned there could not leave. A path that fails from a pruned polygon keeps the region the walker stands on (`NavMeshService.KeepAt`, kept while
the zone is loaded, not cached) and asks again.
**Observed** ("6 dudes" save): a character in 14.26, 2,300 from its borders, stood on a pruned polygon and could not path; the original
`tile14.26.hkt` has a polygon under it (y 702). **Observed** (World's End, zones 25..27 × 12..14, `meitou-tools navmesh --around 1`): with neighbour seeds
zone 26.14 kept 7,943 polygons in raster order and 3,790 in reverse; with the closure every order gives the same kept sets and paths.
An interior whose mask crosses a zone border is built in each zone it reaches, its box ending on the border line; a part without a
seed keeps nothing (no largest-region fallback) and is kept through the closure from the part with the door. **Observed** (zones
19.29/20.29, scratch NavSystem probe): the Y-House `2-Newwworld-INGAME` (x−55338, border x−55296) and Old Storm House `14-` have their
doors in 20.29; built only in 19.29 they had no door join and their 20.29 half was invisible to point queries. Now a path from the
street reaches the door and the far end of the Y-House. Doorless interiors on a border (20.32/21.32 `20-`, `26-Newwworld`) lose their
fallback island.
**Polygon heights** (engine choice): a polygon's plane can be ~25 off its surface (Recast polygons span ramps and terrain folds), so each tile samples
its walkable spans every 2 cells (`HeightGrid`, bilinear); a polygon keeps the grid only when a sample leaves its plane by more than 2, and one
whose samples all lie within 3 of the heightmap (or the water) keeps just a marker and reads the heightmap. **Observed** (headless probe, 20000 random
points against the nearest walkable collision or terrain): mesh heights more than 5 off went from 14.4 to 2.1 % (24.27), 11 to 0.7 % (20.32) and
9.3 to 0.5 % (18.35), at 1.5..3.3 MB of samples per zone.
**Heights**: **Observed** polygons are up to ~15 off over stairs/ramps (Hub gate), so standing
heights snap to walkable collision (`NavSurfaces`, also its edge within `EdgeReach`: the mesh overhangs raised floors, e.g. the Hub bar landing) or zone terrain (water-surface polygons keep their height); the layer is picked by mesh or real height within
`StandRise` of the character wins. A layer more than `StandDrop` (12) below the character is not stepped onto (engine choice: the
ground 22..28 under the Storm House floor, Observed, showed through furniture holes and walkers fell to it). A snap more than `StandRise` off the
character takes the collision surface at the character's own height instead when that is within `TerrainSnapRange` (30) of the mesh (engine choice;
**Observed** at the rock by the Hub's gate, -51686, 2740: our polygon spans its top (1595) and slope, sags to ~1580 and lies nearer a face inside the
rock (~1567), stranding a walker on the top; the original's cached tile has a polygon flat at 1594.8 there). A snap that took only a surface's
edge beside the spot, more than `StandRise` off a character within `StandRise` of the terrain, takes the terrain when the mesh is within `TerrainSnapRange`
of it (engine choice; **Observed**, zone 33.28, scratch probe: by the foot of a walkable top at ~560 west of Black Desert City, ground 537, polygons
at ~550, walkers at 7245, -13968 stopped and dropped their order); with no layer in range, a
polygon within `EdgeTolerance` counts. A character already off every layer near its height still steps down.
A path point takes the height of the corridor polygon under it; of stacked ones the corridor crosses, the one nearest the point's own height (`NavQuery.OnSurface`;
**Observed** with `meitou-tools navmesh --town Squin --path`: a route into a Bughouse and up onto its roof beds ended on the floor below, y 372 for 482).
**Long routes** (engine choice): goals in unloaded zones use `CoarseRoute` (192-unit terrain grid, slope and
water costs; a step whose centre line crosses a building's footprint hull costs `BuildingCost` 40×, so routes go round
walls and towns where open ground allows (a 5.5k-unit wall is walked round; 8× crossed it); the player's buildings are a second layer,
redrawn on the pool when one is added or removed; IGNORE `path mode`, gateways, destroyed buildings and WALKABLE ones that are features or
under the agent height, i.e. bridges, walkboards, pontoons, platforms, are left out; WALKABLE covers walls and houses too, **Verified**
against the placed records). Map features cost the same: a feature is no convex block (Mourn's tube arches over the town), so it is sampled every 48 units
(`BuildingFootprints.FeatureObstacles`), a sample solid where the navmesh's inside rule holds (the nearest face over the terrain faces up) and the top is more
than the agent height over it; each row's run of samples is one rectangle. **Observed** (`meitou-tools navmesh --route`, base game and the three mods: 7,255
building footprints, 17,085 feature rectangles, sampled in 1.2 s): across Rebirth west to east (-49500,-56080 → -45500,-56080) the samples of the route
inside a feature fell 25 → 3 of ~360 (length 5,817 → 5,594), north to south past the statue (-47500,-58500 → -47500,-53500) 89 → 0 (5,318 → 6,054); at the
Hub, past its cliff blocks (-52800,3000 → -51700,4800) 18 → 0 (3,764 → 3,775), (-53200,4500 → -51200,3400) 3 → 0. The navmesh part ends inside loaded zones, the rest is provisional and
re-asked; its zones within `Lookahead` load early. When meshes go live the host sends one `RepathCommand` with those zones at most every second (`RepathCoalesceSeconds`), and only GoTo walkers whose path still to walk crosses one of them (or whose answer is pending) ask again: re-asking every walker at each zone turned long walks about again and again (issue #50). A goal in a loaded zone with no navmesh path is a long route too when its zone is
in another block of side-by-side loaded zones than the walker's (two groups of walkers far apart); within one block, the navmesh's
nearest reachable point and a straight provisional tail (the terrain search, under one lock for every path thread, is not run there;
a way round through an unloaded zone within one block is not found). **Observed**: Hub→(-36000,-6000) and Hub→Squin arrive; "6 dudes"
save, scratch harness with the host's zone loading: Fog Islands (-80620,-24171) → the squad at (-31620,11420), 60.5k units, arrives
(17,506 ticks); before, it walked to the nearest reachable point and stopped on a cliff 10.6k short.
**Roads** (original, **Observed**): `CharMovement::vfunc_18` (`FUN_140661270`, set destination) tries the road route
(`FUN_14065f940`, the off-screen mover's [rule](factions-squads-towns.md#off-screen-world), its own gate 10000 units) for trips over 1000
units when no road path is active, with w from the character's faction `road preference` (CharMovement +0x100, set in `FUN_140661f90`);
after over 16 navmesh failures it falls back to the road route ("trying road network"). Nameless has 0.3, so player squads take roads too.
Ours: the long route asks `RoadNetwork.Route` first (`NavAgent.RoadPreference`, `CharacterCold.RoadPreference`, kept on recruit/dismiss);
when taken, the legs to and from the road are terrain-grid routes (engine choice) and the navmesh part reaches at most `RoadHandover`
(1500) along it, so bends are not cut. Not built: the road try for a trip over 1000 within one loaded block (Meitou: navmesh only) and the road fallback after 16 navmesh
failures (Meitou: the nearest reachable point and a straight tail). Both would send every such walker's request through `RoadNetwork.Route` on the path threads; left out until a
measurement shows the cost, since most in-town trips exceed 1000 units.
**Observed** (scratch harness, nothing loaded): Hub→Squin 17657 straight walks 25522 at 0.3 (99 % within 400 of a road; 43 % at 0);
Hub→Stack 34054; Squin→Admag at 0.8 walks 132184 for 26834 straight (short road `length` values, as off screen).

### Generator (runtime and tool-time navmesh building)

- Thread "NavMeshGenerator" (FUN_1403ce430): sleeps 100 ms when idle, works only when the manager's queues are empty
  (**Verified**: the thread reads the manager's counters and only then calls the job dispatcher). Jobs are dispatched by
  FUN_1403ce170 on a type in `job+0x58 & 7` (**Observed**): 0 gather inputs (FUN_1403cbfa0, "Starting generation task") then
  stitch (FUN_1403cb840); 1 gather inputs, build the mesh (FUN_1403ca3d0, logs vertices/faces/edges and "Stitched N
  additional edges") then stitch; 2 interior (FUN_1403c7560, FUN_1403c9cb0) then stitch; 3 an interior with a given AABB:
  gather inputs and stitch with FUN_1403cbbf0; 4 stitch only (FUN_1403cb840 for exterior uids < 0xffff, FUN_1403cbbf0
  otherwise; FUN_1403cb280 stitches with completed tasks and existing or unloaded zones). Job adders: FUN_1403c6820 "Full",
  1403c6a20 "Partial", 1403c6fd0 "Interior", 1403c71c0 "Stitch", 1403c6b60; the log prints "Job added: ...". "Interior" makes type 3
  (**Verified**: it writes 3 to `job+0x58`); the others' types are **Unknown**. The interior jobs of a zone (FUN_1403ab840, **Verified**) go to every
  building in it with an interior object (+0x1f0) whose +0x2c flag is clear, that is neither a wall (virtual slot 0x2c8, non-null only for `WallBuilding`)
  nor a gateway or door (slot 0x3d8, non-null only for `GatewayBuilding` and `DoorStuff`); the class is not checked there.
- **Inputs** (the gather pass FUN_1403cbfa0, **Observed** unless marked; collision files, placement and groups in
  [../formats/collision.md](../formats/collision.md)). The job carries a zone (or, for interiors, a building), an AABB in
  Kenshi units and an origin; everything is put into one `hkGeometry` (triangles with a material int each) relative to
  the origin and multiplied by 0.1. The job type (`job+0x58 & 7`) chooses the branch: 3 builds an interior, 0 and 1 an
  exterior box.
  - **Terrain** (FUN_1403c16c0): the zone's height grid (FUN_140a08a20) as an N × N vertex grid over the zone, two
    triangles per cell, material −1. FUN_140a08a20 was not traced, but **Observed** (`tile20.32.hkt`): the cliff outlines step on every second
    heightmap sample, a 36-unit grid (N 128) from the zone origin, cut diagonally from (i, j) to (i+1, j+1) (see **Terrain** under "Own builder"); of the tile's 8642
    mesh-end vertices 2739 lie on a 36-unit line and 2740 on an 18-unit one (chance ~1600), so the odd samples are not used.
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
      groups 9 and 19 when the building has an interior object with a real interior (+0x1f0 set, its +0x2c clear). When the
      interior is blank (+0x2c set) and the building is neither a wall nor a gateway or door, its upper-floor contents are
      added too: shapes in groups 17, 18, 20, 21, 22 (mask 0x760000) through the interior collector below. Without an
      interior object every WALKABLE shape is material 1 and no upper-floor contents are added (**Verified**, FUN_1403c8660).
    - **Roofs** (**Observed**, records and `tileX.Y.hkt`, scratch scan of every placement with a tile: interior groups within 60 of its
      origin, exterior polygons within 60; issue #143): Small Shack has no stair part and its roof collision (`Storm_Shack_Exterior`,
      `Storm_Shack_INTERIOR_ROOF_COL`) is `is unwalkable roof`, so material 0. In all 139 intact placements (61 zones) the original's
      interior group spans at most 25 units (the floor) and no exterior polygon lies on the roof, so the roof is never walked; Meitou
      neither (48.13 `8-`: `--path` from the street ends on the floor, y 969, roof 1024). Old Storm House reaches its roof storey by
      `Storm_House_01_Interior-stairs` (`is stairs`, `interior` list); 53 of 82 intact placements (45 zones) have interior faces 30+
      over the floor, the other 29 (all but one from rebirth.mod, e.g. 48.13 `6-`, 9 faces) floor only. Meitou reaches it
      (48.13 `6-`: street to y 1066).
      Stationhouse (issue #156): its one stair, `Storm_House_08_Interior-stairs` (`is stairs`), leads to the upper storey, which is the
      open roof terrace (`Storm_House_08_UPSTAIRS+ROOF_COL`, no collision over it, `--probe`); round it only the parapet ring, 8..16
      higher. The original's groups in the 4 intact placements with a tile (12.35, 20.28, 23.22, 24.22) have faces at the floor and
      +75..95 (the terrace) only. Meitou keeps the terrace (23.22 `854782-`: street to y 860) and prunes the ring there; in 24.22
      `409471-` the ring joins the terrace by a slope too narrow for a radius-4 path.
    - **Interior object and class** (**Verified**, `FUN_140551b20`, run by the Building constructor `FUN_14055ca90`): a BUILDING
      whose `interior` reference list is not empty gets the interior object (+0x1f0, 0x168 bytes, filled by `FUN_140551660`),
      and its class (+0x198, read by virtual slot 0x358) becomes 12 BCTYPE_SHELL_WITH_INTERIOR when it is still 0. The base
      constructor writes 0; subclass constructors then write their own (DoorStuff 1, UseableStuff 2, StorageBuilding 3,
      GatewayBuilding 7, WallBuilding 9, LightBuilding 11, ...), so class 12 means a plain `Building`. The factory
      (`FUN_14057cc70`) makes a plain `Building` unless the building is made as a door, is `is wall gate` (GatewayBuilding),
      `links together` (WallBuilding), has `farm data`, or its `functionality` record's `function` is other than BF_ANY (0)
      or BF_FLUFF (18) (switch table at 0x14057f174; BF_SHELL_WITH_INTERIOR 19 makes a UseableStuff). +0x2c is set when the
      `interior` list's first BUILDING_PART has no `phs or mesh` and no `parts` (the "blank interior" / "interior blank"
      parts): a blank interior. `is gateway` is read there only for an unrelated call; `cavernous` (+0xc2) and
      `interior terrain` (+0xc1, set by `FUN_14029e0c0`) are not read by the generator (their readers: character
      indoors and ray code at 0x1405ca260 and 0x140800481).
    - PROJECTED (1) and OBSTACLE (2): material 0. None of the nine functions that read `path mode` compares it with 1
      (**Verified** by reading each comparison), so **PROJECTED behaves exactly like OBSTACLE**; the tooltip's "2d
      projection" has no counterpart in the code.
    - other owners (not buildings, not doors, not the building's own interior parts) are skipped by flags of the owner
      (+0x1a0 door flag, +0x1f0 sub-object), not decoded further.
    - shapes become triangles as in [collision.md](../formats/collision.md#shape-to-triangles-observed-fun_1403c1cf0)
      (box, capsule as a 16-sided prism, convex hull, triangle mesh; planes and spheres give nothing).
  - **Carvers** (FUN_1403c4390, "N building carvers"): shapes of group 13 (the `interior mask` trigger hull) whose building
    has `BuildingClassType` 12 = BCTYPE_SHELL_WITH_INTERIOR (the class at +0x198, virtual slot 0x358; enum order from the
    FCS) and a real interior (+0x1f0 → +0x2c clear; both above, building shapes); each becomes a carver (FUN_1403c1ae0 builds the volume, FUN_140dd9e80 wraps it as an `hkaiCarver`
    with flags 0, i.e. without CARVER_ERODE_EDGES, the only flag). A carver removes the navmesh inside it, so the exterior navmesh has a hole where a house's interior is; the interior is built separately.
    **Observed** (original tiles 23.22, 24.22, point-in-polygon on a 5-unit grid within 40 of the origin): no exterior polygon under Old Storm
    House `407-`, Stephouse `560-`, Storm House `403-`/`671-`, Bughouse `433-` (0 of 289 points each), although their terrain lies 14..26 below
    the hull's floor. Meitou: a shell is a building `IsShellWithInterior` ("Own builder", shells); its interior mask carver reaches `CarverDrop` (60) below its hull (engine choice); from the hull's bottom only, the
    ground under raised floors stayed as a crawlspace joined to the street and Blister Hill's residents walked under its houses.
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
      building +0x3bc) is inside the box and not below the water level, that point. `door navmesh axis` (FUN_14029cc90) selects a local axis used to
      place two markers on either side of the door (exact direction mapping **Unknown**), dropped to the ground with a ray: the
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
    - interior jobs (**Verified**, FUN_1403cbfa0 from 0x1403cc57c): the door's inner marker point, else the first node of the building
      with type 0 or 1, else a PhysX ray from 1000 units above the building's origin along its own down axis (orientation ×
      NEGATIVE_UNIT_Y), unlimited length, against group 9 only (mask 0x200): the nearest hit, raised to the water level, is the seed;
      only a miss logs "Warning: building interior has no seed point".
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

Shift+F11 in the game (`GameHost.NavOverlay.cs`, `NavmeshOverlayRenderer`) steps off, Meitou's mesh, the original's cached exterior tiles
(`OriginalNavTiles`, [tagfile](../formats/havok-tagfile.md#navmesh-tiles)), both; for the zones the walkers use now (`NavmeshWalkability.World`), 3 (original 4) units
above the mesh, over the finished picture and fainter where the scene hides it. Meitou: polygons cyan (door polygons blue, seed-pruned red), mesh ends
yellow. Original: polygons magenta, ends orange, edges joined to the next tile violet. Rebuilt on a worker when the world or view changes.
**Observed** (Hub, zone 20.32): the original's polygons are large and simplified, building footprints are holes (interiors separate), and rock and cliff
cuts follow the blocky 36-unit terrain grid (generator inputs, terrain). The original has no such view (`rebuild_navmesh` Ctrl+Shift+F11 only rebuilds).

## Gate codes

The original labels the walkable ground by the gates (**Verified** structure, decompile of 1.0.68):
- **Pass**: a background job (`FUN_1402ef4a0`, applied by `FUN_1402f03e0`) floods the navmesh (`FUN_1402ed460`, a Havok flood) from the two side points of every
  gate (GateData, 0x48 bytes: side points +0x20 and +0x2c, codes +0x38 and +0x3c, state +0x40), writes each region's code into its faces, and gives every object
  the code at its position (`setInsideTownWalls`, slot 0x120; read as `amInsideTownWalls`, slot 0x118, RootObject +0xA0, KenshiLib 1ce4a16 names). A flood that
  hits its face limit logs "ERROR: LIMIT REACHED"; the limit is **Unknown**. Code 0 is the open world (**Observed**: the fight-or-flee rule measures the route
  to code 0, and the pass logs "Multiple gatecode 0 regions"). `NavMesh::getGateCode` (`FUN_1403a30f0`) reads a position's code, -1 while a pass runs.
- **Gate states** (`FUN_1402eebb0`, `FUN_1402eb070`): a map keyed by the pair (smaller code | larger << 8); several gates between one pair keep the lowest
  state (**Verified**); routes through several gates are as bad as their worst gate, best route kept (**Observed**). Same code → 0, either -1 → -1.
  Gate state 1 open or broken, 2 closed, 3 locked (**Observed**). `FUN_1402ebdd0` finds the gate a route between two codes leaves by.
- **Readers in play**: DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS ([gates](locks.md#cutting-and-breaking-285--185-cut-286--186-break)), the run-away score
  ([combat](combat.md)). Wall termites reads the pass's town list but is disabled ([walls](buildings-production.md#walls)).

Meitou (`Meitou.Navigation/NavGateRegions`, engine reading): gates are buildings with `is wall gate` or a BF_GATE functionality
(`TownBuildings.IsGate`); their door polygons (named by the building, `ZoneNavMesh.DoorIds`) are the boundaries. From each polygon beside a gate's door
polygons a flood spreads over kept polygons, across zone links, never into a gate's door; it is a walled region (codes from 1, gates in instance id order)
when it stays within 4000 (X/Z) of its start, under 200 000 polygons and away from the loaded world's edge, else code 0, as is every polygon no flood
reached; off the mesh -1. `NavSystem.LabelGates` runs on the pool whenever zone meshes go live or unload; the host hands the result to the world
(`GateRegionsCommand`, not hashed or saved). **Observed** (Meitou's meshes, 3 x 3 zones, 35 to 90 ms): Stack, Squin, Heng and Admag are walled regions
joined to 0 by their gates; the Hub's gateways have no doors, so it has none; Bad Teeth's gates all lie in 0. A gate may also border small pockets of its
own (gateway inside), which get codes of their own.

## Unknowns

- How the game uses `InteriorFilter` (its admit test is "uid < 0x10000", the same side the `ExteriorFilter` requires) and the
  composition of interior instance uids.
- Which numeric job types the five job adders create (the settings each type uses are now known: 0 whole zone, 1 box
  patch, 3 interior).
- The terrain grid resolution of the generator (FUN_140a08a20); the
  building flag at +0x1f0 → +0x2c that gates carvers and part of the WALKABLE rule; the global seed list DAT_14212f470;
  the ray-hit kind that still allows a ground seed; whether OverrideSettings material −1 means "any material".
- Whether the path request converts the footprint radius to Havok units.
- The full avoidance parameters (the repulsion's absolute magnitude); how movement caps a follower's desired speed by its speed mode.
- Whether face data values other than 3, 4, 5 and the gate nibble exist; how building construction and destruction
  update faces (path mode switches at runtime).
- Havok tagfile field layouts for the face/edge arrays beyond sizes (16 / 20 bytes).
- Whether player-faction characters default to speed mode 2 and how the faction field +0x250 is defined; the +0x2f8 / +0x3d4
  fields of the water state function ([Water states](#water-states)).

### Builder constants (engine choices)

Current Recast parameters are authoritative in `NavBuildSettings`, `ZoneGeometryGatherer`,
`SeedPruner`, `NavInteriors`, `NavQuery`, `NavmeshWalkability` and `CollisionCache`; see
[simulation.md](../simulation.md). They are Meitou choices, not original-game constants.
**Unknown** original linked-wall seed fractions (Meitou uses 0.15/0.5/0.85).
**Observed** original start snap500 Havok units and goal snap about30 Kenshi units;
Meitou uses60/30 Kenshi units. Original height/climb1.8/0.5 Havok correspond to18/5 Kenshi, width 0.9 to 9 (Meitou radius 4); its stitch
step/separation 1.0/0.2 to 10/2 (**Verified**, Generator); Meitou's interior joins use height 12 and gaps 6 (doors), 2.5 (doorless hulls), 5 (ruins).
## Expression reference

- Tile files: `h ^= v + 0x9e3779b9 + (h<<6) + (h>>2)`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Face data (per-face material, **Observed** except where marked)
