using System.Text.Json;

namespace Meitou.Content;

/// <summary>
/// One code mod a save was made with: its manifest id, its <c>version</c> (null when it declares none) and its mod (folder) name.
/// </summary>
public sealed record CodeModEnvironmentEntry(string Id, string? Version, string Name);

/// <summary>
/// The code mods a save was made with, in load order, and the Meitou version (docs/formats/save.md "Mod data": <c>meitou-mods/environment.json</c>).
/// Loading compares it with the running mods and warns; it never refuses a save, since the data mods and sidecars load as before.
/// </summary>
public sealed record CodeModEnvironment(string GameVersion, IReadOnlyList<CodeModEnvironmentEntry> Mods)
{
    /// <summary>
    /// The file's <c>format</c> number this game writes and understands; <see cref="TryRead"/> rejects any other.
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// <c>{ "format": 1, "gameVersion": "v0.16.1", "mods": [{ "id", "version", "name" }] }</c>, mods in load order.
    /// </summary>
    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", FormatVersion);
            w.WriteString("gameVersion", GameVersion);
            w.WriteStartArray("mods");
            foreach (var mod in Mods) WriteMod(w, mod);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Writes one mod's object; <c>version</c> is left out when the mod declares none.
    /// </summary>
    static void WriteMod(Utf8JsonWriter w, CodeModEnvironmentEntry mod)
    {
        w.WriteStartObject();
        w.WriteString("id", mod.Id);
        if (mod.Version is not null) w.WriteString("version", mod.Version);
        w.WriteString("name", mod.Name);
        w.WriteEndObject();
    }

    /// <summary>
    /// Reads the file; null when it is not one this game understands (a newer format, or damaged, an id listed twice included): the save still loads,
    /// without the comparison.
    /// </summary>
    public static CodeModEnvironment? TryRead(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Number(root, "format") != FormatVersion) return null;
            if (Text(root, "gameVersion") is not { } game) return null;
            if (!root.TryGetProperty("mods", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var mods = list.EnumerateArray().Select(ReadMod).ToList();
            if (mods.Contains(null) || mods.DistinctBy(m => m!.Id, StringComparer.Ordinal).Count() != mods.Count)
                return null;   // damaged
            return new CodeModEnvironment(game, mods!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one entry of <c>mods</c>; null when it is not an object with string <c>id</c> and <c>name</c>.
    /// </summary>
    static CodeModEnvironmentEntry? ReadMod(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Text(item, "id") is not { } id || Text(item, "name") is not { } name)
            return null;
        return new CodeModEnvironmentEntry(id, Text(item, "version"), name);
    }

    /// <summary>
    /// The string property <paramref name="name"/> of <paramref name="e"/>; null when missing or not a string.
    /// </summary>
    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The int32 property <paramref name="name"/> of <paramref name="e"/>; null when missing or not an int32 number.
    /// </summary>
    static int? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    /// <summary>
    /// What changed since the save was made, one line each: a code mod gone, a version changed (a major change or a downgrade called out), the order
    /// of the mods both have changed, a code mod added. Empty when nothing did. An id listed twice counts once (the first).
    /// </summary>
    public List<string> ChangesTo(CodeModEnvironment now)
    {
        var current = ById(now.Mods);
        var saved = ById(Mods);
        var lines = new List<string>();
        foreach (var mod in Mods.DistinctBy(m => m.Id))
        {
            if (!current.TryGetValue(mod.Id, out var running)) lines.Add(Gone(mod));
            else if (VersionChange(mod, running) is { } change) lines.Add(change);
        }
        if (OrderChanged(now, saved)) lines.Add("The code mods load in a different order than when this was saved");
        foreach (var mod in now.Mods.DistinctBy(m => m.Id).Where(m => !saved.ContainsKey(m.Id)))
            lines.Add($"{mod.Name} ('{mod.Id}') is new to this save");
        return lines;
    }

    /// <summary>
    /// The mods by id, the first of an id listed twice kept.
    /// </summary>
    static Dictionary<string, CodeModEnvironmentEntry> ById(IEnumerable<CodeModEnvironmentEntry> mods) =>
        mods.DistinctBy(m => m.Id).ToDictionary(m => m.Id, StringComparer.Ordinal);

    /// <summary>
    /// The change line for a mod the save ran that is not running now.
    /// </summary>
    static string Gone(CodeModEnvironmentEntry mod) =>
        $"{mod.Name} ('{mod.Id}') was running when this was saved and is not now; its saved data is kept but unused";

    /// <summary>
    /// The change line for a mod whose version differs between the save and now; null when it is the same. Notes a downgrade or a major update.
    /// </summary>
    static string? VersionChange(CodeModEnvironmentEntry saved, CodeModEnvironmentEntry running)
    {
        if (saved.Version == running.Version) return null;
        string from = saved.Version ?? "no version", to = running.Version ?? "no version";
        string note = Parse(saved.Version) is { } a && Parse(running.Version) is { } b
            ? b.CompareTo(a) < 0 ? " (older than the save's)" : b.Major != a.Major ? " (a major update: its saved data may not fit)" : ""
            : "";
        return $"{running.Name} ('{running.Id}') was {from} when this was saved and is {to} now{note}";
    }

    /// <summary>
    /// <paramref name="text"/> as a version; null when null or not a valid one.
    /// </summary>
    static CodeModVersion? Parse(string? text) => text is not null && CodeModVersion.TryParse(text, out var v) ? v : null;

    /// <summary>
    /// Whether the mods running both then and now load in a different relative order.
    /// </summary>
    bool OrderChanged(CodeModEnvironment now, Dictionary<string, CodeModEnvironmentEntry> saved)
    {
        var running = now.Mods.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var before = Mods.Select(m => m.Id).Distinct().Where(running.Contains).ToList();
        var after = now.Mods.Select(m => m.Id).Distinct().Where(saved.ContainsKey).ToList();
        return !before.SequenceEqual(after);
    }
}