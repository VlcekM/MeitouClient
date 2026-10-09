# Terrain rendering and streaming

Part of the world view of `meitou-viewer` (and the game, which shares `src/Meitou.Rendering`); options, keys and the rest of the world mode are in [viewer.md](viewer.md#world-mode).

- **Terrain** (`TerrainQuadtree`, `TerrainLod`, `TerrainRenderer`): CDLOD out to the horizon with a screen-space error measured in rendered pixels (the viewer's own rule, not the game's; same in Faithful and Meitou), as described in
  [formats/terrain.md](formats/terrain.md#terrain-lod). The whole map (every 8th sample) and a `HeightWindow` of the
  finest samples around the eye are height textures (the window follows the camera, see Streaming); one 64 × 64 grid patch is drawn per quadtree node, with the odd vertices
  morphing onto the coarser level, so there are no cracks or pops. Nodes outside the frustum are skipped.
  Two depth slices (far 20000 to `--view-distance`, then near), with the near plane following the eye's height.
  Beyond `--material-distance` the terrain shows the biome ground colour × the colour map.
- Frame times (2026-10-04, offscreen 1280 × 960, average of 10 frames after loading): Shark with water 1.6 ms,
  Port North coast 1.6 ms, The Hub looking at the horizon 1.1 to 1.2 ms, whole world from above 0.6 ms
  (12 ms at 1400 × 1400 with `--material-distance 1e7`, which puts the full material everywhere).
  With streaming (same machine and method, 2026-10-04): Port North 1.2 to 1.3 ms (1.4 before); the same view after
  `--camera-at -2304,62208` 2.9 to 4.2 ms with the material all over the screen (0.5 to 1.1 ms with `--no-stream`,
  where the far terrain is only ground colour); sea past the map edge 1.5 to 3.4 ms; whole world from above 1.8 ms and
  2.6 ms at 1400 × 1400 with `--material-distance 1e7` (the material now reaches as far as the maps' window, 36864
  units around the eye). Flight with `--fly-to` (Port North to -2304,62208 at 8400 units per second, 60 frames per second of wall time,
  1364 frames, 191000 units, no objects), five runs: median 1.9 to 7.1 ms, 99th percentile 6.7 to 25 ms (`--no-stream`:
  3.2 and 16.0 ms; the runs differ a lot with what else the machine does, and the streamed view shades far more
  material). The first frame costs 200 to 250 ms in every run (shader compile, as with `--no-stream`). After it the worst
  frame was 9 to 35 ms in four runs and 407 ms in one (no streaming step was that long; a loaded machine).
  Before the maps were rewritten in place, every window move cost a frame of about 50 ms.
- **Texturing** (`TerrainTextures`, `TerrainShaders`): the biomes of every `blendinfo.dat` cell within the material
  distance of the eye are loaded into two texture arrays (one layer per distinct diffuse/normal pair) **compressed on the GPU as stored**: the diffuse array is BC3 (colour, gloss in alpha), the normal array BC1 (the shader reads only their RGB). A square power-of-two BC1/BC3 DDS at least `--layer-size` wide with its mips is uploaded block for block from the level of that size down to 1x1 (no decode, no resample); BC1 diffuse maps get an opaque alpha block and BC3 normal maps lose their alpha (`BlockCompression.Bc1ToBc3` / `Bc3ToBc1`, tests in `BlockCompressionTests`). Anything else (smaller or non-square files, no mip chain, PNG/TGA, a missing file as a constant colour) is decoded, scaled to size², given a box-filtered mip chain and encoded again with a small bounding-box BC1/BC3 encoder (visibly softer than a stored texture, but the base game has only three such files). The default layer size is **2048**, the game's own: 133 pairs take 1064 MB (66 MB at 512, 266 MB at 1024; the old RGBA8 arrays were 4 to 8 times as large: 1024 would have been 2.1 GB, 2048 8.4 GB), 1.5 GB at the 192-slot limit. Loading the 19 biomes around the rock view takes about 5.5 s at 2048 (disk and block conversion on worker threads) against 1.4-1.8 s at 512 or 1024. `--layer-size 512` / `1024` still work (they take a lower mip level of the stored file); per-biome constants go into a float texture with one row per biome; the blend
  map is sampled for the five slot weights; windows of the overlay and colour maps follow the eye (see Streaming). The layer model, slope scaling
  (FCS value × 0.01, **Verified** from the exe) and the overlay/colour channels follow terrain.md.
- **Streaming** (`TerrainStreamer`, `TerrainTextures`, `UploadQueue`): detail follows the camera, nothing is tied to
  the start point. All decoding is on worker threads; every GL call stays on the render thread, in steps of about 2 MB
  that run until 2 ms of a frame is spent, so no frame waits for a whole upload.
  - *Fine heights*: when the eye is further than 15% of the window's width from its centre, a worker re-reads a
    `HeightWindow` (1536 cells, 27648 units, at the start window's step) centred on it from a second handle on
    `fullmap.tif`, measures it against the quadtree's min/max cells, and the render thread uploads it in slabs into a new
    texture and swaps. The old window is used until then; in the interior both hold the same samples (origins are
    multiples of 64 samples), so nothing moves there. The blend band at the window's border (about 2800 units wide,
    coarse to fine) does move with every swap, so terrain 7000 to 14000 units ahead of the eye can change a little. The bounds only ever widen. Not done with `--step` 8 or more (as coarse
    as the whole-world grid).
  - *Biome layers*: each frame, the cells within the material distance + half a cell of the eye give the biomes needed,
    nearest first. Their texture pairs decode (at most 2 to 6 at a time) and are installed in free slots of the arrays
    (133 pairs in the base game, all fit; the limit is 192 slots, then the least recently needed unused pair is
    evicted). A biome is resident when all its pairs are. The cell table says per biome slot: the biome's row,
    "loading" (254) or unused (255); a slot still loading shows its share in the ground colour, so material fades in
    biome by biome, never as flat or wrong colour.
  - *Overlay and colour maps*: windows of 2048² overlay pixels (36 units) and 4096² colour pixels (18 units), the same
    73728-unit square, in two textures addressed toroidally (texel = world pixel mod window). Moving the window by
    4608 units or more rewrites only the strips that came into view, in place, with mips up to level 6 cut on the
    worker (the origin is aligned to 64 overlay pixels). Decoded tiles are cached (24 overlay, 12 colour). A fresh
    texture per window stalled the GPU for about 50 ms at its first use (Observed on this machine: a re-centre frame of 50 ms, against
    under 16 ms in place), which is why the strips are rewritten in place. The textured shading fades out over the last
    3000 units of the window, far beyond the material distance.
  - Screenshots wait for everything the camera view needs (`TerrainStreamer.Settle`); interactively the far ground
    colour shows first and the material appears around the camera within a second or two (the F11 statistics show `loading N`).
- **Streaming cost and unloading** (`BackgroundWork`, `UploadQueue`, `WorldTextureCache`, `ObjectMeshCache`, `FoliageRenderer`; **Observed**
  with `--fly-benchmark`, 2026-10-04, 1280 × 720, RTX 4070, `--world --town "The Hub" --radius 1.5 --distance 3000 --pitch 20`, 1500 frames at
  150 units per frame round a circle of 12000 units; the machine was shared with the game and other viewers, so run-to-run spread is large):
  - *What ran on the render thread* (found with the benchmark's stage clock, `StageClock`, laps in `WorldApp.Draw`; the worst frames list
    the stages that took 1 ms or more): foliage `Accept` grouping thousands of instances (15 to 20 ms), the foliage texture pump, which had no
    time budget (one texture's slabs, 10 to 35 ms), grass-page and foliage-mesh uploads in one `BufferData` each (10 to 20 ms), the object
    streamer making a big zone's instances in one frame (up to 30 ms), and, on a loaded machine, worker threads at normal priority taking time
    slices from the render thread. Fixed by: grouping on the zone's worker (`PreparedZone`); `WorldTextureCache.Pump` always bounded (default 1.5 ms)
    and slabs of 512 KB; foliage meshes and grass pages uploaded in 512 KB slabs (a page dropped meanwhile deletes what its steps made);
    `ObjectStreamer.FillZones` makes instances 1 ms a frame; every decode and layout job runs on `BackgroundWork`, a few dedicated
    below-normal-priority threads (`ProcessorCount - 3`); a replaced terrain height texture is deleted four frames after the swap.
  - *Compressed textures*: BC1, BC2, BC3, BC4 and BC5 DDS textures with a full mip chain are uploaded as stored (`CompressedTexImage2D`, S3TC; BC4/BC5 as RGTC (core since GL 3.0), BC4 with a swizzle so it still reads as grey with alpha 1 like the decoder's output; **Unverified on a real file**: the base game has no BC4/BC5, the path is only reviewed and compiles); in slabs
    of whole block rows) instead of decoded to RGBA8: 4 to 8 times less GPU memory and no CPU decode of the mips (only the level the swizzle
    test reads). Other formats, and files without a full chain (the viewer generates mips for those), take the RGBA8 path. The picture does not
    change visibly: two runs of The Hub differ by a mean of 0.05 per channel value (max 38 on a few edge pixels, run-to-run noise) and compressed
    against RGBA8 by 0.065. `MEITOU_UNCOMPRESSED_TEXTURES=1` brings the old path back for comparison.
  - *Unloading*: nothing is unloaded while within a draw range, whatever the camera looks at; once a second the objects mark instances within
    their range (`WorldObjectRenderer.MarkInRange`: the mesh as used, the material's textures read) and the foliage the groups of zones
    within their layer range (`FoliageRenderer.TrimResident`). Then a GPU mesh not marked for `IdleSeconds` (60) is deleted (buffers, vertex
    arrays; the instances that had resolved to it go back to waiting, `ObjectStreamer.Scan` asks for it when they come in range again), and a
    `WorldTexture` whose id nobody read for 60 s is deleted (`WorldTextureCache.Trim`; reading `WorldTexture.Id` is what counts as use and
    also starts the reload of an unloaded texture, so materials never need rebuilding). Hysteresis is that idle time. Memory pressure:
    above 768 MB of object meshes or 1024 MB of textures (per cache) the least recently used ones idle for 8 s go too, down to three quarters
    of the mark. At most 24 meshes and 40 textures are deleted per second. Grass pages and terrain biome layers already had their own eviction.
    `MEITOU_UNLOAD_IDLE=<seconds>` changes the idle time (a huge value turns unloading off, for comparison).
  - *Numbers*: resident GPU memory of meshes, textures and grass (the F11 statistics show `resident` MB; `Describe()` and the benchmark list meshes,
    textures, unload and reload counts) over a 3000-frame flight round a 20000-unit circle: before, with nothing unloaded and RGBA8 textures,
    it grew to 6.4 GB (3.5 GB of foliage textures, 2.7 GB of object textures) and stayed there, process working set 5.7 GB (the first
    benchmark runs: 3.0 to 4.4 GB resident, working set 5.6 to 6.2 GB after a 1500-frame flight); now 1.3 to 1.4 GB (compression alone) and, with
    `MEITOU_UNLOAD_IDLE=10`, a 0.7 to 1.3 GB band that follows the camera (about 850 object meshes and 400 foliage meshes unloaded and
    reloaded, 160 + 420 textures); after such a flight the same view looks the same as without unloading (mean difference 0.045, as run-to-run).
    Frame times with water reflections on, 1500 frames: before, 12.9 to 21 ms median, 61 to 83 ms 95th percentile, 122 to 305 ms 99th,
    maximum 270 ms to 2.3 s (those runs also had 5 GB more GPU memory in use); now 11.6 to 12.6 ms, 28 to 30 ms, 73 to 79 ms, maximum 125 ms
    (one run 694 ms, 635 ms of it waiting for the GPU). Render-thread time up to the end of the commands (no GPU wait): median 8.6 to 9.4 ms,
    95th 24 to 26 ms. With `--no-reflections` (the same flight): median 10.0 ms, 95th 16.8, 99th 23.1, maximum 37.0 ms, 28 frames over 20 ms and one over 33.
  - *Left*: single steps of a texture or buffer slab still take 10 to 20 ms now and then (driver or GPU contention, not size: they hit the first
    slab of a fresh buffer or texture), and one such step is the floor of a frame's overrun. The first foliage draw after a burst of streaming
    can take 20 to 50 ms too (it was often the reflection's, which draws first). Not done: a persistent-mapped upload ring (GL 3.3 core has none;
    buffers are filled with `BufferSubData`), unloading the terrain's overlay and colour windows (fixed size). The reflection pass itself is cheap now
    (next bullet, "Reflections").

## What the terrain costs at native 1080p (2026-10-09)

**Observed**, RTX 4070, `--view swamp --size 1920x1080 --upscaler dlss --render-scale native`, 384 to 768 frames; paired A/B runs (`--ab-period 16`) unless a build comparison is named. The terrain stage is 1.14 to 1.26 ms there (0.63 ms at 1280x720, 2.0 times for 2.25 times the pixels; the Hub view 0.89 to 0.99).

- **Breakdown of the 1.26 ms.** Ground colour only, no textured material (`--ab terrain-material`, the probe switch): 0.74 ms, so the material layers are 0.62 +-0.03; of those the normal maps are 0.19 +-0.01 (`--ab normal-maps`; they cost the objects 0.03 +-0.04) and the rest is the diffuse layers. Material only out to 8000 units instead of 30000: -0.05 +-0.08, because the swamp view is near terrain and haze; the far material is cheap here. The sun-shadow receiver is 0.28 ms of the stage before the filter-taps change (probe build returning 1.0, and `--ab shadow-pass`: -0.29 +-0.15, a noisy run) and 0.21 after it. Geometry: doubling the screen-space error (`--ab terrain-error`, the probe switch: about a quarter of the 0.40 M triangles, 58 patches) saves 0.12 +-0.01 ms (0.18 +-0.03 in a loaded run), so vertex work and rasterising are about 0.15 to 0.2 ms; the picture changes by 111 pixels of 12/255 or more (mean 0.10). The remaining 0.25 ms is the per-pixel normal from the height field (4 bilinear reads), the lighting, the haze (a constant haze colour: -0.04) and the framebuffer write.
- **What did not move it**: the number of biomes blended per pixel (weights below 0.08 instead of 0.004 dropped: 1.24 against 1.24); a coarser texture LOD bias (+1): none; anisotropy 4 instead of 8 on the layer arrays (a switch built for the run, not kept): -0.01 +-0.03 (the swamp's terrain is mostly seen at ratios below 4); anisotropy 1 does save (1.16 to 0.91 ms, single runs), but at that setting the ground smears at every angle, so it is not an option.
- **A resolution-aware error was tried and not kept.** The error is in rendered pixels, so at native 1080p the same 16 pixels are a finer deviation on screen than at DLSS Quality (720p render shown at 1080: 24 output pixels). Allowing 1.5 times the error at native (24 px; unchanged from the 0.67 render scale down) saves 0.06 +-0.07 ms on the swamp and 0.08 +-0.01 ms on the Hub, but the Hub picture changes visibly on the ridgelines and slopes (22,552 pixels by 12/255 or more, mean 0.54, maximum 193), so for 1 % of the frame it is not worth the difference to the picture.
- **Kept**: `terrainHeight` reads only the fine window when the point is inside it (weight 1: `mix(c, f, 1)` is `f`, the coarse grid was still being read, four times a pixel and twice a vertex): terrain 1.10 to 1.07 ms (two single runs, the same picture exactly). The terrain's plain lighting (the simple sky) is computed only in the `else` of the game-sky lighting that overwrote it (same values; terrain 1.07 to 1.06). The sun-shadow receiver's taps as constants ([formats/shadows.md](formats/shadows.md)): terrain 1.16 to 1.10 ms (-0.07 +-0.01, `--bench-compare` of two builds). Together, master b985e7b against this branch: terrain 1.26 against 1.14 ms (swamp), 0.99 against 0.95 (Hub).
- **Left**: the near-field material and the shadow receiver are most of what remains, and neither follows distance or LOD; a fade of the normal maps beyond some distance was not built (it can only save part of 0.19 ms, and most swamp terrain is within a few thousand units).

- **Triangle sizes** (**Observed**, 2026-10-09, `--bench-tris`, [bench.md](bench.md#triangles-per-pass-and-triangle-sizes---bench-tris)): on the swamp the near terrain is shading-bound: 2.10 M samples for 2.10 M pixels (1.01 a pixel, no overdraw) from 146 k visible triangles of 14 px^2 on average, of which 12.6 % are under a pixel and 4.7 % of the samples come from triangles under 4 px^2; 406 k primitives are submitted and 259 k leave the clipper. In the hub the far slice is geometry-bound: 376 k clipped triangles make 332 k fragments, and 73 % of the 716 k terrain triangles that leave the clipper are hidden behind nearer terrain or off screen (190 k count); in the desert 70 % of the visible terrain triangles are under a pixel. A depth-pyramid or horizon cull of terrain chunks at low pitch is the lever for the far views; the swamp's terrain cost is the material and shadow receiving per pixel, not the triangles.

### Where the terrain's 0.99 ms goes (probes, 2026-10-09)

**Observed**, RTX 4070, `--view swamp --size 1920x1080 --upscaler dlss --render-scale native` (`MEITOU_STREAMLINE_PATH` set), 768 frames, `--ab-period 16`, so every number is a paired A - B difference of the terrain stage in ms (95 % interval 0.01 to 0.03; the benchmark queues on the GPU lock, so other programs' benchmarks did not run at the same time). The stage itself is 0.99 ms mean (p99 1.2 to 1.3).

- **What the stage timestamp holds.** Only `TerrainRenderer.Draw` of the main view's depth slices (`WorldFrame`: the stamp after the sky to the stamp after the terrain). Probe `nodraw` (the patches not recorded) leaves 0.03 ms for the whole stage: the pass restarts, the slice's depth clear and the stamps. So 0.96 ms is the patch draws. Not in it: the sky (own stage), the TERRAIN-mode rocks (the foliage stage, `fol rocks`), the shadow cascades' and the reflection's terrain (own stages), the water. There is no depth pre-pass. The patch fragment shader has no `discard` and writes no depth (the mesh variant's cross-fade `discard` is the rocks' only), so the early depth test is on, and the patches are drawn nearest first; 1.0 fragments a pixel confirms it ([bench.md](bench.md#triangles-per-pass-and-triangle-sizes---bench-tris)).
- **Method.** `MEITOU_TERRAIN_PROBE=name[,name...]` makes a second terrain colour program from the shader text with the named part cut (`TerrainShaders.ProbeNative`, in `TerrainShaders.Probe.cs`), and `--ab terrain-probe` draws the main view with it on side B (A: the normal program). It is a true removal of code (a second program, not a uniform branch), so register use and occupancy change with it, and **a probe's saving includes that**, not only the removed instructions. `none` (the same text) gives +0.01, the floor. The picture changes with every probe; they are cost probes. The existing switches were re-run on the same build: `terrain-material` 0.48, `terrain-error` 0.05, `normal-maps` 0.17, `anisotropy` (8x to 1x, all textures, read in the terrain stage) 0.15.

| Component (what is removed on side B) | Saves, ms (A - B) | An optimisation | Estimated gain, ms |
| --- | --- | --- | --- |
| **Overlay layers**: everything but the base layer (`layers-base`; `no-maps`, which drops grass, dirt and road, 0.19) | 0.44 | see the rows below | |
| - cliff layer (`no-cliff`, two runs 0.20 and 0.21; `cliff-1proj`, one projection instead of two: 0.05; `cliff-nonormal`: 0.07 +-0.03). **Skipped where its weight is under 0.05** (`eps-cliff-05`): 0.07; under 0.02 (`eps-cliff-02`): 0.06; under 1/255 (`eps-cliff`): 0.01 | 0.20 | The shader skips a layer only when its weight is exactly 0, and the cliff's never is: its weight is above 0.004 on 98 % of the terrain pixels, above 0.02 on 41 %, above 0.05 on 25 %, above 0.1 on 18.5 % (`weights-b`, `weights-d`). So (1) skip it below a threshold of about 0.05: 0.07 ms, the picture the same (118 pixels differ by 4/255 or more against 112 between two identical runs, largest difference 9); (2) on top of that a second program without the cliff code for patches whose steepest slope stays under the cliff's start (the node's height bounds are known on the CPU, so the choice is conservative): the rest of the 0.20, of which about 0.1 is in what the presence of the code does to every pixel's program (hypothesis, **Unknown**: registers / occupancy) | 0.07 now, up to 0.15 with the permutation |
| - slope layer (`no-slope`; its weight is 0.1 or more on every terrain pixel, so skipping it below a threshold saves nothing: `eps-slope-05` 0.00) | 0.06 | none by skipping; it acts as a second base layer on flat ground | |
| - grass layer (`no-grass`) | 0.05 | | |
| - dirt layer (`no-dirt`); road (`no-road`) | 0.04; 0.01 | | |
| **Sun-shadow receiver** (`shadow-off`) | 0.20 | Of it: the 16 filter taps (`shadow-1tap` 0.11, `shadow-8tap` 0.07), the blocker search (`no-blocker` 0.03), the blend into the next cascade (`no-cascade-blend` 0.03); the terrain and landmark terms 0.01 (not significant); what one tap leaves (cascade choice, normal offset, plane, noise) is 0.09. Fewer taps and no blocker search for the terrain at distance (8 taps is what the outer cascades already use) | 0.05 to 0.08 |
| **Environment lighting** (`no-env`: ambient map, irradiance cube, specular cube) | 0.10 | The specular cube alone is 0.07 (`no-env-spec`): ground is rough (low gloss reads the blurriest mips), so a constant or a cheap approximation beyond a distance, or skipping it below a gloss | 0.04 to 0.07 |
| **Whole `kenshiLight`** (`light-flat`: shadow, environment and the BRDF replaced by one dot product) | 0.33 | = the two rows above plus about 0.03 of BRDF arithmetic | |
| **Terrain covered later** (not a probe, see below): 30 % of the terrain fragments are overwritten by objects, foliage and rocks | est. 0.25 | A depth-only terrain pass first (the geometry is 0.05 to 0.07 ms), then the colour pass after objects and foliage with a depth test of equal: the colour program runs only where the terrain is what is seen (the water refracts the terrain, so what lies under it still has to be shaded) | net 0.15 to 0.2 |
| **Normal maps** (`normal-maps`, inside the layer rows) | 0.17 | A distance fade of the normal maps (the swamp's ground is near, so only a part) | 0.03 to 0.08 |
| **Height-field normal** per pixel, four bilinear reads (`normal-vertex`, the normal from the vertices, 0.08; `normal-flat` 0.10, which also drops the slope layers) | 0.08 | One read of a baked normal map (RG8) for the fine window instead of four R16 reads, or per-vertex normals | 0.04 to 0.06 |
| **Haze** (`haze-off`; the constant haze colour in the earlier list gave 0.04) | 0.06 | The haze colour per vertex (it varies slowly) or a small table of the ray's colour | 0.03 to 0.05 |
| **Vertex work and rasterising** (`terrain-error`, a quarter of the triangles) | 0.05 | not a target (see the triangle sizes above) | |
| Wetness (`wet-off`) | 0.02 | not worth it | |
| Stage overhead (`nodraw`) | 0.03 | | |

The rows overlap (cutting one changes what the next costs), but the main ones add up: lighting 0.33 + overlay layers 0.44 + height normal 0.08 + haze 0.06 + geometry 0.05 + overhead 0.03 = 0.99. The normal maps and anisotropy are inside the layer rows.

- **Not the lever.** The biomes per pixel: 98.0 % of the terrain pixels blend one biome, 1.9 % two, so keeping only the strongest 1, 2 or 3 of the five slots saves nothing (`biome1`, `biome2`, `biome3`: -0.01, -0.02, -0.02, the ranking costs a little). The per-biome parameter fetches (13 `texelFetch` of RGBA32F per biome), the cells, blend map, overlay and colour reads and the base layer together are at most 0.04 (the difference of `layers-base` and `terrain-material`). Texture size and formats: 2048 / 1024 / 512 layers (single runs, `--layer-size`) 0.98 and 0.99 / 0.95 / 0.92 ms, so a sixteenth of the texels saves 0.06; the diffuse array is BC3, the normal array BC1, the layers are 8x anisotropic (not 16x). Compression is in place and bandwidth is not what limits it. Anisotropy 8x to 4x saves 0.01 (earlier), to 1x 0.15 but smears the ground.
- **Layer weights** (screenshots of the same view with a weight painted as a colour bit, 1.18 M terrain pixels, water off): slope layer above 0.1 on 100 %; cliff above 0.004 on 98 %, above 0.02 on 41 %, above 0.05 on 25 %, above 0.1 on 18.5 %; grass (overlay red or green) above 0.004 on 84 %; dirt on 20 %; road on 22 %.
- **Samples per fragment** (counted in the shader; a layer sample is one `textureGrad` on an array, once in the diffuse and once in the normal array): height normal 4 (R16, bilinear); overlay 1 and colour 1; blend cells 2 (`texelFetch`, uint) and blend map 1; parameters 13 per biome (`texelFetch`, RGBA32F); layers: base, grass, slope, dirt, road one each, cliff two; in the far fade band ground 1 and world colour 1; lighting: ambient map 1, irradiance cube 1, specular cube 1; the shadow receiver: 8 blocker and 16 filter taps (each a 2 x 2 comparison) in the first cascades, 8 in the outer ones, doubled inside the blend band, plus 1 terrain term and 8 landmark taps where that term applies. A typical swamp pixel (one biome, base, slope, cliff twice, grass mostly, normal maps on) makes about 61: 4 + 2 + 3 + 13 + 11 + 3 + 24 + 1. Measured layer counts (probes `count-lo`, `count-hi`, `count-bio`: a screenshot of the same view with the count painted as colour bits, 1.18 M terrain pixels, water off; the counter missed the base layer, so add one): diffuse layer samples per pixel 5 for 49.5 %, 6 for 41.9 %, 7 for 5.0 %, 8 or more for 2.6 % (mean 5.58; the same number again in the normal array); 98.0 % of the pixels blend one biome, 1.9 % two.
- **Share of terrain fragments covered later** (**Observed**; a screenshot of the swamp view, from its start, with the terrain painted flat (`paint`), water and particles off, the fog volumes on so that the fog cull is as in the benchmark): with the objects and foliage on, 1.18 M pixels show terrain; with `--no-objects --no-foliage` the terrain covers 1.70 M pixels (the rest is hidden by the fog's cull and sky). So 69.5 % of the shaded terrain is what is finally seen and **30.5 %** is drawn over by buildings, trees, bushes, rocks and grass. The terrain is drawn first, ahead of objects and foliage, so these fragments are shaded and then overwritten. The bench's 1.98 M terrain fragments are 0.95 a pixel of the orbit view; the share there was not measured. The water's cover is not counted (it needs the terrain below it). The gain of a late colour pass is an estimate from the share (30 % of the fragment cost, less the extra geometry pass): **Unknown** until built.
- **Reading.** Most of the stage is material and lighting per pixel. The largest single piece is the cliff layer, which is sampled on 98 % of the pixels at a weight that matters on a quarter of them. The picture-neutral options are skipping the cliff below a weight of 0.05 (0.07 ms, measured), the cliff permutation per patch and the late colour pass; the shadow taps, the environment specular, the height normal and the haze are each small and change the picture a little.
