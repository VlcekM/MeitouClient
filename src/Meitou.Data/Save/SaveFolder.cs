using System.Text.RegularExpressions;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// A save folder seen the way the game's save file system sees it (docs/formats/save.md "The save file system"): a stack of layer folders scanned
/// recursively into one map "relative name to file", where a later layer overrides an earlier one. The normal case is one layer, the folder itself.
/// Names are relative with <c>/</c> and compared case-insensitively (<c>quick.save</c>, <c>platoon/X.platoon</c>, <c>zone/zone.X.Y.zone</c>).
/// </summary>
public sealed partial class SaveFolder
{
    /// <summary>
    /// The world save's name: the CAMERA, factions, towns and platoon records.
    /// </summary>
    public const string QuickSaveName = "quick.save";

    /// <summary>
    /// The folder of the <c>.platoon</c> files.
    /// </summary>
    public const string PlatoonDirectory = "platoon";

    /// <summary>
    /// The folder of the <c>zone.X.Y.zone</c> files.
    /// </summary>
    public const string ZoneDirectory = "zone";

    /// <summary>
    /// The portrait atlas's name (<see cref="SavePortraits"/>).
    /// </summary>
    public const string PortraitName = "portraits_texture.png";

    /// <summary>
    /// Meitou's folder of code-mod sidecar files (docs/formats/save.md "Mod data"); the original game's own files never go there.
    /// </summary>
    public const string ModDataDirectory = "meitou-mods";

    /// <summary>
    /// The extension of a code mod's sidecar file.
    /// </summary>
    const string ModDataExtension = ".dat";

    /// <summary>
    /// The code mods the save was made with (docs/formats/save.md "Mod data"); not a sidecar, its extension keeps it out of
    /// <see cref="ModDataFiles"/>.
    /// </summary>
    public const string ModEnvironmentName = ModDataDirectory + "/environment.json";

    /// <summary>
    /// Meitou's own folder of the level editor's session edits (docs/game/level-editor.md "In the game save"); the original ignores it.
    /// </summary>
    public const string LevelEditsDirectory = "meitou-level-edits";

    /// <summary>
    /// The <c>\</c> character, which names are normalised from.
    /// </summary>
    const char Backslash = (char)92;

    /// <summary>
    /// The full path of each relative name, the topmost layer's winning.
    /// </summary>
    readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scans the layer folders into the name map; a folder that does not exist adds nothing.
    /// </summary>
    /// <param name="layers">Folders from the base layer to the topmost; a file of a later folder hides one of the same name in an earlier one.</param>
    public SaveFolder(IEnumerable<string> layers)
    {
        Layers = [.. layers.Select(Path.GetFullPath)];
        foreach (var layer in Layers)
        {
            if (!Directory.Exists(layer)) continue;
            foreach (var file in Directory.EnumerateFiles(layer, "*", SearchOption.AllDirectories))
                _files[Path.GetRelativePath(layer, file).Replace(Backslash, '/')] = file;
        }
    }

    /// <summary>
    /// The save folder of the single layer <paramref name="folder"/>.
    /// </summary>
    public static SaveFolder Open(string folder) => new([folder]);

    /// <summary>
    /// The layer folders as full paths, base first.
    /// </summary>
    public IReadOnlyList<string> Layers { get; }

    /// <summary>
    /// The topmost layer: where a save made from this one goes.
    /// </summary>
    public string TopLayer => Layers[^1];

    /// <summary>
    /// Every relative name in the map, ordinal order.
    /// </summary>
    public IEnumerable<string> Names => _files.Keys.Order(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="name"/> is in the map.
    /// </summary>
    public bool Exists(string name) => _files.ContainsKey(Normalize(name));

    /// <summary>
    /// The full path a name resolves to, or null.
    /// </summary>
    public string? Resolve(string name) => _files.GetValueOrDefault(Normalize(name));

    /// <summary>
    /// The bytes of the file <paramref name="name"/> resolves to; throws <see cref="FileNotFoundException"/> when it is not in the map.
    /// </summary>
    public byte[] ReadAllBytes(string name) =>
        File.ReadAllBytes(Resolve(name) ?? throw new FileNotFoundException($"'{name}' is not in the save folder '{TopLayer}'."));

    /// <summary>
    /// Whether the folder has a <c>quick.save</c>.
    /// </summary>
    public bool HasQuickSave => Exists(QuickSaveName);

    /// <summary>
    /// Reads the save file <paramref name="name"/>.
    /// </summary>
    public SaveFile ReadFile(string name) => SaveFile.Read(ReadAllBytes(name));

    /// <summary>
    /// Reads <c>quick.save</c>.
    /// </summary>
    public SaveFile ReadQuickSave() => ReadFile(QuickSaveName);

    /// <summary>
    /// The names of the files in <c>platoon/</c>.
    /// </summary>
    public IEnumerable<string> PlatoonFiles => Names.Where(IsPlatoonFile);

    /// <summary>
    /// A name in <see cref="PlatoonDirectory"/> ending <c>.platoon</c>.
    /// </summary>
    static bool IsPlatoonFile(string n) =>
        n.StartsWith(PlatoonDirectory + "/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".platoon", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The zones that have a file in <c>zone/</c> (the files, not the CAMERA list: they can differ, which the game warns about).
    /// </summary>
    public IEnumerable<ZoneCoordinate> ZoneFiles
    {
        get
        {
            foreach (var n in Names)
                if (TryParseZoneName(n, out var zone)) yield return zone;
        }
    }

    /// <summary>
    /// The name of mod <paramref name="modId"/>'s sidecar: <c>meitou-mods/&lt;id&gt;.dat</c>.
    /// </summary>
    public static string ModDataName(string modId) => $"{ModDataDirectory}/{modId}{ModDataExtension}";

    /// <summary>
    /// The code-mod sidecar files: (mod id, name) for each <c>meitou-mods/&lt;id&gt;.dat</c>.
    /// </summary>
    public IEnumerable<(string Id, string Name)> ModDataFiles =>
        Names.Where(IsModDataName).Select(n => (n[(ModDataDirectory.Length + 1)..^ModDataExtension.Length], n));

    /// <summary>
    /// Whether <paramref name="name"/> is a sidecar directly in <see cref="ModDataDirectory"/> (case-sensitive).
    /// </summary>
    static bool IsModDataName(string name) =>
        name.StartsWith(ModDataDirectory + "/", StringComparison.Ordinal) && name.EndsWith(ModDataExtension, StringComparison.Ordinal)
        && name.IndexOf('/', ModDataDirectory.Length + 1) < 0;

    /// <summary>
    /// The zones that have a level-edits file (<see cref="LevelEditsDirectory"/>), with the file's name.
    /// </summary>
    public IEnumerable<(ZoneCoordinate Zone, string Name)> LevelEditFiles
    {
        get
        {
            foreach (var n in Names)
                if (LevelEditsNamePattern().Match(n) is { Success: true } m)
                    yield return (new ZoneCoordinate(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)), n);
        }
    }

    /// <summary>
    /// The name of <paramref name="zone"/>'s level-edits file: <c>meitou-level-edits/zone.X.Y.zone</c>.
    /// </summary>
    public static string LevelEditsName(ZoneCoordinate zone) => $"{LevelEditsDirectory}/zone.{zone.X}.{zone.Y}.zone";

    /// <summary>
    /// The name of <paramref name="zone"/>'s zone file: <c>zone/zone.X.Y.zone</c>.
    /// </summary>
    public static string ZoneName(ZoneCoordinate zone) => $"{ZoneDirectory}/zone.{zone.X}.{zone.Y}.zone";

    /// <summary>
    /// The name of a platoon's file: <c>platoon/&lt;string id&gt;.platoon</c>.
    /// </summary>
    public static string PlatoonName(string platoonStringId) => $"{PlatoonDirectory}/{platoonStringId}.platoon";

    /// <summary>
    /// Whether <paramref name="name"/> is a zone file (<c>zone/zone.X.Y.zone</c>, either slash), and its coordinate.
    /// </summary>
    public static bool TryParseZoneName(string name, out ZoneCoordinate zone)
    {
        var m = ZoneNamePattern().Match(name.Replace(Backslash, '/'));
        zone = m.Success ? new ZoneCoordinate(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : default;
        return m.Success;
    }

    /// <summary>
    /// <paramref name="name"/> with <c>/</c> separators and no leading one.
    /// </summary>
    static string Normalize(string name) => name.Replace(Backslash, '/').TrimStart('/');

    /// <summary>
    /// Matches a level-edits name, capturing X and Y.
    /// </summary>
    [GeneratedRegex(@"^meitou-level-edits/zone\.(\d+)\.(\d+)\.zone$", RegexOptions.IgnoreCase)]
    private static partial Regex LevelEditsNamePattern();

    /// <summary>
    /// Matches a zone file name, capturing X and Y.
    /// </summary>
    [GeneratedRegex(@"^zone/zone\.(\d+)\.(\d+)\.zone$", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneNamePattern();
}