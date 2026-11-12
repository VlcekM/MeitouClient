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
    /// The file in a mod's folder that makes it an RE_Kenshi plugin mod (docs/formats/rekenshi.md); Meitou ignores its DLLs.
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
                if (file is not null)
                    mods[Path.GetFileNameWithoutExtension(file)] = new ModInfo(Path.GetFileNameWithoutExtension(file), file, ModSource.Workshop);
            }
        if (Directory.Exists(install.ModsDirectory))
            foreach (var folder in Directory.EnumerateDirectories(install.ModsDirectory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(folder);
                var file = Path.Combine(folder, name + ".mod");
                if (File.Exists(file))
                    mods[name] = new ModInfo(name, file, ModSource.Local);
            }
        return [.. mods.Values.OrderBy(m => m.Name, StringComparer.Ordinal)];
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
        return File.Exists(path) ? [.. ParseModsCfg(File.ReadAllLines(path)).Distinct(StringComparer.Ordinal)] : [];
    }

    /// <summary>
    /// Names the launcher has seen, from <c>data/__mods.list</c> (one name per line, no extension); empty when missing.
    /// </summary>
    public static HashSet<string> ReadKnown(GameInstall install)
    {
        var path = Path.Combine(install.DataDirectory, "__mods.list");
        return File.Exists(path) ? new HashSet<string>(File.ReadAllLines(path).Where(l => l.Length > 0),
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
    /// Writes <paramref name="lines"/> to <paramref name="path"/>, each ended by CRLF, as UTF-8 without a BOM.
    /// </summary>
    static void WriteLines(string path, IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line).Append("\r\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Kahn's algorithm over the mods' header dependencies, stable in name order; names not in the list are ignored. Mods still waiting in a
    /// dependency cycle come last, in name order.
    /// </summary>
    static IEnumerable<ModInfo> DependencyOrder(List<ModInfo> mods)
    {
        var byFile = mods.ToDictionary(m => m.Name + ".mod", StringComparer.Ordinal);
        var waiting = mods.ToDictionary(m => m,
                m => m.Header?.Dependencies.Where(byFile.ContainsKey).Select(d => byFile[d]).Where(d => d != m).ToHashSet() ?? []);
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
    /// <see cref="HasNativePlugin"/> once checked; null before the first use.
    /// </summary>
    bool? _nativePlugin;
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
    /// An RE_Kenshi plugin mod: its data loads in Meitou, its DLLs do not. Checked on first use.
    /// </summary>
    public bool HasNativePlugin => _nativePlugin ??= File.Exists(System.IO.Path.Combine(Folder, ModList.NativePluginFile));

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
/// The author, description, dependencies and references of a type 16/17 <c>.mod</c> header (docs/formats/fcs-mod.md, "File").
/// </summary>
public sealed record ModHeader(int FileType, string Author, string Description, IReadOnlyList<string> Dependencies, IReadOnlyList<string> References)
{
    /// <summary>
    /// Reads the header at the start of the <c>.mod</c> file at <paramref name="path"/>; null when the file is unreadable, not type 16 or 17, or has
    /// a bad string length.
    /// </summary>
    public static ModHeader? TryRead(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            int type = r.ReadInt32();
            if (type is not (16 or 17)) return null;
            if (type == 17) r.ReadUInt32(); // header length
            r.ReadInt32(); // version
            string author = ReadString(r), description = ReadString(r);
            return new ModHeader(type, author, description, Split(ReadString(r)), Split(ReadString(r)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a header string: an int32 byte length (0..1 MiB, else <see cref="InvalidDataException"/>) and that many UTF-8 bytes.
    /// </summary>
    static string ReadString(BinaryReader r)
    {
        int length = r.ReadInt32();
        if (length is < 0 or > 1 << 20) throw new InvalidDataException("bad string length");
        return Encoding.UTF8.GetString(r.ReadBytes(length));
    }

    /// <summary>
    /// Splits a header's comma-separated mod list into trimmed, non-empty names.
    /// </summary>
    static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}