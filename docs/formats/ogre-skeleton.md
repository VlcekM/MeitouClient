# Ogre `.skeleton`

Sources:
- **OGRE source** (MIT): Kenshi runs Ogre 2.0 ("Tindalos") with the classic "v1" skeleton system
  (`OldSkeleton`/`OldBone`). The layout below follows ogre-next branch `v2-0`,
  `OgreSkeletonSerializer.cpp`, `OgreSkeletonFileFormat.h`, `OgreSerializer.cpp` and `OgreSkeleton.cpp`.
  `Meitou.Data.Ogre.OgreSkeletonReader` follows the same structure.
- **Verified 2026-10-04** (`OgreSkeletonReaderTests.Reads_every_base_game_skeleton`,
  `OgreSkeletonReaderTests.Every_base_game_skinned_mesh_matches_its_skeleton`, `meitou-tools skeletons`):
  all 364 non-empty base-game skeletons parse to the last byte and pass the consistency checks listed
  under [Base-game facts](#base-game-facts-verified).

## Versions in the base game (Verified)

| Header string | Files |
| --- | ---: |
| `[Serializer_v1.80]` | 264 |
| `[Serializer_v1.10]` | 100 |

Plus one zero-byte file, `items/armour/meshes/trousers_m.mesh.skeleton` (no header at all; Ogre's
reader would throw on it too, so it can't be in use).

Ogre 2.0 accepts exactly these two strings. **The reader does not branch on the version**: both are
read by the same code. The writer differs: for 1.80 it emits the blend-mode chunk and, when set, the
animation base-info chunk; for 1.10 it emits neither. Verified on the base game: every 1.80 file has a
blend-mode chunk, no 1.10 file does.

## Stream basics

Same as [`.mesh`](ogre-mesh.md#stream-basics): little-endian, `ushort` 2 bytes, `float` 4, strings end at
`\n`, file starts with `ushort 0x1000` and the version string, then chunks of `ushort id`,
`uint length` (including the 6-byte chunk header), body.

- `Vector3` = 3 floats x, y, z.
- `Quaternion` = 4 floats in the order **x, y, z, w** (Ogre's `Serializer::readObject`; Ogre keeps w
  first in memory, the serializer reorders). That is `System.Numerics.Quaternion`'s constructor order.
  Verified: every base-game bone orientation and keyframe rotation is unit length (within 0.001) when
  read this way.

## Chunks

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

Top-level chunks are read in a loop until the end of the file, in any order; Ogre's writer emits blend
mode, all bones, all parent links, all animations, then links. Children are read by structure: an
animation takes a BASEINFO chunk if it comes first, then TRACK chunks while they come; a track takes
KEYFRAME chunks while they come; anything else is stepped back over (Ogre's "backpedal") and handed to
the parent.

### Bone chunk lengths don't count the name (Verified)

The stored length of a BONE chunk is `6 + 2 + 12 + 16 = 36` without scale and `48` with scale, **whatever
the name's length**: Ogre's `calcBoneSizeWithoutScale` leaves the name out (a TODO comment in the
source says it can't be fixed without breaking the scale test). Seen in the hex of `bull.skeleton`
(1.10) and `antilop250.skeleton` (1.80): bones named `Bip01`, `bn02b`, `Bip01 Pelvis` all have length
`0x24`. So a bone can't be skipped by its length, and the reader must parse the name.

Ogre decides whether a scale follows by `length > 36`. Keyframes use the same trick with a correct size:
`6 + 4 + 16 + 12 = 38` without scale, scale present if `length > 38`. The writer only stores a scale
that isn't exactly `(1, 1, 1)`.

Every other chunk's length is correct: Verified, the reader compares the stored length of every
animation, base-info, track and keyframe chunk with the bytes it consumed, and there are 0 mismatches
in the base game.

### What Ogre does with odd data (from the source)

- Unknown top-level chunk id: Ogre's loop ignores the id **without skipping the body**, so the next
  read lands inside it. The reader throws instead.
- Bone handles must be below 256 (`OGRE_MAX_NUM_BONES`); duplicate handles or names throw. (Our reader
  throws on duplicate handles; the base-game test checks the limit and names.)
- A BONE_PARENT names bones that must already exist; giving a bone a second parent throws.
- Missing blend-mode chunk: the skeleton keeps its constructor default, average.
- Animation links are loaded right after the skeleton (`Skeleton::loadImpl`); a missing linked file
  throws, which fails the whole skeleton load.
- A mesh whose linked skeleton fails to load still loads; Ogre logs "Unable to load skeleton ... This
  Mesh will not be animated" (`Mesh::setSkeletonName`).
- Unknown: whether Ogre renormalises orientations when it sets them (`OldNode::setOrientation`); the
  base game's quaternions are unit length anyway.

## Base-game facts (Verified)

Measured with `meitou-tools skeletons` and the tests named at the top:

- 13,443 bones; at most 72 per skeleton. Handles are 0..n-1 contiguous and names unique within a
  skeleton. Observed (one-off scratch run, not in the tests): bones are stored in handle order.
- Parent links form a tree in 363 skeletons. The one exception has two roots: the unreferenced
  `items/armour/meshes/Drifter2_pants_Light Armour.skeleton` (`Bip01` and `__dummy__`). No cycles.
- Blend mode: every 1.80 file stores 0 (average); 1.10 files store none (average by default).
- Bone scale is stored on 905 bones in 23 skeletons (21 of them 1.10). Observed (a sample of the
  values): many are 1 up to float noise (e.g. `0.99999994`, which the writer's exact comparison
  keeps), others real scales such as `2.54` on all axes.
- 564 animations in 30 skeletons. The two character skeletons hold 340 of them:
  `character/meshes/male_skeleton/male_skeleton.skeleton` 174 and `female_skeleton.skeleton` 166, both
  30 bones; the next are animal skeletons with 15 or fewer. The longest animation is 15.93 s.
- 18,629 tracks, 997,406 keyframes. Keyframe times never go back and stay within the animation;
  every track's bone exists; no animation has two tracks for one bone. **No keyframe stores a scale.**
  No animation has base info. Observed (scratch run): every track has keyframes, starts at 0 and ends
  at the animation's length (within 0.001 s), and no two keyframes share a time.
- 5 animations have no tracks at all (`animal/meshes/goat_fur.skeleton`: idle1–3, run, walk).
- Animation links: 118, all in `Drifter2_pants_Light Armour.skeleton`, each to
  `Drifter2_pants_Light Armour_<animation>.skeleton` with scale 1. **None of those files ship**, so Ogre
  couldn't load that skeleton; no mesh links it (its mesh links `male_skeleton.skeleton`), so it is
  unused. It looks like an exporter's per-animation split left behind.

### Meshes and skeletons

The test checks existence, duplicates and bone indices; the counts here are Observed (scratch run).

- 167 skinned meshes link 84 distinct skeleton names. Most link the shared character skeletons:
  `male_skeleton.skeleton` ×54, `female_skeleton.skeleton` ×29 (armour and clothing, skinned to the
  character's bones). 83 of the 365 skeleton files are linked by any mesh; many armour folders ship a
  `.skeleton` per mesh that nothing references.
- Links are bare file names (no folder). 5 differ in case from the file on disk, so the lookup has to
  be case-insensitive (Ogre's file-system archives are case-insensitive on Windows). One name exists
  twice (`whale2.skeleton` in `animal/meshes/` and `animal/meshes/noLOD/`), byte-identical copies.
- Every bone assignment's bone index is below the linked skeleton's bone count, with one exception
  (below).

Links that don't hold (the test lists them explicitly, so a change shows up):

| Mesh | Problem |
| --- | --- |
| `items/armour/meshes/Human_Skin_Suit.mesh` | links `Human_Skin_Suit.skeleton`, which doesn't ship. Used as a female armour mesh by `Newwworld.mod`. |
| `items/armour/meshes/Iron Clad Jacket_F.mesh` | links `Iron Clad [feMale] Jacket.skeleton`, which doesn't ship (`Iron Clad Jacket_F.skeleton` sits beside it). Used by `Newwworld.mod`. |
| `character/meshes/whistler/whistler.mesh` | links `female_skeleton.skeleton` (30 bones) but assigns bones up to 54. No data file references this mesh. |

For the first two Ogre logs and loads the mesh without a skeleton. Kenshi then still makes them
share the character's skeleton instance (see [Binding](#binding-worn-meshes-to-the-character)); whether
such a mesh actually deforms is Unknown (see Open questions).

## How Kenshi uses it

Sources: decompilation of `kenshi_x64.exe` and of Kenshi's own build of `OgreMain_x64.dll` (Ghidra 12.1.4,
auto-analysed; addresses below are in the binary named). **Kenshi's OgreMain is modified**: it exports
methods stock Ogre 2.0 doesn't have (`OldBone::setBoneSize`, `setBonePositionalSize`, `multiplyBoneSize`,
`OldSkeletonInstance::setMovementScale`, `Animation::setOverrideBone`, `Animation::getTranslation`,
`Entity::setSkipAnimationStateUpdate`) and changes the bone and animation maths. Animation selection and
blending are in [animation.md](../animation.md).

### Which skeleton files get loaded

Verified.

Only two paths load skeletons:

1. A mesh's own skeleton link, through Ogre's normal mesh loading. Kenshi's mesh setup
   (`kenshi_x64.exe @ 140447bf0`, logged as `ResourceLoader::SetMeshData`) can override the link with
   `Mesh::setSkeletonName` when its load descriptor carries a name, but item meshes are loaded with a
   shared static default descriptor (`@ 140537690`), so their own link is what Ogre uses (Observed: the
   default descriptor's name is presumed empty, not read at run time).
2. `ANIMATION_FILE` records (`male animation` / `female animation`), added to the body's skeleton as
   linked animation sources (`@ 140539020`) and preprocessed once at startup (`@ 140871f00`,
   `@ 14086dfe0`). See [animation.md](../animation.md#where-animations-come-from).

The `OldSkeletonManager` import has no other caller. So the per-armour `.skeleton` files that no mesh
links are never loaded (Observed: no other loading path found), and nothing loads
`Drifter2_pants_Light Armour.skeleton` or its 118 links.

### Binding worn meshes to the character

Verified.

Each worn or held item becomes an `AttachedEntity` (RTTI name) with one of three modes, picked from the
item record's type when it's created (`kenshi_x64.exe @ 1405358a0`; hair and beards `@ 140532490`):

| Mode | Used for | What happens (`@ 14052d480`, `@ 14052bfa0`) |
| --- | --- | --- |
| 1, shared skeleton | ARMOUR (3), CONTAINER (46, backpacks), ATTACHMENT (6, hair/beards), LIMB_REPLACEMENT (111) | The item entity goes on the body's scene node and calls `Entity::shareSkeletonInstanceWith(body)`, with its own animation-state update turned off. |
| 0, attach to bone | everything else (weapons etc.) | `Entity::attachObjectToBone(body, boneName, entity, orientation, position)`; the node under it gets scale `1 / body node scale` so the item isn't scaled with the character. A bone name the skeleton lacks falls back to the root bone (`@ 1406466a0`). |
| 2 | ARMOUR/CONTAINER/ATTACHMENT records with neither `mesh` nor `mesh female` | No visible entity; only a physics attachment (CHARACTER_PHYSICS_ATTACHMENT `bone name`, `file male`/`file female`, `@ 140535f20`). |

The mesh is picked by gender: `mesh` or `mesh female` (ARMOUR also `overlap mesh` / `overlap mesh female`
when an `overlap items` item is worn too, `@ 140537690`, `@ 14052dc90`). For a female character with an
empty `mesh female`, a mode-0 item falls back to `mesh`; a mode-1 item doesn't, and logs
"[Appearance] No female mesh for ..." instead of showing anything (`@ 140537690`).

Kenshi's `Entity::shareSkeletonInstanceWith` (`OgreMain_x64.dll @ 1800c3a80`) doesn't compare the two
meshes' skeletons (stock Ogre 1.x throws when they differ): it simply adopts the other entity's skeleton
instance, bone matrices and animation-state set. So a worn mesh's bone assignments index straight into
**the body's skeleton, by bone handle**, whatever its own link says. That works because armour is rigged
to the shared character skeletons (`male_skeleton.skeleton` / `female_skeleton.skeleton`, 30 bones).

### Bone maths in Kenshi's Ogre

Verified by disassembly of `OgreMain_x64.dll @ 1801e20e0`.

`OldBone::updateFromParentImpl` runs the normal node update and then overwrites two results:

- **Derived scale** = the bone's *bone size* times its own local scale, per axis. The parent's derived
  scale is **not** inherited (stock Ogre multiplies it in).
- **Derived position** (bones with a parent) = parent derived orientation applied to
  (local position × the bone's *positional size*, per axis, × the **Y component** of the parent's
  derived scale, on all three axes), plus the parent's derived position.

Bone size and positional size are two extra per-bone vectors, both (1, 1, 1) by default (bone
constructors `@ 1801e1a50`, `@ 1801e1b00`); Kenshi sets them from appearance sliders
([animation.md](../animation.md#body-shape-sliders)). Consequence for the file data: the 905 stored bone
scales (local scale) scale their own bone's vertices and their children's *offsets* (through the Y
component), but not the children's own scale. Skinning then uses the usual offset transform
(`OldBone::_getOffsetTransform @ 1801e1dc0`: derived transform times the inverse binding pose).

### Blending in Kenshi's Ogre

Verified, `OgreMain_x64.dll @ 180343200`, `@ 180017ee0`, `@ 180031260`.

- Blend mode average (every base-game skeleton): if the enabled animations' weights sum to more than 1,
  every weight is divided by the sum; otherwise weights are used as they are. Cumulative: never scaled.
- Animations are applied in two passes: first those without override bones, then those with any
  (`Animation::setOverrideBone`). On an override bone, the animation first pulls the bone's accumulated
  rotation back toward its binding orientation by its weight (fully reset at weight ≥ 0.99), then adds
  its own rotation. Other bones blend as in stock Ogre (rotation from identity by weight, translation
  times weight).
- Every track's translation is also multiplied by the skeleton instance's **movement scale**
  (`setMovementScale`, `kenshi_x64.exe @ 14052bb10`); the scale passed with a linked animation source is
  ignored. A state with translation disabled keeps only the Y part of its translations.
- Bones listed as disabled on the skeleton instance are skipped.

## Open questions

- Whether an entity whose own skeleton failed to load (`Human_Skin_Suit.mesh`, `Iron Clad Jacket_F.mesh`)
  still deforms after sharing the body's instance: Kenshi's Ogre compiles bone assignments in
  `Entity::_initialise` (`OgreMain_x64.dll @ 1800bfac0`), but whether that happens without a mesh
  skeleton wasn't traced. Unknown.
- `whistler.mesh` (bone indices up to 54): no data references it, so its behaviour is untested. Unknown.
- Where the bone name for mode-0 attachments (weapons) comes from (RACE `attachment points` .phs or
  fixed names). Unknown.
