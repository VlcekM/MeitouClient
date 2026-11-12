using Meitou.Simulation.Production;

namespace Meitou.Simulation;

/// <summary>
/// The world's last prospecting, for the host.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// The last prospecting (see <see cref="Prospected"/>).
    /// </summary>
    volatile ProspectReport? _prospected;

    /// <summary>
    /// The last finished prospecting (null: none yet). Written by the production system's serial step, read by the host's window;
    /// never part of the state hash.
    /// </summary>
    public ProspectReport? Prospected { get => _prospected; internal set => _prospected = value; }
}