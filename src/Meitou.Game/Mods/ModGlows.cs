using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// A glow as drawn now (<see cref="ModGlows.Current"/>): the colour (rgb, 1 the full channel) and the strength after the fade in and out.
/// </summary>
internal readonly record struct GlowNow(Vector3 Colour, float Strength);

/// <summary>
/// The code mods' display-only glows (docs/modding.md "Glow"): at most one a target, the newest request replacing the one before, fading in and
/// out over <see cref="FadeSeconds"/>. Set and aged on the host's thread; the renderer reads <see cref="Current"/> each frame, the simulation
/// never. A rejected request is logged once per mod and problem, and dropped.
/// </summary>
internal sealed class ModGlows
{
    /// <summary>
    /// Why a request was dropped; each is logged once per mod.
    /// </summary>
    internal enum Problem { BadTarget, BadColour, BadSeconds, BadStrength, Full, OffThread }

    /// <summary>
    /// The longest lifetime a request may ask for, in seconds.
    /// </summary>
    public const float MaxSeconds = 30;
    /// <summary>
    /// The largest colour channel.
    /// </summary>
    public const float MaxColour = 4;
    /// <summary>
    /// The largest strength.
    /// </summary>
    public const float MaxStrength = 4;
    /// <summary>
    /// The targets glowing at once; a request for another is rejected.
    /// </summary>
    public const int MaxGlowing = 256;
    /// <summary>
    /// The seconds a glow takes to come up and to die down.
    /// </summary>
    public const float FadeSeconds = 0.25f;

    /// <summary>
    /// One target's glow: who set it, its colour and strength, and the seconds it has shown and those it has left.
    /// </summary>
    sealed class Glow(string owner, Vector3 colour, float strength, float shown, float left)
    {
        /// <summary>
        /// The mod that set it.
        /// </summary>
        public string Owner { get; } = owner;
        /// <summary>
        /// Its colour, rgb.
        /// </summary>
        public Vector3 Colour { get; } = colour;
        /// <summary>
        /// Its strength at full.
        /// </summary>
        public float Strength { get; } = strength;
        /// <summary>
        /// The seconds it has shown (a replacement starts where the glow before stood).
        /// </summary>
        public float Shown { get; set; } = shown;
        /// <summary>
        /// The seconds it still shows.
        /// </summary>
        public float Left { get; set; } = left;

        /// <summary>
        /// The fade: 0 to 1 over the first and last <see cref="FadeSeconds"/>.
        /// </summary>
        public float Envelope => Math.Clamp(MathF.Min(Shown, Left) / FadeSeconds, 0, 1);
    }

    /// <summary>
    /// Guards the glows: a request may come from any host-side callback.
    /// </summary>
    readonly object _lock = new();
    /// <summary>
    /// The glows by target.
    /// </summary>
    readonly Dictionary<GlowTarget, Glow> _glows = [];
    /// <summary>
    /// The mod and problem pairs logged already.
    /// </summary>
    readonly HashSet<(string Owner, Problem Problem)> _told = [];
    /// <summary>
    /// The thread of the last <see cref="Advance"/> (the host's); 0 before the first.
    /// </summary>
    volatile int _hostThread;

    /// <summary>
    /// Where a rejection's line goes (the game's log).
    /// </summary>
    public Action<string> Log { get; set; } = Console.Error.WriteLine;

    /// <summary>
    /// The targets glowing (fading out included).
    /// </summary>
    public int Count { get { lock (_lock) return _glows.Count; } }

    /// <summary>
    /// Makes <paramref name="target"/> glow, replacing its glow (fading from where it stood, so a renewal shows no restart); false when rejected
    /// (logged once per mod and problem).
    /// </summary>
    public bool Start(string owner, in GlowTarget target, Vector3 colour, float seconds, float strength)
    {
        var problem = Check(target, colour, seconds, strength) ?? Set(target, new Glow(owner, colour, strength, 0, seconds));
        if (problem is null) return true;

        Tell(owner, problem.Value);
        return false;
    }

    /// <summary>
    /// Fades out the glow <paramref name="owner"/> set on <paramref name="target"/>; false when it set none there (or off the host's thread).
    /// </summary>
    public bool Stop(string owner, in GlowTarget target)
    {
        if (OffThread)
        {
            Tell(owner, Problem.OffThread);
            return false;
        }

        lock (_lock)
        {
            if (!_glows.TryGetValue(target, out var glow) || glow.Owner != owner) return false;

            glow.Left = MathF.Min(glow.Left, FadeSeconds * glow.Envelope);
            return true;
        }
    }

    /// <summary>
    /// The request comes from another thread than the host's.
    /// </summary>
    bool OffThread => _hostThread != 0 && Environment.CurrentManagedThreadId != _hostThread;

    /// <summary>
    /// The problem of a request's arguments, or null.
    /// </summary>
    Problem? Check(in GlowTarget target, Vector3 colour, float seconds, float strength)
    {
        if (OffThread) return Problem.OffThread;
        if (target.On.Slot < 0 || target.Kind is < GlowTargetKind.Character or > GlowTargetKind.Worn) return Problem.BadTarget;
        if (!InRange(colour.X) || !InRange(colour.Y) || !InRange(colour.Z)) return Problem.BadColour;
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > MaxSeconds) return Problem.BadSeconds;
        if (!float.IsFinite(strength) || strength < 0 || strength > MaxStrength) return Problem.BadStrength;
        return null;
    }

    /// <summary>
    /// A colour channel in [0, <see cref="MaxColour"/>] (NaN is not).
    /// </summary>
    static bool InRange(float channel) => channel is >= 0 and <= MaxColour;

    /// <summary>
    /// Sets <paramref name="glow"/>, coming up from where the one it replaces stood; <see cref="Problem.Full"/> for a new target past
    /// <see cref="MaxGlowing"/>.
    /// </summary>
    Problem? Set(in GlowTarget target, Glow glow)
    {
        lock (_lock)
        {
            bool replaces = _glows.TryGetValue(target, out var before);
            if (!replaces && _glows.Count >= MaxGlowing) return Problem.Full;

            if (replaces) glow.Shown = FadeSeconds * before!.Envelope;
            _glows[target] = glow;
            return null;
        }
    }

    /// <summary>
    /// Logs a rejection the first time the mod has this problem.
    /// </summary>
    void Tell(string owner, Problem problem)
    {
        lock (_lock)
        {
            if (!_told.Add((owner, problem))) return;
        }
        Log($"mod       {owner}: glow dropped: {Reason(problem)} (later drops for this reason are not logged)");
    }

    /// <summary>
    /// A problem in words, for the log (one <c>case</c> per line, so the translations check reads them as not shown).
    /// </summary>
    static string Reason(Problem problem)
    {
        switch (problem)
        {
            case Problem.BadTarget: return "no character, or an unknown target kind";
            case Problem.BadColour: return $"its colour is not finite in [0, {MaxColour}]";
            case Problem.BadSeconds: return $"its lifetime is not in (0, {MaxSeconds}] seconds";
            case Problem.BadStrength: return $"its strength is not in [0, {MaxStrength}]";
            case Problem.Full: return $"{MaxGlowing} targets already glow";
            case Problem.OffThread: return "set off the host's thread (from a tick system?)";
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
    }

    /// <summary>
    /// Once a frame on the host's thread: ages every glow by <paramref name="seconds"/> and drops those done.
    /// </summary>
    public void Advance(float seconds)
    {
        _hostThread = Environment.CurrentManagedThreadId;
        lock (_lock)
        {
            foreach (var (target, glow) in _glows)
            {
                glow.Shown += seconds;
                glow.Left -= seconds;
                if (glow.Left <= 0) _glows.Remove(target);
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="into"/> (cleared first) with every glow as it shows now.
    /// </summary>
    public void Current(List<(GlowTarget Target, GlowNow Glow)> into)
    {
        into.Clear();
        lock (_lock)
        {
            foreach (var (target, glow) in _glows)
            {
                float strength = glow.Strength * glow.Envelope;
                if (strength > 0) into.Add((target, new GlowNow(glow.Colour, strength)));
            }
        }
    }

    /// <summary>
    /// Drops the glows <paramref name="owner"/> set (the mod left the session).
    /// </summary>
    public void Drop(string owner)
    {
        lock (_lock)
        {
            foreach (var (target, glow) in _glows)
            {
                if (glow.Owner == owner) _glows.Remove(target);
            }
        }
    }

    /// <summary>
    /// Drops every glow (a new world: its characters are not the old world's).
    /// </summary>
    public void Clear()
    {
        lock (_lock) _glows.Clear();
    }
}
