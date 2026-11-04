using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Launcher;

/// <summary>A line of the Mods page.</summary>
public sealed class ModRow(ModInfo mod, bool enabled) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ModInfo Mod { get; } = mod;
    public string Name => Mod.Name;
    public string Detail => LauncherText.Translate(Mod.Source == ModSource.Workshop ? "Steam workshop" : "Local mod")
        + (Mod.Header is { Author.Length: > 0 } h ? $" · {h.Author}" : "");
    public string Notes => string.Join(" · ", new[]
    {
        Mod.HasCode ? LauncherText.Translate("Code mod: runs C# code of its own once allowed") : null,
        Mod.HasNativePlugin && !Mod.HasCode ? LauncherText.Translate("RE_Kenshi plugin: its DLLs are ignored in Meitou") : null,
    }.OfType<string>());
    public bool HasNotes => Mod.HasNativePlugin || Mod.HasCode;

    public bool Enabled
    {
        get;
        set { if (field != value) { field = value; Changed(); } }
    } = enabled;

    /// <summary>The launcher's language changed: the translated texts are read again.</summary>
    public void LanguageChanged()
    {
        Changed(nameof(Detail));
        Changed(nameof(Notes));
    }

    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A line of the Versions page.</summary>
public sealed class ReleaseRow(Release release, bool installed, bool played)
{
    public Release Release { get; } = release;
    public bool Installed { get; } = installed;
    public string Title => Release.Title;
    public string Detail => string.Join(" · ", new[]
    {
        Release.Published.ToString("yyyy-MM-dd"),
        Release.Prerelease ? LauncherText.Translate("nightly") : null,
        Release.Asset is null ? Loc.Format(LauncherText.Translate("no {1} build"), Releases.Rid) : null,
    }.OfType<string>());
    /// <summary>The version Play starts.</summary>
    public bool Played { get; } = played;
    public string State => Played ? LauncherText.Translate("Installed · Play") : Installed ? LauncherText.Translate("Installed") : "";
}
