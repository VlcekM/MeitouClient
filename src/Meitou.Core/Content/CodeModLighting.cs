using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// A code mod's lighting hook as its <c>meitou.json</c> declares it (docs/modding.md "Lighting hooks"): <c>"lighting": { "file": "toon.glsl",
/// "params": { "strength": 1.0 } }</c>. <c>file</c> is GLSL in the mod's folder, <c>params</c> the numbers the mod sets while the game runs, each
/// with the value it starts at.
/// </summary>
public sealed record CodeModLighting(string File, IReadOnlyList<CodeModLightingParam> Params)
{
    /// <summary>
    /// The most parameters one hook declares.
    /// </summary>
    public const int MaxParams = 31;

    /// <summary>
    /// The longest parameter name.
    /// </summary>
    public const int MaxParamName = 32;

    /// <summary>
    /// The optional <c>lighting</c> field of a manifest's root; null when absent, <see cref="InvalidDataException"/> when it is not a hook.
    /// </summary>
    internal static CodeModLighting? Read(JsonElement root)
    {
        if (!root.TryGetProperty("lighting", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("lighting: an object with a file is needed");

        return new CodeModLighting(ReadFile(value), ReadParams(value));
    }

    /// <summary>
    /// The hook's file: a path inside the mod's folder.
    /// </summary>
    static string ReadFile(JsonElement lighting)
    {
        string? file = lighting.TryGetProperty("file", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool inside = !string.IsNullOrWhiteSpace(file) && !Path.IsPathRooted(file) && !file.Split('/', '\\').Contains("..");
        if (!inside) throw new InvalidDataException("lighting.file: a file in the mod's folder is needed");

        return file!;
    }

    /// <summary>
    /// The hook's parameters, in manifest order: each a name GLSL can hold and a finite number.
    /// </summary>
    static List<CodeModLightingParam> ReadParams(JsonElement lighting)
    {
        var read = new List<CodeModLightingParam>();
        if (!lighting.TryGetProperty("params", out var list)) return read;
        if (list.ValueKind != JsonValueKind.Object) throw new InvalidDataException("lighting.params: an object of name and number is needed");

        foreach (var entry in list.EnumerateObject())
        {
            bool number = entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetSingle(out float start) && float.IsFinite(start);
            if (!IsValidParam(entry.Name) || !number || read.Any(p => p.Name == entry.Name))
                throw new InvalidDataException($"lighting.params '{entry.Name}': a name of letters, digits and '_' (at most {MaxParamName}) with a number");
            read.Add(new CodeModLightingParam(entry.Name, entry.Value.GetSingle()));
        }
        if (read.Count > MaxParams) throw new InvalidDataException($"lighting.params: at most {MaxParams}");

        return read;
    }

    /// <summary>
    /// Whether a parameter's name is one GLSL can hold: a letter, then letters, digits and <c>_</c>, at most <see cref="MaxParamName"/>.
    /// </summary>
    public static bool IsValidParam(string name) =>
        name.Length is > 0 and <= MaxParamName && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>
/// One parameter of a lighting hook: its name and the value it starts at.
/// </summary>
public readonly record struct CodeModLightingParam(string Name, float Start);
