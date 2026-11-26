using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

/// <summary>
/// The <c>defeat</c> mode of <c>meitou-tools probe</c>, the back half of a squad defeat: a lone player character, optionally given a bounty with the
/// guards' faction, is knocked out, then each awake squad leader named like the guard that has lines for event 15 I_DEFEATED_SQUAD is fired that
/// event at it in turn (as <see cref="Meitou.Simulation.Combat.SquadDefeat"/> does from a striker) until one says a line; per leader whether a
/// line was said, then the lines said and its orders over the next rounds (the law lines' DA 29).
/// </summary>
static class DefeatProbe
{
    /// <summary>
    /// How long the victim stays out cold, in seconds.
    /// </summary>
    const float KoSeconds = 600;

    /// <summary>
    /// How often each leader is fired at: <c>chance temporary</c> rolls again at every check.
    /// </summary>
    const int Tries = 6;

    /// <summary>
    /// Runs the case on the leaders named like <paramref name="guardName"/>, printing <paramref name="rounds"/> rounds of 20 ticks after a line.
    /// </summary>
    public static void Run(ProbeWorld probe, string guardName, int bounty, int rounds)
    {
        var guards = Guards(probe, guardName);
        if (guards.Count == 0)
        {
            Console.WriteLine("no awake leader with event 15 lines");
            return;
        }

        int victim = Isolate(probe, guards[0]);
        KnockOut(probe, victim, guards[0], bounty);
        foreach (int guard in guards)
        {
            if (!Fire(probe, guard, victim)) continue;

            for (int round = 0; round < rounds; round++)
            {
                probe.Tick(20);
                Print(probe, victim, guard);
            }

            return;
        }
    }

    /// <summary>
    /// The awake squad leaders named like <paramref name="name"/> that have lines for event 15, nearest the player leader first.
    /// </summary>
    static List<int> Guards(ProbeWorld probe, string name)
    {
        var t = probe.Characters;
        var dialogue = probe.World.SystemOf<DialogueSystem>()!;
        return probe.Named(name)
            .Where(i => t.Cold(i) is { Medical.Incapacitated: false } c && probe.World.Squads.Find(c.SquadId)?.Leader == t.IdOf(i))
            .Where(i => (CharacterTask)t.Next[i].Task != CharacterTask.Sleep && dialogue.SetOf(probe.World, i).For(15).Any())
            .OrderBy(i => Vector3.Distance(t.Next[i].Position, probe.LeaderPosition))
            .ToList();
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
    /// Books the bounty with the guard's faction and knocks the victim out.
    /// </summary>
    static void KnockOut(ProbeWorld probe, int victim, int guard, int bounty)
    {
        var world = probe.World;
        var t = probe.Characters;
        var vc = t.Cold(victim)!;
        if (bounty > 0) world.SystemOf<CrimeSystem>()!.AssignBounty(world, t.IdOf(victim), t.Cold(guard)!.Faction, bounty);

        vc.Medical!.KnockOut(KoSeconds);
        probe.Tick(1);
        Console.WriteLine($"victim {probe.Who(victim)} down={vc.Medical.Incapacitated} bounty={vc.Bounties?.Total ?? 0}");
    }

    /// <summary>
    /// Fires event 15 from the guard at the victim up to <see cref="Tries"/> times, a tick apart, and prints the guard and whether it said a line.
    /// </summary>
    static bool Fire(ProbeWorld probe, int guard, int victim)
    {
        var world = probe.World;
        var t = probe.Characters;
        var dialogue = world.SystemOf<DialogueSystem>()!;
        bool said = false;
        for (int i = 0; i < Tries && !said; i++)
        {
            said = dialogue.FireEvent(world, t.IdOf(guard), t.IdOf(victim), 15);
            if (!said) probe.Tick(1);
        }

        bool home = world.SystemOf<ArrestSystem>()?.InOwnTown(world, guard) == true;
        Console.WriteLine($"guard {probe.Who(guard)} inOwnTown={home} said={said}");
        return said;
    }

    /// <summary>
    /// One round: the lines said, the victim and the guard's task, row and orders.
    /// </summary>
    static void Print(ProbeWorld probe, int victim, int guard)
    {
        var t = probe.Characters;
        foreach (var line in probe.World.TakeSaid()) Console.WriteLine($"  SAID {line.Owner}: {line.Text}");

        var vc = t.Cold(victim)!;
        var gc = t.Cold(guard)!;
        string orders = string.Join(", ", gc.OrderQueue.Select(o => o.Row));
        Console.WriteLine($"tick {probe.World.Tick} victim down={vc.Medical!.Incapacitated} carriedBy={vc.CarriedBy} in={vc.InSomething} "
            + $"guard task={(CharacterTask)t.Next[guard].Task} row={gc.Running?.Row} orders=[{orders}]");
        probe.PrintMessages();
    }
}
