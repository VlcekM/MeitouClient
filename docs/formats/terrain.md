# Terrain: world size, heightmap, land maps

Examined 2026-10-04 on the Steam install (Kenshi 1.0.68, "Newland" world). Readers:
`Meitou.Data.World.WorldLayout`, `TiffImage`, `TerrainHeightmap`, `RawHeightTile`; texturing: `BlendInfoFile`,
`BiomeTerrain`, `TerrainMaps`; meshes: `HeightWindow`, `TerrainQuadtree`; water and sky: `WorldWater`, `BiomeWater`, `SkyClock` (drawn by `meitou-viewer --world`, [../viewer.md](../viewer.md#world-mode)). Checks:
`meitou-tools world` (survey) and `tests/Meitou.Tests/World/`. Zone files and placements are in
[zones.md](zones.md).

## World coordinates

X and Z are the ground plane, Y is up (Ogre convention).

| Fact | Value | Status |
| --- | --- | --- |
| World extent | X and Z in [-147456, +147456], 294912 units per side, centred on the origin | **Verified** (below) |
| Zone grid | 64 × 64 zones of 4608 units; zone index = `floor(coord / 4608) + 32` on each axis | **Verified**: all 11,714 placed buildings lie in the zone of the file that places them (`LevelFileTests.Base_game_world_placements_are_consistent`), and all 224 towns whose state lists zones list the zone their position falls in |
| Heightmap | 16385 × 16385 samples, 18 units apart (256 per zone, plus the far edge) | **Verified**: file size and tags, and heights below |
| Height scale | `height = raw × 9800 / 65535`; raw 0 = height 0 | **Verified** (below) |
| Unit size | If 1 unit = 1 dm, the world is 29.5 km across (870 km², the size usually quoted by players); 1 cm would make it 2.9 km | **Unknown**; decimetres fit best |

How the grid and scale were found: the zone files place buildings at absolute X/Z (see
[zones.md](zones.md)). Fitting `zone = floor(x / S) + 32` over 14,614 positions gave 100% agreement at
S = 4608 and at most 73% for any other tried size (4000…8000). The edge value 147456 = 32 × 4608 also
appears literally as a float (`0xC8100000`) in `globalPathing.path`.

## Heightmap: `data/newland/land/fullmap.tif`

A baseline TIFF (`TiffImage` reads it from the tags, nothing hard-coded). **Verified** from the tags:

- Little-endian (`II`), one image directory, 16385 × 16385, 16 bits per sample, 1 sample per pixel,
  photometric 1 (BlackIsZero), no compression, one strip (RowsPerStrip = 16385) of 536,936,450 bytes
  starting at byte 43686.
- Written by "Adobe Photoshop CS6 (Windows)" on 2017:09:25; also carries XMP (tag 700), IPTC (33723),
  Photoshop (34377), Exif (34665) and ICC (34675) blocks, which the game presumably ignores.
- 16385 = 2^14 + 1: the Ogre Terrain style "power of two plus one", so the last row and column lie on the
  far world edge (sample `i` is at world `i × 18 − 147456`).

Orientation: column = world +X, row = world +Z, sample (0, 0) at world (−147456, −147456).
**Verified**: with that mapping, road points lie on the terrain (median |Y − terrain| 0.3); every flipped or
transposed mapping gives a median residual above 350 and no correlation (r < 0.1).

Height scale **Verified** against three independent sources, using bilinear interpolation
(`TerrainHeightmap.HeightAt`):

- Buildings: their state's `world Y pos` equals terrain height + the placement's Y offset. With
  `9800/65535`, 202 of 14,588 building placements (all layer files, before merging) match within 0.005 units; with `9800/65536` 93; with
  9799 or 9801 instead of 9800, 47 and 23. Scanning the scale in steps of 2 shows a single sharp peak at
  9800 (1,061 matches within 0.1, next best 571). `LevelFileTests.Base_game_heights_match_the_heightmap`
  checks that 9800 beats 9790 and 9810 by a factor of two.
- Road points (`leveldata.level`, 20,944): median Y − terrain −0.29, quartiles −1.1 / +0.5.
- Towns (304): median +2.0 (towns are placed by hand, so looser).

Interpolation: bilinear is what we use; the many exact matches suggest the game samples the same way, but
its exact scheme (bilinear vs. triangles) is **Unknown**.

Value statistics (**Verified**, `meitou-tools world`): raw min 0, max 65469 (height 9790), 46,646
distinct values; 25.9% of samples are exactly 0 (sea / outside the land); median raw 2160 (height 323),
p99 22856 (3418), p99.9 36973. The world border (corners and edge midpoints) is 0. The water level is not 0
but 100 (**Verified**, see [Water](#water)); 38.1% of the world lies below it (26% at raw 0, 12% in between).

Streaming: the reader never loads the whole 537 MB; `Downsample(step)` streams rows,
`HeightAt` / `Sample` seek per sample.

## Other maps in `data/newland/land/`

The texturing ones are explained in [How the terrain is textured](#how-the-terrain-is-textured).

| File | Size | What it is |
| --- | --- | --- |
| `biomemap.png` | 1024², RGBA | Biome regions as flat colours, one BIOMES `index` colour per pixel (288 units). Same orientation as the heightmap (**Verified**, below) |
| `blendmap.png` | 1024², RGBA | Per-pixel weights of the biomes listed in `blendinfo.dat` (**Verified**, below) |
| `areasmap.tga` | 256², 24-bit uncompressed (18-byte header + 196,608) | Not analyzed; if it covers the world, 4 × 4 pixels per zone. Same size as `data/land/areasmap.tga` |
| `blendinfo.dat` | 1,364,352 | Magic `KBI1`: the biomes of each 2 × 2-zone cell, then a quadtree of slot masks per cell (**Verified**, below) |
| `fogfeatures.dat`, `features.dat` | | See [zones.md](zones.md#other-placement-files) |
| `overlaymaps/colour.X.Y.png` | 64 files, 2048² each, X, Y = 0..7 | 8 × 8 tiles = 16384² ground tint, 18 units per pixel (below) |
| `overlaymaps/new_overlay.X.Y.png` | 64 files, 1024² each | 8 × 8 tiles = 8192² layer map (grass, dirt, road), 36 units per pixel (below) |
| `overlaymaps/` others | | `biomemap.png`, `ambientmap.png`, `flowmap.png`, `watercolourmap.png`, `distant.png`, `debug.png`, `prosp.tga`, and tool leftovers `DevIL.dll`, `joiner.exe` |
| `textures/` | 364 `.dds` | Terrain layer textures (`*_DIF`, `*_NML`), named by BIOMES records |
| `navtiles/` | 3,995 `tileX.Y.hkt` + `seeds.def` | Havok navmesh tiles (see overview) |

Exe strings (Observed, simple string search of `kenshi_x64.exe`): `fullmap`, `data\newland/land\`,
`Failed to open terrain map`, `ZoneMap::createTextureArray`, `Failed to load blend map`,
`Failed to load index map`, `BiomeMap`, shader parameters `worldSize`, `worldOffset`, `waterHeightRel`,
`blendinfo.dat`, `features.dat`, `fogfeatures.dat`.

## How the terrain is textured

Sources: the land maps above, the BIOMES records, Kenshi's terrain shaders `data/materials/deferred/terrain.hlsl`
and `terrainfp4.hlsl` with `terrain.material` (shipped HLSL, read for facts), and the material setup in
`kenshi_x64.exe` (Ghidra: the biome field reader at `0x140a0d9c0`, the terrain material builder at
`0x140a14c90`; decompiled output kept outside the repo). Implemented from this description in
`tools/Meitou.ModelViewer/TerrainShaders.cs`.

### Which biomes apply where (Verified)

- **`biomemap.png`**: each of its 57 colours is the `index` colour of exactly one BIOMES record (all 57 match).
- **`blendinfo.dat`**: `char[4] "KBI1"`, `int32 cells` (32), `int32 resolution` (32, see the quadtrees below;
  earlier read as cellsZ), then `cells × cells × 5`
  `uint32` slot values, row-major with the row along +Z (cell `(x, z)` at index `z × 32 + x`). A cell is
  294912 / 32 = 9216 units, 2 × 2 zones, 32 × 32 pixels of the 1024² maps. A slot value holds a biome colour in
  its low 24 bits (0 = unused); cells use 1 to 4 of the 5 slots (355, 386, 227, 56 cells).
- **Slot-mask quadtrees** after the table (byte 20,492; **Verified** by `TerrainBiomeTests` and the reader at exe
  `0x140a09d20`): `int32 nodeCount` (2048 = 2 × resolution²), then per cell (table order) a root byte. A node
  byte holds a mask of the cell's slots (bit i = slot i) in bits 0–4 and the number of set bits in bits 5–7
  (`0x21` = slot 0 only, `0x43` = slots 0 and 1). If the root's count is 1 the cell stores only that byte
  (368 cells); otherwise `nodeCount − 1` more bytes follow (656 cells). The file ends exactly after the last
  cell. The bytes form a complete quadtree in heap order: node 1 is the root (byte 0), node i's children are
  4i .. 4i+3, and the resolution² = 1024 leaves (nodes 1024 .. 2047) are the cell's 32 × 32 blend-map pixels at
  `1024 + morton(x, z)`, with x in the even bits and z in the odd bits. Every parent is the OR of its children.
  Every leaf contains the slots with weight at its own pixel, and only slots present in its 3 × 3 neighbourhood
  (it is dilated by one pixel, for bilinear filtering). The game uses it to find the biomes a box inside one
  cell needs (a terrain page, a zone's water). It maps the box corners to leaves, walks both up until they
  meet, and takes that node's mask (`BlendInfoFile.SlotMask`). 13 single-byte cells list 2 table slots, but
  their mask and the blend map use only one.
- **`blendmap.png`** (same 1024² grid): R, G, B, A are the weights (0–255) of slots 0–3 of the pixel's cell and
  the remainder `255 − (R + G + B + A)` is the weight of slot 4. The largest weight names the biome
  `biomemap.png` shows at that pixel on all 1,048,576 pixels; the channel sums are 240–255, or 0 where slot 4
  alone applies (67,292 pixels). Tested in `TerrainBiomeTests.Base_game_blend_map_channels_weight_the_cell_slots`.
  Weights are mostly 0 or 255: biome borders are hard, 288 units wide (Observed, debug view).

### Overlay and colour maps

- **`overlaymaps/new_overlay.X.Y.png`**: 8 × 8 tiles of 1024², tile X along world +X, Y along +Z, 36 units per
  pixel. **Verified**: alpha is the road layer (mean 70.6 at road points of `leveldata.level`, 5.9 at points 300
  units off, `TerrainBiomeTests.Base_game_overlay_alpha_marks_roads`; with the tile indices swapped the contrast is 15.2 vs 3.4,
  with Z flipped none). From the shader (Observed): `max(R, G)` is the grass layer weight, B the dirt layer weight.
- **`overlaymaps/colour.X.Y.png`**: 8 × 8 tiles of 2048², 18 units per pixel, same tiling (Observed: names and
  sizes; the result looks right in the viewer). The shader multiplies RGBA by 1.2 and tints the layers with it;
  its alpha thereby scales the gloss.
- The shader maps both by a bounding box per terrain page (`overlayData`, `biomeData` uniforms: min corner and
  min + size from the page's map object; Observed in the material builder).

### BIOMES fields and shader parameters (Verified, kenshi_x64.exe)

Each biome has six layers, in this order in its texture arrays (`diffuseMaps`, `normalMaps`, resource group
`Landscape`): 0 base, 1 slope, 2 cliff ("vertical"), 3 grass, 4 dirt, 5 road. Texture fields `texture base`,
`texture slope`, `texture vertical`, `texture grass`, `texture dirt`, `texture road`, each with `... normal`.
How the material builder turns fields into shader constants:

| Shader constant | From | Notes |
| --- | --- | --- |
| `scalesA` | `tiling X/Y 1`, `tiling X/Y 2` | slope, cliff |
| `scalesB` | `tiling X/Y 0`, `tiling X/Y grass` | base, grass |
| `scalesC` | `tiling X/Y dirt`, `tiling X/Y road` | dirt, road |
| `slopeMin`, `slopeMax`, `slopeBlend` | `slope min/max/fade` `1`, `2`, `3`, `grass`, each **× 0.01** | compared with `1 − normal.y`, so `19` means 0.19 (about 36°), not 19° as fcs.def says; `... 3` is not in fcs.def |
| `overlayMult` | `overlay mult vertical`, `grass`, `dirt`, `road` | |
| `textureFade` | `ground colour` (RGB), `1 / fade distance` | |
| `absorbance[2]` | `absorbance 0`, `1`, `2`, `grass`, then `dirt`, `road` | rain wetness |
| `brightnessFix` | `brightness fix` per blended biome | 0 becomes 1 |
| `distortion0`, `distortion1` | per biome `1 / distort wavelength` (0 if ≤ 0), `distort amplitude × 0.01` | two biomes per vector |

### Layer model (Observed, terrainfp4.hlsl)

Per pixel, with `slope = 1 − normal.y` and world position `p`:

- Horizontal layers use `uv = p.xz / 5000 × tiling`; the cliff layer is projected on the two vertical planes,
  `(p.z, v)` and `(p.x, v)` with `v = 1 − p.y / 5000` plus a cosine distortion, weighted by how much the surface
  faces X or Z (`(|n.xz| − 0.2) × 7` squared, normalised).
- Layer weights: `smoothstep(min − blend, min, slope) × smoothstep(max + blend, max, slope)`; component X weights
  the slope layer, Y the cliff layer (Z and W are not used for colour).
- Blend order: base → grass by the overlay's `max(R, G)` → slope by its weight → dirt by overlay B → fade to the
  ground colour with distance (`saturate(distance / fade distance − 0.3)`) → road by overlay A → cliff by its
  weight. Base and slope are multiplied by the colour map; cliff, grass, dirt and road by
  `lerp(1, colour, overlay mult)`. Normal maps blend the same way (fading to flat); the result's alpha is gloss.
- Normal maps are in a frame with `binormal = normalize(n × (−1, 0, 0))`, `tangent = binormal × n`; the cliff
  samples flip channels per projection (not reproduced in the viewer).
- Up to four biomes per terrain page (defines `BLEND1..3` name blend-map channels); the first gets
  `1 − (sum of the others)`. Which slot the game treats as the first is **Unknown**; the viewer weights all five
  slots as above, which is equivalent when the weights sum to 1.
- Not reproduced in the viewer: wetness after rain (absorbance; only a fixed darkening below the water), the
  interior clip mask, and the game's own distant material (the viewer's far terrain is under
  [Terrain LOD](#terrain-lod)).

### Why some sea areas in the north-east have other tones (Observed)

In a whole-world view, the sea in the north-east and east shows large patches with straight edges in other
tones. They are not blend cells, colour-map tiles or a rendering fault. `biomemap.png` itself has straight-edged
biome regions painted over the sea there (Desert, Desert Maze, Kenshi Bowl, Blister), and each has its own
`ground colour` and textures. Their straight edges cross the 1024² map at rows 235, 249 and 318 (inside cells, not on the 32-pixel
cell lines) and at 352 to 356 (a slightly oblique edge crossing a cell line). The ground-colour render (viewer far path), the textured render and the
biome field baked from `blendinfo.dat` + `blendmap.png` all show the same shapes. With water drawn, they are
mostly hidden below it. Checked with a probe outside the repo, plus top-down screenshots at
`--material-distance 1` and `1e7`.

## Terrain LOD

### What the game does

- **Verified** (kenshi_x64.exe, the terrain set-up): the terrain plugin is configured with
  `setTerrainScale(294912, 9800)`, `setDetailThreshold(8, 8, 0)`, `setMaterialDistance(30000)` and
  `setOgreBuildLimits(250, 100)`. The patch size is 16, or 64 when a settings flag is set. The default material
  is `DistantTerrain`, the blood decal material is `TerrainBlood`, and the scene node is `TerrainNode`.
- **Verified**: `DistantTerrain` is textured by `overlaymaps/distant.png`, a 128² map of each biome's
  `ground colour` (the game generates it if the file is missing), mapped over the world by
  `(x0, z0, 1 / width, 1 / depth)`. Beyond the material distance (30000) the terrain is drawn with this
  colour only.
- **Observed** (`data/materials/deferred/glsl/cdlod.vert`, `terrain.hlsl`): the terrain is a CDLOD quadtree.
  Each patch is a regular grid; with distance, the vertex shader morphs the odd grid vertices onto the next
  coarser grid (height and normal towards a coarser level in `position.w` and `BINORMAL`), so levels meet
  without cracks or pops.

### In the viewer (`TerrainQuadtree`, `TerrainRenderer`, `TerrainShaders`)

CDLOD in our own implementation. Heights live in two R16 textures that the vertex shader samples: the whole map
every 8th sample (2049², 144 units), and a `HeightWindow` of the finest samples (every `--step`-th) around the
camera, re-read from the file when the camera leaves its middle (docs/viewer.md, Streaming). The fine one fades in over
a band at its border. A 64 × 64 grid patch is drawn per selected node. Node ranges are
`leaf size × 2^level × K` (K = 3, `[` / `]` change it). A node morphs over the last 30% of its range: its odd
vertices slide onto the coarser grid, so touching nodes differ by at most one level and their edges match
(`TerrainQuadtreeTests`). Bounds come from a min/max pyramid of the heights. Normals are computed per pixel from
the height textures. Beyond the material distance (30000, as in the game), the terrain is shaded with the biome
ground colour × the colour map (2048², box-filtered from the 64 tiles), fading over a band. The scene is drawn in
two depth slices (far: 20000 to 450000, then near), so a 24-bit depth buffer reaches the horizon.

## Water

Sources: `kenshi_x64.exe` (Ghidra, the water set-up and material builder; decompiled output kept outside the
repo), `data/materials/forward/water.material` / `water.hlsl` (shipped shaders, read for facts), and the base
game maps and records. Implemented in `Meitou.Data.World.WorldWater` / `BiomeWater` and
`tools/Meitou.ModelViewer/WaterRenderer.cs`.

### Placement (Verified, kenshi_x64.exe)

- **One level for the whole world: Y = 100.** The water's root scene node is created at height 100 (a constant
  in the exe). The same value becomes the shared shader parameter `waterHeight` and the reflection plane
  (normal +Y, at 100). The water set-up function has a single caller, the water initialisation, which runs once.
  Shaders get `waterHeightRel = waterHeight − origin.y` (relative rendering), or −1e8 to switch water effects off.
- **Distant plane**: one `waterPlane` mesh, 500000 × 500000, 100 segments per side, material `waterDistant`, in
  its own visibility group (flag 0x10). The distant shader discards pixels within 4000 units of the camera and
  outside the map bounds (±147456).
- **Near planes**: every zone, when it loads, gets a `zoneWater` plane of 4608 × 4608 (one zone), 2 segments,
  at the zone's position under the water node. This is unconditional: every zone, land or sea. Terrain above 100
  simply hides it.
- **Per-zone material**: a clone of `water`. If the zone's box needs several biomes (from the `blendinfo.dat`
  quadtrees), it gets one pass per biome, using `WaterFPBlend` with `blendChannel` = that biome's slot.
- Observed (base game): 38.1% of the heightmap lies below 100. 582 of 11,714 placed buildings stand on terrain
  below 100, among them pontoon bridges, swamp walkways and riceweed farms; most are in the swamps (Swamps 204,
  ForestLand 81). `WaterTests.Base_game_low_buildings_are_water_structures` checks it.

### Material parameters (Verified, the per-biome material builder)

| Shader parameter | From BIOMES fields |
| --- | --- |
| `scale` | (`water scale X`, `water scale Y`, `scum scale X`, `scum scale Y`) / 5000 |
| `invStrength` | 1 / max(`water strength`, 0.0001) |
| `invOpacity` | 1 / max(`water visibility` × 10, 1) |
| `gloss`, `glow` | `water gloss`, `water glow` |
| `distortion` | (`water distortion`, `scum distortion` / ((scum X + scum Y) / 2)) |
| textures | `water normal`, `texture scum`, `texture scum normal`, `turbulence map` |

The 5000 is a float constant (checked in the disassembly; the decompiler's output suggested −147456 instead).
`water color` is not a shader parameter: it is baked into `overlaymaps/watercolourmap.png`, which the game
generates at 128² from each cell's biome if the file is missing. Observed: the shipped file matches the biome
`water color` at pixel centres on 9433 of 16384 pixels exactly (median difference 0); the rest differ near
biome borders. `flowmap.png` gives the flow direction. Tested in `WaterTests`.

### Shading (Observed, water.hlsl)

Three samples of the normal map, scrolled along the flow with shifted phases, blend into the surface normal
(the up component divided by `invStrength`). Reflection uses a render target of the scene mirrored at the water
plane. Alpha: opaque beyond 4000 units (where the distant plane takes over, with a 400-unit blend); nearer, it
grows with the water depth under the pixel, scaled by `invOpacity` and a Fresnel term. The unit of the time
parameter (`gameTime`) is **Unknown**. The viewer reflects the sky colour instead of a render target, and does
not draw scum, turbulence, rain ripples or the per-biome normal maps. The viewer draws the sea past the world's
edge too (the game's distant plane is cut at the map bounds, see Placement): a plane centred on the camera, with the
water parameters fading to the open sea's (the most common among the maps' outer ring) over 30000 units past the edge.

## Sky and sun

### Sun path (Verified, kenshi_x64.exe sky controller and the CONSTANTS record)

The sky is SkyX, driven by the game's own controller. The `GLOBAL CONSTANTS` record gives `latitude` 54,
`sunrise` 5, `sunset` 23 and `days per year` 100. If sunset ≤ sunrise, the game uses 6 and 20. Each update maps
the hour to a phase: from sunrise to sunset it runs 0 → 1 (day); through the night it runs 1 → 2. With
`a = phase × π` and `φ` = latitude, the direction to the sun is `(cos a, cos φ · sin a, sin φ · sin a)`. So the
sun rises in +X, sets in −X, and peaks at only about 36° above the horizon (at noon, halfway between sunrise
and sunset). `SkyClock` implements this; `WaterTests` checks the record values and the formula.

### Atmosphere (Verified constants, Observed shader)

The SkyX atmosphere options the game sets: inner radius 9.77501, outer radius 10.2963, height position 0.01,
Rayleigh 0.0022, Mie 0.000675, sun intensity 30, wavelengths (0.57, 0.54, 0.44), phase function g −0.991,
exposure 0.48, 4 samples (`SkyAtmosphere`). The sun's light colour comes from SkyX's colour at the sun's
direction. Fog: `pFogParams = (D, D·k1, max(D·k2, D), 0)`, where D and the factors come from runtime settings
(values **Unknown**). `atmospherefog.hlsl` blends to an O'Neil scattering colour, linearly between
`pFogParams.y` and `.z` (Observed). The viewer does not reproduce the scattering. It uses a simple colour model
of its own (`SkyColours`), with the game's sun direction, a horizon colour that is also the fog colour, and fog
that grows with the camera's height.

Observed: `areasmap.tga` (256², 71 colours) is described in `fcs.def` as holding BIOME_GROUP colour indices;
not yet matched to records.

## Legacy terrain: `data/land/` (Observed)

An older world, kept in the install. Probably unused by the Newland game, but `resources.cfg` still lists
`data/land` in its `[Landscape]` and `[Overlaymaps]` groups (for shared textures at least).

- `grasssplits/fullmap.X.Y.raw`: 256 files, X = 10..25, Y = 16..31. Each is a bare 257 × 257 grid of
  little-endian uint16 (132,098 bytes, no header). **Verified** (`TerrainTests.Base_game_legacy_height_tiles_share_their_edges`):
  neighbouring tiles share their edge row/column exactly (480 edges, 0 differences) with X = column
  direction, so together they form one 4097 × 4097 heightmap. Rendered, it shows a different landscape
  from `fullmap.tif`: an old map. Raw range 0..51265. The name suggests the tiles were also used for grass
  placement; the exe contains no `grasssplits` string. Ogre Terrain's own `.raw` import is also headerless
  16-bit, which fits.
- `fullmap.dds` (699,192 bytes), `overlay.tif`, `nogoAreas.dds/.tga`, `areasmap.tga`, `overlaymaps/`
  (`biome_*.png`, `new_overlay.{1..3}.{2,3}.png`), `textures/`. Not analyzed.

## Open questions

- World unit in metres (decimetres most likely).
- How the game interpolates heights between samples, and the details of its terrain paging (page sizes, when
  pages load; the known constants are under [Terrain LOD](#terrain-lod)).
- Meaning of `areasmap.tga`, and which blend slot the game treats as a page's first biome.
- Wetness after rain, the scum layer, the unit of the water shader's time, and the fog distances (runtime
  settings).
- Whether anything still reads `data/land/grasssplits`.
