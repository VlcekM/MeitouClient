using System.Numerics;

namespace Meitou.Simulation.Movement;

/// <summary>What a character is doing (<see cref="CharacterHot.Task"/>). The first tasks of the movement stage; the AI packages replace them later.</summary>
public enum CharacterTask : byte
{
    Idle = 0,
    /// <summary>Walks to random places within the squad's home radius, waiting between them.</summary>
    Wander = 1,
    /// <summary>Walks to <see cref="CharacterHot.Goal"/> (a move order) and then idles.</summary>
    GoTo = 2,
    /// <summary>Keeps its formation place beside the squad leader.</summary>
    Follow = 3,
    /// <summary>Fights the target of an <see cref="Combat.AttackOrder"/> (the combat system owns the character's attacks and reactions; movement leaves it alone).</summary>
    Attack = 4,
    /// <summary>Walks to <see cref="CharacterCold.TreatTarget"/> and bandages it with a first aid kit (<see cref="FirstAid.FirstAidSystem"/> owns it; the movement system leaves it alone).</summary>
    Treat = 5,
    /// <summary>Sits on the ground resting: blood recovers at <c>resting heal rate mult</c> and hunger falls at <c>bed hunger rate</c> (<see cref="FirstAid.FirstAidSystem"/>); any other order replaces it.</summary>
    Rest = 6,
    /// <summary>Walks to <see cref="CharacterCold.CarryTarget"/> and lifts it onto the left shoulder (<see cref="Carrying.CarrySystem"/> owns it; the original's <c>LIFT_PERSON</c>).</summary>
    PickUp = 7,
    /// <summary>Sets the carried character down (<see cref="Carrying.CarrySystem"/>; the original's <c>PUT_DOWN_OBJECT</c>).</summary>
    PutDown = 8,
    /// <summary>Walks to <see cref="CharacterCold.CarryTarget"/> (a downed or dead character) and takes everything from its inventory that fits (<see cref="Carrying.CarrySystem"/>).</summary>
    Loot = 9,
    /// <summary>Creeps up behind <see cref="CharacterCold.CarryTarget"/> and swings to knock it out (<see cref="Carrying.CarrySystem"/>; the original's <c>Task_StealthKO</c>).</summary>
    StealthKo = 10,
    /// <summary>Walks to <see cref="CharacterCold.BuildSite"/> and builds it, delivering what it carries when the site wants it (<see cref="Construction.ConstructionSystem"/>; the original's <c>Task_Build</c>, orders 2 and 95).</summary>
    Build = 11,
    /// <summary>Walks to <see cref="CharacterCold.BuildSite"/> and takes it apart (<see cref="Construction.ConstructionSystem"/>; <c>Task_Dismantle</c>, order 96).</summary>
    Dismantle = 12,
    /// <summary>At <see cref="CharacterCold.BuildSite"/>, filling the 5 s bar that hands over one unit per missing material (<see cref="Construction.ConstructionSystem"/>; <c>Task_AddMaterialsToBuilding</c>).</summary>
    DeliverMaterials = 13,
    /// <summary>Walks to <see cref="CharacterCold.OperateSite"/> and works it: a well, a farm's harvest, a research bench (<see cref="Production.ProductionSystem"/>; the original's <c>Task_OperateMachine</c>).</summary>
    Operate = 14,
    /// <summary>A "Hauling to" job (<see cref="Production.PermaJobs"/>): walks to a machine for its output, then to the job's storage (<see cref="CharacterCold.HaulSite"/>; <see cref="Production.ProductionSystem"/>).</summary>
    Haul = 15,
    /// <summary>Stands where it is and prospects for one game second (<see cref="CharacterCold.Prospecting"/>, <see cref="Production.ProductionSystem"/>; the original's <c>Task_Prospecting</c>, order 150).</summary>
    Prospect = 16,
    /// <summary>
    /// A builder whose site waits for materials it does not carry walks to <see cref="CharacterCold.FetchSite"/> or ground item <see cref="CharacterCold.FetchItem"/> and takes
    /// <see cref="CharacterCold.FetchRecord"/>, then goes back to <see cref="CharacterCold.BuildSite"/> (<see cref="Construction.ConstructionSystem"/>; the original's task 247 "Collecting resource").
    /// </summary>
    FetchMaterials = 17,
    /// Walks to a bed and lies in it, then lies there (<see cref="Locks.BedSystem"/>; the original's <c>Task_UseBed</c>, orders 98, 100, 258). Lying in a bed (<see cref="Locks.HeldState.Bed"/>)
    /// with any other task, the character gets up: another order replaced it.
    /// </summary>
    Sleep = 18,
}

/// <summary>Bits of <see cref="CharacterHot.Flags"/> the movement system uses.</summary>
[Flags]
public enum MoveFlags : ushort
{
    None = 0,
    /// <summary>A path to <see cref="CharacterHot.Goal"/> is wanted; the next Schedule step asks the path service.</summary>
    NeedPath = 1,
    /// <summary>A path request is in flight.</summary>
    Pending = 2,
    /// <summary>The character is following <see cref="CharacterCold.Path"/>.</summary>
    HasPath = 4,
    /// <summary>A path is wanted, asked for or being followed.</summary>
    AnyPath = NeedPath | Pending | HasPath,
}

/// <summary>How a character's last walk ended (<see cref="CharacterCold.WalkEnd"/>).</summary>
public enum WalkEnd : byte
{
    /// <summary>Still walking, or another system stopped the walk.</summary>
    None,
    /// <summary>It followed its path to the last point.</summary>
    Finished,
    /// <summary>It gave the path up: no way there, blocked past the limit, or stopped at a locked door.</summary>
    Failed,
}

/// <summary>Speed modes (docs/game/pathfinding.md "Speed"), also the HUD speed icon's index (Walk, Jog, Run, Group): 0 caps at the race walk speed, 1 at 55, 2 does not cap, 3 keeps to the slowest of the squad.</summary>
public static class SpeedMode
{
    public const byte Walk = 0, Run = 1, Free = 2, Group = 3;
    /// <summary>The number of modes the HUD arrows cycle through.</summary>
    public const int Count = 4;
    public const float RunCap = 55;

    public static float Cap(byte mode, float walkSpeed) => mode switch { Walk => walkSpeed, Run => RunCap, _ => 999 };

    /// <summary>The speed above which a move toward a character runs: Task_Move's update (<c>FUN_140354600</c>, <c>DAT_141683188</c>, <b>Verified</b>) sets its mover to run each frame while its target character's current speed is above this.</summary>
    public const float ChaseRunAbove = 20;

    /// <summary><see cref="Free"/> (the mover's run) while the character in <paramref name="target"/> moves faster than <see cref="ChaseRunAbove"/>, else <paramref name="mode"/>.</summary>
    public static byte Chasing(CharacterTable table, int target, byte mode)
    {
        var v = table.Next[target].Velocity;
        return v.X * v.X + v.Z * v.Z > ChaseRunAbove * ChaseRunAbove ? Free : mode;
    }
}

/// <summary>The animation layers a character plays for its movement state (<see cref="CharacterHot.Animation"/>): the names are the skeleton animations of docs/animation.md.</summary>
public static class AnimationLayers
{
    public const string Idle = "idle_stand_relax";
    public const string WalkLower = "walk lower", WalkUpper = "walk upper";

    public static IReadOnlyList<AnimationLayer> For(ushort animation, float time) => animation == 0
        ? [new AnimationLayer(Idle, time, 1)]
        : [new AnimationLayer(WalkLower, time, 1), new AnimationLayer(WalkUpper, time, 1)];
}
