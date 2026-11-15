# Metal GPU foliage cull plan

Status: **mesh/rock cull implemented, opt-in on Metal (`MEITOU_METAL_GPU_CULL=1`); the CPU cull is the default; grass still on the CPU**.
The GPU progress timeouts it caused had one **Verified** cause, fixed: see "The timeout" below. The opt-in stays until a longer soak and the
`MTL_DEBUG_LAYER` gate. Generated rock LODs and rock impostors need the GPU cull, so the default Metal path has neither. This is the
Metal port of the existing Vulkan GPU foliage cull and grass submission. It is the A2 item in
[metal-performance-memory](metal-performance-memory.md), not a new renderer feature; the visible result must stay
the same.

## The timeout

**Verified** (bench save, `MEITOU_METAL_GPU_CULL=1`, full 52 s play-bench): before the fix 4 of 4 runs hung the GPU within 35 s
(`Caused GPU Timeout Error` on the scene and present buffers; the frame then takes 5 s and every later buffer is "Ignored");
after it 5 of 5 completed, and fps equals the CPU cull's (the frame is GPU-bound). Skipping only the foliage indirect draws also avoided
the hang; skipping the terrain's did not.

Cause: the compute sets bind through argument buffers, so Metal only knows a buffer from `useResource`. `BindSetStage` declared every buffer
`Read`, though the cull kernels write the fades, counts, offsets, args and rows and the next kernel reads them. With no write declared, Metal
did not order the encoders over those buffers (a barrier here only ends the encoder), and the scan/compact kernels or the indirect draws read
unfinished data, which can give huge counts. A compute pass's storage buffers are now declared `Read | Write` (`MetalCommandList.BufferUsage`).
Not tried: `MTL_DEBUG_LAYER` over the cull (the layer aborts earlier on other, pre-existing missing bindings).

## Why

At the save-start view the Metal scene encoder is 6.75-7.77 ms, with **672-673 draws** and **14.7M vertices**.
The CPU walks the zones, culls every group/instance and emits the draws. The GPU path now runs
`FoliageGpuCull` (cull, scan, compact) on Metal for the meshes and TERRAIN-mode rocks, then binds a small indirect
argument buffer; `FoliageGrassGpu` (cull, order) is still Vulkan-only and grass remains on the CPU. Porting the
mesh path gives Metal:

- less CPU culling and draw submission;
- indirect draws whose counts/placement come from the GPU;
- the same LOD/culling inputs the Vulkan path already uses;
- fewer per-frame command-list bindings.

It does **not** replace the scene/object/terrain shader ALU work; both tracks are needed.

## Existing pieces

- `FoliageGpuCull`: instance arena, cull/scan/compact compute kernels, per-view scratch, indirect arguments,
  readbacks for statistics. It now takes `IGpuContext`, binds through argument buffers and works on both backends;
  `MetalFoliageCullTests` compares its rows, offsets and indirect arguments with `FoliageCull` on Metal.
- `FoliageGrassGpu`: blade arena, slot table, cull/order kernels, indirect draws. It is still `GpuContext` only;
  `Supported` requires Vulkan push descriptors, `DrawIndirectCount`, `DrawIndirectFirstInstance` and
  `MultiDrawIndirect`. Metal grass therefore stays on the CPU for now.
- `FoliageRenderer`: has both paths behind `GpuCull`/`GpuGrassActive`; the CPU path is the correctness fallback.
- `MetalCommandList`: has indexed indirect draws, but no multi-draw and no draw-count buffer. It records a loop over
  slots; unused slots must contain zero index counts. This is the main Metal constraint.
- `MetalFrame.PreFrame`: on Metal this is now a command buffer of its own, committed before the frame's own. The
  cull is recorded while the scene rendering is already open (a Metal render encoder cannot record compute).
- `MetalCommandList.Barrier`: closes the open compute/transfer encoders at each dependency, so a following
  dispatch or blit starts after the previous encoder's work. The cull's cull → scan → compact chain relies on it.

## Implementation order

### 1. Make the shared pieces backend-neutral — done for the mesh cull

- `FrameScratch` now takes `IGpuContext`; `FoliageGpuCull` uses `IGpuReadback`, `IGpuFrame.WriteConstants`,
  `IGpuFrame.AllocateConstants` and `IGpuContext.CreateReadback`, so the same code runs on Vulkan and Metal.
- Keep `FoliageCullChunk`, `FoliageCullDraw`, `FoliageCullWork`, `FoliageCullResult` and `GrassResult` unchanged:
  they are the seam between the renderer and the kernels.
- Do not make `GpuContext` public API; the Vulkan path is behaviourally unchanged.

### 2. Metal compute and argument buffers — done for the mesh cull

- `FoliageGpuCull` no longer requires push descriptors; the compute sets bind through argument buffers.
- The cull shaders translate and run on Metal; `MetalFoliageCullTests` checks the cull/scan/compact chain.
- The cull is recorded in `MetalFrame.PreFrame`, a separate command buffer committed before the frame. This is
  required because Metal cannot record a compute encoder while a render encoder is open.
- Cull outputs stay in `FrameScratch` for the frame's slot; no per-view allocation.

### 3. Indirect arguments

Metal has no `DrawIndexedIndirectCount` or `MultiDrawIndirect`, so the renderer must:

- pad the indirect argument list to `DrawCount` and zero every unused slot's `indexCount`;
- record one `drawIndexedPrimitives` per slot, using the existing `MetalCommandList.DrawIndexedIndirect`;
- keep the count buffer only as diagnostic data, not as a Metal draw argument;
- ensure the cull/scan kernels zero the tail before the draw list is used.

This means the CPU still issues one loop iteration per candidate draw. The win is that the CPU no longer culls
instances or builds matrices; the number of loop iterations must stay bounded by the existing scratch caps.

Verify whether Metal's indirect arguments preserve `baseInstance`. `FoliageGrassGpu` uses the first blade
(`firstInstance`) per page. If `baseInstance` is not supported or is unreliable, either:

- encode the blade offset into the vertex/index data for the Metal grass path, or
- keep CPU grass submission until the grass-specific indirect path is ready, while porting mesh/rock culling
  first.

### 4. Meshes and terrain-mode rocks first — done

`FoliageGpuCull` now runs on Metal for the mesh/rock draw path. It uses:

- main near-slice colour/depth;
- shadow cascades (the work list is shared per frame when the eye/range match);
- reflection (with grass disabled).

Validate against `--no-foliage` and the CPU path; the picture and draw counts must match.

### 5. Grass — still on the CPU

Port `FoliageGrassGpu` after the mesh cull is stable. The grass cull/order kernels already handle density,
wind, page ordering and LOD; the Metal-specific part is the indirect loop and the blade-offset handling.
If the Metal indirect loop becomes more expensive than the CPU grass for low blade counts, keep the CPU path
for that view and select the GPU path by visible blade count.

### 6. Guards, memory and fallback

- Keep the CPU path as the fallback when compute/indirect support is unavailable.
- Respect `VramGuard`: `FrameScratch.Cap`, arena growth and the guard's range shortening must remain wired.
- Do not grow a Metal buffer in place while a frame still names its slice; use the existing frame-chunk/ring
  lifetime rules.
- Readbacks used for statistics must remain a frame or two behind, never a stall.

## Validation

- Vulkan build and quick tests unchanged.
- Metal run at the save-start view with `--no-foliage` and with foliage; compare F11 instance/draw counts.
- A/B the Metal live path with `MEITOU_METAL_CPU_CULL=1` (the CPU reference); the pictures must match.
- Capture a Metal frame and confirm the cull compute encoders appear before the scene encoder and the indirect
  arguments produce the same instance counts as the CPU path.
- Check `MTL_DEBUG_LAYER=1` for indirect-buffer/argument errors.
- Measure CPU record time, draw calls, vertices and GPU time before/after. A GPU-cull port that lowers CPU but
  leaves GPU ALU unchanged is still useful, but must be reported as a CPU win.

## Other ideas worth keeping

These are deliberately **not** part of the first port.

- **Half precision:** Apple GPUs run 16-bit ALU faster, but the engine compiles GLSL to SPIR-V/MSL as float.
  A Metal-specific `half` variant for colours/normals/UVs/lighting (not world positions, depth or large
  coordinates) could reduce ALU and bandwidth. It needs a separate pipeline variant and a visual/perf A/B.
- **Scene shader specialization:** compile out branches for features that are off (GI on this Mac, unused
  atmosphere/specular paths). This is likely more valuable than half precision if the ALU limiter stays high.
- **Post shafts:** 11.27% of a recent save-start frame. Check resolution/step count and whether the title's
  29% case is representative; do not lower quality blindly.
- **Shadow cascades:** already have cadence and cached far cascades. A static/dynamic split is only worth it if
  the current redraw cadence still shows up in the frame profile.
- **Tight foliage geometry:** the existing depth prepass and early-depth variants already address most alpha-test
  overdraw. Remeshing grass is not a priority while grass is ~2% of the frame.
- **Mesh shaders:** a long-term project, not justified by the current 15-27 ms frames.
- **Encoder merging and purgeable textures:** low-priority bandwidth/memory work; see
  [metal-performance-memory](metal-performance-memory.md).
