# Characters and appearances

Original race/loadout/appearance/body/attachment/material research from FCS, .bod2/.phs assets,
binary functions and shaders. Assembly: CharacterGenerator/CharacterAppearance/CharacterShape;
rendering: [character-renderer](character-renderer.md), animation: [animation](animation.md).

## Assembly and generation

Resolve race, sex, body/head/hair/beard, body sliders and clothing/weapon/backpack from record
references and .bod2 overrides. Keep generated appearance/loadout seed separate from per-frame
live stats. Record lists' weighted chances, forced equipment, quality/manufacturer rolls and
race restrictions have distinct semantics; missing references do not imply a universal item.
Worn records' lanterns (and an animal's backpack lantern) become `CharacterAppearance.Lights` ([lights](formats/lights.md#carried-lanterns)).
**Original random sequence** (**Verified** by decompile 2026-10-21 unless marked): every draw is MSVCR100 `rand()` (unit `rand()/32768`,
`FUN_1409b1c10`; range `a + unit·(b−a)`, `FUN_1409b1c80`; integer `clamp(a + (int)(unit·(b−a+1)), a, b)`, `FUN_1409b3b40`; weighted pick: one draw, first
cumulative weight ≥ r, `FUN_14006e1a0`), seeded once with `srand(time())` at startup (`FUN_1409b1be0`) and reseeded by world streaming (buildings
`FUN_1405609e0`, foliage cells) and by a face-morph mesh's creation (`srand(FNV-1a-32(mesh name))`, `FUN_14007c070`, only when not cached). So the
original never gives the same people twice: only its algorithm, draw order and distributions can be matched. Draw order of a new character
(`FUN_140581770`): race (one draw unless one candidate) → gender (`female chance`, no draw for a single-gender race) → head (`FUN_14007c880`; a
cumulative pick draws nothing when the list is empty) → head again with the face sliders (`FUN_14007b310`; Face sliders only: no poses, no
colours) → beard (men; no draw for women), hair, hair colour (`FUN_14007adc0`) → body sliders, then body colours, then the skin re-roll
(`FUN_14007b940`, CHARACTERs) → morph index → money chance and amount → bounty, name, personality (`FUN_14062b210`). A list of ranges
(`FUN_14006fcf0`, colours `FUN_140070460`, poses `FUN_140070010`) draws its ungrouped ranges in list order, then once per `random_group` in the
order of a boost `unordered_map<short>` of 16 buckets (bucket mix64(group) & 15; a group in a new bucket goes first, one in a used bucket first in
that bucket's run): human bodies draw group 1 before group 0. Sliders and colours keep separate group maps. The skin re-roll is
`Int(max(min, mid − w), min(max, mid + w))` with w = round(max − min), so it covers the whole range. A morph face's poses come from `srand(FNV-1a-32
(mesh name))` when its mesh is made, so draws after the appearance (money, bounty, name) follow a reseeded or uncached sequence. **Unknown**: the
Character constructor's own draws (it runs before the race pick), where equipment is rolled, the faction field skipping the skin re-roll and morph,
the morph mesh name. Meitou (`CharacterGenerator`, `MsvcRandom`): the same generator and appearance order, one sequence per character from its
seed (scrambled, as neighbouring `srand` seeds start alike), equipment after the morph index, the poses from FNV-1a of race/gender/index; money,
names and personality keep their own per-purpose seeds.

### Random appearance

**Observed** (`data/editor/editor_data_human.xml`): race/gender limits specify `min`, `max`, optional
`mid`, `random_variation` and `random_group`; male height, leg length and hands share group 0.
The generator rolls `round(mid + variation × (2r − 1))`, sharing `r` within each group/category.
Default variation uses the runtime deviation (raw CONSTANTS ×0.5; **Verified** constants-loader
evidence in [character-stats](game/character-stats.md#constants-used-by-this-subsystem)), not the raw field.
Base-game 0.4 therefore gives a ±20% range around the midpoint: human height 92–108, not 84–116.
Explicit XML `random_variation` remains a percentage of the full slider range. Rolls are not
clamped; **Unknown** whether the original applies a later clamp for extreme modded limits.

**Observed** (Kenshi x64 disassembly: template initialization `@ 140073fb2`, randomizers'
final copies `@ 14007acc2`, `@ 14007bc2d`, `@ 14007bfda`): randomization restores `Posture`
from the appearance template, whose default is 20, rather than retaining the randomized value.
Generated NPCs use 20; authored body files retain their posture.

Parts can have different meshes/skeleton links for male/female/race and slot. Clothing layers,
vests/backpacks, hair-hidden/part-map flags, stumps and missing limbs are not ordinary visibility
checkboxes. Weapon inventory choice, equipped/drawn location and sheath/back/hip attach bone
are separate; .phs attachment points are data. Part scales/transforms must follow skeletal axes.

Worn ARMOUR is drawn from the inventory sections `head`, `shirt`, `armour`, `legs`, `boots` and `belt` (`SavedKit.Worn`); the section follows the
record's `slot` (3 hat, 8 shirt, 5 body, 6 legs, 9 boots, 14 belt). **Observed** (merged data of a modded install): mods put masks, goggles and
robes in slot 14 (845 ARMOUR records, 632 with a mesh), so a worn belt-slot ARMOUR must be drawn like any other (it was dropped until fixed: no goggles
or mask showed). A worn ITEM in `belt` (lanterns) shows only its light (`Loadout.Carried`); whether the original also draws an ITEM's own `mesh` there is **Unknown**.

**Verified** (decompilation, `AppearanceBase::setupItemMaterial` `FUN_140536210`): an attached item without a material record whose type is
LIMB_REPLACEMENT (111) gets the appearance's body material (its textures, skin tone and stat-driven normal blend). The races' stumps
(`Human Left Arm Stump` and the others in `Newwworld.mod`, mesh material `default`, no texture fields) are drawn so; prosthetics have a
material. Meitou: `CharacterContent.ItemMaterial` (they were untextured, white, before). Stumps also get the body blood layer ([blood-decals.md](game/blood-decals.md#on-the-body-and-on-severed-limbs)).

## Shading, poses and LOD

**Observed HLSL** body/head texture layering uses head UV+(0,1), border-black and body normal.wy.
**Verified** (`character.hlsl`, `skin.hlsl`; mesh probe 2026-10-21): the binormal is the mesh's own BINORMAL × the tangent's w (w = 1 for a
three-component tangent). Both human bodies store BINORMAL = cross(tangent, normal); `human_male` has four-component tangents (w ±1),
`human_female` three. Meitou (`Model.Build` `gameBinormals`, characters only): w is set so that cross(normal, tangent) × w is that binormal; worn
items flip their normal map's green (skin.hlsl), so their binormal is the opposite. Albedo is written darkened: × 0.7 for bodies, heads, hair and severed limbs (`character.hlsl`
`DARKEN`), × 0.8 for worn items (`skin.hlsl`); the icons' forward shader (`rtticons.hlsl`) has none. Items attached with their own mesh (`StaticObject`):
their darken and green flip are **Unknown**; Meitou applies neither (both keyed on `PartMaterial.Worn`).
Worn items (template `Skinned`, `AppearanceBase::setupItemMaterial` `FUN_140536210`) write gloss = diffuse alpha × `glossMult`, metalness = colour
map blue; nothing sets `glossMult` there and `Skinned_FP` has no default, so it is 0: worn clothing is matte (**Observed**: no setter in the function,
no default in `skin.program`). Items attached with their own mesh (`StaticObject`) keep `glossMult` 1. Lighting (**Verified**, `deferred.hlsl` / `rtt.hlsl`, `lightingFunctions.hlsl`): specular colour = lerp(0.04, albedo, metalness),
diffuse albedo x (1 - metalness), diffuse light x (1 - the specular colour's mean); the GGX Fresnel and Lazarov environment BRDF take that colour.
Meitou: `atmoMetalness` in `kenshiLight` / `stageLight`, set from a worn item's colour map blue; other surfaces stay dielectric
(objects' `metal_map` is not read).
Skin tone, body normals/muscle/starvation, hair/beard overlays, dye/cloth/cutout and wet/dust inputs
have separate parameters. Material runtime facts: [runtime-materials](formats/runtime-materials.md).
Bone/positional sizes and face morph deltas apply before skinning; meshes with their own LOD or
manual linked LOD need compatible remaps/bone weights. Skin/head/hair colour-space assumptions
remain scoped to tested shader/screenshots, not every asset.

Body/race generation, mesh-per-item and clothing/weapon choice source addresses below retain
traceability. Tests check our factories/readers; screenshots cover named appearances only.

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

FACTION `faces weirdness` (0.2-1, most 0.5; `AppearanceBase::buildBody` `FUN_140539020`, **Verified** read): for a race without a fixed body and a character not in a
player squad, floor(weirdness x 10) goes into the generated morph mesh's name (which seeds the face roll) and `AppearanceManager::randomPoses` draws the random morph offsets with it
(**Observed** meaning: the strength of the faction NPCs' random face and body morphs). Not modelled (the name's form and the pose formula are **Unknown**).

CHARACTER `race` overrides the body file's race (fcs.def "will override race in .body file"; the viewer
follows this, not traced). The candidates (`Faction::chooseARace` `FUN_1407ff1a0`, **Verified** in outline): the squad template's
`race override` (7 templates) when one is given, else the CHARACTER's `race`, else the faction's `races`; a weighted draw by the reference value. Meitou keeps a body file's race
before the faction's.

### Hidden body parts (Verified, `@ 140075600`, `@ 140071f90`, `@ 140531cc0`; HLSL `skin.hlsl`)

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

The body's per-vertex `partData.y` holds the part-map bit and, from `@ 140071f90`, a limb bit 1 << (12 + k) (k: 0 L arm, 1 R arm,
2 L leg, 3 R leg) when the vertex's heaviest bone belongs to that limb and its bind-pose Y is below 13.83 (male) / 14.1 (female) for an
arm, 8.6 for a leg. Bones map to limbs through the race's `combat anatomy` LOCATIONAL_DAMAGE `bone name`/`bone name 2`/`bone name 3`
(an unnamed bone takes its nearest named ancestor's part); an arm (`body part type` 2) or leg (1) is the right one when `collapse part`
& 0x12. `hiddenMask` (`uses_hide_parts`) = OR of worn items' `hide parts`, 1 << (`slot` - 38) per worn LIMB_REPLACEMENT, and bit 12 + k
for every limb not in state 0 (`FUN_1400cd470`); `character.hlsl` clips where the interpolated flag is above 0. Meitou: the limb bits
ride in the body vertices' colour Y, the mask in the instance flags (`CharacterContent.MarkBody`, `CharacterAppearance.HiddenLimbs`);
an unnamed root-chain bone (pelvis) is no limb (the original's fallback part index 1 is **Unknown** in effect). The part-map bit (**Verified** read, `FUN_140075600` / `FUN_140071f90`): the race's `part map male` / `part map female`
image loaded by name through Ogre's resources; a DXT1-5 file is refused (logged "Convert this to png instead"), so no bits. Each body vertex samples the
texel (clamp(int(W x u)), clamp(int(H x v))) at its UV; with alpha > 0 an exact table colour sets its bit, any other colour is logged as invalid and sets
none. Meitou: `CharacterContent.MarkBody` (vertex colour Y: limb bits 0-3, part bits from 4), `CharacterAppearance.HiddenParts` in instance flags bits 18-27.
Meitou's decapitation (not the original's): vertex bit 14 marks every vertex of the Head part's bones, hidden by instance flag 28 (`CharacterAppearance.Headless`); flag 29 draws only them (a cut-off head, `HeadOnly`).
Which region each colour marks is the race's own choice (data, not engine). **Observed** (scratch survey of `human_male`/`human_female` with
`MaleMask_Default.png`/`FemaleMask_Default.png`): white the chest (Y 14-15), red shoulders and upper arms (13-15), green a hip band (11-13); robots' `robot_mask.png`
is all red. 19 base ARMOURs use `hide parts` (coats 0x3, robes 0x5, plate jackets and skin suits 0x7, ninja rags 0x4).
Boots (slot 9, not `is locked`) on a character with exactly one fitted leg prosthetic lose that leg's half (`AppearanceBase_SetupItemMaterial`,
**Observed**): left leg replaced collapses the x >= 0 half, right the x < 0 half. Meitou: `CharacterPart.HideSide`, instance flag bits 16-17.

### Weapon parts and their material

**Observed** (decompile: `FUN_1405d24b0` sheath attach, `FUN_140535e50` attach, `FUN_1405358a0` attachment object, `FUN_140536210` `setupItemMaterial`; `data/materials/deferred/skin.hlsl`): a weapon's `sheath` mesh is attached like `bare sword` and `mesh`: the attachment copies the weapon item's material record (the saved `material sid` model, `FUN_140535e50` takes the item and the mesh name), so the scabbard wears the same MATERIAL_SPECS_WEAPON model (grade) as the blade. Meitou gave the sheath no model and resolved it as an unowned mesh (the first model of the weapon's manufacturer: a Rusted Junk look on every graded sword); fixed, and the crossbow's model reaches its `mesh`, `extra mesh` and `live ammo` parts the same way. A weapon with no saved model keeps the resolver's choice (**Unknown** whether the original then picks one at random, `FUN_140840140`).

**Verified** (`skin.hlsl`): worn items always `clip(normal.a - 0.6)`, so a normal map whose alpha is low (BC1 punch-through, atlas islands) hides those texels.

### Clothing colours

Each item instance carries a COLOR_DATA (save key `color sid`). **Observed** (decompile, KenshiLib 1ce4a16 names: `Item::Item`, `AppearanceBase::createAttachedObject`
`FUN_1405358a0`, `Character::getUniformColorScheme`, `Faction::getFactionColorScheme` from `Character::createComponents`):
- A new item takes its record's first `color` reference.
- Put on (attached) without a colour, it takes the wearer's scheme, CHARACTER `color`, else the faction's `color`, and keeps it. A looted uniform keeps its first wearer's colours.
- Drawn: an ARMOUR or CONTAINER (the types fcs.def gives the field) whose `dont colorise` is true (default; every base record stores it) shows no colour, but its colour is still stored.

**Observed** in the owner's 9 saves (scratch probe of platoon files, worn ARMOUR): of 27,230 coloured pieces, 17,866 have the
platoon faction's scheme, 7,634 the `color` of some CHARACTER (the wearer's record was not matched), 1,632 their record's own, 98 other (looted, mod colours). No uncoloured worn piece is in a faction with
a scheme. 31 of 103 base FACTIONs have one; 42 of 142 ARMOUR records have `dont colorise` false.
Meitou: `ItemColours`, `ItemInstance.ColourId` (factory: the record's; `CharacterAssembly.Dye`: the scheme, for any item outside the main grid), `LoadoutItem.Colour`,
`CharacterPart.Colour`. Whether the scheme follows a faction change (a recruit) is **Unknown**. Meitou uses the current faction. A vest texture (body layer) takes
its item's colour like the meshes (the shader has one shirt colour: the first dyed vest's); the original's vest colour source is **Unknown**.

### Crossbows

**Observed** (decompile: `FUN_1405dbd80`, `FUN_1405dbf80`, `FUN_140535d50`, `FUN_14043d120`, `FUN_140436080`, `FUN_1405cc820`):
carried, the whole `mesh` hangs at its section's point (`back`, `back2` past the first, or `hip`), pushed out along the point's z by
the CROSSBOW's `equip offset` (fcs.def default 1.2). Drawn to shoot (task 264 "Equipping crossbow"), the same item moves to `hands`
(`Bip01 Prop2`) with no offset; the melee weapon in hand goes back to its own section first (one item in the hands), and the empty
`sheath` leaves the back bare. `extra mesh loaded` / `extra mesh unloaded` and one `live ammo` bolt per shot sit in child nodes of the
crossbow (at all points): loaded shows with shots > 0, unloaded with 0, bolt i with i < shots; bolts at (`barrel pos Y`, `barrel pos Z`,
0) turned X -90°, Z -90° (bolt +z along the crossbow's +y). Firing hides the loaded string and bolt[shots - 1]; the end of a reload shows them
(`FUN_14043a730`, `FUN_140436fb0`). Meitou: carried and in-hand crossbow with the loaded or unloaded string and its bolts by the combat slot's loaded
bolts (`CharacterCold.CrossbowShots`; 0 until the first reload, the original's starting count is **Unknown**); the snapshot's
`RangedDrawn` selects them (#36: a staff was shown firing while the crossbow stayed on the back). A generated character's crossbow takes row 0 of `back`
(10 x 2); a two-row crossbow fills it, so a rolled back weapon goes to `hip` when free, else is left out, and the inventory never puts two weapons
on the same cells (taken cells: `hip`, then the pockets). The original's placement for this case is **Unknown** (Meitou engine choice).

### LOD of character meshes

Bodies, hair and armour use their meshes' own generated LOD levels (no separate LOD meshes); each entity picks
its level on its own with the `distance_sphere` rule ([formats/ogre-mesh.md](formats/ogre-mesh.md#stream-and-compatibility)). The rule now lives in `Meitou.Data.Ogre.MeshLod`; `CharacterLod` forwards to it.
**Verified** (scratch survey with `OgreMeshReader` of the meshes the base game's records name; `CharacterLodTests`
checks `human_male.mesh`):

| Records' meshes | LOD |
| --- | --- |
| RACE `male mesh` / `female mesh` | counted per record field: 2 levels at 200 for 18 male / 11 female (`human_male`, `bone_male`, `robot`, the three `Stick_person`), 2 at 400 for 32 (animals), none for the rest (`human_female`, `bone_female`, ...) |
| ATTACHMENT hair (`haircut_*`) | 2 levels at 200 (81 references); animal fur 400 or none |
| ARMOUR `mesh` / `mesh female` | 2 levels at 400 for 193 references (masks, hats, coats... meshes with no skeleton link), none for the 51 to meshes with a skeleton link (`samurai pants` ...) |
| CONTAINER backpacks | 2 levels at 400 |
| LIMB_REPLACEMENT, WEAPON (`mesh`, `bare sword`, `sheath`) | none |

Meitou: the `charlod` switch (Character levels). Faithful draws the files' own levels by the rule above with the stored bounding radius (unclamped) and
bias 1, the shadow passes as the picture; Meitou draws its generated levels ([character-renderer](character-renderer.md)). Both sets are prepared at
load (`CharacterContent.PrepareMesh`), so the switch needs no rebuild. 400-character crowd at The Hub, Release: Faithful 2.07M character triangles
a frame against 0.75M, +0.9 ms GPU in the picture and +0.9 ms in the shadows. Whether Kenshi's shadow cameras take the main camera's LOD is **Unknown**.

## Names

**Observed** (character setup `FUN_14062b210`, pool loader `@ 140641d80`): CHARACTER `named` false → the record's name; true → a uniform pick
from the look's sex's list (appearance `sex female`): `data/namesM.txt` (men) or `data/namesF.txt` (women), with `data/namesMF.txt` appended to
both. Lines are trimmed, blank ones skipped; each file is optional. An empty list gives a fixed fallback string (**Unknown**; Meitou: the record
name). 135 of 639 merged CHARACTERs are `named`, among them most start-squad members (Wanderer, Nobody, start slave; not Citizen, Exile).
Meitou: `NamePool`, rolled at spawn (`PopulationSystem.SpawnName`, `RngPurpose.Name`) from the world seed, which a new game from the menu rolls afresh
(`--seed` keeps one), so the start squad's names differ from game to game as the original's; files read from the install, not mods.

## Character editor

`CharacterEditWindow` (constructor `FUN_1405f4dd0(chars, mode, forceRaces)`, layout `Kenshi_CharacterEditor`). Facts **Observed** from the
exe decompile unless marked; Meitou: `CharacterDraft` (data), `EditCharacter` / `FinishCreation` commands (`Simulation/Editing`), `PlayerInterface.Editor`.

- **Modes**: 0 new game (`FUN_140872e00`: every player character after the start's `squad` spawns, with the start's `force race`), 1 in play
  (dialogue `DA_CHARACTER_EDITOR` on the talking player character; `DA_JOIN_SQUAD_WITH_EDIT` and slave purchases on the recruits). Mode 1
  hides the race, subrace and gender arrows; they are also hidden when there is no choice. Everything else works in both modes.
- **Races**: RACE_GROUP name on RACE, RACE name on SUBRACE; a race is listed if not an animal, in a group, has its editor limits XML, `playable`
  (mode 0) and, with a `force race` list, in it (none left: "Game start has no valid races", the list is unrestricted). Showing a character in
  mode 0 (`FUN_1405efd60`) whose race isn't listed switches it to the first group's first race (a race missing from its group: that group's
  first), so The Hive Exile's raceless `Exile` becomes a hive race. Base data: Human
  {Greenlander, Scorchlander}, Shek, Hive {Prince, Soldier Drone, Worker Drone}, Skeleton (**Verified** by probe). Group order **Unknown**
  (Meitou: data order). Gender "-" when the race's gender list has fewer than two entries (**Verified**, `FUN_1405ee0c0`; Meitou: `single gender`); the
  subrace box always shows the RACE name. Description = RACE `description`. Stats panel (`FUN_1405ee0c0`, **Observed**):
  "Hit Points:" and the character's first medical part's maximum as an integer (Meitou, while the draft shows another race: that race's new medical state's), "No bleeding" below `bleed rate` 0.1, then the stat multipliers
  above 1, then those below 1, each as the stat's name (no colon) and (m-1)x100 %. Within each group the order is the race stat map's iteration order
  ([boost-unordered](formats/boost-unordered.md); `RaceData.StatMapOrder`), which gives vanilla's Greenlander Farming, Cooking, Science (**Observed**).
  **Verified** (decompile): the lines go into a DatapanelGUI (`FUN_1406fe9f0`), whose ScrollView grows its canvas and shows its vertical bar when a line
  passes the bottom (`FUN_1406fd4e0`); labels `#afa68b`, values above 1 `#a8b774` with "+", below 1 `#bf4834` (BadBright). The names are the editor's own
  table (`FUN_140888440`), not the stats window's: 1 Strength, 2 Attack, 3 Labouring, 4 Science, 5 Engineering, 6 Robotics, 7 Weapon Smithing, 8 Armour
  Smithing, 9 First Aid, 10 Thievery, 11 Turrets, 12 Farming, 13 Cooking, 14-15 Vet, 16 Stealth, 17 Athletics, 18 Dexterity, 19 Defence, 20 Weaponry,
  21 Toughness, 22 Assassination, 23 Swimming, 24 Perception, 25 Katanas, 26 Sabres, 27 Hackers, 28 Heavy weapons, 29 Blunt, 30 Martial arts, 32 Dodging,
  35 Crossbows, 36 Precision Shooting, 37 Lockpicking, 38 Crossbow Smithing, others empty. Row pitch 25.55 px at 1440 lines (**Observed**, a Hive Prince's
  twelve lines; Meitou: 19.2 at 1080, text top-aligned). The rows are 355 px wide at 1440 lines with or without the bar; with it (20 px) they start at the
  panel's left, without it 13 px right and 13 px down (**Observed**, a Hive Prince's and a skeleton's panels; Meitou: a canvas the bar's width narrower than the
  panel, inset 13 px when it fits, text 6 px above the panel's top).
- **Captions** (`FUN_1405f1a40`, **Observed**): the code sets every button and header from the catalog, upper case: Randomise, Reset (not the
  layout's RESET ALL), Randomise All, Body, Face, Hair, Import, Export, Clothes, Confirm, Race, Subrace, Gender, Change Character, Race
  Description, Race Stats. With one character CHANGE CHARACTER, its arrows and "1/1" stay on screen (vanilla screenshot), arrows inactive.
- **Rows** (`FUN_1405f1a40`, `FUN_1405ebd50`, **Observed**): `TabsPanelContent` holds a `Kenshi_ScrollViewEmptyLight` ScrollView (vertical bar
  only); each control is a template widget stacked at its template's height (`kenshi_templates.xml`): a list is `Kenshi_ListSlider` (name box,
  arrows, value name, "n/count"; captions "Face", "Hair", "Beard"), a slider `Kenshi_Slider` (name box, `Kenshi_ScrollBar` with arrows, value
  box), the hair colour one `Kenshi_MultiSlider` "Hair colour" with "Colour", "Saturation", "Brightness". Captions are catalog messages, not
  upper-cased. The rows scale with the screen (55 layout px = 73 px at 1440p in the vanilla screenshot).
- **Tabs** (`FUN_1405ebd50`, Body first): FACE = head list (if more than one) + XML Face ranges + colours; BODY = "Idle stance" slider, then
  `Skin Tone` (vec3 interpolated; only with more than one colour), then XML Body ranges, then other colours (order also seen in the original's
  screen); HAIR = Hair (attach slot 2) and Beard (13) lists with
  "None" (if more than one entry), hair colour multi-slider `Hair Colour` / `Hair Saturation` / `Hair Brightness` 0..100. No Personality tab, no
  Age. Plain ranges store the integer as the float key; target ranges store value x 0.01 in `target`, a negative value x -0.01 in
  `target_opposite` (the editor shows the reverse). Meitou: women get no beard list (generator rule).
- **Idle stance**: slider 1..n over a list built once (`FUN_14007d480`, from the idle list of `FUN_1405bbd40`): ANIMATION records in data
  order that are `idle`, not `disabled`, `weather type` < 1, `layer` not `overlay`, `has weapon L` and `R` both 0, not `crouched`. Writes
  the record name to `idle stance` (`FUN_1405eaa30`); a stance not in the list shows as 1. **Verified** by probe on base data: 8 stances
  (`idle_stand_relax`, `stand 1`, `idle_stand_guard`, `idle_stand_timid`, `idle_stand_confident`, `idle_stand_normal`, `idle_stand_stiff`,
  `squat`), so the default `idle_stand_normal` is 6, as the original's screen shows. RANDOMISE never rolls it (**Verified**: no randomiser writes it).
  The stance clips are 1-frame poses (0.033 s, two keys) with `play speed` 0 (`squat` 0.01), **Verified** by probe on base data; looped at
  real time they flicker between the keys. The preview plays the chosen stance at its `play speed`, cross-faded at `animation blend rate`
  (Meitou: `PlayerInterface.EditorIdle`, `GameHost.EditorStancePoses`; the original's fade is **Observed** smooth, its rate **Unknown**). In the
  world a character stands in its stance whenever it fits ([animation.md](animation.md#idle-stance)).
- **Buttons** (randomisers **Verified** by decompilation, `FUN_1405efa50`, `FUN_1405ef930`): RANDOMISE rolls the current tab: FACE = head and face
  sliders, then face poses; BODY = body sliders and colours, no skin re-roll; HAIR = hair, no faction limits. RANDOMISE ALL = hair, face, body
  (`FUN_14007f040`), then face poses; the rest stays. Draws as in [Original random sequence](#assembly-and-generation). RESET ALL = the appearance template's defaults (hair, beard,
  colours, `Posture`, idle stance `idle_stand_normal`; slider values **Unknown**, Meitou: XML mid, poses 0, Posture 20); CLOTHES toggles worn
  clothing in the preview; IMPORT/EXPORT: see [below](#import-and-export). CONFIRM asks first when some
  characters were never shown, closes, unpauses at speed 1; it charges nothing. No cancel widget; Esc opens the game menu over the editor
  (**Observed**, vanilla play; Meitou does so in both modes).
- **Race and gender switches** (`FUN_1405ef390`, `FUN_1405eadc0`, **Observed**): each character keeps a look per (race, gender); a switch stores
  the current look under the old pair and loads the new pair's, keeping the idle stance. **Verified** (decompilation, `FUN_1405f03c0`): when the
  editor opens, every pair's look is the template reset (`FUN_14007a490` with 1: first head, no hair or beard, the race's first `hair colors`,
  face sliders at mid) and then a hair roll, nothing else. Meitou (`CharacterDraft.SwitchedLook`): the same when a pair is first shown (sliders at mid).
- **Preview**: a separate scene. **Verified** (install's compositor files): workspace `Kenshi_Character_Editor` = `GBuffer_Minimal` (characters only,
  render queues 50-70) then a `Character_Editor` node, then the GUI. The background is a full-screen `render_quad` of
  `data/gui/gfx/SandyGround.dds` (1920x1080 DXT1, no depth). Lighting (`charedit.compositor`, `rtt.hlsl`, `lightingFunctions.hlsl`): world
  direction (-3, 2, -3) towards the light, colour 0.6: `CalcPunctualLight` (diffuse π·N.L·(1-0.04), GGX for roughness 1-0.99·gloss, specular
  colour 0.04) plus `CalcEnvironmentLight` (`mp_irradiance` mip 3 ·a·4, `mp_specularity` mip (1-gloss)·7 ·a·10, Lazarov BRDF), times exposure
  0.5, written to the 8-bit target untonemapped; no fog, sky or shadows. **Verified** (exe disassembly, `FUN_1405f03c0`, `FUN_1405e9ca0`,
  `FUN_1405e9490` and their static data): the rig's pivot sits at y -3000; `charedit_root_node` 12 below it holds the platform, BUILDING_PART
  `5370-platform_otto.mod` (base: `character_editor_platform_part`, mesh `data/buildings/furniture/new_furniture01/character_editor_platform.mesh`,
  first `material`), at its origin, and the characters on it turned half round (quaternion w 0, y 1). Camera: the pivot takes yaw (about Y)
  and pitch (about X); the camera is its child at (-3, 0, -30) + normalize(-3, 0, -30) x zoom. Mode flag 1 = body (pivot 12 above the feet,
  pitch 0.15π, yaw 0, zoom 0, pitch clamp 0.025π..0.2π, zoom clamp -9..7), 0 = head (pivot at the head bone, pitch 0, yaw 0, zoom -21, pitch
  clamp -0.1π..0.25π, zoom clamp -23..-17); every reset (mode or character change) restores these. Any of three mouse buttons drags (yaw/pitch +=
  delta x sens x dt x -0.1); the wheel zooms (zoom -= wheel x sens x dt x 0.6, clamped). **Observed**: FACE and HAIR give head mode, BODY body.
  Meitou (`EditorOrbit`, `CharacterRenderer.Editor`, `WorldObjectRenderer.Stage`, `PlayerInterface.EditorBackdrop`): while the editor is open the
  world is not drawn (objects keep streaming); the edited character and the platform (a loose instance, its stage frame the character's turned
  half round) are drawn into a UNORM stage texture with that lighting (`AtmosphereShaders.stageLight`, fog distance -2), which the interface puts
  over `SandyGround.dds`, centred between the panels by a projection shift instead of the -3 offset. Orbit starts, limits and zooms are the
  exe's; drag and wheel speeds are engine choices. Its looks are close-ups: meshes without generated LOD levels, which were 99 % of a mesh's
  preparation (a hair 1-2 s; measured 2026-10-21), and the looks the next click can give (next RANDOMISE ALL roll, other gender, neighbouring
  races, every head/hair/beard of the race) are prepared ahead; a click shows in 1-2 frames. **Blend**: a slider moves the character along: when a new preview look is ready it is drawn
  as the mix of the old and new values over 0.12 s, the interval of the throttled rebuild (`CharacterRenderer.Blend`): body shape, posture
  layers, face/body pose weights (morph set recomputed per frame in a scratch ring of `MorphArena`) and skin/hair colours; a look with another
  head, hair or clothes swaps at once. The original's own slider behaviour is **Unknown**.
- **Meitou bar** (Meitou UI, both hosts; `EditorBar`, `GameHost.EditorBar`): instead of the stage, the world's renderers draw a copy of The Hub's intact
  Storm House with interior layout `Bar` (parts, furniture, lamps; a runtime placement keyed `meitou:editor-bar` whose state and layouts are read from
  the source's zone, `BuildingPlacement.StateZone`) 3000 above the highest ground round the edited character, in the interior view with every floor, at
  23:00 under a clear sky without weather particles, the water and its reflection off and the frame cut at 2500 (`WorldFrame.Gpu.ViewCap`: far plane and
  shadow cascades), short of the world 3000 below; headless 1280x960 Release: 4.0-4.2 ms a frame capped, 5-29 ms uncapped (**Observed**, noisy machine). The shown character stands at a fixed spot of the building's frame facing the counter; up to 5 other
  characters of the editor's list sit on the seats nearest the far tables behind it, each over 41 across from it (beyond the orbit's reach; the source's chair spots and sitting clips). Night shows
  in the dark windows. The bar's own people stand and sit in it: its residents' leader (Barman) at a shopkeeper node and first squad member (Ninja Guard) at a guard
  node, 3 patrons of the town's `bar squads` on the next seats and 3 at general nodes, all beyond the same 41, rolled with fixed seeds. The bar stands on
  `FLatTop_Pillar01.mesh` scaled to 1500 each way with its top at the bar's ground, with 14 shrubs round it. A white point light under the roof (half the
  lamps' mean power, radius 220) and a grey character fill (0.04) light the room neutrally. The camera is the stage's
  orbit, moved across instead of shifting the projection. The stage covers the bar until its parts, the streaming and the characters are in, or at most 3 s
  after its room is laid out (the counts are global and can stay up under memory pressure). The faithful UI keeps the stage. Metal: compiled, not yet seen on a Mac.
- **Keys**: `in editor` true while open; a mode 0 character without a body file is randomised on open (Meitou's start characters are already
  rolled). Closing in mode 0 also sets health from the CHARACTER's `starting health` (Meitou: a new race at creation gets a new body, which
  applies it; the edited look keeps stumps and prosthetics).
- **Meitou validation** (`CharacterEditing.Apply`, `CharacterDraft.Sanitize`): player character with a person's look; creation only while
  `PlayerState.InCreation` (new game until CONFIRM) and within the start's force races; surgery needs the surgeon alive within 60 units
  (engine choice), the same race and gender, money > price, and takes the price. Head/hair/beard must be the race's, sliders and poses are
  clamped to the limits, the name to 30 characters. **Verified** by a scratch round trip on save 2: edit, capture, load gives the edited
  values with the original key set.

### Import and export

Folder `./data/character/bodies/export/` under the install, extension `.bod2` (`FUN_141539ad0`/`b10`). **Observed**: EXPORT (`FUN_1405ea410`)
needs no dialog: it writes the edited appearance record to the folder as `<NameText>.bod2` and shows "Character '{1}' exported to file {2}".
IMPORT opens `Kenshi_OpenSaveFileDialog` titled "Import character" on the folder (`FUN_1405f4dd0`); the chosen file must hold a type 66 record,
else "Error loading appearance file '{1}'. The file is in an old format or invalid." (`FUN_1405f4280`), and its look replaces the editor's;
what the original does with another race in mode 1 is **Unknown**. **Verified** (FcsReader + FcsWriter on the install's 12 exports): all are
type 15, one record, id 1, `1--INGAME`, name `default` or `0`, and round-trip byte-exact. Meitou (`CharacterDraft.Import`/`Export`,
`PlayerInterface.Editor`): export writes that layout, overwriting a file of the same name; import sanitizes the file for its race and refuses a
race the editor doesn't offer (creation) or another race or sex (surgeon).
Dialog (`FUN_1401a0b10`, refresh `FUN_14019fd70`, **Observed**): `CurrentFolder` shows the folder as kept (no trailing separator; "./data"
when empty); `ListFiles` gets "[..]" and the subfolders as "[name]", then (open mode) the matching files by file name with extension, item data
folder + "/" + name; ButtonUp cuts the folder at its last `\` or `/`; ButtonCancel is `Tr("Cancel")`, ButtonOpenSave `Tr("Import")` here, both
upper-cased. Order: case-sensitive (vanilla screenshot: "Yumi" before "yoshim"). Meitou: a double click (or IMPORT) on a folder opens it.

### Plastic surgeons

**Verified** by probe of the merged data: 9 dialogue lines carry `DA_CHARACTER_EDITOR` (11); the price is a `DA_TAKE_MONEY` (8) effect on the
same line (val0), gated by `DC_PLAYERMONEY` (2) strictly greater. "plastic surgeon" (CHARACTER 2132 Plastic Surgeon, leader of the bar squads
"bar plastic surgeon" / "plastic surgeon": Holy Nation, Empire Peasants, United Cities, Deadcat, Waystation, Bark, Sho-Battai...) 200;
"doctor shop" (hospital squads' `dialog leader` package) 500; Doctor Chung (Mud Town) 200, no money check; Thieves Guild surgeon free
behind `DC_FACTION_RANK`; Grey free for escaped slaves. Meitou: the surgeon is talked to; its line's DA 8 pays and DA 11 opens the editor in
mode 1, already paid ([dialogue](game/dialogue.md#meitou-design)). An unpaid `EditCharacter` (no UI sends one) is checked against `SurgeonDialogues`:
a line gated only by money or `DC_IS_RECRUITABLE`.

## Open questions

- ~~Body-shape slider formulas~~, ~~`hide parts` bits~~, ~~`hide stump`~~, ~~`muscleBlend`~~: answered (animation.md
  "Body shape sliders", "Hidden body parts" above). Left: the
  stats/nutrition fields listed in animation.md's open questions.
- Random appearance details: the untraced part of the morph-face name, the faction flag that re-rolls the skin tone
  (and a body file's gender); extra hip copies; `RaceLimiter` rules.
- Where the original puts a rolled weapon whose footprint doesn't fit its pool's slot (hip 7x1, back 10x2). Meitou (engine choice,
  `CharacterGenerator`): the slot it fits, hip pool preferring the hip and back pool the back; fitting neither, it is left out.

## Expression reference

- Random appearance: `gender="Female"`
- Colour space: `hdr_to_gamma = false`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Meshes per item (Verified, `@ 140537690`, `@ 1405358a0`)
- Order (Verified, `@ 140581770`)
- Body shading (Observed, HLSL `character.hlsl` main_fs; parameters Verified where noted)
- Hidden body parts (Verified, `@ 140075600`, `@ 140071f90`, `@ 140531cc0`; HLSL `skin.hlsl`)
- Hair meshes (Observed, HLSL; parameters Verified `@ 140532600`)
- Worn clothing (Observed, HLSL `skin.hlsl`; parameters in runtime-materials.md)
- Colour space (Observed)
