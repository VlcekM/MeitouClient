using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The act-out of order 165 (DA_THUG_HUNTER, <c>Task_TownStalker</c>, <see cref="DialogueOrders"/>): the town stalkers and their victims (hashed),
/// each record dropped once its stalker no longer runs the order.
/// </summary>
sealed class TownStalkers(DialogueSystem system, PopulationData data)
{
    /// <summary>
    /// EV_HARASSMENT, what a stalker opens on its victim outside town.
    /// </summary>
    const int Harassment = 26;

    /// <summary>
    /// How far past the gate a stalker waits (<c>Task_TownStalker</c>, <b>Verified</b> constant).
    /// </summary>
    const float StalkPastGate = 300;

    /// <summary>
    /// A town stalker's state (<c>Task_TownStalker</c>, 165): its victim, the waypoint past the town gate once picked, and whether it has harassed /
    /// found no gate.
    /// </summary>
    sealed class Stalk(CharacterId target)
    {
        /// <summary>
        /// The victim.
        /// </summary>
        public CharacterId Target { get; } = target;

        /// <summary>
        /// The point past the town gate it waits at, once picked.
        /// </summary>
        public Vector2? Waypoint { get; set; }

        /// <summary>
        /// It has sent the victim event 26.
        /// </summary>
        public bool Harassed { get; set; }

        /// <summary>
        /// Its town has no gate: in town it follows without harassing.
        /// </summary>
        public bool NoGate { get; set; }
    }

    /// <summary>
    /// Each stalker's state, by stalker.
    /// </summary>
    readonly SortedDictionary<CharacterId, Stalk> _stalkers = new(Comparer<CharacterId>.Create(DialogueSystem.CompareIds));

    /// <summary>
    /// How many stalk.
    /// </summary>
    public int Count => _stalkers.Count;

    /// <summary>
    /// <paramref name="who"/>'s order 165 starts on <paramref name="target"/> (<see cref="Step(World)"/>).
    /// </summary>
    public void Add(CharacterId who, CharacterId target) => _stalkers[who] = new Stalk(target);

    /// <summary>
    /// <paramref name="who"/>'s record goes (its task ended).
    /// </summary>
    public void Drop(CharacterId who) => _stalkers.Remove(who);

    /// <summary>
    /// DA_THUG_HUNTER (17, order 165 <c>Task_TownStalker</c>, docs/game/dialogue.md): while the stalker has a status town, it waits
    /// <see cref="StalkPastGate"/> outside that town's first gate (on the line from the town centre through it), facing its victim, while the victim
    /// has a status town and is no farther from that town's centre than the waypoint; in a town with no gate it follows the victim without
    /// harassing it. Otherwise it follows the victim and, once, when it is not talking, sends it event 26. It is done when, after that, neither is
    /// talking. Engine choices: re-aimed every 15 ticks; the first gate is the town's first outer gate door in fixture order (a player town's: the
    /// first within its radius).
    /// </summary>
    public void Step(World world)
    {
        if (_stalkers.Count == 0) return;
        foreach (var (who, stalk) in _stalkers.ToArray()) Step(world, who, stalk);
    }

    /// <summary>
    /// One stalker's tick of <see cref="Step(World)"/>: dropped once it no longer runs the order; done when the victim is gone or down or, after the
    /// harassment, neither talks; in its status town it waits past the gate or, with no gate, follows; else it follows and harasses.
    /// </summary>
    void Step(World world, CharacterId who, Stalk stalk)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int s) || !DialogueOrders.Runs(table, s, TaskType.TownStalker, stalk.Target))
        {
            _stalkers.Remove(who);
            return;
        }

        // a walk that found no way there ends the task, as a walk step's failure ends its plan
        bool over = !table.TryResolveNext(stalk.Target, out int t) || !DialogueSystem.Awake(table, t) || table.Cold(s)!.PathFailed;
        if (over || Finished(who, stalk))
        {
            _stalkers.Remove(who);
            TaskStart.End(world, s, TaskEnd.Done);
            return;
        }

        var town = table.Cold(s)!.StatusTown;
        if (!town.IsNone && InTown(world, s, t, stalk, town)) return;

        Harass(world, who, stalk);
        Follow(world, s, t);
    }

    /// <summary>
    /// Whether the stalker <paramref name="who"/> has harassed its victim and neither of them talks any more.
    /// </summary>
    bool Finished(CharacterId who, Stalk stalk) => stalk.Harassed && !system.Talking(who) && !system.Talking(stalk.Target);

    /// <summary>
    /// The stalker <paramref name="s"/> in its status town <paramref name="town"/> (<c>FUN_140340360</c>): picks the waypoint past the town's gate
    /// once; with no gate it follows the victim <paramref name="t"/>; it waits at the waypoint while the victim stands within it. False when the
    /// victim has no status town or is farther from its town's centre than the waypoint: then it follows and harasses.
    /// </summary>
    bool InTown(World world, int s, int t, Stalk stalk, CampaignTown town)
    {
        if (stalk.Waypoint is null && !stalk.NoGate) PickWaypoint(world, stalk, town);
        if (stalk.NoGate)
        {
            Follow(world, s, t);
            return true;
        }

        if (!VictimWithin(world, t, stalk.Waypoint!.Value)) return false;

        WaitAt(world, s, t, stalk.Waypoint.Value);
        return true;
    }

    /// <summary>
    /// The point <see cref="StalkPastGate"/> outside the first gate of <paramref name="town"/>, on the line from its centre; or no gate.
    /// </summary>
    void PickWaypoint(World world, Stalk stalk, CampaignTown town)
    {
        if (AiSystem.TownCircle(data, world, town) is not { } circle || FirstGate(world, town, circle) is not { } g)
        {
            stalk.NoGate = true;
            return;
        }

        var outward = g - circle.Centre;
        stalk.Waypoint = g + (outward.LengthSquared() > 1e-6f ? Vector2.Normalize(outward) : Vector2.UnitX) * StalkPastGate;
    }

    /// <summary>
    /// Where the first gate door of <paramref name="town"/> stands (X/Z): a town site's registered gate, a player town's first within its radius,
    /// skipping an inner gate (<c>FUN_140340360</c>: the gate code outside it above 0, so its outside is still within walls). That code is the gate
    /// building's own from the last labelling (<see cref="Locks.LockSystem.GateCodes"/>: read at the outer door marker); before one it is -1 and no
    /// gate is skipped.
    /// </summary>
    static Vector2? FirstGate(World world, CampaignTown town, (Vector2 Centre, float Radius) circle)
    {
        var locks = world.SystemOf<Locks.LockSystem>();
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != Locks.FixtureKind.Door || !f.IsGate) continue;

            var at = new Vector2(f.Position.X, f.Position.Z);
            bool ofTown = town.Site >= 0 ? f.Site == town.Site : Vector2.Distance(at, circle.Centre) <= circle.Radius;
            if (!ofTown) continue;
            if (locks is not null && locks.GateCodes(f.Building, f).Building > 0) continue;   // an inner gate

            return at;
        }

        return null;
    }

    /// <summary>
    /// Whether the victim <paramref name="t"/> has a status town and stands no farther from its centre than <paramref name="waypoint"/>.
    /// </summary>
    bool VictimWithin(World world, int t, Vector2 waypoint)
    {
        var table = world.Characters;
        if (AiSystem.TownCircle(data, world, table.Cold(t)!.StatusTown) is not { } circle) return false;

        var victim = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        return Vector2.Distance(victim, circle.Centre) <= Vector2.Distance(waypoint, circle.Centre);
    }

    /// <summary>
    /// The stalker <paramref name="s"/> walks to <paramref name="waypoint"/> and, standing there, faces its victim <paramref name="t"/>.
    /// </summary>
    static void WaitAt(World world, int s, int t, Vector2 waypoint)
    {
        var table = world.Characters;
        ref var sn = ref table.Next[s];
        bool away = Vector2.Distance(new Vector2(sn.Position.X, sn.Position.Z), waypoint) > DialogueSystem.TalkReach;
        if (world.Tick % 15 == 0 && away)
        {
            DialogueOrders.WalkTo(table, s, waypoint, SpeedMode.Walk);
            return;
        }
        if ((sn.Flags & (ushort)MoveFlags.AnyPath) != 0) return;

        DialogueOrders.Face(table, s, table.Next[t].Position);
    }

    /// <summary>
    /// Once, when the stalker <paramref name="who"/> is not talking, it sends its victim event 26 (<c>FUN_14033bd60</c>).
    /// </summary>
    void Harass(World world, CharacterId who, Stalk stalk)
    {
        if (stalk.Harassed || system.Talking(who)) return;

        world.Defer(new DialogueEvent(who, stalk.Target, Harassment) { Tick = world.Tick + 1 });
        stalk.Harassed = true;
    }

    /// <summary>
    /// The stalker <paramref name="s"/> walks after its victim <paramref name="t"/> while out of talk reach (<c>FUN_14033b350</c>).
    /// </summary>
    static void Follow(World world, int s, int t)
    {
        var table = world.Characters;
        var at = new Vector2(table.Next[s].Position.X, table.Next[s].Position.Z);
        var victim = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        if (world.Tick % 15 == 0 && Vector2.Distance(at, victim) > DialogueSystem.TalkReach) DialogueOrders.WalkTo(table, s, victim, SpeedMode.Walk);
    }

    /// <summary>
    /// Each stalker and its victim (read only).
    /// </summary>
    public IEnumerable<(CharacterId Stalker, CharacterId Victim)> Pairs => _stalkers.Select(p => (p.Key, p.Value.Target));

    /// <summary>
    /// Adds each stalker's victim and state, in stalker id order.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        foreach (var (who, stalk) in _stalkers)
        {
            hasher.Add(who);
            hasher.Add(stalk.Target);
            hasher.Add(stalk.Harassed);
            hasher.Add(stalk.NoGate);
            if (stalk.Waypoint is { } w) hasher.Add(w);
        }
    }
}
