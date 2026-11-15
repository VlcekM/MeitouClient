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

### Material delivery

Fetching (**Verified**, TaskData rows of `FUN_140319a60`, finder `FUN_1405a2a60` -> `FUN_14059d000`, factory `FUN_14032ebd0`
case 247, `FUN_14033cd10`): the planner chain is 2 Building (needs 51) <- 71 Adding materials (needs 50) <- 115 "Finding
building mats" (effect 150, a target finder) <- 247 "Collecting resource". The 115 finder takes the site's materials with
`required - delivered > 0` (`FUN_14054a450`) in list order and returns the first one's source (factor 1; 0 when none):
1. a source remembered for that item (a per-item map in the AI, kept in the AI's per-update cache, which is emptied at the start and end of each update, so it changes no answer; Meitou keeps none);

### Builders and dismantle

Task ids and AI conditions: ai-tasks. Engineer job (**Verified**): row 125 "Engineer" has no
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

- the X/Z distance to `getPositionMarker(charPos)` (slot 0x470: the nearest valid usage node, else the construction position
  +0x26c when fewer than 2) is below `getReachRange()` (slot 0x350), **doubled when not moving**, and the character is on the
  same floor (slot 0x60) or |dy| below a limit: 130 when the door byte (+0x1a0) is set or the building has a door child (slot 0x3e0), else 45 when its containing-building handle (slot 0x1d8) is null (outside any building) or slot 0x428 is true, else 15 (**Verified**, `AI::isWithinChoppingRange` `FUN_14059c6d0`, constants 130 / 45 / 15 in `data_values.txt`; what slot 0x428 is stays **Observed**: ceiling-mounted class).
- Otherwise not; once the path has ended at the target it logs "Task impossible: X can't get to Y". There is no settle slack.

`getReachRange`: `Building` 40 (construction), `WallBuilding` 60, `UseableStuff` (machines, storages, farms, mines, rocks) its
`maxUseRange` +0x420, read from the functionality's `use range` (`FUN_1402a0900`), `DoorStuff` 45 (kind 7) or 25. Vanilla `use range`
(**Verified** by a probe of the install): 10 to 25 for benches and storage, 50 ore mines and iron rocks, 80 wind generators,
100 copper rocks, 140 hybrid stone mines. Which tasks reach their building through `isAtLocation` is **Observed** only through
the goal conditions (the call is virtual). Meitou applies the height limit above wherever it applies the reach range of the same function: builders, fetchers, machine operators, haulers and corpse carriers (`SiteReach.AtHeight`; rocks and turret posts keep their own arrival test).

Meitou (`SiteReach`): builders 40 (walls 60), operators and storage fetches the `use range`, doubled once stopped, measured
to the building's footprint hull (engine choice standing in for touching; a building without collision uses its parts' bounds
hull) or, unknown, its position. A rock's operator has arrived within 10 (20 once stopped) of its mesh's footprint (the convex hull of
its vertices seen from above) as the instance places it (its bounds circle without the mesh); it walks to the bounds box's centre and
faces it (a rock's origin can lie off its pile). Engine choice, nearer than the original: its rule above has a copper rock (`use range`

Damage (**Verified**, slot 62 `hitByMeleeAttack`, KenshiLib 1ce4a16 names): only melee blows lower a complete building's
progress. The Building/`UseableStuff` body (`1402972c0`) returns 1 and changes nothing; only `WallBuilding` (`14029d560`, gates
included) and `DoorStuff` (`14029d690`) override it (formulas: [combat](combat.md#attack-zone)). No ranged slot exists beside
62 on RootObject. In play the destroyed setter (slot 104) is called with 1 only by the wall hit (the "Wall termites" drain
also calls it, but never runs, below); a destroyed wall takes the destroyed transition above (`WallBuilding` slot 70 calls the Building one first).

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
| "Too close to another building." / "No valid usage nodes." | a footprint node box overlaps physics shapes (group mask 0xfffde40, up to 32) or another queued ghost; second text when every usage node is invalid. Meitou (`PlacementCheck.HullBlock`) overlaps footprints in 2D instead, leaving out the sites and ghosts on another floor of the building the ghost stands in (the boxes are 3D). Inside a building, the footprint must lie in its convex indoor hull, and its prism (2 over the floor to 2 under its top) must not reach more than 1 into that building's own collision (walls, floors, roofs; `FootprintPrism`): the hull takes in the walls, so a footprint inside it could stand in a Storm house's sloped wall; from 12 over the floor up, nor more than 1 into the building's drawn roofs and ceilings (its parts' render-mesh triangles within 40° of level, `RoofCover`), as some parts lack that collision (Swamp House upper storey: collision for its floor only, so a wind generator went up through the roof) |
| "The building blocks another building use nodes." | the footprint covers another building's usage node |
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
- **The outpost** (**Verified**, `FUN_140934e10`): a `Town` made from the TOWN `player_town_template` (base data: name
  "Your Outpost", type 1, `town radius mult` 1, no faction), owned by the player faction, named "Your Outpost",
  `public` false, `plevel` 0, `discovered` and `explored` (+0x180/+0x181, KenshiLib 1ce4a16 `TownBase`), added to the player-town list (town manager +0x68) that the save writes
  ([player outposts](../formats/save.md#player-outposts)). On load (`FUN_14092ae80`) a player-faction town whose centre lies strictly inside the
  radius of its nearest other player-owned, non-nest town is removed (the one reached first in list order; its buildings keep their `town` handle,
  become unassigned if that handle no longer resolves) with "Player town '<a>' withing boundaries of <b>" ([rule](../formats/save.md#importing-a-game-savemanagerimportgame-fun_140378a30)).
  No residents: the template lists none, and the resident pick (`Town::chooseResidents` `FUN_1409370d0`, from the building
  `default resident` / `residents override`) runs from `ZoneManager::loadPhase2` (`FUN_140a0e760`) only for towns whose faction
  lacks the player flag (FACTION `+0x250`) (**Verified** by decompile and its only caller).
- **Later adoption** (**Verified**, GOG founding path `0x1404d6730`, decompile `FUN_1404d6d90`): founding an
  outpost explicitly moves existing player buildings into it when their town is a player town or NO_TOWN and their
  nearest town of any faction within 8000 is the new one. An NPC-town building is excluded. Meitou clears the old
  foreign handle and NO_TOWN flag when adopting. Merely extending a town's radius over a NO_TOWN building does
  not adopt it: refit finds members by their existing resolved town, not by containment (`FUN_1405488f0`,
  `FUN_1409fb880`, `FUN_1400f6c20`); its live NO_TOWN handle keeps resolving to NO_TOWN. Reassessment for a
  last-placed NO_TOWN building does nothing (`TownNull` slot 77 `FUN_1409510d0` returns null;
  `FUN_14054d080` requires a town).
- **Unresolved building towns** (**Verified**, `FUN_140555650`, `FUN_1402a14b0` / `FUN_1402a1750`,
  `FUN_1406bd000`, `FUN_1400f6c20`, `FUN_1409f9100`): a null or truly dangling handle (such as a removed
  outpost) resolves to the nearest non-nest town, any owner and no distance cap, for refit, emptiness,
  reassessment and power. A live level-town or NO_TOWN handle keeps that membership instead. Meitou preserves
  it through `BuildSite.ForeignTown`, with `InNoTown` for NO_TOWN; `TownLookup.KeyOf` excludes it from player
  refits and `ProductionSystem.PowerTown` excludes NO_TOWN from grids. `BuildingSave` recognizes the canonical
  NO_TOWN handle even in a new save with no faction-less building carrying it, and writes it back unchanged.
  A loaded null/dangling handle keeps `Town=0` and the nearest-town fallback; it remains distinct from a new
  townless placement. **Observed**, original save and dismantle result: excluding generator 1099 reproduces
  Goonrock's saved radius 2545.56 exactly; counting it gives 2758.76.

- **Slope** (slot 31 `FUN_1404daa20`): ground normal from four rays at the rotated bounding-box corners (outdoors) or
  one ray (indoors); slope = angle from vertical, compared with `max slope` in degrees. With `match slope` (forced for
  interior furniture and nodes) the ghost tilts to the ground and a slope over the limit fails; without it the tilt is
  blended by the player tilt factor and clamped, never failing. Also fails on an object surface more than 5 deg off
  the hit normal, or on ground with normal.y < 0.2 (`FUN_1404d5420`).
  The slope block is wrapped in a call to vtable slot 3, which returns false for `PreviewBuilding`, `_Wall`, `_Resource` and `_Ceiling` (`FUN_14042de80`; only `_Snapping` overrides it with `FUN_1404fe140`), so it runs for an indoor ghost too (**Verified**, `vtables.txt`): one ray from 10 above the origin straight down against the viewed floors' collision, its hit normal (up if none) against `max slope`; the indoor ghost's forced `match slope` makes an over-steep floor (a stair) fail it (NotLevel). Meitou: `FloorSurfaces.NormalBelow` (a ray 10 above the origin straight down on the floors up to the viewed one, the nearest triangle's upward normal) in `GhostFloor.Normal`, and `PlacementCheck` fails NotLevel when its angle from vertical exceeds `max slope` (> 0); the 5 degree surface test stays untraced.
- **Vertical margins** (**Verified**, `FUN_1404d9c00`, `FUN_1404d21e0`, `FUN_1404d23c0`, `FUN_1404d9530`, `FUN_1404d2080`, `FUN_1404d1680`
  and the frame update `FUN_1404dba40`; Meitou: `PlacementCheck.Margins`, `Settle`):
  - **Margins.** Per node: ground - node height for a group not above ground, node height - ground for one that is; a group's
    value is the minimum over its nodes, and the ghost's `+0x84` ("up": how far it may still rise) is the minimum over the
    not-above groups, `+0x80` ("down": how far it may sink) over the above groups, both starting at 1e8.
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

- **A ramp's mesh sits in its wall's own space** (**Verified**, mesh bounds): `basic_wall_ramp2.mesh` spans x -102..-20, z 0..24, just
  outside Concrete Wall's `wall_basic02.mesh` (x -19.5..3.9, z -2.7..118.7); Defensive Wall Ramp IV's staircase spans x -268..-49, z
  187..222 against Wall IV (x -59..27, z 0..305; both scale 0.85). At its wall's position and turn a ramp stands against the wall's -X side.
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
- **Upgrade** (slot 75): record := target, the name-dirty byte +0x19f set, complete and paused cleared (+0x160/+0x161),
  progress capped at 50 material units, the new record's construction state applied (`FUN_14054a450`), then each material
  still owed is taken from the building's own inventory, int(required - delivered) units each, one item destroyed per unit, doors given the new record's `doors` record,
  mesh, physics and window rebuilt, an outdoor building's navmesh area rebuilt.
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
- **Snapped joints** (`FUN_140429cc0`): turn = angle from the snap direction to the line; trunc(turn / 60) shortest pieces
  (none for a single) each turned an equal share; connectors at the joint and after each piece only when the line leaves
  on one side (`dot(perp, b) > -0.01` xor backwards, perp = (D.z, 0, -D.x)). Straight runs get no connectors.
- WallBuilding damage `clamp((hit1 x 2 + hit0 x 0.5) x 0.5, 10, 60) x 0.003` off health (slot 62, **Verified**).

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

## Production

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

### Item furnace

`FurnaceBuilding` (function 14; KenshiLib 1ce4a16 names, bodies checked in our exe). Base data (**Verified**, probe): Item Furnace (`3959-gamedata.base`, research Weapon
Melting), functionality Furnace: `max operators` 0 (so automatic: `ProductionBuilding::vfunc_28` runs it with s = 1), `production mult` 0.25, `produces` Raw Iron 2000, no `consumes`.
- **Grid** (**Verified**, `setupFromData` `FUN_14029f980`): with no `consumes` it adds one input, section `in1` (bytes at `141694a78`), 12 x 7, no slot limit, stacks at least 10
  (`stackingBonusMin` 10, mult 1), level 0, rate 1. Slots 183 / 184 / 185 (input and output updates, rate limit) are empty, so the producer's draw does not run.
- **Burn** (`operate` slot 150, `FUN_140299fa0`, **Observed**): only while on, with an output and an input; state 2; amount as [production](#production) (x slot 167 when `+0x3bc` > 0:
  `+0x3bc` is the BUILDING's `power output`, made positive when `power capacity` > 0, and slot 167 is the generator's load share `GeneratorBuilding::vfunc_167` but 1 for the
  furnace's class (`FUN_1400f6d60`), so the term changes nothing; **Verified**, decompile of `FUN_14029e460` and the vtables); level -= amount, progress += amount; at level <= 0.001 or progress >= 0.999 the output gets (int) the grid's metal of
  Raw Iron (one `addItem` of that quantity), the grid is emptied (`clearAllItems`) and the furnace turns off. So a whole load burns in 1 / (0.25 x P x option) seconds at full health, whatever it holds.
- **Metal** (`getInputValue` slot 180, `FUN_1402a5f40`; constants **Verified** by disassembly): ITEM with `ingredients`: iron x quantity x 0.5; ARMOUR of `material type` 2 or 3
  (chain, plate): total coverage x 0.01 x (1 - `fabrics amount`) x 2, other armour 0; WEAPON: `getItemWeightSingle` (`+0x120`) x 0.2, within 0.5..5; anything else 0.
  Iron (`getIronAmountInItem` `FUN_1402a5750`, **Verified** by disassembly): over the record's `ingredients`, Raw Iron (`1866-gamedata.base`, a literal) adds its v0 x 0.01, any other
  ingredient its own iron, recursively, not times its amount. Total coverage (`FUN_14082fd80`, **Observed**): the run-time int `total coverage` = the sum of the armour's
  `part coverage` v0, the entry `101-gamedata.quack` x 1.5 (truncated), 100 when 0.
- **Hauling**: `canHaveSomeOfThese` (slot 148, `FUN_14029cbc0`) accepts only `item function` 17 (severed limbs), so the AI's haulers bring it nothing else. Default task 26 LOOT_TARGET.
- **Save**: no override of the producer's keys, so `active` is not saved (a loaded furnace is off); the level is `consumption amount-0`, the progress `production amount`.

### Resources

- WATER and GROUND: band = 1 between `<p> altitude min` and `max` (height y), ramping to 0 over `<p> altitude fade`
  below the min and above the max, else 0; value = band x `<p> mult` + `<p> min`.
- STONE: the coverage noise of [foliage](../formats/foliage.md) (`Foliage_NoiseHashC`, five octaves) at scale `stone noise
  zoom` x 1000, cutoff `stone noise cutoff`, boost 3 and cap 1 (**Verified** bytes `141f12ce0`/`ce4`), x `stone mult`.
- IRON, COPPER, CARBON: the nearest rock of that resource within 1500 (3D, squared 2.25e6): its yield x (1 - f),
  f = clamp((d² - r²) / (1500² - r²)), r = the rock's mean horizontal half-size, at least 25 (Meitou always 25: the term
  moves the result under 0.5% for rocks up to 100). No `resources` record or no rock: 0.
- A building that is not the player's (`FUN_140546dd0`: its owner's `+0x250`, the test also used by research
  upgrades and generator fuel) and is no natural site yields 0.5 where it would get 0; the player's yields 0 (Meitou: `ProductionSystem.Create`).
- The farm efficiency also reads the record's `arid`, `green`, `swamp` coverages (crop types 2, 1, 3).
- The prospecting window lists water, fertility, stone, iron and copper from `uses_resources` ([ui-screens](ui-screens.md#prospecting)).

## Crafting

- **Lists.** The functionality's `item crafts` (v0 = finished items the output holds), `armour crafts`, `weapon crafts`
  (MATERIAL_SPECS_WEAPON models), `crossbow crafts`, `backpack crafts`, `robotics crafts`. A weapon bench crafts every
  WEAPON in the `PLAYER_WEAPONS` manufacturer's `weapon types` in each listed model; the model's level is that
  manufacturer's `weapon models` v0 (Rusted Junk 5 ... Edge Type 3 80). `tech level` caps the made level (0: none).
- **Output section** (**Verified**, `FUN_1402b9aa0` code read): `out` is max `inventory footprint width` x max `inventory
  footprint height` (each taken on its own, at least 1) over the records of the item, armour, crossbow, backpack and robotics
  lists; a bench with `weapon crafts` adds every WEAPON record of the database. Meitou: `CraftBench.OutputWidth` / `OutputHeight`.
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
- **Plants**: each has a growth g. Replant (`FUN_1400e48e0`): every g = -0.2 x U[0,1), `plant start` = their minimum,
  `plants harvested` 0, death d 0, clear progress 0, harvest progress 0, farm growth = `plant start`.
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
  simulates the gap (`FUN_1400df6e0`); with no farm keys, the farm growth (not the plants) is `rand() / 32768` (`FUN_1409b1c10`, **Verified**, `FUN_1400e3de0`), which the first tick that grows the plants takes back to the lowest of them, so such a farm is in effect freshly planted. The base game's level files carry no farm keys for town farms (**Verified**: none in `rebirth/zone.21.42.zone`, Shark). The live tick tests no owner (**Verified**, `FUN_1400e5580`): a town farm grows, draws water and is rained on as the player's; only the catch-up ignores water for it.

### Farm plants

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

### Unloaded zones

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

- **Power toggles** (`UseableStuff::getGUIPower` `FUN_140307bf0`, **Verified**): for a player-owned machine whose power figure is not 0, the selection panel's bottom part (section 6)
  gets, for a consumer, a "Battery" toggle (`_recievesBatteryPower`, tooltip "This machine will run off of battery power if the main generators should run low or break down."
  / "...will NOT run off..."), then for every such machine a "Power" toggle (`powerOn`; "This machine is switched On" / "This machine is switched {1}Off"). A click
  (vfunc 161 `FUN_140297a60`, shared by every machine class) flips `powerOn`, sets the Wwise switch `Generator` to `On` / `Off` and posts `UI_Generator` on the GUI object. Meitou: `SetMachinePower`; the bar's green/red On/Off look is an engine choice.
- **Kinds** (registration `FUN_14092da80`, from switching on `vfunc_99`): capacity > 0 a battery, else output > 0 a
  generator; output < 0 a consumer. Function 5 without `consumes` is a wind generator, otherwise fuelled; batteries
  (23) share the generator class (factory `FUN_14057cc70`).
- **Generator output** (`GeneratorBuilding::getPowerOutput`): health x (output + increase) when switched on (`power on`) and
  fuelled for a player owner ("Out of fuel" otherwise; NPC output ignores empty inputs, [below](#npc-generator-state)); **Verified**
  `FUN_140297760`, health the construction fraction. A battery's output is the same health x rate while it holds charge,
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
- **Fuel** (`vfunc_183` on generators): inputs drawn as a producer's, x the load (`GeneratorBuilding::vfunc_167`):
  output / rated x (battery charging + consumer demand) / all generators' output, at most 1 ("Fuel Consumption {1}%").
- **Battery store** (`vfunc_27`): += flow / rate x dt, clamped to 0..capacity.
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
  - Not read by production (slot 150 `FUN_140298700`, slot 109 `FUN_140296e00`), power (slots 163, 166, `FUN_140299370`), `Task_OperateMachine`, or the
    engineers' finder (slot 101 `FUN_1400f6b70`: progress below target).
  Meitou: `Machine.Broken`, saved as `broken`, set and cleared by the dismantle, build, completion and ruin steps; the header (`BuildingSelection.SiteStatus`) and
  turret manning read it, and a town turret's saved `broken` (below). A player-built cage's or safe's lock is a fixture of its own ([locks](locks.md#lock-object)); a broken machine keeps its fixtures registered (doors, cage, bed) and its own lock counts as broken.
- **Status strings** (`GeneratorBuilding::vfunc_114`): "Out of fuel", battery "Charging" / "Draining" / "Stored" / "Empty".
- Data (**Verified**, probe): Small Generator 40, Generator II 100, Wind Generator 50, Small Wind 25, Wind II 100,
  Battery Bank 40 / capacity 60; Weapon Smith -8, refineries -10 to -40, Research Bench III-VI -10 to -30. Power
  research: Upgraded Small Generators +15, Amplified Generators +15, Battery Power / Advanced +10 each, Battery
  Capacity / Advanced +60 each.

### NPC generator state

**Verified** (private decompile `FUN_140297760`, checked against GOG `kenshi_GOG_x64.exe` at `0x140297100` with `objdump`):
NPC fuel generators supply `construction fraction x power output` while `power on`. The empty-input test (slot 149,
`FUN_140298b70`) suppresses output **only for a player-faction owner** (faction slot 11, player marker `+0x250`;
`FUN_140546dd0`). An empty NPC tank therefore supplies power indefinitely. The base game's 47 empty fuel-generator
placements are not evidence of zero generation; this affects the base game as well as mods. Research increases apply
only to the player's buildings (`FUN_14082ec70`). No separate `broken` or `destroyed` check exists in this output getter;
construction damage scales the result rather than imposing a broken-flag cutoff.

**Verified** (private decompile `FUN_140297600`, `FUN_140298700`, `FUN_14029a510`, `FUN_14029a370`, `FUN_1402fdd80`):
the ordinary production path can consume stocked inputs, scaled by generator load, without an ownership exemption.
This does not make fuel a prerequisite for NPC grid output: exhausting that stock still leaves the NPC-output exemption.
Meitou's NPC grid does not simulate those generators' inventory production or drain; its output needs no fuel state.

**Verified** (`FUN_140555650`, `FUN_1400b0ab0`, `FUN_140559ad0`, fraction getter `FUN_140547200`): when the guard
allows loading, a present state's missing `power on` is false and missing progress is zero. Progress divided by the
material target, capped at 1, supplies health; positive infinity means the full target, and a zero target completes
immediately. The original's complete flag does not replace a partial progress value. Meitou's player-site import
currently differs: `BuildingSave.Read` substitutes the target when `is complete` is true. NPC generation follows the
original fraction. `FixtureRegistry.Saved` replaces the whole `TownBuilding.LevelState` when present, without inheriting
missing keys. No state gives a complete, switched-on building (`FUN_140562340`). `broken` is not a separate output cutoff;
the existing ruined-placement exclusion remains. Construction targets are cached with the database version.

**Verified** (`FUN_140297810`, GOG `0x1402971b0`, and [rotating parts](#rotating-parts)): wind multiplies that switched,
health-scaled output by the first spawned wind-speed rotor's share; a result below 1 gives zero. No rotor or no loaded
physics collection gives zero. Meitou uses the placement's rotor and local weather, retaining its existing choice to
model every placed rotor even when no render entity is loaded.

**Verified** (clean GOG base load order, installed-level check): all 170 placed generators have a level state and
`construction progress`; missing progress count is 0. The 47 fuel placements provide 3560 rated power in a fresh-zone
simulation using their real building records and level states, with fuel unused. This is an aggregate harness check,
not a runtime measurement or a per-town distribution; wind output still depends on local weather. Sparse mod states
are covered synthetically. No original-game runtime measurement is claimed.

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

### NPC town machines

- **Output add** (`FUN_1402984b0`, **Verified**): when a producer's accumulator reaches 1 and its output inventory cannot take the item, the accumulator resets to 0, so a full output loses
  the progress (and `isMachineOutputFull`, status 1, stops the operators: row 87 needs !63). Nothing else in the production building removes items.
- **Task_EmptyMachine** (`FUN_14033cd10`, **Verified**): at the building, takes its output item types in order and moves units into the character's inventory while there is room
  (backpack included); a production building at status 1 goes to 2 after a take; ends when nothing is left.
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

## House sales

Rules of `For sale:` and the price: [selected building](ui-screens.md#selected-building). **Verified** on the base game
(`HousePurchaseTests.The_Hub_has_a_ruined_Storm_House_for_4800_in_the_original_data`): The Hub's ruined Storm Houses have
interior meshes but no functionality record, their level states lack `sell home` while their resident templates
(`44421-rebirth.mod`, `44426-rebirth.mod`) set it; target 16, owner's `building cost mult` 2: 0.375 × 16 × 2 × 400 = 4,800.
`sell home` is the flag of the template the building carries, and a building carrying none is for sale (`FixtureRegistry.SalePrice`).

## Ruined by residents

**Verified** (decompile): the squad setup `FUN_14057f1f0` calls slot 104 (set destroyed) on the squad's home with `building ruined` of its template and slot 105
(`FUN_14055b520`, can be destroyed): true when the building's class (+0x198) is 7, GatewayBuilding, the `is wall gate` building (**Verified**, [pathfinding](pathfinding.md)), or it, or a part reached through `parts` at any depth, has a
`destroyed mesh`. So a home whose template is not ruined is set intact, a ruin of the level file too: set destroyed with false (`FUN_140557280`) clears the flag
(+0x1a1) and builds the standing building, and `FUN_14057f4c0` runs the setup for every home, a template with no members too (**Verified**, decompile).
**Observed** (the original's save `bench-bases-2026-10-27`): Blister Hill's Small Shack `23.22:667-Newwworld-INGAME`, destroyed in the level file, is saved
standing under `Civilian solo holy -small`. **Observed** (data, base game and its mods): 26 SQUAD_TEMPLATEs set `building ruined`, among
them `_ruined empty building (DEFAULT)` (`44421-rebirth.mod`), the `default resident` of Old Village (Deadcat, `50243-rebirth.mod`), whose level states are
intact and name no template. Its houses are ruins in the original (issue #145).

Meitou (`ResidentRuins`): before anything reads the level data's `destroyed`, every town is housed by the population's rule ([town residents](factions-squads-towns.md#town-residents))
and each home of a ruined template that has a destroyed mesh is set destroyed in its level state; 71 base-game buildings (Old Village 10 of 10, Abandoned Town 9 of 9,
the library ruins). Any other home the level file has destroyed is set standing (`WorldLevelData.ResidentRestored`, `TownBuilding.RestoredByResidents`); 34 base-game
buildings, e.g. the Snailhouse bar of the Waystation in The Hook (`17.53:4-rebirth-INGAME`, issue #630), the Lost Library, the Lost Armoury, Mourn's shop, and one or
two `Trashed building` homes each in Bast and Endin, whose other buildings are ruins by their default resident. A saved `destroyed` false does not undo a level ruin
unless the player's repair saved it (`meitou town construction`; engine choice: saves made before this pass wrote those buildings intact). A saved `destroyed` true
does not ruin a home the residents stand up while the save has the same `residents template` in it and no `meitou town construction` (engine choice: saves made before
the homes were stood up wrote the level file's ruin; `FixtureRegistry.SavedLevelRuin`). A home housing ruins now stands when the save has it intact under another `residents template` that is not `building ruined` (engine choice, `FixtureRegistry.SavedStanding`: the save was housed differently, e.g. before a mod added residents; **Verified** headless on issue #673's save, where `Bounty hunting for minor factions.mod` adds a Hub resident and the squatters' ruined template lands on the bar's Storm House `20.32:4933-Newwworld-INGAME`). A town's baked distant mesh is a fixed file of the install, drawn as it is (as
in the original). Meitou: `ResidentRuins.CanBeDestroyed` tests `is wall gate` for the class.
