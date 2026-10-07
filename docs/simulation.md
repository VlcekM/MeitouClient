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

- `meitou` boots into the world: `WorldSession` (`src/Meitou.Engine`) runs a fixed 30 Hz tick of input actions, the camera rig
  and `GameClock`, and the renderer draws the camera interpolated between ticks ([engine.md](engine.md#the-game-loop-meitou-meitouenginetime)).
  The renderer is complete enough for play (terrain, buildings, foliage, water, sky, shadows, impostors, upscalers).
- The data layer reads everything a world needs (FCS with the game's merge rules, load order, zone and town placements), and
  `CharacterGenerator` rolls an NPC's appearance and loadout. There are no typed views yet over FACTION, SQUAD_TEMPLATE, TOWN,
  AI_PACKAGE, AI_TASK or the CONSTANTS used by the simulation: code reads `GameRecord` fields by name where it needs one.
- Stage 0 (time facts) is done on branch `sim-core`: `GameClock` runs at the game's 1200/11 s per game hour and `WorldSession`
  offers the game's pause, 1, 2 and 5 (F2 to F4), with a real-time control tick and a game-time simulation tick
  ([engine.md](engine.md#the-game-loop-meitou-meitouenginetime)).
- Missing for a living world: the world model (factions, squads, characters), movement and walkability, character drawing (the
  viewer's character renderer was dropped in phase 8, DECISIONS 23; how it worked is in [character-viewer.md](character-viewer.md)),
  AI, the UI, saves.

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
- **Static data.** Typed views built once at load over `GameRecord`: `GameConstants` with the loader's rescalings
  ([character-stats.md](game/character-stats.md#constants-used-by-this-subsystem)), races, factions and the relation table's
  initial values, squad templates, towns, AI packages and tasks. Probe tables from the research become tests (skipped without
  the game).

## Walkability and movement

Facts ([pathfinding.md](game/pathfinding.md)): the original uses a Havok navmesh per zone; the shipped tiles are a cache that
is regenerated when the zone's building hash differs (mods that add or move buildings) and patched at run time for player
buildings, doors and interiors; path following, steering and avoidance are Kenshi's own code.

Because the original regenerates whenever buildings differ, a drop-in replacement needs a navmesh generator in any case. The
recommendation (owner decision 3) is our own navmesh, built from the terrain heights, building shapes (by BUILDING `path mode`),
foliage flags, doors and the water level with Recast-style generation, cached per zone in our own format and keyed by a hash of
the zone's buildings; the Havok tiles serve only as a reference to compare against. The alternative is a clean-room reader for
the Havok tagfile tiles plus the same generator for changed zones.

Open research before that stage: where building collision shapes come from (`.phs` and `.PxProj` are not analysed,
[formats/overview.md](formats/overview.md)).

Until then movement uses a stub: straight lines on the terrain, no obstacles, nobody enters the water. Characters walk through
walls, which is enough to watch squads move. Movement itself follows `CharMovement` (speed from stat S in decimetres per
second, the speed-mode caps, acceleration 15/s, its own separation steering).

## Characters on screen

A native character renderer is rendering work and can run beside the simulation stages: skinned meshes, attachments,
appearance and the animation layers as [character-viewer.md](character-viewer.md), [characters.md](characters.md) and
[animation.md](animation.md) describe, drawing many characters from the snapshot (bone palettes in a buffer, instancing per
mesh, mesh LOD). Until it exists, the simulation is seen through debug markers.

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
| **5. Navmesh** | Building collision research; generator (or reader) per decision 3; path queries on their own threads; the stub replaced | Paths around buildings and through gates; generation off the frame |
| **6. Player** | New game from NEW_GAME_STARTOFF, the player's squad, selection and move orders ([ui-input.md](game/ui-input.md)), a minimal HUD (clock, speed buttons) | Start a game, select a character, walk it across The Hub |
| **7. Bodies** | Stats and XP, hunger, blood, body parts, KO and death ([character-stats.md](game/character-stats.md)) | Probe tables as tests |
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
