# Far-billboard impostors

Meitou-mode feature (switch name `impostors`, owned and wired by the GPU-driven foliage path, [renderer-native.md](renderer-native.md)
5.7). Beyond a per-mesh distance, a foliage instance is drawn as one camera-facing quad. The quad samples a pre-rendered atlas of the mesh
seen from many directions. This doc covers the baker, the file format and cache, the sampling GLSL, and the preview that checks them. The
original game has no impostors ([formats/foliage.md](formats/foliage.md), "no impostor level was found"), so nothing here is a
compatibility claim. Faithful mode never draws impostors.

Claims use the labels of renderer-native.md:

- **from the code**: true of the code as written.
- **measured**: numbers from a run, with the machine.
- **estimate**: reasoned, not measured.
- **open**: not settled.

Code: `src/Meitou.Rendering/Impostors/`. Tests: `tests/Meitou.Tests/Rendering/ImpostorTests.cs`. Preview:
`tools/Meitou.ModelViewer/ImpostorApp.cs`.

## 1. Technique: hemi-octahedral frames

**Choice (from the code).** One atlas per foliage mesh, a square grid of N × N frames. Frame (i, j) is the mesh rendered orthographically
from the direction `Decode((i, j) / (N − 1))` of a **hemi-octahedral** map of the upper hemisphere:

- encode: `d.y = max(d.y, 0)`, `s = |x| + y + |z|`, `xz = d.xz / s`, `uv = (xz.x + xz.z, xz.x − xz.z) / 2 + 0.5`
- decode: `e = uv·2 − 1`, `xz = (e.x + e.y, e.x − e.y) / 2`, `d = normalize(xz.x, 1 − |xz.x| − |xz.y|, xz.y)`

The grid's border is the horizon, and the centre looks straight down.

**Why this layout:**

- Kenshi's camera is a strategy camera, at most 1840 above the pivot and never below the ground ([formats/camera.md](formats/camera.md)).
  Foliage is seen from above or at the horizon, never from below, so a full octahedron would spend half its frames on views that never
  occur.
- Compared with a spherical (latitude/longitude) grid, the octahedral map spreads frames almost uniformly over the hemisphere. It has no
  crowding at the pole, and any view falls inside one grid triangle with three frames to choose from.
- Compared with a ring of horizontal views (classic billboard), high views from the strategy camera stay correct.

Views below the horizon are clamped onto it.

**Frame basis (from the code).** For frame direction `d` (from the mesh towards the viewer):

- `reference = d.y > 0.999 ? (0, 0, −1) : (0, 1, 0)`
- `right = normalize(cross(reference, d))`
- `up = cross(d, right)`

A point `p` lands at frame UV `(dot(p − c, right), dot(p − c, up)) / 2r + 0.5`, where `c` and `r` are the bounding sphere's centre and
radius. Its depth is `dot(p − c, d) / r · 0.5 + 0.5`.

**Bounding sphere (from the code).** The sphere covers the main mesh plus the leaves mesh. Its centre is the vertex box centre, and its
radius is the farthest vertex × 1.01.

## 2. Maps and sizes

Three maps share one layout: frame (i, j) occupies cell (i, j) of the atlas. Rows run bottom first (GL order).

| Map | Channels | Encoding |
| --- | --- | --- |
| Albedo | RGB albedo (the mesh shader's albedo before lighting), A coverage | BC3 |
| Normal | Octahedral full-sphere encoding of the normal in the frame's basis (x right, y up, z towards the viewer) | BC5 |
| Depth | R depth along the frame direction (sphere-relative), G gloss × specular | BC5 |

The normal map is full-sphere because back-facing double-sided leaves have normals pointing away from the frame. A z ≥ 0 encoding lost
them (from the code, baker version 2).

**Size classes (from the code, `ImpostorClass`).** The class is chosen by the largest instance's radius (mesh radius × the
FOLIAGE_MESH's maximum scale):

| Radius × max scale | Class | Frame | Grid | Atlas | Levels | GPU, 3 maps with mips |
| --- | --- | --- | --- | --- | --- | --- |
| below 48 | none | | | | | |
| 48 to 160 | medium | 128 | 12 × 12 | 1536² | 6 | 9 MB |
| 160 and above | large | 256 | 12 × 12 | 3072² | 7 | 36 MB |

- Below 48 world units an object is at most a few pixels by the time the transition distance is reached. It is cheaper to let it fade
  out at its range (estimate).
- A 12 × 12 grid puts adjacent frames about 8 to 15 degrees apart. The off-frame views in the preview sheets (section 6) keep the
  crown's shape with the dithered frame pick (section 5). Whether a smaller grid would do is open: no other grid was compared.
- The mip chain stops at 4 × 4 pixels per frame, the smallest frame that still holds a BC block (from the code: `Levels = log2(frame) −
  1`).

**Compression decision (from the code):**

- Albedo + coverage is BC3: colour in BC1, coverage in the BC4 alpha block.
- Normal and depth/gloss are BC5: two independent BC4 channels, which keep the octahedral normal and the depth smooth.
- The BC3 colour endpoints are fitted along the principal axis of the block's **covered** pixels (`ImpostorEncoder.EncodeBc3`).
  - The engine's DDS encoder uses bounding-box endpoints, and turns a block of orange leaves and blue-grey twigs into two greys. Frame
    edges are full of such blocks.
  - Measured: the image difference against the mesh barely moved (mean 10.21 to 10.22 on BushTree01's near field). The remaining
    difference is resolution (section 6), not compression.
- An uncompressed RGBA8 option exists in the format and the baker (`ImpostorBaker.Compress = false`, tests).
  - Open: through VkGl a 3072² RGBA8 atlas samples with wrong coverage although the CPU data is right (checked against the PNG dump).
  - The viewer flag was removed.

## 3. The bake

**At load, from the code (`ImpostorBaker`):**

1. Each row of N frames is rendered into a target of `2·N·frame × 2·frame` (RGBA8 + 24-bit depth) through `IGl`. Each frame has its own
   viewport and an orthographic projection fitted to the sphere.
2. The renderer is the **shared mesh shader** (`Shaders.MeshFragment`) with an output switch inserted before its lighting
   (`ImpostorShaders.BakeFragment`). The albedo, normal and gloss are therefore exactly what the mesh shader would have lit, including
   the alpha test, second texture, normal map and triplanar mode.
3. Three passes per row:
   - pass 0: albedo
   - pass 1: normal in the frame basis
   - pass 2: depth and gloss
   Each writes alpha 1 on kept samples and 0 elsewhere.
4. A GPU linear blit halves each pass. That gives a 2 × 2 box filter: alpha becomes coverage and the colour is premultiplied by it. Only
   the half-size picture is read back.
5. While the GPU renders row k + 1, the CPU filters row k (`ImpostorAssembler`, in parallel per frame):
   - un-premultiply
   - **pull-push fill** of empty texels from the covered ones, so bilinear filtering and mips never pull black or a wrong normal into
     the silhouette
   - per-frame mips (coverage-weighted, so frames never bleed into each other)
   - **coverage scaling**: each level's coverage, level 0's included, is scaled so the share of texels ≥ 0.5 equals the frame's covered
     area. This is the alpha-test mip fix. Applied to level 0 too, it keeps branches thinner than half a texel, which otherwise had a
     2 × 2 coverage of 0.25 and vanished (measured: they disappeared in the preview before this was added).
6. BC encoding (section 2).

**Exclusions (from the code, `ImpostorSource.Ineligible`):**

- FOLIAGE_MESH records drawn in TERRAIN mode (MaterialType 2, textured by the biome under them) are skipped. One atlas cannot hold every
  biome's colour.
- EMISSIVE meshes (6) are skipped.

There are 142 such records in the base game (measured).

**Measured: base game, RTX 4070, Release, `--impostor-bake-all --rebake`:**

| | Value |
| --- | --- |
| FOLIAGE_MESH records | 643 |
| TERRAIN / EMISSIVE (skipped) | 142 |
| Distinct sources (mesh, material, max scale) | 485 |
| Too small (no impostor) | 218 |
| Baked | 267 (121 large, 146 medium) |
| Bake time | 407 s total; 1.5 s mean, 14.8 s worst per atlas; a large atlas typically 1.2 to 4.6 s, mostly GPU render, ~0.3 s BC encode |
| Disk cache | 1153 MB (deflated BC data) |
| Loading all 267 from the cache | 24 s (about 90 ms each: inflate + checksum) |
| GPU memory if all resident | 5669 MB (large 4356, medium 1314) |

**Consequences for the foliage path (estimate):**

- Baking everything synchronously at load is too slow, and keeping everything resident is too big.
- Bake or load an atlas **on demand**, when a mesh's instances first come within impostor range, on a background queue.
- Keep only the atlases of the resident biomes on the GPU.
- Under memory pressure, drop level 0 of large atlases (9 MB instead of 36). The texture-quality setting's mip skip
  ([formats/settings.md](formats/settings.md)) is the natural knob.

## 4. File format and cache

**`.mimp`, from the code (`ImpostorAtlas.Write` / `Read`).** Little-endian, written in this order:

1. Header:

   | Field | Type |
   | --- | --- |
   | magic | `"MIMP"` (u32) |
   | format version | i32, 1 |
   | baker version | i32, 4 |
   | grid | i32 |
   | frame pixels | i32 |
   | levels | i32 |
   | centre | 3 × f32 |
   | radius | f32 |
   | name | .NET length-prefixed UTF-8 string |
   | texture count | i32 |

2. Per texture: map (i32), encoding (i32: 0 RGBA8, 1 BC3, 2 BC5), then each level's byte length (i32).
3. FNV-1a 64 of all level data.
4. All levels, map by map and largest first, in one zlib stream.

`Read` returns null on any mismatch: magic, either version, sizes, checksum, or a truncated stream. A damaged or old file is simply
rebaked.

**Cache, from the code (`ImpostorCache`):**

- Location: `%LOCALAPPDATA%\Meitou\impostors\<name>_<key>.mimp`, overridable with `MEITOU_IMPOSTOR_CACHE` or the constructor. Never in
  the repo.
- The key is the first 24 hex digits of a SHA-256 over:
  - the baker and format versions
  - each source file's path, length and last write time (mesh, leaves mesh, every texture)
  - both materials' descriptions
  - the maximum scale
- A mod that replaces a mesh or texture changes the key. Old files are left in place (open: a size cap / LRU sweep).
- Files are written to a temporary name, then moved into place, so a crash never leaves a half file under the real name.
- Bump `ImpostorAtlas.BakerVersion` whenever the baker's output changes.

## 5. Runtime sampling (for the GPU-driven foliage path)

**GLSL (from the code, `ImpostorShaders`):**

| Piece | Contents |
| --- | --- |
| `Functions` (any stage) | `impostorEncode(vec3)`, `impostorDecode(vec2)`, `impostorDecodeNormal(vec2)`, `impostorBasis(dir, out right, out up)`, `impostorSelect(view, grid, out a, out b, out c, out weights)`, `impostorFrameUv(dir, right, up, centre, radius, origin, ray, height, out point)` |
| `FragmentFunctions` (after `Functions`) | `struct ImpostorSurface { vec3 albedo; float coverage; vec3 normal; float gloss; vec3 position; }` and `ImpostorSurface impostorSample(albedoMap, normalMap, depthMap, grid, centre, radius, origin, ray, cellA, cellB, cellC, weights, float pick, bool parallax)` |
| `Vertex` / `Fragment` / `FragmentWithDepth` | A complete reference program on the foliage instance ABI |

The reference program:

- Per-instance matrix rows at locations 7 to 10, row 0 w the fade, 2 meaning whole.
- Six vertices per instance from `gl_VertexID`, no vertex buffer.
- Uniforms: `uImpostor` (centre, radius), `uImpostorGrid`, `uImpostorAlbedo/Normal/Depth`, `uImpostorParallax`, `uImpostorBlend`,
  `uImpostorDebug` (1 albedo, 2 normal, 3 coverage), `uCameraUp`, plus the mesh shader's lighting uniforms.

**Per instance (vertex stage):**

- Transform the eye into object space and pick the grid triangle containing the view direction (`impostorSelect`: three cells and
  barycentric weights, split on the cell's anti-diagonal).
- The quad faces the eye, rolled by the camera's up axis. Its half-size is `r·d / sqrt(d² − r²)`, which covers the sphere's silhouette at
  its centre's plane.

**Per pixel (fragment stage):**

1. **Virtual frame-plane projection.** The object-space ray from the eye through the pixel is intersected with each frame's plane
   through the centre. This gives that frame's UV, so frames line up at the sphere centre's depth whatever the view direction.
2. **Optional parallax** (`parallax = true`, estimate: off by default). One step: read the frame's depth at that UV and re-intersect
   with the plane moved to it.
3. **Frame pick (default) vs blend.**
   - Pick: `pick` is a dither value in [0, 1) (the reference uses a 4 × 4 Bayer matrix). It selects the one frame whose cumulative
     weight passes it, so each pixel shows a crisp single frame and the three interleave. Temporal anti-aliasing averages them.
   - Blend: `pick < 0` blends all three by weight.
   - Measured in the preview sheets: blending thins leaves and branches that do not line up between the frames into a soft mush,
     while picking keeps the crown's density.
4. **Texture gradients.** Every fetch uses `textureGrad` with frame A's gradients. The pick makes the fetches non-uniform control flow,
   and the frames are a grid step apart, so their scales agree.
5. **The cut-out.** Cut at coverage 0.5, the same rule the bake's coverage scaling targets. With a multisampled target and `uCoverage`,
   an alpha-to-coverage ramp over `fwidth(coverage)` is used, as the foliage meshes do.
6. **Result.** `ImpostorSurface` returns albedo, object-space normal (the frame basis back to object space), gloss, and the object-space
   position at the baked depth.

**Lighting contract (from the code).** The impostor's surface goes through the **same lighting as the mesh**: the mesh shader's simple
sun + hemisphere ambient + specular, then `kenshiLight(albedo, n, v, gloss, world)` and `atmoApply` when the atmosphere is on. The
normal and position are transformed by the instance matrix, so a live sun lights the impostor as it lights the mesh. Nothing is baked
into the albedo but the albedo. The preview sheets under three suns (high, low, behind) show matching shading direction and strength
(measured by eye).

**Depth (from the code).**

- `Fragment` keeps early depth testing and writes the quad's depth.
- `FragmentWithDepth` writes the depth of the reconstructed surface (`gl_FragDepth`), so impostors intersect each other and the terrain
  correctly and can cast shadows into a depth-only pass. This costs early-Z.
- Use it for the shadow pass and where trees stand in clusters (estimate).

**Crossfade (from the code).** The mesh fades out with its usual dither (`M14 = fade`, discard where `dither ≥ fade`). The impostor gets
`M14 = −fade` and discards the complement (`dither < fade`). Over the fade band each pixel shows exactly one of the two, with no double
coverage and no gap.

**Transition distance (from the code, `ImpostorLayout.TransitionDistance`).** The impostor shows one atlas texel per screen pixel when the
instance's projected diameter equals the frame size:

`d = R_world · H / (tan(fov / 2) · framePixels)`

`R_world` is the radius × instance scale and `H` the viewport height in pixels.

- Measured for BushTree01 (radius 126.4, scale 2 to 4, large class), FOV 50:

  | Viewport height | Transition distance |
  | --- | --- |
  | 900 | 1906 to 3811 |
  | 1080 | 2287 to 4574 |
  | 2160 | 4574 to 9147 |

- Recommendation (estimate): switch at this distance (per instance, from its own scale) or beyond. The fade band is the last 10% before
  it. Closer than the transition the impostor is magnified and visibly softer than the mesh.

**GPU cost per impostor (estimate):**

- Vertex: 6 vertices, trivial.
- Fragment, default path (pick, no parallax): 3 fetches (albedo, normal, depth: all BC, 1 byte/texel), plus about 60 ALU ops for 3
  decode/basis/projection setups, plus the mesh lighting.
- Parallax adds one fetch. Blending makes it 9 fetches.
- At the transition a large tree covers at most 256² = 65k pixels, of which roughly 30 to 50% pass the cut. Coverage falls with 1 / d².
- So a field of a thousand far trees costs on the order of a few million fragment shades: well below one full-screen pass at 1080p for
  the area beyond 2× the transition distance.
- Measuring it needs the foliage path's GPU timers (open).

## 6. Verification: `--impostor-preview`

Command: `meitou-viewer --impostor-preview <FOLIAGE_MESH name, string id or .mesh file> [--out dir] [--size WxH] [--rebake] [--parallax]
[--blend] [--debug n] [--depth-write] [--class medium|large]`. It renders offscreen (4× MSAA). `--impostor-bake-all [--rebake]` bakes or
loads every eligible mesh and prints per-mesh and total time and size (section 3).

Outputs in `--out` (default `C:\Temp\meitou-impostors`):

| File | What it shows |
| --- | --- |
| `<name>-atlas-{albedo,normal,depth}.png` | Level 0 as stored (decoded from BC) |
| `<name>-sheet-{high,low,back}.png` | Mesh / impostor pairs at 1500 units under three suns; rows are camera elevations −8 to 85°, columns four azimuths |
| `<name>-frames.png`, `-frames2.png` | Cameras exactly on frame directions (bake, layout and projection check) |
| `<name>-field-{mesh,impostor}.png` | 160 random instances 1500 to 4000 units away |
| `<name>-near-{mesh,impostor}.png` | 40 random instances 1500 to 2000 units away |

The field and near pictures use FOV 50, the eye at height 400. Compare pairs with `meitou-tools image-diff a b`.

**Measured, BushTree01, Release, RTX 4070, 1600 × 900:**

- **On frame directions.** The impostor's silhouette matches the mesh's. This verifies the bake, the layout and the projection.
- **Sheets.** Off-frame views keep the crown shape, trunk and shading direction under all three suns.
- **Field (1500 to 4000).** Mean difference 8.10 / 255, 14.0% of pixels over 12. Most of it is the leaves' sub-pixel placement, which no
  billboard reproduces.
- **Near (1500 to 2000).** Mean 10.22, 16.2% over 12.
  - These instances are *inside* their transition distance (1906 to 3811 at 900p), so the impostor is magnified. The mesh's individual
    orange leaves and dark twigs read crisper and more saturated, while the impostor's leaves look paler and more olive (texel
    averaging of leaf and twig).
  - The BC3 encoder change did not move this, so the cause is resolution, not compression.
- At its intended distance (≥ 1906 here) a tree is hard to tell from the mesh. Inside it the difference is visible on close inspection.

**Open:**

- The RGBA8 upload issue (section 2).
- Triplanar materials are baked in object space (the mesh's world position at bake time), so a rotated instance's triplanar pattern
  does not follow the world. That is invisible at impostor distances (estimate).
- The bake uses the simple path's specular input (`gloss × uSpecular`), not a per-material Kenshi BRDF parameter set.
- Views below the horizon are clamped to it.
- Wind sway is not represented: the impostor is the rest pose.
- Cache size cap.
