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

Hit reactions: the six ANIMATION records with `stumbles` all relocate; their `Bip01` track is the knockback
(**Observed**, root displacement of the base-game clips in world units): `mid blow light` 1.13 s ~10 back,
`mid blow` 4.63 s peak 18 back (~45%) ending 4 back, `back blow light` 0.67 s ~0, `back blow low` 1.47 s 17.7
forward, `mid blow drop` 6.57 s ~4 back, `back blow high` has no clip. When they play: [combat.md](game/combat.md#hit-reactions).

### Clip times between ticks

Engine choice (`GameHost.LayerTime`): the host draws a layer's clip time from the last tick's towards this one's by the shorter way round the clip (by the layer's
length), so a loop that wraps keeps moving through its end, forwards or backwards (the combat shuffles and side step have negative play speeds, so their time falls
through 0 once per cycle). Taking the new time at the wrap held the pose for a tick, or swept the whole clip in one tick when the time ran backwards.

### Layer managers

**Verified** (decompilation; Meitou `AnimationSystem.BlendStep`, `Publish`). `AnimationClass` slot 6 (`@ 1405b68c0`) runs each frame: slot 7,
slot 8 (the drawn node turns toward the character's facing by at most 2 rad/s × dt, × 5 unless `+0x244` is 1, slerping when the gap is larger,
`@ 1405b24e0`), the phase (`+0xc8` += dt × `+0xcc`, wrapped to 0..1), then three of the five layer managers built at `@ 1406216b0`, whose
rate is CONSTANTS `animation blend rate` × 2, 1.25, 1, 0.5, 0.5 (`@ 140636dc0`). Manager 1 is normalised to 1, manager 0 to 1 minus the weights
of manager 1's flagged (`+0x5c`) layers, manager 2 not at all: lower (×2), upper and whole body (×1.25), overlay (×1); this mapping is
**Observed** (from that order, not from where layers are added). Per manager (`@ 1405b5c30`), with an active and a fading-out list:
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

**Verified** (`FUN_140520000`, `AnimationClassHuman` vtable `+0xf0`, run every frame right after the normal update `FUN_1405b4770` -> `FUN_140520500`): a character whose carrying-left state byte (`+0x21e`) is set plays the record
named **`carrying left arm only`** (`carrying right arm only` for `+0x21f`), looked up by name (vtable `+0x18`) and played on its own layer (`FUN_1405b7ac0`), and marks that arm busy (`+0x301` left, `+0x300` right). Nothing in the
function tests combat or a technique, so a carrier has the arm-only overlay on the body in the stance, walking, under an attack, and the overlay is the one the idle pool never needs to draw. Meitou: `AnimationSystem.CarryArmClip`, added for
every carrier that is not down. `stand carrying left` is only a candidate of the random idle pool (`FUN_14051f230` returns the character's own stance first; its draw `FUN_14051ec80` passes flag 1 and skips the carrying test, the revalidation
`FUN_14051d310` / `FUN_14051ca10` then drops an upper or overlay pick whose carrying flags differ): **Unknown** whether it ever plays; Meitou does not play it. The idle update `FUN_14051d3b0` returns at once while a technique runs. The
combat guard pick `FUN_14051da80` removes overlay candidates; its upper candidates must carry the carrying flags (`sword ready (carry)` in rebirth.mod). A carrier fights with the right hand (the carrying records need `has weapon L` NO).

### Injury holds

**Verified** (`FUN_14064da70` medical tick -> `FUN_14064a100`; `FUN_14051f9f0` stores the result at `AnimationClass +0x268`; `FUN_140520000` plays it; `FUN_14051fe60` filters). LOCATIONAL_DAMAGE `pain anim` is a
reference to an ANIMATION record (`hand2head`, `hand2chest`, `hand2stomach`, `hand2shoulder`, `hand2leftshoulder`, ...: overlay layer, not idle, `uses left arm` / `uses right arm`, `override` of that arm) whose first value is a
threshold in per cent (60; stomach 65). Each medical tick every part with the reference and a health fraction (`+0x60`, [stats](game/character-stats.md)) **below** threshold x 0.01 enters a set of (record name, fraction) ordered
by fraction ascending, one entry per distinct fraction. `FUN_140520000`, for a character that carries nothing, in the normal animation category (no crawl, no swimming) and moves at 50 or less (constant `141683170`), walks that set
in order and plays the first record `FUN_14051fe60` accepts: a record that uses the right arm needs it free and working; a record that uses the left plays as is when the left arm is free and working, else as its **`-R` mirror**
(record name + `-R`, e.g. `hand2head-R`) when the right is. An arm is busy while its hand holds a weapon (`+0x18d` / `+0x18e`) or an active upper-manager layer uses it (`FUN_1405b2e10` / `FUN_1405b2db0`). No test of combat
or technique. Animals have no pain animations (`FUN_140646bb0` returns none when the slot `0x248` is set; the hit stores the part's name at character `+0x288`, purpose **Unknown**).
Meitou: `AnimationSystem.PainHold`, `BodyPartTemplate.PainAnim/PainThreshold`, `AnimationDefinition.UsesLeftArm/UsesRightArm`. **Unknown**: the keyed record list at `+0x198`
(`FUN_140520000` step before the set) and the byte `+0x302/+0x303` (forced arm busy).

### Combat upper body

**Verified** (`AnimationClassHuman` slot 28, `FUN_14051d660`): in the combat stance the upper body plays no movement clip; it holds one guard,
the idle candidate with the highest weight (speed weight × leg weight), the first in the record list's order on a tie (all guards tie at
standing speed; [boost-unordered](formats/boost-unordered.md)), chosen again every frame. A character counts as moving at any filtered
speed other than 0. Meitou: `AnimationLibrary.CombatGuard`.
A block (an upper-body clip with no lower-body tracks) played standing outside the stance (the running flag set, the fighter held at a distance) would leave the legs
unposed, as no idle plays under a technique: Meitou gives the legs the combat footwork's lower clip then (engine choice, `AnimationSystem.Update` `blockStanding`).

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
- `is combat mode` and `stealth mode`: EITHER (2) or equal to the character's combat / stealth state (when the combat flag is set: [combat](game/combat.md#combat-stance)).
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
- **Leg weight**: the same ramp (without the sigmoid) over the health of the *more hurt* leg against
  `L/R leg damage min / ideal / max` of that side (left values if the left leg is worse). The loader forces
  min < 1 to −101 and max > 99 to 101, so a healthy-leg clip covers the whole range. Idle picks (`@ 14051ec80`, `@ 14051d310`) weigh the legs the same way, so an idle records 0 .. 100 still plays on a leg
  below 0 (limping, not yet collapsed); Meitou reads the range alike in `AnimationDefinition.Fits` (`LegFloor`, `LegCeil`; before, the idle list was empty for such a leg).
  The idle draw `FUN_14051ec80` never comes back empty (**Verified**): with no record fitting it returns the first record of the idle list it was given (`+0x2c0`; which record that is in the base data is **Unknown**). The crawl idles' `-999` leg minimum is read as -101, so a leg past -101 % (a severed one; `HealthPart.Fraction` is unbounded) fits neither `crawl idle` and the pick was empty, leaving a stopped crawler in the bind pose (**Verified**: probe of the base data, no crawl idle fits a leg under -101 in any combat or stealth state). Meitou: `AnimationLibrary.CrawlIdles` then ignores the leg ranges and takes the first grounded idle that fits the rest, in data order.
- Only for idle picks (layer upper or overlay): `being carried` / `carrying left` / `carrying right` must
  match the character, and the weapon-type flags must include the equipped weapon's type; for unarmed (type 5)
  a clip that `has weapon L` or `R` YES gets weight 0.1 instead.
- Finally the weights are **normalised per layer** (each layer's weights divided by that layer's sum,
  `@ 14051cde0`).

### Side steps

**Observed** (decompilation: `CombatMovementController::combatMovementUpdate` `@ 1402af1e0`, `combatMovementAnimationUpdate` `@ 1402ae1b0`,
`setupCombatMovementVariables` `@ 1402ae6d0`, `AnimationClass` slot 29 `@ 14051de70`, `@ 14051f3f0`; constants read from the binary).

- **Facing.** In the fight steering a fighter faces its target (`lookAtCharacter`) whichever way it moves, except in jog mode (body gap over 90,
  back under 70: [combat stance](game/combat.md#combat-stance)) or chase mode, where it faces its velocity.
- **State** `strafing` (+0x124): with the velocity in the facing's frame (z forward, x side), it starts when |x| / |z| > FORWARD_THRESHOLD
  (+0xf4 = 1.5 + 3r per character) and stops when |z| / |x| > STRAFE_THRESHOLD (+0xf0 = 1.5 + 2r), only when `strafeStateTimer` (+0x120) has run
  out; each flip sets it to 1 s. `canStrafe` (+0x126) is set to 1 at movement setup and never cleared (by the writes found); without it a strafing
  fighter would face its velocity. The steering also seems to keep only the velocity's side part while strafing and its forward part otherwise
  (decompile unclear; Meitou does not).
- **Animation.** Forward speed = z, side speed = −x (to the right) only while strafing, else 0; x is zeroed when |z| > 1 and |x| < 0.5. The side-step
  clip gets weight |side| / (|side| + |forward|) (the last value while the sum is ≤ 1, through a filter `@ AC+0x2b8`, times the movement layer's weight), the forward movement clips
  the rest; it plays at F × `play speed` × side speed (signed). Base data: `strafe lower` (layer lower, no `move speed`, rebirth.mod `play speed`
  −0.12, `strafe speed max` 999, combat only); `sidestep` is disabled and `strafe upper` removed. `strafe speed max` (999 on the blows, 99 on the guards, 0 on the walks) is only tested
  for > 0 when the lists are built (`FUN_1405bbd40`, **Observed**): such a clip goes to the strafe list instead of the by-name action list, and the value is never a speed.
  Only the base/animal class reads the strafe list (its first entry is the side step in a fight); humans fetch `strafe lower` by name. An animal's `strafe anim`
  is its side step only when none of its `animations` has `strafe speed max` > 0.
- Meitou: `AnimationSystem.Strafe` and `AnimationLibrary.Strafe`; a fighter in its footwork (`CharacterCold.FaceSlot`) keeps facing its
  target while its path moves it (`MovementSystem`, the turn's speed cut skipped); the share is 0 below a total speed of 1 (standing; engine reading of the weight).

### Idle stance

Verified (decompilation). A human's animation object keeps its own idle: the `idle stance` of its appearance looked up by record name
(`@ 1405bd240` at creation, `@ 1405b9880` after an appearance change). Its idle pick (`AnimationClassHuman` slot 25, `@ 14051f230`;
animals' slot 25 is the plain draw) returns that stance when the character's category is normal (0), the record's `is combat mode`
and `stealth mode` are EITHER or equal to the character's, and the arm check `@ 14051d240` passes (a whole-body stance is index 1, so it is checked: a character with a weapon in hand does not
stand in the stances of the empty-handed, all `has weapon L/R` 0, but draws `stand 1 sword`); otherwise it draws from the idle list (`@ 14051ec80`, `@ 140523450`). The standing update
`@ 14051d3b0` re-picks when the timer (`idle time` seconds) runs out, so a fitting stance is held. Weapon kinds are not tested.
Base data (**Verified** by probe): the 8 stances are layer `all`, combat and stealth mode 0, `play speed` 0 (`squat` 0.01), clips 0.033 s:
characters stand still in their stance out of combat and not sneaking. Meitou: `AnimationDefinition.FitsAsStance`,
`AnimationSystem.OwnStance`; a clip that never advances starts at its first frame (engine choice; the original's start time is **Unknown**).

### In a bed or a cage

**Observed** (decompilation of the out-of-combat pick `@ 140520500`; `Character+0x2f8` 1 bed, 2 imprisoned, [carry-loot](game/carry-loot.md)). In a bed with no action running the animation plays the
bed's functionality `animation` clip (field `+0x408`, `sleepinbed` in the base data) at weight 1 and a layer rate of exactly 0.1 (`@ 14033a1b0` hands the rate to the layer as is, it is not multiplied by the
record's `play speed`); an action in progress (`gotobed`) runs by the normal rules. In a cage the functionality supplies three clips, entry 0 of each list (`UseableStuff::UseableStuff` `FUN_1402a0900`, **Verified**): `+0x408` `animation`
(the Cage: `stand 1`), `+0x410` `animation KO` (`foetal`), `+0x418` `animation dazed` (`sitting dazed`). The pick: when character vfunc `0x30` (unconscious) is true, the KO clip at
rate 1.0; else `Character+0x51c` (`MedicalSystem::dazedOrAlert`, KenshiLib name **Observed**) at or under 0.05, or a leg-collapse state, gives `+0x418` at rate 0.025, at or under 0.2 `+0x418` at 0.5, else `+0x408` at 0.5; a running `stand 1` action is dropped first.
Meitou: a sleeper plays `CharacterCold.OperateClip` as a held clip at its `play speed`; a prisoner (`CharacterCold.CageClips`) plays the KO clip while down, the dazed clip
with a leg down (engine reading of dazedOrAlert), else the idle clip, at the records' play speeds.

### Clips that stop movement

**Verified** (`AnimationClass::isCurrentlyIncapableOfWaypointMovemode` `@ 14051c870`, from `CharMovement::update`): while the current action clip has ANIMATION `disables movement`
(`AnimationData+0x89`; 11 base clips, **Verified** by a probe of the base data: the hit reactions `back blow high/light/low`, `mid blow`, `mid blow drop`, `mid blow light`,
`standing up 3`, `bowing`, `reload 1/2/3 phase`) the character cannot move along waypoints. Meitou: any wanted clip with the flag sets `CharacterAnimation.HoldsStill`, and
`MovementSystem` then stops a character with a path, keeping it, except in the Attack task (as a stagger in a fight, whose clip moves the character itself). `normalise` (layer flag `+0x5f`) is read by the original, its effect **Unknown**; not used.

## Shape notation and factors

**Verified formula body** from14052e6c0; stat-field identity matching remains **Observed**.
Sliders*0.01; lerp(a,t)=1+(a-1)*t, vector multiplication component-wise, bone-local XYZ.
M=(max(strength,weapon-smith,armour-smith)/100-0.2)/0.79, unclamped. Starvation
S=clamp((1-clamp(N-1,0,1))*1.5,0,1); original N identity was unresolved in this trace.
thin7/86/6/4=lerp(0.7/0.86/0.6/0.4,S); bulk=lerp(1.27 men/1.24 women,M),
broad=lerp(1.13 men/1.12 women,M). h=Height*g, Fr=Frame*g, L=Leg-length, H=h+L-1;
human g1, animal override unverified. LB=Legs-bulk*thin7*bulk, St=Stomach*thin6,
C=Chest*broad*thin86, c45=1+0.45*(C-1), c9=1+0.9*(C-1), A=Arm-bulk*thin7*bulk,
Sh=Shoulders*broad, F=Feet*h. Table retains every bone/positional vector and remaining local symbols.
Neutral vectors1 except thighY0.95 (CharacterShapeTests).

Limb factor0 only for a stump (limb state1; a prosthetic keeps1), else1; indices0 left-arm,1 right,2 left-leg,3 right-leg. Upper-arm
stump remains unless hide-stump bits1/2 remove it. Thigh n0.8 when that leg is a stump (limb state 1; **Observed**).
Calf k uses the signed leg ratio (`@ read_boot_height_n7`, **Observed**): b = worn boots' `boot height`, L/R = the `offset` of a fitted
left/right leg LIMB_REPLACEMENT (slot 52/53). Left only: (b+4)/(L+4); right only: x=R, y=b; both: x=R, y=L, then x<y gives
(x+4)/(y+4), else -(y+4)/(x+4), equal 0. Positive scales the left calf, negative the right, 0 neither (`CharacterShape.LegRatioOf`). Movement translation scaleH unless two unidentified states.

**Feet lift** (**Verified**, decompiled): the same pass (`FUN_140538630`) stores at the character's +0x2cc the lower leg `offset` when both leg slots (52, 53) hold
a LIMB_REPLACEMENT, else the worn boots' `boot height` (0 without), and `FUN_1405f16a0` places the body mesh's node at Y = +0x2cc − +0x2c8, so the body is
raised by it. Meitou subtracts it from the ground offset (`CharacterShape.FeetLiftOf`, `CharacterRigs`), so the ragdoll's placement takes it too. Base game: no
boots have a `boot height`; two Scout legs lift 0.5, two Stealth legs -0.2. Mods use it for heels (issue #126; **Observed**: the Animation Collection Pack's
2B Boots, `boot height` 1 on the female sandal mesh, a geta whose teeth sank into the ground before).
Muscle normal blends, head poses and skinning follow their own data/shader rules, not bone scale.

**Engine choice** GroundOffset compensates lowest foot/toe bind-pose height change per appearance;
it does not re-ground crouches or prove the original controller's mechanism. It is measured with every limb (the sliders only): a lost leg's calf and foot scale to 0, so with
both legs lost the lowest foot was the knee and the lowering sank a crawler into the ground and a carried body down its carrier's back (**Observed**, sandbox screenshots). Physically moving
characters/root collision and leg-health/lying/care selection belong in [simulation](simulation.md#animation).

### Stat-driven body

**Verified** (decompilation): `FUN_14052bc70` sets three values on the human appearance (+0x1a0..+0x1a8) from the character's `CharStats`
(offsets: [character-stats](game/character-stats.md#the-stat-list)) and hunger (`+0x4b8`, [ui-screens](game/ui-screens.md)):

- **Bulk M** = map(max(strength, weapon smith, armour smith) × 0.01, 0.2 → 0, 0.99 → 1), not clamped. Strength is read through the stats object's slot-2
  accessor; Meitou takes the stat itself.
- **Definition D** = map((swimming + athletics + 3 × max(dexterity × x, martial arts) − cooking − science) × 0.01 / 5, 0.2 → 0, 0.9 men / 0.99 women → 1),
  clamped 0..1. x is the float at `CharStats +0x34` (**Unknown**; Meitou 1).
- **Starvation S** = clamp((1 − clamp(hunger − 1, 0, 1)) × 1.5, 0, 1).

`FUN_140531820` (not for robot races) then sets the body material's `normalMap` and `bodyBlendNormal` and its `muscleBlend` (`character.hlsl`:
`bodyN = lerp(bodyN, blendNormalMap, muscleBlend)`, before the head's normal is added), and, for a character with stats, rebuilds the bone shape
(`FUN_14052e6c0`, M and S above). The maps: base `nm <g>` blended to `nm <g> strong` by D; when S > 0.25 or S > D, blended to `nm <g> skinny`
by S, the base being `nm <g> strong` when D > 0.33 (RACE fields; humans and Shek have their own `_muscular` / `_skinny` files, robots and hive
one file for all). What calls it, and how often, is **Unknown** (it is a virtual of `AppearanceHuman`, slots 6 and 13).
Meitou (`CharacterShape.BuildFromStats`, `CharacterSnapshot.Build`, `CharacterRenderer.ApplyBuild`): published every tick from the live stats,
applied when a value moves by more than 0.01, the normal maps once both are resident (engine choices).

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

**Observed** (`OgreSkeletonReader` inspection of `male_skeleton.skeleton` and `female_skeleton.skeleton`):
`postures` lasts about 0.166672 s; its 0.0333405 s key is neutral, corresponding to slider 20.
The renderer samples at `clip length × slider / 100`; generated NPC posture follows the
[appearance template](characters.md#random-appearance), rather than the editor range midpoint.

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

- The order the game's `qsort` leaves ANIMATION_FILE records created by the same file in ([Animation files](#animation-files));
  it decides only clips two such files both define (3 names in a large modded install).
- Which COMBAT_TECHNIQUE `animal` values exist (block clones: `<anim name> override`, [combat](game/combat.md#hit-reactions)).
- Which manager each layer is added to (the lower/upper/overlay mapping in [Layer managers](#layer-managers) is inferred), what managers 3 and 4 hold, the play rate of a synched clip, and the `+0x5c` flag.
- ~~Which record field turns on root-motion extraction~~: `relocates` for ANIMATION records; combat techniques
  by their own rule ([Root motion](#clips-and-layers)). Still open: the time divisor of the root-motion velocity,
  and how combat footwork moves the body while combat sets animation-driven movement.
- Per-bone call in `@ 140539020`: Kenshi calls one virtual method on every bone with false, and on the
  children of `Bip01 Head` except `Bip01 Jaw` with true. What it is (scale inheritance? manual control?) is
  Unknown.
- How characters choose between candidate animations: largely answered ([Choosing candidates](#choosing-candidates--14051d660),
  [Movement](#clips-and-layers), [Hit reactions](#clips-and-layers)). Still open: the
  non-combat lower-body update (slot 28 runs only in combat; the out-of-combat path, probably the same
  movement blend, was not traced), how the idle timer's units work,
  the get-up clip's playback rate, how fast the movement direction turns, and which of `foetal` /
  `sitting dazed` / `knockout training` play when. Our engine's rules are in [simulation.md](simulation.md#animation).
- Body shape: the two
  movement-scale exceptions, the stats field x in the muscle definition, what reads (`Height` − 80) × 0.025,
  and what the game does when a body file lacks a slider (the viewer uses 100).

## Expression reference

- Selection data used by the simulation: `clip seconds per second = speed x play speed`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Layer managers (`AnimationClass` vtable slot 6, `@ 1405b68c0`)
- Choosing candidates (`@ 14051d660`)
- Combat footwork and strafing (`AnimationClassHuman` slots 28 and 29, `@ 14051fc60`, `@ 14051de70`)
- The formulas (Verified, decompilation and disassembly of `@ 14052e6c0`; implemented in `CharacterShape`)
- Muscle definition and the body's normal maps (Verified, `@ 14052bc70`, `@ 140531820`)
