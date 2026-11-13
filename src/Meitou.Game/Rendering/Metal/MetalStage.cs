using System.Diagnostics;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// One stage's MSL as a Metal library and its entry point, compiled by a worker as soon as the program exists. Metal compiles MSL source when the
/// library is made: with the system's shader cache warm that takes a fraction of a millisecond, but the first run of a shader text takes tens to
/// hundreds of milliseconds per library (about 14 s for the 200 libraries a play session makes), and compiling them one after another on the render
/// thread stalls the load and, for a program a renderer makes late, a frame. The workers compile in parallel while the program is being made and the
/// world loads; <see cref="Function"/> takes the library when it is first needed, compiling it on the caller's thread when no worker has started
/// it, so waiting for it is never longer than compiling it would have been.
/// </summary>
internal sealed class MetalStage : IDisposable
{
    #region Compile

    /// <summary>
    /// The device the library is made on.
    /// </summary>
    readonly MTLDevice _device;
    /// <summary>
    /// The MSL source, the entry point and the program's name (for the messages).
    /// </summary>
    readonly string _msl, _entry, _name;
    /// <summary>
    /// The fast-math override of the compile options (<c>MEITOU_METAL_FAST_MATH</c>); null leaves Metal's default.
    /// </summary>
    readonly bool? _fastMath;
    /// <summary>
    /// Set by whoever takes the compile (a worker or the first caller of <see cref="Function"/>): 0 until then.
    /// </summary>
    int _claimed;
    /// <summary>
    /// Set once the compile has finished, with a result or an error.
    /// </summary>
    readonly ManualResetEventSlim _done = new();
    /// <summary>
    /// The library the compile made.
    /// </summary>
    MTLLibrary _library;
    /// <summary>
    /// The entry point taken from <see cref="_library"/>.
    /// </summary>
    MTLFunction _function;
    /// <summary>
    /// What the compile failed with; null when it succeeded or has not finished.
    /// </summary>
    Exception? _error;
    /// <summary>
    /// <see cref="CompileMilliseconds"/> in Stopwatch ticks.
    /// </summary>
    static long s_compileTicks;

    /// <summary>
    /// The time the compiles have taken so far in milliseconds, summed over all threads.
    /// </summary>
    public static double CompileMilliseconds => Interlocked.Read(ref s_compileTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// A stage of the program <paramref name="name"/> whose MSL is <paramref name="msl"/> and entry point <paramref name="entry"/>, handed to the
    /// workers.
    /// </summary>
    public MetalStage(MTLDevice device, string msl, string entry, string name, bool? fastMath)
    {
        (_device, _msl, _entry, _name, _fastMath) = (device, msl, entry, name, fastMath);
        MetalStageCompiler.Enqueue(this);
    }

    /// <summary>
    /// The stage's entry point, once compiled; throws what the compile ended with (Metal's error, or a missing entry point).
    /// </summary>
    public MTLFunction Function
    {
        get
        {
            Compile();
            _done.Wait();
            return _error is { } error ? throw error : _function;
        }
    }

    /// <summary>
    /// Compiles the library and takes its entry point, unless another thread has taken the work (then this returns at once).
    /// </summary>
    internal void Compile()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0) return;

        long started = Stopwatch.GetTimestamp();
        try
        {
            using var pool = new NSAutoreleasePool();
            Make();
        }
        catch (Exception e)
        {
            _error = e;
        }
        finally
        {
            long ticks = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref s_compileTicks, ticks);
            if (MetalPipelines.Trace)
                Console.WriteLine($"metal     library {_name} compiled in {ticks * 1000.0 / Stopwatch.Frequency:0.0} ms ({_msl.Length / 1024} KB of MSL)");
            _done.Set();
        }
    }

    /// <summary>
    /// The library and its entry point.
    /// </summary>
    void Make()
    {
        NSError error = default;
        var options = new MTLCompileOptions();
        if (_fastMath is { } fast) options.FastMathEnabled = fast;

        _library = _device.NewLibrary((NSString)_msl, options, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"{_name}: the MSL did not compile: {error.LocalizedDescription}");

        _function = _library.NewFunction((NSString)_entry);
        if (_function.NativePtr == IntPtr.Zero) throw new InvalidOperationException($"{_name}: no function {_entry} in the MSL");
    }

    /// <summary>
    /// Releases the entry point and the library (after the compile, when one is still going).
    /// </summary>
    public void Dispose()
    {
        Compile();
        _done.Wait();
        if (_function.NativePtr != IntPtr.Zero) _function.Dispose();
        if (_library.NativePtr != IntPtr.Zero) _library.Dispose();
        _function = default;
        _library = default;
    }

    #endregion
}

/// <summary>
/// The threads that compile <see cref="MetalStage"/>s in the background: a few, so the compiles run in parallel without taking every core from the
/// loading work, and not the thread pool's, whose threads the compiles would hold while they wait for Metal.
/// </summary>
internal static class MetalStageCompiler
{
    /// <summary>
    /// The stages waiting for a worker, in the order the programs were made.
    /// </summary>
    static readonly System.Collections.Concurrent.BlockingCollection<MetalStage> Queue = [];
    /// <summary>
    /// Starts the workers on the first stage.
    /// </summary>
    static readonly object Start = new();
    /// <summary>
    /// Whether the workers are running.
    /// </summary>
    static bool s_started;

    /// <summary>
    /// Queues <paramref name="stage"/> for a worker.
    /// </summary>
    public static void Enqueue(MetalStage stage)
    {
        EnsureWorkers();
        Queue.Add(stage);
    }

    /// <summary>
    /// Starts the workers: half the cores, from two to six.
    /// </summary>
    static void EnsureWorkers()
    {
        lock (Start)
        {
            if (s_started) return;

            s_started = true;
            int count = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
            for (int i = 0; i < count; i++)
            {
                new Thread(Work) { IsBackground = true, Name = $"metal-compile-{i}" }.Start();
            }
        }
    }

    /// <summary>
    /// A worker: compiles the stages as they come.
    /// </summary>
    static void Work()
    {
        foreach (var stage in Queue.GetConsumingEnumerable()) stage.Compile();
    }
}
