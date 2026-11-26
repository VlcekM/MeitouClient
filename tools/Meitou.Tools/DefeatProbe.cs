using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

/// <summary>
/// The <c>defeat</c> mode of <c>meitou-tools probe</c>: a lone player character, weakened to a sliver of health and optionally given a bounty with
/// the guard's faction, attacks the nearest squad leader named like the guard; then every 20 ticks the lines said, both characters' state, the
/// guard's orders and both squads' long-term memory bits (event 15 I_DEFEATED_SQUAD and its law lines, DA 29).
/// </summary>
static class DefeatProbe
{
    /// <summary>
    /// The share of each body part's health the victim keeps, so one blow downs it.
    /// </summary>
    const float Sliver = 0.05f;

    /// <summary>
    /// The round the victim is weakened at, once the fight's opening shouts have faded.
    /// </summary>
    const int WeakenRound = 8;

    /// <summary>
    /// Runs the case on the guard named like <paramref name="guardName"/> and prints <paramref name="rounds"/> rounds.
    /// </summary>
    public static void Run(ProbeWorld probe, string guardName, int bounty, int rounds)
    {
        if (Guard(probe, guardName) is not { } guard)
        {
            Console.WriteLine("no guard leader with event 15 lines");
            return;
        }

        int victim = Isolate(probe, guard);
        Start(probe, victim, guard, bounty);
        for (int round = 0; round < rounds; round++)
        {
            if (round == WeakenRound) Weaken(probe, victim);
            probe.Tick(20);
            Print(probe, victim, guard);
        }
    }

    /// <summary>
    /// The player character nearest the guard, moved into a new squad of its own unless it is alone already.
    /// </summary>
    static int Isolate(ProbeWorld probe, int guard)
    {
        var world = probe.World;
        var t = probe.Characters;
        int victim = Enumerable.Range(0, t.HighWater)
            .Where(i => t.Next[i].Alive && t.Cold(i) is { IsPlayer: true, Medical.Incapacitated: false })
            .MinBy(i => Vector3.Distance(t.Next[i].Position, t.Next[guard].Position));
        if (world.Squads.Find(t.Cold(victim)!.SquadId) is { Members.Count: 1 }) return victim;

        var before = world.Squads.All.Select(q => q.Id).ToHashSet();
        world.Commands.Enqueue(new NewSquadCommand(t.Next[victim].Position) { Tick = world.Tick });
        probe.Tick(1);
        var fresh = world.Squads.All.First(q => !before.Contains(q.Id));
        world.Commands.Enqueue(new MoveToSquadCommand(t.IdOf(victim), fresh.Id) { Tick = world.Tick });
        probe.Tick(1);
        return victim;
    }

    /// <summary>
    /// The awake squad leader named like <paramref name="name"/> that has lines for event 15: of the smallest squad, then nearest the player leader.
    /// </summary>
    static int? Guard(ProbeWorld probe, string name)
    {
        var t = probe.Characters;
        var dialogue = probe.World.SystemOf<DialogueSystem>()!;
        var leaders = probe.Named(name)
            .Where(i => t.Cold(i) is { Medical.Incapacitated: false } c && probe.World.Squads.Find(c.SquadId)?.Leader == t.IdOf(i))
            .Where(i => (CharacterTask)t.Next[i].Task != CharacterTask.Sleep && dialogue.SetOf(probe.World, i).For(15).Any())
            .ToList();
        if (leaders.Count == 0) return null;

        return leaders
            .OrderBy(i => probe.World.Squads.Find(t.Cold(i)!.SquadId)!.Members.Count)
            .ThenBy(i => Vector3.Distance(t.Next[i].Position, probe.LeaderPosition))
            .First();
    }

    /// <summary>
    /// Weakens the victim, books the bounty, puts it beside the guard and orders the attack.
    /// </summary>
    static void Start(ProbeWorld probe, int victim, int guard, int bounty)
    {
        var world = probe.World;
        var t = probe.Characters;
        var vc = t.Cold(victim)!;
        if (bounty > 0) world.SystemOf<CrimeSystem>()!.AssignBounty(world, t.IdOf(victim), t.Cold(guard)!.Faction, bounty);

        var beside = t.Next[guard].Position + new Vector3(60, 0, 0);
        world.Commands.Enqueue(new DebugTeleport([t.IdOf(victim)], beside) { Tick = world.Tick });
        probe.Tick(1);

        var arrests = world.SystemOf<ArrestSystem>();
        Console.WriteLine($"victim {probe.Who(victim)} bounty={vc.Bounties?.Total}");
        Console.WriteLine($"guard {probe.Who(guard)} inOwnTown={arrests?.InOwnTown(world, guard)}");
        world.Commands.Enqueue(new AttackOrder([t.IdOf(victim)], t.IdOf(guard)) { Picked = true, Unprovoked = true, Tick = world.Tick });
    }

    /// <summary>
    /// Leaves the victim a sliver of each body part's health.
    /// </summary>
    static void Weaken(ProbeWorld probe, int victim)
    {
        foreach (var part in probe.Characters.Cold(victim)!.Medical!.Parts) part.Flesh = part.MaxHp * Sliver;
    }

    /// <summary>
    /// One round: the lines said, the victim, the one that downed it (its orders, row and whether it leads its squad) and the squads' long-term bits.
    /// </summary>
    static void Print(ProbeWorld probe, int victim, int guard)
    {
        var t = probe.Characters;
        var memory = probe.World.SystemOf<DialogueSystem>()!.Memory;
        foreach (var line in probe.World.TakeSaid()) Console.WriteLine($"  SAID {line.Owner}: {line.Text}");

        var vc = t.Cold(victim)!;
        int striker = t.TryResolveNext(vc.DefeatedBy, out int ds) ? ds : guard;
        var sc = t.Cold(striker)!;
        bool leads = probe.World.Squads.Find(sc.SquadId)?.Leader == t.IdOf(striker);
        string orders = string.Join(", ", sc.OrderQueue.Select(o => o.Row));
        Console.WriteLine($"tick {probe.World.Tick} victim down={vc.Medical!.Incapacitated} carriedBy={vc.CarriedBy} in={vc.InSomething} "
            + $"striker {striker} '{sc.Name}' leads={leads} task={(CharacterTask)t.Next[striker].Task} row={sc.Running?.Row} orders=[{orders}]");
        Console.WriteLine($"  victim squad: {Bits(memory.Snapshot(vc.SquadId))}  striker squad: {Bits(memory.Snapshot(sc.SquadId))}");
        probe.PrintMessages();
    }

    /// <summary>
    /// A squad's long-term memory bits per remembered character, and its player entry's.
    /// </summary>
    static string Bits(SquadMemoryData? data)
    {
        if (data is null) return "-";

        var entries = data.Entries.Select(e => $"{e.Who.Slot}:{e.Long:X}");
        string player = data.Player is { } p ? $" player:{p.Long:X}" : "";
        return string.Join(" ", entries) + player;
    }
}
