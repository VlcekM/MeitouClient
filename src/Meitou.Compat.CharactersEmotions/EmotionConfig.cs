using System.Globalization;

namespace Meitou.Compat.CharactersEmotions;

/// <summary>One expression: shape keys and their weights (0..1), as an <c>[EmotionX]</c> section lists them.</summary>
sealed record Expression(string[] Keys, float[] Weights)
{
    public static readonly Expression Empty = new([], []);
}

/// <summary>One shape key of an animation expression: its weight at control frames (ascending), linearly interpolated between them.</summary>
sealed record AnimTrack(string Key, int[] Frames, float[] Weights)
{
    /// <summary>The weight at <paramref name="frame"/>; null before the first control frame or after the last (and at frame 0, which only anchors the interpolation).</summary>
    public float? At(float frame)
    {
        if (frame <= 0 || frame < Frames[0] || frame > Frames[^1]) return null;
        for (int i = 1; i < Frames.Length; i++)
        {
            if (frame > Frames[i]) continue;
            float span = Frames[i] - Frames[i - 1];
            return span <= 0 ? Weights[i] : Weights[i - 1] + (Weights[i] - Weights[i - 1]) * (frame - Frames[i - 1]) / span;
        }
        return Weights[^1];
    }
}

/// <summary>An <c>[animName,...]</c> section of the animation INI.</summary>
sealed record AnimEmotion(string[] Names, int TotalFrames, int Priority, bool IgnoreBlink, bool OnlyAnim, AnimTrack[] Tracks);

/// <summary>The situations in the order of their priority (the plugin's: Normal lowest, KnockOut highest).</summary>
enum Situation { Normal, Relax, Sad, Pain, Angry, Sleep, Attacked, KnockOut }

/// <summary>
/// The plugin's two configuration files (<c>CharactersEmotions_emotions.ini</c>, <c>CharactersEmotions_animemotions.ini</c>): the expression of each situation as shape keys
/// and weights, the keys to zero before expressing, and the animation-driven expressions. Invalid entries are skipped with a log line, as the plugin does.
/// </summary>
sealed class EmotionConfig
{
    public Expression Blinking = Expression.Empty;
    public Expression[] Normal = [];
    public readonly Expression[] BySituation = new Expression[8];
    public string[] InvalidKeys = [];
    /// <summary>Every shape key the situations use: a character must have all of them to be supported.</summary>
    public string[] Required = [];
    /// <summary>The keys the animation expressions need (<c>RequirementShapeKey</c>); a character lacking one has no animation expressions.</summary>
    public string[] AnimRequired = [];
    public readonly Dictionary<string, AnimEmotion> ByAnimation = new(StringComparer.OrdinalIgnoreCase);

    public static EmotionConfig Load(string? pluginFolder, Action<string> log)
    {
        var config = new EmotionConfig();
        config.ReadEmotions(Read(pluginFolder, "CharactersEmotions_emotions.ini", Defaults.Emotions, log), log);
        config.ReadAnimations(Read(pluginFolder, "CharactersEmotions_animemotions.ini", Defaults.AnimEmotions, log), log);
        return config;
    }

    static Ini Read(string? folder, string file, string fallback, Action<string> log)
    {
        string? path = folder is null ? null : Path.Combine(folder, file);
        if (path is not null && File.Exists(path))
        {
            try
            {
                var ini = Ini.Parse(File.ReadAllLines(path));
                log($"{file}: read from {path}");
                return ini;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"{file}: {e.Message}; defaults used");
            }
        }
        return Ini.Parse(fallback.Split('\n'));
    }

    void ReadEmotions(Ini ini, Action<string> log)
    {
        InvalidKeys = Split(ini.Section("Master")?.GetValueOrDefault("InvalidShapeKey"));
        Blinking = ReadExpression(ini.Section("Blinking"), "Blinking", log);
        var normal = new List<Expression>();
        for (int n = 1; ini.Section("EmotionNormal" + n) is { } section; n++)
            if (ReadExpression(section, "EmotionNormal" + n, log) is { Keys.Length: > 0 } e) normal.Add(e);
        Normal = [.. normal];
        BySituation[(int)Situation.Normal] = Expression.Empty;
        foreach (var (situation, name) in new[]
        {
            (Situation.Relax, "EmotionRelax"), (Situation.Sad, "EmotionSad"), (Situation.Pain, "EmotionPain"), (Situation.Angry, "EmotionAngry"),
            (Situation.Sleep, "EmotionSleep"), (Situation.Attacked, "EmotionAttacked"), (Situation.KnockOut, "EmotionKnockOut"),
        })
            BySituation[(int)situation] = ReadExpression(ini.Section(name), name, log);
        Required = [.. Blinking.Keys.Concat(Normal.SelectMany(e => e.Keys)).Concat(BySituation.SelectMany(e => e.Keys)).Distinct(StringComparer.Ordinal)];
    }

    static Expression ReadExpression(IReadOnlyDictionary<string, string>? section, string name, Action<string> log)
    {
        if (section is null) { log($"[{name}] is missing; skipped"); return Expression.Empty; }
        var keys = new List<string>();
        var weights = new List<float>();
        for (int n = 1; section.TryGetValue("ShapeKey" + n, out var key) && key.Length > 0; n++)
        {
            float amount = 100;
            if (section.TryGetValue($"ShapeKey{n}Amount", out var text) && !TryNumber(text, out amount))
            {
                log($"[{name}] ShapeKey{n}Amount='{text}' is invalid; skipped");
                continue;
            }
            keys.Add(key);
            weights.Add(Math.Clamp(amount, 0, 100) / 100);
        }
        if (keys.Count == 0) log($"[{name}] contains no usable ShapeKey entries");
        return new Expression([.. keys], [.. weights]);
    }

    void ReadAnimations(Ini ini, Action<string> log)
    {
        AnimRequired = Split(ini.Section("Master")?.GetValueOrDefault("RequirementShapeKey"));
        foreach (var (name, keys) in ini.Sections)
        {
            if (name.Length == 0 || string.Equals(name, "Master", StringComparison.OrdinalIgnoreCase)) continue;
            string[] names = Split(name);
            if (names.Length == 0) { log("an animation section has no animName; skipped"); continue; }
            if (!keys.TryGetValue("TotalAnimFrame", out var total) || !int.TryParse(total, out int frames) || frames <= 0) { log($"[{name}] TotalAnimFrame is invalid; skipped"); continue; }
            int priority = keys.TryGetValue("EmotionPriority", out var p) && int.TryParse(p, out int pv) && pv is >= 0 and <= 7 ? pv : 0;
            bool ignoreBlink = keys.TryGetValue("IgnoreBlink", out var ib) && bool.TryParse(ib, out bool ibv) && ibv;
            bool only = keys.TryGetValue("OnlyAnimEmotion", out var oa) && bool.TryParse(oa, out bool oav) && oav;
            var tracks = new List<AnimTrack>();
            for (int n = 1; keys.TryGetValue("ShapeKey" + n, out var key) && key.Length > 0; n++)
            {
                int[]? f = keys.TryGetValue($"ShapeKey{n}Frame", out var ft) ? Ints(ft) : null;
                float[]? a = keys.TryGetValue($"ShapeKey{n}Amount", out var at) ? Floats(at) : null;
                if (f is null || a is null || f.Length != a.Length || f.Length == 0) { log($"[{name}] ShapeKey{n} Amount/Frame lists are invalid or have different lengths; skipped"); continue; }
                bool ascending = f[0] >= 0;
                for (int i = 1; i < f.Length; i++) ascending &= f[i] > f[i - 1];
                if (!ascending) { log($"[{name}] ShapeKey{n} frames must be non-negative and strictly ascending; skipped"); continue; }
                tracks.Add(new AnimTrack(key, f, [.. a.Select(v => Math.Clamp(v, 0, 100) / 100)]));
            }
            if (tracks.Count == 0) { log($"[{name}] contains no usable animation Shape Key tracks; skipped"); continue; }
            var emotion = new AnimEmotion(names, frames, priority, ignoreBlink, only, [.. tracks]);
            foreach (string animation in names) ByAnimation[animation] = emotion;
        }
    }

    static string[] Split(string? text) =>
        text is null ? [] : [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    static bool TryNumber(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    static float[]? Floats(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var result = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!TryNumber(parts[i], out result[i])) return null;
        return result;
    }

    static int[]? Ints(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i])) return null;
        return result;
    }
}
