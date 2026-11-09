using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// A code mod's <c>meitou.json</c> beside its <c>.mod</c> (docs/modding.md "Discovery"): <c>id</c> (letters, digits, <c>. _ -</c>; keys its save data
/// and its Harmony instance), <c>apiVersion</c> (the code-mod API it was built for), <c>assemblies</c> (DLL file names in the mod folder, loaded in
/// order) and <c>entry</c> (the full name of its <c>IMod</c> type). Optional since API 2 (docs/modding-dependencies.md "Manifest"): <c>version</c>
/// (the mod's own <see cref="CodeModVersion"/>) and <c>dependencies</c> (other code mods by id, each with an optional version range and
/// <c>optional</c> flag), plus <c>loadAfter</c>, <c>incompatible</c> and <c>gameVersion</c> (CodeModManifest.Ordering.cs).
/// </summary>
public sealed partial record CodeModManifest(string Id, int ApiVersion, IReadOnlyList<string> Assemblies, string Entry,
    CodeModVersion? Version, IReadOnlyList<CodeModDependency>? Dependencies)
{
    static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// The API 1 constructor, kept so mods compiled against API 1 still bind to it (docs/modding-dependencies.md "Compatibility").
    /// </summary>
    // Named as the record's own parameters (and properties), which mods may pass by name.
#pragma warning disable IDE1006
    public CodeModManifest(string Id, int ApiVersion, IReadOnlyList<string> Assemblies, string Entry)
        : this(Id, ApiVersion, Assemblies, Entry, null, null)
    {
    }
#pragma warning restore IDE1006

    /// <summary>
    /// The API 1 deconstruction (four parts), kept beside the generated six-part one for mods compiled against API 1.
    /// </summary>
    public void Deconstruct(out string id, out int apiVersion, out IReadOnlyList<string> assemblies, out string entry)
    {
        id = this.Id;
        apiVersion = this.ApiVersion;
        assemblies = this.Assemblies;
        entry = this.Entry;
    }

    /// <summary>
    /// The declared dependencies, in manifest order; empty when the manifest has none.
    /// </summary>
    public IReadOnlyList<CodeModDependency> DependsOn => Dependencies ?? [];

    /// <summary>
    /// Reads a manifest; throws <see cref="InvalidDataException"/> with the reason when it is not a valid one, or <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> when the file cannot be read. Every field is checked for its JSON kind before it is read.
    /// </summary>
    public static CodeModManifest Read(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path), JsonOptions);
            return FromJson(json.RootElement);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{ModList.CodeManifestFile} is not valid JSON: {e.Message}", e);
        }
        catch (InvalidOperationException e)
        {
            // A JsonElement read of the wrong kind; the field checks should make this unreachable, but it stays a malformed manifest, not a crash.
            throw new InvalidDataException($"{ModList.CodeManifestFile} has a field of the wrong kind: {e.Message}", e);
        }
    }

    /// <summary>
    /// Only the <c>id</c> of a manifest that may be malformed otherwise, so a dependent of a broken mod names it; null when even that cannot be read.
    /// Never throws.
    /// </summary>
    public static string? TryReadId(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path), JsonOptions);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                return null;
            return id.GetString() is { } text && IsValidId(text) ? text : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    static CodeModManifest FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{ModList.CodeManifestFile} must hold an object");
        string id = Text(root, "id");
        if (!IsValidId(id))
            throw new InvalidDataException($"id '{id}': use letters, digits, '.', '_' and '-', starting with a letter or digit");
        int apiVersion = WholeNumber(root, "apiVersion");
        string entry = Text(root, "entry");
        return new CodeModManifest(id, apiVersion, ReadAssemblies(root), entry, ReadVersion(root), ReadDependencies(root, id))
        {
            LoadAfter = ReadLoadAfter(root, id),
            Incompatible = ReadIncompatible(root, id),
            GameVersion = ReadGameVersion(root),
            PortOf = root.TryGetProperty("portOf", out var port) && port.ValueKind == JsonValueKind.String ? port.GetString() : null,
        };
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
            if (dependencies.Any(d => d.Id == dependency.Id))
                throw new InvalidDataException($"dependencies: '{dependency.Id}' is listed twice");
            dependencies.Add(dependency);
        }
        return dependencies;
    }

    static int WholeNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            throw new InvalidDataException($"{name}: a whole number is needed, such as 2 (not \"2\")");
        return number;
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
/// One entry of a manifest's <c>dependencies</c> (docs/modding-dependencies.md "Manifest"): another code mod's manifest id, an optional version
/// range, and whether the mod also loads without it. A plain string is a required dependency on any version.
/// </summary>
public sealed record CodeModDependency(string Id, CodeModVersionRange? Range = null, bool Optional = false)
{
    internal static CodeModDependency FromJson(JsonElement item, string key = "dependencies")
    {
        if (item.ValueKind == JsonValueKind.String) return new CodeModDependency(CheckedId(item.GetString(), key));
        if (item.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{key}: '{item}' is neither an id nor an object");
        string? text = item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
        string id = CheckedId(text, key);
        return new CodeModDependency(id, ReadRange(item, id, key), ReadOptional(item, id, key));
    }

    static CodeModVersionRange? ReadRange(JsonElement item, string id, string key)
    {
        if (!item.TryGetProperty("version", out var version)) return null;
        if (version.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{key}: {id}'s version must be a string such as \">=1.2.0 <2.0.0\"");
                try
        {
            return CodeModVersionRange.Parse(version.GetString()!);
        }
        catch (InvalidDataException e) { throw new InvalidDataException($"{key}: {id}: {e.Message}", e); }
    }

    static bool ReadOptional(JsonElement item, string id, string key) =>
        item.TryGetProperty("optional", out var flag) && flag.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{key}: {id}'s optional must be true or false"),
        };

    static string CheckedId(string? id, string key)
    {
        if (id is null || !CodeModManifest.IsValidId(id))
            throw new InvalidDataException($"{key}: '{id}' is not a mod id");
        return id;
    }

    /// <summary>
    /// Whether an installed version satisfies the range: no range accepts anything, a mod without a version satisfies only no range.
    /// </summary>
    public bool Accepts(CodeModVersion? installed) => Range is null || installed is { } v && Range.Contains(v);

    public override string ToString() => Range is null ? Id : $"{Id} {Range}";
}