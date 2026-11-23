using System.Numerics;

namespace Meitou.Modding;

/// <summary>
/// How a code mod's ribbon trail looks (<see cref="ModContext.KeepTrail"/>, docs/modding.md "Trails").
/// </summary>
public sealed record TrailStyle
{
    /// <summary>
    /// 0: a ribbon through the anchor's point, facing the camera, <see cref="Width"/> wide. Above 0: a band swept by the anchor's +Y axis from its
    /// origin this far (times the anchor's stretch; on <see cref="EffectAnchor.Weapon"/> 8.5 reaches the tip), as a blade sweeps.
    /// </summary>
    public float Span { get; init; }

    /// <summary>
    /// A point ribbon's width at the head, in units.
    /// </summary>
    public float Width { get; init; } = 2;

    /// <summary>
    /// A point ribbon's width at the tail (null: <see cref="Width"/>).
    /// </summary>
    public float? EndWidth { get; init; }

    /// <summary>
    /// How long a place the anchor passed stays in the trail, in seconds: the trail's length in time.
    /// </summary>
    public float Seconds { get; init; } = 0.3f;

    /// <summary>
    /// The colour at the head (rgb up to 4, alpha 0 to 1).
    /// </summary>
    public Vector4 Colour { get; init; } = Vector4.One;

    /// <summary>
    /// The colour at the tail (null: <see cref="Colour"/> with alpha 0, so the trail fades out).
    /// </summary>
    public Vector4? EndColour { get; init; }

    /// <summary>
    /// A particle material (an Ogre <c>.material</c> of the particles, the base game's or a mod's) whose texture, blend and colour draw the trail;
    /// null: Meitou's soft additive streak.
    /// </summary>
    public string? Material { get; init; }
}

/// <summary>
/// One piece of a trail as drawn this frame (<see cref="ModTrails.Advance"/>): between the newer sample A and the older B, each a base and a tip (the
/// same point for a point ribbon), the widths there, the colour at its middle, where along the trail its middle is (0 head, 1 tail) and the
/// material.
/// </summary>
internal readonly record struct TrailQuad(Vector3 BaseA, Vector3 TipA, Vector3 BaseB, Vector3 TipB, float WidthA, float WidthB, Vector4 Colour,
    float Along, string? Material);

/// <summary>
/// The code mods' display-only ribbon trails (docs/modding.md "Trails"): kept by mod and anchor from host-side callbacks, sampled once a frame on the
/// host's thread where their anchors are drawn (<see cref="IEffectFrameSource"/>) and handed to the particles as quads. At most
/// <see cref="MaxTrails"/> at once and <see cref="MaxSamples"/> samples each; the simulation never reads them. A rejected request is logged once
/// per mod and problem, and dropped.
/// </summary>
internal sealed class ModTrails
{
    /// <summary>
    /// Why a request was dropped beyond the anchor's own problems; each is logged once per mod.
    /// </summary>
    internal enum Problem { BadStyle, BadSeconds, Full, OffThread }

    /// <summary>
    /// The trails alive at once (emitting or fading out).
    /// </summary>
    public const int MaxTrails = 64;
    /// <summary>
    /// The samples a trail keeps; the oldest goes first.
    /// </summary>
    public const int MaxSamples = 64;
    /// <summary>
    /// The longest a trail may emit per request, in seconds.
    /// </summary>
    public const float MaxSeconds = 30;
    /// <summary>
    /// The longest a sample may stay (a style's <see cref="TrailStyle.Seconds"/>).
    /// </summary>
    public const float MaxLife = 5;
    /// <summary>
    /// The largest width and span.
    /// </summary>
    public const float MaxSize = 1000;
    /// <summary>
    /// The largest colour channel (alpha at most 1).
    /// </summary>
    public const float MaxColour = 4;

    /// <summary>
    /// A place of the anchor: its base and tip, its age and whether it starts a run (the anchor was lost before it).
    /// </summary>
    struct Sample
    {
        public Vector3 Base, Tip;
        public float Age;
        public bool Starts;
    }

    /// <summary>
    /// One trail: who keeps it, where, how it looks, the seconds it still emits and has been alive, and its samples, newest last.
    /// </summary>
    sealed class Trail(string owner, EffectAnchor anchor, TrailStyle style, float left)
    {
        public string Owner { get; } = owner;
        public EffectAnchor Anchor { get; set; } = anchor;
        public TrailStyle Style { get; set; } = style;
        public float Left { get; set; } = left;
        public double Age { get; set; }
        public bool Lost { get; set; } = true;
        public List<Sample> Samples { get; } = [];

        /// <summary>
        /// Whether it is <paramref name="owner"/>'s trail on <paramref name="anchor"/> (its scale aside).
        /// </summary>
        public bool Keeps(string owner, in EffectAnchor anchor) => Owner == owner && Anchor.Key == anchor.Key;
    }

    /// <summary>
    /// Guards the trails: a request may come from any host-side callback.
    /// </summary>
    readonly object _lock = new();
    /// <summary>
    /// The trails, oldest first.
    /// </summary>
    readonly List<Trail> _trails = [];
    /// <summary>
    /// The mod and problem pairs logged already (the anchor's problems by their <see cref="ModEffects.Problem"/> name).
    /// </summary>
    readonly HashSet<(string Owner, string Problem)> _told = [];
    /// <summary>
    /// The thread of the last <see cref="Advance"/> (the host's); 0 before the first.
    /// </summary>
    volatile int _hostThread;

    /// <summary>
    /// Where a rejection's line goes (the game's log).
    /// </summary>
    public Action<string> Log { get; set; } = Console.Error.WriteLine;

    /// <summary>
    /// The trails alive (emitting or fading out).
    /// </summary>
    public int Count { get { lock (_lock) return _trails.Count; } }

    /// <summary>
    /// Keeps <paramref name="owner"/>'s trail on <paramref name="anchor"/> emitting for <paramref name="seconds"/> from now in
    /// <paramref name="style"/>, starting it when there is none; false when rejected (logged once per mod and problem).
    /// </summary>
    public bool Keep(string owner, in EffectAnchor anchor, TrailStyle style, float seconds)
    {
        if (Check(anchor, style, seconds) is { } problem)
        {
            Tell(owner, problem);
            return false;
        }

        lock (_lock)
        {
            if (Renew(owner, anchor, style, seconds)) return true;
            if (_trails.Count < MaxTrails)
            {
                _trails.Add(new Trail(owner, anchor, style, seconds));
                return true;
            }
        }

        Tell(owner, nameof(Problem.Full));
        return false;
    }

    /// <summary>
    /// The kept trail of the key takes the new anchor scale, style and lifetime; false when there is none.
    /// </summary>
    bool Renew(string owner, in EffectAnchor anchor, TrailStyle style, float seconds)
    {
        foreach (var t in _trails)
        {
            if (!t.Keeps(owner, anchor)) continue;

            (t.Anchor, t.Style, t.Left) = (anchor, style, seconds);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Stops <paramref name="owner"/>'s trail on <paramref name="anchor"/> emitting (what it drew fades out); false when it keeps none there.
    /// </summary>
    public bool Stop(string owner, in EffectAnchor anchor)
    {
        if (OffThread)
        {
            Tell(owner, nameof(Problem.OffThread));
            return false;
        }

        lock (_lock)
        {
            var key = anchor;
            var trail = _trails.FirstOrDefault(t => t.Keeps(owner, key) && t.Left > 0);
            if (trail is null) return false;

            trail.Left = 0;
            return true;
        }
    }

    /// <summary>
    /// The request comes from another thread than the host's.
    /// </summary>
    bool OffThread => _hostThread != 0 && Environment.CurrentManagedThreadId != _hostThread;

    /// <summary>
    /// The problem of a request, or null.
    /// </summary>
    string? Check(in EffectAnchor anchor, TrailStyle? style, float seconds)
    {
        if (OffThread) return nameof(Problem.OffThread);
        if (ModEffects.AnchorProblem(anchor) is { } problem) return problem.ToString();
        if (style is null || !Usable(style)) return nameof(Problem.BadStyle);
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > MaxSeconds) return nameof(Problem.BadSeconds);
        return null;
    }

    /// <summary>
    /// A style whose sizes, life and colours are in range and whose material, if named, is not empty.
    /// </summary>
    static bool Usable(TrailStyle s) =>
        s.Span is >= 0 and <= MaxSize && s.Width is > 0 and <= MaxSize && (s.EndWidth is not { } w || w is >= 0 and <= MaxSize)
        && s.Seconds is > 0 and <= MaxLife && ColourOk(s.Colour) && (s.EndColour is not { } c || ColourOk(c))
        && (s.Material is null || s.Material.Length > 0);

    /// <summary>
    /// Every channel in [0, <see cref="MaxColour"/>], alpha in [0, 1] (NaN is not).
    /// </summary>
    static bool ColourOk(Vector4 c) => c.X is >= 0 and <= MaxColour && c.Y is >= 0 and <= MaxColour && c.Z is >= 0 and <= MaxColour
        && c.W is >= 0 and <= 1;

    /// <summary>
    /// Logs a rejection the first time the mod has this problem.
    /// </summary>
    void Tell(string owner, string problem)
    {
        lock (_lock)
        {
            if (!_told.Add((owner, problem))) return;
        }
        Log($"mod       {owner}: trail dropped: {Reason(problem)} (later drops for this reason are not logged)");
    }

    /// <summary>
    /// A problem in words, for the log (one <c>case</c> per line, so the translations check reads them as not shown).
    /// </summary>
    static string Reason(string problem)
    {
        switch (problem)
        {
            case nameof(Problem.BadStyle): return "its style is missing or out of range";
            case nameof(Problem.BadSeconds): return $"its lifetime is not in (0, {MaxSeconds}] seconds";
            case nameof(Problem.Full): return $"{MaxTrails} trails are already kept";
            case nameof(Problem.OffThread): return "asked off the host's thread (from a tick system?)";
            default: return ModEffects.AnchorReason(Enum.Parse<ModEffects.Problem>(problem));
        }
    }

    /// <summary>
    /// Once a frame on the host's thread: ages the trails and their samples by <paramref name="seconds"/>, samples the emitting ones where their
    /// anchors are (<paramref name="frames"/>), drops the finished ones and fills <paramref name="into"/> (cleared first) with the quads to draw.
    /// </summary>
    public void Advance(float seconds, IEffectFrameSource frames, List<TrailQuad> into)
    {
        _hostThread = Environment.CurrentManagedThreadId;
        into.Clear();
        lock (_lock)
        {
            foreach (var t in _trails)
            {
                Age(t, seconds);
                if (t.Left > 0) Take(t, frames);
                Quads(t, into);
            }
            _trails.RemoveAll(t => t.Left <= 0 && t.Samples.Count == 0);
        }
    }

    /// <summary>
    /// Ages a trail and its samples, dropping those older than its style's life.
    /// </summary>
    static void Age(Trail t, float seconds)
    {
        t.Left -= seconds;
        t.Age += seconds;
        var samples = t.Samples;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            s.Age += seconds;
            samples[i] = s;
        }
        samples.RemoveAll(s => s.Age > t.Style.Seconds);
    }

    /// <summary>
    /// Adds where the trail's anchor is now; a lost anchor adds nothing and the next place starts a new run.
    /// </summary>
    static void Take(Trail t, IEffectFrameSource frames)
    {
        if (EffectFrames.Locate(t.Anchor, frames, t.Age) is not { } frame)
        {
            t.Lost = true;
            return;
        }

        var tip = frame.At + Vector3.Transform(Vector3.UnitY * t.Style.Span * frame.Stretch, frame.Turn);
        if (t.Samples.Count >= MaxSamples) t.Samples.RemoveAt(0);
        t.Samples.Add(new Sample { Base = frame.At, Tip = tip, Age = 0, Starts = t.Lost });
        t.Lost = false;
    }

    /// <summary>
    /// The quads between each sample and the one before it in its run, newest first.
    /// </summary>
    static void Quads(Trail t, List<TrailQuad> into)
    {
        var s = t.Style;
        var samples = t.Samples;
        for (int i = samples.Count - 1; i > 0; i--)
        {
            var (a, b) = (samples[i], samples[i - 1]);
            if (a.Starts) continue;

            float ka = a.Age / s.Seconds, kb = b.Age / s.Seconds, mid = (ka + kb) / 2;
            into.Add(new TrailQuad(a.Base, a.Tip, b.Base, b.Tip, WidthAt(s, ka), WidthAt(s, kb), ColourAt(s, mid), mid, s.Material));
        }
    }

    /// <summary>
    /// A point ribbon's width at <paramref name="k"/> along its life (0 head, 1 tail).
    /// </summary>
    static float WidthAt(TrailStyle s, float k) => s.Width + ((s.EndWidth ?? s.Width) - s.Width) * Math.Clamp(k, 0, 1);

    /// <summary>
    /// The colour at <paramref name="k"/> along its life (0 head, 1 tail).
    /// </summary>
    static Vector4 ColourAt(TrailStyle s, float k) => Vector4.Lerp(s.Colour, s.EndColour ?? s.Colour with { W = 0 }, Math.Clamp(k, 0, 1));

    /// <summary>
    /// Ends what <paramref name="owner"/> keeps (the mod left the session).
    /// </summary>
    public void Drop(string owner)
    {
        lock (_lock) _trails.RemoveAll(t => t.Owner == owner);
    }

    /// <summary>
    /// Forgets every trail (a new world).
    /// </summary>
    public void Clear()
    {
        lock (_lock) _trails.Clear();
    }
}
