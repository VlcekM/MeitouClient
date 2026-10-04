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

For the first two Ogre would log and leave the mesh unanimated if it relied on the mesh's own link.
Whether Kenshi does, or attaches armour to the character's skeleton itself, is Unknown (not checked
in game).

## Open questions

- How Kenshi binds armour and clothing meshes to the character skeleton (shared skeleton instance?
  by bone name or by index?). The cross-check above only shows indices fit the linked skeleton.
- Where Kenshi's animation names come from (game data, or the 174/166 animations in the character
  skeletons directly) and how it blends them (blend mode is always average).
- What the per-armour `.skeleton` files are for, if anything.
