using Meitou.Data.Gameplay.Ai;
using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// Movement on the walkability stand-in (docs/game/pathfinding.md "Movement") and the first tasks: wander within the squad's home
/// radius, go to (move orders), follow the squad leader.
/// <list type="bullet">
/// <item>Speed: the stat S in decimetres (world units) per second, capped by the speed mode (walk speed, 55, none); the character
/// follows a path at that speed at once (the nav agent, Observed), speeds up by 15 units/s per second only in combat (<see cref="Acceleration"/>, the combat
/// steering), and stops at once on arrival.</item>
/// <item>Separation: the repulsion along self-other is nonzero only closer than R and proportional to 100 x (1 - d/R), over at most
/// 26 neighbours (Observed); R is twice the footprint radius 4 of a human (<see cref="SeparationRadius"/>), and the scale from that
/// proportionality to a speed is an engine choice (<see cref="SeparationStrength"/>).</item>
/// <item>Body contact (engine choice; the original only avoids the standing bodies round a character, no physical collision was found: docs/game/pathfinding.md "Bodies in the way"): two standing bodies (<see cref="BodyShare"/> of their footprints) do not stay
/// overlapped. Each takes half of the overlap between its stepped position and the other's last position, averaged over the bodies it overlaps, so a walker shoves a stander
/// aside; an imprisoned or bedded one does not give. Its own velocity into a body it already touches is dropped, so a crowd pressing on does not squash its front. A sitter is never moved, by contact or separation (the original pushes no sitter, Observed), and is not solid to others. Downed, dead and carried bodies neither push (separation) nor
/// block (contact, the walk's covered goal, root motion): the original's avoidance list leaves them out (docs/game/pathfinding.md "Bodies in the way").</item>
/// <item>Paths: a character that wants one is flagged in Think; the Schedule step of the next tick asks the <see cref="PathService"/>;
/// an answer is applied in a serial step, so a path is never read while it is replaced.</item>
/// </list>
/// Water states cap or replace the top speed (<see cref="WaterStateAt"/>, <see cref="Speed.InWater"/>). In water a fighter's footwork is scaled too (x0.75 / 0.5 / 0.4). Not modelled yet (Unknown or later): slopes (the original has no slope penalty), the RACE
/// <c>combat move speed mult</c>. Far trips take the roads in the path layer (<see cref="CharacterCold.RoadPreference"/>).
/// <item>Followers (the original's <c>Task_Follow</c>, docs/game/pathfinding.md "Squads travelling together"): a squad mate aims at its <see cref="Formation.Layout"/> slot turned
/// onto a heading that eases toward the leader's, one second ahead of the leader; anyone else keeps a drawn 20..40 from its target. Its speed is the original's desired speed
/// (<see cref="FollowSpeedFor"/>), capped by its top speed only (engine choice: the original's cap by speed mode is Unknown, and only this lets it settle in its slot). Within
/// <see cref="DirectDistance"/> it steers straight at the aim, else it takes paths, and for <see cref="FollowPathSeconds"/> after a blocked step (engine choice). Not done: the
/// original's aim outside a building the follower cannot enter, and its 50-100 hysteresis (practically nil).</item>
/// <item>Turning: a walker out of combat faces the way it moves while its velocity follows the agent's acceleration (the original's model, Verified); its drawn
/// body turns after the facing at a limited rate (<see cref="AnimationSystem.FollowFacing"/>). Fighters and direct followers keep the heading limit <see cref="TurnRate"/>
/// and the cosine cut (engine choice).</item>
/// Queued move orders (row-29 entries of <see cref="CharacterCold.OrderQueue"/>) and the formation (<see cref="Formation"/>) exist.
/// </summary>
public sealed class MovementSystem(PathService paths) : ITickSystem, IDisposable, ITaskOwner, ITaskHandler, IOrderOwner
{
    /// <summary>The fights the steering reads (null: no fight steering, as in worlds without combat; the world's combat system unless set).</summary>
    public Combat.CombatSystem? Fights { get; set; }
    /// <summary>Speed gained per second in combat (docs/game/pathfinding.md, the combat / follow steering: "ramps up by 15 per second").</summary>
    public const float Acceleration = 15;
    /// <summary>R of the separation steering: neighbour radius plus margin; a human's footprint radius is 4.</summary>
    public const float SeparationRadius = 8;
    /// <summary>Speed in units/s of a repulsion of magnitude 100 (engine choice).</summary>
    public const float SeparationStrength = 0.15f;
    public const int MaxSeparationContributions = 26;
    /// <summary>The share of the footprint radius a body is solid over (engine choice: 2.4 units for a human, about half the shoulders' width).</summary>
    public const float BodyShare = 0.6f;
    /// <summary>A character within this distance of a waypoint has reached it.</summary>
    public const float ArriveRadius = 10;
    /// <summary>
    /// How near (units) a walker comes to a corner it may not cut (<see cref="CanCut"/>) before it turns for the next waypoint (engine choice: about a step).
    /// </summary>
    public const float CornerReach = 2;

    /// <summary>
    /// Whether a walker at <paramref name="from"/> can step straight toward <paramref name="to"/> for <see cref="ArriveRadius"/> units, its height carried on as each step's is: a corner
    /// is cut early only then (engine choice; Observed: zone 22.10, a walker beside a door stair's foot cut its corner up the stair's side and stood there).
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
    /// How near the last point of a path counts as arrived (<b>Verified</b>, the nav agent's <c>FUN_140144440</c>: 0.01 squared Havok units, 1 unit); the agent brakes into it
    /// (<see cref="PathAcceleration"/>), so a walker stops on its goal rather than a stride short of it.
    /// </summary>
    public const float FinalArrive = 1;

    /// <summary>
    /// The nav agent's acceleration in units per second squared (<b>Verified</b>, <c>FUN_140147ca0</c>): the RACE <c>pathfind acceleration</c> (Havok units) x 10. A walker's speed moves toward the
    /// wanted one by at most this x dt, and near its last point it is held to sqrt(2 x this x the distance left), so it stops there.
    /// </summary>
    public static float PathAcceleration(CharacterCold cold) => (cold.Race?.PathfindAcceleration ?? 24) * 10;
    /// <summary>The <see cref="CharacterCold.ArriveWithin"/> of a walk to a spot (engine choice: a barman stands at his node behind the counter, not a stride off it).</summary>
    public const float SpotArrive = 2;
    /// <summary>How fast a formation follower's heading eases toward its leader's, per second, while the leader moves (the original's 0.5).</summary>
    public const float HeadingEase = 0.5f;
    /// <summary>A leader slower than this counts as standing (its follower's speed bands then apply).</summary>
    public const float StandingSpeed = 0.1f;
    /// <summary>Seconds a follower blocked on its way straight to its slot takes paths instead (engine choice).</summary>
    public const float FollowPathSeconds = 3;
    /// <summary>A follower on paths (far off, or blocked) farther than this from its slot asks for a path to it.</summary>
    public const float FollowSlack = 14;
    /// <summary>Seconds between a follower's path requests, and the wait after a failed one.</summary>
    public const float RepathSeconds = 0.5f, FailedPathWait = 2;
    /// <summary>A wanderer waits this many seconds (uniform) after reaching a place.</summary>
    public const float WaitMin = 3, WaitMax = 10;
    /// <summary>A follower nearer its aim than this steers straight at it; farther, it takes paths (the original's 50).</summary>
    public const float DirectDistance = 50;
    /// <summary>
    /// Radians per second the heading of a path follower turns at most (engine choice: a Kenshi recording turns a standing character about 100 degrees in
    /// 0.15 s before it runs; docs/game/pathfinding.md "Turning").
    /// </summary>
    public const float TurnRate = 4 * MathF.PI;
    /// <summary>
    /// Seconds between the requests of a walker on the provisional part of a long route (<see cref="PathResult.Exact"/>), which ask for the path again as
    /// zones load ahead of it, and how many times running it may be stopped there by ground the route did not know before it gives up (engine choices).
    /// </summary>
    public const float ProvisionalRepathSeconds = 2;
    public const int ProvisionalBlockedLimit = 5;
    /// <summary>
    /// How many times running a walker stopped on the navmesh part of its path (no ground it can stand on ahead) asks for the path again from where it stands before it drops it
    /// and the order (engine choice: from a slightly different spot the path can start elsewhere, docs/game/pathfinding.md "Own builder").
    /// </summary>
    public const int BlockedRepathLimit = 3;

    public PathService Paths { get; } = paths;

    /// <summary>The system owns its path service (the host hands it over at construction), so disposing the system stops the path thread.</summary>
    public void Dispose() => Paths.Dispose();

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        ApplyAnswers(world);
        var table = world.Characters;
        foreach (var command in commands)
        {
            if (command is RepathCommand repath)
            {
                for (int s = 0; s < table.Next.Length; s++)
                {
                    ref var c = ref table.Next[s];
                    if (!c.Alive || c.Task != (byte)CharacterTask.GoTo || (c.Flags & (ushort)(MoveFlags.Pending | MoveFlags.HasPath)) == 0) continue;
                    // A walker whose way on does not lead through a changed zone keeps its path (a long walk loads zones ahead all the way, and each would
                    // otherwise turn every walker about). One waiting for an answer asks again: it may have been made on the old meshes.
                    if (repath.Zones is { } zones && (c.Flags & (ushort)MoveFlags.Pending) == 0 && !Crosses(c.Position, table.Cold(s)!.Path, c.PathCursor, zones)) continue;
                    c.Flags = (ushort)((c.Flags & ~(ushort)(MoveFlags.Pending | MoveFlags.HasPath)) | (ushort)MoveFlags.NeedPath);
                    c.PathCursor = 0;
                    table.Cold(s)!.PathRequest = 0;
                }
                continue;
            }
            if (command is SandboxMovement sandbox)
            {
                if (table.TryResolveNext(sandbox.Character, out int target))
                {
                    var tc = table.Cold(target)!;
                    if (sandbox.Mode is { } mode) tc.ModeOverride = mode;
                    if (sandbox.Speed is { } fixedSpeed) tc.SpeedOverride = MathF.Max(fixedSpeed, 0);
                }
                continue;
            }
            if (command is FollowOrder follow)
            {
                // Row 45 Bodyguard or 31 Staying close to each of the characters (the selection when empty) but the target.
                if (!table.TryResolveNext(follow.Target, out int led)) continue;
                foreach (var id in follow.Characters.Count > 0 ? follow.Characters : world.Player.Selection.ToArray())
                    if (table.TryResolveNext(id, out int s) && s != led)
                        PlayerOrders.Give(world, s, new OrderEntry(follow.Guard ? TaskType.BODYGUARD : TaskType.STAY_CLOSE_TO_TARGET, this) { Target = follow.Target }, follow.Queued);
                continue;
            }
            if (command is not MoveOrder order) continue;
            // An empty list orders the selection (the UI sends orders for whatever is selected when the click happens).
            var who = order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray();
            var slots = new List<int>();
            foreach (var id in who)
            {
                if (!table.TryResolveNext(id, out int found)) continue;
                if (!TaskStart.Eligible(world, table.Cold(found)!, TaskType.MOVE_CUS_ORDERED))
                {
                    // A prisoner stays in its cage (row 29 is not registered for the imprisoned or chained): the order waits in its queue.
                    PlayerOrders.Give(world, found, new OrderEntry(TaskType.MOVE_CUS_ORDERED, this) { Point = order.Target }, order.Queued);
                    continue;
                }
                if (table.Cold(found)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, id);   // an order gets a sleeper up
                slots.Add(found);
            }
            var targets = Formation.Scatter(slots.Count, new Vector2(order.Target.X, order.Target.Z));
            for (int k = 0; k < slots.Count; k++)
            {
                int slot = slots[k];
                var target = targets[k];
                table.Cold(slot)!.FollowOrder = CharacterId.None;
                // Shift + right click: after the order in hand.
                PlayerOrders.Give(world, slot, new OrderEntry(TaskType.MOVE_CUS_ORDERED, this) { Point = new Vector3(target.X, order.Target.Y, target.Y) }, order.Queued);
            }
        }
        foreach (var squad in world.Squads.All) RefreshFormation(world, squad);
    }

    /// <summary>
    /// Rebuilds a squad's <see cref="Squad.FormationSlots"/> when its formation, head, members or their roles changed (serial). The head is the leader, or the squad mate the
    /// leader follows (the original's task-31 head). The draws are keyed by the squad and the key, so a rebuild of the same squad gives the same slots.
    /// </summary>
    static void RefreshFormation(World world, Squad squad)
    {
        var table = world.Characters;
        squad.ForceSpeed = world.ForcedSpeeds is { } forced && forced.TryGetValue(squad.TemplateId, out byte force) ? force : Squad.NoForceSpeed;
        var head = FormationHead(table, squad);
        int type = squad.Faction >= 0 && world.FactionFormations is { } formations && squad.Faction < formations.Count ? formations[squad.Faction] : (int)SquadFormation.Random;
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
            return new FormationMember(id, c.Role, c.Animal is not null, Formation.RadiusScale * (c.FootprintRadius > 0 ? c.FootprintRadius : Combat.CombatTuning.DefaultFootprint));
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
        squad.FormationSlots = Formation.Layout((SquadFormation)type, Of(head), others, slaves, () => Rng.Float(Rng.Hash(seed, 0, RngPurpose.Formation, draws++)));
    }

    /// <summary>The character a squad's formation is laid out from: its leader, or the squad mate the leader follows.</summary>
    static CharacterId FormationHead(CharacterTable table, Squad squad)
    {
        var leader = squad.Leader;
        if (leader.IsNone || !table.IsAlive(leader)) return leader;
        var followed = table.Cold(leader.Slot)!.FollowOrder;
        return table.Next[leader.Slot].Task == (byte)CharacterTask.Follow && !followed.IsNone && squad.Members.Contains(followed) && table.IsAlive(followed) ? followed : leader;
    }

    /// <summary>How far above or below the given height the floor <see cref="GoToFloor"/> walks onto may be (engine choice: a storey is about 66).</summary>
    public const float FloorSlack = 20;

    /// <summary>
    /// <see cref="GoTo"/> onto the floor at <paramref name="at"/>'s height (serial phases only). Furniture cuts holes in a building's floor mesh and a goal there resolves to the
    /// storey above or the ground below, so the goal moves to the nearest floor point (<see cref="IWalkability.FloorNear"/>) and keeps its height. Returns the X/Z goal.
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
        n.Task = (byte)CharacterTask.GoTo;
        n.Goal = target;
        n.Mode = mode;
        n.Flags = (ushort)((n.Flags & ~(ushort)MoveFlags.Pending) | (ushort)MoveFlags.NeedPath);
        cold.PathRequest = 0;
    }

    /// <summary>
    /// <see cref="FollowOrder"/>: each awake, free character (not the target) drops what it does and follows the target (<see cref="CharacterCold.FollowOrder"/>); a sleeper gets up first.
    /// The order is row 45 Bodyguard or 31 Staying close; a job's follow (<paramref name="source"/> <see cref="TaskSource.Job"/>) runs the job's <paramref name="row"/>.
    /// </summary>
    internal static void Follow(World world, FollowOrder order, TaskSource source = TaskSource.Order, int job = -1, TaskType? row = null)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int led)) return;
        foreach (var id in order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray())
        {
            if (!table.TryResolveNext(id, out int s) || s == led || table.Cold(s) is not { IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true } cold) continue;
            if (cold.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, id);
            TaskStart.Start(world, s, new TaskRequest(CharacterTask.Follow, row ?? (order.Guard ? TaskType.BODYGUARD : TaskType.STAY_CLOSE_TO_TARGET), source) { Job = job, Target = order.Target });
        }
    }

    /// <summary>
    /// A follow row (31, 45, 289) whose fight was a step of it (<see cref="TaskStart.Start"/> keeps the row's provenance over a goal's or reaction's fight) is over: the character stands idle
    /// out of the fight and its provenance still names the row with the fight as its task.
    /// </summary>
    internal static bool FightStepOver(CharacterTable table, int slot, CharacterCold cold) =>
        cold.Running is { Task: CharacterTask.Attack } run && Production.PermaJobs.Follows(run.Row) && table.Next[slot].Task == (byte)CharacterTask.Idle && !cold.InCombat;

    /// <summary>
    /// The follow row takes its follow up again where its fight left it (serial; the decision step, for an order or a job: the original's plan goes on with the row's follow after 13): its walk
    /// is asked anew under the row's provenance. False, with the row dropped (no provenance, no <see cref="CharacterCold.FollowOrder"/>), when the followed character is gone.
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

    public void RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(CharacterTask.GoTo, this);
        tasks.Register(CharacterTask.Follow, this);
        tasks.Register(CharacterTask.Idle, this);
        tasks.Register(CharacterTask.Wander, this);
    }

    /// <summary>A move order (29) needs an awake character (one out cold keeps it queued until it wakes); a follow order (31, 45) a target that is there, and an awake, free character (not caged or carried).</summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        if (order.IsMove) return table.Cold(slot)!.Medical?.Incapacitated == true ? OrderCheck.Blocked() : OrderCheck.Run;
        if (!table.TryResolveNext(order.Target, out int led) || led == slot) return OrderCheck.Gone;
        return table.Cold(slot) is { IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true } ? OrderCheck.Run : OrderCheck.Blocked();
    }

    /// <summary>A sleeper gets up first; then the move's walk or the follow starts (<see cref="ITaskHandler.Start"/>).</summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        if (table.Cold(slot)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, table.IdOf(slot));
        TaskStart.Start(world, slot, order.IsMove
            ? new TaskRequest(CharacterTask.GoTo, TaskType.MOVE_CUS_ORDERED, TaskSource.Order) { Point = order.Point }
            : new TaskRequest(CharacterTask.Follow, order.Row, TaskSource.Order) { Target = order.Target });
    }

    /// <summary>
    /// A move order's walk (row 29: <see cref="CharacterCold.OrderMove"/>, the Hold point follows it) to <see cref="TaskRequest.Point"/>'s floor, or a follow of <see cref="TaskRequest.Target"/>
    /// (<see cref="CharacterCold.FollowOrder"/>; row 45 guards it). An order's follow breaks a circle of followers back to the character (<see cref="BreakCircle(CharacterTable, CharacterId)"/>).
    /// An engine walk goes to <see cref="TaskRequest.Goal"/> as given (<see cref="GoTo"/>); an engine's idle, wander or follow of the squad's leader (no target) stops the character on it.
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
            cold.OrderMove = request.Row == TaskType.MOVE_CUS_ORDERED;
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

    /// <summary>A walk or a follow holds nothing: the next start sets the movement (the follow's <see cref="CharacterCold.FollowOrder"/> stays for the task it goes back to).</summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason) { }

    /// <summary>
    /// The original drops what would close a circle back to the character just ordered to follow or guard (<paramref name="ordered"/>; <c>FUN_1405c8c10</c>, run by the order giver
    /// <c>FUN_1405c8da0</c> after it adds a one-shot order or a job 31 or 45, <b>Verified</b>; docs/game/animals.md "Following"). Walking the chain from the ordered character (a character
    /// once at most), whoever it reaches loses its one-shot follow of the ordered character and every Staying close / Bodyguard job on it; the walk goes on through the target of the
    /// one-shot follow and of each such job. The jobs are only looked at while the character has a Staying close 31 job (so an animal's 289 and its Bodyguard are not walked).
    /// </summary>
    internal static void BreakCircle(World world, CharacterId ordered)
    {
        if (world.Characters.TryResolveNext(ordered, out int start)) BreakCircle(world, ordered, start, new List<int>(4));
    }

    static void BreakCircle(World world, CharacterId ordered, int slot, List<int> seen)
    {
        var table = world.Characters;
        // Engine guard: the original has none on the job walk; a chain is cut at 64 characters like the one-shot walk was.
        if (seen.Count >= 64 || seen.Contains(slot)) return;
        seen.Add(slot);
        var cold = table.Cold(slot)!;
        // The one-shot follow (a running follow job's FollowOrder is the job's, handled with the jobs).
        var order = cold.FollowOrder;
        bool jobRuns = Production.PermaJobs.ListJob(cold, (CharacterTask)table.Next[slot].Task) is { } running && Production.PermaJobs.Follows(running.Row);
        if (!order.IsNone && !jobRuns)
        {
            if (order == ordered)
            {
                cold.FollowOrder = CharacterId.None;
                if (table.Next[slot].Task == (byte)CharacterTask.Follow) TaskStart.Stand(table, slot);
            }
            else if (table.TryResolveNext(order, out int next)) BreakCircle(world, ordered, next, seen);
        }
        if (!cold.Jobs.Exists(j => j.Type == Meitou.Data.Gameplay.Ai.TaskType.STAY_CLOSE_TO_TARGET)) return;
        for (int k = 0; k < cold.Jobs.Count; k++)
        {
            var job = cold.Jobs[k];
            if (job.Type is not (Meitou.Data.Gameplay.Ai.TaskType.STAY_CLOSE_TO_TARGET or Meitou.Data.Gameplay.Ai.TaskType.BODYGUARD)) continue;
            if (job.Character == ordered)
            {
                Production.ProductionSystem.RemoveJob(world, slot, k);
                k--;
            }
            else if (!job.Character.IsNone && table.TryResolveNext(job.Character, out int next)) BreakCircle(world, ordered, next, seen);
        }
    }

    /// <summary>Drops a character's path and makes it stand (serial phases only); its order queue is the queue's (<see cref="PlayerOrders"/>).</summary>
    public static void Stop(CharacterTable table, int slot)
    {
        ref var n = ref table.Next[slot];
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.Task = (byte)CharacterTask.Idle;
        n.PathCursor = 0;
        var cold = table.Cold(slot)!;
        cold.PathRequest = 0;
        cold.FollowHeading = Vector2.Zero;
    }

    public void Schedule(World world)
    {
        Fights ??= world.Systems.OfType<Combat.CombatSystem>().FirstOrDefault();
        var table = world.Characters;
        var next = table.Next;
        SpeedGroups(world);
        _ = world.Fixtures.DoorPicks;   // the door spots and the doors by building, for the followers of a leader shut in (Think reads them)
        _ = world.Fixtures.DoorsByBuilding;
        for (int i = 0; i < next.Length; i++)
        {
            if (!next[i].Alive || (next[i].Flags & (ushort)MoveFlags.NeedPath) == 0) continue;
            var cold = table.Cold(i)!;
            // The goal's floor: the height the order was given at (a follower's leader on another storey), else the character's own (a wander or follow goal on its level).
            float nearY = cold.GoalHeightAt == next[i].Goal && !float.IsNaN(cold.GoalHeight) ? cold.GoalHeight : next[i].Position.Y;
            var to = new Vector3(next[i].Goal.X, world.Walkability.GroundHeight(next[i].Goal.X, next[i].Goal.Y, nearY), next[i].Goal.Y);
            cold.PathRequest = Paths.Submit(new CharacterId(i, next[i].Generation), next[i].Position, to, cold.FootprintRadius,
                WaterCost.Of(cold.WaterFactor, next[i].MaxSpeed, cold.Race, cold.SwimSpeed), cold.RoadPreference);
            next[i].Flags = (ushort)((next[i].Flags & ~(ushort)MoveFlags.NeedPath) | (ushort)MoveFlags.Pending);
        }
        // The synchronous service has the answers already.
        if (Paths.IsSynchronous) ApplyAnswers(world);
    }

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
                // An older request: the character was given another goal since. Orders given again every tick (a held right button) are each replaced
                // before their path comes, so a found path newer than the one followed is still taken while the latest request is on its way.
                bool waiting = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
                if (!answer.Path.Found || !waiting || n.Task != (byte)CharacterTask.GoTo || answer.Id <= cold.PathApplied) continue;
                cold.Path = [.. answer.Path.Points.Skip(1)];
                cold.PathExact = answer.Path.IsProvisional ? answer.Path.Exact - 1 : int.MaxValue;
                if (answer.Path.Exact > 1) cold.ProvisionalBlocked = 0;
                cold.PathApplied = answer.Id;
                n.PathCursor = 0;
                n.Flags |= (ushort)MoveFlags.HasPath;
                continue;
            }
            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.Pending);
            if (answer.Path.Found)
            {
                cold.PathFailed = false;
                cold.Path = [.. answer.Path.Points.Skip(1)];
                cold.PathExact = answer.Path.IsProvisional ? answer.Path.Exact - 1 : int.MaxValue;
                if (answer.Path.Exact > 1) cold.ProvisionalBlocked = 0;
                n.PathCursor = 0;
                n.Flags |= (ushort)MoveFlags.HasPath;
            }
            else
            {
                // No way there: the old path (an order given on the move) is dropped too.
                cold.PathFailed = true;
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                n.PathCursor = 0;
                n.TaskTime = FailedPathWait;
                if (n.Task == (byte)CharacterTask.GoTo) { TaskStart.Step(table, slot, CharacterTask.Idle); cold.OrderQueue.Clear(); }
            }
        }
    }

    public void Think(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ref var n = ref next[i];
            var task = (CharacterTask)n.Task;
            var flags = (MoveFlags)n.Flags;
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true }) continue;
            bool busy = (flags & MoveFlags.AnyPath) != 0;
            if (task == CharacterTask.Follow)
            {
                // A follow order whose target is gone ends: the character stands where it is.
                if (!cold.FollowOrder.IsNone && !table.IsAlive(cold.FollowOrder))
                {
                    cold.FollowOrder = CharacterId.None;
                    TaskStart.Step(table, i, CharacterTask.Idle);
                    continue;
                }
                var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
                var leader = Followed(squad, cold);
                if (leader.IsNone || leader.Slot == i || !table.IsAlive(leader))
                {
                    TaskStart.Step(table, i, CharacterTask.Wander);
                    continue;
                }
                if (table.Cold(leader.Slot)?.CarriedBy == table.IdOf(i)) continue;   // it carries its leader: the squad follows it, it does not follow itself
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
                    // A squad mate: its slot turned onto its eased heading, one second of the leader's travel ahead of the leader.
                    var heading = cold.FollowHeading;
                    if (heading == Vector2.Zero) heading = leadDir;
                    else if (moving)
                    {
                        heading += (leadDir - heading) * (HeadingEase * dt);
                        heading = heading.LengthSquared() > 1e-8f ? Vector2.Normalize(heading) : leadDir;
                    }
                    cold.FollowHeading = heading;
                    slotPos = leadAt + heading * leadSpeed + Formation.ToWorld(slot, heading);
                    if (ShutOutAim(world, leader.Slot, i, lead.Position, n.Position) is { } outside) (slotPos, shutOut) = (outside + Formation.ToWorld(slot, heading) * DoorSlotScale, true);
                }
                else
                {
                    // Anyone else: its drawn distance from the target, on its own side.
                    var off = here - leadAt;
                    off = off.LengthSquared() > 1e-6f ? Vector2.Normalize(off) : -leadDir;
                    slotPos = leadAt + off * PlainDistance(world.Seed, self, leader);
                }
                // A leader on another storey (the Hub Storm House's roof above it): the follower's own floor under its slot would be "there", so it takes a path to the leader
                // itself, on the leader's floor, and the climb counts in its distance (engine choice; the original's Task_Follow on stacked floors is Unknown).
                float climb = MathF.Abs(lead.Position.Y - n.Position.Y);
                bool otherFloor = !shutOut && climb > FloorSlack && MathF.Abs(world.Walkability.GroundHeight(leadAt.X, leadAt.Y, n.Position.Y) - lead.Position.Y) > FloorSlack;
                if (otherFloor) slotPos = leadAt;
                float away = Vector2.Distance(here, slotPos) + (otherFloor ? climb : 0);
                cold.FollowSpeed = FollowSpeedFor(away, moving, leadDir, new Vector2(MathF.Sin(prev[i].Yaw), MathF.Cos(prev[i].Yaw)));
                n.Mode = lead.Mode;
                // Task_Follow sets the follower's sneaking each update (FUN_14033b350, Verified): on while the leader sneaks out of a fight and the follower counts it an ally (here:
                // the same faction, as squad mates are), off otherwise; dropping it also stands it up. A leader that follows someone itself may change its own flag in this same
                // parallel pass, so its followers are left as they are (engine choice, keeps the step deterministic).
                if (lead.Task != (byte)CharacterTask.Follow && table.Cold(leader.Slot) is { } leaderCold)
                {
                    bool sneak = (leaderCold.Orders & CharacterOrders.Sneak) != 0 && !leaderCold.InCombat && leaderCold.Faction == cold.Faction;
                    if (sneak) cold.Orders |= CharacterOrders.Sneak;
                    else if ((cold.Orders & CharacterOrders.Sneak) != 0)
                    {
                        cold.Orders &= ~CharacterOrders.Sneak;
                        if (cold.Medical is { } med) med.StayingLow = false;
                    }
                }
                n.TaskTime -= dt;
                if (cold.FollowPathTime > 0) cold.FollowPathTime = MathF.Max(cold.FollowPathTime - dt, 0);
                if (away < DirectDistance && !otherFloor && cold.FollowPathTime <= 0)
                {
                    // Near its aim: Move steers it straight there (a path it had is dropped).
                    n.Goal = slotPos;
                    if (busy)
                    {
                        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
                        n.PathCursor = 0;
                        cold.PathRequest = 0;
                    }
                    continue;
                }
                if (away > FollowSlack && (flags & (MoveFlags.NeedPath | MoveFlags.Pending)) == 0 && n.TaskTime <= 0
                    && (!busy || Vector2.DistanceSquared(slotPos, n.Goal) > 20 * 20))
                {
                    n.Goal = slotPos;
                    if (otherFloor) (cold.GoalHeight, cold.GoalHeightAt) = (lead.Position.Y, slotPos);
                    n.Flags |= (ushort)MoveFlags.NeedPath;
                    n.TaskTime = RepathSeconds;
                }
            }
            else if (task == CharacterTask.Wander)
            {
                n.Mode = SpeedMode.Walk;
                if (busy) continue;
                n.TaskTime -= dt;
                if (n.TaskTime > 0) continue;
                var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
                var centre = squad is not null ? squad.HomeCentre : new Vector2(n.Position.X, n.Position.Z);
                float radius = squad is not null ? squad.HomeRadius : 100;
                if (cold.Ai is { WanderRadius: > 0 } mind) (centre, radius) = (mind.WanderCentre, mind.WanderRadius);   // milling about its post (Ai.AiBehaviour.Mill)
                ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Wander, (ulong)world.Tick);
                n.Goal = Rng.PointInDisc(roll, centre, radius);
                n.Flags |= (ushort)MoveFlags.NeedPath;
            }
        }
    }

    /// <summary>Whom a follower keeps its slot by: the target of its <see cref="FollowOrder"/>, a contract's mission target (Ai.AiMind.FollowTarget), else its squad's leader.</summary>
    static CharacterId Followed(Squad? squad, CharacterCold cold) =>
        !cold.FollowOrder.IsNone ? cold.FollowOrder : cold.Ai is { FollowTarget.IsNone: false } follows ? follows.FollowTarget : squad?.Leader ?? CharacterId.None;

    /// <summary>How far a follower that is not its target's squad mate keeps from it: 20..40, drawn per follower and target (the original draws it when the follow task is made).</summary>
    static float PlainDistance(ulong seed, CharacterId self, CharacterId target) =>
        20 + 20 * Rng.Float(Rng.Hash(seed, Rng.Key(self), RngPurpose.Formation, Rng.Key(target)));

    /// <summary>
    /// The original's desired speed of a follower <paramref name="away"/> from its aim (docs/game/pathfinding.md "Squads travelling together"): behind a moving leader the distance
    /// itself, less while it faces away from the leader's heading (down to a tenth); beside a standing one 0 within 20, then 10-20, 20-30 and 30-999 at 50, 200 and 500 (linear in
    /// the distance squared).
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

    readonly record struct SpeedGroupState(float Speed, Vector2 Centre, Vector2 Direction);
    // The player's speed groups of this tick, by key (serial Schedule; read by the parallel Move).
    readonly Dictionary<int, SpeedGroupState> speedGroups = [];

    /// <summary>
    /// <c>SpeedGroup::getSpeed</c> (<c>1407f5740</c>, docs/game/pathfinding.md "Speed groups"), once per tick: a group's speed is the lowest top speed of its members not down and
    /// not carried, its centre their mean position, its direction the sum of their velocities (last tick's).
    /// </summary>
    void SpeedGroups(World world)
    {
        speedGroups.Clear();
        var table = world.Characters;
        var prev = table.Previous;
        var acc = new Dictionary<int, (float Speed, Vector2 Sum, int N, Vector2 Dir)>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!prev[s].Alive || table.Cold(s) is not { SpeedGroup: >= 0, RunSpeed: SpeedMode.Group } c || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone) continue;
            if (!acc.TryGetValue(c.SpeedGroup, out var a)) a = (float.MaxValue, Vector2.Zero, 0, Vector2.Zero);
            acc[c.SpeedGroup] = (MathF.Min(a.Speed, prev[s].MaxSpeed), a.Sum + new Vector2(prev[s].Position.X, prev[s].Position.Z), a.N + 1,
                a.Dir + new Vector2(prev[s].Velocity.X, prev[s].Velocity.Z));
        }
        foreach (var (key, a) in acc) speedGroups[key] = new SpeedGroupState(a.Speed, a.Sum / a.N, a.Dir);
    }

    /// <summary>
    /// The speed cap of a character in <see cref="SpeedMode.Group"/>: in a player's speed group, the group's speed, × max(0.8, 1 - 0.001 × its distance to the centre) for one
    /// ahead of the centre along the group's direction; otherwise (an NPC) the slowest of its squad (<see cref="SlowestOfSquad"/>).
    /// </summary>
    float GroupSpeed(World world, int slot, CharacterCold cold, Vector3 at)
    {
        if (cold.SpeedGroup < 0 || !speedGroups.TryGetValue(cold.SpeedGroup, out var g)) return SlowestOfSquad(world, cold);
        var off = new Vector2(at.X, at.Z) - g.Centre;
        return Vector2.Dot(off, g.Direction) > 0 ? g.Speed * MathF.Max(0.8f, 1 - 0.001f * off.Length()) : g.Speed;
    }

    // The door aim of a follower shut out (docs/game/pathfinding.md "Following"): the door's two points DoorDepth either side of the leaf (door data +0x3bc / +0x3c8),
    // the aim 1.1 of their distance past the outer one, the formation slot shrunk to 0.2.
    const float DoorDepth = 15, DoorPast = 1.1f, DoorSlotScale = 0.2f;

    /// <summary>
    /// Where a squad mate waits while its leader is inside a building it cannot enter (<c>1403335c0</c>; engine reading of "cannot enter": every door of the building locked and
    /// whole): outside the door nearest to it, A + (A − B) × 1.1 with A the door's outer point and B its inner. Null when the leader is not shut in from it.
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
        if (Vector2.Dot(outward, new Vector2(leaderAt.X - s.Centre.X, leaderAt.Z - s.Centre.Z)) > 0) outward = -outward;   // away from the leader inside
        var centre = new Vector2(s.Centre.X, s.Centre.Z);
        Vector2 a = centre + outward * DoorDepth, b = centre - outward * DoorDepth;
        return a + (a - b) * DoorPast;
    }

    /// <summary>
    /// The NPCs' group speed (<see cref="SpeedMode.Group"/>; the original makes speed groups for the player's selection only): the lowest top speed among the squad's members walking a
    /// path in that mode (last tick's, so the order the partitions run in does not matter).
    /// </summary>
    static float SlowestOfSquad(World world, CharacterCold cold)
    {
        var table = world.Characters;
        float slowest = float.MaxValue;
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return slowest;
        foreach (var member in squad.Members)
        {
            if (!table.IsAlive(member)) continue;
            ref readonly var p = ref table.Previous[member.Slot];
            if (p.Mode != SpeedMode.Group || ((MoveFlags)p.Flags & MoveFlags.HasPath) == 0 || table.Cold(member.Slot)?.Medical is { Incapacitated: true }) continue;
            slowest = MathF.Min(slowest, p.MaxSpeed);
        }
        return slowest;
    }

    public void Move(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        var walk = world.Walkability;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ref readonly var p = ref prev[i];
            ref var n = ref next[i];
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true })
            {
                // Lying down (knocked out or dead): no steering, no pushing; the body system has already dropped the orders.
                n.Velocity = Vector3.Zero;
                n.Animation = 0;
                cold.AgentSpeed = 0;
                continue;
            }
            if (n.Task != (byte)CharacterTask.Attack && cold.StaggeredUntil > world.Tick || CharacterFacts.GettingUp(cold, world.Tick)
                || n.Task != (byte)CharacterTask.Attack && cold.Animation is { HoldsStill: true } && ((MoveFlags)n.Flags & MoveFlags.AnyPath) != 0)
            {
                // A stagger outside a fight stops the movement while the stumble clip plays (Character::_startStumble), and getting up after a knockout in or out of
                // one (Task_GetUp), and while a clip with <c>disables movement</c> plays (a bow, a reload; AnimationClass::isCurrentlyIncapableOfWaypointMovemode); the order's path is kept and resumed.
                n.Velocity = Vector3.Zero;
                cold.AgentSpeed = 0;
                continue;
            }
            if (cold.IsImprisoned || cold.InSomething == Locks.HeldState.Bed && n.Task == (byte)CharacterTask.Sleep)
            {
                // Held in its cage or bed (put there by the fixture registry): no path, no steering, not pushed. Its position is left as it is: one laid in this
                // tick's inputs is already at its spot in Next.
                n.Velocity = Vector3.Zero;
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
                cold.AgentSpeed = 0;
                continue;
            }
            // An order move on Hold makes its destination the hold point on every step (Task_Move update FUN_140354600, Verified): a queued move as it starts, and over
            // the position Hold was turned on at mid-walk.
            if (n.Task == (byte)CharacterTask.GoTo && cold.OrderMove && Orders.Holding(cold)) cold.HoldPoint = n.Goal;
            float speed = MathF.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z);
            var dir = Vector2.Zero;
            bool facing = false, agentWalk = false;
            var wanted = Vector2.Zero;
            float desired = 0;
            bool finished = false, closing = false;
            int water = WaterStateAt(walk, p.Position, cold);
            cold.WaterState = (byte)water;
            // A fighter in its footwork: the fight steering's velocity, facing its target (docs/game/combat.md "Fight steering").
            bool steering = Fights is not null && cold.Steer is { Active: true } && n.Task == (byte)CharacterTask.Attack;

            if (steering)
            {
                var v = Combat.CombatSteering.Velocity(world, Fights!, i, cold, cold.Steer!, n.MaxSpeed, cold.Animation?.Strafing ?? false, speed > 0.01f, out var face);
                // In water the steering's caller scales it by the water state (x0.75 / 0.5 / 0.4 in states 1-3, FUN_14065dba0; docs/game/combat.md "Fight steering").
                if (water > 0) v *= water switch { 1 => 0.75f, 2 => 0.5f, _ => 0.4f };
                float vs = v.Length();
                (dir, speed) = vs > 1e-4f ? (v / vs, vs) : (Vector2.Zero, 0f);
                if (face != Vector2.Zero)
                {
                    // The logical heading takes the look-at direction at once (combatMovementUpdate FUN_1402af1e0 writes the unit vector to the target into the movement's
                    // heading +0xd0 every tick, docs/game/combat.md "Fight steering"); only the drawn body turns at a rate (AnimationSystem.FollowFacing).
                    facing = true;
                    n.Yaw = MathF.Atan2(face.X, face.Y);
                }
            }
            else if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0 && n.PathCursor < cold.Path.Length)
            {
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
                    closing = last && cold.ArriveWithin > 0 && dist <= ArriveRadius;
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
                        if (last) finished = true;
                    }
                }
                while (reached && !last);
                if (!reached)
                {
                    dir = to / dist;
                    byte mode = cold.ModeOverride ?? n.Mode;
                    // A carrier in walk mode is raised to run mode (the original's speed selector, Observed; docs/game/carry-loot.md "Speed and weight").
                    if (mode == SpeedMode.Walk && !cold.Carrying.IsNone) mode = SpeedMode.Run;
                    // Its squad template's force speed 0..2 replaces the AI's mode (FUN_140332390 never lowers an explicit task mode, so only a raise is applied; docs/game/pathfinding.md "Force speed").
                    if (!cold.IsPlayer && cold.Ai?.Squad is { ForceSpeed: < Squad.NoForceSpeed } forcing && mode < forcing.ForceSpeed) mode = forcing.ForceSpeed;
                    float top = water > 0 && cold.Race is { } race ? Speed.InWater(water, n.MaxSpeed, race, cold.SwimSpeed) : n.MaxSpeed;
                    desired = cold.SpeedOverride > 0 ? cold.SpeedOverride
                        : n.Task == (byte)CharacterTask.Follow ? MathF.Min(top, cold.FollowSpeed)   // a follower's own desired speed, not its mode's cap
                        : MathF.Min(top, SpeedMode.Cap(mode, n.WalkSpeed));
                    // A fighter in its footwork closes in at a shuffle (docs/game/combat.md "Combat stance").
                    if (cold.InCombat && !cold.CombatRunning && n.Task == (byte)CharacterTask.Attack) desired = MathF.Min(desired, Combat.CombatTuning.FootworkSpeed);
                    if (mode == SpeedMode.Group && cold.SpeedOverride <= 0 && n.Task != (byte)CharacterTask.Follow) desired = MathF.Min(desired, GroupSpeed(world, i, cold, p.Position));
                    if ((cold.Orders & CharacterOrders.Sneak) != 0 && cold.SpeedOverride <= 0) desired = MathF.Min(desired, Orders.SneakSpeed(desired, Orders.Stealth(cold)));
                    if (cold.PaceCap is { } pace && n.Task == (byte)CharacterTask.GoTo && !cold.InCombat) desired = MathF.Min(desired, pace);   // a campaign follower near its trail point
                    if (cold.Medical is { Crawling: true } && cold.CarriedBy.IsNone) desired = Bodies.Speed.Crippled;   // the final override, after water and sneaking
                    // Braking into the last point, never past it.
                    if (last) desired = MathF.Min(desired, MathF.Min(MathF.Sqrt(2 * PathAcceleration(cold) * dist), dist / dt));
                }
                // On the provisional part of a long route: ask again (the zones ahead may have loaded), following this one until the answer comes.
                if (n.PathCursor >= cold.PathExact) Reroute(world, ref n, cold);
            }
            else if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0)
            {
                finished = true;
            }
            else if (n.Task == (byte)CharacterTask.Follow && ((MoveFlags)n.Flags & MoveFlags.AnyPath) == 0 && cold.FollowPathTime <= 0 && cold.FollowSpeed > 0)
            {
                // Near its aim (Think set it as the goal): straight at it at its desired speed, capped by its top speed, never past it.
                var gap = new Vector2(n.Goal.X - p.Position.X, n.Goal.Y - p.Position.Z);
                float dist = gap.Length();
                if (dist > 1e-3f)
                {
                    float top = water > 0 && cold.Race is { } race ? Speed.InWater(water, n.MaxSpeed, race, cold.SwimSpeed) : n.MaxSpeed;
                    dir = gap / dist;
                    desired = MathF.Min(MathF.Min(cold.FollowSpeed, top), dist / dt);
                    if ((cold.Orders & CharacterOrders.Sneak) != 0 && cold.SpeedOverride <= 0) desired = MathF.Min(desired, Orders.SneakSpeed(desired, Orders.Stealth(cold)));
                    if (cold.Medical is { Crawling: true }) desired = MathF.Min(desired, Bodies.Speed.Crippled);
                }
            }

            if (!steering && n.Task == (byte)CharacterTask.Attack && Fights is not null && Fights.SwingAim(world, i, out var swingAim)) n.Yaw = MathF.Atan2(swingAim.X, swingAim.Y);   // a swing turns at once to its target (FUN_140666400, FUN_14060a590)
            if (steering) { }   // its velocity is the steering's
            else if (finished)
            {
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                n.PathCursor = 0;
                speed = 0;
                cold.AgentSpeed = 0;
                // The end of an old path while the new one is on its way (an order given on the move) leaves the task to the new path.
                bool replaced = ((MoveFlags)n.Flags & (MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
                if (!replaced && n.Task == (byte)CharacterTask.GoTo)
                {
                    // The move order leaves the queue; a queued move after it (shift + right click) starts: a path to it is asked for next tick.
                    if (!PlayerOrders.Arrived(cold, ref n)) TaskStart.Step(table, i, CharacterTask.Idle);
                }
                else if (!replaced && n.Task == (byte)CharacterTask.Attack) n.TaskTime = PursuitSystem.RepathSeconds;   // a fighter at the end of its path to a moving target asks for the next one soon (PursuitSystem)
                else if (!replaced)
                {
                    ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Think, (ulong)world.Tick);
                    n.TaskTime = WaitMin + Rng.Float(roll) * (WaitMax - WaitMin);
                }
            }
            else
            {
                // A path is followed by the nav agent, whose speed moves toward the wanted one at its race's acceleration (PathAcceleration); a fighter goes at its
                // steering speed, which ramps by 15 / s up to its top speed and is kept between ticks (CharacterCold.SteerSpeed), not restarted when a path ends.
                if (cold.InCombat)
                {
                    cold.SteerSpeed = MathF.Min(cold.SteerSpeed + Acceleration * dt, MathF.Max(n.MaxSpeed, desired));
                    speed = cold.AgentSpeed = MathF.Min(desired, cold.SteerSpeed);
                }
                else if (n.Task == (byte)CharacterTask.Follow && ((MoveFlags)n.Flags & MoveFlags.AnyPath) == 0) speed = cold.AgentSpeed = desired;   // a follower near its slot steers straight (mode 2)
                else
                {
                    // The agent's velocity vector moves toward the wanted one (path direction x wanted speed) by at most acceleration x dt (FUN_140147ca0), so a
                    // turn costs the speed the change of direction takes; the facing is the path direction, not the velocity's.
                    var velocity = new Vector2(p.Velocity.X, p.Velocity.Z);
                    var gap = dir * desired - velocity;
                    float change = gap.Length(), most = PathAcceleration(cold) * dt;
                    velocity = change <= most ? dir * desired : velocity + gap * (most / change);
                    speed = cold.AgentSpeed = velocity.Length();
                    wanted = dir;
                    agentWalk = true;
                    dir = speed > 1e-4f ? velocity / speed : Vector2.Zero;
                }
                if (agentWalk)
                {
                    // The facing is the way the agent moves (its velocity), at once: the body faces its motion through a turn instead of sliding sideways along the arc;
                    // the drawn body follows the facing at its own rate (AnimationSystem.FollowFacing). Standing up to speed, it faces the path direction.
                    var along = dir != Vector2.Zero ? dir : wanted;
                    if (along != Vector2.Zero)
                    {
                        facing = true;
                        n.Yaw = MathF.Atan2(along.X, along.Y);
                    }
                }
                else if (dir != Vector2.Zero && FaceOf(prev, n, cold) is { } face)
                {
                    // In its footwork a fighter walks its path whichever way it lies and turns to face its target (the side steps, docs/animation.md "Side steps").
                    facing = true;
                    n.Yaw = MathF.Atan2(face.X, face.Y);
                }
                else if (dir != Vector2.Zero)
                {
                    (dir, speed) = Turn(p.Yaw, dir, speed, dt);
                    n.Yaw = MathF.Atan2(dir.X, dir.Y);   // also while it turns on the spot
                }
            }

            // Separation from the neighbours of the last tick, visited in the grid's fixed order.
            var push = new SeparationState { Table = table, Self = i };
            world.Grid.Query(p.Position.X, p.Position.Z, SeparationRadius, ref push, static (int other, ref SeparationState s) =>
                {
                    if (other == s.Self || s.Count >= MaxSeparationContributions) return;
                    var all = s.Table.Previous;
                    var d = new Vector2(all[s.Self].Position.X - all[other].Position.X, all[s.Self].Position.Z - all[other].Position.Z);
                    float len = d.Length();
                    if (len >= SeparationRadius) return;
                    // A carried body is on its carrier's shoulder and a downed or dead one lies out of the flocking list: neither is in anyone's way (docs/game/pathfinding.md "Bodies in the way").
                    if (s.Table.Cold(other) is { } them && (!them.CarriedBy.IsNone || them.Medical is { Incapacitated: true })) return;
                    s.Near[s.Count++] = other;
                    // Exactly on top of each other: the lower slot is pushed one way, the other the opposite.
                    s.Sum += (len > 1e-4f ? d / len : (s.Self < other ? Vector2.UnitX : -Vector2.UnitX)) * (100 * (1 - len / SeparationRadius));
                });

            // Closing on its spot it is not held off by those standing round it (they are pushed aside as it arrives); a sitter is not moved at all.
            bool seated = Seated(n, cold);
            // A steering fighter keeps it too: its repulsion is too weak to keep bodies apart in a crowd, and without it Contact shoves back out what the steering walks in.
            if (closing || seated) push.Sum = Vector2.Zero;
            var drive = closing || seated ? dir * speed : Blocked(world, cold, ref push, new Vector2(p.Position.X, p.Position.Z), dir * speed);
            var step = (drive + push.Sum * SeparationStrength) * dt;
            var shove = closing || seated ? Vector2.Zero : Contact(world, i, cold, ref push, new Vector2(p.Position.X, p.Position.Z) + step);
            step += shove;
            float x = p.Position.X + step.X, z = p.Position.Z + step.Y, y = 0;
            bool stands = false;
            // Pushed by its neighbours where it cannot stand (over a hole furniture cuts in a floor): its own step alone.
            if ((push.Sum != Vector2.Zero || shove != Vector2.Zero) && !(stands = walk.TryStand(x, z, p.Position.Y, out y)))
            {
                step = dir * speed * dt;
                (x, z) = (p.Position.X + step.X, p.Position.Z + step.Y);
            }
            if (step != Vector2.Zero)
            {
                // One already off every surface near its height (a stale height) is not held there: it steps down onto what is below.
                if (!stands) stands = walk.TryStand(x, z, p.Position.Y, out y);
                if (stands || !walk.IsWalkable(p.Position.X, p.Position.Z, p.Position.Y) && walk.IsWalkable(x, z))
                {
                    n.Position = new Vector3(x, y, z);
                    if (speed > 0.1f && !facing) n.Yaw = MathF.Atan2(dir.X, dir.Y);
                }
                else if (steering) speed = 0;   // into a wall or off the ground: it stands this tick
                else
                {
                    // Blocked on the provisional part of a long route (the terrain-only route met a wall or a building): stand and ask again.
                    // Blocked on its navmesh part: ask again from where it stands a few times before giving up.
                    if (n.PathCursor >= cold.PathExact ? ++cold.ProvisionalBlocked <= ProvisionalBlockedLimit : ++cold.BlockedRepaths <= BlockedRepathLimit)
                    {
                        speed = 0;
                        dir = Vector2.Zero;
                        Reroute(world, ref n, cold, now: true);
                        goto moved;
                    }
                    // Blocked (no surface to walk on there): drop the path and wait before trying again; a follower steering at its slot takes paths for a while.
                    speed = 0;
                    if (n.Task == (byte)CharacterTask.Follow) cold.FollowPathTime = FollowPathSeconds;
                    n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                    n.TaskTime = FailedPathWait;
                    if (n.Task == (byte)CharacterTask.GoTo) { TaskStart.Step(table, i, CharacterTask.Idle); cold.OrderQueue.Clear(); }
                }
            }
        moved:
            n.Velocity = new Vector3(dir.X * speed, 0, dir.Y * speed);
            n.Animation = (ushort)(speed < 1 ? 0 : speed <= n.WalkSpeed + 1 ? 1 : 2);
            n.AnimationTime = n.Animation == p.Animation ? p.AnimationTime + dt : 0;
        }
    }

    /// <summary>
    /// The water state of a character (<see cref="Speed.WaterState"/>, a race's own characters only, not a carried body): the original reads the height it stands at, which in water is the
    /// seabed, while here a character on the water keeps the navmesh's flat water surface for its height, so the depth is taken from the terrain below while it is at the surface
    /// (<see cref="Speed.WaterSlack"/> over the water level; a deck or floor higher up counts as dry). The race's size scale is taken as 1 (what humans have, <b>Observed</b>).
    /// </summary>
    static int WaterStateAt(IWalkability walk, Vector3 at, CharacterCold cold)
    {
        if (cold.Race is not { } race || !cold.CarriedBy.IsNone) return 0;
        float standY = at.Y <= WorldWater.Height + Speed.WaterSlack ? MathF.Min(at.Y, walk.TerrainHeight(at.X, at.Z)) : at.Y;
        return Speed.WaterState(standY, 1, race.Swims);
    }

    /// <summary>Asks for the path to the goal again (partition-safe: the character's own state), at most every <see cref="ProvisionalRepathSeconds"/> unless <paramref name="now"/>.</summary>
    static void Reroute(World world, ref CharacterHot n, CharacterCold cold, bool now = false)
    {
        if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0 || !now && world.Tick < cold.ProvisionalRepathTick) return;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        cold.ProvisionalRepathTick = world.Tick + (long)MathF.Ceiling(ProvisionalRepathSeconds / world.TickSeconds);
    }

    /// <summary>Spacing (units) of the points a path is tested at for the zones it crosses (<see cref="Crosses"/>; engine choice, well under a zone).</summary>
    const float ZoneProbeStep = 100;

    /// <summary>Whether the way from <paramref name="at"/> along <paramref name="path"/> from <paramref name="cursor"/> on enters one of <paramref name="zones"/>.</summary>
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

    /// <summary>The solid radius of a body: <see cref="BodyShare"/> of its footprint (a human's when it has no race data).</summary>
    static float BodyRadius(CharacterCold cold) => BodyShare * (cold.FootprintRadius > 0 ? cold.FootprintRadius : Combat.CombatTuning.DefaultFootprint);

    /// <summary>
    /// How far a character stepped to <paramref name="at"/> is put out of the standing bodies among its separation neighbours (partition-safe: the last tick's state of the
    /// others): half of each overlap with the other's last position, all of it against one held in a bed or cage, averaged over the bodies it overlaps (a sum overshoots in a
    /// packed crowd and the whole crowd shakes). Lying, sitting and carried bodies are not in the way (a walker only steers past a sitter, so a ring of sitters cannot wall it in),
    /// and a carried or bedded one is not moved. Not where the other's velocity takes it: in a jam that is where it wants to go, not where it gets to.
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
    /// The velocity a character steps by without the part of it into a standing body it already touches (engine choice), so the ranks behind a blocked one do not press it
    /// into those in front and a crowd does not squash; what is left slides it round them. The bodies counted are <see cref="Contact"/>'s.
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

    /// <summary>Sitting (resting, on a chair or the floor, or on a seat it uses) with no path to follow: the original does not push sitters (Observed in play).</summary>
    /// <summary>Whether another standing character's footprint covers <paramref name="goal"/> (last tick's positions).</summary>
    static bool GoalOccupied(World world, int self, Vector3 goal)
    {
        var state = new OccupiedState { Table = world.Characters, Self = self, Goal = new Vector2(goal.X, goal.Z) };
        world.Grid.Query(goal.X, goal.Z, 30, ref state, static (int other, ref OccupiedState s) =>
        {
            if (s.Found || other == s.Self || !s.Table.Previous[other].Alive || s.Table.Cold(other) is not { CarriedBy.IsNone: true } c || c.Medical is { Incapacitated: true }) return;
            var at = s.Table.Previous[other].Position;
            float r = Combat.CombatTuning.Footprint(c);
            if (Vector2.DistanceSquared(new Vector2(at.X, at.Z), s.Goal) < r * r) s.Found = true;
        });
        return state.Found;
    }

    struct OccupiedState
    {
        public CharacterTable Table;
        public int Self;
        public Vector2 Goal;
        public bool Found;
    }

    static bool Seated(in CharacterHot h, CharacterCold c) => ((MoveFlags)h.Flags & MoveFlags.HasPath) == 0
        && (h.Task == (byte)CharacterTask.Rest || h.Task == (byte)CharacterTask.Operate && c.Running is { Implicit: true, Row: Meitou.Data.Gameplay.Ai.TaskType.SIT_AROUND } run && run.Runs(CharacterTask.Operate));

    /// <summary>
    /// The heading turned from <paramref name="yaw"/> towards <paramref name="wanted"/> by at most <see cref="TurnRate"/> x <paramref name="dt"/>,
    /// and the speed scaled by the cosine of the turn still to make (zero beyond 90 degrees: the character turns where it stands).
    /// </summary>
    /// <summary>The way to the target a fighter in its footwork faces (<see cref="CharacterCold.FaceSlot"/>, the last tick's place), or null.</summary>
    static Vector2? FaceOf(ReadOnlySpan<CharacterHot> prev, in CharacterHot n, CharacterCold cold)
    {
        int t = cold.FaceSlot;
        if (t < 0 || t >= prev.Length || !prev[t].Alive || n.Task != (byte)CharacterTask.Attack || !cold.InCombat || cold.CombatRunning) return null;
        var d = new Vector2(prev[t].Position.X - n.Position.X, prev[t].Position.Z - n.Position.Z);
        return d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : null;
    }

    public static (Vector2 Dir, float Speed) Turn(float yaw, Vector2 wanted, float speed, float dt, float rate = TurnRate)
    {
        float target = MathF.Atan2(wanted.X, wanted.Y);
        float delta = MathF.IEEERemainder(target - yaw, MathF.Tau);
        float max = rate * dt;
        float heading = MathF.Abs(delta) <= max ? target : yaw + MathF.CopySign(max, delta);
        float left = MathF.IEEERemainder(target - heading, MathF.Tau);
        return (new Vector2(MathF.Sin(heading), MathF.Cos(heading)), speed * MathF.Max(0, MathF.Cos(left)));
    }

    struct SeparationState
    {
        public CharacterTable Table;
        public int Self;
        public int Count;
        public Vector2 Sum;
        /// <summary>The neighbours counted, for the body contact.</summary>
        public Neighbours Near;
    }

    [InlineArray(MaxSeparationContributions)]
    struct Neighbours
    {
        int first;
    }
}
