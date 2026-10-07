# Collision shapes (PhysX `.xml` / `.bin`)

Where the static collision of buildings, foliage and other placed objects comes from, how it is placed in the world, and
which collision groups the game gives it. The navmesh generator reads its building geometry from these shapes
([../game/pathfinding.md](../game/pathfinding.md#generator-runtime-and-tool-time-navmesh-building)), so they are the input
our own navmesh builder needs. Status labels as in [../README.md](../README.md).

Sources: the install's files (scanned with a probe outside the repo, `MeitouClient-re/probes/walk/probe`), `fcs.def`, and
decompilation of `kenshi_x64.exe` (Ghidra dump; function names as in the dump). Kenshi links **PhysX 2.8** (`PhysXLoader64.dll`,
`PhysXCore64.dll`, `NxGetUtilLib`) and NVIDIA's **NxuStream** library (`NXU::` classes in the RTTI). Ragdolls and
attachment points use Scythe `.phs` files instead ([phs.md](phs.md)).

## Which file a building part uses

| Field (`fcs.def`) | Record | Meaning |
|---|---|---|
| `xml collision` | BUILDING_PART | the part's static collision, a PhysX NxuStream XML file |
| `destroyed collision` | BUILDING_PART | replaces `xml collision` while the building is destroyed (and the part has a `destroyed mesh`) |
| `phs or mesh` | BUILDING_PART | the **render** mesh; collision never comes from it |
| `interior mask` | BUILDING | a part whose `xml collision` is the indoor trigger hull |
| `destroyed boundary` | BUILDING | navmesh cutter for a destroyed interior (PhysX XML) |
| `collision` | FOLIAGE_MESH | the foliage piece's collision (XML; `.phs` allowed by the schema, none used) |
| `physics file` | items | dropped-item physics (not used by the navmesh) |

- **Verified** (probe over the base game's 603 BUILDING records and the 1,019 distinct parts they reach through `parts`
  and `interior`, recursively): 546 parts name an `xml collision` (12 of them name one of 3 files that are not in the install, with no `.bin` either:
  two Moor house upper floors and a Vast factory floor trigger), 7 a
  `destroyed collision`; 1,012 parts name a `.mesh` in `phs or mesh` and 1 a `.phs`. All 59 `interior mask` parts have an
  `xml collision`. FOLIAGE_MESH: 354 of 748 have a `collision` file, all `.xml`.
- **Verified** (`Zones_PlacePartMesh`, FUN_14055f6e0): only `xml collision` / `destroyed collision` create collision
  (a physics loader object, FUN_1400f3ce0). A part without one gets only a `PhysicsCollection::StaticEnt` or
  `RotatingEnt` for its render entity, and none of their virtual functions calls PhysX (checked: they only touch Ogre
  nodes and materials), so **parts without `xml collision` are invisible to the navmesh**, whatever the building's
  `path mode`. The `phs or mesh` file must contain
  `.mesh` or nothing is created (as [zones.md](zones.md#placing-a-part) says), so the one `.phs` part draws nothing.
- Buildings by `path mode` and whether any exterior part has an `xml collision` (**Verified**, probe):

  | path mode | with collision | parts but no collision | no parts |
  |---|---:|---:|---:|
  | 0 IGNORE | 61 | 29 (tents, farms, carpets) | 2 |
  | 1 PROJECTED | 83 | 0 | 1 |
  | 2 OBSTACLE | 259 | 29 (signs, banners) | 16 |
  | 3 WALKABLE | 122 | 1 | 0 |

## File format: NxuStream2 XML (Verified)

- Root `<NXUSTREAM2>`, one `NxuPhysicsCollection` (`sdkVersion="285"`, `nxuVersion="103"`), written by NVIDIA's 3ds Max
  PhysX plug-in: the 12 `.PxProj` files are that plug-in's project files (`PhysicsExport SDK="2.8.5"`,
  `LengthUnit Type="centimeters"`), not read by the game. 1,125 NXU XML files in `data/` (outside `gui/` and `editor/`).
- Content: `NxParameterDesc` lines, `NxConvexMeshDesc` / `NxTriangleMeshDesc` (mesh data, referenced by id), one
  `NxSceneDesc` (gravity `0 0 -981`, i.e. Z up in the exporter) holding materials and `NxActorDesc` entries. Each actor
  has `globalPose` and one or more shapes, each with an `NxShapeDesc` holding `localPose`, `group`, `groupsMask`, flags.
- Shapes over all 1,125 files: Box 2,026 (`dimensions` = half extents), Convex 1,239, TriangleMesh 376, Capsule 245
  (`radius`, `height`), Plane 91, Sphere 3. No shape has a trigger flag; 55 shapes sit on actors with a body (`hasBody`),
  the rest are static. The files' own `group` and `groupsMask` values are not what the game filters on (the game assigns
  a group per placed object, below).
- **Poses**: 12 numbers, a 3 × 3 rotation written **row by row**, then the translation. A point p of a shape is
  `globalPose × (localPose × p)` in exporter space (Z up, centimetre-named units that are Kenshi units in practice).
- **Mesh data is cooked only**: every convex and triangle mesh has empty `<points>` / `<triangles>` and a hex
  `<cookedData>` blob (PhysX 2.8 cooking output). Layouts as far as a builder needs them (**Verified**: the probe decodes
  all 1,615 meshes, every vertex finite and every triangle index below the vertex count, and the decoded hulls agree with
  the render meshes, see Placement):
  - Triangle mesh `NXS\x01MESH`: int32 version (1), int32 flags, float convex-edge threshold, int32 height-field axis
    (0xFF), float height-field extent, int32 vertex count, int32 triangle count, then the vertices (float32 × 3 each),
    then 3 indices per triangle, 8-bit when flags & 0x08 (302 meshes, flags 0x0A) and 16-bit when flags & 0x10
    (74 meshes, flags 0x12); 32-bit presumably otherwise (none in the base game). The rest (material indices, the
    collision tree) is not needed.
  - Convex mesh `NXS\x01CVXM` (version 3) holds sub-blocks tagged `ICE\x01`; the `CVHL` block (version 5) starts with
    int32 version, int32 vertex count, five more int32 (edge, polygon and index counts, not decoded), then the hull
    vertices (float32 × 3 each). The convex hull of these points is the shape.
- **`.bin` files** (**Verified**): 862 files in `data/`, all start with `NXUSTREAM` (NxuStream's binary form). They are
  the game's conversion of an XML (`PhysicsActual::convertXMLToBin`, FUN_1407e5ba0): named after the XML's base name, with
  `_<scale>` appended when the BUILDING `scale` is not 1 (`Cannibal Camp Big Building_1.5.bin`, `loom_0.6.bin`), the scale
  baked into the shapes. When the `.bin` exists it is loaded instead of the XML (`SimplePhysXEntity::createBT`,
  FUN_1404cd4f0, **Observed**). The XML is the source of truth; a `.bin` is a cache (678 XML files have one).
- When converting, a non-unit scale (sx, sy, sz) given in Ogre axes is applied to the exporter-space data as
  (sx, sz, sy), i.e. Y and Z swapped (FUN_1407e4410, **Observed**); buildings only use uniform scale.

## Placement in the world

- Exporter space to Ogre/Kenshi space: **(x, y, z) → (x, z, −y)** (a −90° rotation about X). **Verified** by the probe: for
  398 parts with a mesh, an `xml collision` and no part offset, the collision AABB (row-major poses, box corners, capsule
  extents, decoded hull vertices) was compared with the mesh's bounds under 12 conventions (row or column order × six
  axis maps). This one wins clearly: mean IoU 0.63, median 0.70, 275 of 398 above 0.5; the next best (column order,
  same map) 0.53, all others ≤ 0.43. Collision hulls are simplified, so IoU 1 is not expected.
- The collision is placed at the **building's** scene node: building position and orientation, building `scale`
  (uniform). Part `offset X/Y/Z` moves the render mesh ([zones.md](zones.md#placing-a-part)) but not the collision: the
  loader takes the building node's position and orientation (FUN_14055f6e0, **Observed**). 13 parts have both an offset
  and an `xml collision` (Verified count); how they look in game is not checked.
- Doors: a door is its own building ([zones.md](zones.md#doors)); its collision uses the door building's node and the
  parent building's scale (**Observed**).
- So a shape point p lands at `building.position + building.rotation × (scale × C(globalPose × (localPose × p)))` with
  C(x, y, z) = (x, z, −y).

## Collision groups (Observed, FUN_14055f6e0 and the loaders)

The game puts every collision shape in a PhysX collision group. The navmesh passes select shapes by group mask
(PhysX `overlapAABBShapes`, wrapper FUN_1403c41c0, which also grows its result buffer by 512 and logs "Too many shapes
found"), so the groups are part of the navmesh rules.

| Group | What |
|---|---|
| 5 | door parts (a building with the door flag) |
| 6 | foliage collision, FOLIAGE_MESH `walkable` false (Foliage_PlaceOne FUN_1406d2410) |
| 9, 10, 11, 12 | ordinary building parts, `building floor` 0, 1, 2, 3 (4 and up use 9, Observed in code: the switch only knows 1..3) |
| 13 | the `interior mask` part's collision (indoor trigger hull, FUN_140553870) |
| 14 | walkable foliage (`walkable` true), foliage-placed objects with an owner, and other `collision` users (FUN_140399ac0) |
| 15, 16, 17, 18 | `is stairs` parts, floor 0..3 |
| 19, 20, 21, 22 | parts of `is exterior furniture` / `is interior furniture` buildings, by the building's floor |
| 23, 24, 25, 26 | `passable` parts and every part of a `path mode` IGNORE building, by the building's floor (also FUN_1400dfb80: floor + 23) |
| 27 | `is unwalkable roof` parts |
| + 14 | added while the building is under construction and its progress is below `build threshold` × the total (so floor-0 parts become 23, like passable ones) |
| 3, 4 | other physics objects (items, FUN_1407626e0, FUN_1407e7460); not used by the navmesh |

The floor used is the part's `building floor`, or the building's own floor for furniture and for IGNORE/passable parts.
FUN_14055f6e0 also reads a bool `no collision` from the BUILDING (not in `fcs.def`, so normally false) into a flag of the
loader object; its effect is **Unknown**.

## Shape to triangles (Observed, FUN_1403c1cf0)

How the navmesh generator turns one shape into triangles (a builder should do the same to match):

| PhysX type | Triangles |
|---|---|
| 2 box | its 8 corners and 12 triangles (PhysX utility functions) |
| 3 capsule | a 16-sided prism around the capsule's axis: radius r, half length h/2 + r, no caps rounding (32 vertices) |
| 5 convex | the hull (FUN_1403c0c00) |
| 6 triangle mesh | the mesh's triangles (FUN_1403c0e80) |
| 0 plane, 1 sphere, 7 height field | nothing |

All vertices are made relative to the generation job's origin and multiplied by 0.1 (Havok space). Each triangle carries
the material the caller gives (see pathfinding.md).

## Unknown

- What the five undecoded `CVHL` counts are (not needed for the hull); triangle meshes with 32-bit indices.
- Whether the game itself drops the 91 plane shapes from the physics scene (the navmesh ignores them either way).
- How the 13 parts with an offset and a collision line up in the game.
- The effect of the BUILDING bool `no collision`.
- The `.bin` format beyond its header (we read the XML).
