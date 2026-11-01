# Impostors

Meitou engine design, not original-game format/parity. `Rendering/Impostors`, FoliageRenderer
Impostors/Lod and FoliageGpuCull. Faithful never draws/bakes atlases. Atlas layout/cache is below;
[foliage](render-foliage.md), [range limits](render-distance-benchmark.md), [bench](bench.md).

## Projection and maps

One hemi-octahedral NxN atlas per mesh (plus biome for TERRAIN rocks). Encode: y=max(y,0),
s=abs(x)+y+abs(z), q=xz/s, uv=(q.x+q.y,q.x-q.y)/2+0.5. Decode e=2uv-1,
xz=(e.x+e.y,e.x-e.y)/2, direction=normalize(xz.x,1-abs(xz.x)-abs(xz.y),xz.y).
Below-horizon views clamp to edge. Frame reference=(0,0,-1) atdy>0.999 otherwiseupY;
right=normalize(cross(reference,d)), up=cross(d,right). FrameUV=(dot(p-c,right),dot(p-c,up))/(2r)+0.5.
Sphere=main+leaves box centre/farthest vertex*1.01. Choose triangle/three frames and per-pixel
coverage vote, preserving thin foliage; do not simply blend transparent mismatched silhouettes.

BC1 albedo/1-bit coverage and BC5 full-sphere frame-normal; sample premultiplied colour then
unpremultiply. Normal uploads one mip coarser; scalar mean gloss, no depth/parallax map.
Rows bottom-first. Grid12, F=power-of-two ceil(screenDiameter/1.4),64..256, chain ends4px/frame.
Small class radius>=2,triangles>=100 uses32px/grid12, transition4000*radius/48. Ordinary transition
4000, large structures12000; ordinary large plants retain4000 transition but large visibility range.

## Bake and residency

Render each row at2NF by2F; output pre-lighting albedo/normal/gloss,2x downsample/readback after
GPU completion. Texture bias log2(2F/projected mean-scale diameter), clamp0..4. Pull-push fill
and per-frame coverage-preserving mips; BC1 covered-pixel principal-axis endpoints. Budget4million
samples/frame, at least one row; keep meshes until atlas Ready. Reuse buffers/arrays, release
pools10s idle. Prepare lazy frame globals outside rendering; warm programs/quad/pipelines safely.
**Verified** pooled output MD5 for267 meshes and split tests; cold-cache validation named views.

Atlas budget min(clamp(share*deviceBudget,48MB,cap),12%budget):discrete8%/1024MB,
integrated5%/256MB, unknown192MB; pressure*0.75; explicit --impostor-budget overrides.
Nearest-first plan degrades far mips before excluding; lookahead3s, one-level margin plus
upscaler bias. Refine on approach, retain old atlas until ready; retry refused upload2s.
Unwanted atlases unload after 20 s idle; after a jump of 10000+ units between scans (a save loaded from the menu's
scene, a teleport) at once. **Observed** (2026-11-01): menu-scene atlases held the total at the limit (1014 MB) for ~20 s after a load.
Integrated heap/budget adequacy unverified. Never reuse in-flight views/indices prematurely.

## Rock and fade rules

TERRAIN atlases use mesh/biome-row/material, cached by RockBakerVersion/BiomeKey/source metadata.
Wait for biome textures, otherwise meshes. Runtime matches terrain tint/far fade; complementary
mesh fade avoids crosshatch. Frame cap256, knee~620 scales transition; area/sphere filter0.06
excludes thin sticks. Different drawn biome/mirroring remain visual limits.

Range fade w=clamp((range-d)/band), band=max(layer transition,0.1range). Stable instance hash
selects `fract(key+a*noise)<w`, a=smoothstep(3,12,p), p=2radius/length(dFdx(objectPoint));
small quads thin whole instances, large use pixel noise. Mesh crossfade keeps complementary
noise; billboard casters fade by key. Minimum-radius16-tap receiver omits blocker search.
Unavailable layouts/atlases and distance fade are separate pop-in causes. Preview:
--impostor-preview <FOLIAGE_MESH> [--class small].

### 2. Maps and sizes

| Map | Channels | Encoding | Resident levels |
| --- | --- | --- | --- |
| Albedo | RGB albedo (the mesh shader's albedo before lighting); alpha is a 1-bit cut-out | BC1 with punch-through alpha | all but the surface-skipped ones |
| Normal | Octahedral full-sphere encoding of the normal in the frame's basis (x right, y up, z towards the viewer) | BC5 | one level coarser than the albedo |

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

**Cache (`ImpostorCache`).** `%LOCALAPPDATA%\Meitou\impostors\<name>_<key>.mimp`, overridable with `MEITOU_IMPOSTOR_CACHE` (see CacheDirectories for the shared cache root); never in the
repo. The key is the first 24 hex digits of a SHA-256 over the baker and format versions, each source file's path, length and write time,
both materials' descriptions, the maximum **and mean** scale, the grid, magnify and bias. Files are written under a temporary name, then
moved. Bump `ImpostorAtlas.BakerVersion` when the baker's output changes.

**Size cap, LRU and stale files (`ImpostorCache.Maintain`, `ImpostorCache.cs`).** A file's last use is its last write time
(`TryLoad` hits and `Save` set it). `Maintain` runs on a worker at the first foliage update and after every 16 saves, under a lock, and:

1. deletes files that are not atlases (shorter than the 12-byte header, wrong magic) and those of an **older** format or baker version than
   the build's (their keys can never match again; a newer version is left, since another build sharing the folder may read it);
2. deletes temporary files (`*.mimp.<pid>.tmp`) an hour old (a crashed writer's);
3. when the remaining `.mimp` files total more than the cap, deletes the least recently used until the total is under 90% of it.

Other files in the folder are not touched. The cap is `MaxBytes`: default 4096 MB (a constant; the
`--impostor-cache-mb` flag and `MEITOU_IMPOSTOR_CACHE_MB` were removed, `ImpostorCacheMb` stays as a property, 0 = no cap). **Verified**
(`ImpostorTests`, a temporary folder, never the user's cache): eviction order and the 90% target, no cap, the stale rules above (older baker,
older format, junk, short file, old temporary go; current, newer, fresh temporary and foreign files stay), and that loads and saves set the last use.
**Unknown**: two viewers evicting at once (each deletion tolerates a failure; the worst case is a rebake).

**Deleting the cache (`ImpostorCache.Clear`, the Tab panel's "Delete billboard cache" button).** Deletes every `.mimp` in the folder after a
Yes / No question that names the count and size (`Measure`); temporary files and foreign files stay, a file another process holds stays. Resident
atlases are not dropped: they are baked again when next needed and not resident. **Verified** (`ImpostorTests`, a temporary folder): the count and
bytes, and that temporary and foreign files survive.

### 5. Runtime sampling

| Piece | Contents |
| --- | --- |
| `Functions` (any stage) | `impostorEncode`, `impostorDecode`, `impostorDecodeNormal`, `impostorBasis`, `impostorSelect(view, grid, out a, b, c, weights)`, `impostorFrameUv` |
| `FragmentFunctions` | `struct ImpostorSurface { vec3 albedo; float coverage; vec3 normal; float gloss; vec3 position; }`, `framePick()` and `impostorSample(albedoMap, normalMap, grid, centre, radius, origin, ray, cellA, cellB, cellC, weights, pick)` |
| `FragmentNative()` | The program on the foliage instance ABI (push constants `ImpostorPush`: sphere, grid, atlas indices, `Gloss` at offset 40) |
