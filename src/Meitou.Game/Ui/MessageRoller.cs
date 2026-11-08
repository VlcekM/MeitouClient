using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>The colour of a DIALOGUE log line (the original's <c>MessageLogColor</c>: ML_NORMAL, ML_PLAYER, ML_SYSTEM).</summary>
enum MessageTone { Normal, Player, System }

/// <summary>The message panel (the original's MessageRoller, docs/game/ui-screens.md "Messages"): notices in turn, and the DIALOGUE log.</summary>
sealed class MessageRoller(PlayerInterface player)
{
    /// <summary>The seconds a message holds at full alpha, then the alpha it loses per second (<b>Verified</b>).</summary>
    const float NoticeHoldSeconds = 4, NoticeFadeRate = 3;

    readonly Queue<(string Text, bool Error)> _noticeQueue = new();
    (string Text, bool Error)? _notice;
    float _noticeHold, _noticeAlpha;
    long _noticeAt;
    internal MyGuiLayout? MessageLayout;
    internal string? MessageClient;

    /// <summary>Logs the message and queues its line for the panel (<see cref="PlayerInterface.Notice"/>).</summary>
    internal void QueueNotice(string text, bool error, string? logText)
    {
        string line = Loc.Tr(text).ReplaceLineEndings(" ");
        LogMessage("", logText?.ReplaceLineEndings(" ") ?? line, MessageTone.System);
        _noticeQueue.Enqueue((line, error));
    }

    /// <summary>Adds a line to the Overview's DIALOGUE log only ("owner: text", docs/game/ui-screens.md "Dialogue log"); the newest 200 are kept.</summary>
    public void LogMessage(string owner, string text, MessageTone tone)
    {
        _uiMessages.Add((owner.Length == 0 ? text : $"{owner}: {text}", tone));
        if (_uiMessages.Count > 200) _uiMessages.RemoveAt(0);
    }

    /// <summary>
    /// The DIALOGUE log as the text box's text: each line after the colour tag of its tone (the original's MessageLogColor: #000000, <c>DialogueTextOption</c>, #c6ac6a),
    /// its own <c>#</c> doubled. The Meitou mode's dark panel takes the kit's text colour for the black.
    /// </summary>
    internal string MessagesText()
    {
        string normal = player.UiMode == UiMode.Meitou ? PlayerInterface.Tag(PlayerInterface.MainColour) : "#000000", own = PlayerInterface.Tag(MyGuiColours.Named("DialogueTextOption", "b3b3b3"));
        return string.Join('\n', _uiMessages.Select(m => (m.Tone switch { MessageTone.Player => own, MessageTone.System => "#c6ac6a", _ => normal })
            + m.Text.Replace("#", "##", StringComparison.Ordinal)));
    }

    /// <summary>Once a frame: the next queued message comes up at full alpha when none shows; the shown one holds, then fades out.</summary>
    internal void PollNotice()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _noticeAt == 0 ? 0 : MathF.Min((float)System.Diagnostics.Stopwatch.GetElapsedTime(_noticeAt, now).TotalSeconds, 0.25f);
        _noticeAt = now;
        if (_notice is null)
        {
            if (_noticeQueue.TryDequeue(out var next)) (_notice, _noticeHold, _noticeAlpha) = (next, NoticeHoldSeconds, 1);
            return;
        }
        if ((_noticeHold -= dt) < 0 && (_noticeAlpha -= dt * NoticeFadeRate) <= 0) _notice = null;
    }

    /// <summary>The shown message in the game's <c>Kenshi_MessagePanel</c> layout, placed where the HUD's <c>MessagePanel</c> is; it never takes the mouse.</summary>
    internal void AddNotice(List<UiControl> controls)
    {
        if (_notice is not { } shown || MessageLayout is null || MessageLayout.Roots.Count == 0) return;
        var root = MessageLayout.Roots[0];
        var b = new UiBindings();
        if (player.FaithfulHud.Find("MessagePanel") is { RealPosition: true } slot) b.RelativePositions[root] = root.Position with { X = slot.Position.X, Y = slot.Position.Y };
        b.Text["MessageTextBox"] = shown.Text;
        b.Properties[root.Name] = new Dictionary<string, string> { ["Alpha"] = Math.Clamp(_noticeAlpha, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var w in MessageLayout.Widgets.Where(w => w != root)) b.Properties[w.Name] = new Dictionary<string, string> { ["NeedMouse"] = "false" };
        // The EditBox's alignment is its text's, which the template's client draws.
        if (MessageClient is not null)
            b.Properties[MessageClient] = new Dictionary<string, string> { ["NeedMouse"] = "false", ["TextAlign"] = "Center", ["TextColour"] = shown.Error ? PlayerInterface.BadColour : MyGuiColours.TemplateInk(player.UiResources, "Kenshi_WordWrapEmpty_StandardText_Large") };
        controls.AddRange(MyGuiPresentation.Arrange(MessageLayout, player.Width, player.Height, b, player.UiResources.Layers).Select(c => c with { Pickable = false }));
    }

    readonly List<(string Text, MessageTone Tone)> _uiMessages = [];
}