using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The player slaves' Obedient Slave job (187): its decision source and the act-out of its goals (ObedientSlave.cs).
/// </summary>
public sealed partial class CaptiveSystem : ITaskOwner
{
    /// <summary>
    /// The decision source and job contributor of the Obedient Slave job (made on first use: it asks this system for its cages).
    /// </summary>
    ObedientJobs? _obedient;

    /// <summary>
    /// The Obedient Slave job (<see cref="_obedient"/>).
    /// </summary>
    ObedientJobs Obedient => _obedient ??= new ObedientJobs(this);

    /// <summary>
    /// The worker rows 189 and 190 a slave's job acts out (a walk to the workplace, standing at it at its clip).
    /// </summary>
    void ITaskOwner.RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(TaskType.AutoLabouringMines, Obedient);
        tasks.Register(TaskType.AutoLabouringMinesPretend, Obedient);
    }
}