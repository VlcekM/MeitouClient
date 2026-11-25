# Ogre skeleton

Ogre serializer/source, base-file scans and Kenshi bone/animation binary research; chunk/version
and surveyed data tables follow. Reader: OgreSkeletonReader; runtime: [animation](../animation.md).

## Stream and chunk exceptions

Same header/chunk/string primitives as [mesh](ogre-mesh.md). Vector3=XYZ floats;
quaternion=XYZW on disk (not Ogre in-memory WXYZ), verified unit rotations on base files.
**Verified** BONE length EXCLUDES name:36 without scale,48 with; parse newline name, do not skip
by stored length. Scale present iflength>36. Keyframe base38, scale if>38. Other scanned chunk
lengths match consumed bytes. Handles<256, parents must exist, duplicate/second-parent constraints
come from Ogre; our rejection behaviour is documented separately from original permissiveness.
Missing blend mode=average. Missing linked animation skeleton can fail skeleton load; mesh can
still load unanimated when skeleton fails. Orientation renormalization is unverified.

## Binding and bone maths

Character race/base/animation sources and worn mesh skeleton links can differ. Map intended
bones/assignment handles and attachment transforms; a mesh link name alone does not prove the
race skeleton has the same order. Preserve base-pose/inverse-bind, track offsets and parent
rotation/scale/position propagation; Ogre/Kenshi positional-size and bone-size factors differ.
Do not substitute a generic matrix hierarchy for decoded inheritance flags.
Average/additive blend modes, missing tracks, masks and per-layer weights affect pose composition;
shape/face poses apply at their researched stages. Formula snippets and open questions below
retain source scope; [animation](../animation.md) supplies slider/factor definitions.

### Versions in the base game (Verified)

| Header string | Files |
| --- | ---: |
| `[Serializer_v1.80]` | 264 |
| `[Serializer_v1.10]` | 100 |

### Chunks — layout/example

```
0x1000 HEADER              ushort id + version string (no length)
0x1010 BLENDMODE           ushort mode: 0 average, 1 cumulative            (optional; 1.80 only in practice)
0x2000 BONE                string name, ushort handle, Vector3 position, Quaternion orientation,
                           [Vector3 scale]                                  (repeats)
0x3000 BONE_PARENT         ushort child handle, ushort parent handle        (repeats)
0x4000 ANIMATION           string name, float length (seconds)              (repeats)
  0x4010 BASEINFO          string base animation name (empty = this one), float base keyframe time
                           (optional, only as the first child)
  0x4100 TRACK             ushort bone handle                               (repeats)
    0x4110 KEYFRAME        float time, Quaternion rotation, Vector3 translation, [Vector3 scale]  (repeats)
0x5000 ANIMATION_LINK      string skeleton file name, float scale           (repeats)
```

### Animation links

An `ANIMATION_LINK` names another skeleton file whose clips the skeleton plays: a clip the skeleton's own animations lack is taken from its
links in order, tracks by bone handle (**Verified**, OgreMain `Skeleton::_getAnimationImpl`, [animation files](../animation.md#animation-files)).
**Observed** (read with `OgreSkeletonReader`: base data and the 234 workshop items of #1418): every link has scale 1, and no linked skeleton
found has links of its own.
`Guild Escort Contracts.mod` (workshop 3798671709) gives Greenlanders and Scorchlanders `human_male_fingers.mesh` / `human_female_fingers.mesh`,
whose 45-bone skeletons (the 30 body bones plus fingers) hold one animation and link `male_skeleton.skeleton` / `female_skeleton.skeleton` for
the rest. Meitou (`SkeletonRig.Clip`) follows the links by file name as a body's own skeleton is found, and a linked skeleton's own links too, 8 deep
at most (engine choice), at scale 1; without them those bodies have no clip at all (a carried one hangs straight off the shoulder, #1418).

### Meshes and skeletons

| Mesh | Problem |
| --- | --- |
| `items/armour/meshes/Human_Skin_Suit.mesh` | links `Human_Skin_Suit.skeleton`, which doesn't ship. Used as a female armour mesh by `Newwworld.mod`. |
| `items/armour/meshes/Iron Clad Jacket_F.mesh` | links `Iron Clad [feMale] Jacket.skeleton`, which doesn't ship (`Iron Clad Jacket_F.skeleton` sits beside it). Used by `Newwworld.mod`. |
| `character/meshes/whistler/whistler.mesh` | links `female_skeleton.skeleton` (30 bones) but assigns bones up to 54. No data file references this mesh. |

### Binding worn meshes to the character

| Mode | Used for | What happens (`@ 14052d480`, `@ 14052bfa0`) |
| --- | --- | --- |
| 1, shared skeleton | ARMOUR (3), CONTAINER (46, backpacks), ATTACHMENT (6, hair/beards), LIMB_REPLACEMENT (111) | The item entity goes on the body's scene node and calls `Entity::shareSkeletonInstanceWith(body)`, with its own animation-state update turned off. |
| 0, attach to bone | everything else (weapons etc.) | `Entity::attachObjectToBone(body, boneName, entity, orientation, position)`; the node under it gets scale `1 / body node scale` so the item isn't scaled with the character. The bone's own scale (body-shape bone size, scale keys) doesn't reach it either: **Verified** (OgreMain `TagPoint::updateFromParentImpl`, the same modified rule as `OldBone`) a tag point's derived scale is its own size × own scale, not inherited, and `TagPoint::updateNode` copies only its derived position and orientation to the item's node. A bone name the skeleton lacks falls back to the root bone (`@ 1406466a0`). |
| 2 | ARMOUR/CONTAINER/ATTACHMENT records with neither `mesh` nor `mesh female` | No visible entity; only a physics attachment (CHARACTER_PHYSICS_ATTACHMENT `bone name`, `file male`/`file female`, `@ 140535f20`). |

## Expression reference

- Chunks: ` 0x1000 HEADER ushort id + version string (no length) 0x1010 BLENDMODE ushort mode: 0 average, 1 cumulative (optional; 1.80 only in practice) 0x2000 BONE string name, ushort handle, Vector3 position, Quaternion orientation, [Vector3 scale] (repeats) 0x3000 BONE_PARENT ushort child handle, ushort parent handle (repeats) 0x4000 ANIMATION string name, float length (seconds) (repeats) 0x4010 BASEINFO string base animation name (empty = this one), float base keyframe time (optional, only as the first child) 0x4100 TRACK ushort bone handle (repeats) 0x4110 KEYFRAME float time, Quaternion rotation, Vector3 translation, [Vector3 scale] (repeats) 0x5000 ANIMATION_LINK string skeleton file name, float scale (repeats) `
- Bone chunk lengths don't count the name: `6 + 2 + 12 + 16 = 36`; `6 + 4 + 16 + 12 = 38`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Versions in the base game (Verified)
- Bone chunk lengths don't count the name (Verified)
- Base-game facts (Verified)
