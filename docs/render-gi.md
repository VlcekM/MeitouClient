# Ray-traced global illumination (Meitou)

Status: **milestone 2** (2026-10-09, branch `global-illumination`): the traced scene, a debug view and probe GI (`--gi`).
This is a Meitou-only feature with no counterpart in the game. Kenshi's indirect light is one flat hemisphere ambient per biome
(`ambient light`, `KenshiLighting` / `AmbientMap`, [formats/lighting.md](formats/lighting.md)) plus SSAO, which the game ships switched off.
It is engine work, so the claims carry where they come from: *from the code*, *measured* (how), or *open*.

GI is an **optional quality mode** (DECISIONS 24): it needs a GPU with ray queries, it does not count against the frame-time targets of
the default picture, and `--faithful` keeps the flat ambient. The owner's target with it on: about 144 fps at 1080p with DLSS at a 0.67
render scale.

## Plan

1. **Milestone 1 (done)**: device support, acceleration structures for the terrain and the objects, a debug view that traces from the
   depth buffer. The question it answers: can the acceleration structures for Kenshi's world be kept up to date cheaply? Yes (see
   "Measured").
2. **Probe GI (done)** (DDGI-like, see "Probes"): a camera-centred grid of irradiance probes traced against the same structures, sampled by the material
   shaders in place of the hemisphere ambient (`mix(uAmbientGround, uAmbientSky, …)`, e.g. `TerrainShaders`). This fits the forward
   renderer as it is: no new targets. It became a runtime branch in `kenshiLight`, not a variant (see "Probes").
3. **Per-pixel GI** (ReSTIR GI, DLSS Ray Reconstruction): needs normal and albedo targets and a composite pass. These are changes to every
   renderer's fragment outputs, so this step comes after the probes.
4. **Fallback** for GPUs without ray queries (the integrated-GPU laptop): sky irradiance in spherical harmonics plus terrain horizon
   occlusion.

## Device (`VulkanDeviceOptions.RayTracing`)

*From the code.* Asked for by `--gi` or `--gi-debug`, through `VulkanDisplay.RayTracing`. When the device has `VK_KHR_acceleration_structure`,
`VK_KHR_ray_query` and `VK_KHR_deferred_host_operations` with buffer device address, `VulkanDevice.HasRayQuery` is set and they are
enabled. Without the option the device is created exactly as before. With it:

- Every buffer block of `GpuAllocator` is allocated with `VK_MEMORY_ALLOCATE_DEVICE_ADDRESS_BIT`, so any buffer can be given a device
  address.
- `BufferUse.RayInput` adds the shader-device-address and acceleration-structure-build-input usages. The object meshes' vertex and index
  buffers get it, so the bottom levels are built from the very buffers that are drawn. There are no copies.
- Buffer device address is the core 1.2 feature, never the EXT/KHR extensions, the same as with Streamline (DECISIONS 17), so DLSS and GI
  coexist.

A device without ray queries prints a warning and runs without the view. Shaders are GLSL 460 with `GL_EXT_ray_query`, compiled by the
same shaderc for Vulkan 1.3 (`GlslProgramCompiler`). `ShaderLibrary.Compute(glsl, name, bindings)` takes set 0 as given, because the
reflection reads neither storage images nor acceleration structures, and pushes it.

## The traced scene (`Gi/GiScene.cs`, `Gi/AccelerationStructure.cs`)

*From the code.*

- **Origin.** Positions in the structures are relative to `GiScene.Origin`, the eye rounded to 64 units. Each frame's instance transforms
  are offset by it, so floats keep their precision anywhere in the world, which is 1.47 M units across.
- **Terrain.** Two height-field grids around the eye: 256 × 256 quads at 16 units (4096 across) and at 160 units (40960 across). Each is
  sampled from the CPU `HeightSnapshot` on a worker, uploaded and built into a bottom level. A grid is rebuilt when the eye has moved an
  eighth of its side, and the old one is freed after the frames that traced it.
- **Terrain facing.** The grids are coarser than the drawn terrain, so in a hollow the drawn surface lies under them. A bounce ray from
  there would hit the grid from below at once, which showed as large flat patches. The terrain instance therefore keeps facing culling,
  and the rays use `gl_RayFlagsCullBackFacingTrianglesEXT`, so they pass up through it.
  - **Verified** (`RayQueryTests`): a triangle counter-clockwise seen from the ray is front-facing (Vulkan's default, no `FLIP_FACING`).
    The grid's triangles are counter-clockwise from above.
  - Objects set `TRIANGLE_FACING_CULL_DISABLE`, because their meshes are not closed.
- **Reshaped meshes.** `BuildingLodMesh` can remake a resident mesh with another finest level and put the new parts into the same
  `GpuObjectMesh`, freeing the old buffers. An object structure therefore remembers the source and the vertex buffers it was built from,
  and is rebuilt when they differ. Before this fix, the geometry records pointed at freed memory: hits read it and the device was lost.
  **Verified**: `--bench-motion fly` with validation in the Hub (it crashed before the fix) and the swamp.
- **Objects.** `WorldObjectRenderer.RayInstances`: the resolved real instances (zones and landmarks, not distant stand-ins) whose bounds
  come within `--gi-range` (default 6000) of the eye.
  - Each mesh has one bottom level, built the first time an instance of it is in range, at most 32 per frame. It is built from the finest
    level the mesh holds (or that level's manual LOD mesh), one geometry per part.
  - A structure unused for 600 frames is dropped. Evicting the mesh itself does not invalidate it: a built structure no longer reads its
    inputs.
- **Top level.** One per frame slot, sized for 16384 instances and rebuilt in place every frame (prefer fast build). Instances and
  geometry records are written into host-visible buffers of the slot.
- **Geometry records.** 32 bytes per geometry: vertex address, index address, stride, kind. A hit's record is the instance's custom index
  plus the geometry index. The shader fetches the triangle's indices and positions with `GL_EXT_buffer_reference` (`uvec2` addresses,
  because `shaderInt64` is not enabled).
- **Foliage** (`FoliageRenderer.RayInstances`, `--gi-foliage <radius>`, default 150, 0 for none): trees, bushes and rocks whose bounding
  radius is at least the threshold and whose bounds come within 4000 units, nearest first, at most 14336 instances. One structure per resident
  mesh (main and leaves parts), keyed by its `GpuMesh` (a reload makes a new one; `RayGeneration` counts deletions). The instance list walks
  every zone, so it is cached: rebuilt every 120 frames, when the eye has moved 250 units, when a foliage mesh was deleted, or 2 frames
  after a rebuild that had structures left to build. Each frame only moves the cached instances to the frame's origin and refreshes the
  records' texture indices.
  - Leaves (and cut-out main meshes) are not opaque in their structures. The probe trace tests each candidate hit against the alpha of the
    part's normal map (the draws' `AlphaSource` 2) at its threshold (leaves without one: 0.5), at mip 2.
  - Plants are double-sided, so a back-face hit on foliage is a surface, not "inside". TERRAIN-mode rocks take the ground colour map, as the
    terrain does.
  - **Measured** (2026-10-09, swamp, DLSS 0.67, `--ab gi-scene`):
    - threshold 60: 5374 foliage instances of 5821, 142.8 fps with GI against 184 without (+1.57 ms GPU);
    - threshold 150 (the default): 375 foliage instances, 146.5 fps (+1.44 ms).
- **Textures at hits.** A geometry record (48 bytes) carries the bindless index of its diffuse map, written each frame per instance
  (`WorldObjectRenderer.RayTexture`, the foliage's `RefreshRayTextures`, at the draws' LOD bias so the entries are shared; 0 when not
  resident). It also carries the cut-out map and threshold. The trace reads the hit's texture coordinates (vertex byte 24) and samples
  mip 4, so a bounce takes the surface's colour. **Verified** by tinting textured hits red in a debug build: building interiors lit red.
- **Not traced yet:** grass, characters, water.
- **Lamps.** The probe trace adds the lamps' diffuse light at each hit ([render-lights.md](render-lights.md)), so lamp light bounces.

## Debug views (`--gi-debug <n>`, `Gi/GiDebugPass.cs`, `GiShaders.Debug`)

*From the code.* A compute pass after the post-processing reads the near slice's depth. It rebuilds positions with the inverse of the
rotation-only view and the near slice's projection, the same way `ShadowShaders.DebugFragment` does, and takes a normal from the depth's
slope on the side of the smaller step. It traces, then scales its picture (render size) over the finished frame.

| Mode | Shows |
| --- | --- |
| 1 | One cosine-weighted bounce ray per pixel: yellow to red by hit distance (rays of 4000 units), blue where it reaches the sky |
| 2 | A grey "clay" picture (albedo 0.5): the sun through a shadow ray, plus `--gi-samples` (default 4) bounce rays. Where a bounce ray hits, it gives the grey hit surface lit by the sun (its own shadow ray) and the flat ambient. Where it misses, it gives the hemisphere ambient in its direction. One bounce, no denoiser, exposure 0.35 |
| 3 | Rays from the eye through each pixel, coloured by the hit's geometric normal: the traced scene itself, with no raster involved (terrain dimmer) |

The F11 statistics and the screenshot log have a `gi` line: update time, instances, object structures and their memory, structures built
this frame, and the terrain grids. The bench registers `gi-scene` (structures and view) and `gi-debug` (the view only) as A/B switches when
the run has `--gi-debug`.

## Measured

**Observed** (2026-10-09, RTX 4070, `--view hub --size 1920x1080 --gi-debug 2 --ab gi-scene --ab-period 16 --bench-frames 512`, TAA):

- GPU total 4.15 against 3.29 ms (+0.86 ±0.01). Of that, the `post` stage, which holds the debug view (9 rays per pixel at 1080p: 4
  bounce, 4 shadow from their hits, 1 shadow from the pixel), is +0.84. The structures, recorded in the frame's `reflection` stage, are
  +0.05.
- Render thread +0.07 ms (`cpu:total`). The `gi` line: 0.07 ms per update, 265 instances and 99 object structures (11 MB) in a close view
  of the Hub.

So keeping the structures up to date costs well under a tenth of a millisecond per frame, and the tracing cost scales with the rays.

## Probes (`--gi`, `Gi/GiProbes.cs`, `GiShaders.ProbeTrace` / `ProbeBlend` / `ProbeSampling`)

*From the code.* DDGI-style irradiance probes, traced against the scene above. `--gi` makes the scene and the probes. The `gi` switch
(Tab panel and `--faithful gi`) chooses between them and the flat ambient: off, the shaders take the flat ambient exactly as before.

- **Grids.** There are two cascades of 32 × 32 probe columns centred on the camera, each column 8 probes high:
  - cascade 0: 128 units apart, 96 up, rays of 4000 units;
  - cascade 1: 512 apart, 384 up, rays of 16000 units.
  A column stands on the lowest ground under its cell (5 samples of `HeightSnapshot`, recomputed only when the grid scrolls or the fine
  height window changes). The grids scroll in whole cells, and a column's tile is its world column modulo 32 (toroidal), so nothing is
  copied. A probe whose position changed starts over: no hysteresis on its next update.
- **Atlases.** The irradiance atlas holds 8 × 8 octahedral texels per probe, plus a 1-texel border (RGBA16F; alpha 1 marks a valid probe).
  The distance atlas holds 16 × 16 per probe, plus the border (RG16F). It stores the mean hit distance and the mean of its square, over
  twice the spacing. The borders copy the opposite edges, so bilinear filtering works across the octahedron's seams.
- **Trace.** Each probe traces 64 rays, in spherical Fibonacci directions turned by a random rotation each update.
  - A ray that misses sees the lighting function's ambient in its direction: the sky's irradiance × (1 − 0.04) × the ambient map × the
    environment factor, the same units as `kenshiLight`'s `envDiffuse`.
  - A front-face hit sees an albedo (the weather's ground colour map on the terrain, a constant 0.35 / 0.32 / 0.28 on objects) times the
    sun through a shadow ray, as `kenshiLight` gives it, plus the probes' own irradiance at the hit. That last term is the multi-bounce
    feedback.
  - A back-face hit is dark, with a negative distance. A probe whose rays see more than 25 % back faces is inside geometry and is marked
    invalid.
- **Blend.** The rays are blended into the irradiance tile (cosine weights) and the distance tile (cosine to the 50th power), borders
  included. The hysteresis is 0.97 per frame.
- **Round robin.** Each frame updates a quarter of the probes, every fourth probe by index, from a phase that advances by one per frame
  (`--gi-phases`, 1, 2, 4 or 8; default 4). The hysteresis per update is 0.97 raised to the number of phases, so a probe settles in about
  as many frames as with every probe updated each frame. When a grid scrolls (or its bases change), `GiShaders.ProbeInvalidate` first sets alpha 0 on every tile
  whose stored position is not its probe's current one (the column that wrapped round from the far side), so its old light is not read
  until its own update starts it over.
- **Sampling** (`giIrradiance` in `kenshiLight`). The 8 probes around the point are blended trilinearly, weighted by the wrap-around
  backface term and by Chebyshev visibility against the distance moments, the same way DDGI does.
  - Cascade 0 is used where it covers the point. Cascade 1 fills in where cascade 0 does not, and the sky's flat ambient fills in where
    neither does.
  - The result replaces `envDiffuse`, by `--gi-strength` (default 1; `uGiParams.y` scales the probes' weight, so 0.75 keeps a quarter
    of the sky's flat ambient).
  - The specular environment term is scaled by the ratio of the probes' luminance to the sky's, clamped to 0..1, as a cheap specular
    occlusion.
- **Shader model.** A runtime branch on `uGiParams.x`, not a compile-time variant: the legacy and native world programs' SPIR-V changes,
  but with the switch off the picture does not.
  - **Measured**, `image-diff`, 1280 × 720, Hub and swamp: with GI off, and with `--gi --faithful gi`, mean 0.0000 against the commit
    before.

**Observed** (2026-10-09, RTX 4070, swamp, 1920 × 1080, `--gi --ab gi-scene --ab-period 16 --bench-frames 512`):

| Setting | GPU with GI | GPU without | Difference | Of it, probe update | Of it, shading | fps with / without |
| --- | --- | --- | --- | --- | --- | --- |
| TAA, every probe each frame | 8.37 | 5.81 | +2.56 | +1.57 | +1.01 | |
| TAA, `--gi-phases 2` | | | | +0.97 | | |
| TAA, `--gi-phases 4` | 7.41 | 5.80 | +1.62 | +0.67 | +1.0 | 134 / 172 |
| DLSS at 0.67, `--gi-phases 4` | 6.34 | 5.26 | +1.09 | +0.59 | +0.55 | 156 / 189 |

- The shading cost is the 8 probes' visibility and irradiance fetches, in terrain, objects and foliage. It scales with the render
  resolution.
- The update does not scale linearly with the phases. About 0.3 ms is fixed: the full barriers between trace and blend, and the base
  upload.
- Render thread: +0.16 ms with the base heights cached; it was +0.42 ms recomputing them every frame.
- **Cheaper updates** (2026-10-09): bounce hits in the trace read the probes without the visibility test (`GI_NO_VISIBILITY`); the distance
  blend skips rays under cos 0.6 (their weight cos^50 is below 1e-11); the barriers between the probe passes and before the shading are
  compute-to-compute/shading ones instead of full ones. Measured: probe update 0.59 to 0.55 ms (DLSS 0.67, swamp). About 0.37 ms of the update
  does not scale with the probes updated (from the 1, 2 and 4 phase runs): the full barriers that start the scene and probe segments.

**Cost breakdown** (2026-10-09, with foliage, object textures and lamps; RTX 4070, `--view swamp --gi --upscaler dlss --render-scale 0.67
--size 1920x1080`). The frame clock has a `gi` stage (the scene and the probe passes, plus the lamps' binning), and the bench has a
`gi-shade` switch: side B updates the probes but the shading does not read them, which isolates the reads.

| Part | GPU ms | How measured |
| --- | --- | --- |
| Whole GI | +1.47 (±0.09) | `--ab gi-scene`, 512 paired frames; 138.6 against 173.6 fps |
| Probe update (`gi` stage) | 0.71 | the same run |
| of it, scene build (bottom and top levels) | ~0.20 | `MEITOU_PASS_STATS=1`, `--fly-benchmark 300 --fly-speed 0` (serialised) |
| of it, trace | ~0.55 | the same; the trace and blend are separate native segments for this |
| of it, blend | ~0.28 | the same |
| Shading reads | +0.74 (±0.10) | `--ab gi-shade` |
| of it, foliage | +0.27 to +0.32 | both runs |
| of it, reflection pass | +0.16 to +0.21 | both runs |
| of it, objects | +0.12 to +0.17 | both runs |
| of it, terrain | +0.15 | both runs |
| Render thread | +0.23 | `--ab gi-scene` |

- The serialised pass meter's parts sum to about 1.0 ms, more than the 0.71 ms of the pipelined `gi` stage; read them as proportions
  (scene ~20 %, trace ~53 %, blend ~27 %).
- **Half the cost is reading the probes, not updating them.** Every shaded pixel blends 8 probes with the visibility test, in every pass
  that runs `kenshiLight`. The foliage pays for its overdraw, and the water reflection pays for a second view of the scene.
- The blend is large for what it does (64 rays into 8 × 8 and 16 × 16 tiles); the distance tile is most of it.

**Cheaper reads and updates** (2026-10-09). **Measured** (idle GPU, same swamp command, 512 paired frames each):

| | Before | After |
| --- | --- | --- |
| Whole GI (`gi-scene`), frame | +1.47 ms, 138.6 / 173.6 fps | +0.94 ms (±0.11), 158.7 / 186.6 fps |
| Probe update (`gi` stage) | 0.71 | 0.47 |
| of it, sleeping probes (`gi-sleep`) | | −0.16 |
| Shading reads (`gi-shade`) | +0.74 | +0.37 (±0.09) |
| of it, water reflection (`gi-reflection`) | +0.16 to +0.21 | 0 (the switch: −0.14) |
| of it, foliage | +0.27 to +0.32 | +0.13 |
| of it, terrain / objects | +0.15 / +0.15 | +0.15 / +0.17 |

- The rest of the update's saving (about 0.08 ms) is the inner-texel blend.
- With GI the p99 frame is still worse than without (104 against 127 fps in the `gi-scene` run).

*From the code:*
- **No probes in the water reflection.** The reflection's draws keep the sky's flat ambient: `GiProbes.InReflection` is set around the
  reflection pass, and the frame block is re-read per native segment, so `uGiParams.x` is 0 there. The bench's `gi-reflection` switch
  (side B) reads the probes in the reflection again.
- **Foliage without the visibility test.** The foliage mesh and grass fragments define `GI_NO_VISIBILITY`: 8 fetches per cascade instead
  of 16, over the foliage's overdraw. Leaking light through walls matters little on leaves.
- **Sleeping probes.** The blend marks a probe whose rays found no surface within reach (the cell's diagonal plus the lookup's offset,
  0.3 of the smaller spacing; back faces count as near) as asleep: state w 2 instead of 1. A sleeping probe at the same place is traced
  and blended only every 8th of its updates (`SleepPeriod`; `rayLength.z` in the trace and blend parameters), with a new random rotation,
  to find out whether something came near; on the other rounds its workgroup returns at once and its tiles keep their values. No pixel
  and no bounce weights such a probe, by construction. The bench's `gi-sleep` switch (side B) traces every probe. **Open:** a thin
  surface between the rays can be missed until the next check.
- **Blend of the inner texels only.** The blend computes the 8 × 8 irradiance and 16 × 16 distance texels (one and four per invocation,
  instead of two and six rounds over the bordered 10 × 10 and 18 × 18 tiles), keeps them in shared memory, and then writes every texel,
  the borders copying their inner texel. cos^50 is five multiplications instead of `pow`.
- **Picture** (Squin at 13:00, the canyon street, 1920 × 1080, against the shot before these changes): mean difference 0.08, max 9.
  Vulkan validation: 0 errors.

**Observed**, in pictures:
- Hub at midday: the change is subtle on open ground. On objects, the blue sky ambient turns into warmer bounce light (mean difference 1.8).
- Low sun (`7.5`) in an alley: the shade is much darker and lit by warm bounce light (mean difference 21).
- **Open:** the overall level may be a little dark (energy lost through the constant object albedo, and no foliage in the scene).

## Open

- **Terrain resolution.** The fine grid matches the drawn terrain only roughly (16 against the fine window's spacing). Building it from
  the streamed fine window directly would remove the culling workaround.
- **Compaction** of the object structures, if the memory grows with longer ranges.
- **Probe update cost.** The fixed part (full barriers; the update could run on an async compute queue beside the shadow pass) and
  a cheaper bounce lookup in the trace (no visibility test at hits).
- **Probe relocation.** DDGI moves probes out of geometry instead of only marking them invalid. In towns many probes sit inside walls.
