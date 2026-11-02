# AI task tables

Task names/IDs: `src/Meitou.Data/Gameplay/Ai/TaskType.cs` (all 288 named rows checked against this research). IDs 289/290 are unnamed in FCS. See [AI](ai.md) for runtime semantics; lock, door, cage and lockpicking task behaviour (76-80, 107, 110, 133, 140, 155, 162, 166/167, 185/186, 195-201, 207, 285/286) is in [locks](locks.md). Tables retain factory mappings and all TaskData/state values; repeated enum names and condition names are represented by IDs.

## taskType to Task class

**Verified** (the Tasker factory `FUN_14032ebd0` switches on a byte table at `140330408` indexed by taskType - 1; the table was read
from the exe bytes with a probe, each case body was matched to the RTTI `Task_*` class its constructor installs, and the resulting
type-to-class list agrees for all 290 rows, including 147 default cases). 290 jump-table entries versus
289 names in `fcs_enums.def`: types 289 and 290 build `Task_Follow` and `Task_BashDoor` and have no enum name (**Unknown** why).
147 types fall through to `Task_Blank` (the factory's default case): no behaviour of their own from this factory.
The per-type record census is omitted; it can be regenerated from merged AI_TASK data.

Factory mappings grouped by identical class; each ID occurs exactly once:

- (goal; Task_Blank): 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 24, 30, 32, 33, 34, 37, 38, 39, 41, 42, 43, 45, 46, 47, 48, 49, 50, 54, 55, 56, 57, 58, 59, 63, 66, 70, 84, 85, 86, 88, 89, 90, 91, 94, 101, 104, 105, 106, 109, 111, 112, 113, 114, 115, 117, 118, 120, 122, 125, 126, 131, 132, 135, 137, 138, 139, 142, 143, 144, 145, 148, 149, 151, 152, 153, 154, 155, 157, 158, 160, 167, 168, 169, 172, 173, 177, 178, 179, 183, 184, 187, 188, 189, 190, 191, 192, 193, 194, 195, 197, 202, 203, 204, 205, 206, 211, 212, 214, 217, 218, 222, 223, 230, 232, 233, 234, 237, 238, 239, 242, 243, 245, 248, 250, 251, 252, 254, 255, 259, 260, 265, 268, 270, 271, 273, 275, 276, 277, 279, 281, 282, 283, 288
- Task_AddMaterialsToBuilding: 71
- Task_BashDoor: 81, 123, 226, 272, 287, 290
- Task_BruteForceLock: 286
- Task_BruteForceShackles: 186
- Task_Build: 2, 95
- Task_CloseDoor: 73, 75
- Task_Crouch: 27
- Task_CuffTarget: 166
- Task_CutLock: 285
- Task_CutShackles: 185
- Task_Dismantle: 96
- Task_DogLimbRunningPlay: 278
- Task_EatingCrops: 231
- Task_EatPrisoner: 220
- Task_EmptyMachine: 284
- Task_EmptyMachine / Task_Pickup: 92
- Task_EmptyMachine_BuildMats / Task_Pickup: 247
- Task_EnterBuilding: 156
- Task_EquipBestWeapon: 6, 264
- Task_FeedCorpseIntoMachine: 136
- Task_FillMachine: 93, 124
- Task_FirstAid: 25, 60, 269
- Task_FirstAidRig: 249
- Task_FocusedMeleeAttack: 5, 61
- Task_Follow: 31, 44, 253, 289
- Task_Follow / Task_FollowAndTalk: 164
- Task_FollowLeader: 141, 171
- Task_GetOutOfBed: 116, 208, 257
- Task_GetOutOfCage: 133, 207, 256
- Task_GetSlave: 174
- Task_GetUp: 52, 65
- Task_GoHomeTown: 128
- Task_GoHomeTownFast: 127
- Task_grabOneFood: 244
- Task_HuntingSmell: 176
- Task_InvestigateAlarms: 209, 210
- Task_Kidnapping: 246
- Task_KillPrisoner: 134, 196
- Task_LeaveBuilding: 200
- Task_LiftSomething: 68, 170, 213, 225, 240
- Task_LockDoor: 77, 79, 162, 198
- Task_Loot_AI / Task_Pickup: 3
- Task_Loot_Order: 26
- Task_Loot_ResourcesWeHaveStorageFor: 241
- Task_MakeAnnouncement: 103, 129
- Task_MakeTargetStandStill: 64
- Task_ManTheGate: 180
- Task_MeleeAttack: 4, 227
- Task_Move: 1, 29, 40, 67
- Task_MoveBowRange: 267
- Task_MoveToDoor: 82
- Task_MoveToDoor_CurrentLocation: 83
- Task_MoveToDoor_Gate_ChooseSide: 121
- Task_MoveToDoor_Inside: 163
- Task_MoveToDoor_Outside: 199
- Task_OpenDoor: 72, 74
- Task_OperateMachine: 87, 221
- Task_OperateMachine / Task_UseTurret: 146, 235
- Task_Patrol: 22
- Task_PatrolTown: 36
- Task_PatrolTownViolent: 216
- Task_PickLock: 76
- Task_PickLock_Shackles: 201
- Task_PlayerTalkto: 12
- Task_ProcessNewSlave: 182
- Task_Prospecting: 150
- Task_PutDownObject: 69
- Task_PutInSomething: 99, 108
- Task_RangedAttack: 262, 263, 266
- Task_ReleasePrisoner: 110
- Task_Runaway: 35, 280
- Task_SheatheWeapon: 7
- Task_Shopping: 119
- Task_SleepOnFloor: 175, 261
- Task_StandAtNode: 51
- Task_StandStill: 62
- Task_StealthKO: 228, 229
- Task_StripWeapons: 181
- Task_Stumble: 147
- Task_TakeIntruderOutside: 224
- Task_TakeIntruderOutsideTown: 274
- Task_TakeOffCuffs: 219
- Task_TalktoPlayer: 102, 159, 161
- Task_TownStalker: 165
- Task_TravelToMissionTarget_ArmyFormation: 215
- Task_TravelToTargetTown: 53, 130
- Task_UnCrouch: 28
- Task_UnlockDoor: 78, 80, 140
- Task_UseBed: 98, 100, 258
- Task_UseCage: 107
- Task_UseTrainingDummy: 97
- Task_WorshipTarget: 236

## Build tasks

**Verified** (factory `FUN_14032ebd0`, order labels `FUN_14079b2d0`): player orders 2 "Build", 95 "Repair" (both
`Task_Build`), 96 "Dismantle" (`Task_Dismantle`); 71 `Task_AddMaterialsToBuilding`; 247 `Task_EmptyMachine_BuildMats`.
Conditions: 50 `hasBuildingMaterials` (`FUN_14059ba50`: nothing outstanding, or the character or its backpack holds
one of an outstanding item), 51 `buildingHasBuildingMaterials` (`FUN_1405a39c0`: site not stalled), 150
`HAVE_SOME_BUILD_MATS` (`FUN_140597530`, **Observed**: inventory holds the target's item). `Task_Build`
(`FUN_14034a440`) plays `medic` (sounds `Character_Build_Loop_Start/Stop`), pushes build work each update and ends
when the building is gone, the site stalls (back to the planner) or it completes. Work, delivery, fetching (115 -> 247),
the Engineer job (125), repair and dismantle: [buildings-production](buildings-production.md#construction-tick).
Meitou: rows 2 and 95 both need state 38 `hasOneWorkingArm` (**Observed**: the table, no provider row), so a build or repair order and the Engineer job (125, effect 2) skip a character with no working arm (`ConstructionSystem.HasWorkingArm`); Dismantle (row 96) is not checked.

## Prospecting

**Verified** (`Task_Prospecting` vtable `1416bf310`: init `140351c90`, update `140002f04`'s target; the HUD button `FUN_140721800`). Order 150, flags 0x41, condition `!me swim`. It is a `ProgressBarTask` (bar caption "Prospecting"). Each update: plays the `medic`
animation at speed 1, sets the character's state field (`+0x198`) to 4 (meaning **Unknown**), adds the character's update time (`Character+0xc4`, [game-loop](game-loop.md#the-character-update-budget)) to the progress; at 1.0 it ends the task
(`FUN_1400522c0`) and hands the character's position, its science stat and its name to the Prospecting window ([ui-screens](ui-screens.md#prospecting)). There is no walking, no XP and no target: it runs where the character stands.

## TaskData registry

**Verified** (disassembly of the table builder `FUN_140319a60`, each registration call `FUN_14060ea00(type, memberFn, ...)` parsed,
then every row re-parsed from the decompile of the same function: 253 registration calls with 253 distinct type numbers, and type,
text, flag bits and the contents and order of lists A and B agree for all 253 rows; the decompile drops some trailing call arguments,
so the `!` on an effect and the `*` marker rest on the disassembly alone and are **Observed**). 251 of the 288 named types plus the
two unnamed types 289 and 290 are registered; 37 named types are not: 9, 10, 11, 18, 23, 27, 33, 46, 48, 49, 50, 54, 56, 63, 66, 70, 109, 111, 117,
120, 126, 145, 151, 154, 160, 178, 179, 191, 193, 205, 206, 229, 251, 252, 265, 267, 271.
Columns: display text (field +0x138), registration flag bits, condition list A (TaskData +0x4c; names prefixed `!` are required
false; `*` marks an entry whose second flag byte (+5) is set, meaning **Unknown**), condition list B (+0xa8, at most 11) and the
single effect (+0x128). **List A is what a row achieves, list B what it needs, the effect the state a goal row wants another row
to achieve** (**Observed**, [planner](#planner)). The `ALWAYS_IMPOSSIBLE_TASK` row's text is the string "null". Two states (33 and 85)
are registered with an empty name.

A/B/effect use StateType IDs below; `isAtLocation` is two ids, 1 and 46 (67 Running achieves 46, so it is no provider of 1). `!` and `*` retain their original meanings; literal `none`/`-` remain unchanged. Unmapped or ambiguous names remain literal. Row order is registration order.

| ID | Text | Flags | A | B | Effect |
| ---: | --- | --- | --- | --- | --- |
| 1 | Moving | 0x63 | isAtLocation, !69, 40, 41 | 27, !24, !83, 60, 90, 59 | - |
| 29 | Move order | 0x63 | isAtLocation | !24, !83, 60, 90, 59 | - |
| 67 | Running | 0x63 | isAtLocation, 40, 41 | 27, !24, !83, 60, 90, 59 | - |
| 82 | Moving to door | 0x61 | 57 | 27, !24, !83, 90, 60 | - |
| 163 | Moving to door inside | 0x41 | 111 | 27, !24, !83, 90, 60 | - |
| 83 | Moving to current door | 0x41 | 58 | 27, !24, !83 | - |
| 199 | Moving to door outside | 0x41 | 126 | 27, !24, !83, 90, 60 | - |
| 104 | null | 0x77 | - | 20 | - |
| 180 | Gate guard | 0x1 | - | 91, 52 | - |
| 121 | Moving to gate | 0x61 | 91 | 27, !26, 60 | - |
| 122 | Open gate | 0x61 | 90 | !54 | 52 |
| 123 | Bashing gate | 0x21 | !54, 56 | !70, !iAmBeingMeleeAttacked immediate, 165, hasWeaponEquipped, 91, !52 | - |
| 290 | Bashing gate | 0x21 | !54, 56 | !70, !iAmBeingMeleeAttacked immediate, 165, hasWeaponEquipped, 91, !52 | - |
| 272 | Smashing | 0x21 | 163 | !70, 165, !iAmBeingMeleeAttacked immediate, hasWeaponEquipped, isAtLocation | - |
| 270 | Assaulting town | 0x21 | 90 | - | 163 |
| 189 | Worker | 0x1 | - | - | 123 |
| 190 | Worker | 0x1 | - | - | 136 |
| 87 | Operating machine | 0x1 | 123 | 38, 73, 2, !hasWeaponEquipped, !63, !62, !89, !169, 93, isAtLocation | - |
| 221 | Operating machine | 0x1 | 136 | 38, 73, 2, 93, !hasWeaponEquipped, isAtLocation | - |
| 152 | Operating automatic machine | 0x1 | - | 2, !hasWeaponEquipped | !62 |
| 124 | Hauling to | 0x1 | - | 2, 67, isAtLocation | - |
| 114 | Ditching to make space | 0x1 | 82 | - | !62 |
| 242 | Ditching resources | 0x1 | 147 | - | !62 |
| 184 | Ditching loot | 0x1 | !121 | - | !62 |
| 92 | Collecting resource | 0x1 | 66, !63, !169 | 82, isAtLocation | - |
| 247 | Collecting resource | 0x1 | 150 | 82, isAtLocation | - |
| 284 | Looting | 0x1 | 68 | 82, isAtLocation | - |
| 115 | Finding building mats | 0x1 | 50 | - | 150 |
| 93 | Filling machine | 0x1 | !62, 63* | 2, 67, !169, isAtLocation | - |
| 94 | Try filling machine | 0x1 | 67 | - | 66 |
| 113 | Try emptying machine | 0x1 | !63, 105, !66, !89 | - | !62 |
| 91 | Unjamming machine | 0x1 | !64 | !62, !63 | - |
| 90 | Unjam all machines | 0x1 | !65 | - | !64 |
| 89 | Keeping everything running | 0x1 | - | !65 | - |
| 88 | Delivering | 0x1 | - | isAtLocation | - |
| 84 | Open current door | 0x41 | 60 | - | 53 |
| 74 | Opening door here | 0x41 | 53 | !55, 58 | - |
| 75 | Closing door here | 0x41 | !53 | 58 | - |
| 85 | Open destination door | 0x41 | 59 | - | 52 |
| 72 | Opening door | 0x41 | 52 | !54, 57 | - |
| 73 | Closing door | 0x41 | !52 | !56, 57 | - |
| 77 | Locking door | 0x41 | 54 | !52, 70, !56, 57 | - |
| 78 | Unlocking door | 0x45 | !54 | 70, 57 | - |
| 140 | Unlocking door | 0x1 | !54 | 70, 57 | - |
| 79 | Locking door here | 0x41 | 55 | !52, 70, 58 | - |
| 162 | Locking door from inside | 0x41 | 112 | 70, 111, !52 | - |
| 198 | Locking door from outside | 0x41 | 127 | 70, 126, !52 | - |
| 80 | Unlocking door here | 0x41 | !55 | 70, 58 | - |
| 287 | Bashing door | 0x21 | !55, 56, 52 | !70, !128, hasWeaponEquipped, 57 | - |
| 195 | Locking up cages | 0x1 | - | - | 54 |
| 155 | Locking up | 0x1 | 87 | !strangers in house, 107 | 112 |
| 197 | Locking up from outside | 0x1 | - | !strangers in house, !107 | 127 |
| 86 | Opening up | 0x1 | !87 | - | 52 |
| 156 | Entering building | 0x61 | 107 | 90, 59 | - |
| 216 | Ransacking town | 0x1 | - | hasWeaponEquipped | 107 |
| 200 | Leaving building | 0x61 | !107 | 59 | - |
| 76 | Lockpicking | 0x55 | !54 | 38, 166, isAtLocation, !hasWeaponEquipped | - |
| 201 | Lockpicking Shackles | 0x55 | !114 | 38, 167, isAtLocation, !hasWeaponEquipped | - |
| 219 | Removing Shackles | 0x45 | !134 | !114 | - |
| 202 | Escaping | 0x55 | none | !134, !79 | - |
| 185 | Cutting shackles | 0x55 | !114 | 38, 122, isAtLocation, !hasWeaponEquipped | - |
| 285 | Cutting lock | 0x55 | !54 | 38, 122, isAtLocation, !hasWeaponEquipped | - |
| 186 | Breaking shackles | 0x55 | !114 | 38, isAtLocation | - |
| 286 | Breaking lock | 0x55 | !54 | 38, isAtLocation | - |
| 133 | Get out of cage | 0x74 | !79 | !54 | - |
| 256 | Get out of cage | 0x74 | !79 | - | - |
| 207 | Escape cage | 0x74 | !79 | !54 | - |
| 81 | Bashing door | 0x21 | !54, 56, 52 | !70, !128, hasWeaponEquipped, 57 | - |
| 226 | Bashing door | 0x21 | !54, 56, 52 | !70, !128, hasWeaponEquipped, 57 | - |
| 95 | Repairing | 0x1 | !56, !71 | 38, isAtLocation, !26 | - |
| 125 | Engineer | 0x1 | - | - | 2 |
| 213 | Lifting/Snatching | 0x1 | 48 | 80, 27, !26, 7, isAtLocation | - |
| 240 | Capture Target | 0x1 | 48 | 95, 113, 80, 27, !26, 7, isAtLocation | - |
| 225 | Lifting/Snatching Order | 0x1 | 48 | 80, 27, !26, isAtLocation | - |
| 246 | Kidnapping | 0x1 | 48 | 80, 27, !26, 7, isAtLocation | - |
| 68 | Lifting | 0x1 | 48 | 80, 27, !26, isAtLocation, 7 | - |
| 170 | Lifting and healing | 0x1 | 115 | !78, !22, 80, 27, !26, isAtLocation | - |
| 69 | Putting down | 0x1 | !48, !47 | 47 | - |
| 2 | Building | 0x1 | 2, !71 | 38, 51, isAtLocation, !26 | - |
| 96 | Demolishing | 0x1 | none | isAtLocation | - |
| 71 | Adding materials | 0x1 | 51 | 50, isAtLocation, !26 | - |
| 3 | Collecting item | 0x65 | 3 | 15, isAtLocation | - |
| 14 | Idleness | 0x75 | - | !162, !hasWeaponEquipped | - |
| 28 | Standing | 0x25 | !24, !25, !26 | !83 | - |
| 65 | Getting up | 0x25 | !162 | 45 | - |
| 52 | Waking up | 0x65 | - | 45 | - |
| 147 | Stumbling | 0x21 | - | 45 | - |
| 6 | Equipping weapon | 0x65 | hasWeaponEquipped | !me swim, 38 | - |
| 264 | Equipping crossbow | 0x65 | hasWeaponEquipped | !me swim, 38 | - |
| 7 | Sheathing weapon | 0x65 | !hasWeaponEquipped, !hasWeaponEquipped, !hasWeaponEquipped | - | - |
| 269 | Fixing Legs | 0x45 | !103 | 38, 23, !me swim, !39, !hasWeaponEquipped | - |
| 25 | First aid | 0x45 | !22 | 38, 23, !target swimming, 42, isAtLocation, !hasWeaponEquipped | - |
| 249 | Splinting | 0x45 | !152 | 38, 151, !target swimming, 42, isAtLocation, !hasWeaponEquipped | - |
| 60 | Repairing | 0x45 | !133 | 38, 132, !target swimming, 42, isAtLocation, !hasWeaponEquipped | - |
| 58 | Medic | 0x45 | - | - | !22 |
| 250 | Splint Rigging | 0x5 | - | - | !152 |
| 57 | Robotics | 0x45 | - | !39 | !133 |
| 62 | Standing still | 0x75 | - | !me swim, !39 | - |
| 64 | Stand still dammit! | 0x45 | 42 | !target swimming, !16 | - |
| 237 | Worshiping | 0x41 | - | - | target worshiped |
| 236 | Worshiping | 0x41 | target worshiped | !hasWeaponEquipped, !47 | - |
| 131 | Looting food | 0x1 | - | - | 68 |
| 259 | Picking up food | 0x71 | 149 | - | 3 |
| 277 | Fetch | 0x20 | 149 | - | 3 |
| 278 | Playing 'Limbs' | 0x20 | !149 | - | - |
| 279 | Limb Jealousy | 0x20 | - | - | 28 |
| 243 | Getting food | 0x61 | - | - | 149 |
| 244 | Grabbing food | 0x61 | 149 | !12, 82, isAtLocation | - |
| 8 | Finding weapon | 0x45 | 4 | - | 3 |
| 177 | Looting the dead | 0x45 | - | - | 3 |
| 212 | Confiscating goods | 0x45 | - | - | 3 |
| 26 | Looting | 0x45 | !0 | isAtLocation | - |
| 238 | Foraging animals | 0x1 | - | 82 | 148 |
| 241 | Looting resources | 0x5 | 148 | isAtLocation | - |
| 35 | Running away | 0x61 | !39, 6 | 60, !26 | - |
| 280 | Running away | 0x61 | - | 60, !26 | - |
| 128 | Retreating | 0x63 | - | !26 | - |
| 127 | Retreating fast | 0x63 | 95 | !26 | - |
| 19 | Going home | 0x61 | 18 | - | isAtLocation |
| 59 | Get ready | 0x65 | 37 | !26, hasWeaponEquipped | - |
| 4 | Attacking | 0x41 | 6*, 7 | !target swimming, !me swim, 27, 60, 90, 59, 37, !69, 41 | - |
| 262 | Shooting | 0x1 | 6*, 7 | !me swim, !iAmBeingMeleeAttacked immediate, 161, !26, hasWeaponEquipped | - |
| 263 | Shooting | 0x1 | 7 | !me swim, !iAmBeingMeleeAttacked immediate, 161, !26, hasWeaponEquipped | - |
| 266 | Shooting | 0x1 | 7 | !me swim, !iAmBeingMeleeAttacked immediate, 161, !26, hasWeaponEquipped | - |
| 228 | Taking out | 0x1 | - | !target swimming, 27, 60, 90, 59, 38, !69 | - |
| 227 | Attacking | 0x20 | 6*, 7 | 27, !78, 60, 90, 59, 37, !69, hasWeaponEquipped, 41 | - |
| 13 | Attacking attackers of | 0x61 | !16, !39, !12* | - | 7 |
| 34 | Attacking trouble makers | 0x21 | !29 | - | 7 |
| 149 | Turret guard | 0x1 | 6* | 38 | using target turret |
| 222 | Turret home guard | 0x1 | 6* | - | using target turret |
| 146 | Turret gunner | 0x1 | using target turret*, using any turret | 38, !47, !iAmBeingMeleeAttacked immediate, 2, 93, !hasWeaponEquipped, isAtLocation | - |
| 235 | Turret gunner | 0x1 | 7 | using any turret | - |
| 234 | Turret guard | 0x1 | 6*, !39, !12* | 38 | using target turret |
| 101 | Assault | 0x61 | - | !47 | 7 |
| 248 | Attacking Squad | 0x61 | - | !iAmBeingMeleeAttacked general, !47 | 7 |
| 165 | Stalking | 0x1 | - | - | - |
| 103 | Diplomatting | 0x1 | 76 | !101, 131 | - |
| 129 | Approaching fast | 0x1 | 76 | !101, 131 | - |
| 159 | Talking | 0x41 | isDialogDelivered | isAtLocation | - |
| 161 | Talking | 0x55 | isDialogDelivered | - | - |
| 12 | Talking | 0x45 | none | isAtLocation | - |
| 102 | Making announcement | 0x41 | none | isAtLocation | - |
| 164 | Following and talking | 0x41 | none | !26, !iAmBeingMeleeAttacked immediate, 28 | - |
| 188 | Working the slaves | 0x1 | none | - | isDialogDelivered |
| 194 | Selling slaves | 0x1 | !155 | - | isDialogDelivered |
| 218 | Selling slaves | 0x1 | !155 | - | isDialogDelivered |
| 192 | Delivering slaves | 0x3 | - | !26, atDestinationTown | - |
| 158 | Shooing | 0x41 | !strangers in house | 107, !130 | isDialogDelivered |
| 283 | Shooing | 0x41 | !strangers in house | 107, !130 | isDialogDelivered |
| 17 | Protection | 0x61 | - | !16, !22 | - |
| 16 | Attacking enemies | 0x61 | - | - | 6 |
| 41 | Fighting for master | 0x21 | - | - | 6 |
| 21 | Attacking others | 0x61 | - | - | 7 |
| 144 | Home territory aggression | 0x61 | - | - | 6 |
| 32 | Self preservation | 0x65 | !iAmBeingMeleeAttacked general | - | !39 |
| 45 | Bodyguard | 0x61 | - | !16, !39 | - |
| 44 | Following | 0x61 | 28 | !16, 60, 90, 59 | - |
| 253 | Following | 0x61 | 28 | !7, 60, 90, 59 | - |
| 40 | Following | 0x61 | 28 | !7, !16, 60, 90, 59 | - |
| 141 | Following leader | 0x61 | - | 60, 90, 59, !26 | - |
| 171 | Following master | 0x61 | - | - | - |
| 31 | Staying close | 0x61 | - | !7, !16, 60, 90, 59 | - |
| 289 | Staying close | 0x20 | - | !7, !16, 60, 90, 168 | - |
| 166 | Restraining | 0x41 | 134, !124 | 38, isAtLocation, 118, 7 | - |
| 181 | Stripping weapons | 0x41 | 118 | 38, isAtLocation, 7 | - |
| 182 | Processing slave | 0x1 | 120 | isAtLocation, 134, 42 | - |
| 183 | Watching slaves | 0x1 | - | - | 120 |
| 260 | Watching for new slaves | 0x1 | - | - | 120 |
| 167 | Capturing slave | 0x1 | - | - | 134 |
| 174 | Ordering slaves | 0x1 | 116 | 38, 125, 134, isAtLocation | - |
| 173 | Gathering slaves | 0x1 | - | - | 116 |
| 217 | Gathering slaves for shipping | 0x1 | - | - | 116 |
| 245 | Gathering unholy slaves | 0x1 | - | - | 116 |
| 187 | Obedient Slave | 0x75 | none | - | - |
| 168 | Carrying slaves | 0x1 | - | - | 115 |
| 169 | Putting down | 0x1 | - | !47 | - |
| 15 | Protect allies | 0x61 | - | - | !12 |
| 288 | Protect allies | 0x61 | - | - | !12 |
| 143 | Protect close allies | 0x61 | - | - | !16 |
| 30 | Holding position | 0x75 | - | - | isAtLocation |
| 51 | Stand at node | 0x61 | 31 | isAtLocation | - |
| 20 | Shopkeeper | 0x41 | - | - | 31 |
| 38 | Static guard | 0x61 | - | !47 | 31 |
| 268 | Static guard indoors | 0x61 | - | !47 | 31 |
| 157 | Static town guard | 0x61 | - | !47 | 31 |
| 47 | general node | 0x1 | - | - | 31 |
| 42 | Getting a weapon | 0x45 | - | 38, 4 | - |
| 176 | Blood tracking | 0x61 | - | !26, 60 | - |
| 22 | Patrolling | 0x21 | - | !26, 60, !hasWeaponEquipped | - |
| 36 | Patrolling town | 0x21 | - | atDestinationTown, !47, !26, 60, !hasWeaponEquipped | - |
| 37 | Wandering town | 0x1 | - | !26, !hasWeaponEquipped | 31 |
| 118 | Shopping | 0x1 | - | 86 | (unnamed state 85) |
| 119 | Shopping | 0x1 | (unnamed state 85) | isAtLocation | - |
| 214 | Relaxing | 0x1 | - | 86 | 123 |
| 43 | Staying home | 0x61 | - | !26, !hasWeaponEquipped, 18 | 136 |
| 255 | Reigning | 0x41 | - | !26, !hasWeaponEquipped, 18 | 136 |
| 275 | Sitting around | 0x1 | - | !26, !47, !hasWeaponEquipped, 86 | 136 |
| 24 | Wandering | 0x21 | 86 | - | isAtLocation |
| 39 | Wandering trader | 0x1 | - | !hasWeaponEquipped, 32, atDestinationTown | - |
| 55 | Recruiting | 0x1 | - | - | isAtLocation |
| 53 | Travelling | 0x61 | atDestinationTown | !26, 32 | - |
| 130 | Travelling quickly | 0x61 | atDestinationTown | !26, 32 | - |
| 215 | Mission formation | 0x61 | 131 | !26 | - |
| 5 | Attacking target | 0x61 | 7 | !target swimming, !78, 37, !69 | - |
| 61 | Attacking target unprovoked | 0x61 | 7 | !target swimming, !78, 37, !69 | - |
| 150 | Prospecting | 0x41 | - | !me swim | - |
| 97 | Training | 0x1 | - | 38, 73, isAtLocation | - |
| 153 | Going to bed | 0x41 | - | !strangers in house P | 83 |
| 239 | Going to bed | 0x41 | - | !strangers in house, 87 | 83 |
| 98 | Sleeping | 0x41 | 72, 83 | !130, 73, !hasWeaponEquipped, !47, isAtLocation | - |
| 258 | Sleeping | 0x41 | - | !130, 73, !hasWeaponEquipped, !47, isAtLocation | - |
| 175 | Sleeping on ground | 0x61 | 72 | 6, !29, !95, !hasWeaponEquipped, !47 | - |
| 261 | Hiding | 0x61 | 72 | 6, !29, !hasWeaponEquipped, !47 | - |
| 99 | Put in bed | 0x1 | 77 | 73, 47, isAtLocation | - |
| 116 | Getting out of bed | 0x41 | !83 | 45 | - |
| 208 | Getting out of bed! | 0x41 | !83 | 45 | - |
| 257 | Getting out of bed | 0x41 | !83 | 45 | - |
| 105 | Find and rescue | 0x1 | - | - | 48 |
| 148 | Find and rescue | 0x1 | - | - | 48 |
| 106 | Find and put in bed | 0x1 | - | - | 77 |
| 100 | Sleeping | 0x41 | 72 | - | - |
| 142 | Find and rescue leader | 0x1 | - | 101 | 48 |
| 135 | Kill random prisoner | 0x1 | - | - | !98 |
| 134 | Kill prisoner | 0x1 | !98 | hasWeaponEquipped, isAtLocation | - |
| 139 | Eat random prisoner | 0x21 | - | - | 135 |
| 282 | Dinner time | 0x21 | - | - | 135 |
| 230 | Eat corpse | 0x61 | - | - | 135 |
| 233 | Eat body | 0x61 | - | - | 135 |
| 232 | Find crops | 0x61 | - | - | !am hungry |
| 231 | Eating crops | 0x61 | !am hungry | farm has food, isAtLocation | - |
| 220 | Eat prisoner | 0x61 | 135 | 142, !47, !hasWeaponEquipped, isAtLocation | - |
| 196 | Beat prisoner | 0x1 | 7 | 78, hasWeaponEquipped, isAtLocation | - |
| 136 | Disposing of corpse | 0x1 | 100 | 99, isAtLocation | - |
| 138 | Find corpses | 0x41 | 99 | - | 48 |
| 137 | Corpse disposal | 0x1 | - | 99 | 100 |
| 107 | Imprisoned | 0x55 | 79 | 73, isAtLocation | - |
| 108 | Put in prison | 0x1 | 81 | 73, 47, isAtLocation | - |
| 112 | Find and put in cage | 0x1 | 154 | !99 | 81 |
| 281 | Find and put in cage | 0x1 | 154 | - | 81 |
| 172 | Getting in my cage | 0x55 | - | - | 79 |
| 211 | Freeing prisoner | 0x1 | - | - | !78 |
| 276 | Liberating prisoners | 0x1 | - | - | !78 |
| 110 | Release from prison | 0x1 | 0, !78 | 125, !73, isAtLocation | - |
| 203 | Arresting | 0x1 | 129 | 7, 118, 48 | - |
| 204 | Bounty hunting | 0x1 | - | - | 129 |
| 223 | Remove Home Intruder | 0x1 | - | !47 | 48 |
| 224 | Intruder out | 0x41 | 137 | 47, 60 | - |
| 273 | Remove Intruder | 0x1 | - | !47 | 48 |
| 274 | Throwing out | 0x41 | 164 | 47, 60, 90 | - |
| 209 | Investigating | 0x1 | !130 | 86 | - |
| 210 | Investigating | 0x1 | !130 | 86 | - |
| 132 | Find and kidnap | 0x1 | 153 | !47 | 48 |
| 254 | Kidnapper | 0x1 | none | 153, 154 | - |

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
| 5 | `hasWeaponEquipped`: a weapon in hand that is not ranged (`FUN_140596a30`, **Verified**) |
| 6 | `isNoEnemiesInVicinity` |
| 7 | `isTargetDown` |
| 12 | `isAlliesFighting`: `FUN_14099a350` (**Verified**) true when any sensed ally (sense filter 1, 8) scores above 0 in the fight threat `FUN_14099a190` ([combat goals](#combat-goals)). Row 244 needs it false; not modelled |
| 13 | `isTargetEnemy` |
| 15 | `isItemLootable` |
| 16 | `isAggressionTowards` |
| 17 | `AM_IDLE` |
| 18 | `isAtHome` |
| 20 | `noneR` |
| 22 | `needsFirstAid` |
| 23 | `haveFirstAidKit`: the items or the worn backpack hold a kit of class 1 (`FUN_140598860`, **Verified**; charges not tested) |
| 24 | `isCrouching` |
| 25 | `isLyingProne` |
| 26 | `IS_CROUCHING_OR_LYING` |
| 27 | `isMovementAllowed` ([range](#perma-jobs)) |
| 28 | `isNearTo` |
| 29 | `isGeneralAggressionLevel` |
| 31 | `atNode` |
| 32 | `hasDestinationTown` |
| 33 | (empty name string) |
| 36 | `atDestinationTown` |
| 37 | `isReadyForAction` |
| 38 | `hasOneWorkingArm` |
| 39 | `isAggressionTowardsMe`: the state-16 evaluator `FUN_14059de10` asked of the character itself, i.e. its byte `+0x2b0` (the immediate-threat flag of 158, [combat](combat.md)); the thunk was followed in the exe, an override branch on a global lookup is **Unknown** (**Observed**) |
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
| 62 | `machineHasEmptyInputs` (`FUN_1405974e0`, **Verified**): a building target has an empty input, [Blocked output](buildings-production.md#blocked-output) |
| 63 | `isMachineOutputFull` (`FUN_1405a2ba0`, **Verified**): building slot 175, status `+0x468` == 1 for the production family; Meitou: evaluated for a machine target |
| 64 | `isMachineJammed` (`FUN_1405a2e60`, **Verified**): 62 or 63 of a building target (63 alone for another hand); asked only by rows 91 and 90 (no data starts them); Meitou: evaluated for a machine target |
| 65 | `isAnyMachineJammed` (`FUN_1405a2f00`, **Verified**): the 64 test over a hand list of the AI (**Unknown** list); not wired in Meitou |
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
| 82 | `gotInventoryRoomForResources`: `FUN_14059bd80` (**Verified**) true when the looker's inventory has room for the target's item type, else its worn backpack has, else the animal-carry path (`FUN_14042ee50`); its failure line is "I don't have space to pick that up!". Row 244 needs it to start. Meitou: `Inventory.HasRoomFor`, tested before an NPC food run starts (`TownDutySystem`) |
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
| 103 | `myLegsAreMessedUp`: false when a leg slot is a stump; else true when a leg part is down (`FUN_140644450`) and the KO point is under its max health less stun (`FUN_1409980c0`, **Verified**; the two leg parts, Meitou: parts with a leg collapse part) |
| 104 | `using target turret` |
| 105 | `isMachineOutputEmpty` |
| 106 | `machineHasFullInputs` |
| 107 | `I_AM_INSIDE_TARGET_BUILDING` |
| 108 | `strangers in house` (`FUN_14060e5e0`, **Verified**): the stranger finder above 0, see [stranger states](#stranger-states); not wired in Meitou |
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
| 121 | `CARRYING_EXCESS_LOOT` (`FUN_14059b7b0`, **Verified**): true when the AI's inventory (`AI+0x308`) holds an item passing the stolen predicate `FUN_14079e550` ([carry-loot](carry-loot.md#stolen-ownership)), else when the worn backpack's inventory (`FUN_1405cb320`, the CONTAINER in the back slot) does. The collector `FUN_140748d40` passes a constant strict = 1 to `FUN_140748bc0`, so the second call's 0 is not forwarded (**Observed**, decompiled signature); the collector also lists nested items. The only row naming it is 184 |
| 122 | `hasTools` |
| 123 | `IS_WORKING` |
| 124 | `isTargetEscapee` |
| 125 | `TARGET_IS_CONSCIOUS` (`FUN_14099a480`, **Verified**): a character target not down (carried, ragdolled), unconscious or dead; a non-character target true, none false; no row provides it |
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
| 139 | `me swim`: water state 3, deep water, and 0 when held in a bed or cage (`FUN_140598e90` over `FUN_1405c7fd0`, **Verified**) |
| 140 | `target swimming` |
| 141 | `strangers in house P` (`FUN_14060e640`, **Verified**): false when the building is public, else as 108; see [stranger states](#stranger-states) |
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
| 156 | `hasWeaponEquipped`: a ranged weapon in hand (`FUN_140596a70`, **Verified**); 6 Equipping weapon draws only melee, 264 Equipping crossbow only ranged, else the hand is emptied (`FUN_140346c30`) |
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

## Stranger states

**Verified** (decompile: registrations `FUN_140610250`, evaluators `FUN_14060e5e0` / `FUN_14060e640`, finder `FUN_1408ced70`, public test `FUN_140599680`). 108 `strangers in house` is true when the finder returns above 0
(it returns 1.0 or 0); 141 `strangers in house P` is false first when the target's building is public, else the same finder. Goals 155, 197, 158, 283 and 239 read 108, goal 153 Going to bed needs `!141`
(the "153" in goal 132's list A is state 153, not this goal).
- **Public test** (`FUN_140599680`, **Verified**): the target as a building, replaced by the building its vfunc `0x1d8` names when that is not empty (**Observed**: a bed's house), else the blackboard home; none of either counts as public;
  then Building vfunc `0x290` (slot 82, `FUN_140548750`), which reads the building's flag `+0xc8`, the `public day` flag ([locks](locks.md)), of its parent when it has one. It also returns not public when the building's `+0xd8` is 1 or 0x22 and a byte `+0xe8` of an object it
  looks up is set (which object and what the byte means: **Unknown**, not modelled).
- **Finder**: 0 with no home. Else the first of two scans:
  - the resident's sensed characters tagged SENSE_NEUTRAL (bit 1) and none of bits 3 KO, 4 dead (`+0x5bc`, `FUN_1406215b0`), 7 NPC class 1 LAW_ENFORCEMENT or 2 MILITARY (status `+0xdc` of `+0x1a0`, Character vfunc 14 `FUN_1405e1ed0`; **Verified**), 9 imprisoned
    (mask 0x298; [tags](perception.md#sense-tags-who-is-an-enemy)) that stand in the home building, are up (Character vfunc 6, `isDown` false) and are not a chained slave of the resident's faction (`FUN_14032e2a0`: chained, owner faction the resident's);
  - only for a resident whose faction is not the player's (faction `+0x250` null): the player's characters in the home that are up, not chained slaves of the resident's faction, `+0x2f8` not 2 (not imprisoned) and either not sneaking (`FUN_1405c9750`: stealth mode and not in combat mode) or
    sensed (`FUN_140855960`, which for a sneaker passes when the resident has any sense record of it) - whatever their tag or class, allies too.
- **Meitou** (`AiSystem.StrangersInHouseP`, wired for 141 only): the public test reads the home (the player's Open to public toggle, else the residents' template `public day`); the planner gives a finder row's list B no target, so the tested building is always the home. "Sensed" is standing in the home
  building (a sneaker also needs `SightMap.Sees`, a live test for the record; engine reading); the tag is the faction relation (neutral: not an ally, not an enemy); up is not incapacitated or carried. 108 is not wired (default false): the lock-up round tests strangers itself
  (`LockSystem.StrangerInside`, any non-squad character in the home), so wiring it would change 155/158/239 as well. **Unknown**: the original's decay of the sense record (a stranger who just left may keep the state set), `FUN_140855960`'s fallback when the resident has no record.

## Arrest and prison rows

Rules: [economy 11.5, 11.7](economy.md#115-bail-and-sentence). **Observed** (evaluators and TaskData hooks resolved from the exe's thunk bytes):

| Id | Semantics |
| --- | --- |
| state 129 `TARGET_IS_ARRESTED` (`FUN_14059a380`) | target `+0x2f8 == 2` (imprisoned), or carried (`+0x3d4`) by a character of another faction; a non-character target: true while I carry it |
| state 78 `TARGET_IS_IMPRISONED` (`FUN_1405a1cc0`) | the target character's `+0x2f8 == 2` |
| goal 203 Arresting | desire = AI slot 32 (`FUN_140598a40`): 0 when status `+0x88` is null and the character's ownership town is set, else 1 |
| goal 204 Bounty hunting ("Arrest bounties") | desire = AI slot 33: the free-cage finder's result; target finder `FUN_14099bfc0` (wanted, uncarried, not imprisoned) |
| goals 112 / 281 Find and put in cage | desire `AI::scorePutCarriedDudeInCage` `FUN_1405a1140` (**Verified**): 0 unless the character carries (flag `+0x348`) a resolvable one; then `FUN_1405a0f10`, in this order: the status town `+0x88` unset 0.1 (`DAT_14167fad0`); the squad data's mission-target hand (`FUN_140507070` `+0x48`, the one `Task_TravelToTargetTown` sets) set and not that town 0 (**Observed**: it is the carrier's own mission hand, not the carried body's town; which Meitou fields feed it is **Unknown**, not modelled); an animal (vfunc `0x248`) 0; a law enforcer (`FUN_140599600`) carrying a character with no bounty with the AI's faction (`FUN_140854080` below 1) and a non-zero slave state (`FUN_1405c8aa0`) 1.4 (`DAT_14169bb48`); the carrier regarding it as friendly (vfunc `0x3f0`, 1) 0; dead (`+0x5bc`) 0.5 (`DAT_14167e8d8`); alive 2.0 (`DAT_141682ad0`). Meitou: `TownDutySystem.CageDesire` gates the cage plan (start, and re-read on the look cadence while walking: 0 drops to 274 / put down); the order against 274 is kept as cage first (274's desire is **Unknown**). Finder `AI::findOwnedFreeCage` (a free BF_CAGE). Both are Task_Blank rows with list A 154 and effect 81 (**Verified**, `FUN_140319a60` registration: only 112 gets the `!99` list B, `isCarriedDudeDead`; 281 has none), so a living carried body qualifies for either goal and a dead one only for 281. Data: 281 in the two "addon- law enforcement" and two fish resident packages |
| goal 211 Freeing prisoner | finder `FUN_1408cf3c0` (`BuildingFinder_Cages`): an occupied usable cage whose occupant's sentence is served (`FUN_1405c9f00`) |
| goal 276 Liberating prisoners | finder `FUN_1408cf140` (`BuildingFinder_CagesToLiberate`): a cage of a faction that fails the relation test `FUN_1406b2630` (read as allied) holding an occupant that is not an enemy |
| goal 42 "Getting a weapon" / 8 FIND_WEAPON / 6, 264 equip (**Verified**, decompile) | 42: B 38 hasOneWorkingArm, !4 hasWeapon, desire 2.0 (`AI::scoreDontWantToBeUnarmed`); plans 8 (finder `AI::findWeapon` `FUN_14099b1d0`, effect 3, acted out by row 3 "Collecting item"). Finder, first pass with a match, each within 100: nearest free WEAPON world item it has room for (in a town only one in its location owned by nobody or an enemy); else the first sensed dead body with a weapon (not carried, not an animal, no squad mate on it); else the first KO/crawling non-ally, player-side only with Bandits loot, not loved. `Task_EquipBestWeapon` (finishes on its first update): sections `back` then `hip`, first item whose ranged flag matches (1 for 264) and usable; an NPC whose faction is not a player faction (`FUN_1405c6a40`->`+0x250` null; **Verified**, `FUN_140346c30`) also the first WEAPON of the whole inventory (every section) in inventory order with the matching ranged flag (usable test, AI vfunc `0x68`: **Unknown**); otherwise the hand is emptied. The item goes to Character vfunc `0x3d8`: `CharacterHuman` `FUN_1405dbf80` sets the held-weapon pointer (`+0x6d8`) to it, sets item byte `+0x129` (meaning **Unknown**) and passes it to the fighting component `FUN_140897f30` (**Observed**: reads the held weapon for the attack stats; no inventory move seen). `CharacterAnimal`'s slot is `FUN_1405e58d0` (copies `+0x6d0` to `+0x708`). Meitou: **not modelled** (deferred): the hand weapon is derived from the back and hip sections (`Fighter.Weapon`), with no held-item pointer, so an item elsewhere in the inventory cannot be drawn (the combat tick resets `DrawnWeapon` to the sections); every Meitou acquisition path (loadout, looting, pickup) seats weapons in a free section first, so only a loadout overflow into `main` with both sections full would trigger it. Data: 42 in 19 packages |
| goals 202 Escaping / 256 desire (**Verified**, decompile) | 202: desire 2.0, B !134 isChained, !79 imprisoned, restricted, no finder or acts: its work is the providers, 219 Removing Shackles (A !134, B !114 from 201 / 185 / 186) and the cage rows; never done (list A `none`). 256 desire 1 while imprisoned, not chained, in a building with a lock that is not locked. Data: 202 in the "Escaping slave" packages, 256 in "captured NPC", 133 in none |
| goal 212 Confiscating goods (**Verified**, decompile) | desire 1, finder `AI::findPrisonerWithContraband` `FUN_1408cf6a0`: cages of the current town (`BuildingFinder_Cages` (1, 0x100)) in list order, an occupant no squad mate works on, first with a stolen equipped armour piece it has room for, else any stolen item: the first cage wins. Effect 3, acted out by row 3 (`Task_Loot_AI`), whose law-enforcement branch takes one unit of each stolen item ([confiscation](carry-loot.md#33-npc-looting)) |
| goals 131 Looting food / 243 Getting food (**Verified**, decompile) | finder `AI::findSomethingToLootFoodFrom` `FUN_1408cb650` over the blackboard's target town, else the current one, else the zone grid: buildings with a non-empty inventory, owned by the looter's faction or the looter not the player's, not inside another building for an animal, not path-impossible, not a shop of the looter's faction; without function stuff when it holds food the looter may eat, with function stuff when (another owner or no production building) and it holds function-3 food (an animal also 15). Nearest wins. Neither needs an own town (243's desire, AI slot 26, passes through to slot 24). 131: effect 68 by 284 `Task_EmptyMachine` (moves output items while there is room; nothing for a building without function stuff); 243: effect 149 by 244 `Task_grabOneFood`, whose list B needs state 82 (room) and not 12 (an ally fighting), so a full inventory starts no run |
| goal 184 Ditching loot / "Put loot in storage" (finder `FUN_14059b040`, registration **Verified**; the readings below **Observed**) | Listed by 11 packages of the base data (Cage Warden, Storage Guard, Slaver Storage Guard, the law-enforcement add-ons, slave catching, ...). List A `!121`, no desire function (default), effect `!62` (row 93 Filling machine puts into the target). Finder: none unless the character carries a state-121 item and has a current town (status `+0x88`). Candidates are storage buildings, functions 2 BF_RESOURCE_STORAGE and 13 BF_GENERAL_STORAGE (the nearest-allies finder's mask `0x2004`; the town lists `0xd` and `2`), kept when `StorageBuilding` slot `0x4a0` (`FUN_1402988c0`) takes one of the stolen items (the record's `itemtype limit` equals the item type and, with a `limit inventory` list, the item is in it; plus an inventory room test). A character with a home town (Ownerships `+0x38`) standing in a town of type 2 reads the *home* town's lists with no owner test; any other reads the current town through `BuildingFinderNearestAllies`, whose building test `FUN_1408ca5d0` passes the looker's own faction's buildings and, through `canIUseThisBuilding` (`FUN_140621230`) on the town, the town's faction's (a storage has an inventory, so no ally passes). Nearest by squared 3D distance wins, within the orders-movement bound (`AI+0x2bc`: 2 ROAM_FAR 4.246733e7, **Verified** `DAT_1416efdcc`; 1 STAY_CLOSE `(meiDistance + 200 + tolerance)^2`; 0 HOLD_GROUND `(meiDistance + 40 + tolerance)^2`; else `1000^2`). Factor 1 with a target, else 0. What row 93 moves at the building is **Unknown** |
| 133 / 207, 256 `Task_GetOutOfCage` | constructor flag "escape" 0 for 133, 1 for 207 and 256 ([locks](locks.md#cages-and-beds)) |

## Prisoner fate and slave rows

**Observed** (TaskData registrations in `FUN_140319a60`: desire at `+0x108`, finder at `+0x160`; the desire thunks resolve to `AI` vtable slots; task classes from their vtables).
Meitou: [simulation, captives](../simulation.md#captives).

| Goal | Desire | Finder / rule |
| --- | --- | --- |
| 135 Kill random prisoner, 282 Dinner time | AI slot 20 `FUN_14032e9b0` (**Verified**): 0 with no town in the status (`+0x88`); otherwise the town's timer entry is created first when absent (before any cage or weapon test), then 1 when the town's kill timer has passed, or every cage it knows is held by a live character (`FUN_1405a1ab0`, the CAGES_ARE_ALL_FULL test; false with no cage); else 0. 135 has no list A or B and its desire no weapon test: the weapon is 134 Kill prisoner's B list (`hasWeaponEquipped`), a plan step the equip row 6 (`Task_EquipBestWeapon`, `FUN_140346c30`: back, hip, then for a non-player any WEAPON item of the inventory) satisfies, so an unarmed captor plans an equip first and one with nothing to draw has no plan (reading list A/B as plan preconditions is an inference). A town not yet in the timer map gets now + uniform[1, 15) h (`FUN_14034a810`, `FUN_1409b1c80(1, 15)`), a global map keyed by the town | `FUN_1408d0d90` (also 139): keeps a remembered occupied cage, else the cage finder `FUN_1408d0770(ai, occupied, 8)`: function-8 buildings, weight clamp(1 - d^2/4608^2, 1e-5, 1), x100 same faction (another faction's only when `canIUseThisBuilding`, `FUN_140621230`, passes: [locks](locks.md#beds)), x100 in the character's home, x1e-5 for a law enforcer when the cage's parent building is not public; the best weight wins. Its candidates are the economy 11.7 gatherer's: the status town (the same `+0x88` the kill rate reads), else the faction's nearest within 3953, the buildings within 1.1 x its radius, plus the home's town ([economy](economy.md#117-arrest), **Verified** `FUN_1408cbea0`, `FUN_140a00690`). No target when the occupant's RACE `robot` (`+0x7c`) is set; else the occupant fires event 10 MARKED_FOR_DEATH at the chooser |
| 139 Eat random prisoner | slot 19 (1) | as 135 |
| 183 Watching slaves | slot 61 `FUN_140597b20` (body **Verified**; the three rows' slot **Observed**): with a target (handle type not 0xb), slot 59 `FUN_14059e1a0`; with none, 1 while the worker does not carry (`+0x348`) and its posture (vfunc `0x368`, Character `+0xe0`) is 0, else 0. A candidate that is not the current task is scored with its target cleared, so a crawling, staying-low or knocked-out worker never starts one of the three (a running one is scored with its target) | `FUN_1408cd630` (**Verified**): none while itself held; first the worker's sensed list with include mask 0x60 (sense-tag bits 5 / 6, slave state 1 / 2) and exclude 0x110 (bit 4 dead, bit 8 **Unknown**, set by the sense update for a looker not of the player's faction), the first entry in slave state whose `FUN_1408ca3d0` holds is the target at once (no distance, floor or cage; a free or escaped slave in the open, or a caged one in sight; Meitou's first pass takes the free ones only, caged ones in sight fall to the cage pass: engine choice); else `BuildingFinder_SlavesToProcess` over its home town: occupied function-8 cages of non-player factions passing the `BuildingFinder_Cages` tests, occupant not of its own squad and needing processing, the cage on the watcher's floor (vfunc `0x60`, `+0xa4`) and in the same building (the `isIndoors` handles, vfunc `0x1d8`, compare equal; two outdoor parties are equal) and within 3D squared distance 32400 (`DAT_1416e18fc`) of it (**Verified**, `FUN_1408cd630`) |
| 260 Watching for new slaves | slot 61 | `FUN_1408cc110`: the same building finder; the first occupant that is not down (vfunc 6) and in slave state 0 |
| 167 Capturing slave | slot 61 (as 183) | `FUN_1408cdd00` (**Verified**): sensed characters tagged KO or CRAWLING (0x4008; CRAWLING = posture 1 or 2 out of combat mode, or in a bed) and not ally or dead (0x11), counted down when unconscious or posture > 0; uncarried, free, not animals, not in the give-up list, NPC class (status `+0xdc`) not 1 LAW_ENFORCEMENT, 3 TRADER or 5 DIPLOMAT, slave state not 1, not maimed (`FUN_140644170`: a leg stump, or both arms stumps); the nearest. The 168 carry finder (`FUN_1408cdf50`) uses the same down test but refuses posture 1 (staying low). Meitou: `CaptiveSystem.Down`/`Maimed` (plus a not-shackled guard). Plan and tasks: [Slave work, shout and capture](#slave-work-shout-and-capture) |
| 168 Carrying slaves | `FUN_140599480` (the rescue desire) | `FUN_1408cdf50`: sensed (0x20 / 0x210) down characters in slave state, owned by its faction (status `+0xe0`), uncarried, free; the nearest |
| 173 Gathering slaves, 245 Gathering unholy slaves | slot 63 `FUN_14059b940`: 1 while the platoon's slave list holds at most 3 characters up and free (vfunc 6 false, `+0x2f8` 0) | 173 `FUN_1408cc4b0`: the nearest occupied processable cage of its faction in the squad's current town; 245 `FUN_1408d1410`: own-faction cages, the first occupant that is a woman (appearance `sex female`, `FUN_140295a50`) or of another race than the gatherer |
| 217 Gathering slaves for shipping | slot 64 `FUN_140597b80` (**Verified**): 1 in a TOWN or OUTPOST (town `+0xd8` 2 or 1); when the town's owner faction is the player's (`+0x250` set) or anti-slavery (byte `+8`, `anti slavery`), only if the worker's faction is its ally (`FUN_1406b2630`; the same faction counts), else 0 | `FUN_1408cc600` (**Verified**): `BuildingFinder_Cages` (`FUN_1408db510`) over the blackboard's town, cages the worker may use that are occupied (`UseableStuff` vfunc `0x4f0` false) and that no other member of the squad works (`FUN_1402690e0` at most 0, the finder's `+0x3c`); then, for a cage whose owner is not the player's, dropped when the occupant is missing, down (vfunc `0x30`: KO, dead, carried, ragdolled, posture 3 or more) or flagged by vfunc `0x360` (byte `+0x5b8`, no writer in the decompile, **Unknown**), or when two squad mates already work the occupant (`FUN_1402690e0` at least 2: `Blackboard::howManyGuysWorkingOnThisSubTarget`, the blackboard entries whose worker is neither the asker nor the target and whose sub-target is the target; not the give-up list); the nearest |
| 174 Ordering slaves (`Task_GetSlave`) | slot 19 | its start posts message type 5 (target, self) to the global queue `DAT_1421346d8` and ends; what the queue does with type 5 is **Unknown** (effect: 116 TARGET_IS_MY_SLAVE) |
| 182 Processing slave (`Task_ProcessNewSlave`) | slot 19 | 3 s of `medic`; then every item of the whole inventory whose record type is WEAPON (2), then ARMOUR (3), ITEM (4), CONTAINER (46) (`FUN_1407469c0`, **Verified**), not locked and not ARMOUR with `stigma` 6 (+0x244, slave garments), goes to the processor's storage or inventory (the WEAPON and ITEM passes are skipped when the processor's faction has byte +0x30 set, meaning **Unknown**); only when the captive's `legs` section is empty is the faction's `slave clothing` made and put on, each template its section does not already hold (`FUN_1403560e0`); event 35 INTRODUCING_NEW_SLAVE (37 RECAPTURED_A_SLAVE when vfunc `0x3e0`) to it; slave state 1 (`FUN_1405a4940`), the owner setter `FUN_14032e430` (leaves the old owner's list, writes the new owner's faction to status `+0xe0`, joins its list) called **only when the owner handle `+0x328` resolves to nothing**, so a live owner and its faction stay (**Verified**, `FUN_14035aa20`); then the captive's `OrdersReceiver` is reset: `FUN_140507530` (state, one-shot orders `+0x70`) and `FUN_140507280` (perma-jobs `+0x88`) destroy every entry (**Verified**), and job 187 Obedient Slave is added toward the owner; every platoon of the processor's faction and every sensed ally lists it (`FUN_14067aca0`) |
| 188 Working the slaves | slot 65 `FUN_14059a0a0`: 0.25 x the platoon's slave list size | `FUN_1408cd940`: a random live, uncarried, free slave of the platoon's list not in the give-up list; B list isDialogDelivered; the event is the row's own, 38 SHOUT_AT_SLAVE_WORKER (**Verified**: the registration stores 0x26 at TaskData `+0x130`) |
| 192 Delivering slaves | slot 65 | none; B: !26, atDestinationTown |
| 194 / 218 Selling slaves | 65; 218 slot 66 `FUN_140597c70`: only in a prison or slave camp (town type 7, 5) | `FUN_1408ca950`: a random allied building of designation flag 0x80000 in the town whose owner is a character or squad (`+0xd8` 1 / 0x22): its leader; B isDialogDelivered (event 39 SLAVE_DELIVERY) |
| 172 Getting in my cage | slot 19 | `FUN_1408caaf0`: for a slave whose owner handle is a character or squad: the nearest free cage of the owner's faction |
| 189 / 190 Worker | slot 19 | `FUN_1408cac60` / `FUN_1408cadb0`: a random building of the platoon's town whose BuildingFunction is in mask 0x41a (bits MINE, RESEARCH, REFINERY, CRAFTING); a character whose side differs from the town's (squad ownership `+0x250`) only works there as a slave of state 1; none found: 0. Effects IS_WORKING (123, 189) and IS_WORKING_PRETEND (136, 190), acted out by 87 / 221 Operating machine |
| 187 Obedient Slave | - | no desire or finder: a job added when a character is enslaved (the slave window's sale, processing), scored by its own branch in `FUN_14050e5b0` (see below) |
| 230 Eat corpse (**Verified**) | slot 19 (1) | `AI::findCorpse_onGround` `FUN_1408cfde0`: sensed DEAD, lying (ragdoll or carried), race `carriable`; an animal eater: not a robot, not its own race; not carried, no squad mate on it; nearest. Effect 135 TARGET_IS_EATEN is always false (`AI::noneState`), so it always plans 220 on the target and stays chosen while the finder finds it |
| 233 Eat body (**Verified**) | slot 19 (1) | `AI::findDeadOrKOBody_onGround` `FUN_1408ce680`: sensed KO, DEAD or CRAWLING, not ALLY, not a robot, not the eater's race, lying or unconscious or dead, not carried, no squad mate on it; nearest (a KO victim is eaten alive) |
| 232 Find crops (**Verified**) | slot 19 (1) | `FUN_1408cb810`: in the blackboard's target town, else the current one, whose faction does not coexist with the eater's (`FUN_1406b23c0`): a current farm target kept, else a random farm (class 13) with grown > 0.4 and died < 0.9. Effect 144 "am hungry" is always true, so it always plans 231 `Task_EatingCrops`: each update `eat` clip, crime 14 on the eater against the farm's faction, `FarmBuilding::eat(age 1 x dt)`: an accumulator past 1 destroys the next plant (growth -10, the harvest index), one plant per game second. Data: 230 in 5 packages and 7 races' `AI Goals`, 233 in Beak Thing and the spiders, 232 in the river and swamp raptors |
| 237 / 236 Worshiping (**Verified**) | slot 19 (1) | `AI::findFogmanWorshipTarget` `FUN_1408ce880`: a remembered cage with an occupant kept; else in the nearest town the first cage whose occupant is being eaten. Effect 146 is always false, so 236 `Task_WorshipTarget` runs on it: beyond 60 walk to the cage, within stop, face it, `bowing`, every uniform[3, 14) game s event 53 (no target); no end of its own. Data: 237 in the fog packages |
| 277 / 278 / 279 dog limb play (**Verified**) | 277 1; 278 `AI::scoreDogPlaying` (mouthful: 0.3 a player squad's dog over 0.4, else 1.1; a player adult over 0.95: 0); 279 `AI::scoreDogPlayingChase` (1.2 NPC; player: 0 adult, 0.3 over 0.2, 0.6 younger) | 277 `AI::findLimbOnGround`: nearest limb (function 17) within 200 in its location (an adult not while fed or hunger above 2.75; returns 1 even with no target). 278 `Task_DogLimbRunningPlay`: first run 80-180 past the centroid of who it senses, away; then random legs of 1000-1400, a new one under 200 left, speed 999; ends when the mouth empties. 279 `AI::findAllyAnimalWithFoodInMouth`: first sensed awake ALLY animal with a mouthful, followed. State 149: a mouthful or food in the inventory or backpack. Data: Bonedog and Boneyard Wolf race `AI Goals` |

- **Kill** (`Task_KillPrisoner`, 134 and 196): faces the target (a character, or a cage's occupant) and plays `chop down static`; each time the clip passes 0.55 the
  character's weapon attack (`FUN_1408868b0`, the race multipliers included) lands through the target's take-hit virtual and the target says event 11 SCREAMING_TORTURE. When the
  target is dead the character's town gets a new kill time, now + uniform[1, 15) h. 134 needs a drawn weapon.
- **Eat** (`Task_EatPrisoner`, 220): walks to within 20 of the target (XZ); a robot target ends it. Then each update: faces it, plays `eat`, says event 52 EATING_SOMETHING_SOUNDS,
  and calls the target's being-eaten virtual with 1.0 (`FUN_1404356f0`): unless dead, and while vfunc `0x358` is false (read as "conscious", **Unknown**) or its byte `+0x4f` is set, the
  target says event 11 and gains toughness XP (`FUN_1408c6430(stats, dt, 10, 21)`: the continuous entry, [character-stats](character-stats.md#levelling-xp), amount 10, stat 21 Toughness), then the
  medical update `FUN_140650370`: rate = 1 x lerp(2, 0.4, toughness x stat scale x 0.01) per second (x5 instead when medical `+0x164`, **Unknown**); parts from the last down:
  the first with h - 1.1 s > -M loses rate x dt from flesh and from the treated pool and the update ends; a limb with nothing left is severed when the global `DAT_142133504`
  is set; with nothing left, a counter `+0x15c` rises, and past 5 the character dies ("eaten"; "{1} has been eaten alive." for the player's). A robot is never eaten. The end
  of the task resets the town's kill time as Kill's does. A target not imprisoned and with `+0x278` set is bitten once and the eater re-ordered (order 0x23, **Unknown**).
- Whether the kill timer map is saved is **Unknown** (Meitou keeps it in the running game only).

### Slave work, shout and capture

Meitou: [simulation, captives](../simulation.md#captives).
- **Worker** (189 `AUTO_LABOURING_MINES`, 190 `..._PRETEND`; **Verified** registrations in `FUN_140319a60`: effects 0x7b IS_WORKING and 0x88 IS_WORKING_PRETEND, finders `FUN_1408cac60` /
  `FUN_1408cadb0`; only 190 commits, 0.5 + r x 2 h, crowd 1). They are not slave-only: the data lists them in "worker fighter indie resident", "Shop" (slave list), "working mines and farms",
  "Work, Protect, Capture", "South drone sacrifice farmer" and, in the Slave role list, "slave worker day guard" (171 at 0.1, 189, 172 at night, 190). The finder asks the platoon's town
  (`Blackboard +0x88`) for a building through `BuildingFinder_SpecialFunction` (mask 0x41a read as bits, **Observed**: 1 MINE, 3 RESEARCH, 4 REFINERY, 10 CRAFTING) and takes one at random
  (slot 2 `FUN_1409fb020`: index `FUN_1409b3b40(0, n - 1)`); the town test is the one in the table above. Acting out is 87 (real: needs the machine's inputs and output room, power, a free operator slot) or
  221 (pretend); both `Task_OperateMachine` ([sitting and the held tool](ai-tasks.md#town-goals), [production](buildings-production.md#production)). Meitou's NPC towns have no running machines, so both are
  the same standing at the building at its functionality's clip (`stonemine` by default): **engine choice**, the building's `use range` is not read (30 units).
- **Obedient Slave** (187, flags 0x75, TaskData kind +4 = 1 and +8 = 0 like a plain job, no lists, no desire or finder of its own, task class `Task_Blank`). Meitou:
  [simulation, captives](../simulation.md#captives). A **perma-job** (it sits in the character's job list, `+0x88`), not a goal, whoever the character is.
  - **Added and removed** (**Verified**, `FUN_1405a4940` slave state setter -> `FUN_14032e0d0`): when the slave state *changes* (the setter does nothing for the same state), a change **to 1**
    destroys the one-shot orders and the perma-jobs (`FUN_140507530`, `FUN_140507280`) and adds job 187 toward the character's owner handle (character `+0x328`, `FUN_1405086d0` with the
    permanent flag, which turns the jobs switch on); a change **to 0 or 2** removes the first job of type 187 and stops, or, when the list holds none, destroys the orders and perma-jobs.
    So freeing, an escape (state 2) or bail ends the obedience at once. `Character::setChainedMode` (`FUN_14032e590`, which sets state 1 first, writes the owner handle from its target and the owner's
    faction, resets the orders object, adds the job and then puts the shackles on, **Verified**) and processing (182, `FUN_14035aa20`) do the same reset themselves, whatever the old state. The job is
    saved with the list (`pjob<i>` 187, `pjobT<i>` the owner handle).
  - **Scored** (**Verified**, `FUN_14050e5b0`, both job passes, the urgent one at step 3 and the full one at step 5; the jobs switch gates it like any job, the list of ignoring types does not hold 187):
    for a character in slave state 1 (`FUN_1405c8aa0`) that is **not sneaking** (`Character::isStealthMode` `FUN_1405c9750`) and whose owner handle (`+0x328`) names a character (type 1) or a squad (0x5b)
    that resolves to a platoon, the entry is not a candidate itself: the owner platoon's **Slave role list** (role 4, priorities 1 to 3: mask 0xe, `FUN_14026b790`) is loaded into the slave's own goal
    list (`+0x178`) and scored with the normal scorer `FUN_14050df10` (desire x finder x weight x crowd, hour window; a goal that plans runs) into the same candidate map. Their keys are the
    goals' scores (typically 0 to 2), below the first jobs' keys (N + 8, N + 7, ...). **So at step 3 the owner's Slave goals beat the character's own priority-3 goals (Self preservation, Getting food, 15 Protect allies)
    and every job but the kind-2 ones**; a slave whose list plans something never reaches them. With the gate failing (not state 1, or sneaking) the entry is scored as a plain job, whose
    desire is a default function (`LAB_1400215ee`, **Unknown**) on a blank task: nothing is read as running. If the list plans nothing, the step goes on as for any character.
  - **What the Slave lists hold** (**Verified**, the resolved Slave lists of the AI_PACKAGE records of the merged base game, Dialogue and rebirth through `AiDatabase`, 64 entries before inheritance): 171 Following master (in 35 packages,
    priority 2), 172 Getting in my cage (23, priority 2), 41 Attack enemies of my slavemaster and 143 Protect own squad (5 and 4, priority 3), 35 Running away (the police and prison packages),
    14 Idleness (the cage wardens at weights 0.05 to 0.11), and the slave camps' workers: package "slave worker day guard" 171 at 0.1, 189 Worker 5-22 at weight 2, 190 Worker (pretend) 5-22 at 1.5, 172 at
    night (22-5) at 0.5, 14 at 0.05; "Shop" and "Shop catun" 190. Read so (**Observed**): a slave owned by a squad running that package works the town's mines by day and goes to its cage at night.
  - **The plan** (**Verified** for the targets): 171 is `Task_FollowLeader` (the same class as 141), which follows the slave **owner** if the character has one, else its squad leader
    ([pathfinding](pathfinding.md#following)); it copies the leader's sneaking every update, so **a slave following a master who is not sneaking cannot sneak**, and escapes by sneaking only while it stands or works.
    189 / 190 are the worker rows ([above](#slave-work-shout-and-capture)): the finder asks the **blackboard's current town** (`+0x88`) of the slave's own platoon, which the AI's rule writes from the
    platoon's leader (`FUN_1402688d0`, [AI](ai.md#package-classes)); a character whose side differs from the town's works only in slave state 1. 172 is the slave's own walk into a free cage of its
    owner's faction (finder `FUN_1408caaf0`). Whether the player platoon's blackboard town is kept up by that rule like an NPC platoon's is **Unknown** (read so).
  - **Other readers of "has job 187"** (`FUN_140597380`): 15 Protect allies' desire `FUN_140599b90` is **0** for a slave in state 1 holding it (**Verified**, sneaking or not); the sense relation
    tests `FUN_1408569a0` (for a slave with the job whose owner resolves, true when the owner's vfunc `0x3f0` holds on the other character, read as "counts its owner's enemies") and `FUN_140852700` (job held and no orders: a looker's filter in the sense tests
    `FUN_140852750` / `FUN_140855a80`) are **Observed**, meaning **Unknown**; `chooseBestAttackTarget` (`FUN_14059e300`) reads it on a candidate: a state-1 slave owned by the chooser's faction (`FUN_14032e2a0`) that holds it takes another branch of the score (**Observed**, effect **Unknown**).
  - The status tooltip tells the player: "Your character will have a permanent job assigned called [Obedient slave]. While this is active he will automatically work for his master."
  - **Meitou**: the AI's slaves (a slave member of its owner's squad in the Slave role; one in another squad takes the Slave list of the squad it obeys, `CharacterCold.SlaveMaster`: the owner squad of
    the squad factory's slaves, the slaver who bought or processed it, the arresting guard's squad, the receiver of a delivery) run it through the AI ([ai](ai.md#goal-loading)). A **player character** gets the job in
    its list (`ObedientSlave`, step 3 of the decision step): the gate as above; the best goal of the master squad's Slave list in its hour by desire x weight (x1.25 the one running, a tie to the
    later-listed) among **171** (the owner, else the master squad's leader), **189 / 190** (a random mine, research bench, refinery or crafting bench of the town the slave's squad leader stands in,
    walked to and stood at at its work clip, the AI's engine choice; no machine runs and the hunger rate is not taken) and **14**. Not acted out for a player character (**gap**): 172 (night cage), 41, 143 and 35
    (the slave does not fight or flee, as the original's step 3 already comes before its own Self preservation). A faction owner with no squad has no master: the job is held but nothing runs. The job names the master squad's leader (the original's handle names a character or a squad). `SlaveMaster` is
    runtime only: after a load the owner is found again from the job's saved handle when it names a character.
- **Shout** (188, `Task_TalktoPlayer`, slots 1 and 2 `FUN_14034c5a0` / `FUN_14034c7c0`): the event is the row's (TaskData `+0x130`: **Verified** 0x26 for 188, 0x27 for 194 and 218, 9 for 158 and 283).
  Start fires it at the target (`FUN_140684990`); each update walks to the target (vfunc `0x3b0`), repeats event 9, gives up after 25 minutes with event 60, and ends when the dialogue is free.
  The slavers' event-38 lines ("shout at slave worker": chance temporary 10, six lines) are in "slaver guard MAIN" and "Holy Nation Paladin". Meitou paces it: a worker shouts at most every 60 to 180 game seconds.
- **Capture** (167): the planner chain is 167 (effect isChained 134) <- 166 Restraining (`Task_CuffTarget`; needs 38, at location, 118 TARGET_IS_UNARMED, 7 target down) <- 181 Stripping weapons
  (`Task_StripWeapons`; effect 118). The chain needs 38 `hasOneWorkingArm` (either arm, **Verified**: rows 166 and 181) and nothing else of the arms; 167 has no B list. Only the later lift (rows 213, 240, 246, 68, 170: 80 `isCarryingArmOK`, the left arm) needs the left arm, so a one-armed capturer cuffs and then leaves the captive. **Strip** (**Observed**, `FUN_14034e7b0`): `medic` for 0.8 s, then the items of the target's `hip` and `back` sections (`FUN_140747ff0`) go to the
  stripper's inventory (else its storage), and the stripper's squad sets short-term tag 6 on the target (`FUN_1406744a0`; the NPC looting finder skips tag-6 targets, [carry-loot](carry-loot.md#33-npc-looting)). **Cuff** (`FUN_14034af20`): a "Restraining" bar
  with `medic`, progress += 0.1 x a per-update float of the character (`+0xc4`, read as frame time: about 10 s, **Unknown**), full at 0.99: the cuffer says event 37 RECAPTURED_A_SLAVE to the
  target (its lines "new slave capture" / "slave recapture", no effects) and posts message type 5 to the global list `DAT_1421346d8`. The shackles themselves are not put on anywhere in the task
  (**Unknown** where; Meitou puts the stock `SHACKLES` on). The list's only reader, `FUN_140a0af10` (via `DAT_1421346d0`), sums per-listener contributions around a point: no game logic
  was found for type 5, which `Task_GetSlave` (174) posts too. Carrying the cuffed one to a cage is goal 112 (not decoded); 168 lifts a downed slave (`Task_LiftSomething`, effect 115) before 112.
  The packages that list 167 and 168 are slaver, guard and workmaster packages ("addon- slave catching", "wandering slaver base", "slave worker day guard", the Rebirth and Holy Nation guards).
- **Looks like a slave**: processing (182) makes state 120 TARGET_LOOKS_LIKE_A_SLAVE true. The watchers' "needs processing" test `FUN_1408ca3d0` (**Verified**): a character with no owner needs it; one with an owner who is not an ally (`FUN_1406b2630`; same faction counts), carried, dead or an animal (vfunc `0x248`) never does; one with an owner of an
  allied faction needs it unless its slave state is not 2 or 3 and vfunc `0x3e0` is true and `FUN_1405a6bb0(platoon, character)` holds. That function (**Observed**, outline): an animal is fine, NPC
  class 1 or 5 is not; otherwise the torso and leg slots (inventory sections 5 and 6) must be empty, shackles or items of the owner faction's `slave clothing` list, and the shackle slot (section 9) must hold a locked item.
  Meitou's reading (**engine choice**): wearing locked shackles or any of the owner's slave clothing; an owner with none always passes.
- **Cage memory**: `Task_PutInSomething` for a cage (`FUN_140358670`) makes the prisoner's squad remember the putter as captor (`FUN_140673a20(prisoner, putter, 5)`) and calls `FUN_14067aca0`, which
  adds the prisoner to the putter's, its allies' and its squad members' platoon lists (the "slave list"). [Squad memory](dialogue.md#squad-memory) has the other writers.

## Desire and commitment

**Observed** (TaskData constructor `FUN_14061d350`, registrations in `FUN_140319a60` parsed for all 253 rows, scorer `FUN_14050df10`, `AITaskSytem`
vfunc 5 `FUN_14050caf0`). TaskData fields: +0x108 desire (a member function, nearly always a virtual of the `AI` object: `mov rax,[rcx]; jmp [rax+slot*8]`);
+0x160 target finder (returns a factor, 0 when it found nothing; the desire is multiplied by it when the row has an effect, `FUN_14060fd80`);
+0x18 / +0x1c commitment minimum / random range in game hours; +0x20 commits; +0x34 crowd factor (default 1); +0x3c is read by the senses
(`FUN_1408529c0`, meaning **Unknown**).

- Score = desire x goal weight x crowd^n (n: **Verified** `FUN_140268f30`, over the blackboard's per-character entries (type, target hand, set from the goal when it starts, `FUN_14050caf0` -> `FUN_140269e90`): other characters whose entry has the goal's type, who are not the goal's target hand themselves, and, when the goal's TaskData row has *Needs target*, whose entry target equals the goal's target. Only types with a crowd factor other than 1 matter. Meitou: `Blackboard.CrowdOf`, targets per `AiSystem.CrowdTarget` (SELF = the member, LEADER, SQUAD_MISSION; SPECIFIC and HOME_GATE count as equal; 106/112/137/281 are not acted out). `FUN_140269010` / `FUN_1402690e0` are variants of the same count with other hand tests, **Unknown** callers); x1.25 for the current task. For a committing current task the
  score is at least 0.4 until the commitment ends and at most 0.05 after it, so the character moves on.
- Commitment: when a committing task becomes current, end = now + min + rand x range. A candidate that is not the current task has its target cleared
  before it is chosen, so a newly chosen goal gets a fresh target from its finder.
- Desires (`AI` vtable slots): 19 = 1, 39 = 0.1 (Idleness), 40 = 0.25 (Patrolling, Patrolling town), 41 = 0.5 (Stand at node, Shopkeeper, Static guard,
  general node), 48 = 0.01 + 0.4 x rand (Wandering town, Staying home, Sitting around, Static town guard, Shopping 119). 125 rows desire 1; 102 call a
  function of their own (sleeping, following, retreating, turrets, Shopping 118: 1 until the shopping list is done, see its entry below).

| Type | Commitment (h) | Crowd |
|---|---|---|
| 37 Wandering town, 118 Shopping | 0.25 + r x 1.5 | 1 |
| 43 Staying home, 255 Reigning, 275 Sitting around | 0.2 + r x 0.8 | 1 |
| 38, 268 Static guard | 0.1 + r x 0.5 | 1 |
| 157 Static town guard | 0.1 + r x 1 | 1 |
| 47 general node | 0.03 + r x 0.1 | 1 |
| 153, 239 Going to bed | 2 + r x 8 | 1 |
| 190 Worker, 221 Operating machine | 0.5 + r x 2 | 1 |
| 240 Capture Target | 0.05 + r x 0.5 | 1 |
| 248 Attacking Squad | 0.3 + r x 2 | 1 |
| 45 Bodyguard | - | 0.9 |
| 57, 58, 125, 237, 250 | - | 0.8 |

## Planner

**Observed** (class `TaskPathfinder`: search `FUN_14050f800`, entry `FUN_14050fe10`, pick `FUN_140510650`, node expansion `FUN_14051bae0`, provider maps
`FUN_14060fe30`, "can run" `FUN_14060f940`). Meitou: [simulation](../simulation.md#planner).

- **Row fields** (builder `FUN_140319a60`, its disassembly read per registration; the two bools **Verified** against a second parse of the same
  calls). *Acts* (+0x134): the registration's first bool, cleared when an effect is set; rows with neither acts nor an effect: 104, 91, 89, 202, 14,
  59, 192, 17, 45, 187, 169, 42, 39, 203, 254. *Needs target* (+0x48), the second bool: 1, 29, 67, 82, 163, 121, 123, 290, 272, 87, 221, 152, 124,
  114, 242, 92, 247, 284, 115, 93, 94, 113, 91, 88, 72, 73, 77, 78, 140, 79, 162, 198, 80, 287, 200, 76, 201, 202, 185, 285, 186, 286, 81, 226, 95,
  213, 240, 225, 246, 68, 170, 2, 96, 71, 3, 25, 249, 60, 236, 244, 26, 241, 4, 262, 263, 266, 228, 227, 13, 146, 235, 101, 248, 165, 103, 129,
  159, 161, 12, 102, 164, 17, 45, 44, 253, 40, 31, 289, 166, 181, 182, 174, 30, 51, 119, 55, 5, 61, 97, 98, 258, 99, 106, 100, 134, 231, 220, 196,
  136, 137, 108, 112, 281, 110, 203. *Target finder* (+0x160): 122, 270, 189, 190, 152, 114, 242, 184, 115, 94, 113, 90, 84, 85, 195, 155, 197, 86,
  216, 125, 58, 250, 57, 237, 131, 259, 277, 279, 243, 8, 177, 212, 238, 19, 13, 34, 149, 222, 234, 101, 248, 188, 194, 218, 158, 283, 16, 41, 21,
  144, 32, 183, 260, 167, 173, 217, 245, 168, 15, 288, 143, 20, 38, 268, 157, 47, 37, 118, 214, 43, 255, 275, 24, 55, 153, 239, 105, 148, 106, 142,
  135, 139, 282, 230, 233, 232, 138, 137, 112, 281, 172, 211, 276, 204, 223, 273, 132. *Not a provider* (+0x40): 29, 186, 286, 226, 213, 240, 225, 246,
  263, 266, 228, 235, 44, 253, 5, 61, 97, 258. *Restricted* (+0x41): 122, 270, 140, 76, 202, 185, 285. *Keeps the queue* (+0x23): 87, 221. +0x22
  (28, 65, 147): **Unknown**.
- **Providers**: six registries, one per category flag bit (1, 2, 4, 0x10, 0x20, 0x40), each map a state and its polarity to the rows of that category,
  not +0x40, whose list A holds it, in ascending task type (filled from the registry's map keyed by type, `FUN_14060fe30`, **Observed**). Which registry a character plans with is its AI's repertoire (`FUN_14060e840`, **Verified**: the mode flags +0x200 pick +0x2a8, checked in the order 4 'imprisoned', 0x40 'crippled', 0x10 'chained', 1 'human', 2 'platoon', else 'animal'); cage entry (`FUN_140330600`) sets 0x10 when the cage's functionality `restrains movement`, else 4; `FUN_14060e8c0` clears the bits again (rows only flagged 0x20 are dog play and animal attacks). The posture setter `FUN_1405c7e20` (**Verified**) sets 0x40 for posture 1 (staying low) and 2 (crippled) and clears it for 0, 3 and 4. Meitou: `Repertoire.Of` (imprisoned 4, 0x40 from `MedicalState.StayingLow` or `Crawling`, chained 0x10, human 1, else animal 0x20; platoon unused, when 2 is set is **Unknown**); the planner (`PlanContext.Category`) plans with the same `Repertoire.Of`, so a crippled or staying-low NPC plans with 0x40 (no stand-up row) and an animal NPC with 0x20. Starts ask `TaskStart.Eligible(world, cold, row)` = `TaskDataTable.Of(row).Category & repertoire != 0`.
- **Can run** (memoised per row, target and effect target): the first B entry that does not hold is reported unmet, false; else true without an effect;
  with one, the effect target (from the finder when asked), false without one; true when the effect holds, else the effect is reported unmet.
- **Search**: a node is (row, target hand, position), with value g = h = the row's desire when made; a node whose desire is not above 0 is a dead end
  (a root one fails the plan). A root without a finder that can run is the plan by itself. Otherwise, while open nodes remain, take the one with the
  **highest** g + h, then accept it when it equals a task already queued or repeats one of its ancestors (up to 50; rows 1 and 67 never count), or
  when it has no finder, acts and can run. Else expand it: children for the effect (target: the effect target its finder found) and for every B
  entry that does not hold (target: its own), one child per provider; a restricted provider (`+0x41`) only when the search flag (`+0x20` of the task system) is set or the acting character's faction is not the player's (**Verified**, `FUN_14051bae0`: the AI's vtable slot 1 `FUN_140506cd0` gives the character's faction, vfunc `0x58` `FUN_140594640`, and its `+0x250` must be null; so every NPC plan may use 122, 270, 140, 76, 202, 185, 285, the player's characters only a flagged search). The flag is the plan call's last argument (`FUN_14050f800` stores it): `FUN_140510920`, the first step of `chooseGoal`, plans the goal from the order queue (`+0x40`) with it set (**Observed**: that step is the orders source); the other sources and `FUN_1405104d0` pass 0. A needs-target provider only when a target is known; a state without one gets a dead end
  (row 104 "null"). The children followed are those of the first unmet B entry in list order, else the effect's. A child not yet seen gets
  g = min(its g, the node's g) and the node as parent; one already closed is accepted from there. A node repeated 4 times is reused, not made again.
- **Result**: the accepted node's chain up to the root (stopping at a queued task) is the plan. Its value is the least desire on it (from 10); above 0,
  every node that acts or has an effect becomes a task (factory) in a new queue, leaf first. A failed plan clears the queue unless the root keeps it.
- **Pick**: candidates are tried by key, highest first (a taken key is raised by 0.0001 until free, so a tie goes to the later-listed goal; **Verified**, `FUN_14050df10`); the first whose plan has a value above 0 becomes current, a candidate whose plan fails has its
  target cleared and the next is tried.
- **Source order**: [Precedence](#precedence).

## Precedence

**Verified** (decompile: `AITaskSytem::chooseGoal` `FUN_140510ef0`, order step `checkOrders` `FUN_140510920`, job pass `FUN_14050e5b0`, tier pass `FUN_140510d60`,
update `FUN_140511130`, `Character::setupAI` `FUN_140621b70`; KenshiLib names **Observed**). How orders, jobs and goals take turns.

- **When**: the chooser runs on a replan request (`AITaskSytem` vfunc 3 `FUN_140506cf0` sets +0x1b0; the update `FUN_140511130` chooses, otherwise `FUN_14050d970`
  only steps the running plan): a plan finished, an order added, a stagger (`forceOrderInsert`), `underMeleeAttack` with the fight off unless the target gets up or is
  player-side and moving ([combat](combat.md#fight-lifecycle-and-orders)). No periodic replan was found (**Observed**). Between replans the plan step (`FUN_14050d970`, **Observed**) ends the current goal's plan
  (`FUN_14050d7d0`, so the chooser runs again) when the goal's list A holds (`FUN_140513f50`), when its hours are over, or when the goal's row can no longer run (`FUN_14060f940`); a further test on
  the queued task is not decoded (**Unknown**). It does not weigh the goal's desire again, so a goal's fight runs until it ends by itself (nothing left, target down, give-up) or its goal is done.
- **Sources**, in order; the first whose pick has a plan above 0 runs:
  1. The front of the order queue (+0x40 deque, count +0x60): first popped when done (list A holds) or its desire x finder is 0 or less; then planned with the search flag
     ([planner](#planner)). A player character's order that fails to plan shows its failure text (vfunc 0x2d8) and stays queued (**Observed**); the chooser goes on.
  2. Committed mode (the update's argument while a committing current goal's commitment runs; off without a current goal) with the running goal's priority (+0x20c) above 2: stop, the goal keeps its plan.
  3. The entries of a second list (+0x70, the one-shot order list, emptied with the queue by `FUN_1405074a0`) become candidates, scored by `FUN_14050df10`. **What fills it** (**Verified**, the writers of the add
     function `FUN_1405086d0`): the entries that are not perma-jobs, i.e. added without the permanent flag or whose TaskData row has kind +4 = 0, from the order giver `FUN_1405c8da0` (Character-side order
     calls, which first clear the queue and this list unless the order is queued) and the save loaders `FUN_140508e10` / `FUN_1405092d0`; job 187 is a perma-job and never in it. The deque +0x40 is filled by the order dispatchers through `FUN_140508380`; how a candidate leaves the +0x70 list is **Unknown**. With a job list, the **urgent job pass** adds the
     candidates of Obedient Slave 187 (slave state 1, not sneaking: the owner platoon's Slave goals, [Obedient Slave](#slave-work-shout-and-capture)) and the kind-2 jobs (turret guard 234) while the AI's sensed-enemy count +0xbc is non-zero, and picks.
  4. Priority-3 goals: the squad's (blackboard, `FUN_140269240` / `FUN_14026b790`) and the character's own (+0xa0 + 0x18 x priority), one pick.
  5. Committed with the running priority above 1: stop. Else **every job** in list order, then the implicit jobs ([Perma-jobs](#perma-jobs)).
  6. Priority-2 goals; committed with the running priority above 0: stop; priority-1 goals; the default task (+0x190, at key 1.0); else AI vfunc 0x98. The default task (**Verified**:
     setter `OrdersReceiver` vfunc 4 `FUN_14050c030`, a fresh 14 Idleness with no target when given none) is reset by every order (`FUN_1405d20d0`, the move order `FUN_1405d22b0`) and by
     standing orders 8 / 9, by the AI's manoeuvre mode (+0x2bc): `FUN_140599970` (a point: the order's) and `FUN_1405966a0` (a target) make it 30 Holding position for HOLD_GROUND (0),
     31 Staying close for STAY_CLOSE (1), and idleness for ROAM_FAR (2, the default). The Hold toggle (standing order 12) does not touch it.
- **Goals of a character** (`setupAI`): everyone 14 Idleness (priority 1, weight 0.1) and 32 Self preservation (priority 3, weight 1, target itself). A player-faction character
  (its faction's +0x250 set, `FUN_1407916d0`) also 243 Getting food (priority 3, weight 0.5) unless it has it; a player-faction animal also its race's `AI Goals` and 259 Picking
  up food (priority 3, 0.5), and loses 230. A character of another faction gets its race's `AI Goals`. Before that branch, any character whose platoon AI object (`FUN_140791ba0`: Character +0x658,
  +0x78, +0xf8) has +0x210 null gets its FACTION record's `AI Goals` (**Verified**, code; +0x210 read as the AI contract slot, null for an ordinary squad, **Observed**). The player faction
  (`204 Nameless`, the only base FACTION with the list) lists 15 Protect allies (AI_TASK 957, classification 3: priority 3, weight 1), so a player character holds goal 15. Hold adds 16 at priority 3; AI packages add theirs ([AI](../simulation.md#ai)). Priorities
  (`taskPriority`): 1 fluff, 2 non-urgent, 3 urgent, 4 obedience; orders are queued at 3.
- **So, for a player character**: an order beats everything, Self preservation included (an ordered medic keeps treating while hit; each landed blow staggers it and the order
  is planned again, [combat](combat.md#fight-lifecycle-and-orders)). Self preservation (while an attacker is within 181.7), Getting food and Hold's attack on sight beat every
  job but the urgent ones (a turret's row refuses a threat within 40 anyway). An order or job can still fight by its own rows: 31 Staying close needs `!16` and 45
  Bodyguard `!16, !39` (16 / 39: the followed character's / its own +0x2b0, a threat within 40, `FUN_14059de10`), whose provider is 13 Attacking attackers of, so a
  follower fights whoever threatens its leader and a bodyguard also its own attacker (**Observed**, from the row lists and the planner). A job higher in the list takes
  over at the next choice; the implicit jobs come after the list, Idleness last. Passive leaves
  Self preservation alone; the Jobs switch only gates the job passes. A fight is a task (4 `Task_MeleeAttack`) in a goal's or an order's plan; `CombatClassAI` steers it and
  chooses nothing; the fight ends with the task.
- **Threat tests of order rows** (**Verified** against the transcribed registry `TaskDataTable`, list B read row by row): of the player's order rows only 146 Turret gunner tests a
  threat (`!158`, its own +0x2b0; no row provides it, so the plan fails while a melee attacker is within 40 and the order waits in the queue). 225 / 246 lift, 2 Building, 95 Repairing,
  96 Demolishing, 87 Operating, 97 Training, 124 Hauling, 150 Prospecting, 25 / 249 First aid / Splinting, 26 Looting, 69 Putting down, 98 Sleeping, 76 / 201 Lockpicking, 108, 110,
  107, 5 / 61 attacks and 29 Move order list none of 158, 159, 16, 39; Rest (54) has no TaskData row. 31 needs `!16` and 45 `!16 !39`, which 13 provides, so they fight instead of failing.
  The providers a plan adds before these rows were not checked for threat states (**Unknown**).
- **Meitou**: the player's characters are decided by one step in this order ([decision step](../simulation.md#decision-step)): source 1 the order queue
  ([simulation](../simulation.md#order-queue); one that cannot run stays queued with its text and the step goes on; a turret gunner's 146 ends under a melee threat and is taken up again
  once it is gone), step 2 has no source (no player goal Meitou runs commits), step 3 the urgent jobs (234 while an enemy is sensed; 187 Obedient Slave of an enslaved player character: its master squad's Slave goals 171, 189 / 190, 14, [Obedient Slave](#slave-work-shout-and-capture)), step 4
  picks among 32 Self preservation, 243 Getting food, a player animal's priority-3 race goals (277-279), 259 Picking up food, Hold's 16 and 15 Protect allies (joining a mate's or
  an ally's fight), step 5 every job in list order through its system's contributor (the switch and the repertoire first), then the implicit jobs, step 6 the priority-2 race goal 233 and
  14 Idleness (a player character's routine: it stands; the default task is never reached, as only ROAM_FAR is modelled). A task that ends leaves its character idle for the step to
  re-decide (the original re-plans; nothing resumes). Followers and bodyguards fight by their rows: an attacker of the followed character within 40 (16), a bodyguard also its own
  within 40 (39), as a step of the row, and follow again when the fight ends. The step runs on the replan triggers above plus an engine re-ask every `JobCheckTicks` of a character
  idle or on a job's task (no periodic replan was found), so a higher job takes over a running one within that period, where the original waits for its next replan. Goal 15 is
  `RetaliationSystem.JoinFight` at step 4 (below).
- **15 Protect allies for a player character** (**Verified**: desire `FUN_140599b90`, finder `FUN_14099ca10`, falloff `FUN_1409b2380`, constants from the exe): desire 0 when Passive or
  sneaking outside a fight; else the finder's best sensed ally (seen or heard, tagged ally) by its fight threat (`FUN_14099a190`, [combat goals](#combat-goals)) x clamp((1000 - d^2) / 990, 0.1, 1)
  (d the distance to that ally; the argument of `FUN_1409b2380` is the squared distance, **Verified**: 1 within 3.2 units, 0.1 from 30; no other distance limit), an ally of another
  faction only with Help Allies and then squared; x0.5 while carrying (`DAT_14167e8d8`). Its plan is 13 Attacking attackers of (attackers within 150 of the ally). Being above 0, it beats every job (step 5)
  whenever 13 finds an attacker, and loses to 32 Self preservation (1 or 0.7) when the character is attacked itself.
  **Meitou** (`RetaliationSystem.JoinFight`, `BestAlly`, `Plan`): the aggro system's senses stand in for the sensed map (entries tagged ALLY, not KO or imprisoned, seen or heard at the last look), the fight
  threat is `CombatSystem.FightThreat` (so an attacked ally that fights nobody counts), Passive gives the desire 0 in a fight too, the slave gate is asked once for the first slave met (the finder latches it), the best ally is the first in slot order on a tie,
  the result is capped at 1.2, the plan is `CombatSystem.BestAttackerOf` for the ally, and a character on Hold also needs `Orders.HoldPost`. It cannot start while the character fights on another plan or
  cannot fight; while its own fight runs with no ally left its desire stays 0.7 (engine, the fight task ends by itself). The step reaches it at the engine re-ask of an idle or job-task character
  (within `JobCheckTicks` of a mate starting to fight) or at any replan; an order or a goal's task keeps it out. **Observed** (offscreen sandbox): a player character in a squad of its own 45 to 55 units away joins
  an ally's fight when nothing else wants it (run with the threat taken as 1). The second zero of `FUN_140599b90` is Passive (the byte `+0x12c` of the character's `+0x450` object, standing order 13; **Observed** that it is the same object as Hold's `+0x12b`). The old engine join (squad mates within 250 at a fixed 0.6) is gone.
- **The medic jobs' own threat tests** (**Verified** against `TaskDataTable`, lists read row by row): 58 Medic and 250 Splint Rigging have no list B; their treatment rows 25 First aid and
  249 Splinting need `38 23 !140 42 46 !157` / `38 151 !140 42 46 !157` (an arm, a kit, the target not swimming, standing still, at the target, weapon away): no threat state. Only 57
  Robotics needs `!39` (its own melee threat within 40), which 13 provides, so a robotics job under a threat within 40 plans a fight, as 45 does. The desire `FUN_140998410` drops to
  0.01 / 0.05 near enemies but stays above 0. So a medic job yields mid-fight only to step 4: Self preservation against its own attacker, 15 for a fighting ally.
- **A blow aimed at another** (a technique's extra hits in its zone) puts the hitter on no threats list: Self preservation's desire (`FUN_140599d20`) reads the senses' threats
  (AI +0x80 / +0x88 = SensoryData +0x58 / +0x60: characters whose intended target is the looker, positive aggression, within 181.7) and the +0x2b0 flag built from them; the hit's
  `underMeleeAttack` (`FUN_140999f00`) only replans (**Observed**). Such a medic fights only through 15.

## Combat goals

**Verified** (registrations in `FUN_140319a60`, finder/desire stubs resolved by disassembly); readings of the finders' tests **Observed**.

- 16 ATTACK_ENEMIES: effect state 6 `isNoEnemiesInVicinity` (registered without an evaluator). Finder `FUN_14059f3a0` walks the
  looker's sensed characters tagged SENSE_ENEMY and not KO, ally or 0x300 (mask 4, 0x308; [sense tags](perception.md#sense-tags-who-is-an-enemy))
  that are seen or heard (entry +0x38 / +0x39), drops those whose faction the looker's own table coexists with (`FUN_1406b23c0`, **Verified**: one way, the looker's list only; Meitou `AggroSystem.AttacksOnSight`) and keeps those passing `FUN_140598630` and `FUN_140599f30` (the post
  and area tests, [perception](perception.md#the-attack-on-sight-tests)); `FUN_14059e300` picks the target. Before those tests an entry is skipped when its posture is not 0
  (vfunc 0x368; staying low, crawling) and it stands in a building (**Verified**, finder body; Meitou `MedicalState.Prone` and `SightMap.BuildingOf`), the posture test being replaced for a chained slave
  of the looker's faction (`FUN_14032e2a0`: chain flag `+0x320`, owner faction the looker's) by its slave state (`FUN_1405c8aa0`): skipped while 1, a target once 2 (escaped); also needed: global `DAT_142133f54` set or the
  target's campaign force null or its `+0xc0` non-zero (the field is **Unknown**, not modelled), and the platoon-side object's vfunc `0xb0` null (**Unknown**, not modelled). Factor 1 with a target, else 0. This is the attack on sight.
- 144 TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME ("Home territory aggression", finder `FUN_14059fd70`, **Verified** stub): SENSE_ENEMY
  entries (mask 4, 0x208) within 1.3 x the home town's radius (`FUN_140926d50`, factor `DAT_14169bb40` = 1.3, read-only; ground-plane squared distance, strict; a
  NEST_MARKER home contains nobody), or outside it in the looker's own building (**Observed**: the enemy's and the looker's building hands match, the looker's not null; the
  looker indoors), skipping a slave whose owner's faction is an ally (a slave of a neutral or enemy owner is a target); with no home town it is
  `FUN_14059fa70` (the same mask and tests, no circle). Meitou: the squad's town (`Blackboard.Town`), else its nest (a NEST town, 1250 x mult, round the nest centre; a save's wild squads have none), and `TownData.Radius`. 21 ATTACK_ENEMIES_AND_NEUTRALS
  (`FUN_14059f720`) takes mask 6 (neutral or enemy, 0x308) and drops a faction the looker's coexists with (one way, neutrals too); `FUN_1408569a0`
  then drops, for an NPC looker not hostile to the target's faction and without TEMP_ENEMY on it, a campaign force's member (and a target whose squad
  record +0xc0 is 0, meaning **Unknown**, not modelled).
- 143 PROTECT_OWN_SQUAD ("Protect close allies"): effect `!isAggressionTowards` (state 16, evaluator `FUN_14059de10`: the target
  resolves to a character whose AI-side object is set and byte +0x2b0 is set, meaning **Unknown**). Finder `FUN_14099d510` scores
  each squad mate (the platoon's active members, the looker included) that passes `FUN_140855960(sense, mate, 1)` (**Verified**: the looker itself; else a mate in its own sensed map that is seen or
  heard now; a mate missing from the map falls to a hand comparison, **Unknown**; a far mate never counts) by its fight threat `FUN_14099a190` (below), the best capped at 1.2
  (`DAT_1416852f0`); with none above 0 it falls back to a hand list at +0x2f8 at 0.1 x the threat. The desire is that value (slot 19 = 1 x the finder): a weak pull for one attacker, a strong
  one for a gang. Achieved by 13 "Attacking attackers of" (list A `!16, !39, !12*`).
- The fight threat `FUN_14099a190` (**Verified**, disassembly; constants read from the exe), on the character's `CombatClassAI`: 0 when it is down (vfunc 0x30); 0.01 when exactly one attacker is
  on its engaged list (askers, at most 5) and that attacker's own list plus its refused-askers counter is above 1 (someone else fights it); else 0.25 n - 0.2 for n listed attackers (0 for none:
  1 gives 0.05, 2 0.30, 5 1.05), raised to 0.1 x its senses' threat sum towards it (AI +0xb0); then 0.01 when it is in a fight task and the value is 0 or less. So an ally counts while it is
  attacked, threatened or fighting, not merely while it "holds a target". Readers: 143's and 15's finders, row state 12 `isAlliesFighting` and `FUN_14099a3e0` (a platoon member above 0).
  Meitou (`CombatSystem.FightThreat`): n is every standing fighter whose target it is (not the engaged list), "someone else fights it" is that attacker's own count above 1, and the threat sum
  adds 1 per threat (the original adds the threats' aggression).
- 15 / 288 PROTECT_ALLIES ("Protect allies", "protect allies - dont leave town"; finder `FUN_14099ca10` at +0x160 for both, **Verified** registration):
  the same shape as 143 over the sensed entries tagged SENSE_ALLY (mask 1, 0x208; same faction or relation >= 50) that are seen or heard, scored by the fight threat
  `FUN_14099a190` (no distance factor for an NPC looker; a player-faction one: [Precedence](#precedence)), capped at 1.2. 288 is registered exactly as 15 (**Verified**: the two registration blocks
  differ only in the task id; the same finder, desire and flags; no code compares 288): "dont leave town" is only the mod record's name. The same-town variant `FUN_14099cf80` is never registered
  (**Verified**, no reference in the exe). Desire `FUN_140599b90` (**Verified**): 0 while sneaking and not in combat mode, while Passive (in a fight too) and for an enslaved character holding job
  187; else the finder's value, x0.5 while carrying. A slave or escaped slave (status int 1 or 2) is defended only when the looker passes `FUN_140997e80`
  (**Verified**, latched once a pass): its faction is a bandit one (fundamental type 8), or it is in no town, or that town's owner books no bounties (Meitou:
  `RetaliationSystem.SlaveHelpAllowed`). An ally of another faction counts for an NPC looker; only a player-faction looker
  needs the global +0x111 and squares the weight ([Help Allies](#squad-ai-options)). Achieved by 13 "Attacking attackers of"
  (`FUN_14099c3b0`, **Observed**): the protected one's attackers that it or the looker senses, within 150 of it (outdoors), are taken when either regards
  the other an enemy (slot 125) or the attacker's AI +0x2a4 equals the goal target's id (meaning **Unknown**, read as "it is attacking the protected one"),
  and never one the looker regards an ally (slot 126). On that reading relations do not matter (a neutral attacking an ally is fought), which agrees with
  the owner's report that the original's guards fight skimmers. Candidates (`FUN_1405cb150`): the subject's attackers plus its own target while it fights;
  only when that list is empty its threats. All passing ones go to `chooseBestAttackTarget` (`FUN_14059e300`, the target score): the best is taken
  (**Verified**). Meitou: `CombatSystem.BestAttackerOf` picks each joiner's attacker (the sense tests and the threats fallback left out); a player character's own 15 is in [Precedence](#precedence).
  Once in the fight, the joiner re-targets by the fight's own lists and then any sensed enemy (the aggro mode that would stop that is 0 for every character, **Verified**: no code writes it), so
  no leash holds it near the one it protected. Gate guards (`Gate guard -no turrets`
  and those inheriting it) list 15, `Gate guard stay in town -no turrets` 288 (**Verified**, package dump).
- 34 ATTACK_TROUBLE_MAKERS ("Law enforcer"; row 34 "Attacking trouble makers", flags 0x21, list A `!29`, effect 7; **Verified** registration in `FUN_140319a60`: desire thunk
  `0x1400215ee` = AI slot 19, 1; finder at +0x160 thunk `0x140052fe0` → `FUN_14099baa0`). Finder (**Verified** structure, field meanings **Observed**): factor 0 while the looker's
  current town (`getCurrentTownLocation`, vfunc 0x78) is null. Else two passes over sensed entries, neither testing seen or heard (**Verified** iterator masks: pass 1 SENSE_ENEMY, pass 2
  SENSE_NEUTRAL, both excluding SENSE_KO), each candidate in the looker's current town and passing the area test `FUN_140599f30`: (1) one whose status object (Character `+0x1a0`, vfunc 0x70) has `+0xac` above 0 or that the looker's squad memory
  tags AGGRESSOR (short tag 2, `FUN_140678960` → `FUN_140678830`); (2) one whose NPC class (status `+0xdc`) is not 1 law enforcement, 3 trader or 5 diplomat (class 2 military
  only when its squad's home town (`FUN_1407956a0` +0x30, **Observed**) is not the looker's town and its faction is not in that town's resident factions (`+0xf0`; whether the owner is in it is **Unknown**)), passing `FUN_14099b820`: not class 7; `+0xac` above 0, AGGRESSOR, or `+0xa0` above 0 with class 8 or an animal; and attacking
  (`FUN_1405ca6f0` → `FUN_1405c92d0`: its status `+0x68` target hand is that character and it is in combat mode) a victim V that the looker regards as an ally (vfunc 0x3f0) or an
  innocent (no bounty with the looker's faction, not class 7 or 8, V `+0x148` below 1, and the attacker's faction is a bandit one (`+0x34` 8) or has `+0x1d0` set, or V does not regard
  the looker an enemy, is no animal and not class 6 or 8). The best by `chooseBestAttackTarget` `FUN_14059e300`; factor 1. **Verified** (base data, the four official files): the
  `Shop` and `Shop-24hr` packages list 34 for the Squad and Squad2 roles (0-24 h, weight 1), next to 143, so every vanilla shop guard has it and the shopkeeper (Leader) has 143 only.
  Meitou (`AggroSystem.FindTroubleMaker`): both passes over the looker's sense entries (not KO, seen or heard or not) in its current town (`AiSystem.TownAt`); "intends aggression" is the TaskData
  intended aggression (+0x24, below) of the candidate's current goal (in a fight its fight row, else its AI goal, else its running row; `TaskAggression.OfGoal`) and +0xa0 that of its running
  row; the class-2 resident-factions test stands on the town's owner faction (**Unknown**); the victim test of pass 2 takes the looker's own or allied faction only (the innocent branch
  is left out); the area test is not modelled.
- TaskData +0x24, the intended aggression (**Verified**, registrations in `FUN_140319a60`; the AI's state broadcast `FUN_14050ed60` copies the current goal's to status +0xac, `getIntendedAggression`,
  and the running task's to +0xa0): 1 for 4 / 227 Attacking, 262 / 263 / 266 Shooting, 228 Taking out, 61 Attacking target unprovoked, 101 Assault, 248 Attacking Squad, 16, 21, 144, 41 Fighting for
  master, 146 / 235 Turret gunner, 131 / 244 food grabbing; 0.5 for 13 Attacking attackers of; 0 for 34, 15, 143, 288, 32 and most rows; negative for the lockpicking, medic, repair, robotics and
  engineer rows, 7 Sheathing and 35 Running away (-0.5; the others are not transcribed). The AGGRESSOR tag (short-term tag 2) is written only by the dialogue effects DA 7 GO_HOME and DA 34
  RUN_AWAY on their target and lasts 7 h (**Verified**, `FUN_140680560`), so a plain fight never sets it.
- 101 DEFEAT_SQUAD ("Assault on target squad", `targeting` 3 = the squad's mission target; row `Assault`: list B `!47`, effect 7; desire AI slot 19 = 1 times the finder's
  factor; carried by the packages `Assault target squad` and `(agg)`, signal 8, and by dialogue `AI contract`s). Finder `FUN_140793eb0` (**Verified**): the goal's target
  hand must be a character (type 1 or 0x22); of its squad (+0x1d8, members +0x58/+0x60) it keeps the members that
  (a) the AI knows, `FUN_140855960(AI+0x28, member, flag)`: the member is the AI's own character, or has an entry in the AI's sensory map (any tag; with the flag set only an
  entry seen or heard now; the flag is not passed here, so the entry's freshness is **Unknown**), else the branch that compares the member's hand with a hand
  stored in an object at the character's +0x280, whose meaning is **Unknown**;
  (b) pass the area test `FUN_140599f30` (**Observed**, [perception](perception.md#the-attack-on-sight-tests): in the home town, within ROAM_FAR's 6517 of the character, or fighting it);
  (c) are not imprisoned (`+0x2f8` != 2, [carry-loot](carry-loot.md)); (d) are standing, character vfunc `0x368` == 0 (not crawling, not ragdolled, so not knocked out or dead).
  The kept ones go to `chooseBestAttackTarget` `FUN_14059e300` (the [target score](combat.md), which also drops ragdolls, allies by faction `FUN_1406b2630` unless slaves,
  the leave-alone cases and members it found no path to): the **best by score**, not the nearest, becomes the target hand. Factor `DAT_14167c308` = 1.0f
  (**Verified**, `data_values.txt`) with a pick, else 0. A pure 0/1 gate; the plan from there (a provider of `isTargetDown`, such as 4 Attacking) is the planner's.
  Meitou: `AiBehaviour.Assault`, `AiSystem.AssaultTargetFor`. Goal target = the contract's mission target (`StartContract`, from dialogue's `AI contract`); no mission target,
  or a goal not aimed at it, gives 0. Known = an entry of the looker's senses (`CharacterFacts.Knows`) or the mission target itself. The plan is the goal
  alone (engine choice: the walk and the weapon are the combat system's); the serial step sends a non-defensive `AttackOrder` to the picked member, the character alone
  (as dialogue's attack effects: its mates join through their own stance), and when the fight ends the next think picks the next standing member. Not modelled: the
  `+0x280` branch, the leave-alone cases, entry age. 248 "Attacking Squad" (same finder shape) is not built.
- 32 SELF_PRESERVATION and 35 RUN_AWAY (**Verified** unless marked: `FUN_140319a60` registrations, stub bytes read from the exe, `Character::setupAI` `FUN_140621b70`). `setupAI` gives **every**
  character goal 14 (weight 0.1) and goal 32 (weight 1, target itself, tier 3); the race `AI Goals` (30 races list 32) may add it again. Row 32: flags 0x65, list A `!159`, no B, effect `!39`;
  desire AI slot 17 (`FUN_140599d20`, [combat](combat.md#fight-lifecycle-and-orders): 1 with a threat within 40, 0.7 with an attacking threat within 181.7, else 0); its finder stub jumps to `FUN_1405985e0`,
  which copies the character's own hand and returns 1.0. The goal row does not act, so the planner goes down to a provider of `!39`: only 13 (desire 1, finder `AI::findAttackerOf`, leads to 4 / 262 / 266) and
  35 (flags 0x61, A `!39, 6`, B `60, !26`, desire slot 55 `FUN_1405a34e0`: 1.0 for a crippled character, else 0.001; 0 for a player-faction one with an arm). Best-first by g + h
  makes 13's chain win unless its leaf is worth less than 35 (**Observed**, [planner](#planner)):
  - **Crippled** (Character vslot `0x360` = `isCrippled`, [the flag](combat.md#crippled-flag)): 35 is worth 1, 4 only 0.1 under a threat within 40 (`FUN_1405a4260`; 0.0001 on Block or Hold, 0 for a non-unique
    NPC with nothing close). 35's `!26` holds although the character lies down (state 26 `FUN_140599e90` answers false for a crippled character) and the crippled repertoire (0x40, set with posture 1 or 2
    by `FUN_1405c7e20`) has no stand-up row (28 and 65 are 0x25). So a crippled NPC under threat **crawls away**; it has no attack on sight either (goal 16's desire is 0 unless standing).
  - **No working arm** (medical `+0x165` and `+0x166` both false): 4's desire is 0 and shooting needs an arm, so 13 is a dead end and 35 runs at 0.001.
  - Anyone else fights: 4 is worth 1 (0.05 at the least with the ranged and squad factors), 35 0.001.
  - 35's entries: 60 `LOCATION_IS_ACCESSIBLE` (`FUN_1405a2710`, 0 behind a locked gate in town walls, [combat](combat.md#fight-lifecycle-and-orders)) and `!26` (a character staying low must stand first).
  State evaluators: 39 `isAggressionTowardsMe` = Character `+0x2b0` (`iAmBeingMeleeAttacked immediate`, `FUN_14059de10`); 159 = the senses' threat count `+0x58` non-zero (`FUN_140436720`); 6 `isNoEnemiesInVicinity` =
  AI `+0xbc` == 0 (`FUN_140597630`; SensoryData `numEnemies`, **Verified**: [senses](perception.md#senses)).
  `Task_Runaway` (35, 280): start `FUN_14034b440` (first run: from the mean position of the sensed threats with positive aggression, by 2000 + 400 r, mover speed field `+0xbc` set to 999); update `FUN_140336820`
  (when the mover stops or is within 200 of its destination: a hop of 1000 + 400 r from where it stands, x and z uniform in [-1, 1] normalised). It has no end of its own: the planner drops it when its goal does
  (under 32, when the threat level falls to 0; as a goal of its own, when its list A `!39, 6` holds, **Observed**). 35 is also a goal of the package `Leader flees while guards fight` (Leader: `Go Home (Run away)`,
  `Run away!`) and of the Slave lists of four police-station packages (0.001 for an NPC).
  Meitou: `SelfPreservation` (the two desires, the choice), `CombatSystem.ThreatLevel` (slot 17), goal 32 as `AiSystem.SelfPreservationGoal` (once per NPC mind, the list entries ignored), a priority-3
  goal of the chooser like the combat goals: its plan is `[35, 32]` when `SelfPreservation.Runs` (a crippled NPC, or one with no working arm), else `[13, 32]` when row 13 finds an attacker of its own
  (`AiSystem.SelfDefencePick`: `CombatSystem.BestAttackerOf` with the NPC as subject and looker, its attackers and its own target at any distance; a turret gunner takes no shooter, engine), else none
  and the next goal is tried. The fight starts as a goal fight (`SendFights`, a defence, not in the aggro log) and is the AI's own: kept while 32 is chosen (a threat on its list beyond 181.7 keeps it at
  0.7, standing for list A `!159`, engine), left for a higher goal. 35 is acted out by
  `FleeSystem` (the squads' run: first run from the threats' centre, hops, `CharacterCold.Fleeing`): the AI asks (`AiMind.FleeRequest`), a fighter is stopped first. A lone runner stops when no threat attacks it
  within 181.7 or its body takes the choice back to fighting; a goal-35 runner (and a broken squad's, with the optional `FleeSystem.SquadBreak`, which the original has no equivalent of: [perception](perception.md#retreat-and-memory)) stops when no enemy is in sight and nobody attacks it. Not modelled: the player faction's branch of slot 55 (the
  player's characters never run), the locked-gate zero of 60, 4's ranged and squad factors, standing up first for a character staying low. An NPC without a mind (no squad: scripted and test worlds) gets
  an engine answer instead (`RetaliationSystem.SelfDefends`, **engine choice**): it fights its attacker at any distance at the alert and every 15 ticks; a crawling one neither fights back nor runs.
  - **Timing**: the original replans an attacked character whose fight is off (`underMeleeAttack` `FUN_140999f00`, the shot handler `FUN_140999d50`) and chooses at its next AI update
    (`FUN_1405112b0`, **Verified**: its sense pass `FUN_14085a8d0` runs first, so the threats list is current, then the task system's update `FUN_140511130`), 8 characters a frame in turn
    ([game loop](game-loop.md#the-character-update-budget)). Meitou: the alert (tick T) puts the attacker on the threats list at once (`AggroSystem.Force`) and asks the NPC to think; it thinks
    at T+1 and its fight starts at T+2. A landed blow or bolt with its fight off asks again the same way (`CombatSystem.HitReplan`), so a hit never waits for the next 15-tick
    think.
  - **Shot from afar**: 32's desire is 0 for a threat beyond 181.7 (**Verified**, `FUN_140599d20`), and the shot handler (`FUN_140999d50`, **Verified**: forced sense with the alarm flag,
    sneaking off, TEMP_ENEMY on the shooter, the shooter on the victim's engaged list `FUN_140666e20`, a replan while its fight is off) starts no fight of its own: no "being shot at" state or row
    was found (**Observed**: row 39 is the melee `+0x2b0`, 158 / 159 the same flag and the threats count). So only another goal answers a far shooter: 143 (the finder counts the looker itself,
    whose fight threat is 0.05 with the shooter on its engaged list, and row 13 takes its own attacker at any distance; **Observed**), 16 / 21 / 144 through the TEMP_ENEMY tag when it is sensed,
    or 32 once the shooter is within 181.7. An NPC with none of them stands under fire (**Observed**, decompile reading). Meitou: the same (a shooter's target counts it as an attacker for 143).
- So a squad with PROTECT goals and no ATTACK goal starts no fight with an idle enemy (**Observed**): e.g. the base `Fog prince squad`
  (`42029-rebirth.mod`, package `fog prince`); its dialogue package `fogman` answers only events 52 and 53 and has no effects.
- **Meitou, NPCs** (`AiSystem.Fights.cs`, [AI](../simulation.md#ai)): 16, 21, 144, 34, 143, 15 and 288 are priority-3 goals of `AiSystem.Choose`, scored like every goal (desire x finder
  factor x weight x crowding, x1.25 for the current one; in their hours). Desires: 16 1 (0 Passive, sneaking outside a fight, prone), 21 / 34 / 143 1, 144 0.5, 15 / 288 as above; the factor is 1
  with a pick for the attack goals (`AggroSystem.FindFoe`: the looker's sensed entries and its squad mates' within 2 R, the shared sightings) and the capped fight threat for the protect
  goals, whose pick is row 13's attacker (none: the goal fails). Engine gates: a character that cannot fight, crawls, runs away under Self preservation or mans a turret wants none. One whose
  lists hold no combat goal starts no fight and joins none: it only fights back when attacked (32 in the same chooser). Nothing else starts an NPC's fight on sight or pulls a mate in.
  - **Squads the AI does not run** (`AiSystem.EngineMinds`, `AiMind.Engine`; engine choice for the routine): `setupAI` gives every character goals (14, 32, its race's, its faction's; [Precedence](#precedence))
    and its squad's packages add theirs, so every NPC of such a squad gets a mind: a roaming platoon's active half (its trip is the population's stand-in), a campaign's loaded squad and a squad
    whose template lists no AI packages. Its combat goals are those of its package for its role (a campaign squad's phase package, as `PopulationSystem.GoalDuty` reads it; a roaming one's
    the package its stand-in follows; none without packages) and of its race; it weighs only them and Self preservation (fight or run), on its routine (standing, wandering, following, the roaming
    leader's walk) and in its own fight, and acts nothing else out; a fight's end gives the routine back (the stand-down). So a template without packages starts no fight (no base race lists an
    attack goal; Cannibal lists 143) and a caravan with 143 / 15 only defends. Not modelled for them: 35 and the packages' other goals (the trip goes on as before). A mind dropped by trade,
    recruitment, capture or storage is made again at the next package cadence (30 ticks); meanwhile, and for an NPC with no squad (scripted worlds only), there is no mind.
  - **In the fight** (`AiSystem.InFight`): the fight a goal started is the AI's own (`AiMind.FightFrom`), and its fighter thinks in it as anyone does. The current combat goal is kept while it is not
    done (the plan step above): an attack goal while its senses count an enemy (`numEnemies`, 16's effect; the same taken for 21, 144 and 34) or its finder picks one, a protect goal while
    its finder values someone above 0 (143 counts its own fight, the 0.01 floor), row 13 not asked again; another combat goal chosen keeps the fight too (the fight re-targets by itself).
    No fight goal commits, so a goal of a higher score takes the fighter out: the serial step ends the fight (`AiSystem.LeaveFights`) and the next tick acts the goal out. Engine choice: a
    fighter also thinks on the 15-tick cadence, where the original only replans on triggers and lets the plan step end the goal.
  - **Pre-emption** (`AiSystem.Preempts`): the engine duties stand for goals and jobs of the same chooser below a priority-3 goal, so the combat goals and 32 are weighed for an NPC on one whose
    system lets a fight interrupt it: treating (`FirstAidSystem` lets the patient go), a town errand's walk (`TownDutySystem` waits for the fight and walks on; one carrying an intruder out
    fights with the body on its shoulder, x0.5 for 15 / 288), a captive job's walk carrying nothing (`CaptiveSystem` gives the job up). The other duties (their systems do not survive a fight
    or are law work of their own: arrests, kidnaps, the cage walks, rescues, lifts, looting, dialogue walks, trades) and a fight the AI did not start (a dialogue's, an assault's, a duty's) get
    32 alone: their characters start no combat goal's fight until they are back on an AI task, but defend themselves (a fight's start ends the duty's task; in a fight only 32's run changes
    anything). Nothing for one fleeing, imprisoned, carried or down, nor for a gunner or a carrier in a fight.
  - **Same-tick starts** (`AiSystem.SendFights`): the original's choosers run one after another and a fight's start puts the fighter on its target's engaged list at once, which the next
    chooser's target score counts (`FUN_14059e300`: minus manliness per attacker; **Observed**). Meitou's thinks read the last tick, so the serial step picks again, in slot order, for a starter
    after the first of the tick, with the fights already started counted as attackers: mates of one sighting spread over the foes.
  - **Triggers** (`AiMind.Replan`, engine stand-ins, the same whatever the goals): a new enemy entry in the senses of an NPC that is free or in its own fight (it and its mates within 2 R), an
    alert (the attacked NPC with its fight off and not getting up, as the original's replan; both squads' mates), a mate's fight start, and the end of the NPC's own fight (the plan finished),
    next to the 15-tick cadence (so an NPC with 15 / 288 that senses an ally's fight weighs it at its next think).

## Town goals

**Observed** (target finders named; TaskData effects lead the planner to the task that acts them out):

- **37 Wandering town** (`FUN_140999a60`): keeps a target that is still a general node; else one building drawn uniformly from the **public**
  buildings of the character's town in the zone it stands in (`FUN_1409fc6c0` over `FUN_1409fbb00`: the zone's building list, Building vfunc 82, which
  returns +0xc8, the `public day` flag, see [locks](locks.md)), then a random node of type 1 (NODE_GENERAL) in it whose owner is unset or the
  character's faction (`FUN_14038c2f0`); none (no public building, or the drawn one has no such node): 0. Effect `atNode`, acted out by 51 Stand at
  node. Vfunc 82 is false for some building kinds (1, 0x22) under a player-side condition (**Unknown**); the flag's default on buildings without a
  resident template is **Unknown**.
- **47 general node / 20 Shopkeeper** (`FUN_14099f280` / `FUN_14099e920`): a node of that type in the home building (owner unset or own faction),
  reserved; guard posts fall back to general nodes. A node marker faces along its local −X: for 784 of 990 shopkeeper nodes in base-game town interiors,
  yaw − 90° points at the nearest Shop Counter's side the node stands on (**Verified**, layouts placed by `TownBuildings`, compared with each counter's
  yaw; the rest are corner or odd-angled counters). The engine turns a character arriving at a node that way.
- **118 Shopping** (`FUN_1409997f0`, **Verified**): a shop of the town selling an item on the character's shopping list (`FUN_1409fc070`), else, only when the faction of the
  shopper's town has no player flag (`+0x250` clear; the list loop runs first and a hit skips this test), a random building (`FUN_1409fc1b0`): a town of the player's faction gets
  no random shopping, only list hits; none found: 0. Factor 1 for a BF_SHOP building, 0.4 otherwise. Then 119 (`Task_Shopping`) at it. Both finders scan the zone's
  BF_SHELL_WITH_INTERIOR buildings; the random one keeps those of the character's town that are public (Building vfunc 82, the `public day`
  flag), the list one those that are shops (vfunc 87, `Building::isAShop`: BF_SHOP furniture inside, residents, and public) and stock the item.
  No faction, membership or hostility test (**Observed**, decompile), so a guild's public home with a counter (the Hub's Thieves Guild
  Watchtower, `public day` true) draws the town's shoppers.
  - **Shopping list** (**Verified**, `FUN_1409a05b0`, made with the AI by `FUN_1409a1010`; not saved): from the CHARACTER record's `shopping` list (73 base
    records; ITEM, v0 wanted count, v1 percent chance, v2 pass). Entries hold the item's `item function` (0 = Water is valid), not the item. Passes 0, 1, 2 ...
    each walk the record in order and take the entries whose v2 is the pass; the list ends at the first pass with no entry (base data: v2 is 0 for 216 entries,
    1 for 14). An entry is kept when a roll of 0..100 inclusive is not above v1. A record with **no** `shopping` entries gets three independent 50 %
    entries of 1 unit each: function 3 (food), 8 (Cactus Rum), 1 (first aid kit). A record whose rolls all fail gets none and never shops.
  - **Desire** (`FUN_140999620` / `FUN_140998fa0`): 1 while some entry has done < wanted, else 0. `done` goes back to 0 when the game clock's day counter
    (`DAT_1421303d0`+8; the hour is +0) differs from the list's and the hour is above 7 (`FUN_140998e40`, called by the desire, the finder and
    `FUN_1409995f0`), and when the buyer's town differs from the one the list last bought in (`FUN_140998da0`, called by the buy step). `FUN_140998fa0` has a
    second reset comparing the list's town with the character's `+0x88` itself, not the town it yields: **Unknown** whether it can fire. That `+8` is a day
    counter (1 at a new game, `FUN_14036cb80`) is **Observed**.
  - **Finder**: the entries in order, skipping those done; the first with a shop of the town stocking that function wins (a random one of them), else the
    random public building. A shop stocks a function when a type 9 object (a container) in it holds an item with that `item function` (`FUN_14054add0`).
  - **Buying** (`FUN_1409a14b0`, from `Task_Shopping` 119; **Verified**): `Task_Shopping` (start `FUN_14033a4c0`, update `FUN_140337d00`; the planner gives 119 the precondition `isAtLocation`, so
    it starts on arrival) holds a timer of 1.0 s, counts it down each update and calls the buy step when it is at or below 0; a sale resets it to 1.0. So the first unit is bought a second after
    arrival, then one a second. The step takes the first open entry whose function some seller in the building has an item of (a function with none is set aside for the call), a random such item, and adds 1 to
    `done` **before the sale**, so a buyer who cannot pay ("I can't afford that.") or has no room still uses the unit up. A failed sale tries again at once while that entry has units left (a new random item
    each time, so a broke buyer uses an entry's whole count in one call), then the next open entry not set aside; the call returns 1 after the first sale. A call that returns 0 ends the task: finished
    (`FUN_1400522c0`) if it had bought before, else impossible (`FUN_14001953d`, **Observed**: the AI re-plans; whether goal 118 is held back afterwards is **Unknown**). The sale `FUN_14074a630` (price,
    purse, what happens to the item) is in [economy](economy.md#npc-purchases).
  - Meitou (`ShoppingList`, `AiSystem.ShopFor`, `AiSystem.BuyForShoppers`, `NpcTrade`): the list, the desire, the daily and town resets and the stock-based pick (stock read from the containers of a building shop and of
    a house's stock containers, by item function; a trader's carried stock is not looked at, as `FUN_14054add0` does not). Arrival starts the timer (`AiMind.NextBuyTick`, hashed, not saved); a serial step in slot
    order buys one unit per second with the rules above (`AiSystem.BuyOnce`), moving the cats between the squads' purses and taking the unit out of the stock. A call that buys nothing ends the visit (target and shop
    forgotten, so the next think picks a shop again). The fallback is a random public building of the town with an interior that is not a wall or ruin (`TownPlaces.Public`; the original's set, the buildings of class SHELL_WITH_INTERIOR with function id 19, is not read from data: **Observed**), not in a town of the player's faction (`AiSystem.RandomShopping`). The desire is the finder's factor (**Verified**): 1 when the pick is a shop (Meitou's shop set stands for BF_SHOP furniture), 0.4 (`ShopNoCounterFactor`) for any other building, 0 when none is found or nothing is left on the list. A shop is walked to as before (a node in it); another building within 30 of its origin on any floor, where the buy step finds no seller and the visit ends. What gates the random fallback on the town's `+0x250` (`FUN_1409997f0`) is **Unknown**.
- **214 Relaxing** (`FUN_140998d10`): the current package's own target (package vfunc 1); none: 0. Effect `IS_WORKING`, acted out by 87 Operating
  machine, i.e. using furniture there; with no free one the plan fails and another goal wins.
- **Hang-around-bar package** (`Package_HangAroundBar`, start vfunc 6; **Observed**): its town is the squad's home town, else the nearest friendly one
  (`FUN_1402916b0`); only while that town's zone is loaded. The town's buildings of designation 3 (BD_BAR, the residents' template's
  `building designation`; town vfunc `0x2d0`) are scanned cyclically from a random one; the first with a free **table** wins, that table is
  reserved for the squad in a global table→squad map (`DAT_14212ddb8`) and becomes the package's target. A town with no bar takes a free table
  outdoors in it; all bars full: no target. The table search (`FUN_140292de0`, `BuildingFinder_TableToSitAt` vfunc 5) keeps furniture of
  function mask 0x10810 (BF_REFINERY, BF_CORPSE_DISPOSAL, BF_TABLE) in the town, of a faction not hostile to the squad's, inside the bar (or,
  with no bar, inside no building), in a public building (vfunc 82) and not reserved. The outdoors variant (flag +0x151, BarOutdoors) skips the
  bars and takes a free outdoor table. So a bar holds as many visiting squads as it has free tables.
- **36 Patrolling town** (`Task_PatrolTown` slots 1-3, **Observed**): walks to its target's position (start, update and resume pass modes 0/1/2
  to the mover `FUN_1405c6b30`); row 36 has no target finder, so where the target comes from is **Unknown**.
- Building layouts' `is bar` / `is shop` are 0 on every exterior layout of the base game and mods checked (2 interior layouts set `is bar`, 9 `is shop`),
  so they do not mark bars and shops (**Verified** over the merged `interiors.level`).
- **43 Staying home** (`FUN_1408cbc00`): furniture in the home whose BuildingFunction is in mask 0x2049a (bits MINE, RESEARCH, REFINERY, TRAINING,
  CRAFTING, CHAIR; reading the mask as bits is **Observed**). Both it and 275 lead to 221 (pretend operating).
- **275 Sitting around** (`FUN_1408caf00`; the registration's finder thunk `LAB_1400061db` is not in the RE files, so the row link is **Observed**; the rest **Verified** by decompile):
  - Gate: 0 (not wanted, no target) when the character is in no town location (character vfunc `0x78`). A non-player-faction character (faction `+0x250` clear) needs that town to
    be its squad's home town (Ownerships `+0x30`) or owned by the squad's faction (`FUN_140621390`); a player-faction one needs a town owned by a player faction, with no home test.
  - Scan (only with no target yet; one already set keeps factor 1): the town's own furniture (Town vfunc `0x2c8` `FUN_1408d10f0`: the buildings of the town's registered zones
    whose town it is; not a 3x3 round the character) of mask 0x1020080 (TRAINING, CHAIR, THRONE), 0x1020000 without TRAINING when `FUN_140856bc0` is true (the faction has `+0x250`,
    or the character is held by such a character). Slot 0 of the finder (`FUN_1408cebe0`) keeps pieces the squad may use (`FUN_140621230`), not on the character's avoid set, with
    a floor-reachability test; the first whose owner faction is the character's or the squad's and that no one else claimed (`FUN_1402690e0` < 1) is the target; none: 0.
    The list order is the zone map's (arbitrary); the usability and avoid-set details are **Unknown**.
  - Pieces: BF_CHAIR and BF_THRONE are sat on ("Chairs" below), BF_TRAINING (the dummy, function 7) is pretend-operated. 43's finder is the `Indoors` variant of the same class (vtable
    slot 5 `FUN_1408cbad0` lists the home's interior), so 43 takes training furniture and chairs but no throne (mask 0x2049a); 275's is the plain one, whose town scan also reaches furniture
    inside buildings (**Observed**: the bar-table finder, same scan, tests "inside the bar").
  - Meitou (`AiSystem.ClaimSitting`, on the package cadence): no piece claimed means the goal is not wanted; a squad does not sit where it stands. The town gate and the owner test above are
    applied as written, with the town the member stands in (radius test) and the building's owner = its property owner, else the level state's `owner faction ID`, else the town's faction;
    the player-side branch is coded but unused (an AI-run squad is never the player's). Pieces: a free chair, throne or (not for the player's side) training dummy in any building of that
    town (`TownBuilding.SeatAt`: the layout's chairs, then `Furniture`: layout thrones and dummies, and free-standing chairs, thrones and dummies), the nearest to the member (engine choice:
    the original takes the first of an arbitrary order). A dummy is stood at, facing its way. Not modelled: the finder's usability and avoid-set filters (slot 0), a piece's own owner
    (the building's is used).
- Chairs (**Verified**, BUILDING_FUNCTIONALITY records of gamedata.base / rebirth.mod / the install's mods, dumped): BuildingFunction 17 BF_CHAIR
  `chair`, `chair-fixed`, `chair-fixed 180` (animation "sitting chair"; Stool, Old Stool, Bench, Sitting Box) and `sit on floor (no chair)`
  ("sitting idle"; Sitting Pillow, Sitting spot (invisible)); 24 BF_THRONE `throne chair` ("sitting chair"); all `max operators` 1.
- Sitting (**Observed**, decompile and disassembly of `Task_OperateMachine` slots 1 `FUN_14035b430` and 2 `FUN_14035bc90`): for a BF_CHAIR or BF_THRONE
  the sitter's body is put on the seat building's own position (vtable `+0x40`, its node origin, height included) with no offset, then turned to a look
  point; what the seat's float at `+0xa4` (vtable `+0x60`) passed alongside does is **Unknown**. The look point starts as position + orientation × +Z (`UNIT_Z`):
  a seat faces its +Z.
- Seat facing (same source): a BF_CHAIR's functionality `flags` picks the look point. 0 (`chair-fixed`,
  `throne chair`) the seat's own facing; 1 (`chair`: Stool, Old Stool, Sitting Box; `sit on floor`) the nearest building of function 16 BF_TABLE,
  11 BF_CORPSE_DISPOSAL or 4 BF_REFINERY (the seat's parent building's furniture, else ones near the sitter); 2 (`chair-fixed 180`: Bench) keeps the seat's
  facing when that target is in front.
  **Verified** (`FUN_14035b430`, branch `0x14035b936–0x14035b9b6`, Ogre imported unary minus):
  behind it, the original negates the **absolute world-space look point**, not the forward vector. The resulting direction is `-(seatPosition + forward) - seatPosition`, generally toward the world origin, rather than a 180° turn about the seat.
  **Observed** native 1.0.68 save: bench at XZ (-50595, -1005), placement yaw 0, table behind, seated yaw 1.5509452 rad, matching that calculation. With none found, the seat's facing. Layout stool rotations are arbitrary (Observed: the Hub's
  Storm House, up to 161° off their table).
- **24 Wandering** (`FUN_140998040`): the nearest town (squared XZ distance from the character, its own town included) of any type but a nest marker (type 8): the town list search
  `FUN_140927f10` is called with type filter 10 ("any"), which still skips type 8 unless the filter is 8 (**Verified**); list A holds `atATown`, so it is dropped while in a town.
  Meitou (`AiSystem.TargetFor`) skips `TownType.NestMarker`.
- **180 Man the gate** (`Task_ManTheGate` slot 2 `FUN_140342c50`, target `FUN_140338690`; **Observed**, disassembly): the gate is the
  task's target, else the Blackboard's home (`HOME_GATE`), else the town's gate nearest the character (town vfunc `0x308`), each only when it
  is a GatewayBuilding (vfunc `0x3d8`). The guard walks to its first door's point `+0x3bc` and turns along `+0x3bc - +0x3c8`. A door's
  two points (`FUN_14029cc90`) are its leaf's centre ± 15 (constant `0x14168a258`) along the leaf's thin axis, dropped to the ground;
  which side `+0x3bc` is depends on a door body offset (**Unknown**). Gate BUILDING records have no nodes (**Verified**, the merged
  load order's Defensive Gates II-V), so the squad's guard node goals find nothing there.

## Perma-jobs

A character's job list (the Orders menu's "Perma-Jobs"; class `OrdersReceiver`, base of `AITaskSytem`). Meitou: [simulation](../simulation.md#production).

- **Lists** (**Verified**, add function `FUN_1405086d0`, constructor `FUN_140507d20`): `OrdersReceiver` +0x70 holds one-shot orders, +0x88 (count +0x90,
  data +0x98) the perma-jobs in priority order, +0x35 the jobs switch. Each entry is a Tasker: +0x10 target hand, +0x70 its TaskData (+0x44 task type).
- **Adding** (**Verified**, `FUN_1405086d0(orders, type, target, position, permanent)`; Shift with an order, tutorial `FUN_14098d8c0`):
  1. TaskData row +4 set and +0xc set: the type becomes +0xc (orders too).
  2. Not permanent, or the resulting row's +4 is 0: appended to the order list.
  3. Else refused when a job of that type exists and the row's +8 is 0, or when one exists with the same target. Otherwise appended, the jobs
     switch turns on (`FUN_140508210`, which also posts a UI event for a player character), and +0x10, when set, is appended as a second job on the
     same target with no checks (0x1f becomes 0x121 = 289 when the receiving character's vfunc +0x248, `asAnimal`, is non-null: **Verified**, `FUN_1405e1f50`
     returns the character itself in the `CharacterAnimal` vtable, `FUN_1405e5950` returns 0 in `CharacterHuman`'s; only this companion is converted, a Follow job 31 stays 31). Type 45 also stores
     the target at +0x18.
  4. The planner is re-run (vfunc +0x18).
  5. For 45 and 31 the order giver `FUN_1405c8da0` then runs the circle break `FUN_1405c8c10`, which removes the 31 / 45 jobs and the one-shot follow that would close a
     circle back to the ordered character (**Verified**; [Following](animals.md#following)).
- **Ordering** (**Observed**, order dispatcher `FUN_1407fa650`): with Shift held (`DAT_142133449`) an order goes to `FUN_1407f5a90`, which adds it
  as a job to each selected character, then posts the Wwise event `Permanent_Job`; without Shift it is a one-shot order. Orders 0x2d, 0x111 and 0xdf
  are always added as jobs; Loot with Shift on a building adds 124 "Hauling to". The Medic and Rescue buttons dispatch 58 and 148 the same way:
  one-shot, or list entries with Shift. Meitou (`PlayerInterface.OrderOrJob`): with Shift, Attack Target and Attack Unprovoked add 16, First Aid
  and Splint Injuries, Loot and Follow their jobs (`PermaJobs.Rules`); Bodyguard always adds 45 (+ 31).
- **Row fields** (**Verified**, `FUN_140319a60`; the 36 rows that set +4, all others leave it 0, **Observed**). +4 kind (2 = turret jobs), +8 several
  targets allowed, +0xc job type, +0x10 companion:

  | Ordered → job (+ companion) | +4 | +8 |
  |---|---|---|
  | 87 → 87, 221 → 221, 124 → 124, 97 → 97 | 1 | 1 |
  | 152 → 152 + 124 | 1 | 1 |
  | 2, 95 → 125; 25 → 58; 249 → 250; 60 → 57; 26 → 238; 4, 5, 61 → 16 | 1 | 0 |
  | 146 → 234 | 2 | 0 (234: 1) |
  | 45 → 45 + 31, or + 289 for an animal receiver (31, 289 rows: +8 1) | 1 | 1 |
  | 148 → 148 + 106; 223 → 223 + 224; 273 → 273 + 274 | 1 | 0 |
  | 136, 137 → 137 + 138 | 1 | 1 |
  | 98, 175, 261 → 153 (153 has no +4: the bed order stays one-shot) | 1 | 0 |
  | 58, 250, 57, 238, 16, 125, 106, 138, 187 (plain jobs) | 1 | 0 |

- **Choosing** (**Verified**, decision pass `FUN_140510ef0`, job scoring `FUN_14050e5b0`, pick `FUN_140510650`; [Precedence](#precedence)): order list → jobs of kind 2 while
  the AI flag at +0xbc is set (and 187) → priority-3 goals → all jobs → tiers 2, 1 → idle. Within a job pass each job considered gets the key N+8, N+7, ...
  (N = list length) in list order, kept when its desire is above 0. The pick takes the highest key above 0. **So the first job that can run wins,
  and a higher job takes over as soon as it can run** at the next re-plan; the key does not depend on the current task, so the running job keeps no priority beyond its place
  (**Verified**, `FUN_14050e5b0`). A job is skipped when: it has no TaskData; the switch is off and its
  type is not 16, 31, 45, 57, 58 or 250 (`FUN_14050c1c0`, tested in both the urgent and the full pass, **Verified**: these run regardless, so the medic job ignores the switch and rescue does not); the
  planner finds an unmet list-B state and the row has no effect (row +0x14 clear; `FUN_14060f940`); its list A holds (done; `FUN_14060f570`,
  `FUN_14050c8e0`); or desire × target finder is 0. Type 187 (Obedient Slave) has a slave-state branch of its own.
- **Range** (**Verified** by decompile of `FUN_1405a3370`, `FUN_140599f30`, `FUN_140597790`, `AI::AI` `FUN_1406236d0`; names KenshiLib 1ce4a16,
  **Observed**): a job's walk is planned through rows 1 Moving / 67 Running, whose list B needs 27 `isMovementAllowed`, so a job whose target fails
  it cannot be planned and is skipped (so is every walking job while the character gets up). One-shot orders walk through 29 Move order, which lacks
  27, so they reach any distance. State 38 `hasOneWorkingArm` (list B of 87, 97, 146, 234) has no providing row, so those jobs are skipped by a
  character with no working arm.
  `AI::isMovementAllowed`: false while the character's +0x278 byte is set (KenshiLib `isCurrentlyGettingUp`); a target object goes to `isTargetInRangeForOrders(RootObject*)`:
  true when the target's `getCurrentTownLocation` is the platoon's `Ownerships::_homeTown` (for the player faction **Unknown**, see 223 below), else
  the position test below, else true for a character of type 1 that is attacking (`CombatClass::isAttacking` in (0, 1)). Position test
  `isTargetInRangeForOrders(Vector3)` by `AI::ordersMovement` (+0x2bc, `AIManuverabilityOrders`; the constructor sets 2): zero vector → true;
  2 ROAM_FAR: the 3D distance² from the character itself < 4.246733e7 (≈ 6517; 12.5 × the medic nearness 3397386.5); 1 STAY_CLOSE (standing order
  NEAR, the centre set to the character's position then): from `centerOfMovement` (+0x2c0) < `meiDistance` (+8) + 200 + `meiDistanceTolerance` (+0xc);
  0 HOLD_GROUND: + 40 instead, with an extra term **Unknown**. Other values: false. Standing order FAR sets ROAM_FAR (EDX = 2 in the disassembly) and clears the centre target.
- **Implicit jobs** (**Verified**, `FUN_14050e5b0` after the list, only for a character with a squad (+0x250), the switch on and not sneaking
  (`FUN_1405c9750`)): lowest keys for the squad AI options at `DAT_142134690` (the overview's AI tab), each kept when its task's desire is above 0:
  +0x117 "Ditch items" → 242 "Ditching resources" (only with an empty task queue +0x60; the entry is tested afresh on every pass, nothing carries over, so a running trip drops out as soon as the finder finds nothing or an order is queued; finder `FUN_1405a59a0`: the carried ITEMs (type 4) of `item
  function` 0 or 15, and food (3) but the first, via `FUN_140749490`, **Verified**; that 242 passes the collect flag **Observed**; the nearest UseableStuff in range whose accept test (slot 0x4a0) takes one, skipping
  ones the squad already uses, **Observed**; Meitou: storages only); then, unless one of three tests holds (`FUN_1402b2b90`
  on the character's +0x450: hold position +0x12b on and not sneaking, **Verified**; byte +0x348, carrying someone; `FUN_1407f4d20`, a set lookup
  read as "selected by the player", **Observed**, not modelled), +0x116 "Sleep when injured" → 257 "Getting out of
  bed once healed" above 153 "Go home and go to bed", and +0x118 "Sit when idle" → 275 "Sitting around" (its task is built with a float 0.5 where the others pass 1, meaning **Unknown**).
  Tooltips (**Verified**, `FUN_140005e11`): ditch puts excess carried materials into storage when idle; sleep goes to bed when injured and idle;
  sit takes chairs and benches at home. The injury threshold of 153 is **Unknown**; its target finder is the bed search (**Verified**, [locks](locks.md#beds)). Saved as CAMERA bools ([save](../formats/save.md)).
  The other options of the panel: [Squad AI options](#squad-ai-options). Feed and share: [eating](character-stats.md#eating).
- **Save** (**Verified**, `OrdersReceiver` vfunc 1 / 2; 4122 GAMESTATE_AI records of the owner's saves, 199 jobs, types 187 and 57 only): record
  `ai` (GAMESTATE_AI); bool `jobs` = the switch; per job i from 0, int `pjob<i>` (type) and handle `pjobT<i>`. The loader re-adds each job
  through the add function (so companions and the switch are re-run) until `pjob<i>` is missing, then reads `jobs`. `AITaskSytem` vfunc 1 / 2
  also saves the order queue (+0x40 deque, [format](../formats/save.md#order-queue)). The GAMESTATE_CHARACTER bool `chase mode` is the
  same switch (**Verified**: the writer `FUN_1406280a0` stores the byte +0x35 of the orders receiver at character +0x650 → +0x20; the loader
  `FUN_140626f50` passes it back as toggle 15); the orders panel's `OrdersChaseButton` (captioned JOBS) is this switch.
- **Finders of the jobs** (**Verified** registrations in `FUN_140319a60`, finder thunks resolved; readings **Observed**; "sensed" = the
  character's sensed list `FUN_140853960(include, exclude)`, whose bits are **Unknown**):
  - 58 Medic / 57 Robotics: `FUN_14099f690` with a flag (58: 0, 57: 1; [Medic jobs](#medic-jobs)). Desire `FUN_140998410` (both, and 250).
  - 250 Splint Rigging: `FUN_14099e240` ([Medic jobs](#medic-jobs)).
  - 16 Attacking enemies: `FUN_14059f3a0` ([Combat goals](#combat-goals)); it searches only while the AI's +0xbc (`numEnemies`,
    [senses](perception.md#senses)) is nonzero, which is also the condition of the kind-2 job pass above.
  - 238 Foraging animals: `FUN_1408ce3a0`: the first sensed (0x18, 1) character that is down (`FUN_1407d1440`: carried, or ragdoll flags), an animal
    (vfunc 0x248), not carried, passes a character vfunc 0x3e8 test or has faction flag +0x1d0 (**Unknown**), is in no give-up list, and holds an
    item that has a store (`FUN_140998950`, inventory then backpack, over `FUN_14059acd0`, **Verified** decompile): a complete building with a production interface of
    the forager's faction (a player-side forager: any player-faction building) within the AI's range mode (ROAM_FAR: squared 4.2467e7) whose `canHaveSomeOfThese` takes
    it, the nearest. Effect 148 → row 241, a timed take of one item kind ([foraging take](carry-loot.md#34-foraging-take-row-241)). Meitou `CarrySystem.Storable`: storages only, `MachineData.Accepts` for the slot test (free room not tested).
  - 223 Remove Home Intruder: `FUN_14099c110`: the first sensed (0x400e, 0x201) character, not carried, inside a building whose handle equals the home building hand of the character's
    Ownerships (+0x38; `Hand` vfunc 0x10 is "not equal", so the test `== 0` means equal; for the player faction the Ownerships is one shared object whose home is **Unknown**), down (vfunc 0x30), special state
    (+0x2f8) 0, i.e. not in a bed or cage. 273 Remove Intruder: `FUN_14099c250` (**Verified** by decompile; slot names KenshiLib 1ce4a16, **Observed**): only while the character's current town
    (vfunc 0x78 `getCurrentTownLocation`) passes town vfunc 0x2f0 (`Town`: +0x3b0 set, meaning **Unknown**; `TownBase`: false) and `FUN_1406211e0` (`Ownerships::isMyTown`: the town is
    `_homeTown` +0x30, `occupiedTown` +0x80, or of the Ownerships' faction +0x70); sensed (0x400e, 0x201) in the same town, not carried, unconscious (vfunc 0x30), special state not 2 (not
    caged), `amInsideTownWalls` (vfunc 0x118) > 0 (inside a building: that building's value, a gate by the side it stands; outdoors the gate code the [gate-code pass](pathfinding.md#gate-codes) gives it).
    So an occupier removes intruders from the town it holds. Meitou for a player character (`ProductionSystem.FindTownIntruder`): the carrier is in a town of the player's (nearest by centre within its
    radius) and the body in the same town inside its walls (`TownDutySystem.AmInsideTownWalls`), a building not needed; the town's gate-side and `isMyTown` tests are not read. Their companions 224 / 274 carry the body outside: `Task_TakeIntruderOutside` (**Verified** by decompile, slots 2/5/7 `FUN_1403355e0`/`FUN_14033da80`/`FUN_1403356e0`) walks
    to the point beyond the home building's first door (`Building` vfunc 0x3e0 → door +0x3bc, pushed out by a factor 1.1 of a reference vector, **Unknown**); its start (slot 2) ends the task
    without a home building and drops at once when nothing is carried; its update (slot 7) drops the body (`FUN_1405ce1e0`) once the walk is over or 5 game minutes (elapsed hours x 60 >= 5,
    **Observed**) have passed. `Task_TakeIntruderOutsideTown` walks to the town's exit point (`Town` vfunc 0x38 `FUN_140927120`, a cached point refreshed on a timer: 200 beyond a door of one of its
    buildings through vfunc 0x290 `FUN_140929560`, else beyond the door of a building `FUN_1409b2ab0` finds; only partly read, **Observed**) and drops it there.
  - 138 Find corpses: `FUN_1408cfb00`: a building of function 8 (cage) of the character's (`FUN_1408cbea0` mask 0x100) holding a dead,
    non-robot body not in a give-up list, else `FUN_1408ce170` (the nearest dead uncarried body: sensed (0x10, 0), down `FUN_1407d1440`, not carried, a flag at +0x5bc, not in a give-up list; for
    an animal finder only other races). 137 Corpse disposal: `FUN_1408d1090` → `FUN_1408d0770`(1, 0xb): the building of function 11 (BF_CORPSE_DISPOSAL, the Cooking pot / Corpse disposal
    functionalities of `Four Cadavres` and the rebirth shredder, **Verified** data) among the character's buildings whose `UseableStuff` vfunc 0x4f0 holds, weight clamp(1 - d^2 / 2.097e7, 1e-5, 1) (`FUN_1409b2380`),
    x100 for the character's faction (else it must pass `canIUseThisBuilding`, `FUN_140621230`, [locks](locks.md#beds): every function-mask finder applies it, not only the ally test), x100 when its owning object is the squad's home building (Ownerships `+0x38`), x1e-5 for a law enforcer (`FUN_140599600`, not a "player-type squad member") when the parent building exists and its vfunc 0x290 (`isPublic`, building `+0xc8`) is false (**Verified**, `FUN_1408d0770`);
    the heaviest wins. At the machine `Task_FeedCorpseIntoMachine` (136; slot 3 `FUN_1403567a0`, **Verified**) lets go of the body and kills/removes it ("incinerated or cooked"), plays `Armor_Worn` and
    `Character_Drop_Person` and floats the functionality's `string` over the machine ([corpse machine label](buildings-production.md#classes-and-functions)).
  - 45 Bodyguard, 31 Staying close and 97 Training have no finder: they act on the job's own target; 45 and 31 are goals (Task_Blank, rows above) on the job's character, the context menu gives
    them as "Follow" (31) and "Bodyguard" (45), the dispatcher always adds 45 as a job. 97 is `Task_UseTrainingDummy` (slots 1-3 `FUN_1403513a0`, `FUN_14035c1e0`, `FUN_140345570`; **Verified** by
    decompile): the dummy (function 7, BF_TRAINING; the animation event `Impact Dummy`, a "Training" progress bar for the player's characters) is operated through `UseableStuff` vfunc 0x4b0: with a
    positive `stat used` (+0x3f8) it calls the continuous XP entry with a cap, `FUN_1408c64f0`(stats, dt, 2.0, &stat, cap = the float +0x1a4, stat number): nothing once the stat is at the cap, else the
    same gain as the operating XP (`0.02 x dt x exp gain mult x 2.0 x race[stat]`) through the core curve with that cap, ((cap - stat) / cap)^2, so the stat approaches the cap without reaching it.
    The cap is **Observed** to be the BUILDING's `output rate` (MkI 6, II 11, III 16; lock 11/21/31; assassin 10/20/30; the descriptions promise levels 5/10/15, the displayed level being the whole part);
    the field +0x1a4 itself was not traced to the record. The bar shows the stat's fraction (+0x3a4). Meitou reads it as the stat's fractional part (`TrainBar`; **Observed**). The row's conditions are 38 `hasOneWorkingArm`, 73, `isAtLocation`.
    The XP is the user's own stat, not the injured one. Meitou: [simulation](../simulation.md#job-lists).
- **UI** (strings, `FUN_14072b540` JobsPanel): "No jobs assigned", "To add jobs, hold shift key when giving an order", the Jobs toggle "If this is
  disabled then the character won't do any of the jobs listed."; tutorial: jobs are reordered by dragging and removed with X. Row text: **Unknown**.
- **Unknown:** what list A/B mean per job type in detail (operating 87: A `IS_WORKING`; B free slot, power, inputs not
  empty, output not full, at the machine); how 124 "Hauling to" gets the items it needs (B 67 `haveSomeResourcesFor`; no row's effect is 67).

### Medic jobs

**Verified** (decompile of `FUN_14099f690` (Medic 58 passes 0, Robotics 57 passes 1, and the NPC `Medic` goal), `FUN_140998200` (need), `FUN_140998b90` (nearness), `FUN_140998410` (desire),
`FUN_14099e240` (splint finder), `FUN_140646400`; constants read from the exe). Names from KenshiLib 1ce4a16: `Blackboard::howManyGuysWorkingOnThisSubTarget` (`FUN_1402690e0`), `isMySubTarget` (`FUN_140269190`),
`isTargetMarkedForDeath` (`FUN_14032eb40`), `AITaskSytem.pathFailures` (+0x218, the "give-up list"), `Character::isAlly` / `isEnemy` (vfuncs `0x3f0` / `0x3e8`), `amInsideTownWalls` (`0x118`), `isIndoors` (`0x1d8`, a building
hand), `isGettingEaten` (+0x279), `CharStats._holdPositionMode` (+0x12b).
- **Give-up**: a task walk that cannot get to its target puts it in `pathFailures` for 20 s (`notifyPathImpossible` `FUN_140796ed0` stores 20.0; **Verified** callers besides the combat chase: the AI plan step `FUN_14050d970` when the task's reach test fails, and `AI::isWithinChoppingRange` `FUN_14059c6d0`, which logs "Task impossible: X can't get to Y"), so the finder skips that patient meanwhile. Meitou: the walk to a patient gives up when its path request finds none or one ending farther than the treat reach from the goal (`FirstAidSystem.Schedule`); before, a medic re-asked the same hopeless path every 0.5 s (issue #124).
- **Kits**: any item of function 1 (organic) or 12 (robot) in the medic's inventory or its backpack (**Verified**: `FUN_14099f690` tests the inventory, then a second lookup of the backpack's inventory; `FUN_14004b84e` compares each item's function only, with no charge or quantity test). Squad mates' kits do not count for the job. The task then takes a kit from the medic's own main grid, then its worn backpack, the fewest charges above zero (the rig task's search, **Verified**; the first aid tasks share its vtable slot, **Observed**: [kit search](character-stats.md#first-aid-update)). The splint finder wants function 2 and takes X = min(the medic's `medic` stat, the kit's quality).
- **Desire** (`FUN_140998410`): 0.01 while the AI's `+0xb0` is above 0, else 0.05 while `+0xbc` is above 0 (the senses' threat towards the medic and `numEnemies`, **Verified**, [senses](perception.md#senses)), else 0.1 while the medic carries someone, else 1. Always above 0, and `FUN_14050e5b0` keeps a job whose desire is above 0 (**Verified**). The finder has no squad-fight test (**Verified**, `FUN_14099f690`): the finder alone does not keep a player medic from a downed mate while its squad fights (**Observed**, offscreen sandbox run); the step does, through 15 Protect allies at step 4 ([precedence](#precedence)). Meitou: an NPC medic waits while its squad fights or it carries someone (engine stand-ins for the low desire; a kidnapper otherwise walked off after a patient with its captive).
- **Need** (`FUN_140998200`) of a patient for this medic: 0 while the patient's CombatClass `+0x130` is set (fighting). The kit test is on unless the medic is not of the player's faction and the patient is imprisoned or a chained
  slave of the medic's faction (`FUN_14032e2a0`, which then counts without kits); the organic need score ([first aid need](character-stats.md#first-aid-need-score)) counts with an organic kit, the robot one with a robot kit.
  Another faction's patient: x 0.8 when `isAlly` (relation 50 or more, the allied flag, squad memory), else x relation / 100, at least 0.05 (`DAT_141696c74`). The further floor of 0.01 for a player medic with a current
  task (`FUN_140507b70`: its task list is not just GET_UP 65 / 52) is dead code: it needs a factor at or below 0, which the 0.05 minimum rules out.
- **Nearness** (`FUN_140998b90`, flag = the medic's hold-position mode): 0 when the flag is set, the patient is over 40 units (1600 squared) from the medic and over 40 from its hold point `+0x130`; else 1 - d^2 / 3397386.5 (3D),
  at most 1 and at least 0.4 when both are in the same building (vfunc `0x78`), else 0. The pass-1 and pass-2 calls pass no flag (an unset register, **Observed**); Meitou applies it everywhere.
- **Pass 1, allies** (sensed entries with bit 0 ally, none of bit 4 dead): not of a robot race (`+0x7c`) unless job 57; not being eaten (`+0x279` & 7); not on the AI's path-failure list; not carried by an enemy of the medic; not
  fighting (`isInCombatMode`); for a medic not of the player's faction the patient must not be maimed (`FUN_140644170`: a stump in a leg, or in both arms: limb states 1 at slots 2 / 3, or 0 and 1) and, when the medic stands in a
  building, must be seen or heard (the entry's two flags). Score = need x nearness. The same faction always counts; another faction only at 0.1 or more and (the medic is not of the player's faction, or **Heal Allies** `DAT_142134690+0x110`).
  The blackboard's sub-target list: when the score exceeds 0.001 and the pair (medic, patient) is on it, that patient is taken at once with score at least 0.95; a patient others work on (entries with another medic) x 0.05; x relation / 100 once more
  when not an ally (never for an ally entry). The highest wins; then **the medic itself** at its plain need beats it when larger. A found patient ends the search.
- **Pass 2, strangers** (sensed entries with any of neutral, knocked out, prisoner, crawling and none of enemy, dead): not of a robot race (job 58), not an animal (`0x248`), not being eaten, not of a not-real faction, not fighting. A free patient:
  not a bandit-class one when the medic's class differs, not an enemy, passing `FUN_14059a670` (**Verified** to be DC 55 I_SHOULD_HELP_THIS_GUY, Meitou `DialogueSystem.Helps`: same NPC class, not for a traitorous or bandit medic, yes when friendly, honourable both or loved, never when hated),
  seen or heard; it counts only when it is down (`0x30`) and the medic's **faction record says `heals strangers`**. An imprisoned patient (`+0x2f8` = 2): its cage must not be the blackboard's cage marked for death, the patient not a slave
  (status +0 = 1), and the cage owned by the medic's squad home (ownerships `+0x38`); a patient with no cage passes. A chained slave owned by the medic's faction also counts (slave state under 2). Then the medic and the patient must share the town
  (`amInsideTownWalls`) and the building (`isIndoors` hands equal; **the earlier reading "different platoon" was wrong**: the hand's vfunc `0x10` is not-equal and the test needs false). Score = need x nearness; the sticky pair of pass 1 applies here too (checked before the next rule); unlike pass 1 a patient
  others work on is skipped, not weighed. No 0.1 floor, no Heal Allies.
- **Pass 3, owned slaves**: the entries (hand types 1 character, 0x5b squad hand) of the medic squad's ownerships tree (the platoon's "slave list", character-ownerships `+0x10`; not a sensed-list or faction walk) that are chained (`+0x320`) slaves (state 1), known to the medic's own AI (`FUN_140855960` on the character's vfunc `0x68`, which returns `AI+0x28`, `FUN_1405c8700`: **Verified**, not the squad's), not fighting, not a robot (job 58): need x nearness. **Pass 4**, only while no one was found and the medic is outdoors:
  sensed carried (bit 15) patients, not a robot or animal, not eaten, seen or heard, whose carrier is of the medic's faction: need x nearness (flag = hold mode). **Pass 5**, after 3 and 4, when the medic stands in its squad's home building: the
  occupants of its cages (function 8 buildings of that place): need x 0.25 flat (`DAT_141696c80`), taken when above the best so far. So NPC guards patch up their prisoners, without a kit.
- **Splint finder** (`FUN_14099e240`): no splint kit: nothing. Sensed allies, not robot races, not path-failed, not carried by an enemy, of the **medic's own faction only**; weight = `needsSplint`(X) (`FUN_140646400`, **Verified** by disassembly:
  0.1 when a part meets the [needsSplint test](character-stats.md#splints); -0.1 for a dead patient, else 0) x nearness (flag = hold mode) with the same sticky pair and x 0.05 rules; the medic itself at 0.1. No squad-fight test in the finder (**Verified**, `FUN_14099e240`), and the job's desire is `FUN_140998410` like the Medic and Robotics jobs (**Verified**: the registration `FUN_140319a60` gives 58, 250 and 57 the same desire thunk `LAB_1400442d8`, no other task uses it; that the thunk is `FUN_140998410` is **Observed**, the function is not in the decompile), always above 0, so a free player splint job runs while its squad fights. Meitou: the squad-fight wait applies to NPCs only (`FindSplint`; only the player's job calls it); the finder's `InCombat` gate on the medic itself stays (**Unknown** in the original).
- **NPCs run it as a goal**: the `Medic` AI_TASK (enum 58) is in the goal lists of most AI packages (Squad, Squad2 and Leader roles; e.g.
  `Guard building, residents`, `Guard town`, `Shop`, `Wandering from town to town`, the slaver packages; **Verified** by a data survey of the
  full load order, 2026-10-24). Its row in `FUN_140319a60` (like 57 and 250) has crowd factor `+0x34` 0.8 (**Observed**). Score = desire x weight x 0.8^n x the finder's score
  (0 for no patient). Meitou does not score it: an NPC heals from the `FirstAidSystem.SlowWorld` sweep when idle (any NPC, whether or not its package lists 58), with the desire's
  fight and carrying cases as hard gates in `FindPatient` (a scored 0.1 would lose to the kidnap/rescue carry, which is not a scored goal here). Scoring it needs the finder's result
  from a serial step (`FindPatient` shares scratch lists, the think is parallel) kept as sim state.
- Meitou (`FirstAidSystem.FindPatient` / `FindSplint`, one finder for the player's jobs, the Medic button and idle NPCs): all five passes. Original data used: the path-failure list is `CharacterCold.PathFailures` (written by pursuits; the finder skips such patients; a treating medic does not note its own failed paths yet: calling `PursuitSystem.NotePathFailure` from `Schedule` made the medic drop its patient in a `--load` run, a stale `PathFailed` flag is the suspect, **Unknown**); `isAlly` is `DialogueSystem.RegardsFriendly`, `isEnemy` its squad memory tags (`EnemyMemory`) then the relation, `FUN_14059a670` is `DialogueSystem.Helps` (personality included); the cage marked for death is a Kill or Eat job of the medic's squad on that cage (`CaptiveSystem.Jobs`). Stand-ins left: "sensed" is the medic's sight range, "seen or heard" always holds, "the same town and building" is `SightMap.PlaceOf`, "the squad home" is the squad's home building (its own, else its town's; a cage in it passes pass 2, and pass 5 runs only inside it, over its cages; a squad without one, the player's, keeps a faction test in pass 2 and has no pass 5), the slave test needs `Shackles`, pass 3's slave list is the squad's slave members and the slaves obeying it (`CharacterCold.SlaveMaster`, runtime only: a slave with no known master, after a load, counts for every squad of its owner faction), the disguise faction of `isAlly` is not read, the sub-target list is the set of (medic, patient) pairs of running treatments, the goal's desire stand-in "no fight in the squad" applies to NPC medics only (player medics are not held back by fights, see Desire above). The sticky pair (a running pair is taken at once with a score of at least 0.95, passes 1 and 2 and the splint finder; `FUN_140269190`, `DAT_1416bc15c`) and the not-being-eaten test (`MedicalState.EatenCountdown`; passes 1, 2 and 4 only, not pass 3, pass 5 or the splint finder) are modelled (**Verified** by decompile of `FUN_14099f690`, `FUN_14099e240`). Not yet modelled: the second relation factor in pass 1 for a patient that is not an ally (`FUN_1406b2460`, **Unknown**).

## Squad AI options

The overview's AI tab, bytes of `DAT_142134690` (**Verified**: CAMERA writer `FUN_1403eb710` / reader `FUN_1403ebf50`, key per byte): +0x110 `heal`,
+0x111 `help`, +0x112 `rescue`, +0x113 `stay`, +0x114 `feed`, +0x115 `share`, +0x116 `sleep`, +0x117 `ditch`, +0x118 `sit`, +0x119 `eject`, +0x11a
`shootfirst`. Captions and tooltips **Verified** in the exe. The AI tab (`FUN_140494ce0`, **Verified**) has nine tick lines (all but `stay` and `eject`, which no
line shows) and a last line "Follow Formation", a `DataPanelLine_DropBox` (`Kenshi_ComboBox`, right edge at 0.97 of the panel; its size **Unknown**) with Random (0), Military (2),
Caravan (1) in that order, tooltip "Formation used when squad members are following a leader", bound to the player faction's FACTION `squad formation`; a change marks every
player squad for a new layout ([squads travelling together](pathfinding.md#squads-travelling-together)). The CAMERA int `formation` holds it, read only when the record has
`ditch`. Meitou: `SquadFormationCommand` sets `World.FactionFormations` (the movement rebuilds the slots when the type changes). New-game values (**Observed**, 511 of the owner's saves): ditch, sleep, sit, share,
feed, eject on; heal, help off (on in 4), rescue, stay, shootfirst off. Readers (**Verified** by decompile):
- **Heal Allies** (+0x110): the medic finder's other-faction test, pass 1 only ([Medic jobs](#medic-jobs)); "Characters with the medic job will also heal characters of allied factions".
- Meitou-only lines after the nine (not in the original): **Heal Neutrals**, a player medic's pass 2 takes any free downed stranger that is no enemy and not carried by one, without the
  `heals strangers`, class and `FUN_14059a670` tests; **Heal All**, the same with enemies too. CAMERA bools `meitou heal neutrals` / `meitou heal all`, written only while on; the
  tab's lines shrink to fit the original panel.
- **Help Allies** (+0x111): the protect-allies finder `FUN_14099ca10` (goals 15, 288; its same-town twin `FUN_14099cf80` is never registered) (sensed (1, 0x208), seen or heard): for a
  player-faction looker each fighting candidate's threat (`FUN_14099a190`) is × `FUN_1409b2380`(d², 10, 1000, 0.1); one of another faction is
  skipped with the option off and squared with it on. Result capped at 1.2.
- **Rescue Allies** (+0x112): the character finder `FUN_1408cd2d0` ([carry-loot](carry-loot.md#32-goals-and-their-target-finders)) requires the
  rescuer's faction only for a player-faction rescuer with the option off.
- **Feed Animals** (+0x114): off, the player characters' eating `FUN_1405d29c0` returns for an animal (vfunc 0x248), and desire `FUN_140597cb0`
  is 0 for an animal (its rows **Unknown**).
- **Share Food** (+0x115): in `FUN_1405d29c0` (the player characters' eating), with no food in its own inventory or backpack and (`Character::isWithThePlayer` `FUN_140856bc0`,
  floats +0x4bc ≤ 0, +0x4b8 < 2.25, meanings **Unknown**) a character takes food from the backpack of a squad member within 200 units (squared 40000).
- **Shoot first** (+0x11a): turret target choice `FUN_1404374d0`: enemies (sensed 4, 0x210) weigh `FUN_140435d70`(…, 1), unseen × 0.1, the current
  target × 1.5; with none, a player gunner with the option takes sensed (2, 0x210) characters whose faction's type (+0x34) is 0 or 8 or whose
  faction default (+0x60) is below -30; each is scored by `FUN_140435d70` with range factor k 0.6 (`DAT_141696c8c`), unseen x 0.1, current x 1.5, **Verified**, no extra weight.

## Unknowns

- Why `taskType` 289 and 290 exist without an enum name (they build `Task_Follow` and `Task_BashDoor`; row 290 of the TaskData table repeats the data of `BASH_GATE`).
- What the 37 named types without a TaskData row do when an AI_TASK record uses them (the list is in the TaskData registry section), and whether any behaviour of the 147 "goal" types (factory default `Task_Blank`) lives outside this factory.
- What the `*` marker on an entry (second flag byte) means, and which category registry a character plans with ([planner](#planner)).
- What the 18 StateType ids with no registration call and the two ids registered with an empty name (33 and 85) evaluate, and the semantics of most evaluators beyond their names.
