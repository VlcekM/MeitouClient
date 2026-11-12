namespace Meitou.Data.Gameplay.Ai;

/// <summary>
/// The original's TaskData state ids (docs/game/ai-tasks.md "StateType ids", Observed): the names its registry passes, spaces as underscores; a name
/// registered twice carries its id, the two empty names are <c>Unnamed&lt;id&gt;</c>.
/// </summary>
public enum StateType
{
    /// <summary>
    /// Registered as <c>none</c> (a name taken from the disassembly; the decompile shows no registration call for it).
    /// </summary>
    None = 0,
    /// <summary>
    /// Registered as <c>isAtLocation</c>.
    /// </summary>
    IsAtLocation = 1,
    /// <summary>
    /// Registered as <c>isBuildingComplete</c>.
    /// </summary>
    IsBuildingComplete = 2,
    /// <summary>
    /// Registered as <c>isSubjectInInventory</c>.
    /// </summary>
    IsSubjectInInventory = 3,
    /// <summary>
    /// Registered as <c>hasWeapon</c>.
    /// </summary>
    HasWeapon = 4,
    /// <summary>
    /// Registered as <c>hasWeaponEquipped</c>: a weapon in hand that is not ranged (<c>FUN_140596a30</c>, Verified).
    /// </summary>
    HasWeaponEquipped = 5,
    /// <summary>
    /// Registered as <c>isNoEnemiesInVicinity</c>.
    /// </summary>
    IsNoEnemiesInVicinity = 6,
    /// <summary>
    /// Registered as <c>isTargetDown</c>.
    /// </summary>
    IsTargetDown = 7,
    /// <summary>
    /// Registered as <c>isAlliesFighting</c>: true when a sensed ally scores above 0 in the fight threat (Verified);
    /// Meitou: <c>AiSystem.AlliesFighting</c>.
    /// </summary>
    IsAlliesFighting = 12,
    /// <summary>
    /// Registered as <c>isTargetEnemy</c>.
    /// </summary>
    IsTargetEnemy = 13,
    /// <summary>
    /// Registered as <c>isItemLootable</c>.
    /// </summary>
    IsItemLootable = 15,
    /// <summary>
    /// Registered as <c>isAggressionTowards</c>.
    /// </summary>
    IsAggressionTowards = 16,
    /// <summary>
    /// Registered as <c>AM_IDLE</c>.
    /// </summary>
    AmIdle = 17,
    /// <summary>
    /// Registered as <c>isAtHome</c>.
    /// </summary>
    IsAtHome = 18,
    /// <summary>
    /// Registered as <c>noneR</c>.
    /// </summary>
    NoneR = 20,
    /// <summary>
    /// Registered as <c>needsFirstAid</c>.
    /// </summary>
    NeedsFirstAid = 22,
    /// <summary>
    /// Registered as <c>haveFirstAidKit</c>: the items or the worn backpack hold a first-aid kit of class 1 (Verified).
    /// </summary>
    HaveFirstAidKit = 23,
    /// <summary>
    /// Registered as <c>isCrouching</c>.
    /// </summary>
    IsCrouching = 24,
    /// <summary>
    /// Registered as <c>isLyingProne</c>.
    /// </summary>
    IsLyingProne = 25,
    /// <summary>
    /// Registered as <c>IS_CROUCHING_OR_LYING</c>: posture 1, 2 or 3, in bed or two unidentified flags, false when crippled (Verified);
    /// Meitou: <c>CharacterFacts.CrouchingOrLying</c>.
    /// </summary>
    IsCrouchingOrLying = 26,
    /// <summary>
    /// Registered as <c>isMovementAllowed</c> (docs/game/ai-tasks.md "Perma-jobs" for its range).
    /// </summary>
    IsMovementAllowed = 27,
    /// <summary>
    /// Registered as <c>isNearTo</c>.
    /// </summary>
    IsNearTo = 28,
    /// <summary>
    /// Registered as <c>isGeneralAggressionLevel</c>.
    /// </summary>
    IsGeneralAggressionLevel = 29,
    /// <summary>
    /// Registered as <c>atNode</c>.
    /// </summary>
    AtNode = 31,
    /// <summary>
    /// Registered as <c>hasDestinationTown</c>.
    /// </summary>
    HasDestinationTown = 32,
    /// <summary>
    /// Registered with an empty name string.
    /// </summary>
    Unnamed33 = 33,
    /// <summary>
    /// Registered as <c>atDestinationTown</c>.
    /// </summary>
    AtDestinationTown = 36,
    /// <summary>
    /// Registered as <c>isReadyForAction</c>.
    /// </summary>
    IsReadyForAction = 37,
    /// <summary>
    /// Registered as <c>hasOneWorkingArm</c>.
    /// </summary>
    HasOneWorkingArm = 38,
    /// <summary>
    /// Registered as <c>isAggressionTowardsMe</c>: the aggression-towards evaluator asked of the character itself (Observed).
    /// </summary>
    IsAggressionTowardsMe = 39,
    /// <summary>
    /// Registered as <c>isWithin10Meters</c>: the ground-plane distance to the target under 100 units (Verified);
    /// Meitou: <c>AiSystem.Within10Meters</c>.
    /// </summary>
    IsWithin10Meters = 40,
    /// <summary>
    /// Registered as <c>isWithin50Meters</c>: the ground-plane distance to the target under 500 units, a position target also within 50 in height
    /// (Verified); Meitou: <c>AiSystem.Within50Meters</c>.
    /// </summary>
    IsWithin50Meters = 41,
    /// <summary>
    /// Registered as <c>targetStandingStill</c>.
    /// </summary>
    TargetStandingStill = 42,
    /// <summary>
    /// Registered as <c>iAmDown</c>.
    /// </summary>
    IAmDown = 43,
    /// <summary>
    /// Registered as <c>canGetUp</c>.
    /// </summary>
    CanGetUp = 45,
    /// <summary>
    /// Registered as <c>isAtLocation</c>: the second id of that name, which row 67 Running achieves (so it is no provider of 1).
    /// </summary>
    IsAtLocation46 = 46,
    /// <summary>
    /// Registered as <c>IS_CARRYING_SOMETHING</c>.
    /// </summary>
    IsCarryingSomething = 47,
    /// <summary>
    /// Registered as <c>IS_CARRYING_TARGET</c>.
    /// </summary>
    IsCarryingTarget = 48,
    /// <summary>
    /// Registered as <c>hasBuildingMaterials</c>.
    /// </summary>
    HasBuildingMaterials = 50,
    /// <summary>
    /// Registered as <c>buildingHasBuildingMaterials</c>.
    /// </summary>
    BuildingHasBuildingMaterials = 51,
    /// <summary>
    /// Registered as <c>DOOR_IS_OPEN</c>.
    /// </summary>
    DoorIsOpen = 52,
    /// <summary>
    /// Registered as <c>DOOR_IS_OPEN_HERE</c>.
    /// </summary>
    DoorIsOpenHere = 53,
    /// <summary>
    /// Registered as <c>isDoorLocked</c>.
    /// </summary>
    IsDoorLocked = 54,
    /// <summary>
    /// Registered as <c>DOOR_IS_LOCKED_HERE</c>.
    /// </summary>
    DoorIsLockedHere = 55,
    /// <summary>
    /// Registered as <c>DOOR_IS_BROKEN</c>.
    /// </summary>
    DoorIsBroken = 56,
    /// <summary>
    /// Registered as <c>AT_LOCATION_BUILDING_DOOR</c>.
    /// </summary>
    AtLocationBuildingDoor = 57,
    /// <summary>
    /// Registered as <c>AT_CURRENT_LOCATION_BUILDING_DOOR</c>.
    /// </summary>
    AtCurrentLocationBuildingDoor = 58,
    /// <summary>
    /// Registered as <c>DESTINATION_IS_ACCESSIBLE</c>.
    /// </summary>
    DestinationIsAccessible = 59,
    /// <summary>
    /// Registered as <c>LOCATION_IS_ACCESSIBLE</c>.
    /// </summary>
    LocationIsAccessible = 60,
    /// <summary>
    /// Registered as <c>machineHasEmptyInputs</c>: a building target has an empty input (Verified).
    /// </summary>
    MachineHasEmptyInputs = 62,
    /// <summary>
    /// Registered as <c>isMachineOutputFull</c>: the machine's output is full (Verified); Meitou evaluates it for a machine target.
    /// </summary>
    IsMachineOutputFull = 63,
    /// <summary>
    /// Registered as <c>isMachineJammed</c>: 62 or 63 of a building target (Verified); asked only by rows 91 and 90.
    /// </summary>
    IsMachineJammed = 64,
    /// <summary>
    /// Registered as <c>isAnyMachineJammed</c>: the 64 test over a hand list of the AI (Verified); not wired in Meitou.
    /// </summary>
    IsAnyMachineJammed = 65,
    /// <summary>
    /// Registered as <c>haveSomeResourcesFrom</c>.
    /// </summary>
    HaveSomeResourcesFrom = 66,
    /// <summary>
    /// Registered as <c>haveSomeResourcesFor</c>.
    /// </summary>
    HaveSomeResourcesFor = 67,
    /// <summary>
    /// Registered as <c>HAVE_SOME_LOOT_FROM</c>.
    /// </summary>
    HaveSomeLootFrom = 68,
    /// <summary>
    /// Registered as <c>isRouteTraceBlocked</c>.
    /// </summary>
    IsRouteTraceBlocked = 69,
    /// <summary>
    /// Registered as <c>isObjectMine</c>.
    /// </summary>
    IsObjectMine = 70,
    /// <summary>
    /// Registered as <c>isBuildingDamaged</c>.
    /// </summary>
    IsBuildingDamaged = 71,
    /// <summary>
    /// Registered as <c>isFullyRested</c>: false off the player's faction, for the player's a MedicalSystem value above 0.9 (Observed); Meitou: false
    /// off the player's side, else not incapacitated.
    /// </summary>
    IsFullyRested = 72,
    /// <summary>
    /// Registered as <c>machineHasFreeOperatorSlot</c>.
    /// </summary>
    MachineHasFreeOperatorSlot = 73,
    /// <summary>
    /// Registered as <c>isEnemyForceDefeated</c>.
    /// </summary>
    IsEnemyForceDefeated = 74,
    /// <summary>
    /// Registered as <c>isMessageDelivered</c>.
    /// </summary>
    IsMessageDelivered = 76,
    /// <summary>
    /// Registered as <c>isCarriedDudeInBed</c>.
    /// </summary>
    IsCarriedDudeInBed = 77,
    /// <summary>
    /// Registered as <c>TARGET_IS_IMPRISONED</c>.
    /// </summary>
    TargetIsImprisoned = 78,
    /// <summary>
    /// Registered as <c>I_AM_IMPRISONED</c>.
    /// </summary>
    AmImprisoned = 79,
    /// <summary>
    /// Registered as <c>isCarryingArmOK</c>: the left arm works (Verified); Meitou: <c>PlanContext.Holds</c>.
    /// </summary>
    IsCarryingArmOk = 80,
    /// <summary>
    /// Registered as <c>CARRIED_DUDE_NOW_IN_CAGE</c>.
    /// </summary>
    CarriedDudeNowInCage = 81,
    /// <summary>
    /// Registered as <c>gotInventoryRoomForResources</c>: the inventory, else the worn backpack, has room for the target's item type, else the
    /// animal-carry path (Verified).
    /// </summary>
    GotInventoryRoomForResources = 82,
    /// <summary>
    /// Registered as <c>isInBed</c>.
    /// </summary>
    IsInBed = 83,
    /// <summary>
    /// Registered as <c>isAtAShop</c>.
    /// </summary>
    IsAtAShop = 84,
    /// <summary>
    /// Registered with an empty name string.
    /// </summary>
    Unnamed85 = 85,
    /// <summary>
    /// Registered as <c>atATown</c>.
    /// </summary>
    AtATown = 86,
    /// <summary>
    /// Registered as <c>allDoorsLocked</c>.
    /// </summary>
    AllDoorsLocked = 87,
    /// <summary>
    /// Registered as <c>isInsideTargetBuilding</c>.
    /// </summary>
    IsInsideTargetBuilding = 88,
    /// <summary>
    /// Registered as <c>haveSomeResourcesFromThisMachineButWantThemGoneIfPossible</c>.
    /// </summary>
    HaveSomeResourcesFromThisMachineButWantThemGoneIfPossible = 89,
    /// <summary>
    /// Registered as <c>DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS</c>.
    /// </summary>
    DestinationNotBlockedByFortifications = 90,
    /// <summary>
    /// Registered as <c>AT_LOCATION_FORTIFICATION_GATE</c>.
    /// </summary>
    AtLocationFortificationGate = 91,
    /// <summary>
    /// Registered as <c>isEnemy</c>.
    /// </summary>
    IsEnemy = 92,
    /// <summary>
    /// Registered as <c>BUILDING_HAS_POWER</c>.
    /// </summary>
    BuildingHasPower = 93,
    /// <summary>
    /// Registered as <c>atDestinationTown</c>.
    /// </summary>
    AtDestinationTown94 = 94,
    /// <summary>
    /// Registered as <c>isAtHomeTown</c>.
    /// </summary>
    IsAtHomeTown = 95,
    /// <summary>
    /// Registered as <c>CAGES_ARE_ALL_FULL</c>.
    /// </summary>
    CagesAreAllFull = 97,
    /// <summary>
    /// Registered as <c>MACHINE_OCCUPANT_IS_ALIVE</c>.
    /// </summary>
    MachineOccupantIsAlive = 98,
    /// <summary>
    /// Registered as <c>isCarriedDudeDead</c>.
    /// </summary>
    IsCarriedDudeDead = 99,
    /// <summary>
    /// Registered as <c>CARRIED_DUDE_NOW_IN_DISPOSAL_MACHINE</c>.
    /// </summary>
    CarriedDudeNowInDisposalMachine = 100,
    /// <summary>
    /// Registered as <c>isLeaderDown</c>.
    /// </summary>
    IsLeaderDown = 101,
    /// <summary>
    /// Registered as <c>isSquadmatesFighting</c>.
    /// </summary>
    IsSquadmatesFighting = 102,
    /// <summary>
    /// Registered as <c>myLegsAreMessedUp</c>: a leg part is down and the KO point is under its max health less stun, false with a leg
    /// stump (Verified).
    /// </summary>
    MyLegsAreMessedUp = 103,
    /// <summary>
    /// Registered as <c>using target turret</c>.
    /// </summary>
    UsingTargetTurret = 104,
    /// <summary>
    /// Registered as <c>isMachineOutputEmpty</c>.
    /// </summary>
    IsMachineOutputEmpty = 105,
    /// <summary>
    /// Registered as <c>machineHasFullInputs</c>.
    /// </summary>
    MachineHasFullInputs = 106,
    /// <summary>
    /// Registered as <c>I_AM_INSIDE_TARGET_BUILDING</c>.
    /// </summary>
    AmInsideTargetBuilding = 107,
    /// <summary>
    /// Registered as <c>strangers in house</c>: the stranger finder above 0 (Verified); not wired in Meitou.
    /// </summary>
    StrangersInHouse = 108,
    /// <summary>
    /// Registered as <c>isDialogDelivered</c>.
    /// </summary>
    IsDialogDelivered = 109,
    /// <summary>
    /// Registered as <c>isDialogDelivered</c>.
    /// </summary>
    IsDialogDelivered110 = 110,
    /// <summary>
    /// Registered as <c>AT_LOCATION_BUILDING_DOOR_INSIDE</c>.
    /// </summary>
    AtLocationBuildingDoorInside = 111,
    /// <summary>
    /// Registered as <c>DOOR_IS_LOCKED_AND_IM_INSIDE</c>.
    /// </summary>
    DoorIsLockedAndImInside = 112,
    /// <summary>
    /// Registered as <c>targetAtATown</c>.
    /// </summary>
    TargetAtATown = 113,
    /// <summary>
    /// Registered as <c>isWearingShackles_locked</c>.
    /// </summary>
    IsWearingShacklesLocked = 114,
    /// <summary>
    /// Registered as <c>IS_CARRYING_TARGET_HEALTHY</c>.
    /// </summary>
    IsCarryingTargetHealthy = 115,
    /// <summary>
    /// Registered as <c>TARGET_IS_MY_SLAVE</c>.
    /// </summary>
    TargetIsMySlave = 116,
    /// <summary>
    /// Registered as <c>TARGET_HAS_BEEN_LOOTED</c>.
    /// </summary>
    TargetHasBeenLooted = 117,
    /// <summary>
    /// Registered as <c>TARGET_IS_UNARMED</c>.
    /// </summary>
    TargetIsUnarmed = 118,
    /// <summary>
    /// Registered as <c>TARGET_LOOKS_LIKE_A_SLAVE</c>.
    /// </summary>
    TargetLooksLikeASlave = 120,
    /// <summary>
    /// Registered as <c>CARRYING_EXCESS_LOOT</c>: the inventory or worn backpack holds a stolen item (Verified).
    /// </summary>
    CarryingExcessLoot = 121,
    /// <summary>
    /// Registered as <c>hasTools</c>.
    /// </summary>
    HasTools = 122,
    /// <summary>
    /// Registered as <c>IS_WORKING</c>.
    /// </summary>
    IsWorking = 123,
    /// <summary>
    /// Registered as <c>isTargetEscapee</c>.
    /// </summary>
    IsTargetEscapee = 124,
    /// <summary>
    /// Registered as <c>TARGET_IS_CONSCIOUS</c>: a character target not down, unconscious or dead; a non-character target true, none false
    /// (Verified); no row provides it.
    /// </summary>
    TargetIsConscious = 125,
    /// <summary>
    /// Registered as <c>AT_LOCATION_BUILDING_DOOR_OUTSIDE</c>.
    /// </summary>
    AtLocationBuildingDoorOutside = 126,
    /// <summary>
    /// Registered as <c>DOOR_IS_LOCKED_AND_IM_OUTSIDE</c>.
    /// </summary>
    DoorIsLockedAndImOutside = 127,
    /// <summary>
    /// Registered as <c>isAlly</c>.
    /// </summary>
    IsAlly = 128,
    /// <summary>
    /// Registered as <c>TARGET_IS_ARRESTED</c>.
    /// </summary>
    TargetIsArrested = 129,
    /// <summary>
    /// Registered as <c>ALARMS_IN_THE_VICINITY</c>.
    /// </summary>
    AlarmsInTheVicinity = 130,
    /// <summary>
    /// Registered as <c>atPackageTarget</c>.
    /// </summary>
    AtPackageTarget = 131,
    /// <summary>
    /// Registered as <c>haveroboAidKit</c>.
    /// </summary>
    HaveroboAidKit = 132,
    /// <summary>
    /// Registered as <c>needsFirstAid_robot</c>.
    /// </summary>
    NeedsFirstAidRobot = 133,
    /// <summary>
    /// Registered as <c>isChained</c>.
    /// </summary>
    IsChained = 134,
    /// <summary>
    /// Registered as <c>TARGET_IS_EATEN</c>.
    /// </summary>
    TargetIsEaten = 135,
    /// <summary>
    /// Registered as <c>IS_WORKING_PRETEND</c>.
    /// </summary>
    IsWorkingPretend = 136,
    /// <summary>
    /// Registered as <c>INTRUDER_IS_OUTSIDE</c>.
    /// </summary>
    IntruderIsOutside = 137,
    /// <summary>
    /// Registered as <c>me swim</c>: water state 3 (deep water), 0 when held in a bed or cage (Verified).
    /// </summary>
    MeSwim = 139,
    /// <summary>
    /// Registered as <c>target swimming</c>.
    /// </summary>
    TargetSwimming = 140,
    /// <summary>
    /// Registered as <c>strangers in house P</c>: false when the building is public, else as 108 (Verified).
    /// </summary>
    StrangersInHouseP = 141,
    /// <summary>
    /// Registered as <c>isTargetRestrainedOrKO</c>.
    /// </summary>
    IsTargetRestrainedOrKo = 142,
    /// <summary>
    /// Registered as <c>farm has food</c>.
    /// </summary>
    FarmHasFood = 143,
    /// <summary>
    /// Registered as <c>am hungry</c>.
    /// </summary>
    AmHungry = 144,
    /// <summary>
    /// Registered as <c>using any turret</c>.
    /// </summary>
    UsingAnyTurret = 145,
    /// <summary>
    /// Registered as <c>target worshiped</c>.
    /// </summary>
    TargetWorshiped = 146,
    /// <summary>
    /// Registered as <c>DITCHED_ALL_RESOURCES</c>.
    /// </summary>
    DitchedAllResources = 147,
    /// <summary>
    /// Registered as <c>LOOTED_STORABLE_ITEMS</c>.
    /// </summary>
    LootedStorableItems = 148,
    /// <summary>
    /// Registered as <c>gotAFoodItem</c>.
    /// </summary>
    GotAFoodItem = 149,
    /// <summary>
    /// Registered as <c>HAVE_SOME_BUILD_MATS</c>.
    /// </summary>
    HaveSomeBuildMats = 150,
    /// <summary>
    /// Registered as <c>hasSplintKit</c>.
    /// </summary>
    HasSplintKit = 151,
    /// <summary>
    /// Registered as <c>needsSplint</c>.
    /// </summary>
    NeedsSplint = 152,
    /// <summary>
    /// Registered as <c>FINAL_GOT_A_KIDNAP_VICTIM</c>.
    /// </summary>
    FinalGotAKidnapVictim = 153,
    /// <summary>
    /// Registered as <c>FINAL_FOUND_AND_PUT_CARRIED_IN_A_CAGE</c>.
    /// </summary>
    FinalFoundAndPutCarriedInACage = 154,
    /// <summary>
    /// Registered as <c>iHaveSomeSlaves</c>.
    /// </summary>
    IHaveSomeSlaves = 155,
    /// <summary>
    /// Registered as <c>hasWeaponEquipped</c>: a ranged weapon in hand (<c>FUN_140596a70</c>, Verified).
    /// </summary>
    HasWeaponEquipped156 = 156,
    /// <summary>
    /// Registered as <c>hasWeaponEquipped</c>.
    /// </summary>
    HasWeaponEquipped157 = 157,
    /// <summary>
    /// Registered as <c>iAmBeingMeleeAttacked immediate</c>.
    /// </summary>
    IAmBeingMeleeAttackedImmediate = 158,
    /// <summary>
    /// Registered as <c>iAmBeingMeleeAttacked general</c>.
    /// </summary>
    IAmBeingMeleeAttackedGeneral = 159,
    /// <summary>
    /// Registered as <c>isWithinMyBowRange</c>.
    /// </summary>
    IsWithinMyBowRange = 160,
    /// <summary>
    /// Registered as <c>hasTwoWorkingArm</c>.
    /// </summary>
    HasTwoWorkingArm = 161,
    /// <summary>
    /// Registered as <c>iAmProne</c>.
    /// </summary>
    IAmProne = 162,
    /// <summary>
    /// Registered as <c>isBuildingDestroyed</c>.
    /// </summary>
    IsBuildingDestroyed = 163,
    /// <summary>
    /// Registered as <c>INTRUDER_IS_OUTSIDE_GATES</c>.
    /// </summary>
    IntruderIsOutsideGates = 164,
    /// <summary>
    /// Registered as <c>crowdLimit8</c>.
    /// </summary>
    CrowdLimit8 = 165,
    /// <summary>
    /// Registered as <c>iHaveSufficientLockSkill</c>.
    /// </summary>
    IHaveSufficientLockSkill = 166,
    /// <summary>
    /// Registered as <c>iHaveSufficientLockSkillForShackles</c>.
    /// </summary>
    IHaveSufficientLockSkillForShackles = 167,
    /// <summary>
    /// Registered as <c>DESTINATION_IS_ACCESSIBLE_ANIMAL</c>.
    /// </summary>
    DestinationIsAccessibleAnimal = 168,
    /// <summary>
    /// Registered as <c>isMachineInputInvalid</c>.
    /// </summary>
    IsMachineInputInvalid = 169,
}