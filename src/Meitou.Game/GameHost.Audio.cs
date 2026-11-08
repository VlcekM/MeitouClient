using System.Numerics;
using Meitou.Game.Audio;

namespace Meitou.Game;

/// <summary>
/// The game's sound (docs/audio.md "In the game"): <see cref="AudioSystem"/> made beside the window, fed each frame from the camera, the game clock and the weather,
/// and the main theme on the title screen. A failure to start it is logged and the game runs silently.
/// </summary>
sealed partial class GameHost
{
    Task<(AudioSystem? Audio, string? Error)>? audioStart;
    AudioSystem? audio;

    /// <summary>Starts making the audio on a worker (the codebook library and the sound device) while the window and the world load; nothing with <c>--no-audio</c>.</summary>
    void BeginAudio()
    {
        if (o.NoAudio) return;
        // The sliders as saved, else the install's settings.cfg (read once, here, so the settings menu shows the same values).
        var settings = config.Audio ??= AudioSettings.FromInstall(install);
        audioStart = Task.Run(() =>
        {
            try
            {
                var made = AudioSystem.TryCreate(install, settings, out var error);
                if (made is not null) made.Player.Gain = MathF.Pow(10, o.AudioGainDb / 20);
                return (made, error);
            }
            catch (Exception e)
            {
                // Whatever the device or the banks throw: the game goes on without sound.
                return ((AudioSystem?)null, e.Message);
            }
        });
    }

    /// <summary>Takes the audio made by <see cref="BeginAudio"/>, logs the outcome and starts the main theme when the title screen is up.</summary>
    void AttachAudio()
    {
        if (audioStart is null)
        {
            Console.WriteLine("audio     off (--no-audio)");
            return;
        }
        var (made, error) = audioStart.GetAwaiter().GetResult();
        audioStart = null;
        audio = made;
        Console.WriteLine(made is not null
            ? $"audio     {made.DeviceName}, ambience and music from the Wwise banks{(o.AudioGainDb != 0 ? $", gain {o.AudioGainDb:+0.#;-0.#} dB" : "")} (--no-audio turns it off)"
            : $"audio     off: {error}");
        if (audio is not null && player is not null && player.TitleOpen) audio.Music.StartMainTheme();
        AttachUiSounds();
    }

    /// <summary>
    /// Once a frame: the ambience and the biome music follow the camera as in the game's camera update. The pivot is the camera's target (the region under it
    /// is the biome), the eye its position; the hour is the game clock's. Not while the title screen is up: the game runs no camera update there, so the main
    /// theme is not stopped by an ambience event or the music bank's unload.
    /// </summary>
    /// <summary>The ears above a character's feet when sounds are heard from it (units; a person is about 180 tall).</summary>
    const float EarHeight = 160;

    void UpdateAudio(double realSeconds)
    {
        // The Audio settings' Sound voices slider (config.Audio.Voices) is applied by AudioSystem.Apply.
        if (audio is null || player is null || player.TitleOpen) return;
        var target = camera.Target;
        var eye = camera.Eye;
        var front = camera.Forward;
        var right = Vector3.Normalize(Vector3.Cross(front, Vector3.UnitY));
        // The Audio settings' Hear from switch: the listener and every distance limit move to the first selected character's ears; the camera keeps the orientation.
        var ears = config.HearFromSelected && player.SelectedPosition is { } lead ? lead + Vector3.UnitY * EarHeight : eye;
        audio.SetListener(ears, front, Vector3.Cross(right, front));
        audio.SetPaused(session.Simulation.IsPaused);
        var weather = ActiveWeather;
        var clock = session.Clock;
        audio.Ambience.FixDelayedSuffix = config.FixAmbienceSuffix;
        audio.Ambience.FixWindStop = config.FixWindStop;
        audio.Update(weather?.World.RegionAt(target.X, target.Z), weather?.State, clock.HourOfDay, clock.Sunrise, clock.Sunset,
            eye.Y - ActiveGround(eye.X, eye.Z), realSeconds);
        UpdateWorldAudio(audio, ears == eye ? target : ears, ears);
        UpdateCharacterAudio(audio, ears);
        UpdateTurretAudio(audio, ears, realSeconds);
    }

    /// <summary>The audio sliders changed in the settings menu: they apply at once.</summary>
    void AudioSettingsChanged()
    {
        if (audio is not null && config.Audio is { } settings) audio.Apply(settings);
    }

    /// <summary>A new game, load or import starts: the game posts <c>Create_or_Load_Game</c>, which fades the main theme out.</summary>
    void AudioGameStarts() => audio?.Music.OnCreateOrLoadGame();

    /// <summary>
    /// Back at the main menu (Meitou's choice: the game has no such path): the world's ambience ends and the main theme plays again, as at the start.
    /// </summary>
    void AudioMainMenu()
    {
        if (audio is null) return;
        audio.SetPaused(false);
        audio.Ambience.Silence();
        ResetWorldAudio();
        ResetCharacterAudio();
        ResetTurretAudio();
        audio.Music.StartMainTheme();
    }

    void DisposeAudio()
    {
        // A start still running (the window closed during loading) is waited for, so its device is released.
        if (audioStart is { } pending) { audio = pending.GetAwaiter().GetResult().Audio; audioStart = null; }
        audio?.Dispose();
        audio = null;
    }
}
