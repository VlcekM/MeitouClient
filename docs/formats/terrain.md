# Terrain: world size, heightmap, land maps

Examined 2026-10-04 on the Steam install (Kenshi 1.0.68, "Newland" world). Readers:
`Meitou.Data.World.WorldLayout`, `TiffImage`, `TerrainHeightmap`, `RawHeightTile`. Checks:
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

## Other maps in `data/newland/land/` (Observed, not decoded)

| File | Size | What it is |
| --- | --- | --- |
| `biomemap.png` | 1024², RGBA | Biome regions as flat colours. Same orientation as the heightmap (Observed: rendered side by side, coastlines and regions line up) |
| `blendmap.png` | 1024², RGBA | Terrain texture blending; not analyzed |
| `areasmap.tga` | 256², 24-bit uncompressed (18-byte header + 196,608) | Not analyzed; if it covers the world, 4 × 4 pixels per zone. Same size as `data/land/areasmap.tga` |
| `blendinfo.dat` | 1,364,352 | Magic `KBI1`, then int32 32, int32 32; data looks like 20-byte entries. **Unknown** |
| `fogfeatures.dat`, `features.dat` | | See [zones.md](zones.md#other-placement-files) |
| `overlaymaps/colour.X.Y.png` | 64 files, 2048² each, X, Y = 0..7 | 8 × 8 tiles = 16384² colour map, one pixel per heightmap cell (Observed from names and sizes) |
| `overlaymaps/new_overlay.X.Y.png` | 64 files | Second overlay set, same tiling (Observed from names) |
| `overlaymaps/` others | | `biomemap.png`, `ambientmap.png`, `flowmap.png`, `watercolourmap.png`, `distant.png`, `debug.png`, `prosp.tga`, and tool leftovers `DevIL.dll`, `joiner.exe` |
| `textures/` | 364 `.dds` | Terrain textures (`*_DIF`, `*_NML`) |
| `navtiles/` | 3,995 `tileX.Y.hkt` + `seeds.def` | Havok navmesh tiles (see overview) |

Exe strings (Observed, simple string search of `kenshi_x64.exe`): `fullmap`, `data\newland/land\`,
`Failed to open terrain map`, `ZoneMap::createTextureArray`, `Failed to load blend map`,
`Failed to load index map`, `BiomeMap`, shader parameters `worldSize`, `worldOffset`, `waterHeightRel`,
`blendinfo.dat`, `features.dat`, `fogfeatures.dat`.

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
- Meaning of `blendinfo.dat`, `blendmap.png`, `areasmap.tga`, and how biome colours map to `BIOMES` records.
- Whether anything still reads `data/land/grasssplits`.
