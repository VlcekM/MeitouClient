using Avalonia.Controls;
using Avalonia.VisualTree;
using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Launcher;

/// <summary>
/// The launcher's own texts in the chosen language: Meitou's catalog for the locale code (<see cref="MeitouTranslations"/>: built in, plus
/// <c>locale/&lt;code&gt;/meitou.po</c> in the Kenshi folder), exact lookups of the English source strings. English, and a string without a
/// translation, pass through unchanged.
/// </summary>
static class LauncherText
{
    static GettextCatalog catalog = GettextCatalog.Empty;
    // Translated text -> its English source, over every catalog loaded so far: a control first seen with a translated text (made after a
    // language switch) can still be turned back to English.
    static readonly Dictionary<string, string> sources = new(StringComparer.Ordinal);

    /// <summary>The active locale code (<c>en_GB</c>, <c>pt_BR</c>, ...).</summary>
    public static string Code { get; private set; } = GameLanguage.DefaultCode;

    /// <summary>Loads <paramref name="code"/>'s catalog (none for English); <paramref name="install"/> is where the override file is looked for.</summary>
    public static void Use(string code, GameInstall? install)
    {
        Code = code;
        catalog = code == GameLanguage.DefaultCode ? GettextCatalog.Empty : MeitouTranslations.Load(code, LocaleFolder(install, code));
        foreach (var (english, translated) in catalog.Messages) sources.TryAdd(translated, english);
    }

    /// <summary><c>locale/&lt;code&gt;</c> of the Kenshi folder, where a <c>meitou.po</c> overrides the built-in texts; null without a folder.</summary>
    public static string? LocaleFolder(GameInstall? install, string code) => install is null ? null : Path.Combine(install.Root, "locale", code);

    /// <summary>The English source of <paramref name="text"/> when it is a translation, else <paramref name="text"/> itself.</summary>
    public static string Source(string text) => sources.GetValueOrDefault(text, text);

    /// <summary><paramref name="text"/> in the active language, or its English when it has no translation.</summary>
    public static string Translate(string text)
    {
        string english = Source(text);
        return catalog.Find(english) ?? english;
    }

    /// <summary>
    /// <see cref="Translate(string)"/> of a text with holes: the catalog's key is the English text with the same <c>{1}</c>, <c>{2}</c> placeholders
    /// the game's own messages use (1-based, so a translation may put them in its own order), filled with <paramref name="args"/> afterwards.
    /// A text built at run time cannot be a key, so the code builds its template instead.
    /// </summary>
    public static string Translate(string text, params object?[] args) => Loc.Format(Translate(text), args);

    /// <summary>
    /// Puts every label under <paramref name="root"/> in the active language; <paramref name="originals"/> remembers each control's English
    /// so a later switch starts from it. Lists, combo boxes, the release notes and the news hold data, not labels, and are skipped, but a
    /// placeholder and a tooltip are labels on any of them.
    /// </summary>
    public static void TranslateTree(Control root, IDictionary<Control, string> originals)
    {
        void Visit(Control control)
        {
            // A placeholder is a label too, even on a control whose items hold data (a combo box's list, a text box's text).
            if (control is TextBox textBox && textBox.PlaceholderText is { } placeholder) textBox.PlaceholderText = Translate(placeholder);
            else if (control is ComboBox combo && combo.PlaceholderText is { } hint) combo.PlaceholderText = Translate(hint);
            if (control is ComboBox or ListBox or ContentControl { Name: "ReleaseNotes" } or StackPanel { Name: "NewsPanel" }) return;
            if (control is TextBlock text && text.Text is { } value)
            {
                if (!originals.ContainsKey(control)) originals[control] = Source(value);
                text.Text = Translate(originals[control]);
            }
            else if (control is Button button && button.Content is string content)
            {
                if (!originals.ContainsKey(control)) originals[control] = Source(content);
                button.Content = Translate(originals[control]);
            }
            if (ToolTip.GetTip(control) is string tip) ToolTip.SetTip(control, Translate(tip));
            foreach (var child in control.GetVisualChildren())
                if (child is Control nested) Visit(nested);
        }
        Visit(root);
    }
}
