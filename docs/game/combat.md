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

**Verified** (decompile): inside a building the weapon's `indoors mod` (CharStats `+0x28`) joins the equipment bonuses. The attack
skill (`FUN_140884ee0` → `FUN_1408844c0`; fists `FUN_140884e00` → `FUN_140884580`) takes bonus `+0x20` (fists `+0x2c`), drops it to 0
when it is positive and the modifier negative, adds the modifier, then subtracts the dust penalty; the block contest's defence
(`FUN_1408862d0`) adds the modifier to `+0x24`. Inside means the character's location type is not 0xb (outdoors), vfunc `0x1e0` is
false and the building's `+0xc2` is clear (the last two **Unknown**). The modifier is refreshed with the weapon (`FUN_140884370`, **Verified**): the bound
weapon's `indoors mod` (weapon `+0x250`), looked up in the held item, then the `back` and `hip` sections; with none it is 8. A person's fists are the `FISTS` item
(18 long, `indoors mod` 8 in the base data, as the robots' `FISTS- robo`), so an unarmed fighter has +8 inside. Meitou: `CombatSystem.IndoorsMod`, inside = in a
building's indoor volume (`SightMap.Inside`); the weapon's mod, the fists item's (`Fighter.Fists`) with none, 8 with no item. The fist rating (`CombatSystem.UnarmedRating`) takes it
in the damage packet, the attack rating and the defence rating alike; a door bash (`LockSystem`) does not (its packet's flag is not traced).

**Verified** (loader `FUN_140888ca0`, record reader `FUN_1406684c0`): techniques not `disabled` go to an attack
list unless `is block` or `is dodge` (then the defence list), in data order. Each keeps a per-category table:
`katanas` 0, `sabre` 1, `blunt` 2, `heavy weapons` 3, `hackers` 4, `unarmed` 5, `bow` 6, `polearm` 8; with
`animal` > 8 only that category. `1 handed` is not read here (only by `FUN_1405ba280`). `power N` is stored ×0.01.

### Attack technique choice

**Observed** (`FUN_1402b4570` → `FUN_140888030` → `FUN_1408876d0`, limbs `FUN_140886030`):

- Gap = |aim − attacker| − 1.5·r·k − lerp(k, 0.45, 1)·r_t, at least 0. r, r_t: `pathfind footprint radius`; k: slot
  0x390 (1 for people). Aim: target + velocity × 1.2 s when its movement is plain mode 0, else its position.
- Pass: category = weapon's (5 for fists), skill = stored category level. If empty and not an animal: category 5 at
  `FUN_140884e00` = lerp(e, 0.2, 1) × (martial arts + CharStats +0x2c bonus − injury term) × slot 0x398. If still
  empty: that again, accepting hurt limbs. e = encumbrance factor (CharStats +0x190, 1 unburdened).
- Filter: category table; `min skill` ≤ skill ≤ `max skill`; (1 − e)·100 ≤ `max encumbrance`; `is prone` = posture ≠ 0 (always, **Verified** in the disassembly of
  `FUN_1408876d0`: the decompile prints the burden test and the prone test as `<` or `==` and prone, but the code rejects on burden above the maximum, then on a prone mismatch);
  limb factor f > 0, and f ≥ 1 except in the last pass.
- f = product over blows. `limb` 4 (weapon) 1; 0 left arm, 1 right arm: 0.001 if not working (+0x5be / +0x5bd).
  Legs 2/3: prone → technique out; flag +0x5b8 or a leg part down → ×0.25. A missing limb
  (`MedicalSystem::getLimbState` `FUN_140643b30` = 1 STUMP, **Verified**; 0 original, 2 replaced, 3 crushed) → 0.001. No blow above 0.01 → 0.
- Reach R = `attack distance` + L/2, L = weapon `length` (CharStats +0x200). The max R of
  filtered techniques, at least 1, is CharStats +0x150. A technique needs gap ≤ R.
- **Fists' L** (**Observed**, decompile): a person's fists are a WEAPON item (`FUN_14062acf0`: its race's `fists lv1`
  reference, else the `FISTS` record), made the current weapon with nothing in hand (`FUN_1405e58d0`); the weapon bind
  (`FUN_140897f30`, which has a category 5 branch) writes +0x200 from the bound weapon's `length`. Base data: `FISTS` and
  the robots' fists (`53668-rebirth.mod`) are 18 long; no other race has `fists lv1`.
- Weight: c = min(a, b)/max(a, b), a = gap + 3, b = k·(`attack distance` + L/4) + 3. Multiply by f, by 0.02 under
  `attack distance min vs static`, by 0.2 for the previous technique unless the target is in state 8, and by `chance`.
  Pick by cumulative weight (`FUN_1408a8c80`, `FUN_1409b1c80`).

Consequences (base data): "Cut left static" (−999) is never an attack. Fists reach `attack distance` + 9, so
"ma badpunch" (−4, the only punch up to martial arts 20) needs a gap of 5 or less. An armed fighter beyond every weapon reach may kick or punch.
With L = 0 the attack zone (below) falls 2r short of the start gap, and a fist swing begun where the steering parks
the fighter hits nobody (**Measured**, Meitou headless, base data: martial arts 100 against 5 Dust Bandits, 5 of 39
swings landed with L = 0; with L = 18, 7 hits from 6 swings put all 5 down in 7.4 s).

Meitou: `TechniqueChooser.ChooseAttack`. Engine readings: CharStats +0x2c is the gear's unarmed bonus; the injury
term, +0x5b8 and part kind 1 are not modelled. Leg down means a leg part's flesh − stun + rig < 0. Plain mode means
a target that is not fighting. Fists: `Fighter.Fists` (its length here, its multipliers in the packet). The gap is 3D.
Meitou fix (Fixes page `Weak punch reach`, on by default; `CombatSystem.PunchReachFix`): a fist attack whose `attack distance` is
below 0 and above −100 reads it as positive, so "ma badpunch" reaches a gap of 13 like the other basic punches; off is the game.

## Hit reactions

**Verified** (`FUN_1404391f0` from `FUN_1404394f0` on a hit): damage = cut+blunt+pierce+stun against the
stumble threshold. A stumble clip is picked unless the hit is light and one is already playing; it is big
only when heavy and none was playing; front/rear by a dot test (`FUN_140435600`). Humanoids only. "Heavy_Hit"
/ "Light_Hit" are only the Wwise "Impacts" switch (Heavy = a big clip). A picked clip cancels the defender's
own technique (its blow never lands) and holds it in the stumble state until the clip ends (`FUN_140665490`,
`FUN_14060cf60`, `FUN_140435570`); with no clip the technique is still cancelled. The break-out timer (`+0x124`, 1.2 s from the stumble's start, `FUN_14060cf60`)
counts down each tick; once due, the first tick a swing at 10-99% progress is incoming (`+0x120`, the incoming list is non-empty) rolls the stumble break-out (`FUN_1406085e0`, chance `FUN_140885d80`);
a failed roll sets the timer to 1.2 s again, a tick with no such swing leaves it due (**Verified**, decompile). Meitou: `CombatSlot.BreakOutTick`.
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
or the wait's end releases it; a swing stopped at a stop frame (attacked, `FUN_14060a590`) decides again at once instead. Meitou: `CombatSlot.PauseUntil`, `CombatTuning.SwingFinished`. Before swinging, an attacker in reach asks its target
(`FUN_14060c670` → `FUN_140666a30`): target swinging at it → the attacker blocks; target stumbling or blocking →
attack; otherwise an initiative roll on both attack skills (`FUN_140665370`, the skill roll above). If the asker wins,
the target blocks. If the target wins, the asker is refused; with no free slot it is refused too, and waits
(see [Attack slots and circling](#attack-slots-and-circling)). `max simultaneous hits` caps one blow's targets ([attack zone](#attack-zone));
`power N` multiplies hit N's packet; `limb N` picks the punch/kick sound.

**Verified** (defence timing and multiple attackers; `FUN_140665cf0`, `FUN_140665e00`, `FUN_140667b40`, `FUN_1406083d0`, `FUN_140609c70`, `FUN_140665fa0`). A swing's *progress* (CombatClass +0x140) is the
attack clip's progress divided by the `anim blocked frame` fraction of the blow to come (idx = blows passed), so it reaches 1 at the blow's impact, not at the clip's end; 1 once every blow has passed;
-1 stumbling, -100 down. Every progress test of the defence reads it: an incoming blow counts when 0 < v < 1; the stumble break-out window is 0.1 <= v < 0.99; a swing is cut short for a defence
when the swinger's own v is under 0.5 and an incoming blow's v is 0.5 or more (so for a katana whose blow is at 0.4 of the clip, a defender aborts at 0.2 of the attacker's clip); a target "swinging
at the asker" (the ask's refusal) is 0 < v < 1, so the asker may swing once the target's blow has landed. **Who is incoming**: the defender's list (+0x220) is rebuilt each tick (`FUN_140667b40`) from its engaged
list (attackers that asked it, i.e. that target it) and its declaration list (an attacker pushes itself on its own target's list every tick of a swing, `FUN_14060bcf0`), keeping those with 0 < v < 1 that have it in
their hit candidates or target it. So a defender reacts only to attackers that target it (a blow from one that targets someone else is met only with a defence already playing), and to the
one whose blow is most advanced (largest v). **One technique at a time** (+0x150): the outcome (`FUN_140665fa0`) compares the current block's direction with the blow's, whoever swings, and needs the clip past 0.5, so
one block covers every later blow of its direction while it plays; the defence update re-picks (`FUN_140609290`) when the most advanced incoming swing changes, its blow index changes or the 1 s timer
(+0x274) runs out, only for a non-dodge technique whose direction differs from the new blow's; a failed new start ends the defence (state 4, no defence for 1.2 s). Dodges are never re-picked. There is no
limit on blocks other than this: a fighter blocks one blow at a time, its swing or wait excluding it. Meitou: `CombatSystem.SwingProgress` (before this the clip's own progress was used, so
a swing was never cut short for a blow before its impact: a skilled fighter alone against four blocked 17 % of the blows at it in a sandbox battle, 34 % with it), `React(reconsider: true)`.
**Measured** (Meitou `--battle`, sandbox floor, base data, 1800 ticks): 1 v 1 Samurai Heavy 62-75 % of blows blocked; 6 v 6 70 %; one stat-100 Samurai Heavy against 4: 36 % (most of the rest were seen with too little lead for the block clip to pass 0.5, or hit it while it swung, waited or was staggered:
a failed block roll is rare at skill 100). The original's own rate was not measured in play (no original binary run here).
**Measured** (Meitou scripted world, base data, katana, defence 86 with heavy plate and attack 60 against four at attack 62 / 37 / 32 / 98, hit reactions on, 60 seeds, to incapacitation): 70 % of blows blocked
(attack 98: 20 %, its block chance is 40 %; the others 64-79 %). Of the 120 blows that landed: stumbling 41 %, a failed roll that played a block of the wrong direction 35 %, mid-swing past 50 % 14 %, a block started
too late for its clip to pass 0.5 (right after the defender's own swing's wait) 8 %. Without hit reactions 68 % blocked and the late blocks lead (95 of 169 landed). No hit met the 1.2 s no-defence lockout. All follow the rules above; the original's rate was not measured.

**Observed** (decompiled, not measured in game), with several attackers: a target answering a request is asked by
anyone, not only its own target, and the winner of the roll turns to attack the asker (`FUN_140666a30`). Deciding
(`FUN_14060c8b0`) tries attacking first and defends only when that fails; a waiting fighter that is attacked goes
to deciding, except in the wait after its own swing (above). During its own swing a fighter aborts to defend only while under 50% of it with an incoming blow past
50% (`FUN_14060a970` → `FUN_1406083d0(0.5, 0.5)`); while anyone attacks it the swing ends at the current blow's stop
frame (`FUN_14060a590`). The defence covers the most advanced incoming blow from anyone; a failed defence roll means
no defence for 1.2 s (+0x14c). A dodge plays its whole clip at its own speed, then decides at once; a block that
ends leaves 0.2 s.

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
- **In it**: a character that is not an ally (`isAlly`), not unconscious (`isLiterallyUnconciousNotPretending`), and an enemy (`isEnemy`)
  or the current target; also a slave (slave state 1) that is not the attacker's ally, when the attacker's faction is not the player's (+0x250
  `isPlayer` null) and not anti-slavery (faction +8). Of the buildings in the box only walls (`WallBuilding`) and doors (`DoorStuff`) take the
  blow, f = 2 × blunt + 0.5 × cut of the powered packet: a door loses clamp(f, 20, 60) × 0.015 of its construction progress (Building +0x164,
  via `getBuildState`; a broken door nothing), a wall clamp(f / 2, 10, 60) × 0.003, destroyed at 0 (**Observed**).
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

**Observed** (decompiled). CombatClass state +0x1f0: 0 attacking, 1 defending, 3 deciding, 4 re-decide, 5 and 6 below,
8 stumbling, 9 no target, 10/11 chase. The planned next state is +0x1f4.

- **Engaged list** (+0x1f8, `FUN_140666e20`): an asker joins its target's list while the list holds at most 4,
  so 5 fighters at most. A refused asker adds to a two-frame overflow counter (+0x210/+0x214, swapped by
  `FUN_14060cc50`). `FUN_14060c670` joins before it checks reach. A blow that lands or is blocked takes its
  defender off the *attacker's* list (`FUN_14060a2f0` → `removeAttackerH` `FUN_1406657d0` on its own +0x1f8; not after a dodge, **Verified**). The fight task's end (`FUN_1406659b0` mode 2, **Verified**) zeroes the leaver's own list count (+0x200) and +0x228 at once; the attackers keep their target and ask again. Meitou: `Engaged` is a flag on the attacker, so a fight's end raises the leaver's `CombatSlot.ListEpoch` (`EndFight`), a fighter stores the target's epoch when it joins (`JoinedEpoch`), and `CombatSystem.Listed` counts it only while they match; the attacker drops `Engaged` in its own Act and asks again in the same step when it is free to decide.
- **Slots** (+0x8, `FUN_140666310`): count = clamp(`max num attack slots` + RACE `extra attack slots`, 1, 5),
  `FUN_1406661d0`. It is 1 while the target is attacking (state 0) or stumbling (its AnimationClass +0x448 has a current stumble clip +0x228; **Verified**).
  The CombatClass constructor also clamps the CONSTANTS value to 1..5.
- A slot is granted by `FUN_140666a30` and kept until the swing's last blow (`FUN_14060a590` → `FUN_140665910`).
  Hitting someone removes them from your own slots.
- **Slot timers** (`AttackSlotManager::periodicUpdate` `FUN_140668000`, **Verified**; dt from the AI update, seconds, game or real **Observed** only): each held slot's timer
  grows; past 5 s the holder keeps it only while it resolves and `isAttacking` the owner, past 20 s never. Quirk: the release always empties slot 0's hand and never resets slot i's
  timer, so a stale holder in slot 1-4 keeps its slot and evicts slot 0's holder each update. Meitou holds a slot only for one swing (shorter than 5 s), so the timers never act.
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
- **Mass combat XP** (`FUN_1408c5e80`) therefore fires on the *target's* stats, once per ask answered in those three cases: being hit at while
  stumbling or already fighting someone. It adds exp gain multiplier × 0.05 to the save-only stat (no consumer found), so it changes nothing
  visible; Meitou does not call `XpService.MassCombat` (the stat is not read anywhere).
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
  - a player-faction chooser drops candidates beyond 400 (its crossbow range with Ranged on); on Hold ×0.01 beyond 40;
  - when the Hold test `FUN_140598630` passes: + range term (×20 for a crippled chooser), + murderous (−2) for a candidate whose fight is off
    (then the whole score ×0.5 when its intended aggression, vfunc 0x1e8 `getIntendedAggression`, is ≤ 0), the whole score ×0.1 while it gets up
    (+0x278), ×3 for a turret gunner when the chooser is not an animal, −10 when crippled or limbless (`MedicalSystem::isUselessNoLimbGuy`);
    otherwise the candidate is dropped. A candidate whose path has failed (`AITaskSytem::isPathImpossible` `FUN_1405b0290`: the chooser's
    `pathFailures` map, AITaskSytem +0x218, where `notifyPathImpossible` `FUN_140796ed0` stores 20 when a path to it fails) skips these last
    terms and is never picked. (**Verified** order and factors in the decompile of `FUN_14059e300`.) Meitou: `PursuitSystem.PathImpossible`, a
    failed pursuit path leaves its target out of the choice (a player's pick too) for 20 s. **Observed**: the 20 counts down in the AI's 4-frame update and, at 0, is re-armed to 20 while no path exists, else dropped (game or real seconds: **Unknown**). Meitou reads seconds and asks the walkability for a path on the spot when one lapses (`PursuitSystem.PathExists`, while the fighter pursues).
    **Verified** (decompile): the path chase is where the failure is reported, for NPCs and the player alike: `CombatClassAI` update `FUN_14060d6a0`, in states 10/11, sets the movement's destination to the target each tick and, when the movement then reports no path (its vfunc 0x48 false), calls `notifyPathImpossible` on the target and picks again (`FUN_140666ee0`); `AITaskSytem::update` `FUN_14050d970` does the same for a task target the movement cannot reach, and ends the task. **Unknown**: what vfunc 0x48 tests exactly (read as "a path to the target exists"). Meitou: the walkability answers an unreachable goal with the way to the reachable point nearest it (a steep slope, a roof, a closed building, an island), which always "exists"; before, the fighter walked to that point and stood there on the attack task for good (**Observed**, scripted box and the Hub's real navmesh, NPC and player's pick: 14 of 21 steep-slope runs stood so, 40 s). The swing starts at a 3D gap (`CombatTuning.Gap`) of at most the technique's reach (`CombatSlot.Reach`, 20.5 for the test katana), while `PursuitSystem.Pursue` stood "in reach" on the horizontal gap alone, so a fighter at the foot of a slope or ledge stood in horizontal reach with no swing. Now `Pursue` stands only when the 3D gap is within the reach too, and a path whose end leaves a 3D gap over the reach (`PursuitSystem.EndsShort`, measured to the target's place when the path was asked, `CharacterCold.PursuitGoal`) counts as no path: the failure is noted at once (the fighter does not walk to the nearest point), the choice leaves the target out and the fight ends or turns elsewhere (under a second on the slope); the 20 s re-check (`PathExists`) uses the same test. The path request also takes the target's height as its floor (`CharacterCold.GoalHeight`), so a target upstairs is walked to up the stairs, not to the ground under it. Measured on the Hub (zones 20.32/21.32, 25 reachable goals each on the street level, upstairs and ahead of a running target, plus the Storm House floor): none dropped.
    The attack-on-sight look (`AggroSystem`) and every order that is not the player's pick (`CombatSystem.Engage`: mates called in by a sighting, retaliation)
    leave it out too, since each finder scores with this function; the player's pick is dropped at the next pick. Without that, a guard shut in with its enemy
    (a cage, a locked room) was called in again at every look and never turned on anyone it could reach (**Observed**, Meitou: a cage shack in United Cities,
    one fighter stood 7 s at the nearest spot; 0.5 s of that after the change).
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
- **First path**: a fight's pursuit path is asked for at once, and again at once on a new target (`CombatSystem.Engage`, the re-pick). The repath wait
  in `TaskTime` (`PursuitSystem`) is not the idle or wander wait (3 to 10 s) a character was in, nor the 2 s wait after a failed path to the old target; left in it a
  fighter stood still for that long before its first step (**Observed**, Meitou: 25 to 79 ticks at 30 per second at the start of a fight, 3 ticks after the change).
- **Joining**: a lower table slot asks first; one not yet in is counted ahead only when it may decide this tick (not staggered,
  defending, or swinging past its abort point), so nobody is left out for a fighter that does not ask (engine choice).
- **Waiting**: fighters left out, and engaged ones in the wait after their own swing (state 6, [Pacing](#pacing)), keep the state-6 band by the [fight steering](#fight-steering); none orbits.
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
- **Joining a fight** (**Observed**: fights are plan rows of priority-3 goals, `chooseGoal` `FUN_140510ef0`). No rule outside the goals adds an NPC to a fight: it joins only by a combat
  goal of its lists in their hours, each with its own finder ([combat goals](ai-tasks.md#combat-goals)): 16 / 21 / 144 attack on sight, 143 for a squad mate in a fight, 15 / 288 for a
  sensed ally in one (valued by its fight threat, through row 13 within 150 of the one protected), 34 against an aggressive enemy, or a neutral attacking an ally, in the looker's town. A squad
  shares sightings ([perception](perception.md), `FUN_140857b10`), not attacks. So a vanilla shopkeeper (143 only) follows its guards (143 and 34) into a fight only once one of them is in it,
  against that guard's foe within 150 of it.
- **Being targeted.** Choosing a fight target (`CombatClass::setAttackTarget`, `setAttackTargetHandle`) calls `AI::underMeleeAttack` on the
  target at once (through `Character::attackingYou`), before any blow; dropping it sends "stop", which only removes the attacker from the
  target's engaged list. A landed blow (not blocked or dodged) on a defender whose fight is off sends it too (`FUN_1404394f0`). The call ends the
  target's sneaking, adds the attacker to its engaged list, to its senses (seen, alert timer 5) and to its squad memory as TEMP ENEMY (kind 4,
  not for allies; the whole squad then takes it for an enemy, [squad memory](dialogue.md#squad-memory)), tells a campaign, and when the target's fight is off: unless it is getting up, or is player-side and moving (CharMovement
  +0x24, `Character::isWithThePlayer`), its current goal is cleared and it replans (`AITaskSytem` vfunc 3); a target getting up instead has a
  10% chance of its knockout timer (+0x4f8 = MedicalSystem +0xa0) being set to 10.0: knocked down again for 10 s (**Verified**). The targeting path sets the call's last flag (r9b = 1 in the disassembly), the hit path
  clears it. With the flag, when the target is the player's, or the attacker is the player's and the target is not yet aware of it, the call
  is ignored unless both are in the same building and on the same floor and the target has a clear line to the attacker. So targeting a
  player's character through a wall, or an unaware NPC from another floor, alerts nobody until a blow lands. Meitou: `CombatSystem.Alerts`, taken by
  `RetaliationSystem` the same tick (it ends sneaking, makes the attacker a threat of the target at once, `AggroSystem.Force`, and asks an NPC's AI to think at the next tick); same place by `SightMap.PlaceOf`, same floor as within 20 units of height inside, a terrain and
  building line between the eyes; aware = fighting the attacker or hit by it (engine readings). The 10% knock-down: `CombatSystem.UnderAttackGettingUp`, from the alert and
  from a landed blow outside a fight. A landed blow or bolt on an NPC with its fight off, not getting up, asks its AI to think again (`CombatSystem.HitReplan`).
- **Threats.** The perception pass (`SensoryData::addToThreatsAndFlockingList`) looks at each sensed live character of another faction: its
  state broadcast's intended aggression adds to +0x90; when its intended target (+0x68) passes `AITaskSytem::intendsToAttackTarget` (prone
  state < 3 and its fight on; on Hold with the fight off, only within 40), a target that is the looker puts it on the threats list
  (SensoryData +0x50) and adds its aggression towards the target to +0x88; a target of the looker's faction adds that to +0x8c.
  `iAmBeingMeleeAttacked general` (state 159) is "threats list not empty"; `immediate` (158) is Character +0x2b0, set in the periodic update
  (`FUN_1404378f0`, **Verified**) while a threat within 40 (squared 1600, `DAT_1416d4a28`) stands (prone state < 3: staying low and crawling count),
  is not in a bed or cage, not getting up (+0x278), not carried and fails `AI::isTargetOutsideTheGates` (`FUN_140598450`: both places' gate codes
  known and different and the route between them not open). Meitou: `CombatSystem.ImmediateThreat` over the senses' threats list (`SenseBlock.Threats`, as of the last look;
  the gate test is `LockSystem.NotBlocked`; getting up not modelled).
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
  focuses on non-combat unless attacked first"). Meitou: [order toggles](../simulation.md#order-toggles).
- **Melee or shooting.** Row 4 Attacking (`AI::scoreMeleeAttack`, slot 53): 1, ×0.1 with Ranged on, ×0.5 when its squad is not intact (`Platoon::isIntact`);
  0 against a caged target; a crippled fighter 0.1 (0.0001 in Block or Hold with no threat within 40; 0 for a non-player non-unique); on Hold
  only for a target its range tests accept. Shooting 262/263/266 (slot 54): only with Ranged on, a crossbow at back or hip and its bolts carried:
  1 (0.5), 0.1 with a threat within 40, which their list B forbids anyway. So Ranged on shoots until dry or rushed, Ranged off never shoots.
- **Fight task.** Rows 4 and 227 build `Task_MeleeAttack`; orders 5 Attack Target and 61 Attack Unprovoked build `Task_FocusedMeleeAttack`.
  The planner first needs 37 `isReadyForAction`, given by 59 Get ready after 6 Equipping weapon draws (at once: its task swaps the
  weapon in one call, **Observed**; Meitou draws when the fight starts). Task start → `initCombatMode` (mode 0):
  CombatClass +0x130 on, state 4, attack start position, the technique cleared; the focused task also stores the target as CombatClassAI
  +0x2c8 and (with its +0x98 flag) marks the broadcast as aggressor (+0x114). Task end → `Character::endCombatMode` → mode 2: +0x130 off,
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
  Stumbling task ends on its first update. A fighting defender stays in its fight's stumble state instead.
- **Actions mid-fight.** Any order runs during a fight. First aid 25, splint 249, repair 60 and pick lock 76/201 test no threat in their rows
  or task updates, so they run while their character is attacked (after sheathing), but every landed blow cuts them off with a stagger and
  the order is planned again afterwards (walk, sheathe, act; what the action keeps of its progress is per task, not traced). Meitou: move, follow, First Aid, Splint, Rest, the Medic and
  Rescue buttons, Pick up, Kidnap, Loot, Stealth KO, Put down, Operate, Prospect, Build, Repair, Dismantle, the bed, lock, shackle, cage, door and force orders, item pick-up,
  containers and Put in cage end the fight (`TaskStart.Start`,
  [simulation](../simulation.md#task-starts)) and act (whether the rows of Pick up, Rest, Build 2, Repair 95, Dismantle 96 and Operate 87 test a threat is **Unknown**). The Loot order (26, `Task_Loot_Order` update) ends when a
  threat is within 40 (+0x2b0). Rows refused while a threat is within 40: Shooting, 146 Turret gunner,
  123/290 Bashing gate, 272 Smashing, 164 Following and talking; 248 Attacking Squad needs no threat at all. There is no pickpocket verb on a
  standing character: stealing from a person is Loot on one that is down or asleep ([verbs](ui-input.md#context-menu-verbs)), after a Stealth KO
  ([below](#stealth-knock-out)) or a knockout.

**Standing orders** (`Character::setStandingOrder`, player faction only; ids are KenshiLib's `StandingOrder`, `M_SET_ORDER_*`):

| Id | Order | Effect |
|---|---|---|
| 0, 1, 2, 16 | run, jog, walk, group speed | movement speed mode; a crouching (prone state 1), uncrippled character stands up instead |
| 3, 4 | stealth on / off | Character +0xd4; Task_Follow rewrites a follower's every update (`FUN_14033b350`, **Verified**): on while its leader sneaks out of a fight and is its ally, else off (Meitou: `MovementSystem` follow step, ally = same faction) |
| 5, 6, 7 | aggressive / defensive / evasive | AI +0x2b8 (no reader found) |
| 8, 9 | far / near | manoeuvre mode: 8 ROAM_FAR (centre target cleared), 9 STAY_CLOSE (centre at its position) |
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

**Observed** (static reading; the animation's combat flag is `AnimationClass +0x244`, the value a clip's `is combat mode` is compared with, [animation](../animation.md#choosing-candidates--14051d660)). One setter, `FUN_14051cc30`, writes it. Set on: every tick of the fight's attack update (`CombatClass::vfunc_11`), and by the combat footwork (`FUN_1402ae1b0`, from a movement flag, not from the target). Cleared by: any new movement goal (`CharMovement::vfunc_18`), the end of the fight task (`CombatClass::vfunc_2`, mode 2), and the chase branch of `CombatClassAI::vfunc_9` (fight states 10 and 11, which also put the movement back to its plain mode 0; every other state sets movement mode 1). When a movement stops (`CharMovement::vfunc_19`) the flag is set to whether the fight task is active (`FUN_140436610`, `CombatClass +0x130`, set at task start). So the stance follows the fight's own steering and is off while the character walks a path to a goal, including a chase of its target along a path (states 10/11). `CombatClassAI::vfunc_9` sets `+0x58` each tick when the navmesh line query between the two (`FUN_1403a56e0`, 1 clear, 0 blocked, −1 failed) returns 0; that, or the two being in different places (a vtable `0x60` id of each differs), or the 2 s chase timer `+0x14c` still running, keeps a fighter already in the chase states 10/11 on the chase (**Verified**: the update skips this test in every other state, so a fighter in its footwork meets a blocked line only when it decides again, `FUN_14060c8b0` in state 4, about every 0.5 s; that step arms the timer to 2.0 when the line is blocked and a hold test `FUN_1402b2b90` is false or the hand at `+0x2d0` is set; the hold branch is not decoded); otherwise the AI picks its target and approach (`FUN_14060c8b0`, state 3) and steers with mode 1.

**Observed** (static reading, the fight steering `FUN_1402af1e0`; its constants are literals in the decompile): the footwork flag `FUN_1402ae1b0` reads is a "running" flag. With gap = distance − (own radius + target's radius), it is set when gap > 90 (> 160 in fight state 6, waiting; Meitou uses 90 in every state) and cleared when gap < 70; then forced on while the target is fleeing (`FUN_1402ae0e0`: target's movement in plain mode 0, speed > 9, for 1.5 s on end; the chaser then aims at a lead point, below). The anim combat flag is `!running`. Speed: up to `min(cap, own max speed)` (the AI's cap is 200, so the max speed), ramping 15 units/s per second; not running it is capped at 18 (16 backing off). So a fighter runs normally from afar and drops into the shuffle within 70 units of its target, distance-driven, whether or not it walks a path.

**Observed** (static reading, `FUN_1402af1e0` → `FUN_1404354d0`; the stat from `FUN_1408866b0` → `FUN_140883a80`): the lead point is the target's position + its velocity (Character vfuncs 9 speed × 10 heading, movement +0xb8 and +0xd0) × `(1 − r) × 2 + 1.2` seconds, recomputed every tick, with r the chaser's CharStats +0x188: its attack play rate (lerp(0.01 (0.6 dex + 0.4 attack), 0.8, 1.2) × G, capped at 1.2, the rate attack clips play at; × 0.8 when `FUN_1405c7fd0(character) == 3`, swimming). So 0.8 s ahead at r = 1.2, 1.6 s at 0.8, more for a slowed attacker; a faster chaser aims past the runner, and since the lead follows the velocity it swings to the new side when the runner turns, and drops as soon as the runner slows under 9 or leaves plain movement (the 1.5 s timer restarts). The steering faces its movement while leading. A second override (`CharMovement::vfunc_28` sets movement +0x168 and a point at +0x16c, the steering's +0x50/+0x54): the chaser heads for that point instead at most 55 u/s, the lead is off, and it clears within 10 units; `CombatClassAI::vfunc_9` skips its chase while it is set. Its caller is **Unknown**.

Meitou (`PursuitSystem`, engine rules): the lead is a path goal, re-pathed when it moves over 12 units (at most every 0.5 s, so a turn can overshoot by ~12 units at a run), put a further 0.5 s of the runner's way ahead (a path ends in a stop and a fighter's speed ramps back slowly) and `PassWidth` 1.25 × the two footprints (clear of the separation radius) beside the runner's track on the chaser's side (bodies are solid, so the chaser passes the runner instead of pushing it along; the original's runs through it). It is used only while the chaser is behind the nearer of the lead point and the swing's aim point (target + velocity × `AimLeadSeconds` 1.2); level with it, it goes for the target and stands once the aim point is within reach, so the runner comes into its swing. Standing is measured to that aim point, as the swing is (stood within reach of the runner itself, the aim point would be out of reach and the chaser idle). A fighter at its path's end asks for the next one after `RepathSeconds` 0.5 (not the wanderer's 3 to 10 s wait). A lead off the navmesh falls back to the target. The +0x168 override is not modelled.

### Fight steering

**Observed** (`CombatMovementController::combatMovementUpdate` `FUN_1402af1e0`, disassembly read for the vector maths; constants read from the binary;
called from `CharMovement::vfunc_11` in movement mode 1 with the frame time, the fight state +0x1f0 and the race's `combat move speed mult`).
Each frame it builds a velocity from forces, not a path:

- **Band** (`combatMovementOffensive` `FUN_1402ae320`, via `CharMovement` `FUN_1402ae900`): each fight state gives a target, near n, far f, a
  "circling speed", `powerThrough` and a speed limit. min = clamp(n − m, 1, f′), max = max(f′, min + 5) with f′ = clamp(f − m, 0, 9999), m =
  +0x110 = CombatClass +0x278 = 1.5 × the raw footprint × the size k (**Verified**: `FUN_14060ce30` rewrites it every tick from `myRadiusX` × the double at `0x1416b3be0` = 1.5, then × Character vfunc 0x390; `FUN_1402ae6d0` copies it; the 1.4 written here before was wrong). Calls: attack start and state 5 (n = 1.4r, f = max' of `FUN_14060cd20`, powerThrough on);
  state 5 escorting (+0x178 → +0x20 set: n = max', f = 50); state 6 (n = N/4 + 15, f = 3N + 25, the drawn side speed); the walk
  of `AttackState.Enter` when no technique is in reach (slot 1 `1402b4af0` → `FUN_1402b4570`: n = 0.7r, f = max'/2, powerThrough; **a swing itself has no band**, see
  [Facing and movement in a swing](#facing-and-movement-in-a-swing)); defending (slot 12: n = r + 7.5, f = r + 19, limit 16, or 18 with powerThrough on a designated
  target +0x2c8); the defaults are 10, 20, limit 200. The **circling speed +0x114 is written but never read** (no read of +0x114 or
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
- **Facing** (**Verified**, disassembly of `FUN_1402af1e0`'s tail `1402afc..`): the heading it writes into the movement's +0xd0 starts as last tick's. Not running (+0xe4 clear) and the
  target not being led (not fleeing): the horizontal unit vector to the look-at character (+0x130, else the target) **at once, no turn rate**; target fleeing (lead point): the velocity's
  direction; running: unchanged. A final override to the velocity direction needs +0x126, which only `FUN_1406628e0` sets (its caller **Unknown**). The drawn node alone turns at a rate
  ([animation](../animation.md#speed-the-animations-see-and-the-drawn-facing)); the heading is what the attack zone reads. Path movement (mode 0) takes the nav agent's facing, also at once.
- **Side steps** ([animation](../animation.md#side-steps)): in the facing's frame (x = facing × −Y), not strafing the side part is ×0.105,
  strafing the forward part is (STRAFE_STATE_DAMPING +0xec = 0.105): movement snaps to forward/back or sideways.
- **Speed**: cap = min(limit, or 55 at a forced point; own max speed); the ramp +0x12c (from 20) rises 15/s up to it; facing-forward
  (dot > 0.5) the ramp, backing (dot < −0.5) 16, sideways 18; not running at most 18. Over the cap the velocity is cut to it.
- **Smoothing**: x and z each through a `MotionFilter` (10 samples, newest first, weights 0.8^k, normalised; per frame).
- The caller then ×speed mult (×0.75 / 0.5 / 0.4 in movement levels 1–3), caps at the animation's max speed and hands it to the nav agent.

Meitou (`CombatSteering`, `Fighting.Steer`, `MovementSystem`): melee fighters in the footwork (not running, not stunned or holding) steer
this way while in the same place as the target with a walkable straight line to it (`CombatSystem.SteersAt`, every 2 units, rechecked
every 0.5 s in the footwork, every tick on a path chase, which a blocked line met at a new pick or in the footwork starts for 2 s at least: `SteerState.PathUntil`, not for a Hold order; engine reading: a different place counts as a blocked line there, the original arms the timer on the line query only); otherwise they walk paths. Engine readings: one band per Meitou fight state (swing, defence, engaged, waiting); the forced
point and leading offsets are not modelled; the separation push and body contact stay on while steering (the repulsion alone, damped to 0.105 and capped at 18, let a packed crowd walk into each other and contact shove them back each tick: they shook); the 26 contributions are one cap for enemies and the rest together, the nearest that push (the original's flocking-list order and which 26 it keeps are **Unknown**), looked for only within the nearer of the flocking reach and the push reach, in a 20-unit circle first, doubled until it holds them (the same bodies as a scan of the whole reach, at a crowd's cost); the smoothing runs per
tick (fewer samples at 0.8^(60 dt) when a tick is longer than a frame) and forgets when steering starts.

Data (**Verified**, the base ANIMATION records through `AnimationLibrary`): with the flag on, the lower clips are the `walk lower combat shuffle` trio (speeds −99 to 50) and `jog lower combat fallback` (50 and up); `walk`/`jog`/`run lower` are combat NO.

**Unknown**: the extra offset the steering adds while leading in a non-zero steering mode (`FUN_1402e9420` at radius + 40, `FUN_1402e9530`) and the side terms of `FUN_1402e93c0`; whether the original also holds a chaser from swinging while behind. When an idle character starts fighting back: [lifecycle](#fight-lifecycle-and-orders). Meitou: its Self preservation engages it two ticks after the alert ([timing](ai-tasks.md#combat-goals)), and `PursuitSystem` keeps the running flag by the rule above ([simulation](../simulation.md#animation)).

## Leaving a fight

How an attacker stops when its target runs. **Observed** (static reading of `Task_MeleeAttack` update `14034ca40`
(thunk `14001681f`), `ChaseMode` vfuncs 0/1 via thunks `140016243`/`140020d1a`, `CombatClassAI::vfunc_9`); literals
**Verified** (decompile). There is no distance leash: the exits are the target going down, nothing left to fight,
and the give-up test of a path chase. Ending the task means `FUN_1400522c0`: it sets the AI task system's `+0x26c`,
which its next update (`FUN_14050d920` → `FUN_14050d7d0`) consumes by dropping the current task and clearing its targets.

- **Target down or held** (`FUN_14060d6a0`, **Verified**): each update, a fight target that is carried, dead or ragdolled (`FUN_1407d1440`: carried flag `+0x3d4`, or the
  body state's `+0x59`/`+0x5a`), or imprisoned (`+0x2f8` 2) while the *fighter's own* fight state is not 8 (stumbling), is dropped, provided the fight state is not 0 (swinging) or
  the attack clip has reached its end point; the fight goes to state 4 (re-decide, kept at 8); when it was also the task's target and refocus is clear the task ends.
  A focused (ordered) target is dropped too. Meitou: `CombatSystem.Act`'s target test drops a target down this tick (after every body step, `BeforeAct`), caged or carried (the
  stumbling fighter skips that step; the swing clause is not modelled, as for a fallen target), and `Consider` leaves a carried one out. The task target is the fight's start target
  (`CombatSlot.TaskSlot`, set by the attack order or goal, kept through retargets); dropping it ends the fight at once, without a new choice (refocus read as clear).
- **Nothing left** (`Task_MeleeAttack` update): the task ends when the fight task stopped (`FUN_1405c9750`), or when all hold:
  the senses (Character vfunc `0x68`) count no enemies (`numEnemies` +0x94 = 0) and no threat to allies (`totalThreatLevelAllies` +0x8c ≤ 0,
  [threats](#fight-lifecycle-and-orders)), nobody is engaged on the character (engaged list + overflow 0),
  the fight has no target (`+0x2a0` none), and the task's own target is gone or down (its vfunc `0x30` true; **Verified** decompile of `FUN_14034ca40`).
  The immediate end above is `CombatClassAI` update `FUN_14060d6a0` (**Verified**: the dropped target equals the task's target and refocus `+0x2e8` is clear).
  Meitou (`CombatSystem.NothingLeft`): a fighter that loses another target, or whose periodic choice finds nobody, fights on without a target (state 9), choosing again every pick
  interval and still defending, until the test passes over its senses ([perception](perception.md#senses): `Enemies`, `ThreatToAllies`), `AttackerCount` and the task target (gone, dead,
  knocked out; carried too, engine reading). Engine choice: a task target with no sense entry counts as gone, so the entry's decay (30 / 60 / 120 units of 2 s) ends the wait on a live
  target the fighter lost; a world without senses ends the fight at once.
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
  - Giving up ends the task, fires dialogue event 60 GIVE_UP_CHASE from the chaser at the target and resets the AI's plan.
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

**Shoot At** (context-menu order 235, offered when the lead mans a turret; [ui-input](ui-input.md#context-menu-verbs)): Meitou's `ShootAtOrder` sets `CharacterCold.TurretTarget` on each selected gunner; the turret's target choice then returns that character while it lives and is free (not dead, caged or carried), instead of scoring candidates, and waits while it is out of range or blocked. The order ends when the gunner stops manning. Engine reading (**Unknown** how the original's 235 binds the target to the gunner's controller).

Controller phases reload → steady aim → fire; target motion/range, skill/injury, equipment and
line of sight determine timing/spread. Bolts retain launch/direction/velocity/impact/friendly-hit
state and consume ammo charges; do not model hitscan. Turret objects and mounted ammo/reload
branches differ from carried crossbows. Player/NPC targeting and friendly-fire training have
separate eligibility. The retained field/constant tables and expression list specify decoded
values; trajectory, aim timing and obstruction uncertainties remain labelled.

## Turrets

**Observed** (decompile of `kenshi_x64.exe`; functions cited) unless marked. A turret is fired through its gunner's ranged controller (`Character +0x2f0`), the same one a crossbow uses.

- **Building and gun.** `TurretBuilding` constructor `FUN_14043cdb0` builds a `GunClassTurret` from the BUILDING's `gun turret` GUN_DATA ("Error: turret … has no gun data"
  without one). The shared loader `FUN_14043baa0` reads `range`, `accuracy perfect skill`, `accuracy deviation at 0 skill`, `aim speed`, `shot speed` (≤ 0 → 1), `num shots`,
  `reload time min/max`, `barrel pos Y/Z`, `live ammo`, `reload anim`. `accuracy perfect skill` goes to gun `+0xc` (`accuracySkillRequired`, KenshiLib 1ce4a16) and is
  never read (**Observed**, 2026-10-26: no read of `+0xc` in any GunClass/GunClassPersonal/GunClassTurret method, the ranged controller or the HUD's accuracy lines;
  the shot, the HUD and the turret tooltip all use `+0x10`, the deviation; the tooltip's skill term differs, [ui-screens](ui-screens.md#selected-building) turret gun lines): dead data, so Meitou ignores it. Loaded shots start at 0 (saved as `current shots`, slots 20/21). GUN_DATA has no damage and no `ammo`
  field (fcs.def `[CROSSBOW,GUN_DATA]` vs `[CROSSBOW]`), so a turret never runs dry.
- **Damage D** = the building's output value `+0x1a4` (**Verified**: loader `FUN_14029e460` = BUILDING `output rate` × functionality `production mult`, ÷ `max operators`
  when > 1; tooltip `FUN_14030d620` "Default damage:" shows Building slot 120 `FUN_140297b30` = `+0x1a4` × the research multiplier of a player-owned building (`FUN_14082ec70`,
  owner test `FUN_140546dd0`)). Base data: harpoon turrets 75, auto/multi 50 (`output rate`, **Verified** probe); walls 10–30.
- **Gun slots of `GunClassTurret`** (vtable `1416d5258`): 6 target weight `FUN_1404348e0`: 1 when dot(turret facing `+0x458`, target − turret) > 0, else 0.1 for a target outdoors,
  for one inside a building 0.1 only in the turret's own building on its floor, else 0 (**Observed**); 12 aim direction = the barrel node's +Z (`FUN_140434f40`); 13 aim error = building `+0x448`;
  18 mount factor M = 1 + 0.01 × the output value of the building in `+0x478` (the `mounted` handle: a wall's turret bonus) (`FUN_1404353b0`); 19 power factor P = 0.8 e + 0.2 (`FUN_140435380`),
  e = building slot 109 `FUN_140296e00` = 0 when not operational, else hp ratio × power grant; 22/23 pierce ends trunc(0.75 D), trunc(1.25 D).
- **Manning** (`Task_UseTurret`, update `FUN_1403474f0`): the building of the order's target or the character's `+0x258`; it must be a turret (slot 107 = 8) with a free
  operator place (slot 159); the gunner faces it and holds clip `combatstance`; unless broken (`+0x3b6`) the controller gets the building and stat 11 Turrets
  (`FUN_140434570`, also sets it active), the controller ticks, and slot 150 `FUN_140434d20` registers the operator and sets the turning speed `+0x464` = e × `aim speed` × (1 + 0.01 D).
  Broken, the update calls `AITaskSytem::taskImpossible` (`FUN_14050c890`) and the task ends (Meitou: `Machine.Broken` ends a player gunner's manning, a town turret's saved `broken` an NPC's; [what damages a building](buildings-production.md#power)).
  Start/end (slots 1/3) set the building's in-use flag; the end clears the controller (`FUN_140437470`). Crossbows use the same controller with stat 35 (`Task_RangedAttack` `FUN_14033bfb0`).
- **Controller tick** (`FUN_140438c70`): sets the trigger flag `+0x34` every tick (so the fire gate `FUN_14043aee0`, run from the character update `FUN_1405cf130`, works every
  frame); chooses the target; none → state 3. Empty gun → state 2. In range (`FUN_140436860`: squared distance gunner→target < h × range², h the height factor from the gunner):
  XP perception and the stat (`FUN_1408c7250`: turrets 1.0, perception 3.0 per second), then the fire decision. With a turret it never walks to the target (blocked or out of range: wait).
- **Fire decision** (`FUN_140437ad0`): aim error ≤ 1°; the steady timer (0.4 s, reset after a shot or reload) counts down only then; a ray from the muzzle along the barrel
  (`FUN_140435b40`, mask `0x87f9e07`): a non-hostile character → hold; something nearer than the target → blocked; else fire.
- **Fire gate** each frame: the reload `FUN_140437ce0` (an empty gun reloads clamp(lerp(s, max, min)), s = 0.01 (0.6 skill + 0.4 dex); XP stat and dexterity while it runs;
  ammo only used when the gun has an ammo record); while loaded the aim point (target `Bip01 Spine2` + lead, `FUN_140436d10`) goes to the building (slot 5 → `+0x4a8`); with the fire flag, the shot.
- **Turning** (`TurretBuilding::vfunc_28` `140434b70`, `RotatingEnt::vfunc_2`, **Verified**): `+0x448` = −99, then while `+0x464` > 0 and someone operates, each `rotation function` 5
  part turns toward `+0x4a8` by its error (yaw about Y or pitch) limited to ± `+0x464` × frame time, down the part chain; `+0x448` = the largest error (degrees,
  `FUN_1404cc0d0`). So an unpowered turret does not turn and its error −99 passes the 1° test: it fires where it points. Sounds: [audio.md](../audio.md#other-world-side-events).
- **Shot** (`FUN_14043a730`): muzzle = barrel node + orientation × (0, `barrel pos Y`, `barrel pos Z`); direction = barrel +Z tilted by `randomDeviant(rand × max(0, lerp(0.005 (skill +
  perception), deviation, 0)))`; harpoon damage = U[min, max] × P × hM, range = P × range × hM, speed = shot speed × (0.8 P hM + 0.2), h = height factor from the muzzle; friendly
  avoidance min(1, 0.015 FF); `Attack` on the building's object; the target is told it is attacked (`FUN_140999d50`). The harpoon is a bolt of the gunner (hit `FUN_140439c90`, [Bolt visuals](#bolt-visuals)).
- **Target choice** (`FUN_140437f40`, `FUN_1404374d0`, score `FUN_140435d70`): the preferred/current target is kept if it still scores and equals the order's; else every
  sensed character (list category 4, mask 0x210) scores s = (f·g)² with g = 1 − d/(k × range) (k 1), f 1 (0.1 for slot 0x360, ×0.1 for slot 0x368; slot 0x368 > 0 is 0 for a gunner not of
  the player's faction); down for any gunner (slot 0x30: out, dead, ragdolled, carried, posture ≥ 3), `+0x2f8` ≠ 0 or `+0x3d4` (carried) → 0, and an ordered target that scores 0 is dropped (**Verified**; Meitou `CombatSystem.ScoresZero`); out of range → 0.001; then × weight (slot 6), × 1.5 when `FUN_1405ca6f0`, + 0.1 when it is the
  current target; × 0.1 when its sense entry is not seen, × 1.5 for the current; best above 1e-6. None: with the player's **Shoot first** option (`DAT_142134690 +0x11a`) a player gunner takes
  sensed characters of fundamental type 0 or 8 or a default-hostile faction (k 0.6); an NPC gunner takes default-hostile ones it is aware of (`FUN_14099b820`) and those fighting a member
  of its own group. An unseen chosen target is looked at (slot 0x3b0).
- **NPC turret goals** 149/222/234 have list A `isNoEnemiesInVicinity` (with the `*` flag): done, so not chosen, while no enemy is near ([ai-tasks](ai-tasks.md#perma-jobs)).
- **Unknown:** the meaning of target slots 0x360/0x368 and `FUN_1405ca6f0`; the facing `+0x458`'s writer; how far "vicinity" is; whether `+0x464` resets when the gunner leaves.

- **Save** (**Verified**, decompile): a turret saves its building and machine keys, the `mounted` handle and its gun's int `current shots` (loaded harpoons, capped at the gun's shots on load); the aim error, rotation and reload timer are not saved ([save](../formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)).

Meitou ([simulation](../simulation.md#ranged)): `CombatSystem.Turrets.cs`, `TurretGun`, `TurretFormulas`. The indoor weight takes the gunner's building and floor for the turret's (engine reading).

## Stealth knock-out

Order 228 "Stealth KO" (verb list `FUN_14079b2d0`), `Task_StealthKO` (vtable slots `FUN_140345eb0` start,
`FUN_14034cd80` update). **Verified** literals, flow **Observed**:

- The attacker walks to the target's position minus the target's heading, a unit vector (+0xd0 of its movement object `+0x640`,
  written normalised by `FUN_140665250`; **Verified**), so to a point 1 behind it; the goal is re-issued every update, also while it walks.
  Within 14 of it the swing starts and the attacker turns to face the target. The task ends at once, silently, only when the target
  senses the attacker (`FUN_140855830` = its sense entry's +0x38 flag); a fighting target does not end it, and the swing has no
  distance end (the roll is made however far the target has walked; **Verified**, no distance test in `FUN_14034cd80`). A
  sleeping or caged target (+0x2f8 ≠ 0 with a bed/cage handle at +0x300) is walked to at the use point the bed/cage gives (**Observed**, not modelled).
- Swing: clip `knockout` at x1.2 (record `knockout`, anim `stealthKO` in rebirth.mod); at 0.5 of it the roll, at 0.85
  the end.
- Chance (`FUN_1408852f0`): X = attacker assassination x injury mult minus the target's max of assassination, 0.5 x
  thievery, 0.5 x stealth term x 100, 0.5 x melee defence, toughness (each x its injury mult); chance =
  100 x (X + K) / 2K, K = 30 for X >= 0 else 15 (disassembly: the hidden third argument is +K). Unclamped. 0 when the
  target's sense entry of the attacker has its +0x38 flag (it sees the attacker); x0.5 when it only hears the attacker (+0x39 heard, **Verified**). Also 0 when the target is fighting, has an entry for the attacker and the attacker is not unseen (Character +0xe8 ≠ 1).
- Roll uniform 0..100; lands when chance > roll. Hit: floating "*Thunk*", target KO time `FUN_140644980` with
  assassination x 0.01 (the put-down KO formula, [carry-loot](carry-loot.md)). Both set the pending Assault crime (code 5, `FUN_140852c80`, against
  the target's faction, no other condition) and float the text in a ScreenLabel over the attacker ([floaters](ui-screens.md#8-damage-floaters), font index 1, shown
  whatever the floaters option says): "*Thunk*" colour (0.4, 0.8, 0.4, 0.9), "Failed!" (0.7, 0.4, 0.4, 0.7); **Verified**.
  Miss, in order (**Verified**): the target says event 56 ASSASSINATION_FAILED at the attacker (`FUN_140685660` over `FUN_140684990`); it notices the attacker
  (`FUN_140858f40` with the alarm flag: sense entry +0x38 set, its hearing of the attacker alarmed 5 s); each squad tags the other TEMP ENEMY (`FUN_1406744a0`, 4);
  the target goes for the attacker (`FUN_1405c9290` → `FUN_140999f00`, `Character::attackTarget`) and perceives and tags it again (`FUN_1405cb0a0`). `attackTarget(attacker, 1, 0)` (`FUN_140999f00`, **Observed** from the call flow) sets the victim's combat target (`FUN_140666e20`), tags TEMP ENEMY, forces the sense, and asks the AI task system for a replan (vfunc 3) unless it is down or getting up; it starts no task, so a front order wins the replan and whether the attacker (still in Task_StealthKO, ended at 0.85) is a Self preservation threat is **Unknown**. Assassination XP via the generic entry, dt 1: 100 x f on a hit, 300 x f on a miss; f = 0.5 for a
  sleeping target (+0x2f8 == 1), 0.07 in prison (+0x2f8 == 2), else 1; " (No XP)": a miss on a target that is fighting and aware of the attacker gives no XP.

Meitou: the point is 1 behind the target's facing; "senses" (`CarrySystem.Senses`) = the target's spot meter on the attacker is full, or it fights the attacker (stand-in for the seen flag), tested every tick of the walk and of the swing, a hit ending the task silently (a target fighting someone else, or the attacker's own combat state, does not: **Verified**, `FUN_14034cd80` has neither test; Meitou's `CombatSystem.Engage` still replaces an attacker's task by Attack); heard = the target's sneaking hearing roll on the attacker's last step before the swing (`CharacterCold.KnockoutHeard`, engine reading of the pass cadence);
the fight zero: the target fighting, fighting the attacker, having spotted it or having heard it (an entry exists), and some looker's meter on the attacker filling (`AggroSystem.Unseen`); the 0.07 and No XP rules as above (the 0.5 uses `CarrySystem.Asleep`: in a bed, or the Rest task with the Sleep behaviour as stand-in; a Rest order alone gives 1; aware: the same entry stand-in; **Verified** `FUN_14034cd80`: No XP is a miss on a fighting target whose entry for the attacker exists, `FUN_140855960` with its seen/heard check off); the Assault is `CrimeSystem.SetPending` at the roll. A miss defers
`DialogueEvent` 56, `AggroSystem.Alarm` (the hearing alarm only: the spot meter is not forced), the two TEMP ENEMY tags and a defensive `AttackOrder` (no assault booked for it) for an NPC target; a player character's target is told of the attacker (`CarrySystem.Engage`: forced sense, replan) and the decision step's `CarrySystem.EngagedDefence` answers unless an order runs ([decision step](../simulation.md#decision-step)), an engine stand-in for the threats list.
The result floats as `FloaterKind.StealthHit` / `StealthMiss` (queued only while the floaters option is on, unlike the original) and, for a player attacker, is also a notice with the chance. The verb shows the chance; a quick right click on an NPC while sneaking gives it.
Order 229 "Stealth Kill" shares the task but nothing issues it (no row, no use of 229; **Observed**).

## Validation

Game record/enumeration/constant checks are scoped separately from hand-decoded runtime formulas.
Synthetic determinism tests verify our implementation/order, not original-game equivalence.
Use researched endpoint/coverage/KO/ranged cases and preserve unknown terms rather than assigning
unverified constants. [Simulation](../simulation.md#combat) describes current wiring.

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
| bow damage 1 / 99 | 1.1 / 1.3 | stored unscaled at fields 8 and 9; `14043c180` scales a crossbow's level-0 pierce ends by `bow damage 1` and its level-1 ends by `bow damage 99` before the quality lerp ([Ranged and turrets](#ranged-and-turrets), **Observed** in the disassembly) |
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
6 GUN_DATA. Every crossbow has `num shots` 1, `aim speed` 1 and the three target multipliers 1.

| Crossbow | pierce min 0/1 | pierce max 0/1 | range 0/1 | reload min 0/1 | reload max 0/1 | deviation 0/1 | perfect skill | shot speed 0/1 | ammo (charges) |
|---|---|---|---|---|---|---|---|---|---|
| Ranger | 20/30 | 30/45 | 600/800 | 3/2 | 4.5/4 | 5/3 | 60 | 400/500 | Bolts [Regulars] (30) |
| Eagle's Cross | 50/60 | 70/100 | 1500/1900 | 5/4 | 10/8 | 2.5/1.5 | 80 | 700/800 | Bolts [Longs] (10) |
| Tooth Pick | 10/15 | 20/25 | 375/500 | 2/1.25 | 3/3 | 11/6 | 90 | 350/450 | Bolts [Toothpicks] (40) |
| Junkbow | 10/15 | 20/24 | 300/400 | 3/2 | 4/3 | 12/6 | 90 | 350/450 | Bolts [Toothpicks] (40) |
| Oldworld Bow MkI | 20/35 | 35/50 | 800/1100 | 3.5/2.5 | 4.5/4 | 4/2.5 | 70 | 500/600 | Bolts [Regulars] (30) |
| Oldworld Bow MkII | 35/45 | 45/60 | 800/1200 | 4/2.2 | 8/6.5 | 4/2.2 | 80 | 500/600 | Bolts [Regulars] (30) |
| Spring Bat | 30/50 | 40/80 | 400/550 | 4.5/3.5 | 6/8 | 8/4 | 70 | 500/600 | Bolts [Heavies] (20) |

### Bolt visuals

**Observed** (decompile; `Harpoon` +0x70 state 0 recycle, 1 flying, 2 hit a character, 3 landed): each bolt is one hardware-instanced entity of the
CROSSBOW `live ammo` mesh (one `HarpoonManager_<mesh>` per mesh) with the gun's material, its local +z along the flight (`setDirection(dir, TS_WORLD,
UNIT_Z)`), scaled (1, 1, max(1, speed x 0.005)) in flight as a fake motion blur and 1 once it hits; no trail. A bolt past its range pitches down 8°
a second about world x and is recycled at range -5000. Landed (`FUN_14043acf0`): exactly at the hit point, flight orientation kept, a world node;
terrain hits under |dot(normal, dir)| 0.1 vanish; objects and buildings land with the "Stone" impact switch; above `harpoonLimit` (setting, default 60; **Verified**, `FUN_14043acf0`: the list is per bolt mesh, the test is count > limit, the new bolt included)
the landed bolt with the longest flight time (`Harpoon +0x60`, summed per flight frame in `FUN_1404380e0`; the first of equals) goes. In a character (`FUN_1406508d0` -> wound record -> `FUN_14052bfa0`): every hitting bolt sticks, attached to the
struck part's `bone name` (or `bone name 2` half the time, `FUN_140649fd0`; the root without the bone) at 0.6 x a random vertex of the body on the
bolt's side (back when it flew the way the character faces), keeping its world orientation in the bone's frame; no timer, cap or `harpoonLimit`;
all go at once when first aid leaves no wound (`FUN_14064d800` -> `FUN_14052bc10`) or the appearance reloads; not recoverable. Meitou: the debris
renderer draws them (in flight interpolated between ticks); a stuck bolt sits 2 units back along its flight from its bone's origin (engine choice for
the random vertex), not saved.
Meitou `tracers` switch (default; Faithful: none, as the game): a semi-transparent streak behind each drawn bolt in flight (`GameHost.AddTracers`, drawn by
`ParticleRenderer.Tracers` with the particles), white and unlit, 0.2 s of its flight long (at most 500 units, never back past the muzzle); when the bolt sticks or goes, its tail
flies on into the stop point within 0.15 s. Needs the particle renderer: nothing shows with `--no-particles`. Not a base-game behaviour!

### Shot origin and flight collision

**Observed** (decompile and disassembly). Origin: the fire gate (`FUN_14043aee0`) refreshes the gun (slot 2 `FUN_1404351f0`) from the crossbow's scene
node in the shooter's `hands` (`Bip01 Prop2` with the attach offset; without built meshes the character's position + (0, 10, 0)); the muzzle (slot 9
`FUN_140434fc0`) is that node's world position + its orientation x (`barrel pos Y`, `barrel pos Z`, 0), the place of loaded bolt 0 (base game: (-1, 5.9, 0),
Spring Bat (-1, 1.75, 0)). The direction is normalise(aim point - muzzle) (slot 12), the aim point the target's `Bip01 Spine2` (`Spine1` for some targets)
plus a lead of target velocity x distance / `shot speed` (first order, **Verified**), tilted by `randomDeviant(random x deviation)`. Flight: each frame a ray from the bolt along its z on the physics
thread (raycastClosestShape, group mask `0x087fde47`, read one frame late); a hit counts within 2 x the step. Groups tested: 0 and 1 (characters among them),
2 terrain, 6 and 14 foliage, 9-12 building floors, 15-18 stairs, 19-22 furniture, 27 unwalkable roofs; not ragdolls, items, doors, interior hulls or
passable parts. The shooter is skipped (`FUN_1400cd0b0`). Meitou: the simulation fires from a fixed point 15 up and 8 ahead (`RangedTuning.Barrel*`, measured
from the posed muzzle) and tests the terrain and the buildings' collision (`SolidHits`; no foliage); the renderer draws a new bolt from the shooter's posed
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
is within 40; never with the flag off; NPCs always on). The Shooting rows' desire reads the flag too; the `CharStats` constructor sets it on for every character (the +0x128 dword = 0x100), and only the player's standing order and the save change it, so NPCs are always Ranged. Other unknowns: the reload animation against the timer (the timer paces the shots, so only the clip's look is open); a character's hit shape
(capsule or ragdoll) and whether downed bodies stop bolts; whether the crossbow's bleed and target multipliers and the armour's `ranged skill mult` do anything; how NPC crossbowmen get their bolts.
(The trigger flag `+0x34` is set by the controller tick itself every tick, so a shot leaves in the frame of its decision: [Turrets](#turrets), **Observed**.)

## Unknowns

- The roles of hunger and "fed" in recovery (the `+0x60`/`+0x64` pair is the hunger level and stomach buffer, [character-stats.md](character-stats.md#hunger-and-starvation)). KO wake-up and the KO point: [character-stats.md](character-stats.md#blood-and-ko).
- The steering's force terms (`FUN_1402af1e0`, `FUN_1402e9740`) behind the side-step and the
  min/max gap (the defence pick `140887970` tests no skill, **Observed**). the stat that scales max blood; the part `+0x28` link.
- The role of the class-table columns 4-6 (athletics, combat speed, stealth is an inference; the heavy 0.9..1 combat speed column is **Observed** by elimination) and of the slot-adjusted factor (armour item field index 0x5d) in `140898e90`.
- Turrets: see [Turrets](#turrets) for its own unknowns; the meaning of the weight/price-like armour table fields; the remaining difficulty floats beyond the dialog bindings listed above (their consumers).
- What `anim hesitate point` (stored as a frame fraction, no reader found) does; what reads the stumble's `+0x128` = 0.85; the owner's slot 0x398 factor in the fist rating (taken as 1); The engine's answers are in [simulation.md](../simulation.md#combat).
- Whether the decompiled maths matches in-game numbers: nothing has been compared in play.

## Expression reference

- Combat: `lerp(t, a, b) = a + (b - a)·t`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Turrets (**Observed** from `GunClassTurret`, `TurretBuilding`, `Task_UseTurret` and the ranged controller; damage loader **Verified**; GUN_DATA and turret BUILDING fields **Verified** by a probe of the install)
- AI (**Observed**)
