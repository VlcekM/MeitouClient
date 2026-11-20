# AI task tables

Task names/IDs: `src/Meitou.Data/Gameplay/Ai/TaskType.cs` (all 288 named rows checked against this research). IDs 289/290 are unnamed in FCS. See [AI](ai.md) for runtime semantics; lock, door, cage and lockpicking task behaviour (76-80, 107, 110, 133, 140, 155, 162, 166/167, 185/186, 195-201, 207, 285/286) is in [locks](locks.md). Tables retain factory mappings and all TaskData/state values; repeated enum names and condition names are represented by IDs.

## taskType to Task class

**Verified** (the Tasker factory `FUN_14032ebd0` switches on a byte table at `140330408` indexed by taskType - 1; the table was read
from the exe bytes with a probe, each case body was matched to the RTTI `Task_*` class its constructor installs, and the resulting
type-to-class list agrees for all 290 rows, including 147 default cases). 290 jump-table entries versus
289 names in `fcs_enums.def`: types 289 and 290 build `Task_Follow` and `Task_BashDoor` and have no enum name.
147 types fall through to `Task_Blank` (the factory's default case): no behaviour of their own from this factory.
The per-type record census is omitted; it can be regenerated from merged AI_TASK data.

Factory mappings grouped by identical class; each ID occurs exactly once:

- (goal; Task_Blank; 45 Bodyguard has no act of its own, a contract's following is goal 44's; Meitou weighs 45 as a fight goal): 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 24, 30, 32, 33, 34, 37, 38, 39, 41, 42, 43, 45, 46, 47, 48, 49, 50, 54, 55, 56, 57, 58, 59, 63, 66, 70, 84, 85, 86, 88, 89, 90, 91, 94, 101, 104, 105, 106, 109, 111, 112, 113, 114, 115, 117, 118, 120, 122, 125, 126, 131, 132, 135, 137, 138, 139, 142, 143, 144, 145, 148, 149, 151, 152, 153, 154, 155, 157, 158, 160, 167, 168, 169, 172, 173, 177, 178, 179, 183, 184, 187, 188, 189, 190, 191, 192, 193, 194, 195, 197, 202, 203, 204, 205, 206, 211, 212, 214, 217, 218, 222, 223, 230, 232, 233, 234, 237, 238, 239, 242, 243, 245, 248, 250, 251, 252, 254, 255, 259, 260, 265, 268, 270, 271, 273, 275, 276, 277, 279, 281, 282, 283, 288
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
- Task_FollowAndTalk: 164 (built on the Task_Follow constructor, then its vtable installed)
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
- Task_Loot_AI / Task_Pickup: 3 (Task_Loot_AI for a character target, hand type 1)
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
animation at speed 1, sets the character's state field (`+0x198`) to 4, adds the character's update time (`Character+0xc4`, [game-loop](game-loop.md#the-character-update-budget)) to the progress; at 1.0 it ends the task
(`FUN_1400522c0`) and hands the character's position, its science stat and its name to the Prospecting window ([ui-screens](ui-screens.md#prospecting)). There is no walking, no XP and no target: it runs where the character stands.

## TaskData registry

**Verified** (disassembly of the table builder `FUN_140319a60`, each registration call `FUN_14060ea00(type, memberFn, ...)` parsed,
then every row re-parsed from the decompile of the same function: 253 registration calls with 253 distinct type numbers, and type,
text, flag bits and the contents and order of lists A and B agree for all 253 rows; the decompile drops some trailing call arguments,
so the `!` on an effect and the `*` marker rest on the disassembly alone and are **Observed**). 251 of the 288 named types plus the
two unnamed types 289 and 290 are registered; 37 named types are not: 9, 10, 11, 18, 23, 27, 33, 46, 48, 49, 50, 54, 56, 63, 66, 70, 109, 111, 117,
120, 126, 145, 151, 154, 160, 178, 179, 191, 193, 205, 206, 229, 251, 252, 265, 267, 271.
Columns: display text (field +0x138), registration flag bits, condition list A (TaskData +0x4c; names prefixed `!` are required
false; `*` marks an entry whose second flag byte (+5) is set), condition list B (+0xa8, at most 11) and the
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
| 192 | Delivering slaves | 0x3 | - | !26, atDestinationTown (36) | - |
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
| 36 | Patrolling town | 0x21 | - | atDestinationTown (94), !47, !26, 60, !hasWeaponEquipped | - |
| 37 | Wandering town | 0x1 | - | !26, !hasWeaponEquipped | 31 |
| 118 | Shopping | 0x1 | - | 86 | (unnamed state 85) |
| 119 | Shopping | 0x1 | (unnamed state 85) | isAtLocation | - |
| 214 | Relaxing | 0x1 | - | 86 | 123 |
| 43 | Staying home | 0x61 | - | !26, !hasWeaponEquipped, 18 | 136 |
| 255 | Reigning | 0x41 | - | !26, !hasWeaponEquipped, 18 | 136 |
| 275 | Sitting around | 0x1 | - | !26, !47, !hasWeaponEquipped, 86 | 136 |
| 24 | Wandering | 0x21 | 86 | - | isAtLocation |
| 39 | Wandering trader | 0x1 | - | !hasWeaponEquipped, 32, atDestinationTown (36) | - |
| 55 | Recruiting | 0x1 | - | - | isAtLocation |
| 53 | Travelling | 0x61 | atDestinationTown (36) | !26, 32 | - |
| 130 | Travelling quickly | 0x61 | atDestinationTown (94) | !26, 32 | - |
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

Evaluators of the states the order step and job pass ask (`RowStates`; **Verified**, each id's registry thunk followed in the 1.0.68 exe, then the
function read): 0 and 135 AI slot 10 `FUN_140641540`, false; 1 AI slot 11 `FUN_1405a2450` (the walk's arrival, [planner](#planner)); 2 `FUN_140596b20`, a
building target's slot 97 false (a door's broken byte `+0x3b6`) and its complete byte `+0x160`; 7 `FUN_14099a5a0` (`CharacterFacts.TargetDown`); 47
`FUN_140598d20` (carrying, `+0x348`); 48 `FUN_14059a300` (carrying the target); 77, 81 and 100 `FUN_14060e5c0` (!47); 52 `FUN_1405a3ad0` (the door's
state 1 open, or 2 opening past 0.8, `DAT_141693ea8`); 54 `FUN_1405a00c0` (locked, not broken, level 1 or more); 56 `FUN_1405a3a10` (broken); 71
`FUN_140598d60` → `FUN_1400f6b70` (built `+0x164` below the target `+0x188`); 78 `FUN_1405a1cc0` (the target's `+0x2f8` 2, caged); 79 `FUN_140599570` (own
`+0x2f8` 2); 83 `FUN_140599540` (`+0x2f8` 1 with a bed); 99 `FUN_1405a14e0` (the carried character's dead byte `+0x5bc`); 114 `FUN_1405a1f00` (the worn
shackles locked); 123 and 136 false; 16 `FUN_14059de10` (the target's immediate-threat flag `+0x2b0`), 39 the same of itself.

| id | name |
|---|---|
| 0 | `none` |
| 1 | `isAtLocation` (`FUN_1405a2450`, **Verified**): of a point: false at the zero point; true when the mover is done (`FUN_14065edb0`: no path, or path state 1 and not moving), or within flat squared distance 144 (`DAT_1416ad830`) and height 20 (`DAT_141683188`); Meitou: evaluated for an order's point (no path or path flags, or that box) |
| 2 | `isBuildingComplete` |
| 3 | `isSubjectInInventory` (`FUN_14059ce30`, **Verified**): the item is in the inventory or held, or resolves to nothing; Meitou: the order keeps the item itself (`GroundPick`): still on the ground false, topped up into a stack (no units left) true, else whether it is in this character's inventory or mouth, so one another took is false |
| 4 | `hasWeapon` |
| 5 | `hasWeaponEquipped`: a weapon in hand that is not ranged (`FUN_140596a30`, **Verified**) |
| 6 | `isNoEnemiesInVicinity` |
| 7 | `isTargetDown` |
| 12 | `isAlliesFighting`: `FUN_14099a350` (**Verified**) true when any sensed ally (sense filter 1, 8) scores above 0 in the fight threat `FUN_14099a190` ([combat goals](#combat-goals)). Row 244 needs it false. Meitou: `AiSystem.AlliesFighting`, asked only by the food errand (`TownDutySystem`); the planner leaves the state false (rows 13, 15, 288, 234 have their own finders) |
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
| 26 | `IS_CROUCHING_OR_LYING` (`FUN_140599e90`, **Verified**): false when crippled (vfunc `0x360`); else true at posture 1, 2 or 3 (staying low, crawling, playing dead), or AI-sub-object byte `+0x49` set, or Character `+0x2f8` is 1 (bed), or Character byte `+0x278` set (the two bytes unidentified, not modelled). Meitou: `CharacterFacts.CrouchingOrLying`, shared by the planner and the slave delivery (crawling and incapacitated stand in for crippled; a knocked-out character is posture 4, not counted) |
| 27 | `isMovementAllowed` ([range](#perma-jobs)) |
| 28 | `isNearTo` |
| 29 | `isGeneralAggressionLevel` |
| 31 | `atNode` |
| 32 | `hasDestinationTown` |
| 33 | (empty name string) |
| 36 | `atDestinationTown` |
| 37 | `isReadyForAction` (`FUN_140998550`, **Verified** decompile): a living character that can use an arm (medical `+0x165` or `+0x166`) with a melee weapon in hand (state 5), and either upright (prone state vfunc `0x368` 0 and state 26 false) or crippled (vfunc `0x360`). State 26 is true in a bed, so a character lying in one is not ready; the planner reaches 37 through row 59 Get ready (`!26`) <- 28 Standing (`!83`) <- 116 / 208 / 257 (`45`) |
| 38 | `hasOneWorkingArm` |
| 39 | `isAggressionTowardsMe`: the state-16 evaluator `FUN_14059de10` asked of the character itself, i.e. its byte `+0x2b0` (the immediate-threat flag of 158, [combat](combat.md)); the thunk was followed in the exe, an override branch on a global lookup is **Unknown** (**Observed**) |
| 40 | `isWithin10Meters`: as 41 with 100 (thunk `FUN_1405a32b0`, `DAT_141683250`; **Verified**, exe bytes). Meitou `AiSystem.Within10Meters` |
| 41 | `isWithin50Meters` (thunk `FUN_1405a3290` -> `FUN_1405a3080`, **Verified**, exe bytes): the ground-plane distance to the target strictly under 500 (`DAT_14168dcf4`; world units are decimetres, so the names are in metres); a position target (type 0xb) also needs a height difference under 50 (`DAT_141683170`); a type 0xd target uses 3000 instead (`DAT_1416ce4b0`). Meitou `AiSystem.Within50Meters` |
| 42 | `targetStandingStill` |
| 43 | `iAmDown` |
| 45 | `canGetUp` (`FUN_140598c20`, **Verified** decompile): the medical unconscious (`+0x161`) and dead (`+0x164`) bytes clear, the starvation knock-out test `FUN_140644320` false (hunger under 0.1, or under a toughness-dependent level once per refractory period) and not carried (`+0x3d4`). Meitou holds it true (`PlanContext.Holds`) |
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
| 59 | `DESTINATION_IS_ACCESSIBLE`: `FUN_140597030` with default false; 168 the same with default true (**Verified**). The building is the target's (a character's indoor building, vfunc 0x1d8), else with no target the mover's destination building (`_destinationInsideBuilding`, `FUN_140793230`); none, or the one the walker is in: true; a door of that building (`imADoor`, its `DoorStuff` parent the building): true; an animal it keeps out (AI slot 101 false and no `allow animals`, `FUN_140547500`): the default; else `hasAnOpenDoor`. Meitou: `PlanContext.Accessible` in the main planner, the door chain (85, 72, 78, 81; 285, 76 where restricted rows are open) folded in by the repertoire and `LockSystem.WayThrough`; [locks](locks.md#locked-doors-on-the-way) |
| 60 | `LOCATION_IS_ACCESSIBLE` |
| 62 | `machineHasEmptyInputs` (`FUN_1405974e0`, **Verified**): a building target has an empty input, [Blocked output](buildings-production.md#blocked-output) |
| 63 | `isMachineOutputFull` (`FUN_1405a2ba0`, **Verified**): building slot 175, status `+0x468` == 1 for the production family; Meitou: evaluated for a machine target |
| 64 | `isMachineJammed` (`FUN_1405a2e60`, **Verified**): 62 or 63 of a building target (63 alone for another hand); asked only by rows 91 and 90 (no data starts them); Meitou: evaluated for a machine target |
| 65 | `isAnyMachineJammed` (`FUN_1405a2f00`, **Verified**): the 64 test over a hand list of the AI ; not wired in Meitou |
| 66 | `haveSomeResourcesFrom` |
| 67 | `haveSomeResourcesFor` |
| 68 | `HAVE_SOME_LOOT_FROM` (evaluator `FUN_140597530`, state 66's, **Verified** `FUN_140610250`: [state evaluators](buildings-production.md)) |
| 69 | `isRouteTraceBlocked` |
| 70 | `isObjectMine` |
| 71 | `isBuildingDamaged` |
| 72 | `isFullyRested` (**Verified**, evaluator `FUN_1405a0610` from the registry's id-0x48 call): of the character target, else the character itself (`FUN_140643cd0`); false for one whose faction is not the player's (faction `+0x250`), so an NPC's rows 98, 100, 175 and 261 are never done and their goals run in their hours; for the player's faction, true when the MedicalSystem's (Character `+0x458`) float `+0xb0` is above 0.9 (the upper half of `DAT_14168a258`). The field (**Verified**, the medical update `FUN_140644e60`, disassembled at `0x140644f9b`): starting at 100, for each part with no replacement limb (`+0x28` null) and the flag `+0x7c` of its vfunc-0xd0 object clear, a part whose (h - s) x 100 / M (M = base x mult x scale - wear, [character-stats](character-stats.md)) is below the running value stores its raw h - s (the exe compares a percentage and stores the raw value); the result x 0.01, snapped to 1 above 0.999. Bandage and rig are not counted. Meitou: `MedicalState.RestedLevel`, `RowStates.FullyRested` |
| 73 | `machineHasFreeOperatorSlot` |
| 74 | `isEnemyForceDefeated` |
| 76 | `isMessageDelivered` (`FUN_140792190`, **Verified**): no Dialogue object (Character +0x280), else `FUN_140673830(dialogue, 2)`: the Dialogue's per-event map (+0xf8) holds event 2, the status town stored there equals the character's current one (Character vfunc 0x78: status +0x88; not compared while it has none), its conversation has ended (+0x148) and it has no chained speaker (+0x2c0). Event 2's entry is written as an ANNOUNCEMENT conversation ends (`FUN_140674830`) and when the player's talk takes over one still running (`FUN_140684320`); nothing clears it, and no save code reads or writes it. Meitou: `DialogueSystem.MessageDelivered` |
| 77 | `isCarriedDudeInBed` |
| 78 | `TARGET_IS_IMPRISONED` |
| 79 | `I_AM_IMPRISONED` |
| 80 | `isCarryingArmOK` (evaluator `1405c7930`, **Verified**): the character's medical byte `+0x166`, the left arm works; failure line "My arm is messed up too badly to lift anything." Meitou: `PlanContext.Holds` (`CarrySystem.LeftArmUsable`) |
| 81 | `CARRIED_DUDE_NOW_IN_CAGE` |
| 82 | `gotInventoryRoomForResources`: `FUN_14059bd80` (**Verified**) true when the looker's inventory has room for the target's item type, else its worn backpack has, else the animal-carry path (`FUN_14042ee50`); its failure line is "I don't have space to pick that up!". Row 244 needs it to start. Meitou: `Inventory.HasRoomFor`, tested before an NPC food run starts (`TownDutySystem.FoodFits`) and, on the item the take picks, before a player-side one (`TownDutySystem.TakeFits`) |
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
| 93 | `BUILDING_HAS_POWER` (evaluator `FUN_140596ab0`, **Verified**): false unless the target is an object (kind 0) found as a building; true for a building class (+0x198, slot 0x358) below 2; else `FUN_140299370` ([power](buildings-production.md#power): the town's generators, or its batteries in battery mode; a machine drawing nothing passes), so not the granted share. Failure line "It needs electricity."; a player order's first unmet condition gives its line (`FUN_140610020` via `FUN_14051a0a0`, said by Character vfunc 0x2d8 from `FUN_140510920`). Rows 87, 221, 146 need it. Meitou: `ProductionSystem.Powered` (operate and turret orders refused with the line; jobs skipped); `RowStates` evaluates it for an order's site (`MachinePowered`), so a running operate or turret order whose power goes ends and leaves the queue without a line |
| 94 | `atDestinationTown` |
| 95 | `isAtHomeTown` (`FUN_140997fe0`, **Verified**): my status town `+0x88` is set and its vfunc `0x2c0` (Town: `FUN_140952060`) accepts my faction: the town's own faction, or one of its set `+0xe8`, which the housing `FUN_14057f820` fills with the faction of every squad housed in the town. Meitou: `ArrestSystem.AtHomeTown` |
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
| 107 | `I_AM_INSIDE_TARGET_BUILDING` (`FUN_14059a4b0`, **Verified**): false outdoors; else the building the character stands in is the target's (an object target, a door or part by its building), with no object target its squad's home (`FUN_1407956a0` `+0x38`) |
| 108 | `strangers in house` (`FUN_14060e5e0`, **Verified**): the stranger finder above 0, see [stranger states](#stranger-states); not wired in Meitou |
| 109 | `isDialogDelivered` |
| 110 | `isDialogDelivered` |
| 111 | `AT_LOCATION_BUILDING_DOOR_INSIDE` |
| 112 | `DOOR_IS_LOCKED_AND_IM_INSIDE` |
| 113 | `targetAtATown` (`FUN_140508ab0`, **Verified**): for a character target (hand type 1) the `+0x88` of its `+0x1a0` object is set (read as its status town, **Observed**), whatever town; a type 0x5b target resolves another way ; any other target: false |
| 114 | `isWearingShackles_locked` |
| 115 | `IS_CARRYING_TARGET_HEALTHY` |
| 116 | `TARGET_IS_MY_SLAVE` |
| 117 | `TARGET_HAS_BEEN_LOOTED` |
| 118 | `TARGET_IS_UNARMED` |
| 120 | `TARGET_LOOKS_LIKE_A_SLAVE` |
| 121 | `CARRYING_EXCESS_LOOT` (`FUN_14059b7b0`, **Verified**): true when the AI's inventory (`AI+0x308`) holds an item passing the stolen predicate `FUN_14079e550` ([carry-loot](carry-loot.md#stolen-ownership)), else when the worn backpack's inventory (`FUN_1405cb320`, the CONTAINER in the back slot) does. The collector `FUN_140748d40` passes a constant strict = 1 to `FUN_140748bc0`, so the second call's 0 is not forwarded (**Observed**, decompiled signature); the collector also lists nested items. The only row naming it is 184 |
| 122 | `hasTools` |
| 123 | `IS_WORKING`: 123 and 136 share the evaluator thunk `0x14002fb1c`, which jumps to `0x1405974d0`, whose bytes are `32 c0 c3` (return false; **Verified**, installed `kenshi_x64.exe`). So 87's and 221's list A never holds and 189 / 190 (effects 123 / 136) always plan their row. Meitou: false |
| 124 | `isTargetEscapee` |
| 125 | `TARGET_IS_CONSCIOUS` (`FUN_14099a480`, **Verified**): a character target not down (carried, ragdolled), unconscious or dead; a non-character target true, none false; no row provides it |
| 126 | `AT_LOCATION_BUILDING_DOOR_OUTSIDE` |
| 127 | `DOOR_IS_LOCKED_AND_IM_OUTSIDE` |
| 128 | `isAlly` |
| 129 | `TARGET_IS_ARRESTED` |
| 130 | `ALARMS_IN_THE_VICINITY` |
| 131 | `atPackageTarget` (`FUN_140506d30`, **Verified**; row 215's list A): true without a platoon (AI `+0x10`). The target is the platoon's package target (`FUN_140796120`): the override package's (`+0x210`, the campaign phase package; `Package_WarBase` `FUN_14028fb10`: its mission target `+0x30`), else the campaign's (war manager vfunc 0x88), else the running package's vtable +0x8 (base `FUN_1402859e0`: the campaign's, else the mission target `+0x30`; `Package_Refugee` `FUN_140288dc0`: a broken one's mission target, else its mission town `+0x10`), kind 1 for a character hand, else 0. Kind 0: unresolved true; a town with gates (vfunc 0x2f0, `hasGates`) within 250 (squared 62500, `DAT_1416e3460`) of its waypoint point (vfunc 0x38) flat; else within 0.4 (`DAT_141696c90`) of its radius (`FUN_140926d50`, false for an object of type 8 LOCATION). Kind 1: unresolved, within 150 (22500) of the hand's point (the package hands leave it at the origin); state 90 false: within 1.1 (`DAT_14169bb44`) of the target's town radius; the same indoor location: true; `FUN_140597030(target, false)` true (outdoors included): true; else within 100 (10000, `DAT_14168dae4`) of A + (A − B) × 3 (`DAT_14168a254`) of the target's building's first door. Meitou: `PlanContext.AtPackageTarget`, `AiSystem.PackageTarget` (campaign target town; a broken refugee's join squad's leader, else its mission town; the mission target, else a town contract's mission town), state 90 held true as everywhere |
| 132 | `haveroboAidKit` |
| 133 | `needsFirstAid_robot` |
| 134 | `isChained` |
| 135 | `TARGET_IS_EATEN` |
| 136 | `IS_WORKING_PRETEND`: as 123, always false |
| 137 | `INTRUDER_IS_OUTSIDE` (`FUN_140598ef0`, **Verified**): the intruder is the target, else the carried character; true with none, for an ally, or when it is outdoors (location type 0xb) and not carried by the asker (224's list A) |
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
| 157 | `hasWeaponEquipped`: a weapon of either kind in hand, 156 or 5 (`FUN_14060e540`, **Verified**); only 7 Sheathing weapon gives !157 |
| 158 | `iAmBeingMeleeAttacked immediate` |
| 159 | `iAmBeingMeleeAttacked general` |
| 160 | `isWithinMyBowRange` |
| 161 | `hasTwoWorkingArm` |
| 162 | `iAmProne` |
| 163 | `isBuildingDestroyed` |
| 164 | `INTRUDER_IS_OUTSIDE_GATES` (`FUN_140598fa0`, **Verified**): the intruder as 137's; true with none, for an ally or one in no town; in a town only when its `amInsideTownWalls` < 1 and the asker no longer carries it (274's list A) |
| 165 | `crowdLimit8` |
| 166 | `iHaveSufficientLockSkill` |
| 167 | `iHaveSufficientLockSkillForShackles` |
| 168 | `DESTINATION_IS_ACCESSIBLE_ANIMAL`: 59 with default true |
| 169 | `isMachineInputInvalid` |

## Stranger states

**Verified** (decompile: registrations `FUN_140610250`, evaluators `FUN_14060e5e0` / `FUN_14060e640`, finder `FUN_1408ced70`, public test `FUN_140599680`). 108 `strangers in house` is true when the finder returns above 0
(it returns 1.0 or 0); 141 `strangers in house P` is false first when the target's building is public, else the same finder. Goals 155, 197, 158, 283 and 239 read 108, goal 153 Going to bed needs `!141`
(the "153" in goal 132's list A is state 153, not this goal).
- **Public test** (`FUN_140599680`, **Verified**): the target as a building, replaced by the building its vfunc `0x1d8` names when that is not empty (**Observed**: a bed's house), else the blackboard home; none of either counts as public;
  then Building vfunc `0x290` (slot 82, `FUN_140548750`), which reads the building's flag `+0xc8`, the `public day` flag ([locks](locks.md)), of its parent when it has one. It also returns not public when the building's `+0xd8` is 1 or 0x22 and the "house locked" flag ([locks](locks.md#doors)) of the squad of a character it
  looks up is set (**Verified**: `FUN_140791ba0` then `+0xe8`; not modelled).
- **Finder**: 0 with no home. Else the first of two scans:
  - the resident's sensed characters tagged SENSE_NEUTRAL (bit 1) and none of bits 3 KO, 4 dead (`+0x5bc`, `FUN_1406215b0`), 7 NPC class 1 LAW_ENFORCEMENT or 2 MILITARY (status `+0xdc` of `+0x1a0`, Character vfunc 14 `FUN_1405e1ed0`; **Verified**), 9 imprisoned
    (mask 0x298; [tags](perception.md#sense-tags-who-is-an-enemy)) that stand in the home building, are up (Character vfunc 6, `isDown` false) and are not a chained slave of the resident's faction (`FUN_14032e2a0`: chained, owner faction the resident's);
  - only for a resident whose faction is not the player's (faction `+0x250` null): the player's characters in the home that are up, not chained slaves of the resident's faction, `+0x2f8` not 2 (not imprisoned) and either not sneaking (`FUN_1405c9750`: stealth mode and not in combat mode) or
    sensed (`FUN_140855960`, which for a sneaker passes when the resident has any sense record of it) - whatever their tag or class, allies too.
- **Meitou** (`AiSystem.StrangersInHouseP` for 141, `AiSystem.StrangersInHouse` for 108 = the same finder without the public test, so 155, 197, 158, 283 and 239 plan only with no stranger in the home): the public test reads the home (the player's Open to public toggle, else the residents' template `public day`); the planner gives a finder row's list B no target, so the tested building is always the home. "Sensed" is standing in the home
  building (a sneaker also needs `SightMap.Sees`, a live test for the record; engine reading); the tag is the faction relation (neutral: not an ally, not an enemy); up is not incapacitated or carried. The lock-up round still tests strangers itself (`LockSystem.StrangerInside`, any non-squad character in the home).

## Arrest and prison rows

Rules: [economy 11.5, 11.7](economy.md#115-bail-and-sentence). **Observed** (evaluators and TaskData hooks resolved from the exe's thunk bytes):
**Arrest chain** (**Verified**, the rows above and the state evaluators `FUN_1405a2290` / `FUN_14059a380`): goal 204's effect 129 TARGET_IS_ARRESTED is true when the target is imprisoned (Character `+0x2f8` = 2), or carried by a character of another faction. Its provider 203 Arresting (Task_Blank, no act; needs 7 target down, 118 TARGET_IS_UNARMED, 48 carrying the target) leaves three unmet needs, so the plan is a fight (row 4 / 5 / 61, state 7), then 181 Stripping weapons (provides 118; needs 38, 7, at location), then 68 Lifting (provides 48; needs 80, 27, !26, at location, 7): the only provider of 48, since 213, 240, 225 and 246 are flagged not-a-provider (`+0x40`, which the provider maps' builder `FUN_14060fe30` skips, **Verified**). 240 Capture Target (needs 95 at home town, 113 target at a town, 80, 27, !26, 7, at location; snatch byte set, [carry-loot](carry-loot.md)) is the order DA 29 gives instead ([economy 11.7](economy.md#117-arrest)). 118 holds when the target holds no weapon in hand and its category-0 inventory sections (`hip`, `back`) are all empty, locked items included; with it true at planning time 181 is left out. Armour and clothing stay on an arrested or caged character: the only whole-inventory strip (`FUN_14035aa20`) is `Task_ProcessNewSlave`'s (its vtable slot 2, no other caller; **Verified**, decompile and `vtables.txt`), so a uniform keeps its disguise level in a cage. Once carried by a stranger 129 holds and 204 is done: caging is the separate goal 112 / 281 of the carrier (desire below), not a step of the arrest. The orders 240 and 213 need no 118 (their list B above), so an ordered arrest lifts the target armed: no 181. Meitou: `ArrestSystem` runs the orders DA 29 and DA 10 / 30 / 40 give as the phases of an arrest (Fight = 7 by row 4, Lift = the order's 240 or 213, so a body another carries is snatched; Walk = the queued order 112), each task with the order's provenance (the fine is the dialogue's, not the chain's). Arrests are runtime state only: saves hold none, so no saved phase needs mapping. Goal 204 is an [engine goal](../simulation.md#arrest-and-jail) (`ArrestGoals`) that the chooser plans through this chain: its act-out is the plan's front work (the fight 4 / 262, 181 through the same `ArrestSystem.Strip`, or 68), and each ends the plan, as 203 does not act; a locked weapon left on the target keeps 118 false, so 181 is planned again. The bounty hunters' chase of a recognised bounty (`Hunt`) is not goal 204: it answers the event-44 dialogue with `DA_ATTACK_CHASE_FOREVER` ([economy 11.8](economy.md#118-bounty-spotting-and-bounty-hunters)). Town needs of DA 29's capture (**Observed**, from the rows and the [planner](#planner), not checked in the running game): order 240 is the front of the order queue and is planned again each update ([Precedence](#precedence)); its list B starts with 95 and 113, so a guard who stands outside a home town plans 127 Retreating fast (the only provider of 95) and one whose target stands in no town plans nothing (113 has no provider) and goes on with his package, the order staying queued. Meitou (`ArrestSystem` as the orders' owner, `IOrderOwner.CanRun` / `StillHolds`, `RunHome`; needs town data): until the guard carries the target, 113 failing blocks the order (it waits queued, its task ended and the fighters the target does not fight back called off), 95 failing makes the guard leave the fight the same way and run to his squad's home under the order. Hand-overs and goal 204 have no town needs.

| Id | Semantics |
| --- | --- |
| state 129 `TARGET_IS_ARRESTED` (`FUN_14059a380`) | target `+0x2f8 == 2` (imprisoned), or carried (`+0x3d4`) by a character of another faction; a non-character target: true while I carry it |
| state 78 `TARGET_IS_IMPRISONED` (`FUN_1405a1cc0`) | the target character's `+0x2f8 == 2` |
| goal 203 Arresting | desire = AI slot 32 (`FUN_140598a40`): 0 when status `+0x88` is null and the character's ownership town is set, else 1 |
| goal 204 Bounty hunting ("Arrest bounties") | desire = AI slot 33: the free-cage finder's result; target finder `FUN_14099bfc0` (a perceived bounty, the squad's short-term tag 7 or an enemy; uncarried, not imprisoned) |
| goals 112 / 281 Find and put in cage | desire `AI::scorePutCarriedDudeInCage` `FUN_1405a1140` (**Verified**): 0 unless the character carries (flag `+0x348`) a resolvable one; then `FUN_1405a0f10`, in this order: the status town `+0x88` unset 0.1 (`DAT_14167fad0`); the mission town of the blackboard's running package (`FUN_140507070` `+0x48` is the running package, its hand `+0x8`, compared by `FUN_1405944e0`) set and not that town 0 (the carrier's own package, not the carried body's town; Meitou `SquadTown.MissionTown`); an animal (vfunc `0x248`) 0; a law enforcer (`FUN_140599600`) carrying a character with no bounty with the AI's faction (`FUN_140854080` below 1) and a non-zero slave state (`FUN_1405c8aa0`) 1.4 (`DAT_14169bb48`); the carrier regarding it as friendly (vfunc `0x3f0`, 1) 0; dead (`+0x5bc`) 0.5 (`DAT_14167e8d8`); alive 2.0 (`DAT_141682ad0`). Meitou: `TownDutySystem.CageDesire` is the goal's desire in the one chooser (`TownGoals`, [town duties](../simulation.md#town-duties)), times the cage finder's factor (`FUN_1408d0d30` gives 1.0, `DAT_14167c308`, with a free cage, else 0). Finder `AI::findOwnedFreeCage` (a free BF_CAGE). Both are Task_Blank rows with list A 154 and effect 81 (**Verified**, `FUN_140319a60` registration: only 112 gets the `!99` list B, `isCarriedDudeDead`; 281 has none), so a living carried body qualifies for either goal and a dead one only for 281. Data: 281 in the two "addon- law enforcement" and two fish resident packages |
| goals 137 / 138 Corpse disposal / Find corpses | 137: desire AI slot 43 `FUN_1406415a0` = 1.0 (`DAT_14167c308`); finder `FUN_1408d1090` (1.0 with a disposal, else 0); list B 99, effect 100 (**Verified**, registration in `FUN_140319a60`, stubs disassembled). 138: desire `FUN_140599480` = 0 on Hold (`+0x450` `+0x12b`) or while carrying (`+0x348`), else 1.0; finder `FUN_1408cfb00` (1.0 with a corpse); list A 99, effect 48 (**Verified**). So 138 lifts with no disposal in reach and 137 plans 138 when nothing is carried. Data: 137 in the cannibal packages, the law-enforcement add-ons, `undertakers`, `Cage Warden`, slaver and raid-retreat packages; 138 in the cannibal packages, `Hospital`, `undertakers`, `slave worker day guard`. Disposals (**Verified**, base-game survey of the built town buildings, 2026-11-16): placed Cooking Pits in the cannibal villages and capital, Corpse Furnaces in Mongrel (8), the Skinhouses, Rebirth, Okran's Shield, Black Scratch, Sho-Battai, Reaver Camps; interior Skeleton Shredders in the labs (Suspicious Lab, Island Lab, Reprogramming Workshop, Narko's Trap, Tower of Abuse); none in Heft. Meitou: `TownDutySystem` (NPCs), ranked with the cage and throw-out goals |
| goals 224 / 274 Intruder out / Throwing out | desire `FUN_1405a13a0` (**Verified**: both rows' registration stub `LAB_140037326` is a lone `jmp` to it, read in the exe's disassembly): 0 unless the character carries (`+0x348`) a resolvable character; 0 when it regards it as friendly (vfunc `0x3f0`); else 2.0 (`DAT_141682ad0`). No town, animal or death test, so a carried animal (cage desire 0) is thrown out. 274 has no effect, so its score is not multiplied by a finder (`FUN_14060fd80`). Meitou: `TownDutySystem.ThrowOutDesire`, the goals' desire in the one chooser (`TownGoals`) |
| goal 211 Freeing prisoner | finder `FUN_1408cf3c0` (`BuildingFinder_Cages`): an occupied usable cage whose occupant's sentence is served (`FUN_1405c9f00`); the target is the occupant ([locks](locks.md#wardens-goals)) |
| goal 276 Liberating prisoners | finder `FUN_1408cf140` (`BuildingFinder_CagesToLiberate`): a cage of a faction that fails the relation test `FUN_1406b2630` (read as allied) holding an occupant that is not an enemy |
| goal 42 "Getting a weapon" / 8 FIND_WEAPON / 6, 264 equip (**Verified**, decompile) | 42: B 38 hasOneWorkingArm, 4 hasWeapon (both required true in the registration `FUN_140319a60`: `FUN_14060e940(row, 0x26, 1)` and `(row, 4, 1)`, as `TaskDataTable`), desire 2.0 (`FUN_140641610` = `DAT_141682ad0`) (`AI::scoreDontWantToBeUnarmed`); plans 8 (finder `AI::findWeapon` `FUN_14099b1d0`, effect 3, acted out by row 3 "Collecting item"; row 8's desire is AI slot 19, 1.0, and the finder's factor 1.0, `DAT_14167c308`, with a find, else 0; 42's row neither acts nor has an effect, so with a weapon the scorer drops it, `FUN_14050df10`). Finder, first pass with a match, each within 100: nearest free WEAPON world item it has room for (in a town only one in its location owned by nobody or an enemy); else the first sensed dead body with a weapon (not carried, not an animal, no squad mate on it); else the first KO/crawling non-ally, player-side only with Bandits loot, not loved. `Task_EquipBestWeapon` (finishes on its first update): sections `back` then `hip`, first item whose ranged flag matches (1 for 264) and usable; an NPC whose faction is not a player faction (`FUN_1405c6a40`->`+0x250` null; **Verified**, `FUN_140346c30`) also the first WEAPON of the whole inventory (every section) in inventory order with the matching ranged flag ; otherwise the hand is emptied. The item goes to Character vfunc `0x3d8`: `CharacterHuman` `FUN_1405dbf80` sets the held-weapon pointer (`+0x6d8`) to it, sets item byte `+0x129`  and passes it to the fighting component `FUN_140897f30` (**Observed**: reads the held weapon for the attack stats; no inventory move seen). `CharacterAnimal`'s slot is `FUN_1405e58d0` (copies `+0x6d0` to `+0x708`). Meitou (`AiSystem.DrawFromInventory`): the hand weapon is derived from the back and hip sections (`Fighter.Weapon`), with no held-item pointer, so the item drawn from elsewhere in the inventory (WEAPON for 6, CROSSBOW for 264, the worn backpack's contents after the items; the usable test counts as true) is seated in the first weapon section it fits and the fighter rebuilt; with both sections full the hand stays empty. Player-faction characters never draw from the inventory. Data: 42 in 19 packages |
| goals 202 Escaping / 256 desire (**Verified**, decompile) | 202: desire 2.0, B !134 isChained, !79 imprisoned, restricted, no finder or acts: its work is the providers, 219 Removing Shackles (A !134, B !114 from 201 / 185 / 186) and the cage rows; never done (list A `none`). 256 desire 1 while imprisoned, not chained, in a building with a lock that is not locked. Rows' desires (**Verified**, the registration's stubs): 219 2.0, 185 1.0, 207 1.0, 133 0.5, 201 0.1; 54 of a character target is its cage's lock (`FUN_1405a00c0`). Data: 202 in the "Escaping slave" packages, 207 in those and "Escape prison and attack target", 256 in "captured NPC" (the unload split's), 133 in none; no campaign package names them. Meitou: `EscapeGoals` |
| goal 212 Confiscating goods (**Verified**, decompile) | desire 1, finder `AI::findPrisonerWithContraband` `FUN_1408cf6a0`: cages of the current town (`BuildingFinder_Cages` (1, 0x100)) in list order, an occupant no squad mate works on, first with a stolen equipped armour piece it has room for, else any stolen item: the first cage wins. Effect 3, acted out by row 3 (`Task_Loot_AI`), whose law-enforcement branch takes one unit of each stolen item ([confiscation](carry-loot.md#33-npc-looting)) |
| goals 131 Looting food / 243 Getting food (**Verified**, decompile) | finder `AI::findSomethingToLootFoodFrom` `FUN_1408cb650` over the blackboard's target town, else the current one, else the zone grid: buildings with a non-empty inventory, owned by the looter's faction or the looter not the player's, not inside another building for an animal, not path-impossible, not a shop of the looter's faction; without function stuff when it holds food the looter may eat, with function stuff when (another owner or no production building) and it holds function-3 food (an animal also 15). Nearest wins. The blackboard's target town is the field `+0xf0` (saved as `target town`, `FUN_14026c2d0`); Meitou reads its `Blackboard.Destination` for it (**Observed**: the only other town hand written, `currenttown` `+0x138`, is the current town), else the town the member stands in, else the 3 x 3 zone cells round it. Neither needs an own town (243's desire, AI slot 26, passes through to slot 24; the finder has no town-membership test, so an NPC looter accepts any owner's building in the area). 131: effect 68 by 284 `Task_EmptyMachine` (moves output items while there is room; nothing for a building without function stuff); 243: effect 149 by 244 `Task_grabOneFood`, whose list B needs state 82 (room) and not 12 (an ally fighting), so a full inventory starts no run (**Observed** in the original 1.0.68, 2026-11-18, one save with its control: a player character at hunger 2.3 (230 on the panel), stomach empty, no bag and no grid cell for either of the store's foods never took Getting food in 69 game minutes and put nothing down; with one 3 x 3 item dropped it took Getting food within 6 game minutes). Meitou: a player-side run tests it on the item the take picks (`TownDutySystem.TakeFits`) |
| goal 184 Ditching loot / "Put loot in storage" (finder `FUN_14059b040`, registration **Verified**; the readings below **Observed**) | Listed by 11 packages of the base data (Cage Warden, Storage Guard, Slaver Storage Guard, the law-enforcement add-ons, slave catching, ...). List A `!121`, desire AI slot 47 `FUN_1406415d0` = 2.0 (`DAT_141682ad0`, **Verified** by the stub's bytes), effect `!62` (row 93 Filling machine puts into the target). Finder: none unless the character carries a state-121 item and has a current town (status `+0x88`). Candidates are storage buildings, functions 2 BF_RESOURCE_STORAGE and 13 BF_GENERAL_STORAGE (the nearest-allies finder's mask `0x2004`; the town lists `0xd` and `2`), kept when `StorageBuilding` slot `0x4a0` (`FUN_1402988c0`) takes one of the stolen items (the record's `itemtype limit` equals the item type and, with a `limit inventory` list, the item is in it; plus an inventory room test). A character with a home town (Ownerships `+0x38`) standing in a town of type 2 reads the *home* town's lists with no owner test; any other reads the current town through `BuildingFinderNearestAllies`, whose building test `FUN_1408ca5d0` passes the looker's own faction's buildings and, through `canIUseThisBuilding` (`FUN_140621230`) on the town, the town's faction's (a storage has an inventory, so no ally passes). Nearest by squared 3D distance wins, within the orders-movement bound (`AI+0x2bc`: 2 ROAM_FAR 4.246733e7, **Verified** `DAT_1416efdcc`; 1 STAY_CLOSE `(meiDistance + 200 + tolerance)^2`; 0 HOLD_GROUND `(meiDistance + 40 + tolerance)^2`; else `1000^2`). Factor 1 with a target, else 0. Row 93 `Task_FillMachine` (`FUN_140344510`, **Verified**) puts the carried items into a storage target (see [Ditching](#ditching)) |
| 133 / 207, 256 `Task_GetOutOfCage` | constructor flag "escape" 0 for 133, 1 for 207 and 256 ([locks](locks.md#cages-and-beds)) |

## Prisoner fate and slave rows

**Observed** (TaskData registrations in `FUN_140319a60`: desire at `+0x108`, finder at `+0x160`; the desire thunks resolve to `AI` vtable slots; task classes from their vtables).
Meitou: [simulation, captives](../simulation.md#captives).

| Goal | Desire | Finder / rule |
| --- | --- | --- |
| 135 Kill random prisoner, 282 Dinner time | AI slot 20 `FUN_14032e9b0` (**Verified**): 0 with no town in the status (`+0x88`); otherwise the town's timer entry is created first when absent (before any cage or weapon test), then 1 when the town's kill timer has passed, or every cage it knows is held by a live character (`FUN_1405a1ab0`, the CAGES_ARE_ALL_FULL test; false with no cage: it gathers every function-8 building (`FUN_1408cbea0` mask 0x100, `FUN_140a00690`) within 1.1 x the status town's radius, else of the nearest town in range, plus the home town, keeps those `canIUseThisBuilding` passes for the character's ownerships (`FUN_140621230`), and is false for no cage, an empty cage or a dead occupant, else true; **Verified**; Meitou `CaptiveSystem.CagesFull` takes the cages of the status town and the squad's home town that `FixtureRegistry.CanUse` passes); else 0. 135 has no list A or B and its desire no weapon test: the weapon is 134 Kill prisoner's B list (`hasWeaponEquipped`), a plan step the equip row 6 (`Task_EquipBestWeapon`, `FUN_140346c30`: back, hip, then for a non-player any WEAPON item of the inventory) satisfies, so an unarmed captor plans an equip first and one with nothing to draw has no plan (reading list A/B as plan preconditions is an inference). A town not yet in the timer map gets now + uniform[1, 15) h (`FUN_14034a810`, `FUN_1409b1c80(1, 15)`), a global map keyed by the town | `FUN_1408d0d90` (also 139; one memory shared by 135, 139, 282 and the eat goals, the AI memory hand `+0x88`, **Verified**): a remembered cage whose building still has an occupant (dead or alive) is returned at once with the plain factor, with no weighting, no robot test and no event 10; if it has lost its occupant the memory is cleared and the search runs; a fresh choice is written to the memory before the robot test (so a robot's cage, once chosen, is returned by the next ask without the test). Every Character has a dialogue state (`+0x280`; other call sites use it unchecked), so the `!= 0` test before event 10 never fails. Meitou: `AiMind.FateCage`, `CaptiveSystem.FateTarget` (engine choice: a dead occupant counts as none). Search: the cage finder `FUN_1408d0770(ai, occupied, 8)`: function-8 buildings, weight clamp(1 - d^2/4608^2, 1e-5, 1), x100 same faction (another faction's only when `canIUseThisBuilding`, `FUN_140621230`, passes: [locks](locks.md#beds)), x100 in the character's home, x1e-5 for a law enforcer when the cage's parent building is not public; the best weight wins (a robot's cage competes: the finder has no robot test, only the wrapper's test below on the winner; Meitou `CaptiveSystem.FateTarget`). Its candidates are the economy 11.7 gatherer's: the status town (the same `+0x88` the kill rate reads), else the faction's nearest within 3953, the buildings within 1.1 x its radius, plus the home's town ([economy](economy.md#117-arrest), **Verified** `FUN_1408cbea0`, `FUN_140a00690`). No target when the occupant's RACE `robot` (`+0x7c`) is set; else the occupant fires event 10 MARKED_FOR_DEATH at the chooser |
| 139 Eat random prisoner | slot 19 (1) | as 135 |
| 183 Watching slaves | slot 61 `FUN_140597b20` (body **Verified**; the three rows' slot **Observed**): with a target (handle type not 0xb), slot 59 `FUN_14059e1a0`; with none, 1 while the worker does not carry (`+0x348`) and its posture (vfunc `0x368`, Character `+0xe0`) is 0, else 0. The target read is the candidate's `+0x10` (`FUN_14060fd80` passes it, **Verified**), which scoring does not reset ([commitment](#desire-and-commitment)) | `FUN_1408cd630` (**Verified**): none while itself held; first the worker's sensed list with include mask 0x60 (sense-tag bits 5 / 6, slave state 1 / 2) and exclude 0x110 (bit 4 dead, set by the sense update for a looker not of the player's faction), the first entry in slave state whose `FUN_1408ca3d0` holds is the target at once (no distance, floor or cage; a free or escaped slave in the open, or a caged one in sight; Meitou's first pass takes the free ones only, caged ones in sight fall to the cage pass: engine choice); else `BuildingFinder_SlavesToProcess` over its home town: occupied function-8 cages of non-player factions passing the `BuildingFinder_Cages` tests, occupant not of its own squad and needing processing, the cage on the watcher's floor (vfunc `0x60`, `+0xa4`) and in the same building (the `isIndoors` handles, vfunc `0x1d8`, compare equal; two outdoor parties are equal) and within 3D squared distance 32400 (`DAT_1416e18fc`) of it (**Verified**, `FUN_1408cd630`) |
| 260 Watching for new slaves | slot 61 | `FUN_1408cc110`: the same building finder; the first occupant that is not down (vfunc 6) and in slave state 0 |
| 167 Capturing slave | slot 61 (as 183) | `FUN_1408cdd00` (**Verified**; the 0xa7 registration in `FUN_140319a60` stores `LAB_140025f2c` at TaskData `+0x160`, a `jmp` to it in the 1.0.68 exe; 0xa8's `LAB_140003c9c` jumps to `FUN_1408cdf50`): the capturer's own sense entries (`FUN_140853960` over the AI's map, no squad sharing, no distance cap: `DAT_14169bc38` is the largest float; nearest by 3D squared distance to the target's current position, vfunc `0x40`), so the building rule is the sight test's ([perception](perception.md#sight)): a downed target is never heard (not moving, not in combat mode), so one in another building is a candidate only when either stands on top, or while an entry from before lasts ([decay](perception.md#senses)); tagged KO or CRAWLING (0x4008; CRAWLING = posture 1 or 2 out of combat mode, or in a bed) and not ally or dead (0x11), counted down when unconscious or posture > 0; uncarried, free, not animals, not in the give-up list, NPC class (status `+0xdc`) not 1 LAW_ENFORCEMENT, 3 TRADER or 5 DIPLOMAT, slave state not 1, not maimed (`FUN_140644170`: a leg stump, or both arms stumps); the nearest. The 168 carry finder (`FUN_1408cdf50`) uses the same down test but refuses posture 1 (staying low). Meitou: `CaptiveSystem.NearestDown` (the entries the mask keeps, 0x11 or 168's 0x210 as ally / imprisoned tags, then characters within sight the capped block lacks that pass `CharacterFacts.Perceives`; the KO / CRAWLING tags are not tested, Meitou has no CRAWLING bit, the target's current state is: engine choice), `Down`/`Maimed` (plus a not-shackled guard). **Observed** (headless, #314 save, Sho-Battai): Slaver Shop members in their Longhouse do not pick the KO'd raiders in the Police Station; one inside it picks those beside it, not a KO'd character 1600 away outdoors. Plan and tasks: [Slave work, shout and capture](#slave-work-shout-and-capture) |
| 168 Carrying slaves | `FUN_140599480` (the rescue desire) | `FUN_1408cdf50`: sensed (0x20 / 0x210) down characters in slave state, owned by its faction (status `+0xe0`), uncarried, free; the nearest. Row 170 (lift and heal first) lists !78 not imprisoned, !22 needs first aid (evaluator not decoded: a thunk), 80, 27, !26, isAtLocation and the planner reaches it for the victim, running a first aid row (25) first for a wounded one. Meitou: the planner plans it (`CaptiveGoals`); the start refuses a carrier `CarrySystem.CanCarry` rejects. | **State 22 `needsFirstAid`** (`FUN_14099d8f0`, **Verified**): the target (a character, or a squad's) is not a robot (a byte of its race object) and its organic first aid need (`scoreFirstAidNeed`) is above 0.01 (`DAT_141682ad8`); a plan for 168 thus runs row 25 First aid (kit 23, arm 38) on a hurt victim before the lift, and fails without a kit. Meitou: the job at the slave treats first (`FirstAidSystem.NeedsFirstAid`, `TreatBeforeLift`: a kit and an arm, else the job is given up).
| 173 Gathering slaves, 245 Gathering unholy slaves | slot 63 `FUN_14059b940`: 1 while the platoon's slave list holds at most 3 characters up and free (vfunc 6 false, `+0x2f8` 0) | 173 `FUN_1408cc4b0`: the nearest occupied processable cage of its faction in the 3x3 zone cells (4608 units, `FUN_1409b2d90`) round the gatherer, filtered by the town the character stands in (`status +0x88`) or unfiltered when it stands in none (**Verified**, `FUN_1409fc450` / `FUN_1409fb880`: town argument null passes all). Each candidate (`FUN_1408db510`): a cage (function 8) with an occupant (`UseableStuff` vfunc `0x4f0` false), not the player's, usable (`FUN_140621230`), not in the gatherer's give-up list (`FUN_1405b0290`), and worked by at most `+0x3c` squad mates (`FUN_1402690e0`), which 173 sets to 0x100: no claim in effect, so gatherers of different squads head for the same cage and the first lifts it (**Verified**; Meitou `CaptiveSystem.GatherCage` takes no claim for 173); 245 `FUN_1408d1410`: own-faction cages, the first occupant that is a woman (appearance `sex female`, `FUN_140295a50`) or of another race than the gatherer, **Verified** (no distance sort; the occupant is never tested for being a player character, down or busy) |
| 217 Gathering slaves for shipping | slot 64 `FUN_140597b80` (**Verified**): 1 in a TOWN or OUTPOST (town `+0xd8` 2 or 1); when the town's owner faction is the player's (`+0x250` set) or anti-slavery (byte `+8`, `anti slavery`), only if the worker's faction is its ally (`FUN_1406b2630`; the same faction counts), else 0 | `FUN_1408cc600` (**Verified**): `BuildingFinder_Cages` (`FUN_1408db510`) over the blackboard's town, cages the worker may use that are occupied (`UseableStuff` vfunc `0x4f0` false) and that no other member of the squad works (`FUN_1402690e0` at most 0, the finder's `+0x3c`); then, for a cage whose owner is not the player's, dropped when the occupant is missing, down (vfunc `0x30`: KO, dead, carried, ragdolled, posture 3 or more) or crippled (vfunc `0x360`, byte `+0x5b8`, [combat](combat.md#crippled-flag); Meitou `CombatSystem.IsCrippled`), or when two squad mates already work the occupant (`FUN_1402690e0` at least 2: `Blackboard::howManyGuysWorkingOnThisSubTarget`, the blackboard entries whose worker is neither the asker nor the target and whose sub-target is the target; not the give-up list); the nearest |
| 174 Ordering slaves (`Task_GetSlave`) | slot 19 | its start posts world event kind 5 (target, self) to the GameWorld list `DAT_1421346d8` and ends; the world update replays it as `setChainedMode` (see [capture](#slave-work-shout-and-capture); effect: 116 TARGET_IS_MY_SLAVE) |
| 182 Processing slave (`Task_ProcessNewSlave`) | slot 19 | 3 s of `medic`; then every item of the whole inventory whose record type is WEAPON (2), then ARMOUR (3), ITEM (4), CONTAINER (46) (`FUN_1407469c0`, **Verified**), not locked (vfunc `0x2f0`), not ARMOUR with `stigma` 6 (+0x244, slave garments) and, in the ARMOUR pass (`FUN_140343480`, handed the worker faction's `slave clothing`), none of that list (**Verified**; so a slave processed again with unlocked shackles keeps its kit and is not dressed anew, #356), goes to the processor's worn backpack (`FUN_1405cb320`), else its inventory (`Inventory::addItem` with the drop flag: what does not fit is dropped at the processor, **Observed**), each marked stolen from the captive on the way (`Item::notifyTheftFrom`, slot 106: an item with no owner yet, **Verified**), so a processor whose package lists 184 Ditching loot (Cage Warden, Slaver Storage Guard) carries the kit to a storage (#977, **Observed**) (the WEAPON and ITEM passes are skipped when the processor's faction has byte +0x30 set); only when the captive's `legs` section is empty is the faction's `slave clothing` made and put on, each template its section does not already hold (`FUN_1403560e0`); event 35 INTRODUCING_NEW_SLAVE (37 RECAPTURED_A_SLAVE when vfunc `0x3e0`) to it; slave state 1 (`FUN_1405a4940`), the owner setter `FUN_14032e430` (leaves the old owner's list, writes the new owner's faction to status `+0xe0`, joins its list) called **only when the owner handle `+0x328` resolves to nothing**, so a live owner and its faction stay (**Verified**, `FUN_14035aa20`); then the captive's `OrdersReceiver` is reset: `FUN_140507530` (the `+0x10` hand, the second list `+0x70`, the current goal) and `FUN_140507280` (perma-jobs `+0x88`) destroy every entry; the order queue stays (**Verified**), and job 187 Obedient Slave is added toward the owner; at the end `FUN_14067aca0` clears the captive's TEMP_ENEMY, criminal and aggressor short-term tags in the memory of the processor's squad, its sensed allies' squads and every platoon of its faction (it adds nothing to a slave list, **Verified**; Meitou: `CaptiveSystem.ForgiveCaptive`, allied characters within sight stand in for the sensed allies; the sensor's ENEMY bit and the town list are not modelled) |
| 188 Working the slaves | slot 65 `FUN_14059a0a0`: 0.25 x the platoon's slave list size | `FUN_1408cd940`: a random live, uncarried, free slave of the platoon's list not in the give-up list; B list isDialogDelivered; the event is the row's own, 38 SHOUT_AT_SLAVE_WORKER (**Verified**: the registration stores 0x26 at TaskData `+0x130`) |
| 192 Delivering slaves | slot 65 `FUN_14059a0a0` (**Verified**): 0.25 (`DAT_141696c80`) x the entries of the platoon's slave list, 0 with none; the same slot serves 188 and 194, 218 uses 66 | none; TaskData flags 0 (it neither acts nor has an effect, **Verified**), so once in the town its plan is its own row alone and the scorer drops it; B: !26, atDestinationTown. State 26 `FUN_140599e90` (**Verified**) is false for a crippled character, true for posture 1 to 3, in a bed, or two flags; so the leader hands nothing over while it crouches or lies. 53 brings it to the town (the only provider of state 36, [town goals](#town-goals)). Meitou: `CaptiveSystem.DeliverDesire`, `StepDeliver` |
| 194 / 218 Selling slaves | 65 (194 shares 188's stub `LAB_14003a918` and so its desire, 0.25 x the platoon's slave list; 218's stub `LAB_14005096b` is a different one, **Verified**); 218 slot 66 `FUN_140597c70`: only in a prison or slave camp (town type 7, 5) | `FUN_1408ca950`: a random allied building of designation flag 0x80000 in the town the leader stands in (status `+0x88`), a target already held kept (**Verified**), whose owner is a character or squad (`+0xd8` 1 / 0x22): its leader; B isDialogDelivered (event 39 SLAVE_DELIVERY) |
| 172 Getting in my cage | slot 19 | `FUN_1408caaf0`: for a character whose owner handle is a character or squad, whatever its slave state (no state test, so an escaped slave that keeps its owner finds one too; **Verified**, decompile; Meitou: not state 0): the nearest free cage of the owner's faction (**Verified**: `BuildingFinder_Cages` over `FUN_1409fc450` with town argument 0, so no town filter, in the 3x3 zone cells round the slave; no squad mate may be on the cage) |
| 189 / 190 Worker | slot 19 | `FUN_1408cac60` / `FUN_1408cadb0`: a random building of the platoon's town whose BuildingFunction is in mask 0x41a (bits MINE, RESEARCH, REFINERY, CRAFTING); a character whose side differs from the town's (squad ownership `+0x250`) only works there as a slave of state 1; none found: 0 (Meitou: `AiSystem.WorkAllowed`, used by the desire and the pick). Effects IS_WORKING (123, 189) and IS_WORKING_PRETEND (136, 190), acted out by 87 / 221 Operating machine |
| 187 Obedient Slave | - | no desire or finder: a job added when a character is enslaved (the slave window's sale, processing), scored by its own branch in `FUN_14050e5b0` (see below) |
| 230 Eat corpse (**Verified**) | slot 19 (1) | `AI::findCorpse_onGround` `FUN_1408cfde0`: sensed DEAD, lying (ragdoll or carried), race `carriable`; an animal eater: not a robot, not its own race; not carried, no squad mate on it; nearest. Effect 135 TARGET_IS_EATEN is always false (`AI::noneState`), so it always plans 220 on the target and stays chosen while the finder finds it |
| 233 Eat body (**Verified**) | slot 19 (1) | `AI::findDeadOrKOBody_onGround` `FUN_1408ce680`: sensed KO, DEAD or CRAWLING, not ALLY, not a robot, not the eater's race, lying or unconscious or dead, not carried, no squad mate on it; nearest (a KO victim is eaten alive) |
| 232 Find crops (**Verified**) | slot 19 (1) | `FUN_1408cb810`: in the blackboard's target town, else the current one, whose faction does not coexist with the eater's (`FUN_1406b23c0`): a current farm target kept, else a random farm (class 13) with grown > 0.4 and died < 0.9. Effect 144 "am hungry" is always true, so it always plans 231 `Task_EatingCrops`: each update `eat` clip, crime 14 on the eater against the farm's faction, `FarmBuilding::eat(age 1 x dt)`: an accumulator past 1 destroys the next plant (growth -10, the harvest index), one plant per game second. Data: 230 in 5 packages and 7 races' `AI Goals`, 233 in Beak Thing and the spiders, 232 in the river and swamp raptors |
| 237 / 236 Worshiping (**Verified**) | slot 19 (1) | `AI::findFogmanWorshipTarget` `FUN_1408ce880`: a remembered cage with an occupant kept; else in the nearest town the first cage whose occupant is being eaten. Effect 146 is always false, so 236 `Task_WorshipTarget` runs on it: beyond 60 walk to the cage, within stop, face it, `bowing`, every uniform[3, 14) game s event 53 (no target); no end of its own. Data: 237 in the fog packages |
| 277 / 278 / 279 dog limb play (**Verified**) | 277 1; 278 `AI::scoreDogPlaying` (mouthful: 0.3 a player squad's dog over 0.4, else 1.1; a player adult over 0.95: 0); 279 `AI::scoreDogPlayingChase` (1.2 NPC; player: 0 adult, 0.3 over 0.2, 0.6 younger) | 277 `AI::findLimbOnGround`: nearest limb (function 17) within 200 in its location (an adult not while fed or hunger above 2.75; returns 1 even with no target). 278 `Task_DogLimbRunningPlay`: first run 80-180 past the centroid of who it senses, away; then random legs of 1000-1400, a new one under 200 left, speed 999; ends when the mouth empties. 279 `AI::findAllyAnimalWithFoodInMouth`: first sensed awake ALLY animal with a mouthful, followed. State 149: a mouthful or food in the inventory or backpack. Data: Bonedog and Boneyard Wolf race `AI Goals` |

- **State evaluators** of these rows (**Verified**: each id's registry thunk followed to its function in the 1.0.68 exe): 98 `MACHINE_OCCUPANT_IS_ALIVE`
  `FUN_1405a1940` not dead (`+0x5bc`); 142 `isTargetRestrainedOrKO` `FUN_1405a17a0` dead, down (vfunc `0x30`) or held in something (`+0x2f8` not 0); 134 `isChained`
  `FUN_1405a1db0` the chained byte `+0x320` (`setChainedMode` sets it; Meitou: locked shackles worn); 124 `isTargetEscapee` `FUN_1405a2080` slave state not 1
  (`FUN_1405c8aa0`, the status's first int, which `FUN_1405a4940` writes); 116 `TARGET_IS_MY_SLAVE` `FUN_1405a2170` chained and the asker on its owner list; 118
  `FUN_1405a2290` unarmed; 120 `FUN_1405a6f60` looks like a slave (`FUN_1405a6bb0`); 115 `IS_CARRYING_TARGET_HEALTHY` `FUN_14059ab30` carrying the target (48) and
  not `needsFirstAid`; 143 `farm has food` `FUN_1408ca350` a farm (function 13) grown past and died below the thresholds; 155 `iHaveSomeSlaves` `FUN_14059a0e0` the
  platoon's slave list not empty; 149 `gotAFoodItem` `FUN_140597e20` a mouthful, or food (item types 0xf, 3) in the inventory or backpack; 144 always true; 135
  and 146 always false.
- **Kill** (`Task_KillPrisoner`, 134 and 196): faces the target (a character, or a cage's occupant) and plays `chop down static`; each time the clip passes 0.55 the
  character's weapon attack (`FUN_1408868b0`, the race multipliers included) lands through the target's take-hit virtual and the target says event 11 SCREAMING_TORTURE. When the
  target is dead the character's town gets a new kill time, now + uniform[1, 15) h. 134 needs a drawn weapon.
- **Eat** (`Task_EatPrisoner`, 220): walks to within 20 of the target (XZ); a robot target ends it. A target not in a cage (`+0x2f8` ≠ 2) that is getting up (`+0x278`) takes one bite and the eater is ordered 35 Running away from it (clear; **Verified**, `FUN_140353c50`). The walk is not this update: row 220's list B holds state 1 (at the target), so the plan walks there first as row 1 Moving and the 35 comes only once it eats (**Verified**, row 220 and `FUN_140353c50`; Meitou's walk is the same). Otherwise each update: faces it, plays `eat`, says event 52 EATING_SOMETHING_SOUNDS,
  and calls the target's being-eaten virtual with 1.0 (`FUN_1404356f0`; every bite, first thing, the medical update `FUN_140650370` writes -1 into the KO timer `+0xa0` and sets Character `+0xec` `playerWantsMeToGetUp`, **Verified** decompile: a knocked-out victim wakes at the next medical update unless a vital part, the blood or a hunger knock-out holds it down, and one playing dead gets up; Meitou: `MedicalState.BeingEaten` writes the timer, `CaptiveSystem.Eat` the get-up): unless dead, and while vfunc `0x358` is false (read as "conscious") or its byte `+0x4f` is set, the
  target says event 11 and gains toughness XP (`FUN_1408c6430(stats, dt, 10, 21)`: the continuous entry, [character-stats](character-stats.md#levelling-xp), amount 10, stat 21 Toughness), then the
  medical update `FUN_140650370`: rate = 1 x lerp(2, 0.4, toughness x stat scale x 0.01) per second (x5 instead when medical `+0x164`); parts from the last down:
  the first with h - 1.1 s > -M loses rate x dt from flesh and from the treated pool and the update ends; a limb part (arm or leg) is handled only when its limb slot reads original (`FUN_1400cd450` 0; a stump, prosthetic or crushed limb is skipped, **Verified**); a limb with nothing left to bite has its whole limb slot cut (`FUN_14064edc0`, no per-part severance flag, and with its item flag 0: no severed limb item drops, **Verified**) when the global `DAT_142133504`
  is set (Meitou: `MedicalState.BeingEaten`); with nothing left, a counter `+0x15c` rises, and past 5 the character dies ("eaten"; "{1} has been eaten alive." for the player's). A robot is never eaten; the first update with nothing left to bite, when the eaten character's faction has the player flag `+0x250`, posts a type 0xc message to the global event list `DAT_1421346d8`. The end
  of the task resets the town's kill time as Kill's does. A target not imprisoned and with `+0x278` set is bitten once and the eater re-ordered (order 0x23; Meitou: `CaptiveSystem.RunFrom`,
  the order `FleeSystem`'s).

### Slave work, shout and capture

Meitou: [simulation, captives](../simulation.md#captives).
- **Worker** (189 `AUTO_LABOURING_MINES`, 190 `..._PRETEND`; **Verified** registrations in `FUN_140319a60`: effects 0x7b IS_WORKING and 0x88 IS_WORKING_PRETEND, finders `FUN_1408cac60` /
  `FUN_1408cadb0`; only 190 commits, 0.5 + r x 2 h, crowd 1). They are not slave-only: the data lists them in "worker fighter indie resident", "Shop" (slave list), "working mines and farms",
  "Work, Protect, Capture", "South drone sacrifice farmer" and, in the Slave role list, "slave worker day guard" (171 at 0.1, 189, 172 at night, 190). The finder asks the platoon's town
  (`Blackboard +0x88`) for a building through `BuildingFinder_SpecialFunction` (mask 0x41a read as bits, **Observed**: 1 MINE, 3 RESEARCH, 4 REFINERY, 10 CRAFTING) and takes one at random
  (slot 2 `FUN_1409fb020`: index `FUN_1409b3b40(0, n - 1)`); the town test is the one in the table above. Acting out is 87 (real: needs the machine's inputs and output room, power, a free operator slot) or
  221 (pretend); both `Task_OperateMachine` ([sitting and the held tool](ai-tasks.md#town-goals), [production](buildings-production.md#production)). So a pretending worker
  operates the building as a real one does (**Verified**: the factory fills the task alike for both rows and the update `FUN_14035bc90` reads no row: once `tryOperate` (slot `0x4f8`) passes, whatever the machine holds,
  it calls the operating XP ([seat XP](#town-goals)) and the building's work step (slot `0x4b0`), which works a fed refinery or a mine as 87 does; Rebirth's Quarry Mines and the Manual Stone Processor both have `stat used` labouring, merged data).
  At a bench the work step makes nothing for an NPC town (**Verified**): `ResearchBuilding`'s (`FUN_14029b8d0`) acts only when the owner faction has the player flag (`+0x250`), `CraftingBuilding`'s (`FUN_1402b5a40`)
  only at status 2 with a current craft. Meitou: 189 operates the town building's machine when its plan starts with 87 and empties it when it starts with 92, 190 operates it with 221 (`AiSystem.WorkAtMachine`, `ProductionSystem.TownOperate`) at its functionality's clip (`stonemine` by default, with its `animation events`, [audio](../audio.md#characters-footsteps-combat-voices) "Machine work"; [NPC town machines](buildings-production.md#npc-town-machines)); **it walks to the building's use node nearest it and has arrived within the building functionality's `use range` of that node, twice that when not moving ([work distance](buildings-production.md#work-distance)).
  - **The finder's visit** (`FUN_1408cebe0`, the query's slot 0, **Verified** by disassembly) keeps a building when all of these hold:
    - `getUseableStuff()` (vfunc 0x300) is set and `getBuildState()` (0x228) is complete;
    - its function (0x2f0) is in the mask, and it is of the query's town;
    - a test of the building against the worker passes (`FUN_14003bd0e`), or the worker is in slave state 1;
    - it is not on a list of the worker's (`+0x650`, `+0x20`), and a foliage building (`+0xc0`) is within reach;
    - `UseableStuff::couldIOperate(worker)` (vfunc 0x500, `FUN_1400f7c90`, the same for UseableStuff, ProductionBuilding and
      CraftingBuilding) holds: not `dontNeedWorkRightNow()` (0x4e8: a constant 0, `FUN_1402ab3c0`, for UseableStuff, ProductionBuilding and CraftingBuilding; ResearchBuilding's `FUN_1402995e0` is true only for a player-owned bench with no current research, FarmBuilding's for a growing crop), and the worker already
      operates it or the operators (`+0x3d0`) are fewer than `numOperatorsMax` (`+0x3ac`; the functionality's `max operators`, 6 for a
      Stone mine, 1 for Stone processing).
    - So the finder takes a refinery with a free slot whatever its inputs.
  - **The plan refuses an empty machine**: 87's B list has !62 `machineHasEmptyInputs`, whose only provider is 93 Filling machine (67
    `haveSomeResourcesFor`, from 94 and a source of the input). **Observed** (KenshiOracle state 0.5 hold, Rebirth, N = 1): the
    original's slaves on 189 work only the quarries (Stone mine) and never the four unfed Manual Stone Processors.
  - **A bench passes 87's list B** (**Verified**, vtables): a `ResearchBuilding` has no StorageBuilding cast (`0x2f8` `FUN_1402adb10` returns null), so `!62`, `!63` and `!89` hold there, and `!169` needs production class 6 (**Observed**: the class has no slot `0x5a8`); so 189 operates
    research benches too, power as at any machine.
  - Meitou: 189 picks only a workplace it can run (`AiSystem.RealWork`): a mine, a research bench, a crafting bench whose sections draw nothing (no queued craft binds them, `CraftingBuilding::vfunc_182`, **Observed**), or a refinery with a machine
    running there that has its inputs. An NPC town's refinery counts only once it holds its inputs: the pick does not ask whether 94 / 93 could fill it (engine choice for the plan's fill-or-fail).
    190 (pretend, no 62 in 221's B list) still picks any workplace. A member on 189 or 190 holds an operator slot (`AiMind.WorkPost`,
    `AiSystem.ClaimWork`, serial on the package cadence, the kept slots reserved first): up to the functionality's `max operators` per
    workplace, no limit for a building without a functionality record. `Task_OperateMachine` (87 and 221) adds the worker to the building's
    operators at its start (`FUN_14035b430` -> `tryOperate` `FUN_1400f8030`, which refuses a new operator once the operators reach `max operators`, and the task
    then ends) and removes it at its end (`FUN_140347350`, **Verified**), so a pretending worker takes a slot too.
- **Obedient Slave** (187, flags 0x75, TaskData kind +4 = 1 and +8 = 0 like a plain job, no lists, no desire or finder of its own, task class `Task_Blank`). Meitou:
  [simulation, captives](../simulation.md#captives). A **perma-job** (it sits in the character's job list, `+0x88`), not a goal, whoever the character is.
  - **Added and removed** (**Verified**, `FUN_1405a4940` slave state setter -> `FUN_14032e0d0`): when the slave state *changes* (the setter does nothing for the same state), a change **to 1**
    destroys the second list, the current goal and the perma-jobs (`FUN_140507530`, `FUN_140507280`; the order queue stays) and adds job 187 toward the character's owner handle (character `+0x328`, `FUN_1405086d0` with the
    permanent flag, which turns the jobs switch on); a change **to any other state** (0, 2 or 3: the setter passes "new state is 1") removes the first job of type 187 and stops, or, when the list holds none, runs the same two resets.
    So freeing, an escape (state 2) or bail ends the obedience at once. DA 28's split also destroys the perma-jobs (`FUN_1405ce4e0` -> `FUN_140507280`, **Verified**): the slave keeps state 1
    and its owner but no longer holds the job, so it plans with its own lists ([dialogue](dialogue.md) DA 28; Meitou: `CharacterCold.ObedienceLost`, which `AiSystem.ObeyedRoles` and the
    protect desire read, cleared by every state change and reset). `Character::setChainedMode` (`FUN_14032e590`, which sets state 1 first, writes the owner handle from its target and the owner's
    faction, runs the same two resets, adds the job and then puts the shackles on, **Verified**) and processing (182, `FUN_14035aa20`) do the same reset themselves, whatever the old state. The job is
    saved with the list (`pjob<i>` 187, `pjobT<i>` the owner handle).
  - **Scored** (**Verified**, `FUN_14050e5b0`, both job passes, the urgent one at step 3 and the full one at step 5; the jobs switch gates it like any job, the list of ignoring types does not hold 187):
    for a character in slave state 1 (`FUN_1405c8aa0`) that is **not sneaking** (`Character::isStealthMode` `FUN_1405c9750`) and whose owner handle (`+0x328`) names a character (type 1) or a squad (0x5b)
    that resolves to a platoon, the entry is not a candidate itself: the owner platoon's **Slave role list** (role 4, priorities 1 to 3: mask 0xe, `FUN_14026b790`) is loaded into the slave's own goal
    list (`+0x178`) and scored with the normal scorer `FUN_14050df10` (desire x finder x weight x crowd, hour window; a goal that plans runs) into the same candidate map. Their keys are the
    goals' scores (typically 0 to 2), below the first jobs' keys (N + 8, N + 7, ...). **So at step 3 the owner's Slave goals beat the character's own priority-3 goals (Self preservation, Getting food, 15 Protect allies)
    and every job but the kind-2 ones**; a slave whose list plans something never reaches them. With the gate failing (not state 1, or sneaking) the entry is scored as a plain job, whose
    desire is a default function (`LAB_1400215ee`) on a blank task: nothing is read as running. If the list plans nothing, the step goes on as for any character.
  - **What the Slave lists hold** (**Verified**, the resolved Slave lists of the AI_PACKAGE records of the merged base game, Dialogue and rebirth through `AiDatabase`, 64 entries before inheritance): 171 Following master (in 35 packages,
    priority 2), 172 Getting in my cage (23, priority 2), 41 Attack enemies of my slavemaster and 143 Protect own squad (5 and 4, priority 3), 35 Running away (the police and prison packages),
    14 Idleness (the cage wardens at weights 0.05 to 0.11), and the slave camps' workers: package "slave worker day guard" 171 at 0.1, 189 Worker 5-22 at weight 2, 190 Worker (pretend) 5-22 at 1.5, 172 at
    night (22-5) at 0.5, 14 at 0.05; "Shop" and "Shop catun" 190. Read so (**Observed**): a slave owned by a squad running that package works the town's mines by day and goes to its cage at night.
  - **The plan** (**Verified** for the targets): 171 is `Task_FollowLeader` (the same class as 141), which follows the slave **owner** if the character has one, else its squad leader
    ([pathfinding](pathfinding.md#following)); it copies the leader's sneaking every update, so **a slave following a master who is not sneaking cannot sneak**, and escapes by sneaking only while it stands or works.
    189 / 190 are the worker rows ([above](#slave-work-shout-and-capture)): the finder asks the **blackboard's current town** (`+0x88`) of the slave's own platoon, which the AI's rule writes from the
    platoon's leader (`FUN_1402688d0`, [AI](ai.md#package-classes)); a character whose side differs from the town's works only in slave state 1. 172 is the slave's own walk into a free cage of its
    owner's faction (finder `FUN_1408caaf0`). The crowd factor of the scorer (TaskData `+0x34`) is the default 1 for every goal the Slave lists hold (**Verified**: no registration in
    `FUN_140319a60` writes it for 14, 35, 41, 143, 171, 172, 189 or 190, and AI_TASK records carry no such field), so the master squad's mates on the same goal change no score.
  - **Leaving the night cage** (**Verified**, `FUN_14026b790`, `FUN_14032ebd0`, `FUN_14050f800`, `FUN_14050fe10`, `TaskRepertoire` slot 1 `FUN_14032dbf0`): there is no own release. Getting in
    (107 `Task_UseCage`, start `FUN_140358040` -> `FUN_140330600`) locks the cage and sets the imprisoned repertoire (4; 0x10 in a cage that restrains movement). The Slave list is loaded through the
    Tasker factory with the slave's repertoire, which makes nothing for a row the repertoire lacks, and the planner looks the root row up in the same repertoire, so a row it lacks plans nothing
    (an empty result, value 0); a list loaded before the caging is kept but its 171 / 189 / 190 then plan nothing. Of the Slave goals only 172 (flags 0x55) and 14 (0x75) carry the bit; 171 (0x61),
    189 and 190 (0x1) do not, and no Slave goal needs `!79`, whose providers 133 / 207 / 256 (0x74) would in any case need the cage unlocked (`!54`). `Task_UseCage`'s end (`FUN_140348330`) only
    resets the animation. So in the morning a caged slave stands (14 in its hours) until another lets it out: its master's gatherers (173, 217, 245, [Prisoner fate](#prisoner-fate-and-slave-rows)), a
    release, the player, or the other callers that clear the hold (`FUN_140330600` with 0: a lift `FUN_1405cff90`, being bought free `FUN_1406add20`, the Building virtuals at slots 73 and 81
    `FUN_1402a2860` / `FUN_1407ad6c0`); `Task_FirstAid`'s update (`FUN_1403563a0`) also takes a caged patient out when the medic's faction is not the player's and the medic regards it as friendly.
  - **Other readers of "has job 187"** (`FUN_140597380`): 15 Protect allies' desire `FUN_140599b90` is **0** for a slave in state 1 holding it (**Verified**, sneaking or not); `chooseBestAttackTarget` (`FUN_14059e300`) never scores a candidate in state 1, chained with the chooser's faction its owner (`FUN_14032e2a0`), that holds it (**Verified**, [target score](combat.md#attack-slots-and-circling)); `FUN_1408569a0` spares its owner's friends ([sense tags](perception.md#sense-tags-who-is-an-enemy)).
    `FUN_140852700` (**Verified**, disassembly) holds when the target has no order queued (its order list empty, or one entry of type 65 or 52) and the AI it is called on holds job 187. Its only callers,
    the escaped-slave test `FUN_140855a80` and its helper `FUN_140852750`, are reached from the sight pass `FUN_140859680` through `FUN_140856b50` with the **looker's** AI, and that pass has
    already returned for a looker in any slave state but 0. A character in state 0 holds no job 187 (only a change to 1, chaining or processing adds it, after clearing the list; every
    change away from 1 removes it), so the test is false there and the job exempts no slave ([escaping slave spotted](#slave-work-shout-and-capture) below).
  - The status tooltip tells the player: "Your character will have a permanent job assigned called [Obedient slave]. While this is active he will automatically work for his master."
  - **Meitou**: the AI's slaves take the Slave list of the squad they obey, their own squad included whatever their squad role there (a guard enslaved by its own squad drops its guard goals and its turret, seat and sitting claims, #991), `CharacterCold.SlaveMaster`: the owner squad of
    the squad factory's slaves, the slaver who bought or processed it, the arresting guard's squad, the receiver of a delivery) run it through the AI ([ai](ai.md#goal-loading)). A **player character** gets the job in
    its list (`ObedientSlave`, step 3 of the decision step): the gate as above; the best goal of the master squad's Slave list in its hour, eligible in its repertoire (`TaskStart.Eligible`: caged,
    only 172 and 14), by desire x finder x weight x crowd (the row's `TaskProfile.Crowd` to the power of its own squad board's entries on the goal; x1.25 the one running, a tie
    to the later-listed) among **171** (the owner, else the master squad's leader); **189 / 190** (the AI's finder, `AiSystem.WorkAt`: a random workplace of the town the
    slave's squad leader stands in with a free operator slot, its own counting as free, 189 only one it can run; walked to and stood at at its work clip; standing there it holds an operator
    slot as `Task_OperateMachine` does, counted with the members' kept slots, `AiSystem.ReserveObedientWork`), at which both take the functionality's `hunger rate`. 189 works the town building's
    machine as the AI's plan does (87's list B in order, `ObedientJobs.ErrandFor`): at the building a full output is emptied into the inventory (92), else an empty input filled from what it
    carries (93), else the machine operated (87; `ProductionSystem.ObedientWorkers`); it first walks away for a full output with no room (the ditch, 114 then 93, `TownDutySystem.DitchStore`), an
    empty input with none of its items carried (row 94's source, `ProductionSystem.InputSource`, emptied there by 92, the ditch first without room for its item) and carried output of the machine a
    storage takes (state 89, 113 then 93, `TownDutySystem.StoreTaking` / `PutAway`); with no source or no storage to make room in, the next start picks another workplace (engine choice: the
    original's finder rolls a fresh target each think). **172** (the finder above, `CaptiveSystem.OwnCage`; walked to and got into, `FixtureRegistry.Imprison`, as the AI slave's
    own walk; done once it is held; it stays until let out, as above); **41** and **143** (the AI's desires and finders, `AiSystem.ObedientFight`, 143's mates being the slave's own squad; the fight a job fight, 41's as a goal's
    attack with its alert voice, 143's as a defence); **35** (done while its list A holds, no immediate threat and no enemy sensed; else row 35's desire with the player-faction vetoes,
    `AiSystem.ObedientRunDesire`, [combat](combat.md#fight-lifecycle-and-orders) "Self-defence"; acted out as `Task_Runaway`'s run from the sensed enemies, `FleeSystem.EnemyRunPoint`, again when a run ends) and
    **14**. A faction owner with no squad has no master: the job is held but nothing runs. The job names the master squad's leader (the original's handle names a character or a squad). `SlaveMaster` is
    runtime only: after a load it is the squad of the owner character the save's `slaver` handle names ([save](../formats/save.md)), else of the one the job's saved handle names
    (`CaptiveSystem.RestoreSlavers`, both once that character is loaded; the escape far from the owner waits for either, a job whose owner the decision step found first too,
    `ObedientSlave.PendingOwnerJob`), so 41's finder and the senses read it as the original's owner handle.
- **Escaping slave spotted** (**Verified**, decompile and disassembly of `FUN_140859680`, `FUN_140856b50`, `FUN_140855a80`, `FUN_140852750`, `FUN_140852700`,
  `FUN_140853620`, `FUN_1405c8a10`, `FUN_140998790`, constants from the data dump): the sight pass of an NPC looker in slave state 0, whose faction has no player flag and no `anti slavery` (faction `+8`, the FACTION bool, loader `FUN_1407fe270`), that does
  not take the target for an enemy (slot 125; a slave owned by the looker's faction never is), asks the test for a target in slave state 1, not caged, whose owner faction is the
  looker's or none and whose owner hand is not null. It fires when the looker's spot value (`FUN_140853620`: the sight test's v when at least 0.01 and the ray is clear, else 0) is
  above 0.05 (`DAT_141696c74`): the looker says event 36 ESCAPING_SLAVE_SPOTTED to the target and the target's slave state becomes 2. No timer and no distance of its own. The test
  (`FUN_140855a80`) holds when the target:
  - sneaks out of a fight (`isStealthMode`, `FUN_1405c9750`; a sneaker reaches the sight pass only once spotted, [spotting a sneaker](perception.md#spotting-a-sneaker));
  - has an owner hand that resolves to nothing and is not in a bed or cage (Character `+0x2f8` 0), unless it is a character hand whose character the character table
    still finds with no object at `+0x1d8` or with its byte `+0xf0` clear (then the tests below run without an owner: the walk test is rows 35 / 127 or not looking like a slave);
  - wears no shackles (`FUN_1405c8a10`: the item in the boots section, else in the body section, answers slot 94 non-null; only `LockedArmour` overrides it, returning the item
    itself, so shackles count locked or not);
  - walks away from its owner (`FUN_140852750`): its current task (Character `+0x648` -> `+0x68`) is 35 Running away or 127 Run away hometown; or the task's position (Tasker
    `+0x58`) is non-zero, farther than 250 (`DAT_141fedd00`, no writer) from the owner and farther from it than the target stands, the target's movement heading (`+0x640` ->
    `+0xd0`) does not point at the owner (dot product with owner minus target not above 0, `FUN_1409b1960`), and the target is outdoors on the ground floor (floor below 1 and
    building hand null), or farther than 250 x sqrt 3 (about 433, `DAT_14168a254` 3 on the square) from the owner, or does not [look like a slave](#prisoner-fate-and-slave-rows)
    to the looker (`FUN_1405a6bb0`);
  - or, shackled and not walking away, is in a fight whose target (CombatClass `+0x2a0`, else the first of `+0x200`) is a character of a faction allied to the looker's
    (`FUN_140998790` -> `FUN_1406b2630`).
  The walk test's exemption `FUN_140852700` (no order queued and job 187) reads job 187 on the looker ([above](#slave-work-shout-and-capture)), so an obedient slave is tested
  like any other. A task's position is the one given to the Tasker factory (`FUN_14032ebd0` stores it at `+0x58`): a player order's point (`FUN_140508380`, from
  `FUN_1405d20d0`), while the role lists' goals, the Slave goals included, are made at the zero point (`FUN_14026b530`, `FUN_14026b790` -> `FUN_140330530`). A planned
  step has its goal's point (**Verified**: `FUN_14050fe10` hands the goal tasker's `+0x58` to the plan's root node, `+0x48`; the node expansion `FUN_14051bae0` gives each child
  its parent's; each step's tasker is made at its node's point), so the walks of a slave's own job goals (to its workplace, its master, a cage) never pass the walk test.
  The orders' points (**Verified**, disassembly of the right-click dispatch `FUN_1407fc6e0` and of `FUN_1407fae70`, `FUN_1407fa650`, `FUN_1407f9e20`, which hand
  their point on to `FUN_1405d20d0`): a ground move's point (29, scattered round the click); a complete building's default order (its vfunc `0x418` row, or 76 Pick
  lock with Alt, 207 / 107 / 108 at a cage) at the building's position (vfunc `0x40`, `+0x48`) with the terrain's height there (`FUN_1409b32f0`). Every other dispatch
  passes the zero vector: an unfinished building's 2 Build and a damaged one's 95 Repair, a container's 26 Loot and the 29 walk to it without Alt, an item's 3
  pick-up (`FUN_1407fb3a0`) and walk (`FUN_1407fc440`), the orders on a character (`FUN_1407fb410`), and a verb picked from the held right
  button's menu (`FUN_1407a7f10`, through `FUN_1407fa650`). The walk to a barred door (no Alt) is a 29 move made at the door's
  `getPositionMarker` from the zero point (`FUN_1407fa850`, vfunc `0x470` `FUN_14054b850`: its marker, or of 2+ use nodes the one nearest the
  world origin; [lie spot](locks.md#cages-and-beds)); Meitou: `LockSystem.DoorPoint`.
  **Observed** (harness, 1.0.65, the task tasker's `+0x58` read after
  each right click): three clicks on different points of an Iron Resource gave its walk step the node's own position each time, three clicks on a
  door the door's one point, while clicks on a plain building (the Hub's bar) or a wall gave each its own point, the cursor's hit on the wall
  at the hit's height: a building with no order of its own takes a move to the clicked point. Event 36's Holy Nation line ("Escaped holy
  nation slave", 40885-Dialogue.mod, in `Npc MILITARY Holy Nation Law 3`) is "Escapee! /SEIZE/ /HIM/!" with DA 6 ATTACK_CHASE_FOREVER and DA 33 CRIME_ALARM. Meitou:
  `EscapingSlave` from the sight pass (`SightCrimes`, [sight](dialogue.md#ambient-conversations); after 36 a slave still escaping and blown gets 36 again, re-tagged when of
  the looker's faction or none, and an ex-slave (state 3) still blown gets 40), else from `ArrestSystem.CatchSlave` ([simulation](../simulation.md#arrest-and-jail)
  "Slave catching"); it reads the running order's `TaskPoint` as the task
  position and none for its job goals, and while the owner is not known (no master squad, or one still to load) tests
  nothing past the shackles but standing outside the looker's town (engine).
- **States 140 and 16** (**Verified**): 140 `target swimming` = `FUN_140598ec0(ai, target hand)`: the hand resolves to an object (else false) and `FUN_1405c7fd0(object)` is 3, i.e. the target's water state is deep water (the HUD's Swimming). 16 `isAggressionTowards` = `FUN_14059de10(ai, target hand)`: only for a character hand (type 1), true when that character's byte `+0x2b0` is set ([combat](combat.md): the immediate-melee flag), so it means "the target is being meleed right now", not an aggression relation. Rows with `!140`: 25 First aid, 249 Splinting, 60 Repairing, 4 Attacking, 64 (which also has `!16`). Meitou's planner target is a position, not a character, so 140 and 16 are not wired; those rows are run by the first aid, repair and combat systems, which do not read them.
- **Nests and the town list** (`FUN_14092d850`, [factions](factions-squads-towns.md)): one town and one nest get the full step per frame (cursors `DAT_142135440` / `DAT_142135438`), so a loaded nest makes at most one squad every N frames, N the nests in the list. Meitou's nests top up on their own look (engine choice: the throttle is the look cadence, not a cursor over the list).
- **`BuildingFinder_SlavesToProcess` accept** (**Verified**, slot 0 `FUN_1408db680` -> `FUN_1408db510`): a building with vfunc `0x300` non-null whose owner faction (vfunc `0x58`) has no player flag (`+0x250`), then a cage (function 8, vfunc `0x2f0`) of the finder's town context within a relation limit (rating <= `0x100`), `Ownerships::canIUseThisBuilding(worker, cage)` true (`FUN_140621230`: [locks](locks.md#beds)), the owner not on the character's excluded list (`FUN_1405b0290`) and occupancy (vfunc `0x4f0`) equal to the wanted flag. No taken or reserved test and no "another job works this occupant" test: Meitou's `Taken` / `Busy` claims in `CaptiveSystem.ProcessTarget` are an engine guard against two processors. Meitou uses `FixtureRegistry.CanUse`.
- **The food finders skip failed stores** (**Verified**, `FUN_1408dac20`, slot 0 of `BuildingFinder_LootFoodAndStuff`): a building in the looker's give-up list (`(character +0x650)->+0x20`, tested by `FUN_1405b0290`, the list `FUN_1405995d0` also reads and a failed walk fills with 20 s) is not offered. Meitou: `TownDutySystem.Store` skips a store in `CharacterCold.Ignored` and a food errand whose walks all fail notes its store there.
- **Processing a slave shaves its head** (**Verified**): `Task_ProcessNewSlave`'s end `FUN_14035aa20`, for a human captive (vfunc `0x250`),
  asks Character vslot 124 (`0x3e0`, KenshiLib `isHeadShaven`): RECAPTURED_A_SLAVE (37) when the head is shaven already, else
  INTRODUCING_NEW_SLAVE (35). It then sets slave state 1 and shaves the head (`CharacterHuman::shaveHead(true)`, `FUN_1405b24a0`).
  RECAPTURED_A_SLAVE also comes from the cuffing task ([capture](#slave-work-shout-and-capture)).
  - Vslot 124 returns 0 in `Character` and `CharacterAnimal` (`FUN_1405e1f20`). `CharacterHuman` overrides it with its appearance's `shaved` (`FUN_1405b24c0`:
    `animation` `+0x448`, `appearance` `+0xe8`, `shaved` `+0x168`; RTTI vtables, KenshiLib offsets).
  - An ex-slave's hair grows back when it goes free after 4 days (`StateBroadcastData::periodicUpdate` `FUN_1405a4f50`, `shaveHead(false)`).
  - Opening the character editor in play (mode 1) grows back every listed human's hair at once (**Verified**, decompile: the constructor
    `FUN_1405f4dd0` calls `shaveHead(false)` at `0x1405f58cf` for each character with a human body when its mode is 1; the mode-1 openers are a
    surgeon's DA_CHARACTER_EDITOR (DA 11, `FUN_140680560` -> `FUN_1407f3670`), DA 3 recruiting and animal buying). So visiting a plastic
    surgeon restores a shaven head, even if the editor is then cancelled. Meitou: `InPlayEditorOpened` -> `CharacterEditing.GrowHair`.
  - Meitou: `CaptiveSystem` fires 35 or 37 and shaves `CharacterCold.Shaved`, saved as `shaved`. The look is built with the character's shave
    (`CharacterOptions.Shaved`; the CHARACTER's `shaved` for a new character and a loaded one without a state record): no hair, and cut horns
    on a mesh with `bone_horns_top_short` (Meitou follows the one appearance
    flag). It is built again when the shave changes (`CharacterAssembly.MatchShave`: processing, spawned slave squads, hair grown back) and on
    load. **Observed** (1.0.68): a player character processed in a Slave Traders cage shows a bald portrait at once.
- **Row 14 Idleness does nothing** (**Verified**): the factory builds it as the blank task (default case), the chooser `FUN_140510ef0` tries the default plan after tiers 3, 2 and 1, and when no goal is chosen at all calls CharMovement vslot `0x98` (`FUN_14065fc70`, the mover reset: the target position is overwritten with the current one, velocity and step zeroed, the path handle cleared, the destination hand reset to none, the move flags cleared). So an NPC with nothing to do stands where it is; no stroll. Meitou: `AiBehaviour.Mill` acts as the idle task (no wander round the post).
- **Body state `+0xb4` of the status object** (**Verified**): 0 awake, 1 asleep (set by `Task_UseBed` and by `Task_SleepOnFloor` `FUN_140348170` with the fake flag `+0xa3` clear), 2 asleep for 261 Hiding (`Task_SleepOnFloor` with the flag set); the end functions clear it. The only readers found are the hearing test `FUN_140857500` (the hearing range is multiplied by a factor only at 0, so 1 and 2 hear alike; event 47 ALMOST_WOKE_UP is chance-fired only at 1) and the sleeping desire below. So 261 differs from 175 only in not firing event 47; no base package uses 261 and Meitou does not act it out.
- **Row 98 Sleeping's desire** (AI slot 30 `FUN_1405a0880`, **Verified**; the planner drops a node whose desire is not above 0, so a 0 makes 153 -> 98 a dead end): 0 when the character carries someone (Character `+0x348`, the byte the character tick clears with the carry flag `+0x3d4`) or its race is a robot (`+0x7c`); 0 when the danger score (AI slot 28 `FUN_1405a0b50`) is above 0: 2 with an enemy sensed (AI `+0xbc`) or a character on its threats list (AI `+0x80` is that list's count: SensoryData `+0x50`, whose add `FUN_14016af10` keeps the count at `+8`, **Verified**), 1.5 with an alarm of its town near it (`FUN_14092a810`), 1 with a sensed neutral or enemy (filter 6, not 0x208) that it hears now (entry `+0x39`, `FUN_1408557f0`) in the same building when it stands in its own home (Ownerships `+0x38`); else 0, but for a player-faction character the protect allies desire (AI slot 14, `FUN_140599b90`). Row 208 Getting out of bed! has this danger score as its desire (AI slot 28, **Verified**: registration in `FUN_140319a60`), so goal 208 wants a sleeper up only for an enemy it senses, an attacker, an alarm its town's finder takes within 375 ([town alarms](economy.md) has the finder's rules) or a neutral or enemy stranger it hears in its home; a fight between characters that are not its enemies makes 208 want it up only through an alarm. Another goal that outscores sleep gets it up too (through the get-up rows 116 and 257; **Observed**, from the planner rules). Then for a character of a faction without a player flag: 1 while it lies in a bed (status `+0xb4`, set to 1 by every update of `Task_UseBed` `FUN_140347950`, with the time in `+0xc0`); 0 when it is in its home and knows (`FUN_140855960`, flag clear) a neutral or enemy entry (filter 6, not 0x208) in the same building; for an animal (Character vfunc `0x248` returns the object itself, 0 for a human) 0 within 180 minutes of `+0xc0`; 0.3 within 20 minutes for anyone; else 1. The player-faction branch is 0.1 when the character has a status town (`+0x88`) whose faction (vfunc `0x58`) has the player flag (`+0x250`), or stands in a building (its `isIndoors` handle, vfunc `0x1d8`, resolved by `FUN_140792910`) whose faction has it (**Verified**, decompile; so not in the wild, nor outdoors in another faction's town), its organic first-aid need (`FUN_140643ca0`, medical `+0xac`) is 0 or less and its rested level (medical `+0xb0`, read through the AI's `+0x318`; state 72's field) is at most 0.9 (the float at `0x14168a25c`), else 0 (the two medical tests **Verified**, decompile); so a player character with an unbandaged wound, or with every part above 90 %, does not go to bed (Meitou: `ProductionSystem.WantsBed`, `InPlayerPlace`). Meitou: `AiSystem.SleepingDesire` in the planner's node desire (`CharacterCold.BedUsedHours` stamped by `FixtureRegistry.HoldSleepers`; a player character the AI plans for (an autonomous squad) takes the non-player branch, so that branch is not modelled, engine choice; the alarm test stands in for `FUN_14092a810`, **Observed**; the danger score's home-stranger branch takes any entry it knows, heard now or not, engine choice). Row 208 is weighed by no goal (engine choice): its danger score empties the sleeping desire, and whichever goal then plans gets the sleeper out of bed first (`AiSystem.Act`), so a Meitou sleeper wakes for those causes whether or not its lists hold 208.
- **Shout** (188, `Task_TalktoPlayer`, slots 1 and 2 `FUN_14034c5a0` / `FUN_14034c7c0`): the event is the row's (TaskData `+0x130`: **Verified** 0x26 for 188, 0x27 for 194 and 218, 9 for 158 and 283).
  Start fires it at the target (`FUN_140684990`); each update walks to the target (vfunc `0x3b0`), repeats event 9, gives up after 25 minutes with event 60, and ends when the dialogue is free.
  The slavers' event-38 lines ("shout at slave worker": chance temporary 10, six lines) are in "slaver guard MAIN" and "Holy Nation Paladin".
  **Observed** (probe, state 0.5 live check, the 10 workmasters of Rebirth, N = 1): a workmaster on 188 keeps it with one slave as target for
  long spells (one to three targets an hour), with order `move_on_free_will` at 72 of 75 checks and `send_dialogue_signal` at 3, and none ever
  takes `stay_in_home`. Its walk runs while the slave moves faster than 20 ([pathfinding](pathfinding.md#running-after-a-moving-target); Meitou `FollowSlave`). Meitou (engine choice): the Shout job keeps with its slave and says the event again every 8 s until its worker speaks,
  then the next job picks a slave again ([simulation](../simulation.md)). The original's `isDialogDelivered` is not that test: it is true
  without a Dialogue object, else whether the event was delivered in the speaker's town ([dialogue](dialogue.md)).
- **Capture** (167): the planner chain is 167 (effect isChained 134) <- 166 Restraining (`Task_CuffTarget`; needs 38, at location, 118 TARGET_IS_UNARMED, 7 target down) <- 181 Stripping weapons
  (`Task_StripWeapons`; effect 118). The chain needs 38 `hasOneWorkingArm` (either arm, **Verified**: rows 166 and 181) and nothing else of the arms; 167 has no B list. Only the later lift (rows 213, 240, 246, 68, 170: 80 `isCarryingArmOK`, the left arm) needs the left arm, so a one-armed capturer cuffs and then leaves the captive. **Strip** (**Observed**, `FUN_14034e7b0`): `medic` for 0.8 s, then the items of the target's `hip` and `back` sections (`FUN_140747ff0`) go to the
  stripper's inventory (else its storage), and the stripper's squad sets short-term tag 6 on the target (`FUN_1406744a0`, once per item moved; the NPC looting finder skips tag-6 targets; Meitou `CaptiveSystem.Strip` sets `MemoryTag.Looted`, [carry-loot](carry-loot.md#33-npc-looting)). **Cuff** (`FUN_14034af20`): a "Restraining" bar
  with `medic`, progress += 0.1 x a per-update float of the character (`+0xc4`, read as frame time: about 10 s), full at 0.99: the cuffer says event 37 RECAPTURED_A_SLAVE to the
  target (its lines "new slave capture" / "slave recapture", no effects) and posts a world event of kind 5 (the target's hand, the cuffer's hand as owner, flag 1) to the GameWorld list `+0x5c8` (`DAT_1421346d8`). Every update, for a target in slave state 0 with no ledger entry for the null faction and whose squad's faction has `+0x250` set (the player's) or the `anti slavery` byte, the cuffer sets a pending Enslaving crime (code 1) against the target's faction through `FUN_140852c80` (**Verified**, `FUN_14034af20`).
  The world update (`FUN_140788a00` on GameWorld `DAT_142134110` → `FUN_140786e30` → `FUN_1407b65b0`, every frame) replays kind 5 as `Character::setChainedMode(target, flag, owner)` (`FUN_14032e590`, **Verified**): the chained flag `+0x320`, slave state 1, the owner hand and, for a character or squad owner, its faction as the owner faction (status `+0xe0`), job 187, and the stock `SHACKLES` put on (`SHACKLES-BODY` when a byte `+0xd0` of the boots section is 0). So a cuffed captive is a slave of the cuffer's faction at once, and 168's finder (`FUN_1408cdf50`, **Verified**: sense include 0x20 = slave state 1, exclude 0x210, owner faction = the carrier's, not carried, `+0x2f8` 0, down) takes it: row 170 lifts it (first aid first), and its goal state 115 holds while it is carried. Caging is goal 112, whose plan (108 Put in prison) needs the carrier to be carrying (47) and a cage from its finder; a non-prison cage then makes the cage's owner the slave's (`FUN_140330600`, **Verified**: town type not 7). Neither the 167 nor the 168 finder tests for a cage, so a slaver squad far from one cuffs, lifts and carries its captives along with it (Meitou: [captives](../simulation.md#captives)).
  The packages that list 167 and 168 are slaver, guard and workmaster packages ("addon- slave catching", "wandering slaver base", "slave worker day guard", the Rebirth and Holy Nation guards).
- **Looks like a slave**: processing (182) makes state 120 TARGET_LOOKS_LIKE_A_SLAVE true. The watchers' "needs processing" test `FUN_1408ca3d0` (**Verified**): a character with no owner needs it; one with an owner who is not an ally (`FUN_1406b2630`; same faction counts), carried, dead or an animal (vfunc `0x248`) never does; one with an owner of an
  allied faction needs it unless its slave state is not 2 or 3 and vfunc `0x3e0` is true and `FUN_1405a6bb0(platoon, character)` holds. That function (**Verified**, whole body): an animal is fine, NPC
  class 1 or 5 is not; when the watcher's faction lacks `allow slaves weapons` (faction byte `+0x30`), a held weapon or an item in a weapon section (back, hip) fails it; then the torso and leg slots (inventory sections 5 and 6) must each be empty, a slave garment (`stigma` 6) or an item of the `slave clothing` list of the faction of the character that owns the AI task system (the **watcher's**, `+0x240`, not the slave's owner; processing, `FUN_14035aa20`, reads the same worker-side list; the status-panel level `FUN_1405a51e0` is a separate function that keeps the owner list); then the shackle slot (section 9) must hold shackles (true; slot `0x2f0` is non-null only for `LockedArmour`, which returns the item itself, locked or not) or be empty (true only for `LockedArmour` on the torso on a shaven head, vslot `0x3e0`); so shackles count worn open too (Meitou `LockSystem.IsShackles`).
  The sections are `Inventory::getSectionOfType(AttachSlot)` (`FUN_140745ef0`): 5 body (Meitou `armour`), 6 legs, 9 boots, each read at its item. So an empty
  list passes only a slave in shackles, or a shaven one in torso shackles.
  Callers: `FUN_1408ca3d0` (needs processing: a slave of an allied owner is left alone when its head is shaven and it passes this test; Meitou
  `CaptiveSystem.NeedsProcessing` and `LooksLikeASlave`; **Observed**, KenshiOracle state 0.5 hold, Rebirth, N = 1: the save's 32 NPC slaves are all shaven, and
  the original's watchers processed only the two escaped player characters in an hour), the state-120 evaluator `FUN_1405a6f60` (not wired in Meitou), the sight pass's town-intruder test `FUN_140852240` (`SightCrimes`), and the escaping-slave walk test `FUN_140852750` (Escaping slave spotted, below).
- **Cage memory**: `Task_PutInSomething` for a cage (`FUN_140358670`) makes the prisoner's squad remember the putter as captor (`FUN_140673a20(prisoner, putter, 5)`) and calls `FUN_14067aca0` (Meitou `DialogueSystem.Forgive`, also run by processing a slave and by Task_ReleasePrisoner `FUN_1403570a0`: the prisoner forgives the releaser, and a booking releaser forgives the prisoner; **Verified** argument order), which
  adds nothing to a slave list: it clears the short-term tags TEMP_ENEMY (4), criminal (7) and aggressor (2) on the prisoner in the putter's squad memory, in the memory of every ally its sensor lists and in every platoon of its faction (`FUN_140289400` over `FUN_1406788d0`, **Verified**), clears the ENEMY bit of the prisoner's entry in the putter's sensor and empties a list of the putter's town. [Squad memory](dialogue.md#squad-memory) has the other writers. Engine choice (#599): Meitou's caging also clears TEMP_ENEMY on the putter's faction in the prisoner's squad memory (`DialogueSystem.Captured`). Left set (as the original does; **Observed** in the #599 save: 23 tags on Mongrel guards from the arrest 7 hours earlier), a released player character on Hold or with job 16 attacks its jailers on sight, which the police-hit rule books as an Assault.

## Desire and commitment

**Observed** (TaskData constructor `FUN_14061d350`, registrations in `FUN_140319a60` parsed for all 253 rows, scorer `FUN_14050df10`, `AITaskSytem`
vfunc 5 `FUN_14050caf0`). TaskData fields: +0x108 desire (a member function, nearly always a virtual of the `AI` object: `mov rax,[rcx]; jmp [rax+slot*8]`);
+0x160 target finder (returns a factor, mostly 0 when it found nothing, a constant for 38, 268 and 157 ([town goals](#town-goals)); the desire is multiplied
by it when the row has an effect, `FUN_14060fd80`);
+0x18 / +0x1c commitment minimum / random range in game hours; +0x20 commits; +0x34 crowd factor (default 1); +0x3c is read by the senses
(`FUN_1408529c0`).
+0x15 never finished (**Verified**, the registration's `*(short *)(row + 0x14) = 0x100` or `0x101`, the latter also setting +0x14): rows 20, 22, 25, 26, 31, 36, 37, 38, 39, 44, 47, 51, 60,
62, 97, 141, 146, 147, 149, 150, 153, 157, 165, 171, 175, 180, 222, 234, 236, 239, 249, 253, 258, 261, 268, 269, 289. The factory (`FUN_14032ebd0`) acts out 26, 147 and 165 with
`Task_Loot_Order`, `Task_Stumble` and `Task_TownStalker`, whose slot 4 does not read the byte; the other 34 with classes whose slot 4 is the base one (`Task_Blank` by default)
(**Verified**, the factory's cases and the vtables; Meitou `TaskDataFlags.NeverDone` on those 34). It is read by the
base `Tasker`'s slot 4 (`FUN_14032dee0`) and the move and door tasks' (`FUN_140332130`); the other classes' own tests ignore it (**Observed**: these two functions are the only
byte compares at `+0x15` in the exe's code). Readers and every class's test: [finished tests](#finished-tests). A row with an empty list A and no effect never holds
there (`FUN_14060f570` answers false), so for 180 Man the gate the byte changes none of the list-A tests.

- Score = desire x goal weight x crowd^n; x1.25 for the current task. For a committing current task the
  score is at least 0.4 until the commitment ends and at most 0.05 after it, so the character moves on. Then, while the running task (the plan's current step) answers
  slot 4 false and the score is below 1 (`DAT_14167c308`), x1.25 again (`DAT_14168ba14`; **Verified**, `FUN_14050df10`). Man the gate at its desire 1 and the Gate guard packages'
  weight 1 (val2 0, **Verified** data) scores 1.25 as the current goal, so this never applies to it in the base data. The job and order passes have no x1.25 at all (their
  keys are list places, `FUN_14050e5b0`, `FUN_140510920`). Meitou: [finished tests](#finished-tests).
- Commitment: when a committing task becomes current, end = now + min + rand x range. A candidate that is not the current task has its finder hand
  (Tasker `+0x38`) reset to none once it scores above 0, before it is chosen, and a candidate whose plan fails has it reset in the pick; its target `+0x10` stays in both (**Verified**,
  `FUN_14050df10`, `FUN_140510650`). So a newly chosen goal gets a fresh finder result. Only rows 35, 37, 38, 43, 47, 52, 65, 118, 127, 153, 157, 190,
  221, 239, 240, 248, 255, 268, 275 and 280 write the commitment fields in their registration; every other row keeps the constructor's 0 and never
  commits (**Verified**, `FUN_140319a60`, `FUN_14061d350`), so no town or captive duty goal commits.
- Desires (`AI` vtable slots): 19 = 1, 39 = 0.1 (Idleness), 40 = 0.25 (Patrolling, Patrolling town), 41 = 0.5 (Stand at node, Shopkeeper, Static guard,
  general node), 48 = 0.01 + 0.4 x rand (Wandering town, Staying home, Sitting around, Static town guard, Shopping 119). 125 rows desire 1; 102 call a
  function of their own (sleeping, following, retreating, turrets, Shopping 118: 1 until the shopping list is done, see its entry below).
- Shared desires (**Verified**: the desire member of each registration in `FUN_140319a60`, the thunks' AI vtable slots, `FUN_1405a33f0` decompiled):
  rows 98, 153, 175, 239 and 261 have Sleeping's desire (AI slot 30, [row 98](#slave-work-shout-and-capture)), so Sleeping on ground weighs danger and the last sleep
  as a bed does. Rows 31, 40, 44, 141 and 289 have Following's (AI slot 15): 0.5 (`DAT_14167e8d8`) times the followed character's StateBroadcastData
  `+0xa8` (`followMe`, KenshiLib name), 1.0 when the target does not resolve. `followMe` (**Verified**, the AI update `FUN_1405a4ae0`) is the
  character's current goal's TaskData `followMeWeight` (`+0x30` of the `AITaskSytem` `+0x1c0` goal's TaskData; 0 without one), raised to 0.1
  (`DAT_14167fad0`) when below. The weight (**Verified**, the 24 writes after their rows' registration calls in `FUN_140319a60`, read in the disassembly;
  the constructor `FUN_14061d350` leaves 0.01): 1.0 for 22, 24, 36, 39, 53, 101, 103, 127, 128, 129, 130, 165, 176, 192, 215 and 248 (patrols, wanders,
  travel, a squad fight, announcements, retreats); 0.5 for 35 Running away; 0.1 written for 19, 37, 43, 153, 239, 255 and 275. So a member follows a
  patrolling or travelling leader at 0.5 and one idling, at home or asleep at 0.05. **Observed** (KenshiOracle Sho-Battai hold, 2026-11-09): skimmer
  packs (`animal agressive patrol nocturnal`, whose Squad list holds Animal Sleep from its sleep add-on and Follow leader at the same weight) sleep with
  their sleeping leader, while members follow a patrolling one. Meitou: `AiSystem.FollowDesire` with `FollowMe` (each character's value stamped in the
  serial schedule step from its mind's goal, else the row of its running order, job or goal; `TaskDataTable.FollowMeWeight`) and
  `AiSystem.SleepingDesire` for 175. `AiSystem.FollowFactor` checks the resolved mission target, slave owner or squad leader, as movement does;
  leading its own squad does not prevent a slave from following its owner.
- More desire stubs (**Verified**, the registration stubs' bytes in the 1.0.68 exe, `jmp` to an `AI` vtable slot): 184, 202, 219 and 41 share slot 47 `FUN_1406415d0`
  (2.0); 155 and 197 slot 31 `FUN_140795f80` (1 while the squad's house-locked flag `+0xe8` is clear); 283 slot 71 `FUN_1405998b0` (1 while the home building resolves and
  is not public, else 0); 243 slot 26 `FUN_140597cb0`; 177 and 238 slot 62 `FUN_140597ae0` (0 while the posture is not 0, else 1); 58, 250 and 57 the stub `LAB_1400442d8`,
  a `jmp` to `FUN_140998410` ([medic jobs](#medic-jobs)). 270 Assaulting town slot 68 `FUN_140597a20` (stub `LAB_140003413`): 1.0 when the current town is a
  `Town` (`isTown`, vfunc `0x268`) whose owner faction is the player's (`+0x250`) and no ally of the character's (`FUN_1406b2630`), else 0. The registration
  sets `+0x120`, so the effect test holds without asking the finder (`FUN_14050c8e0`), and list A 90 holds on the goal's own hand, empty for targeting
  SPECIFIC (`FUN_140597160` is true without a target): the scorer's finished test (`FUN_14050df10` -> `FUN_14060f570`, `FUN_14050c8e0`) drops 270 as a
  candidate on every pass, so it never outscores 254 (weight 1.6) in `Cannibal raid assault town` despite its 2.0; it runs only as a provider of 90. 254's desire `FUN_1405a1280`: 0 while carrying or when two squad mates already carry; in its home town (status
  `+0x88` equal to the ownership town `+0x30`) the carried-body cage desire `FUN_1405a0f10` x the free-cage finder `FUN_1408d0d30`; elsewhere 1.0. With 254's empty target
  that cage desire is 0 while the running package's (`+0x48`) mission town hand `+0x8` is set and differs from the town it stands in (`FUN_1405944e0` compares the hands), else 1.0. 132's desire `FUN_14059a600` (exe bytes) has the same two zero tests,
  else 1.0. What the two make of 254's plan: [carry-loot 3.2](carry-loot.md#32-goals-and-their-target-finders).

| Type | Commitment (h) | Crowd |
|---|---|---|
| 37 Wandering town, 118 Shopping | 0.25 + r x 1.5 | 1 |
| 43 Staying home, 255 Reigning, 275 Sitting around | 0.2 + r x 0.8 | 1 |
| 38, 268 Static guard | 0.1 + r x 0.5 | 1 |

38 and 268 Static guard's finder (`FUN_14099edd0`, **Verified**; the registration stubs `FUN_14099f650` / `FUN_14099f670` call it with the indoors flag 0 / 1, which only reaches the node search; 157 has its own, `FUN_14099f1a0`) returns the factor 0.5 (`DAT_14167e8d8`) on its normal exit (nodes via `FUN_14099e920`) and 1.0 only on its visitor branch: a building of type 1, 3 or 8 whose faction is real and whose vfunc 0x290 holds, with a sensed (filter 2, 0x2a9) uncarried stranger whose home is not this building, then a node beside the stranger. The desire 0.5 is multiplied by it (row 38 has an effect), so a guard scores 0.25; **Resolved** (RE): the building's `+0xc4` is its `building designation` (set from the first resident squad's template, `FUN_14057f1f0`; 1 shop, 3 bar, 8 headquarters by the base templates), vfunc `0x290` the `public day` flag, faction `+0x1d0` the `not real` flag; the stranger filter takes NEUTRAL and leaves out ally, KO, 0x20, 0x80 and imprisoned, then non-sneaking characters standing inside a building other than the home; one is drawn uniformly and `FUN_14038ab50` (kind 2) gives the guard post nearest it. Meitou: `AiSystem.GuardFactor` (1.0 with `AiMind.GuardVisitorAt` read by `SpotFor`, else 0.5).
| 157 Static town guard | 0.1 + r x 1 | 1 |
| 47 general node | 0.03 + r x 0.1 | 1 |
| 153, 239 Going to bed | 2 + r x 8 | 1 |
| 190 Worker, 221 Operating machine | 0.5 + r x 2 | 1 |
| 240 Capture Target | 0.05 + r x 0.5 | 1 |
| 248 Attacking Squad | 0.3 + r x 2 (+0x20 and +0x21 set; **Verified**, the registration in `FUN_140319a60`; Meitou `TaskProfiles`) | 1 |
| 45 Bodyguard | - | 0.9 |
| 57, 58, 125, 237, 250 | - | 0.8 |

### Finished tests

**Verified**: which function is slot 4 (`+0x20`) of every `Task_*` vtable (`re/vtables.txt`), the factory's cases (`FUN_14032ebd0`), the constructors' 0 bytes and each byte's
writers (decompiled). **Observed**: the reader list (a search of the decompile for two-argument calls through `+0x20` on task objects), the clip readings (shares of a clip's
length), the meanings of state 9, of CharBody `+8` as the fight state, of the line start `FUN_140685750` and of the hand +0x80 as the campaign's lead. Slot 4 takes the task and
the character (AITaskSytem `+0x278`, the object `+8` also holds). Each reader gates a list-A test (`FUN_14060f570`, then the effect test `FUN_14050c8e0`):
- the scorer `FUN_14050df10`: a candidate that is not the current goal (another type, or either target hand differs: hand vfunc `0x10` answers "differs", as `FUN_140506b10`
  reads it) is dropped when its own slot 4 and list A hold; the current goal skips that test, and gets the second x1.25 while slot 4 of the running task (the driver
  `+0x270` → `+0x68`) is false and the score is below 1;
- the plan step `FUN_14050d970` (each AI update) and `FUN_14050f0b0` (AI slot 3 `FUN_1405965b0`, with a frame time; only while the goal does not commit or its commitment has
  passed) end the running task when its slot 4 and its own list A hold (`FUN_140513f50`, then `FUN_14050d7d0`);
- the job pass `FUN_14050e5b0` skips a job, and the order step `FUN_140510920` pops the front order, when slot 4 and list A hold; the order step asks the running task instead
  when it matches the front order (`FUN_140506b10`: same type and target).

Goals, jobs and orders are factory instances that never run (the planner builds new ones for its plan, `FUN_14050fe10`, `FUN_1405104d0`), so their own bytes hold the
constructor's 0. A byte raised together with the task's own end (CharBody slot 4 `FUN_1405c67e0`, AITaskSytem `+0x26c`) or a replan request (slot 5 `FUN_1405c6810`, `+0x26d`)
is acted on in the same character update (`FUN_1405c7c30`: CharBody slot 1 `FUN_1405c6d20` runs the task's update, then `FUN_14050d920` ends the task through
`FUN_14050d7d0` or replans through `FUN_14050d080`), so no chooser pass sees it set on a running task; `FUN_14050d920` is skipped while the character is down
(`FUN_1407d1440`), whose task update runs only on a branch of `FUN_140598c20`. The end through `FUN_14050d7d0` pops the front order when it is the
ended task's, with no platoon or chooser test, so a squadless character's ended order leaves too (**Verified**, `FUN_14050d7d0`).

| Class (rows) | Slot 4 | Never run | Running |
|---|---|---|---|
| base `Tasker` (all other classes) | `FUN_14032dee0`: +0x15 clear | as running | +0x15 clear |
| `Task_Move` (1, 29, 40, 67), `Task_MoveToDoor` and its four subclasses (82, 83, 121, 163, 199) | `FUN_140332130`: +0x15 clear and +0x94, set by every update (`FUN_140354600`, `FUN_140332910`, `FUN_140332c10`, `FUN_140332e40`, `FUN_140333170`), cleared by nothing | false | true from its first update |
| `Task_MakeAnnouncement` (103, 129) | `FUN_140333f60`: +0x78 (its line started near the target, `FUN_140685750`; update `FUN_140355770`) and +0x79 (the character's conversation then ended, `Dialogue::conversationHasEnded` `FUN_140671c60`); start and end clear both; an update that finds the conversation over but `FUN_140673830(dialogue, 2)` false clears +0x78 and announces again | false | true once announced, the conversation over and `FUN_140673830` true; it has no end of its own (list A 76 ends it) |
| `Task_MeleeAttack` (4, 227), `Task_FocusedMeleeAttack` (5, 61) | `FUN_140334230` → `FUN_14060cd80` on Character +0x648 (CharBody) → +8: CombatClass state +0x1f0 9 (no target), and no technique (+0x150) whose clip (`FUN_1405b4bb0`; 1.01 when not playing) is at 0.99 (`DAT_141696c78`) or less | same | same |
| `Task_Loot_Order` (26) | `FUN_1403343c0`: +0x7c, set when an update finds the melee threat flag (Character +0x2b0), with a replan request | false | false |
| `Task_Loot_AI` (3 on a character target, hand type 1; else `Task_Pickup`, base) | `FUN_140334460`: +0x78, set with its end once the take is done (update `FUN_14034d930`) | false | false |
| `Task_Loot_ResourcesWeHaveStorageFor` (241) | `FUN_140334590`: +0x78 after 3 s (`DAT_14168a254`, update `FUN_14034e2a0`), with its end (or a replan without a target) | false | false |
| `Task_StripWeapons` (181) | `FUN_140334600`: +0x78 after 0.8 s (`DAT_14168a178`) when the weapons are taken, with its end | false | false |
| `Task_ProcessNewSlave` (182) | `FUN_140334670`: +0x78 once the slave is processed (+0x79, after 3 s) and the character's conversation then ended, with its end (`FUN_14035aa20`) | false | false |
| `Task_GetUp` (52, 65) | `FUN_140334a10`: +0x79, set with its end: the get-up clip past 0.86 (`DAT_1416be6cc`), or at once without a clip or in a bed or cage (start `FUN_14033ca10`, update `FUN_140334a20`) | false | false |
| `Task_LiftSomething` (68, 170, 213, 225, 240), `Task_Kidnapping` (246) | `FUN_140334d40`: +0x7a, set with its end when the `shoulder lift` clip reaches 0.95 (`DAT_1416b77d8`, update `FUN_140354ff0`, which 246's `FUN_1403552d0` calls); the end `FUN_1403471e0` clears it | false | false |
| `Task_Stumble` (147) | `FUN_140334f80`: the character's AnimationClass (+0x448) has no stumble clip (+0x228) | same | same |
| `Task_TownStalker` (165), `Task_FollowAndTalk` (164; case `0xa4` always installs it) | `FUN_14033bc90`: the character's conversation has ended and so has its target's (or the target does not resolve), or its melee threat flag +0x2b0 is set | same | same |
| `Task_TravelToMissionTarget_ArmyFormation` (215) | `FUN_140349de0`: the leader of the character's platoon's campaign (the faction's war manager, +0x88, `FUN_140283500` → `CampaignInstance` slot 5 `FUN_140285820`: its lead hand +0x80, a character, or a squad's leader `FUN_140792b90`) is the character; else +0x78, which the update sets only for that leader once its mover arrived | as running (+0x78 0) | same |

Meitou (`TaskFinishes.Of`, `TaskFinished`): `TaskFinished.Fresh` is the never-run answer, asked by the done test of `PlanContext.TryPlan` for every goal but the current one
(the scorer's drop, `FUN_14050df10`). `TaskFinished.Unfinished` is the running answer, asked by the plan step for the running step (`PlanSteps`, [precedence](#precedence)) and
behind the second x1.25 (`AiSystem.Score` with the plan's step, `DecisionSystem.PickGoal` with `CharacterCold.Running`); where Meitou's plan names the goal, the act-out names
the original's task: a fight (`Attack`) is 4 or 227, or Task_RangedAttack's 262 (base) with `CombatSlot.Ranged`; a treatment 25 (60, 249 and 269 answer the same); a lift
(`PickUp`) 68; a loot its start's row; a task an engine reaction or an NPC's engine start runs its start's row (`TaskFinished.EngineRow`). Melee reads `CombatSlot.TargetSlot`
and `CombatSystem.Playing`; Stumble the hit reaction (`StumbleClip` until `StunUntil`); Talk `DialogueSystem.Talking` and `CharacterFacts.ImmediateThreat`; Formation the squad
leading its platoon's campaign (`CampaignInstance.Leader`) with the character its leader (215's list A, state 131: [StateType ids](#statetype-ids)); a move counts as updated
once its act-out has had a tick. The fight answer raises a running fight goal below 1 (the `goal-fights` golden scenario). Still different: no goal or plan step Meitou runs is
of the Stumble or Talk classes (147, 164 and 165 are engine systems), so their answers are never asked; of the Announcement class 103 is not acted out;
129's running answer is `AiMind.Announced` with state 76 (`TaskFinished.AnnouncementOver`, [campaigns](factions-squads-towns.md#campaigns)).

The order step and the job pass (**Verified**): the order step (`FUN_140510920`) pops the front order, read as the running task when it is the one running (`FUN_140506b10`),
when its list A, its effect test and its slot 4 hold, or when its desire x finder is 0 or less; the task's own end and a replan (`FUN_14050d7d0`, `FUN_14050d080`) pop the
order the running task matches too. The job pass (`FUN_14050e5b0`) skips a job whose task's slot 4, list A and effect test hold, and one whose row can run but neither acts nor
has an effect, unless TaskData `+0x14` is set (the registrations writing the short 0x101 in `FUN_140319a60`: rows 20, 22, 36, 37, 38, 39, 47, 62, 97, 147, 150, 157, 258, 268).
The effect test (`FUN_14050c8e0`) holds for a row without an effect (or with TaskData `+0x120` set), else when it has no finder or its finder scores 0 or less
(`FUN_14060f460`). An empty list A never holds. List A never pops a running order of an own-end class (26, 225, 246) and never skips a job of an own-end or move class.
Meitou (`ListTests`, `RowStates`): `OrderQueue.Prepare` pops a done front (`ListTests.OrderDone`, once per decision; a running one ends with `TaskEnd.Done`) besides the
act-out's own end (`ITaskHandler.Done`); the job step's `CanRun` skips a job by `ListTests.JobSkipped`. A list holding a state `RowStates` does not evaluate never holds; the
evaluated states are marked in [StateType ids](#statetype-ids). Rows this wires: the order rows 2, 5 / 61, 69, 72, 73, 76, 77, 78, 99, 107, 108, 140, 185, 186, 201, 226,
285, 286 (95, 96, 87, 110 and 221 never pop by it: 0 / 123 in list A); of the jobs, 45 Bodyguard is skipped while its list B (`!16 !39`, nobody threatened) holds, so its
companion 31 follows (**Observed**, #838's save headless: Miu's 45 skipped, her 31 following; a row with an effect and a finder, as 138, is never
skipped by it: its finder scoring 0 drops it anyway). A 29 Move pops by state 1 within 12 of its point, and its walk goes on: `Task_Move`'s end (Tasker slot 3
`FUN_1403547b0`) stops the mover only once it is done (`CharMovement` slot 12, then slot 19 `FUN_14065fc70`), so the next order's or job's start takes the walk over (Meitou:
`OrderQueue.PopDone` leaves the walk running). A walk that finds no path or is given up blocked has no path, so state 1 holds and the move alone leaves the queue
(`PlayerOrders.WalkFailed`). Row 3 is the ground item's pick-up only; the original's container orders are 26 Loot on the container and, for a walk to it, 29 with the zero
point, so state 1 never holds and it leaves by its task's end (Meitou: `ContainerAccess.ContainerOrder`, no `TaskPoint`; it leaves at the walk's arrival). The sleep order is 258 (**Verified**, `FUN_140319a60`:
list A empty, desire 1), so it never pops by list A; Meitou's 98 order (the saved 258) takes it.

The desire half (**Verified**, `FUN_14060fd80`; each row's function from its registration in `FUN_140319a60`, its stub followed to the AI vtable slot): TaskData `+0x108`
of the order's target and point, times the finder's score only for a row with an effect and a desire above 0 (`DAT_141681b38`); no order row has an effect. Slots 19 and 43
answer 1 (`FUN_140641550`, `FUN_1406415a0`); slot 39 0.1 (`FUN_140641560`: 76, 186, 201, 286). The rest: 3 slot 72 `FUN_140792650`, 0 while the character is down
(`FUN_1405c9e10`); 45 slot 44 `FUN_1405991c0`, 1 for itself as target or one sensed now, else 0; 225 / 246 slot 59 `FUN_14059e1a0`, 0 while carrying (`+0x348`, set at the attach
by `FUN_1405cff90`, which the lift update `FUN_140354ff0` calls past 0.4 of the clip, `_DAT_1416c16a0`, so a lift order leaves the queue there) or with the target
carried by itself, 0.5 for a target that is no character; 146 slot 35 `FUN_140598080` for the player's faction, 0 while carrying or Passive (the `+0x450` object's `+0x12c`) outside melee combat mode, else 1
(`FUN_140507c10` holds a 146); 25 `FUN_14099e0a0`, the need `FUN_140998200` with an organic kit (a player medic with none: 0); 60 `FUN_14099df10`, 0 for a medic out or dead
(medical `+0x161` / `+0x164`), else the need with a robot kit; 249 `FUN_14099dc40`, 0 for a patient that does not resolve or fights, or a medic out
or dead, else `needsSplint` of X = min(the medic's skill, the quality of the splint kit with the fewest charges in the first of its items and its backpack's holding one;
none: 0), 0.1 when a part qualifies, x the relation / 100 (at least 0.05) for another faction. The pop drops the queue's entry only (`FUN_140518ca0`); the plan step
(`FUN_14050d970`) ends the running task on its slot 4 and list A, or on its list B, never on the desire. Meitou: `OrderDesire`; a desire pop leaves the act-out
to its own end (a lift to its clip's 0.95). The decision step asks every stepping character with a front order on every tick whether it leaves (`PlayerOrders.PopsNow`), as the original's
order step runs on every AI update, and decides it then. 31 / 289 have Following's desire (slot 15 `FUN_1405a33f0`, [shared desires](#desire-and-commitment)):
0.5 x the followed character's `followMe` (at least 0.05), or 1 for a target that does not resolve, so a follow order never pops by it. 152 is no order in Meitou
(its operate orders are 87, 97, 124 and 146), so its desire (slot 21 `FUN_14059df00`) is not asked.

## Planner

**Observed** (class `TaskPathfinder`: search `FUN_14050f800`, entry `FUN_14050fe10`, pick `FUN_140510650`, node expansion `FUN_14051bae0`, provider maps
`FUN_14060fe30`, "can run" `FUN_14060f940`). Meitou: [simulation](../simulation.md#planner).
- **isAtLocation for a point target** (`FUN_1405a2450`, **Verified**): arrived when the mover reports no path or a finished one (`FUN_14065edb0`), or when the flat squared distance is under 144 and |dy| under 20. A finished path counts before any distance test. Meitou (`AiSystem.GoToSpot`/`WalkOver`, `AiMind.ArrivedAt`): a walk the movement followed to its last point (`WalkEnd.Finished`, no fight or flight) counts as arrival at that spot while the walker stays within `AiMind.ArrivedDrift` 20 of where it ended (engine: a lock round or job that walked it off ends the arrival); a walk with no path, blocked past the limit or stopped at a locked door, or one a fight or flight took, counts towards `MaxFailedWalks`; one another system stopped (a captive job's or round's stand) is neither, and is walked again (before, such a stop counted as arrival: #314's Slaver Boss stood in Sho-Battai's police station all day, **Observed** headless on the report's save). The vertical slack `FloorSlack` 6 is an engine choice (it tells floors apart).

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
  263, 266, 228, 235, 44, 253, 5, 61, 97, 258. *Restricted* (+0x41): 122, 270, 140, 76, 202, 185, 285.
- **Providers**: six registries, one per category flag bit (1, 2, 4, 0x10, 0x20, 0x40), each map a state and its polarity to the rows of that category,
  not +0x40, whose list A holds it, in ascending task type (filled from the registry's map keyed by type, `FUN_14060fe30`, **Observed**). Which registry a character plans with is its AI's repertoire (`FUN_14060e840`, **Verified**: the mode flags +0x200 pick +0x2a8, checked in the order 4 'imprisoned', 0x40 'crippled', 0x10 'chained', 1 'human', 2 'platoon', else 'animal'); cage entry (`FUN_140330600`) sets 0x10 when the cage's functionality `restrains movement`, else 4; `FUN_14060e8c0` clears the bits again (rows only flagged 0x20 are dog play and animal attacks). The posture setter `FUN_1405c7e20` (**Verified**) sets 0x40 for posture 1 (staying low) and 2 (crippled) and clears it for 0, 3 and 4. Meitou: `Repertoire.Of` (imprisoned 4, 0x40 from `MedicalState.StayingLow` or `Crawling`, chained 0x10, human 1, else animal 0x20); the planner (`PlanContext.Category`) plans with the same `Repertoire.Of`, so a crippled or staying-low NPC plans with 0x40 (no stand-up row) and an animal NPC with 0x20. Starts ask `TaskStart.Eligible(world, cold, row)` = `TaskDataTable.Of(row).Category & repertoire != 0`.
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
- **Pick**: candidates are tried by key, highest first (a taken key is raised by 0.0001 until free, so a tie goes to the later-listed goal; **Verified**, `FUN_14050df10`; per tier the lists are scored in the order: the entries of that tier in a platoon blackboard map whose hand is unset or names the character (`FUN_140269240`), its role list (`FUN_14026b790`), then its own, which setupAI fills 14, 32, then the faction's and the race's `AI Goals`, `FUN_140510d60`, `FUN_140621b70`. So at equal scores `empire vault prison`'s Leader 260 ranks before 183, `Cannibals at home`' 135 before 138, and in the occupy lists 177 before 276, 276 before 273 and 273 before 243); the first whose plan has a value above 0 becomes current, a candidate whose plan fails has its
  target cleared and the next is tried.
- **Source order**: [Precedence](#precedence).

### Ditching

**Verified** (`FUN_14051bae0`, `FUN_14051c240`, `FUN_14060f940`, `FUN_140344510`, `FUN_1405974e0`). Rows 113, 114 (provider of state 82, room, for 92 / 247 / 284 / 244) and 242 share one finder (`FUN_1405a59a0`, row 114 through the stub `FUN_1405a70a0`): the nearest storage that takes a carried item (finder
text above). A node's effect is always followed (a child for each provider of the state, whether or not it already holds); so with a storage target, whatever its state 62 (slot 149 of its StorageBuilding cast, virtual `0x2f8`) answers,
the plan is `93 Filling machine -> 114` (93 is the only row listing `!62` in list A; B: 2, 67, `!169`, isAtLocation). Row 93 moves the carried items into the storage and ends the first update that moves nothing.
- **Candidates** (**Observed**, read in the decompile `FUN_1405a59a0`, per carried item `FUN_14059acd0`, town query `FUN_1408cbea0`, not traced): the buildings of the character's current town
  (else the nearest town in reach) in the spatial query with mask `0x4002436` (functions 1, 2, 4, 5, 10, 13, 26 if one bit per function, as 184's `0x2004` reads; 9 BF_SHOP would not be in it),
  with a StorageBuilding cast (virtual `0x2f8`), other than the machine being served, owned by the character's faction (vfunc `0x58`; or, when `FUN_140856bc0` holds for the character, by a
  faction with the player flag `+0x250`), whose slot `0x4a0` takes the item; the nearest within the range mode's bound wins. Shop stock containers are mostly function 2 or 13 (barrels,
  crates, `Storage: Ore`; **Verified**, base data: only the three Shop Counters are 9), so when a shop's container is such a candidate, a worker's ditched output joins that shop's stock.
Meitou: `PlanContext.Find` gives rows 113 / 114 / 242 a `Targets.Store` target (`TownDutySystem.DitchStore`: the nearest container fixture or player storage of its faction or the town's, in its town, that takes an item
of `ProductionSystem.Ditchable`), state 67 is true for it while the character carries such an item, and a plan whose first step is 93 or the walk to a
Store target sets `AiMind.DitchRequest`; `TownDutySystem` runs a `Ditch` errand (walk, put in, `Replan`). A building shop's stock container is tested and filled as any
other; it holds the shop's goods, so the put-in items are for sale until the next restock clears them. So a worker of goal 189 whose inventory is full ditches before it empties the machine (92's B 82), and a
town food run (244) that finds no room ditches first (a2-039). Row 94's finder `FUN_14059d9b0` (the nearest machine whose output holds an item the target machine's inputs take, scored by `FUN_14059d000`) is modelled for machines (`ProductionSystem.InputSource`), see [NPC town machines](buildings-production.md#npc-town-machines).

## Animals in beds

**Verified** (rows from the registry transcription above, repertoire choice `FUN_14060e840`; decompile of `FUN_140598c20`, `FUN_140998550`, `FUN_140599e90`): a character lying in a bed (Character `+0x2f8` 1) has state 83 `isInBed` and state 26 true. The only providers of `!83` are the three Task_GetOutOfBed rows (116, 208, 257), all registered 0x41 (human and crippled repertoires); the animal repertoire (0x20) has none. Every row an animal acts on needs `!83` or `isReadyForAction` (37): the walks 1, 29 and 67 list `!83`, 28 Standing lists it, and the attack 227 needs 37, whose provider 59 Get ready needs `!26`, whose provider 28 needs `!83`. So an animal in a bed (put there knocked out by the player's Put in Bed, then woken) can plan nothing that moves or fights: it lies on with no goal, does not fight back and is hit freely. The exploit of hitting a hostile animal in a bed is this planner dead end, kept as is (**Verified** by the tables, **Observed** by the user's play reports; not checked in the running original). A human or crippled character plans 116 first and gets up to fight. Meitou: `Repertoire.StuckInBed` (animal repertoire, in a bed) makes `TaskStart.Eligible` refuse every row but Idle and 52, `AiSystem.FightPick` score 0 and `AiSystem.Act` do nothing, and `BedSystem.GetUp` leave the animal in.

## Precedence

**Verified** (decompile: `AITaskSytem::chooseGoal` `FUN_140510ef0`, order step `checkOrders` `FUN_140510920`, job pass `FUN_14050e5b0`, tier pass `FUN_140510d60`,
update `FUN_140511130`, `Character::setupAI` `FUN_140621b70`; KenshiLib names **Observed**). How orders, jobs and goals take turns.

- **One AI for everyone** (**Verified**, `FUN_14062af50` from Character slot 111 `FUN_14062b210`): every character, of any faction, human or animal, gets an `AI`
  (`FUN_1406236d0`, Character `+0x650`), a `CharBody` task driver (`FUN_140621010`, `+0x648`) and, through `AI::create` `FUN_140622110`, an `AITaskSytem` at AI `+0x20`
  (constructor `FUN_14050db70` → `OrdersReceiver` `FUN_140507d20`). The constructor turns the jobs switch `+0x35` on, leaves the default task `+0x190` and the current goal
  null and asks a replan (`+0x1b0` = 1); `AI::AI` sets the manoeuvre mode `+0x2bc` to 2 ROAM_FAR. In the chooser and planner the faction (`+0x250`, the player's) is tested only by the
  order step's failure text (source 1), the implicit jobs ([perma-jobs](#perma-jobs)) and the restricted providers ([planner](#planner)); around them by `setupAI` (goals,
  below), the faction change (Character slot 20 `FUN_1405cbac0`, which empties the character's own goal lists and runs `setupAI` again) and the order giver's permanent
  branch ([order givers](#order-givers)).
- **When**: the chooser runs on every AI update (**Verified**, disassembly: `AI` update `FUN_1405112b0` calls `FUN_140511130` whenever the AI's `+0x10` is non-null, which at `14051114a` tests the
  replan request +0x1b0 and either way reaches the chooser call at `14051121f`). AI `+0x10` is the platoon (Character `+0x658` → `+0x78`; `FUN_140507070` reads its Blackboard
  at `+0xf8`, **Verified**), so a character without one runs no chooser. Nor does a carried, knocked-out or dead one: the character tick `FUN_1405d00b0` then runs only
  `FUN_14050c7c0` (`FUN_140859220` and AI slot 9, no chooser) instead of the AI update, while carried (`+0x3d4`) or unconscious or dead (`+0x5b9` / `+0x5bc`) and not able to get up (`FUN_140598c20`);
  a dead one skips the rest of the update, and Character slot 27 `FUN_1405c7c30` skips the task-ended (`+0x26c`) and failed (`+0x26d`) handling while down (**Verified**).
  Without a request it first runs the plan step `FUN_14050d970` and the pick keeps the running plan; with one it deletes the driver's task (CharBody slot 12 `FUN_1405c6770`)
  and clears `currentGoal` unless it commits and has not expired, so the order queue's front is planned again from scratch and an uncommitted goal is scored again; a plan
  survives only updates without a request.
- **Replan requests** (**Verified**): `+0x1b0` is written by the constructor (1), the plan search `FUN_14050fe10` (0 at its start), the frame step `FUN_14050f0b0` (1 every frame
  no task runs or no goal is current) and `AITaskSytem` vfunc 3 `FUN_140506cf0` (1 unless `+0x1b1`, which is set while the chooser runs; it also clears the goal unless it commits
  and has not expired, `FUN_14050cdc0`). Vfunc 3 is called by: an order added (`FUN_140508380`, twice when the queue became one entry), a job or second-list add (`FUN_1405086d0`), a front
  insert (`FUN_140507a90`, the stagger), second-list entries of a type removed (`FUN_140507090`), a move order updated in place (`FUN_140508990`), a plan ended with nothing queued
  (`FUN_14050d7d0`), the failed flag's handler (`FUN_14050d080`), a commit row whose commitment passed (`FUN_14050f0b0`), `underMeleeAttack` (`FUN_140999f00`: the fight off, not
  getting up, and not moving with `FUN_140856bc0`, [combat](combat.md#fight-lifecycle-and-orders)), a hostile act or bolt (`FUN_140999d50`: the fight off, not getting up), sneak
  turned on while the goal is 16 (`FUN_1405ca9a0`) and, for a player-faction character only, standing order 3 sneak (`FUN_1405caa50`).
- **The plan's lifecycle** (**Verified**, decompile): a goal's plan is kept and run task by task. Each frame (AI slot 3 `FUN_1405965b0` → `FUN_14050f0b0`) the 20 s
  unreachable marks (`+0x218`) count down, a replan is asked when no task runs or no goal is current, the next queued task starts when none runs, and the running task ends when
  its slot 4 says finished, its commitment (`+0x288`, set for committing rows by `FUN_14050d130`) has passed or it does not commit, its list A (`FUN_14060f570`) and its effect
  test (`FUN_14050c8e0`) hold; a commit row 248 whose commitment passed ends at once. The update's plan step (`FUN_14050d970`, on the update without a replan request) ends the
  running task (the driver's `+0x270` → `+0x68`) when: (a) slot 4, list A and the effect test hold (`FUN_140513f50`); (b) the mover is idle (vfunc 0x50) and `isAtLocation`
  is false: the target is marked unreachable for 20 s (`FUN_140796ed0`, the blackboard told) and a replan asked (`+0x26d`); (c) it is a walk row (1, 67, 82, 83, 120, 121, 163,
  199, 267) and the next queued task's row can already run; (d) the hours (the plan's tasks carry the goal's, `FUN_14050fe10`) are over, or (e) its own list B no longer holds
  (`FUN_14060f940`), both unless the row has `+0x16`. The hours are the Tasker's `+0x64` / `+0x68` against the clock's hour (`*DAT_1421303d0`), given by the task
  factory `FUN_14032ebd0`; the order adds (`FUN_1405086d0`, `FUN_140508380`, `FUN_140507a90`) pass 0 and 100, so an order is never out of its hours. The end
  (`FUN_14050d7d0`) first pops the order queue's front when it matches the running task (`FUN_140506b10`), so an order whose list B fails while its own row runs
  leaves the queue; while a provider step of its plan runs (a door closed before it is locked), the row's own list B is not asked. State 1 of an object target
  (`FUN_14059c6d0`) is true when it resolves to nothing, else by the distance to its position; so (**Observed**, from that reach test, the carried body being at its
  carrier) a lift order's 1 holds once the body is on the shoulder, and its list B (80, 27, !26, 1) does not end it after the attach. Meitou:
  `ListTests.OrderNeedsFail` on the running front order, once its list B has held since the start (`OrderEntry.RowRuns`, standing for the provider steps being
  over, or list B held at the start): a list B entry `RowStates` evaluates fails (the walk's `isAtLocation` and a follow row's fight step left out); the task ends with `TaskEnd.Done`. The end (`FUN_14050d7d0`) then starts the next queued task (`FUN_14050d540`: a row that acts starts; one that does not is
  skipped while `FUN_14060f0c0` holds, all of list A and a list A or an effect, else a replan is asked), else asks a replan and clears the goal unless it commits and has not
  expired. The pick (`FUN_140510650`) keeps a candidate that is the current goal (`FUN_140506c00`: same type, both hands) while a task runs, without planning it again; the
  scorer (`FUN_14050df10`) gives the current goal x1.25, the commitment floor 0.4 or cap 0.05 and, below 1 while the running task's slot 4 says not finished, x1.25 again, and
  drops a candidate that is not current (its finder hand reset first, [commitment](#desire-and-commitment)) when its fresh slot 4 and list A hold, or when it can run but neither acts nor has an effect. After the
  chooser the update clears `currentGoal` when no task runs. So a walk ends as it arrives (its list A, `isAtLocation`) and the next task or a new plan follows at once; an
  animal following its leader (141, list B with 59) stops when its leader goes into a building that keeps animals out (59's default false; read with the mover's destination
  inside the leader's building, **Observed**). A fight step still ends only by itself (nothing left, target down, give-up), when its row stops being runnable, or when its goal scores 0 and the pick plans another over it ([leaving a fight](combat.md#leaving-a-fight)).
  Meitou: [planner](../simulation.md#planner).
- **Goal end** (**Verified**, disassembly; fields from KenshiLib `AITaskSytem`: +0x1C0 `currentGoal`, +0x20C its priority, +0x280
  `currentGoalExpiryTime` in game hours):
  - the plan step tests the hours (start < end half-open, overnight otherwise) and whether the row can still run, unless the running task's row has +0x16 set
    (`FUN_14050d970` reads TaskData `+0x16` of the task, **Verified**; set only for rows 12, 102, 159 and 161 in `FUN_140319a60`);
  - the plan end (`FUN_14050d7d0`) clears the plan and asks for a replan, and keeps `currentGoal` while it commits and its expiry has not
    passed (`TimeOfDay::timeOfDayHasPassed`, `FUN_140791c20`: an unset time counts as passed);
  - the update clears `currentGoal` and its priority whenever no plan is left after the chooser, committed or not. So a committed goal
    ends with its hours: the chooser, committed at the goal's priority, stops before that tier (sources 2 and 5) and plans nothing.
  - The setter (`FUN_14050caf0`) sets the expiry to now only for a new committing goal; every set adds min + r x range, so setting the
    same goal again extends it. Row 153 registers 2, 8 and the commit flag (`FUN_140319a60`).
  - **Observed** (KenshiOracle state 0.5 hold, Rebirth, N = 1): 24 garrison sleepers on 153 (23 to 11) had no goal and no order from
    11:00 and lay on in bed to the hold's end at 11.6 h.
  - Rebirth's `Guard town shifts -base` (55117) gives its Squad role nothing from 11:00 to 12:00: bed 153 runs 23 to 11, static guard, patrol
    and turrets 12 to 22 (**Verified**, record dump). Its town guards still walking to bed at 11:00 (#959: back from a fight off town) stand
    where they are until 12:00, then walk to their posts (**Observed**, headless probe of the #959 save).
- **Sources**, in order; the first whose pick has a plan above 0 runs:
  1. The front of the order queue (+0x40 deque, count +0x60): first popped when done (list A `FUN_14060f570`, the effect test `FUN_14050c8e0` and the Tasker's own
     finished test, slot 4, all hold) or its desire x finder (`FUN_14060fd80`) is 0 or less, at most one pop per update; then the new front alone is put in the emptied
     candidate map at key 1.0 and picked with the search flag ([planner](#planner)); a front that matches the running task (`FUN_140506b10`: type and target, the point
     for a row that needs a target and has none) is tested as that task. Since this step comes before the committed-mode stop, an order beats every goal, committed ones
     included, whenever it plans. The step is the same for every character (**Verified**, `FUN_140510920`): an order that fails to plan stays queued and the chooser goes on; its
     only faction test (`+0x250`) gates the failure text (vfunc 0x2d8), shown for a character of the player's faction that is not an animal (vfunc 0x248 null). The text is the
     line of the row's first list B entry that does not hold (`FUN_140610020`; each state's line is the fourth argument of its registration `FUN_140610170` in
     `FUN_140610250`, **Verified**: empty for 7 and 16, "It needs electricity." for 93, "My arm is messed up too badly to lift anything." for 80, Meitou
     `StateFailureLines`). Meitou (`ListTests.OrderBlocked`): an order whose first failing evaluated entry has no provider waits so; one with a provider starts,
     its act-out doing the step (a follow order whose target is threatened fights the attacker first, then follows).
  2. Committed mode (**Verified**: the update `FUN_140511130` works it out and passes it to the chooser: a current goal with the commit flag `+0x20`, `+0x26d` clear and its
     expiry `+0x280` not passed; the chooser turns it off without a current goal) with the running goal's priority (+0x20c) above 2: stop, the goal keeps its plan.
  3. The entries of a second list (+0x70, count +0x78, emptied with the queue by `FUN_1405074a0`) become candidates, scored by `FUN_14050df10`. It is not the one-shot order
     list: one-shot orders go to the deque. **What fills it** (**Verified**, the add function `FUN_1405086d0` and its callers): an entry added without the permanent flag, or
     with it when its TaskData row has kind +4 = 0. Without the flag only the queue loader `FUN_1405092d0` adds (a player-faction character's saved queue,
     [save](../formats/save.md#order-queue)); with it the perma-job loader `FUN_140508e10`, the permanent order path `FUN_1405c8da0` and the enslaving paths `FUN_14032e0d0`,
     `FUN_14032e590` and `FUN_14035aa20` (job 187, a perma-job and never in it). Who gives what: [order givers](#order-givers).
     The chooser empties the candidate map and scores this list into it (`FUN_140510ef0`, **Verified**). With a job list (count +0x90), the **urgent job pass** adds the
     candidates of Obedient Slave 187 (slave state 1, not sneaking: the owner platoon's Slave goals, [Obedient Slave](#slave-work-shout-and-capture)) and the kind-2 jobs (turret guard 234) while the AI's sensed-enemy count +0xbc is non-zero, and picks
     among them and this list's candidates; when that pick plans nothing, the candidates are cleared, this list's with them. With no jobs there is no pick here: the tier
     pass (`FUN_140510d60`) adds to the map without emptying it, so this list's candidates compete in the priority-3 pick of step 4.
  4. Priority-3 goals: the squad's (blackboard, `FUN_140269240` / `FUN_14026b790`) and the character's own (+0xa0 + 0x18 x priority), one pick. The squad's are the
     Blackboard's requests of the tier (subject none or itself) and the role list of the character's member type (`+0x25c`, set by `FUN_1406215c0`: 0 Squad, 1 Squad2,
     2 Leader, 4 Slave), the Leader list for its platoon's leader (`FUN_140792b90`); the requests are cleared at the chooser's start and after a failed tier
     (`FUN_140514100`) (**Verified**, `FUN_140510ef0`, `FUN_140510d60`). There is no campaign or faction branch: a campaign squad's members read its running package's lists.
  5. Committed with the running priority above 1: stop. Else **every job** in list order, then the implicit jobs ([Perma-jobs](#perma-jobs)).
  6. Priority-2 goals; committed with the running priority above 0: stop; priority-1 goals; the default task (+0x190, at key 1.0); else the mover reset (vfunc 0x98 of the movement object at Character `+0x640`, `FUN_14065fc70`). The default task (**Verified**:
     setter `OrdersReceiver` vfunc 4 `FUN_14050c030`, a fresh 14 Idleness with no target when given none) is reset by every order on `FUN_1405d20d0`'s queue path (not by its
     permanent-job branch), by the move order `FUN_1405d22b0` and by
     standing orders 8 / 9, by the AI's manoeuvre mode (+0x2bc): `FUN_140599970` (a point: the order's) and `FUN_1405966a0` (a target) make it 30 Holding position for HOLD_GROUND (0),
     31 Staying close for STAY_CLOSE (1), and idleness for ROAM_FAR (2, the default). The Hold toggle (standing order 12) does not touch it. Nothing else writes it: the
     constructor leaves it null and neither spawning nor `setupAI` sets it, and while it is null the pick skips its slot (`FUN_140510650` needs a candidate) and the mover
     reset follows.
- **Goals of a character** (`setupAI`): everyone 14 Idleness (priority 1, weight 0.1) and 32 Self preservation (priority 3, weight 1, target itself). A player-faction character
  (its faction's +0x250 set, `FUN_1407916d0`) also 243 Getting food (priority 3, weight 0.5) unless it has it; a player-faction animal also its race's `AI Goals` and 259 Picking
  up food (priority 3, 0.5), and loses 230. A character of another faction gets its race's `AI Goals`. Before that branch, any character whose platoon AI object (`FUN_140791ba0`: Character +0x658,
  +0x78, +0xf8) has +0x210 null gets its FACTION record's `AI Goals` (**Verified**, code; +0x210 read as the AI contract slot, null for an ordinary squad, **Observed**). The player faction
  (`204 Nameless`, the only base FACTION with the list) lists 15 Protect allies (AI_TASK 957, classification 3: priority 3, weight 1), so a player character holds goal 15.
  A list's type already held keeps its place and takes the later list's weight (the adder `FUN_140507950`, **Verified**). Meitou (`AiDatabase.OwnGoals`, `AiMind.RaceGoals`):
  an NPC's faction list while its blackboard runs no contract as the mind is made, then its race's (setupAI's order, `FUN_140621b70`). Hold adds 16 at priority 3; AI packages add theirs ([AI](../simulation.md#ai)). A player-faction
  character gets the same lists in Meitou (`AiSystem.PlayerOwnGoals`). `FUN_1405c9020` (asks a replan with the player-faction flag, then adds a priority-3, weight-1 own goal through
  `FUN_140507950`) is dead code: its only reference is an unreferenced thunk (`0x14000588a`; the other hit is `.pdata`), and Hold's case 0xc does not call it (**Verified**,
  disassembly cross-references). Priorities
  (`taskPriority`): 1 fluff, 2 non-urgent, 3 urgent, 4 obedience; orders are queued at 3.
- **So, for a player character**: an order beats everything, Self preservation included (an ordered medic keeps treating while hit; each landed blow staggers it and the order
  is planned again, [combat](combat.md#fight-lifecycle-and-orders)). Self preservation (while an attacker is within 181.7), Getting food and Hold's attack on sight beat every
  job but the urgent ones (a turret's row refuses a threat within 40 anyway). An order or job can still fight by its own rows: 31 Staying close needs `!16` and 45
  Bodyguard `!16, !39` (16 / 39: the followed character's / its own +0x2b0, a threat within 40, `FUN_14059de10`), whose provider is 13 Attacking attackers of, so a
  follower fights whoever threatens its leader and a bodyguard also its own attacker (**Observed**, from the row lists and the planner). A job higher in the list takes
  over at the next choice; the implicit jobs come after the list, Idleness last. Passive leaves
  Self preservation alone; the Jobs switch only gates the job passes. A fight is a task (4 `Task_MeleeAttack`) in a goal's or an order's plan; `CombatClassAI` steers it and
  chooses nothing; the fight ends with the task.
- **A follow order ends by its list B** (**Verified**, the original 1.0.65 through the Kenshi harness, `AITaskSytem` read with `kh peek`: deque count `+0x60`,
  running task `+0x270`→`+0x68`, `currentGoal` `+0x1c0`, the character's `+0x2b0`): Molly ordered to Follow Metaru (deque 1, front and running 31); Metaru
  ordered to attack a Dust Bandit; the frame Metaru's `+0x2b0` turned 1 while Molly's stayed 0, Molly's deque was empty and her replan asked, then she took
  15 Protect allies and fought; she did not follow again. Knocking out the followed character (Re_Dev damage) emptied the follower's deque the same way.
  Both match the plan step's (e) (`FUN_14050d970` → `FUN_14050d7d0`, which pops the front matching the running task and says nothing).
- **Threat tests of order rows** (**Verified** against the transcribed registry `TaskDataTable`, list B read row by row): of the player's order rows only 146 Turret gunner tests a
  threat (`!158`, its own +0x2b0; no row provides it, so the plan fails while a melee attacker is within 40 and the order waits in the queue). 225 / 246 lift, 2 Building, 95 Repairing,
  96 Demolishing, 87 Operating, 97 Training, 124 Hauling, 150 Prospecting, 25 / 249 First aid / Splinting, 26 Looting, 69 Putting down, 98 Sleeping, 76 / 201 Lockpicking, 108, 110,
  107, 5 / 61 attacks and 29 Move order list none of 158, 159, 16, 39; Rest (54) has no TaskData row. 31 needs `!16` and 45 `!16 !39`, which 13 provides, so they fight instead of failing.
  The providers a plan adds before these rows were not checked for threat states.
- **Meitou**: the player's characters are decided by one step in this order ([decision step](../simulation.md#decision-step)): source 1 the order queue
  ([simulation](../simulation.md#order-queue); one that cannot run stays queued with its text and the step goes on; a turret gunner's 146 ends under a melee threat and is taken up again
  once it is gone), step 2 has no source (no player goal Meitou runs commits), step 3 the urgent jobs (234 while an enemy is sensed; 187 Obedient Slave of an enslaved player character: its master squad's Slave goals 171, 189 / 190, 172, 41, 143, 35, 14, [Obedient Slave](#slave-work-shout-and-capture)), step 4
  picks among 32 Self preservation, 243 Getting food, a player animal's priority-3 race goals (277-279), 259 Picking up food, Hold's 16 and 15 Protect allies (joining a mate's or
  an ally's fight; none has a stepped plan: each is acted out by its engine system, fights by the combat system, 243 by `TownDutySystem`, 277-279 by
  `CaptiveSystem`, 259 by `FeedSystem`, as an NPC's same goal, so the NPC side's plan step has nothing to run for them either), step 5 every job in list order through its system's contributor (the switch and the repertoire first), then the implicit jobs, step 6 the priority-2 race goal 233 and
  14 Idleness (a player character's routine: it stands; the default task is never reached, as only ROAM_FAR is modelled). A task that ends leaves its character idle for the step to
  re-decide (the original re-plans; nothing resumes). A follow order running its row ends when the followed character is attacked or goes down, as the original's (**Verified** below); follow and bodyguard
  jobs, and a follow order given while its target is already threatened, fight by their rows: an attacker of the followed character within 40 (16), a bodyguard also its own
  within 40 (39), as a step of the row, and follow again when the fight ends. The step runs on the replan triggers above plus an engine re-ask at the think turn (`ThinkEveryTicks`) of a character
  idle, on a job's task or on a running order (no periodic replan was found), so a higher job takes over a running one within that period, where the original waits for its next replan. Goal 15 is
  `RetaliationSystem.JoinFight` at step 4 (below).
- **15 Protect allies for a player character** (**Verified**: desire `FUN_140599b90`, finder `FUN_14099ca10`, falloff `FUN_1409b2380`, constants from the exe): desire 0 when Passive or
  sneaking outside a fight; else the finder's best sensed ally (seen or heard, tagged ally) by its fight threat (`FUN_14099a190`, [combat goals](#combat-goals)) x clamp((1000 - d^2) / 990, 0.1, 1)
  (d the distance to that ally; the argument of `FUN_1409b2380` is the squared distance, **Verified**: 1 within 3.2 units, 0.1 from 30; no other distance limit), an ally of another
  faction only with Help Allies and then squared; x0.5 while carrying (`DAT_14167e8d8`). Its plan is 13 Attacking attackers of (attackers within 150 of the ally). Being above 0, it beats every job (step 5)
  whenever 13 finds an attacker, and loses to 32 Self preservation (1 or 0.7) when the character is attacked itself.
  **Meitou** (`RetaliationSystem.JoinFight`, `BestAlly`, `Plan`): the aggro system's senses stand in for the sensed map (entries tagged ALLY, not KO or imprisoned, seen or heard at the last look), the fight
  threat is `CombatSystem.FightThreat` (so an attacked ally that fights nobody counts), Passive gives the desire 0 in a fight too, the slave gate is asked once for the first slave met (the finder latches it), the best ally is the first in slot order on a tie,
  the result is capped at 1.2, the plan is `CombatSystem.BestAttackerOf` for the ally, and a character on Hold also needs `Orders.HoldPost`. It cannot start while the character fights on another plan or
  cannot fight; while its own fight runs with no ally left its desire stays 0.7 (engine: the fight ends by itself, once targetless with no threat left, [leaving a fight](combat.md#leaving-a-fight)). The step reaches it at the engine re-ask of an idle or job-task character
  (within a think period of a mate starting to fight) or at any replan; an order or a goal's task keeps it out. **Observed** (offscreen sandbox): a player character in a squad of its own 45 to 55 units away joins
  an ally's fight when nothing else wants it (run with the threat taken as 1). The second zero of `FUN_140599b90` is Passive (the byte `+0x12c` of the character's `+0x450` object, standing order 13; **Observed** that it is the same object as Hold's `+0x12b`). The old engine join (squad mates within 250 at a fixed 0.6) is gone.
- **The medic jobs' own threat tests** (**Verified** against `TaskDataTable`, lists read row by row): 58 Medic and 250 Splint Rigging have no list B; their treatment rows 25 First aid and
  249 Splinting need `38 23 !140 42 46 !157` / `38 151 !140 42 46 !157` (an arm, a kit, the target not swimming, standing still, at the target, weapon away): no threat state. Only 57
  Robotics needs `!39` (its own melee threat within 40), which 13 provides, so a robotics job under a threat within 40 plans a fight, as 45 does. The desire `FUN_140998410` drops to
  0.01 / 0.05 near enemies but stays above 0. So a medic job yields mid-fight only to step 4: Self preservation against its own attacker, 15 for a fighting ally.
- **A blow aimed at another** (a technique's extra hits in its zone) puts the hitter on no threats list: Self preservation's desire (`FUN_140599d20`) reads the senses' threats
  (AI +0x80 / +0x88 = SensoryData +0x58 / +0x60: characters whose intended target is the looker, positive aggression, within 181.7) and the +0x2b0 flag built from them; the hit's
  `underMeleeAttack` (`FUN_140999f00`) only replans (**Observed**). Such a medic fights only through 15.

### Order givers

**Verified** (decompile; flags read from the disassembly where the decompile drops stack arguments; a grep of the whole decompile, thunks included, finds no
other caller of `FUN_1405d20d0`, `FUN_140508380`, `FUN_140507cd0` / `FUN_140507a90`, `FUN_1405086d0` or `FUN_1405c8da0`). No path tests the receiver's faction
except where said.

- **Push** `FUN_140508380(receiver, type, target, point, clear, permanent)`: with clear, `FUN_1405074a0` empties the order queue and the second list and force-clears the
  current goal (the job list is kept). Then a permanent order whose row has a job kind (`+4`) goes to the job list with the [job add](#perma-jobs)'s remap and duplicate
  test; any other is refused when it has a zero point and an equal entry (type, target, point; `FUN_14050be30`) is queued, else appended at the back. Then a replan.
- **General giver** `FUN_1405d20d0(character, building, type, target, permanent, clear, point)`: (a) a permanent order of a player-faction character whose repertoire
  has the type and whose row has a job kind goes to `FUN_1405c8da0` (with clear: `FUN_1405074a0`, `FUN_140507530`, the [immediate update](#immediate-update); then the job
  add; 45 and 31 also run the circle break `FUN_1405c8c10`). (b) Else the check `FUN_1405d1940` may refuse it (below). (c) With clear: `FUN_1405074a0`, `FUN_140507530` and
  the immediate update. (d) `FUN_140791df0` stores where the order leads (Character `+0x428`..`+0x440`: the building given, else the target's building, else the point's
  gate code), the push, and the default task reset `FUN_140599970` ([precedence](#precedence) source 6).
- **Check** `FUN_1405d1940` (returns 1 = refused, the line said by the character, vfunc `0x2d8`): 4, 5, 16, 21 with no usable arm, 262 / 263 with both `+0x5be` / `+0x5bd`
  clear: "My arm is broken!"; 68, 105, 148, 170, 213, 225 when byte `+0x166` of the `+0x650` → `+0x318` object is 0: "I can't carry anyone with this arm."; 2 on a target
  whose materials test fails: "I don't have any building materials."; 25, 58 without a medkit (item kind 1) in the inventory or backpack: "I don't have a medkit."; 249,
  250 without a kind-2 item: "I don't have a splint kit."; 57, 60 without a kind-0xc item: "I don't have a repair kit."; 12 for a `dumb` character ("{1} can't talk to
  people" in the message log); 150 inside a building ("I need to go outside to do this.") or off solid ground ("I need to be stood on solid ground."); 31 silently on
  itself (else the circle break, and the platoon AI's `+0x260` = 1 when both share a platoon); 28 and 65 at posture 1 first clear the repertoire bit 0x40
  (`FUN_14060e8c0`). Every other type passes.
- `FUN_140507530`: the receiver's `+0x10` hand to none, the second list deleted, a current goal force-cleared; not the order queue.

| Site | Trigger and conditions | Receiver | Order | Clear | Perm |
|---|---|---|---|---|---|
| Move order `FUN_1405d22b0` (Character slot 99) | interface; default task set first; Shift with a mover that takes a waypoint queues nothing; a running 29 is retargeted in place when its gate route (`FUN_1402eb070`) holds | the ordered character | 29 on the target or point | not Shift | 0 |
| `FUN_1405c9130` (slot 98) → `FUN_140508990` | Shift-queued moves of a player-faction character (`FUN_1407f5f50`, `FUN_1407fa850`) | the same | 29 at the back, only when the queue is not empty (else only a replan) | 0 | 0 |
| Posture setter `FUN_1405c7e20` | posture to 0 from another, not crippled, no 65 queued; a direct push (no check, no immediate update, no default reset) | itself | 65 | 1 | 0 |
| Character tick `FUN_1405d00b0` | every tick while ragdolled WHOLE and able to get up (`FUN_140598c20`), with no queued test ([staying low](ragdolls.md#staying-low)) | itself | 65 (an animal, or posture 0), 52 (posture 1, 2), none (3) | 1 | 0 |
| Take hit `FUN_1404391f0` → `FUN_140507cd0` | a landed blow, the fight off, not getting up, unless a shackled defender's 25% knock-out took it ([stagger](combat.md#fight-lifecycle-and-orders)) | itself | 147 at the front | 0 | 0 |
| `Task_Kidnapping` start `FUN_14034ffa0` | the roll failed and the victim is in a bed (`+0x2f8` 1) ([kidnapping](carry-loot.md#kidnapping)) | the victim | 116 | 1 | 0 |
| `Task_EatPrisoner` update `FUN_140353c50` | the target is not a robot, not in a cage (`+0x2f8` ≠ 2) and getting up (`+0x278`), after one bite | the eater | 35 Running away, target the prisoner | 1 | 0 |
| `Task_ReleasePrisoner` start `FUN_1403570a0` | the releaser's faction has `+0x40`, or it is of the player's faction ([release](economy.md#115-bail-and-sentence)) | the prisoner | 133 | 1 | 0 |
| Slave purchase `FUN_1406add20` (DA 42's window) | each ticked bought character ([buying](economy.md#12-slaves-prisoners-animals)) | it | 133 | 1 | 0 |
| Bail `FUN_1406ae170` (DA 43's window) | each bailed prisoner: 128 only when its event-51 call returned 0, then always 133, which wipes the 128 | the prisoner | 128, 133 | 1 | 0 |
| Building bought `FUN_1407ad6c0` (slot 81, action 2) | each member of the owner faction's platoons housed there, after the platoon's new home is picked ([ui-screens](ui-screens.md)) | it | 19 on the new home; with none 22, and the platoon's `+0x115` = 0 (`FUN_1407ebdd0`) | 1 | 0 |
| Prospect button `FUN_140721800` | the Prospecting window closed; the check passes | the character `FUN_1407f4ba0` gives | 150 | 1 | 0 |
| Slave state to 1 `FUN_14032e0d0`, `setChainedMode` `FUN_14032e590`, processing end `FUN_14035aa20` | after `FUN_140507530` and `FUN_140507280` (the job list destroyed); the order queue is kept ([Obedient Slave](#slave-work-shout-and-capture)) | the slave | job 187 toward the owner `+0x328` | - | 1 |
| CharacterHuman setup `FUN_14062ef70` (slot 111) | race `is robot` (`+0x7c`), not of the player's faction | itself | job 57 through `FUN_1405c8da0` (kept queue) | 0 | 1 |
| Loaders `FUN_140508e10`, `FUN_1405092d0` | save load ([save](../formats/save.md#order-queue)) | itself | the saved jobs; the saved queue into the second list | - | 1 / 0 |
| Dialogue effects `FUN_140680560` | 16 calls, all permanent 0 and building 0 but DA 33's; details in [dialogue effects](dialogue.md#effects) | see there | DA 52: 67; 40, 30, 10: 213 then 112; 29: 240 then 112, else 4; 33: 67; 56: 29; 2: 102; 16: 164; 17: 165; 34: 280; 7: 19; 55: 248; 6, 57: 101 | 1, but 0 for each 112 and DA 56's 29 | 0 |

Interface givers: `FUN_1407f9e20` gives every selected player-faction character that is not getting up (`+0x278`) the order, clear when Shift is up, and sets its
`+0xec` ([staying low](ragdolls.md#staying-low)); `FUN_1407fae70` the nearest such character, clear; `FUN_1407fb410` the quick right click on a character (26 Looting);
`FUN_1407f5a90` the Shift job giver ([perma-jobs](#perma-jobs)). `FUN_1405c95b0` (would push 1 Moving) is dead code: its only reference is a thunk nothing calls.

### What clears the queue

**Verified**. The order queue and the second list are emptied (the job list is kept in every case) by:
- the ragdoll setter `FUN_1405d0db0` turning WHOLE (1) or CARRY (0x800) on, which knock-out, bleed-out and death request ([triggers](ragdolls.md#triggers)): it drops a carried
  body, deletes the running task (CharBody slot 12), runs `FUN_1405074a0`, empties the plan queue (`+0x300`, where the pick installs a plan, `FUN_14050d1b0`) and removes the
  character's Blackboard entries (`FUN_140269f30`); being picked up (`FUN_1405ce9c0` with 1) does the same wipe itself;
- every order given with clear, the get-up orders 65 / 52 above included;
- the talk tasks' ends: row 12's update and Task_TalktoPlayer's ([dialogue](dialogue.md#player-conversation)).

Not cleared by: the stop `FUN_1405c8330` (leg collapse, DA 6 / 7 / 34 / 55 / 57: it sets the task-ended flag `+0x26c`, calls the AI's vfunc `0x60`, empties the plan queue
`+0x300` and runs `FUN_140507530`, unless the current task's row has `+0x22`), enslaving (table above), the dialogue tail's `change AI` and `AI contract` (`FUN_140272430`,
`FUN_140272740`: they destroy only the members' job lists), or the body attach `FUN_1405ced90` (carry flag `+0x3d4` set, `+0x2f8` cleared, the running task marked done).
A queued order also leaves when it is popped ([source 1](#precedence)) or when its task ends: the plan end (`FUN_14050d7d0`) and the failed flag's handler (`FUN_14050d080`:
the target unreachable, the next task cannot start) pop the front when it matches the running task.
Meitou: the same for every character (`PlayerOrders.Wipe` at going down and being picked up, the body's orders' clear flag; the leg collapse, enslaving and bed or
cage entry keep the queue; [simulation](../simulation.md#order-queue)).

### Immediate update

**Verified** (`FUN_140791b40`, its thunk `FUN_14001a5b9`; consumer `FUN_140787940` → `FUN_1407875c0`): one global deque of character handles (`DAT_1421348a0 + 0x170`). With
true the receiver's owner is pushed to the front (while fewer than 201 are queued), with false to the back (fewer than 151); one already queued is not added. Each frame the
consumer pops at most two from the front, removes each from that frame's logic batch (`+0x1d8`) and runs its logic tick (Character slot 80 `FUN_1405d00b0`, which runs the AI
update) at once. Callers passing "is of the player's faction" (so the player's go first): the order giver's clear path, `FUN_1405c8da0`, `Task_Stumble`'s end `FUN_140335040`;
passing false: the failed flag's handler `FUN_14050d080`, `underMeleeAttack` `FUN_140999f00`, `FUN_140999d50`, the take hit `FUN_1404391f0`, the TEMP ENEMY tagger
`FUN_1405cb0a0`.

### Held characters

**Verified**. The Blackboard request add has two callers, both first aid's ([requests](ai.md#requests)); the chooser has no conversation test (`FUN_140510ef0`, `FUN_140510d60`,
`FUN_14050e5b0`), and Character `+0x2f8` is written only by bed, cage, floor-sleep and carry code.
- **Carried, knocked out, dead**: no chooser (above); while ragdolled or carried (`FUN_1407d1440`) or in a bed or cage (`+0x2f8` ≠ 0) the mover is not updated (`FUN_1405d00b0`).
- **In a bed or cage**: the AI and the chooser run.
- **Conversation**: the dialogue window pauses the world, so nobody's AI runs; nothing else holds either party ([player conversation](dialogue.md#player-conversation)).
- **Trade, looting**: nobody is held ([who trades](economy.md#who-trades)); `Task_Loot_Order` (26) and `Task_Loot_AI` (3) stop only the looter.
- **Being treated**: request 62 Standing still at tier 3 ([medic jobs](#medic-jobs)).

## Combat goals

**Turret goals' finder and desire** (149, 222, 234; finder `FUN_1408ccc70`, **Verified** structure; desire AI slot 35 `FUN_140598080`). The finder keeps the given turret when its component accepts the character in the same place (1.0), else takes the nearest unused turret around the character (`BuildingFinderTurrets_Unused`): factor 1.0 within 1500, else 0.3; in melee combat mode 0.8 within 600, else 0.05; none found, 0. Row 234 (man a turret) also re-picks the first unused turret when its own is taken or unusable (0.25 while a target was set). The desire is 0 when carrying (or flagged for combat outside melee mode), 0 beyond the squared distance clamp(AI+0x28 x 1.2, 10000, 1e6), 0 for a faction-owned character with neither job 146 nor 234 and no stat flag `+0x129`, 2.0 when enemies are sensed and it has a target, 0.4 for a hurt character (status `+0x318+0x60` below 2.1) sensing none, else 1.0. Meitou: `TownPlaceClaims.ClaimTurrets` takes, per member in order, the nearest turret no earlier member claimed to the character itself (the claims stand for "unused"; the claims are rebuilt each package cadence, so a taken turret is re-picked), `AiSystem.TurretFactor` for the factor and `AiSystem.TurretDesire` for carrying 0 / enemies 2.0 / else 1.0.

**Melee row gates** (**Verified**: rows 4 and 227 of the TaskData table above; state 139 `FUN_140598e90` is the character's water state 3, 140 `FUN_140598ec0` the target's; 78 is the target's `+0x2f8 == 2`). A person's row 4 needs neither itself nor its target deep in the water; an animal's row 227 has no swim test but refuses an imprisoned target and needs it within 500 (state 41). A person's providers of state 7 are 4 and the shooting row 262 (5 and 61 are not providers, `+0x40`; 262 needs `!me swim` and is never planned, [combat](combat.md#arms)), so the plans through 13 (Self preservation, the protect goals), 21, 34 and Assault (goal 101 / 248) have no fight step on a target in deep water, nor for a fighter in it; the player's orders 5 and 61 test only the target. The plan step re-tests a running task's list B against the task's own target hand (`FUN_14050d970` -> `FUN_14060f940`, **Verified** decompile; rows 4, 5 and 61 leave `+0x16` clear), so a fight ends once that target, or for row 4 the fighter, is in deep water. Meitou: `CombatSystem.SwimBarred` (a person's fight: target, or the fighter outside a player's pick; an animal never), tested by `AiSystem.AttackRowBlocked` and the pickers of 13 and the protect goals (factor 0), by `CombatSystem.Engage` for every fight start and by the attack order's `CanRun` and `StillHolds` (the order waits queued), and each tick on the fight's task target (`CombatSystem.SwimStopped`: the fight ends, an order's through the decision step; #778). **Verified** (decompile): the fight's own target choice (`FUN_140666ee0`, stored at CombatClassAI `+0x290` by `FUN_140665580`) writes no task hand, and `Task_MeleeAttack`'s update (`FUN_14034ca40`) neither, while the swim evaluators have no caller but their registration; so a fighter out of deep water may switch to a swimmer and keep fighting it (Meitou's `ChooseTarget` likewise). A person in deep water under threat has no fight chain (4 and 262 need `!me swim`, which no row provides; 5 is flagged `+0x40` and `FUN_14060fe30` skips it), so 32 plans 35 Running away (`SelfPreservation.Runs`).

**Verified** (registrations in `FUN_140319a60`, finder/desire stubs resolved by disassembly); readings of the finders' tests **Observed**.

- 45 Bodyguard (the NPC goal; `Task_Blank`): desire AI slot 44 `FUN_1405991c0` (**Verified**): 1 when its target hand is the character itself or the character senses the target now (its entry seen or heard, `+0x38` / `+0x39`), else 0; no finder, list B `!16 !39`, neither acts nor an effect, crowd 0.9. So while list B holds the scorer drops it (it can run and makes no task); while the target (16) or the character itself (39) has a threat within 40 the plan runs a provider first: 13 Attacking attackers of for 16, and 13, 35 Running away or 234 Turret guard for 39 (their list A), and the chooser's end gives the task system the goal's target, which the fight's target choice reads ([combat](combat.md#attack-slots-and-circling)) (**Observed**, from the planner rules and the row lists; which provider and whose attacker a plan for 39 alone takes is not decoded, so the [precedence](#precedence) reading that a bodyguard fights its own attacker stays a reading). In the base data it is `Bodyguard for leader` (target Leader) or `Bodyguard mission target` (SquadMission), urgent, in 32 packages (**Verified**, base-data census). Meitou (`AiSystem.BodyguardDesire`, `BodyguardPick`): the desire as decoded (sensed now: the last look's senses); its pick is factor 0 while neither the squad leader nor the character has an immediate threat, else the leader's best attacker (row 13's pick, the character counted), fought as the goal's fight. Engine choice: the SquadMission kind also guards the squad leader.
- 16 ATTACK_ENEMIES: effect state 6 `isNoEnemiesInVicinity` (registered without an evaluator). Finder `FUN_14059f3a0` walks the
  looker's sensed characters tagged SENSE_ENEMY and not KO, ally or 0x300 (mask 4, 0x308; [sense tags](perception.md#sense-tags-who-is-an-enemy); the KO bit is set for a carried character too, so one that came round on a shoulder is nobody's target: Meitou `AggroSystem.Targetable`; **Observed**, original 1.0.65, #1024 save: a thrall carried the awake intruder to the exit and nobody attacked it)
  that are seen or heard (entry +0x38 / +0x39), drops those whose faction the looker's own table coexists with (`FUN_1406b23c0`, **Verified**: one way, the looker's list only; Meitou `AggroSystem.AttacksOnSight`) and keeps those passing `FUN_140598630` and `FUN_140599f30` (the post
  and area tests, [perception](perception.md#the-attack-on-sight-tests)); `FUN_14059e300` picks the target. Before those tests an entry is skipped when its posture is not 0
  (vfunc 0x368; staying low, crawling) and it stands in a building (**Verified**, finder body; Meitou `MedicalState.Prone` and `SightMap.BuildingOf`), the posture test being replaced for a chained slave
  of the looker's faction (`FUN_14032e2a0`: chain flag `+0x320`, owner faction the looker's) by its slave state (`FUN_1405c8aa0`): skipped while 1, a target once 2 (escaped); also needed: global `DAT_142133f54` set or the
  target's campaign (`FUN_1409c4b90`, **Verified** by disassembly: its faction's war manager forces record for its platoon, none without a platoon) null or in a phase other than 0 (`+0xc0`): a campaign
  still marching to its target is not attacked on sight. `DAT_142133f54` (initial 0) has this one reader and no writer in the decompile (**Observed**). Meitou `AggroSystem.FinderDrops`,
  `CharacterFacts.MarchingToCampaign`. Also needed (first of all): the target's platoon (`FUN_140791b10`: Character `+0x658`, else its faction's default platoon `FUN_1406b9c60`; `+0x78`) is no
  `UniquePlatoon`: `Platoon` vfunc `0xb0` is `FUN_1407f22f0` (0) and only `UniquePlatoon`'s `FUN_1409b0680` returns the platoon (**Verified**, vtables); a `UniquePlatoon` is made from an
  UNIQUE_SQUAD_TEMPLATE or a saved PLATOON with `special` (`FUN_14057c6f0`), and no base record names either UNIQUE_SQUAD_TEMPLATE (**Verified**, probe), so only mods reach it
  (Meitou `CharacterFacts.UniquePlatoon`; a character with no squad counts as in none). The pick goes through the target score, so the [leave-alone test](combat.md#attack-slots-and-circling) applies too. Factor 1 with a target, else 0. This is the attack on sight.
- 144 TERRITORIAL_AGGRESSION_BUT_DONT_LEAVE_HOME ("Home territory aggression", finder `FUN_14059fd70`, **Verified** stub): SENSE_ENEMY
  entries (mask 4, 0x208) within 1.3 x the home town's radius (`FUN_140926d50`, factor `DAT_14169bb40` = 1.3, read-only; ground-plane squared distance, strict; a
  NEST_MARKER home contains nobody), or outside it in the looker's own building (**Observed**: the enemy's and the looker's building hands match, the looker's not null; the
  looker indoors), skipping a slave whose owner's faction is an ally (a slave of a neutral or enemy owner is a target); with no home town it is
  `FUN_14059fa70` (the same mask and tests, no circle). Meitou: the squad's town (`Blackboard.Town`), else its nest (a NEST town, 1250 x mult, round the nest centre; a save's wild squads have none), and `TownData.Radius`. 21 ATTACK_ENEMIES_AND_NEUTRALS
  (`FUN_14059f720`) takes mask 6 (neutral or enemy, 0x308) and drops a faction the looker's coexists with (one way, neutrals too); `FUN_1408569a0`
  (leave alone, also in `FUN_14059fa70`) then drops, for a looker whose faction has no player flag (`+0x250`), whose squad holds no TEMP_ENEMY on the target
  (`FUN_140678960` tag 4) and whose faction is not hostile to the target's (relation above -30, `FUN_1406b25d0`), a target with diplomatic status (`FUN_140796240` flag 0: NPC class 5,
  biome not factored in) or whose campaign is in phase 0 (`FUN_1409c4b90`); **Verified** by decompile and disassembly. The same test gates the target score `FUN_14059e300`,
  which every finder here picks by (16, 21, 144 with or without its circle, 34, 41, 101), with its player-faction branch ([target score](combat.md#attack-slots-and-circling)).
  Meitou `LeaveAlone.Spares`, in the score (`AggroSystem.Score`, `AiSystem.AssaultTargetFor`, `CombatSystem.Consider`).
- 143 PROTECT_OWN_SQUAD ("Protect close allies"): effect `!isAggressionTowards` (state 16, evaluator `FUN_14059de10`: the target
  resolves to a character whose AI-side object is set and byte +0x2b0 is set). Finder `FUN_14099d510` scores
  each squad mate (the platoon's active members, the looker included) that passes `FUN_140855960(sense, mate, 1)` (**Verified**: the looker itself; else a mate in its own sensed map that is seen or
  heard now; a mate missing from the map falls to a hand comparison; a far mate never counts) by its fight threat `FUN_14099a190` (below), the best capped at 1.2
  (`DAT_1416852f0`); with none above 0 it falls back to the looker character's Ownerships list (`FUN_1407956a0(+0x2f8)`, types 1 and 0x5b) at 0.1 x the threat, sensed now, no state filter (**Verified**; Meitou `AiSystem.OwnedFallback`: the slaves of the squad, `SlaveMaster`). The desire is that value (slot 19 = 1 x the finder): a weak pull for one attacker, a strong
  one for a gang. Achieved by 13 "Attacking attackers of" (list A `!16, !39, !12*`).
- The fight threat `FUN_14099a190` (**Verified**, disassembly; constants read from the exe), on the character's `CombatClassAI`: 0 when it is down (vfunc 0x30); 0.01 when exactly one attacker is
  on its engaged list (askers, at most 5) and that attacker's own list plus its refused-askers counter is above 1 (someone else fights it); else 0.25 n - 0.2 for n listed attackers (0 for none:
  1 gives 0.05, 2 0.30, 5 1.05), raised to 0.1 x its senses' threat sum towards it (AI +0xb0); then 0.01 when it is in a fight task and the value is 0 or less. So an ally counts while it is
  attacked, threatened or fighting, not merely while it "holds a target". Readers: 143's and 15's finders, row state 12 `isAlliesFighting` and `FUN_14099a3e0` (a platoon member above 0).
  Meitou (`CombatSystem.FightThreat`): n is every standing fighter whose target it is (not the engaged list), "someone else fights it" is that attacker's own count above 1, and the threat sum
  adds 1 per threat (the original adds the threats' aggression).
- 15 / 288 PROTECT_ALLIES ("Protect allies", "protect allies - dont leave town"; finder `FUN_14099ca10` at +0x160 for both, **Verified** registration):
  the same shape as 143 over the sensed entries tagged SENSE_ALLY (mask 1, 0x208, so neither KO nor imprisoned ones; Meitou excludes both: `SenseTag.Ko | Imprisoned`; same faction or relation >= 50) that are seen or heard, scored by the fight threat
  `FUN_14099a190` (no distance factor for an NPC looker; a player-faction one: [Precedence](#precedence)), capped at 1.2. A second loop runs only when the first found no ally above 0 (`plVar19` null): the entries of the Ownerships hand list `FUN_1407956a0(character)+0x10` (type 1 character or 0x5b squad) that the looker senses now, not down, slave state below 2, scored by the fight threat x 0.1 (`DAT_14167fad0`) and skipped when `FUN_140998790` holds (the candidate's own target, hand `+0x2a0`, is of the looker's faction or an ally, relation 50 `DAT_141683170`). The list is the platoon's ownerships (**Observed**: the medic finder's slave pass `FUN_14099f690` reads the same list as the squad's slaves; for the player it is the player's whole ownerships), so for an NPC squad its owned slaves; Meitou `AiSystem.OwnedScan` (slaves with `SlaveMaster` = the squad). 288 is registered exactly as 15 (**Verified**: the two registration blocks
  differ only in the task id; the same finder, desire and flags; no code compares 288): "dont leave town" is only the mod record's name. The same-town variant `FUN_14099cf80` is never registered
  (**Verified**, no reference in the exe). Desire `FUN_140599b90` (**Verified**): 0 while sneaking and not in combat mode, while Passive (in a fight too) and for an enslaved character holding job
  187; else the finder's **first** sensed ally above 0 (called with its stop-at-first flag: returned at once, not capped at 1.2; **Verified**, `FUN_140599b90` passes 1 to `FUN_14099ca10`), x0.5 while carrying. The chooser multiplies it by the finder's best (capped) value (`AiSystem.ProtectScan`). The same rule scores a player character's goal 15 (its finder adds the distance falloff and the squaring for another faction's ally): desire x factor, so a lone ally's value is squared. Meitou: one finder, `RetaliationSystem.ScanAllies`, for both (`AiSystem.ProtectScan`, `RetaliationSystem.BestAlly`). A slave or escaped slave (status int 1 or 2) is defended only when the looker passes `FUN_140997e80`
  (**Verified**, latched once a pass): its faction is a bandit one (fundamental type 8), or it is in no town, or that town's owner books no bounties (Meitou:
  `RetaliationSystem.SlaveHelpAllowed`). An ally of another faction counts for an NPC looker; only a player-faction looker
  needs the global +0x111 and squares the weight ([Help Allies](#squad-ai-options)). Achieved by 13 "Attacking attackers of"
  (`FUN_14099c3b0`, **Observed**): the protected one's attackers that it or the looker senses, within 150 of it (outdoors), are taken when either regards
  the other an enemy (slot 125) or the attacker's AI +0x2a4 equals the goal target's id (read as "it is attacking the protected one"),
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
  only when its squad's home town (`FUN_1407956a0` +0x30, **Observed**) is not the looker's town and its faction is not in that town's resident factions (`+0xf0`)), passing `FUN_14099b820`: not class 7; `+0xac` above 0, AGGRESSOR, or `+0xa0` above 0 with class 8 or an animal; and attacking
  (`FUN_1405ca6f0` → `FUN_1405c92d0`, **Verified**: the attacker's own posture below 3 (not playing dead, not out), its status `+0x68` target hand V, and the attacker in combat mode, or holding position (`CharStats +0x12b`) with V within 40 (`DAT_141685344`); V's own combat mode is not read) a victim V that the looker regards as an ally (vfunc 0x3f0) or an
  innocent (no bounty with the looker's faction, not class 7 or 8, V `+0x148` below 1, and the attacker's faction is a bandit one (`+0x34` 8) or `not real` (`+0x1d0`), or V does not regard
  the looker an enemy, is no animal and not class 6 or 8). V `+0x148` is `BountyManager::committingCrime` (crimes at Character `+0xf0`, field `+0x58`; KenshiLib 1ce4a16 layout,
  **Observed**): V has no crime in progress. So the police of a town defend a stranger, the player included, from bandits attacking it there.
  The best by `chooseBestAttackTarget` `FUN_14059e300`; factor 1. **Verified** (base data, the four official files): the
  `Shop` and `Shop-24hr` packages list 34 for the Squad and Squad2 roles (0-24 h, weight 1), next to 143, so every vanilla shop guard has it and the shopkeeper (Leader) has 143 only.
  Meitou (`AggroSystem.FindTroubleMaker`): both passes over the looker's sense entries (not KO, seen or heard or not) in its current town (`AiSystem.TownAt`); "intends aggression" is the TaskData
  intended aggression (+0x24, below) of the candidate's current goal (in a fight its fight row, else its AI goal, else its running row; `TaskAggression.OfGoal`) and +0xa0 that of its running
  row; the class-2 resident-factions test stands on the town's owner faction; pass 2's victim is the attacker's fight target while the attacker fights
  (`AggroSystem.AttacksProtected`; the hold-position variant is not modelled), an ally by faction or `AggroSystem.Innocent` (the bounty of the looker's faction, `CharacterCold.Pending`
  for `+0x148`, "regards the looker an enemy" read as V's faction hostile to the looker's); the area test is not modelled.
- TaskData +0x24, the intended aggression (**Verified**, registrations in `FUN_140319a60`; the AI's state broadcast `FUN_14050ed60` copies the current goal's to status +0xac, `getIntendedAggression`,
  and the running task's to +0xa0): 1 for 4 / 227 Attacking, 262 / 263 / 266 Shooting, 228 Taking out, 61 Attacking target unprovoked, 101 Assault, 248 Attacking Squad, 16, 21, 144, 41 Fighting for
  master, 146 / 235 Turret gunner, 131 / 244 food grabbing; 0.5 for 13 Attacking attackers of; 0 for 34, 15, 143, 288, 32 and most rows; negative for the lockpicking, medic, repair, robotics and
  engineer rows, 7 Sheathing and 35 Running away (-0.5; the others are not transcribed). The AGGRESSOR tag (short-term tag 2) is written only by the dialogue effects DA 7 GO_HOME and DA 34
  RUN_AWAY on their target and lasts 7 h (**Verified**, `FUN_140680560`), so a plain fight never sets it.
- 101 DEFEAT_SQUAD ("Assault on target squad", `targeting` 3 = the squad's mission target; row `Assault`: list B `!47`, effect 7; desire AI slot 19 = 1 times the finder's
  factor; carried by the packages `Assault target squad` and `(agg)`, signal 8, and by dialogue `AI contract`s). Finder `FUN_140793eb0` (**Verified**): the goal's target
  hand must be a character (type 1 or 0x22);
  (b) pass the area test `FUN_140599f30` (**Observed**, [perception](perception.md#the-attack-on-sight-tests): in the home town, within ROAM_FAR's 6517 of the character, or fighting it);
  (c) are not imprisoned (`+0x2f8` != 2, [carry-loot](carry-loot.md)); (d) are standing, character vfunc `0x368` == 0 (not crawling, not ragdolled, so not knocked out or dead).
  The kept ones go to `chooseBestAttackTarget` `FUN_14059e300` (the [target score](combat.md), which also drops ragdolls, allies by faction `FUN_1406b2630` unless slaves,
  the leave-alone cases and members it found no path to): the **best by score**, not the nearest, becomes the target hand. Factor `DAT_14167c308` = 1.0f
  (**Verified**, `data_values.txt`) with a pick, else 0. A pure 0/1 gate; the plan from there (a provider of `isTargetDown`, such as 4 Attacking) is the planner's.
  Meitou: `AiBehaviour.Assault`, `AiSystem.AssaultTargetFor`. Goal target = the contract's mission target (`StartContract`, from dialogue's `AI contract`); no mission target,
  or a goal not aimed at it, gives 0. Known = an entry of the looker's senses (`CharacterFacts.Knows`) or the mission target itself. The plan is the goal
  alone (engine choice: the walk and the weapon are the combat system's); the serial step sends a non-defensive `AttackOrder` to the picked member, the character alone
  (as dialogue's attack effects: its mates join through their own stance), and when the fight ends the next think picks the next standing member. Not modelled: the
  `+0x280` branch, entry age. 248 "Attacking Squad" has the same row shape and the same finder (`FUN_140793eb0`; its commitment is 0.3 + r x 2 h and it has no desire override): Meitou maps it to the assault behaviour of 101. No AI_TASK record of the merged base data has enum 248 (goal lists name AI_TASK records), so only a mod reaches it.
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
  (`42029-rebirth.mod`, package `fog prince`); its dialogue package `fogman` answers only events 52 and 53 and has no effects. **Verified**
  in the original with the Kenshi harness: in the #935 save a standing player 40-75 from its patrolling squads, and from one spawned with Re_Dev, was
  never attacked over 13 game minutes, while a Fogman of a `fog squad wandering` squad (21, attack enemies and neutrals) attacked it; in a fresh
  Wanderer game a naturally spawned squad's Prince and Heavies 0-150 from the player walked on, by day and by night.
- **Meitou, NPCs** (`AiSystem.Fights.cs`, [AI](../simulation.md#ai)): 16, 21, 144, 34, 143, 15 and 288 are priority-3 goals of `AiSystem.Choose`, scored like every goal (desire x finder
  factor x weight x crowding, x1.25 for the current one; in their hours). Desires: 16 1 (0 Passive, sneaking outside a fight, prone), 21 / 34 / 143 1, 144 0.5, 15 / 288 as above; the factor is 1
  with a pick for the attack goals (`AggroSystem.FindFoe`: the looker's sensed entries and its squad mates' within 2 R, the shared sightings) and the capped fight threat for the protect
  goals, whose pick is row 13's attacker (none: the goal fails). Engine gates: a character that cannot fight, crawls, runs away under Self preservation or mans a turret wants none. One whose
  lists hold no combat goal starts no fight and joins none: it only fights back when attacked (32 in the same chooser). Nothing else starts an NPC's fight on sight or pulls a mate in.
  - **Squads the AI does not fully run** (`AiSystem.EngineMinds`, `AiMind.Engine`; Meitou only): a loaded platoon a code mod holds (`HoldPlatoon`). Every other non-player
    squad has a blackboard and full minds, one whose template lists no AI packages included (empty role lists: 14, 32 and its own lists only, [squads without
    packages](factions-squads-towns.md#squads-without-packages)). A held squad's member weighs only the combat goals of its running package for its role and of its own lists,
    and Self preservation (fight or run), on its routine (standing, wandering, following; the mod moves the leader) and in its own fight, and acts nothing else out; a fight's
    end gives the routine back (the stand-down). A member whose full mind gives way to an engine one stops what the full mind had it doing: a sleeper gets up, one sitting
    stands, and a member other than the leader follows its leader again (`AiSystem.ToRoutine`, engine choice); one busy with something the mind does not hold then (a fight, a
    carry, a knock-out) rejoins on a later package step once it stands idle, as does any idle follower of such a squad (`AiSystem.Rejoins`). A mind dropped by trade,
    recruitment, capture or storage is made again at the next package cadence (30 ticks); meanwhile, and for an NPC with no squad (scripted worlds only), there is no mind.
  - **In the fight** (`AiSystem.InFight`): the fight a goal started is the AI's own (`AiMind.FightFrom`), and its fighter thinks in it as anyone does. The current combat goal is kept while it is not
    done (the plan step above): an attack goal while its senses count an enemy (`numEnemies`, 16's effect; the same taken for 21, 144 and 34) or its finder picks one, a protect goal while
    its finder values someone above 0 (143 counts its own fight, the 0.01 floor), row 13 not asked again; another combat goal chosen keeps the fight too (the fight re-targets by itself).
    No fight goal commits, so a goal of a higher score takes the fighter out: the serial step ends the fight (`AiSystem.LeaveFights`) and the next tick acts the goal out. Engine choice: a
    fighter also thinks on the 15-tick cadence, where the original only replans on triggers and lets the plan step end the goal.
  - **Pre-emption** (`AiSystem.Preempts`): the engine duties stand for goals and jobs of the same chooser below a priority-3 goal, so the combat goals and 32 are weighed for an NPC on one whose
    system lets a fight interrupt it: treating (`FirstAidSystem` lets the patient go), a town errand's walk (`TownDutySystem` waits for the fight and
    walks on; one carrying an intruder out or a body to a bed
    fights with the body on its shoulder, x0.5 for 15 / 288), a captive job's walk carrying nothing and its meal at a body or cage (`AiSystem.InterruptibleDuty`; `CaptiveSystem` gives the job up). The other duties (their systems do not survive a fight
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

- **127 Retreating fast / 128 Retreating** (desire AI slot 56 `FUN_1405a3710`, **Verified**): wanted when away from the status town `+0x88` and the way home is reachable (`FUN_1405a2710`); 0.01 (`DAT_141682ad8`) for a slave (state 1) that is not with the player (`FUN_140856bc0`), else 0.5 (`DAT_14167e8d8`). Meitou `AiSystem.GoHomeDesire` (owner path: the slave's owner faction). Further zeros (**Verified**, `FUN_1405a3710`): a character whose faction object has `+0x250` set (`FUN_1407916d0`, a player faction) gets 0 when either arm works (`FUN_140644150`, medical `+0x165` / `+0x166`), when `FUN_1404366b0` holds (its ranged controller `+0x2f0` has its flag `+0x36` set and the hand it names resolves to something not down), or when a weapon-slot item is not of category 3, 6 or 8 (the same veto as row 35 below; the AI plans for no player character in Meitou, so it is not modelled); and any character standing inside town walls (code above 0) whose gate state to code 0 is 3 (all ways out locked) gets 0 (`LockSystem.WallsLocked`). The walk (`Task_GoHomeTown` 128 `FUN_140338970` / `Task_GoHomeTownFast` 127, target `FUN_14033dd70`, speed mode 0 walk / 2 run): the position of the object in its squad's platoon hand `+0xb8` when that is set, else the ownerships' home town waypoint (Town vfunc `0x38` = `getPositionForWaypoint`, the point outside the gates, [factions](factions-squads-towns.md#off-screen-world)), else (no home town) a random offset round the character or an object looked up by the AI's hand. Never a building: Meitou `AiSystem.RetreatPoint` (`TownExit`).
- **53 Travelling / 130 Travelling quickly** (`Task_TravelToTargetTown`, **Verified**, registry `FUN_140319a60`): desire AI slot 57, the constant 1.0 (stub
  `LAB_140046371`, shared with 215); 53 achieves state 36 and 130 state 94, both `atDestinationTown` (one evaluator, `FUN_140507610`); 36 is in list B of
  39 and 192, 94 in 36 Patrolling town's, and no other row has either in list A, so 39 and 192 plan 53 and 36 plans 130. The evaluator: true without squad data; the squad's
  town (below, with the home fallback) against the character's status town (`+0x88`, none: false); no town: AI slot 11 (`FUN_1405a2450`) with the row's own target, false
  for one with neither hand nor point, which 39, 192, 36, 53 and 130 all are (their base AI_TASK records' `targeting` is 0, SPECIFIC). State 32 `hasDestinationTown` (`FUN_1405075d0`): the squad's town without the fallback is set
  (no row provides 32). So a squad with no override, campaign or running package has no 32, plans neither travel row and goes home by none; the fallback only lets 36 stroll
  round the home town while it stands in it. Meitou: `SquadTown.Reached`, `SquadTown.Of`.
  39 Wandering trader (desire AI slot 19, 1.0, stub `LAB_1400215ee`; the factory's default `Task_Blank`; registered without the act flag, `+0x14` and `+0x15` set):
  away from the squad's town its plan is `[53, 39]`; in it `[39]` alone, which can run and which the scorer keeps (`+0x14`), but it starts no task (`FUN_14050d540`)
  and the plan ends at once, so a leader whose best goal is still 39 stands in the town. 36's plan `[130, 36]` turns to 36 when 130's list A holds. Meitou: the plan
  steps 53 / 130 are `TownTravel`'s walk (`AiSystem.PlanStep`), 39 alone stands (`AiSystem.ActTravel`).
- **The squad's town** (`FUN_140269cd0`, **Verified**): the override's (`+0x210`) mission town, set or not; else the campaign's target when a town (hand type 0xd) or nest
  (0x55); else the running package's (`+0x48`) mission town, set or not; only with no running package and the caller's flag the platoon's Ownerships home town (`+0x30`).
  What each class's start (vtable `+0x30`) leaves in the mission town: the base start `FUN_1402856c0` the home town when there is one (the base package,
  `Contract_Timed`, `Contract_UntilExSlave`, `RetreatAndRefresh`, both prisoner gatherings, `Contract_UntilOutOfDanger`; occupation, the passing assault and the cannibal
  retreat run it first; occupation (`FUN_140288fe0`) and the passing assault (`FUN_1402900c0`) then the town their leader stands in, when it stands in one); the trader its
  drawn town (`FUN_140286740`); the patrols none (`FUN_140287060` only stamps the hour); the shippings the platoon's slot-`0x98` hand `+0x60` (`FUN_140287520`); the cannibal
  retreat, when the base start left none, the nearest town of its faction over both town lists (`FUN_140927bb0`), its nearest-nest kind that always (`FUN_140289b50`); the
  bars their bar town (`FUN_1402916b0`); `Contract_HiredAlly` and both base defences the player's town nearest the squad (`FUN_140289490`, `FUN_14028a170`); the war packages
  (`FUN_14028fc80`) the campaign's target hand (campaign vfunc `0xa0` `FUN_1409d4370`: its `+0x48` while `+0x6c` is 0, else none; `+0x6c` is 1 when the target hand is a character, kind 1, set as the instance is made, `StartInvasionData` vfunc 3 `FUN_1409d65e0`, **Verified**; Meitou's campaigns target only towns and nests), with no campaign the home town. Meitou: `SquadTown` (a nest squad's home town is its nest,
  `PopulationSystem.NestSiteOf`).
- **`Task_TravelToTargetTown`** (53 / 130; **Verified**, ctor `FUN_14033ace0`, start `FUN_14033ad80`, update `FUN_14033ae40`, speed functor `FUN_140342a30`; rows 53 and 130
  leave `+0x15` clear, so the base slot 4 calls them finished and their list A ends them on arrival, handing 36's plan `[130, 36]` over to 36):
  - Speed: the ctor's mode is 0 for 53, 1 for 130; the start replaces it with the running package's speed mode (package slot 3, even 4: 4 for the base class, the patrols,
    bars, cannibals at home, prisoner gathering, the trader, occupation, the war base and `Contract_Timed`; 2 for `RetreatAndRefresh`, `GoOutOnPatrol_Running`,
    `Contract_UntilExSlave`, `Contract_UntilOutOfDanger`, the passing assault and the war assaults; 1 for `Contract_HiredAlly`, both base defences, the cannibal retreats and
    the refugee; 0 for both slaver shippings). Each update the functor takes the package's mode when not 4, turns a carrier's 0 (`+0x348`) into 1, takes the platoon's
    `+0xb0` (the template's `force speed`) when not 4, and gives 2 to an escaped slave (the character's slave state, status `+0`, is 2) and to a squad that is not intact
    (`FUN_1407a5be0`). Mode 1 caps at 55, 2 at 999, any other at the mover's walk cap (`+0xc0`). Then, over the platoon's Ownerships members (an ordered set, the traveller
    among them), each resolved, uncarried (`+0x3d4`) one whose CharStats top speed (Character `+0x450` → `+0x17c`) is below the cap lowers it to 0.9 x that speed (`0x14168a25c`),
    so the result lies between 0.9 x and 1 x the slowest. The cap is the mover's desired speed (`+0xbc`). So under a package of mode 4 both rows walk and a carrier does not jog.
  - Walk: to the squad's town, or the detour once set (`+0x90`, resolved by `FUN_140792980`), through `FUN_1405c68a0`: a town's point is its waypoint (Town vfunc `0x38`,
    `getPosWithinChoppingRange` `FUN_14059bf30`, the town's slot 5 `FUN_1400d1d80` being false for town and nest kinds). Each update it applies the mover's own mode,
    then sets mode 2 (run) while AI `+0xb8` (SensoryData `+0x90`, [perception](perception.md)), the character's own goal aggression (status `+0xac`) or AI `+0xb4`
    (SensoryData `+0x8c`) is above 0 (`DAT_141681b38` 0.0; **Verified**); the functor's cap stays. Meitou: `TownTravel.UnderThreat`, `+0x8c` as one per fighting threat.
  - No town (none, or it resolves to nothing): the running package's start (vtable `+0x30`) runs again and the task ends with a replan request (CharBody slot 5
    `FUN_1405c6810`: AITaskSytem `+0x26d`, which the update turns into a replan, [finished tests](#finished-tests)).
  - No path for good (CharMovement `0x50`: path state 3 past the goal-offset retries, [pathfinding](pathfinding.md#walls)) to a town of type 0 (Town `+0xd8` is the TOWN
    record's `type`, read by `FUN_1409353c0`; 0 is a nest or camp) with no detour yet: the detour becomes the nest `FUN_140927ad0` returns, the nearest (X/Z) of the town
    list's second list (the nests, **Observed**), whose faction (vfunc `0x58`) is the character's (AI slot 1), the target left out; none found leaves the detour unset, so the update
    tries again. The time stamped beside it (`+0x88`) has no reader in the class. A detour reached does not end the task (list A tests the squad's town).
  - Otherwise no path (`0x48` false, state 3): `notifyPathImpossible` (`FUN_1405c6840` → `FUN_140796ed0`) puts the town in the character's own `pathFailures`
    (AITaskSytem `+0x218`, 20.0 s; each frame `FUN_14050f0b0` counts it down and, at 0, drops it once the navmesh links the character's polygon to the town's waypoint
    (`FUN_14065db60`), else re-arms 20 s) and calls the override's and the running package's vtable `+0x10` with the town (`FUN_140268a20`); then a replan request and the
    running package's `+0x10` again with an empty hand. `+0x10` is nothing for most classes (`FUN_140285870`), a new start for the trader (`FUN_140286730`, so a new
    town), and for the bars (`FUN_140295990`) a hand moved between their own reservation maps, which hold tables (**Observed**). The map's readers (`isPathImpossible` `FUN_1405b0290`)
    are finders of characters, buildings, furniture and items (**Observed**: none is asked with a town), so the mark changes nothing for travel.
  - Meitou: `TownTravel` (the walk, the functor, both exits, the detour in `AiMind.TravelDetour`; the package calls in the serial step, `AiSystem.RestartPackage`,
    `AiSystem.PathImpossible`; the cap as `CharacterCold.PaceCap`, members in squad order; the town mark is not kept, having no reader; the movement's carrier jog is
    skipped, `MovementSystem.CarriesOutAtWalk`). No path for good is the movement's no path, or a waypoint the navmesh does not reach whose provisional tail
    ([long routes](pathfinding.md)) the walker gave up after its retries (engine choice). A walk Meitou gives up for another reason (blocked, a door) is sent again, as the original's update re-sends its move.
- **129 Approaching fast**: desire AI
  slot 19 (`FUN_140641550`, 1.0; stub `LAB_1400215ee`), so at its target it ties with 215.
- **37 Wandering town** (`FUN_140999a60`): keeps a target that is still a general node; else one building drawn uniformly from the **public**
  buildings of the character's town in the zone it stands in (`FUN_1409fc6c0` over `FUN_1409fbb00`: the zone's building list, Building vfunc 82, which
  returns +0xc8, the `public day` flag, see [locks](locks.md)), then a random node of type 1 (NODE_GENERAL) in it whose owner is unset or the
  character's faction (`FUN_14038c2f0`); none (no public building, or the drawn one has no such node): 0. Effect `atNode`, acted out by 51 Stand at
  node. Vfunc 82 is false for some building kinds (1, 0x22) under a player-side condition.
- **47 general node / 20 Shopkeeper** (`FUN_14099f280` / `FUN_14099e920`): a node of that type in the home building, drawn uniformly at random among those whose owner is unset or accepted by the finder's functor (**Verified**, `FUN_14099f280` / `FUN_14099f1a0`; a kept target is reused while it is still a node; there is no reservation, a node another character stands at is not excluded). Meitou: `AiMind.NodeRoll`, drawn when a goal is taken up anew (`AiSystem.Pick`), beds stay round robin. Guard posts fall back to general nodes, nothing else falls back (a shopkeeper whose home has no shopkeeper node, or a general-node goal with no general node, gets an empty hand and the factor 1.0; **Verified**, `FUN_14099ec70`, `FUN_14099e920` retries only kind 2 with kind 1). The node kind is the node type alone (`FUN_14038c2f0`): 0 shopkeeper, 1 general, 2 guard post, 3 turret, 4 use, 5 light; the record name plays no part (**Observed** that +0xf0 is the node type; base data: only 'Node Defensive Position', 'Node Use' and 'Node Light Mount' are types 3, 4 and 5). Meitou `TownBuildings.NodeKind` (types 3 to 5 are `HomeSpotKind.Other`, a bar or throne name giving the engine's stand-ins). **255 Reigning** (`FUN_1408cbd50`): a BuildingFinder_SpecialFunction_Indoors of mask 0x1000000 (BF_THRONE, function 24) picks a throne furniture and returns 0 with an empty hand, else a constant factor (**Observed**: the registration is truncated; the finders of node types 1/0/2/3 never return 0); so with no throne the goal has desire 0. Meitou sits it on a `ThroneSeat` through the sitting-around claims (`ClaimSitting`, the same town and owner gates as 275). A node marker faces along its local −X: for 784 of 990 shopkeeper nodes in base-game town interiors,
  yaw − 90° points at the nearest Shop Counter's side the node stands on (**Verified**, layouts placed by `TownBuildings`, compared with each counter's
  yaw; the rest are corner or odd-angled counters). The engine turns a character arriving at a node that way.
- **Watching from a node** (`Task_StandAtNode`, row 51, the step goal 20 and the node goals plan; update slot 2 `FUN_140354b90` ->
  `FUN_14034d7c0`, **Verified** decompile): on every update at a node of type 0 (shopkeeper) or 2 (guard post) the character looks at the first
  entry of its sense list passing the tag filter `FUN_140853960(6, 0x688)` ([sense tags](perception.md#sense-tags-who-is-an-enemy): neutral or
  enemy; not down, NPC class 1 / 2, imprisoned or of its own faction), at the character's place now when the entry's `+0x39` byte is set, else
  the entry's place; with none, and at other node types always, it faces the node's orientation (`+0xc0`, CharMovement vfunc 0x30). The look
  (Character vfunc 0x3b0 `FUN_1405c9e70` with flag 0) first asks the AnimationClass (`+0x448`, vfunc 0xb8): `AnimationClassHuman`
  (`FUN_1405b8430`) turns the head with the `head turning` clip (ANIMATION `12610-animation.mod`) while the place is within ±90° of the
  movement heading and reports done; past that, or without the clip, the body turns to it (CharMovement vfunc 0x38, only while not moving,
  `+0x24`); the base AnimationClass reports done, so a non-human never turns. Meitou: `AiSystem.Watch` (entries in slot order, the original's
  list is a hash container's; the head turn is not simulated, so the body keeps its heading within 90°). It decides whom the barman can see:
  before it a player character behind him was out of view and never greeted.
- **38 / 268 Static guard, 157 Static town guard** (**Verified**, disassembly of 1.0.65; names from KenshiLib `AI.h`, whose RVAs are
  0x17b0 lower): 38 `findGuardNodeForBuilding_inOrOut` and 268 `_indoorOnly` call `findGuardNodeForBuilding_nearestPlayer(out,
  indoorsOnly)` (`FUN_14099edd0`); 157 is `findGuardNodeForTown` (`FUN_14099f1a0`).
  - **nearestPlayer**: when the home building (`StateBroadcastData` +0x48) has designation (`Building` +0xc4) BD_SHOP, BD_BAR or
    BD_HQ, is public (vfunc 0x290 `isPublic`) and the character's faction is real (`notARealFaction` +0x1d0 clear), it draws one
    of the characters it senses that are indoors (`isIndoors`, vfunc 0x1d8,
    compared with the home: **Observed**) and takes the home's guard post nearest them (`FUN_14038ab50`): factor 1, 0 with none.
    Otherwise, or with nobody drawn: `AI::findNode(NODE_GUARD_POST, home only, indoorsOnly)`, factor 0.5 whatever it finds.
  - **findGuardNodeForTown**: keeps a current target that is still a node, else `findNode(NODE_GUARD_POST, false, false)`; factor
    1 whatever it finds.
  - **`AI::findNode`** (`FUN_14099e920`): keeps the character's current node of the type while it is free or the character's (home
    only: still in the home). Home only: a random node of the type in the home held by no one or by the character (`FUN_14038c2f0`, the
    same holder test as the town draw below, **Verified**); for a guard post, outside indoorsOnly and when it found fewer than two,
    also the town list's nodes whose building (`+0xd0`) is the home: the gate posts below. Then a general node. Else a uniform draw (`FUN_14038b000`) over the node manager's list for the character's town (+0x50), of the nodes held
    by no one (holder type 11, null) or by the character; no type test. A found node is registered with the node manager
    (`FUN_14038a5a0`). None found: a null target, with which the plan cannot run ([planner](#planner), Can run, **Observed**).
  - **Observed** (KenshiProbe state 0.5 live check, Rebirth Gate Guards, N = 1): the 38 holders of each Defensive Gate II stand on
    3 or 4 guard posts of that gate, 60 to 120 from its centre, often two members on one post (or two close ones: the home search
    above holds a post for one character); no layout in the data has such nodes.
  - **Gate posts** (**Verified**, `FUN_14038b650`, called only by the door setup `FUN_14029cc90`): a gate's guard posts are made at run
    time, `n-guardpost` nodes (`1186-gamedata.base`) off its door points ([180 Man the gate](#town-goals)), put in the town list with
    the gate as their building; none is made where a node already stands near the point. The door setup fails until the door's physics
    body exists, and a per-frame world update (`FUN_1404cdad0`) retries the pending gates, so a gate has no posts until its area has
    loaded. A gate member that thinks before then finds no post: 38 cannot run and 157 wins, then holds through its commitment
    (**Observed**: after a save load in the original, three of the five Rebirth gate squads start on 157; the original's snapshots
    hold 54 characters for the first 132 s, 128 from 198 s). Meitou has every building at load, so its gate members start on 38: the
    start depends on the original's loading, not on a rule.
    The 157 holders walk to posts across the town, up to about 1400 away.
  - Meitou: 157 holds a guard post of its town ([simulation](../simulation.md#ai)); 38 and 268 take the home's posts, a gate
    member a flank spot; branch nearestPlayer and the factors are not modelled.
- **118 Shopping** (`FUN_1409997f0`, **Verified**): a shop of the town selling an item on the character's shopping list (`FUN_1409fc070`), else, only when the faction of the
  shopper's town has no player flag (`+0x250` clear; the list loop runs first and a hit skips this test), a random building (`FUN_1409fc1b0`): a town of the player's faction gets
  no random shopping, only list hits; none found: 0. Factor 1 for a BF_SHOP building, 0.4 otherwise. Then 119 (`Task_Shopping`) at it. The town is the AI status town (`+0x88`), and with none the finder returns 0 (**Verified**, disassembly: the null test at `0x140999868` jumps to the zero return), so a shopper shops only in the town it stands in: one standing in an outpost shops there, and one away from every town does not want to shop. Both finders scan every loaded zone's
  BF_SHELL_WITH_INTERIOR buildings; the random one keeps those of the character's town that are public (Building vfunc 82, the `public day`
  flag), the list one those that are shops (vfunc 87, `Building::isAShop`) and stock the item. `isAShop` (`FUN_1409986e0`, **Observed**, disassembly) is true for
  a shell with an interior designated a shop by its residents and public; for a building whose own function is 9 (BF_SHOP); and for a building with an interior
  owned by a faction with the player flag, holding a function 9 piece, and public, no residents needed. So a house the player owns in a town, set public with a
  Shop Counter built inside, draws the town's shoppers (**Verified** in 1.0.68: a Squin townsperson bought from such a counter in a bought house).
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
    second reset at every desire evaluation: both the list's last town (`+0x30`) and the AI status town (`+0x88`) set and different resets the done counts and records the day, as the day's reset does (**Verified**, `FUN_140998e40`; Meitou `ShoppingList.Pending` with the board's town, or the outpost the shopper stands in), so a shopper that finished in one town wants to shop in the next the same day.
    counter (1 at a new game, `FUN_14036cb80`) is **Observed**.
  - **Finder**: the entries in order, skipping those done; the first with a shop of the town stocking that function wins (a random one of them), else the
    random public building. A shop stocks a function when a function 9 piece (a shop counter) in it holds an item with that `item function` (`FUN_14054add0`).
  - **Buying** (`FUN_1409a14b0`, from `Task_Shopping` 119; **Verified**): `Task_Shopping` (start `FUN_14033a4c0`, update `FUN_140337d00`; the planner gives 119 the precondition `isAtLocation`, so
    it starts on arrival) holds a timer of 1.0 s, counts it down each update and calls the buy step when it is at or below 0; a sale resets it to 1.0. So the first unit is bought a second after
    arrival, then one a second. The step takes the first open entry whose function some seller in the building has an item of (a function with none is set aside for the call), a random such item, and adds 1 to
    `done` **before the sale**, so a buyer who cannot pay ("I can't afford that.") or has no room still uses the unit up. A failed sale tries again at once while that entry has units left (a new random item
    each time, so a broke buyer uses an entry's whole count in one call), then the next open entry not set aside; the call returns 1 after the first sale. A call that returns 0 ends the task: finished
    (`FUN_1400522c0`) if it had bought before, else impossible (`FUN_14001953d`, **Observed**: the AI re-plans). The sale `FUN_14074a630` (price,
    purse, what happens to the item) is in [economy](economy.md#npc-purchases).
  - Meitou (`ShoppingList`, `AiSystem.ShopFor`, `AiSystem.BuyForShoppers`, `NpcTrade`): the list, the desire, the daily and town resets and the stock-based pick (stock read from the containers of a building shop and of
    a house's stock containers, by item function; a trader's carried stock is not looked at, as `FUN_14054add0` does not). Arrival starts the timer (`AiMind.NextBuyTick`, hashed, not saved); a serial step in slot
    order buys one unit per second with the rules above (`AiSystem.BuyOnce`), moving the cats between the squads' purses and taking the unit out of the stock. A call that buys nothing ends the visit (target and shop
    forgotten, so the next think picks a shop again). A public town building the player owns, with an interior and a complete shop counter the player built in
    it, is a shop of its town; that counter's goods are on sale and the cats go to the player's purse (`PlayerShops`). The town is the shopper's status town (`CharacterCold.StatusTown`, worked out from where it stands for a character that holds none, so one the status pass has not reached shops too), as the goal's status town read gives, so a squad stopped in another town shops there and none walks home to shop (`AiSystem.ShopTown`). In an outpost: a complete building the player built in that outpost, set public, with a complete shop counter the player built inside, is a shop of the outpost (`TownPlaceIndex.OutpostShops`), walked to within 30 of its origin on any floor, with no random pick; a shopper of the player's faction (an autonomous squad) is left out and keeps its squad's town (engine choice). The fallback is a random public building of the town with an interior that is not a wall or ruin (`TownPlaces.Public`; the original's set, the buildings of class SHELL_WITH_INTERIOR with function id 19, is not read from data: **Observed**), not in a town of the player's faction (`AiSystem.RandomShopping`). The desire is the finder's factor (**Verified**): 1 when the pick is a shop (Meitou's shop set: trading residents, the layout's shop flag and the player's counter buildings), 0.4 (`ShopNoCounterFactor`) for any other building, 0 when none is found or nothing is left on the list. A shop is walked to as before (a node in it); another building within 30 of its origin on any floor, where the buy step finds no seller and the visit ends.
- **214 Relaxing** (`FUN_140998d10`): the current package's own target (package vfunc 1); none: 0. Effect `IS_WORKING`, acted out by 87 Operating
  machine, i.e. using furniture there; with no free one the plan fails and another goal wins.
- **Hang-around-bar package** (`Package_HangAroundBar`, start vfunc 6; **Observed** unless marked): its town (`FUN_1402916b0`, **Verified**) is the town
  of the platoon's home building, else its home town (platoon vfunc 0x98 `+0x30`), else the nearest town for its faction (`FUN_14004f13d`, mode 2,
  then 0); only while that town's zone is loaded. The town's buildings of designation 3 (BD_BAR, the residents' template's `building designation`;
  town vfunc `0x2d0`) are scanned cyclically from a random one; the first with a free **table** wins, that table is reserved for the squad in a
  global table→squad map (`DAT_14212ddb8`) and becomes the package's target; all bars full: no target.
  - The start needs the platoon's squad object (+0x1d8, null while unloaded) and the town's zone loaded, and sets the started flag (+0x150)
    only then; the target query (vfunc 1, `FUN_140293ea0`, what 214 Relaxing asks) runs the start first while that flag is clear (**Verified**,
    decompile).
  - **Observed** (KH 1.0.65, four runs of one Hub save from 10:20): the Hub's five bar squads (Mercenary guild, two Drifters recruit squads,
    Bar thugs, Hound Pacifier) all relax at the Storm House's tables for 3 hours, while seven `hub squatters` squads (package `Squatters`,
    signal 17, no 214 goal) block none of them. Meitou: a bar-class package takes a table at its start only when a goal of it is 214
    (`AiSystem.Rules.AsksForTable`; engine reading of the lazy start), so a package without one holds none and keeps its home. A squad away from its town whose bar town's zone is not loaded takes no table at the start and takes one on the package cadence once the zone is loaded (`Blackboard.BarWaits`, `AiSystem.Rules.RetryTable`), as the start above needs the zone: until then no member relaxes, so a caravan standing in no town, far from its home's bar, shops and strolls where it stands instead of setting off for that bar (a town's own squad is only in the world with its town; the wait is worked out again when the package starts at a load). Engine choice, not the town chain above: a squad that is not a town's own and stands in a placed town takes that town for its bar town (`AiSystem.Rules.BarTown`), so a caravan stopped in a foreign town sits in that town's bar.
  - The table search (`FUN_140292de0`, **Verified** by disassembly) asks the town (vfunc 0x2c8, `FUN_1409fc540`) for its furniture through a filter
    (vfunc 5 `FUN_1408ca670`). It keeps a piece:
    - of the package's town, and of a faction not hostile to the platoon's (`FUN_1406b26d0`);
    - inside the bar when there is one (`isIndoors`, vfunc 0x1d8);
    - **indoors for the normal kind, outdoors for the outdoors kind** (package flag +0x151, BarOutdoors);
    - of function 16 BF_TABLE, 11 BF_CORPSE_DISPOSAL or 4 BF_REFINERY. A refinery passes only when the object of its vfunc 0x300 has 13 at
      +0x3f8. A table or corpse disposal inside a building needs that building to be the bar, public (vfunc 0x290), or
      to pass an owner test.
  - With no bar the search then drops every piece inside a building. With its flag set it drops the pieces in the table→squad map, the only
    reservation it reads.
  - So a town without a bar gives the normal kind (signal 17) no table: its filter keeps indoor pieces, and the no-bar pass drops them. Only the
    outdoors kind (26) takes a free outdoor table. A bar holds as many visiting squads as it has free tables.
  - **Observed** (KenshiProbe state 0.5 live check, N = 1): a Holy Prisoner Caravan on signal 17 in Rebirth, which has no bar, shops all hour
    and never relaxes.
- **36 Patrolling town** (`Task_PatrolTown` slots 1-3 `FUN_140336370` / `FUN_1403364b0` / `FUN_1403365b0`, **Verified** by the decompile and the `FUN_1405c6b30` mover; row 36 has no finder or
  effect, registration in `FUN_140319a60`, and no commitment). The slots get the character's `CharBody` (the task driver, `CharBody` slot 1 `FUN_1405c6d20`, calls the task's slot 2 with it),
  whose vfunc `0x48` is the character (`FUN_1406210d0`) and `0x40` a constant true (`FUN_1406210c0`; no class overrides it), so the town branch always runs (an earlier reading of `0x40` as a target
  test was wrong). Start and update (the update only when the mover reports idle, reached or failed; resume's mode 2 sends nothing) give the mover the platoon's town hand (`FUN_140269cd0` with the
  home fallback: the override's mission town; else the campaign's target when a town or nest; else the running package's mission town, set or not; only with no
  running package the Ownerships home town; resolved by `FUN_1409f9100`, which takes a hand of type 13 TOWN or 85, a nest's (**Verified** by the decompile), so a nest squad patrols round its nest (that a nest platoon's Ownerships home is its nest is inferred from its save `hometown`, **Observed**); Meitou `AiSystem.PatrolArea` via `PopulationSystem.NestOf`, #771). With a town the move is an area order on the town's position with scatter
  max(500, 0.5 x the town's radius, Town vfunc 84); with none, on the character's own position with scatter 1250 (`DAT_1416f190c`). `setPatrolInput` draws the destination within ±scatter/2 in
  X and Z (redraws: [patrol moves](factions-squads-towns.md#off-screen-world)). So a resident strolls within ±625 of a Town's centre (±250 for a Village), after its list B is met (`atDestinationTown`).
  Meitou (`AiSystem.PatrolArea`, `PatrolPoint`): the squad's town (`SquadTown.Of` with the fallback), else the own position; the first of 10 draws on
  walkable ground that passes the building test of the [patrol draws](factions-squads-towns.md#off-screen-world), a new draw each time a walk is over.
  **Observed** (headless, #370 save, The Hub): before, residents on 36 drifted to 2479 from the centre; after 3000 ticks they end 248-556 from it.
- Building layouts' `is bar` / `is shop` are 0 on every exterior layout of the base game and mods checked (2 interior layouts set `is bar`, 9 `is shop`),
  so they do not mark bars and shops (**Verified** over the merged `interiors.level`).
- **43 Staying home** (`FUN_1408cbc00`): furniture in the home whose BuildingFunction is in mask 0x2049a (bits MINE, RESEARCH, REFINERY, TRAINING,
  CRAFTING, CHAIR; reading the mask as bits is **Observed**). Both it and 275 lead to 221 (pretend operating).
  **Verified** (decompile 2026-11-03): with no target set the finder lists the home interior's buildings that pass the mask (`FUN_1408cbad0`), returns the factor 0 (goal not wanted) when none
  passes and otherwise draws one at random (`FUN_1409fb020`; no claim test in this finder); a target already set gives 1.0. The scorer clears the
  target of every candidate that is not the current goal (`FUN_14050df10`, **Verified**), so a resident not on 43 draws again each time it thinks;
  Meitou draws again at each package cadence while 43 is not the member's current goal (`TownPlaceClaims.ClaimSeat`, the roll keyed by the tick).
  The home is the squad's own building, also under a bar package (**Observed**, 1.0.65, three runs of one Hub save, 10:20 to 13:20: the `hub squatters`, package
  `Squatters` of signal 17, stay at their ruins' campfires and stools, within 120 of where they started), and the furniture its residents' nest stood round it counts
  ([zones](../formats/zones.md#nest-debris-and-loot); a ruin has no interior of its own). Meitou: `HomeSpotKind.Chair` (spots), `Training` and `Workstation`
  (MINE, RESEARCH, REFINERY, CRAFTING pieces of the interior layout; `TownBuilding.Furniture`), then the nest's pieces (`NestBuildings.FurnitureOf`), of `Blackboard.OwnHome`
  are the candidates; `TownPlaceClaims.HasStayingFurniture` is the desire (0 without any), members draw a free
  piece at random (`FreeFurniture`; distinct pieces per member is the engine's claim, **Observed**), sit on a chair, stand at a dummy, or stand at a workstation facing it at the machine's work clip and
  hunger rate (`PretendOperate`: 221's look). 221 runs `Task_OperateMachine`'s update there (**Verified**, `FUN_14035bc90`: `tryOperate`, then
  the operating XP `FUN_1408c7250` for any function but 7, then the building's work step, slot `0x4b0`), so the resident gains the piece's operating
  XP and at a dummy its training; at a workstation Meitou runs `TownOperate` on the layout object's own machine (`ProductionSystem.HomePretenders`,
  [layout objects](buildings-production.md#npc-town-machines)), so a refinery's step draws its inputs into its output and a
  hydroponic farm's harvests; a research or crafting piece's step makes nothing for an NPC owner (no player flag, no queued craft).
  A piece without power gives 221 no plan: its list B needs 93 and no row provides it, so once the resident is home (18) the goal has no plan
  and another wins (**Verified**, the row table). Meitou: the claim marks such a piece (`Blackboard.UnpoweredSeats`, `TownPlaceClaims.Claim`),
  93 on the seat target reads it, and the resident walks home instead of to the piece (`AiSystem.Adopt`); at home 43 has no plan, and the next
  choice once its commitment ends takes another goal. **Observed** (headless, METARU): a Reaver Camp Neophyte (no generator) whose claimed
  workstation draws power stands at home under 43, then patrols the town; none stands at such a piece.
  **Observed** (headless, a Hive Village of the base data): the Worker Drones staying home operate their hut's tanning bench, campfire and other
  refineries; with empty inputs nothing is made.
- **275 Sitting around** (`FUN_1408caf00`; the registration's finder thunk `LAB_1400061db` is not in the RE files, so the row link is **Observed**; the rest **Verified** by decompile):
  - Gate: 0 (not wanted, no target) when the character is in no town location (character vfunc `0x78`). A non-player-faction character (faction `+0x250` clear) needs that town to
    be its squad's home town (Ownerships `+0x30`) or owned by the squad's faction (`FUN_140621390`); a player-faction one needs a town owned by a player faction, with no home test.
  - Scan (only with no target yet; one already set keeps factor 1): the town's own furniture (Town vfunc `0x2c8` `FUN_1408d10f0`: the buildings of the town's registered zones
    whose town it is; not a 3x3 round the character) of mask 0x1020080 (TRAINING, CHAIR, THRONE), 0x1020000 without TRAINING when `FUN_140856bc0` is true (the faction has `+0x250`,
    or the character is held by such a character). Slot 0 of the finder (`FUN_1408cebe0`) keeps pieces the squad may use (`FUN_140621230`), not on the character's avoid set, with
    a floor-reachability test; the first whose owner faction is the character's or the squad's and that no one else claimed (`FUN_1402690e0` < 1) is the target; none: 0.
    The list order is the zone map's (arbitrary).
  - Pieces: BF_CHAIR and BF_THRONE are sat on ("Chairs" below), BF_TRAINING (the dummy, function 7) is pretend-operated. 43's finder is the `Indoors` variant of the same class (vtable
    slot 5 `FUN_1408cbad0` lists the home's interior), so 43 takes training furniture and chairs but no throne (mask 0x2049a); 275's is the plain one, whose town scan also reaches furniture
    inside buildings (**Observed**: the bar-table finder, same scan, tests "inside the bar").
  - Meitou (`TownPlaceClaims.ClaimSitting`, on the package cadence): no piece claimed means the goal is not wanted; a squad does not sit where it stands. The town gate and the owner test above are
    applied as written, with the town the member stands in (radius test) and the building's owner = its property owner, else the level state's `owner faction ID`, else the town's faction;
    the player-side branch applies to an autonomous player squad's members, who also sit in the outpost they have as their town
    ([autonomous player squads](../simulation.md#autonomous-player-squads)). Pieces: a free chair, throne or (not for the player's side) training dummy in any building of that
    town (`TownBuilding.SeatAt`: the layout's chairs, then `Furniture`: layout thrones and dummies, and free-standing chairs, thrones and dummies), the nearest to the member (engine choice:
    the original takes the first of an arbitrary order). A dummy is stood at, facing its way, and pretend-operated like a home workstation (221 `Task_OperateMachine`: the functionality's
    clip, hunger rate and `animation events`; `AiSystem.PretendOperate`). Not modelled: the finder's usability and avoid-set filters (slot 0), a piece's own owner
    (the building's is used).
- Chairs (**Verified**, BUILDING_FUNCTIONALITY records of gamedata.base / rebirth.mod / the install's mods, dumped): BuildingFunction 17 BF_CHAIR
  `chair`, `chair-fixed`, `chair-fixed 180` (animation "sitting chair"; Stool, Old Stool, Bench, Sitting Box) and `sit on floor (no chair)`
  ("sitting idle"; Sitting Pillow, Sitting spot (invisible)); 24 BF_THRONE `throne chair` ("sitting chair"); all `max operators` 1.
- Sitting (**Observed**, decompile and disassembly of `Task_OperateMachine` slots 1 `FUN_14035b430` and 2 `FUN_14035bc90`): for a BF_CHAIR or BF_THRONE
  the sitter's body is put on the seat building's own position (vtable `+0x40`, its node origin, height included) with no offset, then turned to a look
  point. The look point starts as position + orientation × +Z (`UNIT_Z`):
  a seat faces its +Z.
- Seat facing (same source): a BF_CHAIR's functionality `flags` picks the look point. 0 (`chair-fixed`,
  `throne chair`) the seat's own facing; 1 (`chair`: Stool, Old Stool, Sitting Box; `sit on floor`) the nearest building of function 16 BF_TABLE,
  11 BF_CORPSE_DISPOSAL or 4 BF_REFINERY (the seat's parent building's furniture, else ones near the sitter); 2 (`chair-fixed 180`: Bench) keeps the seat's
  facing when that target is in front; behind it the original negates the absolute world-space look point, not the forward vector, so the sitter faces
  `-(seatPosition + forward) - seatPosition`, generally towards the world origin (**Verified**, `FUN_14035b430` branch `0x14035b936–0x14035b9b6`, Ogre's
  unary minus; **Observed** native 1.0.68 save: bench at XZ (-50595, -1005), yaw 0, table behind, seated yaw 1.5509452 rad). Meitou's Fixes switch
  "Bench facing" (on by default) turns it 180° instead; off gives the original's. With none found, the seat's facing. Layout stool rotations are arbitrary (Observed: the Hub's
  Storm House, up to 161° off their table).
- Seat XP (**Verified**, `FUN_14035bc90`): each update calls the operating XP `FUN_1408c7250` with the functionality's `stat used` for every function but 7 (BF_TRAINING),
  so a seat with a stat trains it while sat on, uncapped. No base-game seat sets one (dumped: gamedata.base, Newwworld, rebirth, Dialogue); mods do (CBT Gym Training Tools: thrones and chairs with strength 1, athletics 17,
  toughness 21, `output rate` 1). Meitou: `ProductionSystem.OperatingXp` for the player's seat jobs; NPC town sitters get none (not modelled).
- **24 Wandering** (`FUN_140998040`): the nearest town (squared XZ distance from the character, its own town included) of any type but a nest marker (type 8): the town list search
  `FUN_140927f10` is called with type filter 10 ("any"), which still skips type 8 unless the filter is 8 (**Verified**); list A holds `atATown`, so it is dropped while in a town.
  Meitou (`AiSystem.TargetFor`) skips `TownType.NestMarker`.
- **180 Man the gate** (`Task_ManTheGate` slot 2 `FUN_140342c50`, target `FUN_140338690`; **Observed**, disassembly): the gate is the
  task's target, else the Blackboard's home (`HOME_GATE`, cached on its first resolve from the platoon's home, `FUN_14026b410` **Verified**: it never follows a later change of the home; Meitou `Blackboard.GateHome`), else the town's gate nearest the character (town vfunc `0x308`), each only when it
  is a GatewayBuilding (vfunc `0x3d8`). The guard walks to its first door's point `+0x3bc` and turns along `+0x3bc - +0x3c8`. A door's
  two points (`FUN_14029cc90`) are its leaf's centre ± 15 (constant `0x14168a258`) along the leaf's thin axis, dropped to the ground;
  Meitou takes the fallback gate nearest the squad (one `Blackboard.Gate` per squad), not nearest each guard (engine choice, one guard per gate squad in practice).
  which side `+0x3bc` is depends on a door body offset. Its list B needs the gate open (52), so the guard unlocks and opens it first
  ([locks](locks.md#doors) "Gate guards open their gate"). Gate BUILDING records have no nodes (**Verified**, the merged
  load order's Defensive Gates II-V), the door setup makes the gate's guard posts at run time (Gate posts above).

## Perma-jobs

A character's job list (the Orders menu's "Perma-Jobs"; class `OrdersReceiver`, base of `AITaskSytem`). Meitou: [simulation](../simulation.md#production).

- **Lists** (**Verified**, add function `FUN_1405086d0`, constructor `FUN_140507d20`): `OrdersReceiver` +0x40 is the order queue (a deque, count +0x60), +0x70 (count +0x78,
  data +0x80) a second list ([precedence](#precedence), source 3), +0x88 (count +0x90,
  data +0x98) the perma-jobs in priority order, +0x35 the jobs switch. Each entry is a Tasker: +0x10 target hand, +0x70 its TaskData (+0x44 task type).
- **Adding** (**Verified**, `FUN_1405086d0(orders, type, target, position, permanent)`; Shift with an order, tutorial `FUN_14098d8c0`):
  1. TaskData row +4 set and +0xc set: the type becomes +0xc (orders too).
  2. Not permanent, or the resulting row's +4 is 0: appended to the second list +0x70.
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
  and Splint Injuries, Loot and Follow their jobs (`PermaJobs.Rules`); Bodyguard always adds 45 (+ 31). The adder shows no animal test (**Observed**), so a mixed
  selection's animals would take Medic, Rescue or machine jobs they never work; Meitou (`PermaJobs.Give`) gives an animal only 16, 45 and 31 (the animal menu's orders).
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
  `FUN_14050c8e0`); or desire × target finder is 0. Type 187 (Obedient Slave) has a slave-state branch of its own: in slave state 1 and not sneaking it
  is scored by its master's Slave list before the list-B and list-A skips, which it never meets (row 187 has no lists, no effect and no +0x14, so the
  list-B skip would drop it). Meitou: `ListTests.JobSkipped`. The list pass has no faction test and the switch is on from construction (`FUN_140507d20`), so an NPC works its
  jobs (57 for a robot, 187 for a slave, [order givers](#order-givers)) as a player character does (**Verified**, `FUN_14050e5b0`).
- **Range** (**Verified** by decompile of `FUN_1405a3370`, `FUN_140599f30`, `FUN_140597790`, `AI::AI` `FUN_1406236d0`; names KenshiLib 1ce4a16,
  **Observed**): a job's walk is planned through rows 1 Moving / 67 Running, whose list B needs 27 `isMovementAllowed`, so a job whose target fails
  it cannot be planned and is skipped (so is every walking job while the character gets up). One-shot orders walk through 29 Move order, which lacks
  27, so they reach any distance. State 38 `hasOneWorkingArm` (list B of 87, 97, 146, 234) has no providing row, so those jobs are skipped by a
  character with no working arm.
  `AI::isMovementAllowed`: false while the character's +0x278 byte is set (KenshiLib `isCurrentlyGettingUp`); a target object goes to `isTargetInRangeForOrders(RootObject*)`:
  true when the target's `getCurrentTownLocation` is the platoon's `Ownerships::_homeTown`, else
  the position test below, else true for a character of type 1 that is attacking (`CombatClass::isAttacking` in (0, 1)). Position test
  `isTargetInRangeForOrders(Vector3)` by `AI::ordersMovement` (+0x2bc, `AIManuverabilityOrders`; the constructor sets 2): zero vector → true;
  2 ROAM_FAR: the 3D distance² from the character itself < 4.246733e7 (≈ 6517; 12.5 × the medic nearness 3397386.5); 1 STAY_CLOSE (standing order
  NEAR, the centre set to the character's position then): from `centerOfMovement` (+0x2c0) < `meiDistance` (+8) + 200 + `meiDistanceTolerance` (+0xc). Other values: false. Standing order FAR sets ROAM_FAR (EDX = 2 in the disassembly) and clears the centre target.
- **Implicit jobs** (**Verified**, `FUN_14050e5b0` after the list, only for a character of the player's faction (faction `+0x250`), the switch on and not sneaking
  (`FUN_1405c9750`)): lowest keys for the squad AI options at `DAT_142134690` (the overview's AI tab), each kept when its task's desire is above 0:
  +0x117 "Ditch items" → 242 "Ditching resources" (only with an empty task queue +0x60; the entry is tested afresh on every pass, nothing carries over, so a running trip drops out as soon as the finder finds nothing or an order is queued; finder `FUN_1405a59a0`: the carried ITEMs (type 4) of `item
  function` 0 or 15, and food (3) but the first, via `FUN_140749490`, **Verified**; that 242 passes the collect flag **Observed**; the nearest UseableStuff in range whose accept test (slot 0x4a0) takes one, skipping
  ones the squad already uses, **Observed**; Meitou: storages only); then, unless one of three tests holds (`FUN_1402b2b90`
  on the character's +0x450: hold position +0x12b on and not sneaking, **Verified**; byte +0x348, carrying someone; `FUN_1407f4d20`, the player
  interface's "is selected" (its selection set `+0x208`, the character passed in rdx; **Verified** through its callers `FUN_1407f7f20`, `FUN_1407f8410`,
  `FUN_1407f4dd0`, `FUN_14048f320`; Meitou `ImplicitPick`: `Player.Selection`), +0x116 "Sleep when injured" → 257 "Getting out of
  bed once healed" above 153 "Go home and go to bed", and +0x118 "Sit when idle" → 275 "Sitting around" (its task is built with a float 0.5 where the others pass 1).
  Tooltips (**Verified**, `FUN_140005e11`): ditch puts excess carried materials into storage when idle; sleep goes to bed when injured and idle;
  sit takes chairs and benches at home. its target finder is the bed search (**Verified**, [locks](locks.md#beds)). Saved as CAMERA bools ([save](../formats/save.md)).
  **Priorities and the committed bed** (**Verified**, decompile): the factory's fourth argument, kept at Tasker `+8` and handed by the pick
  `FUN_140510650` to the goal setter as the running priority `+0x20c`, is 2 for 242 and 153, 3 for 257 and 1 for 275. Row 153 has the commit flag
  (TaskData `+0x20`, 2 + r x 8 h), so once the implicit 153 is the current goal the chooser `FUN_140510ef0` runs committed at priority 2 and returns
  before the job pass (its `1 < +0x20c` stop after the priority-3 goals): neither the job list nor 257 is asked until the commitment has passed or
  the plan ends (98 is done by state 72 at a rested level above 0.9), and a list job that turns runnable does not get the sleeper up. The commitment is not saved: the save keeps the job list and the order queue, not the current goal ([save](../formats/save.md#order-queue)), so after a load the chooser picks anew. The order
  step, the urgent job pass and the priority-3 goals come before the stop. 153 plans into 98, so it is wanted by
  [row 98 Sleeping's desire](#slave-work-shout-and-capture) (its player-faction branch). Meitou: `ProductionSystem.BedCommitment` / `BedCommitted`, `WantsBed`, `GetsUpHealed`.
  The other options of the panel: [Squad AI options](#squad-ai-options). Feed and share: [eating](character-stats.md#eating).
- **Save** (**Verified**, `OrdersReceiver` vfunc 1 / 2; 4122 GAMESTATE_AI records of the owner's saves, 199 jobs, types 187 and 57 only): record
  `ai` (GAMESTATE_AI); bool `jobs` = the switch; per job i from 0, int `pjob<i>` (type) and handle `pjobT<i>`. The loader re-adds each job
  through the add function (so companions and the switch are re-run) until `pjob<i>` is missing, then reads `jobs`. `AITaskSytem` vfunc 1 / 2
  also saves the order queue (+0x40 deque, [format](../formats/save.md#order-queue)). The GAMESTATE_CHARACTER bool `chase mode` is the
  same switch (**Verified**: the writer `FUN_1406280a0` stores the byte +0x35 of the orders receiver at character +0x650 → +0x20; the loader
  `FUN_140626f50` passes it back as toggle 15); the orders panel's `OrdersChaseButton` (captioned JOBS) is this switch.
- **Finders of the jobs** (**Verified** registrations in `FUN_140319a60`, finder thunks resolved; readings **Observed**; "sensed" = the
  character's sensed list `FUN_140853960(include, exclude)`):
  - 58 Medic / 57 Robotics: `FUN_14099f690` with a flag (58: 0, 57: 1; [Medic jobs](#medic-jobs)). Desire `FUN_140998410` (both, and 250). The Medic button gives 57 with 58 for a
    squad with a robot ([ui-input](ui-input.md#6-event-dispatch)).
  - 250 Splint Rigging: `FUN_14099e240` ([Medic jobs](#medic-jobs)).
  - 16 Attacking enemies: `FUN_14059f3a0` ([Combat goals](#combat-goals)); it searches only while the AI's +0xbc (`numEnemies`,
    [senses](perception.md#senses)) is nonzero, which is also the condition of the kind-2 job pass above.
  - 238 Foraging animals: `FUN_1408ce3a0`: the first sensed (0x18, 1) character that is down (`FUN_1407d1440`: carried, or ragdoll flags), an animal
    (vfunc 0x248), not carried, passes a character vfunc 0x3e8 test or has faction flag +0x1d0, is in no give-up list, and holds an
    item that has a store (`FUN_140998950`, inventory then backpack, over `FUN_14059acd0`, **Verified** decompile): a complete building with a production interface of
    the forager's faction (a player-side forager: any player-faction building) within the AI's range mode (ROAM_FAR: squared 4.2467e7) whose `canHaveSomeOfThese` takes
    it, the nearest. Effect 148 → row 241, a timed take of one item kind ([foraging take](carry-loot.md#34-foraging-take-row-241)). Meitou `CarrySystem.Storable`: storages only, `MachineData.Accepts` for the slot test (free room not tested). The finder skips a body on the path-failure list (`PursuitSystem.PathImpossible`; **Verified**); only pursuits write that list, the loot walk writes none.
  - 244 Grabbing food, update `FUN_140340850` (**Verified**): the candidates are the building's food list, else its inventory items of function 15 first, then 3; function 3 is always accepted, function 15 for an animal or when the race's `special food` lists it (`FUN_14042ee50`); the first accepted goes into the worn backpack when it takes it, else into the character's inventory, one item, then the task completes (fails with none). One take per meal (**Observed** in the original 1.0.68, 2026-11-19, three hungry player characters in one save: one item each, into the worn bag when it had room, else the grid; one bite, the rest kept on the character, no further food run for an hour or more). Meitou: `TownDutySystem.GrabFood`; food in the worn bag counts as carried (`FeedSystem.CarriesFood`), so the item taken stops a second run before the bite.
  - 223 Remove Home Intruder: `FUN_14099c110`: the first sensed (0x400e, 0x201) character, not carried, inside a building whose handle equals the home building hand of the character's
    Ownerships (+0x38; `Hand` vfunc 0x10 is "not equal", so the test `== 0` means equal), down (vfunc 0x30), special state
    (+0x2f8) 0, i.e. not in a bed or cage. Vfunc 0x30 is `FUN_1405c9e10` (**Verified**): true for dead, unconscious, ragdolled, carried or crippled (leg state >= 3), and the finder has no race test, so a non-carriable one is returned and then refused by the lift. A dead body is not returned: the include mask 0x400e needs tag bit 1, 2, 3 or 14 and a dead entry carries bit 4 alone ([sense tags](perception.md#sense-tags-who-is-an-enemy); **Observed**, read from the two decompiles, not run). Meitou: the player's job takes `Medical.Incapacitated` (dead included), no `Carriable` test; the NPC duty (`TownDutySystem.HomeIntruder`, [Town duties](../simulation.md#town-duties)) only knocked-out, carriable characters. Data: 223 / 224 (with 283) are in every role of `Shop`, `Shop-24hr` (every bar's squad), `Civilian` and `Guard building, residents` (**Verified**, merged data probe). 273 Remove Intruder: `FUN_14099c250` (**Verified** by decompile; slot names KenshiLib 1ce4a16, **Observed**): only while the character's current town
    (vfunc 0x78 `getCurrentTownLocation`) passes town vfunc 0x2f0 (`Town`: +0x3b0 set; `TownBase`: false) and `FUN_1406211e0` (`Ownerships::isMyTown`: the town is
    `_homeTown` +0x30, `occupiedTown` +0x80, or of the Ownerships' faction +0x70); sensed (0x400e, 0x201) in the same town, not carried, unconscious (vfunc 0x30), special state not 2 (not
    caged), `amInsideTownWalls` (vfunc 0x118) > 0 (inside a building: that building's value, a gate by the side it stands; outdoors the gate code the [gate-code pass](pathfinding.md#gate-codes) gives it). A knocked-out patient in a bed passes, so guards throw it out of a rented bed (issue 751); Meitou `TownDutySystem.Intruder` skips bodies in a bed.
    So an occupier removes intruders from the town it holds. 273's desire is AI slot 60 `FUN_140597f20` (**Verified** by decompile): the lift desire of slot 59 (below), and when that is above 0 and the character has a current town, 0 when the town is no Town (vfunc 0x268 null), has no gates (0x2f0, +0x3b0), or its walls are locked up (0x300, Town slot 96 `FUN_140929820`) and the character's faction is the player's (faction +0x250); Meitou `TownDutySystem.TownLets`. Meitou for a player character (`ProductionSystem.FindTownIntruder`): the carrier is in a town of the player's (nearest by centre within its
    radius) and the body in the same town inside its walls (`TownDutySystem.AmInsideTownWalls`), a building not needed; the town's gate-side and `isMyTown` tests are not read. Their companions 224 / 274 carry the body outside: `Task_TakeIntruderOutside` (**Verified** by decompile, slots 2/5/7 `FUN_1403355e0`/`FUN_14033da80`/`FUN_1403356e0`) walks
    to the point beyond the home building's first door (`Building` vfunc 0x3e0 `FUN_1400f7080` → the first entry of the door list; `FUN_140296a40`: A + (A - B) x 1.1, **Verified**, the same point as Leave Building's; Meitou `ProductionSystem.OutsideGoal` via `AiSystem.DoorExit`, the lowest door index, and `TownDutySystem.HomeDoor` for the NPC goal; no door: the town's exit point, **Verified** `FUN_140335480`: with no first door it calls the building's town's vfunc 0x38 with the carrier's position); its start (slot 2) ends the task
    without a home building and drops at once when nothing is carried; its update (slot 7, shared with 274) does nothing while the movement controller's moving byte (+0x24) is set (it zeroes the timer); when clear it stamps the clock once, and drops the body (`FUN_1405ce1e0`) when the movement says the walk is over or elapsed clock hours x 60 >= 5 (**Verified**; so the 5 minutes count only time standing still). Meitou `ProductionSystem.CarryOutOver`: GoTo task = moving; the walk the job started having ended = walk over; the drop is `CarrySystem.LetGo` (the unlink, no put-down knock-out), for the NPC duty too (`TownDutySystem.LetGo`). `Task_TakeIntruderOutsideTown` walks to the town's exit point (`Town` vfunc 0x38 `FUN_140927120`, a cached point refreshed on a timer: 200 beyond a door of one of its
    buildings through vfunc 0x290 `FUN_140929560`, else beyond the door of a building `FUN_1409b2ab0` finds; only partly read, **Observed**) and drops it there.
  - Lift and delivery are separate (**Verified**: row 138 has list A 99 and no desire, its finder never calls the disposal finder, `FUN_14050e5b0` keeps a job while desire x finder > 0; row 137 has list B 99): a dead body is lifted with or without a machine or the delivery job 137, and 223 / 273 lift without 224 / 274 in the list; only the delivery jobs need their building / companion, else the carrier keeps the body. Meitou: 138 and 137 are two jobs (`ErrandJobs.CorpseCanRun`: 137 needs a disposal, `NearestIncinerator`), and `ProductionSystem.DeliveryReady` holds an intruder's carrying out while its companion is missing (the Corpse Disposal order needs no job).
  - Claims (**Verified**, `FUN_1408d0770`, `FUN_1408cfb00`, `FUN_1408ce170`): `FUN_1402690e0` counts the entries of the squad blackboard's goal list (+0x238) whose worker is another character and whose target is the given hand; a count >= 1 skips a candidate machine, cage building or body. The scope is the squad: other squads may target the same body. Carried bodies are skipped globally (the carried byte). Meitou: `SquadGoals` (one list, systems publish their pairs through `IGoalSource`; `Working` = FUN_1402690e0, `IsMine` = FUN_140269190); the carried byte stays a global set (`Lifted`); the machine claim is the errand's `Site`. Callers on it: corpse/intruder errands, captive gathering (217's cage and occupant; 173's finder allows 0x100, i.e. no claim), NPC looting (FUN_14099ae10 counts the squad's entries, **Verified**), the medic and splint finders (FUN_14099f690, FUN_14099e240). A squadless character (test worlds only) shares one list. Entries are keyed by worker with a 3 s timer (`0x40400000`): the writers `FUN_140269ff0` (from the first aid task's update `FUN_14034ef20` and `FUN_1403346d0`) refresh it on every call, `Platoon::update` `FUN_14026a320` subtracts its dt and removes an entry below 0 (**Verified**), so a claim outlives its duty by up to 3 s. Meitou: `GoalClaims` (`World.Claims`) keeps each (worker, target) pair for 3 s after the sources last published it, refreshed once a tick; the original keeps one entry per worker, Meitou one per pair (its cage-and-captive duty adds two).
  - A machine whose path failed (**Verified**): `FUN_1408d0770` skips a building in the AI's ignore map (`FUN_1405b0290`); `FUN_140796ed0` (path-impossible notice, e.g. 'Task impossible: X can't get to Y') stores 20.0 for the hand and tells the squad blackboard, re-armed while the path stays impossible. Meitou `ProductionSystem.NoteWalkEnd`: a walk to the machine that ends short of it skips that machine for 20 s for that carrier, in the same ignore map as the builders' (`CharacterCold.Ignored`, not saved; the combat chase, first aid and the finders of patients, builders and carriers all read it); no walk limit and no body give-up any more (the carrier waits holding the body when no machine remains).
  - 138 Find corpses: `FUN_1408cfb00`: a building of function 8 (cage) of the character's (`FUN_1408cbea0` mask 0x100) holding a dead,
    non-robot body (the robot byte is tested only in this cage stage and for an animal finder, **Verified**; a human carrier lifts a dead robot lying outside) not in a give-up list, else `FUN_1408ce170` (the nearest dead uncarried body: sensed (0x10, 0), down `FUN_1407d1440`, not carried, a flag at +0x5bc, not in a give-up list; for
    an animal finder only other races). 137 Corpse disposal: `FUN_1408d1090` → `FUN_1408d0770`(1, 0xb): the building of function 11 (BF_CORPSE_DISPOSAL, the Cooking pot / Corpse disposal
    functionalities of `Four Cadavres` and the rebirth shredder, **Verified** data) among the character's buildings whose `UseableStuff` vfunc 0x4f0 holds, weight clamp(1 - d^2 / 2.1233664e7, 1e-5, 1) (`FUN_1409b2380`, d = 3D distance to the character, 4608^2, **Verified** `DAT_14172e248`; the first listed wins ties, so everything beyond 4608 goes to the first listed). Candidates (**Verified**, `FUN_1408cbea0` / `FUN_140a00690`): buildings within 1.1 x `Town::getRadius` of the character's current town (location +0x88), else of the nearest town if within squared 1.5625e7 (`DAT_14172dd88`), else of every town; plus the squad's home town. Meitou `CorpseDisposal.Find` for the player's job and the NPCs alike (player machines, placed town buildings and interior disposals, `TownBuilding.Disposals`; the gatherer as `FixtureRegistry.TownCandidates`),
    x100 for the character's faction (else it must pass `canIUseThisBuilding`, `FUN_140621230`, [locks](locks.md#beds): every function-mask finder applies it, not only the ally test) (`FUN_140621230` **Verified**, standalone building: the character's faction, or the building's faction is not hostile to it (relation <= -30, `FUN_1406b26d0`) and is its ally (same faction, allied flag or relation >= 50, `FUN_1406b2630`); nested buildings have extra branches, not traced. Meitou `FixtureRegistry.CanUse`, the same test the cage finder uses: own faction, not hostile and ally by relation, occupation town, squad town, public building; weighted x1 against x100 for the player's own), x100 when its owning object is the squad's home building (Ownerships `+0x38`), x1e-5 for a law enforcer (`FUN_140599600`, not a "player-type squad member") when the parent building exists and its vfunc 0x290 (`isPublic`, building `+0xc8`) is false (**Verified**, `FUN_1408d0770`);
    the heaviest wins. At the machine `Task_FeedCorpseIntoMachine` (136; slot 3 `FUN_1403567a0`, **Verified**) lets go of the body and kills/removes it ("incinerated or cooked"), plays `Armor_Worn` and
    `Character_Drop_Person` and floats the functionality's `string` over the machine ([corpse machine label](buildings-production.md#classes-and-functions)).
  - 45 Bodyguard, 31 Staying close and 97 Training have no finder: they act on the job's own target; 45 and 31 are goals (Task_Blank, rows above) on the job's character, the context menu gives
    them as "Follow" (31) and "Bodyguard" (45), the dispatcher always adds 45 as a job. None of 31, 45, 289 has 47 `IS_CARRYING_SOMETHING` in list B (row table), so nothing in
    those rows stops a carrier following. Meitou: a carrier standing idle with nothing under way (after a pick-up order, a 148 lift or a finished move order, whose provenance stays; #711) takes them up, ahead of 106 when listed above it; one
    whose job, errand or order still holds the body does not, and the other production list jobs wait until the body is put down (`ProductionSystem.AbleFor`). 97 is `Task_UseTrainingDummy` (slots 1-3 `FUN_1403513a0`, `FUN_14035c1e0`, `FUN_140345570`; **Verified** by
    decompile): the dummy (function 7, BF_TRAINING; the animation event `Impact Dummy`, a "Training" progress bar for the player's characters) is operated through `UseableStuff` vfunc 0x4b0: with a
    positive `stat used` (+0x3f8) it calls the continuous XP entry with a cap, `FUN_1408c64f0`(stats, dt, 2.0, &stat, cap = the float +0x1a4, stat number): nothing once the stat is at the cap, else the
    same gain as the operating XP (`0.02 x dt x exp gain mult x 2.0 x race[stat]`) through the core curve with that cap, ((cap - stat) / cap)^2, so the stat approaches the cap without reaching it.
    The cap is **Observed** to be the BUILDING's `output rate` (MkI 6, II 11, III 16; lock 11/21/31; assassin 10/20/30; the descriptions promise levels 5/10/15, the displayed level being the whole part);
    the field +0x1a4 itself was not traced to the record. The bar shows the stat's fraction (+0x3a4). Meitou reads it as the stat's fractional part (`TrainBar`; **Observed**). The row's conditions are 38 `hasOneWorkingArm`, 73, `isAtLocation`.
    The XP is the user's own stat, not the injured one. Meitou: [simulation](../simulation.md#job-lists).
- **UI** (strings, `FUN_14072b540` JobsPanel): "No jobs assigned", "To add jobs, hold shift key when giving an order", the Jobs toggle "If this is
  disabled then the character won't do any of the jobs listed."; tutorial: jobs are reordered by dragging and removed with X.
  Row text: [Jobs list](ui-screens.md#4-hud-mainbargui).
- **Unknown:** what list A/B mean per job type in detail (operating 87: A `IS_WORKING`; B free slot, power, inputs not
  empty, output not full, at the machine); how 124 "Hauling to" gets the items it needs (B 67 `haveSomeResourcesFor`; no row's effect is 67).

### Medic jobs

**Verified** (decompile of `FUN_14099f690` (Medic 58 passes 0, Robotics 57 passes 1, and the NPC `Medic` goal), `FUN_140998200` (need), `FUN_140998b90` (nearness), `FUN_140998410` (desire),
`FUN_14099e240` (splint finder), `FUN_140646400`; constants read from the exe). Names from KenshiLib 1ce4a16: `Blackboard::howManyGuysWorkingOnThisSubTarget` (`FUN_1402690e0`), `isMySubTarget` (`FUN_140269190`),
`isTargetMarkedForDeath` (`FUN_14032eb40`), `AITaskSytem.pathFailures` (+0x218, the "give-up list"), `Character::isAlly` / `isEnemy` (vfuncs `0x3f0` / `0x3e8`), `amInsideTownWalls` (`0x118`), `isIndoors` (`0x1d8`, a building
hand), `isGettingEaten` (+0x279), `CharStats._holdPositionMode` (+0x12b).
- **Stand still** (**Verified**, `FUN_1403563a0`, vtables of `Task_FirstAid`, `Task_FirstAidRig` and `Task_MakeTargetStandStill`, whose slot +0x28 is the same `FUN_1403346d0`): rows 25, 249 and 60 need 42 `targetStandingStill`, which row 64 Stand still dammit! provides; every update of 64 (its slot 1 is empty and its slot 2 calls slot 5) and of First aid asks the patient for 62 Standing still at tier 3 for 3 s ([requests](ai.md#requests)); the splint update `FUN_14034ef20` posts the same request itself, on the medic's own platoon's Blackboard (`FUN_140507070`: AI `+0x10`, the platoon, `+0xf8`), and faces the patient. A medic treating itself stops its own movement instead (vfunc 0x98 of `+0x640`, **Observed**); a target with byte `+0x3d4` set is replaced by a handle at `+0x380` first. The work clip is played by the treatment's update (`FUN_14051eab0`), which runs only once 42 holds, so a medic kneels only at a patient standing still (**Observed**). Meitou (`FirstAidSystem.Schedule`): a medic in reach renews `CharacterCold.StandStillUntil` of its patient every tick (3 s); the movement holds a character still until then unless it fights, flees or is carried, and, for the player's characters, only one with no order in hand (the request is a tier 3 candidate of the chooser's step 4, row 62's `!39`: the order queue, source 1, outranks it, jobs and routine, sources 5 and 6, do not: **Verified** by `FUN_140510d60` / `FUN_140510ef0`, [precedence](#precedence); **Observed** offscreen sandbox, `medic-job` hero walking to a patient stops while a medic treats it, walks on in the build before); the medic kneels (`CharacterCold.TreatKneels`) only in reach of a patient keeping still.
  State 42 `targetStandingStill` (`FUN_14079b120`, **Verified**): false only when the target's mover speed (`+0x640` object, `+0xb8`) is at least 1.0 and it is not ragdolled or carried (`FUN_1407d1440`: `+0x3d4`, or its animation object's WHOLE / CARRY flags), not in a bed or cage (`+0x2f8` = 0) and not getting up (`+0x278`); every other target counts as still (a crawler that moves is not). Lifts (rows 225 / 246, `Task_LiftSomething` start `FUN_14034f990` and update `FUN_140354ff0`, **Verified**) post no stand-still request and need no 42: the lifter only stops itself and plays `shoulder lift`, so Pick Up needs nothing from a walking ally beyond reach. Meitou holds a patient only for the treatments (first aid, splint, repair).
  First aid while crawling (**Verified**): rows 25, 58 and 269 are registered for the crippled repertoire (category 0x45 has 0x40, `TaskDataTable`), the update `FUN_1403563a0` tests no posture, and the clip picker `FUN_1405209d0` plays `medic`, or `crawl use` at posture 1 or 2 (staying low or crawling). A crawling medic treats; it is not made to stand first. Meitou: `Repertoire` / `CharacterFacts.GetUpGoal` already agree; a crawling NPC medic in the real save crawled to a patient and treated (**Observed**); a looping get-up report was not reproduced.
  First aid while playing dead: posture 3 is awake to the AI ([playing dead](ragdolls.md#playing-dead)) and row 25 has no `!26` (**Verified**); the Medic job treating its medic lying down is **Observed** (#870). A ragdolled or carried body walks no step (**Verified**, `FUN_1405d00b0` updates the `+0x640` mover only while `FUN_1407d1440` is false). Meitou (`FirstAidSystem.Awake`, `Itself`): a playing-dead medic treats only itself, lying, without the kneel (engine choice); a player order still gets it up first.
- **Give-up**: a task walk that cannot get to its target puts it in `pathFailures` for 20 s (`notifyPathImpossible` `FUN_140796ed0` stores 20.0; **Verified** callers besides the combat chase: the AI plan step `FUN_14050d970` when the task's reach test fails, and `AI::isWithinChoppingRange` `FUN_14059c6d0`, which logs "Task impossible: X can't get to Y"), so the finder skips that patient meanwhile. Meitou: the walk to a patient gives up when its path request finds none or one whose navmesh part ends farther than the treat reach from the goal (`FirstAidSystem.Schedule`); before, a medic re-asked the same hopeless path every 0.5 s (issue #124). A provisional route ends on the goal itself, so its last point said nothing: medics shut in a locked house stood at its corner nearest the patient, by its stair, for minutes (**Observed**, Gigachad save, zone 39.16 at night).
- **Kits**: any item of function 1 (organic) or 12 (robot) in the medic's inventory or its backpack (**Verified**: `FUN_14099f690` tests the inventory, then a second lookup of the backpack's inventory; `FUN_14004b84e` compares each item's function only, with no charge or quantity test). Squad mates' kits do not count for the job. The task then takes a kit from the medic's own main grid, then its worn backpack, the fewest charges above zero (the rig task's search, **Verified**; the first aid tasks share its vtable slot, **Observed**: [kit search](character-stats.md#first-aid-update)). The splint finder wants function 2 and takes X = min(the medic's `medic` stat, the kit's quality). Meitou: the Medic and Robotics finder (`FirstAidSystem.FindPatient`) tests the main grid and the worn backpack with `HoldsKit` (the planner's state 23), where the task takes its own kit from; a kit in a bag carried in the grid, not worn, counts for neither.
- **Arms**: the treatment rows 25 First aid, 60 Repairing and 249 Splinting have 38 `hasOneWorkingArm` in list B (**Verified**, `TaskDataTable`). Its evaluator `FUN_140998470` (**Verified**, decompile) is true for an animal (vfunc `0x248`), else only with the medical object's knocked-out (`+0x161`, set by the medical tick `FUN_140649320`) and dead (`+0x164`) bytes clear and one of its two arm bytes set ([arms](combat.md#arms)). No row provides 38 (**Verified**, registration `FUN_140319a60`: state 0x26 is only ever added to list B, `FUN_14060e940`; never to list A, `FUN_14060e710`, or as a goal effect, `FUN_14060e740`). The plan step re-tests a running row's list B (rows 25, 60, 249 lack `+0x16`, [precedence](#precedence)). So a medic with both arms out, a skeleton with both arms below 0 too, starts no treatment, of itself neither (**Verified**, the original 1.0.65 through the Kenshi harness: a skeleton with Medic and Robotics jobs and a Skeleton Repair Kit, both arms set to -20 in the save, stood "Standing still" after the Medic button with its arms unchanged; with both at 120 it went to "Robotics" and repaired its left arm to 195 within 25 s). A running one stops when both arms go out (**Observed**, from the plan step's re-test; not seen in the running game). Meitou (`FirstAidSystem.HasWorkingArm`): the First aid and Splint orders wait in the queue (`CanRun`, `StillHolds`), the Medic and Robotics finders (jobs, the Medic button, NPCs) and the splint finder find no patient, and a running job's or NPC's treatment ends (`ArmsOut`).
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
  when not an ally (never for an ally entry). The highest wins; then **the medic itself** at its plain need beats it when larger; a robot-race medic on 58 does not count itself (**Observed**, original: a skeleton with only the Medic job and robot repair kits did not repair itself, and did once given Robotics). A found patient ends the search.
- **Pass 2, strangers** (sensed entries with any of neutral, knocked out, prisoner, crawling and none of enemy, dead): not of a robot race (job 58), not an animal (`0x248`), not being eaten, not of a not-real faction, not fighting. A free patient:
  not a bandit-class one when the medic's class differs, not an enemy, passing `FUN_14059a670` (**Verified** to be DC 55 I_SHOULD_HELP_THIS_GUY, Meitou `DialogueSystem.Helps`: same NPC class, not for a traitorous or bandit medic, yes when friendly, honourable both or loved, never when hated),
  seen or heard; it counts only when it is down (`0x30`) and the medic's **faction record says `heals strangers`**. An imprisoned patient (`+0x2f8` = 2): its cage must not be the blackboard's cage marked for death, the patient not a slave
  (status +0 = 1), and the cage owned by the medic's squad home (ownerships `+0x38`); a patient with no cage passes. A patient chained to this very medic (`FUN_14032e1e0`: chain flag `+0x320` and chain owner `+0x334` = the medic) passes iff its slave state is under 2, whatever the rules above gave, and any other needs them (**Verified**, `FUN_14099f690`:273-283; Meitou `FirstAidSystem.ChainedTo`: chained (`LockSystem.Chained`) and its owner, the leader of its master squad, is the medic). Then the medic and the patient must share the town
  (`amInsideTownWalls`) and the building (the location hands equal: Character vfunc `0x1d8`, slot 59, which stays the building for one standing on top of it, the on-top flag slot 60 is not read; **the earlier reading "different platoon" was wrong**: the hand's vfunc `0x10` is not-equal and the test needs false; Meitou `SightMap.LocationOf`). Score = need x nearness; the sticky pair of pass 1 applies here too (checked before the next rule); unlike pass 1 a patient
  others work on is skipped, not weighed. No 0.1 floor, no Heal Allies.
- **Pass 3, owned slaves**: the entries (hand types 1 character, 0x5b squad hand) of the medic squad's ownerships tree (the platoon's "slave list", character-ownerships `+0x10`; not a sensed-list or faction walk) that are chained (`+0x320`) slaves (state 1), known to the medic's own AI (`FUN_140855960` on the character's vfunc `0x68`, which returns `AI+0x28`, `FUN_1405c8700`: **Verified**, not the squad's), not fighting, not a robot (job 58): need x nearness. Meitou (`FirstAidSystem.SquadSlaves`): the master squad is not saved, so a loaded slave with none counts for every squad of its owner faction, and one that a medic of any squad already treats is skipped (engine choice; without it a whole town guard treated one prisoner, #356). **Pass 4**, only while no one was found and the medic is outdoors:
  sensed carried (bit 15) patients, not a robot or animal, not eaten, seen or heard, whose carrier is of the medic's faction: need x nearness (flag = hold mode). **Pass 5**, after 3 and 4, when the medic's location (vfunc `0x1d8`, on top included) is its squad's home building (ownerships `+0x38`, hand vfunc `8`): the
  occupants of its cages (function 8 buildings of that place): need x 0.25 flat (`DAT_141696c80`), taken when above the best so far. So NPC guards patch up their prisoners, without a kit.
- **Splint finder** (`FUN_14099e240`): no splint kit: nothing. Sensed allies, not robot races, not path-failed, not carried by an enemy, of the **medic's own faction only**; weight = `needsSplint`(X) (`FUN_140646400`, **Verified** by disassembly:
  0.1 when a part meets the [needsSplint test](character-stats.md#splints); -0.1 for a dead patient, else 0) x nearness (flag = hold mode) with the same sticky pair and x 0.05 rules; the medic itself at 0.1. No squad-fight test in the finder (**Verified**, `FUN_14099e240`), and the job's desire is `FUN_140998410` like the Medic and Robotics jobs (**Verified**: the registration `FUN_140319a60` gives 58, 250 and 57 the same desire thunk `LAB_1400442d8`, no other task uses it; that the thunk is `FUN_140998410` is **Observed**, the function is not in the decompile), always above 0, so a free player splint job runs while its squad fights. Meitou: the squad-fight wait applies to NPCs only (`FindSplint`; only the player's job calls it); the finder's `InCombat` gate on the medic itself stays.
- **NPCs run it as a goal**: the `Medic` AI_TASK (enum 58) is in the goal lists of most AI packages (Squad, Squad2 and Leader roles; e.g.
  `Guard building, residents`, `Guard town`, `Shop`, `Wandering from town to town`, the slaver packages; **Verified** by a data survey of the
  full load order, 2026-10-24). Its row in `FUN_140319a60` (like 57 and 250) has crowd factor `+0x34` 0.8 (**Observed**). Score = desire x weight x 0.8^n x the finder's score
  (0 for no patient). In the guard packages it is **Urgent**, weight 1, above their NonUrgent posts (`Guard building, residents`, `Guard town`, the gate and slave-catching guards,
  bodyguards; **Verified** by a data survey of the base game, 2026-11-03), so a guard on its post leaves it to treat. `Shop` and `Shop-24hr` (shops and bars) list it Urgent, weight 1, hours 0-24, in all three roles; `hang out in a bar` NonUrgent; `Civilian` not at all (**Verified**, merged data probe, 2026-11-05). Meitou scores it (`AiBehaviour.Medic`): the finder runs in the serial
  `FirstAidSystem.SlowWorld` sweep on the NPC's turn and keeps its patient and score on the mind (`AiMind.MedicPatient`, `MedicScore`; it shares scratch lists, the think is parallel); the
  think weighs the goal at that score times the desire from the medic's own senses (`AiSystem.MedicDesire`: 0.01 / 0.05 / 1, crowd 0.8; the carrying case is a gate of `FindPatient`), planned as its own step, and the serial step after the think
  runs the finder again and starts row 58's Treat. An NPC whose lists hold no 58 treats nobody through the Medic goal (Fixing Legs below aside). A character without a full AI mind (none, or an engine mind) treats nobody by itself: in the original a treatment starts only from goal 58 or 269, a medic job or an order, and no RACE or FACTION `AI Goals` list holds 58 (**Verified**, base data), so such a character has no list that could.
- **Fixing Legs** (269, `Task_FirstAid`; **Verified** by decompile: its registration in `FUN_140319a60`, desire `FUN_1409983b0`, the task's start `FUN_14034ecf0` and update `FUN_1403563a0`): a goal of 16 races' `AI Goals` ([ai](ai.md)), registered with no finder, no commitment and crowd 1. Desire 0 unless state 103 `myLegsAreMessedUp` holds; then 0.4 (`DAT_141696c90`) while AI `+0xb4` (SensoryData `+0x8c`, [senses](perception.md#senses): the threat towards others of its faction, not towards the looker itself) is above 0, else 1. A race goal has no target hand, and the task's start replaces a null target with the character's own hand, so the treatment is row 25's first aid on itself (skill x0.8, [first aid update](character-stats.md#first-aid-update)); neither the start nor the update tests the task type. So a crippled character on a full AI mind with a first aid kit (23), a working arm (38), out of deep water (139) and with no threat within 40 on itself (39) sheathes its weapon (157) and treats itself (a threat on itself has the providers 13, 35 and 234). The treatment does not stop when the leg holds again: it runs until the leg is fully bandaged (**Observed** in the original 1.0.68, 2026-11-18: a Hub Mercenary Captain of toughness 38.7 carrying two kits, left leg flesh set to -60 of 100 in a copied save, no other medic in reach, loaded twice: after waking he lay for about 2 game minutes with the goal text "Fixing Legs"; from then the goal text read "Medic" while he knelt and bandaged his own leg, the bandage filling the whole bar over about 11 game minutes, far past the roughly 24 points that lift the leg over his knock-out point; he stood only when the bar was full and the goal turned to Relaxing, and in the next 86 game minutes he never fell or treated again while the flesh healed from -63 to -61). What hands the treatment to the Medic goal is not in the decoded functions (row 269 is urgent and wanted at desire 1 while the leg is down, the Medic goal of his `hang out in a bar` package is non-urgent). A leg that stays down when fully bandaged (crushed, or a destroyed leg not cut off, flesh below its negative maximum, until healing lifts it, [cut-off](character-stats.md#cut-off-and-leg-loss)) keeps 103 true, so the goal is chosen again after each treatment ends: the task does not refuse an empty start, its finished test is the base one, false for a never-finished row (`FUN_14032dee0`), and the treatment step reports done when nothing is left to close or bandage (`FUN_14064d800`, `FUN_140649a00`), after which the update ends the task once the robot phase finds no repair kit (`FUN_14050c730`) or, with one, once that phase finds nothing to treat (**Observed**, from the code). The same holds for a robot race of the 16 (Skeleton, P4 Unit, Skeleton P4MkII) with a first aid kit and no robot repair kit: it plans on the kit and the treatment ends at once. Meitou: `AiBehaviour.HealLegs` (`AiSystem.HealLegsDesire`; the threat sum counts 1 per threat, `SenseBlock.ThreatToAllies`), planned from list B by the planner (157 by the sheathing step; 39's provider 13 is no step the engine carries, so the goal waits while Self preservation acts); the think asks (`AiMind.HealLegsAsk`) and `FirstAidSystem.SendGoalTreatments` starts row 269's treatment of the character itself in slot order. While it treats, the think weighs its urgent goals (`AiSystem.Preempts`: every other tier is below the urgent goal while it is wanted): another urgent goal that outscores it ends the treatment and is acted out; the treatment runs to the first aid treatment's own end, nothing left to close or bandage (`FirstAidSystem.Apply`), as in the original above, and the serial `FirstAidSystem.OwnLegsStepEnds` ends it earlier only when its list B fails (no first aid kit of its own, a threat within 40 on itself, deep water, a weapon drawn; the arms by `ArmsOut`), as the plan step re-tests a running step's list B. While it treats it kneels, also once the leg holds (`CharacterCold.TreatKneels`): the leg coming back up starts no get-up (`FirstAidSystem.AtTreatment`, [getting up](ragdolls.md#getting-up)), which would hold it 7.45 s with the treatment stopped. The HUD names it "Fixing Legs" throughout (engine choice: the original's switch to "Medic" is not reproduced). An empty treatment restarts on the next think (15 ticks), not at once as the original's replan does. Characters on an engine mind (platoons a code mod holds) never weigh it.
- Meitou (`FirstAidSystem.FindPatient` / `FindSplint`, one finder for the player's jobs, the Medic button, and the NPCs' Medic goal): all five passes. Original data used: the path-failure list is `CharacterCold.Ignored` (the one ignore map, written by pursuits; the finder skips such patients; a treating medic does not note its own failed paths yet: calling `PursuitSystem.NotePathFailure` from `Schedule` made the medic drop its patient in a `--load` run, a stale `PathFailed` flag is the suspect); `isAlly` is `DialogueSystem.RegardsFriendly`, `isEnemy` its squad memory tags (`EnemyMemory`) then the relation, `FUN_14059a670` is `DialogueSystem.Helps` (personality included); the cage marked for death is a Kill or Eat job of the medic's squad on that cage (`CaptiveSystem.Jobs`). Stand-ins left: "sensed" is the medic's sight range, "seen or heard" is the entry flag of the medic's aggro sense block (`FirstAidSystem.SeenOrHeard`: pass 1 for an NPC medic in a building, pass 2 for free strangers, pass 4; a patient the capped block lacks is judged by the shared sight test directly), "the same town and building" is `SightMap.LocationOf` (on top included), "the squad home" is the squad's home building (its own, else its town's; a cage in it passes pass 2, and pass 5 runs only in it (on top included), over its cages; a squad without one, the player's, keeps a faction test in pass 2 and has no pass 5), the slave test needs `Shackles`, pass 3's slave list is the squad's slave members and the slaves obeying it (`CharacterCold.SlaveMaster`, runtime only: a slave with no known master, after a load, counts for every squad of its owner faction), the disguise faction of `isAlly` is not read, the sub-target list is the set of (medic, patient) pairs of running treatments. The sticky pair (a running pair is taken at once with a score of at least 0.95, passes 1 and 2 and the splint finder; `FUN_140269190`, `DAT_1416bc15c`) and the not-being-eaten test (`MedicalState.EatenCountdown`; passes 1, 2 and 4 only, not pass 3, pass 5 or the splint finder) are modelled (**Verified** by decompile of `FUN_14099f690`, `FUN_14099e240`). Not yet modelled: the second relation factor in pass 1 for a patient that is not an ally (`FUN_1406b2460`).

## Squad AI options

The overview's AI tab, bytes of `DAT_142134690` (**Verified**: CAMERA writer `FUN_1403eb710` / reader `FUN_1403ebf50`, key per byte): +0x110 `heal`,
+0x111 `help`, +0x112 `rescue`, +0x113 `stay`, +0x114 `feed`, +0x115 `share`, +0x116 `sleep`, +0x117 `ditch`, +0x118 `sit`, +0x119 `eject`, +0x11a
`shootfirst`. Captions and tooltips **Verified** in the exe. The AI tab (`FUN_140494ce0`, **Verified**) has nine tick lines (all but `stay` and `eject`, which no
line shows) and a last line "Follow Formation", a `DataPanelLine_DropBox` (`Kenshi_ComboBox`, right edge at 0.97 of the panel) with Random (0), Military (2),
Caravan (1) in that order, tooltip "Formation used when squad members are following a leader", bound to the player faction's FACTION `squad formation`; a change marks every
player squad for a new layout ([squads travelling together](pathfinding.md#squads-travelling-together)). The CAMERA int `formation` holds it, read only when the record has
`ditch`. Meitou: `SquadFormationCommand` sets `World.FactionFormations` (the movement rebuilds the slots when the type changes). New-game values (**Observed**, 511 of the owner's saves): ditch, sleep, sit, share,
feed, eject on; heal, help off (on in 4), rescue, stay, shootfirst off. Readers (**Verified** by decompile):
- **Heal Allies** (+0x110): the medic finder's other-faction test, pass 1 only ([Medic jobs](#medic-jobs)); "Characters with the medic job will also heal characters of allied factions".
- Meitou-only lines after the nine (not in the original): **Heal Neutrals**, a player medic's pass 2 takes any free downed stranger that is no enemy and not carried by one, without the
  `heals strangers`, class and `FUN_14059a670` tests; **Heal All**, the same with enemies too. CAMERA bools `meitou heal neutrals` / `meitou heal all`, written only while on; the
  tab's lines shrink to fit the original panel. Code mods' tick buttons come after these ([modding](../modding.md#registration-surface), squad AI tab options) and shrink
  the lines the same way.
- **Help Allies** (+0x111): the protect-allies finder `FUN_14099ca10` (goals 15, 288; its same-town twin `FUN_14099cf80` is never registered) (sensed (1, 0x208), seen or heard): for a
  player-faction looker each fighting candidate's threat (`FUN_14099a190`) is × `FUN_1409b2380`(d², 10, 1000, 0.1); one of another faction is
  skipped with the option off and squared with it on. Result capped at 1.2.
- **Rescue Allies** (+0x112): the character finder `FUN_1408cd2d0` ([carry-loot](carry-loot.md#32-goals-and-their-target-finders)) requires the
  rescuer's faction only for a player-faction rescuer with the option off.
- **Feed Animals** (+0x114): off, the player characters' eating `FUN_1405d29c0` returns for an animal (vfunc 0x248), and desire `FUN_140597cb0`
  is 0 for an animal.
- **Share Food** (+0x115): in `FUN_1405d29c0` (the player characters' eating), with no food in its own inventory or backpack and (`Character::isWithThePlayer` `FUN_140856bc0`,
  floats +0x4bc ≤ 0, +0x4b8 < 2.25) a character takes food from the backpack of a squad member within 200 units (squared 40000).
- **Shoot first** (+0x11a): turret target choice `FUN_1404374d0`: enemies (sensed 4, 0x210) weigh `FUN_140435d70`(…, 1), unseen × 0.1, the current
  target × 1.5; with none, a player gunner with the option takes sensed (2, 0x210) characters whose faction's type (+0x34) is 0 or 8 or whose
  faction default (+0x60) is below -30; each is scored by `FUN_140435d70` with range factor k 0.6 (`DAT_141696c8c`), unseen x 0.1, current x 1.5, **Verified**, no extra weight. The
  score's own diplomat test: [combat](combat.md#turrets).

## Unknowns

- What the 37 named types without a TaskData row do when an AI_TASK record uses them (the list is in the TaskData registry section), and whether any behaviour of the 147 "goal" types (factory default `Task_Blank`) lives outside this factory.

Row 41 Fighting for master (**Verified**, decompile): desire AI vtable slot `0x178` = `FUN_1406415d0`, the constant `DAT_141682ad0` (2.0); finder `FUN_14059ee50` (a state-1 slave whose owner hand resolves, type 1 character or 0x5b squad; the slave's senses filtered by tag mask: bit 12 set, bits 3, 9 and 14 clear, i.e. an enemy of the slave's master that is not down, imprisoned or crawling, [tags](perception.md#sense-tags-who-is-an-enemy); not of the slave's own platoon, `Platoon` vfunc `0xb0` returns 0 for a loaded one; not a slave of the same owner; the attack-on-sight area and post tests `FUN_140599f30` / `FUN_140598630`; best by `FUN_14059e300`). Meitou: `FoeFinder.MasterEnemies` (`AggroSystem.FindMasterFoe`), `AiBehaviour.Fight` desire 2 (`AiSystem.Fights.cs`); the master is the slave's `SlaveMaster` squad, its enemies are the squad faction's attack-on-sight relations plus the squad's TEMP_ENEMY tags. The area test is not modelled. Squad-intact able test `FUN_14079acf0`: the thresholds `DAT_141693ea8` = 0.8 and `DAT_14168a4a8` = 0.2 (doubles); Character `+0x504` is the embedded medical object's `+0xac`, the organic first-aid need ([perception](perception.md#retreat-and-memory)); that is why no instruction writes `+0x504` itself.
