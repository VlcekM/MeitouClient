# Engine architecture

Projects: Core installation, settings, localisation (shared with the launcher); Data clean-room readers, typed game data and pure rules;
Simulation the headless world (systems, `Navigation/` own navmesh, saving, `Session/` tick clocks, `WorldBoot`), which never references Game;
Game the executable `MeitouClient` (`Host/` WorldSession/input/camera, `Rendering/` Vulkan/content/streaming with `Display/` and `Metal/`,
`Audio/`, `Ui/`, `Mods/` the code-mod loader); ModelViewer debug world/atlas; Tools surveys and mod-check. Folders are feature slices and
namespaces follow them. GPU and simulation ownership stay separate. [Simulation contracts](simulation.md),
[native renderer](renderer-native.md), [viewer controls](viewer.md).

## Loop, input and camera

WorldSession owns world, clocks, input/bindings, camera and last two snapshots. Control30Hz real
(default config tickRate), max10 ticks/frame; simulation1/30 game second, speed0/1/2/5 and
2*speed budget. Control precedes simulation; interpolation uses each clock's alpha, shortest
angle rotation and cuts on mode changes. The authoritative time/order/determinism details are
in simulation. Host copies camera state into WorldCamera and snapshots into CharacterDrawList (every frame for characters in reach; those beyond it in `Distant`, refilled only when the snapshot changes; emptied when the title takes over the list). The next world tick runs on its own
thread during the world draw ([simulation](simulation.md#tick-ahead)).

KenshiCamera follows pivot/boom/zoom/pitch/ground constraints from [camera](formats/camera.md).
Engine choices: pivot500*clamp(boom/600,0.1,1), keys1.5rad/s, mouse0.005rad/pixel; terrain-ground
pivot rather than building support. Free-camera ; follows smoothed flight, exits at ground hit.
InputState consumes events once per tick; commands apply at world boundaries, not UI callbacks.
meitou.user.json persists fps/vsync/fullscreen/tickRate/simThreads/graphics/bindings (layout and versioning: [launcher](launcher.md#graphics)); renderer
settings and current key list belong in viewer. Quicksave F5/loadF9; cinematic F6/hideUI F7.

## Host and loading

`WorldBoot` reads a world's content on a worker with no window (navmesh, towns' buildings, population data, ragdoll rigs, animations);
GameHost adds the code mods and the systems. GameHost partials keep boot, the Metal loop, loading, transitions and the character feed; each other feature is a class the host holds and makes in
`CreateFeatures`: `GameLoop` (the Vulkan and headless run), `GameSound`, `GameSaves`, `TitleScreen`, the scenarios, `Host/WorldSync`, `Effects`, `DevTools`, `Editor`;
PlayerInterface keeps the input entry points, selection, commands and shared UI helpers (core and `PlayerInterface.Ui.cs`); its windows, HUD panels, picking and pointer are classes it makes on first use (`Ui`, `Building`, `Title`, `Editor`). StandardSystems supplies order. NavAdapter
bridges Navigation; never let UI/rendering duplicate simulation state.
GUI executable attaches a caller's console; folder prompt creates its own when necessary.
Native splash precedes Vulkan (Windows only); worker loading and render-thread GPU steps keep the window
responsive. Both backends draw the start's loading frames through `ILoadingCanvas` (`GameHost.LoadingFrame`): Vulkan's swapchain, or
Metal's context frame until its boot ends (docs/metal-backend.md).
On Wayland the window is shown and its first configure taken before the first loading frame is presented: a buffer attached before the shell role makes the compositor drop the client (**Verified** natively on KDE Wayland, GLFW 3.4). Full screen at start shows the window before going full screen there: GLFW ignores showing a full-screen window, and on Wayland (unlike X11) going full screen while hidden never maps it (**Verified** KDE Plasma, GLFW 3.4, PR #77). Scene transitions draw loading between stages, wait on asynchronous results, and
preserve world on failure. Gameplay warm-up waits for nearby population, terrain, scenery, character
assets and used textures (including deferred loads), then <40ms frames for0.5s (max5s for frame pacing). Once the content was ready, a frame
with a few things pending again (a part resolved the next frame, the title camera's drift) pauses that run instead of restarting it; a long frame still
restarts it (`GameHost.CoveredFrame`). **Observed** (gut, 655 mods): restarts held the menu about 1 s and a save 0.6 s longer.
After 5s of continuous `VramGuard` pressure, content it holds back is no longer waited for (it cannot load until the use falls).
Device-local memory: `VramGuard` holds back 384 MB (at most an eighth; a sixteenth under a 3 GB budget, i.e. cards under 4 GB) of the driver's budget and works on the rest; where offered, `VK_EXT_memory_priority` and `VK_EXT_pageable_device_local_memory` let a full heap page streamed textures (priority 0.3, the rest 0.7) to system memory instead of failing. **Observed** by the reporter of #745 (GTX 1650 4 GB, driver 610.57.04, 795 mods): 11-90 MB allocations failed at 92% of the reported budget with 300-500 MB free; the reserve ended them, and paging held 6 GB on the 4 GB heap in a test program. Paging is a fallback only: with the guard's ceiling over 100% the driver paged constantly and the frame rate collapsed. Guard contract: pressure starts at 90% of the use or on a refused allocation and ends under 80%; under it the ranges fall 12% every 0.75 s down to x0.15, but a step waits while the use less the room free in our own blocks (held use) fell by 1% of the budget since the last step or is under 80% (evictions lag a cut by seconds and leave holes, so the driver's figure falls late), and the steps stop after 4 in a row that left the held use within 1% of where they began, until it rises 5% over that. **Observed** (#1429's save, 1080p, `MEITOU_VRAM_BUDGET_MB=4000`, 150 s headless): before, the ranges fell to x0.15 within 10 s and stayed there while the held use went from 85% to 82% (terrain layer slots 682 MB, biome textures 407 MB and the grass store 320 MB do not follow the ranges); with the stop they held at x0.53 at 85%; the ranges then climb 4% a second while the use less the room free in our own blocks (holes left by evictions, empty blocks, spares: `GpuAllocator.Slack`) has stayed under 80% for 4 s, up to a step below the scale the pressure began at, and to 1 once 90 s have passed since it ended. While the ranges are clamped the texture caches' mark and the impostor limit are three quarters, and the grass store, the terrain layer slots and the material cache do not grow; a refused enlargement of those (`VramGuard.AllowsGrowth`) does not enter pressure. **Observed** (#1390's save, still camera, 4K, Maximum textures, `MEITOU_VRAM_BUDGET_MB`, 5 minute runs): before, a pressure entry left the ranges clamped as long as watched (x0.53 to x0.60 at 6300 MB: the caches refilled to 79-81% against a 74% recovery mark, a 160 to 320 MB grass growth took the room, and 380 to 670 MB of holes counted as use); with these, a camera jump's pressure at 6300 MB went back to x0.88 in 30 s and x1 at 100 s, and stayed; at 6000 MB, where the full ranges only just do not fit, the ranges stayed between x0.88 and x1 with 2 entries in 5 minutes (before: x0.60, then x0.28). At 3500 MB (1080p) both stay under pressure at x0.15. A game's warm-up waits only for the characters within 3000 units of the camera's focus (`CharacterRenderer.LoadingFocus`, `LoadingReach`); the rest are prepared once
the screen lifts. The original's loaded zones (the camera's 3 x 3 block) can hold hundreds of NPCs, whose meshes' generated levels are built once and then read from a disk
cache ([character-renderer](character-renderer.md#content-lod-and-memory)). **Observed** (a Great Desert save near Heng, RTX 4090): 479 looks pending and a 130 s wait before this rule, 35 and 40 s with it.
The warm-up (title and game) waits for grass only within the range of the default grass distance (`FoliageRenderer.LoadingPending`, `DefaultGrassRange`);
pages a longer setting reaches grow in, nearest first, after the screen lifts. **Observed** (save siwa-test4, the Ultra tier's grass distance x 2): the title waited
107 s and the game about as long for 16 000 and 27 000 grass pages made at about 400 a second, everything else ready.
The objects count the same way (`WorldObjectRenderer.LoadingPending`): the remakes that coarsen a resident mesh are housekeeping and not waited for. **Observed** (save gut, 655 mods, RTX 4090, 2026-11-12): with the camera at rest two such remakes at a time kept the warm-up waiting 20+ s after everything else was ready.
Phase timing of a start or a load: `MEITOU_LOG_TIMES=1` stamps every log line with the seconds since the process started; the warm-up report prints the GC counts, pause total and CPU seconds so far, the "still preparing" line the streaming queue and the VRAM guard, and each scene-load stage prints when it starts. **Observed** (gut, hidden 2560x1440, 160 fps cap, warm caches, 2026-11-17): menu shown at 17.5-18 s (was 22.4), the warm-up 7 s; city-night loaded from the Load window: click to view ready 13-13.5 s (was 26.5): read 1.3 s, the sky's climb to 5.7 s, build 1.6 s, finishing 0.7 s, warm-up 5 s. Another GPU load (a second game instance) stretches the warm-up to 20-40 s: the render thread then waits in the frame fence.
The game data's mod files (`GameDatabase.Load`) and the world's level files (`WorldLevelData.ApplyLayers`) are read and decoded on workers, up to twice the core count
ahead, and merged one at a time in load order: the result is the sequential merge's (**Verified**: full record, field, reference and instance dumps of 655 mods
identical before and after). The title's character comes from the save Continue loads, read on a worker from the start of the boot (`TitleScreen.PrefetchTitleSave`).
A save capture's base (a copy of the loaded save, `SaveCapture.PrepareBase`) is made once the loading screen is gone: a large save's copy is seconds of a worker.
Under the cover the streams' frames need not be smooth: the character, foliage and object renderers (`Covered`, set by the host each frame) upload and build for up
to 16 ms a frame instead of their play budgets, foliage layouts are queued four per streaming thread (`BackgroundWork.Threads`) and grass pages sixteen (finished ones
are taken once a frame, so this bounds the pages a second: at four, 11 000 near pages took 6.5 s with the threads two thirds idle), object meshes decode on every
streaming thread (four in play), and objects take eight times the zones and 64 times the resolves a frame (the first covered frames take up to a second each). Once the warm-up's content is ready the streams are back at their play budgets, so
its run of smooth frames measures play. **Observed** (siwa-test, Ultra settings, 662 mods, RTX 4090, 9800X3D, warm disk caches): the game's warm-up went from 20.8
to about 6.5 s with this, the grass picking, the coarsening rule and the character LOD cache.
Population builds are queued/applied at frame boundaries without advancing game time, AI or movement;
navmesh-gated spawns are retried while loading. Warm-up and the flight hold the simulation paused.
Which title shows is the Interface setting Main menu style (`MeitouMainMenu` in `meitou.user.json`, default Meitou), independent of the UI mode; the main-menu sky flight needs the Meitou title.
The Faithful title does not wait: its picture is opaque, so the start ends with the splash ([ui-screens](game/ui-screens.md#splash)) and the boot.
While it is up the world is not drawn (`GameHost.TitleHidesWorld`: a black backbuffer, no frame generation; Vulkan host), so its streaming waits for a game's warm-up.
The Meitou title waits for terrain, scenery, character assets and used textures (including deferred loads), then
one full second of <40ms frames (max5s of ready content for frame pacing, so slow GPUs reach the menu). Its menu artwork/fonts/pipelines warm under the opaque cover too;
streaming uploads retire and a compacting GC completes before that final smooth run.
**GC:** the stock runtime (server GC, four heaps, gen0 budget at most 16 MB: `System.GC.Gen0MaxBudget`, GC threads not pinned: `System.GC.NoAffinitize`) in builds and release zips alike; the startup `gc` line names the collector (`GcName`).
**Observed** (2026-10-24, play benchmark, 5 min, ~160 MB/s allocated, RTX 4090): the default budget let ~1 GB build up between collections, 27 gen0/gen1 collections paused
20-190 ms (median ~26); at 16 MB, 330 collections paused 2.5 ms median and 31 ms at most, total pause about the same. 64 MB: max 122 ms; workstation concurrent GC: max 25 ms
but 3.6 s of pauses in all; DATAS (`DynamicAdaptationMode=1`): max 66 ms. `System.GC.Gen0MaxBudget` is read from runtimeconfig (**Verified**: collection counts with
and without it); `GCgen0MaxBudget` there is ignored and `DOTNET_GCgen0size` changed nothing. The
experimental Satori GC (github.com/VSadov/Satori, `PublishWithSatoriGC` SDK) is not used in official builds. **Observed** earlier A/B (RTX 4070, 10.0.13
Satori vs stock 10.0.12 fast flight): longest GC pause 15-21 ms stock vs 1-18 ms Satori, frames unchanged (long frames are foliage updates, docs/viewer.md).
**Observed** (2026-11-01, headless simulation probe of a ~1600-character town save, Ryzen 5600X, 60 s runs): `HeapCount` pins the four GC threads to CPUs 0-3, and about 1 in 40 ephemeral collections paused 100-290 ms while doing 1.5-3 ms of GC work: the time is between the suspension finishing and the collection starting (GC events `SuspendEEStop` to `GCStart`). Pinned: 6 such pauses in 5 runs; `NoAffinitize`: none in 5 (max 2.6 ms); workstation GC: none in 3. The game was not measured.
`GCConserveMemory` is not set. **Observed** A/B (`DOTNET_GCConserveMemory=5`, 3600-frame fly benchmark, 3 clean runs each): managed heap at the end
1.42 vs 1.93 GB, working set 3.37 vs 3.93 GB, but gen2 GCs 12-14 vs 7-8 and pause totals 54-306 vs 36-53 ms (one run 64 frames over 33 ms).
Background GC is on (its default; `System.GC.Concurrent` is not set): background gen2s (reason `AllocLarge`) pause 1-2 ms; a blocking, compacting
gen2 (`NonConcurrentGC`) pauses 0.7-1.4 s over a 6-7 GB heap. **Observed** (2026-11-19, Ryzen 5600X, 32 GB at 78-88 % memory load, every process
counted; play benchmark from an empty navmesh cache with DotRecast's own tile heightfields, [simulation.md](simulation.md#navmesh)): with
`System.GC.HighMemoryPercent` (default 90, not set) at 80, under that load, 27 of 28 gen2s were blocking (reason `AllocSmall`; 22.4 s of pauses,
34 frames over 500 ms; the GC's memory pressure read 80-84 at each), at the default 2 of 5 (at 88-89 % load); with the pooled heightfields 1 of 6
and 0 of 10. The heap count and `ConserveMemory` stay as they are: **Observed** (same benchmark, pooled heightfields, one run each) 8 heaps halve
the gen0 collections (330 for 699) for 2.2 s of pauses against 2.9 s, with longer single ones (gen0 21 ms for 14, gen1 52 for 42) and frame times
within the runs' spread; `ConserveMemory` 5 changed nothing (2.9 s of pauses, no blocking gen2 either way).
Process memory beyond the GC heap is mostly untouched write-combined commit (~6 GB at 6.7 GB of VRAM in use), not resident RAM.
**Pooled buffers** (allocation is the GC lever): streaming garbage is mostly LOH arrays from worker jobs. **Observed** in flights: gen0/gen1 pauses under
~40 ms; single blocking gen2s (`NonConcurrentGC`, reason `AllocSmall`) of 0.3-1.7 s in some runs despite SustainedLowLatency, with or without pooling. Pooled now:
DDS file bytes of `WorldTextureCache.Load` (`DdsReader.ReadKept(..., pool)`, `BoundedBytePool.Streaming`; returned in `Load` after an RGBA8 decode or
by the last upload step of a stored BC upload), the GI terrain grid's samples (one array per `TerrainGrid`), the card trimmer's clip/hull scratch
(`FoliageCardTrimmer.Hull`) and the grass generator (`[ThreadStatic]` `FoliageRandom`, reseeded). Contract: a rented array may be longer than asked,
so its length is never the logical size (pass counts; `DdsFile.TrailingBytes` is meaningless on a pooled file); return it in a `finally` or at the
one point after its last reader, never while anything keeps it; a buffer that is dropped instead (a texture never pumped) just goes to the GC.
These, the terrain levels (`TerrainTextures.Mips`, `Cut`, `CutBlocks`) and the impostor reader's scratch rent from `BoundedBytePool.Streaming`:
power-of-two arrays (a 4.2 MB file rents 8 MB), at most 256 MB idle; a return over that goes to the GC. `ArrayPool<byte>.Shared` held them per core
and kept them: **Observed** (#1352, heap dumps, quicksave, vanilla, RTX 4070) 581 MB idle in it after warm-up and 673 MB after 30 min of play.
**Observed** A/B (same save, headless, then five `tp` hops to towns 40 s apart at speed 5): RSS after warm-up 5.7-5.8 to 4.7 GB, after the hops
7.4 to 5.5 GB; warm-up GC counts and pauses about the same (70-71/31-32/11-12, 1.0 s vs 73/35/13, 1.4 s).
**Observed** (swamp, 1920x1080, 3000-frame flight, 4 alternating pairs, 2026-10-21): allocated 6.2 GB to 4.8 GB (2.1 to 1.6 MB a frame), gen0+gen1
45 to 35 in total, gen2 count unchanged; frame percentiles unchanged within noise.
Not pooled, kept by their owners: `HeightWindow` arrays (read through `HeightSnapshot` on workers), grass blade arrays (kept as `PendingBlades`),
particle texture decodes (preloads not drawn within 30 s are dropped). How to rank allocation sites: docs/bench.md.
**Native buffers** (`Meitou.Data.NativeBuffer`, a `MemoryManager<byte>` over `NativeMemory`): outside the GC heap, freed at once on `Dispose`, so a
transient buffer leaves no LOH garbage or holes. No finalizer (CA2015: it could free a block a `Span` still reads), so an undisposed block leaks;
`NativeBuffer.LiveBytes` (in the F11 `ram` total, the flight's `gc` line) shows what is held. Used for impostor atlases read from the cache: `ImpostorAtlas.Read`
inflates every kept level into one block, the levels are `ReadOnlyMemory<byte>` slices of it (baked atlases stay managed arrays; disposing them does
nothing). The atlas's owner disposes it where it lets go: `FoliageRenderer` after the upload completes, on a refused upload, an abandoned or
finished refine, a mismatched load, and `UnloadImpostor`/teardown (after the `Next` textures that read it). `ImpostorTextures` never frees.
**Observed** (swamp, 1920x1080, 3000-frame flight, 2026-10-21): traced large-object allocations after loading 6.0 to 1.8 GB (`ImpostorAtlas.Read`
4.2 GB to 85 MB); 4 alternating pairs (both orders): allocated 4.9-5.0 to 4.0-4.1 GB, peak working set 5.4-6.4 to 4.9-5.8 GB (set while loading),
0 MB native held at the end, gen2 counts 3-4 vs 3, p99 unchanged. Cancel on window close. Helpers/services dispose/join with their owning scene/session.
After warm-up, loading panels ease off the left/right edges over 0.7s while the loading image fades to the live scene and UI (Faithful: hidden at once, as the original).
Input remains blocked through this reveal. A new game ends warm-up as soon as the squad's characters are drawable, skips the sky arrival and opens the
character editor first (`CharacterEditorHost`); once it closes, the loading screen returns and the world warms up as above. Sky loads dismiss only panels that were shown, keeping the sky flight visible.

Meitou UI mode presents title controls over a live view of the real world; Faithful retains
the original title image and loading screen. Advanced settings persist two independent scene-transition
toggles in `meitou.user.json`, both on by default: `mainMenuTransitions` covers entering a new game/save
from the main menu and returning to it; `inGameTransitions` covers save loads/new games during play.
Each has a persisted speed slider (`mainMenuTransitionSpeed` / `inGameTransitionSpeed`, 0.25–4x, default 1.5x).
The reading worker hands over the save's CAMERA `pos` too; until the load ends the streaming loads round it as a flight's landing and a kept point
(`GameHost.LoadAhead`, `WorldGpu.Ahead` and `Kept`), so the destination's terrain, objects and grass load during the climb. **Observed** (city-night, from the
title): the warm-up after the swap went from 6.7 to 5 s.
Disabling either uses the regular loading screen and warm-up. Enabled scene loads in Meitou use `SkyArrival`, the load starting at once:
the hour runs forward to the destination's (title: its morning; save: CAMERA `time hour`/`minute`, handed over by
the reading worker as soon as `quick.save` is parsed), 0.8 s + 1 s per 6 h, accelerating over its first half and braking over the second;
a 0.5 s wall-clock hold; a 3.4 s ascent about
the fixed eye to the sky. In play, and from the title when the run is at most 3 h (known once the save's hour is), the view climbs at once, without
the hold, the hour running forward meanwhile. The sky is at the destination hour (a day save shows day sky, not stars); a wait there for the load; a 0.3 s fade to black once its stages are done, under
which the destination warms up from the landing view; then a 3 s descent that fades in from black over its first 1.4 s, starting 300 units above the view and
orbiting 1.2 rad round its target onto the view's heading. All but the hold are divided by the selected speed. Ascent ramps speed over its
first/last 40%; descent ramps up over 30% and brakes over 20%. Integrated smoothstep velocity brings
angular velocity and acceleration to zero at both ends of each flight. Save reads, the new world's systems and new-game
system preparation overlap the flight; world replacement waits until terrain is outside
the view. A new game climbs at once at the current hour: no sitting down, hold or hour run.
Escape/Space skips the flight, never pending load work. Progress appears in the sky after 2s.
Camera/hour overrides belong to presentation; simulation does not advance during the flight,
and weather scheduling uses the actual game hour. Sky-only frames avoid world callbacks while
loading; warm-up renders the destination before descent. Errors drop the flight and restore UI.
`TitleComposition` shows the real world at a fixed spot near Okran's Shield (Okran's Pride),
given as a viewer camera code (`TitleComposition.Spot`, 50° lens), just after sunrise (`Sunrise + 0.75` h,
the sun peeking over the hill right of the valley; clear weather, cloud coverage 0.38). The host's renderers stream round that camera as
round any other; booting to the title starts the world there. Everything below is presentation only, set
while the title draws or shows and cleared when it closes; simulation and saves never see it:
- `SkyClock.SunTurn` turns the sun path about the vertical (height unchanged) to ~26° right of the heading;
  stars use the unturned path so their orientation stays continuous into the loading sky.
- The sky renderer's `PlanetFill` (0.8; 0 in play) lights the moons from the viewer's side so their whole face reads,
  and `PlanetTurn` (identity in play) moves them ~1.6° up and ~2.4° left in the view.
  Moon and character fill fade smoothly with sun height from the title's morning level to zero at the horizon.
- The clouds drift right to left across the view at 125 units/s (`CloudWind`), faster than the game's wind.
- Menu dust uses a separate particle renderer with the installed `kenshi_weather_dust1` system at 0.3 density,
  warm tint and 24s wind gusts; it runs while paused and fades out as the camera tilts to the stars.
- The shafts' air layer is raised to 2x and their strength to 1, so the hill the sun peeks over casts
  visible rays through the haze; `FogVolumes.HidePlaced` leaves out the placed `fogfeatures.dat` volumes
  (one stood as a dark dome where the sun is). `PostOptions.Balance` (1.1, 0.98, 0.88; 1 in play), a white balance
  before the tone map, keeps the orange of a lower sun.
- `WorldObjectRenderer.Staged` adds the foreground: a cliff top for the wanderer, the flat-topped hoodoo `FLatTop_Pillar01`
  (drawn through its FOLIAGE_MESH record `FlatTop_Hoodoo01`, terrain texturing), its top ~45 units ahead at the bottom of the
  frame and the block moved 125 units left, so its sheer right side drops to the valley just right of him; on that top, a
  little scree and a stone scaled to pebbles, desert shrubs on its left and a tiny one at its right edge (FOLIAGE_MESH records
  drawn as staged meshes), a few more on the far hill; and the
  buildable `Watchtower` building on the valley floor ~2600 units ahead at the far right, yaw 145° from the view, laid out as a placed
  building (setting or clearing it lays their zones out again). The object streamer reads a staged FOLIAGE_MESH's TERRAIN
  texturing from `material type` (a map feature's from `texture mode`); without it the mesh loses its level indices and
  draws white (**Observed** on this title).
- `FoliageRenderer.Shift` moves the ghost-town ruin (FOLIAGE_MESH houses and scrap piles) ~400 units left,
  applied as foliage zones are accepted.
- One wanderer (sandbox character in drifter's gear, with a stronger build: `CharacterAppearance.Shape`, null in play, sets 0.6
  muscle and broader shoulders, frame, chest, arms and neck) stands on the plateau's top where the ray through his screen point
  (x 0.337, y 0.93) meets it, close to the eye, lit by a warm fill (`CharacterRenderer.Fill`, 0 in play: added ambient,
  three times as strong at the silhouette) so his back, turned to the sun, is not a flat silhouette, ~300 px tall at 900 p, his back to the camera looking
  10° left of the heading, in a frozen `idle_stand_confident` pose. The camera's `MaxNear` is lowered to 20 for
  the title (the near plane is a quarter of the eye's ~350-unit clearance, ~87, which clipped him).
- Three Paladin–Dust Bandit pairs fight on the foreground road in `TitleBattle`'s separate world, using the regular combat and
  animation systems with zero damage and no medical tick. Real time advances them while gameplay is paused; their pair centres
  stay at the authored road points despite animation root motion. Bodies and pair spacing render at 2.5× scale for visibility,
  grounded on the road; combat keeps its normal dimensions. Their poses update every frame, exempt from distance throttling.
  They start three seconds into combat. The world has no fight footwork (pursuit/movement), so `TitleSpacing` holds each pair
  at least both radii + 1 apart at the end of every tick; without it they overlapped and face-the-target turned them round each
  tick. Easter egg (`TitleTornado`, display only, off for `--screenshot`): one landed melee blow in 500, every blow on 1 April and
  25 October, throws its victim 210 up (fast rise, bobbing hover, falling back), spinning six turns over 4.5 s, inside a funnel of
  24 `kenshi_twister_dust1` bursts stacked to 330 (size 0.04 at the foot to 1.8 at the top, growing with the cube of the height; 0.6 of
  their particles' life, so each ring stays near its height).
The title menu is the [Meitou UI kit](meitou-ui.md)'s main menu (`MeitouTitleMenu`): its stack, save card and plate buttons at the kit's
1600×900 sizes scaled with the height, over the kit's vignette. The card (paper, edge, rounded corners, drop shadow), the plates (Button1Skin
9-slice at 70%, pushed skin at 85% under the mouse) and the key chip are baked on the CPU from crops of `Kenshi_UI.png` and uploaded as
the atlas is (UNORM, so they draw in its colours); icons are atlas rectangles. The card shows the newest save's name, day, place,
squad (when it differs from the name), money and members (play time is not read from saves yet) and is Enter's default. The original buttons stay as invisible hit targets at the
same bounds, so input is the faithful screen's. Lettering is Exo 2 from the painter's 16 px atlas (ASCII and Latin-1 up to U+00B7),
or a 48 px bake past 1.25x, so it stays sharp.
The Meitou wordmark, top right, is `assets/branding/kenshi-logo.png` (Lo-Fi's logo, embedded; see THIRD_PARTY_NOTICES),
because the game ships none as a file ([ui-screens](game/ui-screens.md#title-artwork-and-location)).
No title actor enters gameplay or save data; the normal camera lens and character source
are restored when leaving the title.
Title character preparation starts ahead of ordinary streaming during boot; the complete scene uses the readiness contract above.
`--title-preview --screenshot <path>` captures it for visual QA (`--settings-showcase` the settings menu's Graphics page in its states); `--ticks` advances the fight. Save arrival still uses the
loader's leader-focused camera contract; saved camera pose restoration remains unimplemented.

### Eye height

Engine choice (`EyeLift`): loading adds the eye's height above the ground beyond the game's highest camera (1840, `KenshiCamera.MaxHeightAbovePivot`)
to a distance across the ground, so the game's camera heights load as before and a free camera high above the land loads only what it can see. It
measures the terrain's biome textures and which load at full size (`TerrainTextures.UpdateBiomes`), the object zones laid out, kept and unparked
(`ObjectStreamer.NearZone`), foliage zones and grass pages ([render-foliage](render-foliage.md)). Characters: only those within drawing reach of the eye
(6000, 3000 while lifted, `CharacterRenderer.LiftedReach`) or 3000 of a kept point or a flight's arrival are built (meshes, textures); all were before,
the draw list holding every loaded character. **Observed** (play benchmark flights, ~1,400 characters): character textures 576 MB to 122 MB. Drawing
and the kept points and arrivals still measure as before.
A free camera over 3000 above the ground under it (`KenshiCamera.FlightHeight`, the focus gate in [camera](formats/camera.md)) has the weather's fog and
camera particle effects off, the fog thinning over the 500 below it (`SkyRenderer.WeatherFogScale`): they surround a ground-level eye and would fill a flight's whole view.

## Rendering and upscaling

WorldFrame prepares/streams sky/shadows/reflection, the scene (one depth) and post. Algorithms
live in render-* references; Data holds backend-independent terrain/placement/light rules.
Own GLSL compiles through bundled shaderc, explicit native resource/state recording and ordered
secondary jobs. [Native API](renderer-native.md).

Single-sample jittered HDR reconstruction, separate near/far depth, unjittered reflection/shadows.
**Observed** (RTX 4090, shared with other processes): Streamline returns result 39 (`eWarnOutOfVRAM`, with the log line "Exceeded VRAM budget") from `slEvaluateFeature` when the card runs short of memory; the call still records and runs. Meitou took it for a failure, freed DLSS under the recorded frame and the next fence wait said `ErrorDeviceLost` (seen after 30 DLSS/off toggles with other games on the card). Now result 39 counts as success (logged once), and a real failure retires the upscaler at the start of the next frame, not under the frame that holds its commands. 60 toggles with enough memory never failed and did not grow the memory in use (the `vram` control command).
FSR Vulkan SDK1.1.4 runtime DLL; Streamline2.14.16 manual hook, initialize before Vulkan, request
features and proxy present; when it reports DLSS unsupported (any non-NVIDIA GPU) it is shut down at
once. **Observed** (#55, RX 6600): on AMD `slInit` still loads `sl.common`, whose Vulkan hooks warn
"NOT supported". On Linux DLSS goes through NVIDIA's NGX client library and the DLSS model, beside the
executable or in `MEITOU_DLSS_PATH` ([post](render-post.md)). Bottom-up images require matching clip matrices/presentation.
Vendor DLLs are not committed; paths/packaging/licensing: [notices](../THIRD_PARTY_NOTICES.md).
Mip biases and temporal/GI contracts: [post](render-post.md). GI requested by default unless
--no-gi, unsupported ray queries fall back to flat ambient; gi Enhancement chooses output.

## Validation and performance

Tiering/PGO off, serverGC4 heaps, compact after loading then SustainedLowLatency; avoid streaming
allocations and measure tails. Offscreen fixed grass sway enables repeatable pictures. Parity
scripts compare10views; establish baseline noise for temporal vendors. Game --screenshot/--ticks,
--quit-after and --load exercise host wiring; [bench](bench.md) documents measurement.
**Observed** NVIDIA OpenGL/Vulkan sampling: anisotropy -0.25 bias reduced rock mean1.21→0.0009;
other vendors untested. Alpha-to-coverage differs; depth usesD32_SFLOAT. These are reference
observations, not a supported OpenGL backend.
