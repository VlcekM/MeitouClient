using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Meitou.Analyzers;

/// <summary>
/// The files of src/Meitou.Core/Localisation/locale the localisation rules read, handed to the compiler as additional files: the built-text
/// baseline (meitou.built), the ignore list (meitou.ignore) and every built-in catalog (&lt;code&gt;.po). Their place gives the repository root.
/// </summary>
sealed class LocaleFiles
{
    /// <summary>
    /// The locale folder's path from the repository root.
    /// </summary>
    const string LocaleDir = "src/Meitou.Core/Localisation/locale/";

    /// <summary>
    /// The repository root, '/' separators, ending in '/'.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// The built texts still to convert (meitou.built).
    /// </summary>
    public HashSet<string> Built { get; }

    /// <summary>
    /// The prose that is not Meitou's to translate (meitou.ignore).
    /// </summary>
    public IgnoreList Ignored { get; }

    /// <summary>
    /// The built-in catalogs, by language code.
    /// </summary>
    public List<PoCatalog> Catalogs { get; }

    /// <summary>
    /// Reads the files found under the locale folder.
    /// </summary>
    LocaleFiles(string root, Dictionary<string, AdditionalText> files, CancellationToken cancel)
    {
        Root = root;
        Built = ReadList(Text(files, "meitou.built", cancel));
        Ignored = new IgnoreList(Text(files, "meitou.ignore", cancel));
        Catalogs = files
            .Where(f => f.Key.EndsWith(".po", StringComparison.Ordinal))
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new PoCatalog(f.Key.Substring(0, f.Key.Length - 3), Text(files, f.Key, cancel) ?? ""))
            .ToList();
    }

    /// <summary>
    /// The locale files among <paramref name="additional"/>, or null when the baseline is not one of them (the project is not checked).
    /// </summary>
    public static LocaleFiles? Find(IEnumerable<AdditionalText> additional, CancellationToken cancel)
    {
        var files = new Dictionary<string, AdditionalText>(StringComparer.Ordinal);
        string? root = null;
        foreach (AdditionalText file in additional)
        {
            string path = Normalise(file.Path);
            int at = path.LastIndexOf("/" + LocaleDir, StringComparison.Ordinal);
            if (at < 0) continue;

            string name = path.Substring(at + LocaleDir.Length + 1);
            if (name == "meitou.built") root = path.Substring(0, at + 1);
            files[name] = file;
        }

        return root is null ? null : new LocaleFiles(root, files, cancel);
    }

    /// <summary>
    /// The path of a file from the repository root when the script reads it (under src/ or launcher/, not in bin or obj), else null.
    /// </summary>
    public string? RelativePath(string filePath)
    {
        string path = Normalise(filePath);
        if (!path.StartsWith(Root, StringComparison.Ordinal)) return null;

        string relative = path.Substring(Root.Length);
        if (!relative.StartsWith("src/", StringComparison.Ordinal) && !relative.StartsWith("launcher/", StringComparison.Ordinal)) return null;
        if (relative.Split('/').Any(part => part is "bin" or "obj")) return null;

        return relative;
    }

    /// <summary>
    /// A path with '/' separators.
    /// </summary>
    static string Normalise(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// The text of locale file <paramref name="name"/>, or null when it is absent.
    /// </summary>
    static string? Text(Dictionary<string, AdditionalText> files, string name, CancellationToken cancel)
    {
        if (!files.TryGetValue(name, out AdditionalText? file)) return null;

        SourceText? text = file.GetText(cancel);
        return text?.ToString();
    }

    /// <summary>
    /// A list file's texts: one per line ('\n' for a line break), '#' lines and blank lines skipped.
    /// </summary>
    static HashSet<string> ReadList(string? text)
    {
        IEnumerable<string> entries = (text ?? "")
            .Split('\n')
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Replace("\\n", "\n"));
        return new HashSet<string>(entries, StringComparer.Ordinal);
    }
}
