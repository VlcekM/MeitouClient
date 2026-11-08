using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Kidnap;

// The player slaves' Obedient Slave job (187): its decision source and the act-out of the worker rows (ObedientSlave.cs).
public sealed partial class CaptiveSystem : ITaskOwner
{
    readonly ObedientJobs _obedient = new();

    /// <summary>The worker rows 189 and 190 a slave's job acts out (a walk to the workplace, standing at it at its clip).</summary>
    void ITaskOwner.RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(TaskType.AutoLabouringMines, _obedient);
        tasks.Register(TaskType.AutoLabouringMinesPretend, _obedient);
    }
}