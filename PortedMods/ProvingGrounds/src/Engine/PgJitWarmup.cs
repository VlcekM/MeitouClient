using System.Diagnostics;
using System.Reflection;
using ProvingGrounds.Core.Glue;
using System.Runtime.CompilerServices;

namespace ProvingGrounds.Engine;

/// <summary>
/// Task 9.2b (port only; the C++ is native code, compiled before it runs): compiles this mod's methods ahead of their first call, on one background
/// thread below normal priority, started by the first <see cref="ProvingGroundsSystem"/> built in the process (the AddSystem factory). Meitou runs
/// without tiered compilation (src/Meitou.Game/Meitou.Game.csproj: TieredCompilation false, "methods are compiled once, fully optimised"), so a
/// method is compiled on its first call, and in play that landed in the sim thread's tick: the 9.2b measurement (tools/scenarios/perf.sh) found
/// 23 to 97 ms ticks at a town bout's first planning, staging, fight start and result, 80 to 100 % of each in this thread's JIT time, and none from
/// a garbage collection. Meitou compiles its own assemblies ahead the same way (src/Meitou.Game/Host/JitWarmup.cs at v0.17.3), and only once the
/// code mods are initialized (src/Meitou.Game/Mods/GameMods.cs:20-58: a method compiled before a Harmony patch keeps any inlined copy of the
/// target); the factory runs after every mod's Initialize, so every patch is in place before this starts. Meitou's <c>--no-precompile</c> turns
/// it off too.
/// <para>What changes: <see cref="RuntimeHelpers.PrepareMethod(RuntimeMethodHandle)"/> compiles a method without calling it, but compiling may run
/// the type initializer of a type the method reads (a type without an explicit static constructor may be initialized at any time before its first
/// static access), so such an initializer can run earlier and on this thread; the mod's are allocations and reflection lookups that read no
/// changing state. Type initializers themselves are never listed (<see cref="JitWarmupList"/>). And a first call made while this thread is
/// compiling the same method waits for that compile instead of compiling it itself (no longer than the compile it would have done).</para>
/// <para>Listed: every non-generic method and constructor of the two assemblies (ProvingGroundsMeitou, ProvingGrounds.Core), and the methods of the
/// closed generic types of those assemblies the mod's own fields, base types and interfaces name (TownArenaLogic&lt;CharacterId&gt;, and so on; their
/// nested types closed the same way). Generic methods and other instantiations are left to their first call, as Meitou leaves its own.</para>
/// <para>A method that cannot be compiled ahead is counted and left to its first call. Nothing may leave the thread: an exception unhandled on it would
/// end the game. One line in Meitou's log when it is done.</para>
/// </summary>
static class PgJitWarmup
{
    static int started;

    /// <summary>Starts the warm-up once per process (a later world's systems do nothing).</summary>
    internal static void Start(Action<string> log)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        if (Environment.GetCommandLineArgs().Contains("--no-precompile", StringComparer.Ordinal)) { log("jit warm-up off (--no-precompile)"); return; }
        new Thread(() => Run(log)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Proving Grounds jit warm-up" }.Start();
    }

    static void Run(Action<string> log)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            int compiled = 0, failed = 0;
            foreach (var (method, instantiation) in JitWarmupList.Methods([typeof(PgJitWarmup).Assembly, typeof(Core.PGLog).Assembly]))
                if (Prepare(method, instantiation)) compiled++; else failed++;
            log($"jit warm-up: {compiled} methods compiled ahead in {Core.CMath.FormatF(watch.Elapsed.TotalSeconds, 1)} s"
                + (failed > 0 ? $" ({failed} could not be)" : ""));
        }
        catch (Exception e)
        {
            try { log($"jit warm-up stopped: {e.GetType().Name}: {e.Message}"); } catch { }
        }
    }

    static bool Prepare(MethodBase m, RuntimeTypeHandle[]? instantiation)
    {
        try
        {
            if (instantiation is null) RuntimeHelpers.PrepareMethod(m.MethodHandle);
            else RuntimeHelpers.PrepareMethod(m.MethodHandle, instantiation);
            return true;
        }
        catch (Exception) { return false; }
    }
}
