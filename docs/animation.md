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
- **Root motion**: for animations flagged to move the character, Kenshi turns off the state's
  translation (Kenshi's Ogre then drops X and Z, keeping Y) and reads the `Bip01` translation itself with
  `Animation::getTranslation` to move the character (`@ 1405b5c30`). The flag and how the motion is
  applied: [Root motion](#root-motion) below.

The skeleton instance is in **cumulative** blend mode (Kenshi sets it when it attaches its controller to
the body, `@ 1405b93a0`; Verified, details in
[ogre-skeleton.md](formats/ogre-skeleton.md#blending-in-kenshis-ogre)), so Ogre never divides the weights by
their sum: the layer totals below are the whole story, and the posture libraries add on top at weight 1.

The Ogre side then sums the enabled states (cumulative, as above; override bones applied in a second pass).
See [ogre-skeleton.md](formats/ogre-skeleton.md#blending-in-kenshis-ogre).

### The three layers each frame (`AnimationClass` vtable slot 6, `@ 1405b68c0`)

Verified (decompilation). The controller keeps one layer object per index: 0 lower, 1 upper, 2 overlay
(3 tail and 4 ears exist for animals). Each frame:

1. The **synch phase** (one value per character, +0xc8) advances by frame time × the phase rate (+0xcc) and
   wraps to 0..1. The rate is set by the movement blend (below).
2. Layer 1 (upper) is updated with total 1, normalising on.
3. Layer 0 (lower) is updated with total **1 − Σ weights of the upper layer's "full-body" animations**,
   normalising on. A running animation is full-body when its definition is `is action` **or** its `layer` is
   `all` (`@ 1405b5270` sets the running animation's byte +0x5c from definition +0x8d `is action` or +0x8b
   "all"). So a full-body action on the upper layer fades the legs' own animations out as it fades in.
4. Layer 2 (overlay) is updated with total 1, normalising off.
5. The three layers' root translations are summed (see [Root motion](#root-motion)).

Per running animation, besides the start/weight/time rules above (`@ 1405b5c30`):

- The weight rate is the animation's playback speed clamped to 0.5..10, × the layer's rate (layer +0x40);
  999,999 (instant) in the mode passed as the controller's sixth argument.
- Normalising (Verified): with W = Σ active weights and T = total − Σ fading-out weights, every active
  animation **without** the full-body flag is scaled by T / W (full-body ones only when T / W > 1). After the
  per-frame step, if Σ active > total, all active weights are scaled to the total; the fading-out
  animations then share total − Σ active (equally if they were all 0, else in proportion).
- A fading-out animation that doesn't loop keeps advancing in time; if it was started as a block (below) it
  runs **backwards** while it fades.

### Choosing candidates (`@ 14051d660`)

Verified (decompilation). Every selection (movement blend, idles, stumbles) runs the same filter over a list
of definitions and gives each a weight; zero-weight ones are dropped:

- `category` must equal the character's current category (CharacterAnimCategory: 0 ANIM_NORMAL,
  1 IMPRISONED, 2 SLEEPING, 3 CARRIED, 4 SWIMMING, 5 GROUNDED, 6 COMBAT, 7 ATTACKS, 8 RANGED, enum names
  from the FCS editor).
- `is combat mode` and `stealth mode`: EITHER (2) or equal to the character's combat / stealth state.
- Arms (`@ 14051d240`): an upper or overlay animation must fit what the hands hold (`has weapon L/R`
  against the character's state); details not decoded.
- **Speed weight** (`@ 14051d100`): with the current speed v (the character's forward speed, signed, below)
  and the definition's speeds times the race's scale g (vtable 0x390 on the character, 1 for humans):
  a ramp `r(v)` = 0 below `min speed`; rising linearly from 0 at `min speed` to 1 at `move speed` (just 1 when
  `min speed` < 1); falling linearly from 1 at `move speed` to 0 at `max speed` (stays 1 above `move speed`
  when `max speed` < 0.5, i.e. the fastest clip); 0 above. If both v and `min speed` are below −40 the weight
  is 1. A positive r is then sharpened: **w = 1 / (1 + e^(−10 (2r − 1)))** (so r = 0.5 → 0.5, r = 1 → 0.99995).
- **Leg weight**: the same ramp (without the sigmoid) over the health of the *more hurt* leg against
  `L/R leg damage min / ideal / max` of that side (left values if the left leg is worse). The loader forces
  min < 1 to −101 and max > 99 to 101, so a healthy-leg clip covers the whole range.
- Only for idle picks (layer upper or overlay): `being carried` / `carrying left` / `carrying right` must
  match the character, and the weapon-type flags must include the equipped weapon's type; for unarmed (type 5)
  a clip that `has weapon L` or `R` YES gets weight 0.1 instead.
- Finally the weights are **normalised per layer** (each layer's weights divided by that layer's sum,
  `@ 14051cde0`).

### Movement: which clips and how fast

Verified (decompilation; `@ 14051d8e0`, `@ 14051d000`, `@ 14052bad0`, `@ 14052e6c0`):

- The lower-body movement list is every non-overlay, non-weather, non-idle ANIMATION with a non-zero
  `move speed` whose layer isn't `upper`; the upper movement list holds the `upper` ones (`@ 1405bbd40`).
  All candidates of the list that pass the filter **play at once**, each at weight (layer total available ×
  its normalised weight). So between `walk lower` (14) and `jog lower` (45) both play, cross-faded by the
  sigmoid ramps.
- **Playback speed** of each movement clip = **F × `play speed` × v**, with v the character's current speed
  (animation controller +0x180, set every frame by the movement code from the velocity) and
  **F = (2 − H) / g**, where H is the skeleton's movement scale (H = *Height* × g + *Leg length* − 1, see
  "Body shape sliders") and g the race scale (1 for humans). F ≈ 1 / H to first order: a taller body
  plays its walk slower. (For skeletons without 30 bones F stays at its initial value, Unknown, probably 1.)
- That speed is in **clip seconds per real second** for a clip without `synchs` (time += dt × speed).
- A clip with `synchs` ignores its own speed for time: it sits at length × frac(phase + `synch offset`), and
  the **phase advances by F × v × Σ (wᵢ × `play speed`ᵢ) cycles per second**, the sum over the lower
  movement candidates with their normalised weights (`@ 14051d000` stores it at +0xcc). So for synched
  clips the rule is **cycles per second = F × v × play speed**, independent of the clip's length.
  (The base walk, jog and run records are all `synchs`, Verified against the install, so this is their rule.)
- v is in world units (decimetres) per second, as the controller measures it: the movement code divides the
  physics controller's displacement by the frame time (`CharMovement` vtable slot 11, `@ 14065ffa0`), and
  caps it at a medical limit (+0x19c, set by `@ 14051c960` from the medical system).

### Combat footwork and strafing (`AnimationClassHuman` slots 28 and 29, `@ 14051fc60`, `@ 14051de70`)

Verified (decompilation), except where marked:

- In combat, the movement code (`@ 1402ae1b0`) rotates the velocity into the character's frame (facing =
  +Z): the forward component (signed, **negative when backing off**) becomes v, the sideways component
  (negated X) becomes the strafe speed s (+0x184, only when strafing is allowed for the movement, else 0);
  when |forward| > 1 and |sideways| < 0.5, sideways is zeroed. It also sets the character's combat state for
  the filter.
- Slot 28 (the human lower-body update) runs only when the combat state is on. Its category is 0
  (ANIM_NORMAL), or 5 (GROUNDED) when the character can't stand (a flag at character +0x5b8, or the leg
  state vtable 0x368 returning 1 or 2). Unless an action holds the lower layer, the remaining lower weight
  goes to slot 29:
- Slot 29 splits it: strafe share = |s| / (|v| + |s|) (kept from the last frame when |v| + |s| ≤ 1). The
  normal movement blend (above) gets the rest; the record **`strafe lower`** (looked up by name) gets the
  strafe share at playback speed F × its `play speed` × s, so moving to the other side plays it
  **backwards**. Backing off uses the movement blend with negative v, which only clips with a negative
  `move speed` (the `... combat shuffle long BK` records) can match.
- Idle in combat (`@ 14051da80`): from the idle list (non-overlay records with `idle`), filtered as above
  with the carry and weapon-type checks, overlay-layer ones removed, one is picked by weighted chance and
  played at its `play speed`; a new pick happens when its timer runs out (reset to 5 + 60 × random, units
  not traced) or the combat state toggles. The guard records (`guard 1h`, `guard polearm`, `guard4 main` ...)
  are, by their flags, candidates of this pick (Observed: not checked against the data).
- `combatstance` is referenced by name only by the turret task (`Task_UseTurret`, `@ 1403474f0`).
  Observed: no other code names it, so it is not the generic combat overlay.

### Hit reactions (stumbles)

Verified (decompilation of the hit handler `@ 1404391f0`, chooser `@ 14051f420`, start `@ 140520490`):

- `stumbles` is a list of LOCATIONAL_DAMAGE references on an ANIMATION ("uses this animation as the stumble
  when this bodypart is hit"). The animation set builds a map from body part to its stumble animations
  (`@ 1405bbd40`). The record names are not in the executable.
- On a hit to body part P (damage packet d = cut, blunt, pierce, stun):
  - **Heavy** = Σd > the stumble threshold (toughness × stats multiplier × 0.01 × `stumble damage max` ×
    `damage multiplier`, `@ 140884020`; numbers in [game/combat.md](game/combat.md)).
  - **Free** = no stumble is currently playing.
  - If neither, no new stumble. Otherwise the candidates are P's stumble animations that pass the filter
    above, have **`big stumble` = (Heavy and Free)**, and **`stumble from` = the side the blow came from**.
    The side (`@ 140435600`) is FRONT (0) or REAR (1) only: REAR when the attacker's position lies behind
    the defender's facing (dot product of minus the facing with the direction to the attacker > 0; the
    facing vector's identity is Observed). LEFTSIDE / RIGHTSIDE are never produced.
  - Each candidate weighs its `chance` / 100; the one already playing weighs a quarter of that. One is drawn by
    weighted chance.
  - The sound is "Heavy_Hit" when a stumble was found and Heavy and Free, else "Light_Hit".
  - It starts only if the character isn't ragdolled / being carried (`@ 1407d1440`), isn't in two other
    states (+0x5b9, +0x5bc), and its leg state (vtable 0x368) is below 1. It replaces any running stumble and
    plays as an action at speed 1, i.e. playback rate = `play speed` (`@ 1405203b0` → `@ 14051eab0`).
- So the light/heavy split is `big stumble`, front/back is `stumble from`, and high/low/mid is the body part
  list in `stumbles` (head vs. legs ...). `category` and the record names play no role.
- Observed: before all this, if the character has an item in inventory section 9 or 5 that answers a
  virtual check (`@ 1405c8a10`), 25 % of hits are swallowed (no reaction, a tiny KO-time nudge via
  `@ 140644980`); what those items are (shields? armour?) is Unknown.
- While a stumble plays, the character's movement is locked and driven by root motion (below).

### Root motion

Verified (decompilation):

- The flag on a running animation (+0x68) comes from the definition's **`relocates`** (def +0x88) whenever an
  ANIMATION is played (`@ 1405b7ac0`). For COMBAT_TECHNIQUE definitions +0x88 is **`gains ground`**, but
  attacks are not played through that path: the combat code plays them by name (`@ 1405b7600`, from
  `CombatClass` slot 12) with root motion **on for attacks and dodges and off for pure blocks** (`is block`
  without `is dodge`), regardless of `gains ground`. Attacks there play at combat speed × `anim speed mult`
  (dodges at 1 × `anim speed mult`), weight 1, not looping, and blocks rewind when released (above).
- Playing an action (`@ 14051eab0`) also puts the character's movement into "animation drives me" mode when
  the action `relocates` (`@ 14065e240`, movement +0x37c), and leaves it when the action ends
  (`@ 14051e880`). The movement is also animation-driven while a stumble plays, during a ragdoll-to-clip
  blend, or while the current action has `disables movement` (`@ 14051c870`). Entering combat sets the same
  mode (`CombatClass` slots 10/11, `@ 14060cf60`, `@ 14060a590`) and leaving it clears it
  (`CombatClassAI` slot 9); how the footwork then moves the body was not traced (Unknown).
- Each frame the three layers' `Bip01` translations (from `Animation::getTranslation` at the root-motion
  animation's current time) are summed. If the sum's length didn't drop by more than 1 unit since last frame
  (a loop wrap or restart), the **difference to last frame's sum, rotated by the scene node's orientation**,
  is handed to the movement (+0x380). Otherwise nothing moves this frame.
- In animation-driven mode the movement code sets the physics character controller's velocity to that delta
  divided by a time value (min of two movement fields, +0xb4 / +0xbc, Unknown) and moves it **through the
  PhysX character controller, so collisions apply**; the animation speed v is set to 0 meanwhile.

### Turning and head look

- Verified (`@ 1405b18f0`): the body's scene node is oriented every frame straight from the movement's
  direction vector (horizontal part, `Quaternion::FromAxes`), no turn clip and no rate limit in the animation
  code; how fast the movement direction itself turns was not traced (Unknown; ANIMAL_CHARACTER `turn rate`
  exists for animals). The node's Y is the character's Y minus (anim +0x2c8 − anim +0x2cc), a vertical
  offset whose source is Unknown.
- Verified (`AnimationClassHuman` slot 23, `@ 1405b8430`): the record **`head turning`** is a pose library
  on its layer: for a look target within ±90° of the facing it plays at weight 1 and its time is set to
  ((angle + 90°) / 180°) × 0.07 s; beyond ±90° it fades out.

### Knockout, lying down and getting up

- Observed (`@ 140649320`, the medical update; `@ 1405cbd60`): a character that falls unconscious or dies
  is switched to its **ragdoll** (reason bit 1, or 0x800 in a case tied to character +0x3d4); dying also says
  "VO_Creature_Die". No KO or death clip is played; the ragdoll is the pose while down.
- Verified (`Task_GetUp`, `@ 14033ca10`, `@ 140334a20`): getting up picks the clip with slot 27
  (`@ 14051e3d0`): **`standing up 3`**, or **`crawl idle down`** when the character can't stand (flag
  +0x5b8 or leg state 1/2); in deep water (water state 3, `@ 1405c7fd0`) slot 26 instead, **`swim idle`**.
  It is started by a **ragdoll-to-animation blend** (`RagdollAnimation`, `@ 1407d36e0`, parameter 0.75,
  presumably seconds) that takes the ragdoll's current bone pose into the clip and zeroes all fading
  weights. The task ends when the clip is **86 %** through (or at once when the character stays down to
  crawl). The playback rate of the get-up clip was not traced (no speed argument in this path; Unknown).
- `knockout` (clip `stealthKO`) is named only by `Task_StealthKO` (`@ 140345eb0`, `@ 14034cd80`), i.e. the
  sneaking attacker's move; `sleeponfloor` only by `Task_SleepOnFloor` (`@ 140348170`). `foetal`,
  `sitting dazed` and `knockout training` are not named in the executable (chosen through `category` /
  idles, Unknown which).

### Overlays: breathing and wounds

- Verified (`AnimationClassHuman` slot 32, `@ 14051e150`): `breathing` + `" noarms"` (the record
  `breathing noarms`) plays on the overlay layer at weight 0.95, looping, at a speed fixed once per run to
  0.35 + 0.1 × random, when a character value (+0x180, which is the current speed v) is ≤ 22 and the
  character is in none of three states (one of them the leg state 1).
- The wound overlays (`hand2head` ...) are the LOCATIONAL_DAMAGE `pain anim` references: a part's overlay
  plays when its health is below the threshold given with the reference (`@ 14064a100`, see
  [game/character-stats.md](game/character-stats.md)). Observed: the hit handler also stores the hit part's
  `pain anim` name on the character (+0x288).

### In Meitou (`Meitou.Data.Characters.AnimationMask`, viewer `Animator`)

(The viewer's `Animator` was removed with the character viewer in phase 8; [character-viewer.md](character-viewer.md) keeps how it
worked, and the tag `model-viewer-last` has the code.) The viewer implemented the Ogre side of this and the startup preprocessing per layer, without Kenshi's
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
**held at time = length × slider ÷ 100** (they add on top of everything else because the skeleton is in cumulative mode, see [Run-time blending](#run-time-blending)):

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
- ~~Which record field turns on root-motion extraction~~: `relocates` for ANIMATION records; combat techniques
  by their own rule ([Root motion](#root-motion)). Still open: the time divisor of the root-motion velocity,
  and how combat footwork moves the body while combat sets animation-driven movement.
- Per-bone call in `@ 140539020`: Kenshi calls one virtual method on every bone with false, and on the
  children of `Bip01 Head` except `Bip01 Jaw` with true. What it is (scale inheritance? manual control?) is
  Unknown.
- How characters choose between candidate animations: largely answered ([Choosing candidates](#choosing-candidates-14051d660),
  [Movement](#movement-which-clips-and-how-fast), [Hit reactions](#hit-reactions-stumbles)). Still open: the
  non-combat lower-body update (slot 28 runs only in combat; the out-of-combat path, probably the same
  movement blend, was not traced), how the idle timer's units work, the arm check `@ 14051d240` in detail,
  the get-up clip's playback rate, how fast the movement direction turns, and which of `foetal` /
  `sitting dazed` / `knockout training` play when. Our engine's rules are in [simulation.md](simulation.md#animation).
- Body shape: the nutrition value behind starvation (+0x4b8), the thigh-narrowing leg state, the two
  movement-scale exceptions, the stats field x in the muscle definition, what reads (`Height` − 80) × 0.025,
  and what the game does when a body file lacks a slider (the viewer uses 100).

## Leg health and lying down (wired with the bodies)

**Observed** (engine choice, [simulation.md](simulation.md#animation)): the simulation fills `AnimationStance.LeftLeg` and `RightLeg` with the lowest `Fraction x 100`
of the character's leg parts, so a hurt leg selects the `limp` records by their leg damage ranges (100 healthy, negative past function; 1000..1000 means not used).
The base data has no unconscious or dead clip (the skeleton has `stealthKO` and `sleeponfloor`), so a knocked-out or dead character plays the `sleeponfloor` record
until proper clips exist. (The original uses the ragdoll while down and blends from it into `standing up 3`: [Knockout, lying down and getting up](#knockout-lying-down-and-getting-up).)

## Selection data used by the simulation

What the simulation's animation system ([simulation.md](simulation.md#animation)) reads from the ANIMATION records, found while building it:

- **Verified** (fcs.def and the 124 usable ANIMATION records of the install): `play speed` of a movement clip "is multiplied by movement speed, so
  should be small like 0.02, tune until feet match ground speed"; `move speed` is "the ideal speed it travels at"; `min speed` / `max speed` are
  "the ideal speed of the next anim below / above"; `synchs` clips of the lower and upper body share a phase; `has weapon L/R`, `is combat mode`
  and `stealth mode` are the Either enum (0 NO, 1 YES, 2 EITHER); `idle` marks standing clips with an `idle chance` and `idle time min/max`.
- **Observed** in the data: a leg range of 1000..1000 means "not used" (the limp clips constrain one leg only); the base movement chain per body
  layer is walk 14, jog 45, run 90 (`walk lower`, `jog lower`, `run lower` and the `upper` twins); `stand 1` / `stand 1 sword` are the gameplay
  idles, the six `idle_stand_*` poses come from `chareditor.mod`; `squat` is `is action`.
- **Observed** (a cross-check, the stride is not in the data): reading the rule as `clip seconds per second = speed x play speed`, a walk cycle of the
  1.4 s clip takes 1.67 s at speed 14 (0.84 clip seconds per second) and covers 2.3 m, a run cycle of the 0.567 s clip takes 0.31 s at speed 90 (1.8)
  and covers 2.8 m (1 unit = 1 dm): about one stride of a person each, which the other reading (cycles per second = speed x play speed) does not
  give (1.7 m and 5 m).
- **Correction** (decompilation, [Movement](#movement-which-clips-and-how-fast)): the original uses **both** readings, by `synchs`. An
  unsynched clip runs at F × `play speed` × v clip seconds per second; a synched clip's phase advances at F × v × (weighted) `play speed`
  **cycles** per second, F = (2 − H) / g ≈ 1 / H. The stride argument above is not evidence either way (a run cycle of 5 m at 9 m/s is
  1.8 cycles, 3.6 steps per second, which is plausible), so which applies to the walk and run records depends on their `synchs` flag. **Verified** (data, the install): every base-game walk, jog, run, limp, crouch and combat shuffle record has `synchs` set, so their cycles per second are F x v x `play speed` (a walk at 14 goes round 0.84 times a second, a run at 90 1.8 times; the engine had read them as clip seconds before, which made walks slide and runs flail).
