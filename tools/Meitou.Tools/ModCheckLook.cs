using Meitou.Modding;
using Meitou.Simulation;

/// <summary>
/// The close look of <c>meitou-tools mod-check</c> at the first tick two runs differ after (docs/modding.md "Determinism check"): both runs again to
/// that tick, the world hashed after each serial step of it, then the characters that differ there and their differing fields side by side.
/// </summary>
static class ModCheckLook
{
    /// <summary>
    /// The most characters named, and the most differing field lines printed for the first of them.
    /// </summary>
    const int MaxCharacters = 5, MaxFieldLines = 40;

    /// <summary>
    /// One run at the looked-at tick: its hash right after the load and after each tick before, its hashes after the tick, its parts after each
    /// serial step of the tick, each slot's hash and the world itself (kept open for the fields).
    /// </summary>
    sealed record Capture(List<ulong> Before, ModCheck.Hashes End, List<(string Step, ulong[] Parts)> Steps, ulong[] Slots, World World);

    /// <summary>
    /// Opens a world for each run with <paramref name="open"/> (its thread count), runs both to <paramref name="tick"/> and prints where they differ.
    /// <paramref name="firstRun"/> is the first run's hash after that tick in the comparison, to tell whether the run came out the same again.
    /// </summary>
    public static void Run(Func<int, World> open, ModHost? mods, int tick, ulong firstRun, (string Name, int Threads) a, (string Name, int Threads) b)
    {
        Console.WriteLine($"look      both runs again to tick {tick}, hashing the world after each step of that tick...");
        using var worldA = open(a.Threads);
        var x = Advance(worldA, tick, mods);
        using var worldB = open(b.Threads);
        var y = Advance(worldB, tick, mods);

        if (x.End.World != firstRun)
            Console.WriteLine($"look      {a.Name} came out differently this time: what follows compares the new runs");

        if (x.Before[0] != y.Before[0])
        {
            Console.WriteLine("look      the new runs differ right after loading: something one run leaves behind changes the next load");
            return;
        }

        if (x.End.World == y.End.World)
        {
            Console.WriteLine($"look      the new runs agree at tick {tick}: the difference does not come back on every run");
            return;
        }

        int earlier = FirstEarlierTick(x, y);
        if (earlier < 0) ReportSteps(x, y);
        else Console.WriteLine($"look      the new runs differ from tick {earlier} already: the difference comes at another tick each run");

        ReportCharacters(x, y, a.Name, b.Name);
    }

    /// <summary>
    /// The first tick before the looked-at one after which the new runs already differ; -1 when they agree up to it.
    /// </summary>
    static int FirstEarlierTick(Capture x, Capture y)
    {
        for (int i = 1; i < Math.Min(x.Before.Count, y.Before.Count); i++)
            if (x.Before[i] != y.Before[i]) return i;
        return -1;
    }

    /// <summary>
    /// Runs the world to the tick before <paramref name="tick"/>, hashing it after each, then that tick with a hash of each part after each serial
    /// step of it.
    /// </summary>
    static Capture Advance(World world, int tick, ModHost? mods)
    {
        var before = new List<ulong> { world.StateHash() };
        for (int i = 1; i < tick; i++)
        {
            world.RunTick();
            before.Add(world.StateHash());
        }

        var steps = new List<(string Step, ulong[] Parts)>();
        world.AfterPhase = (w, step) => steps.Add((step, ModCheck.PartHashes(w)));
        world.RunTick();
        world.AfterPhase = null;

        var slots = new ulong[world.Characters.HighWater];
        for (int i = 0; i < slots.Length; i++) slots[i] = world.Characters.SlotHash(i);

        return new Capture(before, ModCheck.Snapshot(world, mods), steps, slots, world);
    }

    /// <summary>
    /// Prints the first serial step of the tick after which the runs differ, with the parts that differ there. A system that keeps its tick's
    /// state apart until its own slow world (combat swaps its buffers there) shows its difference at that step.
    /// </summary>
    static void ReportSteps(Capture x, Capture y)
    {
        for (int i = 0; i < Math.Min(x.Steps.Count, y.Steps.Count); i++)
        {
            var parts = ModCheck.DifferingParts(x.Steps[i].Parts, y.Steps[i].Parts).ToList();
            if (parts.Count == 0) continue;

            Console.WriteLine($"look      first differs after the step '{x.Steps[i].Step}', in: {string.Join(", ", parts)}");
            if (x.Steps[i].Step == "slow world" && CombatDiffers(x, y))
            {
                Console.WriteLine("          combat differs: its state of the tick is hashed from its slow world on, so it may come from an "
                    + "earlier step");
            }

            return;
        }

        Console.WriteLine("look      every step of the tick agrees: the difference comes at its end (squads refreshed, events, the table's end)");
    }

    /// <summary>
    /// Whether combat's hash differs after the tick (<c>CombatSystem</c> swaps its buffers in its slow world, so its difference shows there).
    /// </summary>
    static bool CombatDiffers(Capture x, Capture y) =>
        x.End.Systems
            .Where(p => p.Key.EndsWith(":CombatSystem", StringComparison.Ordinal))
            .Any(p => y.End.Systems.GetValueOrDefault(p.Key) != p.Value);

    /// <summary>
    /// Prints the characters whose state differs after the tick, then the differing fields of the first.
    /// </summary>
    static void ReportCharacters(Capture x, Capture y, string nameA, string nameB)
    {
        int slots = Math.Max(x.Slots.Length, y.Slots.Length);
        var differ = Enumerable.Range(0, slots)
            .Where(i => SlotHash(x, i) != SlotHash(y, i))
            .ToList();
        if (differ.Count == 0)
        {
            if (ModCheck.DifferingParts(x.End.Parts, y.End.Parts).Contains("characters"))
                Console.WriteLine("look      no character differs: the character table's size or its free slots do");
            return;
        }

        int shown = Math.Min(differ.Count, MaxCharacters);
        Console.WriteLine($"look      {differ.Count} {(differ.Count == 1 ? "character differs" : "characters differ")}, listed: {shown}");
        foreach (int slot in differ.Take(MaxCharacters)) Console.WriteLine($"look        {Describe(x.World, y.World, slot)}");
        ReportFields(x.World, y.World, differ[0], nameA, nameB);
    }

    /// <summary>
    /// A slot's hash in a capture; 0 past its table's end.
    /// </summary>
    static ulong SlotHash(Capture c, int slot) => slot < c.Slots.Length ? c.Slots[slot] : 0;

    /// <summary>
    /// A slot's character by name, record, faction and squad, from the first world that has one there.
    /// </summary>
    static string Describe(World a, World b, int slot)
    {
        var who = Living(a, slot) ?? Living(b, slot);
        if (who is null) return $"slot {slot}: empty in both";

        string missing = "";
        if (Living(a, slot) is null) missing = ", empty in the first run";
        else if (Living(b, slot) is null) missing = ", empty in the second run";

        return $"slot {slot} '{who.Name}' (record {who.RecordId}, faction {who.Faction}, squad {who.SquadId}{missing})";
    }

    /// <summary>
    /// The living character in a slot, null for an empty one.
    /// </summary>
    static CharacterCold? Living(World world, int slot)
    {
        var table = world.Characters;
        return slot < table.HighWater && table.Previous[slot].Alive ? table.Cold(slot) : null;
    }

    /// <summary>
    /// Prints the fields of a slot's character that differ between the runs (<see cref="StateFields"/>).
    /// </summary>
    static void ReportFields(World a, World b, int slot, string nameA, string nameB)
    {
        var lines = StateFields.Differences(StateFields.Of(a, slot), StateFields.Of(b, slot)).ToList();
        Console.WriteLine($"look      fields of slot {slot} that differ ({nameA} | {nameB}):");
        foreach (var line in lines.Take(MaxFieldLines)) Console.WriteLine($"look        {line}");
        if (lines.Count > MaxFieldLines) Console.WriteLine($"look        and {lines.Count - MaxFieldLines} more");
        if (lines.Count == 0)
            Console.WriteLine("look        none that the field listing reaches (its depth is limited)");
    }
}
