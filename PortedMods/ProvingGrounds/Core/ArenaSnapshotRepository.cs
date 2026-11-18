// Port of upstream/ProvingGrounds/plugin/src/ArenaSnapshotRepository.h and ArenaSnapshotRepository.cpp (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

// Loading and publishing a snapshot over abstract storage: the sidecar file, its backups, and the rules for recovering a matching
// snapshot from a backup. The C++ implements Storage with real files (ArenaSnapshotFiles.cpp, dropped: Meitou keeps mod data in the
// save, port map); the M2 save glue implements IStorage or calls the codec directly. Texts are bytes, as in the codec.
// The out-parameters are `ref`, as the C++ passes std::string& that keep their value when a call does not assign them: a Storage
// that fails without setting `error` leaves the message an earlier call wrote, and LoadSnapshot / PublishSnapshot report that.
public static partial class ArenaPersistence
{
    /// <summary>C++: ArenaSnapshotRepository.h:9-14</summary>
    public enum ReadCode
    {
        ReadOk,
        NotFound,
        ReadFailed,
    }

    /// <summary>C++: ArenaSnapshotRepository.h:16-27 <c>struct Storage</c> (pure virtual). On <see cref="ReadCode.ReadOk"/>,
    /// <c>Read</c> must set <c>text</c> to the file's bytes (never null; a null is read as an empty text). Notes for the M2 glue that
    /// implements it are in the header of ArenaSnapshotJson.cs (config before load, bounded stream reads, encode a game-thread Clone).</summary>
    public interface IStorage
    {
        ReadCode Read(string path, ref byte[] text, ref string error);
        bool ListBackups(string path, ref List<string> paths, ref string error);
        bool Preserve(string path, ref string backupPath, ref string error);
        bool Replace(string path, byte[] text, ref string error);
    }

    // C++: ArenaSnapshotRepository.cpp:8-68
    static LoadResult RecoverSnapshot(IStorage files,
        List<string> backups,
        string saveKey,
        string nativeGeneration,
        string noMatchMessage,
        LoadResult? generationMatch)
    {
        var result = new LoadResult();
        result.Code = LoadCode.WrongSnapshot;
        result.Snapshot.SaveKey = saveKey;
        result.Snapshot.Generation = nativeGeneration;
        result.Message = noMatchMessage;

        var rebound = new LoadResult();
        bool hasRebound = false;
        if (generationMatch != null && generationMatch.Code == LoadCode.Ready)
        {
            rebound = generationMatch.Clone();
            hasRebound = true;
        }

        for (int i = 0; i < backups.Count; ++i)
        {
            byte[] text = System.Array.Empty<byte>();
            string error = "";
            ReadCode read = files.Read(backups[i], ref text, ref error);
            if (read == ReadCode.ReadFailed)
            {
                result.Code = LoadCode.IoFailure;
                result.Message = error.Length == 0 ?
                    "Could not read arena snapshot backup " + backups[i] :
                    error;
                return result;
            }
            if (read == ReadCode.NotFound)
                continue;

            LoadResult candidate = DecodeSnapshot(text ?? System.Array.Empty<byte>());
            if (candidate.Code != LoadCode.Ready ||
                candidate.Snapshot.Generation != nativeGeneration)
                continue;
            if (candidate.Snapshot.SaveKey != saveKey)
            {
                if (nativeGeneration.Length != 0 && !hasRebound)
                {
                    rebound = candidate.Clone();
                    rebound.Message =
                        "Recovered generation-matched snapshot copied from save '" +
                        candidate.Snapshot.SaveKey + "' at " + backups[i];
                    hasRebound = true;
                }
                continue;
            }
            candidate.Message = "Recovered matching snapshot from " +
                backups[i];
            return candidate;
        }
        if (hasRebound)
            return rebound;
        return result;
    }

    /// <summary>C++: ArenaSnapshotRepository.h:29-30 / .cpp:71-141</summary>
    public static LoadResult LoadSnapshot(IStorage files, string path,
        string saveKey, string nativeGeneration)
    {
        var result = new LoadResult();
        result.Snapshot.SaveKey = saveKey;
        result.Snapshot.Generation = nativeGeneration;

        byte[] text = System.Array.Empty<byte>();
        string error = "";
        ReadCode read = files.Read(path, ref text, ref error);
        if (read == ReadCode.ReadFailed)
        {
            result.Code = LoadCode.IoFailure;
            result.Message = error.Length == 0 ? "Could not read arena snapshot" : error;
            return result;
        }
        if (read == ReadCode.NotFound)
        {
            if (nativeGeneration.Length == 0)
            {
                result.Code = LoadCode.Missing;
                result.Message = "No arena snapshot exists for a fresh world";
                return result;
            }

            var backups = new List<string>();
            if (!files.ListBackups(path, ref backups, ref error))
            {
                result.Code = LoadCode.IoFailure;
                result.Message = error.Length == 0 ?
                    "Could not inspect arena snapshot backups" : error;
                return result;
            }
            return RecoverSnapshot(files, backups, saveKey,
                nativeGeneration,
                "Native snapshot has no matching arena sidecar", null);
        }

        LoadResult parsed = DecodeSnapshot(text ?? System.Array.Empty<byte>());
        if (parsed.Code != LoadCode.Ready)
            return parsed;
        if (parsed.Snapshot.SaveKey == saveKey &&
            parsed.Snapshot.Generation == nativeGeneration)
            return parsed;

        LoadResult? generationMatchPtr = null;
        if (nativeGeneration.Length != 0 &&
            parsed.Snapshot.Generation == nativeGeneration)
        {
            LoadResult generationMatch = parsed.Clone();
            generationMatch.Message =
                "Recovered generation-matched snapshot copied from save '" +
                parsed.Snapshot.SaveKey + "'";
            generationMatchPtr = generationMatch;
        }

        var list = new List<string>();
        if (!files.ListBackups(path, ref list, ref error))
        {
            result.Code = LoadCode.IoFailure;
            result.Message = error.Length == 0 ?
                "Could not inspect arena snapshot backups" : error;
            return result;
        }
        string mismatch = parsed.Snapshot.SaveKey != saveKey ?
            "Arena sidecar belongs to a different native save" :
            "Arena sidecar generation does not match the native snapshot";
        return RecoverSnapshot(files, list, saveKey, nativeGeneration,
            mismatch, generationMatchPtr);
    }

    /// <summary>C++: ArenaSnapshotRepository.h:31-32 / .cpp:143-178. <paramref name="error"/> is the caller's string, as in the C++
    /// (EncodeSnapshot empties it on success; a later storage call that fails without writing it leaves it so).</summary>
    public static bool PublishSnapshot(IStorage files, string path,
        Snapshot snapshot,
        ref string error)
    {
        byte[] text = System.Array.Empty<byte>();
        if (!EncodeSnapshot(snapshot, ref text, out error))
            return false;

        byte[] currentText = System.Array.Empty<byte>();
        ReadCode read = files.Read(path, ref currentText, ref error);
        if (read == ReadCode.ReadFailed)
        {
            if (error.Length == 0)
                error = "Could not inspect the existing arena snapshot";
            return false;
        }
        if (read == ReadCode.NotFound)
            return files.Replace(path, text, ref error);

        LoadResult current = DecodeSnapshot(currentText ?? System.Array.Empty<byte>());
        if (current.Code != LoadCode.Ready && current.Code != LoadCode.LegacyReset)
        {
            error = current.Field;
            if (error.Length != 0 && current.Message.Length != 0)
                error += ": ";
            error += current.Message.Length == 0 ?
                "Existing arena snapshot is not replaceable" :
                current.Message;
            return false;
        }

        string backupPath = "";
        if (!files.Preserve(path, ref backupPath, ref error))
            return false;
        return files.Replace(path, text, ref error);
    }
}
