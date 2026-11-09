using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Player;

namespace Meitou.Simulation;

/// <summary>
/// Something the player (or a test) tells the world, applied at the start of simulation tick <see cref="Tick"/>
/// (docs/simulation.md, "Threading": inputs are step 1 of a tick). Commands never touch the world directly, so the same commands
/// at the same ticks give the same world.
/// </summary>
public abstract record SimCommand
{
    /// <summary>
    /// The simulation tick the command applies at; a command for a tick already run applies at the next one.
    /// </summary>
    public required long Tick { get; init; }
}

/// <summary>
/// Move order: the characters walk to <see cref="Target"/> (world units; the height is taken from the ground).
/// </summary>
public sealed record MoveOrder(IReadOnlyList<CharacterId> Characters, Vector3 Target) : OrderCommand;

/// <summary>
/// The context menu's Follow (order 31, <c>Task_Follow</c>; docs/game/ui-input.md "Context menu verbs"): the characters (the selection when empty)
/// keep a formation place behind <see cref="Target"/> until another order replaces it or the target is gone
/// (<see cref="CharacterCold.FollowOrder"/>). <b>Engine</b>: the squad follower's slot rule, not saved.
/// </summary>
public sealed record FollowOrder(IReadOnlyList<CharacterId> Characters, CharacterId Target) : OrderCommand
{
    /// <summary>
    /// Bodyguard (order 45) rather than Follow (31): both fight an attacker of the target within 40 of it (state 16), a bodyguard also its own
    /// attacker within 40 (state 39; <see cref="CharacterCold.Guarding"/>, <c>RetaliationSystem.Guards</c>, docs/game/ai-tasks.md "Precedence"); the
    /// Bodyguard job's follow is the same row.
    /// </summary>
    public bool Guard { get; init; }
}

/// <summary>
/// Sandbox only: fixes a character's speed mode (null: leave) and/or its walking speed in units per second (null: leave; 0: the speed chain's again).
/// The animation sandbox sends it, the game never does.
/// </summary>
public sealed record SandboxMovement(CharacterId Character, byte? Mode, float? Speed) : SimCommand;

/// <summary>
/// Commands waiting for their tick. Any thread may <see cref="Enqueue"/> (the UI runs on the host's thread); the simulation takes
/// the due ones with <see cref="TakeDue"/> in a fixed order: by tick, then in the order they were queued.
/// </summary>
public sealed class CommandQueue
{
    readonly Lock _gate = new();
    readonly List<(SimCommand Command, long Sequence)> _pending = [];
    long _sequence;

    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }

    public void Enqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate) _pending.Add((command, _sequence++));
    }

    /// <summary>
    /// Removes and returns the commands whose tick is at or before <paramref name="tick"/>, oldest tick first, then queue order.
    /// </summary>
    public IReadOnlyList<SimCommand> TakeDue(long tick, Func<SimCommand, bool>? include = null)
    {
        List<(SimCommand Command, long Sequence)>? due = null;
        lock (_gate)
        {
            if (_pending.Count == 0) return [];
            int kept = 0;
            for (int k = 0; k < _pending.Count; k++)
            {
                var p = _pending[k];
                if (p.Command.Tick <= tick && (include is null || include(p.Command))) (due ??= []).Add(p);
                else _pending[kept++] = p;
            }

            if (due is null) return [];
            _pending.RemoveRange(kept, _pending.Count - kept);
        }

        due.Sort(static (a, b) => a.Command.Tick != b.Command.Tick ? a.Command.Tick.CompareTo(b.Command.Tick) : a.Sequence.CompareTo(b.Sequence));
        var commands = new SimCommand[due.Count];
        for (int k = 0; k < commands.Length; k++) commands[k] = due[k].Command;
        return commands;
    }
}

/// <summary>
/// Tells the world where the player is looking (the camera focus), which zones to keep active and whose town residents to load
/// (docs/game/game-loop.md "Zones": the game activates zones around the player's characters; until there is a player, the camera).
/// </summary>
public sealed record FocusCommand(Vector3 Position) : SimCommand;

/// <summary>
/// Selects the player's own characters. No valid targets retains the selection; otherwise non-additive replaces it and additive (shift) adds to it.
/// <paramref name="Toggle"/> (shift on a single character's click, portrait or number key, <c>FUN_1407f9000</c> / <c>FUN_1407f8f60</c>): a character
/// already selected leaves the selection unless it is the only one; one not selected joins it.
/// </summary>
public sealed record SelectCommand(IReadOnlyList<CharacterId> Characters, bool Additive = false, bool Toggle = false) : SimCommand
{
    /// <summary>
    /// With no characters and not additive: empty the selection (agent-playtest control channel, <c>select --none</c>); the game's own empty selects
    /// change nothing.
    /// </summary>
    public bool Clear { get; init; }
}

/// <summary>
/// Clears order selection and shows another faction's character in the HUD panels. <see cref="CharacterId.None"/> clears inspection only.
/// </summary>
public sealed record InspectCommand(CharacterId Target) : SimCommand;

/// <summary>
/// The player's characters the pinned inventory windows show: the snapshot carries their item grids while they are not selected. Display only.
/// </summary>
public sealed record ShowInventoryCommand(IReadOnlyList<CharacterId> Targets) : SimCommand;

/// <summary>
/// The building the HUD panels show (docs/game/ui-screens.md "Selected building"): the snapshot then carries its doors' state. A null instance clears
/// it. Display only.
/// </summary>
public sealed record InspectBuildingCommand(string? Instance) : SimCommand;

/// <summary>
/// The map was closed: towns lose their recently-discovered mark (<see cref="TownKnowledge.Recent"/>).
/// </summary>
public sealed record SeenDiscoveriesCommand : SimCommand;

/// <summary>
/// Stop: the characters (the selection when the list is empty) drop their orders and stand.
/// </summary>
public sealed record StopCommand(IReadOnlyList<CharacterId> Characters) : SimCommand;

/// <summary>
/// The navmesh changed (a zone became ready, a door moved): characters on the way ask for their path again (those with a go-to in hand).
/// Paths made while a zone was still building came from the open-ground stand-in and may cross buildings.
/// With <see cref="Zones"/>, only those whose path still to walk crosses one of them, and those whose answer is on its way; null: all.
/// </summary>
public sealed record RepathCommand : SimCommand
{
    public IReadOnlySet<ZoneCoordinate>? Zones { get; init; }
}