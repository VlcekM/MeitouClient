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
