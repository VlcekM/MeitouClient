using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The colour of a DIALOGUE log line (the original's <c>MessageLogColor</c>: ML_NORMAL, ML_PLAYER, ML_SYSTEM).
/// </summary>
enum MessageTone { Normal, Player, System }

/// <summary>
/// The Overview's DIALOGUE log (docs/game/ui-screens.md "Dialogue log"): the lines said and the message panel's messages.
/// </summary>
sealed class DialogueLog(PlayerInterface player)
{
    /// <summary>
    /// The log's lines, oldest first, each with its tone.
    /// </summary>
    readonly List<(string Text, MessageTone Tone)> _uiMessages = [];

    /// <summary>
    /// Adds a line to the Overview's DIALOGUE log only ("owner: text", docs/game/ui-screens.md "Dialogue log"); the newest 200 are kept.
    /// </summary>
    public void LogMessage(string owner, string text, MessageTone tone)
    {
        _uiMessages.Add((owner.Length == 0 ? text : $"{owner}: {text}", tone));
        if (_uiMessages.Count > 200) _uiMessages.RemoveAt(0);
    }

    /// <summary>
    /// The DIALOGUE log as the text box's text: each line after the colour tag of its tone (the original's MessageLogColor: #000000,
    /// <c>DialogueTextOption</c>, #c6ac6a), its own <c>#</c> doubled. The Meitou mode's dark panel takes the kit's text colour for the black.
    /// </summary>
    internal string MessagesText()
    {
        string normal = player.UiMode == UiMode.Meitou ? PlayerInterface.Tag(PlayerInterface.MainColour) : "#000000";
        string own = PlayerInterface.Tag(MyGuiColours.Named("DialogueTextOption", "b3b3b3"));
        return string.Join('\n', _uiMessages.Select(m => (m.Tone switch { MessageTone.Player => own, MessageTone.System => "#c6ac6a", _ => normal })
            + m.Text.Replace("#", "##", StringComparison.Ordinal)));
    }
}
