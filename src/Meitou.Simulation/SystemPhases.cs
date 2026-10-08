using System.Reflection;

namespace Meitou.Simulation;

/// <summary>
/// Which of the three parallel phases a system implements, and the delegate that runs each one over the world's partitions, built once
/// when the system is added. A phase is implemented when the system's type supplies the method itself instead of the empty default of
/// <see cref="ITickSystem"/>; a phase it does not implement is <c>null</c> here, so the tick skips the barrier. The delegates read
/// the partitions of the tick that is running, so one instance serves every tick.
/// </summary>
sealed class SystemPhases
{
    public SystemPhases(World world, ITickSystem system, int index)
    {
        Index = index;
        var map = system.GetType().GetInterfaceMap(typeof(ITickSystem));
        if (Implements(map, nameof(ITickSystem.Think))) Think = i => system.Think(world, world.PartitionAt(i));
        if (Implements(map, nameof(ITickSystem.Move))) Move = i => system.Move(world, world.PartitionAt(i));
        if (Implements(map, nameof(ITickSystem.Act))) Act = i => system.Act(world, world.PartitionAt(i), world.BufferAt(i));
    }

    /// <summary>The system's position in the world's list: the <see cref="Effect.System"/> of what it emits.</summary>
    public int Index { get; }
    public Action<int>? Think { get; }
    public Action<int>? Move { get; }
    public Action<int>? Act { get; }

    /// <summary>True when the interface method is not the default one (a method that cannot be told is taken as implemented, which is always safe).</summary>
    static bool Implements(InterfaceMapping map, string name)
    {
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
            if (map.InterfaceMethods[i].Name == name)
                return map.TargetMethods[i] is not { } target || target.DeclaringType != typeof(ITickSystem);
        return true;
    }
}
