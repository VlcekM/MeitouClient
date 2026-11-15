# Game loop and time

Original Kenshi1.0.68 GameWorld/frame callbacks, timing globals, RTTI, logs and save evidence.
Engine fixed-tick choices: [simulation](../simulation.md), [engine](../engine.md); do not copy
original thread races. Tables preserve callback/time/control/constants mappings.

## Frame clocks

**Verified** speed literals0,1,2,5; no3x. Pause remembers nonzero speed; requested pause and
speed0 combine into paused state (GameWorld `+0x8b9`, `DAT_1421349c9`, set only by `FUN_140787d40`; **Verified** writer scan). Creation resumes1x. Paused clock/physics/factions stop;
presentation refresh can continue. Wwise labels5x as x4 (**Observed**). **Verified** clock
1200/11 real/game-speed seconds per game hour;24 hours/day, date/daylight from CONSTANTS.
Save clock/camera values are records, not an inferred wall-time timestamp.

## Update, threads and budget

Frame callbacks drive presentation/render and then world work; original order of world systems,
queued jobs and visibility updates matters. Helpers lock/copy pause/speed state; AI back thread
has its own budgets and races. Characters near/far/unloaded follow different update cadences;
round-robin fairness and dt accumulation differ from updating every entity each rendered frame.

### Frame callbacks

The game's one frame listener is `MainListener` (RTTI; vtable `14171c4f0` has the `Ogre::FrameListener` layout of this fork:
slots 0 to 3 = `frameStarted`, `frameRenderingQueued`, `framePostAnimationUpdates`, `frameEnded`, slot 4 is the destructor).
**Verified**: the slot order matches `Ogre::FrameListener` in the Ogre dump and slot 4's function is the destructor shape.

| Order in a frame | Slot, function | What it does (**Observed**, one reading each) |
|---|---|---|
| 1. `frameStarted` | `FUN_14082b370` | Polls keyboard and mouse (two calls on objects at +0x78/+0x80, presumably OIS `capture`); title-screen update when the menu is up; **advances the sky clock** (`FUN_14066ff60`); kicks the AI back thread (below) when the settings byte at +0x70 is non-zero (threaded mode); a few UI/singleton updates. During start-up (a loading object at +0x70) it runs the loading steps instead (initialises Ogre resource groups once, then "Starting Title Screen"). |
| (render) | | updateSceneGraph, compositor update; during it `framePostAnimationUpdates` (`FUN_140829df0`, sets a byte at +0x88 that `frameEnded` consumes and clears) |
| 2. `frameRenderingQueued` | `FUN_14082ac70` | **Measures the frame time** (below). |
| 4. `frameEnded` | `FUN_14082b6e0` | Cursor visibility; in menu/title state (`DAT_142133320` set) it only calls the new-game/import handler `FUN_14047c310` and returns; otherwise calls **`GameWorld::vfunc_0` = `FUN_140788a00(&GameWorld, dt)`**, the whole world update; then a **floating-origin rebase**: when the camera is more than 10000 units (squared distance 1e8, x/z) from the scene's relative origin, the origin is moved and zone-manager/terrain data re-based (calls `FUN_140409c70`, `FUN_140a08d10`, `FUN_1406083b0`); then hotkey state handling. |

**The `DAT_142133308` object in `frameStarted`** (**Verified**, decompile): the global is field `+8` of the engine singleton at `0x142133300` (the world object whose `+0x20` is the title screen, `DAT_142133320`); `FUN_14086cf90` (start-up, log lines "Renderer Created" / "Root Initialised") writes it with a new `Renderer` (`FUN_140815ab0`, base `RendererT` with a list of `RenderEntityT`; fields `+0x58` camera, `+0x60` scene manager, `+0x68` root). Its vfunc 8 (`FUN_14082e580`) refreshes the camera's parent node transform and calls vfunc 1 of each registered render entity: Ogre scene-side visual updates with no simulation state. Meitou's renderers update their own scenes from the published snapshot, so there is no counterpart to compare (not a difference).

### The three time values

`FUN_140788a00` (which `docs/formats/weather.md` already lists as "Time bases", **Verified**) sets three globals every frame, with
`dt` its argument and `speed` the float at GameWorld `+0x700` (the singleton is `DAT_142134110`, so `speed` is the global `DAT_142134810`):

| Global | Value | Used for |
|---|---|---|
| `DAT_142133790` | `dt` (real, capped, smoothed) | SkyX's own update, autosave timer, zone unload timers, pending-order timers, the physics kick, the second helper thread |
| `DAT_142133798` | `dt × speed`, **0 when the paused flag is set** | sky colour/cloud transitions, effect timers, weather updates |
| `DAT_142133794` | `dt × speed`, replaced by **0.01 when that is ≤ 0** (speed 0), not zeroed by the paused flag | characters, factions, squads, zone/area ticks, wetness/dust/haze |

### Speed and pause

**Verified** (two independent places for each value):

| Control | Where | Effect |
|---|---|---|
| `speed_1` (F2) / `speed_2` (F3) / `speed_3` (F4) | actions `0x13` / `0x14` / `0x15` → `FUN_140787e40` | speed **1.0 / 2.0 / 5.0** |

### Threads

| Thread | Class (RTTI) | Kicked | Time given | Work |
|---|---|---|---|---|
| main | | | | Ogre loop, all of the above |
| "AI Rendertime Backthread" | `RenderTimeBackthread` (derived from `ThreadWannabe_HavokCompatible`), `world +0x790` | `frameStarted` (`FUN_1403beeb0(thread, dt)`) | the global `DAT_142133794` | `FUN_140787940`, below |

### The character update budget

`FUN_140786e30` runs on the main thread every unpaused frame over the world's active character list (**Verified**, decompile). The lists it fills belong to
the AI back thread object `DAT_1421348a0` (kicked by `frameStarted`, or run inline through its slot 0x18 at the loop's end); its work `FUN_140787940` runs list
`+0x1a8` with slot 0xd8 `threadedUpdate`, `+0x1c0` with slot 0x278 `threadedUpdate4` (the medical update), `+0x1d8` with slot 0x280 `threadedUpdatePeriodic`
(KenshiLib names; **Observed**: the job's object is taken as that thread object by its list offsets, the call was not traced):

| Tier | Which characters | Turns a frame | Work |
|---|---|---|---|
| all | every character | all | `FUN_1405c7ae0`: `+0xc8` and `+0xcc` += `dt_scaled` |
| A | any character | **8** | slot 0xe8 `periodicUpdate` (`FUN_1405ccd90`); list `+0x1d8` |
| B | flag `+0xe4` clear | **6** | `FUN_1405cfd90` (`+0xc8` += `dt_scaled` again); slot 0xe0 `update` (`FUN_1405cf130`, with `+0xc0` + `dt_scaled`); lists `+0x1c0`, `+0x1a8`. Without a turn only `+0xc0` += `dt_scaled` |
| C | flag `+0xe4` set | `k = 4` when `⌊m/4⌋ < 5`, else `⌊m/4⌋` (m = last frame's flagged count) | every frame: slot 0xe0 and list `+0x1a8`; with a turn: `FUN_1405cfd90` and list `+0x1c0` |

### Update time off screen

**Verified** (decompile, 1.0.68) and **Observed** (Frida on 1.0.65, the oracle-base-cage-blesse save at Rebirth, speed 1):
- The mover update (`CharMovement` slot 0x58) is called only by Character slot 0xd8 `threadedUpdate` (`FUN_1405c7c30`, the runner's list `+0x1a8`), with the
  character's `+0xc4` as its time. Slot 0xe0 `update` (`FUN_1405cf130`) writes `+0xc4` = `DAT_142133794` + `+0xc0` (0.01 when not above 0). `+0xc0` is the time
  gathered since the character's last turn.
- So a character out of view moves, and runs every consumer of `+0xc4`, on twice the time.
  - Observed: over 7.5 real seconds, the sum of `+0xc4` was 2.00 x the real time for the 121 characters out of view and 1.00 x for the 7 in view.
  - The game clock ran 33 game seconds a real second, as above.
  - Ground speed in clock time: walkers 0.9 instead of 0.45, joggers about 3.4 instead of 1.65.
- The other consumers of `+0xc4` (`FUN_14050d920`, `FUN_1405b4770`, `FUN_14043aee0`, `FUN_140685090`, the sensing `FUN_140853de0`) are not read.
- Meitou: `MovementSystem.Move` steps a character out of view (`MedicalClock.OnScreen` against `World.View`) on twice the tick time; with no camera
  (headless worlds, tests) on the tick time. The other consumers keep the tick time (**gap**).

### Inactive characters

`FUN_1405c9410` (a virtual of the logic tick, **Verified**): the zone record at the character's X/Z (vfunc `0xc8`; none, off the grid: nothing happens); a character that is not active (vfunc `0xf8`, flag `+0x608`) in a zone whose loaded flag holds (`FUN_140a090b0`) is activated (vfunc `0x200`, `FUN_1405cc310`: scene node, physics, lists), an active one in a zone that is not loaded is deactivated (vfunc `0x208`, `FUN_1405c9490`: clears the flag and releases the scene and physics objects). An inactive character is out of the active set the update walks, so it neither thinks, moves nor ages while its zone is unloaded, even when its platoon stays loaded (the platoon keeps relevance by its effective leader's place). Meitou: `World.MarkDormant` sets `CharacterHot.Dormant` from `World.LoadedZones` once a tick (none: everyone active); the AI think, movement, body, combat, first-aid, aggro scan and animation loops skip a dormant slot. Serial duties (carrying, captives, production) still look at it but act on tasks the frozen movement no longer advances.

### Settings and constants

Values from the install through the repo's readers (**Verified**, `GameDatabase.Load(LoadOrder.FromInstall)`, one `GLOBAL CONSTANTS` record, `110-gamedata.quack`, modified by `Newwworld.mod` and `rebirth.mod`; `LoadOrder.BaseGame` gives the same numbers, but note that it still includes those two mods, which are part of the core file list; the `fcs.def` defaults differ, e.g. `max faction size` 100, `starvation time` 24, `build speed` 1):

| Field | Value | Meaning (`fcs.def` text; use in the exe **Unknown** unless noted) |
|---|---|---|
| `days per year` | 100 | days in a year (season lengths, weather.md) |
| `sunrise` / `sunset` / `latitude` | 5 / 23 / 54 | the sun path and day/night |
| `night darkness` | 0.35 | exposure factor at night (lighting.md) |
| `build speed` | 6 | "multiplier for overall construction rate" |
| `production speed` | 2 | "multiplier for overall production speeds" |
| `research rate` | 0.3 | "multiplier for overall research TIMES, so < 1 is faster" |
| `research level increase rate` | 1.3 | research time growth per tech level |
| `prison time` | 1 | multiplier for imprisonment time |
| `starvation time` | 60 | in-game hours per 100 hunger points |
| `knockout time base` | 20 | base KO time added to the damage-based time |
| `animation blend rate` | 4 | speed of blending between animations |
| `bed hunger rate` / `encumbrance hunger rate` / `fed recovery rate mult` | 0.33 / 0.7 / 3 | hunger |
| `bleed rate` / `bleeding clot rate` / `blood recovery rate` | 0.01 / 0.0085 / 0.4 | blood (rate per game time) |
| `heal rate mult` / `resting heal rate mult` | 0.25 / 2 | healing |
| `bodypart degeneration rate` / `robot wear rate` / `stun recovery rate` | 1.5 / 1 / 1 | |
| `max squads` / `max squad size` / `max faction size` | 10 / 20 / 30 | |

## Unknowns

- The start-of-new-game time of day. The saves in the install are empty; the sample save of [../formats/save.md](../formats/save.md#camera-type-56-id-1051-the-world-record) (a new game, saved at in-game day 1, 13:39) holds the `time day` / `time hour` / `time minute` and sky keys described above (**Observed** there), but the starting time itself is not traced.
- Whether physics substeps or fixed-step settings are used (the PhysX scene desc has `timeStepMethod`/`maxTimestep` fields; where the game sets them was not found).
- Whether character movement compensates for the non-scaled physics step at 2x and 5x.

## Expression reference

- Summary: `VSync=No`
- Measuring the frame time: `dt_smooth = Σ w_i · d_i / Σ w_i`; `w_i = 0.5^i`
- The clock: `total game hours = day × 24 + time of day`; `Δhours = dt_real × speed × 11/1200`
- The character update budget: `k = 4`; `k = ⌊m/4⌋`; share `1/P`, `P = ⌈m/k⌉`
- Settings and constants: `VSync Interval=1`
