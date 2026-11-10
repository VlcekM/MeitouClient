using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Movement;

/// <summary>
/// Movement on the walkability stand-in (docs/game/pathfinding.md "Movement") and the first tasks: wander within the squad's home
/// radius, go to (move orders), follow the squad leader.
/// <list type="bullet">
/// <item>Speed: the stat S in decimetres (world units) per second, capped by the speed mode (walk speed, 55, none); the character follows a path at
/// that speed at once (the nav agent, Observed), speeds up by 15 units/s per second only in combat (<see cref="Acceleration"/>, the combat steering),
/// and stops at once on arrival.</item>
/// <item>Separation: the repulsion along self-other is nonzero only closer than R and proportional to 100 x (1 - d/R), over at most
/// 26 neighbours (Observed); R is twice the footprint radius 4 of a human (<see cref="SeparationRadius"/>), and the scale from that
/// proportionality to a speed is an engine choice (<see cref="SeparationStrength"/>).</item>
/// <item>Body contact (engine choice; the original only avoids the standing bodies round a character, no physical collision was found:
/// docs/game/pathfinding.md "Bodies in the way"): two standing bodies (<see cref="BodyShare"/> of their footprints) do not stay overlapped. Each
/// takes half of the overlap between its stepped position and the other's last position, averaged over the bodies it overlaps, so a walker shoves a
/// stander aside; an imprisoned or bedded one does not give. Its own velocity into a body it already touches is dropped, so a crowd pressing on does
/// not squash its front. A sitter is never moved, by contact or separation (the original pushes no sitter, Observed), and is not solid to others.
/// Downed, dead and carried bodies neither push (separation) nor block (contact, the walk's covered goal, root motion): the original's avoidance list
/// leaves them out (docs/game/pathfinding.md "Bodies in the way").</item>
/// <item>Paths: a character that wants one is flagged in Think; the Schedule step of the next tick asks the <see cref="PathService"/>;
/// an answer is applied in a serial step, so a path is never read while it is replaced.</item>
/// </list>
/// Water states cap or replace the top speed (<see cref="WaterStateAt"/>, <see cref="Speed.InWater"/>). In water a fighter's footwork is scaled too
/// (x0.75 / 0.5 / 0.4). Not modelled yet (Unknown or later): slopes (the original has no slope penalty), the RACE <c>combat move speed mult</c>. Far
/// trips take the roads in the path layer (<see cref="CharacterCold.RoadPreference"/>).
/// <item>Followers (the original's <c>Task_Follow</c>, docs/game/pathfinding.md "Squads travelling together"): a squad mate aims at its
/// <see cref="Formation.Layout"/> slot turned onto a heading that eases toward the leader's, one second ahead of the leader; anyone else keeps a
/// drawn 20..40 from its target. Its speed is the original's desired speed (<see cref="FollowSpeedFor"/>), capped by its top speed only (engine
/// choice: the original's cap by speed mode is Unknown, and only this lets it settle in its slot). Within <see cref="DirectDistance"/> it steers
/// straight at the aim, else it takes paths, and for <see cref="FollowPathSeconds"/> after a blocked step (engine choice). Not done: the original's
/// aim outside a building the follower cannot enter.</item>
/// <item>Turning: a walker out of combat faces the way it moves while its velocity follows the agent's acceleration (the original's model, Verified);
/// its drawn body turns after the facing at a limited rate (<see cref="AnimationSystem.FollowFacing"/>). Fighters and direct followers keep the
/// heading limit <see cref="TurnRate"/> and the cosine cut (engine choice).</item> Queued move orders (row-29 entries of
/// <see cref="CharacterCold.OrderQueue"/>) and the formation (<see cref="Formation"/>) exist.
/// </summary>
public sealed partial class MovementSystem(PathService paths) : ITickSystem, IDisposable, ITaskOwner, ITaskHandler, IOrderOwner
{
    #region Orders and tasks

    /// <summary>
    /// How far above or below the given height the floor <see cref="GoToFloor"/> walks onto may be (engine choice: a storey is about 66).
    /// </summary>
    public const float FloorSlack = 20;

    /// <summary>
    /// Takes the path answers in, then this tick's movement commands (repaths, sandbox settings, follow and move orders), and rebuilds the squads'
    /// formations that changed (serial).
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        ApplyAnswers(world);
        var table = world.Characters;

        foreach (var command in commands)
        {
            if (command is RepathCommand repath) Repath(table, repath);
            else if (command is SandboxMovement sandbox) ApplySandbox(table, sandbox);
            else if (command is FollowOrder follow) GiveFollow(world, follow);
            else if (command is MoveOrder order) GiveMove(world, order);
        }

        foreach (var squad in world.Squads.All)
            RefreshFormation(world, squad);
    }

    /// <summary>
    /// Registers the walk, follow, idle and wander tasks.
    /// </summary>
    public void RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(CharacterTask.GoTo, this);
        tasks.Register(CharacterTask.Follow, this);
        tasks.Register(CharacterTask.Idle, this);
        tasks.Register(CharacterTask.Wander, this);
    }

    /// <summary>
    /// A move order (29) needs an awake character (one out cold keeps it queued until it wakes); a follow order (31, 45) a target that is there, and
    /// an awake, free character (not caged or carried).
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        if (order.IsMove)
            return table.Cold(slot)!.Medical?.Incapacitated == true ? OrderCheck.Blocked() : OrderCheck.Run;
        if (!table.TryResolveNext(order.Target, out int led) || led == slot) return OrderCheck.Gone;

        bool free = table.Cold(slot) is { IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true };
        return free ? OrderCheck.Run : OrderCheck.Blocked();
    }

    /// <summary>
    /// A sleeper gets up first; then the move's walk or the follow starts (<see cref="ITaskHandler.Start"/>).
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        if (table.Cold(slot)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, table.IdOf(slot));

        TaskStart.Start(world, slot, order.IsMove
            ? new TaskRequest(CharacterTask.GoTo, TaskType.MoveCusOrdered, TaskSource.Order) { Point = order.Point }
            : new TaskRequest(CharacterTask.Follow, order.Row, TaskSource.Order) { Target = order.Target });
    }

    /// <summary>
    /// A move order's walk (row 29: <see cref="CharacterCold.OrderMove"/>, the Hold point follows it) to <see cref="TaskRequest.Point"/>'s floor, or
    /// a follow of <see cref="TaskRequest.Target"/> (<see cref="CharacterCold.FollowOrder"/>; row 45 guards it). An order's follow breaks a circle of
    /// followers back to the character (<see cref="BreakCircle(World, CharacterId)"/>). An engine walk goes to
    /// <see cref="TaskRequest.Goal"/> as given (<see cref="GoTo"/>); an engine's idle, wander or follow of the squad's leader (no target) stops the
    /// character on it.
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;

        if (request.Task == CharacterTask.GoTo)
        {
            if (request.Goal is { } goal)
            {
                GoTo(table, slot, goal, request.Mode ?? cold.RunSpeed);
                return;
            }

            GoToFloor(world, slot, request.Point, request.Mode ?? cold.RunSpeed);
            cold.OrderMove = request.Row == TaskType.MoveCusOrdered;
            return;
        }

        Stop(table, slot);

        if (request.Task != CharacterTask.Follow || request.Target.IsNone)
        {
            // A standing task, or following the squad's leader (an engine start): the character stops and takes it up.
            table.Next[slot].Task = (byte)request.Task;
            return;
        }

        cold.FollowOrder = request.Target;
        cold.FollowPathTime = 0;
        cold.FollowHeading = Vector2.Zero;
        table.Next[slot].Task = (byte)CharacterTask.Follow;
        table.Next[slot].TaskTime = 0;
        if (request.Source == TaskSource.Order) BreakCircle(world, table.IdOf(slot));
    }

    /// <summary>
    /// A walk or a follow holds nothing: the next start sets the movement (the follow's <see cref="CharacterCold.FollowOrder"/> stays for the task it
    /// goes back to).
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason) { }

    /// <summary>
    /// <see cref="GoTo"/> onto the floor at <paramref name="at"/>'s height (serial phases only). Furniture cuts holes in a building's floor mesh and
    /// a goal there resolves to the storey above or the ground below, so the goal moves to the nearest floor point
    /// (<see cref="IWalkability.FloorNear"/>) and keeps its height. Returns the X/Z goal.
    /// </summary>
    public static Vector2 GoToFloor(World world, int slot, Vector3 at, byte mode)
    {
        var floor = world.Walkability.FloorNear(at.X, at.Z, at.Y, FloorSlack) ?? at;
        var flat = new Vector2(floor.X, floor.Z);
        GoTo(world.Characters, slot, flat, mode);
        var cold = world.Characters.Cold(slot)!;
        (cold.GoalHeight, cold.GoalHeightAt) = (floor.Y, flat);
        return flat;
    }

    /// <summary>
    /// Sends a character to a point at a speed mode (serial phases only). A path it is following is kept until the new one
    /// arrives, so a new order given on the move (holding the right button) does not stop it for the ticks the path takes.
    /// </summary>
    public static void GoTo(CharacterTable table, int slot, Vector2 target, byte mode)
    {
        ref var n = ref table.Next[slot];
        var cold = table.Cold(slot)!;
        cold.OrderMove = false;
        cold.ArriveWithin = 0;
        cold.BlockedRepaths = 0;
        cold.BlockedRetryTick = 0;
        cold.WalkEnd = WalkEnd.None;
        n.Task = (byte)CharacterTask.GoTo;
        n.Goal = target;
        n.Mode = mode;
        n.Flags = (ushort)((n.Flags & ~(ushort)MoveFlags.Pending) | (ushort)MoveFlags.NeedPath);
        cold.PathRequest = 0;
        cold.SplicePath = null;
    }

    /// <summary>
    /// <see cref="FollowOrder"/>: each awake, free character (not the target) drops what it does and follows the target
    /// (<see cref="CharacterCold.FollowOrder"/>); a sleeper gets up first. The order is row 45 Bodyguard or 31 Staying close; a job's follow
    /// (<paramref name="source"/> <see cref="TaskSource.Job"/>) runs the job's <paramref name="row"/>.
    /// </summary>
    internal static void Follow(World world, FollowOrder order, TaskSource source = TaskSource.Order, int job = -1, TaskType? row = null)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int led)) return;

        foreach (var id in order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray())
        {
            if (!table.TryResolveNext(id, out int s) || s == led
                || table.Cold(s) is not { IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true } cold)
            {
                continue;
            }

            if (cold.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, id);

            var followRow = row ?? (order.Guard ? TaskType.Bodyguard : TaskType.StayCloseToTarget);
            TaskStart.Start(world, s, new TaskRequest(CharacterTask.Follow, followRow, source) { Job = job, Target = order.Target });
        }
    }

    /// <summary>
    /// A follow row (31, 45, 289) whose fight was a step of it (<see cref="TaskStart.Start"/> keeps the row's provenance over a goal's or reaction's
    /// fight) is over: the character stands idle out of the fight and its provenance still names the row with the fight as its task.
    /// </summary>
    internal static bool FightStepOver(CharacterTable table, int slot, CharacterCold cold) =>
        cold.Running is { Task: CharacterTask.Attack } run && Production.PermaJobs.Follows(run.Row)
        && table.Next[slot].Task == (byte)CharacterTask.Idle && !cold.InCombat;

    /// <summary>
    /// The follow row takes its follow up again where its fight left it (serial; the decision step, for an order or a job: the original's plan goes
    /// on with the row's follow after 13): its walk is asked anew under the row's provenance. False, with the row dropped (no provenance, no
    /// <see cref="CharacterCold.FollowOrder"/>), when the followed character is gone.
    /// </summary>
    internal static bool FollowAgain(World world, int slot, CharacterCold cold)
    {
        var table = world.Characters;

        if (cold.FollowOrder.IsNone || !table.TryResolveNext(cold.FollowOrder, out _))
        {
            cold.Running = null;
            cold.FollowOrder = CharacterId.None;
            return false;
        }

        cold.Running = cold.Running! with { Task = CharacterTask.Follow };
        TaskStart.Step(table, slot, CharacterTask.Follow);
        table.Next[slot].TaskTime = 0;
        cold.FollowPathTime = 0;
        return true;
    }

    /// <summary>
    /// The original drops what would close a circle back to the character just ordered to follow or guard (<paramref name="ordered"/>;
    /// <c>FUN_1405c8c10</c>, run by the order giver <c>FUN_1405c8da0</c> after it adds a one-shot order or a job 31 or 45, <b>Verified</b>;
    /// docs/game/animals.md "Following"). Walking the chain from the ordered character (a character once at most), whoever it reaches loses its
    /// one-shot follow of the ordered character and every Staying close / Bodyguard job on it; the walk goes on through the target of the one-shot
    /// follow and of each such job. The jobs are only looked at while the character has a Staying close 31 job (so an animal's 289 and its Bodyguard
    /// are not walked).
    /// </summary>
    internal static void BreakCircle(World world, CharacterId ordered)
    {
        if (world.Characters.TryResolveNext(ordered, out int start))
            BreakCircle(world, ordered, start, new List<int>(4));
    }

    /// <summary>
    /// Drops a character's path and makes it stand (serial phases only); its order queue is the queue's (<see cref="PlayerOrders"/>).
    /// </summary>
    public static void Stop(CharacterTable table, int slot)
    {
        ref var n = ref table.Next[slot];
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.Task = (byte)CharacterTask.Idle;
        n.PathCursor = 0;
        var cold = table.Cold(slot)!;
        cold.PathRequest = 0;
        cold.FollowHeading = Vector2.Zero;
        cold.WalkEnd = WalkEnd.None;   // stopped from outside: neither arrived nor failed
    }

    /// <summary>
    /// <see cref="RepathCommand"/>: the walkers whose way leads through a zone whose mesh changed splice in the new part or ask for the whole way
    /// again.
    /// </summary>
    static void Repath(CharacterTable table, RepathCommand repath)
    {
        for (int s = 0; s < table.Next.Length; s++)
        {
            ref var c = ref table.Next[s];
            if (!c.Alive || c.Task != (byte)CharacterTask.GoTo || (c.Flags & (ushort)(MoveFlags.Pending | MoveFlags.HasPath)) == 0)
                continue;

            // A walker whose way on does not lead through a changed zone keeps its path (a long walk loads zones ahead all the way, and each
            // would otherwise turn every walker about). One waiting for an answer asks again: it may have been made on the old meshes.
            if (repath.Zones is { } zones && (c.Flags & (ushort)MoveFlags.Pending) == 0
                && !Crosses(c.Position, table.Cold(s)!.Path, c.PathCursor, zones))
            {
                continue;
            }

            // One following a path splices in the part through the new meshes and walks on meanwhile (an answer it waits for may be made on
            // the old ones).
            if ((c.Flags & (ushort)MoveFlags.HasPath) != 0)
            {
                c.Flags = (ushort)(c.Flags & ~(ushort)MoveFlags.Pending);
                table.Cold(s)!.PathRequest = 0;
                AskSplice(ref c, table.Cold(s)!, repath.Zones);
                continue;
            }

            c.Flags = (ushort)((c.Flags & ~(ushort)(MoveFlags.Pending | MoveFlags.HasPath)) | (ushort)MoveFlags.NeedPath);
            c.PathCursor = 0;
            table.Cold(s)!.PathRequest = 0;
        }
    }

    /// <summary>
    /// <see cref="SandboxMovement"/>: a character's speed mode or fixed speed for a sandbox scenario.
    /// </summary>
    static void ApplySandbox(CharacterTable table, SandboxMovement sandbox)
    {
        if (table.TryResolveNext(sandbox.Character, out int target))
        {
            var tc = table.Cold(target)!;
            if (sandbox.Mode is { } mode) tc.ModeOverride = mode;

            if (sandbox.Speed is { } fixedSpeed) tc.SpeedOverride = MathF.Max(fixedSpeed, 0);
        }
    }

    /// <summary>
    /// <see cref="FollowOrder"/>: row 45 Bodyguard or 31 Staying close to each of the characters (the selection when empty) but the target.
    /// </summary>
    void GiveFollow(World world, FollowOrder follow)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(follow.Target, out int led)) return;

        foreach (var id in follow.Characters.Count > 0 ? follow.Characters : world.Player.Selection.ToArray())
        {
            if (table.TryResolveNext(id, out int s) && s != led)
            {
                var entry = new OrderEntry(follow.Guard ? TaskType.Bodyguard : TaskType.StayCloseToTarget, this) { Target = follow.Target };
                PlayerOrders.Give(world, s, entry, follow.Queued);
            }
        }
    }

    /// <summary>
    /// <see cref="MoveOrder"/>: each of the characters (the selection when empty) gets a move to its scattered point; a prisoner keeps it queued,
    /// a sleeper gets up.
    /// </summary>
    void GiveMove(World world, MoveOrder order)
    {
        var table = world.Characters;

        // An empty list orders the selection (the UI sends orders for whatever is selected when the click happens).
        var who = order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray();
        var slots = new List<int>();

        foreach (var id in who)
        {
            if (!table.TryResolveNext(id, out int found)) continue;

            if (!TaskStart.Eligible(world, table.Cold(found)!, TaskType.MoveCusOrdered))
            {
                // A prisoner stays in its cage (row 29 is not registered for the imprisoned or chained): the order waits in its queue.
                PlayerOrders.Give(world, found, new OrderEntry(TaskType.MoveCusOrdered, this) { Point = order.Target }, order.Queued);
                continue;
            }

            if (table.Cold(found)!.InSomething == Locks.HeldState.Bed)
                world.Fixtures.LeaveBed(world, id);   // an order gets a sleeper up

            slots.Add(found);
        }

        var targets = Formation.Scatter(slots.Count, new Vector2(order.Target.X, order.Target.Z));

        for (int k = 0; k < slots.Count; k++)
        {
            int slot = slots[k];
            var target = targets[k];
            table.Cold(slot)!.FollowOrder = CharacterId.None;

            // Shift + right click: after the order in hand.
            var entry = new OrderEntry(TaskType.MoveCusOrdered, this) { Point = new Vector3(target.X, order.Target.Y, target.Y) };
            PlayerOrders.Give(world, slot, entry, order.Queued);
        }
    }

    /// <summary>
    /// Rebuilds a squad's <see cref="Squad.FormationSlots"/> when its formation, head, members or their roles changed (serial). The head is the
    /// leader, or the squad mate the leader follows (the original's task-31 head). The draws are keyed by the squad and the key, so a rebuild of the
    /// same squad gives the same slots.
    /// </summary>
    static void RefreshFormation(World world, Squad squad)
    {
        var table = world.Characters;
        squad.ForceSpeed = world.ForcedSpeeds is { } forced && forced.TryGetValue(squad.TemplateId, out byte force) ? force : Squad.NoForceSpeed;
        var head = FormationHead(table, squad);
        int type = squad.Faction >= 0 && world.FactionFormations is { } formations && squad.Faction < formations.Count
            ? formations[squad.Faction]
            : (int)SquadFormation.Random;
        ulong key = Rng.Mix((ulong)type ^ Rng.Key(head) << 2);
        foreach (var m in squad.Members) key = Rng.Mix(key ^ Rng.Key(m) ^ (ulong)(table.IsAlive(m) ? table.Cold(m.Slot)!.Role : 7) << 56);
        if (key == squad.FormationKey) return;

        squad.FormationKey = key;

        if (head.IsNone || !table.IsAlive(head))
        {
            squad.FormationSlots = [];
            return;
        }

        FormationMember Of(CharacterId id)
        {
            var c = table.Cold(id.Slot)!;
            return new FormationMember(id, c.Role, c.Animal is not null,
                Formation.RadiusScale * (c.FootprintRadius > 0 ? c.FootprintRadius : CombatTuning.DefaultFootprint));
        }

        var others = new List<FormationMember>();
        var slaves = new List<FormationMember>();

        foreach (var m in squad.Members)
        {
            if (m == head || !table.IsAlive(m)) continue;

            var member = Of(m);
            (member.Role == (int)SquadRole.Slave ? slaves : others).Add(member);
        }

        ulong draws = 0, seed = Rng.Hash(world.Seed, (ulong)squad.Id, RngPurpose.Formation, key);
        squad.FormationSlots = Formation.Layout((SquadFormation)type, Of(head), others, slaves,
            () => Rng.Float(Rng.Hash(seed, 0, RngPurpose.Formation, draws++)));
    }

    /// <summary>
    /// The character a squad's formation is laid out from: its leader, or the squad mate the leader follows.
    /// </summary>
    static CharacterId FormationHead(CharacterTable table, Squad squad)
    {
        var leader = squad.Leader;
        if (leader.IsNone || !table.IsAlive(leader)) return leader;

        var followed = table.Cold(leader.Slot)!.FollowOrder;
        bool followsMate = table.Next[leader.Slot].Task == (byte)CharacterTask.Follow && !followed.IsNone && squad.Members.Contains(followed)
            && table.IsAlive(followed);
        return followsMate ? followed : leader;
    }

    /// <summary>
    /// One character of the chain walk (<see cref="BreakCircle(World, CharacterId)"/>).
    /// </summary>
    static void BreakCircle(World world, CharacterId ordered, int slot, List<int> seen)
    {
        var table = world.Characters;

        // Engine guard: the original has none on the job walk; a chain is cut at 64 characters like the one-shot walk was.
        if (seen.Count >= 64 || seen.Contains(slot)) return;

        seen.Add(slot);
        var cold = table.Cold(slot)!;

        // The one-shot follow (a running follow job's FollowOrder is the job's, handled with the jobs).
        var order = cold.FollowOrder;
        bool jobRuns = Production.PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task) is { } running
            && Production.PermaJobs.Follows(running.Row);

        if (!order.IsNone && !jobRuns)
        {
            if (order == ordered)
            {
                cold.FollowOrder = CharacterId.None;
                if (table.Next[slot].Task == (byte)CharacterTask.Follow) TaskStart.Stand(table, slot);
            }
            else if (table.TryResolveNext(order, out int next)) BreakCircle(world, ordered, next, seen);
        }

        if (!cold.Jobs.Exists(j => j.Type == TaskType.StayCloseToTarget)) return;

        for (int k = 0; k < cold.Jobs.Count; k++)
        {
            var job = cold.Jobs[k];
            if (job.Type is not (TaskType.StayCloseToTarget or TaskType.Bodyguard)) continue;

            if (job.Character == ordered)
            {
                Production.ProductionSystem.RemoveJob(world, slot, k);
                k--;
            }
            else if (!job.Character.IsNone && table.TryResolveNext(job.Character, out int next))
                BreakCircle(world, ordered, next, seen);
        }
    }

    #endregion

    #region Paths

    /// <summary>
    /// The path service the walkers ask (owned: <see cref="Dispose"/> stops it).
    /// </summary>
    public PathService Paths { get; } = paths;

    /// <summary>
    /// How far (units) a player's order with no path at all may stand in for one by walking straight at its goal (a few metres, the width of a room:
    /// one shoved into furniture is that close). A goal further off is off the mesh as a rule, and walking at it would go on for ever, so the order
    /// is dropped (engine choice; what the original does is unknown).
    /// </summary>
    public const float StraightOrderReach = 64;

    /// <summary>
    /// Seconds between a follower's path requests, and the wait after a failed one.
    /// </summary>
    public const float RepathSeconds = 0.5f, FailedPathWait = 2;

    /// <summary>
    /// Seconds between the requests of a walker on the provisional part of a long route (<see cref="PathResult.Exact"/>), which ask for the path
    /// again as zones load ahead of it, and how many times running it may be stopped there by ground the route did not know before it gives up
    /// (engine choices).
    /// </summary>
    public const float ProvisionalRepathSeconds = 2;

    /// <summary>
    /// How many times running a walker on the provisional part may be stopped there before it gives up (<see cref="ProvisionalRepathSeconds"/>).
    /// </summary>
    public const int ProvisionalBlockedLimit = 5;

    /// <summary>
    /// How many times running a walker stopped on the navmesh part of its path (no ground it can stand on ahead) asks for the path again from where
    /// it stands before it drops it (engine choice: from a slightly different spot the path can start elsewhere, docs/game/pathfinding.md "Own
    /// builder"). A block while a request is in flight does not count: the walker stands until the answer comes. A player's move order
    /// (<see cref="CharacterCold.OrderMove"/>) is not dropped for it (<see cref="BlockedRetrySeconds"/>).
    /// </summary>
    public const int BlockedRepathLimit = 3;

    /// <summary>
    /// Seconds between the requests of a move-order walker still blocked past <see cref="BlockedRepathLimit"/> /
    /// <see cref="ProvisionalBlockedLimit"/>: it keeps the order, stands and asks again from where it stands (the original's agent asks again after 5
    /// s off its path and never gives the walk up; docs/game/pathfinding.md "Walls").
    /// </summary>
    public const float BlockedRetrySeconds = 5;

    /// <summary>
    /// How far (units) a walker may be off a leg of its new path to enter the path there (<see cref="Join"/>; engine choice: a stride).
    /// </summary>
    public const float JoinSlack = ArriveRadius;

    /// <summary>
    /// Spacing (units) of the points a path is tested at for the zones it crosses (<see cref="Crosses"/>; engine choice, well under a zone).
    /// </summary>
    const float ZoneProbeStep = 100;

    /// <summary>
    /// The system owns its path service (the host hands it over at construction), so disposing the system stops the path thread.
    /// </summary>
    public void Dispose() => Paths.Dispose();

    /// <summary>
    /// Asks the path service for the paths wanted (serial), after gathering the speed groups.
    /// </summary>
    public void Schedule(World world)
    {
        Fights ??= world.Systems.OfType<CombatSystem>().FirstOrDefault();
        var table = world.Characters;
        var next = table.Next;
        _groupSpeeds.Gather(world);
        _ = world.Fixtures.DoorPicks;   // the door spots and the doors by building, for the followers of a leader shut in (Think reads them)
        _ = world.Fixtures.DoorsByBuilding;

        for (int i = 0; i < next.Length; i++)
        {
            if (!next[i].Alive || (next[i].Flags & (ushort)MoveFlags.NeedPath) == 0) continue;

            var cold = table.Cold(i)!;

            // The goal's floor: the height the order was given at (a follower's leader on another storey), else the character's own (a wander or
            // follow goal on its level).
            float nearY = cold.GoalHeightAt == next[i].Goal && !float.IsNaN(cold.GoalHeight) ? cold.GoalHeight : next[i].Position.Y;
            var splice = SpliceTarget(next[i], cold);
            var to = splice ?? new Vector3(next[i].Goal.X, world.Walkability.GroundHeight(next[i].Goal.X, next[i].Goal.Y, nearY), next[i].Goal.Y);
            cold.PathRequest = Paths.Submit(new CharacterId(i, next[i].Generation), next[i].Position, to, cold.FootprintRadius,
                WaterCost.Of(cold.WaterFactor, next[i].MaxSpeed, cold.Race, cold.SwimSpeed), cold.RoadPreference);
            if (splice is null) cold.SplicePath = null;
            else cold.SpliceRequest = cold.LastSplice = cold.PathRequest;

            next[i].Flags = (ushort)((next[i].Flags & ~(ushort)MoveFlags.NeedPath) | (ushort)MoveFlags.Pending);
        }

        // The synchronous service has the answers already.
        if (Paths.IsSynchronous) ApplyAnswers(world);
    }

    /// <summary>
    /// Takes the path service's answers (serial): the answer to a walker's latest request, or a found path newer than the one it follows.
    /// </summary>
    void ApplyAnswers(World world)
    {
        var table = world.Characters;

        foreach (var answer in Paths.Drain())
        {
            if (!table.TryResolveNext(answer.Character, out int slot)) continue;

            var cold = table.Cold(slot)!;
            ref var n = ref table.Next[slot];

            if (cold.PathRequest != answer.Id)
            {
                TakeOlderAnswer(world.Walkability, ref n, cold, answer);
                continue;
            }

            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.Pending);
            if (TakeSpliceAnswer(world.Walkability, ref n, cold, answer)) continue;

            if (answer.Path.Found)
            {
                cold.PathFailed = false;
                TakePath(world.Walkability, ref n, cold, answer.Path);
            }
            else
            {
                NoPathFound(world, slot, ref n, cold);
            }
        }
    }

    /// <summary>
    /// The answer to an older request: the character was given another goal since. Orders given again every tick (a held right button) are each
    /// replaced before their path comes, so a found path newer than the one followed is still taken while the latest request is on its way.
    /// </summary>
    static void TakeOlderAnswer(IWalkability walk, ref CharacterHot n, CharacterCold cold, PathAnswer answer)
    {
        bool waiting = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
        if (!answer.Path.Found || !waiting || n.Task != (byte)CharacterTask.GoTo || answer.Id <= cold.PathApplied || answer.Id == cold.LastSplice)
            return;

        TakePath(walk, ref n, cold, answer.Path);
        cold.PathApplied = answer.Id;
    }

    /// <summary>
    /// The latest request found no path: a player's short order walks straight at its goal, any other walk is dropped and waits.
    /// </summary>
    static void NoPathFound(World world, int slot, ref CharacterHot n, CharacterCold cold)
    {
        // A player ordered out of a spot no path can leave (shut in a hollow the navmesh does not cross, or shoved into one) walks straight
        // at the order's goal instead of losing the order: the ordinary step test still refuses what it cannot walk, and EscapesToward above
        // clears the step out of a hole that cuts the mesh. Only a short order with a goal on the mesh: a goal off it (a click that hit
        // nothing, an order restored from a save) would be walked at for ever, through walls, so it keeps the drop.
        float toGoal = new Vector2(n.Goal.X - n.Position.X, n.Goal.Y - n.Position.Z).Length();
        bool shortHop = toGoal <= StraightOrderReach && world.Walkability.IsWalkable(n.Goal.X, n.Goal.Y, n.Position.Y);

        if (cold.IsPlayer && n.Task == (byte)CharacterTask.GoTo && shortHop)
        {
            cold.PathFailed = false;
            cold.Path = [new Vector3(n.Goal.X, n.Position.Y, n.Goal.Y)];
            cold.PathExact = int.MaxValue;
            n.PathCursor = 0;
            n.Flags |= (ushort)MoveFlags.HasPath;
            return;
        }

        // No way there: the old path (an order given on the move) is dropped too.
        bool following = (n.Flags & (ushort)MoveFlags.HasPath) != 0;
        cold.PathFailed = true;
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
        n.PathCursor = 0;
        n.TaskTime = FailedPathWait;

        if (n.Task == (byte)CharacterTask.GoTo)
        {
            LogDropped(world, n, cold, following ? "no path found on asking again (the path it followed is dropped)" : "no path found");
            cold.WalkEnd = WalkEnd.Failed;
            TaskStart.Step(world.Characters, slot, CharacterTask.Idle);
            cold.OrderQueue.Clear();
        }
    }

    /// <summary>
    /// Follows a found path from its point after the start (the start is where the walker stood when it asked), entering it at <see cref="Join"/>.
    /// </summary>
    static void TakePath(IWalkability walk, ref CharacterHot n, CharacterCold cold, PathResult path)
    {
        cold.Path = [.. path.Points.Skip(1)];
        cold.PathExact = path.IsProvisional ? path.Exact - 1 : int.MaxValue;
        if (path.Exact > 1) cold.ProvisionalBlocked = 0;

        n.PathCursor = Join(walk, n.Position, path.Points[0], cold.Path);
        n.Flags |= (ushort)MoveFlags.HasPath;
    }

    /// <summary>
    /// The point of a new path a walker heads for first. A path asked for on the move (the provisional part of a long route asks every few seconds)
    /// starts where the walker stood then; it has walked on meanwhile, and heading for the path's first point would turn it back and round (engine
    /// choice). So the end of the leg nearest it, among the legs within the distance it has come, when it is within <see cref="JoinSlack"/> of that
    /// leg and can step toward its end; else 0.
    /// </summary>
    static int Join(IWalkability walk, Vector3 at, Vector3 start, Vector3[] path)
    {
        var here = new Vector2(at.X, at.Z);
        var a = new Vector2(start.X, start.Z);
        float reach = Vector2.Distance(a, here) + JoinSlack, along = 0, best = JoinSlack;
        int join = 0;

        for (int k = 0; k < path.Length && along <= reach; k++)
        {
            var b = new Vector2(path[k].X, path[k].Z);
            float gap = SegmentDistance(here, a, b);

            if (gap < best)
            {
                best = gap;
                join = k;
            }

            along += Vector2.Distance(a, b);
            a = b;
        }

        return join > 0 && CanCut(walk, at, path[join]) ? join : 0;
    }

    /// <summary>
    /// The distance from <paramref name="p"/> to the segment from <paramref name="a"/> to <paramref name="b"/>.
    /// </summary>
    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lengthSquared = ab.LengthSquared();
        float t = lengthSquared < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>
    /// Asks for the path again (partition-safe: the character's own state), at most every <see cref="ProvisionalRepathSeconds"/> unless
    /// <paramref name="now"/>.
    /// </summary>
    static void Reroute(World world, ref CharacterHot n, CharacterCold cold, bool now = false)
    {
        if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0 || !now && world.Tick < cold.ProvisionalRepathTick)
            return;

        cold.ProvisionalRepathTick = world.Tick + (long)MathF.Ceiling(ProvisionalRepathSeconds / world.TickSeconds);

        // Blocked: the whole way again from where it stands; on the provisional part, a splice of the stretch ahead (MovementSystem.Splice.cs).
        if (now)
        {
            cold.SplicePath = null;
            n.Flags |= (ushort)MoveFlags.NeedPath;
        }
        else AskSplice(ref n, cold);
    }

    /// <summary>
    /// Whether the way from <paramref name="at"/> along <paramref name="path"/> from <paramref name="cursor"/> on enters one of
    /// <paramref name="zones"/>.
    /// </summary>
    static bool Crosses(Vector3 at, Vector3[] path, int cursor, IReadOnlySet<ZoneCoordinate> zones)
    {
        var a = new Vector2(at.X, at.Z);
        if (zones.Contains(WorldLayout.ZoneOf(a.X, a.Y))) return true;

        for (int i = cursor; i < path.Length; i++)
        {
            var b = new Vector2(path[i].X, path[i].Z);
            float length = Vector2.Distance(a, b);

            for (float d = ZoneProbeStep; d < length + ZoneProbeStep; d += ZoneProbeStep)
            {
                var p = Vector2.Lerp(a, b, MathF.Min(d, length) / MathF.Max(length, 1e-3f));
                if (zones.Contains(WorldLayout.ZoneOf(p.X, p.Y))) return true;
            }

            a = b;
        }

        return false;
    }

    /// <summary>
    /// A walker blocked on its path (no surface to step on, no request in flight) asks for the path again from where it stands, up to
    /// <see cref="ProvisionalBlockedLimit"/> times on the provisional part of a long route (the terrain-only route met a wall or a building) and
    /// <see cref="BlockedRepathLimit"/> on its navmesh part. Past that a player's move order keeps its path and asks again every
    /// <see cref="BlockedRetrySeconds"/>; false: the walk is given up (<see cref="DropBlocked"/>).
    /// </summary>
    static bool AsksAgainBlocked(World world, ref CharacterHot n, CharacterCold cold)
    {
        bool provisional = n.PathCursor >= cold.PathExact;
        bool within = provisional ? ++cold.ProvisionalBlocked <= ProvisionalBlockedLimit : ++cold.BlockedRepaths <= BlockedRepathLimit;
        if (!within && !cold.OrderMove) return false;

        if (within || world.Tick >= cold.BlockedRetryTick)
        {
            if (!within && cold.BlockedRetryTick == 0) LogHeld(world, n, cold, provisional);

            cold.BlockedRetryTick = within ? 0 : world.Tick + (long)MathF.Ceiling(BlockedRetrySeconds / world.TickSeconds);
            Reroute(world, ref n, cold, now: true);
        }

        return true;
    }

    /// <summary>
    /// A blocked walk given up (<see cref="AsksAgainBlocked"/>): the path is dropped and the walker waits before trying again; a follower steering at
    /// its slot takes paths for a while.
    /// </summary>
    static void DropBlocked(World world, int slot, ref CharacterHot n, CharacterCold cold, float x, float z)
    {
        if (n.Task == (byte)CharacterTask.Follow) cold.FollowPathTime = FollowPathSeconds;

        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
        n.TaskTime = FailedPathWait;
        if (n.Task != (byte)CharacterTask.GoTo) return;

        string why = n.PathCursor >= cold.PathExact
            ? $"blocked {cold.ProvisionalBlocked} times on the provisional long-range part (limit {ProvisionalBlockedLimit})"
            : $"blocked {cold.BlockedRepaths} times on the navmesh part (limit {BlockedRepathLimit})";
        LogDropped(world, n, cold, $"{why}, waypoint {n.PathCursor} of {cold.Path.Length}, stuck stepping to {x:0}, {z:0}");
        cold.WalkEnd = WalkEnd.Failed;
        TaskStart.Step(world.Characters, slot, CharacterTask.Idle);
        cold.OrderQueue.Clear();
    }

    /// <summary>
    /// A player's move order held blocked past the try limit (<see cref="AsksAgainBlocked"/>): logged once per stretch of blocks
    /// (docs/game/pathfinding.md "Dropped orders").
    /// </summary>
    static void LogHeld(World world, in CharacterHot n, CharacterCold cold, bool provisional)
    {
        string part = provisional ? "provisional long-range part" : "navmesh part";
        Console.WriteLine(
            $"order     {cold.Name} blocked on the {part} at tick {world.Tick}, keeps its move and asks again every {BlockedRetrySeconds:0} s; " +
            $"at {n.Position.X:0}, {n.Position.Y:0}, {n.Position.Z:0}, goal {n.Goal.X:0}, {n.Goal.Y:0}");
    }

    /// <summary>
    /// A player's move order dropped: why and where, in the log (docs/game/pathfinding.md "Dropped orders"). Other characters' drops are not logged.
    /// </summary>
    static void LogDropped(World world, in CharacterHot n, CharacterCold cold, string why)
    {
        if (!cold.IsPlayer) return;

        Console.WriteLine($"order     {cold.Name} dropped its move at tick {world.Tick}: {why}; " +
            $"at {n.Position.X:0}, {n.Position.Y:0}, {n.Position.Z:0}, goal {n.Goal.X:0}, {n.Goal.Y:0}");
    }

    #endregion

    #region Following

    /// <summary>
    /// How fast a formation follower's heading eases toward its leader's, per second, while the leader moves (the original's 0.5).
    /// </summary>
    public const float HeadingEase = 0.5f;

    /// <summary>
    /// A leader slower than this counts as standing (its follower's speed bands then apply).
    /// </summary>
    public const float StandingSpeed = 0.1f;

    /// <summary>
    /// Seconds a follower blocked on its way straight to its slot takes paths instead (engine choice).
    /// </summary>
    public const float FollowPathSeconds = 3;

    /// <summary>
    /// A follower on paths (far off, or blocked) farther than this from its slot asks for a path to it.
    /// </summary>
    public const float FollowSlack = 14;

    /// <summary>
    /// A follower nearer its aim than this steers straight at it; farther, it takes paths (the original's 50).
    /// </summary>
    public const float DirectDistance = 50;

    /// <summary>
    /// The squared distance within which a follower steers straight at its aim (the original's 50), and beyond which it always takes paths (100).
    /// </summary>
    const float DirectNear2 = DirectDistance * DirectDistance, DirectFar2 = 100 * 100;

    /// <summary>
    /// The door aim of a follower shut out (docs/game/pathfinding.md "Following"): the door's two points DoorDepth either side of the leaf (door
    /// data +0x3bc / +0x3c8), the aim 1.1 of their distance past the outer one, the formation slot shrunk to 0.2.
    /// </summary>
    const float DoorDepth = 15, DoorPast = 1.1f, DoorSlotScale = 0.2f;

    /// <summary>
    /// The follow and wander tasks' decisions of the partition's characters (parallel): a follower's aim, speed and path requests, a wanderer's
    /// next place.
    /// </summary>
    public void Think(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;

        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || prev[i].Dormant || !world.Steps(i)) continue;

            float dt = world.StepSeconds(i);   // a far character's step covers the ticks since its last (the far cadence, World.StepTicks)
            ref var n = ref next[i];
            var task = (CharacterTask)n.Task;
            var flags = (MoveFlags)n.Flags;
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true }) continue;

            bool busy = (flags & MoveFlags.AnyPath) != 0;

            if (task == CharacterTask.Follow) ThinkFollow(world, i, dt, ref n, cold, flags, busy);
            else if (task == CharacterTask.Wander) ThinkWander(world, i, dt, ref n, cold, busy);
        }
    }

    /// <summary>
    /// The original's desired speed of a follower <paramref name="away"/> from its aim (docs/game/pathfinding.md "Squads travelling together"):
    /// behind a moving leader the distance itself, less while it faces away from the leader's heading (down to a tenth); beside a standing one 0
    /// within 20, then 10-20, 20-30 and 30-999 at 50, 200 and 500 (linear in the distance squared).
    /// </summary>
    public static float FollowSpeedFor(float away, bool leaderMoving, Vector2 leaderDir, Vector2 ownDir)
    {
        float d2 = away * away;
        if (d2 < 1) return 0;
        if (leaderMoving) return away * MathF.Max(0.1f, 0.5f + 0.5f * Vector2.Dot(leaderDir, ownDir));
        if (d2 >= 40000) return 30 + 969 * Math.Clamp((d2 - 40000) / 210000, 0, 1);
        if (d2 >= 2500) return 20 + 10 * Math.Clamp((d2 - 2500) / 37500, 0, 1);
        if (d2 >= 400) return 10 + 10 * Math.Clamp((d2 - 400) / 2100, 0, 1);

        return 0;
    }

    /// <summary>
    /// A follower's step of <see cref="Think"/>: its aim by the one it follows, its speed and sneaking, and whether it steers straight there or
    /// asks for a path.
    /// </summary>
    void ThinkFollow(World world, int i, float dt, ref CharacterHot n, CharacterCold cold, MoveFlags flags, bool busy)
    {
        var table = world.Characters;
        var prev = table.Previous;

        // A follow order whose target is gone ends: the character stands where it is.
        if (!cold.FollowOrder.IsNone && !table.IsAlive(cold.FollowOrder))
        {
            cold.FollowOrder = CharacterId.None;
            TaskStart.Step(table, i, CharacterTask.Idle);
            return;
        }

        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        var leader = Followed(world, squad, cold);

        if (leader.IsNone || leader.Slot == i || !table.IsAlive(leader))
        {
            TaskStart.Step(table, i, CharacterTask.Wander);
            return;
        }

        if (table.Cold(leader.Slot)?.CarriedBy == table.IdOf(i))
            return;   // it carries its leader: the squad follows it, it does not follow itself

        var lead = prev[leader.Slot];
        var self = table.IdOf(i);
        var leadAt = new Vector2(lead.Position.X, lead.Position.Z);
        var leadDir = new Vector2(MathF.Sin(lead.Yaw), MathF.Cos(lead.Yaw));
        float leadSpeed = new Vector2(lead.Velocity.X, lead.Velocity.Z).Length();
        bool moving = leadSpeed > StandingSpeed;
        var here = new Vector2(n.Position.X, n.Position.Z);
        Vector2 slotPos;
        bool shutOut = false;

        if (squad is not null && squad.FormationSlots.TryGetValue(self, out var slot) && squad.Members.Contains(leader))
        {
            (slotPos, shutOut) = FormationAim(world, i, dt, n.Position, cold, slot, leader.Slot, lead.Position, leadAt, leadDir, leadSpeed, moving);
        }
        else
        {
            // Anyone else: its drawn distance from the target, on its own side.
            var off = here - leadAt;
            off = off.LengthSquared() > 1e-6f ? Vector2.Normalize(off) : -leadDir;
            slotPos = leadAt + off * PlainDistance(world.Seed, self, leader);
        }

        // A leader on another storey (the Hub Storm House's roof above it): the follower's own floor under its slot would be "there", so it
        // takes a path to the leader itself, on the leader's floor, and the climb counts in its distance (engine choice; the original's
        // Task_Follow on stacked floors is Unknown).
        float climb = MathF.Abs(lead.Position.Y - n.Position.Y);
        bool otherFloor = !shutOut && climb > FloorSlack
            && MathF.Abs(world.Walkability.GroundHeight(leadAt.X, leadAt.Y, n.Position.Y) - lead.Position.Y) > FloorSlack;
        if (otherFloor) slotPos = leadAt;

        float away = Vector2.Distance(here, slotPos) + (otherFloor ? climb : 0);

        // The catch-up factor reads the two movement vectors (+0xd0 / +0xd8 normalised), not the facings (FUN_14033b350, Verified).
        cold.FollowSpeed = FollowSpeedFor(away, moving, Direction(lead.Velocity), Direction(prev[i].Velocity));
        n.Mode = lead.Mode;
        CopySneak(table, lead, leader.Slot, cold);

        n.TaskTime -= dt;
        if (cold.FollowPathTime > 0) cold.FollowPathTime = MathF.Max(cold.FollowPathTime - dt, 0);

        bool direct = FollowDirect(cold, away, n.Position, new Vector2(prev[i].Velocity.X, prev[i].Velocity.Z).Length());

        if (direct && !otherFloor && cold.FollowPathTime <= 0) SteerStraight(ref n, cold, slotPos, busy);
        else AskFollowPath(ref n, cold, flags, busy, slotPos, away, otherFloor, lead.Position.Y);
    }

    /// <summary>
    /// A squad mate's aim: its slot turned onto its eased heading, one second of the leader's travel ahead of the leader; outside the door when the
    /// leader is shut in from it (<see cref="ShutOutAim"/>, the second value true).
    /// </summary>
    static (Vector2 Aim, bool ShutOut) FormationAim(World world, int i, float dt, Vector3 at, CharacterCold cold, Vector2 slot, int leader,
        Vector3 leaderAt, Vector2 leadAt, Vector2 leadDir, float leadSpeed, bool moving)
    {
        var heading = cold.FollowHeading;

        if (heading == Vector2.Zero)
        {
            heading = leadDir;
        }
        else if (moving)
        {
            heading += (leadDir - heading) * MathF.Min(1, HeadingEase * dt);
            heading = heading.LengthSquared() > 1e-8f ? Vector2.Normalize(heading) : leadDir;
        }

        cold.FollowHeading = heading;
        var slotPos = leadAt + heading * leadSpeed + Formation.ToWorld(slot, heading);
        if (ShutOutAim(world, leader, i, leaderAt, at) is { } outside)
            return (outside + Formation.ToWorld(slot, heading) * DoorSlotScale, true);

        return (slotPos, false);
    }

    /// <summary>
    /// Task_Follow sets the follower's sneaking each update (FUN_14033b350, Verified): on while the leader sneaks out of a fight and the follower
    /// regards it as friendly (vfunc 0x3f0; here: the same faction or an allied one, as Carry.Friendly reads it), off otherwise; dropping it also
    /// stands it up. A leader that follows someone itself may change its own flag in this same parallel pass, so its followers are left as they are
    /// (engine choice, keeps the step deterministic).
    /// </summary>
    void CopySneak(CharacterTable table, in CharacterHot lead, int leader, CharacterCold cold)
    {
        if (lead.Task == (byte)CharacterTask.Follow || table.Cold(leader) is not { } leaderCold) return;

        bool sneak = (leaderCold.Orders & CharacterOrders.Sneak) != 0 && !leaderCold.InCombat
            && (leaderCold.Faction == cold.Faction || Fights?.Relations is { } rel && rel.Contains(cold.Faction) && rel.Contains(leaderCold.Faction)
                && rel.IsAlly(cold.Faction, leaderCold.Faction));

        if (sneak)
        {
            cold.Orders |= CharacterOrders.Sneak;
        }
        else if ((cold.Orders & CharacterOrders.Sneak) != 0)
        {
            cold.Orders &= ~CharacterOrders.Sneak;
            if (cold.Medical is { } med) med.StayingLow = false;
        }
    }

    /// <summary>
    /// Near its aim: Move steers it straight there (a path it had is dropped).
    /// </summary>
    static void SteerStraight(ref CharacterHot n, CharacterCold cold, Vector2 slotPos, bool busy)
    {
        n.Goal = slotPos;

        if (busy)
        {
            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
            n.PathCursor = 0;
            cold.PathRequest = 0;
        }
    }

    /// <summary>
    /// A follower on paths farther than <see cref="FollowSlack"/> from its aim asks for a path to it, at most every <see cref="RepathSeconds"/> and
    /// when its goal moved; on the leader's floor when the leader is on another storey.
    /// </summary>
    static void AskFollowPath(ref CharacterHot n, CharacterCold cold, MoveFlags flags, bool busy, Vector2 slotPos, float away, bool otherFloor,
        float leaderY)
    {
        if (away > FollowSlack && (flags & (MoveFlags.NeedPath | MoveFlags.Pending)) == 0 && n.TaskTime <= 0
            && (!busy || Vector2.DistanceSquared(slotPos, n.Goal) > 20 * 20))
        {
            n.Goal = slotPos;
            if (otherFloor) (cold.GoalHeight, cold.GoalHeightAt) = (leaderY, slotPos);

            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = RepathSeconds;
        }
    }

    /// <summary>
    /// A wanderer's step of <see cref="Think"/>: once its wait is over, a new place in its squad's home (or its post's) circle.
    /// </summary>
    static void ThinkWander(World world, int i, float dt, ref CharacterHot n, CharacterCold cold, bool busy)
    {
        n.Mode = SpeedMode.Walk;
        if (busy) return;

        n.TaskTime -= dt;
        if (n.TaskTime > 0) return;

        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        var centre = squad is not null ? squad.HomeCentre : new Vector2(n.Position.X, n.Position.Z);
        float radius = squad is not null ? squad.HomeRadius : 100;
        if (cold.Ai is { WanderRadius: > 0 } mind)
            (centre, radius) = (mind.WanderCentre, mind.WanderRadius);   // milling about its post (Ai.AiBehaviour.Mill)

        ulong roll = Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(i)), RngPurpose.Wander, (ulong)world.Tick);
        n.Goal = Rng.PointInDisc(roll, centre, radius);
        n.Flags |= (ushort)MoveFlags.NeedPath;
    }

    /// <summary>
    /// Whom a follower keeps its slot by: the target of its <see cref="FollowOrder"/>, a contract's mission target (Ai.AiMind.FollowTarget), its
    /// owner (a slave's: Task_FollowLeader, the owner first, <c>FUN_14033b160</c>; Meitou's owner is the leader of the master squad,
    /// <see cref="Kidnap.ObedientSlave.OwnerOf"/>), else its squad's leader.
    /// </summary>
    static CharacterId Followed(World world, Squad? squad, CharacterCold cold) =>
        !cold.FollowOrder.IsNone ? cold.FollowOrder : cold.Ai is { FollowTarget.IsNone: false } follows ? follows.FollowTarget
        : Kidnap.ObedientSlave.OwnerOf(world, cold) is { } owner ? owner : squad?.Leader ?? CharacterId.None;

    /// <summary>
    /// A velocity's horizontal unit direction (zero stays zero, as Ogre's normalise leaves it).
    /// </summary>
    static Vector2 Direction(Vector3 velocity)
    {
        var flat = new Vector2(velocity.X, velocity.Z);
        return flat.LengthSquared() > 0 ? Vector2.Normalize(flat) : Vector2.Zero;
    }

    /// <summary>
    /// Whether a follower steers straight at its aim (<c>Task_Follow</c> flag <c>+0xac</c>, FUN_14033b350, <b>Verified</b>): set within 50; between
    /// 50 and 100 it stays set only while the distance it moved since the last update exceeds its speed squared (else it drops and paths); cleared
    /// beyond 100. The distance moved is kept for the next update.
    /// </summary>
    static bool FollowDirect(CharacterCold cold, float away, Vector3 position, float speed)
    {
        float d2 = away * away;
        if (d2 < DirectNear2) cold.FollowDirect = true;
        else if (cold.FollowDirect && (d2 >= DirectFar2 || Vector3.Distance(position, cold.FollowLast) <= speed * speed))
            cold.FollowDirect = false;

        cold.FollowLast = position;
        return cold.FollowDirect;
    }

    /// <summary>
    /// How far a follower that is not its target's squad mate keeps from it: 20..40, drawn per follower and target (the original draws it when the
    /// follow task is made).
    /// </summary>
    static float PlainDistance(ulong seed, CharacterId self, CharacterId target) =>
        20 + 20 * Rng.Float(Rng.Hash(seed, Rng.Key(self), RngPurpose.Formation, Rng.Key(target)));

    /// <summary>
    /// Where a squad mate waits while its leader is inside a building it cannot enter (<c>1403335c0</c>; engine reading of "cannot enter": every door
    /// of the building locked and whole): outside the door nearest to it, A + (A − B) × 1.1 with A the door's outer point and B its inner. Null when
    /// the leader is not shut in from it.
    /// </summary>
    static Vector2? ShutOutAim(World world, int leader, int self, Vector3 leaderAt, Vector3 at)
    {
        if (world.Sight.BuildingOf(leader) is not { } inside || world.Sight.BuildingOf(self) == inside) return null;

        Locks.Fixture? nearest = null;
        float best = float.MaxValue;

        foreach (var door in world.Fixtures.DoorsByBuilding[inside])
        {
            if (door.Lock is not { EffectivelyLocked: true } || door.DoorBroken) return null;   // a way in
            if (door.Spot is not { } spot) continue;

            float d = Vector3.DistanceSquared(spot.Centre, at);
            if (d < best) (best, nearest) = (d, door);
        }

        if (nearest?.Spot is not { } s) return null;

        var outward = new Vector2(s.Normal.X, s.Normal.Z);
        if (Vector2.Dot(outward, new Vector2(leaderAt.X - s.Centre.X, leaderAt.Z - s.Centre.Z)) > 0)
            outward = -outward;   // away from the leader inside

        var centre = new Vector2(s.Centre.X, s.Centre.Z);
        Vector2 a = centre + outward * DoorDepth, b = centre - outward * DoorDepth;
        return a + (a - b) * DoorPast;
    }

    #endregion

    #region Stepping

    /// <summary>
    /// The fights the steering reads (null: no fight steering, as in worlds without combat; the world's combat system unless set).
    /// </summary>
    public CombatSystem? Fights { get; set; }

    /// <summary>
    /// Speed gained per second in combat (docs/game/pathfinding.md, the combat / follow steering: "ramps up by 15 per second").
    /// </summary>
    public const float Acceleration = 15;

    /// <summary>
    /// R of the separation steering: neighbour radius plus margin; a human's footprint radius is 4.
    /// </summary>
    public const float SeparationRadius = 8;

    /// <summary>
    /// Speed in units/s of a repulsion of magnitude 100 (engine choice).
    /// </summary>
    public const float SeparationStrength = 0.15f;

    /// <summary>
    /// How many neighbours the separation counts at most (Observed).
    /// </summary>
    public const int MaxSeparationContributions = 26;

    /// <summary>
    /// The share of the footprint radius a body is solid over (engine choice: 2.4 units for a human, about half the shoulders' width).
    /// </summary>
    public const float BodyShare = 0.6f;

    /// <summary>
    /// A character within this distance of a waypoint has reached it.
    /// </summary>
    public const float ArriveRadius = 10;

    /// <summary>
    /// How near (units) a walker comes to a corner it may not cut (<see cref="CanCut"/>) before it turns for the next waypoint (engine choice: about
    /// a step).
    /// </summary>
    public const float CornerReach = 2;

    /// <summary>
    /// How far a walker's height may be from its path's before the step looks for a layer at the path's height (<see cref="CorridorHeight"/>).
    /// </summary>
    const float CorridorSlack = 4;

    /// <summary>
    /// How near the last point of a path counts as arrived (<b>Verified</b>, the nav agent's <c>FUN_140144440</c>: 0.01 squared Havok units, 1 unit);
    /// the agent brakes into it (<see cref="PathAcceleration"/>), so a walker stops on its goal rather than a stride short of it.
    /// </summary>
    public const float FinalArrive = 1;

    /// <summary>
    /// How far short of a corner the nav agent's braking starts beyond its stopping distance, and the length its turn term stands for (both 0.1 and 1
    /// Havok unit).
    /// </summary>
    const float CornerMargin = 1, CornerLength = 10;

    /// <summary>
    /// The <see cref="CharacterCold.ArriveWithin"/> of a walk to a spot (engine choice: a barman stands at his node behind the counter, not a stride
    /// off it).
    /// </summary>
    public const float SpotArrive = 2;

    /// <summary>
    /// A wanderer waits this many seconds (uniform) after reaching a place.
    /// </summary>
    public const float WaitMin = 3, WaitMax = 10;

    /// <summary>
    /// Radians per second the heading of a path follower turns at most (engine choice: a Kenshi recording turns a standing character about 100
    /// degrees in 0.15 s before it runs; docs/game/pathfinding.md "Turning").
    /// </summary>
    public const float TurnRate = 4 * MathF.PI;

    /// <summary>
    /// The original's update time of a character out of view (<b>Verified</b>, docs/game/game-loop.md "Update time off screen"): the time the mover
    /// integrates since the character's last update (<c>+0xc0</c>, handed to the mover as <c>+0xc4</c>) gets the frame time once in the character
    /// loop (<c>FUN_140786e30</c>) and again in the visibility pass (<c>FUN_1405c9f60</c>) while the character is not in view (<c>+0x1a9</c>), so it
    /// moves twice as far. In view, or in a world with no camera (<see cref="World.View"/> null, as headless and tests), the time is the tick's.
    /// </summary>
    public const float OffViewTime = 2;

    /// <summary>
    /// The speed caps of the characters in <see cref="SpeedMode.Group"/>, gathered in <see cref="Schedule"/>.
    /// </summary>
    readonly GroupSpeeds _groupSpeeds = new();

    /// <summary>
    /// One character's step through <see cref="Move"/>, handed from stage to stage.
    /// </summary>
    struct StepState
    {
        /// <summary>
        /// The step's time (seconds).
        /// </summary>
        public float Dt;

        /// <summary>
        /// The speed it moves at.
        /// </summary>
        public float Speed;

        /// <summary>
        /// The way it moves (a unit vector, or zero).
        /// </summary>
        public Vector2 Dir;

        /// <summary>
        /// Its heading was set this step.
        /// </summary>
        public bool Facing;

        /// <summary>
        /// It walks a path at the nav agent's velocity.
        /// </summary>
        public bool AgentWalk;

        /// <summary>
        /// The path direction of a nav agent walk.
        /// </summary>
        public Vector2 Wanted;

        /// <summary>
        /// The speed it wants.
        /// </summary>
        public float Desired;

        /// <summary>
        /// It reached the end of its path.
        /// </summary>
        public bool Finished;

        /// <summary>
        /// It closes on its spot (<see cref="CharacterCold.ArriveWithin"/>).
        /// </summary>
        public bool Closing;

        /// <summary>
        /// Its water state.
        /// </summary>
        public int Water;

        /// <summary>
        /// A fighter in its footwork, moved by the fight steering.
        /// </summary>
        public bool Steering;
    }

    /// <summary>
    /// The separation query's state (<see cref="AddSeparation"/>).
    /// </summary>
    struct SeparationState
    {
        /// <summary>
        /// The characters.
        /// </summary>
        public CharacterTable Table;

        /// <summary>
        /// The stepping character's slot.
        /// </summary>
        public int Self;

        /// <summary>
        /// Neighbours counted.
        /// </summary>
        public int Count;

        /// <summary>
        /// The sum of their repulsions.
        /// </summary>
        public Vector2 Sum;

        /// <summary>
        /// The neighbours counted, for the body contact.
        /// </summary>
        public Neighbours Near;
    }

    /// <summary>
    /// The slots of up to <see cref="MaxSeparationContributions"/> neighbours.
    /// </summary>
    [InlineArray(MaxSeparationContributions)]
    struct Neighbours
    {
        /// <summary>
        /// The first slot.
        /// </summary>
        int _first;
    }

    /// <summary>
    /// The covered-goal query's state (<see cref="GoalOccupied"/>).
    /// </summary>
    struct OccupiedState
    {
        /// <summary>
        /// The characters.
        /// </summary>
        public CharacterTable Table;

        /// <summary>
        /// The walker's slot.
        /// </summary>
        public int Self;

        /// <summary>
        /// The goal, X and Z.
        /// </summary>
        public Vector2 Goal;

        /// <summary>
        /// A body covers the goal.
        /// </summary>
        public bool Found;
    }

    /// <summary>
    /// Steps the partition's characters (parallel: each writes only its own state): along a path, at a follow aim or by the fight steering, pushed
    /// by its neighbours and held to what it can stand on.
    /// </summary>
    public void Move(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;

        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || prev[i].Dormant || !world.Steps(i)) continue;

            // A far character's step covers the ticks since its last (the far cadence, World.StepTicks); one off screen moves on twice the time.
            float dt = world.StepSeconds(i) * OffViewTimeFactor(world, prev[i].Position);
            MoveCharacter(world, i, dt, in prev[i], ref next[i], table.Cold(i)!);
        }
    }

    /// <summary>
    /// The nav agent's acceleration in units per second squared (<b>Verified</b>, <c>FUN_140147ca0</c>): the RACE <c>pathfind acceleration</c> (Havok
    /// units) x 10. A walker's speed moves toward the wanted one by at most this x dt, and near its last point it is held to sqrt(2 x this x the
    /// distance left), so it stops there.
    /// </summary>
    public static float PathAcceleration(CharacterCold cold) => (cold.Race?.PathfindAcceleration ?? 24) * 10;

    /// <summary>
    /// The water state of a character (<see cref="Speed.WaterState"/>, a race's own characters only, not a carried body): the original reads the
    /// height it stands at, which in water is the seabed, while here a character on the water keeps the navmesh's flat water surface for its height,
    /// so the depth is taken from what it stands on below (<see cref="IWalkability.WaterBed"/>: a floor just under it, else the terrain) while it is
    /// at the surface (<see cref="Speed.WaterSlack"/> over the water level; a deck or floor higher up counts as dry). The race's size scale is taken
    /// as 1 (what humans have, <b>Observed</b>).
    /// </summary>
    internal static int WaterStateAt(IWalkability walk, Vector3 at, CharacterCold cold)
    {
        if (cold.Race is not { } race || !cold.CarriedBy.IsNone) return 0;

        float standY = at.Y <= WorldWater.Height + Speed.WaterSlack ? MathF.Min(at.Y, walk.WaterBed(at.X, at.Z, at.Y)) : at.Y;
        return Speed.WaterState(standY, 1, race.Swims);
    }

    /// <summary>
    /// The heading turned from <paramref name="yaw"/> towards <paramref name="wanted"/> by at most <see cref="TurnRate"/> x <paramref name="dt"/>,
    /// and the speed scaled by the cosine of the turn still to make (zero beyond 90 degrees: the character turns where it stands).
    /// </summary>
    public static (Vector2 Dir, float Speed) Turn(float yaw, Vector2 wanted, float speed, float dt, float rate = TurnRate)
    {
        float target = MathF.Atan2(wanted.X, wanted.Y);
        float delta = MathF.IEEERemainder(target - yaw, MathF.Tau);
        float max = rate * dt;
        float heading = MathF.Abs(delta) <= max ? target : yaw + MathF.CopySign(max, delta);
        float left = MathF.IEEERemainder(target - heading, MathF.Tau);
        return (new Vector2(MathF.Sin(heading), MathF.Cos(heading)), speed * MathF.Max(0, MathF.Cos(left)));
    }

    /// <summary>
    /// One character's step of <see cref="Move"/>: <paramref name="p"/> its last tick's state, <paramref name="n"/> the one it writes.
    /// </summary>
    void MoveCharacter(World world, int i, float dt, in CharacterHot p, ref CharacterHot n, CharacterCold cold)
    {
        if (HeldStill(world, p, ref n, cold)) return;

        TrackOrderPoints(ref n, cold);
        var s = new StepState { Dt = dt, Speed = MathF.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z) };
        s.Water = WaterStateAt(world.Walkability, p.Position, cold);
        cold.WaterState = (byte)s.Water;

        // A fighter in its footwork: the fight steering's velocity, facing its target (docs/game/combat.md "Fight steering").
        s.Steering = Fights is not null && cold.Steer is { Active: true } && n.Task == (byte)CharacterTask.Attack;

        if (s.Steering)
        {
            SteerFight(world, i, ref n, cold, ref s);
        }
        else if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0 && n.PathCursor < cold.Path.Length)
        {
            WalkPath(world, i, p, ref n, cold, ref s);
        }
        else if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0)
        {
            s.Finished = true;
        }
        else if (n.Task == (byte)CharacterTask.Follow && ((MoveFlags)n.Flags & MoveFlags.AnyPath) == 0 && cold.FollowPathTime <= 0
            && cold.FollowSpeed > 0)
        {
            SteerToAim(p, n, cold, ref s);
        }

        FaceAttack(world, i, ref n, s.Steering);

        // A steering fighter's velocity is the steering's.
        if (!s.Steering)
        {
            if (s.Finished) FinishPath(world, i, ref n, cold, ref s);
            else Drive(world.Characters.Previous, p, ref n, cold, ref s);
        }

        StepBody(world, i, p, ref n, cold, ref s);
        n.Velocity = new Vector3(s.Dir.X * s.Speed, 0, s.Dir.Y * s.Speed);
        n.Animation = (ushort)(s.Speed < 1 ? 0 : s.Speed <= n.WalkSpeed + 1 ? 1 : 2);
        n.AnimationTime = n.Animation == p.Animation ? p.AnimationTime + dt : 0;
    }

    /// <summary>
    /// Whether the character takes no step this tick (its velocity zeroed): lying down, placed this tick, staggered, getting up or held still by a
    /// clip, held in its cage or bed, or asked to stand still by a medic.
    /// </summary>
    static bool HeldStill(World world, in CharacterHot p, ref CharacterHot n, CharacterCold cold)
    {
        if (cold.Medical is { Incapacitated: true })
        {
            // Lying down (knocked out or dead): no steering, no pushing; the body system has already dropped the orders.
            n.Velocity = Vector3.Zero;
            n.Animation = 0;
            cold.AgentSpeed = 0;
            return true;
        }

        if (n.Position != p.Position)
        {
            // Placed earlier this tick (a debug teleport): a step from last tick's position would put it back, as a neighbour's push does to one
            // in a crowd.
            n.Velocity = Vector3.Zero;
            cold.AgentSpeed = 0;
            return true;
        }

        if (n.Task != (byte)CharacterTask.Attack && cold.StaggeredUntil > world.Tick || CharacterFacts.GettingUp(cold, world.Tick)
            || n.Task != (byte)CharacterTask.Attack && cold.Animation is { HoldsStill: true } && ((MoveFlags)n.Flags & MoveFlags.AnyPath) != 0)
        {
            // A stagger outside a fight stops the movement while the stumble clip plays (Character::_startStumble), and getting up after a
            // knockout in or out of one (Task_GetUp), and while a clip with <c>disables movement</c> plays (a bow, a reload;
            // AnimationClass::isCurrentlyIncapableOfWaypointMovemode); the order's path is kept and resumed.
            n.Velocity = Vector3.Zero;
            cold.AgentSpeed = 0;
            return true;
        }

        if (cold.IsImprisoned || cold.InSomething == Locks.HeldState.Bed && n.Task == (byte)CharacterTask.Sleep)
        {
            // Held in its cage or bed (put there by the fixture registry): no path, no steering, not pushed. Its position is left as it is: one
            // laid in this tick's inputs is already at its spot in Next.
            n.Velocity = Vector3.Zero;
            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
            cold.AgentSpeed = 0;
            return true;
        }

        if (AskedToStandStill(n, cold, world.Tick))
        {
            // A medic treating it asked it to stand still (FirstAidSystem): it stops where it is; its own walk is kept and goes on when the ask
            // lapses.
            n.Velocity = Vector3.Zero;
            cold.AgentSpeed = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The points a player's move order sets on every step: the Hold point and the centre of movement.
    /// </summary>
    static void TrackOrderPoints(ref CharacterHot n, CharacterCold cold)
    {
        // An order move on Hold makes its destination the hold point on every step (Task_Move update FUN_140354600, Verified): a queued move as
        // it starts, and over the position Hold was turned on at mid-walk.
        if (n.Task == (byte)CharacterTask.GoTo && cold.OrderMove && Orders.Holding(cold))
            cold.HoldPoint = new Vector3(n.Goal.X, n.Position.Y, n.Goal.Y);

        // Every player order sets the centre of movement to its point (Character::addOrder -> AI::setCenterOfMovement); it matters only while the
        // mode is not ROAM_FAR.
        if (n.Task == (byte)CharacterTask.GoTo && cold.OrderMove && cold.Manoeuvre != ManoeuvreMode.RoamFar)
            cold.CentreOfMovement = new Vector3(n.Goal.X, n.Position.Y, n.Goal.Y);
    }

    /// <summary>
    /// A fighter in its footwork moves at the fight steering's velocity and faces its target.
    /// </summary>
    void SteerFight(World world, int i, ref CharacterHot n, CharacterCold cold, ref StepState s)
    {
        bool strafing = cold.Animation?.Strafing ?? false;
        var v = CombatSteering.Velocity(world, Fights!, i, cold, cold.Steer!, n.MaxSpeed, strafing, s.Speed > 0.01f, out var face);

        // In water the steering's caller scales it by the water state (x0.75 / 0.5 / 0.4 in states 1-3, FUN_14065dba0; docs/game/combat.md
        // "Fight steering").
        if (s.Water > 0) v *= s.Water switch { 1 => 0.75f, 2 => 0.5f, _ => 0.4f };

        float vs = v.Length();
        (s.Dir, s.Speed) = vs > 1e-4f ? (v / vs, vs) : (Vector2.Zero, 0f);

        if (face != Vector2.Zero)
        {
            // The logical heading takes the look-at direction at once (combatMovementUpdate FUN_1402af1e0 writes the unit vector to the
            // target into the movement's heading +0xd0 every tick, docs/game/combat.md "Fight steering"); only the drawn body turns at a rate
            // (AnimationSystem.FollowFacing).
            s.Facing = true;
            n.Yaw = MathF.Atan2(face.X, face.Y);
        }
    }

    /// <summary>
    /// A path walker passes the waypoints it has reached and heads for the next at its wanted speed, braking into the last point and for corners.
    /// </summary>
    void WalkPath(World world, int i, in CharacterHot p, ref CharacterHot n, CharacterCold cold, ref StepState s)
    {
        var walk = world.Walkability;

        // A waypoint reached is passed in the same tick: the walker heads on for the next one rather than standing a tick at each.
        Vector3 waypoint;
        Vector2 to;
        float dist;
        bool last, reached;

        do
        {
            waypoint = cold.Path[n.PathCursor];
            to = new Vector2(waypoint.X - p.Position.X, waypoint.Z - p.Position.Z);
            dist = to.Length();
            last = n.PathCursor == cold.Path.Length - 1;
            s.Closing = last && cold.ArriveWithin > 0 && dist <= ArriveRadius;

            // The last point taken by another body: the walker stops short instead of pushing in (the original's agent, FUN_1401476f0).
            bool occupied = last && cold.ArriveWithin <= 0 && dist <= ArriveRadius && GoalOccupied(world, i, waypoint);
            reached = occupied || dist <= (last ? cold.ArriveWithin > 0 ? cold.ArriveWithin : FinalArrive : ArriveRadius * 0.5f)
                && (last || dist <= CornerReach || CanCut(walk, p.Position, cold.Path[n.PathCursor + 1]))
                // A corner the agent's velocity has carried past (it turns at its acceleration, not at once) is behind it, not circled.
                || !last && dist <= ArriveRadius && to.X * p.Velocity.X + to.Y * p.Velocity.Z < 0;

            if (reached)
            {
                n.PathCursor++;
                if (cold.BlockedRepaths != 0) cold.BlockedRepaths = 0;

                if (last) s.Finished = true;
            }
        }
        while (reached && !last);

        if (!reached)
        {
            s.Dir = to / dist;
            s.Desired = PathSpeed(world, i, p, n, cold, s.Water);

            // Braking into the last point, never past it.
            if (last)
                s.Desired = MathF.Min(s.Desired, MathF.Min(MathF.Sqrt(2 * PathAcceleration(cold) * dist), dist / s.Dt));
            else if (!cold.InCombat)
                (s.Dir, s.Desired) = CornerBrake(p.Velocity, to, s.Dir, s.Desired, PathAcceleration(cold));
        }

        // On the provisional part of a long route: ask again (the zones ahead may have loaded), following this one until the answer comes.
        if (n.PathCursor >= cold.PathExact) Reroute(world, ref n, cold);
    }

    /// <summary>
    /// The speed a path walker wants: its mode's cap (a carrier's walk raised to a run, a squad's force speed raising it) or a follower's own speed,
    /// within its top speed in water, then capped in its footwork, by its group, by sneaking, by a campaign's pace, and a crawler's.
    /// </summary>
    float PathSpeed(World world, int i, in CharacterHot p, in CharacterHot n, CharacterCold cold, int water)
    {
        byte mode = cold.ModeOverride ?? n.Mode;

        // A carrier in walk mode is raised to run mode (the original's speed selector, Observed; docs/game/carry-loot.md "Speed and
        // weight").
        if (mode == SpeedMode.Walk && !cold.Carrying.IsNone) mode = SpeedMode.Run;

        // Its squad template's force speed 0..2 replaces the AI's mode (FUN_140332390 never lowers an explicit task mode, so only a raise
        // is applied; docs/game/pathfinding.md "Force speed").
        if (!cold.IsPlayer && cold.Ai?.Squad is { ForceSpeed: < Squad.NoForceSpeed } forcing && mode < forcing.ForceSpeed)
            mode = forcing.ForceSpeed;

        float top = water > 0 && cold.Race is { } race ? Speed.InWater(water, n.MaxSpeed, race, cold.SwimSpeed) : n.MaxSpeed;
        float desired = cold.SpeedOverride > 0
            ? cold.SpeedOverride
            : n.Task == (byte)CharacterTask.Follow
                ? MathF.Min(top, cold.FollowSpeed)   // a follower's own desired speed, not its mode's cap
                : MathF.Min(top, SpeedMode.Cap(mode, n.WalkSpeed));

        // A fighter in its footwork closes in at a shuffle (docs/game/combat.md "Combat stance").
        if (cold.InCombat && !cold.CombatRunning && n.Task == (byte)CharacterTask.Attack)
            desired = MathF.Min(desired, CombatTuning.FootworkSpeed);

        if (mode == SpeedMode.Group && cold.SpeedOverride <= 0 && n.Task != (byte)CharacterTask.Follow)
            desired = MathF.Min(desired, _groupSpeeds.Of(world, i, cold, p.Position));

        if ((cold.Orders & CharacterOrders.Sneak) != 0 && cold.SpeedOverride <= 0)
            desired = MathF.Min(desired, Orders.SneakSpeed(desired, Orders.Stealth(cold)));

        if (cold.PaceCap is { } pace && n.Task == (byte)CharacterTask.GoTo && !cold.InCombat)
            desired = MathF.Min(desired, pace);   // a campaign follower near its trail point

        if (cold.Medical is { Crawling: true } && cold.CarriedBy.IsNone)
            desired = Speed.Crippled;   // the final override, after water and sneaking

        return desired;
    }

    /// <summary>
    /// A follower near its aim (Think set it as the goal) steers straight at it at its desired speed, capped by its top speed, never past it.
    /// </summary>
    static void SteerToAim(in CharacterHot p, in CharacterHot n, CharacterCold cold, ref StepState s)
    {
        var gap = new Vector2(n.Goal.X - p.Position.X, n.Goal.Y - p.Position.Z);
        float dist = gap.Length();

        if (dist > 1e-3f)
        {
            float top = s.Water > 0 && cold.Race is { } race ? Speed.InWater(s.Water, n.MaxSpeed, race, cold.SwimSpeed) : n.MaxSpeed;
            s.Dir = gap / dist;
            s.Desired = MathF.Min(MathF.Min(cold.FollowSpeed, top), dist / s.Dt);
            if ((cold.Orders & CharacterOrders.Sneak) != 0 && cold.SpeedOverride <= 0)
                s.Desired = MathF.Min(s.Desired, Orders.SneakSpeed(s.Desired, Orders.Stealth(cold)));

            if (cold.Medical is { Crawling: true }) s.Desired = MathF.Min(s.Desired, Speed.Crippled);
        }
    }

    /// <summary>
    /// A fighter out of its footwork turns at once to the target of its swing, or to the swing its defence follows.
    /// </summary>
    void FaceAttack(World world, int i, ref CharacterHot n, bool steering)
    {
        if (!steering && n.Task == (byte)CharacterTask.Attack && Fights is not null && Fights.SwingAim(world, i, out var swingAim))
            n.Yaw = MathF.Atan2(swingAim.X, swingAim.Y);   // a swing turns at once to its target (FUN_140666400, FUN_14060a590)
        else if (!steering && n.Task == (byte)CharacterTask.Attack && Fights is not null && Fights.DefenceAim(world, i, out var defenceAim))
            n.Yaw = MathF.Atan2(defenceAim.X, defenceAim.Y);   // a defence turns at once to the swing it follows (FUN_140609c70)
    }

    /// <summary>
    /// The end of a path: the walker stops, and its task goes on (the move order leaves the queue, a fighter asks for its next path soon, a
    /// wanderer waits).
    /// </summary>
    static void FinishPath(World world, int i, ref CharacterHot n, CharacterCold cold, ref StepState s)
    {
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
        n.PathCursor = 0;
        s.Speed = 0;
        cold.AgentSpeed = 0;

        // The end of an old path while the new one is on its way (an order given on the move) leaves the task to the new path.
        bool replaced = ((MoveFlags)n.Flags & (MoveFlags.NeedPath | MoveFlags.Pending)) != 0;

        if (!replaced && n.Task == (byte)CharacterTask.GoTo)
        {
            cold.WalkEnd = WalkEnd.Finished;

            // The move order leaves the queue; a queued move after it (shift + right click) starts: a path to it is asked for next tick.
            if (!PlayerOrders.Arrived(cold, ref n)) TaskStart.Step(world.Characters, i, CharacterTask.Idle);
        }
        else if (!replaced && n.Task == (byte)CharacterTask.Attack)
        {
            // A fighter at the end of its path to a moving target asks for the next one soon (PursuitSystem).
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
        else if (!replaced)
        {
            ulong roll = Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(i)), RngPurpose.Think, (ulong)world.Tick);
            n.TaskTime = WaitMin + Rng.Float(roll) * (WaitMax - WaitMin);
        }
    }

    /// <summary>
    /// The speed and way a walker moves at: a fighter's ramped steering speed, a near follower's desired speed, else the nav agent's velocity; then
    /// its facing.
    /// </summary>
    static void Drive(ReadOnlySpan<CharacterHot> prev, in CharacterHot p, ref CharacterHot n, CharacterCold cold, ref StepState s)
    {
        // A path is followed by the nav agent, whose speed moves toward the wanted one at its race's acceleration (PathAcceleration); a
        // fighter goes at its steering speed, which ramps by 15 / s up to its top speed and is kept between ticks (CharacterCold.SteerSpeed),
        // not restarted when a path ends.
        if (cold.InCombat)
        {
            cold.SteerSpeed = MathF.Min(cold.SteerSpeed + Acceleration * s.Dt, MathF.Max(n.MaxSpeed, s.Desired));
            s.Speed = cold.AgentSpeed = MathF.Min(s.Desired, cold.SteerSpeed);
        }
        else if (n.Task == (byte)CharacterTask.Follow && ((MoveFlags)n.Flags & MoveFlags.AnyPath) == 0)
        {
            s.Speed = cold.AgentSpeed = s.Desired;   // a follower near its slot steers straight (mode 2)
        }
        else
        {
            // The agent's velocity vector moves toward the wanted one (path direction x wanted speed) by at most acceleration x dt
            // (FUN_140147ca0), so a turn costs the speed the change of direction takes; the facing is the path direction, not the velocity's.
            var velocity = new Vector2(p.Velocity.X, p.Velocity.Z);
            var gap = s.Dir * s.Desired - velocity;
            float change = gap.Length(), most = PathAcceleration(cold) * s.Dt;
            velocity = change <= most ? s.Dir * s.Desired : velocity + gap * (most / change);
            s.Speed = cold.AgentSpeed = velocity.Length();
            s.Wanted = s.Dir;
            s.AgentWalk = true;
            s.Dir = s.Speed > 1e-4f ? velocity / s.Speed : Vector2.Zero;
        }

        Face(prev, p, ref n, cold, ref s);
    }

    /// <summary>
    /// The walker's facing: an agent walker faces the way it moves, a fighter in its footwork its target, anyone else turns toward its way at
    /// <see cref="TurnRate"/>.
    /// </summary>
    static void Face(ReadOnlySpan<CharacterHot> prev, in CharacterHot p, ref CharacterHot n, CharacterCold cold, ref StepState s)
    {
        if (s.AgentWalk)
        {
            // The facing is the way the agent moves (its velocity), at once: the body faces its motion through a turn instead of sliding
            // sideways along the arc; the drawn body follows the facing at its own rate (AnimationSystem.FollowFacing). Standing up to speed,
            // it faces the path direction.
            var along = s.Dir != Vector2.Zero ? s.Dir : s.Wanted;

            if (along != Vector2.Zero)
            {
                s.Facing = true;
                n.Yaw = MathF.Atan2(along.X, along.Y);
            }
        }
        else if (s.Dir != Vector2.Zero && FaceOf(prev, n, cold) is { } face)
        {
            // In its footwork a fighter walks its path whichever way it lies and turns to face its target (the side steps, docs/animation.md
            // "Side steps").
            s.Facing = true;
            n.Yaw = MathF.Atan2(face.X, face.Y);
        }
        else if (s.Dir != Vector2.Zero)
        {
            (s.Dir, s.Speed) = Turn(p.Yaw, s.Dir, s.Speed, s.Dt);
            n.Yaw = MathF.Atan2(s.Dir.X, s.Dir.Y);   // also while it turns on the spot
        }
    }

    /// <summary>
    /// The step itself: the walker's drive plus the separation push and the body contact, its own step alone where the push leaves it nowhere to
    /// stand, then placed where it can stand (<see cref="Place"/>).
    /// </summary>
    static void StepBody(World world, int i, in CharacterHot p, ref CharacterHot n, CharacterCold cold, ref StepState s)
    {
        var walk = world.Walkability;
        float dt = s.Dt;

        // Separation from the neighbours of the last tick, visited in the grid's fixed order.
        var push = new SeparationState { Table = world.Characters, Self = i };
        world.Grid.Query(p.Position.X, p.Position.Z, SeparationRadius, ref push, AddSeparation);

        // Closing on its spot it is not held off by those standing round it (they are pushed aside as it arrives); a sitter is not moved at all.
        bool seated = Seated(n, cold);

        // A steering fighter keeps it too: its repulsion is too weak to keep bodies apart in a crowd, and without it Contact shoves back out what
        // the steering walks in.
        if (s.Closing || seated) push.Sum = Vector2.Zero;

        var at = new Vector2(p.Position.X, p.Position.Z);
        var drive = s.Closing || seated ? s.Dir * s.Speed : Blocked(world, cold, ref push, at, s.Dir * s.Speed);
        var step = (drive + push.Sum * SeparationStrength) * dt;
        var shove = s.Closing || seated ? Vector2.Zero : Contact(world, i, cold, ref push, at + step);
        step += shove;
        float x = p.Position.X + step.X, z = p.Position.Z + step.Y, y = 0;
        bool stands = false;

        // Pushed by its neighbours where it cannot stand (over a hole furniture cuts in a floor): its own step alone.
        if ((push.Sum != Vector2.Zero || shove != Vector2.Zero) && !(stands = walk.TryStand(x, z, p.Position.Y, out y)))
        {
            step = s.Dir * s.Speed * dt;
            (x, z) = (p.Position.X + step.X, p.Position.Z + step.Y);
        }

        if (step != Vector2.Zero) Place(world, i, p, ref n, cold, ref s, x, y, z, stands);
    }

    /// <summary>
    /// One neighbour of the separation query (<see cref="StepBody"/>): its repulsion, and its slot for the body contact.
    /// </summary>
    static void AddSeparation(int other, ref SeparationState s)
    {
        if (other == s.Self || s.Count >= MaxSeparationContributions) return;

        var all = s.Table.Previous;
        var d = new Vector2(all[s.Self].Position.X - all[other].Position.X, all[s.Self].Position.Z - all[other].Position.Z);
        float len = d.Length();
        if (len >= SeparationRadius) return;

        // A carried body is on its carrier's shoulder and a downed or dead one lies out of the flocking list: neither is in anyone's way
        // (docs/game/pathfinding.md "Bodies in the way").
        if (s.Table.Cold(other) is { } them && (!them.CarriedBy.IsNone || them.Medical is { Incapacitated: true }))
            return;

        s.Near[s.Count++] = other;

        // Exactly on top of each other: the lower slot is pushed one way, the other the opposite.
        s.Sum += (len > 1e-4f ? d / len : (s.Self < other ? Vector2.UnitX : -Vector2.UnitX)) * (100 * (1 - len / SeparationRadius));
    }

    /// <summary>
    /// Puts the walker at its stepped point (<paramref name="x"/>, <paramref name="z"/>) when it can stand there, or steps it out of a hole in the
    /// mesh; else it stands, and a blocked walker asks for its path again or gives the walk up.
    /// </summary>
    static void Place(World world, int i, in CharacterHot p, ref CharacterHot n, CharacterCold cold, ref StepState s, float x, float y, float z,
        bool stands)
    {
        var walk = world.Walkability;

        // One already off every surface near its height (a stale height) is not held there: it steps down onto what is below, unless it walks out
        // of a hole along its path (below).
        if (!stands) stands = walk.TryStand(x, z, p.Position.Y, out y);

        // On its navmesh path, a drop or climb beyond the stand limits that the corridor itself takes (a stair top short of a floor's edge)
        // is stepped: the layer is looked for at the path's height there (docs/game/pathfinding.md "Heights").
        if (!stands && !s.Steering && CorridorHeight(n, cold, x, z) is { } along && MathF.Abs(along - p.Position.Y) > CorridorSlack)
            stands = walk.TryStand(x, z, along, out y);

        bool offSurface = !stands && !walk.IsWalkable(p.Position.X, p.Position.Z, p.Position.Y);

        // Shoved into something that cuts a hole in the mesh (a market stall), it walks out along its path toward the surface the path starts
        // on, at its own height, also over a floor below the hole (a chest on an upper floor, #460). From the hole's edge, where it still stands,
        // only once a step there was refused and its path asked again: one merely pushed to the side of a table first gets a path round it.
        bool inHole = !stands && !walk.IsOnSurface(p.Position.X, p.Position.Z, p.Position.Y);
        bool held = offSurface || cold.BlockedRepaths > 0;
        bool escaping = inHole && held && !s.Steering && EscapesToward(walk, n, cold, p.Position, x, z);
        if (escaping) y = p.Position.Y;

        if (stands || offSurface && walk.IsWalkable(x, z) || escaping)
        {
            n.Position = new Vector3(x, y, z);
            if (s.Speed > 0.1f && !s.Facing) n.Yaw = MathF.Atan2(s.Dir.X, s.Dir.Y);
        }
        else if (s.Steering)
        {
            s.Speed = 0;   // into a wall or off the ground: it stands this tick
        }
        else if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0)
        {
            // Blocked with a path already asked for: it stands until the answer comes, so each answer counts as one try below, not each tick
            // waited.
            s.Speed = 0;
            s.Dir = Vector2.Zero;
        }
        else
        {
            // Blocked (no surface to walk on there): it stands and asks for the path again, or gives the walk up.
            s.Speed = 0;
            s.Dir = Vector2.Zero;
            if (!AsksAgainBlocked(world, ref n, cold)) DropBlocked(world, i, ref n, cold, x, z);
        }
    }

    /// <summary>
    /// Whether a walker in a hole in the mesh or at its edge (shoved into something that cuts the hole) may step to (<paramref name="x"/>,
    /// <paramref name="z"/>): only nearer its path's current point, and only when that point is a surface it can stand on, so it walks out and never
    /// further in (docs/game/pathfinding.md "Own builder", shoved into furniture).
    /// </summary>
    static bool EscapesToward(IWalkability walk, in CharacterHot n, CharacterCold cold, Vector3 from, float x, float z)
    {
        if ((n.Flags & (ushort)MoveFlags.HasPath) == 0 || n.PathCursor >= cold.Path.Length) return false;

        var point = cold.Path[n.PathCursor];
        var aim = new Vector2(point.X, point.Z);
        if (Vector2.Distance(new Vector2(x, z), aim) >= Vector2.Distance(new Vector2(from.X, from.Z), aim))
            return false;

        return walk.TryStand(point.X, point.Z, from.Y, out _);
    }

    /// <summary>
    /// Whether a walker at <paramref name="from"/> can step straight toward <paramref name="to"/> for <see cref="ArriveRadius"/> units, its height
    /// carried on as each step's is: a corner is cut early only then (engine choice; Observed: zone 22.10, a walker beside a door stair's foot cut
    /// its corner up the stair's side and stood there).
    /// </summary>
    static bool CanCut(IWalkability walk, Vector3 from, Vector3 to)
    {
        var leg = new Vector2(to.X - from.X, to.Z - from.Z);
        float length = leg.Length();
        if (length < 1e-3f) return true;

        float y = from.Y;
        for (float d = 1; d <= MathF.Min(length, ArriveRadius); d += 1)
            if (!walk.TryStand(from.X + leg.X * (d / length), from.Z + leg.Y * (d / length), y, out y)) return false;

        return true;
    }

    /// <summary>
    /// The height of the walker's navmesh path at (x, z): the leg to its next point, the point's height projected along it; null off a path or on its
    /// provisional part (engine choice: the walker's own height alone is held to <c>StandDrop</c>, and a 17-unit stair top below a floor's edge,
    /// which the mesh joins over a slope of ~2 units, stopped walkers there, Observed: Ancient Lab with TwoStorey, zone 28.60).
    /// </summary>
    static float? CorridorHeight(in CharacterHot n, CharacterCold cold, float x, float z)
    {
        if (((MoveFlags)n.Flags & MoveFlags.HasPath) == 0 || n.PathCursor >= cold.Path.Length || n.PathCursor >= cold.PathExact)
            return null;

        var b = cold.Path[n.PathCursor];
        if (n.PathCursor == 0) return b.Y;

        var a = cold.Path[n.PathCursor - 1];
        var leg = new Vector2(b.X - a.X, b.Z - a.Z);
        float length2 = leg.LengthSquared();
        float t = length2 < 1e-6f ? 1 : Math.Clamp(((x - a.X) * leg.X + (z - a.Z) * leg.Y) / length2, 0, 1);
        return a.Y + (b.Y - a.Y) * t;
    }

    /// <summary>
    /// The nav agent's braking for the corner it heads for (<b>Verified</b>, <c>FUN_140147fd0</c> with the corner from <c>FUN_140146a60</c>;
    /// docs/game/pathfinding.md): once the corner along its heading is within its stopping distance (<see cref="CornerMargin"/> added), its wanted
    /// speed drops to sqrt(2 a <see cref="CornerLength"/> |sin θ|), θ between its velocity and the way to the corner; when even the wanted speed fits
    /// under that, it steers square across its velocity toward the corner's side at that speed.
    /// </summary>
    static (Vector2 Dir, float Desired) CornerBrake(Vector3 moving, Vector2 to, Vector2 dir, float desired, float acceleration)
    {
        var velocity = new Vector2(moving.X, moving.Z);
        float speed = velocity.Length();
        if (speed <= 1e-4f) return (dir, desired);

        var heading = velocity / speed;
        if (Vector2.Dot(to, heading) - CornerMargin >= speed * speed / (2 * acceleration)) return (dir, desired);

        var across = new Vector2(heading.Y, -heading.X);
        float lateral = Vector2.Dot(across, dir);
        float limit = 2 * acceleration * CornerLength * MathF.Abs(lateral);
        if (desired * desired <= limit) return (lateral < 0 ? -across : across, desired);

        return (dir, MathF.Sqrt(limit));
    }

    /// <summary>
    /// Whether a medic's ask to stand still holds the character (<see cref="CharacterCold.StandStillUntil"/>): the original's patient takes 62
    /// Standing still as an urgent goal, which its orders outrank and whose row needs no attack on it (<c>!isAggressionTowardsMe</c>). Engine
    /// reading: an NPC (the player's characters move by orders), not fighting or fleeing.
    /// </summary>
    static bool AskedToStandStill(in CharacterHot n, CharacterCold cold, long tick) =>
        cold.StandStillUntil > tick && !cold.IsPlayer && !cold.InCombat && !cold.Fleeing && cold.CarriedBy.IsNone
        && n.Task != (byte)CharacterTask.Attack;

    /// <summary>
    /// The factor on a character's step time: <see cref="OffViewTime"/> out of view.
    /// </summary>
    static float OffViewTimeFactor(World world, Vector3 at) => world.View is { } view && !MedicalClock.OnScreen(view, at) ? OffViewTime : 1;

    /// <summary>
    /// The solid radius of a body: <see cref="BodyShare"/> of its footprint (a human's when it has no race data).
    /// </summary>
    static float BodyRadius(CharacterCold cold) => BodyShare * (cold.FootprintRadius > 0 ? cold.FootprintRadius : CombatTuning.DefaultFootprint);

    /// <summary>
    /// How far a character stepped to <paramref name="at"/> is put out of the standing bodies among its separation neighbours (partition-safe: the
    /// last tick's state of the others): half of each overlap with the other's last position, all of it against one held in a bed or cage, averaged
    /// over the bodies it overlaps (a sum overshoots in a packed crowd and the whole crowd shakes). Lying, sitting and carried bodies are not in the
    /// way (a walker only steers past a sitter, so a ring of sitters cannot wall it in), and a carried or bedded one is not moved. Not where the
    /// other's velocity takes it: in a jam that is where it wants to go, not where it gets to.
    /// </summary>
    static Vector2 Contact(World world, int self, CharacterCold cold, ref SeparationState near, Vector2 at)
    {
        if (!cold.CarriedBy.IsNone || cold.InSomething != Locks.HeldState.None) return Vector2.Zero;

        var table = world.Characters;
        var all = table.Previous;
        float mine = BodyRadius(cold);
        var shove = Vector2.Zero;
        int contacts = 0;

        for (int k = 0; k < near.Count; k++)
        {
            int other = near.Near[k];
            if (table.Cold(other) is not { } them || them.Medical is { Incapacitated: true }) continue;

            ref readonly var o = ref all[other];
            if (Seated(o, them)) continue;

            var d = at - new Vector2(o.Position.X, o.Position.Z);
            float len = d.Length(), reach = mine + BodyRadius(them);
            if (len >= reach) continue;

            var normal = len > 1e-4f ? d / len : self < other ? Vector2.UnitX : -Vector2.UnitX;
            shove += normal * ((reach - len) * (them.InSomething != Locks.HeldState.None ? 1 : 0.5f));
            contacts++;
        }

        return contacts > 1 ? shove / contacts : shove;
    }

    /// <summary>
    /// The velocity a character steps by without the part of it into a standing body it already touches (engine choice), so the ranks behind a
    /// blocked one do not press it into those in front and a crowd does not squash; what is left slides it round them. The bodies counted are
    /// <see cref="Contact"/>'s.
    /// </summary>
    static Vector2 Blocked(World world, CharacterCold cold, ref SeparationState near, Vector2 at, Vector2 drive)
    {
        if (drive == Vector2.Zero || !cold.CarriedBy.IsNone) return drive;

        var table = world.Characters;
        var all = table.Previous;
        float mine = BodyRadius(cold);

        for (int k = 0; k < near.Count; k++)
        {
            int other = near.Near[k];
            if (table.Cold(other) is not { } them || them.Medical is { Incapacitated: true }) continue;

            ref readonly var o = ref all[other];
            if (Seated(o, them)) continue;

            var d = at - new Vector2(o.Position.X, o.Position.Z);
            float len = d.Length();
            if (len >= mine + BodyRadius(them) || len < 1e-4f) continue;

            var normal = d / len;
            float into = -Vector2.Dot(drive, normal);
            if (into > 0) drive += normal * into;
        }

        return drive;
    }

    /// <summary>
    /// Whether another standing character's footprint covers <paramref name="goal"/> (last tick's positions).
    /// </summary>
    static bool GoalOccupied(World world, int self, Vector3 goal)
    {
        var state = new OccupiedState { Table = world.Characters, Self = self, Goal = new Vector2(goal.X, goal.Z) };
        world.Grid.Query(goal.X, goal.Z, 30, ref state, CoversGoal);
        return state.Found;
    }

    /// <summary>
    /// One character near the goal (<see cref="GoalOccupied"/>): a standing one whose footprint covers it.
    /// </summary>
    static void CoversGoal(int other, ref OccupiedState s)
    {
        if (s.Found || other == s.Self || !s.Table.Previous[other].Alive || s.Table.Cold(other) is not { CarriedBy.IsNone: true } c
            || c.Medical is { Incapacitated: true })
        {
            return;
        }

        var at = s.Table.Previous[other].Position;
        float r = CombatTuning.Footprint(c);
        if (Vector2.DistanceSquared(new Vector2(at.X, at.Z), s.Goal) < r * r) s.Found = true;
    }

    /// <summary>
    /// Sitting (resting, on a chair or the floor, or on a seat it uses) with no path to follow: the original does not push sitters (Observed in
    /// play).
    /// </summary>
    static bool Seated(in CharacterHot h, CharacterCold c) => ((MoveFlags)h.Flags & MoveFlags.HasPath) == 0
        && (h.Task == (byte)CharacterTask.Rest
            || h.Task == (byte)CharacterTask.Operate && c.Running is { Implicit: true, Row: TaskType.SitAround } run
            && run.Runs(CharacterTask.Operate));

    /// <summary>
    /// The way to the target a fighter in its footwork faces (<see cref="CharacterCold.FaceSlot"/>, the last tick's place), or null.
    /// </summary>
    static Vector2? FaceOf(ReadOnlySpan<CharacterHot> prev, in CharacterHot n, CharacterCold cold)
    {
        int t = cold.FaceSlot;
        if (t < 0 || t >= prev.Length || !prev[t].Alive || n.Task != (byte)CharacterTask.Attack || !cold.InCombat || cold.CombatRunning)
            return null;

        var d = new Vector2(prev[t].Position.X - n.Position.X, prev[t].Position.Z - n.Position.Z);
        return d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : null;
    }

    #endregion
}