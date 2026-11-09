using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The message panel (the original's MessageRoller, docs/game/ui-screens.md "Messages"): notices in turn, each also logged in the
/// <see cref="DialogueLog"/>.
/// </summary>
sealed class MessageRoller(PlayerInterface player)
{
    /// <summary>
    /// The seconds a message holds at full alpha, then the alpha it loses per second (<b>Verified</b>).
    /// </summary>
    const float NoticeHoldSeconds = 4, NoticeFadeRate = 3;

    /// <summary>
    /// The messages waiting for the panel, each with whether it is an error.
    /// </summary>
    readonly Queue<(string Text, bool Error)> _noticeQueue = new();

    /// <summary>
    /// The message shown; null when none is.
    /// </summary>
    (string Text, bool Error)? _notice;

    /// <summary>
    /// The shown message's seconds left at full alpha, and its alpha.
    /// </summary>
    float _noticeHold, _noticeAlpha;

    /// <summary>
    /// The timestamp of the last poll; 0 before the first.
    /// </summary>
    long _noticeAt;

    /// <summary>
    /// The game's <c>Kenshi_MessagePanel</c> layout; null when the install has none.
    /// </summary>
    internal MyGuiLayout? MessageLayout;

    /// <summary>
    /// The name of the widget that draws the message text box's text (its template's client).
    /// </summary>
    internal string? MessageClient;

    /// <summary>
    /// Logs the message and queues its line for the panel (<see cref="PlayerInterface.Notice"/>).
    /// </summary>
    internal void QueueNotice(string text, bool error, string? logText)
    {
        string line = Loc.Tr(text).ReplaceLineEndings(" ");
        player.DialogueLog.LogMessage("", logText?.ReplaceLineEndings(" ") ?? line, MessageTone.System);
        _noticeQueue.Enqueue((line, error));
    }

    /// <summary>
    /// Once a frame: the next queued message comes up at full alpha when none shows; the shown one holds, then fades out.
    /// </summary>
    internal void PollNotice()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _noticeAt == 0 ? 0 : MathF.Min((float)System.Diagnostics.Stopwatch.GetElapsedTime(_noticeAt, now).TotalSeconds, 0.25f);
        _noticeAt = now;
        if (_notice is null)
        {
            if (_noticeQueue.TryDequeue(out var next))
                (_notice, _noticeHold, _noticeAlpha) = (next, NoticeHoldSeconds, 1);
            return;
        }

        if ((_noticeHold -= dt) < 0 && (_noticeAlpha -= dt * NoticeFadeRate) <= 0) _notice = null;
    }

    /// <summary>
    /// The shown message in the game's <c>Kenshi_MessagePanel</c> layout, placed where the HUD's <c>MessagePanel</c> is; it never takes the mouse.
    /// </summary>
    internal void AddNotice(List<UiControl> controls)
    {
        if (_notice is not { } shown || MessageLayout is null || MessageLayout.Roots.Count == 0) return;

        var root = MessageLayout.Roots[0];
        var b = new UiBindings();
        if (player.FaithfulHud.Find("MessagePanel") is { RealPosition: true } slot)
            b.RelativePositions[root] = root.Position with { X = slot.Position.X, Y = slot.Position.Y };
        b.Text["MessageTextBox"] = shown.Text;
        string alpha = Math.Clamp(_noticeAlpha, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        b.Properties[root.Name] = new Dictionary<string, string> { ["Alpha"] = alpha };
        foreach (var w in MessageLayout.Widgets.Where(w => w != root))
            b.Properties[w.Name] = new Dictionary<string, string> { ["NeedMouse"] = "false" };

        // The EditBox's alignment is its text's, which the template's client draws.
        if (MessageClient is not null)
        {
            string textStyle = "Kenshi_WordWrapEmpty_StandardText_Large";
            string ink = shown.Error ? PlayerInterface.BadColour : MyGuiColours.TemplateInk(player.UiResources, textStyle);
            b.Properties[MessageClient] = new Dictionary<string, string>
            {
                ["NeedMouse"] = "false",
                ["TextAlign"] = "Center",
                ["TextColour"] = ink,
            };
        }

        var arranged = MyGuiPresentation.Arrange(MessageLayout, player.Width, player.Height, b, player.UiResources.Layers);
        controls.AddRange(arranged.Select(c => c with { Pickable = false }));
    }
}
