# Game loop and time

Original Kenshi1.0.68 GameWorld/frame callbacks, timing globals, RTTI, logs and save evidence.
Engine fixed-tick choices: [simulation](../simulation.md), [engine](../engine.md); do not copy
original thread races. Tables preserve callback/time/control/constants mappings.

## Frame clocks

**Verified** (`FUN_14082ac70`, constants `DAT_141696c74` = 0.05 and `DAT_14167e8d8` = 0.5 from `re/data_values.txt`): the real frame time is
replaced by 0.05 s when above it (the `min(dt, 0.5)` in the other branch never binds), so a frame longer than 0.05 s contributes at most 0.05 x speed and the game slows
below 20 fps at every speed; the cap comes before the speed multiplier. The capped value is then pushed into a 20-entry history (`FUN_1402b2640`, the 0.5 is its
weight base) that writes back the weighted average of the entries so far (weights 0.5^i; which end is newest is **Unknown**), and that smoothed value is what
`FUN_14082b6e0` passes to `FUN_140788a00`. Meitou keeps its own wider budget and no smoothing instead (**engine choice**, [simulation](../simulation.md#time-model)).
Twenty-entry zero-initialized history exponentially weights0.5^i. **Verified** callee/history
layout. **Verified** FUN_140788a00 clocks: real smoothed dt; dt*speed zeroed by paused flag;
dt*speed floored0.01 only when speed<=0, not universally when a menu sets requested-pause.
The last global also changes during callbacks; AI thread can read different values mid-frame.
Real time drives autosave/zone-unload/physics kick; speed time drives weather/characters/world.

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

Loaded zones (3x3 round the camera's zone, and the +-340 box round the player's characters; [corpses](character-stats.md#corpses)) drive squad membership. **Observed** active platoon margin170,
countdown held4s while relevant,5s without zone; release members outside zone or within600 of
an outside-zone leader when irrelevant. Unloaded squads update abstract travel/AI, not detailed
character rendering. Zone timers, player focus and hibernation are separate concepts.
CONSTANTS max squads10/max squad size20/max faction size30 are data facts, enforced by the squads screen and recruiting ([ui-screens](ui-screens.md#squads-tab)).
Autosave/control timing is real, not the same as game-hour simulation.

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
| Pause (`pause`, default Space) | action `0x12` (`FUN_1403636c0` maps names to ids, [ui-input.md](ui-input.md#4-action-table); handled in `FUN_140788100` → `FUN_140787fb0`) | One remembered speed (`DAT_142132ed8`, written only by `FUN_140787fb0`; **Verified**: writer scan, `FUN_140787fb0`, `FUN_140788100` case `0x12`). The key passes "pause" when the paused flag is clear. Every call first refreshes the remembered speed from the current speed when that is above 0 (the first call of the process also takes a speed of 0). Pause: speed 0. Resume: speed = the remembered value (0 leaves the game paused). So the key resumes at the speed it last saw running, whatever the speed buttons did since; after a button pause with nothing stored yet it does nothing. Other screens call it to pause (argument 1: `FUN_1405f03c0`, `FUN_140727820`, `FUN_140770a60`) or resume (argument 0: `FUN_140721f20`, `FUN_1405e9f40`; also `FUN_14036cb80`, the world unload that every menu load and new game runs). `FUN_1405f5d10` is the CharacterEditWindow destructor, not a load step. **Unknown**: the speed a load leaves; Meitou does not model the unload call. Meitou: `SimulationClock.PauseKey` / `Pause` (the Build panel and Pause after loading use `Pause`, so the key resumes them) |
| `speed_1` (F2) / `speed_2` (F3) / `speed_3` (F4) | actions `0x13` / `0x14` / `0x15` → `FUN_140787e40` | speed **1.0 / 2.0 / 5.0** |
| Time buttons in the main bar (`Kenshi_MainPanel.layout`: `TimeSpeedButton1` to `4`) | `FUN_14072d3b0` gives their float user data **0, 1, 2, 5**; click handler `FUN_140724920` calls `FUN_140787e40` | pause, 1x, 2x, 5x. A click only sets that speed (the pause button sets 0 again, never resumes) and leaves the remembered speed alone, as do the F-key speeds. The exe holds the strings "Pause Game", "Normal game speed", "Faster game speed", "Fastest game speed" next to the key binding names in `FUN_1403f0260` (**Observed**: they name the controls, not tooltips; the HUD builder sets no tooltip on these buttons, [Tooltips](ui-screens.md#tooltips)) |

### Threads

| Thread | Class (RTTI) | Kicked | Time given | Work |
|---|---|---|---|---|
| main | | | | Ogre loop, all of the above |
| "AI Rendertime Backthread" | `RenderTimeBackthread` (derived from `ThreadWannabe_HavokCompatible`), `world +0x790` | `frameStarted` (`FUN_1403beeb0(thread, dt)`) | the global `DAT_142133794` | `FUN_140787940`, below |
| physics | `PhysicsActual`, `world +0x18` | `GameWorld::vfunc_0` | real `dt` | `FUN_1407dd060`: dt = 0 if paused, capped at **0.04 s**, a virtual call on the scene object with `dt` (presumably PhysX `simulate`, **Unknown**: not checked against the PhysX DLL), a flush, then a blocking `fetchResults`-like call with an error log "Failed to fetch results" |
| helper | `world +0x8c0` | `GameWorld::vfunc_0` | real `dt` | **Unknown** |
| worker | `DAT_1421349d0` (started at start-up with the lowest priority) | its `+0x20` virtual is called every `frameEnded` | | **Unknown** role |
| navmesh zone queue | `FUN_1403af0f0` (a loop with `Sleep`, calls `FUN_1403ae490`) | message queue | | zone load/unload requests for the AI navmesh (below); the function is `NavMesh` slot 1 and registers the thread name "NavMeshMain" with the Havok thread layer before looping, so a separate thread is **Observed** (the `_beginthreadex` call that starts it was not traced) |

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

Each budget continues after the list position (counting every character) of last frame's last turn; a character at or before it gets a turn while the turns
taken are under that position - list length + 1 + turns (the head takes what the tail left of a full round); a budget that runs out of characters starts the next
frame at the head. Shares of frames with a turn (**Observed**, a simulation of this rule): a flagged character gets one in every frame with fewer than 4 flagged;
with many unflagged characters in the list, 1/P with P = ⌈m/k⌉ (+1 when k divides m), so 1/2 for 4 to 7 flagged, 1/3 for 8 to 11; in an all-flagged list
characters near the head get more (m = 5: three every frame, two every other). An unflagged one gets about 6/b of b. The medical time uses it
([character-stats](character-stats.md#time)).

The flag (KenshiLib `isVisibleUpdateMode`; **Verified** decompile of the visibility pass `FUN_1405c9f60`): set with `+0xe5` whenever `FUN_1405c79c0` holds: the
character's faction is the player's (Faction `+0x250` `isPlayer`), Character `+0x250` (`_isEngagedWithAPlayer`) is set, or a zone test of `FUN_1409c4b10`
(**Unknown**). Otherwise set when the character is in view (`+0x1a9`: within `npc range`, `DAT_1421334c8`, of the camera's node `FUN_1407f2a20`, and passing the
visible-area and frustum tests) and its platoon tests pass (**Unknown** in detail). Meitou: `MedicalTimeBudget`, [character-stats](character-stats.md#time).

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
- Whether the float timestamp in the frame timer is audible after hours of play.
- The platoon tests behind `+0xe4` for an NPC in view and the zone test `FUN_1409c4b10`; `npc range` is a 3D squared distance from the camera's node (`FUN_1407f2a20`).
- What the heavier per-faction step `FUN_1406b97c0` does with the accumulated time, the other speed-scaled timers (`FUN_140792500`, `FUN_1409a7930`; `FUN_140857500` is the hearing cooldown, [perception](perception.md#hearing)), and which function starts a loaded save at speed 1 (only the character-creation window's destructor `FUN_1405f5d10` was seen to set it).
- Character slots 79 and 80, the second helper thread (`world +0x8c0`), the pending-event list at `+0x608`, `FUN_140671f50`, `FUN_1407869e0` and several smaller steps of `FUN_140788a00`.
- Whether physics substeps or fixed-step settings are used (the PhysX scene desc has `timeStepMethod`/`maxTimestep` fields; where the game sets them was not found).
- Whether character movement compensates for the non-scaled physics step at 2x and 5x.
- Which parts of the world use hours vs days for their own timers (about 50 readers of the clock).

## Expression reference

- Summary: `VSync=No`
- Measuring the frame time: `dt_smooth = Σ w_i · d_i / Σ w_i`; `w_i = 0.5^i`
- The clock: `total game hours = day × 24 + time of day`; `Δhours = dt_real × speed × 11/1200`
- The character update budget: `k = 4`; `k = ⌊m/4⌋`; share `1/P`, `P = ⌈m/k⌉`
- Settings and constants: `VSync Interval=1`
