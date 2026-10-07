# Simulation: the game loop and the plan to build it
  Decided in stage 0: a frame may run `6 x speed` ticks (0.2 s of game time per frame at every speed, so 5 fps is the floor at
  which the game still runs at the asked speed); `SimulationClock.AchievedSpeed` is the asked speed times the share of the owed
  ticks that ran, and the title shows it when lower. The budget counts ticks, not milliseconds: a budget in CPU time would make
  the tick count depend on the machine, so it can only be added later as a second limit, not in the determinism tests.
- **Frame order.** `WorldSession.Advance(real dt)` runs all control ticks the frame holds first (input, pause, speed, camera),
  then feeds `real dt` to the simulation clock at the speed they left. A speed change thus applies to the whole frame it is made in
  (at most one frame of error); the simulation never sees the input.

Design and plan for phase 5 of the [roadmap](../ROADMAP.md) (the simulation core) and for the pieces of phases 4, 6 and 7 it needs
first: walkability, characters on screen, saves of the world state. Proposed 2026-10-07; the owner settled the open choices
the same day ([Owner decisions](#owner-decisions)); the work runs in [parallel tracks](#parallel-tracks).

Everything here is an **engine choice**. Facts about the original come from the research in `docs/game/` and are cited, not
restated: [game-loop.md](game/game-loop.md) (frame, time, speeds, the clock, the update order, budgets, zones),
[ai.md](game/ai.md) / [ai-tasks.md](game/ai-tasks.md), [pathfinding.md](game/pathfinding.md),
[character-stats.md](game/character-stats.md), [combat.md](game/combat.md),
[factions-squads-towns.md](game/factions-squads-towns.md), [economy.md](game/economy.md),
[buildings-production.md](game/buildings-production.md), [ui-input.md](game/ui-input.md), [ui-screens.md](game/ui-screens.md),
[formats/save.md](formats/save.md). Each of those ends with an implementation outline; this doc orders them into one plan and
decides what they leave open (scheduling, threads, data layout).

## Where we are

- `meitou` boots into the world: `WorldSession` (`src/Meitou.Engine`) runs a real-time control tick (input actions, camera) and a
  game-time simulation tick that advances the `GameClock` and a `World`, and the renderer draws the camera interpolated between
  control ticks ([engine.md](engine.md#the-game-loop-meitou-meitouenginetime)). The renderer is complete enough for play (terrain,
  buildings, foliage, water, sky, shadows, impostors, upscalers).
- The data layer reads everything a world needs (FCS with the game's merge rules, load order, zone and town placements), and
  `CharacterGenerator` rolls an NPC's appearance and loadout. Typed views exist over CONSTANTS, FACTION (with the initial relations),
  SQUAD_TEMPLATE and TOWN; AI_PACKAGE, AI_TASK and races are still read by field name.
- Stages 0 to 3 and 6 are done on branch `sim-core`: [time facts](#time-model) (the game's clock, pause, 1/2/5), [Skeleton as built](#skeleton-as-built-stage-1) [Populate and move as built](#populate-and-move-as-built-stages-2-and-3) [Player as built](#player-as-built-stage-6) and [Animation, formation and roaming as built](#animation-formation-and-roaming-as-built-after-stage-6).
- Missing for a living world: navmesh walkability and collision with buildings, the AI proper, the UI screens, bodies and combat, saves.

## Principles

1. **Headless and deterministic.** The simulation is a library with no GPU or window. Given the same data, seed, commands and
   tick count it reaches the same state, with any number of worker threads. It never reads a wall clock and never sees the
   frame rate. A state hash makes this testable.
2. **Exact data, good-enough behaviour** (the owner's direction of 2026-10-06). File formats, merge rules and save keys are
   exact, because drop-in compatibility depends on them. Formulas from the research are used where they cost nothing extra.
   Scheduling details of the original (per-frame budgets, its AI thread, the races between them) are not copied.
3. **Parallel by construction.** Per-character work reads the previous tick's state and writes only its own; effects on other
   entities (a hit, an item handed over, a relation change, a spawn) go into ordered queues committed serially at the end of
   the tick. That is what lets the work spread over cores and still be deterministic.
4. **No stutter.** Loading zones, spawning squads, path queries and navmesh building run off the simulation thread; their
   results are taken in at tick boundaries.

## Time model

The original ([game-loop.md](game/game-loop.md#measuring-the-frame-time)) runs everything once per rendered frame with a
variable `dt` (capped at 0.05 s, smoothed over 20 frames), multiplies it by the speed, updates near characters every frame
and far ones in per-frame round robins (6 and 8 per frame, one squad per faction per frame). Its results therefore depend on
the frame rate, and at 5x every step is five times longer.

Proposal (owner decision 1):

- **Two clocks.** A *real-time* control tick (the present `FixedStepClock`, 30 Hz of real time) runs the camera, the input
  actions and the UI, so the camera moves while the game is paused. A *game-time* simulation tick runs the world.
- **Fixed game-time step.** One simulation tick is 1/30 s of game time at every speed. The accumulator is fed
  `real dt × speed`, so speeds 1, 2 and 5 run 30, 60 and 150 ticks per real second and pause runs none. Behaviour does not change
  with the speed or the frame rate; the 5x speed costs five times the CPU per real second (the original keeps the cost and
  coarsens the step instead).
- **Falling behind slows the game.** When a frame owes more ticks than a budget allows (scaled with the speed), the rest are
  dropped and the game runs slower than asked, as the original does below 20 fps through its 0.05 s cap. The HUD can show the
  speed actually achieved.
- **Cadences in game time.** The original's per-frame budgets become rates in game time: near characters move every tick, AI
  thinking runs at its own staggered rate (a few times per game second), far characters and off-screen squads on longer
  cadences with the elapsed time handed over (the original's accumulators, [game-loop.md](game/game-loop.md#the-character-update-budget)).
- **Clock.** `GameClock` advances with simulation ticks: 1200/11 game seconds per game hour, 24 hours a day, `days per year` from
  CONSTANTS; it keeps the day, the time of day and the total game hours (double), the daylight factor with its 2-hour ramps, the
  day/night flag and the `HH:MM` / `Day n` texts ([game-loop.md](game/game-loop.md#the-clock)). Saves store day, hour and minute.
- **Speed and pause** (done in stage 0). Speeds {0, 1, 2, 5}; pause remembers the last non-zero speed; a separate paused flag lets menus stop the
  world without touching the speed; the bindings are the game's (`pause` Space, `speed_1..3` F2 to F4, [ui-input.md](game/ui-input.md)).
- **Real-time things stay real-time**, as in the original: the autosave timer and zone unload timers run on the host's real
  clock. Physics is the exception: the original steps it in real time at every speed; here it would step with the simulation.
  That only shows in ragdolls at 2x and 5x and is decided when physics exists.
- **Drawing.** The renderer reads a snapshot of the last two simulation ticks and interpolates, as it does for the camera now.
- **Coordinates.** Positions are world units in float, no floating origin: at the map edge (147456 units) a float step is
  0.016 units, 1.6 mm. The original rebases its origin every 10000 units for Havok and Ogre; the renderer here already draws
  camera-relative.

## Threading

To start, the simulation ticks on the main thread before the frame is drawn (as the camera does now), with its parallel phases
on a worker pool; the tick is written so that moving it to its own thread, running ahead and publishing snapshots, is a host
change only. It moves when the tick shows up in frame times.

One tick, in order:

1. **Inputs.** Player commands (stamped with the tick they apply at), finished path queries, loaded zones and spawned squads.
2. **Schedule.** Which characters get the full update this tick (relevance: `npc range` 3250 from the camera focus and the view
   test, [game-loop.md](game/game-loop.md#the-character-update-budget)), whose AI thinks, which far characters and squads are due.
3. **Think** (parallel). AI for the characters whose think is due, reading the previous tick's world; output is intents (a task,
   a target, a path request).
4. **Move** (parallel). Path following, steering, separation (from the previous tick's positions), integration on the ground.
5. **Act** (parallel). Combat timers and blows, the medical and needs updates, production; effects on others go to the queues.
6. **Commit** (serial, ordered by entity id then sequence). Damage, item transfers, relation and bounty changes, spawns and
   removals.
7. **Slow world.** Factions, squads, towns, area sectors (every 2 s of game time, [factions-squads-towns.md](game/factions-squads-towns.md#62-area-sectors-homeless--roaming-squads-and-the-biome-population)),
   zone activation, off-screen squads; each on its own cadence.
8. **Publish** the snapshot for drawing.

- **Asynchronous services** (paths, navmesh building, zone loading) run on their own threads with request and result queues. In
  play a result is taken in at the first tick after it is ready, so timing can differ between runs. In the determinism tests they
  run synchronously, so the test covers the simulation's own logic.
- **Randomness.** Seeded streams per (world seed, entity, purpose), so the order threads finish in never changes a roll. The
  original's own random sequence is not reproduced (it is open for the character generator too, [characters.md](characters.md)).

## World model

- **Project.** A new library `src/Meitou.Simulation` (namespace `Meitou.Simulation`), depending on `Meitou.Data` only. The engine's
  `WorldSession` owns a `World` and feeds it commands; rendering reads its snapshot. Dependencies keep pointing one way:
  Core ← Data ← Simulation ← Engine ← Game, Rendering beside it.
- **Entities.** `Faction`, `Platoon` (a squad, with the original's active and unloaded halves,
  [game-loop.md](game/game-loop.md#factions-and-squads)), `Character`, `Town`, building instances, item instances. Their ids
  map onto the save's handles (`{Type, C, CS, I, S}`, [save.md](formats/save.md)), so a save is a direct mapping, not a translation.
- **Layout.** Characters live in a table: dense arrays of structs for the state every tick touches (position, velocity, heading,
  movement mode, path cursor, animation state, flags), an object per character for the rest (stats, body parts, inventory, AI
  blackboard). Slots are reused with a generation number in the id. Squads, factions and towns are plain objects; there are
  few of them.
- **Static data** (stage 1, `src/Meitou.Data/Gameplay/`). Typed views built once at load over `GameRecord`: `GameConstants` (the fields of
  [game-loop.md](game/game-loop.md#settings-and-constants) and [character-stats.md](game/character-stats.md#constants-used-by-this-subsystem)
  with the loader's rescalings applied, so a property is the stored value; a missing field takes the editor's default where the doc lists
  one), `FactionData` + `FactionRelations` (the initial table of [section 3.2](game/factions-squads-towns.md#32-initial-values-verified):
  explicit entry, else the smaller default, self 100; hostile at -30 or below, ally at 50 or above), `SquadTemplate` (section 4) and
  `TownData` (section 7, with the setup rules for `town radius mult` and the NEST_MARKER foliage range). Still to come: races, AI
  packages and tasks. The probe tables are tests (`[Slow]`, skipped without the game): the CONSTANTS values, 103 factions with 10,302
  NPC pairs of which 3,356 hostile and 62 allied and 221 explicit entries, 959 squad templates, 346 towns by type.

## Skeleton as built (stage 1)

Decisions the plan left open, made while building `Meitou.Simulation`:

- **World and systems.** `World` owns the `CharacterTable`, the `CommandQueue`, a `WorkerPool` and an ordered list of `ITickSystem`s
  (movement, AI, combat... plug in here). `RunTick` follows the phases above: `Inputs`, `Schedule` (after the neighbour grid is built),
  `Think`, `Move`, `Act` (each over `Partition`s of slot ranges, a barrier after each system's phase), the effects commit, `SlowWorld`,
  then the swap and the snapshot. Systems run in the order they were added, in every phase.
- **Double-buffered hot state.** `CharacterHot` structs live in two arrays: `Previous` (the last tick's result, read by every phase,
  other characters included) and `Next` (copied from `Previous` at the start of the tick; a phase writes only the slots of its own
  partition). The arrays swap at the end of the tick. Spawns and removals happen only in the serial phases (a removal only in the
  commit or the slow world, never in `Inputs`), so a slot freed this tick is reused no earlier than the commit.
- **Slots.** Reused most-recently-freed first with `CharacterId.Generation` raised; the free list is part of the state hash.
- **Randomness is stateless.** `Rng.Hash(world seed, entity key, purpose, counter)` (SplitMix64 mixing; the entity key is slot and
  generation, the counter is usually the tick number): nothing to store, hash or race on, and the same roll whichever thread asks.
  `RandomStream` is the stateful convenience over the same hash.
- **Cross-character effects.** The Act phase puts effects into a per-partition `EffectBuffer`; the commit concatenates them and sorts by
  (target slot, source slot, system, sequence), none of which depends on the partitioning, then calls `Apply` of the emitting system.
  A source emits all its effects together. Sums over neighbours (separation) use `SpatialGrid`, built each tick from `Previous`,
  which visits cells in a fixed order: float addition is not associative, so the order must be a function of the state alone.
- **Partitioning.** `Threads x 2` partitions (at least 16 slots each) over `0..HighWater`; which worker runs which partition is not fixed.
  One thread means no threads: the caller runs everything. The determinism test runs 1, 4 and 16 threads, so 2, 8 and 32 partitions.
- **State hash.** `World.StateHash()`: a mixing hash written here (no packages) over the seed, the tick, every slot in order (alive,
  generation, and for live ones position, velocity, yaw, mode, flags, goal, path cursor, animation, health, faction, spawn tick) and the
  free list. Floats go in by their bits (-0 as 0). It is a test and debugging tool, not run in play.
- **Snapshots.** Each tick publishes a `WorldSnapshot` of the characters that have an appearance (a character without one is simulated
  but not drawn) and keeps the one before; `WorldSession` exposes `CurrentSnapshot`, `PreviousSnapshot` and `SimulationAlpha` (the
  simulation clock's, not the camera's). The list is rebuilt every tick: cheap at hundreds of characters; at thousands it becomes a
  double-buffered array, a stage 3 measurement.
- **WorldSession.** Owns a `World` over `OpenGroundWalkability(terrain height)`; every simulation tick runs `World.RunTick()` then
  advances the `GameClock`. The host chooses the thread count (`--sim-threads`, `simThreads` in the user config; default half the
  cores, 1 to 8) and the seed (`--seed`). Nothing is drawn from the snapshots yet.
- **Determinism tests** (`tests/Meitou.Tests/Simulation`): a dummy workload (`WanderSystem`: seeded wandering, separation steering over
  the grid, walkability against a synthetic ground that dips under the water, blows on the nearest neighbour, births and deaths that
  reuse slots) runs 300 ticks with 1, 4 and 16 threads; the hashes at six checkpoints are identical, two runs agree, another seed
  differs.


## Populate and move as built (stages 2 and 3)

**Ground.** The simulation samples `Meitou.Data.World.GroundHeights` (the CPU heightmap window of the loaded region plus the coarse
whole-world grid beyond it, immutable, any thread) through `OpenGroundWalkability`; it never calls the renderer. `CharacterTable.Spawn`
and `Remove` check where in the tick they are (`TablePhase`): no spawns in the parallel phases, no removals before the commit
(a phase reads the character as alive), and throw otherwise.

**Squad factory** (`SquadFactory.Plan`, pure given template, multiplier, seed and key; docs/game/factions-squads-towns.md 5.1): counts
(v0 when v1 is 0 or the sentinel 100, else a uniform integer in [v0, v1], v1 below v0 gives v0; trunc(n x M) and at least 1 unless
`dont multiply`), the creation order and roles (leader 2, `choosefrom` picks weighted with 0 as 100 and gated by world states, `squad`
role 0, `squad2` role 1, `animals`, `animals2`, `slaves` as squads of their own with role 4), the layout offsets (rows of 8, 3 apart,
odd ones 1 back; animals2 5 apart) and the animal age. Missing references become messages in `SquadPlan.Problems`. Not made: `prisoners`
(they need cages), `housemates`, the unique-leader replacement, AI packages. The world states start all false (**Unknown** in the original,
3.5), so squads and `choosefrom` entries gated by one do not appear.

**Population** (`PopulationSystem`). `FocusCommand` (the camera's target; the host sends it when the camera has moved 50 units) activates
the zones overlapping the box of +-340 units, plus a ring of one when `Fast zone hopping` is on (`ZoneActivation`, Verified for the ring,
Observed for the box); the zones are looked at every 15 ticks. A placed town in an active zone gets its residents: the town's `residents`
plus the faction's, unless the town overrides with a list of its own; an entry is v0 squads, v0 = 0 dropped (6.4). Squads stand at
seeded spots within 0.6 of the town radius (`size radius` x `town radius mult`) of the placed position; **how the original picks a home
building is Unknown**, so this is an engine choice, and so is the unload grace of 60 game seconds (the original has three real-time timers
of Unknown length): a town whose zone is no longer active for that long loses its characters, and comes back (new generations) when the
zone does. Characters are made by the squad plan, rolled by `CharacterGenerator` through `CharacterAppearance.Build` (seeded from the
world seed, the town, the squad and the member), on one background thread in the game (`Background`) and inside the tick in tests, and are
taken in at the first tick after they are ready; animals have no appearance and are shown as markers. `MemberPlan`/speed: S = lerp(race
`speed min skill`, `speed max skill`, athletics / 100) with a **stand-in athletics of 20** until stats exist, walk speed from the race.
Resident squads are `Squad` objects in `World.Squads` (leader, members, home); the registry is part of the state hash.

**Movement** (`MovementSystem`, docs/game/pathfinding.md "Movement"). Speed S in units (decimetres) per second capped by the speed mode (0 the
race walk speed, 1 at 55, 2 none; wanderers walk, followers copy their leader's mode and run to catch up, orders run), accelerating by 15 per
second and stopping on arrival; separation (repulsion proportional to 100 x (1 - d/R), R = 8 = twice the footprint radius 4, at most 26
neighbours; the scale to a speed, 0.15, is an engine choice); movement stays on ground above the water and a blocked character drops its
path. Paths: Think flags a character that wants one, the next Schedule step asks the `PathService` (a thread of its own in the game, the
caller in tests), answers are applied in a serial step in request order and a late answer to an older request is ignored. Tasks: `Wander`
(a random point within the squad's home radius, then a wait of 3 to 10 s), `GoTo` (`MoveOrder`; a group goes to the slots of a block round the click, `Formation.Place`;
queued orders append), `Follow` (the formation slot beside the leader, repathing every 0.5 s when more than 14 units away).
Not modelled: water states and swimming, roads for long trips, the formation slot rules (**Unknown**), turn rate, the combat speed
multiplier, collision with buildings (the navmesh stage).

of the character renderer ([character-renderer.md](character-renderer.md)); the animation layers come from `AnimationSystem` (see "Animation, formation and
roaming as built"; without that system the older movement-state layers `idle_stand_relax` / `walk lower` + `walk upper` are published). Characters without an appearance get a debug square coloured
(`AnimationLayers`: `idle_stand_relax`, or `walk lower` + `walk upper`). Characters without an appearance get a debug square coloured
by faction through the existing `DebugOverlay`. `--no-population` leaves the world empty.

**Cost** (`HubTests.Tick_cost_of_the_hub_is_recorded`, base-game load order, flat ground, release build, this PC, 600 ticks after the
town loaded; a tick is the same cost at every speed, so a real second costs 30 ticks at 1x and 150 at 5x):

| Characters (The Hub) | Threads | ms per tick | ms per real second at 1x | at 5x |
|---|---|---|---|---|
| 32 (9 squads) | 1 | 0.07 | 2.0 | 10 |
| 32 | 4 | 0.08 | 2.5 | 12 |
| 122 (Squad size multiplier 10) | 1 | 0.15 | 4.4 | 22 |
| 122 | 4 | 0.18 | 5.5 | 27 |
| 122 | 8 | 0.15 | 4.5 | 23 |

At these counts the barrier cost of the worker pool outweighs the parallel gain; threads pay off from some hundreds of characters on
(stage 9 measures again with the AI). The determinism tests run the real workload (population, paths, wander, follow, an order) at 1, 4
and 16 threads, on a synthetic town and on The Hub of the install: identical hashes.


## Animation, formation and roaming as built (after stage 6)

**Animation** (`AnimationSystem`, `AnimationLibrary`, `CharacterAnimation`). Every character carries up to 8 weighted clips; each tick
(the Act phase, after movement) the system picks what it should play, fades the weights, advances the times, and the snapshot carries
the layers (`AnimationLayer(record name, clip seconds, weight)`; the renderer finds the track masks by the record name).

- *What the data says* (**Verified**, fcs.def and the 124 usable ANIMATION records of the install): `play speed` of a movement clip "is multiplied by
  movement speed, so should be small like 0.02, tune until feet match ground speed"; `move speed` is "the ideal speed it travels at";
  `min speed` / `max speed` are "the ideal speed of the next anim below / above"; `synchs` clips of the lower and upper body share a phase;
  `has weapon L/R`, `is combat mode`, `stealth mode` are the Either enum (0 NO, 1 YES, 2 EITHER); `idle` marks standing clips with an `idle chance`
  and `idle time min/max`. **Observed** in the data: a leg range of 1000..1000 means "not used" (the limp clips constrain one leg only);
  the base chain per body layer is walk 14, jog 45, run 90 (`walk lower`, `jog lower`, `run lower` and the `upper` twins);
  `stand 1` / `stand 1 sword` are the gameplay idles, the six `idle_stand_*` poses come from `chareditor.mod`; `squat` is `is action`.
  Cross-check of the rule `clip seconds per second = speed x play speed` (Observed, the stride is not in the data): a walk cycle of the 1.4 s clip takes 1.67 s at
  speed 14 (0.84 clip seconds per second) and covers 2.3 m, a run cycle of the 0.567 s clip takes 0.31 s at speed 90 (1.8) and covers 2.8 m (1 unit = 1 dm): about
  one stride of a person each, which the other reading (cycles per second = speed x play speed) does not give (1.7 m and 5 m).
- *Choice* (**Observed / engine choice**, the original's selection code was not traced): a clip is *valid* when its weapon-in-hand flags, combat and
  stealth modes, crouch / prone, carrying flags and leg ranges fit the character's stance (`AnimationStance`; the weapon kind in the hand
  is checked against the record's kind flags katanas, sabre, blunt, heavy weapons, hackers, polearm, unarmed). The valid movement clips of a
  body layer sorted by `move speed` form a chain: at a speed between two of them the two are blended linearly, below the first and above the last
  one clip alone; clips of one speed (variants) share by `chance`. A character moves from 1 unit per second, else it stands: one of the valid idle clips
  of the whole body by `idle chance`, kept for a time between `idle time min` and `max` (10 to 40 s) and then drawn again. Not made: turning (the
  records have none for humans), strafing, overlays (carrying), `is action` clips, injury and weather variants beyond the leg ranges.
- *Time*: synched movement clips share one phase per character, advanced by `speed x play speed / clip length` cycles per second of the clip that
  weighs most, and a clip's time is `frac(phase + synch offset) x its length` (lengths from the `*_skeleton.skeleton` files, `AnimationLengths`);
  other clips advance by their `play speed` (1 when it is 0). Weights move linearly to their targets at the CONSTANTS `animation blend rate`
  (4 per second: a full cross-fade in a quarter second), the same for every clip (the per-clip speed factor of docs/animation.md was not applied).
- *Weapons*: the stance holds what is drawn (`CharacterCold.DrawnWeapon`, nothing yet: there is no combat or draw order, so everybody walks with the
  sheathed variants). `HandHold.OfCategory` maps a WEAPON's `skill category` to the kinds. The sword variants are tested against the synthetic library
  and the real records.
- *Host*: the clip time and weight are interpolated between the last two snapshots (a wrapped clip time just shows the new value). The HUD lists the first
  selected character's layers.

**Formation** (`Formation.Place`). A move order for several characters sends each to a slot of a block centred on the clicked point: a grid
about as wide as deep (`ceil(sqrt n)` columns), 9 units apart (twice the footprint radius 4 plus a margin), rows across the line from the group's centre to
the click; the characters nearest the click take the front row, and within a row the left-to-right order they already have is kept so paths do not cross. The
original's slot rules are **Unknown** (pathfinding.md "Local avoidance and formation"; FACTION `squad formation` RANDOM / CARAVAN / MILITARY is not read).
Queued orders get their own block. Followers of a squad leader keep the factory's offsets (x 2.5).

**Bar squads** (6.4). The town's `bar squads` plus, for towns of type Town, the faction's: each entry is `v0` squads, each present with a chance of `v1` per cent
(0 is read as 100), made when the town loads, placed and walking like residents; the bar building is not modelled, so they do not sit.

**Roaming squads and unloaded squads** (`PopulationRoaming.cs`, `Platoon`, `PlatoonRegistry`; 6.4, game-loop.md "Factions and squads", ai.md "Unloaded squads").
- A loaded town with `roaming squads` draws one squad per look (every 2 game seconds) while its pool is below the cap: 0.7 of the faction's `roaming population`
  (50 by default: 35 characters; the 0.7 is the nests' rule, for towns **Unknown**) times the population multiplier. The candidate weight is `max(0, v0 - squads of that
  template the town has)` (6.2's town branch), a squad that would pass the cap is skipped, a spawn within 36 units of a player character is refused. In the base data
  50 towns have roaming squads; 109 have bar squads.
- A roaming squad is a `Platoon` (state in `World`, hashed): the town it counts against, the template and key that reproduce its members, a position, a target. It walks
  out to one of the nearest four settlements (outposts, towns, villages, military) that are not hostile to its faction, waits 30 to 120 s there, goes home, waits, picks again.
- *Active and unloaded halves*: while a zone near its position is active it has characters (the leader runs to the target and the others follow it; at a town they mill about
  in it); when none is for 10 s its characters are removed and **a stand-in goes on at 25 units per second (90 beyond 25000), no physics, no paths**, the numbers of the
  original's `UnloadedPlatoon` (units not verified). When a zone near it becomes active the characters are made again from the key (the same members) at the stand-in's place.
  A squad that loses all its members leaves the pool (a new one is drawn).
- Residents are not platoons: a town's residents and bar squads are made when its zone loads and dropped when it has been inactive for 60 s, which makes the town itself the stand-in.
- Not made: roads for long trips (`road preference`), the AI package jobs (`GoOutOnPatrol` and others), faction campaigns, travel through a danger the squad should flee,
  nests and the homeless spawns of the area sectors (6.2, 6.3), unique squads.

## Frame rate and the navmesh in the game (after the animation round)

**Where the 186 to 135 fps went** (`--new-game --quit-after 10`; the `profile` line the host prints at the end of a smoke run, and the renderer's own
`characters` line). Per frame, release build, this PC (timings on a busy machine move by tens of per cent; the figures below are from quiet runs):

| | no population | `--new-game`, 8 sim threads | `--new-game`, 1 sim thread | `--new-game`, `MinPartitionSize` 128 (now) |
|---|---|---|---|---|
| fps | 192 | 135 | 162 | 165 to 183 |
| simulation advance, ms per frame (per tick) | 0.02 (0.08) | 0.41 (2.17) | 0.07 (0.41) | 0.06 to 0.08 (0.40 to 0.48) |
| host draw-list fill | - | 0.025 | 0.022 | 0.015 |
| world draw (render thread CPU) | 2.5 | 4.95 | 3.4 | 2.6 to 3.1 |

- **The cost on our side was the worker pool.** 34 characters were cut into 2 partitions (`MinPartitionSize` 16), so every parallel phase of every system
  (5 systems, 3 phases) woke worker threads and waited at a barrier: 2.2 ms per tick against 0.4 ms for the same tick on one thread, and the stall also showed in
  the draw time (the render thread competes for cores). The game now passes `MinPartitionSize = 128` (a world under 256 slots runs on the calling thread); the default of
  16 stays in `WorldSettings` so the determinism tests still cut small worlds into several partitions. A tick of the Hub is 0.4 to 0.5 ms (it was 0.15 before the
  animation, bar squads, snapshot paths and the 3 layers' publish: about 0.15 ms of that is `Publish` building the snapshot lists, left as is: 1.5 % of a frame at 30 ticks per second).
- **Host fill / interpolation**: 0.015 to 0.025 ms per frame for about 30 characters; one `CharacterPose[]` and a few small loops per drawn character. Not worth more.
- **Renderer side** (track B, `src/Meitou.Rendering/Characters/`): with the world loaded, the characters add about 0.1 to 0.2 ms of CPU update (gather 0.04, pose 0.1) and 0.05 to 0.08 ms of
  draw recording, but the render thread's whole `DrawWorld` is 0.5 to 1 ms above the empty world (2.6 to 3.1 ms against 2.5): mainly the character shadow cascades
  (about 2700 shadow draw calls per 10 s smoke, 1350 motion-vector passes) and the 70 to 90 draw calls of the 21 to 34 drawn characters. GPU: the colour pass of the characters costs
  0.04 ms; nothing there is the limit. That residual is the renderer's to look at (instancing the shadow pass per LOD level); nothing local was changed.

**The navmesh in the game** (`--no-navmesh` turns it off). `Meitou.Game/NavAdapter.cs` is the only place that knows `Meitou.Navigation`: it implements the simulation's
`IAgentWalkability` over `NavSystem.Walkability`; everything else in the game sees `IWalkability`.
- *Zones*: the host asks for the zones of `PopulationSystem.ActiveZones` each frame (`NavSystem.LoadZone`, idempotent), and drops one that has been inactive for 30 s
  (`UnloadZone`, the cache file stays). A cold zone is about 1.5 to 2 s on the nav builder thread, a cached one milliseconds, never on the simulation thread.
- *Stand-in or hold*: where a zone is not loaded, `NavmeshWalkability` answers with the open-ground stand-in, so a query never waits. Two holds keep that from being seen:
  the population does not load a town until its zone's mesh is ready (`PopulationSystem.ZoneGate`), so residents are placed on the mesh; and a new game (and a screenshot)
  waits for the zones round the start (2 zones, 3.2 s cold, 75 ms cached) before the squad is placed. A path asked for across a zone not ready yet is straight;
  when a mesh arrives the host sends a `RepathCommand` (a world command, at the world's tick) and every character with a go-to in hand asks again.
- *Footprint*: `CharacterCold.FootprintRadius` from the race's `pathfind footprint radius` (0 when unknown: the default human, 4); `PathService.Submit` carries it and the adapter
  hands it to `NavAgent.Radius`. The water factor (`water avoidance`) and the faction halving are not passed yet.
- *Tests*: the determinism and the other simulation tests stay on `OpenGroundWalkability` (synchronous). `HubNavigationTests` (Slow): the Wanderer's order across The Hub reaches the goal
  without leaving the mesh and with a detour of at least 10 % over the straight line (the wall's gate). Doors: nothing opens or closes them yet (no building interaction in the game).
- *Look*: the selected character's remaining path is drawn as a yellow line (`CharacterSnapshot.Path`).
## Player as built (stage 6)

**New game.** `meitou --new-game [start]` (default `Wanderer`; `--list-starts` prints the 13 base-game starts). `NewGameStart`
(`Meitou.Data.Gameplay`) reads the NEW_GAME_STARTOFF record: money, start position and its `force pos` flag, squad, towns,
faction relations, research, `force race` (**Verified** against the base records, `New_game_starts_match_the_documented_records`).
`PopulationSystem.StartPlayer` builds the player's squad:

- *Location* (decision, the plan left it open): the first listed town that is placed in the world; its centre, else the record's
  `StartPosition` when `force pos` is set (Rock Bottom). A seeded walkable spot within a third of the town radius. The host loads
  the world around that point (`--at` is overridden) and puts the camera on the squad leader.
- *Members*: every squad link of the start in one squad: SQUAD_TEMPLATE links go through `SquadFactory`, CHARACTER links are single
  members; the first member leads when no template names one. Appearances come from the generator as for residents. The Wanderer
  start is one character. The faction is `Nameless` (the player's), money from the record.
- *Not applied yet* (**Unknown** how the original applies them at start): the faction relation overrides, research, `force race`,
  the characters' inventories (inventory stage).

**Player state is world state.** `PlayerState` (faction, money, squad, **selection**) is hashed and lives in `World`, so a recorded
command stream replays exactly. Characters of the player are flagged `IsPlayer` (hashed). `SelectCommand` (additive or replacing;
non-player characters are dropped) and `StopCommand` are processed by `PlayerSystem` in the commands phase; dead or removed
characters leave the selection after the commit. `MoveOrder` with an empty character list orders the current selection (what the
UI sends, so it never needs the ids at click time). Zone activation follows the player's characters when the player exists; the
camera focus only drives it without a player.

**Orders.** A plain move order replaces the character's current order and queue; `Queued` (shift + right click) appends to the
character's `OrderQueue` (a go-to chain); on arrival the next target is popped. A blocked or failed path drops the queue.
`Stop` ends the go-to and clears the queue. Queued orders and stop are hashed.

**Interface** (`Meitou.Game/PlayerInterface.cs`, host side only; it reads the published snapshot and enqueues commands at the
world's tick):

| Input | Effect |
|---|---|
| left click | select the character under the cursor (ray against a capsule, 2.2 units tall, radius 1 or 1.2 % of the distance); click on nothing deselects |
| left drag | box select: player characters whose chest projects inside the rectangle and lie within 7500 of the camera |
| shift | adds to the selection (click, box, digit, grave) |
| right click | `MoveOrder` to the ground under the cursor (ray marched against the CPU heightmap, then bisected); shift queues |
| `1`..`9` / `` ` `` | select the n-th player character / the whole squad |
| `R` | stop (not in the free camera, where `R` is up) |

Right mouse is the command button, so **Orbit is the middle button only** now; saved configs that bound `Mouse:Right` to orbit lose
that entry on load. The HUD (`DebugOverlay` text and quads, top-left): the game clock and day, money, clickable speed buttons
(pause, 1x, 2x, 5x; the active one lit), the selected characters' names. Selected characters have a green ring on the ground; a
click on the HUD never selects. Characters without a drawing keep their debug squares. The screenshot options `--select-player`
and `--move-to <x> <z>` select the squad / order it to walk, for pictures; a `pickcheck` line in the log checks that the leader's
own pixel picks it and a ray 60 px lower lands near it.

Tests (`PlayerTests`): the new-game squad, selection rules, an order with no ids plus queued orders, plain order and stop, zones
following the player, and a scripted select / move / stop game that hashes the same at 1, 4 and 16 threads. Not covered by tests
(needs a window): the mouse picking, checked by the `pickcheck` line and by hand.

Open: orders are straight go-tos on the stub walkability (through walls) until the navmesh (stage 5) lands; characters have no walk
animation yet (renderer track); no formation for several selected characters (all walk to the same point and separate).
## Bodies as built (stage 7)

Track E, branch `sim-body`: stats, XP, medical state, encumbrance and speed as pure code in `src/Meitou.Simulation/Bodies/`, typed views (`RaceData`,
`BodyPartTemplate`, `LimbReplacement`, `StatsEnumerated`, `StatsData`) in `src/Meitou.Data/Gameplay/Bodies/`. Formulas, units and the engine choices are in
[character-stats.md](game/character-stats.md#as-built-stage-7-meitousimulationbodies-and-meitoudatagameplaybodies). Nothing is wired into `World` yet. What the
core calls:

| When | Call |
|---|---|
| Static data, once per race | `RaceData.From(record, db)` (stat map, anatomy, blood, hunger, speed, footprint radius `PathfindFootprintRadius`, water avoidance); `LimbReplacement.From`, `BodyPartTemplate.From` |
| Spawn | `CharacterStats.Create(StatsData.Read(statsRecord) or CharacterStats.FromGroups(...), race, stats randomise, world seed, Rng.Key(id))`, `MedicalState.Create(race, stats.Strength)` |
| Act phase, per character | build a `MedicalContext(constants, options, race) { Toughness, Strength, Resting, EncumbranceFactor, Seed, CharacterKey ... }`, then `medical.Tick(dtHours, ctx, events)`; `dtHours` = ticks x 11/36000 (a far character passes its elapsed time); `events` is a per-partition list (KO, woke, part down, severed, died) that the commit phase acts on |
| Commit, on a hit | `medical.ChoosePart(ctx)` then `ApplyHit(part, new HitDamage(cut, blunt, pierce), ctx, events)`; on `PartSevered` call `XpService.ToughnessFromLimbLoss` and spawn the limb item |
| Any stat read that should feel injuries and hunger | `medical.StatMultiplier(stat, StatUse...)` (the table of `FUN_1406457e0`) |
| Movement | `Speed.Run(race, stats, medical, encumbranceFactor, shallowWater)` replaces the stand-in athletics of 20 at `PopulationSystem` (`Speed.RunUnhurt(race, stats.Athletics)` when no medical state exists); `Encumbrance.Factor(constants, inventoryWeight, carryingPerson, strength, strengthInjuryMultiplier)` |
| XP | one shared `new XpService(constants, options)`: `Combat`, `Continuous`, `StrengthFromCarrying`, `AthleticsFromRunning`, `Lockpicking`, `Medic`... |
| Eating, first aid | `medical.Feed(amount)`; `medical.Treat(dt, skill, kitQuality, robotKit, ctx)` and `MedicalState.KitDrain` |

`CharacterStats` and `MedicalState` belong to one character, hold no references to shared mutable state and draw their random numbers from the seeded hash, so the
Act phase can run characters in parallel; effects on others stay in the commit queues. The new-game options (Hunger time, Chance of death, Global damage multiplier,
Dismemberment) are a `BodyOptions` the world passes in. Save keys: `MedicalState.WriteSave` / `ReadSave` and `CharacterStats.WriteSave` / `ReadSave` use the keys of
[save.md](formats/save.md) (`blood`, `bleeding`, `hung`, `fed`, `KO`, `flesh<k>`..., `strength`, `toughness2`...); limb states and wounds have no key there yet.

## Walkability and movement

Facts ([pathfinding.md](game/pathfinding.md)): the original uses a Havok navmesh per zone; the shipped tiles are a cache that
is regenerated when the zone's building hash differs (mods that add or move buildings) and patched at run time for player
buildings, doors and interiors; path following, steering and avoidance are Kenshi's own code.

Because the original regenerates whenever buildings differ, a drop-in replacement needs a navmesh generator in any case. The
recommendation (owner decision 3) is our own navmesh, built from the terrain heights, building shapes (by BUILDING `path mode`),
foliage flags, doors and the water level with Recast-style generation, cached per zone in our own format and keyed by a hash of
the zone's buildings; the Havok tiles serve only as a reference to compare against. The alternative is a clean-room reader for
the Havok tagfile tiles plus the same generator for changed zones.

Research for that stage (track C, done): building and foliage collision comes from PhysX NxuStream XML files with cooked
meshes ([formats/collision.md](formats/collision.md)); the generator's inputs, groups, materials, seeds and settings, and what
our builder must reproduce, are in [pathfinding.md](game/pathfinding.md#what-our-builder-needs). Three findings shape stage 5:
the builder needs a reader for cooked PhysX 2.8 convex and triangle meshes (layouts documented), and a seed-based region
prune after Recast (Havok keeps only regions near seed points), plus a per-query clearance check in our own A* instead of
Detour's single agent radius. Comparing with the shipped tiles needs a clean-room Havok tagfile reader (test-only).

Until then movement uses a stub: straight lines on the terrain, no obstacles, nobody enters the water. Characters walk through
walls, which is enough to watch squads move. Movement itself follows `CharMovement` (speed from stat S in decimetres per
second, the speed-mode caps, acceleration 15/s, its own separation steering).

## Characters on screen

A native character renderer is rendering work and can run beside the simulation stages: skinned meshes, attachments,
appearance and the animation layers as [character-viewer.md](character-viewer.md), [characters.md](characters.md) and
[animation.md](animation.md) describe, drawing many characters from the snapshot (bone palettes in a buffer, instancing per
mesh, mesh LOD). It exists as `Meitou.Rendering.Characters` (see [character-renderer.md](character-renderer.md)); the simulation fills a `CharacterDrawList` each frame.

## Stages

Each stage ends with tests and something the owner can run. Performance is measured from stage 3 on: the simulation's
milliseconds per tick at 1x and 5x with the character count, in the game's frame profiler.

| Stage | Content | Gate |
|---|---|---|
| **0. Time facts** | `GameClock` at 1200/11 s per hour; speeds {0, 1, 2, 5} with F2 to F4 and Space; the paused flag; the control tick in real time and the simulation tick in game time (pause and speed now change how many simulation ticks run, so the camera moves to the control tick); daylight factor, day/night, clock texts in the title | Tests for the rates, speed and pause transitions; the camera still moves while paused and at every speed; the title shows the game's clock |
| **1. Skeleton** | `Meitou.Simulation`, `World`, the tick phases, worker pool, seeded streams, state hash; typed views over CONSTANTS, FACTION (+ initial relations), SQUAD_TEMPLATE, TOWN | Determinism test: the same N ticks with 1, 4 and 16 threads give the same hash; probe values as tests |
| **2. Populate** | Squad factory ([factions-squads-towns.md](game/factions-squads-towns.md#51-counts-and-layout-observed-two-functions-share-the-count-rules-see-the-sentinel-note)) with `CharacterGenerator`; town residents for the towns around the camera; zone activation and unloading; debug markers | The Hub shows its residents' markers where the layout puts them; counts follow the template rules (tests) |
| **3. Move** | Movement on the stub; first tasks (wander in the town radius, go to, follow the leader) | Markers wander; determinism holds; tick time at 1x and 5x recorded |
| **4. Characters drawn** | Native character renderer (renderer track; may start at any stage) | Residents of The Hub drawn and animated, frame time recorded |
| **5. Navmesh** | Collision reader (NxuStream XML, cooked meshes); our generator per decision 3 with the seed prune; path queries on their own threads; the stub replaced | Paths around buildings and through gates; generation off the frame |
| **6. Player** | New game from NEW_GAME_STARTOFF, the player's squad, selection and move orders ([ui-input.md](game/ui-input.md)), a minimal HUD (clock, speed buttons) | Start a game, select a character, walk it across The Hub |
| **7. Bodies** | Stats and XP, hunger, blood, body parts, KO and death ([character-stats.md](game/character-stats.md)) | Probe tables as tests (done on `sim-body`, [as built](#bodies-as-built-stage-7); not yet wired into `World`) |
| **8. Combat** | Melee per [combat.md](game/combat.md); ranged later | A fight between two squads resolves; formulas tested |
| **9. AI proper** | Packages, blackboard, scoring and planner ([ai.md](game/ai.md)), off-screen squads with the stand-in speeds | Towns run their daily routines; squads travel off screen |
| **10. Saves** | Read and write the world state ([save.md](formats/save.md)) | Round trip of our own saves; reading the original's sample save |
| **11+** | Economy and trade, buildings and production, the MyGUI UI screens, audio | Per their docs |

Saves could move earlier (the clock and squads alone round-trip), if keeping test worlds becomes useful.

## Parallel tracks

Started 2026-10-07 on branch `sim` (worktree `../MeitouClient-sim`), so the graphics work on `master` goes on undisturbed. Each
track runs in its own worktree and branch off `sim` and is merged back into `sim` after the gates (build, quick tests, the
determinism hash once it exists, one game smoke run). Joins written first, so no track waits on another:

| Join | Where | What |
|---|---|---|
| Snapshot | `Meitou.Simulation.WorldSnapshot` | What a tick publishes: per character its id, `CharacterAppearance`, position, yaw, animation layers |
| Draw list | `Meitou.Rendering.CharacterDrawList` | What the character renderer draws each frame; the host fills it from the last two snapshots, interpolated (the renderers know nothing of the simulation) |
| Walkability | `Meitou.Simulation.IWalkability`, `OpenGroundWalkability` | Ground height, where one may stand, path queries; the stand-in: terrain above the water, straight paths |
| Commands | `Meitou.Simulation.CommandQueue`, `SimCommand`, `MoveOrder` | Player orders stamped with the tick they apply at, taken out by tick then queue order |

| Track | Owns | Stages |
|---|---|---|
| **A. Simulation core** | `Meitou.Engine`, `Meitou.Game`, `Meitou.Simulation`, the solution and project files | 0, 1, 2, 3 |
| **B. Character renderer** | new files in `Meitou.Rendering`, a viewer option to place generated characters | 4 |
| **C. Walkability research** | `docs/` only | building collision shapes, the input to stage 5 |

Next, as tracks free up: the remaining typed data views, relations and the squad factory; the formula libraries of
[character-stats.md](game/character-stats.md), [combat.md](game/combat.md) and [economy.md](game/economy.md) as pure functions
with tests; a read-only save reader; the navmesh builder after track C.

## Owner decisions

Answered by the owner on 2026-10-07; each took the recommendation.

1. **Time model: a fixed game-time tick.** Small steps at every speed; behaviour independent of the speed and the frame rate; 5x
   costs 5x the CPU. (The alternative was the original's model: a real-time tick whose step grows with the speed.)
2. **Tick length: 1/30 s of game time**, the present default.
3. **Walkability: our own navmesh builder** (Recast-style, e.g. the DotRecast library, zlib licence) with our own cache; the Havok
   tiles only as a reference. (The alternative was a clean-room Havok tile reader plus our builder for changed zones.)
4. **Order:** simulation stages 0 to 3 with markers first; the character renderer in parallel.
5. **Physics: none for now** (terrain and navmesh only); BepuPhysics2 when ragdolls and thrown bodies are needed.

## Stage 5 status: navmesh (`Meitou.Navigation`, track D)

**Built (Observed, on The Hub zones 20.32 and 21.32, Release, 12 cores).** Collision reader in `Meitou.Data.Physics`
([collision.md](formats/collision.md#reader-meitoudataphysics)); `ZoneGeometryGatherer` (terrain with the Y 100 water clamp, building
and foliage collision with the mask and path-mode rules, 60 degree building and 40 degree terrain/foliage slopes, interior-mask carvers
skipped for `is gateway` buildings, door painters, seeds from `seeds.def`); a tiled DotRecast 2026.3.1 build (cell 2, cell height 1,
tile 48 cells, agent height 18, max climb 5; areas ground, water, door; monotone regions) with our own tile stitching; the seed prune;
A* over polygons with portal clearance (2 x radius), water factor, door rules and a funnel; `NavWorld` with cross-zone links;
`NavmeshWalkability : IWalkability`; `NavMeshService` (worker thread, queue, cache under `%LOCALAPPDATA%\Meitou\navmesh` keyed by building
hash and settings). `Meitou.Simulation` is unchanged.

**Numbers.** Build about 0.25 to 0.3 s per zone (cell 1.5 / tile 64: 0.55 s; tile 128: 1.4 s). Gathering 1.2 to 1.7 s, mostly foliage
placement, so a cold zone is about 1.5 to 2 s on the worker thread; a cached zone loads in milliseconds. A Hub crossing west to east
(about 3500 straight, 4200 walked) takes about 45 ms.

**Tool.** `meitou-tools navmesh --zone x,z | --town name [--geometry] [--obj f] [--png f] [--scale u] [--box x0,z0,x1,z1]
[--path x0,z0,x1,z1] [--cell c] [--tile n] [--repeat n]`. Output images used for review live outside the repo
(`R:\VlcekM\MeitouClient-re\probes\nav\out\`).

**Gaps.** No interiors; linked-wall and neighbour-border seeds and the ray-down seed check are missing; no detail mesh (heights about
0.5 off); doors are an agent flag (`DoorsClosed`), not live state; cross-zone links only between loaded 4-neighbours; no viewer overlay;
parts with collision but no `.mesh` are dropped by the layout; `BCTYPE_SHELL_WITH_INTERIOR` is approximated.

### Stage 5, second pass (track D)

- **Closed-door leak: not a leak.** The Hub's west wall has a 150-unit stretch with no wall collision (Defensive Wall IV Short pieces end at
  z 2626; the Defensive Gate IV at -51562, 2780 is `destroyed` in the world data, so its destroyed collision stands). Paths in through it exist
  in the data, and the original would find them too (**Observed**: the placements). The three door painters of the zone are a shack door, a
  second door in the wall and a third one with no cells; none is a town gate. The strict test is therefore a property: a path made with a closed
  door never stands on a door polygon.
- **Door painters** are now grown by one cell before they paint (`DoorInflateCells`), so a thin leaf closes its doorway instead of leaving a
  diagonal slip between cells.
- **Cold zone cost.** Foliage placement ran 9 times in series (own zone and the ring), 1.2 to 1.7 s. Now the ring is placed in parallel on a pool
  of `FoliageWorld`s sharing the decoded overlay tiles (`FoliageWorld` takes an optional shared tile cache), concurrently with the terrain and
  buildings, and kept per zone (64 zones), so the next zone places only its three new neighbours. Gather of The Hub: before 1.2 to 1.9 s;
  after about 0.55 to 1.0 s cold (JIT included; the machine was loaded by other agents, so the spread is wide) and about 0.15 to 0.25 s for the next
  zone. Add the 0.25 to 0.3 s build: a first zone is ready in about 1 to 1.3 s, later ones in about 0.5 s, both off the simulation thread.
- **Live doors.** `NavDoors` (one per `NavmeshWalkability`, `walkability.Doors`): `Close(id)`, `Open(id)`, `Set(id, closed)`, `IsClosed`, `Version`.
  `id` is the building's placement id (`BuildingPlacement.InstanceId`). Door polygons keep area `Door` and carry the building they belong to
  (`ZoneNavMesh.DoorIds` / `DoorOf`, cached); a query checks the table when it considers a door polygon, so a flip needs no rebuild and is
  one volatile write. Doors start open (face data 4). `NavAgent.DoorsClosed` still treats every door as closed for one query. Callers that keep
  paths should re-query when `Doors.Version` has changed.
- **Heights.** Mesh heights are the simplified surface (cell 2, about 0.5 off on flat ground, more on slopes). `GroundHeight(x, z)` returns the
  heightmap's height where the mesh lies within 3 units of the terrain, and the mesh's height elsewhere (floors, wall tops, ramps), and
  `FindPath` puts its points on the same height. Movement must sample `GroundHeight` every tick instead of interpolating between path points.
  There is no detail mesh.
- **Not done:** interiors (a separate mesh per building with an interior mask, joined through doors) and the missing seed rules.

#### Wiring `NavSystem` into the game (track A)

```csharp
// once, after the game data and WorldLevelData are loaded (not on the simulation thread; it opens the heightmap and the seeds file)
var nav = new NavSystem(install, db, levels, (x, z) => (float)heightmap.HeightAt(x, z));   // starts the builder thread
var session = new WorldSession(..., walkability: nav.Walkability);   // the stand-in is Program.cs:196 / WorldSession.cs:33; same IWalkability, nothing else changes
nav.ZoneReady += z => { /* optional, builder thread: log, or invalidate paths for that zone */ };

// whenever the zone ring changes (the same place that loads residents), any thread, returns at once
nav.LoadRing(centreZone, 1);             // 3 x 3, nearest first; or nav.LoadZone(zone) (Task completes when live)
nav.UnloadZone(zone);                    // for zones that left the ring; the cache file stays

// doors (any thread): the building's placement id
nav.Doors.Close(buildingInstanceId); nav.Doors.Open(buildingInstanceId);

// on shutdown
nav.Dispose();
```

Thread-safety: `IsWalkable`, `GroundHeight`, `FindPath` and `Doors` are safe from any thread, always (the loaded meshes are immutable snapshots swapped
atomically; a query sees a zone only when it is complete). Where a zone (or the start or goal of a path) is not loaded yet, the open-ground
stand-in answers, so a path made before the build finished can cross a building: re-query when `ZoneReady` fires for the zones it touches.
`FindPath` costs 20 to 50 ms for a town crossing: call it from the path service's threads, never inside a tick phase.

### Stage 5, third pass (track D): interiors and the missing seed rules

- **Interiors.** Every building of a zone with an `interior mask` part that has collision (and is not `is gateway`, the same approximation of
  SHELL_WITH_INTERIOR as the exterior carvers) gets its own small mesh (`ZoneGeometryGatherer.GatherInterior`, `NavInteriors`): the building's own
  shapes in the interior mask 0x87fbe00 (groups 9 to 13, 15 to 22, 27; a destroyed building 0x82600 plus its `destroyed boundary` as cutter), walkable
  by slope (60 degrees) except group 27, furniture buildings standing inside the hull (walkable ones give floor), the door painters, and the inverted
  interior hull: one clipping slab beyond every edge of the hull removes everything outside it. It is seeded from the door's inner marker (the marker
  inside the hull, dropped on the floor). The mesh is a square of whole tiles around the hull, built with the same tiled Recast pipeline, then appended to the
  zone's mesh as extra polygons (pruned ones included) and joined to the exterior along the door polygons of the same door (edges of the interior's
  door polygons facing the exterior's, within 6 units in plan and 12 in height, become links). To queries an interior is part of the zone; its door is
  an ordinary door polygon, so `NavDoors` closes it. The street side already had the hole (the exterior carver). `NavMeshPipeline.BuildZone` does
  exterior + interiors; `NavMeshService` and the tool use it; the cache version went to 3.
- **Gate** (The Hub, zone 20.32; the Hub has no bar in its default state: 21 of its buildings are destroyed, only two have an intact door, a shack and the
  Storm House by the west wall, placement 4933): a path from the street outside the Storm House's door goes round the wall end, through the door and across
  its floor (220 long for 138 straight, 30 ms) and the way back is the same. With that door closed the room is shut. Images:
  `R:\VlcekM\MeitouClient-re\probes\nav\out\house-in.png` and `house-out.png` (teal: interior floor, orange: door polygons, cyan: the path).
- **Cost.** 21 interiors of the Hub zone build in 80 to 400 ms on the worker threads (gather, build and join together); a zone is about 0.3 s more than before at worst.
- **Seed rules.** (1) The ray rule on `seeds.def` points: a point without a height (Y -99) that a ray from above hits on a building is moved onto it, one
  with a height that hits a building is dropped (the original rays groups 9 and 10 only; ours all the zone's building triangles, an approximation); ground
  seeds (the 3 x 3 fallback) are only added where nothing is hit. (2) Linked walls (`link length` above 0): three seeds on a WALKABLE wall where the ray hits
  it, and three ground seeds clamped into the zone for walls that leave it; the three positions (0.15, 0.5, 0.85 along the wall's longest footprint
  extent) are our choice. (3) Neighbour-border midpoints: `NeighbourSeeds` adds the midpoints of the open border edges of the loaded 4-neighbours' kept
  polygons; as in the original the result depends on which neighbours were there first (a cached mesh keeps what it was built with). The Hub's 3 x 3 gains
  30 to 40 kept polygons from it.
- **Not done.** Cross-zone links beyond the four neighbours: zones that meet only at a corner share a point, so a link would have zero width and no agent fits
  through it; every passable border is already linked. Exterior layouts and furniture that the placements do not list (the Storm House floor is one empty
  room: no furniture was placed, only the parts with collision count); `interior terrain` buildings; a seed for interiors without a door (the first node
  of the building); upper floors are in the mask but untested; an interior poking past its zone's border keeps the zone's bounds (its polygons still index).
