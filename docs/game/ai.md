# AI

How `kenshi_x64.exe` decides what a character does: the AI_PACKAGE and AI_TASK records, the squad-level `Blackboard`, the per-character
task system, the GOAP planner, jobs and player orders, and how dialogue ties in. Labels as in [../README.md](../README.md).
The per-`taskType` tables (class mapping, TaskData registration, StateType names) are in [ai-tasks.md](ai-tasks.md); this subsystem
is split into those two files.

Sources: the decompile dump and disassembly of `kenshi_x64.exe` (Ghidra default names; addresses like `FUN_140510ef0` cite functions,
class and slot names come from the MSVC RTTI tables) read outside the repository on 2026-10-05; the FCS editor's decompiled enums
(`taskPriority`, `UnloadedPlatoonJob`, `BlackboardSignalFunctions`, `EventTriggerEnum`, `DialogActionEnum`, `DialogConditionEnum`, ...);
`fcs.def` and `fcs_enums.def`; the install's `data/gamedata.base` + `Newwworld.mod` + `Dialogue.mod` + `rebirth.mod` read with the
repo's `GameDatabase` (probes `<RE workspace outside the repo>/probes\ai` and `...\ai-verify`).
No decompiled text is quoted; rules are restated. The game was never run, and no save was inspected for this doc (the install holds none; the one sample save, from the user's save folder, is analysed in [../formats/save.md](../formats/save.md)).
Related: [game-loop.md](game-loop.md) (when the AI updates), [combat.md](combat.md), [character-stats.md](character-stats.md),
[../formats/fcs-mod.md](../formats/fcs-mod.md), [../formats/save.md](../formats/save.md), [factions-squads-towns.md](factions-squads-towns.md) (platoons, squad templates and their `AI packages`, relations),
[pathfinding.md](pathfinding.md) (how a Task_Move is carried out), [buildings-production.md](buildings-production.md) (Task_Build, Task_OperateMachine), [economy.md](economy.md) (Task_Shopping, bounties),
[ui-input.md](ui-input.md#7-mouse-and-selection) (player orders: the context menu verbs are keyed by `taskType`).

## Summary

| Question | Answer | Label |
|---|---|---|
| Layers | Faction/platoon -> `Blackboard` (squad state, package lists, role goal lists) -> `AIPackage` -> per-character `AI` -> `AITaskSytem` -> `Tasker` objects -> GOAP planner | **Verified** (RTTI, constructors, call chains) |
| Priority tiers | 5: JUST_ACTION 0, FLUFF 1, NON_URGENT 2, URGENT 3, OBEDIENCE 4. The chooser makes one pass per tier (3, then 2, then 1, after the job and request lists); the first pass whose best candidate has a feasible plan wins. Scores are compared within a pass, never between passes | **Verified** (enum names and numbers), **Observed** (pass order and the rule) |
| Task score | desire x weight x crowding^n x continuity/commitment modifiers, only inside the task's hour window | **Observed** |
| Goal weight | `val2 x 0.01`, and 0 means 1.0 (RACE `AI Goals` use `val1 x 0.01` instead) | **Verified** (two readers agree, data uses 10, 50, 200) |
| Squad vs character | A package puts task lists on the squad `Blackboard` per role (Leader, Squad, Squad2, Slave); each character takes its role's list | **Verified** |
| Package choice | By slot, highest first; the current package stays until finished (unless an earlier-listed package of the same slot can activate); first activatable otherwise | **Verified** (update function) |

## Data records

Counts are from the merged database (gamedata.base + Newwworld + Dialogue + rebirth). [fcs-mod.md](../formats/fcs-mod.md) quotes 275
AI_PACKAGE and 143 AI_TASK for another load set; the merged database here gives 219 and 124 (**Observed**, same reader).

### AI_TASK

124 records. One task = one `taskType` (the `enum` field, see [ai-tasks.md](ai-tasks.md)), a `classification` tier, a `targeting` and an
`ending`. Only int fields exist (no bools).

| Field | Values | Label |
|---|---|---|
| `enum` | `taskType` 0..288 (289 names in `fcs_enums.def`); 108 distinct types are used | **Verified**: probe over all 124 records, every value is a valid index. The record `Name` is free text (101 of the 124 are descriptive phrases, not the enum name) that reads as matching its enum, except rebirth record 1532845 "Find and kidnap (raider) urgent" (255 SIT_ON_THRONE) and "eating food on the ground" (14 IDLE), which are **Observed** mod quirks |
| `classification` | TP_FLUFF 5 records, TP_NON_URGENT 82, TP_URGENT 37 | **Verified** |
| `targeting` | SPECIFIC 99, SELF 11, LEADER 4, SQUAD_MISSION 9, HOME_GATE 1 | **Verified** (counts). Behaviour **Observed** (goal reader `FUN_1402704e0`): LEADER takes the squad leader's hand (`FUN_14079aea0`), SQUAD_MISSION the Blackboard's mission target (`FUN_140269b60`), HOME_GATE a Blackboard lookup (`FUN_14026b410`); SPECIFIC and SELF set nothing at load time (SELF is filled with the character's own hand where the task is created) |
| `ending` (TaskEndEvent) | NOTHING 115, REMOVE_MINE_ONLY 8, REMOVE_WHOLE_SQUADS 1 | **Verified** (counts), behaviour **Unknown** |

### AI_PACKAGE

219 records: 96 `inherits from` another, 2 `Leads to` another, 23 `clears existing jobs` = true. Reference lists: `Leader AI Goals`,
`Squad AI Goals`, `Squad2 AI Goals`, `Slave AI Goals` (1487 entries in total: Leader 804, Squad 561, Squad2 58, Slave 64, none
unresolved), plus the `contract end talk passive` and `contract end dialog delivery` dialogues (fcs.def: the delivery one wins when
present, the passive one is the fallback when the player is KO or unreachable), `signal func`, `unloaded func`, and two int fields
`start time` / `end time` seen on some records (not read by the AI code found). Each goal entry is a triple (val0, val1, val2) on
the reference to an AI_TASK:

- val0 = start hour, val1 = end hour (window: (0,24) 1027 entries, (0,100) 338, (0,0) 14, night shifts such as (23,7)). 100 means "no
  limit" (the runtime defaults are 0 and 100). **Verified**: fcs.def describes them as the start and finish hour of day, the goal reader
  `FUN_1402704e0` passes val0/val1 straight to the entry's start/end fields and the scorer `FUN_14050df10` tests the hour against
  them. Because the window rule treats start >= end as "wraps midnight" (see Scoring), (0,0) is also always in window (**Observed**).
- val2 = weight x 100, 0 meaning 1.0 (val2 = 0 in 1145 entries, else 10, 50, 200, ...). **Verified**: the goal reader `FUN_1402704e0`
  (called from `FUN_140270850`) applies `x 0.01` with the zero default, and the data histogram (1145 zeros, then 10 x69, 200 x63,
  50 x44, 100 x27, ...) fits a percentage.
- A legacy `AI Goals` field is also read into lists 0, 1 and 2 (`FUN_140270850`), but 0 of the 219 records have it (**Observed**).
- `clears existing jobs`: the string does not occur anywhere in the exe (raw byte search, while `unloaded func` and
  `contract end talk passive` each occur once), so the game never reads the field by name (**Observed**). What does exist is the
  behaviour itself: starting a contract package and `change AI` on a non-player squad delete every member's job list unconditionally
  (`FUN_140507280` per member in `FUN_140272240` and `FUN_140272430`), see [Dialogue and AI](#dialogue-and-ai).

`signal func` use: 0 x122, 14 x19, 6 x9, 12 x8, ... ; `unloaded func` use: 0 x78, 2 x15, 4 x61, 5 x44, 6 x21 (1 and 3 unused).

### Other records that touch the AI

The squad templates, factions and towns behind these fields are described in [factions-squads-towns.md](factions-squads-towns.md#4-squad-templates).

| Record / field | Role | Label |
|---|---|---|
| SQUAD_TEMPLATE `AI packages` (also 1 of 11 SINGLE_DIPLOMATIC_ASSAULT records, entry (0,100,0)) | 838 of 959 templates; each entry (slot, ...) with slot 0..4 (838 at 0, of which 777 are (0,0,0) and 61 are (0,100,0); 98 at 1, 55 at 2, 8 at 3, 2 at 4), at most 5 packages per template. The slot is a priority: the Blackboard walks slots from the highest to 0. The reader (`FUN_140271620`) passes the entry's first int as the slot to the add-package function `FUN_14026c160`, then resolves every package's `Leads to` among the packages it just loaded (a `Leads to` target that is not one of the squad's own packages stays unlinked) | **Verified** (counts), **Observed** (priority role, `Leads to` resolution) |
| RACE `AI Goals` | 47 of 53 races; entries (SELF_PRESERVATION x30, HEAL_MY_LEGS x16, IDLE x9, EAT_A_RANDOM_DEAD_BODY x7, ...). Read by `FUN_1405094f0`: only val1 x 0.01 is used (no zero default; the data is mostly val1 = 100, i.e. 1.0), val0 is **not** read (the entries are mostly (4,100,0) or (0,100,0)), the hour window is fixed 0..100, and the target comes only from the legacy bool fields `target leader` / `target self` / `squad mission target`, which no current AI_TASK record has (AI_TASK has only `enum`, `classification`, `targeting`, `ending`), so the `targeting` int is ignored on this path | **Verified** (reader and data), **Observed** (data) |
| FACTION / FACTION_TEMPLATE `AI fallback` | Packages added at slot 0 (`FUN_140271fd0`); used by 1 FACTION record and 0 FACTION_TEMPLATE records | **Observed** |
| DIALOGUE_LINE `AI contract` (169 uses; val0 = hours), `change AI` (190 uses), `has package` (751 uses) | See [Dialogue and AI](#dialogue-and-ai) | **Observed** |
| AI_SCHEDULE | Empty in the base game set (0 records in the merged database); fcs.def says its `packages` list switches packages by hour (val0), but no runtime use was found | **Verified** (count) |

## Enums

| Enum | Values | Source, label |
|---|---|---|
| `taskPriority` | TP_JUST_ACTION 0, TP_FLUFF 1, TP_NON_URGENT 2, TP_URGENT 3, TP_OBEDIENCE 4, TP_MAX_SIZE 5 | **Verified**: the FCS editor's enum (decompiled, outside the repo; it is not in `fcs_enums.def`); `OrdersReceiver` holds two arrays of five lists (constructor `FUN_140507d20`, indexed at +0xa0 and +0x118 plus tier x 0x18 in `FUN_140510d60`); data uses only 1, 2, 3 |
| `taskType` | 289 names | `fcs_enums.def`; see [ai-tasks.md](ai-tasks.md) |
| `TaskTargetType` | SPECIFIC, SELF, LEADER, SQUAD_MISSION, HOME_GATE | `fcs_enums.def` |
| `TaskEndEvent` | NOTHING, REMOVE_MINE_ONLY, REMOVE_WHOLE_SQUADS | `fcs_enums.def` |
| `UnloadedPlatoonJob` | NONE 0, PATROL_TOWN 1, PATROL_SHORTRANGE 2, PATROL_LONGRANGE 3, GOHOME 4, TRAVEL_TARGET 5, TRAVEL_TARGET_FAST 6 | The FCS editor's enum (not in `fcs_enums.def`). The numbering of 2, 4, 5 and 6 agrees with the unloaded update's behaviour per value (see below); 1 and 3 have no branch there (**Observed**) |
| `BlackboardSignalFunctions` | see the table below | **Verified**: the FCS editor's enum (not in `fcs_enums.def`) and the RTTI package classes built by the factory agree value by value |
| `EventTriggerEnum` | 0 EV_NONE, 1 PLAYER_TALK_TO_ME, 3 I_SEE_NEUTRAL_SQUAD, 13 UNLOCK_MY_CAGE_OR_SHACKLES, 15 I_DEFEATED_SQUAD, 30 CONTRACT_JOB_ENDED, ... | The editor exe's numbering is authoritative. `fcs_enums.def` lists the names in a different order (it starts with EV_PLAYER_TALK_TO_ME, no EV_NONE). Cross-check: the dialogue `dialogs` val0 histogram (event 3 x294, event 1 x220, 13 x147, 15 x87) and the binary firing 30 on contract end (`FUN_14026a110`). Whether the two lists differ only in order is **Unknown** |
| `DialogActionEnum` (DA_*), `DialogConditionEnum` (DC_*), `DialogRepetitionEnum`, `CharacterTypeEnum`, `TalkerEnum`, `PersonalityTags` | Names and numbers | From the FCS exe decompile (`probes\characters-fcs-decomp`), enum names and values only |

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

The base `AIPackage` object is 0xb0 bytes; subclasses are bigger (0xb8, 0xc8, 0xe8, 0x108, 0x158). Fields (**Observed**, read in the
Blackboard functions): record +0x68, index in the slot list +0x70, signal +0x74, unloaded func +0x78, "leads to" package pointer
+0x90, contract-end dialogs +0x98 / +0xa0; two or three `hand` objects at +0x08, +0x28, +0x48. The interface has 19 slots (RTTI):
slot 12 `canActivate` (base returns true), slot 13 `isFinished` (base false), slot 18 gates the `Leads to` hop (base returns true)
(**Verified**: base-class slot bodies read, and the three slots are the ones the slot selector calls), slot 15 builds a
`JobsPanel::JobInfo` row in a global set for the package when its target hands resolve (**Observed**).

## Classes and roles

All **Verified** by RTTI (`rtti.tsv`) unless noted; roles are **Observed** from constructors and callers.

| Class | Role |
|---|---|
| `Blackboard` | Squad-level AI state: package lists by slot, five role goal lists, task requests, task infos, current package, contract package, town targets |
| `AIPackage` and subclasses | One behaviour of a squad (patrol, war, contract, ...); loads goal lists into the Blackboard when activated |
| `AI` (74 virtual slots) | Per-character AI object at Character +0x650; slot 4 is the per-update entry |
| `AITaskSytem` (sic) | Per-character decision maker, derives `OrdersReceiver` (its constructor calls `FUN_140507d20`); at AI +0x20 |
| `OrdersReceiver` | Holds the job list, an extra Tasker list, the per-tier AI goal lists and the role lists synced from the Blackboard; saves `pjobT<i>` |
| `AITaskSytem::ActionsListMgr`, `ActionDeque` | Plan and action queues |
| `TaskRepertoire` | Factory of `Task_*` objects (AI +0x2a8; vtable `1416bd7d0`) |
| `GOAPTaskMgr` | The planner state (vtable `1416bc1d8`) |
| `Tasker` and `Task_*` | One runnable task. Classes include Task_Move, Task_Build, Task_Loot_AI, Task_Loot_Order, Task_Pickup, Task_MeleeAttack, Task_FocusedMeleeAttack, Task_RangedAttack, Task_Patrol, Task_PatrolTown, Task_Follow, Task_FollowAndTalk, Task_Runaway, Task_StandAtNode, Task_UseBed, Task_UseCage, Task_Shopping, Task_PlayerTalkto, Task_TalktoPlayer, Task_MakeAnnouncement, Task_Blank (full list in [ai-tasks.md](ai-tasks.md)); movement tasks are carried out as in [pathfinding.md](pathfinding.md#movement), combat tasks as in [combat.md](combat.md), building and machine tasks as in [buildings-production.md](buildings-production.md#construction), shopping as in [economy.md](economy.md#8-buying-selling-fencing) |
| `MissionClass`, `MissionEscort`, `CombatClassAI` | Mission targets and combat AI helpers |
| `FactionRelations` | Faction-level relation lookups used by senses |
| `JobsPanel` (`JobInfo`) | The UI list of squad packages and contracts (a set of `JobInfo` rows filled by package slot 15) |
| `DialogLineData`, `DialogAction`, `DialogCondition`, `DialogueWindow`, `DialogueSpeechBubble` | Dialogue line data and its UI |
| `GroupSense`, `TagsClass<SenseType>`, `SubjectiveTags`, `TagsClass<CharacterPerceptionTags_ShortTerm/LongTerm>` | Senses (see below) |
| `UnloadedPlatoon` | Simplified behaviour of an unloaded squad |

## Goal loading

Chain (**Verified** by reading the functions and by data counts):

1. A package is activated (`FUN_1402721a0`), which clears the Blackboard's role lists 0, 1, 2 and 4 (list 3 is not cleared), marks the
   package as just activated, then calls the goal loader (`FUN_140270850`).
2. `inherits from` is processed first, recursively, so a parent's entries are added first. Adding goes through `FUN_140268e10`, which
   calls `FUN_14027b3a0`; that skips an entry whose task type and target hand equal one already in the list, so **the parent's
   entry wins**.
3. Entries go into five role lists on the Blackboard (index to offset is identical in `FUN_140268e10` and the sync `FUN_14026b790`,
   and the constructor lays the lists out at that stride):

| Index | Role | Blackboard offset | Filled from |
|---|---|---|---|
| 0 | Squad | +0x1b0 | `Squad AI Goals`, else `Leader AI Goals` |
| 1 | Squad2 | +0x1c8 | `Squad2 AI Goals`, else Squad, else Leader |
| 2 | Leader | +0x198 | `Leader AI Goals` |
| 3 | (unused) | +0x1e0 | never filled by this loader |
| 4 | Slave | +0x1f8 | `Slave AI Goals` only |

4. Each entry is turned into a `Tasker` (`FUN_140330530` -> factory `FUN_14032ebd0`) with the entry's tier, target, weight, start and
   end hour. The Tasker stores tier +8, target hand +0x10 (hand fields from +0x18), weight +0x30, position +0x58, start +0x64, end
   +0x68, TaskData +0x70 (**Verified**: the factory writes these and the scorer reads them).
5. When a character reaches a tier pass, `FUN_140510d60` calls the sync `FUN_14026b790`, which filters the role list by the pass's tier
   bit and rebuilds the character's Taskers for that tier if the cached ones differ in task types; it also turns the Blackboard's
   TaskRequests into taskers (`FUN_140269240`). **Observed**.

A character's role index lives at `AITaskSytem` +0x25c: loaded from the save field `squad mem type` (a saved 2 becomes 0;
`FUN_140626f50`, which RTTI names `CharacterAnimal::vfunc_18`: the same load code exists in other Character classes), treated as 2 for
the platoon leader inside the choose pass (`FUN_140510ef0` overrides it locally; the stored field is not changed there), and set to
0 or 2 by the generic setter `FUN_1406215c0` (callers pass 0, 2 and, in slave-making code, 4). Meaning of saved value 3 is
**Unknown**.

## Blackboard update

`FUN_1402732b0` (**Verified** by reading; run for each platoon):

- First it refreshes the squad's member counts and nearest town (`FUN_140268a80`, `FUN_1402688d0`). Then slots are walked from the
  highest slot ever added (Blackboard +0x148, raised by `FUN_14026c160` whenever a package is added) down to 0 (`FUN_140272570` per
  slot) until a slot reports success; finally the current package's update (interface slot 9) runs (the whole pass is skipped while a
  flag at +0x14c is set). A faction campaign entry injects its package at slot 24 (`FUN_140272570` adds it before selecting;
  **Observed**).
- A running contract package (Blackboard +0x210, saved as `contractjob`) takes precedence over every slot. When it is finished,
  dialogue event 30 (CONTRACT_JOB_ENDED) fires (`FUN_14026a110`: dialogue manager event 0x1e, then the package's contract-end dialog
  at package +0x98 if set).
- In a slot (**Verified**: `FUN_140272570` read, cross-checked against the slot-12/13/18 base bodies in the RTTI vtable, the max-slot field
  +0x148 raised in `FUN_14026c160` and used by `FUN_1402732b0`, the flag +0x130 set in `FUN_1402721a0`, and the `Leads to` pointer +0x90
  written by `FUN_140271620`): the list is scanned in order. An entry that is the current package: if not finished it
  stays (re-activated first if the "just changed" flag +0x130 is set) and the slot returns success; if finished, its `Leads to`
  package (+0x90) is started when interface slot 18 allows it (success), else the slot returns failure and lower slots are tried.
  An entry that is not the current package and whose `canActivate` is true is activated at once, so an earlier-listed activatable
  package pre-empts the current one. A lone slot-0 package is auto-activated (even when its `canActivate` is false).
- `unloaded func` is read when the package object is built from its record (factory `FUN_140285ba0`, stored at package +0x78); it is
  what the unloaded squad uses. `contract end talk passive` and `contract end dialog delivery` are read there too.

Blackboard layout (**Observed**): constructor `FUN_14026bcf0`; slot -> package-list map at +8; task type -> hand map near +0xb0; hand ->
`TaskRequest` map at +0x158 (default weight 3.0, set in `FUN_140269ff0`); hand -> `TaskInfo` map at +0x220 (who does what, used for
crowding); five role lists at +0x198, +0x1b0, +0x1c8, +0x1e0, +0x1f8; current package +0x48 (its index at +0x50); current town +0x138;
target town +0xf0; mission data +0x100; contract package +0x210 and job hours +0x218. The role-list offsets are **Verified** (see Goal
loading).

## The per-character decision loop

**Chain.** `AI::vfunc_4` (`FUN_1405112b0`) clears caches, updates senses, runs vslot 9, then calls the task system's think
(`FUN_140511130`) and clears caches again. It runs once per character AI update; scheduling is in [game-loop.md](game-loop.md).

**Choose** (`FUN_140510ef0`; parameter = "restricted", forced false when there is no current task; true when the current task is sticky
and unexpired). Order (**Observed** from the function's control flow):

1. Override check (`FUN_140510920`).
2. If restricted and the current tier is above 2, stop (keep the current task).
3. Score the extra Tasker list at +0x70 when it is non-empty (role **Unknown**) into the shared candidate map.
4. If the job list (+0x88, count +0x90) is non-empty: build candidates from jobs only if their TaskData urgent flag (+4 == 2) is set
   and an AI flag (+0xbc) is non-zero (`FUN_14050e5b0`, flag 1), pick, and commit when one passes; otherwise clear the map.
5. Tier 3 (URGENT): score the TaskRequest list, the squad role list for this tier and the character's own list into the map
   (`FUN_140510d60`), pick.
6. If restricted and the current tier is above 1, stop.
7. All jobs (`FUN_14050e5b0`, flag 0), pick.
8. Tier 2, pick.
9. If restricted and the current tier is above 0, stop.
10. Tier 1, pick.
11. Default task (+0x190) at score 1.0.
12. Fallback through a virtual slot 0x98 of an object reached from the character.

The first step that produces a task which passes the planner wins. Scores are compared within a pass (when the job list is empty the
step 3 candidates stay in the map and compete with the tier-3 candidates). **Pick** (`FUN_140510650`) walks a float-keyed map from
the highest key down; a candidate needs score > 0 and a feasible plan (`FUN_14050fe10`) and becomes current through
`AITaskSytem::vfunc_5` (`FUN_14050caf0`), which also records the `TaskInfo` on the Blackboard (`FUN_140269e90`). Job candidates get
keys count+8-i (decreasing by 1 per entry), so they outrank ordinary scored ones in practice.

### Scoring

`FUN_14050df10` (**Observed**):

Filters: skip if the hour is outside the window (start < end: start <= h < end; start >= end: h >= start or h < end; the hour is the
sky controller's integer hour, see [game-loop.md](game-loop.md)); skip if the TaskData dialogue event (+0x130) is set and the
character's dialogue manager (character +0x280) lacks it (the game logs "is missing dialog event"); skip a candidate that is already
done: its condition list A is satisfied and two further checks pass (`FUN_14050c8e0`, a virtual slot 0x20 of the Tasker); for
request lists the Blackboard is also told (`FUN_140268860`).

Formula:

score = desire(task, character) × weight × crowdFactor^n × m

where n is the number of squad-mates doing the same type and target (`TaskInfo`) and m is 1.25 when the candidate has the same type, target and second hand as the current task, else 1.

- `desire` is the TaskData member function (+0x108, this-adjust +0x110; `FUN_14060fd80`), times a factor from evaluating the effect state
  (+0x128) when there is an effect and the desire is positive.
- For the candidate that is the current task: x 1.25, then, if the TaskData is sticky (+0x20): floor 0.4 while the commitment timer has
  not expired, cap 0.05 afterwards. The timer is at AITS +0x280 in game time (hours): when `vfunc_5` makes a *different* sticky task
  current the timer restarts at the current time, and in every case base (+0x18) + rand x range (+0x1c) are added to it; tested by
  `FUN_140791c20`.
- A further x1.25 applies, only to the current-task candidate, if the score is below 1 under an owner-object condition (**Unknown** which).
- A candidate that scores 0 or less is dropped; if the TaskData flag +0x14 is clear and its list B holds with no effect to achieve, it
  is dropped too (**Observed**; why is **Unknown**).
- Ties are broken by adding 0.0001 to the key until it is free.

Squad toggles in the global squad-order settings (DAT_142134690, the PlayerInterface object of [ui-input.md](ui-input.md#7-mouse-and-selection), +0x116 "Sleep when injured", +0x117 "Ditch items", +0x118 "Sit when
idle", named by the aipanel tooltips in `FUN_140494ce0`) inject, for characters of a player squad (its faction's `+0x250` non-null, the player-faction flag of [factions-squads-towns.md](factions-squads-towns.md#1-object-model); the object tested is what Character virtual slot 0x58 returns, which falls back to the faction `nofac`, so it is the faction and not the squad: **Verified** by reading `FUN_14050e5b0` and that slot's body) in the all-jobs pass, GO_HOME_AND_GO_TO_BED
plus GET_OUT_OF_BED_ONCE_HEALED (+0x116), DITCH_ALL_RESOURCES (+0x117) and SIT_AROUND (+0x118) into the job-candidate pass
(`FUN_14050e5b0`). **Verified**: the toggle-to-label mapping from the tooltip strings and the task numbers from the factory calls
(0x99 = 153, 0x101 = 257, 0xf2 = 242, 0x113 = 275). `_SLAVE_OBEDIENCE` (187) has a special case there: it syncs the Slave role list
(index 4) onto the character.

### Default tasks per character

`FUN_140621b70` (**Observed**): IDLE (tier 1, weight 0.1), SELF_PRESERVATION (tier 3, weight 1.0, target self), the `AI Goals` of
a record that has that field (only RACE does; via `FUN_1405094f0`): the faction's record (`+0x240` of the faction object returned by Character virtual slot 0x58, **Verified** in `FUN_140621b70`; the base data has no `AI Goals` field on FACTION, so it adds nothing there) when the squad has no
contract package, and the character's own race (always for non-player squads, for player squads only when virtual slot 0x248
returns non-null), and for player-faction characters AQUIRE_FOOD_AT_HOMEBASE (243) and EAT_FOOD_ON_GROUND (259), both tier 3, weight 0.5 (the hunger model they serve: [character-stats.md](character-stats.md#hunger-and-starvation)).

## GOAP planner and TaskData

- **TaskData** (0x178 bytes, `FUN_14060ea00`) is the static description of a `taskType`, held in a global map (`DAT_141ce90f0`, accessor
  `FUN_140281fd0`) built by the 64 KB function `FUN_140319a60`. It holds the desire function, commitment times, crowding factor,
  condition lists and one effect. Table in [ai-tasks.md](ai-tasks.md). **Verified** (disassembly parse, re-checked against the decompile).
- Conditions are `StateType` evaluators registered by `FUN_140610250` (153 ids, 151 of them named: isAtLocation, hasWeapon, isInBed,
  isMovementAllowed, crowdLimit8, ...); failure-speech strings are attached (e.g. "It's locked."). **Observed**.
- A condition list B holds at most 11 states ("too many states, increase MAX_STATES" is the failure message). **Verified**: the add
  function `FUN_14060e940` refuses a 12th entry and logs that string.
- Condition list A (TaskData +0x4c, checked by `FUN_14060f570`) is tested for "satisfied"; list B (+0xa8, checked by `FUN_14060f940`)
  returns the first unmet state id to the planner. Both cache their result per (TaskData, target, position). **Observed**.
- The planner (`FUN_14050f800`, `FUN_14050fe10`) builds a plan deque from unmet conditions; plan steps become sub-Taskers through the
  factory. `AITaskSytem` +0x268 receives the TaskData's dialogue event (+0x130) of the candidate being scored (cleared at the start of
  each choose pass). **Observed**; the search method itself is **Unknown**.

## Jobs and player orders

- `OrdersReceiver` keeps the job list (+0x88); each job is saved as `pjobT<i>` (its task type and target, `FUN_140508bb0`), and the
  task system saves `tjobT` / `tjobP` (GAMESTATE_AI), matching [save.md](../formats/save.md). **Verified** (field names in writers).
- Jobs are scored before the tier lists (see the chain above), so they pre-empt AI goals. Jobs run on the job list only if urgent in
  the first pass, then again with the rest after tier 3.
- The Blackboard saves currentPackage, target town, towntime, mission data, currenttown, jobhr, contractjob, replacementAI, homelok and
  runningAwayMode (`FUN_14026c2d0`). **Verified** (field names in the writer).

## Squad-level versus character-level

Squad level: package choice by slot, the role lists, task requests (squad-wide wishes with default weight 3.0), crowding counts
(`TaskInfo`), contracts and the unloaded mode. Character level: scoring, sticky commitments, the planner, senses, default tasks. The
two meet in step 5 of the choose chain, where the squad's role list for the character's tier is merged with the character's own list.
All **Verified** by structure.

## Unloaded squads

`UnloadedPlatoon::vfunc_2` (`FUN_1407ee790`). The mode is the squad's campaign entry's, else the current package's `unloaded func`
(`FUN_140268840` reads package +0x78 and gives 0 without a package). A mode of 0 becomes 2 or 4 depending on the result
of `FUN_1407ec570` (meaning **Unknown**). Mode 2 patrols short range (picks a new point when within 500 units of the last), mode 4
goes home (re-issues the move when within 2000 units of the target), modes 5 and 6 travel to the mission target (the speed factor differs per mode and campaign entry); modes 1 and 3 have no branch. The update accumulates its own frame time and reports finished after 60 s in a state
where `FUN_1407ec570` is false. **Observed**. How the platoon objects hold the loaded and unloaded halves and when a squad is unloaded: [game-loop.md](game-loop.md#factions-and-squads); the stand-in speeds 25 / 55 / 60 / 90 are also in [pathfinding.md](pathfinding.md#off-screen-and-far-characters).

## Senses

Debug panel `FUN_1407981b0` lists (**Observed**): SENSE_CANT_SEE, KO, ENEMY, ALLY, NEUTRAL, CRAWLING; subjective tags ST_AGGRESSOR,
ST_INTRUDER, ST_PRISONER, ST_CRIMINAL; long-term perception LT_MY_INTRUDER, LT_MY_LIFESAVER, LT_FREED_ME, LT_STOLE_FROM_ME,
LT_DEFEATED_MY_SQUAD_ONCE, LT_SQUAD_LOST_TO_ME_ONCE, LT_KILLED_MY_FRIEND, LT_I_SCREWED_THIS_GUY, LT_MY_CAPTOR, LT_FRIENDLY_AQUAINTANCE.

## Dialogue and AI

- A TaskData can require a dialogue event (+0x130); characters lacking it skip the task (**Observed**).
- DIALOGUE_LINE `AI contract` starts a contract package for N hours (val0 = hours; the data holds 0 x4, 1 x79, 2 x5, ... up to 192 and
  one 999999). `FUN_140272240` builds the package from the record's `signal func` (0 becomes 33, Contract_Timed), sets the contract end
  time to now + N hours (N < 1 gives 10 minutes), clears every member's job list for non-player squads, and activates it. `change AI`
  swaps the squad's package, only for non-player squads (`FUN_140273330` -> `FUN_140272430`, which also clears the role lists and
  the members' job lists and adds the package at slot 0). **Observed**.
- Contract end fires event 30 (CONTRACT_JOB_ENDED) and plays the package's contract-end dialog (**Observed**, see the Blackboard update above and the `EventTriggerEnum` row).
- Dialogue effects run in `FUN_140680560` (lock/unlock, relations, rewards; **Observed**; the relation slots it calls are in [factions-squads-towns.md](factions-squads-towns.md#34-changing-relations-observed-the-event-table-cross-checked-with-the-dialogue-action-numbers), money and bounty effects in [economy.md](economy.md#11-bounties-and-crimes)). The condition evaluator is probably
  `FUN_140676950` (**Unknown**).

## Unknowns

- Roles of the six category sets that flag bits 1, 2, 4, 0x10, 0x20, 0x40 register into; the meaning of the `*` marker on some
  condition entries (the entry's second flag byte) and of the TaskData flag +0x14.
- The extra x1.25 under an owner-object condition in scoring; the rule that drops a candidate whose B list holds with no effect.
- What the extra Tasker list at +0x70 of `OrdersReceiver` holds, and the "AI flag +0xbc" gating urgent jobs.
- Why `clears existing jobs` has no string reference in the exe (editor-only field, or read some other way).
- What the mode-0 test `FUN_1407ec570` and the finished-after-60-s rule mean for unloaded squads; PATROL_TOWN / PATROL_LONGRANGE
  (1 and 3) have no branch in the unloaded update.
- The meaning of `squad mem type` value 3, and Blackboard role list 3. Tiers 0 (JUST_ACTION) and 4 (OBEDIENCE) are never scored by the
  chooser: the normal passes sync role lists with the mask `1 << tier` for tiers 3, 2, 1, and the slave special case in `FUN_14050e5b0`
  syncs role list 4 with mask 0xe (tiers 1..3); what tier 0 and 4 are for is **Unknown**.
- Why 37 task types have no TaskData; types 289 and 290 (no enum name).
- Exact behaviour of each DA_ effect and DC_ condition; `ending` events.
- Whether `EventTriggerEnum` in `fcs_enums.def` differs from the exe numbering only in order.
- What squad-template slots 1..4 mean beyond descending priority.
- The planner's search method and costs.

## Implementation outline

1. Load AI_TASK / AI_PACKAGE through `GameDatabase` (`FcsRecordType.AI_TASK`, `AI_PACKAGE`), resolving `inherits from` first with
   parent-wins dedup by (taskType, target); weight = val2 x 0.01 or 1.0 (RACE `AI Goals`: val1 x 0.01, window 0..100).
2. Model `TaskData` as a table keyed by `taskType` (from [ai-tasks.md](ai-tasks.md)); start with the 143 types that have real classes.
3. Implement `Blackboard` per platoon: slots (templates 0..4, campaign 24), role lists, update by descending slot (current package
   stays unless an earlier entry can activate), contract precedence, `Leads to` resolved among the squad's own packages.
4. Implement the choose chain with the 5 tiers, hour windows (start >= end wraps midnight), crowding and commitment; treat the scoring
   constants as tunable.
5. Add default tasks, then a minimal planner (unmet condition -> sub-task), then unloaded modes 2, 4, 5, 6.
6. Run on the character update thread described in [game-loop.md](game-loop.md); save `pjobT`, `tjobT`, Blackboard fields per
   [save.md](../formats/save.md).
