# Ray-traced global illumination (Meitou)

Status: **milestone 1** (2026-10-09, branch `global-illumination`): the traced scene and a debug view. Nothing in the normal picture changes
yet. This is a Meitou-only feature with no counterpart in the game. Kenshi's indirect light is one flat hemisphere ambient per biome
(`ambient light`, `KenshiLighting` / `AmbientMap`, [formats/lighting.md](formats/lighting.md)) plus SSAO, which the game ships switched off.
It is engine work, so the claims carry where they come from: *from the code*, *measured* (how), or *open*.

GI is an **optional quality mode** (DECISIONS 24): it needs a GPU with ray queries, it does not count against the frame-time targets of
the default picture, and `--faithful` keeps the flat ambient. The owner's target with it on: about 144 fps at 1080p with DLSS at a 0.67
render scale.

## Plan

1. **Milestone 1 (done)**: device support, acceleration structures for the terrain and the objects, a debug view that traces from the
   depth buffer. The question it answers: can the acceleration structures for Kenshi's world be kept up to date cheaply? Yes (see
   "Measured").
2. **Probe GI** (DDGI-like): a camera-centred grid of irradiance probes traced against the same structures, sampled by the material
   shaders in place of the hemisphere ambient (`mix(uAmbientGround, uAmbientSky, …)`, e.g. `TerrainShaders`). This fits the forward
   renderer as it is: no new targets, and the change is a compile-time variant, so the Faithful SPIR-V stays byte-identical.
3. **Per-pixel GI** (ReSTIR GI, DLSS Ray Reconstruction): needs normal and albedo targets and a composite pass. These are changes to every
   renderer's fragment outputs, so this step comes after the probes.
4. **Fallback** for GPUs without ray queries (the integrated-GPU laptop): sky irradiance in spherical harmonics plus terrain horizon
   occlusion.

## Device (`VulkanDeviceOptions.RayTracing`)

*From the code.* Asked for by `--gi-debug`, through `VulkanDisplay.RayTracing`. When the device has `VK_KHR_acceleration_structure`,
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
- **Not traced yet:** foliage (trees, bushes, the TERRAIN-mode rocks drawn by `FoliageRenderer`), grass, characters, water.

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

## Open

- **Foliage in the scene.** Trees and the TERRAIN-mode rocks are most of what shades the ground in the swamp. The foliage meshes are GPU-culled
  instances in arenas, so their top-level instances would best be written by a compute pass from the same instance data.
- **Terrain resolution.** The fine grid matches the drawn terrain only roughly (16 against the fine window's spacing). Building it from
  the streamed fine window directly would remove the culling workaround.
- **Albedo at hits.** The material's diffuse texture through the bindless table, needed for coloured bounce light (red sand lighting the
  walls).
- **Compaction** of the object structures, if the memory grows with longer ranges.
