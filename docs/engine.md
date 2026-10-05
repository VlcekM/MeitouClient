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

Phase 1 keeps the renderers on OpenGL 3.3 through Silk.NET's `GL` object, unchanged (the parity check below reads 0.0000
against master). Phase 2 puts them behind a GL-shaped interface (the subset of GL calls the renderers use, with the same
signatures and enums) implemented twice: a pass-through to OpenGL and a translation onto Vulkan; see
[../DECISIONS.md](../DECISIONS.md) for why it is GL-shaped rather than Vulkan-shaped.

## Checking a change

- `tools/scripts/parity.sh <viewer exe> <out dir>` renders the eight reference views (The Hub from 40000, the rock at
  −51468,−14324, Port North, zone 14.30; at 13:00 and 02:00) offscreen at 1600×900; `parity-compare.sh <meitou-tools exe> <a> <b>`
  compares two such folders (`meitou-tools image-diff`: mean absolute difference in 0..255, share of pixels over 12, maximum, and
  a ×4 difference image).
- `meitou-viewer --world --town "The Hub" --fly-benchmark 1500 --size 1600x900`: frame-time percentiles of a flight.
- `meitou --screenshot out.png --ticks n`: boots the game offscreen, runs n ticks, saves the picture; `meitou --quit-after 20`
  runs the real window for 20 s and prints the frame rate.
