using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Settings of the <see cref="CaptiveSystem"/>: engine choices unless a doc label says otherwise.
/// </summary>
public sealed record CaptiveSettings
{
    /// <summary>
    /// The captive goals' finders look for each character once in this many ticks (on its slot's turn) (engine: the original's finders run at each
    /// score, so a pick is at most this old).
    /// </summary>
    public int SweepEveryTicks { get; init; } = 30;
    /// <summary>
    /// A worker is at its cage within the cage's <c>use range</c> plus this; a walk that ended within <see cref="Slack"/> counts too (a cage inside a
    /// building).
    /// </summary>
    public float Reach { get; init; } = 15;
    /// <summary>
    /// How far from the cage a walk that ended still counts as there (<see cref="Reach"/>).
    /// </summary>
    public float Slack { get; init; } = 200;
    /// <summary>
    /// Walks that end away from the cage before the job is given up.
    /// </summary>
    public int Walks { get; init; } = 3;
    /// <summary>
    /// The length of the kill clip <c>chop down static</c> (the system builder passes the data's), and the share of it at which its blow lands
    /// (<b>Observed</b>: 0.55).
    /// </summary>
    public float ChopSeconds { get; init; } = 1.5f;
    /// <summary>
    /// The share of the kill clip at which its blow lands (<see cref="ChopSeconds"/>).
    /// </summary>
    public float BlowAt { get; init; } = 0.55f;
    /// <summary>
    /// The slave processing kneels this long before the captive is stripped and enslaved (<b>Observed</b>: 3 s, <c>Task_ProcessNewSlave</c>).
    /// </summary>
    public float ProcessSeconds { get; init; } = 3;
    /// <summary>
    /// Cage guards leave a caged member of their own faction who is no slave alone instead of making it a slave (Meitou option, off as the game: its
    /// finders have no faction test; <c>CaptiveSystem.Spared</c>, asked by the processing and gathering finders). Set while the game runs.
    /// </summary>
    public bool SpareOwnFaction { get; set; }
    /// <summary>
    /// Watching slaves (183) looks at the cages within this distance of the watcher (<b>Observed</b>: 180, <c>FUN_1408cd630</c>).
    /// </summary>
    public float WatchRange { get; init; } = 180;
    /// <summary>
    /// The weapon strip kneels this long (<b>Observed</b>: after 0.8 s, <c>Task_StripWeapons</c>).
    /// </summary>
    public float StripSeconds { get; init; } = 0.8f;
    /// <summary>
    /// The cuffing takes this long. <b>Engine choice</b>: the original's bar grows by 0.1 x a per-update float of the character
    /// (<c>FUN_140336e60</c>, taken as the frame time) and ends at 0.99, so about 10 s.
    /// </summary>
    public float CuffSeconds { get; init; } = 10;
    /// <summary>
    /// It stands within this distance of its slave and follows it when the slave moves on.
    /// </summary>
    public float ShoutReach { get; init; } = 40;
    /// <summary>
    /// The eater and its meal say their lines at most this often (game seconds); the original fires them every frame and its dialogue throttles them.
    /// </summary>
    public float BarkSeconds { get; init; } = 10;
    /// <summary>
    /// Keep a <see cref="CaptiveSystem.Log"/> (tests).
    /// </summary>
    public bool RecordLog { get; init; }
}

/// <summary>
/// What a <see cref="CaptiveJob"/> does to its captive.
/// </summary>
public enum CaptiveJobKind : byte
{
    /// <summary>
    /// Goals 135 / 282 by way of 134 (<c>Task_KillPrisoner</c>): blows until the captive dies.
    /// </summary>
    Kill,
    /// <summary>
    /// Goals 139 / 282 by way of 220 (<c>Task_EatPrisoner</c>): eaten part by part.
    /// </summary>
    Eat,
    /// <summary>
    /// Goals 183 / 260 by way of 182 (<c>Task_ProcessNewSlave</c>): stripped, dressed in slave clothing and enslaved.
    /// </summary>
    Process,
    /// <summary>
    /// Goals 173 / 217 / 245 by way of 174 (<c>Task_GetSlave</c>): taken out of the cage as the squad's slave.
    /// </summary>
    Gather,
    /// <summary>
    /// Goal 192: the leader walks to the shipping town.
    /// </summary>
    Deliver,
    /// <summary>
    /// The slave goal 172 (<c>Task_UseCage</c>): a slave walks into a free cage of its owner.
    /// </summary>
    CageSelf,
    /// <summary>
    /// Goal 167 (<c>Task_StripWeapons</c>, <c>Task_CuffTarget</c>): a downed character stripped, shackled and carried to a cage.
    /// </summary>
    Capture,
    /// <summary>
    /// Goal 168: a downed slave of the carrier's faction carried to a cage.
    /// </summary>
    Carry,
    /// <summary>
    /// Goal 188 (<c>Task_TalktoPlayer</c> with event 38): a slaver shouts at one of its slaves.
    /// </summary>
    Shout,
    /// <summary>
    /// Goal 276 Liberating prisoners by way of 110 (<c>Task_ReleasePrisoner</c>): a prisoner of a faction not allied to the worker's is let out.
    /// </summary>
    Liberate,
    /// <summary>
    /// Goals 230 / 233 by way of 220 (<c>Task_EatPrisoner</c>): a dead or downed body on the ground eaten where it lies (no cage).
    /// </summary>
    EatBody,
    /// <summary>
    /// Goal 232 by way of 231 (<c>Task_EatingCrops</c>): a player farm eaten plant by plant (<see cref="CaptiveJob.Cage"/> holds the farm's site id).
    /// </summary>
    EatCrops,
    /// <summary>
    /// Goal 237 by way of 236 (<c>Task_WorshipTarget</c>): a fogman bows at a cage where someone is being eaten.
    /// </summary>
    Worship,
    /// <summary>
    /// Race goal 278 (<c>Task_DogLimbRunningPlay</c>): a dog sprints about with a limb in its mouth.
    /// </summary>
    DogPlay,
    /// <summary>
    /// Race goal 279: a dog runs after an ally with a limb in its mouth (<see cref="CaptiveJob.Captive"/>).
    /// </summary>
    DogChase,
    /// <summary>
    /// Race goal 277 by way of 3 (<c>Task_Loot_AI</c>'s pick-up): an animal fetches a severed limb into its mouth.
    /// </summary>
    Fetch,
    /// <summary>
    /// Goals 194 / 218 by way of 159 (event 39 SLAVE_DELIVERY): the leader says the event to a slave pen's keeper in the town it stands in, whose
    /// dialogue hands its slaves over (DA 24).
    /// </summary>
    Sell,
    /// <summary>
    /// Goal 259 by way of 3 on a body (<c>Task_Loot_AI</c>): the food on a dead body its finder's second pass found is looted.
    /// </summary>
    LootFood,
}

/// <summary>
/// The phase of a job: walking to the captive, then working on it.
/// </summary>
public enum CaptivePhase : byte
{
    /// <summary>
    /// Walking to the captive.
    /// </summary>
    Walk,
    /// <summary>
    /// Working on it.
    /// </summary>
    Work,
    /// <summary>
    /// Lifting it (168's row 170).
    /// </summary>
    Lift
}

/// <summary>
/// One worker's job on one captive (serial state of the <see cref="CaptiveSystem"/>).
/// </summary>
public sealed class CaptiveJob
{
    /// <summary>
    /// What the job does.
    /// </summary>
    public required CaptiveJobKind Kind { get; init; }
    /// <summary>
    /// The character doing it.
    /// </summary>
    public required CharacterId Worker { get; init; }
    /// <summary>
    /// The character it is done on (none for a job without one).
    /// </summary>
    public required CharacterId Captive { get; init; }
    /// <summary>
    /// The cage the captive is in (or, for <see cref="CaptiveJobKind.Deliver"/> and <see cref="CaptiveJobKind.CageSelf"/>, the cage it goes into).
    /// </summary>
    public required string Cage { get; init; }
    /// <summary>
    /// The worker's faction when the job started.
    /// </summary>
    public required int Faction { get; init; }
    /// <summary>
    /// The town the job is in (its kill timer), -1: none.
    /// </summary>
    public int Site { get; init; } = -1;
    /// <summary>
    /// The job's phase.
    /// </summary>
    public CaptivePhase Phase { get; set; }
    /// <summary>
    /// Game seconds in the phase.
    /// </summary>
    public float Time { get; set; }
    /// <summary>
    /// Walks started so far: a walk that ends short after the first is a lost walk (<see cref="CaptiveSystem.Lost"/>).
    /// </summary>
    public int Walks { get; set; }
    /// <summary>
    /// A goal's job whose walk ended short of its place (<see cref="CaptiveSystem.Lost"/>): its end is a lost walk on <see cref="LostKey"/>.
    /// </summary>
    internal bool LostWalk { get; set; }
    /// <summary>
    /// The ignore-map key of a lost walk's target (none: nothing is remembered).
    /// </summary>
    internal IgnoreKey? LostKey { get; set; }
    /// <summary>
    /// Blows struck (kill); the game second of the next line (eat); the job's steps since event 39 went out (sell).
    /// </summary>
    public int Blows { get; set; }
    /// <summary>
    /// Blows that beat an imprisoned captive down before a capture (196).
    /// </summary>
    public int Beats { get; set; }
    /// <summary>
    /// The second of the phase (<see cref="Time"/>) at which the worker says its next line.
    /// </summary>
    public float NextBark { get; set; }
    /// <summary>
    /// The chooser's goal whose act-out the job is (<see cref="CaptiveGoals"/>).
    /// </summary>
    internal TaskType Goal { get; init; }
    /// <summary>
    /// The goal's finder target as the plan holds it.
    /// </summary>
    internal PlanTarget Target { get; init; }
    /// <summary>
    /// The ground item a fetch picks up (-1: none).
    /// </summary>
    internal int Item { get; init; } = -1;
    /// <summary>
    /// An order's job (<see cref="CaptiveSystem"/>'s 220 order), not a goal's: it runs while the order is the worker's queue front.
    /// </summary>
    internal bool Ordered { get; init; }
    /// <summary>
    /// An order's own reach (<see cref="Player.OrderEntry.Reach"/>) in place of <see cref="CaptiveSettings.Reach"/>; null: the settings'.
    /// </summary>
    internal float? Reach { get; init; }
    /// <summary>
    /// The provenance of the job's walks: an order's job walks as its order, a goal's as its goal.
    /// </summary>
    internal TaskSource Source => Ordered ? TaskSource.Order : TaskSource.Goal;

    /// <summary>
    /// Hashes the job's state.
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add((int)Goal);
        h.Add(Item);
        h.Add((int)Kind);
        h.Add(Worker);
        h.Add(Captive);
        h.Add(Rng.StableHash(Cage));
        h.Add(Faction);
        h.Add(Site);
        h.Add((int)Phase);
        h.Add(Time);
        h.Add(Walks);
        h.Add(Blows);
        h.Add(NextBark);
        if (Beats != 0) h.Add(Beats);
        if (Ordered) h.Add(0x4F726472);
        if (Reach is { } reach) h.Add(reach);
    }
}

/// <summary>
/// What happened in a captive job (<see cref="CaptiveEvent"/>).
/// </summary>
public enum CaptiveEventKind
{
    /// <summary>
    /// A job started.
    /// </summary>
    Started,
    /// <summary>
    /// The captive was killed.
    /// </summary>
    Killed,
    /// <summary>
    /// The captive was eaten.
    /// </summary>
    Eaten,
    /// <summary>
    /// The captive was made a slave (a capture's or a processing's).
    /// </summary>
    Enslaved,
    /// <summary>
    /// A slave was gathered from a cage into the squad.
    /// </summary>
    Gathered,
    /// <summary>
    /// A slave was put in a cage.
    /// </summary>
    Caged,
    /// <summary>
    /// The job ended without its work done.
    /// </summary>
    GaveUp,
    /// <summary>
    /// The captive was cuffed.
    /// </summary>
    Captured,
    /// <summary>
    /// The worker shouted at a slave.
    /// </summary>
    Shouted,
    /// <summary>
    /// The captive was released.
    /// </summary>
    Liberated
}

/// <summary>
/// One event of a captive job, for the log and the tests.
/// </summary>
public readonly record struct CaptiveEvent(long Tick, CaptiveEventKind Kind, CaptiveJobKind Job, CharacterId Worker, CharacterId Captive);

/// <summary>
/// What captors do with their captives (docs/simulation.md "Captives"; facts in docs/game/ai-tasks.md "Prisoner fate" and "Slave rows"). The original
/// runs these as package and race goals planned down to their tasks; each is a goal of the one chooser here (<see cref="CaptiveGoals"/>: the finders'
/// serial look, desires, states), and a goal the chooser starts is acted out as a job of its worker, worked serially in <c>SlowWorld</c> while the
/// goal holds the worker (<see cref="DutyGoals"/>) and done at the original's own end. A player-faction animal's race goals are the same goals, on
/// its chooser's rungs (<see cref="Ladder.OnRungs"/>).
/// <list type="bullet">
/// <item><b>Kill</b> (135 "Kill random prisoner"; 282 "Dinner time" eats instead): wanted when the town's kill timer has run out or all the faction's
/// cages there are full (<b>Observed</b>, AI slot 20 <c>FUN_14032e9b0</c>; the timer is set to now + uniform[1, 15) game hours when the town is first
/// asked about and whenever a captive dies of a kill or a meal, <c>FUN_14034a810</c>). The target is the best occupied cage (the cage finder's
/// weights and usable test, <c>FUN_1408d0770</c>,
/// <see cref="FateTarget"/>), its occupant alive; when that best cage holds a robot there is no target this ask;
/// the occupant says event 10 MARKED_FOR_DEATH to the worker. Its plan draws a weapon first (B list hasWeaponEquipped, <see cref="DrawWeapon"/>);
/// with none to draw the goal does not plan. At the cage it plays <c>chop down static</c> and strikes once per clip at
/// 0.55 of it (<see cref="CombatSystem.Execute"/>), the occupant crying event 11 SCREAMING_TORTURE each time, until it dies.</item>
/// <item><b>Eat</b> (139 "Eat random prisoner", wanted always; 282 at the kill rate): the same target; the eater sheathes its weapon (B list
/// !hasWeaponEquipped), plays <c>eat</c>, says event 52 EATING_SOMETHING_SOUNDS while the captive says 11, and the captive is eaten
/// (<see cref="MedicalState.BeingEaten"/>) until it dies.</item>
/// <item><b>Process</b> (260 "Watching for new slaves": an awake occupant of a cage of the town, not a slave, <c>FUN_1408cc110</c>; 183 "Watching
/// slaves": an occupant within 180 that needs processing, <c>FUN_1408cd630</c>; both only in cages of non-player factions): the worker kneels 3 s
/// (<c>medic</c>), then strips the captive's weapons and armour into its own backpack or inventory, marked stolen from the captive (locked shackles
/// stay), dresses it in its faction's <c>slave clothing</c> where the slot is free, says event 35 INTRODUCING_NEW_SLAVE (37 RECAPTURED_A_SLAVE for
/// one whose head is shaven already), makes it its faction's slave and shaves its head (<c>Task_ProcessNewSlave</c>, <b>Observed</b>; the head
/// <b>Verified</b>).</item>
/// <item><b>Capture / Carry / Shout</b> (167, 168, 188): see <c>CaptiveSystem.Capture.cs</c>. The slaves' own work (189, 190) is not a job but the
/// AI's <see cref="AiBehaviour.Work"/>.</item>
/// </list>
/// The jobs and the kill timers are not saved (the original keeps the timers in a global map it does not save, <b>Observed</b>); hashed only while
/// any exist.
/// </summary>
public sealed partial class CaptiveSystem(PopulationData data, CombatSystem? combat, CaptiveSettings? settings = null) : ITickSystem, IStateHashed,
        IGoalSource, IDutyGoalOwner
{
    #region State and the serial step

    /// <summary>
    /// The jobs under way, in the order they started.
    /// </summary>
    readonly List<CaptiveJob> _jobs = [];

    /// <summary>
    /// The captive goals of the chooser: the finders' picks, the desires and states, the starts and ends of their jobs.
    /// </summary>
    CaptiveGoals? _goalsOfChooser;

    /// <summary>
    /// The order owner of 220 Eat target alive (<see cref="OrderOwner"/>), made on first use.
    /// </summary>
    CaptiveOrders? _orders;

    /// <summary>
    /// The owner of the order 220 Eat target alive (docs/modding.md "Ordering characters"), made on first use.
    /// </summary>
    internal CaptiveOrders OrderOwner => _orders ??= new CaptiveOrders(this);

    /// <summary>
    /// The captive goals (<see cref="_goalsOfChooser"/>), made on first use.
    /// </summary>
    internal CaptiveGoals Goals => _goalsOfChooser ??= new CaptiveGoals(this, data);

    /// <summary>
    /// The engine goals of the world (the AI's), resolved at the first step that needs them.
    /// </summary>
    DutyGoals? _duties;

    /// <summary>
    /// The events of the jobs, while <see cref="CaptiveSettings.RecordLog"/> is on.
    /// </summary>
    readonly List<CaptiveEvent> _log = [];

    /// <summary>
    /// Refreshes a character's equipment after a job changed what it wears.
    /// </summary>
    readonly CharacterAssembly _assembly = new(data);

    /// <summary>
    /// The XP a meal still awake gains in toughness.
    /// </summary>
    readonly XpService _xp = new(data.Bodies.Constants, data.BodyOptions);

    /// <summary>
    /// The settings (the defaults when none were given).
    /// </summary>
    public CaptiveSettings Settings { get; } = settings ?? new CaptiveSettings();

    /// <summary>
    /// The length of the kill clip in game seconds (the system builder sets the data's <c>chop down static</c>;
    /// <see cref="CaptiveSettings.ChopSeconds"/> without animations).
    /// </summary>
    public float ChopSeconds { get; set; } = (settings ?? new CaptiveSettings()).ChopSeconds;

    /// <summary>
    /// The jobs under way.
    /// </summary>
    public IReadOnlyList<CaptiveJob> Jobs => _jobs;

    /// <summary>
    /// The events of the jobs (only while <see cref="CaptiveSettings.RecordLog"/> is on).
    /// </summary>
    public IReadOnlyList<CaptiveEvent> Log => _log;

    /// <summary>
    /// The serial step: the slavers restored now and then, the racks, every job stepped on, then the captive goals' finders on each character's turn.
    /// </summary>
    public void SlowWorld(World world)
    {
        if (world.Beat(RestoreEveryTicks, TickPhases.SlaverRestore)) RestoreSlavers(world);
        StepRacks(world);
        StepJobs(world);
        OrderRunsFromRisers(world);

        // Each character is looked at once in SweepEveryTicks, on its slot's turn (slot mod the period), so the looks spread over the ticks
        // (docs/simulation.md "Spread sweeps"; the original runs its AI updates 8 characters a frame in turn, FUN_140786e30).
        Goals.Look(world, world.Every(Settings.SweepEveryTicks));
    }

    /// <summary>
    /// Registers the captive goals with the chooser (<see cref="CaptiveGoals.Register"/>).
    /// </summary>
    void IDutyGoalOwner.RegisterGoals(DutyGoals goals) => Goals.Register(goals);

    /// <summary>
    /// Steps every job on, in list order: a job is stepped only while its goal holds the worker (one it no longer holds is left to the AI's sweep,
    /// which ends it), and one over by its own rule is done (<see cref="DutyGoals.Done"/>).
    /// </summary>
    void StepJobs(World world)
    {
        var table = world.Characters;
        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        for (int k = 0; k < _jobs.Count;)
        {
            var j = _jobs[k];
            int w = table.TryResolveNext(j.Worker, out int at) ? at : -1;
            if (j.Ordered && (w < 0 || !OrderOwner.Holds(world, w, j)))
            {
                _jobs.RemoveAt(k);
                End(world, j, CaptiveEventKind.GaveUp);
                continue;
            }

            if (w < 0 || !j.Ordered && _duties?.Runs(world, w, j.Goal) != true || Step(world, j))
            {
                k++;
                continue;
            }

            _jobs.RemoveAt(k);
            Finish(world, w, j);
        }
    }

    /// <summary>
    /// A goal's job is over by its own rule: the goal is done (its act-out's task stops), or for a lost walk ends as one
    /// (<see cref="DutyGoals.LostWalk"/>: its target ignored for 20 s), and the worker's finders look again, so its next choice sees what is left.
    /// </summary>
    void Finish(World world, int w, CaptiveJob j)
    {
        if (j.Ordered)
        {
            TaskStart.Stand(world.Characters, w);
            Goals.LookAt(world, w);
            return;
        }

        if (j.LostWalk) _duties!.LostWalk(world, w, j.Goal, j.LostKey);
        else _duties!.Done(world, w, j.Goal);

        Goals.LookAt(world, w);
    }

    /// <summary>
    /// Whether a goal's job lost its walk: called once its walk is over short of the place, true when it had started one (<see cref="CaptiveJob.Walks"/>
    /// above 0), marking the job so (<paramref name="key"/>, its target): the original's plan step ends a running step whose mover is idle away from
    /// its target and ignores that target for 20 s (<c>FUN_14050d970</c>, <b>Verified</b>; no tries). False before its first walk.
    /// </summary>
    internal static bool Lost(CaptiveJob j, IgnoreKey? key)
    {
        if (j.Walks == 0) return false;

        (j.LostWalk, j.LostKey) = (true, key);
        return true;
    }

    /// <summary>
    /// Whether the job's last walk (<see cref="WalkFor"/>) is over and the movement gave it up (no way there, blocked, a locked door:
    /// <see cref="WalkEnd.Failed"/>): then marked a lost walk on <paramref name="key"/> (<see cref="Lost"/>; false before its first walk).
    /// </summary>
    internal static bool OwnWalkLost(CharacterTable table, int w, CaptiveJob j, IgnoreKey? key) =>
        table.Next[w].Task != (byte)CharacterTask.GoTo && table.Cold(w)!.WalkEnd == WalkEnd.Failed && Lost(j, key);

    /// <summary>
    /// A walk of the job's worker on the job's row, counted in <see cref="CaptiveJob.Walks"/>.
    /// </summary>
    internal static void WalkFor(World world, CaptiveJob j, int w, Vector2 to, byte mode)
    {
        TaskStart.Walk(world, w, to, mode, RowOf(j.Kind), j.Source);
        j.Walks++;
    }

    /// <summary>
    /// A job whose own task's walk found no way (a lift's or a loot's, <see cref="CarrySystem.LiftWalkFails"/>): ends it (false, for the step) as a
    /// lost walk on <paramref name="key"/>, as the original's plan step answers any failed mover (<c>FUN_14050d970</c>, <b>Verified</b>).
    /// </summary>
    bool LostOwnWalk(World world, CaptiveJob j, IgnoreKey? key)
    {
        (j.LostWalk, j.LostKey) = (true, key);
        return End(world, j, CaptiveEventKind.GaveUp);
    }

    /// <summary>
    /// One tick of a loot run (<see cref="CaptiveJobKind.LootFood"/>): over once the loot task ends; a lost walk when no path gets the looter to its
    /// body (<see cref="CarrySystem.LootWalkFails"/>).
    /// </summary>
    bool StepLootFood(World world, CaptiveJob j, int w)
    {
        if (world.Characters.Next[w].Task != (byte)CharacterTask.Loot) return End(world, j, null);

        return world.SystemOf<CarrySystem>()?.LootWalkFails(world, w) != true || LostOwnWalk(world, j, IgnoreKey.Of(j.Captive));
    }

    /// <summary>
    /// The job a goal's act-out runs for <paramref name="worker"/> (null: none).
    /// </summary>
    internal CaptiveJob? JobOf(CharacterId worker, TaskType goal)
    {
        foreach (var j in _jobs)
            if (j.Worker == worker && j.Goal == goal) return j;
        return null;
    }

    /// <summary>
    /// Starts a goal's job (<see cref="CaptiveGoals"/>, serial): it is kept with the others and its start logged.
    /// </summary>
    internal void Begin(World world, CaptiveJob job)
    {
        _jobs.Add(job);
        Note(world, CaptiveEventKind.Started, job);
    }

    /// <summary>
    /// A goal holding <paramref name="worker"/> ended by the chooser or what took the worker (<see cref="IDutyGoal.End"/>, serial): its job goes, given
    /// up, and its clip with it; a captive of a kill or a meal that died meanwhile still resets its town's kill timer (the task's end does,
    /// <c>FUN_140353990</c>). The task is left to whatever ended the goal.
    /// </summary>
    internal void Drop(World world, CharacterId worker, TaskType goal)
    {
        if (JobOf(worker, goal) is not { } j) return;

        _jobs.Remove(j);
        var table = world.Characters;
        if (table.TryResolveNext(worker, out int w) && table.Cold(w) is { } wc)
        {
            if (j.Kind != CaptiveJobKind.Fetch) wc.ActionClip = null;
            else if (wc.PickingUpItem == j.Item) wc.PickingUpItem = null;
        }

        if (j.Kind is CaptiveJobKind.Kill or CaptiveJobKind.Eat && j.Site >= 0 && table.TryResolveNext(j.Captive, out int c)
            && table.Cold(c)?.Medical is { Dead: true })
            _killAt[j.Site] = NextKill(world, j.Site);

        Note(world, CaptiveEventKind.GaveUp, j);
    }

    /// <summary>
    /// Hashes the goals' picks, the jobs and the kill timers (each only once any exist).
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        _goalsOfChooser?.Hash(ref hasher);
        if (_jobs.Count == 0 && _killAt.Count == 0) return;   // only once in use, so worlds without captives hash as before
        hasher.Add(0x43617074);
        hasher.Add(_jobs.Count);
        foreach (var j in _jobs) j.Hash(ref hasher);
        hasher.Add(_killAt.Count);
        foreach (var (site, at) in _killAt)
        {
            hasher.Add(site);
            hasher.Add(at);
        }

        hasher.Add(_timerRolls);
    }

    #endregion

    #region Jobs

    /// <summary>
    /// The goal rows of the job kinds.
    /// </summary>
    static readonly HashSet<TaskType> JobRows = [.. Enum.GetValues<CaptiveJobKind>().Select(RowOf)];

    /// <summary>
    /// The ANIMATION records the jobs hold (<c>Task_KillPrisoner</c>, <c>Task_EatPrisoner</c>, <c>Task_ProcessNewSlave</c>, <b>Observed</b>).
    /// </summary>
    public const string ChopClip = "chop down static", EatClip = "eat", ProcessClip = "medic";

    /// <summary>
    /// EventTriggerEnum values the captive jobs fire (docs/game/dialogue.md "Enums").
    /// </summary>
    public const int MarkedForDeath = 10, ScreamingTorture = 11, IntroducingNewSlave = 35, RecapturedSlave = 37, SlaveDelivery = 39,
            EatingSounds = 52;

    /// <summary>
    /// Whether a job holds the character as its worker or its captive.
    /// </summary>
    internal bool Busy(CharacterId id)
    {
        foreach (var j in _jobs)
            if (j.Worker == id || j.Captive == id) return true;
        return false;
    }

    /// <summary>
    /// Whether a job is on the cage.
    /// </summary>
    internal bool Taken(string cage)
    {
        foreach (var j in _jobs)
            if (j.Cage == cage) return true;
        return false;
    }

    /// <summary>
    /// The distance from its target within which the job starts its work (at a cage, past the cage's <c>use range</c>): its order's own
    /// (<see cref="CaptiveJob.Reach"/>), else <see cref="CaptiveSettings.Reach"/>.
    /// </summary>
    float ReachOf(CaptiveJob j) => j.Reach ?? Settings.Reach;

    /// <summary>
    /// Whether the worker walks the job's own walk: a GoTo whose running row is the job's (<see cref="RowOf"/>). A walk it was on when the job
    /// started, its AI's or an errand's, is not, so the job sends its own at once, as a goal taken up ends the plan before it (docs/game/ai-tasks.md
    /// "Precedence").
    /// </summary>
    static bool WalksOwn(CharacterTable table, int w, CaptiveJob j) =>
        table.Next[w].Task == (byte)CharacterTask.GoTo && table.Cold(w)?.Running?.Row == RowOf(j.Kind);

    /// <summary>
    /// Whether a walk's row is a job's (<see cref="RowOf"/>): a fight may interrupt a player animal's job walking it (<see cref="Ai.AiSystem"/>'s
    /// pre-emption).
    /// </summary>
    internal static bool IsJobRow(TaskType row) => JobRows.Contains(row);

    /// <summary>
    /// The row a job's walks act out (<see cref="TaskStart.Walk(World, int, Vector3, byte, TaskType, TaskSource)"/>): the task its goal plans to
    /// (<see cref="CaptiveJobKind"/>).
    /// </summary>
    static TaskType RowOf(CaptiveJobKind kind) => kind switch
    {
        CaptiveJobKind.Kill => TaskType.KillCageOccupant,
        CaptiveJobKind.Eat or CaptiveJobKind.EatBody => TaskType.EatTargetAlive,
        CaptiveJobKind.Process => TaskType.ProcessAndStripNewSlave,
        CaptiveJobKind.Gather => TaskType.GetSlave,
        CaptiveJobKind.Deliver => TaskType.GoToSomewhereForDeliveringSlaves,
        CaptiveJobKind.Sell => TaskType.GiveAllMySlavesTo,
        CaptiveJobKind.CageSelf => TaskType.SlaveGetInMyMastersCage,
        CaptiveJobKind.Capture => TaskType.CaptureNewSlaves,
        CaptiveJobKind.Carry => TaskType.CarryWoundedSlaves,
        CaptiveJobKind.Shout => TaskType.WorkTheSlaves,
        CaptiveJobKind.Liberate => TaskType.LiberateAllThePrisoners,
        CaptiveJobKind.EatCrops => TaskType.EatCrops,
        CaptiveJobKind.Worship => TaskType.WorshipTarget,
        CaptiveJobKind.DogPlay => TaskType.PlayBecauseIHaveALimbInMouth,
        CaptiveJobKind.Fetch or CaptiveJobKind.LootFood => TaskType.Pickup,
        _ => TaskType.ChaseAllyDogsWithMouthLimbs,
    };

    /// <summary>
    /// The plan's equip step before 134 Kill prisoner (B list hasWeaponEquipped; 6 <c>Task_EquipBestWeapon</c> <c>FUN_140346c30</c>, <b>Verified</b>:
    /// the weapon on its back, then on its hip, else for a non-player any WEAPON item of its inventory), as the kill's start makes sure of it: a
    /// captor with a weapon in a weapon slot draws it; one carrying a melee weapon elsewhere puts it in a free slot first (<b>engine reading</b>: with
    /// no free slot the original's equip is not read). False with no weapon to draw: the start is refused (finding one is goals 8 and 42).
    /// </summary>
    internal bool DrawWeapon(CharacterCold cold)
    {
        if (cold.Fighter is null || cold.Inventory is not { } inv) return false;
        // A worn unarmed weapon in hand is not swapped for a packed one (engine extension, docs/characters.md "Worn unarmed weapons").
        if (cold.Fighter is { Unarmed: true, Weapon: null }
                && inv.All().FirstOrDefault(i => i.Type == FcsRecordType.Weapon && !InventoryCommands.IsEquipmentSection(i.Section)) is { } packed)
        {
            var owner = inv.Items.Contains(packed) ? inv : inv.Backpack?.Contents;
            int at = owner?.Items.IndexOf(packed) ?? -1;
            if (at >= 0)
            {
                owner!.Items.RemoveAt(at);
                if (InventoryCommands.TryEquip(inv, packed, cold)) _assembly.RefreshEquipment(cold);
                else owner.Items.Insert(at, packed);   // no free weapon slot: left where it was
            }
        }

        if (cold.Fighter is not { Unarmed: false } armed) return false;
        cold.DrawnWeapon = CombatSystem.MeleeDrawn(armed);
        return true;
    }

    #endregion

    #region Finders

    /// <summary>
    /// The kill timer of each town: the game hour from which a kill is wanted again (FUN_14034a810's map), by site.
    /// </summary>
    readonly SortedDictionary<int, double> _killAt = [];

    /// <summary>
    /// The kill timer rolls made so far (each one's salt, so two rolls of one town differ).
    /// </summary>
    int _timerRolls;

    /// <summary>
    /// The salt of the kill timer's roll.
    /// </summary>
    const ulong TimerSalt = 0x4B696C6C54696D72;

    /// <summary>
    /// The ex-slave's lowest slave level (FUN_1405a51e0).
    /// </summary>
    const float ExSlaveFloor = 0.01f;

    /// <summary>
    /// The game hour from which the town <paramref name="site"/> wants a kill again (null: not asked yet).
    /// </summary>
    public double? KillTimer(int site) => _killAt.TryGetValue(site, out double at) ? at : null;

    /// <summary>
    /// The town's cages (registering its fixtures when nobody has yet).
    /// </summary>
    internal IEnumerable<Fixture> CagesOf(World world, int site) => CagesOf(world, data, site);

    /// <summary>
    /// The town's cages (registering its fixtures when nobody has yet).
    /// </summary>
    static IEnumerable<Fixture> CagesOf(World world, PopulationData data, int site)
    {
        if (!world.Fixtures.IsRegistered(site)) world.Fixtures.RegisterSite(world, data, site);
        foreach (var f in world.Fixtures.All)
            if (f.Kind == FixtureKind.Cage && f.Site == site) yield return f;
    }

    /// <summary>
    /// The live character held in a cage, or -1.
    /// </summary>
    internal static int OccupantSlot(World world, Fixture f)
    {
        var table = world.Characters;
        if (FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s) || table.Cold(s) is not { Medical: { Dead: false } })
            return -1;

        return s;
    }

    /// <summary>
    /// The town the kill and eat goals read: the town the worker stands in (the character status's town, `+0x88`: the kill rate's gate and its timer
    /// key, <b>Verified</b> `FUN_14032e9b0`), else its squad's town (<b>engine reading</b>); -1: none.
    /// </summary>
    internal int FateTown(World world, int slot, Blackboard board) => TownAt(data, world.Characters.Next[slot].Position) is >= 0 and var here ? here
            : board.Town;

    /// <summary>
    /// The cage whose occupant a killer or eater takes (<c>FUN_1408d0d90</c> over the cage finder <c>FUN_1408d0770</c> in its occupied mode,
    /// <b>Verified</b>): <see cref="FixtureRegistry.FindCage"/> with <c>occupied</c>, over the candidates of the worker's town
    /// (<see cref="FixtureRegistry.CageCandidates"/>: its town, else its faction's nearest, plus its home's) with the faction and usable tests of the
    /// finder (a cage of another faction only when <see cref="FixtureRegistry.CanUse"/> passes) and the weights clamp(1 - d^2 / 4608^2, 1e-5, 1),
    /// x100 own faction, x100 in the worker's home. Not one whose occupant is dead or another job's. A robot occupant (RACE <c>robot</c>) does not
    /// leave the cage out: the finder wrapper tests it only on the best cage, and then there is no target this ask (<c>FUN_1408d0d90</c>,
    /// <b>Verified</b>). The wrapper remembers its choice (<see cref="AiMind.FateCage"/>, stored before the robot test): while the remembered cage
    /// holds a captive it is returned as it is (no weighting, no robot test, <paramref name="fresh"/> false: no event 10); once it holds none the
    /// memory is cleared and the search runs again. <b>Engine choice</b>: a dead occupant counts as none (the jobs work live captives only), and a
    /// remembered cage another job works is no target this ask.
    /// </summary>
    internal Fixture? FateTarget(World world, int slot, CharacterCold cold, string? home, out bool fresh, bool remember = true)
    {
        fresh = false;
        var table = world.Characters;
        var mind = cold.Ai;
        if (mind?.FateCage is { } id)
        {
            if (world.Fixtures.Find(id) is { } kept && OccupantSlot(world, kept) is >= 0 and var held)
                return Taken(kept.Id) || Busy(table.IdOf(held)) ? null : kept;

            if (remember) mind.FateCage = null;
        }

        var candidate = world.Fixtures.CageCandidates(world, data, cold, table.Next[slot].Position);
        var best = world.Fixtures.FindCage(world, cold, table.Next[slot].Position, home,
            f => !candidate(f) || Taken(f.Id) || OccupantSlot(world, f) is not (>= 0 and var s) || Busy(table.IdOf(s)), occupied: true);
        if (best is null) return null;
        if (mind is not null && remember) mind.FateCage = best.Id;
        fresh = true;
        return table.Cold(table.TryResolveNext(best.Occupant, out int o) ? o : slot)!.Race is { IsRobot: true } ? null : best;
    }

    /// <summary>
    /// The state CAGES_ARE_ALL_FULL (<c>FUN_1405a1ab0</c>, <c>FUN_1408cbea0</c> mask 0x100, <c>FUN_140a00690</c>): every cage of the character's town
    /// and of its squad's home town that <see cref="FixtureRegistry.CanUse"/> (<c>canIUseThisBuilding</c>) passes for it holds a live character, and
    /// there is at least one.
    /// </summary>
    public bool CagesFull(World world, int site, CharacterCold who) => CagesFull(world, data, site, who);

    /// <summary>
    /// Whether the cages of the town are full for <paramref name="who"/> (<see cref="CagesFull(World, int, CharacterCold)"/>).
    /// </summary>
    public static bool CagesFull(World world, PopulationData data, int site, CharacterCold who)
    {
        int home = world.Squads.Find(who.SquadId)?.Town ?? -1;
        bool any = false;
        foreach (int town in home >= 0 && home != site ? new[] { site, home } : [site])
        {
            foreach (var f in CagesOf(world, data, town))
            {
                if (!world.Fixtures.CanUse(world, who, f)) continue;
                if (OccupantSlot(world, f) < 0) return false;
                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// The kill rate (AI slot 20, <c>FUN_14032e9b0</c>): the town's timer has run out, or all the faction's cages there are full. The first ask sets
    /// the timer.
    /// </summary>
    internal bool KillDue(World world, int site, CharacterCold who)
    {
        if (!_killAt.TryGetValue(site, out double at)) _killAt[site] = at = NextKill(world, site);
        return world.GameHours >= at || CagesFull(world, site, who);
    }

    /// <summary>
    /// now + uniform[1, 15) game hours (<c>FUN_14034a810</c>).
    /// </summary>
    double NextKill(World world, int site) =>
        world.GameHours + 1 + 14 * Rng.Float(Rng.Hash(world.Seed, (ulong)(uint)site, RngPurpose.Slow, TimerSalt + (ulong)_timerRolls++));

    /// <summary>
    /// A captive a slaver processes (<c>FUN_1408cc110</c> for 260, <c>FUN_1408cd630</c> for 183, through <c>BuildingFinder_SlavesToProcess</c>,
    /// <b>Observed</b>): a cage of the town whose owner is not the player's faction and that the worker may use
    /// (<c>Ownerships::canIUseThisBuilding</c>, <see cref="Locks.FixtureRegistry.CanUse(World, CharacterCold, Locks.Fixture)"/>: its own faction, a
    /// non-enemy cage outdoors in its home town, one indoors in a public building, else an ally; <c>FUN_1408db510</c>), holding a live character no
    /// other job works. 260 takes an awake occupant that is no slave; 183 one on the worker's floor and in its building (or both outdoors) within
    /// <see cref="CaptiveSettings.WatchRange"/> in 3D, whose owner (if any) is an ally and that is not a slave of an owner that still looks one
    /// (<c>FUN_1408ca3d0</c>; its flag <c>0x3e0</c> is taken as slave state 1, <b>Unknown</b>). With <see cref="CaptiveSettings.SpareOwnFaction"/>
    /// neither takes an occupant of the worker's own faction that is no slave.
    /// </summary>
    internal Fixture? ProcessTarget(World world, int slot, CharacterCold cold, int site, bool watching)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        foreach (var f in CagesOf(world, site))
        {
            if (f.Owner < 0 || f.Owner == world.Player.Faction || !world.Fixtures.CanUse(world, cold, f) || Taken(f.Id))
                continue;

            if (OccupantSlot(world, f) is not (>= 0 and var s) || Busy(table.IdOf(s))) continue;
            var c = table.Cold(s)!;
            if (Spared(cold, c)) continue;

            if (watching)
            {
                // The cage must be on the watcher's floor and in its building (two outdoor parties have the same, null, handle) and within 180 in 3D
                // (FUN_1408cd630).
                if (world.Sight.FloorAt(f.Building, f.Position) != world.Sight.FloorOf(slot, at)
                        || (world.Sight.HasRoom(f.Building) ? f.Building : null) != world.Sight.BuildingOf(slot)
                    || Vector3.DistanceSquared(at, f.Position) > Settings.WatchRange * Settings.WatchRange) continue;
                // A slave of an allied owner that still looks one is left alone, as is any of an owner who is no ally; the rest are processed
                // (FUN_1408ca3d0).
                if (!NeedsProcessing(cold, c)) continue;
            }
            else if (c.SlaveState != 0 || c.Medical is { Incapacitated: true }) continue;
            return f;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="worker"/> leaves caged <paramref name="c"/> alone: <see cref="CaptiveSettings.SpareOwnFaction"/> is on and it is of
    /// the worker's own faction and no slave.
    /// </summary>
    bool Spared(CharacterCold worker, CharacterCold c) => Settings.SpareOwnFaction && c.SlaveState == 0 && c.Faction == worker.Faction;

    /// <summary>
    /// The first pass of the watchers' finder (183, <c>FUN_1408cd630</c>, <b>Verified</b>: it walks the watcher's sensed list, masks 0x60 / 0x110):
    /// the nearest (engine choice; the original takes the first) live character within sight range that the watcher senses
    /// (<see cref="CharacterFacts.Perceives"/>: one it has not seen or heard, a sneaking one or one in another building, is none, #649), in slave
    /// state 1 or 2, free (not carried, caged or held), no animal, not another job's, and <see cref="NeedsProcessing"/>. No distance
    /// bound, no cage. -1: none.
    /// </summary>
    internal int FreeSlaveToProcess(World world, int slot, CharacterCold me)
    {
        var scan = new FreeScan { System = this, World = world, Self = slot, Me = me, Best = -1, BestD2 = float.MaxValue };
        var p = world.Characters.Next[slot].Position;
        world.Grid.Query(p.X, p.Z, AggroSystem.RangeOf(me, AggroSystem.OutdoorSight), ref scan, static (int v, ref FreeScan st) =>
        {
            var table = st.World.Characters;
            if (v == st.Self || !table.Next[v].Alive || table.Cold(v) is not { SlaveState: not 0, Medical: { Dead: false }, Race: not null } c
                    || !c.CarriedBy.IsNone || c.InSomething != HeldState.None
                || c.Fighter is { IsAnimal: true } || c.Appearance?.Animal is not null) return;
            var id = table.IdOf(v);
            if (st.System.Busy(id) || !st.System.NeedsProcessing(st.Me, c)) return;

            if (!CharacterFacts.Perceives(st.World, st.Self, v)) return;

            float d2 = Vector3.DistanceSquared(table.Next[st.Self].Position, table.Next[v].Position);
            if (d2 < st.BestD2 || d2 == st.BestD2 && v < st.Best) (st.Best, st.BestD2) = (v, d2);
        });
        return scan.Best;
    }

    /// <summary>
    /// The state of <see cref="FreeSlaveToProcess"/>'s scan: the watcher, and the nearest slave found so far.
    /// </summary>
    struct FreeScan
    {
        /// <summary>
        /// The system scanning.
        /// </summary>
        public CaptiveSystem System;
        /// <summary>
        /// The world.
        /// </summary>
        public World World;
        /// <summary>
        /// The watcher's slot.
        /// </summary>
        public int Self;
        /// <summary>
        /// The watcher.
        /// </summary>
        public CharacterCold Me;
        /// <summary>
        /// The nearest slave so far (-1: none).
        /// </summary>
        public int Best;
        /// <summary>
        /// Its squared distance.
        /// </summary>
        public float BestD2;
    }

    /// <summary>
    /// The watchers' needs-processing test (<c>FUN_1408ca3d0</c>, <b>Verified</b>) for a live character that is no animal and not carried: one
    /// without an owner needs it; one whose owner is no ally of the watcher does not; one of an allied owner does when it is an escaped or ex-slave
    /// (state 2, 3), and a slave unless its head is shaven (Character vslot 124 <c>0x3e0</c>, <c>CharacterHuman::isHeadShaven</c>) and it looks like
    /// a slave (<see cref="LooksLikeASlave"/>). So a slave once processed, shaven and in its kit, is left alone. Its other test, that no other member
    /// of the platoon works the target (<c>FUN_1402690e0</c>), is the callers' <c>Busy</c>.
    /// </summary>
    bool NeedsProcessing(CharacterCold watcher, CharacterCold c) =>
        c.SlaveOwner < 0 || Allied(watcher.Faction, c.SlaveOwner) && (c.SlaveState is 2 or 3 || !c.Shaved || !LooksLikeASlave(watcher, c));

    /// <summary>
    /// The looks-like-a-slave test with the watcher's faction (<c>FUN_1405a6bb0</c>, <b>Verified</b>, docs/game/ai-tasks.md "Looks like a slave"): an
    /// animal passes and a law enforcer or a diplomat (NPC class 1, 5) fails; without the faction's <c>allow slaves weapons</c> a weapon in hand or
    /// in a weapon section fails it; the armour and legs sections must each hold nothing, a slave garment (<c>stigma</c> 6) or one of the faction's
    /// <c>slave clothing</c>; then the boots section passes with shackles in it, and empty only with shackles in the armour section on a shaven head,
    /// locked or not (<see cref="LockSystem.IsShackles"/>).
    /// </summary>
    internal bool LooksLikeASlave(CharacterCold watcher, CharacterCold c)
    {
        if (c.Fighter is { IsAnimal: true }) return true;
        if (c.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Diplomat) return false;
        var faction = (uint)watcher.Faction < (uint)data.Factions.Count ? data.Factions[watcher.Faction] : null;
        if (faction is not { AllowSlavesWeapons: true } && !CharacterFacts.TargetUnarmed(c)) return false;

        string[] clothing = SlaveClothing(watcher.Faction);
        var armour = c.Inventory?.Items.Find(i => i.Section == "armour");
        if (!SlaveWear(armour, clothing) || !SlaveWear(c.Inventory?.Items.Find(i => i.Section == "legs"), clothing))
            return false;

        return c.Inventory?.Items.Find(i => i.Section == "boots") is { } boots ? LockSystem.IsShackles(boots) : LockSystem.IsShackles(armour) && c.Shaved;
    }

    /// <summary>
    /// An armour or legs piece a slave may wear: none, a slave garment (<c>stigma</c> 6) or one of the watcher's faction's <c>slave clothing</c>.
    /// </summary>
    bool SlaveWear(ItemInstance? item, string[] clothing) =>
        item is null || data.Db.Find(item.Record)?.GetInt("stigma", 0) == SlaveStigma || clothing.Contains(item.Record);

    /// <summary>
    /// The cage a liberator opens (276, <c>BuildingFinder_CagesToLiberate</c> <c>FUN_1408cf140</c>, <b>Verified</b> by decompile): of the cages in
    /// the 3 x 3 zone cells round the worker (<c>FUN_1409fc450</c>, <see cref="Data.World.WorldLayout.WithinOneZone"/>) whose town is its status
    /// town (<c>+0x88</c>, <see cref="AiSystem.StatusTownAt"/> as its next update writes it; a player outpost or a nest too), of any town when it has
    /// none (<c>FUN_1409fb880</c>, a null town passes every building; the cage's town as <see cref="CageTown"/>), the first in list order that
    /// <see cref="Liberable"/> passes; no distance test. The order is Meitou's (<b>engine choice</b>): the registry's, which is that of the towns'
    /// registration and the player's buildings' (built, or read from a save); the original's is its loaded-zone map's, then each zone's
    /// <c>set&lt;Building*&gt;</c> by address. Null: none (the finder's 0).
    /// </summary>
    internal Fixture? LiberateTarget(World world, int slot, CharacterCold cold)
    {
        var at = world.Characters.Next[slot].Position;
        var town = AiSystem.StatusTownAt(data, world, at, cold.StatusTown);
        if (town.Site >= 0 && !world.Fixtures.IsRegistered(town.Site)) world.Fixtures.RegisterSite(world, data, town.Site);

        var asker = world.Characters.IdOf(slot);
        var goals = SquadGoals.Of(world);
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || !Data.World.WorldLayout.WithinOneZone(at.X, at.Z, f.Position.X, f.Position.Z)) continue;
            if (!town.IsNone && CageTown(world, f) != town) continue;
            if (Liberable(world, asker, cold, f, goals)) return f;
        }

        return null;
    }

    /// <summary>
    /// Whether a liberator frees the cage's occupant (<c>FUN_1408db880</c>, <c>FUN_1408cf140</c>, <b>Verified</b> by decompile): a cage whose
    /// faction fails the relation test <c>FUN_1406b2630</c> (neither the worker's nor allied to it; a cage of no faction fails it too), not on
    /// the worker's path-impossible list (<c>FUN_1405b0290</c>, its lost walks), holding a live occupant that is not the worker's enemy, that its
    /// squad's long-term tags do not mark hated (<c>FUN_1405995d0</c>, & 0x2b0) and that no squad mate works (<c>FUN_1402690e0</c> on the squad's
    /// own blackboard; another squad's liberator may head for the same captive).
    /// </summary>
    bool Liberable(World world, CharacterId asker, CharacterCold cold, Fixture f, SquadGoals goals)
    {
        var table = world.Characters;
        if (f.Owner >= 0 && (f.Owner == cold.Faction || Allied(cold.Faction, f.Owner))) return false;
        if (cold.IsIgnored(new IgnoreKey(f.Id, default), world.Tick)) return false;
        if (OccupantSlot(world, f) is not (>= 0 and var s)) return false;

        var occupant = table.IdOf(s);
        if (data.Relations.Contains(cold.Faction) && AggroSystem.IsEnemy(data.Relations, cold.Faction, table.Cold(s)!.Faction)) return false;
        if (Hates(world, cold, occupant)) return false;

        return goals.Working(cold.SquadId, asker, GoalTarget.Of(occupant)) == 0;
    }

    /// <summary>
    /// Whether the squad's long-term tags on <paramref name="who"/> mark it hated (<c>FUN_1405995d0</c>: STOLE_FROM_ME, MY_CAPTOR,
    /// DEFEATED_MY_SQUAD_ONCE or KILLED_MY_FRIEND).
    /// </summary>
    bool Hates(World world, CharacterCold cold, CharacterId who)
    {
        var memory = (_talk ??= world.SystemOf<Dialogue.DialogueSystem>())?.Memory.Find(world, cold.SquadId, who);
        return (memory?.Long & Dialogue.MemoryTag.HateMask) is > 0;
    }

    /// <summary>
    /// Whether a squad mate of the liberator works <paramref name="occupant"/> (276's start, as its finder's test).
    /// </summary>
    internal bool LiberateClaimed(World world, CharacterId asker, CharacterCold cold, CharacterId occupant) =>
        SquadGoals.Of(world).Working(cold.SquadId, asker, GoalTarget.Of(occupant)) > 0;

    /// <summary>
    /// The town a cage's building belongs to (<c>FUN_1400f6c20</c>, <see cref="Construction.TownLookup.KeyOf"/>): a placed town's cage its town, a
    /// nest camp's its nest, a player-built one its outpost or the placed town its key names (by handle, else the nearest town); none when Meitou
    /// cannot name it (a level building's <see cref="Construction.TownLookup.LevelOwned"/>).
    /// </summary>
    static CampaignTown CageTown(World world, Fixture f)
    {
        if (f.Site >= 0) return CampaignTown.OfSite(f.Site);
        if (f.Wild >= 0) return CampaignTown.OfNest(f.Wild);
        if (world.Buildings.Find(f.Building) is not { } site) return CampaignTown.None;

        int key = Construction.TownLookup.KeyOf(world, site);
        if (key > 0) return CampaignTown.OfPlayer(key);

        return key is < 0 and not Construction.TownLookup.LevelOwned ? CampaignTown.OfSite(-key - 1) : CampaignTown.None;
    }

    /// <summary>
    /// A character's "looks like a slave" level in 0..1 (status object +0xf4, shown by the inventory window's data panel and the status tooltip;
    /// <c>FUN_1405a51e0</c>, <b>Observed</b>, docs/game/ui-screens.md): a sum over its worn kit: the hat section empty 0.4 (when a character flag,
    /// <b>Unknown</b>, holds; taken as true), the body section empty 0.1 or holding a locked piece 1, the legs section empty 0.1, a locked piece 1 or
    /// the owner's slave clothing 0.5, the boots section empty 0.1 or a locked piece 1 (a lock in any other section counts nothing). An escaped slave
    /// (state 2) doubles it and keeps at least 0.1, an ex-slave (3) keeps at least 0.01; at most 1. Sections by the item slot ids: 3 head, 5 body
    /// (armour), 6 legs, 9 boots.
    /// </summary>
    public float LooksLikeSlaveLevel(CharacterCold c)
    {
        if (c.Inventory is not { } inv) return 0;
        string[] clothing = (uint)c.SlaveOwner < (uint)(data.Factions?.Count ?? 0) && data.Db.Find(data.Factions![c.SlaveOwner].Id) is { } owner
            ? [.. owner.GetReferences("slave clothing").Select(l => l.TargetStringId)] : [];
        var armour = inv.Items.FirstOrDefault(i => i.Section == "armour");
        var legs = inv.Items.FirstOrDefault(i => i.Section == "legs");
        var boots = inv.Items.FirstOrDefault(i => i.Section == "boots");

        // A locked piece counts 1 in the armour, legs and boots sections only (nothing in the head or any other section).
        float level = inv.Items.Any(i => i.Section == "head") ? 0 : 0.4f;
        level += armour is null ? 0.1f : armour.LockedOn ? 1 : 0;
        level += legs is null ? 0.1f : legs.LockedOn ? 1 : clothing.Contains(legs.Record) ? 0.5f : 0;
        level += boots is null ? 0.1f : boots.LockedOn ? 1 : 0;
        if (c.SlaveState == 2) level = MathF.Max(level * 2, 0.1f);
        else if (c.SlaveState == 3) level = MathF.Max(level, ExSlaveFloor);   // the ex-slave's level is floored, not doubled (FUN_1405a51e0)
        return MathF.Min(level, 1);
    }

    /// <summary>
    /// Whether two factions are allied: the same, or allies in the relations.
    /// </summary>
    internal bool Allied(int a, int b) => a >= 0 && b >= 0
            && (a == b || data.Relations.Contains(a) && data.Relations.Contains(b) && data.Relations.IsAlly(a, b));

    #endregion

    #region One job

    /// <summary>
    /// One tick of a job; false when it is over.
    /// </summary>
    bool Step(World world, CaptiveJob j)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        j.Time += dt;

        // No job timer: a job ends by its own rule or when the chooser's goal no longer holds the worker (StepJobs).
        if (!table.TryResolveNext(j.Worker, out int w) || table.Cold(w) is not { } wc || wc.Medical is { Incapacitated: true } || wc.InCombat
            || wc.Fleeing || !wc.CarriedBy.IsNone)
        {
            return End(world, j, CaptiveEventKind.GaveUp);
        }

        return StepUncaged(world, j, w, wc) ?? StepAtCage(world, j, w, wc, dt);
    }

    /// <summary>
    /// One tick of a job that is not done at a cage (the logistics, the slave work, eating bodies or crops, worship, the dogs' games, processing a
    /// loose slave); null for a cage's job.
    /// </summary>
    bool? StepUncaged(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        if (j.Kind is CaptiveJobKind.Gather or CaptiveJobKind.Deliver or CaptiveJobKind.Sell or CaptiveJobKind.CageSelf)
            return StepLogistics(world, j, w, wc);

        if (j.Kind == CaptiveJobKind.Fetch) return wc.PickingUpItem == j.Item || End(world, j, null);
        if (j.Kind == CaptiveJobKind.LootFood) return StepLootFood(world, j, w);

        if (j.Kind is CaptiveJobKind.Capture or CaptiveJobKind.Carry or CaptiveJobKind.Shout)
            return StepSlaveWork(world, j, w, wc);

        if (j.Kind == CaptiveJobKind.EatBody) return StepEatBody(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.EatCrops) return StepEatCrops(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.Worship) return StepWorship(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.DogPlay) return StepPlay(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.DogChase) return StepChase(world, j, w);
        if (j.Kind == CaptiveJobKind.Process && j.Cage.Length == 0) return StepProcessLoose(world, j, w, wc);

        return null;
    }

    /// <summary>
    /// One tick of a job at a cage (kill, eat, liberate, process): over when the captive left the cage or died; the walk there, then facing the
    /// captive while it works.
    /// </summary>
    bool StepAtCage(World world, CaptiveJob j, int w, CharacterCold wc, float dt)
    {
        var table = world.Characters;
        var cage = world.Fixtures.Find(j.Cage);

        if (cage is null || !table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { } cc || cc.InWhat != cage.Id || !cc.IsImprisoned
            || cc.Medical is not { Dead: false })
        {
            return Over(world, j, table.TryResolveNext(j.Captive, out int gone) && table.Cold(gone)?.Medical is { Dead: true });
        }

        if (j.Phase == CaptivePhase.Walk)
        {
            if (!Walk(world, j, w, cage, out bool arrived)) return End(world, j, CaptiveEventKind.GaveUp);
            if (!arrived) return true;

            TaskStart.Stand(table, w);
            j.Phase = CaptivePhase.Work;
            j.Time = 0;
            wc.ActionClip = j.Kind switch { CaptiveJobKind.Kill => ChopClip, CaptiveJobKind.Eat => EatClip, _ => ProcessClip };
        }

        // Facing the captive while it works.
        var to = table.Next[c].Position - table.Next[w].Position;
        if (to.X * to.X + to.Z * to.Z > 1e-4f) table.Next[w].Yaw = MathF.Atan2(to.X, to.Z);

        return WorkAtCage(world, j, w, wc, c, cc, dt);
    }

    /// <summary>
    /// The work of a job at a cage, once there: the kill's blows, the eating, the release, the processing.
    /// </summary>
    bool WorkAtCage(World world, CaptiveJob j, int w, CharacterCold wc, int c, CharacterCold cc, float dt)
    {
        var table = world.Characters;

        switch (j.Kind)
        {
            case CaptiveJobKind.Kill:
                {
                    if (combat is null) return End(world, j, CaptiveEventKind.GaveUp);

                    // One blow per chop clip, at 0.55 of it.
                    if (j.Time < (j.Blows + Settings.BlowAt) * MathF.Max(ChopSeconds, 0.1f)) return true;
                    j.Blows++;
                    world.Defer(new Dialogue.DialogueEvent(j.Captive, j.Worker, ScreamingTorture) { Tick = world.Tick + 1 });
                    bool died = combat.Execute(world, w, c, j.Blows);
                    if (cc.Medical!.Incapacitated) TaskStart.Stand(table, c);
                    return died ? Over(world, j, true) : true;
                }
            case CaptiveJobKind.Eat:
                {
                    if (cc.Race is null || cc.Race.IsRobot) return End(world, j, CaptiveEventKind.GaveUp);
                    if (j.Time >= j.NextBark)
                    {
                        j.NextBark = j.Time + Settings.BarkSeconds;
                        world.Defer(new Dialogue.DialogueEvent(j.Worker, j.Captive, EatingSounds) { Tick = world.Tick + 1 });
                        world.Defer(new Dialogue.DialogueEvent(j.Captive, j.Worker, ScreamingTorture) { Tick = world.Tick + 1 });
                    }

                    return Eat(world, j, c, cc, dt) ? Over(world, j, true) : true;
                }
            case CaptiveJobKind.Liberate:
                {
                    // At once: Task_ReleasePrisoner frees the prisoner in its start and its update ends it (FUN_1403570a0 / FUN_140337c90, Verified).
                    world.Fixtures.Release(world, j.Captive);
                    Note(world, CaptiveEventKind.Liberated, j);
                    return End(world, j, null);
                }
            case CaptiveJobKind.Process:
                {
                    if (j.Time < Settings.ProcessSeconds) return true;
                    Process(world, w, wc, c, cc);
                    Note(world, CaptiveEventKind.Enslaved, j);
                    return End(world, j, null);
                }
        }

        return true;
    }

    /// <summary>
    /// A watcher's job on a slave out of a cage (<see cref="FreeSlaveToProcess"/>): walks to it, kneels <see cref="CaptiveSettings.ProcessSeconds"/>
    /// and processes it (<see cref="Process"/>). The job ends when the slave is carried off, caged, dead or no longer a slave; one that walks off
    /// during the kneel is walked after (engine choice).
    /// </summary>
    bool StepProcessLoose(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { Medical: { Dead: false }, SlaveState: not 0 } cc
                || !cc.CarriedBy.IsNone || cc.InSomething != HeldState.None)
            return End(world, j, CaptiveEventKind.GaveUp);

        ref var n = ref table.Next[w];
        var to = new Vector2(table.Next[c].Position.X, table.Next[c].Position.Z);
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), to);
        if (j.Phase == CaptivePhase.Work && d > Settings.Reach * 2)
            (j.Phase, wc.ActionClip) = (CaptivePhase.Walk, null);

        if (j.Phase == CaptivePhase.Walk)
        {
            bool walking = n.Task == (byte)CharacterTask.GoTo;
            if (d > Settings.Reach && !(!walking && j.Walks > 0 && d <= Settings.Slack))
            {
                if (walking && Vector2.Distance(n.Goal, to) <= 5) return true;
                if (!walking && Lost(j, IgnoreKey.Of(j.Captive))) return End(world, j, CaptiveEventKind.GaveUp);
                if (!walking && ++j.Walks > Settings.Walks) return End(world, j, CaptiveEventKind.GaveUp);

                TaskStart.Walk(world, w, to, SpeedMode.Walk, RowOf(j.Kind), TaskSource.Goal);
                return true;
            }

            TaskStart.Stand(table, w);
            j.Phase = CaptivePhase.Work;
            j.Time = 0;
            wc.ActionClip = ProcessClip;
        }

        var face = table.Next[c].Position - n.Position;
        if (face.X * face.X + face.Z * face.Z > 1e-4f) n.Yaw = MathF.Atan2(face.X, face.Z);
        if (j.Time < Settings.ProcessSeconds) return true;
        Process(world, w, wc, c, cc);
        Note(world, CaptiveEventKind.Enslaved, j);
        return End(world, j, null);
    }

    /// <summary>
    /// The walk to the cage: within its <c>use range</c> + the job's reach (<see cref="ReachOf"/>) it has arrived, or when a walk ended within
    /// <see cref="CaptiveSettings.Slack"/>; a walk that ended farther counts against <see cref="CaptiveSettings.Walks"/>. Its own faction's shut
    /// doors on the way are unlocked first (the lock rounds' "keys", engine choice). False: give up.
    /// </summary>
    bool Walk(World world, CaptiveJob j, int w, Fixture cage, out bool arrived)
    {
        var table = world.Characters;
        ref var n = ref table.Next[w];
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(cage.Position.X, cage.Position.Z));
        bool walking = WalksOwn(table, w, j);
        arrived = d <= cage.UseRange + ReachOf(j) || !walking && j.Walks > 0 && d <= Settings.Slack;
        if (arrived || walking) return true;
        if (Lost(j, new IgnoreKey(cage.Id, default))) return false;
        if (++j.Walks > Settings.Walks)
        {
            world.Fixtures.MarkUnreachable(world, cage, j.Faction);
            return false;
        }

        if (world.Fixtures.IsShut(cage.Building) && world.Fixtures.DoorOwner(cage.Building) == j.Faction)
            world.Fixtures.SetDoorsLocked(cage.Building, false);

        TaskStart.Walk(world, w, new Vector2(cage.Position.X, cage.Position.Z), SpeedMode.Walk, RowOf(j.Kind), j.Source);
        return true;
    }

    /// <summary>
    /// Whether a job holds <paramref name="id"/> as its captive. A plain loop: a lambda capturing the id made a closure per character the
    /// scan looked at.
    /// </summary>
    bool IsJobCaptive(CharacterId id)
    {
        foreach (var j in _jobs)
            if (j.Captive == id) return true;
        return false;
    }

    /// <summary>
    /// The captive died or is gone: a death resets the town's kill timer (<c>FUN_140353990</c>, the end of both the kill and the meal).
    /// </summary>
    bool Over(World world, CaptiveJob j, bool died)
    {
        if (died && j.Site >= 0) _killAt[j.Site] = NextKill(world, j.Site);
        return End(world, j, died ? j.Kind == CaptiveJobKind.Eat ? CaptiveEventKind.Eaten : CaptiveEventKind.Killed : CaptiveEventKind.GaveUp);
    }

    /// <summary>
    /// Ends a job: its clip goes (its goal's act-out is stopped by <see cref="DutyGoals.Done"/>).
    /// </summary>
    bool End(World world, CaptiveJob j, CaptiveEventKind? kind)
    {
        var table = world.Characters;
        if (table.TryResolveNext(j.Worker, out int w) && table.Cold(w) is { } wc) wc.ActionClip = null;

        if (kind is { } k) Note(world, k, j);
        return false;
    }

    /// <summary>
    /// Logs an event of the job (while <see cref="CaptiveSettings.RecordLog"/> is on).
    /// </summary>
    void Note(World world, CaptiveEventKind kind, CaptiveJob j)
    {
        if (Settings.RecordLog) _log.Add(new CaptiveEvent(world.Tick, kind, j.Kind, j.Worker, j.Captive));
    }

    #endregion

    #region Eating

    /// <summary>
    /// The amount of the toughness XP a meal still awake gains per update (<b>Observed</b>: 10, <c>FUN_1404356f0</c>).
    /// </summary>
    const float EatToughnessXp = 10;

    /// <summary>
    /// The salt of the crop eater's farm roll.
    /// </summary>
    const ulong CropSalt = 0x4372_6F70_4561_7421;

    /// <summary>
    /// How near the farm's origin an eater stops (engine choice: the original walks to the farm's work point, getPositionMarker).
    /// </summary>
    internal const float CropReach = 30;

    /// <summary>
    /// The eaters whose meal got up this tick, with it (<see cref="StepEatBody"/>), given order 35 after the job loop.
    /// </summary>
    readonly List<(CharacterId Eater, CharacterId Meal)> _risers = [];

    /// <summary>
    /// The meals of 230 and 233 (<c>AI::findCorpse_onGround</c> <c>FUN_1408cfde0</c> / <c>AI::findDeadOrKOBody_onGround</c> <c>FUN_1408ce680</c>,
    /// docs/game/ai-tasks.md), the nearest within the eater's sight (engine reading of its sensed list): 230 a dead body of a <c>carriable</c> race
    /// lying down, for an animal eater not a robot and not of its own race; 233 a dead or downed one not of an allied faction, not a robot and not of
    /// the eater's race. Not carried, not caged, and no other job's. -1: none.
    /// </summary>
    internal int BodyToEat(World world, int slot, CharacterCold cold, bool dead)
    {
        var table = world.Characters;
        var next = table.Next;
        var at = next[slot].Position;
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight), best = range * range;
        bool animal = cold.Fighter is { IsAnimal: true } || cold.Appearance?.Animal is not null;
        int found = -1;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !next[s].Alive || table.Cold(s) is not { Medical: { } m, Race: { } race } body || !body.CarriedBy.IsNone
                    || body.InSomething != HeldState.None)
                continue;

            float d2 = Vector3.DistanceSquared(at, next[s].Position);
            if (d2 > best) continue;
            if (dead)
            {
                if (!m.Dead || !race.Carriable || animal && (race.IsRobot || race.StringId == cold.Race?.StringId))
                    continue;
            }
            else if (!m.Incapacitated || race.IsRobot || race.StringId == cold.Race?.StringId || Allied(cold.Faction, body.Faction))
                continue;
            if (IsJobCaptive(table.IdOf(s))) continue;
            (found, best) = (s, d2);
        }

        return found;
    }

    /// <summary>
    /// A meal on the ground (220 on a 230 / 233 target): the eater walks to the body, then eats it with the <c>eat</c> clip as a caged one is eaten
    /// (<see cref="Eat"/>), barking event 52. The action has no dead test (<c>FUN_140353c50</c>, <b>Verified</b>): a corpse is bitten like a living
    /// body until its parts are gone and the passes run out, when it is removed; a living body until it dies. Carried away, caged or gone: over. A
    /// meal getting up (not a robot) is bitten once more, then the eater is ordered 35 Running away on it (<see cref="RunFrom"/>).
    /// </summary>
    bool StepEatBody(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { Medical: { } m, Race: { } race } cc || !cc.CarriedBy.IsNone
                || cc.InSomething != HeldState.None)
            return End(world, j, CaptiveEventKind.GaveUp);

        bool rising = !m.Dead && j.Phase == CaptivePhase.Work && !race.IsRobot && CharacterFacts.GettingUp(cc, world.Tick);
        if (!m.Dead && !m.Incapacitated && !rising) return End(world, j, CaptiveEventKind.GaveUp);

        ref var n = ref table.Next[w];
        var body = table.Next[c].Position;
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(body.X, body.Z));
        if (j.Phase == CaptivePhase.Walk)
        {
            bool walking = WalksOwn(table, w, j);
            if (d > ReachOf(j) && !(!walking && j.Walks > 0 && d <= Settings.Slack))
            {
                if (walking) return true;
                if (Lost(j, IgnoreKey.Of(j.Captive)) || ++j.Walks > Settings.Walks)
                    return End(world, j, CaptiveEventKind.GaveUp);
                TaskStart.Walk(world, w, new Vector2(body.X, body.Z), SpeedMode.Run, RowOf(j.Kind), j.Source);
                return true;
            }

            TaskStart.Stand(table, w);
            j.Phase = CaptivePhase.Work;
            j.Time = 0;
            wc.DrawnWeapon = WeaponKinds.None;   // 220's B list: !hasWeaponEquipped
            wc.ActionClip = EatClip;
        }

        var to = body - n.Position;
        if (to.X * to.X + to.Z * to.Z > 1e-4f) n.Yaw = MathF.Atan2(to.X, to.Z);
        if (j.Time >= j.NextBark)
        {
            j.NextBark = j.Time + Settings.BarkSeconds;
            world.Defer(new Dialogue.DialogueEvent(j.Worker, j.Captive, EatingSounds) { Tick = world.Tick + 1 });
        }

        bool corpse = m.Dead;
        bool eaten = Eat(world, j, c, cc, world.TickSeconds);
        if (rising && !m.Dead) return RunFrom(world, j);
        if (!eaten) return true;
        bool over = End(world, j, CaptiveEventKind.Eaten);

        // The meal over, the original destroys the character with the reason "eaten" (FUN_140799af0): the corpse goes at once, as the body system
        // removes a decayed one.
        if (corpse)
        {
            var id = table.IdOf(c);
            world.Uniques.Left(id);
            table.Remove(id);
        }

        return over;
    }

    /// <summary>
    /// <c>Task_EatPrisoner</c>'s update (<c>FUN_140353c50</c>, <b>Verified</b>) on a meal that is not a robot, not in a cage and getting up: after
    /// the bite the eater is given 35 on it, its queue cleared. The job ends here; the order is given after the job loop, as its clear lets every
    /// duty go.
    /// </summary>
    bool RunFrom(World world, CaptiveJob j)
    {
        _risers.Add((j.Worker, j.Captive));
        return End(world, j, CaptiveEventKind.GaveUp);
    }

    /// <summary>
    /// Gives each eater of <see cref="_risers"/> still in the world order 35 on its meal (serial, after the job loop).
    /// </summary>
    void OrderRunsFromRisers(World world)
    {
        if (_risers.Count == 0) return;

        var flee = world.SystemOf<FleeSystem>();
        foreach (var (eater, meal) in _risers)
        {
            if (flee is not null && world.Characters.TryResolveNext(eater, out int s)) flee.GiveRun(world, s, meal);
        }

        _risers.Clear();
    }

    /// <summary>
    /// The finder of 232 (<c>FUN_1408cb810</c>, docs/game/ai-tasks.md "Eating crops"): in the town the eater stands in, whose faction it does not get
    /// along with (engine reading of the coexist test: an enemy by the relations), a farm with food
    /// (<see cref="Production.ProductionSystem.HasFood"/>) drawn uniformly at random. Only a player town's are looked at: the towns' farms are
    /// machines too (<c>ProductionSystem.TownMachines</c>), but their eaters are <b>not modelled</b>. Null: none.
    /// </summary>
    internal string? CropsToEat(World world, int slot, CharacterCold cold, Blackboard board)
    {
        var town = AiSystem.TownAt(data, world, world.Characters.Next[slot].Position);
        if (town.PlayerTown <= 0 || world.Player.Faction < 0 || !data.Relations.Contains(cold.Faction)
                || !AggroSystem.IsEnemy(data.Relations, cold.Faction, world.Player.Faction))
            return null;

        List<string>? farms = null;
        foreach (var m in world.Machines.All)
        {
            if (m.Farm is { } f && Production.ProductionSystem.HasFood(f) && world.Buildings.Find(m.Site) is { Complete: true } site
                    && site.Town == town.PlayerTown
                && !_jobs.Any(o => o.Kind == CaptiveJobKind.EatCrops && o.Cage == m.Site))
                (farms ??= []).Add(m.Site);
        }

        if (farms is null) return null;
        return farms[Rng.Int(Rng.Hash(world.Seed, (ulong)Rng.Key(world.Characters.IdOf(slot)) ^ (ulong)world.Tick, RngPurpose.Slow, CropSalt),
                farms.Count)];
    }

    /// <summary>
    /// <c>Task_EatingCrops</c> (231): the eater walks to the farm, then each tick plays <c>eat</c>, is reported for crop theft (crime 14) against the
    /// farm's faction and eats (<see cref="Production.ProductionSystem.EatCrop"/>, one plant per game second). It stops when the farm has no food
    /// left (the original's B list, 143) or the job grows old.
    /// </summary>
    bool StepEatCrops(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        if (world.Machines.Find(j.Cage) is not { Farm: { } farm } || !Production.ProductionSystem.HasFood(farm)
                || world.Buildings.Find(j.Cage) is not { } site)
            return End(world, j, null);

        ref var n = ref table.Next[w];
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(site.Position.X, site.Position.Z));
        if (j.Phase == CaptivePhase.Walk)
        {
            bool walking = WalksOwn(table, w, j);
            if (d > Settings.Reach + CropReach && !(!walking && j.Walks > 0 && d <= Settings.Slack + CropReach))
            {
                if (walking) return true;
                if (Lost(j, new IgnoreKey(j.Cage, default)) || ++j.Walks > Settings.Walks)
                    return End(world, j, CaptiveEventKind.GaveUp);
                TaskStart.Walk(world, w, new Vector2(site.Position.X, site.Position.Z), SpeedMode.Run, RowOf(j.Kind), TaskSource.Goal);
                return true;
            }

            TaskStart.Stand(table, w);
            j.Phase = CaptivePhase.Work;
            j.Time = 0;
            wc.ActionClip = EatClip;
        }

        world.SystemOf<Crime.CrimeSystem>()?.SetPending(world, w, Crime.CrimeCode.CropTheft, world.Player.Faction, CharacterId.None,
                Crime.CrimeSystem.MinTimer);
        if (!Production.ProductionSystem.EatCrop(farm, world.TickSeconds)) return End(world, j, null);
        world.Machines.Touch();
        return true;
    }

    /// <summary>
    /// The medical context of a captive the rack or a meal damages (toughness, strength, the seed and a per-character roll key).
    /// </summary>
    MedicalContext ContextOf(World world, int c, CharacterCold cc) => new(data.Bodies.Constants, data.BodyOptions, cc.Race!)
    {
        Toughness = cc.Stats?.ScaledToughness ?? 0,
        Strength = cc.Stats?.Strength ?? 50,
        HasStats = cc.Stats is not null,
        Seed = world.Seed,
        CharacterKey = Rng.Key(world.Characters.IdOf(c)),
    };

    /// <summary>
    /// What a medical update of a captive does outside the medical state: a death or knock-out stands it up, a lost limb is announced to the renderer
    /// and the character cries event 70 (leg) or 71 (arm; none for a part of a type outside the four,
    /// <see cref="Data.Gameplay.Dialogue.DialogueEvents.LostLimb"/>). The limb's item drops when <paramref name="limbDrops"/>: the rack's does
    /// (<c>FUN_14029af90</c> severs with the item flag 1), an eaten one's does not (<c>FUN_140650370</c>: 0), <b>Verified</b> by decompile.
    /// </summary>
    static void ApplyBodyEvents(World world, CharacterId captive, int c, MedicalState medical, List<MedicalEvent> events, bool limbDrops)
    {
        foreach (var e in events)
        {
            if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut) TaskStart.Stand(world.Characters, c);
            else if (e.Kind == MedicalEventKind.PartSevered)
            {
                var part = medical.Parts[e.Part].Template;
                world.Characters.LimbsChanged.Enqueue((c, part.LimbSlot, limbDrops));
                if (Data.Gameplay.Dialogue.DialogueEvents.LostLimb(part) is { } cry)
                    world.Defer(new Dialogue.DialogueEvent(captive, CharacterId.None, cry) { Tick = world.Tick + 1 });
            }
        }
    }

    /// <summary>
    /// One update of a meal (<see cref="MedicalState.BeingEaten"/>, at the captive's own rate); true when the captive died of it or otherwise.
    /// </summary>
    bool Eat(World world, CaptiveJob j, int c, CharacterCold cc, float dt)
    {
        var table = world.Characters;
        var medical = cc.Medical!;
        var ctx = ContextOf(world, c, cc);
        bool wasDead = medical.Dead;
        var events = new List<MedicalEvent>();

        // A meal still awake gains toughness XP: the generic continuous entry with amount 10 (FUN_1404356f0 calls FUN_1408c6430(stats, dt, 10, 21);
        // Observed). Engine choice: "awake" for the
        // original's unnamed conscious test (vfunc 0x358).
        if (cc.Stats is { } stats && !medical.Incapacitated)
            _xp.Continuous(stats, cc.Race!, StatsEnumerated.Toughness, dt, EatToughnessXp, ExperienceSource.Injury);

        float rate = MedicalState.EatRate(cc.Stats?.Toughness ?? 0, cc.Stats?.Scale ?? 1);
        bool vampiric = table.TryResolveNext(j.Worker, out int w) && table.Cold(w)?.Race is { Vampiric: true };

        // Each bite also sets playerWantsMeToGetUp (Character +0xec, FUN_140650370, Verified): a victim lying on, playing dead, gets up (staying low
        // when sneaking, docs/game/ragdolls.md "Staying low").
        if (medical.PlayingDead) medical.StandUp(Orders.Has(cc, CharacterOrders.Sneak));
        HealthObservers.Before(world, c);
        bool eaten = medical.BeingEaten(rate, dt, ctx, data.BodyOptions.Dismemberment != Dismemberment.Never, events, vampiric);
        HealthObservers.After(world, c, HealthChangeKind.Eaten);
        ApplyBodyEvents(world, j.Captive, c, medical, events, limbDrops: false);
        if (!wasDead) DeathObservers.Report(world, c, j.Worker, DeathSite.Eaten);   // the eater's bite killed it

        // "{1} has been eaten alive." for the player's own (FUN_140856bc0: of the player's faction, or of a squad that is).
        if (eaten && (cc.IsPlayer || world.Player.Faction >= 0 && cc.Faction == world.Player.Faction))
            world.Post(Loc.Tr("{1} has been eaten alive.", world.ShownName(cc)));

        return wasDead ? eaten : medical.Dead;
    }

    #endregion

    #region Processing

    /// <summary>
    /// The record types a new slave is stripped of, in the original's order.
    /// </summary>
    static readonly FcsRecordType[] Stripped = [FcsRecordType.Weapon, FcsRecordType.Armour, FcsRecordType.Item, FcsRecordType.Container];

    /// <summary>
    /// The ARMOUR <c>stigma</c> of slave garments, which processing leaves on.
    /// </summary>
    const int SlaveStigma = 6;

    /// <summary>
    /// The processing of a new slave once the kneel is over (<c>Task_ProcessNewSlave</c>, <b>Verified</b>): it is stripped (<see cref="Strip"/>) and,
    /// only when its legs slot is empty then, dressed in each of the worker's faction's <c>slave clothing</c> it does not wear already. Event 35, or
    /// 37 for one whose head is shaven already, from the worker to it, and its head is shaven; it becomes the worker's faction's slave unless it has
    /// an owner already (it keeps that one), and its one-shot orders and perma-jobs are cleared. A captive without locked shackles is cuffed first
    /// (<see cref="Restrain"/>).
    /// </summary>
    void Process(World world, int w, CharacterCold wc, int c, CharacterCold cc)
    {
        var table = world.Characters;
        Restrain(world, wc, cc);
        if (cc.Inventory is { } inv)
        {
            string[] clothing = SlaveClothing(wc.Faction);
            Strip(world, table.Next[w].Position, inv, wc, StolenMark.Of(cc, table.IdOf(c)), clothing);
            if (!inv.Items.Any(i => i.Section == "legs")) Dress(inv, cc, clothing);
            _assembly.RefreshEquipment(cc);
        }

        _assembly.RefreshEquipment(wc);

        // Event 37 for a captive whose head is shaven already, a slave processed before, else 35 (FUN_14035aa20, Verified: Character vslot 124
        // isHeadShaven).
        world.Defer(new Dialogue.DialogueEvent(table.IdOf(w), table.IdOf(c), cc.Shaved ? RecapturedSlave : IntroducingNewSlave) { Tick = world.Tick
                + 1 });

        // The owner is set only when it had none that resolves (FUN_14032e430 behind FUN_14035aa20): a recaptured slave keeps its owner and its
        // master squad. Meitou's owner is a faction, and the master squad (runtime only) stands for the handle when it is known.
        bool owned = cc.SlaveOwner >= 0 && (cc.SlaveMaster < 0 || world.Squads.Find(cc.SlaveMaster) is not null);
        if (owned) FixtureRegistry.Enslave(world, cc, cc.SlaveOwner);
        else FixtureRegistry.Enslave(world, cc, wc.Faction, wc.SquadId);

        // Then its orders object is reset: the one-shot orders and the perma-jobs are destroyed (FUN_140507530, FUN_140507280) and job 187 is added
        // toward the owner (a player character's list; an NPC holds it in effect; both obey through AiSystem.ObeyedRoles).
        ObedientSlave.Reset(world, cc);

        // Then its head is shaven (CharacterHuman::shaveHead(true) in FUN_14035aa20, Verified); an animal has no head to shave.
        if (cc.Fighter is not { IsAnimal: true }) cc.Shaved = true;
        _assembly.MatchShave(cc);
        world.SystemOf<Dialogue.DialogueSystem>()?.Forgive(world, w, table.IdOf(c));
    }

    /// <summary>
    /// The plan's cuff before <see cref="Process"/>: 182 needs 134 <c>isChained</c>, which only 166 Restraining (<c>Task_CuffTarget</c>) gives, and
    /// its <c>setChainedMode</c> makes the cuffer the owner. So a human captive without locked shackles is chained
    /// (<see cref="CharacterAssembly.Chain"/>) and becomes a slave of the worker's faction and squad, whose medics then treat it in its cage
    /// (<c>FirstAidSystem.ChainedTo</c>). 166 also needs 7 <c>isTargetDown</c>, which Meitou does not ask of a caged or loose slave (<b>engine
    /// choice</b>: unchained, it never looks like a slave and was processed again forever, #508, #509).
    /// </summary>
    void Restrain(World world, CharacterCold wc, CharacterCold cc)
    {
        if (cc.Fighter is { IsAnimal: true } || LockSystem.WornShackles(cc) is not null) return;

        _assembly.Chain(cc);
        FixtureRegistry.Enslave(world, cc, wc.Faction, wc.SquadId);
    }

    /// <summary>
    /// The records of a faction's <c>slave clothing</c> (none for no faction).
    /// </summary>
    string[] SlaveClothing(int faction) =>
        (uint)faction < (uint)data.Factions.Count && data.Db.Find(data.Factions[faction].Id) is { } record
            ? [.. record.GetReferences("slave clothing").Select(l => l.TargetStringId)]
            : [];

    /// <summary>
    /// The strip of <see cref="Process"/> (<c>FUN_14035aa20</c>, <b>Verified</b>): every item of the whole inventory of record type WEAPON, then
    /// ARMOUR, ITEM and CONTAINER goes to the worker (<see cref="HandOver"/>), except locked ones and ARMOUR whose <c>stigma</c> is 6 (slave
    /// garments); a backpack goes with its content. The ARMOUR pass also leaves on the worker's faction's <c>slave clothing</c>
    /// (<c>FUN_140343480</c>'s list argument), so a slave processed again keeps its kit and is not dressed anew (#356). Each item taken that has no
    /// owner yet becomes stolen from the captive, <paramref name="from"/> (<c>Item::notifyTheftFrom</c>, slot 106, <b>Verified</b>), so a worker
    /// whose package lists 184 Ditching loot takes the kit to a storage (#977).
    /// </summary>
    void Strip(World world, Vector3 at, Inventory inv, CharacterCold wc, StolenMark from, string[] clothing)
    {
        foreach (var type in Stripped)
        {
            foreach (var item in inv.Items.ToList())
            {
                if (item.Type != type || item.LockedOn || type == FcsRecordType.Armour && Kept(item, clothing)) continue;

                inv.Items.Remove(item);
                item.Section = "main";
                if (item.Mark == StolenMark.None) item.Mark = from;
                HandOver(world, at, item, wc);
            }
        }
    }

    /// <summary>
    /// Where one stripped item goes (<c>FUN_14035aa20</c>, <c>FUN_140343480</c>): the worker's worn backpack first (<b>Verified</b>), else its
    /// inventory (Meitou's main grid and bag, <see cref="Inventory.Add"/>; the original's <c>Inventory::addItem</c> tries its other accepting
    /// sections before the main grid, not modelled); what neither takes is dropped on the ground at the worker, <paramref name="at"/> (the add's
    /// drop flag, the drop made by the inventory's owner, <b>Observed</b>).
    /// </summary>
    static void HandOver(World world, Vector3 at, ItemInstance item, CharacterCold wc)
    {
        var into = wc.Inventory;
        if (InventoryCommands.FitsInBag(item) && into?.Backpack?.Contents is { } bag && bag.AddTo("backpack_content", item))
            return;
        if (into is not null && into.Add(item)) return;

        world.GroundItems.Drop(item, at, 0);
    }

    /// <summary>
    /// An ARMOUR piece the strip leaves on: a slave garment (<c>stigma</c> 6) or one of the worker's faction's <c>slave clothing</c>.
    /// </summary>
    bool Kept(ItemInstance item, string[] clothing) =>
        data.Db.Find(item.Record)?.GetInt("stigma", 0) == SlaveStigma || clothing.Contains(item.Record);

    /// <summary>
    /// The dressing of <see cref="Process"/> (<c>FUN_1403560e0</c>): each piece of <paramref name="clothing"/> the captive does not wear already is
    /// made and put on.
    /// </summary>
    void Dress(Inventory inv, CharacterCold cc, string[] clothing)
    {
        foreach (string record in clothing)
        {
            if (inv.Items.Any(i => i.Record == record && InventoryCommands.IsEquipmentSection(i.Section))) continue;
            if (data.Items.Create(record, 1) is { } piece) InventoryCommands.TryEquip(inv, piece, cc);
        }
    }

    #endregion
}
