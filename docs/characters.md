# Characters and appearances

Original race/loadout/appearance/body/attachment/material research from FCS, .bod2/.phs assets,
binary functions and shaders. Assembly: CharacterGenerator/CharacterAppearance/CharacterShape;
rendering: [character-renderer](character-renderer.md), animation: [animation](animation.md).

## Assembly and generation

### Random appearance

Parts can have different meshes/skeleton links for male/female/race and slot. Clothing layers,
vests/backpacks, hair-hidden/part-map flags, stumps and missing limbs are not ordinary visibility
checkboxes. Weapon inventory choice, equipped/drawn location and sheath/back/hip attach bone
are separate; .phs attachment points are data. Part scales/transforms must follow skeletal axes.

**Verified** (decompilation, `AppearanceBase::setupItemMaterial` `FUN_140536210`): an attached item without a material record whose type is
LIMB_REPLACEMENT (111) gets the appearance's body material (its textures, skin tone and stat-driven normal blend). The races' stumps
(`Human Left Arm Stump` and the others in `Newwworld.mod`, mesh material `default`, no texture fields) are drawn so; prosthetics have a
material. Meitou: `CharacterContent.ItemMaterial` (they were untextured, white, before). Stumps also get the body blood layer ([blood-decals.md](game/blood-decals.md#on-the-body-and-on-severed-limbs)).

## Shading, poses and LOD

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

CHARACTER `race` overrides the body file's race (fcs.def "will override race in .body file"; the viewer
follows this, not traced). The candidates (`Faction::chooseARace` `FUN_1407ff1a0`, **Verified** in outline): the squad template's
`race override` (7 templates) when one is given, else the CHARACTER's `race`, else the faction's `races`; a weighted draw by the reference value, where an entry of value 0 or less is never drawn (**Verified** for the faction list, `FUN_1407fe270`). The body file is not consulted for the race (**Verified**: `FUN_140581770` calls `chooseARace` with the CHARACTER and fills the chosen race's appearance from the body afterwards, `FUN_1406c3a70` has no race read); Meitou uses the body file's race only after
when all of those give none (engine fallback; the 19 base-game CHARACTERs with a body and no race list either have an unreadable old-format body or are mod characters). With no candidate the original logs "Missing race" and uses RACE `17-gamedata.quack` (Greenlander) **by id**
(`FUN_140074f00`, `FUN_140538360`, **Verified** read), so a translation mod that renames it changes nothing.

The body file's `head` is kept only when the race lists it. **Verified** (read, `AppearanceHuman::updateCharaterTexture` `FUN_140533b50`, a vtable slot): a `head` that names no HEAD record, or one missing from the
race's `heads male` / `heads female` for the gender (`FUN_14007c9e0`), is replaced by a weighted pick from that list (`FUN_14007c880`, written back to the appearance); an empty list picks nothing and the
head stays. So `Eyegore` (race `Hive Soldier Drone South Hive`, whose `Eyegore.bod2` names the Western `Hive Soldier Drone` and its head `StickPeopleHead02`) draws the South Hive head texture.
Meitou: `CharacterAppearance.HeadOf` (the list's first head; **Observed** on the base game's Eyegore, #692).

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

### Weapon parts and their material

#### A weapon's grade look

**Verified** (base-game data, 23 MATERIAL_SPECS_WEAPON records read with `GameDatabase`): the look of a weapon grade is its **model**, the item's `material sid`, a MATERIAL_SPECS_WEAPON, and nothing else. The record holds `texture map`, `normal map` (may be empty), `specular mult` (1, except `001` = 0), `attack mod` / `defence mod`, an empty `metalness map` and the scale fields `overall scale` / `scale length` / `scale thickness` / `scale width` (all 1 in the base game; only the item icon reads them). No rust, wear or dirt amount exists as a number: a rusted blade is a rust texture. The manufacturer (`company sid`, WEAPON_MANUFACTURER) only decides which models exist (`weapon models`: model, level in value 0, pick weight in value 1); the level and the manufacturer change stats, name and weight, not the picture. Base textures, in `data/items/weapons/tex/`: Rusted Junk, Rusting blade-, Mk 0, Mk IV, Mk V, Mk VI and Industrial 007 share `01_HI` (no normal map); Rusting Blade `02`; Mid-Grade Salvage `03`; Old Refitted Blade `04`; Refitted Blade `05`; Catun No.1-3 `06`-`08`; Mk I `09`; Mk II `10`; Mk III `11`; Edge Type 1-3 `12`-`14` (each with a `_n_HI` normal map from `02` up); Merchants Blade `tech5r`; `001` `plain2`. The model's texture covers the whole mesh of the weapon (blade, handle, sheath).

**Verified** (`skin.hlsl`): worn items always `clip(normal.a - 0.6)`, so a normal map whose alpha is low (BC1 punch-through, atlas islands) hides those texels.

### Surveys

`dotnet run --project tools/Meitou.Tools -- characters` builds the look of every ANIMAL_CHARACTER and of one CHARACTER per race list and reports a missing body mesh, skeleton,
texture or part mesh; `items` resolves each worn ARMOUR/CONTAINER's material (7,370 of 13,107 mesh fields name no material list and take the mesh's own material: not a failure);
`specs` counts the paint factors of coloured materials; `record <name|id>[|...]` prints a record. **Observed** (owner's mods): the only people/animals failing were the 4 all-0-chance
animals (animals.md). Samurai Clothpants' `color samurai armour nopaint faction painted` has `dont colorise` false, so a worn pair takes the wearer's faction scheme (a
white second colour for Bounty Hunters), which paints the colour map's second region white over the light texture. **Verified** (decompile `FUN_140536210`): the original reads `paint factor 1/2` from the item's record (`+0x78`, 0 for every item) and the colour map from the material (`+0xb0`); the colours come from the colour record at `+0xb8`, so the inputs are ours. **Observed** (controlled scene, siwa-test6 at 13:00, four people side by side in Bloodraiders): the pants draw textured and tinted red, so the white pair of Trak is his stored white scheme, not a render fault.

### Characters that cannot be drawn

**Verified** (decompile of kenshi_x64.exe):
- A character whose race id names no RACE record (`FUN_140074f00`) logs "Warning: Missing race: <id>. Using fallback.", takes
  `17-gamedata.quack` (Greenlander) and rewrites its `race` reference to it; `FUN_140538360` (the appearance's race) falls back the same way.

### Clothing colours

- A new item takes its record's first `color` reference.
- Put on (attached) without a colour, it takes the wearer's scheme, CHARACTER `color`, else the faction's `color`, and keeps it. A looted uniform keeps its first wearer's colours.
- Drawn: an ARMOUR or CONTAINER (the types fcs.def gives the field) whose `dont colorise` is true (default; every base record stores it) shows no colour, but its colour is still stored.

Limbs (**Verified**, decompile: `FUN_1400cfdd0` → `FUN_140535d50` → `FUN_1405358a0`; `FUN_140536210`): a fitted limb's item is attached like clothing, so a
prosthetic takes and keeps its own colour, else the wearer's scheme. The `dont colorise` reset runs only for types 3, 46 and 6 (ARMOUR, CONTAINER, ATTACHMENT), so a
LIMB_REPLACEMENT always shows its colour; `setupItemMaterial` dyes any skinned attached item but an ATTACHMENT that has a colour and whose material has a `color map`.
**Observed** (scratch probe, base data): 20 of 28 LIMB_REPLACEMENTs have a material, none with a `color map`, so vanilla prosthetics look undyed; a mod material with
one shows the scheme (issue #279). In the owner's distinct saves 5 of 35 saved prosthetic items carry a `color sid` (4 the Player Faction Color mod's scheme, 1 `orange robes`).
Meitou: `LoadoutLimb.Colour` (the fitted item's `ColourId`, `SavedKit.SetLimbs`), `CharacterAppearance.AddLimb`.

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

## Character editor

- **Modes**: 0 new game (`FUN_140872e00`: every player character after the start's `squad` spawns, with the start's `force race`), 1 in play
  (dialogue `DA_CHARACTER_EDITOR` on the talking player character; `DA_JOIN_SQUAD_WITH_EDIT` and slave purchases on the recruits). Mode 1
  hides the race, subrace and gender arrows; they are also hidden when there is no choice. Everything else works in both modes.
- **Tabs** (`FUN_1405ebd50`, Body first): FACE = head list (if more than one) + XML Face ranges + colours; BODY = "Idle stance" slider, then
  `Skin Tone` (vec3 interpolated; only with more than one colour), then XML Body ranges, then other colours (order also seen in the original's
  screen); HAIR = Hair (attach slot 2) and Beard (13) lists with
  "None" (if more than one entry), hair colour multi-slider `Hair Colour` / `Hair Saturation` / `Hair Brightness` 0..100. No Personality tab, no
  Age. Plain ranges store the integer as the float key; target ranges store value x 0.01 in `target`, a negative value x -0.01 in
  `target_opposite` (the editor shows the reverse). Meitou: women get no beard list (generator rule).
- **Race and gender switches** (`FUN_1405ef390`, `FUN_1405eadc0`, **Observed**): each character keeps a look per (race, gender); a switch stores
  the current look under the old pair and loads the new pair's, keeping the idle stance. **Verified** (decompilation, `FUN_1405f03c0`): when the
  editor opens, every pair's look is the template reset (`FUN_14007a490` with 1: first head, no hair or beard, the race's first `hair colors`,
  face sliders at mid) and then a hair roll, nothing else. Meitou (`CharacterDraft.SwitchedLook`): the same when a pair is first shown (sliders at mid).
- **Meitou bar** (the `editorbar` switch, Meitou, off by default, else the platform; both hosts; `EditorBar`, `EditorBarScene`, `CharacterEditorHost`): instead of the stage, the world's whole pipeline, with the player's graphics settings, draws the base data's
  Storm House (`3384-TwoStorey.mod`) with interior layout `Bar`, from records alone (parts, furniture, lamps; a runtime placement keyed `meitou:editor-bar`, `BuildingPlacement.Standalone`: no town's material, the part variants of a fixed place; barman from residents `44856-rebirth.mod`, patrons from The Hub's `bar squads`), the frame holding nothing of the world (`WorldObjectRenderer.Isolated`, `GiScene.SkipWorld`), 3000 above the highest ground round the edited character, in the interior view with every floor, at
  23:00 under a clear sky without weather particles, the water and its reflection off and the frame cut at 2500 (`WorldGpu.ViewCap`: far plane and
  shadow cascades), short of the world 3000 below; headless 1280x960 Release: 4.0-4.2 ms a frame capped, 5-29 ms uncapped (**Observed**, noisy machine). The shown character stands at a fixed spot of the building's frame facing the counter; up to 5 other
  characters of the editor's list sit on the seats nearest the far tables behind it, each over 41 across from it (beyond the orbit's reach; the source's chair spots and sitting clips). Night shows
  in the dark windows. The bar's own people stand and sit in it: its residents' leader (Barman) at a shopkeeper node and first squad member (Ninja Guard) at a guard
  node, 3 patrons of the town's `bar squads` on the next seats and 3 at general nodes, all beyond the same 41, rolled with fixed seeds. Their clips run at the
  record's `play speed`, a seat's at the sitter's 0.55 of it ([animation](animation.md#at-a-machine-or-on-a-seat)); one without `loop` holds its last frame, as the simulation holds a one-shot clip, so a mod's sit-down seat clip is not replayed (#242);
  the edited character's stance preview follows the same rule.
  Base data has none: its seat clips loop (`sitting chair` a 0.033 s pose at speed 0.01, `sitting idle` 10.9 s at 1; **Verified** by probe on base data).
  The original seats a sitter with the functionality's first `animation` reference (**Verified**, decompilation, `FUN_1402a0900`). The bar stands on
  `FLatTop_Pillar01.mesh` scaled to 1500 each way with its top at the bar's ground, with 14 shrubs round it. A point light under the roof in the lamps' own colour (a quarter of
  their mean power, radius 220) fills the room; the characters take no fill of their own (**Observed**, a bare Greenlander: white, at half the lamps' power and
  with a grey fill of 0.04 on the characters alone, the skin shone and seemed to glow in the warm, dim room). The camera is the stage's
  orbit, moved across instead of shifting the projection. The stage covers the bar until its parts, the streaming and the characters are in, or at most 3 s
  after its room is laid out (the counts are global and can stay up under memory pressure). With the switch off, the stage. Metal: compiled, not yet seen on a Mac.
- **Keys**: `in editor` true while open; a mode 0 character without a body file is randomised on open (Meitou's start characters are already
  rolled). Closing in mode 0 also sets health from the CHARACTER's `starting health` (Meitou: a new race at creation gets a new body, which
  applies it; the edited look keeps stumps and prosthetics), then validates each human's inventory sections for its race (**Verified**, `FUN_1405f5d10`:
  worn hats, shirts and shoes of a race that closes them are removed, [disabled slots](game/ui-screens.md); Meitou: `CharacterEditing.EndCreation`).
- **Meitou validation** (`CharacterEditing.Apply`, `CharacterDraft.Sanitize`): player character with a person's look; creation only while
  `PlayerState.InCreation` (new game until CONFIRM) and within the start's force races; surgery needs the surgeon alive within 60 units
  (engine choice), the same race and gender, money > price, and takes the price. Head/hair/beard must be the race's, sliders and poses are
  clamped to the limits, the name to 30 characters. **Verified** by a scratch round trip on save 2: edit, capture, load gives the edited
  values with the original key set.

### Plastic surgeons

**Verified** by probe of the merged data: 9 dialogue lines carry `DA_CHARACTER_EDITOR` (11); the price is a `DA_TAKE_MONEY` (8) effect on the
same line (val0), gated by `DC_PLAYERMONEY` (2) strictly greater. "plastic surgeon" (CHARACTER 2132 Plastic Surgeon, leader of the bar squads
"bar plastic surgeon" / "plastic surgeon": Holy Nation, Empire Peasants, United Cities, Deadcat, Waystation, Bark, Sho-Battai...) 200;
"doctor shop" (hospital squads' `dialog leader` package) 500; Doctor Chung (Mud Town) 200, no money check; Thieves Guild surgeon free
behind `DC_FACTION_RANK`; Grey free for escaped slaves. Meitou: the surgeon is talked to; its line's DA 8 pays and DA 11 opens the editor in
mode 1, already paid (dialogue). Opening it in mode 1 grows back a shaven head
([ai-tasks](game/ai-tasks.md), "Processing a slave shaves its head"). An unpaid `EditCharacter` (no UI sends one) is checked against `SurgeonDialogues`:
a line gated only by money or `DC_IS_RECRUITABLE`.
**Observed** (the user, base game): the surgeon's editor (its `NameText`) is the only way to rename a squad member; the portrait's `NameText`
EditBox takes no typing. Meitou matches.

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
