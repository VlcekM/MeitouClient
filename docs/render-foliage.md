# Foliage rendering

Current engine contracts; original placement/material/range facts: [formats/foliage.md](formats/foliage.md).
Atlas format, transitions and residency: [impostors.md](impostors.md). GPU API: [native renderer](renderer-native.md).

## Rendering and streaming

`FoliageWorld`/`FoliageLayout` build deterministic zone layouts on workers with separate height,
overlay and random state. Fine/coarse tiers and velocity lookahead prepare zones before entry;
layouts, GPU resources and visibility are separate lifetimes. Keep ready data until replacements
publish. Instances draw in every intersecting depth slice, not only the near slice.

`FoliageRenderer`/`FoliageShaders`: race-independent world instances, material modes, alpha test,
lighting/wind and shadows. Faithful uses layer ranges and full-detail rules. Meitou `range`
chooses size-class distances; `lod`/`screen-lod` choose generated geometry by projected size;
`impostors` draws atlases only when ready, otherwise meshes. Class range and billboard transition
are independent. Generated LOD/cache keys must track geometry changes; camera motion must not
cause fine/coarse levels to oscillate or evict a still-needed mesh.

`FoliageCull` supplies ordered zone/group/chunk work; `FoliageGpuCull` selects/compacts instances
and indirect draws separately for scene slices, reflection and each shadow cascade.
GPU grass uses selection/thinning kernels and indirect arguments. Stable selection and complementary
mesh/impostor splits are tested against CPU reference. Prepare resources/constants before parallel
recording; workers record immutable draw jobs, never upload/allocate or mutate render globals.

## Grass

Worker-built 576-unit pages (8x8 per zone) use placement density and terrain rules from research. A page's blades are generated into pooled arrays
(`FoliageGrassField.RentBladesWithPrefixes`) that go back once its last upload step has run: fresh arrays per page were ~4 GB in 50 s of flight
(**Observed**, allocation trace).
Near pages draw blades; ranges and paging determine coverage. The blades live in one store (`FoliageGrassGpu`), 160 MB and 32768 slots at
the default grass distance (x 8 the layer ranges), times the distance ratio squared rounded up, at most 8 and an eighth of the memory budget (x 2: 640 MB); raising the setting
past it builds a larger store and makes every page again (`GrowGrassStore`; not when `VramGuard` refuses it), lowering it keeps the store until restart. A page that finds the
store full keeps its blades and waits; while full, one waiting page a frame is tried. **Observed** (save siwa-test4, x 2 in the 160 MB
store): retrying every waiting page each frame cost 15.8 ms of the render thread (trace: `BufferArena.Allocate` and the release of the
partial allocation), and the pages past the store were never drawn. Sway is held for deterministic
screenshots unless deliberately stepped. `grass-velocity` supplies sway motion only where screen
motion warrants it; camera reprojection handles the rest. Incorrect previous phase/camera/depth
causes temporal ghosting. Keep blade motion consistent between colour/depth/velocity views.

## Visibility, depth and memory

Bounds/range/frustum, Hi-Z occlusion, fog culls and projected-size LOD are distinct filters.
`VramGuard` scales demand under device-local pressure; textures, meshes, scratch and atlases
must cooperate. Resident mip and mesh detail track projected demand; avoid refine/unload churn.
FrameScratch overflow must leave the affected view safely rather than writing past allocations.
Mesh textures stream mips as the objects' do (`MipStreaming`): the once-a-second residency pass offers each group's nearest box distance over
the mesh's texel size (`MeshTexelScale` of its most stretched part over the material tile) at the group's largest scale, before reading the keys.
Triplanar and TERRAIN materials and grass sprites keep every mip. **Observed** (VRAM dump, 2026-11-01): 808 MB of foliage textures, all at full
resolution, before this.

`early-depth` uses discard-free shaders only for opaque non-fading geometry. Cut-out and fading
geometry retain tests. `foliage-prepass` draws cut-out/dither depth then colour at EQUAL;
`impostor-prepass` applies the same design to atlas quads. Identical coverage, shader inputs,
projection and fade seeds are required in both passes. Extra geometry work can outweigh shading
savings. `impostor-quad` tightens transparent borders using mesh extents; keep conservative bounds.

Card trimming (`FoliageCardMask`, `FoliageCardTrim`, renderer Trim) clips eligible coplanar
alpha-tested cards to a conservative opaque outline, including mip/filter widening. Do not
apply mesh-card assumptions to grass/impostor frames. Reflections obey water-reflection level,
range, mirrored frustum and water visibility; shadows use caster-specific LOD/range.

## Validation and limitations

**Verified** split tests: CPU/GPU mesh/billboard selection complementary; screenshots checked
cypress grove/grass/materials in zone14.30. **Observed** RTX4070 views: subpixel triangles and
transparent leaf cards can dominate work, but trimming/prepasses are not universally faster.
Use [bench.md](bench.md), matching counts, resolution/upscaler and moving/still cameras.
Temporal still-to-still noise is a baseline; do not treat every differing pixel as a regression.
Late layout, unavailable atlases and range fade have separate pop-in causes. Grass prepass,
leaf-card LOD/density changes and other quality tradeoffs require separate validation.
