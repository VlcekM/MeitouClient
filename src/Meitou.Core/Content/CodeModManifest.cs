using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// A code mod's <c>meitou.json</c> beside its <c>.mod</c> (docs/modding.md "Discovery"): <c>id</c> (letters, digits, <c>. _ -</c>; keys its save data and its Harmony
/// instance), <c>apiVersion</c> (the code-mod API it was built for), <c>assemblies</c> (DLL file names in the mod folder, loaded in order) and <c>entry</c> (the full name of
/// its <c>IMod</c> type). Optional since API 2 (docs/modding.md "Dependencies"): <c>version</c> (the mod's own <see cref="CodeModVersion"/>) and <c>dependencies</c> (other code
/// mods by id, each with an optional version range and <c>optional</c> flag).
/// </summary>
public sealed record CodeModManifest(string Id, int ApiVersion, IReadOnlyList<string> Assemblies, string Entry,
    CodeModVersion? Version = null, IReadOnlyList<CodeModDependency>? Dependencies = null)
{
    /// <summary>The declared dependencies, in manifest order; empty when the manifest has none.</summary>
    public IReadOnlyList<CodeModDependency> DependsOn => Dependencies ?? [];

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
        return new CodeModManifest(id, apiVersion, ReadAssemblies(root), entry, ReadVersion(root), ReadDependencies(root, id));
    }

    static CodeModVersion? ReadVersion(JsonElement root)
    {
        if (!root.TryGetProperty("version", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || !CodeModVersion.TryParse(value.GetString()!, out var version))
            throw new InvalidDataException($"version: '{value}' is not MAJOR.MINOR.PATCH");
        return version;
    }

    static List<CodeModDependency>? ReadDependencies(JsonElement root, string id)
    {
        if (!root.TryGetProperty("dependencies", out var list)) return null;
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("dependencies: a list is needed");
        var dependencies = new List<CodeModDependency>();
        foreach (var item in list.EnumerateArray())
        {
            var dependency = CodeModDependency.FromJson(item);
            if (dependency.Id == id) throw new InvalidDataException($"dependencies: the mod lists itself ('{id}')");
            if (dependencies.Any(d => d.Id == dependency.Id)) throw new InvalidDataException($"dependencies: '{dependency.Id}' is listed twice");
            dependencies.Add(dependency);
        }
        return dependencies;
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

/// <summary>
/// One entry of a manifest's <c>dependencies</c> (docs/modding.md "Dependencies"): another code mod's manifest id, an optional version range, and whether the mod
/// also loads without it. A plain string is a required dependency on any version.
/// </summary>
public sealed record CodeModDependency(string Id, CodeModVersionRange? Range = null, bool Optional = false)
{
    internal static CodeModDependency FromJson(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String) return new CodeModDependency(CheckedId(item.GetString()));
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"dependencies: '{item}' is neither an id nor an object");
        string id = CheckedId(item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null);
        CodeModVersionRange? range = null;
        if (item.TryGetProperty("version", out var version))
        {
            if (version.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"dependencies: {id}'s version must be a string such as \">=1.2.0 <2.0.0\"");
            try { range = CodeModVersionRange.Parse(version.GetString()!); }
            catch (InvalidDataException e) { throw new InvalidDataException($"dependencies: {id}: {e.Message}", e); }
        }
        bool optional = item.TryGetProperty("optional", out var flag) && flag.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"dependencies: {id}'s optional must be true or false"),
        };
        return new CodeModDependency(id, range, optional);
    }

    static string CheckedId(string? id)
    {
        if (id is null || !CodeModManifest.IsValidId(id)) throw new InvalidDataException($"dependencies: '{id}' is not a mod id");
        return id;
    }

    /// <summary>Whether an installed version satisfies the range: no range accepts anything, a mod without a version satisfies only no range.</summary>
    public bool Accepts(CodeModVersion? installed) => Range is null || installed is { } v && Range.Contains(v);

    public override string ToString() => Range is null ? Id : $"{Id} {Range}";
}
