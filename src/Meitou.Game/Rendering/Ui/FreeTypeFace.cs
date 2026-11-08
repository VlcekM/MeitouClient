using FreeTypeSharp;
using Meitou.Data.Ui;
using StbTrueTypeSharp;
using static FreeTypeSharp.FT;

namespace Meitou.Rendering.Ui;

/// <summary>
/// The process-wide FreeType library and one face per font file, shared by every size of it (each <see cref="UiFont"/> owns an
/// <c>FT_Size</c> on it). FreeType objects are not thread-safe: every call on a face or its sizes holds <see cref="Gate"/> (the UI is drawn
/// on the render thread; the gate guards a stray second caller). Faces live as long as the process.
/// </summary>
internal sealed unsafe class FreeTypeFace
{
    /// <summary>Taken around every FreeType call on a face, size or glyph slot.</summary>
    public static readonly object Gate = new();
    static FT_LibraryRec_* library;
    static readonly Dictionary<string, FreeTypeFace> faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the TrueType interpreter was set to version 35, the full bytecode interpreter of the FreeType the original used (2.13 defaults to
    /// version 40, which ignores most horizontal hinting). False if FreeType refused the property; the default interpreter is used then.
    /// </summary>
    public static bool Interpreter35 { get; private set; }

    /// <summary>The face of font file <paramref name="path"/> (opened on first use).</summary>
    public static FreeTypeFace Open(string path)
    {
        string full = Path.GetFullPath(path);
        lock (Gate)
        {
            if (faces.TryGetValue(full, out var known)) return known;
            EnsureLibrary();
            var face = new FreeTypeFace(File.ReadAllBytes(full), full);
            faces[full] = face;
            return face;
        }
    }

    static void EnsureLibrary()
    {
        if (library != null) return;
        FT_LibraryRec_* created;
        if (FT_Init_FreeType(&created) != FT_Error.FT_Err_Ok) throw new InvalidOperationException("Could not initialise FreeType.");
        int version = 35;
        fixed (byte* module = "truetype"u8)
        fixed (byte* property = "interpreter-version"u8)
            Interpreter35 = FT_Property_Set(created, module, property, &version) == FT_Error.FT_Err_Ok;
        library = created;
    }

    readonly byte* memory;
    readonly int length;
    readonly StbTrueType.stbtt_fontinfo? kerningInfo;
    readonly Dictionary<long, int> kerning = [];

    FreeTypeFace(byte[] data, string filePath)
    {
        FilePath = filePath;
        length = data.Length;
        // FT_New_Memory_Face does not copy: the bytes stay for the process (the face does too).
        memory = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)data.Length);
        data.AsSpan().CopyTo(new Span<byte>(memory, data.Length));
        FT_FaceRec_* face;
        var error = FT_New_Memory_Face(library, memory, data.Length, 0, &face);
        if (error != FT_Error.FT_Err_Ok)
        {
            System.Runtime.InteropServices.NativeMemory.Free(memory);
            throw new InvalidDataException($"FreeType could not open the font {filePath} ({error}).");
        }
        Face = face;
        Os2 = KenshiFontSize.Os2Metrics(new ReadOnlySpan<byte>(memory, length));
        // Kerning is read through stb_truetype, which also follows GPOS pair adjustment (FreeType's FT_Get_Kerning reads only the old 'kern' table).
        var info = new StbTrueType.stbtt_fontinfo();
        if (StbTrueType.stbtt_InitFont(info, memory, 0) != 0) kerningInfo = info;
    }

    public string FilePath { get; }
    public FT_FaceRec_* Face { get; }
    public bool Scalable => ((int)Face->face_flags & (int)FT_FACE_FLAG.FT_FACE_FLAG_SCALABLE) != 0;
    public int UnitsPerEm => Face->units_per_EM;
    /// <summary>The hhea ascender (positive) and descender (negative) in font units.</summary>
    public int Ascender => Face->ascender;
    public int Descender => Face->descender;
    /// <summary>The OS/2 ascent and descent values (font units); null without the table.</summary>
    public (int TypoAscender, int TypoDescender, int WinAscent, int WinDescent)? Os2 { get; }

    /// <summary>The kerning between two code points in font units (negative: closer); 0 for none.</summary>
    public int KerningUnits(int left, int right)
    {
        if (kerningInfo is null) return 0;
        long key = (long)left << 32 | (uint)right;
        if (!kerning.TryGetValue(key, out int units)) kerning[key] = units = StbTrueType.stbtt_GetCodepointKernAdvance(kerningInfo, left, right);
        return units;
    }
}
