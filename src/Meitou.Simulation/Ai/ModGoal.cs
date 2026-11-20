using Meitou.Content;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The faults of one world's mod goals, shared by their <see cref="ModGoal"/>s: how many have faulted (the hash adds their ticks while any has).
/// </summary>
internal sealed class ModGoalFaults
{
    /// <summary>
    /// How many goals have faulted; set from the think's threads.
    /// </summary>
    public int Count;
}

/// <summary>
/// A code mod's goal (<see cref="IModGoal"/>) as an engine-acted goal of <see cref="DutyGoals"/>, with its calls caught (docs/modding.md "Chooser goals
/// and jobs"). A <see cref="IModGoal.Desire"/> that throws or answers a number that is not finite faults at that tick: the goal still answers the
/// rest of that tick and is not asked from the next tick on, so the cut falls on a tick boundary whatever the threads did, and no start of it is
/// asked from then (that tick's included). A start or end that throws faults at once. The first fault is logged with the mod's id and the player
/// gets a notice; the faults are hashed (<see cref="DutyGoals.Hash"/>). A faulted goal, and one whose registering system the world stopped
/// (<see cref="ModSystemFaults"/>), is dropped: the mod is called no more and the AI lets its characters go (<see cref="DutyGoals"/>).
/// </summary>
internal sealed class ModGoal(ModGoalClaim claim, PopulationData data, ModGoalFaults faults) : IDutyGoal
{
    /// <summary>
    /// The tick of the goal's first fault (<see cref="long.MaxValue"/>: none); set from the think's threads.
    /// </summary>
    long _fault = long.MaxValue;

    /// <summary>
    /// The goal type.
    /// </summary>
    public TaskType Type => claim.Type;

    /// <summary>
    /// The tick of the goal's first fault (<see cref="long.MaxValue"/>: none).
    /// </summary>
    public long FaultTick => Volatile.Read(ref _fault);

    /// <summary>
    /// Whether the goal is dropped for the world: it faulted, or its registering system was stopped. A serial read.
    /// </summary>
    public bool Dropped(World world) => FaultTick != long.MaxValue || world.ModFaults.IsStopped(claim.System);

    /// <summary>
    /// The mod's desire for the asker (parallel, the asker's partition); 0 for a goal faulted before this tick or whose system the world stopped, and
    /// for a fault now.
    /// </summary>
    public float Desire(World world, in DutyAsk ask)
    {
        if (FaultTick < world.Tick) return 0;

        // A stopped system changes only at a serial step (ModSystemFaults.Settle), so every thread of a tick reads the same answer.
        if (world.ModFaults.IsStopped(claim.System) || world.Characters.Cold(ask.Slot) is not { } cold) return 0;

        float desire;
        try
        {
            desire = claim.Goal.Desire(new ModGoalAsk(world, data, cold, ask.Squad, ask.Slot, ask.Id, ask.Type, ask.Targeting));
        }
        catch (Exception e)
        {
            Fault(world, "desire", e);
            return 0;
        }

        if (float.IsFinite(desire)) return desire;

        Fault(world, "desire", new InvalidOperationException($"the desire {desire} is not a finite number"));
        return 0;
    }

    /// <summary>
    /// The mod's start (serial); false once dropped, and for a throw.
    /// </summary>
    public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        if (Dropped(world)) return false;

        try
        {
            return claim.Goal.Start(world, new ModGoalStart(ask.Slot, ask.Id, ask.Squad, ask.Type, ask.Targeting));
        }
        catch (Exception e)
        {
            Fault(world, "start", e);
            return false;
        }
    }

    /// <summary>
    /// The mod's end (serial); not called once dropped.
    /// </summary>
    public void End(World world, int slot, CharacterId id, DutyEnd why)
    {
        if (Dropped(world)) return;

        try
        {
            claim.Goal.End(world, slot, id, claim.Type, EndOf(why));
        }
        catch (Exception e)
        {
            Fault(world, "end", e);
        }
    }

    /// <summary>
    /// The mod's reason for an engine goal's end.
    /// </summary>
    static ModGoalEnd EndOf(DutyEnd why) => why switch
    {
        DutyEnd.Displaced => ModGoalEnd.Displaced,
        DutyEnd.Order => ModGoalEnd.Order,
        DutyEnd.Replanned => ModGoalEnd.Replanned,
        DutyEnd.Taken => ModGoalEnd.Taken,
        _ => ModGoalEnd.Gone,
    };

    /// <summary>
    /// Records a fault at the world's tick (the earliest wins; the threads of one tick all write the same tick), and logs the first with the
    /// player's notice.
    /// </summary>
    void Fault(World world, string call, Exception e)
    {
        long tick = world.Tick;
        long seen = Volatile.Read(ref _fault);
        while (tick < seen)
        {
            long was = Interlocked.CompareExchange(ref _fault, tick, seen);
            if (was == seen)
            {
                if (seen != long.MaxValue) return;

                Interlocked.Increment(ref faults.Count);
                Report(world, call, e);
                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// The first fault's log line and notice.
    /// </summary>
    void Report(World world, string call, Exception e)
    {
        var (owner, name) = (claim.Claim.Owner, claim.Claim.Name);
        string what = $"goal {(int)claim.Type} '{name}'";
        Console.Error.WriteLine($"mod       {owner}: {what} dropped for this world, its {call} failed at tick {world.Tick}: {e}");

        if (!CodeModFaults.Tell(owner, e))
            world.ModFaults.Notify($"Mod {owner}: its goal {name} stopped after an error ({e.GetType().Name}); the rest of the game goes on.");
    }
}
