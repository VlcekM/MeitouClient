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

- In view with line of sight: the meter fills by dt / T, T = lerp(v, 0.5 + 5 s, 0.5) x lerp(f, 4, 1). f is the looker's
  alertness: 0.5 without a current goal, else a goal float (+0x3c, meaning **Unknown**); 0 when it is KO'd, busy
  (+0x2f8 count > 0) or in state 1, and then the meter does not fill and the target is not seen.
- Seen (result 1) when the meter was above 1 before this check; else result 2 (noticing). No line of sight: result 0.
- Not in view: the meter empties by dt / 260 (`FUN_140853aa0`) and is dropped at 0.
- The target keeps a watcher list (+0x1f0, `FUN_140855f30`): level and time per looker; level 0 removes it. Its only two writers are the spot
  meter above (level 2 noticing, 1 seen, 0 removes) and the **forced sense** `FUN_140858f40(looker, target)` (level 1, meter 1.0 = `DAT_14167c308`);
  hearing (`canHear`) writes none, so a heard sneaker gets no arrow (**Verified**: the callers of `FUN_140855f30` are exactly these two functions).
  The forced sense is called by the perceive-and-tag `FUN_1405cb0a0` (DA_MASSIVE_ALARM, AI tasks), the dialogue actions 6 / 55 / 57 and events 0x21 / 0x37
  (`FUN_140680560`), the landed blow `FUN_140999f00` and bolt `FUN_140999d50` (the victim senses the attacker; the blow's early returns on its `param_4`
  flag are not decoded), the drop of a carried body `FUN_1405ce1e0`, and the tasks `FUN_14034cd80` / `FUN_14034ffa0` / `FUN_1403552d0` (**Verified**, callee lists).
  `FUN_1408586d0` shows, for entries younger than 4 s (`DAT_14168bf24`; the loop drops the first stale entry and stops), only for a player-owned sneaker (**Observed**: its faction object's `+0x250` or `FUN_140856bc0`), and none
  for a looker that is being carried (`+0x3d4`, **Verified** [carry-loot](carry-loot.md)), an `arrow.mesh` entity per looker (`FUN_1405dc7d0`, pooled, render
  queue 0x57): dir = looker - sneaker (3D), tail at sneaker + (0, 12, 0) + dir x min(|dir| - 7, 15) (no skip when negative),
  turned from -X onto dir, then the node is scaled (0.5, 0.35, 0.35) in its own frame (**Verified**: `Node::setScale` with `DAT_14167e8d8` 0.5 and `DAT_141695190` 0.35).
  The mesh runs from its origin (shaft radius 0.5) to the tip at -X 10, head ±1.2 (**Verified**, read with `OgreMeshReader`), so the drawn arrow is 5 long, shaft radius 0.175, head ±0.42. Colour red 0xdd0000 detected, yellow 0xffdd00 noticing, green 0x00ff00 when a
  looker test (vtable +0x3f0, **Unknown**) passes. Material (`FUN_1409b7830`): `debug_marker` cloned as `debug_#rrggbb`,
  pass 0 `colour` = (rgb, 1); with its last argument set (this call) pass 1 is kept as (rgb, 0.2), else removed. The
  sneaker's state +0xe8 (`stealthUnseen`, **Observed** `FUN_1408586d0`) becomes 0 when it is fighting or a looker's meter is red, 2 when only yellow, else 1 (unseen).
- Name tag (`FUN_1406e8100`, colours loaded in `FUN_1406eb3a0`): shown within 10954 of the camera (1.2e8
  squared). Tag state 1/2/3 tints a stealth icon with the GUI colours `NameTagStealthHidden` / `AlmostSeen` / `Seen`,
  fallbacks (0.25, 0.521, 0.745), (0.745, 0.721, 0.25), (0.745, 0.25, 0.29) (**Verified** literals; the GUI XML may
  override). A second icon's frame is round((1 - x) x (frames - 1)), x = clamp(character +0xd8, 0, 1): the
  `LightLevel` image of `Kenshi_CharacterNameTags` (7 frames, frame 0 full light; **Verified**, kenshi_images.xml), on the
  panel's right in colour (0.942, 0.806, 0.341) (**Verified**, `Kenshi_CharacterNamePanel.layout`). The tag state is set alongside: 3 seen, 2 almost seen, 1 hidden.
  A name longer than the layout's text box shows whole, "[Drifter-cannibal escapee]" for one (**Observed**). Whether the panel widens to fit
  it or the text overflows its box, and whether a short name narrows the panel, is **Unknown**; Meitou widens it, the text box stretching and
  the icons keeping its edges by the layout's aligns (`Stretch`, `Left`, `Right`).
- Stealth XP: see [character-stats](character-stats.md#levelling-xp).

Meitou: `AggroSystem` keeps one meter per looker and sneaker for every awake NPC of another faction (one that picks no fights
looks for sneakers only, when one is in range; not in bed, asleep, carried, caged or fighting), no line-of-sight ray, f = 0.5,
dt = the scan interval; a meter decays also when the target is out of range (the original's rule there is **Unknown**). Crime
witnesses and the stealth knock-out read the same meters. A sneaking player
character gets the arrows (`MarkerRenderer`, both passes like the movement marker, lit by the sun; no green) and the original's stealth icon left of its name in the tag
colours: seen if any watcher detects, almost seen if any notices, else hidden, and the light-level icon from its `SightMap` light.
The list (`CharacterCold.Watchers`, 4 s, a carried looker left out) is fed by the spot meter (`AggroSystem.Visit`, sight only, as in the original) and by the forced
sense `AggroSystem.Notice` (level seen, for a player-side target of another faction): a massive alarm, dialogue attacks 6 / 55 / 57, and a melee blow or bolt that lands on a
conscious victim (engine choice: not when it goes down). Not wired: the forced sense of the AI tasks, the body drop and dialogue events 0x21 / 0x37, and a hit's `param_4`
gate. Whether the arrows read well in game (the mesh is 10 units long) was not checked with a run (**Unknown**).
Every player character within the 1.2e8 range gets the `Kenshi_CharacterNamePanel` over its head (name in `Kenshi_StandardFont_Large` with shadow, selected: white in [brackets], else #b3b3b3 (**Observed**); the icon only while sneaking); NPCs get none
(**Observed** in the original). **Verified** (`FUN_1406e8100`, `FUN_1406e2fd0`, `FUN_1406e2900`, `FUN_1405cc310`, `FUN_1403eca90`): a tag is made when a character with a faction whose `+0x250` is set (and a relation test against the player's faction object, **Unknown**) enters the world; the `Show names` option (`DAT_142133508`, saved by `FUN_1403eca90`) is copied into each tag (`+0x20`), and off hides the name text and sizes the panel to the two icons (Meitou: `PlayerInterface.ShowNames`, Interface settings "Show names", default on); a carried character's tag is lifted by the name's height (Meitou draws none for a carried one). Where the tag stands is **Unknown**; Meitou puts it 22 over the feet, moving it over the drawn head as the head drops from 12 to 8 above them (crouching, sitting on the ground, a sleeper laid onto the mattress away from its feet; `PlayerInterface.TagAnchor`, also for task bars and speech).

## Disguise

State on the character's status object (Character `+0x1a0`): level `+0x100`, uniform faction `+0x108`, armour stigma `+0x104`, blown cooldown `+0x110`.
- **Uniforms**: an item's `Gear::isUniform` faction (`+0x1e8`, KenshiLib 1ce4a16), saved as the item's `uniform` string (the faction's id; `Gear::_serialise`). Clothing a
  CHARACTER with `wears uniform` is made with gets its faction ([economy](economy.md); Meitou `CharacterAssembly`, `ItemInstance.Uniform`).
- **Level** (`AI::vfunc_9` `FUN_1405a51e0`, **Observed**; run for the player's faction): reset to 0; a uniform hat (attach slot 3) +0.4; a uniform body armour (5) +0.4, its
  faction replacing the hat's; uniform legs (6) of that faction +0.2. The faction (`+0x108`) is the last uniform piece's. No body armour, no legs, a body of another uniform than the
  hat (or none) or legs of another uniform: the level x 0.1. So a full set is 1.0, hat and body 0.8.
- **Works** (`FUN_1407c0520`, **Verified**): level > 0.6 and cooldown <= 0; then the sense tags take the uniform faction for the character's ([sense tags](#sense-tags-who-is-an-enemy)).
- **Seeing through** (`SensoryData::disguiseMods` `FUN_140856f00`, **Observed**; literals **Verified**): per looker and frame, nothing when the looker is asleep, the target is
  farther than 400, or two flags (looker slot `0x248`, target `+0x3d4`) or target `+0x2f8` = 2 are set (**Unknown** meanings). A working disguise starts the check with m = level and
  b = 5 (two other branches: an escaped / ex-slave with m = 1 - looks-like-slave and b = 1, and an undisguised character the looker's faction has crimes on with m = 1, b = 5;
  [economy](economy.md) 11.8). Then x 0.5 when the looker's squad has a grudge on it (long tag & 0x2b0, mark 2), x 0.6 when the uniform is the looker's faction's (mark 0), x 0.5
  when the looker's faction owns it as a slave (mark 1), x 0.75 inside the walls of a town that is not public, x 2 unless the looker stands in a town (no nest) of a real faction other
  than the player's that it is not hostile to; k = lerp(stealth, 1, 2) x m x those (stealth = the raw stat / 100). a = 40 k + b feeds the spot meter of [spotting a sneaker](#spotting-a-sneaker)
  (`FUN_140856cb0` with a and 4a: T = lerp(v, 5a, a) x lerp(f, 4, 1)), the same per-looker map. Once it is full (or the cooldown already runs), and the looker is awake, not
  an ally: no crimes of the looker's faction on the target sets the cooldown to 250; else the looker fires event 44 BOUNTY_SPOTTED. The marks are 14 s entries the data panel's
  tooltip reads.
- **Cooldown**: the same field crimes set ("recently wanted", 250 from a witnessed or booked crime, [economy](economy.md) 11.2); counted down by dt (`AI::updateStateBroadcast`
  `FUN_1405a4ae0`); saved as `disguiseblown` (`FUN_140626580`, **Verified**).
- Not traced: `sellingUniformDetectionCheck` (selling a uniform, `CRIME_UNIFORM_THEFT`), event `EV_I_SEE_UNIFORM_IMPOSTER`, the dialogue gates of "regards as enemy".

Meitou (`ArrestSystem.Disguise.cs`, every spot sweep): the level and faction from the worn head, armour and legs; lookers are awake NPCs not of the player's faction within 400 that see
the character (`SightMap.Sees`), with their own meter per pair (not shared with the sneak meters, engine choice), alertness 0.5; the town tests read the town the point stands in
(engine reading of the status object's hysteresis), inside the walls by the gate regions. The slave and bounty branches stay the arrest system's (a working disguise hides the bounty
from its spotting); `AggroSystem` and the arrest spotting take `ArrestSystem.SeenFaction`.

## Hearing

Moving-state gate; ordinary noise lerp(speed fraction,350*f,600*f), stealth noise
lerp(stealth,450*f,80), f = lerp(s,1,0.25) dust / lerp(s,1,0.75) rain or acid / 1, from the mover's weather (no
protection term; **Verified**). `SensoryData::canHear` `FUN_140857500` (**Verified**), run only when sight failed, in order: both with the player: heard
within 1000, nothing else; a target neither moving nor in combat mode: not heard; another interior than the listener: only in combat mode and with one of them outdoors
(building to building never); another floor of the same building: only in combat mode, at most 1 apart, unless the building is cavernous (`+0xc2`); a per-pair
cooldown of 2 s of 1x game time divided by the speed (so 2/speed² real s; paused never) returns the last answer. Range N x0.5 when the listener's StateBroadcast
`+0xb4` (`isSleeping`, KenshiLib name **Observed**) is 0 (awake: only the sleep tasks set it, to 1 or 2 by Task_SleepOnFloor `FUN_140348170`, to 1 by
`FUN_140347950` and `FUN_140357d00`, and their exits reset it to 0, **Verified** writer scan), x0.5 for another interior and x0.5 again when the building (the target's, else the listener's) has no open door.
Chance 0.75 x (1 - d²/N²), sneaking (0.05 when `+0xb4` is 0, else 0.1) x (1 - stealth) x the same; x2 while the pair's alarm runs (5 s, set by
the forced sense `FUN_140858f40` with its alarm flag: the perceive-and-tag `FUN_1405cb0a0` of DA_MASSIVE_ALARM and AI tasks, the drop of a carried body `FUN_1405ce1e0`,
a melee victim `FUN_140999f00` and a bolt's victim `FUN_140999d50` (also `FUN_14034cd80`, `FUN_14034ffa0`, `FUN_1403552d0` and the case 0x21 of `FUN_140680560`; the dialogue attack effects 6 / 0x37 / 0x39 pass flag 0, **Verified**); counted down in `FUN_1408593d0`, **Verified**). In combat mode in the same place and on
the same floor any chance > 0 is a sure hearing; else a roll. A heard listener with `+0xb4` 1 drops it on a coin flip and, for a sneaking player target, says event 47
ALMOST_WOKE_UP. Sneaking or crawling targets get stealth XP ([character-stats](character-stats.md)). Meitou (`AggroSystem.Hears`, one test for the
attack-on-sight look and the chaser's look): the player pair (player faction only: `FUN_140856bc0`'s owner path for slaves and pets is not modelled), the moving or combat-mode
gate (a fighter standing still makes noise 350 f), the interior, floor, door and sure-hearing rules, the awake halving and the sleeper's coin flip with event 47 (asleep =
`CarrySystem.Asleep`, in a bed or resting with the sleep behaviour; 1 and 2 are not told apart), the cooldown (2 s of simulated time, game speed not applied; per-listener
list `AggroSystem.Slot.Heard`) and the alarm doubling (set through `AggroSystem.Alarm` by DA_MASSIVE_ALARM, a landed melee blow or bolt (the victim's forced sense), the stealth KO and the kidnap; the AI perceive-and-tag tasks `FUN_14034ffa0` / `FUN_1403552d0` and a dropped carried body `FUN_1405ce1e0` are not wired); the sneaking base is 0.05 for an awake listener, 0.1 for a sleeper (`Orders.SneakHearChance`).
Hearing does not imply ordinary visual cone/line-of-sight eligibility.

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
- **Decay** (`FUN_140859010`): seen and heard cleared; age = (now − time) in sense units (hours × 3600 / 33 × 0.5, 2 s at 1x); while under 8 (25 when the character is in no building,
  vfunc 0x78 null, that reading **Observed**) the place follows the character's real one; dropped past 30 units, 60 when it is in combat mode, the looker's squad holds a long tag of mask 0x232 on it or it is the
  looker's slave, 120 for a player-faction character inside the looker's home building (the looker's faction not "not real", `+0x1d0`). A character whose vfunc 0x240 holds is dropped at once.
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

**No squad runs for being broken** (**Observed**; the readers of the intact flag and the base data, 2026-10). The readers of the platoon's intact flag
(`FUN_1407ac7b0`, the cached `FUN_14079acf0`) are package can-start / finished / update functions (`RetreatAndRefresh` `FUN_1402871c0` starts when not
intact; also `FUN_1402870a0`, `FUN_1402872a0`, `FUN_140288890`, `FUN_14028ea90`, `FUN_14028eca0`, `FUN_140290450`), platoon and campaign code
(`FUN_1409c3910`, `FUN_1409c43e0`, `FUN_1409c5040`, `FUN_1409c70f0`, `FUN_1406bab60`, `FUN_1408f6d20`, `FUN_1408f7a80`, `FUN_1408f8740`, `FUN_1407ec0c0`; only
skimmed for a run order, none seen), the save (`FUN_140372ef0`) and two desires: melee `FUN_1405a4260` and shooting `FUN_140598260` are halved when not intact. None starts `Task_Runaway`
(`FUN_14034b440`) or goal 35: a healthy character's 35 stays at 0.001 against the attack's 0.05 or more
([ai-tasks](ai-tasks.md#combat-goals)). The only retreat that follows from not intact is the `RetreatAndRefresh` package (signal 5; goal 128
`RETREAT_HOMETOWN`, a walk home, not a run): in the base data (`AI_PACKAGE` `RETREAT_AND_REFRESH`) no squad template lists it. It is referenced only as the
`fallback AI package` of five `SINGLE_DIPLOMATIC_ASSAULT` records, the `retreat AI leader` (and the unread `victory AI leader`) of four `FACTION_CAMPAIGN`
records, and the `delivery AI package` / `AI contract` of five dialogue lines. So an ordinary squad (guards, shop squads, bandits, wildlife) fights on
when broken; only a campaign's retreat phase or those fallbacks walk a broken squad home (Meitou: `PackageClass.Retreat`). The FACTION run-away ratios stay unread.
Where a vanilla NPC does run (**Observed**, base data and decompile, 2026-10):
- *Crippled or armless*: goal 32 plans 35 (desire `FUN_1405a34e0`: 1.0 crippled, else a flat 0.001 when 60 holds, 0 for a player-faction one with an arm; no read of outnumbered, injury, the intact flag or the faction ratios). Meitou: `SelfPreservation`.
- *Dialogue DA 34 RUN_AWAY* (`FUN_140680560`, a target-needing effect: the branch sits under the target-resolved test, so an event with no target runs nothing). 186 records carry it, 156 reachable from a `DIALOGUE_PACKAGE`; nearly all are not fights (witnessing crimes: 43, 41; talks: 1, 3; slave escapes: 54, 55; 13). The combat-flavoured ones: 4 lines of events 5 and 66 (`Swamper Gang Bad Relations`: a stronger enemy, a squad-size and a relation condition; their `chance` fields are 0 too) and 2 get-up lines (event 24, `Get up fight bandit`: outnumbered and a stronger enemy, or a squad down). All four have `chance permanent` and `chance temporary` 0 (a roll that never passes under the line check, **Observed**), and event 24 has no target. Meitou has the effect (`DialogueSystem.RunAway`) and events 3, 66, 24; event 5 (unnamed in the editor) has no known source (**Unknown**).
- *Event 50 SQUAD_BROKEN*: `FUN_1407a5be0` (the platoon's intact refresh) fires it at each member that is not down, at the member's own attack target, on the tick the flag goes from intact to not. Base data binds it once: the `Dust bandit base` package (3 templates), root `defeated in player town- mark for raid` (taunt bubbles, no effect). Not built in Meitou.
- *Packages*: the retreat package above.
Nothing else issues `Task_Runaway` for a fighting NPC that I could find; a scan for `FUN_1405d20d0` calls with task 0x23 found only `FUN_140353c50` (a follow-type task update on a target flagged at `+0x278`, **Unknown** purpose). Meitou's `FleeSystem` squad break (everyone standing runs) is therefore off by default (`FleeSystem.SquadBreak`, **Unknown** whether the game ever shows
one); the AI's own requests (a lone crippled runner, a goal 35) run regardless. Leash/home/running state must
not be equated with the attack planner or discarded immediately after a lost sighting.
- `Task_Runaway`'s centre (`FUN_14034b440`, **Verified**): the mean position of the runner's sense entries tagged NEUTRAL or ENEMY (include 6) and not KO, bit 4 or bit 9 (exclude 0x218; `FUN_140853960` keeps an entry when `(tag & include) != 0 && (tag & exclude) == 0`), neutrals included, but an entry counts only when its status float `+0xac` (intended aggression of its current goal) is above 0; the original adds the running task's `+0xa0` when a status handle (`+0x18`, set from the target's `vfunc 0x58`) equals one of the sense record, which handle is **Unknown**, not modelled. With none counted it uses its own position. Meitou: `FleeSystem.Centre` (`TaskAggression.OfGoal`; no senses: the enemies in sight range).

### Sense tags: who is an enemy

**Observed** (decompile of the perception update `FUN_1408580d0`; bit names from the senses debug panel `FUN_1407981b0`). Each sensed
entry has a tag word (entry +0x30 → +0x30): bit 0 SENSE_ALLY, 1 SENSE_NEUTRAL, 2 SENSE_ENEMY, 3 SENSE_KO, 14 SENSE_CRAWLING; others
are set by `FUN_1408580d0` (**Verified**): 4 dead (the only bit then set), 3 down or carried (`FUN_1407d1440`, vfunc 0x30), 10 same faction as the looker, 11 the target's race is a robot (`is robot`, race `+0x7c`),
12 the looker is a slave whose owner (hand at looker `+0x328`: a character or a squad) calls the target an enemy (owner's slot 125; goal 41's filter), 13 the target's faction has a player flag (`+0x250`), 14 crawling or
staying low and not in combat mode, 15 carried (`+0x3d4`), 7 the status object's `+0xdc` (vfunc 0x70) 1 or 2, 5 / 6 slave state 1 / 2, 9 imprisoned (`+0x2f8` 2). A tag filter `FUN_140853960(m1, m2)` keeps entries with `tag & m1` non-zero and `tag & m2` zero. The combat finders read it ([ai-tasks](ai-tasks.md#combat-goals)).
- Not the looker's enemy (Character slot 125, `vfunc_125` at 0x3e8): ALLY when slot 126 (0x3f0) says ally, else NEUTRAL.
- Enemy: NEUTRAL when `FUN_140796240` holds (the target's platoon is a campaign force, [ui-input](ui-input.md); Meitou `AggroSystem` Visit), else ENEMY (unless the looker's platoon object lacks a sensor that accepts it).
- **Slot 125** (`CharacterAnimal::vfunc_125`, shared by all characters): never the same faction. Across platoons the looker's squad's
  TEMP_ENEMY tag (or, for a looker whose faction has +0x250, the target's tag on it) makes an enemy and TEMP_ALLY prevents one. A disguise
  (uniform faction at +0x108 while +0x100 > 0.6 and +0x110 <= 0) stands in for the target's faction. A slave target: not when its owner
  is the looker's, and only when the looker is hostile to the owner's faction. A slave looker: never its owner's faction. A target of the
  faction the looker's platoon object +0x80 names (**Unknown** role) is an enemy. Otherwise hostile(looker faction → target faction),
  relation <= -30, when `FUN_1408569a0` fails and the looker's +0xea flag is clear (and the target's is clear, or the looker's faction is
  not real or its default relation, relations `+0x60`, is strictly below -30; **Verified** `FUN_14079c950`). +0xea is "freed": set by a release
  (`Task_ReleasePrisoner` `FUN_1403570a0`), a paid bail (`FUN_1406ae170`) and a slave bought free (`FUN_1406add20`; **Verified**). Cleared by the status update
  `FUN_1405a4f50` (`AI::vfunc_9`) on every update while the status object's current town (`+0x88`) is null, i.e. once the character is out of every town (**Verified**; an earlier
  reading, "while the 8 bytes at `+0x110` are zero", was wrong: that function never reads `+0x110`), and by a pending crime set through the wrapper `FUN_140852e10` when the setter
  succeeds (callers: police hit, caught stealing, fencing, kidnapping seen, uniform theft, body put-down; not through the direct setter `FUN_140852c80`, e.g. attack orders and
  looting; **Verified**). Saved as the short key `tn` (`FUN_140626580`/`FUN_140626830`; the loader zeroes it first). Meitou `CharacterCold.Freed`: set by all three, not by
  a kidnap victim's release; cleared outside towns (`ArrestSystem`, by the [status town](carry-loot.md#the-status-town)) and by `SetPending(viaWrapper)` for the first four callers; saved as `tn`;
  `FUN_1408569a0` is left out. Whether the original's status `+0x88` is already set when a loaded character first updates (else a loaded flag dies at once) is **Unknown**.
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

**Observed** (decompile; goal 16's finder, [ai-tasks](ai-tasks.md#combat-goals)). A sensed character of a faction that
is not the looker's ally becomes a target only when both tests pass:
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
- **Post** (`AI::isTargetInRangeForHOLDMode_hostiles` `FUN_140598630`): only for a character on Hold (`CharStats::holdPositionMode`
  `FUN_1402b2b90`: CharStats +0x12b and not sneaking) that is not `FUN_1407f2840` (**Verified**: Ranged toggle on, posture 0, and manning a turret, or both arms working with a ranged weapon in hand or on the back; Meitou `Orders.RangedExempt`): a target slower than 2 (movement
  +0xb8), or in its own state > 1 (crawling, playing dead), or with the movement's animation-driven flag +0x37c set (every swing sets it, [combat](combat.md), "A swing has no steering"), must be within 40 of the looker; a target more than 80 from the hold point
  (CharStats +0x130, set when Hold is turned on and to a ground move's destination while on Hold) must be within 60 of the looker. The reach is 140 instead of 80 when the target is in a fight
  (`Character::isInCombatMode` `FUN_140436610`: the fight task runs, against anyone) and the dot product of its movement direction (+0xd0) with the unit vector from it to the looker is above 0 (`FUN_1409b1960`).
  All distances to the looker are 3D (`Ogre::Vector3::squaredDistance`). **Verified**: the structure and constants `DAT_141682ad0` 2.0, `DAT_1416d4a28` 1600, `DAT_141685048` 80, `DAT_1416efdd8` 140, `DAT_1416bf8d4` 3600, `DAT_141681b38` 0.
  **Observed**: the direction of the subtraction (the decompile drops the second operand of `Vector3::operator-`; looker minus target is read).
  Meitou `Orders.HoldPost` / `HoldFar`: speed is the horizontal velocity, +0xd0 is read as the yaw, the swing as `CombatSlot.AttackTech >= 0` and every distance, the hold point included, 3D (squared Vector3 distance, FUN_140598630: **Verified**; the point carries the Y of where Hold was set). Meitou has no goal 21 (aggressive neutrals), so its finder skipping the post test has no counterpart. Other stand-ins are **Observed**.
- So there is no give-up distance measured from where a fight began: an attack starts inside the AI's area, and a
  chase ends by time and lost senses ([Leaving a fight](combat.md#leaving-a-fight)).


### Summary

| Figure | Value | Label | Where |
|---|---|---|---|
| Vision range outdoors | 1500 x RACE `vision range mult` (0.5 to 2 in the data; 1 for 41 of 53 races) | **Verified** (literal) | `FUN_140851bc0` |
| Vision range inside a building | 500 (no race multiplier) | **Verified** (literal) | `FUN_140851bc0`, `FUN_140852f70` |
| Night | range x lerp(daylight, 0.5, 1): half at night | **Verified** (literals), daylight factor in [game-loop.md](game-loop.md#frame-clocks) | `FUN_140851bc0` |
| Weather (dust storm / other affecting weather) | range x lerp(s(1 - P), 1, 0.4 / 0.75), unclamped | **Verified** (decompile) | `FUN_140851bc0` |
| View cone | factor v = (cos a - 0.2) / 0.8; nothing beyond a = 78.5 degrees; a measured from the drawn facing (AnimationClass slot 0xc0 on Character +0x448: a stored +0x330 vector when non-zero, else the drawn node's direction), which lags the commanded one | **Verified** (literals; the slot reads +0x330, else node +0xd0); that +0xd0 is the drawn forward **Observed**; +0x330's writer **Unknown** | `FUN_140852f70`, `FUN_1405b22d0`; Meitou `SightMap` (`CharacterAnimation.DrawYaw`) |
| Seen when | v > 0.01 and d <= lerp(light, 0.2 or 0.4, 1) x contrast x R x v | **Observed** (control flow), constants **Verified** | `FUN_140852f70`, `FUN_140857b10` |
| Sneaking / crawling target | v x0.5 crawling; x0.75 or x0.5 sneaking; x lerp(stealth, 1, 0.6) | **Verified** (literals) | `FUN_140852f70`; Meitou `SightMap.Sees` (crawling or staying low) |
| Spot time of a sneaker | lerp(v, 0.5 + 5 s, 0.5) x lerp(f, 4, 1) s; meter > 1 = seen; forgets at dt / 260 | **Verified** (literals), flow **Observed** | `FUN_140856cb0`, `FUN_140853aa0` |
| Noise range of a mover | lerp(speed ratio, 350, 600); sneaking lerp(stealth, 450, 80); weather x0.25 / x0.75 | **Verified** (literals) | `FUN_140851d10` |
| Hearing chance per check | 0.75 x (1 - d^2/N^2); sneaking 0.1 (0.05) x (1 - stealth) x ...; x2 if recently alerted | **Verified** (disassembly and bytes) | `FUN_140857500` |
| Squad-shared sightings | a target a mate saw or heard counts as seen within 2 x R | **Observed** | `FUN_140857b10`, `GroupSense`; Meitou: the attack goals' finders read the mates' entries seen or heard at their last look within 2 R of the looker (`AggroSystem.FindFoe`); the looks themselves stay per character |
| Gather per update | every character of the character grid (the dead, the down and animals too) within R on the ground plane, R taking 500 inside town walls instead of inside a building: a value above 0 the character keeps, set from the building it is in, else stands on (a gate: its [gate code](pathfinding.md#gate-codes) at the character's position; a wall: unchanged; another building: its own); at 40 or more, everyone within 250 too. No cap: `FUN_1409ff210` ignores the limits 40 and 20 it is passed | **Verified** (literals; decompile: `FUN_1407866d0` adds the 250 lookup at 40 or more, `FUN_1409ff210` never reads its limit); who the grid holds, the ground-plane test and R's town-walls rule **Observed** | `FUN_14085a8d0`, `FUN_1407866d0`, `FUN_1409ff210`, `FUN_1409fb450`, `FUN_140792200`; Meitou caps the new characters of a look ([senses](#senses)), which the original does not |
| Update rate | with the AI, in the logic tick: 8 characters per frame in turn | **Observed** | [game-loop.md](game-loop.md#the-character-update-budget) |
| Run away (Task_Runaway) | 2000 + 400 r away from the threats' centre, then hops of 1000 + 400 r; until no enemy is sensed (state 6) and no threat attacks within 40 (state 39), 35's list A; under goal 32 until the threat level is 0 | **Verified** (literals), flow **Observed** | `FUN_14034b440`, `FUN_140336820` |
| Squad not intact (retreat package) | Never while a member is imprisoned (`+0x2f8` 2) or carried by a non-ally (`FUN_14079ab40`); else not intact without a squad leader (platoon `+0xa0`) or with no able member; 4+ members (platoon `+0xa4`): fewer than 20% (40% with the leader down; 0 with squad byte `+0x70`, writer **Unknown**) able; under 4: a persistent platoon (`+0x115`) with a home holder is intact while anyone is able, any other while the leader is up. No retreat-package exception, no enemy counted. Able: not dead and either not down, or down with the organic first-aid need (`+0x504` = Character `+0x458` medical `+0xac`, **Verified**: `FUN_140644e60` writes it, [character-stats](character-stats.md#first-aid-need-score)) under 0.8 (persistent with home) / 0.2, so a knocked-out member that is barely hurt still counts. Leader down: the generic down test (out, carried, limb-held, playing dead; not crawling). Meitou: `Ai.SquadIntact` (`Able`, `Decide`), used by `AiSystem.IsIntact`, the campaign platoon flag and the optional `FleeSystem` squad break, passing `SquadIntact.PersistentHome` (a town squad with its own building). The same rule is a platoon's intact flag (`FUN_1407ac7b0`), cached at unload: what campaigns count | **Verified** (decompile `FUN_14079acf0`, `FUN_14079ab40`, `FUN_1407a5be0`) | `FUN_14079acf0` |
| FACTION `run away ratio of squad size` / `relative to enemy` | loaded into Faction +0xc / +0x10 (base data: 92 factions 0, others 0.2 to 0.5); not read: a scan of the game logic (0x140200000-0x140a00000) for float loads at those offsets found none | **Observed** (negative) | `FUN_1407fe270` |
| A lone character running away | Goal 32 (every character's) plans row 35 when a crippled character (the medical crippled flag, Character `+0x5b8`) or one with no working arm is threatened; anyone else fights through 13/4. Desire AI slot 55 `FUN_1405a34e0` (1.0 crippled, else 0.001, player-faction veto), set out with the planner's choice in [ai-tasks](ai-tasks.md#combat-goals). Meitou: `SelfPreservation`, run by `FleeSystem` | **Verified** (slot 55 literals, flag, state 26), the planner's choice **Observed** | `FUN_1405a34e0`, `FUN_1405a4260`, `FUN_140599e90`, `FUN_140649320` |
| Attack-on-sight area / post | ROAM_FAR: 6517 around the looker; HOLD_GROUND / STAY_CLOSE: R + 40 / R + 200 + R' around the centre of movement; Hold: 80 (140) / 60 / 40 | **Verified** (literals), flow **Observed** | `FUN_140597790`, `FUN_140598630` |

## Blood smell

**Verified** (decompile: `ZoneSmell` ctor `FUN_140a25b30`, `addSmell` `FUN_1405e83a0`, `periodicUpdate` `FUN_14090cef0`, `Smell::update` `FUN_1408f4740`). Every zone has two
channels: blood (rise 1/60, decay 1/240 per second) and eggs (rise 1/240, decay 1/60). Sources post each periodic update: 1 a corpse with flies, 2 a bleeding character (bleed rate
non-zero, body state 0), 3 eggs. The zone update (`FUN_14090cef0`, **Verified** by disassembly) accumulates only kinds 1 and 3, so bleeders count for nothing: blood target =
corpses (the 0.25 bleeder share multiplies a count that stays 0); position = the corpses' centroid, else the zero vector (a hunter still on such a zone runs to the world origin
until the value falls below its threshold). A zone record is freed when its zone is not loaded and both channels' targets are 0 (`FUN_1408f85f0`). The value moves toward the target at the rates, clamped. Corpse flies: [character-stats](character-stats.md#corpses).
176 HUNTING_BLOODSMELL (`AI::scoreHuntingBlood` `FUN_140794b60`, `Task_HuntingSmell`): animals only; the blackboard keeps a zone (`+0xf8`) while its blood is at least the animal's
Cadence (`FUN_1408f85f0`, **Verified**): a timer collects the speed-scaled frame time until 10 s; each kick then steps ONE zone record (cursor `DAT_1421352f8`) with dt = the frozen timer (10 s plus the overshoot), and when the cursor passes the last record timer and cursor reset. So a zone steps about once per cycle with dt >= 10 game seconds, in lumps of 10 s x rate, and the posted-entry map is cleared after each step. Meitou (`SmellSystem`): all zones step together every 10 game seconds with the time gathered since the last step.
ANIMAL_CHARACTER `smell blood` (`smell eggs` is the eggs threshold); else the best of its zone and its active island's, x0.8 for a neighbour, x0.5 otherwise, above the threshold;
desire 1 with one. The task runs (movement mode 2) to the smell's position; no end of its own. Data: "Smell Hunter" in the two Blood Hunter packages. SQUAD_TEMPLATE `blood smell mult`
is read into the platoon's patrol settings; its consumer was not traced (**Unknown**).

## Unknowns

- Slot 14 `+0xb4`, slot 60's `+0xd5`, the hearing re-roll factor.
- The update cadence of the squad-intact test; what Character vfunc 0x240 (an entry dropped at once) and tag bit 8 (cleared when seen) are. (Character `+0x5b8` is the medical crippled flag, [combat](combat.md#crippled-flag).)
- The writer of a character's location handle `+0x80`; when can-see casts its physics ray; the building rules of can-hear;
  the alertness goal float and the +0x3f0 (green pointer) test of spotting.

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 4. Can see: the order of tests (`FUN_140857b10`)
