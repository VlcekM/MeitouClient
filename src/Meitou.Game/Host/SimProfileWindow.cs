using System.Globalization;
using Meitou.Game.Rendering.Profiling;
using Meitou.Simulation;
using Meitou.Simulation.Movement;

namespace Meitou.Game.Host;

/// <summary>
/// The simulation's profile once a frame: F12's sim row and its parts, and with --log-spikes the slow frames' systems and a line of the steady cost
/// every few seconds.
/// </summary>
sealed class SimProfileWindow(GameHost host)
{
    #region Frame parts

    // F12's sim parts: the names (the tick phases, the time outside the world's ticks, the world's systems), made again when the world is, and the
    // frame's ms.

    /// <summary>
    /// The world the names were made for.
    /// </summary>
    World? _simNamesFor;

    /// <summary>
    /// The parts' names: the phases, "outside ticks", the systems, the profile's parts and "tick ahead".
    /// </summary>
    string[] _simNames = [];

    /// <summary>
    /// The frame's ms of each part in <see cref="_simNames"/>.
    /// </summary>
    double[] _simMs = [];

    /// <summary>
    /// Hands the frame's <paramref name="advanceMs"/> to the profiler with the world's tick profile split into phases and systems, and clears the
    /// profile.
    /// </summary>
    internal void SimProfile(FrameProfiler profiler, double advanceMs)
    {
        var world = host.Session.World;
        var profile = world.Profile;
        int phases = TickProfile.PhaseNames.Length;
        int systems = world.Systems.Count, parts = phases + 1 + systems;
        if (_simNamesFor != world || _simNames.Length != parts + profile.Parts.Count + 1) NameParts(world, profile);

        Array.Clear(_simMs);
        double inTicks = 0, ahead = host.Session.TakeAheadMs();
        for (int p = 0; p < phases; p++) inTicks += _simMs[p] = profile.PhaseMs[p];
        // The phases also hold the tick run ahead during the last draw (docs/simulation.md "Tick ahead"), which is not in this frame's advance.
        _simMs[phases] = Math.Max(0, advanceMs - Math.Max(0, inTicks - ahead));
        for (int k = 0; k < profile.SystemMs.Length && phases + 1 + k < parts; k++) _simMs[phases + 1 + k] = profile.SystemMs[k];
        for (int k = 0; k < profile.Parts.Count && parts + k < _simMs.Length; k++) _simMs[parts + k] = profile.Parts[k].Ms;
        _simMs[^1] = ahead;
        int paths = world.Systems.OfType<MovementSystem>().FirstOrDefault()?.Paths.Pending ?? 0;
        profiler.Outside(advanceMs, profile.Ticks, _simNames, _simMs, phases + 1, systems, paths);

        // --log-spikes: a slow frame's simulation by system and part (the phases only add up what these hold).
        if (SpikeLog.Enabled && advanceMs > GameHost.SpikeSimMs)
            for (int k = phases + 1; k < _simMs.Length; k++)
                if (_simMs[k] >= 2) SpikeLog.Note($"sim {_simNames[k]}", ms: _simMs[k]);
        if (SpikeLog.Enabled) SimWindow(profile, advanceMs, ahead, phases + 1, parts);
        profile.Clear();
    }

    /// <summary>
    /// Makes the parts' names for the world (the systems without their "System" suffix) and their ms.
    /// </summary>
    void NameParts(World world, TickProfile profile)
    {
        _simNamesFor = world;
        _simNames =
        [
            .. TickProfile.PhaseNames,
            "outside ticks",
            .. world.Systems.Select(s => s.GetType().Name is var n && n.EndsWith("System", StringComparison.Ordinal) && n.Length > 6 ? n[..^6] : n),
            .. profile.Parts.Select(p => p.Name),
            "tick ahead",
        ];
        _simMs = new double[_simNames.Length];
    }

    #endregion

    #region Steady cost

    // --log-spikes: the simulation's steady cost, summed over a window of real time (the spike notes only see frames over SpikeSimMs).
    // The ragdolls sampled once a frame: in play, moving, the physics' awake bodies, frames with any moving.

    /// <summary>
    /// The window's length in real seconds.
    /// </summary>
    const double SimWindowSeconds = 5;

    /// <summary>
    /// The window's ms by key: '#' phase, '~' slow world, '.' part, none for a system.
    /// </summary>
    readonly Dictionary<string, double> _simWindowMs = [];

    /// <summary>
    /// When the window started (real seconds, -1 before the first), and the ms in the frame and ahead summed over it.
    /// </summary>
    double _simWindowStart = -1, _simWindowMain, _simWindowAhead;

    /// <summary>
    /// The ticks run in the window.
    /// </summary>
    long _simWindowTicks;

    /// <summary>
    /// The frames in the window.
    /// </summary>
    int _simWindowFrames;

    /// <summary>
    /// The ragdolls summed over the window's frames: in play, moving, the physics' awake bodies, moving carried, whole and limbs.
    /// </summary>
    long _simWindowRagdolls, _simWindowMoving, _simWindowAwake, _simWindowCarried, _simWindowWhole, _simWindowLimbs;

    /// <summary>
    /// The window's frames with any ragdoll moving.
    /// </summary>
    int _simWindowStepping;

    /// <summary>
    /// --log-spikes: every <see cref="SimWindowSeconds"/> one line of the speed, ticks a second, the sim's ms a frame, then ms a tick by phase, by
    /// system, by system in the slow world phase and by part, biggest first. Keys carry their list's prefix: '#' phase, '~' slow world, '.' part.
    /// </summary>
    void SimWindow(TickProfile profile, double advanceMs, double ahead, int firstSystem, int firstPart)
    {
        double now = host.RealTime.Elapsed.TotalSeconds;
        if (_simWindowStart < 0) _simWindowStart = now;
        AddFrame(profile, advanceMs, ahead, firstSystem, firstPart);

        double seconds = now - _simWindowStart;
        if (seconds < SimWindowSeconds) return;

        if (_simWindowTicks > 0) PrintWindow(seconds);
        ResetWindow(now);
    }

    /// <summary>
    /// Adds the frame's ticks, ms by key and ragdolls to the window.
    /// </summary>
    void AddFrame(TickProfile profile, double advanceMs, double ahead, int firstSystem, int firstPart)
    {
        (_simWindowTicks, _simWindowMain, _simWindowAhead, _simWindowFrames) =
            (_simWindowTicks + profile.Ticks, _simWindowMain + advanceMs, _simWindowAhead + ahead, _simWindowFrames + 1);
        void Add(string key, double ms) => _simWindowMs[key] = _simWindowMs.GetValueOrDefault(key) + ms;
        for (int p = 0; p < firstSystem - 1; p++) Add("#" + _simNames[p], _simMs[p]);
        for (int k = firstSystem; k < _simMs.Length - 1; k++) Add((k >= firstPart ? "." : "") + _simNames[k], _simMs[k]);
        for (int k = 0; k < profile.SystemMs.Length && firstSystem + k < firstPart; k++)
            Add("~" + _simNames[firstSystem + k], profile.Ms(TickProfile.SlowWorld, k));
        if (host.Session.World.Systems.OfType<Simulation.Ragdolls.RagdollSystem>().FirstOrDefault() is { } rag)
        {
            (_simWindowRagdolls, _simWindowMoving, _simWindowAwake, _simWindowStepping) = (_simWindowRagdolls + rag.Count,
                _simWindowMoving + rag.Moving, _simWindowAwake + rag.AwakeBodies, _simWindowStepping + (rag.Moving > 0 ? 1 : 0));
            (_simWindowCarried, _simWindowWhole, _simWindowLimbs) =
                (_simWindowCarried + rag.MovingCarried, _simWindowWhole + rag.MovingWhole, _simWindowLimbs + rag.MovingLimbs);
        }
    }

    /// <summary>
    /// The window's line over its <paramref name="seconds"/>.
    /// </summary>
    void PrintWindow(double seconds)
    {
        var inv = CultureInfo.InvariantCulture;
        string Top(char kind, int count) => string.Join(", ",
            _simWindowMs.Where(p => (p.Key[0] is '#' or '~' or '.' ? p.Key[0] : ' ') == kind && p.Value / _simWindowTicks >= 0.01)
            .OrderByDescending(p => p.Value).Take(count)
            .Select(p => string.Create(inv, $"{p.Key.TrimStart('#', '~', '.')} {p.Value / _simWindowTicks:0.00}")));
        double tickMs = _simWindowMs.Where(p => p.Key[0] == '#').Sum(p => p.Value) / _simWindowTicks;
        Console.WriteLine(string.Create(inv, $"simprof   {host.Session.Simulation.Speed:0}x {_simWindowFrames / seconds:0} fps, ") +
            string.Create(inv, $"{_simWindowTicks / seconds:0} ticks/s, ") +
            string.Create(inv, $"{host.Session.World.Snapshot.Characters.Count} characters; ") +
            string.Create(inv, $"ms a frame: in frame {_simWindowMain / _simWindowFrames:0.00}, ahead {_simWindowAhead / _simWindowFrames:0.00}; ") +
            string.Create(inv, $"ms a tick {tickMs:0.00}: ") +
            $"phases: {Top('#', 10)}; systems: {Top(' ', 12)}; slow world: {Top('~', 12)}; parts: {Top('.', 12)}; " +
            string.Create(inv, $"ragdolls {(double)_simWindowRagdolls / _simWindowFrames:0.0}, ") +
            string.Create(inv, $"moving {(double)_simWindowMoving / _simWindowFrames:0.0} ") +
            string.Create(inv, $"(carried {(double)_simWindowCarried / _simWindowFrames:0.0}, ") +
            string.Create(inv, $"whole {(double)_simWindowWhole / _simWindowFrames:0.0}, ") +
            string.Create(inv, $"limbs {(double)_simWindowLimbs / _simWindowFrames:0.0}), ") +
            string.Create(inv, $"awake bodies {(double)_simWindowAwake / _simWindowFrames:0}, ") +
            string.Create(inv, $"stepping {100.0 * _simWindowStepping / _simWindowFrames:0}% of frames"));
    }

    /// <summary>
    /// Starts the next window at <paramref name="now"/>.
    /// </summary>
    void ResetWindow(double now)
    {
        _simWindowMs.Clear();
        (_simWindowStart, _simWindowTicks, _simWindowMain, _simWindowAhead, _simWindowFrames) = (now, 0, 0, 0, 0);
        (_simWindowRagdolls, _simWindowMoving, _simWindowAwake, _simWindowStepping, _simWindowCarried, _simWindowWhole, _simWindowLimbs) =
            (0, 0, 0, 0, 0, 0, 0);
    }

    #endregion
}
