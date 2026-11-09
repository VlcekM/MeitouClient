using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Meitou.Game.Host;

/// <summary>
/// Compiles the Meitou assemblies' methods ahead of their first call, on three background threads below normal priority from the start of the game
/// (docs/bench.md "Play benchmark"). The game runs without tiering (DECISIONS 19): a method is compiled once, fully optimised, when first called, and
/// in play that landed in frames (the AI's first act, a screen's first binding, 20-80 ms each). Compiled here the code is the same, and the frame
/// finds it ready. Generic methods and methods of generic types are left out (their instantiations are only known when used); so is code already
/// compiled. <c>--no-precompile</c> turns it off.
/// </summary>
static class JitWarmup
{
    /// <summary>
    /// Threads compiling: the first frames call the rendering code within seconds of the start, and one thread took 7-8 s for all of it.
    /// </summary>
    const int Threads = 3;

    /// <summary>
    /// The order the game first calls them in: the simulation (the world ticks during the loading), the game's rendering (the first frames), the
    /// host, then the rest. Assemblies not named come after them.
    /// </summary>
    static readonly string[] Order = ["Meitou.Simulation", "MeitouClient"];

    /// <summary>
    /// The game's renderers: compiled before the rest of the game's code, as the first frames call them.
    /// </summary>
    const string RenderingNamespace = "Meitou.Game.Rendering";

    /// <summary>
    /// The methods listed and not yet compiled, taken by the compiling threads.
    /// </summary>
    static readonly ConcurrentQueue<MethodBase> Queue = new();
    /// <summary>
    /// Times the warm-up from the listing to the last thread's end.
    /// </summary>
    static readonly Stopwatch Watch = new();
    /// <summary>
    /// Methods compiled, methods that could not be, and compiling threads still running.
    /// </summary>
    static int s_compiled, s_failed, s_running;

    /// <summary>
    /// Starts the warm-up: one thread lists the methods, then it and two more compile them (unless <c>--no-precompile</c>, the caller's check).
    /// </summary>
    public static void Start() => Spawn(List);

    /// <summary>
    /// Starts a background thread below normal priority.
    /// </summary>
    static void Spawn(ThreadStart body) =>
        new Thread(body) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "jit warm-up" }.Start();

    /// <summary>
    /// Lists the methods, then compiles with the other threads. Nothing may leave these threads: an exception unhandled on one would end the game. A
    /// method that cannot be compiled ahead (an assembly its signature needs is missing on this platform, say) is counted and left to compile on its
    /// first call, if it ever has one.
    /// </summary>
    static void List()
    {
        Watch.Start();
        try
        {
            Enqueue();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"warning   the jit warm-up stopped listing: {e.GetType().Name}: {e.Message}");
        }
        s_running = Threads;
        for (int t = 1; t < Threads; t++) Spawn(Work);
        Work();
    }

    /// <summary>
    /// Queues every non-generic method and constructor of the Meitou assemblies, the simulation's first, then the game's (its renderers first), then
    /// the rest.
    /// </summary>
    static void Enqueue()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var assemblies = typeof(JitWarmup).Assembly.GetReferencedAssemblies()
            .Where(a => a.Name?.StartsWith("Meitou", StringComparison.Ordinal) == true)
            .Select(TryLoad)
            .OfType<Assembly>()
            .Prepend(typeof(JitWarmup).Assembly)
            .OrderBy(a => Array.IndexOf(Order, a.GetName().Name) is var i and >= 0 ? i : Order.Length)
            .ToList();
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = [.. e.Types.OfType<Type>()];
            }

            types = [.. types.OrderBy(t => t.Namespace?.StartsWith(RenderingNamespace, StringComparison.Ordinal) == true ? 0 : 1)];
            foreach (var type in types)
            {
                if (type.ContainsGenericParameters) continue;
                MethodBase[] methods;
                try
                {
                    methods = [.. type.GetMethods(all), .. type.GetConstructors(all)];
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref s_failed);
                    continue;
                }
                foreach (var method in methods)
                    if (!method.IsAbstract && !method.ContainsGenericParameters
                        && (method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0
                        && (method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                        Queue.Enqueue(method);
            }
        }
    }

    /// <summary>
    /// Compiles queued methods until the queue is empty; the last thread to finish logs the count.
    /// </summary>
    static void Work()
    {
        try
        {
            while (Queue.TryDequeue(out var method))
            {
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    Interlocked.Increment(ref s_compiled);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref s_failed);
                }
            }
            if (Interlocked.Decrement(ref s_running) == 0)
            {
                string failed = s_failed > 0 ? $" ({s_failed} could not be)" : "";
                Console.WriteLine($"jit       {s_compiled} methods compiled ahead in {Watch.Elapsed.TotalSeconds:0.0} s{failed}");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"warning   a jit warm-up thread stopped: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Loads a referenced assembly; null when it cannot be.
    /// </summary>
    static Assembly? TryLoad(AssemblyName name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}