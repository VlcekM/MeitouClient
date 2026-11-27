using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// A code mod's lighting hook as its <c>meitou.json</c> declares it (docs/modding.md "Lighting hooks"): <c>"lighting": { "file": "toon.glsl",
/// "params": { "strength": 1.0 }, "textures": { "ramp": "ramp.png" } }</c>. <c>file</c> is GLSL in the mod's folder, <c>params</c> the numbers the
/// mod sets while the game runs, each with the value it starts at, <c>textures</c> the images its GLSL reads.
/// </summary>
public sealed record CodeModLighting(string File, IReadOnlyList<CodeModLightingParam> Params)
{
    /// <summary>
    /// The most textures one hook declares (and the lighting hooks hold together).
    /// </summary>
    public const int MaxTextures = 4;

    /// <summary>
    /// The most parameters one hook declares.
    /// </summary>
    public const int MaxParams = 31;

    /// <summary>
    /// The longest parameter or texture name.
    /// </summary>
    public const int MaxParamName = 32;

    /// <summary>
    /// The images the hook's GLSL reads, in manifest order; empty when it declares none.
    /// </summary>
    public IReadOnlyList<CodeModLightingTexture> Textures { get; init; } = [];

    /// <summary>
    /// The optional <c>lighting</c> field of a manifest's root; null when absent, <see cref="InvalidDataException"/> when it is not a hook.
    /// </summary>
    internal static CodeModLighting? Read(JsonElement root)
    {
        if (!root.TryGetProperty("lighting", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("lighting: an object with a file is needed");

        return new CodeModLighting(ReadFile(value, "lighting.file"), ReadParams(value)) { Textures = ReadTextures(value) };
    }

    /// <summary>
    /// A file the hook names under <paramref name="key"/>: a path inside the mod's folder.
    /// </summary>
    static string ReadFile(JsonElement holder, string key)
    {
        string? file = holder.TryGetProperty("file", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return InsideFolder(file) ? file! : throw new InvalidDataException($"{key}: a file in the mod's folder is needed");
    }

    /// <summary>
    /// Whether a path stays inside the mod's folder: relative, with no step up.
    /// </summary>
    static bool InsideFolder(string? file) =>
        !string.IsNullOrWhiteSpace(file) && !Path.IsPathRooted(file) && !file.Split('/', '\\').Contains("..");

    /// <summary>
    /// The hook's textures, in manifest order: each a name GLSL can hold and a file, as text or as <c>{ "file", "repeat" }</c> (repeat: the image
    /// tiles; else its edge pixels go on).
    /// </summary>
    static List<CodeModLightingTexture> ReadTextures(JsonElement lighting)
    {
        var read = new List<CodeModLightingTexture>();
        if (!lighting.TryGetProperty("textures", out var list)) return read;
        if (list.ValueKind != JsonValueKind.Object) throw new InvalidDataException("lighting.textures: an object of name and file is needed");

        foreach (var entry in list.EnumerateObject())
        {
            string key = $"lighting.textures '{entry.Name}'";
            if (!IsValidParam(entry.Name) || read.Any(t => t.Name == entry.Name))
                throw new InvalidDataException($"{key}: a name of letters, digits and '_' (at most {MaxParamName}), used once");
            read.Add(ReadTexture(entry, key));
        }
        if (read.Count > MaxTextures) throw new InvalidDataException($"lighting.textures: at most {MaxTextures}");

        return read;
    }

    /// <summary>
    /// One texture: its file as text, or an object with <c>file</c> and an optional <c>repeat</c>.
    /// </summary>
    static CodeModLightingTexture ReadTexture(JsonProperty entry, string key)
    {
        if (entry.Value.ValueKind == JsonValueKind.String)
        {
            string? file = entry.Value.GetString();
            return InsideFolder(file) ? new(entry.Name, file!, false) : throw new InvalidDataException($"{key}: a file in the mod's folder is needed");
        }
        if (entry.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{key}: a file, or an object with a file, is needed");

        bool repeat = entry.Value.TryGetProperty("repeat", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new(entry.Name, ReadFile(entry.Value, key), repeat);
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
/// One texture of a lighting hook: the name its GLSL reads it by (<c>TEXTURE_name</c>), its image file in the mod's folder, and whether it tiles.
/// </summary>
public readonly record struct CodeModLightingTexture(string Name, string File, bool Repeat);

/// <summary>
/// One parameter of a lighting hook: its name and the value it starts at.
/// </summary>
public readonly record struct CodeModLightingParam(string Name, float Start);
