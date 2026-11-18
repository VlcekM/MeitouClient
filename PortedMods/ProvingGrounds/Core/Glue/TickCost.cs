using System.Diagnostics;

namespace ProvingGrounds.Core.Glue;

// Task 9.2a (M9-release.md task 9.2): the cost-per-tick instrument. Not a port: the original had no instrument. Ships in the Release build,
// off by default (the live runs and the owner play the Release DLL; a debug build's numbers would not be the cost the player pays). Off = one
// volatile bool read per measured site; on = Stopwatch.GetTimestamp() at start and end, no allocation. Turned on by the test-only
// `pg perf on` (src/Engine/PgConsole.cs); read by `pg perf` and, while on, by the F10 row "Proving Grounds: status".

/// <summary>Average and maximum of a window of durations, in milliseconds, and how many durations it holds (an immutable value: the sim thread
/// publishes it in the snapshot).</summary>
public readonly record struct TickCostStats(double AvgMs, double MaxMs, int Count)
{
    /// <summary>The F10 status row's piece: <c>tick avg &lt;x&gt; ms max &lt;y&gt; ms (&lt;n&gt;)</c>, numbers through CMath.FormatF (3 decimals;
    /// rule "Numbers to text").</summary>
    public string RowText() => "tick avg " + CMath.FormatF(AvgMs, 3) + " ms max " + CMath.FormatF(MaxMs, 3) + " ms ("
        + Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
}

/// <summary>
/// The last <see cref="Size"/> per-tick durations (Stopwatch ticks) in a ring: their average and maximum (fewer while filling). One thread
/// only (the sim thread): no locks. <see cref="Add"/> is O(1) and never allocates; <see cref="Stats"/> scans the ring for the maximum (600
/// longs, read at the 0.20 s publish), so a maximum that leaves the window is gone at once.
/// </summary>
public sealed class TickCostWindow
{
    /// <summary>The window: the last 600 ticks (M9 task 9.2).</summary>
    public const int Size = 600;

    readonly long[] ring = new long[Size];
    readonly long frequency;
    int next, count;
    long sum;

    /// <param name="frequency">Timestamp ticks per second (<see cref="Stopwatch.Frequency"/> in the game).</param>
    public TickCostWindow(long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        this.frequency = frequency;
    }

    public TickCostWindow() : this(Stopwatch.Frequency) { }

    /// <summary>Durations in the window (0..600).</summary>
    public int Count => count;

    /// <summary>One tick's duration, in timestamp ticks (negative is read as 0).</summary>
    public void Add(long elapsed)
    {
        if (elapsed < 0) elapsed = 0;
        if (count == Size) sum -= ring[next];
        else count++;
        ring[next] = elapsed;
        sum += elapsed;
        next = next == Size - 1 ? 0 : next + 1;
    }

    /// <summary>Empties the window.</summary>
    public void Reset()
    {
        Array.Clear(ring);
        next = count = 0;
        sum = 0;
    }

    /// <summary>Average and maximum over the window, in milliseconds; 0 / 0 / 0 when empty.</summary>
    public TickCostStats Stats()
    {
        if (count == 0) return new(0, 0, 0);
        long max = 0;
        for (int i = 0; i < count; i++) if (ring[i] > max) max = ring[i];   // the filled part is [0, count) until the ring wraps, then all of it
        return new(ToMs((double)sum / count), ToMs(max), count);
    }

    double ToMs(double t) => t * 1000.0 / frequency;
}

/// <summary>
/// Per-patch counters for the Harmony patch bodies: a fixed slot per patch (its index known at install time), each with its calls, total and
/// maximum duration. Patches run on parallel AI worker threads too: every update is an <see cref="Interlocked"/> operation (the maximum by a
/// CompareExchange loop), nothing allocates or locks. No window: numbers since the last <see cref="Reset"/>.
/// </summary>
public sealed class PatchCostCounters
{
    // one slot = calls, total, max (three longs, side by side)
    readonly long[] slots;
    readonly string[] names;
    readonly long frequency;
    volatile bool on;

    public PatchCostCounters(IReadOnlyList<string> names, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        this.names = [.. names];
        this.frequency = frequency;
        slots = new long[this.names.Length * 3];
    }

    public PatchCostCounters(IReadOnlyList<string> names) : this(names, Stopwatch.Frequency) { }

    /// <summary>Whether the patches are measured (off by default). Any thread.</summary>
    public bool On { get => on; set => on = value; }

    public int SlotCount => names.Length;

    public string Name(int slot) => names[slot];

    /// <summary>The start of a measured body: 0 while off (the one volatile read), else <see cref="Stopwatch.GetTimestamp"/> (never 0 in
    /// practice; a start of 0 is not counted).</summary>
    public long Begin() => on ? Stopwatch.GetTimestamp() : 0;

    /// <summary>The end of a measured body begun at <paramref name="start"/>: nothing when the body began while off.</summary>
    public void End(int slot, long start)
    {
        if (start != 0) Record(slot, Stopwatch.GetTimestamp() - start);
    }

    /// <summary>A measured body for <c>using var _ = counters.Measure(slot);</c> (a struct: no allocation; its Dispose is the finally of the body).</summary>
    public Scope Measure(int slot) => new(this, slot, Begin());

    /// <summary>One call of <paramref name="slot"/> that took <paramref name="elapsed"/> timestamp ticks (negative read as 0). Any thread. A
    /// slot outside the table is ignored (a patch never throws into the game).</summary>
    public void Record(int slot, long elapsed)
    {
        if ((uint)slot >= (uint)names.Length) return;
        if (elapsed < 0) elapsed = 0;
        int b = slot * 3;
        Interlocked.Increment(ref slots[b]);
        Interlocked.Add(ref slots[b + 1], elapsed);
        long seen = Volatile.Read(ref slots[b + 2]);
        while (elapsed > seen)
        {
            long was = Interlocked.CompareExchange(ref slots[b + 2], elapsed, seen);
            if (was == seen) break;
            seen = was;
        }
    }

    /// <summary>Zeroes every slot (any thread; a call running at that moment may leave part of itself counted).</summary>
    public void Reset()
    {
        for (int i = 0; i < slots.Length; i++) Interlocked.Exchange(ref slots[i], 0);
    }

    /// <summary>One slot's numbers: calls, total and maximum in milliseconds, average (0 with no calls).</summary>
    public (long Calls, double TotalMs, double AvgMs, double MaxMs) Read(int slot)
    {
        int b = slot * 3;
        long calls = Interlocked.Read(ref slots[b]), total = Interlocked.Read(ref slots[b + 1]), max = Interlocked.Read(ref slots[b + 2]);
        double totalMs = total * 1000.0 / frequency;
        return (calls, totalMs, calls > 0 ? totalMs / calls : 0, max * 1000.0 / frequency);
    }

    /// <summary>The measured body of <see cref="Measure"/>: Dispose ends it.</summary>
    public readonly ref struct Scope(PatchCostCounters counters, int slot, long start)
    {
        public void Dispose()
        {
            if (start != 0) counters.End(slot, start);
        }
    }
}

/// <summary>The JSON fields of the test-only <c>pg perf</c> answer (task 9.2a): numbers through <see cref="TestJson.F"/> (4 decimals, null for a
/// non-finite value).</summary>
public static class TickCostJson
{
    /// <summary><c>{"avgMs":..,"maxMs":..,"ticks":n}</c>.</summary>
    public static string Window(TickCostStats s) =>
        "{\"avgMs\":" + TestJson.F(s.AvgMs, 4) + ",\"maxMs\":" + TestJson.F(s.MaxMs, 4) + ",\"ticks\":"
        + s.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

    /// <summary>Every slot as <c>[{"patch":..,"calls":n,"totalMs":..,"avgMs":..,"maxMs":..},...]</c>, in slot order.</summary>
    public static string Patches(PatchCostCounters counters)
    {
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < counters.SlotCount; i++)
        {
            var (calls, total, avg, max) = counters.Read(i);
            if (i > 0) sb.Append(',');
            sb.Append("{\"patch\":").Append(PGLog.Quote(counters.Name(i))).Append(",\"calls\":")
                .Append(calls.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(",\"totalMs\":").Append(TestJson.F(total, 4)).Append(",\"avgMs\":").Append(TestJson.F(avg, 4))
                .Append(",\"maxMs\":").Append(TestJson.F(max, 4)).Append('}');
        }
        return sb.Append(']').ToString();
    }
}
