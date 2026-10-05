# Progress (engine branch)

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

## Phase 2: Vulkan backend — in progress

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
- [ ] Performance: Vulkan frame times are ~3-4x GL right now (hub 38 ms vs 12.5 ms) — the CPU-side translation needs work.
- [ ] Game `--renderer`, swapchain present (flip once), interactive viewer.
- [ ] Foliage shadow-cascade culling once for all cascades; p99 < 6.9 ms target.

## Phase 3: upscalers — not started

## Next step

Phase 2: profile and speed up VkGl (CPU cost per draw), then the swapchain and the game.
