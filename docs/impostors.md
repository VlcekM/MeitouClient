# Far-billboard impostors

Meitou-mode feature (switch name `impostors`, owned and wired by the GPU-driven foliage path, [renderer-native.md](renderer-native.md)
5.7). Beyond a per-mesh distance, a foliage instance is drawn as one camera-facing quad. The quad samples a pre-rendered atlas of the mesh
seen from many directions. This doc covers the baker, the file format and cache, the sampling GLSL, the preview that checks them, and how
the foliage path draws them (section 7). The
original game has no impostors ([formats/foliage.md](formats/foliage.md), "no impostor level was found"), so nothing here is a
compatibility claim. Faithful mode never draws impostors.

Claims use these labels:

- **from the code**: true of the code as written.
- **Verified**: a test or the parity gate checks it.
- **Observed**: seen or measured in a run, with the machine.
- **Unknown**: not settled or not tried.

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
radius.

**Bounding sphere (from the code).** The sphere covers the main mesh plus the leaves mesh. Its centre is the vertex box centre, and its
radius is the farthest vertex × 1.01.

## 2. Maps and sizes

Two maps share one layout: frame (i, j) occupies cell (i, j) of the atlas. Rows run bottom first. Since baker version 5 (format version 2)
there is no depth map and no BC3: the VRAM of the first version (1.1 to 2.4 GB) was the reason.

| Map | Channels | Encoding | Resident levels |
| --- | --- | --- | --- |
| Albedo | RGB albedo (the mesh shader's albedo before lighting); alpha is a 1-bit cut-out | BC1 with punch-through alpha | all but the surface-skipped ones |
| Normal | Octahedral full-sphere encoding of the normal in the frame's basis (x right, y up, z towards the viewer) | BC5 | one level coarser than the albedo |

- **Albedo.** A transparent texel decodes to black with alpha 0, so bilinear filtering returns colour x coverage and the shader divides by
  the coverage (premultiplied sampling, `impostorSample`). **Verified** (`ImpostorTests`, BC1 cut-out test).
- **Normal.** Full-sphere because back-facing double-sided leaves have normals pointing away from the frame. It is stored at full
  resolution in the file but uploaded one level coarser (a normal needs less resolution than colour). The file also holds the mean gloss
  (`Gloss`, the mean of pass 2's G channel), passed as a push constant, replacing the per-texel gloss of the old depth map.
- **No depth.** The old depth map served parallax and `gl_FragDepth` (`MEITOU_IMPOSTOR_DEPTH`); both are gone. The quad's depth is used,
  and the shadow casters use the frame plane through the crown's centre.

**Frame size by distance (`ImpostorClass.For`).** With `ScreenDiameter(r) = 2 r / (2 tan(fov/2)) * 1080 / 4000` (the world radius of the
largest instance, `radius x max scale`, seen at the 4000-unit reference distance, 1080 lines, 50 degrees):

- `F = smallest power of two >= ScreenDiameter / 1.4`, clamped to 64 .. 256. The 1.4 (`MEITOU_IMPOSTOR_MAGNIFY`) lets the impostor be
  magnified by up to 1.4 at the reference distance. Meshes whose largest instance is smaller than radius 48 (`MinimumRadius`) get no
  medium or large impostor; since 2026-10-07 those with radius 2 or more and 100 or more triangles get the **small class** (section 11).
- Grid `G = 12` (`MEITOU_IMPOSTOR_GRID`). Levels = `log2(F) - 1` (the chain stops at 4 x 4 per frame, the smallest BC block).
- BushTree01 (large): 256 px, 12 x 12, 3072 px per side, 18 MB resident (8.0 MB at 8 x 8).
- **Observed** (the 267 base-game atlases, all resident, Release, RTX 4070): 68 at 256 px (1224 MB), 56 at 128 px (252 MB), 143 at 64 px
  (161 MB), 1637 MB in all, against 5669 MB for version 4. Only the atlases a view needs are resident (section 7).
- **Grid choice (Observed).** Mean difference to the meshes (whole image, forest at `--range-large 12000`): old impostors 3.80; new at 8 x
  8 2.94, 10 x 10 2.74, 12 x 12 2.56, 16 x 16 2.47. Magnify 2.0: 2.68, 1.4: 2.56, 1.0: 2.50. 12 and 1.4 were taken as the knee.
- `LevelsToSkip` drops top levels the transition distance never needs (an atlas is never sampled sharper than one texel per pixel).

**Compression (from the code).** BC1 colour endpoints are fitted along the principal axis of the block's covered pixels
(`ImpostorEncoder.EncodeBc1`, four-colour mode for fully opaque blocks, three-colour with the transparent code otherwise); the engine's DDS
encoder uses bounding-box endpoints and turns blocks of orange leaves and blue-grey twigs into two greys. An uncompressed RGBA8 option
exists in the format and the baker (tests).

## 3. The bake

**At load (`ImpostorBaker`, native, no `IGl`).**

1. Each row of N frames is rendered into a target of `2·N·F x 2·F` (RGBA8 + 32-bit float depth, "impostor bake target"), a viewport and
   an orthographic projection per frame fitted to the sphere. A row's passes, the halving blit and the copy into a readback buffer are
   recorded into the frame's pre-frame command list, and the row is read once that frame has completed (`ImpostorBakeJob.Step`, a few rows
   per frame, so a bake never stalls a frame; `Bake(..., nextFrame)` drives the frames for tools and tests).
2. The renderer is the shared mesh shader (`Shaders.MeshFragment`) with an output switch before its lighting
   (`ImpostorShaders.BakeFragment`), so albedo and normal are what the mesh shader would have lit, including the alpha test, second
   texture, normal map and triplanar mode.
3. Two passes per row (albedo, normal in the frame basis; gloss is read from a third pass's G channel for the mean only). Each writes
   alpha 1 on kept samples and 0 elsewhere. A GPU linear blit halves each pass (a 2 x 2 box filter) and only the half-size picture is
   read back.
4. **Texture LOD bias (the fix for "fuller and pinker"; Observed, see section 6).** The bake samples the mesh's textures with a bias
   `log2(2F / ScreenDiameter(radius x mean scale))`, clamped 0..4, where `mean scale = (|min| + |max|) / 2`. At the reference distance the
   mesh's leaf texture is read at the mip its size selects; its alpha mip thins the leaves and its colour mip averages leaf with twig.
   Without the bias the bake sampled mip 0 at twice the frame resolution and produced full, pale-pink crowns.
5. While the GPU renders row k + 1 the CPU filters row k (`ImpostorAssembler`, parallel per frame): un-premultiply, **pull-push fill** of
   empty texels from covered ones (so bilinear filtering and mips never pull black or a wrong normal into the silhouette), per-frame
   mips, and the cut-out: the albedo's coverage is scaled per level so the share of texels >= 0.5 equals the frame's covered area, and
   the alpha is stored as the binary `coverage x scale >= 0.5`.
6. BC1 / BC5 encoding (section 2).

**Exclusions (`ImpostorSource.Ineligible`):** FOLIAGE_MESH records drawn in TERRAIN mode (MaterialType 2) and EMISSIVE meshes (6): 142
records in the base game.

**Observed, base game, RTX 4070, Release, `--impostor-bake-all --rebake` (version 5, against version 4 in brackets):** 643 FOLIAGE_MESH
records, 230 atlases baked at version 5 in 25.8 s in all (267 in 407 s at version 4; the sets differ, version 5's sources also differ by
mean scale and the size class follows the distance). Disk cache 376 MB (1153 MB).

## 4. File format and cache

**`.mimp` (`ImpostorAtlas.Write` / `Read`).** Little-endian, in this order:

1. Header:

   | Field | Type |
   | --- | --- |
   | magic | `"MIMP"` (u32) |
   | format version | i32, 2 |
   | baker version | i32, 5 |
   | grid | i32 |
   | frame pixels | i32 |
   | levels | i32 |
   | centre | 3 x f32 |
   | radius | f32 |
   | mean gloss | f32 |
   | name | .NET length-prefixed UTF-8 string |
   | texture count | i32 (2) |

2. Per texture: map (i32: 0 albedo, 1 normal), encoding (i32: 0 RGBA8, 1 BC3, 2 BC5, 3 BC1), then each level's byte length (i32).
3. FNV-1a 64 of all level data.
4. All levels, map by map and largest first, in one zlib stream.

`Read` returns null on any mismatch (magic, either version, sizes, checksum, truncated stream): a damaged or old file is rebaked.

**Cache (`ImpostorCache`).** `%LOCALAPPDATA%\Meitou\impostors\<name>_<key>.mimp`, overridable with `MEITOU_IMPOSTOR_CACHE`; never in the
repo. The key is the first 24 hex digits of a SHA-256 over the baker and format versions, each source file's path, length and write time,
both materials' descriptions, the maximum **and mean** scale, the grid, magnify and bias. Files are written under a temporary name, then
moved. Bump `ImpostorAtlas.BakerVersion` when the baker's output changes.

**Size cap, LRU and stale files (`ImpostorCache.Maintain`, `ImpostorCache.cs`; since 2026-10-07).** A file's last use is its last write time
(`TryLoad` hits and `Save` set it). `Maintain` runs on a worker at the first foliage update and after every 16 saves, under a lock, and:

1. deletes files that are not atlases (shorter than the 12-byte header, wrong magic) and those of an **older** format or baker version than
   the build's (their keys can never match again; a newer version is left, since another build sharing the folder may read it);
2. deletes temporary files (`*.mimp.<pid>.tmp`) an hour old (a crashed writer's);
3. when the remaining `.mimp` files total more than the cap, deletes the least recently used until the total is under 90% of it.

Other files in the folder are not touched. The cap is `MaxBytes`: default 512 MB (`MEITOU_IMPOSTOR_CACHE_MB`, `--impostor-cache-mb`, 0 = no cap);
the 267 base-game atlases are 376 MB on disk (**Observed**), so the base game fits with room for mods and re-bakes. **Verified**
(`ImpostorTests`, a temporary folder, never the user's cache): eviction order and the 90% target, no cap, the stale rules above (older baker,
older format, junk, short file, old temporary go; current, newer, fresh temporary and foreign files stay), and that loads and saves set the last use.
**Unknown**: two viewers evicting at once (each deletion tolerates a failure; the worst case is a rebake).

## 5. Runtime sampling

**GLSL (`ImpostorShaders`).**

| Piece | Contents |
| --- | --- |
| `Functions` (any stage) | `impostorEncode`, `impostorDecode`, `impostorDecodeNormal`, `impostorBasis`, `impostorSelect(view, grid, out a, b, c, weights)`, `impostorFrameUv` |
| `FragmentFunctions` | `struct ImpostorSurface { vec3 albedo; float coverage; vec3 normal; float gloss; vec3 position; }`, `framePick()` and `impostorSample(albedoMap, normalMap, grid, centre, radius, origin, ray, cellA, cellB, cellC, weights, pick)` |
| `FragmentNative()` | The program on the foliage instance ABI (push constants `ImpostorPush`: sphere, grid, atlas indices, `Gloss` at offset 40) |

**Per instance (vertex stage).** The eye goes to object space and `impostorSelect` picks the grid triangle (three cells and barycentric
weights). The quad faces the eye, rolled by the camera's up axis, half-size `r·d / sqrt(d² - r²)`.

**Per pixel.**

1. **Virtual frame-plane projection.** The object-space ray through the pixel is intersected with each frame's plane through the centre,
   so the frames line up at the centre's depth whatever the view direction.
2. **Vote for the cut-out, pick for the colour (default) vs blend.** The cut-out is the three frames' coverages blended by their weights
   (`vote`, constant weights over the instance, so it is smooth in space), cut at 0.5. The colour comes from one frame, picked by `pick`
   in [0, 1) among the frames that cover the point (weight x coverage), so a texel is never shaded black; the normal and the position are
   the covering frames' blend (a curved bulb has different normals at the points the frames put under a pixel, and picking among them
   dithered the shading). The pick noise is `framePick()`, a transposed-weight interleaved-gradient noise (it replaced the 4 x 4 Bayer
   matrix, whose regular dot grid showed on large impostors seen steeply). Before 2026-10-07 the frame was picked first and its own
   coverage cut (see section 6, "Dotted fringes"). `pick < 0` blends the three colours by weight (leaves that do not line up thin out into
   a mush, so it is a debug option). **The shadow casters keep the plain pick** (`pick + 2` in `DepthFragment`): with the vote their coverage
   was solid over the whole crown and cast flat slabs with straight edges onto the canopy (**Observed**, the Hub view at 40000 units;
   gone with the plain pick, 2026-10-07).
3. **Texture gradients.** All fetches use frame A's `textureGrad` gradients (the pick makes them non-uniform control flow).
4. **Cut-out.** Cut at coverage 0.5 (the albedo's punch-through alpha, divided out of the colour); with a multisampled target an
   alpha-to-coverage ramp over `fwidth(coverage)` is used, as the foliage meshes do.
5. **Result.** Albedo, object-space normal, mean gloss and the position on the frame plane.

**Lighting contract.** The surface goes through the same lighting as the mesh (`kenshiLight`, `atmoApply`), with the normal transformed by
the instance matrix, so a live sun lights the impostor as it lights the mesh; only the albedo is baked.

**Programs (`ImpostorDraw`, `ImpostorProgram`).** `Plain` for the colour pass (early depth test, the quad's depth) and `Caster` for the
shadow cascades.

**Crossfade.** The mesh fades out with its usual dither (`M14 = fade`); the impostor gets `M14 = -fade` and discards the complement, so
over the band each pixel shows exactly one of the two.

**Transition distance.** An impostor shows one atlas texel per screen pixel when the instance's projected diameter equals the frame size:
`d = R_world · H / (tan(fov/2) · F)`. The foliage path uses one adjustable distance (section 7) instead, because a group keeps one range
and the cull's chunk carries one transition. The frame size of section 2 is chosen so that the default 4000 is about this distance for
the largest instance of each mesh.

## 6. Verification: `--impostor-preview`

`meitou-viewer --impostor-preview <FOLIAGE_MESH name, string id or .mesh file> [--out dir] [--size WxH] [--rebake] [--blend] [--debug n]
[--class medium|large]` renders offscreen (4x MSAA). `--impostor-bake-all [--rebake]` bakes or loads every eligible mesh and prints per-mesh
and total time and size. (`--parallax` and `--depth-write` were removed with the depth map.)

| File | What it shows |
| --- | --- |
| `<name>-atlas-{albedo,normal}.png` | Level 0 as stored (decoded from BC) |
| `<name>-sheet-{high,low,back}.png` | Mesh / impostor pairs at 1500 units under three suns; rows are camera elevations -8 to 85 degrees, columns four azimuths |
| `<name>-frames.png`, `-frames2.png` | Cameras exactly on frame directions (bake, layout and projection check) |
| `<name>-field-{mesh,impostor}.png` | 160 random instances 1500 to 4000 units away |
| `<name>-near-{mesh,impostor}.png` | 40 random instances 1500 to 2000 units away |
| `<name>-t4k-{mesh,impostor}.png` | 120 random instances 3500 to 4500 units away (the default switch distance) |

Compare pairs with `meitou-tools image-diff a b`.

**Observed (2026-10-07, RTX 4070, 1600 x 900; the pictures are in `C:\Temp\agent-B\shots`, not in the repo):**

- On frame directions the impostor's silhouette matches the mesh's (verifies the bake, the layout and the projection).
- Off-frame views keep the crown shape, trunk and shading direction under all three suns.
- **Colour, density.** Version 4's crowns were fuller and pinker than the meshes at the switch distance. With the bake's LOD bias (section 3)
  the world crops (`shots\f2_on.png` against `f_off.png`, `s_on.png` against `s_off.png`) match the meshes' colour and density closely;
  the pale pink blobs are gone. Whole-picture mean difference to the meshes in the forest at large range 12000: 3.80 (version 4) to 2.56.
- **Crossfade.** The view at 4200 units (`--at -37582,-80684 --yaw -70.5 --pitch 4 --distance 4200`, `shots\x_on.png` against `x_off.png`):
  mean difference 0.39, 1.3% of pixels over 12, max 163 (the maximum is a few leaf pixels and shadow edges); no visible seam, hole or
  popping where the band is.
- **Dotted fringes at the silhouette of trunk bulbs: cause and fix (2026-10-07).** Test mesh `Baobabesque Tree` (bulb trunks; 64 px frames,
  `--impostor-preview`, `near` pictures).
  *Cause* (**Verified** by the pictures below): the per-pixel frame pick. A bulb lies far in front of or behind the crown's centre, where the
  frame-plane projection lines the frames up; the frames (8 to 15 degrees apart) therefore put the bulb's edge a few pixels apart. Each pixel
  picked one frame and cut on that frame's own coverage, so at the edge and, where the picked frame had no coverage at that point, inside the
  bulb, pixels were discarded at random: a dotted edge and a dither of holes. It was not the BC1 1-bit alpha (a bilinear 0/1 alpha is a smooth
  ramp over one texel and is cut at 0.5), not the mip coverage scale, not the fill or dilation of colour into transparent texels (the fill is
  there, and the cut-out stays binary), and not the compression: the blended debug mode (`--blend`) with the same atlas has a clean edge.
  *Fix*: vote for the cut-out, pick for the colour (section 5, item 2). No change to the atlas, so `BakerVersion` stays 5, **VRAM is unchanged**
  (the format is the same BC1 + BC5; no BC3 / BC7 alpha was needed) and a cache from before is valid. The cost is two more albedo fetches
  and, for the normal, two more (the normal map is one level coarser and cheap) per impostor pixel.
  *Numbers* (**Observed**, mean difference of the impostor picture to the mesh picture, `--impostor-preview`, base 87c7857 / new): Baobabesque
  `t4k` 0.72 / 0.58, `field` 1.98 / 1.56, `near` 1.29 / 1.03 (over-12 share 1.82% / 1.59%); BushTree01 `t4k` 4.09 / 3.65, `field` 8.46 / 7.73,
  `near` 10.76 / 9.84. Crop: `C:\Temp\agent-B2\crop_baobab_base_new_mesh.png` (base impostor, new impostor, mesh). A cut of the vote at 0.4
  (coverage x 1.25) or 0.59 (x 0.85) instead of 0.5 was worse on three of the six numbers each; 0.5 stayed.
  *Whole pictures*: the Meitou ten views against meshes only (`--faithful impostors`) are about equal (mean 3.07 / 3.05 forest 13:00, 4.82 /
  5.20 Hub 13:00, 1.02 / 1.06 zone14_30): the dotted pixels are few against the crowns' leaf noise, and the Hub's crowns are a little smoother
  and fuller than the meshes' leaves. The improvement is the silhouettes (crop `C:\Temp\agent-B2\crop_hub_base_new_mesh.png`: base, new,
  meshes, Hub 13:00 at 40000 units: the base's speckled crown edges are clean).

**Gate pictures, ten views (2026-10-07, new build against the base build's `C:\Temp\base-87c7857\meitou`, warm cache):** `--faithful all` and
`--faithful impostors` are 0 px (max 0, **Verified**). The default Meitou views differ only where billboards are drawn (mean / share over 12 /
max): forest 13:00 0.87 / 2.7% / 133, 02:00 0.13 / 0.004% / 18; Hub 1.63 / 5.1% / 114, 0.24 / 0.008% / 21; Port North 0.018 / 0.08% / 144,
0.003 / 0.01% / 30; rock 0.001 / 0.002% / 120, 0.0003 / 0% / 14; zone14_30 0.32 / 0.74% / 72, 0.07 / 0% / 16. The differences are the crowns'
edges and the leaf-by-leaf choice of frame (the colour pick is the same noise, but the cut-out is no longer random); the meshes-only
pictures are the reference (about equal distance, see the fringe item above).

**Not represented (from the code):** triplanar materials are baked in object space; the bake uses the simple specular path; views below
the horizon are clamped to it; wind sway (the impostor is the rest pose).

## 7. In the foliage path (as built; native since phase 8 stage 2, format 2 and the budget since 2026-10-07)

Labels: **Verified** (a test or the parity gate checks it), **Observed** (seen or measured in a run, with the machine), **Unknown**.

Code: `FoliageRenderer.Impostors.cs` (streaming, budget and drawing), `FoliageCull.cs` / `FoliageShaders.cs` / `FoliageGpuCull.cs` (the
split), `Impostors/ImpostorDraw.cs`. Tests: `FoliageCullTests.Impostor_split_is_complementary`,
`FoliageGpuCullTests.Gpu_impostor_split_matches_FoliageCull` (Slow), `ImpostorTests`.

### Switch, distance, budget, options

- The `impostors` Enhancement (F7): Meitou (default) draws impostors; Faithful never loads, bakes or draws one. `--faithful impostors`
  turns it off, `--faithful all` includes it.
- `ImpostorDistance` (default 4000; `--impostor-distance <u>`; Tab slider "Impostor distance (F7 Meitou)", 500 to 40000): the transition
  distance T along the ground, for every atlas.
- `ImpostorBudgetMb` (`--impostor-budget <MB>`, env `MEITOU_IMPOSTOR_BUDGET_MB`): an explicit limit on the VRAM atlases may hold. Without it
  (the default since 2026-10-07; it was a fixed 192) the limit is a share of the card's budget, section 10.
- `MEITOU_IMPOSTOR_LOG=1` prints one line per atlas made Ready, evicted or refused. `MEITOU_IMPOSTOR_CASTERS=0` keeps the meshes as
  shadow casters.
- A group gets an impostor only when its mesh's atlas is Ready and T <= range - band. A mesh without a Ready atlas stays a mesh.
  **Meitou default ranges** are large 12000, medium 5000, small 800 (docs/viewer.md, "Foliage"), so large meshes are impostors from 3600
  to 12000 and medium ones from 3600 to 5000.

### Budget and eviction (superseded by section 10: kept for the numbers of the fixed 192 MB budget with LRU refusals)

- An atlas's resident size is known before it is made (`ImpostorTextures.BytesFor`). `UploadImpostor` checks the budget first. If it
  does not fit, `MakeImpostorRoom` evicts atlases that were not used for more than 1 s, least recently used first; if that is not enough
  the atlas is refused: its state goes to `None` with a retry in 8 s, and its meshes keep drawing as meshes.
- Uploads are limited to 6 MB per frame (64 MB while settling), through `GpuFrame.Staging`.
- **Observed** (RTX 4070, Meitou defaults, still camera, forest close view): 49 atlases, 119 MB (albedo 76.7 + normal 42.2). In a flight over
  the forest (300 frames, `--fly-benchmark`) 46 to 47 atlases, 200 to 206 MB at the end; the budget (192 MB) holds the refused and evicted
  ones back. The extra for all of it is under the 500 MB target by a factor of 2.5 to 4.

### The split (Verified)

The crossfade band is [T - B, T) with B = 0.1 T. With the transition fade m = clamp((T - d) / B, 0, 1) and the range fade w:

| Distance | Mesh list (row 0 w) | Impostor list (row 0 w) |
| --- | --- | --- |
| d < T - B (m = 1) | `Pack(w)` | not listed |
| T - B <= d < T (0 < m < 1) | m | -m (the complementary dither) |
| d >= T (m = 0) | not listed | `Pack(w)` |

- CPU: `FoliageCull.CullGroup(..., parts)` with `PackMesh` / `PackImpostor`; hidden is -2.
- GPU: a group with an impostor has its mesh chunks flagged `ImpostorMesh` (4) and a second set flagged `Impostor` (8), with `Transition`
  and `InverseTransitionBand` in the chunk's former padding. Batches are meshes, TERRAIN-mode rocks, then impostors (one
  `DrawIndexedIndirect` of the six-index quad, bindless atlas indices, `ImpostorPush`).
- **Verified**: the split test, `Gpu_impostor_split_matches_FoliageCull` (bit for bit), `MEITOU_GPU_CULL_VERIFY=1` in the forest view,
  and `MEITOU_GPU_CULL=0` / `MEITOU_RECORD_THREADS=0` give pictures 0 px from the default.

### Streaming (from the code)

- Once a second (every update while settling) zones whose far corner is beyond T - B ask for the atlases of their resident, in-range
  meshes. A worker loads the cache; a miss is baked on the render thread, rows per frame by a sample budget (all rows per update while
  settling; section 8), then saved on a worker. An atlas uploads one level per frame (all while settling).
- Offscreen settling (`FoliageRenderer.Settle`) ends the frame with `Gpu.Finish()` while a bake or an upload waits.
- An atlas unused for `IdleSeconds` (60) is unloaded, and by the budget sooner.

### Shadows

- In the cascades a group with an impostor casts the impostor beyond T (the same split, its own cull per cascade), drawn by the `Caster`
  program: one frame per texel by the pick (no fade), cut at coverage 0.5, depth on the frame plane through the crown's centre. The quad
  faces the sun (`uImpostorView` w 1). `MEITOU_IMPOSTOR_CASTERS=0` keeps mesh casters.
- **Observed** (forest close view, still camera, `--fly-benchmark 300`, RTX 4070, Meitou defaults, GPU timers of the shadow cascades,
  mean ms; the foliage-caster rows are the "fol meshes" pass inside the cascades):

  | Shadow range, casters | Cascades GPU total | Foliage casters |
  | --- | ---: | ---: |
  | 10000, impostor casters | 1.31 | 0.94 |
  | 10000, mesh casters (`CASTERS=0`) | 2.91 | 2.55 |
  | 15000, impostor casters | 1.98 | 1.34 |
  | 15000, mesh casters | 3.69 | 3.28 |
  | 15000, impostors off (`--faithful impostors`) | 3.58 | 3.17 |

  At 5000 (the game's range) there is no difference. Impostor casters cost about 40% (10000) and 55% (15000) of the mesh casters.

### Meitou pictures against the base Meitou pictures (Observed, ten views, `C:\Temp\base-6f4af19\meitou`)

| View | mean | over 12 | max |
| --- | ---: | ---: | ---: |
| forest 13:00 / 02:00 | 7.06 / 0.71 | 21.1% / 1.67% | 223 / 51 |
| hub 13:00 / 02:00 | 6.97 / 1.57 | 25.8% / 2.56% | 161 / 61 |
| portnorth 13:00 / 02:00 | 0.147 / 0.012 | 0.405% / 0.101% | 215 / 43 |
| rock 13:00 / 02:00 | 0.259 / 0.019 | 0.703% / 0.057% | 174 / 30 |
| zone14_30 13:00 / 02:00 | 3.53 / 0.43 | 13.3% / 0.905% | 104 / 25 |

These differ by design: the new ranges (12000 / 5000 / 800 draw far trees and bushes the base pictures lack), the longer shadow distance
(10000, more and longer distant shadows) and the billboards. With `--faithful impostors --range-large 5000 --range-medium 2500
--shadow-range 5000` (the baseline's settings) all ten views are 0 px (**Verified**), and `--faithful all` is 0 px (**Verified**).

### Frame times (Observed, 2026-10-07, RTX 4070, 1600 x 900, forest close view, `--fly-benchmark 300`, three interleaved runs; `nvidia-smi` idle (0-18%) before each)

Still camera (`--fly-speed 0`). Master is 410579b at its defaults (shadow 5000, ranges 5000 / 2500); "new" is the Meitou defaults; "off" is the
new defaults with `--faithful impostors` (so the new ranges and shadow 10000 with meshes). The GPU "frame" time is bimodal here (GPU clock
states) and not reliable; the per-pass rows are:

| | Master | New | New, impostors off |
| --- | ---: | ---: | ---: |
| CPU frame mean (ms) | 1.19, 1.03, 1.08 | 1.52, 1.34, 1.31 | 1.14, 1.22, 1.37 |
| Shadow cascades GPU (ms) | 1.35, 1.18, 1.82 | 1.65, 1.65, 1.67 | 3.05, 3.05, 3.04 |
| Foliage colour GPU (ms) | 0.93, 0.67, 1.25 | 0.74, 0.98, 0.98 | 1.33, 1.35, 1.32 |

So against its own meshes the billboards save about 1.4 ms of shadow and 0.4 ms of colour GPU time at the longer ranges, and cost about
0.2 ms of CPU (the second set of chunks, and the atlas loading). Against master at its short defaults, the new defaults draw far more
(12000 / 5000 / shadow 10000) for about the same GPU time and +0.2 ms CPU.

Flying (default `--fly-benchmark` circle, three runs each): CPU frame mean 2.72, 3.42, 3.88 ms for master and 4.33, 3.67, 3.91 for new
(upd-foliage 0.47 to 0.59 against 0.64 to 0.67: atlas loading, uploads and the second chunk set); shadows GPU 0.67 to 3.47 against 1.43
to 1.65. The flight numbers vary run to run as much as the difference, so the CPU cost in a flight is about +0.2 to 0.5 ms (**Observed**,
noisy).

### VRAM per owner (Observed, `vram` line of `--fly-benchmark`)

| Owner | Before (billboards-wip, default ranges) | After, still | After, flight |
| --- | ---: | ---: | ---: |
| impostor atlas albedo | 320 | 76.7 | 129.4 |
| impostor atlas normal | 88 | 42.2 | 71.1 |
| impostor atlas depth | 88 | 0 | 0 |
| Total impostor | about 495 (41 atlases) | 119 (49 atlases) | 200 (47 atlases) |

Whole-process device-local use at the end: 2928 MB without impostors at the new ranges, 3120 MB with (still), 3824 MB with (flight, which
also holds more foliage textures and meshes). Master's viewer has no impostors.

### Open

- A per-instance transition by projected size (needs a per-instance transition in the cull; the small class has a per-mesh one, section 11); a budget slider.
- Thin branches (1 pixel at the 64 px frame size) are thinned by the vote; at the transition distance they are sub-pixel anyway
  (**Unknown** whether a larger frame for such meshes is worth its VRAM).
- The cold-cache validation errors and the drawing program's compile at the first impostor: fixed, section 9.

## 8. Bake pacing and the first fast flight (2026-10-07)

**The hitch, measured** (**Observed**, RTX 4070, 1600 x 900, forest `--at -37582,-80684 --distance 1400 --pitch 10 --time 13
--fly-benchmark 300 --fly-speed 600`, an empty cache folder per run, `MEITOU_BENCH_SKIP=30` leaves the first 30 frames out of the
percentiles: the process's first frames cost 60-80 ms of GPU wait with or without impostors). The machine is shared with other agents'
viewers, so single runs vary by tens of milliseconds of GPU wait; all four interleaved runs, base 87c7857 (with only the benchmark skip
option added) / this build:

| Run | p95 ms | p99 ms | max ms | gen2 GCs | GC pause ms | allocated MB (render thread) |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 17.3 / 14.9 | 29.4 / 20.4 | 35.3 / 37.0 | 7 / 1 | 68 / 5 | 8408 (1705) / 2200 (19) |
| 2 | 15.1 / 12.5 | 21.3 / 17.2 | 25.6 / 20.7 | 8 / 2 | 37 / 9 | 9492 (1974) / 2378 (19) |
| 3 | 15.2 / 13.4 | 18.6 / 15.9 | 20.6 / 19.7 | 7 / 2 | 35 / 4 | 9156 (1953) / 2700 (21) |
| 4 | 16.6 / 14.1 | 19.9 / 18.1 | 25.5 / 26.1 | 7 / 2 | 146 / 7 | 9129 (1900) / 2726 (21) |

(Earlier sets of three on the way, with a quieter or louder machine: base max 28-111, new max 15-59; the new build's p99 was 14-24 against
base 19-60.) For scale, impostors off (`--faithful impostors`, no bakes at all), three runs: p99 19 / 24 / 29, max 37 / 50 / 62; and warm caches:
base p99 20 / 15, new 22 / 23, max 42-68. So what is left in a cold flight is the viewer's own outliers (water, terrain, reflection, GC of
the 2.4 GB managed heap), not the bakes.

**Cause** (**Verified** by the allocation counters, `GC.GetAllocatedBytesForCurrentThread` around the bake step, and the table): not the GPU
work. Per bake the render thread (1) copied each row's three pictures out of the mapped readback buffer into new arrays (`ToArray`, 9 MB a
row for a large atlas), (2) allocated the assembler's atlas levels, about 100 MB of zeroed large arrays, and the readback buffers and
(3) the filtering workers allocated about 6 MB of float arrays per frame (900 MB per large atlas), all on the large object heap. That was 8-9 GB
allocated in a cold flight (1.7-2.0 GB on the render thread), 5-8 gen2 collections and pauses up to 146 ms. In addition the first bake
compiled its shaders and pipelines on the render thread (a 60 ms frame).

**Fixes** (`ImpostorAssembler`, `ImpostorBaker`/`ImpostorBakeJob`, `FoliageRenderer.Impostors.cs`):

- the workers read the readback buffer's mapping in place (`AddRow(row, nint, nint, nint)`); buffers are pooled in the baker and freed
  10 s after the last bake;
- the assembler's per-frame arrays come from a shared pool, its atlas levels are not cleared (every texel is written) and are reused by the
  next bake of the same size (a pool of two); `ImpostorAssembler.ReleasePool` frees them with the readback buffers after 10 s idle;
- the first foliage update with impostors on constructs the baker (its programs compile with the loading);
- **a bake step records rows by a sample budget** (`MEITOU_IMPOSTOR_BAKE_MSAMPLES`, default 40 million shaded samples a frame; a row is
  `grid x 3 x (2F)^2`): 256 px atlases 4 rows a frame (3 frames), 128 and 64 px atlases all 12 rows in one frame (it was 2 rows a frame
  whatever the size: 6 frames for any atlas). The GPU cost of a bake is small: in cold still-camera runs a budget of 100 million (a whole
  large atlas in one frame) left the frame times at 5-6 ms p50 and no spike above 15 ms that the other budgets did not also have
  (**Observed**; the flight runs could not separate budgets 10 and 100 from the shared machine's noise, so 40 is a middle value, not a measured optimum);
- **order**: the waiting bakes run nearest first, by the distance of the nearest zone that wants the atlas from the eye now or from where its
  motion puts it in 3 s (`velocity x ImpostorLookaheadSeconds`; the zone layouts already look 1.5 s ahead). A mesh without an atlas keeps
  drawing as a mesh until the atlas is Ready (unchanged, the fallback).

**Verified**: the atlases are byte-identical to the previous build's for all 267 base-game meshes (`--impostor-bake-all`, md5 of every `.mimp`
against a folder made by the previous baker, with the pooled assembler too), which is why `BakerVersion` stays 5; `--impostor-bake-all` takes
26 and 30 s with the new baker (43.6 s with the old one in the same session, on a shared machine; 25.8 s in section 3's earlier measurement), and `ImpostorTests` has a test that the
assembler's output does not depend on the pooled memory it reuses.

**Not done**: pre-baking atlases of zones beyond the foliage layout's reach (the layout itself is the limit), a bake on several workers
at once, moving the bake's mesh upload (1-8 ms) off the render thread.

## 9. Cold-cache validation errors and the drawing warm-up (2026-10-07)

**The 6 sync-validation errors (`MEITOU_VK_VALIDATION=sync`, empty cache, offscreen `--screenshot`).** **Verified** (a stack trace taken where the copy
was recorded, then 0 errors on the forest view and the Hub, both with an empty cache folder, and in all ten gate views): not the bake's own
resources and not the atlas upload. `ImpostorBaker.RecordRow` calls `NativeFrame.Bind` for every column of a row, inside the row's
`BeginRendering`; `Prepare` evaluates the frame globals, and the shadow pass's `uShadowNoise` getter uploads the noise texture on its first
read (`ShadowPass.UploadNoise`: a `vkCmdCopyBufferToImage` and a transfer barrier into `GpuFrame.PreFrame`). In a run that settles before it draws a
world frame (offscreen, a cold cache: the bake is the first thing to read the globals) that first read happened inside the bake's rendering: one copy
plus five barrier complaints (stages, accesses and "in a dynamic rendering instance") = 6. A warm cache has no bake, so the first read was in a normal
`Prepare`. **Fix**: `ImpostorBaker.PrepareFrameGlobals` calls `frame.Prepare` once before the first row's pass (a throw-away binding), so the lazy
upload is recorded outside the rendering. The bake's commands and the atlas bytes are unchanged (md5 of all 55 forest-view `.mimp` files from a cold
bake identical before and after, **Verified**). The same hazard exists for any other lazy global read for the first time inside a pass; none other was seen.

**The drawing warm-up.** Before, `ImpostorDraw` (two programs, the quad's upload) was made, and the pipeline of each (state, formats) combination
compiled, at the first impostor drawn. **Observed** (RTX 4070, forest view, warm cache, a timer around the setup in `AddImpostorDraws`): 7.5 ms at the
first caster segment (programs and quad) and 0.65 ms for the colour pipeline. The first foliage update is outside any pass with a frame open (the
bake records there too), so the quad upload (`Uploads.Begin`, into `PreFrame`) is valid there. Now:

- `ImpostorDraw` is made in the first foliage update with the baker (`UpdateImpostors`): programs and quad at load, not at the first impostor.
- Every foliage pass that draws no impostor yet asks for the pipeline of its current state and formats (`WarmImpostorPipeline`: the host's
  `DrawState` with the foliage's alpha to coverage, depth or colour program), so a combination is compiled in a pass without impostors. Pipelines are
  cached by key as before.
- **Observed**: with the first twelve foliage passes held back from drawing impostors (a temporary test hook, removed), no impostor setup in a later
  pass took 0.2 ms or more; before, the first took 7.5 ms. **Verified**: 0 validation errors, ten views in both modes max 0 (below).
- **Limit (Observed)**: the pass formats are known only inside a pass, so in a run whose first drawn frame already has impostors (offscreen after the
  settle, the game's first frame after its settle) that frame still compiles the pipelines, now 4.1 to 4.6 ms (caster) plus 0.4 ms (colour) instead of
  7.5 + 0.65 ms: the programs and the quad are gone from it. **Unknown**: whether warming from the passes' known formats at the first update
  (reflection, scene, cascades) is worth the coupling to the world frame.

**Gate (Release, RTX 4070).** `--faithful all`, ten views, against `C:\Temp\base-87c7857\faithful`: max 0. Meitou defaults, ten views with an empty
impostor cache folder (so every view bakes), against `C:\Temp\mp\meitou` (master cd65d05): max 0. Sync validation on all ten Meitou views and the Hub
with a cold cache: 0 errors (**Verified**).

## 10. Residency: the budget that follows the card, far mips and the plan (2026-10-07)

*In short: the fixed 192 MB with LRU refusal is gone. The default limit is a share of the card's video-memory budget; each atlas holds only the mip
levels its nearest instance can sample, and is refined (reloaded from the disk cache and swapped in) when an instance approaches; a plan, redone once
a second, decides nearest first which atlases fit and, when they do not, degrades the farthest by a level at a time before it leaves any out. At
the 1 px preset every tree and bush stays an impostor (Hub 46.5 to 23.7 ms, 22.8 ms of it by this alone), the 3 400 refuse-and-reload cycles are
gone, and the default pictures are unchanged to the pixel.* Code: `ImpostorBudget.cs`, `FoliageRenderer.Impostors.cs` (`PlanImpostors`,
`ApplyImpostorPlan`, `StepImpostorRefines`). Tests: `ImpostorBudgetTests`.

**What was wrong** (**Observed**, master `0a3614a`, RTX 4070, CPX preset of [render-distance-benchmark.md](render-distance-benchmark.md)): every wanted
atlas was re-stamped as in use at each scan, so none was ever "unused for a second": `MakeImpostorRoom` could not evict anything, refused the new
atlas (its mesh stayed a mesh: Hub 46.4 ms, 47.9 M mesh triangles a frame), and the refused one asked again after 8 s, which loaded its atlas from the cache
only to refuse it again: 3 437-3 593 loads in 300 frames at the Hub for 112 resident atlases, 2 246-2 265 in the forest for 84.

**The limit** (`ImpostorBudget`, **Verified** by `ImpostorBudgetTests`). `B` is the device-local budget the driver reports (`VulkanDevice.VideoMemory()`,
VK_EXT_memory_budget, the figure `VramGuard` works from, so atlases and everything else give way together; before the guard's first sample the same call).

- default = `clamp(share x B, 48 MB, cap)`, never more than 12% of `B`; share 8% and cap 1024 MB on a discrete GPU, 5% and 256 MB on an integrated one
  (`Properties.DeviceType`); 192 MB (the old fixed value) while `B` is unknown;
- x0.75 while the guard is under pressure (the pattern of `WorldTextureCache.EffectiveMark`);
- `--impostor-budget <MB>` / `MEITOU_IMPOSTOR_BUDGET_MB` replace the default (the pressure factor still applies).

| Budget `B` | 2 GB | 4 GB | 8 GB | 11.2 GB (the RTX 4070's 11 453 MB: 916 MB) | 12 GB | 16 GB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| discrete GPU | 164 MB | 328 MB | 655 MB | 916 MB | 983 MB | 1024 MB (cap; 8% = 1311) |
| integrated GPU | 102 MB | 205 MB | 256 MB (cap; 5% = 410) | 256 MB | 256 MB | 256 MB |
| under pressure (x0.75), discrete | 123 MB | 246 MB | 492 MB | 687 MB | 737 MB | 768 MB |

The 8% is the object caches' pattern (textures 17.9%, meshes 6.7% of the card they were tuned on). The cap is the Hub and forest CPX working set with far mips
(413-494 MB as the allocator counts it, 364-450 MB by the plan: section below) with room to spare; The floor never takes more than 12% of a small budget (a 256 MB budget gets 31 MB). **Integrated GPUs share the system's memory**:
the device-local heap is system RAM (Intel) or a small carve-out (some AMD APUs, whose other heap is not device-local, where `DeviceLocal` allocations
cannot go), and `B` is what all of the process's allocations may take, so the share and the cap are smaller there, and the atlases degrade by mips before they
ever take more. **Unknown** (no integrated GPU to run on): the heap sizes such drivers report and whether 5% is right for them; `MEITOU_FORCE_INTEGRATED=1` with
`MEITOU_VRAM_BUDGET_MB` shows the integrated numbers on this card (below).

**Far mips** (`SkipFor`, `PlanImpostors`). The albedo keeps the levels whose frames are at least as big as the instance's sphere is on the screen at the
nearest distance it is drawn as an impostor (`LevelsToSkip` before, which used the transition distance for every atlas); the normal map one level fewer.
The nearest distance is the nearest ground of any zone that wants the atlas (the scan already computed it for the bake order), less the eye's motion over 3 s,
never inside the transition. One level is `4x` fewer bytes: a 256 px atlas is 9.0 MB at skip 0 and 2.2 MB at skip 1.
- **Margin** (**Verified**, gate below). The mip is chosen for the distance divided by `2^margin` (`MEITOU_IMPOSTOR_MIP_MARGIN`, 1 level; plus the upscaler's
  negative texture bias): a ray off the screen's axis crosses a frame plane at a larger angle, so the sampler's lod at the edge is up to half a level finer than
  the centre's, and the transition's own level is what the base build clamps to. With no margin the Meitou view `zone14_30` 13:00 differed from the base in a few
  horizon pixels (max 17; the 02:00 render max 3); half a level gave max 2; one level gave 0 px.
- **Refine and coarsen.** An atlas whose wanted level is finer than the resident one is reloaded from the cache on a worker (`ImpostorCache.TryLoad`, no CPU
  copy is kept), its new textures made and uploaded within the frame's 6 MB (64 MB settling) and swapped in; the old ones are drawn until then and freed after the
  frames in flight (`Bindless.Free`/deferred delete). Three run at once, finer ones first, nearest first, started as slots free up. A coarser one is made only when
  the plan is over the limit, or by two or more levels after ten seconds of holding more than needed. Under the guard's pressure nothing finer is started.
  `MEITOU_IMPOSTOR_FARMIP=0` gives the old single level per atlas (no refine, no coarsen).

**The plan** (once a second, every update while settling). `impostorWanted` (the atlases some zone wants as impostors, nearest ground each) is sorted by that
distance (a resident atlas counts as 20% nearer: a newcomer must be that much nearer to take its place); each gets its mip, the bytes are summed, and when the sum is
over the limit the farthest atlases take one more level off, a pass at a time up to 3 more levels (a blurrier far crown costs no frame time, a mesh does), and only then
are the farthest left out one by one (`impostorDropped`, an atlas left out for two scans, or at once under pressure, is unloaded and its meshes draw). Unwanted atlases are evicted
first (least recently used) when the real use passes the limit. A mesh the bounds already rule out is never loaded; one that is wanted and admitted is loaded
nearest first. Nothing is refused and retried on a timer any more; an atlas the upload still cannot fit (a refine in flight, the guard) retries after 2 s.
A first greedy plan (nearest first, each atlas degraded alone, then left out) was measured and dropped: at 128 MB Hub CPX took 59 ms
(master 46 ms); the plan above runs 20.6 ms there.

**Measured** (**Observed**, RTX 4070, 1600 x 900, CPX preset, `--fly-pipelined`, GPU frame p50, three interleaved runs master / new; branch with the small class of section 11,
which adds the rest; the F1 share is the run with `MEITOU_IMPOSTOR_SMALL=0`):

| View | master | F1 only | F1 + small class | impostor VRAM, master / new (allocator) | atlas loads in 300 frames, master / new (refines) |
| --- | --- | --- | --- | --- | --- |
| Hub, CPX | 46.6, 46.4, 46.4 | 23.7 | 20.6, 20.7, 20.6 | 219 / 413 MB | 3 437-3 593 / 311 (100) |
| forest, CPX | 21.6, 21.4, 21.4 | 19.6 | 15.2, 15.1, 15.2 | 212 / 494 MB | 2 246-2 265 / 293 (154) |
| Stack, CPX (one pair) | 45.6 | | 15.7 | | 324 atlases, 452 MB |

Far mips alone, Hub CPX with the small class off: 874 MB with `MEITOU_IMPOSTOR_FARMIP=0`, about half that with them. At the defaults (11.4 GB card) nothing
measurable changes: forest flight pipelined 2.6 against 2.5-2.6 ms, paced 6.7, 6.8, 6.7 against 6.8, 6.7, 7.0, render thread 2.2 against 2.3;
Hub 2.7-2.8 against 2.7. The default forest flight now holds the atlases master refused or evicted: 66 atlases 204 MB against 117 atlases 367 MB
(whole process 3 461 against 3 589 MB; includes up to 20 s of atlases no longer wanted).

**Small budgets** (default forest flight, paced, 300 frames; `MEITOU_VRAM_BUDGET_MB` forces the card's budget; **Observed**):

| Forced budget | limit in force | master | new |
| --- | --- | --- | --- |
| 2 048 MB (watchdog raised to 0.995: both builds abort at the default 0.95 at startup) | | guard ranges x0.15, no atlases | the same (the guard has cut every range) |
| 3 584 MB | 215 MB (pressure) | guard x0.60 | guard x0.60, 74 atlases |
| 4 096 MB | 328 MB | whole process peak 84% of the budget | plan fits all wanted; peak 88% (closer to the guard's 90%) |
| 4 096 MB, `MEITOU_FORCE_INTEGRATED=1` | 205 MB | | 73 admitted, 10 of them coarser than their nearest instance wants |

And the 1 px preset under an explicit limit (`--impostor-budget`), Hub CPX: 128 MB 20.6 ms (254 atlases a level or more coarser than needed), 256 MB 20.6 ms
(162 coarser), 916 MB the same 20.6 (none), against 46.4 ms with master's 192 MB. At 8 GB (655 MB) Hub CPX: master 46.3, new 20.6 ms. **Unknown**: how the far
crowns look at 128 MB (levels beyond what the instance's size needs; no picture was looked at), and the pop-in on a card whose limit is below the working set
(the `pop-in    impostors` line counts it: atlases left out, admitted but not resident, coarser than wanted with a refine on its way, refines in flight; the meshes draw meanwhile, as before; the "coarser than wanted" count is high during fast flights).

**Gate** (**Verified**, Release, RTX 4070, ten views at 1600 x 900): Meitou defaults against master's ten views (`C:\Temp\mr\meitou`): 0 px in all ten with
the far mips and budget alone (small class off; with it, section 11); `--faithful impostors` against master rendered with the same option 0 px; `--faithful all` against `C:\Temp\base-87c7857\faithful` 0 px.
`MEITOU_VK_VALIDATION=sync`: forest and Hub with an empty cache 0 errors; a flight with `--range-large 48000` that makes refines 0 errors.

## 11. The small impostor class (2026-10-07)

*In short: meshes under radius 48 with enough triangles get impostors too, with 32 px frames and a transition in proportion to their size. The 218 such
meshes of the base game draw as meshes today; 139 get an atlas (0.28 MB each, 39 MB for all). Hub CPX 23.7 to 20.6 ms and forest CPX 19.6 to 15.2 ms on top of
section 10; medium range 80 000 in the forest 6.6 to 4.0 ms; the default pictures differ only in a dozen small spots of one view.*

**Survey** (**Observed**, `--impostor-bake-all` with `MEITOU_IMPOSTOR_SURVEY=1`, base game): 218 meshes with a radius under 48, of which 139 qualify (radius and triangle floors below), 39 MB at full detail, about 0.3 MB each. The heavy ones at the Hub CPX
were `CacTreeTu_03` (1 892 triangles x 1 459 instances) and `TechRustyJunk_*`; before this, "no impostor class" (the old
`State()` label said radius under 48; the mesh's own radius, a half diagonal, can still differ from the baker's farthest vertex by a few percent, so the class is estimated from
the bounds times 1.02 and the load decides).

**Design** (`ImpostorClass`, `ImpostorClass.For(radius, triangles)`):
- **Which meshes**: radius at least 2 (`MEITOU_IMPOSTOR_SMALL_MIN_RADIUS`) and at least 100 triangles (`MEITOU_IMPOSTOR_SMALL_MIN_TRIANGLES`: fewer cost less than the
  quad). TERRAIN and EMISSIVE meshes stay ineligible as before. `MEITOU_IMPOSTOR_SMALL=0` switches the class off.
- **Frame and grid**: 32 px, 12 x 12 (`MEITOU_IMPOSTOR_SMALL_FRAME`, `_GRID`): 4 levels, an atlas 384 px a side, 0.28 MB. Grid 8 (0.1 MB) was measured and is worse where it counts
  (mean difference to the mesh, `--impostor-preview`, field pictures: 0.34-0.52 at grid 8 against 0.30-0.42 at 12) for 0.2 MB an atlas, so "fewer views" is not taken.
- **Transition** (`ImpostorClass.Transition`): `T = ImpostorDistance x radius / 48`, per mesh (the cull already carries one per group): the largest instance is the
  same 27.8 px across at its transition whatever its radius (at 1080 lines), as the smallest medium mesh (radius 48) is at 4000, so a 32 px frame is magnified 0.87 there: sharper than the 1.4 the
  larger classes allow. Radius 2: 167 units; 10: 833; 20: 1 667; 47.9: 3 992. The bake's texture lod bias uses
  the class's own distance (`BakeDistance`, was always 4000), the crossfade band is a tenth of `T` as before.
- **Atlases** are made like any (cache `.mimp`, same baker version: medium and large atlases are byte-identical, so existing caches stay valid; small ones are new files, 19 MB more on disk), planned like any (section 10); `--impostor-preview <mesh> --class small` previews one at
  its own transition (the pictures are scaled by `T / 4000`).

**Look** (**Observed**): `--impostor-preview` mesh against impostor at the transition, whole-picture mean difference
(the objects are a few hundred pixels of a 1600 x 900 picture, so the numbers are small; a crop of one showed silhouette and shading alike):
0.13-0.21 for CacTreeTu_03, Cactus_Type01, Human_Skeleton_Part06 and TechRustyJunk_04; the same pictures at 1500-4000 x `T / 4000`
(`field`) 0.30-0.42.

**Gate** (**Verified**): Meitou defaults, ten views against master's: nine views 0 px; forest 13:00 differs in a dozen small spots (mean 0.0125, 0.033% over 12, max 153) and 02:00 (0.0021, 0.003%, max 20),
which are the small impostors (`MEITOU_IMPOSTOR_SMALL=0`: 0 px in both); `--faithful impostors` and `--faithful all` 0 px as in section 10.

**Measured** (**Observed**, CPX, pipelined GPU frame p50): Hub 23.7 (section 10 alone) to 20.6 ms, colour mesh triangles 5.47 M to 22 k (47.9 M on master), 49 of the 311 atlases small, 2.1 MB; forest 19.6 to 15.2 ms (11.7 M on master
to 0.3 M mesh triangles); medium range 80 000 alone (other options default) 6.6, 6.7, 6.6 to 4.1, 4.0, 3.9 ms. Render thread (paced, cpu-only p50, three runs) forest CPX 8.3, 8.3, 8.2 to 8.6, 9.3, 8.6 ms (+0.3 to +1.0: `upd-foliage`, the reflection cull, more impostor sets); Hub CPX 7.7 to 7.0. At the defaults nothing measurable (section 10).
