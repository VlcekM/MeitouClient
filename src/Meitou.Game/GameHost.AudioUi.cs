using Meitou.Game.Audio;

namespace Meitou.Game;

/// <summary>The interface's sounds (docs/audio.md "Interface"): <see cref="PlayerInterface.UiSound"/> goes to <see cref="UiSounds"/> on the GUI object while audio runs, also on the title screen.</summary>
sealed partial class GameHost
{
    UiSounds? uiSounds;

    /// <summary>Called when the audio is attached: the interface starts sounding, or stays silent without audio.</summary>
    void AttachUiSounds()
    {
        if (player is null) return;
        uiSounds = audio is null ? null : new UiSounds(audio.Player, () => scene.Database);
        var sounds = uiSounds;
        player.UiSound = sounds is null ? null : e => sounds.Play(e);
    }

    /// <summary>One statistics line; empty without audio.</summary>
    string UiAudioStats() => uiSounds is null ? "" : $"{uiSounds.Posted} interface sounds";
}
