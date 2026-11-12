using System.Numerics;
using Meitou.Data;
using Meitou.Simulation;

namespace Meitou.Game.Audio;

/// <summary>
/// The game's sound (docs/audio.md "In the game"): <see cref="AudioSystem"/> made beside the window, fed each frame from the camera, the game clock
/// and the weather, and the main theme on the title screen. A failure to start it is logged and the game runs silently.
/// </summary>
sealed class GameSound(GameHost host)
{
    /// <summary>
    /// The audio being made on a worker; null once attached or with <c>--no-audio</c>.
    /// </summary>
    Task<(AudioSystem? Audio, string? Error)>? _audioStart;

    /// <summary>
    /// The audio; null without sound.
    /// </summary>
    internal AudioSystem? Audio;

    /// <summary>
    /// The characters' sounds.
    /// </summary>
    internal readonly CharacterAudio Characters = new(host);

    /// <summary>
    /// The ground lookups, once made.
    /// </summary>
    internal GroundSurfaces? Ground => Characters.Ground;

    /// <summary>
    /// The biome and ground lookups the sounds use, shared with the ground effects.
    /// </summary>
    internal GroundSurfaces GroundSurfacesOf(GameDatabase db) => Characters.GroundSurfacesOf(db);

    /// <summary>
    /// The animation sound events, shared with the ground effects.
    /// </summary>
    internal AnimationSoundEvents AnimationSoundsOf(GameDatabase db) => Characters.AnimationSoundsOf(db);

    /// <summary>
    /// How far through its clip a layer is (<see cref="CharacterAudio.ClipFraction"/>).
    /// </summary>
    internal static float ClipFraction(AnimationLayer layer, bool loops) => CharacterAudio.ClipFraction(layer, loops);

    /// <summary>
    /// The interiors', doors' and emitters' sounds.
    /// </summary>
    internal readonly WorldAudio World = new(host);

    /// <summary>
    /// The turrets' movement sounds.
    /// </summary>
    internal readonly TurretAudio Turrets = new(host);

    /// <summary>
    /// The interface's sounds.
    /// </summary>
    internal readonly UiAudio Ui = new(host);

    /// <summary>
    /// The ears above a character's feet when sounds are heard from it (units; a person is about 180 tall).
    /// </summary>
    const float EarHeight = 160;

    /// <summary>
    /// Starts making the audio on a worker (the codebook library and the sound device) while the window and the world load; nothing with
    /// <c>--no-audio</c>.
    /// </summary>
    internal void BeginAudio()
    {
        if (host.WorldOptions.NoAudio) return;
        // The sliders as saved, else the install's settings.cfg (read once, here, so the settings menu shows the same values).
        var settings = host.Config.Audio ??= AudioSettings.FromInstall(host.Install);
        _audioStart = Task.Run(() =>
        {
            try
            {
                var made = AudioSystem.TryCreate(host.Install, settings, out var error);
                if (made is not null) made.Player.Gain = MathF.Pow(10, host.WorldOptions.AudioGainDb / 20);
                return (made, error);
            }
            catch (Exception e)
            {
                // Whatever the device or the banks throw: the game goes on without sound.
                return ((AudioSystem?)null, e.Message);
            }
        });
    }

    /// <summary>
    /// Takes the audio made by <see cref="BeginAudio"/>, logs the outcome and starts the main theme when the title screen is up.
    /// </summary>
    internal void AttachAudio()
    {
        if (_audioStart is null)
        {
            Console.WriteLine("audio     off (--no-audio)");
            return;
        }
        var (made, error) = _audioStart.GetAwaiter().GetResult();
        _audioStart = null;
        Audio = made;
        Console.WriteLine(made is not null
            ? $"audio     {made.DeviceName}, ambience and music from the Wwise banks{GainText()} (--no-audio turns it off)"
            : $"audio     off: {error}");
        if (Audio is not null && host.Player is not null && host.Player.TitleOpen) Audio.Music.StartMainTheme();
        Ui.Attach(Audio);
    }

    /// <summary>
    /// The gain part of the audio log line: ", gain N dB" when the gain option is set, else empty.
    /// </summary>
    string GainText() => host.WorldOptions.AudioGainDb != 0 ? $", gain {host.WorldOptions.AudioGainDb:+0.#;-0.#} dB" : "";

    /// <summary>
    /// Once a frame: the ambience and the biome music follow the camera as in the game's camera update. The pivot is the camera's target (the region
    /// under it is the biome), the eye its position; the hour is the game clock's. Not while the title screen is up: the game runs no camera update
    /// there, so the main theme is not stopped by an ambience event or the music bank's unload.
    /// </summary>
    internal void UpdateAudio(double realSeconds)
    {
        // The Audio settings' Sound voices slider (config.Audio.Voices) is applied by AudioSystem.Apply.
        if (Audio is null || host.Player is null || host.Player.TitleOpen) return;

        var target = host.Camera.Target;
        var eye = host.Camera.Eye;
        var front = host.Camera.Forward;
        var right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));

        // The Audio settings' Hear from switch: the listener and every distance limit move to the first selected character's ears; the camera
        // keeps the orientation.
        var ears = host.Config.HearFromSelected && host.Player.SelectedPosition is { } lead ? lead + Vector3.UnitY * EarHeight : eye;
        Audio.SetListener(ears, front, Vector3.Cross(right, front));
        Audio.SetPaused(host.Session.Simulation.IsPaused);
        var weather = host.ActiveWeather;
        var clock = host.Session.Clock;
        Audio.Ambience.FixDelayedSuffix = host.Config.FixAmbienceSuffix;
        Audio.Ambience.FixWindStop = host.Config.FixWindStop;
        Audio.Update(weather?.World.RegionAt(target.X, target.Z), weather?.State, clock.HourOfDay, clock.Sunrise, clock.Sunset,
            eye.Y - host.ActiveGround(eye.X, eye.Z), realSeconds);
        World.Update(Audio, ears == eye ? target : ears, ears);
        Characters.Update(Audio, ears);
        Turrets.Update(Audio, ears, realSeconds);
    }

    /// <summary>
    /// The audio sliders changed in the settings menu: they apply at once.
    /// </summary>
    internal void AudioSettingsChanged()
    {
        if (Audio is not null && host.Config.Audio is { } settings) Audio.Apply(settings);
    }

    /// <summary>
    /// A new game, load or import starts: the game posts <c>Create_or_Load_Game</c>, which fades the main theme out.
    /// </summary>
    internal void AudioGameStarts() => Audio?.Music.OnCreateOrLoadGame();

    /// <summary>
    /// Back at the main menu (Meitou's choice: the game has no such path): the world's ambience ends and the main theme plays again, as at the start.
    /// </summary>
    internal void AudioMainMenu()
    {
        if (Audio is null) return;
        Audio.SetPaused(false);
        Audio.Ambience.Silence();
        World.Reset();
        Characters.Reset();
        Turrets.Reset();
        Audio.Music.StartMainTheme();
    }

    /// <summary>
    /// The window closes: the audio and its device are released.
    /// </summary>
    internal void DisposeAudio()
    {
        // A start still running (the window closed during loading) is waited for, so its device is released.
        if (_audioStart is { } pending)
        {
            Audio = pending.GetAwaiter().GetResult().Audio;
            _audioStart = null;
        }

        Audio?.Dispose();
        Audio = null;
    }
}
