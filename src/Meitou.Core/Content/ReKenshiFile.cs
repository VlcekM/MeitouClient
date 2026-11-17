using System.Text;
using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// A mod's <c>RE_Kenshi.json</c> (docs/formats/rekenshi.md "Data entries"): the plugin DLLs it names, which Meitou never loads, and its data
/// entries, the file rebinds (<see cref="ReKenshiRebinds"/>) and the sound banks, as written.
/// </summary>
public sealed class ReKenshiFile
{
    /// <summary>
    /// Whether <c>Plugins</c> or <c>PreloadPlugins</c> names a DLL.
    /// </summary>
    public bool HasPlugins { get; private init; }

    /// <summary>
    /// <c>FileRebinds</c>: the path the original opens to the path it opens instead (or <c>HIDE</c>), in file order, unparsed.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> FileRebinds { get; private init; } = [];

    /// <summary>
    /// <c>SoundBanks</c>: the sound bank files to load, in file order, unparsed.
    /// </summary>
    public IReadOnlyList<string> SoundBanks { get; private init; } = [];

    /// <summary>
    /// The enabled mods found on disk, in load order (RE_Kenshi walks the active mods).
    /// </summary>
    public static List<ModInfo> EnabledMods(GameInstall install)
    {
        var mods = ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
        return
        [
            .. ModList.ReadEnabled(install)
                .Select(n => mods.GetValueOrDefault(n))
                .OfType<ModInfo>(),
        ];
    }

    /// <summary>
    /// <paramref name="path"/> with its first <c>$(modroot "Name")</c> replaced by the folder of the enabled mod of that name (RE_Kenshi's
    /// <c>ParsePath</c>); a name no enabled mod has stays as written, and so names no file.
    /// </summary>
    public static string ExpandModRoot(string path, IReadOnlyList<ModInfo> enabled)
    {
        const string open = "$(modroot \"";
        int at = path.IndexOf(open, StringComparison.Ordinal);
        int end = at < 0 ? -1 : path.IndexOf("\")", StringComparison.Ordinal);
        if (end < at + open.Length) return path;

        string name = path[(at + open.Length)..end];
        return enabled.FirstOrDefault(m => m.Name == name) is { } mod ? path[..at] + mod.Folder + path[(end + 2)..] : path;
    }

    /// <summary>
    /// The enabled mods' sound banks in order (mod by mod in load order, each file's list in its order), as RE_Kenshi finds each: under the
    /// install's <c>data/audio/</c> when that file exists, else the path as written (after <see cref="ExpandModRoot"/>), from the install root.
    /// </summary>
    public static List<string> ModSoundBanks(GameInstall install)
    {
        var enabled = EnabledMods(install);
        var audio = Path.Combine(install.DataDirectory, "audio");
        var banks = new List<string>();
        foreach (var mod in enabled)
        {
            if (mod.ReKenshi is not { } file) continue;

            foreach (var entry in file.SoundBanks)
            {
                if (BankFile(install, audio, ExpandModRoot(entry, enabled)) is { } bank) banks.Add(bank);
                else Console.Error.WriteLine($"mods      {mod.Name}: RE_Kenshi sound bank {entry} names no file; skipped");
            }
        }

        return banks;
    }

    /// <summary>
    /// The bank file a <c>SoundBanks</c> entry names (<c>\</c> read as a separator on every system): under <paramref name="audio"/> when that
    /// file exists, else the path from the install root; null when it is no path (RE_Kenshi then loses that bank alone).
    /// </summary>
    static string? BankFile(GameInstall install, string audio, string entry)
    {
        string path = entry.Replace('\\', '/');
        try
        {
            string underAudio = Path.Combine(audio, path);
            return GameFiles.Resolve(File.Exists(underAudio) ? underAudio : Path.GetFullPath(path, install.Root));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file in <paramref name="modFolder"/>, or null when there is none or it is not a JSON object (RE_Kenshi logs a file it cannot parse
    /// and takes nothing from it; so does this, on the error stream).
    /// </summary>
    public static ReKenshiFile? TryRead(string modFolder)
    {
        string path = Path.Combine(modFolder, ModList.NativePluginFile);
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(Text(File.ReadAllBytes(path)));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Read(doc.RootElement) : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"mods      {path}: {e.Message}; its entries are ignored");
            return null;
        }
    }

    /// <summary>
    /// The file's text: UTF-8, or, when the bytes are not valid UTF-8, the game's code page (<see cref="GameNameEncoding"/>). RE_Kenshi's reader
    /// does not check the encoding and hands the bytes to the game's file calls, which take the code page.
    /// </summary>
    static string Text(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return GameNameEncoding.Encoding.GetString(bytes);
        }
    }

    /// <summary>
    /// The entries of a parsed file: string members of <c>FileRebinds</c> (others skipped, as RE_Kenshi does) and string items of
    /// <c>SoundBanks</c>.
    /// </summary>
    static ReKenshiFile Read(JsonElement root)
    {
        var rebinds = new List<KeyValuePair<string, string>>();
        if (root.TryGetProperty("FileRebinds", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in map.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String) rebinds.Add(new(entry.Name, entry.Value.GetString()!));
        }

        return new ReKenshiFile
        {
            HasPlugins = NamesAny(root, "Plugins") || NamesAny(root, "PreloadPlugins"),
            FileRebinds = rebinds,
            SoundBanks = Strings(root, "SoundBanks"),
        };
    }

    /// <summary>
    /// Whether member <paramref name="name"/> is an array with at least one item.
    /// </summary>
    static bool NamesAny(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0;

    /// <summary>
    /// The string items of array member <paramref name="name"/>; empty when it is missing or not an array.
    /// </summary>
    static List<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return [];

        return [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
    }
}
