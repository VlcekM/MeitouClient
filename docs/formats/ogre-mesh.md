# Ogre `.mesh`

Sources:
- **OGRE source** (MIT): Kenshi's `OgreMain_x64.dll` is Ogre 2.0 ("Tindalos" in its strings), which
  still has the classic "v1" mesh system. The layout below follows ogre-next branch `v2-0`,
  `OgreMeshSerializerImpl.cpp` / `OgreMeshFileFormat.h`. `Meitou.Data.Ogre.OgreMeshReader` follows the
  same structure (notice in [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md)).
- **Verified 2026-10-04** (`OgreMeshReaderTests.Reads_every_base_game_mesh`, `meitou-tools meshes`):
  all 3,277 base-game meshes parse to the last byte; every index is below its vertex count; every
  vertex position lies inside the mesh's stored bounding box (which checks the vertex layout decoding).

## Versions in the base game (Verified)

| Header string | Files | Ogre serializer |
| --- | ---: | --- |
| `[MeshSerializer_v1.100]` | 1,740 | `MeshSerializerImpl` (current in Ogre 2.0) |
| `[MeshSerializer_v1.8]` | 1,460 | `MeshSerializerImpl_v1_8` |
| `[MeshSerializer_v1.41]` | 77 | `MeshSerializerImpl_v1_41` (same as 1.8 except pose/morph data) |

The DLL also knows 1.40, 1.30, 1.20, 1.10 (no base-game files use them; the reader rejects them).
The only differences between the three supported versions are in the LOD section.

## Stream basics

- Little-endian. (Ogre detects endianness from the header id; a byte-swapped id means big-endian. Not
  seen; the reader rejects it.)
- `bool` = 1 byte, `ushort` = 2, `uint` and `float` = 4.
- `string` = bytes up to a `\n` (a trailing `\r` is dropped). No length prefix.
- File starts with `ushort 0x1000` and the version string. No length after the header id.
- Then chunks: `ushort id`, `uint length` (including the 6-byte chunk header), body. A chunk's
  children follow its fields. Ogre reads children **by structure**, not by length: a parent keeps
  reading child chunks while the next id is one it expects, and otherwise steps back 6 bytes and
  returns (old exporters wrote wrong lengths, per a comment in Ogre's LOD reader). Only sections Ogre
  itself skips are skipped by length.

## Chunks

```
0x1000 HEADER            ushort id + version string (no length)
0x3000 MESH              bool skeletallyAnimated
  0x5000 GEOMETRY        shared vertices (none in Kenshi: Verified 0 meshes use shared vertices)
  0x4000 SUBMESH         string material, bool useSharedVertices, uint indexCount, bool indexes32Bit,
                         ushort/uint indices[indexCount]
    0x5000 GEOMETRY      required if !useSharedVertices
    0x4010 SUBMESH_OPERATION      ushort operation (1 points, 2 lines, 3 line strip, 4 triangle list (default), 5 strip, 6 fan)
    0x4100 SUBMESH_BONE_ASSIGNMENT uint vertex, ushort bone, float weight   (repeats)
    0x4200 SUBMESH_TEXTURE_ALIAS   string alias, string texture              (repeats)
  0x6000 MESH_SKELETON_LINK       string skeleton file name
  0x7000 MESH_BONE_ASSIGNMENT     as 0x4100, for shared vertices
  0x8000 MESH_LOD_LEVEL           see LOD below
  0x9000 MESH_BOUNDS              float min x,y,z, max x,y,z, radius
  0xA000 SUBMESH_NAME_TABLE
    0xA100 ..._ELEMENT            ushort submesh index, string name          (repeats)
  0xB000 EDGE_LISTS               stencil-shadow edges (walked, not kept)
  0xC000 POSES                     see Pose chunks below
  0xD000 ANIMATIONS, 0xE000 TABLE_EXTREMES   skipped by length

GEOMETRY (0x5000): uint vertexCount, then
  0x5100 VERTEX_DECLARATION
    0x5110 VERTEX_ELEMENT         ushort source, type, semantic, offset, index   (repeats)
  0x5200 VERTEX_BUFFER            ushort bindIndex, ushort vertexSize            (repeats)
    0x5210 VERTEX_BUFFER_DATA     vertexCount * vertexSize bytes (interleaved elements of that source)
```

`vertexSize` must equal the sum of the sizes of the elements with that `source` (Ogre checks; so do we).

Edge list (0xB000): repeated `0xB100` chunks: `ushort lodIndex`, `bool isManual`; unless manual:
`bool isClosed`, `uint triangles`, `uint edgeGroups`, `triangles × 48 bytes` (index set, vertex set,
3 vertex indices, 3 shared indices, 4-float normal), then `edgeGroups` × `0xB110` chunk
(`uint vertexSet, triStart, triCount, edgeCount`, `edgeCount × 25 bytes`).

### Vertex elements

Semantics: 1 position, 2 blend weights, 3 blend indices, 4 normal, 5 diffuse, 6 specular,
7 texture coordinates, 8 binormal, 9 tangent.
Types: 0–3 float1–4, 4 colour (deprecated, assume ARGB), 5–8 short1–4, 9 ubyte4, 10 colour ARGB,
11 colour ABGR, 12–15 double1–4, 16–19 ushort1–4, 20–23 int1–4, 24–27 uint1–4.

Used in the base game (Verified, element count over 3,748 submeshes): position float3 (all), normal
float3 (all), UV float2 (3,541), binormal float3 (2,431), tangent float3 (1,777) or float4 (1,462),
diffuse colour ARGB (1,628) or ABGR (89). **No blend weights/indices elements**: the 167 skinned meshes
store skinning as bone assignments (Ogre builds blend buffers from them at load).

### Pose chunks (Verified)

Per ogre-next `v2-0` `MeshSerializerImpl::readPoses` / `readPose`; 1.8 shares the current reader, 1.41 has no
normals flag:

```
0xC000 POSES
  0xC100 POSE          string name, ushort target (0 = shared vertices, else submesh index + 1),
                       [1.100 / 1.8] bool includesNormals                     (repeats)
    0xC111 POSE_VERTEX uint vertex index, Vector3 offset, [Vector3 normal if includesNormals]   (repeats)
```

Verified: all 13 base-game meshes with poses read to the end with this (the mesh reader test parses every
mesh to its last byte). Observed (same survey): every pose targets submesh 0, none includes normals, and the
poses of one mesh touch 5,772 (`Stick_person.mesh`) to 26,703 (`bone_male.mesh`) vertices in total.

### LOD (0x8000)

- **1.100**: `string strategy`, `ushort levels`; then for each level after 0, a chunk 0x8110 (manual)
  or 0x8120 (generated), body starting with `float userValue`:
  - manual: `string meshName`
  - generated, per submesh: `uint indexCount`, `uint indexStart`, `uint bufferIndex`; if `bufferIndex`
    is `0xFFFFFFFF`: `bool 32bit`, `uint bufferIndexCount`, indices; else it reuses the buffer of
    LOD level `bufferIndex` (1-based).
- **1.8 / 1.41**: `string strategy`, `ushort levels`, `bool manual`; for each level after 0, a 0x8100
  USAGE chunk: `float userValue`, then a 0x8110 chunk (`string meshName`) if manual, else one 0x8120
  chunk per submesh: `uint indexCount`, `bool 32bit`, indices.

1,389 base-game meshes have LOD data.

## Other base-game facts (Verified)

- 3,748 submeshes, 22.7 M vertices, 23.0 M indices; 167 skinned (have a skeleton link).
- Edge lists in 103 meshes (walked), poses in 13 (read). No vertex animations or extremes.

## How Kenshi uses it

Sources: decompilation of `kenshi_x64.exe` and Kenshi's own `OgreMain_x64.dll` (Ghidra 12.1.4); see also
[ogre-skeleton.md](ogre-skeleton.md#how-kenshi-uses-it) for skinning and bone maths and
[animation.md](../animation.md).

### Poses: character morph targets

The 13 meshes with poses are the race body meshes and their limb-stump variants (Verified: `OgreMeshReader`
parses the pose chunks, see [Pose chunks](#pose-chunks-verified); counts from a scratch survey over every base-game
mesh):

| Mesh (`character/meshes/...`) | Poses | Names (examples) |
| --- | ---: | --- |
| `human/human_male.mesh`, `human_female.mesh` | 23, 29 | `wide_cheekbones`, `long_nose`, `big_mouth`, `tiltup_eyes`, `overbite`, `wide_jaw` |
| `bone/bone_male.mesh`, `bone_female.mesh` | 28, 35 | `bone_` + face names, plus `bone_horns_curved`, `bone_horns_top_short`, `bone_horns_bottom_short`, `bone_horns_thick`, ... |
| `stick_person/Stick_person*.mesh` (3) | 9 each | `stick_big_eyes`, `stick_long_antenna`, ... |
| `human/` limb-stump meshes (6) | 23 or 29 | `0` ... `22` / `0` ... `28` |

They are face (and horn) shape morphs, not expressions or animation. Verified by decompilation:

- Pose names are looked up as float keys in the character's appearance data
  (`kenshi_x64.exe @ 14007c070`); a pose with a non-zero value is blended into the vertices.
- When building a body, Kenshi clones the mesh, adds every non-zero pose to the clone's positions and
  normals with `Mesh::softwareVertexPoseBlend`, then removes all poses from the clone (`@ 140071ac0`).
  The result is cached as a mesh named `<mesh>_morph_<n>` (logged "Created Morph"), so a character's
  face costs nothing at render time. The three `bone_horns_*` poses are skipped in this bake unless a
  flag is set (Kenshi's "SLAVE" variant handling, `@ 14007c070`; Observed, not traced further).
- The character editor instead clones the mesh as `<mesh>_CHAREDIT_<n>` and keeps its poses
  (`@ 140071dc0`) so sliders can change live.
- The weight is the stored appearance value itself, unscaled and unclamped (Verified, `@ 140071ac0` passes it
  straight to `softwareVertexPoseBlend`); in the SLAVE variant `bone_horns_top_short` and
  `bone_horns_bottom_short` are blended at 1 and `bone_horns_curved` is skipped (details in
  [../characters.md](../characters.md#face-shapes-poses)).

### LOD

- **Strategy**: at startup Kenshi looks up the `distance_sphere` LOD strategy (falling back to the default
  one) (`kenshi_x64.exe @ 1404483c0`), and its mesh setup (`@ 140447bf0`) sets it on every mesh it
  creates an entity for, whatever the file says. Verified. In the files (Verified, scratch survey of all
  base-game meshes with `OgreMeshReader`): `distance_sphere` 1,329 meshes, `Distance` 60.
- **Distances** come from the files' LOD user values: 2,472 generated (reduced-index) levels and 50
  manual levels (separate `_LOD` meshes). Typical values are 4000 and 8000; others 400, 500, 1000, 1500.
  Level counts per mesh (including level 0): 1 ×324, 2 ×1,039, 3 ×17, 5 ×1, 8 ×8 (Verified, same survey).
  In v1.100 files a later level reuses the index buffer of an earlier one: level 2's `bufferIndex` is 1 (the
  first reduced level's buffer, read at its own start and count) in 1,198 submeshes; level 1 always has its
  own buffer, sometimes longer than its count (Verified, scratch survey). Character meshes: see
  [../characters.md](../characters.md#lod-of-character-meshes) (bodies and hair 200, armour 400).
- **Selecting a level** (Verified, decompilation of Kenshi's `OgreMain_x64.dll`:
  `DistanceLodStrategyBase::lodUpdateImpl @ 1800ac380`, `LodStrategy::lodSet @ 1800ac0c0`,
  `transformUserValue @ 1800ac4c0`): per object, value = (distance from the LOD camera's position to the
  centre of the object's world bounding box − the object's world bounding radius) × the camera's LOD bias ×
  a per-call factor. The user value is used as it is (not squared; `transformUserValue` returns its
  argument), and the level is a lower bound over the mesh's sorted values minus one, at least 0: level k is
  drawn once the value is strictly above level k's distance. Kenshi's `distance_sphere` (`getSquaredDepth
  @ 1800ac690`: squared distance minus squared radius) is only the per-object `getValue` path; the batch
  update above is what the 2.0 scene manager runs (Observed, Ogre 2.0 source structure). Implemented in
  `Meitou.Data.Characters.CharacterLod`; the viewer takes bias and factor as 1 and the mesh file's bounds.
- No LOD is generated at run time. Nothing in `kenshi_x64.exe` imports `OgreMeshLodGenerator_x64.dll` or
  refers to `lod_generator.cfg` (Verified: import table and string search; no other DLL in the install
  names it either). The `.cfg` describes the developers' offline baking (e.g. `animal/meshes/noLOD`
  with `lod1=8000,p,1.0`).
- Kenshi doesn't set a mesh LOD bias (no import of the bias setters; Verified). Entity manual LOD levels
  are only touched to give them the same material as the main entity (`@ 1400d1400`, `@ 1404273f0`).
- Per-object visibility distance is separate: characters and attached items get
  `MovableObject::setRenderingDistance` from a global view-distance setting (×7 for some races,
  `@ 140539020`, `@ 140537020`). Observed (setting's source not traced).
- **World objects** use the same rule and the same files' levels (`Meitou.Data.Ogre.MeshLod`, which `CharacterLod` now forwards to):
  the viewer takes the mesh file's bounds (centre and radius) per instance, selects a level per instance (not per submesh, since all
  submeshes of one mesh share the levels' distances in the survey) and cross-fades with a dither
  ([../render-objects.md](../render-objects.md)). The fade band (6% of the level distance) is the viewer's own, not the game's (Ogre switches hard).
  Building meshes: levels are reduced index lists with ascending distances (`ObjectLodTests`, install-gated). Manual levels name another
  mesh file and are drawn instead of the main mesh.

### Other mesh setup

- If a mesh without a skeleton has more than one UV set, Kenshi logs "has multiple uv sets" and drops the
  extra texture-coordinate elements (`@ 140447bf0`). Verified.
- The body entity's local bounding box keeps its minimum corner, and its maximum corner becomes the
  largest of the three maximum coordinates on every axis, presumably so animation doesn't push the mesh
  out of it (`@ 140539020`); attached items get a box built from the same value (`@ 140537020`).
  Verified (the purpose is a guess).

## Open questions

- How pose weights are derived from appearance values. Observed: body files store values under the pose
  names themselves ([../characters.md](../characters.md#face-shapes-poses)); whether Kenshi scales them is Unknown.
