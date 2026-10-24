using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Meitou.Game;

/// <summary>
/// Compiles the Meitou assemblies' methods ahead of their first call, on three background threads below normal priority from the start of the game (docs/bench.md
/// "Play benchmark"). The game runs without tiering (DECISIONS 19): a method is compiled once, fully optimised, when first called, and in play that landed in
/// frames (the AI's first act, a screen's first binding, 20-80 ms each). Compiled here the code is the same, and the frame finds it ready. Generic methods and
/// methods of generic types are left out (their instantiations are only known when used); so is code already compiled. <c>--no-precompile</c> turns it off.
/// </summary>
static class JitWarmup
{
    /// <summary>Threads compiling: the first frames call the rendering code within seconds of the start, and one thread took 7-8 s for all of it.</summary>
    const int Threads = 3;

    // The order the game first calls them in: the simulation (the world ticks during the loading), the rendering (the first frames), the host, then the rest.
    static readonly string[] Order = ["Meitou.Simulation", "Meitou.Rendering", "MeitouClient"];

    static readonly ConcurrentQueue<MethodBase> queue = new();
    static readonly Stopwatch watch = new();
    static int compiled, failed, running;

    public static void Start() => Spawn(List);

    static void Spawn(ThreadStart body) => new Thread(body) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "jit warm-up" }.Start();

    // Nothing may leave these threads: an exception unhandled on one would end the game. A method that cannot be compiled ahead (an assembly its signature needs is
    // missing on this platform, say) is counted and left to compile on its first call, if it ever has one.
    static void List()
    {
        watch.Start();
        try { Enqueue(); }
        catch (Exception e) { Console.Error.WriteLine($"warning   the jit warm-up stopped listing: {e.GetType().Name}: {e.Message}"); }
        running = Threads;
        for (int t = 1; t < Threads; t++) Spawn(Work);
        Work();
    }

    static void Enqueue()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var assemblies = typeof(JitWarmup).Assembly.GetReferencedAssemblies().Where(a => a.Name?.StartsWith("Meitou", StringComparison.Ordinal) == true)
            .Select(TryLoad).OfType<Assembly>().Prepend(typeof(JitWarmup).Assembly)
            .OrderBy(a => Array.IndexOf(Order, a.GetName().Name) is var i and >= 0 ? i : Order.Length).ToList();
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = [.. e.Types.OfType<Type>()]; }
            foreach (var type in types)
            {
                if (type.ContainsGenericParameters) continue;
                MethodBase[] methods;
                try { methods = [.. type.GetMethods(all), .. type.GetConstructors(all)]; }
                catch (Exception) { Interlocked.Increment(ref failed); continue; }
                foreach (var method in methods)
                    if (!method.IsAbstract && !method.ContainsGenericParameters && (method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0
                        && (method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                        queue.Enqueue(method);
            }
        }
    }

    static void Work()
    {
        try
        {
            while (queue.TryDequeue(out var method))
            {
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    Interlocked.Increment(ref compiled);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failed);
                }
            }
            if (Interlocked.Decrement(ref running) == 0)
                Console.WriteLine($"jit       {compiled} methods compiled ahead in {watch.Elapsed.TotalSeconds:0.0} s{(failed > 0 ? $" ({failed} could not be)" : "")}");
        }
        catch (Exception e) { Console.Error.WriteLine($"warning   a jit warm-up thread stopped: {e.GetType().Name}: {e.Message}"); }
    }

    static Assembly? TryLoad(AssemblyName name)
    {
        try { return Assembly.Load(name); }
        catch (Exception) { return null; }
    }
}
