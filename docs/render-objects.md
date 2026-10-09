# Object rendering and streaming

Part of the world view of `meitou-viewer` (and the game, which shares `src/Meitou.Rendering`); options, keys and the rest of the world mode are in [viewer.md](viewer.md#world-mode).

- **Objects** (`WorldObjects`, `WorldObjectRenderer`): placements become meshes with `WorldObjectLayout`, built
  as the game builds them ([formats/zones.md](formats/zones.md#from-placements-to-meshes)): parts chosen with the
  game's `rand()` seeded from the position, doors added, destroyed states (`destroyed mesh`, upper floors
  removed), rotating parts at their rolled start angle, and each part's MATERIAL_SPEC from the part, the building
  or the building's town (`BuildingTowns`; `BuildingMaterial` turns it into textures). A building whose state names an
  `exterior layout name` also gets that layout's signs and banners (`BuildingLayouts`, from `interiors.level`;
  [formats/zones.md](formats/zones.md#building-layouts)), counted in the `objects` log line; interior layouts (furniture)
  and nest debris are not drawn. Map features (and parts
  without a chosen material) are textured by `MaterialResolver` ([above](#how-mesh-textures-are-resolved); candidate preferred: the one
  naming the placed record). Draw distance, LOD and streaming are described below ("Object streaming, LOD and distant towns"). The log
  line `objects` counts destroyed buildings and the foliage resource buildings, which the game draws as foliage rocks (drawn by the foliage below).
  TERRAIN-mode map features are drawn with the terrain shader through plain per-level vertex arrays (`TerrainRenderer.DrawMeshes`), with the game's `Feature_Terrain` rules ([formats/foliage.md](formats/foliage.md#terrain-mode-meshes)): the one biome of `biomemap.png` at the mesh's origin (the terrain's blend-map mix while that biome's textures are not resident), slope clamped at 1, per-vertex cliff projection weights, no roads, back faces culled; they pick a mesh LOD level but do not fade.
- **Object streaming, LOD and distant towns** (`ObjectStreamer`, `ObjectMeshCache`, `BuildingLodShaders`, `ObjectRanges`; data side
  `MeshLod`, `DistantTowns`):
  - Zones are laid out on worker threads (`WorldObjects.BuildZone`, 3 at a time, nearest first) within the distant range of the eye
    and dropped one zone width beyond it. A mesh is requested once an instance is within its range, decoded on a worker (including the
    index buffer with every LOD level back to back), then uploaded in steps of about 1 MB through the shared `UploadQueue`
    (`WorldTextureCache` slices textures the same way), so a frame is rarely held up more than a few ms (interactive run with
    `MEITOU_STREAM_LOG=1`: slow-update lines went from 30 to 220 ms down to mostly under 10, rare spikes to 50 ms from driver syncs and
    scans). Meshes and textures are unloaded again when out of every draw range for a while (next bullet, "Streaming cost and unloading").
  - **Mesh LOD**: per submesh, from the mesh file's levels (`MeshLod`, the rule in [formats/ogre-mesh.md](formats/ogre-mesh.md#lod)),
    with the world bounding sphere of the mesh. Level changes are a dithered cross-fade (interleaved gradient noise, per-instance
    `lo`/`hi` range carried in the instance matrix; the upper level takes the pixels below the threshold and the lower one the rest, so
    they never both cover a pixel), a smoothstep band of 6% of the level's distance. Manual levels draw another mesh file.
  - **Draw distance** (the `reach` switch, `F8`): a real object is dropped by `PartRenderingDistance` (zones.md) and fades out over a band near
    `--object-distance` (default 20000 in Meitou and 12000 in Faithful; the game itself shows real objects only in loaded zones, about 3000).
    The depth slices split at 1.1 x that (at least 20000), so Meitou's second slice starts at 22000. The terrain LOD error follows the same switch (16 px / 10 px).
  - **Landmarks** (Meitou only; docs/renderer-native.md 8.19): a placement whose world bounding radius (the mesh's bounds radius, read from the file without
    loading it, times its largest scale) is 2000 or more is a landmark: the giant map features (the Skylink satellite, rib cages, tower cores, dangler and
    ring wrecks, pipelines; 90 placements of 31 meshes in the base game, no building part). Landmarks are collected once at start from all populated
    zones (about a second on a worker), left out of the zones, and kept in one list that `Scan`, `MarkInRange`, the CPU cull and the shadow casters all walk
    with the same range: `--landmark-distance` (default 150000, Tab slider "Landmark distance (Meitou)", 1000 to 400000) but never less than the object
    distance, and still capped by the game's part distance for building parts. Their meshes use the far LOD forms and mip streaming like any object. Faithful
    (`--faithful reach` or `all`, or `--landmark-distance 0`) builds no list: every placement stays in its zone and the object distance rules, so the
    pictures do not change. Without Meitou at the start there is no list (and no slider); `F8` later moves the distances, not the landmarks.
    With the Meitou shadows every landmark drawn also casts its shadow however far it is (a landmark shadow map along the sun beyond the cascades;
    [formats/shadows.md](formats/shadows.md), "Landmark shadows").
  - **Distant towns**: for a town with a baked mesh (`data/meshes/distant/distant_<handle>.mesh`, `DistantTowns.Find`) the baked mesh
    rises in as the real buildings fade out, with per-vertex fade from the eye distance (no dither cost); it falls off at
    `--distant-range` zones (default 10, the game's setting maximum; the game's default is 6; `--no-distant` disables). A town without one
    shows each building's own `distant mesh` as an instance ("stand-in").
  - **Batching**: one instanced draw per (mesh, material set, level, town): instance matrix rows are vertex attributes 7 to 10 (divisor 1,
    re-pointed per batch), uniforms and texture binds are cached. A view of The Hub from 3500 units: 61 draw calls, 155 instances, 0.3 ms
    CPU (offscreen run, 2026-10-04). Objects are drawn in every depth slice (far first).
  - Debug: `MEITOU_LOD_DEBUG=1` colours surfaces by LOD level, `=2` draws only wireframe by level (green 0, yellow 1, orange 2, red 3,
    magenta manual level, blue distant stand-in). `MEITOU_STREAM_LOG=1` prints slow steps. `--distant-range <zones>`, `--no-distant`.
  - Not done: eviction of meshes, fading of TERRAIN-mode features, cross-fade of manual levels' own materials; the F11 statistics show
    objects, draw calls, draw CPU ms and what is still loading.
- Back-face culling is off for objects (open building meshes); the light (`kenshiLight`) and the haze are the atmosphere's (above).

Verified with screenshots (2026-10-04, saved outside the repo): The Hub (`--town "The Hub"`: walls, gates and
towers join up, buildings upright, roads in the ground texture), zone 44.23 (TERRAIN-mode cliff blocks take
the biome textures), zone 54.44 (UV-mapped cliff features), zone 31.45 at radius 3, and the whole world from
above (biome regions as in `biomemap.png`). Building assembly, before/after (2026-10-04): The Hub (21 destroyed
houses now in their ruined meshes), Brink (wind generators at rolled angles, destroyed shacks), Squin (houses in
the town material instead of a stray candidate), Stack (`--at -55671,-12401 --radius 0.15 --distance 350 --pitch
20 --yaw 90`: the Small Shack's door fills its frame).

## What the objects cost on the swamp view (2026-10-09)

**Observed**, RTX 4070, `--view swamp --size 1920x1080 --upscaler dlss --render-scale native` (the DLAA target view), 512 to 768 frames. The machine was shared with other viewers during some runs, so every figure below is from a paired A/B (`--ab-period 16`, 64 for anything with the shadow cache) or from two builds run alternately, never from one run against another.

- **Where `--ab objects-draw` (1.87 +-0.09 ms) goes.** Three passes draw the same renderer: the main view (the `objects` stage, 0.72 to 0.80 ms), the water reflection pass (its stage is 0.93 to 1.0 ms with the objects and about 0.25 without, so the objects are the larger part of it; they are drawn at the reflection's own LOD bias 3 and object distance) and the shadow cascades (0.29 +-0.08 ms of the `shadows` stage, whose mean of 0.6 to 0.7 is a median of 0.3 plus the spikes of the cached cascades redrawing). The reflection's share belongs to `ReflectionPass` and was not touched.
- **The main view is fragment-bound, not triangle-bound.** Probe builds (env-var shader edits, not kept), single runs, medians: a fragment shader that writes a constant takes the `objects` stage from 0.81 to 0.25 ms (and the reflection stage from 1.00 to 0.60, so 0.4 ms of the reflection is object shading); clipping every vertex away leaves 0.50 (vertex stage and front end) and takes the shadow stage from 0.72 to 0.55; replacing `kenshiLight` by the albedo (no sun shadow, no sky lighting) saves 0.15; removing the weather's wetness saves nothing (the swamp's rain); a constant haze colour saves 0.03. The rest, about 0.4 ms of the 0.55, is the material: the diffuse and normal fetches (8x anisotropic) and the arithmetic round them. A texture LOD bias 1 coarser saved nothing measurable. 80 batch draws a frame in the main view and 0.6 draws of TERRAIN-mode features, 0.17 us of CPU each: the draw count is not the cost.
- **Mesh LOD is not a lever here** (a probe that multiplied the LOD distance of what is drawn, the streaming still holding the finer levels; not kept). Main view, objects stage: LOD distance x2 saves 0.08 +-0.11 ms (219 pixels differ by 4 or more); x100, every instance at its coarsest level, saves 0.12 +-0.07 ms and takes the triangles of the main view and the reflection from 1.42 M to 1.09 M, so the rest are in meshes without reduced levels or in instances nearer than their first LOD distance (**Unknown** which; the split was not measured). Shadow cascades with LOD distance x3 (`--ab-period 64`): -0.03 +-0.12 ms. Leaving objects smaller than two texels of a cascade out of it, as foliage meshes are (`ShadowPass.MinFoliageCasterTexels`): -0.01 +-0.08 ms over 2048 frames (711 against 698 object instances a frame). None of the three is in the code.
- **Batches nearest first** (`WorldObjectRenderer.SortNearestFirst`, Meitou with the `reach` switch; `--ab object-sort`): the colour batches are sorted by their nearest instance before they are drawn, so what a nearer batch covers fails the early depth test. Objects stage -0.03 +-0.02, reflection stage -0.02 +-0.03 (it draws through the same code), frame -0.16 +-0.16; the picture does not change (the cross-fade dither is per pixel, not per draw order). Alpha-tested parts (cut-out cloth, fences) shade before they can fail the depth test, so the gain is small.
- **Plain lighting only where used.** The mesh shader computed the viewer's plain lighting (with a `pow`) and then overwrote it with `kenshiLight` under the game sky; `BuildingLodShaders.Fragment` now computes it in an `else` branch (same values; the shared `Shaders.MeshFragment` is untouched, because the foliage and the impostor bake patch strings in it). Objects 0.73 to 0.72 ms, within the noise.
- **The shadow receiver's taps as constants** ([formats/shadows.md](formats/shadows.md), "The filter's taps are constants") saves 0.03 ms of the objects stage and 0.05 of the reflection stage.
- **Total of this session's changes on the swamp view** (master b985e7b against this branch, paired `--ab occlusion` runs so side A is the default configuration, 512 frames, repeated runs agree within 0.03 ms): GPU total 7.47 against 7.12 ms (-0.35, 4.7 %), objects 0.76 against 0.71, terrain 1.26 against 1.14, reflection 0.97 against 0.92, foliage 1.81 against 1.69 (the shared receiver). The Hub (`--view hub`): 3.94 against 3.90 ms (its cascades cover little). Pictures against master: swamp 13,124 pixels differ by 1/255 and none by more; the Hub 91 pixels by 4 or more, 5 by 12 or more (maximum 16).
