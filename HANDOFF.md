# Metal handoff — 2026-10-25

Repository: `/Users/daniilmiroshnykov/code/dotnet/MeitouClient`  
Branch: `metal-spike`  
Last code commit: `d25d951f fix: draw settings in the shared overlay pass`

State: functional opt-in Metal preview; **not 100% Vulkan effect parity**. Upstream v0.15.0 (`cd01e732`) is merged.
**MEP-1, MEP-2, MEP-3 and MEP-5 are accepted** (2026-10-24: MetalFX temporal stability; reflection MSAA/characters;
native bindless atmosphere-lit debris; native bindless water with foam shadows). **MEP-4 tessellation/terrain relief was
evaluated and removed**: it is not shipping, and Metal keeps the flat/parallax terrain path. Vulkan remains the
reference/default and the GPU cull stays off.

Build: Release 0 warnings / 0 errors. Current quick suite: 450 passed, 2 skipped. The 11 Metal upload tests still fail
before touching the engine while the host's `MTLCompilerService` XPC remains unhealthy after the 19:15 WindowServer
GPU timeout; they passed on 2026-10-23 before that crash and need a full shutdown, not a code change.

UI fixes (2026-10-25): Metal UI render-target images now flip V on both backends, the UI scissor uses Metal's
top-left origin, and Metal uses the shared character draw list. Portraits, item icons and the editor stage are
upright; editor race/gender/slider changes show live; the trader stock grid and HUD name plates sit in their
windows. Verified by the user at 2560x1600.

Terrain relief LOD (2026-10-25): the parallax march now uses 8 view and 4 sun steps near (4 and 2 at the parallax
end), the three heaviest ground layers, and 4 steps on the displaced-detail path. The Metal town capture had the
interior-cut draw at 11.25 ms of a 31.03 ms frame; with the march off it was 4.28 ms and 19.75 ms, and the 6.2 KB
register spill fell to 48 B. A 45 s Border Zone smoke with the LOD ran at 20.3 ms GPU with relief on.

Player quality controls (2026-10-25): **Ground relief quality** (Low/Medium/High: one/two/three layers, 4/6/8 view and
2/3/4 sun steps near) and **Light shafts quality** (96/128/160 cells, 32/48/64 slices, 1/2/2 samples) are saved settings in
the launcher, the Tab/Metal panels and the native settings. The quality presets scale both; Low turns ground relief and its
tessellation effects off, and `--low-end` matches. The Metal smoke at High held 20.8 ms GPU at Border Zone; Low (both set to
0) held 19.5 ms, confirming the live uniform path.

Metal overlay coalescing (2026-10-25): the HUD, the direct HUD lines and the stats/settings overlays now share one
`DebugOverlay.OverlayPass`, so they load/store the 3024×1712 backbuffer once instead of in separate render encoders.
The final MetalFX/post upscale remains its own pass; merging it would require keeping the post chain's render pass open
across the HUD. Build 0/0; quick suite 450 passed / 2 skipped; internal HUD screenshot verified. No new Xcode capture.

Metal binding fixes (2026-10-25): the post-TAA reactive pass now rebinds `uNearDepth`/`uFarDepth` before its second
fullscreen encoder, and the shadow blocker pass binds its atlas for every layer. Xcode had flagged those as missing
fragment texture bindings; the affected passes are 50.64 µs and 62.25 µs, so this is API correctness, not a frame-time
win. Build 0/0; quick suite 450 passed / 2 skipped; internal screenshot runs. No new Xcode capture to confirm the
missing-binding rows are gone.

Metal memory targets (2026-10-25): the post-TAA history is allocated only when TAA is the fallback, the composite's
LDR/FXAA/SMAA targets are allocated only when the selected path needs them, and the 8192² overview map is no longer
prefetched (it loads when the map first opens). This avoids the never-bound 2 × 40.5 MiB TAA histories, 2 × 20.25 MiB
SMAA targets, 20.25 MiB FXAA target and 32 MiB map prefetch in the 13:38 trace — about 174 MiB, before the driver's own
26 MiB. Build 0/0; quick suite 450 passed / 2 skipped; internal screenshot runs. No new Xcode capture.

Metal binding cache (2026-10-25): `MetalCommandList` now uses `setVertexBufferOffset`/`setFragmentBufferOffset` when the
same buffer moves to another slice, and caches texture and sampler state per slot. This targets Xcode's redundant-binding
API insights (324 buffer binds in Render Encoder 16; 85 buffer and 83 sampler binds in Render Encoder 28). It is API/CPU
hygiene, not a GPU-time claim. Build 0/0; quick suite 450 passed / 2 skipped; internal screenshot runs. No new capture.
The 14:50 trace showed API Usage down from 637 to 57 insights; the remaining post-TAA `uFarDepth` row was the dummy far
slot, now bound to the real depth (unused with `uHasFar = 0`). Local commit `a12f8360`; no new capture.

Metal set/push reuse (2026-10-25): descriptor argument buffers are now slices of the frame constants ring instead of
16-byte `MTLBuffer`s created per bind, and push-constant blocks are cached per program/stage and skipped when the full
block is unchanged. This targets the late-`MTLBuffer` rows (the tiny values were resource addresses, not lamp colour)
and the 20 redundant `setVertexBytes`/`setFragmentBytes` rows in Render Encoder 6. API/CPU cleanup, not a GPU-time claim.
Build 0/0; quick suite 450 passed / 2 skipped; internal screenshot runs. No new capture.

Metal pointer layering (2026-10-25): the in-game pointer now appends to the shared overlay pass after the HUD, map,
inventory and settings. The overlay coalescing had delayed that pass until the end of the frame while the pointer still
flushed its own encoder immediately, so the UI covered the cursor. Build 0/0; quick suite 450 passed / 2 skipped; no new
capture. Needs a live check with the map/inventory open.

Metal settings layering (2026-10-25): `SettingsMenu.Draw` now accepts the shared `OverlayPass`, so the settings window is
appended after the HUD and escape menu instead of being flushed before them. This fixes the settings window sitting
behind the pause menu. Build 0/0; quick suite 450 passed / 2 skipped; live check needed with Options open.

## Upstream sync (2026-10-25, `cd01e732`)

`metal-spike` merges upstream v0.15.0 (`cd01e732`, 166 commits since `57a7968a`): the HUD town panel and outpost
settings, house purchase and housed squads, the build readout and construction-panel changes, rotating parts, tick
ahead, the play benchmark, Steam app-id handling in the launcher, and the simulation/UI fixes in that release. The
shared code is upstream's; the Metal adaptations are:

- `TerrainRenderer` keeps the seam (`IGpuContext`, `GpuSetLayout`) and now caches upstream's march/no-march colour
  sets. The no-normal variants remain compile-time programs for Metal, and the cut patches use upstream's
  `LateEqual` path.
- `UiPainter` ports upstream's S3TC DDS prefetch to `GpuTexture`/`PixelRect`; `ImageFlipV` and the Metal top-left
  scissor stay. The overview map uploads compressed instead of decoding it in the opening frame.
- `GameHost.Metal` follows the shared `tickAhead` setting around `WorldFrame.Draw`, runs the rotor update through
  `UpdateBuildings`, and takes the new town/build/readout interface callbacks through `ConfigurePlayerInterface`.
  `MetalContext.Finish` tolerates a still that never opened a frame.
- The launcher keeps the Metal/Auto/Vulkan selector and Metal memory budget while taking upstream's Steam app-id
  handling and Debug-build selection.

Validation: Release build 0 warnings / 0 errors. Quick suite: 450 passed, 2 skipped. The full suite ran 613 tests:
585 passed, 2 skipped, 26 failed. Of the failures, 24 are Vulkan/GPU tests that cannot find `libvulkan.1.dylib` on this
host (and the Vulkan smoke is blocked by GLFW's "doesn't support Vulkan" error); the 2 `HousePurchaseUiTests` fail at
line 61 on clean `origin/master` too. A 30 s `--new-game Wanderer` Metal run and a 15 s tick-ahead run completed
without a GPU fault; HUD, inventory and overview stills were written at 2560x1600 and show upright icons, the
inventory grid and the name plates. The trade still path still finds no trader (pre-existing), so live trade remains
the verification path.

## Upstream sync (2026-10-24, `57a7968a`)

`metal-spike` now merges upstream `57a7968a` (131 commits since `a9970dbc`), including player-built buildings and
their interior/floor rules, patrols, campfires/torches/smoke, the construction and building panels, item-icon lighting
and caching, the new audio voice setting, and the terrain cut/outline work. The simulation and shared UI are unchanged
in structure; the renderer-side adaptations are:

- `MarkerRenderer` replaces `MoveMarkerRenderer`: the movement marker keeps its old path and the new stealth arrows use a
  rotation uniform. Both the Vulkan HUD and the Metal HUD now set `Markers`, and the shared frame draws `gpu.Markers`.
- `CharacterMaterialTable` is adapted to `IGpuContext` (the same records for both backends; Metal and Vulkan upload the
  dirty records in an upload batch). `CharacterRenderer.Draw` still uses the seam's `GpuProgram`/`GpuBufferBinding` and the
  preallocated per-view instance rows, so Metal does not need the Vulkan descriptor layout.
- Terrain decals now use the upstream instanced `DecalInstance` path and separate colour/alpha blending.
  `MetalPipelines` honours `BlendState.AlphaSrc`/`AlphaDst`, so Metal gets the game's additive alpha without the old
  two-pass workaround.
- Terrain cut relief and the terrain-outline refinement remain tessellation-only; Metal reports
  `HasTessellation == false` and keeps the flat/parallax path. A 2026-10-24 MEP-4 prototype was removed (see below),
  so this remains a known limitation. The new `TerrainShaders.ReliefDepth` text is in the shared fragment source but
  is not selected on Metal.
- Upstream's Audio settings now own the voice cap (`Sound voices` -> `AudioSettings.Voices`). The earlier Metal-only
  `MeitouVoices` toggle was removed so there is one setting; the shared `SettingsMenu` exposes it on both backends.
- `FillWorldDrawList` (the Metal no-interface path) now passes the reusable ragdoll buffer to `Blend`, as the shared
  draw list does.
- Metal stills no longer call `PrepareStill` before a frame exists: texture settling needs an open Metal frame, so it
  runs on the first frame and skips `WorldFrame.FinishLoading` (which calls `Finish` and closes that frame). This fixes
  `no frame slot` / `frame closed` crashes in `--screenshot --ui-panel ...` after upstream queued more object textures.

Validation on 2026-10-24: Release build 0 warnings / 0 errors. Quick non-Metal suite: 543 passed, 24 skipped, 1 known
upstream failure (`ItemIconRenderTests` reports 0.964 clothing overlap against its 0.99 threshold; clean
`origin/master` reports 0.964 too), and the Metal upload tests remain excluded while `MTLCompilerService` is unhealthy.
Metal smoke runs at the Hub and in the duel completed without a GPU timeout; the `--ui-panel inventory` screenshot was
written at 1600x1200. The frame rate in these runs was below the earlier clean-machine acceptance run, so re-measure
after a reboot and with the launcher's 8120 MB preset. Visual acceptance of the new arrows, blood decals, icons,
building effects and construction panels is still manual.

## Effect parity audit (2026-10-24)

This is **not 100% Vulkan effect parity**. The shared frame covers most effects, but the following are still
Metal-specific fallbacks or absent paths:

| Area | Vulkan effect | Metal status |
|---|---|---|
| Terrain | Ground displacement, rock smoothing, terrain-outline refinement (tessellation) | **absent**; `HasTessellation == false`, so only flat patches + parallax. A MEP-4 prototype was removed (performance/fallback failure) |
| Water | Meitou water lighting, foam sun/cloud shadows | **parity** through the native bindless atmosphere path (MEP-5 accepted); waves, foam, caustics, reflection and refraction are present; reflection uses the shared 4x MSAA/resolve path |
| Debris | Atmosphere-lit + hazed | **atmosphere-lit and hazed** through the bindless native path (MEP-3 accepted) |
| Particles | Near-plane close pass for large sprites crossing the camera | **disabled** on Metal; normal-depth and low-resolution particles work |
| Foliage | Generated rock levels + GPU mesh/rock cull | **CPU cull**; generated levels are tied to the GPU path, so the Metal rock LOD detail/perf differs |
| Post | SMAA, FogRate/VRS, particle coverage in the upscaler's reactive mask, sun-motion reprojection | **SMAA and sun-motion reprojection present**; FogRate/VRS is absent (performance-only); MetalFX gets no particle coverage (far-object shimmer), TAA keeps full- and low-resolution particles; TAA is experimental |
| GI | `GiScene`/`GiProbes` when Vulkan ray queries exist | **no default path**; `MetalProbeGi` is experimental and opt-in; macOS Vulkan reports no ray queries, so local no-GI parity holds |
| Temporal | Full TAA including character/grass motion | MetalFX is the default; TAA is exposed as experimental and still ghosts |
| Occlusion | Hi-Z foliage occlusion culling | **not implemented**; CPU cull remains correct but slower |

The **new upstream effects are wired**: building effects (campfires, torches, furnace smoke), blood decals, item
icons, construction/interior UI, patrols and audio voices use the shared simulation/render/UI paths. The new
`MarkerRenderer` movement marker and stealth arrows work on both. Terrain cut relief is tessellation-only and therefore
falls under the first row.

**Ship decision:** suitable for an **opt-in Metal test/preview** now (Vulkan remains the default and reference), but
not for a “Metal is at 100% parity” or default-release claim. The remaining visible gap is tessellated terrain;
near-camera particles are still absent. Water lighting/foam shadows and debris lighting/haze are accepted. Tessellated
terrain is a permanent known limitation of this preview, not a pending toggle. GI/temporal/FogRate/Hi-Z are
quality/performance follow-ups unless the test explicitly requires them. Keep the Metal UI hiding the tessellation
toggles and the experimental GPU cull, and label TAA experimental.

Post-merge smoke: 120 s `--new-game Wanderer` at 800x600, 8120 MB budget, GPU cull off: 6,693 frames / 56 fps, no
GPU timeout. This is not a substitute for the 30-minute acceptance soak; the full Metal test suite is still blocked by
the host's `MTLCompilerService` XPC.

## Next iteration: Metal Effects Parity I (MEP-I)

The plan is in [docs/metal-effects-parity-plan.md](docs/metal-effects-parity-plan.md): post effects (MEP-1),
reflection quality (MEP-2), debris lighting (MEP-3), tessellation/terrain relief (MEP-4, evaluated and removed), and
water lighting (MEP-5, accepted). MEP-3 is the preview-test gate; water is now accepted too. Vulkan stays the
default/reference, the GPU cull stays off, and MEP-I covers visual effects only.

**MEP-4 was evaluated and removed (2026-10-24).** Metal has no tessellation control stage, so a prototype wrote
explicit triangle factors and used post-tessellation vertex stages. CPU factor generation cost ~103 ms/frame for
terrain plus ~25 ms/frame for rocks (3 fps); moving the factors to GPU compute raised the loaded Hub view to ~35 fps
against the ~54 fps MEP-3 baseline. The deferred-colour path then hit `NotSupportedException: no Metal primitive for
PatchList` when a factor allocation failed and the job fell back to an indexed draw under a patch-topology pipeline.
Rather than ship a visible, fragile, performance-negative path, the code was removed; `HasTessellation` is false again
and the tessellation toggles stay hidden. Revisiting it needs a hard performance budget, near-range/quality caps,
per-patch flat fallback and full validation (see the plan).

**MEP-5 water is accepted (2026-10-24).** `WaterRenderer` now uses the native model on both backends: one
`WaterConstants` block and all water, height, ocean/shore/river, reflection and refraction textures in the bindless
table. The `METAL_WATER` fragment variant, `BindMetalSamplers` and the packed two-layer parameter map are gone; the
full shared atmosphere path is compiled, foam reads `kenshiShadow`, and the water programs have 0 direct fragment
samplers. Reflection/refraction and the MEP-2 MSAA decision are unchanged. The user accepted the in-game swamp and
desert water; screenshot runs compiled and rendered both Metal views without a shader or GPU error. The 30-minute
post-merge soak remains a separate gate.

**MEP-1 is accepted:** particle coverage in TAA's motion alpha, the shared sun-motion tracker, the Metal 0..1
velocity-depth fix, the MetalFX y-down motion convention, camera-cut resets and SMAA in `PostComposite`. MetalFX
gets **no particle coverage**: full-size rain/dust made distant objects shimmer, so both full- and low-resolution
coverage now stay TAA-only. FogRate remains a documented performance-only difference (Metal has no VRS attachment).

**MEP-2 reflection quality is accepted.** `IGpuContext.MaxSampleCount` supplies the device's sample-count capability
(Vulkan limits / Metal `supportsTextureSampleCount`); `ReflectionPass` builds multisampled colour+depth targets on
Metal too, uses private storage for them and resolves colour before the water samples it. The reflection fog reads
sample 0 of the multisample depth. Level 2 reflects characters on both backends (previously the shared pass drew only
terrain/objects/foliage); levels 1-4 are terrain, characters, placed objects and foliage. `--reflection-samples <n>`
selects the count (1 disables MSAA for the A/B).

**MEP-3 debris lighting is accepted.** `DebrisRenderer` is a native bindless program on both backends: the full
`kenshiLight`/`atmoApply` fragment reads frame, shadow, GI, lamp and debris textures from the bindless table, while a
128-byte push block carries the camera-relative model, simple-sky fallback light and per-part diffuse/cut-out state.
Metal's old simple sun/ambient debris fragment is gone. `SamplerBudget` reports/checks the direct fragment-sampler
count (debris and water both have 0). The Hub/desert gameplay check passed; bone/limb visibility was
not separately exercised before the test character died, so luckyren Mode remains the quick explicit debris check.

## Upstream sync (2026-10-23)

`963c611b` merges upstream `a9970dbc` (177 commits, including the gameplay/UI/audio work) into the Metal branch. The
new character previews and skin scattering were ported to the `IGpuContext`/`GpuTexture` seam: `CharacterPreview`
resolves half-float stages and portraits into the 8-bit atlases, `SkinScattering` bakes its LUT through the upload batch,
and the Metal host now sets `Look`, `SkinShading`, `SkinRaces`, `EnhancedIcons` and the pose clock. The Vulkan
tessellation path (ground displacement and rock smoothing) is represented in the seam as `GpuProgram.NativeTessellated`
and patch-list pipelines; Metal reports `HasTessellation == false`, hides both toggles, and keeps the flat/parallax
fallback. Ground parallax and the new skin/icon/voice settings are exposed in the Metal settings panel.

The upstream Vulkan-only post changes (the SSAO radius fix and the checkerboard low-resolution particle depth) are not
in Metal's custom post chain yet; nor are upstream's TAA sharpness and other Vulkan composite details. These are the
next parity items after the merged build is run on the M1 Pro. Metal still needs a real visual pass after the merge:
the previews, skin LUT, terrain height arrays and ground parallax compile but were not exercised on the host after the
WindowServer crash.

### 2026-10-23 21:32 Metal progress timeout

The first merged full-screen run (`--renderer metal --windowed --size 800x600`, then full screen,
`MEITOU_METAL_GPU_BUDGET_MB=7168`) produced a `gpuEvent` progress timeout at 21:32:29 and a WindowServer watchdog
restart at 21:36:46. The game process was left in `?E`/zombie state; the unified log was unavailable (`logd` broken with
the same opendirectory failure).

A second run (`MEITOU_METAL_GPU_BUDGET_MB=5120`, ground relief off, `--windowed --size 800x600`) also ended in a
WindowServer watchdog at 21:48:46. Its log shows the window growing to 3024x1712 (so it was full screen by then),
MetalFX temporal 0.67, and two ANE inference timeouts immediately before the watchdog. The date/conditions changed
from the first run, so this does not exonerate the 21:32 optimization set on its own.

The Metal defaults were then narrowed to the stable set:

- GPU foliage cull and its separate pre-frame command buffer: **off and not user-selectable**; `MEITOU_METAL_GPU_CULL=1`
  remains a diagnostic and prints a warning. The launcher clears any inherited switch.
- Encoder bind/state caching: **on** (`MEITOU_METAL_NO_STATE_CACHE=1` for an A/B).
- Lossless target compression / base-view reuse: **on** (`MEITOU_METAL_NO_COMPRESSION=1` for an A/B).
- Ground relief / parallax: **on** (`MEITOU_METAL_GROUND_RELIEF=0` for an A/B).

The CPU cull is the default reference. The launcher exposes a soft Metal memory budget in Settings: Safe 5120 MB (default),
Balanced 6144 MB, High 8120 MB only on 16 GB-or-larger Macs. It passes `MEITOU_METAL_GPU_BUDGET_MB` only for Metal and
does not expose the cull. The game warns above 5120 MB; the 8120 MB preset was stable in the 30-minute acceptance run.

**2026-10-24 13:14, after a full shutdown:** the safe path ran at **3024x1712 full-size**, MetalFX temporal 0.67 and
the 5120 MB budget with ground relief, GPU cull, state cache and compression off: 5039 frames in 175.8 s, 29 fps, no
watchdog or zombie process. That rules out MetalFX temporal 0.67 and the final resolution on their own; it leaves the
optimization set or the stale GPU/WindowServer state from the first crash as the cause.

**2026-10-24 13:26:** the first A/B, `MEITOU_METAL_GPU_CULL=1` only, reproduced the crash after about two minutes
(WindowServer watchdog, `MeitouClient` left `?E`). The shell's `tee` log had only the "no such file" line because it was
run from the wrong directory, so the last pass is unknown, but the A/B is decisive: the GPU cull (or its separate
pre-frame command buffer) is the trigger. The cull path is now opt-in and prints a warning; the CPU cull is the
default. The next debugging step is Metal's indirect draw: the cull writes `MTLDrawIndexedPrimitivesIndirectArguments`
with a non-zero `baseInstance`, while the stable CPU path uses direct `DrawIndexed` with `firstInstance`. Verify that
Apple silicon supports that indirect field (or replace it with per-draw vertex-buffer offsets/CPU compacted draws)
before enabling the cull again. Do not use Xcode replay.

**2026-10-24 13:52, 30-minute acceptance:** `MEITOU_METAL_FAST_STATE=1`, `MEITOU_METAL_COMPRESSION=1`,
`MEITOU_METAL_GROUND_RELIEF=1`, budget 8120 MB, **GPU cull off**: stable for half an hour, including full-size use. This
is the first-iteration baseline: state cache, compression and ground relief on; CPU cull; a user-selectable soft budget;
no cull switch exposed. MetalFX temporal 0.67 was in use and stable, so it is not part of the regression.

Matched profiling (2026-10-23, 800x600 points = 1600x1200 pixels, native/no AA, same view): Metal with the GPU
mesh/rock cull ~46 fps steady (command-buffer GPU 21.7 ms, record 4.8 ms); the CPU cull is ~44 fps and 22.6 ms.
Vulkan is ~41-42 fps (GPU 22.3 ms, world draw CPU 6.0 ms). Vulkan's GPU time is dominated by terrain (13.75 ms);
particles, post, cull and shadows are 1-2 ms each. At 400x300 points Metal is ~89 fps and Vulkan ~82 fps. An early
Metal capture showed zero compute encoders: the live-enable line was missing from the committed renderer, so it was a
CPU-cull frame. The line is now committed; the fresh capture has two command buffers and compute encoders. Its pre-frame is 0.91 ms
(2.84%); the one captured frame is 32.15 ms, with the main scene 22.3 ms and terrain 11.3 ms. The earlier 18.79%
terrain-shadow sweep was a rebuild frame. An early Vulkan comparison was invalid because `--windowed` maximized it
despite `--size`; fixed. `--log-spikes` prints the Vulkan per-stage run summary at exit.

## Deterministic Metal tests

`tests/Meitou.Tests/Metal` adds a windowless Metal test device and 16 deterministic tests, including
`MetalFoliageCullTests` (the cull/scan/compact chain against `FoliageCull`). They run on macOS only with the normal
quick suite:

```bash
dotnet test tests/Meitou.Tests/Meitou.Tests.csproj -c Release \
  --filter "Category!=Slow&Category!=Bench"
```

The tests cover upload sub-rectangle packing, odd-width R8/R16 row padding, texture and buffer bounds, buffer
offsets, mip-level and array-layer bounds, texture-view caching, live resource-table counts after release, and the
frame-ring deferred-release window/budget. They found and fixed one new boundary bug: `MetalUploads.Write` did not
reject a mip level at or above `TextureDesc.Levels`.

## Current state

Metal now runs with terrain textures, water, foliage, terrain shadows, cloud shadows and far impostors **enabled by
default**.

Diagnostic opt-outs:

```text
MEITOU_METAL_FOLIAGE=0
MEITOU_METAL_WATER=0
MEITOU_METAL_SHADOWS=0
MEITOU_METAL_CLOUD_SHADOWS=0
MEITOU_METAL_NO_TERRAIN_TEXTURES=1
MEITOU_METAL_NO_IMPOSTORS=1
MEITOU_METAL_CPU_CULL=1
MEITOU_TERRAIN_NO_NORMAL=1
MEITOU_METAL_GROUND_RELIEF=0   (disables the default upstream parallax)
MEITOU_METAL_GPU_CULL=1        (experimental GPU cull + separate pre-frame; unsafe on Metal, not exposed)
MEITOU_METAL_NO_STATE_CACHE=1  (the default encoder bind/state cache off)
MEITOU_METAL_NO_COMPRESSION=1  (the default lossless target compression/base views off)
```

The terrain no-normal switch selects the compile-time variant (the viewer's `N`/`normal-maps` toggle does the same).
`MEITOU_METAL_FAST_MATH=0` is a diagnostic only: Metal's default already has fast math on.

The previous safe mode is no longer needed for the black-terrain/water/foliage failures.

## Launcher (macOS)

`launcher/Meitou.Launcher` is the Avalonia launcher documented in [docs/launcher.md](docs/launcher.md); the release
workflow already builds `osx-arm64` and `osx-x64` packages (README has the download links). First start and Settings
offer **Auto**, **Vulkan (MoltenVK)** and **Metal** on macOS; Auto uses Vulkan when its loader is present and Metal
otherwise, so an Apple-silicon user needs no Homebrew just to start. Metal starts the game with `--renderer metal`,
removes `DYLD_LIBRARY_PATH` and needs no MoltenVK. The choice lives in `launcher.json` and is appended after Launch
arguments, so it overrides a hand-written `--renderer`. The launcher reads the release's `settings-schema.json`
`renderers` list; a Vulkan-only release does not claim Metal support. First start also explains that Kenshi is required
and links to [docs/kenshi-on-macos.md](docs/kenshi-on-macos.md). Vulkan now gets a narrow
`~/Library/Application Support/Meitou/vulkan/` loader folder through `DYLD_LIBRARY_PATH` (symlinks to the loader and
MoltenVK) instead of all of `/opt/homebrew/lib`, so Homebrew `libjpeg`/`libpng` cannot replace AppKit/ImageIO's copies.
The launcher runs installed GitHub releases, not a local checkout; a release without the Metal backend reports the
game's error in the status line.

A debug `dotnet run` from the agent shell on 2026-10-23 failed before showing a window with Avalonia.Native
`RenderTimer` error `-6661` (no active display/WindowServer session after the crash); the Release build is 0 warnings /
0 errors. Run it from the user's terminal or the packaged app after a full shutdown.

## Shared WorldFrame/SceneHost path

`4791a496` moved the Metal host onto the shared renderer path:

- `SceneHost` now takes `IGpuContext`; the seam carries `BeginHostPassParallel`, `SetPassStage`, `EndHostPassParallel`
  and `ClearDepth`, so Vulkan keeps its secondary-buffer staging and Metal records inline.
- Metal's scene pass uses the same `SceneHost` as Vulkan.
- `WorldFrame.CreateGpu` takes `IGpuContext` and builds terrain, textures, streamer, sky/weather, particles, lamps,
  objects, foliage, water, shadows and the memory guard on both backends. The full `PostProcess` and GI are
  Vulkan-only; the reflection pass and Meitou water are shared. Metal supplies its allocation sample and its own
  post chain, and now shares `WorldFrame.DrawShadows`.
- `GameHost.Metal` populates the shared `gpu` object, so the shared title/audio/save/editor helpers see the same
  weather, objects, foliage, characters and lamps. The character-editor warm-up path has an explicit Metal guard.
- The old direct construction was removed from `GameHost.Metal`; `WorldFrame.Gpu.Dispose` now owns those renderers.

Verification: Metal screenshots at `-16134,26409` and a 20-second loaded-save run (451 frames, 22 fps) are clean;
the Vulkan reference screenshot at the same view runs; quick tests 476 passed / 2 skipped; build 0 warnings / 0 errors.

## Content/testability Milestone A (2026-10-22)

The Metal host now wires the content test paths tracked in
[docs/metal-content-testability-plan.md](docs/metal-content-testability-plan.md):

- `--sandbox` spawns the real sandbox actors and wires its keys, tick and panel; `--duel` starts
  the measured duel and restarts a lap on Backspace; `--crowd` now spawns simulated characters
  (`SpawnCrowd`), not the posed renderer harness; startup `--new-game` enqueues `FinishCreation`.
- F10 remains the settings panel. **Ctrl+F10** opens the shared debug menu.
- `FollowCamera`, `SandboxFollowCamera` and `EditorCamera` run each frame; selection rings, the
  numpad `/` squad tour, character flights and free-camera camera-code copy/paste are wired.
- `WorldNews()` drains relation, knockout, town, wild and aggro messages to the HUD/log.

Accepted by the user in a normal Metal `--new-game Wanderer` run (352 s, about 35 fps): startup,
HUD, discovery messages and camera follow.

## Content/testability Milestones B-C (2026-10-22)

- `SetUpBlood` now takes `IGpuContext`; Metal calls it after the shared world renderers exist and
  before a loaded save's decals are read. `UpdateBlood` runs before every `WorldFrame.Draw`, so
  blood marks, decal ageing, luckyren Mode and debris advance in normal play and stills.
  `WorldFrame.Gpu.Dispose` already owns `Decals` and `Debris`; the decal store is managed data.
- `PrepareStill(IGpuContext)` is shared by Vulkan and Metal: it selects the showcase leader, issues
  `--move-to`, runs `--ticks` of control and simulation time (and duel/title per-tick work), puts
  the camera on the walker and settles the streamers. The Metal still loop freezes the simulation
  after that and captures the same panel set as Vulkan, including `--ui-showcase` output names.
- `--ui-panel inventory`, `stats`, editor panels, trade, talk and the other shared layouts use the
  same `ShowUiPanel` calls on Metal. Unsupported `trade`/`talk` panels are skipped when the shared
  showcase cannot open them.
- The first Metal run crashed compiling debris: the full atmosphere lighting plus its diffuse map
  is 17 direct fragment samplers (Metal allows 16). The temporary simple sun/ambient Metal fragment
  was superseded by MEP-3's native bindless debris path; the full `kenshiLight`/`atmoApply` fragment
  now runs on both backends.

Build after B-C: Release 0 warnings / 0 errors; quick suite 476 passed / 2 skipped. Manual Metal
validation: blood decals were confirmed by the user on 2026-10-22; debris/luckyren and the
deterministic still commands remain to check.

## Shared frame draw

`WorldFrame.Draw` now runs behind `IWorldPostChain` (`WorldPostChain.cs`). Vulkan's `PostProcess` implements it;
Metal's `MetalPostChain` wraps `PostComposite` + `PostTaa` and supplies the scene colour, the scene depth, the
jitter/temporal choice and the scene effects. Metal's host no longer has its local scene/post loop: it sets up the
title staging, portrait/hover inputs and UI, then calls `WorldFrame.Draw` (which also draws the shared overlays).

The chain reports what it lacks: Metal has no GI; the shared draw skips that path. Both draw the scene over one depth
(Vulkan reversed, Metal conventional). The reflection pass, Meitou water and low-resolution particles run on both.

Verification for this slice: Release build 0 warnings / 0 errors; quick suite 476 passed / 2 skipped.
The normal Metal `--new-game Wanderer` run was accepted by the user on 2026-10-22; title, editor,
overlays, resize and settings were exercised in the earlier acceptance passes.

Current gaps are the audit above: MEP-4 tessellation (known limitation) and near-camera particles. GPU culls,
presentation/HDR, UI acceptance and the default switch remain separate work.

## Metal ray-tracing probe

`src/Meitou.MetalSpike --rt-bench` now builds a 524k-triangle terrain acceleration structure and traces the
DDGI-sized probe batch (32 rays/probe, 1/4 of 16,384 probes = 131,072 rays) on Metal. On this M1 Pro the batch
measured about 0.25-0.31 ms/frame; the structure took about 75-100 ms to build and used 88 MB plus 97 MB scratch.
A 1280x720 camera-ray debug image is written to the temp directory.

This is traversal only: no objects, foliage, TLAS, refit, probe accumulation or resolve/apply. The probe looks
promising for the M1 Pro; full `GiScene`/`GiProbes` scene build and memory remain the decision point.
The in-game Metal GI is now opt-in (`MEITOU_METAL_GI=1`): a save load with it enabled froze the test M1 Pro,
so normal launches keep it off.

## The fix

The black/miscoloured terrain and the acid-pool water GPU fault came from one Metal upload bug.

`MetalUploads.Write` repacks a texture sub-rectangle into full-width rows. It was copying the tile at `region.X` in the
source row **and** passing `destinationOrigin.x = region.X` to the blit, applying the offset twice. That shifted every
terrain overlay/colour-map piece and left zeroed texels at the left edge. The fix places the tile at offset zero in the
packed source row (`00164c75`).

This also removed the GPU errors and foliage hang seen in the smoke runs after the same path was exercised.

A second bug caused the dark/bright title-screen flicker: auto-exposure uses two ping-pong targets. Only one was seeded on the first frame, and the adaptation read the target it was writing instead of the previous frame's output. Both targets are seeded before smoothing and the pass now reads the previous output (`b125a8d7`, `95a39d3b`). A per-frame title probe after the second fix stayed at mean 103.6 -> 103.5 with no alternation.

**Fixed: sustained full-feature streaming GPU hang.** The Vulkan backend's `FrameRing.BeginFrame` waits on the slot's fence before reusing per-frame memory or running deletions. Metal was retiring by frame number alone. `MetalContext.BeginFrame` now waits for the command buffer of the slot it is about to reuse (`(slot + 1) % FrameRingDepth`) before releasing retired resources and resetting the slot. This keeps three frames in flight, unlike the serial one-frame diagnostic. The env switch `MEITOU_METAL_SLOT_WAIT=0` restores the old behavior for A/B diagnosis; `MEITOU_METAL_SLOT_WAIT=serial` waits for every previous frame instead.

Acceptance after the change: three full-feature spatial-0.4 auto-camera runs: 120 s serial, 120 s reused-slot, and **180 s reused-slot**; all completed with no `Caused GPU Hang`, no command-buffer failure and no upload failure. The 180 s run was 6221 frames (35 fps). The old full-feature path failed around 36-43 s. User acceptance (2026-10-22): checked all settings, no glitches. The remaining known issue is temporal MetalFX 0.67, which hangs before the first frame with a loaded save; spatial 0.40 is the stable path. Rare one-frame visual glitches may be separate and still need a frame-accurate incident capture.

### Profiling attempts

- `xcrun xctrace record --template 'Metal System Trace' ... --attach` fails before recording with `Failed starting ktrace session` / `Couriers have returned unexpectedly`, including under `sudo`. The host blocks kernel tracing; no usable `.trace` is produced.
- In-process `MTLCaptureManager` is a manual one-frame capture behind **Ctrl+/** (`MetalGpuCapture`). It starts only after the first rendered frame and stops after the present, writing a `.gputrace` under `~/Documents/Meitou Captures`. The earlier 14 GB attempt started before the first frame and swallowed the loading uploads; the guard is deliberate. Whole-device captures can still be large, so keep the window small and check free space. **Do not automate captures**; the unattended `MEITOU_METAL_CAPTURE_AT` trigger was removed after a capture-related system restart. **Do not open/replay a trace in Xcode while the game is running.** On 2026-10-23 a 2.6 GB trace, the game at ~7.6 GB (`MEITOU_METAL_GPU_BUDGET_MB=7168`), Xcode's ~2 GB `MTLReplayer`, and a 16 GB M1 Pro produced a WindowServer GPU progress timeout and a reboot. Xcode/MTLReplayer GPU events at 12:58 and 16:48 predate the latest renderer commits. Prefer the steady trace and stage counters; if a capture is unavoidable, quit the game first and use a smaller budget/scene.
- Pass labels show every pass completes recording (`scene`, `terrain`, `objects`, foliage, post, UI); the GPU error is reported at frame-slot completion, so pass labels alone cannot identify the hung draw. The remaining useful diagnostic is per-pass command buffers or completion fences.
- Next diagnostic: encode an `MTLSharedEvent` signal after each named pass and read `SignaledValue` when a command buffer reports a hang/error. The last signaled pass is the last one that completed; the next pass is the suspect. This writes no trace and keeps a deterministic run to one output line.

Verified clean:

- `-16134,26409` (water/acid pools): terrain and water normal.
- `-44047,250` (previous black terrain): normal.
- `-12213,36572` (previous black terrain): normal.
- `-10,-8899` and `-66800,-41400`: normal.
- Recenter path via `--fly-to` from `-66800,-41400` to `-44047,250`: final map window covers the destination and the
  terrain stays textured.
- 15-second all-feature live runs at `-16134,26409`: stable, no GPU errors; ~12 fps Native.
- 15-second run with water/shadows/cloud shadows on: stable; ~12 fps.

## Next work

1. **MEP-4 is closed as not shipping; MEP-5 is accepted.** Do not re-enable the tessellation prototype; keep the
   flat/parallax path and the hidden toggles. MEP-3 passed the Hub/desert gameplay check (luckyren remains an optional
   limb-visibility check). Do not start GPU culling or default-switch work until the 30-minute soak and the parity /
   default decision are settled.
2. Finish the GPU draw path on Metal: `FoliageGpuCull` is live for meshes/rocks, but `FoliageGrassGpu` and
   `HizPyramid` are still Vulkan-only (grass uses the CPU). The plan, constraints and validation gates are in
   [docs/metal-gpu-cull-plan.md](docs/metal-gpu-cull-plan.md). Port grass next; verify `baseInstance` in Metal
   indirect arguments. `MEITOU_METAL_CPU_CULL=1` restores the CPU reference for A/Bs. Readback copies used only
   for statistics still need a safer post-frame path if they prove unreliable.
3. Terrain performance: the phase-0 gate is closed. In the matched no-object view, material-distance 1 saved 17%, anisotropy
   and terrain-error were noise, and the normal-map-off compile variant saved 12% (kept for that quality switch).
   Normal maps on, loaded-branch, early-fragment and half-precision experiments did not clear the noise floor, so
   the default path is unchanged. A distance material LOD (normal fade plus a per-patch no-normal split) was tried
   and reverted: the best forced case was ~13.5 ms vs 14.2 baseline, and `500,1500` was noise. See
   [docs/metal-terrain-shader-plan.md](docs/metal-terrain-shader-plan.md); only revisit direct MSL or a finer
   material split if a matched shipping capture shows terrain is the bottleneck. Do not remove the terrain/foliage
   prepasses.
4. Profile and target `post shafts`, `post scene effects` and the shadow cascades; these were 11.27%,
   6.51% and 14.29% of the latest 26.84 ms capture.
5. Remaining polish outside MEP-I: HDR/EDR, purgeable/idle texture cleanup, and any performance follow-ups.
6. Decide the GI baseline: Metal GI stays experimental and opt-in; no-GI matches Vulkan on this Mac.
7. Make Metal the macOS default only after MEP-I and the separate performance/polish pass are accepted.

Known diagnostic issue: `MTL_DEBUG_LAYER=1` currently reports pre-existing texture-type mismatches at
`uHeightFine` (Cube bound where 2D is expected) and `uOceanMaps` (2D bound where 2DArray is expected). It
reproduces without the recent texture-view/compression changes; fix it before treating the validation gate as
clean.

## Commands

Metal uses the native libraries already copied into the build output. **Do not set `DYLD_LIBRARY_PATH` for Metal.**
It is a Vulkan/MoltenVK workaround and can inject Homebrew `libjpeg`/`libpng` into AppKit/ImageIO; a separate
2026-10-23 `SIGBUS` was in CoreCursor/ImageIO while loading a cursor image. Use it only with `--renderer vulkan`.

Normal Metal run:

```bash
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --windowed --size 1512x856
```

Unattended world smoke test (all features on):

```bash
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --windowed --size 1512x856 --no-population --quit-after 15 \
  --at -16134,26409 --distance 2000 --pitch 20 --yaw 30 --time 12.93
```

Still with a map-window recenter:

```bash
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --screenshot /tmp/metal-recenter.png --size 1512x856 --windowed \
  --at -66800,-41400 --fly-to -44047,250 --distance 864 --pitch 20 --yaw 30 --time 14.38 --no-population
```

Static screenshot at the former water fault:

```bash
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --screenshot /tmp/metal-water.png --size 1512x856 --windowed \
  --at -16134,26409 --distance 2000 --pitch 20 --yaw 30 --time 12.93 --no-population
```

## Notes

- Keep `--renderer vulkan` as the reference; do not change its behavior.
- Do not commit game assets.
- `--fly-to` is implemented for Metal screenshots as six camera cuts with streaming settles; it exercises map-window
  recentering without an interactive flight.
- `MetalUploads.Write`'s packed-row path now keeps source data at the start of each row; do not reintroduce a source
  `region.X` offset.
- Re-run quick tests with `dotnet test --filter "Category!=Slow&Category!=Bench"` after renderer changes.
