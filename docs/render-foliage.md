# Foliage rendering

Current engine contracts; original placement/material/range facts: [formats/foliage.md](formats/foliage.md).
Atlas format, transitions and residency: [impostors.md](impostors.md). GPU API: [native renderer](renderer-native.md).

## Rendering and streaming

`FoliageWorld`/`FoliageLayout` build deterministic zone layouts on workers with separate height,
overlay and random state. Fine/coarse tiers and velocity lookahead prepare zones before entry;
layouts, GPU resources and visibility are separate lifetimes. Keep ready data until replacements
publish.
Which zones are laid out and kept, and which grass pages made and kept, adds the eye's height above the ground beyond the game's highest camera
to the ground distance (`FoliageRenderer.Load`, [engine](engine.md#eye-height)). Drawing still measures across the ground.

`FoliageRenderer`/`FoliageShaders`: race-independent world instances, material modes, alpha test,
lighting/wind and shadows. Faithful uses layer ranges and full-detail rules. Meitou `range`
chooses size-class distances; `lod`/`screen-lod` choose generated geometry by projected size;
`impostors` draws atlases only when ready, otherwise meshes. Class range and billboard transition
are independent. Generated LOD/cache keys must track geometry changes; camera motion must not
cause fine/coarse levels to oscillate or evict a still-needed mesh.

`FoliageCull` supplies ordered zone/group/chunk work; `FoliageGpuCull` selects/compacts instances
and indirect draws separately for the scene, the reflection and each shadow cascade.
GPU grass uses selection/thinning kernels and indirect arguments. Two kernels a view (`FoliageGrassShaders`): the first decides each slot and appends the ones that draw to the view's list; the second ranks that list by (distance, tie) for the draw order. The ranking is all pairs, so it runs over the draws only: over every slot (22 000 to 69 000 at grass distance x16, a few hundred to a few thousand of them drawn) it cost 0.65 to 1.1 ms a view, two views a frame, whatever was in sight (**Observed**, per-kernel GPU timestamps, town at night and desert outpost: 1.56 -> 0.07 ms and 2.21 -> 0.21 ms a frame; **Verified**, `FoliageGrassGpuTests`: the draws and their order against the CPU's). A page's box is its square and the heights its blades span (lowest root to highest tip; the centre's ground height with 2000 units either way was no bound). In the main view a page whose box (grown by half the patch's widest quad and its sway) lay wholly behind the last frame's surfaces is left out, by the meshes' occlusion rule (`FoliageShaders` `HizOccluded`: the previous frame's depth pyramid, the eye's step allowed for; a box within 40 units of the eye's plane or leaving that picture is drawn): the cull is per page of 576 units, so every page inside the view's volume drew all its blades, hidden behind the near ground or not (**Observed**, paired in one session, 2560x1440: desert outpost close-up 2.66 M blades with none on screen -> 0.10 M, grass stage 1.95 -> 0.15 ms of GPU; grass biome 8.28 M -> 1.49 M, 5.94 -> 1.26 ms; a paused frame with it on and off differs as two frames with it off do). `MEITOU_GRASS_OCCLUSION=0` turns it off. The blades' cost is their triangles' setup: a vertex program that only rejects the vertex costs the same.
Stable selection and complementary
mesh/impostor splits are tested against CPU reference. Prepare resources/constants before parallel
recording; workers record immutable draw jobs, never upload/allocate or mutate render globals.

## Grass

Worker-built 576-unit pages (8x8 per zone) use placement density and terrain rules from research. A page's blades are generated into pooled arrays
(`FoliageGrassField.RentBladesWithPrefixes`) that go back once its last upload step has run: fresh arrays per page were ~4 GB in 50 s of flight
(**Observed**, allocation trace).
Near pages draw blades; ranges and paging determine coverage. The blades live in one store (`FoliageGrassGpu`), 160 MB and 32768 slots at
the default grass distance (x 8 the layer ranges), times the distance ratio squared and the kept places (`Areas`, render-objects.md "Kept points") rounded up, at most 8 and an eighth of the memory budget (x 2: 640 MB); raising the setting
past it builds a larger store and makes every page again (`GrowGrassStore`; not when `VramGuard` refuses it), lowering it keeps the store until restart. A page that finds the store full keeps its blades and waits; the nearest waiting ones go first, and up to 16 farther pages a frame give way to them, so the store holds the grass nearest the eye (**Observed**, save missing-grass, x 2: 73,000 pages waiting with nearer ones behind far ones). With 64 of the eye's pages waiting, only pages nearer than the farthest of the 16 nearest waiting are made, and the loading screen stops waiting for grass. **Observed** (save siwa-test4, x 2 in the 160 MB
store): retrying every waiting page each frame cost 15.8 ms of the render thread (trace: `BufferArena.Allocate` and the release of the
partial allocation), and the pages past the store were never drawn. Pages round kept points ([render-objects.md](render-objects.md#kept-points))
rank after the eye's; while an eye page waits for room the kept pages queue nothing and the one farthest from its point is freed each frame
(frees land after the frames in flight, so a few more may go than needed). The page walk (`UpdateGrass`) is skipped while its last walk left nothing in progress (no job,
no blades waiting, no page without buffers, none wanted), no zone, page or store changed since, and neither the eye nor a kept point moved a quarter page: it would
decide the same (**Observed**, stutter final boss save, paired play-bench runs: upd-foliage 1.1 -> 0.5 ms a frame). It is also skipped while the last walk changed nothing and had nothing in flight though something is still left over (a page without buffers, pages waiting in a full store): such a walk never counted as settled and every page of every zone was walked each frame (**Observed**, siwa-test6 at grass distance x16, two minutes idle: upd-foliage 3.45 -> 0.5 ms a frame once the pages are in; a sample profile had 2.2 of the 3.4 ms in the walk's `KeepBest`). While pages are still being made (about 12 s after a load at x16) the walk runs each frame as before. The draw skips pages beyond their zone's longest grass range before sorting. Sway is held for deterministic
screenshots unless deliberately stepped. `grass-velocity` supplies sway motion only where screen
motion warrants it; camera reprojection handles the rest. Incorrect previous phase/camera/depth
causes temporal ghosting. Keep blade motion consistent between colour/depth/velocity views.

The traced sun builds pages near the eye into structures from the arena: [Traced sun](render-shadows.md#traced-sun).

**Far grass** (`AlphaCover`, the `Far grass cover` row, on by default; engine choice): a grass sprite's mips average its alpha, so a thin mask falls under the fixed 0.6 cut a few levels down (`WiryThorns01-WHITEblue_DIF`, Gut: 7.7 % of the texels pass at the top level, under 1 % four levels down) and the drawn grass vanished a short way from the eye while its traced shadow stayed (**Observed** at Gut from 900 units: dark ground, no grass). The sprites are loaded through `WorldTextureCache.Get(cut: 0.6)`: each BC2/BC3 level's stored alpha is scaled as it is read (BC3: the blocks' two alpha endpoints) by cut / t, t the threshold at which that level keeps the top level's share of texels, never above the cut; the top level is untouched (Castano, "Computing Alpha Mipmaps", as rewritten levels: the shadows read the same texture and stay with the drawn grass). The table is worked out once per file from all its levels and logged (`textures  cover kept in ...`); a cut texture is cached and shared apart from the same file loaded plain. Off: the files' own mips, as the original. **Verified** by same-view pairs at Gut.

**Far grass shading** (grass fragment, `uGrassRayReach`, a frame uniform holding `GiScene.GrassRayParams.X`; engine choice): the traced sun's rays see grass only to their reach (1200 to 2400 units), so past it no blade shaded another and the grass read flat and bright beside the near grass (**Observed** at Gut). From 0.7 to 1 of the reach the sun on a blade is scaled by exp(-0.55 d / max(sun height, 0.4)), d running from 0 at the tip to 1 at the root (Beer's law through the blades above, through `atmoSelfShadow`), and its albedo by 1 - 0.25 d for the sky. 0 (no traced grass) leaves the grass as it was. The numbers are a look, matched by eye to the ray-shaded grass at Gut.

**Traced grass memory** (`TracedGrass`, 512 MB): at the limit a patch nearer the eye takes the place of the farthest structure not asked for this frame (`MakeRoom`); refused instead, dense grass (Gut: 140 structures, 461 MB) left the patches walked up to without shadows until the ones left behind aged out (600 frames), **Observed** in play as flat chunks near the camera.

**Far grass thinning** (the grass cull and `GrassVertex`; engine choice): a page farther than 1500 units draws 1500 / d of its blades (the first of its density order, one in eight at least) and each blade is drawn d / 1500 times as wide (by its own distance, eight times at most), so the cover stays. A blade is under a pixel wide there; with the user's settings (density x2, distance x16) Gut drew 3.97 M blades (**Verified**, paused pair on a free GPU at 2560x1440: 0.99 M after, grass 10.8 to 7.2 ms, frame GPU 23.6 to 18.5 ms, the far view unchanged to the eye). The traced grass structures keep every blade (they reach 2400 units at most).

**Grass under the upscalers** (`GrassTexture`): grass sprites are sampled without the temporal upscalers' LOD bias (-1 at render scale 1, `PostProcess`). A level sharper, the wiry strands are under a pixel wide and TAA and DLSS smeared the moving grass into a grey veil (**Verified** at Gut, paused, mid-rotation frames at 20 to 45 ms a frame: a veil with the bias, strands and clumps kept without; FXAA showed no veil either way; one more level, +1, read blocky in stills).

**Grass depth first** (`FoliageRenderer.GrassDepthFirst`, the GPU grass of a colour view without alpha to coverage): the indirect draw runs twice with the same program, first with the colour masked and the fragment stopping after its cut (`uGrassDepthOnly`, the view's `previousTime` at -1), then with the depth test EQUAL and no depth write, so a pixel is lit once. Blades cover each other many times and every layer the depth test had not yet hidden ran the lighting (**Verified** at Gut, paused A/B in one session at 2560x1440: grass 8.5 to 4.1 ms, frame GPU 18.1 to 13.7 ms, the picture equal within the upscaler's frame-to-frame noise; without any lighting the grass stage is 2.4 ms). The CPU-culled fallback keeps one draw.

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
