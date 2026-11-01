# Terrain rendering

`TerrainQuadtree`, `TerrainLod`, `TerrainRenderer`, `TerrainTextures`, `TerrainStreamer`.
Original height/blend/map/material facts: [formats/terrain.md](formats/terrain.md).
Engine LOD/streaming rules below; GPU lifetime: [renderer-native.md](renderer-native.md).

## Geometry and material

CDLOD chooses patches by error in rendered pixels, morphing between levels without cracks.
Whole-world coarse heights keep the horizon; a fine moving window follows the eye. Use fine
height directly at weight1, blend at edges. Height/normal sampling must agree between terrain,
objects, water/shore bakes and collision queries. Near/far slices retain consistent depth.
TERRAIN-mode rock geometry is foliage work, not terrain-patch stage timing.

Biome materials resolve blendinfo slots, mask/overlay/colour maps and layer colour/normal inputs.
Preserve source tile scales, layer order and weights; the engine's texture window does not alter
world coordinates. `terrain-layer-skip` can omit insignificant layer work; thresholds are quality
choices requiring boundary/material checks. `terrain-late-colour` separates depth from shading
where occluders justify it. A depth prepass is extra geometry, not automatically a speedup.

**World colour map** (`TerrainTextures.PrepareWorld`): the 8 × 8 colour tiles box-filtered into one 2048² RGBA map for the far terrain
and foliage. Cached raw (16 MB, no header) in `%LOCALAPPDATA%\Meitou\terrain\worldcolour_<hash>.bin`: hash of a version tag, the size and
each tile's path, length and write time, so a mod's tile gets its own file; a file of the wrong length is rebuilt. Decoding the tiles
took over a second a start. Old files are never deleted; bump the tag when the filtering changes. `--no-load-cache` skips it.

**Colour window** (`MapWindows`, `TerrainColourCache`): the colour map near the eye, a 4096² texture addressed toroidally (the overlay
window beside it is 2048² RGBA8). It is BC3 (21 MB instead of 85 MB RGBA8), not BC1: the colour tiles' alpha is gloss (**Observed**: all 256
values, 30 % at 255). The 64 tiles are encoded once with 7 levels into `%LOCALAPPDATA%\Meitou\terrain\colour\<hash>.bc3` (magic `MTC3`, side,
the levels' blocks; ~5.6 MB a tile, 342 MB in all), hash of a version tag and the tile's path, length and write time. A start with tiles
missing from it encodes them on workers in the loading step "terrain colour map (first start only)" (**Observed** 5.8 s on a 2026 desktop);
the window then copies blocks instead of decoding PNGs (**Observed** at The Hub: first window 0.6 s instead of 1.6-3 s, a move 0.15 s instead of
0.85 s). Every piece must start on a block at every level, so the window origin is aligned to 128 overlay pixels (256 colour pixels). Tiles
that are not square multiples of 256 keep the RGBA8 path.

## Ground relief

Meitou look, not in the game (`GroundRelief`). The parallax and the displacement are Faithful/Meitou switches `parallax` and
`displace` (Tab "Ground parallax", "Ground displacement (tessellation)"; Faithful: flat ground, as the game). Tab toggles "Terrain outline
refinement (tessellation)", "Rock smoothing (tessellation)"; sliders "Ground relief quality" (High by default; Medium and Low reduce
the march; compiled into the colour programs, one set per quality made on first use: a uniform quality cost High 12% of the terrain's
GPU time, **Verified** by swamp-low A/B), "Ground relief depth (cm, typical ground)" (default 30), "Ground displacement distance (m)" (100), "Ground parallax
distance (m)" (60); all on the settings' Graphics > World page, where quality and depth hide while parallax and displacement are both
off and each distance while its effect is off; viewer K toggles the parallax. The other values are tuned constants, expressed at a 20 cm reference depth and
settable for a run with the viewer's `--relief Name=value`: target slope 0.2, scale follow 0.5, max depth 80 cm (120 at the default
30), cliff scale 1, overlay relief 1, self-shadow 0.6, tessellation detail 0.5, refinement 0.5 px, tilt 0.5, broad cut 2 m.

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

**Composite.** Every ground layer adds material; the relief height keeps the heaviest ground layers (base, grass, slope, dirt,
road) by how much of the pixel each shows, from the blend map, overlay window and slope per pixel, not per patch, so neighbouring
patches meet on the same heights: five at High quality, two at Medium, one at Low. The cliff over all has its own march. A biome
under 10% of the pixel is not marched (fading in to 30%), a layer under 5% is left out (to 15%).

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
to 2 and 1), in full to half of it; the sun's march reads the height two levels coarser and is skipped at 0 self-shadow. Where the
ground is displaced, the view ray marches only the detail finer than the mesh (the height less its level at the segment, 8 steps at High, 4 below),
the whole height again as the displacement fades. The
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
`TerrainRenderer.UseColour`; a toggle builds the other set once): the terrain stage 3.37 -> 2.42 ms and the frame 7.0 -> 6.1 ms at a save's view,
the picture unchanged. The interior-cut patches' late colour took 1.4 ms for 19 draws with 8 wait-for-idles (the SMs idle a third of it): its `discard` turned the early
depth test off. Their prepass already discards the cut, so the late colour draws them with the plain program and an equal depth test (both passes
run the same vertex and tessellation stages, invariant positions): GPU frame 6.14 -> 5.80 ms, the picture unchanged (one pixel over 8 levels at a town view).
With the parallax on (60 m, displacement 60 m), nearly all the visible terrain is relief patches. The march held their pixel warps to 12% (21% without it) at the
same register file use, with the SM throughput at 20% (39%). On the RTX path the march's sampling hardly counts: no self-shadow, or the base layer alone,
changed nothing. Every biome's march now runs before any is shaded (`biomeRelief`, then `biome` with its result), so the march's state no longer sits on
the shading's: terrain 4.4 -> 4.0 ms, the picture unchanged. Without the march (`NO_MARCH`) that loop and its results' array are left out; kept with
constants, they cost 0.28 ms. Drawing patches beyond the parallax's end with the march-free program saved nothing there.
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

## Rocks

Meitou look, not in the game (`RockSmoothing`; Tab: "Rock smoothing (tessellation)"). Many TERRAIN-mode rocks have hard
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
