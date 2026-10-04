# Character animation

How Kenshi gets animations onto a character: where the animations come from, how it edits them at startup,
how it picks and blends them at run time, and the appearance sliders that act on the skeleton. File formats
are in [ogre-skeleton.md](formats/ogre-skeleton.md) and [ogre-mesh.md](formats/ogre-mesh.md); the bone
maths and Ogre-side blending of Kenshi's modified OgreMain are in
[ogre-skeleton.md](formats/ogre-skeleton.md#how-kenshi-uses-it).

Sources: decompilation of `kenshi_x64.exe` and Kenshi's `OgreMain_x64.dll` (Ghidra 12.1.4). Addresses are in
`kenshi_x64.exe` unless marked otherwise. Field names are FCS field names from `fcs.def`
([fcs-mod.md](formats/fcs-mod.md)). Nothing here has been checked in game.

All playback goes through Ogre `AnimationState`s on the body entity. The executable also links Havok
Behavior/Animation code (`hkb*`, `hka*` class names in its strings), but no use of it for characters was
found (Observed).

## Where animations come from

Verified, `@ 140539020`.

- The body entity is the race's `male mesh` / `female mesh` (or CHARACTER `mesh`), and its skeleton is that
  mesh's own link: in the base game `male_skeleton.skeleton` (174 animations) or `female_skeleton.skeleton`
  (166). A body mesh without a skeleton logs "[AppearanceBase::buildBody] ... has no skeleton." and the
  body isn't built.
- Then Kenshi collects the ANIMATION_FILE records listed in the race's `animation files` and in the
  character's own `animation files` (CHARACTER / ANIMAL_CHARACTER), sorts them (comparator at `14052ba90`,
  order not traced), and for each adds its `male animation` or `female animation` file, by the character's
  gender, as a linked animation source of the body's skeleton. A missing file logs "Error: Skeleton file
  ... not found" and is skipped; it doesn't fail the body. Finally the entity's animation states are
  refreshed, so every animation of the skeleton and its linked files becomes a state.
- Records name animations by their Ogre animation name: `anim name` in ANIMATION, ANIMAL_ANIMATION and
  COMBAT_TECHNIQUE. A name the entity has no state for is ignored (`@ 14051caa0`).
- Which animation wins when the skeleton and a linked file both have the name follows Ogre's lookup order
  (the skeleton's own first, then linked files in the order added, per Ogre source). Not checked in
  Kenshi's build.

## Startup preprocessing

Verified, `@ 140871f00`, `@ 14086dfe0`.

Run once, before characters exist. It edits the **loaded skeleton resources**, so every character using a
file sees the result.

1. Gather all ANIMATION records, and the COMBAT_TECHNIQUE records whose `animal` value is 8 or less.
   When several records share an `anim name`, every later one is renamed `<name>-<n>` (`n` a running
   counter over all renames) and keeps the old name in `anim name original`.
2. For each ANIMATION_FILE with `preprocess` set (default true; "needed for humans"), take its
   `male animation` and `female animation` files (once each if they're the same file). For each file:
   - Every renamed pair becomes a copy: the animation `<name>` is cloned as `<name>-<n>`, so each
     duplicate record gets its own animation to edit.
   - COMBAT_TECHNIQUE records with `is block` true and `is dodge` false: the lower-body tracks (see
     `delete below waist` below) are removed from the animation, and a clone is added under a derived
     name (suffix not traced, Unknown) with override bones `Bip01 R UpperArm`, `Bip01 R Forearm`,
     `Bip01 R Hand`, `Bip01 R Clavicle`, `Bip01 Prop2`.
   - Every ANIMATION record whose animation exists gets override bones (`@ 1408690b0`) and loses
     tracks (`@ 140869860`), then the animation is optimised:

     | Field | Override bones |
     | --- | --- |
     | `override L arm` | `Bip01 L UpperArm`, `Bip01 L Forearm`, `Bip01 L Hand`, `Bip01 L Clavicle`, `Bip01 Prop1` |
     | `override R arm` | `Bip01 R UpperArm`, `Bip01 R Forearm`, `Bip01 R Hand`, `Bip01 R Clavicle`, `Bip01 Prop2` |
     | `override head` | `Bip01 Neck`, `Bip01 Head` |

     | Field | Tracks deleted |
     | --- | --- |
     | `delete root` | `Bip01` |
     | `delete L arm` | `Bip01 L Clavicle`, `Bip01 L UpperArm`, `Bip01 L Forearm`, `Bip01 L Hand` |
     | `delete R arm` | `Bip01 R Clavicle`, `Bip01 R UpperArm`, `Bip01 R Forearm`, `Bip01 R Hand` |
     | `delete below waist` | `Bip01`, `Bip01 Pelvis`, `Bip01 R/L Thigh`, `Bip01 R/L Calf`, `Bip01 R/L Foot`, `Bip01 R/L Toe0` |
     | `delete spine0` | `Bip01 Spine` |
     | `delete body head` | `Bip01 Spine`, `Bip01 Spine1`, `Bip01 Spine2`, `Bip01 Neck`, `Bip01 Head` |
     | `delete weapons` | `Bip01 Prop2`, `Bip01 Prop1` |
     | `delete tail` (default **true**) | `Bip01 Tail`, `Bip01 Tail1` ... `Bip01 Tail4` |

     Bones the skeleton doesn't have are skipped. ANIMAL_ANIMATION records are not part of this pass.
   - The animations `postures`, `shoulder set` and `neck set` lose their `Bip01 L Hand`, `Bip01 R Hand`,
     `Bip01 Prop1` and `Bip01 Prop2` tracks.

What override bones do during blending: [ogre-skeleton.md](formats/ogre-skeleton.md#blending-in-kenshis-ogre).
A file that fails to load logs "[preprocessSkeleton] Failed to load skeleton file: ...".

## Animation definitions

Verified, `@ 1405ba280`.

Each record becomes a runtime definition. For ANIMATION / ANIMAL_ANIMATION it reads: `category`, `layer`,
`idle time min`, `idle time max`, `idle chance` (÷100), `weather type`, `stumbles`, `stumble from`,
`big stumble`, `chance` (÷100), `anim name`, `loop`, `min speed`, `max speed`, `move speed`, `play speed`,
`relocates`, `is action`, `disables movement`, `has weapon L`, `has weapon R`, `is combat mode`,
`stealth mode`, `synchs`, `synch offset`, `normalise`, `possible ending`, `crouched`, `prone`,
`being carried`, `carrying left`, `carrying right`, the weapon-type booleans (`katanas`, `sabre`,
`1 handed`, `heavy weapons`, `blunt`, `hackers`, `unarmed`, `polearm`, `crossbow`), `slave anim`,
`uses left arm` / `uses right arm` (also set when the matching `has weapon` isn't NO), the leg-damage
min/max/ideal values, and `events`. A COMBAT_TECHNIQUE becomes a definition from `anim name`,
`anim speed mult`, `gains ground`, `is block` and its weapon types, always on the upper layer.

`layer` maps to a layer index by exact string: `lower` 0, `overlay` 2, `tail` 3, `ears` 4, anything else 1
(the upper body). `all` also lands on 1 but sets a separate "all" flag.

Grouping for a character's set (`@ 1405bbd40`, Observed, not traced in full): `disabled` records are left
out; records with a `weather type` go to per-weather overlay lists; overlays with `idle` set to an idle
overlay list; animations with a non-zero `move speed` are sorted by it into upper and lower movement
lists (animations with `strafe speed max` > 0 to a strafe list); everything else is kept by name.

## Run-time blending

Per layer, Kenshi keeps a list of active animations and a list of animations fading out
(`@ 1405b5c30`, Verified). Each running animation has a weight, a target weight, a time, a speed and its
definition's `loop`, `synchs` and `synch offset`.

- **Start** (`@ 1405b3090`, Verified): the Ogre state is fetched by name, set to the definition's loop
  flag and weight 0, enabled, and placed at its start time (synched animations at the layer's phase plus
  `synch offset`).
- **Weight** (`@ 1405b1630`, Verified): each frame the weight moves linearly toward the target by
  rate × frame time, without overshooting, and is never negative. Animations that reach 0 in the fade-out
  list are disabled and dropped.
- **Rate** (Observed): the per-animation rate is the animation's speed clamped to 0.5 ... 10, times a
  per-character rate; in one mode it is effectively instant (999,999). CONSTANTS `animation blend rate`
  (fcs.def default 4, "1 is very slow") is read at startup (`@ 14086b2b0`); that it is the per-character
  rate is likely but not traced.
- **Normalising** (Observed): when the layer normalises, the active weights are scaled so they add up to
  the layer's total, and the fading-out animations share what is left. This matches `normalise`'s
  description ("will always total up to 1.0").
- **Time** (`@ 1405b1700`, Verified): an unsynched animation advances by frame time × its speed; looping
  ones wrap, others stop at the end (or 0 when playing backwards) and report that they finished. A synched
  animation instead sits at length × fractional part of (layer phase + `synch offset`), so a lower-body
  walk and an upper-body walk stay in step.
- **Root motion** (Observed): for animations flagged to move the character, Kenshi turns off the state's
  translation (Kenshi's Ogre then drops X and Z, keeping Y) and reads the `Bip01` translation itself with
  `Animation::getTranslation` to move the character (`@ 1405b5c30`). Which record field sets the flag
  (`relocates` is the obvious candidate) wasn't traced.

The Ogre side then sums the enabled states (average mode: weights scaled down only if they sum above 1;
override bones applied in a second pass). See [ogre-skeleton.md](formats/ogre-skeleton.md#blending-in-kenshis-ogre).

### In Meitou (`Meitou.Data.Characters.AnimationMask`, viewer `Animator`)

The viewer implements the Ogre side of this and the startup preprocessing per layer, without Kenshi's
per-layer controller (no fades, synching or normalising):

- A layer's deleted tracks and override bones come from its ANIMATION record (tables above; `delete tail`
  defaults to true). A name that is an Ogre animation rather than a record name uses the first ANIMATION record
  with that `anim name`, first in load order (viewer choice; Kenshi's gathering order was not traced). Instead of cloning the
  animation per record (as the preprocessing does), the viewer keeps the source animation and skips the deleted
  tracks when it samples.
- Per track, as stock Ogre's v1 node track (MIT source): translation × weight is added, rotation
  nlerp(identity, key, weight) is applied in local space, scale 1 + (key − 1) × weight multiplies.
- **Observed** (data, scratch survey): every animation of `male_skeleton.skeleton` has a track for all 30 bones,
  so without the deletions an upper-body animation would also drive the legs. `walk lower` deletes 15 tracks
  (both arms, spine, neck, head, props) and `walk upper sword` 10 (`delete below waist`); together they move the
  whole body. Several records share one Ogre animation with different masks (`run upper stealth` and
  `run lower stealth` both play `ninjarun`).

## Appearance hooks on the skeleton

### Posture sliders

Verified, `@ 14052cf80`, called from `@ 1405338f0`.

Three skeleton animations are used as one-frame pose libraries: each is enabled, looping, at weight 1, and
**held at time = length × slider ÷ 100**:

| Animation | Slider (appearance value) |
| --- | --- |
| `postures` | `Posture` |
| `neck set` | `Neck position` |
| `shoulder set` | `Shoulder set` |

The portrait renderer (`@ 14084b890`) does the same, and also copies every bone's size from the character.

### Body shape sliders

Verified, `@ 14052e6c0`.

Only for skeletons with exactly 30 bones (the human-shaped `male_skeleton` / `female_skeleton`). Slider
values (÷100) from the character's appearance drive the extra per-bone vectors of Kenshi's Ogre
([bone maths](formats/ogre-skeleton.md#bone-maths-in-kenshis-ogre)):

- **Sliders read**: `Height`, `Leg length`, `Frame`, `Legs bulk`, `Legs shape`, `Hips`, `Chest`, `Waist`,
  `Stomach`, `Mid-section`, `Breast size`, `Breast height`, `Breast spacing` (the last two female only),
  `Arm bulk`, `Shoulders`, `Hands`, `Feet`, `Head size`, `Head shape`, `Neck`, `Neck width`,
  `Neck length`, `Jaw`.
- **Bone size set on**: `Bip01 Pelvis`, `Bip01 Spine`, `Bip01 Spine1`, `Bip01 Spine2`, `Bip01 L/R Thigh`,
  `Bip01 L/R Calf`, `Bip01 L/R Foot`, `Bip01 L/R Toe0`, `Bip01 L/R Clavicle`, `Bip01 L/R UpperArm`,
  `Bip01 L/R Forearm`, `Bip01 L/R Hand`, `Bip01 Neck`, `Bip01 Head`, `Bip01 Jaw` (if present),
  `L Boob` / `R Boob` (if present).
- **Positional size set on**: `Bip01 L/R Thigh`, `Bip01 L/R UpperArm`, `Bip01 L/R Toe0`, `L Boob` / `R Boob`.
- `Mid-section` is looked up but its value is never used (Verified, disassembly: the result register is
  overwritten unread). The same function also stores (`Height` − 80) × 0.025 in the appearance object; what
  reads it is Unknown.

#### The formulas (Verified, decompilation and disassembly of `@ 14052e6c0`; implemented in `CharacterShape`)

Notation: every slider value is × 0.01 (100 → 1), written by name (*Height*, *Arm bulk*...). `lerp(a, t)` means
1 + (a − 1) × t. Vectors are (X, Y, Z) in the bone's own axes; "×" on a vector is per component.

Two character values feed in besides the sliders (computed by `@ 14052bc70`, called before every shape update):

- **Muscle** *M* = (max(strength, weapon smith, armour smith) × 0.01 − 0.2) / (0.99 − 0.2), **not clamped**
  (strength 20 → 0, 99 → 1, 0 → −0.25). Strength is a virtual getter on the stats object; the two smithing
  skills are read directly. Observed: the field names come from matching the stats object's offsets to the
  stats save writer (`@ 14064a8f0`, which writes `strength` from +0x80, `weapon smith` +0xd0, `armour smith`
  +0xd4...); that it is the same object is assumed.
- **Starvation** *S* = clamp((1 − clamp(*N* − 1, 0, 1)) × 1.5, 0, 1), where *N* is a character float at
  +0x4b8 (Unknown; presumably nutrition: *S* is 1 up to *N* = 4/3 and 0 from *N* = 2).

Factors: *thin7* = lerp(0.7, S), *thin86* = lerp(0.86, S), *thin6* = lerp(0.6, S), *thin4* = lerp(0.4, S);
*bulk* = lerp(1.27, M) for men, lerp(1.24, M) for women; *broad* = lerp(1.13, M) men, lerp(1.12, M) women.

Derived values: *h* = *Height* × g, *Fr* = *Frame* × g, where g is a virtual on the character that is 1.0 for
`Character` and `CharacterHuman` (vtable slot 0x390 returns the constant 1; animals may override it, not
checked). *L* = *Leg length*, *H* = h + L − 1.

| Bone | Bone size | Positional size |
| --- | --- | --- |
| `Bip01 L/R Thigh` | (t, H × 0.95, t) × n, with t = (*Legs shape* × LB + (*Hips* − 1) / 3) × Fr, LB = *Legs bulk* × thin7 × bulk | (1, Fr × (2 − h) × *Hips*, 1) |
| `Bip01 L/R Calf` | ((2 − *Legs shape*) × LB × Fr, H, same) × leg factor × (1, k, 1) | – |
| `Bip01 Pelvis` | (*Hips* × Fr, h, *Hips* × Fr) | – |
| `Bip01 Spine` | (p × Fr, h, p × St × Fr), p = (*Hips* − 1) × 0.6 + 1, St = *Stomach* × thin6 | – |
| `Bip01 Spine1` | (*Waist* × thin7 × Fr, h, St × Fr) | – |
| `Bip01 Spine2` | (c45 × Fr, h, c9 × Fr), C = *Chest* × broad × thin86, c45 = (C − 1) × 0.45 + 1, c9 = (C − 1) × 0.9 + 1 | – |
| `L Boob`, `R Boob` (if the skeleton has `L Boob`) | (bx × Fr, by × h, B × Fr), B = *Breast size* × thin4; below 1: bx = (B − 1) × 0.75 + 1, by = (B − 1) × 0.5 + 1, else bx = by = B | (Bh, Bs × c9 × Fr × ((1 − h) × 0.5 + 1), (2 − h) × c9 × Fr); Bh, Bs = *Breast height*, *Breast spacing* for women, 1 for men |
| `Bip01 L/R UpperArm` | (A × Fr, h, ((A − 1) × 1.5 + 1) × Fr) × stump factor, A = *Arm bulk* × thin7 × bulk | (Sh × c45, 1, 1) |
| `Bip01 L/R Forearm` | (A × Fr, h, A × Fr) × arm factor | – |
| `Bip01 L/R Clavicle` | (Sh × Fr, ((Sh − 1) × 0.3 + 1) × Fr, Sh × Fr), Sh = *Shoulders* × broad | – |
| `Bip01 L/R Hand` | (A × Fr, h, A × Fr) × *Hands* × arm factor | – |
| `Bip01 L/R Foot` | (F, L, F) × leg factor, F = *Feet* × h | – |
| `Bip01 L/R Toe0` | (F, F, F) × leg factor | (1, 1, F) × leg factor |
| `Bip01 Neck` | (*Neck width* × thin6 × broad × Fr, *Neck length*, *Neck* × thin7 × Fr) | – |
| `Bip01 Head` | (f × *Head size* × *Head shape*, *Head size*, f × *Head size*), f = (Fr − 1) × 0.25 + 1 | – |
| `Bip01 Jaw` (if present) | (*Jaw* × head X, head Y, head Z) | – |

At neutral sliders (all 100, M = S = 0) every vector is (1, 1, 1) except the thighs' Y, 0.95 (follows from the table;
`CharacterShapeTests` checks the implementation against it). The positional sizes with (2 − h) roughly cancel the parent's Y scale, which multiplies
child offsets in Kenshi's bone maths, so a taller body keeps its hip width.

Limb factors (left/right per side):

- **Arm / leg factor**: 0 when that limb is missing, else 1. The character's limb object answers per index
  0 left arm, 1 right arm, 2 left leg, 3 right leg (order Observed: matches fcs.def `severed limbs`), "1" meaning
  missing (`@ 1400cd450`, not traced further).
- **Stump factor** (upper arms): 1, unless the item worn under the name `armour` has `hide stump` with that
  arm's bit (HideStump: 1 left, 2 right, 3 both); then it is the arm factor, so a missing arm loses its stump
  too. Without it, a missing arm keeps the upper arm (the stump). Matches fcs.def "Scale specified upper arms
  to zero when wearing this with missing limbs".
- **n** (thighs): 0.8 when a per-leg object of the character (+0x4e0 right, +0x4d8 left) reports state 1, else 1.
  What it is (a robotic leg?) is Unknown.
- **k** (calves): a calf length ratio set by the appearance's equipment pass (`@ 140538630`): from the boots'
  ARMOUR `boot height` and the LIMB_REPLACEMENT `offset` of robotic legs (slots 52, 53), it stores
  (a + c) / (b + c) for the two legs (c a constant, not read), negative when the right leg is the longer one.
  The left calf's Y is × the ratio when it is positive, the right calf's Y × its negation when negative. 0 (no
  robotic legs) leaves both at 1. Observed (formula details not traced).

The skeleton's **movement scale** (`@ 14052bb10`) is H, except 1.0 when either of two character states holds
(a field at +0x2f8 equal to 1, or a flag of another object; neither identified). It multiplies every track
translation (ogre-skeleton.md), so walk cycles match the leg length.

**Observed** (viewer, male Greenlander body): the bone sizes scale the legs about the hips while `Bip01` keeps
its binding height (about 10 units; the idle's root track barely moves it), so the soles end about 1.8 units
below the ground at Height 120 and as much above it at 80 (bind pose and `idle_stand_relax` alike). What keeps
the feet on the ground in Kenshi (the character controller, the scene node height, a use of the
(`Height` − 80) × 0.025 value) was not found; the viewer doesn't compensate.

**Not Verified**: anything in game. The viewer (`--shape`, [viewer.md](viewer.md#characters)) applies the
formulas with M from the CHARACTER's `stats` STATS record (strength and smithing; an approximation, the game
uses the live stats), S = 0, all limbs present.

#### Muscle definition and the body's normal maps (Verified, `@ 14052bc70`, `@ 140531820`)

A third value, the **muscle definition** D = clamp(((swimming + athletics + 3 × max(dexterity × x, unarmed))
− (cooking + science)) × 0.01 / 5, mapped so 0.2 → 0 and 0.99 (women) / 0.9 (men) → 1, to 0–1), with x an
unidentified stats field (Unknown; stat names Observed as for M). It drives the body material
(characters.md, "Body shading"): the normal map is RACE `nm <gender>` and the `bodyBlendNormal` map
`nm <gender> strong`, blended by `muscleBlend` = D. If S > 0.25 or S > D, the blend map is
`nm <gender> skinny` with `muscleBlend` = S, and the base is `nm <gender> strong` when D > 0.33. The viewer
computes this (`CharacterShape.NormalBlend`) but doesn't render the blend.

### Heads

HEAD records only hold textures (`texture map`, `normal map`, `mask map`); RACE `heads male` /
`heads female` list them. There is no separate head mesh or skeleton: the head is part of the body mesh,
its shape comes from the body mesh's poses ([ogre-mesh.md](formats/ogre-mesh.md#poses-character-morph-targets))
and the sliders above. Hair and beards are ATTACHMENT records worn with a shared skeleton
([ogre-skeleton.md](formats/ogre-skeleton.md#binding-worn-meshes-to-the-character)).

## Open questions

- The order of ANIMATION_FILE sources (comparator `@ 14052ba90`) and which file wins on a name clash.
- The suffix of the block-animation clones, and which COMBAT_TECHNIQUE `animal` values exist.
- The per-character blend rate and the normalising rules in detail (`@ 1405b5c30`).
- Which record field turns on root-motion extraction.
- Per-bone call in `@ 140539020`: Kenshi calls one virtual method on every bone with false, and on the
  children of `Bip01 Head` except `Bip01 Jaw` with true. What it is (scale inheritance? manual control?) is
  Unknown.
- How characters choose between candidate animations (idle chance, speed bands, injury ranges, combat
  state) beyond the definitions above. Not traced.
- Body shape: the nutrition value behind starvation (+0x4b8), the thigh-narrowing leg state, the two
  movement-scale exceptions, the stats field x in the muscle definition, what reads (`Height` − 80) × 0.025,
  and what the game does when a body file lacks a slider (the viewer uses 100).
