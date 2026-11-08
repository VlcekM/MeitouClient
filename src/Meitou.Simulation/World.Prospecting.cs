using Meitou.Simulation.Production;

namespace Meitou.Simulation;

public sealed partial class World
{
    volatile ProspectReport? _prospected;

    /// <summary>The last finished prospecting (null: none yet). Written by the production system's serial step, read by the host's window; never part of the state hash.</summary>
    public ProspectReport? Prospected { get => _prospected; internal set => _prospected = value; }
}