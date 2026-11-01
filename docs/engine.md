# Engine architecture

Projects: Core installation; Data clean-room readers/typed game data; Simulation headless world;
Navigation own navmesh; Engine WorldSession/time/input/camera; Rendering Vulkan/content/streaming;
Rendering.Display window/device/presenter; Game host/UI; ModelViewer debug world/atlas; Tools surveys.
GPU and simulation ownership stay separate. [Simulation contracts](simulation.md),
[native renderer](renderer-native.md), [viewer controls](viewer.md).

## Loop, input and camera

WorldSession owns world, clocks, input/bindings, camera and last two snapshots. Control30Hz real
(default config tickRate), max10 ticks/frame; simulation1/30 game second, speed0/1/2/5 and
6*speed budget. Control precedes simulation; interpolation uses each clock's alpha, shortest
angle rotation and cuts on mode changes. The authoritative time/order/determinism details are
in simulation. Host copies camera state into WorldCamera and snapshots into CharacterDrawList. The next world tick runs on its own
thread during the world draw ([simulation](simulation.md#tick-ahead)).

KenshiCamera follows pivot/boom/zoom/pitch/ground constraints from [camera](formats/camera.md).
Engine choices: pivot500*clamp(boom/600,0.1,1), keys1.5rad/s, mouse0.005rad/pixel; terrain-ground
pivot rather than building support. Free-camera ; follows smoothed flight, exits at ground hit.
InputState consumes events once per tick; commands apply at world boundaries, not UI callbacks.
meitou.user.json persists fps/vsync/fullscreen/tickRate/simThreads/graphics/bindings (layout and versioning: [launcher](launcher.md#graphics)); renderer
settings and current key list belong in viewer. Quicksave F5/loadF9; cinematic F6/hideUI F7.

## Host and loading

GameHost partials own boot/interactive/offscreen, characters, saving, transitions, duel and battle;
PlayerInterface owns picking/HUD/panels/commands. StandardSystems supplies order. NavAdapter
bridges Navigation; never let UI/rendering duplicate simulation state.
GUI executable attaches a caller's console; folder prompt creates its own when necessary.
Native splash precedes Vulkan; worker loading and render-thread GPU steps keep the window
responsive. On Wayland the window is shown and its first configure taken before the first loading frame is presented: a buffer attached before the shell role makes the compositor drop the client (**Verified** natively on KDE Wayland, GLFW 3.4). Full screen at start shows the window before going full screen there: GLFW ignores showing a full-screen window, and on Wayland (unlike X11) going full screen while hidden never maps it (**Verified** KDE Plasma, GLFW 3.4, PR #77). Scene transitions draw loading between stages, wait on asynchronous results, and
preserve world on failure. Gameplay warm-up waits for nearby population, terrain, scenery, character
assets and used textures (including deferred loads), then <40ms frames for0.5s (max5s for frame pacing).
After 5s of continuous `VramGuard` pressure, content it holds back is no longer waited for (it cannot load until the use falls).
A game's warm-up waits only for the characters within 3000 units of the camera's focus (`CharacterRenderer.LoadingFocus`, `LoadingReach`); the rest are prepared once
the screen lifts. The original's loaded zones (the camera's 3 x 3 block) can hold hundreds of NPCs, whose meshes' generated levels are built once and then read from a disk
cache ([character-renderer](character-renderer.md#content-lod-and-memory)). **Observed** (a Great Desert save near Heng, RTX 4090): 479 looks pending and a 130 s wait before this rule, 35 and 40 s with it.
The warm-up (title and game) waits for grass only within the range of the default grass distance (`FoliageRenderer.LoadingPending`, `DefaultGrassRange`);
pages a longer setting reaches grow in, nearest first, after the screen lifts. **Observed** (save siwa-test4, the Ultra tier's grass distance x 2): the title waited
107 s and the game about as long for 16 000 and 27 000 grass pages made at about 400 a second, everything else ready.
Under the cover the streams' frames need not be smooth: the character, foliage and object renderers (`Covered`, set by the host each frame) upload and build for up
to 16 ms a frame instead of their play budgets, foliage layouts and grass pages are queued four per streaming thread (`BackgroundWork.Threads`; finished ones are
taken once a frame), and objects take eight times the zones and resolves a frame. Once the warm-up's content is ready the streams are back at their play budgets, so
its run of smooth frames measures play. **Observed** (siwa-test, Ultra settings, 662 mods, RTX 4090, 9800X3D, warm disk caches): the game's warm-up went from 20.8
to about 6.5 s with this, the grass picking, the coarsening rule and the character LOD cache.
Population builds are queued/applied at frame boundaries without advancing game time, AI or movement;
navmesh-gated spawns are retried while loading. Warm-up and the flight hold the simulation paused.
The Faithful title does not wait: its picture is opaque, so the start ends with the splash ([ui-screens](game/ui-screens.md#splash)) and the boot.
The Meitou title waits for terrain, scenery, character assets and used textures (including deferred loads), then
one full second of <40ms frames (max5s of ready content for frame pacing, so slow GPUs reach the menu). Its menu artwork/fonts/pipelines warm under the opaque cover too;
streaming uploads retire and a compacting GC completes before that final smooth run.
**GC:** the stock runtime (server GC, four heaps, gen0 budget at most 16 MB: `System.GC.Gen0MaxBudget`) in builds and release zips alike; the startup `gc` line names the collector (`GcName`).
**Observed** (2026-10-24, play benchmark, 5 min, ~160 MB/s allocated, RTX 4090): the default budget let ~1 GB build up between collections, 27 gen0/gen1 collections paused
20-190 ms (median ~26); at 16 MB, 330 collections paused 2.5 ms median and 31 ms at most, total pause about the same. 64 MB: max 122 ms; workstation concurrent GC: max 25 ms
but 3.6 s of pauses in all; DATAS (`DynamicAdaptationMode=1`): max 66 ms. `System.GC.Gen0MaxBudget` is read from runtimeconfig (**Verified**: collection counts with
and without it); `GCgen0MaxBudget` there is ignored and `DOTNET_GCgen0size` changed nothing. The
experimental Satori GC (github.com/VSadov/Satori, `PublishWithSatoriGC` SDK) is not used in official builds. **Observed** earlier A/B (RTX 4070, 10.0.13
Satori vs stock 10.0.12 fast flight): longest GC pause 15-21 ms stock vs 1-18 ms Satori, frames unchanged (long frames are foliage updates, docs/viewer.md).
`GCConserveMemory` is not set. **Observed** A/B (`DOTNET_GCConserveMemory=5`, 3600-frame fly benchmark, 3 clean runs each): managed heap at the end
1.42 vs 1.93 GB, working set 3.37 vs 3.93 GB, but gen2 GCs 12-14 vs 7-8 and pause totals 54-306 vs 36-53 ms (one run 64 frames over 33 ms).
Process memory beyond the GC heap is mostly untouched write-combined commit (~6 GB at 6.7 GB of VRAM in use), not resident RAM.
**Pooled buffers** (allocation is the GC lever): streaming garbage is mostly LOH arrays from worker jobs. **Observed** in flights: gen0/gen1 pauses under
~40 ms; single blocking gen2s (`NonConcurrentGC`, reason `AllocSmall`) of 0.3-1.7 s in some runs despite SustainedLowLatency, with or without pooling;
what triggers them is **Unknown**. Pooled now:
DDS file bytes of `WorldTextureCache.Load` (`DdsReader.ReadKept(..., pool)`, `ArrayPool<byte>.Shared`; returned in `Load` after an RGBA8 decode or
by the last upload step of a stored BC upload), the GI terrain grid's samples (one array per `TerrainGrid`), the card trimmer's clip/hull scratch
(`FoliageCardTrimmer.Hull`) and the grass generator (`[ThreadStatic]` `FoliageRandom`, reseeded). Contract: a rented array may be longer than asked,
so its length is never the logical size (pass counts; `DdsFile.TrailingBytes` is meaningless on a pooled file); return it in a `finally` or at the
one point after its last reader, never while anything keeps it; a buffer that is dropped instead (a texture never pumped) just goes to the GC.
`ArrayPool<byte>.Shared` rounds up to a power of two (a 4.2 MB file rents 8 MB) and trims idle arrays on gen2; textures in flight bound what it holds.
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
0 MB native held at the end, gen2 counts 3-4 vs 3, p99 unchanged. GC pause totals 28-161 ms (base) vs 98-158 ms (native): inconclusive, **Unknown**.
Cancel on window close. Helpers/services dispose/join with their owning scene/session.
After warm-up, loading panels ease off the left/right edges over 0.7s while the loading image fades to the live scene and UI (Faithful: hidden at once, as the original).
Input remains blocked through this reveal. A new game ends warm-up as soon as the squad's characters are drawable, skips the sky arrival and opens the
character editor first (`GameHost.Editor`); once it closes, the loading screen returns and the world warms up as above. Sky loads dismiss only panels that were shown, keeping the sky flight visible.

Meitou UI mode presents title controls over a live view of the real world; Faithful retains
the original title image and loading screen. Advanced settings persist two independent scene-transition
toggles in `meitou.user.json`, both on by default: `mainMenuTransitions` covers entering a new game/save
from the main menu and returning to it; `inGameTransitions` covers save loads/new games during play.
Each has a persisted speed slider (`mainMenuTransitionSpeed` / `inGameTransitionSpeed`, 0.25–4x, default 1.5x).
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
  They start three seconds into combat.
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

## Rendering and upscaling

WorldFrame prepares/streams sky/shadows/reflection, scene depth slices and post. Algorithms
live in render-* references; Data holds backend-independent terrain/placement/light rules.
Own GLSL compiles through bundled shaderc, explicit native resource/state recording and ordered
secondary jobs. [Native API](renderer-native.md).

Single-sample jittered HDR reconstruction, separate near/far depth, unjittered reflection/shadows.
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
