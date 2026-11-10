using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Textures;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Ui;

/// <summary>
/// Native alpha-composited MyGUI atlas slices and styled native control frames.
/// </summary>
public sealed partial class UiPainter : IDisposable
{
    /// <summary>
    /// The game's standard face (Exo2 SemiBold): the fallback for text whose font resource is not found, and the debug overlay's when no monospace
    /// system font is.
    /// </summary>
    internal const string StandardFontFile = "Exo2-SemiBold.ttf";

    /// <summary>
    /// The overlay everything is drawn with: its device, its batches and its text when no font of the painter's is made.
    /// </summary>
    readonly DebugOverlay _overlay;

    /// <summary>
    /// The MyGUI resources: font resources, skins, templates, images and pointers.
    /// </summary>
    readonly MyGuiResources _resources;

    /// <summary>
    /// Finds a game file by name (fonts, textures); null when it is not found.
    /// </summary>
    readonly Func<string, string?> _resolve;

    #region Fonts

    /// <summary>
    /// The most glyph-cache fonts kept (each holds an atlas of 0.25 to 4 MiB): beyond it a size already made, the nearest, is used instead.
    /// </summary>
    const int MaxUiFonts = 48;

    /// <summary>
    /// The pixel size the large standard and painted faces are baked at.
    /// </summary>
    const float LargePixels = 48;

    /// <summary>
    /// The fallback faces: standard (Exo2), painted (Sentencia) and the painted title stamp; null when their file is not found.
    /// </summary>
    DebugOverlay? _standardFont, _paintedFont, _titleFont;

    /// <summary>
    /// The standard and painted faces baked at <see cref="LargePixels"/>: lettering scaled well past the 16 px atlases is drawn from these, so it
    /// stays sharp.
    /// </summary>
    DebugOverlay? _standardLarge, _paintedLarge;

    /// <summary>
    /// The glyph-cache fonts made, by resource, file, size, dpi and look (at most <see cref="MaxUiFonts"/>).
    /// </summary>
    readonly Dictionary<UiFontKey, DebugOverlay> _uiFonts = [];

    /// <summary>
    /// The TrueType font resources read, by name (null: none of that name, or not a TrueType one).
    /// </summary>
    readonly Dictionary<string, MyGuiTrueTypeFont?> _fontSpecs = new(StringComparer.Ordinal);

    /// <summary>
    /// The point size the original draws each font file at a size and resolution (<see cref="EffectiveFontSize"/>).
    /// </summary>
    readonly Dictionary<(string Path, float Size, float Resolution), float> _fontSizes = [];

    /// <summary>
    /// Whether FreeType's absence was reported, and whether <see cref="_standardSource"/> was looked up.
    /// </summary>
    bool _freeTypeWarned, _standardSourceKnown;

    /// <summary>
    /// The file and resource standard-face text of an explicit pixel size is drawn from (<see cref="StandardSource"/>); null when neither is found.
    /// </summary>
    (string Path, MyGuiTrueTypeFont? Spec)? _standardSource;

    /// <summary>
    /// The <c>font size2</c> option's value (<see cref="FontSizeSetting"/>).
    /// </summary>
    float _fontSizeSetting = KenshiFontSize.DefaultSetting;

    /// <summary>
    /// The Interface scale setting (1 = 100%): every size taken from the window's height is times this.
    /// </summary>
    public float InterfaceScale { get; set; } = 1;

    /// <summary>
    /// The game files the fonts were found at, by name (null: not found).
    /// </summary>
    readonly Dictionary<string, string?> _fontPaths = new(StringComparer.Ordinal);

    /// <summary>
    /// How text is drawn (docs/formats/mygui.md "Fonts"). <see cref="UiMode.Faithful"/>: MyGUI's own rendering of the font resources (FreeType native
    /// hinting, the resource's <c>Codes</c> and substitute glyph, its line metrics, whole pixels). <see cref="UiMode.Meitou"/> (the default):
    /// any glyph the face has, light hinting or oversampling, Meitou's line box. Set by the host; fonts of the other mode are made when it changes.
    /// </summary>
    public UiMode Mode { get; set; } = UiMode.Meitou;

    /// <summary>
    /// A font made for one resource, file, size, dpi and look.
    /// </summary>
    readonly record struct UiFontKey(string Resource, string Path, long Size64, uint Dpi, bool Faithful, bool Snapped);

    /// <summary>
    /// The TrueType font resource <paramref name="name"/>, read once; null when there is none of that name.
    /// </summary>
    MyGuiTrueTypeFont? FontSpec(string name)
    {
        if (!_fontSpecs.TryGetValue(name, out var spec))
        {
            var read = _resources.Resources.TryGetValue(name, out var resource) ? MyGuiTrueTypeFont.Read(resource) : null;
            _fontSpecs[name] = spec = read;
        }

        return spec;
    }

    /// <summary>
    /// The file and resource that standard-face text of an explicit pixel size (<c>FontPixels</c>) is drawn from: the standard font resource (a
    /// locale's own font), else Exo2.
    /// </summary>
    (string Path, MyGuiTrueTypeFont? Spec)? StandardSource()
    {
        if (_standardSourceKnown) return _standardSource;

        _standardSourceKnown = true;
        var spec = FontSpec("Kenshi_StandardFont_Medium");
        if (spec is not null && FontPath(spec.Source) is { } path) _standardSource = (path, spec);
        else if (FontPath(StandardFontFile) is { } exo) _standardSource = (exo, null);

        return _standardSource;
    }

    /// <summary>
    /// The font for <paramref name="resource"/> at <paramref name="size64"/> (1/64 points) and <paramref name="dpi"/> in the look asked for, made
    /// once; past <see cref="MaxUiFonts"/> the nearest made already.
    /// </summary>
    DebugOverlay UiFontFor(string resource, MyGuiTrueTypeFont? spec, string path, long size64, uint dpi, bool faithful, bool snapped)
    {
        var key = new UiFontKey(resource, path, size64, dpi, faithful, snapped);
        if (_uiFonts.TryGetValue(key, out var known)) return known;

        if (_uiFonts.Count >= MaxUiFonts)
        {
            var nearest = NearestUiFont(resource, path, size64, dpi, faithful, snapped);
            if (nearest is not null) return nearest;
        }

        return _uiFonts[key] = CreateUiOverlay(new UiFontOptions(path, size64, dpi, faithful, snapped, spec));
    }

    /// <summary>
    /// The font made already for the same resource, file, dpi and look whose size is nearest <paramref name="size64"/>, or null.
    /// </summary>
    DebugOverlay? NearestUiFont(string resource, string path, long size64, uint dpi, bool faithful, bool snapped)
    {
        // Continuous resizing asks for ever new sizes: reuse the nearest one made (fonts are never freed while the UI runs).
        DebugOverlay? nearest = null;
        long distance = long.MaxValue;

        foreach (var (other, font) in _uiFonts)
        {
            bool same = other.Resource == resource && other.Path == path && other.Dpi == dpi && other.Faithful == faithful && other.Snapped == snapped;
            if (same && Math.Abs(other.Size64 - size64) is var gap && gap < distance) (nearest, distance) = (font, gap);
        }

        return nearest;
    }

    /// <summary>
    /// A glyph-cache overlay; if FreeType cannot be used on this machine, a Latin-only stb_truetype one (once reported).
    /// </summary>
    DebugOverlay CreateUiOverlay(UiFontOptions options)
    {
        try
        {
            return DebugOverlay.FromUiFont(_overlay.Gpu, new UiFont(_overlay.Gpu, options));
        }
        catch (Exception e) when (e is DllNotFoundException or FileLoadException or BadImageFormatException or EntryPointNotFoundException
            or InvalidOperationException or InvalidDataException)
        {
            if (!_freeTypeWarned)
            {
                _freeTypeWarned = true;
                Console.Error.WriteLine($"ui        FreeType is unavailable ({e.Message}); text uses the Latin-only fallback.");
            }
            return DebugOverlay.FromFont(_overlay.Gpu, options.Path, options.Size64 / 64f * options.Dpi / 72f);
        }
    }

    /// <summary>
    /// The points a TrueType font resource is drawn at for the UI scale <paramref name="scale"/> (docs/formats/mygui.md): its size scaled and cut to
    /// whole points, then resized as the original does on load (<see cref="KenshiFontSize"/>). At 1080 lines the resource's own size, resized.
    /// </summary>
    float ScaledFontSize(string path, MyGuiTrueTypeFont spec, float scale) =>
        EffectiveFontSize(path, MathF.Floor((spec.Size > 0 ? spec.Size : 20) * scale + 1e-3f), spec.Resolution > 0 ? spec.Resolution : 50);

    /// <summary>
    /// The em pixels of the original's standard text (<c>Kenshi_StandardFont_Medium</c>) at <paramref name="viewportHeight"/> lines, as <see
    /// cref="ScaledFontSize"/> sizes it (13.9 at 1080, 17.4 at 1440); the linear 13.9 per 1080 lines without the resource.
    /// </summary>
    public float StandardTextPixels(int viewportHeight)
    {
        if (FontSpec("Kenshi_StandardFont_Medium") is { } spec && FontPath(spec.Source) is { } path)
            return ScaledFontSize(path, spec, Scale(viewportHeight)) * (spec.Resolution > 0 ? spec.Resolution : 50) / 72;
        return 20 * 50 / 72f * Scale(viewportHeight);
    }

    /// <summary>
    /// The point size the original draws font resource <paramref name="path"/> at (<see cref="KenshiFontSize"/>).
    /// </summary>
    float EffectiveFontSize(string path, float size, float resolution)
    {
        if (!_fontSizes.TryGetValue((path, size, resolution), out float effective))
            _fontSizes[(path, size, resolution)] = effective = KenshiFontSize.Effective(File.ReadAllBytes(path), size, resolution, FontSizeSetting);
        return effective;
    }

    /// <summary>
    /// The <c>font size2</c> option (points added to every font's default height); a change takes effect on the next frames.
    /// </summary>
    public float FontSizeSetting
    {
        get => _fontSizeSetting;
        set
        {
            if (_fontSizeSetting == value) return;

            _fontSizeSetting = value;
            _fontSizes.Clear();
        }
    }

    /// <summary>
    /// The path of the game file <paramref name="name"/>, looked up once; null when it is not found.
    /// </summary>
    string? FontPath(string name)
    {
        if (!_fontPaths.TryGetValue(name, out var path)) _fontPaths[name] = path = _resolve(name);
        return path;
    }

    #endregion

    #region Textures

    /// <summary>
    /// The textures loaded from files or images and owned by the painter, by name.
    /// </summary>
    readonly Dictionary<string, (GpuTexture Texture, SampledTexture Sampled)> _textures = new(StringComparer.Ordinal);

    /// <summary>
    /// Textures the host draws at run time (the squad portraits), shown by an ImageBox's <c>ImageTexture</c> under their name; not owned.
    /// </summary>
    readonly Dictionary<string, (GpuTexture Texture, SampledTexture Sampled)> _external = new(StringComparer.Ordinal);

    /// <summary>
    /// Which texels of each uploaded texture are fully opaque, by name (<see cref="OpaqueCoverage"/>); textures given on the GPU have none and never
    /// count.
    /// </summary>
    readonly Dictionary<string, OpacityMask> _masks = new(StringComparer.Ordinal);

    /// <summary>
    /// The skins' atlases went with <see cref="ReleaseTextures"/>: skin parts are drawn only from loaded atlases, so the next <see cref="Draw"/>
    /// loads them again.
    /// </summary>
    bool _skinsReleased;

    /// <summary>
    /// The fully opaque pieces of the frame being drawn.
    /// </summary>
    readonly List<UiRect> _opaqueRects = new();

    /// <summary>
    /// The fully opaque pieces of the last <see cref="Draw"/>, which <see cref="OpaqueCoverage"/> measures.
    /// </summary>
    readonly List<UiRect> _previousOpaqueRects = new();

    /// <summary>
    /// The area of <see cref="_previousOpaqueRects"/>' union, measured when asked; null until then and when they changed.
    /// </summary>
    double? _opaqueArea;

    /// <summary>
    /// The viewport size of the last <see cref="Draw"/>.
    /// </summary>
    int _coverageWidth, _coverageHeight;

    /// <summary>
    /// The DDS files being read by <see cref="Prefetch"/>, by name, until the first draw that shows one.
    /// </summary>
    readonly Dictionary<string, Task<DdsFile?>> _stored = new(StringComparer.Ordinal);

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
            _opaqueArea ??= UnionArea([.. _previousOpaqueRects]);
            return _coverageWidth > 0 && _coverageHeight > 0 ? (float)(_opaqueArea.Value / ((double)_coverageWidth * _coverageHeight)) : 0;
        }
    }

    /// <summary>
    /// Makes <paramref name="texture"/> the image an ImageBox shows for <c>ImageTexture</c> <paramref name="name"/> (the caller keeps and frees it).
    /// </summary>
    public void SetTexture(string name, GpuTexture texture, SampledTexture sampled)
    {
        if (_external.TryGetValue(name, out var given) && given.Texture == texture && given.Sampled == sampled) return;
        _external[name] = (texture, sampled);
        _textureVersion++;
    }

    /// <summary>
    /// Uploads <paramref name="image"/> (made by the host) as <c>ImageTexture</c> <paramref name="name"/>, between frames; the painter owns the copy.
    /// <paramref name="srgb"/> false stores the bytes as they are, as the game's atlas textures are (art baked from them then draws in their
    /// colours).
    /// </summary>
    public void SetImage(string name, RgbaImage image, bool srgb = true) => Upload(name, image, srgb);

    /// <summary>
    /// Frees texture <paramref name="name"/> (after the frames in flight); a later draw that names it loads it again.
    /// </summary>
    public void Forget(string name)
    {
        if (_textures.Remove(name, out var t)) t.Texture.Dispose();
        _masks.Remove(name);
        _textureVersion++;
    }

    /// <summary>
    /// Frees every texture loaded from files or images (not the fonts, nor those given with <see cref="SetTexture"/>); later draws load them again.
    /// </summary>
    public void ReleaseTextures()
    {
        foreach (var t in _textures.Values) t.Texture.Dispose();
        _textures.Clear();
        _masks.Clear();
        _skinsReleased = true;
        _textureVersion++;
    }

    /// <summary>
    /// Loads every skin's atlas again (after <see cref="ReleaseTextures"/>).
    /// </summary>
    void LoadSkinTextures()
    {
        var names = _resources.Skins.Values.Select(s => s.Texture).Where(s => s.Length > 0).Distinct();
        foreach (string name in names) LoadTexture(name);

        _skinsReleased = false;
    }

    /// <summary>
    /// Whether <c>ImageTexture</c> <paramref name="name"/> is on the GPU.
    /// </summary>
    public bool HasTexture(string name) => _textures.ContainsKey(name) || _external.ContainsKey(name);

    /// <summary>
    /// The texture <paramref name="name"/>: one given or loaded already, a rendered icon, a prefetched or S3TC DDS file uploaded as stored, else the
    /// file decoded and uploaded. Throws when the file is not found.
    /// </summary>
    (GpuTexture Texture, SampledTexture Sampled) LoadTexture(string name)
    {
        if (_external.TryGetValue(name, out var given) || _textures.TryGetValue(name, out given)) return given;
        bool icon = name.StartsWith(RenderedIconPrefix, StringComparison.Ordinal)
            || name.StartsWith(RenderedBuildingIconPrefix, StringComparison.Ordinal);
        if (icon && RenderedIcons?.Invoke(name) is { } rendered) return rendered;
        string path = _resolve(name) ?? throw new FileNotFoundException($"MyGUI texture not found: {name}");
        if (_stored.Remove(name, out var reading) && reading.Result is { } dds) return UploadStored(name, dds);
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && ReadStoredTop(path) is { } top) return UploadStored(name, top);
        return Upload(name, TextureLoader.LoadFile(path, allMips: false).Levels[0]);
    }

    /// <summary>
    /// The top level of an S3TC (BC1-BC3) DDS file as stored, read without its mips (the overview's map: 32 MB as BC1, 256 MB as RGBA8), or null for
    /// any other file (or one the reader cannot take: it is loaded decoded, where its error shows). Like <see cref="Prefetch"/>'s files it gets no
    /// <see cref="OpacityMask"/>.
    /// </summary>
    static DdsFile? ReadStoredTop(string path)
    {
        try
        {
            using var file = File.OpenHandle(Content.GameFiles.Resolve(path));
            return StoredS3tc(DdsReader.ReadTopSurface(file));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="dds"/> when it is one plain 2D S3TC (BC1-BC3) surface, which is uploaded as stored; null otherwise.
    /// </summary>
    static DdsFile? StoredS3tc(DdsFile dds) =>
        dds is { Format: DdsFormat.Bc1 or DdsFormat.Bc2 or DdsFormat.Bc3, IsCubemap: false, IsVolume: false, ImageCount: 1, Width: > 0, Height: > 0 }
            ? dds
            : null;

    /// <summary>
    /// Starts reading the S3TC (BC1-BC3) DDS files among the ImageBox textures <paramref name="names"/> on workers; the first <see cref="Draw"/> that
    /// shows one uploads its top level as stored, with no CPU decode and no <see cref="OpacityMask"/> (it never counts in <see
    /// cref="OpaqueCoverage"/>). The overview's map, an 8192 x 8192 BC1 file, decoded to 256 MB of RGBA8 inside the frame that opened the panel:
    /// about 400 ms (docs/bench.md "Play benchmark").
    /// </summary>
    public void Prefetch(IEnumerable<string> names)
    {
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            if (!name.EndsWith("GUI_Map.dds", StringComparison.OrdinalIgnoreCase)
                && !_textures.ContainsKey(name) && !_external.ContainsKey(name) && !_stored.ContainsKey(name) && _resolve(name) is { } path
                && path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                _stored[name] = Task.Run(() => ReadStored(path));
            }
        }
    }

    /// <summary>
    /// A prefetched file, when it is uploaded as stored (<see cref="StoredS3tc"/>).
    /// </summary>
    static DdsFile? ReadStored(string path)
    {
        try
        {
            return StoredS3tc(DdsReader.ReadFile(path));
        }
        catch (Exception)
        {
            // a file the reader cannot take is loaded as before, at first use (where its error shows)
            return null;
        }
    }

    /// <summary>
    /// Uploads the top level of <paramref name="dds"/> as stored (BC1, BC2 or BC3) as texture <paramref name="name"/>.
    /// </summary>
    (GpuTexture Texture, SampledTexture Sampled) UploadStored(string name, DdsFile dds)
    {
        var format = dds.Format switch
        {
            DdsFormat.Bc1 => InternalFormat.CompressedRgbaS3TcDxt1Ext,
            DdsFormat.Bc2 => InternalFormat.CompressedRgbaS3TcDxt3Ext,
            _ => InternalFormat.CompressedRgbaS3TcDxt5Ext,
        };
        using var upload = _overlay.Gpu.Uploads.Begin();
        var texture = upload.Create(new TextureDesc(format, dds.Width, dds.Height, Name: $"UI {name}"));
        upload.Write(texture, 0, 0, new PixelRect(0, 0, (uint)dds.Width, (uint)dds.Height), dds.SurfaceData(dds.Surface(0, 0)));
        _textureVersion++;
        return _textures[name] = (texture, Sampled(texture));
    }

    /// <summary>
    /// Uploads <paramref name="image"/> as texture <paramref name="name"/> (sRGB when <paramref name="srgb"/>), with its opacity mask, replacing the
    /// one of that name.
    /// </summary>
    (GpuTexture Texture, SampledTexture Sampled) Upload(string name, RgbaImage image, bool srgb = false)
    {
        // A redrawn image replaces its texture (released once the frames in flight are done).
        if (_textures.Remove(name, out var old)) old.Texture.Dispose();
        using var upload = _overlay.Gpu.Uploads.Begin();
        var desc = new TextureDesc(srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8, image.Width, image.Height, Name: $"UI {name}");
        var texture = upload.Create(desc);
        upload.Write(texture, 0, 0, new PixelRect(0, 0, (uint)image.Width, (uint)image.Height), image.Pixels);
        _masks[name] = new OpacityMask(image);
        _textureVersion++;
        return _textures[name] = (texture, Sampled(texture));
    }

    /// <summary>
    /// <paramref name="texture"/> with the linear, edge-clamped sampler the UI draws with.
    /// </summary>
    SampledTexture Sampled(GpuTexture texture) =>
        new(_overlay.Gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear)), texture.View());

    #endregion

    #region Painting

    /// <summary>
    /// The one-line edit boxes the last <see cref="Draw"/> laid out: where each one's text starts and how it was measured, for <see
    /// cref="EditCaretAt"/>.
    /// </summary>
    readonly List<(UiRect Box, DebugOverlay Font, float Scale, float X, string Text)> _editBoxes = [];

    /// <summary>
    /// The pass a <see cref="Draw"/> without one of its caller's draws into, made once and reset every frame.
    /// </summary>
    DebugOverlay.OverlayPass? _overlayPass;

    /// <summary>
    /// A caption's parsed colour tags, lines and their widths, by everything they are made from; entries not used for <see cref="TextLayoutLife"/>
    /// frames are dropped.
    /// </summary>
    readonly Dictionary<TextLayoutKey, TextLayout> _textLayouts = new();

    /// <summary>
    /// The <c>FontName</c> a skin template sets, by skin (null: none, or no such template).
    /// </summary>
    readonly Dictionary<string, string?> _templateFonts = new(StringComparer.Ordinal);

    /// <summary>
    /// The frames drawn, by which <see cref="TextLayout.Used"/> is told.
    /// </summary>
    long _textFrame;

    /// <summary>
    /// The frames a text layout lives unused; the stale ones are dropped every this many frames.
    /// </summary>
    const int TextLayoutLife = 120;

    /// <summary>
    /// What <see cref="string.ReplaceLineEndings()"/> breaks at. A text's lines and its alignment words are read without the split, query and closure
    /// per control per frame they were (docs/bench.md "Play benchmark").
    /// </summary>
    static readonly System.Buffers.SearchValues<char> LineEnds = System.Buffers.SearchValues.Create("\r\n\f\u0085\u2028\u2029");

    /// <summary>
    /// What a text layout is made from: its font, caption, width, scale and the bits of its wrap, original, focused and numeric flags.
    /// </summary>
    readonly record struct TextLayoutKey(DebugOverlay Font, string Caption, float Width, float Scale, int Mode);

    /// <summary>
    /// A caption as drawn (<see cref="LayOutText"/>): its plain text, colour runs, lines and their widths.
    /// </summary>
    sealed class TextLayout(string text, IReadOnlyList<TextColourRun> runs, IReadOnlyList<string> lines, float[] widths)
    {
        /// <summary>
        /// The plain text, its tags taken out.
        /// </summary>
        public readonly string Text = text;

        /// <summary>
        /// The text's colour runs.
        /// </summary>
        public readonly IReadOnlyList<TextColourRun> Runs = runs;

        /// <summary>
        /// The lines drawn.
        /// </summary>
        public readonly IReadOnlyList<string> Lines = lines;

        /// <summary>
        /// Each line's width in pixels.
        /// </summary>
        public readonly float[] Widths = widths;

        /// <summary>
        /// The frame (<see cref="_textFrame"/>) the layout was last used in.
        /// </summary>
        public long Used;
    }

    /// <summary>
    /// Makes the fallback faces from the game's Exo2 and Sentencia files; the textures load at first use.
    /// </summary>
    public UiPainter(DebugOverlay overlay, MyGuiResources resources, Func<string, string?> resolve)
    {
        this._overlay = overlay;
        this._resources = resources;
        this._resolve = resolve;

        // Upload before rendering starts. Missing atlas files remain an explicit diagnostic.
        try
        {
            // The fallback faces (a control whose font is not a TrueType resource): glyph-cache fonts in Meitou's look, 2x oversampled so that
            // scaled text stays smooth.
            DebugOverlay Fallback(string path, int pixels) => CreateUiOverlay(new UiFontOptions(path, pixels * 64L, 72, false, false, null));

            if (resolve(StandardFontFile) is { } standard)
            {
                _standardFont = Fallback(standard, 16);
                _standardLarge = Fallback(standard, (int)LargePixels);
            }

            if (resolve("Sentencia_1.ttf") is { } painted)
            {
                _paintedFont = Fallback(painted, 16);
                _paintedLarge = Fallback(painted, (int)LargePixels);
                _titleFont = DebugOverlay.FromFont(overlay.Gpu, painted, 144);   // a stamp of Latin capitals, baked large for the title
            }

            LoadSkinTextures();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The width in pixels of <paramref name="text"/> in the standard face at <paramref name="emPixels"/> (to fit a panel to its text before it is
    /// drawn).
    /// </summary>
    public float MeasureStandard(string text, float emPixels) => (_standardFont ?? _overlay).MeasureText(text, emPixels / 16f);

    /// <summary>
    /// Draws <paramref name="controls"/> into a <paramref name="width"/> x <paramref name="height"/> target, with the mouse at <paramref
    /// name="mouse"/> and the button of action <paramref name="active"/> pushed: into <paramref name="pass"/>, else into a pass of the painter's own.
    /// Decoded icons are uploaded first (a few a frame), and the frame's opaque pieces kept for <see cref="OpaqueCoverage"/>.
    /// </summary>
    public void Draw(IReadOnlyList<UiControl> controls, int width, int height, Vector2 mouse, string? active, float minimumTextPixels = 0,
        DebugOverlay.OverlayPass? pass = null)
    {
        for (int uploads = 0; uploads < IconUploadsPerFrame && _decodedIcons.TryDequeue(out var icon); uploads++)
            Upload(icon.Name, icon.Image);
        if (_skinsReleased) LoadSkinTextures();

        bool owns = pass is null;
        pass ??= (_overlayPass ??= new DebugOverlay.OverlayPass(_overlay.Gpu, width, height)).Reset(width, height);
        using var owned = owns ? pass : null;
        _editBoxes.Clear();
        _overlay.Flush(width, height, null, pass);   // what was queued before goes first, not into the first control's recording
        BeginPaintFrame(width, height, minimumTextPixels);

        foreach (var c in controls) PaintCached(c, width, height, mouse, active, minimumTextPixels, pass);

        _overlay.Flush(width, height, null, pass);
        if (!CollectionsMarshal.AsSpan(_opaqueRects).SequenceEqual(CollectionsMarshal.AsSpan(_previousOpaqueRects)))
        {
            _previousOpaqueRects.Clear();
            _previousOpaqueRects.AddRange(_opaqueRects);
            _opaqueArea = null;
        }

        (_coverageWidth, _coverageHeight) = (width, height);
        _opaqueRects.Clear();
        if (++_textFrame % TextLayoutLife == 0) DropStaleTextLayouts();
    }

    /// <summary>
    /// Drops the text layouts not used for <see cref="TextLayoutLife"/> frames.
    /// </summary>
    void DropStaleTextLayouts()
    {
        foreach (var (key, laid) in _textLayouts)
        {
            if (laid.Used < _textFrame - TextLayoutLife) _textLayouts.Remove(key);
        }
    }

    /// <summary>
    /// One control: its background, image, skin, the pushed button's accent and its caption.
    /// </summary>
    void PaintControl(UiControl c, int width, int height, Vector2 mouse, string? active, float minimumTextPixels, DebugOverlay.OverlayPass pass)
    {
        var b = c.Bounds;
        string widgetState = c.Properties.GetValueOrDefault("State") ?? DefaultWidgetState(c, b, mouse, active);
        var clip = (c.Clip ?? new(0, 0, width, height)).Intersect(new(0, 0, width, height));
        if (b.Intersect(clip).Width <= 0 || b.Intersect(clip).Height <= 0) return;
        if (b.Width <= 0 || b.Height <= 0) return;

        PaintBackground(c, b, clip, mouse, active, width, height, pass);
        PaintImage(c, b, clip, width, height, pass);
        PaintSkin(c, b, clip, widgetState, width, height, pass);
        if (c.Type == "Button" && c.Enabled && c.Action == active && c.ActiveColour is { } accent)
            _overlay.Rect(b.X + 4, b.Y + b.Height - 4, b.X + b.Width - 4, b.Y + b.Height - 2, accent);
        _overlay.Flush(width, height, clip, pass);
        PaintCaption(c, b, clip, widgetState, width, height, minimumTextPixels, pass);
    }

    /// <summary>
    /// The state a control without a <c>State</c> property is in: disabled, pushed (the active action), highlighted (under the mouse) or normal.
    /// </summary>
    static string DefaultWidgetState(UiControl c, UiRect b, Vector2 mouse, string? active)
    {
        if (!c.Enabled) return "disabled";
        if (active == c.Action && active is not null) return "pushed";

        return b.Contains(mouse.X, mouse.Y) && c.Action.Length > 0 ? "highlighted" : "normal";
    }

    /// <summary>
    /// A control's background colour (a button's active or hover colour, dimmed when disabled), recorded as opaque when it is.
    /// </summary>
    void PaintBackground(UiControl c, UiRect b, UiRect clip, Vector2 mouse, string? active, int width, int height, DebugOverlay.OverlayPass pass)
    {
        if (c.Background.W > 0)
        {
            var colour = c.Type == "Button" && c.Action == active ? c.ActiveColour ?? new Vector4(0.25f, 0.5f, 0.4f, c.Background.W)
                : c.Type == "Button" && b.Contains(mouse.X, mouse.Y) ? c.HoverColour ?? c.Background : c.Background;

            if (!c.Enabled) colour = c.DisabledColour ?? colour * new Vector4(0.6f, 0.6f, 0.6f, 1);
            colour.W *= Alpha(c.Properties);
            _overlay.Rect(b.X, b.Y, b.X + b.Width, b.Y + b.Height, colour);
            if (colour.W >= 1) _opaqueRects.Add(b.Intersect(clip));
        }
        _overlay.Flush(width, height, clip, pass);
    }

    /// <summary>
    /// An ImageBox's picture (rotated, nine-sliced or stretched); one whose icon is not loaded or is drawn by the renderer marks the control <see
    /// cref="_paintVolatile"/>.
    /// </summary>
    void PaintImage(UiControl c, UiRect b, UiRect clip, int width, int height, DebugOverlay.OverlayPass pass)
    {
        if (c.Type == "ImageBox" && MyGuiImages.Resolve(_resources, c.Properties) is { } shown && IsVolatileIcon(shown.Texture))
            _paintVolatile = true;

        if (c.Type == "ImageBox" && MyGuiImages.Resolve(_resources, c.Properties) is { } image && IsImageReady(image.Texture))
            DrawImageBox(c, image, b, clip, width, height, pass);
    }

    /// <summary>
    /// Whether the texture is an icon that is not loaded yet or is drawn by the renderer, so the control's picture changes without its properties.
    /// </summary>
    bool IsVolatileIcon(string texture) =>
        texture.StartsWith(RenderedIconPrefix, StringComparison.Ordinal)
        || texture.StartsWith(RenderedBuildingIconPrefix, StringComparison.Ordinal)
        || texture.StartsWith(IconPrefix, StringComparison.Ordinal) && !_textures.ContainsKey(texture);

    /// <summary>
    /// Whether an ImageBox's texture can be drawn now: a file icon only once loaded, a rendered icon once the host has made it (this asks for it).
    /// </summary>
    bool IsImageReady(string texture) =>
        (!texture.StartsWith(IconPrefix, StringComparison.Ordinal) || _textures.ContainsKey(texture))
        && (!texture.StartsWith(RenderedIconPrefix, StringComparison.Ordinal) || RenderedIconReady(texture));

    /// <summary>
    /// Draws an ImageBox's picture from <paramref name="image"/>: its source in the texture, flipped for an image the renderer drew, tinted and faded
    /// by the control's properties.
    /// </summary>
    void DrawImageBox(UiControl c, MyGuiImage image, UiRect b, UiRect clip, int width, int height, DebugOverlay.OverlayPass pass)
    {
        var loaded = LoadTexture(image.Texture);
        float textureWidth = loaded.Texture.Desc.Width, textureHeight = loaded.Texture.Desc.Height;
        var source = image.Source.Width > 0 && image.Source.Height > 0 ? image.Source : new UiRect(0, 0, textureWidth, textureHeight);

        // ImageFlipV: an image drawn by the renderer. Both backends draw it with the engine's y-up clip space; Metal's
        // targets keep row 0 at the top but its viewport maps that clip space top-down, so the stored picture is upside
        // down in both cases and the sampled V is flipped on both.
        bool flip = c.Properties.GetValueOrDefault("ImageFlipV") == "true";
        float v0 = source.Y / textureHeight, v1 = (source.Y + source.Height) / textureHeight;
        if (flip) (v0, v1) = (v1, v0);

        var tint = Colour(c.Properties.GetValueOrDefault("Colour"), Vector4.One) * new Vector4(1, 1, 1, Alpha(c.Properties));
        float border = 0;
        var invariant = CultureInfo.InvariantCulture;
        bool bordered = !flip && float.TryParse(c.Properties.GetValueOrDefault("ImageBorder"), NumberStyles.Float, invariant, out border)
            && border > 0;

        // ImageAngle: radians clockwise about the box's centre (MyGUI's RotatingSkin, the map's camera marker).
        bool rotated = float.TryParse(c.Properties.GetValueOrDefault("ImageAngle"), NumberStyles.Float, invariant, out float angle)
            && angle != 0;
        var uv = new Vector4(source.X / textureWidth, v0, (source.X + source.Width) / textureWidth, v1);
        var destination = new Vector4(b.X, b.Y, b.X + b.Width, b.Y + b.Height);
        if (rotated)
            _overlay.ImageRotated(loaded.Sampled, destination, uv, tint, angle);
        else if (bordered)
            DrawBorderedImage(c, loaded, source, b, border, tint);
        else
            _overlay.Image(loaded.Sampled, destination, uv, tint);

        if (angle == 0 && tint.W >= 1 && _masks.TryGetValue(image.Texture, out var imageMask))
            AddOpaque(imageMask, source, b, clip, allowCore: !flip && !bordered);
        _overlay.Flush(width, height, clip, pass);
    }

    /// <summary>
    /// An image cut into nine pieces by a border of <paramref name="border"/> texels: the corners keep their size (times <c>ImageBorderScale</c>),
    /// the edges and the middle stretch.
    /// </summary>
    void DrawBorderedImage(UiControl c, (GpuTexture Texture, SampledTexture Sampled) loaded, UiRect source, UiRect b, float border, Vector4 tint)
    {
        var invariant = CultureInfo.InvariantCulture;
        float borderScale = float.TryParse(c.Properties.GetValueOrDefault("ImageBorderScale"), NumberStyles.Float, invariant, out float parsed)
            ? Math.Max(0, parsed) : 1;
        float bx = Math.Min(border, source.Width / 2), by = Math.Min(border, source.Height / 2);
        float dx = Math.Min(bx * borderScale, b.Width / 2), dy = Math.Min(by * borderScale, b.Height / 2);
        Span<float> xs = [b.X, b.X + dx, b.X + b.Width - dx, b.X + b.Width];
        Span<float> ys = [b.Y, b.Y + dy, b.Y + b.Height - dy, b.Y + b.Height];
        Span<float> us = [source.X, source.X + bx, source.X + source.Width - bx, source.X + source.Width];
        Span<float> vs = [source.Y, source.Y + by, source.Y + source.Height - by, source.Y + source.Height];
        float textureWidth = loaded.Texture.Desc.Width, textureHeight = loaded.Texture.Desc.Height;

        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                var destination = new Vector4(xs[column], ys[row], xs[column + 1], ys[row + 1]);
                var uv = new Vector4(us[column] / textureWidth, vs[row] / textureHeight, us[column + 1] / textureWidth, vs[row + 1] / textureHeight);
                _overlay.Image(loaded.Sampled, destination, uv, tint);
            }
        }
    }

    /// <summary>
    /// The UI scale on a viewport <paramref name="height"/> lines high: the height over the 1080 lines the layouts' pixel sizes are made for, times
    /// <see cref="InterfaceScale"/>.
    /// </summary>
    float Scale(int height) => height / 1080f * InterfaceScale;

    /// <summary>
    /// <see cref="Scale"/> with the height's part kept within half and one and a half (the standard font's caption scale).
    /// </summary>
    float ClampedScale(int height) => Math.Clamp(height / 1080f, 0.5f, 1.5f) * InterfaceScale;

    /// <summary>
    /// A skin piece's rectangle times <paramref name="scale"/>.
    /// </summary>
    static UiRect Scaled(UiRect r, float scale) =>
        scale == 1f ? r : new UiRect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);

    /// <summary>
    /// The skin's image parts in the widget's state (MyGUI's SubSkin, MainSkin and TileRect).
    /// </summary>
    void PaintSkin(UiControl c, UiRect b, UiRect clip, string widgetState, int width, int height, DebugOverlay.OverlayPass pass)
    {
        if (_resources.Skins.TryGetValue(c.Skin, out var skin) && _textures.TryGetValue(skin.Texture, out var atlas))
        {
            var atlasMask = _masks.GetValueOrDefault(skin.Texture);

            // A toggled button (Checked) uses MyGUI's *_checked states, else looks pushed.
            bool on = c.Properties.GetValueOrDefault("Checked") == "true";
            string state = on ? widgetState + "_checked" : widgetState;
            // Meitou: a skin's pieces are fixed pixels in the files; they grow with the UI scale, so that borders and radio buttons keep their
            // size against the layout on a 4K screen (#495).
            float scale = Scale(height);
            foreach (var part in skin.Parts)
            {
                if (part.Type is not ("SubSkin" or "MainSkin" or "TileRect")) continue;
                if (!part.States.TryGetValue(state, out var source) && !(on && part.States.TryGetValue("pushed", out source))
                    && !part.States.TryGetValue("normal", out source))
                    continue;

                var dest = Scaled(part.Offset, scale).AlignTo(b, skin.Width * scale, skin.Height * scale, part.Align);
                var tint = Colour(c.Properties.GetValueOrDefault("Colour"), Vector4.One) * new Vector4(1, 1, 1, Alpha(c.Properties));
                if (part.Type == "TileRect")
                    PaintTileRect(c, part, state, atlas, atlasMask, source, dest, clip, tint, scale);
                else
                    PaintSkinPart(atlas, atlasMask, source, dest, clip, width, height, tint);
            }

            _overlay.Flush(width, height, clip, pass);
        }
    }

    /// <summary>
    /// A TileRect part: the source repeated at its tile size over <paramref name="dest"/>. A control's <c>TileSize</c> is already in screen
    /// pixels (inventory cells); the state's, or the source's, is in skin pixels and is multiplied by <paramref name="scale"/>.
    /// </summary>
    void PaintTileRect(UiControl c, MyGuiSkinPart part, string state, (GpuTexture Texture, SampledTexture Sampled) atlas, OpacityMask? atlasMask,
        UiRect source, UiRect dest, UiRect clip, Vector4 tint, float scale)
    {
        var properties = part.StateProperties?.GetValueOrDefault(state) ?? part.StateProperties?.GetValueOrDefault("normal");
        string? controlTileSize = c.Properties.GetValueOrDefault("TileSize");
        string? tileSize = controlTileSize ?? properties?.GetValueOrDefault("TileSize");
        var size = tileSize?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float tileW = size?.Length == 2 ? float.Parse(size[0], CultureInfo.InvariantCulture) : source.Width;
        float tileH = size?.Length == 2 ? float.Parse(size[1], CultureInfo.InvariantCulture) : source.Height;
        float tileScale = controlTileSize is null ? scale : 1;
        tileW *= tileScale;
        tileH *= tileScale;
        if (properties?.GetValueOrDefault("TileH") != "true") tileW = dest.Width;
        if (properties?.GetValueOrDefault("TileV") != "true") tileH = dest.Height;
        if (tileW > 0 && tileH > 0 && tint.W >= 1 && atlasMask?.Covers(source) == true) _opaqueRects.Add(dest.Intersect(clip));
        if (tileW <= 0 || tileH <= 0) return;

        float atlasWidth = atlas.Texture.Desc.Width, atlasHeight = atlas.Texture.Desc.Height;
        for (float tileY = dest.Y; tileY < dest.Y + dest.Height; tileY += tileH)
        {
            for (float tileX = dest.X; tileX < dest.X + dest.Width; tileX += tileW)
            {
                float pieceW = Math.Min(tileW, dest.X + dest.Width - tileX), pieceH = Math.Min(tileH, dest.Y + dest.Height - tileY);
                float u1 = (source.X + source.Width * pieceW / tileW) / atlasWidth, v1 = (source.Y + source.Height * pieceH / tileH) / atlasHeight;
                _overlay.Image(atlas.Sampled, new(tileX, tileY, tileX + pieceW, tileY + pieceH),
                    new(source.X / atlasWidth, source.Y / atlasHeight, u1, v1), tint);
            }
        }
    }

    /// <summary>
    /// A SubSkin or MainSkin part stretched over <paramref name="dest"/>, the image and its UVs cropped to the viewport together.
    /// </summary>
    void PaintSkinPart((GpuTexture Texture, SampledTexture Sampled) atlas, OpacityMask? atlasMask, UiRect source, UiRect dest, UiRect clip,
        int width, int height, Vector4 tint)
    {
        float x0 = Math.Max(0, dest.X), y0 = Math.Max(0, dest.Y);
        float x1 = Math.Min(width, dest.X + dest.Width), y1 = Math.Min(height, dest.Y + dest.Height);
        if (x1 <= x0 || y1 <= y0 || dest.Width <= 0 || dest.Height <= 0) return;

        float tw = atlas.Texture.Desc.Width, th = atlas.Texture.Desc.Height;
        _overlay.Image(atlas.Sampled, new(x0, y0, x1, y1), new(
            (source.X + (x0 - dest.X) / dest.Width * source.Width) / tw,
            (source.Y + (y0 - dest.Y) / dest.Height * source.Height) / th,
            (source.X + (x1 - dest.X) / dest.Width * source.Width) / tw,
            (source.Y + (y1 - dest.Y) / dest.Height * source.Height) / th), tint);
        if (tint.W >= 1 && atlasMask is not null) AddOpaque(atlasMask, source, dest, clip, allowCore: true);
    }

    /// <summary>
    /// The caption: its font and size, the edit caret and selection, then its lines.
    /// </summary>
    void PaintCaption(UiControl c, UiRect b, UiRect clip, string widgetState, int width, int height, float minimumTextPixels,
        DebugOverlay.OverlayPass pass)
    {
        bool focused = c.Properties.GetValueOrDefault("Focused") == "true";
        if (focused) _paintVolatile = true;   // the caret blinks on real time
        if (c.Caption.Length == 0 && !focused) return;
        if (SkinShowsNoCaption(c)) return;

        var textArea = UiTextLayout.ContentRect(_resources, c);
        if (textArea.Width <= 0 || textArea.Height <= 0) return;
        var textClip = clip.Intersect(textArea);
        string fontName = c.Properties.GetValueOrDefault("FontName") ?? TemplateFont(c.Skin) ?? "Kenshi_StandardFont_Medium";
        var font = CaptionFont(fontName);

        // A toggled button's caption takes the skin's *_checked colour too (the editor's selected tab is brighter).
        string textState = c.Properties.GetValueOrDefault("Checked") == "true" ? widgetState + "_checked" : widgetState;
        var textColour = MyGuiColours.Text(_resources, c, textState, c.Enabled ? DebugOverlay.TextColour : new(0.45f, 0.43f, 0.38f, 1));
        textColour.W *= Alpha(c.Properties);
        font.Target = _overlay.Target;
        float scale = c.TextScale ?? (c.Skin.Length > 0 ? ClampedScale(height) : 1);
        bool original = SizeCaptionFont(c, fontName, height, minimumTextPixels, ref font, ref scale);
        if (minimumTextPixels > 0 || original)
        {
            // Short captions keep the main UI's readable size. Centre the line box over
            // its inset client and clip to the widget instead of shrinking the lettering.
            float extra = Math.Max(0, font.LineHeight * scale - textArea.Height);
            textArea = textArea with { Y = textArea.Y - extra / 2, Height = textArea.Height + extra };
            textClip = clip.Intersect(new(textArea.X, b.Y, textArea.Width, b.Height));
        }
        else scale = Math.Min(scale, textArea.Height / font.LineHeight);
        if (scale > 1.25f && (font == _standardFont ? _standardLarge : font == _paintedFont ? _paintedLarge : null) is { } large)
        {
            scale *= font.LineHeight / large.LineHeight;
            font = large;
            font.Target = _overlay.Target;
        }

        var laid = LayOutText(c.Caption, font, textArea.Width, scale, c.Properties.GetValueOrDefault("WordWrap") == "true", original,
            focused, focused && c.Properties.GetValueOrDefault("Numeric") == "true");
        string align = c.Properties.GetValueOrDefault("TextAlign") ?? "Left VCenter";
        float y = CaptionTop(c, textArea, font, scale, laid.Lines.Count, align);
        var frame = new CaptionFrame(font, textArea, textClip, scale, textColour, width, height, pass);
        if (focused && laid.Lines.Count > 1) y = PaintWrappedCaret(c, laid, frame, y);
        else if (focused) PaintSelection(c, laid.Text, frame, y, align);
        PaintCaptionLines(c, b, clip, laid, frame, y, align);
        font.Flush(width, height, textClip, pass);
    }

    /// <summary>
    /// What one caption is painted with: its font, text box and clip, scale and colour, and the overlay pass's viewport.
    /// </summary>
    readonly record struct CaptionFrame(DebugOverlay Font, UiRect TextArea, UiRect TextClip, float Scale, Vector4 TextColour, int Width,
        int Height, DebugOverlay.OverlayPass Pass);

    /// <summary>
    /// MyGUI shows a button caption only through a text part of its skin: a button skin of image parts alone (the map's centre button, "O")
    /// shows none.
    /// </summary>
    bool SkinShowsNoCaption(UiControl c) =>
        c.Type == "Button" && _resources.Skins.TryGetValue(c.Skin, out var captionSkin) && !_resources.Templates.ContainsKey(c.Skin)
        && !captionSkin.Parts.Any(p => p.Type is "SimpleText" or "EditText");

    /// <summary>
    /// The baked font a caption's font name draws with: the title font, the painted one (painted and banner names), else the standard one.
    /// </summary>
    DebugOverlay CaptionFont(string fontName)
    {
        if (fontName == "Meitou_TitleFont") return _titleFont ?? _paintedFont ?? _overlay;
        bool painted = fontName.Contains("Painted", StringComparison.Ordinal) || fontName.Contains("Banner", StringComparison.Ordinal);
        return painted ? _paintedFont ?? _standardFont ?? _overlay : _standardFont ?? _overlay;
    }

    /// <summary>
    /// A caption with its own font size (<c>FontPixels</c>) or a font resource takes a font baked at that size (<paramref name="scale"/> 1).
    /// True when the text keeps the resource font's size as MyGUI does (the Faithful mode).
    /// </summary>
    bool SizeCaptionFont(UiControl c, string fontName, int height, float minimumTextPixels, ref DebugOverlay font, ref float scale)
    {
        bool original = false;

        // Settings request a font baked at its physical pixel size rather than scaling the 16px atlas.
        if (int.TryParse(c.Properties.GetValueOrDefault("FontPixels"), out int pixels) && StandardSource() is { } standardFace)
        {
            pixels = Math.Clamp(Math.Max(pixels, (int)MathF.Round(minimumTextPixels)), 8, 96);

            // An explicit pixel size is Meitou's own text in either mode: oversampled and freely placed.
            font = UiFontFor("", standardFace.Spec, standardFace.Path, pixels * 64L, 72, false, false);
            font.Target = _overlay.Target;
            scale = 1;
        }
        else if (ResourceFont(fontName, c.TextScale ?? Scale(height), minimumTextPixels) is { } resourceFont)
        {
            font = resourceFont;
            font.Target = _overlay.Target;
            scale = 1;

            // MyGUI never scales text to its widget: a font resource keeps its size in the Faithful mode, overflowing the line box and clipped by
            // the widget.
            original = Mode == UiMode.Faithful;
        }

        return original;
    }

    /// <summary>
    /// The first line's top: the text box aligned by <paramref name="align"/> (top, centre or bottom), scrolled by the box's bar.
    /// </summary>
    static float CaptionTop(UiControl c, UiRect textArea, DebugOverlay font, float scale, int lineCount, string align)
    {
        // An EditBox scrolled by its bar (MyGuiPresentation: TextScroll pixels); the client clips the lines.
        string? scroll = c.Properties.GetValueOrDefault("TextScroll");
        float scrolled = float.TryParse(scroll, NumberStyles.Float, CultureInfo.InvariantCulture, out float textScroll) ? textScroll : 0;
        return -scrolled + textArea.Y
            + (align.Contains("Bottom", StringComparison.Ordinal) ? Math.Max(0, textArea.Height - font.LineHeight * scale * lineCount)
                : HasWord(align, "Center", "VCenter") ? Math.Max(0, (textArea.Height - font.LineHeight * scale * lineCount) / 2)
                : 0);
    }

    /// <summary>
    /// A wrapped box being typed in: the caret on its own line, the text scrolled up to keep that line in view (no selection drawn). Returns
    /// the first line's top after the scroll.
    /// </summary>
    float PaintWrappedCaret(UiControl c, TextLayout laid, CaptionFrame frame, float y)
    {
        var (text, lines) = (laid.Text, laid.Lines);
        var (font, textArea, scale) = (frame.Font, frame.TextArea, frame.Scale);
        int caret = int.TryParse(c.Properties.GetValueOrDefault("CaretIndex"), out int at) ? Math.Clamp(at, 0, text.Length) : 0;
        var (caretLine, caretColumn) = CaretInLines(text, p => font.WrapText(p, textArea.Width, scale), caret);
        caretLine = Math.Min(caretLine, lines.Count - 1);
        caretColumn = Math.Min(caretColumn, lines[caretLine].Length);
        float lineHeight = font.LineHeight * scale;
        y -= Math.Max(0, (caretLine + 1) * lineHeight - textArea.Height);
        if ((System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 2)) % 2 == 0)
        {
            float caretX = textArea.X + font.MeasureText(lines[caretLine][..caretColumn], scale), caretY = y + caretLine * lineHeight;
            _overlay.Rect(caretX, caretY, caretX + 1, caretY + lineHeight, frame.TextColour);
        }

        _overlay.Flush(frame.Width, frame.Height, frame.TextClip, frame.Pass);
        return y;
    }

    /// <summary>
    /// A one-line box being typed in: its selection and its blinking caret.
    /// </summary>
    void PaintSelection(UiControl c, string text, CaptionFrame frame, float y, string align)
    {
        var (font, textArea, scale) = (frame.Font, frame.TextArea, frame.Scale);
        int Index(string key) => int.TryParse(c.Properties.GetValueOrDefault(key), out int at) ? Math.Clamp(at, 0, text.Length) : 0;
        int selection = Index("SelectionStart"), length = Index("SelectionLength"), caret = Index("CaretIndex");
        float textWidth = font.MeasureText(text, scale);
        float origin = LineLeft(textArea, textWidth, align);
        if (length > 0)
        {
            float start = origin + font.MeasureText(text[..selection], scale);
            float end = origin + font.MeasureText(text[..Math.Min(selection + length, text.Length)], scale);
            _overlay.Rect(start, y, end, y + font.LineHeight * scale, new(0.25f, 0.4f, 0.45f, 0.8f));
        }

        if ((System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 2)) % 2 == 0)
        {
            float caretX = origin + font.MeasureText(text[..caret], scale);
            _overlay.Rect(caretX, y, caretX + 1, y + font.LineHeight * scale, frame.TextColour);
        }

        _overlay.Flush(frame.Width, frame.Height, frame.TextClip, frame.Pass);
    }

    /// <summary>
    /// A line's left edge in the text box, aligned by <paramref name="align"/> (left, centre or right).
    /// </summary>
    static float LineLeft(UiRect textArea, float textWidth, string align) =>
        textArea.X + (align.Contains("Right", StringComparison.Ordinal) ? textArea.Width - textWidth
            : HasWord(align, "Center", "HCenter") ? (textArea.Width - textWidth) / 2
            : 0);

    /// <summary>
    /// The caption's lines, those in the clip drawn (shadowed when asked, in their colour runs); a one-line edit box's line is kept for the
    /// text input.
    /// </summary>
    void PaintCaptionLines(UiControl c, UiRect b, UiRect clip, TextLayout laid, CaptionFrame frame, float y, string align)
    {
        var (text, runs, lines) = (laid.Text, laid.Runs, laid.Lines);
        var (font, scale, textColour) = (frame.Font, frame.Scale, frame.TextColour);
        int cursor = 0;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            string line = lines[lineIndex];
            float textWidth = laid.Widths[lineIndex];
            float x = LineLeft(frame.TextArea, textWidth, align);
            if (c.Type == "EditBox" && lines.Count == 1) _editBoxes.Add(new(clip.Intersect(b), font, scale, x, line));
            if (new UiRect(x, y, textWidth, font.LineHeight * scale).Intersect(frame.TextClip).Width > 0)
            {
                if (c.Properties.GetValueOrDefault("TextShadow") == "true") font.TextScaled(line, x + 1, y + 1, new(0, 0, 0, textColour.W), scale);
                if (runs.Count == 0) font.TextScaled(line, x, y, textColour, scale);
                else DrawRuns(font, text, line, ref cursor, runs, x, y, textColour, scale);
            }
            else if (runs.Count > 0 && text.IndexOf(line, cursor, StringComparison.Ordinal) is >= 0 and int at) cursor = at + line.Length;
            y += font.LineHeight * scale;
        }
    }

    /// <summary>
    /// <paramref name="caption"/> as drawn: its plain text and colour runs (MyGUI's inline tags; an edited box shows its text as typed), its lines
    /// (wrapped, or one per line break and shortened with "..." to <paramref name="width"/>; a font resource's long line in the Faithful mode is
    /// cropped by the widget instead, as MyGUI does) and their widths. Made once per caption, font, width and scale, not every frame.
    /// </summary>
    TextLayout LayOutText(string caption, DebugOverlay font, float width, float scale, bool wrap, bool original, bool focused, bool numeric)
    {
        var key = new TextLayoutKey(font, caption, width, scale, (wrap ? 1 : 0) | (original ? 2 : 0) | (focused ? 4 : 0) | (numeric ? 8 : 0));
        if (!_textLayouts.TryGetValue(key, out var laid))
        {
            string text = caption.Replace("\\n", "\n", StringComparison.Ordinal);
            IReadOnlyList<TextColourRun> runs = [];
            if (!focused) (text, runs) = MyGuiTextTags.Parse(text);
            IReadOnlyList<string> lines = numeric ? [text] : wrap ? font.WrapText(text, width, scale) : FitLines(text, original, font, width, scale);
            var widths = new float[lines.Count];
            for (int i = 0; i < widths.Length; i++) widths[i] = font.MeasureText(lines[i], scale);
            _textLayouts[key] = laid = new(text, runs, lines, widths);
        }
        laid.Used = _textFrame;
        return laid;
    }

    /// <summary>
    /// The <c>FontName</c> skin template <paramref name="skin"/> sets, looked up once (null: none, or no such template).
    /// </summary>
    string? TemplateFont(string skin)
    {
        if (!_templateFonts.TryGetValue(skin, out var font))
        {
            _templateFonts[skin] = font = _resources.Templates.TryGetValue(skin, out var template)
                ? template.Descendants("Property").FirstOrDefault(p => (string?)p.Attribute("key") == "FontName")?.Attribute("value")?.Value : null;
        }

        return font;
    }

    /// <summary>
    /// The lines of <paramref name="text"/>: one per line break, each shortened with "..." to <paramref name="width"/> unless <paramref
    /// name="original"/> (the widget crops it).
    /// </summary>
    static string[] FitLines(string text, bool original, DebugOverlay font, float width, float scale)
    {
        var lines = text.AsSpan().IndexOfAny(LineEnds) >= 0 ? text.ReplaceLineEndings("\n").Split('\n') : [text];
        if (!original) for (int i = 0; i < lines.Length; i++) lines[i] = font.FitText(lines[i], width, scale);
        return lines;
    }

    /// <summary>
    /// Draws <paramref name="line"/> of the plain <paramref name="text"/> in its tags' colours (<see cref="MyGuiTextTags"/>; before the first tag
    /// <paramref name="colour"/>, the alpha always <paramref name="colour"/>'s). <paramref name="cursor"/> is where the line is looked for in the
    /// text, then past it; a line not found there (shortened) starts at it.
    /// </summary>
    static void DrawRuns(DebugOverlay font, string text, string line, ref int cursor, IReadOnlyList<TextColourRun> runs, float x, float y,
        Vector4 colour, float scale)
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
            if (caret > start + paragraph.Length && p < paragraphs.Length - 1)
            {
                line += wrapped.Count;
                start += paragraph.Length + 1;
                continue;
            }

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

    /// <summary>
    /// Whether the space-separated <paramref name="align"/> holds the word <paramref name="a"/> or <paramref name="b"/>.
    /// </summary>
    static bool HasWord(string align, string a, string b)
    {
        var text = align.AsSpan();
        foreach (var range in text.Split(' '))
            if (text[range] is var word && (word.SequenceEqual(a) || word.SequenceEqual(b))) return true;
        return false;
    }

    /// <summary>
    /// A control's <c>Alpha</c> property, clamped to 0..1; 1 when it is missing or not a number.
    /// </summary>
    static float Alpha(IReadOnlyDictionary<string, string> properties) =>
        float.TryParse(properties.GetValueOrDefault("Alpha"), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? Math.Clamp(value, 0, 1)
            : 1;

    /// <summary>
    /// A colour written as three or four numbers (RGB, alpha 1 when missing); <paramref name="fallback"/> when it is missing or not one.
    /// </summary>
    static Vector4 Colour(string? value, Vector4 fallback)
    {
        if (value is null) return fallback;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return fallback;
        var numbers = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) return fallback;
        }
        return new(numbers[0], numbers[1], numbers[2], parts.Length == 4 ? numbers[3] : 1);
    }

    /// <summary>
    /// The text index nearest <paramref name="p"/> in the one-line edit box under it as the last <see cref="Draw"/> laid it out (the gap between two
    /// characters closest to the pointer); null when no edit box was drawn there. The screen host places the caret with it.
    /// </summary>
    public int? EditCaretAt(Vector2 p)
    {
        for (int i = _editBoxes.Count - 1; i >= 0; i--)
        {
            var (box, font, scale, x, text) = _editBoxes[i];
            if (!box.Contains(p.X, p.Y)) continue;
            int best = 0;
            float distance = MathF.Abs(p.X - x);
            for (int k = 1; k <= text.Length; k++)
            {
                if (MathF.Abs(p.X - (x + font.MeasureText(text[..k], scale))) is var d && d < distance)
                {
                    best = k;
                    distance = d;
                }
            }

            return best;
        }
        return null;
    }

    #endregion

    #region Opacity

    /// <summary>
    /// Records the part of <paramref name="dest"/> that is opaque: all of it when every texel of <paramref name="source"/> is, else (when <paramref
    /// name="allowCore"/>) the stretched image of the slice's largest opaque core, so ragged panel edges do not disqualify the solid middle.
    /// </summary>
    void AddOpaque(OpacityMask mask, UiRect source, UiRect dest, UiRect clip, bool allowCore)
    {
        if (mask.Covers(source))
        {
            _opaqueRects.Add(dest.Intersect(clip));
            return;
        }

        if (!allowCore || source.Width <= 0 || source.Height <= 0 || mask.Core(source) is not { } core) return;
        float fx = dest.Width / source.Width, fy = dest.Height / source.Height;
        _opaqueRects.Add(new UiRect(dest.X + (core.X - source.X) * fx, dest.Y + (core.Y - source.Y) * fy, core.Width * fx, core.Height * fy)
            .Intersect(clip));
    }

    /// <summary>
    /// The area of the union of <paramref name="rects"/> (overlaps once): the cells of the grid their edges make, each in or out.
    /// </summary>
    internal static double UnionArea(List<UiRect> rects)
    {
        rects.RemoveAll(r => r.Width <= 0 || r.Height <= 0);
        if (rects.Count == 0) return 0;
        var xs = rects.SelectMany(r => new[] { r.X, r.X + r.Width }).Distinct().Order().ToArray();
        var ys = rects.SelectMany(r => new[] { r.Y, r.Y + r.Height }).Distinct().Order().ToArray();
        var covered = new bool[(xs.Length - 1) * (ys.Length - 1)];
        foreach (var r in rects)
        {
            int x0 = Array.BinarySearch(xs, r.X), x1 = Array.BinarySearch(xs, r.X + r.Width);
            int y0 = Array.BinarySearch(ys, r.Y), y1 = Array.BinarySearch(ys, r.Y + r.Height);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++) covered[y * (xs.Length - 1) + x] = true;
            }
        }

        double area = 0;
        for (int y = 0; y + 1 < ys.Length; y++)
        {
            for (int x = 0; x + 1 < xs.Length; x++)
                if (covered[y * (xs.Length - 1) + x]) area += (double)(xs[x + 1] - xs[x]) * (ys[y + 1] - ys[y]);
        }

        return area;
    }

    /// <summary>
    /// One bit per texel of an uploaded texture: alpha 255. Answers whether a source rectangle (texels) is opaque throughout, remembering each
    /// rectangle asked.
    /// </summary>
    sealed class OpacityMask
    {
        /// <summary>
        /// One bit per texel, alpha 255; texel i is bit i % 64 of word i / 64.
        /// </summary>
        readonly ulong[] _bits;

        /// <summary>
        /// The texture's size in texels.
        /// </summary>
        readonly int _width, _height;

        /// <summary>
        /// Whether each texel rectangle asked is opaque throughout.
        /// </summary>
        readonly Dictionary<(int X0, int Y0, int X1, int Y1), bool> _answers = new();

        /// <summary>
        /// The largest opaque core of each texel rectangle asked (null: none).
        /// </summary>
        readonly Dictionary<(int X0, int Y0, int X1, int Y1), UiRect?> _cores = new();

        /// <summary>
        /// The mask of <paramref name="image"/>'s fully opaque texels.
        /// </summary>
        public OpacityMask(RgbaImage image)
        {
            (_width, _height) = (image.Width, image.Height);
            _bits = new ulong[(_width * _height + 63) / 64];
            var pixels = image.Pixels;
            for (int i = 0, n = _width * _height; i < n; i++)
                if (pixels[i * 4 + 3] == 255) _bits[i >> 6] |= 1UL << (i & 63);
        }

        /// <summary>
        /// The texel rectangle <paramref name="source"/> covers, clamped to the texture: the key its answers are remembered by.
        /// </summary>
        (int X0, int Y0, int X1, int Y1) KeyOf(UiRect source) =>
            (Math.Max(0, (int)MathF.Floor(source.X)), Math.Max(0, (int)MathF.Floor(source.Y)),
                Math.Min(_width, (int)MathF.Ceiling(source.X + source.Width)), Math.Min(_height, (int)MathF.Ceiling(source.Y + source.Height)));

        /// <summary>
        /// Whether every texel of <paramref name="source"/> is opaque (false for an empty rectangle).
        /// </summary>
        public bool Covers(UiRect source)
        {
            var key = KeyOf(source);
            if (_answers.TryGetValue(key, out bool known)) return known;
            bool opaque = key.X1 > key.X0 && key.Y1 > key.Y0;
            for (int y = key.Y0; opaque && y < key.Y1; y++)
            {
                for (int x = key.X0; x < key.X1; x++)
                {
                    int i = y * _width + x;
                    if ((_bits[i >> 6] & 1UL << (i & 63)) == 0)
                    {
                        opaque = false;
                        break;
                    }
                }
            }

            return _answers[key] = opaque;
        }

        /// <summary>
        /// The largest rectangle of opaque texels inside <paramref name="source"/>, inset one texel so bilinear taps never reach a translucent
        /// neighbour; null when none is left.
        /// </summary>
        public UiRect? Core(UiRect source)
        {
            var key = KeyOf(source);
            if (_cores.TryGetValue(key, out var known)) return known;
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
                        int i = (key.Y0 + row) * _width + key.X0 + x;
                        heights[x] = (_bits[i >> 6] & 1UL << (i & 63)) != 0 ? heights[x] + 1 : 0;
                    }
                    stack.Clear();
                    for (int x = 0; x <= w; x++)
                    {
                        int at = x == w ? 0 : heights[x];
                        while (stack.Count > 0 && heights[stack.Peek()] >= at)
                        {
                            int top = heights[stack.Pop()], left = stack.Count > 0 ? stack.Peek() + 1 : 0;
                            long area = (long)top * (x - left);
                            if (area > bestArea)
                            {
                                bestArea = area;
                                best = new UiRect(key.X0 + left, key.Y0 + row - top + 1, x - left, top);
                            }
                        }
                        stack.Push(x);
                    }
                }
                if (best is { } found)
                    best = found.Width > 2 && found.Height > 2 ? new UiRect(found.X + 1, found.Y + 1, found.Width - 2, found.Height - 2) : null;
            }
            return _cores[key] = best;
        }
    }

    #endregion

    #region Icons

    /// <summary>
    /// The start of a <see cref="RenderedIconTexture"/> name.
    /// </summary>
    public const string RenderedIconPrefix = "meitou:icon:";

    /// <summary>
    /// The start of a <see cref="RenderedBuildingIconTexture"/> name.
    /// </summary>
    public const string RenderedBuildingIconPrefix = "meitou:building-icon:";

    /// <summary>
    /// The start of an <see cref="IconTexture"/> name.
    /// </summary>
    const string IconPrefix = "file:";

    /// <summary>
    /// The most decoded icons <see cref="Draw"/> uploads in a frame.
    /// </summary>
    const int IconUploadsPerFrame = 4;

    /// <summary>
    /// The icons asked for with <see cref="IconReady"/>, by name.
    /// </summary>
    readonly HashSet<string> _requestedIcons = new(StringComparer.Ordinal);

    /// <summary>
    /// The icons decoded on workers, waiting for <see cref="Draw"/> to upload them.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentQueue<(string Name, RgbaImage Image)> _decodedIcons = new();

    /// <summary>
    /// The icons whose file could not be decoded, by name.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _failedIcons = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the image file <paramref name="path"/> (an item icon) is on the GPU and can be shown as an ImageBox's <c>ImageTexture</c> of <see
    /// cref="IconTexture"/>. The first ask starts decoding it on a worker, <see cref="Draw"/> uploads it when done; a file that cannot be read stays
    /// false.
    /// </summary>
    public bool IconReady(string path)
    {
        string name = IconTexture(path);
        if (_textures.ContainsKey(name)) return true;
        if (_requestedIcons.Add(name))
        {
            Task.Run(() =>
            {
                try
                {
                    _decodedIcons.Enqueue((name, TextureLoader.LoadFile(path, allMips: false).Levels[0]));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
                {
                    _failedIcons.TryAdd(name, true);
                }
            });
        }

        return false;
    }

    /// <summary>
    /// Icon files asked for (<see cref="IconReady"/>) that are still decoding or waiting for their upload.
    /// </summary>
    public int PendingIconFiles => _requestedIcons.Count(n => !_textures.ContainsKey(n) && !_failedIcons.ContainsKey(n));

    /// <summary>
    /// The <c>ImageTexture</c> name of the icon the renderer draws from an item's mesh (<see cref="RenderedIcons"/>), shown once it exists; flip it
    /// (<c>ImageFlipV</c>), as the renderer's targets are upside down.
    /// </summary>
    public static string RenderedIconTexture(string record, string material, string colour = "") =>
        RenderedIconPrefix + record + "|" + material + "|" + colour;

    /// <summary>
    /// The <c>ImageTexture</c> name of the picture the renderer draws of a BUILDING (<see cref="RenderedIcons"/> looks it up too); flip it like <see
    /// cref="RenderedIconTexture"/>.
    /// </summary>
    public static string RenderedBuildingIconTexture(string record) => RenderedBuildingIconPrefix + record;

    /// <summary>
    /// The record of a <see cref="RenderedBuildingIconTexture"/> name.
    /// </summary>
    public static bool TryParseRenderedBuildingIcon(string name, out string record)
    {
        record = name.StartsWith(RenderedBuildingIconPrefix, StringComparison.Ordinal) ? name[RenderedBuildingIconPrefix.Length..] : "";
        return record.Length > 0;
    }

    /// <summary>
    /// The record, material and colour of a <see cref="RenderedIconTexture"/> name.
    /// </summary>
    public static bool TryParseRenderedIcon(string name, out string record, out string material, out string colour)
    {
        (record, material, colour) = ("", "", "");
        if (!name.StartsWith(RenderedIconPrefix, StringComparison.Ordinal) || name.Split('|') is not [var r, var m, var c]) return false;
        (record, material, colour) = (r[RenderedIconPrefix.Length..], m, c);
        return record.Length > 0;
    }

    /// <summary>
    /// Looks up the texture of a <see cref="RenderedIconTexture"/> or <see cref="RenderedBuildingIconTexture"/> name, asking for it to be made when
    /// it is not (the host sets this; null: no icons are drawn).
    /// </summary>
    public Func<string, (GpuTexture Texture, SampledTexture Sampled)?>? RenderedIcons { get; set; }

    /// <summary>
    /// Whether the rendered icon <paramref name="name"/> can be drawn now (this asks for it too).
    /// </summary>
    public bool RenderedIconReady(string name) => RenderedIcons?.Invoke(name) is not null;

    /// <summary>
    /// The <c>ImageTexture</c> name under which the image file <paramref name="path"/> is shown once <see cref="IconReady"/>.
    /// </summary>
    public static string IconTexture(string path) => IconPrefix + path;

    #endregion

    #region Pointers

    /// <summary>
    /// The mouse pointers looked up, by name (null: unknown).
    /// </summary>
    readonly Dictionary<string, MyGuiPointer?> _pointers = new(StringComparer.Ordinal);

    /// <summary>
    /// The pointers' pictures made by <see cref="PointerImage"/>, by name (null: unknown or unreadable).
    /// </summary>
    readonly Dictionary<string, (RgbaImage Image, int HotX, int HotY)?> _pointerImages = new(StringComparer.Ordinal);

    /// <summary>
    /// Draws the mouse pointer <paramref name="name"/> (kenshi_pointers.xml) with its hot spot on <paramref name="mouse"/>, in screen pixels as MyGUI
    /// does; nothing for an unknown name. Called last in a frame: the original's <c>Pointer</c> layer is above every other.
    /// </summary>
    public void DrawPointer(string name, Vector2 mouse, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        if (Pointer(name) is { } pointer) DrawPointerImage(pointer, pointer.At(MathF.Round(mouse.X), MathF.Round(mouse.Y)), width, height, pass);
    }

    /// <summary>
    /// Draws the pointer <paramref name="name"/> with the middle of its left edge on <paramref name="leftMiddle"/>, beside a cursor the window system
    /// draws (the enhanced Hardware mouse); nothing for an unknown name.
    /// </summary>
    public void DrawPointerBeside(string name, Vector2 leftMiddle, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        if (Pointer(name) is { } pointer)
        {
            DrawPointerImage(pointer, new(MathF.Round(leftMiddle.X), MathF.Round(leftMiddle.Y - pointer.Height / 2), pointer.Width, pointer.Height),
                width, height, pass);
        }
    }

    /// <summary>
    /// The mouse pointer <paramref name="name"/>, looked up once; null for an unknown name.
    /// </summary>
    MyGuiPointer? Pointer(string name)
    {
        if (!_pointers.TryGetValue(name, out var pointer)) _pointers[name] = pointer = MyGuiPointers.Resolve(_resources, name);
        return pointer;
    }

    /// <summary>
    /// Draws <paramref name="pointer"/>'s image into <paramref name="at"/>, in a batch of its own.
    /// </summary>
    void DrawPointerImage(MyGuiPointer pointer, UiRect at, int width, int height, DebugOverlay.OverlayPass? pass)
    {
        _overlay.Flush(width, height, null, pass);   // whatever was queued before (another texture, text) goes first
        var loaded = LoadTexture(pointer.Image.Texture);
        var source = pointer.Image.Source;
        float w = loaded.Texture.Desc.Width, h = loaded.Texture.Desc.Height;
        _overlay.Image(loaded.Sampled, new(at.X, at.Y, at.X + at.Width, at.Y + at.Height),
            new(source.X / w, source.Y / h, (source.X + source.Width) / w, (source.Y + source.Height) / h), Vector4.One);
        _overlay.Flush(width, height, null, pass);
    }

    /// <summary>
    /// The pointer <paramref name="name"/> as a picture of its own at the size it is drawn (<see cref="DrawPointer"/>'s), with its hot spot, for a
    /// cursor the window system draws (frame generation: FrameGenerationSetup); null for an unknown name or an unreadable texture.
    /// </summary>
    public (RgbaImage Image, int HotX, int HotY)? PointerImage(string name)
    {
        if (_pointerImages.TryGetValue(name, out var cached)) return cached;
        var pointer = Pointer(name);
        RgbaImage? sheet = null;
        try
        {
            if (pointer is not null && _resolve(pointer.Image.Texture) is { } path) sheet = TextureLoader.LoadFile(path, allMips: false).Levels[0];
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
        }

        var made = pointer is null || sheet is null ? ((RgbaImage, int, int)?)null
            : (Crop(sheet, pointer.Image.Source, (int)MathF.Round(pointer.Width), (int)MathF.Round(pointer.Height)), (int)MathF.Round(pointer.PointX),
                (int)MathF.Round(pointer.PointY));
        return _pointerImages[name] = made;
    }

    /// <summary>
    /// The <paramref name="source"/> part of <paramref name="sheet"/> scaled to <paramref name="width"/> × <paramref name="height"/> (nearest texel).
    /// </summary>
    static RgbaImage Crop(RgbaImage sheet, UiRect source, int width, int height)
    {
        var image = new RgbaImage(Math.Max(width, 1), Math.Max(height, 1));
        for (int y = 0; y < image.Height; y++)
        {
            int sy = Math.Clamp((int)(source.Y + (y + 0.5f) * source.Height / image.Height), 0, sheet.Height - 1);
            for (int x = 0; x < image.Width; x++)
            {
                int sx = Math.Clamp((int)(source.X + (x + 0.5f) * source.Width / image.Width), 0, sheet.Width - 1);
                Array.Copy(sheet.Pixels, (sy * sheet.Width + sx) * 4, image.Pixels, (y * image.Width + x) * 4, 4);
            }
        }
        return image;
    }

    #endregion

    /// <summary>
    /// Frees the textures and the fonts.
    /// </summary>
    public void Dispose()
    {
        ReleaseTextures();
        _stored.Clear();
        foreach (var font in _uiFonts.Values) font.Dispose();
        _uiFonts.Clear();
        _standardFont?.Dispose(); _paintedFont?.Dispose(); _titleFont?.Dispose(); _standardLarge?.Dispose(); _paintedLarge?.Dispose();
        _standardFont = null; _paintedFont = null; _titleFont = null; _standardLarge = null; _paintedLarge = null;
    }
}