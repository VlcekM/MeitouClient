using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>The colour of a DIALOGUE log line (the original's <c>MessageLogColor</c>: ML_NORMAL, ML_PLAYER, ML_SYSTEM).</summary>
enum MessageTone { Normal, Player, System }

/// <summary>The minimal HUD: the clock, the speed buttons, the selection panel, rings, paths and the drag box.</summary>
sealed partial class PlayerInterface
{

    /// <summary>Pause, then the clock's speeds.</summary>
    static readonly double[] ButtonSpeeds = [0, .. SimulationClock.Speeds];
    const float Margin = 10, Pad = 6;

    public float HudBottom => 48;

    // ---- notices (the original's MessageRoller, docs/game/ui-screens.md "Messages") ----

    /// <summary>The seconds a message holds at full alpha, then the alpha it loses per second (<b>Verified</b>).</summary>
    const float NoticeHoldSeconds = 4, NoticeFadeRate = 3;

    readonly Queue<(string Text, bool Error)> noticeQueue = new();
    (string Text, bool Error)? notice;
    float noticeHold, noticeAlpha;
    long noticeAt;
    MyGuiLayout? messageLayout;
    string? messageClient;

    /// <summary>
    /// Queues a message for the message panel ("Discovered {1}", "Saved", or an error text in red); each shows for 4 s in turn. <paramref name="logText"/>: a longer
    /// text for the message log (the overview's messages) when the panel's line is a summary of it.
    /// </summary>
    public void Notice(string text, bool error = false, string? logText = null)
    {
        string line = Loc.Tr(text).ReplaceLineEndings(" ");
        LogMessage("", logText?.ReplaceLineEndings(" ") ?? line, MessageTone.System);
        noticeQueue.Enqueue((line, error));
    }

    /// <summary>Adds a line to the Overview's DIALOGUE log only ("owner: text", docs/game/ui-screens.md "Dialogue log"); the newest 200 are kept.</summary>
    public void LogMessage(string owner, string text, MessageTone tone)
    {
        uiMessages.Add((owner.Length == 0 ? text : $"{owner}: {text}", tone));
        if (uiMessages.Count > 200) uiMessages.RemoveAt(0);
    }

    /// <summary>
    /// The DIALOGUE log as the text box's text: each line after the colour tag of its tone (the original's MessageLogColor: #000000, <c>DialogueTextOption</c>, #c6ac6a),
    /// its own <c>#</c> doubled. The Meitou mode's dark panel takes the kit's text colour for the black.
    /// </summary>
    string MessagesText()
    {
        string normal = UiMode == UiMode.Meitou ? Tag(MainColour) : "#000000", player = Tag(MyGuiColours.Named("DialogueTextOption", "b3b3b3"));
        return string.Join('\n', uiMessages.Select(m => (m.Tone switch { MessageTone.Player => player, MessageTone.System => "#c6ac6a", _ => normal })
            + m.Text.Replace("#", "##", StringComparison.Ordinal)));
    }

    /// <summary>A MyGUI "r g b" colour as a <c>#rrggbb</c> tag.</summary>
    static string Tag(string rgb)
    {
        var c = MyGuiColours.Parse(rgb, Vector4.One);
        return $"#{(int)MathF.Round(c.X * 255):x2}{(int)MathF.Round(c.Y * 255):x2}{(int)MathF.Round(c.Z * 255):x2}";
    }

    /// <summary>Once a frame: the next queued message comes up at full alpha when none shows; the shown one holds, then fades out.</summary>
    void PollNotice()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = noticeAt == 0 ? 0 : MathF.Min((float)System.Diagnostics.Stopwatch.GetElapsedTime(noticeAt, now).TotalSeconds, 0.25f);
        noticeAt = now;
        if (notice is null)
        {
            if (noticeQueue.TryDequeue(out var next)) (notice, noticeHold, noticeAlpha) = (next, NoticeHoldSeconds, 1);
            return;
        }
        if ((noticeHold -= dt) < 0 && (noticeAlpha -= dt * NoticeFadeRate) <= 0) notice = null;
    }

    /// <summary>The shown message in the game's <c>Kenshi_MessagePanel</c> layout, placed where the HUD's <c>MessagePanel</c> is; it never takes the mouse.</summary>
    void AddNotice(List<UiControl> controls)
    {
        if (notice is not { } shown || messageLayout is null || messageLayout.Roots.Count == 0) return;
        var root = messageLayout.Roots[0];
        var b = new UiBindings();
        if (faithfulHud.Find("MessagePanel") is { RealPosition: true } slot) b.RelativePositions[root] = root.Position with { X = slot.Position.X, Y = slot.Position.Y };
        b.Text["MessageTextBox"] = shown.Text;
        b.Properties[root.Name] = new Dictionary<string, string> { ["Alpha"] = Math.Clamp(noticeAlpha, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var w in messageLayout.Widgets.Where(w => w != root)) b.Properties[w.Name] = new Dictionary<string, string> { ["NeedMouse"] = "false" };
        // The EditBox's alignment is its text's, which the template's client draws.
        if (messageClient is not null)
            b.Properties[messageClient] = new Dictionary<string, string> { ["NeedMouse"] = "false", ["TextAlign"] = "Center", ["TextColour"] = shown.Error ? BadColour : MyGuiColours.TemplateInk(uiResources, "Kenshi_WordWrapEmpty_StandardText_Large") };
        controls.AddRange(MyGuiPresentation.Arrange(messageLayout, Width, Height, b, uiResources.Layers).Select(c => c with { Pickable = false }));
    }

    /// <summary>Drops the transient state of the mouse (a drag box, a steering right click): the world it belonged to is gone.</summary>
    public void Reset()
    {
        floaters.Clear();
        dragFrom = null;
        dragging = false;
        steering = false;
        mapDrag = null;
        (held, liftPress, lifting, heldSerial) = (null, null, null, 0);
        selectedFor = null;
        selected = [];
        selectedBuilding = null;
        Follow = null;
        CloseContextMenu();
    }

    bool OverHud(Vector2 p) => ContainsUi(p);

    void PressButton(int index)
    {
        session.Simulation.SetSpeed(ButtonSpeeds[index]);   // the pause button (0) never resumes; only the pause key does
    }

    /// <summary>The selected characters of the current snapshot, found once per snapshot (the HUD box is asked for on every hit test).</summary>
    IReadOnlyList<CharacterSnapshot> Selected()
    {
        var snapshot = session.CurrentSnapshot;
        if (!ReferenceEquals(snapshot, selectedFor))
        {
            selected = [.. snapshot.Characters.Where(c => c.Selected)];
            inspected = snapshot.Characters.FirstOrDefault(c => c.Inspected);
            selectedFor = snapshot;
        }
        return selected;
    }

    WorldSnapshot? selectedFor;
    IReadOnlyList<CharacterSnapshot> selected = [];
    CharacterSnapshot? inspected;

    /// <summary>The character the HUD panels describe: the other faction's character last clicked, else the first selected one (docs/game/ui-screens.md section 4). The selection stays whoever is inspected.</summary>
    CharacterSnapshot? Lead() => Selected().FirstOrDefault() is { } first ? inspected ?? first : inspected;

    /// <summary>The HUD and the drag box, over the finished picture.</summary>
    public void Draw(DebugOverlay o, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        PollUiSounds();
        Width = width;
        Height = height;
        PollBiome();
        PollNotice();
        PollProspect();
        PollTutorials();
        UpdateStealthArrows();
        DrawFloaters(o);
        FadeMarker();

        // The box.
        if (dragging && dragFrom is { } from)
        {
            var edge = new Vector4(0.6f, 1f, 0.6f, 1);
            o.Rect(MathF.Min(from.X, mouse.X), MathF.Min(from.Y, mouse.Y), MathF.Max(from.X, mouse.X), MathF.Max(from.Y, mouse.Y), new Vector4(0.4f, 1f, 0.5f, 0.12f));
            o.Line(from.X, from.Y, mouse.X, from.Y, 1, edge);
            o.Line(mouse.X, from.Y, mouse.X, mouse.Y, 1, edge);
            o.Line(mouse.X, mouse.Y, from.X, mouse.Y, 1, edge);
            o.Line(from.X, mouse.Y, from.X, from.Y, 1, edge);
        }

        DrawGameUi(o, width, height, pass);
        o.Flush(width, height, null, pass);
    }
}
