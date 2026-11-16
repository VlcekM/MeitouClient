using System.Runtime.InteropServices;
using System.Text;

namespace Meitou.Content;

/// <summary>
/// The encoding the original keeps file and mod names in (docs/formats/overview.md, "Name encoding"): the system's ANSI code page on Windows, so
/// <c>mods.cfg</c>, <c>__mods.list</c> and the header dependency lists compare as bytes in it; UTF-8 elsewhere, where there is no such code page.
/// </summary>
public static partial class GameNameEncoding
{
    /// <summary>
    /// The code page, with an encoder that throws on a character the code page lacks (the original converts without best fit).
    /// </summary>
    static readonly Encoding Strict = Create(EncoderFallback.ExceptionFallback);

    /// <summary>
    /// The code page, with an encoder that writes <c>?</c> for a character the code page lacks, as the original's conversion does.
    /// </summary>
    static readonly Encoding Lenient = Create(new EncoderReplacementFallback("?"));

    /// <summary>
    /// The encoding for reading and writing name lists.
    /// </summary>
    public static Encoding Encoding => Lenient;

    /// <summary>
    /// Whether the code page holds every character of <paramref name="name"/>; a name it does not hold turns into <c>?</c>s in the original, which
    /// then name no file.
    /// </summary>
    public static bool CanEncode(string name)
    {
        try
        {
            Strict.GetByteCount(name);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="name"/> as the original has it: characters the code page lacks become <c>?</c>.
    /// </summary>
    public static string AsGameName(string name) => Lenient.GetString(Lenient.GetBytes(name));

    /// <summary>
    /// The encoding of the code page the system uses for file names.
    /// </summary>
    static Encoding Create(EncoderFallback encoderFallback)
    {
        if (!OperatingSystem.IsWindows()) return new UTF8Encoding(false);

        int codePage = (int)GetACP();
        if (codePage == 65001) return new UTF8Encoding(false);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage, encoderFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>
    /// The system's ANSI code page (kernel32).
    /// </summary>
    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();
}