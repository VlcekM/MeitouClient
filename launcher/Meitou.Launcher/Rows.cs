using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Launcher;

/// <summary>
/// A line of the Mods page.
/// </summary>
public sealed class ModRow(ModInfo mod, bool enabled, CodeModEntry? code = null) : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The mod.
    /// </summary>
    public ModInfo Mod { get; } = mod;

    /// <summary>
    /// The mod as the code-mod rules see it (<see cref="CodeModDependencies.ReadEntry"/>), read with the list: its manifest, or why that cannot be
    /// read. Its <c>Enabled</c> is the state at reading; <see cref="Enabled"/> is current.
    /// </summary>
    public CodeModEntry Code { get; } = code ?? new CodeModEntry(mod.Name, null, enabled);

    /// <summary>
    /// The code manifest (docs/modding-dependencies.md); null for a data mod or one that cannot be read.
    /// </summary>
    public CodeModManifest? Manifest => Code.Manifest;

    /// <summary>
    /// What the game would refuse it for in the current list (<see cref="CodeModChecks.Check"/>), one line each; empty when nothing.
    /// </summary>
    public IReadOnlyList<string> DependencyProblems
    {
        get;
        set
        {
            if (field.SequenceEqual(value)) return;
            field = value;
            Changed(nameof(Notes));
            Changed(nameof(HasNotes));
        }
    } = [];

    /// <summary>
    /// The mod's name.
    /// </summary>
    public string Name => Mod.Name;

    /// <summary>
    /// Where it comes from (Steam workshop or local) and its author.
    /// </summary>
    public string Detail => LauncherText.Translate(Mod.Source == ModSource.Workshop ? "Steam workshop" : "Local mod")
        + (Mod.Header is { Author.Length: > 0 } h ? $" · {h.Author}" : "");

    /// <summary>
    /// The row's notes: a code mod, an ignored RE_Kenshi plugin, its dependencies, its game version and why it will not load.
    /// </summary>
    public string Notes => string.Join(" · ", new[]
    {
        Mod.HasCode ? LauncherText.Translate("Code mod: runs C# code of its own") : null,
        Mod.HasNativePlugin && !Mod.HasCode ? LauncherText.Translate("RE_Kenshi plugin: its DLLs are ignored in Meitou") : null,
        Manifest is { DependsOn.Count: > 0 } m ? Loc.Format(LauncherText.Translate("Requires {1}"), string.Join(", ", m.DependsOn)) : null,
        Manifest is { LoadAfter.Count: > 0 } a ? Loc.Format(LauncherText.Translate("Loads after {1}"), string.Join(", ", a.LoadAfter)) : null,
        Manifest is { Incompatible.Count: > 0 } c
            ? Loc.Format(LauncherText.Translate("Incompatible with {1}"), string.Join(", ", c.Incompatible)) : null,
        Manifest is { GameVersion: { } g }
            ? Loc.Format(LauncherText.Translate("Built for Meitou {1} (checked when the game starts)"), g) : null,
    }.Concat(DependencyProblems.Select(p => Loc.Format(LauncherText.Translate("Will not load: {1}"), p))).OfType<string>());

    /// <summary>
    /// Whether the row shows its notes (a code mod or a native plugin).
    /// </summary>
    public bool HasNotes => Mod.HasNativePlugin || Mod.HasCode;

    /// <summary>
    /// Whether the mod is ticked; a change raises <see cref="PropertyChanged"/>.
    /// </summary>
    public bool Enabled
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                Changed();
            }
        }
    } = enabled;

    /// <summary>
    /// The launcher's language changed: the translated texts are read again.
    /// </summary>
    public void LanguageChanged()
    {
        Changed(nameof(Detail));
        Changed(nameof(Notes));
    }

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for <paramref name="name"/>.
    /// </summary>
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A line of the Versions page.
/// </summary>
public sealed class ReleaseRow(Release release, bool installed, bool played)
{
    /// <summary>
    /// The release.
    /// </summary>
    public Release Release { get; } = release;

    /// <summary>
    /// Whether it is installed.
    /// </summary>
    public bool Installed { get; } = installed;

    /// <summary>
    /// The release's title.
    /// </summary>
    public string Title => Release.Title;

    /// <summary>
    /// Its publish date, whether it is a nightly and whether it has no build for this runtime.
    /// </summary>
    public string Detail => string.Join(" · ", new[]
    {
        Release.Published.ToString("yyyy-MM-dd"),
        Release.Prerelease ? LauncherText.Translate("nightly") : null,
        Release.Asset is null ? Loc.Format(LauncherText.Translate("no {1} build"), Releases.Rid) : null,
    }.OfType<string>());

    /// <summary>
    /// The version Play starts.
    /// </summary>
    public bool Played { get; } = played;

    /// <summary>
    /// "Installed · Play" for the version Play starts, "Installed" for another installed one, else empty.
    /// </summary>
    public string State => Played ? LauncherText.Translate("Installed · Play") : Installed ? LauncherText.Translate("Installed") : "";
}
