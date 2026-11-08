using System.Numerics;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// A player character's walk up to an NPC to talk (<see cref="DialogueSystem"/>) or trade (<see cref="Trading.TradeSystem"/>). The walk follows the NPC: its goal is renewed when the NPC
/// has moved on or the walk has ended short of it (Task_Move toward a character re-aims, <b>Observed</b>). It is given up when the walker does anything else (another order, a fight), or
/// once it has gone the give-up time without moving on the walk (engine choice: a long walk, a slow walker's or down stairs, is not cut short, one stuck in place is).
/// </summary>
sealed class WalkUp(float giveUpSeconds)
{
    /// <summary>How far the NPC may move from the walk's goal, and how often that is looked at (the pursuit's constants).</summary>
    const float RepathDistance = 12, RepathSeconds = 0.5f;
    /// <summary>How far the walker must have moved since the last look for the walk to count as going on (engine choice).</summary>
    const float Moved = 1;

    long giveUp, nextAim;
    // Where the walk last sent the walker (the NPC's place then), and where the walker stood at the last look.
    Vector2 aimedAt, lastAt;

    /// <summary>Starts the walk of <paramref name="walker"/> (slot <paramref name="w"/>) to the NPC in slot <paramref name="n"/>; none when it is already in talk range.</summary>
    public void Begin(World world, CharacterId walker, int w, int n)
    {
        var table = world.Characters;
        var at = table.Next[n].Position;
        giveUp = world.Tick + Ticks(world, giveUpSeconds);
        nextAim = world.Tick + Ticks(world, RepathSeconds);
        aimedAt = XZ(at);
        lastAt = XZ(table.Next[w].Position);
        if (!DialogueSystem.InTalkRange(table, w, n)) world.Defer(new MoveOrder([walker], at) { Tick = world.Tick + 1 });
    }

    /// <summary>Keeps the walk on the NPC (it is not yet in talk range); false when it is given up.</summary>
    public bool Follow(World world, CharacterId walker, int w, int n)
    {
        if (world.Tick < nextAim) return world.Tick < giveUp;
        nextAim = world.Tick + Ticks(world, RepathSeconds);
        var table = world.Characters;
        ref readonly var tp = ref table.Next[w];
        var at = table.Next[n].Position;
        bool walking = tp.Task == (byte)CharacterTask.GoTo && Vector2.Distance(tp.Goal, aimedAt) <= RepathDistance, stopped = tp.Task == (byte)CharacterTask.Idle;
        if (!walking && !stopped) return false;
        var here = XZ(tp.Position);
        if (walking && Vector2.Distance(here, lastAt) >= Moved) giveUp = world.Tick + Ticks(world, giveUpSeconds);
        else if (world.Tick >= giveUp) return false;
        lastAt = here;
        if (stopped || Vector2.Distance(aimedAt, XZ(at)) > RepathDistance)
        {
            aimedAt = XZ(at);
            world.Defer(new MoveOrder([walker], at) { Tick = world.Tick + 1 });
        }
        return true;
    }

    static Vector2 XZ(Vector3 p) => new(p.X, p.Z);

    static long Ticks(World world, float seconds) => (long)MathF.Ceiling(seconds / world.TickSeconds);
}
