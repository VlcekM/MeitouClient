# Buildings and production

Original building classes/function IDs, records, construction/producer/power/storage/farm/research
binary research; field/constants tables and expressions retained. Placement: [zones](../formats/zones.md),
prices/stock: [economy](economy.md). Meitou construction contract: [simulation](../simulation.md#buildings);
Meitou production: [simulation](../simulation.md#production).

## Construction

### Construction state

`Building+0x160` (returned by Building slot 69) holds the construction state (**Verified**, offsets read in the
initializer `FUN_14055ba20`, the tick `FUN_1405595a0` and the save writer/reader `FUN_1405547f0` / `FUN_140555650`):

| Offset | Meaning | Save key |
|---|---|---|
| +0x160 byte | complete | `is complete` |
| +0x161 byte | paused (auto-jobs skip it) | `pause` |
| +0x162 byte | dismantled (set when dismantling reaches 0) | - |
| +0x168 float | dismantle work held while `FUN_14025c6d0` blocks it (**Unknown** what) | - |
| +0x164 float | progress, in material units | `construction progress` |
| +0x178 / +0x180 | material entries `{item, required float, delivered float}` | `<i>construction mats` = delivered of entry i |
| +0x188 float | target = sum of required amounts | - |
| +0x18c float | rate = `build speed mult` / (1200/11), i.e. per game hour converted to per second | - |
| +0x190 int | builder counter of the 0.75 falloff | - |
| +0x194 float | visual threshold = clamp(`build threshold` / 100, 0, 0.95) | - |
| +0x1a0 byte | is a door (set by the DoorStuff constructor `FUN_14029fb40`): build work x3, never completes itself (neither by progress nor by the immediate finish; its parent building does, `FUN_1405595a0` lines 56-60 and 121-123, **Verified**), no refund | - |
| +0x1a1 byte | destroyed | `destroyed` |

Materials (**Verified**, `FUN_14055ba20`): one entry per BUILDING `construction` reference (item = the referenced
record, required = its first value); if that list is empty and int `build materials` > 0, one entry of item
`580-gamedata.base` (Building Materials) with required = `build materials`. Delivered starts at 0. Re-initialising keeps existing entries
(matched by item) and resets only their required amounts. A total of 0 completes at once. Data (**Verified**, probe of
the install): `gamedata.base` has `construction` lists on 2 buildings (House: Building Materials x15), the rest use
`build materials`; `rebirth.mod` adds lists to 174 buildings, all ITEM records (e.g. Harpoon Turret: Steel bars x3,
Iron Plates x7, Copper x3).

### Construction tick

**Verified** (`Task_Build` update `FUN_14034a440`, event dispatcher `FUN_1407b65b0` case 11, `FUN_1405595a0`):

1. Each update of a builder whose site is not stalled pushes a type-11 event `(building, amount)` with
   `amount = lerp(0.3, 2.0, clamp(engineering, 0, 100) / 100) * dt`, engineering the raw stored stat (CharStats +0xcc, no
   effective-stat multiplier; dismantle reads it the same way, **Verified** `FUN_14034a440`, `FUN_1403518c0`), `dt` = the character's update time
   (`Character+0xc4`, game-speed-scaled seconds). The builder gains engineering XP (`FUN_1408c6250`, [stats](character-stats.md)).
2. The dispatcher calls Building slot 70 with that amount. Slot 70 scales it by the *Building speed* option
   (`142133584`, 0.5..2) and CONSTANTS `build speed` (`g_Constants+0x168`, 6 in the base game), then by `0.75^k`
   where `k` = calls since the counter was last reset (k capped at 15; a counter above 100 restarts at 0), and by 3
   for a door (+0x1a0).
3. `progress += rate * scaled amount`; a rate <= 0 sets progress to the target at once.
4. When +0x1a0 and complete are both clear and `progress >= target`, Building slot 72 (`FUN_140562340`) completes it
   (progress = target, complete set, functionality/lights/interior switched on) and, for a player-faction building (**Observed**),
   posts "Building '{1}' construction complete [at '{2}']." and the `Building_Complete` sound.

The counter is zeroed by Building slot 28 (`FUN_1405626a0`); how often that runs is **Unknown**, so whether the 0.75
falloff is per frame (several builders on one site) is **Observed** only.

Materials gate progress (**Verified**, `FUN_14054a3f0`): a site is stalled when it is not complete and
`sum(min(delivered, required)) <= progress`; `Task_Build` ends instead of pushing an event. One delivered unit thus
allows one unit of progress, and the last tick may overshoot by one event.

Resulting time for one builder (**Verified** algebra of the above): `hours = materials / (build speed mult x 6 x option
x lerp(0.3, 2, engineering/100))`. The build window's "Estimated build time" (`FUN_14055bf10`) is the same without the
engineering factor (it equals an engineering 41.2 builder). Meitou should run the tick above, not the UI hours.

Destroyed buildings (**Verified**, `FUN_1405595a0`): the first tick on a building with +0x1a1 set clears complete, sets
progress to 0.4 x target, sets each delivered amount to `floor(delivered x U[0.3, 0.5) + 0.5)` and calls slot 104
(`FUN_140557280`, destroyed off: navmesh/doors restored, **Observed**). That transition lives only in Building slot 70, whose callers are the
build event 11 (`FUN_1407b65b0`), the add-materials task with amount 0 (`FUN_140348870`), save import (amount FLT_MAX, which skips it),
placement, the wall forwarding and finish helpers; the per-frame refresh (slot 28 `FUN_1405626a0`) never calls it. So nothing ruins a building
by itself: it stays destroyed, complete, until a builder or a delivery touches it (**Verified**, decompile; Meitou `ConstructionSystem.Touch`).

Save import (**Verified**, slot 18 `FUN_140552b70`): when importing, a building whose faction is not the player's has
`destroyed` cleared, every delivered set to required and is completed at once (slot 70 with FLT_MAX).

### Construction look

**Verified** (`FUN_140559110`, `FUN_1405583e0`, `data/materials/deferred/objects.hlsl`): the shader constant
`constructionState` of every part entity = `(f, f, 0)` (`(0, f, 0)` while +0x1a0 is set), with
`f = (min(progress/target, 1) - threshold) / (1 - threshold)`. The vertex shader gives each vertex
`y = height / mesh top`; pixels above `1.2 f` keep only the scaffold grid (`construction.dds`, alpha > 0.7), and the
grid overlay fades out above `1 - 5 (f - 0.8)`. The shadow caster clips the same way.

### Material delivery

**Verified** (`Task_AddMaterialsToBuilding` `FUN_140348870` / `FUN_140351e20` / `FUN_140336e60`): animation `medic`,
sound `Character_Building_Materials`; a bar fills at `dt x 0.2` (5 s). When full, for every entry with
required > delivered it looks for one item of that record in the character's inventory, then its backpack; if found
the item is removed and delivered += 1 (`FUN_14054a3a0`). Materials are therefore consumed on delivery, one unit per
missing material type per action; progress later spends delivered units (stall rule above). The task is done when
nothing is outstanding and fails to the planner when something is outstanding but nothing is carried. Whether the bar
restarts between actions is **Unknown**.

Fetching (**Verified**, TaskData rows of `FUN_140319a60`, finder `FUN_1405a2a60` -> `FUN_14059d000`, factory `FUN_14032ebd0`
case 247, `FUN_14033cd10`): the planner chain is 2 Building (needs 51) <- 71 Adding materials (needs 50) <- 115 "Finding
building mats" (effect 150, a target finder) <- 247 "Collecting resource". The 115 finder takes the site's materials with
`required - delivered > 0` (`FUN_14054a450`) in list order and returns the first one's source (factor 1; 0 when none):
1. a source remembered for that item (a per-item map in the AI, kept in the AI's per-update cache, which is emptied at the start and end of each update, so it changes no answer; Meitou keeps none);
2. a loose item within 1200 of the character with the same record, not blacklisted and not lying in another faction's
   building (`FUN_140997d80`, **Observed**); the first the world grid query meets;
3. a complete building of the character's town (its current town, else the nearest town within 3953, plus its home
   town; `FUN_1408cbea0`, query mask 0x4002436 = functions 1, 2, 4, 5, 10, 13, 26 read as bits, **Observed**) whose
   owner it may use, that is not the excluded one, holds the item and has items: storages (`StorageBuilding`, slot
   0x3b0 null) first, then production buildings (their output); nearest within the AI's range. An item matches when
   it is the same record, or, when neither is an ITEM (type 4), any record of the same type.
The town candidates are the buildings found by a spatial query round the town (`FUN_140a00690`: within the town's radius x 1.1 of its position, filtered by the Ownerships' `canIUseThisBuilding`), not the buildings registered to the town, so a player outpost's storages count by where they stand (**Verified**; Meitou `FixtureRegistry.TownCandidates`, shared with the cage finders). The range is the AI's mode `AI+0x2bc` (**Verified**, constructor `FUN_1406236d0` sets 2; character orders 8/9 of
`FUN_1405caa50` change it, which HUD buttons send them is **Unknown**): mode 2 squared distance <= 4.246733e7 (about 6517)
from the character; mode 0 `(18 + 40 + 5)`, mode 1 `(18 + 200 + 5)` (AI+8 = 18, AI+0xc = 5), others 1000.
Task 247 is `Task_EmptyMachine_BuildMats` (+0x78 clear) for a building target, `Task_Pickup` for an item. At the building
the update lists its records (a storage: every record it holds; a production building: its output), takes the first
record whose count (slot 5, `FUN_1403411c0`: always 2) is above 0, i.e. the first listed record, and moves up to 2 units
while the character has room, then ends. Meitou takes the material instead of the first listed record (engine choice). Looting food (284, `Task_EmptyMachine` with flag +0x78 = 1, `FUN_14033cd10`, **Verified**) takes the first listed record (the storage's distinct records in inventory item order, `FUN_140299990`, no food filter) the same way but up to 10 units, one per pass, ending when the building no longer holds it or there is no room.

Missing-material speech: **Verified** text in the original `locale/zh_CN/LC_MESSAGES/main.po`, source annotation
`character.cpp:3412`: "I don't have any building materials." / "我没有建筑材料了。" The separate GOAP.cpp:490
line is "I don't have any materials". **Observed** (user report): repeating a manual build order without materials
shows a character speech bubble. Meitou emits the character-order line once per explicit order when the site is stalled,
the builder carries no needed material and no fetch source exists; exact original trigger guards remain **Unknown**.

### Builders and dismantle

Task ids and AI conditions: [ai-tasks](ai-tasks.md#build-tasks). Engineer job (**Verified**): row 125 "Engineer" has no
lists, effect 2 and the finder `FUN_140999060`, which ignores the job's target and runs `BuildingFinderForEngineers`
(`FUN_1409a6900`, `FUN_1409a6c10`) from the character's position. Candidates are
buildings of the character's owner that are not blacklisted, dismantled, holding dismantle work (+0x168 > 0) or paused,
and not (slot 0x2f0 = 19 SHELL_WITH_INTERIOR and destroyed, slot 0x330 = +0x1a1: any other destroyed building stays a candidate and the
engineer's first touch ruins it, clearing the flag); an incomplete building (or one needing repair, slot 0x328)
is a candidate, a complete one contributes its mounted children that need work. Score `max(2 (1 - d^2 / 9000^2),
0.0001)` (d 3D), x0.5 when slot 89 is set, / (n + 1) with n = characters already working it; a stalled site the
character has no material for (condition 50 false) is multiplied by the planner cost of the 115 finder (`FUN_14060f1d0`)
and skipped at 0, i.e. when no source exists. The highest score wins. No cap on builders per site (**Observed**: the
score and the 0.75 falloff spread them). Work distance: [below](#work-distance).

### Work distance

**Observed** (decompile of kenshi_x64.exe 1.0.68, names from KenshiLib 1ce4a16): the AI's arrival condition `isAtLocation`
(AI slot 11, `FUN_1405a2450`) at a Vector3 is 12 units X/Z and |dy| < 20, or the path has ended; at an object it is
`AI::isWithinChoppingRange(target, isMoving)` (`FUN_14059c6d0`). For a building (type 0) that is arrived when:
- the building `is foliage` (+0xc0, a rock) and the character is inside its bounds box scaled 1.2 (`getAABB`, slot 0x420), under a
  movement flag (+0x640 +8, **Unknown**); or the character's movement reports touching the building's handle (+0x300 slot 1); or
- the X/Z distance to `getPositionMarker(charPos)` (slot 0x470: the nearest valid usage node, else the construction position
  +0x26c when fewer than 2) is below `getReachRange()` (slot 0x350), **doubled when not moving**, and the character is on the
  same floor (slot 0x60) or |dy| below a limit: 130 when the door byte (+0x1a0) is set or the building has a door child (slot 0x3e0), else 45 when its containing-building handle (slot 0x1d8) is null (outside any building) or slot 0x428 is true, else 15 (**Verified**, `AI::isWithinChoppingRange` `FUN_14059c6d0`, constants 130 / 45 / 15 in `data_values.txt`; what slot 0x428 is stays **Observed**: ceiling-mounted class).
- Meitou: one shared `FixtureRegistry.AtCage` for the cage goals (arrest, kidnap, town duty): flat distance to the cage's position below its `use range` (doubled when not moving) and |dy| below 15; the former `CageReach` / `CageSlack` slack of cages is gone (a cage's position marker, the floor number and the outdoor 45 are not modelled).
- Otherwise not; once the path has ended at the target it logs "Task impossible: X can't get to Y". There is no settle slack.
- That notice (**Verified**, `FUN_14059c6d0`, also the failure handlers `FUN_14033da80` / `FUN_140332150`) calls `FUN_140796ed0`: the target's handle goes into the AI's ignore map (AI +0x20 -> +0x218) with the timer 20.0, and the squad blackboard is told. Readers skip the handle: the loose-item source finder `FUN_14059d000` (before the ownership test), the engineer finder `FUN_1409a6900` (first condition), the turret finder `FUN_1405b0da0`, the corpse-machine finder ([ai-tasks](ai-tasks.md)). The entry's decay is **Unknown** (a 20 s timer is assumed). Meitou `ConstructionSystem.NoteUnreachable` (`CharacterCold.Ignored`, not saved): a builder whose path ended short ignores that site or loose item for 20 s; a fetcher of a loose item goes back to its site and picks another source.

`getReachRange`: `Building` 40 (construction), `WallBuilding` 60, `UseableStuff` (machines, storages, farms, mines, rocks) its
`maxUseRange` +0x420, read from the functionality's `use range` (`FUN_1402a0900`), `DoorStuff` 45 (kind 7) or 25. Vanilla `use range`
(**Verified** by a probe of the install): 10 to 25 for benches and storage, 50 ore mines and iron rocks, 80 wind generators,
100 copper rocks, 140 hybrid stone mines. Which tasks reach their building through `isAtLocation` is **Observed** only through
the goal conditions (the call is virtual). Meitou applies the height limit above wherever it applies the reach range of the same function: builders, fetchers, machine operators, haulers and corpse carriers (`SiteReach.AtHeight`; rocks and turret posts keep their own arrival test).

Meitou's NPC town workers (189, 190, and a player slave's work, `AiSystem.AtWork`): within the building functionality's `use range` (30
without one) of the building's position, doubled once its velocity is nearly 0, so a worker held off by the rock short of the reach has arrived
(standing in for the touching test; the usage nodes of `getPositionMarker` are not read: engine choice). **Observed** (Meitou, Rebirth, before
this rule: a fixed 30): every worker that reached its mine stood 57 to 71 units from the mine's position with its walk never over, and never mined;
with it (the mines' `use range` 40) all 15 that reached their rock work at its clip. They still gather at the same point of each rock, where the
original's spread round it (the usage nodes).

Meitou (`SiteReach`): builders 40 (walls 60), operators and storage fetches the `use range`, doubled once stopped, measured
to the building's footprint hull (engine choice standing in for touching; a building without collision uses its parts' bounds
hull) or, unknown, its position. A rock's operator has arrived inside its mesh's footprint (the convex hull of its vertices
seen from above) as the instance places it, scaled 1.2 about the box's middle (a circle of 1.2 x its radius without the mesh; the bounds box without the movement flag let
miners work a scattered or long rock from 20 m off, #332), or within the `use range` (doubled once stopped) of the box's centre, which
stands in for the position marker (engine choice; a rock's origin can lie off its pile); it faces that centre. Taking its ore
measures 40 to the footprint's edge. Height: Meitou has no floor id, so `SiteReach.AtHeight` requires |dy| below the limit above (130 door, 15 inside a building, else 45; slot 0x428 not read) for every site goal.

Repair (**Verified**, Building slot 0x328 `FUN_1400f6b70`, `Task_Build` `FUN_14034a440`): a building needs repair when
`progress < target`; building health is construction progress. Orders 2 and 95 are both `Task_Build`, which pushes the
same build work and ends once complete and `progress / target >= 1` (`FUN_140547200`). A complete building never stalls
(`FUN_14054a3f0` tests only incomplete ones), so repairing takes no materials. Whether slot 70 clamps at the target is **Unknown**.

Damage (**Verified**, slot 62 `hitByMeleeAttack`, KenshiLib 1ce4a16 names): only melee blows lower a complete building's
progress. The Building/`UseableStuff` body (`1402972c0`) returns 1 and changes nothing; only `WallBuilding` (`14029d560`, gates
included) and `DoorStuff` (`14029d690`) override it (formulas: [combat](combat.md#attack-zone)). No ranged slot exists beside
62 on RootObject. In play the destroyed setter (slot 104) is called with 1 only by the wall hit (the "Wall termites" drain
also calls it, but never runs, below); a destroyed wall takes the destroyed transition above (`WallBuilding` slot 70 calls the Building one first).

Purchased ruins: **Observed** in the user's original-game screenshot (Squin Longhouse, before and after purchase): Condition is 0
with an empty bar before repair starts; the destroyed-building transition's 40% is not its purchase-time condition.
**Observed** in the same user's original-game Squin Longhouse screenshot just after starting repair: Door Condition 16,
Building Condition 9, Building Material 9/24. This is consistent with progress 9.6 displayed as an integer and randomized
retained materials; one screenshot does not establish a fixed retained amount or the original RNG sequence.
**Observed** in the user's original-game comparison: right-clicking a purchased damaged house exposes
Repair, and selected characters supply building materials and work. This is reconstruction, distinct from the complete-house
repair above. Meitou uses the destroyed-building transition and material gate already described here, activated by the explicit
order; the exact original activation timing remains **Unknown**. Implementation and regression coverage:
[town-house repairs](../simulation.md#buildings).

Dismantle (**Verified**, `Task_Dismantle` `FUN_1403518c0`, Building slot 73 `FUN_1402a2860`): refused by the builder itself when the task runs (a speech bark, then the task ends): "I have to
dismantle the furniture first." / "...unmount the buildings first." while those exist (Meitou: the order is accepted and the builder barks it, `OrderCheck.Blocked`); a door redirects to its
building. Animation `stonemine`, engineering XP as for building. Per update `amount = dt x lerp(0.05, 4, eng/100) x 4`;
slot 73 clears complete (a complete building first drops to `target - 1`), then `progress -= amount x rate` (no option
or CONSTANTS factor). At progress <= 0: every turret mounted on it (`BuildingFinderTurretsOnCertainBuilding`) is destroyed through `FUN_140799af0` with the reason "dismantled" (no refund, **Verified**; Meitou `DestroyMountedTurrets`), +0x162 set, sound `Thing_Complete`, refund, building
removed.
The menu offers it only when Building slot 79 (`FUN_14029c380`) holds (**Verified**): false unless the owner is a player faction, and false for a foliage building (flag `+0xc0`); true when the parent building is a player faction's; one more branch (a `creates player town` building whose related object has no player owner) can make it false and is not modelled (**Unknown**). Meitou gates the verb and `DismantleOrder` on the player's faction.

Refund (**Verified**, `FUN_14029dc10`, called by slot 73 and by `FUN_14054c810` for mounted buildings that cannot
move): skipped for doors; for each entry with delivered > 0, spawns `max(1, trunc(U[m, 1) x delivered))` items at
random points in the building's bounds, `m` = CONSTANTS `min dismantle materials percentage` / 100 (60 -> 0.6,
`g_Constants+0x134`): one item per unit, x and z uniform within the bounding box (`+0x27c`), the height from the
navmesh or the ground below (**Observed**). Cancelling an unbuilt site is a dismantle and returns 60-100 % of what was delivered.

### Placement

Build-mode object `DAT_14212de48`; the ghost (class `PreviewBuilding`, subclasses `_Wall`, `_Resource`, `_Ceiling`,
`_Snapping`) at +0x18 recomputes its flags every frame in slot 25 `FUN_1404dba40`; the click handler `FUN_1404e27e0`
shows the first failing message in this order (**Verified** unless marked):

| Message | Check |
|---|---|
| "Can't build too close to another town." / "Can't place in a building you don't own." | a non-player town is within its block radius (below); second text when the ghost is inside/on a building |
| "No resources at this location, mining would be pointless." / "This crop will not grow in this environment." | resource value or crop yield < 0.01 (below) |
| "Cannot build inside incomplete buildings." | the building under the ghost is incomplete |
| "Must be placed outside or on a roof." / "Must be placed inside a building." | indoor/outdoor rule; the first for `BF_RAIN_COLLECTOR` (26) |
| "Can only be placed on certain locations (marked by blue nodes)." | `PreviewBuilding_Snapping` not on a snap node ([snapping](#snapping-to-a-building); Meitou never fails it) |
| "Too close to another building." / "No valid usage nodes." | a footprint node box overlaps physics shapes (group mask 0xfffde40, up to 32) or another queued ghost; second text when every usage node is invalid. Meitou (`PlacementCheck.HullBlock`) overlaps footprints in 2D instead, leaving out the sites and ghosts on another floor of the building the ghost stands in (the boxes are 3D). Inside a building, the footprint must lie in its convex indoor hull, and its prism (2 over the floor to 2 under its top) must not reach more than 1 into that building's own collision (walls, floors, roofs; `FootprintPrism`): the hull takes in the walls, so a footprint inside it could stand in a Storm house's sloped wall |
| "There are characters in the way." | an object in a node box is rejected by `FUN_1407d1440` (**Observed**) |
| "The building blocks another building use nodes." | the footprint covers another building's usage node |
| "The building has one or more parts on invalid ground." | a BUILDING_PART with `ground type` LAND has its node centre below y = 98 (water level, **Observed**) |
| "Invalid placing floor number. ..." | inside a building above floor 0 a node overlaps floor collision; or a ceiling ghost finds no ceiling |
| "Ground needs to be more level." | vertical margins or slope (below) |

- **Towns** (`FUN_140926d50`, `FUN_140926eb0`): radius = `town radius mult` x base (town 2500, village 937.5, ruins
  875, point of interest 437.5, others 1250). A `creates player town` building is blocked within 2.5 x radius (1 x for
  ruins/POI) or 2000 of the centre; other buildings only within 1 x radius, so they may stand outside any town.
  Player towns never block. **Verified** (`FUN_140927f10`, `FUN_140926d50`, `FUN_140926eb0`, `FUN_1404dba40`): the ghost's
  town is the nearest by centre among all types except type 8 `NEST_MARKER` (skipped, so a nest marker never blocks; the
  ghost's call arguments are not in the decompile, the other callers pass type 10 = any);
  type 10 null towns have radius 0 (type 8 too), so they block only a `creates player town` building, within 2000.
  No town blocks an `is interior furniture` or `is node` ghost (ghost `+0x109`) whose footprint nodes all stand in one
  building it may be built in (ghost `+0x8b`): `FUN_1404dba40` clears the town, so furniture goes into a bought town house.
- **Building in a building** (**Verified** by decompile, `FUN_1404d69c0`): every footprint node must stand in the same
  building, and that building's owner (Building slot `+0x58`; none means the FACTION `nofac`, "No Faction") is a player
  faction (`+0x250`), or the ghost is not `creates player town` and the building is destroyed (slot `+0x330`, `+0x1a1`,
  the save's `destroyed`) or its owner faction is `not real` (`+0x1d0`, set from the record in `FUN_1407fe270`; `nofac`
  is, so ownerless buildings qualify). Else the ghost is refused, with "Can't place in a building you don't own." when a
  town also blocks (the text without one is **Unknown**; Meitou shows the same). Meitou: `PlacementRules.MayBuildIn`.
  **Unknown/not modelled:** the original tests only that nearest town (Meitou tests every one), and swaps it for the
  town containing the ghost (`FUN_140928890`) when inside 0.9 x its radius (0.6 x when the nearest is ruins/POI).
- **Outposts** (**Verified**, `FUN_1404d6d90` per queued ghost on Confirm, then `FUN_1404db180`): a ghost in or on a
  building takes that building's town when it is within 5000 (X/Z) of the ghost, so furniture in a bought town house
  joins that town and never makes an outpost (Meitou: none, `BuildSite.Town` names player towns only; the parent instance id is unique only per
  zone ([zones](../formats/zones.md)), so the level building of that id nearest the ghost is meant; a level building
  of no town, such as a wilderness ruin, goes on to the rules below); else the nearest town of the player faction within 5000 (X/Z); else the town of
  the nearest player building within 2500 (the decompile adds an X/Z > 10000 test there that looks unreachable,
  **Observed**); else a `creates player town` building that is not a wall makes an outpost at its position
  (the ghost node's position plus `Aabb::getSize` of the ghost's bounds, the full X, Y and Z extent, **Verified** `FUN_1404d6d90`; Meitou: `PlannedBuilding.BoundsSize`, the merge of the ghost parts' mesh bounds in their own space, from `PlayerInterface.GhostBoundsSize`), and every player building whose town is a player town or none
  and whose nearest town of any faction within 8000 is the new one moves into it. Walls (`links together`, ghost class
  `PreviewBuilding_Wall`) never make one there; after the pass, queued walls still without a town take the last player
  town among the queue's ghosts, else the nearest town of any faction within 16000 of their mean position, else a new
  outpost there. A global flag (`DAT_142134690+0x298`, meaning **Unknown**) skips all of this.
- **The outpost** (**Verified**, `FUN_140934e10`): a `Town` made from the TOWN `player_town_template` (base data: name
  "Your Outpost", type 1, `town radius mult` 1, no faction), owned by the player faction, named "Your Outpost",
  `public` false, `plevel` 0, `discovered` and `explored` (+0x180/+0x181, KenshiLib 1ce4a16 `TownBase`), added to the player-town list (town manager +0x68) that the save writes
  ([player outposts](../formats/save.md#player-outposts)). On load (`FUN_14092ae80`) a player-faction town whose centre lies strictly inside the
  radius of its nearest other player-owned, non-nest town is removed (the one reached first in list order; its buildings keep their `town` handle,
  become unassigned if that handle no longer resolves) with "Player town '<a>' withing boundaries of <b>" ([rule](../formats/save.md#importing-a-game-savemanagerimportgame-fun_140378a30)).
  No residents: the template lists none, and the resident pick (`Town::chooseResidents` `FUN_1409370d0`, from the building
  `default resident` / `residents override`) runs from `ZoneManager::loadPhase2` (`FUN_140a0e760`) only for towns whose faction
  lacks the player flag (FACTION `+0x250`) (**Verified** by decompile and its only caller).
- **Unresolved building towns** (**Verified**, `FUN_140555650`, `FUN_1402a14b0` / `FUN_1402a1750`, `FUN_1406bd000`): the saved `town` handle is resolved as a handle; when its town was removed or never existed, the building stores the canonical null town. It is not reassigned by position on first use, a tick, another placement or dismantling. It is absent from the player-town panel and town ownership association until a later placement operation assigns it.

  **Power** (**Verified**, `FUN_14092d850`): the power manager advances frames from the registered town list; `NO_TOWN` is not a frame. Thus an unresolved building town does not share a grid with other unresolved buildings or contribute to a town's power/battery totals. Production's complete-building tick and the exact input/output effect of the missing town remain **Unknown** from the traced call chain.
- **Centre, radius and placement-time merge** (**Verified** by decompile, `Town::reassessTownPosition` `FUN_1405488f0`, via `TownList::reassessTownPositions`
  `FUN_14054d080`; called on placement commit `FUN_1404d72a0`, dismantle `FUN_1402a2860` / `FUN_14054fea0` and town merge
  `FUN_140931840`): a player town's centre (+0x48) becomes a weighted mean of its buildings' position markers (two building
  sets, the first weighted x4; which buildings each holds is **Unknown**), put on the ground and on the navmesh within 500 (kept
  when no point is found); the radius (+0x51c) becomes the farthest building's X/Z distance from it + 200, at least 1250, clamped
  to [1000, 20000]. A town with no buildings at all is removed. `Town::getRadius` (`FUN_140926eb0`) returns +0x51c for a player
  town (2500 while it is at most 1), and `type`-based radii (table above) otherwise; campaigns' arrival and in-town tests
  (`FUN_140926d50`) use it. So the first building's spot stops being the centre as the base grows. Meitou:
  `ConstructionSystem.ReassessTown`, a plain mean of the town's outdoor sites (engine choice); load reassessment deliberately does not run the merge pass. **Verified** (`FUN_14054d080`, `FUN_140931840`, `FUN_140927060`, `FUN_14092b3c0`): on placement-time reassessment, overlapping same-owner towns use `distance² < (radiusA + radiusB)²`; the smaller-radius town survives, the other town's buildings move to it, and the other town is destroyed.
  **Verified** (`FUN_14054d080`, `FUN_1402a2860`, `FUN_14054fea0`): the placement/dismantle reassessment removes a player town with no buildings; load-time normalization does not apply this empty-town removal.
- **Slope** (slot 31 `FUN_1404daa20`): ground normal from four rays at the rotated bounding-box corners (outdoors) or
  one ray (indoors); slope = angle from vertical, compared with `max slope` in degrees. With `match slope` (forced for
  interior furniture and nodes) the ghost tilts to the ground and a slope over the limit fails; without it the tilt is
  blended by the player tilt factor and clamped, never failing. Also fails on an object surface more than 5 deg off
  the hit normal, or on ground with normal.y < 0.2 (`FUN_1404d5420`).
  The slope block is wrapped in a call to vtable slot 3, which returns false for `PreviewBuilding`, `_Wall`, `_Resource` and `_Ceiling` (`FUN_14042de80`; only `_Snapping` overrides it with `FUN_1404fe140`), so it runs for an indoor ghost too (**Verified**, `vtables.txt`): one ray from 10 above the origin straight down against the viewed floors' collision, its hit normal (up if none) against `max slope`; the indoor ghost's forced `match slope` makes an over-steep floor (a stair) fail it (NotLevel). Meitou: `FloorSurfaces.NormalBelow` (a ray 10 above the origin straight down on the floors up to the viewed one, the nearest triangle's upward normal) in `GhostFloor.Normal`, and `PlacementCheck` fails NotLevel when its angle from vertical exceeds `max slope` (> 0); the 5 degree surface test stays untraced.
- **Vertical margins** (**Verified**, `FUN_1404d9c00`, `FUN_1404d21e0`, `FUN_1404d23c0`, `FUN_1404d9530`, `FUN_1404d2080`, `FUN_1404d1680`
  and the frame update `FUN_1404dba40`; Meitou: `PlacementCheck.Margins`, `Settle`):
  - **Groups and nodes.** One group per BUILDING_PART with `affects footprint` (`FUN_1407e5f80`); its `above ground` bool picks
    the side. Its nodes are the four corners (X, Z) of the part's mesh bounds box, times the building's `scale`, at height
    `footprint vertical` x scale. A node's world place = the ghost's turn x (node + the part's scene-node position) + the ghost's
    position (the ghost's tilt is in the turn). Wall ghosts (`PreviewBuilding_Wall`, own slots) add intermediate points along
    long LAND parts and are not measured here.
  - **Ground** under a node (`FUN_1404d2080`): the terrain height there, never below 100 (`FUN_1409b2a40`). A ray hit counts only
    when its object type is 0xe (the terrain, **Observed**: `FUN_1404d5420` resets the lift when the cursor hits any other type);
    anything else falls back to the terrain height, so buildings never count as ground.
  - **Margins.** Per node: ground - node height for a group not above ground, node height - ground for one that is; a group's
    value is the minimum over its nodes, and the ghost's `+0x84` ("up": how far it may still rise) is the minimum over the
    not-above groups, `+0x80` ("down": how far it may sink) over the above groups, both starting at 1e8.
  - **Tolerance.** `vertical position tolerance` T (BUILDING float) when |T| > 0: with no above-ground group, the first group is
    measured again with its nodes moved up by T x scale (the building's turn applied, **Observed**: the decompile computes
    the turned offset but its use is unclear) and node - ground, folded into "down"; with only above-ground groups, moved down
    by |T| x scale and ground - node, folded into "up".
  - **Frame nudge** (`FUN_1404d1680`, added to the lift and to the margins in `FUN_1404dba40`, before the validity test): outdoors
    with down + up < 0 the ghost centres, lift += (up - down) / 2 (unless a building is under the cursor, **Unknown** use, not
    modelled); else a negative down raises it by -down, a negative up lowers it by |up|. `is interior furniture` / `is node` ghosts
    (that may also stand outside) are instead pulled to lift 0: step = min(-lift, up - 0.01), then clamped to -down. So the margin
    failure outdoors is "down + up < 0" ("Ground needs to be more level.", `FUN_1404d1640`), and buildings sink into slopes
    by themselves. The margins are skipped for interior-only furniture, inside a building (`FUN_14042def0`), and when
    `PreviewBuilding +0x48` is set (never seen set outside the build-mode object).
  - **Not modelled:** ghosts on a building's floor (Meitou takes the floor as level, the original measures against the terrain
    under them, **Unknown**), the indoor lift reset, parts nested under another part (the node position is the whole chain's, the
    original's the part's own scene node), and a mesh whose bounds file is missing (the part is left out).
- **Indoors** (`FUN_1404d69c0`, flags read in `FUN_1404debc0`): an `is interior furniture` or `is node` building
  is valid with all node points in one player-owned building; otherwise only if it is `is exterior furniture` or
  `is node` (**Verified** by code). So interior-only furniture must be inside, while interior+exterior items (Campfire,
  Camp Bed, storage boxes, fish racks) go either way. Without `is interior furniture`
  the check passes. `ceiling placement` uses the ceiling ghost: `PreviewBuilding_Ceiling` (vtable `1416e1b68`) overrides slots 4 (`FUN_1404fe0b0`, returns 1), 21 and 27. Slot 21 `FUN_1404d28a0` (**Verified**) is the position update: a ray from 5 above the cursor point (`DAT_141685340`) pointing UP, collision mask `0x8071c00` (groups 10, 11, 12, 16, 17, 18, 27: upper floors, their stairs, unwalkable roofs), result initialised to -99; on a hit the ghost's y = hit y - 1 (`DAT_14167c308`) and the hit object is kept (+0x130); on a miss y += 20 (`DAT_141683188`) and the valid-surface byte (+0x8a) is cleared. Slot 27 `FUN_1404fe0a0` compares an object with the kept hit. Meitou hangs the ghost 1 below the ceiling the room model finds (`Room.CeilingAbove`, `CeilingHang`), which approximates the ray (it does not apply the collision mask).
- **Resources** (`FUN_1404d9ea0`): `world resource mining` STONE/WATER/GROUND from a noise field; IRON/COPPER/CARBON
  need the biome's `resources` and a resource node within 1500, falling off from the node radius (>= 25) to 1500.
  Farms: fertility `1 - (1 - v) x fertility effect` (0 at or below `minimum fertility`), yield = biome
  dry/green/swamp weights x the farm's factors x fertility.
- **Resource popup** (**Observed**, `FUN_1404d9ea0`, each frame for a ghost with a `world resource mining`): a
  DatapanelGUI on the `Popup` layer above the ghost (its bounding-box corners projected, the highest y, kept on the
  screen). Rows (key, value): `<name>:` (`FUN_140a0c300`: Iron, Stone, Copper, Carbon, Water, Fertility) and
  `{1,num}%` of int(v x 100), v the value clamped to 0..1 (a farm: its fertility factor); a farm adds `Crop yield:`
  and `Environment:`. Values are coloured (1 - c, c, 0), c clamped to 0..1. Environment: `-` when `fertility effect`
  is 0, else of the coverages Green, Arid, Swamp (keys 1, 2, 3) the first largest, `, ` and the next equal one, or
  `Dead` when the largest is below 0.1, in the crop yield's colour. The ghost is valid only while v and the crop yield
  are >= 0.01. The popup's crop yield (`FUN_1400e40a0`, **Verified**) is a fixed constant when the farm's own `fertility effect` is 0, else the weighted biome coverages: it keys on the record's flag, not on slot 59 as the running farm does (`FarmBuilding`), and `PlacementRules.Readout` matches that.
- **Controls** (bindings `FUN_1403636c0`, applied in `FUN_1404d5420` x frame time): rotate +-80 deg/s, move up/down
  +-60 units/s (6 with the slow modifier; offset reset above 1000), tilt factor +-1/s in 0..1, default 0.2. Each step is capped
  by the margins (**Verified**, `FUN_1404d5420`): the up key moves by min(step, up margin) while the up margin is above 0 and
  otherwise the down key tests the same against the down margin; the margins are those of the previous frame, after the nudge.
- **Commit** (`FUN_1404d5130`, `FUN_1404db5a0`, `FUN_1404d72a0`): a click queues the ghost (position relative to the
  parent building, or height above terrain, and floor) and spawns a new ghost; Confirm assigns towns, then the factory
  `FUN_14057cc70` creates each building with its town, the player faction, rotation, parent building and floor. No
  terrain is flattened; outdoor buildings with `path mode` and `build threshold` 0 trigger a navmesh rebuild of their
  area (`FUN_1404d2c80` -> `FUN_1403a66c0`, **Observed**). That a new building starts at progress 0 with nothing
  delivered is **Observed** (the factory's complete flag is false).
- **Builders sent on Confirm** (**Verified**, `FUN_1404d72a0`'s tail: `FUN_1407f9e20(player, 0, 2, last building)`; 2 = Build, `FUN_14079b2d0`): after the
  loop, when a non-node building (`is node` clear, created by `FUN_14057cc70`) was made, every selected character gets the plain Build
  order on the LAST such building queued (one order per Confirm, not per ghost; the shift argument is 0, so Shift never makes it a job, and the
  queue is replaced as by a right click, `FUN_1405d20d0`). Any such building, furniture (camp bed) or full building alike; no "free character" or
  distance test. Meitou: `ConstructionSystem.OrderBuild` at the end of `PlaceBuildings`, on the last placed site's root (unlocked or town-blocked
  plans create nothing and are skipped); the bark and material fetch are those of a right-click order.
- **Pause** (**Verified**): entering build mode sets the world's requested pause without touching the speed
  (`FUN_1404e65e0` -> `FUN_140787d40(world, 1)`; paused = request or speed 0). Every exit clears it, so the game runs on
  at the speed the player had: Confirm (`FUN_1404db180`), the Esc handler (`FUN_14082b010` -> `FUN_1404d4320`, when
  `FUN_1404d1860` says the mode is on) and the end of the commit (`FUN_1404e4120`) all end in `FUN_1404d1870` ->
  `FUN_140787d40(world, 0)`. Meitou pauses by speed and resumes only a game that build mode paused.

### Snapping to a building

- **Data** (**Verified**, fcs.def and a dump of the merged base-game data): `snaps to` (BUILDING list) "makes this building aggressively
  snap to others. Used for ramps and stairs." Its users: Wall Ramp and Wall Ramp (old) -> Concrete Wall, Defensive Wall Ramp II-V -> their
  Defensive Wall, Outpost Prison Ramp, Ancient Lab Ramp (Gate), HIVE Door A-C -> Hive Commune Hut, Spotlight and Searchlight -> Node Light
  Mount, GunPlank -> Node Turret, Ore Drill I-III -> Iron Resource (a foliage resource). Which ghosts are `PreviewBuilding_Snapping` and how
  they snap is **Unknown** (not decompiled yet).
- **A ramp's mesh sits in its wall's own space** (**Verified**, mesh bounds): `basic_wall_ramp2.mesh` spans x -102..-20, z 0..24, just
  outside Concrete Wall's `wall_basic02.mesh` (x -19.5..3.9, z -2.7..118.7); Defensive Wall Ramp IV's staircase spans x -268..-49, z
  187..222 against Wall IV (x -59..27, z 0..305; both scale 0.85). At its wall's position and turn a ramp stands against the wall's -X side.
- **Turrets** have no `snaps to`, and none refers to the `Node Turret` instances (node type 3, as `Node Defensive Position`) that wall parts
  such as `Wall Level 02` carry: they stand on a wall's walkway like on any surface (**Observed**, data only).
- **Meitou** (engine choice, `GhostSnap`, `BuildMode.SnapToTarget`): a `snaps to` ghost takes the position and turn of the nearest building
  of a target record (queued ghost, site or level building) that puts its footprint centre within 150 of the cursor; it is judged by its own
  pitch like a wall piece, and neither its target nor, on a wall, any other wall blocks it. With no target in reach it places as usual (the
  "blue nodes" refusal is not decided). Nodes inside other buildings (light mounts) and resources are not searched. A ghost over a raised
  walkable surface outside the rooms (a wall top, `InteriorView.SurfaceMoveHit`) stands on it level, with that height as its ground, and
  walls do not block it.

### Upgrades

A building is upgraded or downgraded in place: same object, its record swapped (**Verified** unless marked: Building slots
75-78, `FUN_140027eda`/`StorageBuilding::vfunc_75`, `vfunc_76`, `FarmBuilding::vfunc_77`/`78`; the panel's buttons
dispatch to slots 75 / 76 through `FUN_140313d50` / `FUN_140313d90`).
- **Upgrade target** (slot 77): the record's first `upgrades to` reference, only when the building has a construction state
  and an owner, and the target is in the research manager's set at +0x130 (`FUN_14082ea60`; taken to be the unlocked
  buildings, **Observed**). **Downgrade target** (slot 78): the first BUILDING whose `upgrades to` names this record (the
  search order over the records is **Unknown**).
- **Upgrade** (slot 75): record := target, the name-dirty byte +0x19f set, complete and paused cleared (+0x160/+0x161),
  progress capped at 50 material units, the new record's construction state applied (`FUN_14054a450`), then each material
  still owed is taken from the building's own inventory, int(required - delivered) units each, one item destroyed per unit, doors given the new record's `doors` record,
  mesh, physics and window rebuilt, an outdoor building's navmesh area rebuilt.
- **Downgrade** (slot 76): record := the downgrade target, +0x19f set, progress capped at **80**; when the state's materials
  already cover it (+0x188 minus `FUN_140549260` <= 0, **Observed** reading) the building is completed at once (slot 0x230
  with FLT_MAX). Otherwise as the upgrade.
- Data (**Verified**, game data with mods): chains on production buildings (Stone Mine I-VII, Iron Refinery I-IV, Research
  Bench II-VI ...), farms (S-XL), turrets, training dummies, walls (Makeshift Wall -> Defensive Wall II ...) and gates
  (Makeshift Gate -> Defensive Gate II, III -> IV -> V); the targets are mostly also `enable buildings` of a research, so a
  higher tier can be placed directly too. Walls have their own slot 75/77 (`WallBuilding`, `wall master`: the whole line).
- Meitou (`UpgradeSite` / `DowngradeSite`, `ConstructionSystem.CanUpgrade` / `CanDowngrade`, chain lookup
  `BuildingUpgrades`): the site's record, name, rate, target and materials swapped, delivered amounts carried over per item
  (engine reading of the re-applied state), complete and paused cleared, progress capped at 50 / 80; a downgrade whose
  delivered materials cover the lower record finishes at once. An upgrade then pulls what is still owed from its own stock
  (`PullOwn`: the machine's storage, then input and output stock). A machine gets a new one for the new record that keeps the
  stock of the same items, storage items that fit, a farm's growth by plant index, a bench's queue and the power switches; the
  rest drops at the building (engine; `ProductionSystem.Rebuild`). An unfinished site's machine is saved and read. Engine scope:
  no walls or wall pieces (gates, `is wall gate`, do upgrade). The doors stay registered while it is built (`BuildSite.Upgrading`, not
  saved: a reloaded unfinished upgrade gets its doors when finished) and keep their state by door index; the building
  leaves the navmesh until it is complete again (engine).

### Walls

Ghost class `PreviewBuilding_Wall` for `links together` buildings; **Verified** unless marked (slots 9/10/12/17/20/21/24/
25 at `FUN_140428920`, `FUN_140428100`, `FUN_140427780`, `FUN_140429510`, `FUN_14042b660`, `FUN_14042b390`,
`FUN_140428fe0`, `FUN_140428570`). Data: `link type` 0 normal, 1 connector, 2 lower wedge, 3 single (towers, gates), 4
short; a piece runs from its position along its own +Z for `link length` x `scale` (base footprints match: Makeshift Wall
155 x 1.1 reaches z 169.7). Subsections get a derived `wall master` ([loader](../formats/fcs-mod.md#game-loader)). The
"left side"/"right side" actors of fcs.def's help text do not occur in the exe (**Observed**, absence).

- **Controls**: click the start, move, click the end (no hold-drag). The first click starts a line for a normal wall, or
  for a single only when snapped (unsnapped singles and selected subsections place as one ordinary ghost). The second
  click queues every piece and, unless the end snapped or the wall is a single, a new line starts at the far end (clicks
  chain a polyline). Right click drops the line; with no line, Meitou's right click on empty ground drops the chosen item (ghost and list highlight, queue stays; original **Unknown**, [level-editor](level-editor.md)). The first click posts "Blue arrows point outwards, towards enemy.";
  arrows (`arrow.mesh`, "bluebuilding", at rot x (0, 30, len/2), turned 180 deg, scale 2) mark each piece; the mesh's tip is at -X (see
  [perception](perception.md#spotting-a-sneaker)), which way it ends up facing is **Unknown**. A global flag `DAT_1421337b8` can block the first click (**Unknown**).
- **Pieces** (`FUN_14042b660`): the wall and its `wall subsections`; connector and lower wedge kept apart, the rest with a
  length sorted longest first. A single is used once per line.
- **Straight run** (`FUN_14042a4b0`): along the horizontal start->end, capped at 10000. Terrain profile every 0.5 x the
  longest length (10 if 0), each point re-placed at that 3D distance along the slope, interior points lowered to
  min(y, 0.25 prev + 0.5 y + 0.25 next). Height offsets so/eo = start/end y minus the profile there; piece end =
  profile point at cur + dir x len plus `eo + (rem - len) / L x (so - eo)`, at 3D distance len - stretch from cur. Piece
  = first with length < remaining + 10 (full pieces, then shorts; overshoot <= 10). A bend > 10 deg to the previous
  piece puts the shortest piece centred on the joint along the bisector (crest, not when the shortest is a single), < -10
  the wedge at the joint (valley). Stops when < 10 remains, past the end, or after 500 pieces; a leftover shorter than
  the shortest piece - 10 stays open. A rising piece (its end higher than the current point) whose pitch from level exceeds the wall's `max slope` (the wall object's +0x88, strictly greater;
  **Verified**, `FUN_14042a4b0`) is not placed: `FUN_14042a2b0` lays pieces of the LONGEST length along the previous piece's direction (3D), for as long as half a piece is less than the distance left (a
  single only once), where the distance is to the first profile point from the cursor on at which the ground stands `building height` (+0x8c) - 10 above the line (the constant `DAT_14167fad4` = 10), else to
  the point of greatest rise; then the run ends (Meitou `WallLine.Straight`). With a snapped end, later frames shorten every piece
  by remaining / count so the run closes on the point (**Observed**).
- **Orientation** (`FUN_1404278b0`): +Z towards the piece's end (pitching with the slope), +X level (Y x Z).
- **Snapping** (`FUN_1404289f0`, `FUN_140427950`): candidates are the queued wall ghosts and every loaded building with
  `links together` (**Verified**, `FUN_1409fe710` -> `FUN_1409fe1e0`: every object of type 0 in each zone of the loaded set,
  no owner or state test, so intact level town walls count; Building slot 4 filters, **Unknown**). Meitou: the queued ghosts,
  the player's sites and the level's walls within 300 of the cursor (`BuildingFootprints.LinkedAround`; every zone counts as
  loaded, a level wall that is a site is the site). Link points: start =
  position, end = position + rot x (0, 0, len); direction = the wall's +Z, a connector's end direction turned 48 deg
  about Y. Points within sqrt(10) count as joined. Search in X/Z, radius 90 (25 inside a building): a free point scores
  d^2 - r^2, a joined one d^2, best below r^2, so free points snap within 127 and win. Codes 1 free end, 2 free start, 3
  joined. A line started on a start (code & 2) runs backwards: each piece is placed from the far point back towards the
  joint. Its end snaps only to ends; a forward line's only to starts (a non-matching best hit is dropped).
- **Snapped joints** (`FUN_140429cc0`): turn = angle from the snap direction to the line; trunc(turn / 60) shortest pieces
  (none for a single) each turned an equal share; connectors at the joint and after each piece only when the line leaves
  on one side (`dot(perp, b) > -0.01` xor backwards, perp = (D.z, 0, -D.x)). Straight runs get no connectors.
- **Validity**: every piece runs the ordinary checks (footprint, slope as its own pitch: `rot x Y` within `max slope` of
  up, `FUN_140429510`); one failure refuses the line, pieces are drawn by their own result. Near a snapped joint (pieces
  within the first/last 120 of the run) walls with a link point within 20 of it do not block; connectors and wedges
  never block (`FUN_140427b00`, **Observed**).
- **Commit**: each piece is queued and becomes its own building and construction site from its own record (`is wall
  gate` -> gate building, else `WallBuilding`). On Confirm and zone load a short/connector/wedge without a parent
  attaches to a nearby wall (`FUN_14029ac50`, links not saved, **Observed**); a child forwards build work and deliveries
  to the parent (slot 69), whose progress sets the children's (slots 71-73), so an attached subsection costs nothing of
  its own (**Observed**). A wall with `build threshold` 0 rebuilds the navmesh when queued (see Commit above).
- **Wall termites: disabled in 1.0.68** (**Verified**: no code adds to the list). The gate-code manager (singleton
  `DAT_14212e368`) drains a list of `TownBase*` (+0x78, count +0x80) after each gate-code pass (`FUN_1402ef1f0`, from
  `FUN_1402f03e0`): per town, the nearest building is destroyed (`destroyed navmesh`) or dismantled, with the message "Wall
  termites! - Your base became inaccessible.  You need at least 1 gate in your walls.". The only code that references the
  singleton is its constructor, getter, destructor, `FUN_1402eea40` and the drain itself, and none of them appends, so the
  list stays empty (KenshiLib 1ce4a16's `Town::notifyAccesibility` has no 1.0.68 body). Meitou does not build it.
- WallBuilding damage `clamp((hit1 x 2 + hit0 x 0.5) x 0.5, 10, 60) x 0.003` off health (slot 62, **Verified**).

### Build menu

**Verified** (`FUN_1406bff30`, `FUN_1404e4480`, `FUN_1404e4210`, `FUN_14082ea60`, `FUN_140830b40`): every BUILDING is a
candidate; category = `building category` (default "misc"), group = `building group` or the building's own name.
A building is offered when it is in the research manager's unlocked set, filled from each completed RESEARCH's
`enable buildings` (or the window's debug flag +0xa8 is set); a category shows when one member passes. In the base game
88 buildings are unlocked by research; categories DOORS, sign, nodes, moor, CANNIBAL, EXTERIOR, Furniture and misc have
none and never show. A new game starts with the research `_Default Start` (`5359-gamedata.base`) completed (**Verified**: the exe
string; **Observed**: `finished0` of the five sample saves); Meitou adds it to every new game (`ResearchGate`). Order (**Verified**, `FUN_1404e4480`, `FUN_1404e4210`, comparator `FUN_1404d6060`): categories by name in byte order (a `std::map` of names), groups by name in byte order within a category, and a group's offered members taken from the last in the building list to the first and sorted by `build materials` descending (equal ones keep that reversed order, an insertion sort); the default pick is the first of them (`BuildMenu.Groups`). The Commands text stays **Unknown**.
Stats panel (`FUN_1404df450`): "Materials cost:" (each `construction` item and amount, or Building Materials x
`build materials`, or "Nothing"), "Estimated build time:" (formula above, whole hours), then power output/consumption,
`power capacity`, Num People (`max operators`), Produces (`produces`, `output rate`), Efficiency, Damage, Training max
and Turrets bonus (their formulas **Unknown**).
Opening the menu pauses the game (a normal pause, the speed buttons resume) and closes the inventory windows
(**Observed**, in-game).

## Classes and functions

The factory `FUN_14057cc70` picks the class from the functionality's `function` (**Observed**): `Building` ->
`UseableStuff` -> `StorageBuilding` -> `ProductionBuilding` -> `FarmBuilding`, `CraftingBuilding`, `FurnaceBuilding`,
generators, `RainCollectorBuilding`; `ResearchBuilding` beside them. `BuildingFunction` (**Verified**, editor enum `BF_*`):
0 ANY, 1 MINE, 2 RESOURCE_STORAGE, 3 RESEARCH, 4 REFINERY, 5 GENERATOR, 6 BED, 7 TRAINING, 8 CAGE, 9 SHOP, 10 CRAFTING,
11 CORPSE_DISPOSAL, 12 TURRET, 13 GENERAL_STORAGE, 14 ITEM_FURNACE, 15 LIGHT, 16 TABLE, 17 CHAIR, 18 FLUFF,
19 SHELL_WITH_INTERIOR, 20 WALL, 21 GATE, 22 DOOR, 23 BATTERY, 24 THRONE, 25 SKELETON_BED, 26 RAIN_COLLECTOR,
27 MINE_NATURAL, 28 STEERING, 29 ENGINE, 30 LIQUID_TANK. `MiningResource` (**Verified**): 0 NONE, 1 IRON, 2 STONE,
3 COPPER, 4 CARBON, 5 WATER, 6 GROUND. A farm is function 1 with `world resource mining` 6 and a BUILDING `farm data`
reference (FARM_DATA); a well is function 1 with resource 5 (**Verified**, probe: `farming wheat`, `well`).

**Corpse machine label** (**Verified**, `Task_FeedCorpseIntoMachine` slot 3 `FUN_1403567a0`, disassembly; floats read from the exe): each time a body is fed in
([job 136](ai-tasks.md)) and the task's target hand is a BUILDING (type 0) that resolves, the building's functionality (`UseableStuff` +0x400, KenshiLib 1ce4a16) `string`
field, when not empty, floats as a [damage floater](ui-screens.md#8-damage-floaters) label: colour (0.7, 0.7, 0.7, alpha 0.8), font index 0 (`Kenshi_FloaterFont_Small`),
rise index 1 (4 units/s, gone after 3 s), anchored at the building's position + (0, 5, 0). The text is the data field as loaded (`gamedata.pot` lists it for translation): vanilla
`Cooking pot` (`2678-gamedata.base`) "Cooking!", `Corpse disposal` (`4004-gamedata.base`) "Incinerating", `Corpse disposal- shredder` (`1531904-rebirth.mod`) "Shredding"; no other
BUILDING_FUNCTIONALITY in the install sets `string` (**Verified**, byte scan of `gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`).
Meitou: `ProductionSystem.Incinerate` queues it as a `FloaterKind.Label` floater, so it shows only while damage floaters are on (engine choice).

**Constants** (**Verified**, CONSTANTS loader `FUN_14086b2b0`): `production speed` P = 2, `research rate` 0.3,
`research level increase rate` 1.3. G = the day length at speed 1, 2618.18 s (`FUN_14066cd50`, the exe's float); per-day
data rates are divided by G, per-hour by G/24. dt = the scaled frame time. The option sliders (production `142133590`,
research `14213358c`) default **Unknown**; 1 below.

## Production

**Per operator and frame** (**Observed**, `ProductionBuilding::vfunc_150` `FUN_140298700`, called by
`Task_OperateMachine` `FUN_14035bc90` with the operator's skill factor s):
`amount = yield x outputRate x upgrade x dt x P x health x powerSat x s x option`. Several operators add up.
Neither it nor the output add (`vfunc_184` `FUN_1402984b0`) tests the owner. An operated building (the player's, an NPC town's or a
rock) makes output only while someone operates it.
- **Another faction's machine** (**Observed**, decompile): the player may operate it. The menu offers 87 / 152 on it unless it is `destroyed` (+0x1a1,
  KenshiLib 1ce4a16; [ui-input](ui-input.md#context-menu-verbs)); `Task_OperateMachine`'s start (`FUN_14035b430`) only asks `tryOperate` (slot `0x4f8`:
  `UseableStuff` `FUN_1400f8030`, `CraftingBuilding` `FUN_1402b5bf0`: refused when `dontNeedWorkRightNow` or every operator slot is taken), no owner test;
  for a foliage building (a rock) of no player owner it then makes the player's faction its owner. The output stays in the machine, so taking it is looting
  another faction's inventory (26). Meitou: [NPC town machines](#npc-town-machines).
- **Automatic machines** (**Verified**, decompile): +0x3ac (`numOperatorsMax`, KenshiLib 1ce4a16) is the BUILDING's `max operators`
  unless below 0, then the functionality's (`FUN_1402a3750`, `FUN_14029e460`); +0x3a9 "has operators" = that value is neither 0 nor from two negatives.
  With +0x3a9 clear (raw 0, or both records negative), `ProductionBuilding::vfunc_28` (`FUN_14029ad90`, each frame) calls slot 150 itself with no operator and s = 1, so the machine
  works unattended (`ResearchBuilding::vfunc_28` does the same). Meitou: `MachineData.Unattended` (no base-game pair is negative on both, **Verified** over the
  base records, so mods only). Only raw +0x3ac = 0 (not the both-negative case) gives the menu order 152 Auto-Haul instead of 87
  ([ui-input](ui-input.md#context-menu-verbs)). Base game (**Verified** data): Iron Refinery III / IV, Cotton and Hemp Auto-Loom,
  Automatic Grain Silo, Hybrid Stone Mine II (building 0); Campfire, Camp Fish Pile, Bread Oven, wind generators, Rain Collector,
  Battery Bank, storages and lights (functionality 0).
- s = `lerp(stat/100, 0.5, 1.4)` of the effective stat `stat used` (`FUN_1408852a0`; 1 without an operator; the lerp
  helper `FUN_140015b63` is `a + (b - a) t`). **Verified**: no clamp, so a stat above 100 lifts s past 1.4 (`FUN_14035bc90` passes it
  unchanged to the work slot 0x4b0). A farm's own efficiency roll keeps its clamp to 0..1.
- outputRate = BUILDING `output rate` x functionality `production mult` (research benches too), with `output per day` / (2G)
  (production buildings only, `FUN_14029e7b0`), then / the raw `max operators` (+0x3ac) when above 1, for every function
  (**Verified**, `FUN_14029e460`, `FUN_14029e7b0`, `FUN_1402b9aa0`). Each operator adds its share, so a full crew makes
  `output rate x production mult x P/2 x s` cycles per game day; a building whose own `max operators` is 0 is never divided.
- yield (slot 120 via offset `0x46c`): 1 without `world resource mining`; else the resource at the position
  ([resources](#resources)), at least 0.1 for a natural site; for any other building 0 becomes 0.5 when it is not the player's (**Verified**, `FUN_14029c670`: the natural branch takes `FUN_140a12ff0` and the 0.1 floor, the other `FUN_140a13540` and the 0.5 rule).
- health x powerSat (slot 109, `FUN_140296e00`, **Verified**): health the [construction state](#construction-state)'s progress / target capped at 1; powerSat
  the granted share of the demand, 1 with no demand. Meitou: `Machine.Health`, 1 for rocks and a target of 0.
- upgrade = the research `production mult` of the building ([research](#research)), default 1. The upgrade lookup (`production mult`,
  `power increase`, `power capacity increase`) answers only for the player's buildings (owner with the player pointer +0x250, `FUN_140546dd0`);
  any other gets the default 1, 0, 0 (**Verified**, `FUN_14082ec70`).

**Recipes** (**Observed**, loader `FUN_14029e7b0`). Each `consumes` entry (item, v0, v1) is an input: section `in<k>`
of the building's inventory limited to v0, a level float (starts 0) and a rate v1/100. Each cycle the inputs lose
`amount x rate` from their level, refilled by taking one item of the section (+1 level each, slot 180). The `produces`
first entry is the output: section `out` of the item's footprint, capacity v0 items, a progress float. `vfunc_185`
clamps amount to what the inputs hold and to `capacity - held - progress`; nothing left: status 1 (output full) or 0.
Else status 2, inputs drawn, progress += amount; each time progress >= 0.999 one output item is added and progress is 0
(`vfunc_184` `FUN_1402984b0`). Example: well (Water 10, production mult 48, `output rate` 0.75) at s 1: 36 water/day,
holding at most 10. Save (`vfunc_17`/`18` `FUN_1402a1fa0` / `FUN_1402a23a0`, **Observed**): floats `production amount`
(output progress), `resource mult` (yield), `mining resource level` (the yield again, written only for a building that is
not a natural site, has a functionality and passes the owner test of [resources](#resources); 0 occurrences in two saves),
`consumption amount-<k>` (input levels); the items are in the building's INVENTORY_STATE.

**Held tool** (**Observed**, `Task_OperateMachine` set-up `FUN_14035b430`): when the task starts, a functionality with a
`special tool` reference gets that item made and attached to the operator's `hands` point (`Bip01 Prop2`); the
`special tool` records are ITEMs with a `mesh` in Newwworld.mod: Pickaxe (mines, rocks) and Hoe (farms). The attach is
skipped when a virtual test on the character (`+0x30`) fails (**Unknown** what it tests; Meitou skips animals). Meitou
shows it for the whole operate task, the walk there included, and drops it when the task ends (the original's removal
was not traced, **Unknown**).
Operator animation: the functionality's `animation` reference (**Verified**, probe of vanilla BUILDING_FUNCTIONALITY); its `animation events` (the pick hitting ore, stone, a plate)
sound on that clip ([audio](../audio.md#characters-footsteps-combat-voices) "Machine work"). No
farming clip exists: cactus, cotton, potato, rice and veg farms use `stonemine` (the mining swing, with the Hoe); `farming
wheat` and `farming hemp` have none (what the original plays then is **Unknown**; Meitou falls back to `stonemine`). Whether
the original plays it while the crop grows is moot (**Observed**): row 87 needs state 73 `machineHasFreeOperatorSlot`, whose evaluator `FUN_140598de0` is **Verified** false while the machine's
status `+0x468` is 3 (a growing farm; a bench with an empty queue too), so the planner finds no way to operate a growing farm. Meitou lets an operator tend it with water and plays the clip (engine choice).

| function | examples (production mult) |
|---|---|
| 1 MINE | Ore mine 64, Stone mine 128, copper mine 72, well 48 |
| 1 MINE (farm types) | farming wheat 64, veg 64, cactus / cotton / hemp / potatoes / rice 48 |
| 4 REFINERY | Campfire 192, grain silo 72, Iron / Steel / copper plates 48, Rum / Grog / Sake / bread / drugs 24, chainmail and plates 12, Textiles cotton 32 |
| 5 GENERATOR | Power 0.00125, wind power 1 |
| 10 CRAFTING | most benches 1, cooking general 2 |
| 14 ITEM_FURNACE | Furnace 0.25 |
| 23 BATTERY | battery 0.00125 |
| 26 RAIN_COLLECTOR | 48 |

### Blocked output

Status `+0x468` of a `ProductionBuilding` (production, furnace, generator, wind, rain, torture; benches and farms reuse the field with words of their own).
- **Values** (**Verified**, panel `FUN_140300090`): 2 ready or working, 1 output full, 0 nothing to work with (an empty input), 3 a bench with an empty queue or a growing farm.
- **Start / save** (**Verified**): the constructor `FUN_1402a3750` sets 2. Not saved (`FUN_1402a1fa0` / `FUN_1402a23a0` write only `production amount`, `resource mult`, `mining resource level`, `consumption amount-<k>`), so a load starts at 2.
- **Set** (`operate` `FUN_140298700`, **Verified**): when the clamp `vfunc_185` `FUN_14029a510` leaves amount above 0, status 2 and the inputs are drawn; else, with an output, status = 1 if the output section cannot take one more item, else 0.
- **Cleared** (**Verified**): `ProductionBuilding::vfunc_28` `FUN_14029ad90` each frame, only for a machine with operators (`+0x3a9`): 1 becomes 2 once the output has room (capacity below 1: the section takes one; else held below capacity), 0 becomes 2 once no input is empty (slot 149
  `FUN_140298b70`: a rated input with a recipe item, level at most 0.001 and, **Observed**, none of the item in the section: the section test's second argument is lost in the decompile). An automatic machine only runs `operate`, which recomputes it. `Task_EmptyMachine` (`FUN_14033cd10`) also sets 1 to 2 right after emptying, only when the building's function class (vslot `0x358`) is 4.
- **What stops** (**Verified**): progress and consumption (amount 0, no item made). Not the operator: `Task_OperateMachine`'s update (`FUN_14035bc90`) asks `tryOperate` (slot `0x4f8`, `FUN_1400f8030`), which refuses only on slot `0x4e8` (`FUN_1402ab3c0`, a constant 0 for every production class but the farm, whose `FUN_1400f6e00` refuses a growing crop), so the animation and XP go on while blocked; the power demand (`FUN_140299630`) does not read the status either. What takes the operator away is **Unknown** (the planner's re-run).
- **Readers**: the panel word ("Output full" 1; "Out of resource" 0 with slot 149 true; "Production queue empty" 3; then No power, No workers, Working); the rain collector's text; planner states 62 / 63 / 64 below; `FUN_14059ab90` (a building finder over status 0 or 1, caller **Unknown**).
- **Planner states** (evaluators **Verified**, hand type 0 = building): 62 `FUN_1405974e0` = slot 149. 63 `FUN_1405a2ba0` = slot 175 (`+0x578`): production family `FUN_140296fb0` (output object `+0x448` and status 1), `StorageBuilding` false; the farm
  `FUN_1400deed0` also needs float `+0x51c` below `+0x54c` (meaning **Unknown**), the bench `FUN_1402b5320` a list test (**Unknown**). 64 `FUN_1405a2e60` = 62 or 63 for a building hand (any other hand: 63 only). 65 `FUN_1405a2f00` = the 64 test over a hand list at `FUN_1407956a0(AI+0x2f8)+0x58` (**Unknown** list).
  105 slot 176 = output section empty; 106 slot 174 = an input at capacity. Rows asking 64 / 65: 91 (A !64), 90 (A !65, effect !64), 89 (B !65), all `Task_Blank` with no `AI_TASK` enum in the base data (`gamedata_base.txt`, **Observed**): no data starts that chain.
- **Operator plan** (**Observed**, TaskData table, providers by list A): row 87 needs !63 and !62. As a job's root it is skipped while they are unmet ([perma-jobs](ai-tasks.md#perma-jobs)). Reached as a provider (Worker 189 / 190 need 123, which 87 achieves), the search expands
  an unmet !63 into rows 92 Collecting resource (`Task_EmptyMachine`; A: 66, !63, !169) and 113 (A: !63, 105, !66, !89), which achieve it: the worker takes the output into its own inventory (B 82 room; 114 ditching makes it) before operating.
- **Crafting** (`FUN_1402bcac0`, **Observed**): after a finished item is stored, status 1 when the output has become full (not when it could not be stored).

Meitou: `Machine.Status` (hashed, not saved, a producer starts at 2), `HasEmptyInputs`, `OutputBlocked`, `Jammed`; `ProductionSystem.ClearBlocked` (its serial step) clears 1 / 0 for a producer that takes operators, an operator's or automatic machine's step sets them. The panel reads the word chain
(`ProductionStatus`). Planner: `Holds` evaluates 62 / 63 / 64 for a `Targets.Machine` target, which no finder gives yet; 65, 105, 106, the farm and bench variants and the 87 to 92 plan are not modelled (the job system's `HasWork` / `Room` is the engine reading of the blocked start).

### Item furnace

`FurnaceBuilding` (function 14; KenshiLib 1ce4a16 names, bodies checked in our exe). Base data (**Verified**, probe): Item Furnace (`3959-gamedata.base`, research Weapon
Melting), functionality Furnace: `max operators` 0 (so automatic: `ProductionBuilding::vfunc_28` runs it with s = 1), `production mult` 0.25, `produces` Raw Iron 2000, no `consumes`.
- **Grid** (**Verified**, `setupFromData` `FUN_14029f980`): with no `consumes` it adds one input, section `in1` (bytes at `141694a78`), 12 x 7, no slot limit, stacks at least 10
  (`stackingBonusMin` 10, mult 1), level 0, rate 1. Slots 183 / 184 / 185 (input and output updates, rate limit) are empty, so the producer's draw does not run.
- **Active** (`incinerate` `FUN_140299790`, `+0x490`, **Observed**): the panel's button. Off with an empty grid: nothing. Off otherwise: on, level 1, output progress 0. On: off,
  level and progress 0, the items stay. The grid's section is disabled while on (`InventorySection::setEnabled(!active)`), then slot `0x318(active)`.
- **Burn** (`operate` slot 150, `FUN_140299fa0`, **Observed**): only while on, with an output and an input; state 2; amount as [production](#production) (x slot 167 when `+0x3bc` > 0:
  `+0x3bc` is the BUILDING's `power output`, made positive when `power capacity` > 0, and slot 167 is the generator's load share `GeneratorBuilding::vfunc_167` but 1 for the
  furnace's class (`FUN_1400f6d60`), so the term changes nothing; **Verified**, decompile of `FUN_14029e460` and the vtables); level -= amount, progress += amount; at level <= 0.001 or progress >= 0.999 the output gets (int) the grid's metal of
  Raw Iron (one `addItem` of that quantity), the grid is emptied (`clearAllItems`) and the furnace turns off. So a whole load burns in 1 / (0.25 x P x option) seconds at full health, whatever it holds.
- **Metal** (`getInputValue` slot 180, `FUN_1402a5f40`; constants **Verified** by disassembly): ITEM with `ingredients`: iron x quantity x 0.5; ARMOUR of `material type` 2 or 3
  (chain, plate): total coverage x 0.01 x (1 - `fabrics amount`) x 2, other armour 0; WEAPON: `getItemWeightSingle` (`+0x120`) x 0.2, within 0.5..5; anything else 0.
  Iron (`getIronAmountInItem` `FUN_1402a5750`, **Verified** by disassembly): over the record's `ingredients`, Raw Iron (`1866-gamedata.base`, a literal) adds its v0 x 0.01, any other
  ingredient its own iron, recursively, not times its amount. Total coverage (`FUN_14082fd80`, **Observed**): the run-time int `total coverage` = the sum of the armour's
  `part coverage` v0, the entry `101-gamedata.quack` x 1.5 (truncated), 100 when 0.
- **Panel** (`getGUIData` slot 40, `FUN_14030b750`, **Observed**): section 0xf is its own, every other section the producer's. `Total Metals:` int(metal x 10) x 0.1, Good from 1,
  else Bad; `Active:` a `Kenshi_Button2` line `OFF` (Main) / `ON` (Good), tooltip "Warning:" "Switching on will incinerate the entire inventory!".
- **Hauling**: `canHaveSomeOfThese` (slot 148, `FUN_14029cbc0`) accepts only `item function` 17 (severed limbs), so the AI's haulers bring it nothing else. Default task 26 LOOT_TARGET.
- **Save**: no override of the producer's keys, so `active` is not saved (a loaded furnace is off); the level is `consumption amount-0`, the progress `production amount`.

Meitou (`ProductionSystem.Furnace.cs`): the grid is the machine's `Storage` (saved as section `in1`), `Machine.Burning` / `BurnLevel`, the `IncinerateFurnace` command and the panel rows
on the player's furnace; its window draws the grid in the `in1` slot (disabled while on); engine choices: the window grows to fit the grid (original sizing **Unknown**), the input label and bar are left empty, a weapon's weight is its inventory unit weight, a burning furnace whose output cannot take the load's iron holds with status 1 (output full) until there is room (the original's `operate` has no such test), the status returns to 0 when it stops, the button plays `General_Click`.

### Resources

(**Observed**, `uses_world_resource_mining` `FUN_14029c670`, `uses_resources` `FUN_140a13540`,
`FUN_140a12ff0`.) The BIOME_GROUP at the position (areas map) names an ENVIRONMENT_RESOURCES record (`resources`);
field prefixes by MiningResource: `iron`, `stone`, `copper`, `carbon`, `water`, `farming`.
- WATER and GROUND: band = 1 between `<p> altitude min` and `max` (height y), ramping to 0 over `<p> altitude fade`
  below the min and above the max, else 0; value = band x `<p> mult` + `<p> min`.
- STONE: the coverage noise of [foliage](../formats/foliage.md) (`Foliage_NoiseHashC`, five octaves) at scale `stone noise
  zoom` x 1000, cutoff `stone noise cutoff`, boost 3 and cap 1 (**Verified** bytes `141f12ce0`/`ce4`), x `stone mult`.
- IRON, COPPER, CARBON: the nearest rock of that resource within 1500 (3D, squared 2.25e6): its yield x (1 - f),
  f = clamp((d² - r²) / (1500² - r²)), r = the rock's mean horizontal half-size, at least 25 (Meitou always 25: the term
  moves the result under 0.5% for rocks up to 100). No `resources` record or no rock: 0.
- Rocks are BUILDINGs with `is foliage` placed by the foliage system as a FOLIAGE_MESH's `building type` ("Iron
  Resource", "Copper Resource", "Stone Resource"; functionality 1 with the resource, not 27). Level data also places
  `is foliage` buildings (1241 "Iron Resource" entries in the merged building lists, **Observed**). A rock is a natural site
  (Building `+0xc0`): its yield is `FUN_140a12ff0` where it stands, at least 0.1 (`FarmBuilding::vfunc_92` returns it):
  WATER/GROUND as above, STONE the stone noise x `stone mult`, else the resources record's `<p> mult` (0 when missing).
- **Rocks are mined like mines** (**Verified** data, gamedata.base): the rock BUILDINGs reference the player mines'
  functionalities (Iron Resource → `Ore mine`: Raw Iron, output 5, `production mult` 64; Copper Resource → `copper mine`:
  Copper, output 5, 72; both `stat used` 3, `stonemine`, special tool Pickaxe, `output per day`) with their own `output rate`
  (1.2, 1, rebirth.mod over the base's 0.45, 0.4), `max operators` (3, 2) and `build materials` (2, 3). So hand mining is `Task_OperateMachine` on the rock (**Observed**); the ore waits in
  the rock's output until the player takes it (**Observed** by the owner in play; no rock-specific transfer in the operate update). No
  rock has a saved state in two saves (253 zone files, **Observed**). Owner: `nofac` until a player character starts working it, then the player's
  faction ([selected building](ui-screens.md#selected-building), Rocks).
- A building that is not the player's (`FUN_140546dd0`: its owner's `+0x250`, the test also used by research
  upgrades and generator fuel) and is no natural site yields 0.5 where it would get 0; the player's yields 0 (Meitou: `ProductionSystem.Create`).
- The farm efficiency also reads the record's `arid`, `green`, `swamp` coverages (crop types 2, 1, 3).
- The prospecting window lists water, fertility, stone, iron and copper from `uses_resources` ([ui-screens](ui-screens.md#prospecting)).
- **NPC mines** (**Observed**, two saves of one game): NPC towns' mines have production states; Quarry Mine A 14 of 16,
  Quarry Mine B 16 of 18 and Stone Mine 8 of 8 with `production amount` > 0 (at most 0.19), none holding output items; Ore
  Mine 0 of 8; Ore Drill III (Automated) `resource mult` 0.5 (the not-player fallback). So NPCs work them (the town goal 43
  Staying home leads to 221 operating, [ai-tasks](ai-tasks.md#town-goals)). What empties an NPC mine's output: [NPC town machines](#npc-town-machines).

- **Rain collector** (function 26, `RainCollectorBuilding`, vtable `1416eca48`; **Verified**, decompile): a ProductionBuilding that overrides slot 109 (health x power share, `FUN_140296e00`)
  with `FUN_140298470` = that times the rain strength `FUN_140298400`: the current weather's `wetness` at the building (when the building is outdoors), 0 when the weather's
  `affect type` is WA_ACID. So an automatic collector (`max operators` 0) makes its `produces` only in rain, in proportion to the wetness (Meitou: `Machine.Rain`). Its state text (`FUN_140301410`): "Rain is being collected." / "It is not raining. Nothing to collect."; stat row "Rain strength: {int(wetness x 100)}%" (`FUN_14030b460`); Meitou does not show them.

## Crafting

Function 10, `CraftingBuilding` (**Observed** unless noted).
- **Lists.** The functionality's `item crafts` (v0 = finished items the output holds), `armour crafts`, `weapon crafts`
  (MATERIAL_SPECS_WEAPON models), `crossbow crafts`, `backpack crafts`, `robotics crafts`. A weapon bench crafts every
  WEAPON in the `PLAYER_WEAPONS` manufacturer's `weapon types` in each listed model; the model's level is that
  manufacturer's `weapon models` v0 (Rusted Junk 5 ... Edge Type 3 80). `tech level` caps the made level (0: none).
- **Queue item** (`FUN_1402b5ca0`): seconds = hours x G/24, progress, critical state (2 not rolled, 1 critical, 0 none).
- **Tick** (`CraftingBuilding::vfunc_150` `FUN_1402b5a40`), state 2 with a queue: amount = `output rate x production
  mult` (/ `max operators` when above 1, `FUN_1402b9aa0`) `x health x s x option x P x dt / seconds`. The critical is
  rolled at the item's first work: random < chance. Inputs drawn `amount x per-item` as for [recipes](#production);
  progress 0.999 makes the item (`FUN_1402bcac0`).
- **Inputs** (`vfunc_182`): section k takes ingredient k at `ingredients` v0/100 x material cost; with `overrides
  ingredients` or no ingredients, the bench's `consumes` at v1/100. Material cost (item vfunc 102): a weapon's
  `material cost`, else 1. Armour: each section at `total coverage`/100; Fabrics (`584-gamedata.base`, hard-coded,
  **Verified** bytes) at that x `fabrics amount`.
- **Partial levels** (**Verified**, `FUN_1402bb470`, `FUN_1402b8440`): the bench keeps a map item -> level (+0x520). A recipe switch first stores every
  section's level under its item, then each section taking an ingredient gets `map[item]` (0 when absent), so a half-used ingredient comes back when
  the recipe does. A save stores the sections' levels in the map, then writes each entry above 0 (k counts written entries only); a load fills the map
  (sids not in the game are skipped). The sections' own levels load from `consumption amount-<k>`. Meitou: `CraftState.Partial`, `ProductionSystem.Bind`.
- **Hours:** ITEM `production time` (default 4); crossbow / robotic limb `craft time hrs` / P (defaults 20, 10); weapon
  (`FUN_1402b76f0`) clamp(q² x 100 x `value`/300 x 2/P, 2, 32), q = level/100; armour (`FUN_140898e90`) coverage/100
  x lerp(q, 1, 5), at least 1, then hat x0.6, legs x0.45, shirt x3, boots x0.5, chain (`material type` 2) x2. Constants
  **Verified** by the bytes. `total coverage` (`FUN_14082fd80`): sum of `part coverage` v0, chest `101-gamedata.quack`
  x1.5 truncated, 0 -> 100.
- **Quality.** Armour (`FUN_14082e6b0`): tier from the int skill (20, 40, 60, 80 -> 1..4), +1 on a critical, at most 5;
  quality by tier 5, 20, 40, 60, 80, 95 (**Verified**, jump table). A robotic limb (type 111) or crossbow takes the same roll as
  its `level` (`FUN_1402bcac0` passes it to the item factory, whose limb and crossbow branches hand it to the Gear constructor;
  **Verified**, decompile); Meitou caps it at `tech level` as for armour. Critical chance (`FUN_1402b5900`): (skill -
  threshold)/50, none below 0; threshold for stats 8 and 38 the floor of the skill's tier (80/60/40/20, else 5), else
  the item level. A weapon whose level is above the crafter's skill, with no critical, comes out as a lower model
  (`FUN_1402bcac0` -> `FUN_1402bb020`, **Verified**: the player-weapons maker's highest model level at or below the smith's level and the queued weapon's level whose model the bench lists
  in `weapon crafts` and research has enabled; Meitou: `ProductionSystem.WeaponModel`, the bench's lowest model when none qualifies where the original takes a default record, **Unknown**). A critical on a
  weapon below the smith's level takes a model from `FUN_1402bb2b0` of min(item level, skill) (not read; Meitou keeps the queued model).
- **Research** `enable item`, `enable armour`, `enable weapon type`, `enable weapon model`, `enable crossbow`,
  `enable backpack`, `enable robotics` unlock crafts, v0 = the repetition that does it; `repeats` and `repeat mult`
  ([Research](#research) repetitions). Whether a craft no research lists is available from the start is **Unknown** (Meitou: yes).
- **Save** (GAMESTATE_BUILDING, `vfunc_17`/`18` `FUN_1402b8440` / `FUN_1402b8ab0`): float `num crafts`, bool `repeat`,
  per queued k strings `current<k>` (item) and `currentExtraData<k>` (weapon model), float `current prog<k>`, int
  `crit<k>`; partly drawn items as strings `craft partial item sid <k>` (item id), floats `craft partial item val <k>` and int `craft partial item size`
  (**Verified**, `FUN_1402b8440` / `FUN_1402b8ab0`): see Partial levels.
- **UI:** overview CraftTab (`CraftingStationsList`, `QueueItemsPanel`, `CraftQueue`, `QueueRepeat` "Repeat Queue",
  `QueueDescriptionPanel1/2`); the building window's `CraftingPanel` (`CraftingName`, `QueueButton` "QUEUE..."); its rows and slot ghosts: [Selected building](ui-screens.md#selected-building); the description of a hovered queue entry (State, ETA, Current Smith, Predicted Result, Material Costs, Critical Success Chance): [Craft tab](ui-screens.md#craft-tab).
- Example: Weapon Smith 2033, functionality 2035 (stat 7, tech level 25, consumes Iron Plates 5/100).

## Farming

`FarmBuilding` (ctor `FUN_1400e8420`, tick `vfunc_28` `FUN_1400e5580`, harvest `vfunc_150` `FUN_1400e59e0`, save
`vfunc_17`/`18`; all **Observed** by decompile). FARM_DATA fields (**Verified**, probe; Wheat Farm 25): `amount` 25
plants, `growth time` 22 h, `harvest time` 48 h, `death time` 8 h, `drought death time` 48 h, `drought multiplier` 0,
`death threshold` 0.4, `consumption rate` 2 /day, `harvest rate` 256 /day, `clear rate` 500 /day, `output per plant` 1,
`fertility effect` 0.25, `minimum fertility` 0, crop weights `arid` 0.5 `green` 1 `swamp` 0, `inside`; layout `spacing`,
`jitter`, `aspect`, `layout`, `plants` (FARM_PART meshes, see [farm plants](#farm-plants)). The farm has
the [layout's plant count](#farm-plants), not `amount`.
- **Setup**: water rate w = `consumption rate`/G x the biome group's `farm water usage` (Desert 8, most 1, Floodlands 0.1);
  harvest and clear rates / G; harvest time at least 1e-6; indoor = `fertility effect` 0; every input's rate is 1.
  Fertility factor F = `1 - (1 - f) x fertility effect`, 0 when f <= `minimum fertility`, f the GROUND resource
  (`FUN_1400e4320`, `vfunc_91`); F is also the yield. Both the water usage and F apply to indoor farms too, with no indoor branch
  (**Verified**, `FUN_1400e8420`): their effect 0 gives F = 1 unless the ground is at or below the minimum (the shipped indoor
  FARM_DATA have `minimum fertility` -1, so never). The biome crop terms of the crop match C (`FUN_1400dfe10`) and the rain top-up read where the farm stands instead (slot 59 `isIndoors`: interior furniture inside a building, **Verified**), not `fertility effect` 0, which is a separate byte: a hydroponic farm standing outdoors takes the biome match and rain; Meitou `Machine.Indoors`, set at creation and by the power tick. The ghost's popup keeps the flag (the ghost's parent is not read there, **Unknown**).
- **Plants**: each has a growth g. Replant (`FUN_1400e48e0`): every g = -0.2 x U[0,1), `plant start` = their minimum,
  `plants harvested` 0, death d 0, clear progress 0, harvest progress 0, farm growth = `plant start`.
- **Tick**, h = dt/(G/24): while the farm is not inside a building (slot 59 `isIndoors` null, **Verified**; or the byte at `+0x240` is set, meaning **Unknown**, treated as unset; the farm's own indoor flag, fertility effect 0, is not read, so a hydroponic farm standing outside is rained on; Meitou `Machine.Indoors`), rain fills the FIRST input's level by
  `wetness x w x rate x dt x 3` (to 1 at most; **Verified**: `FUN_1400e5580`, the 3 at `14168a254`). `wetness` is the weather record's `wetness`
  (0..1; the def field `+0x94` of the region's current weather at the farm, loader `FUN_1409de7b0`), not `rain intensity` and not times the weather's strength;
  any weather counts, acid included. Meitou: `WeatherHere.Wetness`, `ProductionSystem.FarmTick`. Water draw
  (`FUN_1400df4c0`): an input whose level is below `w x dt` takes one item from its section, or fails (level 0); then
  level -= `w x dt`. Growth step `inc = h x F / growth time`, x `drought multiplier` when the draw failed, x health x
  powerSat when the building uses power. Farm growth < 1: each plant with -1 <= g < 1 gains inc; farm growth = the
  lowest such g, capped at 1. 1 <= growth < 2: growth += h / `harvest time` (ripe; at 2 it rots). Growth >= 2:
  d += h / `death time`. While 0 < growth < 1 and `drought death time` > 0: a failed draw adds h / `drought death time`
  to d, a good one takes it off while 0 < d < 0.6 with no floor at 0 (**Verified**, `FUN_1400e5580`; one step can leave d below 0, nothing clamps it later). d >= 1 or every plant harvested: replant.
- **Harvest** (an operator, skill factor s): when growth >= 1, d < `death threshold`, not every plant harvested and the
  status is not 1, harvest progress += health x powerSat x P x dt x s x harvest rate (no room test yet). At 1 the output
  section is asked for room for the item (**Verified**, `FUN_1400e59e0`): none sets status 1 and keeps the progress (the tick keeps 1 while the output
  cannot take the item, else resets to 2; the work function does nothing at 1). With room, ceil(`output per plant`) rolls give one item each with probability e (a do-while: 1 to 2 rolls
  for an output of 1 or 2; 0.5 gives one e-roll), then the fraction `output - int(output)` is rolled once against itself; the progress resets to 0, the plant's g = -10, `plants
  harvested` += 1; the work point moves to the next plant. A crop of zero items floats "Failed".
  e (`FUN_1400dfe10`) = `clamp01(lerp(farming/100, 0.5, 2))` x C x F, C = sum of the farm's crop weights x the biome
  coverages (1 indoors), farming the operator's effective stat (**Verified**: the 0.01 at `141682ad8` and the call order).
- **Clearing**: d >= `death threshold`: clear progress += health x powerSat x P x dt x s x clear rate; at 1 one plant is
  cleared as if harvested.
- **Status** (`+0x468`): 3 growing with water, 0 no water, 2 harvestable or dead, 1 output full.
- **Save** (GAMESTATE_BUILDING, after the [production keys](#production)): floats `plant growth` (farm growth), `plant death` (d),
  `plant start`, `save time` (game hours); int `plants harvested`. A load with more than an hour since `save time`
  simulates the gap (`FUN_1400df6e0`); with no farm keys, growth is random (`FUN_1409b1c10`).

### Farm plants

**Observed** by decompile unless marked: layout `FUN_1400e6230`, part counts `FUN_1400e7a70`, plant look `FUN_1400df360`,
harvest look `FUN_1400e4800`, replant `FUN_1400e48e0`, geometry `FUN_1400e4a20`, material `FUN_1400e70a0`, upload `FUN_1400e4540`.
- **Data** (**Verified**, probe of all 27 vanilla farm buildings): every FARM_DATA has `layout` 0; `spacing` 5 to 13, `jitter`
  0.3 to 0.7, `aspect` 1 outdoors and 0.7 for indoor farms. `plants` entries have v0 0 and v1 20 to 100 (e.g. Wheat 25 / 100,
  Cotton 50 / 100 / 100). FARM_PART fields: `mesh`, `material` (Crops_material, hempfarm), `scale start` 0.1, `scale end` 1,
  `scale variance` 0, `offset start` / `end` 0, `delay` 0 in every vanilla part.
- **Part shares**: v0 is a group, v1 a weight; each part gets `floor(amount x v1 / group total + 0.5)` plants, the rounding rest
  going to the group's last part with more than one. So `plants` is a fixed mesh mix (Wheat: 20 % Wheat1, 80 % Wheat2), not
  growth stages: a plant never changes mesh.
- **Positions**, n = `amount`, s = `spacing`. Layout 0 grid: `across` = trunc(sqrt(n) x `aspect` + 0.5) (at least 1) along the
  building's x axis, n / `across` (integer) along its z, centred on the building (constants 0.5, **Verified** by the exe bytes);
  each cell moved by (U - 0.5) x `jitter` x s on both axes. The plant count is `across` x (n / `across`), fewer than `amount` for
  e.g. Wheat 50 (49), Wheat 75 (72), Hemp 40 (36), indoor 20 (18) (**Verified** by the formula on the data). Layout 1: one plant at
  the building, the others at angle U x 2pi, radius s + U x U x sqrt(n) x s, x times `aspect`, z divided by it (0 becomes 0.001).
  Layout 2: uniform in a box sqrt(n) x `aspect` x s by sqrt(n) / `aspect` x s. Not `inside`: each plant's y is the ground's.
- **Meshes**: each plant takes one part per group: a roll in [0, n) walks the parts cyclically from the first (at most
  2 x parts - 1 steps), subtracting their shares; the first group part reached below 0 that has plants left is used. A plant also
  rolls r = rand / 32767.
- **Look** while g >= -1: f = (clamp01(g) - `delay`) / (1 - `delay`); scale = lerp(`scale start`, `scale end`, f) x
  (1 + (r - 0.5) x `scale variance`); height offset = lerp(`offset start`, `offset end`, f), minus death² x the tallest part mesh's
  top for a non-static part (static: scale and offset start = end). Harvested (g = -10) and replanting zero a non-static part's
  scale. Vanilla: scale 0.1 + 0.9 x clamp01(g); a dying crop sinks by d² x mesh height.
- **Shader** (**Verified**, `data/materials/deferred/foliage.hlsl` `farm_vs`, `foliage.material` `FarmPlants : StaticObject`,
  cull none, `FarmShadowCaster`): `plantData[256]` = 128 plants x (position - building, offset | scale, ±0.1 r, sin 37r, cos 37r);
  yaw basis dx = (sin, 0, cos), dz = dx x up; colour = lerp(white, `death colour`, saturate(death)) + 0.1 r, `death` =
  d / `death threshold` x 0.8; a static part has -0.1 r (darker) and no death tint. The mesh merges every plant's part mesh with its
  slot index (`BLENDINDICES`); slots are cumulative per part, capped at 127.

### Unloaded zones

**Verified** (decompile, `ZoneManager::updateRendertimeThread` / `ZoneMapContent::threadedUpdate`): buildings update only in active zones; an
unloaded zone has no building objects. Per zone pass (`FUN_1409fb2b0`): objects whose vfunc 0x3e8 (needs update) is true always enter the ready list (Building: any sub-part busy; ProductionBuilding: busy, or not (flag +0x3a9 and no current job); FarmBuilding: always), idle ones only while a budget of 10 per call lasts, from a rotation cursor that then advances (**Verified**). The pass cadence of `threadedUpdate` and whether the idle battery step (`FUN_1402994a0`, no dt) shows a rate difference are **Unknown**. Meitou updates every machine of an active zone each tick, dt-scaled (a difference, idle battery charge unverified). No building but the farm reads a saved time, so mines, wells, refiners, generators, research and crafting stop unloaded and do not
catch up.

**Farm catch-up** (**Verified**, `FarmBuilding::loadFromSerialise` / `timeSkip`): the farm saves `save time` (game hours) when its zone unloads
and on every save. On load, h = now - save time; if h > 1 and the farm is built, `timeSkip(h, ignoreWater = owner is not the player)`:
- Cycle C = G (`growth time` / fertility, no power factor) + `harvest time` + `death time`; position p from `plant growth` (0..1 over G, 1..2 over harvest time,
  then `plant death` x death time).
- Water m = min over inputs of level + item count; watered hours w = m / (rate x h), rate the per-second water rate (+0x534, as the live
  tick uses it) and h in game hours (it mixes the units and divides by h too, as written; w = h with no rate).
- h <= w or an NPC farm: p += h, death 0 (not added to); when p > C the harvested count resets and p wraps (`p - floor(p / C) x C`). Nothing replants (`FUN_1400e48e0` is not
  called), and the plants' own growth values, harvest and clear progress are untouched.
- Else p = (p + w) wrapped on C, Gd = G / `drought multiplier` (1e8, `DAT_1416888b0`, when that is 0) and the drought cycle Cd = Gd + harvest + death; p += h - w with no multiplier
  (drought stretches only the growth phase). With a `drought death time` DDT > 0: death = saved death + (h - w) / DDT (the saved death is not zeroed here); above 1 the harvested count
  resets, death keeps its fraction and p = that fraction x DDT; no replant. p then wraps on Cd; phases use Gd (p < Gd: growth p / Gd; then 1 + (p - Gd) / harvest time; else 2) and a
  position in the death phase ADDS (p - Gd - harvest) / death time to the accumulated death.
  The plants are not touched (**Verified**, `FUN_1400df6e0` writes no plant): while the growth is below 1 the live tick (`FUN_1400e5580`) sets it to the lowest plant again, so a catch-up inside the growing phase is undone by the next tick, a vanilla quirk Meitou keeps.
- A player farm then uses its water, per input: need = w x rate x the input's amount - level, the level zeroed; need > 0 removes the whole
  stack when it holds no more than need, else floor(need) items with the level 1 - frac(need) (**Verified**, `FUN_1400df6e0`).

Meitou: the *Production far away* switch ([simulation](../simulation.md)); Faithful applies the above while running (a farm out of the active
zones keeps its stamp and catches up when its zone is active again; generators burn no fuel unloaded).

## Research

**Null-town bench** (**Verified**, research contribution `FUN_14029b8d0`): the original's research-building tick
requires the building's town pointer to resolve before it contributes to the global research manager. A complete
player research bench whose saved town resolves to `NO_TOWN` therefore does not raise the research bench level.
Meitou applies the same `Town != 0` gate; the research state itself remains global.

`ResearchBuilding::vfunc_150` (`FUN_14029b8d0`, **Observed**): an operator of a player-owned bench posts event 8 with
`outputRate x upgrade x health x powerSat x s` (s from `stat used` 4, science); the dispatcher `FUN_1407b65b0` passes it
to `FUN_140836b70`: contribution k of the frame is x 0.6^k (counter `+0x174`, cleared with the rate readout by
`FUN_14082e690`), the queue's first item gains contribution x dt seconds and completes past
`hours x G/24`, hours = `time x 0.3 x 1.3^(level - 1) / option` (`FUN_14082ed10`). Benches: `output rate` 0.85, 0.9,
0.95, 1.0, 1.05, 1.1 (I to VI); functionality `tech level` 1 to 6; power 0, 0, -10, -15, -25, -30 (**Verified**, probe).
- **Levels** (**Observed**): the manager's tech level starts 1; each finished `is level upgrade` research of `level` L
  raises it to L+1 (`FUN_140830880`). A complete player bench raises the bench level to its `tech level`
  (`FUN_14029be00` -> `FUN_140834390`); it only rises: losing the best bench does not lower it, only the manager's constructor and the
  research load zero it (**Verified**, `FUN_140834390`, `FUN_140836cb0`, `FUN_1408360f0`). Available (`FUN_1408333b0`): not `blueprint only`, not finished or queued,
  `level` <= tech level, requirements met leniently (`FUN_140832fa0` with its flag set, **Verified**: a RESEARCH finished, another record
  unlocked, or else that record's own `requirements` all met strictly, one level, an empty list passing; blueprint learning `FUN_1402b65d0`
  checks strictly), grouped by `category`. The research window's queue click (`FUN_1404976c0`) checks leniently too (**Verified**, disassembly: flags 1, 1;
  "Cannot research '{1}'. Requirements not met." otherwise), so a research one unfinished requirement away queues and completes: base Tech Level 2 needs the research
  Small House, which needs nothing (#363). The fourth flag's extra test (`+0xd8` set, `FUN_140832e50`, only when global `+0x2a0` is set) is **Unknown**; Meitou skips it.
- **Queue** (`FUN_1408348a0`): needs `level` <= bench level (`FUN_140830a30` names the missing level) and the cost
  paid (`FUN_1408343c0`): `cost` items (of the repetition, below) summed over the inventories of the
  player's research benches (**Verified**: `FUN_14082e750` runs a `BuildingFinderNearestAllies_Function` at the position of the camera focus node
  (`FUN_1407f2960`, the position the zone load centres on), `FUN_1409fc450` visits the building cells within +-1 of its cell (`floor(x / 4608)`, the zone grid), the finder keeps
  buildings of function 3 owned by the player (`FUN_1408ca830`, `FUN_1408ca5d0`); the finder's +0x40 = 3 is the function it requires; its +0x18 = 8 is read by neither the cell walk nor the tests, meaning **Unknown**) in those
  3 x 3 zones, counted by `FUN_14082e810` and removed by `FUN_14082e900`; a paid id is remembered and never charged again. Meitou: `ProductionSystem.Benches`, the zone
  of the last `FocusCommand` (sent when the camera moved 50 units; derived, not saved; none, as in headless runs: every bench), so a camera in another zone sees no bench. Partial progress is kept when an item leaves the queue.
- **Repetitions** (**Verified**, loader `FUN_140834b70`, `FUN_1408320a0`; Meitou `ResearchData.CostAt` / `TimeAt`, `ResearchState`): a research
  with `repeats` R > 1 gets copies "name 2" .. "name R" (repetition j = 1 .. R-1) beside the base (j = 0), so it is finished max(1, R)
  times in all. Repetition j has factor f = max(1, `repeat mult`^(j-1)) (1 for j = 0 and 1); each `cost` count c becomes
  max(trunc(c x f), c + j) and `time` trunc(time x f), which the hours above read. Mult 2, c 10: 10, 11, 20, 40, 80. How the
  copies' `requirements` chain (`FUN_1400b61b0`) and how a save names a copy are **Unknown** (Meitou counts finishes per base id).
- **Enable lists per repetition** (**Verified**, `FUN_140834b70` calls `FUN_1408320a0` for every research with `repeats` > 0, repeats 1 included, on the base as repetition 0
  and on each copy; `FUN_140831e60` runs it over `enable buildings`, `enable weapon type`, `enable weapon model`, `enable armour`, `enable item`, `enable robotics`,
  `enable crossbow`, `enable backpack`): an entry's v0 is the repetition that unlocks it and v1 a level floor. Repetition j keeps the entries with v0 = j (others are removed
  from that record, so v0 at or above `repeats`, or below 0, never applies) and raises its `level` to at least their v1. The record's `level` is what the
  queue test (bench level), availability (tech level, `FUN_1408333b0`) and the hours (`FUN_14082ed10`) read, so the next repetition can need a higher bench. With `repeats` 0
  nothing is filtered or raised: every entry applies when the single record completes, whatever v0 (base data: Advanced Medicine's "Advanced First Aid Kit" 1,4,4 and
  Heavy Generator Theory's "Generator II" 1,4,0). Meitou: `ResearchData.LevelAt`, `ResearchState.Refresh` (crafts), `ResearchGate.Unlocked` (buildings). The blueprint tooltip's tech level still reads the
  record's own `level` (the original's base record carries the repetition 0 floor; only matters for a repeating research with a blueprint).
- **Complete** (`FUN_140834550`): leaves the queue, joins the finished set, `improve buildings` applied
  (`FUN_140831a60`, the research's own fields for each listed building: `production mult` replaces the building's, so the
  last finished wins; `power increase` and `power capacity increase` add; lookup `FUN_14082ec70`, default 1, 0, 0), lists
  rebuilt, message "Research complete: {1}", sound `Thing_Complete`.
- **Tech levels** (**Verified**, probe): Tech Level 2 time 4 (6 Book, requires Small House), 3 time 10 (10 Book),
  4 time 16 (4 Ancient Science Book), 5 time 24 (8 Ancient Science Book), 6 time 32 (2 AI Core), each after the previous.
- **Save** (RESEARCH record, `FUN_1408310b0` / `FUN_1408360f0`): floats `num currents`, `num finished`; strings
  `current<k>` (queued id), floats `current prog<k>` (seconds), strings `finished<k>` and `paid<k>` (the paid ids; the prefix
  at `14171dd08` is "paid", **Verified** by the exe bytes).
- **UI**: the overview window's ResearchTab (`ResearchTypeList`, `ResearchQueueList`, `ResearchBenchMessage`, description
  panels); the bench's `Kenshi_InventoryResearchWindow` (`ResearchButton`); HUD `ResearchProgressBar`; header
  caption "RESEARCH - LEVEL {1} (CURRENT RATE: {2}x)" (`FUN_140494af0`); see [Research tab](ui-screens.md#research-tab).

### Blueprints

- **Item** (**Verified**, `BlueprintItem` constructor `FUN_1402b78a0`, item factory `FUN_140580750`; KenshiLib 1ce4a16 names): no BLUEPRINT (90) record
  exists in the data. A blueprint is an item made from a RESEARCH record: the factory turns a RESEARCH (as the record, or as the company or material) into a
  `BlueprintItem` whose look is the research's `blueprint item` ITEM, else `BLUEPRINT_ITEM` (weight 0, 1 x 1, no stack, function 11), and whose manufacturer slot
  (`+0xc0`) is the research. Its `value` (`+0x1e8`) is the research's `money`; prices in [economy](economy.md) section 5. Shops get them from VENDOR_LIST `blueprints`
  and the gear lists (economy Unknowns, list table); saves in [save](../formats/save.md).
- **Gear research** (**Verified**, `Research::_setup` `FUN_140834b70` -> `_createBlueprintsFor` `FUN_1408324f0`): at start every ARMOUR, CONTAINER and CROSSBOW
  gets a RESEARCH `<item id>.TECH.1`: a copy of `RESEARCH_TEMPLATE` (base data: level 1, time 12, Smithing, `blueprint only`), named as the item, `blueprint only`
  true, `money` = trunc(ItemValue(item, quality 40, no category row) x `blueprint price mult`), `enable armour` / `enable backpack` / `enable crossbow` = the item,
  `blueprint item` = `BLUEPRINT_ITEM_ARMOUR` (armour) or `BLUEPRINT_ITEM_GEAR`. Base data: 165 such records, `money` 0 only for the three shackles (**Verified**, test). What happens when the id already exists is **Unknown**
  (Meitou keeps the existing record).
- **Learning** (**Verified**, `BlueprintItem::learnResearch` `FUN_1402b65d0`): right click in an own window ([ui-screens](ui-screens.md#6-inventory-trade-dialogue-build)).
  Requirements unmet (`checkRequirements` `FUN_140832fa0` with no level or cost test: each `requirements` RESEARCH finished, each BUILDING unlocked) posts
  "Failed to learn from blueprint: Technology requirements not met"; finished before (the finished set, any repetition) "Research already known"; both keep the
  item. Otherwise `completeResearch` (as above, message and sound included) and the item is used up. Tech and bench level play no part.
- **Tooltip** (`FUN_1407ab430`, **Observed** by decompile): the research name and "Research Blueprint" (Title), the item rows, then the first match of: finished
  before, "Research already known" (Title); `level` above the player's bench level (`uses_level` `FUN_140830a30` answers the level, else 0), "Needs research bench level {1,num}"
  (BadBright); requirements met strictly (`uses_requirements` = `FUN_140832fa0` with the lenient flag clear, **Verified**: the check learning makes, not the research list's), "Right-click to learn tech" (Title), else "Cannot learn this yet" (BadBright). Then `Tech Level` (ink) with the `level` (Main),
  the prices, `Requirements:` with the `requirements` names (cyan `#14ffdc` when finished, else BadBright; the first in one row, the rest ", "-joined in a second), and a row per
  `enable buildings`, `enable weapon type`, `enable weapon model`, `enable armour` and `enable item` reference (`FUN_14079a890`): "New Building:", "New Weapon Class:",
  "New Weapons Grade:", "New Armour Type:", "New Items:", the name in Good. Meitou: `BlueprintRows`, `UnlockRows` (a row per requirement).
- **Starting blueprints** (**Verified**, `CharacterHuman::setupInventorySections` `FUN_14062c4e0`): after the weapons and before the `inventory` list, a
  new character gets one blueprint (quantity forced to 1) of each RESEARCH in its CHARACTER `blueprints` list whose v0 is above 0. Base data: the settler
  (Wheatstraw Farming) and Savant (Skeleton-Human Transformation Bible), both v0 1.

## Power

**Observed** unless noted. Per building: `power output` (generator output, battery rate, consumer draw when negative)
and `power capacity` (battery store, "in seconds" per fcs.def), plus research ([improvements](#research)).
- **Power toggles** (`UseableStuff::getGUIPower` `FUN_140307bf0`, **Verified**): for a player-owned machine whose power figure is not 0, the selection panel's bottom part (section 6)
  gets, for a consumer, a "Battery" toggle (`_recievesBatteryPower`, tooltip "This machine will run off of battery power if the main generators should run low or break down."
  / "...will NOT run off..."), then for every such machine a "Power" toggle (`powerOn`; "This machine is switched On" / "This machine is switched {1}Off"). A click
  (vfunc 161 `FUN_140297a60`, shared by every machine class) flips `powerOn`, sets the Wwise switch `Generator` to `On` / `Off` and posts `UI_Generator` on the GUI object. Meitou: `SetMachinePower`; the bar's green/red On/Off look is an engine choice.
- **Kinds** (registration `FUN_14092da80`, from switching on `vfunc_99`): capacity > 0 a battery, else output > 0 a
  generator; output < 0 a consumer. Function 5 without `consumes` is a wind generator, otherwise fuelled; batteries
  (23) share the generator class (factory `FUN_14057cc70`).
- **Generator output** (`GeneratorBuilding::getPowerOutput`): health x (output + increase) when switched on (`power on`) and
  fuelled ("Out of fuel" otherwise); **Verified** `FUN_140297760`, health the construction fraction. A battery's output is the same health x rate while it holds charge,
  and its capacity health x (capacity + increase) (`FUN_140297650`). Meitou the same (`ProductionSystem.Output`, `Capacity`).
- **Wind generators** (**Verified**, decompiled): output = the share x the above, 0 when below 1 (`WindGeneratorBuilding::getPowerOutput`); the share
  (`PhysicsCollection::getWindRotationPower`) is the first ROTATION_WIND_SPEED part's wind share ([rotating parts](#rotating-parts)) among the parts the placement actually spawned (FUN_140848950 spawns only the chosen random-group parts, so the rotor depends on the position's seed; Meitou: `WorldObjectLayout.WindRotor`, per machine), 0 with none or without
  the building's physics collection (render entity) loaded. Meitou gives every built one its share from the scheduled wind of the weather region at the site.
  Sound intensity = the share. State (`getGUIState`): the production state, then "No Wind" ("There is currently not enough wind to drive the
  machine.") while the share is 0. Panel (`getGUIPower`), only with rated output (`getMaxPower`: `power output` + research) above 0: section 4 (the condition
  panel) rows `Wind`, `{int(share x 100)}%` with the share as the bar's first layer, tooltip "The current amount of wind." and, with a ROTATION_WIND_SPEED
  part, a new line "Operating wind speed is between {1,num=fix,p=2} mph and {2,num=fix,p=2} mph." (wind min, max x 0.22369362); `Power Output`, int(output),
  output / rated as the second layer, tooltip "Power is switched off." when off, else "Power output is at maximum." unless output <= rated − 1, then "Power output
  is running at reduced efficiency due to damage." when the share is above 0 (so also in light wind) or "Currently not enough wind to generate any power".
  Section 6 (stats): `Wind Speed` `{1,num=fix,p=1} mph` of the region's wind at the building, tooltip "The current wind speed". Meitou
  (`PlayerInterface`): the same for the player's built wind generators, both bars in the yellow layer (the original's layer colours not seen), the rows after
  the condition row (order **Observed**), the state words and tooltips per [ui-screens](ui-screens.md#selected-building) (slot 114).
- **Demand** (`vfunc_163`, `FUN_140299630`): a consumer's |output + increase|, x operators / raw `max operators` when that is above 0, only while slot 164 holds (a
  lamp outdoors by day wants nothing); a farm (`FUN_1400df330`, **Verified**) always its whole |output + increase|, no operator share and not
  asking `power on` (Meitou the same); a
  battery's rate while below its capacity health x (capacity + increase) (`FUN_140297650`).
- **Town frame** (`Town::vfunc_87`): consumers sorted by ascending demand (`FUN_140927350`); each takes
  min(demand, supply left / consumers left). Consumers left short with `gets battery` are counted; if batteries were in
  use the previous frame (town `+0x470`) they share the batteries' output the same way. With no supply left the
  batteries drain by what was drawn (`FUN_1402978d0`) and the town enters battery mode; with a surplus it leaves it and
  the batteries charge from it, each at most its rate: in stored order each battery takes min(surplus left / batteries still to come, its rate), the count falling by one for every battery, switched off or full ones included (**Verified**, `FUN_14092cd60`; the switch byte `vfunc 0x228` and "rate 0 when full" are engine readings, **Observed**). Factor = granted / demand (1 with no demand), x health
  (`vfunc_109`); production, crafting, research and farm growth are scaled by it.
- **One town per call** (**Verified**, `FUN_14092d850`): the world's per-frame update collects every eligible town (`vfunc 0x270` == 0) and calls `vfunc 0x2b8` (the frame above) on the one at a static cursor (`DAT_14213543c`, incremented first, wrapped to 0 at the count) per call; the other towns keep their last grants. The call rate is not decoded. Meitou `ProductionSystem.ShareOneTown`: one grid town per production pass in ascending number (an engine choice for the cadence); batteries of every town still integrate their last grant each pass.
- **Fuel** (`vfunc_183` on generators): inputs drawn as a producer's, x the load (`GeneratorBuilding::vfunc_167`):
  output / rated x (battery charging + consumer demand) / all generators' output, at most 1 ("Fuel Consumption {1}%").
- **Battery store** (`vfunc_27`): += flow / rate x dt, clamped to 0..capacity.
- **Efficiency and "has power"** (the sound emitter's inputs, [audio](../audio.md#point-emitters)): slot 108 = operators / raw `max operators` x `vfunc_109` x `vfunc_119`, with
  `vfunc_119` = yield (`vfunc_92`) x the BUILDING's raw `output rate` (`+0x1a8`) x the research production mult; `FUN_140299370` is true for a generator (fuel and switch ignored), a building drawing nothing,
  else `power on` and the town's generator output (`Town::vfunc_87` `+0x474`) above 0 or battery mode with batteries' output (`+0x480`) above 0. Town `+0x47c` is the consumers' total demand. The town panel also reads +0x484 / +0x488 (taken to be the batteries' drain / charging this frame) and +0x48c / +0x490 (their charge / capacity) (**Observed**, `FUN_140727380`; [Town panel](ui-screens.md#town-panel)); Meitou sums them per town (`TownPower`).
- **Switch** (`FarmBuilding::vfunc_161`): flips `power on` (slot 99), then the `Generator` switch `On`/`Off` and `UI_Generator` on GUI object 110.
- **Save** (building `vfunc_17`/`18`): bools `power on`, `gets battery`, `broken`; float `batterycharge`.
- **Broken machines** (**Verified**, decompile and writer scan, 2026-10-26): `broken` is UseableStuff `+0x3b6` (`setBroken` `FUN_1400f6d10`, a no-op for plain buildings), set by every
  dismantle step (`Building::addDismantleProgress` `FUN_1402a2860`), cleared by a build step that leaves progress above 0 (`addConstructionProgress` `FUN_1405595a0`), by setting progress above 0
  (`FUN_140559ad0`) and by completion (`FUN_140562340`); also loaded (`FUN_1402a1750`). Every dismantle step also leaves the building incomplete (it drops to
  target − 1 and clears +0x160 first), so a machine is broken only while unfinished. Readers (**Verified**, scan of `+0x3b6` and its getters, slot 97 `FUN_1400f6d00`
  and slot 34 `FUN_1400f6d20` of the machine classes; plain, wall and gate buildings return 0):
  - Slot 114: every class's chain asks slot 97 before completion (`FUN_140300090`; Building / Storage / Useable / Light / Research `FUN_1402fe040`; Turret
    `FUN_1402fe430`; Crafting `FUN_1403016a0`; Farm `FUN_1400e0310`; RainCollector `FUN_140301410`): `Broken` (Bad) instead of Under construction; a
    generator's, battery's or wind generator's own word is set after the chain and still wins ([ui-screens](ui-screens.md#selected-building), slot 114).
  - `Task_UseTurret` (`FUN_1403474f0`): a broken turret cannot be manned ([combat](combat.md#turrets)); unfinished ones cannot be anyway.
  - Context menu (`FUN_1407a6bc0`) and hover (`FUN_1407fc6e0`): 95 REPAIR / `spanner` only for a complete broken building, which never occurs (above).
  - Locks (`FUN_140297d60`, `FUN_1402aa590`): a lock whose owner (+8) is broken counts as broken; for a cage or safe being dismantled.
  - Slot 34 (RootObject "disabled": dead or unconscious characters, broken machines): the world-query filter `FUN_1407865d0` drops them. Its users are **Unknown**: an exe scan
    finds no E8/E9 call, RIP-relative or absolute reference to it except its thunk `14004b353`, and none to that thunk (likewise for its sibling `FUN_1407861a0`,
    thunk `1400525e5`, the only caller of `FUN_140786100`), so it is reached indirectly, if at all.
  - Not read by production (slot 150 `FUN_140298700`, slot 109 `FUN_140296e00`), power (slots 163, 166, `FUN_140299370`), `Task_OperateMachine`, or the
    engineers' finder (slot 101 `FUN_1400f6b70`: progress below target).
  Meitou: `Machine.Broken`, saved as `broken`, set and cleared by the dismantle, build, completion and ruin steps; the header (`BuildingSelection.SiteStatus`) and
  turret manning read it, and a town turret's saved `broken` (below). A player-built cage's or safe's lock is a fixture of its own ([locks](locks.md#lock-object)); a broken machine keeps its fixtures registered (doors, cage, bed) and its own lock counts as broken.
- **What damages a building** (**Verified**, decompile): hits go to slot 62, which is `FUN_1402972c0` (returns 1, no effect) for every UseableStuff class (machines,
  turrets, storages, lights) and `FUN_1400d20b0` (returns 0, no effect either) for plain and gate buildings; only WallBuilding (`FUN_14029d560`, [walls](#walls)) and doors (`FUN_14029d690`)
  lose health, a wall at 0 becoming destroyed (slot 104). Destroyed (+0x1a1) is otherwise set only by SQUAD_TEMPLATE `building ruined` (28 true; `FUN_14057f1f0` from `RootObjectFactory::createRandomUnloadedSquad`, **Verified**: the squad's
  building gets the template's `building designation` if it has none, its `public day`, and is destroyed when ruined and destructible: [ruined by residents](#ruined-by-residents)) and
  its own deferred replay: slot 104 (`FUN_140557280`) called while `FUN_14025c6d0(DAT_1421348a0)` holds queues message 0x10 (`FUN_140349be0` on
  `DAT_142134110`) instead, replayed by `FUN_1407b65b0` from the GameWorld update `FUN_140786e30` (**Observed**, decompile), so it adds no source; the first build step on it ruins the building ([construction tick](#construction-tick)). Dismantle (slot 73, its
  only damage path for machines) is offered only when slot 79 (`FUN_14029c380`) holds: the owner is a player faction. So raids and fights never break a
  machine, and a town's own turret (owned by the town's faction) breaks only through a loaded `broken` (none set in the owner's six saves, **Observed**: no
  `broken` true in their zone files). `Task_UseTurret` asks only slot 107, a free place (slot 159) and slot 97, not completion or destroyed. Meitou: an NPC gunner does not man a town turret whose saved state has `broken` (`CombatSystem.Manning`); town buildings
  have no machines otherwise, which this rule makes unnecessary.
- **Status strings** (`GeneratorBuilding::vfunc_114`): "Out of fuel", battery "Charging" / "Draining" / "Stored" / "Empty".
- Data (**Verified**, probe): Small Generator 40, Generator II 100, Wind Generator 50, Small Wind 25, Wind II 100,
  Battery Bank 40 / capacity 60; Weapon Smith -8, refineries -10 to -40, Research Bench III-VI -10 to -30. Power
  research: Upgraded Small Generators +15, Amplified Generators +15, Battery Power / Advanced +10 each, Battery
  Capacity / Advanced +60 each.

### Rotating parts

**Verified** (decompiled `RotatingEnt` constructor `FUN_140556250`, `update` `140547ca0` with its disassembly, `weatherUpdated` `140549c60`,
`Building::update`, `getOutputBasedRotationSpeedMult`) unless noted. A BUILDING_PART without a collision file whose `rotation function` (or
its holder's) is set gets one; function 0 becomes 6 (still). Fields: `rotation speed min/max` (degrees/s), `wind speed rotation min/max`
(units/s), `rotation axis` (0 X, 1 Y, 2 Z, else none). Function 3 raises speed max and wind max to their mins; every other function rolls
speed max x U(0.95, 1.05). Then the node turns by U(0, 2π) about the axis (both rolls from the building's generator). `wind speed rotation danger`
is read and never used (no reader of its field among the rotor functions or the wind panel, the only caller of `getWindMillPart`). Each frame of a built building (`Building::update`, skipped while the game is paused, `DAT_1421349c9`, [game-loop](game-loop.md#speed-and-pause)) each part steps with
dt = `DAT_142133798` (game dt, 0 paused); functions 1 to 3 then set the orientation to angle x axis, the angle counting from 0 (the start roll
is lost once a part moves):
- **1 CONSTANT**: speed max (fan blades, six-barrel). **2 OUTPUT**: (max − min) x the building's mult each frame (spinners, drills, looms).
  The mult: 1 for a plain building; a generator (output > 0, no capacity) `power on` ? its power output x 0.01 : 0; any other machine
  operators / max operators (1 when 0) x efficiency (active ? granted / demand x health : 0) x yield x `output rate` x research mult.
- **3 WIND_SPEED**: at the wind speed w of the region: share = 0 below wind min, 1 at max or above, else (w − min) / (max − min); speed =
  lerp(speed min, speed max, share). The share is what wind generators multiply by.
- **4 WIND_DIRECTION** (heads): speed max is a turn rate. Told the weather, it starts a turn: from its orientation now to the one whose
  local X is the wind direction brought into the parent's frame (y = 0), turned −90° about Y (`quatSetDirection`), so its +Z, where the blades
  sit, faces against the wind vector (into the wind, the vector being the way it blows as Meitou's cloud drift reads it); elapsed 0, length |yaw now − yaw target| / speed (yaws in (−π, π], unwrapped). Each frame while elapsed < length:
  slerp(clamp(elapsed / length), from, to, short way), then elapsed += dt. Speed 0: never turns.
- **Weather**: functions 3 and 4 register with the region at the part and are told at once, on a weather change and every frame the region's
  wind is interpolating (`WeatherRegion::updateBT`), so a head stands still while the wind changes and turns after.
- Data (**Verified**, probe of the merged data): Wind Generator (I, II) head 25°/s, blades 90°/s, wind 10 to 50; Small Wind Generator body
  25°/s, blades 180°/s wind 4 to 25 (its share), dynamo wind 5 to 25; refinery spinners 16, drills 100, generator spinner 100, looms 90.

Meitou (`RotorSpec`, `RotatingParts.Advance`): the renderer turns the parts it draws by the game frame time and the scheduled
wind; a level building's mult comes from its data as its sound emitter's efficiency does (generator output x 0.01, batteries and machines
needing neither operators nor power their `output rate`, other machines 0; engine choice).

**Storage** (**Verified**, 603 buildings): 87 have `has inventory`; default 18 x 18, `itemtype limit` 4; `stackable bonus
mult` 1 to 100000; `limit inventory` restricts the items. Loader note (`FUN_14057cc70`:192-202, 555-567, **Verified**): when the functionality record's int `flags` has bit 4 set the loader only prints the log line 'Warning: Nest building loaded <name>' and goes on, no state changes. Grid (**Observed**, the building loader): a building whose `limit inventory`
names exactly one item is that item's `inventory footprint` (Iron Plates 4 x 3, Building Materials 6 x 3, steel bars 6 x 1), else
`storage size`, for built and town-placed storages alike (`MachineData.StorageGrid`). Its window (**Observed**, screenshots) is
`Kenshi_InventoryGenericWindow`: caption STORAGE, the one limited item's name as heading, `Max Capacity: N` with N the `stackable bonus
mult`, the line red with "(Full)" once the first stack reaches it (exact red **Unknown**). Locks and containers: [locks](locks.md).
Water Tower takes only Water and `Storage: Ore` only Raw Iron, both with mult 100 and functionality `Resource
storage` (function 2); Large Barrels also hold Water. Other `itemtype limit`s (**Verified**, merged game data): ARMOUR
(Armour Storage, Armour Stand), WEAPON (weapon stands, cabinets, display boards), CROSSBOW (Crossbow Locker); none takes
CONTAINER (backpacks). Shop counters (function 9) have no type limit in Meitou (`IsStorage` is functions 2 and 13 only).
Two acceptance rules (**Verified**, decompile): the AI's storage finder (`StorageBuilding` slot `0x4a0`, `FUN_1402988c0`) needs the item's record
type to equal `itemtype limit` (and the item in `limit inventory` when listed); the player's hand (drag or put, refusal "Invalid container type.",
code 6 of `FUN_140713d20`) asks the inventory section (`FUN_14074bc30`), which the building gives type 7 "any" unless `itemtype limit` is WEAPON
(then 0, compared with an item field `+0x110` whose meaning is **Unknown**; Meitou reads it as the item being a weapon) (`FUN_140554530`), and a
`limit inventory` list restricts it to the listed items. So a General Storage Chest (ITEM) takes armour and weapons by hand while haulers never fill
it with them. Meitou: `MachineData.Accepts` (AI) and `TakesByHand` (player).


### NPC town machines

- **Output add** (`FUN_1402984b0`, **Verified**): when a producer's accumulator reaches 1 and its output inventory cannot take the item, the accumulator resets to 0, so a full output loses
  the progress (and `isMachineOutputFull`, status 1, stops the operators: row 87 needs !63). Nothing else in the production building removes items.
- **Who empties it** (**Verified**, a scan of every AI package in the merged data): rows 87, 92, 93, 113, 114, 124, 152, 221, 242 and 247 are in no package; NPCs reach them only through
  the planner from goal 189 (effect IS_WORKING, provider 87: needs !63, !62, !89, !169) and the player's jobs. For a full output the provider of !63 is 92 (`Task_EmptyMachine`,
  `FUN_14033cd10`: needs 82 room for the machine's output item and isAtLocation); for no room the provider of 82 is 114 (a `Task_Blank` goal with effect !62; its finder `FUN_1405a70a0` = the nearest storage or machine that accepts a carried item type; the
  filler of 62 is 93 `Task_FillMachine`, `FUN_140344510`, which puts the carried items into a storage through `FUN_140340da0`). **Verified** (state 62 on a storage is not the question): state 62
  (`FUN_1405974e0`) reads the building's production component (virtual `0x2f8`, which a pure storage lacks: `FUN_140344510` takes its storage branch on exactly that), so 62 is false for a storage and
  the effect `!62` of rows 113 / 114 / 242 already holds there, but the planner never tests an effect before following it: the node expansion `FUN_14051bae0` puts the effect first in the list of states
  it opens children for, B entries after it only when unmet, and the child maker `FUN_14051c240` asks nothing of the state; so the providers of `!62` (93 alone: the only row listing `!62` in A)
  are made whether or not it holds, with the effect target (the finder's storage) as their target. Row 242, 114 and 113 share the finder (`FUN_1405a59a0` through the stub `FUN_1405a70a0`:
  the nearest storage taking a carried item). "Can run" (`FUN_14060f940`) tests B first, then the effect on its target (false and the effect reported unmet when it does not hold); it only gates the accepting of a finder-less acting node. Row 93 then needs 67 (carries something the target
  takes), 2, `!169` and isAtLocation, and acts (`FUN_140344510`): a ditch into a storage is plan `[93, 114, ...]`, not blocked by 62. A workplace the plan cannot make runnable (inputs to fetch: 94 then 66 from another machine's output, 113) fails goal 189, and goal 190
  (pretend, row 221, which asks nothing of the machine) is what the NPC does.
- **Task_EmptyMachine** (`FUN_14033cd10`, **Verified**): at the building, takes its output item types in order and moves units into the character's inventory while there is room
  (backpack included); a production building at status 1 goes to 2 after a take; ends when nothing is left.
- **State evaluators** (**Observed**, read from the decompile, meanings of the virtual calls inferred): 66 `FUN_140597530` the character carries the machine's output item; 67 `FUN_1405a45f0` it carries an item the machine (an input) or storage accepts;
  82 `FUN_14059bd80` inventory or worn backpack has room for the target's item (a machine: its output item); 105 `FUN_1405a2c40` machine output empty; 169 `FUN_1405975d0` input invalid
  (production type 6 flag).
- **Meitou** (`ProductionSystem.TownWork`, `AiSystem.WorkAtMachine`): the town building a worker of goal 189 stands at is a virtual complete site `townwork:<building key>` of the town's
  faction (like a rock or a training dummy: no registry site, not drawn, saved or built; its machine is kept in memory and in the save), made when the first worker arrives and kept in the save ([town machines](../formats/save.md)). A worker whose plan starts with
  87 operates it every tick (skill factor, operating XP: the player's operator lines), with 92 empties it into its inventory. Only producers run: a research bench counts for the player's
  research alone and a crafting bench with no queue refuses an operator, so both stay pretence. Ditching: rows 113 / 114 / 93 (`TownDutySystem.DitchStore` and the Ditch errand, [ai-tasks](ai-tasks.md#ditching)). Row 94 (`WANT_TO_FILL_MACHINE`, effect 66) and its finder `FUN_14059d9b0` (**Verified**: for each input item of the target machine in list order, `FUN_14059d000` looks for a cached earlier pick, then loose ground items, then the nearest building whose production component's output lists the item (identity for ITEM records, same record type otherwise) with some in it, the buildings split by the owner test of vfunc `0x3b0`, within the sight range of the AI's range mode) give the chain 92 at the source, 94, 93 at the machine, 87: Meitou plans it (`PlanContext.Find` row 94, states 66 / 67 on machine targets), the worker walks to the source (`AiSystem.StepBuilding`), `TownWorkers` empties it (92), fills the inputs (93, `FillInputs`) and operates. Not modelled (**Unknown** or undecoded): the cached pick, ground items, the `0x3b0` split and the building mask `0x4002436` (the town's workplaces stand in). A machine exists only once a worker has been at its building; the original's has its empty output from the start, so a source with no output yet is none in both. Unpowered: town machines draw no player grid (power held true).
  A town machine is no player machine for the hauling jobs (`ProductionSystem.Takes`).
### Hauling

- **Tasks** (**Verified** classes, **Observed** meaning): `Task_EmptyMachine` (92 "Collecting resource", 284 "Looting")
  moves a building's output into the character's inventory while it has room (`FUN_14033cd10`). Rows 92 and 113 list "output not
  full" (!63) in list A, which row 87 needs, so a full output leads the planner from 87 to 92 ([Blocked output](#blocked-output), **Observed**, table). `Task_FillMachine`
  (93 "Filling machine", 124 "Hauling to") puts carried items, main grid and backpack, into a machine or a storage
  (`FUN_140344510`) (**Verified** by re-reading it): the target building without a production component is a storage, whose inventory takes the character's items and its backpack's (`FUN_140340da0`); one with a production component takes what its inputs accept (`FUN_140343780`, or `FUN_140340f10` for type 4); the task ends the first update that moves nothing. GOAP rows: [ai-tasks](ai-tasks.md); states 62 machineHasEmptyInputs, 63 isMachineOutputFull,
  66 haveSomeResourcesFrom, 67 haveSomeResourcesFor, 82 gotInventoryRoomForResources, 169 isMachineInputInvalid.
- **Option** (**Verified**, string): "If they have nothing else to do, characters will automatically put any excess
  materials they are carrying into storage."
- **Jobs** (**Verified**, [perma-jobs](ai-tasks.md#perma-jobs)): Shift with an order on a machine adds job 87 "Operating machine";
  on a storage 124 "Hauling to" (several targets allowed); an automatic machine (152) adds 152 plus 124 on it. The first job in a
  character's list that can run wins; operating is skipped while the machine's output is full or its inputs are empty (row 87's !63 and !62 unmet at the job's root,
  [perma-jobs](ai-tasks.md#perma-jobs)), so a character moves down its list and returns when the machine can work again.
- **Unknown:** how the original picks the storage (nearest, same town, a range), when an operator leaves a
  machine to haul, where a 124 job takes its items from, and whether 152 / 124 on an automatic machine ever empty its output
  (152's goal is only "no empty inputs", !62).

## House sales

Rules of `For sale:` and the price: [selected building](ui-screens.md#selected-building). **Verified** on the base game
(`HousePurchaseTests.The_Hub_has_a_ruined_Storm_House_for_4800_in_the_original_data`): The Hub's ruined Storm Houses have
interior meshes but no functionality record, their level states lack `sell home` while their resident templates
(`44421-rebirth.mod`, `44426-rebirth.mod`) set it; target 16, owner's `building cost mult` 2: 0.375 × 16 × 2 × 400 = 4,800.
Meitou reads `sell home` from the saved state, else the level state, else the resident template; **Unknown** whether the
original falls back to the template.

## Ruined by residents

**Verified** (decompile): the squad setup `FUN_14057f1f0` calls slot 104 (set destroyed) on the squad's home with `building ruined` of its template and slot 105
(`FUN_14055b520`, can be destroyed): true when the building's class (+0x198) is 7, GatewayBuilding, the `is wall gate` building (**Verified**, [pathfinding](pathfinding.md)), or it, or a part reached through `parts` at any depth, has a
`destroyed mesh`. So a home whose template is not ruined is set intact. **Observed** (data, base game and its mods): 26 SQUAD_TEMPLATEs set `building ruined`, among
them `_ruined empty building (DEFAULT)` (`44421-rebirth.mod`), the `default resident` of Old Village (Deadcat, `50243-rebirth.mod`), whose level states are
intact and name no template. Its houses are ruins in the original (issue #145).

**A ruin holds no furniture.** **Verified** (decompile): set destroyed (`FUN_140557280`) destroys the interior's internal buildings and furniture (`FUN_14054f4d0`,
`FUN_140548580`), after the squad setup has loaded the template's interior; the layout names stay, and housing names a ruin's layouts as any home's
([layouts](factions-squads-towns.md#town-residents)). **Observed** (saves METARU, autosaves 1 and 2, redev, Beep Fan Club 1): no saved building lies inside any ruin
(0 of 56, 56, 23, 23, 62), while 283 of 284 standing buildings with an interior name hold furniture; every saved ruin names no interior layout (its template
names none). Whether a ruin's interior is laid out again when it next loads (the interior loader `FUN_1405621e0` has no destroyed test) is **Unknown**: no save
has a ruin whose name is set. Meitou: no reader lays out a ruin's layouts (drawing, lamps, effects, emitters, navmesh, and `TownBuildings`: no beds, nodes,
fixtures or item slots).

Meitou (`ResidentRuins`): before anything reads the level data's `destroyed`, every town is housed by the population's rule ([town residents](factions-squads-towns.md#town-residents))
and each home of a ruined template that has a destroyed mesh is set destroyed in its level state; 71 base-game buildings (Old Village 10 of 10, Abandoned Town 9 of 9,
the library ruins). A saved `destroyed` false does not undo a level ruin unless the player's repair saved it (`meitou town construction`; engine choice: saves
made before this pass wrote those buildings intact). A town's baked distant mesh is a fixed file of the install, drawn as it is (as in the original). Meitou: `ResidentRuins.CanBeDestroyed` tests `is wall gate` for the class. Not modelled: setting a level ruin intact (13 level ruins get a template that is not ruined, e.g. Bast's and Endin's
`Trashed building w/squatters`, yet those towns are ruins in the original, so where the original skips the call is **Unknown**).

A [town override](factions-squads-towns.md#town-overrides) reads its zone back from the level file and houses the replacement's residents, so Meitou
(`FixtureRegistry.ApplyTownRuins`) sets each of the town's buildings in its own zone that is not the player's: a home of a ruined template that can be destroyed is a
ruin, any other as its level file has it (a resident ruin of the old town stands again). Its other zones keep their state, but such a home there becomes a ruin
too. A loaded save takes its ruins of standing buildings back, then applies its replacements again; the building states it writes carry them. The host replaces those
buildings' level placements (drawing, navmesh, lamps) and drops a ruin's particle effects (a standing one's come back); doors, locks and indoor sight follow. **Observed** (data):
95 overrides change ruins, e.g. Stoat (override) half destroyed 8, Catun (override) fishman takeover 14; Free Settlement to The Free City stands 7 up again.

## Unknowns

- How often Building slot 28 resets the builder counter, which HUD orders set the AI
  range mode, and what `FUN_14025c6d0` (holding dismantle work) tests.
- Event types of the queue at `DAT_1421346d8` other than 8 (research progress) and 11 (build work).
- `vfunc_0x520` (a consumer counted as active): 1 for every class but `LightBuilding`, whose answer is in [lights](../formats/lights.md#when-a-light-is-on).
- Option slider defaults; the third value of reference triples.
