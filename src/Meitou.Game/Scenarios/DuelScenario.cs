using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Population;

namespace Meitou.Game.Scenarios;

/// <summary>
/// The trailer duel (<c>--duel [x,z]</c>, docs/simulation.md "Duel"): two Samurai on the real map at sunset, nobody else, a scripted start and the
/// deterministic simulation, so every lap is the same fight to the tick. A lap is a new world made the same way (<see cref="DuelRestart"/>); the game
/// clock, the simulation clock's phase and the animation clock (clouds, water, foliage sway) start again with it, so a camera flight recorded from a
/// restart sees the same picture frame for frame whatever angle it films from. Nothing here runs in the normal game.
/// </summary>
sealed class DuelScenario(GameHost host)
{
    /// <summary>
    /// The built-in spot (the owner's pick, 2026-10-13): on a flat rock mesa in the dunes (top about 1350), about 160 units from its south (+Z) cliff
    /// and 370 east of the rock spire at its west end, the setting sun beyond (−X).
    /// </summary>
    public static readonly Vector2 DuelSpot = new(35400, -112020);

    /// <summary>
    /// The duel's hour, held still: the sun about 5 degrees above the horizon (SkyClock: sunrise 5, sunset 23, latitude 54).
    /// </summary>
    public const float DuelHour = 22;

    /// <summary>
    /// The CHARACTER record both fighters are made from (the base game's Samurai Heavy: Samurai Armour and Helmet, legplates, boots, a nodachi), and
    /// the faction whose looks they get.
    /// </summary>
    const string DuelRecord = "Samurai Heavy", DuelFaction = "United Cities";

    /// <summary>
    /// The two start this far apart along Z (across the sun), facing each other, and charge on <see cref="DuelOrderTick"/>.
    /// </summary>
    const float DuelGap = 40;
    const int DuelOrderTick = 45;

    Vector3 _duelSpot;
    Vector2 _duelShift;
    string _duelRecord = "";
    int? _duelFaction;
    CharacterId _duelA = CharacterId.None, _duelB = CharacterId.None;

    /// <summary>
    /// The lap, measured at the start.
    /// </summary>
    readonly DuelLap _lap = new();

    /// <summary>
    /// The laps started.
    /// </summary>
    int _duelLaps;

    // The looks (SpawnSandbox's variant numbers): the first two whose rolled clothing has a helmet.
    (int A, int B) _duelVariants = (1, 2);

    // The hour a lap starts at: --time, or what the Tab panel's time of day slider last set (it would snap back at the next lap otherwise).
    internal float HeldHour;
    internal WorldSettings WorldSettings = null!;
    internal bool DuelRestartRequested;

    internal bool IsDuel => host.GameOptions.Duel && !host.Sandbox.IsSandbox;

    /// <summary>
    /// Finds the record and faction, measures the lap on a world of its own and starts the first lap; returns where the camera starts.
    /// </summary>
    internal Vector3 DuelStart()
    {
        var spot = host.GameOptions.DuelAt is { } at ? new Vector2(at.X, at.Z) : DuelSpot;
        _duelSpot = new Vector3(spot.X, host.Gpu.Terrain.HeightAt(spot.X, spot.Y), spot.Y);
        var db = host.Scene.Database!;
        _duelRecord = db.OfType(FcsRecordType.Character).FirstOrDefault(r => r.BaseName == DuelRecord)?.StringId
            ?? throw new InvalidOperationException($"--duel: no CHARACTER named '{DuelRecord}' in the game data");
        int faction = host.PopData!.Factions
            .Select((f, i) => (f, i))
            .FirstOrDefault(p => p.f.BaseName == DuelFaction, (null!, -1))
            .Item2;
        _duelFaction = faction >= 0 ? faction : null;
        if (_duelFaction is null) Console.Error.WriteLine($"duel      no faction named '{DuelFaction}': the pair gets another faction's looks");

        _duelVariants = HelmetedVariants();
        CentreOnSpot(spot);
        MeasureLap(report: true);
        DuelRestart();
        return _duelSpot;
    }

    /// <summary>
    /// The pair drifts as it fights (about 140 units in the built-in duel): the spawn is moved so the middle of the lap's fight is on the spot.
    /// </summary>
    void CentreOnSpot(Vector2 spot)
    {
        HeldHour = host.WorldOptions.Hour;
        _duelShift = Vector2.Zero;
        var middle = MeasureLap(report: false);
        _duelShift = spot - middle;
        foreach (float z in new[] { -DuelGap / 2, DuelGap / 2 })
        {
            if (!host.SimWalkability!.IsWalkable(spot.X + _duelShift.X, spot.Y + _duelShift.Y + z))
            {
                Console.Error.WriteLine($"duel      {spot.X + _duelShift.X:0}, {spot.Y + _duelShift.Y + z:0} is under water: pick another spot with --duel x,z");
            }
        }
    }

    /// <summary>
    /// The Samurai's clothing is rolled from the record's lists, and many rolls have no helmet: the first two variants whose roll has one (else 1 and
    /// 2).
    /// </summary>
    (int, int) HelmetedVariants()
    {
        var found = new List<int>();
        for (int v = 1; v <= 200 && found.Count < 2; v++)
        {
            if (_duelFaction is not { } f) continue;

            var look = host.Population!.SandboxLook(WorldSettings.Seed, _duelRecord, false, v, f);
            if (look is not null && look.Parts.Any(p => p.Slot == AttachSlot.Hat)) found.Add(v);
        }

        if (found.Count < 2) Console.Error.WriteLine($"duel      no two looks of {DuelRecord} with a helmet in 200 rolls");
        return found.Count == 2 ? (found[0], found[1]) : (1, 2);
    }

    /// <summary>
    /// Fills a new world with the pair and the script: the focus on the spot, both ordered to attack each other on <see cref="DuelOrderTick"/>.
    /// </summary>
    void DuelSetup(World world, PopulationSystem population)
    {
        var at = new Vector2(_duelSpot.X, _duelSpot.Z) + _duelShift;

        // Facing +Z is yaw 0 (the sandbox's yaw π/2 faces +X).
        _duelA = population.SpawnSandbox(world, _duelRecord, at - new Vector2(0, DuelGap / 2), player: false, yaw: 0,
            variant: _duelVariants.A, faction: _duelFaction);
        _duelB = population.SpawnSandbox(world, _duelRecord, at + new Vector2(0, DuelGap / 2), player: false, yaw: MathF.PI,
            variant: _duelVariants.B, faction: _duelFaction);
        world.Commands.Enqueue(new FocusCommand(_duelSpot) { Tick = 0 });
        world.Commands.Enqueue(new AttackOrder([_duelA], _duelB) { Tick = DuelOrderTick });
        world.Commands.Enqueue(new AttackOrder([_duelB], _duelA) { Tick = DuelOrderTick });
    }

    /// <summary>
    /// Runs the duel once, headless, on a world of its own to find when the first fighter is down, which places the lap round it; returns the pair's
    /// mean middle over the lap.
    /// </summary>
    Vector2 MeasureLap(bool report)
    {
        var built = host.Saves.BuildSystems(interactive: true);
        var world = new World(WorldSettings, host.SimWalkability!, built.Systems);
        try
        {
            DuelSetup(world, built.Population!);
            var combat = built.Combat!;
            var (down, winner, centre) = _lap.Measure(world, combat, _duelA, _duelB, new Vector2(_duelSpot.X, _duelSpot.Z));
            if (!report) return centre;

            ReportLap(combat, down, winner, centre);
            return centre;
        }
        finally
        {
            world.Dispose();
            foreach (var s in built.Systems) (s as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The measured lap's blows, first hit, knockouts and timing, for the log.
    /// </summary>
    void ReportLap(CombatSystem combat, long down, string winner, Vector2 centre)
    {
        var log = combat.Log;
        string blows = string.Join(", ", log.GroupBy(e => e.Outcome).Select(gr => $"{gr.Count()} {gr.Key}".ToLowerInvariant()));
        var firstHit = log.FirstOrDefault(e => e.Outcome == BlowOutcome.Hit) is { Tick: > 0 } h ? h.Tick / 30.0 : 0;
        string kos = string.Join(" ", log.Where(e => e.Knockout).Select(e => (e.Tick / 30.0).ToString("0.0")));
        Console.WriteLine($"duel      blows: {blows}; first hit {firstHit:0.0} s; KO at {kos}");
        Console.WriteLine($"duel      {DuelRecord} vs {DuelRecord} at {_duelSpot.X:0}, {_duelSpot.Z:0}: " +
            (down < 0 ? $"nobody down in {DuelLap.DuelMaxTicks / 30} s" : $"first down after {down / 30.0:0.0} s, {winner}") +
            $"; a lap is {_lap.SkipTicks / 30.0:0.0} s to {_lap.EndTick / 30.0:0.0} s ({(_lap.EndTick - _lap.SkipTicks) / 30.0:0.0} s)");
        Console.WriteLine($"duel      the spawn is moved {_duelShift.X:0}, {_duelShift.Y:0} so the lap's fight is round {centre.X:0}, {centre.Y:0}");
    }

    /// <summary>
    /// Starts a lap: a new world with the pair (the same every time), the game clock back at the start hour, the simulation clock's phase and the
    /// animation clock at 0, fresh per-character renderer state. Between frames only (no tick in flight).
    /// </summary>
    void DuelRestart()
    {
        var built = host.Saves.BuildSystems(interactive: true);
        host.Session.ReplaceWorld(built.Systems, world => DuelSetup(world, built.Population!));
        host.Population = built.Population;
        host.Saves.WorldEpoch++;
        host.Session.Clock.SetTime(host.WorldOptions.Day ?? Simulation.Session.GameClock.DefaultStartDay, HeldHour);
        host.Session.Simulation.ResetPhase();

        // The fight before the lap, headless; the last tick publishes the snapshot (the frame draws the pair at once).
        do
        {
            host.Session.World.RunTick();
        }
        while (host.Session.World.Tick <= _lap.SkipTicks);

        host.Session.Clock.Advance(host.Session.Simulation.TickSeconds);
        host.AnimSeconds = 0;
        host.IndexedFor = null;
        host.PreviousById.Clear();
        host.Player.Reset();
        _duelLaps++;
    }

    /// <summary>
    /// After the frame's ticks: the duel's sun stands still (the hour of the lap's start, or the Tab slider's), so every lap and every moment of one
    /// has the same light.
    /// </summary>
    internal void DuelHoldClock()
    {
        if (IsDuel) host.Session.Clock.HoldHourOfDay(HeldHour);
    }

    /// <summary>
    /// Once per frame, between ticks: a restart asked for (Backspace, a cinema flight starting) or the end of the lap starts the next one.
    /// </summary>
    internal void DuelFrame(bool flightStarted)
    {
        if (!IsDuel) return;

        var world = host.Session.World;
        if (!DuelRestartRequested && !flightStarted && world.Tick < _lap.EndTick) return;

        // A lap that ran to its end on the tick must be the measured one (a frame that ran past it has nothing to compare).
        if (world.Tick == _lap.EndTick) _lap.ReportLapEnd(_duelLaps, world.StateHash());
        DuelRestart();
        if (DuelRestartRequested) host.Player.Notice("Duel restarted (Backspace)");
        DuelRestartRequested = false;
    }
}
