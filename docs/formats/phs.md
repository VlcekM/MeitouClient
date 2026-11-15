# Scythe physics files (`.phs`)

Ragdolls, physics props, building collision and character attachment points. Written by the Scythe
physics editor for **PhysX 2.8** (the game ships `PhysXLoader64.dll`/`PhysXCore64.dll`; the loader fills
`Nx*Desc` structs). Status labels as in [../README.md](../README.md).

Sources: decompilation of `kenshi_x64.exe` (Ghidra 12.1.4): file reader `@ 1401a4ff0` (constructor
`@ 1401a47b0`, default version 1.4), shape reader `@ 1401a48c0`, scale + insert `@ 1407e40a0`
(`PhysicsActual::scytheInsert`), insert `@ 1401b1f20`, actor builder `@ 1401b1610` / `@ 1401a8be0`,
shape-desc fill `@ 1401a7d80`, joint builder `@ 1401a81b0` (common part `@ 1401a7e90`), ragdoll start
`@ 1405b9a10`, ragdoll actor callback `ScytheRagdollPhysicsT` slot 8 `@ 1407e2ae0`, attachment loader
`@ 1407e3b80`. Readers: `Meitou.Data.Physics.PhysicsFile` (whole file; **Verified**: reads all 86 install files to
the end), `Meitou.Data.Characters.PhysicsAttachmentFile` (attachment points).

**Verified** (throwaway parser outside the repo): every `.phs` in the install (86 files, all 1.4 except
`meshes/crate.phs` 1.1) reads with the layout below to exactly the end of file.

## Which files the game uses (Verified, base-game RACE records)

| RACE field | File | Races |
| --- | --- | --- |
| `male ragdoll`, `female ragdoll` | `ragdoll/hu_m_ragdoll_5.phs` | 24 human-skeleton races (both sexes; no race uses `hu_f_*`) |
| same | `ragdoll/hu_m_ragdoll.phs` | Cannibal, Fishman, Cannibal Skav, Alpha/King Fishman |
| `male carry ragdoll`, `female carry ragdoll` | `ragdoll/hu_m_ragdoll_2.phs` | all 30 human-skeleton races |
| `male ragdoll`, `female ragdoll` | `animal/ragdoll/<x>.phs` | one file per animal: bull, cage_beast, dog1, elephant_turtle, flesh_insect, goat, gorilla, landbat, mammal_beak, pack_beast, robot_worker, skimmer, swamp_duck, whale2 |
| `attachment points` | `ragdoll/attachments-weapons.phs`, `attachments-bonedog.phs` | |

Other `.phs` fields: CHARACTER_PHYSICS_ATTACHMENT `file male`/`file female` (lanterns), BUILDING_PART
`phs or mesh`. The game picks `female ragdoll` when the character is female (`@ 1405d0db0`).

## Layout (Verified by reading every file to EOF)

Little-endian; `string` = `int32 length` + bytes (no terminator); `vec3` = 3 floats; `bool` = 1 byte.
The editor writes several blocks as raw memory, so unused fields hold heap fill (`0xBAADF00D`,
`0xCDCDCDCD`) or stale values: treat everything not listed as used as garbage.

```
float   version                         gates fields below
int32   actorCount, Actor[actorCount]
[v >= 1.4] int32 n, Point[n]            models: the mesh(es) the file belongs to (ragdolls: one, e.g. the skeleton mesh)
int32   n, Joint[n]                     n × (v >= 1.2 ? 0x264 : 0x138) bytes
int32   n, n × 0x38c bytes              never non-empty in the install
int32   n, Shape[n]                     loose shapes (never non-empty)
int32   n, Point[n]                     loose points
int32   n, n × 0x7c bytes               loose lights (never non-empty)

Actor:
  int32  shapeCount, pointCount, lightCount, unknown
  vec3   position                       model space; the bone position in most ragdolls (not all, see below)
  float  garbage
  float  unknown
  bool   static                         nonzero: no body (static actor; joints treat it as the world)
  float  density                        NxActorDesc.density (used when mass is 0)
  float  angularDamping / 10            game multiplies by 10
  float  linearDamping
  32 bytes                              copied into the actor's points; zero in all files
  float  mass                           NxBodyDesc.mass
  float  sleepLinearVelocity, sleepAngularVelocity   (-1 = SDK default)
  vec3   centre of mass (actor local)
  36 bytes                              garbage in ragdolls, identity 3x3 in props; not read by the game
  [v >= 1.1] bool  use centre of mass  applies the vec3 above (NxActor setter)
  [v >= 1.2] string name, bool (sets NxActorDesc flag 8), bool disableGravity
  [v >= 1.3] int32 solverIterationCount (game raises it to at least 16)
  [v >= 1.4] int32 model                index into the model points, -1 none
             string bone, vec3 bonePosition, vec3 axisX, axisY, axisZ   bone binding pose, model space
             bool  unknown              1 only on the root actor (`Bip01`) of the human ragdolls
  Shape[shapeCount], Point[pointCount], lightCount × 0x7c bytes

Shape (reader @ 1401a48c0):
  vec3 axisX, axisY, axisZ, position    model space; axes are rotation-matrix columns
  int32 type                            see below
  32 bytes                              zero
  int32 unknown (0)
  vec3 size                             see below
  float unknown (0)
  float mass, density, skinWidth        NxShapeDesc; skinWidth -1 = SDK default
  string cookedName                     e.g. data\...\cooked\barrel01-hull-0
  [v >= 1.2] string cookedFile          same + .bin: the cooked PhysX mesh the game loads
  bool unknown, bool hasMesh
  if (v >= 1.2 ? type == 9 : hasMesh):
    bool convex, int32 vertexCount, int32 triangleCount,
    vec3[vertexCount] vertices, vec3[vertexCount] (second array, normals?), uint16[3·triangleCount]
    [v >= 1.2] 2 bytes (garbage)
  [v >= 1.2] bool flag (shape flag 0x100), 3 bytes unknown

Point (attachment point / model):
  vec3 axisX, axisY, axisZ, position    columns + position, model space
  int32 garbage, 32 bytes
  string mesh, vec3 scale

Joint (raw 0x264-byte struct; offsets):
  0x00 vec3 anchor (model space)   0x0c vec3 normal   0x18 vec3 axis   0x24 vec3 normal × axis (unused)
  0x30 int32 type
  0x58 float high limit / twist high (degrees)   0x5c float low limit / twist low   0x60 float swing limit
  0x68 bool limits enabled
  0x6c int32 actor0, 0x70 int32 actor1        actor indices, -1 = world
  0x74 float maxForce, 0x78 float maxTorque   used only when > 0 (breakable)
  0x7c bool collision between the two actors
  0x80 int32 unknown (4)   0xe8 float unknown (0.4)
  0xdc int32 projection (1 = NX_JPM_POINT_MINDIST; 0 with distance and angle 0 in every joint of the human ragdoll: none), 0xe0 distance, 0xe4 angle
  0xf8 bool motor (revolute): 0xfc velocity × 1e5, 0x100 max force × 1e5
  0x108 bool spring 1: 0x10c target, 0x110 spring, 0x114 damper
  0x118 bool spring 2: 0x11c target, 0x120 spring, 0x124 damper
  0x128 bool spring 3: 0x12c target, 0x130 spring, 0x134 damper
  0x138 char[300] name ("Joint"; v < 1.2 records end at 0x138)
  everything else: garbage
```

The 0x7c records are lights (**Observed**, `packbeast_pole_simple.phs`: position, an orientation, colour
255 255 255, a range 100); the game hands them to the insert target. The 0x38c record is **Unknown**.

## Building the PhysX objects (Verified, decompiled)

- **Scale**: before building, actor positions, shape positions and joint anchors are multiplied by the
  insert scale (per axis); shape sizes by it as noted below. For ragdolls the scale is the race's scale
  (the character's virtual `0x390`, 1 for humans), group 3 or 4 (`@ 1405b9a10`).
- **Actors** get the insert rotation as orientation (they have none of their own) and position
  `R·position + T`. A non-static actor gets a body: mass, angular damping × 10, linear damping,
  sleep velocities, solver iterations (≥ 16), body flags `0x900` (+ disable gravity), sleep energy 0.05,
  wake-up counter 0.4. Only actors with shapes get a PhysX actor (points are still handed over); one
  named `root` is made kinematic.
- **Shapes**: local pose = (axes, position − actor position) with the axes as matrix columns.
  `type` 4 sphere: radius = size.x/2 · scale.x. 5 capsule (along local Y): radius = size.x/2 · scale.x,
  height (between cap centres) = size.y · scale.z. 9 mesh: `cookedFile` loaded as a convex mesh when
  `convex` (all 86 mesh shapes), otherwise as a triangle mesh; the embedded vertices are not used.
  Any other type (3; also 6 in two lantern files) box: half-extents = size/2 · scale.
  Shape group = insert group, material from the game, mass/density/skinWidth from the file.
- **Joints** need at least one non-static actor; a static or -1 actor means the world. Anchor
  = `R·anchor + T`; normal and axis are rotated by `R` and used as both actors' local normal/axis (the
  actors are axis-aligned with `R`), then the global anchor is set. Types: 1 revolute, 2 spherical,
  3 cylindrical, 4 prismatic, 5 fixed, 6 D6, 7 point-on-line; others are skipped.
  - Revolute: limits enabled → low = 0x5c, high = 0x58 (degrees → radians, restitution 0, hardness 1);
    spring 1 → spring/damper, target in degrees; motor as above; projection distance + angle.
  - Spherical: limits enabled → twist low = 0x5c, twist high = 0x58, swing = 0x60 (degrees → radians);
    springs 1/2/3 → twist/swing/joint spring (targets are multiplied by 57.29578, i.e. treated as radians
    → degrees); projection distance.
  - D6 reads 0x58/0x5c and spring 3 into its own fields (**Unknown** which; no ragdoll uses D6).
- **Bones**: each created actor is registered in a map under its **bone name** (`@ 1401a8be0`); the
  ragdoll binds bodies to skeleton bones through it. The stored bone transform is read only by the
  attachment loader, not by the physics build.
- **Ragdoll actors** (`ScytheRagdollPhysicsT` slot 8): set NxActor virtuals `0x1a0`/`0x1b0` to 0.5
  (**Observed**: likely linear/angular damping) and raise body flag 0x80 (kinematic) — the ragdoll
  starts driven by the animation.

## Ragdoll content (Verified against the files)

- Units are skeleton model space (Kenshi world units, Y up). In `hu_m_ragdoll_5.phs` every actor's
  bone transform equals `male_skeleton.skeleton`'s binding pose (≤ 0.0003 units, rotations exact) and
  `position` equals the bone position. `hu_m_ragdoll`/`_2` and most animal files match their skeletons
  exactly; cage_beast, goat, pack_beast, whale2, mammal_beak differ for some bones (older skeleton).
- One actor per bone, one shape each, actor name = bone name; one model point (the preview mesh, e.g.
  `man5.mesh`). Joints: mostly actor0 = parent body, actor1 = child, anchor near the child's bone
  origin (`hu_m_ragdoll_5` ≤ 0.19 units, others up to 0.7). Exceptions: `hu_m_ragdoll_5`
  `Spine1→Bip01` and `Head→Neck` (actor1 is the parent); `hu_m_ragdoll_2` joins both thighs to Spine1.
  `hu_m_ragdoll` has one joint with both actors -1 (skipped).
- Limb capsules span the bone: centre at mid-bone, `size.y` = bone length.

`hu_m_ragdoll_5.phs` (all human races, both sexes). Every actor: mass 1, density 0, angular damping 0.5,
linear 0, solver 4. Sizes in skeleton units (full box size / sphere diameter / capsule diameter × height).

| Actor (bone) | Shape | Size |
| --- | --- | --- |
| Bip01 | box | 2.345 × 2.081 × 2.105 |
| Bip01 L/R Thigh | capsule | Ø1.5 × 4.394 |
| Bip01 L/R Calf | capsule | Ø1.5 × 4.608 |
| Bip01 Spine1 | box | 2.25 × 2.254 × 2.225 |
| Bip01 Spine2 | box | 2.68 × 2.929 × 2.415 |
| Bip01 L UpperArm / R UpperArm | capsule | Ø1.075 / Ø1.1 × 2.849 / 2.699 |
| Bip01 L Forearm / R Forearm | capsule | Ø0.935 / Ø0.765 × 3.244 |
| Bip01 Neck | capsule | Ø1.335 × 1.029 |
| Bip01 Head | sphere | Ø2.3 |

| Joint (actor0 → actor1) | Type | Limits (degrees) |
| --- | --- | --- |
| Bip01 → L Thigh | revolute | −130 … 50 |
| Bip01 → R Thigh | revolute | −157 … 51 |
| L Thigh → L Calf, R Thigh → R Calf | revolute | 0 … 160, 0 … 154 |
| Spine1 → Bip01 | revolute | −37 … 37 |
| Spine1 → Spine2 | revolute | −30 … 30 |
| Spine2 → Neck | revolute | −30 … 25 |
| Spine2 → L/R UpperArm | spherical | twist −90 … 90, swing 170 |
| L UpperArm → L Forearm, R → R | revolute | −152 … 0, −144 … 0 |
| Head → Neck | revolute | −10 … 39, spring 10000 (damper 0, target 0) |

All limits enabled, no projection, no breaking, no joint collision. `_nolimits` differs only in the
limits-enabled bytes (Verified by diff); `_nojoints` has no joints.

## How Kenshi uses attachment points (Verified, decompiled)

`@ 1407e3b80` turns every point of every actor into an attachment named after the **actor** (`hip`,
`back`...), on the actor's bone, with offset

- position = inverse(bone rotation) × (point position − bone position)
- rotation = inverse(bone rotation) × point rotation

using the bone transform **stored in the file** (Ogre `Matrix3::SetColumn` +
`Quaternion::FromRotationMatrix`, `@ 1407e2350`). These offsets are what `Entity::attachObjectToBone`
later gets ([../characters.md](../characters.md#assembly-and-generation)).
`CharacterAppearanceTests.Weapon_attachment_points_match_the_male_skeleton` checks
`attachments-weapons.phs` (actors `hip` on `Bip01 Pelvis`, `back`/`back2` on `Bip01 Spine2`) against
`male_skeleton.skeleton` within 0.01 units.
`attachments-bonedog.phs` (v1.4) has one actor, `mouth` on `Bip01 Ponytail1`, one point (**Verified**: read with
`PhysicsAttachmentFile` from the install). Only RACE Bonedog and Boneyard Wolf name it; the other 28 races with
`attachment points` name `attachments-weapons.phs` (no `mouth`) (**Verified**, merged base data). Its use: a held limb
([character-stats](../game/character-stats.md#eating), "Mouth").

## Unknown

- How the running ragdoll maps body poses to bones after the switch to dynamic (offsets from the live
  skeleton or the bind pose), and which joint end PhysX treats as parent for the reversed joints.
- Actor fields marked unknown, the 32-byte blocks, joint 0x80 / 0xe8, shape tail bytes, the 0x38c
  record, D6 field mapping, and what Kenshi does with lights and `root`-actor callbacks.
- Mass at runtime: the files give each human body mass 1 (cannibal file: density 1, mass 0); whether
  the game rescales masses later was not traced.
