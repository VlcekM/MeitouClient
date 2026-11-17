using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The original's "is this task finished?" (each task class's vtable slot 4, <see cref="TaskFinish"/>; docs/game/ai-tasks.md "Finished tests") for
/// Meitou's tasks: asked of a goal never started (the scorer's done test), and of the task a character runs now (the scorer's second x1.25). Any
/// thread (reads the last tick's tables).
/// </summary>
public static class TaskFinished
{
    #region Never started

    /// <summary>
    /// A clip played past this share counts as over (<c>DAT_141696c78</c>, <c>FUN_14060cd80</c>).
    /// </summary>
    const float ClipOver = 0.99f;

    /// <summary>
    /// Slot 4 of a fresh instance of <paramref name="row"/>'s class for the character at <paramref name="slot"/>: a goal, job or order not started
    /// (their own bytes still hold the constructor's 0). <paramref name="target"/> is the task's character target (<see cref="CharacterId.None"/>:
    /// none).
    /// </summary>
    public static bool Fresh(World world, int slot, TaskType row, CharacterId target)
    {
        bool onCharacter = !target.IsNone;
        return TaskFinishes.Of(row, onCharacter) switch
        {
            TaskFinish.Base => TaskDataTable.Of(row)?.Is(TaskDataFlags.NeverDone) != true,

            // Their bytes start at 0: a move, an announcement, a loot, a lift or a get-up that never ran is not finished.
            TaskFinish.Move or TaskFinish.Announcement or TaskFinish.OwnEnd => false,
            TaskFinish.Melee => FightOver(world, slot),
            TaskFinish.Stumble => !Stumbling(world, slot),
            TaskFinish.Talk => TalkOver(world, slot, target),
            TaskFinish.Formation => LeadsCampaign(world, slot),
            _ => true,
        };
    }

    /// <summary>
    /// <c>Task_MeleeAttack</c>'s test (<c>FUN_140334230</c> → <c>FUN_14060cd80</c> on the character's fight state): no target, and no technique
    /// (attack or reaction) still short of 0.99 of its clip.
    /// </summary>
    static bool FightOver(World world, int slot)
    {
        if (world.SystemOf<CombatSystem>() is not { } combat) return true;

        var id = world.Characters.IdOf(slot);
        if (combat.StateOf(id).TargetSlot >= 0) return false;

        return combat.Playing(id, world.Tick) is not { Progress: <= ClipOver };
    }

    /// <summary>
    /// The character's hit reaction is playing (<c>Task_Stumble</c>'s test, <c>FUN_140334f80</c>: AnimationClass <c>+0x228</c> set).
    /// </summary>
    static bool Stumbling(World world, int slot)
    {
        if (world.SystemOf<CombatSystem>() is not { } combat) return false;

        var s = combat.StateOf(world.Characters.IdOf(slot));
        return s.StumbleClip >= 0 && world.Tick < s.StunUntil;
    }

    /// <summary>
    /// <c>Task_TownStalker</c>'s test (<c>FUN_14033bc90</c>): its conversation and its target's are over (or it has no target), or a melee threat is
    /// within 40 of it (<c>+0x2b0</c>).
    /// </summary>
    static bool TalkOver(World world, int slot, CharacterId target)
    {
        var talk = world.SystemOf<Dialogue.DialogueSystem>();
        bool over = talk?.Talking(world.Characters.IdOf(slot)) != true && (target.IsNone || talk?.Talking(target) != true);
        if (over) return true;

        return world.SystemOf<CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, slot);
    }

    /// <summary>
    /// <c>Task_TravelToMissionTarget_ArmyFormation</c>'s test (<c>FUN_140349de0</c>): the leader of its platoon's campaign (the war manager's
    /// record of the platoon, slot 5 <c>FUN_140285820</c>: its lead platoon's leader) is the character.
    /// </summary>
    static bool LeadsCampaign(World world, int slot)
    {
        if (world.Characters.Cold(slot)?.Ai?.Squad is not { PlatoonId: >= 0 } squad) return false;
        if (world.Platoons.Find(squad.PlatoonId) is not { Campaign: { } campaign } platoon) return false;

        return campaign.Leader == platoon.Id && squad.Leader == world.Characters.IdOf(slot);
    }

    #endregion

    #region Running

    /// <summary>
    /// Whether the character runs a task now whose slot 4 says it is not finished (the scorer's second x1.25, <c>FUN_14050df10</c>):
    /// <paramref name="planned"/> is the row Meitou's plan or start names (<see cref="TaskType.None"/>: nothing runs); the act-out tells the
    /// original's task where Meitou's plan names its goal instead (<see cref="RunningRow"/>).
    /// </summary>
    public static bool Unfinished(World world, int slot, TaskType planned)
    {
        if (planned == TaskType.None) return false;

        var (row, target) = RunningRow(world, slot, planned);
        return TaskFinishes.Of(row, !target.IsNone) switch
        {
            // Its first update sets the byte: a move started this tick has not had one.
            TaskFinish.Move => !Updated(world, slot),

            // Set only by the update that also ends the task (or asks a replan), which the same character update carries out.
            TaskFinish.OwnEnd => true,
            _ => !Fresh(world, slot, row, target),
        };
    }

    /// <summary>
    /// The original's running task for Meitou's act-out, with its character target: a fight is <c>Task_MeleeAttack</c> (4, 227 for an animal) or a
    /// ranged row; a treatment a first aid row (25, 60, 249, 269 all have +0x15); a lift a <c>Task_LiftSomething</c> row; a loot its start's row;
    /// else <paramref name="planned"/>.
    /// </summary>
    static (TaskType Row, CharacterId Target) RunningRow(World world, int slot, TaskType planned)
    {
        var cold = world.Characters.Cold(slot);
        var task = (CharacterTask)world.Characters.Next[slot].Task;
        var run = cold?.Running is { } r && r.Runs(task) ? r : null;
        return task switch
        {
            CharacterTask.Attack => (FightRow(world, slot, cold, planned), CharacterId.None),
            CharacterTask.Treat => (TaskType.FirstAidOrder, CharacterId.None),
            CharacterTask.PickUp => (TaskType.LiftPerson, CharacterId.None),
            CharacterTask.Loot when run is not null => (run.Row, run.Target),
            _ => (planned, CharacterId.None),
        };
    }

    /// <summary>
    /// The fight row running: <paramref name="planned"/> when it is one, else the ranged row for a crossbow fight, the animal's or the human's melee
    /// row.
    /// </summary>
    static TaskType FightRow(World world, int slot, CharacterCold? cold, TaskType planned)
    {
        if (TaskFinishes.Of(planned) == TaskFinish.Melee) return planned;
        if (world.SystemOf<CombatSystem>()?.StateOf(world.Characters.IdOf(slot)).Ranged == true) return TaskType.RangedAttack;

        return cold is not null && SquadRegistry.IsAnimal(cold) ? TaskType.MeleeAttackAnimal : TaskType.MeleeAttack;
    }

    /// <summary>
    /// Whether the act-out has had an update since its start (one started this tick has not).
    /// </summary>
    static bool Updated(World world, int slot) => world.Characters.Cold(slot)?.Running is not { } run || run.Tick < world.Tick;

    #endregion
}
