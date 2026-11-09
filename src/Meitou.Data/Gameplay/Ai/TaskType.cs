namespace Meitou.Data.Gameplay.Ai;

/// <summary>
/// The game's <c>taskType</c>: the <c>enum</c> field of an AI_TASK record and the number the AI, the Tasker factory and the player's context-menu
/// verbs key on (docs/game/ai-tasks.md "taskType to Task class", Verified: names from <c>fcs_enums.def</c>, numbers as the record values and the
/// exe's tables use them). 0 is the enum's first entry, which no record uses and no table names here (<see cref="None"/>); 289 and 290 exist in the
/// exe's tables without a name (Unknown why).
/// </summary>
public enum TaskType
{
    /// <summary>
    /// The enum's first entry (<c>NULL_TASK</c> in fcs_enums.def): no record uses it and the Tasker factory has no case for it.
    /// </summary>
    None = 0,
    /// <summary>
    /// The Tasker factory builds <c>Task_Move</c>; TaskData text "Moving".
    /// </summary>
    MoveOnFreeWill = 1,
    /// <summary>
    /// The Tasker factory builds <c>Task_Build</c>; TaskData text "Building".
    /// </summary>
    Build = 2,
    /// <summary>
    /// The Tasker factory builds <c>Task_Loot_AI</c> / <c>Task_Pickup</c>; TaskData text "Collecting item".
    /// </summary>
    Pickup = 3,
    /// <summary>
    /// The Tasker factory builds <c>Task_MeleeAttack</c>; TaskData text "Attacking".
    /// </summary>
    MeleeAttack = 4,
    /// <summary>
    /// The Tasker factory builds <c>Task_FocusedMeleeAttack</c>; TaskData text "Attacking target".
    /// </summary>
    FocusedMeleeAttack = 5,
    /// <summary>
    /// The Tasker factory builds <c>Task_EquipBestWeapon</c>; TaskData text "Equipping weapon".
    /// </summary>
    EquipWeapon = 6,
    /// <summary>
    /// The Tasker factory builds <c>Task_SheatheWeapon</c>; TaskData text "Sheathing weapon".
    /// </summary>
    UnequipWeapon = 7,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Finding weapon".
    /// </summary>
    FindWeapon = 8,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    ChooseEnemyAndAttack = 9,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    ChooseAttackerOfAlly = 10,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    AttackCharactersAttacker = 11,
    /// <summary>
    /// The Tasker factory builds <c>Task_PlayerTalkto</c>; TaskData text "Talking".
    /// </summary>
    PlayerTalkTo = 12,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Attacking attackers of".
    /// </summary>
    AttackAttackersOf = 13,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Idleness".
    /// </summary>
    Idle = 14,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Protect allies".
    /// </summary>
    ProtectAllies = 15,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Attacking enemies".
    /// </summary>
    AttackEnemies = 16,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Protection".
    /// </summary>
    Protection = 17,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    RaidTown = 18,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Going home".
    /// </summary>
    GoHomebuilding = 19,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Shopkeeper".
    /// </summary>
    StandAtShopkeeperNode = 20,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Attacking others".
    /// </summary>
    AttackEnemiesAndNeutrals = 21,
    /// <summary>
    /// The Tasker factory builds <c>Task_Patrol</c>; TaskData text "Patrolling".
    /// </summary>
    Patrol = 22,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    AttackTown = 23,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Wandering".
    /// </summary>
    Wanderer = 24,
    /// <summary>
    /// The Tasker factory builds <c>Task_FirstAid</c>; TaskData text "First aid".
    /// </summary>
    FirstAidOrder = 25,
    /// <summary>
    /// The Tasker factory builds <c>Task_Loot_Order</c>; TaskData text "Looting".
    /// </summary>
    LootTarget = 26,
    /// <summary>
    /// The Tasker factory builds <c>Task_Crouch</c>; not in the TaskData registry.
    /// </summary>
    Crouch = 27,
    /// <summary>
    /// The Tasker factory builds <c>Task_UnCrouch</c>; TaskData text "Standing".
    /// </summary>
    StandUp = 28,
    /// <summary>
    /// The Tasker factory builds <c>Task_Move</c>; TaskData text "Move order".
    /// </summary>
    MoveCusOrdered = 29,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Holding position".
    /// </summary>
    HoldPosition = 30,
    /// <summary>
    /// The Tasker factory builds <c>Task_Follow</c>; TaskData text "Staying close".
    /// </summary>
    StayCloseToTarget = 31,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Self preservation".
    /// </summary>
    SelfPreservation = 32,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    QuellAggression = 33,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Attacking trouble makers".
    /// </summary>
    AttackTroubleMakers = 34,
    /// <summary>
    /// The Tasker factory builds <c>Task_Runaway</c>; TaskData text "Running away".
    /// </summary>
    RunAway = 35,
    /// <summary>
    /// The Tasker factory builds <c>Task_PatrolTown</c>; TaskData text "Patrolling town".
    /// </summary>
    PatrolTown = 36,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Wandering town".
    /// </summary>
    WanderTown = 37,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Static guard".
    /// </summary>
    StandAtGuardNodeHomebuilding = 38,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Wandering trader".
    /// </summary>
    WanderingTrader = 39,
    /// <summary>
    /// The Tasker factory builds <c>Task_Move</c>; TaskData text "Following".
    /// </summary>
    GetNearTo = 40,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Fighting for master".
    /// </summary>
    AttackEnemiesOfMySlavemaster = 41,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Getting a weapon".
    /// </summary>
    NotBeUnarmed = 42,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Staying home".
    /// </summary>
    StayInHome = 43,
    /// <summary>
    /// The Tasker factory builds <c>Task_Follow</c>; TaskData text "Following".
    /// </summary>
    FollowPlayerOrder = 44,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Bodyguard".
    /// </summary>
    Bodyguard = 45,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    Chase = 46,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "general node".
    /// </summary>
    StandAtGeneralNode = 47,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    StandAtDefensiveNode = 48,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    StandAtBuildingGuardNode = 49,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    StandAtBuildingDefensiveNode = 50,
    /// <summary>
    /// The Tasker factory builds <c>Task_StandAtNode</c>; TaskData text "Stand at node".
    /// </summary>
    StandAtNode = 51,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetUp</c>; TaskData text "Waking up".
    /// </summary>
    Aoeu = 52,
    /// <summary>
    /// The Tasker factory builds <c>Task_TravelToTargetTown</c>; TaskData text "Travelling".
    /// </summary>
    TravelToTargetTown = 53,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    Rest = 54,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Recruiting".
    /// </summary>
    RecruitAtJobcenter = 55,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    SwitchFollowMeModeOn = 56,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Robotics".
    /// </summary>
    JobRepairRobot = 57,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Medic".
    /// </summary>
    JobMedic = 58,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Get ready".
    /// </summary>
    GetReadyForAction = 59,
    /// <summary>
    /// The Tasker factory builds <c>Task_FirstAid</c>; TaskData text "Repairing".
    /// </summary>
    FirstAidRobot = 60,
    /// <summary>
    /// The Tasker factory builds <c>Task_FocusedMeleeAttack</c>; TaskData text "Attacking target unprovoked".
    /// </summary>
    UnprovokedFocusedMeleeAttack = 61,
    /// <summary>
    /// The Tasker factory builds <c>Task_StandStill</c>; TaskData text "Standing still".
    /// </summary>
    StandStill = 62,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    SquadWaitForMe = 63,
    /// <summary>
    /// The Tasker factory builds <c>Task_MakeTargetStandStill</c>; TaskData text "Stand still dammit!".
    /// </summary>
    MakeTargetStandStill = 64,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetUp</c>; TaskData text "Getting up".
    /// </summary>
    GetUp = 65,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    ForceGetUp = 66,
    /// <summary>
    /// The Tasker factory builds <c>Task_Move</c>; TaskData text "Running".
    /// </summary>
    MoveOnFreeWillFast = 67,
    /// <summary>
    /// The Tasker factory builds <c>Task_LiftSomething</c>; TaskData text "Lifting".
    /// </summary>
    LiftPerson = 68,
    /// <summary>
    /// The Tasker factory builds <c>Task_PutDownObject</c>; TaskData text "Putting down".
    /// </summary>
    PutDownObject = 69,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    PutDownCharacterInBed = 70,
    /// <summary>
    /// The Tasker factory builds <c>Task_AddMaterialsToBuilding</c>; TaskData text "Adding materials".
    /// </summary>
    AddMaterialsToBuilding = 71,
    /// <summary>
    /// The Tasker factory builds <c>Task_OpenDoor</c>; TaskData text "Opening door".
    /// </summary>
    OpenDoor = 72,
    /// <summary>
    /// The Tasker factory builds <c>Task_CloseDoor</c>; TaskData text "Closing door".
    /// </summary>
    CloseDoor = 73,
    /// <summary>
    /// The Tasker factory builds <c>Task_OpenDoor</c>; TaskData text "Opening door here".
    /// </summary>
    OpenDoorHere = 74,
    /// <summary>
    /// The Tasker factory builds <c>Task_CloseDoor</c>; TaskData text "Closing door here".
    /// </summary>
    CloseDoorHere = 75,
    /// <summary>
    /// The Tasker factory builds <c>Task_PickLock</c>; TaskData text "Lockpicking".
    /// </summary>
    PickLock = 76,
    /// <summary>
    /// The Tasker factory builds <c>Task_LockDoor</c>; TaskData text "Locking door".
    /// </summary>
    LockDoor = 77,
    /// <summary>
    /// The Tasker factory builds <c>Task_UnlockDoor</c>; TaskData text "Unlocking door".
    /// </summary>
    UnlockDoor = 78,
    /// <summary>
    /// The Tasker factory builds <c>Task_LockDoor</c>; TaskData text "Locking door here".
    /// </summary>
    LockDoorHere = 79,
    /// <summary>
    /// The Tasker factory builds <c>Task_UnlockDoor</c>; TaskData text "Unlocking door here".
    /// </summary>
    UnlockDoorHere = 80,
    /// <summary>
    /// The Tasker factory builds <c>Task_BashDoor</c>; TaskData text "Bashing door".
    /// </summary>
    BashDoor = 81,
    /// <summary>
    /// The Tasker factory builds <c>Task_MoveToDoor</c>; TaskData text "Moving to door".
    /// </summary>
    MoveToBuildingDoor = 82,
    /// <summary>
    /// The Tasker factory builds <c>Task_MoveToDoor_CurrentLocation</c>; TaskData text "Moving to current door".
    /// </summary>
    MoveToCurrentLocationBuildingDoor = 83,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Open current door".
    /// </summary>
    OpenDoorForCurrentLocation = 84,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Open destination door".
    /// </summary>
    OpenDoorForDestination = 85,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Opening up".
    /// </summary>
    OpenUpShopDoors = 86,
    /// <summary>
    /// The Tasker factory builds <c>Task_OperateMachine</c>; TaskData text "Operating machine".
    /// </summary>
    OperateMachinery = 87,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Delivering".
    /// </summary>
    DeliverResources = 88,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Keeping everything running".
    /// </summary>
    JobKeepEverythingRunning = 89,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Unjam all machines".
    /// </summary>
    UnjamAllMachines = 90,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Unjamming machine".
    /// </summary>
    UnjamMachine = 91,
    /// <summary>
    /// The Tasker factory builds <c>Task_EmptyMachine</c> / <c>Task_Pickup</c>; TaskData text "Collecting resource".
    /// </summary>
    CollectOutputResource = 92,
    /// <summary>
    /// The Tasker factory builds <c>Task_FillMachine</c>; TaskData text "Filling machine".
    /// </summary>
    FillMachine = 93,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Try filling machine".
    /// </summary>
    WantToFillMachine = 94,
    /// <summary>
    /// The Tasker factory builds <c>Task_Build</c>; TaskData text "Repairing".
    /// </summary>
    Repair = 95,
    /// <summary>
    /// The Tasker factory builds <c>Task_Dismantle</c>; TaskData text "Demolishing".
    /// </summary>
    Dismantle = 96,
    /// <summary>
    /// The Tasker factory builds <c>Task_UseTrainingDummy</c>; TaskData text "Training".
    /// </summary>
    UseTrainingDummy = 97,
    /// <summary>
    /// The Tasker factory builds <c>Task_UseBed</c>; TaskData text "Sleeping".
    /// </summary>
    UseBed = 98,
    /// <summary>
    /// The Tasker factory builds <c>Task_PutInSomething</c>; TaskData text "Put in bed".
    /// </summary>
    PutSomeoneInBed = 99,
    /// <summary>
    /// The Tasker factory builds <c>Task_UseBed</c>; TaskData text "Sleeping".
    /// </summary>
    GetPutInBed = 100,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Assault".
    /// </summary>
    DefeatSquad = 101,
    /// <summary>
    /// The Tasker factory builds <c>Task_TalktoPlayer</c>; TaskData text "Making announcement".
    /// </summary>
    SeekAndTalkAndSendSignal = 102,
    /// <summary>
    /// The Tasker factory builds <c>Task_MakeAnnouncement</c>; TaskData text "Diplomatting".
    /// </summary>
    MakeAnnouncement = 103,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "null".
    /// </summary>
    AlwaysImpossibleTask = 104,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find and rescue".
    /// </summary>
    FindAndRescue = 105,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find and put in bed".
    /// </summary>
    FindBedAndPutIn = 106,
    /// <summary>
    /// The Tasker factory builds <c>Task_UseCage</c>; TaskData text "Imprisoned".
    /// </summary>
    UseCage = 107,
    /// <summary>
    /// The Tasker factory builds <c>Task_PutInSomething</c>; TaskData text "Put in prison".
    /// </summary>
    PutInCage = 108,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    KnockoutPrisoner = 109,
    /// <summary>
    /// The Tasker factory builds <c>Task_ReleasePrisoner</c>; TaskData text "Release from prison".
    /// </summary>
    ReleasePrisoner = 110,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    BreakoutPrisoner = 111,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find and put in cage".
    /// </summary>
    FindCageAndPutIn = 112,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Try emptying machine".
    /// </summary>
    EmptyMachineOutputs = 113,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Ditching to make space".
    /// </summary>
    GetRidOfResourcesInMyInventory = 114,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Finding building mats".
    /// </summary>
    FindSomeBuildingMaterials = 115,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetOutOfBed</c>; TaskData text "Getting out of bed".
    /// </summary>
    GetOutOfBed = 116,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    FindAShop = 117,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Shopping".
    /// </summary>
    Shopping = 118,
    /// <summary>
    /// The Tasker factory builds <c>Task_Shopping</c>; TaskData text "Shopping".
    /// </summary>
    BuyShit = 119,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    MoveInsideBuilding = 120,
    /// <summary>
    /// The Tasker factory builds <c>Task_MoveToDoor_Gate_ChooseSide</c>; TaskData text "Moving to gate".
    /// </summary>
    MoveToFortificationGate = 121,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Open gate".
    /// </summary>
    OpenFortificationGate = 122,
    /// <summary>
    /// The Tasker factory builds <c>Task_BashDoor</c>; TaskData text "Bashing gate".
    /// </summary>
    BashGate = 123,
    /// <summary>
    /// The Tasker factory builds <c>Task_FillMachine</c>; TaskData text "Hauling to".
    /// </summary>
    OperateStorage = 124,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Engineer".
    /// </summary>
    JobBuilder = 125,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    TalktoNearestPlayerCharacter = 126,
    /// <summary>
    /// The Tasker factory builds <c>Task_GoHomeTownFast</c>; TaskData text "Retreating fast".
    /// </summary>
    RunAwayHometown = 127,
    /// <summary>
    /// The Tasker factory builds <c>Task_GoHomeTown</c>; TaskData text "Retreating".
    /// </summary>
    RetreatHometown = 128,
    /// <summary>
    /// The Tasker factory builds <c>Task_MakeAnnouncement</c>; TaskData text "Approaching fast".
    /// </summary>
    MakeAnnouncementFast = 129,
    /// <summary>
    /// The Tasker factory builds <c>Task_TravelToTargetTown</c>; TaskData text "Travelling quickly".
    /// </summary>
    TravelToTargetTownFast = 130,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Looting food".
    /// </summary>
    LootFoodAndStuff = 131,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find and kidnap".
    /// </summary>
    FindAndKidnap = 132,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetOutOfCage</c>; TaskData text "Get out of cage".
    /// </summary>
    GetOutOfCageLegit = 133,
    /// <summary>
    /// The Tasker factory builds <c>Task_KillPrisoner</c>; TaskData text "Kill prisoner".
    /// </summary>
    KillCageOccupant = 134,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Kill random prisoner".
    /// </summary>
    KillARandomCageOccupant = 135,
    /// <summary>
    /// The Tasker factory builds <c>Task_FeedCorpseIntoMachine</c>; TaskData text "Disposing of corpse".
    /// </summary>
    FeedCorpseIntoMachine = 136,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Corpse disposal".
    /// </summary>
    DeadGuysGoInThePot = 137,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find corpses".
    /// </summary>
    FindADeadGuy = 138,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Eat random prisoner".
    /// </summary>
    EatARandomCageOccupant = 139,
    /// <summary>
    /// The Tasker factory builds <c>Task_UnlockDoor</c>; TaskData text "Unlocking door".
    /// </summary>
    UnlockDoorPlayerOrder = 140,
    /// <summary>
    /// The Tasker factory builds <c>Task_FollowLeader</c>; TaskData text "Following leader".
    /// </summary>
    FollowSquadleader = 141,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find and rescue leader".
    /// </summary>
    FindAndRescueLeader = 142,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Protect close allies".
    /// </summary>
    ProtectOwnSquad = 143,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Home territory aggression".
    /// </summary>
    TerritorialAggressionButDontLeaveHome = 144,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    GetReEquipped = 145,
    /// <summary>
    /// The Tasker factory builds <c>Task_OperateMachine</c> / <c>Task_UseTurret</c>; TaskData text "Turret gunner".
    /// </summary>
    UseTurret = 146,
    /// <summary>
    /// The Tasker factory builds <c>Task_Stumble</c>; TaskData text "Stumbling".
    /// </summary>
    StumbleTaskForced = 147,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find and rescue".
    /// </summary>
    FindAndRescueIfTheresBeds = 148,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Turret guard".
    /// </summary>
    ManATurret = 149,
    /// <summary>
    /// The Tasker factory builds <c>Task_Prospecting</c>; TaskData text "Prospecting".
    /// </summary>
    Prospecting = 150,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    EmptyingMachine = 151,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Operating automatic machine".
    /// </summary>
    OperateAutomaticMachinery = 152,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Going to bed".
    /// </summary>
    GoHomeAndGoToBed = 153,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    GoToTheBarAndDrink = 154,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Locking up".
    /// </summary>
    LockAllMyDoors = 155,
    /// <summary>
    /// The Tasker factory builds <c>Task_EnterBuilding</c>; TaskData text "Entering building".
    /// </summary>
    EnterBuilding = 156,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Static town guard".
    /// </summary>
    StandAtGuardNodeHometownOutside = 157,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Shooing".
    /// </summary>
    ShooStrangersOutOfMyBuilding = 158,
    /// <summary>
    /// The Tasker factory builds <c>Task_TalktoPlayer</c>; TaskData text "Talking".
    /// </summary>
    SendDialogueSignal = 159,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    SendDialogueSignalRepeat = 160,
    /// <summary>
    /// The Tasker factory builds <c>Task_TalktoPlayer</c>; TaskData text "Talking".
    /// </summary>
    SendDialogueSignalWithoutMoving = 161,
    /// <summary>
    /// The Tasker factory builds <c>Task_LockDoor</c>; TaskData text "Locking door from inside".
    /// </summary>
    LockDoorFromInside = 162,
    /// <summary>
    /// The Tasker factory builds <c>Task_MoveToDoor_Inside</c>; TaskData text "Moving to door inside".
    /// </summary>
    MoveToBuildingDoorInsidepos = 163,
    /// <summary>
    /// The Tasker factory builds <c>Task_Follow</c> / <c>Task_FollowAndTalk</c>; TaskData text "Following and talking".
    /// </summary>
    FollowWhileTalking = 164,
    /// <summary>
    /// The Tasker factory builds <c>Task_TownStalker</c>; TaskData text "Stalking".
    /// </summary>
    TownStalker = 165,
    /// <summary>
    /// The Tasker factory builds <c>Task_CuffTarget</c>; TaskData text "Restraining".
    /// </summary>
    ChainTarget = 166,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Capturing slave".
    /// </summary>
    CaptureNewSlaves = 167,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Carrying slaves".
    /// </summary>
    CarryWoundedSlaves = 168,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Putting down".
    /// </summary>
    PutDownCarriedDudeIfTheyCanWalk = 169,
    /// <summary>
    /// The Tasker factory builds <c>Task_LiftSomething</c>; TaskData text "Lifting and healing".
    /// </summary>
    LiftObjectButHealFirst = 170,
    /// <summary>
    /// The Tasker factory builds <c>Task_FollowLeader</c>; TaskData text "Following master".
    /// </summary>
    FollowSlavemaster = 171,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Getting in my cage".
    /// </summary>
    SlaveGetInMyMastersCage = 172,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Gathering slaves".
    /// </summary>
    GatherSlavesFromCages = 173,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetSlave</c>; TaskData text "Ordering slaves".
    /// </summary>
    GetSlave = 174,
    /// <summary>
    /// The Tasker factory builds <c>Task_SleepOnFloor</c>; TaskData text "Sleeping on ground".
    /// </summary>
    SleepOnFloor = 175,
    /// <summary>
    /// The Tasker factory builds <c>Task_HuntingSmell</c>; TaskData text "Blood tracking".
    /// </summary>
    HuntingBloodsmell = 176,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Looting the dead".
    /// </summary>
    LootTheDead = 177,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    LootToReplaceMissingWeapon = 178,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    HuntMyThief = 179,
    /// <summary>
    /// The Tasker factory builds <c>Task_ManTheGate</c>; TaskData text "Gate guard".
    /// </summary>
    ManTheGate = 180,
    /// <summary>
    /// The Tasker factory builds <c>Task_StripWeapons</c>; TaskData text "Stripping weapons".
    /// </summary>
    StripTargetsWeapons = 181,
    /// <summary>
    /// The Tasker factory builds <c>Task_ProcessNewSlave</c>; TaskData text "Processing slave".
    /// </summary>
    ProcessAndStripNewSlave = 182,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Watching slaves".
    /// </summary>
    SlaveWatching = 183,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Ditching loot".
    /// </summary>
    PutLootInStorage = 184,
    /// <summary>
    /// The Tasker factory builds <c>Task_CutShackles</c>; TaskData text "Cutting shackles".
    /// </summary>
    CutShackles = 185,
    /// <summary>
    /// The Tasker factory builds <c>Task_BruteForceShackles</c>; TaskData text "Breaking shackles".
    /// </summary>
    BruteForceShackles = 186,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Obedient Slave".
    /// </summary>
    SlaveObedience = 187,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Working the slaves".
    /// </summary>
    WorkTheSlaves = 188,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Worker".
    /// </summary>
    AutoLabouringMines = 189,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Worker".
    /// </summary>
    AutoLabouringMinesPretend = 190,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    GoToNearestHq = 191,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Delivering slaves".
    /// </summary>
    GoToSomewhereForDeliveringSlaves = 192,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    CaptureEscapingSlaves = 193,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Selling slaves".
    /// </summary>
    GiveAllMySlavesTo = 194,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Locking up cages".
    /// </summary>
    LockAllTheCages = 195,
    /// <summary>
    /// The Tasker factory builds <c>Task_KillPrisoner</c>; TaskData text "Beat prisoner".
    /// </summary>
    BeatCageOccupant = 196,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Locking up from outside".
    /// </summary>
    LockAllMyDoorsFromOutside = 197,
    /// <summary>
    /// The Tasker factory builds <c>Task_LockDoor</c>; TaskData text "Locking door from outside".
    /// </summary>
    LockDoorFromOutside = 198,
    /// <summary>
    /// The Tasker factory builds <c>Task_MoveToDoor_Outside</c>; TaskData text "Moving to door outside".
    /// </summary>
    MoveToBuildingDoorOutsidepos = 199,
    /// <summary>
    /// The Tasker factory builds <c>Task_LeaveBuilding</c>; TaskData text "Leaving building".
    /// </summary>
    LeaveBuilding = 200,
    /// <summary>
    /// The Tasker factory builds <c>Task_PickLock_Shackles</c>; TaskData text "Lockpicking Shackles".
    /// </summary>
    PickLockOnShackles = 201,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Escaping".
    /// </summary>
    TotalEscape = 202,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Arresting".
    /// </summary>
    ArrestTarget = 203,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Bounty hunting".
    /// </summary>
    HuntBounties = 204,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    ArrestTargetsCarriedPerson = 205,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    FindCageAndPutInIfBounty = 206,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetOutOfCage</c>; TaskData text "Escape cage".
    /// </summary>
    GetOutOfCageEscape = 207,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetOutOfBed</c>; TaskData text "Getting out of bed!".
    /// </summary>
    GetOutOfBedIfItsEmergency = 208,
    /// <summary>
    /// The Tasker factory builds <c>Task_InvestigateAlarms</c>; TaskData text "Investigating".
    /// </summary>
    InvestigateAlarms = 209,
    /// <summary>
    /// The Tasker factory builds <c>Task_InvestigateAlarms</c>; TaskData text "Investigating".
    /// </summary>
    InvestigateAlarmsAlliesOnly = 210,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Freeing prisoner".
    /// </summary>
    PoliceFreePrisonersWhenDone = 211,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Confiscating goods".
    /// </summary>
    LootStolenGoods = 212,
    /// <summary>
    /// The Tasker factory builds <c>Task_LiftSomething</c>; TaskData text "Lifting/Snatching".
    /// </summary>
    LiftPersonSnatchingAllowed = 213,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Relaxing".
    /// </summary>
    RelaxInTownPackage = 214,
    /// <summary>
    /// The Tasker factory builds <c>Task_TravelToMissionTarget_ArmyFormation</c>; TaskData text "Mission formation".
    /// </summary>
    TravelToTargetPackage = 215,
    /// <summary>
    /// The Tasker factory builds <c>Task_PatrolTownViolent</c>; TaskData text "Ransacking town".
    /// </summary>
    RunAroundTownLookingForPeople = 216,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Gathering slaves for shipping".
    /// </summary>
    GatherSlavesFromCagesIfItsAnExportTown = 217,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Selling slaves".
    /// </summary>
    GiveAllMySlavesToIfItsAnImportTown = 218,
    /// <summary>
    /// The Tasker factory builds <c>Task_TakeOffCuffs</c>; TaskData text "Removing Shackles".
    /// </summary>
    TakeOffMyShackles = 219,
    /// <summary>
    /// The Tasker factory builds <c>Task_EatPrisoner</c>; TaskData text "Eat prisoner".
    /// </summary>
    EatTargetAlive = 220,
    /// <summary>
    /// The Tasker factory builds <c>Task_OperateMachine</c>; TaskData text "Operating machine".
    /// </summary>
    PretendToOperateMachinery = 221,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Turret home guard".
    /// </summary>
    ManATurretOnBuilding = 222,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Remove Home Intruder".
    /// </summary>
    PickupIntruders = 223,
    /// <summary>
    /// The Tasker factory builds <c>Task_TakeIntruderOutside</c>; TaskData text "Intruder out".
    /// </summary>
    TakeIntruderOutside = 224,
    /// <summary>
    /// The Tasker factory builds <c>Task_LiftSomething</c>; TaskData text "Lifting/Snatching Order".
    /// </summary>
    LiftPersonPlayerOrder = 225,
    /// <summary>
    /// The Tasker factory builds <c>Task_BashDoor</c>; TaskData text "Bashing door".
    /// </summary>
    BashDoorPlayerOrder = 226,
    /// <summary>
    /// The Tasker factory builds <c>Task_MeleeAttack</c>; TaskData text "Attacking".
    /// </summary>
    MeleeAttackAnimal = 227,
    /// <summary>
    /// The Tasker factory builds <c>Task_StealthKO</c>; TaskData text "Taking out".
    /// </summary>
    StealthKnockout = 228,
    /// <summary>
    /// The Tasker factory builds <c>Task_StealthKO</c>; not in the TaskData registry.
    /// </summary>
    StealthKill = 229,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Eat corpse".
    /// </summary>
    EatARandomDeadBody = 230,
    /// <summary>
    /// The Tasker factory builds <c>Task_EatingCrops</c>; TaskData text "Eating crops".
    /// </summary>
    EatCrops = 231,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find crops".
    /// </summary>
    FindCropsToEat = 232,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Eat body".
    /// </summary>
    EatARandomKoBody = 233,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Turret guard".
    /// </summary>
    ManATurretPlayerJob = 234,
    /// <summary>
    /// The Tasker factory builds <c>Task_OperateMachine</c> / <c>Task_UseTurret</c>; TaskData text "Turret gunner".
    /// </summary>
    ShootAtTarget = 235,
    /// <summary>
    /// The Tasker factory builds <c>Task_WorshipTarget</c>; TaskData text "Worshiping".
    /// </summary>
    WorshipTarget = 236,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Worshiping".
    /// </summary>
    FogmanWorshipVictim = 237,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Foraging animals".
    /// </summary>
    LootAnimalsJob = 238,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Going to bed".
    /// </summary>
    GoHomeAndGoToBedSecure = 239,
    /// <summary>
    /// The Tasker factory builds <c>Task_LiftSomething</c>; TaskData text "Capture Target".
    /// </summary>
    LiftPersonSnatchingAllowedInTownOnly = 240,
    /// <summary>
    /// The Tasker factory builds <c>Task_Loot_ResourcesWeHaveStorageFor</c>; TaskData text "Looting resources".
    /// </summary>
    LootResourceItemsWeHaveStorageFor = 241,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Ditching resources".
    /// </summary>
    DitchAllResources = 242,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Getting food".
    /// </summary>
    AquireFoodAtHomebase = 243,
    /// <summary>
    /// The Tasker factory builds <c>Task_grabOneFood</c>; TaskData text "Grabbing food".
    /// </summary>
    GrabOneFood = 244,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Gathering unholy slaves".
    /// </summary>
    GatherSlavesFromCagesIfFemaleOrBeast = 245,
    /// <summary>
    /// The Tasker factory builds <c>Task_Kidnapping</c>; TaskData text "Kidnapping".
    /// </summary>
    KidnapOrder = 246,
    /// <summary>
    /// The Tasker factory builds <c>Task_EmptyMachine_BuildMats</c> / <c>Task_Pickup</c>; TaskData text "Collecting resource".
    /// </summary>
    CollectOutputResourceBuildMats = 247,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Attacking Squad".
    /// </summary>
    DefeatSquadLimitChaseRange = 248,
    /// <summary>
    /// The Tasker factory builds <c>Task_FirstAidRig</c>; TaskData text "Splinting".
    /// </summary>
    SplintOrder = 249,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Splint Rigging".
    /// </summary>
    SplintJob = 250,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    EscapeKidnap = 251,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    EscapeKidnapStr = 252,
    /// <summary>
    /// The Tasker factory builds <c>Task_Follow</c>; TaskData text "Following".
    /// </summary>
    FollowUrgentEscape = 253,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Kidnapper".
    /// </summary>
    FinalKidnapperCageJob = 254,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Reigning".
    /// </summary>
    SitOnThrone = 255,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetOutOfCage</c>; TaskData text "Get out of cage".
    /// </summary>
    GetOutOfCageOpportunistic = 256,
    /// <summary>
    /// The Tasker factory builds <c>Task_GetOutOfBed</c>; TaskData text "Getting out of bed".
    /// </summary>
    GetOutOfBedOnceHealed = 257,
    /// <summary>
    /// The Tasker factory builds <c>Task_UseBed</c>; TaskData text "Sleeping".
    /// </summary>
    UseBedOrder = 258,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Picking up food".
    /// </summary>
    EatFoodOnGround = 259,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Watching for new slaves".
    /// </summary>
    NewSlaveProcessing = 260,
    /// <summary>
    /// The Tasker factory builds <c>Task_SleepOnFloor</c>; TaskData text "Hiding".
    /// </summary>
    SleepOnFloorFakeAmbush = 261,
    /// <summary>
    /// The Tasker factory builds <c>Task_RangedAttack</c>; TaskData text "Shooting".
    /// </summary>
    RangedAttack = 262,
    /// <summary>
    /// The Tasker factory builds <c>Task_RangedAttack</c>; TaskData text "Shooting".
    /// </summary>
    RangedAttackFocused = 263,
    /// <summary>
    /// The Tasker factory builds <c>Task_EquipBestWeapon</c>; TaskData text "Equipping crossbow".
    /// </summary>
    EquipCrossbow = 264,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    UnequipCrossbow = 265,
    /// <summary>
    /// The Tasker factory builds <c>Task_RangedAttack</c>; TaskData text "Shooting".
    /// </summary>
    RangedAttackFocusedUnprovoked = 266,
    /// <summary>
    /// The Tasker factory builds <c>Task_MoveBowRange</c>; not in the TaskData registry.
    /// </summary>
    MoveInBowRange = 267,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Static guard indoors".
    /// </summary>
    StandAtGuardNodeHomebuildingIndoorsOnly = 268,
    /// <summary>
    /// The Tasker factory builds <c>Task_FirstAid</c>; TaskData text "Fixing Legs".
    /// </summary>
    HealMyLegs = 269,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Assaulting town".
    /// </summary>
    AssaultFortificationsPreferGates = 270,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); not in the TaskData registry.
    /// </summary>
    AssaultFortificationsPreferWalls = 271,
    /// <summary>
    /// The Tasker factory builds <c>Task_BashDoor</c>; TaskData text "Smashing".
    /// </summary>
    SmashBuilding = 272,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Remove Intruder".
    /// </summary>
    PickupIntrudersTown = 273,
    /// <summary>
    /// The Tasker factory builds <c>Task_TakeIntruderOutsideTown</c>; TaskData text "Throwing out".
    /// </summary>
    TakeIntruderOutsideTown = 274,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Sitting around".
    /// </summary>
    SitAround = 275,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Liberating prisoners".
    /// </summary>
    LiberateAllThePrisoners = 276,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Fetch".
    /// </summary>
    AnimalFetchALimb = 277,
    /// <summary>
    /// The Tasker factory builds <c>Task_DogLimbRunningPlay</c>; TaskData text "Playing 'Limbs'".
    /// </summary>
    PlayBecauseIHaveALimbInMouth = 278,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Limb Jealousy".
    /// </summary>
    ChaseAllyDogsWithMouthLimbs = 279,
    /// <summary>
    /// The Tasker factory builds <c>Task_Runaway</c>; TaskData text "Running away".
    /// </summary>
    RunAwayForced = 280,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Find and put in cage".
    /// </summary>
    FindCageAndPutDeadguyIn = 281,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Dinner time".
    /// </summary>
    EatARandomCageOccupantMeasuredRate = 282,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Shooing".
    /// </summary>
    ShooStrangersOutOfMyBuildingIfPrivate = 283,
    /// <summary>
    /// The Tasker factory builds <c>Task_EmptyMachine</c>; TaskData text "Looting".
    /// </summary>
    LootContainer = 284,
    /// <summary>
    /// The Tasker factory builds <c>Task_CutLock</c>; TaskData text "Cutting lock".
    /// </summary>
    CutLock = 285,
    /// <summary>
    /// The Tasker factory builds <c>Task_BruteForceLock</c>; TaskData text "Breaking lock".
    /// </summary>
    BruteForceLock = 286,
    /// <summary>
    /// The Tasker factory builds <c>Task_BashDoor</c>; TaskData text "Bashing door".
    /// </summary>
    BashDoorHere = 287,
    /// <summary>
    /// The Tasker factory builds the factory default <c>Task_Blank</c> (no act of its own); TaskData text "Protect allies".
    /// </summary>
    ProtectAlliesStayInTown = 288,
    /// <summary>
    /// The Tasker factory builds <c>Task_Follow</c>; TaskData text "Staying close".
    /// </summary>
    Unnamed289 = 289,
    /// <summary>
    /// The Tasker factory builds <c>Task_BashDoor</c>; TaskData text "Bashing gate".
    /// </summary>
    Unnamed290 = 290,
}