using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// One effect a mod queued (<see cref="ModContext.PlayEffect(string, Vector3, float, Vector3?)"/>): the id it named and the particle system that
/// resolved to, at a world point or on a character (<see cref="On"/> not <see cref="CharacterId.None"/>; then <see cref="Bone"/>, null for the
/// chest), the particles' recolour (null: as their texture) and the seconds it emits and follows the character. A weapon glow's effect
/// (<see cref="ModWeaponGlows"/>, <see cref="OnBlade"/>) follows the character's held blade instead: at its hilt end, +Y along it, scaled to it.
/// </summary>
internal readonly record struct ModEffectRequest(string Owner, string Effect, string System, Vector3 At, CharacterId On, string? Bone,
    Vector3? Tint, float Seconds, bool OnBlade = false)
{
    /// <summary>
    /// The effect follows a character rather than staying at <see cref="At"/>.
    /// </summary>
    public bool Attached => On != CharacterId.None;
}

/// <summary>
/// What plays the mods' effects: the renderer's particles in the game, a recorder in tests.
/// </summary>
internal interface IModEffectSink
{
    /// <summary>
    /// Starts <paramref name="request"/>; returns what plays it (followed by <see cref="Follow"/>), or null when nothing could start (the character
    /// is not drawn, no particles).
    /// </summary>
    object? Start(in ModEffectRequest request);

    /// <summary>
    /// Moves an attached effect's emitter to where its character is now.
    /// </summary>
    void Follow(object playing, in ModEffectRequest request);

    /// <summary>
    /// Moves an attached effect as <see cref="Follow"/> does; false when it could not (its character or blade is not drawn, or it stopped).
    /// </summary>
    bool Track(object playing, in ModEffectRequest request)
    {
        Follow(playing, request);
        return true;
    }

    /// <summary>
    /// Makes a started effect emit for <paramref name="seconds"/> from now (0: it stops, its particles live out).
    /// </summary>
    void Renew(object playing, float seconds)
    {
    }
}

/// <summary>
/// The code mods' display-only effects (docs/modding.md "Visual effects"): queued from host-side callbacks, started once a frame on the host's
/// thread through an <see cref="IModEffectSink"/>, at most <see cref="MaxPerFrame"/> a frame and <see cref="MaxAlive"/> playing. The simulation
/// never reads it. A rejected request is logged once per mod and problem, and dropped.
/// </summary>
internal sealed class ModEffects
{
    /// <summary>
    /// Why a request was dropped; each is logged once per mod.
    /// </summary>
    internal enum Problem { UnknownEffect, BadPoint, BadCharacter, BadSeconds, BadTint, Full, OffThread }

    /// <summary>
    /// The requests waiting to start, all mods together.
    /// </summary>
    public const int MaxQueued = 128;
    /// <summary>
    /// The requests started in one frame.
    /// </summary>
    public const int MaxPerFrame = 16;
    /// <summary>
    /// The effects playing at once; further requests wait in the queue.
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
    /// One started effect: its request, what plays it and its seconds left.
    /// </summary>
    sealed class Playing(ModEffectRequest request, object handle)
    {
        /// <summary>
        /// What the mod asked for.
        /// </summary>
        public ModEffectRequest Request { get; } = request;
        /// <summary>
        /// What the sink plays it with.
        /// </summary>
        public object Handle { get; } = handle;
        /// <summary>
        /// The seconds it still plays.
        /// </summary>
        public float Left { get; set; } = request.Seconds;
    }

    /// <summary>
    /// Guards the lists: a request may come from any host-side callback.
    /// </summary>
    readonly object _lock = new();
    /// <summary>
    /// The requests waiting to start, oldest first.
    /// </summary>
    readonly List<ModEffectRequest> _queued = [];
    /// <summary>
    /// The effects playing.
    /// </summary>
    readonly List<Playing> _alive = [];
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
    /// Whether <paramref name="effect"/> plays now (<see cref="ModContext.HasEffect"/>): false off the host's thread or before the particles load.
    /// </summary>
    public bool Has(string? effect) =>
        !string.IsNullOrEmpty(effect) && !(_hostThread != 0 && Environment.CurrentManagedThreadId != _hostThread) && Exists?.Invoke(effect) == true;

    /// <summary>
    /// Where a rejection's line goes (the game's log).
    /// </summary>
    public Action<string> Log { get; set; } = Console.Error.WriteLine;

    /// <summary>
    /// The requests waiting to start.
    /// </summary>
    public int Queued { get { lock (_lock) return _queued.Count; } }

    /// <summary>
    /// The effects playing.
    /// </summary>
    public int Alive { get { lock (_lock) return _alive.Count; } }

    /// <summary>
    /// Nothing queued or playing: the frame has nothing to do.
    /// </summary>
    public bool Idle { get { lock (_lock) return _queued.Count == 0 && _alive.Count == 0; } }

    /// <summary>
    /// The emitters the weapon glows play through the same sink (<see cref="ModWeaponGlows"/>), counted against <see cref="MaxAlive"/>.
    /// </summary>
    public int Reserved { get; set; }

    /// <summary>
    /// Whether <paramref name="count"/> more emitters fit under <see cref="MaxAlive"/> beside those playing and reserved.
    /// </summary>
    public bool Fits(int count)
    {
        lock (_lock) return _alive.Count + Reserved + count <= MaxAlive;
    }

    /// <summary>
    /// Queues an effect at <paramref name="at"/>, or with <paramref name="attached"/> on the character <paramref name="on"/>; false when it was
    /// rejected (logged once per mod and problem).
    /// </summary>
    public bool Play(string owner, string effect, Vector3 at, bool attached, CharacterId on, string? bone, Vector3? tint, float seconds)
    {
        string? system = null;
        var problem = Check(effect, at, attached, on, bone, tint, seconds)
            ?? Resolved(effect, out system)
            ?? Enqueue(new ModEffectRequest(owner, effect, system!, at, attached ? on : CharacterId.None, bone, tint, seconds));
        if (problem is null) return true;

        Tell(owner, effect, problem.Value);
        return false;
    }

    /// <summary>
    /// Queues <paramref name="request"/>; <see cref="Problem.Full"/> when <see cref="MaxQueued"/> already wait.
    /// </summary>
    Problem? Enqueue(in ModEffectRequest request)
    {
        lock (_lock)
        {
            if (_queued.Count >= MaxQueued) return Problem.Full;

            _queued.Add(request);
            return null;
        }
    }

    /// <summary>
    /// The problem of a request's arguments, or null.
    /// </summary>
    Problem? Check(string effect, Vector3 at, bool attached, CharacterId on, string? bone, Vector3? tint, float seconds)
    {
        if (_hostThread != 0 && Environment.CurrentManagedThreadId != _hostThread) return Problem.OffThread;
        if (string.IsNullOrEmpty(effect)) return Problem.UnknownEffect;
        if (!float.IsFinite(at.X + at.Y + at.Z)) return Problem.BadPoint;
        if (attached && (on.Slot < 0 || bone is { Length: 0 })) return Problem.BadCharacter;
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > MaxSeconds) return Problem.BadSeconds;
        if (tint is { } t && !TintInRange(t)) return Problem.BadTint;
        return null;
    }

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
    /// The particle system of <paramref name="effect"/> (<see cref="Resolve"/>), or null when it names none.
    /// </summary>
    public string? System(string? effect)
    {
        if (string.IsNullOrEmpty(effect)) return null;

        string? system = Resolve is { } resolve ? resolve(effect) : effect;
        return system is { Length: > 0 } ? system : null;
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
            case Problem.BadPoint: return "its point is not finite";
            case Problem.BadCharacter: return "no character, or an empty bone name";
            case Problem.BadSeconds: return $"its lifetime is not in (0, {MaxSeconds}] seconds";
            case Problem.BadTint: return $"its tint is not finite in [0, {MaxTint}]";
            case Problem.Full: return $"{MaxQueued} effects are already queued";
            case Problem.OffThread: return "queued off the host's thread (from a tick system?)";
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
    }

    /// <summary>
    /// Once a frame on the host's thread: ages the playing effects by <paramref name="seconds"/>, moves the attached ones to their characters, then
    /// starts the queued ones, oldest first, within the caps.
    /// </summary>
    public void Advance(float seconds, IModEffectSink sink)
    {
        _hostThread = Environment.CurrentManagedThreadId;
        lock (_lock)
        {
            Age(seconds);
            Follow(sink);
            StartQueued(sink);
        }
    }

    /// <summary>
    /// Takes <paramref name="seconds"/> off every playing effect, dropping those done.
    /// </summary>
    void Age(float seconds)
    {
        foreach (var p in _alive) p.Left -= seconds;
        _alive.RemoveAll(p => p.Left <= 0);
    }

    /// <summary>
    /// Moves the attached effects' emitters to their characters.
    /// </summary>
    void Follow(IModEffectSink sink)
    {
        foreach (var p in _alive)
        {
            if (p.Request.Attached) sink.Follow(p.Handle, p.Request);
        }
    }

    /// <summary>
    /// Starts up to <see cref="MaxPerFrame"/> queued requests while fewer than <see cref="MaxAlive"/> play; one that cannot start is dropped.
    /// </summary>
    void StartQueued(IModEffectSink sink)
    {
        int started = 0;
        while (_queued.Count > 0 && started < MaxPerFrame && _alive.Count + Reserved < MaxAlive)
        {
            var request = _queued[0];
            _queued.RemoveAt(0);
            started++;
            if (sink.Start(request) is { } handle) _alive.Add(new Playing(request, handle));
        }
    }

    /// <summary>
    /// Drops what <paramref name="owner"/> queued and stops following its effects (the mod left the session).
    /// </summary>
    public void Drop(string owner)
    {
        lock (_lock)
        {
            _queued.RemoveAll(r => r.Owner == owner);
            _alive.RemoveAll(p => p.Request.Owner == owner);
        }
    }

    /// <summary>
    /// Drops everything (a new world: its characters are not the old world's).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queued.Clear();
            _alive.Clear();
        }
    }
}
