# Terrain and land maps

Original-file research: TIFF tags, placement/road/town cross-checks, blend/overlay data, exe and
shipped terrain/water/sky shaders. Field tables/expressions retain evidence scopes. Engine LOD,
windows and water enhancements: [render-terrain](../render-terrain.md), [render-water](../render-water.md).

## Coordinates and height

**Verified** extent[-147456,+147456]XZ, Y up;64² zones4608, index=floor(coord/4608)+32.
Fullmap.tif is little-endian TIFF16385² uint16 BlackIsZero/uncompressed; read tags rather than
hard-code strip location. Sample(i,j)=(18i-147456,18j-147456); +X columns,+Z rows; height=raw*9800/65535.
Orientation verified by road residuals; scale by building/road/town fits and LevelFileTests.
Bilinear is our interpolation; exact original interpolation remains unknown. Downsample/window
reads stream rows/samples rather than allocating the entire537MB. Unit evidence: [pathfinding](../game/pathfinding.md).

## Material maps

Blendinfo slot masks/quadtrees select biome layers per cell, distinct from biomemap and region
areasmap. Preserve row/axis layout, slot ordering and mask intersections across zone/window edges.
Overlay RGBA=grass/grass-spots/dirt/road; texture coordinates use world scale, not chunk-local
indices. Colour maps and blend/overlay maps have different scales/meanings. BIOMES loader
converts field units into shader parameters; table retains scales, heights, normals, colours,
absorbance and road/layer weights. Blend source diffuse/normal/alpha correctly, including
triplanar material modes and far-map tint. Do not infer biome/weather region from one shared map.

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
  alone applies (67,292 pixels). Tested in `TerrainBiomeTests.Base_game_slot_masks_and_channels_describe_the_blend_map`.
  Weights are mostly 0 or 255: biome borders are hard, 288 units wide (Observed, debug view).

## LOD, water and sun

Original terrain LOD/chunk/detail settings are not Meitou CDLOD. Preserve game settings research,
while renderer documents own screen-error/morph/streaming policy. Legacy data/land tiles are a
separate layout, not current fullmap indexing.

**Verified** water rootY100; biome material/scum/normal/turbulence/scale rules in tables. Original
water is flat and reflection/opacity/glitter/shader depth rules are separate from Meitou FFT/surf.
**Verified** (shipped `forward/water.hlsl`, `water.material`): the alpha's depth is the G-buffer distance at the pixel
(`depthMap` = compositor `global_gbuffer` 2, `length(world - camera) / farClip`) minus the fragment's own distance: the
distance through the water to whatever is behind it, objects included; then `a *= saturate(depth * invOpacity)` (by Fresnel) and `saturate(depth / 2)`.
**Observed** flowmap RG fails channel directions; B is scum input. RiverFlowBake is an engine
height-derived alternative, not a decoded meaning of the source RG. Shore/rain/wetness unknowns
stay explicit. Sun trajectory derives latitude/sunrise/sunset and sky controller; [sky](sky.md),
[lighting](lighting.md), [weather](weather.md) own shared atmospheric/time inputs.

### World coordinates

| Fact | Value | Status |
| --- | --- | --- |
| World extent | X and Z in [-147456, +147456], 294912 units per side, centred on the origin | **Verified** (below) |
| Zone grid | 64 × 64 zones of 4608 units; zone index = `floor(coord / 4608) + 32` on each axis | **Verified**: all 11,714 placed buildings lie in the zone of the file that places them (`LevelFileTests.Base_game_world_placements_are_consistent`), and all 224 towns whose state lists zones list the zone their position falls in |
| Heightmap | 16385 × 16385 samples, 18 units apart (256 per zone, plus the far edge) | **Verified**: file size and tags, and heights below |
| Height scale | `height = raw × 9800 / 65535`; raw 0 = height 0 | **Verified** (below) |
| Unit size | If 1 unit = 1 dm, the world is 29.5 km across (870 km², the size usually quoted by players); 1 cm would make it 2.9 km | **Unknown**; decimetres fit best (**Observed**: `human_left_arm.mesh` is 6.5 units long, 65 cm at 1 dm) |

### Other maps in `data/newland/land/`

| File | Size | What it is |
| --- | --- | --- |
| `biomemap.png` | 1024², RGBA | Biome regions as flat colours, one BIOMES `index` colour per pixel (288 units). Same orientation as the heightmap (**Verified**, below) |
| `blendmap.png` | 1024², RGBA | Per-pixel weights of the biomes listed in `blendinfo.dat` (**Verified**, below) |
| `areasmap.tga` | 256², 24-bit uncompressed (18-byte header + 196,608) | BIOME_GROUP colour per zone: 4 × 4 pixels per zone over the whole world, no flip (**Verified**, [weather.md](weather.md#scheduling-rules)). Same size as `data/land/areasmap.tga` |
| `blendinfo.dat` | 1,364,352 | Magic `KBI1`: the biomes of each 2 × 2-zone cell, then a quadtree of slot masks per cell (**Verified**, below) |
| `fogfeatures.dat`, `features.dat` | | See [zones.md](zones.md#placement-files-and-assembly) |
| `overlaymaps/colour.X.Y.png` | 64 files, 2048² each, X, Y = 0..7 | 8 × 8 tiles = 16384² ground tint, 18 units per pixel (below) |
| `overlaymaps/new_overlay.X.Y.png` | 64 files, 1024² each | 8 × 8 tiles = 8192² layer map (grass, dirt, road), 36 units per pixel (below) |
| `overlaymaps/` others | | `biomemap.png`, `ambientmap.png`, `flowmap.png`, `watercolourmap.png`, `distant.png`, `debug.png`, `prosp.tga`, and tool leftovers `DevIL.dll`, `joiner.exe` |
| `textures/` | 364 `.dds` | Terrain layer textures (`*_DIF`, `*_NML`), named by BIOMES records |
| `navtiles/` | 3,995 `tileX.Y.hkt` + `seeds.def` | Havok navmesh tiles (see overview) |

### BIOMES fields and shader parameters (Verified, kenshi_x64.exe)

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

### Material parameters (Verified, the per-biome material builder)

| Shader parameter | From BIOMES fields |
| --- | --- |
| `scale` | (`water scale X`, `water scale Y`, `scum scale X`, `scum scale Y`) / 5000 |
| `invStrength` | 1 / max(`water strength`, 0.0001) |
| `invOpacity` | 1 / max(`water visibility` × 10, 1) |
| `gloss`, `glow` | `water gloss`, `water glow` |
| `distortion` | (`water distortion`, `scum distortion` / ((scum X + scum Y) / 2)) |
| textures | `water normal`, `texture scum`, `texture scum normal`, `turbulence map` |

## Open questions

- World unit in metres (decimetres most likely).
- How the game interpolates heights between samples, and the details of its terrain paging (page sizes, when
  pages load; the known constants are under [Terrain LOD](#lod-water-and-sun)).
- Which blend slot the game treats as a page's first biome.
- Wetness after rain, the scum layer, the unit of the water shader's time, and the fog distances (runtime
  settings).
- Whether anything still reads `data/land/grasssplits`.

## Expression reference

- World coordinates: `height = raw × 9800 / 65535`; `zone = floor(x / S) + 32`
- Layer model: `slope = 1 − normal.y`; `uv = p.xz / 5000 × tiling`; `v = 1 − p.y / 5000`; `binormal = normalize(n × (−1, 0, 0))`; `tangent = binormal × n`
- Placement: `waterHeightRel = waterHeight − origin.y`
- Sun path: `a = phase × π`
- Atmosphere: `pFogParams = (D, 0.06 D, min(D, 0.6 D), 0)`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Which biomes apply where (Verified)
- BIOMES fields and shader parameters (Verified, kenshi_x64.exe)
- Layer model (Observed, terrainfp4.hlsl)
- Why some sea areas in the north-east have other tones (Observed)
- Placement (Verified, kenshi_x64.exe)
- Material parameters (Verified, the per-biome material builder)
- Shading (Observed, water.hlsl)
- Sun path (Verified, kenshi_x64.exe sky controller and the CONSTANTS record)
- Atmosphere (Verified constants, Observed shader)
- Legacy terrain: `data/land/` (Observed)
