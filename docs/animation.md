# Character animation

Original AnimationClass/race records, Ogre skeletons and body-shape binary research. Tables and
expressions retain scopes; [skeleton](formats/ogre-skeleton.md), [characters](characters.md),
[stats](game/character-stats.md), [combat](game/combat.md) own related facts. Runtime AnimationLibrary/
AnimationMask/AnimationSystem and CharacterShape use data; renderer consumes named layers.

## Clips and layers

Race/animation-source skeleton links supply clips, definitions and masks; normalize names and
attach to the intended skeleton rather than choosing a clip from another race by name alone.
Startup preprocessing, priorities/selection flags, movement speed, combat footwork, root-motion,
turn/head-look, hit/KO/get-up and breathing/wound overlays have different consumers. Keep the
original lower/upper/whole-body layering and additive/average semantics; do not translate every
clip as a whole-body override. Advance one-shots through fades; loop durations and root tracks
must agree with movement. Missing clips/lengths use explicit fallback, not original-parity claims.

### Clip times between ticks

Engine choice (`GameHost.LayerTime`): the host draws a layer's clip time from the last tick's towards this one's by the shorter way round the clip (by the layer's
length), so a loop that wraps keeps moving through its end, forwards or backwards (the combat shuffles and side step have negative play speeds, so their time falls
through 0 once per cycle). Taking the new time at the wrap held the pose for a tick, or swept the whole clip in one tick when the time ran backwards.

Layer weights between ticks (`GameHost.BlendLayers`): the host interpolates the **whole** weight vector of the last two snapshots. The renderer adds layers up without normalising (the rest of
the weight is the binding pose) and the simulation keeps each body half's weights at a total of 1, so a layer that is only drawn while it exists in the current snapshot drops the total for the
span after it fades out (a gait change: `jog lower` leaving at weight ~0.25) and the pose lurches towards the binding pose for a frame; a new layer drawn at its full weight from the start of
the span has the same fault. A layer new this tick fades in from 0, one gone fades out to 0 at its last clip time, keeping its place in the layer order. **Verified** (probe of the drawn
bone palette of a running character at game speed 0.1, 560 moving frames): 6 such pops of up to 1.7 units a frame (hand, foot) before, none after; invisible at speed 1 only because one tick lasts 33 ms.

### Layer managers

**Verified** (decompilation; Meitou `AnimationSystem.BlendStep`, `Publish`). `AnimationClass` slot 6 (`@ 1405b68c0`) runs each frame: slot 7,
slot 8 (the drawn node turns toward the character's facing by at most 2 rad/s × dt, × 5 unless `+0x244` is 1, slerping when the gap is larger,
`@ 1405b24e0`), the phase (`+0xc8` += dt × `+0xcc`, wrapped to 0..1), then three of the five layer managers built at `@ 1406216b0`, whose
rate is CONSTANTS `animation blend rate` × 2, 1.25, 1, 0.5, 0.5 (`@ 140636dc0`). Manager 1 is normalised to 1, manager 0 to 1 minus the weights
of manager 1's flagged (`+0x5c`) layers, manager 2 not at all: lower (×2), upper and whole body (×1.25), overlay (×1); this mapping is
**Observed** (from that order); **Verified** for record clips (`FUN_1405ba280`, `FUN_1405b7540`): the ANIMATION record's `layer` picks the manager, `lower` 0, `overlay` 2, two other
names 4 and 3, `all` and anything else 1. A layer is flagged +0x5c when its record is `is action` or `layer` `all`, a technique's when it is not a plain block (**Observed**: `FUN_1405b5270`
computes that value; that it lands in +0x5c rests on a reading of the stack arguments, backed by manager 0 giving way only to manager 1's flagged, whole-body, layers). Per manager (`@ 1405b5c30`), with an active and a fading-out list:
- a fading layer's time advances only when it loops (+0x5d); a fading one-shot stands still, except a block replayed by `FUN_1405b7600` (+0x62), which plays backwards (**Verified**;
  Meitou: `AnimationSystem.RunsFading`, `FadingTime`, the block at its last forward rate, `Layer.Rate`). A defence start (`FUN_140609290`) first moves manager 1's flagged layers to the fading list (`FUN_1405b36f0`).
- a fading layer falls to 0 at the manager rate; an active one moves toward its target at the manager rate × its play rate clamped to 0.5..10
  (`@ 1405b1630`; instant on the first update). The play rate is signed (F × play speed × signed speed), so a clip with a negative play speed
  (the combat shuffles) takes the 0.5 floor going forward and its true rate backing off; Meitou `BlendStep`. What the play rate (`+0x40`) of a synched movement clip is was not traced: Meitou uses
  F × play speed × speed;
- before stepping, the active weights are scaled up to fill the total minus the fading weights (only up); after, everything is scaled down
  when over the total. A cross-fade therefore always sums to the total.
- times: a synched layer's is its clip length × frac(phase + `synch offset`), others advance by dt × play rate, wrapping or clamped (`@ 1405b1700`).
- the phase rate `+0xcc` is written only by the lower movement pick (`@ 14051d8e0` through `@ 14051d000`) as S × F × Σ(`play speed` × normalised weight) over **all** its candidates, synched or not
  (**Verified**), and kept otherwise: standing out of combat or under a whole-body technique nothing rewrites it, so a walk fading out goes on stepping at its last rate; the combat stance runs the pick every frame, so
  standing in it the rate is 0. F = `@ 14052bad0` = appearance `+0x188` divided by the race scale (vtable 0x390); `@ 14052e6c0` writes `+0x188` = 2 − H (H = Height × g + Leg length − 1, **Verified**), so a
  human's F is Meitou's 2 − H. Meitou: `CharacterAnimation.PhaseRate`, `AnimationSystem.Settle`. The planted foot of a clip moves at 0.4 to 0.73 of the ground speed by the data's `play speed` (measured against the
  skeleton clips, **Observed**), and the rate formula is the original's, so the original slides alike; no stride correction exists in the code read.
- layers: both managers' lists are vectors that double when full (`@ 1405b5270`): no cap was found (**Verified**); Meitou keeps up to `CharacterAnimation.MaxLayers` 16 (engine choice; a base-game fight needs 5).

### Speed the animations see, and the drawn facing

**Verified** (`CharMovement` update `FUN_14065ffa0`, movement spec): the speed S an animation is chosen and played by is the **measured displacement** of the physics
step over dt (capped at the character's max speed, `+0x19c`), not the commanded velocity; in the fight steering it is rotated into the facing's frame
(forward signed, side = minus x). Meitou: `AnimationSystem.Act` takes the commanded velocity no faster than the character really moved this tick, so a walker held by a wall
or a crowd does not run on the spot (it is never taken faster than commanded; pushes by neighbours are not added).

**Verified** (`AnimationClass` slot 8 `@ 1405b24e0`; constants 2.0 at `1421682ad0` and 5.0 at `141685340` read from the binary): the drawn node does not take the facing
at once. Each frame it slerps towards it by at most a half-angle step of 2 × dt in the combat stance (flag `+0x244` = 1), 10 × dt otherwise, so the body turns at
most 4 rad/s (229 deg/s) in the stance and 20 rad/s out of it; an imprisoned character (`+0x2f8` = 1) takes it directly. Meitou: `CharacterAnimation.DrawYaw`
(`AnimationSystem.FollowFacing`, published as `CharacterSnapshot.DrawYaw`, used by the host for the drawn facing); a carried, held or down character takes the facing
directly (engine choice). The movement's own turn rate stays an engine choice (`MovementSystem.TurnRate`).

### A carrier

**Verified** records (rebirth.mod, by probe): `carrying left arm only` is an overlay clip with `override L arm` and every other bone deleted, `stand carrying left` an overlay with `override L arm`,
`walk/jog carrying left` upper clips with `override L arm`; `shoulder lift left/right` play speed 2.

### Combat upper body

**Verified** (`AnimationClassHuman` slot 28, `FUN_14051d660`): in the combat stance the upper body plays no movement clip; it holds one guard,
the idle candidate with the highest weight (speed weight × leg weight), the first in the record list's order on a tie (all guards tie at
standing speed; [boost-unordered](formats/boost-unordered.md)), chosen again every frame. A character counts as moving at any filtered
speed other than 0. Meitou: `AnimationLibrary.CombatGuard`.
A block (an upper-body clip with no lower-body tracks) played standing outside the stance (the running flag set, the fighter held at a distance) would leave the legs
unposed, as no idle plays under a technique: Meitou gives the legs the combat footwork's lower clip then (engine choice, `AnimationSystem.PickGait` `blockStanding`).

### Animation files

**Verified** (decompilation of `AppearanceBase::buildBody` `@ 140539020`, Kenshi's OgreMain): after building the body entity, the game
gathers the ANIMATION_FILE records of the race's `animation files`, then of the character's own record's, sorts them by the
`ushort` at GameData+0x2f8 (KenshiLib `createdIndex`: the load-order index of the file that created the record; **Observed** name),
highest first (`qsort`, comparator `@ 14052ba90`; ties are in no defined order), and for each takes `female animation` or
`male animation` by the appearance's sex, cuts it to the bare file name, and when the resource exists calls
`OldSkeletonInstance::addLinkedSkeletonAnimationSource(name, -1)` (a negative scale is Kenshi's per-instance link, at scale 1).
Kenshi's `Skeleton::_getAnimationImpl` looks a clip up in the skeleton's own animations first, then in the linked sources in
link order (first found wins); tracks apply by bone handle, and a name already linked is not linked again.
The base game uses none (one ANIMATION_FILE, `base animations`, that no race names); mods add theirs to every race: in a
large modded install 355 of 573 ANIMATION clips exist only in linked files (**Observed**). Meitou: `AnimationFiles`,
`CharacterRigs.Rig(name, appearance)`; `AnimationLengths` adds every ANIMATION_FILE's clips after the base skeletons in the same
order (it knows clips by name only). Tie order: list order (engine choice). With `preprocess` set, the startup preprocessing
(below) also runs over the file's skeletons (`@ 140871f00`).

### Choosing candidates (`@ 14051d660`)

A weapon animates as its WEAPON `skill category animation override` unless that is 21 (the default), else its `skill category` (`Sword::Sword` `+0x238`, **Verified**;
9 base weapons override, e.g. cleavers, jittes, heavy polearms, the Topper); its skill stays its own. Meitou: `WeaponData.AnimationCategory` sets the weapon kind clips and
techniques match.

Verified (decompilation). Every selection (movement blend, idles, stumbles) runs the same filter over a list
of definitions and gives each a weight; zero-weight ones are dropped:

- `category` must equal the character's current category (CharacterAnimCategory: 0 ANIM_NORMAL,
  1 IMPRISONED, 2 SLEEPING, 3 CARRIED, 4 SWIMMING, 5 GROUNDED, 6 COMBAT, 7 ATTACKS, 8 RANGED, enum names
  from the FCS editor). Meitou plays category 4 in deep water ([water states](game/pathfinding.md#water-states)): `swim` (clip `swim2`), `swim carry`, `swim carry left`, `swim idle`;
  and category 5 while crawling ([forced crawl](game/ragdolls.md#forced-crawl)).
  The hit-reaction pick `FUN_14051f420` -> `FUN_14051d660` tests it too, against the class category `AC+0xf0` that `FUN_140520500` sets (4 in deep water for a swimming race, else 5 held down or crawling, else 0; **Verified**): a swimmer or crawler only gets stumbles of category 4 or 5, none if no record has them (Meitou: `AnimationStance.Category`). The stumble picker never reads `weather type` (`FUN_1405bbd40` files a weather record in the by-name and per-part stumble maps like any other, **Verified**), so such a record listing `stumbles` is a valid stumble (base data: the only weather-type record, a mod's `wind in face`, has none; Meitou appends them last as `WeatherCategory`, taken by no other selector).
- `is combat mode` and `stealth mode`: EITHER (2) or equal to the character's combat / stealth state (when the combat flag is set: [combat](game/combat.md#combat-stance)).
- `crouched` / `prone` (record byte +0x9c for crouched; `FUN_14051ec80`:110 compares it with `AnimationClass` +0x21c; the class's prone byte is +0x21d): both are written together by `FUN_14051dda0` (vtable slot 18); with both set crouched is cleared and "ANIM ERROR: crouched and proned" logged. Task_Crouch (27) sets crouched once its `squat T` clip has passed a progress threshold (`FUN_140346440`, threshold not read), Task_UnCrouch (28) and the end of many tasks clear it (**Verified**). The base data has no crouch task. Meitou: `CharacterCold.Crouched` feeds `AnimationStance.Crouched` (never with prone); nothing sets it yet, the task trigger is missing. The movement filter `FUN_14051d660` reads neither byte (**Verified**, decompile; no reader of `+0x21d` found in the animation class's functions): the category alone keeps the crawl clips to a crawler. So a character with a leg down swims in deep water: `FUN_140520500` sets category 4 before it looks at the crippled flag (#651; the reporter's original: limbless humans swim). Meitou: `FitsExceptLegs` still matches `prone`; the swim pick asks with the stance not prone (`AnimationSystem`), which gives a one-legged swimmer `swim` at its crawl speed 5 (**Observed**, scratch pick over the base data).
- Arms (`@ 14051d240`, **Verified**): for layer index 1 (upper **and** `all`, which keeps index 1) and 2 (overlay) the lower layer is exempt: `has weapon L` and `R` both YES mean exactly
  one hand holds a weapon; otherwise each hand's YES / NO must match what it holds, EITHER passes. For the same layers the weapon type held (5 = unarmed) must be a set bit of the record's weapon-kind
  mask, except that fists skip the test (`@ 14051ec80`). Meitou: `AnimationDefinition.ArmsFit`, `KindFits`, applied in `FitsExceptLegs`.
- **Speed weight** (`@ 14051d100`): with the current speed v (the character's forward speed, signed, below)
  and the definition's speeds times the race's scale g (vtable 0x390 on the character, 1 for humans):
  a ramp `r(v)` = 0 below `min speed`; rising linearly from 0 at `min speed` to 1 at `move speed` (just 1 when
  `min speed` < 1); falling linearly from 1 at `move speed` to 0 at `max speed`, 0 above; when `max speed` < 0.5 the
  falling line is followed past `max speed` too (`@ 14051cd10`): with a max of 0 it rises, so the fastest clip stays 1,
  and with a negative max (the backing-off `walk lower combat shuffle long BK`, −99/−10/−6) it is 0 at forward speeds. If both v and `min speed` are below −40 the weight
  is 1. A positive r is then sharpened: **w = 1 / (1 + e^(−10 (2r − 1)))** (so r = 0.5 → 0.5, r = 1 → 0.99995).
- Only for idle picks (layer upper or overlay): `being carried` / `carrying left` / `carrying right` must
  match the character, and the weapon-type flags must include the equipped weapon's type; for unarmed (type 5)
  a clip that `has weapon L` or `R` YES gets weight 0.1 instead.
- Finally the weights are **normalised per layer** (each layer's weights divided by that layer's sum,
  `@ 14051cde0`).

### Side steps

- **Facing.** In the fight steering a fighter faces its target (`lookAtCharacter`) whichever way it moves, except in jog mode (body gap over 90,
  back under 70: [combat stance](game/combat.md#combat-stance)) or chase mode, where it faces its velocity.
- **Animation.** Forward speed = z, side speed = −x (to the right) only while strafing, else 0; x is zeroed when |z| > 1 and |x| < 0.5. The side-step
  clip gets weight |side| / (|side| + |forward|) (the last value while the sum is ≤ 1, through a filter `@ AC+0x2b8`, times the movement layer's weight), the forward movement clips
  the rest; it plays at F × `play speed` × side speed (signed). Base data: `strafe lower` (layer lower, no `move speed`, rebirth.mod `play speed`
  −0.12, `strafe speed max` 999, combat only); `sidestep` is disabled and `strafe upper` removed. `strafe speed max` (999 on the blows, 99 on the guards, 0 on the walks) is only tested
  for > 0 when the lists are built (`FUN_1405bbd40`, **Observed**): such a clip goes to the strafe list instead of the by-name action list, and the value is never a speed.
  Only the base/animal class reads the strafe list (its first entry is the side step in a fight); humans fetch `strafe lower` by name. An animal's `strafe anim`
  is its side step only when none of its `animations` has `strafe speed max` > 0.
  Human side step (`FUN_14051fc60`, **Verified**): only in combat mode, the record named `strafe lower` from the race's by-name map (first lookup cached globally), weight 1 − the movement layer's, no stance fit test. Meitou: `AnimationLibrary.Strafe` looks it up by name.
- Meitou: `AnimationSystem.Strafe` and `AnimationLibrary.Strafe`; a fighter in its footwork (`CharacterCold.FaceSlot`) keeps facing its
  target while its path moves it (`MovementSystem`, the turn's speed cut skipped); the share is 0 below a total speed of 1 (standing; engine reading of the weight).

### In a bed or a cage

**Observed** (decompilation of the out-of-combat pick `@ 140520500`; `Character+0x2f8` 1 bed, 2 imprisoned, [carry-loot](game/carry-loot.md)). In a bed with no action running the animation plays the
bed's functionality `animation` clip (field `+0x408`, `sleepinbed` in the base data) at weight 1 and a layer rate of exactly 0.1 (`@ 14033a1b0` hands the rate to the layer as is, it is not multiplied by the
record's `play speed`); an action in progress (`gotobed`) runs by the normal rules. In a cage the functionality supplies three clips, entry 0 of each list (`UseableStuff::UseableStuff` `FUN_1402a0900`, **Verified**): `+0x408` `animation`
(the Cage: `stand 1`), `+0x410` `animation KO` (`foetal`), `+0x418` `animation dazed` (`sitting dazed`). The pick: when character vfunc `0x30` (unconscious) is true, the KO clip at
rate 1.0; else `Character+0x51c` (`MedicalSystem::dazedOrAlert`, KenshiLib name **Observed**) at or under 0.05, or a leg-collapse state, gives `+0x418` at rate 0.025, at or under 0.2 `+0x418` at 0.5, else `+0x408` at 0.5; a running `stand 1` action is dropped first.
Meitou: a sleeper plays `CharacterCold.OperateClip` as a held clip at that flat 0.1 (`AnimationSystem.BedRate`; #634: a mod's `sleepinbed` `play speed` 0.2 does not speed it up); a prisoner (`CharacterCold.CageClips`) plays the KO clip while down, the dazed clip
with a leg down (engine reading of dazedOrAlert), else the idle clip, at the records' play speeds.

### At a machine or on a seat

**Verified** (decompilation, `Task_OperateMachine`'s update `FUN_14035bc90`, the named play `FUN_1405203b0` / `FUN_14051eab0`): every update the operator plays the
functionality's `animation` (`stonemine` when it names none) at rate R × the record's `play speed`, R = 0.55 + (2 − 0.55) × 0.01 × its level in the
functionality's `stat used` (the stat getter `FUN_140884ac0`, with its multipliers; 0 for stat 0), at most 1.5. Seats and training dummies run the same task; a chair has
no stat (**Verified** by probe: `chair`, `chair-fixed`, `sit on floor (no chair)` `stat used` 0), so a sitter plays its clip at 0.55 × `play speed`. Base data hides it on
chairs (`sitting chair` is a still pose); mods that animate the seat clip show it (#634: Immersive non-combat animations sets `sitting chair` to `play speed` 0.7).
Meitou: `AnimationSystem.OperateRateOf` (`CharacterCold.OperateStat`); a seated NPC's seat is taken as one without a stat (engine choice).

## Shape notation and factors

**Feet lift** (**Verified**, decompiled): the same pass (`FUN_140538630`) stores at the character's +0x2cc the lower leg `offset` when both leg slots (52, 53) hold
a LIMB_REPLACEMENT, else the worn boots' `boot height` (0 without), and `FUN_1405f16a0` places the body mesh's node at Y = +0x2cc − +0x2c8, so the body is
raised by it. Meitou subtracts it from the ground offset (`CharacterShape.FeetLiftOf`, `CharacterRigs`), so the ragdoll's placement takes it too. Base game: no
boots have a `boot height`; two Scout legs lift 0.5, two Stealth legs -0.2. Mods use it for heels (issue #126; **Observed**: the Animation Collection Pack's
2B Boots, `boot height` 1 on the female sandal mesh, a geta whose teeth sank into the ground before).
Muscle normal blends, head poses and skinning follow their own data/shader rules, not bone scale.

### Stat-driven body

**Verified** (decompilation): `FUN_14052bc70` sets three values on the human appearance (+0x1a0..+0x1a8) from the character's `CharStats`
(offsets: [character-stats](game/character-stats.md#the-stat-list)) and hunger (`+0x4b8`, [ui-screens](game/ui-screens.md)):

- **Bulk M** = map(max(strength, weapon smith, armour smith) × 0.01, 0.2 → 0, 0.99 → 1), not clamped. Strength is read through the stats object's slot-2
  accessor; Meitou takes the stat itself.
- **Starvation S** = clamp((1 − clamp(hunger − 1, 0, 1)) × 1.5, 0, 1).

### Startup preprocessing

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

### Posture sliders

| Animation | Slider (appearance value) |
| --- | --- |
| `postures` | `Posture` |
| `neck set` | `Neck position` |
| `shoulder set` | `Shoulder set` |

### The formulas (Verified, decompilation and disassembly of `@ 14052e6c0`; implemented in `CharacterShape`)

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

## Open questions

- ~~Which record field turns on root-motion extraction~~: `relocates` for ANIMATION records; combat techniques
  by their own rule ([Root motion](#clips-and-layers)). Still open: the time divisor of the root-motion velocity,
  and how combat footwork moves the body while combat sets animation-driven movement.
- How characters choose between candidate animations: largely answered ([Choosing candidates](#choosing-candidates--14051d660),
  [Movement](#clips-and-layers), [Hit reactions](#clips-and-layers)). Still open: the
  non-combat lower-body update (slot 28 runs only in combat; the out-of-combat path, probably the same
  movement blend, was not traced), how the idle timer's units work,
  the get-up clip's playback rate, how fast the movement direction turns, and which of `foetal` /
  `sitting dazed` / `knockout training` play when. Our engine's rules are in simulation.md.

## Expression reference

- Selection data used by the simulation: `clip seconds per second = speed x play speed`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Layer managers (`AnimationClass` vtable slot 6, `@ 1405b68c0`)
- Choosing candidates (`@ 14051d660`)
- Combat footwork and strafing (`AnimationClassHuman` slots 28 and 29, `@ 14051fc60`, `@ 14051de70`)
- The formulas (Verified, decompilation and disassembly of `@ 14052e6c0`; implemented in `CharacterShape`)
- Muscle definition and the body's normal maps (Verified, `@ 14052bc70`, `@ 140531820`)
