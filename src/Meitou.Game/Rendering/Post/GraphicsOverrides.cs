using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The code mods' overrides of the look settings (docs/modding.md "Graphics overrides"). The overrides are laid over the options only while
/// the world is drawn (<see cref="Apply"/> before, <see cref="Restore"/> after): between frames the options hold the player's values, so the
/// settings menu shows them, a change the player makes there stays, and nothing of a mod's is saved. The settings are the post chain's look:
/// the sliders <c>WorldFrame.AddLookSliders</c> makes and the camera effects' and grade's switches, each a plain field of
/// <see cref="PostOptions"/>, so laying a value over one and taking it back costs nothing.
/// </summary>
sealed class GraphicsOverrides
{
    /// <summary>
    /// One setting: its key, its range, and how it is read and set on the options it was made for.
    /// </summary>
    sealed record Setting(string Key, float Min, float Max, Func<float> Get, Action<float> Set);

    /// <summary>
    /// The labels of the tone map's and the night grade's switches (<c>Enhancements</c> "tonemap" and "nightgrade"), which key their saved values.
    /// </summary>
    const string ToneMappingSwitch = "Tone map and grade", NightGradeSwitch = "Night grading";

    /// <summary>
    /// The settings' keys, in the order the settings are listed.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } = [.. Settings(new PostOptions()).Select(s => s.Key)];

    /// <summary>
    /// Guards the overrides: mods set them from the host's thread, the frame reads them where it is drawn.
    /// </summary>
    readonly Lock _lock = new();

    /// <summary>
    /// The overrides held, in the order they were first set: a later one wins over an earlier one of the same setting.
    /// </summary>
    readonly List<(string Mod, string Key, float Value)> _overrides = [];

    /// <summary>
    /// The settings on the game's options, by key; empty until <see cref="Bind"/>.
    /// </summary>
    Dictionary<string, Setting> _settings = [];

    /// <summary>
    /// The player's values <see cref="Apply"/> laid overrides over this frame, for <see cref="Restore"/>.
    /// </summary>
    readonly List<(Setting Setting, float Player)> _held = [];

    /// <summary>
    /// The settings a mod may override, on <paramref name="options"/>.
    /// </summary>
    static List<Setting> Settings(PostOptions options)
    {
        var sliders = new List<Slider>();
        WorldFrame.AddLookSliders(sliders, options);
        var settings = sliders.Select(s => new Setting(s.Label, s.Min, s.Max, s.Get, s.Set)).ToList();
        settings.Add(Switch(WorldFrame.BloomSwitch, () => options.Bloom, v => options.Bloom = v));
        settings.Add(Switch(WorldFrame.DepthOfFieldSwitch, () => options.DepthOfField, v => options.DepthOfField = v));
        settings.Add(Switch(WorldFrame.VignetteSwitch, () => options.Vignette, v => options.Vignette = v));
        settings.Add(Switch(WorldFrame.FilmGrainSwitch, () => options.FilmGrain, v => options.FilmGrain = v));
        settings.Add(Switch(WorldFrame.LetterboxSwitch, () => options.Letterbox, v => options.Letterbox = v));
        settings.Add(Switch(WorldFrame.CameraEffectsSwitch, () => options.CameraEffectsEverywhere, v => options.CameraEffectsEverywhere = v));
        settings.Add(Switch(ToneMappingSwitch, () => options.ToneMapping, v => options.ToneMapping = v));
        settings.Add(Switch(NightGradeSwitch, () => options.NightGrade, v => options.NightGrade = v));

        return settings;
    }

    /// <summary>
    /// A switch as a setting of 0 or 1, under the key its saved value has.
    /// </summary>
    static Setting Switch(string label, Func<bool> get, Action<bool> set) =>
        new(SettingsMenu.SwitchKey(label), 0, 1, () => get() ? 1 : 0, v => set(v >= 0.5f));

    /// <summary>
    /// Takes the game's options: from here on the overrides are laid over them, and <see cref="Player"/> reads them.
    /// </summary>
    public void Bind(PostOptions options)
    {
        var settings = Settings(options).ToDictionary(s => s.Key, StringComparer.Ordinal);
        lock (_lock) _settings = settings;
    }

    /// <summary>
    /// The player's value of a setting; null for an unknown one or before <see cref="Bind"/>.
    /// </summary>
    public float? Player(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock)
        {
            if (!_settings.TryGetValue(key, out var setting)) return null;

            int held = _held.FindIndex(h => ReferenceEquals(h.Setting, setting));
            return held >= 0 ? _held[held].Player : setting.Get();
        }
    }

    /// <summary>
    /// Sets a mod's override of a setting (replacing the one it held); false for an unknown setting or a value that is not finite.
    /// </summary>
    public bool Set(string mod, string key, float value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Keys.Contains(key) || !float.IsFinite(value)) return false;

        lock (_lock)
        {
            int at = _overrides.FindIndex(o => o.Mod == mod && o.Key == key);
            if (at >= 0) _overrides[at] = (mod, key, value);
            else _overrides.Add((mod, key, value));
        }

        return true;
    }

    /// <summary>
    /// Ends a mod's override of a setting; false when it held none.
    /// </summary>
    public bool Release(string mod, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock) return _overrides.RemoveAll(o => o.Mod == mod && o.Key == key) > 0;
    }

    /// <summary>
    /// Ends every override of a mod (it failed, or let them all go).
    /// </summary>
    public void Drop(string mod)
    {
        lock (_lock) _overrides.RemoveAll(o => o.Mod == mod);
    }

    /// <summary>
    /// Before the world is drawn: lays the overrides over the options, keeping the player's value of each setting for <see cref="Restore"/>.
    /// </summary>
    public void Apply()
    {
        lock (_lock)
        {
            foreach (var (_, key, value) in _overrides)
            {
                if (!_settings.TryGetValue(key, out var setting)) continue;

                if (!_held.Exists(h => ReferenceEquals(h.Setting, setting))) _held.Add((setting, setting.Get()));
                setting.Set(Math.Clamp(value, setting.Min, setting.Max));
            }
        }
    }

    /// <summary>
    /// After the world is drawn: puts the player's values back.
    /// </summary>
    public void Restore()
    {
        lock (_lock)
        {
            foreach (var (setting, player) in _held) setting.Set(player);
            _held.Clear();
        }
    }
}
