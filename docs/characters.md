# Characters: from records to a dressed body

How Kenshi puts a character together from FCS data: body file, race, head, hair, clothing, weapons, colours
and face shapes. Skeleton binding and bone maths are in [formats/ogre-skeleton.md](formats/ogre-skeleton.md),
animation in [animation.md](animation.md), material building in
[formats/runtime-materials.md](formats/runtime-materials.md). Implemented in `Meitou.Data.Characters`
(`CharacterAppearance`, `AppearanceFile`, `PhysicsAttachmentFile`, `AnimationMask`) and shown by
`meitou-viewer --character` ([viewer.md](viewer.md#characters)). Status labels as in [README.md](README.md).

Sources: decompilation of `kenshi_x64.exe` (Ghidra 12.1.4; addresses are function entry points), Kenshi's
HLSL under `data/materials/deferred/` (read for facts only, marked "HLSL"), `fcs.def` descriptions, and the
base game's records. Nothing here was checked in game.

## Body files (`.bod2`)

CHARACTER `body` names a body file ("Leave blank for random body", fcs.def). **Verified** (FcsReader on the
files, `CharacterAppearanceTests`): a `.bod2` is an ordinary FCS file (no header in type 15) holding one
record of type 66, CHARACTER_APPEARANCE, typically named `default` with id `1--INGAME`. Of the 80
CHARACTER records (merged load order) that name a body file, 60 point at type 15 and 4 at type 16 files (read by `AppearanceFile`); 14 at
legacy layouts (types 5, 8, 9, 10, 13: the `.body` files and three `.bod2`), and 2 at files that don't ship
(`Crow Face.bod2`, `Cat.bod2`). Observed fields (Ruka.bod2, Beep.bod2):

| Kind | Fields |
| --- | --- |
| bool | `sex female` |
| float | body and face sliders (`Height`, `Waist`, `Head size`, `Posture`, `Neck position`, `Shoulder set`, `Hair Colour`, `Hair Brightness`, `Hair Saturation`, `Skin Tone`, `Age`...; mostly 0–200 with 100 neutral) and **pose weights keyed by the body mesh's pose names** (`bone_wide_jaw = 0.37`, `stick2_long_horns = -0.6`...) |
| int | `Age` |
| vec3 | `Skin Tone` (RGB, e.g. (0.99, 0.90, 0.90)) |
| string | `head` (HEAD id), `hair style`, `beard` (ATTACHMENT ids, may be empty), `idle stance` (an animation name, e.g. `idle_stand_relax`) |
| reference | `race` (RACE) |

CHARACTER `race` overrides the body file's race (fcs.def "will override race in .body file"; the viewer
follows this, not traced).

## Parts

`CharacterAppearance.Build` resolves, in this order (viewer choices marked):

1. **Race**: CHARACTER `race`, else the body file's; a RACE record is its own race. If none resolves the viewer falls back to `Greenlander` (viewer choice, not game behaviour).
2. **Gender**: the body file's `sex female`; without one, CHARACTER `female chance` ≥ 50 (viewer choice; the
   game rolls it). A `single gender` race always uses the male mesh (viewer choice).
3. **Body mesh**: CHARACTER `mesh`, else RACE `male mesh` / `female mesh`; its skeleton is the mesh's own link
   (Verified, animation.md).
4. **Head**: the body file's `head`, else the race's first `heads male` / `heads female` (viewer choice).
   HEAD records are textures only (`texture map`, `normal map`, `mask map`).
5. **Hair and beard**: the body file's `hair style` / `beard`; without a body file the race's `hairs` entry of
   the highest weight that has a mesh (viewer choice). CHARACTER `shaved` removes the hair (viewer: drops it).
6. **Clothing**: CHARACTER `clothing` (ARMOUR) grouped by ARMOUR `slot`; each entry is
   (quantity, chance) — **Observed** from the data: 3,151 of about 3,900 entries are (1, 100), chances run
   from −50 to 2000, at least 46 entries have quantity −1 (fcs.def: a "no item" option) and at least 482 have quantity 0
   (meaning Unknown; e.g. Dust Bandit's Wooden Sandals (0, 400)). fcs.def: chances are normalised among items
   of one type. The viewer takes the highest chance per slot, counting quantity 0 as an item and negative
   quantities as "nothing" (or a weighted roll with `--seed`).
   Slots in use: AttachSlot 3 HAT, 5 BODY, 6 LEGS, 8 SHIRT, 9 BOOTS (Verified by survey).
   RACE `no hats` / `no shirts` / `no shoes` drop items of slots 3 / 8 / 9 before a mesh is made
   (**Verified**, `@ 140537690`). `backpack` (CONTAINER) is worn when its chance is ≥ 50 (viewer choice).
7. **Weapons**: CHARACTER `weapons`. **Unknown**: which entry Kenshi picks and what the three values mean in
   practice. fcs.def says "val0 is quantity, val1 is slot (0=hip, 1=back), val2 is absolute chance", but the
   base data mostly stores (0, 100, 0) ×481, (1, 0, 0) ×321, (1, 100, 0) ×172, (1, 0, 100) ×55. The viewer
   shows the first listed weapon at `hip`; `--equip` adds more (the second goes to `back`, further ones to
   `back2`).

Hats hide the hair mesh when ARMOUR `hide hair` is set (default true, "hats only"); `hide beard` hides the
beard (fcs.def; the viewer applies both).

### Meshes per item (Verified, `@ 140537690`, `@ 1405358a0`)

- The field is `mesh`, or `mesh female` for women; ARMOUR uses `overlap mesh` (`... female`) instead when
  one of its `overlap items` is worn too. Shared-skeleton items (ARMOUR, CONTAINER, ATTACHMENT,
  LIMB_REPLACEMENT) never fall back from `mesh female` to `mesh` ("[Appearance] No female mesh for ..."),
  bone-attached ones do. Records of those types with neither mesh get no entity.
- **The path is reduced to its bare file name** (`FUN_1409b4610`, the same helper the material builder uses
  for textures) and the entity is created from that name in the autodetect group. So which file loads is
  decided by the resource index, where the last-registered location wins within a group
  ([formats/ogre-material.md](formats/ogre-material.md#lookup-observed-source-for-ogres-rules)). For
  `katana05.mesh` both copies are in `General`; `./data/items/weapons/mesh` (resources.cfg line 90) comes after
  `./data/meshes` (line 21), so the game loads `data/items/weapons/mesh/katana05.mesh` (v1.8, 12.8 units
  long) — the path the WEAPON record names anyway. `data/meshes/katana05.mesh` (1.6 units) is a different,
  smaller model that a weapon never shows. Verified for the name reduction (decompiled); the index rule is
  Ogre's (Observed (source)).
- **Weapon scale**: none. A mode-0 item's node gets scale 1 / body node scale (ogre-skeleton.md), and the
  weapon meshes are modelled at character scale (katana 12.8 units, human 19.5). Observed by screenshots: the
  katana fits the hand and the hip.

## Weapons and other bone-attached items

**Verified**, `@ 1405373e0`, `@ 1407e3b80`, `@ 1405d24b0`, `@ 1405dbd80`, `@ 1405dbf80`.

- Per race, Kenshi builds a table of attachment points: first `hands` = bone `Bip01 Prop2` with zero offset
  and identity rotation, then every point of the RACE `attachment points` file (`.phs`,
  [formats/phs.md](formats/phs.md)), named after its actor. For humans
  (`data/ragdoll/attachments-weapons.phs`): `hip` on `Bip01 Pelvis`, `back` and `back2` on `Bip01 Spine2`.
- A **sheathed** weapon (and an ITEM, CONTAINER, CROSSBOW or ARMOUR carried that way) uses its `mesh`
  (fcs.def: "Sheathed if its a weapon") at its slot name; a slot named `back` becomes `back2` when an
  item counter (not identified) is above 0.
- **Drawing** puts the WEAPON `bare sword` mesh at `hands` and the `sheath` mesh at the slot point (an item
  whose type has no bare mesh uses `mesh`).
- The entity is then attached with `attachObjectToBone(bone, offset rotation, offset position)`
  (ogre-skeleton.md, mode 0); an unknown bone falls back to the root bone.

## Body shading (Observed, HLSL `character.hlsl` main_fs; parameters Verified where noted)

The body is one material layering several textures (all with border addressing on a transparent black border,
`character.material`):

1. Body diffuse and normal (RACE `body texture *`, `nm *`; the normal is blended toward `nm * strong` by a
   `muscleBlend` parameter, whose source was not traced).
2. Head diffuse and normal added at uv + (0, 1) (head UVs sit in v −1..0).
3. **Skin tone**: diffuse × (1 − `skintone` × (body mask R + head mask R)); body mask = RACE `body mask *`,
   head mask = HEAD `mask map`. `skintone` = 1 − the body file's `Skin Tone`, per channel (**Verified**,
   `@ 140531430`).
4. **Hair and beard on the skin**: ATTACHMENT `head texture` (`head texture female`) sampled at the head UVs;
   alpha = dot(texel, one-hot `head alpha channel`), brightness = dot(texel, one-hot `head channel`); the skin
   is blended toward hair colour × brightness by that alpha (beard: hair colour × alpha). Channel fields read
   at `@ 140532600` (Verified); the blend is HLSL.
5. **Clothing layers** ("vest"): ARMOUR `vest texture`, `vest normalmap`, `vest colormap` (`... female`) at the
   body UVs; the vest normal's alpha is the coverage: diffuse, normal (X/Y) and gloss blend toward the vest's by
   it; the vest colour map's R mixes in shirt colour × vest brightness.
6. Blood; then the result × 0.7 (`DARKEN`) into the G-buffer. Normals are "DXT5" swizzled (X in alpha, Y in
   green).
7. Body parts hidden by armour: RACE `part map *` (a PNG) and ARMOUR `hide parts` (PartMapColours bitset) feed
   a clip in the vertex stage. **Unknown**: the bit-to-colour mapping (values 1–7 occur); not reproduced.

### Hair meshes (Observed, HLSL; parameters Verified `@ 140532600`)

The hair entity gets a clone of the hair material with `diffuseMap` = ATTACHMENT `texture map`
(`... female`), one-hot vectors `diffuseChannel` = `hair diffuse channel` and `alphaChannel` = `hair alpha
channel`, `threshold` from `alpha rejection`, `specularMult` from `specular`, `mipmap bias`, and `color` =
the hair colour. The shader cuts where dot(texel, alphaChannel) < threshold and outputs
dot(texel, diffuseChannel) × colour.

**Hair colour** (Verified, `@ 140532600`): `ColourValue::setHSB(Hair Colour × 0.01, Hair Saturation × 0.01,
Hair Brightness × 0.01)` from the body file. Ogre wraps the hue into 0–1 and clamps saturation and brightness
to 0–1, so Ruka's brightness −35 gives black hair. Without a body file the viewer uses `color 1` of the race's
likeliest `hair colors` COLOR_DATA (the game's randomiser `@ 14007adc0` was not traced).

### Worn clothing (Observed, HLSL `skin.hlsl`; parameters in runtime-materials.md)

Items on the shared skeleton use the `Skinned` template. Its fragment program **always cuts where the normal
map's alpha is below 0.6** (independent of ItemShader), so clothing cut-outs live in the normal map's alpha.
With a dye (`COLOURING`): intensity = mean of the diffuse RGB; diffuse is blended toward
`color1.rgb × lerp(intensity, 1, color1.a)` by the colour map's R and likewise `color2` by G; the colour
map's B is metalness. `color1/2` come from the dye's `color 1` / `color 2` with alpha = the material's
`paint factor 1/2` (runtime-materials.md). The viewer dyes with ARMOUR `color`, else CHARACTER `color` when
`dont colorise` is false (faction colours not followed).

## Face shapes (poses)

Kenshi bakes every non-zero pose weight into a clone of the body mesh (Verified, ogre-mesh.md). **Observed**:
the body files store pose weights directly under the pose names (`bone_*` for Shek, `stick2_*` for the
third stick mesh, unprefixed for humans), e.g. Ruka has 24 non-zero of the female Shek mesh's 35 poses. The
viewer adds offset × weight; whether Kenshi scales the stored value first is Unknown. **Verified**
(`OgreMeshReader`, every base-game mesh): the 13 meshes with poses parse to the end; all poses target
submesh 0 and store no normals, so only positions move.

## Colour space (Observed)

Kenshi does no sRGB conversion: the executable has no sRGB or gamma strings besides Ogre's `getAsRGBA`, no
material script asks for gamma-corrected textures, and the tone mapper's final gamma step is switched off
(`hdr_to_gamma = false` in `materials/common/constants.hlsl`). Textures are sampled and lit as stored, which
the viewer does too.

## Open questions

- Which `weapons` entry a CHARACTER gets and what its values mean (see Parts).
- The body-shape slider formulas (`@ 14052e6c0`, gender-specific constants; animation.md) — the viewer does
  not apply body proportions.
- `hide parts` bits versus part-map colours; `hide stump`.
- The game's random choices for bodies without a file (head, hair, colours, sliders: `@ 14007adc0`).
- `muscleBlend` (strong/skinny normal maps).
