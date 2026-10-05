# Decisions (engine branch)

Choices made while working unattended on the `engine` branch, with the reason. Newest last.

1. **Parity reference.** The reference viewer is master at `f127922`, built Release into
   `R:\VlcekM\MeitouClient-engine-work\ref\viewer` (outside the repo). Screenshots and benchmark output live in
   `R:\VlcekM\MeitouClient-engine-work` too. After each merge of master into `engine` the reference is rebuilt from the
   merged master commit and the reference screenshots are taken again.
2. **Parity tooling.** `meitou-tools image-diff a.png b.png [diff.png]` (mean absolute RGB difference in 0..255, share of
   pixels whose largest channel difference is over 12, maximum) and `tools/scripts/parity.sh` /
   `parity-compare.sh` (the eight gate views: four places at 13:00 and 02:00, offscreen 1600x900). No Python on the machine,
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
7. **Phase 2 backend: GL-shaped, not Vulkan-shaped (reverses the Phase 1 interface).** Evidence: all 30 world shaders compile
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
