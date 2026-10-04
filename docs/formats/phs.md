# Scythe physics files (`.phs`)

Ragdolls, physics props and character attachment points (RACE `attachment points`, `male ragdoll`...,
CHARACTER_PHYSICS_ATTACHMENT `file male`). Written by the Scythe physics editor. Status labels as in
[../README.md](../README.md).

Sources: decompilation of `kenshi_x64.exe` (Ghidra 12.1.4): the file reader `@ 1401a4ff0` (its constructor
`@ 1401a47b0` sets the default version 1.4), and the attachment-point loader `@ 1407e3b80`. Reader in
`Meitou.Data.Characters.PhysicsAttachmentFile` (attachment files only).

## Layout (Verified for attachment files)

Little-endian; `string` = `int32 length` + that many bytes (no terminator in the files seen; the game
copies them into fixed buffers). `vec3` = 3 floats.

```
float   version              1.4 in attachments-weapons.phs; fields below are gated by it
int32   actorCount
Actor   actors[actorCount]
[v >= 1.4] int32 n, AttachPoint[n]      points not owned by an actor
int32 n, n × (v ≥ 1.2 ? 0x264 : 0x138) bytes   joints
int32 n, n × 0x38c bytes
int32 n, n × Shape                     loose shapes
int32 n, AttachPoint[n]
int32 n, n × 0x7c bytes

Actor:
  int32  shapeCount, pointCount, extraCount, (int32)
  vec3   position
  4 + 4 + 1 + 4 + 4 + 4 + 32 + 4 + 8 + 12 + 36 bytes   physical properties (not decoded)
  [v >= 1.1] 1 byte
  [v >= 1.2] string name, 2 bytes                       ("Actor" before 1.2)
  [v >= 1.3] int32                                      (default 4)
  [v >= 1.4] int32, string bone, vec3 bonePosition, vec3 axisX, vec3 axisY, vec3 axisZ, 1 byte
  Shape[shapeCount]                                    size depends on the shape (reader @ 1401a48c0, not decoded)
  AttachPoint[pointCount]
  extraCount × 0x7c bytes

AttachPoint:
  vec3 axisX, axisY, axisZ    rotation matrix columns (model space)
  vec3 position               model space
  4 + 32 bytes
  string mesh                 the mesh the editor previews there (e.g. katana04F.mesh)
  vec3 scale
```

The axis triples are the **columns** of a rotation matrix, turned into a quaternion with Ogre's
`Matrix3::SetColumn` + `Quaternion::FromRotationMatrix` (`@ 1407e2350`).

**Verified** (`CharacterAppearanceTests.Weapon_attachment_points_match_the_male_skeleton`):
`data/ragdoll/attachments-weapons.phs` (version 1.4) reads to the end with three actors `hip`
(bone `Bip01 Pelvis`), `back` and `back2` (both `Bip01 Spine2`), one point each, and each actor's stored bone
position and rotation equal the binding pose of that bone in `male_skeleton.skeleton` (model space, within
0.01 units; rotations agree, which also checks the column order). `Every_attachment_phs_reads_or_is_refused`
runs the reader over every `.phs` in `data/`: files with collision shapes (ragdolls) are refused, the
rest read.

## How Kenshi uses attachment points (Verified, decompiled)

`@ 1407e3b80` turns every point of every actor into an attachment named after the **actor** (`hip`,
`back`...), on the actor's bone, with offset

- position = inverse(bone rotation) × (point position − bone position)
- rotation = inverse(bone rotation) × point rotation

using the bone transform **stored in the file**, i.e. the point's place relative to the bone in the binding
pose. These offsets are what `Entity::attachObjectToBone` later gets
([../characters.md](../characters.md#weapons-and-other-bone-attached-items)).

## Unknown

- The meaning of the undecoded actor fields, joints and shapes (ragdoll physics).
- Whether any shipped attachment file is older than 1.4 (then actors carry no bone and the points can't
  be placed this way).
