# AI task types and TaskData table

Companion to [ai.md](ai.md): the per-`taskType` tables behind the AI subsystem. Labels as in [../README.md](../README.md).
Where the task classes are used: [pathfinding.md](pathfinding.md#movement) (Task_Move and friends), [combat.md](combat.md) (melee, ranged, equip and first-aid tasks),
[buildings-production.md](buildings-production.md#construction) (Task_Build, Task_AddMaterialsToBuilding, Task_OperateMachine), [economy.md](economy.md#8-buying-selling-fencing) (Task_Shopping),
[ui-input.md](ui-input.md#7-mouse-and-selection) (the player's context-menu verbs are keyed by these `taskType` numbers) and [factions-squads-towns.md](factions-squads-towns.md#4-squad-templates) (the squad templates that choose AI packages).

Sources: `kenshi_x64.exe` decompile dump and disassembly read outside the repository on 2026-10-05 (addresses like `FUN_14032ebd0` cite
functions); `fcs_enums.def` (taskType names); the install's merged database read with `GameDatabase` (probe
`R:\VlcekM\MeitouClient-re\probes\ai`). No decompiled text is quoted; the tables are facts read out of the registration code and a
jump table.

## taskType to Task class

**Verified** (the Tasker factory `FUN_14032ebd0` switches on a byte table at `140330408` indexed by taskType - 1; the table was read
from the exe bytes with a probe, each case body was matched to the RTTI `Task_*` class its constructor installs, and the resulting
type-to-class list agrees with this table for all 290 rows, including the 147 default cases; the last column re-counted from the
merged database: 108 distinct types, the same per-type record counts and tier/target/ending combinations). 290 jump-table entries versus
289 names in `fcs_enums.def`: types 289 and 290 build `Task_Follow` and `Task_BashDoor` and have no enum name (**Unknown** why).
147 types fall through to `Task_Blank` (the factory's default case): no behaviour of their own from this factory.
The last column says whether any AI_TASK record (merged database) uses the type.

| # | taskType | Task class | AI_TASK use |
|---|---|---|---|
| 1 | `MOVE_ON_FREE_WILL` | Task_Move | - |
| 2 | `BUILD` | Task_Build | - |
| 3 | `PICKUP` | Task_Loot_AI / Task_Pickup | - |
| 4 | `MELEE_ATTACK` | Task_MeleeAttack | - |
| 5 | `FOCUSED_MELEE_ATTACK` | Task_FocusedMeleeAttack | - |
| 6 | `EQUIP_WEAPON` | Task_EquipBestWeapon | - |
| 7 | `UNEQUIP_WEAPON` | Task_SheatheWeapon | - |
| 8 | `FIND_WEAPON` | (goal; Task_Blank) | - |
| 9 | `CHOOSE_ENEMY_AND_ATTACK` | (goal; Task_Blank) | - |
| 10 | `CHOOSE_ATTACKER_OF_ALLY` | (goal; Task_Blank) | - |
| 11 | `ATTACK_CHARACTERS_ATTACKER` | (goal; Task_Blank) | - |
| 12 | `PLAYER_TALK_TO` | Task_PlayerTalkto | - |
| 13 | `ATTACK_ATTACKERS_OF` | (goal; Task_Blank) | - |
| 14 | `IDLE` | (goal; Task_Blank) | 2 (NON_URGENT/SPECIFIC, FLUFF/SPECIFIC) |
| 15 | `PROTECT_ALLIES` | (goal; Task_Blank) | 2 (URGENT/SELF, NON_URGENT/SPECIFIC) |
| 16 | `ATTACK_ENEMIES` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 17 | `PROTECTION` | (goal; Task_Blank) | - |
| 18 | `RAID_TOWN` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 19 | `GO_HOMEBUILDING` | (goal; Task_Blank) | - |
| 20 | `STAND_AT_SHOPKEEPER_NODE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 21 | `ATTACK_ENEMIES_AND_NEUTRALS` | (goal; Task_Blank) | 2 (URGENT/SPECIFIC, NON_URGENT/SPECIFIC) |
| 22 | `PATROL` | Task_Patrol | 1 (NON_URGENT/SPECIFIC) |
| 23 | `ATTACK_TOWN` | (goal; Task_Blank) | - |
| 24 | `WANDERER` | (goal; Task_Blank) | - |
| 25 | `FIRST_AID_ORDER` | Task_FirstAid | - |
| 26 | `LOOT_TARGET` | Task_Loot_Order | - |
| 27 | `CROUCH` | Task_Crouch | - |
| 28 | `STAND_UP` | Task_UnCrouch | - |
| 29 | `MOVE_CUS_ORDERED` | Task_Move | - |
| 30 | `HOLD_POSITION` | (goal; Task_Blank) | 1 (NON_URGENT/SELF) |
| 31 | `STAY_CLOSE_TO_TARGET` | Task_Follow | - |
| 32 | `SELF_PRESERVATION` | (goal; Task_Blank) | 1 (URGENT/SELF) |
| 33 | `QUELL_AGGRESSION` | (goal; Task_Blank) | - |
| 34 | `ATTACK_TROUBLE_MAKERS` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 35 | `RUN_AWAY` | Task_Runaway | 1 (URGENT/SELF/REMOVE_MINE_ONLY) |
| 36 | `PATROL_TOWN` | Task_PatrolTown | 2 (NON_URGENT/SPECIFIC, FLUFF/SPECIFIC) |
| 37 | `WANDER_TOWN` | (goal; Task_Blank) | - |
| 38 | `STAND_AT_GUARD_NODE_HOMEBUILDING` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 39 | `WANDERING_TRADER` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 40 | `GET_NEAR_TO` | Task_Move | - |
| 41 | `ATTACK_ENEMIES_OF_MY_SLAVEMASTER` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 42 | `NOT_BE_UNARMED` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 43 | `STAY_IN_HOME` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 44 | `FOLLOW_PLAYER_ORDER` | Task_Follow | 1 (NON_URGENT/SQUAD_MISSION) |
| 45 | `BODYGUARD` | (goal; Task_Blank) | 2 (URGENT/LEADER, URGENT/SQUAD_MISSION) |
| 46 | `CHASE` | (goal; Task_Blank) | - |
| 47 | `STAND_AT_GENERAL_NODE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 48 | `STAND_AT_DEFENSIVE_NODE` | (goal; Task_Blank) | - |
| 49 | `STAND_AT_BUILDING_GUARD_NODE` | (goal; Task_Blank) | - |
| 50 | `STAND_AT_BUILDING_DEFENSIVE_NODE` | (goal; Task_Blank) | - |
| 51 | `STAND_AT_NODE` | Task_StandAtNode | - |
| 52 | `__aoeu__` | Task_GetUp | - |
| 53 | `TRAVEL_TO_TARGET_TOWN` | Task_TravelToTargetTown | 1 (NON_URGENT/SPECIFIC) |
| 54 | `REST` | (goal; Task_Blank) | - |
| 55 | `RECRUIT_AT_JOBCENTER` | (goal; Task_Blank) | - |
| 56 | `SWITCH_FOLLOW_ME_MODE_ON` | (goal; Task_Blank) | - |
| 57 | `JOB_REPAIR_ROBOT` | (goal; Task_Blank) | - |
| 58 | `JOB_MEDIC` | (goal; Task_Blank) | 2 (URGENT/SELF, NON_URGENT/SELF) |
| 59 | `GET_READY_FOR_ACTION` | (goal; Task_Blank) | - |
| 60 | `FIRST_AID_ROBOT` | Task_FirstAid | - |
| 61 | `UNPROVOKED_FOCUSED_MELEE_ATTACK` | Task_FocusedMeleeAttack | - |
| 62 | `STAND_STILL` | Task_StandStill | - |
| 63 | `SQUAD_WAIT_FOR_ME` | (goal; Task_Blank) | - |
| 64 | `MAKE_TARGET_STAND_STILL` | Task_MakeTargetStandStill | - |
| 65 | `GET_UP` | Task_GetUp | - |
| 66 | `FORCE_GET_UP` | (goal; Task_Blank) | - |
| 67 | `MOVE_ON_FREE_WILL_FAST` | Task_Move | - |
| 68 | `LIFT_PERSON` | Task_LiftSomething | - |
| 69 | `PUT_DOWN_OBJECT` | Task_PutDownObject | - |
| 70 | `PUT_DOWN_CHARACTER_IN_BED` | (goal; Task_Blank) | - |
| 71 | `ADD_MATERIALS_TO_BUILDING` | Task_AddMaterialsToBuilding | - |
| 72 | `OPEN_DOOR` | Task_OpenDoor | - |
| 73 | `CLOSE_DOOR` | Task_CloseDoor | - |
| 74 | `OPEN_DOOR_HERE` | Task_OpenDoor | - |
| 75 | `CLOSE_DOOR_HERE` | Task_CloseDoor | - |
| 76 | `PICK_LOCK` | Task_PickLock | - |
| 77 | `LOCK_DOOR` | Task_LockDoor | - |
| 78 | `UNLOCK_DOOR` | Task_UnlockDoor | - |
| 79 | `LOCK_DOOR_HERE` | Task_LockDoor | - |
| 80 | `UNLOCK_DOOR_HERE` | Task_UnlockDoor | - |
| 81 | `BASH_DOOR` | Task_BashDoor | - |
| 82 | `MOVE_TO_BUILDING_DOOR` | Task_MoveToDoor | - |
| 83 | `MOVE_TO_CURRENT_LOCATION_BUILDING_DOOR` | Task_MoveToDoor_CurrentLocation | - |
| 84 | `OPEN_DOOR_FOR_CURRENT_LOCATION` | (goal; Task_Blank) | - |
| 85 | `OPEN_DOOR_FOR_DESTINATION` | (goal; Task_Blank) | - |
| 86 | `OPEN_UP_SHOP_DOORS` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 87 | `OPERATE_MACHINERY` | Task_OperateMachine | - |
| 88 | `DELIVER_RESOURCES` | (goal; Task_Blank) | - |
| 89 | `JOB_KEEP_EVERYTHING_RUNNING` | (goal; Task_Blank) | - |
| 90 | `UNJAM_ALL_MACHINES` | (goal; Task_Blank) | - |
| 91 | `UNJAM_MACHINE` | (goal; Task_Blank) | - |
| 92 | `COLLECT_OUTPUT_RESOURCE` | Task_EmptyMachine / Task_Pickup | - |
| 93 | `FILL_MACHINE` | Task_FillMachine | - |
| 94 | `WANT_TO_FILL_MACHINE` | (goal; Task_Blank) | - |
| 95 | `REPAIR` | Task_Build | - |
| 96 | `DISMANTLE` | Task_Dismantle | - |
| 97 | `USE_TRAINING_DUMMY` | Task_UseTrainingDummy | - |
| 98 | `USE_BED` | Task_UseBed | - |
| 99 | `PUT_SOMEONE_IN_BED` | Task_PutInSomething | - |
| 100 | `GET_PUT_IN_BED` | Task_UseBed | - |
| 101 | `DEFEAT_SQUAD` | (goal; Task_Blank) | 2 (URGENT/SQUAD_MISSION, URGENT/SQUAD_MISSION/REMOVE_WHOLE_SQUADS) |
| 102 | `SEEK_AND_TALK_AND_SEND_SIGNAL` | Task_TalktoPlayer | - |
| 103 | `MAKE_ANNOUNCEMENT` | Task_MakeAnnouncement | - |
| 104 | `ALWAYS_IMPOSSIBLE_TASK` | (goal; Task_Blank) | - |
| 105 | `FIND_AND_RESCUE` | (goal; Task_Blank) | - |
| 106 | `FIND_BED_AND_PUT_IN` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 107 | `USE_CAGE` | Task_UseCage | - |
| 108 | `PUT_IN_CAGE` | Task_PutInSomething | - |
| 109 | `KNOCKOUT_PRISONER` | (goal; Task_Blank) | - |
| 110 | `RELEASE_PRISONER` | Task_ReleasePrisoner | - |
| 111 | `BREAKOUT_PRISONER` | (goal; Task_Blank) | - |
| 112 | `FIND_CAGE_AND_PUT_IN` | (goal; Task_Blank) | 2 (NON_URGENT/SPECIFIC, URGENT/SPECIFIC) |
| 113 | `EMPTY_MACHINE_OUTPUTS` | (goal; Task_Blank) | - |
| 114 | `GET_RID_OF_RESOURCES_IN_MY_INVENTORY` | (goal; Task_Blank) | - |
| 115 | `FIND_SOME_BUILDING_MATERIALS` | (goal; Task_Blank) | - |
| 116 | `GET_OUT_OF_BED` | Task_GetOutOfBed | - |
| 117 | `FIND_A_SHOP` | (goal; Task_Blank) | - |
| 118 | `SHOPPING` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 119 | `BUY_SHIT` | Task_Shopping | - |
| 120 | `MOVE_INSIDE_BUILDING` | (goal; Task_Blank) | - |
| 121 | `MOVE_TO_FORTIFICATION_GATE` | Task_MoveToDoor_Gate_ChooseSide | - |
| 122 | `OPEN_FORTIFICATION_GATE` | (goal; Task_Blank) | - |
| 123 | `BASH_GATE` | Task_BashDoor | - |
| 124 | `OPERATE_STORAGE` | Task_FillMachine | - |
| 125 | `JOB_BUILDER` | (goal; Task_Blank) | - |
| 126 | `TALKTO_NEAREST_PLAYER_CHARACTER` | (goal; Task_Blank) | - |
| 127 | `RUN_AWAY_HOMETOWN` | Task_GoHomeTownFast | 2 (URGENT/SPECIFIC/REMOVE_MINE_ONLY, FLUFF/SPECIFIC/REMOVE_MINE_ONLY) |
| 128 | `RETREAT_HOMETOWN` | Task_GoHomeTown | 2 (NON_URGENT/SPECIFIC/REMOVE_MINE_ONLY, FLUFF/SPECIFIC/REMOVE_MINE_ONLY) |
| 129 | `MAKE_ANNOUNCEMENT_FAST` | Task_MakeAnnouncement | 2 (NON_URGENT/SQUAD_MISSION/REMOVE_MINE_ONLY) |
| 130 | `TRAVEL_TO_TARGET_TOWN_FAST` | Task_TravelToTargetTown | 1 (NON_URGENT/SPECIFIC) |
| 131 | `LOOT_FOOD_AND_STUFF` | (goal; Task_Blank) | 1 (URGENT/SQUAD_MISSION/REMOVE_MINE_ONLY) |
| 132 | `FIND_AND_KIDNAP` | (goal; Task_Blank) | - |
| 133 | `GET_OUT_OF_CAGE_LEGIT` | Task_GetOutOfCage | 1 (NON_URGENT/SPECIFIC) |
| 134 | `KILL_CAGE_OCCUPANT` | Task_KillPrisoner | - |
| 135 | `KILL_A_RANDOM_CAGE_OCCUPANT` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 136 | `FEED_CORPSE_INTO_MACHINE` | Task_FeedCorpseIntoMachine | - |
| 137 | `DEAD_GUYS_GO_IN_THE_POT` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 138 | `FIND_A_DEAD_GUY` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 139 | `EAT_A_RANDOM_CAGE_OCCUPANT` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 140 | `UNLOCK_DOOR_PLAYER_ORDER` | Task_UnlockDoor | - |
| 141 | `FOLLOW_SQUADLEADER` | Task_FollowLeader | 2 (NON_URGENT/LEADER) |
| 142 | `FIND_AND_RESCUE_LEADER` | (goal; Task_Blank) | 1 (NON_URGENT/LEADER) |
| 143 | `PROTECT_OWN_SQUAD` | (goal; Task_Blank) | 2 (URGENT/SELF, NON_URGENT/SELF) |
| 144 | `TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 145 | `GET_RE_EQUIPPED` | (goal; Task_Blank) | - |
| 146 | `USE_TURRET` | Task_OperateMachine / Task_UseTurret | - |
| 147 | `STUMBLE_TASK_FORCED` | Task_Stumble | - |
| 148 | `FIND_AND_RESCUE_IF_THERES_BEDS` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 149 | `MAN_A_TURRET` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 150 | `PROSPECTING` | Task_Prospecting | - |
| 151 | `EMPTYING_MACHINE` | (goal; Task_Blank) | - |
| 152 | `OPERATE_AUTOMATIC_MACHINERY` | (goal; Task_Blank) | - |
| 153 | `GO_HOME_AND_GO_TO_BED` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 154 | `GO_TO_THE_BAR_AND_DRINK` | (goal; Task_Blank) | - |
| 155 | `LOCK_ALL_MY_DOORS` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 156 | `ENTER_BUILDING` | Task_EnterBuilding | - |
| 157 | `STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 158 | `SHOO_STRANGERS_OUT_OF_MY_BUILDING` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 159 | `SEND_DIALOGUE_SIGNAL` | Task_TalktoPlayer | - |
| 160 | `SEND_DIALOGUE_SIGNAL_REPEAT` | (goal; Task_Blank) | - |
| 161 | `SEND_DIALOGUE_SIGNAL_WITHOUT_MOVING` | Task_TalktoPlayer | - |
| 162 | `LOCK_DOOR_FROM_INSIDE` | Task_LockDoor | - |
| 163 | `MOVE_TO_BUILDING_DOOR_INSIDEPOS` | Task_MoveToDoor_Inside | - |
| 164 | `FOLLOW_WHILE_TALKING` | Task_Follow / Task_FollowAndTalk | - |
| 165 | `TOWN_STALKER` | Task_TownStalker | - |
| 166 | `CHAIN_TARGET` | Task_CuffTarget | - |
| 167 | `CAPTURE_NEW_SLAVES` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 168 | `CARRY_WOUNDED_SLAVES` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 169 | `PUT_DOWN_CARRIED_DUDE_IF_THEY_CAN_WALK` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 170 | `LIFT_OBJECT_BUT_HEAL_FIRST` | Task_LiftSomething | - |
| 171 | `FOLLOW_SLAVEMASTER` | Task_FollowLeader | 1 (NON_URGENT/SPECIFIC) |
| 172 | `SLAVE_GET_IN_MY_MASTERS_CAGE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 173 | `GATHER_SLAVES_FROM_CAGES` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 174 | `GET_SLAVE` | Task_GetSlave | - |
| 175 | `SLEEP_ON_FLOOR` | Task_SleepOnFloor | 1 (NON_URGENT/SPECIFIC) |
| 176 | `HUNTING_BLOODSMELL` | Task_HuntingSmell | 1 (NON_URGENT/SPECIFIC) |
| 177 | `LOOT_THE_DEAD` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 178 | `LOOT_TO_REPLACE_MISSING_WEAPON` | (goal; Task_Blank) | - |
| 179 | `HUNT_MY_THIEF` | (goal; Task_Blank) | - |
| 180 | `MAN_THE_GATE` | Task_ManTheGate | 1 (NON_URGENT/HOME_GATE) |
| 181 | `STRIP_TARGETS_WEAPONS` | Task_StripWeapons | - |
| 182 | `PROCESS_AND_STRIP_NEW_SLAVE` | Task_ProcessNewSlave | - |
| 183 | `SLAVE_WATCHING` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 184 | `PUT_LOOT_IN_STORAGE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 185 | `CUT_SHACKLES` | Task_CutShackles | - |
| 186 | `BRUTE_FORCE_SHACKLES` | Task_BruteForceShackles | - |
| 187 | `_SLAVE_OBEDIENCE` | (goal; Task_Blank) | - |
| 188 | `WORK_THE_SLAVES` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 189 | `AUTO_LABOURING_MINES` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 190 | `AUTO_LABOURING_MINES_PRETEND` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 191 | `GO_TO_NEAREST_HQ` | (goal; Task_Blank) | - |
| 192 | `GO_TO_SOMEWHERE_FOR_DELIVERING_SLAVES` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 193 | `CAPTURE_ESCAPING_SLAVES` | (goal; Task_Blank) | - |
| 194 | `GIVE_ALL_MY_SLAVES_TO` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 195 | `LOCK_ALL_THE_CAGES` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 196 | `BEAT_CAGE_OCCUPANT` | Task_KillPrisoner | - |
| 197 | `LOCK_ALL_MY_DOORS_FROM_OUTSIDE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 198 | `LOCK_DOOR_FROM_OUTSIDE` | Task_LockDoor | - |
| 199 | `MOVE_TO_BUILDING_DOOR_OUTSIDEPOS` | Task_MoveToDoor_Outside | - |
| 200 | `LEAVE_BUILDING` | Task_LeaveBuilding | - |
| 201 | `PICK_LOCK_ON_SHACKLES` | Task_PickLock_Shackles | - |
| 202 | `TOTAL_ESCAPE` | (goal; Task_Blank) | 1 (URGENT/SELF) |
| 203 | `ARREST_TARGET` | (goal; Task_Blank) | - |
| 204 | `HUNT_BOUNTIES` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 205 | `ARREST_TARGETS_CARRIED_PERSON` | (goal; Task_Blank) | - |
| 206 | `FIND_CAGE_AND_PUT_IN_IF_BOUNTY` | (goal; Task_Blank) | - |
| 207 | `GET_OUT_OF_CAGE_ESCAPE` | Task_GetOutOfCage | 1 (NON_URGENT/SPECIFIC) |
| 208 | `GET_OUT_OF_BED_IF_ITS_EMERGENCY` | Task_GetOutOfBed | 1 (URGENT/SPECIFIC) |
| 209 | `INVESTIGATE_ALARMS` | Task_InvestigateAlarms | 1 (URGENT/SPECIFIC) |
| 210 | `INVESTIGATE_ALARMS_ALLIES_ONLY` | Task_InvestigateAlarms | 1 (URGENT/SPECIFIC) |
| 211 | `POLICE_FREE_PRISONERS_WHEN_DONE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 212 | `LOOT_STOLEN_GOODS` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 213 | `LIFT_PERSON_SNATCHING_ALLOWED` | Task_LiftSomething | - |
| 214 | `RELAX_IN_TOWN_PACKAGE` | (goal; Task_Blank) | 2 (FLUFF/SPECIFIC, NON_URGENT/SPECIFIC) |
| 215 | `TRAVEL_TO_TARGET_PACKAGE` | Task_TravelToMissionTarget_ArmyFormation | 1 (NON_URGENT/SQUAD_MISSION) |
| 216 | `RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE` | Task_PatrolTownViolent | 1 (NON_URGENT/SPECIFIC) |
| 217 | `GATHER_SLAVES_FROM_CAGES_IF_ITS_AN_EXPORT_TOWN` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 218 | `GIVE_ALL_MY_SLAVES_TO_IF_ITS_AN_IMPORT_TOWN` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 219 | `TAKE_OFF_MY_SHACKLES` | Task_TakeOffCuffs | - |
| 220 | `EAT_TARGET_ALIVE` | Task_EatPrisoner | - |
| 221 | `PRETEND_TO_OPERATE_MACHINERY` | Task_OperateMachine | - |
| 222 | `MAN_A_TURRET_ON_BUILDING` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 223 | `PICKUP_INTRUDERS` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 224 | `TAKE_INTRUDER_OUTSIDE` | Task_TakeIntruderOutside | 1 (URGENT/SPECIFIC) |
| 225 | `LIFT_PERSON_PLAYER_ORDER` | Task_LiftSomething | - |
| 226 | `BASH_DOOR_PLAYER_ORDER` | Task_BashDoor | - |
| 227 | `MELEE_ATTACK_ANIMAL` | Task_MeleeAttack | - |
| 228 | `STEALTH_KNOCKOUT` | Task_StealthKO | - |
| 229 | `STEALTH_KILL` | Task_StealthKO | - |
| 230 | `EAT_A_RANDOM_DEAD_BODY` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 231 | `EAT_CROPS` | Task_EatingCrops | - |
| 232 | `FIND_CROPS_TO_EAT` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 233 | `EAT_A_RANDOM_KO_BODY` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 234 | `MAN_A_TURRET_PLAYER_JOB` | (goal; Task_Blank) | - |
| 235 | `SHOOT_AT_TARGET` | Task_OperateMachine / Task_UseTurret | - |
| 236 | `WORSHIP_TARGET` | Task_WorshipTarget | - |
| 237 | `FOGMAN_WORSHIP_VICTIM` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 238 | `LOOT_ANIMALS_JOB` | (goal; Task_Blank) | - |
| 239 | `GO_HOME_AND_GO_TO_BED_SECURE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 240 | `LIFT_PERSON_SNATCHING_ALLOWED_IN_TOWN_ONLY` | Task_LiftSomething | - |
| 241 | `LOOT_RESOURCE_ITEMS_WE_HAVE_STORAGE_FOR` | Task_Loot_ResourcesWeHaveStorageFor | - |
| 242 | `DITCH_ALL_RESOURCES` | (goal; Task_Blank) | - |
| 243 | `AQUIRE_FOOD_AT_HOMEBASE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 244 | `GRAB_ONE_FOOD` | Task_grabOneFood | - |
| 245 | `GATHER_SLAVES_FROM_CAGES_IF_FEMALE_OR_BEAST` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 246 | `KIDNAP_ORDER` | Task_Kidnapping | - |
| 247 | `COLLECT_OUTPUT_RESOURCE_BUILD_MATS` | Task_EmptyMachine_BuildMats / Task_Pickup | - |
| 248 | `DEFEAT_SQUAD_LIMIT_CHASE_RANGE` | (goal; Task_Blank) | - |
| 249 | `SPLINT_ORDER` | Task_FirstAidRig | - |
| 250 | `SPLINT_JOB` | (goal; Task_Blank) | - |
| 251 | `ESCAPE_KIDNAP` | (goal; Task_Blank) | - |
| 252 | `ESCAPE_KIDNAP_STR` | (goal; Task_Blank) | - |
| 253 | `FOLLOW_URGENT_ESCAPE` | Task_Follow | 1 (URGENT/SQUAD_MISSION) |
| 254 | `FINAL_KIDNAPPER_CAGE_JOB` | (goal; Task_Blank) | 2 (URGENT/SPECIFIC) |
| 255 | `SIT_ON_THRONE` | (goal; Task_Blank) | 2 (NON_URGENT/SPECIFIC, URGENT/SPECIFIC) |
| 256 | `GET_OUT_OF_CAGE_OPPORTUNISTIC` | Task_GetOutOfCage | 1 (NON_URGENT/SPECIFIC) |
| 257 | `GET_OUT_OF_BED_ONCE_HEALED` | Task_GetOutOfBed | - |
| 258 | `USE_BED_ORDER` | Task_UseBed | - |
| 259 | `EAT_FOOD_ON_GROUND` | (goal; Task_Blank) | - |
| 260 | `NEW_SLAVE_PROCESSING` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 261 | `SLEEP_ON_FLOOR_FAKE_AMBUSH` | Task_SleepOnFloor | - |
| 262 | `RANGED_ATTACK` | Task_RangedAttack | - |
| 263 | `RANGED_ATTACK_FOCUSED` | Task_RangedAttack | - |
| 264 | `EQUIP_CROSSBOW` | Task_EquipBestWeapon | - |
| 265 | `UNEQUIP_CROSSBOW` | (goal; Task_Blank) | - |
| 266 | `RANGED_ATTACK_FOCUSED_UNPROVOKED` | Task_RangedAttack | - |
| 267 | `MOVE_IN_BOW_RANGE` | Task_MoveBowRange | - |
| 268 | `STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 269 | `HEAL_MY_LEGS` | Task_FirstAid | 1 (URGENT/SELF) |
| 270 | `ASSAULT_FORTIFICATIONS_PREFER_GATES` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 271 | `ASSAULT_FORTIFICATIONS_PREFER_WALLS` | (goal; Task_Blank) | - |
| 272 | `SMASH_BUILDING` | Task_BashDoor | - |
| 273 | `PICKUP_INTRUDERS_TOWN` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 274 | `TAKE_INTRUDER_OUTSIDE_TOWN` | Task_TakeIntruderOutsideTown | 1 (NON_URGENT/SPECIFIC) |
| 275 | `SIT_AROUND` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 276 | `LIBERATE_ALL_THE_PRISONERS` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 277 | `ANIMAL_FETCH_A_LIMB` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 278 | `PLAY_BECAUSE_I_HAVE_A_LIMB_IN_MOUTH` | Task_DogLimbRunningPlay | 1 (URGENT/SPECIFIC) |
| 279 | `CHASE_ALLY_DOGS_WITH_MOUTH_LIMBS` | (goal; Task_Blank) | 1 (URGENT/SPECIFIC) |
| 280 | `RUN_AWAY_FORCED` | Task_Runaway | - |
| 281 | `FIND_CAGE_AND_PUT_DEADGUY_IN` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 282 | `EAT_A_RANDOM_CAGE_OCCUPANT_MEASURED_RATE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 283 | `SHOO_STRANGERS_OUT_OF_MY_BUILDING_IF_PRIVATE` | (goal; Task_Blank) | 1 (NON_URGENT/SPECIFIC) |
| 284 | `LOOT_CONTAINER` | Task_EmptyMachine | - |
| 285 | `CUT_LOCK` | Task_CutLock | - |
| 286 | `BRUTE_FORCE_LOCK` | Task_BruteForceLock | - |
| 287 | `BASH_DOOR_HERE` | Task_BashDoor | - |
| 288 | `PROTECT_ALLIES_STAY_IN_TOWN` | (goal; Task_Blank) | 1 (URGENT/SELF) |
| 289 | `?` | Task_Follow | - |
| 290 | `?` | Task_BashDoor | - |

## TaskData registry

**Verified** (disassembly of the table builder `FUN_140319a60`, each registration call `FUN_14060ea00(type, memberFn, ...)` parsed,
then every row re-parsed from the decompile of the same function: 253 registration calls with 253 distinct type numbers, and type,
text, flag bits and the contents and order of lists A and B agree for all 253 rows; the decompile drops some trailing call arguments,
so the `!` on an effect and the `*` marker rest on the disassembly alone and are **Observed**). 251 of the 288 named types plus the
two unnamed types 289 and 290 are registered; 37 named types are not: 9, 10, 11, 18, 23, 27, 33, 46, 48, 49, 50, 54, 56, 63, 66, 70, 109, 111, 117,
120, 126, 145, 151, 154, 160, 178, 179, 191, 193, 205, 206, 229, 251, 252, 265, 267, 271.
Columns: display text (field +0x138), registration flag bits, condition list A (TaskData +0x4c; names prefixed `!` are required
false; `*` marks an entry whose second flag byte (+5) is set, meaning **Unknown**), condition list B (+0xa8, at most 11) and the
single effect (+0x128). What list A versus list B mean is **Unknown**: A reads like "done when" conditions (isAtLocation,
isBuildingComplete) and B like "may start when" ones (isMovementAllowed, hasWeapon) in every row seen (**Observed**). The scorer
agrees with that reading in part: it drops a candidate whose list A is satisfied (`FUN_14060f570`) and the planner takes the first
unmet state id from list B (`FUN_14060f940`), but it also drops a candidate whose B holds and that has no effect, so the roles are
not settled. The `ALWAYS_IMPOSSIBLE_TASK` row's text is the string "null". Two states (33 and 85) are registered with an empty name.

| # | taskType | Text | Flags | A | B | Effect |
|---|---|---|---|---|---|---|
| 1 | `MOVE_ON_FREE_WILL` | Moving | 0x63 | isAtLocation, !isRouteTraceBlocked, isWithin10Meters, isWithin50Meters | isMovementAllowed, !isCrouching, !isInBed, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE | - |
| 29 | `MOVE_CUS_ORDERED` | Move order | 0x63 | isAtLocation | !isCrouching, !isInBed, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE | - |
| 67 | `MOVE_ON_FREE_WILL_FAST` | Running | 0x63 | isAtLocation, isWithin10Meters, isWithin50Meters | isMovementAllowed, !isCrouching, !isInBed, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE | - |
| 82 | `MOVE_TO_BUILDING_DOOR` | Moving to door | 0x61 | AT_LOCATION_BUILDING_DOOR | isMovementAllowed, !isCrouching, !isInBed, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, LOCATION_IS_ACCESSIBLE | - |
| 163 | `MOVE_TO_BUILDING_DOOR_INSIDEPOS` | Moving to door inside | 0x41 | AT_LOCATION_BUILDING_DOOR_INSIDE | isMovementAllowed, !isCrouching, !isInBed, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, LOCATION_IS_ACCESSIBLE | - |
| 83 | `MOVE_TO_CURRENT_LOCATION_BUILDING_DOOR` | Moving to current door | 0x41 | AT_CURRENT_LOCATION_BUILDING_DOOR | isMovementAllowed, !isCrouching, !isInBed | - |
| 199 | `MOVE_TO_BUILDING_DOOR_OUTSIDEPOS` | Moving to door outside | 0x41 | AT_LOCATION_BUILDING_DOOR_OUTSIDE | isMovementAllowed, !isCrouching, !isInBed, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, LOCATION_IS_ACCESSIBLE | - |
| 104 | `ALWAYS_IMPOSSIBLE_TASK` | null | 0x77 | - | noneR | - |
| 180 | `MAN_THE_GATE` | Gate guard | 0x1 | - | AT_LOCATION_FORTIFICATION_GATE, DOOR_IS_OPEN | - |
| 121 | `MOVE_TO_FORTIFICATION_GATE` | Moving to gate | 0x61 | AT_LOCATION_FORTIFICATION_GATE | isMovementAllowed, !IS_CROUCHING_OR_LYING, LOCATION_IS_ACCESSIBLE | - |
| 122 | `OPEN_FORTIFICATION_GATE` | Open gate | 0x61 | DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS | !isDoorLocked | DOOR_IS_OPEN |
| 123 | `BASH_GATE` | Bashing gate | 0x21 | !isDoorLocked, DOOR_IS_BROKEN | !isObjectMine, !iAmBeingMeleeAttacked immediate, crowdLimit8, hasWeaponEquipped, AT_LOCATION_FORTIFICATION_GATE, !DOOR_IS_OPEN | - |
| 290 | `?` | Bashing gate | 0x21 | !isDoorLocked, DOOR_IS_BROKEN | !isObjectMine, !iAmBeingMeleeAttacked immediate, crowdLimit8, hasWeaponEquipped, AT_LOCATION_FORTIFICATION_GATE, !DOOR_IS_OPEN | - |
| 272 | `SMASH_BUILDING` | Smashing | 0x21 | isBuildingDestroyed | !isObjectMine, crowdLimit8, !iAmBeingMeleeAttacked immediate, hasWeaponEquipped, isAtLocation | - |
| 270 | `ASSAULT_FORTIFICATIONS_PREFER_GATES` | Assaulting town | 0x21 | DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS | - | isBuildingDestroyed |
| 189 | `AUTO_LABOURING_MINES` | Worker | 0x1 | - | - | IS_WORKING |
| 190 | `AUTO_LABOURING_MINES_PRETEND` | Worker | 0x1 | - | - | IS_WORKING_PRETEND |
| 87 | `OPERATE_MACHINERY` | Operating machine | 0x1 | IS_WORKING | hasOneWorkingArm, machineHasFreeOperatorSlot, isBuildingComplete, !hasWeaponEquipped, !isMachineOutputFull, !machineHasEmptyInputs, !haveSomeResourcesFromThisMachineButWantThemGoneIfPossible, !isMachineInputInvalid, BUILDING_HAS_POWER, isAtLocation | - |
| 221 | `PRETEND_TO_OPERATE_MACHINERY` | Operating machine | 0x1 | IS_WORKING_PRETEND | hasOneWorkingArm, machineHasFreeOperatorSlot, isBuildingComplete, BUILDING_HAS_POWER, !hasWeaponEquipped, isAtLocation | - |
| 152 | `OPERATE_AUTOMATIC_MACHINERY` | Operating automatic machine | 0x1 | - | isBuildingComplete, !hasWeaponEquipped | !machineHasEmptyInputs |
| 124 | `OPERATE_STORAGE` | Hauling to | 0x1 | - | isBuildingComplete, haveSomeResourcesFor, isAtLocation | - |
| 114 | `GET_RID_OF_RESOURCES_IN_MY_INVENTORY` | Ditching to make space | 0x1 | gotInventoryRoomForResources | - | !machineHasEmptyInputs |
| 242 | `DITCH_ALL_RESOURCES` | Ditching resources | 0x1 | DITCHED_ALL_RESOURCES | - | !machineHasEmptyInputs |
| 184 | `PUT_LOOT_IN_STORAGE` | Ditching loot | 0x1 | !CARRYING_EXCESS_LOOT | - | !machineHasEmptyInputs |
| 92 | `COLLECT_OUTPUT_RESOURCE` | Collecting resource | 0x1 | haveSomeResourcesFrom, !isMachineOutputFull, !isMachineInputInvalid | gotInventoryRoomForResources, isAtLocation | - |
| 247 | `COLLECT_OUTPUT_RESOURCE_BUILD_MATS` | Collecting resource | 0x1 | HAVE_SOME_BUILD_MATS | gotInventoryRoomForResources, isAtLocation | - |
| 284 | `LOOT_CONTAINER` | Looting | 0x1 | HAVE_SOME_LOOT_FROM | gotInventoryRoomForResources, isAtLocation | - |
| 115 | `FIND_SOME_BUILDING_MATERIALS` | Finding building mats | 0x1 | hasBuildingMaterials | - | HAVE_SOME_BUILD_MATS |
| 93 | `FILL_MACHINE` | Filling machine | 0x1 | !machineHasEmptyInputs, isMachineOutputFull* | isBuildingComplete, haveSomeResourcesFor, !isMachineInputInvalid, isAtLocation | - |
| 94 | `WANT_TO_FILL_MACHINE` | Try filling machine | 0x1 | haveSomeResourcesFor | - | haveSomeResourcesFrom |
| 113 | `EMPTY_MACHINE_OUTPUTS` | Try emptying machine | 0x1 | !isMachineOutputFull, isMachineOutputEmpty, !haveSomeResourcesFrom, !haveSomeResourcesFromThisMachineButWantThemGoneIfPossible | - | !machineHasEmptyInputs |
| 91 | `UNJAM_MACHINE` | Unjamming machine | 0x1 | !isMachineJammed | !machineHasEmptyInputs, !isMachineOutputFull | - |
| 90 | `UNJAM_ALL_MACHINES` | Unjam all machines | 0x1 | !isAnyMachineJammed | - | !isMachineJammed |
| 89 | `JOB_KEEP_EVERYTHING_RUNNING` | Keeping everything running | 0x1 | - | !isAnyMachineJammed | - |
| 88 | `DELIVER_RESOURCES` | Delivering | 0x1 | - | isAtLocation | - |
| 84 | `OPEN_DOOR_FOR_CURRENT_LOCATION` | Open current door | 0x41 | LOCATION_IS_ACCESSIBLE | - | DOOR_IS_OPEN_HERE |
| 74 | `OPEN_DOOR_HERE` | Opening door here | 0x41 | DOOR_IS_OPEN_HERE | !DOOR_IS_LOCKED_HERE, AT_CURRENT_LOCATION_BUILDING_DOOR | - |
| 75 | `CLOSE_DOOR_HERE` | Closing door here | 0x41 | !DOOR_IS_OPEN_HERE | AT_CURRENT_LOCATION_BUILDING_DOOR | - |
| 85 | `OPEN_DOOR_FOR_DESTINATION` | Open destination door | 0x41 | DESTINATION_IS_ACCESSIBLE | - | DOOR_IS_OPEN |
| 72 | `OPEN_DOOR` | Opening door | 0x41 | DOOR_IS_OPEN | !isDoorLocked, AT_LOCATION_BUILDING_DOOR | - |
| 73 | `CLOSE_DOOR` | Closing door | 0x41 | !DOOR_IS_OPEN | !DOOR_IS_BROKEN, AT_LOCATION_BUILDING_DOOR | - |
| 77 | `LOCK_DOOR` | Locking door | 0x41 | isDoorLocked | !DOOR_IS_OPEN, isObjectMine, !DOOR_IS_BROKEN, AT_LOCATION_BUILDING_DOOR | - |
| 78 | `UNLOCK_DOOR` | Unlocking door | 0x45 | !isDoorLocked | isObjectMine, AT_LOCATION_BUILDING_DOOR | - |
| 140 | `UNLOCK_DOOR_PLAYER_ORDER` | Unlocking door | 0x1 | !isDoorLocked | isObjectMine, AT_LOCATION_BUILDING_DOOR | - |
| 79 | `LOCK_DOOR_HERE` | Locking door here | 0x41 | DOOR_IS_LOCKED_HERE | !DOOR_IS_OPEN, isObjectMine, AT_CURRENT_LOCATION_BUILDING_DOOR | - |
| 162 | `LOCK_DOOR_FROM_INSIDE` | Locking door from inside | 0x41 | DOOR_IS_LOCKED_AND_IM_INSIDE | isObjectMine, AT_LOCATION_BUILDING_DOOR_INSIDE, !DOOR_IS_OPEN | - |
| 198 | `LOCK_DOOR_FROM_OUTSIDE` | Locking door from outside | 0x41 | DOOR_IS_LOCKED_AND_IM_OUTSIDE | isObjectMine, AT_LOCATION_BUILDING_DOOR_OUTSIDE, !DOOR_IS_OPEN | - |
| 80 | `UNLOCK_DOOR_HERE` | Unlocking door here | 0x41 | !DOOR_IS_LOCKED_HERE | isObjectMine, AT_CURRENT_LOCATION_BUILDING_DOOR | - |
| 287 | `BASH_DOOR_HERE` | Bashing door | 0x21 | !DOOR_IS_LOCKED_HERE, DOOR_IS_BROKEN, DOOR_IS_OPEN | !isObjectMine, !isAlly, hasWeaponEquipped, AT_LOCATION_BUILDING_DOOR | - |
| 195 | `LOCK_ALL_THE_CAGES` | Locking up cages | 0x1 | - | - | isDoorLocked |
| 155 | `LOCK_ALL_MY_DOORS` | Locking up | 0x1 | allDoorsLocked | !strangers in house, I_AM_INSIDE_TARGET_BUILDING | DOOR_IS_LOCKED_AND_IM_INSIDE |
| 197 | `LOCK_ALL_MY_DOORS_FROM_OUTSIDE` | Locking up from outside | 0x1 | - | !strangers in house, !I_AM_INSIDE_TARGET_BUILDING | DOOR_IS_LOCKED_AND_IM_OUTSIDE |
| 86 | `OPEN_UP_SHOP_DOORS` | Opening up | 0x1 | !allDoorsLocked | - | DOOR_IS_OPEN |
| 156 | `ENTER_BUILDING` | Entering building | 0x61 | I_AM_INSIDE_TARGET_BUILDING | DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE | - |
| 216 | `RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE` | Ransacking town | 0x1 | - | hasWeaponEquipped | I_AM_INSIDE_TARGET_BUILDING |
| 200 | `LEAVE_BUILDING` | Leaving building | 0x61 | !I_AM_INSIDE_TARGET_BUILDING | DESTINATION_IS_ACCESSIBLE | - |
| 76 | `PICK_LOCK` | Lockpicking | 0x55 | !isDoorLocked | hasOneWorkingArm, iHaveSufficientLockSkill, isAtLocation, !hasWeaponEquipped | - |
| 201 | `PICK_LOCK_ON_SHACKLES` | Lockpicking Shackles | 0x55 | !isWearingShackles_locked | hasOneWorkingArm, iHaveSufficientLockSkillForShackles, isAtLocation, !hasWeaponEquipped | - |
| 219 | `TAKE_OFF_MY_SHACKLES` | Removing Shackles | 0x45 | !isChained | !isWearingShackles_locked | - |
| 202 | `TOTAL_ESCAPE` | Escaping | 0x55 | none | !isChained, !I_AM_IMPRISONED | - |
| 185 | `CUT_SHACKLES` | Cutting shackles | 0x55 | !isWearingShackles_locked | hasOneWorkingArm, hasTools, isAtLocation, !hasWeaponEquipped | - |
| 285 | `CUT_LOCK` | Cutting lock | 0x55 | !isDoorLocked | hasOneWorkingArm, hasTools, isAtLocation, !hasWeaponEquipped | - |
| 186 | `BRUTE_FORCE_SHACKLES` | Breaking shackles | 0x55 | !isWearingShackles_locked | hasOneWorkingArm, isAtLocation | - |
| 286 | `BRUTE_FORCE_LOCK` | Breaking lock | 0x55 | !isDoorLocked | hasOneWorkingArm, isAtLocation | - |
| 133 | `GET_OUT_OF_CAGE_LEGIT` | Get out of cage | 0x74 | !I_AM_IMPRISONED | !isDoorLocked | - |
| 256 | `GET_OUT_OF_CAGE_OPPORTUNISTIC` | Get out of cage | 0x74 | !I_AM_IMPRISONED | - | - |
| 207 | `GET_OUT_OF_CAGE_ESCAPE` | Escape cage | 0x74 | !I_AM_IMPRISONED | !isDoorLocked | - |
| 81 | `BASH_DOOR` | Bashing door | 0x21 | !isDoorLocked, DOOR_IS_BROKEN, DOOR_IS_OPEN | !isObjectMine, !isAlly, hasWeaponEquipped, AT_LOCATION_BUILDING_DOOR | - |
| 226 | `BASH_DOOR_PLAYER_ORDER` | Bashing door | 0x21 | !isDoorLocked, DOOR_IS_BROKEN, DOOR_IS_OPEN | !isObjectMine, !isAlly, hasWeaponEquipped, AT_LOCATION_BUILDING_DOOR | - |
| 95 | `REPAIR` | Repairing | 0x1 | !DOOR_IS_BROKEN, !isBuildingDamaged | hasOneWorkingArm, isAtLocation, !IS_CROUCHING_OR_LYING | - |
| 125 | `JOB_BUILDER` | Engineer | 0x1 | - | - | isBuildingComplete |
| 213 | `LIFT_PERSON_SNATCHING_ALLOWED` | Lifting/Snatching | 0x1 | IS_CARRYING_TARGET | isCarryingArmOK, isMovementAllowed, !IS_CROUCHING_OR_LYING, isTargetDown, isAtLocation | - |
| 240 | `LIFT_PERSON_SNATCHING_ALLOWED_IN_TOWN_ONLY` | Capture Target | 0x1 | IS_CARRYING_TARGET | isAtHomeTown, targetAtATown, isCarryingArmOK, isMovementAllowed, !IS_CROUCHING_OR_LYING, isTargetDown, isAtLocation | - |
| 225 | `LIFT_PERSON_PLAYER_ORDER` | Lifting/Snatching Order | 0x1 | IS_CARRYING_TARGET | isCarryingArmOK, isMovementAllowed, !IS_CROUCHING_OR_LYING, isAtLocation | - |
| 246 | `KIDNAP_ORDER` | Kidnapping | 0x1 | IS_CARRYING_TARGET | isCarryingArmOK, isMovementAllowed, !IS_CROUCHING_OR_LYING, isTargetDown, isAtLocation | - |
| 68 | `LIFT_PERSON` | Lifting | 0x1 | IS_CARRYING_TARGET | isCarryingArmOK, isMovementAllowed, !IS_CROUCHING_OR_LYING, isAtLocation, isTargetDown | - |
| 170 | `LIFT_OBJECT_BUT_HEAL_FIRST` | Lifting and healing | 0x1 | IS_CARRYING_TARGET_HEALTHY | !TARGET_IS_IMPRISONED, !needsFirstAid, isCarryingArmOK, isMovementAllowed, !IS_CROUCHING_OR_LYING, isAtLocation | - |
| 69 | `PUT_DOWN_OBJECT` | Putting down | 0x1 | !IS_CARRYING_TARGET, !IS_CARRYING_SOMETHING | IS_CARRYING_SOMETHING | - |
| 2 | `BUILD` | Building | 0x1 | isBuildingComplete, !isBuildingDamaged | hasOneWorkingArm, buildingHasBuildingMaterials, isAtLocation, !IS_CROUCHING_OR_LYING | - |
| 96 | `DISMANTLE` | Demolishing | 0x1 | none | isAtLocation | - |
| 71 | `ADD_MATERIALS_TO_BUILDING` | Adding materials | 0x1 | buildingHasBuildingMaterials | hasBuildingMaterials, isAtLocation, !IS_CROUCHING_OR_LYING | - |
| 3 | `PICKUP` | Collecting item | 0x65 | isSubjectInInventory | isItemLootable, isAtLocation | - |
| 14 | `IDLE` | Idleness | 0x75 | - | !iAmProne, !hasWeaponEquipped | - |
| 28 | `STAND_UP` | Standing | 0x25 | !isCrouching, !isLyingProne, !IS_CROUCHING_OR_LYING | !isInBed | - |
| 65 | `GET_UP` | Getting up | 0x25 | !iAmProne | canGetUp | - |
| 52 | `__aoeu__` | Waking up | 0x65 | - | canGetUp | - |
| 147 | `STUMBLE_TASK_FORCED` | Stumbling | 0x21 | - | canGetUp | - |
| 6 | `EQUIP_WEAPON` | Equipping weapon | 0x65 | hasWeaponEquipped | !me swim, hasOneWorkingArm | - |
| 264 | `EQUIP_CROSSBOW` | Equipping crossbow | 0x65 | hasWeaponEquipped | !me swim, hasOneWorkingArm | - |
| 7 | `UNEQUIP_WEAPON` | Sheathing weapon | 0x65 | !hasWeaponEquipped, !hasWeaponEquipped, !hasWeaponEquipped | - | - |
| 269 | `HEAL_MY_LEGS` | Fixing Legs | 0x45 | !myLegsAreMessedUp | hasOneWorkingArm, haveFirstAidKit, !me swim, !isAggressionTowardsMe, !hasWeaponEquipped | - |
| 25 | `FIRST_AID_ORDER` | First aid | 0x45 | !needsFirstAid | hasOneWorkingArm, haveFirstAidKit, !target swimming, targetStandingStill, isAtLocation, !hasWeaponEquipped | - |
| 249 | `SPLINT_ORDER` | Splinting | 0x45 | !needsSplint | hasOneWorkingArm, hasSplintKit, !target swimming, targetStandingStill, isAtLocation, !hasWeaponEquipped | - |
| 60 | `FIRST_AID_ROBOT` | Repairing | 0x45 | !needsFirstAid_robot | hasOneWorkingArm, haveroboAidKit, !target swimming, targetStandingStill, isAtLocation, !hasWeaponEquipped | - |
| 58 | `JOB_MEDIC` | Medic | 0x45 | - | - | !needsFirstAid |
| 250 | `SPLINT_JOB` | Splint Rigging | 0x5 | - | - | !needsSplint |
| 57 | `JOB_REPAIR_ROBOT` | Robotics | 0x45 | - | !isAggressionTowardsMe | !needsFirstAid_robot |
| 62 | `STAND_STILL` | Standing still | 0x75 | - | !me swim, !isAggressionTowardsMe | - |
| 64 | `MAKE_TARGET_STAND_STILL` | Stand still dammit! | 0x45 | targetStandingStill | !target swimming, !isAggressionTowards | - |
| 237 | `FOGMAN_WORSHIP_VICTIM` | Worshiping | 0x41 | - | - | target worshiped |
| 236 | `WORSHIP_TARGET` | Worshiping | 0x41 | target worshiped | !hasWeaponEquipped, !IS_CARRYING_SOMETHING | - |
| 131 | `LOOT_FOOD_AND_STUFF` | Looting food | 0x1 | - | - | HAVE_SOME_LOOT_FROM |
| 259 | `EAT_FOOD_ON_GROUND` | Picking up food | 0x71 | gotAFoodItem | - | isSubjectInInventory |
| 277 | `ANIMAL_FETCH_A_LIMB` | Fetch | 0x20 | gotAFoodItem | - | isSubjectInInventory |
| 278 | `PLAY_BECAUSE_I_HAVE_A_LIMB_IN_MOUTH` | Playing 'Limbs' | 0x20 | !gotAFoodItem | - | - |
| 279 | `CHASE_ALLY_DOGS_WITH_MOUTH_LIMBS` | Limb Jealousy | 0x20 | - | - | isNearTo |
| 243 | `AQUIRE_FOOD_AT_HOMEBASE` | Getting food | 0x61 | - | - | gotAFoodItem |
| 244 | `GRAB_ONE_FOOD` | Grabbing food | 0x61 | gotAFoodItem | !isAlliesFighting, gotInventoryRoomForResources, isAtLocation | - |
| 8 | `FIND_WEAPON` | Finding weapon | 0x45 | hasWeapon | - | isSubjectInInventory |
| 177 | `LOOT_THE_DEAD` | Looting the dead | 0x45 | - | - | isSubjectInInventory |
| 212 | `LOOT_STOLEN_GOODS` | Confiscating goods | 0x45 | - | - | isSubjectInInventory |
| 26 | `LOOT_TARGET` | Looting | 0x45 | !none | isAtLocation | - |
| 238 | `LOOT_ANIMALS_JOB` | Foraging animals | 0x1 | - | gotInventoryRoomForResources | LOOTED_STORABLE_ITEMS |
| 241 | `LOOT_RESOURCE_ITEMS_WE_HAVE_STORAGE_FOR` | Looting resources | 0x5 | LOOTED_STORABLE_ITEMS | isAtLocation | - |
| 35 | `RUN_AWAY` | Running away | 0x61 | !isAggressionTowardsMe, isNoEnemiesInVicinity | LOCATION_IS_ACCESSIBLE, !IS_CROUCHING_OR_LYING | - |
| 280 | `RUN_AWAY_FORCED` | Running away | 0x61 | - | LOCATION_IS_ACCESSIBLE, !IS_CROUCHING_OR_LYING | - |
| 128 | `RETREAT_HOMETOWN` | Retreating | 0x63 | - | !IS_CROUCHING_OR_LYING | - |
| 127 | `RUN_AWAY_HOMETOWN` | Retreating fast | 0x63 | isAtHomeTown | !IS_CROUCHING_OR_LYING | - |
| 19 | `GO_HOMEBUILDING` | Going home | 0x61 | isAtHome | - | isAtLocation |
| 59 | `GET_READY_FOR_ACTION` | Get ready | 0x65 | isReadyForAction | !IS_CROUCHING_OR_LYING, hasWeaponEquipped | - |
| 4 | `MELEE_ATTACK` | Attacking | 0x41 | isNoEnemiesInVicinity*, isTargetDown | !target swimming, !me swim, isMovementAllowed, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE, isReadyForAction, !isRouteTraceBlocked, isWithin50Meters | - |
| 262 | `RANGED_ATTACK` | Shooting | 0x1 | isNoEnemiesInVicinity*, isTargetDown | !me swim, !iAmBeingMeleeAttacked immediate, hasTwoWorkingArm, !IS_CROUCHING_OR_LYING, hasWeaponEquipped | - |
| 263 | `RANGED_ATTACK_FOCUSED` | Shooting | 0x1 | isTargetDown | !me swim, !iAmBeingMeleeAttacked immediate, hasTwoWorkingArm, !IS_CROUCHING_OR_LYING, hasWeaponEquipped | - |
| 266 | `RANGED_ATTACK_FOCUSED_UNPROVOKED` | Shooting | 0x1 | isTargetDown | !me swim, !iAmBeingMeleeAttacked immediate, hasTwoWorkingArm, !IS_CROUCHING_OR_LYING, hasWeaponEquipped | - |
| 228 | `STEALTH_KNOCKOUT` | Taking out | 0x1 | - | !target swimming, isMovementAllowed, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE, hasOneWorkingArm, !isRouteTraceBlocked | - |
| 227 | `MELEE_ATTACK_ANIMAL` | Attacking | 0x20 | isNoEnemiesInVicinity*, isTargetDown | isMovementAllowed, !TARGET_IS_IMPRISONED, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE, isReadyForAction, !isRouteTraceBlocked, hasWeaponEquipped, isWithin50Meters | - |
| 13 | `ATTACK_ATTACKERS_OF` | Attacking attackers of | 0x61 | !isAggressionTowards, !isAggressionTowardsMe, !isAlliesFighting* | - | isTargetDown |
| 34 | `ATTACK_TROUBLE_MAKERS` | Attacking trouble makers | 0x21 | !isGeneralAggressionLevel | - | isTargetDown |
| 149 | `MAN_A_TURRET` | Turret guard | 0x1 | isNoEnemiesInVicinity* | hasOneWorkingArm | using target turret |
| 222 | `MAN_A_TURRET_ON_BUILDING` | Turret home guard | 0x1 | isNoEnemiesInVicinity* | - | using target turret |
| 146 | `USE_TURRET` | Turret gunner | 0x1 | using target turret*, using any turret | hasOneWorkingArm, !IS_CARRYING_SOMETHING, !iAmBeingMeleeAttacked immediate, isBuildingComplete, BUILDING_HAS_POWER, !hasWeaponEquipped, isAtLocation | - |
| 235 | `SHOOT_AT_TARGET` | Turret gunner | 0x1 | isTargetDown | using any turret | - |
| 234 | `MAN_A_TURRET_PLAYER_JOB` | Turret guard | 0x1 | isNoEnemiesInVicinity*, !isAggressionTowardsMe, !isAlliesFighting* | hasOneWorkingArm | using target turret |
| 101 | `DEFEAT_SQUAD` | Assault | 0x61 | - | !IS_CARRYING_SOMETHING | isTargetDown |
| 248 | `DEFEAT_SQUAD_LIMIT_CHASE_RANGE` | Attacking Squad | 0x61 | - | !iAmBeingMeleeAttacked general, !IS_CARRYING_SOMETHING | isTargetDown |
| 165 | `TOWN_STALKER` | Stalking | 0x1 | - | - | - |
| 103 | `MAKE_ANNOUNCEMENT` | Diplomatting | 0x1 | isMessageDelivered | !isLeaderDown, atPackageTarget | - |
| 129 | `MAKE_ANNOUNCEMENT_FAST` | Approaching fast | 0x1 | isMessageDelivered | !isLeaderDown, atPackageTarget | - |
| 159 | `SEND_DIALOGUE_SIGNAL` | Talking | 0x41 | isDialogDelivered | isAtLocation | - |
| 161 | `SEND_DIALOGUE_SIGNAL_WITHOUT_MOVING` | Talking | 0x55 | isDialogDelivered | - | - |
| 12 | `PLAYER_TALK_TO` | Talking | 0x45 | none | isAtLocation | - |
| 102 | `SEEK_AND_TALK_AND_SEND_SIGNAL` | Making announcement | 0x41 | none | isAtLocation | - |
| 164 | `FOLLOW_WHILE_TALKING` | Following and talking | 0x41 | none | !IS_CROUCHING_OR_LYING, !iAmBeingMeleeAttacked immediate, isNearTo | - |
| 188 | `WORK_THE_SLAVES` | Working the slaves | 0x1 | none | - | isDialogDelivered |
| 194 | `GIVE_ALL_MY_SLAVES_TO` | Selling slaves | 0x1 | !iHaveSomeSlaves | - | isDialogDelivered |
| 218 | `GIVE_ALL_MY_SLAVES_TO_IF_ITS_AN_IMPORT_TOWN` | Selling slaves | 0x1 | !iHaveSomeSlaves | - | isDialogDelivered |
| 192 | `GO_TO_SOMEWHERE_FOR_DELIVERING_SLAVES` | Delivering slaves | 0x3 | - | !IS_CROUCHING_OR_LYING, atDestinationTown | - |
| 158 | `SHOO_STRANGERS_OUT_OF_MY_BUILDING` | Shooing | 0x41 | !strangers in house | I_AM_INSIDE_TARGET_BUILDING, !ALARMS_IN_THE_VICINITY | isDialogDelivered |
| 283 | `SHOO_STRANGERS_OUT_OF_MY_BUILDING_IF_PRIVATE` | Shooing | 0x41 | !strangers in house | I_AM_INSIDE_TARGET_BUILDING, !ALARMS_IN_THE_VICINITY | isDialogDelivered |
| 17 | `PROTECTION` | Protection | 0x61 | - | !isAggressionTowards, !needsFirstAid | - |
| 16 | `ATTACK_ENEMIES` | Attacking enemies | 0x61 | - | - | isNoEnemiesInVicinity |
| 41 | `ATTACK_ENEMIES_OF_MY_SLAVEMASTER` | Fighting for master | 0x21 | - | - | isNoEnemiesInVicinity |
| 21 | `ATTACK_ENEMIES_AND_NEUTRALS` | Attacking others | 0x61 | - | - | isTargetDown |
| 144 | `TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME` | Home territory aggression | 0x61 | - | - | isNoEnemiesInVicinity |
| 32 | `SELF_PRESERVATION` | Self preservation | 0x65 | !iAmBeingMeleeAttacked general | - | !isAggressionTowardsMe |
| 45 | `BODYGUARD` | Bodyguard | 0x61 | - | !isAggressionTowards, !isAggressionTowardsMe | - |
| 44 | `FOLLOW_PLAYER_ORDER` | Following | 0x61 | isNearTo | !isAggressionTowards, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE | - |
| 253 | `FOLLOW_URGENT_ESCAPE` | Following | 0x61 | isNearTo | !isTargetDown, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE | - |
| 40 | `GET_NEAR_TO` | Following | 0x61 | isNearTo | !isTargetDown, !isAggressionTowards, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE | - |
| 141 | `FOLLOW_SQUADLEADER` | Following leader | 0x61 | - | LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE, !IS_CROUCHING_OR_LYING | - |
| 171 | `FOLLOW_SLAVEMASTER` | Following master | 0x61 | - | - | - |
| 31 | `STAY_CLOSE_TO_TARGET` | Staying close | 0x61 | - | !isTargetDown, !isAggressionTowards, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE | - |
| 289 | `?` | Staying close | 0x20 | - | !isTargetDown, !isAggressionTowards, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS, DESTINATION_IS_ACCESSIBLE_ANIMAL | - |
| 166 | `CHAIN_TARGET` | Restraining | 0x41 | isChained, !isTargetEscapee | hasOneWorkingArm, isAtLocation, TARGET_IS_UNARMED, isTargetDown | - |
| 181 | `STRIP_TARGETS_WEAPONS` | Stripping weapons | 0x41 | TARGET_IS_UNARMED | hasOneWorkingArm, isAtLocation, isTargetDown | - |
| 182 | `PROCESS_AND_STRIP_NEW_SLAVE` | Processing slave | 0x1 | TARGET_LOOKS_LIKE_A_SLAVE | isAtLocation, isChained, targetStandingStill | - |
| 183 | `SLAVE_WATCHING` | Watching slaves | 0x1 | - | - | TARGET_LOOKS_LIKE_A_SLAVE |
| 260 | `NEW_SLAVE_PROCESSING` | Watching for new slaves | 0x1 | - | - | TARGET_LOOKS_LIKE_A_SLAVE |
| 167 | `CAPTURE_NEW_SLAVES` | Capturing slave | 0x1 | - | - | isChained |
| 174 | `GET_SLAVE` | Ordering slaves | 0x1 | TARGET_IS_MY_SLAVE | hasOneWorkingArm, TARGET_IS_CONSCIOUS, isChained, isAtLocation | - |
| 173 | `GATHER_SLAVES_FROM_CAGES` | Gathering slaves | 0x1 | - | - | TARGET_IS_MY_SLAVE |
| 217 | `GATHER_SLAVES_FROM_CAGES_IF_ITS_AN_EXPORT_TOWN` | Gathering slaves for shipping | 0x1 | - | - | TARGET_IS_MY_SLAVE |
| 245 | `GATHER_SLAVES_FROM_CAGES_IF_FEMALE_OR_BEAST` | Gathering unholy slaves | 0x1 | - | - | TARGET_IS_MY_SLAVE |
| 187 | `_SLAVE_OBEDIENCE` | Obedient Slave | 0x75 | none | - | - |
| 168 | `CARRY_WOUNDED_SLAVES` | Carrying slaves | 0x1 | - | - | IS_CARRYING_TARGET_HEALTHY |
| 169 | `PUT_DOWN_CARRIED_DUDE_IF_THEY_CAN_WALK` | Putting down | 0x1 | - | !IS_CARRYING_SOMETHING | - |
| 15 | `PROTECT_ALLIES` | Protect allies | 0x61 | - | - | !isAlliesFighting |
| 288 | `PROTECT_ALLIES_STAY_IN_TOWN` | Protect allies | 0x61 | - | - | !isAlliesFighting |
| 143 | `PROTECT_OWN_SQUAD` | Protect close allies | 0x61 | - | - | !isAggressionTowards |
| 30 | `HOLD_POSITION` | Holding position | 0x75 | - | - | isAtLocation |
| 51 | `STAND_AT_NODE` | Stand at node | 0x61 | atNode | isAtLocation | - |
| 20 | `STAND_AT_SHOPKEEPER_NODE` | Shopkeeper | 0x41 | - | - | atNode |
| 38 | `STAND_AT_GUARD_NODE_HOMEBUILDING` | Static guard | 0x61 | - | !IS_CARRYING_SOMETHING | atNode |
| 268 | `STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY` | Static guard indoors | 0x61 | - | !IS_CARRYING_SOMETHING | atNode |
| 157 | `STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE` | Static town guard | 0x61 | - | !IS_CARRYING_SOMETHING | atNode |
| 47 | `STAND_AT_GENERAL_NODE` | general node | 0x1 | - | - | atNode |
| 42 | `NOT_BE_UNARMED` | Getting a weapon | 0x45 | - | hasOneWorkingArm, hasWeapon | - |
| 176 | `HUNTING_BLOODSMELL` | Blood tracking | 0x61 | - | !IS_CROUCHING_OR_LYING, LOCATION_IS_ACCESSIBLE | - |
| 22 | `PATROL` | Patrolling | 0x21 | - | !IS_CROUCHING_OR_LYING, LOCATION_IS_ACCESSIBLE, !hasWeaponEquipped | - |
| 36 | `PATROL_TOWN` | Patrolling town | 0x21 | - | atDestinationTown, !IS_CARRYING_SOMETHING, !IS_CROUCHING_OR_LYING, LOCATION_IS_ACCESSIBLE, !hasWeaponEquipped | - |
| 37 | `WANDER_TOWN` | Wandering town | 0x1 | - | !IS_CROUCHING_OR_LYING, !hasWeaponEquipped | atNode |
| 118 | `SHOPPING` | Shopping | 0x1 | - | atATown | (unnamed state 85) |
| 119 | `BUY_SHIT` | Shopping | 0x1 | (unnamed state 85) | isAtLocation | - |
| 214 | `RELAX_IN_TOWN_PACKAGE` | Relaxing | 0x1 | - | atATown | IS_WORKING |
| 43 | `STAY_IN_HOME` | Staying home | 0x61 | - | !IS_CROUCHING_OR_LYING, !hasWeaponEquipped, isAtHome | IS_WORKING_PRETEND |
| 255 | `SIT_ON_THRONE` | Reigning | 0x41 | - | !IS_CROUCHING_OR_LYING, !hasWeaponEquipped, isAtHome | IS_WORKING_PRETEND |
| 275 | `SIT_AROUND` | Sitting around | 0x1 | - | !IS_CROUCHING_OR_LYING, !IS_CARRYING_SOMETHING, !hasWeaponEquipped, atATown | IS_WORKING_PRETEND |
| 24 | `WANDERER` | Wandering | 0x21 | atATown | - | isAtLocation |
| 39 | `WANDERING_TRADER` | Wandering trader | 0x1 | - | !hasWeaponEquipped, hasDestinationTown, atDestinationTown | - |
| 55 | `RECRUIT_AT_JOBCENTER` | Recruiting | 0x1 | - | - | isAtLocation |
| 53 | `TRAVEL_TO_TARGET_TOWN` | Travelling | 0x61 | atDestinationTown | !IS_CROUCHING_OR_LYING, hasDestinationTown | - |
| 130 | `TRAVEL_TO_TARGET_TOWN_FAST` | Travelling quickly | 0x61 | atDestinationTown | !IS_CROUCHING_OR_LYING, hasDestinationTown | - |
| 215 | `TRAVEL_TO_TARGET_PACKAGE` | Mission formation | 0x61 | atPackageTarget | !IS_CROUCHING_OR_LYING | - |
| 5 | `FOCUSED_MELEE_ATTACK` | Attacking target | 0x61 | isTargetDown | !target swimming, !TARGET_IS_IMPRISONED, isReadyForAction, !isRouteTraceBlocked | - |
| 61 | `UNPROVOKED_FOCUSED_MELEE_ATTACK` | Attacking target unprovoked | 0x61 | isTargetDown | !target swimming, !TARGET_IS_IMPRISONED, isReadyForAction, !isRouteTraceBlocked | - |
| 150 | `PROSPECTING` | Prospecting | 0x41 | - | !me swim | - |
| 97 | `USE_TRAINING_DUMMY` | Training | 0x1 | - | hasOneWorkingArm, machineHasFreeOperatorSlot, isAtLocation | - |
| 153 | `GO_HOME_AND_GO_TO_BED` | Going to bed | 0x41 | - | !strangers in house P | isInBed |
| 239 | `GO_HOME_AND_GO_TO_BED_SECURE` | Going to bed | 0x41 | - | !strangers in house, allDoorsLocked | isInBed |
| 98 | `USE_BED` | Sleeping | 0x41 | isFullyRested, isInBed | !ALARMS_IN_THE_VICINITY, machineHasFreeOperatorSlot, !hasWeaponEquipped, !IS_CARRYING_SOMETHING, isAtLocation | - |
| 258 | `USE_BED_ORDER` | Sleeping | 0x41 | - | !ALARMS_IN_THE_VICINITY, machineHasFreeOperatorSlot, !hasWeaponEquipped, !IS_CARRYING_SOMETHING, isAtLocation | - |
| 175 | `SLEEP_ON_FLOOR` | Sleeping on ground | 0x61 | isFullyRested | isNoEnemiesInVicinity, !isGeneralAggressionLevel, !isAtHomeTown, !hasWeaponEquipped, !IS_CARRYING_SOMETHING | - |
| 261 | `SLEEP_ON_FLOOR_FAKE_AMBUSH` | Hiding | 0x61 | isFullyRested | isNoEnemiesInVicinity, !isGeneralAggressionLevel, !hasWeaponEquipped, !IS_CARRYING_SOMETHING | - |
| 99 | `PUT_SOMEONE_IN_BED` | Put in bed | 0x1 | isCarriedDudeInBed | machineHasFreeOperatorSlot, IS_CARRYING_SOMETHING, isAtLocation | - |
| 116 | `GET_OUT_OF_BED` | Getting out of bed | 0x41 | !isInBed | canGetUp | - |
| 208 | `GET_OUT_OF_BED_IF_ITS_EMERGENCY` | Getting out of bed! | 0x41 | !isInBed | canGetUp | - |
| 257 | `GET_OUT_OF_BED_ONCE_HEALED` | Getting out of bed | 0x41 | !isInBed | canGetUp | - |
| 105 | `FIND_AND_RESCUE` | Find and rescue | 0x1 | - | - | IS_CARRYING_TARGET |
| 148 | `FIND_AND_RESCUE_IF_THERES_BEDS` | Find and rescue | 0x1 | - | - | IS_CARRYING_TARGET |
| 106 | `FIND_BED_AND_PUT_IN` | Find and put in bed | 0x1 | - | - | isCarriedDudeInBed |
| 100 | `GET_PUT_IN_BED` | Sleeping | 0x41 | isFullyRested | - | - |
| 142 | `FIND_AND_RESCUE_LEADER` | Find and rescue leader | 0x1 | - | isLeaderDown | IS_CARRYING_TARGET |
| 135 | `KILL_A_RANDOM_CAGE_OCCUPANT` | Kill random prisoner | 0x1 | - | - | !MACHINE_OCCUPANT_IS_ALIVE |
| 134 | `KILL_CAGE_OCCUPANT` | Kill prisoner | 0x1 | !MACHINE_OCCUPANT_IS_ALIVE | hasWeaponEquipped, isAtLocation | - |
| 139 | `EAT_A_RANDOM_CAGE_OCCUPANT` | Eat random prisoner | 0x21 | - | - | TARGET_IS_EATEN |
| 282 | `EAT_A_RANDOM_CAGE_OCCUPANT_MEASURED_RATE` | Dinner time | 0x21 | - | - | TARGET_IS_EATEN |
| 230 | `EAT_A_RANDOM_DEAD_BODY` | Eat corpse | 0x61 | - | - | TARGET_IS_EATEN |
| 233 | `EAT_A_RANDOM_KO_BODY` | Eat body | 0x61 | - | - | TARGET_IS_EATEN |
| 232 | `FIND_CROPS_TO_EAT` | Find crops | 0x61 | - | - | !am hungry |
| 231 | `EAT_CROPS` | Eating crops | 0x61 | !am hungry | farm has food, isAtLocation | - |
| 220 | `EAT_TARGET_ALIVE` | Eat prisoner | 0x61 | TARGET_IS_EATEN | isTargetRestrainedOrKO, !IS_CARRYING_SOMETHING, !hasWeaponEquipped, isAtLocation | - |
| 196 | `BEAT_CAGE_OCCUPANT` | Beat prisoner | 0x1 | isTargetDown | TARGET_IS_IMPRISONED, hasWeaponEquipped, isAtLocation | - |
| 136 | `FEED_CORPSE_INTO_MACHINE` | Disposing of corpse | 0x1 | CARRIED_DUDE_NOW_IN_DISPOSAL_MACHINE | isCarriedDudeDead, isAtLocation | - |
| 138 | `FIND_A_DEAD_GUY` | Find corpses | 0x41 | isCarriedDudeDead | - | IS_CARRYING_TARGET |
| 137 | `DEAD_GUYS_GO_IN_THE_POT` | Corpse disposal | 0x1 | - | isCarriedDudeDead | CARRIED_DUDE_NOW_IN_DISPOSAL_MACHINE |
| 107 | `USE_CAGE` | Imprisoned | 0x55 | I_AM_IMPRISONED | machineHasFreeOperatorSlot, isAtLocation | - |
| 108 | `PUT_IN_CAGE` | Put in prison | 0x1 | CARRIED_DUDE_NOW_IN_CAGE | machineHasFreeOperatorSlot, IS_CARRYING_SOMETHING, isAtLocation | - |
| 112 | `FIND_CAGE_AND_PUT_IN` | Find and put in cage | 0x1 | FINAL_FOUND_AND_PUT_CARRIED_IN_A_CAGE | !isCarriedDudeDead | CARRIED_DUDE_NOW_IN_CAGE |
| 281 | `FIND_CAGE_AND_PUT_DEADGUY_IN` | Find and put in cage | 0x1 | FINAL_FOUND_AND_PUT_CARRIED_IN_A_CAGE | - | CARRIED_DUDE_NOW_IN_CAGE |
| 172 | `SLAVE_GET_IN_MY_MASTERS_CAGE` | Getting in my cage | 0x55 | - | - | I_AM_IMPRISONED |
| 211 | `POLICE_FREE_PRISONERS_WHEN_DONE` | Freeing prisoner | 0x1 | - | - | !TARGET_IS_IMPRISONED |
| 276 | `LIBERATE_ALL_THE_PRISONERS` | Liberating prisoners | 0x1 | - | - | !TARGET_IS_IMPRISONED |
| 110 | `RELEASE_PRISONER` | Release from prison | 0x1 | none, !TARGET_IS_IMPRISONED | TARGET_IS_CONSCIOUS, !machineHasFreeOperatorSlot, isAtLocation | - |
| 203 | `ARREST_TARGET` | Arresting | 0x1 | TARGET_IS_ARRESTED | isTargetDown, TARGET_IS_UNARMED, IS_CARRYING_TARGET | - |
| 204 | `HUNT_BOUNTIES` | Bounty hunting | 0x1 | - | - | TARGET_IS_ARRESTED |
| 223 | `PICKUP_INTRUDERS` | Remove Home Intruder | 0x1 | - | !IS_CARRYING_SOMETHING | IS_CARRYING_TARGET |
| 224 | `TAKE_INTRUDER_OUTSIDE` | Intruder out | 0x41 | INTRUDER_IS_OUTSIDE | IS_CARRYING_SOMETHING, LOCATION_IS_ACCESSIBLE | - |
| 273 | `PICKUP_INTRUDERS_TOWN` | Remove Intruder | 0x1 | - | !IS_CARRYING_SOMETHING | IS_CARRYING_TARGET |
| 274 | `TAKE_INTRUDER_OUTSIDE_TOWN` | Throwing out | 0x41 | INTRUDER_IS_OUTSIDE_GATES | IS_CARRYING_SOMETHING, LOCATION_IS_ACCESSIBLE, DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS | - |
| 209 | `INVESTIGATE_ALARMS` | Investigating | 0x1 | !ALARMS_IN_THE_VICINITY | atATown | - |
| 210 | `INVESTIGATE_ALARMS_ALLIES_ONLY` | Investigating | 0x1 | !ALARMS_IN_THE_VICINITY | atATown | - |
| 132 | `FIND_AND_KIDNAP` | Find and kidnap | 0x1 | FINAL_GOT_A_KIDNAP_VICTIM | !IS_CARRYING_SOMETHING | IS_CARRYING_TARGET |
| 254 | `FINAL_KIDNAPPER_CAGE_JOB` | Kidnapper | 0x1 | none | FINAL_GOT_A_KIDNAP_VICTIM, FINAL_FOUND_AND_PUT_CARRIED_IN_A_CAGE | - |

## StateType ids

**Observed** (registry `FUN_140610250`, one `FUN_140610170` call per id; names are the strings passed; the names of the other ids were re-read
from the decompile and agree with this table, except that the decompile shows no registration call for ids 0, 2, 3, 6, 13 and
20, whose names here come from the disassembly). No registration call was found for ids 8, 9, 10, 11, 14, 19, 21, 30, 34, 35, 44,
49, 61, 75, 96, 119, 138, 170; ids 33 and 85 are registered with an empty name.

| id | name |
|---|---|
| 0 | `none` |
| 1 | `isAtLocation` |
| 2 | `isBuildingComplete` |
| 3 | `isSubjectInInventory` |
| 4 | `hasWeapon` |
| 5 | `hasWeaponEquipped` |
| 6 | `isNoEnemiesInVicinity` |
| 7 | `isTargetDown` |
| 12 | `isAlliesFighting` |
| 13 | `isTargetEnemy` |
| 15 | `isItemLootable` |
| 16 | `isAggressionTowards` |
| 17 | `AM_IDLE` |
| 18 | `isAtHome` |
| 20 | `noneR` |
| 22 | `needsFirstAid` |
| 23 | `haveFirstAidKit` |
| 24 | `isCrouching` |
| 25 | `isLyingProne` |
| 26 | `IS_CROUCHING_OR_LYING` |
| 27 | `isMovementAllowed` |
| 28 | `isNearTo` |
| 29 | `isGeneralAggressionLevel` |
| 31 | `atNode` |
| 32 | `hasDestinationTown` |
| 33 | (empty name string) |
| 36 | `atDestinationTown` |
| 37 | `isReadyForAction` |
| 38 | `hasOneWorkingArm` |
| 39 | `isAggressionTowardsMe` |
| 40 | `isWithin10Meters` |
| 41 | `isWithin50Meters` |
| 42 | `targetStandingStill` |
| 43 | `iAmDown` |
| 45 | `canGetUp` |
| 46 | `isAtLocation` |
| 47 | `IS_CARRYING_SOMETHING` |
| 48 | `IS_CARRYING_TARGET` |
| 50 | `hasBuildingMaterials` |
| 51 | `buildingHasBuildingMaterials` |
| 52 | `DOOR_IS_OPEN` |
| 53 | `DOOR_IS_OPEN_HERE` |
| 54 | `isDoorLocked` |
| 55 | `DOOR_IS_LOCKED_HERE` |
| 56 | `DOOR_IS_BROKEN` |
| 57 | `AT_LOCATION_BUILDING_DOOR` |
| 58 | `AT_CURRENT_LOCATION_BUILDING_DOOR` |
| 59 | `DESTINATION_IS_ACCESSIBLE` |
| 60 | `LOCATION_IS_ACCESSIBLE` |
| 62 | `machineHasEmptyInputs` |
| 63 | `isMachineOutputFull` |
| 64 | `isMachineJammed` |
| 65 | `isAnyMachineJammed` |
| 66 | `haveSomeResourcesFrom` |
| 67 | `haveSomeResourcesFor` |
| 68 | `HAVE_SOME_LOOT_FROM` |
| 69 | `isRouteTraceBlocked` |
| 70 | `isObjectMine` |
| 71 | `isBuildingDamaged` |
| 72 | `isFullyRested` |
| 73 | `machineHasFreeOperatorSlot` |
| 74 | `isEnemyForceDefeated` |
| 76 | `isMessageDelivered` |
| 77 | `isCarriedDudeInBed` |
| 78 | `TARGET_IS_IMPRISONED` |
| 79 | `I_AM_IMPRISONED` |
| 80 | `isCarryingArmOK` |
| 81 | `CARRIED_DUDE_NOW_IN_CAGE` |
| 82 | `gotInventoryRoomForResources` |
| 83 | `isInBed` |
| 84 | `isAtAShop` |
| 85 | (empty name string) |
| 86 | `atATown` |
| 87 | `allDoorsLocked` |
| 88 | `isInsideTargetBuilding` |
| 89 | `haveSomeResourcesFromThisMachineButWantThemGoneIfPossible` |
| 90 | `DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS` |
| 91 | `AT_LOCATION_FORTIFICATION_GATE` |
| 92 | `isEnemy` |
| 93 | `BUILDING_HAS_POWER` |
| 94 | `atDestinationTown` |
| 95 | `isAtHomeTown` |
| 97 | `CAGES_ARE_ALL_FULL` |
| 98 | `MACHINE_OCCUPANT_IS_ALIVE` |
| 99 | `isCarriedDudeDead` |
| 100 | `CARRIED_DUDE_NOW_IN_DISPOSAL_MACHINE` |
| 101 | `isLeaderDown` |
| 102 | `isSquadmatesFighting` |
| 103 | `myLegsAreMessedUp` |
| 104 | `using target turret` |
| 105 | `isMachineOutputEmpty` |
| 106 | `machineHasFullInputs` |
| 107 | `I_AM_INSIDE_TARGET_BUILDING` |
| 108 | `strangers in house` |
| 109 | `isDialogDelivered` |
| 110 | `isDialogDelivered` |
| 111 | `AT_LOCATION_BUILDING_DOOR_INSIDE` |
| 112 | `DOOR_IS_LOCKED_AND_IM_INSIDE` |
| 113 | `targetAtATown` |
| 114 | `isWearingShackles_locked` |
| 115 | `IS_CARRYING_TARGET_HEALTHY` |
| 116 | `TARGET_IS_MY_SLAVE` |
| 117 | `TARGET_HAS_BEEN_LOOTED` |
| 118 | `TARGET_IS_UNARMED` |
| 120 | `TARGET_LOOKS_LIKE_A_SLAVE` |
| 121 | `CARRYING_EXCESS_LOOT` |
| 122 | `hasTools` |
| 123 | `IS_WORKING` |
| 124 | `isTargetEscapee` |
| 125 | `TARGET_IS_CONSCIOUS` |
| 126 | `AT_LOCATION_BUILDING_DOOR_OUTSIDE` |
| 127 | `DOOR_IS_LOCKED_AND_IM_OUTSIDE` |
| 128 | `isAlly` |
| 129 | `TARGET_IS_ARRESTED` |
| 130 | `ALARMS_IN_THE_VICINITY` |
| 131 | `atPackageTarget` |
| 132 | `haveroboAidKit` |
| 133 | `needsFirstAid_robot` |
| 134 | `isChained` |
| 135 | `TARGET_IS_EATEN` |
| 136 | `IS_WORKING_PRETEND` |
| 137 | `INTRUDER_IS_OUTSIDE` |
| 139 | `me swim` |
| 140 | `target swimming` |
| 141 | `strangers in house P` |
| 142 | `isTargetRestrainedOrKO` |
| 143 | `farm has food` |
| 144 | `am hungry` |
| 145 | `using any turret` |
| 146 | `target worshiped` |
| 147 | `DITCHED_ALL_RESOURCES` |
| 148 | `LOOTED_STORABLE_ITEMS` |
| 149 | `gotAFoodItem` |
| 150 | `HAVE_SOME_BUILD_MATS` |
| 151 | `hasSplintKit` |
| 152 | `needsSplint` |
| 153 | `FINAL_GOT_A_KIDNAP_VICTIM` |
| 154 | `FINAL_FOUND_AND_PUT_CARRIED_IN_A_CAGE` |
| 155 | `iHaveSomeSlaves` |
| 156 | `hasWeaponEquipped` |
| 157 | `hasWeaponEquipped` |
| 158 | `iAmBeingMeleeAttacked immediate` |
| 159 | `iAmBeingMeleeAttacked general` |
| 160 | `isWithinMyBowRange` |
| 161 | `hasTwoWorkingArm` |
| 162 | `iAmProne` |
| 163 | `isBuildingDestroyed` |
| 164 | `INTRUDER_IS_OUTSIDE_GATES` |
| 165 | `crowdLimit8` |
| 166 | `iHaveSufficientLockSkill` |
| 167 | `iHaveSufficientLockSkillForShackles` |
| 168 | `DESTINATION_IS_ACCESSIBLE_ANIMAL` |
| 169 | `isMachineInputInvalid` |

## Unknowns

- Why `taskType` 289 and 290 exist without an enum name (they build `Task_Follow` and `Task_BashDoor`; row 290 of the TaskData table repeats the data of `BASH_GATE`).
- What the 37 named types without a TaskData row do when an AI_TASK record uses them (the list is in the TaskData registry section), and whether any behaviour of the 147 "goal" types (factory default `Task_Blank`) lives outside this factory.
- What lists A and B of a TaskData row mean exactly, what the `*` marker on an entry (second flag byte) means, and what the registration flag bits (0x1, 0x21, 0x41, 0x55, 0x61, 0x63, 0x77, ...) select; [ai.md](ai.md#unknowns) lists the six category sets they register into.
- What the 18 StateType ids with no registration call and the two ids registered with an empty name (33 and 85) evaluate, and the semantics of most evaluators beyond their names.
- How the effect column (`!` prefix, single effect per row) is used by the planner: the planner search itself is **Unknown** ([ai.md](ai.md#goap-planner-and-taskdata)).

## Implementation outline

1. Add the `taskType` enum (from `fcs_enums.def`, plus the two unnamed values 289 and 290) to `Meitou.Data` and a table taskType to Task class from the first table of this doc (143 types with a class, 147 falling through to `Task_Blank`), generated from the doc's rows or a probe and checked by a test that counts 290 rows and 147 defaults.
2. Hold the TaskData registry as data: one row per registered type (253 rows: text, flag bits, list A, list B, effect). Load it from a committed table of identifiers and numbers (facts only), not from the game; a test checks 251 named types plus 289 and 290 and the 37 unregistered names.
3. Implement StateType evaluators by id (153 registered ids, 151 named), starting with the ones that appear most in lists A and B (`isAtLocation`, `isMovementAllowed`, `hasWeapon`, `isInBed`, `crowdLimit8`); an unregistered or undecoded id evaluates to "unmet" so the planner stays conservative.
4. Build the Tasker factory on that table and plug it into the choose chain and planner of [ai.md](ai.md#implementation-outline); implement the real `Task_*` classes in the order movement ([pathfinding.md](pathfinding.md)), combat ([combat.md](combat.md)), building and machines ([buildings-production.md](buildings-production.md)), shopping and prisoners.
5. Use the same `taskType` numbers for the player's context-menu verbs ([ui-input.md](ui-input.md#7-mouse-and-selection)).
