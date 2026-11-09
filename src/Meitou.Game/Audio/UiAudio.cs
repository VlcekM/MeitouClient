namespace Meitou.Game.Audio;

/// <summary>
/// The interface's sounds (docs/audio.md "Interface"): <see cref="PlayerInterface.UiSound"/> goes to <see cref="UiSounds"/> on the GUI object
/// while audio runs, also on the title screen.
/// </summary>
sealed class UiAudio(GameHost host)
{
    /// <summary>
    /// The interface's sound object; null without audio.
    /// </summary>
    internal UiSounds? Sounds;

    /// <summary>
    /// Called when the audio is attached: the interface starts sounding, or stays silent without audio.
    /// </summary>
    internal void Attach(AudioSystem? audio)
    {
        if (host.Player is null) return;
        Sounds = audio is null ? null : new UiSounds(audio.Player, () => host.Scene.Database);
        var sounds = Sounds;
        host.Player.InterfaceSounds.UiSound = sounds is null ? null : e => sounds.Play(e);
    }

    /// <summary>
    /// One statistics line; empty without audio.
    /// </summary>
    internal string Stats() => Sounds is null ? "" : $"{Sounds.Posted} interface sounds";
}
