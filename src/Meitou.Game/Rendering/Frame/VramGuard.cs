using System.Diagnostics;
using System.Globalization;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Keeps the device-local memory in use under the driver's budget (VK_EXT_memory_budget) when the draw ranges ask for more than the card has.
/// Past that point the driver fails allocations or resets the device (a TDR): the world's streaming has no other limit than the ranges.
/// <list type="bullet">
/// <item>It samples the memory in use of the budget a few times a second (<see cref="Tick"/>). At <see cref="High"/> (90%) it enters
/// <see cref="Pressure"/>: <see cref="Streaming"/> goes false (no new zone layouts, grass pages, textures or mesh uploads are started, but for the
/// zones and meshes within <see cref="NearReach"/> of the camera), the caches evict what has been idle for a couple of seconds instead of a minute,
/// and <see cref="RangeScale"/> falls by about 12% every 0.75 s (down to <see cref="MinScale"/>) while the use stays high: every renderer multiplies
/// the ranges it draws and streams to by it. The farthest things leave first because the ranges they were in end first.</item>
/// <item>Pressure ends under <see cref="Low"/> (80%); streaming resumes. The ranges come back, 4% a second, only once the use has stayed
/// under <see cref="Recover"/> (74%) for four seconds: the band between is where the system rests, so it does not swing.</item>
/// <item>Allocations that can be large call <see cref="Allows"/> first (a texture's image, a bigger scratch buffer or instance arena): it asks
/// for a fresh sample and refuses what would take the use past <see cref="Ceiling"/> (92%), counting what was reserved since the sample, and a
/// refusal puts the guard into pressure at once. Streaming moves a gigabyte in a quarter of a second at its fastest, so the sampling alone
/// would be too slow.</item>
/// <item>At the default settings the use is a third of an 11 GB card's budget: the guard never leaves its idle state there
/// (<see cref="RangeScale"/> 1 multiplies exactly, so pictures are unchanged).</item>
/// </list>
/// Render thread only (<see cref="Tick"/>, <see cref="Allows"/>); the properties may be read from the job threads.
/// </summary>
public sealed class VramGuard
{
    /// <summary>
    /// The use fractions of the budget: pressure starts at <see cref="High"/> and ends under <see cref="Low"/>, the ranges return only under <see
    /// cref="Recover"/>, and <see cref="Allows"/> refuses past <see cref="Ceiling"/>.
    /// </summary>
    public const double High = 0.90, Low = 0.80, Recover = 0.74, Ceiling = 0.92;

    /// <summary>
    /// Seconds between samples, between two steps down, and between two steps up; and the calm needed before the ranges return.
    /// </summary>
    public const double SampleSeconds = 0.25, StepDownSeconds = 0.75, StepUpSeconds = 1.0, CalmSeconds = 4.0;

    /// <summary>
    /// The newest a sample must be for <see cref="Allows"/> to use it.
    /// </summary>
    const double FreshSeconds = 0.02;

    /// <summary>
    /// The range factor's step down (about 12%), its step up (4%) and its floor.
    /// </summary>
    public const float StepDown = 0.88f, StepUp = 1.04f, MinScale = 0.15f;

    /// <summary>
    /// How far round the camera zones are still laid out and object meshes still decoded under <see cref="Pressure"/> (world units): a card that
    /// never gets back under <see cref="Low"/> would otherwise never load the town the player stands in (#336). The range clamp's floor
    /// (<see cref="MinScale"/> of the default object distance) is about this.
    /// </summary>
    public const float NearReach = 3000;

    /// <summary>
    /// The most the per-frame scratch may take the use to (<see cref="Allows(ulong)"/> stops at <see cref="Ceiling"/>).
    /// </summary>
    public const double PriorityCeiling = 0.945;

    /// <summary>
    /// Seconds between two squeezes (a refused scratch asks for a shorter range every frame it is refused; this paces them).
    /// </summary>
    const double SqueezeSeconds = 0.05;

    /// <summary>
    /// Reads the device-local memory in use and the budget.
    /// </summary>
    readonly Func<(ulong Used, ulong Budget)> _sample;

    /// <summary>
    /// The time in seconds, monotonic.
    /// </summary>
    readonly Func<double> _clock;

    /// <summary>
    /// Where the one log line goes.
    /// </summary>
    readonly Action<string>? _log;

    /// <summary>
    /// When the last sample and the last range step were taken, and since when the use has stayed under <see cref="Recover"/> (seconds).
    /// </summary>
    double _lastSample = double.NegativeInfinity, _lastStep, _calmSince;

    /// <summary>
    /// Whether the guard is under pressure (<see cref="Pressure"/>); read from the job threads.
    /// </summary>
    volatile bool _pressure;

    /// <summary>
    /// The range factor (<see cref="RangeScale"/>).
    /// </summary>
    float _scale = 1f;

    /// <summary>
    /// The last sample's use and budget, and the bytes <see cref="Allows"/> granted since it.
    /// </summary>
    long _used, _budget, _reserved;

    /// <summary>
    /// Whether the one log line was written.
    /// </summary>
    bool _logged;

    /// <summary>
    /// When the last squeeze was (seconds).
    /// </summary>
    double _lastSqueeze = double.NegativeInfinity;

    /// <summary>
    /// The allocator's refused allocations as the last step saw them.
    /// </summary>
    int _seenFailures;

    /// <summary>
    /// A guard that samples the video memory with <paramref name="sample"/>, idle until the use runs high.
    /// </summary>
    /// <param name="sample">The device-local memory in use and the budget (<c>VulkanDevice.VideoMemory</c>).</param>
    /// <param name="clock">Seconds, monotonic (a stopwatch by default; tests drive it).</param>
    /// <param name="log">Where the one log line goes (the console by default).</param>
    public VramGuard(Func<(ulong Used, ulong Budget)> sample, Func<double>? clock = null, Action<string>? log = null)
    {
        this._sample = sample;
        var watch = Stopwatch.StartNew();
        this._clock = clock ?? (() => watch.Elapsed.TotalSeconds);
        this._log = log ?? Console.WriteLine;
    }

    /// <summary>
    /// The allocator made lean while under pressure (<see cref="Gpu.Core.GpuAllocator.Lean"/>): its empty blocks and spares, room kept to grow
    /// into, go back to the driver first, before anything drawn is evicted. Null: none (tests).
    /// </summary>
    public Gpu.Core.GpuAllocator? Allocator { get; init; }

    /// <summary>
    /// The factor every draw and streaming range is multiplied by (1: no clamp).
    /// </summary>
    public float RangeScale => _scale;

    /// <summary>
    /// The memory use is high: the caches evict what they can, and nothing new is started.
    /// </summary>
    public bool Pressure => _pressure;

    /// <summary>
    /// New zones, pages, textures and uploads may be started (false under <see cref="Pressure"/>).
    /// </summary>
    public bool Streaming => !_pressure;

    /// <summary>
    /// Whether what lies within <see cref="NearReach"/> may start loading: always, but under pressure only while the use is under <see
    /// cref="PriorityCeiling"/>.
    /// </summary>
    public bool StreamsNear => !_pressure || Fraction < PriorityCeiling;

    /// <summary>
    /// The last sample, as a fraction of the budget.
    /// </summary>
    public double Fraction => _budget == 0 ? 0 : (double)_used / _budget;

    /// <summary>
    /// The driver's device-local budget at the last sample (0 until one is taken, or when unknown).
    /// </summary>
    public long BudgetBytes => _budget;

    /// <summary>
    /// Bytes in use above <see cref="Low"/> of the budget while under pressure (what is to be shed), else 0.
    /// </summary>
    public long ExcessBytes => _pressure ? Math.Max(_used - (long)(Low * _budget), 0) : 0;

    /// <summary>
    /// Times the guard entered pressure, the lowest <see cref="RangeScale"/> it reached, and allocations <see cref="Allows"/> refused.
    /// </summary>
    public int Activations { get; private set; }

    /// <summary>
    /// The lowest <see cref="RangeScale"/> reached.
    /// </summary>
    public float LowestScale { get; private set; } = 1f;

    /// <summary>
    /// Allocations <see cref="Allows"/> refused.
    /// </summary>
    public int Refusals { get; private set; }

    /// <summary>
    /// The smallest and largest driver budgets sampled (0 before the first): the driver's budget shrinks while other programs hold video memory.
    /// </summary>
    public long LowestBudget { get; private set; }

    /// <summary>
    /// The largest driver budget sampled (0 before the first).
    /// </summary>
    public long HighestBudget { get; private set; }

    /// <summary>
    /// Whether the guard is doing anything (pressure, or ranges not back at 1).
    /// </summary>
    public bool Active => _pressure || _scale < 1f;

    /// <summary>
    /// The statistics line (F11): idle, or the clamp and what is paused.
    /// </summary>
    public string Status =>
        Active ? $"guard: ranges x{_scale:0.00}{(_pressure ? ", streaming paused" : ", recovering")}, "
                + $"{Fraction * 100:0}% of the {_budget / 1048576} MB budget{(Refusals > 0 ? $", {Refusals} allocations refused" : "")}"
                + $"{(PriorityGrants > 0 ? $", {PriorityGrants} scratch grants over the ceiling" : "")}"
               : $"guard: ok, {Fraction * 100:0}% of the {_budget / 1048576} MB budget"
                + $"{(LowestBudget < HighestBudget ? $" (sampled {LowestBudget / 1048576} to {HighestBudget / 1048576} MB)" : "")}";

    /// <summary>
    /// Once a frame: takes a sample when one is due and moves the state.
    /// </summary>
    public void Tick()
    {
        double now = _clock();
        if (now - _lastSample >= SampleSeconds) Sample(now);
    }

    /// <summary>
    /// Takes a sample now (forgetting what was reserved since the last), widens the budget's range seen, and moves the state.
    /// </summary>
    void Sample(double now)
    {
        _lastSample = now;
        var (u, b) = _sample();
        (_used, _budget, _reserved) = ((long)u, (long)b, 0);
        if (_budget > 0)
            (LowestBudget, HighestBudget) = (LowestBudget == 0 ? _budget : Math.Min(LowestBudget, _budget), Math.Max(HighestBudget, _budget));
        Step(now);
    }

    /// <summary>
    /// Whether <paramref name="bytes"/> more device-local memory may be taken: the use (freshly sampled, plus what earlier calls reserved since)
    /// stays under <see cref="Ceiling"/> of the budget. A refusal puts the guard into pressure now. An unknown budget allows everything.
    /// </summary>
    public bool Allows(ulong bytes) => Allows(bytes, 0);

    /// <summary>
    /// <see cref="Allows(ulong)"/> with <paramref name="pending"/> bytes the caller knows are on their way but are not in the memory in use yet (they
    /// count against the room without being reserved again).
    /// </summary>
    public bool Allows(ulong bytes, ulong pending)
    {
        double now = _clock();
        if (now - _lastSample >= FreshSeconds) Sample(now);
        if (_budget <= 0) return true;
        long wanted = _used + _reserved + (long)pending + (long)bytes;
        if (wanted <= Ceiling * _budget)
        {
            _reserved += (long)bytes;
            return true;
        }
        Refusals++;
        if (!_pressure) Enter(now, (double)wanted / _budget);
        return false;
    }

    /// <summary>
    /// Whether a lean texture (a small image of something drawn now, <see cref="WorldTextureCache.LeanSize"/>) may be loaded, under pressure too: the
    /// use with <paramref name="pending"/> stays under <see cref="PriorityCeiling"/>. Never enters pressure; reserves what it grants.
    /// </summary>
    public bool AllowsLean(ulong bytes, ulong pending)
    {
        double now = _clock();
        if (now - _lastSample >= FreshSeconds) Sample(now);
        if (_budget <= 0) return true;
        if (_used + _reserved + (long)pending + (long)bytes > PriorityCeiling * _budget) return false;
        _reserved += (long)bytes;
        LeanGrants++;
        return true;
    }

    /// <summary>
    /// Lean texture loads granted (<see cref="AllowsLean"/>).
    /// </summary>
    public int LeanGrants { get; private set; }

    /// <summary>
    /// Scratch allocations granted above <see cref="Ceiling"/> (the use then is the scratch's, not the caches').
    /// </summary>
    public int PriorityGrants { get; private set; }

    /// <summary>
    /// <see cref="Allows(ulong)"/> for what a frame cannot be drawn without (the foliage cull's and the grass kernels' per-frame scratch: tens of
    /// MB, against caches of gigabytes): refused only past <see cref="PriorityCeiling"/>, so the streaming and the caches give way first. A grant
    /// above <see cref="Ceiling"/> enters pressure (the caches evict and the ranges fall, which lowers the next frames' need).
    /// </summary>
    public bool AllowsPriority(ulong bytes)
    {
        double now = _clock();
        if (now - _lastSample >= FreshSeconds) Sample(now);
        if (_budget <= 0) return true;
        long wanted = _used + _reserved + (long)bytes;
        if (wanted > PriorityCeiling * _budget)
        {
            Refusals++;
            if (!_pressure) Enter(now, (double)wanted / _budget);
            Squeeze(now);
            return false;
        }
        _reserved += (long)bytes;
        if (wanted > Ceiling * _budget)
        {
            PriorityGrants++;
            if (!_pressure) Enter(now, (double)wanted / _budget);
        }
        return true;
    }

    /// <summary>
    /// A frame's scratch was refused: the ranges that make it fall one step now instead of waiting for the next regular step (0.75 s), so a view
    /// is shortened within a few frames rather than left out for seconds. Only under pressure; the regular steps and the recovery go on as before.
    /// </summary>
    void Squeeze(double now)
    {
        if (!_pressure || now - _lastSqueeze < SqueezeSeconds) return;
        _lastSqueeze = _lastStep = now;
        _scale = Math.Max(MinScale, _scale * StepDown);
        LowestScale = Math.Min(LowestScale, _scale);
        Squeezes++;
    }

    /// <summary>
    /// Range steps taken early because a scratch was refused.
    /// </summary>
    public int Squeezes { get; private set; }

    /// <summary>
    /// Enters pressure: lean allocator, the first range step due at once, and the log line the first time.
    /// </summary>
    void Enter(double now, double fraction)
    {
        _pressure = true;
        Activations++;
        if (Allocator is { } a) a.Lean = true;
        _lastStep = now - StepDownSeconds;   // the first step at once
        if (_logged) return;
        _logged = true;
        _log?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"vram      guard: device-local memory at {fraction * 100:0}% of the {_budget / 1048576} MB budget: new streaming paused " +
            $"(what is drawn loads small), idle textures and meshes evicted, draw and streaming ranges clamped (x{_scale * StepDown:0.00} " +
            $"and falling while over {Low * 100:0}%); the statistics show it, this line is not repeated"));
    }

    /// <summary>
    /// Moves the state after a sample: enters pressure at <see cref="High"/> or on a refused allocation, leaves it under <see cref="Low"/>, steps the
    /// ranges down under pressure and back up after the calm.
    /// </summary>
    void Step(double now)
    {
        if (_budget <= 0) return;
        double f = Fraction;

        // An allocation the driver refused since the last sample: pressure, whatever the use reads.
        int failures = Allocator?.Failures ?? 0;
        bool failed = failures != _seenFailures;
        _seenFailures = failures;
        if (!_pressure && (f >= High || failed)) Enter(now, f);
        else if (_pressure && f < Low)
        {
            _pressure = false;
            if (Allocator is { } a) a.Lean = false;
            _calmSince = now;
            _lastStep = now;
        }
        if (_pressure)
        {
            if (now - _lastStep >= StepDownSeconds)
            {
                _scale = Math.Max(MinScale, _scale * StepDown);
                LowestScale = Math.Min(LowestScale, _scale);
                _lastStep = now;
            }
        }
        else if (_scale < 1f)
        {
            if (f >= Recover) _calmSince = now;
            else if (now - _calmSince >= CalmSeconds && now - _lastStep >= StepUpSeconds)
            {
                _scale = Math.Min(1f, _scale * StepUp);
                _lastStep = now;
            }
        }
    }
}