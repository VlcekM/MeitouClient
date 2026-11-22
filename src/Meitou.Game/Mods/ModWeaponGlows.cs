using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// A character's weapon glow as drawn now (<see cref="ModWeaponGlows.Current"/>): the colour (rgb, 1 the full channel) and the strength after
/// the fade in and out.
/// </summary>
internal readonly record struct WeaponGlow(Vector3 Colour, float Strength);

/// <summary>
/// The code mods' display-only weapon glows (docs/modding.md "Weapon glow"): at most one a character, the newest request replacing the one before,
/// fading in and out over <see cref="FadeSeconds"/>, with an optional particle system played once on the held weapon (at its hilt end, +Y along
/// the blade, scaled to it) through the mods' effect sink, counted against <see cref="ModEffects.MaxAlive"/>. Set and aged on the host's thread;
/// the renderer reads <see cref="Current"/> each frame, the simulation never. A rejected request is logged once per mod and problem, and dropped.
/// </summary>
internal sealed class ModWeaponGlows(ModEffects effects)
{
    /// <summary>
    /// Why a request was dropped; each is logged once per mod.
    /// </summary>
    internal enum Problem { BadCharacter, BadColour, BadSeconds, BadStrength, UnknownEffect, Full, OffThread }

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
    /// The characters glowing at once; a request for another is rejected.
    /// </summary>
    public const int MaxGlowing = 256;
    /// <summary>
    /// The seconds a glow takes to come up and to die down.
    /// </summary>
    public const float FadeSeconds = 0.25f;
    /// <summary>
    /// The seconds an emitter is started or renewed for past its glow's end, so a clock's drift never stops it first; the glow stops it.
    /// </summary>
    const float EmitMargin = 1;

    /// <summary>
    /// One character's glow: who set it, its colour, strength and particle system, the seconds it has shown and those it has left, and its
    /// emitters while they play.
    /// </summary>
    sealed class Glow(string owner, Vector3 colour, float strength, string? effect, string? system, float shown, float left)
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
        /// The effect id the mod named (null: no particles).
        /// </summary>
        public string? Effect { get; } = effect;
        /// <summary>
        /// The particle system it resolved to.
        /// </summary>
        public string? System { get; } = system;
        /// <summary>
        /// The seconds it has shown (a replacement starts where the glow before stood).
        /// </summary>
        public float Shown { get; set; } = shown;
        /// <summary>
        /// The seconds it still shows.
        /// </summary>
        public float Left { get; set; } = left;
        /// <summary>
        /// Its playing effect (one at most) with its request; empty while the blade is not drawn.
        /// </summary>
        public List<(object Handle, ModEffectRequest Request)> Emitters { get; set; } = [];
        /// <summary>
        /// A newer request took these emitters over: they are renewed to its lifetime at the next frame.
        /// </summary>
        public bool Renew { get; set; }

        /// <summary>
        /// The fade: 0 to 1 over the first and last <see cref="FadeSeconds"/>.
        /// </summary>
        public float Envelope => Math.Clamp(MathF.Min(Shown, Left) / FadeSeconds, 0, 1);

        /// <summary>
        /// The same particles as <paramref name="other"/>'s, so its emitters can go on.
        /// </summary>
        public bool SameParticles(Glow other) => System is not null && System == other.System && Colour == other.Colour;
    }

    /// <summary>
    /// Guards the glows: a request may come from any host-side callback.
    /// </summary>
    readonly object _lock = new();
    /// <summary>
    /// The glows by character.
    /// </summary>
    readonly Dictionary<CharacterId, Glow> _glows = [];
    /// <summary>
    /// Emitters of glows that ended or were replaced, stopped at the next frame.
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
    /// Where a rejection's line goes (the game's log).
    /// </summary>
    public Action<string> Log { get; set; } = Console.Error.WriteLine;

    /// <summary>
    /// The characters glowing (fading out included).
    /// </summary>
    public int Count { get { lock (_lock) return _glows.Count; } }

    /// <summary>
    /// The particle emitters playing for the glows.
    /// </summary>
    public int Emitting { get { lock (_lock) return _glows.Values.Sum(g => g.Emitters.Count); } }

    /// <summary>
    /// Makes the weapon of <paramref name="on"/> glow, with <paramref name="effect"/> along its blade (null: none), replacing its glow; false when
    /// rejected (logged once per mod and problem). A replacement with the same particles and colour keeps its emitters going.
    /// </summary>
    public bool Start(string owner, CharacterId on, Vector3 colour, float seconds, float strength, string? effect = null)
    {
        string? system = null;
        var problem = Check(on, colour, seconds, strength)
            ?? Resolved(effect, out system)
            ?? Set(on, new Glow(owner, colour, strength, effect, system, 0, seconds));
        if (problem is null) return true;

        Tell(owner, problem.Value);
        return false;
    }

    /// <summary>
    /// Fades out the glow <paramref name="owner"/> set on <paramref name="on"/> and stops its particles; false when it set none there (or off the
    /// host's thread).
    /// </summary>
    public bool Stop(string owner, CharacterId on)
    {
        if (OffThread)
        {
            Tell(owner, Problem.OffThread);
            return false;
        }

        lock (_lock)
        {
            if (!_glows.TryGetValue(on, out var glow) || glow.Owner != owner) return false;

            glow.Left = MathF.Min(glow.Left, FadeSeconds * glow.Envelope);
            Retire(glow);
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
    Problem? Check(CharacterId on, Vector3 colour, float seconds, float strength)
    {
        if (OffThread) return Problem.OffThread;
        if (on.Slot < 0) return Problem.BadCharacter;
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
    /// The particle system of <paramref name="effect"/> as <see cref="ModContext.PlayEffect(string, Vector3, float, Vector3?)"/> resolves it;
    /// <see cref="Problem.UnknownEffect"/> when one is named and resolves to none.
    /// </summary>
    Problem? Resolved(string? effect, out string? system)
    {
        system = effect is null ? null : effects.System(effect);
        return effect is not null && system is null ? Problem.UnknownEffect : null;
    }

    /// <summary>
    /// Sets <paramref name="glow"/>, coming up from where the one it replaces stood and taking over its emitters when the particles are the same;
    /// <see cref="Problem.Full"/> for a new character past <see cref="MaxGlowing"/>.
    /// </summary>
    Problem? Set(CharacterId on, Glow glow)
    {
        lock (_lock)
        {
            bool replaces = _glows.TryGetValue(on, out var before);
            if (!replaces && _glows.Count >= MaxGlowing) return Problem.Full;

            if (replaces)
            {
                glow.Shown = FadeSeconds * before!.Envelope;
                TakeOver(glow, before);
            }
            _glows[on] = glow;
            return null;
        }
    }

    /// <summary>
    /// The new glow keeps the old one's emitters (renewed at the next frame) when its particles are the same; else they stop.
    /// </summary>
    void TakeOver(Glow glow, Glow before)
    {
        if (!glow.SameParticles(before) || before.Emitters.Count == 0)
        {
            Retire(before);
            return;
        }

        glow.Emitters = [.. before.Emitters.Select(e => (e.Handle, e.Request with { Owner = glow.Owner, Effect = glow.Effect! }))];
        glow.Renew = true;
    }

    /// <summary>
    /// Hands the glow's emitters to be stopped at the next frame.
    /// </summary>
    void Retire(Glow glow)
    {
        foreach (var (handle, _) in glow.Emitters) _stopping.Add(handle);
        glow.Emitters = [];
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
        Log($"mod       {owner}: weapon glow dropped: {Reason(problem)} (later drops for this reason are not logged)");
    }

    /// <summary>
    /// A problem in words, for the log (one <c>case</c> per line, so the translations check reads them as not shown).
    /// </summary>
    static string Reason(Problem problem)
    {
        switch (problem)
        {
            case Problem.BadCharacter: return "no character";
            case Problem.BadColour: return $"its colour is not finite in [0, {MaxColour}]";
            case Problem.BadSeconds: return $"its lifetime is not in (0, {MaxSeconds}] seconds";
            case Problem.BadStrength: return $"its strength is not in [0, {MaxStrength}]";
            case Problem.UnknownEffect: return "no EFFECT record or particle system has its effect id";
            case Problem.Full: return $"{MaxGlowing} characters already glow";
            case Problem.OffThread: return "set off the host's thread (from a tick system?)";
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
    }

    /// <summary>
    /// Once a frame on the host's thread: ages every glow by <paramref name="seconds"/> and drops those done, then, with a
    /// <paramref name="sink"/>, plays their particles: stops the retired emitters, starts a glow's along its blade once the blade is drawn, renews
    /// those a newer request took over and moves the others with the blade (all of a glow's stop while its blade is not drawn).
    /// </summary>
    public void Advance(float seconds, IModEffectSink? sink = null)
    {
        _hostThread = Environment.CurrentManagedThreadId;
        lock (_lock)
        {
            Age(seconds);
            if (sink is null) return;

            foreach (var handle in _stopping) sink.Renew(handle, 0);
            _stopping.Clear();
            foreach (var (on, glow) in _glows) Emit(on, glow, sink);
            effects.Reserved = _glows.Values.Sum(g => g.Emitters.Count);
        }
    }

    /// <summary>
    /// Takes <paramref name="seconds"/> off every glow, retiring and dropping those done.
    /// </summary>
    void Age(float seconds)
    {
        foreach (var (on, glow) in _glows)
        {
            glow.Shown += seconds;
            glow.Left -= seconds;
            if (glow.Left > 0) continue;

            Retire(glow);
            _glows.Remove(on);
        }
    }

    /// <summary>
    /// Plays one glow's particles for this frame (<see cref="Advance"/>).
    /// </summary>
    void Emit(CharacterId on, Glow glow, IModEffectSink sink)
    {
        if (glow.System is null) return;
        if (glow.Left <= FadeSeconds)
        {
            Retire(glow);   // fading out: its particles die down with it
            return;
        }

        if (glow.Emitters.Count == 0)
        {
            StartEmitters(on, glow, sink);
            return;
        }
        if (glow.Renew)
        {
            foreach (var (handle, _) in glow.Emitters) sink.Renew(handle, glow.Left + EmitMargin);
            glow.Renew = false;
        }
        foreach (var (handle, request) in glow.Emitters)
        {
            if (!sink.Track(handle, request))
            {
                Retire(glow);
                return;
            }
        }
    }

    /// <summary>
    /// Starts the glow's effect on the blade when it fits under the effects' cap; nothing while the blade is not drawn (it fails to start).
    /// The particles take the glow's colour, but for white (their own).
    /// </summary>
    void StartEmitters(CharacterId on, Glow glow, IModEffectSink sink)
    {
        if (!effects.Fits(1)) return;

        Vector3? tint = glow.Colour == Vector3.One ? null : glow.Colour;
        var request = new ModEffectRequest(glow.Owner, glow.Effect!, glow.System!, default, on, null, tint, glow.Left + EmitMargin, OnBlade: true);
        if (sink.Start(request) is { } handle) glow.Emitters.Add((handle, request));
    }

    /// <summary>
    /// Fills <paramref name="into"/> (cleared first) with every glow as it shows now.
    /// </summary>
    public void Current(List<(CharacterId On, WeaponGlow Glow)> into)
    {
        into.Clear();
        lock (_lock)
        {
            foreach (var (on, glow) in _glows)
            {
                float strength = glow.Strength * glow.Envelope;
                if (strength > 0) into.Add((on, new WeaponGlow(glow.Colour, strength)));
            }
        }
    }

    /// <summary>
    /// Drops the glows <paramref name="owner"/> set (the mod left the session); their emitters stop at the next frame.
    /// </summary>
    public void Drop(string owner)
    {
        lock (_lock)
        {
            foreach (var (on, glow) in _glows)
            {
                if (glow.Owner != owner) continue;

                Retire(glow);
                _glows.Remove(on);
            }
        }
    }

    /// <summary>
    /// Drops every glow and forgets their emitters (a new world: its characters and particles are not the old world's).
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _glows.Clear();
            _stopping.Clear();
            effects.Reserved = 0;
        }
    }
}
