using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Textures;
using Meitou.Data.Ui;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Ui;

/// <summary>Native alpha-composited MyGUI atlas slices and styled native control frames.</summary>
public sealed partial class UiPainter : IDisposable
{
    /// <summary>The game's standard face (Exo2 SemiBold): the fallback for text whose font resource is not found, and the debug overlay's when no monospace system font is.</summary>
    internal const string StandardFontFile = "Exo2-SemiBold.ttf";

    readonly DebugOverlay overlay;
    readonly MyGuiResources resources;
    readonly Func<string, string?> resolve;
    DebugOverlay? standardFont, paintedFont, titleFont;
    /// <summary>The one-line edit boxes the last <see cref="Draw"/> laid out: where each one's text starts and how it was measured, for <see cref="EditCaretAt"/>.</summary>
    readonly List<(UiRect Box, DebugOverlay Font, float Scale, float X, string Text)> editBoxes = [];
    readonly Dictionary<UiFontKey, DebugOverlay> uiFonts = [];
    readonly Dictionary<string, MyGuiTrueTypeFont?> fontSpecs = new(StringComparer.Ordinal);
    readonly Dictionary<(string Path, float Size, float Resolution), float> fontSizes = [];
    bool freeTypeWarned, standardSourceKnown;
    (string Path, MyGuiTrueTypeFont? Spec)? standardSource;

    /// <summary>
    /// How text is drawn (docs/formats/mygui.md "Fonts"). <see cref="UiMode.Faithful"/>: MyGUI's own rendering of the font resources (FreeType native
    /// hinting, the resource's <c>Codes</c> and substitute glyph, its line metrics, whole pixels). <see cref="UiMode.Meitou"/> (the default):
    /// any glyph the face has, light hinting or oversampling, Meitou's line box. Set by the host; fonts of the other mode are made when it changes.
    /// </summary>
    public UiMode Mode { get; set; } = UiMode.Meitou;

    /// <summary>A font made for one resource, file, size, dpi and look.</summary>
    readonly record struct UiFontKey(string Resource, string Path, long Size64, uint Dpi, bool Faithful, bool Snapped);

    /// <summary>The most glyph-cache fonts kept (each holds an atlas of 0.25 to 4 MiB): beyond it a size already made, the nearest, is used instead.</summary>
    const int MaxUiFonts = 48;

    MyGuiTrueTypeFont? FontSpec(string name)
    {
        if (!fontSpecs.TryGetValue(name, out var spec))
            fontSpecs[name] = spec = resources.Resources.TryGetValue(name, out var resource) ? MyGuiTrueTypeFont.Read(resource) : null;
        return spec;
    }

    /// <summary>The file and resource that standard-face text of an explicit pixel size (<c>FontPixels</c>) is drawn from: the standard font resource (a locale's own font), else Exo2.</summary>
    (string Path, MyGuiTrueTypeFont? Spec)? StandardSource()
    {
        if (standardSourceKnown) return standardSource;
        standardSourceKnown = true;
        var spec = FontSpec("Kenshi_StandardFont_Medium");
        if (spec is not null && FontPath(spec.Source) is { } path) standardSource = (path, spec);
        else if (FontPath(StandardFontFile) is { } exo) standardSource = (exo, null);
        return standardSource;
    }

    DebugOverlay UiFontFor(string resource, MyGuiTrueTypeFont? spec, string path, long size64, uint dpi, bool faithful, bool snapped)
    {
        var key = new UiFontKey(resource, path, size64, dpi, faithful, snapped);
        if (uiFonts.TryGetValue(key, out var known)) return known;
        if (uiFonts.Count >= MaxUiFonts)
        {
            // Continuous resizing asks for ever new sizes: reuse the nearest one made (fonts are never freed while the UI runs).
            DebugOverlay? nearest = null;
            long distance = long.MaxValue;
            foreach (var (other, font) in uiFonts)
                if (other.Resource == resource && other.Path == path && other.Dpi == dpi && other.Faithful == faithful && other.Snapped == snapped
                    && Math.Abs(other.Size64 - size64) is var gap && gap < distance) (nearest, distance) = (font, gap);
            if (nearest is not null) return nearest;
        }
        return uiFonts[key] = CreateUiOverlay(new UiFontOptions(path, size64, dpi, faithful, snapped, spec));
    }

    /// <summary>A glyph-cache overlay; if FreeType cannot be used on this machine, a Latin-only stb_truetype one (once reported).</summary>
    DebugOverlay CreateUiOverlay(UiFontOptions options)
    {
        try { return DebugOverlay.FromUiFont(overlay.Gpu, new UiFont(overlay.Gpu, options)); }
        catch (Exception e) when (e is DllNotFoundException or FileLoadException or BadImageFormatException or EntryPointNotFoundException
            or InvalidOperationException or InvalidDataException)
        {
            if (!freeTypeWarned)
            {
                freeTypeWarned = true;
                Console.Error.WriteLine($"ui        FreeType is unavailable ({e.Message}); text uses the Latin-only fallback.");
            }
            return DebugOverlay.FromFont(overlay.Gpu, options.Path, options.Size64 / 64f * options.Dpi / 72f);
        }
    }

    /// <summary>
    /// The points a TrueType font resource is drawn at for the UI scale <paramref name="scale"/> (docs/formats/mygui.md): its size scaled and cut to whole points,
    /// then resized as the original does on load (<see cref="KenshiFontSize"/>). At 1080 lines the resource's own size, resized.
    /// </summary>
    float ScaledFontSize(string path, MyGuiTrueTypeFont spec, float scale) =>
        EffectiveFontSize(path, MathF.Floor((spec.Size > 0 ? spec.Size : 20) * scale + 1e-3f), spec.Resolution > 0 ? spec.Resolution : 50);

    /// <summary>
    /// The em pixels of the original's standard text (<c>Kenshi_StandardFont_Medium</c>) at <paramref name="viewportHeight"/> lines, as <see cref="ScaledFontSize"/> sizes it
    /// (13.9 at 1080, 17.4 at 1440); the linear 13.9 per 1080 lines without the resource.
    /// </summary>
    public float StandardTextPixels(int viewportHeight)
    {
        if (FontSpec("Kenshi_StandardFont_Medium") is { } spec && FontPath(spec.Source) is { } path)
            return ScaledFontSize(path, spec, viewportHeight / 1080f) * (spec.Resolution > 0 ? spec.Resolution : 50) / 72;
        return 20 * 50 / 72f * viewportHeight / 1080f;
    }

    /// <summary>The point size the original draws font resource <paramref name="path"/> at (<see cref="KenshiFontSize"/>).</summary>
    float EffectiveFontSize(string path, float size, float resolution)
    {
        if (!fontSizes.TryGetValue((path, size, resolution), out float effective))
            fontSizes[(path, size, resolution)] = effective = KenshiFontSize.Effective(File.ReadAllBytes(path), size, resolution);
        return effective;
    }
    readonly Dictionary<string, string?> fontPaths = new(StringComparer.Ordinal);

    string? FontPath(string name)
    {
        if (!fontPaths.TryGetValue(name, out var path)) fontPaths[name] = path = resolve(name);
        return path;
    }
    /// <summary>The standard and painted faces baked at <see cref="LargePixels"/>: lettering scaled well past the 16 px atlases is drawn from these, so it stays sharp.</summary>
    DebugOverlay? standardLarge, paintedLarge;
    const float LargePixels = 48;
    readonly Dictionary<string, (GpuTexture Texture, SampledTexture Sampled)> textures = new(StringComparer.Ordinal);
    /// <summary>Textures the host draws at run time (the squad portraits), shown by an ImageBox's <c>ImageTexture</c> under their name; not owned.</summary>
    readonly Dictionary<string, (GpuTexture Texture, SampledTexture Sampled)> external = new(StringComparer.Ordinal);
    /// <summary>Which texels of each uploaded texture are fully opaque, by name (<see cref="OpaqueCoverage"/>); textures given on the GPU have none and never count.</summary>
    readonly Dictionary<string, OpacityMask> masks = new(StringComparer.Ordinal);
    readonly List<UiRect> opaqueRects = new();
    readonly List<UiRect> previousOpaqueRects = new();
    double? opaqueArea;
    int coverageWidth, coverageHeight;
    DebugOverlay.OverlayPass? overlayPass;
    /// <summary>A caption's parsed colour tags, lines and their widths, by everything they are made from; entries not used for <see cref="TextLayoutLife"/> frames are dropped.</summary>
    readonly Dictionary<TextLayoutKey, TextLayout> textLayouts = new();
    /// <summary>The <c>FontName</c> a skin template sets, by skin (null: none, or no such template).</summary>
    readonly Dictionary<string, string?> templateFonts = new(StringComparer.Ordinal);
    long textFrame;
    const int TextLayoutLife = 120;
    readonly record struct TextLayoutKey(DebugOverlay Font, string Caption, float Width, float Scale, int Mode);
    sealed class TextLayout(string text, IReadOnlyList<TextColourRun> runs, IReadOnlyList<string> lines, float[] widths)
    {
        public readonly string Text = text;
        public readonly IReadOnlyList<TextColourRun> Runs = runs;
        public readonly IReadOnlyList<string> Lines = lines;
        public readonly float[] Widths = widths;
        public long Used;
    }

    /// <summary>
    /// The share of the viewport (0..1) under fully opaque pieces of the last <see cref="Draw"/>: solid backgrounds and atlas slices whose
    /// texels are all alpha 255, drawn with alpha 1; text and translucent pieces do not count. Overlaps count once. The measure for how
    /// much scene shading a UI depth stamp could skip.
    /// </summary>
    public float OpaqueCoverage
    {
        get
        {
            // Measured when asked (the stats line), not every frame: the union is costly and the pieces change whenever a panel or a name tag moves.
            opaqueArea ??= UnionArea([.. previousOpaqueRects]);
            return coverageWidth > 0 && coverageHeight > 0 ? (float)(opaqueArea.Value / ((double)coverageWidth * coverageHeight)) : 0;
        }
    }

    /// <summary>Makes <paramref name="texture"/> the image an ImageBox shows for <c>ImageTexture</c> <paramref name="name"/> (the caller keeps and frees it).</summary>
    public void SetTexture(string name, GpuTexture texture, SampledTexture sampled) => external[name] = (texture, sampled);

    /// <summary>
    /// Uploads <paramref name="image"/> (made by the host) as <c>ImageTexture</c> <paramref name="name"/>, between frames; the painter owns the copy. <paramref name="srgb"/>
    /// false stores the bytes as they are, as the game's atlas textures are (art baked from them then draws in their colours).
    /// </summary>
    public void SetImage(string name, RgbaImage image, bool srgb = true) => Upload(name, image, srgb);

    /// <summary>Frees texture <paramref name="name"/> (after the frames in flight); a later draw that names it loads it again.</summary>
    public void Forget(string name)
    {
        if (textures.Remove(name, out var t)) t.Texture.Dispose();
        masks.Remove(name);
    }

    /// <summary>Frees every texture loaded from files or images (not the fonts, nor those given with <see cref="SetTexture"/>); later draws load them again.</summary>
    public void ReleaseTextures()
    {
        foreach (var t in textures.Values) t.Texture.Dispose();
        textures.Clear();
        masks.Clear();
    }

    /// <summary>Whether <c>ImageTexture</c> <paramref name="name"/> is on the GPU.</summary>
    public bool HasTexture(string name) => textures.ContainsKey(name) || external.ContainsKey(name);

    public UiPainter(DebugOverlay overlay, MyGuiResources resources, Func<string, string?> resolve)
    {
        this.overlay = overlay;
        this.resources = resources;
        this.resolve = resolve;
        // Upload before rendering starts. Missing atlas files remain an explicit diagnostic.
        try
        {
            // The fallback faces (a control whose font is not a TrueType resource): glyph-cache fonts in Meitou's look, 2x oversampled so that scaled text stays smooth.
            DebugOverlay Fallback(string path, int pixels) => CreateUiOverlay(new UiFontOptions(path, pixels * 64L, 72, false, false, null));
            if (resolve(StandardFontFile) is { } standard)
            {
                standardFont = Fallback(standard, 16);
                standardLarge = Fallback(standard, (int)LargePixels);
            }
            if (resolve("Sentencia_1.ttf") is { } painted)
            {
                paintedFont = Fallback(painted, 16);
                paintedLarge = Fallback(painted, (int)LargePixels);
                titleFont = DebugOverlay.FromFont(overlay.Gpu, painted, 144);   // a stamp of Latin capitals, baked large for the title
            }
            foreach (string name in resources.Skins.Values.Select(s => s.Texture).Where(s => s.Length > 0).Distinct())
            {
                LoadTexture(name);
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>The width in pixels of <paramref name="text"/> in the standard face at <paramref name="emPixels"/> (to fit a panel to its text before it is drawn).</summary>
    public float MeasureStandard(string text, float emPixels) => (standardFont ?? overlay).MeasureText(text, emPixels / 16f);

    public void Draw(IReadOnlyList<UiControl> controls, int width, int height, Vector2 mouse, string? active, float minimumTextPixels = 0,
        DebugOverlay.OverlayPass? pass = null)
    {
        for (int uploads = 0; uploads < IconUploadsPerFrame && decodedIcons.TryDequeue(out var icon); uploads++) Upload(icon.Name, icon.Image);
        bool owns = pass is null;
        pass ??= (overlayPass ??= new DebugOverlay.OverlayPass(overlay.Gpu, width, height)).Reset(width, height);
        using var owned = owns ? pass : null;
        editBoxes.Clear();
        foreach (var c in controls)
        {
            var b = c.Bounds;
            string widgetState = c.Properties.GetValueOrDefault("State") ?? (!c.Enabled ? "disabled" : active == c.Action && active is not null ? "pushed" : b.Contains(mouse.X, mouse.Y) && c.Action.Length > 0 ? "highlighted" : "normal");
            var clip = (c.Clip ?? new(0, 0, width, height)).Intersect(new(0, 0, width, height));
            if (b.Intersect(clip).Width <= 0 || b.Intersect(clip).Height <= 0) continue;
            if (b.Width <= 0 || b.Height <= 0) continue;
            if (c.Background.W > 0)
            {
                var colour = c.Type == "Button" && c.Action == active ? c.ActiveColour ?? new Vector4(0.25f, 0.5f, 0.4f, c.Background.W)
                    : c.Type == "Button" && b.Contains(mouse.X, mouse.Y) ? c.HoverColour ?? c.Background : c.Background;
                if (!c.Enabled) colour = c.DisabledColour ?? colour * new Vector4(0.6f, 0.6f, 0.6f, 1);
                colour.W *= Alpha(c.Properties);
                overlay.Rect(b.X, b.Y, b.X + b.Width, b.Y + b.Height, colour);
                if (colour.W >= 1) opaqueRects.Add(b.Intersect(clip));
            }
            overlay.Flush(width, height, clip, pass);
            if (c.Type == "ImageBox" && MyGuiImages.Resolve(resources, c.Properties) is { } image && (!image.Texture.StartsWith(IconPrefix, StringComparison.Ordinal) || textures.ContainsKey(image.Texture)) && (!image.Texture.StartsWith(RenderedIconPrefix, StringComparison.Ordinal) || RenderedIconReady(image.Texture)))
            {
                var loaded = LoadTexture(image.Texture);
                var source = image.Source.Width > 0 && image.Source.Height > 0 ? image.Source : new UiRect(0, 0, loaded.Texture.Desc.Width, loaded.Texture.Desc.Height);
                // ImageFlipV: an image drawn by the renderer. Both backends draw it with the engine's y-up clip space; Metal's
                // targets keep row 0 at the top but its viewport maps that clip space top-down, so the stored picture is upside
                // down in both cases and the sampled V is flipped on both.
                bool flip = c.Properties.GetValueOrDefault("ImageFlipV") == "true";
                float v0 = source.Y / loaded.Texture.Desc.Height, v1 = (source.Y + source.Height) / loaded.Texture.Desc.Height;
                if (flip) (v0, v1) = (v1, v0);
                var tint = Colour(c.Properties.GetValueOrDefault("Colour"), Vector4.One) * new Vector4(1, 1, 1, Alpha(c.Properties));
                float border = 0;
                bool bordered = !flip && float.TryParse(c.Properties.GetValueOrDefault("ImageBorder"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out border) && border > 0;
                // ImageAngle: radians clockwise about the box's centre (MyGUI's RotatingSkin, the map's camera marker).
                if (float.TryParse(c.Properties.GetValueOrDefault("ImageAngle"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float angle) && angle != 0)
                {
                    overlay.ImageRotated(loaded.Sampled, new(b.X, b.Y, b.X + b.Width, b.Y + b.Height),
                        new(source.X / loaded.Texture.Desc.Width, v0, (source.X + source.Width) / loaded.Texture.Desc.Width, v1), tint, angle);
                }
                else if (bordered)
                {
                    float borderScale = float.TryParse(c.Properties.GetValueOrDefault("ImageBorderScale"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? Math.Max(0, parsed) : 1;
                    float bx = Math.Min(border, source.Width / 2), by = Math.Min(border, source.Height / 2);
                    float dx = Math.Min(bx * borderScale, b.Width / 2), dy = Math.Min(by * borderScale, b.Height / 2);
                    Span<float> xs = [b.X, b.X + dx, b.X + b.Width - dx, b.X + b.Width];
                    Span<float> ys = [b.Y, b.Y + dy, b.Y + b.Height - dy, b.Y + b.Height];
                    Span<float> us = [source.X, source.X + bx, source.X + source.Width - bx, source.X + source.Width];
                    Span<float> vs = [source.Y, source.Y + by, source.Y + source.Height - by, source.Y + source.Height];
                    for (int row = 0; row < 3; row++)
                        for (int column = 0; column < 3; column++)
                            overlay.Image(loaded.Sampled, new(xs[column], ys[row], xs[column + 1], ys[row + 1]),
                                new(us[column] / loaded.Texture.Desc.Width, vs[row] / loaded.Texture.Desc.Height,
                                    us[column + 1] / loaded.Texture.Desc.Width, vs[row + 1] / loaded.Texture.Desc.Height), tint);
                }
                else
                    overlay.Image(loaded.Sampled, new(b.X, b.Y, b.X + b.Width, b.Y + b.Height),
                        new(source.X / loaded.Texture.Desc.Width, v0, (source.X + source.Width) / loaded.Texture.Desc.Width, v1), tint);
                if (angle == 0 && tint.W >= 1 && masks.TryGetValue(image.Texture, out var imageMask)) AddOpaque(imageMask, source, b, clip, allowCore: !flip && !bordered);
                overlay.Flush(width, height, clip, pass);
            }
            if (resources.Skins.TryGetValue(c.Skin, out var skin) && textures.TryGetValue(skin.Texture, out var atlas))
            {
                var atlasMask = masks.GetValueOrDefault(skin.Texture);
                // A toggled button (Checked) uses MyGUI's *_checked states, else looks pushed.
                bool on = c.Properties.GetValueOrDefault("Checked") == "true";
                string state = on ? widgetState + "_checked" : widgetState;
                foreach (var part in skin.Parts.Where(p => p.Type is "SubSkin" or "MainSkin" or "TileRect"))
                {
                    if (!part.States.TryGetValue(state, out var source) && !(on && part.States.TryGetValue("pushed", out source)) && !part.States.TryGetValue("normal", out source)) continue;
                    var dest = part.Offset.AlignTo(b, skin.Width, skin.Height, part.Align);
                    var tint = Colour(c.Properties.GetValueOrDefault("Colour"), Vector4.One) * new Vector4(1, 1, 1, Alpha(c.Properties));
                    if (part.Type == "TileRect")
                    {
                        var properties = part.StateProperties?.GetValueOrDefault(state) ?? part.StateProperties?.GetValueOrDefault("normal");
                        string? tileSize = c.Properties.GetValueOrDefault("TileSize") ?? properties?.GetValueOrDefault("TileSize");
                        var size = tileSize?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        float tileW = size?.Length == 2 ? float.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture) : source.Width;
                        float tileH = size?.Length == 2 ? float.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture) : source.Height;
                        if (properties?.GetValueOrDefault("TileH") != "true") tileW = dest.Width;
                        if (properties?.GetValueOrDefault("TileV") != "true") tileH = dest.Height;
                        if (tileW > 0 && tileH > 0 && tint.W >= 1 && atlasMask?.Covers(source) == true) opaqueRects.Add(dest.Intersect(clip));
                        if (tileW > 0 && tileH > 0)
                            for (float tileY = dest.Y; tileY < dest.Y + dest.Height; tileY += tileH)
                                for (float tileX = dest.X; tileX < dest.X + dest.Width; tileX += tileW)
                                {
                                    float pieceW = Math.Min(tileW, dest.X + dest.Width - tileX), pieceH = Math.Min(tileH, dest.Y + dest.Height - tileY);
                                    overlay.Image(atlas.Sampled, new(tileX, tileY, tileX + pieceW, tileY + pieceH),
                                        new(source.X / atlas.Texture.Desc.Width, source.Y / atlas.Texture.Desc.Height,
                                            (source.X + source.Width * pieceW / tileW) / atlas.Texture.Desc.Width, (source.Y + source.Height * pieceH / tileH) / atlas.Texture.Desc.Height), tint);
                                }
                        continue;
                    }
                    // Crop the image and UVs to the viewport together.
                    float x0 = Math.Max(0, dest.X), y0 = Math.Max(0, dest.Y), x1 = Math.Min(width, dest.X + dest.Width), y1 = Math.Min(height, dest.Y + dest.Height);
                    if (x1 <= x0 || y1 <= y0 || dest.Width <= 0 || dest.Height <= 0) continue;
                    float tw = atlas.Texture.Desc.Width, th = atlas.Texture.Desc.Height;
                    overlay.Image(atlas.Sampled, new(x0, y0, x1, y1), new(
                        (source.X + (x0 - dest.X) / dest.Width * source.Width) / tw,
                        (source.Y + (y0 - dest.Y) / dest.Height * source.Height) / th,
                        (source.X + (x1 - dest.X) / dest.Width * source.Width) / tw,
                        (source.Y + (y1 - dest.Y) / dest.Height * source.Height) / th), tint);
                    if (tint.W >= 1 && atlasMask is not null) AddOpaque(atlasMask, source, dest, clip, allowCore: true);
                }
                overlay.Flush(width, height, clip, pass);
            }
            if (c.Type == "Button" && c.Enabled && c.Action == active && c.ActiveColour is { } accent)
                overlay.Rect(b.X + 4, b.Y + b.Height - 4, b.X + b.Width - 4, b.Y + b.Height - 2, accent);
            overlay.Flush(width, height, clip, pass);
            bool focused = c.Properties.GetValueOrDefault("Focused") == "true";
            if (c.Caption.Length == 0 && !focused) continue;
            // MyGUI shows a button caption only through a text part of its skin: a button skin of image parts alone (the map's centre button, "O") shows none.
            if (c.Type == "Button" && resources.Skins.TryGetValue(c.Skin, out var captionSkin) && !resources.Templates.ContainsKey(c.Skin) && !captionSkin.Parts.Any(p => p.Type is "SimpleText" or "EditText")) continue;
            var textArea = UiTextLayout.ContentRect(resources, c);
            if (textArea.Width <= 0 || textArea.Height <= 0) continue;
            var textClip = clip.Intersect(textArea);
            string fontName = c.Properties.GetValueOrDefault("FontName") ?? TemplateFont(c.Skin) ?? "Kenshi_StandardFont_Medium";
            var font = fontName == "Meitou_TitleFont" ? titleFont ?? paintedFont ?? overlay :
                fontName.Contains("Painted", StringComparison.Ordinal) || fontName.Contains("Banner", StringComparison.Ordinal) ? paintedFont ?? standardFont ?? overlay : standardFont ?? overlay;
            // A toggled button's caption takes the skin's *_checked colour too (the editor's selected tab is brighter).
            string textState = c.Properties.GetValueOrDefault("Checked") == "true" ? widgetState + "_checked" : widgetState;
            var textColour = MyGuiColours.Text(resources, c, textState, c.Enabled ? DebugOverlay.TextColour : new(0.45f, 0.43f, 0.38f, 1));
            textColour.W *= Alpha(c.Properties);
            font.Target = overlay.Target;
            float scale = c.TextScale ?? (c.Skin.Length > 0 ? Math.Clamp(height / 1080f, 0.5f, 1.5f) : 1);
            bool original = false;
            // Settings request a font baked at its physical pixel size rather than scaling the 16px atlas.
            if (int.TryParse(c.Properties.GetValueOrDefault("FontPixels"), out int pixels) && StandardSource() is { } standardFace)
            {
                pixels = Math.Clamp(Math.Max(pixels, (int)MathF.Round(minimumTextPixels)), 8, 96);
                // An explicit pixel size is Meitou's own text in either mode: oversampled and freely placed.
                font = UiFontFor("", standardFace.Spec, standardFace.Path, pixels * 64L, 72, false, false);
                font.Target = overlay.Target;
                scale = 1;
            }
            else if (ResourceFont(fontName, c.TextScale ?? height / 1080f, minimumTextPixels) is { } resourceFont)
            {
                font = resourceFont;
                font.Target = overlay.Target;
                scale = 1;
                // MyGUI never scales text to its widget: a font resource keeps its size in the Faithful mode, overflowing the line box and clipped by the widget.
                original = Mode == UiMode.Faithful;
            }
            if (minimumTextPixels > 0 || original)
            {
                // Short captions keep the main UI's readable size. Centre the line box over
                // its inset client and clip to the widget instead of shrinking the lettering.
                float extra = Math.Max(0, font.LineHeight * scale - textArea.Height);
                textArea = textArea with { Y = textArea.Y - extra / 2, Height = textArea.Height + extra };
                textClip = clip.Intersect(new(textArea.X, b.Y, textArea.Width, b.Height));
            }
            else scale = Math.Min(scale, textArea.Height / font.LineHeight);
            if (scale > 1.25f && (font == standardFont ? standardLarge : font == paintedFont ? paintedLarge : null) is { } large)
            {
                scale *= font.LineHeight / large.LineHeight;
                font = large;
                font.Target = overlay.Target;
            }
            var laid = LayOutText(c.Caption, font, textArea.Width, scale, c.Properties.GetValueOrDefault("WordWrap") == "true", original,
                focused, focused && c.Properties.GetValueOrDefault("Numeric") == "true");
            var (text, runs, lines) = (laid.Text, laid.Runs, laid.Lines);
            int cursor = 0;
            string align = c.Properties.GetValueOrDefault("TextAlign") ?? "Left VCenter";
            // An EditBox scrolled by its bar (MyGuiPresentation: TextScroll pixels); the client clips the lines.
            float scrolled = float.TryParse(c.Properties.GetValueOrDefault("TextScroll"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float textScroll) ? textScroll : 0;
            float y = -scrolled + textArea.Y + (align.Contains("Bottom", StringComparison.Ordinal) ? Math.Max(0, textArea.Height - font.LineHeight * scale * lines.Count) : HasWord(align, "Center", "VCenter") ? Math.Max(0, (textArea.Height - font.LineHeight * scale * lines.Count) / 2) : 0);
            if (focused && lines.Count > 1)
            {
                // A wrapped box being typed in: the caret on its own line, the text scrolled up to keep that line in view (no selection drawn).
                int caret = int.TryParse(c.Properties.GetValueOrDefault("CaretIndex"), out int at) ? Math.Clamp(at, 0, text.Length) : 0;
                var (caretLine, caretColumn) = CaretInLines(text, p => font.WrapText(p, textArea.Width, scale), caret);
                caretLine = Math.Min(caretLine, lines.Count - 1);
                caretColumn = Math.Min(caretColumn, lines[caretLine].Length);
                float lineHeight = font.LineHeight * scale;
                y -= Math.Max(0, (caretLine + 1) * lineHeight - textArea.Height);
                if ((System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 2)) % 2 == 0)
                {
                    float caretX = textArea.X + font.MeasureText(lines[caretLine][..caretColumn], scale), caretY = y + caretLine * lineHeight;
                    overlay.Rect(caretX, caretY, caretX + 1, caretY + lineHeight, textColour);
                }
                overlay.Flush(width, height, textClip, pass);
            }
            else if (focused)
            {
                int Index(string key) => int.TryParse(c.Properties.GetValueOrDefault(key), out int at) ? Math.Clamp(at, 0, text.Length) : 0;
                int selection = Index("SelectionStart"), length = Index("SelectionLength"), caret = Index("CaretIndex");
                float textWidth = font.MeasureText(text, scale);
                float origin = textArea.X + (align.Contains("Right", StringComparison.Ordinal) ? textArea.Width - textWidth : HasWord(align, "Center", "HCenter") ? (textArea.Width - textWidth) / 2 : 0);
                if (length > 0)
                {
                    float start = origin + font.MeasureText(text[..selection], scale);
                    float end = origin + font.MeasureText(text[..Math.Min(selection + length, text.Length)], scale);
                    overlay.Rect(start, y, end, y + font.LineHeight * scale, new(0.25f, 0.4f, 0.45f, 0.8f));
                }
                if ((System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 2)) % 2 == 0)
                {
                    float caretX = origin + font.MeasureText(text[..caret], scale);
                    overlay.Rect(caretX, y, caretX + 1, y + font.LineHeight * scale, textColour);
                }
                overlay.Flush(width, height, textClip, pass);
            }
            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                string line = lines[lineIndex];
                float textWidth = laid.Widths[lineIndex];
                float x = textArea.X + (align.Contains("Right", StringComparison.Ordinal) ? textArea.Width - textWidth : HasWord(align, "Center", "HCenter") ? (textArea.Width - textWidth) / 2 : 0);
                if (c.Type == "EditBox" && lines.Count == 1) editBoxes.Add(new(clip.Intersect(b), font, scale, x, line));
                if (new Meitou.Data.Ui.UiRect(x, y, textWidth, font.LineHeight * scale).Intersect(textClip).Width > 0)
                {
                    if (c.Properties.GetValueOrDefault("TextShadow") == "true") font.TextScaled(line, x + 1, y + 1, new(0, 0, 0, textColour.W), scale);
                    if (runs.Count == 0) font.TextScaled(line, x, y, textColour, scale);
                    else DrawRuns(font, text, line, ref cursor, runs, x, y, textColour, scale);
                }
                else if (runs.Count > 0 && text.IndexOf(line, cursor, StringComparison.Ordinal) is >= 0 and int at) cursor = at + line.Length;
                y += font.LineHeight * scale;
            }
            font.Flush(width, height, textClip, pass);
        }
        overlay.Flush(width, height, null, pass);
        if (!CollectionsMarshal.AsSpan(opaqueRects).SequenceEqual(CollectionsMarshal.AsSpan(previousOpaqueRects)))
        {
            previousOpaqueRects.Clear();
            previousOpaqueRects.AddRange(opaqueRects);
            opaqueArea = null;
        }
        (coverageWidth, coverageHeight) = (width, height);
        opaqueRects.Clear();
        if (++textFrame % TextLayoutLife == 0)
            foreach (var (key, laid) in textLayouts)
                if (laid.Used < textFrame - TextLayoutLife) textLayouts.Remove(key);
    }

    /// <summary>
    /// <paramref name="caption"/> as drawn: its plain text and colour runs (MyGUI's inline tags; an edited box shows its text as typed), its lines (wrapped, or one per line
    /// break and shortened with "..." to <paramref name="width"/>; a font resource's long line in the Faithful mode is cropped by the widget instead, as MyGUI does) and
    /// their widths. Made once per caption, font, width and scale, not every frame.
    /// </summary>
    TextLayout LayOutText(string caption, DebugOverlay font, float width, float scale, bool wrap, bool original, bool focused, bool numeric)
    {
        var key = new TextLayoutKey(font, caption, width, scale, (wrap ? 1 : 0) | (original ? 2 : 0) | (focused ? 4 : 0) | (numeric ? 8 : 0));
        if (!textLayouts.TryGetValue(key, out var laid))
        {
            string text = caption.Replace("\\n", "\n", StringComparison.Ordinal);
            IReadOnlyList<TextColourRun> runs = [];
            if (!focused) (text, runs) = MyGuiTextTags.Parse(text);
            IReadOnlyList<string> lines = numeric ? [text] : wrap ? font.WrapText(text, width, scale) : FitLines(text, original, font, width, scale);
            var widths = new float[lines.Count];
            for (int i = 0; i < widths.Length; i++) widths[i] = font.MeasureText(lines[i], scale);
            textLayouts[key] = laid = new(text, runs, lines, widths);
        }
        laid.Used = textFrame;
        return laid;
    }

    string? TemplateFont(string skin)
    {
        if (!templateFonts.TryGetValue(skin, out var font))
            templateFonts[skin] = font = resources.Templates.TryGetValue(skin, out var template)
                ? template.Descendants("Property").FirstOrDefault(p => (string?)p.Attribute("key") == "FontName")?.Attribute("value")?.Value : null;
        return font;
    }

    /// <summary>Records the part of <paramref name="dest"/> that is opaque: all of it when every texel of <paramref name="source"/> is, else (when <paramref name="allowCore"/>) the stretched image of the slice's largest opaque core, so ragged panel edges do not disqualify the solid middle.</summary>
    void AddOpaque(OpacityMask mask, UiRect source, UiRect dest, UiRect clip, bool allowCore)
    {
        if (mask.Covers(source)) { opaqueRects.Add(dest.Intersect(clip)); return; }
        if (!allowCore || source.Width <= 0 || source.Height <= 0 || mask.Core(source) is not { } core) return;
        float fx = dest.Width / source.Width, fy = dest.Height / source.Height;
        opaqueRects.Add(new UiRect(dest.X + (core.X - source.X) * fx, dest.Y + (core.Y - source.Y) * fy, core.Width * fx, core.Height * fy).Intersect(clip));
    }

    /// <summary>The area of the union of <paramref name="rects"/> (overlaps once): the cells of the grid their edges make, each in or out.</summary>
    internal static double UnionArea(List<UiRect> rects)
    {
        rects.RemoveAll(r => r.Width <= 0 || r.Height <= 0);
        if (rects.Count == 0) return 0;
        var xs = rects.SelectMany(r => new[] { r.X, r.X + r.Width }).Distinct().Order().ToArray();
        var ys = rects.SelectMany(r => new[] { r.Y, r.Y + r.Height }).Distinct().Order().ToArray();
        var covered = new bool[(xs.Length - 1) * (ys.Length - 1)];
        foreach (var r in rects)
        {
            int x0 = Array.BinarySearch(xs, r.X), x1 = Array.BinarySearch(xs, r.X + r.Width), y0 = Array.BinarySearch(ys, r.Y), y1 = Array.BinarySearch(ys, r.Y + r.Height);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) covered[y * (xs.Length - 1) + x] = true;
        }
        double area = 0;
        for (int y = 0; y + 1 < ys.Length; y++) for (int x = 0; x + 1 < xs.Length; x++)
            if (covered[y * (xs.Length - 1) + x]) area += (double)(xs[x + 1] - xs[x]) * (ys[y + 1] - ys[y]);
        return area;
    }

    /// <summary>One bit per texel of an uploaded texture: alpha 255. Answers whether a source rectangle (texels) is opaque throughout, remembering each rectangle asked.</summary>
    sealed class OpacityMask
    {
        readonly ulong[] bits;
        readonly int width, height;
        readonly Dictionary<(int X0, int Y0, int X1, int Y1), bool> answers = new();
        readonly Dictionary<(int X0, int Y0, int X1, int Y1), UiRect?> cores = new();

        public OpacityMask(RgbaImage image)
        {
            (width, height) = (image.Width, image.Height);
            bits = new ulong[(width * height + 63) / 64];
            var pixels = image.Pixels;
            for (int i = 0, n = width * height; i < n; i++)
                if (pixels[i * 4 + 3] == 255) bits[i >> 6] |= 1UL << (i & 63);
        }

        public bool Covers(UiRect source)
        {
            (int X0, int Y0, int X1, int Y1) key = (Math.Max(0, (int)MathF.Floor(source.X)), Math.Max(0, (int)MathF.Floor(source.Y)), Math.Min(width, (int)MathF.Ceiling(source.X + source.Width)), Math.Min(height, (int)MathF.Ceiling(source.Y + source.Height)));
            if (answers.TryGetValue(key, out bool known)) return known;
            bool opaque = key.X1 > key.X0 && key.Y1 > key.Y0;
            for (int y = key.Y0; opaque && y < key.Y1; y++)
                for (int x = key.X0; x < key.X1; x++)
                {
                    int i = y * width + x;
                    if ((bits[i >> 6] & 1UL << (i & 63)) == 0) { opaque = false; break; }
                }
            return answers[key] = opaque;
        }

        /// <summary>The largest rectangle of opaque texels inside <paramref name="source"/>, inset one texel so bilinear taps never reach a translucent neighbour; null when none is left.</summary>
        public UiRect? Core(UiRect source)
        {
            (int X0, int Y0, int X1, int Y1) key = (Math.Max(0, (int)MathF.Floor(source.X)), Math.Max(0, (int)MathF.Floor(source.Y)), Math.Min(width, (int)MathF.Ceiling(source.X + source.Width)), Math.Min(height, (int)MathF.Ceiling(source.Y + source.Height)));
            if (cores.TryGetValue(key, out var known)) return known;
            int w = key.X1 - key.X0, h = key.Y1 - key.Y0;
            UiRect? best = null;
            if (w > 2 && h > 2)
            {
                var heights = new int[w];
                var stack = new Stack<int>();
                long bestArea = 0;
                for (int row = 0; row < h; row++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (key.Y0 + row) * width + key.X0 + x;
                        heights[x] = (bits[i >> 6] & 1UL << (i & 63)) != 0 ? heights[x] + 1 : 0;
                    }
                    stack.Clear();
                    for (int x = 0; x <= w; x++)
                    {
                        int at = x == w ? 0 : heights[x];
                        while (stack.Count > 0 && heights[stack.Peek()] >= at)
                        {
                            int top = heights[stack.Pop()], left = stack.Count > 0 ? stack.Peek() + 1 : 0;
                            long area = (long)top * (x - left);
                            if (area > bestArea) { bestArea = area; best = new UiRect(key.X0 + left, key.Y0 + row - top + 1, x - left, top); }
                        }
                        stack.Push(x);
                    }
                }
                if (best is { } found) best = found.Width > 2 && found.Height > 2 ? new UiRect(found.X + 1, found.Y + 1, found.Width - 2, found.Height - 2) : null;
            }
            return cores[key] = best;
        }
    }

    // A text's lines and its alignment words, read without the split, query and closure per control per frame they were (docs/bench.md "Play benchmark").
    static readonly System.Buffers.SearchValues<char> LineEnds = System.Buffers.SearchValues.Create("\r\n\f\u0085\u2028\u2029");   // what ReplaceLineEndings breaks at

    static string[] FitLines(string text, bool original, DebugOverlay font, float width, float scale)
    {
        var lines = text.AsSpan().IndexOfAny(LineEnds) >= 0 ? text.ReplaceLineEndings("\n").Split('\n') : [text];
        if (!original) for (int i = 0; i < lines.Length; i++) lines[i] = font.FitText(lines[i], width, scale);
        return lines;
    }

    /// <summary>
    /// Draws <paramref name="line"/> of the plain <paramref name="text"/> in its tags' colours (<see cref="MyGuiTextTags"/>; before the first tag <paramref name="colour"/>, the alpha
    /// always <paramref name="colour"/>'s). <paramref name="cursor"/> is where the line is looked for in the text, then past it; a line not found there (shortened) starts at it.
    /// </summary>
    static void DrawRuns(DebugOverlay font, string text, string line, ref int cursor, IReadOnlyList<TextColourRun> runs, float x, float y, Vector4 colour, float scale)
    {
        int at = text.IndexOf(line, cursor, StringComparison.Ordinal);
        if (at < 0) at = cursor;
        cursor = Math.Min(text.Length, at + line.Length);
        int run = -1;
        while (run + 1 < runs.Count && runs[run + 1].Start <= at) run++;
        for (int start = 0; start < line.Length;)
        {
            int end = run + 1 < runs.Count ? Math.Clamp(runs[run + 1].Start - at, start + 1, line.Length) : line.Length;
            string piece = line[start..end];
            font.TextScaled(piece, x, y, run < 0 ? colour : new(runs[run].Colour, colour.W), scale);
            x += font.MeasureText(piece, scale);
            start = end;
            while (run + 1 < runs.Count && runs[run + 1].Start <= at + start) run++;
        }
    }

    /// <summary>
    /// Where <paramref name="caret"/> (an index into <paramref name="text"/>) falls in the text wrapped by <paramref name="wrap"/>: the line and
    /// the characters before it there. Each paragraph wraps on its own and wrapping keeps one space of a run, so its lines are matched back to it.
    /// </summary>
    static (int Line, int Column) CaretInLines(string text, Func<string, IReadOnlyList<string>> wrap, int caret)
    {
        int line = 0, start = 0;
        var paragraphs = text.Split('\n');
        for (int p = 0; p < paragraphs.Length; p++)
        {
            string paragraph = paragraphs[p];
            var wrapped = wrap(paragraph);
            if (caret > start + paragraph.Length && p < paragraphs.Length - 1) { line += wrapped.Count; start += paragraph.Length + 1; continue; }
            int local = caret - start, at = 0;
            for (int l = 0; l < wrapped.Count; l++)
            {
                string s = wrapped[l];
                while (at < paragraph.Length && paragraph[at] == ' ') at++;   // the spaces the line broke at
                int column = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == ' ') while (at < paragraph.Length && paragraph[at] == ' ') at++;
                    else at++;
                    if (at <= local) column = i + 1;
                }
                if (local <= at || l == wrapped.Count - 1) return (line + l, column);
            }
            return (line, 0);
        }
        return (line, 0);
    }

    static bool HasWord(string align, string a, string b)
    {
        var text = align.AsSpan();
        foreach (var range in text.Split(' '))
            if (text[range] is var word && (word.SequenceEqual(a) || word.SequenceEqual(b))) return true;
        return false;
    }

    static float Alpha(IReadOnlyDictionary<string, string> properties) => float.TryParse(properties.GetValueOrDefault("Alpha"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out float value) ? Math.Clamp(value, 0, 1) : 1;
    static Vector4 Colour(string? value, Vector4 fallback)
    {
        if (value is null) return fallback;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return fallback;
        var numbers = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++) if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return fallback;
        return new(numbers[0], numbers[1], numbers[2], parts.Length == 4 ? numbers[3] : 1);
    }

    /// <summary>
    /// Whether the image file <paramref name="path"/> (an item icon) is on the GPU and can be shown as an ImageBox's <c>ImageTexture</c> of <see cref="IconTexture"/>.
    /// The first ask starts decoding it on a worker, <see cref="Draw"/> uploads it when done; a file that cannot be read stays false.
    /// </summary>
    public bool IconReady(string path)
    {
        string name = IconTexture(path);
        if (textures.ContainsKey(name)) return true;
        if (requestedIcons.Add(name))
            Task.Run(() =>
            {
                try { decodedIcons.Enqueue((name, TextureLoader.LoadFile(path, allMips: false).Levels[0])); }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException) { failedIcons.TryAdd(name, true); }
            });
        return false;
    }

    /// <summary>Icon files asked for (<see cref="IconReady"/>) that are still decoding or waiting for their upload.</summary>
    public int PendingIconFiles => requestedIcons.Count(n => !textures.ContainsKey(n) && !failedIcons.ContainsKey(n));
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> failedIcons = new(StringComparer.Ordinal);

    /// <summary>The <c>ImageTexture</c> name of the icon the renderer draws from an item's mesh (<see cref="RenderedIcons"/>), shown once it exists; flip it (<c>ImageFlipV</c>), as the renderer's targets are upside down.</summary>
    public static string RenderedIconTexture(string record, string material, string colour = "") => RenderedIconPrefix + record + "|" + material + "|" + colour;
    public const string RenderedIconPrefix = "meitou:icon:";

    /// <summary>The <c>ImageTexture</c> name of the picture the renderer draws of a BUILDING (<see cref="RenderedIcons"/> looks it up too); flip it like <see cref="RenderedIconTexture"/>.</summary>
    public static string RenderedBuildingIconTexture(string record) => RenderedBuildingIconPrefix + record;
    public const string RenderedBuildingIconPrefix = "meitou:building-icon:";

    /// <summary>The record of a <see cref="RenderedBuildingIconTexture"/> name.</summary>
    public static bool TryParseRenderedBuildingIcon(string name, out string record)
    {
        record = name.StartsWith(RenderedBuildingIconPrefix, StringComparison.Ordinal) ? name[RenderedBuildingIconPrefix.Length..] : "";
        return record.Length > 0;
    }

    /// <summary>The record, material and colour of a <see cref="RenderedIconTexture"/> name.</summary>
    public static bool TryParseRenderedIcon(string name, out string record, out string material, out string colour)
    {
        (record, material, colour) = ("", "", "");
        if (!name.StartsWith(RenderedIconPrefix, StringComparison.Ordinal) || name.Split('|') is not [var r, var m, var c]) return false;
        (record, material, colour) = (r[RenderedIconPrefix.Length..], m, c);
        return record.Length > 0;
    }

    /// <summary>Looks up the texture of a <see cref="RenderedIconTexture"/> or <see cref="RenderedBuildingIconTexture"/> name, asking for it to be made when it is not (the host sets this; null: no icons are drawn).</summary>
    public Func<string, (GpuTexture Texture, SampledTexture Sampled)?>? RenderedIcons { get; set; }

    /// <summary>Whether the rendered icon <paramref name="name"/> can be drawn now (this asks for it too).</summary>
    public bool RenderedIconReady(string name) => RenderedIcons?.Invoke(name) is not null;

    /// <summary>The <c>ImageTexture</c> name under which the image file <paramref name="path"/> is shown once <see cref="IconReady"/>.</summary>
    public static string IconTexture(string path) => IconPrefix + path;
    const string IconPrefix = "file:";
    const int IconUploadsPerFrame = 4;
    readonly HashSet<string> requestedIcons = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentQueue<(string Name, RgbaImage Image)> decodedIcons = new();

    readonly Dictionary<string, MyGuiPointer?> pointers = new(StringComparer.Ordinal);

    /// <summary>
    /// Draws the mouse pointer <paramref name="name"/> (kenshi_pointers.xml) with its hot spot on <paramref name="mouse"/>, in screen pixels as MyGUI does; nothing for an unknown name.
    /// Called last in a frame: the original's <c>Pointer</c> layer is above every other.
    /// </summary>
    /// <summary>
    /// The text index nearest <paramref name="p"/> in the one-line edit box under it as the last <see cref="Draw"/> laid it out (the gap between two characters closest to the pointer);
    /// null when no edit box was drawn there. The screen host places the caret with it.
    /// </summary>
    public int? EditCaretAt(Vector2 p)
    {
        for (int i = editBoxes.Count - 1; i >= 0; i--)
        {
            var (box, font, scale, x, text) = editBoxes[i];
            if (!box.Contains(p.X, p.Y)) continue;
            int best = 0;
            float distance = MathF.Abs(p.X - x);
            for (int k = 1; k <= text.Length; k++)
                if (MathF.Abs(p.X - (x + font.MeasureText(text[..k], scale))) is var d && d < distance) { best = k; distance = d; }
            return best;
        }
        return null;
    }

    public void DrawPointer(string name, Vector2 mouse, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        if (!pointers.TryGetValue(name, out var pointer)) pointers[name] = pointer = MyGuiPointers.Resolve(resources, name);
        if (pointer is null) return;
        overlay.Flush(width, height, null, pass);   // whatever was queued before (another texture, text) goes first
        var loaded = LoadTexture(pointer.Image.Texture);
        var at = pointer.At(MathF.Round(mouse.X), MathF.Round(mouse.Y));
        var source = pointer.Image.Source;
        float w = loaded.Texture.Desc.Width, h = loaded.Texture.Desc.Height;
        overlay.Image(loaded.Sampled, new(at.X, at.Y, at.X + at.Width, at.Y + at.Height),
            new(source.X / w, source.Y / h, (source.X + source.Width) / w, (source.Y + source.Height) / h), Vector4.One);
        overlay.Flush(width, height, null, pass);
    }

    (GpuTexture Texture, SampledTexture Sampled) LoadTexture(string name)
    {
        if (external.TryGetValue(name, out var given) || textures.TryGetValue(name, out given)) return given;
        if ((name.StartsWith(RenderedIconPrefix, StringComparison.Ordinal) || name.StartsWith(RenderedBuildingIconPrefix, StringComparison.Ordinal)) && RenderedIcons?.Invoke(name) is { } rendered) return rendered;
        string path = resolve(name) ?? throw new FileNotFoundException($"MyGUI texture not found: {name}");
        if (stored.Remove(name, out var reading) && reading.Result is { } dds) return UploadStored(name, dds);
        return Upload(name, TextureLoader.LoadFile(path, allMips: false).Levels[0]);
    }

    /// <summary>
    /// Starts reading the S3TC (BC1-BC3) DDS files among the ImageBox textures <paramref name="names"/> on workers; the first <see cref="Draw"/> that shows one
    /// uploads its top level as stored, with no CPU decode and no <see cref="OpacityMask"/> (it never counts in <see cref="OpaqueCoverage"/>). The overview's map,
    /// an 8192 x 8192 BC1 file, decoded to 256 MB of RGBA8 inside the frame that opened the panel: about 400 ms (docs/bench.md "Play benchmark").
    /// </summary>
    public void Prefetch(IEnumerable<string> names)
    {
        foreach (string name in names.Distinct(StringComparer.Ordinal))
            if (!name.EndsWith("GUI_Map.dds", StringComparison.OrdinalIgnoreCase)
                && !textures.ContainsKey(name) && !external.ContainsKey(name) && !stored.ContainsKey(name) && resolve(name) is { } path && path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                stored[name] = Task.Run(() =>
                {
                    try { return DdsReader.ReadFile(path) is { Format: DdsFormat.Bc1 or DdsFormat.Bc2 or DdsFormat.Bc3, IsCubemap: false, IsVolume: false, ImageCount: 1, Width: > 0, Height: > 0 } dds ? dds : null; }
                    catch (Exception) { return null; }   // a file the reader cannot take is loaded as before, at first use (where its error shows)
                });
    }

    readonly Dictionary<string, Task<DdsFile?>> stored = new(StringComparer.Ordinal);

    (GpuTexture Texture, SampledTexture Sampled) UploadStored(string name, DdsFile dds)
    {
        var format = dds.Format switch
        {
            DdsFormat.Bc1 => InternalFormat.CompressedRgbaS3TCDxt1Ext,
            DdsFormat.Bc2 => InternalFormat.CompressedRgbaS3TCDxt3Ext,
            _ => InternalFormat.CompressedRgbaS3TCDxt5Ext,
        };
        using var upload = overlay.Gpu.Uploads.Begin();
        var texture = upload.Create(new TextureDesc(format, dds.Width, dds.Height, Name: $"UI {name}"));
        upload.Write(texture, 0, 0, new PixelRect(0, 0, (uint)dds.Width, (uint)dds.Height), dds.SurfaceData(dds.Surface(0, 0)));
        return textures[name] = (texture, Sampled(texture));
    }

    (GpuTexture Texture, SampledTexture Sampled) Upload(string name, RgbaImage image, bool srgb = false)
    {
        // A redrawn image replaces its texture (released once the frames in flight are done).
        if (textures.Remove(name, out var old)) old.Texture.Dispose();
        using var upload = overlay.Gpu.Uploads.Begin();
        var texture = upload.Create(new TextureDesc(srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8, image.Width, image.Height, Name: $"UI {name}"));
        upload.Write(texture, 0, 0, new PixelRect(0, 0, (uint)image.Width, (uint)image.Height), image.Pixels);
        masks[name] = new OpacityMask(image);
        return textures[name] = (texture, Sampled(texture));
    }

    SampledTexture Sampled(GpuTexture texture) => new(overlay.Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), texture.View());

    public void Dispose()
    {
        ReleaseTextures();
        stored.Clear();
        foreach (var font in uiFonts.Values) font.Dispose();
        uiFonts.Clear();
        standardFont?.Dispose(); paintedFont?.Dispose(); titleFont?.Dispose(); standardLarge?.Dispose(); paintedLarge?.Dispose();
        standardFont = null; paintedFont = null; titleFont = null; standardLarge = null; paintedLarge = null;
    }
}
