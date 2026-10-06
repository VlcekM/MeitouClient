# Decisions (engine branch)

Choices made while working unattended on the `engine` branch, with the reason. Newest last.

1. **Parity reference.** The reference viewer is master at `f127922`, built Release into
   `R:\VlcekM\MeitouClient-engine-work\ref\viewer` (outside the repo). Screenshots and benchmark output live in
   `R:\VlcekM\MeitouClient-engine-work` too. After each merge of master into `engine` the reference is rebuilt from the
   merged master commit and the reference screenshots are taken again.
2. **Parity tooling.** `meitou-tools image-diff a.png b.png [diff.png]` (mean absolute RGB difference in 0..255, share of
   pixels whose largest channel difference is over 12, maximum) and `tools/scripts/parity.sh` /
   `parity-compare.sh` (the gate views: five places at 13:00 and 02:00, offscreen 1600x900, ten pictures; four places and eight pictures before the forest view was added). No Python on the machine,
   so the comparison is a C# tool. `PngWriter` moved from the viewer to `Meitou.Data.Textures` so the tool can write diff
   images.
3. **Phase 1 is a move, not a rewrite.** The gate is pixel identity (mean < 0.05), so the renderers move into
   `src/Meitou.Rendering` with their GL call sequences unchanged. The backend interface is defined in Phase 1 with Vulkan's
   shape in mind (pipelines = shaders + fixed state, render passes = targets + load/store, command lists, explicit queries)
   and implemented for OpenGL; the frame orchestration uses it where that does not change the pictures. Moving each
   renderer's own drawing onto the interface happens per renderer in Phase 2, when its Vulkan version is written.
4. **Grass sway in offscreen renders.** The grass sway ran on a wall clock, so two runs of the same `--screenshot` differed
   (rock view mean 0.28, zone 14.30 0.08 between two runs of master). Offscreen renders now hold it still
   (`FoliageRenderer.SwaySeconds = 0`); interactively it follows real time as before. The exact parity gate (mean 0.0000 in all
   eight views) was measured against master `f127922` plus the same two-line patch (worktree
   `R:\VlcekM\MeitouClient-engine-work\refdet-src`); against unpatched master the only differences are the swaying grass
   blades (rock 0.76, zone 14.30 0.07, the rest 0.0000).
5. **Benchmark location.** The brief gives `--fly-benchmark 1500 --size 1600x900` without a place; the viewer's default is the
   world's centre. The benchmark is run at `--town "The Hub"` (the place docs/viewer.md measured before), the same for master
   and engine builds, interleaved, three runs each.
6. **`Meitou.Engine` built by a subagent** (fixed tick, game clock, input bindings, Kenshi and free cameras, 42 tests), reviewed
   and merged. Engine choices it made are listed in docs/engine.md (pivot speed units, 150 game seconds per hour, key speeds,
   default keys). The settings panel key was changed from F1 to Tab to match the viewer.
7. **Phase 2 backend: GL-shaped, not Vulkan-shaped (reverses the Phase 1 interface).** *Superseded by 22.* Evidence: all 30 world shaders compile
   unchanged to SPIR-V with shaderc's relaxed Vulkan rules plus automatic binding and location mapping (`Silk.NET.Shaderc`
   with its native binary from NuGet); the renderers use 88 distinct GL functions over 957 call sites. So the interface is the
   exact GL subset the renderers use (same signatures and Silk enums): the OpenGL backend is a pass-through (pictures stay
   identical), porting a renderer is a type change, and the work concentrates in one GL-on-Vulkan translation layer (lazy
   pipelines keyed by state, dynamic rendering, per-draw push descriptors, uniforms emulated through a reflected default
   uniform block, rename-on-write buffers). The Vulkan-shaped `IGpuDevice` from Phase 1, which nothing used, was removed.
   Consequences recorded now: **bindless descriptors and multithreaded command recording do not fit an immediate GL-shaped
   API** and are not attempted; deferred deletion tied to frame fences, a transfer queue for uploads, the pipeline cache on
   disk and frames in flight do fit and are part of the layer.
8. **`WorldSession`** (in `Meitou.Engine`): the loaded region and focus, the tick clock, the game clock, input, bindings and
   the camera rig; the game host drives it. Rendering state (`WorldScene`, `WorldFrame.Gpu`) stays in `Meitou.Rendering`.
9. **Anisotropic LOD bias on NVIDIA.** With anisotropic filtering on, NVIDIA's OpenGL driver samples a quarter mip level
   finer than its Vulkan driver for the same texture and sampler state (rock view: mean difference 1.21 at bias 0, 0.0009 at
   −0.25, 1.08 at −0.5; with anisotropy off both match to 0.0008). `VkGl` gives anisotropic samplers `mipLodBias = −0.25` on
   NVIDIA (vendor 0x10DE) only; other vendors are untested and get 0.
10. **Alpha-to-coverage and depth formats.** Observed: the remaining parity differences are grass edges drawn with
    alpha-to-coverage (rock and zone views), where the two drivers turn alpha into samples differently. GL `DEPTH_COMPONENT24` is created as `D32_SFLOAT` (finer, universally
    supported); textures written mid-frame keep before-frame semantics only for uploads (rendered targets and
    `glGenerateMipmap` are recorded in order in the frame).
11. **Tiered PGO off in the game and the viewer.** Profiling the flight benchmark showed the render thread parked in
    `PollGCWorker` (a runtime suspension) during 10-25 ms frame spikes with no GC pause counted: tiered PGO keeps
    instrumenting and re-JITting hot render code, and each code install suspends the runtime. Pipelined Vulkan flight p99:
    16.1 ms with it, 9.4 ms with `TieredPGO=false` (9.7 ms with tiered compilation off entirely). Set in both csproj files.
12. **A full collection at the end of loading, then `SustainedLowLatency`.** A blocking gen2 GC a few frames after the load
    cost 270-400 ms in the flight benchmark (frame 3). `WorldFrame.FinishLoading()` (viewer after settling, game after
    booting) runs one compacting collection as part of the load and leaves gen2 collections to the background GC.
13. **Where `--renderer vulkan` applies.** The game (`meitou`) runs windowed and offscreen on either backend (`Display`:
    swapchain through `VulkanPresenter`, MAILBOX without vsync, FIFO with). The viewer uses Vulkan for `--screenshot` and
    `--fly-benchmark` (headless); its interactive window stays OpenGL (a developer tool; the game is the Vulkan window).
14. **Upscaling: where it sits, MSAA off, motion from depth.** The scene is drawn at the render size (display × scale, rounded)
    with a jittered projection (Halton 2,3; `8 × ratio²` phases, FSR's rule), the sky and both depth slices jittered, the shadow
    maps and the water reflection not (the reflection is reused across frames; a jittered copy would shimmer). The upscaler
    runs right after the scene and SSAO; bloom, exposure and the composite run at the display size on its output, so the HDR
    picture is what gets reconstructed (the vendors' recommended place). With any upscaler on the scene is single-sampled:
    TAA, FSR and DLSS reconstruct edges from the jittered frames and want single-sample inputs. Motion vectors come from depth
    alone (camera reprojection): the world is static apart from foliage sway, which therefore ghosts slightly under TAA (sway
    is off in offscreen pictures). To keep the far slice's depth for the reprojection, it gets its own depth buffer while an
    upscaler is on instead of being cleared; with the upscaler off nothing changes (parity unchanged).
15. **Texture LOD bias with an upscaler** (Vulkan only): every mipmapped fetch gets `log2(scale) − 1` for FSR and DLSS (the vendors'
    guidance) and `log2(scale) − 0.5` for the built-in TAA (−1 shimmers at native scale with its gentler accumulation).
    OpenGL 3.3 has no global bias (only per texture or sampler object), so GL with a render scale below 1 looks softer.
16. **FSR through the FidelityFX API DLL of SDK 1.1.4.** AMD's SDK 2.x releases ship DX12-only DLLs; the newest prebuilt Vulkan
    `amd_fidelityfx_vk.dll` (FSR 3.1.4, MIT) is in SDK v1.1.4, so that is what `FsrUpscaler` loads at run time (`MEITOU_FFX_PATH`
    or next to the executable; never committed). FSR's `viewSpaceToMetersFactor` gets 0.1: Kenshi's unit is unknown, decimetres
    fit the world's quoted size best (docs/formats/terrain.md). The device turns on FP16, 16-bit storage, formatless storage
    images and subgroup size control where supported, because the DLL chooses its FP16 shaders from what the GPU supports.
17. **DLSS through Streamline 2.14.1 in manual-hooking mode.** Streamline's interposer is loaded at run time (only when DLSS is asked
    for, on the command line or in the saved settings, because `slInit` must run before the Vulkan instance), its reported instance
    and device extensions and 1.2 features are added to our own device (the EXT/KHR buffer-device-address extensions are left out:
    core 1.2 with the feature on; `privateData` is enabled for NGX), and the device is handed over with `slSetVulkanInfo`. Only
    `vkQueuePresentKHR` goes through the interposer's proxy (Streamline needs to see presents); the swapchain calls stay native, which
    DLSS Super Resolution does not need. Offscreen runs present nothing, so Streamline's per-present bookkeeping does not run there
    (fine for pictures and benchmarks). The render size stays ours; the DLSS mode follows the render scale (DLAA at 1). Our images are
    bottom-up, so the matrices DLSS gets have clip y flipped to describe the picture as stored. None of the NVIDIA DLLs are in the
    repository (`MEITOU_STREAMLINE_PATH` or next to the executable: `sl.interposer.dll`, `sl.common.dll`, `sl.dlss.dll`, `nvngx_dlss.dll`).
18. **OpenGL removed.** `GlPassthrough`, the OpenGL window and context in the game's `Display`, the viewer's GL windows and raw
    `Silk.NET.OpenGL` drawing (mesh, character, world), and the `Silk.NET.OpenGL` package are gone; `--renderer gl|vulkan` no longer
    chooses anything (`vulkan` is accepted and ignored, `gl` prints that OpenGL is gone and is ignored; so is an old `renderer` key in
    `meitou.user.json`). Vulkan is the only backend, windowed (the game, and the viewer's `--world`, mesh and character modes through
    `Meitou.Rendering.Display.VulkanDisplay`, which also holds the Streamline setup) and headless (`--screenshot`, `--fly-benchmark`).
    The GL-shaped `IGl` over `VkGl` stays as the renderer API (DECISIONS 7 still holds): the renderers, their 957 call sites and the
    GLSL do not change, the shaders are compiled to SPIR-V as before, and a Vulkan-shaped interface remains an option for later
    without a second backend to keep in step. The enumerations `IGl` takes were Silk's; they are now ours (`Gpu/GlEnums.cs`), the
    GL specification's names and token values for the members the code uses, so a renderer's change was its `using`. The parity
    reference stays master `f127922`'s OpenGL pictures (DECISIONS 1, 4; folders kept outside the repo), and the gate is the one
    Vulkan check: mean difference 0.08 or less in the eight views (measured at removal: 0.0000 to 0.0789; mesh, animated mesh and
    character screenshots 0.0000). The interactive mesh and character windows are no longer multisampled (the swapchain image is
    single-sampled; the offscreen pictures still resolve a 4x target).
19. **The frame-time tail: GC, JIT, priorities, culling.** Attributed with the flight benchmark's new lines (stage means, GC pauses on
    the worst frames, `gc` totals, `MEITOU_JOB_STATS=1` per call-site allocation) and GC events: the streaming threads allocated ~3.4 MB
    a frame, and each single-threaded gen0 GC (2 MB promoted) spent 3-15 ms scanning the ~0.5 GB gen2, suspending every thread. Changes,
    each measured in interleaved runs: (1) less garbage: grass pages built in a per-thread buffer instead of a list sized for every
    candidate (2.4 GB a flight to 0.25), map-window levels from the shared array pool (5.1 GB a flight in all to 2.6); (2) server GC on
    four heaps (`System.GC.HeapCount`): 6 collections a flight instead of 43, 1.3-2.4 ms each, +0.5 GB working set; (3) no tiered
    compilation (methods compiled once, optimised; tier-up stalls of 2-11 ms gone, loading no slower); (4) the render thread above normal
    priority and the foliage culling (by zone group, and the shadow cascades' candidates by chunk) on dedicated above-normal job threads
    (`RenderJobs`; the thread pool's preempted workers made the tail worse), merged in the old order so pictures are identical; (5) the
    S3TC upload no longer copies a level of zeros before filling it in slabs (a GL-era way to allocate), and the "DXT5 normal" test runs
    on the worker. Result at 1600x900 (four interleaved runs each, medians): p50 4.15 -> 3.7 ms, p95 6.85 -> 5.4, p99 9.15 -> 7.0,
    max ~23 -> ~16.
20. **Shader bindings shifted after compiling; bundled shaderc pinned.** A Vulkan SDK on PATH supplied a newer `shaderc_shared`
    whose glslang puts combined image samplers in a resource kind shaderc's binding-base API cannot reach, so fragment samplers
    landed at 0.. (docs/engine.md "Vulkan backend"). Bindings are now offset per stage by patching the SPIR-V, which holds for any
    shaderc, and the compiler loads the one it ships. Shader cache version 3 (2 had been written by experiments with other output).
21. **Meitou shadows** (the `shadows` switch, F5; docs/formats/shadows.md "Meitou shadows"): cascades fitted from the camera's near
    plane to the range (the game's halved splits leave two of four cascades in front of the near plane in most views), drawn on a
    staggered schedule (CPU is the bottleneck: the far cascades' foliage casters are most of the pass's CPU in wide views), a soft receiver
    with a blocker search, and the terrain's own shadow beyond the range from a per-sun-direction sweep of the world height grid
    (one fetch per pixel instead of a ray march in forward shaders with overdraw). The faithful path is untouched (pixel-identical).
22. **Adopted 2026-10-06 (supersedes 7, amends 18): a native Vulkan-shaped renderer API replaces the GL-shaped `IGl` (the "IGl stays" sentence of
    18).** Design in docs/renderer-native.md. Evidence: the frame is CPU-bound (forest: foliage 8.7 ms and shadows 6.1 ms of CPU against
    7 ms of GPU), and a scratchpad spike on the RTX 4070 measured a foliage mesh draw at 2.0 µs through VkGl as `FoliageRenderer.DrawMesh`
    issues it, against 0.18-0.30 µs recorded directly with the same SPIR-V and 0.06-0.10 µs for a push-constant or indirect draw. Plan: the
    native API in `Meitou.Rendering/Gpu/` (no new project: Meitou.Rendering takes the Vulkan reference, Core and Shaders move there,
    Meitou.Rendering.Vulkan is folded in and removed in phase 8); a seam (`IGlInterop`) that lets native and VkGl code share a frame with full
    barriers, all images in GENERAL, and VkGl's pipeline and descriptor caches invalidated after each native segment (without that,
    VkGl silently draws with the native pipeline: reproduced, and invisible to the validation layer); parity ports first with
    byte-identical SPIR-V and VkGl's descriptor layout, the native model (bindless textures, frame and view constants, push constants)
    as a second gated step; GPU-driven culling for foliage after a CPU reference-alignment step; secondary command buffers in wave 4;
    `IGl` and `VkGl` (1,452 calls in 27 files) deleted in phase 8. Gate: 0 differing pixels in the ten `parity.sh` views with
    `--faithful all`, the baseline run twice first. The owner's answers to the open questions are in the doc's "Owner decisions".
