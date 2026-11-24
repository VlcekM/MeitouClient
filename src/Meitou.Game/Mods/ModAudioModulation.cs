using Meitou.Game.Audio;

namespace Meitou.Modding;

/// <summary>
/// Playback modulation a code mod applies to active sounds (<see cref="ModContext.SetAudioModulation"/>, docs/modding.md "Audio modulation"):
/// <paramref name="Rate"/> multiplies playback speed and pitch (<see cref="MinRate"/> to <see cref="MaxRate"/>; 1 = unchanged),
/// <paramref name="LowPass"/> caps the per-sound one-pole low-pass coefficient (<see cref="MinLowPass"/> to 1; 1 = unfiltered),
/// <paramref name="Gain"/> multiplies volume (0 to <see cref="MaxGain"/>; 1 = unchanged).
/// Applies to world and ambience sounds by default; set <paramref name="AffectMusic"/> or <paramref name="AffectUi"/> to modulate music or UI
/// sounds as well.
/// </summary>
public readonly record struct AudioModulation(float Rate = 1, float LowPass = 1, float Gain = 1,
    bool AffectMusic = false, bool AffectUi = false)
{
    /// <summary>
    /// Lowest and highest allowed playback rate multiplier.
    /// </summary>
    public const float MinRate = 0.1f, MaxRate = 4f;

    /// <summary>
    /// Lowest allowed low-pass coefficient (1 = unfiltered).
    /// </summary>
    public const float MinLowPass = 0.01f;

    /// <summary>
    /// Highest allowed volume gain multiplier.
    /// </summary>
    public const float MaxGain = 2f;

    /// <summary>
    /// No modulation (all multipliers at 1).
    /// </summary>
    public static readonly AudioModulation Identity = new(1, 1, 1, false, false);

    /// <summary>
    /// Whether this modulation has no effect (rate, low-pass and gain are all 1).
    /// </summary>
    public bool IsIdentity => Rate == 1 && LowPass == 1 && Gain == 1;

    /// <summary>
    /// Returns a copy with <see cref="Rate"/>, <see cref="LowPass"/> and <see cref="Gain"/> clamped to their valid ranges (non-finite values
    /// become 1).
    /// </summary>
    public AudioModulation Clamped() => new(
        float.IsFinite(Rate) ? Math.Clamp(Rate, MinRate, MaxRate) : 1,
        float.IsFinite(LowPass) ? Math.Clamp(LowPass, MinLowPass, 1) : 1,
        float.IsFinite(Gain) ? Math.Clamp(Gain, 0, MaxGain) : 1,
        AffectMusic,
        AffectUi);
}

/// <summary>
/// Pre-composed modulation values for one sound category, read lock-free on the audio thread.
/// </summary>
readonly record struct AudioChannelModulation(float Rate, float LowPass, float Gain)
{
    /// <summary>
    /// No modulation on this channel.
    /// </summary>
    internal static readonly AudioChannelModulation Identity = new(1, 1, 1);
}

/// <summary>
/// Immutable snapshot of all active mods' composed audio modulations, swapped atomically onto <see cref="WwisePlayer"/> for lock-free reads on
/// the audio thread.
/// </summary>
sealed class AudioModulationSnapshot(AudioChannelModulation world, AudioChannelModulation music, AudioChannelModulation ui)
{
    /// <summary>
    /// Modulation for world and ambience sounds.
    /// </summary>
    internal readonly AudioChannelModulation World = world;

    /// <summary>
    /// Modulation for music tracks.
    /// </summary>
    internal readonly AudioChannelModulation Music = music;

    /// <summary>
    /// Modulation for interface sounds (<see cref="AudioSystem.GuiObject"/>).
    /// </summary>
    internal readonly AudioChannelModulation Ui = ui;
}

/// <summary>
/// Composes per-mod <see cref="AudioModulation"/> entries and publishes a lock-free snapshot to <see cref="WwisePlayer"/>.
/// Multiple mods compose multiplicatively for <see cref="AudioModulation.Rate"/> and <see cref="AudioModulation.Gain"/>, and by minimum for
/// <see cref="AudioModulation.LowPass"/>, clamped to the valid ranges.
/// </summary>
sealed class ModAudioModulations
{
    /// <summary>
    /// Guards <see cref="_entries"/> and <see cref="_player"/> across host/mod threads.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// Active non-identity modulations by mod id.
    /// </summary>
    readonly Dictionary<string, AudioModulation> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The audio player receiving the composed snapshot; null when audio is off or not yet attached.
    /// </summary>
    WwisePlayer? _player;

    /// <summary>
    /// The latest composed snapshot; null when no mod is modulating audio.
    /// </summary>
    internal AudioModulationSnapshot? Current { get; private set; }

    /// <summary>
    /// Connects this registry to <paramref name="player"/> and pushes the current snapshot.
    /// </summary>
    internal void Attach(WwisePlayer? player)
    {
        lock (_gate)
        {
            _player = player;
            _player?.SetModAudio(Current);
        }
    }

    /// <summary>
    /// Sets or updates <paramref name="modId"/>'s audio modulation (identity clears it) and publishes the composed snapshot.
    /// </summary>
    internal void Set(string modId, AudioModulation modulation)
    {
        var clamped = modulation.Clamped();
        lock (_gate)
        {
            if (clamped.IsIdentity) _entries.Remove(modId);
            else _entries[modId] = clamped;

            PublishLocked();
        }
    }

    /// <summary>
    /// Removes <paramref name="modId"/>'s modulation (e.g. on clear or failed initialization).
    /// </summary>
    internal void Drop(string modId)
    {
        lock (_gate)
        {
            if (!_entries.Remove(modId)) return;

            PublishLocked();
        }
    }

    /// <summary>
    /// Recomputes <see cref="Current"/> from <see cref="_entries"/> and pushes it to <see cref="_player"/>.
    /// </summary>
    void PublishLocked()
    {
        if (_entries.Count == 0)
        {
            Current = null;
            _player?.SetModAudio(null);
            return;
        }

        float wRate = 1, wLow = 1, wGain = 1;
        float mRate = 1, mLow = 1, mGain = 1;
        float uRate = 1, uLow = 1, uGain = 1;

        foreach (var entry in _entries.Values)
        {
            wRate *= entry.Rate;
            wLow = MathF.Min(wLow, entry.LowPass);
            wGain *= entry.Gain;

            if (entry.AffectMusic)
            {
                mRate *= entry.Rate;
                mLow = MathF.Min(mLow, entry.LowPass);
                mGain *= entry.Gain;
            }

            if (entry.AffectUi)
            {
                uRate *= entry.Rate;
                uLow = MathF.Min(uLow, entry.LowPass);
                uGain *= entry.Gain;
            }
        }

        Current = new AudioModulationSnapshot(
            ClampChannel(wRate, wLow, wGain),
            ClampChannel(mRate, mLow, mGain),
            ClampChannel(uRate, uLow, uGain));
        _player?.SetModAudio(Current);
    }

    /// <summary>
    /// Clamps composed channel multipliers to the valid audio ranges.
    /// </summary>
    static AudioChannelModulation ClampChannel(float rate, float lowPass, float gain) => new(
        Math.Clamp(rate, AudioModulation.MinRate, AudioModulation.MaxRate),
        Math.Clamp(lowPass, AudioModulation.MinLowPass, 1),
        Math.Clamp(gain, 0, AudioModulation.MaxGain));
}
