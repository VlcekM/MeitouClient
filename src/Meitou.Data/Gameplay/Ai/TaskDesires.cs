namespace Meitou.Data.Gameplay.Ai;

/// <summary>
/// What a TaskData row's desire member (<c>+0x108</c>) is (docs/game/ai-tasks.md "Desire and commitment").
/// </summary>
public enum TaskDesireKind : byte
{
    /// <summary>
    /// A constant (<see cref="TaskDesire.Value"/>).
    /// </summary>
    Constant,
    /// <summary>
    /// AI slot 48 (<c>FUN_1406415e0</c>): 0.01 + 0.4 x a fresh roll each time it is asked.
    /// </summary>
    Random,
    /// <summary>
    /// A function of its own that reads state.
    /// </summary>
    Function,
}

/// <summary>
/// A row's desire member as its registration sets it: the kind, the constant's value and where it is (the AI vtable slot or the function).
/// </summary>
public readonly record struct TaskDesire(TaskDesireKind Kind, float Value, string Source);

/// <summary>
/// The desire member of each of the 253 TaskData rows (<b>Verified</b>: the member stored before each registration call <c>FUN_14060ea00</c> in
/// <c>FUN_140319a60</c>, each stub followed through its <c>jmp</c> to the AI vtable slot or the function in the 1.0.68 exe bytes;
/// docs/game/ai-tasks.md "Desire and commitment"). 111 rows not listed here have AI slot 19's 1.0 (<c>FUN_140641550</c>); of those listed, 33 are other constants
/// (slots 57 and 43: 1.0; 39: 0.1; 40: 0.25; 41: 0.5; 46: 1.5; 47 and 58: 2.0), 5 random (slot 48) and 104 functions of their own.
/// </summary>
public static class TaskDesires
{
    /// <summary>
    /// AI slot 19's constant (<c>FUN_140641550</c>, <c>DAT_14167c308</c>).
    /// </summary>
    static readonly TaskDesire Slot19 = new(TaskDesireKind.Constant, 1, "AI slot 19 FUN_140641550");

    /// <summary>
    /// The rows whose desire member is not slot 19's.
    /// </summary>
    static readonly Dictionary<TaskType, TaskDesire> Table = new()
    {
        [TaskType.MoveOnFreeWill] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.Idle] = new(TaskDesireKind.Constant, 0.1f, "AI slot 39 FUN_140641560"),
        [TaskType.StandAtShopkeeperNode] = new(TaskDesireKind.Constant, 0.5f, "AI slot 41 FUN_140641580"),
        [TaskType.Patrol] = new(TaskDesireKind.Constant, 0.25f, "AI slot 40 FUN_140641570"),
        [TaskType.PatrolTown] = new(TaskDesireKind.Constant, 0.25f, "AI slot 40 FUN_140641570"),
        [TaskType.StandAtGuardNodeHomebuilding] = new(TaskDesireKind.Constant, 0.5f, "AI slot 41 FUN_140641580"),
        [TaskType.AttackEnemiesOfMySlavemaster] = new(TaskDesireKind.Constant, 2, "AI slot 47 FUN_1406415d0"),
        [TaskType.NotBeUnarmed] = new(TaskDesireKind.Constant, 2, "AI slot 58 FUN_140641610"),
        [TaskType.StandAtGeneralNode] = new(TaskDesireKind.Constant, 0.5f, "AI slot 41 FUN_140641580"),
        [TaskType.StandAtNode] = new(TaskDesireKind.Constant, 0.5f, "AI slot 41 FUN_140641580"),
        [TaskType.TravelToTargetTown] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.MoveOnFreeWillFast] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.PickLock] = new(TaskDesireKind.Constant, 0.1f, "AI slot 39 FUN_140641560"),
        [TaskType.MoveToBuildingDoor] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.MoveToCurrentLocationBuildingDoor] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.AlwaysImpossibleTask] = new(TaskDesireKind.Constant, 0.1f, "AI slot 39 FUN_140641560"),
        [TaskType.MoveToFortificationGate] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.OpenFortificationGate] = new(TaskDesireKind.Constant, 1.5f, "AI slot 46 FUN_1406415c0"),
        [TaskType.TravelToTargetTownFast] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.GetOutOfCageLegit] = new(TaskDesireKind.Constant, 0.5f, "AI slot 41 FUN_140641580"),
        [TaskType.DeadGuysGoInThePot] = new(TaskDesireKind.Constant, 1, "AI slot 43 FUN_1406415a0"),
        [TaskType.TerritorialAggressionButDontLeaveHome] = new(TaskDesireKind.Constant, 0.5f, "AI slot 41 FUN_140641580"),
        [TaskType.Prospecting] = new(TaskDesireKind.Constant, 1, "AI slot 43 FUN_1406415a0"),
        [TaskType.MoveToBuildingDoorInsidepos] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.PutLootInStorage] = new(TaskDesireKind.Constant, 2, "AI slot 47 FUN_1406415d0"),
        [TaskType.BruteForceShackles] = new(TaskDesireKind.Constant, 0.1f, "AI slot 39 FUN_140641560"),
        [TaskType.MoveToBuildingDoorOutsidepos] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.PickLockOnShackles] = new(TaskDesireKind.Constant, 0.1f, "AI slot 39 FUN_140641560"),
        [TaskType.TotalEscape] = new(TaskDesireKind.Constant, 2, "AI slot 47 FUN_1406415d0"),
        [TaskType.TravelToTargetPackage] = new(TaskDesireKind.Constant, 1, "AI slot 57 FUN_1405983f0"),
        [TaskType.TakeOffMyShackles] = new(TaskDesireKind.Constant, 2, "AI slot 47 FUN_1406415d0"),
        [TaskType.StandAtGuardNodeHomebuildingIndoorsOnly] = new(TaskDesireKind.Constant, 0.5f, "AI slot 41 FUN_140641580"),
        [TaskType.BruteForceLock] = new(TaskDesireKind.Constant, 0.1f, "AI slot 39 FUN_140641560"),
        [TaskType.WanderTown] = new(TaskDesireKind.Random, 0, "AI slot 48 FUN_1406415e0"),
        [TaskType.StayInHome] = new(TaskDesireKind.Random, 0, "AI slot 48 FUN_1406415e0"),
        [TaskType.BuyShit] = new(TaskDesireKind.Random, 0, "AI slot 48 FUN_1406415e0"),
        [TaskType.StandAtGuardNodeHometownOutside] = new(TaskDesireKind.Random, 0, "AI slot 48 FUN_1406415e0"),
        [TaskType.SitAround] = new(TaskDesireKind.Random, 0, "AI slot 48 FUN_1406415e0"),
        [TaskType.Pickup] = new(TaskDesireKind.Function, 0, "AI slot 72 FUN_140792650"),
        [TaskType.MeleeAttack] = new(TaskDesireKind.Function, 0, "AI slot 53 FUN_1405a4260"),
        [TaskType.ProtectAllies] = new(TaskDesireKind.Function, 0, "AI slot 14 FUN_140599b90"),
        [TaskType.AttackEnemies] = new(TaskDesireKind.Function, 0, "AI slot 52 FUN_140598020"),
        [TaskType.Protection] = new(TaskDesireKind.Function, 0, "AI slot 51 FUN_140597ff0"),
        [TaskType.FirstAidOrder] = new(TaskDesireKind.Function, 0, "FUN_14099e0a0"),
        [TaskType.StandUp] = new(TaskDesireKind.Function, 0, "FUN_140317bd0"),
        [TaskType.StayCloseToTarget] = new(TaskDesireKind.Function, 0, "AI slot 15 FUN_1405a33f0"),
        [TaskType.SelfPreservation] = new(TaskDesireKind.Function, 0, "AI slot 17 FUN_140599d20"),
        [TaskType.RunAway] = new(TaskDesireKind.Function, 0, "AI slot 55 FUN_1405a34e0"),
        [TaskType.GetNearTo] = new(TaskDesireKind.Function, 0, "AI slot 15 FUN_1405a33f0"),
        [TaskType.FollowPlayerOrder] = new(TaskDesireKind.Function, 0, "AI slot 15 FUN_1405a33f0"),
        [TaskType.Bodyguard] = new(TaskDesireKind.Function, 0, "AI slot 44 FUN_1405991c0"),
        [TaskType.Aoeu] = new(TaskDesireKind.Function, 0, "FUN_140598cc0"),
        [TaskType.JobRepairRobot] = new(TaskDesireKind.Function, 0, "FUN_140998410"),
        [TaskType.JobMedic] = new(TaskDesireKind.Function, 0, "FUN_140998410"),
        [TaskType.GetReadyForAction] = new(TaskDesireKind.Function, 0, "AI slot 34 FUN_14059a260"),
        [TaskType.FirstAidRobot] = new(TaskDesireKind.Function, 0, "FUN_14099df10"),
        [TaskType.GetUp] = new(TaskDesireKind.Function, 0, "FUN_140598c80"),
        [TaskType.LiftPerson] = new(TaskDesireKind.Function, 0, "AI slot 59 FUN_14059e1a0"),
        [TaskType.UnlockDoorHere] = new(TaskDesireKind.Function, 0, "AI slot 27 FUN_140599100"),
        [TaskType.BashDoor] = new(TaskDesireKind.Function, 0, "AI slot 36 FUN_14079d1a0"),
        [TaskType.CollectOutputResource] = new(TaskDesireKind.Function, 0, "AI slot 22 FUN_1405a6860"),
        [TaskType.UseBed] = new(TaskDesireKind.Function, 0, "AI slot 30 FUN_1405a0880"),
        [TaskType.FindAndRescue] = new(TaskDesireKind.Function, 0, "FUN_140599480"),
        [TaskType.FindBedAndPutIn] = new(TaskDesireKind.Function, 0, "FUN_1405a0d50"),
        [TaskType.FindCageAndPutIn] = new(TaskDesireKind.Function, 0, "FUN_1405a1140"),
        [TaskType.Shopping] = new(TaskDesireKind.Function, 0, "FUN_140999620"),
        [TaskType.BashGate] = new(TaskDesireKind.Function, 0, "AI slot 37 FUN_1405992f0"),
        [TaskType.RunAwayHometown] = new(TaskDesireKind.Function, 0, "AI slot 56 FUN_1405a3710"),
        [TaskType.RetreatHometown] = new(TaskDesireKind.Function, 0, "AI slot 56 FUN_1405a3710"),
        [TaskType.FindAndKidnap] = new(TaskDesireKind.Function, 0, "FUN_14059a600"),
        [TaskType.KillARandomCageOccupant] = new(TaskDesireKind.Function, 0, "AI slot 20 FUN_14032e9b0"),
        [TaskType.FindADeadGuy] = new(TaskDesireKind.Function, 0, "FUN_140599480"),
        [TaskType.FollowSquadleader] = new(TaskDesireKind.Function, 0, "AI slot 15 FUN_1405a33f0"),
        [TaskType.FindAndRescueLeader] = new(TaskDesireKind.Function, 0, "FUN_1405993f0"),
        [TaskType.ProtectOwnSquad] = new(TaskDesireKind.Function, 0, "AI slot 14 FUN_140599b90"),
        [TaskType.UseTurret] = new(TaskDesireKind.Function, 0, "AI slot 35 FUN_140598080"),
        [TaskType.FindAndRescueIfTheresBeds] = new(TaskDesireKind.Function, 0, "FUN_1405994c0"),
        [TaskType.ManATurret] = new(TaskDesireKind.Function, 0, "AI slot 35 FUN_140598080"),
        [TaskType.OperateAutomaticMachinery] = new(TaskDesireKind.Function, 0, "AI slot 21 FUN_14059df00"),
        [TaskType.GoHomeAndGoToBed] = new(TaskDesireKind.Function, 0, "AI slot 30 FUN_1405a0880"),
        [TaskType.LockAllMyDoors] = new(TaskDesireKind.Function, 0, "AI slot 31 FUN_140795f80"),
        [TaskType.EnterBuilding] = new(TaskDesireKind.Function, 0, "FUN_140317c00"),
        [TaskType.CaptureNewSlaves] = new(TaskDesireKind.Function, 0, "AI slot 61 FUN_140597b20"),
        [TaskType.CarryWoundedSlaves] = new(TaskDesireKind.Function, 0, "FUN_140599480"),
        [TaskType.PutDownCarriedDudeIfTheyCanWalk] = new(TaskDesireKind.Function, 0, "FUN_1405a0710"),
        [TaskType.LiftObjectButHealFirst] = new(TaskDesireKind.Function, 0, "AI slot 59 FUN_14059e1a0"),
        [TaskType.GatherSlavesFromCages] = new(TaskDesireKind.Function, 0, "AI slot 63 FUN_14059b940"),
        [TaskType.SleepOnFloor] = new(TaskDesireKind.Function, 0, "AI slot 30 FUN_1405a0880"),
        [TaskType.HuntingBloodsmell] = new(TaskDesireKind.Function, 0, "AI slot 49 FUN_140794b60"),
        [TaskType.LootTheDead] = new(TaskDesireKind.Function, 0, "AI slot 62 FUN_140597ae0"),
        [TaskType.SlaveWatching] = new(TaskDesireKind.Function, 0, "AI slot 61 FUN_140597b20"),
        [TaskType.WorkTheSlaves] = new(TaskDesireKind.Function, 0, "AI slot 65 FUN_14059a0a0"),
        [TaskType.GoToSomewhereForDeliveringSlaves] = new(TaskDesireKind.Function, 0, "AI slot 65 FUN_14059a0a0"),
        [TaskType.GiveAllMySlavesTo] = new(TaskDesireKind.Function, 0, "AI slot 65 FUN_14059a0a0"),
        [TaskType.LockAllMyDoorsFromOutside] = new(TaskDesireKind.Function, 0, "AI slot 31 FUN_140795f80"),
        [TaskType.LeaveBuilding] = new(TaskDesireKind.Function, 0, "FUN_140317c40"),
        [TaskType.ArrestTarget] = new(TaskDesireKind.Function, 0, "AI slot 32 FUN_140598a40"),
        [TaskType.HuntBounties] = new(TaskDesireKind.Function, 0, "AI slot 33 FUN_140598a90"),
        [TaskType.GetOutOfBedIfItsEmergency] = new(TaskDesireKind.Function, 0, "AI slot 28 FUN_1405a0b50"),
        [TaskType.LiftPersonSnatchingAllowed] = new(TaskDesireKind.Function, 0, "AI slot 59 FUN_14059e1a0"),
        [TaskType.GatherSlavesFromCagesIfItsAnExportTown] = new(TaskDesireKind.Function, 0, "AI slot 64 FUN_140597b80"),
        [TaskType.GiveAllMySlavesToIfItsAnImportTown] = new(TaskDesireKind.Function, 0, "AI slot 66 FUN_140597c70"),
        [TaskType.ManATurretOnBuilding] = new(TaskDesireKind.Function, 0, "AI slot 35 FUN_140598080"),
        [TaskType.PickupIntruders] = new(TaskDesireKind.Function, 0, "AI slot 59 FUN_14059e1a0"),
        [TaskType.TakeIntruderOutside] = new(TaskDesireKind.Function, 0, "FUN_1405a13a0"),
        [TaskType.LiftPersonPlayerOrder] = new(TaskDesireKind.Function, 0, "AI slot 59 FUN_14059e1a0"),
        [TaskType.MeleeAttackAnimal] = new(TaskDesireKind.Function, 0, "AI slot 53 FUN_1405a4260"),
        [TaskType.ManATurretPlayerJob] = new(TaskDesireKind.Function, 0, "AI slot 35 FUN_140598080"),
        [TaskType.LootAnimalsJob] = new(TaskDesireKind.Function, 0, "AI slot 62 FUN_140597ae0"),
        [TaskType.GoHomeAndGoToBedSecure] = new(TaskDesireKind.Function, 0, "AI slot 30 FUN_1405a0880"),
        [TaskType.LiftPersonSnatchingAllowedInTownOnly] = new(TaskDesireKind.Function, 0, "AI slot 59 FUN_14059e1a0"),
        [TaskType.AquireFoodAtHomebase] = new(TaskDesireKind.Function, 0, "AI slot 26 FUN_140597cb0"),
        [TaskType.GatherSlavesFromCagesIfFemaleOrBeast] = new(TaskDesireKind.Function, 0, "AI slot 63 FUN_14059b940"),
        [TaskType.KidnapOrder] = new(TaskDesireKind.Function, 0, "AI slot 59 FUN_14059e1a0"),
        [TaskType.CollectOutputResourceBuildMats] = new(TaskDesireKind.Function, 0, "AI slot 23 FUN_14059a110"),
        [TaskType.DefeatSquadLimitChaseRange] = new(TaskDesireKind.Function, 0, "AI slot 32 FUN_140598a40"),
        [TaskType.SplintOrder] = new(TaskDesireKind.Function, 0, "FUN_14099dc40"),
        [TaskType.SplintJob] = new(TaskDesireKind.Function, 0, "FUN_140998410"),
        [TaskType.FollowUrgentEscape] = new(TaskDesireKind.Function, 0, "AI slot 16 FUN_140597900"),
        [TaskType.FinalKidnapperCageJob] = new(TaskDesireKind.Function, 0, "FUN_1405a1280"),
        [TaskType.SitOnThrone] = new(TaskDesireKind.Function, 0, "AI slot 67 FUN_140597c20"),
        [TaskType.GetOutOfCageOpportunistic] = new(TaskDesireKind.Function, 0, "AI slot 38 FUN_140599260"),
        [TaskType.GetOutOfBedOnceHealed] = new(TaskDesireKind.Function, 0, "AI slot 29 FUN_140599160"),
        [TaskType.EatFoodOnGround] = new(TaskDesireKind.Function, 0, "AI slot 25 FUN_140597d30"),
        [TaskType.NewSlaveProcessing] = new(TaskDesireKind.Function, 0, "AI slot 61 FUN_140597b20"),
        [TaskType.SleepOnFloorFakeAmbush] = new(TaskDesireKind.Function, 0, "AI slot 30 FUN_1405a0880"),
        [TaskType.RangedAttack] = new(TaskDesireKind.Function, 0, "AI slot 54 FUN_140598260"),
        [TaskType.RangedAttackFocused] = new(TaskDesireKind.Function, 0, "AI slot 54 FUN_140598260"),
        [TaskType.RangedAttackFocusedUnprovoked] = new(TaskDesireKind.Function, 0, "AI slot 54 FUN_140598260"),
        [TaskType.HealMyLegs] = new(TaskDesireKind.Function, 0, "FUN_1409983b0"),
        [TaskType.AssaultFortificationsPreferGates] = new(TaskDesireKind.Function, 0, "AI slot 68 FUN_140597a20"),
        [TaskType.SmashBuilding] = new(TaskDesireKind.Function, 0, "AI slot 37 FUN_1405992f0"),
        [TaskType.PickupIntrudersTown] = new(TaskDesireKind.Function, 0, "AI slot 60 FUN_140597f20"),
        [TaskType.TakeIntruderOutsideTown] = new(TaskDesireKind.Function, 0, "FUN_1405a13a0"),
        [TaskType.PlayBecauseIHaveALimbInMouth] = new(TaskDesireKind.Function, 0, "AI slot 69 FUN_140599720"),
        [TaskType.ChaseAllyDogsWithMouthLimbs] = new(TaskDesireKind.Function, 0, "AI slot 70 FUN_140599800"),
        [TaskType.FindCageAndPutDeadguyIn] = new(TaskDesireKind.Function, 0, "FUN_1405a1140"),
        [TaskType.EatARandomCageOccupantMeasuredRate] = new(TaskDesireKind.Function, 0, "AI slot 20 FUN_14032e9b0"),
        [TaskType.ShooStrangersOutOfMyBuildingIfPrivate] = new(TaskDesireKind.Function, 0, "AI slot 71 FUN_1405998b0"),
        [TaskType.BashDoorHere] = new(TaskDesireKind.Function, 0, "AI slot 36 FUN_14079d1a0"),
        [TaskType.ProtectAlliesStayInTown] = new(TaskDesireKind.Function, 0, "AI slot 14 FUN_140599b90"),
        [TaskType.Unnamed289] = new(TaskDesireKind.Function, 0, "AI slot 15 FUN_1405a33f0"),
    };

    /// <summary>
    /// The desire member of a row; null for a type with no TaskData row.
    /// </summary>
    public static TaskDesire? Of(TaskType type) => TaskDataTable.Of(type) is null ? null : Table.GetValueOrDefault(type, Slot19);
}
