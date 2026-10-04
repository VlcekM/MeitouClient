# Characters: from records to a dressed body

How Kenshi puts a character together from FCS data: body file, race, head, hair, clothing, weapons, colours
and face shapes. Skeleton binding and bone maths are in [formats/ogre-skeleton.md](formats/ogre-skeleton.md),
animation in [animation.md](animation.md), material building in
[formats/runtime-materials.md](formats/runtime-materials.md). Implemented in `Meitou.Data.Characters`
(`CharacterAppearance`, `AppearanceFile`, `PhysicsAttachmentFile`, `AnimationMask`, `CharacterGenerator`) and shown by
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

`CharacterAppearance.Build` resolves, in this order. Without `--seed` it takes the likeliest choice of each roll
the game makes ([Generating a character](#generating-a-character)); with a seed `CharacterGenerator` rolls them
all and `Build` uses the result (a `Loadout`). Viewer choices are marked.

1. **Race**: CHARACTER `race`, else the body file's; a RACE record is its own race. If none resolves the viewer falls back to `Greenlander` (viewer choice, not game behaviour).
2. **Gender**: the body file's `sex female`; without one, CHARACTER `female chance` ≥ 50 (viewer choice; the
   game rolls it). A `single gender` race always uses the male mesh.
3. **Body mesh**: CHARACTER `mesh`, else RACE `male mesh` / `female mesh`; its skeleton is the mesh's own link
   (Verified, animation.md).
4. **Head**: the body file's `head`, else the race's first `heads male` / `heads female` (viewer choice).
   HEAD records are textures only (`texture map`, `normal map`, `mask map`).
5. **Hair and beard**: the body file's `hair style` / `beard`; without a body file the race's `hairs` entry of
   the highest weight that has a mesh (viewer choice). CHARACTER `shaved` removes the hair (viewer: drops it).
6. **Clothing**: CHARACTER `clothing` (ARMOUR), one item per ARMOUR `slot`, entries (quantity, chance) as in
   [Rolling clothing](#rolling-clothing); the viewer takes the highest chance
   per slot among the eligible entries. Slots in use: AttachSlot 3 HAT, 5 BODY, 6 LEGS, 8 SHIRT, 9 BOOTS
   (Verified by survey). RACE `no hats` / `no shirts` / `no shoes` drop items of slots 3 / 8 / 9 before a mesh
   is made (**Verified**, `@ 140537690`) as well as when the clothing is rolled. `backpack` (CONTAINER) is worn
   when its chance is ≥ 50 (viewer choice).
7. **Weapons**: CHARACTER `weapons`, rules in [Rolling weapons](#rolling-weapons). Without a
   seed the viewer shows the first entry of the hip pool at `hip` and the first of the back pool at `back` (with
   the base data's chances of 100 the first entry of a pool is what the game always picks); `--equip` adds more
   (to `hip`, `back`, then `back2`).

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

## Generating a character

What Kenshi does when it spawns an NPC from a CHARACTER record. Sources: `RootObjectFactory::process`
(`@ 140581770`), `CharacterHuman::setupInventorySections` (`@ 14062c4e0`) and the functions named below
(decompiled, read for facts only). Implemented by `CharacterGenerator` (+ `Loadout`, `AppearanceLimits`) and used
by `meitou-viewer --character ... --seed n` ([viewer.md](viewer.md#characters)). The generator follows these
rules with its own random numbers (`System.Random` per seed), not the game's sequence.

**Reference values**: the code reads a reference's three ints at offsets 0, 4, 8 (Verified); below they are
called val0, val1, val2, taken to be the file order (Observed: e.g. clothing is (1, 100, 0) in 3,151 entries,
matching fcs.def's default "(1, 100)" for quantity and chance; fcs-mod.md). fcs.def's text for `clothing`
counts from 1 ("val1 is quantity, val2 is percentage chance"), for `weapons` from 0.

**The game's random helpers** (Verified, `@ 1409b1c10`, `@ 1409b1c80`, `@ 1409b3b40`, `@ 1409b32b0`): all use the
C runtime's `rand()`; a unit float is rand × 2⁻¹⁵, a float range a + unit × (b − a), an integer range
a..b inclusive (unit × (b − a + 1), clamped). Slider values are rounded half away from zero.

**Weighted picks.** Three kinds occur (Verified):

- *Relative*: running sums of the weights, roll uniformly in [0, total), take the first entry whose running sum
  exceeds the roll (a one-entry list is taken without a roll).
- *Absolute* (chances out of 100): the same running sums, but the roll is in [0, 100), and nothing is picked when
  the roll is not below the total. So entries whose chances add up to 100 or more always give the first entry
  that reaches 100: with the base data's chances of 100, only the first listed entry of a pool ever spawns.
- *Normalised* (heads, hair): weights divided by their sum into a cumulative list, roll in [0, 1), take the first
  entry whose cumulative value is ≥ the roll, else the last.

### Order (Verified, `@ 140581770`)

1. **Race** (`@ 1407ff1a0`): a squad template's `race override`, else the CHARACTER's `race` list (entries with
   val0 > 0, relative by val0).
2. **Gender** (`@ 140076500`): female when a unit roll is below `female chance` × 0.01, never for a
   `single gender` race.
3. **Body file**: if CHARACTER `body` loads, it is the appearance (Observed: for some characters the gender roll
   is applied to it afterwards; the condition, a flag of the faction, was not identified). Otherwise a random
   appearance is rolled (below).
4. **Inventory** (`@ 14062c4e0`): backpack, robot limbs, clothing, crossbow, weapons, blueprints, inventory items.

### Random appearance

For characters without a body file (`@ 14007c880`, `@ 14007b310`, `@ 14007b940`, `@ 14007adc0`; tables built
`@ 140074680`, `@ 140075180`), all **Verified** unless marked:

- **Head**: the race's `heads male` / `heads female` that have a `texture map`, val0 as weight, normalised pick.
- **Hair and beard**: the race's `hairs` split by ATTACHMENT `attach slot` 2 (hair) and 13 (beard); women get no
  beard list. Weight val0 clamped to 1–100, normalised pick. Records without a mesh (`Bald`, `No beard`) are real
  options. When the character's FACTION lists `hairstyles`, the weights are restricted to hairs of that list (and
  renormalised) as long as any of them remains in the slot; e.g. Dust Bandits (FACTION) allow only `No beard`
  among the beards. The faction comes from the spawn, not from the CHARACTER: the Dust Bandit CHARACTER has no
  `faction` (Observed), so the viewer takes `--faction`.
- **Hair colour**: a relative pick from the race's `hair colors` (val0, 0 counting as 100); its `color 1` is
  converted with `ColourValue::getHSB` and stored × 100 as `Hair Colour`, `Hair Saturation`, `Hair Brightness`.
  Without any: brightness 100, saturation 0.
- **Sliders** from the race's `editor limits` XML (`data/editor/editor_data_*.xml`, read by
  `AppearanceManager::loadEditorXMLData` `@ 140077090`): a `Config` with `gender="Female"` is the female one,
  any other (or none) the male one; a single-gender race reads only the male one. Categories Face and Body hold
  `Range`s (`name`, `min`, `max`, optional `mid`, `target`, `target_opposite`, `random_group`,
  `random_variation`) and `ColourRange`s (a list of `Colour r g b` in 0–255). Per range: mid = `mid` clamped to
  [min, max], else round(min + (max − min) × 0.5); variation = round((max − min) × `random_variation` × 0.01),
  else × CONSTANTS `appearance random deviation percentage` (fcs.def default 0.5; the base data stores 0.4,
  Observed). A ColourRange spans 0..(n − 1) × 10 and its value v is the colour at index v × 0.1, blended
  linearly with the next.
- **Rolling a slider**: value = round(mid + variation × (2u − 1)) for a unit roll u; ranges with the same
  `random_group` share one u. Not clamped to min/max. Face ranges without `target` and all Body ranges (and
  colours) are rolled per character and stored under their names (`Height`, `Skin Tone`...), the same keys as in
  `.bod2` files.
- **Skin tone**: rolled again, uniformly over the ColourRange's whole span (integer in [min, max]); Observed: only
  for characters whose faction lacks a flag (likely: not the player's).
- **Face poses** (`@ 14007c070`, `@ 14007b7c0`, `@ 140070010`): Face ranges with a `target` are not rolled per
  character. Instead the character gets `morph index` = floor(u × RACE `morph num`), and the face mesh of that
  index is built once: its name is made from the race, the morph index and one more value (not traced), the
  generator is seeded with `srand(FNV-1a 32 of that name)`, and every target range is rolled as above; the value
  v goes to pose `target` as v × 0.01, or, with a `target_opposite`, a negative v goes to the opposite pose as
  v × −0.01 (both poses start at 0). So NPCs of one race and gender share `morph num` (5) faces. The generator
  reproduces that by seeding the face from (race, gender, morph index).
- `idle stance` is set to `idle_stand_normal` (Observed, in the default-appearance function `@ 14007a490`), and
  `Posture` is copied from a template appearance (not traced).

### Rolling clothing

**Verified**, `@ 1405815a0` (slot order), `@ 140580390` (per slot), `@ 14062c4e0` (quality), `@ 140580750` (material).

- Slots in this order: SHIRT (unless RACE `no shirts`), HAT (unless `no hats`), BOOTS (unless `no shoes`; a second
  condition was not identified), BODY, LEGS, BELT. Other slots are never filled from `clothing`.
- Per slot: entries with quantity (val0) ≠ 0 and chance (val1) > 0 whose ARMOUR `slot` is the slot and which the
  race may wear (`RaceLimiter`; Observed: presumably ARMOUR `races` / `races exclude`); relative pick by val1. A
  picked entry with quantity < 1 means no item. **Quantity 0 entries never spawn.** Observed in the data: 472
  clothing entries are (0, 100, 0), e.g. Dust Bandit's Wooden Sandals (0, 400), so Dust Bandits always wear
  their Samurai Boots.
- **Quality**: CHARACTER `armour grade` (ArmourRarity), plus 1 when the grade is below 5 and an integer roll
  0..100 is below `armour upgrade chance`. Grade to item quality: 0 → 5, 1 → 20, 2 → 40, 3 → 60, 4 → 80,
  5 → 95 (any other value 20) (`@ 1406210f0`).
- **Material**: a relative pick from the ARMOUR's `material` list (MATERIAL_SPECS_CLOTHING), val1 with 0 counting
  as 100 (entries gated by `world state` conditions, not followed).
- Backpack: CHARACTER `backpack` entries with val0 > 0 the race may wear, absolute pick by val1.

### Rolling weapons

**Verified**, `@ 14062c4e0`, `@ 140580750`, `@ 1405742f0`, `@ 14057fd40`, `@ 1405d24b0`.

- CHARACTER `weapons` entries with **val0 (quantity) > 0** form two pools by **val1: 0 = hip, anything else =
  back**. Weight val2 (0 counts as 100), absolute pick per pool, so a character carries at most one weapon of
  each pool (plus copies). Observed in the data: 481 entries are (0, 100, 0) and never spawn, e.g. Dust Bandit's
  Topper and Katana; its Horse Chopper (1, 100, 0) always goes on the back (unless it rolls a crossbow).
- **Crossbow** first: CHARACTER `crossbows` entries with val0 > 0, absolute pick by val1 (Dust Bandit's Junkbow:
  15). With a crossbow, weapons of `inventory footprint height` ≥ 2 are skipped.
- **Manufacturer**: a relative pick from CHARACTER `weapon level` (WEAPON_MANUFACTURER, val0, 0 counting as 100);
  with an empty list `917-gamedata.base` (Ancient).
- **Model**: a relative pick from the manufacturer's `weapon models` (MATERIAL_SPECS_WEAPON) by val1 (0 counts as
  100); the model entry's val0 is the weapon's level. With no model (or level 0) the game uses manufacturer
  `1057-gamedata.base` and model `1058-gamedata.base`, level = that entry's val0 + the model's `overall level`.
  A manufacturer's `weapon types` is used only when no weapon is given.
- The weapon goes into the inventory section `hip` (1 slot) or `back` (2 slots); a quantity above 1 adds one
  copy to the same section. **`back` becomes `back2`** when the item's `inventory y` (its row in the section,
  item field read from save key `inventory y` `@ 140761550`) is above 0, i.e. for the second back item.
  Unknown: where the crossbow goes (the viewer puts it at `back`, a back weapon then at `back2`) and what
  happens to an extra hip copy (not shown).

## Weapons and other bone-attached items

**Verified**, `@ 1405373e0`, `@ 1407e3b80`, `@ 1405d24b0`, `@ 1405dbd80`, `@ 1405dbf80`.

- Per race, Kenshi builds a table of attachment points: first `hands` = bone `Bip01 Prop2` with zero offset
  and identity rotation, then every point of the RACE `attachment points` file (`.phs`,
  [formats/phs.md](formats/phs.md)), named after its actor. For humans
  (`data/ragdoll/attachments-weapons.phs`): `hip` on `Bip01 Pelvis`, `back` and `back2` on `Bip01 Spine2`.
- A **sheathed** weapon (and an ITEM, CONTAINER, CROSSBOW or ARMOUR carried that way) uses its `mesh`
  (fcs.def: "Sheathed if its a weapon") at its slot name; a slot named `back` becomes `back2` when the
  item's `inventory y` (its row in the inventory section) is above 0 ([Rolling weapons](#rolling-weapons)).
- **Drawing** puts the WEAPON `bare sword` mesh at `hands` and the `sheath` mesh at the slot point (an item
  whose type has no bare mesh uses `mesh`).
- The entity is then attached with `attachObjectToBone(bone, offset rotation, offset position)`
  (ogre-skeleton.md, mode 0); an unknown bone falls back to the root bone.

## Body shading (Observed, HLSL `character.hlsl` main_fs; parameters Verified where noted)

The body is one material layering several textures (all with border addressing on a transparent black border,
`character.material`):

1. Body diffuse and normal (RACE `body texture *`, `nm *`; the normal is blended toward `nm * strong` or
   `nm * skinny` by `muscleBlend`, from the character's stats and starvation: **Verified** `@ 140531820`, rule in
   [animation.md](animation.md#muscle-definition-and-the-bodys-normal-maps-verified--14052bc70--140531820); the
   viewer doesn't render the blend).
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
7. Body parts hidden by armour (see below): a per-vertex part mask against a `hiddenMask` constant; the
   fragment is clipped where any bit matches. Not reproduced by the viewer (it needs a second per-vertex
   attribute).

### Hidden body parts (Verified, `@ 140075600`, `@ 140071f90`, `@ 140531cc0`; HLSL `skin.hlsl`)

When a body entity is built, Kenshi adds a vertex element (`uint2`, TEXCOORD1 in `skin.hlsl`: x = blood
zone, y = part bits) to a clone of the body mesh:

- **Part bits from the part map.** RACE `part map male` / `part map female` is loaded as an image (DXT
  formats are refused: "Image error: Part map for race '...' is a dxt format. Convert this to png instead.";
  the base game uses PNGs in `character/skins/masks/`). Each vertex samples the pixel at (⌊u × width⌋,
  ⌊v × height⌋), clamped to the image; a pixel with alpha 0 adds nothing; otherwise its RGB must be one of
  these, giving the bit (anything else logs "Part map contains invalid colour: #..."):

  | Colour | RGB | Bit | PartMapColours |
  | --- | --- | --- | --- |
  | White | FFFFFF | 0x1 | White |
  | Red | FF0000 | 0x2 | Red |
  | Green | 00FF00 | 0x4 | Green |
  | Blue | 0000FF | 0x8 | Blue |
  | Yellow | FFFF00 | 0x10 | Yellow |
  | Magenta | FF00FF | 0x20 | Magenta |
  | Cyan | 00FFFF | 0x40 | Cyan |
  | Orange | FF8000 | 0x80 | Orange |
  | Purple | 8000FF | 0x100 | Purple |
  | Teal | 008080 | 0x200 | (not in the enum) |
  | Black | 000000 | none | |

  So ARMOUR `hide parts` (a PartMapColours bitset) is bit n for the enum's n-th colour. **Observed** (data):
  `MaleMask_Default.png` / `FemaleMask_Default.png` (256×256, 11 races) use only white, red and green, and
  `robot_mask.png` only red; ARMOUR `hide parts` values in the base game are 0 ×123, 1, 2, 3 ×2 each, 4, 5 ×3,
  7 ×7 (all body-slot items, e.g. Drifter's Leather Jacket 1, Sleeveless Longcoat 2, Ninja Gi 4, Plate Jacket
  7). Which body region each colour marks was not looked at.
- **Limb bits** (bits 12–15, limb 0 left arm ... 3 right leg): each vertex's dominant bone is mapped to a RACE
  `combat anatomy` part (LOCATIONAL_DAMAGE `bone name`s, `body part type` 2 arm / 1 leg, the side from
  `collapse part` having a right-side RagdollPart bit, mask 0x12). The limb bit is set if the vertex's
  bind-pose Y is below a cut height: 14.1 (arms) / 8.6 (legs) for women, 13.83 / 8.6 for men.
- **`hiddenMask`** (`@ 140531cc0`, set on the body material's vertex program): the OR of every worn item's
  `hide parts`, plus bit (`slot` − 38) for each worn LIMB_REPLACEMENT (LimbSlot 50–53 → bits 12–15), plus
  the character's missing limbs as bits 12–15. A missing or replaced limb thus clips the body below the cut
  height, and the stump / robotic limb mesh takes its place.

`hide stump` (ARMOUR, body slot): see [animation.md](animation.md#the-formulas-verified-decompilation-and-disassembly-of--14052e6c0-implemented-in-charactershape)
— it zeroes the upper arm of a missing arm (bits: 1 left, 2 right, 3 both; base game: 0 ×113, 1 ×2, 2 ×2, 3 ×25).

### Hair meshes (Observed, HLSL; parameters Verified `@ 140532600`)

The hair entity gets a clone of the hair material with `diffuseMap` = ATTACHMENT `texture map`
(`... female`), one-hot vectors `diffuseChannel` = `hair diffuse channel` and `alphaChannel` = `hair alpha
channel`, `threshold` from `alpha rejection`, `specularMult` from `specular`, `mipmap bias`, and `color` =
the hair colour. The shader cuts where dot(texel, alphaChannel) < threshold and outputs
dot(texel, diffuseChannel) × colour.

**Hair colour** (Verified, `@ 140532600`): `ColourValue::setHSB(Hair Colour × 0.01, Hair Saturation × 0.01,
Hair Brightness × 0.01)` from the body file. Ogre wraps the hue into 0–1 and clamps saturation and brightness
to 0–1, so Ruka's brightness −35 gives black hair. Without a body file the viewer uses `color 1` of the race's
likeliest `hair colors` COLOR_DATA; `--seed` rolls it the game's way ([Random appearance](#random-appearance)).

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
viewer adds offset × weight. Rolled faces store slider value × 0.01 as the weight (Verified, [Random
appearance](#random-appearance)), consistent with the files' values. **Verified** (`@ 140071ac0`): the bake uses
the stored value as the weight, no scaling or clamping, and skips zeros. The one exception: when the caller asks
for it (Observed: presumably CHARACTER `shaved`, "Sheks will have their horns cut") and the mesh has a
`bone_horns_top_short` pose, the cached morph's name gets `SLAVE` appended and `bone_horns_top_short` /
`bone_horns_bottom_short` are baked at weight 1 (when their stored value is non-zero), `bone_horns_curved` not at
all. The viewer does the same for `shaved` characters (`CharacterShape.BakeWeight`). **Observed** (editor limits
`data/editor/editor_data_*.xml`): face sliders run about −200..200 with a `target` pose and an optional
`target_opposite`; the files' weights reach ±2 (e.g. `tiltdown_brow` 1.72). **Verified**
(`OgreMeshReader`, every base-game mesh): the 13 meshes with poses parse to the end; all poses target
submesh 0 and store no normals, so only positions move.

## LOD of character meshes

Bodies, hair and armour use their meshes' own generated LOD levels (no separate LOD meshes); each entity picks
its level on its own with the `distance_sphere` rule ([formats/ogre-mesh.md](formats/ogre-mesh.md#lod)). The rule now lives in `Meitou.Data.Ogre.MeshLod`; `CharacterLod` forwards to it.
**Verified** (scratch survey with `OgreMeshReader` of the meshes the base game's records name; `CharacterLodTests`
checks `human_male.mesh`):

| Records' meshes | LOD |
| --- | --- |
| RACE `male mesh` / `female mesh` | counted per record field: 2 levels at 200 for 18 male / 11 female (`human_male`, `bone_male`, `robot`, the three `Stick_person`), 2 at 400 for 32 (animals), none for the rest (`human_female`, `bone_female`, ...) |
| ATTACHMENT hair (`haircut_*`) | 2 levels at 200 (81 references); animal fur 400 or none |
| ARMOUR `mesh` / `mesh female` | 2 levels at 400 for 193 references (masks, hats, coats... meshes with no skeleton link), none for the 51 to meshes with a skeleton link (`samurai pants` ...) |
| CONTAINER backpacks | 2 levels at 400 |
| LIMB_REPLACEMENT, WEAPON (`mesh`, `bare sword`, `sheath`) | none |

So a male human body drops to its reduced level once the camera is 200 units (about ten body heights) beyond its
bounding sphere, its armour at 400. The morph bake (above) changes only the shared vertex buffer, so the reduced
level keeps the face shape (Observed, Ogre's `Mesh::clone`).

## Colour space (Observed)

Kenshi does no sRGB conversion: the executable has no sRGB or gamma strings besides Ogre's `getAsRGBA`, no
material script asks for gamma-corrected textures, and the tone mapper's final gamma step is switched off
(`hdr_to_gamma = false` in `materials/common/constants.hlsl`). Textures are sampled and lit as stored, which
the viewer does too.

## Open questions

- ~~Body-shape slider formulas~~, ~~`hide parts` bits~~, ~~`hide stump`~~, ~~`muscleBlend`~~: answered (animation.md
  "Body shape sliders", "Hidden body parts" above). Left: which body region each part-map colour marks, and the
  stats/nutrition fields listed in animation.md's open questions.
- Random appearance details: the untraced part of the morph-face name, the faction flag that re-rolls the skin tone
  (and a body file's gender), the template `Posture`; crossbow placement; extra hip copies; `RaceLimiter` rules.
