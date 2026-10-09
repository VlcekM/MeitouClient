# Foliage rendering

Part of the world view of `meitou-viewer` (and the game, which shares `src/Meitou.Rendering`); options, keys and the rest of the world mode are in [viewer.md](viewer.md#world-mode). The game's foliage format and placement rules are in [formats/foliage.md](formats/foliage.md).


Trees, bushes, rocks (the mineable Iron/Copper rocks too) and grass, placed as Kenshi does
([formats/foliage.md](formats/foliage.md)); `--no-foliage` leaves them out and `F` toggles them.

- **Placement** (`FoliageWorld`, `FoliageLayout`), in two tiers around the eye, each worker with its own heightmap handle, overlay
  tile cache and biome map:
  - *Whole* within the near reach (`NearReach`: the longest MEDIUM / CLOSE mesh layer range, 1000, x `RangeSetting`, or the grass
    reach, 1000 x `GrassRangeSetting`, whichever is longer) plus half a zone of prefetch: every layer and the grass.
  - *Far only* from there out to the far reach (`FarReach`: the longest mesh layer range, FAR 8000, x `RangeSetting`): only the FAR
    layers, without the grass coverage, which is nine tenths of a zone's layout (`FoliageLayout.Place(..., farOnly: true)`; exact, see
    [formats/foliage.md](formats/foliage.md#distances): the far instances are the same as the whole layout's). When such a zone comes
    within the near reach it is laid out whole and its groups are replaced.
  - Order: whole layouts first, then far ones, each nearest first, measured from the eye or from where the eye will be in 1.5 s
    (its smoothed velocity; capped at two zones, and a jump such as a camera code is not a motion), so streaming keeps ahead of a
    flying camera. The prediction adds whole layouts ahead; it never adds zones beyond the far reach. Up to three whole and three
    far layouts at a time; whole layouts go to the front of the worker queue (`BackgroundWork.RunUrgent`), ahead of grass pages and
    texture decodes. Zones beyond the far reach + one zone are dropped.
  - Cost per zone (zone 18.22's 7 x 7 neighbourhood, one thread, **Observed** 2026-10-05): whole 70–100 ms, of which the grass coverage
    65–77 ms; far only 8 ms (reading the ground). `Meitou.Data` is built optimised in Debug too (`-p:MeitouDebugData=true` to debug
    it): unoptimised, a whole zone took 0.6 s and a far one 78 ms, too slow for any flying camera.
  - Settling the view at zone 18.22 at x4 (183 zones in reach): 2.9 s and 48,849 placed meshes, against 44 s and 1,004,250 before the
    tiers; the picture is identical (image-diff mean 0, max 0).
- **Pop-in while flying** (fixed 2026-10-05; the user's report: junk, ruins and plants appearing right in front of the camera around
  zone 18.22, whatever the sliders). Those are foliage (the layers TechRustyJunk, Tech_Wreckage01, Motor_WHOLE, Vast_Cluster_Pieces,
  all MEDIUM: 4000 units at x4), not placed objects (none within 2500 units). The cause was zone layout falling behind the camera:
  every zone out to 32000 units was laid out whole (0.6 s each in the Debug build, three at a time), so at Shift speed (about 8400
  units a second at 1400 units above the ground) the zones under the camera were not laid out yet. `--fly-benchmark 1500
  --fly-speed 140 --fly-radius 60000` from `--at -60564,-45142 --distance 1400 --pitch 25` prints a `pop-in` line (the nearest zone
  within the near reach without its whole layout, per frame): before, 608 of 1500 frames had such a zone, nearest at distance 0 (the
  camera's own zone; frames 200 to 900 nearly all); after, none (and within the far reach, only zones within 1 km of its edge are
  missing at times, against zones at distance 0 before). At 2100 units a second neither had any. `MEITOU_FLY_SHOT=<frame>,...` saves
  those frames of the flight as pictures (with `--screenshot`); frame 500 showed bare rock before and the plants, trees and rock
  stacks after. Two draw-side faults were fixed with it: a zone's groups kept the range of the "Foliage draw distance" slider at the
  time they were laid out (the slider only affected zones loaded afterwards; ranges are now taken at draw time), and a zone was
  culled as a box with a fixed margin of 300 units, so a big mesh (the ruins reach 650+ units from their origin, FAR rock pillars
  thousands) whose zone was out of view vanished while still on screen (the margin is now the zone's largest mesh reach).
- **Meshes** (`FoliageRenderer`, `FoliageShaders`): each instance is drawn up to its layer's range (MEDIUM 1000, FAR
  8000, × `RangeSetting`, the game's `foliage range`), measured along the ground, and fades out with a dither over
  the last tenth of it (the game's 10-unit transition is too short to see). Instances of one mesh are one instanced
  draw per part of the mesh and of its leaves mesh, with the shared mesh shader (so the atmosphere's light and haze
  apply). FOLIAGE-mode meshes and leaves cut out on the normal map's alpha (`alpha threshold` / 255, `leaves alpha
  threshold` / 255), double-sided, with alpha to coverage when the scene is multisampled; other modes are back-face
  culled. TRIPLANAR uses the object shader's triplanar path, DUAL modes the vertex-alpha blend. TERRAIN-mode meshes
  (most rocks) go through the terrain shader (`TerrainRenderer.DrawMeshes`, the map-feature rules above), one draw each, and pop at the middle of
  the fade band instead of dithering. DUST tinting is not reproduced. Trees do not sway (Kenshi's object shader
  has no wind).
- **Ranges by size** (the `range` switch, `F6`; Meitou by default, `--faithful range` for the game's): the game ends all of a layer's
  meshes at the layer's range, so at x4 a tree, a ruin wall and a skull of the same MEDIUM layer all stop at 4000. With Meitou ranges each
  mesh is drawn to the range of its size class ([formats/foliage.md](formats/foliage.md#mesh-sizes): bounding radius × largest scale;
  small below 40, medium to 125, large above): large (trees, ruins, wrecks, rock stacks) 50000 (12000 until 2026-10-07), medium (junk, boulders, bushes) 12000 (5000 until then), small
  (litter, small plants) 3500 by default (800 until 2026-10-08). The three distances are settings, not constants: Tab sliders "Large / Medium / Small foliage range"
  (1000-120000, 400-80000, 200-40000, log scale, steps of 50; defaults 50000 / 12000 / 3500: the billboards draw beyond 4000 as one quad per tree) and `--range-large`, `--range-medium`, `--range-small`. The fade band is the same
  rule as before, a tenth of the range (at least the layer's transition), dithered (screen-door noise of the pixel, as objects' LOD fades; Meitou billboards, a few pixels across at long ranges, take a per-instance threshold instead so they do not flicker over pixels: [impostors.md](impostors.md) section 12, **Verified** from the code, flicker gain **Unknown** by measurement). How the old sliders fit in:
  - "Foliage draw distance x" (`RangeSetting`, the game's `foliage range`) still scales every layer in Faithful. In Meitou it only matters for
    the large meshes of FAR layers (the landmark rock formations, 8000 × x, 32000 at x4): they keep the longer of that and the large range,
    so the formations still stand on the horizon. Every other mesh (MEDIUM and CLOSE layers, and the small and medium children of FAR
    layers) follows its class slider alone.
  - "Grass draw distance x" and "Grass density x" are unchanged: grass has no meshes to classify.
  - Until a mesh is decoded its size is unknown; its groups count as the longest class range (for the reload and residency decisions only:
    nothing undecoded is drawn).
  - No pop-in from it: a zone beyond the near reach is laid out with its FAR layers only, so the near reach (`NearReach`, where zones are
    laid out whole) is the longest class range in Meitou (50000 by default, 12000 before 2026-10-07; against 4000 at x4 in Faithful), or the grass reach if longer;
    the far reach covers the FAR layers' range as before. A longer large range costs layout work on the worker threads (whole layouts,
    70-100 ms a zone, for 1.6 times the area at 5000 against 4000), not render-thread time.
  - Cost (docs/engine.md, "Foliage ranges by size"): at the defaults the dense tree view draws 1 202 meshes instead of 5 034 (most of the
    game's x4 set is litter and bushes), foliage stage 3.5 -> 2.1 ms and culling 4.1 -> 1.2 ms of render-thread CPU (Release); the junk field
    671 -> 377 meshes, 2.8 -> 1.75 ms. Settling a still picture takes about twice as long (more zones laid out whole).
  - `--faithful all` (or `--faithful range`) gives the game's ranges: the parity views are unchanged by the switch (0 differing pixels).
- **Impostors** (the `impostors` switch, `F7`; Meitou by default, `--faithful impostors` never draws them, [impostors.md](impostors.md)): beyond
  the impostor distance (default 4000, Tab slider "Impostor distance (F7 Meitou)", `--impostor-distance <u>`) a tree or bush is one
  camera-facing quad sampling a pre-baked atlas, crossfaded with the mesh over a tenth of the distance, and cast into the sun's shadow cascades
  the same way. The atlases (baked on first need, cached in `%LOCALAPPDATA%\Meitou\impostors`) are held within a VRAM budget that follows the card (8% of the driver's budget, at most 1024 MB; 5% and 256 MB on an integrated GPU; x0.75 under VRAM-guard pressure; 164 / 328 / 655 / 983 / 1024 MB at 2 / 4 / 8 / 12 / 16 GB), `--impostor-budget <MB>` overrides it ([impostors.md](impostors.md) section 10). Far atlases keep fewer mips (`MEITOU_IMPOSTOR_FARMIP=0` off, `MEITOU_IMPOSTOR_MIP_MARGIN` 1 level); a plan keeps the nearest, degrades the farthest and only then leaves a mesh as a mesh. Meshes under radius 48 with 100 or more triangles have a small class (32 px frames, transition by size; `MEITOU_IMPOSTOR_SMALL=0` off, `_SMALL_MIN_RADIUS`, `_SMALL_MIN_TRIANGLES`, `_SMALL_FRAME`, `_SMALL_GRID`). `MEITOU_FORCE_INTEGRATED=1` makes the budget rule treat the card as integrated (testing). The benchmark prints a `pop-in    impostors:` line. `MEITOU_IMPOSTOR_CASTERS=0`
  keeps mesh casters, `MEITOU_IMPOSTOR_LOG=1` logs atlas loads and evictions. The disk cache is capped at 512 MB by default
  (`--impostor-cache-mb <MB>` or `MEITOU_IMPOSTOR_CACHE_MB`, 0 = no cap): older files go first, files of an older format or baker version
  at once ([impostors.md](impostors.md) section 4). `MEITOU_IMPOSTOR_BAKE_MSAMPLES` (40) is how many million shaded samples of a bake are
  recorded in a frame (section 8). `--fly-benchmark` leaves the first n frames out of its percentiles with `MEITOU_BENCH_SKIP=<n>` (default 1;
  a cold run's first 30 frames load the first zones). Tuning knobs for the baker: `MEITOU_IMPOSTOR_GRID` (12),
  `MEITOU_IMPOSTOR_MAGNIFY` (1.4), `MEITOU_IMPOSTOR_BIAS` (1; the caches differ by them).
- **Generated levels** (the `lod` switch, no F key: the Tab panel's checkbox, `--faithful lod` draws every instance at full detail as before; Meitou by default;
  [formats/foliage.md](formats/foliage.md#generated-mesh-levels-in-the-viewer-meitou-2026-10-08)): the TERRAIN-mode rocks and plants have no LOD levels in the game and some have thousands of
  triangles (`FOLIAGE_Plant_Swamp-TwigLarger`, 5000 triangles, 437 instances in the swamp view), which made them the biggest part of the foliage GPU time. Meitou makes 50 / 25 / 10 % levels
  on the workers when such a mesh loads (quadric edge collapse, `MeshSimplifier`; cached in `%LOCALAPPDATA%\Meitou\lods`, `MEITOU_LOD_CACHE`), measures how far each deviates from the original,
  and the GPU cull gives each instance the coarsest level whose deviation shows less than 4 pixels of the render (`MEITOU_LOD_PIXELS`), in a shadow cascade 2 texels (`MEITOU_LOD_TEXELS`), so thin
  meshes (a sheet, a twig) keep their detail until they are far. `MEITOU_LOD_LOG=1` lists the meshes and their levels, `MEITOU_LOD_ALT=1` alternates levels per frame for same-run timing.
  Since 2026-10-09 (`lod-far` switch, A/B-able; [formats/foliage.md](formats/foliage.md#generated-mesh-levels-in-the-viewer-meitou-2026-10-08), "Rougher levels"): levels with a shading angle up to 1.7 rad are kept (the swamp twig gets 25 and 10 % levels) and past `MEITOU_LOD_FAR` (2500 units) the pixel tolerance grows with the distance; swamp view at 1080p DLAA **Observed** -0.40 +-0.17 ms GPU total (foliage -0.26 +-0.05), colour-view rock triangles 1641 k to 1068 k.
- **Culling** (`FoliageCull`, step A1 of the GPU-driven foliage in [renderer-native.md](renderer-native.md#56-parity-what-can-match-exactly-and-what-cannot-be-promised)):
  each zone's instances are records of 96 bytes (`FoliageInstanceRecord`: transform, bounding sphere, ground position, scale, group index:
  the layout a compute shader will read), the spheres filled once when the mesh's bounds are known instead of per frame; a group's range
  as range² and the band as a reciprocal; the planes' normal lengths once per view; and the batches drawn in the order of their mesh's
  first group in the work list (visible or not) instead of their first visible instance. Pictures unchanged: 0 differing pixels in the
  ten parity views (Debug, `--faithful all`; Release with Meitou's other switches, `--faithful range`), except the rock view, which differs
  between two runs of the base build itself (21 pixels at 13:00, 6 at 02:00, one spot at about (716-725, 302-323), the fenced wall left of
  the rock; cause **Unknown**): A1's rock pictures equal one of the base runs' exactly, and the Release
  Meitou run differs from the base in the same spot (31 pixels). Tests `FoliageCullTests`.
  - **GPU cull** (step A2, [renderer-native.md](renderer-native.md#561-as-built-step-a2-2026-10-06-foliagegpucull)): by default the meshes' instances
    are tested by compute kernels per view (main slices, cascades, reflection) and drawn with indirect draws; the TERRAIN-mode rocks too, in the same dispatch, drawn through `TerrainRenderer.DrawMeshesIndirect` with their biome rows and mirroring.
    `MEITOU_GPU_CULL=0` brings back the CPU cull above (for A/B); `MEITOU_GPU_CULL_VERIFY=1` runs both, compares the GPU's lists a frame later
    with the CPU's (sets, order, every matrix and fade, the draw arguments, every rock placement) and prints a `gpu cull verify` line at exit. Pictures unchanged (0 px); the instance arena holds 68-byte records (`FoliageInstanceRecord.Pack`, `gpu cull verify` equal);
    the grass too: its pages are picked, thinned and ordered by two kernels per view and drawn with one indirect draw (`MEITOU_GPU_GRASS=0` for the CPU walk; [5.6.2](renderer-native.md#562-as-built-wave-3b-gpu-grass-2026-10-06-foliagegrassgpu));
    `MEITOU_FOLIAGE_TIMING=1` adds the dispatch recording, the rock draws per call and a `foliage gpu cull:` line with the kernels' GPU time per view.
- **Video memory guard** (2026-10-07, [renderer-native.md 8.11](renderer-native.md#811-video-memory-at-large-ranges-2026-10-07)):
  at about 90% of the driver's device-local budget (VK_EXT_memory_budget) streaming of new zones, grass pages, textures and meshes stops, idle
  caches are evicted after 2 s, and the foliage, object and distant-town ranges (and a shadow distance above its default) are multiplied by a
  falling scale until the use is under 80%; the ranges return slowly under 74%. F11 shows `guard: ok, N% of the M MB budget` or
  `guard: ranges x0.62, streaming paused, ...`, and the console logs one `vram      guard:` line the first time. It never acts at the default
  settings. Knobs: `MEITOU_VRAM_GUARD=0` (off), `MEITOU_VRAM_BUDGET_MB=<mb>` (pretend a smaller card, for testing), `MEITOU_VRAM_KILL=<fraction>`
  (headless runs: exit with code 9 past that share of the budget, default 0.95, 0 = off; the process is terminated outright, without
  unloading the driver, so it can't hang on the way out and keep holding its video memory). `--shadow-range` is capped at 15000 on the command
  line. The F11 `scratch` line and the benchmark's `scratch` line give the foliage cull's and grass kernels' per-frame memory (held, need, refused).
  Beyond the whole reach (the longest of the small, medium and grass ranges) a zone keeps only large meshes; the instance arena takes 68 bytes
  a record. All pictures unchanged (0 px).
- **Object textures and meshes at large ranges** (2026-10-07, [renderer-native.md 8.14](renderer-native.md#814-object-textures-and-meshes-at-large-ranges-2026-10-07)):
  what is in range is no longer held at full detail. An object texture loses the top mips no pixel can sample at the distance of its nearest user (the pixel footprint
  from the field of view, render size and sampler bias, over how large a texel is: the part's `MeshTexelScale`, the instance's scale, the material's tile; two levels of margin;
  not triplanar materials, not the distant towns' atlas, not textures of 512 or less) and is loaded again, the old image drawn until the new one is in, when something comes near; a mesh is
  decoded with only the LOD levels (and vertices) its nearest instance can draw and remade in place when one gets nearer. Object textures at the forest view, default / all20 / all40 / `max`:
  536 / 1252 / 1490 / 1725 MB become 500 / 788 / 984 / 1342; object meshes 73 / 273 / 395 / 813 become 53 / 157 / 265 / 666. The caches' high-water marks follow the driver's budget (the old
  fixed marks on the 11.4 GB card) and under the guard's pressure the streaming sheds first. The benchmark's `resident` line shows `mip streaming: N MB less than every mip, R refined, C coarsened`
  and the meshes `remade finer / coarser`; `pop-in    objects:` counts parts drawn untextured, textures waiting for a finer image, instances held back for a mesh remake. Knobs: `MEITOU_MIP_STREAM=0`,
  `MEITOU_MESH_STREAM=0` (the old behaviour, for comparisons), `MEITOU_MIP_MARGIN`, `MEITOU_MIP_TOP`, `MEITOU_MIP_SKIP=<names>`, `MEITOU_MIP_LOG=1`, `MEITOU_TEX_STATS=1` (a texture summary at exit).
  Pictures unchanged: 0 px in the ten views, both modes.
- **Grass**: blades are generated per 576-unit page (8 × 8 a zone) on worker threads when the page comes within
  the grass range (`FoliageGrassField`, the game's candidate rule; seeded per page, so deterministic but not the
  game's exact blades), uploaded as one instance buffer per (page, grass type), and drawn nearest page first with
  quads built in the vertex shader: sway along X on the top edge for wind layers, (held still in offscreen renders, so pictures repeat exactly; interactively it follows real time), sinking into the ground over
  the last fifth of the range, sprite alpha cut at 0.6 (alpha to coverage when multisampled), colour map over the
  zone, lit with an up normal; the aerial perspective is evaluated per vertex.
  Pages are generated nearest first, at most 12 at a time over all zones. **Density**: a page is generated for the
  highest density setting (`MaxGrassDensity`, 2) and records, per 1/64 of its candidates, how many blades they gave
  (`FoliageGrassField.BladesWithPrefixes`). The candidates come from one random sequence in order, so the blades of a
  lower setting are exactly the first ones of a higher setting's (test `Lower_grass_density_is_a_prefix_of_a_higher_one`):
  the "Grass density x" slider draws that prefix of every loaded page at once, no page is regenerated, and the
  game's rule (`density x 0.005 x area x setting` uniform candidates) is unchanged. Memory is that of density 2.
- **Range** (what limited it, fixed 2026-10-04): foliage was drawn only in the near depth slice, i.e. up to about
  20400 units (`SplitDistance` 20000 x 1.02), so the FAR layers (8000 x range setting) were cut short at x4 (32000):
  it is drawn in every slice now (`Draw(..., continuation: true)` for the second, counts and GPU time summed). Zones
  were laid out only to the mesh reach, so a grass range above the foliage range loaded no grass: the reach is the
  longer of both now. The cull skips a whole zone outside the frustum before its instances. Checked with
  `MEITOU_FOLIAGE_RANGE`, `MEITOU_GRASS_RANGE`, `MEITOU_GRASS_DENSITY` (start values of the sliders): at x4 trees,
  rocks and grass appear out to 32000 / 4000 units (screenshots forest zone 14.30 at x1 and x4).
- **Range cost** (RTX 4070, 1600 x 900, 4x MSAA, offscreen, 2026-10-04; GPU times vary 2-5x between runs while other
  viewers run, so the quietest of several runs is given first): zone 14.30 from 5000 units out at x4: 180 zones laid
  out in 9 s (518k meshes, 205 grass pages, 628k blades), 1091 meshes drawn, 313 draw calls, draw CPU 1.5-1.9 ms,
  GPU 1.0 ms (up to 4.5 ms contended), frame 4.6 ms; The Hub and Squin from outside their walls at x4: 0.6-1.2 ms
  GPU, 1.1-1.7 ms CPU. At x16 (measured before the sliders were capped at x8): 700-2100 zones (the whole world is in reach), 2-7M mesh instances
  (about 80 bytes each, up to ~0.5 GB), 2600 grass pages with 5.8M blades in the forest, 3400-5600 draw calls, draw
  CPU 25-70 ms, GPU 30-150 ms, layout 45-190 s: x16 is not usable (sliders stop at x8, not measured). x4 is within 3 ms GPU for
  foliage when the GPU is not shared.
- **Reflections** (`ReflectionPass`; what the game does is in [formats/terrain.md](formats/terrain.md#shading-observed-waterhlsl)). **What is drawn follows the game's `water reflection` and `reflection range`** (`--water-reflection 0..4`, viewer default 4 (the game's missing-key default is 2: sky and terrain only; 3 adds objects, 4 foliage); `--reflection-range`, viewer default 3 x haze distance (the game's is 0.6); Tab sliders; details and what is not done in [formats/settings.md](formats/settings.md#viewer-water-reflection)); the cut-downs below say how much of it. The
  mirrored scene is drawn into a half-resolution 4x multisampled texture, and what it draws is cut to what a half-resolution, ripple-distorted
  image can show. Foliage meshes (no grass) and objects reach 3000 units from the eye (`FoliageDistance` through `FoliageRenderer.Draw(...,
  maxRange)`, which also stops the pass walking the far zones; `ObjectDistance`; before, the foliage went as far as the picture's own layers, up to 32000
  units), objects choose their LOD level at 3 times the distance (`ObjectLodBias`, through `WorldObjectRenderer.LodBias`), and the mirrored
  terrain measures its LOD error with half the pixel scale (`TerrainLodScale` 0.5: its target is half the size; its own quadtree: `TerrainRenderer.Draw(..., secondary: true)`, so the two passes do not reset
  each other's ranges each frame; 37 to 50% of the triangles in the two test views). The pass learns the framebuffer to return to from the caller (`RestoreFramebuffer`) instead of
  three `glGet` calls: with the driver's threaded optimisation those waited for the driver thread and cost 2 to 4 ms of CPU a frame. A finished
  image is reused for up to 3 frames (`MaxAge`) while the eye moves less than 3 units plus 0.4% of its height above the water and the view turns less than
  about 0.1 degree, so a still camera redraws every 4th frame (the water samples the image with the matrix it was drawn with, so
  the only error is that parallax). Knobs for experiments: `MEITOU_REFL_FOLIAGE`, `_OBJECTS`, `_LOD`,
  `_TLOD`, `_AGE` (the defaults are 2000, 2200, 3, 0.5, 3 since 2026-10-09, 3000 and 3000 before, see "Cheaper reflection"; `MEITOU_REFL_FOLIAGE=1e9 _LOD=1 _TLOD=1 _AGE=0` restores the old limits; the "before" numbers below used it, so they already include the glGet fix, which alone took the pass from about 5.6 to 1.9 ms of CPU in fly-speed-40 runs; the last A/B runs against a HEAD build were lost to other programs saturating the GPU).
  `--fly-benchmark` prints `reflect`: passes drawn and reused, the pass's CPU and GPU time (timestamp queries, a few frames late; the first
  pass is left out), and the CPU time by part; the worst frames name the part of a slow reflection.
  **Cheaper reflection** (Meitou, 2026-10-09; `--ab refl-cull` flips all of it, off = the earlier 3000-unit reach and no culls; measured on the swamp target view in [bench.md](bench.md), **Observed**: -0.53 ms GPU at 1920x1080 native DLAA, the pass 1.00 to 0.59 ms). The ranges are now 2000 (objects, `ObjectDistance`) and 2200 (foliage, `FoliageDistance`), measured from the mirrored eye. Beside them, things that no water shows are left out, by construction and not by a threshold: (1) `ReflectionPass.MayShow` (an object's bounding sphere, set on `WorldObjectRenderer.MirrorCull` around the pass's object draw): a thing's image lies on the water plane where the ray from the eye to its mirror image crosses it, `eye + t (P - eye)` with `t = h_eye / (h_eye + h_P)` (heights above the water); the footprint of the sphere's image (discs of radius `t r` along the segment from the highest to the lowest point, padded by 60 units for the ripples' shift of the lookup, at most 0.04 of the texture) is sampled on a grid of at most 9 by 9 points a 100 units apart in the terrain heights (`HeightSnapshot`), and the thing is kept when one is under water level + 25 (the breakers' run-up); wholly below the water, or under 1.5 texels of radius in the half-resolution image, it is left out too, and with the weather fog complete at the distance (`FogVolumes.AtmosphereDistance`) nothing of the water beyond it is looked up (the water ends in `atmoApply`). (2) `WaterRect`: a grid of about 20 pixel cells; each ray down to the plane that meets ground under the water level is projected with the reflection's matrix, and the box of those points (padded by 0.08 for the ripples) replaces the frustum's side planes for every depth slice, so terrain chunks, objects and foliage zones outside it are skipped; with no such cell the pass is not drawn at all (the water then shows the sky colour, and there is no water to see). **Observed**: in the swamp target view the box covers the whole picture (far low ground is water), and `MayShow` keeps 70 to 85% of the instances; the gain is in the ranges; on a low view the culls give a bit-identical picture, the ranges change 314 pixels by 12 or more (max 24). **Unknown**: how much the culls save in views with dry ground around a pond (not measured), and what the shorter ranges cost in other weather.
  **Numbers** (**Observed**, 2026-10-05, RTX 4070, 1280 x 720, 1500 frames, `--fly-benchmark` with the default 150 units a frame, three runs each, on a
  machine that other programs share, so only the means of the pass are firm; frame percentiles moved by 2x between runs of the same build, and
  the runs with `--no-reflections` were as noisy): Port North (mostly desert, little water): the pass 0.73 to 1.02 ms CPU and 0.90 to 1.23 ms GPU
  per frame before, 0.58 to 1.02 and 0.66 to 1.24 now (p99 of the frame 17.6 to 36.6 ms before, 20.8 to 70 ms now, 17.0 to 42.2 without reflections: no
  difference that the noise does not cover). Shark (swamp, forests; the pass dominated by foliage): 1.40 to 1.63 ms CPU and 2.40 to 2.48 ms GPU
  before, 1.14 to 1.29 and 1.72 to 1.99 now (-20%, -25%); frame p99 27.6 to 55.7 ms before, 29.2 to 30.8 now (26.4 to 38 without reflections), the
  slowest pass 101 ms of CPU in one run before (12 to 14 ms in the others), 12 to 28 ms now. A still camera (Shark, `--fly-speed 0`): 450 of 600 frames reuse the image, 0.20 ms CPU
  and 0.38 ms GPU per frame. Looks: the same views with the old (HEAD) and the shipped settings differ by a mean of 0.002 to 0.08 of 255 over the picture
  and 0.26 over a pond with mirrored stilt houses and trees (0.36% of its pixels by more than 12, shipped LOD 3 against HEAD; a view of the same scene run twice differs by 0.003);
  pushing it further (foliage 1500, LOD 4, terrain 0.25, objects 2000) loses the trees' reflection in the pond (0.6, 1.2%). Not done: drawing
  the reflection spread over frames (a rendered frame costs the same, so the 99th percentile does not move), fewer samples or a smaller texture
  (the shoreline's stair steps came back).
- **Cost** (RTX 4070, 1280 × 960, 4x MSAA, offscreen, 2026-10-04): a cypress grove in zone 14.30 (995 meshes,
  68k blades) 0.6–1.2 ms draw CPU, about 2 ms GPU (timestamp queries; the F11 statistics show both); the grassland
  at the zone's centre (128k blades) 0.2 ms CPU, 0.3 ms GPU; frame 2.8 ms against 2.3 ms with `--no-foliage`.
  A 20640-unit `--fly-to` from zone 14.30 eastward: median 3.9 ms against 2.8 ms, 99th percentile 46 ms against
  30 ms (uploads are held to 2 ms a frame and one texture a frame). The log line `foliage` gives the totals;
  `MEITOU_FOLIAGE_DEBUG=nograss` or `nomeshes` leaves one part out.
- Verified with screenshots (2026-10-04, saved outside the repo): zone 14.30 (cypress grove with cut-out canopies,
  scattered rocks, grassland with four grass types), swamp zone 24.38 (swamp plants and ferns), an Iron Resource
  rock in zone 30.30 (`--at -8777,-9105`), each against `--no-foliage`.
- **Cost of the parts** (**Observed**, 2026-10-09, RTX 4070, `--view swamp --ab <switch> --ab-period 16 --bench-frames 640`, 1280 x 720, TAA, master 271f21e): the grass (91 600 blades) costs 0.18 +-0.02 ms GPU a frame (3.51 against 3.33 ms total; 283 against 299 fps), the foliage meshes and impostors 0.52 +-0.02 ms (3.48 against 2.96), so at this size neither is what keeps the swamp slow by itself; the TERRAIN-mode rocks are not in either switch.
- Not reproduced: the game's exact grass blades, DUST tint, translucency, wetness, shadows, sub grass, ambient
  sounds, collision; mesh LOD levels (pages use the full mesh, as PagedGeometry's batches do).


- **Triangle sizes and discarded texels** (**Observed**, 2026-10-09, `--bench-tris`, [bench.md](bench.md#triangles-per-pass-and-triangle-sizes---bench-tris)): on the swamp the foliage meshes shade 5.54 M fragment invocations (2.67 a pixel) from 321 k clipped triangles (835 k submitted), and only 1.54 M (28 %) survive the alpha test; grass shades 0.91 M, 26 % surviving; the TERRAIN-mode rocks submit 582 k primitives, 317 k leave the clipper and 115 k fragments run (the rocks share the objects' late depth test through the cross-fade `discard`: forcing the early test took them to 40 k). Of the meshes' visible triangles 44 % are under one pixel and 27 % more 1-4 px^2; the whole foliage pass submits 1.93 M primitives for 0.91 M that leave the clipper. The shading cost is the transparent part of the cards (cut the cards to the opaque outline), the geometry cost the triangles nobody sees (a mesh LOD chosen by projected triangle size). The grass motion-vector pass for the upscaler redraws all grass: 515 k primitives, 7.50 M invocations, 0.19 ms.
