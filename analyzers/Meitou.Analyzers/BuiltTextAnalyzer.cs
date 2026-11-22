using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Meitou.Analyzers;

/// <summary>
/// MEI0001: a text a player reads built with $"...", which no catalog can match (docs/formats/localisation.md "Keeping Meitou's catalogs
/// complete"). The same rule as the Translations workflow's check (.github/scripts/loc-strings.mjs), so it fails the build before a push does;
/// the texts listed in src/Meitou.Core/Localisation/locale/meitou.built (an additional file) are the baseline still to convert.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BuiltTextAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// The baseline's path from the repository root.
    /// </summary>
    const string BaselinePath = "src/Meitou.Core/Localisation/locale/meitou.built";

    /// <summary>
    /// The rule reported.
    /// </summary>
    static readonly DiagnosticDescriptor Rule = new(
        "MEI0001",
        "Player-facing text built with $\"...\"",
        "\"{0}\" is built with $\"...\" where a player reads it, so no catalog can match it; give it to Loc.Tr with {{1}}, {{2}} placeholders",
        "Localisation",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// The rules this analyzer reports: MEI0001.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <summary>
    /// Checks each compilation's files, generated code skipped.
    /// </summary>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(Start);
    }

    /// <summary>
    /// Reads the baseline and checks each file of the compilation under src/ or launcher/; nothing without the baseline.
    /// </summary>
    static void Start(CompilationStartAnalysisContext context)
    {
        AdditionalText? baseline = context.Options.AdditionalFiles.FirstOrDefault(f => Normalise(f.Path).EndsWith("/" + BaselinePath));
        if (baseline is null) return;

        string normalised = Normalise(baseline.Path);
        string root = normalised.Substring(0, normalised.Length - BaselinePath.Length);
        HashSet<string> listed = ReadList(baseline.GetText(context.CancellationToken));
        context.RegisterSyntaxTreeAction(tree => Check(tree, root, listed));
    }

    /// <summary>
    /// Reports the built texts of one file that the baseline does not list.
    /// </summary>
    static void Check(SyntaxTreeAnalysisContext context, string root, HashSet<string> listed)
    {
        string? path = RelativePath(context.Tree.FilePath, root);
        if (path is null) return;

        string src = context.Tree.GetText(context.CancellationToken).ToString();
        foreach (CSharpLiteral literal in BuiltTexts.Find(src, path).Where(l => !listed.Contains(l.Value)))
        {
            int end = Math.Min(literal.End, src.Length);
            var location = Location.Create(context.Tree, TextSpan.FromBounds(literal.Start, end));
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, literal.Value));
        }
    }

    /// <summary>
    /// The path of a source file from the repository root when the script reads it (a .cs file under src/ or launcher/, not in bin or obj),
    /// else null.
    /// </summary>
    static string? RelativePath(string filePath, string root)
    {
        string path = Normalise(filePath);
        if (!path.StartsWith(root, StringComparison.Ordinal) || !path.EndsWith(".cs", StringComparison.Ordinal)) return null;

        string relative = path.Substring(root.Length);
        if (!relative.StartsWith("src/", StringComparison.Ordinal) && !relative.StartsWith("launcher/", StringComparison.Ordinal)) return null;
        if (relative.Split('/').Any(part => part is "bin" or "obj")) return null;

        return relative;
    }

    /// <summary>
    /// A list file's texts: one per line ('\n' for a line break), '#' lines and blank lines skipped.
    /// </summary>
    static HashSet<string> ReadList(SourceText? text)
    {
        if (text is null) return [];

        IEnumerable<string> entries = text.ToString()
            .Split('\n')
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Replace("\\n", "\n"));
        return new HashSet<string>(entries, StringComparer.Ordinal);
    }

    /// <summary>
    /// A path with '/' separators.
    /// </summary>
    static string Normalise(string path)
    {
        return path.Replace('\\', '/');
    }
}
