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
