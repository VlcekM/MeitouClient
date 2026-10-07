using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// Something the player (or a test) tells the world, applied at the start of simulation tick <see cref="Tick"/>
/// (docs/simulation.md, "Threading": inputs are step 1 of a tick). Commands never touch the world directly, so the same commands
/// at the same ticks give the same world.
/// </summary>
public abstract record SimCommand
{
    /// <summary>The simulation tick the command applies at; a command for a tick already run applies at the next one.</summary>
    public required long Tick { get; init; }
}

/// <summary>Move order: the characters walk to <see cref="Target"/> (world units; the height is taken from the ground).</summary>
public sealed record MoveOrder(IReadOnlyList<CharacterId> Characters, Vector3 Target) : SimCommand
{
    /// <summary>Add to the characters' order lists (shift in the game) instead of replacing them.</summary>
    public bool Queued { get; init; }
}

/// <summary>
/// Commands waiting for their tick. Any thread may <see cref="Enqueue"/> (the UI runs on the host's thread); the simulation takes
/// the due ones with <see cref="TakeDue"/> in a fixed order: by tick, then in the order they were queued.
/// </summary>
public sealed class CommandQueue
{
    readonly Lock gate = new();
    readonly List<(SimCommand Command, long Sequence)> pending = [];
    long sequence;

    public int Count
    {
        get { lock (gate) return pending.Count; }
    }

    public void Enqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (gate) pending.Add((command, sequence++));
    }

    /// <summary>Removes and returns the commands whose tick is at or before <paramref name="tick"/>, oldest tick first, then queue order.</summary>
    public List<SimCommand> TakeDue(long tick)
    {
        List<(SimCommand Command, long Sequence)> due;
        lock (gate)
        {
            due = pending.FindAll(p => p.Command.Tick <= tick);
            if (due.Count == 0) return [];
            pending.RemoveAll(p => p.Command.Tick <= tick);
        }
        due.Sort((a, b) => a.Command.Tick != b.Command.Tick ? a.Command.Tick.CompareTo(b.Command.Tick) : a.Sequence.CompareTo(b.Sequence));
        return due.ConvertAll(p => p.Command);
    }
}

/// <summary>
/// Tells the world where the player is looking (the camera focus), which zones to keep active and whose town residents to load
/// (docs/game/game-loop.md "Zones": the game activates zones around the player's characters; until there is a player, the camera).
/// </summary>
public sealed record FocusCommand(Vector3 Position) : SimCommand;
