using System.Diagnostics;
using System.Runtime.CompilerServices;
using Meitou.Data.Characters;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Compat.CharactersEmotions;

/// <summary>
/// The behaviour of the Characters Emotions plugin as a face controller (docs/compat-characters-emotions.md): per supported character a blink timer, a random normal
/// expression, and the highest-priority situation (knocked out, attacked, asleep, in combat, hurt, hungry, sitting) turned into shape-key weights that ease towards their
/// targets, plus the animation-driven expressions. Everything runs on the simulation clock of the frame, so a paused game holds the face still and a stepped one is repeatable.
/// Specified by the plugin's readmes and default INI text (MIT, Copyright (c) 2026 호옹이호옹); this is a re-implementation, not a copy of its code.
/// </summary>
sealed class FaceDriver(EmotionConfig config, CharactersEmotionsSettings settings, Action<string> log) : IFaceController
{
    /// <summary>The fade between two situations' faces, in seconds, where the plugin gives no time of its own (it gives one for the normal expression only).</summary>
    const float SituationFade = 0.25f;
    const float AttackedRise = 0.05f;
    /// <summary>The hunger value below which the character is hungry (BodyStatus.Hunger, 3 fed to 0 starving; docs/game/character-stats.md "Hunger").</summary>
    const float HungryBelow = 2.5f;

    readonly Dictionary<CharacterId, State> _states = [];
    readonly HashSet<CharacterId> _hits = [];
    readonly object _gate = new();
    readonly ConditionalWeakTable<IReadOnlySet<string>, Support> _supports = new();
    readonly HashSet<CharacterId> _reported = [];
    long _activeStamp;
    int _active;

    sealed record Support(bool Base, bool Anim);

    sealed class Channel
    {
        public string[] Keys = new string[8];
        public float[] Values = new float[8];
        public int Count;

        public void Clear() => Count = 0;

        public int Find(string key)
        {
            for (int i = 0; i < Count; i++) if (Keys[i] == key) return i;
            return -1;
        }

        public float Get(string key) => Find(key) is var i and >= 0 ? Values[i] : 0;

        public void Max(string key, float value)
        {
            int i = Find(key);
            if (i >= 0) { if (value > Values[i]) Values[i] = value; return; }
            if (Count == Keys.Length) { Array.Resize(ref Keys, Count * 2); Array.Resize(ref Values, Count * 2); }
            Keys[Count] = key;
            Values[Count++] = value;
        }

        public void Remove(int index)
        {
            Count--;
            Keys[index] = Keys[Count];
            Values[index] = Values[Count];
        }
    }

    sealed class State
    {
        public double Last = double.NaN, NextCheck, AttackedAt = -1e9, BlinkNext, BlinkStart = -1, NormalNext, NormalUntil;
        public long Seen;
        public bool Player;
        public float Distance;
        public ulong Rng;
        public int NormalIndex = -1;
        public bool Ko, Sleeping, Angry, Pain, Sad, Relax;
        public Situation Situation;
        public readonly Channel Current = new(), Target = new(), Anim = new(), Output = new();

        public float Random()
        {
            Rng ^= Rng << 13;
            Rng ^= Rng >> 7;
            Rng ^= Rng << 17;
            return (Rng >> 40) / (float)(1 << 24);
        }
    }

    /// <summary>MEITOU_EMOTIONS_TRACE=1 logs the situation, the animation layers and the weights of the player's characters when they change (diagnostics).</summary>
    static readonly bool Trace = Environment.GetEnvironmentVariable("MEITOU_EMOTIONS_TRACE") == "1";
    string _lastTrace = "";

    void TraceFrame(State st, CharacterSnapshot c, double now)
    {
        string line = $"{c.Name}: {st.Situation} layers=[{string.Join(", ", c.Animations.Where(l => l.Weight > 0.05f).Select(l => $"{l.Name}@{l.Weight:0.0}"))}] out=[{string.Join(", ", Enumerable.Range(0, st.Output.Count).Select(i => $"{st.Output.Keys[i]}={st.Output.Values[i]:0.00}"))}]";
        if (line == _lastTrace) return;
        _lastTrace = line;
        log($"t={now:0.00} {line}");
    }

    /// <summary>The hits the world resolved since the last frame: their victims show the attacked expression.</summary>
    public void NoteHits(IReadOnlyList<CombatHit> list)
    {
        lock (_gate)
            foreach (var hit in list) _hits.Add(hit.Victim);
    }

    public void Update(in FaceFrame frame, FaceWeights face)
    {
        var c = frame.Character;
        if (c.Body is not { } body || face.Available is not { } available) return;
        bool mine = c.IsPlayer && (!settings.OnlySelectedCharacter || c.Selected);
        if (!mine && !(settings.IncludeOthers && !settings.OnlySelectedCharacter)) return;
        var support = _supports.GetValue(available, set => new Support(
            config.Required.All(set.Contains), config.AnimRequired.Length > 0 && config.AnimRequired.All(set.Contains)));
        if (!support.Base)
        {
            if ((c.IsPlayer || Trace) && _reported.Add(c.Id)) log($"{c.Name}: no expressions, its body mesh lacks shape keys ({string.Join(", ", config.Required.Where(k => !available.Contains(k)))})");
            return;
        }

        double now = frame.Seconds;
        if (!Admit(c.Id, c.Name, c.IsPlayer, frame.CameraDistance, out var st)) return;
        if (double.IsNaN(st.Last) || now < st.Last - 0.5) Start(st, c, now, body);
        if (Trace && c.IsPlayer) TraceFrame(st, c, now);
        float dt = (float)Math.Clamp(now - st.Last, 0, 0.5);
        st.Last = now;

        if (now >= st.NextCheck)
        {
            Evaluate(st, c, body);
            st.NextCheck = now + (settings.CheckFrequency + st.Random() * settings.CheckRandomize) / 1000.0;
        }
        lock (_gate)
            if (_hits.Count > 0 && _hits.Remove(c.Id)) st.AttackedAt = now;

        // The situation: attacked rides over the others for a moment and fades.
        float attacked = 0;
        double since = now - st.AttackedAt, keep = settings.AttackedKeepTime / 1000.0, off = Math.Max(settings.AttackedOffTime / 1000.0, 0.01);
        if (!st.Ko && since >= 0 && since < keep + off) attacked = since < keep ? 1 : (float)(1 - (since - keep) / off);
        var situation = st.Situation;
        int shown = (int)(attacked > 0 ? Situation.Attacked : situation);

        // Animation expression: the playing layer with a section, at its frame.
        var anim = st.Anim;
        anim.Clear();
        AnimEmotion? driven = null;
        if (settings.AllowAnimEmotions && support.Anim && FindAnimation(c) is var (emotion, layer)) driven = AnimWeights(emotion, layer, shown, anim);

        var target = st.Target;
        target.Clear();
        bool replaceDefault = driven is { OnlyAnim: true } && anim.Count > 0;
        if (!replaceDefault)
        {
            if (situation == Situation.Normal) NormalExpression(st, now, target);
            else AddExpression(target, config.BySituation[(int)situation], 1 - attacked);
            if (attacked > 0) AddExpression(target, config.BySituation[(int)Situation.Attacked], attacked);
        }

        // Ease the current weights towards the targets.
        float fade = attacked > 0 ? AttackedRise : situation == Situation.Normal ? settings.NormalMorphTime / 1000f : SituationFade;
        var current = st.Current;
        for (int i = 0; i < target.Count; i++) if (current.Find(target.Keys[i]) < 0) current.Max(target.Keys[i], 0);
        for (int i = current.Count - 1; i >= 0; i--)
        {
            float goal = target.Get(current.Keys[i]);
            float step = fade <= 0 ? 1 : dt / fade;
            current.Values[i] += Math.Clamp(goal - current.Values[i], -step, step);
            if (current.Values[i] <= 0.001f && goal == 0) current.Remove(i);
        }

        // Blinking: not asleep or out cold, not while an animation expression ignores it.
        float blink = Blink(st, now, blocked: situation is Situation.Sleep or Situation.KnockOut || driven is { IgnoreBlink: true });

        var output = st.Output;
        output.Clear();
        for (int i = 0; i < current.Count; i++) output.Max(current.Keys[i], current.Values[i]);
        for (int i = 0; i < anim.Count; i++) output.Max(anim.Keys[i], anim.Values[i]);
        if (blink > 0)
            for (int i = 0; i < config.Blinking.Keys.Length; i++) output.Max(config.Blinking.Keys[i], blink * config.Blinking.Weights[i]);

        foreach (string key in config.InvalidKeys)
            if (available.Contains(key)) face.Set(key, 0);
        for (int i = 0; i < output.Count; i++) face.Set(output.Keys[i], output.Values[i]);
    }

    /// <summary>The state of a character: made when it is first handled, if fewer than <see cref="CharactersEmotionsSettings.NumberOfCharacters"/> were handled lately.</summary>
    bool Admit(CharacterId id, string name, bool player, float distance, out State st)
    {
        long stamp = Stopwatch.GetTimestamp();
        if (stamp - _activeStamp > Stopwatch.Frequency / 2)
        {
            _activeStamp = stamp;
            _active = 0;
            List<CharacterId>? gone = null;
            foreach (var (key, state) in _states)
            {
                if (stamp - state.Seen > Stopwatch.Frequency * 2) (gone ??= []).Add(key);
                else _active++;
            }
            if (gone is not null) foreach (var key in gone) _states.Remove(key);
        }
        if (_states.TryGetValue(id, out st!)) { (st.Seen, st.Distance) = (stamp, distance); return true; }
        if (!player && _active >= settings.NumberOfCharacters)
        {
            // Over the limit: the nearest to the camera keep their faces; this one takes the place of the farthest other's if it is nearer.
            CharacterId? farthest = null;
            float far = distance;
            foreach (var (key, state) in _states)
                if (!state.Player && state.Distance > far) (farthest, far) = (key, state.Distance);
            if (farthest is not { } evicted) return false;
            _states.Remove(evicted);
            _active--;
        }
        _active++;
        st = new State { Seen = stamp, Player = player, Distance = distance, Rng = ((ulong)id.Slot * 2654435761UL + (ulong)id.Generation * 40503UL + 0x9E3779B97F4A7C15UL) | 1 };
        _states[id] = st;
        if (_reported.Add(id)) log($"{name}: expressions on");
        return true;
    }

    void Start(State st, CharacterSnapshot c, double now, BodyStatus body)
    {
        st.Current.Clear();
        Evaluate(st, c, body);
        st.NextCheck = now;
        st.AttackedAt = -1e9;
        st.BlinkStart = -1;
        st.BlinkNext = now + st.Random() * settings.BlinkingFrequency / 1000.0;
        st.NormalIndex = -1;
        st.NormalNext = now + (settings.NormalFrequency + st.Random() * settings.NormalRandomizer) / 1000.0;
    }

    static readonly string[] BedAnimations = ["sleepinbed", "sleeponfloor"], SitAnimations = ["sitting chair", "sitting idle"];

    void Evaluate(State st, CharacterSnapshot c, BodyStatus body)
    {
        st.Ko = body.Unconscious || body.Dead;
        st.Sleeping = c.InBed || Playing(c, BedAnimations);
        st.Angry = c.InCombat || c.CombatMode;
        st.Pain = !body.Dead && body.WorstPart < settings.HurtBoundary;
        st.Sad = body.Hunger < HungryBelow;
        st.Relax = Playing(c, SitAnimations);
        st.Situation = st.Ko ? Situation.KnockOut : st.Sleeping ? Situation.Sleep : st.Angry ? Situation.Angry : st.Pain ? Situation.Pain
            : st.Sad ? Situation.Sad : st.Relax ? Situation.Relax : Situation.Normal;
    }

    static bool Playing(CharacterSnapshot c, string[] names)
    {
        var layers = c.Animations;
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].Weight < 0.5f) continue;
            foreach (string name in names)
                if (string.Equals(layers[i].Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    (AnimEmotion, AnimationLayer)? FindAnimation(CharacterSnapshot c)
    {
        (AnimEmotion, AnimationLayer)? best = null;
        float weight = 0;
        var layers = c.Animations;
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.Weight > weight && config.ByAnimation.TryGetValue(layer.Name, out var emotion))
            {
                best = (emotion, layer);
                weight = layer.Weight;
            }
        }
        return best;
    }

    /// <summary>
    /// The weights of an animation expression at the layer's frame (clip time over clip length, times the section's last frame) into <paramref name="into"/>; null
    /// when a more important default expression (the section's EmotionPriority) takes the face.
    /// </summary>
    static AnimEmotion? AnimWeights(AnimEmotion emotion, AnimationLayer layer, int shownSituation, Channel into)
    {
        if (shownSituation >= emotion.Priority) return null;
        float t = layer.Length > 0 ? layer.Time / layer.Length : 0;
        t -= MathF.Floor(t);
        float frame = t * emotion.TotalFrames;
        foreach (var track in emotion.Tracks)
            if (track.At(frame) is { } w) into.Max(track.Key, w);
        return emotion;
    }

    static void AddExpression(Channel target, Expression expression, float factor)
    {
        if (factor <= 0) return;
        for (int i = 0; i < expression.Keys.Length; i++) target.Max(expression.Keys[i], expression.Weights[i] * factor);
    }

    /// <summary>The random normal expression: one of the EmotionNormalN every so often, kept for a while, then dropped; only while nothing more important shows.</summary>
    void NormalExpression(State st, double now, Channel target)
    {
        if (config.Normal.Length == 0) return;
        if (st.NormalIndex >= 0 && now >= st.NormalUntil)
        {
            st.NormalIndex = -1;
            st.NormalNext = now + (settings.NormalFrequency + st.Random() * settings.NormalRandomizer) / 1000.0;
        }
        else if (st.NormalIndex < 0 && now >= st.NormalNext)
        {
            st.NormalIndex = Math.Min((int)(st.Random() * config.Normal.Length), config.Normal.Length - 1);
            st.NormalUntil = now + settings.NormalKeepTime / 1000.0;
        }
        if (st.NormalIndex >= 0) AddExpression(target, config.Normal[st.NormalIndex], 1);
    }

    /// <summary>The blink's weight now (0 closed-open triangle over BlinkingTime), starting the next one when due.</summary>
    float Blink(State st, double now, bool blocked)
    {
        if (blocked)
        {
            st.BlinkStart = -1;
            if (now >= st.BlinkNext) st.BlinkNext = now + 0.1;
            return 0;
        }
        if (st.BlinkStart < 0)
        {
            if (now < st.BlinkNext) return 0;
            st.BlinkStart = now;
        }
        double span = Math.Max(settings.BlinkingTime / 1000.0, 0.02);
        double p = (now - st.BlinkStart) / span;
        if (p >= 1)
        {
            st.BlinkStart = -1;
            st.BlinkNext = now + (settings.BlinkingFrequency + st.Random() * settings.BlinkingRandomizer) / 1000.0;
            return 0;
        }
        return (float)(p < 0.5 ? p * 2 : (1 - p) * 2);
    }
}
