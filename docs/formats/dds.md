# DirectDraw Surface (`.dds`)

Sources:
- **Public documentation** (Microsoft): `DDS_HEADER`, `DDS_PIXELFORMAT`, `DDS_HEADER_DXT10`, and
  "Block Compression (Direct3D 10)" for BC1–BC5. `Meitou.Data.Textures.DdsReader` / `DdsDecoder` are written
  from these descriptions.
- **Verified 2026-10-04** (`DdsReaderTests.Reads_every_base_game_dds`): all 1,907 base-game `.dds` files parse,
  their computed surfaces fit the file exactly (two exceptions below), and 25 files of each format decode
  (every image, largest and smallest mip level).

## Layout

Little-endian throughout.

```
char[4]  magic "DDS "
DDS_HEADER (124 bytes)
  uint size              124
  uint flags             DDSD_*: CAPS 0x1, HEIGHT 0x2, WIDTH 0x4, PITCH 0x8, PIXELFORMAT 0x1000,
                         MIPMAPCOUNT 0x20000, LINEARSIZE 0x80000, DEPTH 0x800000
  uint height, width
  uint pitchOrLinearSize (ignored by the reader; sizes are computed)
  uint depth             volume textures only
  uint mipMapCount       only meaningful with DDSD_MIPMAPCOUNT
  uint reserved1[11]
  DDS_PIXELFORMAT (32 bytes)
    uint size            32
    uint flags           ALPHAPIXELS 0x1, ALPHA 0x2, FOURCC 0x4, RGB 0x40, YUV 0x200, LUMINANCE 0x20000
    char fourCC[4]       e.g. DXT1, DXT3, DXT5, DX10
    uint rgbBitCount
    uint rMask, gMask, bMask, aMask
  uint caps, caps2       caps2: CUBEMAP 0x200, faces +X..-Z 0x400..0x8000, VOLUME 0x200000
  uint caps3, caps4, reserved2
[fourCC == "DX10"] DDS_HEADER_DXT10 (20 bytes)
  uint dxgiFormat, resourceDimension (3 = 2D, 4 = 3D), miscFlag (0x4 = cube), arraySize, miscFlags2
surface data
```

Surface order: image by image (cube faces +X, -X, +Y, -Y, +Z, -Z, or array slices), each with its whole mip
chain from largest to smallest. Level *n* is `max(1, w >> n)` × `max(1, h >> n)`. Sizes:

| Format | FourCC / DXGI | Bytes |
| --- | --- | --- |
| BC1 | `DXT1`, DXGI 70–72 | 8 per 4×4 block, `ceil(w/4) × ceil(h/4)` blocks |
| BC2 | `DXT2`, `DXT3`, DXGI 73–75 | 16 per block |
| BC3 | `DXT4`, `DXT5`, DXGI 76–78 | 16 per block |
| BC4 / BC5 | `ATI1`/`BC4U`, `ATI2`/`BC5U`, DXGI 79–80 / 82–83 | 8 / 16 per block |
| uncompressed | masks in the pixel format, or DXGI 27–29, 85–93, 115, 61, 65 | `w × h × bitCount / 8`, no row padding |

The mip count is 1 unless `DDSD_MIPMAPCOUNT` is set and the count is non-zero (the same rule as Ogre's
DDS codec, Observed (source)).

### Block decoding (from the public spec; pinned by `DdsReaderTests`)

- **Colour block** (BC1, and the second half of BC2/BC3): two RGB565 endpoints, then 16 two-bit indices,
  pixel 0 in the low bits, row by row. 565 expands by bit replication. If `color0 > color1`, colours 2 and 3
  are 2/3–1/3 and 1/3–2/3 mixes; otherwise colour 2 is the midpoint and colour 3 is **transparent black**
  (BC1 only; BC2/BC3 always use four colours).
- **BC2 alpha**: 64 bits, 4 bits per pixel, scaled ×17.
- **BC3 alpha / BC4 / BC5 channel**: two 8-bit endpoints and 16 three-bit indices (48 bits). `a0 > a1` gives 6
  interpolated values (8 total); otherwise 4 interpolated values plus 0 and 255.
- Interpolated values are rounded to nearest (the spec allows small hardware differences).
- BC4 decodes to grey (R = G = B); BC5 to (R, G, 0) with alpha 255.

## Base game survey (Verified, `DdsReaderTests.Reads_every_base_game_dds`)

| Format | Files |
| --- | ---: |
| DXT5 (BC3) | 1,157 |
| DXT1 (BC1) | 698 |
| DXT3 (BC2) | 47 |
| DXT5 cubemap | 3 |
| DXT1 cubemap | 1 |
| uncompressed 16-bit X1R5G5B5 (`meshes/sky0026.dds`) | 1 |
| **total** | **1,907** |

- No DX10 headers, no volume textures, no BC4/BC5, no 24/32-bit uncompressed files.
- 64 files have a single level (no mip chain); 64 have a non-power-of-two side. All sides are multiples of 4
  (Observed in a scratch survey, not in the test).
- Every file is exactly header + computed surfaces, except `land/textures/randomredrock.dds` and
  `randomredrock_N.dds` (512×512 DXT1, 10 levels): 16 extra bytes at the end, i.e. two more 8-byte blocks
  than a full chain. Readers ignore them.
- The other texture files under data/ are 1,967 `.png`, 11 `.tga` and 1 `.jpg`
  (`TextureLoaderTests.Loads_base_game_png_tga_and_jpg` decodes all `.tga` / `.jpg` and every 20th `.png` with
  StbImageSharp).

## Channel conventions in Kenshi's textures

Not part of the DDS format, but needed to use the files (details in [../viewer.md](../viewer.md)):

- Diffuse alpha is **gloss** ("Shininess is encoded in the alpha channel", `fcs.def`), not transparency.
- Building / object / foliage normal maps are plain RGB tangent-space normals; their **alpha** is the
  cut-out mask (BuildingShader ALPHA, FOLIAGE) or emissive amount (EMISSIVE). Verified for `Trees&VegAtlas02`
  and `Arc-Leaves` by looking at the decoded channels; consistent with `objects.hlsl` (`clip(normalTex.a - threshold)`).
- Character body normal maps are **swizzled**: R = G = B hold Y, alpha holds X (`human_male_body_normal.dds`:
  mean R, G, B all ≈ 126; Kenshi's `character.hlsl` reads `.wy`). Observed on the human body maps.

## Open questions

- Whether Kenshi's Ogre build loads textures with gamma correction (sRGB) or treats diffuse as linear.
