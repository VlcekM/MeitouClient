# Perception and fleeing

Original sense/GroupSense/vision/hearing/runaway research. Binary literals are Verified;
interpreted flags/control flow stay Observed or Unknown. AI target choice: [ai](ai.md);
relations: [factions](factions-squads-towns.md). Simulation senses/leash remain approximations.

## Sight

- **Range** (`FUN_140851bc0`, **Verified** literals): 500 inside a building, else 1500 x race `vision range mult`; x
  lerp(daylight, 0.5, 1); x weather: lerp(s(1-P), 1, 0.4) dust, lerp(.., 1, 0.75) any other affecting weather on the looker
  ([weather](../formats/weather.md#effects-on-characters), **Verified**; Meitou `AggroSystem.RangeOf`).
- **Cone**: v = (cos a - 0.2) / 0.8, zero past 78.46 deg. Crawling/ground posture x0.5; sneaking x0.75 (v >= 0.5) or x0.5,
  then x lerp(stealth, 1, 0.6). Stealth scales skill x carry 0.75 x crawl 2 x load/equipment/race/injury, clamped 0..1.
- **Seen** (`FUN_140852f70`, **Verified** literals): d <= lerp(ambient, F, 1) x contrast x R x v, R x2 when the looker is on
  top (below); contrast = clamp(target light - looker light + 1, 0, 2), ambient = clamp(mean of both, 0, 1); F = 0.2 when the
  looker is inside a building (character vfunc +0x78, the building it is in; **Observed**), else 0.4. v must exceed 0.01.
- **Place** (**Observed**, `FUN_140857b10`, Character slots 59/60): each character has a location (slot 59: a carried one its
  carrier's, a caged or bedded one its building's, else its own handle `+0x80`, whose writer is not traced; type 0xb is
  outdoors). Looker and target must share it unless either is **on top**: slot 60, in a building and `+0xd5` set, from an
  upward physics query (mask `0x8079e00`, in the character update) finding nothing over it, i.e. a roof or roofless part.
  A building whose BUILDING `enforce ceiling` is set (7) has nobody on top above floor 0 (`Character::threadedUpdatePeriodic` `FUN_1405d00b0`, **Verified**).
  The location compare (hand vfunc +0x10, `FUN_1400cd0b0`) is `!=`: true when the handles differ (**Verified**); town alarms store the culprit's location ([economy](economy.md#town-alarms)). A physics ray (`FUN_1408533a0`, start 18 above the looker,
  blocked by anything more than 2 short of the target) runs in some branches.
- **Light level** (`+0xd8`, `FUN_140a0af10`, `FUN_140a08da0`, **Verified** literals): set each update to the daylight factor;
  when requested by a sight test (`+0xdc`) and the character is the player's or daylight < 0.75, it is resampled: 1 above
  daylight 0.5, else 2 x daylight + per light within 500 of the zone search: 1.5 x power scale x (1 - d^2 / r^2) (x the spot
  cone (cos - cos(outer/2)) / (cos(inner/2) - cos(outer/2)), clamped) while d <= r; a light counts only when its indoor flag
  equals the character's and, both inside, its floor (custom parameter w) equals the character's (slot 0x60). Building
  lights and characters' carried lights both count.
The request byte `+0xdc` is raised by the sight test `FUN_140852f70`:104-112 on looker and target (the target's value is read before the flag is set, so the first test after a quiet period sees bare daylight) and by the stat multiplier `FUN_1406457e0`:190 (**Verified**). Meitou (`SightMap.SampleLight`, `Light` / `PeekLight`): the same rule with a per-slot request flag that the workers only ever set (`Range` and the stat multiplier read through `Light`, the HUD and the query bound through `PeekLight`) and the serial update consumes, so the outcome does not depend on the order of the parallel tests.

Meitou (`SightMap`): every sight test (aggro, dialogue sightings, crime witnesses, bounty spotting, slave catching, loot witnesses) uses these
rules. Rooms are the registered towns' buildings' `interior mask` volumes and lamps their lights, plus the lanterns characters carry
(`CharacterAppearance.Lights` at the owner, its place and floor) and the lamps of completed player buildings (outdoor lights; ruins and the player's buildings have no room); a lit lamp of another building's interior is not counted indoors (floors are
compared only within the character's building). With the `interiorlamps` switch on (Meitou, default) interior lamps count at
any daylight, as the renderer draws them; off, night-only ones count at daylight <= 0.1 as in the game. No physics ray, no weather, no posture factor; the 0.75 / 0.5 light gate is
evaluated every tick for everyone. The overhead and floor queries are redone only when a character moves (headless, Release:
0.29 ms per tick for 840 characters, 540 indoors, standing). Observed in the data (headless probe of five big towns' 60
lit rooms): standing under an interior lamp gives light 0.7-2.9, 444 of 462 lamps lighting the floor below them; the Hub's
intact buildings are mostly ruins, which count as outdoors.

Can-see first checks state/squad-shared sightings (within2R, no cone/light), then player rules,
building compatibility, NPC test/stealth accumulation and conditional physics ray. Ray starts
18 above looker, obstruction>2 short of target blocks. Unknown flags/range cap/special tests are
not universal vision rules. Shared senses keep targets about two completed squad update rounds.
Faction hostility/disguise/personal tags choose enemy; coexistence not read in this predicate (the attack-on-sight finder drops a coexisting faction afterwards, looker's list only: [tests](#the-attack-on-sight-tests)).

## Spotting a sneaker

A sneaking target in view (v > 0.01) of an NPC is not seen at once (`FUN_140857b10` -> `FUN_140856cb0`, extra args 0.5
and stealth x 5). Each looker keeps a meter per target (`SensoryData::SpottingPeopleMgr::Spot`, map at sense +0x150).
**Verified** literals, flow **Observed**:

- Seen (result 1) when the meter was above 1 before this check; else result 2 (noticing). No line of sight: result 0.
- Not in view: the meter empties by dt / 260 (`FUN_140853aa0`) and is dropped at 0.
- Stealth XP: see [character-stats](character-stats.md#levelling-xp).

## Disguise

State on the character's status object (Character `+0x1a0`): level `+0x100`, uniform faction `+0x108`, armour stigma `+0x104`, blown cooldown `+0x110`.
- **Uniforms**: an item's `Gear::isUniform` faction (`+0x1e8`, KenshiLib 1ce4a16), saved as the item's `uniform` string (the faction's id; `Gear::_serialise`). Clothing a
  CHARACTER with `wears uniform` is made with gets its faction ([economy](economy.md); Meitou `CharacterAssembly`, `ItemInstance.Uniform`).
- **Works** (`FUN_1407c0520`, **Verified**): level > 0.6 and cooldown <= 0; then the sense tags take the uniform faction for the character's ([sense tags](#sense-tags-who-is-an-enemy)).
- **Cooldown**: the same field crimes set ("recently wanted", 250 from a witnessed or booked crime, [economy](economy.md) 11.2); counted down by dt (`AI::updateStateBroadcast`
  `FUN_1405a4ae0`); saved as `disguiseblown` (`FUN_140626580`, **Verified**).
- Not traced: `sellingUniformDetectionCheck` (selling a uniform, `CRIME_UNIFORM_THEFT`), event `EV_I_SEE_UNIFORM_IMPOSTER`, the dialogue gates of "regards as enemy".

Meitou (`ArrestSystem.Disguise.cs`, every spot sweep): the level and faction from the worn head, armour and legs; lookers are awake NPCs not of the player's faction within 400 that see
the character (`SightMap.Sees`), with their own meter per pair (not shared with the sneak meters, engine choice), alertness 0.5; the town tests read the town the point stands in
(engine reading of the status object's hysteresis), inside the walls by the gate regions. The slave and bounty branches stay the arrest system's (a working disguise hides the bounty
from its spotting); `AggroSystem` and the arrest spotting take `ArrestSystem.SeenFaction`.

## Senses

`SensoryData` (Character vfunc 0x68; the AI holds it at AI `+0x28`, so AI `+0xb0` is its `+0x88`, `+0xb4` its `+0x8c`, `+0xbc` its `+0x94`). **Verified** by decompile unless marked.

- **Entries**: a hash map by character hand (count `+0x28`), no bound; each 0x40 bytes (`FUN_1408580d0`): place `+0x0`, the time last sensed (`+0x10`, world hours), faction `+0x18`,
  slave state `+0x20`, tag word `+0x30` ([tags](#sense-tags-who-is-an-enemy); also bit 3 KO when down or dead, bit 9 imprisoned, 5/6 slave states, 7/10/11/12/13/14/15 other states), seen
  `+0x38`, heard `+0x39`, alert timer `+0x3c` (counted down by the pass).
- **Pass** (`FUN_14085a070`, at the start of every AI update `FUN_1405112b0` through `FUN_14085a8d0`, before the task system's update `FUN_140511130` that chooses, so a replan reads a current
  threats list; skipped once when a squad mate's pass was shared, `FUN_14085a770` +0x1d0): `+0x9c` keeps the last threat total (`+0x88` + `+0x8c`); the counts, threats list, threat sums and nearest enemy (`+0x0`) are cleared;
  every existing entry is sensed again first (its character taken out of the gather), then each remaining gathered character (`FUN_1408593d0`): seen (`FUN_140857b10`), else heard
  (`FUN_140857500`). Sensed: the entry is made or refreshed (place, time, seen, heard, tags) and the threats run on it; not sensed: decay. An animal whose last threat total was 0 and is
  above 0 now says `VO_Creature_Alert`. Dropped entries are deleted at the end (`FUN_140856330`).
- **Counts** (`FUN_1408593d0`, every entry kept or decayed this pass): tagged ENEMY and its character not down (vfunc 0x30), dead (`+0x5bc`) or carried (`+0x3d4`): `numEnemies` `+0x94`
  and the nearest's squared distance at `+0x0`; else tagged NEUTRAL and not down, dead or carried: `numNeutrals` `+0x98`. At a refresh an ALLY standing (posture 0), not out and not dead
  counts at `+0x4c` (conscious allies), any other ALLY at `+0x48`.
- **Threats** (`addToThreatsAndFlockingList` `FUN_140854370`, at a refresh only): see [combat](combat.md#fight-lifecycle-and-orders). The list `+0x50` is the characters whose intended
  target is the looker; `+0x88` sums their positive aggression, `+0x8c` the aggression of those whose target is of the looker's faction, `+0x90` all sensed others' intended aggression.
- **Forced sense** `FUN_140858f40` (`AI::underMeleeAttack`, massive alarms, a dropped body, a bolt's victim): no entry → one is made as seen (with its threats); then seen set and the
  alert timer 5. An existing entry's threats wait for the next pass, which the replan's AI update runs first. So an attacker is a threat
  of its target by the time it chooses.
- **AI `+0xbc`** is `numEnemies`: its reach is the gather's (sight range, [summary](#summary)) plus every entry not yet decayed. Readers: goal 16's finder and `FUN_14059fa70` (gate), the
  urgent kind-2 jobs (`FUN_14050e5b0`), state 6 `isNoEnemiesInVicinity`, the medic job's desire (0.01 while `+0xb0` > 0, else 0.05 while `+0xbc` > 0), IS_OUTNUMBERED, playing dead.
  The fight's end and the chase give-up read the senses directly ([leaving a fight](combat.md#leaving-a-fight)); target choice the threats and the entries ([target selection](combat.md#attack-slots-and-circling)).

A carried or knocked-out character runs `FUN_140859220` instead of the sense update (`FUN_1405d00b0`: the sense update runs when not carried and either not down/dead or the medical get-up test `FUN_140598c20` passes, **Verified**): counts and threats cleared, each entry decays as one not sensed, nobody gathered (Meitou: `AggroSystem.Fade`). So one lying awake (playing dead) senses normally; Meitou also lets one whose knockout ends within two looks, or that is held down for its look, look (`AggroSystem.AwakeLying`, engine choice: playing dead is decided on the waking tick; `SenseBlock.Faded` marks a faded block).
Meitou (`SenseBlock`, `CharacterFacts.Senses`): written by `AggroSystem`'s look in `Act` for every living character not carried or knocked out (`AwakeLying` aside) on its turn (one in `ScanEveryTicks` = 15, by slot), the looker's
own slot only, and by `AggroSystem.Force` (serial: the alert, a landed blow, a bolt, an alarm; it also takes an existing entry's threat at once, standing for the pass the original runs before
its chooser). Engine choices: at most 40 entries (`SenseBlock.Cap`; a new one replaces the one sensed
longest ago; when all were sensed at this look a kin or enemy entry replaces a neutral's and an enemy one an ally's, `AggroSystem.Insert`, so a squad
of more than 40 does not fill its blocks with its own mates and miss the enemy it fights, **Measured** on the #412 save: 75 of 88 Holy Nation
attackers had no target before, none after; a block can fill with enemies and lose allies in a large battle), the gather takes up to 60 new characters within 250 then up to 40 in all within reach (every character it visits counts, sensed or not, in the grid's order; **Measured** in the
sandbox with 250 idle neutrals round a player squad: the player's characters kept 0 entries, so a squad mate never sensed its attacked mate and none joined, 15 Protect allies reading
nothing; with the quota lifted all joined within 10 ticks). So a looker's **kin**, its squad mates and for a player's character every other player character, are sensed at each look
before the gather, outside the quota, and take the place of a neutral stranger's entry in a full block (`AggroSystem.VisitKin`); so does every **fighter** (`InCombat`, listed per tick in `Sync`) within the look's reach (the sight range or 600 for noise, `AggroSystem.VisitFighters`), whatever its side but the looker's own squad (visited as kin), so the goals that join other people's fights (34, 15, 288, 143, attack on sight of a fighter) find the attacker and its victim in a crowd; the pass stops at the first enemy fighter the full block cannot take (every entry an enemy or KO) and, once any fighter was refused, skips the looker's own faction. **Measured** in the sandbox (250 idle neutrals of the victim's faction round a player attacking a neutral squad, 600 ticks): crowd members sensing the fight 8 of 250 (the hero) and 0 (the victim) before, 249 and 249 after; look cost 205-260 ms before, 206-219 ms after per 10200 looks; a 300-fighter `--battle` 1217 ms before, 1277 ms after per 24000 looks (a first version without the stop doubled it). Other strangers still wait for the quota; a looker that neither fights nor may attack on sight gathers beyond 250 and works
the relation part of its tags out only every 4th look (`FarLooks`); slot 125 also counts whoever fights the looker or is fought by it, of any faction (Meitou's scripted fights); the
threat sums count 1 per threat (no aggression value). Without the Aggro part (sandbox, duel, battle) the look keeps the senses only (`AggroSettings.AttackOnSight` false).

## Retreat and memory

Tag map and update rates come from binary, not renderer visibility. Runaway/forced-runaway, home-town retreat, retreat-refresh package and
squad Run-Away order have different target/ending rules.

- *Crippled or armless*: goal 32 plans 35 (desire `FUN_1405a34e0`: 1.0 crippled, else a flat 0.001 when 60 holds, 0 for a player-faction one with an arm; no read of outnumbered, injury, the intact flag or the faction ratios). Meitou: `SelfPreservation`.
- *Event 50 SQUAD_BROKEN*: `FUN_1407a5be0` (the platoon's intact refresh) fires it at each member that is not down, at the member's own attack target, on the tick the flag goes from intact to not. Base data binds it once: the `Dust bandit base` package (3 templates), root `defeated in player town- mark for raid` (taunt bubbles, no effect). Not built in Meitou.

### Sense tags: who is an enemy

**Observed** (decompile of the perception update `FUN_1408580d0`; bit names from the senses debug panel `FUN_1407981b0`). Each sensed
entry has a tag word (entry +0x30 → +0x30): bit 0 SENSE_ALLY, 1 SENSE_NEUTRAL, 2 SENSE_ENEMY, 3 SENSE_KO, 14 SENSE_CRAWLING; others
are set by `FUN_1408580d0` (**Verified**): 4 dead (the only bit then set), 3 down or carried (`FUN_1407d1440`, vfunc 0x30), 10 same faction as the looker, 11 the target's race is a robot (`is robot`, race `+0x7c`),
12 the looker is a slave whose owner (hand at looker `+0x328`: a character or a squad) calls the target an enemy (owner's slot 125; goal 41's filter), 13 the target's faction has a player flag (`+0x250`), 14 crawling or
staying low and not in combat mode, 15 carried (`+0x3d4`), 7 the status object's `+0xdc` (vfunc 0x70) 1 or 2, 5 / 6 slave state 1 / 2, 9 imprisoned (`+0x2f8` 2). A tag filter `FUN_140853960(m1, m2)` keeps entries with `tag & m1` non-zero and `tag & m2` zero. The combat finders read it ([ai-tasks](ai-tasks.md#combat-goals)).
- Not the looker's enemy (Character slot 125, `vfunc_125` at 0x3e8): ALLY when slot 126 (0x3f0) says ally, else NEUTRAL.
- Enemy: NEUTRAL when `FUN_140796240` holds (the target's platoon is a campaign force, [ui-input](ui-input.md); Meitou `AggroSystem` Visit), else ENEMY (unless the looker's platoon object lacks a sensor that accepts it).
- So attack on sight is relations only: no fundamental-type BANDIT rule (**Observed**). In the base data the Shek Kingdom, Holy Nation and
  United Cities start at -100 only with Bloodraiders, Cannibals, Rebel Farmers, Grass Pirates, Gorillo Bandits and Yabuta Outlaws (Holy
  Nation also Holy Nation Outlaws); at -10 with Starving and Dust Bandits; at 0 with the other bandit factions (**Verified** by probe of
  the merged data). Skeletons are not a faction rule either: the Holy Nation's kill on sight is the event 3 root `Holy Nation spots skeleton`
  (`19066-Dialogue.mod`: target not in combat, not an ally, within talking range; children need DC 79 HAS_ROBOT_LIMBS, true for an `is robot`
  race, and run DA 6), in the packages `Holy Nation Paladin` (Paladin, High Paladin, Holy Sentinel), `Holy Nation Gate Guard`, `holy nation guard` and `Holy Priest`;
  `Holy Chosen` has no dialogue package and the `Holy Army force` templates no `dialog leader` / `dialog squad`, so the Chosen join only
  through the squad's TEMP_ENEMY tag (**Verified** by probe of the merged base data). Meitou headless on the #412 save: a Paladin says the line
  and its 113-member squad attacks within 3 ticks. Fights lower relations ([factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)), so a
  bandit faction a town keeps fighting can drop to hostile. A Dust Bandit squad (the player at -10, not hostile) starts its fight through its
  event 3 / 66 lines ([dialogue](dialogue.md)); `Bandit mugging one liner`, `bandit fight picking new target in wilds` and `Bandit Revenge` need
  the speaker outside a non-player town (DC 31 false), `Give us your stuff!` the target, so inside a town's radius (the Hub: 2500) only
  `Bandit hold up 1` can fire (20 % a check, 50 % permanent; **Verified** by probe of the merged data; Meitou headless on a save at the Hub's
  edge: no attack until the boss's patrol left the circle, then the one liner and the fight). The only BANDIT-type target rule found is the player's Shoot first gunners
  ([squad AI options](ai-tasks.md#squad-ai-options)).

### The attack-on-sight tests

- **Area** (`FUN_140599f30`, **Verified** by decompile): true when the target's current town (vfunc +0x78) is the looker's
  squad home town (character vfunc +0x98, `Ownerships::_homeTown` +0x30). Else the AI's area test `FUN_140597790` by its manoeuvre mode (AI +0x2bc
  `ordersMovement`, KenshiLib `AIManuverabilityOrders`: 0 HOLD_GROUND, 1 STAY_CLOSE, 2 ROAM_FAR): ROAM_FAR around the
  looker itself (AI +0x2c0 set to its position), in when d^2 < 42467328 (about 6517 units); HOLD_GROUND around the
  centre of movement AI +0x2c0 (height from the state broadcast's position, AI +0x18 +0xcc), in when d < R + 40 + R'
  (R = AI +0x8 `meiDistance`, R' = AI +0xc `meiDistanceTolerance`) or d^2 < (s^2)^2 with s^2 the squared 3D distance from
  the AI's own position record (AI +0x18 +0xc8, **Observed** to be where the AI reads its zone from) to the centre at the centre's own height (operands **Verified** from the disassembly of `FUN_140597790`; as written, a looker more than a few units from its centre takes every target); STAY_CLOSE in when d < R + 200 + R'. A target outside it still
  counts while it is fighting the looker (its combat object's +0x140 between 0 and 1 for the looker, `FUN_140665420`).
  The same position test bounds job targets ([range](ai-tasks.md#perma-jobs), ROAM_FAR by default). Writers:
  `AI::setManuveringFreedomLevel` from standing orders 8/9 ([combat](combat.md#fight-lifecycle-and-orders)); the centre by
  `AI::setCenterOfMovement`, which every player order calls with its point (`Character::addOrder`) and which in
  HOLD_GROUND / STAY_CLOSE also installs the hold-position task 30 / 31. The radii are the constructor's (`AI::AI`: meiDistance 18, tolerance 5; no other writer found), so HOLD_GROUND reaches 63 and STAY_CLOSE 223.
  **Meitou** (`Orders.InArea`, `AggroSystem.InAttackArea`, `CharacterCold.Manoeuvre` / `CentreOfMovement`, not saved like `HoldPoint`): the own-building pass, the area by mode and the fighting-the-looker pass run for the finders 16, 21 and 144 (not 34). Standing orders FAR (8) and NEAR (9) are `ManoeuvreCommand` (FAR: ROAM_FAR, centre cleared; NEAR: STAY_CLOSE, centre at the character; a ground move order moves the centre while the mode is not ROAM_FAR); the orders panel has no buttons for them because the original has none ([combat](combat.md#fight-lifecycle-and-orders)), and nothing sets HOLD_GROUND (**Verified**: no writer of AI +0x2bc but `FUN_140596680`, called only by `setStandingOrder` with 2 and 1); the `s^2` term is implemented in `Orders.InArea`. With the default ROAM_FAR the test never fails (sight 1500 is well inside 6517), so for a sensed target it only bites on player characters with a standing order. The target score's range term takes the mode's reach (`TargetScore.RangeOf`).
- So there is no give-up distance measured from where a fight began: an attack starts inside the AI's area, and a
  chase ends by time and lost senses ([Leaving a fight](combat.md#leaving-a-fight)).

### Summary

| Figure | Value | Label | Where |
|---|---|---|---|
| Vision range outdoors | 1500 x RACE `vision range mult` (0.5 to 2 in the data; 1 for 41 of 53 races) | **Verified** (literal) | `FUN_140851bc0` |
| Vision range inside a building | 500 (no race multiplier) | **Verified** (literal) | `FUN_140851bc0`, `FUN_140852f70` |
| Night | range x lerp(daylight, 0.5, 1): half at night | **Verified** (literals), daylight factor in [game-loop.md](game-loop.md#frame-clocks) | `FUN_140851bc0` |
| Weather (dust storm / other affecting weather) | range x lerp(s(1 - P), 1, 0.4 / 0.75), unclamped | **Verified** (decompile) | `FUN_140851bc0` |
| Seen when | v > 0.01 and d <= lerp(light, 0.2 or 0.4, 1) x contrast x R x v | **Observed** (control flow), constants **Verified** | `FUN_140852f70`, `FUN_140857b10` |
| Sneaking / crawling target | v x0.5 crawling; x0.75 or x0.5 sneaking; x lerp(stealth, 1, 0.6) | **Verified** (literals) | `FUN_140852f70`; Meitou `SightMap.Sees` (crawling or staying low) |
| Spot time of a sneaker | lerp(v, 0.5 + 5 s, 0.5) x lerp(f, 4, 1) s; meter > 1 = seen; forgets at dt / 260 | **Verified** (literals), flow **Observed** | `FUN_140856cb0`, `FUN_140853aa0` |
| Noise range of a mover | lerp(speed ratio, 350, 600); sneaking lerp(stealth, 450, 80); weather x0.25 / x0.75 | **Verified** (literals) | `FUN_140851d10` |
| Hearing chance per check | 0.75 x (1 - d^2/N^2); sneaking 0.1 (0.05) x (1 - stealth) x ...; x2 if recently alerted | **Verified** (disassembly and bytes) | `FUN_140857500` |
| Gather per update | every character of the character grid (the dead, the down and animals too) within R on the ground plane, R taking 500 inside town walls instead of inside a building: a value above 0 the character keeps, set from the building it is in, else stands on (a gate: its [gate code](pathfinding.md#gate-codes) at the character's position; a wall: unchanged; another building: its own); at 40 or more, everyone within 250 too. No cap: `FUN_1409ff210` ignores the limits 40 and 20 it is passed | **Verified** (literals; decompile: `FUN_1407866d0` adds the 250 lookup at 40 or more, `FUN_1409ff210` never reads its limit); who the grid holds, the ground-plane test and R's town-walls rule **Observed** | `FUN_14085a8d0`, `FUN_1407866d0`, `FUN_1409ff210`, `FUN_1409fb450`, `FUN_140792200`; Meitou caps the new characters of a look ([senses](#senses)), which the original does not |
| Run away (Task_Runaway) | 2000 + 400 r away from the threats' centre, then hops of 1000 + 400 r; until no enemy is sensed (state 6) and no threat attacks within 40 (state 39), 35's list A; under goal 32 until the threat level is 0 | **Verified** (literals), flow **Observed** | `FUN_14034b440`, `FUN_140336820` |
| A lone character running away | Goal 32 (every character's) plans row 35 when a crippled character (the medical crippled flag, Character `+0x5b8`) or one with no working arm is threatened; anyone else fights through 13/4. Desire AI slot 55 `FUN_1405a34e0` (1.0 crippled, else 0.001, player-faction veto), set out with the planner's choice in [ai-tasks](ai-tasks.md#combat-goals). Meitou: `SelfPreservation`, run by `FleeSystem` | **Verified** (slot 55 literals, flag, state 26), the planner's choice **Observed** | `FUN_1405a34e0`, `FUN_1405a4260`, `FUN_140599e90`, `FUN_140649320` |
| Attack-on-sight area / post | ROAM_FAR: 6517 around the looker; HOLD_GROUND / STAY_CLOSE: R + 40 / R + 200 + R' around the centre of movement; Hold: 80 (140) / 60 / 40 | **Verified** (literals), flow **Observed** | `FUN_140597790`, `FUN_140598630` |

## Unknowns

- Slot 14 `+0xb4`, slot 60's `+0xd5`, the hearing re-roll factor.
- The writer of a character's location handle `+0x80`; when can-see casts its physics ray; the building rules of can-hear;
  the alertness goal float and the +0x3f0 (green pointer) test of spotting.

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 4. Can see: the order of tests (`FUN_140857b10`)
