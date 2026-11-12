using System.Text;

namespace Meitou.Content;

/// <summary>
/// A save slot the launcher can take a mod list from: a folder holding <c>quick.save</c>, under the root labelled <paramref name="Source"/>.
/// </summary>
public sealed record SaveModSlot(string Name, string Folder, DateTime ModifiedUtc, string Source);

/// <summary>
/// The mods a save was made with: the <c>mods</c> reference list of its CAMERA record (docs/formats/save.md "CAMERA"), read from
/// <c>quick.save</c> without the save reader of Meitou.Data, which the launcher does not ship. Records are walked one by one up to the
/// first CAMERA record; their byte sizes can't be used to skip them (the game writes the child count there).
/// </summary>
public static class SaveModList
{
    /// <summary>
    /// The FCS record type of a CAMERA record, the one holding the <c>mods</c> list.
    /// </summary>
    const int CameraType = 56;
    /// <summary>
    /// The largest count or string length <see cref="ReadCount"/> accepts (64 Mi); a larger one means the file is not read right.
    /// </summary>
    const int MaxCount = 64 * 1024 * 1024;

    /// <summary>
    /// The folders the game lists saves from: Meitou's own, the install's <c>save</c> folder and the original's per-user one.
    /// </summary>
    public static IReadOnlyList<(string Folder, string Label)> Roots(GameInstall install)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return [(Path.Combine(local, "Meitou", "save"), "Meitou"), (install.SaveDirectory, "Kenshi"),
            (Path.Combine(local, "kenshi", "save"), "Kenshi user")];
    }

    /// <summary>
    /// The save slots under <paramref name="roots"/>, newest first; a folder found twice is listed once.
    /// </summary>
    public static IReadOnlyList<SaveModSlot> Scan(IEnumerable<(string Folder, string Label)> roots)
    {
        var found = new Dictionary<string, SaveModSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, label) in roots.Where(r => Directory.Exists(r.Folder)))
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                string folder = Path.GetFullPath(directory), quick = Path.Combine(folder, "quick.save");
                if (File.Exists(quick))
                    found.TryAdd(folder, new SaveModSlot(Path.GetFileName(folder), folder, File.GetLastWriteTimeUtc(quick), label));
            }
        return [.. found.Values.OrderByDescending(s => s.ModifiedUtc).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The mods the save in <paramref name="folder"/> loaded, in load order, as <c>.mod</c> names without extension; the base game's
    /// files (<c>base</c>, <see cref="ModList.CoreModNames"/>) are left out. Null when the save has no CAMERA record or it has no <c>mods</c> list.
    /// </summary>
    public static List<string>? Read(string folder)
    {
        using var stream = new FileStream(Path.Combine(folder, "quick.save"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var r = new BinaryReader(stream);
        if (r.ReadInt32() != 15) throw new InvalidDataException("quick.save is not an FCS type 15 file.");
        r.ReadInt32(); // next id
        for (int n = ReadCount(r); n > 0; n--)
            if (ReadRecordMods(r, out var mods))
                return mods is null ? null : [.. mods.Where(m => m != "base" && !ModList.CoreModNames.Contains(m))];
        return null;
    }

    /// <summary>
    /// Skips one record; true for a CAMERA record, with its mods list (null without one). The fields are read in the save's
    /// record layout (docs/formats/save.md).
    /// </summary>
    static bool ReadRecordMods(BinaryReader r, out List<string>? mods)
    {
        r.ReadUInt32(); // byte size
        bool camera = r.ReadInt32() == CameraType;
        r.ReadInt32(); // id
        SkipString(r); // name
        SkipString(r); // string id
        r.ReadUInt32(); // flags
        SkipMap(r, 1);  // bools
        SkipMap(r, 4);  // floats
        SkipMap(r, 4);  // ints
        SkipMap(r, 12); // vec3
        SkipMap(r, 16); // vec4
        SkipStringMap(r); // strings
        SkipStringMap(r); // filenames

        mods = ReadReferences(r);
        SkipInstances(r);
        return camera;
    }

    /// <summary>
    /// The targets of the "mods" category, in order; null without one. Each reference is a target string and three ints.
    /// </summary>
    static List<string>? ReadReferences(BinaryReader r)
    {
        List<string>? mods = null;
        for (int c = ReadCount(r); c > 0; c--)
        {
            var list = ReadString(r) == "mods" ? new List<string>() : null;
            for (int n = ReadCount(r); n > 0; n--)
            {
                string target = ReadString(r);
                r.BaseStream.Seek(12, SeekOrigin.Current); // three ints
                list?.Add(target);
            }
            mods ??= list;
        }
        return mods;
    }

    /// <summary>
    /// Skips a record's instance list: per instance two strings, position and rotation (28 bytes) and a list of strings.
    /// </summary>
    static void SkipInstances(BinaryReader r)
    {
        for (int n = ReadCount(r); n > 0; n--)
        {
            SkipString(r); // id
            SkipString(r); // target
            r.BaseStream.Seek(28, SeekOrigin.Current); // position, rotation
            for (int s = ReadCount(r); s > 0; s--) SkipString(r);
        }
    }

    /// <summary>
    /// Skips a name-keyed map whose values are <paramref name="valueSize"/> bytes each.
    /// </summary>
    static void SkipMap(BinaryReader r, int valueSize)
    {
        for (int n = ReadCount(r); n > 0; n--)
        {
            SkipString(r);
            r.BaseStream.Seek(valueSize, SeekOrigin.Current);
        }
    }

    /// <summary>
    /// Skips a map of string keys to string values.
    /// </summary>
    static void SkipStringMap(BinaryReader r)
    {
        for (int n = ReadCount(r); n > 0; n--)
        {
            SkipString(r);
            SkipString(r);
        }
    }

    /// <summary>
    /// Skips a length-prefixed string.
    /// </summary>
    static void SkipString(BinaryReader r) => r.BaseStream.Seek(ReadCount(r), SeekOrigin.Current);

    /// <summary>
    /// Reads a length-prefixed UTF-8 string.
    /// </summary>
    static string ReadString(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(ReadCount(r)));

    /// <summary>
    /// Reads an int32 count or length; <see cref="InvalidDataException"/> when it is negative or over <see cref="MaxCount"/>.
    /// </summary>
    static int ReadCount(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n is < 0 or > MaxCount) throw new InvalidDataException($"Implausible count {n} in quick.save.");
        return n;
    }
}