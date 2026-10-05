# Characters: stats, needs, injuries, races

How the original game models a character's skills and attributes, hunger, blood and bleeding, body parts and
injuries, healing and first aid, knock-out and death, races and their modifiers, robotic limbs and encumbrance.
How a character is *assembled and drawn* (body files, clothing, face) is in [../characters.md](../characters.md);
how stats and starvation reshape the body is in [../animation.md](../animation.md) (body-shape formulas).
FCS record layout is in [../formats/fcs-mod.md](../formats/fcs-mod.md).
How these numbers are used in a fight (damage, armour, block, XP events, KO) is in [combat.md](combat.md); the clock and the scaled frame time that the rates run on are in
[game-loop.md](game-loop.md#the-clock); how stats feed movement is in [pathfinding.md](pathfinding.md#movement) and production in [buildings-production.md](buildings-production.md#production-and-crafting);
the saved form of stats, hunger and body parts is in [../formats/save.md](../formats/save.md#platoon-files-squads-and-their-characters); the AI tasks that react to hunger and injuries are in [ai.md](ai.md#default-tasks-per-character);
prices that use stat offsets are in [economy.md](economy.md#12-slaves-prisoners-animals).

## Sources

- Decompilation of `kenshi_x64.exe` (Ghidra 12.1.4 dump; addresses are in that binary, `FUN_...` names are
  Ghidra's). Class names are the game's own, from MSVC RTTI: `Character`, `CharacterHuman`, `CharStats`,
  `CharStatsAnimal`, `MedicalSystem` (inner struct `HealthPartStatus`), `RaceData`, `RobotLimbItem`,
  `RaceLimiter`, `CharacterStatsWindow`, `MedicalDatapanel`.
- `fcs.def` and `fcs_enums.def` in the install (field names and the editor's descriptions, paraphrased here; the
  editor's *defaults* differ from the base game's values) and the editor `forgotten construction set.exe`
  (decompiled with ILSpy outside the repo) for the `StatsEnumerated` and `LimbState` enum numbers.
- Base-game data read with the repo's `GameDatabase` through throw-away probes (the four core files
  `gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`; no `mods.cfg` mods). "Base game" below means
  that load order.
- The exe's `.data` was also read directly (a probe mapping virtual addresses to file bytes) for static float
  constants.
- No game session was run. Time: the game clock (total game hours, [game-loop.md](game-loop.md#the-clock)) is a `double` read at `[0x1421303d0]+0xa0`; the medical code
  computes `dt` as the difference of two readings (`FUN_140791ca0`). The editor text of `starvation time` says
  "in-game hours" and the starvation-KO code multiplies the same difference by 60, so `dt` is game hours
  (**Observed**). Many rates carry a `dt * 0.05` factor whose origin (unit conversion or tick scale) is
  **Unknown**; it is written as 0.05.

Object layout conventions (**Observed** unless said): `Character` embeds its `MedicalSystem` at +0x458, so
medical +0x60 is `Character+0x4b8` (**Verified**: `FUN_1406464c0` resets "character+0x4b8" to 3.0 and passes
`character+0x458` as the medical object, whose own reset uses +0x60; `../animation.md` reads the same float).
`MedicalSystem+0x1a8` points to the `CharStats`, and `CharStats+8` points back to the medical object; the owning
`Character` is `MedicalSystem+0xe0` and `CharStats+0x10`. Character virtual slot +0xd0 returns the `RaceData`;
slots +0x390 and +0x238 return per-character scale factors that are 1.0 for humans (`../animation.md`: slot 0x390 is
the constant 1 for `Character`/`CharacterHuman`); slot +0x250 returns the stats object for humanoids (null for
simple creatures) and slot +0x248 a non-null object for some characters that are exempt from limb loss and wear
(meaning **Unknown**).

## Stats

### The stat list

**Verified.** Three independent places agree on the numbering: the editor's `StatsEnumerated` enum (names), the
per-stat accessor `FUN_140884190` (stat number to `CharStats` byte offset, a switch), and the stats save writer
`FUN_14064a8f0` (offset to saved field name; read back by the loader `FUN_14064b6a0`, which stores at the same
offsets). Stats are `float`s inside `CharStats`. The number is what RACE `heal stat`, `stats good<N>`,
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

`STAT_NONE` is 0 and `STAT_END` 39. The enum continues with derived-value labels (`_PrimaryWeaponDamage`,
`_MaxCarryWeight`, `_ToughnessKnockoutPoint`, `_encumbrance`, ...) that have no storage (**Observed**: they are
in the editor enum, the accessor has no case for them).

The save writer/loader also handle fields the FCS `[STATS]` record does not list (legacy, mostly unused; the saved keys are listed in [../formats/save.md](../formats/save.md#platoon-files-squads-and-their-characters)):
`endurance` +0x84, `warrior spirit` +0x13c, `arrow defence` +0xa0, `bluff` +0xb4, `survival` +0xbc,
`tracking` +0xc0, `climbing` +0xc4, `doctor` +0xc8 (**Observed**), an `xp` float and a `free attribute points` value (both are saved STATS keys in [../formats/save.md](../formats/save.md#platoon-files-squads-and-their-characters); their meaning is **Unknown**).

The FCS `[STATS]` record has the 33 fields `strength`, `dexterity`, `toughness2`, `perception`; `attack`,
`defence`, `dodge`, `unarmed`; `athletics`, `swimming`; `engineer`, `medic`, `robotics`, `science`; `assassin`,
`lockpicking`, `stealth`, `thievery`; `armour smith`, `bow smith`, `cooking`, `farming`, `labouring`,
`weapon smith`; `blunt`, `bow`, `ff`, `hackers`, `heavy weapons`, `katana`, `poles`, `sabres`, `turrets`
(**Verified** against `fcs_fields.tsv`). The editor describes each as "[0-100]", but the base game's 51 STATS
records hold values up to 300 (`blunt`, `hackers`, `katana`, `sabres`, `medic`, `defence` max 300; `attack`,
`strength`, `dexterity`, `athletics`, `toughness2` max 200; **Verified**, probe over all STATS records). So
starting values can exceed 100; the *gain* curve below is what saturates near 101.

### Building a character's stats

**Observed**, `FUN_14064cb30` (CharStats constructor), `FUN_14064b6a0`, `FUN_140643cf0`.

1. If the CHARACTER has a `stats` reference, every field of that STATS record is copied into its offset
   (`FUN_14064b6a0`).
2. Otherwise the CHARACTER integers `combat stats`, `unarmed stats`, `stealth stats`, `ranged stats` and
   `strength` (the "if no stats data assigned" fields) each fan one number out to a group of stats
   (exact groups not decoded).
3. `stats randomise` n (CHARACTER; base game: 0 on 120 records, most others 3 to 10, up to 25) adds a random
   offset in [-n, +n] to about 30 stats (`FUN_140643cf0`; the helper is `FUN_1409b1c80(-n, n)`, assumed uniform).
4. Every stat number 1 to 38 is then **multiplied by the race multiplier** of that number (next section). So the
   race's `strength` / `dexterity` and its good/bad lists change the *starting* values, not only the XP rate.

### Race stat multipliers

**Verified.** The `RaceData` loader `FUN_14042efa0` builds a map `StatsEnumerated -> float` at `RaceData+0xa0`:
numbers 0 to 38 start at 1.0; each non-zero `stats good<N>` (N = 0, 1, 2, ... until a key is missing) sets that
stat's entry to **1.2**; each non-zero `stats bad<N>` sets it to **0.8**; then RACE `strength` overwrites entry 1
and RACE `dexterity` entry 18. The map is read by the character constructor and by every XP function
(`FUN_1408c6430`, `FUN_1408c6980`, `FUN_1408c6130`, ... all multiply by `map[stat]`; a second reader per function
is the cross-check). `0` in a good/bad list is STAT_NONE and skipped (the scan continues past a stored 0 and stops
only at the first missing key). The goods are applied first, then the bads, then `strength` / `dexterity`, so a
stat listed in both ends up 0.8 and RACE `strength` / `dexterity` always win for stats 1 and 18 (**Observed**,
statement order in `FUN_14042efa0`). Probe over RACE (base-game load order): Greenlander good =
Science, Farming, Cooking; Cannibal good Cooking, bad Farming; Scorchlander good Stealth, Athletics, Weapon smith,
Armour smith, Dodge, bad Labouring, Farming, Cooking (strength 0.9, dexterity 1.1); Shek good Melee attack,
Toughness, bad Science, Labouring, Robotics, Thieving, Farming, Stealth, Athletics, Dodge (strength 1.1,
dexterity 0.8); Fishman good Swimming, Athletics, bad Dodge; Hive Soldier Drone good Melee attack, Toughness, bad
Science, Robotics, Engineering, Weapon smith, Armour smith, Medic, Farming, Cooking, Perception; the
skeleton-type robots (Skeleton, the MkII skeletons, Screamer MkI, P4 Unit, Soldierbot) are all bad at Thieving,
Stealth and Dodge, but the robot spiders, Big-Bones and Crimper have no good/bad entries and the Hive Queen has
good Stealth, Science, Medic and bad Labouring. These match the races' descriptions (Greenlanders "scientists,
engineers...").

### Levelling (XP)

There is no XP counter and no level table: a stat *is* a float and activities push it up.

**Core gain** (`FUN_1408c5df0`, **Observed**, re-read; all the XP wrappers below pass the cap 101 (`0x42ca0000`),
only `FUN_1408c64f0` takes the cap as an argument):

- f = ((cap - stat) / cap)^2, with cap = 101
- gain = amount x f, applied only when 0 < amount <= 20 and 0 < f <= 20; the new value is kept only if it is not NaN
  (an old NaN becomes 20)
- stat = stat + gain

so a stat at 1 grows at full rate, at 50 at about a quarter, and approaches 101 asymptotically (at exactly 101 the
gain is 0). Because f is a *square*, a stat above 101 (possible from data, see the STATS maxima above) is **not**
frozen: f grows again with the distance from 101, so such a stat keeps rising until f exceeds 20, i.e. stat above
about 101 x (1 + sqrt 20) = 553. Stats never decrease through this function.

`amount` is composed of (**Observed**, one function each):

- the global `exp gain multiplier` (base 3.0), stored as `* 0.25` = **0.75** (`DAT_142133f08`, stored as a
  64-bit `double` at constants indices 0x4e..0x4f, **Verified** loader `FUN_14086b2b0` against the readers);
- the stat's **race multiplier** (above);
- the **skill-difference factor** `FUN_1408c5fa0(own, other)` = 1 + (other - own) * 0.01 * (100 / K), K =
  `skill diff xp 2x bonus` (10) when the other is better, `skill diff xp 0x penalty` (25) when the other is worse
  (the editor text names a 0.1x floor and a 6x cap; neither is in this function, callers may clamp);
- a per-activity constant, and either `0.02 * dt` for continuous activities (**Verified**: the float at
  `142021e50` is 0.02, read from the exe's `.data`) or, for per-event gains, `0.1 * 0.75 = 0.075`
  (`DAT_142134edc`, recomputed at the top of `FUN_1408c6980`);
- for combat gains also the new-game option **Global damage multiplier** (`DAT_142133580`), so faster fights do
  not give more XP per minute.

`FUN_1408c6430(stats, dt, amount, stat)` is the generic continuous entry: `stat` rises by `core(0.02 * dt * 0.75 *
amount * map[stat])`; `FUN_1408c64f0` is the same with an explicit stat slot and cap (training dummies: the
editor says their `output rate` is the highest skill they can train).

| Activity | Function | Effect (**Observed**) |
| --- | --- | --- |
| Hit landed (events 0 and 1) | `FUN_1408c6980` | `attack` (or `unarmed` when the stat-set kind at +0x1f0 is 5) gets 0.075 x skill-diff(vs opponent defence) x `map[2]`; the current weapon skill, `dexterity` (x `map[18]`) similarly; event 1 uses halves (0.5 and 0.4) |
| Attack defended (event 2) | same | `defence` at 0.25 of base, weapon skill at 0.1 |
| Being hit (event 4) | same | `defence` 2.0 of base (**Verified**: the stat written is the CharStats slot +0x124, the same slot event 2 uses for `defence`, and [combat.md](combat.md#experience) lists the same; `FUN_1408c6360` handles stat-set kind 5), and `toughness` = 0.075 x `xp rate toughness` (1.33) x skill-diff(own toughness vs damage) x `map[21]` |
| Event 6 | same | the same attacker-side gains as event 2 (attack/unarmed 0.25, weapon skill 0.1) but no `defence` gain (meaning of event 6 not decoded) |
| Strength tail of every event | same | `strength` rises by `core(0.075 * 0.55 * 2*xp rate strength * map[1])` scaled x0.4 for event 2, x0.1 for event 4, and by the weapon-weight factor below; for unarmed (kind 5) the weapon-weight factor is replaced by (1 - encumbrance factor) |
| Toughness per damage received | `FUN_1408c68e0`, called from `FUN_140666780` | 0.075 x k x `xp rate toughness` x `map[21]`; k = 0.05 normally, 0.25 for damage kind 1, 0.5 for kind 5 (kind meanings not decoded) |
| Toughness when knocked out | `FUN_1408c6720`, `FUN_1408c6780` | flat gains (6780 scales with damage taken) |
| Toughness from losing a limb | `FUN_14064edc0` via `FUN_1408c7310` | generic entry with dt 1, amount 300 into stat 21 (a large one-off gain) |
| Weapon-weight strength factor | `FUN_1408838e0` / `FUN_1408839a0` | `min strength xp mult` (0.1) + clamp((weapon weight - strength x injury multiplier) / `weight strength diff 1x` (20), 0, `weight strength diff max` (1)); stored at +0x184 |
| Strength from carrying | `FUN_1408c60c0` (from the movement tick `FUN_1408c6660`) | (1 - encumbrance factor)^2 capped at 0.5, x2 unless in state 0xb, x `xp rate strength from walking` (0.5) |
| Athletics (running) | `FUN_1408c6660` | `FUN_1408c5ec0` (the encumbrance factor, x1.5 when the speed is over 80% of the character's maximum) x `xp rate athletics` (stored 0.5) via the generic entry; swimming (stat 23) gets 5, or 10 when carrying, while in water |
| Stealth (sneaking) | `FUN_1408c65a0`, `FUN_1408c7330` | stat 16 gains scaled by a decaying exposure factor (+0x174, x0.7 per call); zero when KO'd or in a non-default state |
| Lockpicking | `FUN_1408c62e0` | stat 37 gains 1.2 x `map[37]` on success, 0.24 x on failure |
| Engineering | `FUN_1408c6250` | stat 5 gains `0.02 * dt * 0.75 * map[5] * 0.75` |
| Medic | `FUN_1408c6130` | `dt / T * 0.75 * map[stat]`, T seconds per point from `XP rate medic 1` (15, at skill 1) to `XP rate medic 99` (150, at 99); editor text: "seconds of usage per point". The interpolation argument is hidden in the decompile (**Unknown** exact form) |
| Mass combat | `FUN_1408c5e80` | +0x9c rises by 0.75 x 0.05 per call |
| Other crafting and machine skills | callers of `FUN_1408c7250` / `FUN_1408c7310` | `FUN_1408c7250(stat)` picks a per-stat base `amount` and calls the generic entry with a global float as `dt` (`DAT_142133794`, the game-speed-scaled frame time, the third time value of [game-loop.md](game-loop.md#the-three-time-values)): 0.5 by default, thievery and lockpicking 0.1, turrets 1.0, perception 3.0, farming 0.75, crossbows 1.2 (**Observed**; the farming and crossbows values come from a switch fall-through, 0.5 x 1.5 and 0.8 x 1.5). `FUN_1408c7310(stat, amount)` is the same with dt 1 |

### Effective stats: injuries and hunger

Throughout, `lerp(a, b, t)` = a + (b - a) * t. (The game's function `FUN_140015b63` takes `t` first; [combat.md](combat.md) and [economy.md](economy.md) write `lerp(t, a, b)` in that order. The notation differs only between docs.)

**Observed**, `FUN_1406457e0(medical, stat, useHunger, useLimbs, usePain, useDamageState, useStun, useLimbItems)`
returns a multiplier in [0, 1] by which a stat is scaled whenever the game *uses* the skill (damage, accuracy,
speeds, crafting). For every stat it has the same shape:

- H = lerp(h0, 1, clamp(hunger - 1, 0, 1)), hunger level 0..3 (see Needs); only used when `useHunger`
- L = lerp(l0, 1, limb), `limb` a body-part health fraction (see below); only used when `useLimbs`
- m = H x L
- when p0 < 1 and `usePain`: m = m x lerp(p0, 1, clamp(2.5 x P, 0, 1)), where P is a pain value held on the owning
  `Character` (+0xd8; its source was not traced, this doc earlier called it "stun"), and the character is flagged "in
  pain" (+0xdc)
- for the perception-like rows (24, 36, marked in the table) and `useStun`: m = m x `FUN_1406439f0`, a factor
  lerp(1, 0.5, v) that is 1 unless a value v > 0 at CharStats-owner +0x14c is set while the state flag at +0x148 is 1
- result = lerp(f0, 1, m), clamped to [0, 1]; with `useDamageState` times the limb-item factor (below); finally
  times the stat bonus factor from the extra column (1 when not requested)

The hunger term is 1 at hunger level 2 or above and falls to `h0` at level 1 or below (**Verified** thresholds
against the hunger-level reader `FUN_140644d30` and the UI comparisons in `FUN_14088a8f0`). `limb` is built from three aggregates that
`FUN_140644e60` recomputes from all body parts: **A** (`+0xb8`) = health fraction of the best ARM part (1.0 for
characters with a +0x248 object), **Hd** (`+0xbc`) = health fraction of the HEAD part, **T** (`+0xc0`) = the
lowest health fraction among TORSO parts (health fraction = the part's `+0x60`, about 1.0 when healthy), and
the two flags at medical +0x165/+0x166, which turn false when the right/left arm is disabled (collapsed).

Decoded cases (hex constants converted; **Observed**, individual cells are only as reliable as the hand decoding
of a Ghidra switch, which has fall-through cases. Every row was re-decoded from the constants; the science/robotics/cooking and
friendly-fire rows were corrected because their cases fall through into the next case, all other rows agree):

| Stats | `limb` | h0 | l0 | f0 | p0 | extra |
| --- | --- | --- | --- | --- | --- | --- |
| strength (1) | A x T | 0 | 0.75 | 0.1 | none | |
| melee attack (2), unarmed (30) | A x T | 0.75 | 0 | 0.7 | none | |
| labouring, engineer, weapon smith, armour smith, farming, bow smith (3,5,7,8,12,38) | A x T, x0.01 if both arms disabled | 0.5 | 0 | 0.25 | 0.25 | |
| science, robotics, cooking (4,6,13) | Hd (falls through into the medic row) | 0.6 | 0.4 | 0.25 | 0.25 | |
| medic, turrets, hive medic, vet (9,11,14,15) | Hd | 0.6 | 0.4 | 0.25 | none | |
| thievery, lockpicking (10,37) | A, x0.01 if both arms disabled | 0.75 | 0.4 | 0.25 | none | |
| stealth (16) | A | 0.9 | 0.9 | 0.8 | none | |
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

With `useDamageState` the result is multiplied by `FUN_140644b90`: the product of the fitted limb items'
multipliers for that stat (see Limb replacements), times **0.66 if either arm is disabled**.

### Derived values (pointers only)

- Cut, blunt, pierce and stumble damage (`FUN_140883ba0`, `FUN_140883c60`, `FUN_140883d10`, `FUN_140884020`) depend
  on strength, dexterity, the weapon, the injury multipliers of stats 1 and 18 and CONSTANTS `cut|blunt damage
  1|99`, `pierce damage multiplier`, `stumble damage max`, all multiplied by `damage multiplier` (0.65) at load.
  Not decoded further here; the damage formulas are in [combat.md](combat.md#damage-per-blow) and the hit pipeline in [combat.md](combat.md#the-hit-pipeline-medicalsystem).
- **Run speed value** (`FUN_140883db0`): V = lerp(`speed min skill`, `speed max skill`, athletics * 0.01) of the
  race (human 70 to 120) times (0.5 + 0.5 x s), s = the value of `Character` virtual slot +0x398
  (`CharacterHuman::vfunc_115`, a constant 1.0 for humans, so the factor is 1 there; it is **not** a limb health
  factor, as an earlier draft claimed). `FUN_140886460` turns it into the speed S =
  (V - 11) x leg factor x hunger factor x replaced-leg multipliers x encumbrance factor x (CharStats+0x18) + 11,
  never below 11; water state 1 (shallow water, `FUN_1405c7fd0`) caps S at 45 (**Verified** by reading `FUN_140886460`: the cap follows the water state, not a walking mode; the full water-state and speed-mode rules are in [pathfinding.md](pathfinding.md#speed-the-stat-s-see-character-statsmd)). RaceData +0x64 (`walk speed`) is **not** the walking
  speed: it is used in the other (water) modes, doubled for races that do not swim; two special states force 7
  and 5 (**Observed**; "game speed" units, scale unknown).
- **Leg factor** (`FUN_140884970`): the lower of the two legs' health fractions x 1.8, clamped to 0..1.
- **Hunger factor** (`FUN_140644d30`): lerp(0.5, 1, clamp(hunger - 1, 0, 1)): malnourished characters move at half
  speed at worst.
- Dodge skill (`FUN_140885190`): reduced by (1 - clamp(encumbrance, 0.4, 1)) of itself and by a leg term.

## Needs

### Hunger and starvation

The medical object holds a **hunger level** (`+0x60`, 0 to 3, the UI shows level x 100 as "Hunger") and a
**stomach buffer** (`+0x64`, food waiting to be digested). A new character starts at 3.0 (constructor
`FUN_1406409f0` and `FUN_1406464c0`). **Verified** thresholds (the comparisons against 2.0 and 1.0 in the status
panel `FUN_14088a8f0`, the hunger term of the stat multiplier `FUN_1406457e0` and the speed factor
`FUN_140644d30`, and `../animation.md`'s starvation shape reading the same float):

| Hunger level | State |
| --- | --- |
| 2 or above | well fed, no problem |
| below 2, at or above 1 | stat penalty and up to half movement speed; the panel says "Hungry" when the stomach buffer is empty and "Malnourished" ("fed and slowly recovering") when there is food in it (**Observed**; the panel also needs a value at medical +0xa4 of at least 0.7, meaning not traced) |
| below 1 | "Starving": the character passes out below a toughness-dependent point; dies at 0 |

Each medical tick (`FUN_14064da70`, `MedicalSystem` vtable slot 2, **Observed**):

- `T = starvation time * HungerTime` hours. `starvation time` is **60** in the base game (editor default 24; editor
  text: hours to lose 100 points, "x3 is the time from full to death") and `HungerTime` is the new-game option
  **Hunger time** (0.25 to 8, tooltip "how long it takes characters to get hungry; higher means longer",
  `DAT_142133594`). **Verified** binding: `FUN_1403e7620(&DAT_142133578)` = `starvation time` x the float at +0x1c of
  that block, which is `DAT_142133594`. At defaults one level lasts 60 hours at rate 1: 3.0 to 2.0 in 60 h, to 1.0
  in 120 h, to 0 in 180 h.
- The tick does nothing to hunger when T <= 0, or while a global flag at `[DAT_142134690] + 0x2f0` is set (`DAT_142134690` is the PlayerInterface and the byte at +0x2f0 is the one that also gates most input events, [ui-input.md](ui-input.md#6-event-dispatch); its meaning is **Unknown** in both docs, probably a pause or sandbox switch). The tick also refreshes the aggregates (`FUN_140644e60`); blood
  and body-part updates run from `FUN_140652000`, not here.
- With an empty buffer: `hunger` falls by `dt / T * rate`, `rate` from `FUN_1408837a0` = race `hunger rate` x a situation
  factor: resting, knocked out or dead: `bed hunger rate` (0.33); active: 1 + (1 - encumbrance factor) x
  `encumbrance hunger rate` (0.7); one situation (using a machine, probably BUILDING `hunger rate`) gives 0.8 and a
  last branch reads CharStats+0x178 (**Unknown**).
- With food in the buffer: no drain; instead `moved = min(buffer, dt / T * k * fed recovery rate mult *
  race hunger rate)` with k the character's slot-0x390 factor (1.0 for humans); the buffer falls and the hunger level rises by `moved`.
  Eating refills at about three times the starvation speed. Hunger is capped at 3.0.
- When `hunger < character slot 0x348` and the buffer is empty the buffer is set to `min(0.04, difference)`
  (purpose **Unknown**; slot 0x348 may be 0 for players).
- Races with `hunger rate` 0 never starve: the skeleton-type robots (Skeleton, MkII, Screamer, P4, Soldierbot),
  Big-Bones and the robot spiders (**Verified**, probe over RACE). Not every `is robot` race: the Hive Queen
  (hunger rate 3) and Crimper (5) are robots that do get hungry.

Passing out (`FUN_140657620`, **Observed**): the threshold is `(100 - K * f) / 100`, K = lerp(`min toughness ko point`,
`max toughness ko point`, toughness / 100) (base game 10 to 85), f = 1 with an empty buffer, 2 with food (the f
choice is in `FUN_140644320`). So toughness 0 passes out at hunger 0.9, toughness 100 at 0.15. The UI line "When
the red bar goes below {1} you will pass out" prints `FUN_140657620`. `FUN_140644320` itself only records a
timestamp and a random interval when hunger is below that threshold, and is also called from the vital-part-down
branch of `FUN_14064f300` (where its result gates the KO); the starvation-to-KO state transition was not traced and
the return meaning of `FUN_140644320` is **Unknown**. Death at hunger <= 0 is in `FUN_140652000`. Food item
nutrition values and the effect of `food quality mult` (0.5 in the base game, `FUN_14075faf0`) are **Unknown**.

### Sleep, beds and resting

- A character in the resting state (`Character+0x2f8 == 1`) uses `bed hunger rate` and heals blood at
  `resting heal rate mult` (2) (**Observed**, `FUN_1408837a0`, `FUN_140652000`).
- While resting on a bed-like object (the object at `Character+0x300`; kinds 6 and 0x19) the **degeneration
  multiplier is forced to 0** and the part-heal rate is multiplied by the object's own value (virtual +0x3c0):
  for kind 6 it multiplies the organic heal rate, for kind 0x19 the robotic/repair rate and the wear repair
  (**Observed**; which BUILDING field supplies the value is **Unknown**). A character in state 2 heals twice as
  fast.
- Unconsciousness and starvation KO share the KO timer below; waking requires the timer to run out and all
  critical parts to be above 0 ("Recovery coma").

## Blood and bleeding

**Observed**: `FUN_140646140` (blood tick), `FUN_14064f300` (damage into a part), `FUN_140644b00`,
`FUN_1406440a0`, `FUN_140652000`.

- **Capacity** (`FUN_1406440a0`): `blood_max = min blood + (max blood - min blood) * s * k * z` with RACE
  `min blood` / `max blood` (75 / 150 for humans), `s` = strength / 100 for characters with a stats object
  (1 otherwise) and `k`, `z` the two character scale slots (1.0 for humans). The editor text: "amount of blood at
  highest strength/age".
- Current blood is `medical +0x70`.
- **Wounds**: each cut adds a wound (a list at +0x178) whose strength decays by `bleeding clot rate x 0.1` per
  `dt` (0.00085 at base values) and is removed at 0. A damaged **stump** limb (limb state 1) with
  `h + b < 0` contributes `-(h + b) * 0.002` (`FUN_140644b00`); intact limbs do not.
- **Bleed rate** (`+0x78`) = `(extra blood loss from bodyparts * sum(stump bleed) + sum(wound strengths)) *
  ChanceOfDeath * 1.2 * race bleed rate`, then `blood` falls by `bleed_rate * dt`. `ChanceOfDeath` is the new-game option
  "Chance of death" (0.5 to 4, `DAT_142133578`; tooltip: "directly affects rates of wound degeneration and blood
  loss"). Severing creates a wound of strength `bleed rate * 200` (0.2 at base values, `FUN_14064edc0`). How an
  ordinary cut turns into a wound strength is not traced; the editor text of CONSTANTS `bleed rate` is
  "cutDamage * this * TIME".
- **Immediate loss per hit** (`FUN_14064f300`): `blood` falls by `(cut + pierce) * immediate blood loss (0.2) * hit scale *
  race bleed rate`.
- **Recovery** (`FUN_140652000`): while the bleed rate is 0 and blood is below capacity, `blood` rises by `blood recovery
  rate (0.04 after x0.1) * dt * race heal rate`, x2 when resting. Above 25 the "unconscious from blood loss" flag
  (+0x163) clears.
- **Blood-loss KO**: blood <= 0, or <= -25 when already flagged, starts a KO (`FUN_1406447d0`); **death** when blood
  <= -capacity.
- Races with `bleed rate` 0 are bloodless (editor text). Robots in the base game: 0.1 to 0.5, `max blood` =
  `min blood`.

## Body parts and injuries

### What a body part is

A character's parts come from its RACE `combat anatomy` reference list (**Verified** reader `FUN_14064dd10`; every one of the 53 base-load races lists 7 LOCATIONAL_DAMAGE references,
except the No-Head MkII skeleton with 6; there are 9 LOCATIONAL_DAMAGE records in all, the two extra ones being
the forelegs). Each reference carries two integers. **Observed**: value 1 is the part's **base
HP** (100 for humans, 250 Garru, 75 hive workers, 2500 to 5000 Leviathan) and value 0 its **hit weight** (a
relative chance to be struck; the hit resolver `FUN_1406508d0` builds a cumulative-weight map from it and draws a
random number). The assignment of the two integers to the two constructor arguments is inferred, not visible in
the decompile. Base-game anatomies (value 0 / value 1, probe over RACE `combat anatomy`):

| Race | Head | Chest | Stomach | Arms (L, R) | Legs (L, R) |
| --- | --- | --- | --- | --- | --- |
| Greenlander, Cannibal | 80/100 | 140/100 | 140/100 | 80, 40 (/100) | 80, 80 (/100) |
| Scorchlander | 80/100 | 140/100 | 140/100 | 80, 60 (/100) | 80, 80 (/100) |
| Shek | 80/125 | 140/125 | 140/125 | 80, 60 (/125) | 80, 80 (/125) |
| Hive Worker Drone | 80/125 | 140/75 | 60/75 | 80, 40 (/75) | 80, 80 (/75) |
| Hive Soldier Drone | 80/200 | 140/100 | 60/100 | 80, 40 (/100) | 80, 80 (/100) |
| Fishman | 100/150 | 100/150 | 100/150 | 80, 80 (/150) | 10, 10 (/200) |
| Skeleton | 80/200 | 140/200 | 80/200 | 80, 60 (/200) | 80, 80 (/200) |
| Garru (animal) | 100/250 | 100/250 | 100/250 | forelegs 100, 100 (/100) | 20, 20 (/250) |
| Leviathan | 100/2500 | 100/5000 | 100/5000 | 100, 100 (/2500) | 50, 50 (/2500) |

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

Field meanings: `body part type` is `BodyPartType` 0 TORSO, 1 LEG, 2 ARM, 3 HEAD (aggregates above);
`collapses` and `death` (loaded to part +0x31 and +0x32; "vital" below) and `KO mult` (+0x34) are read by
`FUN_14064d0e0`; `collapse part` is a bit mask of the ragdoll part that goes limp: 1 whole body, 2 right arm,
4 left arm, 0x10 right leg, 0x20 left leg (the loader derives the side: 4 and 0x20 left, 2 and 0x10 right);
`bone name`, `bone name 2/3` are the bones used for severed limbs; `pain anim` (reference + threshold percent)
is the overlay played when the part is hurt (`FUN_14064a100`, `FUN_140646bb0`). **Unknown**: the consumers of
`affects move speed` and `affects skills` (no reader of those strings was found; the data are asymmetric
on purpose, right arm 1.0 against left arm 0.25 and a right-arm hit weight of 40 against 80, so the editor's
"main arm" is the right one).

The limb-slot index used by limb replacement and RACE `severed limbs` is 0 left arm, 1 right arm, 2 left leg,
3 right leg (**Verified**: the severed-limb references carry values 0..3 in that order for Human/Hive/Shek/
Cannibal, and `FUN_14064f5c0` computes the slot from type and side the same way).

### Per-part state (`MedicalSystem::HealthPartStatus`)

**Observed**, from `FUN_14064d0e0` (constructor), `FUN_14064f5c0` (update), `FUN_14064f300` / `FUN_140644a70`
(damage), `FUN_140644e60` (aggregates).

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
| +0x4c | extra buffer used only by the health fraction |
| +0x50 | **wear** w (permanent loss of maximum, robots) |
| +0x54 / +0x58 / +0x5c | base HP (ref value 1; forced to 5 for a stump) / multiplier (set by the constructor and rewritten each tick from the character slot-0x390 factor) / body scale (character slot 0x238) |
| +0x60 | health fraction `((h - s) + extra) / M` |

`M = base * mult * scale - w` is the effective maximum HP. For every part the **limb state** (a small per-character
object read through `FUN_1400cd450(obj, slot)`) is `LimbState` ORIGINAL 0, STUMP 1, REPLACED 2, CRUSHED 3
(**Verified**: the editor enum, the `== 1` / `== 2` tests in `FUN_14064edc0`, `FUN_14064f5c0`, `FUN_140644b00`, and
`../animation.md`'s thigh rule "state 1").

### Damage into a part

`FUN_14064f300` / `FUN_140644a70` (hit with a 5-vector: cut, blunt, pierce, extra stun, scale; **Observed**):

- `h` falls by `cut + pierce + blunt * bluntPermanentOrganDamage` (CONSTANTS, base 0: blunt does **not** reduce HP),
- `s` rises by `(1 - bluntPermanentOrganDamage) * blunt + extraStun`: blunt damage is stun,
- the health fraction is recomputed; immediate blood loss as above,
- parts of robots or with a fitted limb also gain **wear**: `w` rises by `(cut + pierce + blunt) * 0.04 * robot wear
  rate`, unless the character has the slot-0x248 object.

A part is **down** when `h - s + extra < 0`: its collapse mask is applied (`FUN_140649870`: the right/left arm
flags at medical +0x165/+0x166 turn false, the matching limb slots are marked), and if the part is vital the
character is knocked out. A part is **destroyed** when `h - s + extra < -M` in the hit path (`FUN_14064f300`; the
per-tick path `FUN_14064f5c0` tests `h - s < -M` without `extra`): a vital part kills the character; any other
part is **severed** (limb state to STUMP, `rand(5, 30)` blood lost, a severed limb item may spawn from RACE `severed
limbs`) subject to the *Dismemberment* setting (**Verified** against the code: `DAT_142133504` is tested `> 0`
in the hit path and `> 1` in the per-tick path, and only for parts whose limb state is ORIGINAL; the option's
tooltip says "-100", which equals -M only for 100-HP parts): Never = 0, **Rare** = 1 (only when a hit takes it
below -M, and the hit path also needs the owner to have a stats object) and **Frequent** = 2 (whenever HP drops
below -M, including by degeneration); the install's `settings.cfg` has `Dismemberment=1` (checked). Limbs can also be eaten off by animals regardless. The *Blood* video
setting only gates blood effects.

### Degeneration, healing, stun

**Observed**, `FUN_14064f5c0`, called for every part by `FUN_140652000` each tick with `dt`, two heal rates, a
degeneration multiplier and the wear-repair rate. Let `D` = the untreated damage fraction:
`D = clamp(((M - h - b) * 100 / M - 20) / (90 - 20), 0, 1)`, and D is multiplied by 0.25 if `h + b > 0` (so untreated damage under
20% of M does nothing and 90% or more is full speed).

- **Degeneration** (only while `h > -3M`): `h` falls by `dt * 0.05 * bodypart degeneration rate (1.5) * ChanceOfDeath *
  degMult * D * 1.2`, `degMult = lerp(degeneration mult 1 (1.7), degeneration mult 99 (0.03), toughness / 100)`: a
  toughness-1 character deteriorates about 57 times faster than a toughness-99 one. `degMult` is 0 for non-vital
  robotic/replaced parts ("skeleton limbs don't degenerate") and while resting on a bed-like object.
- **Healing of treated damage**: while `b > 0` and `h < M`: `heal = min(b, dt * 0.05 * heal rate mult (0.25) * R)`,
  `b` falls by `heal`, `h` rises by `heal`, `h <= M`. `R = (M / 100) * A` with A the race `heal rate` x bed factor for organic
  parts, or `(M / 100) * B` for robotic/replaced parts; x250 for those too; a stump has base HP 5 and heals at
  twice A. Nothing heals a part that has not been bandaged first (first aid fills `b`).
- **Self healing** (RACE `self healing`; animals and a few robots): when `h - s < M - b` and the owner is
  conscious and alive, `b` rises by `dt * 0.05 * heal rate mult * R * 0.4`: untreated damage turns into treated damage at
  0.4 of the normal heal speed.
- **Stun**: `s` falls by `dt * 0.05 * stun recovery rate (1) * heal rate mult * R`, floor 0; `extra` is held under 50 - (h - s).
- **Wear repair** (robots, at a repair object): `w` falls by `dt * 0.05 * resting heal rate mult * repair`, moving that
  amount into `b`.
- **Pain factor** (+0x3c): above 1 it decays by `dt * 0.01`.

### Knock-out, coma, death

- **KO point** (`FUN_140643ae0`, **Verified** with the editor description "negative HP at which a character goes into
  a coma"): `-lerp(min toughness ko point, max toughness ko point, toughness * 0.01)`, 10 at toughness 0 and 85 at
  toughness 100 in the base game (editor default max 99). (`toughness` here is the stat times CharStats+0x34, a
  scale that is 1.0 for humans, **Unknown** otherwise.)
- **KO time** (`FUN_1406447d0`, **Observed**): `base + sum of terms` with base `knockout time base` (20) and
  `k = lerp(knockout mult 1 (3.0), knockout mult 99 (0.75), toughness / 100)`: for each vital part
  `(M - (h + s)) * k * 0.25 / M * 100`; for each part with `h - s < 0` `|h - s| * k * KO mult / M * 100`; plus, once, `|max blood - blood| * k * 0.5 / max blood * 100` (**Verified** by reading `FUN_1406447d0`: the term is `|capacity - blood| x k x 0.25 / capacity x 100 x 2`; [combat.md](combat.md#ko-bleeding-and-death) writes it as T x 50 / max blood).
  Losing a **leg** adds a second KO (`FUN_140644980`, called from the sever path `FUN_14064edc0` for limb slots 2 and  3 only, so arm loss does not): `(2 * base + the same terms) * (1 - toughness / 100) * 4`, floor 3, then multiplied  by a random number in [0, 1]. The result is stored at +0xa0 and counts down in `FUN_140652000`; its unit is  **Unknown**.
- **Death** (`FUN_14064f5c0` returning 0, `FUN_140652000`; **Observed**): the dead flag (+0x164) is set when a
  vital part is destroyed (`h - s < -M` on a part with `death`), when blood reaches -capacity or when hunger
  reaches 0. Untreated damage is therefore a slow death (degeneration), blood loss a faster one.
- `FUN_14088a8f0` (the status panel) lists the states shown to the player: "Unconscious" ("Knocked out cold",
  "Recovery coma": stays down until all critical parts are above 0), "Bleeding", "Blood loss KO", "Crippled"
  (legs), "Crippled arm", "Critical" (injuries degenerate), "Limb trauma", "Staying low" / "Playing dead"
  (post-KO behaviour), "Starving", "Malnourished", "Hungry", "Discovered!", "In Disguise", and a "Recovery rate: Nx"
  line.

## First aid, medkits and robot repair

**Observed**, `FUN_140649a00` (apply), `FUN_140644250` (kit drain), `FUN_140649d50`, `FUN_14064d800`.

- A treatment adds to each part's bandaged pool: `b` rises by `rate * dt` capped so `h + b <= M`, with `rate =
  lerp(1, 15, min(skill, kit quality) * 0.01) * medic speed mult (3)`, further x `robot medic speed mult` (0.33)
  for robotic kits (item function 12). The kit quality is the ITEM `quality` (1 to 100) at item offset +0x11c.
  Without a kit only a healer that has no stats object works (animals); robot kits treat only robotic parts and
  ordinary kits only organic ones.
- Kits are consumed: `remaining` falls by `lerp(medkit drain 99 (0.1), medkit drain 1 (2.0), (100 - skill) / 100) * dt`
  (the loader scales both by 0.1; x0.33 for robot kits). An expert uses a kit 20 times slower than a beginner.
- Who treats whom: the patient race's `heal stat` is the skill used: Medic (9) for most races, Hive medic (14) for
  hive races, Vet (15) for crabs, Robotics (6) for robots (the Hive Queen, an `is robot` race, uses 14).
- Medic XP is awarded while treating (XP table). The first-aid animation field is BUILDING_FUNCTIONALITY
  `animation medic`.
- The ratio of the bandaging speed (1 to 45 per `dt` at skill 50) to the conversion into HP (about
  0.0125 x R per `dt`) shows that bandaging is much faster than healing, but the two `dt` units may differ
  (**Unknown**).

## Limb replacements and robots

**Verified** (field to stat mapping: the `RobotLimbItem` constructor `FUN_1400ce2d0`, the editor field list, and the
28 base LIMB_REPLACEMENT records by probe; an earlier draft said 29, the base load has 28: 24 from
`Newwworld.mod` and 4 from `rebirth.mod`).

A LIMB_REPLACEMENT record (`slot` 50 left arm, 51 right arm, 52 left leg, 53 right leg; **Verified**) becomes a
`RobotLimbItem` with a `StatsEnumerated -> float` multiplier map. Each multiplier is stored as two fields, `X mult`
at the lowest quality and `X mult 1` at the highest, interpolated by the item's quality grade
(`FUN_1400cd500`).

| Slot | Fields | Stat numbers |
| --- | --- | --- |
| all | `swimming mult`, `HP` (/`HP 1`), `unarmed damage bonus` (/`... 1`), `overall mult` | 23, part HP |
| arms | `strength mult`, `dexterity mult`, `thievery mult` (applied to both 10 and 37), `ranged mult` | 1, 18, 10 and 37, 35 |
| legs | `athletics mult`, `stealth mult` | 17, 16 |

`FUN_1400cd780(limb, stat)` reads the map (1.0 when absent); `FUN_140644b90` multiplies both arms' (and for stats
16, 17 and 23 the legs') entries into the stat multiplier, then x0.66 if either arm is disabled; the x0.66 applies
only to the arm-related stats (1, 2, 6, 9, 10, 11, 18, 19, 22, 23, 30, 35, 37), not to 16 and 17 and not to stats
that have no case (those return 1.0) (**Observed**, re-read). `HP` replaces the part's base HP for that slot;
`craft time hrs` is the build time. Base-game records (low-quality value to high-quality value, probe over all 28):
human and hive prosthetic stumps (HP 100, swimming x0.75, rest 1); Stealth Leg (HP 50 to 175, athletics 0.7 to
1.05, stealth 0.75 to 1.25); Scout Leg (HP 50 to 150, athletics 1.0 to 1.4, stealth 0.25 to 0.95); Economy parts
(HP 35 to 100; arms strength 0.66 to 1, dexterity 0.66 to 0.8, ranged 0.6 to 0.9; legs athletics and stealth 0.1 to
0.9); KLR Series (arms HP 100 to 250, strength 0.75 to 1.1, dexterity 0.75 to 1.1, unarmed bonus 0 to 5, ranged 0.75
to 1; legs HP 100 to 300, athletics 0.4 to 1, stealth 0.1 to 0.5); Skeleton arms (HP 50 to 175, strength 0.75 to 1,
dexterity 0.75 to 1.25, unarmed bonus 0 to 4) and legs (athletics 0.4 to 1.1, stealth 0.1 to 0.6 or 0.7);
Industrial Lifter arms (HP 70 to 200, strength 1 to 1.25, dexterity 0.6 to 1, unarmed bonus 0 to 4); Thief's Arm
(right: ranged 0.75 to 1, thievery 0.9 to 1.25) and Steady Arm (left: ranged 0.75 to 1.25, thievery 0.75 to 1).

Robots (RACE `is robot`: Skeleton and the MkII skeletons, Soldierbot, P4 Unit, Screamer, Crimper, Big-Bones,
Hive Queen, the robot spiders; **Verified** probe for the flag list): attributes are fixed (editor text: "can only be
upgraded with parts"). The typical values hold for the skeleton-type robots only (Skeleton, MkII, Screamer, P4,
Soldierbot: `hunger rate` 0, `bleed rate` 0.1 to 0.4, `max blood` = `min blood` = 100, `heal stat` 6 (Robotics),
`heal rate` 2 or 3, the fastest of all races), **not** for every `is robot` race (**Verified** probe; an earlier
draft generalised): the robot spiders have heal rate 1, bleed rate 0.5, blood 250/250 and self healing; Big-Bones
has heal rate 1, blood 1000/5000 and no self healing; Crimper has hunger rate 5, heal rate 1, blood 50/600,
strength 2; the Hive Queen has `heal stat` 14 (Hive medic), hunger rate 3, heal rate 1, blood 50/50. Robots gain
**wear** (permanent maximum loss, repaired at special beds; the UI:
"points of wear damage ... can be fixed at a Skeleton Bed") and their limbs do not degenerate. `RaceLimiter` (a map
`GameData* -> Limiter`) restricts which items a race may equip (not decoded).

## Races

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
| `vision range mult` | detection range | +0x74 | AI |
| `is robot`, `swims`, `carriable`, `single gender`, `gigantic`, `vampiric`, `no hats`, `no shirts`, `no shoes`, `cant enter buildings` (stored inverted) | flags | +0x7c, +0x79, +0x7a, +0x7b, +0x78, +0x7d, +0x7e, +0x7f, +0x80, +0x9c | many |
| `extra attack slots` | added to CONSTANTS `max num attack slots` (1) | +0x84 | combat (`FUN_1406658c0`, ...) |
| `self healing` | slow healing without bandaging (read per part) | per part | `FUN_14064dd10` |
| `strength`, `dexterity` | stat multipliers (starting value and XP rate) | map entries 1, 18 | all XP code |
| `stats good<N>`, `stats bad<N>` | x1.2 / x0.8 for the listed stat numbers | map at +0xa0 | all XP code |
| `weather immunity<N>` | WeatherAffecting kinds ignored | set at +0xe0 | weather |
| `special food` | extra ITEMs the race can eat | set at +0x00 | eating |
| `vampiric` | "when eating, sucks nutrition instead of eating limbs" (editor text) | +0x7d | eating |
| `combat anatomy`, `severed limbs`, `limb replacement` | body parts, dropped limbs per slot, default prosthetics | references | see above |

`combat move speed mult` is read elsewhere (`FUN_1406628e0`, **Unknown** effect); `pathfind *`, `hull size *`,
`portrait *`, `blood colour` (ARGB, RaceData +0x8c..) are movement and visuals.

Base-game values (probe over RACE, merged core load):

| Race | heal stat | speed min/max | walk | hunger | heal | bleed | blood min/max | str | dex | notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Greenlander (also Cannibal, Flayed) | Medic | 70/120 | 15 | 1 | 1 | 1 | 75/150 | 1 | 1 | good Science, Farming, Cooking; the `Cannibal Skav` variant has hunger rate 0.6 |
| Scorchlander | Medic | 80/120 | 15 | 0.9 | 1.1 | 0.9 | 75/150 | 0.9 | 1.1 | |
| Shek | Medic | 70/110 | 15 | 1.25 | 0.8 | 0.9 | 75/150 | 1.1 | 0.8 | |
| Hive Worker Drone | Hive medic | 80/140 | 15 | 0.5 | 1 | 0.3 | 50/50 | 0.8 | 1.2 | |
| Hive Soldier Drone | Hive medic | 70/130 | 15 | 0.6 | 1 | 0.3 | 50/50 | 1 | 1 | |
| Hive Prince | Hive medic | 70/140 | 15 | 0.5 | 1 | 0.3 | 50/50 | 0.8 | 1.2 | |
| Fishman | Medic | 65/75 | 15 | 1 | 1 | 1 | 75/125 | 1 | 1 | |
| Skeleton | Robotics | 70/110 | 15 | 0 | 2 | 0.1 | 100/100 | 1 | 1 | robot |
| Soldierbot, P4, MkII skeletons | Robotics | 70/110 | 15 | 0 | 3 | 0.1 to 0.4 | 100/100 | 1 | 1 | robots |
| Garru | Medic | 110/130 | 15 | 2.1 | 2 | 1 | 75/150 | 1 | 1 | self healing, 1 extra attack slot |
| Bull | Medic | 80/130 | 13 | 2 | 2 | 1 | 75/250 | 1 | 1 | self healing |
| Goat | Medic | 90/90 | 15 | 1 | 2 | 1 | 30/80 | 1 | 1 | |
| Beak Thing | Medic | 170/170 | 15 | 5 | 1 | 1 | 100/400 | 1 | 1 | 2 extra attack slots |
| Leviathan | Medic | 40/40 | 14 | 10 | 1 | 0.3 | 1000/5000 | 1 | 1 | gigantic, 3 extra attack slots |
| Spider | Medic | 60/60 | 25 | 0.5 | 1 | 0.1 | 20/50 | 1 | 1 | vampiric |
| Crab | Vet | 50/56 | 15 | 2 | 1 | 0.4 | 40/300 | 1 | 1 | |

`self healing` is true for most animals (Garru, Bulls, Cage Beast, Goat, Bonedog, Boneyard Wolf, Swamp Turtle, Beak
Thing, Leviathan, Crab, raptors, spiders, Skimmer, Land Bat) and two robot spiders; `vampiric` for Spider, Small
Spider, Skimmer, Land Bat (**Verified**, probe).

Animals use `CharStatsAnimal`, fed by CHARACTER / ANIMAL_CHARACTER fields (`animal strength`, `HP mult`,
`lifespan`, `scale min/max`, `smell blood`); not decoded here.

## Encumbrance and carrying

**Observed**, `FUN_140884780` (the encumbrance factor `e`, stored at `CharStats+0x190` by the stats refresh
`FUN_1408866b0`, `CharStats` vtable slot 4), `FUN_1405c89d0`, `FUN_1408c60c0`, `FUN_1408837a0`.

- Carried weight `W` = the inventory's total weight (`FUN_1405c89d0`) plus `carry person weight` (30 in the base game)
  when carrying a person.
- Capacity `C = carry weight mult * strength * strengthInjuryMultiplier + encumbrance base`, with
  `carry weight mult` 1.2, `encumbrance base` 15 (the load at strength 0), `strength` the effective strength stat
  and the multiplier `FUN_1406457e0(strength)`.
- **Encumbrance factor** `e = clamp((C - 0.05 W) / (0.95 W), 0, 1)`: 1 while `W <= C`, then falling to 0 as `W`
  approaches 20 C (W = 2 C gives about 0.47).
- Effects: run speed (multiplier), hunger rate (1 + (1 - e) * 0.7 when active), strength XP from walking
  ((1 - e)^2, capped 0.5), athletics XP, the dodge skill.
- `weapon inventory weight mult` (0.5) scales the inventory weight of weapons (weapon weight is derived from
  blunt damage; editor text). A bag's `encumbrance effect` multiplies the weight of its contents.

## New-game advanced options that change these mechanics

**Observed**, UI constructor `FUN_1409151d0` and writer `FUN_140912f80` (nine floats copied to the globals at
`142133578..142133598` when options are applied). The exe image holds zeros there, so defaults are set at game
start, not statically readable (**Unknown**; the sliders' ranges are in the binding).

| Option label | Global | Slider range | Effect found |
| --- | --- | --- | --- |
| Hunger time | 142133594 | 0.25 to 8 | multiplies `starvation time` |
| Chance of death | 142133578 | 0.5 to 4 | multiplies the bleed rate (`FUN_140646140`) and the wound degeneration (`FUN_14064f5c0`); it does **not** touch starvation: the starvation time reads the *Hunger time* float, which is at +0x1c of this block (**Verified**: addresses of the slider bindings in `FUN_1409151d0` and the writer `FUN_140912f80`) |
| Global damage multiplier | 142133580 | 0.5 to 4 | multiplies damage and combat XP ([combat.md](combat.md#global-tuning-the-constants-record)) |
| Building speed / Research speed / Production speed | 142133584 / 14213358c / 142133590 | 0.5 to 2 | construction, research and production rates, [buildings-production.md](buildings-production.md#global-constants-used-here) |
| Number of nests multiplier | 142133588 | 0.5 to 4 | scales the nest count of the area spawner, [factions-squads-towns.md](factions-squads-towns.md#63-nests) (**Observed**: that doc's "global density float" `DAT_142133588` is this slider; [combat.md](combat.md#global-tuning-the-constants-record) lists the binding) |

Other settings read by this subsystem (`settings.cfg`): `Blood` (`DAT_142133574`: gates blood effects) and
`Dismemberment` (`DAT_142133504`: 0 Never, 1 Rare, 2 Frequent).

## CONSTANTS used by this subsystem

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
| `food quality mult` | 0.5 | [1.0] | x1 (stored as is; it scales a food item's `charges`. It is `food price mult`, not this field, that is multiplied by `global price mult`: [economy.md](economy.md#3-constants-price-block)) | item constructor `FUN_14075faf0` |
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
| `base block chance`, `block chance increase` / `reduction per 10levels` | 70, 12, 15 | | x1, x0.1, x0.1 | `FUN_140885d80`, `FUN_1408862d0` (blocking, not decoded) |
| `attack chance factor` | 0.05 | | x1 | `FUN_140665370` |
| `minimum lockpick chance` | 5 | | x0.01 | lockpicking |
| `appearance random deviation percentage` | 0.4 | | x0.5 | [../characters.md](../characters.md) |

Fixed defaults the loader writes before the reads (no FCS field): six floats at indices 0x2b to 0x30 = 20.0
(`DAT_142133e7c`..), 0.3 at 0x3f, 0.6 at 0x65 and 0.06 at 0x66 (**Observed**, re-read; an earlier draft said seven).
The writer copies nine floats, so two more slots (`14213357c` and `142133598`) have no row above: they are bound
to two other UI controls in `FUN_1409151d0` (not traced).

## Unknowns

- What the `dt * 0.05` factor and the small `0.04` / `0.002` / `1.2` constants in the medical update stand for,
  and whether `dt` is exactly game hours; the unit of the KO timer.
- The per-stat floors of `FUN_1406457e0` were re-decoded from the decompile by a second reader (two rows were
  wrong and are fixed) but still not checked against assembly; the source of the pain value at Character +0xd8,
  and the consumers of LOCATIONAL_DAMAGE `affects skills` / `affects move speed`.
- The situational hunger factors (a machine/building, CharStats+0x178), slot 0x348's meaning, item nutrition
  values and how `food quality mult` composes with them.
- Which callers feed `FUN_1408c7250` / `FUN_1408c7310` for which activity, the exact medic XP time curve (`DAT_142133794` is the scaled frame time, [game-loop.md](game-loop.md#the-three-time-values)), and the 0.1 floor / 6x cap of the skill-difference factor
  (not in `FUN_1408c5fa0`; maybe clamped by its callers). Meaning of XP event 6.
- The defaults of the new-game option block (zero in the exe image).
- RACE `combat move speed mult`, the `starting health` reference (editor: value -100..100 per part, below -100
  amputated), animal CHARACTER fields.
- Which of the two ints of a `combat anatomy` reference is HP and which the hit weight (the part constructor
  `FUN_14064d0e0` takes the weight into +0x38, the HP into +0x54 and a scale into +0x58, but the call arguments
  are hidden in the decompile, so the assignment is inferred from consumers and data), and the meaning of the
  character slot +0x248.
- What the Hive Queen and Crimper (`is robot` races that eat and have blood) are meant to be in the flag semantics:
  whether `is robot` alone selects wear and robotic healing for them (it does in the part code, **Observed**).
- Damage kinds 1, 5 and 6 passed to `FUN_140666780` / `FUN_1408c68e0`.

## Implementation outline

Order that gives playable behaviour early. Existing pieces: `GameDatabase` / `GameRecord` expose RACE, STATS,
CONSTANTS, LOCATIONAL_DAMAGE and LIMB_REPLACEMENT by field name (`GetFloat`, `GetInt`, `GetReferences`);
`CharacterGenerator` ([../characters.md](../characters.md)) already reads STATS-like values for appearance; the
body-shape code ([../animation.md](../animation.md)) needs the hunger level and strength defined here.

1. **Data layer**: typed readers over `GameRecord`: a `GameConstants` type applying the loader scalings (x0.1 for
   bleed, clot, blood recovery and medkit drain, x0.25 for exp gain, x0.5 athletics XP, damage values x
   `damage multiplier`), `RaceData` (the fields above, the stat map with 1.2 / 0.8 / strength / dexterity, the
   anatomy list), body-part templates from LOCATIONAL_DAMAGE, `LimbReplacement` with quality interpolation, and
   the `StatsEnumerated` enum from this doc. Turn the probe tables here into tests (skipped without the game).
2. **Stats**: a `CharacterStats` with the 38 stat numbers, construction from STATS (or the CHARACTER integer
   groups), randomisation and race multipliers, `Gain(stat, amount)` with the `((101 - s) / 101)^2` curve (note it
   keeps growing above 101, up to about 553), then
   the event sources (combat events, walking, medic, ...) behind an XP service combat and AI can call.
3. **Medical simulation** (a new simulation type, `dt` in game hours): `HealthPart` with `h, s, b, w` and the
   update formulas, wounds and blood, hunger level with stomach buffer, KO timer, death rules, severing hooks
   (limb state), self healing and robot wear. Expose `GetStatMultiplier` (hunger, limbs, pain) used by every
   stat read.
4. **Care**: bed/rest factors, first aid with kit quality and drain, robot repair, the new-game option block and
   the Dismemberment setting.
5. **Encumbrance and speed**: the weight / capacity function and the run-speed chain, wired into movement when
   movement exists in the engine.
6. **Presentation**: feed the body-shape code the hunger level and strength; status-panel texts from the
   thresholds here; per-part health from the anatomy.
