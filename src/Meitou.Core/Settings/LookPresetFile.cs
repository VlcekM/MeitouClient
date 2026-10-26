using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Meitou.Settings;

/// <summary>A look preset as a file holds it: its name, the Meitou version that made it, and the <c>graphics</c> values it sets over Meitou's.</summary>
public sealed record LookPresetData(string Name, string Made, Dictionary<string, float> Values);

/// <summary>
/// The look preset files (<c>presets/&lt;name&gt;.json</c> beside <c>meitou.user.json</c>: <c>format</c>, <c>name</c>, <c>meitou</c>, <c>values</c>) and their share
/// code (<c>MT1:</c> and the deflated JSON in base64), shared by the game's settings menu and the launcher so both read and write the same
/// presets (docs/native-ui.md "Settings").
/// </summary>
public static class LookPresetFile
{
    public static LookPresetData? Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var values = new Dictionary<string, float>();
            foreach (var p in root.GetProperty("values").EnumerateObject()) values[p.Name] = p.Value.GetSingle();
            return new(root.GetProperty("name").GetString() ?? "Unnamed", root.TryGetProperty("meitou", out var m) ? m.GetString() ?? "?" : "?", values);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    public static string Write(LookPresetData look)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("format", 1);
            json.WriteString("name", look.Name);
            json.WriteString("meitou", look.Made);
            json.WriteStartObject("values");
            foreach (var (key, value) in look.Values) json.WriteNumber(key, value);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string ShareCode(LookPresetData look)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize)) deflate.Write(Encoding.UTF8.GetBytes(Write(look)));
        return "MT1:" + Convert.ToBase64String(buffer.ToArray());
    }

    /// <summary>The preset a share code holds (whitespace ignored), or null when it is not one.</summary>
    public static LookPresetData? FromShareCode(string? code)
    {
        string text = string.Concat((code ?? "").Where(c => !char.IsWhiteSpace(c)));
        if (!text.StartsWith("MT1:", StringComparison.Ordinal)) return null;
        try
        {
            using var deflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(text[4..])), CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            return Read(reader.ReadToEnd());
        }
        catch (Exception e) when (e is FormatException or InvalidDataException) { return null; }
    }

    /// <summary>A preset's file name: its name with the characters a file name may not hold replaced.</summary>
    public static string FileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json";

    /// <summary><paramref name="stem"/>, or with the first number that makes it a name none of <paramref name="taken"/> has.</summary>
    public static string FreeName(string stem, IEnumerable<string> taken)
    {
        var names = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(stem)) return stem;
        for (int i = 2; ; i++) if (!names.Contains($"{stem} {i}")) return $"{stem} {i}";
    }

    /// <summary>The player's presets in <paramref name="folder"/> (file path and preset), in file name order; unreadable files are left out.</summary>
    public static List<(string File, LookPresetData Look)> ReadFolder(string folder)
    {
        var found = new List<(string, LookPresetData)>();
        try
        {
            if (!Directory.Exists(folder)) return found;
            foreach (var file in Directory.GetFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                if (Read(File.ReadAllText(file)) is { } look) found.Add((file, look));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>Writes a preset to <paramref name="folder"/> under its file name and returns the file's path.</summary>
    public static string Save(string folder, LookPresetData look)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, FileName(look.Name));
        File.WriteAllText(file, Write(look));
        return file;
    }
}
