# Terrain rendering

`TerrainQuadtree`, `TerrainLod`, `TerrainRenderer`, `TerrainTextures`, `TerrainStreamer`.
Original height/blend/map/material facts: [formats/terrain.md](formats/terrain.md).
Engine LOD/streaming rules below; GPU lifetime: [renderer-native.md](renderer-native.md).

## Geometry and material

CDLOD chooses patches by error in rendered pixels, morphing between levels without cracks.
Whole-world coarse heights keep the horizon; a fine moving window follows the eye. Use fine
height directly at weight1, blend at edges. Height/normal sampling must agree between terrain,
objects, water/shore bakes and collision queries.
TERRAIN-mode rock geometry is foliage work, not terrain-patch stage timing.

Biome materials resolve blendinfo slots, mask/overlay/colour maps and layer colour/normal inputs.
Preserve source tile scales, layer order and weights; the engine's texture window does not alter
world coordinates. `terrain-layer-skip` can omit insignificant layer work; thresholds are quality
choices requiring boundary/material checks. `terrain-late-colour` separates depth from shading
where occluders justify it. A depth prepass is extra geometry, not automatically a speedup.

**Layer arrays** (`TerrainTextures`), two tiers per texture pair (a BC3 diffuse and a BC1 normal):
- **Far arrays** hold every pair of the biomes within the material distance (`--material-distance`, default 30000; the "Terrain material distance" row, live: the draw and the streamer read it each frame) at a quarter of the layer size (512² at 2048, about 0.5 MB a pair),
  at a fixed index per pair, at most 256 (the array layers every Vulkan device has; a pair past that stays in ground colour). The R8 height array
  and the relief texture use the same index. A biome is drawn once all its pairs are in.
- **Near arrays** have 32 slots at the full size (about 270 MB at 2048²), 32 per kept place (`Areas`, render-objects.md "Kept points") up to 128 when
  the guard allows: growing reallocates both arrays (no image copy), so every pair drops to far and loads near again; they never shrink, and a refused size is not asked again. A slot goes to a pair within its near range of the eye, then of a kept point while slots are left
  ([render-objects.md](render-objects.md#kept-points)), least recently needed first: the range is the pair's tile × pixels per radian ÷ layer size × 11.3, with an upscaler's negative bias widening it.
  When no slot is left, a pair stays far.
- Per sample the shader works out the near arrays' level (8x anisotropic: the long axis over up to 8 taps, never under the short axis). Below
  `FarShift` (2) less the sampler bias it reads the near slot; otherwise, or without a slot, it reads the far array. For a DDS layer taken as stored,
  both tiers read the file's own mips, so from that level down they are the same texels. A resized layer is filtered separately at each size, so
  a faint seam is possible there (**Unknown** until seen in game).
- **BC1 normal blocks** (`TerrainTextures.LoadNearNormal`, `Deblock`): a 4 x 4 block of the game's BC1 normal maps holds four colours, so magnified it lights as a flat facet with hard edges.
  **Verified** on the siwa-test6 sand (displaced ground, DLSS and TAA): the shading normal set to the smooth terrain normal removes the facets, a constant albedo keeps them, and the facet size
  matches a block on screen. Reading a coarser mip with a spline (an earlier try) only makes bigger world-aligned squares, as that mip is BC1 too. So the near normal layers are decoded at load, made unit vectors (`Normalise`: the game lights with `normalize(rgb * 2 - 1)`, **Verified** in `terrainfp4.hlsl`,
  `gbuffer.hlsl` and `deferred.hlsl`, and 132 of the 165 normal maps of `newland/land/textures` store a blue of 1 over any x and y, mean lengths up to 1.34, **Verified** by decoding them at 512²:
  x and y kept as stored with z rebuilt gave a mean tilt of 61 degrees where the game has 38 on `CinderSurface01_NML`, 90 where it has about 47 at the 90th percentile of 18 maps; the material cache's bake normalises its blended normal for the same reason),
  smoothed by a separable Gaussian (sigma 1.5 texels, wrapping), their mips made from that and stored as BC5 (x and y; the shader rebuilds z): no block grid is left (**Verified**, same
  views before and after). Near arrays are 32 bytes a block pair (BC3 + BC5) instead of 24: +33 % (1024 MB at 96 slots became about 1365 MB, **Observed** from the formula). The far arrays stay
  BC1 as stored (their texels are minified there). Detail finer than a block is not in the file; it is smoothed away, not recovered.
  **Disk cache** (`NormalCachePath`): each deblocked layer is kept in `%LOCALAPPDATA%\Meitou\terrain\normals\<hash>.bc5` (magic `MNB5`, level count, each level's length and blocks; 5.6 MB a 2048² layer),
  hash of a version tag (`NormalCacheVersion`, bump it when `Deblock` or the encoding changes), the size and the source file's path, length and write time, so a mod's replacement gets its own
  file; a missing or misfitting file is rebuilt and written. **Observed** (siwa-test6 start, same machine): a hit takes 3-79 ms of worker time, a miss 100-430 ms.
  **Observed** VRAM: the log line for 32 near and 152 far slots went from 332 MB (BC3+BC1) to 417 MB with BC5 near layers; at 96 slots the formula gives about 1365 MB instead of 1024 MB.
  **Observed** cost: the viewer bench (`--world --at 57400,-119500`, 2560x1440, FSR 0.67, sand views at distance 60 and 400) varies by streaming more than by this change (terrain stage medians
  0.14 ms close and 0.7-0.9 ms mid with the BC5 build against 0.18 and 2.0 ms for a BC1 build of an older commit with other terrain changes): no cost from BC5 is measurable there; **Unknown** under load.
  **Observed** BC3 diffuse blocks: over the 2048² stored diffuse of `DesertSandLoop03_DIF` the mean grey step across a 4-texel block boundary is 2.7 levels against 2.2 inside (grain dominates), `EasySand_DIF` 12.5 against 11.7,
  `FlintCliffs_DIF` 5.7 against 4.7, `Sand3D_DIF` 1.2 against 0.35 (a smooth texture, a one-level step): no block pattern shows, the diffuse is left as stored.
- Params texels 0 and 1 hold the far indices (the relief code reads these too), texel 1's W the biome's relief factor; texels 13 and 14 hold the near slots, -1 for none.
- Before this the arrays held a full-size slot per pair within the 30 km material distance: 128 slots, about 1.07 GB, in the Great Desert
  (**Observed**, VRAM pie and log 2026-11-01).

**World colour map** (`TerrainTextures.PrepareWorld`): the 8 × 8 colour tiles box-filtered into one 2048² RGBA map for the far terrain
and foliage. Cached raw (16 MB, no header) in `%LOCALAPPDATA%\Meitou\terrain\worldcolour_<hash>.bin`: hash of a version tag, the size and
each tile's path, length and write time, so a mod's tile gets its own file; a file of the wrong length is rebuilt. Decoding the tiles
took over a second a start. Old files are never deleted; bump the tag when the filtering changes. `--no-load-cache` skips it.

**Colour window** (`MapWindows`, `TerrainColourCache`): the colour map near the eye, a 4096² texture addressed toroidally (the overlay
window beside it is 2048² RGBA8). It is BC3 (21 MB instead of 85 MB RGBA8), not BC1: the colour tiles' alpha is gloss (**Observed**: all 256
values, 30 % at 255). The 64 tiles are encoded once with 7 levels into `%LOCALAPPDATA%\Meitou\terrain\colour\<hash>.bc3` (magic `MTC3`, side,
the levels' blocks; ~5.6 MB a tile, 342 MB in all), hash of a version tag and the tile's path, length and write time. A start with tiles
missing from it encodes them on workers in the loading step "terrain colour map (first start only)" (**Observed** 5.8 s on a 2026 desktop);
the window then reads only the block rows it needs from those files (`TerrainColourCache.ReadBlocks`; no whole tile is loaded, which cost 11 MB of fresh arrays a tile per window move) instead of decoding PNGs (**Observed** at The Hub: first window 0.6 s instead of 1.6-3 s, a move 0.15 s instead of
0.85 s). Every piece must start on a block at every level, so the window origin is aligned to 128 overlay pixels (256 colour pixels). Tiles
that are not square multiples of 256 keep the RGBA8 path. The overlay window's decoded tiles (1024², the window's 3 x 3) stay between moves,
so a move decodes a PNG only when the window reaches a new tile (**Observed**, headless: a move 13-45 ms and ~0.1 MB allocated, was 170-320 ms
and 17-29 MB with the tiles dropped after each).

**Standby windows** ("Keep textures at characters", [render-objects.md](render-objects.md#kept-points)): a kept point outside the middle of the
drawn window gets a standby copy, uploaded but not drawn, which is swapped in when the eye arrives there (the shaders read whichever is current, so
nothing is composed or uploaded on the jump); the replaced copy stays as the standby for where the eye was. Colour/overlay
(`TerrainTextures.MapStandby`, one per kept place, 2 to 6, ~45 MB each): for a point more than a quarter window from the drawn origin. Fine heights
(`TerrainRenderer.FineStandby`, `TerrainStreamer`, one per kept place, 3 to 8, ~5 MB each at step 1): for a point beyond 0.3 of the window's width from its
centre; the bounds take a standby's patch at its first swap (they only widen). A standby goes when a new point needs its place and no kept point is
in its middle. None is made under `VramGuard` pressure; once made they stay until exit. A standby's read or compose does not hold up the loading screen (its
upload slabs do, for a few frames); a jump while one is read or uploaded waits for it. The first swap-in of a new pair may hitch once (a fresh
texture's first use stalled the driver ~50 ms, see the window comment in `TerrainTextures`). A standby compose shares the nine-tile overlay cache, so the eye's next move may
decode its tiles again.

## Ground relief

Meitou look, not in the game (`GroundRelief`). The parallax and the displacement are Faithful/Meitou switches `parallax` and
`displace` (Tab "Ground parallax", "Ground displacement (tessellation)"; Faithful: flat ground, as the game). Tab toggles "Terrain outline
refinement (tessellation)" and "Rock smoothing (tessellation)" (both off by default, on at the Ultra preset); sliders "Ground relief quality" (High by default; Medium and Low reduce
the march; compiled into the colour programs, one set per quality made on first use: a uniform quality cost High 12% of the terrain's
GPU time, **Verified** by swamp-low A/B), "Ground relief depth (cm, typical ground)" (default 20, set by every preset), "Ground displacement
distance (m)" (100), "Ground parallax distance (m)" (60), "Ground parallax depth (% of relief depth)" (100; `GroundRelief.ParallaxScale`: the parallax's depth alone, the displaced mesh keeps the relief depth, so where both run the march's fine detail is scaled against the mesh); all on the settings' Graphics > World page, where quality and depth hide while parallax
and displacement are both off and each distance while its effect is off; viewer K toggles the parallax. The other values are tuned constants,
expressed at a 20 cm reference depth and settable for a run with the viewer's `--relief Name=value`: target slope 0.2, scale follow 0.5, max depth
80 cm, cliff scale 1, overlay relief 1, self-shadow 0.6, tessellation detail 0.5, refinement 0.5 px, tilt 0.5, broad cut 2 m.

**Heights** (`NormalHeight.FromNormals`, at load, ≤512², an R8 array, one slice per pair slot; no readable normal map: flat, out
of the relief). The normal map's slopes (`dh/du = x/z`, `dh/dv = -y/z` in the terrain tangent frame) are integrated by FFT
(Frankot–Chellappa), below 2 cycles a tile dropped, scaled to the 1st–99th percentiles. A slope field splits into a gradient part
(what a surface makes) and a curl part (what none makes; noise splits evenly); each half-octave band is kept by its own
`(g − c)/(g + c)` (a Wiener gain, the noise measured from the curl) and times `(cycles / 2)^tilt`, weighing broad shapes down against
the detail. Always in the terrain shader's frame, the one the game lights the normal map in, also for maps authored in another:
integrated in their own (mirrored or turned, where they are far more integrable), their bumps faced the sun where the lighting put
shade. **Observed** (Great Desert dunes at 50240, −50298, 12:35, displacement on: correlation at the ripples' scale between the
picture's brightness and the depth-rebuilt normal's N·L): −0.63 with `DesertSandLoop03_NML` integrated mirrored, +0.62 in the
shader's frame; a mesa without such layers +0.50 either way. Layers more integrable in another frame (`(g − c)/(g + c)`, shader's
then theirs): `DesertSandLoop03_NML` 0.25 / 0.89, `Sands_NML` 0.16 / 0.92, `Bedded_Sandstone_NML` 0.66 / 0.86,
`Geobased_Rockloop_NML` 0.28 / 0.72, `Lichen_Rock_NML` 0.15 / 0.61, `Grass-Space_Z_NML` 0.04 / 0.75, `ScarredRock_NML` 0.05 / 0.72,
`Whitestone_Floor_NML` 0.07 / 0.78, `Sand01bc_Grey_NML` −0.30 / 0.60: their bands keep that much less, the last few nearly flat. Grass and mulch (`GrasZ`, `Branch_Mulch`) are largely non-integrable and come out nearly flat; gravels lose their
broad bands; cobbles and pebbles (`RoadLoop01`, `River_Pebbles`) keep all. Lit by a low sun, the heights match the normal maps'
own lighting at a median correlation of 0.88. The game's terrain normal maps are BC1 2048².

**Depth.** Each layer is given the depth that makes its root-mean-square slope the target over its tile (5000 / tiling units):
`slope × tile / (512 × steepness)`, steepness the height's RMS slope per texel over 4-texel blocks (texel noise left out), kept per
slot as `1 / (512 × steepness)` (R32F). That depth, as a multiple of 20 cm, is raised to the scale-follow power (fine textures not
flat, coarse ones not metres deep), capped at the max depth, times the depth slider over the 20 cm reference; the cliff's times the cliff
scale. **Observed** (field tour): the integrated range or normal-map strength as the depth made broad noise deep
(`BurntWoodLike`, `SharpblockLoop`) and fine textures flat or spiky; this rule gives at 20 cm e.g. `WadiGravel` 13–15 cm,
`Rocksurface01` 13 cm, `BurntWoodLike` 29 cm, `SharpblockLoop` 30 cm (1.5× that at the default 30).
A layer's depth is then times its texture's relief share in the softness table (`GroundSoftness.Relief`, the second value of a line of `assets/ground-softness.cfg`; 1 when left out), for the normal maps of fine noise the rule raises into spikes. **Observed** (the Ashlands at 100794, 84294, displaced and with parallax, before and after): the field of spikes there was `Ashland_AshNoise` (the slope layer, 42 cm by the rule), `Ashland-snow` (a nearly flat map tiled every 333 m, 68 cm) and `Ashland_Ash` (35 cm; its map keeps 0.01 of its slope as a surface in the frame it is lit in, 0.85 in its own, mirrored one); at 0.2, 0.15 and 0 the ground is smooth. No measure of the map told these from gravel in the engine (the surface share, or the map's own slope at the 512² level the height is built from: `RockBed01` and `WadiGravel` sit beside the snow), and rules on them flattened `Sands`, `Lichen_Rock`, `Whitestone_Floor` and others (**Observed**, the user), so the share is data. **Verified** (field tour of the 121 layer tiles, before and after, displaced): only the `Ashland_Ash` and `Ashland_AshNoise` tiles change (the `Ashland-snow` tile shows its new depth in its label, not in its picture: **Unknown** why). The tour is not exact from run to run: two road tiles differed between two runs of one build, by which texture tier had loaded when the picture was taken.
A biome's ground and cliff depth are then times its `GroundRelief.BiomeDepth` factor (by BIOMES string id; 1 for the rest), so a
biome's depth can be cut without touching textures other biomes share. ForestLand (`4442-Newwworld.mod`, all of Shark's ground,
also Ancient Labs, Farm Ruin and patches north of Shark) has 1/3: **Observed** (blend and overlay maps sampled round Shark; depths
from `LayerDepthCm` at 30 cm) its layers came out base 37 cm (`GrassBadTestNML`), slope and dirt 67–70 (`Lichen_Rock`), cliff 85,
the swamp biomes round it about 26; those textures are shared by 9–12 other biomes.

**Composite.** Every ground layer adds material; the relief height keeps the heaviest ground layers (base, grass, slope, dirt,
road) by how much of the pixel each shows, from the blend map, overlay window and slope per pixel, not per patch, so neighbouring
patches meet on the same heights: five at High quality, two at Medium, one at Low. One march a pixel (`pixelRelief`): the layers of every
biome blended are gathered by their share of the pixel times the biome's factor (a layer two biomes share is one entry), so all biomes'
ground coordinates move alike. The cliff has its own march, on the projections of the biome whose cliff weighs most, its moves applied to
every biome's cliff. A layer under 5% of the pixel is left out (fading in to 15%). A biome under 3% of the pixel is not shaded at all
(`BiomeEps`, ramping in to 6%; the rest renormalised): each costs up to 14 layer samples.

**Displacement** (needs `tessellationShader`): main-view patches within the displacement distance + 10 units (interior-cut ones
too) draw with tessellated colour and prepass programs (patch lists of 3, culling off, `fractional_odd_spacing`). The vertex
stage tests each corner's coverage once; an edge is split only where its ends have relief, into segments of `max(0.6, 0.004 ×
distance)` over the detail, at most 64, from the edge alone, so shared edges split alike. The evaluation stage (`reliefMove`: one
blend lookup, one normal) moves the point by `Σ depth × (height − ½) × shown` upward for the ground and along the ground's normal for
the cliff (its two vertical projections blended as the material blends them), in full to 60% of the distance, none from it. Each
vertex reads the height at the mip whose texels are half a segment, leaving out what the mesh cannot carry (spikes), less its
level whose texels are half the broad cut (2 m): the characters walk on the undisplaced surface, so broad swells swallowed their
feet; the bumps stay, and beyond the displacement the parallax keeps the broad shapes. Centred:
what stands on the ground sits level with the bumps; troughs below the flat shadow casters look up the sun shadow at the ground's
height (`atmoShadowLift`). The material (layer coordinates, blend, overlay, field, normal) is read at the undisplaced point, passed
in the patches' unused `vNormal`, so a cliff's horizontal projections do not slide up its bumps. Shadow casters, reflections and
the rock bake are not tessellated.

**Parallax** (the patch shader; the fallback without tessellation): one offset is marched through the shown layers' heights by
weight (`Relief`: one unscaled coordinate with its derivatives, and per layer its tiling, slice and weight) along the view ray, moving every layer's coordinate by it, then toward the sun for a soft self-shadow that dims the sun
only (`atmoSelfShadow` in `kenshiLight`; darkening the albedo also darkened the ambient). Centred like the displacement: the flat
surface is the relief's middle, so objects stand in the ground (marched from the top, the ground looks dug out round them). Steps
fall with distance from 16 view and 8 sun steps to 6 and 3 at the parallax distance at High (Medium 6 and 3 to 3 and 2; Low 4 and 2
to 2 and 1), in full to half of it; the sun's march reads the height two levels coarser and is skipped at 0 self-shadow. The view ray takes two steps for each texel of the level it reads that it crosses, between half and four times that count (one a texel passed over the top of a bump one texel wide: teeth along its edge from close up, **Observed**, the user), then halves the step holding the crossing (4 times at High, 2 below) before taking the point between its two ends; the height is read at the level of the footprint's short axis (the long one at most 8 times it). The material cache's march takes its steps and halves its crossing the same way. **Observed** (cracked plain at 136327, 47721, pitch 9, before and after): a fixed 16 steps along a grazing ray cut the relief into slices and the long axis chose a level whose heights were a blur. Leaving the broad shapes out of the march and fading its depth at grazing angles were tried and dropped: the ground lost its depth (**Observed**, the user). **Observed** (field tour of the 121 layer tiles with parallax alone, before and after): a median change of 0.36/255 a tile; past the ash tiles the largest are `GrassBadTest` 3.5, `Lichen_Rock` 3.3 and `Darklayer02` 2.6, none flatter to the eye (`RoadLoop01` at 9.1 is the tour's texture-tier timing, above). Where the
ground is displaced, the view ray marches only the detail finer than the mesh (the height less its level at the segment, 8 steps at High, 4 below),
the whole height again as the displacement fades. A cubic read of the height for the displaced mesh (its level's texels are two segments apart, the bilinear read between them is a tent) was tried and dropped: it rounded every texture's bumps. The
cliff layer is marched on each of its two projections with a weight over 0.1 along the surface's normal (the ray's move across the
face over its depth into it). Heights are read as stored on both projections: the shading turns the `(x, height)` normal map round,
but a texel's height does not change with that (read upside down, `CliffChunky`'s cracks became ridges). The `(z, height)`
projection's tangent frame is degenerate where the normal faces ±X (the game's `n × (−X)`), so its shading matches the height only
for some facings. Not in the rock impostor bake (parallax 0 there).

**Refinement** (needs tessellation): every main-view patch takes the tessellated programs; an edge is also split where the terrain
leaves it (at its quarters and middle) by more than 0.5 px, level `sqrt(deviation px / 0.5)` (a split in n takes a smooth curve's
deviation down by about n²), at most 16, and the new points are put on the terrain (`terrainHeight`); an unsplit edge keeps its
ends, so neighbours meet. Displacement and refinement take the larger level per edge. Shadow casters stay unrefined. On Metal
the tessellation toggles stay hidden and the flat/parallax path is used; a 2026-10-24 prototype was removed for cost and
fallback risk (`docs/metal-backend.md`, "Tessellation").

**Cost** (**Observed**, `meitou-viewer --ab <switch>` at a save's view, RTX 4090, 1080p DLSS 0.83): the terrain stage without
relief 0.86 ms at error 16 (0.96 at 8, 1.34 at 4; error 1, 41 M triangles: 15.6 ms; the slider's minimum is 4); parallax +0.88 ms
(master's base-layer parallax: +0.25; a `Relief` holding a coordinate and derivatives per layer cost +2.3, +0.62 capped at one
layer: its size, not the layers sampled, set the cost); displacement +0.78 ms at 100 m (+0.9 at 25 m, +1.16 at 150 m before the
compact `Relief`) (segments grow with distance, each
ring adds about the same); refinement +0.38 ms at error 16, +0.24 at 32 (crests as smooth as error 4, which costs +0.48), so error
32 with it costs less than error 16 without. Drawing the tessellated patches' colour at once instead of prepass then late colour
cost more (the bumps overdraw themselves).
**Registers** (**Observed**, Nsight GPU Trace, 2560x1440 at render scale 1): with the march in the fragment stage the colour pass held about 220
registers a thread and a tenth of the pixel warps (SM throughput 20%, texture 22%: latency-bound), whether the parallax ran or not (a uniform branch
does not lower the allocation). With the parallax off the colour programs are built without the march (`TerrainShaders.FragmentNative(march: false)`,
`TerrainPrograms.UseColour`; a toggle builds the other set once): the terrain stage 3.37 -> 2.42 ms and the frame 7.0 -> 6.1 ms at a save's view,
the picture unchanged. The interior-cut patches' late colour took 1.4 ms for 19 draws with 8 wait-for-idles (the SMs idle a third of it): its `discard` turned the early
depth test off. Their prepass already discards the cut, so the late colour draws them with the plain program and an equal depth test (both passes
run the same vertex and tessellation stages, invariant positions): GPU frame 6.14 -> 5.80 ms, the picture unchanged (one pixel over 8 levels at a town view).
With the parallax on (60 m, displacement 60 m), nearly all the visible terrain is relief patches. The march held their pixel warps to 12% (21% without it) at the
same register file use, with the SM throughput at 20% (39%). On the RTX path the march's sampling hardly counts: no self-shadow, or the base layer alone,
changed nothing. The march runs before any biome is shaded, so its state does not sit on the shading's: terrain 4.4 -> 4.0 ms, the picture
unchanged. One march a pixel instead of one a biome, the biome threshold and the layer derivatives taken once from the point's (chain rule):
terrain 3.90 -> 3.77 ms (**Observed**, `--play-bench` idle/orbit on save "stutter final boss", RTX 4070 1080p DLSS; screenshot diff 0.001% of pixels
over 12/255). Drawing patches beyond the parallax's end with the march-free program saved nothing there.
**Metal (Observed, 2026-10-25, a town view at 1024x640 scene / 2560x1600 output):** the interior-cut colour draw took 11.25 ms of a 31.03 ms frame,
with a 6.2 KB register spill. With the march off it took 4.28 ms and the frame 19.75 ms, the spill 48 B. That run used an 8/4-step, three-layer march; High's 5 layers and
16/8 steps are **Unknown** on Metal (Medium or Low cut the cost); Metal's shader compiler and tile-memory architecture make the march much more expensive than the RTX path.
The displacement costs 0.42 ms at a save's view (60 m; GPU frame 5.80 vs 5.38 with it off), mostly the tessellated prepass, bound by the primitive
engine (30%). Culling patches off the view in the control stage (corners padded 40 units, sides by 10%) saved 0.03 ms, within the noise: not kept.

**Texture field** (the game's and the viewer's `--terrain-sandbox [cliffs] [x,z]`, `GroundRelief.FieldCentre`): the empty world
(no population, foliage or objects) round a spot, default (38376, -8280), the flattest 2000-unit square of the heightmap found
(plane-fit residual 1.6 units, slope 0.25%). `TerrainTextures.SetField` gives each distinct ground pair (base, slope, grass, dirt,
road of every biome) a 200-unit tile, ordered by layer then name, its biome pinned resident (105 tiles from 58 biomes, 129 of the
133 pairs); with `cliffs` the 36 cliff pairs on 100-unit tiles on an even 45° face at (92164, 123124) facing −Z (residual 2.7 units
over 800, the steadiest steep face found), the camera below it. The patches' material and displacement read the tile's biome row
and layer (`fieldTile`/`fieldMask`) instead of the blend; the relief fades to 0 over a tile's outer 8%. The tile under the camera
is named at the top left with its tile size and depth. In the game's sandbox Esc opens the settings. The viewer's `--field-tour
<dir> [--tour-distance u] [--tour-pitch deg]` saves a picture of every tile and an index.html (105 tiles in about 30 s).

## Resolved colour

Meitou (`TerrainRenderer.Resolve`, `PostProcess.TerrainResolveTargets`, `WorldFrame.DrawSceneTerrain` / `DrawSceneLate`; `MEITOU_TERRAIN_RESOLVE=0` goes back to the
patches' second pass, for comparing). In the main view the patches' colour is not drawn over their triangles: it is one triangle over the picture.
- **Prepass**: the patches' depth pass runs in a pass of its own, into the scene's depth and an RGBA32F target that keeps, per pixel, the undisplaced point less the drawn
  one (the material's coordinates are read at the undisplaced point) and the fragment's depth; 0 where no patch drew. The interior cut discards there as before.
- **Resolve**: after the objects and the foliage, outside the scene's pass, `Fragment` runs for each pixel whose depth is still the prepass's (anything drawn nearer since
  leaves it out): the drawn point from the depth, the material's point by the stored offset, the steps to the next pixels (the footprint of every layer lookup) from the
  neighbouring pixels' points, to the nearer of the two neighbours that still show a patch. No lookup leaves its level to the rasteriser (a discarded neighbour gives no
  derivative): the world's maps read level 0, the region's maps and the dust's noise take the steps.
- **Why**: the displaced patches are tessellated to about 6 pixels a segment; drawn as triangles, the colour program ran for the helper pixels of every small triangle's
  edges as well (they cannot leave early: a stub that returned for `gl_HelperInvocation` after the derivatives gained nothing), and the tessellation ran a second time.
  **Observed** (2560x1440, one session, paused, alternated): desert close-up 2.97 -> 1.92 ms of GPU for the terrain, grass biome 3.28 -> 1.97, sand under spotlights at
  night 4.77 -> 2.87; a town with little ground in view 0.24 -> 0.37 (the target's clear and the pass over the picture). The pictures differ by 0.34 / 255 on average
  where two frames of the same setting differ by 0.21, at most 24 / 255 on about 150 pixels along ripple crests (the steps across an outline).
- Not in the reflection, the viewer's other views or with the wireframe: those draw the patches' colour as before (`ResolveInputs` null).

## Material cache

Meitou, not in the game (`MaterialCache`, A/B `terrain-material-cache`, B: every pixel shades the layers). The patches' layered
material is baked into world-space levels round the eye, and a patch pixel within them reads the cache instead of shading up to five biomes of up
to 14 layer samples each.
- **Levels**: 4 of 2048² texels, level k's texel 0.25 × 2^k units (51, 102, 205, 410 m squares), RGBA8 albedo + gloss and RGBA8 tangent-space
  normal x, y (z follows), rain absorbance, relief height: 128 MB, not made when `VramGuard` refuses it. Each is addressed toroidally (repeat
  sampler, texel = world mod side), its origin snapped to 64 texels round the eye; as the eye moves a level bakes only the strips it gains, and is
  built again only after moving a whole side. A first bake takes 256 rows a frame (its pixels shade the layers until it is whole).
- **Bake** (`TerrainShaders.MaterialBakeFragmentNative`): the colour shader run per texel at its world point (terrain height and normal, the texel as
  the sampling footprint, distance 0, no march), both targets in one pass, written before the blood, wetness and lighting. Its relief height is one
  height field over `BakeSpan` (parallax depth × max depth × cliff scale): the ground's (the march's gather, depth × height) mixed by the cliff's
  share with the cliff's (its projections' heights along the normal, ÷ n.y, held at n.y 0.3).
- **Refresh**: the inputs (bindless indices, overlay window, `TerrainTextures.MaterialVersion` = writes to the biomes' parameter rows, relief settings)
  are hashed each frame; a change re-bakes every level 64 rows a frame, finest first, drawn from meanwhile.
- **Draw**: the level whose texel is the pixel's footprint (short axis, as the layers' 8x anisotropic sampling), blended with the next coarser;
  a footprint finer than level 0's texel (within ~23 m of a 1080p camera), beyond the levels, beyond 0.3 × the nearest resident biome's fade distance (the bake has no distance fade), or on faces with n.y ≤ 0.5
  **Observed** (title screen, DLSS 0.67, `LodBias` -1.58): the layers' sampler takes the upscaler's bias, so the live layers are sharper than a cache level at the pixel's footprint; the level is chosen with the bias (`uBakeBias`), and a level more than one level coarser than that (a close ground beyond the fine levels' square, from a raised eye: the title's nearest band read level 3 for a pixel wanting level 0.6) is not read, the layers are shaded. Edge measure of that band 4.3 (cache) to 5.9 (layers, equal to the cache off).
  (an xz texel spans 1 / n.y of the face: **Observed**, cliffs smeared into vertical streaks, their parallax wavy), the layers are shaded (the
  finer footprint mixed with the cache over the half level below 0, so no line where it switches). Level 0's
  texel is about 5x a layer's (tiling 50: 2048 texels over 100 units); read magnified it blurred the near ground (**Observed**, 2026-11-07). The
  parallax marches the cached height with the march's steps and self-shadow (the sun's from two levels coarser; where displaced, only what the level
  holds finer than one at half the mesh spacing): one read a step, no per-biome work. A per-pixel gather (cliff layers live over a cached ground,
  per-biome fade) cost as much as it saved: terrain 4.0 ms against 3.9 live.
- **Differences** (**Observed**, viewer A/B stills, 1920x1080 DLSS, before near footprints shaded the layers): mean 0.8-1.1 / 255, 0.35-1.4% of pixels over 12; the ripples' parallax phase
  and self-shadow shift slightly; a slope's parallax (n.y over 0.5) follows the height field instead of the cliff's projections. After a 90 m flight at 12 units a
  frame: mean 1.27 against the live picture, no stale strips. The game's `--screenshot` does not run the cache (its frames skip the hook).
- **Cost** (**Observed** before near footprints shaded the layers, `--play-bench` on save "stutter final boss", RTX 4070, 1080p DLSS 0.83, interleaved with master): terrain 3.94 -> 2.33 ms
  idle, orbit 3.98 -> 2.23, pan 3.77 -> 1.77; GPU frame 7.85 -> 6.20; frame p99 unchanged. Viewer A/B at two views: terrain -0.39 and -0.88 ms.

## Ground trails

Meitou look, not in the game; the `trails` switch (Tab "Ground trails"), off by default, not yet seen on a GPU. Look and cost **Unknown**. Render side only: no gameplay
or save change, and marks are lost when the window moves past them. Vulkan host only (`GameHost.Metal` never makes the renderer).

Cost and the marks' grid: the colour programs read the two images at every step of the ground's march (eight taps a step). **Observed** (desert outpost, 2560x1440, paired switch): 1.5 ms of the terrain's 3.8 ms; with the reads stubbed in the shader the terrain is 2.3 ms, and skipping single reads behind a one-tap test gains next to nothing: what pays is a pixel whose march reads no mark at all. So `TerrainTrailRenderer` keeps a grid over the coarse window (`CellsSide` 256 cells of 2 units, R8, `uTrailCells`) of the cells a stamp wrote to and has not worn away since (`MarkLife`), from the stamps' boxes on the CPU, with coarser levels holding the largest of their four cells; the programs ask it once a pixel, at the level whose cells are twice the march's reach wide (`trailWithin`: the view's march plus the sun's, 3 units more), and leave the images alone when it answers no. A stamp's box is what `StampFragment` writes (a body's rim out to `BodyWritten` texels of its stadium, not its whole quad) plus a texel and the bicubic read's three. **Observed** (terrain stage, before and after): views with few marks in sight -0.3 to -0.9 ms (coast 4.11 -> 3.27, `blocky` 4.14 -> 3.20, `light-shafts` 2.79 -> 1.87, `missing-grass` 3.20 -> 2.46); views on a squad walking about are unchanged (marks within reach of nearly every pixel). **Verified** (one paused frame, the grid against every cell marked, three views, alternated): the pictures differ no more than two captures of the same mode.

- **Image** (`TerrainTrailRenderer`): two levels, each 2048² and addressed toroidally as the material cache's levels, the origin snapped to 64 texels. **Coarse**: R16F (8 MB), texel 0.25 units, a height: 0
  untouched, -1 pressed in full (1.2 units, 12 cm), above 0 a rim; it reaches 240 units from the camera's target (the strategy camera's pivot; not the eye, so zooming keeps the marks) and shows fading over the
  window's outer tenth. **Fine**: RG16F (16 MB), texel 0.0625 units (a sole is about 40 texels), 60 units from the target; its y says a mark was recorded there, and the programs blend it over the coarse by
  that (fading at its window's edge), read through a cubic B-spline (four bilinear reads) so no texel shows in the normals. Every mark is stamped into both, with the same shape and noise (sizes scale
  by the texel); the columns and rows a window gains are wiped (the fine image then falls back on the coarse where it recorded nothing); all of it after a jump. A frame with nothing to change records nothing.
- **Marks** (`GameHost.Trails`, within that reach, not carried, in bed or caged): every character with a pose leaves marks from its **posed body as capsules**
  (`CharacterRenderer.ContactCapsules`: one per skinned bone, round the vertices it moves most, from the mesh's bone boxes; boxes under 0.5 units, fingers and toes, are left out).
  A capsule whose underside is within 0.5 units (5 cm) of the terrain under its ends and under 2.5 cm (sink over 0.5 of 1) is cut at that plane. Its speed over the ground, per second
  (frame rate does not matter), against the body's own: over 1.2 times (+3 units/s) it **swings**: nothing; over 0.4 times (+1 unit/s) it **slides**; else it is **planted**.
  A planted part **prints once**, and never again until it has swung, slid or left the ground (a character turning on the spot does not rewrite its prints; a lying body stamps once).
  **Upright** characters (standing, walking, all animals) only press: among their contacts one with a lower contact of the same body close beside it (a shin over a foot) is left out, and a print within 0.6 of its length of one of the body's last 8 is the same footfall. Capsules are grouped by **limb** from the skeleton (a bone's furthest ancestor within 0.17 of the body's size: a foot takes in its toes, not the knee): a limb prints **once per footfall**, 0.12 game seconds after landing (a foot lands on its heel and rolls flat), at once if a capsule lies whole on the ground, or as it leaves if it never settled. A planted limb prints an **imprint**: its own posed mesh as worn (`CharacterRenderer.RenderImprints`, bare foot, boot, stick leg, paw or hoof alike, no shape is drawn by hand), seen from under the ground in an orthographic 128-pixel tile (8 x 8 per frame) over a band from 0.4 units under the ground to 0.9 above it (the sole, not the foot's sides or the ankle); the depth presses the ground (0.6 of a full mark, about 7 cm), the lowest parts deepest, with a rim where the shape is near, the outline averaged over a disc (0.4 units in the fine image, 0.9 in the coarse). **Observed** (siwa-test6): read bilinearly, the coarse image drew stairs along the outline of prints made outside the fine image; parallax and displacement made no difference; the coarse image is now read through the same cubic B-spline as the fine one. Lying bodies
  (crawling, down, dead, ragdoll) press a stadium per part (rims first, then cores, so overlapping parts leave one bowl) and **plough** (a swept stadium from last frame's contact,
  heaped front and sides, feathered rear) where a part slides. A character with no pose (not drawn) leaves no print (only a crawler's furrow). No stamp above 2.5 units over
  the terrain (floors, roofs) or under the water line. **Observed** only on offscreen frames; the thresholds are tuned by eye. CPU **Observed**: 0.11 ms per frame for 37 characters
  (about 3 us each); the GPU draws a few quads.
- **Shape**: body stadia draw as two passes after the other marks (all rims, then all cores), so parts of one body that overlap leave one bowl, not ridges. Edge, depth and rim
  are modulated by value noise fixed to the image's texels (4, 16 and 64 texel cells), so every stamp on the same ground gets the same irregularity: no flicker, no seams between sweeps.
- **Ground** (`GroundSoftness`): a mark's strength is times the softness (0 to 1) of the biome layer under it (`GroundSurfaces.LayerAt`: the layer the footsteps' lookup picks, its GroundType and diffuse texture). The game has no such value (**Verified**, `meitou-tools biomes` over the load order: GroundType is Dirt for 40 of 75 BIOMES records, rock and Obedience's hardened ground among them, `absorbance 0` is 0.55 in all; gloss and normal-map relief measured on the 87 layer textures do not part slabs from sand), so a table by texture name, `assets/ground-softness.cfg` (embedded; values set by eye from the textures): `texture = softness`, with an optional second value after a comma, the texture's share of its relief depth (above, "Depth"), or `texture:Type = softness` for the layers of one GroundType (`Flat_Land_DIF` is tar mud in Flintcanyon Tarpits and hard ground, typed Dirt, in Robot Graveyard). Read after it, a later line replacing an earlier one: the same file in each loaded mod's folder in load order, then beside the executable. **Verified** (a line in a loaded workshop mod's folder, then one beside the executable, read back with a debug command). A texture in no table takes its GroundType's: sand, mud and snow 1, dirt 0.45, the rest 0. A mark under a low building floor is stamped but hidden.
- **Shading** (colour programs built with `TERRAIN_TRAILS`; `TerrainRenderer.MakeTrailed`, the switch off: none of it is in the program): the slopes over the pixel's footprint
  tilt the shading normal, and pressed ground is up to 15% darker. Patches only, fading from n.y 0.7 to 0.55.
- **Relief**: with parallax the height joins the ground's march and the sun's (`reliefHeight`, `bakedHeight`), read whole where the ground is displaced, so troughs self-shadow; no
  height layer (`g.ground.n` 0): normal only. With displacement and no parallax the mesh carries it (`uTrailMesh`), averaged over a segment, only where the relief already split the
  mesh. The depth prepass runs the same evaluation stage. Under the cache, one march step is `BakeSpan` / 16, up to several times a mark's depth: resolved by the secant step, **Unknown** how well.
- **Wear** (`TerrainTrailRenderer.Wear`): -ln(left) per game second is 1/900 + wind / 9000 + rain amount / 40, in steps of at least 1% (one quad over the image), and stops while paused.
  The whole window wears alike; ground type does not change it. Tuned on paper.

## Rocks

Meitou look, not in the game (`RockSmoothing`; Tab: "Rock smoothing (tessellation)", off by default, on at the Ultra preset; the welded normals are always made). Many TERRAIN-mode rocks have hard
edges ([foliage](formats/foliage.md), "Expression reference"). At decode (`FoliageRenderer.DecodeTrimmed`, not the leaves) a
position whose copies' normals disagree gets, per corner, the area-weighted normals of the faces round it turning less than 60°
from the corner's own; positions that agree keep theirs. Every position also gets one geometry normal (all its faces, or the
agreed one) in the tangent slot, w = 2 (the terrain material reads no tangents; a real tangent's w is ±1, so other TERRAIN-mode
meshes stay flat). The mesh programs then have tessellated twins (colour, solid and the casters, `meshDepthTess`, so the
shadow map curves alike): Phong tessellation (α 0.75, `RockSmoothing.Roundness`) to the geometry normals, each edge split by
`sqrt(bend / pixel)` (bend: α/4 of the ends' normals' components along it; pixel: 0.0015 × its midpoint's distance over the
tessellation detail), at most 16, `fractional_odd_spacing`; both sides of an edge curve it from the same two ends, so it does not
crack. Levels by the camera eye in the shadow pass too. The impostor bake and the GI's ray meshes stay flat.

## Interior cut

Patches over a building's interior mask are cut: [render-interior-cut](render-interior-cut.md).

## Streaming and budgets

Worker jobs decode/sample; render thread uploads/publishes through BackgroundWork/UploadQueue.
Keep valid old windows until replacements are ready. Prioritize useful nearby work, limit each
frame's upload/allocation steps and retire resources only after in-flight references finish.
WorldTextureCache and mesh caches share memory pressure/range policy; avoid unloading resources
immediately needed by adjacent zones or a new mip level. Cold load/settle and steady-state frames
are separate performance measures. Height-window changes invalidate dependent bakes/probe bases.

## Validation

Use matched rendered resolution/error, not display resolution alone. Inspect seams/morph bands,
biome edges, fine/coarse window movement, horizon and teleport. **Observed** RTX4070 swamp/Hub
probes: near views can be material/shadow-bound; distant views can be geometry-bound. Changing
anisotropy, error or LOD need not change the limiting cost. [bench.md](bench.md).
Faithful material/placement claims remain in the format reference; keep output/noise evidence
with any deliberate Meitou quality change.
