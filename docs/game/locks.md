# Locks, doors, cages, beds and shackles

Original-game rules for lockable objects and the tasks that use them. Task tables: [ai-tasks](ai-tasks.md);
carrying into beds/cages: [carry-loot](carry-loot.md); crime amounts and bounties: [economy](economy.md#crimes-bounties-and-prisoners);
lockpicking stat and XP core: [character-stats](character-stats.md). As built: [simulation](../simulation.md#cages-beds-and-locks). Function addresses are kenshi_x64.exe 1.0.68.

## Data

| Object | How it is recognised | Fields |
| --- | --- | --- |
| Cage | BUILDING `functionality` -> BUILDING_FUNCTIONALITY `function` 8 (BF_CAGE) | Base data: `Prisoner Cage` (hardness 50), `Spiked Cage` (25) use functionality `Cage`; `Prisoner Pole` (30) uses `Cage- prisoner post`; rebirth.mod `Peeler Machine` uses `skin peeler` (also 8) |
| Restraint (pole, peeler) | a cage whose functionality has `restrains movement` true | `Cage- prisoner post`, `skin peeler` |
| Bed | `function` 6 (BF_BED), 25 (BF_SKELETON_BED) | `Bed`, `Old Bed` (`sleeping`), `Camp Bed`, `Fish Bed` (`sleeping on floor`), `Skeleton Repair Bed` |
| Door | a BUILDING listed in another building's `doors` list (35 base records; [pathfinding](pathfinding.md) for `is door`) | `door type` (DoorMaterial: sound only), `always locks`, `lock level bonus`; motion fields `door axis/move dist/move speed/comes out` |
| Lockable container | BUILDING `has lock` true (all 10 also `has inventory`) | `Safe` (bonus 25, hardness 90), `Metal Chest` (10, 60), `Ancient Safe` (10, 60), `Secure Weapon Cabinet`, `Crossbow Locker` (15, 60), `Weapon Display` (10, 30), `Bandit Loot Store`, `Weapon Cabinet` (0, 30), `Secure Wooden Chest`, `Metal Box` (-15, 15) |
| Shackles | ARMOUR `is locked` true | `Prisoner Shackles` (`SHACKLES`), `Hive Prisoner Shackles` (`SHACKLES-BODY`): lock level 20, hardness 20; `Faulty Prisoner Shackles`: 5, 10 |

Probe over the merged base data (gamedata.base, Newwworld, Dialogue, rebirth): 603 BUILDING, 103 FACTION (**Verified**).

- BUILDING_FUNCTIONALITY `occupant selection` (beds, cages): selecting the building selects its occupant (editor text). Cage
  animations: `Cage` idle `stand 1` / `sitting idle`, dazed `sitting dazed`, KO `foetal`; the post uses `stand pole` for all slots
  including `animation lockpick`. Beds: `sleepinbed`. `use range` 25 cages, 15 beds (**Verified** data).
- FACTION `doors lock level` (default 40), `lock level random` (default 10): 78 factions at 40/10; others 20..80
  (Shinobi Thieves 80, Nameless 75, Traders Guild / Slave Traders / Skeleton Legion 55, Holy Nation 25) (**Verified** data).
- `cages lock level` and `containers lock level` exist in fcs.def and the data but are **never read**: the strings are absent
  from kenshi_x64.exe; only `doors lock level` and `lock level random` occur (**Verified**: byte search of the exe). Every
  building lock uses the doors value.
- SQUAD_TEMPLATE `initial door state` (DoorState CLOSED 0, OPEN 1, LOCKED 2, BROKEN 3): 959 templates, 935 CLOSED; LOCKED 18
  (labs, outposts, HQs, gate guards), BROKEN 4, OPEN 4 (**Verified** data). `public day` is read into the home building
  (`FUN_14057f1f0`, building +0xc8, saved as `is public day`); **`public night` is never read** (string absent from the exe,
  **Verified**).

## Lock object

One structure (0x28 bytes) serves doors, lockable buildings, cages and shackles (**Verified**: the same constructor
fields in the door constructor `FUN_14029fb40`, the functional-building constructor `FUN_1402a0900` and `FUN_14029a160`):

| Offset | Meaning |
| --- | --- |
| +0 | level, int, clamped to 0..100 |
| +4 | hardness, float (cut/break resistance) |
| +8 | owning object (building or item) |
| +0x10 / +0x14 | retry interval bounds, 5 and 20 s |
| +0x18 | pick time accumulator |
| +0x1c | time of the next pick roll, starts at 5 s |
| +0x20 | locked flag |
| +0x21 | broken flag |

**Effectively locked** = locked flag set, broken flag clear, owner not broken (owner virtual +0x308, slot 97: a machine's `broken` +0x3b6, a door's
+0x3ec, 0 for plain buildings) and level >= 1 (`FUN_140297d60` and every consumer; **Verified**). A broken or level-0 lock is open whatever its flag.
Meitou: `LockState.EffectivelyLocked` with `OwnerBroken` (a player cage or safe being dismantled, whose fixture stays registered while its machine is broken); doors test `DoorBroken` beside it.

Creation (**Verified**, `FUN_14029d860`): level = FACTION `doors lock level` + uniform int in [-r, r] (r = `lock level random`)

A player-built cage or safe goes through the same constructor (`FUN_1402a0900`, faction from the building's owner slot), so its lock rolls from the player's faction
(`204-gamedata.base`, Nameless: `doors lock level` 75 ± its `lock level random`, plus e.g. the Safe's bonus 25) and starts unlocked (**Verified** by decompile: the constructor
has no player case). Meitou
(`FixtureRegistry.RegisterPlayerBuilding`): a fixture `<site>` with that roll once the building is finished (engine: doors likewise), a cage then working as a town one; a safe's
fixture has no grid (its items are the machine's storage), so it gets no container verbs. Saved with the building's own lock keys (below).

## Lockpicking (Task_PickLock 76, Task_PickLock_Shackles 201)

- **Chance** (`FUN_140297dc0`, **Verified** by disassembly): with `L` = lock level and `s` = effective lockpicking skill,
  `p = 0.9` if `s >= L`, else `p = 0.9 / 2^((L - s) / 10)`. A character imprisoned in a cage (`Character+0x2f8 == 2`) uses
  `s - 10` for the roll and the shown chance; the skill condition does not subtract it (**Verified**).
- **Allowed** (conditions 166 `iHaveSufficientLockSkill`, 167 `...ForShackles`; `FUN_1408ca230`, `FUN_1408ca120`):
  `p > minimum lockpick chance x 0.01` (CONSTANTS 5 -> 0.05; strict). Otherwise "I'm not skilled enough..." (**Verified**).
  At 0.05 the cutoff is `L - s > 41.7`.
- **Animation** (**Verified** `FUN_140520bc0`, `FUN_140359160`:86-90): every update the picker plays the target cage functionality's `animation lockpick`, else `medic` (`crawl use` when crawling), at speed `0.55 + 1.45 x 0.01 x skill` (the lerp `FUN_140015b63`, lo 0.55, hi 2.0; not a clamp). The speed is cosmetic: it does not change the roll timing. Meitou: `CharacterCold.PickWork` (set by `LockSystem` each picking tick), `AnimationSystem.HeldClip`, `CharacterAnimation.HeldRate`; a prisoner picking its own cage plays it in place of the caged pose (the base `Cage` names no `animation lockpick`, so `medic`; only `Cage- prisoner post` names one, `stand pole`).
- **XP** (`FUN_1408c62e0`, **Verified**): after each roll, stat 37 gains `k x map[37] x ((101 - stat) / 101)^2` with
  `k = 1.2` on success, 0.24 on failure. The lock level is passed but unused.
- **Target** (`Task_PickLock` slot 5): a building handle (a door resolves to its lock-owning object) or a **character**, meaning
  that character's cage (only when they are in a building prison). Shackles: the item handle, or a character, meaning their
  worn shackles. Self-targets are allowed: the caged character picks its own cage, a shackled one its own shackles (both
  task rows carry the -10 penalty when caged). Task list B also needs `hasOneWorkingArm` and no weapon equipped.
- **Success side effects**: the picker's own squad's "house locked" flag is cleared when the door is of its own home (**Observed**, `FUN_140359160`: a stranger's pick leaves the owners' flag set); a freed character gets relation event 0xd from
  the picker (0xe on every failed roll, spoken by the captive or the shackle wearer, no player-only test; **Verified**, `FUN_140359160` event code defaults to 0xe, 0xd only on success). Event 13's memory side effect does not run for 14.

## Cutting and breaking (285 / 185 cut, 286 / 186 break)

Same targets as picking; Task_CutLock / Task_BruteForceLock share the shackle tasks' update (**Verified** vtables).

- **Cut** (`Task_CutShackles` slot 2, **Observed**): needs `hasTools` (state 122). Each update: hardness -=
  `clamp(0.01 x a x b, 0.05, 0.2) x dt x 2`, where a is the stat-based multiplier `FUN_1406457e0(stats, 1, 1)` (**Verified**, `FUN_140359f80`: the hunger term only, no limb, light or damage terms) and b the strength stat; the break test below uses every term (`FUN_140338090`). Meitou: `Lockpicking.CutStrength`. At <= 0 the
  best tool is consumed (a tool: ITEM `item function` 9, e.g. Hacksaw; `hasTools` `FUN_1405989e0` tests item `+0x124 == 9`, **Verified**) (aborts if none), the lock's broken flag is set, "Shackles broken!" (also for locks).
  The bar's caption is "Cutting: {1,num}" with the hardness x 10 as a whole number, set each update like the pick's (`FUN_1406e4910`, **Verified** `FUN_140359f80`; no fill is set there). Meitou: `World.CutBar`, the fill empty, for locks and shackles (#656: a safe of hardness 60 takes 270 s at strength 11 and showed nothing). Same crime rule as picking (2 for own-target / buildings, 7 for another character).
- **Break** (`Task_BruteForceShackles` slot 2, **Observed**): instant; succeeds when effective strength x stat scale > 3 x hardness,
  else "I'm not strong enough to do that". Sets broken and the hardness to 0, plays Character_Unlock_Success, logs "Shackles broken!" and floats "*Crunck*" (both, **Verified** `FUN_140352c50`), and fires event 13 UNLOCK_MY_CAGE_OR_SHACKLES at the freed character (a cage's occupant, the shackles' wearer) from the breaker (`LockSystem.BruteForced`).
- **Bash door** (81/226/287, `Task_BashDoor`, update `FUN_140348c10`, **Observed**): needs a weapon (animals use their natural
  one); humans play `stonemine`, the blow lands when the clip's progress crosses 0.6 (**Verified** `FUN_140348c10`: it compares the named clip's progress with the previous update's, which the task start sets to 1, so the first blow is 0.6 into the first cycle, then one per cycle; an animal plays another clip: `FUN_140887420` returns the first attack technique in data order (loader `FUN_140888ca0`: not `disabled`, no block or dodge) whose per-category flag for the animal's weapon category is set, played at speed 1; the blow is at 0.6 of that clip's progress as for a human and the clip restarts once its progress reads past 0.98 (double `0x1416c0b90`) - **Verified**; Meitou `CombatSystem.AnimalBashSeconds`, the clip length over `anim speed mult` is **Observed** reading). Each update books Burglary (2) against the owner. A blow
  (door vfunc 62, `FUN_14029d690`): the attacker's damage packet, x0.25 for an animal (vfunc 0x248), then
  `f = clamp(2 x blunt + 0.5 x cut, 20, 60)`; 0.015 x f comes off the door's own construction progress (`getBuildState`, slot 69, returns
  the door's Building +0x160; `constructionProgress` +4 = the Building's progress `+0x164` in material units, see Repair); the lock's hardness is not touched
  (Meitou: `Fixture.DoorProgress`, `LockSystem.Damage`, a fraction of 1 that a blow takes 0.015 f off as if the door's target were 1, so a whole door takes 2 to 4 blows: an engine reading, see Repair; not among the door save keys, so not saved; a template's BROKEN door starts at 0, engine reading). At <= 0 the door is broken, its lock
  unlocked and the door opened, sound `Break`. The task ends once the door is broken, open or opening; no timer (**Verified**, `FUN_140348c10`: the door's vfunc `0x3e0`
  `FUN_1402adcc0` returns the door itself, so the "lock owner" tests are the door's own broken flag `+0x3ec` and state `+0x380` 1 or 2; the lock object's locked and broken flags are never read,
  so a closed unlocked door, or one whose lock was cut, is struck on). The Burglary booking comes on every update that passes the in-the-way test, before the blow test.
  Meitou: every basher (the player's Smash order, a planner NPC, the pursuit's) books Burglary each update in reach against a door owner other than its own faction (**Verified**, `FUN_140348c10`, shared by rows 81/123/226/272/287/290); an NPC basher still drops its plan once the door is no longer locked (its planner replans).
  - **Who** (**Verified** scans): only the planner makes 81 and 287; 226 is the player's Smash order (not a provider, desire 1). Row 226's own list B is `!70 !128 5 57` (**Verified**, row table): the order is refused on a door of the smasher's faction or of an ally's (`LockSystem.SmashRefused`); the context menu already offers Smash only on doors not owned by the player's faction.
  - **When** (**Verified**, evaluator `FUN_140597030` is one function for state 59 on every goal: `FUN_1405499a0`, a door broken, open or opening): a row needing DESTINATION_IS_ACCESSIBLE (59; every walk to a target, attacks, entering) finds the target's building without an open
    door (`Building::hasAnOpenDoor`: none, broken, open or opening; a closed unlocked door counts as shut). 85 Open destination door (finder: that building)
    wants DOOR_IS_OPEN; its providers, in ascending type, are 72 Opening door, 81 and 287. Locked: 72 needs !54, which 78 (own door only) or 81 could give, and
    81 (B: not mine, not allied, a weapon, at the door) wins, walked to by 82. 287 is the break-out twin (LOCATION_IS_ACCESSIBLE 60 → 84 → 74 → !55).
  - **Door and place**: the building's one door (`getDoor`); at it within the door's reach of its marker (25, 45 for a gateway; x2 standing), height 130.
  - Meitou ([simulation](../simulation.md#cages-beds-and-locks)): `DoorPlan` runs this search for NPC attackers whose target is shut in.
  - **Gates** (123/290 Bashing gate, [table](ai-tasks.md)). Every walk row (1, 29, 67, 82, 163, 199) needs
    `DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS` (90; **Verified**, registered by name in the condition table `FUN_140610250`, evaluator
    `FUN_140597160` via `FUN_1405a28a0`): true when the walker's gate code (`amInsideTownWalls`, slot 0x118) equals the destination's, or
    either is -1; otherwise true only when the gate state between the two codes (`FUN_1402eb070`) is 1 (open or broken), or the target is a
    building whose own code links to the walker's code with state 1 (**Verified**, `FUN_140597160`: only when the two codes differ and neither is -1; for a destination of type 0, a building: a gate building with a door tests the walker's code against the door's gate code, `+0x378`,
    through the state map and passes on state 1; any other building passes when its own object's gate code, at its position, equals the walker's; then the pair test above). Meitou: `LockSystem.NotBlocked` compares the codes of two positions;
    the planner's building destinations pass the door point (`DoorPlan`), which is that second test, and no gate building is a destination, so no overload is added. Providers of 90: 122 Open gate (needs !54 unlocked) and 270 Assaulting
    town; 123/290 give !54 and 56 at a gate (91, walked to by 121). So raiders breaking a locked gate is the planner's chain on gate codes
    (the chain itself is an inference from the table, as for 81; the user reports it in play). Codes and states: [gate codes](pathfinding.md#gate-codes).
    122 and 270 are restricted rows, offered to every NPC plan (**Verified**, `FUN_14051bae0`: the acting character's faction is not the player's, [planner](ai-tasks.md#planner)), so raiders plan the gate chain. Meitou: [simulation](../simulation.md#cages-beds-and-locks) ("Bashing gates").
    270 (Assaulting town, effect state 163 `isBuildingDestroyed`, provided by 272 Smashing, which shares 123's desire function `LAB_14000dc29`; its finder `LAB_14001bcca` is an unresolved thunk, so the link to `FUN_1405a3f60` is **Observed** by shape only): with a nonzero place (Character vfunc `0x78`) it first returns the route gate as 122's finder `FUN_1405a28e0` does, so 270 -> 272 bashes the same gate door (`FUN_140348c10` resolves a hand to a building's door, else bashes the object itself); a target that is a building in the looker's gate region is kept; only when no gate is found does it scan the place's wall buildings (function mask `0x100000`, sorted by distance) for the first whose gate code is the looker's (`FUN_1405b0eb0`). The gate codes are flooded from gates only ([gate codes](pathfinding.md#gate-codes)), so for two different known codes a gate route normally exists and the wall scan is **Observed** to be a rare fallback. Meitou (`GatePlan`, `LockSystem.Smash`): 270's finder gives the gate first (as 122), else the nearest non-destroyed wall site of the attacker's own gate code (`LockSystem.NearestWall`); state 163 holds when the wall line is destroyed, 46 (`isWithinChoppingRange`: the wall's reach range 60 of its footprint, the height test of `SiteReach`) is provided only by 67 Running whose list B needs 90 again, so a repeat of 270: **the plan exists only for an attacker already within reach of the wall**. No gate leads to the target (`RouteState` gives no gate) -> the wall fallback (s01-f25). Smashing (272, s01-f26): `Task_BashDoor` on a hand that is no door damages the object itself; Meitou walks to the wall's reach, faces it and strikes every `BashSwingSeconds` with the attacker's packet (x0.25 for animals) through the wall formula shared with the fight's blows (`CombatSystem.StrikeWall`: clamp((2 blunt + 0.5 cut) / 2, 10, 60) x 0.003 of the wall line's progress, destroyed at 0). No crime is booked (only doors book Burglary). `CharacterCold.BashWall` marks that `Bashing` names a wall site.
    123 / 290 also list `!158` (`iAmBeingMeleeAttacked immediate`, Character `+0x2b0`, **Verified**: thunk `140042866` returns that byte; [threats](combat.md#fight-lifecycle-and-orders)), so a basher with a melee threat in reach does not plan the gate bash: `GatePlan` reads `CombatSystem.ImmediateThreat`.

## Doors

- Door object fields (**Verified**: save writer `DoorStuff` slot 148 / reader 149): `state` (+0x380: 0 closed, 1 open,
  2 opening, 3 closing; this is not the DoorState enum), `doa` open amount (+0x37c, 0..1), `broken` (+0x3ec), `gatecode`, `wants lock`
  (+0x384), and the lock (`lock lvl`, `locked`, `broken`, `hardness`). `wants lock` starts from `always locks` (gates).
- **Destroyed buildings have no doors** ([pathfinding](pathfinding.md) "Destroyed interiors", `Building::createPhysical`) and no lock: **Verified** by census, the
  level data's states of destroyed buildings (both Hub `Defensive Gate IV`, `4400-`/`4617-Newwworld-INGAME`) and two original Hub saves carry no
  door or lock keys for them (those saves write them only for the Hub's intact Bar and Residential Small). Meitou registers no door or own lock
  for them; their interior fixtures stay.
- **Door panel** (player's building, the HUD lines of a selected house: [ui-screens](ui-screens.md#selected-building)): open/close button `FUN_140546fb0`: an open door closes; otherwise an effectively locked
  one is unlocked first, then opened (**Verified**). Meitou: a door still moving counts by where it heads and turns back (an opening one closes, a
  closing one reopens); the original's open/close act only from closed / open, so a slow gate ignores the button for seconds.
- **Player lock toggle** (`FUN_140547060`, **Verified**): flips `wants lock` (sound `Lock_Building` / `Unlock_Building`); locking a
  closed door sets the lock at once, unlocking clears it; an open door locks when it next closes (the update above).
- **Owner lock/unlock** (77/79/162/198, 78/80/140; B list `isObjectMine`): progress 0.3 per game second, so 3.3 s; ends with
  `Character_Lock` / `Character_Unlock_Success`. Locking closes the door first. No skill or level involved (**Verified**). Task_LockDoor update `FUN_140337670` (**Verified**): work += dt x 0.3; on reaching 1 it starts the door closing (`FUN_140298c50`,
  only from open), sets the lock's locked byte at once (`wants lock` is not touched), plays `Character_Lock` and sets the squad's house flag. The context menu's own-door verbs are these two orders: Lock 77 on an unlocked own door, Unlock 140 (0x8c) on a locked own door (`FUN_1407a6bc0` door branch, **Verified**); an own cage gets Lock 77 / Unlock 78 (cage branch, **Verified**; the update has no door to close there; Meitou runs the cage's unlock as row 140, same conditions); the panel's buttons are the instant toggle below.
  Row 77 (lock) lists `!DOOR_IS_BROKEN` in list B, so it is never planned on a smashed door (rows 79/162/198 have none; a lock on a broken door is not shown). The close acts only on an open, whole door (a closing, opening or smashed door is left as it is).
  Meitou: `DoorLockOrder` (nearest selected character walks to the door's work spot, `LockSystem.DoorLockJobs`); the job (`CharacterCold.DoorJob`) is hashed while set and not saved. The lock-up round (`LockSystem.Rounds`) does the same: `FixtureRegistry.CloseDoor` then the lock at once, smashed doors left out of the round (engine choice for 198: the data does not exclude them).
- **Residents** (`allDoorsLocked` 87, `FUN_140596bd0`): true when the character is in bed, or its squad's "house locked" flag
  is set (squad AI object +0xe8, saved `homelok`; also read by dialogue condition 11 `BUILDING_IS_CLOSED_AND_SECURED`, **Verified** `FUN_140676950` case 0xb; set by Task_LockDoor at each door it locks whose lock is the worker's faction's, and Task_CloseDoor (a door with a working lock), when no door of the home is open or opening (`FUN_1407933a0`, **Verified**: one locked door is enough); cleared by unlocking, picking or a
  non-player opening a door (`Task_OpenDoor` update `FUN_1403370f0`, **Verified**: every update with the door unlocked, unless the opener's faction is the player's; Meitou `LockSystem.NoteDoorOpened`, from the walk-through opening)), else when the home building has no door that is open, broken or effectively unlocked
  (`FUN_1408cb1c0`, which also returns that door as the next target). Base AI_PACKAGE hours (**Verified** data):

| Package | Task | Hours |
| --- | --- | --- |
| Shop (leader and squad) | 155 Shop Lock all the doors | 0-6 |
| Civilian (leader) | 155 | 20-8 |
| addon- lockup and sleep at night | 155 | 22-6 |
| Shop catun | 155 | 23-7 (leader), 23-6 (squad) |
| Storage Guard, Slaver Storage Guard | 197 Lock all the doors - stay outside | 23-7 |
| Cage Warden (and copy) | 195 Lock all my cages | 22-24 (leader), 19-21 (squad) |

- **Opening up** (86 `OPEN_UP_SHOP_DOORS`, Task_Blank, finder `FUN_1408cb360`, **Verified** decompile): the building the character stands in when
  its owner matches the AI's (an owner test, **Observed**), else its home; the target is the first door that is closed and not opening (the plan unlocks, then opens it). With none left
  the finder clears the squad's "house locked" flag. A = !87, so it scores only while the home counts as locked. Only squads whose packages list
  86 open up (base data: Civilian, Shop, Shop-24hr, Slaver and Storage Guard leaders, among others); the rest stay locked.
  Meitou (`LockSystem.LockUpSweep`): the opener's round starts while the home counts as locked by either branch of 87 (the round's flag, or `FixtureRegistry.AllDoorsLocked`). The worker approaches each door from its nearer side, unlocks it and starts the opening motion. **Observed** (headless, #293): the three `stolen slave clothing shop` Swamp Shacks of Slave Markets (Newwworld level data) start `locked0` true with `initial door state` 0 (CLOSED) on the template; the level state wins, so with only round-locked homes opened the shopkeeper outside could not get in, its path ended 29 units from its node behind the back wall (within the goal snap 30) and it stood there all day.

## Cages and beds

- **Entering** (`FUN_140330600` with a building, from Task_UseCage / Task_PutInSomething; **Verified**): `Character+0x2f8 = 2`,
  the prison handle at +0x300 (handle type +0x308 = 0 for a building), the character is moved to the cage position and placed
  at its node; AI movement flags 0x10 when `restrains movement` (pole), else 4. The cage's lock is set **locked** when the
  cage has one (all cages do). A prisoner with an owner handle (`+0x330` not 11: a slave, escaped slave or ex-slave) becomes the cage owner's slave (owner
  `FUN_14032e430`, state 1) when the cage's building owner resolves and the cage's building (its parent's, `FUN_1400d5fa0`) designation `+0xc4` is not 7, the police
  station (**Observed** by decompile; Meitou `FixtureRegistry.TakesSlave`). **Observed**: prisoners with a bounty for the cage owner get a sentence ([economy](economy.md#115-bail-and-sentence)).
  Unique characters are recorded separately.
- **Locked cage**: the occupant cannot leave; `Get out of cage` (133, 207) needs `!isDoorLocked`; tooltip shows the lock level
  or "The cage you are in is UNLOCKED. Nobody knows this but you" (`FUN_1405d4260`).
- **Leaving** (`Task_GetOutOfCage`, `FUN_1403554b0`, **Verified**): restores AI flags and pose. The task's constructor flag (`+0x80`; 1 for 207 and 256, 0 for 133, the task factory `FUN_14032ebd0` and `FUN_140335200`) is copied to the prisoner's
  `+0x1a0 -> +0xe8` (Meitou `Escaped`) only when the cage owner faction is real (`+0x1d0` clear); for a `not real` owner nothing is written. A nonzero flag also turns a slave in
  state 1 into state 2 and sets crime 0xc Escaping Prison against the cage owner (the setter refuses a `not real` or `fundamental type` 8 victim), after the hold is cleared (`FUN_140330600`), since the setter
  refuses an imprisoned culprit. There is no sentence test: a prisoner whose sentence has run out who leaves through 207 or 256 still escapes. 133 (flag 0) is added by `Task_ReleasePrisoner` only for a releaser Meitou (`LockSystem.OpportunisticEscapes`): an AI prisoner whose role list holds 256, 207 or 202 in its hours walks out of an unlocked cage.
  that books bounties or is the player's. Lifting a prisoner out of another faction's cage (`FUN_14034fbe0`): the lifter gets code 7; the prisoner gets the escaped flag and a code 0xc call made while it is still
  held (`+0x2f8` = 2), which the setter refuses ([carry-loot](carry-loot.md)), so no pending crime (**Verified**).
- **Escaped flag** (`StateBroadcastData::isEscapedPrisoner` `+0xe8`, KenshiLib 1ce4a16; **Verified** by decompile: every write through `+0x1a0`). Set only by `FUN_14034fbe0`
  (lifted out) and `Task_GetOutOfCage` flag 1 (above). Cleared only by: being caged again (`FUN_140330600`'s put-in branch); `Task_GetOutOfCage`
  flag 0 (133, a release); `ActivePlatoon::periodicUpdate` (`FUN_1404ff060`, vtable slot +0x40): a squad split from another (`Platoon::isSeparatedSquad` `+0xb8`) whose parent
  has an active platoon within squared distance 1.0404e7 (~3226 units) moves each member that is in nothing (`+0x2f8` = 0), not slave state 1, not carried (`+0x3d4`) and not down (vfunc +0x30) into the parent, and
  clears its escaped and kidnapped (`+0xe9`) flags ([the merge](factions-squads-towns.md), Meitou `PopulationSystem.RejoinSeparated`); the status update `FUN_1405a4f50` (**Verified**; an array-form write, `param_1[0x3a]`, which `field_writes.txt` does not index):
  cleared on each update while the status town (`+0x88`, [rule](carry-loot.md#the-status-town)) is null, as the freed flag `+0xea` is. So "Leave town to clear this status"
  ([ui-screens](ui-screens.md)) is true; no timer clears it, the 99999 hrs is text only. Meitou: `ArrestSystem.DropTownStatusOutsideTowns`. (The other `+0xe8` writes in `FUN_1408cb360` / `FUN_1407933a0` are the Blackboard's `homeIsLockedUp`.)
- **Beds**: `+0x2f8 = 1`, bed handle +0x300 ([carry-loot](carry-loot.md)); beds have no lock. Details below.
- Character save keys: `in something` (= +0x2f8) and `in what` (handle +0x300) (**Verified**, `FUN_1406280a0`); platoons
  save `imprisoned` (`FUN_140372ef0`).

### Beds

**Verified** (Stoat placements, zone 39.16): the outdoor Camp Beds are standalone buildings with BF_BED and `sleeponfloor`.
Meitou registers them as outdoor fixtures using their level placement and bed marker, alongside beds from interior layouts.

**Observed** (decompiled, kenshi_x64.exe 1.0.68) unless marked. Data: every bed has `max operators` 1 and `use range` 15; the clip is the functionality's
`animation` (`sleepinbed` for Bed, Old Bed and the Skeleton Repair Bed, `sleeponfloor` for Camp and Fish Bed) (**Verified**, data probe).
- **Enter / leave** (`FUN_14032e2f0(character, enter, bed)`): entering sets `+0x2f8 = 1` and the handle, and adds the character to the bed's occupant
  set (UseableStuff slot 159 `FUN_1400f8030`, refused once the set holds `max operators`); leaving (`FUN_140345620`) removes it and clears both.
  "Free for X" (slot 158 `FUN_1400f7bf0`): X is in the set or the set is below `max operators`.
- **Task_GetOutOfBed** (116, 208, 257; `FUN_140345760`, `FUN_140348480`): `Get_Out` sound; when the bed's clip is `gotobed` it is played backwards and
  the character leaves at 10 %, else at once.
- **Rent cost** (UseableStuff slot 152 `FUN_140330ab0(bed, character)`): 0 in this order: no bed faction; the faction's `not real` flag
  (+0x1d0; the FACTION loader `FUN_1407fe270` reads `not real` into it, **Verified**); ally(bed faction, character's) ([factions](factions-squads-towns.md) 3.3); a
  `fundamental type` 8 (bandit) owner; no town for the building (`FUN_1400f6c20` takes the root building, `FUN_1409f9100` the town nearest its position); a town of type
  (+0xd8, **Observed**) 0 Nest, 4 Ruins or 9 PointOfInterest. Meitou uses the placed town of the bed's site (none for a player-built bed). -1 when `canIUseThisBuilding` (below; Meitou `BedSystem.MayUse`: hostile owner, or a
  building not public, without the squad-town clauses) fails or the house has no
  resident squad, or its template's `public beds` is false, unless the building's designation (+0xc4, set from the `building designation` of the
  first squad to take it as home: `FUN_140553210`, `FUN_14057f1f0`) is 3 BD_BAR (the house record below); else the template's `bed usage cost`,
  x4 for a BF_SKELETON_BED (25), negative read as 0. Data (**Verified**): 928 templates `public beds` false, 31 true (bars and shops, 0 to 750 cats);
  40 have designation 3, 20 of them with `public beds` false (Shop bar holy / old / black desert, Smugglers Bar, bakeries, bounty hunters, 100 cats).
- **Payment** (`FUN_1403576c0`, player faction only): -1 → the character says "This isn't mine", refused; > 0 → free when this bed was paid less than
  24 (game hours, **Observed**) ago (a global map keyed by the name string at `+0x58` of the third argument, the empty string when null, storing the game time as a float; never saved, **Verified** by references, [save](../formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)), else "Can't afford c.{1} for bed rental." and refused, or paid with
  "Paid c.{1} for bed rental.".
- **Bed search** (`FUN_1408cfe00`, target finder of 153 Going to bed (**Verified**: its registration in `FUN_140319a60` stores the thunk at row +0x160)
  and desire of 148): a character in a bed keeps it. For a player-faction character: the complete buildings of function mask 0x40 (BF_BED only; skeleton
  beds are not searched) in its town, else the nearest town within √1.5625e7 ≈ 3953, plus its home building's town (`FUN_1408cbea0`, the same gatherer as the cage finder and the kill/eat finder: [economy 11.7](economy.md#117-arrest)); each free one not in
  the squad's give-up list weighs 0.5 for the character's faction, 0.01 for a faction that counts it as an ally, else is skipped; x3 when its
  construction needs more than 1 material (Building+0x188), x2 with a parent building, x4 inside town walls (vfunc 0x118 `amInsideTownWalls` > 0,
  KenshiLib 1ce4a16; factor `DAT_14168bf24` = 4.0, **Verified**; Meitou: the gate code at the bed's position above 0, none where the codes are unknown). The heaviest wins (first on a tie, `FUN_1408daa90`). The town's beds are first filtered by the squad's
  `canIUseThisBuilding` (below; the gatherer `FUN_140a00690`). So an **allied** faction's bed (relation to the player >= 50, e.g. the Shinobi Thieves after
  joining, +100) is taken when no own bed is free; a merely neutral faction's never is (**Verified** by decompile, `FUN_1408cfe00`, `FUN_1406b2630`).
- **UI**: a quick right click on a bed (function 6 or 25) gives 258 (UseableStuff slot 131 `FUN_140299230`), sent to every selected character (`FUN_1407f9e20`, **Verified**: no gate for 258); the first to arrive takes the bed, the others are refused at it; the held menu (`FUN_1407a6bc0`) offers
  99 "Put in Bed" when the last selected player character carries someone, else 258 "Sleep" (verb names `FUN_14079b2d0`).
- Meitou: [simulation](../simulation.md#cages-beds-and-locks).

### Building record

The house keeps, from the squads housed in it (**Verified**, decompile): designation `+0xc4`, public byte `+0xc8` and the squad template `+0xf0`. `FUN_14057f1f0` (house setup for a template) writes the template's `building designation` only while the house has none and its `public day` every time (the last setup wins); `FUN_14057f4c0` (a platoon made at the building) writes the designation over any value, a 0 included; `FUN_140553210` (a squad assigned) stores its template every time and the designation while none is set; `FUN_140792570` (a saved platoon's home handle at load) writes the designation while none is set. Readers: the bar, shop and public tests of the town places, `FUN_140330ab0` (bed cost: `public beds` and `bed usage cost` of the stored template, the stored designation for the bar exception). Meitou: `FixtureRegistry.RecordOf` (`BuildingRecord`), written by `PopulationSystem.Spawn` / `SpawnSquad` and `AiSystem.Attach`, hashed with the registry, saved into the building's state of a registered building as `meitou designation`, `meitou template` and `is public day` (the player's toggle wins). A building with no record (a town not made yet, a save from before) falls back to its level data's residents template.

## Torture racks

**Class** (**Verified**, factory `FUN_14057cc70`): a BUILDING whose functionality has `function` 8 (BF_CAGE) and a non-empty `produces` list or `flags` other than 0 is made a `TortureBuilding` (a
`ProductionBuilding`; every other cage is a plain `UseableStuff`). `gamedata.base` has none; rebirth.mod's `skin peeler` (Peeler Machine) and many mods do (the cooking pole, the hooks, the drill, executions: `produces` set, `flags` 0 in the whole install,
**Verified**, census of the merged load order). Meitou: `TownBuildings.IsRack` -> `Fixture.Rack`; a rack is still an ordinary cage fixture (use node, occupant, lock, `restrains movement`).

**Per frame** (**Verified**, slot 28 `FUN_14029af90`; the constants are the exe's): the user is the first occupant.
- No user: timer and busy flag clear, status 0. Otherwise the user's dialogue gets event 11 (`FUN_140684990`; Meitou fires it each tick, the dialogue's own speaking gate limits it).
- **Armour first**: a user with items in its `armour` section (`FUN_140748380`) only advances a timer by the frame (game seconds); when the first piece's cut + blunt resistance (Armour `+0x228` / `+0x22c`,
  the item's own values) is below timer x 0.04 (`0x1416b1630`) the piece is destroyed (`FUN_140799af0`) and the timer restarts. Output progress 0, status 0.
- **Stretch** (no such armour): each part with a side (`+0x20` non-zero: arms, legs) with flesh - 1.1 x stun above minus its maximum loses the frame in flesh (`FUN_140644a70` with only its first term: no stun, wound or blood);
  one past the limit is cut off (`FUN_14064edc0`) when the dismemberment global `DAT_142133504` is set. While any sided part still has flesh above 0 the frame is busy; when none has, the sideless parts (head, chest, stomach)
  are stretched the same way and the frame is busy while one still takes damage. Vital parts then kill as in any medical update.

Meitou: `CaptiveSystem.StepRacks` (every tick, `CaptiveSystem.Rack.cs`), `MedicalState.StretchLimbs`; the machine (status, output) exists for a player-built rack only. Not hashed unless a rack exists; not saved.

## Player orders on locks

Engine orders built on the tasks above (`LockSystem.Force.cs`, `LockSystem.Cages.cs`; context-menu verbs in [ui-input](ui-input.md#context-menu-verbs)):
- **Use Tools / Use Strength** (285 / 286, `ForceLockOrder`) on a door's, container's or cage's lock (an occupied one of another faction through its occupant's menu, `PrisonerVerbs`): a prisoner works its own cage's lock where it stands (**Observed**: slot 5 of Task_CutLock `FUN_140352af0` and Task_BruteForceLock `FUN_140353350` resolves a character target to the building it is held in, `+0x300`, and rows 285 / 286 have no imprisoned condition, as the pick; rows 76, 201, 185, 186, 285 and 286 carry category 0x55, which includes the chained repertoire 0x10, so a pole prisoner works its lock too, **Verified** row table), else the nearest selected character walks to it (a door from the nearer side) and cuts it (hardness falls as the shackle cut, the best tool is used up, the lock is broken) or breaks it (one strength test, a broken door lock also breaks the door); the picking crime is booked each update (Burglary against the owner, or code 7 for a cage with a captive). **Verified** by vtables / **Observed**: the shackle tasks' update is shared (above); that doors and containers take the same formulas is an engine reading.
- **Smash** (226, a door only): the NPC basher (above) without an attack target; needs a weapon ("I need a weapon to smash that" otherwise), walks to the stand-off spot and swings once per `stonemine` cycle, the blow at 0.6 of it (`LockSystem.BashClipSeconds`: the clip length over its record play speed); Burglary is booked per update in reach.
- **Set Free** (110, `SetFreeOrder`): the nearest selected character walks to the prisoner's cage and lets the prisoner out as `FixtureRegistry.Release` does (a locked cage of its own faction too, the release unlocks it; another faction's only unlocked: "It's locked."; refused for a dead, unconscious or carried prisoner, state 125 `FUN_14099a480`, **Verified**) (cage unlocked and emptied, sentence cleared). The release leaves the slave state (`FUN_1403570a0` never sets it, **Verified**): only bail (`FUN_1406ae170`) and buy-free (`FUN_1406add20`) clear it, and a releaser not of the player's faction takes a slave over (`FUN_14032e430`: new owner, same state). Out of another faction's cage a pending code 7 (Terrorism) is booked as for lifting a prisoner out. **Get in** (107, `GetInCageOrder`): the character walks into a free, unlocked cage and is imprisoned. **Put in** (108): `PutInCageOrder.Cage` names the cage; **Pick Lock** of an empty cage `PickLockOrder.Cage`. Engine choices: the walks and reaches.

## Save keys

Building records in `zone/*.zone` (**Verified** keys by census of 51 zone files, **Observed** meanings): the building's own lock
as `lock lvl`, `locked`, `broken`, `hardness` (written for every functional building, lock or not); each door `i` with the
suffix `i`: `state<i>`, `doa<i>`, `broken<i>`, `gatecode<i>`, `wants lock<i>`, and its lock `lock lvl<i>`, `locked<i>`,
`hardness<i>`. `locked` is saved true only when the lock is effectively locked; on load, level and `locked` are taken only when the saved `lock lvl` is above 0.
A machine (UseableStuff, e.g. a safe) writes its own `broken` after its base class's lock keys (`StorageBuilding::vfunc_17` `FUN_1402a14b0` calls `DoorStuff::vfunc_17` first,
**Observed** by decompile), so the machine's flag wins the shared key and is read back into both; Meitou writes in the same order (`BuildingSave`, then `MachineSave`).
Shackles: the item's INVENTORY_ITEM_STATE record carries the same four keys (`LockedArmour` vfunc 75 writes, 76 reads through
`uses_locked_hardness`, **Observed**), with the same load rule.

## Unknown

| Gap | Engine choice |
| --- | --- |
| Noise or witness alert from a failed pick (nothing traced besides the pending crime and sounds) | none beyond the pending crime |
| Whether KO'd, carried or restrained characters may pick (no explicit test besides task conditions) | require conscious, free arms. A pole (restraint) prisoner may: the rows are registered for the chained repertoire and `FUN_1408ca230` has no restraint test (**Verified**; #642: the original's Rebirth start picks its own pole) |
| Which tool cutting consumes when several are carried | the highest-quality one |
| Exact strength terms of cut/break | settled (**Verified** `FUN_140338090`, `FUN_140641480`): multiplier flags hunger, limbs, limb items, gear (no light, no dust) x strength x CharStats scale `+0x34` (1 for people, the age for animals); Meitou `Lockpicking.Strength` |
| Whether an awake prisoner can be lifted out of an unlocked cage (`FUN_14034fbe0`'s callers not traced) | allowed; the carry rules then knock out a non-friendly one on the shoulder and set a friendly one down |
| Whether non-owners path around locked doors | a building whose doors are all effectively locked is closed for every path; closed unlocked doors are passable |
| `gatecode` | preserve it |
| Which door states 59/60 open for a walk | a walker whose next path legs cross a closed door's plane opens it; at a locked one it stops ("It's locked.") |
| What else sets or clears the squad "house locked" flag | the rules above |
