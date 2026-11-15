# Character stats and medical state

Kenshi1.0.68 research: CharStats/MedicalSystem binary functions, fcs.def and merged base records.
Evidence scopes, stat IDs, race/part constants and activity tables are retained below.
Implementation: `Meitou.Data.Gameplay.Bodies`, `Meitou.Simulation.Bodies`, FirstAid and Items.
Movement/combat formulas belong to [pathfinding](pathfinding.md) and [combat](combat.md).

## Stats and XP

### Effective stealth

**Verified** (decompile; in game "no hunger or arm term", #46): the getter sends stat 16 not to the table but
to `FUN_140884850`: s = clamp(stored x 0.75 if carrying (Character +0x348) x 2 while the posture is not 0
(vfunc 0x368 = `FUN_1405c7e10`, Character +0xe0: crawling or ragdolled, [ragdolls](ragdolls.md#forced-crawl)) x encumbrance (CharStats +0x190) x the body term (medical +0xb4) x the gear's stealth
(CharStats +0x3c) x the fitted limbs' factor `FUN_140644b90(16)` x 0.01, 0, 1); the getter returns s x 100 and
the sneak speed `FUN_140884920` reads s. Body term (`FUN_140644da0`): clamp(lowest health fraction over TORSO and
LEG parts, 1 with none, + toughness x scale x 0.01, 0, 1). Its only caller is the sense update `FUN_1408586d0`,
past an early return on a Character +0xd4 byte, so otherwise it keeps its last value (1 at start; **Observed**,
fits #46's torso at 0.5 leaving stealth at 1). The table's stealth case (A; 0.9, 0.9, 0.8) exists, but no caller
found reaches it. Meitou: `Orders.Stealth` (its x2 reads `MedicalState.Prone`, both crawls).

### Gear factors

**Verified** (decompile of `FUN_140887030`, the worn-gear refresh, its call to `FUN_140884370`, and the ARMOUR
loader `FUN_140898e90`). Over the worn armour list (Character +0x4a8/+0x4b0) it multiplies the pieces' floats
into CharStats: +0x40 `asassination mult` (assassination row), +0x44 `dexterity mult` (dexterity row), +0x48
`damage output mult` (katana, sabres, hackers, heavy, blunt rows; used only when not 1 and above 0), +0x50
`ranged skill mult` (crossbows and friendly fire rows), +0x38 `dodge mult`, +0x3c stealth, +0x4c `fist injury
mult`; sums `perception bonus` (+0x30), `unarmed bonus` (+0x2c) and the attack/defence bonuses. Stealth and dodge
both start from the worn backpack's `stealth mult` (CONTAINER +0x248, `FUN_140763810`; negative becomes 1), 1
without one. With no armour and no backpack (inventory section 12) dodge and dexterity are 1.1 and stealth 1.2
(literals at `14169bb44`, `1416852f0`). A piece's stealth (ARMOUR +0x25c, disassembly of `FUN_140898e90`), with
t = quality + `level bonus` x 0.01: lerp(t, material lo, hi) x lerp(t, class lo, hi) x `stealth mult`; 1 if
`stealth mult` is negative; a hat (`slot` 3) that lists coverage is raised to 1. The tables (constructor
`FUN_1408a8f90`, material +0x1c, class +0x18 of the [economy](economy.md#42-armour-and-clothing-type-3) tables):
CLOTH and LEATHER 1..1, CHAIN 0.3..0.7, METAL_PLATE 0.1..0.8; GEAR_CLOTH and LIGHT 1..1, MEDIUM 0.9..0.9,
HEAVY 0..0.4. **Verified** (same disassembly, 2026-10-26): CharStats +0x18, the run speed factor (`Speed.Run`,
the Athletics tooltip's gear row), is max(1, largest piece factor) x min(backpack, smallest piece factor), the
backpack's raw `athletics mult` (CONTAINER +0x238; +0x240 `combat speed mult`, +0x244 `combat skill bonus`,
+0x248 `stealth mult`, all from `FUN_140763810`) or 1 without one. The backpack (back slot, type CONTAINER, `FUN_1405cb320`) also seeds the
gear totals (`FUN_140887030`, **Verified**): its `combat skill bonus` starts both the attack and the defence bonus sums (CharStats +0x20 / +0x24;
0 without one) and its `combat speed mult` the combat speed product (+0x1c; 1 without one); each armour piece adds or multiplies on top
(vanilla: Large Backpack -10 / x0.8, Medium -5 / x0.9, Small -2 / x0.95). Meitou: the `Fighter` constructor. A piece's factor (ARMOUR +0x248): lerp(t,
class column +0x10) x `athletics mult`, 1 for `slot` 3, 5 and 8; column +0x10 is GEAR_CLOTH and LIGHT 1..1,
MEDIUM 0.85..1, HEAVY 0.8..0.95. `perception bonus` is an int (ARMOUR +0x254); the effective perception
`FUN_140884ac0(0x18, 0)` is (trained + CharStats +0x30) x the stat multiplier. Meitou: `Fighter.AthleticsFactor`,
`Fighter.PerceptionBonus` (added in `CombatSystem.Eff`), `ArmourPiece.AthleticsOf`. A piece's combat speed factor (ARMOUR +0x258,
**Verified**: `FUN_140898e90` stores it next to +0x248 and `FUN_140887030` multiplies it into the product above) is lerp(t, class column) x
`combat speed mult`, forced to 1 for a hat (`slot` 3, whatever its mult); the class column is **Observed** by elimination (the one column the
athletics and stealth blocks leave unused): heavy 0.9..1, else 1 (the tooltip's "Combat speed effect"). Meitou: `ArmourPiece.CombatSpeed`,
the `Fighter` product. The stun row factor `FUN_1406439f0`
is the dust storm: lerp(1, 0.5, v) while medical +0x148 = 1, v = medical +0x14c = the storm strength x (1 -
the gear's protection), clamped 0..1 (`FUN_140648ef0`, which also posts "You are in a dust storm, it affects
your accuracy in combat."). Meitou: `Fighter.Factors` / `Naked` / `StealthMult`, `ArmourPiece.Stealth` (the dust value is `MedicalState.DustStatFactor`,
[weather](../formats/weather.md#effects-on-characters)); crossbow and assassination skills take their factor in `CombatSystem.Eff`,
dexterity and damage output stay applied by the combat formulas (`DexterityMult`, `DamageOutput`; **Verified** `FUN_1408868b0`: the latter scales the whole packet
once, only when above 0 and not 1, and the weapon skill is read raw).

## Hunger, blood and parts

**Verified** hunger thresholds: starts3, >=2 unpenalized, <2 penalized, <1 starving, <=0 death.
**Observed** (1.0.68 `FUN_140581770`, fresh-character factory): a squad template's `malnourished` flag sets an organic member's initial hunger to
1.7 (170 on the HUD, constant `0x3fd9999a`), separately from its later free-food floor. `startoff- rebirth` (`1532935-rebirth.mod`) has the flag;
The Slaves starts at 170. Changing between organic races in the creator keeps hunger; robots are reset to 3 (`FUN_14064e410`, `FUN_1405e9f40`).
**Observed** tick: T=starvation-time*HungerTime (base60 hours per level). Empty stomach drains
dt/T*rate; fed stomach converts buffer into hunger, capped3. Rate includes race, rest/KO/dead
bed factor0.33, active encumbrance factor and situation terms. Zero hunger-rate races do not
starve; robot flag alone does not imply zero rate. Eating: [Eating](#eating); NPCs' free food:
[Free food](#free-food). Starvation KO threshold uses toughness10..85 and doubles with food;
actual transition timing is not fully decoded.

The medical update gets dt twice in the original (every frame, plus again for each character with a budgeted visit: about x(1 + visits / frames), x2 while the budget covers everyone); Meitou ticks exact game time ([engine choices](../simulation.md#engine-choices-against-the-originals-frame-loop)).

Medical parts retain flesh h, bandage b, stun s, wear w, maximum M and hit/rig/replacement state.
Effective part health, coverage/hit choice and body aggregates are distinct. Apply blunt/cut,
bandaging and stun to the correct quantities; do not heal untreated flesh directly.

### Time

**Verified** (decompile): `FUN_1405c7d40` (Character slot 0x278) hands Character +0xc8 to the medical update `FUN_140652000` and clears it,
so part, blood, clot and KO-timer rates are per second of that sum. Hunger instead reads the game clock's elapsed time (`FUN_14064da70` via `FUN_140791ca0`).

### Part tick (`FUN_14064f5c0`, **Verified** decompile)

In order, per part: the hit multiplier falls 0.01 a second while h >= 0 (reset to 1 below). Self healing (RACE `self healing`: in the base data only animal races and the robot spiders, no playable race, **Verified** merged data probe; conscious, alive,
h - s < M - b) adds 0.05 dt `heal rate mult` R 0.4 to b and skips degeneration that tick; otherwise, while h < M and h > -3M,
h -= 0.05 dt `bodypart degeneration rate` ChanceOfDeath degMult D 1.2 with D = clamp(((M-h-b)100/M - 20)/70, 0, 1), quartered
when h + b > 0; degMult lerps 1.7..0.03 by toughness and is 0 for a non-vital robotic part (robot race or limb replacement)
and in any bed. Healing: while h < M, b > 0 and the limb is not crushed (state 3), min(b, 0.05 dt `heal rate mult` R), x250
on a robotic part, moves from b to h. R = M/100 x the organic rate (robotic for a robot race or a replacement); a stump
(state 1) has base HP 5 and R = 2 x the organic rate without M/100. Repair (a repair bed): 0.05 dt `resting heal
rate mult` x its rate comes off w (down to 0) and goes into b, held to at most M - h; with w at 0 it still goes into b, so the bed
treats untreated damage too (**Observed**: a skeleton's limbs with wear only, damage only and both each gained HP at the same rate in a
Skeleton Repair Bed, the last keeping its untreated gap until its wear was gone).
Rig is held under 50 - (h - s); s falls 0.05 dt `stun recovery rate` `heal rate mult` R.

### Blood and KO

**Verified** (decompile): wounds lose `bleeding clot rate` x0.1 a second (`FUN_140646140`); a hit opens one of strength
max(cut, pierce) x `bleed rate` x0.1 x scale (`FUN_1406508d0`). Bleeding = (`extra blood loss from bodyparts` x stumps
(-(h+b) 0.002 each) + wounds) x ChanceOfDeath x 1.2 x race `bleed rate`, floored at 0; blood falls by it a second, then is
clamped to capacity every tick, and with no bleeding recovers `blood recovery rate` x0.1 x race `heal rate` a second
(`FUN_140652000`). The wound is opened whatever the race's `bleed rate` (no race read in `FUN_1406508d0`; a bloodless race
gets wounds that drain nothing). Bleeding has no per-part term but a stump's: a bandage never closes a wound (first aid closes
the newest one first, see [First aid update](#first-aid-update)); a bandaged stump stops bleeding once h + b is back at 0.
The KO timer +0xa0 falls by the medical update's time ([Time](#time): 1 to 2 x dt).

**worstDamage** (medical `+0xa4`, KenshiLib 1ce4a16; **Verified** decompile of the aggregates `FUN_140644e60`): the minimum over the organic parts
(no `robotLimb`, and a byte `+0x7c` clear on an object the part returns, by offset the race's `robot`, **Observed**) of (`flesh` + `bandaging`) / M (M = `_maxHealth` x `age` x `HPMult` - `wearDamage`), computed as a percentage and scaled back, set to 1 above 0.999;
starts at 1.0 (constructor `FUN_1406409f0`). So it is at most 1 and under 0 only while a part is below zero. Read by the HUD's health word, the hunger
word and the Toughness XP multiplier ([ui-screens.md](ui-screens.md#4-hud-mainbargui)). Meitou: no single field; `HealthPart.Fraction` is per part.

**Verified** coma point = -lerp(10, 85, toughness x stats multiplier / 100) (`FUN_140643ae0`: CharStats `+0x90` x `+0x34`, 1 for people and the age for animals, through the plain unclamped lerp `FUN_140015b63`, so toughness past 100 goes past 85). Every toughness read of the medical code uses the same stat x multiplier x 0.01 (**Verified**: `FUN_1406447d0`, `FUN_140644980`, `FUN_140652000` degeneration, `FUN_140657620` starvation point; the leg-loss factor `FUN_14064edc0` and `FUN_1406508d0` clamp it to 0..1, the lerps do not). Meitou: `CharacterStats.ScaledToughness` is stat x multiplier and every `MedicalContext` is built from it; `MedicalState.KoPoint`, `KoTerms`, `DegenerationMult`, `StarvationKoThreshold` use the unclamped lerp. **Verified** KO time of a blood or vital knock-out (`FUN_1406447d0`, KenshiLib
`startKnockoutTimer`): `knockout time base` (20, not scaled) + k x (per vital part 25 (M - h - s)/M + per part with h - s < 0 its
`KO mult` x 100 |h - s|/M + 50 x missing blood / capacity), k as in the [expression reference](#expression-reference). So a part n % below 0 adds
k x (25 + 2.25 n) for the head, k x (25 + 1.25 n) for the chest or stomach, k x 0.5 n for a limb. In game (1.0.65, issue #134: whole body 20 at
toughness 0/50/100; head, chest, stomach, arm, leg rows, two parts at once and missing blood, toughness 0/50/100) every reading fits within 0.02
(0.2 for a part still sinking). Meitou: `MedicalState.KoTime`.
Death: vital h-s<-M, blood<=-capacity or hunger<=0. Preserve KO/coma/death distinctions.

- A combat blow (`applyDamage` `14064f300`, from `addWound`) that leaves a vital (`fatal`, part `+0x32`) part at h - s + rig < 0
  restarts the KO timer `+0xa0` (`startKnockoutTimer` overwrites it, it does not take the longer) and sets unconscious
  `+0x161`, on every such blow. Damage through the same function with its 4th argument set (load, weather) skips it for a
  conscious character. A part going under 0 in the tick starts nothing.
- `reassessCollapseMode` (`140649320`) rebuilds `+0x161` each update: set by a hunger KO, a KO timer > 0, a vital part at
  collapse stage 2, or blood: for a character unconscious before the update, blood < 0 (and it sets `+0x163`); for a
  conscious one (or with a fresh stump, below), blood < 0, or < -25 once `+0x163`.
  `+0x163` (`bloodlossTrauma`, KenshiLib 1ce4a16; medical bytes `+0x160 crippled`, `+0x161 unconcious`, `+0x162 sub50KO`, `+0x165 rightArmOk`,
  `+0x166 leftArmOk`) clears in `medicalUpdate` only on an update that finds blood below capacity and then above 25 (**Verified**, decompile of
  `FUN_140652000`: the test sits inside the recovery branch). Natural recovery passes through that window; blood written straight to capacity does not,
  which is why issue #82 saw the HUD's "Blood loss trauma" term 142 s after the blood was back; the test runs while bleeding too. So a character wakes once all of those are clear.
  Meitou: the same, in `TickBlood` (and the off-screen `Recover`).
- Fresh limb loss (**Verified**, decompile of `FUN_14064da70`, `FUN_140645350`): each medical update first checks the four limb slots for a stump
  (limb state 1) whose h - s is under -M (M from the stump's base HP 5, so the cut part until it heals past that). If one is, it sets `+0x163` and
  passes the flag to `reassessCollapseMode`, which then applies the conscious blood rule to an unconscious character: blood holds it down only under -25.
- Stage of a torso/head part, v = h - s + rig: 0 when v >= 0; under the KO point 2, and a vital part sets the coma flag
  `+0x162`; between the KO point and 0, 2 for a non-vital part, and for a vital one 2 only while `+0x162` is set. `+0x162`
  clears on an update where no vital part is at stage 2. So a vital part between the KO point and 0 does not keep a
  character down; one under it does ("Recovery coma") until every vital part is back to 0. Legs use h - s + bandage
  against the KO point ([ragdolls](ragdolls.md)).
- Save keys: `coma` = `+0x162`, `unconcious` = `+0x161`, `incapacitated` = `+0x160` (a leg-collapse part at stage > 0).
- Meitou: `MedicalState.VitalPartsKeepDown`, `RestartKo`, the wake test `Blood >= 0` (-25 with `HasFreshStump`).

### Cut-off and leg loss

**Verified** (decompile: `FUN_14064f300` blow, `FUN_14064f5c0` tick, `FUN_14064edc0` sever, `FUN_140644980` leg KO, `FUN_1409b1c10` roll):
- A destroyed part that is not vital is cut off: a blow needs h-s+rig < -M, a tick h-s < -M (a worsening wound). A vital part kills instead.
- Blow: Dismemberment >= Rare, a stats object, limb original or crushed. Tick: Frequent only, limb original only. A prosthetic is never cut off.
- Sever: the part becomes a stump; blood -= (5 + 25 x U) after the KO below (the roll `FUN_1409b1c80(5, 30)`); toughness XP 300; LOST_LEG / LOST_ARM dialogue.
- **A leg going down in posture 0** (`PS_NORMAL`: conscious, not staying low, not crawling) knocks the character out for **3 s** of medical time,
  whether it is cut off, crushed, worn through or under the KO point. The collapse check `FUN_140649320` sets the crippled flag for a part at stage > 0 whose
  `collapse part` has a leg bit (stage of a leg, `FUN_140644450`: a stump 1, crushed or h - s under -M 2, h - s + b under the KO point 2), and when
  `getProneState` (slot 0x368) is 0 calls `setProneState(2)` (slot 0x370); the setter `FUN_1405c7e20` writes 3.0 to Character +0x4f8 on a change from 0 to 2,
  which is the KO timer (KenshiLib 1ce4a16: `MedicalSystem` at Character +0x458, `knockoutTimer` at +0xa0), over whatever it held; the same check then sets
  unconscious for a timer > 0. The character update `FUN_1405d00b0` makes the same 0 -> 2 change for a crippled character (**Verified**, decompile).
  So a cut leg's rolled time (the sever's max(3, (2 x knockout time base + |capacity - blood| x k x 0.5) x (1 - toughness/100) x 4) x U, `FUN_140644980`,
  then the roll) is always overwritten. **Verified** in game for a cut (1.0.65, issue #134): five leg cuts (KenshiLib `amputate`, which is `FUN_14064edc0`, then
  `reassessCollapseMode`), three of them standing (two with the posture not read), at toughness 0 and 50, full or low blood, all wrote exactly 3; an arm cut wrote nothing.
  The posture follows the down state (4 for a knock-out) only in `FUN_1405d00b0`, which then runs the collapse check (medical slot 0x10, `FUN_14064da70`), so a
  knock-out leaves it at 0 until that update: a vital or blood knock-out and a leg going down before it gets its timer overwritten to 3 too (**Verified**, decompile). Another posture
  (staying low, crawling, knocked out (4), playing dead (3)): no KO; waking with a leg down goes from 4 to 2.
- A destroyed limb not cut off (setting too low; a crushed limb met by a tick) is down like any other leg: in posture 0 the 3 s KO above, then the crawl (posture 2).
- The put-down, stealth and shackle knock-outs call the same function with a factor and no roll (the blood term only, **Verified**).
  Meitou: `MedicalState.KoBlood`, used by `PutDownKoSeconds` ([carry-loot](carry-loot.md)).
- The KO toughness XP (`FUN_1408c6720`) is paid only on the vital-part branch of `FUN_14064f300`, not for a leg or blood KO (**Verified**).
  Meitou pays it for every KO event: a gap.
- Meitou: `MedicalState.ApplyHit`, `CheckPart`, `Sever`, `LegCollapse` (after a blow, the tick's part loop and a bite; `LegLossKoSeconds`), the posture of the last
  tick's end (`Standing` then; not saved, the live `Standing` before the first tick).

### Decapitation (Meitou)

The head falls (`Decapitation`, drawing only): when the renderer last drew the character with its head and it is now listed headless, the old look's
`SeveredHead` (the body's head vertices only, instance flag 29, with hair, beard, hat and its own cap over the rim of its all-head triangles) keeps that frame's bones,
taken at the cut (`CharacterRenderer.HoldPose`), turns about the head bone and drops (gravity 98.1, as the limbs) 3 to 7 units aside, thrown with the downing blow's ragdoll speed (with "Advanced ragdolls"; `CharacterSnapshot.Knockdown`, [ragdolls](ragdolls.md#initial-state); it then drifts and tumbles that way, spinning 0.1 rad/s faster per unit/s), its clock starting once its look
is drawn. It moves as a ball over a height (`Decapitation.Step`, steps of 1/120 s): a landing faster than 6 units/s throws it back up with 0.32 of its speed, takes 0.3 of its speed across the ground and half its tumble; on the ground it rolls the way it goes, turning by the ground it covers, slowing by 28 units/s2 until it rests under 0.4 units/s. Its floor is 1.3 above the walkable ground (sandbox floor, terrain, building floor) or a body lying there (spheres round its bones, as last drawn; not its own
neck and head, nothing above where it fell from). It is no item and not saved: a load or new game clears the heads (a corpse loaded headless drops none); past the
dropped-items limit the oldest goes. Both caps are made with each person's body mesh on the loading worker (`CharacterContent.CapMesh`) and uploaded on first use.
Saved as `meitou decapitated` ([save](../formats/save.md#limbs-medical_state-limbs)).
The neck spurts (`Decapitation`, drawing only; with the `Blood` setting on and `bleed rate` > 0): a mist at the cut, then 6 pulses 0.35 s apart, weaker
each time, of the `HackBlood-system` spray (and the `Bloodsplash01` mist; both in the race's `blood colour` when it is not the default 0x800000: the particles take that hue at their texture's brightness, `ParticleRenderer.Burst` recolour, also for luckyren bursts) along the spine (`Bip01 Spine2` to `Bip01 Neck` as drawn; while a pulse emits, its emitter is moved and turned with the neck each frame, `ParticleRenderer.MoveBurst`, so it follows the falling body), each leaving a splash decal in
the race's `blood colour` up to 18 units off along it. Timed by the shown tick; not saved.

## Aid, limbs and load

First aid fills bandage; healing consumes it. Medkits, robot repair and beds apply separate
skills/charges/rates. Limb replacements modify part health and stat factors; missing limbs,
stumps and robot wear are not interchangeable. Race tables own HP/blood/hunger/heal/XP inputs.

### First aid update

**Verified** (decompile of `FUN_14033c5a0` (task), `FUN_14064d800`, `FUN_140649a00`, drain `FUN_140644250`). Each update, with
skill x 0.8 when treating oneself and q = min(skill, kit quality):
- **Wounds first.** While the patient has open wounds, the newest one's strength falls by `medic speed mult` x 2 x lerp(0.0005, 0.03,
  q x 0.01) x dt and it closes below 0; the kit drains once; nothing is bandaged that update. The kit must match the patient (robot
  kit for a robot race).
- **Bandaging.** The first part in part order of the kit's kind with h + b < M takes rate x dt (rate below); only if that fills it
  (clamped to M) does the next part also take the full rate x dt. The kit drains once per part bandaged. So a patient with n injured
  parts takes about n times as long, and n times the charges, as one with a single part of the same damage.
- **Drain** = lerp(`medkit drain 99`, `medkit drain 1`, (100 - skill) / 100) x dt (skill, not q), x `robot medic speed mult` for a
  robot kit (function 12).
- **Player only.** The bandaging step drains only when the medic's faction is the player's (Faction +0x250); NPC medics use up kits
  only on wounds.
- **Skill.** `Task_FirstAid` (`FUN_1403563a0`) runs a first aid kit phase, then (none left) a robot repair kit phase; the skill is
  CharStats +0x98 (`medic`) in the first, +0xdc (`robotics`) in the second, and the trained stat 9 or 6 likewise; the race's `heal
  stat` is not read here. The task reads the raw field, no injury/hunger stat multiplier (**Verified**,
  `FUN_14033c5a0`, x0.8 for self; the splint passes the raw field too, `FUN_14034ef20`); Meitou matches (`FirstAidSystem.SkillOf`).
- **Kit search** (**Verified**): vtable +0x38 of `Task_FirstAid` and `Task_FirstAidRig` is `FUN_14033c740` (the rig task passes
  function 2). It searches the medic's main grid, else its worn backpack's contents (`FUN_1405cb320`: the item with slot id 12 at +0xb8,
  type 46 CONTAINER; Meitou's `Backpack` is the `backpack_attach` section). `FUN_140746480`
  takes the item of that function with 0 < charges < 99999 and the fewest charges (the first of equals). No squad mates, no buildings.
  The task keeps the kit it found (task `+0x80`, **Verified**: `FUN_14033c7e0`, called from `FUN_1403563a0`): it searches again only when the stored kit is null or of another
  function (`+0x124`); a found kit that `FUN_14032dff0` calls invalid (not an item, or no charges: it is then removed from its holder) is dropped and the search runs once more, firing event 0x16 (EV_FIRSTAID_KIT_EMPTY)
  when that finds none (a first search that finds none is left to `FUN_1403563a0`: the robot phase, else the end); `FUN_14033c5a0` clears it when it is used up. Meitou: `CharacterCold.TreatKit` (`FirstAidSystem.KitOf`; runtime, derived), also dropped when no longer held.
- **dt** (**Verified**, `FUN_1405cf130`): the task update gets the character's update time `+0xc4` = the scaled frame time `DAT_142133794` (0.01 when not positive) plus the time summed while the character was not visited (`+0xc0`, cleared), passed on by `FUN_1403563a0`; so its long run is the scaled frame time once, without the medical clock's extra visits ([Time](#time)). Meitou: `FirstAidSystem.Apply`, `MedicalState.CloseWound` / `Treat`, `dt = TickSeconds`.

### First aid need score

**Verified** (decompile of the aggregates `FUN_140644e60`, read through `scoreFirstAidNeed` `FUN_140643ca0`; constants read from the exe). Medical `+0xac` (organic) and `+0xa8` (robot) are recomputed with the other aggregates;
a dead patient reads -0.1 (`DAT_1416bc158`). Per part pct = 100 / M x (flesh + bandage) (M as [part state](#per-part-state-medicalsystemhealthpartstatus)). Organic parts (no robot race, no fitted limb) and robotic ones are taken apart,
each with the least pct over its vital parts (`+0x32`) and over all of them (the organic minimum is `worstDamage` +0xa4); each x 0.01, 1 above 0.999. need = max(0.5 x (1 - worst), 1 - worst vital). The blood term
(1 - blood / max blood) x max(0, bleeding `+0x78` - 1.5 x extra blood loss from bodyparts) x 10 replaces it, for a robot race in the robot score and otherwise in the organic one, when larger and above 0.2. Everything bandaged
is 0. The player's quick right click and the medic finders treat above 0.01 (`DAT_141682ad8`) as needing aid. Meitou: `MedicalState.FirstAidNeed`; the snapshot's `NeedsAid` / `NeedsRobotAid` are these tests, for every character.

### Splints

**Verified** (disassembly of `FUN_140646400`, `FUN_140649d50`, `FUN_1405988e0`; Splint Kit records in gamedata.base). A splint kit is an
ITEM with `item function` 2 (Splint Kit q20, rebirth.mod q25; Advanced Splint Kit q50; 100 charges). A splint raises a part's rig, which
counts as health in the fraction, the down (h-s+rig<0) and sever tests, so a splinted broken leg walks again (leg speed reads the fraction).
- `needsSplint`(X): patient not dead, race not `is robot`, some non-vital part with rig<b, (h-s+rig)*1.05<50 and rig+1<trunc(X). The
  menu asks with X=50 (verb "Splint Injuries", task 249 "Splinting"), a medic with min(medic, kit quality). Robotic limbs are not excluded.
- Update: t=min(medic skill, kit quality)*0.01, cap=1-trunc(t*-100), t in float and the product rounded to float, so quality 20 caps at 20 (**Verified**, `MULSS`/`CVTSD2SS` at 0x140649ddb..e1b). The first non-vital part in part order with h-s+rig<50, rig<s+b,
  rig<cap gets rig+=lerp(1,15,t)*medic speed mult*dt, clamped to s+b and cap; so only bandaged or stunned damage can be splinted.
  The kit drains like a medkit only when rig was added (`FUN_140644250`, once per update, no faction test); medic XP (stat 9) each update
  that a kit is held (`FUN_14034ef20`, **Verified**). Ends when no part qualifies.
- Kit: the rig task takes a splint kit (function 2) from the medic's own inventory or backpack by the [kit search](#first-aid-update),
  keeps it until empty, removes it then and searches again; with none left it says event 22 (**Verified**, `FUN_14034ef20`, `FUN_14032dff0`).
- Order 249 refuses without such a kit ("I don't have a splint kit."). The job 250 "Splint Rigging" (finder `FUN_14099e240`, may pick the medic
  itself): [perma-jobs](ai-tasks.md#perma-jobs); Meitou runs it as a splint treatment (`FirstAidSystem.FindSplint`, the decision step's job).
- Meitou: `SplintOrder` (the Treat task with `TreatSplint`), `MedicalState.NeedsSplint` / `Splint`, `FirstAidSystem.OwnKit` (own kit,
  fewest charges); dt as for first aid. Left as they are: Meitou's splint skill is `stat x StatMultiplier`, where the rig
  task passes the raw CharStats+0x98 (**Verified**); XP only when rig is added.

### The stat list

**Verified.** Three independent places agree on the numbering: the editor's `StatsEnumerated` enum (names), the
per-stat accessor `FUN_140884190` (stat number to `CharStats` byte offset, a switch), and the stats save writer
`FUN_14064a8f0` (offset to saved field name; read back by the loader `FUN_14064b6a0`, which stores at the same
offsets). **Verified** (`FUN_14064b6a0`): a saved character without a `lockpicking` key loads it as `thievery` x 0.5, and one without `toughness2` loads toughness as its `defence` (+0x90 = +0x124); with the key the stored value is used. Meitou: `CharacterStats.ReadSave`.
Stats are `float`s inside `CharStats`. The number is what RACE `heal stat`, `stats good<N>`,
`stats bad<N>`, BUILDING `stat used` and all XP code pass around.

| # | `StatsEnumerated` | STATS field / save name | `CharStats` offset |
| --- | --- | --- | --- |
| 1 | STAT_STRENGTH | `strength` | +0x80 |
| 2 | STAT_MELEE_ATTACK | `attack` | +0x120 |
| 3 | STAT_LABOURING | `labouring` | +0xe4 |
| 4 | STAT_SCIENCE | `science` | +0xe0 |
| 5 | STAT_ENGINEERING | `engineer` | +0xcc |
| 6 | STAT_ROBOTICS | `robotics` | +0xdc |
| 7 | STAT_SMITHING_WEAPON | `weapon smith` | +0xd0 |
| 8 | STAT_SMITHING_ARMOUR | `armour smith` | +0xd4 |
| 9 | STAT_MEDIC | `medic` | +0x98 |
| 10 | STAT_THIEVING | `thievery` | +0xac |
| 11 | STAT_TURRETS | `turrets` | +0x114 |
| 12 | STAT_FARMING | `farming` | +0xe8 |
| 13 | STAT_COOKING | `cooking` | +0xec |
| 14 | STAT_HIVEMEDIC | shares `medic` | +0x98 |
| 15 | STAT_VET | shares `medic` | +0x98 |
| 16 | STAT_STEALTH | `stealth` | +0xa4 |
| 17 | STAT_ATHLETICS | `athletics` | +0x94 |
| 18 | STAT_DEXTERITY | `dexterity` | +0x88 |
| 19 | STAT_MELEE_DEFENCE | `defence` | +0x124 |
| 20 | STAT_WEAPONS | no storage (the accessor returns a dummy slot) | n/a |
| 21 | STAT_TOUGHNESS | `toughness2` | +0x90 |
| 22 | STAT_ASSASSINATION | `assassin` | +0xb8 |
| 23 | STAT_SWIMMING | `swimming` | +0xa8 |
| 24 | STAT_PERCEPTION | `perception` | +0x8c |
| 25 | STAT_KATANAS | `katana` | +0xf8 |
| 26 | STAT_SABRES | `sabres` | +0xfc |
| 27 | STAT_HACKERS | `hackers` | +0x100 |
| 28 | STAT_HEAVYWEAPONS | `heavy weapons` | +0x108 |
| 29 | STAT_BLUNT | `blunt` | +0x104 |
| 30 | STAT_MARTIALARTS | `unarmed` | +0x10c |
| 31 | STAT_MASSCOMBAT | `mass combat` (save only) | +0x9c |
| 32 | STAT_DODGE | `dodge` | +0xf0 |
| 33 | STAT_SURVIVAL | no storage (dummy) | n/a |
| 34 | STAT_POLEARMS | `poles` | +0x118 |
| 35 | STAT_CROSSBOWS | `bow` | +0x110 |
| 36 | STAT_FRIENDLY_FIRE | `ff` | +0xf4 |
| 37 | STAT_LOCKPICKING | `lockpicking` | +0xb0 |
| 38 | STAT_SMITHING_BOW | `bow smith` | +0xd8 |

### Levelling (XP)

| Activity | Function | Effect (**Observed**) |
| --- | --- | --- |
| Attack defended (event 2) | same | **Verified**: `defence` at 0.25 of base with skill-diff(own defence x `map[19]` vs opponent attack) and no race factor in the gain; weapon skill at 0.1 (diff against the opponent's defence, Meitou uses the attack). No attack or dexterity gain |
| Being hit (event 4) | same | `defence` 2.0 of base (**Verified**: the stat written is the CharStats slot +0x124, the same slot event 2 uses for `defence`, and [combat.md](character-stats.md#stats-and-xp) lists the same; `FUN_1408c6360` handles stat-set kind 5), and `toughness` = 0.075 x `xp rate toughness` (1.33) x skill-diff(own, other) x `map[21]`, own = toughness x CharStats `+0x34` (the stat scale), other = cut + blunt + pierce + stun of the packet the hit carries (**Verified** `FUN_1408c6980`; callers the melee hit `FUN_1404394f0`, its copy of the blow's packet, and the bolt hit `FUN_140439c90`, pierce only; that the melee packet is the powered one is **Observed**). Meitou: `XpService.Combat` `damageTaken`; the hit also pays the per-damage gain below |
| Strength tail of every event | same | `strength` rises by `core(0.075 * 0.55 * 2*xp rate strength * map[1])` scaled x0.4 for event 2, x0.1 for event 4 (1 for events 0, 1 and 6, **Verified**), and by the weapon-weight factor below; for unarmed (kind 5) the weapon-weight factor is replaced by (1 - encumbrance factor) |
| Own punch (attacker) | `FUN_1408c68e0`, called only from `MedicalSystem::punchSomething` `FUN_140666780` | **Verified**: 0.075 x k x `xp rate toughness` x `map[21]` x Global damage multiplier; k by hit material: 0.25 for 1 (metal), 0.5 for 5 (blocked), else 0.05. A hit taken does not call it ([punch self-damage](combat.md#punch-self-damage)) |
| Toughness when knocked out | `FUN_1408c6720` (from the part damage `FUN_14064f300` when a hit knocks out, unless its fourth argument is set) | **Verified**: core(0.075 x `map[21]`) (`DAT_142134edc` = exp gain multiplier x 0.1, set in `FUN_1408c6980`) |
| Toughness on an ordered get-up (`Task_GetUp` start `FUN_14033ca10`) | `FUN_1408c6780` | **Verified**: core(f x (n + 10) x 0.075 x `map[21]`), f the HUD's Toughness XP multiplier ([ui-screens.md](ui-screens.md#4-hud-mainbargui): 1, +1 left arm crippled, +2 right arm, +2 `bloodlossTrauma`, +2 always since `worstDamage` < 15), n the sensed `numEnemies` (SensoryData +0x94), only when the player ordered the get-up, something is sensed and no conscious ally ([ragdolls](ragdolls.md#playing-dead)); dialogue events 0x17 to 0x19. Meitou: `XpService.ToughnessFromGettingUp` (events not sent) |
| Toughness from losing a limb | `FUN_14064edc0` via `FUN_1408c7310` | generic entry with dt 1, amount 300 into stat 21 (a large one-off gain) |
| Weapon-weight strength factor | `FUN_1408838e0` / `FUN_1408839a0` | `min strength xp mult` (0.1) + clamp((weapon weight - strength x injury multiplier) / `weight strength diff 1x` (20), 0, `weight strength diff max` (1)); stored at +0x184 |
| Athletics (moving) | `FUN_1408c6660` | **Verified** gate (`FUN_140660dc0`): player faction only, not fight steering (movement mode 1) or swinging (+0x37c), moving faster than 2 (walking trains too). `FUN_1408c5ec0` (the encumbrance factor, x1.5 when the speed is over 80% of the run value V, not the current maximum) x `xp rate athletics` (stored 0.5) via the generic entry, with strength from carrying; in deep water only swimming (stat 23): 5, or 10 when carrying. Meitou: `BodySystem.TrainOnTheMove` (the Attack task stands for steering and swings) |
| Lockpicking | `FUN_1408c62e0` | stat 37 gains 1.2 x `map[37]` on success, 0.24 x on failure |
| Engineering | `FUN_1408c6250` | stat 5 gains `0.02 * dt * 0.75 * map[5] * 0.75` |
| Medic | `FUN_1408c6130` | **Verified**: `dt / T * 0.75 * map[patient race's heal stat]` added straight to the stat (no core curve, no cap); T = lerp(`XP rate medic 1`, `XP rate medic 99`, skill x 0.01) (the ends' order from the field names; editor text "seconds of usage per point"); nothing when T <= 0 |
| Mass combat | `FUN_1408c5e80` | **Verified**: +0x9c rises by 0.75 x 0.05 per call, straight; one caller, the answer to a slot ask `FUN_140666a30` ([combat](combat.md#attack-slots-and-circling): the asked character's, while stumbling or fighting; nothing reads the stat; not called in Meitou) |

### Effective stats: injuries and hunger

Decoded cases (hex constants converted). `limb`, h0, l0 and f0 are **Verified** in game for every row (#46,
[above](#stats-and-xp)); p0, the arm-disabled terms and `extra` stay **Observed** (hand-decoded from a switch with
fall-through cases):

| Stats | `limb` | h0 | l0 | f0 | p0 | extra |
| --- | --- | --- | --- | --- | --- | --- |
| strength (1) | A x T | 0 | 0.75 | 0.1 | none | |
| melee attack (2), unarmed (30) | A x T | 0.75 | 0 | 0.7 | none | |
| labouring, engineer, weapon smith, armour smith, farming, bow smith (3,5,7,8,12,38) | A x T, x0.01 if both arms disabled | 0.5 | 0 | 0.25 | 0.25 | |
| science, robotics, cooking (4,6,13) | Hd (falls through into the medic row) | 0.6 | 0.4 | 0.25 | 0.25 | |
| medic, turrets, hive medic, vet (9,11,14,15) | Hd | 0.6 | 0.4 | 0.25 | none | |
| thievery, lockpicking (10,37) | A, x0.01 if both arms disabled | 0.75 | 0.4 | 0.25 | none | |
| stealth (16) | A (unreached: [effective stealth](#effective-stealth)) | 0.9 | 0.9 | 0.8 | none | |
| athletics (17) | 1 | 0.7 | 1 | 0.5 | none | |
| dexterity (18) | A x T | 0.7 | 0 | 0.4 | none | x CharStats+0x44 if requested |
| defence (19) | A x Hd | 0.75 | 0 | 0.8 | none | |
| assassin (22) | 1, or A x0.01 if both arms disabled | 1 | 0 | 0.5 | none | x CharStats+0x40 |
| swimming (23) | A x T | 0.5 | 0 | 0.01 | none | |
| perception (24) | Hd | 0.6 | 0.5 | 0.33 | 0.25 | x `FUN_1406439f0` |
| crossbows (35) | Hd | 0.6 | 0.4 | 0.25 | 0.5 | x CharStats+0x50 |
| friendly fire (36) | Hd (falls through into the perception row) | 0.6 | 0.5 | 0.33 | 0.25 | x CharStats+0x50, x `FUN_1406439f0` |
| katana, sabres, hackers, heavy, blunt (25 to 29) | | | | | | x CharStats+0x48 only |
| all others (toughness, ...) | | | | | | 1.0 |

### Hunger and starvation

The medical object holds a **hunger level** (`+0x60`, 0 to 3, the UI shows level x 100 as "Hunger") and a
**stomach buffer** (`+0x64`, food waiting to be digested). A new character starts at 3.0 (constructor
`FUN_1406409f0` and `FUN_1406464c0`). **Verified** thresholds (the comparisons against 2.0 and 1.0 in the status
panel `FUN_14088a8f0`, the hunger term of the stat multiplier `FUN_1406457e0` and the speed factor
`FUN_140644d30`, and `../animation.md`'s starvation shape reading the same float):

| Hunger level | State |
| --- | --- |
| 2 or above | well fed, no problem |
| below 1 | "Starving": the character passes out below a toughness-dependent point; dies at 0 |

### Eating

**Verified** (decompile of `FUN_1405d29c0`, `FUN_1405d0780`, `FUN_140748d70`, `FUN_1405c7f70`, `FUN_1405c9d90`,
`FUN_14042ee50`). The character update (`FUN_1405dc650`, vtable +0x2b0) runs the auto-eat each update; there is no
eat task, animation or player command (a right click on food does nothing, [ui-screens](ui-screens.md)).
- **Wants food** (slot 0x340): a character of the player's faction or a slave (status +0x1a0 = 1) always,
  anyone else below hunger 2.1. **Hungry**: wants food, stomach (+0x64) empty, hunger < 2.5.
- **Order**: an animal (slot 0x248) eats nothing while the squad option Feed Animals (+0x114) is off. Hungry:
  own inventory, then the worn backpack's. Else, for a character of the player's side (`FUN_140856bc0`) with
  an empty stomach, hunger < 2.25 and Share Food (+0x115) on: the backpack of each squad member (squad order)
  within 200 units. Knocked-out characters eat too (the status text "You have passed out from starvation. You
  have food though...").
- **Choice** (`FUN_140748d70`): first `item function` 15 ("Possibly Edible": raw meat) the race eats (RACE
  `special food` lists it, or any for an animal), then 3 (food). Score: opened (charges +0x118 below full
  +0x114) (2 - charges/full) x 1000, else full / 100; the highest wins. Death items of a living owner skipped.
- **Bite** (`FUN_1405d0780`): n = min(charges, 300 - hunger, 50) (a limb, function 17: 30, eaten whole); the item
  loses n and goes at 0; stomach = n / 100; "Ate {1}" floats 15 up (white, alpha 0.9); Wwise switch
  Consume=Eat and `Character_Consume` within 200 units of the camera. The stomach then digests (hunger tick).
- Food records do not stack (`stackable` 1 on all 15 base food items, **Verified** by probe). `vampiric`
  (spiders, land bats, skimmers) is read only by the loader here; it concerns eating downed bodies.

### Free food

**Verified** (decompile of the hunger tick `FUN_14064da70` and slot 0x348 `FUN_1405c9ca0`). Each hunger tick,
before the drain: if the character's floor (slot 0x348) is above its hunger and its stomach is empty, the
stomach gets min(floor - hunger, 0.04). Nobody but the player has to feed anyone; NPCs hover at their floor.

| Character | Floor |
| --- | --- |
| animal (slot 0x248), only while the byte `142133599` (`GameplayOptions` +0x21, KenshiLib `animalsEat`) is 0: never, it is 1 in every game ([options](#new-game-advanced-options-that-change-these-mechanics)), so animals take the other rows and the player's animals get hungry. Meitou: no animal row | 3 |
| slave (status 1) | 1 |
| imprisoned (+0x2f8 = 2): held by the player's faction / free / escaped slave | -10 / 2 / 1.15 |
| player's faction | -10 (never) |
| anyone else | its platoon's +0x110: 3, or for a SQUAD_TEMPLATE with `malnourished` 1 + 1.2 x rand (`FUN_1407ee2d0`) |

### Corpses

**Verified** (decompile of `CharacterHuman::vfunc_29` `FUN_1405ccd90`, game clock `FUN_14066cc00` in hours). The
death time is set on the first update as a corpse. A corpse neither carried (+0x3d4) nor in a bed or cage
(+0x2f8 = 0) is destroyed ("corpse decayed") once more than 12 hours have passed; while held, the death time is
kept at least now - 6, so it lies 6 more hours once let go. Flies (**Verified**, same function): 4 game hours after death, while the area's weather has `affectCharactersType` 0 (none), the corpse of a non-robot race gets the effect
`Fly-Particle` (`55393-Newwworld.mod`) and the ambient sound `Corpse Flies` (`95425-sfx.mod`) and posts blood smell type 1 each pass (perception);
under a weather that affects characters it loses them (a weather record's `affect type`: dust storm and acid rain do, so a corpse in a storm has none and gets them when it clears). **Verified** (strings in the exe, `FindStrRefs`): the two record ids appear only in this function, which is slot 0xe8 of both the `Character` and the `CharacterHuman` vftables, so every kind of character gets flies (animals included), and nothing else does (no limbs, heads, meat or items). The effect is [`Fly-particle`](../formats/particle-universe.md) at the corpse's node. A corpse whose zone is not loaded is
destroyed at once ("corpse unloaded"), carried or not: the zone is the 64 x 64 grid cell of the corpse's x/z (`FUN_140a08b20`, `FUN_140a08ae0`: ZoneMap record 0x168 bytes from `+200`
of the zone manager) and "loaded" is its byte `+0xb1` (`FUN_140a090b0`; `+0xb0` = requested, `FUN_140a090d0`; `FUN_140a09360` = either). A position outside the grid is never removed.
No other test: nothing in the original ties a corpse to its squad, so a platoon unloading or despawning does not delete its dead.

**Which zones are loaded (Verified**, decompile of the zone manager, `DAT_1421349c0`): `+0xb1` is cleared by the unload `FUN_140a0a4f0` and set to 1 (with `+0xb0` = 0) for every zone of the load list
when the manager's state machine (`+0x1681d8`, `FUN_140a0f820`) goes from 4 (loading) to 5 (done). The list is filled by `FUN_140a0f080` (activate zone map, radius r, timer index, timer):
the zone and every neighbour within r. The camera feeds it: `FUN_140a0f590` asks for r = 1 (the 3 x 3 block) round the **centre zone** `+0x1681d0`, which is the zone of the
camera focus (`DAT_142134690`, the camera object's node at `+0x30`/`+0x58`) and moves only when the focus is more than 4147.2 (0.9 x 4608) from the centre zone's middle (record `+0xf8`/`+0x100`,
set by `FUN_140a0b950` as origin + half of 4608) on x or z (`FUN_140a0f820`). Each request (`FUN_140a0e570`) re-arms one of three per-zone timers (`+0xc0`, `+0xc4`, `+0xc8`, indexed by the caller; defaults
4, 6 and 5 s of real time, table `0x141748740`; a caller may pass its own); `FUN_140a0b830` counts them down by the real dt and unloads the zone (`FUN_140a0a4f0`) once all are 0, so a zone left by the
camera stays 4 s. A Town (not Nest, NestMarker or Null) also asks for its own zone set (r = 0, timer index 2; the requester is the town, not buildings, and there is no fixed 1 s): the zone of its position plus the zone being loaded when that is within one zone on both axes, asked once at load with the 5 s default (`FUN_14092dbb0`), then every frame while its loaded flag `+0x1ac` is set (`FUN_140929b20` from the town update `FUN_140929cf0`): while any zone of the set is requested or loaded (`FUN_140a09360`: flag `+0xb0` or `+0xb1`), every zone of the set is asked again with timer index 2 *set* (not maximised, `FUN_140a0e570`) to the largest of the **index 0 and 1** timers (`+0xc0`, `+0xc4`; `FUN_140a09380` never reads `+0xc8`) among the loaded zones of the set, or to the 5 s default when that is not positive. **Verified** (decompile of `FUN_140929b20`, `FUN_140a09380`, `FUN_140a0e570`, `FUN_140a0b830`): the town only copies the camera's / player's remaining timers onto the other zones of its set, so its zones lapse together with the last of them (the zone update counts all three down and unloads at the first frame all are spent; the town's slot, a copy of that maximum, is spent in the same frame); nothing extends a zone. The 5 s default is written every frame only when no loaded zone of the set has a live index 0 or 1 timer (zones asked by the town alone), which would hold them for good; every load trigger found also covers the town's zone by the camera block or the player box (**Observed**, not provable from the code). With the last zone gone the town clears `+0x1ac` and releases its registrations (`FUN_140929b20`); `+0x1ac` is otherwise written only by the load `FUN_14092dbb0` and the null town's constructor `FUN_140934fe0`. Zones are unloaded by `FUN_140a0b830` alone, and in bulk by the world teardown (`FUN_14036c320`, `FUN_140a0c580`). So the original does **not** keep a visited town loaded until a reload (the earlier reading was wrong): it unloads about 4 s (camera) or 6 s (player box) after the last request, and the town's platoons unload with it ([persistent platoons](factions-squads-towns.md#1-object-model)). While a zone is loaded every character in it is active and updated by the world's tiers ([update budget](game-loop.md#the-character-update-budget): 8 logic ticks and 6 full updates a frame, round robin, for the unflagged, so a far character is visited every N/6 frames with its accumulated time), the original's only LOD. The town list's update (`FUN_14092d850`) runs the town step for every town each frame, and the production step for one loaded town a frame, only while `+0x1ac` is set. Meitou's town sites keep 60 s of grace instead of 4-6 s ([engine choice](../simulation.md#population)); `AskTownZones` copies the same maximum. A camera jump (`FUN_140872e00`) asks for the new place at once.
**The box (Verified**, decompile of `FUN_140a0f640`; constant `DAT_14174a100` = 340.0): every zone overlapping the box of +-340 round a point is asked for with radius r = the `Fast zone hopping` setting (`DAT_142133518`, the storage of that name registered by `FUN_1403eca90`; 1 when on, else 0) and timer index 1 (6 s). Its callers are the character frame update `FUN_1405cfd90` and the character update `FUN_1405c7b20`, each for a living character whose faction is the player's (`FUN_1407916d0`: the character's vtable `0x58` object, its faction (**Observed**), has `+0x250` set), so NPC characters ask for nothing. The unloaded platoon's activation check `FUN_1407ebf30` asks for the platoon's position every time it runs when the platoon's faction is the player's (**Verified**; Meitou `RefreshLoaded`), and the platoon step `FUN_1404ffad0` calls it too (**Observed**); Meitou asks for every platoon of that faction, loaded or not. Timer defaults (**Verified**, the floats of `0x141748740`, `...744`, `...748`): 4, 6, 5 s.
A corpse is removed when no request holds its zone: the camera's block 4 s after it last covered the zone, the player's box 6 s after. Corpses are not written to a zone file.

### What a body part is

A character's parts come from its RACE `combat anatomy` reference list (**Verified** reader `FUN_14064dd10`; every one of the 53 base-load races lists 7 LOCATIONAL_DAMAGE references,
except the No-Head MkII skeleton with 6; there are 9 LOCATIONAL_DAMAGE records in all, the two extra ones being
the forelegs). Each reference carries two integers. **Observed**: value 1 is the part's **base
HP** (100 for humans, 250 Garru, 75 hive workers, 2500 to 5000 Leviathan) and value 0 its **hit weight** (a
relative chance to be struck; the hit resolver `FUN_1406508d0` builds a cumulative-weight map from it and draws a
random number). The assignment of the two integers to the two constructor arguments is inferred, not visible in
the decompile. Base-game anatomies (value 0 / value 1, probe over RACE `combat anatomy`):

LOCATIONAL_DAMAGE records (**Verified**: all 9 base records by probe; loader `FUN_14064d0e0`; Head, Chest,
Stomach, two arms, two legs, two forelegs):

| Record | type | collapses | death (vital) | severance | collapse part | affects move speed | affects skills | KO mult |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Head | 3 HEAD | yes | yes | yes | 1 | 0 | 0.6 | 2 |
| Chest | 0 TORSO | yes | yes | no | 1 | 0 | 0 | 1 |
| Stomach | 0 TORSO | yes | yes | yes | 1 | 0.3 | 0 | 1 |
| Left Arm | 2 ARM | yes | no | yes | 4 | 0 | 0.25 | 0.5 |
| Right Arm | 2 ARM | yes | no | yes | 2 | 0 | 1 | 0.5 |
| Left / Right Leg | 1 LEG | yes | no | yes | 32 / 16 | 0 | 0 | 0.5 |
| Left / Right Foreleg | 2 ARM | no | no | yes | 4 / 2 | 0 | 0.25 / 1 | 0.5 |

### Per-part state (`MedicalSystem::HealthPartStatus`)

| Offset | Meaning |
| --- | --- |
| +0x00 | LOCATIONAL_DAMAGE record |
| +0x08 | body part type 0 to 3 |
| +0x10 / +0x18 | medical system / character |
| +0x20 | side (0 none, 1 left, 2 right) |
| +0x28 | fitted replacement limb (non-null: robotic) |
| +0x30 / +0x31 / +0x32 | `self healing` (RACE) / `collapses` / vital (`death`) |
| +0x34 | `KO mult` |
| +0x38 / +0x3c | hit weight (ref value 0) / transient hit-focus multiplier (starts 1) |
| +0x40 | flesh HP **h** (may go negative, down to -3 M) |
| +0x44 | **stun** s (blunt damage lands here) |
| +0x48 | **bandaged pool** b: damage that has been treated and is waiting to be healed |
| +0x4c | **rig** (save key `rig`): the splint, added to h - s by the fraction, the collapse and the sever tests ([Splints](#splints)); not in the medical row's arrows, number or tooltips, only its white layer, rig x 0.01 (**Verified** `FUN_14088a8f0`, [ui-screens](ui-screens.md#4-hud-mainbargui)) |
| +0x50 | **wear** w (permanent loss of maximum, robots) |
| +0x54 / +0x58 / +0x5c | base HP (ref value 1; forced to 5 for a stump) / multiplier (set by the constructor and rewritten each tick from the character slot-0x390 factor) / body scale (character slot 0x238) |
| +0x60 | health fraction `((h - s) + extra) / M` |

### Limb replacements and robots

A LIMB_REPLACEMENT record (`slot` 50 left arm, 51 right arm, 52 left leg, 53 right leg; **Verified**) becomes a
`RobotLimbItem` with a `StatsEnumerated -> float` multiplier map. Each value is stored as two fields, `X` at the
lowest quality and `X 1` at the highest, scaled by the item's `level` (Gear +0x224 int, +0x220 = level x 0.01), not
the save's `quality` float. Constructor `read_athletics_mult_1_n17` (`0x1400ce2d0`; **Verified**, disassembly and
constant bytes):
- Skill multipliers (arms 50/51: strength, dexterity, thievery for 10 and 37, ranged; legs 52/53: athletics,
  stealth) via `FUN_1400cd500(level, X, X 1)`: if X < 1 <= X 1, level >= 40 gives lerp(clamp((level-40)/55,0,1), 1, X 1)
  and level < 40 gives lerp(clamp((level-5)/35,0,1), X, 1), so the grade bands 5/40/95 hit X, 1, X 1; otherwise
  lerp(level x 0.01, X, X 1).

Per limb slot k (0 L arm, 1 R arm, 2 L leg, 3 R leg) the character holds a state (0 original, 1 stump, 2
replaced, 3 crushed) and the fitted item (holder `FUN_1400cd3f0`; **Verified** by the save code below).
- Fit (`FUN_1406445b0`): part base HP = item HP, wear = item `wear`, stun = item `stun`, flesh =
  min(base - wear - item `dam`, base - wear). Remove: the item takes `dam` = base - wear - flesh, `wear`,
  `stun`; the part becomes base 5, flesh 5, wear/stun 0. On load the item is fitted keeping the saved part floats.
- Sever (`FUN_14064edc0`) returns for a replaced slot; from state 0 or 3 it makes a stump, awards toughness,
  drops the RACE `severed limbs` item of the slot at `bone name 2` (the first entry of that slot whose target resolved; a reference to a
  missing record is skipped, and with no entry nothing drops: no default item) and opens a wound. The item takes that bone's pose and falls from it ([collision](../formats/collision.md#item-physics)).
- CHARACTER `starting health` (LOCATIONAL_DAMAGE, val0), applied to a new (not loaded) character (`FUN_140645ef0`, **Observed**): with
  M the part's maximum, val0 < -M makes the limb a stump (flesh 0, bandaged pool 5); else flesh = clamp(val0, -M, M) and the
  bandaged pool M - flesh. **Verified** by probe: Rock Bottom's `Naked dude` has Left Arm -150, a left-arm stump. Meitou ignores it on non-limbs. **Verified** (decompile): an arm or leg entry with val0 >= -M also becomes a stump when that limb is already not original (an earlier entry or fit changed it); a non-limb entry below -M is a stump in the original too (it writes past its 4-slot limb table, not copied; the base game has none: all 8 entries are arms or legs).
- A stump shows the RACE `limb replacement` record whose `slot` is 50 + k (`uses_limb_replacement`); a crushed
  limb attaches no mesh. Hidden body vertices: [characters.md](../characters.md#hidden-body-parts-verified--140075600--140071f90--140531cc0-hlsl-skinhlsl).
- Meitou in play: fitted items sit in the inventory's `*_replace` sections (the limbs window), weigh nothing, are not traded and
  are saved only through MEDICAL_STATE `limbs`. Dropping a LIMB_REPLACEMENT whose `slot` is 50 + k on slot k fits it when limb k is a
  stump; lifting it out removes it (a right click puts a carried one on its stump or takes a fitted one off). The original cannot fit over a crushed limb: its slot is disabled
  ([limbs window](ui-screens.md#limbs-window)); fitting and removing are instant, with no medic or timer (`FUN_140645690`, `FUN_1400d5a30`, **Verified** in the decompile).

| Slot | Fields | Stat numbers |
| --- | --- | --- |
| all | `swimming mult`, `HP` (/`HP 1`), `unarmed damage bonus` (/`... 1`), `overall mult` | 23, part HP |
| arms | `strength mult`, `dexterity mult`, `thievery mult` (applied to both 10 and 37), `ranged mult` | 1, 18, 10 and 37, 35 |
| legs | `athletics mult`, `stealth mult` | 17, 16 |

Part HP is `HP` to `HP 1` by quality (level x 0.01), rounded to the nearest 5, half up (**Verified**, `FUN_1400ce2d0` -> `FUN_1409b2310` with 5; 70..200 at level 95 gives 195).
A race switched in the character creator keeps the prosthetics fitted at the start (Meitou, #360).

### Races

RACE fields for this subsystem (loader `FUN_14042efa0`; **Verified** field by field: the reader's store offset
and a consumer that reads the same offset):

| Field | Meaning | `RaceData` offset | Consumer |
| --- | --- | --- | --- |
| `heal rate` | multiplier of part healing and blood recovery | +0x58 | `FUN_140652000` |
| `bleed rate` | multiplier of blood loss (0 = bloodless) | +0x5c | `FUN_14064f300`, `FUN_140646140` |
| `heal stat` | `StatsEnumerated` of the first-aid skill | +0x88 | `FUN_140649a00`, `FUN_1408c6130` |
| `hunger rate` | multiplier of starvation speed | +0x70 | `FUN_1408837a0`, `FUN_14064da70` |
| `min blood`, `max blood` | blood at weakest / strongest | +0x50 / +0x54 | `FUN_1406440a0` |
| `speed min skill`, `speed max skill` | run speed at athletics 0 / 100 | +0x4c / +0x48 | `FUN_140883db0` |
| `walk speed`, `swim speed mult`, `swim offset`, `water avoidance` | movement; water avoidance a becomes a + 1 if a >= 0 else 1 / (1 - a) | +0x64, +0x60, +0x68, +0x6c | `FUN_140886460`, pathfinding |
| `vision range mult` | detection range: outdoor vision range = 1500 x this ([perception.md](perception.md#sight)) | +0x74 | AI |
| `is robot`, `swims`, `carriable`, `single gender`, `gigantic`, `vampiric`, `no hats`, `no shirts`, `no shoes`, `cant enter buildings` (stored inverted) | flags | +0x7c, +0x79, +0x7a, +0x7b, +0x78, +0x7d, +0x7e, +0x7f, +0x80, +0x9c | many |

`gigantic` (race +0x78) raises the Ogre rendering distance of the character to D x 7 (`FUN_140539020` :225-228, constant 7.0, **Verified**), and the per-frame visibility pass `FUN_1405c9f60` :44-52 skips the beyond-D clear for it (see [combat](combat.md)). D is the `npc range` setting ([settings](../formats/settings.md)). Other races are also hidden when the position (feet) is farther than D from the eye (`FUN_1405c9f60`); Ogre's own test uses the bounding sphere (centre minus radius), as Meitou does. Meitou: `RaceData.GiganticDrawScale`, `CharacterInstance.DrawDistanceScale`, the far test in `CharacterRenderer`.
| `extra attack slots` | added to CONSTANTS `max num attack slots` (1) | +0x84 | combat (`FUN_1406658c0`, ...) |
| `self healing` | slow healing without bandaging (read per part) | per part | `FUN_14064dd10` |
| `strength`, `dexterity` | stat multipliers (starting value and XP rate) | map entries 1, 18 | all XP code |
| `stats good<N>`, `stats bad<N>` | x1.2 / x0.8 for the listed stat numbers | map at +0xa0 | all XP code |
| `weather immunity<N>` | WeatherAffecting kinds ignored | set at +0xe0 | weather |
| `special food` | extra ITEMs the race can eat | set at +0x00 | eating |
| `vampiric` | "when eating, sucks nutrition instead of eating limbs" (editor text). **Verified** (`FUN_1404356f0` passes the EATER's flag to `FUN_140650370`): no part is eaten; each update d = rate x dt x 0.5 (rate as for limbs), the victim's hunger level (+0x60) loses d x 0.01 and its blood (+0x70) d x 0.07; at hunger -0.5 the eaten-pass count (death past 5) starts. Robots are untouched. Meitou: `MedicalState.BeingEaten` (no starvation death while being eaten) | +0x7d | eating |
| `combat anatomy`, `severed limbs`, `limb replacement` | body parts, dropped limbs per slot, default prosthetics | references | see above |

### New-game advanced options that change these mechanics

Saved per world only, in the save's CAMERA record (the "Key" column), never in `settings.cfg` (**Verified**, decompile: `saveGame`
`FUN_140375360` calls the saver `FUN_1403ee910` on the globals, `loadGame` `FUN_140373f00` calls the loader `FUN_1403eef20` on them; the save list's
preview `FUN_14047e390` reads a copy; key strings read from the exe at `1416ce7f0..1416ce810`; 6 original saves carry all ten keys, one with `ht` 8):
- Defaults (`FUN_1403e75d0`, the struct's default setter) and loader start: every float 1, +0x4 0, +0x20 and +0x21 1, +0x22 0. **Verified** (callers of `FUN_1403e75d0`): the
  globals get the defaults once, from the start-up initialiser `FUN_14037dce0`; the other callers (`FUN_14047b480`, `FUN_140914c40` Continue, `FUN_14047f4a0`) run it on a local copy
  and `FUN_1403829c0` on an embedded one, and a new game (`FUN_14047c310` kind 4) does not touch the globals. So the New game window (`FUN_1409151d0` binds its sliders to the globals)
  keeps the last edit, the import window's pick or the last loaded save's values; Meitou: `PlayerInterface.NewGameOptions`, set from the save in `SwapInSave`.
- Without float `cod` the loader keeps those defaults and reads nothing. With it: `cod gdm ps rs bs` and bool `ep` are read
  unconditionally (a missing one would read 0 / false), `ht`, `nnm`, `bl` and bool `heal` (into +0x22) only when present. No clamping.
- The saver writes the seven floats and `ep`, `bl`; not `heal` (also a squad-toggle key) nor +0x21. Nothing reads +0x22.

| Option label | Key | Global | Slider range | Effect found |
| --- | --- | --- | --- | --- |
| Hunger time | `ht` | 142133594 | 0.25 to 8 | multiplies `starvation time` |
| Chance of death | `cod` | 142133578 | 0.5 to 4 | multiplies the bleed rate (`FUN_140646140`) and the wound degeneration (`FUN_14064f5c0`); it does **not** touch starvation: the starvation time reads the *Hunger time* float, which is at +0x1c of this block (**Verified**: addresses of the slider bindings in `FUN_1409151d0` and the writer `FUN_140912f80`) |
| Global damage multiplier | `gdm` | 142133580 | 0.5 to 4 | multiplies damage and combat XP ([combat.md](combat.md#global-tuning-the-constants-record)) |
| Building speed / Research speed / Production speed | `bs` / `rs` / `ps` | 142133584 / 14213358c / 142133590 | 0.5 to 2 | construction, research and production rates, [buildings-production.md](buildings-production.md#construction-tick) |
| Bandits loot the player ("Makes life unfair"; default on) | bool `bl` | 142133598 (byte) | checkbox | NPCs may loot the player's downed characters ([NPC looting](carry-loot.md#33-npc-looting)) |
| Easy prospecting (no help text; default off) | bool `ep` | 14213357c (byte) | checkbox | none: **Verified** that nothing reads it in 1.0.68 (the only RIP-relative references, by a scan of the exe's `.text`, are the writer `FUN_140912f80` and this UI; the struct's own methods load/save/reset `FUN_1403eef20`/`FUN_1403ee910`/`FUN_1403e75d0` and `getStarvationTimeInHours` `FUN_1403e7620` only copy it or read `+0x1c`). A dead option |

### CONSTANTS used by this subsystem

One loader (`FUN_14086b2b0`) reads the "GLOBAL CONSTANTS" record (`110-gamedata.quack` in `gamedata.base`) into the
block at `142133dd0` (`field address = 0x142133dd0 + 4 * index`). **Verified**: for every row below the loader's
read and at least one consumer found through the data cross-references; base values from a probe over CONSTANTS
(editor defaults in brackets). "Stored" is the scaling the loader applies.

| FCS field | Base | [Editor] | Stored | Used by |
| --- | --- | --- | --- | --- |
| `damage multiplier` | 0.65 | [8.0] | multiplies the damage values below | combat |
| `bleed rate` | 0.01 | [0.01] | x0.1 | `FUN_14064edc0`, `FUN_1406508d0` |
| `immediate blood loss` | 0.2 | | x1 | `FUN_14064f300` |
| `bleeding clot rate` | 0.0085 | [0.0005] | x0.1 | `FUN_140646140`, `FUN_1406464c0` |
| `extra blood loss from bodyparts` | 1 | [1] | x1 | `FUN_140646140` |
| `blood recovery rate` | 0.4 | [0.3] | x0.1 | `FUN_140652000` |
| `bodypart degeneration rate` | 1.5 | [1.0] | x1 | `FUN_14064f5c0` |
| `degeneration mult 1` / `99` | 1.7 / 0.03 | [3 / 0.5] | x1 | `FUN_140652000` |
| `knockout mult 1` / `99` | 3 / 0.75 | [3 / 3] | x1 | `FUN_1406447d0`, `FUN_140644980` |
| `knockout time base` | 20 | [30] | x1 | same |
| `min` / `max toughness ko point` | 10 / 85 | [10 / 99] | x1 | `FUN_140643ae0`, `FUN_140644320` |
| `stun recovery rate` | 1 | [1] | x1 | `FUN_14064f5c0` |
| `blunt permanent organ damage` | 0 | | x1 | `FUN_14064f300`, `FUN_140644a70` |
| `heal rate mult` | 0.25 | [1.0] | x1 | `FUN_14064f5c0` |
| `resting heal rate mult` | 2 | [1.0] | x1 | `FUN_14064f5c0`, `FUN_140652000` |
| `medic speed mult` | 3 | [1.0] | x1 | `FUN_140649a00`, `FUN_140649d50`, `FUN_14064d800` |
| `medkit drain 1` / `99` | 20 / 1 | [1.0 / 0.05] | x0.1 | `FUN_140644250` |
| `robot medic speed mult` | 0.33 | [1.0] | x1 | `FUN_140644250`, `FUN_140649a00` |
| `robot wear rate` | 1 | [1.0] | x1 | `FUN_14064f300` |
| `XP rate medic 1` / `99` | 15 / 150 | [10 / 300] | x1 | `FUN_1408c6130` |
| `starvation time` | 60 | [24] | x1 | `FUN_1403e7620` |
| `fed recovery rate mult` | 3 | [4.0] | x1 | `FUN_14064da70` |
| `bed hunger rate` | 0.33 | [0.4] | x1 | `FUN_1408837a0` |
| `encumbrance hunger rate` | 0.7 | [0.7] | x1 | `FUN_1408837a0` |
| `food quality mult` | 0.5 | [1.0] | x1 (stored as is; it scales the `charges` of an ITEM of function 3 or 15 above 1 charge, raw meat included, **Verified** `FUN_14075faf0`. It is `food price mult`, not this field, that is multiplied by `global price mult`: [economy.md](economy.md#3-constants-price-block)) | item constructor `FUN_14075faf0` |
| `exp gain multiplier` | 3 | [3.0] | x0.25 | all XP |
| `skill diff xp 2x bonus` / `0x penalty` | 10 / 25 | [10 / 20] | x1 | `FUN_1408c5fa0` |
| `min strength xp mult` | 0.1 | [0.05] | x1 | `FUN_1408838e0` |
| `weight strength diff 1x` / `max` | 20 / 1 | [20 / 1.5] | x1 | `FUN_1408838e0`, `FUN_1408839a0` |
| `xp rate strength` | 0.5 | [1.0] | x1 | `FUN_1408c6980` |
| `xp rate strength from walking` | 0.5 | [1.0] | x1 | `FUN_1408c60c0` |
| `xp rate athletics` | 1 | [1.0] | x0.5 | `FUN_1408c6660` |
| `xp rate toughness` | 1.33 | [1.33] | x1 | `FUN_1408c68e0`, `FUN_1408c6980` |
| `encumbrance base` | 15 | [10] | x1 | `FUN_140884780` |
| `carry weight mult` | 1.2 | [1.2] | x1 | `FUN_140884780` |
| `carry person weight` | 30 | [40] | x1 | `FUN_1405c89d0` |
| `weapon inventory weight mult` | 0.5 | [0.5] | x1 | `FUN_140889cd0`, `FUN_140898830` |
| `max num attack slots` | 1 | | x1 | combat |
| `unarmed damage mult` | 0.8 | [1.5] | x1 | `FUN_1408868b0` |
| `damage resistance min` / `max` | -0.65 / 0.65 | | x1 | `FUN_140644770`, `FUN_1406508d0` |
| `attack chance factor` | 0.05 | | x1 | `FUN_140665370` |
| `minimum lockpick chance` | 5 | | x0.01 | lockpicking |
| `appearance random deviation percentage` | 0.4 | | x0.5 | [../characters.md](../characters.md) |

## Unknowns

- What the `dt * 0.05` factor and the small `0.04` / `0.002` / `1.2` constants in the medical update stand for.
- Which callers feed `FUN_1408c7250` / `FUN_1408c7310` for which activity (`DAT_142133794` is the scaled frame time, [game-loop.md](game-loop.md#the-three-time-values)), and the 0.1 floor / 6x cap of the skill-difference factor
  (not in `FUN_1408c5fa0`; maybe clamped by its callers). Meaning of XP event 6.
- Which of the two ints of a `combat anatomy` reference is HP and which the hit weight (the part constructor
  `FUN_14064d0e0` takes the weight into +0x38, the HP into +0x54 and a scale into +0x58, but the call arguments
  are hidden in the decompile, so the assignment is inferred from consumers and data), and the meaning of the
  character slot +0x248.
- Why the leader of issue #134 run 20261024-172646 was down before any KO with the stomach at -20 or -40 (toughness 100, KO point -85) and stayed down after
  the timer ran out, when the chest or head at the same depth left it up: the stage (`FUN_140644450`), collapse (`FUN_140649320`) and KO-time (`FUN_1406447d0`)
  code read the same vital byte (part +0x32) and the same fields for both torso parts, so the decompile predicts no difference. A coma flag (+0x162) set
  earlier in the run would do it (it stays set while any vital part is below 0, [Blood and KO](#blood-and-ko)); the run's history does not show it.

## Expression reference

- Levelling: `0.1 * 0.75 = 0.075`
- Hunger and starvation: `T = starvation time * HungerTime`; `moved = min(buffer, dt / T * k * fed recovery rate mult * race hunger rate)`
- Sleep, beds and resting: `Character+0x2f8 == 1`
- Blood and bleeding: `blood_max = min blood + (max blood - min blood) * s * k * z`
- Per-part state: `M = base * mult * scale - w`; `== 1`; `== 2`
- Damage into a part: `Dismemberment=1`
- Degeneration, healing, stun: `D = clamp(((M - h - b) * 100 / M - 20) / (90 - 20), 0, 1)`; `degMult = lerp(degeneration mult 1 (1.7), degeneration mult 99 (0.03), toughness / 100)`; `heal = min(b, dt * 0.05 * heal rate mult (0.25) * R)`; `h <= M`; `R = (M / 100) * A`
- Knock-out, coma, death: `k = lerp(knockout mult 1 (3.0), knockout mult 99 (0.75), toughness / 100)`
- First aid, medkits and robot repair: `h + b <= M`; `rate = lerp(1, 15, min(skill, kit quality) * 0.01) * medic speed mult (3)`, one part per update ([First aid update](#first-aid-update))
- Encumbrance and carrying: `C = carry weight mult * strength * strengthInjuryMultiplier + encumbrance base` (strength's whole stat multiplier); `e = clamp((C - 0.05 W) / (0.95 W), 0, 1)`; `W <= C`
- CONSTANTS used by this subsystem: `field address = 0x142133dd0 + 4 * index`
