# Engine architecture

How the code that runs the game is organised: the libraries, the game loop, the camera, the renderers and the backend
interface. Everything here is an **engine choice** unless it cites a fact about the original game (those live in
`docs/formats/` with Verified / Observed / Unknown labels). Plan and status: [../ROADMAP.md](../ROADMAP.md).

## Projects

| Project | Kind | What it holds |
| --- | --- | --- |
| `src/Meitou.Core` | library | The install (`GameInstall`: `KENSHI_PATH` / `meitou.local.json`). |
| `src/Meitou.Data` | library | Readers for the game's files (FCS, Ogre, DDS, world data) and the facts about the game as code (`KenshiCamera`, `KenshiLighting`, `ShadowCascades`, `TerrainQuadtree`...). No GPU, no windowing. |
| `src/Meitou.Engine` | library | The simulation frame: `WorldSession` (what is loaded, tick, clock, input, camera), fixed tick, game clock, input actions, the camera rig. No GPU, no windowing (tested headless). |
| `src/Meitou.Rendering` | library | The world renderers, their streaming and the frame (`WorldFrame`); the native Vulkan API under them (`Gpu/`, with the device in `Gpu/Core`, the shader compiler in `Gpu/Shaders` and the presenter), the FSR and DLSS upscalers (`Upscalers/`). No windowing. |
| `src/Meitou.Rendering.Display` | library | `VulkanDisplay`: the Silk.NET window with a Vulkan surface, the device, the `GpuContext`, the presenter and optional Streamline; shared by the game and the viewer. |
| `src/Meitou.Game` | exe `meitou` | The game: input, the loop; boots into the world. |
| `tools/Meitou.ModelViewer` | exe `meitou-viewer` | The debug viewer (`--world`, `--impostor-preview`) on the same libraries; its mesh and character modes were removed in phase 8 ([character-viewer.md](character-viewer.md)). |
| `tools/Meitou.Tools` | exe `meitou-tools` | Surveys of the install; `image-diff` for renderer parity checks. |

Dependencies point one way: Core ← Data ← Engine / Rendering ← Game / viewer. The renderers know nothing of the engine
(the game copies the camera state into the renderers' `WorldCamera`), and the engine knows nothing of rendering.

## The game loop (`meitou`, `Meitou.Engine.Time`)

- **Two clocks** (stage 0 of [simulation.md](simulation.md)). The **control tick** (`FixedStepClock`, 30 Hz of real time by default,
  `--tick-rate` or `tickRate` in the user config) consumes the input, handles pause and speed and moves the camera, so the camera
  keeps moving while the game is paused and at every speed. Each displayed frame adds the real time since the last one to an
  accumulator and runs as many control ticks as it holds, at most 10 (a long stall drops the rest; `DroppedTicks` counts them). The
  tick count depends only on the summed time, not on how it was split into frames (tests `TimeTests`).
- **Simulation tick** (`SimulationClock`): one tick is 1/30 s of **game** time at every speed. Its accumulator is fed `real dt x speed`,
  so speeds 1, 2 and 5 run 30, 60 and 150 ticks per real second and a pause runs none. A frame runs at most 6 x speed ticks
  (`MaxTicksPerFrame`); the rest are dropped and the game runs slower than asked (`AchievedSpeed`; the window title shows
  `(running x0.4)` when it does). Within a frame all control ticks run first (they may change the speed), then the simulation ticks
  the resulting speed allows (`WorldSession.Advance`). A speed change therefore applies to the whole frame it is made in.
- **Speed and pause** (Verified, [game/game-loop.md](game/game-loop.md#speed-and-pause)): the speeds are 1, 2 and 5 (no 3x); pause sets
  the speed to 0 and remembers the last non-zero one, and the next pause press returns to it; `RequestedPause` stops the world for
  menus and loading without touching the speed (paused = requested OR speed 0). Keys: the game's defaults, Space pause, F2/F3/F4 speed
  1x/2x/5x (`Speed1`..`Speed3`); `.` and `,` step through 1, 2, 5 as an extra. A new world runs at 1 (the original is at 0 until its start routine).
- **Interpolated drawing**: the camera keeps its state of the previous and the current control tick (`Interpolated<CameraState>`); a
  frame draws `At(alpha)` with `alpha` = the accumulator's fraction of a tick, so motion is smooth at any display rate. Angles take
  the shortest way round; a camera mode switch is a cut. The character snapshots are interpolated by `SimulationClock.Alpha` the same way.
- **Game time** (`GameClock`): advanced by simulation ticks only (`Advance(gameSeconds)`); **Verified** 1200/11 = 109.09 game seconds
  per game hour, 24 hours a day, the day count (a new game starts on day 1, **Observed** in the sample save, at 13:00, an engine
  choice), `IsDaytime` (strict `sunrise < hour < sunset`), `DaylightFactor` (0 at night, linear 2-hour ramps after sunrise and before
  sunset, 1 between; **Observed**), `HH:MM` (`floor(frac x 60)` minutes) and `Day: n` texts. Sunrise, sunset and days per year come
  from the CONSTANTS record (5 / 23 / 100); sunset not after sunrise gives 6 and 20. The window title shows the clock. The hour
  drives the sun, sky and lighting; the heat haze's game time is `HoursSinceStart` and stops while paused.
- **Systems**: the host adds the game's systems through `StandardSystems` ([simulation.md](simulation.md#systems-and-their-order)) and sends the camera focus as a
  `FocusCommand`; it fills `CharacterDrawList` from the interpolated snapshots
  ([simulation.md](simulation.md#population)). `--no-population` leaves the world empty.
- **World** (`Meitou.Simulation.World`, owned by `WorldSession.World`): runs one tick per simulation tick, with the phases, the
  worker pool, the stateless seeded randomness and the state hash described in [simulation.md](simulation.md#threading).
  After each tick it publishes a `WorldSnapshot`; the session keeps the last two (`PreviousSnapshot`, `CurrentSnapshot`) and
  `SimulationAlpha` (the simulation clock's fraction of a tick) so the host can interpolate characters like the camera. Worker
  threads: `--sim-threads` or `simThreads` in the user config (default half the cores, 1 to 8); `--seed` sets the world seed.
- **Input** (`Meitou.Engine.Input`): the host feeds an `InputState` (keys, buttons, mouse movement and wheel) from the
  windowing library's events; each tick consumes it once, so a press is seen by exactly one tick however frames and ticks fall.
  `InputBindings` maps keys and buttons to `InputAction`s; defaults below, overridable in the user config.
- **Frames**: vsync off by default with a frame limiter (default 240 fps; sleeps most of the frame and spins the last 1.5 ms);
  `--vsync`, `--fps-limit <n>` (0 = unlimited). The window opens maximized.
- **User config**: `meitou.user.json` (git-ignored) in the working directory, else next to the executable: `fpsLimit`, `vSync`,
  `tickRate` (the control tick), `simThreads`, `graphics` (the Tab panel's sliders by label) and `bindings` (action → comma-separated keys, e.g.
  `"RotateLeft": "Q,Left"`, buttons as `Mouse:Right`). Written on exit.

## The game host (`src/Meitou.Game`)

Code layout (engine choices; behaviour is the game loop above):

| File | Holds |
| --- | --- |
| `Program.cs` | `Main`: options, the install, the optional new-game start, the scene; hands over to `GameHost`. |
| `GameOptions.cs` | The game's own command-line options and the usage text. |
| `GameHost.cs` | Fields, `Boot` (GPU, camera, the simulation through `StandardSystems`, the session, the player interface), the nav zone upkeep. |
| `GameHost.Screenshot.cs`, `GameHost.Interactive.cs` | The unattended `--screenshot` run; the window loop, input wiring, title, `--quit-after` profile. |
| `GameHost.Characters.cs` | The renderer's draw list from the two snapshots, markers, the camera focus command. |
| `KeyMap.cs` | Silk key and button to the engine's. |
| `PlayerInterface*.cs` | Events and commands; picking (`.Picking`); the HUD (`.Hud`). The HUD formats the snapshot's numbers (`SkillSummary`) itself. |
| `NavAdapter.cs` | The navmesh as the simulation's `IAgentWalkability`. |

- **System order**: the host does not list systems; it calls `StandardSystems.Build` (`src/Meitou.Simulation/StandardSystems.cs`), which
  holds the order and the reasons for it. Tests that mean the game's wiring ask it for the parts they need (`SyntheticTown.Standard`).
- **Tick length**: `WorldSession` refuses a `WorldSettings.TickSeconds` that differs from the simulation clock's tick (the game clock
  advances by one, the world by the other).
- `PopulationSystem.ActiveZones` is double-buffered: the host reads it within the frame, right after the ticks, never across one.
  `WorldSession.Dispose` disposes the world and every `IDisposable` system (the movement system joins its path thread).
- Tests: `[Slow]` needs the install or a GPU; `[Bench]` asserts a timing (`--filter "Category!=Slow&Category!=Bench"` for a quiet quick run);
  the base game and the install's load order are loaded once per run by `InstallData`; waiting on helper threads goes through `TestWaits.TickUntil`.

## The camera (`Meitou.Engine.Cameras`)

The strategy camera follows [formats/camera.md](formats/camera.md) (Verified facts in `KenshiCamera`): a pivot on the ground, a
boom of 10 to 2000 (start 150, pitched 30°), the zoom step `boom -= zoom speed · input · min(boom / 600, 0.75)` (zoom speed 125
from `settings.cfg`; one wheel notch = 1), the pitch step refused once the view direction's y is at or past −0.92 / 0.2, and the
eye kept `min(0.2 · boom, 20) + 10` above the ground under it. Engine choices: the pivot moves at 500 (`camera speed`) ×
`clamp(boom / 600, 0.1, 1)` units per second (the setting's unit is Unknown); keys turn at 1.5 rad/s; mouse drag 0.005 rad per
pixel; the pivot follows the terrain only (the game also stands it on buildings; not done). The free camera (`;`, the game's
`toggle_fps_camera`) flies with a smoothed velocity at 1.5 × the height above the ground; leaving it puts the pivot where the
view meets the ground.

Default keys: `W A S D` move, `Q`/`E` or `Left`/`Right` rotate, `Up`/`Down` pitch, wheel or `PageUp`/`PageDown` zoom,
middle drag orbit (right click is the move command, see [simulation.md](simulation.md#player)), left click or drag selects, `1`..`9`, `` ` ``, `R`; `;` free camera (`R`/`F` up/down in it), `Space` pause, `F2`/`F3`/`F4` speed 1x/2x/5x (`.`/`,` step through them), `Tab` settings panel, `F8` or
`PrintScreen` screenshot (C:\Temp; Kenshi's own keys), `F10` key list, `F11` frame statistics, `F12` profiler chart (gpu, cpu, off),
`Esc` quit. The three debug overlays are the viewer's, and as there they are left out of screenshots.

## Rendering (`Meitou.Rendering`)

- **`WorldFrame`**: loads the region (`Load`: heightmap window, whole-world coarse heights, game data, objects), creates the
  renderers (`CreateGpu`), sets up the camera (`Setup`) and draws a frame (`Draw`): streaming updates, sky preparation,
  shadow cascades, the water reflection, the sky, then each depth slice (terrain, objects, foliage, water), then the post chain.
  `WorldOptions` (the `--world` options) configures it for both the game and the viewer.
- **Renderers**: `TerrainRenderer` (+ `TerrainTextures`, `TerrainStreamer`), `WorldObjectRenderer` (+ `ObjectStreamer`,
  `BuildingLodMesh`), `FoliageRenderer`, `WaterRenderer` + `ReflectionPass`, `SkyRenderer` (+ `AtmosphereShaders`), `ShadowPass`,
  `PostProcess`, `DebugOverlay` + `SettingsPanel`, `WorldTextureCache`, `BackgroundWork` / `UploadQueue` for streaming. What
  each draws and why: [viewer.md](viewer.md) ("World mode").
- **Scene logic is backend-independent** and mostly lives in `Meitou.Data.World`: the terrain quadtree and LOD, cascade fitting
  (`ShadowCascades`), haze and lighting parameters (`Sky`, `KenshiLighting`), object LOD and ranges (`MeshLod`, `ObjectRanges`),
  foliage placement (`FoliageLayout`, `FoliageGrassField`), zone layouts (`WorldObjectLayout`).
- **Determinism of pictures**: offscreen renders (`--screenshot`, `--fly-benchmark`) hold the grass sway still
  (`FoliageRenderer.SwaySeconds = 0`), so the same command gives the same picture; interactively the sway follows real time.

## Backend interface

The renderers record Vulkan directly through the native API in `src/Meitou.Rendering/Gpu/` ([renderer-native.md](renderer-native.md),
DECISIONS 22, adopted 2026-10-06; the GL-shaped `IGl` and its translation `VkGl` were deleted in phase 8 stage 3, renderer-native.md 8.9).
`GpuContext` is made by the display (or a test) from the device and owns the frame loop (`BeginFrame` / `EndFrame` / `Finish`) and the
segments: `BeginNative` / `EndNative` hand a renderer the frame's command list with a full barrier before and after; a host that begins
a rendering (`BeginHostPass`, with its `PassTargets` and `DrawState`) lets guests draw into it (`BeginGuest` / `EndGuest`, or `Record`
for a prepared job, possibly on a recording thread); `Interleave` records something that leaves a pass alone (a timestamp). Beside it:
`GpuFrame` and `CommandList`, `DeviceBuffer` / `Transient` / `BufferArena` / `Uploader`, `Texture` / `SamplerCache`, `ShaderLibrary` /
`PipelineLibrary`, `BindlessTable`, `ResourceStates`, `QueryArena` / `GpuStats`, and `LegacyProgram` (a GLSL pair with the SPIR-V and
layout VkGl built, kept for the programs not yet in the native model). `FrameGlobals` holds the frame-wide textures, blocks and uniform
values by GLSL name. The device and the shader compiler are in `Gpu/Core` and `Gpu/Shaders` (namespaces `Meitou.Rendering.Gpu.Core` /
`.Shaders`), the presenter in `Gpu/VulkanPresenter.cs`, the upscalers in `src/Meitou.Rendering/Upscalers`.

The renderers still state their resources in GL's vocabulary where that decides pixels: `GlConventions` (formats, blend factors,
compare functions, wrap modes, swizzles, vertex formats) and `SamplerDesc.FromGl` translate it, and the enumerations they take (`GLEnum`,
`InternalFormat`, `TextureMinFilter`, ... ten in all) are ours, at the end of `Gpu/GlConventions.cs`: the GL specification's names and
token values, only the members the code uses. **Vulkan is the only backend**: OpenGL was removed ([../DECISIONS.md](../DECISIONS.md) 18).
`--renderer vulkan` is accepted and ignored; `--renderer gl` prints a line saying OpenGL is gone and is ignored; an old `renderer` key in
`meitou.user.json` is ignored.

The window and the device come from `VulkanDisplay` (`Meitou.Rendering.Display`): windowed (swapchain through
`VulkanPresenter`, MAILBOX without vsync, FIFO with) for `meitou` and the viewer's interactive modes, headless (no window) for
`--screenshot` and `--fly-benchmark`. `MEITOU_VK_VALIDATION=1` turns on the validation layer for any of them and prints the error
count on exit; `=sync` adds synchronisation validation and `=gpu` GPU-assisted validation (bindless indices), both through
`VK_EXT_layer_settings`. Pipelines are cached on disk in `%LOCALAPPDATA%\Meitou\pipeline-cache.bin` (`MEITOU_PIPELINE_CACHE=<file>`
elsewhere, `=0` off).

### Vulkan backend

- **Device** (`Core/VulkanDevice`): Vulkan 1.3 with dynamic rendering, synchronization2, timeline semaphores, host query
  reset, scalar/std430 uniform blocks, push descriptors, depth clip control and extended dynamic state; a block allocator
  (64 MB blocks), a frame ring (2 frames in flight, deferred deletion), a pipeline cache. `MEITOU_VK_VALIDATION=1` turns the
  validation layer on (the Vulkan SDK's layer must be installed). **Required** (owner decision 1 of renderer-native.md): descriptor
  indexing (runtime arrays, partially bound, update after bind, non-uniform indexing) and `multiDrawIndirect` /
  `drawIndirectFirstInstance`; without them `GpuContext` refuses to start with a message naming what is missing. `drawIndirectCount`
  and `shaderDrawParameters` are enabled when present. `VK_EXT_debug_utils` is on whenever the loader has it (object names and
  command labels for capture tools), with or without validation.
- **Shaders** (`Shaders/`): the renderers' GLSL 3.30 compiled to SPIR-V by shaderc (relaxed Vulkan rules: loose uniforms in
  `gl_DefaultUniformBlock`, automatic bindings, vertex 0.., fragment 32..), varyings given locations by name, reflected from
  the SPIR-V (blocks, members, samplers, inputs). Compiled programs are cached on disk (`%LOCALAPPDATA%\Meitou\shader-cache`;
  `GlslProgramCompiler.CacheVersion` is in the key, bump it when the output for the same source can change).
  The stage offset is **not** set through shaderc's per-kind binding bases: every stage compiles with all bases 0 (the
  auto-binder gives each resource of a set its own slot) and the compiler then adds the stage base to each `Binding`
  decoration. Reason (2026-10-05): with the Vulkan SDK 1.4.363 `Bin` on PATH, its `shaderc_shared.dll` was loaded instead of
  the NuGet one; its glslang has a resource kind for combined image samplers (`EResCombinedSampler`) that
  `shaderc_uniform_kind` cannot address, so fragment `sampler2D`s got 0.. and clashed with the vertex stage (terrain drew
  flat, rocks floated; 21 tests failed; old cached SPIR-V hid it). The compiler also loads the shaderc it ships
  (`NativeLibrary.TryLoad` against the Silk assembly, which resolves `runtimes/<rid>/native`, not PATH).
  At link the default blocks are moved to descriptor set 1 (a decoration patch) as dynamic uniform buffers.
- **Coordinates: nothing is flipped while drawing.** Vulkan's framebuffer row 0 is where GL's row 0 is (NDC y = −1), so render
  targets, `gl_FragCoord` and texture coordinates mean the same; GL's counter-clockwise front faces are clockwise in Vulkan
  (the sign of the area flips with the y direction); clip depth stays −1..1 (`VK_EXT_depth_clip_control`; without it a remap is
  compiled into the vertex shaders). The picture is flipped once, by the blit into the swapchain image (`VulkanPresenter`).
- **State.** A pipeline per (program, vertex layout, primitive, attachment formats, blend, colour mask, polygon mode,
  alpha-to-coverage, depth clamp) from `PipelineLibrary`, created on first use; viewport, scissor, cull mode, front face, depth
  test/write/compare and depth bias are dynamic state. A host hands its guests a `DrawState` (the rules VkGl applied to GL's state:
  depth test and write only with a depth attachment, and so on).
- **Resources.** For a `LegacyProgram`, set 0 holds the named uniform blocks and the samplers, pushed (`VK_KHR_push_descriptor`) when
  anything in it changed; set 1 holds the loose uniforms (the default block), written into the frame's constants and bound by dynamic
  offset. Native-model programs use frame constants, push constants and the bindless table.
- **Memory and order.** Every image stays in `GENERAL` layout (owner decision 5 of renderer-native.md); native segments are
  separated by full memory barriers. Uploads (`Uploader`, staged through the frame's constants) go into a command buffer submitted
  ahead of the frame's own, with a full barrier at its start and end; textures are written in place (an upload is seen by the whole
  frame). A second copy into the same image within one upload command buffer waits for the first (a transfer barrier; before
  2026-10-06 the two copies raced, which synchronisation validation reported as 10 WRITE_AFTER_WRITE hazards per view and which made a
  few rock-view pixels differ between runs, docs/viewer.md). Mip generation, blits and readbacks are recorded in order in the frame.
- **Queries and counters.** `QueryArena`: timestamps from a per-frame-slot pool, read a frame ring later (`TryRead`), never waited
  for; `GpuContext.GpuFrameMs` is the last completed frame's GPU time. `GpuStats` counts per frame draws (indirect ones apart),
  dispatches, pipeline binds, descriptor pushes, native segments, constant and upload bytes, and keeps running totals for meters.
- **Known differences from OpenGL** (the last OpenGL pictures, master `f127922`, are the parity reference; Vulkan was within 0.08 mean in all eight views of the time; the forest view came later): NVIDIA's GL samples anisotropic textures a quarter mip
  finer, matched with a −0.25 LOD bias on NVIDIA (DECISIONS 9); alpha-to-coverage edges differ slightly (DECISIONS 10);
  `DEPTH_COMPONENT24` is a 32-bit float depth buffer.

## Upscaling

`--upscaler off|taa|fsr|dlss`, `--render-scale <0.25..1|native|quality|balanced|performance|ultra>` (1, 1/1.5, 1/1.7, 1/2, 1/3 per
axis; default 1 for all three, i.e. DLAA / FSR native AA; quality was the FSR/DLSS default until 2026-10-05), `--sharpness <0..1>`; in the game also the Tab panel (kept in `meitou.user.json`,
command-line values win). Off draws the scene at the display size and smooths the edges with the game's FXAA (the Faithful anti-aliasing; the scene is never multisampled). With an upscaler (`PostProcess`, DECISIONS 14):

- **Render size and jitter.** The scene is drawn at the display size × scale, single-sampled, its projection moved each frame by a
  Halton(2,3) offset of up to half a render pixel (`Jitter`; `8 × ratio²` phases). Shadows and the water reflection are not jittered.
- **Motion vectors** come from depth (the world is static): the far depth slice gets its own depth buffer, and a full-screen pass
  carries each pixel's surface into the previous frame with one matrix per slice, built relative to the eye (`Reprojection`).
  It writes RG = motion in UV (previous UV = UV − motion, jitter excluded) and B = depth of one D3D-style projection over the whole
  view (fixed planes 1 .. 10⁶: FSR decodes last frame's depth with this frame's planes, so they must not follow the camera), which is also
  what FSR/DLSS get as depth (R32F).
- **Grass motion.** The swaying grass moves on its own, which camera motion misses (trails under every upscaler). After the velocity
  pass, `FoliageRenderer.DrawGrassMotion` (through `PostProcess.ObjectMotion`) redraws the near slice's blades of layers with wind
  into the motion texture (red and green only), placing each now and with last frame's sway phase and camera, where its depth matches
  the depth buffer's (read in the shader, so the scene's depth is untouched). Only with an upscaler on; `MEITOU_GRASS_MOTION=0` turns it
  off for comparisons. Checked (rock view, sway at real speed, `--sway-step 0.016667` against a still picture at the same sway time,
  `--sway-start`): mean difference without / with: TAA 0.173 / 0.129, FSR 0.589 / 0.519, DLSS 0.438 / 0.349.
- **TAA** (`UpscaleShaders.Taa`, both backends): each display pixel takes a Gaussian of the 3 × 3 jittered render samples around
  it, the history reprojected with Catmull-Rom and clipped to the neighbourhood's colour spread (YCoCg variance clipping), blended
  in a tone-mapped space; the result (display size, HDR) is the history and the input of the exposure and the composite.
- **FSR** (`--upscaler fsr`, Vulkan; `Meitou.Rendering/Upscalers/FsrUpscaler`): AMD FSR 3.1.4 through the FidelityFX API
  (`amd_fidelityfx_vk.dll` from FidelityFX SDK v1.1.4, MIT, the newest SDK with a Vulkan DLL; DECISIONS 16), loaded at run time from
  `MEITOU_FFX_PATH` (the DLL or its folder) or next to the executable; never in the repository. Without it, TAA runs
  (one line says so). Inputs: the scene colour (HDR, FSR's auto exposure), the R32F depth, the motion texture with motion-vector scale
  (−render width, −render height) (FSR wants render pixels towards the previous position), the jitter as is (both conventions move the
  picture by +jitter along image columns and rows), sharpening = `--sharpness`. Images stay in GENERAL (declared COMMON /
  UNORDERED_ACCESS, which the backend maps to GENERAL and restores). `MEITOU_FFX_DEBUG=1` turns on FSR's input checks and messages,
  `MEITOU_FFX_DEBUGVIEW=1` its debug overlay (its rows appear upside down in our bottom-up picture). Checked: the jitter and motion
  conventions by flipping each (rock view, quality: still picture vs the native reference 5.2 mean, flipped jitter 6.4-8.5; orbiting
  camera vs a still picture at the end angle 4.8, flipped motion 7.9).
- **DLSS** (`--upscaler dlss`, Vulkan, NVIDIA; `Upscalers/Streamline` + `DlssUpscaler`; DECISIONS 17): NVIDIA DLSS Super Resolution
  through Streamline 2.14.1 (manual hooking). Needs `sl.interposer.dll`, `sl.common.dll`, `sl.dlss.dll` and `nvngx_dlss.dll` (optionally
  `NvLowLatencyVk.dll`, else one error line) in `MEITOU_STREAMLINE_PATH` or next to the executable; Streamline loads before the device
  when DLSS is asked for, so switching to DLSS in the game's panel works only if the game started with it. Per frame: a frame token,
  the constants (the fixed-plane projection and the clip-to-previous-clip matrix with y flipped, jitter as is, motion scale (−1, −1),
  camera vectors), four tags (depth, motion, colour in and out, all in GENERAL) and the evaluation; the water mask as a fifth tag (`kBufferTypeBiasCurrentColorHint`, NGX's "Bias.Current.Color.Mask"). Observed: with
  DLSS 310's default (transformer) preset the hint changes nothing; with a CNN preset (`MEITOU_DLSS_PRESET=5`, E) it changes the water
  edges. `MEITOU_DLSS_PRESET=<n>` sets Streamline's preset for every mode (0 default, 1..15 = A..O). `MEITOU_STREAMLINE_LOG=1` shows
  Streamline's info lines. Checked on an RTX 4070 (driver 596.49, DLSS 310.9.1): validation clean; rock view at quality: still picture
  vs the native reference 4.7 mean (FSR 5.2), orbiting vs still 3.9 (FSR 4.8).
- **Reactive mask (water).** Water is blended over the seabed without writing depth, so its moving surface has no motion vectors of
  its own. The velocity pass marks a pixel as water where the eye is above the water plane and the depth point below it (the water quad
  reaches past the far plane, so the sea under the horizon counts too) and writes `PostProcess.WaterReactive` (0.5) into A of the motion
  texture (TAA leans on the current frame by half of it) and, for FSR and DLSS, into a reactive mask. The mask is R32F: Streamline has no
  size for `R8_UNORM` ("format 0 native 9") and drops such a resource, FSR takes either. Checked by eye on Port North: the
  mask covers the water exactly.
- **Texture detail.** On Vulkan every mipmapped fetch is biased by `log2(scale) − 0.5` (TAA) or `− 1` (FSR/DLSS) (`ITextureLodBias`;
  DECISIONS 15).
- **Offscreen pictures** draw `WarmupFrames` frames first so the history converges; `--orbit-step <degrees>` turns the camera every
  frame of them (a check of the motion vectors: the picture should match a still one at the end angle but for edge differences).
  `--sway-step <seconds>` advances the grass sway every frame of them and `--sway-start <seconds>` sets where it starts (a check of the
  grass motion).

## Frame time

The render thread is above normal priority, the streaming threads below normal (`BackgroundWork`), and its parallel loops (foliage culling)
run on a few above-normal job threads (`RenderJobs`). The game and the viewer run server GC on four heaps without tiered compilation
(DECISIONS 19). The flight benchmark (`--fly-benchmark`) prints the stage means, the shadow casters' means, GC totals and the worst frames
with their stages and GC pauses; `MEITOU_JOB_STATS=1` adds what each streaming call site allocated and cost.
A draw through `VkGl` cost 1.5-2.3 µs of CPU in Release and 3.9-5.3 µs in Debug (measured before the native port, "Frame cost breakdown" below; VkGl is gone since phase 8), so draw counts matter more than triangles: meshes that repeat are drawn instanced (the
TERRAIN-mode rocks were one draw each and cost ~23 ms of shadow pass in a forest; docs/viewer.md, "Shadow pass cost").

## Frame cost breakdown (2026-10-06)

Phase 1 of the native-Vulkan migration: where the render thread's CPU time goes today (measurement only, no optimisation; no pixel
changes). Everything below is **Observed** (RTX 4070, driver 596.49, 1600 x 900, `--time 13`, still camera; **the machine was shared with
other agents' viewers and builds, GPU utilisation before a run was 0-45 %, so absolute milliseconds drift: the same Release trees view measured 3.5 ms
in the first batch and 5.9-7.1 ms an hour later; compare rows within a table, not tables with each other**). Medians of 3 runs unless a table
says otherwise. "Release" and "Debug" are `dotnet build -c Release` / `dotnet build` of our assemblies (Silk.NET and the driver are the same
in both; Debug-built assemblies carry `DisableOptimizations`, so the JIT compiles our code without optimising it; the game runs without tiered
compilation in both, see the runtimeconfig). The user runs Debug.

**How it was measured** (all off by default):
- `StageClock.OnClose/OnStart` (hook, null = one compare) plus `StageClock.Sub/Phase` marks at the shadow cascades, the foliage steps, the reflection
  pass and the post stages. The viewer's `PassMeter` (`MEITOU_PASS_STATS=1`, `MEITOU_PASS_STATS_SKIP=<frames>`, `MEITOU_PASS_TAG=<label>`) takes at
  every mark the Stopwatch time, the difference of the `VkGlStats` counters (now cumulative; new: pipeline binds, dynamic-state commands, set-1 binds,
  uniform-ring copies, descriptor writes, pushed textures, skipped pushes, vertex/index buffer binds, barriers, the GL state / uniform / texture-bind /
  bind / attrib calls the renderers made, pipeline-creation, fence-wait, submit, acquire and present ticks) and a GPU timestamp. A stage is a row;
  its parts are rows under it (part times are inside the stage's). Rows are means per frame; `PASSCSV` lines are for scripts.
  **Since phase 8 stage 3** (VkGl deleted, renderer-native.md 8.9) the counter columns are the native API's (`GpuStats.Running`: draws,
  indirect draws, dispatches, pipeline binds, descriptor pushes, native segments, constant KB, upload KB) plus fence-wait, submit, acquire and
  present; the tables below were taken with the VkGl columns and are kept as measured.
  **Since 2026-10-07** the GPU column is per frame like the others; before, a row's GPU time was divided by the times the row ran, so stages drawn
  once per depth slice (terrain, objects, foliage, water) and cascades drawn every second or fourth frame showed per run
  ([render-distance-benchmark.md](render-distance-benchmark.md) section 1).
- `MEITOU_VKGL_PHASES=1` (removed with VkGl in phase 8 stage 3): Stopwatch around the nine parts of `PrepareDraw` and, separately, around the `vkCmd*` calls themselves
  (`VkGlStats.PhaseTicks/NativeTicks`; ~0.05 µs per read, so the sums read a little high). `MEITOU_VK_MICRO=1`: 100 000 `vkCmdSetScissor` / `vkCmdSetCullMode`
  recorded through Silk.NET and through the raw function pointer.
- Benchmark runs: `set KENSHI_PATH=<Kenshi install>`, `set MEITOU_PASS_STATS=1`, `set MEITOU_PASS_STATS_SKIP=80`, then
  `meitou-viewer.exe --world <view> [--faithful shadows] --time 13 --size 1600x900 --fly-benchmark 300 --fly-speed 0` (serialised: the GPU is waited
  for after each frame; 220 measured frames). Views: `hub` = `--town "The Hub" --distance 40000 --pitch 3`; `rock` = `--at -51468,-14324 --yaw 95 --pitch 2 --distance 300`;
  `portnorth` = `--town "Port North"`; `zone14_30` = `--zone 14,30`; `forest` = `--at -37582,-80684 --yaw -70.5 --pitch 6.1 --distance 10588`;
  `junk` = `--at -60564,-45142 --distance 1400 --pitch 25`; `trees` (dense tree view, found by trying camera spots around the forest: 5033 foliage meshes
  and 61 757 grass blades in the main pass, against 1908 and 63 588 in `forest`) = `--at -39000,-82000 --yaw 45 --pitch 5 --distance 800`.
  Default shadows are Meitou's; "faithful" is `--faithful shadows`.

### Frame totals per view

Render-thread CPU per frame (mean over frames of the sum of all stages but the final GPU wait, plus the submit; 3 runs, median):

| view | Release, Meitou shadows | Release, faithful | Debug, Meitou | Debug, faithful | draws/frame Meitou (faithful) | GPU frame ms, Release Meitou |
|---|---|---|---|---|---|---|
| hub | 2.07 | 2.47 | 5.68 | 6.86 | 655 (915) | 6.6 |
| rock | 2.87 | 3.61 | 8.10 | 10.30 | 741 (1024) | 6.9 |
| portnorth | 1.46 | 1.68 | 3.57 | 4.44 | 343 (553) | 7.6 |
| zone14_30 | 2.61 | 2.82 | 7.24 | 8.05 | 1037 (1174) | 8.4 |
| forest | 3.35 | 3.87 | 9.54 | 10.94 | 1135 (1332) | 8.1 |
| junk | 2.62 | 3.13 | 7.62 | 8.16 | 889 (1113) | 10.9 |
| trees | 3.52 | 4.36 | 9.74 | 11.79 | 1040 (1283) | 5.5 |

Debug costs 2.4-3.3 times Release in every view; Meitou shadows are 5-20 % cheaper than the faithful ones (fewer cascade draws). GPU frame times are
from the first to the last timestamp of a frame and include the other agents' load: only the order of magnitude (5-11 ms) is meaningful. Windowed
(`meitou-viewer.exe --world <view> --time 13 --quit-after 20`, vsync on, 2 runs, first 120 frames skipped; same draws per frame) the render-thread CPU was
**higher**: Release forest 6.3 / 6.5 ms (132 fps), junk 5.6 / 4.1, trees 6.4 / 5.8; Debug forest 13.9 / 13.6 (72-74 fps), junk 12.3 / 12.7, trees 12.4 / 11.4;
every stage row was about 1.5-2 times its offscreen value. Explained by the owner (2026-10-06): the window was unfocused while another game (League of Legends) ran, so the windowed runs shared the CPU and GPU with it; treated as solved, not re-measured. In windowed mode
the acquire cost 0.01 ms, the wait for a free frame (fence) 0.2-1.0 ms of the "between frames" time, the submit 0.09-0.10 ms, `vkQueuePresentKHR` 0.07-0.08 ms
and the (closed) overlays 0.01 ms: acquire/present are not a CPU cost.

Counters per frame (Release, Meitou shadows and faithful, one run each; the counts do not depend on the build):

| view | shadows | draws | pipeline binds | uniform KB | uniform ring copies | descriptor pushes | descriptor writes | textures pushed | pushes skipped | vertex-buffer binds | dynamic-state cmds | set-1 binds | render passes | GL state calls | GL uniform calls | GL texture binds | GL bind calls | GL attrib calls |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| hub | meitou | 655 | 27 | 215 | 767 | 281 | 1836 | 1554 | 374 | 1767 | 148 | 637 | 13 | 182 | 3208 | 711 | 555 | 449 |
| hub | faithful | 915 | 27 | 244 | 1010 | 326 | 1973 | 1645 | 589 | 3508 | 157 | 879 | 12 | 263 | 4641 | 1021 | 859 | 1176 |
| rock | meitou | 741 | 27 | 237 | 764 | 273 | 2251 | 1918 | 468 | 2627 | 153 | 658 | 13 | 195 | 3158 | 894 | 678 | 896 |
| rock | faithful | 1024 | 30 | 287 | 977 | 328 | 2417 | 2028 | 697 | 3851 | 154 | 869 | 12 | 256 | 3986 | 1142 | 907 | 1466 |
| portnorth | meitou | 343 | 18 | 103 | 323 | 57 | 592 | 513 | 286 | 1461 | 124 | 314 | 13 | 160 | 1808 | 440 | 332 | 457 |
| portnorth | faithful | 553 | 19 | 144 | 506 | 79 | 648 | 547 | 474 | 2283 | 120 | 495 | 12 | 184 | 2493 | 603 | 451 | 706 |
| zone14_30 | meitou | 1037 | 21 | 394 | 1402 | 765 | 5035 | 4269 | 272 | 2148 | 127 | 1027 | 13 | 130 | 4728 | 1334 | 919 | 165 |
| zone14_30 | faithful | 1174 | 22 | 423 | 1539 | 772 | 5058 | 4283 | 402 | 2438 | 125 | 1163 | 12 | 138 | 5130 | 1378 | 947 | 238 |
| forest | meitou | 1135 | 23 | 381 | 1377 | 675 | 4600 | 3899 | 460 | 3073 | 148 | 1068 | 13 | 216 | 5069 | 1366 | 1024 | 909 |
| forest | faithful | 1332 | 23 | 414 | 1574 | 681 | 4621 | 3912 | 652 | 3572 | 143 | 1264 | 12 | 248 | 5814 | 1471 | 1086 | 1112 |
| junk | meitou | 889 | 29 | 281 | 973 | 291 | 2359 | 1990 | 597 | 3641 | 165 | 827 | 13 | 313 | 4782 | 1233 | 934 | 1477 |
| junk | faithful | 1113 | 32 | 324 | 1173 | 332 | 2479 | 2068 | 782 | 4486 | 165 | 1025 | 12 | 360 | 5609 | 1451 | 1077 | 1787 |
| trees | meitou | 1040 | 27 | 374 | 1253 | 621 | 4208 | 3561 | 420 | 2890 | 157 | 972 | 13 | 226 | 4779 | 1286 | 974 | 928 |
| trees | faithful | 1283 | 33 | 426 | 1468 | 651 | 4296 | 3618 | 632 | 3739 | 161 | 1184 | 12 | 298 | 5722 | 1459 | 1144 | 1357 |

### Per pass

Means per frame, medians of 3 runs (median of the three runs' means for every cell). CPU Rel / Dbg: the render thread's time in that pass (a stage that
runs once per depth slice, such as terrain and foliage, is summed; reflection and shadow cascades are drawn only on some frames with Meitou shadows, so their
rows are averages). GPU: the time between the timestamps at the pass's ends, which also holds barriers and overlap with neighbours; read a few frames late.
Rows with a "/" are parts of the stage above them. "GL calls" = state + uniform + texture-bind + bind + attribute calls the renderer made in the pass. "dyn state" =
`vkCmdSet*` commands, "set 1" = `vkCmdBindDescriptorSets` of the loose-uniform block, "tex pushed" = combined image samplers written by descriptor pushes.
Not shown (nothing measurable): uploads at frame start, `upd-*` stages (0.1-0.2 ms Release), sky-prepare, the shadow cascades c0-c2 parts
(drawn with the same code as c3 at smaller counts), and "new pipelines" (zero in every measured frame: the pipeline cache never misses after the 80 skipped frames).
A fully idle `fol cull` / `fol upload` row is the foliage renderer's own culling and instance upload (CPU, no draws): `fol cull` is 0.15-0.34 ms Release / 0.7-0.9 ms Debug (the cascades share one culling, charged to the first cascade drawn).

**forest, Meitou shadows** (`--at -37582,-80684 --yaw -70.5 --pitch 6.1 --distance 10588`):

| pass | CPU Rel ms | CPU Dbg ms | GPU ms | draws | pipe binds | uniform KB | uniform copies | pushes | tex pushed | vbuf binds | dyn state | set 1 binds | GL calls |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| shadow c0 | 0.40 | 1.04 | 0.04 | 1 | 1 | 0.2 | 1 | 1 | 2 | 1 | 9 | 1 | 74 |
| shadow c1 | 0.09 | 0.21 | 0.13 | 14.5 | 1 | 4.8 | 10 | 5.5 | 10 | 91.5 | 3 | 9.5 | 281 |
| shadow c2 | 0.16 | 0.44 | 1.45 | 33 | 0.8 | 6 | 26.8 | 8.5 | 16.5 | 245 | 4.5 | 26.3 | 584 |
| shadow c3 | 0.24 | 0.66 | 1.86 | 68 | 0.8 | 11 | 56.8 | 11.8 | 23 | 337.5 | 5 | 56.3 | 693 |
| &nbsp;&nbsp;shadow c3/objects | 0.03 | 0.05 | 0.44 | 6 | 0.3 | 2.1 | 1 | 3 | 6 | 66 | 0.3 | 0.8 | 60 |
| &nbsp;&nbsp;shadow c3/fol meshes | 0.05 | 0.12 | 1.01 | 18.3 | 0.3 | 2.6 | 18.5 | 8.3 | 16.5 | 200.8 | 4 | 18.3 | 450 |
| &nbsp;&nbsp;shadow c3/fol rocks | 0.05 | 0.17 | 0.23 | 6.8 | 0.3 | 0 | 0.3 | 0.3 | 0 | 33.8 | 0.3 | 0.3 | 104 |
| shadows | 0.04 | 0.05 | 0.03 | 2 | 1 | 0 | 0 | 1 | 1 | 0 | 10 | 0 | 32 |
| reflection | 0.12 | 0.40 | 0.16 | 51.3 | 0.8 | 12.5 | 48.5 | 0.8 | 9 | 91 | 3.3 | 47.8 | 191 |
| sky-draw | 0.02 | 0.04 | 0.20 | 1 | 1 | 0.4 | 1 | 1 | 3 | 0 | 9 | 1 | 63 |
| terrain | 0.34 | 1.47 | 2.00 | 274 | 2 | 48 | 276 | 2 | 40 | 274 | 18 | 274 | 706 |
| objects | 0.18 | 0.42 | 0.13 | 40 | 2 | 30.5 | 20 | 18 | 234 | 440 | 2 | 18 | 442 |
| foliage | 1.01 | 3.13 | 0.76 | 342 | 3 | 209.6 | 626 | 317 | 2934 | 995 | 9 | 326 | 3765 |
| &nbsp;&nbsp;foliage/fol meshes | 0.10 | 0.28 | 0.61 | 27 | 1 | 18.2 | 28 | 18 | 234 | 297 | 7 | 27 | 712 |
| &nbsp;&nbsp;foliage/fol grass | 0.68 | 1.87 | 0.11 | 298 | 1 | 190.9 | 596 | 298 | 2682 | 596 | 1 | 298 | 2715 |
| &nbsp;&nbsp;foliage/fol rocks | 0.07 | 0.25 | 0.05 | 17 | 1 | 0.5 | 2 | 1 | 18 | 102 | 1 | 1 | 337 |
| water | 0.03 | 0.06 | 0.05 | 2 | 2 | 1.3 | 4 | 2 | 16 | 2 | 3 | 2 | 142 |
| post | 0.44 | 1.14 | 0.92 | 306 | 8 | 56.3 | 307 | 306 | 610 | 596 | 72 | 306 | 1609 |
| &nbsp;&nbsp;post/post ssao | 0.02 | 0.04 | 0.19 | 3 | 2 | 0.1 | 3 | 3 | 3 | 0 | 27 | 3 | 27 |
| &nbsp;&nbsp;post/post upscale | 0.37 | 1.04 | 0.52 | 300 | 3 | 56.2 | 301 | 300 | 601 | 596 | 18 | 300 | 1547 |
| &nbsp;&nbsp;post/post exposure | 0.03 | 0.04 | 0.16 | 2 | 2 | 0 | 2 | 2 | 3 | 0 | 18 | 2 | 17 |
| &nbsp;&nbsp;post/post composite | 0.01 | 0.01 | 0.05 | 1 | 1 | 0 | 1 | 1 | 3 | 0 | 9 | 1 | 14 |

Release gap to Debug in the forest: foliage 1.01 -> 3.13 ms, terrain 0.34 -> 1.47, shadow cascades 0.93 -> 2.4, post 0.44 -> 1.14. The shadow rows of the faithful
shadows (forest, below) put 316 draws and 2.4 ms of GPU into cascade 3 alone against 65 draws with Meitou's.

**forest, faithful shadows** (`--faithful shadows`; only the rows that differ in kind):

| pass | CPU Rel ms | CPU Dbg ms | GPU ms | draws | pipe binds | uniform KB | uniform copies | pushes | tex pushed | vbuf binds | dyn state | set 1 binds | GL calls |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| shadow c3 | 0.99 | 2.93 | 2.44 | 316 | 4 | 55.4 | 291 | 34 | 66 | 1174 | 27 | 289 | 2725 |
| shadow c3/fol meshes | 0.21 | 0.46 | 1.39 | 73 | 1 | 10.4 | 74 | 31 | 62 | 803 | 16 | 73 | 1798 |
| shadow c3/fol rocks (TERRAIN-mode) | 0.24 | 0.80 | 0.39 | 27 | 1 | 0.1 | 1 | 1 | 0 | 135 | 1 | 1 | 415 |

(the rest of c3, about 210 draws, is its terrain; cascades 0 and 1 are unused in this view.)

**trees (dense tree view), Meitou shadows**:

| pass | CPU Rel ms | CPU Dbg ms | GPU ms | draws | pipe binds | uniform KB | uniform copies | pushes | tex pushed | vbuf binds | dyn state | set 1 binds | GL calls |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| shadow c0 | 0.45 | 1.14 | 0.15 | 15 | 3 | 9 | 12 | 7 | 12 | 95 | 11 | 11 | 351 |
| shadow c1 | 0.08 | 0.20 | 0.18 | 16.5 | 1.5 | 5.2 | 14 | 7.5 | 14 | 110.5 | 5 | 13.5 | 317 |
| shadow c2 | 0.16 | 0.42 | 0.45 | 27 | 1 | 5.6 | 17.3 | 9.8 | 19 | 192.5 | 4 | 16.8 | 378 |
| shadow c3 | 0.26 | 0.73 | 0.82 | 65.3 | 1 | 11.1 | 53.3 | 11.8 | 23 | 303.3 | 4.5 | 52.8 | 572 |
| &nbsp;&nbsp;shadow c3/objects | 0.04 | 0.07 | 0.16 | 9.3 | 0.3 | 2.1 | 2.5 | 5 | 10 | 101.8 | 0.3 | 2.3 | 87 |
| &nbsp;&nbsp;shadow c3/fol meshes | 0.03 | 0.08 | 0.52 | 12.3 | 0.3 | 2.4 | 12.5 | 6.3 | 12.5 | 134.8 | 3.5 | 12.3 | 316 |
| &nbsp;&nbsp;shadow c3/fol rocks | 0.07 | 0.24 | 0.12 | 5.8 | 0.3 | 0 | 0.3 | 0.3 | 0 | 28.8 | 0.3 | 0.3 | 89 |
| shadows | 0.04 | 0.06 | 0.02 | 2 | 1 | 0 | 0 | 1 | 1 | 0 | 10 | 0 | 32 |
| reflection | 0.10 | 0.34 | 0.08 | 46.5 | 0.8 | 12 | 45.5 | 0.8 | 9 | 68.8 | 3.3 | 44.8 | 175 |
| sky-draw | 0.02 | 0.04 | 0.12 | 1 | 1 | 0.4 | 1 | 1 | 3 | 0 | 9 | 1 | 63 |
| terrain | 0.30 | 1.19 | 1.26 | 239 | 2 | 42 | 241 | 2 | 40 | 239 | 18 | 239 | 636 |
| objects | 0.15 | 0.33 | 0.04 | 30 | 2 | 36.5 | 15 | 13 | 169 | 330 | 2 | 13 | 353 |
| foliage | 1.20 | 3.78 | 0.77 | 320 | 4 | 200.7 | 573 | 290 | 2707 | 1013 | 14 | 302 | 3713 |
| &nbsp;&nbsp;foliage/fol meshes | 0.13 | 0.34 | 0.54 | 33 | 2 | 28.5 | 35 | 22 | 286 | 363 | 12 | 33 | 899 |
| &nbsp;&nbsp;foliage/fol grass | 0.63 | 1.65 | 0.10 | 268 | 1 | 171.7 | 536 | 267 | 2403 | 536 | 1 | 268 | 2445 |
| &nbsp;&nbsp;foliage/fol rocks | 0.20 | 0.85 | 0.13 | 19 | 1 | 0.5 | 2 | 1 | 18 | 114 | 1 | 1 | 367 |
| water | 0.03 | 0.06 | 0.01 | 2 | 2 | 1.3 | 4 | 2 | 16 | 2 | 4 | 2 | 142 |
| post | 0.39 | 0.98 | 0.59 | 276 | 8 | 50.7 | 277 | 275 | 548 | 536 | 72 | 276 | 1459 |
| &nbsp;&nbsp;post/post ssao | 0.02 | 0.03 | 0.13 | 3 | 2 | 0.1 | 3 | 3 | 3 | 0 | 27 | 3 | 27 |
| &nbsp;&nbsp;post/post upscale | 0.32 | 0.89 | 0.31 | 270 | 3 | 50.5 | 271 | 269 | 539 | 536 | 18 | 270 | 1397 |
| &nbsp;&nbsp;post/post exposure | 0.03 | 0.04 | 0.11 | 2 | 2 | 0 | 2 | 2 | 3 | 0 | 18 | 2 | 17 |
| &nbsp;&nbsp;post/post composite | 0.01 | 0.01 | 0.03 | 1 | 1 | 0 | 1 | 1 | 3 | 0 | 9 | 1 | 14 |

**junk field, Meitou shadows** (`--at -60564,-45142 --distance 1400 --pitch 25`; mesh draws and grass are fewer, objects and the reflection more):

| pass | CPU Rel ms | CPU Dbg ms | GPU ms | draws | pipe binds | uniform KB | uniform copies | pushes | tex pushed | vbuf binds | dyn state | set 1 binds | GL calls |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| shadow c0 | 0.28 | 0.69 | 0.21 | 21 | 3 | 9.4 | 21 | 12 | 22 | 169 | 15 | 20 | 508 |
| shadow c1 | 0.13 | 0.32 | 0.39 | 44.5 | 1.5 | 6 | 36.5 | 10 | 19 | 380.5 | 6 | 36 | 798 |
| shadow c2 | 0.10 | 0.27 | 0.99 | 37.8 | 0.8 | 4.5 | 33.8 | 8 | 15.5 | 288.3 | 4 | 33.5 | 620 |
| shadow c3 | 0.19 | 0.51 | 2.54 | 85.3 | 1.3 | 12.3 | 72.8 | 18.8 | 36.5 | 479.8 | 5.5 | 72.3 | 902 |
| &nbsp;&nbsp;shadow c3/objects | 0.04 | 0.08 | 0.66 | 10 | 0.5 | 2.1 | 2.3 | 9 | 17.5 | 108.5 | 0.5 | 2 | 110 |
| &nbsp;&nbsp;shadow c3/fol meshes | 0.07 | 0.16 | 1.63 | 27.5 | 0.3 | 2.9 | 27.8 | 9.3 | 18.5 | 302.5 | 4.5 | 27.5 | 621 |
| &nbsp;&nbsp;shadow c3/fol rocks | 0.01 | 0.02 | 0.05 | 5.3 | 0.3 | 0 | 0.3 | 0.3 | 0 | 26.3 | 0 | 0.3 | 81 |
| shadows | 0.04 | 0.05 | 0.03 | 2 | 1 | 0 | 0 | 1 | 1 | 0 | 10 | 0 | 32 |
| reflection | 0.15 | 0.55 | 0.35 | 72 | 1.3 | 20.9 | 69.5 | 4.5 | 59 | 234.3 | 5 | 68.3 | 520 |
| sky-draw | 0.02 | 0.04 | 0.20 | 1 | 1 | 0.4 | 1 | 1 | 3 | 0 | 9 | 1 | 63 |
| terrain | 0.30 | 1.37 | 2.70 | 239 | 2 | 42 | 241 | 2 | 40 | 239 | 18 | 239 | 636 |
| objects | 0.15 | 0.41 | 0.18 | 36 | 2 | 34.1 | 30 | 30 | 390 | 396 | 2 | 27 | 472 |
| foliage | 0.75 | 2.27 | 1.08 | 228 | 5 | 129 | 342 | 124 | 1234 | 1228 | 14 | 208 | 3365 |
| &nbsp;&nbsp;foliage/fol meshes | 0.22 | 0.68 | 0.64 | 76 | 2 | 44.7 | 78 | 25 | 325 | 836 | 11 | 76 | 1670 |
| &nbsp;&nbsp;foliage/fol grass | 0.35 | 0.99 | 0.18 | 130 | 1 | 83.3 | 260 | 97 | 873 | 260 | 1 | 130 | 1203 |
| &nbsp;&nbsp;foliage/fol rocks | 0.07 | 0.23 | 0.26 | 22 | 2 | 1 | 4 | 2 | 36 | 132 | 2 | 2 | 490 |
| water | 0.03 | 0.06 | 0.01 | 2 | 2 | 1.3 | 4 | 2 | 16 | 2 | 4 | 2 | 142 |
| post | 0.22 | 0.58 | 1.06 | 120 | 8 | 21.4 | 121 | 78 | 154 | 224 | 72 | 120 | 679 |
| &nbsp;&nbsp;post/post ssao | 0.02 | 0.04 | 0.21 | 3 | 2 | 0.1 | 3 | 3 | 3 | 0 | 27 | 3 | 27 |
| &nbsp;&nbsp;post/post upscale | 0.16 | 0.48 | 0.65 | 114 | 3 | 21.3 | 115 | 72 | 145 | 224 | 18 | 114 | 617 |
| &nbsp;&nbsp;post/post exposure | 0.03 | 0.04 | 0.14 | 2 | 2 | 0 | 2 | 2 | 3 | 0 | 18 | 2 | 17 |
| &nbsp;&nbsp;post/post composite | 0.01 | 0.01 | 0.07 | 1 | 1 | 0 | 1 | 1 | 3 | 0 | 9 | 1 | 14 |

### Inside VkGl: one draw (historical: VkGl was deleted in phase 8 stage 3)

`MEITOU_VKGL_PHASES=1`, 3 runs each, Meitou shadows, medians. Microseconds **per draw**, averaged over all draws of the run (the mix of
terrain, foliage, grass and post draws; first number = the part's whole time, second = the part of it spent inside the `vkCmd*` call(s), i.e. Silk.NET's dispatch plus
the driver's recording). Stopwatch reads add ~0.05 µs each, so the sums read a little high. These are `PrepareDraw` and the draw call only: the renderer's own GL calls (see "GL calls" above)
are not in them.

| part of a draw | Release forest | Release trees | Release hub | Debug forest | Debug trees | Debug hub |
|---|---|---|---|---|---|---|
| vertex layout + pipeline key | 0.07 | 0.11 | 0.12 | 0.33 | 0.40 | 0.42 |
| pipeline lookup (dictionary on a 20-field key) | 0.06 | 0.07 | 0.08 | 0.32 | 0.38 | 0.45 |
| bind pipeline (when it changed) | 0.05 / 0.03 | 0.08 / 0.06 | 0.13 / 0.10 | 0.07 / 0.04 | 0.11 / 0.07 | 0.15 / 0.11 |
| dynamic state compare + sets | 0.07 / 0.02 | 0.10 / 0.03 | 0.14 / 0.05 | 0.17 / 0.03 | 0.20 / 0.04 | 0.25 / 0.07 |
| set 1: loose uniforms copied to the ring + bind by offset | 0.24 / 0.07 | 0.31 / 0.10 | 0.38 / 0.12 | 0.57 / 0.11 | 0.65 / 0.13 | 0.77 / 0.17 |
| set 0: build the writes, compare with the last push | 0.32 | 0.38 | 0.53 | 1.42 | 1.58 | 2.00 |
| push descriptors (when different) | 0.18 / 0.12 | 0.25 / 0.16 | 0.25 / 0.16 | 0.22 / 0.14 | 0.30 / 0.20 | 0.30 / 0.19 |
| vertex buffers (one `vkCmdBindVertexBuffers` per input) | 0.31 / 0.13 | 0.41 / 0.18 | 0.45 / 0.19 | 0.51 / 0.20 | 0.66 / 0.27 | 0.63 / 0.27 |
| index bind + `vkCmdDraw*` | 0.18 / 0.10 | 0.22 / 0.13 | 0.26 / 0.15 | 0.26 / 0.14 | 0.33 / 0.18 | 0.38 / 0.22 |
| **total** | **1.47 / 0.46** | **1.92 / 0.65** | **2.34 / 0.79** | **3.85 / 0.66** | **4.60 / 0.91** | **5.34 / 1.03** |

So a draw's preparation is 1.5-2.3 µs in Release and 3.9-5.3 µs in Debug (the "~4 µs" the docs gave is the Debug figure), of which only 0.5-0.8 µs (Release) /
0.7-1.0 µs (Debug) is inside the `vkCmd*` calls; the rest is C# in VkGl. Debug adds 2.5-3 µs per draw and nearly all of it in the dictionary key
(0.3 + 0.3 µs), the sampler/buffer compare loop of set 0 (1.4-2.0 µs against 0.3-0.5) and the loose-uniform copy. The calls themselves are cheap:
`MEITOU_VK_MICRO=1` (Release, 100 000 calls each, recorded outside a render pass): `vkCmdSetScissor` 23.4 ns through Silk.NET, 20.9 ns through the raw function pointer;
`vkCmdSetCullMode` 22.2 / 16.1 ns; an empty loop 0.5 ns. Silk.NET's dispatch costs 2-6 ns a call (the same in Debug, it is a NuGet assembly); the driver's recording
is ~16-21 ns for a state command; per draw the pipeline-bind, push, vertex-buffer and draw calls together are 0.03-0.2 µs each in the table (averages over all draws, including draws that skip them).
Whole-draw all-in cost, for scale: the forest's grass (298 draws a frame, 9 GL calls each) is 0.68 ms Release / 1.87 ms Debug = 2.3 / 6.3 µs per draw.

### Foliage draw distance (dense tree view)

Foliage and grass draw distance both set to x1, x2, x4 (the default) and x8 (`MEITOU_FOLIAGE_RANGE=<x>`, `MEITOU_GRASS_RANGE=<x>`, the command-line
equivalent of the Tab sliders), `trees` view, Meitou shadows, `--fly-benchmark 300 --fly-speed 0`, the four settings interleaved in one batch (2 runs each, median of
the two; this batch ran when the machine was slower: its x4 is 7.1 ms Release where the first batch gave 3.5, compare down the column only):

| setting | foliage meshes drawn | grass blades | foliage draws (main pass, per frame) | frame CPU Rel ms | foliage stage Rel ms (meshes, grass) | frame CPU Dbg ms | foliage stage Dbg ms (meshes, grass) |
|---|---|---|---|---|---|---|---|
| x1 | 65 | 2 474 | 37 | 3.38 | 0.45 (0.07, 0.15) | 7.36 | 1.04 (0.16, 0.32) |
| x2 | 260 | 10 313 | 91 | 3.95 | 0.73 (0.13, 0.35) | 8.56 | 1.85 (0.31, 0.82) |
| x4 | 5 034 | 61 757 | 320 | 7.09 | 2.39 (0.26, 1.21) | 17.30 | 6.30 (0.59, 2.81) |
| x8 | 36 171 | 276 964 | 1 022 | 15.58 | 7.73 (0.45, 3.69) | 43.86 | 24.66 (1.22, 9.36) |

The draw count grows with the area (x8: 3.2 times x4's); mesh instances are batched (36 171 meshes need few draws) but grass is drawn one page per draw
(about 270 blades per page: 0.9-1.0 draw per 1000 blades), so grass pages are what the draw count follows. The foliage stage is about half of the growth at x8 (Release 0.36 -> 7.7 ms);
the rest is the shadow cascade 0 (the foliage culling it pays for: 0.46 -> 2.2 ms), the cascade-3 casters (0.14 -> 0.9) and the post stage's grass-motion redraw (33 -> 936 draws,
0.21 -> 2.2 ms). The GPU frame time grew 8.2 -> 15.4 ms (Release) at x8 as well.

### Foliage ranges by size (the `range` switch)

`trees` and `junk` views, Release, Meitou shadows, `--fly-benchmark 300 --fly-speed 0`, `MEITOU_PASS_STATS=1 MEITOU_PASS_STATS_SKIP=80`, foliage x4;
`--faithful range` against Meitou ranges at three slider sets (large / medium / small: low 3000 / 1500 / 500, default 5000 / 2500 / 800, high
8000 / 4000 / 1600, through `--range-large/-medium/-small`), interleaved, 2 runs each, mean of the two (**Observed** 2026-10-06; the owner was gaming on
the machine, so the frame means were noisy, 6-12 ms for the same run: the table gives the frame p50 and the per-stage means; compare within a view only).
"fol cull" is the sum of the culling steps (the main pass's depth slices and the first shadow cascade's shared candidate pass).

| view | ranges | foliage meshes drawn (main pass) | frame CPU p50 ms | foliage stage ms | shadow c0 ms | fol cull ms | meshes laid out (settle) |
|---|---|---|---|---|---|---|---|
| trees | Faithful (4000 for MEDIUM) | 5 034 | 7.47 | 3.50 | 1.73 | 4.11 | 128 471 (5.1 s) |
| trees | Meitou low | 368 | 5.24 | 2.25 | 0.53 | 1.03 | 128 471 |
| trees | Meitou default | 1 202 | 5.67 | 2.12 | 0.86 | 1.21 | 187 864 (11.5 s) |
| trees | Meitou high | 3 619 | 6.42 | 2.39 | 1.26 | 1.93 | 325 409 |
| junk | Faithful | 671 | 5.61 | 2.82 | 0.79 | 2.65 | 44 161 |
| junk | Meitou low | 260 | 4.63 | 1.61 | 0.56 | 0.60 | 44 161 |
| junk | Meitou default | 377 | 4.70 | 1.75 | 0.58 | 0.81 | 56 979 |
| junk | Meitou high | 545 | 5.96 | 3.06 | 0.90 | 1.61 | 91 244 |

Reading: most of the instances the game's per-layer range draws at x4 are small and medium meshes (litter, bushes, boulders), so the default
Meitou ranges draw a quarter of the trees view's meshes while reaching 1000 units further for the large ones; the culling cost follows the
number of instances in range (Faithful's 4.1 ms of culling in the trees view against 1.2). The longer near reach (the longest class range,
5000, against 4000) lays out 1.5 times the meshes on the worker threads and doubles the settle time of a still picture; at 8000 it is 2.5 times.
The low and default rows differ by less than the noise. GPU frame times were 3.7-5.7 ms in all rows.
*Later (2026-10-07): the Meitou defaults became 12000 / 5000 / 800 with billboards beyond 4000, so the "default" row above is the old default; see renderer-native.md 8.10.*

### Reading

**What dominates** (Release, forest, Meitou shadows, 3.35 ms; Debug 9.5 ms): (1) the foliage stage, 1.0 ms (Debug 3.1): 298 grass-page draws take 0.68 ms of it, the instanced meshes 0.10, the
TERRAIN-mode rocks 0.07, plus 0.15 for the foliage culling; (2) the shadow cascades together, 0.9 ms (Debug 2.4), of which 0.34 (Debug 0.94) is the foliage culling that cascade 0 pays for all of them and the rest 6-30 instanced mesh draws a cascade; (3) terrain, 0.34 ms
(Debug 1.5) for 274 chunk draws; (4) the post stage, 0.44 ms (Debug 1.1), of which 0.37 / 1.04 is the **second** grass draw for the upscaler's motion vectors (300 draws that repeat the grass pages, only
active with TAA/FSR/DLSS); (5) objects 0.18 (0.42), reflection 0.12 (0.40), water, sky, SSAO, exposure and composite 0.1 together. GPU, by contrast, is spread differently (terrain 2.0 ms and the
cascade-3 casters 1.9 ms lead): the CPU follows the **number of draws and of descriptor/uniform work per draw**, the GPU the pixels and triangles. The render thread never waited for the GPU or
for the swapchain in the measured frames beyond the deliberate per-frame wait of the benchmark, and acquire, submit and present together are 0.2 ms.

**After the native foliage port** (2026-10-06, wave 3 probe; docs/renderer-native.md 7.1 has the method and every number): the foliage renderer records
natively with the same shaders, and its CPU per draw fell from 4.4 / 2.8 / 3.3 µs (colour meshes, grass, shadow-cascade meshes, forest still camera) to
2.0 / 2.0 / 1.3 µs. In the forest view the foliage stage went from 1.08-1.14 to 0.87-0.88 ms (pass meter), the render thread's p50 by roughly 0.3 ms;
GPU time did not change. What is left per draw (1.4-2 µs) is the legacy model's default-block copies and descriptor work, as predicted below.
Its step O (the native descriptor model: push constants, one frame set per segment, bindless textures; same pictures) took a further 0.35 /
0.7 µs off a colour mesh / grass draw and about 0.3 ms off the foliage stage in the forest (three runs each on a shared machine; the depth
meshes did not gain within the scatter); docs/renderer-native.md 7.1.

**Where the per-draw CPU goes** (measured before the port): about 1.5-2.3 µs of VkGl work per draw (Release), a third of it in the driver; the larger part is C# that rebuilds, per draw, state the renderer
could have kept: a descriptor set 0 compare/build (the grass pushes 9 images to the same set 298 times a frame: 2 682 texture descriptors, 3 278 descriptor writes, each grass draw copies the whole vertex and fragment default blocks, 640 bytes in two ring copies, 190 KB of ring copies a frame for grass alone, 381 KB for the forest frame), a pipeline key of 20 fields hashed per draw (the cache never missed: 0 new pipelines), one
`vkCmdBindVertexBuffers` per input location (about 3 per draw: 3 073 a frame), and ~5 000 GL uniform calls, ~1 400 texture binds and ~1 000 other bind calls a frame (about 4.5, 1.2 and 0.9 per draw) that the
translation layer has to absorb before the draw. Debug triples all of it. In the same breath the vkCmd calls are only 0.5-0.8 µs per draw, so even a perfect native layer would still pay ~0.7 µs per draw for the calls
that remain; what it can remove is the other 1-1.5 µs (Release) / 3-4 µs (Debug).

**What a native API would remove** (interpretation of the numbers above): the per-draw pipeline key and lookup (0.13 / 0.7 µs: pipelines known up front, selected by an id); the set-0 build and compare (0.3-0.5 / 1.4-2 µs: persistent descriptor sets per material, or a bindless table, so a draw
binds nothing it already bound: textures pushed per frame drop from ~3 900 to the few hundred materials); the whole-block loose-uniform copies (0.25-0.4 / 0.6-0.8 µs and 381 KB a frame: per-draw data as a small push-constant or one
instance record, matrices instanced); the GL-call traffic (5 000 uniform + 2 400 bind calls a frame become the data written once into that record); vertex-buffer binds batched into one call; and most of all **fewer draws**: grass pages merged or drawn with
indirect/instanced calls (298 draws for 62 000 blades, and again for the motion pass), the cascade casters multi-drawn. The 16-21 ns per state command and 2-6 ns of Silk.NET dispatch say the function-call layer is not the cost; the layer above it is.
A Debug build of the renderers' own code (the user's build) costs 2.4-3.3 times Release for the same work, so cutting calls and per-draw bookkeeping helps Debug most.

**Not measured / Unknown**: the CPU cost of the streaming threads (not the render thread); GPU times per pass are approximate (timestamp gaps, shared GPU);
the `detail` lines at the end of a benchmark log give the instances and calls of the last frame only (Meitou shadows draw some cascades on alternate frames).

## Checking a change

- `tools/scripts/parity.sh <viewer exe> <out dir>` renders the ten reference pictures (five views: The Hub from 40000, the rock at
  −51468,−14324, Port North, zone 14.30 and the forest at −37582,−80684; at 13:00 and 02:00) offscreen at 1600×900 on Vulkan;
  `parity-compare.sh <meitou-tools exe> <a> <b>` compares two such folders (`meitou-tools image-diff`: mean absolute difference in
  0..255, share of pixels over 12, maximum, and a ×4 difference image). Against the stored OpenGL pictures (DECISIONS 1, 4, 18) the
  gate was a mean of 0.08 or less per view; the native-renderer ports gate against the build before them at maximum 0
  (renderer-native.md 7.7).
- `meitou-viewer --world --fly-benchmark 1500 --size 1600x900 [--fly-pipelined]`: frame-time percentiles of
  a flight; serialized (the GPU waited for each frame, 60 fps pacing) or pipelined (two frames in flight, no pacing: the
  interval between frames).
- `meitou --screenshot out.png --ticks n`: boots the game offscreen, runs n ticks, saves the picture; `meitou --quit-after 20`
  runs the real window for 20 s and prints the frame rate.
