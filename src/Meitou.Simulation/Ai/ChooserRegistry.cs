using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Ai;

/// <summary>
/// A system that adds goals and job contributors to the chooser for the task types its mod claimed (<c>ModContext.AddGoalType</c>,
/// <c>ModContext.AddJobType</c>), once when the world is made (docs/modding.md "Chooser goals and jobs").
/// </summary>
public interface IChooserOwner
{
    /// <summary>
    /// Registers the system's goals and job contributors with <paramref name="chooser"/>.
    /// </summary>
    void Register(ChooserRegistry chooser);
}

/// <summary>
/// Where a system registers its goals and job contributors (only inside <see cref="IChooserOwner.Register"/>). A type its mod did not claim, another
/// mod's, or one registered twice throws, and a throw drops everything that system registered, with a log line, and stops a mod's system.
/// </summary>
public sealed class ChooserRegistry
{
    /// <summary>
    /// The types the mods claimed (null: none).
    /// </summary>
    readonly SimulationExtensions? _extensions;

    /// <summary>
    /// The world's chooser host, which keeps the job contributors.
    /// </summary>
    readonly DecisionSystem _decisions;

    /// <summary>
    /// The goals the world's systems registered so far (all registries of one world share it).
    /// </summary>
    readonly List<ModGoalClaim> _goals;

    /// <summary>
    /// The mod of the registering system (null: a system no mod made, which may register any claimed type).
    /// </summary>
    readonly string? _mod;

    /// <summary>
    /// The registering system's index in the world.
    /// </summary>
    readonly int _system;

    /// <summary>
    /// Whether goals may be registered (only while the world is made; a system added later registers job contributors alone).
    /// </summary>
    readonly bool _goalsOpen;

    /// <summary>
    /// Whether <see cref="IChooserOwner.Register"/> runs now.
    /// </summary>
    bool _open;

    /// <summary>
    /// The job types this registry added, taken back when the system's registration throws.
    /// </summary>
    readonly List<TaskType> _jobsAdded = [];

    /// <summary>
    /// How many goals the world's systems had registered before this one: the goals from there on are taken back when its registration throws.
    /// </summary>
    int _goalsBefore;

    /// <summary>
    /// The registering system (named in a desire filter's fault).
    /// </summary>
    readonly ITickSystem? _owner;

    /// <summary>
    /// How many desire filters the world had before this registry's, taken back from there when its registration throws.
    /// </summary>
    int _filtersBefore;

    /// <summary>
    /// A registry for one system's registration.
    /// </summary>
    internal ChooserRegistry(SimulationExtensions? extensions, DecisionSystem decisions, List<ModGoalClaim> goals, string? mod, int system,
        bool goalsOpen, ITickSystem? owner = null)
    {
        (_extensions, _decisions, _goals, _mod, _system, _goalsOpen, _owner) = (extensions, decisions, goals, mod, system, goalsOpen, owner);
    }

    /// <summary>
    /// Registers <paramref name="filter"/> on the chooser's desires (<see cref="IGoalDesireFilter"/>): every goal, plan step, order and job of any
    /// task type, the engine's and the mods', for every character. Filters are asked in registration order, each seeing the desire the earlier ones
    /// left (docs/modding.md "Chooser goals and jobs").
    /// </summary>
    public void DesireFilter(IGoalDesireFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        CheckOpen();
        if (_owner is null) throw new InvalidOperationException("Desire filters are registered by a world's system.");

        _decisions.DesireFilters.Hooks.Add(filter, _owner, _system);
    }

    /// <summary>
    /// Registers <paramref name="goal"/> for <paramref name="type"/>, a goal type the system's mod claimed (from 1000 up): any character whose goal
    /// lists hold an AI_TASK of that <c>enum</c> weighs it in its chooser (docs/modding.md "Chooser goals and jobs").
    /// </summary>
    public void Goal(TaskType type, IModGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        CheckOpen();
        if (!_goalsOpen) throw new InvalidOperationException("Goals are registered only when the world is made.");

        var claim = GoalClaim(type);
        if (_goals.Exists(g => g.Type == type)) throw new ArgumentException($"Goal type {(int)type} is registered already.", nameof(type));

        _goals.Add(new ModGoalClaim(goal, type, claim, _system));
    }

    /// <summary>
    /// Registers <paramref name="jobs"/> as the contributor of the jobs of <paramref name="type"/>, a job type the system's mod claimed (from 1000
    /// up): the chooser's job passes ask it for each listed job of that type, for any character with a job list.
    /// </summary>
    public void Jobs(TaskType type, IJobSource jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        CheckOpen();
        CheckJobClaim(type);
        if (_decisions.JobSourceOf(type) is not null) throw new ArgumentException($"Job type {(int)type} has a contributor already.", nameof(type));

        _decisions.RegisterModJob(type, jobs, _mod ?? "?");
        _jobsAdded.Add(type);
    }

    /// <summary>
    /// Has the <see cref="IChooserOwner"/> system at <paramref name="index"/> of <paramref name="world"/> register (goals only while the world is
    /// made, <paramref name="goalsOpen"/>) into <paramref name="goals"/> and the world's chooser host. A throw drops what that system registered and
    /// is logged; a mod's system is also stopped, with the player's notice, as for a throw of one of its phases (<see cref="ModSystemFaults.Fault"/>).
    /// </summary>
    internal static void Collect(World world, ITickSystem system, int index, SimulationExtensions? extensions, List<ModGoalClaim> goals,
        bool goalsOpen)
    {
        if (system is not IChooserOwner owner) return;

        var factory = SimulationExtensions.FactoryOf(system);
        var registry = new ChooserRegistry(extensions, world.Decisions, goals, factory?.Owner, index, goalsOpen, system);
        try
        {
            registry.Run(owner);
        }
        catch (Exception e)
        {
            string who = factory is null ? system.GetType().FullName ?? "?" : $"{factory.Owner} '{factory.Name}'";
            Console.Error.WriteLine($"mod       {who}: chooser goals and jobs not registered for this world: {e}");
            if (factory is not null) world.ModFaults.Fault(index, "chooser registration", e);
        }
    }

    /// <summary>
    /// Runs <paramref name="owner"/>'s registration; a throw takes back what it registered and is passed on.
    /// </summary>
    void Run(IChooserOwner owner)
    {
        _goalsBefore = _goals.Count;
        _filtersBefore = _decisions.DesireFilters.Hooks.Count;
        _open = true;
        try
        {
            owner.Register(this);
        }
        catch
        {
            Undo();
            throw;
        }
        finally
        {
            _open = false;
        }
    }

    /// <summary>
    /// Throws outside <see cref="IChooserOwner.Register"/>.
    /// </summary>
    void CheckOpen()
    {
        if (!_open) throw new InvalidOperationException("Chooser goals and jobs are registered only in IChooserOwner.Register.");
    }

    /// <summary>
    /// The claim of goal type <paramref name="type"/>, which must be the registering system's mod's.
    /// </summary>
    ModGoalType GoalClaim(TaskType type)
    {
        if (_extensions?.GoalTypes.TryGetValue(type, out var claim) != true)
            throw new ArgumentException($"Goal type {(int)type} is not claimed (AddGoalType).", nameof(type));
        if (_mod is not null && claim!.Owner != _mod)
            throw new ArgumentException($"Goal type {(int)type} is claimed by mod '{claim.Owner}'.", nameof(type));

        return claim!;
    }

    /// <summary>
    /// Throws unless job type <paramref name="type"/> is claimed by the registering system's mod.
    /// </summary>
    void CheckJobClaim(TaskType type)
    {
        if (_extensions?.JobTypes.ContainsKey(type) != true)
            throw new ArgumentException($"Job type {(int)type} is not claimed (AddJobType).", nameof(type));
        if (_mod is not null && _extensions.TaskOwners.GetValueOrDefault(type) is { } owner && owner != _mod)
            throw new ArgumentException($"Job type {(int)type} is claimed by mod '{owner}'.", nameof(type));
    }

    /// <summary>
    /// Takes back the goals and job contributors this registry added.
    /// </summary>
    void Undo()
    {
        _goals.RemoveRange(_goalsBefore, _goals.Count - _goalsBefore);
        foreach (var type in _jobsAdded) _decisions.RemoveJob(type);
        _jobsAdded.Clear();
        _decisions.DesireFilters.Hooks.TruncateTo(_filtersBefore);
    }

}

/// <summary>
/// A registered goal before the AI takes it up: the mod's goal, its type, its claim and the registering system's index in the world.
/// </summary>
internal sealed record ModGoalClaim(IModGoal Goal, TaskType Type, ModGoalType Claim, int System);

/// <summary>
/// A goal of a mod's own type (from 1000 up) that the chooser weighs as it weighs the engine's goals: by the list record's tier, hours and weight,
/// with the running goal's continuity (docs/modding.md "Chooser goals and jobs"). The mod acts it out.
/// </summary>
public interface IModGoal
{
    /// <summary>
    /// How much the character wants the goal now; 0 or below: not wanted. Called in parallel with other characters' asks, on the asker's partition,
    /// for a character whose lists hold the type in the hour's window: answer from <paramref name="ask"/> and from the mod's own state that only its
    /// serial steps change. A throw or an answer that is not a finite number drops the goal for the world from the next tick.
    /// </summary>
    float Desire(in ModGoalAsk ask);

    /// <summary>
    /// The goal won: find its target and start its act-out (<see cref="TaskStart"/>, a row of the mod's own from 1000 up,
    /// <see cref="TaskSource.Goal"/>). Called serially, in slot order, in the tick it wins or the next, only while the chooser still holds the
    /// character for it. True: the goal holds the character until it ends; false: nothing started, and the type weighs 0 for that character for four
    /// think periods.
    /// </summary>
    bool Start(World world, in ModGoalStart ask);

    /// <summary>
    /// The goal stopped holding the character for <paramref name="why"/>: drop what the act-out holds and leave the task alone (for
    /// <see cref="ModGoalEnd.Displaced"/> the engine stops a task that is still the act-out's). Called serially, in slot order, in the tick the end
    /// is seen or the next. Not called after <see cref="ModGoals.Done"/>, nor once the goal is dropped (a fault, or its system stopped), nor at a load
    /// (nothing held is saved).
    /// </summary>
    void End(World world, int slot, CharacterId id, TaskType type, ModGoalEnd why);
}

/// <summary>
/// One character and a goal of a mod's type as the goal's start is asked about it (serial): the character's slot, id and squad, the goal type and its
/// record's <c>targeting</c>.
/// </summary>
public readonly record struct ModGoalStart(int Slot, CharacterId Id, Squad Squad, TaskType Type, TaskTargetType Targeting);

/// <summary>
/// Why a mod's goal stopped holding its character (<see cref="IModGoal.End"/>).
/// </summary>
public enum ModGoalEnd : byte
{
    /// <summary>
    /// Another goal, or a job or source pick of a player character, won the chooser: the engine stops a task that is still the act-out's, and
    /// the next tick acts the winner out.
    /// </summary>
    Displaced,
    /// <summary>
    /// An order's task starts on the character, or it changes faction (a recruit).
    /// </summary>
    Order,
    /// <summary>
    /// The character's plan was reset: a knock-out, death or pick-up (its plan wiped), an order on its body (a stumble, a get-up), a new mind, or
    /// the goal's plan ended (it left its hours).
    /// </summary>
    Replanned,
    /// <summary>
    /// Something else holds the character now: it flees, fights or is caged, or a task another system started replaced the act-out.
    /// </summary>
    Taken,
    /// <summary>
    /// The character left the world.
    /// </summary>
    Gone,
}

/// <summary>
/// The mod's serial steps' view of its goals (docs/modding.md "Chooser goals and jobs"); serial phases only, and only for a type from 1000 up.
/// </summary>
public static class ModGoals
{
    /// <summary>
    /// Whether the goal <paramref name="type"/> holds the character at <paramref name="slot"/> and its act-out may go on now: it started, and the AI
    /// still holds the character for it.
    /// </summary>
    public static bool Holds(World world, int slot, TaskType type) => GoalsOf(world, type)?.Runs(world, slot, type) == true;

    /// <summary>
    /// The act-out is over by the mod's rule: the goal lets the character go, a task still the act-out's stops (a walk stands, another ends through
    /// its handler), and the character chooses again at its next think. <see cref="IModGoal.End"/> is not called. Nothing when the goal does not
    /// hold the character.
    /// </summary>
    public static void Done(World world, int slot, TaskType type) => GoalsOf(world, type)?.Done(world, slot, type);

    /// <summary>
    /// The world's goals when <paramref name="type"/> is a registered mod goal type; null for an unregistered one. Throws in a parallel phase and for
    /// a type below 1000.
    /// </summary>
    static DutyGoals? GoalsOf(World world, TaskType type)
    {
        if (world.Characters.Phase == TablePhase.Parallel) throw new InvalidOperationException("Mod goals are read in the serial phases only.");
        if ((int)type < SimulationExtensions.FirstModCode)
            throw new ArgumentException($"Type {(int)type} is no mod goal type (from {SimulationExtensions.FirstModCode} up).", nameof(type));

        return world.SystemOf<AiSystem>()?.Duties is { } goals && goals.IsMod(type) ? goals : null;
    }
}
