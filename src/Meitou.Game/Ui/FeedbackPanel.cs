using Meitou.Data.Ui;
using Meitou.Game.Host;
using Meitou.Game.Title;

namespace Meitou.Game.Ui;

/// <summary>
/// What the player filled in on the feedback form: the attachments are the ticked ones the game can make.
/// </summary>
sealed record FeedbackForm(string Title, string Body, bool Screenshot, bool Navmesh, bool Save, bool Log, string? Discord);

/// <summary>
/// The form's ticks and the Discord name, kept in meitou.user.json (<see cref="UserConfig.Feedback"/>) so the next report starts from them.
/// </summary>
sealed class FeedbackSettings
{
    /// <summary>
    /// Attach a screenshot of the game (default on).
    /// </summary>
    public bool Screenshot { get; set; } = true;

    /// <summary>
    /// A second screenshot with the navmesh drawn over the world (off by default: it takes a moment to make).
    /// </summary>
    public bool Navmesh { get; set; }

    /// <summary>
    /// Make a save when sending and attach it (default on).
    /// </summary>
    public bool Save { get; set; } = true;

    /// <summary>
    /// Attach log.txt (default off).
    /// </summary>
    public bool Log { get; set; }

    /// <summary>
    /// Send <see cref="DiscordName"/> with the report (default on), so it can be followed up on Discord.
    /// </summary>
    public bool Discord { get; set; } = true;

    /// <summary>
    /// The player's Discord name.
    /// </summary>
    public string DiscordName { get; set; } = "";
}

/// <summary>
/// Meitou's feedback button, always at the top right in both UI modes, and the form it opens: a title, the text, and ticks for a screenshot (taken
/// when the button is pressed, without the menus), a save made when sending and log.txt. The player's Discord name goes along unless unticked; the
/// ticks and the name are remembered (<see cref="FeedbackSettings"/>). The host (FeedbackCapture) takes the picture, makes the files and sends them
/// to the reports service, which opens a GitHub issue (docs/feedback.md). The feedback button and form (docs/feedback.md): the
/// button over the interface, the form's fields and ticks, and the screenshot taken without the menus.
/// </summary>
sealed class FeedbackPanel(PlayerInterface player)
{
    #region State

    /// <summary>
    /// The form's screen id.
    /// </summary>
    internal const string FeedbackScreen = "feedback";

    /// <summary>
    /// The button's widget name.
    /// </summary>
    internal const string FeedbackButton = "MeitouFeedbackButton";

    /// <summary>
    /// The title edit box.
    /// </summary>
    const string FeedbackTitleBox = "TitleEditBox";

    /// <summary>
    /// The text edit box.
    /// </summary>
    const string FeedbackBodyBox = "BodyEditBox";

    /// <summary>
    /// The screenshot tick.
    /// </summary>
    const string FeedbackShotTick = "ScreenshotTickBox";

    /// <summary>
    /// The navmesh screenshot tick.
    /// </summary>
    const string FeedbackNavTick = "NavmeshTickBox";

    /// <summary>
    /// The save tick.
    /// </summary>
    const string FeedbackSaveTick = "SaveTickBox";

    /// <summary>
    /// The log tick.
    /// </summary>
    const string FeedbackLogTick = "LogTickBox";

    /// <summary>
    /// The Discord name tick.
    /// </summary>
    const string FeedbackDiscordTick = "DiscordTickBox";

    /// <summary>
    /// The Discord name edit box.
    /// </summary>
    const string FeedbackDiscordBox = "DiscordEditBox";

    /// <summary>
    /// The screens a feedback screenshot leaves out: the game menu and what it opens, and the form itself.
    /// </summary>
    internal static readonly HashSet<string> FeedbackShotHides = ["menu", "message", "save", "load", "import", "new-game", "file", FeedbackScreen];

    /// <summary>
    /// Where the last arranged frame put the button (empty: not shown).
    /// </summary>
    UiRect _feedbackButtonBounds;

    /// <summary>
    /// A screenshot was taken when the button was pressed.
    /// </summary>
    bool _feedbackHasShot;

    /// <summary>
    /// A navmesh screenshot was taken (only in a game).
    /// </summary>
    bool _feedbackHasNav;

    /// <summary>
    /// The status line is an error.
    /// </summary>
    bool _feedbackStatusError;

    /// <summary>
    /// Why no save can be made now; null when one can.
    /// </summary>
    string? _feedbackSaveMissing;

    /// <summary>
    /// The send's progress or why it failed; empty for none.
    /// </summary>
    string _feedbackStatus = "";

    /// <summary>
    /// The player chose to send from this older version anyway (until the form is opened again).
    /// </summary>
    bool _feedbackOldConfirmed;

    /// <summary>
    /// The button's layout (<see cref="FeedbackButtonLayout"/>); null before the UI is read.
    /// </summary>
    internal MyGuiLayout? ButtonLayout;

    /// <summary>
    /// This frame is the feedback screenshot: drawn without the menus, the form and the button.
    /// </summary>
    public bool ShotWithoutMenus { get; set; }

    /// <summary>
    /// A send is under way: the form's Send waits for it.
    /// </summary>
    public bool FeedbackSending { get; set; }

    /// <summary>
    /// The form's ticks and Discord name: the host hands in the saved ones and saves them on a send.
    /// </summary>
    public FeedbackSettings FeedbackPrefs { get; set; } = new();

    /// <summary>
    /// The tag of a newer Meitou than the running one (<see cref="LatestRelease"/>), or null: the form says so and Send asks first.
    /// </summary>
    public string? FeedbackNewer { get; set; }

    #endregion

    #region Layouts

    /// <summary>
    /// The button's layout: a Kenshi button at the top right, a tenth of the width wide.
    /// </summary>
    internal static MyGuiLayout FeedbackButtonLayout() => MyGuiReader.ParseLayout(
        $"""<MyGUI type="Layout"><Widget type="Button" skin="Kenshi_Button1" position_real="0.9 0 0.1 0.03" layer="Info" name="{FeedbackButton}"/></MyGUI>""");

    /// <summary>
    /// The form's layout. Positions are written as in the form before the navmesh tick (a 0.62 high window) and scaled to the window's height;
    /// the rows from the navmesh tick down sit one 0.055 row lower.
    /// </summary>
    internal static MyGuiLayout FeedbackLayout()
    {
        const double oldHeight = 0.62, height = 0.66, row = 0.055;
        static string F(double old) => (old * oldHeight / height).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        static string Tick(string tick, double y) =>
            $"""
            <Widget type="Button" skin="Kenshi_TickBoxSkin" position_real="0.03 {F(y)} 0.05 {F(0.054)}" name="{tick}"/>
            <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.09 {F(y)} 0.88 {F(0.054)}" name="lb{tick}"><Property key="TextAlign" value="Left VCenter"/></Widget>
            """;
        return MyGuiReader.ParseLayout($"""
            <MyGUI type="Layout">
              <Widget type="Window" skin="Kenshi_WindowCX" position_real="0 0 0.36 {height.ToString(System.Globalization.CultureInfo.InvariantCulture)}" layer="Window" name="Root">
                <Property key="Caption" value="SEND FEEDBACK"/>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 {F(0.02)} 0.94 {F(0.1)}" name="lbIntro"><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/></Widget>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 {F(0.13)} 0.94 {F(0.045)}" name="lbTitleBox"><Property key="TextAlign" value="Left VCenter"/></Widget>
                <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.03 {F(0.175)} 0.94 {F(0.065)}" name="{FeedbackTitleBox}"><Property key="MaxTextLength" value="100"/></Widget>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 {F(0.255)} 0.94 {F(0.045)}" name="lbBodyBox"><Property key="TextAlign" value="Left VCenter"/></Widget>
                <Widget type="EditBox" skin="Kenshi_WordWrap" position_real="0.03 {F(0.3)} 0.94 {F(0.25)}" name="{FeedbackBodyBox}">
                  <Property key="Static" value="false"/><Property key="MaxTextLength" value="5000"/><Property key="MultiLine" value="true"/><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/>
                </Widget>
                {Tick(FeedbackShotTick, 0.57)}
                {Tick(FeedbackNavTick, 0.57 + row)}
                {Tick(FeedbackSaveTick, 0.57 + 2 * row)}
                {Tick(FeedbackLogTick, 0.57 + 3 * row)}
                <Widget type="Button" skin="Kenshi_TickBoxSkin" position_real="0.03 {F(0.57 + 4 * row)} 0.05 {F(0.054)}" name="{FeedbackDiscordTick}"/>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.09 {F(0.57 + 4 * row)} 0.4 {F(0.054)}" name="lb{FeedbackDiscordTick}"><Property key="TextAlign" value="Left VCenter"/></Widget>
                <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.5 {F(0.565 + 4 * row)} 0.47 {F(0.065)}" name="{FeedbackDiscordBox}"><Property key="MaxTextLength" value="64"/></Widget>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 {F(0.8 + row)} 0.94 {F(0.085)}" name="StatusText"><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/></Widget>
                <Widget type="Button" skin="Kenshi_Button1" position_real="0.53 {F(0.895 + row)} 0.215 {F(0.08)}" name="SendButton"><Property key="DefaultAction" value="true"/></Widget>
                <Widget type="Button" skin="Kenshi_Button1" position_real="0.765 {F(0.895 + row)} 0.215 {F(0.08)}" name="CancelButton"/>
              </Widget>
            </MyGUI>
            """);
    }

    #endregion

    #region Button

    /// <summary>
    /// The button at the top right, over everything; left out of the feedback screenshot and while the form is open, and during play when
    /// <see cref="PlayerInterface.FeedbackButtonInPlay"/> is off unless the title or the game menu is open.
    /// </summary>
    internal void AddFeedbackButton(List<UiControl> controls)
    {
        _feedbackButtonBounds = default;
        if (ButtonLayout is null || ShotWithoutMenus || player.FaithfulScreens.IsOpen(FeedbackScreen)) return;
        if (!player.FeedbackButtonInPlay && !player.TitleOpen && !player.FaithfulScreens.IsOpen("menu")) return;

        float scale = player.UiScale;
        float w = 150 * scale;
        float h = 36 * scale;
        var b = new UiBindings();
        b.Positions[FeedbackButton] = _feedbackButtonBounds = new(player.Width - w - 8 * scale, 8 * scale, w, h);
        b.Text[FeedbackButton] = FrontendScreens.UpperCaption("Feedback");
        b.Actions[FeedbackButton] = "feedback-open";
        controls.AddRange(MyGuiPresentation.Arrange(ButtonLayout, player.Width, player.Height, b, player.UiResources.Layers));
    }

    /// <summary>
    /// A press on the feedback button, before anything else (it is over every screen): the host takes the screenshot on the next frame and then opens
    /// the form.
    /// </summary>
    internal bool FeedbackButtonDown(System.Numerics.Vector2 p)
    {
        if (!player.UiVisible || !_feedbackButtonBounds.Contains(p.X, p.Y)) return false;

        player.InterfaceSounds.Sound("General_Click");
        player.RaiseScreenAction(new(FeedbackScreen, FeedbackButton, "feedback-open"));
        return true;
    }

    #endregion

    #region Form

    /// <summary>
    /// Opens the form; <paramref name="screenshot"/>: a picture was taken; <paramref name="navmesh"/>: a navmesh picture can be made;
    /// <paramref name="saveMissing"/>: why no save can be made, or null.
    /// </summary>
    public void OpenFeedback(bool screenshot, bool navmesh, string? saveMissing)
    {
        _feedbackHasShot = screenshot;
        _feedbackHasNav = navmesh;
        _feedbackSaveMissing = saveMissing;
        if (!FeedbackSending)
        {
            _feedbackStatus = "";
            _feedbackStatusError = false;
        }

        _feedbackOldConfirmed = false;
        player.FaithfulScreens.SetText(FeedbackScreen, FeedbackDiscordBox, FeedbackPrefs.DiscordName);
        player.FaithfulScreens.Open(FeedbackScreen);
        player.UpdateMenuPause();
    }

    /// <summary>
    /// The form's title, text, the ticked attachments the game has and the Discord name (when ticked and not empty).
    /// </summary>
    public FeedbackForm FeedbackValues()
    {
        string title = player.FaithfulScreens.GetText(FeedbackScreen, FeedbackTitleBox).Trim();
        string body = player.FaithfulScreens.GetText(FeedbackScreen, FeedbackBodyBox).Trim();
        string? discord = FeedbackPrefs.Discord && FeedbackPrefs.DiscordName.Trim() is { Length: > 0 } tag ? tag : null;
        return new(title, body,
            FeedbackPrefs.Screenshot && _feedbackHasShot, FeedbackPrefs.Navmesh && _feedbackHasNav, FeedbackPrefs.Save && _feedbackSaveMissing is null,
            FeedbackPrefs.Log, discord);
    }

    /// <summary>
    /// The send's progress or why it failed, under the ticks (a failure also as a notice once the form is closed).
    /// </summary>
    public void FeedbackProgress(string text, bool error = false)
    {
        (_feedbackStatus, _feedbackStatusError) = (text, error);

        // the form was closed while it sent
        if (error && !player.FaithfulScreens.IsOpen(FeedbackScreen)) player.Notice($"Feedback not sent: {text}", true);
    }

    /// <summary>
    /// The issue is open: the form closes, emptied, and a message thanks the player (its first button copies the link).
    /// </summary>
    public void FeedbackSent(int number, string url)
    {
        player.FaithfulScreens.SetText(FeedbackScreen, FeedbackTitleBox, "");
        player.FaithfulScreens.SetText(FeedbackScreen, FeedbackBodyBox, "");
        (_feedbackStatus, _feedbackStatusError) = ("", false);
        player.FaithfulScreens.Close(FeedbackScreen);
        string thanks = $"Thanks for the feedback! It is now issue #{number} on GitHub.";
        player.Confirm("THANK YOU", thanks, () => player.ClipboardWrite?.Invoke(url), "COPY LINK", cancel: "OK");
        player.UpdateMenuPause();
    }

    /// <summary>
    /// The form's bindings: captions, the ticks as the settings and the attachments the game has allow, the status, and Send only with a title and no
    /// send under way.
    /// </summary>
    internal void BindFeedbackScreen(UiBindings b)
    {
        void Enable(params string[] names)
        {
            foreach (string name in names)
            {
                b.Disabled.Remove(name);
                b.Actions[name] = name;
            }
        }

        void Tick(string name, bool on)
        {
            b.Properties[name] = new Dictionary<string, string> { ["StateSelected"] = on ? "true" : "false", ["Checked"] = on ? "true" : "false" };
        }

        b.Text["Root"] = FrontendScreens.UpperCaption("Send feedback");
        b.Text["lbIntro"] = "Tell us what went wrong or what you think. It becomes a public issue on GitHub, with your Meitou version and load order.";
        b.Text["lbTitleBox"] = "Title";
        b.Text["lbBodyBox"] = "What happened";
        b.Text["lb" + FeedbackShotTick] =
            _feedbackHasShot ? "Attach a screenshot of the game" : "Attach a screenshot (none was taken)";
        b.Text["lb" + FeedbackNavTick] =
            _feedbackHasNav ? "Attach a screenshot with the navmesh drawn over it" : "Attach a navmesh screenshot (only in a game)";
        b.Text["lb" + FeedbackSaveTick] =
            _feedbackSaveMissing is null ? "Save the game now and attach the save" : $"Attach a save ({_feedbackSaveMissing})";
        b.Text["lb" + FeedbackLogTick] = "Attach log.txt (it can show folder names, such as your user name)";
        b.Text["lb" + FeedbackDiscordTick] = "Include my Discord name";
        bool old = _feedbackStatus.Length == 0 && FeedbackNewer is not null;
        b.Text["StatusText"] = old
            ? $"You are on {BuildVersion.Tag}; {FeedbackNewer} is out. Please check whether this still happens there first."
            : _feedbackStatus;
        if (_feedbackStatusError || old) b.Properties["StatusText"] = new Dictionary<string, string> { ["TextColour"] = PlayerInterface.BadColour };
        b.Text["SendButton"] = FrontendScreens.UpperCaption(FeedbackSending ? "Sending" : "Send");
        b.Text["CancelButton"] = FrontendScreens.UpperCaption("Close");
        Enable("CancelButton", FeedbackLogTick, FeedbackDiscordTick);
        if (!FeedbackSending && player.FaithfulScreens.GetText(FeedbackScreen, FeedbackTitleBox).Trim().Length > 0) Enable("SendButton");
        if (_feedbackHasShot) Enable(FeedbackShotTick);
        if (_feedbackHasNav) Enable(FeedbackNavTick);
        if (_feedbackSaveMissing is null) Enable(FeedbackSaveTick);
        Tick(FeedbackShotTick, FeedbackPrefs.Screenshot && _feedbackHasShot);
        Tick(FeedbackNavTick, FeedbackPrefs.Navmesh && _feedbackHasNav);
        Tick(FeedbackSaveTick, FeedbackPrefs.Save && _feedbackSaveMissing is null);
        Tick(FeedbackLogTick, FeedbackPrefs.Log);
        Tick(FeedbackDiscordTick, FeedbackPrefs.Discord);
        if (!FeedbackPrefs.Discord) b.Disabled.Add(FeedbackDiscordBox);
    }

    /// <summary>
    /// A form event: Close, a tick flipped, the Discord name typed, or Send (with a title, not while sending).
    /// </summary>
    internal void FeedbackFormAction(MyGuiEvent e)
    {
        if (e.Action != "text") player.InterfaceSounds.Sound("General_Click");
        if (e.Widget == "CancelButton") player.FaithfulScreens.Close(FeedbackScreen);
        else if (e.Widget == FeedbackShotTick) FeedbackPrefs.Screenshot = !FeedbackPrefs.Screenshot;
        else if (e.Widget == FeedbackNavTick) FeedbackPrefs.Navmesh = !FeedbackPrefs.Navmesh;
        else if (e.Widget == FeedbackSaveTick) FeedbackPrefs.Save = !FeedbackPrefs.Save;
        else if (e.Widget == FeedbackLogTick) FeedbackPrefs.Log = !FeedbackPrefs.Log;
        else if (e.Widget == FeedbackDiscordTick) FeedbackPrefs.Discord = !FeedbackPrefs.Discord;
        else if (e.Widget == FeedbackDiscordBox && e.Action == "text") FeedbackPrefs.DiscordName = e.Text;
        else if (e.Widget == "SendButton" && !FeedbackSending && FeedbackValues().Title.Length > 0) SendFeedbackOrAsk();
        player.UpdateMenuPause();
    }

    /// <summary>
    /// Send, but from an older version than the newest only once the player has been asked to try the newest one first.
    /// </summary>
    void SendFeedbackOrAsk()
    {
        if (FeedbackNewer is not { } newer || _feedbackOldConfirmed)
        {
            Send();
            return;
        }

        string warning = $"You are playing {BuildVersion.Tag}, and {newer} is out. It may already be fixed there: please update and check "
            + "whether it still happens before sending.";
        player.Confirm("Newer version out", warning, SendAnyway, "Send anyway", cancel: "Cancel");
    }

    /// <summary>
    /// The player sends from the older version anyway: the form does not ask again until it is opened again.
    /// </summary>
    void SendAnyway()
    {
        _feedbackOldConfirmed = true;
        Send();
    }

    /// <summary>
    /// Raises the form's send for the host, which makes the attachments and sends the report.
    /// </summary>
    void Send() => player.RaiseScreenAction(new(FeedbackScreen, "SendButton", "feedback-send"));

    #endregion
}