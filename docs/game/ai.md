# AI

Original AI_PACKAGE/AI_TASK/Blackboard/Tasker/GOAP research from binary, enum/schema and merged
records. [ai-tasks](ai-tasks.md) is the authoritative task/class/condition registry; [simulation](../simulation.md#ai)
describes engine approximations. Preserve squad requests versus per-character decisions.

## Packages and goals

Package slot/class/signal, tiers, target/ending, time windows, weights and world-state rules are
record data, not hard-coded task names. Blackboard maintains squad/home/targets/requests; loading
packages and choosing goals are separate operations. Default character tasks/jobs/player orders,
urgent reactions, unloaded-platoon jobs and dialogue event eligibility have distinct branches.
Do not assume every factory Task_Blank goal has no behaviour outside that factory.

**Observed** scoring FUN_14050df10: filter hour (start<end half-open, overnight otherwise), missing
dialogue event and already-satisfied A. score=desire*weight*crowdFactor^n*m, n same type/target
mates, m1.25 for current type/target/second hand. Effect-state evaluation can scale positive
desire. Sticky current task floors0.4 before commitment expiry, caps0.05 after; another owner
condition can add1.25 when score<1 (unknown). Drop nonpositive candidates; B-satisfied/no-effect
branch also drops. Candidates are keyed by score in an ordered map; while a key is taken the score gets +0.0001 (**Verified**, `FUN_14050df10`), so of equal scores the later-listed goal ranks first (tried highest first, `FUN_140510650`). The default task (IDLE, desire key 0.1, hours 0-100; set by `FUN_14050c030`) is no candidate: `FUN_140510ef0` tries it, at key 1.0, only after priorities 3, 2 and 1 all failed (**Observed**). Desire, commitment and crowd factor per
type: [ai-tasks](ai-tasks.md#desire-and-commitment); exact A/B meanings remain unresolved.

The planner (`TaskPathfinder`, [ai-tasks](ai-tasks.md#planner), Observed) chains rows from a goal's effect and unmet B states to the
rows whose list A achieves them, best-first by desire, and queues the chain leaf first.
Squad toggles inject sleep/ditch/sit goals; slave obedience uses role4. Player commands and job
queues must coexist without erasing urgent state. Squad-shared sensing, retreat and off-screen
routing use [perception](perception.md), [population](factions-squads-towns.md), [pathfinding](pathfinding.md).

### AI_TASK

| Field | Values | Label |
|---|---|---|
| `enum` | `taskType` 0..288 (289 names in `fcs_enums.def`); 108 distinct types are used | **Verified**: probe over all 124 records, every value is a valid index. The record `Name` is free text (101 of the 124 are descriptive phrases, not the enum name) that reads as matching its enum, except rebirth record 1532845 "Find and kidnap (raider) urgent" (255 SIT_ON_THRONE) and "eating food on the ground" (14 IDLE), which are **Observed** mod quirks |
| `classification` | TP_FLUFF 5 records, TP_NON_URGENT 82, TP_URGENT 37 | **Verified** |
| `targeting` | SPECIFIC 99, SELF 11, LEADER 4, SQUAD_MISSION 9, HOME_GATE 1 | **Verified** (counts). Behaviour **Observed** (goal reader `FUN_1402704e0`): LEADER takes the squad leader's hand (`FUN_14079aea0`), SQUAD_MISSION the Blackboard's mission target (`FUN_140269b60`), HOME_GATE a Blackboard lookup (`FUN_14026b410`); SPECIFIC and SELF set nothing at load time (SELF is filled with the character's own hand where the task is created) |
| `ending` (TaskEndEvent) | NOTHING 115, REMOVE_MINE_ONLY 8, REMOVE_WHOLE_SQUADS 1 | **Verified** (counts), behaviour **Unknown** |

### Other records that touch the AI

| Record / field | Role | Label |
|---|---|---|
| SQUAD_TEMPLATE `AI packages` (also 1 of 11 SINGLE_DIPLOMATIC_ASSAULT records, entry (0,100,0)) | 838 of 959 templates; each entry (slot, ...) with slot 0..4 (838 at 0, of which 777 are (0,0,0) and 61 are (0,100,0); 98 at 1, 55 at 2, 8 at 3, 2 at 4), at most 5 packages per template. The slot is a priority: the Blackboard walks slots from the highest to 0. The reader (`FUN_140271620`) passes the entry's first int as the slot to the add-package function `FUN_14026c160`, then resolves every package's `Leads to` among the packages it just loaded (a `Leads to` target that is not one of the squad's own packages stays unlinked) | **Verified** (counts), **Observed** (priority role, `Leads to` resolution) |
| RACE `AI Goals` | 47 of 53 races; entries (SELF_PRESERVATION x30, HEAL_MY_LEGS x16, IDLE x9, EAT_A_RANDOM_DEAD_BODY x7, ...). Read by `FUN_1405094f0`: only val1 x 0.01 is used (no zero default; the data is mostly val1 = 100, i.e. 1.0), val0 is **not** read (the entries are mostly (4,100,0) or (0,100,0)), the hour window is fixed 0..100, and the target comes only from the legacy bool fields `target leader` / `target self` / `squad mission target`, which no current AI_TASK record has (AI_TASK has only `enum`, `classification`, `targeting`, `ending`), so the `targeting` int is ignored on this path | **Verified** (reader and data), **Observed** (data) |
| FACTION / FACTION_TEMPLATE `AI fallback` | Packages added at slot 0 (`FUN_140271fd0`); used by 1 FACTION record and 0 FACTION_TEMPLATE records | **Observed** |
| DIALOGUE_LINE `AI contract` (169 uses; val0 = hours), `change AI` (190 uses), `has package` (751 uses) | `change AI` replaces the squad's packages, `AI contract` runs one aimed at the dialogue target: [dialogue Effects](dialogue.md#effects) | **Observed** |
| AI_SCHEDULE | Empty in the base game set (0 records in the merged database); fcs.def says its `packages` list switches packages by hour (val0), but no runtime use was found | **Verified** (count) |

AI contract start (**Verified**, `FUN_140272240`, called by `FUN_140272740` and `FUN_140272570`, `FUN_140272c20`): does nothing when the platoon's running contract (+0x210) already has the same package record (+0x68), so a repeated identical contract keeps its end time and target. Otherwise it ends the old contract with `FUN_14026a110` (event 30 on the leader's dialogue state, the old package's end text, notice to the old target; also what a natural end runs), then builds the new one (end = now + hours; under 1 hour is the constant 10 minutes). Meitou: `Blackboard.StartContract`; the event 30 fires only with a `ContractEndTalk` (a small difference).

### Enums

| Enum | Values | Source, label |
|---|---|---|
| `taskPriority` | TP_JUST_ACTION 0, TP_FLUFF 1, TP_NON_URGENT 2, TP_URGENT 3, TP_OBEDIENCE 4, TP_MAX_SIZE 5 | **Verified**: the FCS editor's enum (decompiled, outside the repo; it is not in `fcs_enums.def`); `OrdersReceiver` holds two arrays of five lists (constructor `FUN_140507d20`, indexed at +0xa0 and +0x118 plus tier x 0x18 in `FUN_140510d60`); data uses only 1, 2, 3 |
| `taskType` | 289 names | `fcs_enums.def`; see [ai-tasks.md](ai-tasks.md) |
| `TaskTargetType` | SPECIFIC, SELF, LEADER, SQUAD_MISSION, HOME_GATE | `fcs_enums.def` |
| `TaskEndEvent` | NOTHING, REMOVE_MINE_ONLY, REMOVE_WHOLE_SQUADS | `fcs_enums.def` |
| `UnloadedPlatoonJob` | NONE 0, PATROL_TOWN 1, PATROL_SHORTRANGE 2, PATROL_LONGRANGE 3, GOHOME 4, TRAVEL_TARGET 5, TRAVEL_TARGET_FAST 6 | The FCS editor's enum (not in `fcs_enums.def`). The numbering of 2, 4, 5 and 6 agrees with the unloaded update's behaviour per value; 1 and 3 have no branch there (**Verified** by code, [off-screen world](factions-squads-towns.md#off-screen-world)) |
| `BlackboardSignalFunctions` | see the table below | **Verified**: the FCS editor's enum (not in `fcs_enums.def`) and the RTTI package classes built by the factory agree value by value |
| `EventTriggerEnum` | 0 EV_NONE, 1 PLAYER_TALK_TO_ME, 3 I_SEE_NEUTRAL_SQUAD, 13 UNLOCK_MY_CAGE_OR_SHACKLES, 15 I_DEFEATED_SQUAD, 30 CONTRACT_JOB_ENDED, 44 BOUNTY_SPOTTED, 45 ESCAPED_PRISONER_SPOTTED, 46 PRISONER_FREE_TO_GO, 51 BOUGHT_ME_FROM_SLAVERY, ... | The editor exe's numbering is authoritative. `fcs_enums.def` is a commented subset in another order (it starts with EV_PLAYER_TALK_TO_ME, no EV_NONE). Cross-check: the dialogue `dialogs` val0 histogram (event 3 x294, event 1 x220, 13 x147, 15 x87) and the binary firing 30 on contract end (`FUN_14026a110`) and 13/44/45/46/51 at the prison sites of [economy 11.5-11.8](economy.md#115-bail-and-sentence). Full list in [dialogue](dialogue.md#enums) (**Verified**) |
| `DialogActionEnum` (DA_*), `DialogConditionEnum` (DC_*), `DialogRepetitionEnum`, `CharacterTypeEnum`, `TalkerEnum`, `PersonalityTags` | Names and numbers. `DialogActionEnum` has unprefixed entries 41-48 (`CHOOSE_SLAVES_SELLING` .. `CHOOSE_ANIMALS_BUYING`, 43 `CHOOSE_PRISONER_BAIL`), so 49 is `DA_CLEAR_BOUNTY`; arrest-related: 6 ATTACK_CHASE_FOREVER, 8 TAKE_MONEY, 10 PAY_BOUNTY, 29 ARREST_TARGET, 30 ARREST_TARGETS_CARRIED_PERSON, 32 ASSIGN_BOUNTY (matched to the action switch `FUN_140680560`, **Observed**). DC compare-by: 0 `==`, 1 `<`, 2 `>` | Full lists in [dialogue](dialogue.md#enums). From the FCS exe decompile (`probes\characters-fcs-decomp`), enum names and values only |

### Signal functions and the package classes

**Verified** (factory `FUN_140285ba0` builds the subclass from the signal value with one switch case per value; each constructor's
vtable is the RTTI class named here; the editor's enum names are matched to the classes by meaning).

| Signal | FCS name | Package class | Signal | FCS name | Package class |
|---|---|---|---|---|---|
| 1 | SIG_RETREAT_CANNIBAL_RAID | CannibalRetreatFromRaid | 20 | WAR_ASSAULT_TOWN | Package_WarAssault |
| 4 | SIG_CANNIBAL_START_PATROL | GoOutOnPatrol_Cannibal | 22 | SIG_REFUGEE_DISBAND | Package_Refugee |
| 5 | SIG_SQUAD_NOT_INTACT | RetreatAndRefresh | 23 | SIG_START_PATROL_RUNNING | GoOutOnPatrol_Running |
| 6 | SIG_START_PATROL | GoOutOnPatrol | 24 | SIG_GATHER_UP_ALL_LOCAL_PRISONERS_UNHOLY | PrisonerGathering_unholy |
| 10 | SIG_GATHER_UP_ALL_LOCAL_PRISONERS | PrisonerGathering | 25 | SIG_RETREAT_CANNIBAL_RAID_TO_NEAREST_NEST | CannibalRetreatFromRaid_nearestNest |
| 11 | SIG_SLAVER_SHIP_TO_WORKCAMPS | SlaverPrisonerShipping_Camps | 27 | SIG_CANNIBALS_AT_HOME | Package_CannibalsAtHome |
| 12 | SIG_BODYGUARD | Contract_HiredAlly | 28 | SIG_SLAVER_SHIP_TO_ANYWHERE_CLOSE | SlaverPrisonerShipping_AnywhereTownsIncluded |
| 13 | SIG_BECOME_EX_SLAVE | Contract_UntilExSlave | 29 | SIG_PASSING_BY_TOWN_ASSAULT | Package_PassingByTownAssault |
| 14 | SIG_OUT_OF_DANGER | Contract_UntilOutOfDanger | 30 | SIG_OCCUPY_CONQUERED_TOWN | Package_OccupyTown |
| 15 | SIG_DEFEND_PLAYER_TOWN_TIMED | Contract_DefendPlayerBase | 31 | SIG_DEFEND_PLAYER_TOWN_CAMPAIGN | Campaign_DefendPlayerBase |
| 16 | SIG_WANDERING_TOWN_TO_TOWN | Package_WanderingTrader | 32 | WAR_ASSAULT_TOWN_CANNIBAL | Package_WarAssault_Cannibal |
| 17, 26 | SIG_HANG_OUT_AT_BAR, SIG_HANG_OUT_OUTDOORS_ONLY | Package_HangAroundBar (constructor argument 0 / 1) | 33 | SIG_TIMED_CONTRACT | Contract_Timed |
| 19 | WAR_GATHER_FORCES | Package_WarBase | 0, 2, 3, 7, 8, 9, 18, 21 | SIGNAL_NONE, SIG_AT_SAFE_TOWN, SIG_AT_HOME_TOWN, SIG_DIPLOMAT_MISSION, SIG_DEFEAT_SQUAD, SIG_HOME_OWNER_KEEP_LOCKED, SIG_RAIDING_WEAK_VILLAGES, WAR_BATTLE_MEETING | plain `AIPackage` |

### Prisoner and slaver packages

**Observed** (each class's vtable decompiled: slot 6 activate, 12 can start, 13 finished, 19 the class's own finder; the base `AIPackage` slot 12 is 1, slot 13 is 0).
Meitou: [simulation, captives](../simulation.md#captives).

| Class (signal) | Can start | Finished | Activate |
| --- | --- | --- | --- |
| `CannibalRetreatFromRaid` (1) | the platoon has a squad, the leader is not in a town of its faction (status `+0x88` town's faction), and a member carries someone (`+0x348`) | the leader is in a town of its faction | mission town = the base package's (the platoon's home town), else the nearest town of its faction (`FUN_140927bb0`) |
| `..._nearestNest` (25) | as 1 | as 1 | always the nearest town of its faction |
| `GoOutOnPatrol_Cannibal` (4) | not while the cages its first able member knows are all full (`FUN_1405a1ab0`); else as `GoOutOnPatrol` | as `GoOutOnPatrol` | as `GoOutOnPatrol` |
| `Package_CannibalsAtHome` (27, a `Package_HangAroundBar` subclass) | the platoon's current town is its home town; or a town of a faction not hostile to it whose town vfunc `0x2d0(3, leader)` list (its BD_BAR buildings) is not empty (**Observed**) | a player-owned town with town vfunc `0x298` false, else not can-start | the bar package's table pick |
| `PrisonerGathering` (10), `_unholy` (24) | the squad is intact and slot 19 finds a prisoner: the leader's town, else the nearest town of its faction within 3062 (squared 9.375e6), not a slave camp or prison; a cage (function flag 0x100) of a faction allied to it (`FUN_1406b2630`), not player-owned, with an occupant that is not down (24: a woman or another race than the leader) | the platoon's slave list holds more than 3 x its members, or no prisoner is left | base |
| `SlaverPrisonerShipping_Camps` (11) | the platoon's slave list is not empty | it is empty | mission town = slot 19: the nearest slave camp (type 5) of a faction not hostile to it (`FUN_140927f10`) |
| `..._AnywhereTownsIncluded` (28) | as 11 | as 11 | a TOWN (type 2) with town flag bit 10 (`FUN_140936e60` sets the flags from the town and faction records; bit 10's meaning **Unknown**) within 0.7 x the camp's squared distance, not hostile, else the camp (`FUN_140939090`) |

Slot 3 returns 4 in the base class, 1 for signals 1 and 25, 2 for `RetreatAndRefresh`, 0 for 11 and 28 (meaning **Unknown**).

The goals these packages list (capture 167, carry 168, work the slaves 188, the slaves' own worker 189 / 190 and obedience 187) are in [ai-tasks, slave work, shout and capture](ai-tasks.md#slave-work-shout-and-capture).
A slave's Slave role list runs through the Obedient Slave job (187): [role lists](#goal-loading).

### Package classes

**Verified** by code (the blackboard update `FUN_1402732b0` → slot walk `FUN_140272570`, activation `FUN_1402721a0`, the class vtables) unless marked.
- Vtable: +0x30 start (called on activation after the old package's +0x38 end), +0x48 update (the running package, after the choice), +0x50 sees a character,
  +0x60 can start, +0x68 finished, +0x90 may hand over to `Leads to` (base: yes; a finished package whose `Leads to` is refused fails its slot), +0x18 speed
  mode (base 4, PassingByTownAssault 2, Refugee and DefendPlayerBase 1). Package fields: +0x8 `mission town`, +0x28 `mission target`, +0x48 `mission employer` (saved under these keys).
- Blackboard current town (+0x138, `FUN_1402688d0`, before the choice): the leader's town, else the nearest town (any type but nest markers) whose radius
  (`FUN_140926d50` × 1) holds the platoon; entering one sets the arrival hour (+0x140) and fires the leader's ENTER_TOWN (49). The leader's own town is the `+0x88` of the
  object at Character `+0x1a0`; Character slot 15 (`FUN_1405c89b0`) reads that field for any member (Town slot 70 compares it, [campaigns](factions-squads-towns.md#campaigns)).
- **Package_HangAroundBar** (17/26): can start in its home town, or in a town not hostile to it that has bars (`vfunc 0x2d0` kind 3); finished in a player town
  that is not `is public` (the squad not the player's) or when it can no longer start. So a bar package runs where the squad is.
- **Package_WanderingTrader** (16): start draws the stay W = uniform[5, 8) h, state 0 and the next town with the chooser `FUN_14092c5e0` (faction = own, from the
  platoon's position, excluding the current town, mask 0x6c: towns of type town, village, slave camp or military, or any player town; kept only when `is public`
  (Town +0x380) or of its own faction, not hostile, not in its `no-go zones` (FactionWarMgr +0xa0); the 4 nearest by X/Z, one uniformly). Update: no town → start
  again; in its town state 0 → 1 → 2, and when ready (`FUN_1402868c0`: in a player town that is not public at once, else W h after arriving and between
  06:00 and 20:59) it drops its town (`FUN_140288bd0`) to draw another. Can start while state < 2 or when ready; finished at state 2. So templates with a slot-0 bar
  package hop town to town, sitting in each town's bar 5-8 h. Unread: a leader flag (Character +0x648 → +0x70) that also lets it leave 0.2 h after being set (**Unknown**).
  Meitou (`PopulationData.TraderTown`): placed towns and the player's outposts (`PlayerTown.Public`, the [town panel](ui-screens.md#town-panel)'s tick) in one nearest-4 draw, placed towns first in a tie (the original's order over its two lists is **Unknown**).
- **Package_PassingByTownAssault** (29): can start when `attacks` (settings.cfg) is not 5, it is not spent (+0xb4), its leader's town is a player town whose faction it is
  not allied with (`FUN_1406b2630`: same or relation ≥ 50), the platoon is intact, the faction's wait has passed (global map by faction `DAT_14212dde0` of
  {last end, running}: free while one runs; a faction that never ended one is free; else more than the wait since the last end) and that town's faction has someone
  within 2500 of it (and Town vfunc 0x2f0, +0x3b0 set, **Unknown**). Start: the wait for next time by `attacks` (`FUN_140286a70`, uniform, game hours **Observed**: 0 none, 1 [6, 48),
  2 [32, 96), 3 [72, 144), 4 [130, 188)), the town as mission town and target, its faction the enemy (+0x100), start hour, and the enemy's people within 2500 as targets;
  each one seen later (and every enemy-tagged, awake, posture-0 character in a town in the members' sensed lists, `FUN_140294980`) is added, never removed, and tagged squad memory 4 (the tag has no expiry and the end does not clear it, **Verified** `FUN_140296150` never calls the clearer; Meitou: `AggroSystem.Refresh` emits it for every sensed character of `Squad.War`, the hook's in-view gate `+0x1a9` taken as always true, engine choice). End: the faction's running count drops, at 0 the last-end hour is set. Finished: not intact (spent); else not
  before 0.25 h, not while a target stands (**Verified**, `FUN_14028eca0` and the hand-over `FUN_14028ea90`: its current town is the mission town — the hand compare vfunc `0x10`, `FUN_1400cd0b0`, returns "differs", the test wants it false — with the inside-town-walls value `0x118` non-zero, not unconscious or dead, `+0x2f8` not 2 caged, posture 0; Meitou `AiSystem.DefenderStanding`, the walls value not modelled), then (spent) with at least 2 members up. May lead
  (to the occupation): intact, no target standing, at least 2 up. Data (rebirth.mod): 33 bandit/raider templates have it at slot 1-3 with `hostile occupy town` above it.
- **Sensing hooks** (package slots 10 `assessNeutral` and 11 `assessEnemy`, **Verified**; called from `FUN_140859680`, which `FUN_14085a5f0` feeds one sensed-list entry per Character update
  (`FUN_1405c7d40`, vfunc 79) through a cursor at `+0x100` that wraps at the list's end, so every entry is visited in turn; the hook runs only when the listener or the target is on
  screen (`+0x1a9`, the camera frustum test of `FUN_1405c9f60`) or the target is with the player (`FUN_140856bc0`). Meitou: `AggroSystem.HookRuns` applies the gate from the host's camera (`World.View`, `MedicalClock.OnScreen`; a world never told a camera runs the hooks), and each look takes every accepted sensed character at once instead of one per update (the cursor only spreads the visits over updates; a look every 15 ticks stands for the walk
  through the list, **Observed**)): base: TEMP ENEMY on a target of the squad faction's current campaign; `Contract_HiredAlly` (12): the employer's faction TEMP ALLY, and a
  target the employer is allied with is cleared of TEMP ENEMY / criminal / aggressor, tagged TEMP ALLY and not sensed as an enemy; `Contract_DefendPlayerBase` (15): the mission
  town's faction TEMP ALLY; `Contract_UntilOutOfDanger` none; `Package_WarAssault` / `_Cannibal` (`FUN_14028a3f0`, **Verified**): TEMP ENEMY on anyone not an animal (Character vfunc 0x248 null), of a faction whose `not real` flag (`+0x1d0`) is clear and whose
  `fundamental type` (`+0x34`) is not 8 (the faction's, not the character's class), and whose current town is none or the package's mission town (a hand comparison that returns true on a
  difference, so another town is skipped; there is no distance test). Meitou (`AggroSystem.WarEnemy`, `WarTargets.Town`): a hired squad never takes its hirer's faction (or, 12, its allies) as an enemy, 15 takes the
  hirer's faction for the town's; an Everyone war target follows the rules above and each one the looker sees or hears this look is tagged TEMP ENEMY in its squad's memory (`FUN_140674300` tag 4, the time refreshed each look; same-faction refused;
  `AggroSystem.TagEffect`, applied serially), which makes it an enemy of every member until the tag fades (6 h, `SquadMemory.Age`). `Contract_HiredAlly`'s hook (`FUN_140289570`, **Verified**: the employer handle, a character or platoon, resolves and its faction equals the sensed
  character's) tags TEMP ALLY (3) the same way, setting the squad's player TEMP ALLY flag for a player-faction character; Meitou writes it for each such character the looker sees or hears (a look that picks no fights
  does not run the scan, so tags the hook would write there are missed); the base and other hooks write no tags yet. `Contract_DefendPlayerBase` (hook `FUN_14028a290`, ctor `FUN_14028a0c0`, start `FUN_14028a170`; **Verified**): the exempt faction is the owner of the player town nearest the platoon leader (the mission town, set at the start; none when there is no player town), not a stored hirer, and seeing it tags the character TEMP ALLY (`FUN_140674300` kind 3). Meitou: `AggroSystem.EmployerOf` (signal 15), `Scan.EmployerTags`; signal 12 keeps the hirer's faction. The package **update** (vtable +0x48; `Contract_HiredAlly` `FUN_1402910e0`, `Contract_UntilOutOfDanger` `FUN_140290f90`, both defend classes `FUN_140291410`, **Verified**) also tags the mission target itself (+0x28, a character or squad) TEMP ALLY in the platoon's memory every update; the defend classes only with a mission town set, else they run the start. Meitou `AiSystem.TagMissionTarget` (characters; signals 12, 14, 15 with a player town; the campaign's defend package is run by the campaign).
- **Package_OccupyTown** (30): never starts on its own (only by `Leads to`). Start: the leader's town as mission town, its faction the enemy (+0xb8, memory 4 on sight),
  each member fires TAKEN_OVER_PLAYER_TOWN (73), the platoon made persistent if it was not (undone at the end). Update: the platoon joins the town's occupier set
  (Town +0x118; its count +0x128 is "Hostile Occupiers:" in the town panel, `FUN_140922e60`). End: removed from the set, "{1} squad has been removed from {2}"
  (faction, town). Finished: the town gone (vfunc 0x2f0 false), or no member alive and up in it (`FUN_140289180`) for more than 4 of its timer (`DAT_142133794` per
  call, read as seconds, **Observed**). No ownership change: the town stays the player's. `campaign victory occupy town` sits only in an unread `victory AI leader`.
- **Package_Refugee** (22, 4 templates: the Bandit Raiders roamers, Black Dragon and the two White Eyebrow camp squads; all list it beside patrol/bar/assault/occupy packages, at slot 0 or 2; goals
  215 TRAVEL_TO_TARGET_PACKAGE to the mission, 15, 22, 127 RUN_AWAY_HOMETOWN, 36). **Verified** by decompile (vtable `Package_Refugee` 0x1416adf38; the package's +0x88 is its platoon):
  - Can start (`FUN_140288ed0`): the home town's vfunc 0x288 (`TownBase +0xe0 → +8`, meaning **Unknown**, read as destroyed), or the platoon's leader is a slave
    (the status object at Character +0x1a0, its first int = `slavestate` 1 slave / 2 escaped, `FUN_1405c8aa0`; [carry-loot](carry-loot.md#unknowns)) and its squad (platoon +0x1d8 → +0x58) has fewer than 3 members.
  - Start (`FUN_140287760`): platoon +0x115 cleared (not persistent), flag `broken` (+0xb0) = the platoon is not intact (`FUN_1407ac7b0`), mission target cleared, mission town = the nearest town of the platoon's
    faction other than its home (`FUN_140927ad0`, X/Z), and with the home destroyed the platoon is re-homed there (`FUN_1407ec8d0`).
  - Update (`FUN_14028d190`; only for a broken one): with no target it takes the nearest other platoon of its faction in its 4608-wide grid cell (`FUN_1408f56b0`/`FUN_1408f60f0`) that is intact and
    has more than 2 members (`FUN_1407ec0c0`: `FUN_1407ac7b0` and platoon +0xa4 > 2) as mission target (type 1 handle); the travel goal walks there; within 1000 (squared 1e6 `<`) of the target
    every member of its squad is added to the target's squad (squad vfunc 0x10), a target running the same package id forgets its own mission target, and the platoon is removed from its faction
    (`FUN_1406ba9d0`). A target that no longer resolves is cleared.
  - Finished (`FUN_140288f80`): the leader's current town (`+0x1a0 → +0x88`) is the mission town (**Observed**: the second argument of the handle compare `FUN_140594440` is not in the decompile).
  Meitou (`AiSystem.Packages.cs`, exercised by a throwaway test, not kept): as decoded for AI-run squads (a squad of a roaming platoon is not AI-run), counting living members for both counts, searching the AI-run squads of the
  faction in the same grid cell; the merge is `SquadManagement.Merge`. Not built: the destroyed-home half (Meitou towns are never destroyed), persistence.
- **Campaign_DefendPlayerBase** (31): start: the player town nearest its platoon (`FUN_140927f10`) as mission town; finished when its employer turned on it (+0xa8) or the
  blackboard's contract time (+0x218) has passed; sees the town faction as allies (memory 3); its end posts "Contract with {faction} has ended." / "has been
  terminated." when its employer is the player's. Only as a campaign's `pt2` package (the campaign drives it).
- **Package_WarBase** (19) / **Package_WarAssault** (20, 32): finished when the platoon is out of its campaign; no update of their own (the campaign drives them,
  [campaigns](factions-squads-towns.md#campaigns)).

### Goal loading

1. A package is activated (`FUN_1402721a0`), which clears the Blackboard's role lists 0, 1, 2 and 4 (list 3 is not cleared), marks the
   package as just activated, then calls the goal loader (`FUN_140270850`).
2. `inherits from` is processed first, recursively, so a parent's entries are added first. Adding goes through `FUN_140268e10`, which
   calls `FUN_14027b3a0`; that skips an entry whose task type and target hand equal one already in the list, so **the parent's
   entry wins**. The hand (`FUN_1400cd060`) is equal when both are null or kind and all four id words match; at load (`FUN_1402704e0`) only `targeting` 2 LEADER, 3 SQUAD_MISSION and 4 HOME_GATE fill it, so two same-type entries with `targeting` SPECIFIC and/or SELF (hands null; SELF's is filled at goal sync, after) count as duplicates (**Verified**; Meitou `AiData.SameHand`; a LEADER or SQUAD_MISSION hand that is null at activation would also match them in the original, not modelled).
   One base-game record has two parents (`warrior guild`: `Guard building, residents` and `Shop-24hr`, which share an ancestor; **Observed** in the data 2026-10-14); Meitou
   loads them in the listed order and counts only a true cycle as a problem. Whether the original walks every parent is **Unknown**.
3. Entries go into five role lists on the Blackboard (index to offset is identical in `FUN_140268e10` and the sync `FUN_14026b790`,
   and the constructor lays the lists out at that stride):

| Index | Role | Blackboard offset | Filled from |
|---|---|---|---|
| 0 | Squad | +0x1b0 | `Squad AI Goals`, else `Leader AI Goals` |
| 1 | Squad2 | +0x1c8 | `Squad2 AI Goals`, else Squad, else Leader |
| 2 | Leader | +0x198 | `Leader AI Goals` |
| 3 | (unused) | +0x1e0 | never filled by this loader |
| 4 | Slave | +0x1f8 | `Slave AI Goals` only |

### Requests

A Blackboard also holds requests one character makes of another (map at +0x158, keyed by the asker's hand). **Verified** (`FUN_140269ff0` add, `FUN_14026a320`
countdown, `FUN_140269240` reader): an entry has the asker, a task type, a subject hand, a target hand, a tier, a float and a time to live of 3.0 s; the asker
asking itself adds nothing, asking again renews the entry, and the Blackboard update counts the time down by its dt and erases it below 0. The tier pass
(`FUN_140510d60`, [precedence](ai-tasks.md#precedence) step 4) adds the entries of its tier whose subject is none or the character itself as candidates.
The float (1.4 in both callers) is read as the candidate's weight (**Observed**). Callers: `FUN_1403346d0` (`Task_MakeTargetStandStill`, and every update of
the first aid tasks, [medic jobs](ai-tasks.md#medic-jobs)) asks 62 Standing still of the target at tier 3 on the target's platoon Blackboard (`FUN_140791ba0`).
Meitou models only that caller ([medic jobs](ai-tasks.md#medic-jobs)).

### Update cadence and scope (engine choices)

**Verified** (original; `FUN_140787940` -> `FUN_1402e7590` -> `FUN_1406b98d0`, `FUN_140786e30`): every faction keeps a cursor over its loaded platoons and steps exactly one platoon per kick (count x accumulated dt, then the platoon's update slot), one kick per rendered frame, so a platoon updates every N kicks (N = its faction's loaded platoon count); the unloaded list (`+0x230`, cursor `+0x23c`, `FUN_1406b9970`) runs on its own elapsed game time. The logic tick (character slot 29, where the AI update runs) goes to at most 8 characters per frame in round-robin over the active set, so a character thinks every ceil(N/8) frames.
**Meitou (engine choice, deliberate):** `AiSystem.SlowWorld` updates every AI-run squad on a fixed 30-tick cadence (`AiSettings.PackageEveryTicks`) and `AiSystem.Think` runs a character when `(tick + slot) % ThinkEveryTicks (15) == 0` or its mind version changed. The simulated time per platoon agrees; only the granularity and the dependence on faction size and frame rate differ. A faithful port needs a defined kick-per-tick mapping and would make the simulation frame-rate dependent and non-deterministic. A package change thinks at once.

Scope: of the 143 task types with a real `Task_*` class (147 of 290 fall to `Task_Blank`, [taskType to Task class](ai-tasks.md#tasktype-to-task-class)) `TaskBehaviours.Table` maps 49 types to an `AiBehaviour` (17 of them among those 143, the rest are goal markers with no class of their own); `AiSystem.Desire` is 0 for the rest, so those are never chosen as goals by the decision ladder, but for the 21 captive goals (`CaptiveSystem.GoalTypes`, `AiBehaviour.Captive`): a member not taken by the sweep weighs those its sweep turn or its last job's end found startable ([captives](../simulation.md#captives)). The 126 with a class and no behaviour, many acted by other systems (player orders, door and lock work, carrying, first aid, combat), by `TaskType` id: 1, 2, 3, 4, 5, 6, 7, 12, 25, 26, 27, 28, 29, 31, 40, 51, 52, 60, 61, 62, 64, 65, 67, 68, 69, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 87, 92, 93, 95, 96, 97, 98, 99, 100, 102, 103, 107, 108, 110, 116, 119, 121, 123, 124, 129, 133, 134, 136, 140, 147, 150, 156, 159, 161, 162, 163, 164, 165, 166, 170, 174, 176, 181, 182, 185, 186, 196, 198, 199, 200, 201, 207, 208, 213, 219, 220, 221, 224, 225, 226, 227, 228, 229, 231, 235, 236, 240, 241, 244, 246, 247, 249, 253, 256, 257, 258, 261, 262, 263, 264, 266, 267, 269, 272, 274, 278, 280, 284, 285, 286, 287, 289, 290.

## Unknowns

- Which of the six category registries (flag bits 1, 2, 4, 0x10, 0x20, 0x40; [planner](ai-tasks.md#planner)) a character plans with; the meaning of the `*` marker on some
  condition entries (the entry's second flag byte) and of the TaskData flag +0x14.
- The extra x1.25 under an owner-object condition in scoring; the rule that drops a candidate whose B list holds with no effect.
- (Answered: AI +0xbc is the senses' `numEnemies`, [perception](perception.md#senses).) +0x70 of `OrdersReceiver` is the one-shot order list ([perma-jobs](ai-tasks.md#perma-jobs)).
- Why `clears existing jobs` has no string reference in the exe (editor-only field, or read some other way).
- Which AI packages use which `unloaded func` value, beyond the shipping ones (signals 11 and 28: all four have 5 TRAVEL_TARGET, **Verified**
  by a base-data dump of the AI_PACKAGE records). The unloaded update itself, the keep test `FUN_1407ec570`
  and the 60 s orphan timer are in [off-screen world](factions-squads-towns.md#off-screen-world).
- The meaning of `squad mem type` value 3, and Blackboard role list 3. Tiers 0 (JUST_ACTION) and 4 (OBEDIENCE) are never scored by the
  chooser: the normal passes sync role lists with the mask `1 << tier` for tiers 3, 2, 1, and the slave special case in `FUN_14050e5b0`
  syncs role list 4 with mask 0xe (tiers 1..3); what tier 0 and 4 are for is **Unknown**.
- Why 37 task types have no TaskData; types 289 and 290 (no enum name).
- `ending` events (DA/DC behaviour: [dialogue](dialogue.md#unknowns)).
- What squad-template slots 1..4 mean beyond descending priority.
