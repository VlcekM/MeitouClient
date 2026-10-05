# Progress (engine branch)

## Summary (overnight job, read this first)

All three phases are done on branch `engine` (worktree `R:\VlcekM\MeitouClient-engine`). Nothing was pushed and master was not touched.
Master has not moved since `f127922`, so the merge is a fast-forward:

```
cd R:\VlcekM\MeitouClient
git merge engine
```

**What was done**
- Phase 1: `Meitou.Engine` (fixed tick, clock, input bindings, Kenshi/free cameras), `Meitou.Rendering` (renderers moved out of
  the viewer), `Meitou.Game` (`meitou`, boots into a town, settings in `meitou.user.json`). Parity with master 0.0000.
- Phase 2: a Vulkan 1.3 backend behind a GL-shaped interface (`IGl`: `GlPassthrough` and `VkGl`), `--renderer gl|vulkan` in the
  game (windowed and offscreen) and the viewer (offscreen). Validation clean.
- Phase 3: render scale, Halton jitter, depth-reprojected motion vectors and a water reactive mask; built-in TAA (both backends);
  FSR 3.1.4 (FidelityFX API DLL, runtime-loaded) and DLSS (Streamline 2.14.1, runtime-loaded) on Vulkan; `--upscaler`,
  `--render-scale`, `--sharpness` and the game's Tab panel. Missing DLLs or a failing vendor upscaler fall back to TAA.

**Numbers** (this machine; 1500-frame flight over The Hub, Vulkan pipelined, Release build; logs `MeitouClient-engine-work/bench/up2-*`)

| size, upscaler | GPU frame (ms) | p50 / p95 / p99 / max (ms) |
|---|---|---|
| 1600x900 off (MSAA 4x) | 3.64 | 4.1 / 6.7 / 8.9 / 15.8 |
| 1600x900 TAA native | 2.79 | 3.8 / 6.8 / 9.1 / 17.0 |
| 1600x900 FSR quality | 2.86 | 4.0 / 6.9 / 9.4 / 16.4 |
| 1600x900 DLSS quality | 2.87 | 3.8 / 7.1 / 10.3 / 19.2 |
| 2560x1440 off (MSAA 4x) | 5.93 | 6.0 / 8.2 / 10.9 / 17.0 |
| 2560x1440 TAA native | 4.50 | 4.8 / 7.6 / 10.1 / 17.7 |
| 2560x1440 FSR quality | 4.28 | 4.6 / 7.5 / 10.6 / 18.1 |
| 2560x1440 DLSS quality | 4.93 | 4.9 / 7.8 / 10.2 / 18.2 |

- 1600x900 is CPU/streaming-bound, so upscaling helps at 1440p and above. Any upscaler turns MSAA off, which is part of the GPU saving.
- Vulkan vs GL (Phase 2, 1600x900): pipelined p95 6.5-6.9 vs 9.1, p99 8.7-10.4 vs 15.5. GL master-equivalent serialized p99 was 21.7-24.4.
- Upscaler GPU cost at 1600x900: TAA 0.1 ms, FSR 0.38 ms, DLSS 0.73 ms.
- Image quality on the rock view, as the mean difference from the native-resolution still reference (lower is closer; TAA native 3.41): DLSS quality 4.68, FSR quality 5.16.
  Temporal stability, orbit end frame vs a still at the same yaw: TAA 3.7, DLSS 3.92, FSR 4.79. TAA at quality scale on GL vs the reference: 6.43.
- Game, windowed, 12 s: off 109 fps, TAA 110, FSR 103, DLSS 105. Validation clean in every mode.
- Gates: build 0 warnings; tests 308/308 (with `KENSHI_PATH`). Parity with upscalers off is unchanged: GL 0.0000 on all 8 views. Vulkan:
  hub 0.0010, portnorth 0.0003, rock 0.0426, zone14_30 0.0789 (13:00); night views 0.0000-0.0001 (all within the 0.08 gate).
- An interleaved A/B against the Phase 2 tip shows no regression from Phase 3 with the upscaler off (p50 4.0 vs 4.0-4.1, p99 9.1-9.5 vs 8.9-9.5).
  An earlier batch read about 10 ms p50 for every mode; it was disturbed and is superseded (`bench/up-*`).

**Screenshots** (`R:\VlcekM\MeitouClient-engine-work\shots\taa1`):
- stills: `rock_taa.png`, `rock_taaq.png`, `rock_fsr.png`, `rock_dlss.png`;
- after an orbit (history under motion): `rock_orbit_taa.png`, `fsr_orbit.png`, `dlss_orbit.png`;
- `pn_reactive.png`: Port North water with the reactive mask.
- Parity sets are in `shots/p4gl` and `shots/p4vk`.

**Decisions** ([DECISIONS.md](DECISIONS.md)):
- 1-5: parity reference, tooling, Phase 1 as a move, offscreen grass sway, benchmark place.
- 6-8: engine library, GL-shaped backend interface, `WorldSession`.
- 9-13: NVIDIA aniso bias, alpha-to-coverage and depth formats, tiered PGO off, post-load GC, where Vulkan applies.
- 14: upscaling placement (after scene + SSAO, MSAA off, motion from depth).
- 15: texture LOD bias (Vulkan only).
- 16: FSR from SDK 1.1.4 (2.x is DX12-only), decimetre units.
- 17: DLSS via Streamline manual hooking.

**Running FSR/DLSS:** no vendor DLL is in the repository.
- FSR: `amd_fidelityfx_vk.dll` from the FidelityFX SDK v1.1.4 release (MIT), next to the executable or via `MEITOU_FFX_PATH`.
- DLSS: `sl.interposer.dll`, `sl.common.dll`, `sl.dlss.dll` and `nvngx_dlss.dll`, next to the executable or via `MEITOU_STREAMLINE_PATH`
  (tonight's copies are in `MeitouClient-engine-work\dlss-runtime`).
- Then run `meitou --renderer vulkan --upscaler fsr|dlss [--render-scale quality]`.

**What's left**
- The p99 target (< 6.9 ms at 1600x900) is not met. The tail is render-thread streaming work (foliage/terrain/object updates,
  reflection terrain), which should move off the render thread.
- Offscreen DLSS runs log Streamline's "presentCommon() was not observed" once, because nothing is presented; windowed runs go through
  the present proxy. Streamline's swapchain hooks are not routed: DLSS Super Resolution doesn't need them, frame generation would.
- Not done yet:
  - a DLSS reactive/transparency hint (FSR and TAA get the water mask);
  - per-object motion, so foliage sway ghosts slightly under TAA;
  - temporal dither for foliage and building LOD fades;
  - a GL global texture LOD bias (GL looks softer below native scale).

## Details

Unattended job: reorganise into a bootable game (Phase 1), Vulkan backend (Phase 2), upscalers (Phase 3). Brief at
`C:\Temp\meitou-engine-vulkan-upscalers-prompt.md`. Decisions in [DECISIONS.md](DECISIONS.md). Work files (reference
viewer, screenshots, benchmark logs) in `R:\VlcekM\MeitouClient-engine-work` (outside the repo).

## Phase 1: reorganise into a bootable game — done

- [x] Worktree `R:\VlcekM\MeitouClient-engine`, branch `engine` from master `f127922` (master has not moved since).
- [x] Parity tooling: `meitou-tools image-diff`, `tools/scripts/parity.sh`, `parity-compare.sh`.
- [x] `src/Meitou.Engine`: `WorldSession`, fixed tick (30 Hz), game clock (scale, pause), input actions and bindings, Kenshi
      strategy camera + free camera, interpolation; 44 tests.
- [x] `src/Meitou.Rendering`: the world renderers, streaming and `WorldFrame` moved out of the viewer (GL calls unchanged).
- [x] `src/Meitou.Game` (`meitou`): boots at `--town` (default The Hub) or a coordinate, Kenshi camera (boom 150, 30°),
      maximized, vsync off with a 240 fps limiter, `meitou.user.json` (sliders, bindings, fps, tick rate). `--screenshot`,
      `--quit-after` for unattended checks.
- [x] Viewer on the same libraries: `--world` parity views, a mesh and a character screenshot identical to master.
- [x] Gates:
  - parity: all eight views mean 0.0000 against master + the sway patch (DECISIONS 4);
  - benchmark (The Hub, 1500 frames, 1600x900, three interleaved runs): engine p50 10.2–11.9 ms, p95 16.9–17.9, p99 21.7–24.4,
    max 34–49; master p50 9.9–11.3, p95 17.0–19.6, p99 21.1–32.2, max 40–91: within noise;
  - build 0 warnings; tests 205/205 with KENSHI_PATH (before WorldSession tests), engine tests 44/44.
  - game smoke test: 20 s maximized, 103 fps average while other renders shared the GPU, 600 ticks.
- [x] Docs: docs/engine.md, docs/viewer.md, docs/README.md, README.md, ROADMAP.md.

## Phase 2: Vulkan backend — done (performance target not reached, see below)

Plan (DECISIONS 7): a GL-shaped interface (`IGl`, the exact subset of GL the renderers use), a pass-through OpenGL
implementation, all renderers ported onto it (parity must stay 0.0000), then a GL-on-Vulkan translation layer (`VkGl`).

- [x] `IGl` + `GlPassthrough`; all renderers on it (parity 0.0000).
- [x] `Meitou.Rendering.Vulkan`: device/allocator/frame ring (vk-core agent), GLSL→SPIR-V compiler + reflection (vk-shaders
      agent), `VkGl` (buffers with rename-on-write, textures, FBOs, programs with emulated loose uniforms, pipelines keyed by
      state, push descriptors, timestamp queries). Convention tests (`VkGlTests`) with validation clean.
- [x] Viewer `--renderer gl|vulkan` (Vulkan headless for `--screenshot`/`--fly-benchmark`; `MEITOU_VK_VALIDATION=1`).
- [x] Parity, Vulkan vs GL reference (`shots/vk3`): hub 0.0010/0.0000, portnorth 0.0003/0.0000, rock 0.0426/0.0001,
      zone14_30 0.0789/0.0001 (13:00/02:00). Validation clean. Found: NVIDIA GL vs Vulkan anisotropic filtering differ by a
      quarter mip level (DECISIONS 9).
- [~] Performance (1600x900 flight, 1500 frames, this machine with other sessions running; logs in `engine-work/bench`):
      | measurement | GL (master-equivalent) | Vulkan |
      |---|---|---|
      | serialized (Finish each frame, 60 fps pacing), p50 / p99 / max | 10.2-11.9 / 21.7-24.4 / 34-49 | 9.1 / 20.0 / 30.7 |
      | `--fly-pipelined` (2 frames in flight), p50 / p95 / p99 / max | 3.6 / 9.1 / 15.5 / 23.7 | 4.0 / 6.5-6.9 / 8.7-10.4 / 16-29 |
      GPU frame (Vulkan timestamps) 3.5-3.8 ms; VkGl draw prep 0.55 ms for ~670 draws; 56 descriptor pushes a frame.
      Fixed on the way: tiered PGO stalls (DECISIONS 11), the post-load gen2 GC and upload backlog (DECISIONS 12), foliage
      culled once for all shadow cascades (hub shadow CPU 12.3 -> 1.9 ms), descriptor templates, dynamic uniform offsets.
      **Target p99 < 6.9 ms not reached** (p99 ~9-10 ms, max 16-29): the tail is render-thread streaming work shared with GL
      (foliage/terrain/object updates up to 25 ms, reflection terrain up to 10 ms).
- [x] Game `--renderer gl|vulkan`: windowed (swapchain via `VulkanPresenter`, flip at present, MAILBOX/FIFO) and offscreen;
      game screenshot GL vs Vulkan mean 0.044; validation clean; interactive smoke test Vulkan 221 fps vs GL 204 (240 cap).
      The viewer's interactive window stays GL (DECISIONS 13).
- [x] 2560x1440 (`bench/1440-*`): Vulkan serialized p50/p99 9.8/15.6, pipelined 6.0/11.6 (GPU frame 5.8 ms: GPU-bound);
      GL serialized 12.2/25.2, pipelined 5.6/19.3. Upscaling (Phase 3) is the lever at this size.
- [x] docs/engine.md "Vulkan backend". Master unchanged at f127922 (nothing to merge at phase end).
- [x] Foliage shadow-cascade culling once for all cascades.

## Phase 3: upscalers — done

- [x] Render scale, Halton(2,3) jitter (`8 × ratio²` phases), depth-reprojected motion vectors (one matrix per slice; the far slice
      keeps its own depth while temporal), fixed-plane upscaler depth, water reactive mask (DECISIONS 14).
- [x] Built-in TAA (both backends): Gaussian resolve, Catmull-Rom history, YCoCg variance clip, tone-mapped blend.
- [x] FSR 3.1.4 through `amd_fidelityfx_vk.dll` (SDK 1.1.4), runtime-loaded (DECISIONS 16). Fixed on the way: VkGl returned a dummy
      for depth textures behind a plain sampler (this also fixed Vulkan SSAO), and FSR needs the dedicated-allocation entry points.
- [x] DLSS through Streamline 2.14.1 manual hooking, runtime-loaded, present proxy in the game (DECISIONS 17). Validation clean.
- [x] Texture LOD bias with an upscaler (Vulkan, DECISIONS 15).
- [x] Options: `--upscaler`, `--render-scale`, `--sharpness`; game Tab panel sliders saved in `meitou.user.json`; the window title shows
      the active upscaler; viewer `--orbit-step` for motion tests; warm-up frames before offscreen pictures.
- [x] Gates: build 0 warnings, 308/308 tests, parity unchanged (GL 0.0000, Vulkan ≤ 0.079), benchmarks in the summary above.
- [x] Master unchanged at `f127922` (nothing to merge at phase end). docs/engine.md "Upscaling".
