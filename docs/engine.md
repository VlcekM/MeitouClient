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
| `src/Meitou.Rendering` | library | The world renderers, their streaming and the frame (`WorldFrame`). |
| `src/Meitou.Game` | exe `meitou` | The game: window, input, the loop; boots into the world. |
| `tools/Meitou.ModelViewer` | exe `meitou-viewer` | The debug viewer (meshes, characters, `--world`) on the same libraries. |
| `tools/Meitou.Tools` | exe `meitou-tools` | Surveys of the install; `image-diff` for renderer parity checks. |

Dependencies point one way: Core ← Data ← Engine / Rendering ← Game / viewer. The renderers know nothing of the engine
(the game copies the camera state into the renderers' `WorldCamera`), and the engine knows nothing of rendering.

## The game loop (`meitou`, `Meitou.Engine.Time`)

- **Fixed simulation tick** (`FixedStepClock`): 30 Hz by default (`--tick-rate`, or `tickRate` in the user config). Each
  displayed frame adds the real time since the last one to an accumulator and runs as many ticks as it holds, at most 10 (a long
  stall drops the rest instead of spiralling; `DroppedTicks` counts them). The tick count depends only on the summed time, not on
  how it was split into frames (tests `TimeTests`).
- **Interpolated drawing**: the camera keeps its state of the previous and the current tick (`Interpolated<CameraState>`); a frame
  draws `At(alpha)` with `alpha` = the accumulator's fraction of a tick, so motion is smooth at any display rate. Angles take
  the shortest way round; a camera mode switch is a cut.
- **Game time** (`GameClock`): advanced by ticks only; time scale 1, 2, 3 or 5 (`.` / `,`; Kenshi's speed buttons are 1×, 2×, 3×,
  5× is a common mod option), pause (`Space`). 150 game seconds per game hour at scale 1 (an engine choice until the game's own
  rate is traced; Unknown). The hour drives the sun, sky and lighting.
- **Input** (`Meitou.Engine.Input`): the host feeds an `InputState` (keys, buttons, mouse movement and wheel) from the
  windowing library's events; each tick consumes it once, so a press is seen by exactly one tick however frames and ticks fall.
  `InputBindings` maps keys and buttons to `InputAction`s; defaults below, overridable in the user config.
- **Frames**: vsync off by default with a frame limiter (default 240 fps; sleeps most of the frame and spins the last 1.5 ms);
  `--vsync`, `--fps-limit <n>` (0 = unlimited). The window opens maximized.
- **User config**: `meitou.user.json` (git-ignored) in the working directory, else next to the executable: `fpsLimit`, `vSync`,
  `tickRate`, `graphics` (the Tab panel's sliders by label) and `bindings` (action → comma-separated keys, e.g.
  `"RotateLeft": "Q,Left"`, buttons as `Mouse:Right`). Written on exit.

## The camera (`Meitou.Engine.Cameras`)

The strategy camera follows [formats/camera.md](formats/camera.md) (Verified facts in `KenshiCamera`): a pivot on the ground, a
boom of 10 to 2000 (start 150, pitched 30°), the zoom step `boom -= zoom speed · input · min(boom / 600, 0.75)` (zoom speed 125
from `settings.cfg`; one wheel notch = 1), the pitch step refused once the view direction's y is at or past −0.92 / 0.2, and the
eye kept `min(0.2 · boom, 20) + 10` above the ground under it. Engine choices: the pivot moves at 500 (`camera speed`) ×
`clamp(boom / 600, 0.1, 1)` units per second (the setting's unit is Unknown); keys turn at 1.5 rad/s; mouse drag 0.005 rad per
pixel; the pivot follows the terrain only (the game also stands it on buildings; not done). The free camera (`;`, the game's
`toggle_fps_camera`) flies with a smoothed velocity at 1.5 × the height above the ground; leaving it puts the pivot where the
view meets the ground.

Default keys: `W A S D` move, `Q`/`E` or `Left`/`Right` rotate, `Up`/`Down` pitch, wheel or `PageUp`/`PageDown` zoom, right or
middle drag orbit, `;` free camera (`R`/`F` up/down in it), `Space` pause, `.`/`,` time scale, `Tab` settings panel, `F12`
screenshot (temp folder), `Esc` quit.

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

The renderers call `IGl` (`src/Meitou.Rendering/Gpu/IGl.cs`): the exact subset of OpenGL 3.3 they use, with Silk.NET's
signatures and enums. It has two implementations, chosen with `--renderer gl|vulkan` (game and viewer; default `gl`):

- `GlPassthrough` forwards each call to OpenGL (generated by reflection); the pictures are OpenGL's own.
- `VkGl` (`src/Meitou.Rendering.Vulkan`) translates the calls onto Vulkan 1.3 as they come. See
  [../DECISIONS.md](../DECISIONS.md) (7) for why the interface is GL-shaped rather than Vulkan-shaped.

### Vulkan backend (`VkGl`)

- **Device** (`Core/VulkanDevice`): Vulkan 1.3 with dynamic rendering, synchronization2, timeline semaphores, host query
  reset, scalar/std430 uniform blocks, push descriptors, depth clip control and extended dynamic state; a block allocator
  (64 MB blocks), a frame ring (2 frames in flight, deferred deletion), a pipeline cache. `MEITOU_VK_VALIDATION=1` turns the
  validation layer on (the Vulkan SDK's layer must be installed).
- **Shaders** (`Shaders/`): the renderers' GLSL 3.30 compiled to SPIR-V by shaderc (relaxed Vulkan rules: loose uniforms in
  `gl_DefaultUniformBlock`, automatic bindings, vertex 0.., fragment 32..), varyings given locations by name, reflected from
  the SPIR-V (blocks, members, samplers, inputs). Compiled programs are cached on disk (`%LOCALAPPDATA%\Meitou\shader-cache`).
  At link the default blocks are moved to descriptor set 1 (a decoration patch) as dynamic uniform buffers.
- **Coordinates: nothing is flipped while drawing.** Vulkan's framebuffer row 0 is where GL's row 0 is (NDC y = −1), so render
  targets, `gl_FragCoord` and texture coordinates mean the same; GL's counter-clockwise front faces are clockwise in Vulkan
  (the sign of the area flips with the y direction); clip depth stays −1..1 (`VK_EXT_depth_clip_control`; without it a remap is
  compiled into the vertex shaders). The picture is flipped once, by the blit into the swapchain image (`VulkanPresenter`).
- **State.** GL state is tracked as GL defines it and turned into Vulkan at the draw: a pipeline per (program, vertex layout,
  primitive, attachment formats, blend, colour mask, polygon mode, alpha-to-coverage, depth clamp), created on first use;
  viewport, scissor, cull mode, front face, depth test/write/compare and depth bias are dynamic state.
- **Resources.** Set 0 holds the named uniform blocks and the samplers, pushed (`VK_KHR_push_descriptor`) only when they changed
  for the program; set 1 holds the loose uniforms, copied into a per-frame uniform ring when a `glUniform*` changed them and
  bound by dynamic offset. Samplers and views are cached on the texture until its parameters change.
- **Memory and order.** Every image stays in `GENERAL` layout; a full memory barrier before each render pass orders attachment
  writes, transfers and sampling. Uploads go into a command buffer submitted ahead of the frame's own; a static buffer the frame
  has already drawn from is renamed (new memory, old contents copied) before it is written, stream/dynamic buffers get a new
  version in host-visible per-frame memory; textures are written in place (an upload is seen by the whole frame).
  `glGenerateMipmap`, blits and readbacks are recorded in order in the frame.
- **Queries.** GL timestamp and elapsed-time queries are timestamps from a per-frame-slot pool (fresh entries for every issue);
  unread results are kept when the slot is reused. `VkGlStats` counts draws, passes, new pipelines, uploads, renames, pushes,
  the CPU time spent preparing draws, and the last frame's GPU time.
- **Known differences from GL** (parity ≤ 0.08 mean in all eight views): NVIDIA's GL samples anisotropic textures a quarter mip
  finer, matched with a −0.25 LOD bias on NVIDIA (DECISIONS 9); alpha-to-coverage edges differ slightly (DECISIONS 10);
  `DEPTH_COMPONENT24` is a 32-bit float depth buffer.

## Checking a change

- `tools/scripts/parity.sh <viewer exe> <out dir>` renders the eight reference views (The Hub from 40000, the rock at
  −51468,−14324, Port North, zone 14.30; at 13:00 and 02:00) offscreen at 1600×900; `parity-compare.sh <meitou-tools exe> <a> <b>`
  compares two such folders (`meitou-tools image-diff`: mean absolute difference in 0..255, share of pixels over 12, maximum, and
  a ×4 difference image).
- `meitou-viewer --world --fly-benchmark 1500 --size 1600x900 [--renderer vulkan] [--fly-pipelined]`: frame-time percentiles of
  a flight; serialized (the GPU waited for each frame, 60 fps pacing) or pipelined (two frames in flight, no pacing: the
  interval between frames).
- `meitou --screenshot out.png --ticks n`: boots the game offscreen, runs n ticks, saves the picture; `meitou --quit-after 20`
  runs the real window for 20 s and prints the frame rate.
