using FreeTypeSharp;
using Meitou.Data.Ui;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using static FreeTypeSharp.FT;

namespace Meitou.Rendering.Ui;

/// <summary>How a <see cref="UiFont"/> is made (docs/formats/mygui.md "Fonts").</summary>
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
    /// <summary>One glyph: layout in output pixels (offsets from the pen and the baseline, quad size), and its rectangle in the atlas in texels.</summary>
    internal readonly record struct Glyph(float Advance, float OffsetX, float OffsetY, float Width, float Height, ushort X, ushort Y, ushort PixelWidth, ushort PixelHeight);

    const int MaxSide = 2048;
    readonly IGpuContext gpu;
    readonly FreeTypeFace face;
    readonly FT_SizeRec_* size;
    readonly bool faithful;
    readonly int oversample;
    FT_LOAD loadFlags;
    int hintingFailures;
    readonly MyGuiCodeRanges? ranges;
    readonly int substituteCode, offsetHeight, pad;
    readonly float tabWidth, kerningScale;
    readonly Dictionary<int, Glyph> glyphs = [];
    readonly Dictionary<uint, Glyph> byIndex = [];
    byte[] pixels;
    byte[] scratch = new byte[1024], uploadScratch = new byte[1024];
    int cursorX, cursorY, rowHeight;
    int dirtyX0, dirtyY0, dirtyX1, dirtyY1;
    bool dirty;
    GpuTexture? texture;
    SampledTexture sampled;
    float tab = -1;
    bool disposed;

    /// <summary>The atlas side in texels.</summary>
    public int Side { get; private set; }
    public int Generation { get; private set; }

    /// <summary>Counts every font's <see cref="Generation"/> changes and atlas releases (render thread): recorded glyph quads are stale once it moves.</summary>
    public static int Changes { get; private set; }
    /// <summary>Glyphs are placed on whole pixels (Faithful, and Meitou fonts the original draws with <c>Antialias</c> false).</summary>
    public bool Snapped { get; }
    /// <summary>The line's height and the distance from its top to the baseline, in pixels.</summary>
    public float LineHeight { get; }
    public float Baseline { get; }
    public float CharWidth { get; }

    public UiFont(IGpuContext gpu, UiFontOptions options)
    {
        this.gpu = gpu;
        faithful = options.Faithful;
        Snapped = faithful || options.Snapped;
        var resource = options.Resource;
        oversample = !faithful && !Snapped ? 2 : 1;
        ranges = faithful ? resource?.Codes ?? MyGuiCodeRanges.Bmp : null;
        substituteCode = resource?.SubstituteCode ?? -1;
        offsetHeight = faithful ? resource?.OffsetHeight ?? 0 : 0;
        tabWidth = resource?.TabWidth ?? 0;
        pad = Math.Max(1, faithful ? resource?.Distance ?? 1 : 1) * oversample;
        var hinting = resource?.Hinting ?? MyGuiHinting.UseNative;
        loadFlags = !faithful ? Snapped ? (FT_LOAD)FT_LOAD_TARGET_LIGHT : FT_LOAD.FT_LOAD_NO_HINTING : hinting switch
        {
            MyGuiHinting.ForceAuto => FT_LOAD.FT_LOAD_FORCE_AUTOHINT,
            MyGuiHinting.DisableAuto => FT_LOAD.FT_LOAD_NO_AUTOHINT,
            MyGuiHinting.DisableAll => FT_LOAD.FT_LOAD_NO_HINTING,
            _ => FT_LOAD.FT_LOAD_DEFAULT,
        };
        face = FreeTypeFace.Open(options.Path);
        float ppem = options.Size64 / 64f * options.Dpi / 72f;
        lock (FreeTypeFace.Gate)
        {
            FT_SizeRec_* created;
            Check(FT_New_Size(face.Face, &created), "FT_New_Size");
            size = created;
            Check(FT_Activate_Size(size), "FT_Activate_Size");
            if (face.Scalable) Check(FT_Set_Char_Size(face.Face, (nint)(options.Size64 * oversample), 0, options.Dpi, options.Dpi), "FT_Set_Char_Size");
            else SelectStrike(ppem * oversample);
            var metrics = face.Face->size->metrics;
            if (faithful)
            {
                // MyGUI: the grid-fitted ascent and descent, raised to the OS/2 Windows and typographic values at the pixel size.
                int ascent = (int)(metrics.ascender >> 6), descent = (int)((-metrics.descender) >> 6);
                if (face.Os2 is { } os2 && face.UnitsPerEm > 0)
                {
                    int yppem = metrics.y_ppem, upem = face.UnitsPerEm;
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
                float em = ppem / Math.Max(1, face.UnitsPerEm);
                LineHeight = ppem * 1.25f;
                Baseline = (LineHeight - (face.Ascender - face.Descender) * em) / 2 + face.Ascender * em;
            }
            kerningScale = faithful ? 0 : ppem / Math.Max(1, face.UnitsPerEm);
        }
        Side = ppem * oversample <= 48 ? 512 : ppem * oversample <= 96 ? 1024 : MaxSide;
        pixels = new byte[Side * Side];
        (cursorX, cursorY) = (pad, pad);
        // Like MyGUI, which builds every glyph of its ranges at load: the usual ones now (not while a frame draws them), the rest on demand.
        if (ranges is not null) { if (ranges.Count <= 1024) foreach (int cp in ranges.CodePoints()) Lookup(cp); }
        else { for (int cp = 32; cp < 127; cp++) Lookup(cp); for (int cp = 0xA0; cp < 0x100; cp++) Lookup(cp); }
        CharWidth = Lookup('M').Advance;
    }

    static void Check(FT_Error error, string call)
    {
        if (error != FT_Error.FT_Err_Ok) throw new InvalidOperationException($"{call} failed ({error}).");
    }

    /// <summary>A bitmap-only font: the smallest strike at least as large as <paramref name="ppem"/>, else the largest.</summary>
    void SelectStrike(float ppem)
    {
        var f = face.Face;
        if (f->num_fixed_sizes <= 0) return;
        int best = 0;
        float bestSize = float.MaxValue, largest = -1;
        int largestIndex = 0;
        for (int i = 0; i < f->num_fixed_sizes; i++)
        {
            float strike = f->available_sizes[i].y_ppem / 64f;
            if (strike >= ppem && strike < bestSize) { best = i; bestSize = strike; }
            if (strike > largest) { largest = strike; largestIndex = i; }
        }
        Check(FT_Select_Size(f, bestSize == float.MaxValue ? largestIndex : best), "FT_Select_Size");
    }

    /// <summary>The glyph drawn for <paramref name="codePoint"/>, rasterising it if it is new (CPU only; <see cref="Prepare"/> uploads).</summary>
    public Glyph Lookup(int codePoint)
    {
        if (glyphs.TryGetValue(codePoint, out var known)) return known;
        var made = Make(codePoint);
        glyphs[codePoint] = made;
        return made;
    }

    /// <summary>Makes the glyphs of <paramref name="text"/> exist and uploads what changed, so drawing it takes only <see cref="Lookup"/>.</summary>
    public void Ensure(string text)
    {
        foreach (var rune in text.EnumerateRunes()) Lookup(rune.Value);
        Prepare();
    }

    /// <summary>The kerning between two code points in output pixels (none in Faithful mode: MyGUI 3.2 does not kern).</summary>
    public float Kern(int left, int right) => faithful ? 0 : face.KerningUnits(left, right) * kerningScale;

    Glyph Make(int codePoint)
    {
        if (codePoint == '\t')
        {
            if (tab < 0) tab = tabWidth > 0 ? tabWidth : 8 * Lookup(' ').Advance;
            return new Glyph(tab, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        // Line breaks are the text layout's; other C0 controls and (Meitou) zero-width marks have no glyph.
        if (codePoint < 0x20 || !faithful && codePoint is >= 0x200B and <= 0x200F or 0xFEFF or >= 0xFE00 and <= 0xFE0F) return default;
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
            uint Index(int cp) => cp >= 0 && (ranges is null || cp == ' ' || ranges.Contains(cp)) ? FT_Get_Char_Index(face.Face, (nuint)cp) : 0;
            uint index = Index(codePoint);
            if (index != 0) return index;
            if (substituteCode >= 0 && Index(substituteCode) is var substitute and not 0) return substitute;
            if (!faithful && Index('?') is var question and not 0) return question;
            return 0;
        }
    }

    Glyph ForIndex(uint index)
    {
        if (byIndex.TryGetValue(index, out var known)) return known;
        var made = Rasterise(index);
        byIndex[index] = made;
        return made;
    }

    Glyph Rasterise(uint index)
    {
        float advance, offsetX, offsetY;
        int width, height;
        lock (FreeTypeFace.Gate)
        {
            Check(FT_Activate_Size(size), "FT_Activate_Size");
            var error = FT_Load_Glyph(face.Face, index, loadFlags | FT_LOAD.FT_LOAD_RENDER);
            // Some fonts' bytecode is rejected by this FreeType (ume-pgs5.ttf: Invalid_Opcode at every size): the auto-hinter, then no hinting,
            // for the glyph, and for the font from the third failure on (a font whose hinting fails once is not tried at every glyph).
            if (error != FT_Error.FT_Err_Ok && (loadFlags & (FT_LOAD.FT_LOAD_NO_HINTING | FT_LOAD.FT_LOAD_FORCE_AUTOHINT)) == 0)
            {
                if (++hintingFailures >= 3) loadFlags = FT_LOAD.FT_LOAD_FORCE_AUTOHINT;
                error = FT_Load_Glyph(face.Face, index, FT_LOAD.FT_LOAD_FORCE_AUTOHINT | FT_LOAD.FT_LOAD_RENDER);
            }
            if (error != FT_Error.FT_Err_Ok && (loadFlags & FT_LOAD.FT_LOAD_NO_HINTING) == 0)
            {
                if (++hintingFailures >= 3) loadFlags = FT_LOAD.FT_LOAD_NO_HINTING;
                error = FT_Load_Glyph(face.Face, index, FT_LOAD.FT_LOAD_NO_HINTING | FT_LOAD.FT_LOAD_RENDER);
            }
            if (error != FT_Error.FT_Err_Ok) return default;
            var slot = face.Face->glyph;
            // MyGUI advances by the hinted advance; Meitou by the unhinted one (light hinting leaves it alone), both in output pixels.
            advance = faithful ? slot->advance.x / 64f
                : (slot->linearHoriAdvance != 0 ? slot->linearHoriAdvance / 65536f : slot->advance.x / 64f) / oversample;
            ref var bitmap = ref slot->bitmap;
            width = (int)bitmap.width;
            height = (int)bitmap.rows;
            if (width <= 0 || height <= 0 || bitmap.buffer == null || bitmap.pitch <= 0 || width > MaxSide - 2 * pad || height > MaxSide - 2 * pad)
                return new Glyph(advance, 0, 0, 0, 0, 0, 0, 0, 0);
            if (scratch.Length < width * height) scratch = new byte[width * height];
            switch (bitmap.pixel_mode)
            {
                case FT_Pixel_Mode_.FT_PIXEL_MODE_GRAY:
                    for (int row = 0; row < height; row++)
                        new ReadOnlySpan<byte>(bitmap.buffer + (long)row * bitmap.pitch, width).CopyTo(scratch.AsSpan(row * width, width));
                    break;
                case FT_Pixel_Mode_.FT_PIXEL_MODE_MONO:
                    // An embedded bitmap strike: one bit per texel, most significant first.
                    for (int row = 0; row < height; row++)
                    {
                        byte* source = bitmap.buffer + (long)row * bitmap.pitch;
                        for (int x = 0; x < width; x++) scratch[row * width + x] = (source[x >> 3] >> (7 - (x & 7)) & 1) != 0 ? (byte)255 : (byte)0;
                    }
                    break;
                default:
                    return new Glyph(advance, 0, 0, 0, 0, 0, 0, 0, 0);
            }
            // MyGUI places the bitmap at the metrics' bearing (floor(ascent - horiBearingY) from the line top, less OffsetHeight); for
            // hinted glyphs that is bitmap_left / bitmap_top, which are used here.
            offsetX = slot->bitmap_left / (float)oversample;
            offsetY = (-slot->bitmap_top - offsetHeight) / (float)oversample;
        }
        if (!Pack(width, height, out int x0, out int y0)) return new Glyph(advance, 0, 0, 0, 0, 0, 0, 0, 0);
        for (int row = 0; row < height; row++) System.Buffer.BlockCopy(scratch, row * width, pixels, (y0 + row) * Side + x0, width);
        MarkDirty(x0, y0, x0 + width, y0 + height);
        return new Glyph(advance, offsetX, offsetY, width / (float)oversample, height / (float)oversample, (ushort)x0, (ushort)y0, (ushort)width, (ushort)height);
    }

    /// <summary>A free rectangle of <paramref name="width"/> x <paramref name="height"/> texels (with the spacing around it): growing the atlas, then starting it over, when it is full.</summary>
    bool Pack(int width, int height, out int x, out int y)
    {
        bool restarted = false;
        while (true)
        {
            if (cursorX > pad && cursorX + width + pad > Side) { cursorY += rowHeight + pad; cursorX = pad; rowHeight = 0; }
            if (width + 2 * pad <= Side && cursorY + height + pad <= Side)
            {
                (x, y) = (cursorX, cursorY);
                cursorX += width + pad;
                rowHeight = Math.Max(rowHeight, height);
                return true;
            }
            if (Side < MaxSide) Grow();
            else if (!restarted) { Restart(); restarted = true; }
            else { (x, y) = (0, 0); return false; }
        }
    }

    void MarkDirty(int x0, int y0, int x1, int y1)
    {
        if (!dirty) (dirtyX0, dirtyY0, dirtyX1, dirtyY1, dirty) = (x0, y0, x1, y1, true);
        else (dirtyX0, dirtyY0, dirtyX1, dirtyY1) = (Math.Min(dirtyX0, x0), Math.Min(dirtyY0, y0), Math.Max(dirtyX1, x1), Math.Max(dirtyY1, y1));
    }

    /// <summary>The atlas twice as large: glyph rectangles keep their place, texture coordinates change (and the GPU texture is made anew).</summary>
    void Grow()
    {
        Retire();
        int side = Side * 2;
        var larger = new byte[side * side];
        for (int row = 0; row < Side; row++) System.Buffer.BlockCopy(pixels, row * Side, larger, row * side, Side);
        pixels = larger;
        Side = side;
        Generation++;
        Changes++;
    }

    /// <summary>Forgets every glyph and empties the atlas (it is full at its largest size).</summary>
    void Restart()
    {
        Retire();
        glyphs.Clear();
        byIndex.Clear();
        tab = -1;
        Array.Clear(pixels);
        (cursorX, cursorY, rowHeight) = (pad, pad, 0);
        Generation++;
        Changes++;
    }

    /// <summary>
    /// Drops the GPU texture after sending it what is pending (quads already queued sample it). It is freed once the frames that use it
    /// have finished (<see cref="GpuTexture.Dispose"/>), so the atlas can be replaced in the middle of a frame.
    /// </summary>
    void Retire()
    {
        if (texture is null) { dirty = false; return; }
        UploadDirty();
        texture.Dispose();
        texture = null;
    }

    /// <summary>Uploads the glyphs rasterised since the last call, creating the GPU texture first when there is none.</summary>
    public void Prepare()
    {
        if (texture is null) Create();
        else if (dirty) UploadDirty();
    }

    /// <summary>The atlas as a draw samples it (uploads first).</summary>
    public SampledTexture Sampled
    {
        get
        {
            Prepare();
            return sampled;
        }
    }

    void Create()
    {
        using var upload = gpu.Uploads.Begin();
        var created = upload.Create(new TextureDesc(InternalFormat.R8, Side, Side, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "UI font"));
        upload.Write(created, 0, 0, new PixelRect(0, 0, (uint)Side, (uint)Side), pixels);
        texture = created;
        sampled = new SampledTexture(gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), created.View());
        dirty = false;
    }

    void UploadDirty()
    {
        if (!dirty || texture is null) { dirty = false; return; }
        int width = dirtyX1 - dirtyX0, height = dirtyY1 - dirtyY0;
        if (uploadScratch.Length < width * height) uploadScratch = new byte[width * height];
        for (int row = 0; row < height; row++) System.Buffer.BlockCopy(pixels, (dirtyY0 + row) * Side + dirtyX0, uploadScratch, row * width, width);
        using var upload = gpu.Uploads.Begin();
        upload.Write(texture, 0, 0, new PixelRect(dirtyX0, dirtyY0, (uint)width, (uint)height), uploadScratch.AsSpan(0, width * height));
        dirty = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (FreeTypeFace.Gate) FT_Done_Size(size);
        texture?.Dispose();
        texture = null;
    }
}
