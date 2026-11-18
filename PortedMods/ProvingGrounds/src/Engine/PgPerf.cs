using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

/// <summary>
/// The cost-per-tick instrument (task 9.2a; Core/Glue/TickCost.cs): one switch for the whole process, off by default, turned on by the
/// test-only <c>pg perf on</c>. The tick system measures its <c>SlowWorld</c> and <c>Inputs</c> into its own per-world windows
/// (<see cref="ProvingGroundsSystem"/>); every Harmony patch body of <see cref="PgPatches.All"/> measures itself into its slot here with one
/// line, <c>using var perf = PgPerf.Measure(PgPerf.Slot);</c> (a struct scope: its Dispose is the body's finally; off = one volatile read).
/// The patch counters are process-wide (patches are), the tick windows per world.
/// </summary>
static class PgPerf
{
    // The patch slots, one per patch METHOD of PgPatches.All() (a prefix and a postfix of one patch are two slots), in All()'s order.
    internal const int ConsoleRegister = 0,
        QuickSave = 1, HandleSavesPrefix = 2, HandleSavesPostfix = 3,
        ApplyBlowPrefix = 4, ApplyBlowPostfix = 5, BestAttackerOf = 6, FindFoe = 7, BeginPickUp = 8, Remember = 9, SetPending = 10, DeedsBlow = 11,
        FindPatient = 12,
        MedicalApplyPrefix = 13, MedicalApplyPostfix = 14,
        LoadUi = 15, BindScreen = 16, MouseDownPrefix = 17, MouseDownPostfix = 18, MouseUpPrefix = 19, MouseUpPostfix = 20, MouseMove = 21,
        LoopFrame = 22, Wheel = 23, TradePortraits = 24,
        PickedRightDown = 25, SiteRelease = 26, OrdersGive = 27, PickWithSelection = 28, ConversationEffect = 29,
        UiKey = 30;

    static readonly string[] Names =
    [
        "DevConsoleHost.RegisterConsoleCommands postfix",
        "GameSaves.QuickSaveNow prefix", "GameSaves.HandleSaves prefix", "GameSaves.HandleSaves postfix",
        "CombatSystem.ApplyBlow prefix", "CombatSystem.ApplyBlow postfix", "CombatSystem.BestAttackerOf postfix", "AggroSystem.FindFoe postfix",
        "CarrySystem.BeginPickUp prefix", "FactionDeeds.Remember prefix", "CrimeSystem.SetPending prefix", "FactionDeeds.Blow prefix",
        "FirstAidSystem.FindPatient postfix",
        "FirstAidSystem.Apply prefix", "FirstAidSystem.Apply postfix",
        "PlayerInterface.LoadUi postfix", "ScreenRouter.BindFaithfulScreen postfix", "PlayerInterface.MouseDown prefix", "PlayerInterface.MouseDown postfix",
        "PlayerInterface.MouseUp prefix", "PlayerInterface.MouseUp postfix", "PlayerInterface.MouseMove postfix", "GameLoop.Frame prefix",
        "MyGuiScreenHost.Wheel prefix", "TradeWindows.CharacterTradePortraits postfix",
        "PlayerInterface.PickedRightDown prefix", "BuildMode.SiteRelease prefix", "PlayerOrders.Give prefix", "PointerView.PickWithSelection postfix",
        "DialogueSystem.ConversationEffect prefix",
        "PlayerInterface.UiKey postfix",
    ];

    /// <summary>The patch counters (any thread).
    /// <para>First use (task 9.2a review): this class's static initializer allocates <see cref="Names"/> and the counters, and the runtime takes
    /// its class-init lock once. That happens on a serial, frame-thread or startup path long before any AI-thread patch can run: the first
    /// pg_debug.log line of the mod's Initialize (PgLogSink.Append, task 9.2b), the console's
    /// registration postfix (startup) and every tick's Inputs / SlowWorld call into PgPerf first, so a parallel AI-thread patch body (the
    /// BestAttackerOf / FindFoe postfixes) never pays the initializer or waits on its lock.</para></summary>
    internal static readonly PatchCostCounters Patches = new(Names);

    /// <summary>The number of patch slots and slot <paramref name="slot"/>'s name ("Target.Method prefix|postfix"): PgCheck hooks checks them
    /// against PgPatches.All() in order (count, names, and the slot constant each patch method passes to <see cref="Measure"/>).</summary>
    internal static int SlotCount => Names.Length;
    internal static string SlotName(int slot) => Names[slot];

    /// <summary>Whether the instrument runs (any thread; off by default; <c>pg perf on|off</c>).</summary>
    internal static bool On { get => Patches.On; set => Patches.On = value; }

    /// <summary>Bumped by <c>pg perf reset</c> (frame thread); each tick system empties its windows on its next serial step when it changed.</summary>
    internal static int ResetSerial => Volatile.Read(ref resetSerial);
    static int resetSerial;

    /// <summary><c>pg perf reset</c> (frame thread): the patch counters now, the tick windows on the next serial step (sim thread only).</summary>
    internal static void Reset()
    {
        Patches.Reset();
        Interlocked.Increment(ref resetSerial);
    }

    /// <summary>A patch body measured into <paramref name="slot"/> (no allocation; off: one volatile read).</summary>
    internal static PatchCostCounters.Scope Measure(int slot) => Patches.Measure(slot);

    /// <summary>The start of a measured tick step: 0 while off, else the timestamp.</summary>
    internal static long Begin() => Patches.Begin();

    /// <summary>Timestamp ticks since <paramref name="start"/>.</summary>
    internal static long Since(long start) => System.Diagnostics.Stopwatch.GetTimestamp() - start;

    // task 9.2b: while the instrument is on, the time spent writing the test-only lines (TestLine) and the lines of the three log files
    // (PgLogSink.Append), process-wide; a SlowWorld spike line reports how much of it fell in that tick
    static long testLineTicks, fileLineTicks;
    internal static long TestLineTicks => Interlocked.Read(ref testLineTicks);
    internal static long FileLineTicks => Interlocked.Read(ref fileLineTicks);

    /// <summary>A TEST ONLY line (written while the test verbs are armed) to Meitou's log, its time counted while the instrument is on.</summary>
    internal static void TestLine(string line)
    {
        long start = Begin();
        ProvingGroundsMod.Ctx?.Log(line);
        if (start != 0) Interlocked.Add(ref testLineTicks, Since(start));
    }

    /// <summary>A log-file write (PgLogSink.Append) began at <paramref name="start"/> (0: the instrument was off).</summary>
    internal static void FileLineDone(long start)
    {
        if (start != 0) Interlocked.Add(ref fileLineTicks, Since(start));
    }
}

/// <summary>The tick costs the sim thread publishes (task 9.2a): whether the instrument was on at the publish, and the SlowWorld and Inputs
/// windows (last 600 ticks each). Immutable.</summary>
sealed record PgPerfStats(bool On, TickCostStats Tick, TickCostStats Inputs);
