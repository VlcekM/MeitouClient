using System.Numerics;

namespace Meitou.Simulation.Player;

/// <summary>
/// The parts and options of an order given by <see cref="World.GiveOrder(CharacterId, Data.Gameplay.Ai.TaskType, OrderOptions)"/>
/// (docs/modding.md "Ordering characters"); room for more options. Engine API, not in the original.
/// </summary>
public sealed record OrderOptions
{
    /// <summary>
    /// The character or body the order targets; null: none.
    /// </summary>
    public CharacterId? Target { get; init; }

    /// <summary>
    /// The site the order names (a building or fixture id); null: none.
    /// </summary>
    public string? Site { get; init; }

    /// <summary>
    /// The point the order names (a move's goal).
    /// </summary>
    public Vector3 Point { get; init; }

    /// <summary>
    /// Queue the order after the others instead of replacing the queue.
    /// </summary>
    public bool Queued { get; init; }

    /// <summary>
    /// The distance from its target within which the order's job starts its work, in place of the row's own (null); a finite number, 0 or more.
    /// Read by the rows that walk to a target and then work there (<see cref="OrderEntry.Reach"/>); the others keep their own distance.
    /// </summary>
    public float? Reach { get; init; }
}
