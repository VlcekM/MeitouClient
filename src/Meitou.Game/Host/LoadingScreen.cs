using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Ui;

namespace Meitou.Game.Host;

/// <summary>
/// The loading screen drawn while the game starts and between scenes (docs/engine.md "Loading", docs/game/ui-screens.md "Loading screen"). <see
/// cref="Faithful"/>: the original's <c>Kenshi_LoadingScreen</c> as it shows it (a picture of the <c>Kenshi_LoadingScreen</c> image set, the layout's
/// own panel with "Loading" and its dots and a tip, hidden at once when done). Meitou: one of every <c>loading_screen*.dds</c> picture, the layout's
/// panel made small at the bottom right with the tip, and Meitou's panel at the bottom left: LOADING..., a spinner and an approximate progress bar
/// under it (engine choice); both slide out when done. The bar eases towards the end of the running step by the time the step usually takes, so it
/// moves while a step runs; it never goes back.
/// </summary>
sealed class LoadingScreen : IDisposable
{
    #region Showing

    /// <summary>
    /// The overlay the screen draws its rectangles and text with.
    /// </summary>
    readonly DebugOverlay _overlay;
    /// <summary>
    /// The original's LOADING... face (Kenshi_UI_Messages: MailartRubberstamp-Regular.ttf); null without the font.
    /// </summary>
    DebugOverlay? _stamp;
    /// <summary>
    /// Paints the layout's controls; null when the original's layout did not load (the spinner only).
    /// </summary>
    readonly UiPainter? _painter;
    /// <summary>
    /// The original's MyGUI resources (<c>kenshi_init.xml</c>); null when they did not load.
    /// </summary>
    readonly MyGuiResources? _resources;
    /// <summary>
    /// The expanded <c>Kenshi_LoadingScreen.layout</c>; null without it.
    /// </summary>
    readonly MyGuiLayout? _layout;
    /// <summary>
    /// Picks each showing's tip, picture and group.
    /// </summary>
    readonly Random _random = new();
    /// <summary>
    /// What a showing picks from: the tips, the Meitou pictures (<c>loading_screen*.dds</c>) and the image set's groups (Faithful).
    /// </summary>
    readonly string[] _tips, _pictures, _groups;
    /// <summary>
    /// This showing's picture and image-set group; null when there are none.
    /// </summary>
    string? _picture, _group;
    /// <summary>
    /// This showing's tip, translated; empty without tips.
    /// </summary>
    string _tip = "";
    /// <summary>
    /// When this showing began (seconds of <see cref="_clock"/>): the Faithful dots count from it.
    /// </summary>
    double _shownAt;
    /// <summary>
    /// The screen's clock (seconds) for the steps, the exit and the splash.
    /// </summary>
    readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>
    /// The original's image set of the Faithful pictures.
    /// </summary>
    const string ImageSet = "Kenshi_LoadingScreen";
    /// <summary>
    /// Windows-1252, its code page provider registered once.
    /// </summary>
    static readonly Lazy<System.Text.Encoding> Windows1252 = new(() =>
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(1252);
    });

    /// <summary>
    /// The original's background-loading bar, drawn over the Faithful screen (the host steps it).
    /// </summary>
    public ResourceLoadBar? LoadBar { get; set; }

    /// <summary>
    /// The original's screen (the Faithful UI mode); set by the host before drawing.
    /// </summary>
    public bool Faithful { get; set; }

    /// <summary>
    /// A screen with its resources and what it picks from, the first showing's tip and picture picked.
    /// </summary>
    LoadingScreen(DebugOverlay overlay, UiPainter? painter, MyGuiResources? resources, MyGuiLayout? layout, string[] tips,
        string[] pictures, string[] groups)
    {
        (this._overlay, this._painter, this._resources, this._layout,
            this._tips, this._pictures, this._groups) = (overlay, painter, resources, layout, tips, pictures, groups);
        Pick();
    }

    /// <summary>
    /// What a showing of the screen draws, picked anew each time as the original's window does when shown (<c>FUN_140916ca0</c>): a tip, a picture
    /// (Meitou) and a group of the image set (Faithful: one of its groups by position, shown by name, so <c>MyGuiImages</c> takes the first group of
    /// that name, as MyGUI does).
    /// </summary>
    void Pick()
    {
        _tip = _tips.Length > 0 ? Loc.Tr(_tips[_random.Next(_tips.Length)]) : "";
        _picture = _pictures.Length > 0 ? _pictures[_random.Next(_pictures.Length)] : null;
        _group = _groups.Length > 0 ? _groups[_random.Next(_groups.Length)] : null;
        _shownAt = _clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// The screen with the original's layout, picture and tips when they are found (else the spinner and bar on black).
    /// </summary>
    public static LoadingScreen Create(DebugOverlay overlay, Func<string, string?> resolve, string? dataDirectory, bool faithful)
    {
        // tips.txt through the resource lookup: the active language's own copy (locale/<code>/tips.txt) replaces data/tips.txt; a tip that is also a
        // message of the catalog (main.pot lists the tips) is translated. The original keeps every non-empty line (FUN_14091a280; a written "\n" is a
        // line break, which the painter honours).
        string[] tips = [];
        string? tipsFile = resolve("tips.txt") ?? (dataDirectory is null ? null : Path.Combine(dataDirectory, "tips.txt"));
        if (tipsFile is not null && File.Exists(tipsFile)) tips = ReadTips(tipsFile);
        try
        {
            var resources = MyGuiResources.Load("kenshi_init.xml", resolve, "kenshi_fonts.xml");
            var layout = resolve("Kenshi_LoadingScreen.layout") is { } file ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(file), resources) : null;
            // Meitou: the pictures the original ships (loading_screen0..10, some numbers missing). Faithful: the image set's groups in file order,
            // repeats kept.
            var pictures = Enumerable.Range(0, 16).Select(i => $"loading_screen{i}.dds").Where(n => resolve(n) is not null).ToArray();
            string[] groups = resources.Resources.TryGetValue(ImageSet, out var set)
                ? [.. set.Elements("Group").Select(g => (string?)g.Attribute("name")).OfType<string>()] : [];
            var painter = new UiPainter(overlay, resources, resolve);
            var stamp = resolve("MailartRubberstamp-Regular.ttf") is { } stampFile ? DebugOverlay.FromFont(overlay.Gpu, stampFile, 64) : null;
            return new(overlay, painter, resources, layout, tips, pictures, groups) { _stamp = stamp, Faithful = faithful };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"loading   the original loading screen did not load ({e.Message}); spinner only");
            return new(overlay, null, null, null, tips, [], []) { Faithful = faithful };
        }
    }

    /// <summary>
    /// The non-empty lines of a tips file: by its byte order mark when it has one, else UTF-8 when it is valid UTF-8, else Windows-1252 (the French,
    /// German and Spanish files are; engine choice: how the original's MyGUI strings show those bytes is Unknown).
    /// </summary>
    static string[] ReadTips(string file)
    {
        byte[] bytes = File.ReadAllBytes(file);
        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new System.Text.UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (System.Text.DecoderFallbackException)
        {
            text = Windows1252.Value.GetString(bytes);
        }
        return [.. text.Split(['\r', '\n']).Select(l => l.Trim()).Where(l => l.Length > 0)];
    }

    /// <summary>
    /// Starts the bar again (a new load between scenes), with a new tip and picture.
    /// </summary>
    public void Restart()
    {
        _shown = 0;
        _from = _to = 0;
        _exitStart = null;
        _drawn = false;
        _released = false;
        Pick();
    }

    /// <summary>
    /// One frame of the screen: the splash while it plays, else the Faithful window or Meitou's two panels with the bar, sliding out as it exits;
    /// nothing once gone.
    /// </summary>
    public void Draw(int width, int height, bool background = true)
    {
        float t = ExitProgress;
        if (t >= 1) return;
        _drawn = true;
        if (SplashPlaying && _exitStart is null)
        {
            DrawSplash(width, height);
            return;
        }
        if (Faithful && _painter is not null && _layout is not null && _resources is not null)
        {
            // Hidden at once when done, also when the mode was switched to Faithful during the Meitou slide-out.
            if (_exitStart is null) DrawFaithful(width, height, background);
            return;
        }
        if (_painter is not null) _painter.Mode = UiMode.Meitou;
        float departure = t * t * (3 - 2 * t), opacity = 1 - departure;
        if (background)
        {
            _overlay.Rect(0, 0, width, height, new Vector4(0.02f, 0.02f, 0.018f, opacity));
            _overlay.Flush(width, height);
        }
        float progress = _exitStart is null ? Progress : 1;
        // Two panels along the bottom (engine choice): the loading panel on the left (LOADING..., the spinner, the bar, the step), the layout's own
        // panel on the right, made small and holding only the tip. Sizes at 1080 rows, scaled with the height.
        float s = height / 1080f, margin = 40 * s, panelHeight = 120 * s, top = height - margin - panelHeight;
        float half = Math.Max(width / 2f - 1.5f * margin, 1);
        var box = new UiRect(margin, top, Math.Min(480 * s, half), panelHeight);
        var tipBox = new UiRect(width - margin - Math.Min(480 * s, half), top, Math.Min(480 * s, half), panelHeight);
        float slide = (box.Width + margin + 8 * s) * departure;
        box = box with { X = box.X - slide };
        tipBox = tipBox with { X = tipBox.X + slide };
        DrawPanels(width, height, background, opacity, s, box, tipBox);
        DrawProgressRow(width, height, s, box, progress);
    }

    /// <summary>
    /// The layout's panels round <paramref name="box"/> (the loading panel) and <paramref name="tipBox"/> (the tip), over the picture; a plain
    /// panel without the layout.
    /// </summary>
    void DrawPanels(int width, int height, bool background, float opacity, float s, UiRect box, UiRect tipBox)
    {
        if (_painter is not null && _layout is not null && _resources is not null)
        {
            var b = new UiBindings();
            if (!background) b.Hidden.Add("BackgroundImage");
            var image = new Dictionary<string, string> { ["Alpha"] = opacity.ToString(CultureInfo.InvariantCulture) };
            if (_picture is not null) image["ImageTexture"] = _picture;
            b.Properties["BackgroundImage"] = image;
            b.Positions["LoadingPanel"] = tipBox;
            b.Positions["MessageText"] = new UiRect(tipBox.X + 8 * s, tipBox.Y + 8 * s, tipBox.Width - 16 * s, tipBox.Height - 16 * s);
            b.Hidden.Add("LoadingText");
            b.Text["MessageText"] = _tip;
            b.Properties["MessageText"] = new Dictionary<string, string>
            {
                ["Static"] = "true",
                ["WordWrap"] = "true",
                ["TextAlign"] = "Center",
                ["FontName"] = "Kenshi_StandardFont_Medium",
                ["FontPixels"] = Math.Max(8, (int)MathF.Round(16 * s)).ToString(CultureInfo.InvariantCulture)
            };
            var controls = MyGuiPresentation.Arrange(_layout, width, height, b, _resources.Layers).ToList();
            // The loading panel in the tip panel's frame (Kenshi_FloatingPanelSkin, the layout's LoadingPanel skin).
            controls.Add(new UiControl("LoadingBox", "Widget", box, "", "", "Kenshi_FloatingPanelSkin", true,
                new Dictionary<string, string>(), Vector4.Zero));
            _painter.Draw(controls, width, height, new Vector2(-1, -1), null);
        }
        else _overlay.Rect(box.X, box.Y, box.X + box.Width, box.Y + box.Height, new Vector4(0.08f, 0.07f, 0.06f, 0.85f));
    }

    /// <summary>
    /// The loading panel's row: LOADING... and the running step, the spinner and the progress bar along the panel's bottom.
    /// </summary>
    void DrawProgressRow(int width, int height, float s, UiRect box, float progress)
    {
        // The bar along the panel's bottom; above it LOADING... and the running step on the left, the spinner on the right.
        float pad = 16 * s, barHeight = 10 * s;
        float x0 = box.X + pad, x1 = box.X + box.Width - pad, barTop = box.Y + box.Height - pad - barHeight;
        float rowTop = box.Y + pad * 0.75f, rowHeight = barTop - pad * 0.5f - rowTop, cy = rowTop + rowHeight / 2;
        float textEnd = x0;
        if (_stamp is not null)
        {
            float scale = rowHeight * 0.52f / _stamp.LineHeight, textTop = cy - _stamp.LineHeight * scale / 2;
            _stamp.Target = _overlay.Target;
            _stamp.TextScaled("LOADING...", x0 + 2, textTop + 2, new Vector4(0, 0, 0, 0.8f), scale);
            textEnd = x0 + _stamp.TextScaled("LOADING...", x0, textTop, new Vector4(0.87f, 0.85f, 0.81f, 1), scale);
            _stamp.Flush(width, height);
        }
        else textEnd = x0 + _overlay.Text("LOADING...", x0, cy - _overlay.LineHeight / 2, DebugOverlay.TextColour);

        float outer = Math.Min(rowHeight * 0.45f, 22 * s), inner = outer * 0.45f, cx = x1 - outer;
        DrawSpinner(cx, cy, outer, inner, s);
        if (_label.Length > 0 && textEnd + 12 * s + _label.Length * _overlay.CharWidth <= cx - outer - 12 * s)
            _overlay.Text(_label, textEnd + 12 * s, cy - _overlay.LineHeight / 2, new Vector4(0.62f, 0.6f, 0.52f, 1));
        _overlay.Rect(x0 - 2, barTop - 2, x1 + 2, barTop + barHeight + 2, new Vector4(0.35f, 0.31f, 0.24f, 1));
        _overlay.Rect(x0, barTop, x1, barTop + barHeight, new Vector4(0.08f, 0.07f, 0.06f, 1));
        _overlay.Rect(x0, barTop, x0 + (x1 - x0) * progress, barTop + barHeight, new Vector4(0.78f, 0.66f, 0.42f, 1));
        _overlay.Flush(width, height);
    }

    /// <summary>
    /// The spinner at the right end: twelve spokes, the bright one going round once a second.
    /// </summary>
    void DrawSpinner(float cx, float cy, float outer, float inner, float s)
    {
        double turn = _clock.Elapsed.TotalSeconds % 1;
        const int spokes = 12;
        for (int i = 0; i < spokes; i++)
        {
            float a = i * MathF.Tau / spokes;
            float behind = (float)(((turn * spokes - i) % spokes + spokes) % spokes) / spokes;   // 0 at the head, towards 1 behind it
            var colour = new Vector4(0.9f, 0.84f, 0.7f, 0.15f + 0.85f * (1 - behind));
            _overlay.Line(cx + MathF.Sin(a) * inner, cy - MathF.Cos(a) * inner, cx + MathF.Sin(a) * outer,
                cy - MathF.Cos(a) * outer, Math.Max(2, 3 * s), colour);
        }
    }

    /// <summary>
    /// The original's window (<c>LoadingWindow</c>, <c>FUN_14091a280</c>, docs/game/ui-screens.md "Loading screen"): the layout as it is, the picture
    /// in <c>BackgroundImage</c>, the tip in <c>MessageText</c>, and in <c>LoadingText</c> the catalog's "Loading" upper-cased (its font,
    /// <c>Kenshi_UI_Messages</c>, has only A to Z and the dot) with one more dot each second, back to none after three, left-aligned where
    /// "LOADING.." would be centred in the panel.
    /// </summary>
    void DrawFaithful(int width, int height, bool background)
    {
        _painter!.Mode = UiMode.Faithful;
        var b = new UiBindings();
        if (!background) b.Hidden.Add("BackgroundImage");
        if (_group is not null)
            b.Properties["BackgroundImage"] = new Dictionary<string, string> { ["ImageResource"] = ImageSet, ["ImageGroup"] = _group,
                ["ImageName"] = "Background" };
        string word = Loc.Tr("Loading").ToUpperInvariant();
        int dots = (int)Math.Floor(_clock.Elapsed.TotalSeconds - _shownAt) % 4;
        b.Text["LoadingText"] = word + new string('.', dots);
        b.Text["MessageText"] = _tip;
        var controls = MyGuiPresentation.Arrange(_layout!, width, height, b, _resources!.Layers).ToList();
        int text = controls.FindIndex(c => c.Name == "LoadingText"), panel = controls.FindIndex(c => c.Name == "LoadingPanel");
        if (text >= 0 && panel >= 0)
        {
            var c = controls[text];
            float measured = _painter.MeasureText(c.Properties.GetValueOrDefault("FontName") ?? "", word + "..", height, 0, 0).Width;
            var box = controls[panel].Bounds;
            var properties = new Dictionary<string, string>(c.Properties) { ["TextAlign"] = "Left VCenter" };
            controls[text] = c with { Bounds = c.Bounds with { X = box.X + (box.Width - measured) * 0.5f }, Properties = properties };
        }
        if (LoadBar is { Visible: true } bar) controls.AddRange(bar.Controls(_resources, width, height));
        _painter.Draw(controls, width, height, new Vector2(-1, -1), null);
    }

    /// <summary>
    /// Frees the painter and the LOADING... face.
    /// </summary>
    public void Dispose()
    {
        _painter?.Dispose();
        _stamp?.Dispose();
    }

    #endregion

    #region Progress

    /// <summary>
    /// The running step's name.
    /// </summary>
    string _label = "";
    /// <summary>
    /// The running step's share of the bar (from, to; 0..1), the seconds it usually takes and when it started.
    /// </summary>
    double _from, _to, _expected = 1, _stepStart;
    /// <summary>
    /// The bar's position last shown; it never goes back.
    /// </summary>
    float _shown;

    /// <summary>
    /// A step starts: its name, the bar's share it covers (<paramref name="start"/> to <paramref name="end"/>, 0..1) and about how long it takes.
    /// </summary>
    public void Step(string name, double start, double end, double seconds)
    {
        _label = name;
        _from = Math.Max(start, _shown);
        _to = Math.Max(end, _from);
        _expected = Math.Max(seconds, 0.05);
        _stepStart = _clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// The bar's position now: the step's start, eased towards 95 % of its share by the time it has run against the time it usually takes.
    /// </summary>
    public float Progress
    {
        get
        {
            double t = (_clock.Elapsed.TotalSeconds - _stepStart) / _expected;
            double eased = _from + (_to - _from) * 0.95 * (1 - Math.Exp(-2.5 * t));
            return _shown = Math.Max(_shown, (float)Math.Clamp(eased, 0, 1));
        }
    }

    #endregion

    #region Exit

    /// <summary>
    /// How long the panels take to slide out (seconds).
    /// </summary>
    const double ExitSeconds = 0.7;
    /// <summary>
    /// When the exit began (seconds of <see cref="_clock"/>); null while loading.
    /// </summary>
    double? _exitStart;
    /// <summary>
    /// The screen was drawn this showing: a load that never showed it has nothing to dismiss.
    /// </summary>
    bool _drawn;
    /// <summary>
    /// The pictures were freed since the screen went (<see cref="ReleaseWhenGone"/>).
    /// </summary>
    bool _released;

    /// <summary>
    /// The exit's progress, 0..1; 0 before it starts.
    /// </summary>
    float ExitProgress => _exitStart is { } start ? (float)Math.Clamp((_clock.Elapsed.TotalSeconds - start) / ExitSeconds, 0, 1) : 0;
    /// <summary>
    /// The panels are sliding out.
    /// </summary>
    public bool Exiting => _exitStart is not null && ExitProgress < 1;

    /// <summary>
    /// Reveal the ready scene while the panels slide out (Faithful: hidden at once, as the original's window). A fast sky load that never showed
    /// panels has nothing to dismiss.
    /// </summary>
    public void Finish()
    {
        if (_drawn) _exitStart ??= _clock.Elapsed.TotalSeconds - (Faithful ? ExitSeconds : 0);
    }

    /// <summary>
    /// Once the screen has gone, frees its pictures (the picture and the UI atlas, about 17 MB at 1080p); the next showing loads them again. The host
    /// calls it on frames the screen is not drawn.
    /// </summary>
    public void ReleaseWhenGone()
    {
        if (_released || _exitStart is null || ExitProgress < 1) return;
        _painter?.ReleaseTextures();
        _released = true;
    }

    #endregion

    #region Splash

    /// <summary>
    /// The original's splash (<c>FUN_140916140</c>, stepped by <c>FUN_140913720</c>; docs/game/ui-screens.md "Splash"): on black, the
    /// <c>Kenshi_Splash_Logos</c> picture (its height × 100, so the screen's height) and then <c>Kenshi_Splash_LoFi</c> (× 0.7), each centred, at the
    /// image's height × the UI scale × that factor, capped at the screen's height.
    /// </summary>
    static readonly (string ImageSet, float Scale)[] SplashSlides = [("Kenshi_Splash_Logos", 100), ("Kenshi_Splash_LoFi", 0.7f)];
    /// <summary>
    /// The splash picture showing (-1 before the splash, past the last once it is over), and the frames it was drawn fully opaque.
    /// </summary>
    int _splashSlide = -1, _fullFrames;
    /// <summary>
    /// When the picture showing started (seconds of <see cref="_clock"/>).
    /// </summary>
    double _slideStart;
    /// <summary>
    /// A press asked to end the picture showing.
    /// </summary>
    bool _skipSlide;

    /// <summary>
    /// A splash slide's height: the image height × the GUI's horizontal scale (<c>DAT_1421337ec</c> = screen width / the 1920 px width of
    /// <c>Kenshi_UI.png</c>, set by <c>FUN_1406e3c30</c>) × the slide's factor, capped at the screen height.
    /// </summary>
    internal static int SplashSlideHeight(float imageHeight, int screenWidth, int screenHeight,
        float factor) => (int)Math.Min(imageHeight * (screenWidth / 1920f) * factor, screenHeight);

    /// <summary>
    /// Plays the splash from its first picture (the Faithful start, which opens on the title); the loading screen shows once it is over.
    /// </summary>
    public void StartSplash()
    {
        if (_painter is null || _resources is null) return;
        (_splashSlide, _fullFrames, _skipSlide, _slideStart) = (0, 0, false, _clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// The splash is still showing a picture.
    /// </summary>
    public bool SplashPlaying => _splashSlide >= 0 && _splashSlide < SplashSlides.Length;

    /// <summary>
    /// A key or button was pressed: the picture showing ends once it is fully shown.
    /// </summary>
    public void SkipSplash()
    {
        if (SplashPlaying) _skipSlide = true;
    }

    /// <summary>
    /// One frame of the splash: the picture fades in at 3 a second and ends after 4 seconds, or on a press, once it has been drawn fully opaque on
    /// more than three frames; then the next one starts at once, with no fade out.
    /// </summary>
    void DrawSplash(int width, int height)
    {
        double elapsed = _clock.Elapsed.TotalSeconds - _slideStart;
        float alpha = (float)Math.Min(1, elapsed * 3);
        if (alpha == 1) _fullFrames++;
        bool ended = (_skipSlide || elapsed > 4) && _fullFrames > 3;
        _overlay.Rect(0, 0, width, height, new Vector4(0, 0, 0, 1));
        _overlay.Flush(width, height);
        if (!ended && _resources!.Resources.TryGetValue(SplashSlides[_splashSlide].ImageSet, out var set)
            && set.Element("Group")?.Attribute("size")?.Value.Split(' ') is [var w, var h]
            && float.TryParse(w, CultureInfo.InvariantCulture, out float imageWidth)
                && float.TryParse(h, CultureInfo.InvariantCulture, out float imageHeight) && imageHeight > 0)
        {
            int shownHeight = SplashSlideHeight(imageHeight, width, height, SplashSlides[_splashSlide].Scale);
            int shownWidth = (int)(imageWidth / imageHeight * shownHeight);
            var bounds = new UiRect((int)(width * 0.5f - shownWidth * 0.5f), (int)(height * 0.5f - shownHeight * 0.5f), shownWidth, shownHeight);
            var properties = new Dictionary<string, string>
            {
                ["ImageResource"] = SplashSlides[_splashSlide].ImageSet,
                ["ImageGroup"] = "Main",
                ["ImageName"] = "Main",
                ["Alpha"] = alpha.ToString(CultureInfo.InvariantCulture),
            };
            _painter!.Mode = UiMode.Faithful;
            _painter.Draw([new UiControl("Splash", "ImageBox", bounds, "", "", "", true, properties, Vector4.Zero)], width,
                height, new Vector2(-1, -1), null);
        }
        if (ended) (_splashSlide, _fullFrames, _skipSlide, _slideStart) = (_splashSlide + 1, 0, false, _clock.Elapsed.TotalSeconds);
    }

    #endregion
}