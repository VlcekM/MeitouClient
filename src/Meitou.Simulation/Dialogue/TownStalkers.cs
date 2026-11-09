using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// DA_THUG_HUNTER: the town stalkers and their victims (hashed).
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
        /// Its town has no gate: it only stays.
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
    /// <paramref name="who"/> starts stalking <paramref name="target"/> (<see cref="StepStalkers"/>).
    /// </summary>
    public void Add(CharacterId who, CharacterId target) => _stalkers[who] = new Stalk(target);

    /// <summary>
    /// DA_THUG_HUNTER (17, order 165 <c>Task_TownStalker</c>, docs/game/dialogue.md): in a town, the stalker waits <see cref="StalkPastGate"/>
    /// outside the town's first gate (on the line from the town centre through it), facing its victim, while the victim is no farther from the centre
    /// than that point; with no gate it just stays. Once the victim is farther out, or anywhere outside a town, it follows it and, once, when it is
    /// not talking, sends it event 26. It is done when, after that, neither is talking. Engine choices: re-aimed every 15 ticks; the first gate is
    /// the town's first gate door in fixture order; the gate-code test on the gate's outside is not made.
    /// </summary>
    public void StepStalkers(World world)
    {
        if (_stalkers.Count == 0) return;
        var table = world.Characters;
        foreach (var (who, stalk) in _stalkers.ToArray())
        {
            if (!table.TryResolveNext(who, out int s) || !DialogueSystem.Awake(table, s) || !table.TryResolveNext(stalk.Target, out int t) || !DialogueSystem.Awake(table, t)) { _stalkers.Remove(who); DialogueSystem.Release(world, who); continue; }
            bool talking = system.Talking(who);
            bool victimTalking = system.Talking(stalk.Target);
            if (stalk.Harassed && !talking && !victimTalking) { _stalkers.Remove(who); DialogueSystem.Release(world, who); continue; }
            table.Cold(s)!.Engaged = true;   // the order holds it, not the AI
            if (table.Cold(s)!.InCombat) continue;   // its walks would end the fight it is in (a new task drops the fight task)
            ref var sn = ref table.Next[s];
            var victim = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
            int site = AiSystem.TownAt(data, world, sn.Position).Site;
            if (site >= 0 && !stalk.NoGate)
            {
                var centre = new Vector2(data.Sites[site].Position.X, data.Sites[site].Position.Z);
                if (stalk.Waypoint is null)
                {
                    var gate = world.Fixtures.All.FirstOrDefault(f => f.Kind == Locks.FixtureKind.Door && f.IsGate && f.Site == site);
                    if (gate is null) { stalk.NoGate = true; continue; }
                    var g = new Vector2(gate.Position.X, gate.Position.Z);
                    var outward = g - centre;
                    stalk.Waypoint = g + (outward.LengthSquared() > 1e-6f ? Vector2.Normalize(outward) : Vector2.UnitX) * StalkPastGate;
                }
                var w = stalk.Waypoint.Value;
                if (Vector2.Distance(victim, centre) <= Vector2.Distance(w, centre))
                {
                    if (world.Tick % 15 == 0 && Vector2.Distance(new Vector2(sn.Position.X, sn.Position.Z), w) > DialogueSystem.TalkReach && (sn.Task != (byte)CharacterTask.GoTo || Vector2.Distance(sn.Goal, w) > DialogueSystem.TalkReach))
                        TaskStart.Walk(world, s, w, SpeedMode.Walk, TaskType.TownStalker, TaskSource.Npc);
                    else if (sn.Task != (byte)CharacterTask.GoTo)
                    {
                        var to = table.Next[t].Position - sn.Position;
                        if (to.X * to.X + to.Z * to.Z > 1e-4f) sn.Yaw = MathF.Atan2(to.X, to.Z);
                    }
                    continue;
                }
            }
            else if (site >= 0) continue;   // no gate: it only stays
            // Outside: follow and harass.
            if (!stalk.Harassed && !talking)
            {
                world.Defer(new DialogueEvent(who, stalk.Target, Harassment) { Tick = world.Tick + 1 });
                stalk.Harassed = true;
            }
            if (world.Tick % 15 == 0 && Vector2.Distance(new Vector2(sn.Position.X, sn.Position.Z), victim) > DialogueSystem.TalkReach)
                TaskStart.Walk(world, s, victim, SpeedMode.Walk, TaskType.TownStalker, TaskSource.Npc);
        }
    }

    /// <summary>
    /// Adds each stalker's victim and state, in stalker id order.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        foreach (var (who, stalk) in _stalkers) { hasher.Add(who); hasher.Add(stalk.Target); hasher.Add(stalk.Harassed); hasher.Add(stalk.NoGate); if (stalk.Waypoint is { } w) hasher.Add(w); }
    }
}
