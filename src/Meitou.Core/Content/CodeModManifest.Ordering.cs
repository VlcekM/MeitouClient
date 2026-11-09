using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// The API 2 manifest keys beside dependencies (docs/modding-dependencies.md "Manifest"): ordering without sharing, declared conflicts and the Meitou
/// versions a mod was built for.
/// </summary>
public sealed partial record CodeModManifest
{
    /// <summary>
    /// <c>loadAfter</c>: ids of code mods that, when enabled, must load earlier. Ordering only: nothing is imported and nothing is required.
    /// </summary>
    public IReadOnlyList<string> LoadAfter { get; init; } = [];

    /// <summary>
    /// <c>incompatible</c>: code mods (by id, optionally a version range) this one refuses to run beside; whichever loads second is refused.
    /// </summary>
    public IReadOnlyList<CodeModConflict> Incompatible { get; init; } = [];

    /// <summary>
    /// <c>gameVersion</c>: the Meitou releases the mod was built against; null accepts any. Compared with the running build's release
    /// (docs/modding-dependencies.md "Manifest").
    /// </summary>
    public CodeModVersionRange? GameVersion { get; init; }

    /// <summary>
    /// The keys used that an API 1 game would ignore, so they need <c>apiVersion</c> 2.
    /// </summary>
    public IEnumerable<string> Api2Keys()
    {
        if (DependsOn.Count > 0) yield return "dependencies";
        if (LoadAfter.Count > 0) yield return "loadAfter";
        if (Incompatible.Count > 0) yield return "incompatible";
        if (GameVersion is not null) yield return "gameVersion";
    }

    /// <summary>
    /// Whether this mod or <paramref name="other"/> declares the other incompatible (at its installed version).
    /// </summary>
    public bool ConflictsWith(CodeModManifest other) =>
        Incompatible.Any(c => c.Matches(other)) || other.Incompatible.Any(c => c.Matches(this));

    /// <summary>
    /// The optional <c>loadAfter</c> list of mod ids, duplicates dropped; empty when absent. Refuses a bad id and the mod naming itself.
    /// </summary>
    static List<string> ReadLoadAfter(JsonElement root, string id)
    {
        if (!root.TryGetProperty("loadAfter", out var list)) return [];
        if (list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("loadAfter: a list of mod ids is needed");
        var ids = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            string? other = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (other is null || !IsValidId(other))
                throw new InvalidDataException($"loadAfter: '{item}' is not a mod id");
            if (other == id) throw new InvalidDataException($"loadAfter: the mod lists itself ('{id}')");
            if (!ids.Contains(other)) ids.Add(other);
        }
        return ids;
    }

    /// <summary>
    /// The optional <c>incompatible</c> list; empty when absent. Refuses the mod naming itself.
    /// </summary>
    static List<CodeModConflict> ReadIncompatible(JsonElement root, string id)
    {
        if (!root.TryGetProperty("incompatible", out var list)) return [];
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("incompatible: a list is needed");
        var conflicts = new List<CodeModConflict>();
        foreach (var item in list.EnumerateArray())
        {
            var conflict = CodeModConflict.FromJson(item);
            if (conflict.Id == id) throw new InvalidDataException($"incompatible: the mod lists itself ('{id}')");
            conflicts.Add(conflict);
        }
        return conflicts;
    }

    /// <summary>
    /// The optional <c>gameVersion</c> range; null when absent. A range naming a pre-release is refused.
    /// </summary>
    static CodeModVersionRange? ReadGameVersion(JsonElement root)
    {
        if (!root.TryGetProperty("gameVersion", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("gameVersion: a range string such as \">=0.16.0 <0.17.0\" is needed");
        CodeModVersionRange range;
        try
        {
            range = CodeModVersionRange.Parse(value.GetString()!);
        }
        catch (InvalidDataException e) { throw new InvalidDataException($"gameVersion: {e.Message}", e); }
        if (range.NamesPrerelease)
            throw new InvalidDataException("gameVersion: name releases only (0.16.1, not 0.16.1-nightly.x); a nightly counts as its release");
        return range;
    }
}

/// <summary>
/// One entry of <c>incompatible</c>: a mod id, or an object with <c>id</c> and an optional <c>version</c> range (only those versions conflict).
/// </summary>
public sealed record CodeModConflict(string Id, CodeModVersionRange? Range = null)
{
    /// <summary>
    /// Reads one <c>incompatible</c> entry, in the same shapes as a dependency; an <c>optional</c> flag is refused.
    /// </summary>
    internal static CodeModConflict FromJson(JsonElement item)
    {
        var parsed = CodeModDependency.FromJson(item, "incompatible");
        if (parsed.Optional) throw new InvalidDataException($"incompatible: {parsed.Id} cannot be optional");
        return new CodeModConflict(parsed.Id, parsed.Range);
    }

    /// <summary>
    /// Whether <paramref name="other"/> is this mod at a version the range names (no range: any version, even none).
    /// </summary>
    public bool Matches(CodeModManifest other) => other.Id == Id && (Range is null || other.Version is { } v && Range.Contains(v));

    public override string ToString() => Range is null ? Id : $"{Id} {Range}";
}