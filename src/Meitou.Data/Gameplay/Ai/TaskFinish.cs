namespace Meitou.Data.Gameplay.Ai;

/// <summary>
/// The finished test (vtable slot 4, <c>+0x20</c>) of the task class the Tasker factory (<c>FUN_14032ebd0</c>) builds for a row
/// (docs/game/ai-tasks.md "Finished tests"): the base <c>Tasker</c>'s, or one of the overrides. Slot 4 true lets a row's list A end the task, skip
/// the goal, job or order as done, and keeps the scorer's second x1.25 off.
/// </summary>
public enum TaskFinish : byte
{
    /// <summary>
    /// The base <c>Tasker</c>'s (<c>FUN_14032dee0</c>): true unless the row is <see cref="TaskDataFlags.NeverDone"/> (+0x15).
    /// </summary>
    Base,
    /// <summary>
    /// <c>Task_Move</c> and the <c>Task_MoveToDoor</c> classes (<c>FUN_140332130</c>): the base test and the task's own byte +0x94, which its
    /// first update sets and nothing clears; false on an instance that never ran.
    /// </summary>
    Move,
    /// <summary>
    /// <c>Task_MakeAnnouncement</c> (<c>FUN_140333f60</c>): its announcement started (+0x78) and the conversation then ended (+0x79); false on an
    /// instance that never ran.
    /// </summary>
    Announcement,
    /// <summary>
    /// <c>Task_MeleeAttack</c>, <c>Task_FocusedMeleeAttack</c> (<c>FUN_140334230</c>, the character's fight state): no target, and no technique
    /// playing short of 0.99 of its clip.
    /// </summary>
    Melee,
    /// <summary>
    /// The classes whose byte goes up only in the update that also asks the task's own end or a replan (<c>Task_Loot_AI</c>,
    /// <c>Task_Loot_Order</c>, <c>Task_Loot_ResourcesWeHaveStorageFor</c>, <c>Task_StripWeapons</c>, <c>Task_ProcessNewSlave</c>,
    /// <c>Task_GetUp</c>, <c>Task_LiftSomething</c>, <c>Task_Kidnapping</c>): false on an instance that never ran and while one runs.
    /// </summary>
    OwnEnd,
    /// <summary>
    /// <c>Task_Stumble</c> (<c>FUN_140334f80</c>): the character's animation has no stumble clip.
    /// </summary>
    Stumble,
    /// <summary>
    /// <c>Task_TownStalker</c>, <c>Task_FollowAndTalk</c> (<c>FUN_14033bc90</c>): the character's conversation and its target's have ended (or it
    /// has no target), or a melee threat is within 40 of it.
    /// </summary>
    Talk,
    /// <summary>
    /// <c>Task_TravelToMissionTarget_ArmyFormation</c> (<c>FUN_140349de0</c>): the character leads its platoon's campaign (or, as leader, arrived:
    /// byte +0x78).
    /// </summary>
    Formation,
}

/// <summary>
/// The <see cref="TaskFinish"/> of each row (docs/game/ai-tasks.md "Finished tests", <b>Verified</b>: the factory's cases and each class's vtable
/// slot 4 in the exe).
/// </summary>
public static class TaskFinishes
{
    /// <summary>
    /// The finished test of the class the factory builds for <paramref name="row"/>; row 3 is <c>Task_Loot_AI</c> only for a character target
    /// (<paramref name="characterTarget"/>: the hand's type 1), else <c>Task_Pickup</c>, which keeps the base test.
    /// </summary>
    public static TaskFinish Of(TaskType row, bool characterTarget = false) => (int)row switch
    {
        1 or 29 or 40 or 67 or 82 or 83 or 121 or 163 or 199 => TaskFinish.Move,
        103 or 129 => TaskFinish.Announcement,
        4 or 5 or 61 or 227 => TaskFinish.Melee,
        3 => characterTarget ? TaskFinish.OwnEnd : TaskFinish.Base,
        26 or 52 or 65 or 68 or 170 or 181 or 182 or 213 or 225 or 240 or 241 or 246 => TaskFinish.OwnEnd,
        147 => TaskFinish.Stumble,
        164 or 165 => TaskFinish.Talk,
        215 => TaskFinish.Formation,
        _ => TaskFinish.Base,
    };
}
