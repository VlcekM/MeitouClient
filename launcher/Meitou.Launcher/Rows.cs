using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Launcher;

/// <summary>
/// A line of the Mods page.
/// </summary>
public sealed class ModRow(ModInfo mod, bool enabled, CodeModEntry? code = null) : INotifyPropertyChanged
{
    /// <summary>
    /// How many of the blanked mods <see cref="BlankingNote"/> names; a patch out of place hits dozens.
    /// </summary>
    const int NamedBlanks = 3;

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
    /// The earlier mods whose file paths it empties where it sits in the list (<see cref="ModBlanking.Check"/>); empty when none, or disabled.
    /// </summary>
    public IReadOnlyList<BlankedFile> Blanks
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
    /// The mod's Steam workshop page; null for a local mod. A workshop item's folder is named by its published file id.
    /// </summary>
    public Uri? WorkshopPage => CoveredMods.WorkshopId(Mod) is { } id ? CoveredMods.WorkshopPage(id) : null;

    /// <summary>
    /// The entry that has the mod built into Meitou (<see cref="CoveredMods.BuiltInOf(string, string?)"/>), by name or code mod id (the id the
    /// game reads, found even when <c>meitou.json</c> does not parse); null when none does.
    /// </summary>
    public BuiltInMod? BuiltIn => CoveredMods.BuiltInOf(Mod.Name, Code.Id);

    /// <summary>
    /// The entry suggesting a code mod in place of this RE_Kenshi mod (<see cref="CoveredMods.SuggestionOf"/>); null when none does.
    /// </summary>
    public SuggestedMod? Suggestion => CoveredMods.SuggestionOf(Mod);

    /// <summary>
    /// The entry of the official port the game runs in place of this RE_Kenshi mod's plugin (<see cref="CoveredMods.PortOf"/>); null when none.
    /// </summary>
    public PortedMod? Port => CoveredMods.PortOf(Mod);

    /// <summary>
    /// Whether the mod list holds <see cref="Suggestion"/>'s code mod (set with the list), so the row offers no link to it.
    /// </summary>
    public bool SuggestionInstalled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Changed(nameof(Notes));
            Changed(nameof(SuggestionPage));
        }
    }

    /// <summary>
    /// The Steam workshop page of the suggested code mod while it is not installed; null otherwise, and the row shows no button.
    /// </summary>
    public Uri? SuggestionPage => Suggestion is { } r && !SuggestionInstalled ? CoveredMods.WorkshopPage(r.ByWorkshopId) : null;

    /// <summary>
    /// The text of the button that opens <see cref="SuggestionPage"/>.
    /// </summary>
    public string? SuggestionLabel => Suggestion is { } r ? Loc.Format(LauncherText.Translate("View {1}"), r.By) : null;

    /// <summary>
    /// The tip of the button that opens <see cref="SuggestionPage"/> (bound: the list's own text is not translated with the window's labels).
    /// </summary>
    public string? SuggestionTip => Suggestion is null ? null
        : LauncherText.Translate("Open the Steam workshop page of a Meitou mod that does the same; subscribing is up to you");

    /// <summary>
    /// The tip of the row's link icon, which opens <see cref="WorkshopPage"/>; null (no tip) for a local mod, whose row has no icon.
    /// </summary>
    public string? PageTip => WorkshopPage is null ? null : LauncherText.Translate("Open its Steam workshop page");

    /// <summary>
    /// The row's notes: built into Meitou, a code mod, an ignored or ported RE_Kenshi plugin, its file rebinds and sound banks and a suggested code
    /// mod, an update since the cover was matched, its dependencies, its game version, why it will not load and the mods it blanks.
    /// </summary>
    public string Notes => string.Join(" · ", new[]
    {
        CoveredNote,
        Mod.HasCode && BuiltIn is null ? LauncherText.Translate("Code mod: runs C# code of its own") : null,
        PluginNote,
        Mod.ReKenshi is { FileRebinds.Count: > 0 } ? LauncherText.Translate("RE_Kenshi file rebinds: applied to the map files") : null,
        Mod.ReKenshi is { SoundBanks.Count: > 0 } ? LauncherText.Translate("RE_Kenshi sound banks: loaded before the game's") : null,
        SuggestionNote,
        UpdatedNote,
        Manifest is { DependsOn.Count: > 0 } m ? Loc.Format(LauncherText.Translate("Requires {1}"), string.Join(", ", m.DependsOn)) : null,
        Manifest is { LoadAfter.Count: > 0 } a ? Loc.Format(LauncherText.Translate("Loads after {1}"), string.Join(", ", a.LoadAfter)) : null,
        Manifest is { Incompatible.Count: > 0 } c
            ? Loc.Format(LauncherText.Translate("Incompatible with {1}"), string.Join(", ", c.Incompatible)) : null,
        Manifest is { GameVersion: { } g }
            ? Loc.Format(LauncherText.Translate("Built for Meitou {1} (checked when the game starts)"), g) : null,
    }
        .Concat(DependencyProblems.Select(p => Loc.Format(LauncherText.Translate("Will not load: {1}"), p)))
        .Append(BlankingNote)
        .OfType<string>());

    /// <summary>
    /// The note for an RE_Kenshi plugin without code of its own that Meitou has not built in: Meitou runs its port (<see cref="Port"/>), or its DLLs
    /// are ignored; null otherwise.
    /// </summary>
    string? PluginNote => !Mod.HasNativePlugin || Mod.HasCode || BuiltIn is not null ? null
        : Port is not null ? LauncherText.Translate("RE_Kenshi plugin: Meitou runs its own port of it in its place")
        : LauncherText.Translate("RE_Kenshi plugin: its DLLs are ignored in Meitou");

    /// <summary>
    /// The note for <see cref="BuiltIn"/>: the Meitou setting the feature became, and for a code mod that it is not run; null when not built in.
    /// </summary>
    string? CoveredNote => BuiltIn is not { } built ? null
        : Loc.Format(LauncherText.Translate(Mod.HasCode ? "Built into Meitou: {1}; this mod is not run" : "Built into Meitou: {1}"),
            LauncherText.Translate(built.Setting));

    /// <summary>
    /// The note for <see cref="Suggestion"/>: the suggested code mod, and whether the list holds it; null when none is suggested.
    /// </summary>
    string? SuggestionNote => Suggestion is not { } r ? null
        : SuggestionInstalled ? Loc.Format(LauncherText.Translate("One Meitou mod that does the same: {1} (installed)"), r.By)
        : Loc.Format(LauncherText.Translate("One Meitou mod that does the same: {1}"), r.By);

    /// <summary>
    /// The note for a covered workshop mod updated after the version its cover was matched against (<see cref="CoveredMods.UpdatedSince"/>): the
    /// update's date, and that features it added may be missing; null otherwise.
    /// </summary>
    string? UpdatedNote
    {
        get
        {
            var items = BuiltIn?.Items ?? Port?.Items ?? Suggestion?.Items;
            if (items is null || CoveredMods.UpdatedSince(Mod, items) is not { } updated) return null;

            string date = updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string note = LauncherText.Translate("Updated on the workshop on {1}, after Meitou last checked it: some new features may be missing");
            return Loc.Format(note, date);
        }
    }

    /// <summary>
    /// The public wishlist (<see cref="CoveredMods.Wishlist"/>) for an RE_Kenshi plugin Meitou neither builds in, ports nor has a suggestion for, so
    /// the player can ask for it; null otherwise, and the row shows no Request button.
    /// </summary>
    public Uri? RequestPage => Mod.HasNativePlugin && !Mod.HasCode && BuiltIn is null && Port is null && Suggestion is null ? CoveredMods.Wishlist : null;

    /// <summary>
    /// The text of the button that opens <see cref="RequestPage"/>.
    /// </summary>
    public string? RequestLabel => RequestPage is null ? null : LauncherText.Translate("Request it");

    /// <summary>
    /// The tip of the button that opens <see cref="RequestPage"/>.
    /// </summary>
    public string? RequestTip => RequestPage is null ? null
        : LauncherText.Translate("Open the public wishlist of RE_Kenshi mods for Meitou, where you can ask for this one");

    /// <summary>
    /// The note for <see cref="Blanks"/>: how many records lose file paths and whose, most records first; null when none.
    /// </summary>
    string? BlankingNote
    {
        get
        {
            if (Blanks.Count == 0) return null;

            string mods = string.Join(", ", Blanks.Take(NamedBlanks).Select(b => b.Mod));
            if (Blanks.Count > NamedBlanks) mods = Loc.Format(LauncherText.Translate("{1} and {2} more"), mods, Blanks.Count - NamedBlanks);
            string text = LauncherText.Translate("Empties file paths in {1} records of {2}: it likely belongs above them");
            return Loc.Format(text, Blanks.Sum(b => b.Records), mods);
        }
    }

    /// <summary>
    /// Whether the row shows its notes (a code mod, RE_Kenshi entries, a covered mod or a mod that blanks others).
    /// </summary>
    public bool HasNotes =>
        Mod.ReKenshi is { HasPlugins: true } or { FileRebinds.Count: > 0 } or { SoundBanks.Count: > 0 }
        || Mod.HasCode
        || BuiltIn is not null
        || Suggestion is not null
        || Blanks.Count > 0;

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
        Changed(nameof(PageTip));
        Changed(nameof(SuggestionLabel));
        Changed(nameof(SuggestionTip));
        Changed(nameof(RequestLabel));
        Changed(nameof(RequestTip));
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
