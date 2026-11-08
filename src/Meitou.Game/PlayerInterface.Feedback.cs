using Meitou.Data.Ui;
using Meitou.Game.Host;
using Meitou.Game.Ui;

namespace Meitou.Game;

/// <summary>What the player filled in on the feedback form: the attachments are the ticked ones the game can make.</summary>
sealed record FeedbackForm(string Title, string Body, bool Screenshot, bool Navmesh, bool Save, bool Log, string? Discord);

/// <summary>The form's ticks and the Discord name, kept in meitou.user.json (<see cref="UserConfig.Feedback"/>) so the next report starts from them.</summary>
sealed class FeedbackSettings
{
    public bool Screenshot { get; set; } = true;
    /// <summary>A second screenshot with the navmesh drawn over the world (off by default: it takes a moment to make).</summary>
    public bool Navmesh { get; set; }
    public bool Save { get; set; } = true;
    public bool Log { get; set; }
    /// <summary>Send <see cref="DiscordName"/> with the report (default on), so it can be followed up on Discord.</summary>
    public bool Discord { get; set; } = true;
    public string DiscordName { get; set; } = "";
}

/// <summary>
/// Meitou's feedback button, always at the top right in both UI modes, and the form it opens: a title, the text, and ticks for a screenshot
/// (taken when the button is pressed, without the menus), a save made when sending and log.txt. The player's Discord name goes along unless unticked; the ticks and the name
/// are remembered (<see cref="FeedbackSettings"/>). The host (GameHost.Feedback.cs) takes the
/// picture, makes the files and sends them to the reports service, which opens a GitHub issue (docs/play-reports.md "Feedback").
/// </summary>
sealed partial class PlayerInterface
{
    const string FeedbackScreen = "feedback", FeedbackButton = "MeitouFeedbackButton";
    const string FeedbackTitleBox = "TitleEditBox", FeedbackBodyBox = "BodyEditBox", FeedbackShotTick = "ScreenshotTickBox", FeedbackNavTick = "NavmeshTickBox", FeedbackSaveTick = "SaveTickBox", FeedbackLogTick = "LogTickBox",
        FeedbackDiscordTick = "DiscordTickBox", FeedbackDiscordBox = "DiscordEditBox";
    /// <summary>The screens a feedback screenshot leaves out: the game menu and what it opens, and the form itself.</summary>
    static readonly HashSet<string> FeedbackShotHides = ["menu", "message", "save", "load", "import", "new-game", "file", FeedbackScreen];

    MyGuiLayout? feedbackButtonLayout;
    /// <summary>Where the last arranged frame put the button (empty: not shown).</summary>
    UiRect feedbackButtonBounds;
    bool feedbackHasShot, feedbackHasNav, feedbackStatusError;
    string? feedbackSaveMissing;
    string feedbackStatus = "";

    /// <summary>This frame is the feedback screenshot: drawn without the menus, the form and the button.</summary>
    public bool ShotWithoutMenus { get; set; }
    /// <summary>A send is under way: the form's Send waits for it.</summary>
    public bool FeedbackSending { get; set; }
    /// <summary>The form's ticks and Discord name: the host hands in the saved ones and saves them on a send.</summary>
    public FeedbackSettings FeedbackPrefs { get; set; } = new();
    /// <summary>The tag of a newer Meitou than the running one (<see cref="LatestRelease"/>), or null: the form says so and Send asks first.</summary>
    public string? FeedbackNewer { get; set; }
    /// <summary>The player chose to send from this older version anyway (until the form is opened again).</summary>
    bool feedbackOldConfirmed;
    /// <summary>The button during play (<see cref="UserConfig.FeedbackButton"/>); off, it still shows on the title and with the game menu open.</summary>
    public bool FeedbackButtonInPlay { get; set; } = true;

    static MyGuiLayout FeedbackButtonLayout() => MyGuiReader.ParseLayout(
        $"""<MyGUI type="Layout"><Widget type="Button" skin="Kenshi_Button1" position_real="0.9 0 0.1 0.03" layer="Info" name="{FeedbackButton}"/></MyGUI>""");

    /// <summary>
    /// The form's layout. Positions are written as in the form before the navmesh tick (a 0.62 high window) and scaled to the window's height;
    /// the rows from the navmesh tick down sit one 0.055 row lower.
    /// </summary>
    static MyGuiLayout FeedbackLayout()
    {
        const double OldHeight = 0.62, Height = 0.66, Row = 0.055;
        static string F(double old) => (old * OldHeight / Height).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        static string Tick(string tick, double y) =>
            $"""
            <Widget type="Button" skin="Kenshi_TickBoxSkin" position_real="0.03 {F(y)} 0.05 {F(0.054)}" name="{tick}"/>
            <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.09 {F(y)} 0.88 {F(0.054)}" name="lb{tick}"><Property key="TextAlign" value="Left VCenter"/></Widget>
            """;
        return MyGuiReader.ParseLayout($"""
            <MyGUI type="Layout">
              <Widget type="Window" skin="Kenshi_WindowCX" position_real="0 0 0.36 {Height.ToString(System.Globalization.CultureInfo.InvariantCulture)}" layer="Window" name="Root">
                <Property key="Caption" value="SEND FEEDBACK"/>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 {F(0.02)} 0.94 {F(0.1)}" name="lbIntro"><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/></Widget>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 {F(0.13)} 0.94 {F(0.045)}" name="lbTitleBox"><Property key="TextAlign" value="Left VCenter"/></Widget>
                <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.03 {F(0.175)} 0.94 {F(0.065)}" name="{FeedbackTitleBox}"><Property key="MaxTextLength" value="100"/></Widget>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 {F(0.255)} 0.94 {F(0.045)}" name="lbBodyBox"><Property key="TextAlign" value="Left VCenter"/></Widget>
                <Widget type="EditBox" skin="Kenshi_WordWrap" position_real="0.03 {F(0.3)} 0.94 {F(0.25)}" name="{FeedbackBodyBox}">
                  <Property key="Static" value="false"/><Property key="MaxTextLength" value="5000"/><Property key="MultiLine" value="true"/><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/>
                </Widget>
                {Tick(FeedbackShotTick, 0.57)}
                {Tick(FeedbackNavTick, 0.57 + Row)}
                {Tick(FeedbackSaveTick, 0.57 + 2 * Row)}
                {Tick(FeedbackLogTick, 0.57 + 3 * Row)}
                <Widget type="Button" skin="Kenshi_TickBoxSkin" position_real="0.03 {F(0.57 + 4 * Row)} 0.05 {F(0.054)}" name="{FeedbackDiscordTick}"/>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.09 {F(0.57 + 4 * Row)} 0.4 {F(0.054)}" name="lb{FeedbackDiscordTick}"><Property key="TextAlign" value="Left VCenter"/></Widget>
                <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.5 {F(0.565 + 4 * Row)} 0.47 {F(0.065)}" name="{FeedbackDiscordBox}"><Property key="MaxTextLength" value="64"/></Widget>
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.03 {F(0.8 + Row)} 0.94 {F(0.085)}" name="StatusText"><Property key="WordWrap" value="true"/><Property key="TextAlign" value="Left Top"/></Widget>
                <Widget type="Button" skin="Kenshi_Button1" position_real="0.53 {F(0.895 + Row)} 0.215 {F(0.08)}" name="SendButton"><Property key="DefaultAction" value="true"/></Widget>
                <Widget type="Button" skin="Kenshi_Button1" position_real="0.765 {F(0.895 + Row)} 0.215 {F(0.08)}" name="CancelButton"/>
              </Widget>
            </MyGUI>
            """);
    }

    /// <summary>The button at the top right, over everything; left out of the feedback screenshot and while the form is open, and during play when
    /// <see cref="FeedbackButtonInPlay"/> is off unless the title or the game menu is open.</summary>
    void AddFeedbackButton(List<UiControl> controls)
    {
        feedbackButtonBounds = default;
        if (feedbackButtonLayout is null || ShotWithoutMenus || faithfulScreens.IsOpen(FeedbackScreen)) return;
        if (!FeedbackButtonInPlay && !TitleOpen && !faithfulScreens.IsOpen("menu")) return;
        float scale = Height / 1080f, w = 150 * scale, h = 36 * scale;
        var b = new UiBindings();
        b.Positions[FeedbackButton] = feedbackButtonBounds = new(Width - w - 8 * scale, 8 * scale, w, h);
        b.Text[FeedbackButton] = UpperCaption("Feedback");
        b.Actions[FeedbackButton] = "feedback-open";
        controls.AddRange(MyGuiPresentation.Arrange(feedbackButtonLayout, Width, Height, b, uiResources.Layers));
    }

    /// <summary>A press on the feedback button, before anything else (it is over every screen): the host takes the screenshot on the next frame and then opens the form.</summary>
    bool FeedbackButtonDown(System.Numerics.Vector2 p)
    {
        if (!UiVisible || !feedbackButtonBounds.Contains(p.X, p.Y)) return false;
        Sound("General_Click");
        ScreenAction?.Invoke(new(FeedbackScreen, FeedbackButton, "feedback-open"));
        return true;
    }

    /// <summary>Opens the form; <paramref name="screenshot"/>: a picture was taken; <paramref name="navmesh"/>: a navmesh picture can be made; <paramref name="saveMissing"/>: why no save can be made, or null.</summary>
    public void OpenFeedback(bool screenshot, bool navmesh, string? saveMissing)
    {
        feedbackHasShot = screenshot;
        feedbackHasNav = navmesh;
        feedbackSaveMissing = saveMissing;
        if (!FeedbackSending) { feedbackStatus = ""; feedbackStatusError = false; }
        feedbackOldConfirmed = false;
        faithfulScreens.SetText(FeedbackScreen, FeedbackDiscordBox, FeedbackPrefs.DiscordName);
        faithfulScreens.Open(FeedbackScreen);
        UpdateMenuPause();
    }

    public FeedbackForm FeedbackValues() => new(faithfulScreens.GetText(FeedbackScreen, FeedbackTitleBox).Trim(), faithfulScreens.GetText(FeedbackScreen, FeedbackBodyBox).Trim(),
        FeedbackPrefs.Screenshot && feedbackHasShot, FeedbackPrefs.Navmesh && feedbackHasNav, FeedbackPrefs.Save && feedbackSaveMissing is null, FeedbackPrefs.Log,
        FeedbackPrefs.Discord && FeedbackPrefs.DiscordName.Trim() is { Length: > 0 } discord ? discord : null);

    /// <summary>The send's progress or why it failed, under the ticks (a failure also as a notice once the form is closed).</summary>
    public void FeedbackProgress(string text, bool error = false)
    {
        (feedbackStatus, feedbackStatusError) = (text, error);
        if (error && !faithfulScreens.IsOpen(FeedbackScreen)) Notice($"Feedback not sent: {text}", true);   // the form was closed while it sent
    }

    /// <summary>The issue is open: the form closes, emptied, and a message thanks the player (its first button copies the link).</summary>
    public void FeedbackSent(int number, string url)
    {
        faithfulScreens.SetText(FeedbackScreen, FeedbackTitleBox, "");
        faithfulScreens.SetText(FeedbackScreen, FeedbackBodyBox, "");
        (feedbackStatus, feedbackStatusError) = ("", false);
        faithfulScreens.Close(FeedbackScreen);
        Confirm("THANK YOU", $"Thanks for the feedback! It is now issue #{number} on GitHub.", () => ClipboardWrite?.Invoke(url), "COPY LINK", cancel: "OK");
        UpdateMenuPause();
    }

    void BindFeedbackScreen(UiBindings b)
    {
        void Enable(params string[] names) { foreach (string name in names) { b.Disabled.Remove(name); b.Actions[name] = name; } }
        void Tick(string name, bool on) => b.Properties[name] = new Dictionary<string, string> { ["StateSelected"] = on ? "true" : "false", ["Checked"] = on ? "true" : "false" };
        b.Text["Root"] = UpperCaption("Send feedback");
        b.Text["lbIntro"] = "Tell us what went wrong or what you think. It becomes a public issue on GitHub, with your Meitou version and load order.";
        b.Text["lbTitleBox"] = "Title";
        b.Text["lbBodyBox"] = "What happened";
        b.Text["lb" + FeedbackShotTick] = feedbackHasShot ? "Attach a screenshot of the game" : "Attach a screenshot (none was taken)";
        b.Text["lb" + FeedbackNavTick] = feedbackHasNav ? "Attach a screenshot with the navmesh drawn over it" : "Attach a navmesh screenshot (only in a game)";
        b.Text["lb" + FeedbackSaveTick] = feedbackSaveMissing is null ? "Save the game now and attach the save" : $"Attach a save ({feedbackSaveMissing})";
        b.Text["lb" + FeedbackLogTick] = "Attach log.txt (it can show folder names, such as your user name)";
        b.Text["lb" + FeedbackDiscordTick] = "Include my Discord name";
        bool old = feedbackStatus.Length == 0 && FeedbackNewer is not null;
        b.Text["StatusText"] = old ? $"You are on {BuildVersion.Tag}; {FeedbackNewer} is out. Please check whether this still happens there first." : feedbackStatus;
        if (feedbackStatusError || old) b.Properties["StatusText"] = new Dictionary<string, string> { ["TextColour"] = BadColour };
        b.Text["SendButton"] = UpperCaption(FeedbackSending ? "Sending" : "Send");
        b.Text["CancelButton"] = UpperCaption("Close");
        Enable("CancelButton", FeedbackLogTick, FeedbackDiscordTick);
        if (!FeedbackSending && faithfulScreens.GetText(FeedbackScreen, FeedbackTitleBox).Trim().Length > 0) Enable("SendButton");
        if (feedbackHasShot) Enable(FeedbackShotTick);
        if (feedbackHasNav) Enable(FeedbackNavTick);
        if (feedbackSaveMissing is null) Enable(FeedbackSaveTick);
        Tick(FeedbackShotTick, FeedbackPrefs.Screenshot && feedbackHasShot);
        Tick(FeedbackNavTick, FeedbackPrefs.Navmesh && feedbackHasNav);
        Tick(FeedbackSaveTick, FeedbackPrefs.Save && feedbackSaveMissing is null);
        Tick(FeedbackLogTick, FeedbackPrefs.Log);
        Tick(FeedbackDiscordTick, FeedbackPrefs.Discord);
        if (!FeedbackPrefs.Discord) b.Disabled.Add(FeedbackDiscordBox);
    }

    void FeedbackFormAction(MyGuiEvent e)
    {
        if (e.Action != "text") Sound("General_Click");
        if (e.Widget == "CancelButton") faithfulScreens.Close(FeedbackScreen);
        else if (e.Widget == FeedbackShotTick) FeedbackPrefs.Screenshot = !FeedbackPrefs.Screenshot;
        else if (e.Widget == FeedbackNavTick) FeedbackPrefs.Navmesh = !FeedbackPrefs.Navmesh;
        else if (e.Widget == FeedbackSaveTick) FeedbackPrefs.Save = !FeedbackPrefs.Save;
        else if (e.Widget == FeedbackLogTick) FeedbackPrefs.Log = !FeedbackPrefs.Log;
        else if (e.Widget == FeedbackDiscordTick) FeedbackPrefs.Discord = !FeedbackPrefs.Discord;
        else if (e.Widget == FeedbackDiscordBox && e.Action == "text") FeedbackPrefs.DiscordName = e.Text;
        else if (e.Widget == "SendButton" && !FeedbackSending && FeedbackValues().Title.Length > 0) SendFeedbackOrAsk();
        UpdateMenuPause();
    }

    /// <summary>Send, but from an older version than the newest only once the player has been asked to try the newest one first.</summary>
    void SendFeedbackOrAsk()
    {
        void Send() => ScreenAction?.Invoke(new(FeedbackScreen, "SendButton", "feedback-send"));
        if (FeedbackNewer is not { } newer || feedbackOldConfirmed)
        {
            Send();
            return;
        }

        Confirm("Newer version out", $"You are playing {BuildVersion.Tag}, and {newer} is out. It may already be fixed there: please update and check "
            + "whether it still happens before sending.", () => { feedbackOldConfirmed = true; Send(); }, "Send anyway", cancel: "Cancel");
    }
}
