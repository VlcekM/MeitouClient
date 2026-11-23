using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// One effect a mod asked for (<see cref="ModContext.PlayEffect(string, EffectAnchor, float, Vector3?)"/>): the id it named and the particle
/// system that resolved to, where it plays, the particles' recolour (null: as their texture) and the seconds it emits.
/// </summary>
internal readonly record struct ModEffectRequest(string Owner, string Effect, string System, EffectAnchor Anchor, Vector3? Tint, float Seconds)
{
    /// <summary>
    /// The anchor is a fixed point with no scale of its own (a renewal or pulse may change one): nothing to follow.
    /// </summary>
    public bool Fixed => Anchor.Kind == EffectAnchorKind.Point && Anchor == Anchor.Key;

    /// <summary>
    /// The seconds since it first started playing (its anchor's pulse, <see cref="EffectAnchor.Pulsing"/>).
    /// </summary>
    public double Age { get; init; }
}

/// <summary>
/// What plays the mods' effects: the renderer's particles in the game, a recorder in tests.
/// </summary>
internal interface IModEffectSink
{
    /// <summary>
    /// Starts <paramref name="request"/> where its anchor is now; returns what plays it, or null when the anchor cannot be located (the effect
    /// waits) or nothing plays (no particles).
    /// </summary>
    object? Start(in ModEffectRequest request);

    /// <summary>
    /// Moves a started effect to where its anchor is now; false when the anchor cannot be located or the effect stopped (it pauses).
    /// </summary>
    bool Follow(object playing, in ModEffectRequest request);

    /// <summary>
    /// Makes a started effect emit for <paramref name="seconds"/> from now (0: it stops, its particles live out).
    /// </summary>
    void Renew(object playing, float seconds);
}

/// <summary>
/// The code mods' display-only effects (docs/modding.md "Visual effects"): queued from host-side callbacks, then played once a frame on the host's
/// thread through an <see cref="IModEffectSink"/>, at most <see cref="MaxPerFrame"/> started a frame and <see cref="MaxAlive"/> alive. An effect
/// whose anchor cannot be located pauses (its emission stops) and starts again where it is once it can, until its lifetime ends. A kept effect
/// (<see cref="Keep"/>) is renewed, not restarted, by the same mod, effect and anchor. The simulation never reads it. A rejected request is
/// logged once per mod and problem, and dropped.
/// </summary>
internal sealed class ModEffects
{
    /// <summary>
    /// Why a request was dropped; each is logged once per mod.
    /// </summary>
    internal enum Problem
    {
        UnknownEffect, BadPoint, BadCharacter, BadBuilding, BadStretch, BadSeconds, BadTint, Full, OffThread, BadProjectile, BadPulse,
    }

    /// <summary>
    /// The requests waiting to start, all mods together.
    /// </summary>
    public const int MaxQueued = 128;
    /// <summary>
    /// The effects started in one frame.
    /// </summary>
    public const int MaxPerFrame = 16;
    /// <summary>
    /// The effects alive at once (playing or paused); further requests wait in the queue.
    /// </summary>
    public const int MaxAlive = 64;
    /// <summary>
    /// The longest lifetime a request may ask for, in seconds.
    /// </summary>
    public const float MaxSeconds = 30;
    /// <summary>
    /// The largest tint channel (the particles' recolour is about 0 to 2).
    /// </summary>
    public const float MaxTint = 4;
    /// <summary>
    /// The largest stretch an anchor may ask for.
    /// </summary>
    public const float MaxStretch = 100;
    /// <summary>
    /// The shortest and longest pulse an anchor may ask for, in seconds.
    /// </summary>
    public const float MinPulseSeconds = 0.05f, MaxPulseSeconds = 60;

    /// <summary>
    /// One effect alive or waiting: its request, what plays it (null while paused) and its seconds left.
    /// </summary>
    sealed class Effect(ModEffectRequest request, bool kept)
    {
        /// <summary>
        /// What the mod asked for.
        /// </summary>
        public ModEffectRequest Request { get; set; } = request;
        /// <summary>
        /// A kept effect (<see cref="Keep"/>), renewed by its key.
        /// </summary>
        public bool Kept { get; } = kept;
        /// <summary>
        /// What the sink plays it with; null while paused or not started.
        /// </summary>
        public object? Handle { get; set; }
        /// <summary>
        /// The seconds it still plays.
        /// </summary>
        public float Left { get; set; } = request.Seconds;
        /// <summary>
        /// Its lifetime was renewed: the sink is told at the next frame.
        /// </summary>
        public bool Renewed { get; set; }
        /// <summary>
        /// The seconds it has been alive.
        /// </summary>
        public double Age { get; set; }

        /// <summary>
        /// Its request as of now (its age for the pulse).
        /// </summary>
        public ModEffectRequest Now => Request with { Age = Age };

        /// <summary>
        /// Whether it is the kept <paramref name="effect"/> of <paramref name="owner"/> on <paramref name="anchor"/> (its scale aside).
        /// </summary>
        public bool Keeps(string owner, string effect, in EffectAnchor anchor) =>
            Kept && Request.Owner == owner && Request.Effect == effect && Request.Anchor.Key == anchor.Key;
    }

    /// <summary>
    /// Guards the lists: a request may come from any host-side callback.
    /// </summary>
    readonly object _lock = new();
    /// <summary>
    /// The effects waiting for room, oldest first.
    /// </summary>
    readonly List<Effect> _queued = [];
    /// <summary>
    /// The effects alive.
    /// </summary>
    readonly List<Effect> _alive = [];
    /// <summary>
    /// What played effects that were stopped, paused or replaced: stopped in the sink at the next frame.
    /// </summary>
    readonly List<object> _stopping = [];
    /// <summary>
    /// The mod and problem pairs logged already.
    /// </summary>
    readonly HashSet<(string Owner, Problem Problem)> _told = [];
    /// <summary>
    /// The thread of the last <see cref="Advance"/> (the host's); 0 before the first.
    /// </summary>
    volatile int _hostThread;

    /// <summary>
    /// An effect id to its particle system, null for an unknown one; without it every id is taken as a system name.
    /// </summary>
    public Func<string, string?>? Resolve { get; set; }

    /// <summary>
    /// Whether an effect id names a particle system the loaded particles have; null until the host sets it (then none does).
    /// </summary>
    public Func<string, bool>? Exists { get; set; }

    /// <summary>
    /// Where a rejection's line goes (the game's log).
    /// </summary>
    public Action<string> Log { get; set; } = Console.Error.WriteLine;

    /// <summary>
    /// Where an anchor is drawn now (its frame's origin), on the host's thread; null until the host sets it, or when the anchor is not there.
    /// </summary>
    public Func<EffectAnchor, Vector3?>? Locate { get; set; }

    /// <summary>
    /// The caller is on the host's thread (or no frame has run yet).
    /// </summary>
    public bool OnHostThread => !OffThread;

    /// <summary>
    /// The requests waiting for room.
    /// </summary>
    public int Queued { get { lock (_lock) return _queued.Count; } }

    /// <summary>
    /// The effects alive (playing or paused).
    /// </summary>
    public int Alive { get { lock (_lock) return _alive.Count; } }

    /// <summary>
    /// The effects playing now (not paused).
    /// </summary>
    public int Playing { get { lock (_lock) return _alive.Count(a => a.Handle is not null); } }

    /// <summary>
    /// Nothing queued, alive or to stop: the frame has nothing to do.
    /// </summary>
    public bool Idle { get { lock (_lock) return _queued.Count == 0 && _alive.Count == 0 && _stopping.Count == 0; } }

    /// <summary>
    /// The request comes from another thread than the host's.
    /// </summary>
    bool OffThread => _hostThread != 0 && Environment.CurrentManagedThreadId != _hostThread;

    /// <summary>
    /// Whether <paramref name="effect"/> plays now (<see cref="ModContext.HasEffect"/>): false off the host's thread or before the particles load.
    /// </summary>
    public bool Has(string? effect) => !string.IsNullOrEmpty(effect) && !OffThread && Exists?.Invoke(effect) == true;

    /// <summary>
    /// The particle system of <paramref name="effect"/> (<see cref="Resolve"/>), or null when it names none.
    /// </summary>
    public string? System(string? effect)
    {
        if (string.IsNullOrEmpty(effect)) return null;

        string? system = Resolve is { } resolve ? resolve(effect) : effect;
        return system is { Length: > 0 } ? system : null;
    }

    /// <summary>
    /// Queues a new effect on <paramref name="anchor"/>; false when it was rejected (logged once per mod and problem).
    /// </summary>
    public bool Play(string owner, string effect, in EffectAnchor anchor, Vector3? tint, float seconds) =>
        Ask(owner, effect, anchor, tint, seconds, kept: false);

    /// <summary>
    /// Renews <paramref name="owner"/>'s kept <paramref name="effect"/> on <paramref name="anchor"/> to <paramref name="seconds"/> from now, or
    /// queues it when there is none (another tint restarts it); false when rejected.
    /// </summary>
    public bool Keep(string owner, string effect, in EffectAnchor anchor, Vector3? tint, float seconds) =>
        Ask(owner, effect, anchor, tint, seconds, kept: true);

    /// <summary>
    /// A request checked, then renewed or queued.
    /// </summary>
    bool Ask(string owner, string effect, in EffectAnchor anchor, Vector3? tint, float seconds, bool kept)
    {
        string? system = null;
        var problem = Check(effect, anchor, tint, seconds)
            ?? Resolved(effect, out system)
            ?? Add(new ModEffectRequest(owner, effect, system!, anchor, tint, seconds), kept);
        if (problem is null) return true;

        Tell(owner, effect, problem.Value);
        return false;
    }

    /// <summary>
    /// Renews the kept effect of the same key, or queues a new one; <see cref="Problem.Full"/> when <see cref="MaxQueued"/> already wait.
    /// </summary>
    Problem? Add(in ModEffectRequest request, bool kept)
    {
        lock (_lock)
        {
            if (kept && Renew(request)) return null;
            if (_queued.Count >= MaxQueued) return Problem.Full;

            _queued.Add(new Effect(request, kept));
            return null;
        }
    }

    /// <summary>
    /// The kept effect of the request's key takes its lifetime (another tint: its request, from a fresh start); false when there is none.
    /// </summary>
    bool Renew(in ModEffectRequest request)
    {
        var r = request;
        var same = _alive.Concat(_queued).FirstOrDefault(a => a.Keeps(r.Owner, r.Effect, r.Anchor));
        if (same is null) return false;

        same.Left = request.Seconds;
        if (same.Request.Tint != request.Tint) Pause(same);
        else same.Renewed = same.Handle is not null;
        same.Request = request;
        return true;
    }

    /// <summary>
    /// Ends <paramref name="owner"/>'s kept <paramref name="effect"/> on <paramref name="anchor"/>; false when it has none.
    /// </summary>
    public bool Stop(string owner, string effect, in EffectAnchor anchor)
    {
        var key = anchor;
        lock (_lock)
        {
            int removed = _queued.RemoveAll(a => a.Keeps(owner, effect, key));
            return removed + End(a => a.Keeps(owner, effect, key)) > 0;
        }
    }

    /// <summary>
    /// Removes the living effects that match, stopping what plays them at the next frame; how many.
    /// </summary>
    int End(Func<Effect, bool> match)
    {
        foreach (var a in _alive.Where(match)) Pause(a);
        return _alive.RemoveAll(a => match(a));
    }

    /// <summary>
    /// Stops what plays <paramref name="effect"/> at the next frame; it is paused (started again where its anchor is).
    /// </summary>
    void Pause(Effect effect)
    {
        if (effect.Handle is { } handle) _stopping.Add(handle);
        effect.Handle = null;
        effect.Renewed = false;
    }

    /// <summary>
    /// The problem of a request's arguments, or null.
    /// </summary>
    Problem? Check(string effect, in EffectAnchor anchor, Vector3? tint, float seconds)
    {
        if (OffThread) return Problem.OffThread;
        if (string.IsNullOrEmpty(effect)) return Problem.UnknownEffect;
        if (AnchorProblem(anchor) is { } problem) return problem;
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > MaxSeconds) return Problem.BadSeconds;
        if (tint is { } t && !TintInRange(t)) return Problem.BadTint;
        return null;
    }

    /// <summary>
    /// The problem of an anchor: an unknown kind, a point, offset or turn that is not finite (or a zero turn), no character or bone, no building
    /// id, a negative bolt id, a stretch or size out of (0, <see cref="MaxStretch"/>], a pulse out of range. Trails share it.
    /// </summary>
    internal static Problem? AnchorProblem(in EffectAnchor a)
    {
        if (a.Kind is < EffectAnchorKind.Point or > EffectAnchorKind.Projectile || !Finite(a.Offset) || a.Turn is { } t && !Usable(t))
            return Problem.BadPoint;
        if (a.Kind is EffectAnchorKind.Bone or EffectAnchorKind.Weapon && a.Character.Slot < 0) return Problem.BadCharacter;
        if (a.Kind == EffectAnchorKind.Bone && string.IsNullOrEmpty(a.Name)) return Problem.BadCharacter;
        if (a.Kind == EffectAnchorKind.Building && string.IsNullOrEmpty(a.Name)) return Problem.BadBuilding;
        if (a.Kind == EffectAnchorKind.Projectile && a.ProjectileId < 0) return Problem.BadProjectile;
        if (a.Stretch is { } s && !(s > 0 && s <= MaxStretch)) return Problem.BadStretch;
        if (a.Size is { } z && !(z > 0 && z <= MaxStretch)) return Problem.BadStretch;
        return PulseProblem(a);
    }

    /// <summary>
    /// <see cref="Problem.BadPulse"/> for a pulse amount out of [0, 1) or, with one, seconds out of [<see cref="MinPulseSeconds"/>,
    /// <see cref="MaxPulseSeconds"/>].
    /// </summary>
    static Problem? PulseProblem(in EffectAnchor a)
    {
        if (!(a.PulseAmount >= 0 && a.PulseAmount < 1)) return Problem.BadPulse;
        if (a.PulseAmount > 0 && !(a.PulseSeconds >= MinPulseSeconds && a.PulseSeconds <= MaxPulseSeconds)) return Problem.BadPulse;
        return null;
    }

    /// <summary>
    /// Every component of <paramref name="v"/> is finite.
    /// </summary>
    static bool Finite(Vector3 v) => float.IsFinite(v.X + v.Y + v.Z);

    /// <summary>
    /// A turn that is finite and not zero (it is normalized where it is used).
    /// </summary>
    static bool Usable(Quaternion q) => float.IsFinite(q.X + q.Y + q.Z + q.W) && q.LengthSquared() > 1e-8f;

    /// <summary>
    /// Every channel of <paramref name="tint"/> is in [0, <see cref="MaxTint"/>] (NaN is not).
    /// </summary>
    static bool TintInRange(Vector3 tint) =>
        tint.X is >= 0 and <= MaxTint && tint.Y is >= 0 and <= MaxTint && tint.Z is >= 0 and <= MaxTint;

    /// <summary>
    /// The particle system of <paramref name="effect"/>; <see cref="Problem.UnknownEffect"/> when it names none.
    /// </summary>
    Problem? Resolved(string effect, out string? system)
    {
        system = System(effect);
        return system is not null ? null : Problem.UnknownEffect;
    }

    /// <summary>
    /// Logs a rejection the first time the mod has this problem.
    /// </summary>
    void Tell(string owner, string effect, Problem problem)
    {
        lock (_lock)
        {
            if (!_told.Add((owner, problem))) return;
        }
        Log($"mod       {owner}: effect '{effect}' dropped: {Reason(problem)} (later drops for this reason are not logged)");
    }

    /// <summary>
    /// A problem in words, for the log (one <c>case</c> per line, so the translations check reads them as not shown).
    /// </summary>
    static string Reason(Problem problem)
    {
        switch (problem)
        {
            case Problem.UnknownEffect: return "no EFFECT record or particle system has that id";
            case Problem.BadPoint: return "its anchor's kind, point, offset or turn is not usable";
            case Problem.BadCharacter: return "no character, or an empty bone name";
            case Problem.BadBuilding: return "no building id";
            case Problem.BadStretch: return $"its stretch is not in (0, {MaxStretch}]";
            case Problem.BadSeconds: return $"its lifetime is not in (0, {MaxSeconds}] seconds";
            case Problem.BadTint: return $"its tint is not finite in [0, {MaxTint}]";
            case Problem.Full: return $"{MaxQueued} effects are already queued";
            case Problem.OffThread: return "queued off the host's thread (from a tick system?)";
            case Problem.BadProjectile: return "its projectile id is negative";
            case Problem.BadPulse: return $"its pulse is not an amount in [0, 1) every [{MinPulseSeconds}, {MaxPulseSeconds}] seconds";
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
    }

    /// <summary>
    /// A shared anchor problem in words (<see cref="ModTrails"/>' log too).
    /// </summary>
    internal static string AnchorReason(Problem problem) => Reason(problem);

    /// <summary>
    /// Once a frame on the host's thread: ages the living effects by <paramref name="seconds"/>, moves the playing ones with their anchors (pausing
    /// those that lost theirs) and tells renewals, stops what was stopped or paused, then starts the paused and the queued ones, oldest first,
    /// within the caps.
    /// </summary>
    public void Advance(float seconds, IModEffectSink sink)
    {
        _hostThread = Environment.CurrentManagedThreadId;
        lock (_lock)
        {
            Age(seconds);
            Follow(sink);
            foreach (var handle in _stopping) sink.Renew(handle, 0);
            _stopping.Clear();
            StartWaiting(sink);
        }
    }

    /// <summary>
    /// Takes <paramref name="seconds"/> off every living effect, ending those done.
    /// </summary>
    void Age(float seconds)
    {
        foreach (var a in _alive)
        {
            a.Left -= seconds;
            a.Age += seconds;
        }
        End(a => a.Left <= 0);
    }

    /// <summary>
    /// Tells the renewed playing effects their new lifetime and moves those that follow something to it, pausing those whose anchor is gone.
    /// </summary>
    void Follow(IModEffectSink sink)
    {
        foreach (var a in _alive)
        {
            if (a.Handle is not { } handle) continue;

            if (a.Renewed) sink.Renew(handle, a.Left);
            a.Renewed = false;
            if (!a.Request.Fixed && !sink.Follow(handle, a.Now)) Pause(a);
        }
    }

    /// <summary>
    /// Starts up to <see cref="MaxPerFrame"/> effects: the paused ones, then queued ones while fewer than <see cref="MaxAlive"/> live. One whose
    /// anchor is not there stays, paused, until its lifetime ends.
    /// </summary>
    void StartWaiting(IModEffectSink sink)
    {
        int started = 0;
        foreach (var a in _alive)
        {
            if (started >= MaxPerFrame) return;
            if (a.Handle is null && TryStart(a, sink)) started++;
        }
        while (_queued.Count > 0 && started < MaxPerFrame && _alive.Count < MaxAlive)
        {
            var a = _queued[0];
            _queued.RemoveAt(0);
            _alive.Add(a);
            if (TryStart(a, sink)) started++;
        }
    }

    /// <summary>
    /// Starts <paramref name="effect"/> for the seconds it has left; false when its anchor is not there.
    /// </summary>
    static bool TryStart(Effect effect, IModEffectSink sink)
    {
        effect.Handle = sink.Start(effect.Now with { Seconds = effect.Left });
        return effect.Handle is not null;
    }

    /// <summary>
    /// Ends what <paramref name="owner"/> queued and plays (the mod left the session).
    /// </summary>
    public void Drop(string owner)
    {
        lock (_lock)
        {
            _queued.RemoveAll(a => a.Request.Owner == owner);
            End(a => a.Request.Owner == owner);
        }
    }

    /// <summary>
    /// Forgets everything (a new world: its characters, buildings and particles are not the old world's).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queued.Clear();
            _alive.Clear();
            _stopping.Clear();
        }
    }
}
