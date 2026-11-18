// SPIKE CODE (task 0.7, live engine questions). Later milestones replace every part of this file; nothing here is a design to keep.
using System.Numerics;
using System.Text.Json;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Spikes;

/// <summary>
/// Spike commands (<c>pg spar-report</c>, <c>pg cage</c>, ...), applied in the system's serial <c>Inputs</c>; continuous parts (hold, carry wait,
/// spar watch) in <c>SlowWorld</c>. Answers go to log.txt as <c>PG-SPIKE {json}</c> lines.
/// </summary>
static class Spikes
{
    internal static readonly string[] Verbs =
        ["spar", "spar-report", "spar-stop", "cage", "cagestate", "uncage", "hold", "unhold", "where", "carrybed", "talk"];
    // S15's place / findspot / finish-build / sites are real test-only verbs since task 4.1 (src/Game/ArenaIdentityTestVerbs.cs).

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    internal static void Line(string kind, object o) =>
        ProvingGroundsMod.Ctx?.Log("PG-SPIKE " + JsonSerializer.Serialize(new { kind, data = o }, Json));

    internal static bool TryId(string s, out CharacterId id) => Engine.CharacterIdText.TryParse(s, out id);
    static string K(CharacterId id) => $"c:{id.Slot}.{id.Generation}";
    static float[] P(Vector3 v) => [MathF.Round(v.X, 1), MathF.Round(v.Y, 1), MathF.Round(v.Z, 1)];

    // ---------- S1 / S2: spar ----------
    sealed class Fighter
    {
        public CharacterId Id;
        public CharacterOrders Orders;
        public Vector3? HoldPoint;
        public float[] Stats = [];
        public CombatSlot Start;
        public float Hp;
    }
    static Fighter[] spar = [];
    static float[] relStart = [];
    static long relChangesStart;
    static int bountyStart;
    static readonly HashSet<string> intruders = [];
    internal static volatile int SparA = -1, SparB = -1;

    static float Hp(CharacterCold c) => c.Medical?.Parts.Sum(p => p.Flesh) ?? -1;
    static int Bounties(World w) { int n = 0; foreach (var f in spar) if (w.Characters.TryResolveNext(f.Id, out int s) && w.Characters.Cold(s)?.Bounties is { } b) n += b.Entries.Sum(e => e.Amount); return n; }
    static float[] Relations(World w, int pf) { var r = ProvingGroundsMod.Data!.Relations; var a = new float[r.Count * 2]; for (int i = 0; i < r.Count; i++) { a[2 * i] = r.Get(pf, i); a[2 * i + 1] = r.Get(i, pf); } return a; }

    static string Spar(World world, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !TryId(args[0], out var a) || !TryId(args[1], out var b)) return "usage: pg spar <idA> <idB>";
        var t = world.Characters;
        if (!t.TryResolveNext(a, out int sa) || !t.TryResolveNext(b, out int sb)) return "spar: id not found";
        var combat = world.Systems.OfType<CombatSystem>().FirstOrDefault();
        if (combat is null) return "spar: no CombatSystem";
        var list = new List<Fighter>();
        foreach (var (id, s) in new[] { (a, sa), (b, sb) })
        {
            var c = t.Cold(s)!;
            list.Add(new Fighter { Id = id, Orders = c.Orders, HoldPoint = c.HoldPoint, Stats = c.Stats?.ToArray() ?? [], Start = combat.StateOf(id), Hp = Hp(c) });
            Line("spar-orders-before", new { id = K(id), c.Name, orders = c.Orders.ToString(), hold = c.HoldPoint is { } h ? P(h) : null, player = c.IsPlayer, c.Faction });
            // the bout: no Block (it stops attacking, CombatSystem.Act.cs:677), no Passive, no Hold, no Jobs (set directly, as Orders.Apply does)
            c.Orders &= ~(CharacterOrders.Block | CharacterOrders.Passive | CharacterOrders.Hold | CharacterOrders.Jobs);
            c.HoldPoint = null;
        }
        spar = list.ToArray();
        SparA = sa; SparB = sb;
        intruders.Clear();
        int pf = ProvingGroundsMod.Data!.PlayerFaction;
        relStart = Relations(world, pf);
        relChangesStart = ProvingGroundsMod.Data!.Relations.Changes;
        bountyStart = Bounties(world);
        bool picked = t.Cold(sa)!.IsPlayer;
        world.Commands.Enqueue(new AttackOrder([a], b) { Picked = picked, Tick = world.Tick + 1 });
        world.Commands.Enqueue(new AttackOrder([b], a) { Picked = t.Cold(sb)!.IsPlayer, Tick = world.Tick + 1 });
        return $"spar: {K(a)} <-> {K(b)} ordered (Picked={picked}), relation changes counter {relChangesStart}, bounties {bountyStart}";
    }

    static string SparReport(World world, bool stopping)
    {
        if (spar.Length == 0) return "spar-report: no spar";
        var t = world.Characters;
        var combat = world.Systems.OfType<CombatSystem>().First();
        foreach (var f in spar)
        {
            if (!t.TryResolveNext(f.Id, out int s) || t.Cold(s) is not { } c) continue;
            var now = combat.StateOf(f.Id);
            var stats = c.Stats?.ToArray() ?? [];
            var gained = new Dictionary<string, float>();
            for (int k = 0; k < Math.Min(stats.Length, f.Stats.Length); k++)
                if (MathF.Abs(stats[k] - f.Stats[k]) > 1e-5f) gained[((StatsEnumerated)k).ToString()] = MathF.Round(stats[k] - f.Stats[k], 4);
            var (tgS, tgG) = combat.TargetOf(s, f.Id.Generation);
            Line("spar-fighter", new
            {
                id = K(f.Id), c.Name,
                thrown = now.Thrown - f.Start.Thrown, landed = now.Landed - f.Start.Landed, parried = now.Parried - f.Start.Parried,
                evaded = now.Evaded - f.Start.Evaded, taken = now.Taken - f.Start.Taken, blocks = now.Blocks - f.Start.Blocks, dodges = now.Dodges - f.Start.Dodges,
                hpStart = MathF.Round(f.Hp, 1), hpNow = MathF.Round(Hp(c), 1), ko = c.Medical?.Unconscious, xp = gained,
                orders = c.Orders.ToString(), target = $"{tgS}.{tgG}", inCombat = c.InCombat,
                // the S2 ApplyBlow tally became the real per-blow feed (src/Game/CombatHooks.cs, task 3.2b): the PG-SPAR result line has it
            });
        }
        int pf = ProvingGroundsMod.Data!.PlayerFaction;
        var rel = Relations(world, pf);
        int changed = 0;
        for (int k = 0; k < rel.Length; k++) if (MathF.Abs(rel[k] - relStart[k]) > 1e-4f) changed++;
        Line("spar-world", new { relationsChanged = changed, relationChangesCounter = ProvingGroundsMod.Data!.Relations.Changes - relChangesStart,
            bountiesBefore = bountyStart, bountiesNow = Bounties(world), intruders = intruders.ToArray() });
        if (!stopping) return "spar-report logged";
        // restore: the saved orders and hold point directly, then the Jobs switch through the public command so the decision step asks again
        var ids = new List<CharacterId>();
        foreach (var f in spar)
        {
            if (!t.TryResolveNext(f.Id, out int s) || t.Cold(s) is not { } c) continue;
            c.Orders = f.Orders; c.HoldPoint = f.HoldPoint; ids.Add(f.Id);
            world.Commands.Enqueue(new OrdersCommand([f.Id], CharacterOrders.Jobs, (f.Orders & CharacterOrders.Jobs) != 0) { Tick = world.Tick + 1 });
            Line("spar-orders-after", new { id = K(f.Id), orders = c.Orders.ToString(), hold = c.HoldPoint is { } h ? P(h) : null });
        }
        world.Commands.Enqueue(new StopCommand(ids) { Tick = world.Tick + 1 });
        SparA = SparB = -1;
        spar = [];
        return "spar stopped, orders restored, Stop sent";
    }

    /// <summary>Third parties fighting a spar fighter (TargetOf another's slot).</summary>
    static void WatchSpar(World world)
    {
        if (spar.Length == 0) return;
        var combat = world.Systems.OfType<CombatSystem>().FirstOrDefault();
        if (combat is null) return;
        var t = world.Characters;
        for (int i = 0; i < t.Next.Length; i++)
        {
            if (i == SparA || i == SparB || !t.Next[i].Alive) continue;
            var (ts, _) = combat.TargetOf(i, t.IdOf(i).Generation);
            if (ts == SparA || ts == SparB) intruders.Add($"{K(t.IdOf(i))} {t.Cold(i)?.Name} -> {ts}");
        }
    }

    // ---------- S11: cages ----------
    static Fixture? CageOf(World world, string site) => Game.PrisonerTestVerbs.CageOf(world, site);   // task 6.1: one copy

    // Promoted (task 6.1): the recipe is the test-only pg imprison's (src/Game/PrisonerTestVerbs.cs, src/Engine/Cages.cs Imprison); this spike
    // verb keeps its name and its M0 text answer over that one copy.
    static string Cage(World world, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !TryId(args[0], out var who)) return "usage: pg cage <prisoner> <siteId>";
        string site = args[1].StartsWith("b:") ? args[1][2..] : args[1];
        if (Game.PrisonerTestVerbs.CageOf(world, site) is not { } cage) return $"cage: no cage fixture for {site}";
        var (found, released, ok) = Engine.Cages.Imprison(world, who, cage);
        if (!found) return "cage: prisoner not found";
        var c = world.Characters.TryResolveNext(who, out int s) ? world.Characters.Cold(s)! : null;
        Line("cage", new { cage.Id, released, imprisoned = ok, held = c?.InSomething.ToString(), c?.InWhat, locked = cage.Lock?.Locked, effective = cage.Lock?.EffectivelyLocked });
        return $"cage: release {released}, imprison {ok}, locked {cage.Lock?.Locked}";
    }

    static string CageState(World world, IReadOnlyList<string> args)
    {
        if (args.Count < 1) return "usage: pg cagestate <siteId>";
        string site = args[0].StartsWith("b:") ? args[0][2..] : args[0];
        if (CageOf(world, site) is not { } cage) return $"cagestate: no cage for {site}";
        string occ = "none";
        if (world.Characters.TryResolveNext(cage.Occupant, out int s) && world.Characters.Cold(s) is { } c)
            occ = $"{K(cage.Occupant)} {c.Name} held={c.InSomething} in={c.InWhat} pos={string.Join(",", P(world.Characters.Next[s].Position))}";
        Line("cagestate", new { cage.Id, kind = cage.Kind.ToString(), occupant = occ, locked = cage.Lock?.Locked, effective = cage.Lock?.EffectivelyLocked,
            level = cage.Lock?.Level, broken = cage.Lock?.Broken, tick = world.Tick });
        return $"cagestate {cage.Id}: occupant {occ}, locked {cage.Lock?.Locked}";
    }

    static string Uncage(World world, IReadOnlyList<string> args)
    {
        if (args.Count < 1 || !TryId(args[0], out var who)) return "usage: pg uncage <prisoner>";
        return $"uncage: release {Engine.Cages.Release(world, who)}";   // task 9.3d: the bound Release (0.20.2 added a parameter)
    }

    // ---------- S10: hold an NPC ----------
    static readonly Dictionary<CharacterId, Vector3> held = [];
    static string Hold(World world, IReadOnlyList<string> args, bool on)
    {
        if (args.Count < 1 || !TryId(args[0], out var who) || !world.Characters.TryResolveNext(who, out int s)) return "usage: pg hold|unhold <id>";
        var t = world.Characters;
        var c = t.Cold(s)!;
        if (on)
        {
            held[who] = t.Next[s].Position;
            c.Engaged = true;
            if (c.Ai is { } ai) ai.Walking = false;
            TaskStart.Stand(t, s);
            return $"hold {K(who)} at {string.Join(",", P(t.Next[s].Position))} task {t.Next[s].Task}";
        }
        held.Remove(who);
        c.Engaged = false;
        if (c.Ai is { } a2) a2.Replan = true;
        return $"unhold {K(who)}: Engaged=false, Replan=true";
    }

    static string Where(World world, IReadOnlyList<string> args)
    {
        foreach (var a in args)
        {
            if (!TryId(a, out var who) || !world.Characters.TryResolveNext(who, out int s)) continue;
            var c = world.Characters.Cold(s)!;
            ref var n = ref world.Characters.Next[s];
            Line("where", new { id = K(who), c.Name, position = P(n.Position), task = ((CharacterTask)n.Task).ToString(), c.Engaged,
                heldFrom = held.TryGetValue(who, out var h) ? MathF.Round(Vector2.Distance(new(h.X, h.Z), new(n.Position.X, n.Position.Z)), 1) : -1,
                carrying = K(c.Carrying), carriedBy = K(c.CarriedBy), inSomething = c.InSomething.ToString(), c.InWhat,
                ko = c.Medical?.Unconscious, tick = world.Tick });
        }
        return "where logged";
    }

    // ---------- S8: carry to a bed ----------
    static (CharacterId Medic, CharacterId Patient, long Since)? carryJob;
    static string CarryBed(World world, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !TryId(args[0], out var medic) || !TryId(args[1], out var patient) || !world.Characters.TryResolveNext(medic, out int m))
            return "usage: pg carrybed <medic> <patient>";
        var carry = world.Systems.OfType<CarrySystem>().FirstOrDefault();
        if (carry is null) return "carrybed: no CarrySystem";
        bool ok = carry.BeginPickUp(world, m, patient);
        if (ok) carryJob = (medic, patient, world.Tick);
        return $"carrybed: BeginPickUp {ok}";
    }

    static void StepCarry(World world)
    {
        if (carryJob is not { } j) return;
        var t = world.Characters;
        if (!t.TryResolveNext(j.Medic, out int m)) { carryJob = null; return; }
        var c = t.Cold(m)!;
        if (c.Carrying != j.Patient)
        {
            if (world.Tick - j.Since > 30 * 20) { Line("carrybed", new { result = "never carried", task = ((CharacterTask)t.Next[m].Task).ToString() }); carryJob = null; }
            return;
        }
        carryJob = null;
        var bed = world.Fixtures.FindBed(world, m, ProvingGroundsMod.Data!.Relations);
        if (bed is null) { Line("carrybed", new { result = "no free bed" }); return; }
        var r = TaskStart.Start(world, m, new TaskRequest(CharacterTask.GoTo, TaskType.FindBedAndPutIn, TaskSource.Goal) { Site = bed.Id });
        Line("carrybed", new { result = r.ToString(), bed = bed.Id, bedPos = P(bed.Position), bed.Building, ticksToLift = world.Tick - j.Since });
    }

    // ---------- S4: talk ----------
    static string Talk(World world, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !TryId(args[0], out var a) || !TryId(args[1], out var b)) return "usage: pg talk <playerChar> <npc>";
        world.Commands.Enqueue(new TalkOrder(a, b) { Tick = world.Tick + 1 });
        return $"talk: {K(a)} -> {K(b)} queued";
    }

    /// <summary>Set by <c>pg spikes off</c> (frame thread); the next <see cref="Tick"/> clears the state on the sim thread.</summary>
    internal static volatile bool ClearRequested;

    /// <summary>Forgets every spike's state (holds, spar, carry job, tallies): after <c>pg spikes off</c> (through <see cref="ClearRequested"/>)
    /// and when a new world takes over (ProvingGroundsSystem.TakeOver), so a load never re-asserts Engaged on whoever now holds a held slot.
    /// Sim thread only (serial step).</summary>
    internal static void Clear()
    {
        held.Clear();
        spar = [];
        relStart = [];
        relChangesStart = 0;
        bountyStart = 0;
        intruders.Clear();
        SparA = SparB = -1;
        carryJob = null;
    }

    // ---------- dispatch ----------
    internal static string Run(World world, Engine.ConsoleLine pg) => pg.Verb switch
    {
        "spar" => Spar(world, pg.Args),
        "spar-report" => SparReport(world, false),
        "spar-stop" => SparReport(world, true),
        "cage" => Cage(world, pg.Args),
        "cagestate" => CageState(world, pg.Args),
        "uncage" => Uncage(world, pg.Args),
        "hold" => Hold(world, pg.Args, true),
        "unhold" => Hold(world, pg.Args, false),
        "where" => Where(world, pg.Args),
        "carrybed" => CarryBed(world, pg.Args),
        "talk" => Talk(world, pg.Args),
        _ => $"unknown {pg.Verb}",
    };

    /// <summary>SlowWorld (serial): re-assert holds (Engaged is cleared by others, CAP 19), the carry wait, the spar watch.</summary>
    internal static void Tick(World world)
    {
        if (ClearRequested) { ClearRequested = false; Clear(); }   // `pg spikes off` (frame thread) asked; cleared here, on the sim thread
        if (!SpikePatches.Armed) return;   // disarmed: no hold re-asserts Engaged, no carry wait, no spar watch
        var t = world.Characters;
        foreach (var id in held.Keys)
            if (t.TryResolveNext(id, out int s) && t.Cold(s) is { } c && !c.Engaged) { c.Engaged = true; Line("hold-reassert", new { id = K(id), tick = world.Tick }); }
        StepCarry(world);
        WatchSpar(world);
    }
}
