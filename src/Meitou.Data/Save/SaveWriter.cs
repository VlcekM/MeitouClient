namespace Meitou.Data.Save;

/// <summary>
/// Writes a <see cref="SaveGame"/> into a save folder the way the game does (docs/formats/save.md "The save file system").
/// </summary>
public sealed class SaveWriter(SaveGame game)
{
    /// <summary>
    /// The save it writes.
    /// </summary>
    readonly SaveGame _game = game;

    /// <summary>
    /// Writes the save into <paramref name="folder"/> the way the game does (docs/formats/save.md "The save file system"): first into a working
    /// folder <c>_current&lt;N&gt;</c> beside it (the first N whose folder does not exist), then the finished folder replaces the old one in two
    /// renames, so a crash leaves either the old save or the new one, never half of one. The old folder's contents are replaced as a whole: files the
    /// new save no longer has are gone. <paramref name="beforeInstall"/> runs between the two renames (a test uses it to fail there); the old save is
    /// then moved back.
    /// </summary>
    public void Write(string folder, Action? beforeInstall = null)
    {
        folder = Path.GetFullPath(folder);
        var work = CreateWorkFolder(folder);
        string? old = null;
        try
        {
            WriteFiles(work);

            if (Directory.Exists(folder))
            {
                old = folder + ".old";
                if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
                Directory.Move(folder, old);
            }
            beforeInstall?.Invoke();
            Directory.Move(work, folder);
        }
        catch
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            // The old save goes back if the new one could not be moved in.
            if (old is not null && !Directory.Exists(folder) && Directory.Exists(old)) Directory.Move(old, folder);
            throw;
        }
        DeleteOld(old);
    }

    /// <summary>
    /// Makes the working folder <c>_current&lt;N&gt;</c> beside <paramref name="folder"/>: the first N whose folder does not exist.
    /// </summary>
    static string CreateWorkFolder(string folder)
    {
        var parent = Path.GetDirectoryName(folder) ?? throw new ArgumentException("A save folder needs a parent folder.", nameof(folder));
        Directory.CreateDirectory(parent);
        int n = 1;
        string work;
        while (Directory.Exists(work = Path.Combine(parent, "_current" + n))) n++;
        Directory.CreateDirectory(work);
        return work;
    }

    /// <summary>
    /// Every file of the save into the working folder: the record files, the portraits, the mod data and the level edits.
    /// </summary>
    void WriteFiles(string work)
    {
        Directory.CreateDirectory(Path.Combine(work, SaveFolder.PlatoonDirectory));
        Directory.CreateDirectory(Path.Combine(work, SaveFolder.ZoneDirectory));
        foreach (var (name, file) in _game.AllFiles())
            file.WriteFile(Path.Combine(work, name.Replace('/', Path.DirectorySeparatorChar)));
        if (_game.Portraits is not null) File.WriteAllBytes(Path.Combine(work, SaveFolder.PortraitName), _game.Portraits);
        WriteModData(work);
        WriteLevelEdits(work);
    }

    /// <summary>
    /// The code mods' sidecar files and the mod environment, when the save has any.
    /// </summary>
    void WriteModData(string work)
    {
        if (_game.ModData.Count == 0 && _game.ModEnvironment is null) return;
        Directory.CreateDirectory(Path.Combine(work, SaveFolder.ModDataDirectory));
        foreach (var (id, bytes) in _game.ModData)
            File.WriteAllBytes(Path.Combine(work, SaveFolder.ModDataName(id).Replace('/', Path.DirectorySeparatorChar)), bytes);
        if (_game.ModEnvironment is not null)
            File.WriteAllBytes(Path.Combine(work, SaveFolder.ModEnvironmentName.Replace('/', Path.DirectorySeparatorChar)), _game.ModEnvironment);
    }

    /// <summary>
    /// The level editor's edits, one file per zone, when the save has any.
    /// </summary>
    void WriteLevelEdits(string work)
    {
        if (_game.LevelEdits.Count == 0) return;
        Directory.CreateDirectory(Path.Combine(work, SaveFolder.LevelEditsDirectory));
        foreach (var (zone, bytes) in _game.LevelEdits)
            File.WriteAllBytes(Path.Combine(work, SaveFolder.LevelEditsName(zone).Replace('/', Path.DirectorySeparatorChar)), bytes);
    }

    /// <summary>
    /// Removes the replaced save's <c>.old</c> folder; one that cannot go yet is removed by the next write.
    /// </summary>
    static void DeleteOld(string? old)
    {
        if (old is null) return;
        try
        {
            Directory.Delete(old, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            /* a leftover .old folder is removed by the next write */
        }
    }
}
