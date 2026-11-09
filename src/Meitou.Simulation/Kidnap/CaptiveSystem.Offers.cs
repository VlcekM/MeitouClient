using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The captive goals the AI weighs (<see cref="AiBehaviour.Captive"/>, docs/simulation.md "Captives"). The sweep starts the job of a member that
/// stands free, unscored. Any other member awake (resting on a seat the AI gave it, in a job, on another system's duty) is not taken: its sweep turn,
/// and the end of its job, keep the captive goals whose job would start, each with the original's desire (<see cref="AiMind.CaptiveOffers"/>). Its
/// think weighs them against its other goals, as the original's chooser does (docs/game/ai-tasks.md "Precedence"); one that wins gets the character
/// up, and the serial step after the think starts its job (<see cref="SendGoalJobs"/>).
/// </summary>
public sealed partial class CaptiveSystem
{
    /// <summary>
    /// The goal types <see cref="TryStart"/> starts a job for: the AI's captive goals.
    /// </summary>
    internal static readonly TaskType[] GoalTypes =
    [
        // The prisoner's fate, the eaters and the worshippers.
        TaskType.KillARandomCageOccupant, TaskType.EatARandomCageOccupant, TaskType.EatARandomCageOccupantMeasuredRate,
        TaskType.EatARandomDeadBody, TaskType.EatARandomKoBody, TaskType.FindCropsToEat, TaskType.FogmanWorshipVictim,
        // The dogs' limb play.
        TaskType.AnimalFetchALimb, TaskType.PlayBecauseIHaveALimbInMouth, TaskType.ChaseAllyDogsWithMouthLimbs,
        // The slavers and their slaves.
        TaskType.NewSlaveProcessing, TaskType.SlaveWatching, TaskType.LiberateAllThePrisoners, TaskType.CaptureNewSlaves,
        TaskType.CarryWoundedSlaves, TaskType.WorkTheSlaves, TaskType.GatherSlavesFromCages, TaskType.GatherSlavesFromCagesIfFemaleOrBeast,
        TaskType.GatherSlavesFromCagesIfItsAnExportTown, TaskType.GoToSomewhereForDeliveringSlaves, TaskType.SlaveGetInMyMastersCage,
    ];

    /// <summary>
    /// A member whose think may weigh its captive goals, now or once it is the AI's again: alive and up, not a player character, a prisoner or
    /// carried. Not one asleep or going to bed: its bed goal keeps it (engine choice).
    /// </summary>
    static bool Weighs(World world, int slot)
    {
        var table = world.Characters;
        ref readonly var n = ref table.Next[slot];
        return n.Alive && n.Task != (byte)CharacterTask.Sleep
            && table.Cold(slot) is { Ai.Behaviour: not AiBehaviour.Sleep, IsPlayer: false, IsImprisoned: false } c
            && c.CarriedBy.IsNone && c.Medical is not { Incapacitated: true };
    }

    /// <summary>
    /// The member's offers (<see cref="AiMind.CaptiveOffers"/>): while it <see cref="Weighs"/>, its captive goals in their hours whose job would
    /// start; none otherwise.
    /// </summary>
    void KeepOffers(World world, Squad squad, Blackboard board, int slot, int hour)
    {
        if (world.Characters.Cold(slot) is not { Ai: { } mind } cold) return;
        mind.CaptiveOffers.Clear();
        if (!Weighs(world, slot)) return;
        var roles = RolesOf(world, squad, board, slot, cold);
        for (int g = 0; g < roles.Count; g++)
            if (roles[g].InWindow(hour)) Offer(world, squad, board, slot, cold, roles[g].Task.Type);
        for (int g = 0; g < mind.RaceGoals.Count; g++)
            Offer(world, squad, board, slot, cold, mind.RaceGoals[g].Task.Type);
    }

    /// <summary>
    /// Offers goal <paramref name="type"/> to a character's next think when it is a captive goal not already wanted that could start now, with
    /// its desire.
    /// </summary>
    void Offer(World world, Squad squad, Blackboard board, int slot, CharacterCold cold, TaskType type)
    {
        var mind = cold.Ai!;
        if (TaskBehaviours.Of(type) != AiBehaviour.Captive || mind.CaptiveDesire(type) > 0) return;
        if (TryStart(world, squad, board, slot, cold, type, probe: true) is null) return;
        if (OfferDesire(world, squad, cold, type) is > 0 and var desire) mind.CaptiveOffers.Add((type, desire));
    }

    /// <summary>
    /// Clears the captive offers of the character at <paramref name="slot"/>.
    /// </summary>
    static void ClearOffers(World world, int slot) => world.Characters.Cold(slot)?.Ai?.CaptiveOffers.Clear();

    /// <summary>
    /// The workers whose job ended in this step's job loop (SlowWorld), offered anew once those jobs are gone.
    /// </summary>
    readonly List<CharacterId> _ended = [];

    /// <summary>
    /// Keeps the offers of each worker whose job just ended, on the state after the job, so its next think weighs its captive goals at once rather
    /// than at its next sweep turn.
    /// </summary>
    void OfferAfterJobs(World world)
    {
        var table = world.Characters;
        foreach (var id in _ended)
        {
            if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { Ai.Squad: { Ai: { } board } squad })
                KeepOffers(world, squad, board, slot, world.Hour);
        }

        _ended.Clear();
    }

    /// <summary>
    /// The original's desire of a captive goal whose job would start (docs/game/ai-tasks.md "Prisoner fate and slave rows"): 188 and 192 0.25 x the
    /// platoon's slave list (AI slot 65), the limb play and chase their own, the rest 1; the target finder's factor is 1 with a target.
    /// </summary>
    float OfferDesire(World world, Squad squad, CharacterCold cold, TaskType type) => type switch
    {
        TaskType.WorkTheSlaves or TaskType.GoToSomewhereForDeliveringSlaves => DeliverDesire(world, squad),
        TaskType.PlayBecauseIHaveALimbInMouth => PlayDesire(world, cold),
        TaskType.ChaseAllyDogsWithMouthLimbs => ChaseDesire(world, cold),
        _ => 1,
    };

    /// <summary>
    /// Starts the job of each NPC whose think chose a captive goal (<see cref="AiMind.CaptiveAsk"/>; serial, after the think): the job start runs
    /// again on the state of now. The offers go either way; with no job, the next think chooses again.
    /// </summary>
    internal void SendGoalJobs(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { CaptiveAsk: true } mind } cold) continue;
            mind.CaptiveAsk = false;
            mind.CaptiveOffers.Clear();
            if (!table.Next[s].Alive || mind.Goal is not { } goal || mind.Squad.Ai is not { } board) continue;
            if (_jobs.Count >= Settings.MaxJobs || Busy(table.IdOf(s))) continue;
            Start(world, mind.Squad, board, s, cold, goal);
        }
    }
}