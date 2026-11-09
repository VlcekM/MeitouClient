using System.Buffers.Binary;

namespace Meitou.Data.Ui;

/// <summary>
/// The point size the original draws a <c>ResourceTrueTypeFont</c> at (docs/formats/mygui.md): after loading, the game resets every font to its
/// MyGUI default height in pixels plus the <c>font size2</c> setting, taken as points again at the resource's resolution.
/// </summary>
public static class KenshiFontSize
{
    /// <summary>
    /// The original's <c>font size2</c> default (settings.cfg).
    /// </summary>
    public const float DefaultSetting = 3;

    /// <summary>
    /// The size in points that replaces <paramref name="size"/> (points at <paramref name="resolution"/> dpi) for the TrueType font
    /// <paramref name="ttf"/>; <paramref name="size"/> itself when the font's tables cannot be read.
    /// </summary>
    public static float Effective(ReadOnlySpan<byte> ttf, float size, float resolution, float setting = DefaultSetting)
    {
        if (DefaultHeight(ttf, size * resolution / 72) is not { } height) return size;
        return Math.Max(1, height + setting);
    }

    /// <summary>
    /// MyGUI's default height at <paramref name="em"/> pixels: the ascent (hhea ascender) plus descent (descender), each rounded to the nearest pixel
    /// and at least the OS/2 Windows metric at the whole pixel size.
    /// </summary>
    static int? DefaultHeight(ReadOnlySpan<byte> ttf, float em)
    {
        var head = Table(ttf, "head");
        var hhea = Table(ttf, "hhea");
        if (head.Length < 20 || hhea.Length < 8) return null;
        int unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(head[18..]);
        if (unitsPerEm == 0 || !(em > 0)) return null;
        int ascender = BinaryPrimitives.ReadInt16BigEndian(hhea[4..]), descender = BinaryPrimitives.ReadInt16BigEndian(hhea[6..]);
        // Rounded to the nearest pixel (FreeType's FT_PIX_ROUND), not up: Exo2 24 pt then gives 23 pt as the original draws it (docs/formats/mygui.md).
        int ascent = (int)MathF.Floor(ascender * em / unitsPerEm + 0.5f), descent = (int)MathF.Floor(-descender * em / unitsPerEm + 0.5f);
        var os2 = Table(ttf, "OS/2");
        if (os2.Length >= 78)
        {
            int ppem = (int)MathF.Round(em);
            ascent = Math.Max(ascent, BinaryPrimitives.ReadUInt16BigEndian(os2[74..]) * ppem / unitsPerEm);
            descent = Math.Max(descent, BinaryPrimitives.ReadUInt16BigEndian(os2[76..]) * ppem / unitsPerEm);
        }
        return ascent + descent;
    }

    /// <summary>
    /// The OS/2 table's typographic (sTypo) and Windows (usWin) ascent and descent in font units, which MyGUI raises its line height to; null without
    /// the table.
    /// </summary>
    public static (int TypoAscender, int TypoDescender, int WinAscent, int WinDescent)? Os2Metrics(ReadOnlySpan<byte> ttf)
    {
        var os2 = Table(ttf, "OS/2");
        if (os2.Length < 78) return null;
        return (BinaryPrimitives.ReadInt16BigEndian(os2[68..]), BinaryPrimitives.ReadInt16BigEndian(os2[70..]),
            BinaryPrimitives.ReadUInt16BigEndian(os2[74..]), BinaryPrimitives.ReadUInt16BigEndian(os2[76..]));
    }

    /// <summary>
    /// The bytes of the TrueType table <paramref name="tag"/> in <paramref name="ttf"/>; empty when the font has none or it runs past the file.
    /// </summary>
    static ReadOnlySpan<byte> Table(ReadOnlySpan<byte> ttf, string tag)
    {
        if (ttf.Length < 12) return default;
        int count = BinaryPrimitives.ReadUInt16BigEndian(ttf[4..]);
        for (int i = 0; i < count; i++)
        {
            int record = 12 + 16 * i;
            if (record + 16 > ttf.Length) break;
            if (ttf[record] != tag[0] || ttf[record + 1] != tag[1] || ttf[record + 2] != tag[2] || ttf[record + 3] != tag[3]) continue;
            long offset = BinaryPrimitives.ReadUInt32BigEndian(ttf[(record + 8)..]);
            long length = BinaryPrimitives.ReadUInt32BigEndian(ttf[(record + 12)..]);
            return offset + length <= ttf.Length ? ttf.Slice((int)offset, (int)length) : default;
        }
        return default;
    }
}