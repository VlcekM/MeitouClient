using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's captive goals: the captive jobs a think asks for, started by the <see cref="CaptiveSystem"/> in the serial step.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// Resolved in the serial step; starts the captive jobs the think asked for.
    /// </summary>
    CaptiveSystem? _captives;

    /// <summary>
    /// Set by a think that chose a captive goal (<see cref="AiMind.CaptiveAsk"/>).
    /// </summary>
    int _capturing;

    /// <summary>
    /// The act-out of a captive goal (<see cref="AiBehaviour.Captive"/>): it stands while the serial step starts the goal's job
    /// (<see cref="CaptiveSystem.SendGoalJobs"/>), as the Medic goal's treatment.
    /// </summary>
    void AskCaptiveJob(World world, int slot, AiMind mind)
    {
        Become(world, slot, CharacterTask.Idle);
        mind.CaptiveAsk = true;
        Interlocked.Exchange(ref _capturing, 1);
    }

    /// <summary>
    /// The serial step's start of the captive jobs the thinks asked for this tick (<see cref="CaptiveSystem.SendGoalJobs"/>), only when one asked.
    /// </summary>
    void SendCaptiveJobs(World world)
    {
        if (Interlocked.Exchange(ref _capturing, 0) != 0)
            (_captives ??= world.SystemOf<CaptiveSystem>())?.SendGoalJobs(world);
    }

    /// <summary>
    /// A captive job started (<see cref="CaptiveSystem"/>; serial): its goal becomes the mind's current goal, with a one-step plan and no
    /// commitment, as the original's chooser holds the goal it runs. The goal it ran before ends, with its commitment (43 Staying home's), so the
    /// think after the job chooses anew.
    /// </summary>
    internal static void TakeCaptiveGoal(AiMind mind, AiGoal goal)
    {
        mind.Goal = goal;
        mind.Behaviour = AiBehaviour.Captive;
        mind.Plan.Clear();
        mind.Plan.Add(new PlanStep(goal.Task.Type, default));
        mind.CommitUntil = 0;
        mind.CaptiveOffers.Clear();
    }
}