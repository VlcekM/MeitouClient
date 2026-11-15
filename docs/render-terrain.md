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
  (render-objects.md), least recently needed first: the range is the pair's tile × pixels per radian ÷ layer size × 11.3, with an upscaler's negative bias widening it.
  When no slot is left, a pair stays far.
- Params texels 0 and 1 hold the far indices (the relief code reads these too), texel 1's W the biome's relief factor; texels 13 and 14 hold the near slots, -1 for none.

**World colour map** (`TerrainTextures.PrepareWorld`): the 8 × 8 colour tiles box-filtered into one 2048² RGBA map for the far terrain
and foliage. Cached raw (16 MB, no header) in `%LOCALAPPDATA%\Meitou\terrain\worldcolour_<hash>.bin`: hash of a version tag, the size and
each tile's path, length and write time, so a mod's tile gets its own file; a file of the wrong length is rebuilt. Decoding the tiles
took over a second a start. Old files are never deleted; bump the tag when the filtering changes. `--no-load-cache` skips it.

**Standby windows** ("Keep textures at characters", render-objects.md): a kept point outside the middle of the
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

**Refinement** (needs tessellation): every main-view patch takes the tessellated programs; an edge is also split where the terrain
leaves it (at its quarters and middle) by more than 0.5 px, level `sqrt(deviation px / 0.5)` (a split in n takes a smooth curve's
deviation down by about n²), at most 16, and the new points are put on the terrain (`terrainHeight`); an unsplit edge keeps its
ends, so neighbours meet. Displacement and refinement take the larger level per edge. Shadow casters stay unrefined. On Metal
the tessellation toggles stay hidden and the flat/parallax path is used; a 2026-10-24 prototype was removed for cost and
fallback risk (`docs/metal-backend.md`, "Tessellation").

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
- Not in the reflection, the viewer's other views or with the wireframe: those draw the patches' colour as before (`ResolveInputs` null).

## Material cache

Meitou, not in the game (`MaterialCache`, A/B `terrain-material-cache`, B: every pixel shades the layers). The patches' layered
material is baked into world-space levels round the eye, and a patch pixel within them reads the cache instead of shading up to five biomes of up
to 14 layer samples each.
- **Bake** (`TerrainShaders.MaterialBakeFragmentNative`): the colour shader run per texel at its world point (terrain height and normal, the texel as
  the sampling footprint, distance 0, no march), both targets in one pass, written before the blood, wetness and lighting. Its relief height is one
  height field over `BakeSpan` (parallax depth × max depth × cliff scale): the ground's (the march's gather, depth × height) mixed by the cliff's
  share with the cliff's (its projections' heights along the normal, ÷ n.y, held at n.y 0.3).
- **Refresh**: the inputs (bindless indices, overlay window, `TerrainTextures.MaterialVersion` = writes to the biomes' parameter rows, relief settings)
  are hashed each frame; a change re-bakes every level 64 rows a frame, finest first, drawn from meanwhile.

## Ground trails

- **Image** (`TerrainTrailRenderer`): two levels, each 2048² and addressed toroidally as the material cache's levels, the origin snapped to 64 texels. **Coarse**: R16F (8 MB), texel 0.25 units, a height: 0
  untouched, -1 pressed in full (1.2 units, 12 cm), above 0 a rim; it reaches 240 units from the camera's target (the strategy camera's pivot; not the eye, so zooming keeps the marks) and shows fading over the
  window's outer tenth. **Fine**: RG16F (16 MB), texel 0.0625 units (a sole is about 40 texels), 60 units from the target; its y says a mark was recorded there, and the programs blend it over the coarse by
  that (fading at its window's edge), read through a cubic B-spline (four bilinear reads) so no texel shows in the normals. Every mark is stamped into both, with the same shape and noise (sizes scale
  by the texel); the columns and rows a window gains are wiped (the fine image then falls back on the coarse where it recorded nothing); all of it after a jump. A frame with nothing to change records nothing.
- **Shape**: body stadia draw as two passes after the other marks (all rims, then all cores), so parts of one body that overlap leave one bowl, not ridges. Edge, depth and rim
  are modulated by value noise fixed to the image's texels (4, 16 and 64 texel cells), so every stamp on the same ground gets the same irregularity: no flicker, no seams between sweeps.
- **Ground** (`GroundSoftness`): a mark's strength is times the softness (0 to 1) of the biome layer under it (`GroundSurfaces.LayerAt`: the layer the footsteps' lookup picks, its GroundType and diffuse texture). The game has no such value (**Verified**, `meitou-tools biomes` over the load order: GroundType is Dirt for 40 of 75 BIOMES records, rock and Obedience's hardened ground among them, `absorbance 0` is 0.55 in all; gloss and normal-map relief measured on the 87 layer textures do not part slabs from sand), so a table by texture name, `assets/ground-softness.cfg` (embedded; values set by eye from the textures): `texture = softness`, with an optional second value after a comma, the texture's share of its relief depth (above, "Depth"), or `texture:Type = softness` for the layers of one GroundType (`Flat_Land_DIF` is tar mud in Flintcanyon Tarpits and hard ground, typed Dirt, in Robot Graveyard). Read after it, a later line replacing an earlier one: the same file in each loaded mod's folder in load order, then beside the executable. **Verified** (a line in a loaded workshop mod's folder, then one beside the executable, read back with a debug command). A texture in no table takes its GroundType's: sand, mud and snow 1, dirt 0.45, the rest 0. A mark under a low building floor is stamped but hidden.
- **Shading** (colour programs built with `TERRAIN_TRAILS`; `TerrainRenderer.MakeTrailed`, the switch off: none of it is in the program): the slopes over the pixel's footprint
  tilt the shading normal, and pressed ground is up to 15% darker. Patches only, fading from n.y 0.7 to 0.55.
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
