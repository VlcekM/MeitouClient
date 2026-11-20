using Meitou.Content;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The job contributors a code mod's system registered in one world (docs/modding.md "Chooser goals and jobs"), with their calls caught. One whose
/// call throws is dropped for that world: it answers that it cannot run and starts nothing from then on, what the throwing call did stands, the
/// first fault is logged with the mod's id and the player gets a notice, and the faults enter the state hash. The job passes run in the serial
/// phases only, so the drop falls at the same call at any thread count. The engine's contributors are called as they are, never caught.
/// </summary>
sealed class ModDecisionFaults
{
    /// <summary>
    /// The answer of a dropped source or contributor.
    /// </summary>
    static readonly DecisionCheck DroppedAnswer = DecisionCheck.No("stopped after an error");

    /// <summary>
    /// The guards in registration order (their index is what the hash records).
    /// </summary>
    readonly List<Guard> _guards = [];

    /// <summary>
    /// How many guards have dropped their registration.
    /// </summary>
    int _faults;

    /// <summary>
    /// <paramref name="source"/>, the contributor mod <paramref name="mod"/> registered for job type <paramref name="type"/>, with its calls caught.
    /// </summary>
    public IJobSource Wrap(IJobSource source, string mod, TaskType type) =>
        new GuardedJobs(source, Add(mod, $"job contributor for type {(int)type}"));

    /// <summary>
    /// Adds the dropped registrations to <paramref name="hasher"/>; nothing while none has dropped.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_faults == 0) return;

        hasher.Add(0x4D444543);   // "MDEC"
        hasher.Add(_faults);
        for (int i = 0; i < _guards.Count; i++)
        {
            if (_guards[i].Dropped) hasher.Add(i);
        }
    }

    /// <summary>
    /// A new guard for a registration of mod <paramref name="mod"/>, named <paramref name="what"/> in the log and the notice.
    /// </summary>
    Guard Add(string mod, string what)
    {
        var guard = new Guard(this, mod, what);
        _guards.Add(guard);
        return guard;
    }

    /// <summary>
    /// One registration's fault state.
    /// </summary>
    sealed class Guard(ModDecisionFaults owner, string mod, string what)
    {
        /// <summary>
        /// Set once a call threw: the registration is not called again in this world.
        /// </summary>
        public bool Dropped { get; private set; }

        /// <summary>
        /// Drops the registration after <paramref name="call"/> threw <paramref name="e"/>, with the log line and the player's notice; once only.
        /// </summary>
        public void Fault(World world, string call, Exception e)
        {
            if (Dropped) return;

            Dropped = true;
            owner._faults++;

            Console.Error.WriteLine($"mod       {mod}: {what} dropped for this world, its {call} threw at tick {world.Tick}: {e}");
            if (!CodeModFaults.Tell(mod, e))
                world.ModFaults.Notify($"Mod {mod}: its {what} stopped after an error ({e.GetType().Name}); the rest of the game goes on.");
        }
    }

    /// <summary>
    /// A mod's job contributor with its calls caught.
    /// </summary>
    sealed class GuardedJobs(IJobSource inner, Guard guard) : IJobSource
    {
        /// <summary>
        /// The mod's answer for job <paramref name="k"/>; it cannot run once dropped or when the call throws.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            if (guard.Dropped) return DroppedAnswer;

            try
            {
                return inner.CanRun(world, slot, k);
            }
            catch (Exception e)
            {
                guard.Fault(world, "CanRun", e);
                return DroppedAnswer;
            }
        }

        /// <summary>
        /// The mod's start of job <paramref name="k"/>; nothing once dropped.
        /// </summary>
        public void Start(World world, int slot, int k)
        {
            if (guard.Dropped) return;

            try
            {
                inner.Start(world, slot, k);
            }
            catch (Exception e)
            {
                guard.Fault(world, "Start", e);
            }
        }

        /// <summary>
        /// Whether the mod calls job <paramref name="k"/> urgent; not once dropped or when the call throws.
        /// </summary>
        public bool Urgent(World world, int slot, int k)
        {
            if (guard.Dropped) return false;

            try
            {
                return inner.Urgent(world, slot, k);
            }
            catch (Exception e)
            {
                guard.Fault(world, "Urgent", e);
                return false;
            }
        }

        /// <summary>
        /// The mod's stop of job <paramref name="k"/>; nothing once dropped.
        /// </summary>
        public void Stop(World world, int slot, int k)
        {
            if (guard.Dropped) return;

            try
            {
                inner.Stop(world, slot, k);
            }
            catch (Exception e)
            {
                guard.Fault(world, "Stop", e);
            }
        }
    }
}
