using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// A code mod's <c>meitou.json</c> beside its <c>.mod</c> (docs/modding.md "Discovery"): <c>id</c> (letters, digits, <c>. _ -</c>; keys its save data and its Harmony
/// instance), <c>apiVersion</c> (the code-mod API it was built for), <c>assemblies</c> (DLL file names in the mod folder, loaded in order) and <c>entry</c> (the full name of
/// its <c>IMod</c> type).
/// </summary>
public sealed record CodeModManifest(string Id, int ApiVersion, IReadOnlyList<string> Assemblies, string Entry)
{
    /// <summary>Reads a manifest; throws <see cref="InvalidDataException"/> with the reason when it is not a valid one.</summary>
    public static CodeModManifest Read(string path)
    {
        try
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            using var json = JsonDocument.Parse(File.ReadAllBytes(path), options);
            return FromJson(json.RootElement);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{ModList.CodeManifestFile} is not valid JSON: {e.Message}", e);
        }
    }

    static CodeModManifest FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{ModList.CodeManifestFile} must hold an object");
        string id = Text(root, "id");
        if (!IsValidId(id)) throw new InvalidDataException($"id '{id}': use letters, digits, '.', '_' and '-', starting with a letter or digit");
        if (!root.TryGetProperty("apiVersion", out var api) || !api.TryGetInt32(out int apiVersion))
            throw new InvalidDataException("apiVersion: a whole number is needed");
        string entry = Text(root, "entry");
        return new CodeModManifest(id, apiVersion, ReadAssemblies(root), entry);
    }

    static string Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text)
            throw new InvalidDataException($"{name}: a non-empty string is needed");
        return text;
    }

    static List<string> ReadAssemblies(JsonElement root)
    {
        if (!root.TryGetProperty("assemblies", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw new InvalidDataException("assemblies: a list of DLL file names is needed");
        var names = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            string? name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (name is not { Length: > 0 } || Path.GetFileName(name) != name || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"assemblies: '{item}' is not a DLL file name in the mod folder");
            names.Add(name);
        }
        return names;
    }

    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
