using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering;
using Meitou.Rendering.Ui;

namespace Meitou.Game;

/// <summary>
/// The loading screen drawn while the game starts and between scenes (docs/engine.md "Loading", docs/game/ui-screens.md "Loading screen"). <see cref="Faithful"/>:
/// the original's <c>Kenshi_LoadingScreen</c> as it shows it (a picture of the <c>Kenshi_LoadingScreen</c> image set, the layout's own panel with "Loading" and its
/// dots and a tip, hidden at once when done). Meitou: one of every <c>loading_screen*.dds</c> picture, the layout's panel made small at the bottom right with the tip,
/// and Meitou's panel at the bottom left: LOADING..., a spinner and an approximate progress bar under it (engine choice); both slide out when done. The bar eases
/// towards the end of the running step by the time the step usually takes, so it moves while a step runs; it never goes back.
/// </summary>
sealed class LoadingScreen : IDisposable
{
    readonly DebugOverlay overlay;
    DebugOverlay? stamp;   // the original's LOADING... face (Kenshi_UI_Messages: MailartRubberstamp-Regular.ttf)
    readonly UiPainter? painter;
    readonly MyGuiResources? resources;
    readonly MyGuiLayout? layout;
    readonly Random random = new();
    readonly string[] tips, pictures, groups;
    string? picture, group;
    string tip = "";
    double shownAt;
    readonly Stopwatch clock = Stopwatch.StartNew();
    string label = "";
    double from, to, expected = 1, stepStart;
    float shown;
    const double ExitSeconds = 0.7;
    double? exitStart;
    bool drawn;

    float ExitProgress => exitStart is { } start ? (float)Math.Clamp((clock.Elapsed.TotalSeconds - start) / ExitSeconds, 0, 1) : 0;
    public bool Exiting => exitStart is not null && ExitProgress < 1;

    /// <summary>The original's background-loading bar, drawn over the Faithful screen (the host steps it).</summary>
    public ResourceLoadBar? LoadBar { get; set; }

    /// <summary>The original's screen (the Faithful UI mode); set by the host before drawing.</summary>
    public bool Faithful { get; set; }

    /// <summary>Reveal the ready scene while the panels slide out (Faithful: hidden at once, as the original's window). A fast sky load that never showed panels has nothing to dismiss.</summary>
    public void Finish()
    {
        if (drawn) exitStart ??= clock.Elapsed.TotalSeconds - (Faithful ? ExitSeconds : 0);
    }

    LoadingScreen(DebugOverlay overlay, UiPainter? painter, MyGuiResources? resources, MyGuiLayout? layout, string[] tips, string[] pictures, string[] groups)
    {
        (this.overlay, this.painter, this.resources, this.layout, this.tips, this.pictures, this.groups) = (overlay, painter, resources, layout, tips, pictures, groups);
        Pick();
    }

    /// <summary>
    /// What a showing of the screen draws, picked anew each time as the original's window does when shown (<c>FUN_140916ca0</c>): a tip, a picture (Meitou) and a
    /// group of the image set (Faithful: one of its groups by position, shown by name, so <c>MyGuiImages</c> takes the first group of that name, as MyGUI does).
    /// </summary>
    void Pick()
    {
        tip = tips.Length > 0 ? Meitou.Localisation.Loc.Tr(tips[random.Next(tips.Length)]) : "";
        picture = pictures.Length > 0 ? pictures[random.Next(pictures.Length)] : null;
        group = groups.Length > 0 ? groups[random.Next(groups.Length)] : null;
        shownAt = clock.Elapsed.TotalSeconds;
    }

    /// <summary>The screen with the original's layout, picture and tips when they are found (else the spinner and bar on black).</summary>
    public static LoadingScreen Create(DebugOverlay overlay, Func<string, string?> resolve, string? dataDirectory, bool faithful)
    {
        // tips.txt through the resource lookup: the active language's own copy (locale/<code>/tips.txt) replaces data/tips.txt; a tip that is also a
        // message of the catalog (main.pot lists the tips) is translated. The original keeps every non-empty line (FUN_14091a280; a written "\n" is a line break,
        // which the painter honours).
        string[] tips = [];
        string? tipsFile = resolve("tips.txt") ?? (dataDirectory is null ? null : Path.Combine(dataDirectory, "tips.txt"));
        if (tipsFile is not null && File.Exists(tipsFile)) tips = ReadTips(tipsFile);
        try
        {
            var resources = MyGuiResources.Load("kenshi_init.xml", resolve, "kenshi_fonts.xml");
            var layout = resolve("Kenshi_LoadingScreen.layout") is { } file ? MyGuiTemplates.Expand(MyGuiReader.ReadLayout(file), resources) : null;
            // Meitou: the pictures the original ships (loading_screen0..10, some numbers missing). Faithful: the image set's groups in file order, repeats kept.
            var pictures = Enumerable.Range(0, 16).Select(i => $"loading_screen{i}.dds").Where(n => resolve(n) is not null).ToArray();
            string[] groups = resources.Resources.TryGetValue(ImageSet, out var set) ? [.. set.Elements("Group").Select(g => (string?)g.Attribute("name")).OfType<string>()] : [];
            var painter = new UiPainter(overlay, resources, resolve);
            var stamp = resolve("MailartRubberstamp-Regular.ttf") is { } stampFile ? DebugOverlay.FromFont(overlay.Gpu, stampFile, 64) : null;
            return new(overlay, painter, resources, layout, tips, pictures, groups) { stamp = stamp, Faithful = faithful };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"loading   the original loading screen did not load ({e.Message}); spinner only");
            return new(overlay, null, null, null, tips, [], []) { Faithful = faithful };
        }
    }

    const string ImageSet = "Kenshi_LoadingScreen";

    /// <summary>
    /// The non-empty lines of a tips file: by its byte order mark when it has one, else UTF-8 when it is valid UTF-8, else Windows-1252 (the French, German and
    /// Spanish files are; engine choice: how the original's MyGUI strings show those bytes is Unknown).
    /// </summary>
    static string[] ReadTips(string file)
    {
        byte[] bytes = File.ReadAllBytes(file);
        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (System.Text.DecoderFallbackException) { text = Windows1252.Value.GetString(bytes); }
        return [.. text.Split(['\r', '\n']).Select(l => l.Trim()).Where(l => l.Length > 0)];
    }

    /// <summary>Windows-1252, its code page provider registered once.</summary>
    static readonly Lazy<System.Text.Encoding> Windows1252 = new(() =>
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(1252);
    });

    /// <summary>A step starts: its name, the bar's share it covers (<paramref name="start"/> to <paramref name="end"/>, 0..1) and about how long it takes.</summary>
    public void Step(string name, double start, double end, double seconds)
    {
        label = name;
        from = Math.Max(start, shown);
        to = Math.Max(end, from);
        expected = Math.Max(seconds, 0.05);
        stepStart = clock.Elapsed.TotalSeconds;
    }

    /// <summary>The bar's position now: the step's start, eased towards 95 % of its share by the time it has run against the time it usually takes.</summary>
    public float Progress
    {
        get
        {
            double t = (clock.Elapsed.TotalSeconds - stepStart) / expected;
            double eased = from + (to - from) * 0.95 * (1 - Math.Exp(-2.5 * t));
            return shown = Math.Max(shown, (float)Math.Clamp(eased, 0, 1));
        }
    }

    /// <summary>
    /// The original's splash (<c>FUN_140916140</c>, stepped by <c>FUN_140913720</c>; docs/game/ui-screens.md "Splash"): on black, the <c>Kenshi_Splash_Logos</c> picture
    /// (its height × 100, so the screen's height) and then <c>Kenshi_Splash_LoFi</c> (× 0.7), each centred, at the image's height × the UI scale × that factor, capped
    /// at the screen's height.
    /// </summary>
    static readonly (string ImageSet, float Scale)[] SplashSlides = [("Kenshi_Splash_Logos", 100), ("Kenshi_Splash_LoFi", 0.7f)];
    int splashSlide = -1, fullFrames;
    double slideStart;
    bool skipSlide;

    /// <summary>Plays the splash from its first picture (the Faithful start, which opens on the title); the loading screen shows once it is over.</summary>
    public void StartSplash()
    {
        if (painter is null || resources is null) return;
        (splashSlide, fullFrames, skipSlide, slideStart) = (0, 0, false, clock.Elapsed.TotalSeconds);
    }

    /// <summary>The splash is still showing a picture.</summary>
    public bool SplashPlaying => splashSlide >= 0 && splashSlide < SplashSlides.Length;

    /// <summary>A key or button was pressed: the picture showing ends once it is fully shown.</summary>
    public void SkipSplash() { if (SplashPlaying) skipSlide = true; }

    /// <summary>
    /// One frame of the splash: the picture fades in at 3 a second and ends after 4 seconds, or on a press, once it has been drawn fully opaque on more than three
    /// frames; then the next one starts at once, with no fade out.
    /// </summary>
    void DrawSplash(int width, int height)
    {
        double elapsed = clock.Elapsed.TotalSeconds - slideStart;
        float alpha = (float)Math.Min(1, elapsed * 3);
        if (alpha == 1) fullFrames++;
        bool ended = (skipSlide || elapsed > 4) && fullFrames > 3;
        overlay.Rect(0, 0, width, height, new Vector4(0, 0, 0, 1));
        overlay.Flush(width, height);
        if (!ended && resources!.Resources.TryGetValue(SplashSlides[splashSlide].ImageSet, out var set)
            && set.Element("Group")?.Attribute("size")?.Value.Split(' ') is [var w, var h]
            && float.TryParse(w, CultureInfo.InvariantCulture, out float imageWidth) && float.TryParse(h, CultureInfo.InvariantCulture, out float imageHeight) && imageHeight > 0)
        {
            // The scale is Unknown (DAT_1421337ec, beside the UI scale DAT_1421337f0): taken as the UI scale, height / 1080 (engine choice).
            int shownHeight = (int)Math.Min(imageHeight * (height / 1080f) * SplashSlides[splashSlide].Scale, height);
            int shownWidth = (int)(imageWidth / imageHeight * shownHeight);
            var bounds = new UiRect((int)(width * 0.5f - shownWidth * 0.5f), (int)(height * 0.5f - shownHeight * 0.5f), shownWidth, shownHeight);
            var properties = new Dictionary<string, string>
            {
                ["ImageResource"] = SplashSlides[splashSlide].ImageSet, ["ImageGroup"] = "Main", ["ImageName"] = "Main", ["Alpha"] = alpha.ToString(CultureInfo.InvariantCulture),
            };
            painter!.Mode = UiMode.Faithful;
            painter.Draw([new UiControl("Splash", "ImageBox", bounds, "", "", "", true, properties, Vector4.Zero)], width, height, new Vector2(-1, -1), null);
        }
        if (ended) (splashSlide, fullFrames, skipSlide, slideStart) = (splashSlide + 1, 0, false, clock.Elapsed.TotalSeconds);
    }

    /// <summary>Starts the bar again (a new load between scenes), with a new tip and picture.</summary>
    public void Restart() { shown = 0; from = to = 0; exitStart = null; drawn = false; released = false; Pick(); }

    bool released;

    /// <summary>Once the screen has gone, frees its pictures (the picture and the UI atlas, about 17 MB at 1080p); the next showing loads them again.
    /// The host calls it on frames the screen is not drawn.</summary>
    public void ReleaseWhenGone()
    {
        if (released || exitStart is null || ExitProgress < 1) return;
        painter?.ReleaseTextures();
        released = true;
    }

    public void Draw(int width, int height, bool background = true)
    {
        float t = ExitProgress;
        if (t >= 1) return;
        drawn = true;
        if (SplashPlaying && exitStart is null) { DrawSplash(width, height); return; }
        if (Faithful && painter is not null && layout is not null && resources is not null)
        {
            // Hidden at once when done, also when the mode was switched to Faithful during the Meitou slide-out.
            if (exitStart is null) DrawFaithful(width, height, background);
            return;
        }
        if (painter is not null) painter.Mode = UiMode.Meitou;
        float departure = t * t * (3 - 2 * t), opacity = 1 - departure;
        if (background)
        {
            overlay.Rect(0, 0, width, height, new Vector4(0.02f, 0.02f, 0.018f, opacity));
            overlay.Flush(width, height);
        }
        float progress = exitStart is null ? Progress : 1;
        // Two panels along the bottom (engine choice): the loading panel on the left (LOADING..., the spinner, the bar, the step), the layout's own panel on the
        // right, made small and holding only the tip. Sizes at 1080 rows, scaled with the height.
        float s = height / 1080f, margin = 40 * s, panelHeight = 120 * s, top = height - margin - panelHeight;
        float half = Math.Max(width / 2f - 1.5f * margin, 1);
        var box = new UiRect(margin, top, Math.Min(480 * s, half), panelHeight);
        var tipBox = new UiRect(width - margin - Math.Min(480 * s, half), top, Math.Min(480 * s, half), panelHeight);
        float slide = (box.Width + margin + 8 * s) * departure;
        box = box with { X = box.X - slide };
        tipBox = tipBox with { X = tipBox.X + slide };
        if (painter is not null && layout is not null && resources is not null)
        {
            var b = new UiBindings();
            if (!background) b.Hidden.Add("BackgroundImage");
            var image = new Dictionary<string, string> { ["Alpha"] = opacity.ToString(CultureInfo.InvariantCulture) };
            if (picture is not null) image["ImageTexture"] = picture;
            b.Properties["BackgroundImage"] = image;
            b.Positions["LoadingPanel"] = tipBox;
            b.Positions["MessageText"] = new UiRect(tipBox.X + 8 * s, tipBox.Y + 8 * s, tipBox.Width - 16 * s, tipBox.Height - 16 * s);
            b.Hidden.Add("LoadingText");
            b.Text["MessageText"] = tip;
            b.Properties["MessageText"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Center",
                ["FontName"] = "Kenshi_StandardFont_Medium", ["FontPixels"] = Math.Max(8, (int)MathF.Round(16 * s)).ToString(CultureInfo.InvariantCulture) };
            var controls = MyGuiPresentation.Arrange(layout, width, height, b, resources.Layers).ToList();
            // The loading panel in the tip panel's frame (Kenshi_FloatingPanelSkin, the layout's LoadingPanel skin).
            controls.Add(new UiControl("LoadingBox", "Widget", box, "", "", "Kenshi_FloatingPanelSkin", true, new Dictionary<string, string>(), Vector4.Zero));
            painter.Draw(controls, width, height, new Vector2(-1, -1), null);
        }
        else overlay.Rect(box.X, box.Y, box.X + box.Width, box.Y + box.Height, new Vector4(0.08f, 0.07f, 0.06f, 0.85f));

        // The bar along the panel's bottom; above it LOADING... and the running step on the left, the spinner on the right.
        float pad = 16 * s, barHeight = 10 * s;
        float x0 = box.X + pad, x1 = box.X + box.Width - pad, barTop = box.Y + box.Height - pad - barHeight;
        float rowTop = box.Y + pad * 0.75f, rowHeight = barTop - pad * 0.5f - rowTop, cy = rowTop + rowHeight / 2;
        float textEnd = x0;
        if (stamp is not null)
        {
            float scale = rowHeight * 0.52f / stamp.LineHeight, textTop = cy - stamp.LineHeight * scale / 2;
            stamp.Target = overlay.Target;
            stamp.TextScaled("LOADING...", x0 + 2, textTop + 2, new Vector4(0, 0, 0, 0.8f), scale);
            textEnd = x0 + stamp.TextScaled("LOADING...", x0, textTop, new Vector4(0.87f, 0.85f, 0.81f, 1), scale);
            stamp.Flush(width, height);
        }
        else textEnd = x0 + overlay.Text("LOADING...", x0, cy - overlay.LineHeight / 2, DebugOverlay.TextColour);

        // The spinner at the right end: twelve spokes, the bright one going round once a second.
        float outer = Math.Min(rowHeight * 0.45f, 22 * s), inner = outer * 0.45f, cx = x1 - outer;
        double turn = clock.Elapsed.TotalSeconds % 1;
        const int Spokes = 12;
        for (int i = 0; i < Spokes; i++)
        {
            float a = i * MathF.Tau / Spokes;
            float behind = (float)(((turn * Spokes - i) % Spokes + Spokes) % Spokes) / Spokes;   // 0 at the head, towards 1 behind it
            var colour = new Vector4(0.9f, 0.84f, 0.7f, 0.15f + 0.85f * (1 - behind));
            overlay.Line(cx + MathF.Sin(a) * inner, cy - MathF.Cos(a) * inner, cx + MathF.Sin(a) * outer, cy - MathF.Cos(a) * outer, Math.Max(2, 3 * s), colour);
        }
        if (label.Length > 0 && textEnd + 12 * s + label.Length * overlay.CharWidth <= cx - outer - 12 * s)
            overlay.Text(label, textEnd + 12 * s, cy - overlay.LineHeight / 2, new Vector4(0.62f, 0.6f, 0.52f, 1));
        overlay.Rect(x0 - 2, barTop - 2, x1 + 2, barTop + barHeight + 2, new Vector4(0.35f, 0.31f, 0.24f, 1));
        overlay.Rect(x0, barTop, x1, barTop + barHeight, new Vector4(0.08f, 0.07f, 0.06f, 1));
        overlay.Rect(x0, barTop, x0 + (x1 - x0) * progress, barTop + barHeight, new Vector4(0.78f, 0.66f, 0.42f, 1));
        overlay.Flush(width, height);
    }

    /// <summary>
    /// The original's window (<c>LoadingWindow</c>, <c>FUN_14091a280</c>, docs/game/ui-screens.md "Loading screen"): the layout as it is, the picture in
    /// <c>BackgroundImage</c>, the tip in <c>MessageText</c>, and in <c>LoadingText</c> the catalog's "Loading" upper-cased (its font, <c>Kenshi_UI_Messages</c>, has only
    /// A to Z and the dot) with one more dot each second, back to none after three, left-aligned where "LOADING.." would be centred in the panel.
    /// </summary>
    void DrawFaithful(int width, int height, bool background)
    {
        painter!.Mode = UiMode.Faithful;
        var b = new UiBindings();
        if (!background) b.Hidden.Add("BackgroundImage");
        if (group is not null)
            b.Properties["BackgroundImage"] = new Dictionary<string, string> { ["ImageResource"] = ImageSet, ["ImageGroup"] = group, ["ImageName"] = "Background" };
        string word = Meitou.Localisation.Loc.Tr("Loading").ToUpperInvariant();
        int dots = (int)Math.Floor(clock.Elapsed.TotalSeconds - shownAt) % 4;
        b.Text["LoadingText"] = word + new string('.', dots);
        b.Text["MessageText"] = tip;
        var controls = MyGuiPresentation.Arrange(layout!, width, height, b, resources!.Layers).ToList();
        int text = controls.FindIndex(c => c.Name == "LoadingText"), panel = controls.FindIndex(c => c.Name == "LoadingPanel");
        if (text >= 0 && panel >= 0)
        {
            var c = controls[text];
            float measured = painter.MeasureText(c.Properties.GetValueOrDefault("FontName") ?? "", word + "..", height, 0, 0).Width;
            var box = controls[panel].Bounds;
            var properties = new Dictionary<string, string>(c.Properties) { ["TextAlign"] = "Left VCenter" };
            controls[text] = c with { Bounds = c.Bounds with { X = box.X + (box.Width - measured) * 0.5f }, Properties = properties };
        }
        if (LoadBar is { Visible: true } bar) controls.AddRange(bar.Controls(resources, width, height));
        painter.Draw(controls, width, height, new Vector2(-1, -1), null);
    }

    public void Dispose() { painter?.Dispose(); stamp?.Dispose(); }
}
