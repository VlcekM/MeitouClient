using Meitou.Simulation.Combat;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The event log's deaths and knock-outs (<see cref="EventLog"/>): at each tick boundary, a line for every awake character that went down, came round
/// or died since the last one, with the cause of a death and the last character that landed a blow on it (what it is aware of, not a recorded killer:
/// a later bleed-out has none). Serial; it only reads the world.
/// </summary>
sealed class DownLog
{
    /// <summary>
    /// The state each slot had at the last <see cref="Observe"/>: up, out cold or dead.
    /// </summary>
    enum Down : byte
    {
        /// <summary>
        /// Conscious.
        /// </summary>
        Up,
        /// <summary>
        /// Unconscious.
        /// </summary>
        Out,
        /// <summary>
        /// Dead.
        /// </summary>
        Dead,
    }

    /// <summary>
    /// A blow landed this many game seconds ago or less names its striker on a knock-out or death line.
    /// </summary>
    const float HitSeconds = 30;

    /// <summary>
    /// The world the remembered states are of; another world (a load) starts afresh.
    /// </summary>
    World? _world;
    /// <summary>
    /// Per slot, its generation at the last <see cref="Observe"/> (-1: not seen then).
    /// </summary>
    int[] _generation = [];
    /// <summary>
    /// Per slot, its state at the last <see cref="Observe"/>.
    /// </summary>
    Down[] _down = [];

    /// <summary>
    /// Writes the changes since the last call; a character seen for the first time is only remembered.
    /// </summary>
    public void Observe(World world, EventLog log)
    {
        Follow(world);
        var table = world.Characters;
        var state = table.Previous;
        for (int slot = 0; slot < state.Length; slot++)
        {
            if (!state[slot].Alive || state[slot].Dormant || table.Cold(slot) is not { Medical: { } medical })
            {
                _generation[slot] = -1;
                continue;
            }

            var now = StateOf(medical);
            bool known = _generation[slot] == state[slot].Generation;
            var was = _down[slot];
            (_generation[slot], _down[slot]) = (state[slot].Generation, now);
            if (known && was != now)
                log.Line(world, now == Down.Dead ? "death" : "down", Text(world, slot, now, medical));
        }
    }

    /// <summary>
    /// Starts afresh on another world and makes room for every slot of the table.
    /// </summary>
    void Follow(World world)
    {
        if (!ReferenceEquals(_world, world))
        {
            _world = world;
            Array.Fill(_generation, -1);
        }

        int high = world.Characters.HighWater;
        if (_generation.Length >= high) return;
        int old = _generation.Length, size = Math.Max(high, old * 2);
        Array.Resize(ref _generation, size);
        Array.Resize(ref _down, size);
        Array.Fill(_generation, -1, old, size - old);
    }

    /// <summary>
    /// Dead, out cold or up.
    /// </summary>
    static Down StateOf(MedicalState medical)
    {
        if (medical.Dead) return Down.Dead;
        return medical.Unconscious ? Down.Out : Down.Up;
    }

    /// <summary>
    /// "X died: BloodLoss; last hit by Y", "X was knocked out; last hit by Y", "X came round".
    /// </summary>
    static string Text(World world, int slot, Down now, MedicalState medical)
    {
        string who = EventLog.Who(world, slot);
        string hitter = LastHitter(world, slot) is { } by ? "; last hit by " + by : "";
        return now switch
        {
            Down.Dead => $"{who} died: {medical.Cause}{hitter}",
            Down.Out => $"{who} was knocked out{hitter}",
            _ => $"{who} came round",
        };
    }

    /// <summary>
    /// The last character that landed a blow on the slot (<see cref="CombatSlot.HitFrom"/>) within <see cref="HitSeconds"/>, or null: the slot it
    /// names is not tied to a generation, so an older hit could name a stranger in a reused slot.
    /// </summary>
    static string? LastHitter(World world, int slot)
    {
        if (world.SystemOf<CombatSystem>() is not { } combat) return null;
        var state = combat.StateOf(world.Characters.IdOf(slot));
        if (state.HitFrom < 0 || state.HitTick < 0 || world.Tick - state.HitTick > HitSeconds / world.TickSeconds)
            return null;
        bool there = state.HitFrom < world.Characters.HighWater && world.Characters.Cold(state.HitFrom) is not null;
        return there ? EventLog.Who(world, state.HitFrom) : null;
    }
}
