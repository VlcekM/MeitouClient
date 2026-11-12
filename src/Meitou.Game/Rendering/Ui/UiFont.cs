using FreeTypeSharp;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;
using static FreeTypeSharp.FT;

namespace Meitou.Game.Rendering.Ui;

/// <summary>
/// How a <see cref="UiFont"/> is made (docs/formats/mygui.md "Fonts").
/// </summary>
/// <param name="Path">The font file.</param>
/// <param name="Size64">The FreeType character size in 26.6 points at <paramref name="Dpi"/>; pixels at 72 dpi for sizes given in pixels.</param>
/// <param name="Faithful">
/// MyGUI's own rendering: the font's hinting mode (native bytecode), the <c>Codes</c> ranges, the substitute glyph, MyGUI's line metrics and
/// whole-pixel placement. Otherwise the Meitou look: any glyph the face has, light hinting or 2x oversampling, the 1.25 em line.
/// </param>
/// <param name="Snapped">Meitou only: glyphs placed on whole pixels (hinted at 1x) instead of oversampled at 2x and placed freely.</param>
/// <param name="Resource">The <c>ResourceTrueTypeFont</c> the font comes from; null: its defaults.</param>
internal sealed record UiFontOptions(string Path, long Size64, uint Dpi, bool Faithful, bool Snapped, MyGuiTrueTypeFont? Resource);

/// <summary>
/// A UI font at one size: FreeType glyphs rasterised on demand into one R8 atlas that grows (to 2048 square) and, when that is full, starts over.
/// Layout metrics come from the same glyph records the atlas is drawn with, so measuring and drawing agree for every script. Render thread
/// only. <see cref="Generation"/> changes whenever glyph rectangles move (growth, restart): anything holding atlas coordinates must drop them.
/// </summary>
internal sealed unsafe class UiFont : IDisposable
{
    /// <summary>
    /// One glyph: layout in output pixels (offsets from the pen and the baseline, quad size), and its rectangle in the atlas in texels.
    /// </summary>
    internal readonly record struct Glyph(float Advance, float OffsetX, float OffsetY, float Width, float Height, ushort X, ushort Y, ushort PixelWidth,
        ushort PixelHeight);

    /// <summary>
    /// The largest atlas side in texels; a full atlas of this size starts over.
    /// </summary>
    const int MaxSide = 2048;

    /// <summary>
    /// The device the atlas texture is made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The font file's shared face.
    /// </summary>
    readonly FreeTypeFace _face;

    /// <summary>
    /// This font's FreeType size on the face (activated before each glyph is loaded).
    /// </summary>
    readonly FT_SizeRec_* _size;

    /// <summary>
    /// Whether the font draws in MyGUI's own way (<see cref="UiFontOptions.Faithful"/>).
    /// </summary>
    readonly bool _faithful;

    /// <summary>
    /// How many times larger glyphs are rasterised than drawn: 2 for oversampled Meitou fonts, else 1.
    /// </summary>
    readonly int _oversample;

    /// <summary>
    /// The FreeType load flags (the hinting); weakened after repeated hinting failures (<see cref="LoadGlyph"/>).
    /// </summary>
    FT_LOAD _loadFlags;

    /// <summary>
    /// How many glyph loads failed with the hinting asked for so far.
    /// </summary>
    int _hintingFailures;

    /// <summary>
    /// The code points the font resource allows (Faithful only; null: any).
    /// </summary>
    readonly MyGuiCodeRanges? _ranges;

    /// <summary>
    /// The resource's substitute code point (-1: none), its height offset (Faithful only) and the texels kept free around each glyph.
    /// </summary>
    readonly int _substituteCode, _offsetHeight, _pad;

    /// <summary>
    /// The resource's tab width (0: eight spaces), and the scale from font units to output pixels for kerning (0 in Faithful mode).
    /// </summary>
    readonly float _tabWidth, _kerningScale;

    /// <summary>
    /// The glyphs made, by code point.
    /// </summary>
    readonly Dictionary<int, Glyph> _glyphs = [];

    /// <summary>
    /// The glyphs rasterised, by glyph index (code points that resolve to one glyph share it).
    /// </summary>
    readonly Dictionary<uint, Glyph> _byIndex = [];

    /// <summary>
    /// The atlas's coverage texels, <see cref="Side"/> x <see cref="Side"/>.
    /// </summary>
    byte[] _pixels;

    /// <summary>
    /// The buffers a glyph's bitmap and an atlas upload are copied through (grown as needed).
    /// </summary>
    byte[] _scratch = new byte[1024], _uploadScratch = new byte[1024];

    /// <summary>
    /// Where the next glyph is packed in the atlas, and the height of the row being filled.
    /// </summary>
    int _cursorX, _cursorY, _rowHeight;

    /// <summary>
    /// The atlas rectangle changed since the last upload.
    /// </summary>
    int _dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1;

    /// <summary>
    /// Whether the atlas has changes not uploaded yet.
    /// </summary>
    bool _dirty;

    /// <summary>
    /// The atlas on the GPU (null until <see cref="Prepare"/> and after it is retired).
    /// </summary>
    GpuTexture? _texture;

    /// <summary>
    /// The atlas with the sampler it is drawn with.
    /// </summary>
    SampledTexture _sampled;

    /// <summary>
    /// The tab's advance, measured on first use (-1: not yet).
    /// </summary>
    float _tab = -1;

    /// <summary>
    /// Whether the font was disposed.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// The atlas side in texels.
    /// </summary>
    public int Side { get; private set; }

    /// <summary>
    /// Raised whenever glyph rectangles move (the atlas grew or started over).
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Counts every font's <see cref="Generation"/> changes and atlas releases (render thread): recorded glyph quads are stale once it moves.
    /// </summary>
    public static int Changes { get; private set; }

    /// <summary>
    /// Glyphs are placed on whole pixels (Faithful, and Meitou fonts the original draws with <c>Antialias</c> false).
    /// </summary>
    public bool Snapped { get; }

    /// <summary>
    /// The line's height in pixels.
    /// </summary>
    public float LineHeight { get; }

    /// <summary>
    /// The distance from the line's top to the baseline, in pixels.
    /// </summary>
    public float Baseline { get; }

    /// <summary>
    /// The advance of 'M' in pixels.
    /// </summary>
    public float CharWidth { get; }

    /// <summary>
    /// Opens the face at the size, hinting and line metrics <paramref name="options"/> asks for and makes the usual glyphs.
    /// </summary>
    public UiFont(IGpuContext gpu, UiFontOptions options)
    {
        this._gpu = gpu;
        _faithful = options.Faithful;
        Snapped = _faithful || options.Snapped;
        var resource = options.Resource;
        _oversample = !_faithful && !Snapped ? 2 : 1;
        _ranges = _faithful ? resource?.Codes ?? MyGuiCodeRanges.Bmp : null;
        _substituteCode = resource?.SubstituteCode ?? -1;
        _offsetHeight = _faithful ? resource?.OffsetHeight ?? 0 : 0;
        _tabWidth = resource?.TabWidth ?? 0;
        _pad = Math.Max(1, _faithful ? resource?.Distance ?? 1 : 1) * _oversample;
        var hinting = resource?.Hinting ?? MyGuiHinting.UseNative;
        _loadFlags = !_faithful ? Snapped ? (FT_LOAD)FT_LOAD_TARGET_LIGHT : FT_LOAD.FT_LOAD_NO_HINTING : hinting switch
        {
            MyGuiHinting.ForceAuto => FT_LOAD.FT_LOAD_FORCE_AUTOHINT,
            MyGuiHinting.DisableAuto => FT_LOAD.FT_LOAD_NO_AUTOHINT,
            MyGuiHinting.DisableAll => FT_LOAD.FT_LOAD_NO_HINTING,
            _ => FT_LOAD.FT_LOAD_DEFAULT,
        };
        _face = FreeTypeFace.Open(options.Path);
        float ppem = options.Size64 / 64f * options.Dpi / 72f;
        lock (FreeTypeFace.Gate)
        {
            FT_SizeRec_* created;
            Check(FT_New_Size(_face.Face, &created), "FT_New_Size");
            _size = created;
            Check(FT_Activate_Size(_size), "FT_Activate_Size");
            if (_face.Scalable)
                Check(FT_Set_Char_Size(_face.Face, (nint)(options.Size64 * _oversample), 0, options.Dpi, options.Dpi), "FT_Set_Char_Size");
            else SelectStrike(ppem * _oversample);
            var metrics = _face.Face->size->metrics;
            if (_faithful)
            {
                // MyGUI: the grid-fitted ascent and descent, raised to the OS/2 Windows and typographic values at the pixel size.
                int ascent = (int)(metrics.ascender >> 6), descent = (int)((-metrics.descender) >> 6);
                if (_face.Os2 is { } os2 && _face.UnitsPerEm > 0)
                {
                    int yppem = metrics.y_ppem, upem = _face.UnitsPerEm;
                    ascent = Math.Max(ascent, os2.WinAscent * yppem / upem);
                    descent = Math.Max(descent, os2.WinDescent * yppem / upem);
                    ascent = Math.Max(ascent, os2.TypoAscender * yppem / upem);
                    descent = Math.Max(descent, -os2.TypoDescender * yppem / upem);
                }
                LineHeight = ascent + descent;
                Baseline = ascent;
            }
            else
            {
                float em = ppem / Math.Max(1, _face.UnitsPerEm);
                LineHeight = ppem * 1.25f;
                Baseline = (LineHeight - (_face.Ascender - _face.Descender) * em) / 2 + _face.Ascender * em;
            }
            _kerningScale = _faithful ? 0 : ppem / Math.Max(1, _face.UnitsPerEm);
        }
        Side = ppem * _oversample <= 48 ? 512 : ppem * _oversample <= 96 ? 1024 : MaxSide;
        _pixels = new byte[Side * Side];
        (_cursorX, _cursorY) = (_pad, _pad);
        PrimeGlyphs();
        CharWidth = Lookup('M').Advance;
    }

    /// <summary>
    /// Like MyGUI, which builds every glyph of its ranges at load: the usual ones now (not while a frame draws them), the rest on demand.
    /// </summary>
    void PrimeGlyphs()
    {
        if (_ranges is not null)
        {
            if (_ranges.Count <= 1024)
            {
                foreach (int cp in _ranges.CodePoints()) Lookup(cp);
            }

            return;
        }

        for (int cp = 32; cp < 127; cp++) Lookup(cp);
        for (int cp = 0xA0; cp < 0x100; cp++) Lookup(cp);
    }

    /// <summary>
    /// Throws when the FreeType call <paramref name="call"/> failed.
    /// </summary>
    static void Check(FT_Error error, string call)
    {
        if (error != FT_Error.FT_Err_Ok)
        {
            throw new InvalidOperationException($"{call} failed ({error}).");
        }
    }

    /// <summary>
    /// A bitmap-only font: the smallest strike at least as large as <paramref name="ppem"/>, else the largest.
    /// </summary>
    void SelectStrike(float ppem)
    {
        var f = _face.Face;
        if (f->num_fixed_sizes <= 0) return;
        int best = 0;
        float bestSize = float.MaxValue, largest = -1;
        int largestIndex = 0;
        for (int i = 0; i < f->num_fixed_sizes; i++)
        {
            float strike = f->available_sizes[i].y_ppem / 64f;
            if (strike >= ppem && strike < bestSize)
            {
                best = i;
                bestSize = strike;
            }

            if (strike > largest)
            {
                largest = strike;
                largestIndex = i;
            }
        }
        Check(FT_Select_Size(f, bestSize == float.MaxValue ? largestIndex : best), "FT_Select_Size");
    }

    /// <summary>
    /// The glyph drawn for <paramref name="codePoint"/>, rasterising it if it is new (CPU only; <see cref="Prepare"/> uploads).
    /// </summary>
    public Glyph Lookup(int codePoint)
    {
        if (_glyphs.TryGetValue(codePoint, out var known)) return known;
        var made = Make(codePoint);
        _glyphs[codePoint] = made;
        return made;
    }

    /// <summary>
    /// Makes the glyphs of <paramref name="text"/> exist and uploads what changed, so drawing it takes only <see cref="Lookup"/>.
    /// </summary>
    public void Ensure(string text)
    {
        foreach (var rune in text.EnumerateRunes()) Lookup(rune.Value);
        Prepare();
    }

    /// <summary>
    /// The kerning between two code points in output pixels (none in Faithful mode: MyGUI 3.2 does not kern).
    /// </summary>
    public float Kern(int left, int right) => _faithful ? 0 : _face.KerningUnits(left, right) * _kerningScale;

    /// <summary>
    /// The glyph of <paramref name="codePoint"/>: a tab's advance, nothing for control characters (and in Meitou mode zero-width marks), else the
    /// face's glyph it resolves to.
    /// </summary>
    Glyph Make(int codePoint)
    {
        if (codePoint == '\t')
        {
            if (_tab < 0) _tab = _tabWidth > 0 ? _tabWidth : 8 * Lookup(' ').Advance;
            return new Glyph(_tab, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        // Line breaks are the text layout's; other C0 controls and (Meitou) zero-width marks have no glyph.
        if (codePoint < 0x20 || !_faithful && codePoint is >= 0x200B and <= 0x200F or 0xFEFF or >= 0xFE00 and <= 0xFE0F) return default;
        return ForIndex(Resolve(codePoint));
    }

    /// <summary>
    /// The face glyph for a code point. MyGUI: a code point the ranges allow and the face has; else the <c>SubstituteCode</c> glyph if that
    /// is itself such a code point; else the face's undefined glyph (index 0). Meitou: any code point the face has, else the substitute,
    /// else '?', else the undefined glyph.
    /// </summary>
    uint Resolve(int codePoint)
    {
        lock (FreeTypeFace.Gate)
        {
            uint Index(int cp) => cp >= 0 && (_ranges is null || cp == ' ' || _ranges.Contains(cp)) ? FT_Get_Char_Index(_face.Face, (nuint)cp) : 0;
            uint index = Index(codePoint);
            if (index != 0) return index;
            if (_substituteCode >= 0 && Index(_substituteCode) is var substitute and not 0) return substitute;
            if (!_faithful && Index('?') is var question and not 0) return question;
            return 0;
        }
    }

    /// <summary>
    /// The glyph of face glyph <paramref name="index"/>, rasterised once.
    /// </summary>
    Glyph ForIndex(uint index)
    {
        if (_byIndex.TryGetValue(index, out var known)) return known;
        var made = Rasterise(index);
        _byIndex[index] = made;
        return made;
    }

    /// <summary>
    /// Loads and renders face glyph <paramref name="index"/>, packs its bitmap into the atlas and returns its layout; an empty glyph with only its
    /// advance when it has no bitmap or does not fit.
    /// </summary>
    Glyph Rasterise(uint index)
    {
        float advance, offsetX, offsetY;
        int width, height;
        lock (FreeTypeFace.Gate)
        {
            Check(FT_Activate_Size(_size), "FT_Activate_Size");
            if (LoadGlyph(index) != FT_Error.FT_Err_Ok) return default;
            var slot = _face.Face->glyph;

            // MyGUI advances by the hinted advance; Meitou by the unhinted one (light hinting leaves it alone), both in output pixels.
            advance = _faithful ? slot->advance.x / 64f
                : (slot->linearHoriAdvance != 0 ? slot->linearHoriAdvance / 65536f : slot->advance.x / 64f) / _oversample;
            ref var bitmap = ref slot->bitmap;
            width = (int)bitmap.width;
            height = (int)bitmap.rows;
            if (width <= 0 || height <= 0 || bitmap.buffer == null || bitmap.pitch <= 0 || width > MaxSide - 2 * _pad || height > MaxSide - 2 * _pad)
            {
                return new Glyph(advance, 0, 0, 0, 0, 0, 0, 0, 0);
            }

            if (!CopyBitmap(bitmap, width, height)) return new Glyph(advance, 0, 0, 0, 0, 0, 0, 0, 0);

            // MyGUI places the bitmap at the metrics' bearing (floor(ascent - horiBearingY) from the line top, less OffsetHeight); for
            // hinted glyphs that is bitmap_left / bitmap_top, which are used here.
            offsetX = slot->bitmap_left / (float)_oversample;
            offsetY = (-slot->bitmap_top - _offsetHeight) / (float)_oversample;
        }
        if (!Pack(width, height, out int x0, out int y0)) return new Glyph(advance, 0, 0, 0, 0, 0, 0, 0, 0);
        for (int row = 0; row < height; row++) System.Buffer.BlockCopy(_scratch, row * width, _pixels, (y0 + row) * Side + x0, width);
        MarkDirty(x0, y0, x0 + width, y0 + height);
        return new Glyph(advance, offsetX, offsetY, width / (float)_oversample, height / (float)_oversample, (ushort)x0, (ushort)y0, (ushort)width,
            (ushort)height);
    }

    /// <summary>
    /// Loads a glyph with the hinting it is asked for. A font whose bytecode this FreeType rejects (ume-pgs5.ttf: Invalid_Opcode at every size) is
    /// tried with the auto-hinter, then with no hinting, for the glyph; and for the font from the third failure on (a font whose hinting fails once
    /// is not tried at every glyph). Called with the FreeType lock held.
    /// </summary>
    FT_Error LoadGlyph(uint index)
    {
        var error = FT_Load_Glyph(_face.Face, index, _loadFlags | FT_LOAD.FT_LOAD_RENDER);
        if (error != FT_Error.FT_Err_Ok && (_loadFlags & (FT_LOAD.FT_LOAD_NO_HINTING | FT_LOAD.FT_LOAD_FORCE_AUTOHINT)) == 0)
        {
            if (++_hintingFailures >= 3) _loadFlags = FT_LOAD.FT_LOAD_FORCE_AUTOHINT;
            error = FT_Load_Glyph(_face.Face, index, FT_LOAD.FT_LOAD_FORCE_AUTOHINT | FT_LOAD.FT_LOAD_RENDER);
        }

        if (error != FT_Error.FT_Err_Ok && (_loadFlags & FT_LOAD.FT_LOAD_NO_HINTING) == 0)
        {
            if (++_hintingFailures >= 3) _loadFlags = FT_LOAD.FT_LOAD_NO_HINTING;
            error = FT_Load_Glyph(_face.Face, index, FT_LOAD.FT_LOAD_NO_HINTING | FT_LOAD.FT_LOAD_RENDER);
        }

        return error;
    }

    /// <summary>
    /// Copies the rendered bitmap of the current glyph into the scratch buffer as 8-bit coverage; false for a pixel mode this does not take.
    /// </summary>
    bool CopyBitmap(FT_Bitmap_ bitmap, int width, int height)
    {
        if (_scratch.Length < width * height) _scratch = new byte[width * height];
        switch (bitmap.pixel_mode)
        {
            case FT_Pixel_Mode_.FT_PIXEL_MODE_GRAY:
                for (int row = 0; row < height; row++)
                {
                    new ReadOnlySpan<byte>(bitmap.buffer + (long)row * bitmap.pitch, width).CopyTo(_scratch.AsSpan(row * width, width));
                }

                return true;
            case FT_Pixel_Mode_.FT_PIXEL_MODE_MONO:
                // An embedded bitmap strike: one bit per texel, most significant first.
                for (int row = 0; row < height; row++)
                {
                    byte* source = bitmap.buffer + (long)row * bitmap.pitch;
                    for (int x = 0; x < width; x++)
                    {
                        _scratch[row * width + x] = (source[x >> 3] >> (7 - (x & 7)) & 1) != 0 ? (byte)255 : (byte)0;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A free rectangle of <paramref name="width"/> x <paramref name="height"/> texels (with the spacing around it): growing the atlas, then starting
    /// it over, when it is full.
    /// </summary>
    bool Pack(int width, int height, out int x, out int y)
    {
        bool restarted = false;
        while (true)
        {
            if (_cursorX > _pad && _cursorX + width + _pad > Side)
            {
                _cursorY += _rowHeight + _pad;
                _cursorX = _pad;
                _rowHeight = 0;
            }

            if (width + 2 * _pad <= Side && _cursorY + height + _pad <= Side)
            {
                (x, y) = (_cursorX, _cursorY);
                _cursorX += width + _pad;
                _rowHeight = Math.Max(_rowHeight, height);
                return true;
            }

            if (Side < MaxSide)
            {
                Grow();
            }
            else if (!restarted)
            {
                Restart();
                restarted = true;
            }
            else
            {
                (x, y) = (0, 0);
                return false;
            }
        }
    }

    /// <summary>
    /// Grows the atlas rectangle waiting for upload to hold the one given.
    /// </summary>
    void MarkDirty(int x0, int y0, int x1, int y1)
    {
        if (!_dirty)
        {
            (_dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1, _dirty) = (x0, y0, x1, y1, true);
            return;
        }

        (_dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1) = (Math.Min(_dirtyX0, x0), Math.Min(_dirtyY0, y0), Math.Max(_dirtyX1, x1), Math.Max(_dirtyY1, y1));
    }

    /// <summary>
    /// The atlas twice as large: glyph rectangles keep their place, texture coordinates change (and the GPU texture is made anew).
    /// </summary>
    void Grow()
    {
        Retire();
        int side = Side * 2;
        var larger = new byte[side * side];
        for (int row = 0; row < Side; row++) System.Buffer.BlockCopy(_pixels, row * Side, larger, row * side, Side);
        _pixels = larger;
        Side = side;
        Generation++;
        Changes++;
    }

    /// <summary>
    /// Forgets every glyph and empties the atlas (it is full at its largest size).
    /// </summary>
    void Restart()
    {
        Retire();
        _glyphs.Clear();
        _byIndex.Clear();
        _tab = -1;
        Array.Clear(_pixels);
        (_cursorX, _cursorY, _rowHeight) = (_pad, _pad, 0);
        Generation++;
        Changes++;
    }

    /// <summary>
    /// Drops the GPU texture after sending it what is pending (quads already queued sample it). It is freed once the frames that use it
    /// have finished (<see cref="GpuTexture.Dispose"/>), so the atlas can be replaced in the middle of a frame.
    /// </summary>
    void Retire()
    {
        if (_texture is null)
        {
            _dirty = false;
            return;
        }

        UploadDirty();
        _texture.Dispose();
        _texture = null;
    }

    /// <summary>
    /// Uploads the glyphs rasterised since the last call, creating the GPU texture first when there is none.
    /// </summary>
    public void Prepare()
    {
        if (_texture is null) Create();
        else if (_dirty) UploadDirty();
    }

    /// <summary>
    /// The atlas as a draw samples it (uploads first).
    /// </summary>
    public SampledTexture Sampled
    {
        get
        {
            Prepare();
            return _sampled;
        }
    }

    /// <summary>
    /// Makes the GPU texture from the whole atlas.
    /// </summary>
    void Create()
    {
        using var upload = _gpu.Uploads.Begin();
        var created = upload.Create(new TextureDesc(InternalFormat.R8, Side, Side, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "UI font"));
        upload.Write(created, 0, 0, new PixelRect(0, 0, (uint)Side, (uint)Side), _pixels);
        _texture = created;
        _sampled = new SampledTexture(_gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear)),
            created.View());
        _dirty = false;
    }

    /// <summary>
    /// Uploads the atlas rectangle changed since the last upload.
    /// </summary>
    void UploadDirty()
    {
        if (!_dirty || _texture is null)
        {
            _dirty = false;
            return;
        }

        int width = _dirtyX1 - _dirtyX0, height = _dirtyY1 - _dirtyY0;
        if (_uploadScratch.Length < width * height) _uploadScratch = new byte[width * height];
        for (int row = 0; row < height; row++) System.Buffer.BlockCopy(_pixels, (_dirtyY0 + row) * Side + _dirtyX0, _uploadScratch, row * width, width);
        using var upload = _gpu.Uploads.Begin();
        upload.Write(_texture, 0, 0, new PixelRect(_dirtyX0, _dirtyY0, (uint)width, (uint)height), _uploadScratch.AsSpan(0, width * height));
        _dirty = false;
    }

    /// <summary>
    /// Frees the FreeType size and the GPU texture.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (FreeTypeFace.Gate) FT_Done_Size(_size);
        _texture?.Dispose();
        _texture = null;
    }
}