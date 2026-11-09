using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
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
    /// The sweep for new jobs looks at each character once in this many ticks (on its slot's turn), starts at most <see cref="MaxNewJobs"/> over the
    /// period (spread: at least one a tick) and keeps at most <see cref="MaxJobs"/> running.
    /// </summary>
    public int SweepEveryTicks { get; init; } = 30;
    public int MaxJobs { get; init; } = 32;
    public int MaxNewJobs { get; init; } = 8;
    /// <summary>
    /// A worker is at its cage within the cage's <c>use range</c> plus this; a walk that ended within <see cref="Slack"/> counts too (a cage inside a
    /// building).
    /// </summary>
    public float Reach { get; init; } = 15;
    public float Slack { get; init; } = 200;
    /// <summary>
    /// Walks that end away from the cage before the job is given up.
    /// </summary>
    public int Walks { get; init; } = 3;
    /// <summary>
    /// Game seconds a job may last before it is given up.
    /// </summary>
    public float GiveUpSeconds { get; init; } = 600;
    /// <summary>
    /// The length of the kill clip <c>chop down static</c> (the system builder passes the data's), and the share of it at which its blow lands
    /// (<b>Observed</b>: 0.55).
    /// </summary>
    public float ChopSeconds { get; init; } = 1.5f;
    public float BlowAt { get; init; } = 0.55f;
    /// <summary>
    /// A liberator opens the cage after this long at it (<b>engine choice</b>: the release task's timing is not documented).
    /// </summary>
    public float ReleaseSeconds { get; init; } = 3;
    /// <summary>
    /// The slave processing kneels this long before the captive is stripped and enslaved (<b>Observed</b>: 3 s, <c>Task_ProcessNewSlave</c>).
    /// </summary>
    public float ProcessSeconds { get; init; } = 3;
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
    /// A worker shouting at a slave says event 38 again after this many seconds while no line is said (<b>engine choice</b>: the original's re-plan
    /// rate is not decoded).
    /// </summary>
    public float ShoutSeconds { get; init; } = 8;
    /// <summary>
    /// It stands within this distance of its slave and follows it when the slave moves on.
    /// </summary>
    public float ShoutReach { get; init; } = 40;
    /// <summary>
    /// Game hours a target stays on a worker's give-up list after a job on it failed (<b>engine choice</b>).
    /// </summary>
    public double GiveUpHours { get; init; } = 1;
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
    /// Goals 194 / 218: a delivered slave is put into a cage of the receiving town.
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
}

public enum CaptivePhase : byte { Walk, Work }

/// <summary>
/// One worker's job on one captive (serial state of the <see cref="CaptiveSystem"/>).
/// </summary>
public sealed class CaptiveJob
{
    public required CaptiveJobKind Kind { get; init; }
    public required CharacterId Worker { get; init; }
    public required CharacterId Captive { get; init; }
    /// <summary>
    /// The cage the captive is in (or, for <see cref="CaptiveJobKind.Deliver"/> and <see cref="CaptiveJobKind.CageSelf"/>, the cage it goes into).
    /// </summary>
    public required string Cage { get; init; }
    public required int Faction { get; init; }
    /// <summary>
    /// The town the job is in (its kill timer), -1: none.
    /// </summary>
    public int Site { get; init; } = -1;
    public CaptivePhase Phase { get; set; }
    /// <summary>
    /// Game seconds in the phase and of the whole job.
    /// </summary>
    public float Time { get; set; }
    public float Age { get; set; }
    public int Walks { get; set; }
    /// <summary>
    /// Blows struck (kill); the game second of the next line (eat).
    /// </summary>
    public int Blows { get; set; }
    /// <summary>
    /// Blows that beat an imprisoned captive down before a capture (196).
    /// </summary>
    public int Beats { get; set; }
    public float NextBark { get; set; }
    /// <summary>
    /// A job of one of the player's animals (<see cref="CaptiveSystem"/>'s race-goal run for them): a player order ends it. The worker's task and
    /// goal as the last step left them.
    /// </summary>
    public bool PlayerSide { get; init; }
    public byte LastTask { get; set; }
    public Vector2 LastGoal { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add((int)Kind);
        h.Add(Worker);
        h.Add(Captive);
        h.Add(Rng.StableHash(Cage));
        h.Add(Faction);
        h.Add(Site);
        h.Add((int)Phase);
        h.Add(Time);
        h.Add(Age);
        h.Add(Walks);
        h.Add(Blows);
        h.Add(NextBark);
        if (Beats != 0) h.Add(Beats);
        if (PlayerSide)
        {
            h.Add((int)LastTask);
            h.Add(LastGoal);
        }
    }
}

public enum CaptiveEventKind { Started, Killed, Eaten, Enslaved, Gathered, Delivered, Caged, GaveUp, Captured, Shouted, Liberated }

public readonly record struct CaptiveEvent(long Tick, CaptiveEventKind Kind, CaptiveJobKind Job, CharacterId Worker, CharacterId Captive);

/// <summary>
/// What captors do with their captives (docs/simulation.md "Captives"; facts in docs/game/ai-tasks.md "Prisoner fate" and "Slave rows"). The original
/// runs these as package goals planned down to their tasks; Meitou runs each as a job of one worker, started by a sweep for every member of an AI
/// squad whose current role list holds the goal in its hours and who is free (the AI's, awake, not fighting, not in another job), worked serially in
/// <c>SlowWorld</c> while the AI leaves the worker alone (<see cref="CharacterCold.Engaged"/>). The cages a worker uses are those of the town its
/// squad belongs to.
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
/// (<c>medic</c>), then strips the captive's weapons and armour into its own inventory (locked shackles stay), dresses it in its faction's <c>slave
/// clothing</c> where the slot is free, says event 35 INTRODUCING_NEW_SLAVE (37 RECAPTURED_A_SLAVE for one whose head is shaven already), makes it
/// its faction's slave and shaves its head (<c>Task_ProcessNewSlave</c>, <b>Observed</b>; the head
/// <b>Verified</b>).</item>
/// <item><b>Capture / Carry / Shout</b> (167, 168, 188): see <c>CaptiveSystem.Capture.cs</c>. The slaves' own work (189, 190) is not a job but the
/// AI's <see cref="AiBehaviour.Work"/>.</item>
/// </list>
/// The jobs and the kill timers are not saved (the original keeps the timers in a global map it does not save, <b>Observed</b>); hashed only while
/// any exist.
/// </summary>
public sealed partial class CaptiveSystem(PopulationData data, CombatSystem? combat, CaptiveSettings? settings = null) : ITickSystem, IStateHashed,
        IDecisionOwner, IGoalSource
{
    readonly List<CaptiveJob> _jobs = [];
    readonly List<CaptiveEvent> _log = [];
    // The kill timer of each town: the game hour from which a kill is wanted again (FUN_14034a810's map), by site.
    readonly SortedDictionary<int, double> _killAt = [];
    readonly CharacterAssembly _assembly = new(data);
    readonly XpService _xp = new(data.Bodies.Constants, data.BodyOptions);
    /// <summary>
    /// The amount of the toughness XP a meal still awake gains per update (<b>Observed</b>: 10, <c>FUN_1404356f0</c>).
    /// </summary>
    const float EatToughnessXp = 10;
    int _timerRolls;

    public CaptiveSettings Settings { get; } = settings ?? new CaptiveSettings();
    /// <summary>
    /// The length of the kill clip in game seconds (the system builder sets the data's <c>chop down static</c>;
    /// <see cref="CaptiveSettings.ChopSeconds"/> without animations).
    /// </summary>
    public float ChopSeconds { get; set; } = (settings ?? new CaptiveSettings()).ChopSeconds;
    public IReadOnlyList<CaptiveJob> Jobs => _jobs;
    public IReadOnlyList<CaptiveEvent> Log => _log;
    /// <summary>
    /// The game hour from which the town <paramref name="site"/> wants a kill again (null: not asked yet).
    /// </summary>
    public double? KillTimer(int site) => _killAt.TryGetValue(site, out double at) ? at : null;

    const ulong TimerSalt = 0x4B696C6C54696D72;

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        if (world.Tick % RestoreEveryTicks == 0) RestoreSlavers(world);
        StepRacks(world);
        for (int k = 0; k < _jobs.Count;)
        {
            var j = _jobs[k];
            if (j.PlayerSide && Overridden(world, j))
            {
                _jobs.RemoveAt(k);
                continue;
            }
            if (Step(world, j))
            {
                if (j.PlayerSide && table.TryResolveNext(j.Worker, out int w))
                    (j.LastTask, j.LastGoal) = (table.Next[w].Task, table.Next[w].Goal);
                k++;
            }
            else
            {
                _jobs.RemoveAt(k);
                _ended.Add(j.Worker);
            }
        }
        OfferAfterJobs(world);

        // Each character is looked at once in SweepEveryTicks, on its slot's turn (slot mod the period), so the sweep spreads over the ticks instead
        // of landing on one (8-11 ms a sweep with ~1,400 characters loaded, docs/simulation.md "Spread sweeps"; the original runs its AI updates 8
        // characters a frame in turn, FUN_140786e30).
        int every = world.Every(Settings.SweepEveryTicks), phase = (int)(world.Tick % every);
        if (phase == 0) ExpireWaits(world.GameHours);
        Sweep(world, every);
        RunPlayerAnimalGoals(world, every);
    }

    /// <summary>
    /// The player's animals keep their race's <c>AI Goals</c> but 230 (<c>Character::setupAI</c>, docs/game/animals.md "AI goals"): the limb fetch
    /// (277, <see cref="Fetch"/>; the limb goes into the mouth and is eaten, <see cref="Items.FeedSystem"/>), the limb play (278) and chase (279) by
    /// their player-side desires, and eating a downed body not of an ally (233). Crop raiding (232) finds nothing for them: its farms are those of a
    /// town the eater does not get along with, and only the player's farms are machines. A player character's are the decision step's
    /// (<see cref="RaceGoals"/>: priority 3 at step 4, priority 2 at step 6); this pass runs them in list order for the other animals of the player's
    /// faction, idle only (engine). An order that runs ends one (<see cref="CaptiveJob.PlayerSide"/>).
    /// </summary>
    void RunPlayerAnimalGoals(World world, int every)
    {
        if (world.Player.Faction < 0) return;
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || !world.Turn(s, every) || table.Next[s].Task != (byte)CharacterTask.Idle
                    || table.Cold(s) is not { Fighter.IsAnimal: true } c || !PlayerSquad(world, c) || DecisionSystem.Decides(c))
                continue;
            if (!FreeForRaceGoal(world, s, c)) continue;
            if (PickRaceGoal(world, s, c, null, firstInList: true) is { } pick) StartRaceGoal(world, s, c, pick);
        }
    }

    IReadOnlyList<AiGoal> RaceGoalsOf(CharacterCold c) => c.Race is { } race ? data.Ai.RaceGoals(race.StringId) : [];

    /// <summary>
    /// A player animal's race goal: its type, desire x the entry's weight, and its target (a body's or a mouthful ally's slot, a limb).
    /// </summary>
    readonly record struct RaceGoal(TaskType Type, float Score, int Target, GroundItem? Limb);

    /// <summary>
    /// Not engaged, fighting, fleeing, carried, down, or another captive job's.
    /// </summary>
    bool FreeForRaceGoal(World world, int s, CharacterCold c) =>
        !c.Engaged && !c.InCombat && !c.Fleeing && c.CarriedBy.IsNone && c.Medical is { Incapacitated: false } && !Busy(world.Characters.IdOf(s));

    /// <summary>
    /// The race goal of priority <paramref name="priority"/> (any: null) the animal would run now (a pure read): the first that can in list order
    /// (<paramref name="firstInList"/>), else the best desire x weight, a tie to the later-listed (<c>FUN_14050df10</c>). Desires: 277 1 with a limb
    /// found, 278 <see cref="PlayDesire"/>, 279 <see cref="ChaseDesire"/> with a mouthful ally, 233 1 with a body.
    /// </summary>
    RaceGoal? PickRaceGoal(World world, int s, CharacterCold c, TaskPriority? priority, bool firstInList)
    {
        if (c is not { Fighter.IsAnimal: true, Race: { } race }) return null;
        RaceGoal? best = null;
        foreach (var goal in data.Ai.RaceGoals(race.StringId))
        {
            if (priority is { } p && goal.Task.Priority != p) continue;
            float desire = 0;
            int target = -1;
            GroundItem? limb = null;
            switch (goal.Task.Type)
            {
                case TaskType.AnimalFetchALimb when LimbFor(world, s, c) is { } found:
                    (desire, limb) = (1, found);
                    break;
                case TaskType.PlayBecauseIHaveALimbInMouth:
                    desire = PlayDesire(world, c);
                    break;
                case TaskType.ChaseAllyDogsWithMouthLimbs when ChaseDesire(world, c) > 0 && MouthfulAlly(world, s, c) is >= 0 and var mouthful:
                    (desire, target) = (ChaseDesire(world, c), mouthful);
                    break;
                case TaskType.EatARandomKoBody when BodyToEat(world, s, c, dead: false) is >= 0 and var meal:
                    (desire, target) = (1, meal);
                    break;
            }
            if (!(desire > 0)) continue;
            var pick = new RaceGoal(goal.Task.Type, desire * goal.Weight, target, limb);
            if (firstInList) return pick;
            if (best is null || pick.Score >= best.Value.Score) best = pick;
        }
        return best;
    }

    void StartRaceGoal(World world, int s, CharacterCold c, RaceGoal pick)
    {
        var table = world.Characters;
        CaptiveJobKind? kind = pick.Type switch
        {
            TaskType.PlayBecauseIHaveALimbInMouth => CaptiveJobKind.DogPlay,
            TaskType.ChaseAllyDogsWithMouthLimbs => CaptiveJobKind.DogChase,
            TaskType.EatARandomKoBody => CaptiveJobKind.EatBody,
            _ => null,
        };
        if (kind is not { } k)
        {
            if (pick.Limb is { } limb) Fetch(world, s, c, limb);
            return;
        }
        var job = new CaptiveJob { Kind = k, Worker = table.IdOf(s), Captive = pick.Target >= 0 ? table.IdOf(pick.Target) : CharacterId.None,
                Cage = "", Faction = c.Faction, PlayerSide = true };
        (job.LastTask, job.LastGoal) = (table.Next[s].Task, table.Next[s].Goal);
        _jobs.Add(job);
        c.Engaged = true;
        Note(world, CaptiveEventKind.Started, job);
    }

    public void RegisterSources(DecisionSystem decisions)
    {
        decisions.RegisterJob(TaskType.SlaveObedience, _obedient);
        decisions.Register(new RaceGoals(this, TaskPriority.Urgent));
        decisions.Register(new RaceGoals(this, TaskPriority.NonUrgent));
    }

    /// <summary>
    /// A player animal's race goals of one priority (<c>Character::setupAI</c> adds the race's <c>AI Goals</c> to a player-faction animal, after
    /// 243): priority 3 at step 4, weighed with the other goals, priority 2 at step 6 before 14 Idleness. The best desire x weight starts
    /// (<see cref="PickRaceGoal"/>); it runs while its job does (the fetch: while its pick-up walk does).
    /// </summary>
    sealed class RaceGoals(CaptiveSystem owner, TaskPriority priority) : IDecisionSource
    {
        (long Tick, int Slot, RaceGoal Goal) _picked = (-1, -1, default);

        public string Name => $"Race goals (priority {(int)priority})";
        public DecisionStep Step => priority == TaskPriority.Urgent ? DecisionStep.Goals : DecisionStep.Fallback;
        /// <summary>
        /// Step 4: after 243 (<c>setupAI</c>'s order); step 6: with the priority-2 goals, before the priority-1 ones.
        /// </summary>
        public int Key => priority == TaskPriority.Urgent ? 250 : 1000 + (int)TaskType.EatARandomKoBody;

        public DecisionCheck CanRun(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            if (cold is not { Fighter.IsAnimal: true, Race: not null }) return DecisionCheck.No("not an animal");
            if (Running(world, slot, cold) is { } type)
                return PriorityOf(cold, type) == priority ? DecisionCheck.Runs : DecisionCheck.No("another race goal runs");
            if (!owner.FreeForRaceGoal(world, slot, cold)) return DecisionCheck.No("busy");
            if (owner.PickRaceGoal(world, slot, cold, priority, firstInList: false) is not { } pick)
                return DecisionCheck.No("none holds");
            _picked = (world.Tick, slot, pick);
            return DecisionCheck.Run;
        }

        public float Desire(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            if (Running(world, slot, cold) is { } type) return owner.PickRaceGoal(world, slot, cold, priority, firstInList: false) is { } now
                    && now.Type == type ? now.Score : 1;
            return _picked.Tick == world.Tick && _picked.Slot == slot ? _picked.Goal.Score : 0;
        }

        public void Start(World world, int slot)
        {
            if (_picked.Tick == world.Tick && _picked.Slot == slot)
                owner.StartRaceGoal(world, slot, world.Characters.Cold(slot)!, _picked.Goal);
        }

        /// <summary>
        /// The race goal the animal runs: its player-side job's, or the limb fetch's pick-up walk.
        /// </summary>
        TaskType? Running(World world, int slot, CharacterCold cold)
        {
            var id = world.Characters.IdOf(slot);
            foreach (var j in owner._jobs)
                if (j.Worker == id && j.PlayerSide)
                    return j.Kind switch
                    {
                        CaptiveJobKind.DogPlay => TaskType.PlayBecauseIHaveALimbInMouth,
                        CaptiveJobKind.DogChase => TaskType.ChaseAllyDogsWithMouthLimbs,
                        _ => TaskType.EatARandomKoBody,
                    };
            return cold.PickingUpItem is not null && cold.Running is { Row: TaskType.AnimalFetchALimb } ? TaskType.AnimalFetchALimb : null;
        }

        TaskPriority? PriorityOf(CharacterCold cold, TaskType type) =>
            owner.RaceGoalsOf(cold).FirstOrDefault(g => g.Task.Type == type)?.Task.Priority;
    }

    /// <summary>
    /// A player animal's job the player took over since its last step: its task changed other than a walk that ended, or it walks elsewhere. The job
    /// ends without stopping it, so the order stands.
    /// </summary>
    static bool Overridden(World world, CaptiveJob j)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(j.Worker, out int w)) return false;
        ref var n = ref table.Next[w];
        byte go = (byte)CharacterTask.GoTo;
        bool taken = n.Task == go ? j.LastTask != go || n.Goal != j.LastGoal : n.Task != j.LastTask
                && !(j.LastTask == go && n.Task == (byte)CharacterTask.Idle);
        if (!taken && !PlayerOrders.Wins(world, w)) return false;
        if (table.Cold(w) is { } wc)
        {
            wc.Engaged = false;
            wc.ActionClip = null;
        }
        return true;
    }

    // ------------------------------------------------------------------ the sweep

    bool Busy(CharacterId id)
    {
        foreach (var j in _jobs)
            if (j.Worker == id || j.Captive == id) return true;
        return false;
    }

    bool Taken(string cage)
    {
        foreach (var j in _jobs)
            if (j.Cage == cage) return true;
        return false;
    }

    /// <summary>
    /// A worker the AI holds and nothing else: awake (not asleep or sitting), not fighting, in no job.
    /// </summary>
    bool Free(World world, int slot)
    {
        var table = world.Characters;
        return table.Next[slot].Alive && table.Cold(slot) is { } c && AiSystem.Holds(table.Next[slot], c)
                && table.Next[slot].Task != (byte)CharacterTask.Rest && !Busy(table.IdOf(slot));
    }

    void Sweep(World world, int every)
    {
        var table = world.Characters;
        int hour = world.Hour, started = 0;

        // MaxNewJobs over the period, spread like the looks (at least one a tick).
        int maxNew = Math.Max(1, (Settings.MaxNewJobs + every - 1) / every);
        foreach (var squad in world.Squads.All)
        {
            if (_jobs.Count >= Settings.MaxJobs || started >= maxNew) return;
            if (squad.Ai is not { } board || squad.Members.Count == 0) continue;
            foreach (var member in squad.Members)
            {
                if (_jobs.Count >= Settings.MaxJobs || started >= maxNew) return;
                if (!table.TryResolveNext(member, out int slot) || !world.Turn(slot, every)) continue;
                bool free = Free(world, slot);
                if (free && StartFirst(world, squad, board, slot, hour)) started++;

                // A free member the start found nothing for has nothing to offer either.
                else if (free) ClearOffers(world, slot);
                else KeepOffers(world, squad, board, slot, hour);
            }
        }
    }

    /// <summary>
    /// Starts the job of the first of the member's goals in their hours that finds a target, unscored: its role's list, then its race's; false when
    /// none does.
    /// </summary>
    bool StartFirst(World world, Squad squad, Blackboard board, int slot, int hour)
    {
        var cold = world.Characters.Cold(slot)!;
        var roles = RolesOf(world, squad, board, slot, cold);
        for (int g = 0; g < roles.Count; g++)
            if (roles[g].InWindow(hour) && Start(world, squad, board, slot, cold, roles[g])) return true;
        // The race's own AI Goals (Character::setupAI loads them for every NPC; their window is always open).
        if (cold.Engaged || cold.Ai is not { RaceGoals: { Count: > 0 } race }) return false;
        for (int g = 0; g < race.Count; g++)
            if (Start(world, squad, board, slot, cold, race[g])) return true;
        return false;
    }

    /// <summary>
    /// The member's role list: an obeying slave takes its master squad's Slave AI Goals instead of its own lists, as the AI's goal pick does (job
    /// 187).
    /// </summary>
    static IReadOnlyList<AiGoal> RolesOf(World world, Squad squad, Blackboard board, int slot, CharacterCold cold) =>
        AiSystem.ObeyedRoles(world, squad, cold) ?? board.Roles[(int)AiSystem.RoleOf(squad, world.Characters.IdOf(slot), cold)];

    /// <summary>
    /// Starts the job of <paramref name="goal"/> when it finds a target (<see cref="TryStart"/>); the goal becomes the mind's
    /// (<see cref="AiSystem.TakeCaptiveGoal"/>).
    /// </summary>
    bool Start(World world, Squad squad, Blackboard board, int slot, CharacterCold cold, AiGoal goal)
    {
        if (TryStart(world, squad, board, slot, cold, goal.Task.Type) is not { } job) return false;
        _jobs.Add(job);
        cold.Engaged = true;
        if (cold.Ai is { } mind) AiSystem.TakeCaptiveGoal(mind, goal);
        Note(world, CaptiveEventKind.Started, job);
        return true;
    }

    static readonly HashSet<TaskType> JobRows = [.. Enum.GetValues<CaptiveJobKind>().Select(RowOf)];

    /// <summary>
    /// Whether the worker walks the job's own walk: a GoTo whose running row is the job's (<see cref="RowOf"/>). A walk it was on when the job
    /// started, its AI's or an errand's, is not, so the job sends its own at once, as a goal taken up ends the plan before it (docs/game/ai-tasks.md
    /// "Precedence").
    /// </summary>
    static bool WalksOwn(CharacterTable table, int w, CaptiveJob j) =>
        table.Next[w].Task == (byte)CharacterTask.GoTo && table.Cold(w)?.Running?.Row == RowOf(j.Kind);

    /// <summary>
    /// Whether a walk's row is a job's (<see cref="RowOf"/>): a fight may interrupt it, the job is given up (<see cref="Ai.AiSystem"/>'s
    /// pre-emption).
    /// </summary>
    internal static bool IsJobRow(TaskType row) => JobRows.Contains(row);

    /// <summary>
    /// The row a job's walks act out (<see cref="TaskStart.Walk(World, int, Vector2, byte, TaskType, TaskSource)"/>): the task its goal plans to
    /// (<see cref="CaptiveJobKind"/>).
    /// </summary>
    static TaskType RowOf(CaptiveJobKind kind) => kind switch
    {
        CaptiveJobKind.Kill => TaskType.KillCageOccupant,
        CaptiveJobKind.Eat or CaptiveJobKind.EatBody => TaskType.EatTargetAlive,
        CaptiveJobKind.Process => TaskType.ProcessAndStripNewSlave,
        CaptiveJobKind.Gather => TaskType.GetSlave,
        CaptiveJobKind.Deliver => TaskType.GiveAllMySlavesTo,
        CaptiveJobKind.CageSelf => TaskType.SlaveGetInMyMastersCage,
        CaptiveJobKind.Capture => TaskType.CaptureNewSlaves,
        CaptiveJobKind.Carry => TaskType.CarryWoundedSlaves,
        CaptiveJobKind.Shout => TaskType.WorkTheSlaves,
        CaptiveJobKind.Liberate => TaskType.LiberateAllThePrisoners,
        CaptiveJobKind.EatCrops => TaskType.EatCrops,
        CaptiveJobKind.Worship => TaskType.WorshipTarget,
        CaptiveJobKind.DogPlay => TaskType.PlayBecauseIHaveALimbInMouth,
        _ => TaskType.ChaseAllyDogsWithMouthLimbs,
    };

    /// <summary>
    /// A job for goal <paramref name="type"/> of the member in <paramref name="slot"/>, when the goal is one of the captive goals and it finds a
    /// target (null otherwise). With <paramref name="probe"/> it only tells whether the job would start: nothing is drawn, fetched, said or
    /// remembered (the kill rate's town timer is still created, as its desire does).
    /// </summary>
    CaptiveJob? TryStart(World world, Squad squad, Blackboard board, int slot, CharacterCold cold, TaskType type, bool probe = false)
    {
        int site = board.Town;
        switch (type)
        {
            case TaskType.KillARandomCageOccupant:
                site = FateTown(world, slot, board);

                // The desire reads the town and creates its timer before any other test; the weapon is the plan's equip step (FateJob), not a
                // condition of the goal.
                if (site < 0 || !KillDue(world, site, cold) || combat is null) return null;
                return FateJob(world, slot, cold, board, site, CaptiveJobKind.Kill, probe);
            case TaskType.EatARandomCageOccupant:
                // No town test: the finder falls back to the nearest town.
                return FateJob(world, slot, cold, board, FateTown(world, slot, board), CaptiveJobKind.Eat, probe);
            case TaskType.EatARandomCageOccupantMeasuredRate:
                site = FateTown(world, slot, board);
                return site < 0 || !KillDue(world, site, cold) ? null : FateJob(world, slot, cold, board, site, CaptiveJobKind.Eat, probe);
            case TaskType.EatARandomDeadBody:
            case TaskType.EatARandomKoBody:
                return BodyToEat(world, slot, cold, dead: type == TaskType.EatARandomDeadBody) is >= 0 and var meal
                    ? new CaptiveJob { Kind = CaptiveJobKind.EatBody, Worker = world.Characters.IdOf(slot), Captive = world.Characters.IdOf(meal),
                            Cage = "", Faction = cold.Faction }
                    : null;
            case TaskType.FogmanWorshipVictim:
                return cold.Carrying.IsNone && WorshipTarget(world, slot) is { } shrine
                    ? new CaptiveJob { Kind = CaptiveJobKind.Worship, Worker = world.Characters.IdOf(slot), Captive = shrine.Occupant,
                            Cage = shrine.Id, Faction = cold.Faction }
                    : null;
            case TaskType.AnimalFetchALimb:
                if (!probe && cold.Fighter is { IsAnimal: true }) Fetch(world, slot, cold);   // it picks the limb up itself: no job
                return null;
            case TaskType.PlayBecauseIHaveALimbInMouth:
                return cold.Fighter is { IsAnimal: true } && Plays(world, cold)
                    ? new CaptiveJob { Kind = CaptiveJobKind.DogPlay, Worker = world.Characters.IdOf(slot), Captive = CharacterId.None, Cage = "",
                            Faction = cold.Faction }
                    : null;
            case TaskType.ChaseAllyDogsWithMouthLimbs:
                return cold.Fighter is { IsAnimal: true } && Chases(world, cold) && MouthfulAlly(world, slot, cold) is >= 0 and var mouthful
                    ? new CaptiveJob { Kind = CaptiveJobKind.DogChase, Worker = world.Characters.IdOf(slot),
                            Captive = world.Characters.IdOf(mouthful), Cage = "", Faction = cold.Faction }
                    : null;
            case TaskType.FindCropsToEat:
                return CropsToEat(world, slot, cold, board) is { } farm
                    ? new CaptiveJob { Kind = CaptiveJobKind.EatCrops, Worker = world.Characters.IdOf(slot), Captive = CharacterId.None, Cage = farm,
                            Faction = cold.Faction }
                    : null;
            case TaskType.NewSlaveProcessing:
            case TaskType.SlaveWatching:
                // The slot-61 desire of 183 / 260 (and 167) for a goal without a target: 1 while not carrying and in posture 0, else 0.
                if (cold.Carrying is { IsNone: false } || cold.Medical is { Standing: false }) return null;

                // 183 first takes a free or escaped slave in sight, with no cage and no town (FUN_1408cd630).
                if (type == TaskType.SlaveWatching && FreeSlaveToProcess(world, slot, cold) is >= 0 and var loose)
                    return new CaptiveJob { Kind = CaptiveJobKind.Process, Worker = world.Characters.IdOf(slot),
                            Captive = world.Characters.IdOf(loose), Cage = "", Faction = cold.Faction, Site = site };
                if (site < 0) return null;
                return ProcessTarget(world, slot, cold, site, watching: type == TaskType.SlaveWatching) is { } found
                    ? new CaptiveJob { Kind = CaptiveJobKind.Process, Worker = world.Characters.IdOf(slot), Captive = found.Occupant, Cage = found.Id,
                            Faction = cold.Faction, Site = site }
                    : null;
            case TaskType.LiberateAllThePrisoners:
                return LiberateTarget(world, slot, cold, board) is { } freed
                    ? new CaptiveJob { Kind = CaptiveJobKind.Liberate, Worker = world.Characters.IdOf(slot), Captive = freed.Occupant,
                            Cage = freed.Id, Faction = cold.Faction, Site = freed.Site }
                    : null;
            case TaskType.CaptureNewSlaves:
            case TaskType.CarryWoundedSlaves:
            case TaskType.WorkTheSlaves:
                return TryStartSlaveWork(world, squad, slot, cold, type, probe);
            default:
                return TryStartLogistics(world, squad, board, slot, cold, type);
        }
    }

    CaptiveJob? FateJob(World world, int slot, CharacterCold cold, Blackboard board, int site, CaptiveJobKind kind, bool probe = false)
    {
        if (FateTarget(world, slot, cold, board.OwnHome?.Key, out bool fresh, remember: !probe) is not { } cage)
            return null;
        if (kind == CaptiveJobKind.Kill && !(probe ? HasWeaponToDraw(cold) : DrawWeapon(cold))) return null;
        var table = world.Characters;
        var worker = table.IdOf(slot);
        var job = new CaptiveJob { Kind = kind, Worker = worker, Captive = cage.Occupant, Cage = cage.Id, Faction = cold.Faction, Site = site };
        if (probe) return job;

        // A fresh choice has the occupant say "marked for death" to the one who picked it (event 10, FUN_1408d0d90; every character has a dialogue
        // state, so the original's test for one never fails).
        if (fresh) world.Defer(new Dialogue.DialogueEvent(cage.Occupant, worker, MarkedForDeath) { Tick = world.Tick + 1 });
        if (kind == CaptiveJobKind.Eat) cold.DrawnWeapon = WeaponKinds.None;   // 220's B list: !hasWeaponEquipped
        return job;
    }

    /// <summary>
    /// The plan's equip step before 134 Kill prisoner (B list hasWeaponEquipped; 6 <c>Task_EquipBestWeapon</c> <c>FUN_140346c30</c>, <b>Verified</b>:
    /// the weapon on its back, then on its hip, else for a non-player any WEAPON item of its inventory): a captor with a weapon in a weapon slot
    /// draws it; one carrying a melee weapon elsewhere puts it in a free slot first (<b>engine reading</b>: with no free slot the original's equip is
    /// not read). False with no weapon to draw: the goal then does not plan (finding one is goals 8 and 42).
    /// </summary>
    bool DrawWeapon(CharacterCold cold)
    {
        if (cold.Fighter is null || cold.Inventory is not { } inv) return false;
        if (cold.Fighter.Unarmed
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

    /// <summary>
    /// Whether <see cref="DrawWeapon"/> finds a weapon, drawing nothing: one in a weapon slot, else a weapon carried elsewhere (taken as fitting a
    /// free slot).
    /// </summary>
    static bool HasWeaponToDraw(CharacterCold cold) =>
        cold.Fighter is { } fighter && cold.Inventory is { } inv
        && (!fighter.Unarmed || inv.All().Any(i => i.Type == FcsRecordType.Weapon && !InventoryCommands.IsEquipmentSection(i.Section)));

    /// <summary>
    /// The ANIMATION records the jobs hold (<c>Task_KillPrisoner</c>, <c>Task_EatPrisoner</c>, <c>Task_ProcessNewSlave</c>, <b>Observed</b>).
    /// </summary>
    public const string ChopClip = "chop down static", EatClip = "eat", ProcessClip = "medic";

    /// <summary>
    /// EventTriggerEnum values the captive jobs fire (docs/game/dialogue.md "Enums").
    /// </summary>
    public const int MarkedForDeath = 10, ScreamingTorture = 11, IntroducingNewSlave = 35, RecapturedSlave = 37, SlaveDelivery = 39,
            EatingSounds = 52;

    // ------------------------------------------------------------------ finders

    /// <summary>
    /// The town's cages (registering its fixtures when nobody has yet).
    /// </summary>
    IEnumerable<Fixture> CagesOf(World world, int site) => CagesOf(world, data, site);

    static IEnumerable<Fixture> CagesOf(World world, PopulationData data, int site)
    {
        if (!world.Fixtures.IsRegistered(site)) world.Fixtures.RegisterSite(world, data, site);
        foreach (var f in world.Fixtures.All)
            if (f.Kind == FixtureKind.Cage && f.Site == site) yield return f;
    }

    /// <summary>
    /// The live character held in a cage, or -1.
    /// </summary>
    static int OccupantSlot(World world, Fixture f)
    {
        var table = world.Characters;
        if (FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s) || table.Cold(s) is not { Medical: { Dead: false } })
            return -1;
        return s;
    }

    /// <summary>
    /// The town the kill and eat goals read: the town the worker stands in (the character status's town, `+0x88`: the kill rate's gate and its timer
    /// key, <b>Verified</b> `FUN_14032e9b0`), else its squad's town (<b>engine reading</b>, as <see cref="LiberateTarget"/>); -1: none.
    /// </summary>
    int FateTown(World world, int slot, Blackboard board) => TownAt(data, world.Characters.Next[slot].Position) is >= 0 and var here ? here
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
    Fixture? FateTarget(World world, int slot, CharacterCold cold, string? home, out bool fresh, bool remember = true)
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

    public static bool CagesFull(World world, PopulationData data, int site, CharacterCold who)
    {
        int home = world.Squads.Find(who.SquadId)?.Town ?? -1;
        bool any = false;
        foreach (int town in home >= 0 && home != site ? new[] { site, home } : [site])
            foreach (var f in CagesOf(world, data, town))
            {
                if (!world.Fixtures.CanUse(world, who, f)) continue;
                if (OccupantSlot(world, f) < 0) return false;
                any = true;
            }
        return any;
    }

    /// <summary>
    /// The kill rate (AI slot 20, <c>FUN_14032e9b0</c>): the town's timer has run out, or all the faction's cages there are full. The first ask sets
    /// the timer.
    /// </summary>
    bool KillDue(World world, int site, CharacterCold who)
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
    /// (<c>FUN_1408ca3d0</c>; its flag <c>0x3e0</c> is taken as slave state 1, <b>Unknown</b>).
    /// </summary>
    Fixture? ProcessTarget(World world, int slot, CharacterCold cold, int site, bool watching)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        foreach (var f in CagesOf(world, site))
        {
            if (f.Owner < 0 || f.Owner == world.Player.Faction || !world.Fixtures.CanUse(world, cold, f) || Taken(f.Id))
                continue;
            if (OccupantSlot(world, f) is not (>= 0 and var s) || Busy(table.IdOf(s))) continue;
            var c = table.Cold(s)!;
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
    /// The first pass of the watchers' finder (183, <c>FUN_1408cd630</c>, <b>Verified</b>): the nearest (engine reading of the sensed list: a bare
    /// sight-range query, no building test) live character in sight in slave state 1 or 2 that is free (not carried, caged or held), no animal, not
    /// another job's or given up, and <see cref="NeedsProcessing"/>. No distance bound, no cage. -1: none.
    /// </summary>
    int FreeSlaveToProcess(World world, int slot, CharacterCold me)
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
            if (st.System.Busy(id) || st.System.Waiting(st.System._givenUp, id, st.World.GameHours) || !st.System.NeedsProcessing(st.Me, c))
                return;
            float d2 = Vector3.DistanceSquared(table.Next[st.Self].Position, table.Next[v].Position);
            if (d2 < st.BestD2 || d2 == st.BestD2 && v < st.Best) (st.Best, st.BestD2) = (v, d2);
        });
        return scan.Best;
    }

    struct FreeScan
    {
        public CaptiveSystem System;
        public World World;
        public int Self;
        public CharacterCold Me;
        public int Best;
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
    /// <c>slave clothing</c>; then the boots section passes with a locked piece, and empty only with a locked armour piece on a shaven head.
    /// </summary>
    bool LooksLikeASlave(CharacterCold watcher, CharacterCold c)
    {
        if (c.Fighter is { IsAnimal: true }) return true;
        if (c.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Diplomat) return false;
        var faction = (uint)watcher.Faction < (uint)data.Factions.Count ? data.Factions[watcher.Faction] : null;
        if (faction is not { AllowSlavesWeapons: true } && !CharacterFacts.TargetUnarmed(c)) return false;

        string[] clothing = faction is not null && data.Db.Find(faction.Id) is { } record
                ? [.. record.GetReferences("slave clothing").Select(l => l.TargetStringId)] : [];
        var armour = c.Inventory?.Items.Find(i => i.Section == "armour");
        if (!SlaveWear(armour, clothing) || !SlaveWear(c.Inventory?.Items.Find(i => i.Section == "legs"), clothing))
            return false;
        return c.Inventory?.Items.Find(i => i.Section == "boots") is { } boots ? boots.LockedOn : armour is { LockedOn: true } && c.Shaved;
    }

    /// <summary>
    /// An armour or legs piece a slave may wear: none, a slave garment (<c>stigma</c> 6) or one of the watcher's faction's <c>slave clothing</c>.
    /// </summary>
    bool SlaveWear(ItemInstance? item, string[] clothing) =>
        item is null || data.Db.Find(item.Record)?.GetInt("stigma", 0) == SlaveStigma || clothing.Contains(item.Record);

    /// <summary>
    /// The cage a liberator opens (276, <c>BuildingFinder_CagesToLiberate</c> <c>FUN_1408cf140</c>, <b>Observed</b>): a cage of a faction that is
    /// neither the worker's nor allied to it, holding a live occupant that is not the worker's enemy and that no other job works; the nearest in the
    /// town the worker stands in, else its squad's town.
    /// </summary>
    Fixture? LiberateTarget(World world, int slot, CharacterCold cold, Blackboard board)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        int site = TownAt(data, at) is >= 0 and var here ? here : board.Town;
        if (site < 0) return null;
        Fixture? best = null;
        float bestD = float.MaxValue;
        foreach (var f in CagesOf(world, site))
        {
            if (f.Owner < 0 || f.Owner == cold.Faction || Allied(cold.Faction, f.Owner) || Taken(f.Id)) continue;
            if (OccupantSlot(world, f) is not (>= 0 and var s) || Busy(table.IdOf(s))) continue;
            if (data.Relations.Contains(cold.Faction) && AggroSystem.IsEnemy(data.Relations, cold.Faction, table.Cold(s)!.Faction))
                continue;
            float d = Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(f.Position.X, f.Position.Z));
            if (d < bestD) (best, bestD) = (f, d);
        }
        return best;
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

    const float ExSlaveFloor = 0.01f;

    bool Allied(int a, int b) => a >= 0 && b >= 0
            && (a == b || data.Relations.Contains(a) && data.Relations.Contains(b) && data.Relations.IsAlly(a, b));

    // ------------------------------------------------------------------ one job

    /// <summary>
    /// One tick of a job; false when it is over.
    /// </summary>
    bool Step(World world, CaptiveJob j)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        j.Time += dt;
        j.Age += dt;
        if (!table.TryResolveNext(j.Worker, out int w) || table.Cold(w) is not { } wc || wc.Medical is { Incapacitated: true } || wc.InCombat
                || wc.Fleeing || !wc.CarriedBy.IsNone)
            return End(world, j, CaptiveEventKind.GaveUp);
        if (j.Age > Settings.GiveUpSeconds) return End(world, j, CaptiveEventKind.GaveUp);
        if (j.Kind is CaptiveJobKind.Gather or CaptiveJobKind.Deliver or CaptiveJobKind.CageSelf)
            return StepLogistics(world, j, w, wc);
        if (j.Kind is CaptiveJobKind.Capture or CaptiveJobKind.Carry or CaptiveJobKind.Shout)
            return StepSlaveWork(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.EatBody) return StepEatBody(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.EatCrops) return StepEatCrops(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.Worship) return StepWorship(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.DogPlay) return StepPlay(world, j, w, wc);
        if (j.Kind == CaptiveJobKind.DogChase) return StepChase(world, j, w);
        if (j.Kind == CaptiveJobKind.Process && j.Cage.Length == 0) return StepProcessLoose(world, j, w, wc);
        var cage = world.Fixtures.Find(j.Cage);
        if (cage is null || !table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { } cc || cc.InWhat != cage.Id || !cc.IsImprisoned
                || cc.Medical is not { Dead: false })
            return Over(world, j, table.TryResolveNext(j.Captive, out int gone) && table.Cold(gone)?.Medical is { Dead: true });
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
                    if (cc.Medical.Incapacitated) TaskStart.Stand(table, c);
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
                    if (j.Time < Settings.ReleaseSeconds) return true;
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
                if (!walking && ++j.Walks > Settings.Walks)
                {
                    SetWait(_givenUp, j.Captive, world.GameHours + Settings.GiveUpHours);
                    return End(world, j, CaptiveEventKind.GaveUp);
                }
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
    /// The walk to the cage: within its <c>use range</c> + <see cref="CaptiveSettings.Reach"/> it has arrived, or when a walk ended within
    /// <see cref="CaptiveSettings.Slack"/>; a walk that ended farther counts against <see cref="CaptiveSettings.Walks"/>. Its own faction's shut
    /// doors on the way are unlocked first (the lock rounds' "keys", engine choice). False: give up.
    /// </summary>
    bool Walk(World world, CaptiveJob j, int w, Fixture cage, out bool arrived)
    {
        var table = world.Characters;
        ref var n = ref table.Next[w];
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(cage.Position.X, cage.Position.Z));
        bool walking = WalksOwn(table, w, j);
        arrived = d <= cage.UseRange + Settings.Reach || !walking && j.Walks > 0 && d <= Settings.Slack;
        if (arrived || walking) return true;
        if (++j.Walks > Settings.Walks)
        {
            world.Fixtures.MarkUnreachable(world, cage, j.Faction);
            return false;
        }
        if (world.Fixtures.IsShut(cage.Building) && world.Fixtures.DoorOwner(cage.Building) == j.Faction)
            world.Fixtures.SetDoorsLocked(cage.Building, false);
        TaskStart.Walk(world, w, new Vector2(cage.Position.X, cage.Position.Z), SpeedMode.Walk, RowOf(j.Kind), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// The meals of 230 and 233 (<c>AI::findCorpse_onGround</c> <c>FUN_1408cfde0</c> / <c>AI::findDeadOrKOBody_onGround</c> <c>FUN_1408ce680</c>,
    /// docs/game/ai-tasks.md), the nearest within the eater's sight (engine reading of its sensed list): 230 a dead body of a <c>carriable</c> race
    /// lying down, for an animal eater not a robot and not of its own race; 233 a dead or downed one not of an allied faction, not a robot and not of
    /// the eater's race. Not carried, not caged, and no other job's. -1: none. </summary> <summary> Whether a job holds <paramref name="id"/> as its
    /// captive. A plain loop: a lambda capturing the id made a closure per character the scan looked at.
    /// </summary>
    bool IsJobCaptive(CharacterId id)
    {
        foreach (var j in _jobs)
            if (j.Captive == id) return true;
        return false;
    }

    int BodyToEat(World world, int slot, CharacterCold cold, bool dead)
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
    /// body until its parts are gone and the passes run out, when it is removed; a living body until it dies. Carried away, caged or gone: over.
    /// </summary>
    bool StepEatBody(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { Medical: { } m, Race: { } } cc || !cc.CarriedBy.IsNone
                || cc.InSomething != HeldState.None
            || !m.Dead && !m.Incapacitated)
            return End(world, j, CaptiveEventKind.GaveUp);
        ref var n = ref table.Next[w];
        var body = table.Next[c].Position;
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(body.X, body.Z));
        if (j.Phase == CaptivePhase.Walk)
        {
            bool walking = WalksOwn(table, w, j);
            if (d > Settings.Reach && !(!walking && j.Walks > 0 && d <= Settings.Slack))
            {
                if (walking) return true;
                if (++j.Walks > Settings.Walks) return End(world, j, CaptiveEventKind.GaveUp);
                TaskStart.Walk(world, w, new Vector2(body.X, body.Z), SpeedMode.Run, RowOf(j.Kind), TaskSource.Goal);
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
        if (!Eat(world, j, c, cc, world.TickSeconds)) return true;
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
    /// The finder of 232 (<c>FUN_1408cb810</c>, docs/game/ai-tasks.md "Eating crops"): in the town the eater stands in, whose faction it does not get
    /// along with (engine reading of the coexist test: an enemy by the relations), a farm with food
    /// (<see cref="Production.ProductionSystem.HasFood"/>) drawn uniformly at random. Only the player's farms are machines in Meitou, so only a
    /// player town has any. Null: none.
    /// </summary>
    string? CropsToEat(World world, int slot, CharacterCold cold, Blackboard board)
    {
        var town = AiSystem.TownAt(data, world, world.Characters.Next[slot].Position);
        if (town.PlayerTown <= 0 || world.Player.Faction < 0 || !data.Relations.Contains(cold.Faction)
                || !AggroSystem.IsEnemy(data.Relations, cold.Faction, world.Player.Faction))
            return null;
        List<string>? farms = null;
        foreach (var m in world.Machines.All)
            if (m.Farm is { } f && Production.ProductionSystem.HasFood(f) && world.Buildings.Find(m.Site) is { Complete: true } site
                    && site.Town == town.PlayerTown
                && !_jobs.Any(o => o.Kind == CaptiveJobKind.EatCrops && o.Cage == m.Site))
                (farms ??= []).Add(m.Site);
        if (farms is null) return null;
        return farms[Rng.Int(Rng.Hash(world.Seed, (ulong)Rng.Key(world.Characters.IdOf(slot)) ^ (ulong)world.Tick, RngPurpose.Slow, CropSalt),
                farms.Count)];
    }

    const ulong CropSalt = 0x4372_6F70_4561_7421;

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
                if (++j.Walks > Settings.Walks) return End(world, j, CaptiveEventKind.GaveUp);
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

    // How near the farm's origin an eater stops (engine choice: the original walks to the farm's work point, getPositionMarker).
    const float CropReach = 30;

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
    /// and the character cries event 70 (leg) or 71 (arm).
    /// </summary>
    static void ApplyBodyEvents(World world, CharacterId captive, int c, MedicalState medical, List<MedicalEvent> events)
    {
        foreach (var e in events)
        {
            if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut) TaskStart.Stand(world.Characters, c);
            else if (e.Kind == MedicalEventKind.PartSevered)
            {
                world.Characters.LimbsChanged.Enqueue((c, medical.Parts[e.Part].Template.LimbSlot));
                world.Defer(new Dialogue.DialogueEvent(captive, CharacterId.None, medical.Parts[e.Part].Template.LimbSlot is 2 or 3 ? 70
                        : 71) { Tick = world.Tick + 1 });
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
            _xp.Continuous(stats, cc.Race!, StatsEnumerated.Toughness, dt, EatToughnessXp);
        float rate = MedicalState.EatRate(cc.Stats?.Toughness ?? 0, cc.Stats?.Scale ?? 1);
        bool vampiric = table.TryResolveNext(j.Worker, out int w) && table.Cold(w)?.Race is { Vampiric: true };
        bool eaten = medical.BeingEaten(rate, dt, ctx, data.BodyOptions.Dismemberment != Dismemberment.Never, events, vampiric);
        ApplyBodyEvents(world, j.Captive, c, medical, events);

        // "{1} has been eaten alive." for the player's own (FUN_140856bc0: of the player's faction, or of a squad that is).
        if (eaten
                && (cc.IsPlayer || world.Player.Faction >= 0 && cc.Faction == world.Player.Faction)) world.Post(Loc.Tr("{1} has been eaten alive.",
                        cc.Name));
        return wasDead ? eaten : medical.Dead;
    }

    /// <summary>
    /// The processing of a new slave once the kneel is over (<c>Task_ProcessNewSlave</c>, <b>Verified</b>): every item of the whole inventory of
    /// record type WEAPON, then ARMOUR, ITEM and CONTAINER goes to the worker (its inventory, else the ground; engine choice for the original's
    /// storage), except locked ones and ARMOUR whose <c>stigma</c> is 6 (slave garments); a backpack goes with its content. Only when the captive's
    /// legs slot is empty is it dressed: each of its faction's <c>slave clothing</c> it does not wear already. Event 35, or 37 for one whose head is
    /// shaven already, from the worker to it, and its head is shaven; it becomes the worker's faction's slave unless it has an owner already (it
    /// keeps that one), and its one-shot orders and perma-jobs are cleared.
    /// </summary>
    void Process(World world, int w, CharacterCold wc, int c, CharacterCold cc)
    {
        var table = world.Characters;
        bool before = cc.SlaveOwner >= 0 || cc.SlaveState != 0;
        if (cc.Inventory is { } inv)
        {
            foreach (var type in Stripped)
                foreach (var item in inv.Items.ToList())
                {
                    if (item.Type != type || item.LockedOn || type == FcsRecordType.Armour
                            && data.Db.Find(item.Record)?.GetInt("stigma", 0) == SlaveStigma)
                        continue;
                    inv.Items.Remove(item);
                    item.Section = "main";
                    if (wc.Inventory is not { } into || !into.Add(item))
                        world.GroundItems.Drop(item, table.Next[c].Position, 0);
                }
            if (!inv.Items.Any(i => i.Section == "legs") && data.Factions is { } factions && (uint)wc.Faction < (uint)factions.Count
                    && data.Db.Find(factions[wc.Faction].Id) is { } faction)
                foreach (var link in faction.GetReferences("slave clothing"))
                {
                    if (inv.Items.Any(i => i.Record == link.TargetStringId && InventoryCommands.IsEquipmentSection(i.Section)))
                        continue;
                    if (data.Items.Create(link.TargetStringId, 1) is { } clothing)
                        InventoryCommands.TryEquip(inv, clothing, cc);
                }
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
        // toward the owner (a player character's list; the AI's slaves run it as AiSystem.ObeyedRoles).
        ObedientSlave.Reset(world, cc);

        // Then its head is shaven (CharacterHuman::shaveHead(true) in FUN_14035aa20, Verified); an animal has no head to shave.
        if (cc.Fighter is not { IsAnimal: true }) cc.Shaved = true;
        world.SystemOf<Dialogue.DialogueSystem>()?.Forgive(world, w, table.IdOf(c));
    }

    /// <summary>
    /// The record types a new slave is stripped of, in the original's order.
    /// </summary>
    static readonly FcsRecordType[] Stripped = [FcsRecordType.Weapon, FcsRecordType.Armour, FcsRecordType.Item, FcsRecordType.Container];
    /// <summary>
    /// The ARMOUR <c>stigma</c> of slave garments, which processing leaves on.
    /// </summary>
    const int SlaveStigma = 6;

    /// <summary>
    /// The captive died or is gone: a death resets the town's kill timer (<c>FUN_140353990</c>, the end of both the kill and the meal).
    /// </summary>
    bool Over(World world, CaptiveJob j, bool died)
    {
        if (died && j.Site >= 0) _killAt[j.Site] = NextKill(world, j.Site);
        return End(world, j, died ? j.Kind == CaptiveJobKind.Eat ? CaptiveEventKind.Eaten : CaptiveEventKind.Killed : CaptiveEventKind.GaveUp);
    }

    /// <summary>
    /// Ends a job: the worker goes back to the AI.
    /// </summary>
    bool End(World world, CaptiveJob j, CaptiveEventKind? kind)
    {
        var table = world.Characters;
        if (table.TryResolveNext(j.Worker, out int w) && table.Cold(w) is { } wc)
        {
            wc.Engaged = false;
            wc.ActionClip = null;
            if (table.Next[w].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, w);
        }
        if (kind is { } k) Note(world, k, j);
        return false;
    }

    void Note(World world, CaptiveEventKind kind, CaptiveJob j)
    {
        if (Settings.RecordLog) _log.Add(new CaptiveEvent(world.Tick, kind, j.Kind, j.Worker, j.Captive));
    }

    public void Hash(ref StateHasher hasher)
    {
        if (_jobs.Count == 0 && _killAt.Count == 0 && _givenUp.Count == 0) return;   // only once in use, so worlds without captives hash as before
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
        if (_givenUp.Count > 0) HashWaits(ref hasher);
    }
}