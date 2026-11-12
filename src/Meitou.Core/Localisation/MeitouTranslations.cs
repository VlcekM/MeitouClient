using System.Text;

namespace Meitou.Localisation;

/// <summary>
/// Meitou's own texts (launcher, title menu, settings menu) in a language (docs/formats/localisation.md): a gettext <c>.po</c> per locale
/// code built into Meitou.Core (<c>Localisation/locale/&lt;code&gt;.po</c>), with <c>locale/&lt;code&gt;/meitou.po</c> in the Kenshi folder read
/// over it, so a language can be added or a string changed without a rebuild. The game's own texts come from Kenshi's catalog; this one is
/// consulted after it (<see cref="Loc.Tr(string)"/>). Keys are the English source strings, lookups exact.
/// </summary>
public static class MeitouTranslations
{
    /// <summary>
    /// The embedded resource name of a built-in catalog is <see cref="Prefix"/>, the locale code and <see cref="Suffix"/>.
    /// </summary>
    const string Prefix = "Meitou.locale.", Suffix = ".po";

    /// <summary>
    /// The file in <c>locale/&lt;code&gt;/</c> whose messages override the built-in ones.
    /// </summary>
    public const string OverrideFile = "meitou.po";

    /// <summary>
    /// The locale codes with a catalog built into Meitou, sorted.
    /// </summary>
    public static IReadOnlyList<string> EmbeddedCodes { get; } = typeof(MeitouTranslations).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..^Suffix.Length]).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Whether Meitou has a built-in catalog for <paramref name="code"/> (<c>pt_BR</c> or <c>pt-BR</c>).
    /// </summary>
    public static bool HasEmbedded(string code) => Embedded(code) is not null;

    /// <summary>
    /// Whether <paramref name="code"/> has any Meitou texts: built in, or a <see cref="OverrideFile"/> in <paramref name="localeFolder"/>.
    /// </summary>
    public static bool Has(string code, string? localeFolder) =>
        HasEmbedded(code) || (localeFolder is not null && File.Exists(Path.Combine(localeFolder, OverrideFile)));

    /// <summary>
    /// The catalog of <paramref name="code"/>: the built-in one with the messages of <paramref name="localeFolder"/>'s <see cref="OverrideFile"/>
    /// over it; <see cref="GettextCatalog.Empty"/> when neither exists. An unreadable override is reported and skipped.
    /// </summary>
    public static GettextCatalog Load(string code, string? localeFolder)
    {
        var result = GettextCatalog.Empty;
        if (Embedded(code) is { } name)
            using (var stream = typeof(MeitouTranslations).Assembly.GetManifestResourceStream(Prefix + name + Suffix))
                if (stream is not null)
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) result = GettextCatalog.ReadPo(reader.ReadToEnd());
        if (localeFolder is null || Path.Combine(localeFolder, OverrideFile) is not { } file || !File.Exists(file))
            return result;
        try
        {
            var over = GettextCatalog.ReadPo(File.ReadAllText(file, Encoding.UTF8));
            if (result.Count == 0) return over;
            result.Add(over);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"locale    {file}: {e.Message}; ignored");
        }
        return result;
    }

    /// <summary>
    /// The built-in catalog's locale code matching <paramref name="code"/> (dash or underscore, any case); null when none is built in.
    /// </summary>
    static string? Embedded(string code)
    {
        string normalized = code.Replace('-', '_');
        return EmbeddedCodes.FirstOrDefault(c => c.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }
}