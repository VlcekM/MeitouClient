# Ogre mesh

Format research from Ogre serializer/source and base-game reader scans; versions/chunks/vertex
fields retained below. `OgreMeshReader` reads geometry/weights/poses/LOD; [skeleton](ogre-skeleton.md)
owns bone/animation layout. [Characters](../characters.md), [materials](ogre-material.md).

## Stream and compatibility

Little-endian header ushort0x1000 then newline-terminated version string, NO header length.
Primitive bool1/ushort2/uint-float4; strings newline with trailing CR dropped. Ordinary chunks
ushortID+uintLength INCLUDING6-byte header. Children are parsed structurally, not blindly skipped
by parent length: exporter bugs exist. Byte-swapped endian header is detected but unsupported.
Keep shared versus submesh-local vertices, index width, declaration source/offset/type/semantic
and bone assignments distinct. Bounds/mesh/skeleton links and manual/generated LOD are separate.

Pose chunks map affected vertices, optional normal deltas and target submesh; LOD can store linked
manual meshes or reduced indices. Some formats/versions alter these layouts; use the retained
version/chunk tables, not one guessed body size. Character morph, hidden-part and mesh-specific
LOD rules are data/research, not arbitrary importer conventions. Unknown chunk/unsupported
construct must fail clearly rather than silently reinterpret vertex bytes.

**Verified** base-file reader checks cover named versions/chunks/poses; that does not establish
support for every exporter or mod. Preserve contradictory stored lengths and documented parse
exceptions where structure is known. Current render conversion does not define the source format.

### Versions in the base game (Verified)

| Header string | Files | Ogre serializer |
| --- | ---: | --- |
| `[MeshSerializer_v1.100]` | 1,740 | `MeshSerializerImpl` (current in Ogre 2.0) |
| `[MeshSerializer_v1.8]` | 1,460 | `MeshSerializerImpl_v1_8` |
| `[MeshSerializer_v1.41]` | 77 | `MeshSerializerImpl_v1_41` (same as 1.8 except pose/morph data) |

Mods also ship `[MeshSerializer_v1.40]` (`MeshSerializerImpl_v1_4`): 2,616 of 13,000 mod meshes in one large
workshop set, all read by `OgreMeshReader` (**Verified**, whole-set parse). Same as 1.41 except the LOD chunk:
no strategy-name string before the level count, and each level's value is the squared distance (Ogre takes its
square root). No 1.40 file in that set has LOD, so that branch follows Ogre's source only.

### Chunks — layout/example

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

### Pose chunks (Verified) — layout/example

```
0xC000 POSES
  0xC100 POSE          string name, ushort target (0 = shared vertices, else submesh index + 1),
                       [1.100 / 1.8] bool includesNormals                     (repeats)
    0xC111 POSE_VERTEX uint vertex index, Vector3 offset, [Vector3 normal if includesNormals]   (repeats)
```

### Poses: character morph targets

| Mesh (`character/meshes/...`) | Poses | Names (examples) |
| --- | ---: | --- |
| `human/human_male.mesh`, `human_female.mesh` | 23, 29 | `wide_cheekbones`, `long_nose`, `big_mouth`, `tiltup_eyes`, `overbite`, `wide_jaw` |
| `bone/bone_male.mesh`, `bone_female.mesh` | 28, 35 | `bone_` + face names, plus `bone_horns_curved`, `bone_horns_top_short`, `bone_horns_bottom_short`, `bone_horns_thick`, ... |
| `stick_person/Stick_person*.mesh` (3) | 9 each | `stick_big_eyes`, `stick_long_antenna`, ... |
| `human/` limb-stump meshes (6) | 23 or 29 | `0` ... `22` / `0` ... `28` |

### Bone assignments

**Verified** (OgreMain `Mesh::compileBoneAssignments` and `Mesh::buildIndexMap`, decompiled): each vertex data's blend indices go through an
index map listing the bones its assignments use, ascending by handle. A vertex with no assignment gets weight 1 on blend index 0 (and 0 on the
other slots), so it follows the lowest used bone fully. Hair is the common case: `FemaleHaircutsSamuraiPony.mesh` leaves 1161 of 2015 vertices
unassigned and uses only the head bone, so they move with the head. Implemented in `Model.Build`.

### Vertex sharing

**Verified** (all 3277 base-game `.mesh` files decoded through `Model.Build`, 2026-10-31): 22.7M vertices for 7.68M triangles
(2.96 per triangle), no shared vertex data, near-unindexed. Byte-identical vertices: 19.2M (2.50/tri). The tangents are per face:
ignoring them leaves 9.0M (1.18/tri); quantising them to 8 bits still leaves 15.6M. FIFO-16 ACMR as stored 2.97 (no reuse); after
exact dedup and a cache-order pass (Tipsify) 2.52. `newland/` (terrain-scale objects) has the most duplication (3.00 → 2.25/tri).
Averaging the tangents of vertices equal in all else (same handedness) moves them 4.1° on average: 56% under 1°, 6.5% by 15° or more
(`newland/`, `items/`, `character/` most). After `Model.Build` (any binormal mode) every normal is unit length, tangent components
and w lie in [-1, 1], weights and colours in [0, 1]. `OgreMeshWeld` (key also includes bone assignments, pose offsets and
handedness) takes all 3277 files from 22.7M to 9.2M vertices (40.5%) and pose entries from 160,616 to 34,212, with every submesh, LOD,
bone-assignment and pose index in range afterwards (scratch check over the install). CPU renders of six normal-mapped building meshes: mean pixel change 0.0–0.3/255.

## Open questions

- How pose weights are derived from appearance values. Observed: body files store values under the pose
  names themselves ([../characters.md](../characters.md#shading-poses-and-lod)); whether Kenshi scales them is Unknown.

## Expression reference

- Pose chunks: ` 0xC000 POSES 0xC100 POSE string name, ushort target (0 = shared vertices, else submesh index + 1), [1.100 / 1.8] bool includesNormals (repeats) 0xC111 POSE_VERTEX uint vertex index, Vector3 offset, [Vector3 normal if includesNormals] (repeats) `
- LOD: `lod1=8000,p,1.0`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Versions in the base game (Verified)
- Pose chunks (Verified)
- LOD (0x8000)
- Other base-game facts (Verified)
