# Combat

How Kenshi resolves a fight: which attack gets chosen, whether it is blocked or dodged, how much damage the
weapon and the wielder produce, how armour and toughness reduce it, how the damage lands on a body part, and
what makes a character fall, black out, lose a limb or die. Also the XP a fight gives. Character records,
stats and weapon choice are in [characters.md](../characters.md); animation playback (which the block and
dodge outcome depends on) is in [animation.md](../animation.md). FCS record formats are in
[fcs-mod.md](../formats/fcs-mod.md).
The stat numbers, healing, hunger and the effective-stat multipliers that feed these formulas are in [character-stats.md](character-stats.md); the clock and the scaled `dt` are in
[game-loop.md](game-loop.md#the-three-time-values); the saved body-part state is in [../formats/save.md](../formats/save.md#platoon-files-squads-and-their-characters); the AI that picks attacks is in
[ai.md](ai.md) and [ai-tasks.md](ai-tasks.md); item prices of weapons and armour are in [economy.md](economy.md#4-item-base-value-itemvalue-fun_140896e20); relations that decide who fights
are in [factions-squads-towns.md](factions-squads-towns.md#3-relations).

Sources: decompilation of `kenshi_x64.exe` (Ghidra) and of the editor `forgotten construction set.exe`
(ILSpy, for enum names only), plus the base game's `gamedata.base` read through a probe. Addresses are in
`kenshi_x64.exe`. Field names are FCS field names. Labels: **Verified** (checked against the game data, or
two independent places in the binary, as stated), **Observed** (one function read, not cross-checked),
**Unknown**. Nothing here has been compared with in-game behaviour. Formulas are written as maths, not
transcribed; `lerp(t, a, b) = a + (b - a)·t` (`140015b63`; [character-stats.md](character-stats.md#effective-stats-injuries-and-hunger) writes the same function as `lerp(a, b, t)`, the game's function takes `t` first).

## Overview of a hit

1. The attacker's AI picks a combat technique (`COMBAT_TECHNIQUE`) by weapon type, skill and distance
   (**Unknown**: the attacker-side choice was not read; `140887970` below is the defender's reaction chooser).
2. When the blow arrives, the defender's *current animation* decides the outcome: blocking in the right
   direction (and past half its animation), or mid-dodge, means no hit. The skill roll happens earlier, when
   the defender picks its reaction technique (**Observed**).
3. A hit builds a damage packet (cut, blunt, pierce, stun, bleed, armour penetration) from the weapon, the
   wielder's stats and the target type.
4. The medical system picks a body part, applies armour and toughness, then moves flesh, stun and blood.
5. Knockdown, KO, dismemberment and death follow from part and blood state; XP is awarded to both sides.

## Class map

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

## Enums (from the editor's decompile; facts)

- StatsEnumerated (full table with `CharStats` offsets in [character-stats.md](character-stats.md#the-stat-list)): 1 STRENGTH, 2 MELEE_ATTACK, 9 MEDIC, 11 TURRETS, 16 STEALTH, 17 ATHLETICS, 18 DEXTERITY,
  19 MELEE_DEFENCE, 20 WEAPONS, 21 TOUGHNESS, 22 ASSASSINATION, 24 PERCEPTION, 25 KATANAS, 26 SABRES,
  28 HEAVYWEAPONS, 29 BLUNT, 30 MARTIALARTS, 31 MASSCOMBAT, 32 DODGE, 34 POLEARMS, 35 CROSSBOWS,
  36 FRIENDLY_FIRE, 38 SMITHING_BOW; 39 END. (Other values are non-combat skills.) Derived stats follow
  the real ones: `_PrimaryWeaponDamage`, `_DamageResistance`, `_KnockoutTime`, `_ToughnessKnockoutPoint`,
  `_WoundDeteriorationSpeed`, `_combatSpeed`.
- WeaponCategory: 0 SKILL_KATANAS, 1 SKILL_SABRES, 2 SKILL_BLUNT, 3 SKILL_HEAVY, 4 SKILL_HACKERS,
  5 SKILL_UNARMED, 6 SKILL_BOW, 7 SKILL_TURRET, 8 ATTACK_POLEARMS, 9-20 creature attacks (ELEPHANT, DOG,
  BULL, ROBOTSPIDER, SPIDER, CAGEBEAST, DUCK, GORILLA, GAR, FROG, GOAT, GIRAFFE), 21 ATTACK_NULL.
- ArmourClass: 0 GEAR_CLOTH, 1 LIGHT, 2 MEDIUM, 3 HEAVY. ArmourType: 0 CLOTH, 1 LEATHER, 2 CHAIN,
  3 METAL_PLATE. ArmourRarity: 0 PROTOTYPE .. 5 MASTER (grade to quality in characters.md).
- CutDirection: 0 DEFAULT, 1 DOWNWARD, 2 LEFT, 3 RIGHT, 4 THRUST, 5 UPWARDS, 6 PIERCED, 7-9 REAR_*.
  HitMaterialType: 0 MISSED, 1 METAL, 2 FLESH, 3 SAND, 4 WOOD, 5 SWORD, 6 CHAIN.
  BodyPartType: 0 TORSO, 1 LEG, 2 ARM, 3 HEAD. RagdollPart bit flags: WHOLE 1, RIGHT_ARM 2, LEFT_ARM 4,
  HEAD 8, RIGHT_LEG 16, LEFT_LEG 32. ProneState: NORMAL, STAYING_LOW, CRIPPLED, PLAYING_DEAD, KO.

## Global tuning: the CONSTANTS record

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
| bow damage 1 / 99 | 1.1 / 1.3 | stored unscaled at fields 8 and 9; data xrefs show `14043c180` (crossbow gun data) reads both, but the decompile does not show how (**Observed**) |
| stumble damage max | 80 | stumble threshold |
| unarmed damage mult | 0.8 | |
| attack chance factor | 0.05 | hit roll skew |
| base block chance | 70 | |
| block chance increase / reduction per 10 levels | 12 / 15 | stored ×0.1, i.e. 1.2 and 1.5 per level |
| max num attack slots | 1 | enemies that may melee one target (+ RACE `extra attack slots`) |
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

A second struct, set by the difficulty dialog ("Advanced options": built by `1409151d0`, copied into the globals
by `140912f80`, nine floats from `142133578`), holds `142133580`, the global damage multiplier, used in the hit
pipeline (`1406508d0`), fist injury (`140666780`), crossbow damage (`14043c180`) and XP scaling; and
`142133578`, which scales wound deterioration and blood loss (`14064f5c0`, `140646140`). **Verified**: the
dialog builder `1409151d0` binds the "Global damage multiplier" slider to `142133580` and the "Chance of death"
slider (range 0.5-4) to `142133578`, and the tooltip says it "directly affects rates of wound degeneration and
blood loss"; the same dialog binds Hunger time to `142133594`, Production speed to `142133590`, Research
speed to `14213358c`, Building speed to `142133584` and Number of nests to `142133588`.
`settings.cfg` "Dismemberment" (`142133504`; [character-stats.md](character-stats.md#damage-into-a-part)): 0 none; 1 rare, a limb is lost only when a hit drives its flesh
below the negative maximum; 2 frequent, lost whenever flesh is below it (**Observed**, `14064f5c0`/`14064f300`).

## Weapons

### Stats of a weapon item

Computed once per weapon by `140889cd0` from the WEAPON record, the item's quality fraction q (0..1), the
WEAPON_MANUFACTURER and the MATERIAL_SPECS_WEAPON record. **Observed**.

- cut multiplier = lerp(q, 0.3, 2.0) · `cut damage multiplier` · manufacturer `cut damage mod`.
- blunt multiplier = lerp(q, 0, 2.0) · `blunt damage multiplier` · manufacturer `blunt damage mod`.
- min cut = `min cut damage mult` · manufacturer `min cut damage`.
- weight (item field `+0x120`) = the larger of `weight kg` · manufacturer `weight mod` and 40 · blunt multiplier
  · `weight mult` (WEAPON field) · the global weapon weight multiplier, which is the CONSTANTS field `weapon
  inventory weight mult` (`142133ee8` = struct field `0x46`, value 0.5); the kg term is not scaled by those
  two. A second copy at `+0x230` is the larger of the kg term and 40 · blunt multiplier, unscaled.
- `defence mod`, `attack mod`, `indoors mod` (ints) come from the WEAPON record, with the material's
  `attack mod` / `defence mod` added. `bleed mult`, `length`, `armour penetration`, `pierce damage multiplier`
  and the damage multipliers against robots, humans and animals (and per-race `race damage`, ×0.01) are
  copied to the wielder's stats when the weapon is equipped (`140897f30`, which also stores the weapon's cut
  and blunt multipliers). **Observed**. (The FCS field is `pierce damage multiplier`; `pierce multiplier` was
  a shorthand.)
- Weapon category (above) picks which skill applies: category 0 katanas, 1 sabres, 4 hackers, 2 blunt, 3 heavy
  weapons, 6 bow, 7 turret, 8 polearms, 5 unarmed/default (`140897f30` maps the category to the skill slot of
  CharStats). **Observed**.

The base game has 48 WEAPON records, 7 CROSSBOW, 6 GUN_DATA, 44 COMBAT_TECHNIQUE, 9 LOCATIONAL_DAMAGE
(dumped from `gamedata.base` with a probe, re-counted: **Verified**). WEAPON `pierce damage multiplier` is
non-zero on exactly 11 of the 48 records, all creature or robot-creature weapons (bull weak 0.4, garru 0.4,
dog 0.5, duck 0.5, bull, elephant, giraffe, robot spider, skimmer, spider, small spider 1); all swords, clubs,
polearms, cleavers and fists are 0 (**Verified**, counted). `armour penetration` is negative on katanas and
other thin blades (katana and guardless katana -0.3, nodachi -0.2, longsword -0.15, ...), positive on clubs
and cleavers (0.1-0.3) and 0 on creature weapons (**Observed** from the same dump).

### Damage per blow

S is the wielder's CharStats, `skill` its weapon-category skill, `str`, `dex` strength and dexterity (each
multiplied by the condition factor of `1406457e0` ([character-stats.md](character-stats.md#effective-stats-injuries-and-hunger)), which lowers a stat as the relevant limbs are hurt and by
the stat multiplier `+0x34`). All **Observed** (`140883ba0`, `140883c60`, `140883d10`).

- cut = lerp( (skill + dex·m)·cutMult / 200, cutBase, cut99 ), with cut99 the stored `cut damage 99` (0.65·80
  = 52) and cutBase the stored global `cut damage 1` (0.65·20 = 13) - not a weapon field. Blunt-dominant
  weapons (cut multiplier strictly below blunt multiplier) instead use 0.4 · the stored `blunt damage 1` as
  cutBase (`140897f30`).
- blunt = lerp( (0.5·skill + 1.5·str·m)·bluntMult / 200, bluntBase, blunt99 ), parameter not clamped; blunt99 =
  stored `blunt damage 99` (52). The bluntBase of non-blunt-dominant weapons is not decoded (the blunt-dominant
  case sets it to the stored `blunt damage 1`, 13).
- pierce = 0.78·(skill + dex·m)·pierceMult + 10·pierceMult, where 0.78 = the stored `pierce damage multiplier`
  (0.65·1.2).
- The packet (`1408868b0`) is {cut, blunt, pierce, stun 0, bleed multiplier, armour penetration}, then scaled
  by the weapon's multiplier for the target kind (robot, human, animal), the per-race damage map and a
  general multiplier. The armour penetration slot is only filled when a target is passed in.
- Unarmed (category 5): martial arts skill f = lerp(martial·0.01, 0.25, 1); total = f · (toughness term ·
  0.8 + strength term + 10), min 1; split blunt = g·total, cut = (1-g)·total, g = lerp(martial·0.01, 1, 0.4);
  pierce 0 and the stun 0. Armour `unarmed` bonuses and RACE `fists
  lv1/lv100` feed this. The 0.8 is the CONSTANTS `unarmed damage mult` (**Verified** by its offset). Exact term
  shapes: **Observed**.
- Stumble threshold (`140884020`) = toughness·mult·0.01 · (0.65·stumble max) = toughness·m·0.52. A hit whose
  damage sum (cut + blunt + pierce + stun, as passed to the reaction) exceeds it, and a further condition on the
  target's current state holds, plays "Heavy_Hit" instead of "Light_Hit" (`1404391f0`). **Observed**.

## Technique choice, block and dodge

COMBAT_TECHNIQUE records (loader `1406684c0`; the field names the loader reads match the 44 data rows
through a probe: **Verified** for the names, **Observed** for field-to-offset): flags is-block, is-dodge,
stumble-dodge, gains-ground, prone, low strike; floats hesitate point, attack distance, min distance vs static,
acceptable end time; min/max skill, max encumbrance, `chance` (selection weight), max simultaneous hits;
validity flags per weapon type (1 handed, blunt, hackers, heavy weapons, katanas, polearm, sabre, unarmed);
also per-technique `attack direction 1/2`, `limb 1/2`, `power 1/2`, `anim blocked frame 1/2`, `anim stop frame
1/2`, `num techniques`, `anim name`, `anim speed mult`, `events`, `animal`, and in the data `use L arm` /
`use R arm` / `disabled`.

Selector `140887970` (**Observed**) is the *defender's* reaction chooser (the incoming attack's direction and
the attacker's skill are its inputs): the candidates are the techniques whose weapon-type validity flag
matches the defender's current weapon category and whose prone flag matches its posture. A block roll runs
first (block chance below against a uniform roll); when it succeeds, block techniques whose direction equals
the incoming one are the candidates; when it fails, block techniques of a *different direction* (of the same
direction class) are picked so the hit passes. Dodge techniques are chosen by a weighted random pick on
`chance`, only if the defender is in the dodge-capable state (for unarmed fighters this state is granted by an
extra roll against the variant chance `140885d80`). Whether the min/max skill and max encumbrance fields are
tested here was **not** seen in this function.

Block chance (`1408862d0`, **Observed**). With D the defender's defence and d = D - attacker skill:
D = melee defence integer + the weapon's `defence mod` (for armed fighters; the weapon term is dropped when
the weapon cannot block) + a float equipment bonus, +20 when the defender is in a guarding state (whether this is the state the HUD's Block stance sets is **Unknown**; its tooltip also says +20 melee defence, [ui-screens.md](ui-screens.md#4-hud-mainbargui)); unarmed
fighters use the martial-arts-based value of `140884ee0`.

- p = 70 + 1.2·d when d > 0, else 70 + 1.5·d; above 90, p = 90 + 0.2·(p - 90); clamped to [5, 95]. The
  constants 70 / 1.5 / 1.2 are the stored `base block chance`, `block chance reduction per 10 levels` and
  `increase per 10 levels` (struct offsets `+0x44`, `+0x48`, `+0x4c`, matched to the loader's stores).
- The variant `140885d80` (the dodge-style roll for unarmed fighters) uses the "effective defence" of
  `140885190` instead and clamps to [0, 95]. That effective defence is the stat minus (1 - e)·stat minus
  (1 - l)·stat plus a multiplier term, with e the encumbrance factor clamped to [0.4, 1], l a leg-health
  factor (the worse leg's health fraction ·1.8, clamped to [0, 1] by `140884970` (**Verified** by reading it; [character-stats.md](character-stats.md#derived-values-pointers-only) says the same) and then to [0.5, 1] inside `140885190`), and +20 added in the
  guarding state for unarmed fighters. **Observed**.

Hit outcome (`140665fa0`, **Observed**): blocked (code 5) if the defender's technique is a block, its
direction matches and its animation is past 50%; dodged (code 0) if the technique is a dodge and progress is
within [0.1, 0.98]; otherwise a hit (code 2). `1404394f0` then runs the medical pipeline and awards XP.

Skill-vs-skill roll (`140665370`): with F = 0.05 (`attack chance factor`, struct `+0x40`), A = 1 + F·max(0,
s2 - s1), B = 1 + F·max(0, s1 - s2); true when r1·A < r2·B for two uniform randoms. P = 0.5 at equal skill;
with k = B/A, P = 1 - 1/(2k) for k ≥ 1, else k/2 (the closed form is derived by hand from the comparison).
**Observed**. Dodge skill (`140885ce0`) = 0.01·(0.6·equipment dodge + 0.4·dexterity·m).

## Armour

Per piece (loader `140898e90`, **Observed**; class and material tables from the constructor `1408a8f90`,
decoded from constants plus disassembly; the price columns of the same tables are in [economy.md](economy.md#42-armour-and-clothing-type-3)). With t = quality fraction + level bonus·0.01:

- cut defence = clamp( lerp(t, matMin.cut, matMax.cut) · classCut + lerp(t, 0, `cut def bonus`), 0, 0.9 ).
- blunt defence = clamp( lerp(t, matMin.blunt, matMax.blunt) · classBlunt + lerp(t, 0, `blunt def bonus`), 0, 0.8 ).
- flat pierce defence = lerp(t, matMin.pierce, matMax.pierce) · `pierce def mult`.
- cut-into-stun fraction = clamp(field, 0, 1).
- athletics, combat speed and stealth factors = lerp over class and material tables times the item's mults
  (athletics 1.0 for body, shirt and hat slots; combat speed 1.0 for hats).

Class table (cut, blunt factors at lowest to highest grade): CLOTH 0.4 / 0.1; LIGHT 0.8 / 0.7; MEDIUM 0.9 / 0.9;
HEAVY 1 / 1. Material table, (cut, blunt, flat pierce) at lowest -> highest grade: CLOTH (0, 0, 0) -> (0.1,
0.3, 0); LEATHER (0, 0, 0) -> (0.55, 0.4, 35); CHAIN (0.05, 0, 0) -> (0.65, 0.3, 60); METAL_PLATE (0.1, 0, 0)
-> (0.85, 0.6, 90). The class table's last three columns (assumed athletics, combat speed, stealth) run from
lowest to highest grade: CLOTH and LIGHT 1 / 1 / 1; MEDIUM 0.85 -> 1, 1 -> 1, 0.9 -> 0.9; HEAVY 0.8 -> 0.95,
0.9 -> 1, 0 -> 0.4. The column roles are an inference (not cross-checked). **Observed**.
Per-slot adjustments in the loader (**Observed**, `140898e90`, applied when the item has `part coverage`
entries): the item's weight is `total coverage`·0.05 times a material weight factor, and by `slot` value 3 (hat)
x1.25 weight and x0.6 on a further coverage-derived factor, 6 (legs) x0.6·0.75 on that factor, 8 (shirt) x0.5
weight, x3 on that factor, x0.8 cut and blunt defence, x0.5 flat pierce defence, 9 (boots) x0.5 on that
factor (slot names inferred from which ARMOUR records use each number). The remaining
table fields that look like weight or price and the meaning of the further factor are not decoded.

Coverage: each ARMOUR record's `part coverage` maps a body part to a percentage; at hit time a random roll
above it means the piece does not cover (`14065d620`); a missing entry never covers. **Observed**.

Worn-gear totals (`140887030`) sum attack, defence, perception and unarmed bonuses and multiply dexterity,
dodge, damage-output, fist-injury, ranged and stealth multipliers. **Observed**.

## The hit pipeline (MedicalSystem)

`1406508d0`, **Observed**, everything below. The per-part state, degeneration, healing and the KO and coma rules that follow a hit are in [character-stats.md](character-stats.md#body-parts-and-injuries).

1. Choose a part among those not yet destroyed (flesh above minus its maximum), weighted by the part's `hit`
   weight (RACE `combat anatomy`) times its hit multiplier `hitmult` (the weight alone for hit direction 6).
   A low strike excludes arms and head. The chosen part's `hitmult` is then raised by 1 per hit (to at most 4;
   a part whose health fraction is negative is held to about 2, a destroyed part is set to 0), and it decays
   by 0.01·dt per update towards 1 while the part's flesh is non-negative (`14064f5c0`; reset to 1 once the
   flesh is negative). New parts start at 1. **Observed**.
2. Each covering armour piece in turn raises the cut reduction cutRed to cutRed + cutDef·(1 - cutRed), the blunt reduction bluntRed to bluntRed + bluntDef·(1 - bluntRed), adds pierceDef to the flat pierce defence pierceFlat, and adds cutDef·cutIntoStun·(1 - previous cutRed) to the stun fraction stunFrac. When the weapon's armour
   penetration pen is non-zero, both reductions are multiplied by (1 - pen) and then capped at 0.9 (the cap
   is applied only in that case); a negative pen (katanas, -0.3) therefore *raises* the reduction.
3. cut' = cut·(1 - cutRed); blunt' = blunt·(1 - bluntRed); pierce' = max(0, pierce - pierceFlat); stun is the
   cut times stunFrac plus the same fraction of the pierce that armour absorbed.
4. Toughness resistance r = lerp(clamp(toughness·m·0.01, 0, 1), -0.65, 0.65); cut, blunt, pierce and stun are
   each multiplied by (1 - r); cut and blunt are floored at 0; then all four are multiplied by the global
   damage multiplier. (r is -0.65 at toughness 0, so an untrained target takes 1.65 times the damage and a
   toughness-100 target 0.35 times.)
5. Apply to the part (`14064f300`, `140644a70`): the flesh falls by cut + pierce + (blunt permanent fraction, 0)·blunt, and the stun rises by (1 - permanent)·blunt plus the packet's stun; so with the base value of 0 blunt only stuns, and stun recovers.
   The blood falls by (cut + pierce)·0.2·bleed multiplier·race blood factor. Robots (and parts whose `+0x28` link is
   set, role unknown) additionally gain wear (+0.04·(cut+pierce+blunt)·robot wear rate), which lowers the part's maximum.
   Also here: blood at or below 0 (or below -25 once the second unconscious flag `+0x163` is set) knocks the
   character out.

HealthPartStatus (per part, [character-stats.md](character-stats.md#per-part-state-medicalsystemhealthpartstatus); save keys `hit`, `hitmult`, `flesh`, `stun`, `bandage`, `rig`, `wear` plus the
part name; **Verified**: the save writer `14064d390` and the loader `14064d600` use the same offsets, the key
strings were read from the executable, and the constructor `14064d0e0` initialises the same fields from
a LOCATIONAL_DAMAGE record): `hit` weight at `+0x38`, `hitmult` `+0x3c`, flesh at `+0x40`, stun `+0x44`,
bandage `+0x48`, rig `+0x4c`, wear `+0x50`, base HP `+0x54` (RACE `combat anatomy` second value, 100 if
under 1), HP multipliers `+0x58`/`+0x5c`, health fraction `+0x60`; collapses flag `+0x31`, death flag `+0x32`,
KO mult `+0x34`. Max flesh = base·multipliers - wear; health fraction = (flesh - stun + rig) / max
(`14064f300`).

LOCATIONAL_DAMAGE (9 base records, **Verified** from the data: two of them, Left and Right Foreleg, are the
animal arms): head, chest, stomach have `death` set; arms and legs do not. `severance` is set on head, stomach,
arms, legs and forelegs, not on chest; KO multiplier 2 (head), 1 (chest, stomach), 0.5 (limbs); the head's
skill effect is 0.6, the left arm 0.25, the right arm 1; the stomach slows movement by 0.3. Collapse parts:
head 1 (whole), left arm 4, right arm 2, left leg 32, right leg 16 (forelegs 4 and 2, `collapses` false).

## KO, bleeding and death

All **Observed** unless noted.

- A part whose (flesh - stun + rig) falls below zero collapses (`140649870`: the part's `collapse part` bits
  force the matching limbs down). If it carries the death flag the character is knocked out (`14064f300`,
  `1406447d0`), with an unconscious flag (`+0x161`) and a KO timer (`+0xa0`). Blood below 0 also knocks out
  (see the pipeline above).
- KO time (`1406447d0`) = knockout time base (20) + per death-flag part (max - flesh - stun)·0.25·T/max·100 +
  per part with negative (flesh - stun) |flesh - stun|·T·KOmult/max·100 + |max blood - blood|·T·50/max
  blood, with T = lerp(toughness·m·0.01, 3, 0.75) (CONSTANTS `knockout mult 1/99`). KO point =
  -lerp(toughness fraction, 10, 85) (`140643ae0`): the flesh level at which the part gives out falls with
  toughness; where this is consumed was not found. **Observed**.
- Wound deterioration scales by lerp(toughness, 1.7, 0.03) (`140643c70`) times the bodypart degeneration rate
  and the "chance of death" setting. Bleeding drains blood, clotting and bandages slow it, blood recovers at
  the recovery rate when not bleeding.
- Death (`140652000`): blood at or below -max blood (`1406440a0`: min blood + (max blood - min blood) scaled by
  a CharStats value and race factors, RACE `max blood`/`min blood`, default 150/75 per the earlier reading),
  the medical system's `+0x60` value (the hunger level, 3 at full, running down while the `+0x64` stomach buffer is empty: [character-stats.md](character-stats.md#hunger-and-starvation), **Observed**) at or below zero, or a death-flag part with (flesh - stun) below -max
  (`14064f5c0`, which returns "dead").
- Limb loss follows the Dismemberment setting for parts flagged `severance` (above); limb state enum:
  ORIGINAL, STUMP, REPLACED, CRUSHED.
- Fist injury (`140666780`, **Observed**): only for an unarmed (category 5) non-robot attacker. Striking SWORD
  material damages the attacker's hand part (cut 5, blunt 1), METAL (cut 2, blunt 2), CHAIN (cut 1, blunt 1);
  scaled by 1 - 0.5·(martial/100 + toughness·m/100), the global damage multiplier and the armour fist injury
  mult, then applied as a hit to the hand (`140644a70`) with toughness XP awarded. Robot targets count as METAL.

## Ranged and turrets

Mostly **Unknown**. Loaders: GUN_DATA (`14043baa0`: accuracy deviation at 0 skill, accuracy perfect skill, aim
speed, shots, range, reload min/max, shot speed, positioning) and CROSSBOW (`14043c180`). **Observed** for the
crossbow: pierce damage min and max, range, reload min/max, accuracy deviation and shot speed are each the
lerp between the record's level-0 and level-1 values, by the item's quality; the two pierce damages are
multiplied by the global damage multiplier and truncated to ints; barrel position comes from the model's
`barrel pos Y`/`Z` fields. Crossbows use weapon category 6 (bow skill). The turret path (`14043cdb0`,
`rangedCombat.cpp`, TURRETS skill 11) was not read. The constants `bow damage 1/99` are consumed there but
their exact use is **Unknown**.

## Experience

**Observed** (`1408c5df0`, `1408c5fa0`, `1408c6980` and neighbours; the full XP table and the stat numbers are in [character-stats.md](character-stats.md#levelling-xp)). Base unit 0.075: the stored `exp gain
multiplier` (3·0.25 = 0.75, held as a double) times 0.1. Every award is also multiplied by the difficulty
dialog's global damage multiplier (`142133580`).

- A skill with level L rises by amount·((101 - L)/101)^2 (diminishing returns); nothing happens unless the
  amount is in (0, 20].
- Difference factor = 1 + (opponent - own)/10 when the opponent is as good or better (so 2 at +10), or
  1 + (opponent - own)/25 when worse (0 at -25).
- Events (`1408c6980`, event code in its second argument): 0 hit dealt (attack or martial skill, weapon-category
  skill, dexterity, strength at about 0.55); 1 the same with the attack skill at 0.5 and the weapon skill at 0.4
  (apparently a blocked or glancing blow); 2 block (defence at 0.25, weapon skill 0.1); 4 hit taken (defence
  2, toughness at `xp rate toughness`, strength about 0.1). Each is scaled by the race XP multiplier map (the
  character race's per-stat entries) and the global damage multiplier. Mass combat, medic, dodge and
  athletics have their own functions (`1408c5e80`, `1408c6130`, `1408c6360`, `1408c6660`).

## Unknowns

- KO wake-up, flags `+0x162`/`+0x163`, and the roles of hunger and "fed" in recovery; (the `+0x60`/`+0x64` pair is the hunger level and stomach buffer, [character-stats.md](character-stats.md#hunger-and-starvation)); where the KO point (`140643ae0`) is consumed.
- Attacker-side technique choice (which technique an attacker starts, using min/max skill, encumbrance, distance); whether `140887970` tests min/max skill. Attack-slot AI (`14060a9e0` and neighbours), aggression,
  stance and target selection. The bluntBase of non-blunt-dominant weapons; the stat that scales max blood; the part `+0x28` link.
- The role of the class-table columns 4-6 (athletics, combat speed, stealth is an inference) and of the slot-adjusted factor (armour item field index 0x5d) in `140898e90`.
- Turrets; bow accuracy and range maths and the use of `bow damage 1/99`; the meaning of the weight/price-like armour table fields; the remaining difficulty floats beyond the dialog bindings listed above (their consumers).
- Whether the +20 guarding state of the block chance is the state the HUD's Block stance sets ([ui-screens.md](ui-screens.md#4-hud-mainbargui)).
- Whether the decompiled maths matches in-game numbers: nothing has been compared in play.

## Implementation outline

1. Read CONSTANTS, WEAPON, WEAPON_MANUFACTURER, MATERIAL_SPECS_WEAPON/CLOTHING, ARMOUR, LOCATIONAL_DAMAGE,
   COMBAT_TECHNIQUE, CROSSBOW and GUN_DATA through the existing record layer, with the stored (rescaled)
   constants exposed once.
2. Pure functions: weapon stats (q, record, manufacturer), damage packet, block chance, skill roll,
   armour stacking, toughness resistance, part damage. Unit-test each against the formulas above, using the
   game install behind `Assert.SkipWhen`.
3. A `BodyPartState` per part (`hit`, `hitmult`, flesh, stun, bandage, rig, wear) plus blood, KO timer and flags, saved ([../formats/save.md](../formats/save.md#platoon-files-squads-and-their-characters)) with the
   same key names as the original.
4. Defender reaction choice (block roll, wrong-direction block on a failed roll, weighted dodge) and outcome selection from animation state: block direction and progress above 0.5, dodge window 0.1..0.98; the per-part `hitmult` rise and decay.
5. XP distribution after each resolved blow.
6. Defer ranged, turrets and AI target choice until the Unknowns are read.
