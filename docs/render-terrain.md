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
