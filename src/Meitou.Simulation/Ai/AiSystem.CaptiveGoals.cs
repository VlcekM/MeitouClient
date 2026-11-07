using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Kidnap;

namespace Meitou.Simulation.Ai;

public sealed partial class AiSystem
{
    // Resolved in the serial step; starts the captive jobs the think asked for.
    CaptiveSystem? captives;

    // Set by a think that chose a captive goal (<see cref="AiMind.CaptiveAsk"/>).
    int capturing;

    /// <summary>
    /// The act-out of a captive goal (<see cref="AiBehaviour.Captive"/>): it stands while the serial step starts the goal's job
    /// (<see cref="CaptiveSystem.SendGoalJobs"/>), as the Medic goal's treatment.
    /// </summary>
    void AskCaptiveJob(World world, int slot, AiMind mind)
    {
        Become(world, slot, CharacterTask.Idle);
        mind.CaptiveAsk = true;
        Interlocked.Exchange(ref capturing, 1);
    }

    void SendCaptiveJobs(World world)
    {
        if (Interlocked.Exchange(ref capturing, 0) != 0) (captives ??= world.SystemOf<CaptiveSystem>())?.SendGoalJobs(world);
    }
}
