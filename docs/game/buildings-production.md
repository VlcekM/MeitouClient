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
| +0x1a0 byte | is a door (set by the DoorStuff constructor `FUN_14029fb40`): build work x3, never completes itself, no refund | - |
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
   `amount = lerp(0.3, 2.0, clamp(engineering, 0, 100) / 100) * dt`, `dt` = the character's update time
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
(`FUN_140557280`, destroyed off: navmesh/doors restored, **Observed**).

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
1. a source remembered for that item (a per-item map in the AI, re-used while valid);
2. a loose item within 1200 of the character with the same record, not blacklisted and not lying in another faction's
   building (`FUN_140997d80`, **Observed**); the first the world grid query meets;
3. a complete building of the character's town (its current town, else the nearest town within 3953, plus its home
   town; `FUN_1408cbea0`, query mask 0x4002436 = functions 1, 2, 4, 5, 10, 13, 26 read as bits, **Observed**) whose
   owner it may use, that is not the excluded one, holds the item and has items: storages (`StorageBuilding`, slot
   0x3b0 null) first, then production buildings (their output); nearest within the AI's range. An item matches when
   it is the same record, or, when neither is an ITEM (type 4), any record of the same type.
The range is the AI's mode `AI+0x2bc` (**Verified**, constructor `FUN_1406236d0` sets 2; character orders 8/9 of
`FUN_1405caa50` change it, which HUD buttons send them is **Unknown**): mode 2 squared distance <= 4.246733e7 (about 6517)
from the character; mode 0 `(18 + 40 + 5)`, mode 1 `(18 + 200 + 5)` (AI+8 = 18, AI+0xc = 5), others 1000.
Task 247 is `Task_EmptyMachine_BuildMats` (+0x78 clear) for a building target, `Task_Pickup` for an item. At the building
the update lists its records (a storage: every record it holds; a production building: its output), takes the first
record whose count (slot 5, `FUN_1403411c0`: always 2) is above 0, i.e. the first listed record, and moves up to 2 units
while the character has room, then ends. Meitou takes the material instead of the first listed record (engine choice).

### Builders and dismantle

Task ids and AI conditions: [ai-tasks](ai-tasks.md#build-tasks). Engineer job (**Verified**): row 125 "Engineer" has no
lists, effect 2 and the finder `FUN_140999060`, which ignores the job's target and runs `BuildingFinderForEngineers`
(`FUN_1409a6900`, `FUN_1409a6c10`) from the character's position. Candidates are
buildings of the character's owner that are not blacklisted, dismantled, holding dismantle work (+0x168 > 0) or paused,
and not (slot 0x2f0 = 19 and destroyed, slot 0x330 = +0x1a1); an incomplete building (or one needing repair, slot 0x328)
is a candidate, a complete one contributes its mounted children that need work. Score `max(2 (1 - d^2 / 9000^2),
0.0001)` (d 3D), x0.5 when slot 89 is set, / (n + 1) with n = characters already working it; a stalled site the
character has no material for (condition 50 false) is multiplied by the planner cost of the 115 finder (`FUN_14060f1d0`)
and skipped at 0, i.e. when no source exists. The highest score wins. No cap on builders per site (**Observed**: the
score and the 0.75 falloff spread them). Work distance (`isAtLocation`) is **Unknown**.

Repair (**Verified**, Building slot 0x328 `FUN_1400f6b70`, `Task_Build` `FUN_14034a440`): a building needs repair when
`progress < target`; building health is construction progress. Orders 2 and 95 are both `Task_Build`, which pushes the
same build work and ends once complete and `progress / target >= 1` (`FUN_140547200`). A complete building never stalls
(`FUN_14054a3f0` tests only incomplete ones), so repairing takes no materials. What lowers a complete building's
progress (attacks) and whether slot 70 clamps at the target are **Unknown**.

Purchased ruins: **Observed** in the user's original-game comparison: right-clicking a purchased damaged house exposes
Repair, and selected characters supply building materials and work. This is reconstruction, distinct from the complete-house
repair above. Meitou uses the destroyed-building transition and material gate already described here, activated by the explicit
order; the exact original activation timing remains **Unknown**. Implementation and regression coverage:
[town-house repairs](../simulation.md#buildings).

Dismantle (**Verified**, `Task_Dismantle` `FUN_1403518c0`, Building slot 73 `FUN_1402a2860`): refused with "I have to
dismantle the furniture first." / "...unmount the buildings first." while those exist; a door redirects to its
building. Animation `stonemine`, engineering XP as for building. Per update `amount = dt x lerp(0.05, 4, eng/100) x 4`;
slot 73 clears complete (a complete building first drops to `target - 1`), then `progress -= amount x rate` (no option
or CONSTANTS factor). At progress <= 0: mounted turrets released, +0x162 set, sound `Thing_Complete`, refund, building
removed.

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
| "Can only be placed on certain locations (marked by blue nodes)." | `PreviewBuilding_Snapping` not on a snap node |
| "Too close to another building." / "No valid usage nodes." | a footprint node box overlaps physics shapes (group mask 0xfffde40, up to 32) or another queued ghost; second text when every usage node is invalid |
| "There are characters in the way." | an object in a node box is rejected by `FUN_1407d1440` (**Observed**) |
| "The building blocks another building use nodes." | the footprint covers another building's usage node |
| "The building has one or more parts on invalid ground." | a BUILDING_PART with `ground type` LAND has its node centre below y = 98 (water level, **Observed**) |
| "Invalid placing floor number. ..." | inside a building above floor 0 a node overlaps floor collision; or a ceiling ghost finds no ceiling |
| "Ground needs to be more level." | vertical margins or slope (below) |

- **Towns** (`FUN_140926d50`, `FUN_140926eb0`): radius = `town radius mult` x base (town 2500, village 937.5, ruins
  875, point of interest 437.5, others 1250). A `creates player town` building is blocked within 2.5 x radius (1 x for
  ruins/POI) or 2000 of the centre; other buildings only within 1 x radius, so they may stand outside any town.
  Player towns never block.
- **Outposts** (**Verified**, `FUN_1404d6d90` per queued ghost on Confirm, then `FUN_1404db180`): a ghost in or on a
  building takes that building's town; else the nearest town of the player faction within 5000 (X/Z); else the town of
  the nearest player building within 2500 (the decompile adds an X/Z > 10000 test there that looks unreachable,
  **Observed**); else a `creates player town` building that is not a wall makes an outpost at its position
  (node position plus bounding-box size, as decompiled), and every player building whose town is a player town or none
  and whose nearest town of any faction within 8000 is the new one moves into it. Walls (`links together`, ghost class
  `PreviewBuilding_Wall`) never make one there; after the pass, queued walls still without a town take the last player
  town among the queue's ghosts, else the nearest town of any faction within 16000 of their mean position, else a new
  outpost there. A global flag (`DAT_142134690+0x298`, meaning **Unknown**) skips all of this.
- **The outpost** (**Verified**, `FUN_140934e10`): a `Town` made from the TOWN `player_town_template` (base data: name
  "Your Outpost", type 1, `town radius mult` 1, no faction), owned by the player faction, named "Your Outpost",
  `public` false, `plevel` 0, added to the player-town list (town manager +0x68) that the save writes
  ([player outposts](../formats/save.md#player-outposts)). On load (`FUN_14092ae80`) a player-faction town within the
  radius of another player town is removed with "Player town '<a>' withing boundaries of <b>".
- **Slope** (slot 31 `FUN_1404daa20`): ground normal from four rays at the rotated bounding-box corners (outdoors) or
  one ray (indoors); slope = angle from vertical, compared with `max slope` in degrees. With `match slope` (forced for
  interior furniture and nodes) the ghost tilts to the ground and a slope over the limit fails; without it the tilt is
  blended by the player tilt factor and clamped, never failing. Also fails on an object surface more than 5 deg off
  the hit normal, or on ground with normal.y < 0.2 (`FUN_1404d5420`).
- **Vertical margins** (`FUN_1404d9c00`, `FUN_1404d21e0`): ground height by a ray from 1000 above; nodes marked
  `above ground` must be at or above it, others at or below; both minimum margins must be >= 0. `vertical position
  tolerance` widens this for single-kind buildings (**Observed**).
- **Indoors** (`FUN_1404d69c0`, flags read in `FUN_1404debc0`): an `is interior furniture` or `is node` building
  is valid with all node points in one player-owned building; otherwise only if it is `is exterior furniture` or
  `is node` (**Verified** by code). So interior-only furniture must be inside, while interior+exterior items (Campfire,
  Camp Bed, storage boxes, fish racks) go either way. Without `is interior furniture`
  the check passes. `ceiling placement` uses the ceiling ghost (hangs 1 below the hit of an upward ray).
- **Resources** (`FUN_1404d9ea0`): `world resource mining` STONE/WATER/GROUND from a noise field; IRON/COPPER/CARBON
  need the biome's `resources` and a resource node within 1500, falling off from the node radius (>= 25) to 1500.
  Farms: fertility `1 - (1 - v) x fertility effect` (0 at or below `minimum fertility`), yield = biome
  dry/green/swamp weights x the farm's factors x fertility.
- **Controls** (bindings `FUN_1403636c0`, applied in `FUN_1404d5420` x frame time): rotate +-80 deg/s, move up/down
  +-60 units/s (6 with the slow modifier, capped by the margins; offset reset above 1000), tilt factor +-1/s in 0..1,
  default 0.2.
- **Commit** (`FUN_1404d5130`, `FUN_1404db5a0`, `FUN_1404d72a0`): a click queues the ghost (position relative to the
  parent building, or height above terrain, and floor) and spawns a new ghost; Confirm assigns towns, then the factory
  `FUN_14057cc70` creates each building with its town, the player faction, rotation, parent building and floor. No
  terrain is flattened; outdoor buildings with `path mode` and `build threshold` 0 trigger a navmesh rebuild of their
  area (`FUN_1404d2c80` -> `FUN_1403a66c0`, **Observed**). That a new building starts at progress 0 with nothing
  delivered is **Observed** (the factory's complete flag is false).

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
  chain a polyline). Right click drops the line. The first click posts "Blue arrows point outwards, towards enemy.";
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
  the shortest piece - 10 stays open. A climb steeper than `max slope` continues straight until the hill rises
  `building height` - 10 above it (`FUN_14042a2b0`, **Observed**). With a snapped end, later frames shorten every piece
  by remaining / count so the run closes on the point (**Observed**).
- **Orientation** (`FUN_1404278b0`): +Z towards the piece's end (pitching with the slope), +X level (Y x Z).
- **Snapping** (`FUN_1404289f0`, `FUN_140427950`): candidates are the queued wall ghosts and every loaded building with
  `links together` (no owner or family filter seen; Building slot 4 filters, **Unknown**). Link points: start =
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
- **Walls after building** (**Observed**): "Wall termites! - Your base became inaccessible. You need at least 1 gate in
  your walls." (`FUN_1402ef1f0`) destroys a trapping wall with `destroyed navmesh`, else dismantles it; WallBuilding damage
  `clamp((hit1 x 2 + hit0 x 0.5) x 0.5, 10, 60) x 0.003` off health (slot 62, **Verified**).

### Build menu

**Verified** (`FUN_1406bff30`, `FUN_1404e4480`, `FUN_1404e4210`, `FUN_14082ea60`, `FUN_140830b40`): every BUILDING is a
candidate; category = `building category` (default "misc"), group = `building group` or the building's own name.
A building is offered when it is in the research manager's unlocked set, filled from each completed RESEARCH's
`enable buildings` (or the window's debug flag +0xa8 is set); a category shows when one member passes. In the base game
88 buildings are unlocked by research; categories DOORS, sign, nodes, moor, CANNIBAL, EXTERIOR, Furniture and misc have
none and never show. A new game starts with the research `_Default Start` (`5359-gamedata.base`) completed (**Verified**: the exe
string; **Observed**: `finished0` of the five sample saves); Meitou adds it to every new game (`ResearchGate`). A group's default pick is the member with the highest `build materials`. Sort order **Unknown**.
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

**Constants** (**Verified**, CONSTANTS loader `FUN_14086b2b0`): `production speed` P = 2, `research rate` 0.3,
`research level increase rate` 1.3. G = the day length at speed 1, 2618.18 s (`FUN_14066cd50`, the exe's float); per-day
data rates are divided by G, per-hour by G/24. dt = the scaled frame time. The option sliders (production `142133590`,
research `14213358c`) default **Unknown**; 1 below.

## Production

**Per operator and frame** (**Observed**, `ProductionBuilding::vfunc_150` `FUN_140298700`, called by
`Task_OperateMachine` `FUN_14035bc90` with the operator's skill factor s):
`amount = yield x outputRate x upgrade x dt x P x health x powerSat x s x option`. Several operators add up.
Nothing else calls slot 150 (**Observed**: the only decompiled caller of vtable +0x4b0 that also tests the building), and neither it
nor the output add (`vfunc_184` `FUN_1402984b0`) tests the owner: any building, the player's, an NPC town's or a rock, makes output
only while someone operates it.
- s = `lerp(stat/100, 0.5, 1.4)` of the effective stat `stat used` (`FUN_1408852a0`; 1 without an operator; the lerp
  helper `FUN_140015b63` is `a + (b - a) t`).
- outputRate = BUILDING `output rate` x functionality `production mult`; with `output per day`, / (2G) (loader
  `FUN_14029e7b0`). So a building makes `output rate x production mult x P/2 x s` cycles per game day.
- yield (slot 120 via offset `0x46c`): 1 without `world resource mining`; else the resource at the position
  ([resources](#resources)), at least 0.1 for a natural site; 0 becomes 0.5 for a building not the player's.
- health x powerSat (slot 109): health `min(1, hp/max)`; powerSat the granted share of the demand, 1 with no demand.
- upgrade = the research `production mult` of the building ([research](#research)), default 1.

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
  (1.2, 1) and `max operators` (3, 2). So hand mining is `Task_OperateMachine` on the rock (**Observed**); the ore waits in
  the rock's output until the player takes it (**Observed** by the owner in play; no rock-specific transfer in the operate update). No
  rock has a saved state in two saves (253 zone files, **Observed**).
- A building that is not the player's (`FUN_140546dd0`: its owner's `+0x250`, the test also used by research
  upgrades and generator fuel) yields 0.5 where it would get 0; the player's yields 0.
- The farm efficiency also reads the record's `arid`, `green`, `swamp` coverages (crop types 2, 1, 3).
- The prospecting window lists water, fertility, stone, iron and copper from `uses_resources` ([ui-screens](ui-screens.md#prospecting)).
- **NPC mines** (**Observed**, two saves of one game): NPC towns' mines have production states; Quarry Mine A 14 of 16,
  Quarry Mine B 16 of 18 and Stone Mine 8 of 8 with `production amount` > 0 (at most 0.19), none holding output items; Ore
  Mine 0 of 8; Ore Drill III (Automated) `resource mult` 0.5 (the not-player fallback). So NPCs work them (the town goal 43
  Staying home leads to 221 operating, [ai-tasks](ai-tasks.md#town-goals)). **Unknown:** what empties an NPC mine's output.

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
  the item level. A weapon below the crafter's skill with no critical comes out as a lower model
  (`uses_weapon_crafts_weapon_models`; which model is **Unknown**, Meitou takes the best at or below the skill).
- **Research** `enable item`, `enable armour`, `enable weapon type`, `enable weapon model`, `enable crossbow`,
  `enable backpack`, `enable robotics` unlock crafts, v0 = the repetition that does it; `repeats` and `repeat mult`
  (cost x mult per repetition). Whether a craft no research lists is available from the start is **Unknown** (Meitou: yes).
- **Save** (GAMESTATE_BUILDING, `vfunc_17`/`18` `FUN_1402b8440` / `FUN_1402b8ab0`): float `num crafts`, bool `repeat`,
  per queued k strings `current<k>` (item) and `currentExtraData<k>` (weapon model), float `current prog<k>`, int
  `crit<k>`; partly drawn items as `craft partial item sid <k>` / `val <k>` / `size` (Meitou does not write these).
- **UI:** overview CraftTab (`CraftingStationsList`, `QueueItemsPanel`, `CraftQueue`, `QueueRepeat` "Repeat Queue",
  `QueueDescriptionPanel1/2`); the building window's `CraftingPanel` (`CraftingName`, `QueueButton` "QUEUE...").
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
  (`FUN_1400e4320`, `vfunc_91`); F is also the yield.
- **Plants**: each has a growth g. Replant (`FUN_1400e48e0`): every g = -0.2 x U[0,1), `plant start` = their minimum,
  `plants harvested` 0, death d 0, clear progress 0, harvest progress 0, farm growth = `plant start`.
- **Tick**, h = dt/(G/24): outdoors, rain fills each input level by `rain x w x dt x 3` (to 1 at most). Water draw
  (`FUN_1400df4c0`): an input whose level is below `w x dt` takes one item from its section, or fails (level 0); then
  level -= `w x dt`. Growth step `inc = h x F / growth time`, x `drought multiplier` when the draw failed, x health x
  powerSat when the building uses power. Farm growth < 1: each plant with -1 <= g < 1 gains inc; farm growth = the
  lowest such g, capped at 1. 1 <= growth < 2: growth += h / `harvest time` (ripe; at 2 it rots). Growth >= 2:
  d += h / `death time`. While 0 < growth < 1 and `drought death time` > 0: a failed draw adds h / `drought death time`
  to d, a good one takes it off while 0 < d < 0.6. d >= 1 or every plant harvested: replant.
- **Harvest** (an operator, skill factor s): when growth >= 1, d < `death threshold`, not every plant harvested and the
  output is not full, harvest progress += health x powerSat x P x dt x s x harvest rate. At 1: the output gets, per whole
  unit of `output per plant`, one item with probability e (the fraction rolled once); the plant's g = -10, `plants
  harvested` += 1; the work point moves to the next plant. No room: status 1. A crop of zero items floats "Failed".
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

## Research

`ResearchBuilding::vfunc_150` (`FUN_14029b8d0`, **Observed**): an operator of a player-owned bench posts event 8 with
`outputRate x upgrade x health x powerSat x s` (s from `stat used` 4, science); the dispatcher `FUN_1407b65b0` passes it
to `FUN_140836b70`: contribution k of the frame is x 0.6^k (counter `+0x174`, cleared with the rate readout by
`FUN_14082e690`), the queue's first item gains contribution x dt seconds and completes past
`hours x G/24`, hours = `time x 0.3 x 1.3^(level - 1) / option` (`FUN_14082ed10`). Benches: `output rate` 0.85, 0.9,
0.95, 1.0, 1.05, 1.1 (I to VI); functionality `tech level` 1 to 6; power 0, 0, -10, -15, -25, -30 (**Verified**, probe).
- **Levels** (**Observed**): the manager's tech level starts 1; each finished `is level upgrade` research of `level` L
  raises it to L+1 (`FUN_140830880`). A complete player bench raises the bench level to its `tech level`
  (`FUN_14029be00` -> `FUN_140834390`). Available (`FUN_1408333b0`): not `blueprint only`, not finished or queued,
  `level` <= tech level, requirements met (a RESEARCH finished, a BUILDING unlocked; `FUN_140830490`, engine reading
  of a garbled decompile), grouped by `category`.
- **Queue** (`FUN_1408348a0`): needs `level` <= bench level (`FUN_140830a30` names the missing level) and the cost
  paid (`FUN_1408343c0`): `cost` items (x `repeat mult` per repeat, `FUN_1408320a0`) summed over the inventories of the
  player's research benches near the player (`FUN_14082e750`, `FUN_14082e810`), then removed; a paid id is remembered and
  never charged again. Partial progress is kept when an item leaves the queue.
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
  "RESEARCH - LEVEL {1} (CURRENT RATE: {2}x)" (`FUN_140494af0`).

## Power

**Observed** unless noted. Per building: `power output` (generator output, battery rate, consumer draw when negative)
and `power capacity` (battery store, "in seconds" per fcs.def), plus research ([improvements](#research)).
- **Kinds** (registration `FUN_14092da80`, from switching on `vfunc_99`): capacity > 0 a battery, else output > 0 a
  generator; output < 0 a consumer. Function 5 without `consumes` is a wind generator, otherwise fuelled; batteries
  (23) share the generator class (factory `FUN_14057cc70`).
- **Generator output** (`GeneratorBuilding::vfunc_166`): health x (output + increase) when switched on (`power on`) and
  fuelled ("Out of fuel" otherwise). Wind: x a value read from the turbine's animation (`FUN_1404cbd90`, **Unknown**
  units), nothing below 1. A battery's output is its rate while it holds charge.
- **Demand** (`vfunc_163`): a consumer's |output + increase|, x operators / raw `max operators` when that is above 0; a
  battery's rate while below its capacity health x (capacity + increase) (`FUN_140297650`).
- **Town frame** (`Town::vfunc_87`): consumers sorted by ascending demand (`FUN_140927350`); each takes
  min(demand, supply left / consumers left). Consumers left short with `gets battery` are counted; if batteries were in
  use the previous frame (town `+0x470`) they share the batteries' output the same way. With no supply left the
  batteries drain by what was drawn (`FUN_1402978d0`) and the town enters battery mode; with a surplus it leaves it and
  the batteries charge from it, each at most its rate. Factor = granted / demand (1 with no demand), x health
  (`vfunc_109`); production, crafting, research and farm growth are scaled by it.
- **Fuel** (`vfunc_183` on generators): inputs drawn as a producer's, x the load (`GeneratorBuilding::vfunc_167`):
  output / rated x (battery charging + consumer demand) / all generators' output, at most 1 ("Fuel Consumption {1}%").
- **Battery store** (`vfunc_27`): += flow / rate x dt, clamped to 0..capacity.
- **Efficiency and "has power"** (the sound emitter's inputs, [audio](../audio.md#point-emitters)): slot 108 = operators / raw `max operators` x `vfunc_109` x `vfunc_119`, with
  `vfunc_119` = yield (`vfunc_92`) x the BUILDING's raw `output rate` (`+0x1a8`) x the research production mult; `FUN_140299370` is true for a generator (fuel and switch ignored), a building drawing nothing,
  else `power on` and the town's generator output (`Town::vfunc_87` `+0x474`) above 0 or battery mode with batteries' output (`+0x480`) above 0. Town `+0x47c` is the consumers' total demand.
- **Switch** (`FarmBuilding::vfunc_161`): flips `power on` (slot 99), then the `Generator` switch `On`/`Off` and `UI_Generator` on GUI object 110.
- **Save** (building `vfunc_17`/`18`): bools `power on`, `gets battery`, `broken`; float `batterycharge`.
- **Status strings** (`GeneratorBuilding::vfunc_114`): "Out of fuel", battery "Charging" / "Draining" / "Stored" / "Empty".
- Data (**Verified**, probe): Small Generator 40, Generator II 100, Wind Generator 50, Small Wind 25, Wind II 100,
  Battery Bank 40 / capacity 60; Weapon Smith -8, refineries -10 to -40, Research Bench III-VI -10 to -30. Power
  research: Upgraded Small Generators +15, Amplified Generators +15, Battery Power / Advanced +10 each, Battery
  Capacity / Advanced +60 each.

**Storage** (**Verified**, 603 buildings): 87 have `has inventory`; default 18 x 18, `itemtype limit` 4; `stackable bonus
mult` 1 to 100000; `limit inventory` restricts the items. Grid (**Observed**, the building loader): a building whose `limit inventory`
names exactly one item is that item's `inventory footprint` (Iron Plates 4 x 3, Building Materials 6 x 3, steel bars 6 x 1), else
`storage size`, for built and town-placed storages alike (`MachineData.StorageGrid`). Its window (**Observed**, screenshots) is
`Kenshi_InventoryGenericWindow`: caption STORAGE, the one limited item's name as heading, `Max Capacity: N` with N the `stackable bonus
mult`, the line red with "(Full)" once the first stack reaches it (exact red **Unknown**). Locks and containers: [locks](locks.md).
Water Tower takes only Water and `Storage: Ore` only Raw Iron, both with mult 100 and functionality `Resource
storage` (function 2); Large Barrels also hold Water.

### Hauling

- **Tasks** (**Verified** classes, **Observed** meaning): `Task_EmptyMachine` (92 "Collecting resource", 284 "Looting")
  moves a building's output into the character's inventory while it has room (`FUN_14033cd10`). No GOAP row has "output not
  full" (!63) as its effect, so a full output blocks operating (87) rather than leading the planner to 92 (**Observed**, table). `Task_FillMachine`
  (93 "Filling machine", 124 "Hauling to") puts carried items, main grid and backpack, into a machine or a storage
  (`FUN_140344510`). GOAP rows: [ai-tasks](ai-tasks.md); states 62 machineHasEmptyInputs, 63 isMachineOutputFull,
  66 haveSomeResourcesFrom, 67 haveSomeResourcesFor, 82 gotInventoryRoomForResources, 169 isMachineInputInvalid.
- **Option** (**Verified**, string): "If they have nothing else to do, characters will automatically put any excess
  materials they are carrying into storage."
- **Jobs** (**Verified**, [perma-jobs](ai-tasks.md#perma-jobs)): Shift with an order on a machine adds job 87 "Operating machine";
  on a storage 124 "Hauling to" (several targets allowed); an automatic machine (152) adds 152 plus 124 on it. The first job in a
  character's list that can run wins; operating is skipped while the machine's output is full or its inputs are empty (task 87's start
  conditions), so a character moves down its list and returns when the machine can work again.
- **Unknown:** how the original picks the storage (nearest, same town, a range), when an operator leaves a
  machine to haul, and where a 124 job takes its items from.

## Unknowns

- How often Building slot 28 resets the builder counter, the work distance of builders, which HUD orders set the AI
  range mode, and what `FUN_14025c6d0` (holding dismantle work) tests.
- Event types of the queue at `DAT_1421346d8` other than 8 (research progress) and 11 (build work).
- The wind generator's animation value (its output share). `vfunc_0x520` (a consumer counted as active): 1 for every class but `LightBuilding`, whose answer is in [lights](../formats/lights.md#when-a-light-is-on).
- Option slider defaults; the third value of reference triples; the record field of `BuildingClassType`.


## House sales: level-data inputs

**Verified** against base-game The Hub placements and referenced records by
`HousePurchaseTests.The_Hub_has_a_ruined_Storm_House_for_4800_in_the_original_data`:
ruined Storm Houses have interior meshes but no functionality record; their level states omit
`sell home`, while resident templates `44421-rebirth.mod` and `44426-rebirth.mod` enable it.
Their target is 16, owner faction's `building cost mult` is 2, and the documented ruined-house
formula yields 0.375 × 16 × 2 × 400 = 4,800. Explicit saved/level sale flags take precedence
in Meitou; absent flags fall back to the resident template. This fallback is an engine reading
of the data, not verification of the original callback. See [selected-building rules](ui-screens.md#selected-building).
