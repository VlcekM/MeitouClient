# Collision shapes

Clean-room PhysX/NxuStream facts; never link SDKs. **Verified** reader/probe checks1136 XML and
1711 cooked meshes for finite vertices/valid indices; AABB comparison checks placement axes.
`Meitou.Data.Physics`; [navmesh](../game/pathfinding.md), [zones](zones.md).

## XML, cooked data and cache

NXUSTREAM2/NxuPhysicsCollection sdk285/nxu103; Max .PxProj is exporter project, not loaded.
NxSceneDesc actors/globalPose/shapes/localPose/materials/groups; poses row-major3x3 then
translation. Cooked mesh descriptions also carry an `NxSimpleTriangleMesh` with empty points/triangles (**Verified**: vanilla `data/buildings`, `CollisionInstallTests`). A description with empty cookedData is read from those plain lists instead (Building Collision Fix: the Longhouse base and stairs; **Observed**: without it the Longhouse had no ground floor in its interior navmesh). Box dimensions half extents,
capsule radius/height, plane/sphere and mesh IDs retained. Exporter Z-up/gravity(0,0,-981).

Triangle blob `NXS 01 MESH`: version1,flags,convex-edge threshold,heightfield-axis0xff,
heightfield extent,vertex-count,triangle-count, floatXYZ vertices,3indices/triangle.
Flags0x08→u8,0x10→u16; u32 fallback inferred, not seen. Convex `NXS 01 CVXM` v3 contains
ICE01 blocks; CVHLv5 hasversion,vertex-count,five count ints, then hull floatXYZ. Build hull;
remaining collision tree/material indices not needed for navigation. Unknown blocks must not
be interpreted as another layout.

862 bin files startNXUSTREAM; converted XML cache (scale suffix/baked shape scale), prefer existing
bin in original loader. XML is source of truth. Nonuniform Ogre scale maps(sx,sz,sy) to exporter.

## Placement and groups

**Verified** C(x,y,z)=(x,z,-y) wins398-part AABB comparison. World point=building.position+
building.rotation*(scale*C(globalPose*(localPose*p))). **Verified** for a shape with both poses translated (`ShieldBridge_COL.xml`: walkboard triangle
mesh with localPose translation (845, 124, 0), boxes with globalPose translations; placed tilted at 47.16): the original tile's deck polygons lie at the
walkboard's deck height to 0.1 ([pathfinding](../game/pathfinding.md), shells). Collision uses BUILDING node, not render
part offset (**Observed** loader); door uses its own node and parent's scale. File groups are
not final game groups: loaders assign per part/building/foliage, nav mask0x809de40. Group table
and shape-to-triangle branches retain floors/stairs/doors/ground/obstacle distinctions. Do not
use every rendered triangle as collision or infer PhysX actor dynamics from appearance.

### Which file a building part uses

| Field (`fcs.def`) | Record | Meaning |
|---|---|---|
| `xml collision` | BUILDING_PART | the part's static collision, a PhysX NxuStream XML file |
| `destroyed collision` | BUILDING_PART | replaces `xml collision` while the building is destroyed (and the part has a `destroyed mesh`) |
| `phs or mesh` | BUILDING_PART | the **render** mesh; collision never comes from it |
| `interior mask` | BUILDING | a part whose `xml collision` is the indoor trigger hull; its `phs or mesh` is the render mask that cuts terrain, foliage and map features ([render-interior-cut](../render-interior-cut.md)) |
| `destroyed boundary` | BUILDING | navmesh cutter for a destroyed interior (PhysX XML) |
| `collision` | FOLIAGE_MESH | the foliage piece's collision (XML; `.phs` allowed by the schema, none used) |
| `physics file` | items | dropped-item physics (not used by the navmesh) |

- **Verified** (probe over the base game's 603 BUILDING records and the 1,019 distinct parts they reach through `parts`
  and `interior`, recursively): 546 parts name an `xml collision` (2 of them name a file that is not in the install, `moorhouse01-upper.xml` and `moorhouse01-upper2.xml`, with no `.bin` either; an earlier scan counted 12 parts and 3 files, probably a case-sensitive lookup, see "Reader" below:
  the two Moor house upper floors), 7 a
  `destroyed collision`; 1,012 parts name a `.mesh` in `phs or mesh` and 1 a `.phs`. All 59 `interior mask` parts have an
  `xml collision`. FOLIAGE_MESH: 354 of 748 have a `collision` file, all `.xml`.
- **Verified** (`Zones_PlacePartMesh`, FUN_14055f6e0): only `xml collision` / `destroyed collision` create collision
  (a physics loader object, FUN_1400f3ce0). A part without one gets only a `PhysicsCollection::StaticEnt` or
  `RotatingEnt` for its render entity, and none of their virtual functions calls PhysX (checked: they only touch Ogre
  nodes and materials), so **parts without `xml collision` are invisible to the navmesh**, whatever the building's
  `path mode`. The `phs or mesh` file must contain
  `.mesh` or nothing is created (as [zones.md](zones.md#placement-files-and-assembly) says), so the one `.phs` part draws nothing.
- Buildings by `path mode` and whether any exterior part has an `xml collision` (**Verified**, probe):

| path mode | with collision | parts but no collision | no parts |
  |---|---:|---:|---:|
  | 0 IGNORE | 61 | 29 (tents, farms, carpets) | 2 |
  | 1 PROJECTED | 83 | 0 | 1 |
  | 2 OBSTACLE | 259 | 29 (signs, banners) | 16 |
  | 3 WALKABLE | 122 | 1 | 0 |

- Farms (**Verified**, probe of the loaded BUILDING records named "Farm"): every outdoor farm (all crops, S to XL) has one
  part, `farm posts` (`farm_poles1.mesh`, bounds about 103 x 150, the same for every size), with no `xml collision`; the indoor
  farms (`indoor_farm1_part`) have collision.

### Collision groups (Observed, FUN_14055f6e0 and the loaders)

| Group | What |
|---|---|
| 5 | door parts (a building with the door flag; Meitou: any `doors`-list building) |
| 6 | foliage collision, FOLIAGE_MESH `walkable` false (Foliage_PlaceOne FUN_1406d2410) |
| 9, 10, 11, 12 | ordinary building parts, `building floor` 0, 1, 2, 3 (4 and up use 9, Observed in code: the switch only knows 1..3) |
| 13 | the `interior mask` part's collision (indoor trigger hull, FUN_140553870) |
| 14 | walkable foliage (`walkable` true), foliage-placed objects with an owner, and map features (`MapFeature`, FUN_140399ac0, **Verified**) |
| 15, 16, 17, 18 | `is stairs` parts, floor 0..3 |
| 19, 20, 21, 22 | parts of `is exterior furniture` / `is interior furniture` buildings, by the building's floor |
| 23, 24, 25, 26 | `passable` parts and every part of a `path mode` IGNORE building, by the building's floor (also FUN_1400dfb80: floor + 23) |
| 27 | `is unwalkable roof` parts |
| + 14 | added while the building is under construction and its progress is below `build threshold` × the total (so floor-0 parts become 23, like passable ones) |
| 2 | the terrain (Observed: the ground-height rays' masks `0x204` and `0x4004` pair it with floors 9 or foliage 14; the interior-ambience ray `0x2004` is 2 + 13, [audio.md](../audio.md#interiors)) |
| 3, 4 | other physics objects (items, FUN_1407626e0, FUN_1407e7460); not used by the navmesh |

The character's ground ray (`FUN_14065f160`, **Verified**: from 20 units above the feet, straight down) uses mask `0x7de04` (groups 2, 9..12, 14, 15..18) outdoors and `0x7f9e00` (groups 9..12, 15..22) when the
character is in a building; the hit's group and BUILDING decide the footstep surface (`interior material` / `exterior material`, [audio](../audio.md#characters-footsteps-combat-voices)). No per-shape physics material is
consulted (`FUN_14065df80` reads only the group and the hit object).

### Shape to triangles (Observed, FUN_1403c1cf0)

| PhysX type | Triangles |
|---|---|
| 2 box | its 8 corners and 12 triangles (PhysX utility functions) |
| 3 capsule | a 16-sided prism around the capsule's axis: radius r, half length h/2 + r, no caps rounding (32 vertices) |
| 5 convex | the hull (FUN_1403c0c00) |
| 6 triangle mesh | the mesh's triangles (FUN_1403c0e80) |
| 0 plane, 1 sphere, 7 height field | nothing |

## Item physics

Items with `has collision` true get a PhysX object in `FUN_1407626e0` (entity callback) at the item's pose: position `+0x48`, orientation `+0xb0` (**Verified**, decompile). A WEAPON or a severed item (`+0x1da`, below) whose `physics file` contains `.xml` (`FUN_140132570` on `DAT_14170f424`) gets a `SimplePhysXEntity` from that file; any other a static box the size of its mesh's bounds, centred on them (the mesh: `ground mesh`, else `mesh`, else `steel1.mesh`, `FUN_140760300`; a weapon that exists bare takes its `bare sword` for `mesh`, not modelled: Meitou uses `mesh`) (`StaticBoxEntity`, built by `FUN_1404ccf40` through `FUN_1407e38c0`). Without `has collision` none, so the mouse ray cannot hit it. Base data (**Verified**, probe over the merged records): all but 10 items have collision (2 rugs, 8 limb stumps and legs); 29 of 48 weapons name an `.xml`. Meitou picks a ground item by these shapes where it draws it (`GameHost.GroundShape`; engine choice: a collision file missing from the install falls back to the box); a severed limb by its file only while its fall is known, one loaded from a save by its box, as in the original (**Verified**: only `FUN_14075d9b0` sets `+0x1da`, the item save and load leave it, and `FUN_14036f950` activates loaded items with it false).
- Placed items (`+0x1d9` 0): raised, then a ray down (`FUN_1409b6a90`, mask `0xf07f9e04`, or `0x7f9e04` when `+0x13c` is 2) turns the up axis to the ground normal (**Verified**). Groups 28–31 (**Observed**).
- Severed items (`+0x1d9` and `+0x1da` set by the sever, `FUN_14075d660` and `FUN_14075d9b0`): no ground snap, so they keep the bone's pose; group 3, or 4 when the item type is not 0xb (**Verified** the value written to the descriptor's `+0x70`; **Observed** that it is the group). The object is dynamic: the 4th argument of `FUN_1407e30d0` is `+0x1da == 0`, false here, and `FUN_1404cde30` sets its `+0x10` flag from it (**Observed**).
- No velocity is written on this path (**Observed**: none in `FUN_1407626e0`, `FUN_1404cde30`, `FUN_14075e5d0`, `FUN_140763fa0`). The NxuStream actor loader is not read: its flags and any start velocity are **Unknown**.
- The 28 `Severed …` ITEM records of rebirth.mod (`item function` 17, `persistent` 0, `weight kg` 1, `has collision` true; **Verified**) all name `data/character/meshes/human/arm_mesh_collision.xml` (**Verified**): one `NxActorDesc` "Capsule" with a body, capsule radius 0.691524 and height 5.464595, local pose rows `0 -1 0`, `0 0 -1`, `1 0 0` with translation `2 0 0`, body `mass` 1 and `angularDamping` 0.1. Values used as the file gives them (**Observed**: no unit conversion, as in the ragdoll files).
- `persistent` 0 returns from `FUN_14075e5d0` before the parameter toggle (`FUN_140336d10`); 1 to 3 select a toggle or a position copy (control flow **Verified**; meaning **Unknown**).
- Scene gravity 98.1 and the sleep thresholds are those of the ragdoll scene ([ragdolls](../game/ragdolls.md)); item material index 0 (**Observed** as the default material).

## Expression reference

- File format: NxuStream2 XML: `sdkVersion="285"`; `nxuVersion="103"`; `PhysicsExport SDK="2.8.5"`; `LengthUnit Type="centimeters"`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- File format: NxuStream2 XML (Verified)
- Collision groups (Observed, FUN_14055f6e0 and the loaders)
- Shape to triangles (Observed, FUN_1403c1cf0)
