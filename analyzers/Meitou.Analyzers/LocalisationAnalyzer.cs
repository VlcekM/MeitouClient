using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Meitou.Analyzers;

/// <summary>
/// The rules of Meitou's own texts (docs/formats/localisation.md "Keeping Meitou's catalogs complete"), as .github/scripts/loc-strings.mjs
/// checks them, so the build fails before a push does. MEI0001: a text a player reads built with $"...", which no catalog can match, unless
/// meitou.built lists it. MEI0002: a message some built-in catalog has no usable translation of, unless meitou.ignore lists it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LocalisationAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// MEI0001: built text.
    /// </summary>
    static readonly DiagnosticDescriptor BuiltRule = new(
        "MEI0001",
        "Player-facing text built with $\"...\"",
        "\"{0}\" is built with $\"...\" where a player reads it, so no catalog can match it; give it to Loc.Tr with {{1}}, {{2}} placeholders",
        "Localisation",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// MEI0002: missing translation.
    /// </summary>
    static readonly DiagnosticDescriptor MissingRule = new(
        "MEI0002",
        "Text without a translation",
        "\"{0}\" has no usable translation in {1}; translate it (node .github/scripts/loc-fill.mjs) or list it in meitou.ignore",
        "Localisation",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// The rules this analyzer reports: MEI0001 and MEI0002.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [BuiltRule, MissingRule];

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
    /// Reads the locale files and checks each C# file under src/ or launcher/ and each launcher .axaml file; nothing without the locale files.
    /// </summary>
    static void Start(CompilationStartAnalysisContext context)
    {
        LocaleFiles? locale = LocaleFiles.Find(context.Options.AdditionalFiles, context.CancellationToken);
        if (locale is null) return;

        context.RegisterSyntaxTreeAction(tree => CheckCSharp(tree, locale));
        context.RegisterAdditionalFileAction(file => CheckAxaml(file, locale));
    }

    /// <summary>
    /// Reports one C# file's built texts that meitou.built does not list and its messages a catalog lacks.
    /// </summary>
    static void CheckCSharp(SyntaxTreeAnalysisContext context, LocaleFiles locale)
    {
        string? path = locale.RelativePath(context.Tree.FilePath);
        if (path is null || !path.EndsWith(".cs", StringComparison.Ordinal)) return;

        string src = context.Tree.GetText(context.CancellationToken).ToString();
        (List<CSharpLiteral> built, List<LocMessage> messages) = LocTexts.FindInCSharp(src, path);
        foreach (CSharpLiteral literal in built.Where(l => !locale.Built.Contains(l.Value)))
        {
            Location at = Location.Create(context.Tree, Span(literal.Start, literal.End, src.Length));
            context.ReportDiagnostic(Diagnostic.Create(BuiltRule, at, literal.Value));
        }

        foreach ((LocMessage message, string lacking) in Missing(messages, locale))
        {
            Location at = Location.Create(context.Tree, Span(message.Start, message.End, src.Length));
            context.ReportDiagnostic(Diagnostic.Create(MissingRule, at, message.Id, lacking));
        }
    }

    /// <summary>
    /// Reports the captions of a launcher .axaml file that a catalog lacks.
    /// </summary>
    static void CheckAxaml(AdditionalFileAnalysisContext context, LocaleFiles locale)
    {
        string? path = locale.RelativePath(context.AdditionalFile.Path);
        if (path is null || !path.StartsWith("launcher/", StringComparison.Ordinal) || !path.EndsWith(".axaml", StringComparison.Ordinal)) return;
        if (context.AdditionalFile.GetText(context.CancellationToken) is not { } text) return;

        foreach ((LocMessage message, string lacking) in Missing(LocTexts.FindInAxaml(text.ToString()), locale))
        {
            TextSpan span = Span(message.Start, message.End, text.Length);
            Location at = Location.Create(context.AdditionalFile.Path, span, text.Lines.GetLinePositionSpan(span));
            context.ReportDiagnostic(Diagnostic.Create(MissingRule, at, message.Id, lacking));
        }
    }

    /// <summary>
    /// The messages not on the ignore list that a catalog cannot translate, each with the catalogs and their reasons ("de_DE, fr_FR: no entry").
    /// </summary>
    static IEnumerable<(LocMessage Message, string Lacking)> Missing(IEnumerable<LocMessage> messages, LocaleFiles locale)
    {
        foreach (LocMessage message in messages.Where(m => !locale.Ignored.Ignores(m)))
        {
            IEnumerable<string> byReason = locale.Catalogs
                .Select(c => (c.Code, Problem: c.Problem(message)))
                .Where(c => c.Problem is not null)
                .GroupBy(c => c.Problem)
                .Select(g => string.Join(", ", g.Select(c => c.Code)) + ": " + g.Key);
            string lacking = string.Join("; ", byReason);
            if (lacking.Length > 0) yield return (message, lacking);
        }
    }

    /// <summary>
    /// The span [start, end), clamped to a text of <paramref name="length"/> characters.
    /// </summary>
    static TextSpan Span(int start, int end, int length)
    {
        return TextSpan.FromBounds(Math.Min(start, length), Math.Min(end, length));
    }
}
