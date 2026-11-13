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
+ the BUILDING record's `lock level bonus`, then clamped (doors: values below 2 become 1; buildings: below 0 becomes 0) and
capped at 100. The faction is the building's owner faction at creation. A building gets a lock when its `function` is 8
(every cage and pole, regardless of `has lock`) or `has lock` is set; the lock starts unlocked. Hardness: containers and
cages use their own `hardness`; a door reads `hardness` from the **parent** building's record (the house, e.g. 40 for
houses, 20 swamp/old, 60 outposts), its bonus from the door's own record (**Observed**: argument traced to the parent in
`Zones_CreateDoor`). Shackles (`FUN_14089a440`): level = ARMOUR `lock level` clamped 0..100, hardness = ARMOUR `hardness`,
locked at creation (no faction term).

A player-built cage or safe goes through the same constructor (`FUN_1402a0900`, faction from the building's owner slot), so its lock rolls from the player's faction
(`204-gamedata.base`, Nameless: `doors lock level` 75 ± its `lock level random`, plus e.g. the Safe's bonus 25) and starts unlocked (**Verified** by decompile: the constructor
has no player case). Meitou
(`FixtureRegistry.RegisterPlayerBuilding`): a fixture `<site>` with that roll once the building is finished (engine: doors likewise), a cage then working as a town one; a safe's
fixture has no grid (its items are the machine's storage), so it gets no container verbs. Saved with the building's own lock keys (below).

Save sample (autosave of the owner's game, 51 zone files): `lock lvl` values span 0..100 with most at 15..50 (**Observed**).

## Lockpicking (Task_PickLock 76, Task_PickLock_Shackles 201)

- **Chance** (`FUN_140297dc0`, **Verified** by disassembly): with `L` = lock level and `s` = effective lockpicking skill,
  `p = 0.9` if `s >= L`, else `p = 0.9 / 2^((L - s) / 10)`. A character imprisoned in a cage (`Character+0x2f8 == 2`) uses
  `s - 10` for the roll and the shown chance; the skill condition does not subtract it (**Verified**).
- **Allowed** (conditions 166 `iHaveSufficientLockSkill`, 167 `...ForShackles`; `FUN_1408ca230`, `FUN_1408ca120`):
  `p > minimum lockpick chance x 0.01` (CONSTANTS 5 -> 0.05; strict). Otherwise "I'm not skilled enough..." (**Verified**).
  At 0.05 the cutoff is `L - s > 41.7`.
- **Rolls** (`FUN_140297e50`, **Verified**): each task update adds the character's update time (`Character+0xc4`, the
  game-speed-scaled seconds since its last full update, see [game-loop](game-loop.md#the-character-update-budget)) to the accumulator. When it passes the
  next-roll time it resets to 0 and rolls `rand() < p`: success clears the locked flag; failure draws the next interval uniform
  in [5, 20] s. The first roll comes after 5 s. Expected time to open is therefore about `5 + 12.5 x (1/p - 1)` s.
  Meitou rolls on every sim tick with `world.TickSeconds` (`Picking.Step`, engine simplification): the original calls the roll once per task update, i.e. at the character's update cadence
  with the time accumulated since its last full update (coarser for characters outside the every-frame tier). The accumulated total is the same, so the time to open is statistically equal; a roll
  can come up to one update interval earlier and the crime booking is per tick. Per-character update tiers are not modelled (negligible effect).
- **Animation** (**Verified** `FUN_140520bc0`, `FUN_140359160`:86-90): every update the picker plays the target cage functionality's `animation lockpick`, else `medic` (`crawl use` when crawling), at speed `0.55 + 1.45 x 0.01 x skill` (the lerp `FUN_140015b63`, lo 0.55, hi 2.0; not a clamp). The speed is cosmetic: it does not change the roll timing. Meitou: `CharacterCold.PickWork` (set by `LockSystem` each picking tick), `AnimationSystem.HeldClip`, `CharacterAnimation.HeldRate`; a prisoner picking its own cage plays it in place of the caged pose (the base `Cage` names no `animation lockpick`, so `medic`; only `Cage- prisoner post` names one, `stand pole`).
- **XP** (`FUN_1408c62e0`, **Verified**): after each roll, stat 37 gains `k x map[37] x ((101 - stat) / 101)^2` with
  `k = 1.2` on success, 0.24 on failure. The lock level is passed but unused.
- **Bar** (**Verified**, Task_PickLock slot 2 `FUN_140359160`): each update sets the task's floating bar
  (`FloatingProgressBar::ProgressBarWidget`, `FUN_1406e8580`, layout `Kenshi_ProgressBarPanel`: `ProgressBar` fill, `Text`
  caption) to pick time / next-roll time and its caption to "Lockpicking (Chance {p}%)" with the cage penalty. Its screen anchor
  (character vfunc +0x38) and the visibility flag (character `+0x1a8`) are **Unknown**.
- **Messages/sounds** (**Verified** `FUN_140359160`): after every roll, success or failure, any picker (no player check) gets a
  floating label (`FUN_14073faf0`, font 0 small) 15 units over it: "Pick success!" (0.4, 0.8, 0.4, alpha 0.9) or "Pick failed"
  (0.7, 0.4, 0.4, alpha 0.7), the stealth KO's colours; no name, no log line. Success also plays `Character_Unlock_Success`.
  Its rise-speed argument is **Unknown** (Meitou: the hit's 4/s). Meitou: `LockSystem.PickFloater`, shown whatever the floaters option. Wwise events: [audio](../audio.md#characters-footsteps-combat-voices).
- **Target** (`Task_PickLock` slot 5): a building handle (a door resolves to its lock-owning object) or a **character**, meaning
  that character's cage (only when they are in a building prison). Shackles: the item handle, or a character, meaning their
  worn shackles. Self-targets are allowed: the caged character picks its own cage, a shackled one its own shackles (both
  task rows carry the -10 penalty when caged). Task list B also needs `hasOneWorkingArm` and no weapon equipped.
- **Crime** (**Observed**: `FUN_140852c80`, the pending-crime setter of [economy](economy.md#116-booking-factions-and-police-hits-observed),
  called on every update of the task, before the roll): a building owned by another faction -> code 2 (Burglary) against the
  owner faction; a cage holding a character -> code 7 against the captive's `+0x1a0 -> +0xe0`. The task's target resolves to a character (the captive, or the picker itself on a self-pick) and the victim faction is that character's slave owner, not the cage's owner; a captive that is no slave books nothing in Meitou (`SetPending` refuses faction -1; the original's null-faction booking is **Unknown**). Meitou: `Lockpicking.Crime`. Another character's shackles (`Task_PickLock_Shackles` `FUN_140359d90`, `Task_CutShackles` `FUN_140359f80`,
  `Task_BruteForceShackles` `FUN_140352c50`; **Verified**): code 7 against the same field, the wearer's slave owner faction (status `+0xe0`, 0 for a free wearer, **Unknown** how a pending crime against it is booked), with the
  wearer's owner handle as the victim; cut and break book code 2 against faction 0 for a target that is no other character. economy.md lists code 7 as "Terrorism" (from the game's name table); which label the game shows for freeing is **Unknown**.
- **Success side effects**: the picker's own squad's "house locked" flag is cleared when the door is of its own home (**Observed**, `FUN_140359160`: a stranger's pick leaves the owners' flag set); a freed character gets relation event 0xd from
  the picker (0xe on every failed roll, spoken by the captive or the shackle wearer, no player-only test; **Verified**, `FUN_140359160` event code defaults to 0xe, 0xd only on success). Event 13's memory side effect does not run for 14.

## Cutting and breaking (285 / 185 cut, 286 / 186 break)

On shackles the context-menu verbs are "Unlock Shackles" (201), "Use Tools" (185) and "Use Strength" (186) (`FUN_14079b2d0`, **Observed**).
A locked shackle cannot be unequipped: "This item is locked in place and cannot be removed. You can try to pick the lock, find a saw, or use brute strength."

Same targets as picking; Task_CutLock / Task_BruteForceLock share the shackle tasks' update (**Verified** vtables).

- **Cut** (`Task_CutShackles` slot 2, **Observed**): needs `hasTools` (state 122). Each update: hardness -=
  `clamp(0.01 x a x b, 0.05, 0.2) x dt x 2`, where a is the stat-based multiplier `FUN_1406457e0(stats, 1, 1)` (**Verified**, `FUN_140359f80`: the hunger term only, no limb, light or damage terms) and b the strength stat; the break test below uses every term (`FUN_140338090`). Meitou: `Lockpicking.CutStrength`. At <= 0 the
  best tool is consumed (a tool: ITEM `item function` 9, e.g. Hacksaw; `hasTools` `FUN_1405989e0` tests item `+0x124 == 9`, **Verified**) (aborts if none), the lock's broken flag is set, "Shackles broken!" (also for locks).
  The bar shows hardness x 10. Same crime rule as picking (2 for own-target / buildings, 7 for another character).
- **Break** (`Task_BruteForceShackles` slot 2, **Observed**): instant; succeeds when effective strength x stat scale > 3 x hardness,
  else "I'm not strong enough to do that". Sets broken and the hardness to 0, plays Character_Unlock_Success, logs "Shackles broken!" and floats "*Crunck*" (both, **Verified** `FUN_140352c50`), and fires event 13 UNLOCK_MY_CAGE_OR_SHACKLES at the freed character (a cage's occupant, the shackles' wearer) from the breaker (`LockSystem.BruteForced`).
- **Taking off** (`Task_TakeOffCuffs` 219, **Verified**): its update (slot 2, `FUN_140349d00`) queues a world event (kind 5, the
  character's own hand, de-duplicated on the GameWorld list `+0x5c8`) that `FUN_1407b65b0` replays as `Character::setChainedMode(character, false)` (`FUN_14032e590`): the chained flag `+0x320` is cleared and the worn locked
  armour (`LockedArmour`, vfunc `0x2f0`; boots section 9, else section 5, `FUN_1405c8a10`) is removed through the inventory (vfunc `0x30`; whether the item goes to the pack or is destroyed is **Unknown**);
  a player faction's character is refreshed. The inventory's equip and unequip handlers (`FUN_1405dbd80`, `FUN_1405dc310`, **Verified**) also set and clear the flag when an ARMOUR piece with a lock (vfunc `0x2f0`) goes on or comes off, whatever its lock state: a picked, cut or broken lock leaves a wearer chained (none of the pick, cut or break paths calls `FUN_14032e590`). Meitou `LockSystem.Chained`: wears a piece with a lock.
  219 needs !114 (not wearing locked shackles) and gives !134 (`isChained`); 202 Escaping plans 201 / 185 / 186 for the first, then 219.
- **Sounds** (**Verified**, strings and callers). Slot 1 of Task_PickLock (also Task_PickLock_Shackles), Task_CutShackles (also Task_CutLock) and Task_UnlockDoor posts the `Character_Unlock` loop on the character; Task_BruteForceShackles has no slot-1 sound.
  `Character_Unlock_Success` comes from slot 2 when a roll, cut or break succeeds. Slot 3 (the task's end, success included) of Task_PickLock and Task_UnlockDoor posts `Character_Unlock_Fail`, which only stops the loop;
  Task_CutShackles slot 3 only removes the progress bar, so what ends an interrupted cut's loop is **Unknown**. Task_CuffTarget (putting shackles on: "Restraining" bar, `medic` animation) posts no event.
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
  - **Desire** (`AI::scoreAggressiveAction` `FUN_14079d1a0`): 0 for a building in the basher's home town; 1.1 for a bandit (NPC type 8); a player character 1.1
    when aggressive, against an enemy or a not-real faction, else 0; an NPC 0 towards an ally (or in a war campaign, flag **Unknown**), 1 towards a hostile faction (**Verified**, `FUN_1406b26d0`: another faction, relation at or below the hostile threshold; the coexistence flag is not read; Meitou `FactionRelations.IsHostile`), else 0
    for trader, civilian, diplomat or adventurer factions and 1 otherwise. Picking and breaking score 0.1, so a bash beats them. A bandit
    (1.1 > 72's 1) bashes even an unlocked closed door (81 reads no lock); Meitou `DoorPlan` uses the exact state for attackers and ransackers alike, and a planner basher drops its plan on an unlocked door unless it is aggressive (desire over 1).
  - **Door and place**: the building's one door (`getDoor`); at it within the door's reach of its marker (25, 45 for a gateway; x2 standing), height 130.
  - **Impossible**: no door, or a character ahead within a gap of 10 that a ray along the facing hits (any character). Which registry a character plans with
    is **Unknown** (an animal-only one could not reach 81 through 85).
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
- **Repair** (order 95, `Task_Build`; **Verified**: `FUN_1407fc6e0`, `FUN_1405595a0`, `FUN_14034a440`, `FUN_14029fb40`): a door is a Building of its own whose progress a builder raises. Slot 70 pushes the same work as for a site (`lerp(0.3, 2, engineering/100) x dt` x options, falloff), x3 for a door (`+0x1a0`, `DAT_14168a254`); `FUN_14029fb40` leaves the complete flag set on every door,
  so `FUN_14054a3f0` never stalls it and repair needs no materials. Progress above 0 calls slot 98 (`FUN_1402adca0`: only sets `+0x3ec`) with 0: the door is no longer broken (the lock is not touched; it stays open and unlocked from the bash). The task ends once progress / target (`FUN_140547200`, at most 1) reaches 1; the door never "completes" (no message). A right click on a complete, broken, own building (only doors report broken, slot 97) gives order 95 (Shift: job 125 Engineer, [table](ai-tasks.md)). **Unknown**: a placed door's starting progress (`FUN_1405595a0` with the maximum float sets it to the target, taken as the load); the blow above then takes 0.3 to 0.9 off a target of 5 to 50 (door records' `build materials`; Door House 8), i.e. many blows, unlike Meitou's 2 to 4 (**Observed** from the data, not changed).
  Meitou: `ConstructionSystem` (`BuildOrder` on a door fixture id, own doors; `DoorStep`): the character walks to the door's work spot, adds `Rate x work / Target` of the door's record to `DoorProgress` (a fraction), clears `DoorBroken` above 0 and stops at 1; one falloff counter per door per tick.

## Doors

- Door object fields (**Verified**: save writer `DoorStuff` slot 148 / reader 149): `state` (+0x380: 0 closed, 1 open,
  2 opening, 3 closing; this is not the DoorState enum), `doa` open amount (+0x37c, 0..1), `broken` (+0x3ec), `gatecode`, `wants lock`
  (+0x384), and the lock (`lock lvl`, `locked`, `broken`, `hardness`). `wants lock` starts from `always locks` (gates).
- **Destroyed buildings have no doors** ([pathfinding](pathfinding.md) "Destroyed interiors", `Building::createPhysical`) and no lock: **Verified** by census, the
  level data's states of destroyed buildings (both Hub `Defensive Gate IV`, `4400-`/`4617-Newwworld-INGAME`) and two original Hub saves carry no
  door or lock keys for them (those saves write them only for the Hub's intact Bar and Residential Small). Meitou registers no door or own lock
  for them; their interior fixtures stay.
- **Motion** (**Verified**, constructor `FUN_14029fb40`, update slot 28 `FUN_140299030`, pose `FUN_140298d10` by disassembly): doors
  slide, they never swing. Open (`FUN_140297040`) works only from state 0 -> 2 and tests no lock; close (`FUN_140298c50`) only from
  1 -> 3, not on a destroyed door. Each frame `doa` moves by frame time x `door move speed` (+0x388, default 0.5 before the record
  is read); at 1 the state is 1, below 0 it is 0 and, with `wants lock`, the lock is set (`FUN_1402969f0`). Sounds `Door_Open`,
  `Door_Close`; `Door_Shut` at both ends. Leaf offset from its closed pose, `d` = `door move dist`, `o` = `door comes out` (both signed):
  `o = 0`: `slide x d x doa`; else `t = |o| / (0.25 |d| + |o|)` and `doa < t`: `out x o x doa / t`, otherwise
  `slide x d x (doa - t) / (1 - t) + out x o`. `slide` is the door collision shape's local axis `door axis` (0 X, 1 Y, 2 Z); `out`
  is the shape box's thinnest other axis, sign-flipped by a dot test against the shape position (`FUN_14029cc90`; the reference
  point is not decoded, **Observed**: Meitou points it away from the part's origin, which matches the Storm House doors in game).
  The axes are the box's in the collision file's Z-up PhysX frame (actor and shape pose composed), so an unrotated box's axis 1 is
  the part's -Z and axis 2 is up (**Observed**: with this frame no door of the 35 slides along its thinnest box side, and
  Defensive Gate IV lowers from the top as in game; the mesh frame instead slid 5 Storm House doors through their own thickness).
  Data (35 door records): houses slide sideways (axis 0, 16..30 units, speed 1); defensive gates (65..100, speed 0.2..0.25), Saloon,
  Makeshift Gate Panel and Storm House B/D/F..I lift (axis 2); `Concrete Gate Door` (85) and Moor C doors slide sideways (axis 1);
  Outpost Door III/IV sink (-20, -27); 14 TwoStorey doors step out 0.6..13 units first.
- **Who opens and closes** (**Observed**, callers of the two functions): opening: Task_OpenDoor (its start and update; the update
  fails on a locked lock), the door panel button, a broken lock (bashing), dialogue effect 0x36 (the speaker's building's door).
  Closing: Task_CloseDoor, Task_LockDoor and the door panel. The planner opens doors for walks: Moving (1, 29, 67) lists states 59
  `DESTINATION_IS_ACCESSIBLE` and 60 `LOCATION_IS_ACCESSIBLE` in list B, which tasks 85 / 84 make true: they are `Task_Blank` goal-style rows (**Verified**, the factory list) whose finder picks the door and whose effect 52 / 53 is
  provided by `Task_OpenDoor` 72 / 74, the task that opens it ([ai-tasks](ai-tasks.md)). Nothing closes a door behind a walker: only the lock tasks list a closed door (`!52`), and no
  AI package lists tasks 73/75 (**Observed**: TaskData and package census). How states 59/60 pick their door is **Unknown**.
- **Door panel** (player's building, the HUD lines of a selected house: [ui-screens](ui-screens.md#selected-building)): open/close button `FUN_140546fb0`: an open door closes; otherwise an effectively locked
  one is unlocked first, then opened (**Verified**). Meitou: a door still moving counts by where it heads and turns back (an opening one closes, a
  closing one reopens); the original's open/close act only from closed / open, so a slow gate ignores the button for seconds.
- **Player lock toggle** (`FUN_140547060`, **Verified**): flips `wants lock` (sound `Lock_Building` / `Unlock_Building`); locking a
  closed door sets the lock at once, unlocking clears it; an open door locks when it next closes (the update above).
- **Navigation**: the navmesh treats a door as open once `doa >= 0.6` (`FUN_140298d10` -> `FUN_1403a6070`); closed faces are
  blocked for every filter ([pathfinding](pathfinding.md)). A locked lock makes `Task_OpenDoor` fail ("It's locked.") for anyone,
  owner included: the owner must unlock first (**Verified**). Whether the planner routes strangers around locked doors is **Unknown**.
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

- **Lock up from outside** (197; **Verified** rows, `FUN_140596e90`): list B is `!strangers in house` and `!107` (107 I_AM_INSIDE_TARGET_BUILDING), and the lock row 198 needs state 126 AT_LOCATION_BUILDING_DOOR_OUTSIDE
  (**Verified**, `FUN_140596e90`: true when the character's X/Z distance to the door's outer point is below its reach range, 45 for a door; the outer point is the door record's point `+0x3bc` pushed out
  by 1.1 times (`DAT_14169bb44`) its distance to the other point `+0x3c8`; the two points lie 15 either side of the leaf, so about 48 from the leaf; which of the two the original calls outer is **Unknown**), so a 197 worker inside first
  leaves (row 200) and locks each door from its outer side. Meitou: a 197 round (`Round.FromOutside`) is given to a member already outside when there is one, else the worker first walks out to the first
  door's outer point (the side away from the building's origin), then works each door within 45 of its outer point (`LockSystem.OuterPoint`); 155 rounds are unchanged.
- **Opening up** (86 `OPEN_UP_SHOP_DOORS`, Task_Blank, finder `FUN_1408cb360`, **Verified** decompile): the building the character stands in when
  its owner matches the AI's (an owner test, **Observed**), else its home; the target is the first door that is closed and not opening (the plan unlocks, then opens it). With none left
  the finder clears the squad's "house locked" flag. A = !87, so it scores only while the home counts as locked. Only squads whose packages list
  86 open up (base data: Civilian, Shop, Shop-24hr, Slaver and Storage Guard leaders, among others); the rest stay locked.
  Meitou (`LockSystem.LockUpSweep`): the opener's round starts while the home counts as locked by either branch of 87 (the round's flag, or `FixtureRegistry.AllDoorsLocked`). The worker approaches each door from its nearer side, unlocks it and starts the opening motion. **Observed** (headless, #293): the three `stolen slave clothing shop` Swamp Shacks of Slave Markets (Newwworld level data) start `locked0` true with `initial door state` 0 (CLOSED) on the template; the level state wins, so with only round-locked homes opened the shopkeeper outside could not get in, its path ended 29 units from its node behind the back wall (within the goal snap 30) and it stood there all day.
- **Initial door state** (`FUN_140553210`, when the home building is assigned to a squad; **Verified**): CLOSED -> unlocked, not
  broken, closed; OPEN -> unlocked, open; LOCKED -> locked, closed; BROKEN -> unlocked, lock broken, door broken, pose 0 (closed). Saved buildings keep
  their saved values. The gate on building +0xa8 that allows it is **Unknown** (presumably "not restored from a save").
- **Level-data door state**: the level data's GAMESTATE_BUILDING of a placed building carries the [save keys](#save-keys), so a
  building no save has starts from them (**Verified** data, census of the merged `newland/leveldata` zones: 996 doored building
  states have `state0`: 547 closed, 442 open, 7 opening, `doa0` matching, `lock lvl0` 20..49; 265 have no door keys). Town gates
  (`Defensive Gate II-IV`, `Defensive Gate level 2`, `Makeshift Gate`) are mostly open (51 open, 22 closed, 29 closed and locked,
  2 opening; all `wants lock`).
  That the original applies them on a new game is **Observed** (the save format: base buildings keep their level state's handle,
  [save](../formats/save.md#object-handles-and-slot-lists)). Meitou: lock and door state from the level state, the save's on top;
  only doors with a `state` key count as restored, so `initial door state` still applies to the others (**engine choice**, the
  +0xa8 gate is Unknown).

## Cages and beds

- **Entering** (`FUN_140330600` with a building, from Task_UseCage / Task_PutInSomething; **Verified**): `Character+0x2f8 = 2`,
  the prison handle at +0x300 (handle type +0x308 = 0 for a building), the character is moved to the cage position and placed
  at its node; AI movement flags 0x10 when `restrains movement` (pole), else 4. The cage's lock is set **locked** when the
  cage has one (all cages do). **Observed**: prisoners with a bounty for the cage owner get a sentence ([economy](economy.md#115-bail-and-sentence)).
  Unique characters are recorded separately.
- **Facing in a cage** (**Verified** by decompile, `kenshi_x64.exe` 1.0.68): entering sets no orientation. The character update (`FUN_1405cf130`, every frame while
  `Character+0x2f8 != 0`) resolves the prison handle (`FUN_1402676e0`) and, for state 2 (cage, pole, peeler: one path), places the character at the building's position
  (vfunc 0x40, `FUN_1400d1e00`: Building+0x48) with the building's own orientation quaternion (vfunc 0xc0, `FUN_1400d1f00`: Building+0xb0) through `FUN_1405b1a30`, which
  writes the animation object's position and orientation directly (no turn rate). So a prisoner faces the cage building's local +Z (Meitou: `Fixture.Yaw`, 0 along +Z), whatever
  it faced before, also after a load; there is no door, node or direction-marker lookup (state 1, a bed, takes the `getPositionMarker` / `getDirectionMarkerQuat` path of "Lie spot"
  below; no Building subclass overrides vfuncs 0x40 / 0xc0). The six `Node Use` nodes of the cage records play no part here. The same rule serves the pole and the Peeler Machine.
  Nothing else is written in that branch: no path or order queue (one virtual on the object at Character+0x648, vfunc 0x98, is not decoded; the movement physics body is synced after, `FUN_14065dc60`).
  A sleeper (state 1 with a resolvable bed) is re-placed every update at the use node nearest where it currently is (the markers are asked with the character's own position, `FUN_1405c8540` writes it).
  Meitou: `FixtureRegistry.HoldCaged` (from `LockSystem.SlowWorld`) places every caged character back at the cage's position and yaw (place only) and `HoldSleepers` every sleeper at its nearest lie spot, so a prisoner that came from a save or was turned stands as the original does.
  Axis chain (**Verified**): the character update stores the building's quaternion (Building+0xb0, plain orientation, no marker/-90 turn as for beds) into the animation object and its node; a character at the identity
  orientation faces +Z (the portrait stage, `FUN_14084b890`; `human_male.mesh` head and toes extend to +Z, measured), so a prisoner faces the cage's local +Z, as `TownBuildings.YawOf` (yaw of Q applied to +Z) and
  `CharacterRenderer` (`CreateRotationY(yaw)`) have it. The `cage1.mesh` (Prisoner Cage part `Cannibal Camp Cage`) lock box is a vertex cluster on its +Z wall (x ~ +4, z ~ +7, mid height; **Observed** from vertex positions),
  and the Spiked Cage's `Cage_LOCK-ext-Node001` (5.6, 0, 13) looks along -Z (node yaw -85 deg, minus 90) at it: the door is on local +Z, so a prisoner faces the door. The part's `offset Z` (12) belongs to a
  `is for position marker` part: it places the building's position marker (where characters use the cage), not the mesh. The mesh and the occupant both sit at the origin.
- **Locked cage**: the occupant cannot leave; `Get out of cage` (133, 207) needs `!isDoorLocked`; tooltip shows the lock level
  or "The cage you are in is UNLOCKED. Nobody knows this but you" (`FUN_1405d4260`).
- **Pose while held** (**Observed**, the original against Meitou): the fixture's own clips play, nothing else. A caged prisoner out cold or dead lies on its side in
  the cage's `animation KO` (the Prisoner Cage's `foetal`), an awake one stands in its `animation` (the Cage's stand) at once, and the Prisoner Pole keeps its stand
  pose whatever its occupant's state. No fall is played into the cage and no get-up out of it. An animal, which has no such clips, is left as a still frame: its
  animation does not advance. Meitou: `AnimationSystem` skips the ragdoll hold for a held character (the furniture refuses the ragdoll, so nobody posed it and it
  stayed flat in the knockout's pose) and leaves a held animal's layers alone. A prisoner loaded from a save takes its cage's clips when `FixtureRegistry.Bind` binds it,
  as one put in by `Imprison` does; before, it played the plain idle or lying clip.
- **Leaving** (`Task_GetOutOfCage`, `FUN_1403554b0`, **Verified**): restores AI flags and pose. The task's constructor flag (`+0x80`; 1 for 207 and 256, 0 for 133, the task factory `FUN_14032ebd0` and `FUN_140335200`) is copied to the prisoner's
  `+0x1a0 -> +0xe8` (Meitou `Escaped`) only when the cage owner faction is real (`+0x1d0` clear); for a `not real` owner nothing is written. A nonzero flag also turns a slave in
  state 1 into state 2 and sets crime 0xc Escaping Prison against the cage owner (the setter refuses a `not real` or `fundamental type` 8 victim), after the hold is cleared (`FUN_140330600`), since the setter
  refuses an imprisoned culprit. There is no sentence test: a prisoner whose sentence has run out who leaves through 207 or 256 still escapes. 133 (flag 0) is added by `Task_ReleasePrisoner` only for a releaser Meitou (`LockSystem.OpportunisticEscapes`): an AI prisoner whose role list holds 256, 207 or 202 in its hours walks out of an unlocked cage.
  that books bounties or is the player's. Lifting a prisoner out of another faction's cage (`FUN_14034fbe0`): the lifter gets code 7; the prisoner gets the escaped flag and a code 0xc call made while it is still
  held (`+0x2f8` = 2), which the setter refuses ([carry-loot](carry-loot.md)), so no pending crime (**Verified**).
- **Escaped flag** (`StateBroadcastData::isEscapedPrisoner` `+0xe8`, KenshiLib 1ce4a16; **Verified** by decompile: every write through `+0x1a0`). Set only by `FUN_14034fbe0`
  (lifted out) and `Task_GetOutOfCage` flag 1 (above). Cleared only by: being caged again (`FUN_140330600`'s put-in branch); `Task_GetOutOfCage`
  flag 0 (133, a release); `ActivePlatoon::periodicUpdate` (`FUN_1404ff060`, vtable slot +0x40): a squad split from another (`Platoon::isSeparatedSquad` `+0xb8`) whose parent
  has an active platoon within squared distance 1.0404e7 (~3226 units) moves each member that is not imprisoned, not slave state 1 and passes two more tests (byte `+0x3d4` clear, vfunc +0x30 false; **Unknown**) into the parent, and
  clears its escaped and kidnapped (`+0xe9`) flags; the status update `FUN_1405a4f50` (**Verified**; an array-form write, `param_1[0x3a]`, which `field_writes.txt` does not index):
  cleared on each update while the status town (`+0x88`, [rule](carry-loot.md#the-status-town)) is null, as the freed flag `+0xea` is. So "Leave town to clear this status"
  ([ui-screens](ui-screens.md)) is true; no timer clears it, the 99999 hrs is text only. Meitou: `ArrestSystem.DropTownStatusOutsideTowns`. (The other `+0xe8` writes in `FUN_1408cb360` / `FUN_1407933a0` are the Blackboard's `homeIsLockedUp`.)
- **Exit spot** (**Observed** from decompile, `FUN_140330600` → `FUN_140661f90`): clearing the hold resets the character's movement body while `+0x2f8` and the handle are
  still set, so it looks up the held building's `Node Use` (type 4) node nearest the character's position (`FUN_14038ab50`; the character stands at the cage origin) and
  moves the body there after a physics ray query at the node (`FUN_1403a2fa0`, door hit filter; the ray's exact use is **Unknown**). No node (Prisoner Pole): it stays.
  Data (base game probe): Prisoner Cage's nearest is `usenode008` at local (0.2, 9.4), the door side; Spiked Cage's `Cage_GuardNode003` at (-1.6, -13.4), behind it
  (its lock node is 14.1 away); Peeler Machine's one node at (0.1, 11.3). Meitou: `FixtureRegistry.ExitSpot` for Get out and Release; lifting out leaves the body to the carrier, but a gatherer's take-out
  (173, 217, 245), which no one carries, puts it at the exit spot too (`LiftOut` with `stepOut`). **Observed** (Rebirth, 2026-11-09): the middle of an
  emptied Prisoner Cage is a pocket of the navmesh no path leaves, so a slave left there stood still all hour while the original's walked to the mines.
- **Release** (`Task_ReleasePrisoner` 110): clears the occupancy, "{1} has been released from prison." (**Observed**).
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
- **Task_UseBed** (98 USE_BED, 100 GET_PUT_IN_BED, 258 USE_BED_ORDER; slots 1 `FUN_140357d00`, 2 `FUN_140351770`, 5 `FUN_140347950`, 6 `FUN_140347ba0`):
  the character is put at the bed's position marker and plays its clip (below, "Lie spot"); with the task's flag +0xa0 (set by which callers is **Unknown**) the start first
  checks rent (`FUN_140357ad0`: a negative cost is passed on to the payment only when the bed has a parent building, slot 59) and power; with the flag clear
  the update (slot 2 `FUN_140351770`, gated by +0xa1 == 0) tests power once instead, so power is always tested (**Verified**). Power (`FUN_140299370`, the sound emitter's powered test, buildings-production.md):
  true for a building with no power figure (`power output` + research bonus 0) or without a grid, else the grid must supply it. Refused: the task ends and the character is told "This bed needs power to operate."
  The base Skeleton Repair Bed has `power output` -10, the other base beds 0 (**Verified**, probe of every BUILDING with a bed functionality). A town's placed
  ones (**Observed**, original game, a skeleton put in one): Black Desert City (Small Generators in its interiors), Burn's Tower (a Small Wind Generator) and Skinhouse HQ
  (Small Generators, Generator II) let it in; the Island Lab (two Battery Banks, no generator) and the Reprogramming Workshop (none) refuse; with every generator's
  `power output` set to 0 by a mod, Black Desert City's refuses too. Making the owner faction `not real` or not, or its `default relation` 0, changed none of
  these. Meitou: `BedSystem.HasPower`, shared by `LieIn` and `LayIn`, checks player-built machines and placed beds against the town's generators
  (`Unpowered`, `TownBuilding.Generators`). Refusal happens before payment or dropping a carried patient, so the carrier keeps it.
  Sound `Character_Bed` / `Get_In`; then enters. Row 258 has no done-list (it runs until replaced), row 98's is
  `isFullyRested`, `isInBed` ([ai-tasks](ai-tasks.md)).
- **Task_GetOutOfBed** (116, 208, 257; `FUN_140345760`, `FUN_140348480`): `Get_Out` sound; when the bed's clip is `gotobed` it is played backwards and
  the character leaves at 10 %, else at once.
- **Task_PutInSomething** for a bed (99; the body's task type 100; `FUN_140358170`, `FUN_140358670`): a player-faction carrier pays the rent first: its own
  cost, or the body's when that is non-negative and lower (a refusal fails the task). Once the put-down clip is below 0.2 and the bed is free for the
  body, both are unlinked and the body enters the bed, then is placed at the bed's nearest type-4 node (`FUN_14038ab50`). A bed that is not free for the body at that moment ends the task
  (**Verified**, `FUN_140358670`: no new bed search, the carrier keeps the body; nothing is tested while walking); Meitou: `BedSystem.Step` tests only on arrival (`LayIn`), put-in job 106 picks again. A relation event follows
  (faction vfunc 5 with the task's +0x7c; amount **Unknown**).
- **Lie spot** (**Verified** by decompile: `FUN_140357d00` calls vfuncs 0x470 / 0x480, `Building::getPositionMarker` `FUN_14054b850` and
  `getDirectionMarkerQuat` `FUN_140549720`, names from KenshiLib 1ce4a16): with fewer than 2 usage nodes (Building+0x2f0) the position is the marker
  +0x26c (the construction position, set in the constructor `FUN_14055ca90`) and the orientation the building's; else the usage node nearest the
  character (squared 3D distance; nodes in the bad-node map +0x300 skipped), turned by Quaternion(w -0.707107, y 0.707107): the node's yaw - 90°, as for
  town nodes ([ai-tasks](ai-tasks.md#town-goals)). Data (**Verified**, base game probe): `Bed` and `Skeleton Repair Bed` hold two `Node Use` (node type 4)
  instances at local Z +11.18 / -11.05 (+12.18 / -12.05), the second turned 180°; Old, Camp and Fish Bed have none. `sleepinbed` holds `Bip01` at about
  (-11.8, 6.7, -1.5) in model space (`gotobed` moves it there from the origin), so from a node the pelvis lands on the mattress centre. Meitou:
  `FixtureRegistry.LieSpot`, also applied to a sleeper bound on load (engine choice), which also takes the bed's clip again (it is not saved; without it a loaded sleeper lay on the floor at the node).
  **Bad nodes** (**Verified**): with 2 or more use nodes `getPositionMarker` skips every node in the building's bad-node map (`+0x318` / `+0x338`) and returns node 0 when all are skipped. `FUN_14054d410(building, position)` marks the node
  nearest the position within 20 units (squared 400) with the world clock (`DAT_1421303d0 + 0xa0`, game hours, **Observed**: Task_TakeIntruderOutside's 5-minute rule reads its differences x 60); its only producer is `notifyPathImpossible`
  (`FUN_140796ed0`, from the path-failure paths `FUN_140332150`, `FUN_14033da80`, `FUN_14050d970`, `FUN_14060d6a0`, `FUN_1405c6840` when the failed goal is a building; the position is the movement object's vector `+0xdc`, goal or position **Unknown**). It also
  records whether any node was still good before marking: only if none was, the goal goes on the AI's give-up list (20). `FUN_14054b790` drops an entry once stamp + 0.5 (`DAT_1416e8e10`) is past. Meitou: `FixtureRegistry.MarkBadNode` (from `BedSystem`'s failed walk, at the walker's position) and `LieSpot`; 0.5 game hours; not saved.
  The marker `+0x26c` is the building's position plus its orientation applied to the `offset X/Y/Z` x scale of a part flagged `is for position marker` (`FUN_14055f6e0`, **Verified**; the mesh is not
  offset), else the construction position. Of the 12 flagged base parts (`bed`, `camp_bed1_part`, two Cannibal cages, five `stone processor`s, three training dummies) only `camp_bed1_part`
  (offset Y 1) belongs to a bed with fewer than 2 nodes (`bed` has two), so a Camp Bed's lie spot is 1 x scale above its origin. Meitou: `WorldObjectLayout.PositionMarker` (from the laid-out parts, last flagged part with a mesh wins) stored on the bed fixture (`Fixture.Marker`) and used by `LieSpot`. The cages' marker (offset Z 12) is not their occupant's place, see "Facing in a cage".
  Heights (**Verified**, base game probe of the clips and `bed1.mesh` / `camp_bed1.mesh` bounds): `sleepinbed` holds `Bip01` 6.7 above the spot, `Bed`'s mesh
  top is 7.36; `Camp Bed` plays `sleeponfloor` (`Bip01` 1.46) on a mesh 1.73 high. Drawn there, the body sinks into the mattress; Meitou draws a sleeper 1.5
  higher (`World.BedLift`, engine choice). How the original avoids the sinking is **Unknown**.
- **Getting out of bed** (`Task_GetOutOfBed`, **Verified**): the start `FUN_140345760` posts Get_Out / Character_Bed; when the bed's clip name (`FUN_140297330`) is `gotobed` the sleeper stays in bed, else it leaves at once (`FUN_140345620`). The update `FUN_140348480` plays `gotobed` at rate -1 and leaves once its progress is under 0.1. No base bed names `gotobed` (they use `sleepinbed`), so only modded beds animate the exit. Meitou: `FixtureRegistry.GetOutOfBed` (`CharacterCold.LeavingBed` / `BedExitDone`, `AnimationSystem.FinishBedExit`) for the AI's get-out step and the bed system's get-up; forced removals (carried off, kidnapped, an order) use `LeaveBed` at once. Meitou stands one getting out where it lay unless the bed's navmesh hole leaves no floor at its height there (a modded bunk's node lies 16 or more inside it); then it stands on the nearest floor point at that height within 40 (`FixtureRegistry.StandBeside`, engine choice: where the original's body lands is **Unknown**).
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
- **canIUseThisBuilding** (`Ownerships::canIUseThisBuilding` `FUN_140621230(ownerships, building, character)`, **Verified** by decompile, names KenshiLib
  1ce4a16): true with no squad faction, for the occupied town's buildings or the squad's own faction's; true for a cage (function 8) when the character passed in has a non-zero
  slave state (`FUN_1405c8aa0`, the status object's `slavestate`; the cage chooser `FUN_1408d0770` passes its carrier, the gatherer `FUN_140a00690` passes none, so a candidate list has dropped
  such cages before the clause counts); false when the building's faction counts the squad's as an enemy, or the building has an inventory; outdoors, true in
  the squad's home town; indoors, true when the parent building `isPublic`, the squad's faction is real and the building's is not the player's; else
  ally(building's faction → squad's, `FUN_1406b2630`; the enemy test is `FUN_1406b26d0`, both read the building faction's own table). Used by the cage finder ([economy 11.7](economy.md#117-arrest)) and the bed search. Meitou: `FixtureRegistry.CanUse`, the occupied-town and home-town clauses read the carrier's squad (home town = the squad's town); the slave-state clause is not modelled. A cage "has a parent" (**Observed**) when it is an item of a building's interior layout or its placed building is flagged `is inside building`; the parent's own key is not kept, so that building's `is public day` (public when absent) stands in for the parent's `isPublic`.
- **Bed search, other characters** (**Verified** by decompile, order of the steps; what the building lists hold is **Observed**): a target already set is kept. The candidate list
  is built from the first step that yields a bed: (1) the beds (interior furniture of function 6, `FUN_14054d4f0`) of the home building (the squad's Ownerships +0x38 hand); (2) with the squad type
  (status +0xdc) not 4, those of the nearest "designated" building (`BuildingFinderNearestAllies_Designated`, kind 2; its per-building test `FUN_1408dac20` reads the building's designation record (vfunc 0x160), its faction, a shop test, a give-up test and an occupancy count `FUN_1403344c0`
  and is only partly read; the meaning of the designation kinds is **Unknown**); (3) with the squad type not 1, 2, 5 or 6, those of the nearest designated building of kind 3;
  (4) else the buildings of function mask 0x40 (`BuildingFinderNearestAllies_Function`, the 3 x 3 grid cells of zones round the character, whole list kept in the finder's order). The first bed in
  the list that no squad mate has as its sub-target (`FUN_1402690e0` count below 1, `howManyGuysWorkingOnThisSubTarget`) and that `UseableStuff` vfunc 0x4f0 (a place free) accepts is the target, passed over only when the squad's home is
  not the building the character is in (+0x128 hand compare, **Unknown**) and the bed's field behind vfunc 0x228 +0x28 is above 1 (**Unknown**, read as a price). The order inside a list is
  that of a set of pointers (steps 1 to 3), so it is not reproducible. Meitou: `FixtureRegistry.FindBedNpc` takes the first free bed of the squad's home building, else the nearest free bed of its
  town that passes the finders' allies test below (engine choices for steps 2 to 4 and the order), so a town's homeless vagrants sleep in a bar's
  beds, not in a private house where the residents call them trespassers. The search is the target finder of 153 / 239 Go home go to bed: its factor is 0 when it finds no bed
  (**Verified**), so a character without a bed does not want the goal and the next one is chosen. Meitou: a sleeper whose home has bed spots takes one of them;
  without (homeless, or a home with no bed) `AiSystem.SpareBed` runs the search (a squad without a town: own or allied beds within the bed search range,
  engine choice); with no bed found the goal's desire is 0. Only 175 Sleep on floor rests without a bed.
  The finders' allies test (`FUN_1408ca5d0`, **Verified** by decompile) passes a building of the finder's faction or one `canIUseThisBuilding` accepts, which
  includes the town the squad occupies ([economy](economy.md)), so a homeless occupier sleeps in the occupied town's beds. Meitou: `AiSystem.OccupiedBed`, the
  member index's (round robin) complete BF_BED of the occupied town not held outside the squad (engine choice for the order).
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
- **Output**: progress = 0.5 - 0.25 x (best level of the sideless parts + best of the sided, level = (flesh - 1.1 stun) / maximum, -1 when none), so 0 for a whole body and 1 once every part is past its limit. The frame after the last busy one
  the rack adds to its output (`vfunc_184`, the argument is whatever the register held, **Unknown**; Meitou 0): an item when the progress is at least 0.999, then progress 0, status 0; other frames status 1.
- The base class then runs with `+0x1a4` (output value) zeroed each frame, so the machine's own production adds nothing, and the "has operators" byte `+0x3a9` holds the busy flag. Not modelled: the user object's `+0xe8` virtual 7 call (**Unknown**) and the global byte
  `DAT_1421349c9` that blanks the frame (**Unknown**).

Meitou: `CaptiveSystem.StepRacks` (every tick, `CaptiveSystem.Rack.cs`), `MedicalState.StretchLimbs`; the machine (status, output) exists for a player-built rack only. Not hashed unless a rack exists; not saved.

## Containers (Meitou)

Interior items with `has inventory` (grid as for a [storage](buildings-production.md), 18 x 18 by default) are container fixtures with their own grid, locked or not (`has lock`).
A resident squad with `vendors` has its home's stock containers filled by the building filler, trader or not, and those holding something locked (the others unlocked) ([building shops](economy.md#building-shops-observed-decompile));
the others start empty but for the town's [artifacts](economy.md#artifacts) (what else the original stocks them with is **Unknown**). **Engine choices**, not Verified against the original: a right click with a selection walks the nearest
character to one (`OpenContainerOrder`, reach 20, on the container's floor: `MovementSystem.GoToFloor` at its height; placements sit on their own floor, **Observed**: 3415 containers of the 14 busiest zones, the walk's floor at most 17 above the origin, none a storey off, #292) and opens a storage window at the left edge, vertically centred (as a machine's, [simulation](../simulation.md)), staying open until the player closes it (asking for the same container again reopens it, also while paused); the character's inventory opens beside it for an order-driven container and not when a storage or rock is merely selected (the original has no walk-away test; an opener who is down or gone closes it); the player's own locked one refuses
("It's locked."). Another faction's: pointer `lockpick` while locked, else `scavenge` (`search` for own or unowned ones); a locked one with Alt sends the walker to pick it (76 PICK_LOCK), without Alt "Hold down the ALT key to pick the lock." and a walk to it, as the original's right-click path (`FUN_1407fc6e0`); what is taken from it is marked stolen from its owner after the pending crime and the steal roll (`CrimeSystem.Steal`, [simulation](../simulation.md#crimes-and-bounties); caught, the item stays). A building shop's stock containers each show their own part of the shop's stock (a rectangle of it, `Shop.ForBuilding`, `ContainerAccess.GridOf`; engine choice for where the original's containers keep it), its other containers their own contents: taking from a stock one is the theft of any other faction's container (the item is marked stolen from the container's owner), putting into it is refused ("That stock is the shop's."); without Alt another faction's gets the original's "Hold down the ALT key to steal items" and a walk, the menu's Loot (and Alt) steals at once. Items move with
`PutInMachine` / `TakeFromMachine` addressed by the fixture id, within 60 of it ("Out of trading range.", [economy](economy.md#trading-range)). Contents are saved in Meitou's own GAMESTATE_BUILDING string `meitou container <layout index>` (the index of the fixture id; JSON list of items with cell, quality, level,
material, maker, charge, the unique flag when set and a bag's contents; absent while empty; `Locks/ContainerSave.cs`), the original's interior ids being unmapped. Like the locks, only into building states the
save already holds (zones visited), and the original ignores the key (**Unverified**). A lock on a stored shackle and prosthetic wear are not kept.

## Player orders on locks

Engine orders built on the tasks above (`LockSystem.Force.cs`, `LockSystem.Cages.cs`; context-menu verbs in [ui-input](ui-input.md#context-menu-verbs)):
- **Use Tools / Use Strength** (285 / 286, `ForceLockOrder`) on a door's, container's or cage's lock (an occupied one of another faction through its occupant's menu, `PrisonerVerbs`; the prisoner itself never forces it, whether the original lets a caged character run 286 on its own cage is **Unknown**): the nearest selected character walks to it (a door from the nearer side) and cuts it (hardness falls as the shackle cut, the best tool is used up, the lock is broken) or breaks it (one strength test, a broken door lock also breaks the door); the picking crime is booked each update (Burglary against the owner, or code 7 for a cage with a captive). **Verified** by vtables / **Observed**: the shackle tasks' update is shared (above); that doors and containers take the same formulas is an engine reading.
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
| Whether KO'd, carried or restrained characters may pick (no explicit test besides task conditions) | require conscious, free arms, not on a pole |
| Which tool cutting consumes when several are carried | the highest-quality one |
| Exact strength terms of cut/break | settled (**Verified** `FUN_140338090`, `FUN_140641480`): multiplier flags hunger, limbs, limb items, gear (no light, no dust) x strength x CharStats scale `+0x34` (1 for people, the age for animals); Meitou `Lockpicking.Strength` |
| Whether an awake prisoner can be lifted out of an unlocked cage (`FUN_14034fbe0`'s callers not traced) | allowed; the carry rules then knock out a non-friendly one on the shoulder and set a friendly one down |
| Whether non-owners path around locked doors | a building whose doors are all effectively locked is closed for every path; closed unlocked doors are passable |
| `gatecode` | preserve it |
| Which door states 59/60 open for a walk | a walker whose next path legs cross a closed door's plane opens it; at a locked one it stops ("It's locked.") |
| What else sets or clears the squad "house locked" flag | the rules above |
