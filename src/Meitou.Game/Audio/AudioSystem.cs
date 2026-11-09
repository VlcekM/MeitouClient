using System.Numerics;
using Meitou.Content;
using Meitou.Data.Audio;
using Meitou.Data.World;

namespace Meitou.Game.Audio;

/// <summary>
/// The player's audio options (the original's <c>settings.cfg</c> keys, docs/formats/settings.md#audio): the six category volumes (0..1, the game
/// sends them as the <c>User_Volume_*</c> parameters × 100) and how often biome music starts (<see cref="MusicFrequency"/>, minutes); and Meitou's
/// own <see cref="Voices"/>, the most sounds mixed at once, and <see cref="Range"/>, how far sounds carry (neither in <c>settings.cfg</c>).
/// </summary>
public sealed record AudioSettings(float Music = 0.5f, float Ambient = 0.5f, float Footstep = 0.5f, float Sfx = 0.5f, float Ui = 0.5f, float Vo = 0.5f,
    float MusicFrequency = 5, int Voices = AudioSettings.MeitouVoices, float Range = AudioSettings.MeitouRange)
{
    /// <summary>
    /// The range of <see cref="MusicFrequency"/>, in minutes.
    /// </summary>
    public const float MinFrequency = 1, MaxFrequency = 20;

    /// <summary>
    /// The voice cap's range and Meitou's default (docs/audio.md "Voice cap"); <see cref="MaxVoices"/> means every sound is mixed.
    /// </summary>
    public const int MinVoices = 16, MaxVoices = 256, MeitouVoices = 96;

    /// <summary>
    /// The hearing range's factor on every distance (docs/audio.md "Hearing range"): 1 is the original, Meitou's default carries twice as far.
    /// </summary>
    public const float MinRange = 1, MaxRange = 4, MeitouRange = 2;

    /// <summary>
    /// What <see cref="WwisePlayer.VoiceCap"/> takes: the cap, 0 at <see cref="MaxVoices"/> (no cap).
    /// </summary>
    public int VoiceCap => Voices >= MaxVoices ? 0 : Math.Max(Voices, MinVoices);

    /// <summary>
    /// The install's <c>settings.cfg</c>; every missing key takes the original's default.
    /// </summary>
    public static AudioSettings FromInstall(GameInstall install) => new(
        Music: AudioSystem.Setting(install, "Music volume", 0.5f),
        Ambient: AudioSystem.Setting(install, "Ambient volume", 0.5f),
        Footstep: AudioSystem.Setting(install, "Footstep volume", 0.5f),
        Sfx: AudioSystem.Setting(install, "Sfx volume", 0.5f),
        Ui: AudioSystem.Setting(install, "UI volume", 0.5f),
        Vo: AudioSystem.Setting(install, "VO volume", 0.5f),
        MusicFrequency: AudioSystem.Setting(install, "Music frequency", 5, 0, 1000));
}

/// <summary>
/// The audio of a viewer or game session: the codebook library (from the cache, else the exe), the bank player, the ambience and music
/// rules and the output device, wired together. <see cref="TryCreate(GameInstall, AudioSettings, out string?)"/> returns null with the reason
/// when any part is missing, so a host simply runs without sound.
/// <para>
/// For the events a host posts itself (footsteps, combat, the interface, voices): <see cref="Player"/>'s <c>Post(event, gameObject)</c>,
/// <c>SetPosition</c>, <c>UnregisterObject</c>, <c>SetSwitch(group, value, gameObject)</c> and <c>SetParameter(name, value, gameObject)</c>
/// (docs/audio.md#api-for-event-sources). <see cref="MusicObject"/> and <see cref="GuiObject"/> are the game's own object ids; pick other ids
/// for emitters. <see cref="SetListener"/> follows the camera.
/// </para>
/// </summary>
public sealed class AudioSystem : IDisposable
{
    /// <summary>
    /// The mixer's and the output device's sample rate, in Hz.
    /// </summary>
    public const int SampleRate = 44100;

    /// <summary>
    /// The game object the original posts the music, the ambience and the pause events on (<c>RegisterGameObj(100, "Music")</c>).
    /// </summary>
    public const ulong MusicObject = 100;

    /// <summary>
    /// The game object of the interface sounds (<c>RegisterGameObj(110, "GUI")</c>).
    /// </summary>
    public const ulong GuiObject = 110;

    /// <summary>
    /// The OpenAL output that pulls the player's mix.
    /// </summary>
    readonly AudioOutput _output;
    /// <summary>
    /// Whether the game is paused (<see cref="SetPaused"/>), so the pause and resume events are posted only on a change.
    /// </summary>
    bool _paused;

    /// <summary>
    /// The bank player: loads banks, posts events and mixes the voices.
    /// </summary>
    public WwisePlayer Player { get; }
    /// <summary>
    /// The biome, day/night and weather ambience rules.
    /// </summary>
    public AmbienceDirector Ambience { get; }
    /// <summary>
    /// The music timer and the music events.
    /// </summary>
    public MusicDirector Music { get; }
    /// <summary>
    /// The output device's name, as OpenAL reports it ("default" when it reports none).
    /// </summary>
    public string DeviceName => _output.DeviceName;

    /// <summary>
    /// The options last applied (<see cref="Apply"/>).
    /// </summary>
    public AudioSettings Settings { get; private set; }

    /// <summary>
    /// Wires the parts made by <see cref="TryCreate(GameInstall, AudioSettings, out string?)"/> and loads the ambience's start banks.
    /// </summary>
    AudioSystem(WwisePlayer player, AudioOutput output, MusicDirector music, AudioSettings settings)
    {
        Player = player;
        this._output = output;
        Music = music;
        Settings = settings;
        Ambience = new AmbienceDirector(player);
        Ambience.Start();
    }

    /// <summary>
    /// <paramref name="ambientVolume"/>: the install's <c>settings.cfg</c> <c>Ambient volume</c> (0..1, missing → 0.5), for hosts that keep the other
    /// options in <c>settings.cfg</c> too (the viewer): the rest is read from there.
    /// </summary>
    public static AudioSystem? TryCreate(GameInstall install, float ambientVolume, out string? error) =>
        TryCreate(install, AudioSettings.FromInstall(install) with { Ambient = ambientVolume }, out error);

    /// <summary>
    /// <paramref name="settings"/>: the volumes and the music frequency to start with. The game sends the volumes as the game parameters
    /// <c>User_Volume_*</c> and <c>Footstep_Volume</c> (× 100), which the buses in <c>Init.bnk</c> turn into their gains (docs/audio.md#volumes).
    /// </summary>
    public static AudioSystem? TryCreate(GameInstall install, AudioSettings settings, out string? error)
    {
        var exe = install.Executable;
        var library = WwiseCodebookLibrary.Load(exe);
        if (library is null)
        {
            error = $"no Wwise codebook library in {exe} ({WwiseCodebookLibrary.Diagnose(exe)})";
            return null;
        }
        var player = new WwisePlayer(library, Path.Combine(install.DataDirectory, "audio"), SampleRate);
        var music = new MusicDirector(player, settings.MusicFrequency, settings.Music);
        var output = AudioOutput.TryOpen(SampleRate, player.Mix, out error);
        if (output is null)
        {
            player.Dispose();
            return null;
        }
        var system = new AudioSystem(player, output, music, settings);
        system.Apply(settings);
        return system;
    }

    /// <summary>
    /// Applies the options (the game does at start and when its options close): each volume becomes its game parameter × 100 and a changed music
    /// frequency moves the music timer's deadline earlier, never later.
    /// </summary>
    public void Apply(AudioSettings settings)
    {
        Settings = settings;
        Music.SetVolume(settings.Music);
        Player.SetParameter("User_Volume_Ambient_SFX", Math.Clamp(settings.Ambient, 0, 1) * 100);
        Player.SetParameter("Footstep_Volume", Math.Clamp(settings.Footstep, 0, 1) * 100);
        Player.SetParameter("User_Volume_SFX", Math.Clamp(settings.Sfx, 0, 1) * 100);
        Player.SetParameter("User_Volume_UI", Math.Clamp(settings.Ui, 0, 1) * 100);
        Player.SetParameter("User_Volume_VO", Math.Clamp(settings.Vo, 0, 1) * 100);
        Music.SetFrequency(settings.MusicFrequency);
        Player.VoiceCap = settings.VoiceCap;
        Player.Reach = Math.Clamp(settings.Range, AudioSettings.MinRange, AudioSettings.MaxRange);
    }

    /// <summary>
    /// The game was paused (or its speed set to 0) or resumed: posts <c>Pause_Game</c> / <c>Resume_Game</c> on the music object, which duck the SFX
    /// bus to silence and the ambience by 9 dB over a second, and give them back over three; the music is not touched. Does nothing when the state is
    /// the same.
    /// </summary>
    public void SetPaused(bool pause)
    {
        if (pause == _paused) return;
        _paused = pause;
        Player.Post(pause ? "Pause_Game" : "Resume_Game", MusicObject);
    }

    /// <summary>
    /// Whether <see cref="SetPaused"/> last paused.
    /// </summary>
    public bool Paused => _paused;

    /// <summary>
    /// Where the ears are: the camera's eye and the way it faces. Meitou gives the listener the camera's true front and up; the original sets the
    /// listener's front slot to the camera's up vector and leaves the top slot unset (docs/audio.md#listener), which cannot be reproduced.
    /// </summary>
    public void SetListener(Vector3 eye, Vector3 front, Vector3 up) => Player.SetListener(eye, front, up);

    /// <summary>
    /// Feeds the ambience and the music the frame's state the way the game's camera update does (docs/audio.md): <paramref name="region"/> is the
    /// weather region under the camera's pivot (null: none), <paramref name="weather"/> the weather at the camera (null: clear),
    /// <paramref name="hour"/> the game hour against <paramref name="sunrise"/> and <paramref name="sunset"/>, <paramref name="eyeAboveGround"/> the
    /// eye's height over the ground under it and <paramref name="realSeconds"/> the real time since the last call.
    /// </summary>
    public void Update(WeatherRegion? region, WeatherState? weather, double hour, double sunrise, double sunset, float eyeAboveGround,
        double realSeconds)
    {
        Ambience.Update(new AmbienceInput
        {
            Biome = region?.Def.Ambience,
            Hour = ((hour % 24) + 24) % 24,
            Sunrise = (float)sunrise,
            Sunset = (float)sunset,
            EyeAboveGround = eyeAboveGround,
            Rain = weather?.Rain ?? 0,
            Wind = weather is null ? 0 : weather.Weather.WindIntensity * weather.WindSpeed * 0.01f,
            RealSeconds = realSeconds,
        });
        Music.Update(region?.Def.Music);
    }

    /// <summary>
    /// One line for a statistics overlay: the biome heard, day or night, rain, wind, voices and clips, and the music (the one remembered, seconds
    /// until its timer posts it, music voices).
    /// </summary>
    public string StatsLine()
    {
        string music = MusicState();
        string biome = Ambience.Biome is { } heard ? $"{heard} {(Ambience.Night ? "night" : "day")}" : "no biome";
        string flags = $"{(Ambience.Raining ? ", rain" : "")}{(Ambience.Windy ? ", wind" : "")}{(_paused ? ", paused" : "")}";
        string capped = Player.Silenced > 0 ? $" ({Player.Silenced} capped)" : "";
        string musicHeard = Music.TitleScreen ? "main theme" : Music.Biome ?? "none";
        return $"{biome}{flags}: {Player.Stats.Voices} voices{capped}, {Player.Stats.Clips} clips loaded; " +
            $"music {musicHeard}, {music}, {Player.MusicVoices} voices";
    }

    /// <summary>
    /// The music part of <see cref="StatsLine"/>: what the timer has remembered, its volume, or the seconds to the next post.
    /// </summary>
    string MusicState()
    {
        if (Music.Remembered is null) return "nothing to post";
        if (Music.Volume <= 0) return "volume 0";

        return $"next in {Math.Max(0, Music.SecondsToNext):0} s";
    }

    /// <summary>
    /// <c>settings.cfg</c>'s <c>Ambient volume</c> (0..1); 0.5 when missing or unreadable.
    /// </summary>
    public static float AmbientVolumeSetting(GameInstall install) => Setting(install, "Ambient volume", 0.5f);

    /// <summary>
    /// A number of the install's <c>settings.cfg</c> (<c>key=value</c> lines), clamped to <paramref name="min"/>..<paramref name="max"/>;
    /// <paramref name="fallback"/> when missing or unreadable.
    /// </summary>
    public static float Setting(GameInstall install, string key, float fallback, float min = 0, float max = 1)
    {
        try
        {
            var file = Path.Combine(install.Root, "settings.cfg");
            if (!File.Exists(file)) return fallback;
            foreach (var line in File.ReadLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase) && TryParseValue(line[(eq + 1)..], out float v))
                    return Math.Clamp(v, min, max);
            }
        }
        catch (IOException)
        {
        }

        return fallback;
    }

    /// <summary>
    /// Parses a settings value as an invariant-culture float.
    /// </summary>
    static bool TryParseValue(string text, out float value) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Closes the output device, then the player.
    /// </summary>
    public void Dispose()
    {
        _output.Dispose();
        Player.Dispose();
    }
}