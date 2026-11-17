using System.Text;

namespace Meitou.Content;

/// <summary>
/// The mods on disk and the game's mod list files, <c>data/mods.cfg</c> and <c>data/__mods.list</c>, with the rules of the original
/// game and its launcher (docs/formats/overview.md, "Load order"). Shared by the game's load order and the launcher.
/// </summary>
public static class ModList
{
    /// <summary>
    /// mods.cfg entries the game ignores because they name core files.
    /// </summary>
    public static readonly IReadOnlyList<string> CoreModNames = ["Newwworld", "Dialogue", "rebirth"];

    /// <summary>
    /// The RE_Kenshi file in a mod's folder (docs/formats/rekenshi.md, <see cref="ReKenshiFile"/>): Meitou applies its file rebinds and ignores
    /// its DLLs.
    /// </summary>
    public const string NativePluginFile = "RE_Kenshi.json";

    /// <summary>
    /// The file in a mod's folder that makes it a Meitou code mod (docs/modding.md "Discovery").
    /// </summary>
    public const string CodeManifestFile = "meitou.json";

    /// <summary>
    /// Mod names (without <c>.mod</c>) from <c>mods.cfg</c>, with the game's rules: lines are taken as they are
    /// (not trimmed); empty lines and lines starting with <c>#</c>, <c>/</c> or <c>;</c> are skipped; the text
    /// after the last dot must be exactly <c>mod</c>; core files are skipped. Duplicates are kept here.
    /// </summary>
    public static IEnumerable<string> ParseModsCfg(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] is '#' or '/' or ';') continue;
            int dot = line.LastIndexOf('.');
            if (dot < 0 || line[(dot + 1)..] != "mod") continue;
            var name = line[..dot];
            if (!CoreModNames.Contains(name)) yield return name;
        }
    }

    /// <summary>
    /// Mods on disk by name (case-sensitive, as in the game): workshop items first, each the first <c>*.mod</c>
    /// file of its folder in sorted order; then <c>mods/&lt;name&gt;/&lt;name&gt;.mod</c>, which replaces a workshop
    /// mod of the same name. The game asks Steam for the subscribed items; we take every downloaded item.
    /// </summary>
    public static Dictionary<string, string> FindMods(GameInstall install) =>
        Discover(install).ToDictionary(m => m.Name, m => m.Path, StringComparer.Ordinal);

    /// <summary>
    /// <see cref="FindMods"/> with each mod's source and whether it is an RE_Kenshi plugin mod, in name order.
    /// </summary>
    public static List<ModInfo> Discover(GameInstall install)
    {
        var mods = new Dictionary<string, ModInfo>(StringComparer.Ordinal);
        if (install.WorkshopDirectory is { } workshop)
            foreach (var item in Directory.EnumerateDirectories(workshop).Order(StringComparer.Ordinal))
            {
                var file = Directory.EnumerateFiles(item)
                    .Where(f => Path.GetExtension(f) == ".mod")
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();
                if (file is not null) AddWorkshopMod(mods, file);
            }
        if (Directory.Exists(install.ModsDirectory))
            foreach (var folder in Directory.EnumerateDirectories(install.ModsDirectory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(folder);
                var file = Path.Combine(folder, name + ".mod");

                // A name the code page cannot hold becomes ?s in the original, and that path names no file.
                if (GameNameEncoding.CanEncode(name) && File.Exists(file))
                    mods[name] = new ModInfo(name, file, ModSource.Local);
            }
        return [.. mods.Values.OrderBy(m => m.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Adds the Workshop mod of <paramref name="file"/> under its file stem as the original has it: a stem the code page cannot hold keeps its
    /// <c>?</c>s, and so does its path, which then names no file and fails to load.
    /// </summary>
    static void AddWorkshopMod(Dictionary<string, ModInfo> mods, string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        if (!GameNameEncoding.CanEncode(name))
        {
            name = GameNameEncoding.AsGameName(name);
            file = Path.Combine(Path.GetDirectoryName(file)!, name + ".mod");
        }

        mods[name] = new ModInfo(name, file, ModSource.Workshop);
    }

    /// <summary>
    /// The lines of the name list <paramref name="path"/> (<c>mods.cfg</c>, <c>__mods.list</c>) in the code page names are kept in
    /// (<see cref="GameNameEncoding"/>), taken as bytes: a byte-order mark is part of the first line, as in the original.
    /// </summary>
    public static List<string> ReadLines(string path)
    {
        using var reader = new StreamReader(path, GameNameEncoding.Encoding, detectEncodingFromByteOrderMarks: false);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// The file the enabled mods are read from instead of <c>data/mods.cfg</c> (<c>--mods-cfg</c>, <see cref="UseModsCfg"/>); null: the install's
    /// own. Saving always writes the install's file.
    /// </summary>
    public static string? ModsCfgOverride { get; private set; }

    /// <summary>
    /// Reads the enabled mods from <paramref name="file"/> from now on (<c>--mods-cfg</c>, <c>MEITOU_MODS_CFG</c>); a missing file is an error, not
    /// an empty list.
    /// </summary>
    public static void UseModsCfg(string file)
    {
        file = Path.GetFullPath(file);
        if (!File.Exists(file)) throw new FileNotFoundException($"mods.cfg file not found: {file}", file);
        ModsCfgOverride = file;
    }

    /// <summary>
    /// Back to the install's own <c>data/mods.cfg</c> (tests).
    /// </summary>
    public static void ClearModsCfg() => ModsCfgOverride = null;

    /// <summary>
    /// The file the enabled mods are read from: <see cref="ModsCfgOverride"/>, else <c>data/mods.cfg</c>.
    /// </summary>
    public static string ModsCfgPath(GameInstall install) => ModsCfgOverride ?? Path.Combine(install.DataDirectory, "mods.cfg");

    /// <summary>
    /// The enabled mods named in <c>data/mods.cfg</c> (or the <c>--mods-cfg</c> file), in order, without duplicates; empty when the file is missing.
    /// </summary>
    public static List<string> ReadEnabled(GameInstall install)
    {
        var path = ModsCfgPath(install);
        return File.Exists(path) ? [.. ParseModsCfg(ReadLines(path)).Distinct(StringComparer.Ordinal)] : [];
    }

    /// <summary>
    /// Names the launcher has seen, from <c>data/__mods.list</c> (one name per line, no extension); empty when missing.
    /// </summary>
    public static HashSet<string> ReadKnown(GameInstall install)
    {
        var path = Path.Combine(install.DataDirectory, "__mods.list");
        return File.Exists(path) ? new HashSet<string>(ReadLines(path).Where(l => l.Length > 0),
                StringComparer.Ordinal) : new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The mod list as the original launcher shows it: the enabled mods in <c>mods.cfg</c> order, then the others sorted so a mod
    /// comes after its dependencies (unknown names dropped; Meitou keeps mods in a dependency cycle at the end instead of dropping
    /// them). A mod not in <c>__mods.list</c> is new and starts enabled. Names in <c>mods.cfg</c> that are not on disk are left out.
    /// </summary>
    public static List<ModEntry> LauncherOrder(GameInstall install, IReadOnlyList<ModInfo> mods)
    {
        var byName = mods.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var known = ReadKnown(install);
        var result = new List<ModEntry>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in ReadEnabled(install))
            if (byName.TryGetValue(name, out var mod) && placed.Add(name))
                result.Add(new ModEntry(mod, true));
        foreach (var mod in DependencyOrder([.. mods.Where(m => !placed.Contains(m.Name))]))
            result.Add(new ModEntry(mod, !known.Contains(mod.Name)));
        return result;
    }

    /// <summary>
    /// Writes <c>mods.cfg</c> (the enabled mods' <c>&lt;name&gt;.mod</c> in list order) and <c>__mods.list</c> (every listed name, plus
    /// names it already had that are not on disk now, as the original does without Steam). CRLF, as the Windows game writes them.
    /// </summary>
    public static void Save(GameInstall install, IEnumerable<ModEntry> entries)
    {
        var list = entries.ToList();
        var known = ReadKnown(install);
        var names = list.Select(e => e.Mod.Name).ToList();
        names.AddRange(known.Where(n => !names.Contains(n, StringComparer.Ordinal)).Order(StringComparer.Ordinal));
        WriteLines(Path.Combine(install.DataDirectory, "mods.cfg"), list.Where(e => e.Enabled).Select(e => e.Mod.Name + ".mod"));
        WriteLines(Path.Combine(install.DataDirectory, "__mods.list"), names);
    }

    /// <summary>
    /// Writes <paramref name="lines"/> to <paramref name="path"/>, each ended by CRLF, in the code page names are kept in
    /// (<see cref="GameNameEncoding"/>).
    /// </summary>
    static void WriteLines(string path, IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line).Append("\r\n");
        File.WriteAllText(path, text.ToString(), GameNameEncoding.Encoding);
    }

    /// <summary>
    /// Kahn's algorithm over the mods' header dependencies, stable in name order; a dependency names a mod by its text before the last dot (the
    /// launcher strips the extension), names not in the list are ignored. Mods still waiting in a dependency cycle come last, in name order.
    /// </summary>
    static IEnumerable<ModInfo> DependencyOrder(List<ModInfo> mods)
    {
        var byName = mods.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var waiting = mods.ToDictionary(m => m, m => DependedOn(m, byName));
        while (waiting.Count > 0)
        {
            var ready = waiting.Where(p => p.Value.Count == 0).Select(p => p.Key).OrderBy(m => m.Name, StringComparer.Ordinal).FirstOrDefault();
            if (ready is null) break;
            waiting.Remove(ready);
            foreach (var deps in waiting.Values) deps.Remove(ready);
            yield return ready;
        }
        foreach (var mod in waiting.Keys.OrderBy(m => m.Name, StringComparer.Ordinal))
            yield return mod;
    }

    /// <summary>
    /// The mods of <paramref name="byName"/> that <paramref name="mod"/>'s header depends on, itself left out.
    /// </summary>
    static HashSet<ModInfo> DependedOn(ModInfo mod, Dictionary<string, ModInfo> byName)
    {
        var deps = new HashSet<ModInfo>();
        foreach (var dependency in mod.Header?.Dependencies ?? [])
            if (byName.TryGetValue(StripExtension(dependency), out var other) && other != mod) deps.Add(other);
        return deps;
    }

    /// <summary>
    /// <paramref name="name"/> without the text from its last dot, unless that dot is the first character (the launcher's rule for dependencies).
    /// </summary>
    static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 1 ? name[..dot] : name;
    }
}

/// <summary>
/// Where a mod was found.
/// </summary>
public enum ModSource
{
    /// <summary>
    /// A folder of the install's <c>mods</c> directory.
    /// </summary>
    Local,

    /// <summary>
    /// A downloaded Steam workshop item of Kenshi.
    /// </summary>
    Workshop
}

/// <summary>
/// A mod on disk: its name (the <c>.mod</c> file's stem), file and source.
/// </summary>
public sealed class ModInfo(string name, string path, ModSource source)
{
    /// <summary>
    /// The <c>.mod</c> header once <see cref="Header"/> has read it; null before that or when unreadable.
    /// </summary>
    ModHeader? _header;
    /// <summary>
    /// Whether <see cref="Header"/> has tried to read the file yet (the header may be null after it did).
    /// </summary>
    bool _headerRead;
    /// <summary>
    /// <see cref="ReKenshi"/> once read; null before that or when the mod has none.
    /// </summary>
    ReKenshiFile? _rekenshi;
    /// <summary>
    /// Whether <see cref="ReKenshi"/> has tried to read the file yet.
    /// </summary>
    bool _rekenshiRead;
    /// <summary>
    /// <see cref="HasCode"/> once checked; null before the first use.
    /// </summary>
    bool? _code;

    /// <summary>
    /// The mod's name: the <c>.mod</c> file's name without its extension, as <c>mods.cfg</c> lists it.
    /// </summary>
    public string Name { get; } = name;
    /// <summary>
    /// Full path of the mod's <c>.mod</c> file.
    /// </summary>
    public string Path { get; } = path;
    /// <summary>
    /// Where the mod was found: the install's <c>mods</c> folder or a Steam workshop item.
    /// </summary>
    public ModSource Source { get; } = source;
    /// <summary>
    /// The folder the <c>.mod</c> file sits in, which also holds the mod's other files.
    /// </summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>
    /// An RE_Kenshi plugin mod: its <see cref="ReKenshi"/> file names DLLs, which Meitou does not load (its data does).
    /// </summary>
    public bool HasNativePlugin => ReKenshi is { HasPlugins: true };

    /// <summary>
    /// The mod's <see cref="ModList.NativePluginFile"/>, read on first use; null when it has none or it cannot be read.
    /// </summary>
    public ReKenshiFile? ReKenshi
    {
        get
        {
            if (!_rekenshiRead)
            {
                _rekenshi = ReKenshiFile.TryRead(Folder);
                _rekenshiRead = true;
            }
            return _rekenshi;
        }
    }

    /// <summary>
    /// A Meitou code mod: its folder has <see cref="ModList.CodeManifestFile"/>, so it runs C# code of its own. Checked on first use.
    /// </summary>
    public bool HasCode => _code ??= File.Exists(CodeManifestPath);

    /// <summary>
    /// Where the mod's <see cref="ModList.CodeManifestFile"/> is (or would be): in <see cref="Folder"/>.
    /// </summary>
    public string CodeManifestPath => System.IO.Path.Combine(Folder, ModList.CodeManifestFile);

    /// <summary>
    /// The <c>.mod</c> file's header, read on first use; null when it is unreadable.
    /// </summary>
    public ModHeader? Header
    {
        get
        {
            if (!_headerRead)
            {
                _header = ModHeader.TryRead(Path);
                _headerRead = true;
            }
            return _header;
        }
    }
}

/// <summary>
/// A mod in the launcher's list with whether it is enabled (<see cref="ModList.Save"/> writes the enabled ones to <c>mods.cfg</c>).
/// </summary>
public sealed record ModEntry(ModInfo Mod, bool Enabled);

/// <summary>
/// The author, description, dependencies and references of a <c>.mod</c> header as the original launcher reads it (docs/formats/fcs-mod.md, "File";
/// docs/formats/overview.md, "Name encoding").
/// </summary>
public sealed record ModHeader(int FileType, string Author, string Description, IReadOnlyList<string> Dependencies, IReadOnlyList<string> References)
{
    /// <summary>
    /// The launcher's string buffer: a longer string keeps its first size - 1 bytes (the references string all of them).
    /// </summary>
    const int StringSize = 2048;

    /// <summary>
    /// Reads the header at the start of the <c>.mod</c> file at <paramref name="path"/>; null when the file is unreadable or ends early, or its type
    /// is 15 or lower (the launcher reads a header above 15, a type-17 one after its length).
    /// </summary>
    public static ModHeader? TryRead(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            int type = r.ReadInt32();
            if (type <= 15) return null;
            if (type > 16) r.ReadUInt32(); // header length
            r.ReadInt32(); // version
            string author = Encoding.UTF8.GetString(ReadString(r, true));
            string description = Encoding.UTF8.GetString(ReadString(r, true));
            var dependencies = Split(ReadString(r, true));
            return new ModHeader(type, author, description, dependencies, Split(ReadString(r, false)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a header string as the launcher does: an int byte length, up to <see cref="StringSize"/> bytes whole, a longer string (or a negative
    /// length) cut and the rest skipped; then taken up to its first NUL. A longer string keeps size - 1 bytes when <paramref name="cutLast"/>.
    /// </summary>
    static byte[] ReadString(BinaryReader r, bool cutLast)
    {
        uint length = r.ReadUInt32();
        byte[] bytes;
        if (length <= StringSize)
        {
            bytes = r.ReadBytes((int)length);
            if (bytes.Length < length) throw new EndOfStreamException();
        }
        else
        {
            bytes = r.ReadBytes(StringSize);
            r.BaseStream.Seek((long)(int)length - StringSize, SeekOrigin.Current);
            if (cutLast) bytes = bytes[..(StringSize - 1)];
        }

        int nul = Array.IndexOf(bytes, (byte)0);
        return nul < 0 ? bytes : bytes[..nul];
    }

    /// <summary>
    /// Splits a header's comma-separated mod list, bytes in the code page names are kept in (<see cref="GameNameEncoding"/>), into its non-empty
    /// parts, untrimmed as the launcher keeps them.
    /// </summary>
    static string[] Split(byte[] list) => GameNameEncoding.Encoding.GetString(list).Split(',', StringSplitOptions.RemoveEmptyEntries);
}