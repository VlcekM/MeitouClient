using Meitou.Data.Characters;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation.Editing;

namespace Meitou.Game.Editor;

/// <summary>
/// The character editor's body files (<see cref="CharacterEditorPanel"/>): EXPORT writes the draft's look, IMPORT opens a dialog on the export
/// folder whose chosen file's look replaces the draft's.
/// </summary>
sealed class EditorBodyFiles(PlayerInterface player, CharacterEditorPanel panel)
{
    /// <summary>
    /// The body files IMPORT and EXPORT use, under the install (<c>FUN_141539ad0</c>), and their extension.
    /// </summary>
    const string ExportFolder = "data/character/bodies/export/", BodyExtension = ".bod2";

    /// <summary>
    /// The folder the import dialog shows, as the original keeps it (install-relative, no trailing separator).
    /// </summary>
    string _importFolder = "./" + ExportFolder.TrimEnd('/');

    /// <summary>
    /// The dialog's lines.
    /// </summary>
    IReadOnlyList<UiListItem> _importEntries = [];

    /// <summary>
    /// The chosen line's id; null for none.
    /// </summary>
    string? _importChoice;

    /// <summary>
    /// A line's id for a folder (the folder's path), against a file's (its path).
    /// </summary>
    const string FolderEntry = "folder:";

    /// <summary>
    /// EXPORT (<c>FUN_1405ea410</c>): the look goes to the export folder under the name typed in NameText, replacing a file of that name.
    /// </summary>
    internal void ExportLook(CharacterDraft draft, string installRoot)
    {
        string name = draft.Name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            player.Notice($"Can't export: '{name}' is not a valid file name.", true);
            return;
        }
        string relative = ExportFolder + name + BodyExtension;
        try
        {
            Directory.CreateDirectory(Path.Combine(installRoot, ExportFolder));
            Data.Fcs.FcsWriter.WriteFile(draft.Export(), Path.Combine(installRoot, relative));
            player.Notice(Loc.Tr("Character '{1}' exported to file {2}", name, "./" + relative));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            player.Notice($"Can't export '{name}': {e.Message}", true);
        }
    }

    /// <summary>
    /// IMPORT opens the dialog on the export folder.
    /// </summary>
    internal void ShowExportFolder(string installRoot) => ShowImportFolder("./" + ExportFolder.TrimEnd('/'), installRoot);

    /// <summary>
    /// Shows <paramref name="folder"/> in the import dialog (<c>FUN_14019fd70</c>): "[..]" and its folders as "[name]", then its body files by their
    /// file names, each group in ordinal order; the line ids are the paths (folder + "/" + name). Nothing is chosen.
    /// </summary>
    void ShowImportFolder(string folder, string installRoot)
    {
        _importFolder = folder;
        _importChoice = null;
        string full = Path.Combine(installRoot, folder);
        var entries = new List<UiListItem>();
        try
        {
            var folders = new[] { ".." }.Concat(Directory.EnumerateDirectories(full)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal));
            foreach (string name in folders)
                entries.Add(new(FolderEntry + folder + "/" + name, "[" + name + "]"));
            foreach (string name in Directory.EnumerateFiles(full, "*" + BodyExtension).Select(Path.GetFileName).OfType<string>()
                    .Order(StringComparer.Ordinal))
                entries.Add(new(folder + "/" + name, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        _importEntries = entries;
    }

    /// <summary>
    /// IMPORT (<c>FUN_1405f4280</c>): the chosen file's look replaces the draft's, race and sex included. A file without an appearance record fails
    /// with the original's message. Meitou: a file of a race the editor doesn't offer, or (at a surgeon, where race and sex are fixed) of another
    /// race or sex, is refused.
    /// </summary>
    void ImportLook(string path)
    {
        if (panel.Editor is not { } s || panel.Draft(s.Current) is not { } draft || panel.EditorData is not { } data) return;
        string file = Path.GetFileNameWithoutExtension(path);
        Data.Fcs.FcsRecord appearance;
        try
        {
            appearance = AppearanceFile.Read(Path.Combine(data.InstallRoot, path)).Record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Data.Fcs.FcsFormatException or InvalidDataException)
        {
            player.Notice(Loc.Tr("Error loading appearance file '{1}'. The file is in an old format or invalid.", file), true);
            return;
        }
        bool female = appearance.Bools.TryGetValue("sex female", out var f) && f;
        bool allowed = CharacterDraft.RaceOf(data.Db, appearance) is { } race && (s.Kind == EditKind.Creation
            ? CharacterEditorPanel.IndexOf(panel.EditorRaces(data.Db), race) >= 0
            : race.StringId == draft.Race.StringId && (female == draft.Female || race.GetBool("single gender")));
        if (!allowed || !draft.Import(appearance))
        {
            player.Notice($"Can't import '{file}': this character can't take that race or sex.", true);
            return;
        }
        player.FaithfulScreens.Close("file");
    }

    /// <summary>
    /// The import dialog's buttons and list: a folder line opens on a double click or IMPORT, a file line imports; the up arrow cuts the folder at
    /// its last separator.
    /// </summary>
    internal void FileDialogAction(MyGuiEvent e)
    {
        if (panel.EditorData is not { } data) return;
        if (e.Widget == "ButtonCancel") player.FaithfulScreens.Close("file");
        else if (e.Widget == "ButtonUp" && _importFolder.LastIndexOfAny(['/', '\\']) is var cut and > 0)
            ShowImportFolder(_importFolder[..cut], data.InstallRoot);
        else if (e.Widget == "ListFiles" && e.Action == "select") _importChoice = e.Text;
        else if (e.Widget == "ListFiles" && e.Action == "accept" || e.Widget == "ButtonOpenSave")
        {
            if (_importChoice is not { } choice) return;
            if (choice.StartsWith(FolderEntry, StringComparison.Ordinal)) ShowImportFolder(choice[FolderEntry.Length..], data.InstallRoot);
            else ImportLook(choice);
        }
    }

    /// <summary>
    /// The import dialog (<c>Kenshi_OpenSaveFileDialog</c>, titled "Import character", <c>FUN_1405f4dd0</c>, <c>FUN_1401a0b10</c>): the folder in the
    /// combo box, the up arrow, the list, IMPORT and CANCEL; no file name box (it opens files).
    /// </summary>
    internal void BindFileDialog(UiBindings b)
    {
        b.TemplateScale = player.UiScale;
        b.Text["Root"] = "Import character";
        b.Text["ButtonOpenSave"] = Loc.Tr("Import").ToUpperInvariant();
        b.Text["ButtonCancel"] = Loc.Tr("Cancel").ToUpperInvariant();
        foreach (string name in new[] { "ButtonCancel", "ButtonOpenSave", "ButtonUp" })
        {
            b.Disabled.Remove(name);
            b.Actions[name] = name;
        }
        if (_importChoice is null) b.Disabled.Add("ButtonOpenSave");
        b.Hidden.Add("EditFileName");
        b.Text["CurrentFolder"] = _importFolder;
        b.Disabled.Add("CurrentFolder");
        b.Lists["ListFiles"] = _importEntries;
        b.SelectedItems["ListFiles"] = _importEntries.ToList().FindIndex(i => i.Id == _importChoice);
        b.Properties["ButtonOpenSave"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
    }
}
