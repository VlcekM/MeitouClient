# The removed mesh and character viewer

`meitou-viewer` had two modes besides `--world` and `--impostor-preview`: a **mesh viewer** (`meitou-viewer <mesh>`) that
showed one Ogre mesh, textured and optionally animated, and a **character viewer** (`meitou-viewer --character <record>`) that
assembled a dressed, posed and animated character from FCS records. Phase 8 dropped both instead of porting them to the native
Vulkan API ([DECISIONS.md](../DECISIONS.md) 23). This page keeps what they did and how, so a later native character renderer can be
rebuilt from it. The rules of the game itself are in [characters.md](characters.md), [animation.md](animation.md),
[formats/ogre-skeleton.md](formats/ogre-skeleton.md), [formats/ogre-mesh.md](formats/ogre-mesh.md) and
[formats/runtime-materials.md](formats/runtime-materials.md); this page describes our implementation and links there for the rules.
Status labels as in [README.md](README.md); the labels below are the ones docs/viewer.md recorded while the modes existed.

## Reading the code

The last commit with the code is tagged **`model-viewer-last`** (master `e8f234b`). Read a file with, for example:

```
git show model-viewer-last:tools/Meitou.ModelViewer/CharacterRenderer.cs
git show model-viewer-last:docs/viewer.md        # the old usage sections, verbatim
```

Removed files (all in `tools/Meitou.ModelViewer`, sizes at the tag):

| File | Size | Role |
| --- | --- | --- |
| `Program.cs` (mesh-mode part) | 24.5 KB, 472 lines | Entry point (dispatch to `--world`, `--character`, `--impostor-preview`, else the mesh viewer); `ViewerOptions` (mesh-mode command line), `Scene` (one loaded mesh, its material candidates, animation index and time), `ViewerApp` (loading, `--info`, screenshot and interactive window). `SmokeTest` (`--quit-after`) is shared with the world viewer. |
| `Renderer.cs` | 26.5 KB, 509 lines | `NativeMeshProgram` (a `LegacyProgram` with VkGl's SPIR-V and layout, drawn natively inside VkGl's open pass, "step P" of [renderer-native.md](renderer-native.md)); `RenderOptions` (display toggles); `Renderer` (uploads a `Model`, loads and caches textures, detects swizzled normal maps, draws parts, the grid and skeleton lines). |
| `CharacterRenderer.cs` | 21.4 KB, 416 lines | Draws a `CharacterScene` with its own fragment shader (body layering, hair, worn clothing with cut-out and dye), LOD index ranges; uses `Renderer` for the clear, the grid and the texture cache. |
| `CharacterApp.cs` | 19.2 KB, 358 lines | `CharacterViewOptions` (command line), `CharacterApp` (loading, framing, keys, screenshot). |
| `CharacterScene.cs` | 22.7 KB, 373 lines | `CharacterShading`, `CharacterMaterial`, `CharacterPartModel`, `CharacterScene`: turns a `CharacterAppearance` into meshes, a skeleton, materials and animation layers; bakes face poses; applies body shape; picks LOD levels. |
| `Animator.cs` | 12.1 KB, 242 lines | `AnimationLayer` and `Animator`: poses an Ogre skeleton with Kenshi's bone maths and blending, produces the skin matrices. |
| `Camera.cs` | 2.2 KB, 63 lines | The orbit camera of these two modes (the world viewer has its own `WorldCamera`). |

What they used and what stays (none of it is removed with the modes):

- `Meitou.Data.Ogre`: `OgreMeshReader` / `OgreMesh` (submeshes, vertex data, bone assignments, poses, LOD levels, bounds),
  `OgreSkeletonReader` / `OgreSkeleton` / `OgreAnimation` / `OgreKeyFrame` / `OgreSkeletonBlendMode`, `OgreMaterialLibrary`, `MeshLod`.
- `Meitou.Data.Characters`: `CharacterAppearance` (with `CharacterOptions`, `CharacterPart`, `AttachMode`, `BodyLayer`, `AttachmentPoint`),
  `AppearanceFile` (`.bod2`), `CharacterGenerator` and `Loadout` (`--seed`), `CharacterShape`, `CharacterLod`, `AnimationMask`,
  `PhysicsAttachmentFile`.
- `Meitou.Data`: `GameDatabase`, `LoadOrder`, `Textures.TextureLoader` (DDS etc. to RGBA8 levels). `Meitou.Core`: `Content.GameInstall`.
- `Meitou.Rendering`: `Model` / `ModelPart` / `Vertex` (mesh to triangle lists and skin weights), `Shaders` (`MeshVertex` with the
  skinning, `MeshFragment`, `LineVertex` / `LineFragment`, `MaxBones` = 128), `NativeShaders.BonesBlock`, `MaterialResolver` /
  `SurfaceMaterial` / `AlphaSource`, `AssetLocator`, `FramebufferCapture`.
- Tests: `CharacterAppearanceTests`, `CharacterGeneratorTests`, `CharacterShapeTests` (with `CharacterLodTests`), `OgreMeshReaderTests`,
  `OgreSkeletonReaderTests`. **No test exercised `Animator`, `CharacterScene` or the renderers**; everything about them below was checked
  by screenshots only.

## Usage (as it was)

```
meitou-viewer <mesh> [options]
meitou-viewer human_male --anim ninjarun
meitou-viewer bush01 --screenshot out.png --size 800x600 --yaw 35 --pitch 20
meitou-viewer mask2 --info

meitou-viewer --character "Dust Bandit"
meitou-viewer --character Ruka --screenshot ruka.png --size 900x1000
meitou-viewer --character Greenlander --female --equip "Samurai Boots" --equip Katana --drawn
meitou-viewer --character "Dust Bandit" --anim "walk lower" --anim "walk upper":0.8
meitou-viewer --character "Dust Bandit" --faction "Dust Bandits" --seed 3
meitou-viewer --character Ruka --shape height=1.2 --shape "Arm bulk=145;muscle=1"
meitou-viewer --character Greenlander --lod 1 --wireframe
```

**Mesh mode.** `<mesh>` is a bare file name (`.mesh` optional) or a path, found as in [viewer.md](viewer.md#finding-files). Options:
`--texture <file>` and `--normal <file>` (use these instead of the resolved textures), `--skeleton <file>`, `--anim <name|index>`
(default: binding pose), `--time <s>`, `--material <n>` (the n-th material candidate), `--screenshot <png>`, `--size WxH` (default
1280x960), `--yaw` / `--pitch` (degrees, defaults 35 / 20), `--zoom <factor>`, `--wireframe`, `--skeleton-lines`, `--no-grid`,
`--vertex-colours` (apply vertex colours on every part that has them), `--no-fcs` (skip the game records: faster, no FCS textures),
`--info` (print what was found and exit). It printed one line per finding: mesh and how it was found, the records using it, the
skeleton tried and chosen, each submesh (material name, vertex and triangle counts, skinned / tangents / colours) with up to 12
material candidates, the animations with lengths, the bounds and the load time.

Keys: left drag orbit, right or middle drag pan, wheel zoom, `F` frame, `W` cycles solid / solid + wireframe / wireframe, `T`
textures, `N` normal maps, `C` vertex colours, `B` back-face culling, `K` skeleton lines, `G` grid, `M` / `Shift+M` next / previous
material candidate, `Right` / `Left` next / previous animation, `Home` binding pose, `Space` pause, `Up` / `Down` speed × or ÷ 1.5,
`P` screenshot, `Esc` quit. The title showed the file, the animation (index, name, time / length, speed, paused) and the material
candidate.

**Character mode.** The record is a CHARACTER or RACE, found by string id, else by name (CHARACTER before RACE). Options:
`--female` / `--male` (default: the body file, else CHARACTER `female chance` ≥ 50), `--equip <record>` (repeatable; replaces the
same armour slot), `--naked`, `--drawn` (first weapon drawn), `--seed <n>` (roll the character as the game spawns one), `--faction
<record>` (FACTION of a seeded character), `--anim <name>[:weight]` (repeatable, blended; an ANIMATION record name or an Ogre animation
name), `--bind-pose`, `--time <s>`, `--no-morphs`, `--no-skin-tone`, `--no-postures`, `--shape <name=value>` (repeatable or
`;`-separated), `--no-shape`, `--lod <n>`, and the mesh mode's `--screenshot`, `--size`, `--yaw` / `--pitch` (default pitch 15 here) /
`--zoom`, `--skeleton-lines`, `--no-grid`, `--wireframe`, `--info`. Extra keys: `Tab` / `1`–`9` select an animation layer, `Right` /
`Left` change its animation, `+` / `-` its weight (steps of 0.1, 0 to 2), `Insert` adds a copy of the selected layer at weight 0.5,
`Delete` removes it, `Home` clears all layers (binding pose), `L` cycles LOD auto / 0 / 1 / ... (no `C` or `M`). The title showed the
layers (`[selected]`, weights), speed, pause and each part's LOD level. Console lines: `character`, `loadout`, `note`, `body`, `head`,
`layers`, `part` (record, field, mesh, where attached, material), `anim`, `posture`, `shape`, `morphs`, `lod`.

Both modes: `--quit-after <s>` closes the window after s seconds and prints the frames drawn (still available for `--world`);
`MEITOU_VK_VALIDATION=1` runs the Vulkan validation layer; `--renderer` is accepted and ignored.

## Assembling a character

All choices are made in `Meitou.Data.Characters` (stays), rules and evidence in [characters.md](characters.md):
`CharacterAppearance.Build(db, installRoot, record, new CharacterOptions { Female, Equip, Naked, WeaponDrawn, Seed, Faction })`
returns the race, the body file (`.bod2`, if the CHARACTER has one), gender, body mesh and textures, head, hair, beard, hair colour,
skin tone, face pose weights, the race's attachment points, the clothing "vest" layers drawn on the body, and the parts: per worn or
carried item its record, field, mesh, `AttachMode` (shared skeleton or bone), attachment point name and, for a rolled loadout, the
chosen MATERIAL_SPECS_CLOTHING / MATERIAL_SPECS_WEAPON. `--equip` adds items (replacing the same armour slot), `--naked` leaves out
clothing and weapons.

With `--seed n` (`CharacterGenerator`) the character is rolled by the game's spawn rules
([characters.md](characters.md#generating-a-character)): gender, and without a body file a random head, hair, beard, hair colour,
skin tone, sliders and one of the race's `morph num` faces from its editor limits; then clothing per slot with quality and material,
backpack, crossbow and weapons with manufacturer and model (the model's texture is used). The roll was printed (`loadout` lines).
`--faction` names the FACTION the character spawns in (its `hairstyles` limit the hair). The same seed always gives the same
character, not the one the game would give. Without `--seed` each roll takes its likeliest choice.

## Loading a mesh

1. **Find** the file (`AssetLocator`, [viewer.md](viewer.md#finding-files)). Character parts are looked up by **bare name** in the
   configured resource locations first (`assets.Configured.Find`), because Kenshi reduces item mesh paths to the bare name
   ([characters.md](characters.md#meshes-per-item-verified--140537690--1405358a0)).
2. **Read** it with `OgreMeshReader` ([formats/ogre-mesh.md](formats/ogre-mesh.md)).
3. **Build** a `Model` (`Meitou.Rendering.Model.Build(mesh, boneCount)`; stays): one `ModelPart` per submesh that has vertices,
   using shared or own vertex data and the matching bone assignments.
   - Triangle lists are used as stored; strips are expanded with alternating winding (`i-2, i-1, i` for even i, `i-1, i-2, i` for
     odd); fans as `0, i-1, i`; other operations (lines, points) are skipped with a warning.
   - Every part is deinterleaved into one fixed vertex, 84 bytes, packed:

     | Location | Field | Type | Offset |
     | --- | --- | --- | --- |
     | 0 | position | vec3 | 0 |
     | 1 | normal (normalised; +Y if missing or NaN) | vec3 | 12 |
     | 2 | uv (first set; 0 if none) | vec2 | 24 |
     | 3 | tangent xyz, w = handedness | vec4 | 32 |
     | 4 | colour RGBA 0–1 (white if none) | vec4 | 48 |
     | 5 | four bone handles, one byte each | uvec4 from one uint (`VertexAttribIPointer`, unsigned bytes) | 64 |
     | 6 | four weights | vec4 | 68 |

   - Tangent handedness: a 4-component tangent's w sign; else, with a binormal element, the sign of dot(cross(normal, tangent),
     binormal); else +1.
   - Vertex colours: `ColourArgb` (and the old generic colour) are stored B, G, R, A in memory and are swapped; `ColourAbgr` is R, G, B, A.
   - **Weights** (Ogre's rule): per vertex the 4 largest assignments with bone index below the skeleton's bone count (and below 256),
     weight > 0, renormalised to sum 1. Assignments past the bone count are dropped and counted in a warning (`whistler.mesh`: 6,917 of
     them). Without a skeleton (`boneCount` null) the part is not skinned.
   - `Model.Min/Max/Center/Radius` are the binding-pose bounds; `PosedBounds(skin)` skins every vertex on the CPU the way the vertex
     shader does (for framing an animated pose).

## Materials and textures

**Mesh mode** used `MaterialResolver` (stays; the world's objects use it too), whose rules and evidence are in
[viewer.md](viewer.md#how-mesh-textures-are-resolved): per submesh a list of `SurfaceMaterial` candidates, best first; `--texture` /
`--normal` were put in front of the list; `M` / `--material n` cycled through them (e.g. the 16 weapon models of a katana).

**Texture loading** (`Renderer.Texture`, cached per name and addressing):

- Looked up by bare file name first (the game reduces texture fields to the bare name, runtime-materials.md), then as given.
- Decoded on the CPU by `TextureLoader` into RGBA8 levels and uploaded uncompressed as RGBA8, rows as stored (top row first) with UVs
  unchanged ([viewer.md](viewer.md#conventions-verified-by-screenshots)). Mipmaps were generated when the file's chain did not reach
  1x1. Trilinear filtering, anisotropy 8.
- Addressing: repeat, or **clamp to a transparent black border** (0, 0, 0, 0) for the character units (Kenshi's `character.material`).
- No sRGB conversion, as in Kenshi ([characters.md](characters.md#colour-space-observed)).
- **Swizzled normal maps** (Observed, viewer heuristic; the world's `WorldTextureCache` has the same test): a normal map whose mean
  blue (on the first mip at most 256x256) is below 180 and whose R, G, B means agree within 8 is taken as swizzled (X in alpha, Y in
  green; Z rebuilt as sqrt(1 − x² − y²)). This catches the character body maps; building and weapon maps have a mean blue of about
  240–255. The game picks by shader template instead; the character body shader forced swizzled decoding anyway.

**Character mode** built a `CharacterMaterial` per part (`CharacterScene.BodyMaterial` / `PartMaterial`) with one of three shadings:

- **Body**: RACE body texture / normal / body mask from `CharacterAppearance`; HEAD `texture map`, `normal map`, `mask map`; skin
  tone = `CharacterAppearance.SkinToneParameter` (1 − the body file's `Skin Tone` per channel; zero with `--no-skin-tone`); hair
  overlay = the hair ATTACHMENT's `head texture` (`head texture female`), with one-hot vectors from `head alpha channel` (default 1)
  and `head channel` (default 0); beard overlay likewise (alpha channel only); hair colour; up to three "vest" layers
  (`CharacterAppearance.BodyLayers`: ARMOUR `vest texture`, `vest normalmap`, `vest colormap`); shirt colour = `color 1` of the
  CHARACTER's `color` COLOR_DATA (faction dyes not followed).
- **Hair** (ATTACHMENT): diffuse = `texture map` (`texture map female` for women, else `texture map`); one-hot `hair diffuse channel`
  and `hair alpha channel`; threshold = `alpha rejection` / 255 (default 128); hair colour; always double-sided.
- **Item** (everything else):
  - Clothing: the MATERIAL_SPECS_CLOTHING (or MATERIAL_SPEC / MATERIAL_SPECS_WEAPON) reference of `material` with the highest weight
    (`material female` for women when set); a `--seed` loadout's chosen clothing material wins unless `material female` applies. Dye:
    the item's `color` COLOR_DATA, else the CHARACTER's `color` unless `dont colorise` (default true); used only if the spec has a
    `color map`. `Colour1/2` = RGB of the dye's `color 1` / `color 2`, alpha = the spec's `paint factor 1/2`. Clip on normal alpha
    (0.6) when the item is on the shared skeleton and has a normal map. Double-sided when `material type` is 2.
  - A seeded loadout's weapon: its manufacturer model (MATERIAL_SPECS_WEAPON) `texture map` / `normal map`.
  - Otherwise the first `MaterialResolver` candidate of submesh 0 (weapons: the manufacturers' models).

## Skeletons

**Mesh mode** chose, first that loads: `--skeleton`; for a mesh used by ARMOUR, CONTAINER, ATTACHMENT or LIMB_REPLACEMENT the body
skeleton (`female_skeleton` if the using field contains "female", else `male_skeleton`), because Kenshi shares the body's skeleton
instance with worn items by bone index ([formats/ogre-skeleton.md](formats/ogre-skeleton.md#binding-worn-meshes-to-the-character));
then the mesh's own link; then `<mesh>.skeleton` beside it. Only meshes with a link (or `--skeleton`) got one. A missing skeleton left
the mesh in its binding pose, with a hint to try `--skeleton male_skeleton`.

**Character mode**: the body mesh's own link (an error if it has none); every shared-skeleton part (ARMOUR, ATTACHMENT hair and beards,
CONTAINER, LIMB_REPLACEMENT; `AttachMode.SharedSkeleton`) was built with the body skeleton's bone count and drawn with the **body's skin
matrices**, by bone handle, whatever its own link says. Bone-attached parts (`AttachMode.Bone`: weapons, sheaths) were built
unskinned and placed by `model = offset × boneDerivedTransform`, where the offset is the attachment point's rotation then translation
in the bone's frame (`CharacterAppearance.AttachmentPoints`: `hands` = `Bip01 Prop2` with zero offset, then the race's `.phs`
points; [characters.md](characters.md#weapons-and-other-bone-attached-items)). A point the race lacks, or a bone the skeleton lacks,
fell back to the root bone. Sheathed = `mesh` at its slot; `--drawn` = `bare sword` at `hands` plus `sheath` at the slot.

## Posing (`Animator`)

`Animator` follows Ogre's v1 node maths with Kenshi's changes ([formats/ogre-skeleton.md](formats/ogre-skeleton.md#bone-maths-in-kenshis-ogre),
"Blending in Kenshi's Ogre"):

- Bones are indexed by **handle** (array size = largest handle + 1) and processed parents before children.
- Each pose starts every bone from its binding position, orientation and scale.
- **Blending** (`Pose(layers)`): weights are clamped at 0; with blend mode average (every base-game skeleton) they are multiplied by
  1 / sum only when the sum is above 1. Two passes: first the layers without override bones, then those with any. In the second pass,
  a layer first pulls each of its override bones' accumulated rotation back toward the binding orientation by its weight (nlerp; fully
  at weight ≥ 0.99). Per track of a layer (skipping tracks of excluded bones, bones past the count and empty tracks):
  position += key translation × weight × movement scale; rotation = rotation × nlerp(identity, key rotation, weight) (local space,
  renormalised); scale ×= 1 + (key scale − 1) × weight.
- **Sampling**: keyframes found by binary search; before the first key or after the last, the end key; between keys, lerp of
  translation and scale and **nlerp along the shortest path** for rotation (Ogre's default linear mode). No spline mode.
- **Derived transforms**: root: derived = local, derived scale = bone size × own scale. Child: derived orientation = parent's × own;
  derived scale = bone size × own scale (**not** inherited); derived position = parent orientation applied to (own position ×
  positional size × the **Y** of the parent's derived scale) + parent's derived position.
- **Skin matrix** per bone (`SkinMatrices`; its doc comment said "posed derived × inverse binding", but the code used Ogre's
  `OldBone::_getOffsetTransform` form): translate(−binding derived position) · scale(derived scale / binding derived scale) ·
  rotate(derived orientation × inverse binding orientation) · translate(derived position) (System.Numerics row-vector order). The scale
  acts along the binding pose's model axes. **Observed**: with the bone-local form (derived × inverse bind) a non-neutral Height opened
  gaps at elbows and shoulders; with this form the body stays closed.
- `BoneTransform(h)` (derived scale × rotation × translation) placed bone-attached items; `BonePosition` / `BoneParent` drew the
  skeleton lines.
- `SetShape(boneSizes, positionalSizes)` set Kenshi's two extra per-bone vectors by bone name (others stay 1, binding pose unchanged);
  `MovementScale` multiplied every track translation.

**`AnimationLayer`**: animation, time, weight (default 1), speed (default 1), loop (always true in practice), excluded bone handles
(ANIMATION `delete *` fields), override bone handles (`override *` fields), label. `Advance(dt)` adds dt × speed and wraps modulo the
length (or clamps when not looping). Layers were **not synchronised**, had **no fades** and no per-layer normalising: Kenshi's
per-layer controller ([animation.md](animation.md#run-time-blending)) was not reproduced. **No root motion** handling either: a
`Bip01` translation track was applied like any other (Kenshi drops X and Z for relocating animations).

**Mesh mode** played one animation at a time at weight 1 without the ANIMATION record preprocessing, so upper/lower animations (`walk
upper`, `walk lower`) moved only part of the body. Time advanced by dt × speed and wrapped.

**Character mode** layers (`CharacterScene.Layer(name, weight)`): `AnimationMask.Find(db, name)` maps an ANIMATION record name (`run
upper stealth`) to its Ogre animation, deleted bones and override bones; an Ogre animation name (`ninjarun`) uses the first ANIMATION
record with that `anim name` ([animation.md](animation.md#in-meitou-meitoudatacharactersanimationmask-viewer-animator)). Instead of
cloning the animation per record (as Kenshi's startup preprocessing does) the source animation is kept and the deleted tracks are
skipped when sampling. So `walk lower` + `walk upper` animate the whole body. Default layers: the `--anim` list, else the body file's
`idle stance` at weight 1 (unless `--bind-pose`). With a body file and without `--no-postures`, `postures`, `neck set` and `shoulder
set` were added at weight 1 and speed 0, held at length × `Posture` / `Neck position` / `Shoulder set` ÷ 100
([animation.md](animation.md#posture-sliders)). With the idle the four layers sum to weight 4, so average mode scales each to 0.25;
that is why `--no-postures` visibly changes the idle.

## Body shape and face poses

- **Body shape** (`CharacterScene.ApplyShape`, formulas in `CharacterShape` and [animation.md](animation.md#body-shape-sliders)): only
  on skeletons with exactly `CharacterShape.HumanBoneCount` (30) bones, as in Kenshi. `CharacterShape.InputFor(db, appearance)` gives
  the sliders (body file or rolled), muscle from the CHARACTER's `stats` STATS record (strength, weapon/armour smithing; an
  approximation), starvation 0, all limbs present, a missing slider counting as 100. `--shape` overrides went through
  `CharacterShape.Override`: Kenshi units, or a fraction when ≤ 3 (`height=0.8` = 80); names case-insensitive with spaces optional
  (`legsbulk=130`); also `muscle=`, `starve=`, `legratio=`, `missing=larm,rarm,lleg,rleg`, `hidestump=0..3`. `CharacterShape.Compute`
  returns bone sizes, positional sizes and the movement scale, handed to `Animator.SetShape` and `MovementScale`.
- **Weapons on bones** inherit the hand's bone size through `BoneTransform` (Kenshi's behaviour there is **Unknown**; Kenshi gives the
  item node 1 / body node scale, ogre-skeleton.md).
- **Feet** are not kept on the ground at non-neutral Height (**Observed**, [animation.md](animation.md#body-shape-sliders)).
- **Face poses** (`ApplyPoses`, unless `--no-morphs`): for every pose of the body mesh with a stored weight in the appearance
  (`CharacterAppearance.PoseWeights`), `CharacterShape.BakeWeight(name, stored, cutHorns)` gives the weight (the stored value unscaled;
  `shaved` characters on a mesh with `bone_horns_top_short` get the cut-horn rule); the offset × weight is added to the part's vertex
  positions (and normals, renormalised, if the pose has them) before upload ([characters.md](characters.md#face-shapes-poses)). The
  pose's target is submesh `target − 1`.

## Skinning on the GPU

Linear blend skinning with four bones per vertex, in the vertex shader (`Shaders.MeshVertex`, stays):
`skin = Σ uBones[bone_k] × weight_k` when the part is skinned and the weights are not all zero, else identity; `world = uModel × skin`;
normal and tangent through `mat3(world)` (no inverse transpose). No dual quaternions.

The bone block, as declared in `Shaders.MeshVertex` and `NativeShaders.BonesBlock`:

```glsl
uniform mat4 uBones[128];                                 // Shaders.MeshVertex (GL-shaped program)

layout(std140, set = 0, binding = 5) uniform MeshBones    // NativeShaders.BonesBlock (native model)
{
    mat4 bones[128];
} meshBones;
```

`Shaders.MaxBones` = 128. The viewer drew through a `LegacyProgram` (step P), where `uBones[128]` sat in the vertex stage's default
uniform block ([renderer-native.md](renderer-native.md) measured the foliage program's vertex default block at 8,272 bytes, 8 KB of it
this same `uBones[128]`; the viewer's block was not measured) and was set once per frame from
`MemoryMarshal.Cast<Matrix4x4, float>(SkinMatrices[0 .. min(count, 128)])`. System.Numerics matrices are row-major with row vectors; GL
reads the same floats column-major, which is the column-vector form of the same transform, so no transpose is needed. In the native
model `MeshBones` (set 0, binding 5, 8 KB, allocated once a frame) is the slot meant for this and was not yet read by any consumer
(renderer-native.md). The human skeletons have 30 bones; bone handles are bytes, so more than 128 would read past the array.

## Character shading (the viewer's own GLSL)

`CharacterRenderer` reused `Shaders.MeshVertex` with its own fragment shader. Inputs: `uShading` (0 item, 1 body, 2 hair), samplers
`uDiffuse`, `uNormal`, `uColourMap`, `uHeadDiffuse`, `uHeadNormal`, `uHeadMask`, `uBodyMask`, `uHairOverlay`, `uBeardOverlay`, and
`uVest0/uVestN0/uVestC0` ... `uVest2/uVestN2/uVestC2`; flags `uHasDiffuse`, `uHasNormal`, `uNormalSwizzled`, `uHasHead`,
`uHasHeadNormal`, `uHasHeadMask`, `uHasBodyMask`, `uHasHair`, `uHasBeard`, `uVestCount`, `uVestColoured0..2`, `uClipNormalAlpha`,
`uDyed`; values `uShirtColour`, `uSkinTone`, `uHairColour`, `uHairAlpha`, `uHairMult`, `uBeardAlpha`, `uDiffuseChannel`,
`uAlphaChannel`, `uAlphaThreshold`, `uColour1`, `uColour2`, `uLightDir`, `uEye`. The order follows Kenshi's character shaders
(**Observed**, HLSL; rules in [characters.md](characters.md#body-shading-observed-hlsl-characterhlsl-main_fs-parameters-verified-where-noted)):

- **Body** (all its textures with border addressing):
  1. body diffuse (fallback (0.7, 0.6, 0.5, 0.2)) and normal at the UV;
  2. head diffuse, head normal and head mask **added** at uv + (0, 1) (head UVs sit in v −1..0 of the body mesh);
  3. skin tone: diffuse × (1 − skin tone × (body mask R + head mask R));
  4. hair overlay at the head UV: mix toward hair colour × clamp(dot(texel, head channel)) by clamp(dot(texel, head alpha channel));
     beard: b = clamp(dot(texel, alpha channel)), mix toward hair colour × b by b;
  5. albedo = rgb, gloss = alpha;
  6. up to three vests at the body UV: coverage = vest normal's alpha; albedo, normal (the `.wy` pair) and gloss mixed toward the
     vest's by it; with a vest colour map and a shirt colour, the vest diffuse is first mixed toward shirt colour × mean(vest rgb) by
     the colour map's R;
  7. normals always decoded swizzled (X in alpha, Y in green).

  Not drawn: blood, the × 0.7 `DARKEN`, `muscleBlend` toward the strong / skinny normal maps, hidden body parts.
- **Hair**: discard where dot(texel, alpha channel) < threshold; albedo = dot(texel, diffuse channel) × hair colour; gloss =
  brightness × 0.3; no normal map.
- **Item**: discard where the normal map's alpha < 0.6 when `uClipNormalAlpha` (Kenshi's `Skinned` template always cuts there,
  **Observed**, HLSL `skin.hlsl`, [characters.md](characters.md#worn-clothing-observed-hlsl-skinhlsl-parameters-in-runtime-materialsmd));
  dye: intensity = mean(diffuse rgb), diffuse mixed toward colour1.rgb × mix(intensity, 1, colour1.a) by the colour map's R, then
  colour2 likewise by G; albedo = rgb, gloss = alpha. Items not on the shared skeleton (weapons) did not clip.

Normal mapping (not for hair): tangent Gram-Schmidt against the normal, bitangent = cross(n, t) × w; back faces flip the normal.

## Lighting, background, grid

Not Kenshi's deferred lighting; a fixed forward model shared by both modes (the mesh mode's `Shaders.MeshFragment` path without the
world's sky, the character shader the same formulas):

- One directional light toward normalize(0.45, 0.8, 0.35), colour (1, 0.97, 0.92); diffuse = max(n·l, 0).
- Hemisphere ambient: mix((0.22, 0.20, 0.18), (0.42, 0.45, 0.50), 0.5 + 0.5 n.y).
- Blinn-Phong: pow(max(n·h, 0), 8 + 56 gloss) × gloss × 0.5 (× `specular mult` in mesh mode), times the diffuse term. Gloss is the
  diffuse alpha (the mesh mode used 0.3 when the diffuse alpha was the cut-out).
- No shadows, no fog, no exposure. Clear colour (0.16, 0.17, 0.19).
- Grid on y = 0: 21 lines each way at a step of 10^floor(log10(radius / 2)); the X axis red, the Z axis blue. Skeleton lines in green
  without depth test. Wireframe: a second pass with polygon mode line, polygon offset (−1, −1) and a flat colour (0.95, 0.75, 0.2).
- Back faces culled unless the material is double-sided (or `B` turned culling off); front faces counter-clockwise.

## LOD

Character mode only (the mesh mode always drew level 0). Each part kept its mesh's generated LOD levels (`CharacterLod.Levels(mesh)`):
level 0's indices followed by every reduced level's indices in **one element buffer**, with an (offset, count) per level and submesh; a
level whose indices pointed past the submesh's vertices fell back to level 0's range. Each frame `CharacterScene.UpdateLod(eye)` picked a
level per part with Kenshi's `distance_sphere` rule (`CharacterLod.Value`: distance from the eye to the mesh file's bounds centre,
transformed by the part's attachment, minus the bounds radius; `CharacterLod.Select` against the level distances: 200 for bodies and
hair, 400 for armour; [characters.md](characters.md#lod-of-character-meshes), [formats/ogre-mesh.md](formats/ogre-mesh.md#lod)), LOD
bias and factor 1. Manual levels (separate meshes) were not loaded: such a level drew level 0. `--lod n` forced level n (clamped per
mesh). The morph bake changes only the vertices, so the reduced levels keep the face shape.

## Camera

`Camera`: an orbit camera, Y up. Vertical field of view 40°. `Frame(centre, radius, yaw, pitch)`: target = centre, distance = radius /
sin(fov / 2) × 1.05. Eye = target + (cos p sin y, sin p, cos p cos y) × distance. Near = max(distance − 4 radius, 0.01 distance,
0.001); far = distance + 30 radius. Orbit: yaw −= dx × 0.01, pitch += dy × 0.01 clamped to ±1.55 rad. Zoom: distance × 0.88^steps,
at least 0.02 radius. Pan moves the target in the view plane by the pixel delta scaled to the target distance. `--zoom z` divides the
distance by z.

Framing: mesh mode used the binding bounds, or the posed bounds (`Model.PosedBounds`) when an animation was playing; character mode
the union of every part's posed bounds, bone-attached parts transformed by their attachment. `F` re-framed.

## Screenshots

- `--screenshot`: headless (`VulkanDisplay` without a window, no vsync), drawn into a **4x multisampled** colour + 24-bit depth
  framebuffer, resolved by a blit into a single-sampled one and written as PNG by `FramebufferCapture.SavePng`.
- `P` in a window: the swapchain image (not multisampled) to `C:\Temp\meitou-viewer-<mesh or character>-<yyyyMMdd-HHmmss>.png`, never
  the working directory (which may be the repo).

## What was verified with it

From docs/viewer.md (labels kept):

- **Verified with screenshots (2026-10-04)**, skinning: `human_male` binding pose and `ninjarun` at 0.2 s (skeleton lines on the
  skinned mesh), `pack_beast` `walk`, `Iron Clad Jacket_F` on `female_skeleton` (its own link is broken).
- **Verified with screenshots (2026-10-04)**, characters: Dust Bandit with `--seed` 1–6 (Samurai Boots, Horse Chopper on the back in
  the Rusting Blade / Rusted Junk texture, or the Junkbow instead; varied skin tones; no beard), Hungry Bandit seeds 1–4 (men and
  women, different heads, haircuts, beards, skin; iron club at the hip). Earlier, before the spawn rules were traced: Dust Bandit
  (Greenlander, helmet hides the hair, sandals win the boots slot with chance 400, horse chopper at the hip with the handle forward),
  the same with a katana drawn in the right hand, sheath at the hip and a second katana diagonally on the back while blending `walk
  lower` + `walk upper sword`, Ruka (Shek woman: horn and face poses, skin tone, leather shirt as a vest layer), Beep (Hive Worker
  Drone with a stick-people head texture and the rag loincloth). Body shape and LOD: Ruka with and without `--no-shape`, Greenlander
  at Height 80 and 120 (no seams at shoulders or elbows), with `muscle=1`, `starve=1` and missing limbs with and without `hidestump`,
  Dust Bandit at forced LOD 0 and 1 (wireframe) and switching by distance.
- **Observed** (Kenshi HLSL, read for facts): the character shader samples the head at uv + (0, 1) and adds it to the body, both with
  border addressing and a transparent black border, and reads the body normal as `.wy`. Screenshots of `human_male` show a correct
  face only with this.
- **Observed** (screenshots): weapons share a UV layout: MATERIAL_SPECS_WEAPON textures (e.g. `01_HI.dds`, `12_HI.dds`) map sensibly
  onto both `katana05` and `chopper01_bare`.
- The conventions (right-handed, Y up, counter-clockwise front faces, textures as stored, a human about 19.5 units tall, the katana the
  game loads 12.8) were checked in these modes: [viewer.md](viewer.md#conventions-verified-by-screenshots).
- `--character "Dust Bandit"` (solid and wireframe) was part of the validation and 0-pixel checks of the native port
  ([renderer-native.md](renderer-native.md)).

## What it never did

- Hidden body parts (`hide parts`, the part map; needs a second per-vertex attribute; rules in
  [characters.md](characters.md#hidden-body-parts-verified--140075600--140071f90--140531cc0-hlsl-skinhlsl)), stumps for missing limbs,
  limb replacements' clipping.
- `muscleBlend` toward the strong / skinny normal maps (rule known), blood, faction colours / dyes, metalness, the `DARKEN` × 0.7.
- Kenshi's deferred lighting, shadows, HDR and exposure (the missing sRGB conversion is correct: Kenshi has none either).
- Kenshi's per-layer animation controller: fades, synchronised layers (`synchs`, `synch offset`), normalising, root motion.
- Manual LOD meshes; LOD in mesh mode.
- Feet on the ground at non-neutral Height.
- Without `--seed`, the likeliest choice of each roll (first head, likeliest hair, neutral sliders), not a rolled character.
- Mesh mode: one texture set per mesh from FCS (per submesh only for script materials); metalness and specular maps beyond
  diffuse-alpha gloss; TERRAIN-mode features, dust, construction scaffold, colour masks; ARMOUR on the ground (its temporary record
  from `vest texture` / `vest normalmap`, runtime-materials.md, was not followed); ground items clipped on normal alpha like the
  world's objects, not with the 0.6 rule; leaves without a normal map drawn opaque.
- No tests of the drawing or the `Animator` itself.
