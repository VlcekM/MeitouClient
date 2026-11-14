using System.Numerics;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Gates (docs/game/locks.md "Gates"): the gate codes (<see cref="IGateRegions"/>) and the states between them, which
/// DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS reads, and the NPC attackers that bash a locked gate between them and their target
/// (<see cref="GatePlan"/>). Without gate codes (headless, before the first labelling) nothing is blocked.
/// </summary>
public sealed partial class LockSystem
{
    /// <summary>
    /// The gate codes now (null: none yet, every destination counts as reachable).
    /// </summary>
    public IGateRegions? Regions { get; private set; }

    /// <summary>
    /// The gate states of the codes' pairs, rebuilt once a check (<see cref="BashCheckTicks"/>) from the gates' doors; null when not yet asked this
    /// check.
    /// </summary>
    Dictionary<int, List<(int To, int State, GateLink Gate)>>? _gateGraph;

    /// <summary>
    /// Takes the host's gate regions; the gate graph is built again.
    /// </summary>
    void GateInputs(GateRegionsCommand command)
    {
        Regions = command.Regions;
        _gateGraph = null;
    }

    /// <summary>
    /// A gate's state as the gate-code pass records it (docs/game/pathfinding.md "Gate codes", <b>Observed</b>): 1 when a door is broken, open or
    /// opening, 2 when one is closed but not locked, 3 when all are locked. A gate without door fixtures (a town not spawned) counts as open (engine
    /// choice).
    /// </summary>
    public static int GateState(FixtureRegistry fixtures, string building)
    {
        int state = 0;
        foreach (var f in fixtures.OfBuilding(building))
        {
            if (f.Kind != FixtureKind.Door) continue;
            int s = f.DoorBroken || f.Motion is DoorMotion.Open or DoorMotion.Opening ? 1 : f.Lock is { EffectivelyLocked: true } ? 3 : 2;
            state = state == 0 ? s : Math.Min(state, s);
        }

        return state == 0 ? 1 : state;
    }

    /// <summary>
    /// The lock the gate graph is built under.
    /// </summary>
    readonly object _gateGraphLock = new();

    /// <summary>
    /// The gate graph: each gate region's links to the others, with the state of the gates between. Built on first use; the combat step asks from its
    /// workers (CombatSystem.ImmediateThreat), so the graph is built under a lock and published whole.
    /// </summary>
    Dictionary<int, List<(int To, int State, GateLink Gate)>> GateGraph(World world)
    {
        if (Volatile.Read(ref _gateGraph) is { } built) return built;
        lock (_gateGraphLock)
        {
            if (_gateGraph is { } raced) return raced;
            var graph = new Dictionary<int, List<(int To, int State, GateLink Gate)>>();
            if (Regions is not null)
            {
                foreach (var gate in Regions.Gates)
                {
                    if (gate.Codes.Length < 2) continue;
                    int state = GateState(world.Fixtures, gate.Building);
                    for (int i = 0; i < gate.Codes.Length; i++)
                    {
                        for (int j = 0; j < gate.Codes.Length; j++)
                        {
                            if (i == j) continue;
                            if (!graph.TryGetValue(gate.Codes[i], out var list)) graph[gate.Codes[i]] = list = [];
                            list.Add((gate.Codes[j], state, gate));
                        }
                    }
                }
            }

            Volatile.Write(ref _gateGraph, graph);
            return graph;
        }
    }

    /// <summary>
    /// The state between two codes (<c>FUN_1402eb070</c>): 0 the same code, -1 when either is unknown, else the route through the gates whose worst
    /// gate is best (<b>Observed</b>; several gates between one pair keep the lowest state, <b>Verified</b> <c>FUN_1402eebb0</c>). Two codes no gates
    /// join count as locked (3; what the original answers is <b>Unknown</b>). <paramref name="first"/> is the gate the route leaves
    /// <paramref name="from"/> by, nearest <paramref name="near"/> among the equally good ones.
    /// </summary>
    public int RouteState(World world, int from, int to, Vector2 near, out GateLink? first)
    {
        first = null;
        if (from == to) return 0;
        if (from < 0 || to < 0) return -1;
        var graph = GateGraph(world);

        // Widest-path search: a code's value is the worst gate on the best route to it; ties keep the first gate nearer the walker.
        var best = new Dictionary<int, (int State, GateLink Gate)>();
        var open = new PriorityQueue<int, int>();
        if (!graph.TryGetValue(from, out var start)) return 3;
        foreach (var (next, state, gate) in start)
        {
            if (best.TryGetValue(next, out var known) && (known.State < state || known.State == state && Nearer(world, known.Gate, gate, near)))
                continue;

            best[next] = (state, gate);
            open.Enqueue(next, state);
        }

        var done = new HashSet<int> { from };

        // States only grow along a route, so a code's first dequeue is its best.
        while (open.TryDequeue(out int code, out _))
        {
            if (!done.Add(code)) continue;
            var (worst, via) = best[code];
            if (code == to)
            {
                first = via;
                return worst;
            }

            if (!graph.TryGetValue(code, out var edges)) continue;
            foreach (var (next, state, _) in edges)
            {
                int s = Math.Max(worst, state);
                if (done.Contains(next) || best.TryGetValue(next, out var known) && known.State <= s) continue;
                best[next] = (s, via);
                open.Enqueue(next, s);
            }
        }

        return 3;
    }

    /// <summary>
    /// True when <paramref name="known"/>'s door is nearer <paramref name="near"/> than <paramref name="other"/>'s (or the other has none).
    /// </summary>
    static bool Nearer(World world, GateLink known, GateLink other, Vector2 near)
            => GateDistance(world, known, near) <= GateDistance(world, other, near);

    /// <summary>
    /// The distance from <paramref name="near"/> to the gate's main door leaf (infinity without one).
    /// </summary>
    static float GateDistance(World world, GateLink gate, Vector2 near) =>
        MainGateDoor(world, gate.Building) is { } d ? Vector2.Distance(near, Flat(d.Spot.Centre)) : float.MaxValue;

    /// <summary>
    /// DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (90; <c>FUN_140597160</c>, <b>Verified</b>): the walker's code is the destination's, or either is
    /// unknown, or the route between them is open (state 1). Engine reading: the codes are those of the positions (the original keeps each object's
    /// on the object, updated by the pass).
    /// </summary>
    public bool NotBlocked(World world, Vector3 from, Vector3 to)
    {
        if (Regions is null) return true;
        int a = Regions.CodeAt(from), b = Regions.CodeAt(to);
        return a == b || a < 0 || b < 0 || RouteState(world, a, b, Flat(from), out _) == 1;
    }

    /// <summary>
    /// Whether a character at <paramref name="at"/> is shut in: it stands inside town walls (gate code above 0) and the state from its code to the
    /// open world (code 0) is locked (<c>FUN_1402eb070</c> = 3, <b>Verified</b>: <c>FUN_1405a34e0</c> and <c>FUN_1405a3710</c> give no desire then;
    /// the stand-in for the location test <c>FUN_1405a2710</c>'s gate half).
    /// </summary>
    public bool WallsLocked(World world, Vector3 at)
    {
        if (Regions is null) return false;
        int code = Regions.CodeAt(at);
        return code > 0 && RouteState(world, code, 0, Flat(at), out _) == 3;
    }

    /// <summary>
    /// Whether the town's walls are locked up (<c>Town</c> vfunc <c>0x300</c> = <c>FUN_140929820</c>, <b>Verified</b>): the town has at least one
    /// gate (a gateway building with a door) and no gate door is open (door state 1; a broken one counts as open); closed, opening and closing doors
    /// all count as locked.
    /// </summary>
    public static bool TownLockedUp(World world, TownSite site) =>
        GatesLockedUp(site.Buildings.Where(b => !b.Destroyed).SelectMany(b => world.Fixtures.OfBuilding(b.Key)));

    /// <summary>
    /// The rule of <see cref="TownLockedUp"/> over a town's door fixtures: shared with the campaign assault's breach test.
    /// </summary>
    public static bool GatesLockedUp(IEnumerable<Fixture> doors)
    {
        bool any = false;
        foreach (var gate in doors)
        {
            if (gate.Kind != FixtureKind.Door || !gate.IsGate) continue;
            if (gate.DoorBroken || gate.Motion == DoorMotion.Open) return false;
            any = true;
        }

        return any;
    }

    /// <summary>
    /// A gate's main door (lowest door index with a known leaf) and its leaf.
    /// </summary>
    internal static (Fixture Door, DoorSpot Spot)? MainGateDoor(World world, string building)
    {
        (Fixture, DoorSpot)? main = null;
        foreach (var f in world.Fixtures.OfBuilding(building))
        {
            if (f.Kind == FixtureKind.Door && world.Fixtures.DoorSpotOf(f) is { } spot && (main is null || f.DoorIndex < main.Value.Item1.DoorIndex))
                main = (f, spot);
        }

        return main;
    }

    /// <summary>
    /// Gives the free NPC attackers whose target is in another region behind a route that is not open the first gate on that route to bash, when the
    /// planner says so (<see cref="GatePlan"/>), or to unlock when it is their own faction's (<see cref="UnlocksOwn"/>); false when it gave none
    /// (the house doors are looked at then).
    /// </summary>
    bool FindGateBasher(World world, int s, int t, CharacterCold c)
    {
        if (Regions is null) return false;
        var next = world.Characters.Next;
        int a = Regions.CodeAt(next[s].Position), b = Regions.CodeAt(next[t].Position);
        if (a == b || a < 0 || b < 0) return false;
        if (RouteState(world, a, b, Flat(next[s].Position), out var gate) == 1) return false;

        // No gate leads there: 270 falls back to a wall of the attacker's region (FUN_1405a3f60), planned through 272 Smashing.
        if (gate is null) return FindWallBasher(world, s, t, c, a);
        if (gate is not { } g || MainGateDoor(world, g.Building) is not { } main) return false;

        // Its own faction's locked gate is unlocked at the leaf, not bashed (see UnlocksOwn).
        if (!UnlocksOwn(main.Door, c) && !_gatePlan.PlansBash(world, Combat, this, s, t, g.Building, main.Door, main.Spot)) return false;
        c.Bashing = main.Door.Id;
        c.BashTime = 0;
        return true;
    }

    /// <summary>
    /// The planner's view of an attacker whose target is behind a gate, reused across plans.
    /// </summary>
    readonly GatePlan _gatePlan = new();

    /// <summary>
    /// How a ransacker (goal 216) at <paramref name="from"/> gets past the gates between it and a building at <paramref name="to"/>, by the planner
    /// (<see cref="GatePlan.PlansEntry"/>): a walk when no gate is closed on the route (or the codes are unknown), the gate's door to bash (row 123 /
    /// 290), or none when the plan finds no way (a locked gate it does not want to bash, or no gate on the route).
    /// </summary>
    internal (DoorPlan.Entry Entry, string? Door) GateEntry(World world, int s, Vector3 from, Vector3 to)
    {
        if (Regions is null) return (DoorPlan.Entry.Walk, null);
        int a = Regions.CodeAt(from), b = Regions.CodeAt(to);
        if (a == b || a < 0 || b < 0 || RouteState(world, a, b, Flat(from), out var gate) == 1)
            return (DoorPlan.Entry.Walk, null);

        if (gate is not { } g || MainGateDoor(world, g.Building) is not { } main) return (DoorPlan.Entry.Blocked, null);
        var entry = _gatePlan.PlansEntry(world, Combat, this, s, to, g.Building, main.Door, main.Spot);
        return (entry, entry == DoorPlan.Entry.Bash ? main.Door.Id : null);
    }

    /// <summary>
    /// How many characters bash the gate whose door is <paramref name="doorId"/> now (crowdLimit8).
    /// </summary>
    internal static int BashersAt(World world, string doorId)
    {
        int n = 0;
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { Bashing: { } id } && id == doorId) n++;
        return n;
    }

    /// <summary>
    /// The spot <see cref="BashStandOff"/> out from a gate's leaf on the walker's side (a gate has no inside to be away from).
    /// </summary>
    static Vector2 GateStandSpot(DoorSpot spot, Vector2 walker)
    {
        var n = new Vector2(spot.Normal.X, spot.Normal.Z);
        if (n.LengthSquared() < 1e-6f) n = Vector2.UnitY;
        n = Vector2.Normalize(n);
        var centre = Flat(spot.Centre);
        if (Vector2.Dot(walker - centre, n) < 0) n = -n;
        return centre + n * BashStandOff;
    }
}