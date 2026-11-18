namespace ProvingGrounds.Core.Glue;

/// <summary>
/// Core's <see cref="IConfigFiles"/> over the real disk (task M2.6): the file steps of PGConfig.cpp that Core sequences (ReadText :436-464,
/// the default-file write :614-619, the settings save's temporary file, MoveFileExW and DeleteFileW :1060-1068). Infrastructure with no game
/// types, so it lives in Core/Glue and is tested there (Tests/Glue/PgConfigFilesTests.cs). Every call returns a status; nothing throws into Load.
/// <para>Missing vs Unreadable (M1 note, PGConfig.cpp:439-446): <see cref="ConfigReadStatus.Missing"/> ONLY when the file does not exist
/// (the C++ checks GetFileAttributesW for ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND); a locked, denied or failing read is Unreadable, so
/// Load never writes the shipped defaults over a file it merely could not open.</para>
/// <para>Atomic writes: <see cref="Write"/> writes a temporary file beside the target, flushes it to disk and moves it over the target
/// (<c>File.Move(.., overwrite: true)</c> = MoveFileExW with MOVEFILE_REPLACE_EXISTING, the call Core's <see cref="IConfigFiles.MoveReplace"/>
/// contract names), so a crash mid-write never leaves a half file where the C++'s in-place <c>fopen "wb"</c> could. The status Core sees is
/// the same: NotOpened when the temporary file cannot be created, Incomplete when writing, flushing or the move fails (the temporary file is
/// deleted then). The flush to disk stands in for MOVEFILE_WRITE_THROUGH. The final move replaces an existing file only for Core's
/// temporary file (a path ending ".tmp", which the C++ truncates with "wb"); the default file is written only if no file appeared since
/// Load found none (a file created meanwhile, by the player or another process, is kept and the write reports Incomplete).</para>
/// </summary>
public sealed class PgConfigFiles : IConfigFiles
{
    // C++: PGConfig.cpp:452-453 (the 1 MiB guard; Core applies it too: refusing a larger file before reading it gives the same result)
    const long MaxBytes = 1024 * 1024;

    // C++: PGConfig.cpp:436-464 (ReadText)
    public ConfigRead Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes) return new ConfigRead(ConfigReadStatus.Unreadable, null);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return new ConfigRead(ConfigReadStatus.Read, bytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException && !File.Exists(path))
        {
            return new ConfigRead(ConfigReadStatus.Missing, null);
        }
        catch (Exception)
        {
            return new ConfigRead(ConfigReadStatus.Unreadable, null);
        }
    }

    // C++: PGConfig.cpp:614-619 and :1062-1066 (_wfopen_s "wb", fwrite, fclose), made atomic (class comment)
    public ConfigWriteStatus Write(string path, byte[] bytes)
    {
        string temporary = path + ".write.tmp";
        FileStream stream;
        try { stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None); }
        catch (Exception) { return ConfigWriteStatus.NotOpened; }
        try
        {
            using (stream)
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: path.EndsWith(".tmp", StringComparison.Ordinal));
            return ConfigWriteStatus.Written;
        }
        catch (Exception)
        {
            Delete(temporary);
            return ConfigWriteStatus.Incomplete;
        }
    }

    // C++: PGConfig.cpp:1064 MoveFileExW(temporaryPath, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)
    public bool MoveReplace(string source, string destination)
    {
        try { File.Move(source, destination, overwrite: true); return true; }
        catch (Exception) { return false; }
    }

    // C++: PGConfig.cpp:1066 DeleteFileW (result ignored)
    public void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { }
    }
}
