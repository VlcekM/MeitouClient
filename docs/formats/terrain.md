# Terrain: world size, heightmap, land maps

Examined 2026-10-04 on the Steam install (Kenshi 1.0.68, "Newland" world). Readers:
`Meitou.Data.World.WorldLayout`, `TiffImage`, `TerrainHeightmap`, `RawHeightTile`; texturing: `BlendInfoFile`,
`BiomeTerrain`, `TerrainMaps`; meshes: `HeightWindow`, `TerrainMesh` (drawn by `meitou-viewer --world`, [../viewer.md](../viewer.md#world-mode)). Checks:
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
p99 22856 (3418), p99.9 36973. The world border (corners and edge midpoints) is 0. Whether 0 is also the
water level is **Unknown** (the exe has a shader parameter `waterHeightRel`).

Streaming: the reader never loads the whole 537 MB; `Downsample(step)` streams rows,
`HeightAt` / `Sample` seek per sample.

## Other maps in `data/newland/land/`

The texturing ones are explained in [How the terrain is textured](#how-the-terrain-is-textured).

| File | Size | What it is |
| --- | --- | --- |
| `biomemap.png` | 1024², RGBA | Biome regions as flat colours, one BIOMES `index` colour per pixel (288 units). Same orientation as the heightmap (**Verified**, below) |
| `blendmap.png` | 1024², RGBA | Per-pixel weights of the biomes listed in `blendinfo.dat` (**Verified**, below) |
| `areasmap.tga` | 256², 24-bit uncompressed (18-byte header + 196,608) | Not analyzed; if it covers the world, 4 × 4 pixels per zone. Same size as `data/land/areasmap.tga` |
| `blendinfo.dat` | 1,364,352 | Magic `KBI1`: the biomes of each 2 × 2-zone cell (**Verified**, below), then bytes of Unknown meaning |
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
- **`blendinfo.dat`**: `char[4] "KBI1"`, `int32 cellsX` (32), `int32 cellsZ` (32), then `cellsX × cellsZ × 5`
  `uint32` slot values, row-major with the row along +Z (cell `(x, z)` at index `z × 32 + x`). A cell is
  294912 / 32 = 9216 units, 2 × 2 zones, 32 × 32 pixels of the 1024² maps. A slot value holds a biome colour in
  its low 24 bits (0 = unused); cells use 1 to 4 of the 5 slots (355, 386, 227, 56 cells). After the table
  (byte 20,492) come 1,343,860 more bytes: an `int32` 2048, then bytes such as `0x21 0x22 0x24 0x28 0x30 0x43 0x45`
  that look like a slot count in bits 5–6 plus a 5-bit slot mask (Observed); probably which shader
  permutation each terrain tile needs. Meaning **Unknown**; `BlendInfoFile.Trailer` keeps them raw.
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
- Not reproduced in the viewer: wetness and water (`waterHeightRel`, absorbance), the interior clip mask, the
  distant-terrain material (`DistantTerrain`, `distant.png`), LOD morphing (the vertex shader blends height
  and normal towards a coarser level stored in `position.w` and `BINORMAL`).

### Terrain mesh in the viewer

`TerrainMesh` cuts a `HeightWindow` (every n-th heightmap sample of a rectangle, read row by row) into chunks of
64 × 64 cells; each chunk has one vertex buffer and shares per-LOD index buffers (every 2^l-th vertex) plus
skirts that hang below its edges to hide LOD cracks. Normals are central differences of the window's heights.
Triangles `(i, j), (i, j+1), (i+1, j)` face +Y (`TerrainMeshTests`). The game's own terrain LOD scheme is
**Unknown** beyond the morph described above.

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
- How the game interpolates heights between samples, and what the terrain LOD/paging does (exe mentions
  `PLSM2`, `TerrainNode`, `DistantTerrain`).
- Meaning of the bytes after the `blendinfo.dat` table, of `areasmap.tga`, and which blend slot the game treats as a page's first biome.
- The water level and how water, wetness and the distant terrain are drawn.
- Whether anything still reads `data/land/grasssplits`.
