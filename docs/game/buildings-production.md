# Buildings and production

How the original game models buildings: placing and constructing them, production and crafting, power, storage,
farming, and research and tech. Time units and the per-frame `dt` come from [game-loop.md](game-loop.md); skills and
the effective-stat multipliers that feed production are in [character-stats.md](character-stats.md); the zone and
draw-distance use of building functions is in [../formats/zones.md](../formats/zones.md); the FCS record layout is in
[../formats/fcs-mod.md](../formats/fcs-mod.md). Prices and trade are in [economy.md](economy.md); who works at a
bench (job assignment, tasks) is in [ai-tasks.md](ai-tasks.md).

## Sources

- Decompilation of `kenshi_x64.exe` (Ghidra 12.1.4 dump; addresses are in that binary, written `FUN_...` as in the
  other docs). Class names are the game's own, from MSVC RTTI: `Building`, `UseableStuff`, `StorageBuilding`,
  `ProductionBuilding`, `FarmBuilding`, `CraftingBuilding`, `FurnaceBuilding`, `GeneratorBuilding`,
  `WindGeneratorBuilding`, `RainCollectorBuilding`, `ResearchBuilding`, `TurretBuilding`, `PreviewBuilding`, and the
  task classes `Task_Build`, `Task_AddMaterialsToBuilding`, `Task_OperateMachine`. `Town` is a separate settlement
  class (RTTI names `Town` and `TownBase`), not part of the building chain. Where a function is shared by several
  classes it appears under the first class's slot name (`FarmBuilding::vfunc_N` in the naming pass, although the
  slot is shared with `ProductionBuilding`, `StorageBuilding` and others); several addresses below are one-instruction
  thunks that jump to the real body, and the catalog says "thunk" where only the thunk was identified.
- `fcs.def` and `fcs_enums.def` in the install (field names and the editor's descriptions, paraphrased) and the
  editor `forgotten construction set.exe` (decompiled with ILSpy outside the repo) for the `BuildingFunction`,
  `BuildingClassType` and `MiningResource` enum numbers.
- Base-game data read with the repo's `GameDatabase` through a throw-away probe (`MeitouClient-re/probes/buildings`,
  re-run independently in `probes/buildings-verify`; the four core files `gamedata.base`, `Newwworld.mod`,
  `Dialogue.mod`, `rebirth.mod`, no `mods.cfg` mods). It dumped 603 `BUILDING`, 86 `BUILDING_FUNCTIONALITY`, 181
  `RESEARCH`, 27 `FARM_DATA` and 24 `ENVIRONMENT_RESOURCES` records. "Base game" below means that load order.
- Disassembly (Ghidra headless and a direct read of the exe bytes, not decompiler output) of `FUN_14055bf10`,
  `FUN_140554470` and `FUN_1405595a0` to check the arithmetic where the decompiler lost a floating-point return value.
- No game session was run. Labels: **Verified** = a probe read of the data, disassembly, or two independent
  places in the exe that agree; **Observed** = read from one decompiled function; **Unknown** = open.

## Class map and BuildingFunction

Every placed building is a `Building` subclass chosen from the `BUILDING_FUNCTIONALITY` function number by a class
factory in `FUN_14057cc70` (**Observed**; the same function is catalogued in [../formats/zones.md](../formats/zones.md)
as `Zones_CreateDoor` because it also creates doors; the switch distinguishes at least functions 1/4, 2, 3, 5/23, 8,
10, 12, 14, 15 and 26, and treats 0 and 18 as plain buildings; zones.md names function 5 as the generator and 12 as
the turret, which is consistent with the enum below). The hierarchy is `Building` -> `UseableStuff` ->
`StorageBuilding` -> `ProductionBuilding` -> `FarmBuilding`, `CraftingBuilding`, `FurnaceBuilding`,
`GeneratorBuilding`, `WindGeneratorBuilding`, `RainCollectorBuilding`; `ResearchBuilding` sits in the same layer
(**Observed**, inferred from the shared vtable slots in `rtti.tsv`; the base lists were not read). `PreviewBuilding` is
the ghost shown in build mode. The saved per-building state (`GAMESTATE_BUILDING`: `construction progress`, `production amount`, `power on`, `batterycharge`, ...) is in [../formats/save.md](../formats/save.md#zone-files-buildings-and-loose-items-of-a-zone).

`BuildingFunction` numbers (**Verified**: editor enum `BF_*` read with ILSpy, and the `function` field of every
probed functionality record falls inside 0 to 30; the names agree with the record names except the oddity below): 0
ANY, 1 MINE, 2 RESOURCE_STORAGE, 3 RESEARCH, 4 REFINERY, 5 GENERATOR, 6 BED, 7 TRAINING, 8 CAGE, 9 SHOP, 10
CRAFTING, 11 CORPSE_DISPOSAL, 12 TURRET, 13 GENERAL_STORAGE, 14 ITEM_FURNACE, 15 LIGHT, 16 TABLE, 17 CHAIR, 18 FLUFF,
19 SHELL_WITH_INTERIOR, 20 WALL, 21 GATE, 22 DOOR, 23 BATTERY, 24 THRONE, 25 SKELETON_BED, 26 RAIN_COLLECTOR, 27
MINE_NATURAL, 28 STEERING, 29 ENGINE, 30 LIQUID_TANK. The functionality record named "Cooking pot" carries function
11 (the same number as "Corpse disposal"), which looks like a data quirk (**Observed**, probe). `MiningResource`:
NONE, IRON, STONE, COPPER, CARBON, WATER, GROUND (0 to 6, **Verified** from the editor enum). The editor also has a
`BuildingClassType` enum (**Verified**, editor enum): 0 FLUFF, 1 DOOR, 2 USABLE, 3 STORAGE, 4 PRODUCTION, 5 RESEARCH,
6 CRAFTING, 7 GATEWAY, 8 TURRET, 9 WALL, 10 ITEM_FURNACE, 11 LIGHT, 12 SHELL_WITH_INTERIOR, 13 FARM; which record
field carries it was not traced (**Unknown**).

A `BUILDING` record lists its functionality records and carries `build materials`, `build speed mult`,
`build threshold`, `power output`, `power capacity`, `output rate`, `max operators`, `max slope` and an optional
inventory (**Verified**: field names in `fcs.def` and the probe). A `BUILDING_FUNCTIONALITY` record carries
`function`, `stat used`, `max operators`, `production mult`, `output per day`, `overrides ingredients`, `is production
building`, `tech level`, `world resource mining`, `hunger rate`, `use range`, and the `consumes`, `produces` and craft
lists (**Verified**: field names in `fcs.def` and the probe).

## Global constants used here

All are read by the CONSTANTS loader `FUN_14086b2b0` into the block at `142133dd0` (same loader and block as
[character-stats.md](character-stats.md); not redefined here). Base-game values (**Verified**: probe of the
CONSTANTS record): build speed 6, production speed 2, research rate 0.3, `research level increase rate` 1.3, `min dismantle
materials percentage` 60, `blueprint price mult` 1.5 (field names as in [game-loop.md](game-loop.md#settings-and-constants) and [economy.md](economy.md#3-constants-price-block)). Where each lands in the block (**Verified**: the loader's
store offsets agree with the absolute addresses the consumers read): build speed `142133f38`, production speed
`142133f34`, `research level increase rate` `142133f20`, research rate `142133f24`, min dismantle (stored as percentage /
100 = 0.6) `142133f04`.

Three options-menu sliders at `142133584`, `14213358c` and `142133590` scale building speed, research speed and
production speed (**Verified** by readers: `142133584` is read only by the construction code `FUN_1405595a0` and
`FUN_14055bf10`; `14213358c` by the research-time function `FUN_14082ed10`; `142133590` by the production and
crafting ticks `FUN_140298700`, `FUN_1402b5a40`). Their defaults are **Unknown** (not read from a saved config) and are
written as 1 below. dt in the per-tick formulas is `DAT_142133794`, the frame time scaled by the game speed
(**Verified** in [game-loop.md](game-loop.md#the-three-time-values): `dt x speed`, 0.01 at speed 0).

**Day length constant.** Many building formulas divide by a value returned by `FUN_14066cd50` (thunk
`FUN_14000e9b2`), a float read from a static slot. Reading the exe bytes gives 2618.18 (= 86400 / 33), the seconds in
a game day at speed 1; this is the day length that [game-loop.md](game-loop.md#the-clock) lists as **Verified** (1 game hour =
109.09 s). It is written G below, so G / 24 is the seconds in one game hour (**Verified**: the slot's bytes plus the
cross-check with game-loop.md; each use below is **Observed**). Rates in the data that are "per day" are divided by G,
rates "per hour" by G / 24.

## Construction

**Placement** (**Observed**). Build mode updates in `FUN_1404e27e0` (the build window and its placement messages: [ui-screens.md](ui-screens.md#6-inventory-trade-dialogue-build)) and the ghost in `FUN_1404d5420`. The slope test is
`FUN_1404daa20`, which reads the building's `max slope` field (**Verified**: the field name is referenced by that
function in `strings.tsv`; probe values: 25 for 439 of the 603 buildings, 45 for 125, 10 for 26, 0 for 6, a few
others up to 65; the unit and the exact comparison are **Unknown**); the resource-yield preview (mines, wells, farms)
is `FUN_1404d9ea0`. A building can only be placed where the preview reports no overlap, an acceptable ground slope
and (for resource buildings) a non-zero yield (`uses_resources`, `FUN_140a13540`).

**Construction state** (**Observed**). `FUN_14055ba20` initialises a `ConstructionState`: the build threshold is the
building's `build threshold` field divided by 100, clamped to 0 to 0.95, and the per-building build speed
multiplier (below) is stored in it; the required materials come from the building's `construction` list, or from
`build materials` when there is none. `FUN_14054a3f0` is a "waiting for materials" test: it is true while the
delivered amounts (each capped at its required amount) do not exceed the progress already built, i.e. builders must
wait for deliveries. Builders deliver materials with `Task_AddMaterialsToBuilding` (thunk `FUN_140029b09`, vtable
slot 2) and build with `Task_Build` (thunk `FUN_140036449`, slot 2).

**Build time shown in the UI** (**Verified** by disassembly of `FUN_14055bf10` and `FUN_140554470`, and by the exe bytes of
`FUN_14066cd50`). `FUN_140554470` returns the building's `build speed mult` divided by G / 24 (a per-second rate).
`FUN_14055bf10` (whose only caller `FUN_1404df450` is an info-panel function) multiplies that by the build option
slider, the CONSTANTS build speed and G / 24 and divides the total materials by the result. Because G / 24 cancels,

  hours = materials / (build speed mult x K x opt)

where materials is the sum of the quantities in the `construction` list (or `build materials`), K the CONSTANTS build
speed (6) and opt the build option slider. The result is in game hours. A building with zero materials shows no
time. The multiplier read is the one in the building instance's own field map, which is where research
improvements land (**Observed**; the formula for the research effect itself is **Unknown**).

**Applying work** (**Observed**, `Building::vfunc_70` = `FUN_1405595a0`, shared by every building class in `rtti.tsv`).
Each call receives a time step; it is multiplied by the build option slider and K, then by 0.75 once for each
builder that already applied work earlier in the sequence (the call counter is capped at 15 steps, and wraps to 0
after 100; where it is reset was not found), and by 3 when a mode flag at offset `0x1a0` is set (probably
deconstruction; **Unknown**). The progress counter then rises by the stored build rate (field at `0x18c`, set to 0.5
by the building constructor `FUN_140530870` and changed elsewhere) times that step. The building completes when the
progress reaches the target (field at `0x188`, 1.0 by default); completion raises the "Building_Complete" event with
the message "Building '{1}' construction complete". How the data `build materials` / `build speed mult` turn into the
rate or the target here was **not** traced, so the displayed hours formula above is the only verified link; the
earlier claim that each tick adds dt x mult x K x opt / materials is **Unknown**.

**Discrepancy with fcs.def.** The editor text for `build speed mult` describes build time as materials times the
multiplier. The disassembled UI code divides, so a larger multiplier builds faster. The code is taken as right.

**Dismantling** (**Observed**). `FUN_14029dc10` (called from `FUN_1402a2860`, whose strings include "dismantled", and from
`FUN_14054c810`) drops the building's materials as items at random points inside its bounds: for each material
entry with a positive remaining amount it takes a random fraction between the CONSTANTS "min dismantle materials
percentage" (0.6 in the base game) and 1 of that amount, truncates it and drops at least 1 item. So the 60 is a
refund fraction floor, not a time floor (the earlier text said the latter; the constant is only read in this
function). When this runs (dismantle versus destruction) is **Unknown**.

## Production and crafting

**Per-tick output** (**Observed**, `FUN_140298700` = `ProductionBuilding::vfunc_150`, also reached through
`GeneratorBuilding`'s vtable). The function is called once per operator per frame as `vfunc_150(operator, skill
factor)`: the second argument is that operator's skill factor (the caller is `Task_OperateMachine::vfunc_2`,
`FUN_14035bc90`, which computes it from the operator's stat). While the building is switched on and has either a
progress target or an input list, each call computes

  amount = health x powerSat x resourceYield x outputRate x upgradeMult x skillFactor x optProd x P x dt

where, by the vtable slots it calls: health x powerSat is `vfunc_109` (`FUN_140296e00`; health is min(1, hp / max hp)
by `FUN_140547200`, powerSat the granted power divided by the demand (`vfunc_163`, `FUN_140299630`), 1 when the demand is zero; a gate slot `vfunc_164`, true in `ProductionBuilding`, makes the whole term 0 in classes that override it), and
resourceYield x outputRate x upgradeMult is `vfunc_120` (`FUN_140546cd0` = `FUN_140296df0` x `FUN_140297b30`):
resourceYield is the local resource multiplier stored at offset `0x46c` (1 for buildings without
`world resource mining`; from `FUN_14029c670` otherwise, with a floor of 0.5 for non-natural sites and 0.1 for natural
deposits), outputRate is the stored output rate (below), and upgradeMult the first value of the building's research
upgrade record (`FUN_14082ec70`, default 1). optProd is the production option slider, P the CONSTANTS production
speed (2) and dt the scaled frame time. Since every operator calls it, the factors of several operators add up.

The slot `vfunc_185` (`FUN_14029a510`) then clamps `amount` to what the input stock and the output space allow and
reports whether anything is left. If so, the building enters state 2 and calls the consume step (`vfunc_183`,
`FUN_14029a370`, thunk `FUN_14003a8b9`), which removes `amount` x (per-input rate) from each input buffer, and the
produce step (`vfunc_184`, `FUN_1402984b0`, thunk `FUN_140011a54`), which adds `amount` to a progress accumulator and,
each time it reaches about 1 (0.999), emits the `produces` list. Consumption is therefore continuous and the output
discrete. These slot identities are **Verified** (the vtable slots in `rtti.tsv` and the thunk addresses agree); the
arithmetic is **Observed**.

**Where the data rates go** (**Observed**, loader `FUN_14029e7b0`). The stored output rate is the building's `output
rate` times the functionality's `production mult`. When the functionality has `output per day` set, that product is
turned into a per-second rate by dividing by 2 x G, and it is further divided by a per-building count when that is
above 1. With the default P = 2 and options 1, a full-efficiency building (health 1, power satisfied, skill factor 1,
yield 1) therefore completes `production mult` x `output rate` cycles per game day; in general the daily rate is that
value times P / 2 times the skill factor. For `production mult` 48 (wells), 64 (ore, wheat) or 192 (campfires) that is
the number of production cycles per day. The earlier statement "production mult x P cycles per day" ignored the
factor 1/2 in the loader and is corrected here (**Observed**). A cycle produces the `produces` list once.

**Skill factor** (**Observed**, `FUN_1408852a0`). The operator stat named by `stat used` (`FUN_140884ac0`, the effective
stat from [character-stats.md](character-stats.md#effective-stats-injuries-and-hunger)) is mapped by

  skillFactor = 0.5 + (1.4 - 0.5) x skill / 100

so skill 0 works at half speed and skill 100 at 1.4 times (an operator with no stat gets 1). The same stat also drives
the work animation speed in `Task_OperateMachine` with a separate mapping (0.55 to 2, capped at 1.5; **Observed**).

**Crafting** (**Observed**). `CraftingBuilding::vfunc_150` (`FUN_1402b5a40`; the item-completion slot `vfunc_184` is
`FUN_1402beb30`, thunk `FUN_140043923`) advances the current recipe by the same power x health, output-rate and skill
factors, the production option slider, P and dt, divided by the recipe's duration; the duration of an item is its
`craft time hrs` divided by P (`FUN_1402b9120` / `FUN_1402b91d0`). How the hours value relates to the per-tick unit was
not traced (**Unknown**). It is run by `Task_OperateMachine` (thunk `FUN_140020fef`). When a craft starts, a random
roll against `FUN_1402b5900` (operator skill and bench `tech level`; exact curve **Unknown**) decides the quality flag
of the output. A bench with function 10 (CRAFTING) is the one shown as "armour chain", "weapon smith" and so on.

Main functionalities in the base game (**Verified**, probe of the functionality table: `production mult` and `output
per day`; all those listed have `output per day` true except crafting benches, power, battery and the furnace):

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

The refinery recipes are consumes / produces lists of (item, amount, percentage); for example Rum consumes Water 5
and Cactus 5 and produces Cactus Rum 5 (**Verified**, probe). What the percentage and flag of each reference triple
mean in production was not decoded (**Unknown**).

## Power

**Verified (probe)** base-game numbers: Small Generator `power output` 40, Generator II 100, Wind Generator 50,
Small Wind Generator 25, Wind Generator II 100; Battery Bank output 40 and `power capacity` 60. Research benches
consume power (negative output): Bench III -10, IV -15, V -25, VI -30; benches I and II have 0. Consumers elsewhere
use small negative numbers (lights -1 to -4, crafting benches -4 to -10, hybrid stone mines -35 and -40).

**Capacity and output** (**Observed**). A building's power value is its data `power output` (positive for generators,
negative for consumers) **plus** the research "power increase" from the building's upgrade record (additive, default
0), via `FUN_1402976a0`; its capacity (`FUN_140297650`) is the data `power capacity` plus the research "power capacity
increase", times the health fraction. (The earlier text called the research effect a multiplier on the output; the
code adds it.) A wind generator's output depends on the wind fraction `FUN_1404cbd90` (the weather system's wind is described in [../formats/weather.md](../formats/weather.md)); how the wind field is derived
is **Unknown**. Fuel generators burn fuel at the functionality `production mult` (0.00125) at full output (**Observed**
reading; the unit is **Unknown**).

**Grid** (**Observed**, `Town::vfunc_87` = `FUN_14092cd60`, thunk `FUN_14002744e`). Each town ([factions-squads-towns.md](factions-squads-towns.md#7-towns)) sums its generators' output
into a supply and its battery outputs into a reserve. Consumers are sorted (the order is the ascending order of
their demand, presumably) and served in turn: each takes the smaller of its demand and an even share of what remains
among those not yet served, which is max-min fair sharing. If generator supply is short and the town's battery flag
is on, the batteries cover the shortfall in the same even-share way up to each battery's own output
(`FUN_1402978d0`, `FUN_1402979d0`); surplus supply charges batteries up to `power capacity`. A building's powerSat is
its granted share of its own demand; `FUN_140299370` is the has-power check (it also returns true when the building
needs no power; the minimum share that counts as powered is **Unknown**). The generator tooltip is `FUN_140300b40`.

## Storage

(**Verified**, probe of 603 buildings.) 87 buildings have an inventory (`has inventory` true). The inventory size
fields default to 18 by 18 cells and `itemtype limit` to 4 (every record carries them; of the 87 with an inventory, 31
are 18 by 18 and 594 of all 603 have limit 4). `stackable bonus mult` ranges from 1 to 100000 over all records (536
have 1; a resource store holds many times the normal stack). The `limit inventory` list restricts what a storage
building accepts (for example "Storage: Armour Plate", 4 by 4, bonus 25, accepts only Armour Plating). The
constructors are `FUN_1402a30d0` (`StorageBuilding`) and `FUN_1402a3750` (`ProductionBuilding`); the functionality
loaders are `FUN_14029e7b0` and `FUN_14029e460` (**Observed**).

## Farming

`FarmBuilding` (ctor `FUN_1400e8420`, update `FUN_1400e5580` = `FarmBuilding::vfunc_28`, `FarmBuilding::vfunc_150`
`FUN_1400e59e0`) is driven by a `FARM_DATA` record (**Verified** field list, probe: `growth time`, `consumption rate`,
`fertility effect`, `minimum fertility`, `output per plant`, `harvest rate`, `clear rate`, `harvest time`, `death
threshold`, `death time`, `drought death time`, `drought multiplier`, `arid`, `green`, `swamp`, `inside`, `amount`,
`plants`). Example (Wheat Farm 25, **Verified** by the same probe): growth time 22, consumption rate 2, fertility effect 0.25, harvest
time 48, death threshold 0.4, death time 8, drought death time 48.

- Rates (**Observed**, ctor `FUN_1400e8420`): `consumption rate`, `harvest rate` and `clear rate` are divided by G, i.e.
  they are per game **day**; the consumption rate is then multiplied by the `farm water usage` of the biome group at
  the farm's position (a `BIOME_GROUP` field, see [../formats/weather.md](../formats/weather.md) for the biome groups; **Verified** in the probe: the Desert group has 8, most groups 1, Floodlands
  0.1, Okran's Valley 1.5). `growth time`, `harvest time`, `death time` and `drought death time` are stored as read
  and used in game hours (the update divides the scaled frame time by G / 24 to get hours). Indoor farms (fertility
  effect 0) are flagged.
- Growth (**Observed**, `FUN_1400e5580`): per tick the growth fraction rises by

  dtHours x fertilityFactor / growthTime  (x drought multiplier when the water inputs failed; x power x health for
  farms that use power)

  with fertilityFactor = 1 - (1 - f) x fertility effect, and 0 when f is at or below the `minimum fertility`
  (`FUN_1400e4320`, **Observed**); f is the local farming fertility (0 to 1, from the biome's
  `ENVIRONMENT_RESOURCES.farming mult`, with `farming min` as a floor and the altitude min / max / fade fields as a
  smooth cut-off; coverage in `FUN_140a12ff0`, inverse lerp `FUN_140027d90`). Growth state 0 to 1 is growing, 1 to 2
  is mature and waiting for harvest (it advances by dtHours / harvest time), and 2 and above is the dead or spoiling
  state (**Observed**, reading of the thresholds only).
- Water (**Observed**, `FUN_1400df4c0` called from the update): the farm's input list (the `consumes` list, water) is
  drawn down at the scaled rate; when a draw fails the plants dry, the drought timer advances by dtHours / `drought
  death time` (0 means never, as for indoor farms) and the crop is lost when that timer reaches 1; the `death threshold` is compared with the same timer to pick the farm status (**Observed**, thresholds only).
- Harvest (**Observed**, `FarmBuilding::vfunc_150` `FUN_1400e59e0`): an operator with skill factor s adds to a harvest
  progress at `harvest rate` x power x health x P x dt x s; each time it reaches 1 the farm rolls, for each of `output
  per plant` units (the fractional part rolled once as a probability), a success with probability equal to the
  efficiency `FUN_1400dfe10` and puts one item per success into the output. The efficiency is the biome match
  (the farm's `arid`, `green` and `swamp` weights multiplied by the local coverage of each, summed; 1 for indoor
  farms) times the stored fertility factor times min(1, 0.5 + 1.5 x p) where p is an argument supplied by the caller
  (not traced). The earlier text said harvest = plants x output per plant x fertility factor; that is replaced by
  this reading, and the `p` argument is **Unknown**. Clearing dead crops uses the `clear rate` in the same way.
- The info panel (`FUN_1400e09b0`) and `FarmBuilding::vfunc_91` (thunk `FUN_140013ade`) show these values.

`ENVIRONMENT_RESOURCES` records (**Verified**, 24 probed) set per-biome multipliers: `iron mult`, `copper mult`,
`carbon mult`, `stone mult` (with `stone noise zoom` / `cutoff`), `water mult`, `water min`, `farming mult`,
`farming min`, `arid`, `green`, `swamp`, and altitude bands for water and farming. Mines and wells read these through
`FUN_14029c670`.

## Research and tech

Research buildings (function 3, class `ResearchBuilding`, `vfunc_150` `FUN_14029b8d0`) work a queue of `RESEARCH`
records (**Verified** field list, probe: `category`, `level`, `time`, `money`, `repeats`, `repeat mult`, `is level
upgrade`, `blueprint only`, `production mult`, `power increase`, `power capacity increase`, `requirements`, `cost`,
`improve buildings`, `blueprint item`).

**Bench contribution** (**Observed**). `ResearchBuilding::vfunc_150` does not advance research directly. Each call (one
per operator per frame, with the operator's skill factor as its argument) pushes a type-8 entry onto the global event
queue at `DAT_1421346d8` whose payload is outputRate x upgradeMult x health x powerSat x skill factor (the same slots
as in production, so a bench's `output rate` is its rate), and the event dispatcher `FUN_1407b65b0` handles type 8 by
calling the research add-progress function `FUN_140836b70` (thunk `FUN_1400194a2`) on the research manager. This
resolves the earlier open question about the consumer of that queue for research; other event types (1 to 16 in the
dispatcher) were not decoded.

**Time** (constants **Verified**: the loader `FUN_14086b2b0` stores 0.3 and 1.3 at `142133f24` and `142133f20`, and
`FUN_14082ed10` reads exactly those slots plus the option `14213358c`; the formula shape is **Observed**, single
decompile). The research hours of an item are

  hours = time x 0.3 x 1.3^(level - 1) / researchSpeedOption

where time is the record's `time`, level its `level`, and 0.3 and 1.3 the CONSTANTS `research rate` and `research level increase rate`.
`FUN_14082f050` converts the hours to game seconds (x G / 24) and `FUN_14082f090` gives the progress fraction.
`FUN_140836b70` adds each contribution times the scaled frame time to the item's accumulated seconds and completes the
item when that passes the total (**Observed**). A call's contribution is first multiplied by 0.6 once for every
earlier call counted since the counter was last reset, so contribution number k (counting from 0) is scaled by 0.6^k
and more benches or operators give diminishing returns (**Observed**). A bench's own rate is `output rate`
(**Verified** data: Small Research Bench 0.85, II 0.9, III 0.95, IV 1.0, V 1.05, VI 1.1).

**Completion** (**Observed**). `FUN_140834550` completes an item and `FUN_140834390` raises the level of a repeatable;
`repeat mult` scales the cost of each repeat (`FUN_1408320a0`); `FUN_140836cb0` resets progress. `FUN_140831a60`
applies `improve buildings` (a research item raises the `production mult`, `power increase` and `power capacity
increase` of the buildings it lists; the per-building record of these three values is looked up by `FUN_14082ec70`,
default 1, 0, 0, which is where upgradeMult and the additive power terms above come from). Blueprint research creates a
blueprint item from a template (`FUN_1408324f0`), priced with the 1.5 blueprint multiplier ([economy.md](economy.md#10-blueprints-research-cost-artifacts)).

**Costs and prerequisites** (**Observed**). Costs are item lists paid from storage (`FUN_1408343c0`, check `FUN_140832e50`,
removal `FUN_140832f20` / `FUN_14082e810` / `FUN_14082e900`); the queue is `FUN_1408348a0`; the available list is
rebuilt in `FUN_1408333b0`. A level needs a bench of at least the level from `FUN_140830a30`; level upgrades use a
tier lookup (`FUN_140830880`). The UI rate readout is `FUN_140494af0`.

**Tech levels** (**Verified**, probe). The `is level upgrade` records: Tech Level 2 time 4 (cost 6 Book, requires a Small
House), Tech Level 3 time 10 (10 Book), Tech Level 4 time 16 (4 Ancient Science Book), Tech Level 5 time 24 (8
Ancient Science Book), Tech Level 6 time 32 (2 AI Core), each requiring the previous.

## Unknowns

- How the data `build materials` and `build speed mult` feed the per-call build rate and target in `Building::vfunc_70`
  (the stored rate at offset `0x18c`, default 0.5, and the target at `0x188`), where its builder counter is reset, and
  what the flag that triples the work is. The UI hours formula is verified; the in-game rate is not.
- When the dismantle refund (`FUN_14029dc10`) runs, and the exact slope comparison and unit of `max slope`.
- The other event types of the queue at `DAT_1421346d8` (only type 8, research progress, was followed); how
  worker-assignment events reach buildings.
- The unit and exact use of generator fuel burn (`production mult` 0.00125) and of the wind fraction.
- The crafting quality curve (`FUN_1402b5900`), the unit conversion between `craft time hrs` and the per-tick
  crafting amount, and the minimum power share for "powered".
- Default values of the three option sliders and whether they multiply or divide in every use.
- Meaning of the percentage and flag of consumes / produces reference triples.
- Whether the 0.6^k research falloff counter is reset per frame (per call is what the code shows).
- The argument `p` of the farm efficiency `FUN_1400dfe10`, and the record field that carries `BuildingClassType`.

## Implementation outline

1. Load `BUILDING`, `BUILDING_FUNCTIONALITY`, `FARM_DATA`, `ENVIRONMENT_RESOURCES`, `BIOME_GROUP` and `RESEARCH`
   through the existing `Meitou.Data` `GameDatabase` / FCS reader (the probe shows the field names to use); keep the
   CONSTANTS values in the same constants object used for characters, plus the day length G from game-loop.md.
2. Port construction first: delivered-materials gate, build-threshold clamp, the UI estimate hours = materials /
   (mult x 6 x opt), builder rates with the 0.75 per-builder falloff; decide the in-game rate once the open question
   above is answered. Dismantle refunds drop 60 to 100 percent of each material.
3. Port production with the per-operator call: amount = health x powerSat x yield x (output rate x production mult
   [/ 2G if per day]) x upgradeMult x skill lerp 0.5 to 1.4 x P x dt, clamp by inputs and output space, accumulate
   into the progress and emit the `produces` list at 1.
4. Add a per-town power grid with max-min fair sharing and batteries (output = data + research increase); wire powerSat
   back into production.
5. Port farms (growth, water, drought, harvest rolls) and research (hours, 0.6^k falloff, `improve buildings`) last,
   since they depend on production and power.
6. Use the building function numbers from [../formats/zones.md](../formats/zones.md) when deciding draw and AI
   behaviour. The link from `docs/README.md` to this page is pending (README is outside this task's edit scope).
