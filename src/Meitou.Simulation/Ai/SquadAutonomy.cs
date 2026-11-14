using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Lets the AI run a player's squad, or takes it back (<see cref="Squad.Autonomous"/>, docs/simulation.md "Autonomous player squads"). Turning it
/// off drops the squad's packages and its members' minds; a squad that is not the player's is left alone. Meitou only: the original's AI plans for
/// no player squad.
/// </summary>
public sealed record SetSquadAutonomy(int SquadId, bool On) : SimCommand;

/// <summary>
/// Gives a player's squad its home (<see cref="Squad.AssignedHome"/>): a town building's <see cref="TownBuilding.Key"/> or a player-built
/// building's id (<see cref="BuildSite.Id"/>); null: none. A town building also makes its town the squad's (<see cref="Blackboard.Town"/>).
/// </summary>
public sealed record SetSquadHome(int SquadId, string? Building) : SimCommand;

/// <summary>
/// The player's squads the AI runs (docs/simulation.md "Autonomous player squads"): the switch and home commands, the home's building, and the
/// rules that keep the AI below the player's orders and jobs. A member of such a squad gets a mind of its package's goals
/// (<see cref="AiMind.Player"/>); the AI holds it only while no order, job or reaction runs its task (<see cref="Free"/>), an urgent package goal
/// takes it off a job through the decision step (<see cref="PackageGoals"/>), and a job takes it back from any other goal
/// (<see cref="Yields"/>), as the original's chooser orders them (docs/game/ai-tasks.md "Precedence").
/// </summary>
public sealed class SquadAutonomy(PopulationData data)
{
    #region Commands

    /// <summary>
    /// Applies a switch or home command; false for any other command. <paramref name="squad"/> is the player's squad it named (null: none).
    /// </summary>
    public bool Apply(World world, SimCommand command, out Squad? squad)
    {
        squad = null;
        switch (command)
        {
            case SetSquadAutonomy a:
                squad = PlayerSquad(world, a.SquadId);
                if (squad is not null) Switch(world, squad, a.On);
                return true;
            case SetSquadHome h:
                squad = PlayerSquad(world, h.SquadId);
                if (squad is not null) GiveHome(world, squad, h.Building);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The player's squad with id <paramref name="id"/> (not the squad of the dead); null when there is none.
    /// </summary>
    Squad? PlayerSquad(World world, int id)
    {
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        return world.Squads.Find(id) is { Dead: false } squad && player >= 0 && squad.Faction == player ? squad : null;
    }

    /// <summary>
    /// Turns the squad's autonomy on or off; off drops its board, its saved AI and its members' minds (<see cref="Release"/>).
    /// </summary>
    static void Switch(World world, Squad squad, bool on)
    {
        if (squad.Autonomous == on) return;

        squad.Autonomous = on;
        if (on) return;

        DropMinds(world, squad);
        squad.Ai = null;
        squad.SavedAi = null;
    }

    /// <summary>
    /// Notes the squad's home and, when its board is made, moves it there.
    /// </summary>
    void GiveHome(World world, Squad squad, string? building)
    {
        squad.AssignedHome = string.IsNullOrEmpty(building) ? null : building;
        if (squad.Ai is { } board) Settle(world, squad, board);
    }

    #endregion

    #region Minds

    /// <summary>
    /// The minds of a player's squad the AI does not run, and those its members brought from another squad, go (<see cref="Release"/>).
    /// </summary>
    internal static void DropMinds(World world, Squad squad)
    {
        var table = world.Characters;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true, Ai.Player: true } cold) continue;
            if (squad is { Autonomous: true, Ai: not null } && ReferenceEquals(cold.Ai.Squad, squad)) continue;

            Release(world, slot, cold);
        }
    }

    /// <summary>
    /// Takes a character's mind away; a task the AI started for it ends (it stands, and the decision step decides it again), but one lying in a
    /// bed lies on.
    /// </summary>
    static void Release(World world, int slot, CharacterCold cold)
    {
        var mind = cold.Ai!;
        var task = (CharacterTask)world.Characters.Next[slot].Task;
        bool own = cold.Running is { } run && run.Runs(task) && (mind.Owns(run) || AiSystem.OwnsFight(cold, mind));
        cold.Ai = null;
        if (own && cold.InSomething != Locks.HeldState.Bed) TaskStart.End(world, slot, TaskEnd.Stopped);
    }

    /// <summary>
    /// Whether the mind is its character's own squad's and that squad is autonomous (a character moved to another squad keeps its mind until the
    /// next package step).
    /// </summary>
    static bool Active(CharacterCold cold, AiMind mind) => mind.Player && mind.Squad.Autonomous && cold.SquadId == mind.Squad.Id;

    /// <summary>
    /// Whether the AI may hold a player character (<see cref="AiSystem.Holds"/> goes on to its own tests): its squad is autonomous and no plan
    /// runs its task, or the plan is 14 Idleness's or one the think started (<see cref="AiMind.Owns"/>, its own fight). An order's, a job's, a
    /// reaction's or another goal's task is not the AI's.
    /// </summary>
    internal static bool Free(in CharacterHot n, CharacterCold cold, AiMind mind)
    {
        if (!Active(cold, mind)) return false;
        if (cold.Running is not { } run || !run.Runs((CharacterTask)n.Task)) return true;

        return run.Source == TaskSource.Goal && (run.Row == TaskType.Idle || mind.Owns(run) || AiSystem.OwnsFight(cold, mind));
    }

    /// <summary>
    /// Whether a player character of an autonomous squad is on a job's task and able: the think then weighs its urgent package goals only
    /// (<see cref="AiSystem.Restrict.Urgent"/>).
    /// </summary>
    internal static bool OnJob(in CharacterHot n, CharacterCold cold, AiMind mind) =>
        Active(cold, mind) && cold is { InCombat: false, Fleeing: false, IsImprisoned: false } && cold.CarriedBy.IsNone && cold.Carrying.IsNone
        && cold.Medical is not { Incapacitated: true } && cold.Running is { Source: TaskSource.Job } run && run.Runs((CharacterTask)n.Task);

    /// <summary>
    /// Whether the jobs come before the task <paramref name="run"/> runs for a player character (<see cref="DecisionSystem.GoalsComeFirst"/>): the
    /// AI's own task for a package goal that is not urgent (the chooser's step 5 comes before its priority-2 and priority-1 goals).
    /// </summary>
    internal static bool Yields(CharacterCold cold, RunningTask run) => cold.Ai is { Player: true, Urgent: false } mind && mind.Owns(run);

    /// <summary>
    /// Whether another start has taken a player character's task since the think started its walk (an order, a job): what ended since is then not
    /// that walk.
    /// </summary>
    internal static bool WalkTaken(CharacterCold cold, AiMind mind) => mind.Player && (cold.Running is not { } run || !mind.Owns(run));

    /// <summary>
    /// The think started a task for a player character: its tick and row are kept, so the task counts as the AI's own (<see cref="AiMind.Owns"/>).
    /// The character's own slot.
    /// </summary>
    internal static void Stamp(World world, int slot)
    {
        if (world.Characters.Cold(slot) is { Ai: { Player: true } mind, Running: { } run }) (mind.OwnTick, mind.OwnRow) = (run.Tick, run.Row);
    }

    #endregion

    #region Homes

    /// <summary>
    /// The buildings made for the player-built homes, by site id: one object per home, as the boards hold their homes by reference. Derived from
    /// the sites, not hashed.
    /// </summary>
    readonly Dictionary<string, TownBuilding> _built = new(StringComparer.Ordinal);

    /// <summary>
    /// Puts the squad's assigned home on its board: its own home, the home in use unless a package moved that, and its town; the members think
    /// again. A home that no longer exists is forgotten; a player-built one not complete counts as none until it is. Nothing when unchanged.
    /// </summary>
    public void Settle(World world, Squad squad, Blackboard board)
    {
        var (home, town) = Resolve(world, squad);
        if (ReferenceEquals(board.OwnHome, home) && board.Town == town) return;

        var old = board.OwnHome;
        board.OwnHome = home;
        if (ReferenceEquals(board.Home, old)) board.Home = home;

        board.Town = town;
        board.GateHome = null;
        board.Touch();
        Rethink(world, squad);
    }

    /// <summary>
    /// The package step's look at an autonomous squad's home: a player-built one may have been finished, ruined or removed since.
    /// </summary>
    public void Refresh(World world, Squad squad, Blackboard board)
    {
        if (squad.AssignedHome is null && board.OwnHome is null) return;
        if (board.OwnHome is null or { RuntimeKey: not null }) Settle(world, squad, board);
    }

    /// <summary>
    /// The members' minds try the home's spots again and think at the next tick.
    /// </summary>
    static void Rethink(World world, Squad squad)
    {
        foreach (var id in squad.Members)
        {
            if (!world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot)?.Ai is not { } mind) continue;

            mind.HomeUnreachable = false;
            mind.Replan = true;
        }
    }

    /// <summary>
    /// The building and town (a site index; -1: none) of the squad's assigned home: a key with a zone is a town building's, any other a
    /// player-built building's id. An id or key nothing answers to is forgotten.
    /// </summary>
    (TownBuilding? Home, int Town) Resolve(World world, Squad squad)
    {
        if (squad.AssignedHome is not { } key) return (null, -1);

        int colon = key.IndexOf(':');
        if (colon >= 0)
        {
            var placed = TownBuildingOf(key, colon);
            if (placed.Home is null) squad.AssignedHome = null;

            return placed;
        }

        if (world.Buildings.Find(key) is not { } site) squad.AssignedHome = null;
        else if (site.Complete) return (Built(site), -1);

        return (null, -1);
    }

    /// <summary>
    /// The town building with run-time name <paramref name="key"/> (its instance id starts after <paramref name="colon"/>) and its town's site.
    /// </summary>
    (TownBuilding? Home, int Town) TownBuildingOf(string key, int colon)
    {
        var instance = key.AsSpan(colon + 1);
        for (int s = 0; s < data.Sites.Count; s++)
        {
            foreach (var b in data.Sites[s].Buildings)
                if (instance.SequenceEqual(b.InstanceId) && b.Key == key) return (b, s);
        }

        return (null, -1);
    }

    /// <summary>
    /// A player-built building as a home: its place and facing, known by its id (<see cref="TownBuilding.RuntimeKey"/>, as its doors and beds
    /// know it), with no spots of its own (its beds are buildings placed in it, <see cref="HomeBed"/>; it has no nodes).
    /// </summary>
    TownBuilding Built(BuildSite site)
    {
        if (_built.TryGetValue(site.Id, out var known)) return known;

        float yaw = TownBuildings.YawOf(site.Rotation);
        return _built[site.Id] = new TownBuilding(site.Id, site.Record, site.Name, site.Position, yaw, false, false, [])
        {
            RuntimeKey = site.Id,
            Zone = site.Zone,
            HasInterior = true,
        };
    }

    /// <summary>
    /// The bed a player character takes in a home without bed spots of its own (a player-built building, a town house furnished by the player):
    /// the complete beds placed in it (<see cref="BuildSite.Parent"/>) in creation order, one per member; null for an NPC, or for a member past
    /// its beds (the bed search stands in, <see cref="Locks.FixtureRegistry.FindBed"/>).
    /// </summary>
    internal static Locks.Fixture? HomeBed(World world, TownBuilding home, AiMind mind)
    {
        if (!mind.Player) return null;

        string id = home.Key;
        int left = mind.MemberIndex;
        foreach (var site in world.Buildings.All)
        {
            if (site.Parent != id || !site.Complete || world.Fixtures.Find(site.Id + "@bed0") is not { Kind: Locks.FixtureKind.Bed } bed) continue;
            if (left-- == 0) return bed;
        }

        return null;
    }

    #endregion

    #region Decision step

    /// <summary>
    /// The decision step's source for an autonomous squad's urgent package goals (priority 3, step 4): it can run while the character's think
    /// wants one (<see cref="AiMind.Urgent"/>), and counts as running once the character is off its job, so the jobs (step 5) do not take it back.
    /// Its start only frees the character (it stands as 14 Idleness); the next think chooses and acts the goal out. <b>Engine choice</b>: one
    /// source of weight 0.5 and desire 1 for whichever goal the think picks, so Self preservation (1 or 0.7) still wins.
    /// </summary>
    internal sealed class PackageGoals : IDecisionSource
    {
        /// <summary>
        /// The trace name.
        /// </summary>
        public string Name => "Package goals";
        /// <summary>
        /// Step 4, the priority-3 goals.
        /// </summary>
        public DecisionStep Step => DecisionStep.Goals;
        /// <summary>
        /// Listed after the numbered goals.
        /// </summary>
        public int Key => 1000;
        /// <summary>
        /// Its weight (engine choice).
        /// </summary>
        public float Weight => 0.5f;

        /// <summary>
        /// Whether the character's think wants an urgent package goal: it starts for one on a job, and runs for any other.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            var cold = world.Characters.Cold(slot)!;
            if (cold.Ai is not { Urgent: true } mind || !Active(cold, mind)) return DecisionCheck.No("no urgent package goal");

            return OnJob(world.Characters.Next[slot], cold, mind) ? DecisionCheck.Run : DecisionCheck.Runs;
        }

        /// <summary>
        /// Takes the character off its job: it stands idle and thinks at the next tick.
        /// </summary>
        public void Start(World world, int slot)
        {
            TaskStart.Become(world, slot, CharacterTask.Idle, TaskType.Idle, TaskSource.Goal);
            if (world.Characters.Cold(slot)?.Ai is { } mind) mind.Replan = true;
        }
    }

    #endregion
}
