# Combat

Original research: CharStats/CombatClass/MedicalSystem/ranged controller, FCS records and binary
constants. Tables/formulas/evidence scopes retained below; [stats](character-stats.md) owns medical
state/XP, [animation](../animation.md) owns layers/root motion. Implementation: Gameplay.Combat,
Simulation.Combat and Animation; approximate engine scheduling is not original parity.

## Weapons and damage

**Verified** (packet `FUN_1408868b0` from `FUN_140883ba0`/`FUN_140883c60`/`FUN_140883d10`, disassembled; item stats `FUN_140897f30`, `FUN_140889cd0`;
`FUN_14060a2f0` scales it by the impact point's `power`). Inputs: skill = the raw stat of the weapon's category (vslot 0x18: no injury multiplier, no gear
factor); dex = `FUN_1406457e0` multiplier (hunger, limbs, limb items incl. the gear's `dexterity mult`, no light/dust) x stat x read-time scale m; str = the same
multiplier x stat x m. cut=lerp((skill+dex)*cutMult/200,cutBase,cut99); blunt=lerp((0.5*skill+1.5*str)*bluntMult/200,bluntBase,blunt99), both unclamped;
pierce=0.78*(skill+dex)*pierceMult+10*pierceMult (no /200). Stored cut/blunt endpoints 13/52; cutBase 13 and bluntBase 0, but a weapon whose cut multiplier is
below its blunt one (club, mace) has bluntBase 13 and cutBase 0.4*13. The weapon item's `min cut damage` (mult x manufacturer, item +0x248) is read by **no**
hit code (whole-.text scan of reads at +0x248: only the armour and UI users): there is no cut floor. No random spread anywhere in the packet. The packet
(cut, blunt, pierce, stun 0) is then x the weapon's `human`/`animal`/`robot damage mult` (target robot race flag first, then the animal slot, then the human slot; 1 for a non-character
target), x its `race damage` percent x 0.01 for the target's race, and, last, x the wearer's gear `damage output mult` (the whole packet, only when it is above 0 and not 1;
the weapon skill itself is not scaled by it). Bleed multiplier (item `bleed mult`) and `armour penetration` ride along unscaled.
Quality q = item level x 0.01 (`FUN_1400d3090`).

Unarmed (stat-set category 5, the FISTS item for a person): rating R = lerp(e,0.2,1) x (martial arts raw + (int)(`unarmed bonus` with the [indoors](#indoors) modifier - dust penalty)) x slot 0x398
(`FUN_140884e00` with its flag set, e the encumbrance factor; slot 0x398 is 1 for a person, `FUN_1405e5990`). Every combat caller sets the flag (the packet, the attack rating
`FUN_140884ee0` sets `dl` = 1 before its jump, the defence rating `FUN_140886290` goes through it), so an unarmed fighter's attack, defence and damage rating are this one number (**Verified**, disassembly);
total = max(1, lerp(R/100,0.25,1) x (`unarmed damage mult` x toughness x scale + str + 10)) with no halving of any term; blunt share lerp(R/100,1,0.4), the rest cut; no pierce.
The kind/race/general multipliers, bleed and penetration are the FISTS item's. Stumble threshold toughness*m*0.52 plus state eligibility.

## Techniques, block and dodge

Technique direction/category/weapon/state/reach/animation flags gate selection. The attack rating (`FUN_140884ee0`) and the
defence ratings (`FUN_140884f50`, the block contest `FUN_1408862d0`) read the trained melee attack / defence (CharStats `+0x120` / `+0x124`) with
no injury or hunger multiplier, plus the gear and weapon bonuses, the [indoors](#indoors) modifier, less the dust penalty (attack), +20 guarding
(defence, when the caller's flag is set: not for the dialogue combat rating `FUN_140852a30`); with fists both are the fist rating (**Verified**, disassembly;
Meitou `CombatSystem.AttackRating` / `DefenceRating`). The attack and block play rates (`FUN_1408866b0` → `FUN_140883a80`) take those ratings and are x 0.8
while swimming (water state 3). Only the unarmed dodge (`FUN_140885190`) has an injury (leg) term. **Observed** outcome: block5 if matching block
past50% animation; dodge0 within progress0.1..0.98; otherwise hit2. Skill roll r1*A<r2*B,
A=1+0.05*max(0,s2-s1), B=1+0.05*max(0,s1-s2). Derived chance with k=B/A is1-1/(2k) ifk>=1,
elsek/2. General skill blend0.01*(0.6*stat+0.4*dexterity*m) feeds dodge/reload, with stat/gear
and injury variants.

### Indoors

**Verified** (loader `FUN_140888ca0`, record reader `FUN_1406684c0`): techniques not `disabled` go to an attack
list unless `is block` or `is dodge` (then the defence list), in data order. Each keeps a per-category table:
`katanas` 0, `sabre` 1, `blunt` 2, `heavy weapons` 3, `hackers` 4, `unarmed` 5, `bow` 6, `polearm` 8; with
`animal` > 8 only that category. `1 handed` is not read here (only by `FUN_1405ba280`). `power N` is stored ×0.01.

### Attack technique choice

- Gap = |aim − attacker| − 1.5·r·k − lerp(k, 0.45, 1)·r_t, at least 0. r, r_t: `pathfind footprint radius`; k: slot
  0x390 (1 for people). Aim: target + velocity × 1.2 s when its movement is plain mode 0, else its position.
- Pass: category = weapon's (5 for fists), skill = stored category level. If empty and not an animal: category 5 at
  `FUN_140884e00` = lerp(e, 0.2, 1) × (martial arts + CharStats +0x2c bonus − injury term) × slot 0x398. If still The injury term is the dust-storm attack penalty `FUN_140643a30` (int(`+0x14c` x K) while `+0x148` is 1); the attack chooser's unarmed passes (`FUN_140888030`:69, :78) ask with the flag set too, so its unarmed skill loses it (Meitou `CombatSystem.Act` `UnarmedSkill`).
  empty: that again, accepting hurt limbs. e = encumbrance factor (CharStats +0x190, 1 unburdened).
- Filter: category table; `min skill` ≤ skill ≤ `max skill`; (1 − e)·100 ≤ `max encumbrance`; `is prone` = posture ≠ 0 (always, **Verified** in the disassembly of
  `FUN_1408876d0`: the decompile prints the burden test and the prone test as `<` or `==` and prone, but the code rejects on burden above the maximum, then on a prone mismatch);
  limb factor f > 0, and f ≥ 1 except in the last pass.
- The reader `FUN_1406684c0` stores one 0x14-byte blow (direction, power, blocked frame, limb, stop frame) per index 1..`num techniques` with no upper bound (**Verified**); 0 stores none, so f is 0 and the technique is never chosen (Meitou: `CombatTechnique.Blows`, `MoreBlows`). An absent field reads 0 too (**Observed**, static reading: the reader takes it with the int table's `operator[]`, `FUN_14006cf30`): a partial record, made from a change whose base is not loaded or again after REMOVED, is never an attack. Base data and More Combat Animation + Military Craft have no such record; Great Anims after Military Craft leaves 3 (`Cut left`, `Downward combo`, `heavy swing`: removed by one, changed by the other).
- f = product over blows. `limb` 4 (weapon) 1; 0 left arm, 1 right arm: 0.001 if not working (+0x5be / +0x5bd).
  Legs 2/3: prone → technique out; flag +0x5b8 or a leg part down → ×0.25. A missing limb
  (`MedicalSystem::getLimbState` `FUN_140643b30` = 1 STUMP, **Verified**; 0 original, 2 replaced, 3 crushed) → 0.001. No blow above 0.01 → 0.
- Reach R = `attack distance` + L/2, L = weapon `length` (CharStats +0x200). The max R of
  filtered techniques, at least 1, is CharStats +0x150. A technique needs gap ≤ R.
- Weight: c = min(a, b)/max(a, b), a = gap + 3, b = k·(`attack distance` + L/4) + 3. Multiply by f, by 0.02 under
  `attack distance min vs static`, by 0.2 for the previous technique unless the target is in state 8, and by `chance`.
  Pick by cumulative weight (`FUN_1408a8c80`, `FUN_1409b1c80`): a map keyed by the running total, a draw of `rand()`/32768 × total, the
  first key above it; weights of 0 or less are left out (**Verified**, decompile).
- The category (CharStats +0x1f0) is the bound weapon's `skill category` (weapon +0x234, `FUN_140897f30`, `FUN_140898ae0`), not its
  `skill category animation override` (+0x238): a cleaver takes the hackers' techniques (**Verified**).
- The clip is the technique's `anim name` plus a suffix (none, or " override" for a defence started over a running one), found by name
  (`FUN_1405b7800`, `FUN_1400f4aa0`; the lookups `FUN_1405c5660` and `FUN_1405b5270` take the name alone and draw nothing): no second
  choice by weapon or chance (**Verified**).

Consequences (base data): "Cut left static" (−999) is never an attack. Fists reach `attack distance` + 9, so
"ma badpunch" (−4, the only punch up to martial arts 20) needs a gap of 5 or less. An armed fighter beyond every weapon reach may kick or punch.
With L = 0 the attack zone (below) falls 2r short of the start gap, and a fist swing begun where the steering parks
the fighter hits nobody (**Measured**, Meitou headless, base data: martial arts 100 against 5 Dust Bandits, 5 of 39
swings landed with L = 0; with L = 18, 7 hits from 6 swings put all 5 down in 7.4 s).

## Hit reactions

**Verified** (`FUN_1404391f0` from `FUN_1404394f0` on a hit): damage = cut+blunt+pierce+stun against the
stumble threshold. A stumble clip is picked unless the hit is light and one is already playing; it is big
only when heavy and none was playing; front/rear by a dot test (`FUN_140435600`). Humanoids only. "Heavy_Hit"
/ "Light_Hit" are only the Wwise "Impacts" switch (Heavy = a big clip). A picked clip cancels the defender's
own technique (its blow never lands) and holds it in the stumble state until the clip ends (`FUN_140665490`,
`FUN_14060cf60`, `FUN_140435570`); with no clip the technique is still cancelled. The break-out timer (`+0x124`, 1.2 s from the stumble's start, `FUN_14060cf60`)
counts down each tick; once due, the first tick a swing at 10-99% progress is incoming (`+0x120`, the incoming list is non-empty) rolls the stumble break-out (`FUN_1406085e0`, chance `FUN_140885d80`);
a failed roll sets the timer to 1.2 s again, a tick with no such swing leaves it due (**Verified**, decompile). Meitou: `CombatSlot.BreakOutTick`.
A won roll plans a defence (+0x1f4 = 1), leaves the stumble for state 3 and clears +0x14c; state 3 (`FUN_14060d0b0`) starts it through `FUN_140609290`, whose chooser, the fighter still
stumbling (`FUN_140435570`), takes only `is stumble dodge` rows; with none it gets no technique and 1.2 s, but the stumble state has ended either way (**Verified**).
The stumble clip is faded out, not cut, either way (**Verified**, decompile and base data): `FUN_140609290` (called with 0 from state 3) first moves every layer of manager 1 flagged +0x5c
to the fading list (`FUN_1405b36f0`), and a stumble dodge then clears manager 1 too (`FUN_1405b7800` -> `FUN_1405b35c0`). +0x5c is the ANIMATION record's `is action` or `layer` "all"
(`FUN_1405ba280`; `layer` "all" and any name but lower / overlay / two others puts the layer in manager 1), and every stumble record of the base game is `layer all`, `is action`. The fade
needs a swing still incoming on that tick (the start's `FUN_140665e00` pick); without one the clip plays on (**Observed**). Meitou: `Act`, step 2 (`StumbleClip` dropped only with such a
swing), `AnimationSystem` (a stumble action whose `StumbleClip` is gone fades out; a fading one-shot layer then freezes, [animation](../animation.md#layer-managers)).
Knockback is the clips' root motion only (**Observed**: no impulse in these functions; [animation](../animation.md); ragdolls: [ragdolls](ragdolls.md)).
A blocked blow neither staggers nor pushes (`FUN_140665540`). A shackled defender is knocked out instead 25% of the time; a defender whose fight is off: [stagger outside a fight](#fight-lifecycle-and-orders).
**Verified** (`FUN_140520490` → `FUN_14051eab0`; `FUN_140435570` → `FUN_1405b3a40`): the clip plays at its record's play speed
(no stat or CONSTANTS factor) and the stumble state (8) lasts until the clip is past 0.98 of its length (`DAT_14168db34`), so
`mid blow` holds about 4.5 s; nothing else times it. The clip pick weighs a record ×0.25 when it is the one playing (`FUN_14051f420`).
Blocks: the defence update (`FUN_140609c70`) plays `<anim name> override` (the clone with right-arm override bones, `FUN_14086dfe0`)
only when asked to; the defending state (`FUN_14060ce30`) and the stumble break-out both ask for the plain block (lower body deleted).

## Pacing

**Verified** (`re_spec` decompilation of the fight tick `14060d6a0`): there is no wait before the first swing; a fight decides on its first tick.
Defence pools (`FUN_140887970`): the table is the fighter's weapon category (CharStats +0x1f0), **forced to 5 (fists) when the right arm does not work**
(the byte at `*(CharStats+8)+0x165`: CharStats +8 is the medical object, +0x165 its right-arm-ok byte, **Verified**: the same byte `FUN_1408c5f40`, `FUN_140885eb0`
and the arm gate `FUN_1409991d0` read, and `FUN_140649320` clears it for a `collapse part` bit 2 part that is a stump, crushed or down; the left arm, +0x166, is not read here).
An armed fighter only ever blocks (block rows need `is dodge` 0, so the "Dodge back" rows flagged as blocks
are not blocks); a fighter on the fists table dodges on its dodge-chance roll (and then finds no block row: every unarmed block row in the base and the common mods is `is dodge` or disabled, **Verified**
from the COMBAT_TECHNIQUE rows), so **a character whose right arm is out cannot block, only dodge**, and both arms out is the same;
a left arm out alone keeps the hip weapon and its blocks, but a weapon in the back section is not drawn then ([Arms](#arms)).
Against a fist blow (the attacker not an animal, its CharStats +0x1f0 5) the block chance is x min(max((the defender's raw martial arts - 0.75) / 0.35, 0.5), 1.1)
before the roll: 1.1 from level 2, 0.71 at 1 (`FUN_140887970`, **Verified** disassembly; the dodge chance is not scaled). Meitou: `DefenceFormulas.FistBlockFactor`. Meitou: `Fighter.Weapon` (null without the right arm) gives `TechniqueCategory` 5 and `CanBlock` false;
a stumbling one breaks out only into the `is stumble dodge`
a stumbling one breaks out only into the `is stumble dodge`
rows, after a roll of `FUN_140885d80` against the strongest attacker whose swing is at 10-99 % (`FUN_1406085e0`). A blow from behind is met as
direction 7 (from 1) or 9 (from 2/3) (`FUN_1404356c0`). A target playing a hit reaction grants 1 attack slot (`FUN_1406661d0`).
**Verified**: after an attack the fighter waits 0.6 s after a hit, 0.3 s blocked, 0.2 s dodged
(`FUN_14060a2f0`, `FUN_14060d6a0`; flat, no stats). The wait starts at the later of two points: the clip past 0.98 (`FUN_14060a590` arms +0x144 once
`FUN_1405b4c00` calls the clip done; the 0.98 is read from the binary) and the clip's end marker, the technique's `acceptable end time` (`FUN_1405b34b0`: a clip is
busy while its progress is under the marker, and the wait counts only while none is busy; a record that stores 0, as most mods' do, is never busy). While +0x144 is set the
fighter is forced into state 6 every tick (`FUN_14060d6a0`, the pause block before the decide step), so it neither decides nor starts a defence: a blow at it in its wait is
met with no block or dodge (state 6's "attacked" test, `FUN_1402b3800`, sets state 4 and the pause block puts it back). Only a hit reaction (`FUN_14060cf60` clears +0x144)
or the wait's end releases it; a swing stopped at a stop frame (attacked, `FUN_14060a590`) decides again at once instead. Meitou: `CombatSlot.PauseUntil`, `CombatTuning.SwingFinished`.
**Verified** (`FUN_14060d6a0`, technique +0x40 = `acceptable end time` / `num frames` from `FUN_1406684c0`): a target that goes down, dies or is caged mid-swing is dropped
only once the clip's own progress reaches that marker; then the clip fades (`FUN_1405b3c60`), the fighter re-decides on the same tick and the chooser, its opponent pointer
just cleared, picks a new target at once. The drop comes before 0.98 arms +0x144, so a kill has no post-swing wait: the clip's tail is the pause, and a later blow of the
combo still resolves against the zone (the swing update runs on in state 0). Base martial-arts tails after the last blow (frames): Double strike 31→40 of 57, Double punch 23→33 of 52, kicklow 22→35 of 40,
shotei 18→36 of 45, badpunch 24→38 of 45, mega kick 32→54 of 63, jump knee 40→75 of 80; a row with marker 0 drops at once. Meitou: `CombatSystem.FinishingSwing` (before, the
swing was cut on the killing blow's tick and the next one started at once). Before swinging, an attacker in reach asks its target
(`FUN_14060c670` → `FUN_140666a30`): target swinging at it → the attacker blocks; target stumbling or blocking →
attack; otherwise an initiative roll on both attack skills (`FUN_140665370`, the skill roll above). If the asker wins,
the target blocks. If the target wins, the asker is refused; with no free slot it is refused too, and waits
(see [Attack slots and circling](#attack-slots-and-circling)). `max simultaneous hits` caps one blow's targets ([attack zone](#attack-zone));
`power N` multiplies hit N's packet; `limb N` picks the punch/kick sound.

**Verified** (decompile; the waits are `deadTimer` +0x148): there is no wait before the first swing (`startupState`, state 3, enters the
planned state at once). Deciding sets the wait to 0.5 s before it asks; the impact check overwrites it by outcome, so a swing that finds
nobody in its [attack zone](#attack-zone) waits 0.5 s, and the loser of the initiative roll (plan 1) decides again after at most 0.5 s.

### Attack zone

**Verified** (`calculateTargetsInAttackZone` `FUN_1406087a0`, `attackImpactCheck` `FUN_14060a2f0`; field names KenshiLib). At a blow's impact
frame the blow resolves against every character in a box ahead of the attacker, at most the technique's `max simultaneous hits` (+0x54), in
the list's order; each outcome sets the wait (the last wins) and each one's own block or dodge decides it (`Character::hitByMeleeAttack`,
HitMaterialType: `HIT_MISSED` dodged 0.2 s, `HIT_SWORD` blocked 0.3 s, flesh or metal hit 0.6 s).

- **Box**: heading f = the movement direction (`AbstractMovementBase` +0xd0, horizontal); k = the size (Character vfunc 0x390), s =
  lerp(k, 0.45, 1), r = `myRadiusX` (+0x1a0, the race's `pathfind footprint radius`), L = the weapon's length (CharStats +0x200). Centre
  (L/4 + r + max(0, attack distance − |pos − attack start|)) s ahead; half-extents (width, height, depth) (r + 0.5, 25, L/2) s, or
  (13, 8, L/2) s when the impact point's direction is `CUT_LEFT` or `CUT_RIGHT` (a horizontal cut sweeps wide and low). Then
  h = max(0, (offset − `getRadius`) / 2), `getRadius` = k × footprint: the centre moves back by h and the depth grows by h. The height
  follows the ground (a down ray, clamped to ±15 of the feet, +9). Physics overlap query, at most 20 bodies.
- Per target the attacker frees that one's attack slot (`AttackSlotManager::freeSlotH`), and after a hit or block takes it off its own engaged
  list (`removeAttackerH`, [attack slots](#attack-slots-and-circling)).

**Verified** (disassembly of `FUN_1406087a0`): "attack start" is `CombatClass` +0x15c, written at each attack's start by `FUN_1402b4570` (the fight start's write is only the initial
value), so `moved` is the 3D distance from where this swing began, in any direction: the attacker's own drift, backing or circling counts, not just a lunge. The clamp is max(−1, attack distance − moved)
(the constant at `0x141684fcc` is −1.0, not 0). The list is built at the attack start and again at each blow frame (`FUN_14060a590`); the one-shot flag +0x1d4 is cleared every fight tick (`FUN_14060ce30`),
so each blow resolves against the heading and positions of its own impact tick. The start gap and the zone agree for a body that has not moved: the zone's far end is (0.75 L + r + attack distance) s,
the start allows gap ≤ attack distance + L/2, i.e. a centre distance of attack distance + L/2 + 1.5 r k + r_t, which is under it when L ≥ 2r. A swing whose attacker has drifted `moved` ≥ attack
distance loses the term and reaches (0.75 L + r) s only. See [Facing and movement in a swing](#facing-and-movement-in-a-swing).

Meitou (`CombatSystem.Strike`): the box above in 2D, a body counting when its size-scaled footprint circle meets it and its feet are within the
half-height + 9 of the attacker's; the order is the target first, then by distance (the physics order is arbitrary). Hits left over go to walls
(the nearest edge of the wall line within the half-width) and doors (the leaf widened by its half-width), nearest first (`ApplyZoneBuildings`);
a door takes it off its own progress as a bash does ([locks](locks.md#cutting-and-breaking-285--185-cut-286--186-break)). A defender that is not the attacker's target defends only with a block or dodge it is already playing.

**Verified** (`FUN_14060a590`, `FUN_14060a2f0`): the zone and the impact check run inside the swing update itself, on the tick the blow index (+0x268) passes the previous tick's (+0x26c), and the hits apply at once. A blow therefore lands even when that same tick also ends the swing (a stop frame, a blocked frame at or past the end) or starts the next one. Meitou (`CombatSystem.Apply`): the blow effect carries its technique; only a hit, knock-out or kill applied earlier on that tick (`CombatSlot.CancelTick`) takes the swing from its attacker, and the attacker's swing state (stop, last outcome) is touched only while the effect's swing is still current; one that ended on that tick gets its pause from this outcome. Before, such blows were dropped (about 1 to 4 per cent of blows in battles of Shek vs bandits, more in the duel scenarios); measured in `--battle` runs.

### Facing and movement in a swing

**Verified** (`FUN_14060a590` the swing update, `FUN_1402b4570` the attack start, `FUN_14065ffa0` the movement update, `FUN_14065e240`, `FUN_140665250`, `FUN_1406084e0`; constants read from the binary).
Whether a swing connects is settled by these four rules, none of which the engine had before:

- **No facing test to start**: the start (`FUN_1402b4570`) tests only the gap (above); it never checks the angle to the target.
- **A swing has no steering.** The attack start and every swing tick set the movement's animation-driven flag (+0x37c = 1, `FUN_14065e240`); in the movement update (`FUN_14065ffa0`) that selects the
  branch that moves the body only by the last animation root-motion displacement (+0x380, written by the animation update `FUN_1405b68c0` from the relocating clips) divided by the frame time. The fight
  steering (bands, repulsion, footwork speed) does not run until a state with its own update clears the flag again (`FUN_1402ae900` and the states 5 and 6 updates do, `FUN_14060cf60`/`FUN_14060d6a0` too).
  `AttackState` has no update; the band the older notes gave a swing is `AttackState.Enter`'s walk when no technique is in reach. So during a swing the attacker neither closes in nor drifts: it moves
  by the clip's root motion (the technique's `Bip01` track) and the push of the bodies round it.
- **The heading is set at once, not turned.** `FUN_140665250` (CharMovement vfunc 0x30) writes the normalised direction into +0xd0; vfunc 0x38 (`FUN_14065dc90`) points it at a position. The attack start
  calls vfunc 0x38 with the target's place when it is over 5 away (`FUN_140666400`; the constant at `0x1416ad4b0` is 25 = 5²). Each swing tick, while no blow has passed (+0x268 = 0), the swing's progress (+0x140)
  is under X and the aim point (target's place, plus its velocity × 1.2 s when the target's movement mode +0x378 is 0, i.e. not in fight steering) is over 8 away (`0x1416ad81c`), it sets the movement's
  look-at to the target and faces the aim point (over 5; always when over 8). X = `FUN_1406084e0` = max(0.6 (20 − D) / 20, max(0.1, (4 − n) / 3)); D = the target's defence rating (`FUN_140886290`: the fist
  attack rating for fists, else melee defence + weapon block bonus + the +0x124 term + 20 when guarding) less the attacker's attack rating (`FUN_140884ee0`); n = the target's CombatClass +0x200 + its
  overflow counter (the engaged list's size, [slots](#attack-slots-and-circling)); constants 0.6 (double `0x14168a248`), 20 (`0x141683188`), 4, 0.1 (`0x14167fad0`). One attacker alone: X = 1, so the heading
  follows the target through the whole windup; at three, X = 1/3 at equal skill. After that the heading stays where it was for the rest of the swing (the later blows, the zone is computed
  with that heading).
- **The zone uses the heading at the blow's impact tick** (+0xd0, a logical value; the drawn node turns at ≤ 4 rad/s in the stance, [animation](../animation.md#speed-the-animations-see-and-the-drawn-facing),
  so the body can look turned away while the zone is already on target).

Out of a swing, steering sets the heading to the target every tick ([Fight steering](#fight-steering)), so a fighter that is not running always starts its swing facing the target.

Meitou: `Fighting.Steer` returns false while `AttackTech >= 0` (no band, no path: the fighter stands), `CombatSystem.SwingAim` + `MovementSystem` set the heading as above, and `MovementSystem`'s steering and
path-facing branches set the heading at once (before, `Turn` at 4π rad/s, which left an attacker whose target slipped past it 100° and more off for the blow).

### Attack slots and circling

- **Engaged list** (+0x1f8, `FUN_140666e20`): an asker joins its target's list while the list holds at most 4,
  so 5 fighters at most. A refused asker adds to a two-frame overflow counter (+0x210/+0x214, swapped by
  `FUN_14060cc50`). `FUN_14060c670` joins before it checks reach. A blow that lands or is blocked takes its
  defender off the *attacker's* list (`FUN_14060a2f0` → `removeAttackerH` `FUN_1406657d0` on its own +0x1f8; not after a dodge, **Verified**). The fight task's end (`FUN_1406659b0` mode 2, **Verified**) zeroes the leaver's own list count (+0x200) and +0x228 at once; the attackers keep their target and ask again. Meitou: `Engaged` is a flag on the attacker, so a fight's end raises the leaver's `CombatSlot.ListEpoch` (`EndFight`), a fighter stores the target's epoch when it joins (`JoinedEpoch`), and `CombatSystem.Listed` counts it only while they match; the attacker drops `Engaged` in its own Act and asks again in the same step when it is free to decide.
- **Slots** (Meitou's Faithful setting passes null, so the loaded `max num attack slots` is used: `UserConfig.AttackSlotsOption`, `CombatSystem.UseAttackSlots`; `FUN_1406661d0`, **Verified**) (+0x8, `FUN_140666310`): count = clamp(`max num attack slots` + RACE `extra attack slots`, 1, 5),
  `FUN_1406661d0`. It is 1 while the target is attacking (state 0) or stumbling (its AnimationClass +0x448 has a current stumble clip +0x228; **Verified**).
  The CombatClass constructor also clamps the CONSTANTS value to 1..5.
- A slot is granted by `FUN_140666a30` and kept until the swing's last blow (`FUN_14060a590` → `FUN_140665910`).
  Hitting someone removes them from your own slots.
- **Slot timers** (`AttackSlotManager::periodicUpdate` `FUN_140668000`, **Verified**; dt from the AI update, seconds, game or real **Observed** only): each held slot's timer
  grows; past 5 s the holder keeps it only while it resolves and `isAttacking` the owner, past 20 s never. Quirk: the release always empties slot 0's hand and never resets slot i's
  timer, so a stale holder in slot 1-4 keeps its slot and evicts slot 0's holder each update. Meitou (`HasSlot`) holds a slot for a swing and stops counting a holder 20 s (`SlotHoldSeconds`) after its swing began, as the 20 s rule; the 5 s rule never acts (a swing is shorter).
- **The answer to an ask** (`FUN_140666a30`, CombatClass vtable +0x28). `FUN_14060c670` calls it on the *target's* CombatClass with the asker, the
  asker's fighting stat (`FUN_140885e40`) and its +0x14c timer; the return value is the asker's planned next state (+0x1f4): 0 attack, 1 defend,
  5 circle. **Observed**, in order: target's fight task not running (+0x130) → 0; target attacking (state 0, or deciding with plan 0) while
  `FUN_140665420` (its +0x140 value, read only when the asker's hand passes a test) is strictly between 0 and 1 → 1; no free slot
  (`FUN_140666310`) → 5; target stumbling (state 8, or deciding with plan 8) or `Character::stumbleState` `FUN_140435570` true (a stumble clip still playing) →
  **mass combat XP** to the target, take the slot again, +0x120 = 1 → 0; target without a target (+0x2a0 = 0xb) → plan 2, slot → 0 / 5;
  the target fighting someone other than the asker (its target handle +0x298 `operator!=` the asker, **Verified**) with +0x144 (`inDeadTime`) clear (+0x144 is set when the defence update `FUN_14060a190` →
  `FUN_140609c70` plays its block or dodge, and at a swing's last blow while attacked; cleared by the pause after an attack `FUN_14060d6a0`
  and the stumble; so a fighter already blocking or dodging falls through to the roll below): target defending → XP, slot → 0 / 5; target attacking → XP, then
  a random contest `FUN_140665370` of the two fighting stats (each side's factor 1 + |difference| × a constant for the lower one, two uniform
  draws; true → 5), else slot → +0x120 = 1, plan 1, → 0. Other cases: +0x240 set → target plans defence, → 0; target in Block mode
  (`CharStats::isDefensiveMode` `FUN_1402b2b50`) → 0; else the same contest: asker wins with a free slot → target plans defence, → 0; target wins → it plans to attack
  and turns on the asker (`CombatClass::setAttackTargetHandle` `FUN_140665650`, state 3 unless `FUN_1405b34b0`: an unfinished, non-looping whole-body action clip still plays, then at its next decision), → 1 when the asker is its target, else 5.
  `setAttackTargetHandle` does nothing when the asker already is its target (hand vfunc +8 is "equal"), else stores the asker's handle in +0x2a0..+0x2b0
  and notifies both characters ([being targeted](#fight-lifecycle-and-orders)). A focused fighter is turned this way too, but its next pick, at most 1 s
  later, returns the focused target.
  Meitou (`CombatSystem.MassCombatAnswer`, `MassCombatEffect`): the three XP cases once per answered ask, on the target's stat 31 (save-only, nothing reads it); "defending" is read as blows coming at the target (engine reading of state 1) and dead time as a block or dodge playing.
- **Target selection** (`CombatClassAI::chooseAttackTarget` `FUN_140666ee0`, from the deciding state; **Verified** decompile). The current target is kept
  while the pick timer (+0x2f0, s) is under 1 (0.3 on Hold). Then a focused target (CombatClassAI +0x2c8, set by orders 5 and 61,
  [lifecycle](#fight-lifecycle-and-orders)) is returned without scoring, unless its path is in the task system's impossible list
  (`AITaskSytem::isPathImpossible`) or refocus mode (+0x2e8) is set. Otherwise three lists are scored: the attackers (engaged list +0x1f8) of
  the AI task's target when the chooser senses it (any entry), the chooser's own attackers, and its threats (SensoryData +0x50). The AI task's target is `AITaskSytem` +0x10, which the
  chooser's end (`FUN_14050ca70`, from `chooseGoal` `FUN_140510ef0`, **Verified**) sets to the chosen goal's target only for goals 45 Bodyguard, 17 Protection, 87, 152 and 124 (operating
  machinery or storage), and clears otherwise: a self-defender (32) or a joiner (15, 143) has none, so only a bodyguard scores its protectee's attackers, and only it gets the +10 term below. A
  candidate must not be crippled (vfunc 0x360) and must be sensed by the chooser or by the task's target (`FUN_140855960` flag 0: an entry, the character itself, or its conversation partner).
  Each list is scored on its own (`FUN_14059e300`) and the best over the lists wins when above -100 (`DAT_1416abef4`, **Verified**); none →
  the sensed-enemy finder `FUN_14059fa70` when the task system's aggro mode (OrdersReceiver +0x30, KenshiLib `AggroType`, 0 aggressive by
  the constructor; no other writer traced) is 0, else no target. The finder (**Verified** decompile) walks the SENSE_ENEMY entries (mask 4, 0x208) that are seen or
  heard now (entry +0x38 / +0x39), not left alone (`FUN_1408569a0`), passing the Hold post test `FUN_140598630` and the area test `FUN_140599f30`
  ([perception](perception.md)) and not friendly (character vfunc 0x3f0), and returns the best by `FUN_14059e300`. So the current target is kept only as a list
  member (an attacker, a threat) or, through the finder, while it is seen or heard and tagged an enemy: one that runs out of sight on open ground is lost at the next pick (a path
  chase does not pick, below). Meitou (`CombatSystem.ChooseTarget` over `CharacterFacts.Senses`, [perception](perception.md#senses)): the lists and the finder as above (the leave-alone and
  area tests left out), the aggro mode taken as 0; a pick after the timer runs out is made while the fighter runs on a path too (engine choice).
- **Target score** (`AI::chooseBestAttackTarget` `FUN_14059e300`). Skips ragdolls (`Character::isRagdoll`), candidates the leave-alone test
  `FUN_1408569a0` passes, allies by faction (`FUN_1406b2630`, unless the candidate is a slave) and impossible-path candidates. Leave alone: for
  an NPC chooser, a candidate it does not remember as an attacker and whose faction is not an enemy, when the candidate has diplomatic status
  (`Character::isDiplomaticStatus`: NPC class Diplomat, [ui-screens](ui-screens.md#4-hud-mainbargui)) or is in a war campaign whose +0xc0 is 0; for a player chooser, a candidate not aware of it, conscious,
  of another faction, while the chooser mans no turret, whose intended target is nobody, or who has no aggression towards it, or whose target
  is not the chooser's ally (and an obedient slave leaves alone its master's allies). So player characters never pick an unaware bystander. Weights are CharStats fields that `CharStats::init` sets
  to the same constants for every character (**Verified** literals; not saved, no other writer found; names KenshiLib 1ce4a16): `STUBBOURNESS`
  +0x154 = 5, `PROFESSIONALNESS` +0x158 = 1, `HOLDING_POSITION` +0x15c = 2, `IMMEDIACY` +0x160 = 2, `IMMEDIACY_MAX_RANGE` +0x164 = 200,
  `MANLINESS` +0x168 = 1, `MURDEROUS` +0x16c = −2. Terms, in order:
  - + stubbornness (×4 when the chooser's prone state, vfunc 0x368, is not 0) for the current target; − professionalness while the candidate
    staggers (its animation's current stumble clip, AnimationClass +0x228, the one `Character::stumbleState` plays);
  - − manliness × (candidate's attackers + waiting count, less 1 when the chooser is one); a Taunt candidate gets +2 and this term ×0.35; a
    Passive one −1;
  - +10 when the candidate's intended target (state broadcast +0x68) is the chooser's AI-task target, that target is not fighting it back, and
    the candidate has under 2 attackers;
  - Range term (`AI::scoreTargetRange`) = (1 − d/R) × w by the chooser's manoeuvre mode ([area](perception.md#the-attack-on-sight-tests)):
    HOLD_GROUND R = 2 (meiDistance + tolerance) = 46, STAY_CLOSE R = meiDistance + 200 + tolerance = 223, ROAM_FAR (the default) R = 200;
    w = immediacy in ROAM_FAR, else holding position (both 2).
  - So a person prefers, in order of size: the attacker of its protectee left unopposed (+10), its current target (+5, +20 crouched or
    crawling), one already fighting (+2 over an idle one), the nearest (up to +2), the least crowded (−1 per attacker). Meitou: `Combat.TargetScore`
    for the aggro look and the fighters' re-pick, candidates and omissions in [simulation](../simulation.md#combat).
- **Deciding** (`FUN_14060c8b0`): pick a target, set +0x148 = 0.5, then ask (`FUN_14060c670`). The ask requires
  centre distance − r_t ≤ +0x150 + (1.5r + L)·k and no other body nearer than r + 2.5 (`FUN_1402e9620`). That test
  (`FlockingTools::getDistanceToClosestCharacter`, called to count non-enemies only) takes the least `range` of the flocking list, 99 when empty;
  r is the race's raw `pathfind footprint radius` (CombatClass +0x280 = r + 2.5, ctor `FUN_14060a9e0`). The list (`FUN_14085a070`) holds every
  other character not down or dead (`FUN_1407d1440`), in a bed or cage (+0x2f8) or sitting on a chair or throne (StateBroadcast +0xb8 `isSitting`, **Verified**; a sitting looker builds none) whose centre is within getRadius_self + getRadius_other + 40
  horizontally and 20 in height; `range` = the 3D centre distance, `isEnemy` = Character vfunc 1000 (**Observed**).
  If the ask fails: being attacked → defend; engaged → state 5; else state 6.
- **State 5** (`CircleStateAI`, update `FUN_1402b3180`): walk to the target until the body gap reaches max'
  (`FUN_14060cd20` = +0x150 + 1.5rk − L/4, +12 while running, minus 1.5rk; at least 6). Face the target and ask each
  tick. Once the ask passes and no other body is within 1.2 × (r + 2.5) (`FUN_1402e9620`), it re-picks its target (`FUN_140666ee0`,
  re-deciding on a new one) and, with a free slot, re-decides (**Verified**, `CircleStateAI` `FUN_1402b3180`).
  The attack start uses the same walk while no technique is in reach.
- **State 6** (`WaitingStateAI`, `FUN_1402b3760` / `FUN_1402b3800`): not engaged. Keep the body gap in
  [n/4 + 15, 3n + 25], each end minus 1.5rk. n = engaged count + last frame's refusals. Its side speed s goes to
  steering +0x114, which nothing reads ([steering](#fight-steering)). Try to join each tick; re-decide once engaged or attacked.
- **Side-step speed** (unused): drawn on entry as s = 50u − 25. If |s| is below the steering's least speed c (+0x108,
  `FUN_1402ae6d0`), s becomes ±12. c = 30 + 3b + 12u′ with b = 3 + 3u″. For a humanoid, c′ = 3(1 − e)c + 1 and
  b′ = 2(1 − e)b + 1. b′ is kept in 0..20; c′ is at most 90 and at least b′ + 10. So c′ = 11 for an unburdened
  person, 39 or more for an animal.
- Both states also start a 1.5–4.5 s timer. State 6 does not use it; state 5 uses it only in Block mode
  (CharStats +0x128, standing), clearing its own slots each time it runs out.

Meitou (`CombatSystem.HasSlot` / `MayJoin`, `Fighting.Steer`):

- **Slots**: the holders are the fighters whose swing at the target has a blow to come. On the same tick, fighters
  with lower table slots ask first (engine choice for update order). Fighters are counted within 300 units. Those
  lower asks are foreseen from the last tick's state (threads run the fighters apart), counting any that may ask
  (rejoining, a block's pause cut short by a blow at it, swing or block ending now, never asked yet; not one in its post-swing wait), so the count is never
  exceeded. **Verified** by `DuelTests` (5 attackers, target fighting back, 1-3 slots).
- **Ask while defending**: a blocking or dodging target gets the initiative roll (the +0x144 path); one that wins while still in its
  defence leaves the loser yielding from the defence's end (engine choice; the original switches it to deciding at once only when no unfinished non-looping action clip plays, `FUN_1405b34b0`).
- **Player's pick**: a target the player picked (right click, Attack Target, Attack Unprovoked; not Attack All) is kept: the winner of a
  roll does not turn on the asker while that target stands (engine choice; without it a losing old foe re-yields every exchange and the
  order never sticks). The original lets the swap happen and returns to the focused target at the next pick (above).
- **Engaged list**: a landed or blocked blow takes a defender fighting the attacker off its list (`CombatSlot.Engaged`): it asks again.
- **Approach**: the [fight steering](#fight-steering) band of its state (max' = max(6, reach − L/4) when engaged).
- **Joining**: a lower table slot asks first; one not yet in is counted ahead only when it may decide this tick (not staggered,
  defending, or swinging past its abort point), so nobody is left out for a fighter that does not ask (engine choice).
- **Waiting**: fighters left out, and engaged ones in the wait after their own swing (state 6, [Pacing](#pacing)), keep the state-6 band by the [fight steering](#fight-steering); none orbits.
  n (`CombatSystem.FightersAt`) is the target's engaged list plus the askers it refused on the last tick (`CombatSlot.RefusedTick`; a path chaser does not ask, as state 11).
- **Block-mode timer**: an engaged fighter on the Block order (standing) frees the slots on itself every 1.5–4.5 s (`CombatSlot.SlotsCleared`:
  swings started before hold none). The +12 while running is in `CombatTuning.StandGap`.
- **Body room** (`CombatSystem.NearestFriend`, `BodyRoom`): the ask fails while a non-enemy's centre is nearer than footprint + 2.5, ×1.2 in
  state 5 (`CombatSlot.CircleState`: engaged, its last ask failed with no blow coming at it). An AI rule, not collision: Meitou bodies are solid
  only to `BodyShare` × footprint (2.4 for a human), and the separation push (radius 8) spreads crowded allies until they have room. Engine
  readings: an enemy is one of a hostile faction, the target, or one fighting the asker; carried bodies do not count. Out of the list (`CombatSteering.OutOfFlock`): the down,
  caged, in a bed, carried, and those resting on a seat (Meitou's chair or throne sitting).

## Fight lifecycle and orders

**Observed** (decompile of kenshi_x64 1.0.68; class/method/field names KenshiLib 1ce4a16, **Observed**; literals **Verified** in the decompile).

- **Who has fight goals.** `Character::setupAI` gives every character goals 14 Idleness (priority 1) and 32 Self preservation (priority 3,
  urgent, target itself); player-faction characters also 243 Getting food (animals 259), others their race's goals ([precedence](ai-tasks.md#precedence)). Attack on sight is goal 16 ([combat goals](ai-tasks.md#combat-goals)):
  NPCs get it from packages, a player character only while on Hold. Its desire (`AI::scoreAttackEnemies`) is 1, but 0 while sneaking outside a
  fight (`Character::isStealthMode`), Passive, or not standing (prone state ≠ 0); `scoreProtectAllies` (`FUN_140599b90`, **Verified**) is 0 when sneaking and not in combat mode, when Passive (in a fight
  too), and for a slave (state 1) whose AI holds a job of type 187 Obedient Slave (`FUN_140597380` scans the job list); otherwise the finder's score, x0.5 while carrying.
  Meitou: `AiSystem.FightDesire` (NPCs) and `RetaliationSystem.JoinFight` (the player's), a state-1 slave with a `SlaveMaster` standing for job 187.
- **Being targeted.** Choosing a fight target (`CombatClass::setAttackTarget`, `setAttackTargetHandle`) calls `AI::underMeleeAttack` on the
  target at once (through `Character::attackingYou`), before any blow; dropping it sends "stop", which only removes the attacker from the
  target's engaged list. A landed blow (not blocked or dodged) on a defender whose fight is off sends it too (`FUN_1404394f0`). The call ends the
  target's sneaking, adds the attacker to its engaged list, to its senses (seen, alert timer 5) and to its squad memory as TEMP ENEMY (kind 4,
  not for allies; the whole squad then takes it for an enemy, [squad memory](dialogue.md#squad-memory)), tells a campaign, and when the target's fight is off: unless it is getting up, or is player-side and moving (CharMovement
  +0x24, `Character::isWithThePlayer`), its current goal is cleared and it replans (`AITaskSytem` vfunc 3); a target getting up instead has a
  10% chance of its knockout timer (+0x4f8 = MedicalSystem +0xa0) being set to 10.0: knocked down again for 10 s (**Verified**). The targeting path sets the call's last flag (r9b = 1 in the disassembly), the hit path
  clears it. With the flag, when the target is the player's, or the attacker is the player's and the target is not yet aware of it, the call
  is ignored unless both are in the same building and on the same floor and the target has a clear line to the attacker. So targeting a
  player's character through a wall, or an unaware NPC from another floor, alerts nobody until a blow lands. The line test (`FUN_1408533a0`) is a ray between
  the eyes with no length limit, so distance never matters. The melee callers are the CombatClass target setters (`FUN_140665580`, `FUN_140665650`,
  `FUN_1406681e0`; the deciding state `FUN_14060c8b0` sets the target at the pick, before any chase), the swing start (`FUN_1402b4570`, flag clear)
  and the stealth knock-out (`FUN_14034cd80`). A shooter never comes here: the ranged controller's target setter (`FUN_1404371f0`) only sends "stop"
  to the old target (the shot handler with its flag clear, `FUN_1406657d0` only; its one other call, `FUN_1405c68a0`, drives the shooter's own movement object), so the
  new target learns nothing at the pick. Each shot fired (`FUN_14043a730`) and each bolt hit (`FUN_140439c90`) calls the shot handler `FUN_140999d50` on the target with
  the flag set: the forced sense of the shooter with the alarm flag (`FUN_140858f40`, 1), sneaking off (`FUN_1405ca9a0`, 0), the squad's TEMP_ENEMY tag
  (`FUN_1406744a0`, 4), and with its fight off (+0x130) and not getting up (+0x278) a replan; no knock-down roll and no stand from staying low (**Verified**, decompile; the only writer of 10.0 to +0x4f8 is
  `FUN_140999f00`). So crossbowmen picking or shooting a character that gets up never knock it down, and a target learns of a shooter at its first shot. Meitou:
  `CombatSystem.Alerts` for a melee attacker, the first shot since the pick for a shooter (`RetaliationSystem.AlertsNow`; the bowman's alert adds `AggroSystem.Alarm`), taken by
  `RetaliationSystem` the same tick (it ends sneaking, makes the attacker a threat of the target at once, `AggroSystem.Force`, and asks an NPC's AI to think at the next tick); same place by `SightMap.PlaceOf`, same floor as within 20 units of height inside, a terrain and
  building line between the eyes; aware = fighting the attacker or hit by it (engine readings). The 10% knock-down: `CombatSystem.UnderAttackGettingUp`, from the alert of
  an attacker not in ranged mode (`CombatSlot.Ranged`) and from a landed melee blow outside a fight; a bolt's stagger skips it. A landed blow or bolt on an NPC with its fight off, not getting up, asks its AI to think again (`CombatSystem.HitReplan`).
- **Self-defence.** Self preservation's desire (AI slot 17): 1 with a threat within 40, 0.7 with a threat of positive aggression within 181.7
  (squared 33000), else 0. It wants `!isAggressionTowardsMe`, given by 13 "Attacking attackers of" (desire 1; finder `AI::findAttackerOf`:
  the subject's attackers and threats that are enemies or target it, never allies, within 150 of a subject other than the chooser; best by the
  target score) or 35 Running away (slot 55 `FUN_1405a34e0`, **Verified** by decompile: for a character whose faction object is set at `+0x250` (a player
  faction, `FUN_1407916d0`) 0 when it has an arm to fight with (either arm usable, medical `+0x165` / `+0x166`, `FUN_140644150`), when `FUN_1404366b0` holds
  (the character's `+0x2f0` object, read as ranged mode, has its flag `+0x36` set and the hand it names resolves to something not down), or when any
  item in a weapon slot is outside categories 3, 6, 8; then, for every character, 1.0 when Character vslot `0x360` (byte `+0x5b8`, `FUN_1405e1ef0`) is set; else
  0.001 when it can get out (`AI::isLocationAccessible` `FUN_1405a2710`: a null location hand, i.e. outdoors, or a building with no doors, a door open or
  one opening, `Building::hasAnOpenDoor` `FUN_1405499a0`) unless it stands inside town walls (its [gate code](pathfinding.md#gate-codes) above 0) whose gate state to code 0 (`FUN_1402eb070`; the states 1 a gate open or broken, 2 closed, 3 locked, a route as bad as its worst gate and the best route kept, **Observed**) is 3, i.e. every way out passes a locked gate; else 0).
  <a id="crippled-flag"></a>Byte `+0x5b8` is the **crippled flag** (**Verified**): `Character` embeds its `MedicalSystem` at `+0x458` (KenshiLib 1ce4a16 layout), so `+0x5b8` is medical `+0x160`, which the medical
  tick `FUN_140649320` clears at its start and sets when a part whose `collapse part` has bit 0x10 or 0x20 tests down (the forced crawl, [ragdolls](ragdolls.md#forced-crawl)); vslot `0x360` is
  `Character::isCrippled` (KenshiLib). Its neighbours are the same medical bytes: `+0x5b9` (`+0x161`) knocked out, `+0x5bc` (`+0x164`) dead, `+0x5bd` / `+0x5be` (`+0x165` / `+0x166`) the arms. The character tick
  `FUN_1405d00b0` sets posture 2 exactly while it is set. Readers: the leg-technique penalty ([above](#attack-technique-choice)), the idle stance, state 26 (false for a crippled character, `FUN_140599e90`)
  and the down finder of `FUN_1408cd2d0`. Meitou: `MedicalState.Crawling`.
  Rows 13 and 35 both carry the crippled-repertoire bit (flags `0x61`). The planner takes 35 when 13's chain is worth less: 35 is worth 1 to a crippled character and 4 only 0.1 under a threat within 40, so a
  crippled NPC runs; with no working arm 4 is 0 and 35 (0.001) is all that is left; anyone else fights back, 35 being worth 0.001 (details: [Self preservation](ai-tasks.md#combat-goals)).
  An attacker beyond 181.7 (a shooter) leaves the desire at 0: only another goal answers it ([shot from afar](ai-tasks.md#combat-goals)).
  Meitou: `SelfPreservation`, goal 32 in the AI's chooser (both plans; `FleeSystem` runs), `RetaliationSystem` only for an NPC without a mind; Passive does not touch this path (exe tooltip: "Passive
  focuses on non-combat unless attacked first"). Meitou: order toggles.
- **Melee or shooting.** Row 4 Attacking (`AI::scoreMeleeAttack`, slot 53): 1, ×0.1 with Ranged on, ×0.5 when its squad is not intact (`Platoon::isIntact`);
  0 against a caged target; a crippled fighter 0.1 (0.0001 in Block or Hold with no threat within 40; 0 for a non-player non-unique); on Hold
  only for a target its range tests accept. Shooting 262/263/266 (slot 54): only with Ranged on, a crossbow at back or hip and its bolts carried:
  1 (0.5), 0.1 with a threat within 40, which their list B forbids anyway. So Ranged on shoots until dry or rushed, Ranged off never shoots.
- **Fight task.** Rows 4 and 227 build `Task_MeleeAttack`; orders 5 Attack Target and 61 Attack Unprovoked build `Task_FocusedMeleeAttack`.
  The planner first needs 37 `isReadyForAction`, given by 59 Get ready after 6 Equipping weapon draws (at once: its task swaps the
  weapon in one call, **Observed**; Meitou draws when the fight starts). Task start → `initCombatMode` (mode 0):
  CombatClass +0x130 on, state 4, attack start position, the technique cleared; the focused task also stores the target as CombatClassAI
  +0x2c8 and (with its +0x98 flag) marks the broadcast as aggressor (+0x114). **Verified** (`FUN_14032ebd0` builds the task through `FUN_14033c160` with flag 0 for row 5 and 1 for row 61; `FUN_14033c250` start runs the Assault booking `FUN_140334070` only with the flag; rows 4, 13, 16 and 227 build `Task_MeleeAttack`, whose flag is 0): only Attack Unprovoked (61) books an Assault and sets +0x114; Attack Target (5) and every AI fight do not. Row 61's intended aggression is 1, row 5's 0. The booking runs again on every update of the task (`FUN_14033c250` calls `FUN_140334070` each time; the victim's byte +0x114, the bounty, class 8, +0xdc and combat-mode gates apply each time), so the pending crime's timer is refreshed while the victim stays passive. Meitou: `AttackOrder.Unprovoked`, `CombatSystem.Assaulted` at the start and `CombatSystem.RepeatAssaults` each tick. Task end → `Character::endCombatMode` → mode 2: +0x130 off,
  the target released, state 4, dead timer `calculateDeadTime(2)` = 0.2 s, the animation combat flag off, movement mode MOVE_NORMAL
  (`CharMovement::setMovementMode`), engaged and
  threat counts zeroed, focus cleared. `getTimeSinceLastCombatModeInHours` reads +0x138, stamped at start.
- **Weapon away.** The fight's end sheathes nothing. Idleness (14), first aid, splinting, repair, lockpicking and patrols need
  `!hasWeaponEquipped`, so the planner puts 7 Sheathing weapon before them: a character stays armed while urgent goals run and sheathes when it
  drops to Idleness or is ordered to such a job.
- **Orders beat goals.** `AITaskSytem::chooseGoal` tries, in order: the player's order queue (`checkOrders`, OrdersReceiver +0x38), jobs, urgent
  perma-jobs, priority-3 goals (Self preservation, Hold's goal 16, package goals), the other perma-jobs, priority 2 then 1 goals, the hold-position
  task. So a character with an order does not defend itself: a hit staggers it (below) and the queue's first order then runs again, unless its row needs no melee threat
  (146 Turret gunner's `!158`: that plan fails, the order stays queued and Self preservation runs; [precedence](ai-tasks.md#precedence)); one on a job
  (not urgent) does ([precedence](ai-tasks.md#precedence)).
  `Character::addOrder` without Shift clears the order queue and the jobs, sets the AI's centre of movement to the order
  point ([area](perception.md#the-attack-on-sight-tests)), queues the order at priority 3 and replans at once.
- **Right click elsewhere mid-fight.** Ground: `PlayerInterface::playerMove` gives each selected character order 29 Move order. At the replan
  its fight task ends (above): it no longer blocks or dodges, since a target whose fight is off always answers an ask with "attack"
  ([ask](#attack-slots-and-circling)). It also stops being its attackers' threat and leaves their engaged lists, so once their 1 s pick timer
  runs out they keep it only through the sensed-enemy fallback or a focused order, then chase it ([leaving](#leaving-a-fight)). Each blow
  that lands on it on the way staggers it (below). `Task_Move` start resets the clip and turns off root-motion tracking
  (`CharMovement::trackAnimationMovement`); it zeroes the Hold point (CharStats +0x130), which its update (`FUN_140354600`, **Verified**) sets to the
  move's destination on every step while the move runs, for tasks 29 and 40 only (flag `+0x95`), Hold on and not sneaking (`FUN_1402b2b90`) and a ground-point target,
  so Hold follows the last ground move, a queued one from its start (Meitou `CharacterCold.OrderMove`). Once the order is done the goals run again: with a
  threat left, Self preservation restarts a fight on `findAttackerOf`'s pick; without one, Idleness sheathes. A right click on an enemy is
  order 5 (focused); on anything else, that target's quick verb.
- **Stagger outside a fight** (`Character::_startStumble` `FUN_1404391f0`, from every landed blow). A shackled defender is knocked out
  instead 25% of the time (`FUN_140644980` with factor 0.002, the leg-loss KO shape, so its 3 s floor). Otherwise the movement stops, and when the defender's fight is off: one
  getting up is left alone; else, unless it is shooting at a live target, its current task ends (`FUN_1400522c0`), and task 147
  `STUMBLE_TASK_FORCED` ("Stumbling") is pushed to the front of its order queue (`AI::forceOrderInsert`, which also replans).
  `Task_Stumble` tracks the clip's root motion until `Character::stumbleState` reports the clip done (`AnimationClass::isLinearAnimationDone`),
  then ends and is popped (`AITaskSytem::bodyTaskComplete` pops the front order when it is the finished task). The interrupted order stays
  queued behind it and is planned again from its start. The clip itself is picked only for a humanoid that is not a ragdoll, not KO'd or dead
  and standing (prone state 0), and only when none is playing or the blow is heavy ([hit reactions](#hit-reactions)); without one the
  Stumbling task ends on its first update. A fighting defender stays in its fight's stumble state instead. Meitou keeps the stumble as combat timers (`StunUntil`, `StaggeredUntil`, the clip) honoured by the movement system; the hold time already matches (the clip length). Meitou reports Stumbling (147) as the goal text for the clip's stun (`CharacterCold.StumbleTaskUntil`, unsaved, display only via `AiSystem.ActivityText`; no `CharacterTask` value, so saving and hashing are untouched) and the replan stays the existing `DecisionSystem.Request`.

**Standing orders** (`Character::setStandingOrder`, player faction only; ids are KenshiLib's `StandingOrder`, `M_SET_ORDER_*`):

| Id | Order | Effect |
|---|---|---|
| 0, 1, 2, 16 | run, jog, walk, group speed | movement speed mode; a crouching (prone state 1), uncrippled character stands up instead |
| 3, 4 | stealth on / off | Character +0xd4; Task_Follow rewrites a follower's every update (`FUN_14033b350`, **Verified**): on while its leader sneaks out of a fight and is its ally, else off (Meitou: `MovementSystem` follow step, ally = same faction) |
| 5, 6, 7 | aggressive / defensive / evasive | AI +0x2b8 (no reader found) |
| 8, 9 | far / near | manoeuvre mode: 8 ROAM_FAR (centre target cleared), 9 STAY_CLOSE (centre at its position). **Verified** (decompile sweep of every call of the Character slot 0x308 and of `FUN_1407f3880`, the HUD's sender): no HUD button, hotkey, saved-state replay (`FUN_140626f50`) or AI package sends 9; 8 is sent once, to a spawned character (`FUN_1405836e0`, a no-op: ROAM_FAR is the default); Hold (12) leaves the mode alone, and HOLD_GROUND (0) is never set. The orders panel's buttons send only 3/4, 11, 12, 13, 14, 15, 17 and the speed ids 0-2, 16. Meitou has the commands and no UI for them, as the original |
| 11 | Block (`DEFENSIVE_COMBAT`) | CharStats +0x128 (set only with vfunc 0x250, KenshiLib `isHuman`) |
| 12 | Hold | +0x12b; on: hold point +0x130 = position (a running order move's update overwrites it with its destination; a get-up's start sets it to where it wakes) and goal 16 added (priority 3); off: goal 16 removed |
| 13 | Passive | +0x12c |
| 14 | Taunt | +0x12a |
| 15 | Jobs (`CHASE`) | `OrdersReceiver::setJobsEnabled` |
| 17 | Ranged | +0x129 |

Readers: Block (`isDefensiveMode`: +0x128 and prone state 0) feeds the block chance, melee defence and dodge (exe tooltip: never attacks,
+20 defence), makes the ask answer 0 and runs the state-5 timer. Hold (`holdPositionMode`: +0x12b, not sneaking) feeds the post test
`AI::isTargetInRangeForHOLDMode_hostiles` ([perception](perception.md#the-attack-on-sight-tests)), the score's ×0.01, the 0.3 s re-pick and
the melee desire. Passive and Taunt act through the desires and the target score above; Ranged through the shooting desire and the shooting
gate ([ranged](#unknowns-of-the-ranged-path)). Neither reads Block, so a Block character with Ranged on still shoots (**Verified**: no +0x128 read in the
shooting desire `FUN_140598260` or the gate `FUN_1407f2840`; no ranged routine found among the other readers, **Observed**).

## Combat stance

### Fight steering

- **Band** (`combatMovementOffensive` `FUN_1402ae320`, via `CharMovement` `FUN_1402ae900`): each fight state gives a target, near n, far f, a
  "circling speed", `powerThrough` and a speed limit. min = clamp(n − m, 1, f′), max = max(f′, min + 5) with f′ = clamp(f − m, 0, 9999), m =
  +0x110 = CombatClass +0x278 = 1.5 × the raw footprint × the size k (**Verified**: `FUN_14060ce30` rewrites it every tick from `myRadiusX` × the double at `0x1416b3be0` = 1.5, then × Character vfunc 0x390; `FUN_1402ae6d0` copies it; the 1.4 written here before was wrong). Calls: attack start and state 5 (n = 1.4r, f = max' of `FUN_14060cd20`, powerThrough on);
  state 5 escorting (+0x178 → +0x20 set: n = max', f = 50); state 6 (n = N/4 + 15, f = 3N + 25, the drawn side speed); the walk
  of `AttackState.Enter` when no technique is in reach (slot 1 `1402b4af0` → `FUN_1402b4570`: n = 0.7r, f = max'/2, powerThrough; **a swing itself has no band**, see
  [Facing and movement in a swing](#facing-and-movement-in-a-swing)); defending (slot 12: n = r + 7.5, f = r + 19, kept to the followed attacker +0x1a8 with limit 16, or to a designated
  target +0x2c8 with powerThrough and limit 18); the defaults are 10, 20, limit 200. The **circling speed +0x114 is written but never read** (no read of +0x114 or
  CharMovement +0x22c anywhere): state 6 does not orbit.
- **Seek** along the facing (the unit vector to the target, or the lead point while chasing): with d = centre distance and R = own radius
  `+0xe0` + target radius (`getRadius`, footprint × size), m(X) = (d / (R + X) − 1) × 100; outside max the seek is m(max) × 2 × speed mult
  (walk in), inside it min(m(min), 0) (back off only inside min; not while chasing). A forced point (+0x50) adds 750 × the unit vector to it.
- **Repulsion** (`FlockingTools::calculateCurrentRepulsionVector` `FUN_1402e9740` over the [flocking list](#attack-slots-and-circling)): each
  other character c except the target, enemies (c's `isEnemy` of the mover) with radius R + ENEMY_DIST (+0xfc = 15 + 30u) and non-enemies with
  R + ALLY_DIST (+0x100 = m + 30u), pushes along the flat unit vector away from c by (1 − d / (r_c + radius)) × 100 when positive
  (`getRepulsionVector` `FUN_1402e9420`), at most 26 contributions; with `powerThrough` one pointing behind the facing is turned to the
  perpendicular of the facing on its own side, its length kept (`FUN_1402e9530`). Velocity = (enemies + 2 × allies) × 0.105 + seek (×0.5 when
  the caller's flag is set: a look-at target over 40 away in a state with +299, rare).
- **Dead zone** (unless `powerThrough`): under STOP (+0x104) while moving, or START (+0x108) while standing, the velocity is 0. STOP = 3 + 3u,
  START = 30 + 3 STOP + 12u; for a humanoid both × (1 − `encumbranceMult`) × 2 / × 3, + 1; STOP is kept in 0..20, START ≤ 90 and ≥ STOP + 10.
- **Side steps** ([animation](../animation.md#side-steps)): in the facing's frame (x = facing × −Y), not strafing the side part is ×0.105,
  strafing the forward part is (STRAFE_STATE_DAMPING +0xec = 0.105): movement snaps to forward/back or sideways.
- **Speed**: cap = min(limit, or 55 at a forced point; own max speed); the ramp +0x12c (from 20) rises 15/s up to it; facing-forward
  (dot > 0.5) the ramp, backing (dot < −0.5) 16, sideways 18; not running at most 18. Over the cap the velocity is cut to it.
- **Smoothing**: x and z each through a `MotionFilter` (10 samples, newest first, weights 0.8^k, normalised; per frame).
- The caller then ×speed mult (×0.75 / 0.5 / 0.4 in movement levels 1–3), caps at the animation's max speed and hands it to the nav agent.

Data (**Verified**, the base ANIMATION records through `AnimationLibrary`): with the flag on, the lower clips are the `walk lower combat shuffle` trio (speeds −99 to 50) and `jog lower combat fallback` (50 and up); `walk`/`jog`/`run lower` are combat NO.

## Leaving a fight

How an attacker stops when its target runs. **Observed** (static reading of `Task_MeleeAttack` update `14034ca40`
(thunk `14001681f`), `ChaseMode` vfuncs 0/1 via thunks `140016243`/`140020d1a`, `CombatClassAI::vfunc_9`); literals
**Verified** (decompile). There is no distance leash: the exits are the target going down, nothing left to fight,
and the give-up test of a path chase. Ending the task means `FUN_1400522c0`: it sets the AI task system's `+0x26c`,
which its next update (`FUN_14050d920` → `FUN_14050d7d0`) consumes by dropping the current task and clearing its targets.

- **Nothing left** (`Task_MeleeAttack` update): the task ends when the fight task stopped (`FUN_1405c9750`), or when all hold:
  the senses (Character vfunc `0x68`) count no enemies (`numEnemies` +0x94 = 0) and no threat to allies (`totalThreatLevelAllies` +0x8c ≤ 0,
  [threats](#fight-lifecycle-and-orders)), nobody is engaged on the character (engaged list + overflow 0),
  the fight has no target (`+0x2a0` none), and the task's own target is gone or down (its vfunc `0x30` true; **Verified** decompile of `FUN_14034ca40`).
  The immediate end above is `CombatClassAI` update `FUN_14060d6a0` (**Verified**: the dropped target equals the stored focused hand and refocus `+0x2e8` is clear, i.e. a picked attack).
  Meitou (`CombatSystem.NothingLeft`): a fighter that loses another target, or whose periodic choice finds nobody, fights on without a target (state 9), choosing again every pick
  interval and still defending, until the test passes over its senses ([perception](perception.md#senses): `Enemies`, `ThreatToAllies`), `AttackerCount` and the task target (gone, dead,
  knocked out; carried too, engine reading). Engine choice: a task target with no sense entry counts as gone, so the entry's decay (30 / 60 / 120 units of 2 s) ends the wait on a live
  target the fighter lost; a world without senses ends the fight at once. A player character's fight that its own goal started (32 Self preservation or
  15 Protect allies, row 13) also ends without a target once nobody attacks it and no threat is sensed on it or its allies, whatever `Enemies` holds
  (`CombatSystem.GoalSpent`): the original ends it through the goal, whose desire falls to 0 and whose replacement takes over. **Observed** (original 1.0.65,
  harness, #695): after a fight with a spawned Dust Bandit squad the character went Aimless with a crawling, conscious bandit 45 away, which
  `numEnemies` counts (`FUN_1408593d0` skips only down, dead `+0x5bc` or carried). Engine choice: an enemy in a cage is not counted (`SenseBlock.CagedEnemies`)
  and a task target in a cage counts as gone. The original ends such a fight through its goal ([arrest chain](ai-tasks.md): effect 129 holds once the target is
  imprisoned), which the engine's own fights (an arrest's, a joiner's) lack. **Observed** (Meitou, headless The Slaves start, a player slave put in a cage
  mid-arrest): without it the guards stayed in combat mode while they sensed the prisoner and turned on it when it was let out (#629).
- **Path chase give-up** (`ChaseMode`, a base of `Task_MeleeAttack` at task `+0x78`): checked only while the fight is in
  state 11, the path chase that `CombatClassAI::vfunc_9` enters from state 10 when the navmesh line to the target is
  blocked, the two are in different places, or the 2 s chase timer runs ([Combat stance](#combat-stance)). Any other
  state clears the test's cached gate, so it restarts with each path chase. The gate (`vfunc_0`, computed once on entry,
  which also stamps the start time) passes when the chaser's faction is **not the player's** (Faction `+0x250` null),
  the target is alive (vfunc `0x30`), not in a bed or cage (`+0x2f8` 0), the chaser is not in a conversation (`Dialogue::conversationHasEnded` `FUN_140671c60`), and either the target flees (its current task is 35 Running away or 127 Retreating fast, or 29 Move order
  to a point farther from the chaser than the target is: `FUN_140331ee0`), or the chaser counts it an enemy (Character
  slot 125), or two floats of the chaser's status object (`+0x1a0`: `+0xa0` + `+0xac` > 0) or the target's `+0x148` are set.
  Then each tick (`vfunc_1`, never when Character slot 126 calls the target friendly):
  - nothing before 5 game minutes since the chase began; after 1.1 game hours (sense-age unit > 60, below) the AI's plan is
    reset (`FUN_1405074a0`) each tick, the chase continues;
  - the chaser has no sense entry for the target → give up;
  - the target is neither seen nor heard (entry `+0x38`/`+0x39`) and its last sighting is older than 12 units, or the chaser
    stands within 60 of where it was last sensed (squared 3600) → give up.
  - Giving up ends the task, fires dialogue event 60 GIVE_UP_CHASE from the chaser at the target and resets the AI's plan. Meitou (`AggroSystem.ResetPlan`): both resets clear the chaser's plan and commitment and ask for a new think; `vfunc_1` is `FUN_14033a800`, called from the `Task_MeleeAttack` update `FUN_14034ca40` (not `FUN_14034c7c0`, `Task_PlayerTalkto`).
- Time: the world clock is in game hours; the sense-age unit is hours × 3600 / 33 × 0.5 (`FUN_140852150`, `FUN_14033a170`),
  so 12 = 13.2 game minutes, about 24 s real at 1x ([game loop](game-loop.md#the-three-time-values)).
- So a player's character never gives up a path chase by this rule, and an NPC on open ground (no path chase) keeps
  running at the target with the footwork lead while it has a fight target. The attack goal's finder (seen/heard only, [Combat goals](ai-tasks.md#combat-goals)) lets go at once; the pick (`FUN_140666ee0`) keeps
  a runner only while it is seen or heard, or while it still attacks or threatens the chaser (its entry then lasts, [decay](perception.md#senses)). The runner's side: [perception](perception.md#retreat-and-memory).

Meitou (`AggroSystem.Chase`, reading the chaser's sense entry of its target, [perception](perception.md#senses)): this give-up (9.1 s gate, 24 s unsensed, 60 units of the entry's place;
NPCs only, event 60), tested in every running chase, not only a path chase (engine choice), but never while a target not sneaking is within 60 of the chaser itself (engine choice: one
running past its runner has it at its back); its own fight target sneaking is seen in view without the spot meter (engine choice).
An NPC's fight that its own combat goal started also ends when the goal is done or its chooser takes a goal that is no fight (`AiSystem.InFight`, [combat goals](ai-tasks.md#combat-goals)); it is the AI's plan step, as the original's.

## Armour and medical hit

**Verified** (take-hit `FUN_1404394f0` -> outcome `FUN_140665fa0` -> `FUN_1406508d0` -> `FUN_14064f300`; armour `FUN_140898e90`, tables `FUN_1408a8f90`).
A blocked blow does no damage; a dodged one none. On a hit: (1) part pick: parts with flesh above -M (M = base x multiplier x scale - wear; stun and rig do not count) that
are not a limb stump, weight = hit weight x `hitMult` (weight alone for impact direction 6), a low strike drops arms and head, rand in [0, total); with none the last part
is hit. The picked part's `hitMult` is updated before the damage: 0 if its flesh is gone, else +1 while < 4, but with a negative fraction held to at most 2 and +1 only while < 1.
(2) each worn piece rolls its own coverage (rand in [0,1) < the part's `part coverage` percent x 0.01; unlisted part = not covered). Covering pieces, in worn order:
cutRed += cutDef(1-cutRed), bluntRed likewise, flat pierce += pierceDef, stunFrac += cutDef x cutIntoStun x (1-cutRed before). A non-zero penetration p: both reductions x(1-p),
capped 0.9 (only then). (3) cut' = cut(1-cutRed), blunt' = blunt(1-bluntRed), pierce' = max(0, pierce - flat), stun = (cut + pierce absorbed) x stunFrac (the packet's own
stun is discarded). (4) toughness: r = lerp(clamp(tough x scale x 0.01, 0, 1), `damage resistance min`, `max`); all four x (1-r), cut and blunt floored at 0, then x the
global damage multiplier option. (5) `FUN_14064f300`: flesh -= cut + pierce + blunt x `blunt permanent organ damage`; stun += the rest of the blunt + stun; blood -=
(cut + pierce) x `immediate blood loss` x bleed mult x race `bleed rate`; wear on robots/prosthetics (cut+pierce+blunt) x 0.04 x `robot wear rate`. Armour build
(`ArmourPiece.Create`): class and material tables (cloth 0/0.1..., chain 0.05..0.65, plate 0.1..0.85; class cut 0.4/0.8/0.9/1, blunt 0.1/0.7/0.9/1) **Verified**
against `FUN_1408a8f90`'s constants; caps 0.9 cut and 0.8 blunt; shirts (slot 8) cut/blunt x0.8, pierce x0.5. Quality = level x 0.01, not clamped; a piece with level 0 or less, or
with no `part coverage`, has quality 1 and skips the slot rules (shirt factors, the hat's stealth floor) (**Verified**, `FUN_140898e90` disassembly; `ArmourPiece.QualityOf`/`Graded`). Health, bandage, stun, blood and
KO/coma/death thresholds use [character-stats](character-stats.md). Never tick medicine twice.

### Punch self-damage

**Verified** (`MedicalSystem::punchSomething` `FUN_140666780`, called on the attacker's medical at the end of the take-hit `FUN_1404394f0`; arguments material, target,
technique, combo index per KenshiLib 1ce4a16; constants from `re/data_values.txt`). Every blow that reaches a defender hurts a martial artist's own limb:
- Material: 5 blocked, 0 dodged, 2 hit (`HitMaterialType`: 0 missed, 1 metal, 2 flesh, 3 sand, 4 wood, 5 sword, 6 chain); 1 when the target's race `is robot`.
  A swing that reaches nobody never calls it.
- None unless the attacker's stat-set kind is 5 (fists), its race is not `is robot`, the blow's impact point `limb` (clamped combo index) is not 4, and
  that limb (0 left arm, 1 right arm, 2 left leg, 3 right leg) has no fitted robot limb (part `+0x28`).
- Packet: material 1 cut 2 blunt 2; 5 cut 5 blunt 1; 6 cut 1 blunt 1; else blunt 1. Times f = (1 - 0.5 x (fist rating + toughness x scale) / 100)
  x Global damage multiplier x the gear's `fist injury mult` product (CharStats `+0x4c`); fist rating = `FUN_140884e00` with its flag, toughness the raw stat
  (`+0x90`) x `+0x34`. No clamp: above 200 together f is negative and the blow heals the limb.
- Written as `FUN_140644a70`: flesh -= cut + `blunt permanent organ damage` x blunt, stun += the rest of the blunt (no armour, wound, bleed or toughness
  resistance). With the base organ damage 0 a landed or dodged punch only stuns the limb; blocked blows and robot targets cut it.
- Then the part-down test asks for the collapse (`FUN_140649870`) and the attacker gains toughness ([character-stats](character-stats.md), `FUN_1408c68e0`).

Meitou: `CombatSystem.Punch`, `MedicalState.PunchDamage`, `Fighter.FistInjury`; the medical tick sees the down part.

### Block sparks

**Verified** (decompile of the take-hit `FUN_1404394f0`, outcome 5; test `FUN_140436980`; visibility `FUN_1405c9f60`; entity setter `FUN_1405b93a0`): a blocked blow plays the impact sound
`Impacts`/`HtH_Impacts` when the attacker fights with fists (its CharStats +0x1f0 is 5) **and** `FUN_140436980(defender, attacker, technique, blow)` is false, else `Impacts`/`Parry`.
`FUN_140436980` is true when the attacker's race has the robot byte (RACE `is robot`, +0x7c), or when the *defender's* own limb of the number the blow's `limb N` names (0 left arm, 1 right arm,
2 left leg, 3 right leg; 4 or none: false) has limb state 2, a fitted prosthetic (`FUN_14002a6e9` on the defender's medical object, Character +0x458; the original passes the defender there, not the
attacker). In the Parry case, when the defender's AnimationClass (Character +0x448) byte +0x88 is set and Character +0x1a9 is set, it spawns the EFFECT `5463-nodes_furniture1_otto.mod` (`small- sparks1`,
particle system `kenshi_sparks`: 40 sparks at once, 0.05-0.2 s life, falling) on the defender's `Bip01 Prop2` bone, offset (0, `FUN_1409b3b40(2)`, 0) (a random 0 or 1). **AnimationClass +0x88** is "a body
entity is attached" (1 when `FUN_1405b93a0` binds the Ogre entity and skeleton, 0 when it is cleared). **Character +0x1a9** is "in view": the per-frame visibility pass `FUN_1405c9f60` sets it, then clears it
beyond a squared view distance (`DAT_1421334c8` against `FUN_1407f2a20`, skipped when the race byte +0x78 is set) and, **Observed**, when the position fails the visible-area queries (`FUN_1406ae7b0`, `FUN_1406ae710`) and then a camera frustum test (`Ogre::Frustum` planes, bounding radius `FUN_14065e1e0`); `FUN_1405c87f0` sets it to the visibility just set on the entity. So the flag is "the character is in view";
`FUN_1405c87f0` is only the setter used when a character is hidden or shown (indoors), which also sets the entity's visibility.
on screen with a body. Meitou: `CombatSystem.IsParry` decides (armed attacker, robot attacker, or the defender's prosthetic at the blow's limb) and sets both the sound and `World.BlockSparks`;
`GameHost.UpdateBlockSparks` bursts the system at the posed `Bip01 Prop2` of a character drawn this frame (`CharacterRenderer.BonePosition`: attached and in the world), which is both flags.

## Ranged and turrets

Controller phases reload → steady aim → fire; target motion/range, skill/injury, equipment and
line of sight determine timing/spread. Bolts retain launch/direction/velocity/impact/friendly-hit
state and consume ammo charges; do not model hitscan. Turret objects and mounted ammo/reload
branches differ from carried crossbows. Player/NPC targeting and friendly-fire training have
separate eligibility. The retained field/constant tables and expression list specify decoded
values; trajectory, aim timing and obstruction uncertainties remain labelled.

## Turrets

- **Damage D** = the building's output value `+0x1a4` (**Verified**: loader `FUN_14029e460` = BUILDING `output rate` × functionality `production mult`, ÷ `max operators`
  when > 1; tooltip `FUN_14030d620` "Default damage:" shows Building slot 120 `FUN_140297b30` = `+0x1a4` × the research multiplier of a player-owned building (`FUN_14082ec70`,
  owner test `FUN_140546dd0`)). Base data: harpoon turrets 75, auto/multi 50 (`output rate`, **Verified** probe); walls 10–30.
- **Manning** (`Task_UseTurret`, update `FUN_1403474f0`): the building of the order's target or the character's `+0x258`; it must be a turret (slot 107 = 8) with a free
  operator place (slot 159); the gunner faces it and holds clip `combatstance`; unless broken (`+0x3b6`) the controller gets the building and stat 11 Turrets
  (`FUN_140434570`, also sets it active), the controller ticks, and slot 150 `FUN_140434d20` registers the operator and sets the turning speed `+0x464` = e × `aim speed` × (1 + 0.01 D).
  Broken, the update calls `AITaskSytem::taskImpossible` (`FUN_14050c890`) and the task ends (Meitou: `Machine.Broken` ends a player gunner's manning of its own turret, a town turret's saved `broken` any gunner's; [what damages a building](buildings-production.md#power)).
  Start/end (slots 1/3) set the building's in-use flag; the end clears the controller (`FUN_140437470`). Crossbows use the same controller with stat 35 (`Task_RangedAttack` `FUN_14033bfb0`).
- **Turning** (`TurretBuilding::vfunc_28` `140434b70`, `RotatingEnt::vfunc_2`, **Verified**): `+0x448` = −99, then while `+0x464` > 0 and someone operates, each `rotation function` 5
  part turns toward `+0x4a8` by its error (yaw about Y or pitch) limited to ± `+0x464` × frame time, down the part chain; `+0x448` = the largest error (degrees,
  `FUN_1404cc0d0`). So an unpowered turret does not turn and its error −99 passes the 1° test: it fires where it points. Sounds: [audio.md](../audio.md#other-world-side-events).
- **Shot** (`FUN_14043a730`): muzzle = barrel node + orientation × (0, `barrel pos Y`, `barrel pos Z`); direction = barrel +Z tilted by `randomDeviant(rand × max(0, lerp(0.005 (skill +
  perception), deviation, 0)))`; harpoon damage = U[min, max] × P × hM, range = P × range × hM, speed = shot speed × (0.8 P hM + 0.2), h = height factor from the muzzle; friendly
  avoidance min(1, 0.015 FF); `Attack` on the building's object; the target is told it is attacked (`FUN_140999d50`). The harpoon is a bolt of the gunner (hit `FUN_140439c90`, Bolt visuals).
- **Target choice** (`FUN_140437f40`, `FUN_1404374d0`, score `FUN_140435d70`): the preferred/current target is kept if it still scores and equals the order's; else every
  sensed character (list category 4, mask 0x210) scores s = (f·g)² with g = 1 − d/(k × range) (k 1), f 1 (0.1 for slot 0x360, ×0.1 for slot 0x368; slot 0x368 > 0 is 0 for a gunner not of
  the player's faction); down for any gunner (slot 0x30: out, dead, ragdolled, carried, posture ≥ 3), `+0x2f8` ≠ 0 or `+0x3d4` (carried) → 0, and an ordered target that scores 0 is dropped (**Verified**; Meitou `CombatSystem.ScoresZero`); out of range → 0.001; then × weight (slot 6), × 1.5 when `FUN_1405ca6f0`, + 0.1 when it is the
  current target; × 0.1 when its sense entry is not seen, × 1.5 for the current; best above 1e-6. None: with the player's **Shoot first** option (`DAT_142134690 +0x11a`) a player gunner takes
  sensed characters of fundamental type 0 or 8 or a default-hostile faction (k 0.6); an NPC gunner takes default-hostile ones it is aware of (`FUN_14099b820`) and those fighting a member
  of its own group. An unseen chosen target is looked at (slot 0x3b0).
- **NPC turret goals** 149/222/234 have list A `isNoEnemiesInVicinity` (with the `*` flag): done, so not chosen, while no enemy is near ([ai-tasks](ai-tasks.md#perma-jobs)).

- **Save** (**Verified**, decompile): a turret saves its building and machine keys, the `mounted` handle and its gun's int `current shots` (loaded harpoons, capped at the gun's shots on load); the aim error, rotation and reload timer are not saved ([save](../formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)).

Meitou ([simulation](../simulation.md#ranged)): `CombatSystem.Turrets.cs` (firing), `TurretManning` (gunners, gun state), `TurretGun`, `TurretFormulas`. The indoor weight takes the gunner's building and floor for the turret's (engine reading).

## Stealth knock-out

Order 228 "Stealth KO" (verb list `FUN_14079b2d0`), `Task_StealthKO` (vtable slots `FUN_140345eb0` start,
`FUN_14034cd80` update). **Verified** literals, flow **Observed**:

- Start (`FUN_140345eb0`, **Verified**): phase 0 (-1 when the `knockout` clip already plays), then the attacker is set sneaking (`FUN_1405ca9a0(attacker, 1)`) unless it is already in stealth mode, for the whole task, also for a queued order whose character stopped sneaking meanwhile (Meitou: `CarrySystem.Start` sets `Sneak`; `CanRunKnockOut` does not ask for it, only the offer does).

- Offered (order list `FUN_1407a6bc0` and the quick right click `PlayerInterface::characterSelected` `FUN_1407fb410`, **Verified**): only while the attacker `Character::isStealthMode` (`FUN_1405c9750`: sneaking, not
  in combat) and the target is not an animal (vfunc +0x248 `isAnimal`, non-zero only on `CharacterAnimal`); no faction or race test.
  So no animal can be KO'd, cleansers included ("Cleanser Unit" is an `ANIMAL_CHARACTER`, Newwworld.mod). Meitou: `CarrySystem.CanBeKnockedOut`.

- The attacker walks to the target's position minus the target's heading, a unit vector (+0xd0 of its movement object `+0x640`,
  written normalised by `FUN_140665250`; **Verified**), so to a point 1 behind it; the goal is re-issued every update, also while it walks.
  Within 14 of it the swing starts and the attacker turns to face the target. The task ends at once, silently, only when the target
  senses the attacker (`FUN_140855830` = its sense entry's +0x38 flag) or is down (vfunc 0x30 `isDown`, [carry-loot](carry-loot.md); tested every
  update before the state logic, **Verified**), so the verb offered on an unconscious target walks nowhere, rolls nothing and trains nothing; a fighting target does not end it, and the swing has no
  distance end (the roll is made however far the target has walked; **Verified**, no distance test in `FUN_14034cd80`). A
  sleeping or caged target (+0x2f8 ≠ 0 with a bed/cage handle at +0x300) is walked to at the building's `getPositionMarker(attacker position)` (slot 0x470: the nearest use node with two or more, else the marker `+0x26c`; **Verified**). `FUN_1405c68a0` (the move helper, mode 0) runs every update before the state logic in states 0 to 3, so the attacker keeps walking toward the goal through the swing; the `knockout` ANIMATION has `disables movement` false (base data), so only reaching the point stops it. Meitou: `CarrySystem.ScheduleKnockOuts` re-aims through the swing. A caged target's goal is `FixtureRegistry.FloorMarker` (engine choice): of the use nodes with a floor within 20 of their height, the one nearest the attacker, moved onto that floor. A cage cuts a hole in the floor mesh, and the node nearest an attacker on the storey below can stand over the floor's edge, so the walk went under the cage and never up the stairs (**Observed**, #649's save, Rebirth Storm House `21.20:3226-rebirth-INGAME`, cage 25 upstairs: 3 of its 6 nodes have no floor near them; with the rule the attacker climbs and the KO lands). A walk that cannot get to the target (no path, or one ending over 14 from the goal or 20 off its floor) ends the task silently, as `AITaskSytem::update` ends a task whose target the movement cannot reach (above); before, the attacker stood on the task for good.
- Swing: clip `knockout` at x1.2 (record `knockout`, anim `stealthKO` in rebirth.mod); at 0.5 of it the roll, at 0.85
  the end.
- Chance (`FUN_1408852f0`): X = attacker assassination x injury mult minus the target's max of assassination, 0.5 x
  thievery, 0.5 x stealth term x 100, 0.5 x melee defence, toughness (each x its injury mult); chance =
  100 x (X + K) / 2K, K = 30 for X >= 0 else 15 (disassembly: the hidden third argument is +K). Unclamped. 0 when the
  target's sense entry of the attacker has its +0x38 flag (it sees the attacker); x0.5 when it only hears the attacker (+0x39 heard, **Verified**). Also 0 when the target is fighting, has an entry for the attacker and the attacker is not unseen (Character +0xe8 ≠ 1).

## Validation

### Class map

| Class (RTTI) | Role |
|---|---|
| `Gear` | item base; `+0x220` quality fraction (level·0.01), `+0x224` level int. **Verified** (ctor `1400d3090`, and `1400ce2d0` lerping by `+0x220`) |
| `Weapon`, `Sword`, `Crossbow` | weapon items; `Sword` is the melee class, `Crossbow` creates the gun data |
| `Armour` | worn armour; loader `140898e90` |
| `CharStats` / `CharStatsAnimal` | stats, skills, per-hit damage maths |
| `MedicalSystem` | body parts, blood, KO, death; per-part `HealthPartStatus` |
| `CombatClass`, `CombatClassAI`, `CombatState`, `AttackState` | per-character combat state |
| `GunClassPersonal`, `GunClassTurret`, `TurretBuilding` | ranged weapons |
| `Task_MeleeAttack`, `Task_FocusedMeleeAttack`, `Task_RangedAttack`, `Task_UseTurret`, `Task_EquipBestWeapon`, `Task_SheatheWeapon`, `Task_StripWeapons`, `Task_CuffTarget`, `Task_MakeTargetStandStill` | AI tasks ([ai-tasks.md](ai-tasks.md#tasktype-to-task-class), [ai.md](ai.md)) |

### Global tuning: the CONSTANTS record

Loaded by `14086b2b0` (from "Initialising GameData", `140870ae0`) into one struct at `142133dd0`, field n at
`+4n`. **Verified** (field names read by the loader against the CONSTANTS record dumped from `gamedata.base`
through a probe, every value in the table below re-read; the "stored" rescalings re-read in the loader; consumers
by data xrefs and by the offsets used in the consuming functions). The loader rescales some values; "stored"
below is what the engine uses.

| Field | File value | Stored / use |
|---|---|---|
| damage multiplier | 0.65 | multiplied into the stored blunt/cut 1/99, pierce multiplier and stumble max |
| blunt damage 1 / 99 | 20 / 80 | blunt base at skill 1 and 99 |
| cut damage 1 / 99 | 20 / 80 | cut base likewise |
| pierce damage multiplier | 1.2 | |
| stumble damage max | 80 | stumble threshold |
| unarmed damage mult | 0.8 | |
| attack chance factor | 0.05 | hit roll skew |
| base block chance | 70 | |
| block chance increase / reduction per 10 levels | 12 / 15 | stored ×0.1, i.e. 1.2 and 1.5 per level |
| max num attack slots | 1 | clamped to 1..5 (CombatClass constructor `FUN_14060a9e0`); enemies that may swing at one target (+ RACE `extra attack slots`, [slots](#attack-slots-and-circling)) |
| damage resistance min / max | -0.65 / 0.65 | toughness resistance range |
| damage resistance randomised | False | present in the file only: the loader never reads it (no such string in the executable) |
| knockout mult 1 / 99 | 3 / 0.75 | KO time scale by toughness |
| knockout time base | 20 | |
| min / max toughness ko point | 10 / 85 | |
| degeneration mult 1 / 99 | 1.7 / 0.03 | wound deterioration by toughness |
| bodypart degeneration rate | 1.5 | |
| immediate blood loss, extra blood loss from bodyparts | 0.2, 1 | |
| bleed rate, bleeding clot rate, blood recovery rate | 0.01, 0.0085, 0.4 | stored ×0.1 |
| stun recovery rate | 1 | |
| blunt permanent organ damage | 0 | fraction of blunt that becomes lasting flesh damage |
| exp gain multiplier | 3 | stored ×0.25 = 0.75 |
| skill diff xp 2x bonus / 0x penalty | 10 / 25 | |
| xp rate toughness, strength, from walking, athletics | 1.33, 0.5, 0.5, 1 | athletics stored ×0.5 |
| XP rate medic 1 / 99 | 15 / 150 | |
| weight strength diff 1x / max, min strength xp mult | 20 / 1, 0.1 | strength XP from carrying |

### Objects

| Object | What | Where |
|---|---|---|
| `GunClass` | the firing data of any gun (29 virtual slots); base of the next two | loader `FUN_14043baa0` |
| `GunClassPersonal` | a crossbow's gun, owned by the `Crossbow` item | loader `FUN_14043c180`, made by the item constructor `FUN_140898c90` |
| `GunClassTurret` | a turret building's gun (GUN_DATA; source file `rangedCombat.cpp`) | `FUN_14043cdb0` |
| ranged controller | one per character (`Character +0x2f0`): skill, target, aim point, reload timer, fire flags | tick `FUN_140438c70`, fire gate `FUN_14043aee0`, reload `FUN_140437ce0`, target choice `FUN_140437f40` / `FUN_1404374d0` |
| `Harpoon` | one bolt or harpoon in flight or stuck: a plain struct, pooled by `HarpoonManager` | spawn `FUN_14043a2f0`, per-frame update `FUN_14043afe0`, flight `FUN_1404380e0`, hit `FUN_140439c90`, landed `FUN_14043acf0`, recycle `FUN_140436c10` |

### Record fields

**Base-game data** (**Verified** by reading `gamedata.base`, `Newwworld.mod`, `Dialogue.mod` and `rebirth.mod`; the counts agree with "Data facts" below): 7 CROSSBOW,
6 GUN_DATA. Every crossbow has `num shots` 1, `aim speed` 1 and the three target multipliers 1. A crossbow's shot speed is the quality lerp of `shot speed` and `shot speed 1` with no clamp
(**Verified**, `FUN_14043c180` overwrites the loader's ≤ 0 → 1 value at gun `+0x18`; every shipped crossbow is positive). Meitou: `CrossbowData.At`.

| Crossbow | pierce min 0/1 | pierce max 0/1 | range 0/1 | reload min 0/1 | reload max 0/1 | deviation 0/1 | perfect skill | shot speed 0/1 | ammo (charges) |
|---|---|---|---|---|---|---|---|---|---|
| Ranger | 20/30 | 30/45 | 600/800 | 3/2 | 4.5/4 | 5/3 | 60 | 400/500 | Bolts [Regulars] (30) |
| Eagle's Cross | 50/60 | 70/100 | 1500/1900 | 5/4 | 10/8 | 2.5/1.5 | 80 | 700/800 | Bolts [Longs] (10) |
| Tooth Pick | 10/15 | 20/25 | 375/500 | 2/1.25 | 3/3 | 11/6 | 90 | 350/450 | Bolts [Toothpicks] (40) |
| Junkbow | 10/15 | 20/24 | 300/400 | 3/2 | 4/3 | 12/6 | 90 | 350/450 | Bolts [Toothpicks] (40) |
| Oldworld Bow MkI | 20/35 | 35/50 | 800/1100 | 3.5/2.5 | 4.5/4 | 4/2.5 | 70 | 500/600 | Bolts [Regulars] (30) |
| Oldworld Bow MkII | 35/45 | 45/60 | 800/1200 | 4/2.2 | 8/6.5 | 4/2.2 | 80 | 500/600 | Bolts [Regulars] (30) |
| Spring Bat | 30/50 | 40/80 | 400/550 | 4.5/3.5 | 6/8 | 8/4 | 70 | 500/600 | Bolts [Heavies] (20) |

### Shot origin and flight collision

**Observed** (decompile and disassembly). Origin: the fire gate (`FUN_14043aee0`) refreshes the gun (slot 2 `FUN_1404351f0`) from the crossbow's scene
node in the shooter's `hands` (`Bip01 Prop2` with the attach offset; without built meshes the character's position + (0, 10, 0)); the muzzle (slot 9
`FUN_140434fc0`) is that node's world position + its orientation x (`barrel pos Y`, `barrel pos Z`, 0), the place of loaded bolt 0 (base game: (-1, 5.9, 0),
Spring Bat (-1, 1.75, 0)). The direction is normalise(aim point - muzzle) (slot 12), the aim point the target's `Bip01 Spine2` (`Spine1` for some targets)
plus a lead of target velocity x distance / `shot speed` (first order, **Verified**), tilted by `randomDeviant(random x deviation)`. Flight: each frame a ray from the bolt along its z on the physics
thread (raycastClosestShape, group mask `0x087fde47`, read one frame late); a hit counts within 2 x the step. Groups tested: 0 and 1 (0: the characters' click hulls, below),
2 terrain, 6 and 14 foliage, 9-12 building floors, 15-18 stairs, 19-22 furniture, 27 unwalkable roofs; not ragdolls, items, doors, interior hulls or
passable parts. The shooter is skipped (`FUN_1400cd0b0`). Meitou: the simulation fires from a fixed point 15 up and 8 ahead (`RangedTuning.Barrel*`, measured
from the posed muzzle) and tests the click hulls, the terrain and the buildings' collision (`SolidHits`; no foliage); the renderer draws a new bolt from the shooter's posed
muzzle and closes the gap to its simulated flight over 150 units (`GameHost.LaunchBlend`).

### Arms

**Observed** (decompile and disassembly). The medical object (Character +0x458) holds two arm-usable bytes, right +0x5bd and left +0x5be, set every tick
(`FUN_140649320`): an arm is out when a part whose `collapse part` has its bit (2 right, 4 left) is a stump or crushed, or is down (flesh - stun + rig < 0;
`FUN_140644450`); an original or fitted prosthetic limb passes. Planner conditions 38 `hasOneWorkingArm` (either) and 161 `hasTwoWorkingArm` (both;
`FUN_140998470`, `FUN_1409984d0`). Weapon categories 3 Heavy, 6 Bow (every crossbow, `FUN_140898c90`) and 8 Polearms need both arms; the rest only the right
(`FUN_1409991d0`, weapon choice `FUN_1405c97a0`). Without both, the shooting gate (`FUN_1407f2840`) fails: attack orders stay melee (`FUN_1407f9e20`) and the
Shooting tasks 262/263/266 (condition 161) are not planned; an arm's change puts the held weapon away (`FUN_1405cc230`): a two-arm one on either arm, another
when an arm it needs is out. The melee weapon drawn (`FUN_140345c10`; hip and back are the first item of the `hip` and `back` sections, `FUN_140747c30`,
`FUN_140747e00`): right arm out, none (fists); right only, the hip one; both, the back one unless it is a crossbow, else the hip one. The hip one replaces a drawn
back one only when it rates higher by Item vtable +0x378, which returns 0 for every WEAPON and CROSSBOW (`FUN_1408a52d0`), so never. **Observed**, not built:
with both arms, `FUN_140334250` keeps the weapon held instead of drawing the back one; it is true when the character's location resolves to a building whose
+0xc2 byte is 0 (indoors?). Meitou: `MedicalState.ArmWorks` (also carrying and swimming); `Fighter.Weapon` takes back or hip by the arms each tick
(`Fighter.SetArms` in the combat Act, a change redraws), the appearance gives the first hip and back weapons both mesh sets (`WeaponShown.Back*`, snapshot
`BackDrawn`); the crossbow needs both arms (`CombatSystem` ranged switch) and the planner conditions read them.
A two-arm category at the hip with one arm: **Verified** (`FUN_140345c10`, `FUN_1405cc230`, `FUN_1405c97a0`) the drawn-weapon choice never tests the hip weapon's category (only the *back* weapon of
category 3, 6 or 8 is dropped for the hip one in `FUN_1405c97a0` when an arm is out), so a hip heavy weapon or polearm is drawn again after an arm is lost; `FUN_1405cc230` only sheathes a held category 3/6/8
weapon at the moment an arm changes (the change's mask bit 2 with the left byte clear, bit 4 with the right byte clear). Meitou: the hip weapon with the left arm out (`Fighter.Weapon`), and on an arm's change a block
in progress ends when the weapon goes (`CombatSystem.Act`, step 1). **Drawing is not timed**: equipping (`FUN_140346c30`, the Equipping weapon task 6 / 264, planned before Attacking 227, whose list needs
`hasWeaponEquipped`) sets the weapon in hand at once through Character vfunc 0x3d8 (the hip/back mesh refresh `FUN_1405cbf90`) with no wait for a clip, so a fighter never swings sheathed. Meitou: `Engage`
and `StartAttack` set `DrawnWeapon` to the weapon of the hand before a swing, and `CharacterAssembly.RefreshEquipment` and the arms' change update a weapon already drawn (a weapon picked up or lost in a fight left it stale).

### Unknowns of the ranged path

**The ranged-or-melee switch** (**Observed**; no single function decides it). The Ranged order button sets a per-character flag (Character `+0x450` object `+0x129`,
`FUN_1405caa50` id 0x11, from the panel's click `FUN_140721550` through `FUN_1407f3880`). The shooting gate `FUN_1407f2840` needs it, then both arms working, a ranged weapon in the
hands or at the back, and `vfunc 0x368` of the character zero (the player-side test `FUN_1405caa50` also uses). Where the gate holds, the player's attack orders (5, 0x10, 0x3d) become
the shoot orders 0x106 / 0x107 / 0x10a (`FUN_1407f9e20`), i.e. the Shooting tasks 262 / 263 / 266 ([ai-tasks](ai-tasks.md)), whose list A needs `!iAmBeingMeleeAttacked immediate`,
`hasTwoWorkingArm` and `hasWeaponEquipped`; where it does not, attack orders stay melee. The attack-on-sight post and area test `FUN_140598630` skips its 80 / 140 unit melee
reach limits when the gate holds, so a Ranged-on crossbowman engages from afar (Meitou: `Orders.RangedExempt` in `AggroSystem`; a crossbow on the hip, a crouching or one-armed holder does not skip it). Nothing switches back by the target's distance: melee comes from the planner
dropping the Shooting task when a threat is within 40 (`immediate`, [lifecycle](#fight-lifecycle-and-orders), which also has the shooting desire). Meitou: `CombatSystem.ChooseMode` follows that (crossbow unless dry, crawling (the gate's prone test) or a threat
is within 40; never with the flag off; NPCs always on). The Shooting rows' desire reads the flag too; the `CharStats` constructor sets it on for every character (the +0x128 dword = 0x100), and only the player's standing order and the save change it, so NPCs are always Ranged. **Reload survives a fight flip** (**Observed**, decompile: the task end `FUN_140437470` and the target setter `FUN_1404371f0` do not touch the controller's reload flag/timer `+0x74..+0x80`, only `FUN_140437ce0` does): a target lost and found again, or a switch to melee and back, resumes the reload where it stood. Meitou resets only the aim state (#193). A shooter whose line has a friend in it holds fire for ever (`FUN_140437ad0` result 2, **Observed**): a 100-strong block mostly stands loaded and blocked, as vanilla. Other unknowns: the reload animation against the timer (the timer paces the shots, so only the clip's look is open); whether the crossbow's bleed and target multipliers and the armour's `ranged skill mult` do anything; how NPC crossbowmen get their bolts.
(The trigger flag `+0x34` is set by the controller tick itself every tick, so a shot leaves in the frame of its decision: [Turrets](#turrets), **Observed**.)

## Unknowns

- The roles of hunger and "fed" in recovery (the `+0x60`/`+0x64` pair is the hunger level and stomach buffer, [character-stats.md](character-stats.md#hunger-and-starvation)). KO wake-up and the KO point: [character-stats.md](character-stats.md#blood-and-ko).
- Whether the decompiled maths matches in-game numbers: nothing has been compared in play.

## Expression reference

- Combat: `lerp(t, a, b) = a + (b - a)·t`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Turrets (**Observed** from `GunClassTurret`, `TurretBuilding`, `Task_UseTurret` and the ranged controller; damage loader **Verified**; GUN_DATA and turret BUILDING fields **Verified** by a probe of the install)
