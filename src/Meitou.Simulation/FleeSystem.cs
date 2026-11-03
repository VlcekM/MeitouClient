using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// NPC squads running away from a lost fight (docs/simulation.md "Fleeing"; the original's facts in docs/game/perception.md "Fleeing"). All work is serial
/// (<c>SlowWorld</c>), in slot and squad-id order, so it is the same at any thread count.
/// <list type="bullet">
/// <item><b>When</b> (only with <see cref="SquadBreak"/>, off by default: the original runs no squad for being broken; the squad-intact test is <see cref="Ai.SquadIntact"/>,
/// docs/game/perception.md "Squad not intact"): every <see cref="CheckEveryTicks"/> ticks an NPC squad in a fight breaks when it is not intact. The FACTION run-away ratios are loaded by the original but never read, and no enemy is counted.</item>
/// <item><b>How</b> (the original's <c>Task_Runaway</c>, Observed): each member still standing stops fighting and runs at full speed to a point
/// <see cref="FirstRun"/> + up to <see cref="RunSpread"/> units from the centre of the enemies it can see, on the far side of it; when it gets there (or within
/// <see cref="Arrived"/> of it, or its path fails) it runs <see cref="Hop"/> + up to <see cref="RunSpread"/> units in a random direction.</item>
/// <item><b>Until</b> (the task's end conditions, <c>isNoEnemiesInVicinity</c> and not <c>isAggressionTowardsMe</c>): no enemy standing within its sight range and
/// nobody attacking it. It then goes back to wandering. A fleeing character neither looks for fights (<see cref="AggroSystem"/>) nor answers attacks
/// (<see cref="RetaliationSystem"/>). The player's characters never flee by themselves.</item>
/// <item><b>On the AI's request</b> (<see cref="Ai.AiMind.FleeRequest"/>, the same run): an NPC whose Self preservation goes to Running away (a crippled one, or one with no working arm,
/// under attack: <see cref="Ai.SelfPreservation"/>) runs from the centre of its threats and stops when none attacks it within reach (<see cref="CharacterCold.FleesAlone"/>); a goal 35 of
/// its lists starts the squads' run for one character.</item>
/// </list>
/// </summary>
public sealed class FleeSystem(FactionRelations relations, CombatSystem combat, float sightRange = AggroSystem.OutdoorSight) : ITickSystem, IStateHashed
{
    /// <summary>How often squads in a fight are checked, and fleeing characters tested for safety (engine choice, as retaliation's).</summary>
    public const int CheckEveryTicks = 15;
    /// <summary><c>Task_Runaway</c>'s first run: 2000 + 400 x a random number in [0, 1) from the threats' centre.</summary>
    public const float FirstRun = 2000, RunSpread = 400;
    /// <summary>Its later hops: 1000 + 400 x random, in a random direction, when the last one ended or is within 200 units of its end.</summary>
    public const float Hop = 1000, Arrived = 200;

    public FactionRelations Relations { get; } = relations;
    /// <summary>The sight range R before the race multiplier, for the enemies counted and the safety test (the <see cref="AggroSettings.SightRange"/> of the game).</summary>
    public float SightRange { get; } = sightRange;
    /// <summary>
    /// Whether a squad in a fight that is not intact runs (Meitou's addition, off: the original's squads do not, docs/game/perception.md "Squad not intact": only a package with the
    /// retreat signal acts on it, and no squad template lists one). The AI's own requests (a lone flight, a goal 35) run either way.
    /// </summary>
    public bool SquadBreak { get; init; }
    /// <summary>The squads that broke so far.</summary>
    public long Broken { get; private set; }
    /// <summary>Characters that started running so far.</summary>
    public long Fled { get; private set; }

    readonly List<Squad> squads = [];
    readonly HashSet<int> attacked = [];
    readonly HashSet<int> seen = [];
    readonly List<int> standing = [];
    // The characters standing this check by faction (in slot order) and, per faction asked, the standing enemies of it: a battle of one faction, or of
    // factions at peace, needs no scans, and a few enemies in a big crowd are checked directly rather than by a grid scan over the whole crowd.
    readonly Dictionary<int, List<int>> standingBy = [];
    readonly List<int> standingFactions = [];
    readonly Dictionary<int, List<int>> enemiesOf = [];
    readonly Stack<List<int>> spare = [];
    /// <summary>Up to this many enemies standing, a scan tests each of them instead of querying the grid.</summary>
    const int DirectEnemies = 256;

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        bool check = world.Tick % CheckEveryTicks == 0;
        if (check) { Attacked(table); StandingFactions(table); standingAt = world.Tick; }
        // Flights the AI asked for in this tick's think: Self preservation's provider 35 (from the threats), or a goal 35 (from the enemies it senses).
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Ai.FleeRequest: not Ai.FleeAsk.None } asker) continue;
            var ask = asker.Ai!.FleeRequest;
            asker.Ai.FleeRequest = Ai.FleeAsk.None;
            if (!asker.Fleeing && !asker.IsPlayer && asker.Medical is not { Incapacitated: true }) Begin(world, table, i, ask);
        }
        // Characters already running: the next hop, or the end when it is safe.
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive || table.Cold(i) is not { Fleeing: true } cold) continue;
            if (cold.Medical is { Incapacitated: true } || cold.IsPlayer) { cold.Fleeing = cold.FleesAlone = false; continue; }
            if (check && (cold.FleesAlone ? Unthreatened(world, i, cold) : Safe(world, table, i)))
            {
                cold.Fleeing = cold.FleesAlone = false;
                TaskStart.Become(world, i, CharacterTask.Wander, Meitou.Data.Gameplay.Ai.TaskType.WANDERER, TaskSource.Npc);
                n.TaskTime = 0;
                continue;
            }
            bool running = n.Task == (byte)CharacterTask.GoTo;
            if (running && Vector2.DistanceSquared(new Vector2(n.Position.X, n.Position.Z), n.Goal) > Arrived * Arrived) continue;
            if (n.Task == (byte)CharacterTask.Attack) world.Defer(new StopCommand([new CharacterId(i, n.Generation)]) { Tick = world.Tick + 1 });
            Run(world, table, i, RandomDirection(world, i, 1), Hop);
        }
        if (!check || !SquadBreak) return;
        // Squads in a fight that break.
        squads.Clear();
        squads.AddRange(world.Squads.All);
        squads.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var squad in squads) Consider(world, table, squad);
    }

    void Consider(World world, CharacterTable table, Squad squad)
    {
        if (!Relations.Contains(squad.Faction)) return;
        int size = 0, able = 0;
        bool fighting = false, home = Ai.SquadIntact.PersistentHome(squad);
        standing.Clear();
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { } c) continue;
            // A member running alone (Self preservation) does not stop its squad from breaking; it counts as the others do and is not started again.
            if (c.IsPlayer || c.Fleeing && !c.FleesAlone) return;
            size++;
            if (Ai.SquadIntact.Able(world, c, home)) able++;
            bool down = c.Medical is { Incapacitated: true };
            if (down || c.Fighter is null) continue;
            standing.Add(s);
            if (c.InCombat || table.Next[s].Task == (byte)CharacterTask.Attack) fighting = true;
        }
        // The squad-intact test (Ai.SquadIntact, FUN_14079acf0, docs/game/perception.md "Fleeing"): a member caged or carried off by a non-ally keeps it intact; else it
        // needs a leader and someone able; under 4 members it holds while the leader is up, from 4 while those able to fight are a share of the squad. No enemy is counted.
        if (!fighting || standing.Count == 0 || Ai.SquadIntact.Held(table, squad, Relations)) return;
        if (Ai.SquadIntact.Decide(able, size, !squad.Leader.IsNone, Ai.SquadIntact.Down(table, squad.Leader), home)) return;
        Broken++;
        foreach (int s in standing)
        {
            if (table.Cold(s)!.Fleeing) continue;
            var (centre, k) = Centre(world, table, s);
            table.Cold(s)!.FleesAlone = false;
            Begin(world, table, s, centre, k);
        }
    }

    /// <summary>
    /// A flight the AI asked for (<see cref="Ai.AiMind.FleeRequest"/>). From the threats (<see cref="Ai.FleeAsk.Threat"/>, Self preservation's provider 35, <see cref="Ai.SelfPreservation"/>): if
    /// a threat still attacks it and its body still takes the choice to running; it runs from their centre and stops when none is left. From the enemies it senses
    /// (<see cref="Ai.FleeAsk.Enemies"/>, a goal 35): as a broken squad's member does.
    /// </summary>
    void Begin(World world, CharacterTable table, int s, Ai.FleeAsk ask)
    {
        var cold = table.Cold(s)!;
        if (ask == Ai.FleeAsk.Threat)
        {
            float level = combat.ThreatLevel(world, s, out var threats);
            if (level <= 0 || !Ai.SelfPreservation.Runs(cold, level, Ai.SelfPreservation.Walled(world, s))) return;
            cold.FleesAlone = true;
            Begin(world, table, s, threats, 1);
            return;
        }
        if (standingAt != world.Tick) { StandingFactions(table); standingAt = world.Tick; }
        var (centre, k) = Centre(world, table, s);
        cold.FleesAlone = false;
        Begin(world, table, s, centre, k);
    }

    /// <summary>
    /// <c>Task_Runaway</c>'s start (<c>FUN_14034b440</c>): the character stops fighting and runs at full speed to a point <see cref="FirstRun"/> + up to <see cref="RunSpread"/> from
    /// <paramref name="centre"/> (the mean of <paramref name="count"/> positions), on the far side of it from the character; a random way when there is none.
    /// </summary>
    void Begin(World world, CharacterTable table, int s, Vector3 centre, int count)
    {
        var here = table.Next[s].Position;
        Vector2 away;
        if (count == 0) away = RandomDirection(world, s, 0);
        else
        {
            if (count > 1) centre /= count;
            away = new Vector2(here.X - centre.X, here.Z - centre.Z);
            away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : RandomDirection(world, s, 0);
        }
        var from = count == 0 ? new Vector2(here.X, here.Z) : new Vector2(centre.X, centre.Z);
        ref var n = ref table.Next[s];
        table.Cold(s)!.Fleeing = true;
        if (n.Task == (byte)CharacterTask.Attack || table.Cold(s)!.InCombat) world.Defer(new StopCommand([new CharacterId(s, n.Generation)]) { Tick = world.Tick + 1 });
        Run(world, table, s, away, FirstRun, from);
        Fled++;
    }

    /// <summary>
    /// A lone runner is safe when no threat attacks it within reach any more, or its body no longer takes the choice to running (the AI's goal 32 is not wanted then,
    /// <see cref="Ai.SelfPreservation.Runs"/>): the other goals take it over.
    /// </summary>
    bool Unthreatened(World world, int s, CharacterCold cold)
    {
        float level = combat.ThreatLevel(world, s, out _);
        return level <= 0 || !Ai.SelfPreservation.Runs(cold, level, Ai.SelfPreservation.Walled(world, s));
    }

    /// <summary>
    /// The centre <c>Task_Runaway</c> runs from (<c>FUN_14034b440</c>, <b>Verified</b>): the sum of the positions and the count of the runner's sense entries tagged NEUTRAL or ENEMY (include mask 6)
    /// and neither KO, bit 4 nor imprisoned (exclude 0x218; <c>FUN_140853960</c>), neutrals included, that intend aggression: the status float +0xac, the intended aggression of the entry's current
    /// goal (<see cref="Ai.TaskAggression.OfGoal"/>), must be above 0. The original adds the running task's +0xa0 when the entry's target matches a handle of the sense record (which one:
    /// <b>Unknown</b>, not modelled). A runner without senses falls back to the enemies standing in its sight range.
    /// </summary>
    (Vector3 Centre, int Count) Centre(World world, CharacterTable table, int s)
    {
        var centre = Vector3.Zero;
        int k = 0;
        if (world.SystemOf<AggroSystem>()?.SensesOf(s) is not { } senses)
        {
            Enemies(world, table, s, null, (_, p) => { centre += p; k++; });
            return (centre, k);
        }
        foreach (var e in senses.Entries)
        {
            var tags = e.Tags;
            if ((tags & (SenseTag.Neutral | SenseTag.Enemy)) == 0 || (tags & (SenseTag.Ko | SenseTag.Imprisoned)) != 0 || e.Target == s || (uint)e.Target >= (uint)table.HighWater) continue;
            if (!table.Next[e.Target].Alive || table.Next[e.Target].Generation != e.Generation || table.Cold(e.Target) is not { } c || Ai.TaskAggression.OfGoal(c) <= 0) continue;
            centre += table.Next[e.Target].Position;
            k++;
        }
        return (centre, k);
    }

    // The tick StandingFactions was last built on (a request may come on any tick, the periodic check on every CheckEveryTicks).
    long standingAt = -1;

    struct Scan
    {
        public CharacterTable Table;
        public FactionRelations Relations;
        public int Self, Faction;
        public Vector3 Eye;
        public float Range, Limit;
        public HashSet<int>? Into;
        public Action<int, Vector3>? Each;
        public bool Any;
    }

    /// <summary>The enemies standing within slot <paramref name="s"/>'s sight range (no view cone: the original's "in the vicinity" is what the senses hold).</summary>
    bool Enemies(World world, CharacterTable table, int s, HashSet<int>? into, Action<int, Vector3>? each, float limit = float.MaxValue)
    {
        var cold = table.Cold(s)!;
        var scan = new Scan
        {
            Table = table, Relations = Relations, Self = s, Faction = cold.Faction, Eye = table.Next[s].Position,
            Range = AggroSystem.RangeOf(cold, SightRange), Limit = limit, Into = into, Each = each,
        };
        var enemies = EnemiesOf(cold.Faction);
        if (enemies.Count <= DirectEnemies) foreach (int e in enemies) Visit(e, ref scan);
        else world.Grid.Query(scan.Eye.X, scan.Eye.Z, scan.Range, ref scan, Visit);
        return scan.Any;
    }

    static void Visit(int slot, ref Scan s)
    {
        if (slot == s.Self || s.Into?.Count > s.Limit || !s.Table.Next[slot].Alive) return;
        var p = s.Table.Next[slot].Position;
        if (Vector3.DistanceSquared(p, s.Eye) > s.Range * s.Range) return;
        if (s.Table.Cold(slot) is not { Medical.Incapacitated: false } c || !AggroSystem.IsEnemy(s.Relations, s.Faction, c.Faction)) return;
        s.Any = true;
        s.Into?.Add(slot);
        s.Each?.Invoke(slot, p);
    }

    bool Safe(World world, CharacterTable table, int s) => !attacked.Contains(s) && (!AnyEnemy(table.Cold(s)!.Faction) || !Enemies(world, table, s, null, null));

    /// <summary>Whether a character standing anywhere this check (<see cref="StandingFactions"/>) is an enemy of <paramref name="faction"/>: when none is, no scan can find one.</summary>
    bool AnyEnemy(int faction) => EnemiesOf(faction).Count > 0;

    /// <summary>The characters standing this check whose faction is an enemy of <paramref name="faction"/>, by faction in order of first appearance, then slot.</summary>
    List<int> EnemiesOf(int faction)
    {
        if (enemiesOf.TryGetValue(faction, out var list)) return list;
        list = spare.Count > 0 ? spare.Pop() : [];
        foreach (int other in standingFactions)
            if (AggroSystem.IsEnemy(Relations, faction, other)) list.AddRange(standingBy[other]);
        enemiesOf[faction] = list;
        return list;
    }

    /// <summary>The characters standing (alive, not down) by faction, for <see cref="EnemiesOf"/>; once per check.</summary>
    void StandingFactions(CharacterTable table)
    {
        foreach (var l in standingBy.Values) { l.Clear(); spare.Push(l); }
        foreach (var l in enemiesOf.Values) { l.Clear(); spare.Push(l); }
        standingBy.Clear();
        enemiesOf.Clear();
        standingFactions.Clear();
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Medical.Incapacitated: false } c) continue;
            if (!standingBy.TryGetValue(c.Faction, out var l))
            {
                standingBy[c.Faction] = l = spare.Count > 0 ? spare.Pop() : [];
                standingFactions.Add(c.Faction);
            }
            l.Add(i);
        }
    }

    /// <summary>The slots some living character is fighting (the combat state as of this tick).</summary>
    void Attacked(CharacterTable table)
    {
        attacked.Clear();
        for (int i = 0; i < table.HighWater; i++)
        {
            ref readonly var n = ref table.Next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.Attack) continue;
            var st = combat.StateOf(new CharacterId(i, n.Generation));
            if (st.TargetSlot >= 0) attacked.Add(st.TargetSlot);
        }
    }

    Vector2 RandomDirection(World world, int s, ulong salt)
    {
        var id = new CharacterId(s, world.Characters.Next[s].Generation);
        ulong roll = Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Flee, ((ulong)world.Tick << 2) | salt);
        // Task_Runaway draws x and z uniformly in [-1, 1] and normalises; a draw at the origin is replaced by +X.
        float x = Rng.Float(roll) * 2 - 1, z = Rng.Float(Rng.Mix(roll)) * 2 - 1;
        var v = new Vector2(x, z);
        return v.LengthSquared() > 1e-8f ? Vector2.Normalize(v) : Vector2.UnitX;
    }

    void Run(World world, CharacterTable table, int s, Vector2 direction, float distance, Vector2? from = null)
    {
        ref var n = ref table.Next[s];
        var id = new CharacterId(s, n.Generation);
        float r = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Flee, ((ulong)world.Tick << 2) | 2));
        var start = from ?? new Vector2(n.Position.X, n.Position.Z);
        TaskStart.Walk(world, s, start + direction * (distance + RunSpread * r), SpeedMode.Free, Meitou.Data.Gameplay.Ai.TaskType.RUN_AWAY, TaskSource.Npc);
    }

    public void Hash(ref StateHasher hasher)
    {
        // Only once something fled, so worlds without fleeing hash as before.
        if (Fled == 0) return;
        hasher.Add(Broken);
        hasher.Add(Fled);
    }
}
