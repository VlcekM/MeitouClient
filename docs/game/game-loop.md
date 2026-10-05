# Game loop and time

How `kenshi_x64.exe` structures a frame, what "time" means in it (real time, game speed, the in-game clock), and what it updates
every frame versus rarely or not at all when something is far from the player. Labels as in [../README.md](../README.md).

Sources: the decompile dump of `kenshi_x64.exe` (Ghidra default names; addresses like `FUN_140788a00` cite functions, class and
slot names come from the MSVC RTTI tables) and of the bundled `OgreMain_x64.dll` and `SkyX_x64.dll`, read outside the repository on
2026-10-05; the install's `data/gamedata.base` (+ `Newwworld.mod`, `rebirth.mod`) read with the repo's `GameDatabase` (probe
`R:\VlcekM\MeitouClient-re\probes\gameloop`), `fcs.def`, `settings.cfg`, `controls.cfg`, `data/gui/layout/Kenshi_MainPanel.layout`.
No decompiled text is quoted; rules are restated. The install's `save/` folders hold no files; the one sample save, from the user's save folder, is analysed in [../formats/save.md](../formats/save.md) and was not used for this doc.
Related: [formats/weather.md](../formats/weather.md) (weather calendar and its time bases), [formats/terrain.md](../formats/terrain.md#sun-path-verified-kenshi_x64exe-sky-controller-and-the-constants-record)
(sun path), [formats/sky.md](../formats/sky.md), [formats/lighting.md](../formats/lighting.md), [formats/zones.md](../formats/zones.md),
[formats/camera.md](../formats/camera.md).
Subsystems driven from this loop: [ai.md](ai.md) (what the AI thread runs per character), [pathfinding.md](pathfinding.md) (the NavMesh thread and movement),
[factions-squads-towns.md](factions-squads-towns.md) (factions, platoons, population), [character-stats.md](character-stats.md) (needs, blood, healing, XP),
[combat.md](combat.md), [buildings-production.md](buildings-production.md) and [economy.md](economy.md) (time-scaled rates and the day length), [ui-input.md](ui-input.md) (the input actions run in step 4 of the world update),
[ui-screens.md](ui-screens.md) (speed buttons, clock text) and [../formats/save.md](../formats/save.md) (the saved clock and the autosave).

## Summary

| Question | Answer | Label |
|---|---|---|
| Fixed or variable timestep? | Variable: one frame time per rendered frame, capped at 0.05 s and smoothed over 20 frames; physics steps in its own thread with its own cap (0.04 s) | **Observed** |
| Tick rate | None: everything runs once per rendered frame (`kenshi.cfg` has `VSync=No`; no frame limiter was found in the loop, not searched exhaustively) | **Observed** |
| Game speeds | 0 (pause), 1, 2, 5; hard-coded literals (**Verified**); no console or debug speed found (**Observed**) | **Verified** |
| Day length | 1 game hour = 109.09 real seconds at speed 1, so a day = 2618 s = 43.6 min; 100 days per year (CONSTANTS) | **Verified** |
| Where the game runs | Inside `Ogre::Root::startRendering`; the world update is in the `frameEnded` callback, after rendering | **Verified** |
| Near vs far | Per character: a "relevance" test (range `npc range` = 3250, camera view) decides full update every frame or a round-robin of 6 per frame with accumulated time; squads off screen are simulated in an abstract way, one per faction per frame | **Observed** |

## Main loop

- **Verified** (game: `FUN_14086cf90` ends in `FUN_140813460`, a thunk to `Ogre::Root::startRendering`; Ogre dump: `startRendering`
  is a `PeekMessage` / `TranslateMessage` / `DispatchMessage` pump followed by `renderOneFrame` until a stop flag). The game has no loop of its own.
- **Verified** (Ogre dump `Root::renderOneFrame` / `_updateAllRenderTargets`): one frame is, in order, `frameStarted` listeners →
  `SceneManager::updateSceneGraph` for every scene manager → compositor update (the rendering itself) → `frameRenderingQueued`
  listeners → swap of the final targets → `frameEnded` listeners.
  This build of Ogre has a fifth callback (`framePostAnimationUpdates`, slot 2). **Observed**: it is fired by
  `Root::_fireFrameListenerPostAnimationThreads`, whose only caller is `SceneManager::updateAllOldAnimations`, itself called from
  `SceneManager::_cullPhase01` (a virtual call during the render, no direct caller in the dump). So it comes *during* the compositor
  update, before `frameRenderingQueued`, and possibly once per culled camera; the game's handler only sets a flag, so the count does not matter.
- Start-up (`FUN_14086cf90`, **Observed**) logs "Startup", "Renderer Created", "Root Initialised", "Started", builds the game world,
  GUI and thread objects, starts the worker thread and starts rendering. It does **not** set the game speed: the speed global
  (`DAT_142134810`) lives in the exe's zero-initialised data (checked in the PE image), so the speed is 0 until the game-start routine
  `FUN_1405f5d10` (a large function named by the naming pass after its use of `starting health`) un-pauses and sets it to 1.0.

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

Consequence (**Verified** from the order above, **Observed** for the use): the world is simulated *after* the frame is rendered, with
the frame time measured at the end of this frame's rendering call; the clock and the AI back thread are advanced at the *start* of the
next frame, using values the previous `frameEnded` left behind.

## Measuring the frame time

`FUN_14082ac70` (**Observed**; the pieces cross-check each other):

1. A performance-counter timer (`CPerfTimer`, microseconds) gives the time now; the stored previous time is a **float** of seconds,
   so the difference carries the float's rounding (about 0.25 ms at one hour of play, about 4 ms at ten hours). Possible jitter source; **Unknown** whether it is audible.
2. The raw frame time is **capped at 0.05 s** (one branch, `dt > 0.05` gives 0.05; a second branch that would cap at 0.5 s is unreachable). Below 20 fps the game therefore slows down instead of taking bigger steps.
3. It is passed through `FUN_1402b2640` (class at `DAT_142133658`): a 20-entry history (initialised by `FUN_14063e3d0(…, 20, 0)`), newest first,
   result `dt_smooth = Σ w_i · d_i / Σ w_i` with `w_i = 0.5^i`, written back in place. The history starts at zeros, so the first frames are small.
   (Ghidra shows the push call without its value argument. **Verified** by reading the callee `FUN_1402b2130`: it takes a value pointer
   as its second parameter and stores that value at the front of the deque, after which the smoothing function drops the oldest entry
   so the length stays 20; the weight ratio 0.5 is the constant stored at the start of the object.)

The result is the global `DAT_142133794` and is what `frameEnded` passes into `GameWorld::vfunc_0`.

### The three time values

`FUN_140788a00` (which `docs/formats/weather.md` already lists as "Time bases", **Verified**) sets three globals every frame, with
`dt` its argument and `speed` the float at GameWorld `+0x700` (the singleton is `DAT_142134110`, so `speed` is the global `DAT_142134810`):

| Global | Value | Used for |
|---|---|---|
| `DAT_142133790` | `dt` (real, capped, smoothed) | SkyX's own update, autosave timer, zone unload timers, pending-order timers, the physics kick, the second helper thread |
| `DAT_142133798` | `dt × speed`, **0 when the paused flag is set** | sky colour/cloud transitions, effect timers, weather updates |
| `DAT_142133794` | `dt × speed`, replaced by **0.01 when that is ≤ 0** (speed 0), not zeroed by the paused flag | characters, factions, squads, zone/area ticks, wetness/dust/haze |

Notes (**Observed**):
- `DAT_142133794` is also written by `frameRenderingQueued` (real `dt`) before `frameEnded` runs, so between the two callbacks it holds the
  real time, and during `frameStarted` and rendering it holds the previous frame's scaled time. The AI back thread runs during rendering
  and can read either value; that race is a property of the original, not something to copy.
- The 0.01 floor only applies when `speed` is exactly 0 (the pause button or the pause key). When the **paused flag** is set while `speed` is
  non-zero (menus, see below), `DAT_142133798` is 0 but `DAT_142133794` still advances by `dt × speed`; weather.md's "0.01 when paused" holds for the first kind.
- Everything that is *not* scaled: SkyX's update (it multiplies by its own time multiplier, see the clock), the physics step, the autosave,
  zone unload timers, pending-order timers.

## Speed and pause

**Verified** (two independent places for each value):

| Control | Where | Effect |
|---|---|---|
| Pause (`pause`, default Space) | action `0x12` (`FUN_1403636c0` maps names to ids, [ui-input.md](ui-input.md#4-action-table); handled in `FUN_140788100` → `FUN_140787fb0`) | Toggle on the paused flag: pausing sets `speed` to 0 and remembers the last non-zero speed (`DAT_142132ed8`); resuming restores it. Other screens call the same function to pause (argument 1: `FUN_1405f03c0`, `FUN_140727820`, `FUN_140770a60`) or un-pause (argument 0: `FUN_14036cb80`, `FUN_1405e9f40`, `FUN_140721f20`, `FUN_1405f5d10`) |
| `speed_1` (F2) / `speed_2` (F3) / `speed_3` (F4) | actions `0x13` / `0x14` / `0x15` → `FUN_140787e40` | speed **1.0 / 2.0 / 5.0** |
| Time buttons in the main bar (`Kenshi_MainPanel.layout`: `TimeSpeedButton1` to `4`) | `FUN_14072d3b0` gives their float user data **0, 1, 2, 5**; click handler `FUN_140724920` calls `FUN_140787e40` | pause, 1x, 2x, 5x; the exe holds the tooltip strings "Pause Game", "Normal game speed", "Faster game speed", "Fastest game speed" (they sit in `FUN_1403f0260`, which [ui-input.md](ui-input.md#10-settingscfg-and-the-options-window) identifies as the options-tab builder; that they belong to these four buttons is **Observed** from the wording only) |

- Speeds are literals: every caller of the speed setters passes 0, 1.0, 2.0 or 5.0 (checked in the action switch, the button handler, `FUN_1405e9f40` and `FUN_1405f5d10`); no CONSTANTS field, setting or console command sets them (**Verified** for CONSTANTS/settings: `GAME SPEEDS` in `fcs_fields` is only a `fcs.def` heading that holds `build speed`, `production speed`, `prison time`, `research rate`, `research level increase rate`, which are multipliers of other things, see Settings and constants, and the string does not occur in the exe; **Observed** for the console: no console strings found). There is no 3x speed; `speed_3` is 5x.
- Other code reads the speed global too (**Observed**, from `data_xrefs`): `FUN_140792500` (a wall-clock timer scaled by the speed), `FUN_1409a7930` (a countdown scaled by it), `FUN_140857500` and the unloaded-squad update; their purposes are not decoded.
- **Observed**: the sound system gets the state `Game_Speed` = `Normal`, `x2` or `x4` (5.0 is labelled `x4`) and a one-shot event `Game_Speed_*` when the speed is set from the UI; `Pause_Game` / `Resume_Game` events on the paused flag.
- Two things pause the game (**Observed**, `FUN_140787d40`): the paused flag at GameWorld `+0x8b9` (`DAT_1421349c9`) is set to *requested pause OR speed == 0*. Other callers (including the save system and menu code) set the flag directly without touching the speed; the speed buttons and the pause key go through speed 0. When the flag *changes*, it copies (flag, speed) into the locked state records of the helper threads (`FUN_14078ac50`; three objects) and updates the button highlight (`FUN_140724880`); `FUN_140787e40` and `FUN_140787fb0` also write the record of one of those objects on every call.
- Finishing character creation un-pauses and resets the speed to 1 (`FUN_1405e9f40`, **Observed**).
- While paused: the clock does not advance (SkyX multiplier 0); `FUN_140787230` runs instead of the character scheduler (it only gives each character a presentation refresh, slot 78); the faction update gets dt 0; physics gets dt 0; unloaded squads return at once; the weather's own transitions get 0. A "PAUSED" label panel is shown (`PausedPanel`).

## The clock

The sky object (`DAT_1421303d0`, class created by `FUN_14066e380`, the same one weather.md and sky.md call "the sky controller") owns the game clock.

- **State** (**Verified**: `FUN_14066ff60` computes it each frame, `FUN_14066d7d0`/`FUN_14066db50` write and read it): integers hour `+0`, minute `+4`, day `+8`; a float time of day in hours `[0, 24)` in the SkyX controller (`+0x1c`); and `total game hours = day × 24 + time of day` as a double at `+0xa0`. Minutes are `floor(frac(time of day) × 60)`; seconds are not kept.
- **Advancing** (**Verified**: `FUN_14066f190` (Sky_Update), `SkyX::SkyX::update` in the SkyX dump, controller slot 1 `FUN_14066d400`): each frame SkyX's time multiplier is set to `speed × 0.0091666…` hours per second (0 when the paused flag is set); `SkyX::update` multiplies it by the **real** `dt` and passes the product, in hours, to the controller, which adds it to the time of day and wraps at 24. The day counter increments when the wrap is seen. In symbols: `Δhours = dt_real × speed × 11/1200`.
  Cross-check: `FUN_14066d3f0` returns 109.09, which is `1 / 0.0091666…`.
  The `dt` handed to SkyX is `DAT_142133790` (read at `frameStarted`, so the value stored by the previous frame's world update, one frame
  late; `FUN_14066ff60`'s second argument at its call site is ignored). It is the capped value, so below 20 fps the clock runs slow too.
  **Verified** by `Sky_Update` (the speed global, the paused flag and the `0.009166666` literal, and the pointer passed to SkyX) together
  with `SkyX::SkyX::update` (multiplier × dt) and the controller's add-and-wrap function; `SkyX` offsets +0x78 (multiplier), +0x38 (controller).
  The game updates SkyX itself; it does not register SkyX as an Ogre frame listener (**Observed**: none of the six `addFrameListener` callers builds SkyX), so the update is not applied twice.

| Speed | game hours per real minute | real seconds per game hour | real minutes per day | real hours per year (100 days) |
|---|---|---|---|---|
| 1 | 0.55 | 109.09 | 43.64 | 72.7 |
| 2 | 1.10 | 54.55 | 21.82 | 36.4 |
| 5 | 2.75 | 21.82 | 8.73 | 14.5 |

- **Sun constants**: `latitude` 54, `sunrise` 5, `sunset` 23, `days per year` 100 (merged `GLOBAL CONSTANTS`, **Verified** by loading the install's load order, 2026-10-05; `FUN_14066e0e0`/`FUN_14066e380` read them; sunset ≤ sunrise gives 6 and 20, as terrain.md says). `SkyClock` already implements the sun path from them.
- **Day/night** (**Observed**): `FUN_14066cb20` is "daytime" when `sunrise/24 < hour/24 < sunset/24` (strict). `FUN_14066ff60` sets the Wwise state `Time_in_Game` to `Day` or `Night` when that flips.
- **Daylight factor** (**Observed**, `FUN_14066cb50`): 0 outside daytime; `(t − sunrise)/2 h` for the first 2 game hours after sunrise (`1/12` of a day = 0.083333 in day fractions); `(sunset − t)/2 h` for the last two; 1 between. Read by the character logic tick (`FUN_1405ccd90` stores it in the character) and by `FUN_140a0af10`, which sums light contributions near a position (below 0.5 it uses 2 × factor as the base); it reads as a light-level query for a character, not decoded further. Distinct from the sun-colour daylight scale in lighting.md.
- **Absolute time helpers**: `FUN_14066cb00` = total minutes as int; `FUN_14066cc10`/`FUN_14066cc30` = total hours (double) and "hours since". About 50 functions read `DAT_1421303d0` (weather, contracts, corpses, squads, save, UI); their individual uses are not traced.
- **Units elsewhere** (**Verified** for weather by weather.md; **Observed** for the rest): weather durations and wind updates are in game minutes (`total hours × 60`), seasons in days; a corpse is removed **12 game hours** after death ("corpse decayed", `FUN_1405ccd90`; while a body is carried or attached its age is clamped to 6 hours); `starvation time` is in game hours per 100 hunger points (`fcs.def`: "number of in-game hours it takes for a character to lose 100 points of hunger", value 60 in the data; the hunger model that uses it is [character-stats.md](character-stats.md#hunger-and-starvation)).
- **Text**: the clock shows `HH:MM` (`FUN_14066d4e0`, two-digit zero-padded) and the day label uses the format `Day: {1,num}` (`FUN_14066d600`); `TimeText` / `DayText` are widgets of the main bar.
- **Wall-clock**: nothing in the loop reads the system date/time (`GetTickCount` and the performance counter only).

### Save data

**Verified** (writer `FUN_14066d7d0` and reader `FUN_14066db50` agree; the same keys in the save files: [save.md](../formats/save.md#camera-type-56-id-1051-the-world-record)): the save stores `time day`, `time hour`, `time minute` (ints), and the sky's transition state:
`sky update current time`, `sky update total time` (floats), `sky updating` (bool), `sky ambient color mult` and `…speed` (vectors),
`sky clouds density` and `…density speed` (floats). On load, negative day/hour/minute are clamped to 0; the controller's time is
`hour + minute / 60`; total hours = `day × 24 + time`; the shader's `gameTime` (water, effects) is `total hours − the value at load`, so it restarts from 0 at each load. If the sky block is absent, the transition state is reset: transition flag on, current and total transition time 0, both speeds 0, ambient multiplier (1, 1, 1) and cloud density 0.25; a block whose `sky updating` is false gets the same reset except for the ambient and density values it holds. The time of day at the start of a new game is **Unknown** (not traced).

## One GameWorld update

`FUN_140788a00(world, dt)` (`GameWorld::vfunc_0`) in order (**Observed**; unidentified callees are named by address):

1. If the AI back thread (`world +0x790`, the same object as `DAT_1421348a0`) is still running, take and release its mutex, i.e. **wait for it to finish** (`FUN_14025c6d0`, `FUN_14025f630`, `FUN_14025c410`).
2. `FUN_140785da0`: clears a UI flag (`+0x580` object, +0x40), updates a GUI singleton, the navmesh-change check ("Navmesh changed - invalidating paths", `FUN_1406624f0` on the object at `+0x4b0`), and the gates (`FUN_1402f03e0`, "A gate got lost somehow").
3. The three time values (above); the camera's view matrix is copied into a global.
4. `FUN_140788100`: **input actions** (pause, speeds, quicksave/quickload `0x0e`/`0x0f`, camera and other hotkeys); each only acts when its guard flags allow (window open, text box focused).
5. Singleton updates: the **autosave** timer (`FUN_14047be70`, real time, see Settings), the resource **loading queue** (`FUN_14044bf40`, "queued" / "preloaded" / "loaded"), others.
6. If the **physics thread** (`world +0x18`, class `PhysicsActual`) is idle: path-mode update, hand-over of physics results (virtual +0x20), the building/world object (`+0x580`), the save-file system (`FUN_1404742b0`), the mission/quest tick (`FUN_14039f7c0`, "Mission Complete"), and then **kick the physics thread with the real `dt`**. If it is still busy this whole block is skipped this frame.
7. Second helper thread (`world +0x8c0`, 0x1e0-byte object): kicked with the real `dt` when idle (**Unknown** what it does).
8. Smaller per-frame systems with the real `dt` (`FUN_140724310` on `+0x4e0`, an object at `+0x8b0` that gets the camera position, terrain/sky-related calls, the weather rain/heat-haze update `FUN_1409ea410`), and a list of **pending timed events** at `+0x608` whose countdowns run on the real `dt`.
9. **Characters**: when not paused `FUN_140786e30` (the budgeted scheduler below); when paused `FUN_140787230`.
10. **Factions/squads**: `FUN_1402e74f0(+0x4a8, dt_scaled)` (0 when paused), see below; then `FUN_140671f50`, the bird manager kick (real `dt`), one more manager update (`DAT_142134100` +0x20), the GUI percent/progress updates (`FUN_1406ea070`) and a zone-related call if `DAT_142134690 +0x38` is set.

Not found in this function: the weather schedule, zone manager and character relevance, because they run on the AI back thread (next).

## Threads

| Thread | Class (RTTI) | Kicked | Time given | Work |
|---|---|---|---|---|
| main | | | | Ogre loop, all of the above |
| "AI Rendertime Backthread" | `RenderTimeBackthread` (derived from `ThreadWannabe_HavokCompatible`), `world +0x790` | `frameStarted` (`FUN_1403beeb0(thread, dt)`) | the global `DAT_142133794` | `FUN_140787940`, below |
| physics | `PhysicsActual`, `world +0x18` | `GameWorld::vfunc_0` | real `dt` | `FUN_1407dd060`: dt = 0 if paused, capped at **0.04 s**, a virtual call on the scene object with `dt` (presumably PhysX `simulate`, **Unknown**: not checked against the PhysX DLL), a flush, then a blocking `fetchResults`-like call with an error log "Failed to fetch results" |
| helper | `world +0x8c0` | `GameWorld::vfunc_0` | real `dt` | **Unknown** |
| worker | `DAT_1421349d0` (started at start-up with the lowest priority) | its `+0x20` virtual is called every `frameEnded` | | **Unknown** role |
| navmesh zone queue | `FUN_1403af0f0` (a loop with `Sleep`, calls `FUN_1403ae490`) | message queue | | zone load/unload requests for the AI navmesh (below); the function is `NavMesh` slot 1 and registers the thread name "NavMeshMain" with the Havok thread layer before looping, so a separate thread is **Observed** (the `_beginthreadex` call that starts it was not traced) |

- **Observed** (`FUN_1403beeb0`, `FUN_1403bf100`): a back thread has a mutex-protected run flag. A kick while it runs is dropped (returns false); with threading disabled (flag at `+0x45`) the body runs inline in the caller. The AI thread's *inline* mode (`DAT_142133500`, the settings byte at `+0x70`) is always off: the settings loader hard-codes that byte to 1, so the threaded mode is the one used.
- The physics step is **not multiplied by the game speed** (**Observed**: the speed is stored in the thread's state record but `FUN_1407dd060` only reads the paused flag). So ragdolls and rigid bodies run in real time at any game speed (**Unknown** whether character movement compensates).

## The AI back thread

The thread named in the constructor `FUN_140882280` ("AI Rendertime Backthread", three `lektor<Character*>` lists). Its body is `FUN_140787940` (**Observed**), run once per frame during rendering:

1. Zone manager tick with the camera position (`FUN_140a0f2d0`): weather regions (`FUN_1408f69d0`, see weather.md), the loaded zones' per-frame work, zone unload tests, area-sector ticks.
2. The weather manager's camera update (`FUN_1409e8f70`: fog blend, rain and wind sounds) and, **when not paused**, an area-sector housekeeping step (`FUN_1408f85f0`: after 10 s of accumulated time it handles one sector per frame), the map-discovery update ("Discovered {1}", `FUN_14092f6b0`), the **faction round robin** (`FUN_1402e7590` on the faction list, see Factions and squads) and one more update (`FUN_140801540`).
3. **Character relevance** (`FUN_1405c9f60`) for every character in the world's active set (`+0x768`, count at `+0x770`) and for a second container.
4. The three lists the main thread filled in `FUN_140786e30`: list 1 (all characters that got the full update this frame) gets the **AI/body update** (Character slot 27, `FUN_1405c7c30`: brain update with the combined time, movement body update, position sync) unless the character's `+0x658` pointer is null (then it is dropped); list 2 gets slot 79, list 3 slot 80 (not decoded). The lists are cleared afterwards. What the brain update does with its time is described in [ai.md](ai.md#the-per-character-decision-loop), and the movement update in [pathfinding.md](pathfinding.md#charmovement-update).

Timing (**Observed**, from the order of the callbacks): the thread is kicked at `frameStarted` and the world update at `frameEnded` first waits for it, so the *relevance* flags computed here are read by the character scheduler of the **same** frame (after the render), while the three lists that scheduler fills are consumed by the thread run of the **next** frame (the AI/body update lags the scheduler by one frame). Slot numbers of the list calls (27, 79, 80) match the call offsets 0xd8, 0x278, 0x280.

## The character update budget

`FUN_140786e30` runs on the main thread every unpaused frame over the world's active character set (**Observed**; the three tiers are described
by rules, cursors are remembered between frames so each pass continues where the last stopped):

| Tier | Which characters | Per frame | Work |
|---|---|---|---|
| all | every character | all | `FUN_1405c7ae0`: two accumulators += `dt_scaled` |
| A | any character | **8**, round robin | Character slot 29 (`FUN_1405ccd90`, the logic tick); added to list 3 |
| B | characters with the priority flag (`+0xe4`) clear | **6**, round robin | slot 28 `FUN_1405cf130` (full update) with the *accumulated* time; `FUN_1405cfd90`; added to lists 1 and 2. The others of this class only add `dt_scaled` to their accumulator |
| C | characters with the priority flag set | all get slot 28 **every frame**; a budget of `B` of them (rotating) also get `FUN_1405cfd90`, with `B = 4` when `⌊n/4⌋ < 5` and `B = ⌊n/4⌋` otherwise (n = flagged count of the previous frame) | list 1; list 2 for the budgeted ones |

- **Slot 28** (full update): the time it uses is `dt_scaled + accumulator`, **replaced by 0.01 when it is not positive**; the accumulator is then reset. So skipped characters catch up in one bigger step. It re-syncs the character with physics (positions, rotation), animation and the zone.
- **Slot 29** (logic): flags, daylight value (`FUN_14066cb50`), "dead" handling (**corpse decays after 12 game hours**, "corpse unloaded" when its zone is gone), brain hooks.
- **Relevance** (`FUN_1405c9f60`, **Observed**): a character is "relevant" (`+0x1a9`) unless it is beyond the **`npc range`** setting from the camera/player focus object (a squared comparison; default 3250, `settings.cfg` has 3250; the loader replaces values *below* 1000 by 3000 (checked: strict comparison), the stored value is at settings +0x38 = `DAT_1421334c8`; applies to characters whose data flag at `+0x78` is clear) *or* outside the camera's view: it tests the character position with a radius of 1.5 × its body radius, the same point 18 units higher, its bounds, and finally the side planes of the view frustum with a margin. Characters that are "forced" (`FUN_1405c79c0`, or a global flag set during the zone-hopping state) get both flags set at once. A relevant character that ends the pass without the priority flag gets it set, unless two exception conditions hold (`FUN_1407916d0`, `FUN_140671c60`); so in practice **near characters in view are updated every frame**. `+0xe5` (full detail, also passed to a virtual call at the start of the full update) is set for the same forced cases and by a squad-rank comparison; its exact rule is **Unknown**. For irrelevant characters the accumulator grows by `dt_scaled` (unless paused) and the animation object is flagged. The rule behind the priority flag in the squad-related branches is **Unknown**.
- So **no character is frozen**: far characters still get slot 28 six per frame and slot 29 eight per frame, just rarely, with a longer step. The cadence depends on the frame rate and the number of characters: with N irrelevant characters at 60 fps the interval is about `N / 6` frames.

## Factions and squads

Relations, squad templates, population pools and spawning are in [factions-squads-towns.md](factions-squads-towns.md); the AI packages of platoons and the abstract unloaded behaviour (modes 2, 4, 5, 6) are in [ai.md](ai.md#unloaded-squads); stand-in speeds also in [pathfinding.md](pathfinding.md#off-screen-and-far-characters).

**Observed** (`FUN_1402e74f0`, `FUN_1406bad00`, `FUN_1406b9970`, `FUN_1406b98d0`, `FUN_1407ebd40`, RTTI `Platoon`, `ActivePlatoon`, `UnloadedPlatoon`):

- Each **faction** (the list at `world +0x4a8`) accumulates `dt_scaled` every frame (accumulator at faction `+0x264`); one faction per frame (round robin cursor `DAT_14212e314`) gets a heavier step (`FUN_1406b97c0`) that hands the accumulated value to two sub-objects of the faction (their work is **Unknown**). The accumulator is not reset there: it is reset by the back-thread step below.
- Each faction steps **one platoon per frame** on the main thread (`FUN_1406b9970`: a cursor over its squad list; a squad without an unloaded half is skipped, **and the skip uses up that frame's step**) and, on the back thread (`FUN_1406b98d0`, which also zeroes the accumulator), **one squad of its second list per frame** with `dt` = (list length × accumulated time), i.e. the time slice is scaled back up so each squad still sees the real elapsed time. On the back thread **every faction** gets this step each frame (`FUN_1402e7590` walks from its stored index to the end of the list because the per-faction function always returns false, then wraps; **Observed** from that return value, which the decompile masks to zero); only the heavier faction-level step in the bullet above is one faction per frame.
- A `Platoon` (squad object, `Platoon` slot 26 = `FUN_1407ebd40`) is either **active** (`+0x1d8`, an `ActivePlatoon`: its characters exist in the world) or **unloaded** (`+0x1e0`, an `UnloadedPlatoon`): the update goes to whichever exists and copies its position.
- **UnloadedPlatoon update** (`FUN_1407ee790`): has its **own timer** (microseconds, like the frame timer), multiplies its elapsed time by `speed` and returns immediately while paused. Squads of the player's faction (a field at `+0x250` of the faction object) are not simulated this way. It moves a stand-in for the leader (a small object that takes the scaled time and a target position) with no physics; the movement speed value stored on the stand-in is 25, 55, 60 or 90 depending on a small class value `c` returned by a helper object (default 4) and the squad's mode `m` (`c` = 1 gives 55, `c` = 2 gives 90; otherwise for `m` = 6 it is 60, or 90 when the straight distance exceeds 25000, a plain distance, not squared; every other case 25; units not verified). The code compares squared distances with 500² (250000, mode 2) and 2000² (4e6, mode 4) and plain distances with 9216 and 25000 as thresholds. A squad that has waited for more than 60 s of its own time returns "false" (finished). The modes (travel to a point, go to a target, follow) and the thresholds' roles are **Unknown** in detail.
- **ActivePlatoon stays active** while its position is inside a **loaded zone**, with a margin of **170** units around the zone rectangle; otherwise a countdown starts and the squad is unloaded when it ends (`FUN_1404febd0`, **Observed**): the timer is held at 4 s while the position's zone is loaded and inside the margin or while one of the members passes an unidentified test, set to 5 s when the position has no zone object at all, and runs down by the scaled `dt` otherwise. Members: when a squad's leader is outside its zone (`FUN_1407c06f0`), each member that is **within 600 units** (squared distance < 360000) of the leader and not relevant is released with the reason "leader wandered out of zone", and a member that is itself outside its zone is released with "wandered out of zone" (`FUN_14079c600`, **Observed**).
- Squads per faction and sizes are capped by CONSTANTS: `max squads` 10, `max squad size` 20, `max faction size` 30 (probe; the code that enforces them is **Unknown**, [factions-squads-towns.md](factions-squads-towns.md#9-settings-and-constants-that-matter); the Squads tab does show "Maximum number of squads reached.", [ui-screens.md](ui-screens.md#5-overview-window)).

## Zones

**Observed** unless noted (ZoneManager `DAT_1421349c0`; zone grid 64 × 64 of 4608 units, [terrain.md](../formats/terrain.md)):

- The manager holds 64 × 64 zone objects (0x168 bytes each) and a set of **loaded/active** zones (`FUN_140a08ae0` returns the object for indices below 64).
- **What activates zones**: a budgeted character whose faction object has its field at `+0x250` set (the player's, `FUN_1405cfd90`; the same field the squad update uses to recognise the player's faction) calls `FUN_140a0f640` with its position: every zone overlapping the **box of ±340 units** around it is activated (`FUN_140a0f080`), plus a ring of neighbours whose width is the **`Fast zone hopping`** setting (0 or 1): off activates only those zones; on adds the 3 × 3 ring (the setting's tooltip: "keeps a larger area in memory"). **Verified** by the tooltip text plus the ring parameter being that setting (the settings loader stores `Fast zone hopping` at settings +0x88, and `FUN_140a0f640` passes that byte as the ring width to `FUN_140a0f080`, which loops from −ring to +ring in both zone indices); the "player's faction" condition is **Observed**.
- **Unloading**: the per-zone test `FUN_140a0b830` runs **three countdown timers on the real frame time** and unloads the zone only when all have run out and the manager's state value (`+0x1681d8`) is below 2. The timer lengths and what sets them are **Unknown**.
- Zone/navmesh work is **queued**: messages (0 = load, 1 = unload sector, 2, 3/4 = other) are processed by `FUN_1403ae490` ("Deferred load zone", "Unload sector", "Delaying unload zone", "Stuck loading zone"), in a worker thread loop. A zone can be loaded later than requested ("Deferred load"). The navmesh side of this queue (messages, Havok instances, the 3 x 3 zone ring in `Havok.log`) is in [pathfinding.md](pathfinding.md#manager-thread).
- **Area sectors** (`FUN_1408f9240`): population/spawn upkeep per area island runs **every 2 s of scaled time** (accumulated `dt_scaled`) and is skipped while two UI-state checks (`FUN_1406e2100`, `FUN_1406e20a0`) are true (**Observed**: `FUN_1406e2100` is the visibility test of the loading window, [ui-input.md](ui-input.md#6-event-dispatch), so the spawn step waits while the loading screen shows; `FUN_1406e20a0` is not decoded; the spawn rules are in [factions-squads-towns.md](factions-squads-towns.md#62-area-sectors-homeless--roaming-squads-and-the-biome-population)); per island it sums a population weight of its sectors against a target (`FUN_1408f5ca0`) and spawns or culls (**Unknown** in detail).
- **Draw/load ranges** are in the settings: `view distance` 5000, `npc range` 3250, `objects view range` 3000, `feature range` 2 (zones around the camera map features load for), `distant town range` 6, `terrain hi-res distance` 400. See [formats/zones.md](../formats/zones.md#draw-distance-and-distant-towns). The exact radius for *real* objects around a zone stays **Unknown** there and here.

## Settings and constants

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

Consumers of these values: [buildings-production.md](buildings-production.md#global-constants-used-here) (build, production, research, dismantle), [character-stats.md](character-stats.md#constants-used-by-this-subsystem) (hunger, blood, healing, XP, carrying), [combat.md](combat.md#global-tuning-the-constants-record) (damage, block, KO), [economy.md](economy.md#3-constants-price-block) (prices, loot fractions) and [factions-squads-towns.md](factions-squads-towns.md#9-settings-and-constants-that-matter) (squad caps). The two difficulty-dialog options that scale them at run time (Hunger time, Chance of death, damage, building, research and production speed, nest count) are listed in [character-stats.md](character-stats.md#new-game-advanced-options-that-change-these-mechanics).

`FUN_14086b2b0` copies these into a runtime struct (**Observed**; the struct slots were not mapped to their consumers). Some are rescaled on load (the full list of scalings is in [character-stats.md](character-stats.md#constants-used-by-this-subsystem)): `bleed rate`, `bleeding clot rate` and `blood recovery rate` ×0.1, `min dismantle materials percentage` ×0.01 and `exp gain multiplier` ×0.25. There is no CONSTANTS field for the frame time, speeds, update budgets or ranges: those are literals or settings.

Settings that touch the loop (`settings.cfg`, loader `FUN_1403e84f0`): `npc range`, `Fast zone hopping`, `Auto save` (1) and `Auto save time (minutes)` (10, **Observed**: the autosave timer accumulates the real frame time while the game is not paused and fires when it exceeds `minutes × 60` seconds; it waits while a zone transition is in progress or a modal state is active; slot rotation and the saved files: [save.md](../formats/save.md#autosave-and-quicksave)), `Squad size multiplier`, `Global population multiplier`, `view distance`, `objects view range`, `feature range`, `distant town range`. `kenshi.cfg` has `VSync=No`, `VSync Interval=1`.

## Differences and extensions to existing docs

- weather.md "Time bases": correct; extended here with the `0.01` caveat (only for speed 0), the one-frame offset of the globals, and the exact paused-flag semantics.
- terrain.md/sky.md: the clock advances by `dt_real × speed × 11/1200` hours; no contradiction. The game's day/night "daylight factor" (2-hour ramps) is new.
- buildings-production.md: the building formulas divide by the day length G = 2618.18 s (86400 / 33), which is this doc's 109.09 s per game hour x 24; the same figure, so no contradiction. character-stats.md: the medical `dt` is game hours taken from the clock difference (`total game hours` at `+0xa0`), not the scaled frame time of the XP functions (`DAT_142133794`, the third time value above).

## Unknowns

- The start-of-new-game time of day. The saves in the install are empty; the sample save of [../formats/save.md](../formats/save.md#camera-type-56-id-1051-the-world-record) (a new game, saved at in-game day 1, 13:39) holds the `time day` / `time hour` / `time minute` and sky keys described above (**Observed** there), but the starting time itself is not traced.
- Whether the float timestamp in the frame timer is audible after hours of play.
- The exact priority rule behind `+0xe4` (which squads) and the distance metric of `npc range` (2D or 3D; it is a squared distance from a camera/player focus object).
- What the heavier per-faction step `FUN_1406b97c0` does with the accumulated time, the other speed-scaled timers (`FUN_140792500`, `FUN_1409a7930`, `FUN_140857500`), and which function starts a loaded save at speed 1 (only the new-game routine `FUN_1405f5d10` was seen to set it).
- Character slots 79 and 80, the second helper thread (`world +0x8c0`), the pending-event list at `+0x608`, `FUN_140671f50`, `FUN_1407869e0` and several smaller steps of `FUN_140788a00`.
- Timer lengths of the zone unload test and the margins of the `UnloadedPlatoon` modes; whether physics substeps or fixed-step settings are used (the PhysX scene desc has `timeStepMethod`/`maxTimestep` fields; where the game sets them was not found).
- Whether character movement compensates for the non-scaled physics step at 2x and 5x.
- How a loaded squad becomes unloaded in detail (activation of an `UnloadedPlatoon` by player proximity).
- Which parts of the world use hours vs days for their own timers (about 50 readers of the clock).

## Implementation outline

1. **Frame clock** (new, small): measure real dt, cap 0.05 s, smooth with the 20-sample 0.5-decay average, derive `dt_scaled = dt × speed` and the 0.01 floor; expose the three values. Keep physics (if any) on its own real-time cap (0.04).
2. **GameClock** (new, next to `SkyClock` in `Meitou.Data.World`): `day`, `time of day`, total hours; advance by `dt × speed × 11/1200` hours per frame; `Phase(hour)`/`SunDirection` already exist in `SkyClock`; add `IsDaytime`, `DaylightFactor` (2-hour ramps), the Day/Night event, `HH:MM` and `Day n` formatting, 100 days per year from CONSTANTS. Save/load: day, hour, minute.
3. **Speed and pause**: speed ∈ {0, 1, 2, 5} (start the world at 1 at game start, the original is 0 until the start routine runs), last non-zero speed for un-pause, a separate paused flag for menus; wire `pause`, `speed_1..3`.
4. **Character scheduler**: relevance (range from `npc range`, view test), the three tiers (8 / 6 / flagged-all with budget `max(4, n/4)`), accumulated time with the 0.01 floor, relevance computed before the scheduler in the same frame, the AI/body lists consumed one frame later. A deterministic single-thread version first; the AI back thread is an optimisation (the original joins it at the start of the world update).
5. **Squads**: active vs unloaded halves, the abstract mover with its own timer, the 4–5 s grace outside loaded zones, the one-squad-per-faction-per-frame slicing with scaled dt.
6. **Zones**: ±340 box activation (ring 1 with the setting), deferred queue, unload timers; use `Meitou.Data` zone readers (`LevelFile`, zone grid) and the viewer's streaming as the base.
7. **Constants/settings**: read the listed CONSTANTS and settings through `GameDatabase`/`GameInstall`; the remaining rates (`build speed` and others) belong to the systems that use them (research, crafting, medical).
8. **Order inside a frame**: poll input; advance clock (with the previous frame's real dt); (optionally kick the AI pass); render; measure dt; world update (input actions → autosave → physics hand-over → characters → factions); floating origin rebase.
