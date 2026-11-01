using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Game;

/// <summary>
/// The trailer duel (<c>--duel [x,z]</c>, docs/simulation.md "Duel"): two Samurai on the real map at sunset, nobody else, a scripted start and the deterministic
/// simulation, so every lap is the same fight to the tick. A lap is a new world made the same way (<see cref="DuelRestart"/>); the game clock, the simulation clock's
/// phase and the animation clock (clouds, water, foliage sway) start again with it, so a camera flight recorded from a restart sees the same picture frame for frame
/// whatever angle it films from. Nothing here runs in the normal game.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The built-in spot (the owner's pick, 2026-10-13): on a flat rock mesa in the dunes (top about 1350), about 160 units from its south (+Z) cliff and 370 east of the rock spire at its west end, the setting sun beyond (−X).</summary>
    public static readonly Vector2 DuelSpot = new(35400, -112020);
    /// <summary>The duel's hour, held still: the sun about 5 degrees above the horizon (SkyClock: sunrise 5, sunset 23, latitude 54).</summary>
    public const float DuelHour = 22;
    /// <summary>The CHARACTER record both fighters are made from (the base game's Samurai Heavy: Samurai Armour and Helmet, legplates, boots, a nodachi), and the faction whose looks they get.</summary>
    const string DuelRecord = "Samurai Heavy", DuelFaction = "United Cities";
    /// <summary>The two start this far apart along Z (across the sun), facing each other, and charge on <see cref="DuelOrderTick"/>.</summary>
    const float DuelGap = 40;
    const int DuelOrderTick = 45;
    /// <summary>
    /// Two Samurai of the same record are evenly matched: in the built-in duel the first one goes down after 165 s (28 hits, 61 blocks). A lap is the last
    /// <see cref="DuelLeadTicks"/> (40 s) before that and <see cref="DuelHoldTicks"/> (4 s) after; the fight before the lap is run headless at each restart
    /// (about 0.06 ms a tick). A duel with nobody down in <see cref="DuelMaxTicks"/> (5 min) loops its first <see cref="DuelOpenTicks"/> (60 s).
    /// </summary>
    const int DuelLeadTicks = 1200, DuelHoldTicks = 120, DuelMaxTicks = 9000, DuelOpenTicks = 1800;

    bool IsDuel => g.Duel && !IsSandbox;

    Vector3 duelSpot;
    Vector2 duelShift;
    string duelRecord = "";
    int? duelFaction;
    CharacterId duelA = CharacterId.None, duelB = CharacterId.None;
    // The lap: the world's ticks from duelSkipTicks (run headless at the restart) to duelEndTick.
    long duelSkipTicks, duelEndTick = DuelOpenTicks;
    ulong duelEndHash;
    int duelLaps;
    bool duelRestartRequested;
    // The looks (SpawnSandbox's variant numbers): the first two whose rolled clothing has a helmet.
    (int A, int B) duelVariants = (1, 2);
    // The hour a lap starts at: --time, or what the Tab panel's time of day slider last set (it would snap back at the next lap otherwise).
    float duelHour;
    Meitou.Simulation.WorldSettings worldSettings = null!;

    /// <summary>Finds the record and faction, measures the lap on a world of its own and starts the first lap; returns where the camera starts.</summary>
    Vector3 DuelStart()
    {
        var spot = g.DuelAt is { } at ? new Vector2(at.X, at.Z) : DuelSpot;
        duelSpot = new Vector3(spot.X, gpu.Terrain.HeightAt(spot.X, spot.Y), spot.Y);
        var db = scene.Database!;
        duelRecord = db.OfType(FcsRecordType.CHARACTER).FirstOrDefault(r => r.BaseName == DuelRecord)?.StringId
            ?? throw new InvalidOperationException($"--duel: no CHARACTER named '{DuelRecord}' in the game data");
        int faction = popData!.Factions.Select((f, i) => (f, i)).FirstOrDefault(p => p.f.BaseName == DuelFaction, (null!, -1)).Item2;
        duelFaction = faction >= 0 ? faction : null;
        if (duelFaction is null) Console.Error.WriteLine($"duel      no faction named '{DuelFaction}': the pair gets another faction's looks");
        duelVariants = HelmetedVariants();
        // The pair drifts as it fights (about 140 units in the built-in duel): the spawn is moved so the middle of the lap's fight is on the spot.
        duelHour = o.Hour;
        duelShift = Vector2.Zero;
        var middle = MeasureLap(report: false);
        duelShift = spot - middle;
        foreach (float z in new[] { -DuelGap / 2, DuelGap / 2 })
            if (!simWalkability!.IsWalkable(spot.X + duelShift.X, spot.Y + duelShift.Y + z))
                Console.Error.WriteLine($"duel      {spot.X + duelShift.X:0}, {spot.Y + duelShift.Y + z:0} is under water: pick another spot with --duel x,z");
        MeasureLap(report: true);
        DuelRestart();
        return duelSpot;
    }

    /// <summary>The Samurai's clothing is rolled from the record's lists, and many rolls have no helmet: the first two variants whose roll has one (else 1 and 2).</summary>
    (int, int) HelmetedVariants()
    {
        var found = new List<int>();
        for (int v = 1; v <= 200 && found.Count < 2; v++)
            if (duelFaction is { } f && population!.SandboxLook(worldSettings.Seed, duelRecord, false, v, f) is { } look && look.Parts.Any(p => p.Slot == AttachSlot.Hat))
                found.Add(v);
        if (found.Count < 2) Console.Error.WriteLine($"duel      no two looks of {DuelRecord} with a helmet in 200 rolls");
        return found.Count == 2 ? (found[0], found[1]) : (1, 2);
    }

    /// <summary>Fills a new world with the pair and the script: the focus on the spot, both ordered to attack each other on <see cref="DuelOrderTick"/>.</summary>
    void DuelSetup(World world, PopulationSystem population)
    {
        var at = new Vector2(duelSpot.X, duelSpot.Z) + duelShift;
        // Facing +Z is yaw 0 (the sandbox's yaw π/2 faces +X).
        duelA = population.SpawnSandbox(world, duelRecord, at - new Vector2(0, DuelGap / 2), player: false, yaw: 0, variant: duelVariants.A, faction: duelFaction);
        duelB = population.SpawnSandbox(world, duelRecord, at + new Vector2(0, DuelGap / 2), player: false, yaw: MathF.PI, variant: duelVariants.B, faction: duelFaction);
        world.Commands.Enqueue(new FocusCommand(duelSpot) { Tick = 0 });
        world.Commands.Enqueue(new AttackOrder([duelA], duelB) { Tick = DuelOrderTick });
        world.Commands.Enqueue(new AttackOrder([duelB], duelA) { Tick = DuelOrderTick });
    }

    /// <summary>Runs the duel once, headless, on a world of its own to find when the first fighter is down, which places the lap round it; returns the pair's mean middle over the lap.</summary>
    Vector2 MeasureLap(bool report)
    {
        var built = BuildSystems(interactive: true);
        var world = new World(worldSettings, simWalkability!, built.Systems);
        try
        {
            DuelSetup(world, built.Population!);
            var combat = built.Combat!;
            long down = -1;
            var middles = new List<Vector2>();   // the pair's middle after each tick
            string winner = "";
            ulong openHash = 0;
            // On past the knockout to the lap's end, for the state hash every lap must reach.
            while (world.Tick < DuelMaxTicks && (down < 0 || world.Tick < down + DuelHoldTicks))
            {
                world.RunTick();
                var (pa, pb) = (world.Characters.Previous[duelA.Slot].Position, world.Characters.Previous[duelB.Slot].Position);
                middles.Add(new Vector2(pa.X + pb.X, pa.Z + pb.Z) / 2);
                bool a = combat.StateOf(duelA).Down, b = combat.StateOf(duelB).Down;
                if (world.Tick == DuelOpenTicks) openHash = world.StateHash();
                if (down < 0 && (a || b)) (down, winner) = (world.Tick, a && b ? "both down" : a ? "the one that started at +Z wins" : "the one that started at -Z wins");
            }
            (duelSkipTicks, duelEndTick) = down < 0 ? (0, DuelOpenTicks) : (Math.Max(down - DuelLeadTicks, 0), down + DuelHoldTicks);
            var lap = middles.Skip((int)duelSkipTicks).Take((int)(Math.Min(duelEndTick, world.Tick) - duelSkipTicks)).ToList();
            var centre = lap.Count == 0 ? new Vector2(duelSpot.X, duelSpot.Z) : lap.Aggregate(Vector2.Zero, (s, p) => s + p) / lap.Count;
            duelEndHash = down < 0 ? openHash : world.StateHash();
            if (!report) return centre;
            var log = combat.Log;
            string blows = string.Join(", ", log.GroupBy(e => e.Outcome).Select(gr => $"{gr.Count()} {gr.Key}".ToLowerInvariant()));
            Console.WriteLine($"duel      blows: {blows}; first hit {(log.FirstOrDefault(e => e.Outcome == BlowOutcome.Hit) is { Tick: > 0 } h ? h.Tick / 30.0 : 0):0.0} s; KO at {string.Join(" ", log.Where(e => e.Knockout).Select(e => (e.Tick / 30.0).ToString("0.0")))}");
            Console.WriteLine($"duel      {DuelRecord} vs {DuelRecord} at {duelSpot.X:0}, {duelSpot.Z:0}: " +
                (down < 0 ? $"nobody down in {DuelMaxTicks / 30} s" : $"first down after {down / 30.0:0.0} s, {winner}") + $"; a lap is {duelSkipTicks / 30.0:0.0} s to {duelEndTick / 30.0:0.0} s ({(duelEndTick - duelSkipTicks) / 30.0:0.0} s)");
            Console.WriteLine($"duel      the spawn is moved {duelShift.X:0}, {duelShift.Y:0} so the lap's fight is round {centre.X:0}, {centre.Y:0}");
            return centre;
        }
        finally
        {
            world.Dispose();
            foreach (var s in built.Systems) (s as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Starts a lap: a new world with the pair (the same every time), the game clock back at the start hour, the simulation clock's phase and the animation clock at 0,
    /// fresh per-character renderer state. Between frames only (no tick in flight).
    /// </summary>
    void DuelRestart()
    {
        var built = BuildSystems(interactive: true);
        session.ReplaceWorld(built.Systems, world => DuelSetup(world, built.Population!));
        population = built.Population;
        worldEpoch++;
        session.Clock.SetTime(o.Day ?? Meitou.Engine.Time.GameClock.DefaultStartDay, duelHour);
        session.Simulation.ResetPhase();
        // The fight before the lap, headless; the last tick publishes the snapshot (the frame draws the pair at once).
        do session.World.RunTick();
        while (session.World.Tick <= duelSkipTicks);
        session.Clock.Advance(session.Simulation.TickSeconds);
        animSeconds = 0;
        indexedFor = null;
        previousById.Clear();
        player.Reset();
        duelLaps++;
    }

    /// <summary>After the frame's ticks: the duel's sun stands still (the hour of the lap's start, or the Tab slider's), so every lap and every moment of one has the same light.</summary>
    void DuelHoldClock()
    {
        if (IsDuel) session.Clock.HoldHourOfDay(duelHour);
    }

    /// <summary>Once per frame, between ticks: a restart asked for (Backspace, a cinema flight starting) or the end of the lap starts the next one.</summary>
    void DuelFrame(bool flightStarted)
    {
        if (!IsDuel) return;
        var world = session.World;
        if (!duelRestartRequested && !flightStarted && world.Tick < duelEndTick) return;
        // A lap that ran to its end on the tick must be the measured one (a frame that ran past it has nothing to compare).
        if (world.Tick == duelEndTick)
            Console.WriteLine(world.StateHash() is var hash && hash == duelEndHash ? $"duel      lap {duelLaps} ended as measured (state hash {hash:x16})"
                : $"duel      lap {duelLaps} DIFFERS from the measured run: state hash {hash:x16}, measured {duelEndHash:x16}");
        DuelRestart();
        if (duelRestartRequested) player.Notice("Duel restarted (Backspace)");
        duelRestartRequested = false;
    }
}
