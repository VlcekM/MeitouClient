using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meitou.Launcher;

/// <summary>
/// The game's <c>meitou.user.json</c> in <see cref="LauncherConfig.DataDirectory"/> (the game's working directory), edited as a JSON tree
/// so every key the launcher does not show stays as the game wrote it. Top-level keys are the game's <c>UserConfig</c> properties in
/// camelCase; <c>graphics</c> holds the Tab panel's sliders and switches by label (docs/launcher.md "Graphics").
/// </summary>
sealed class GameSettings
{
    public const string FileName = "meitou.user.json";
    public static string FilePath => Path.Combine(LauncherConfig.DataDirectory, FileName);

    readonly JsonObject root;

    /// <summary>Why the file could not be read; while set, <see cref="Save"/> writes nothing, so the game's file is never replaced by a guess.</summary>
    public string? Problem { get; }

    /// <summary>The file's layout (the game's <c>UserConfig.CurrentVersion</c>; no key: 1); null when there is no file yet.</summary>
    public int? Version => exists ? root["version"] is JsonValue v && v.TryGetValue(out int n) ? n : 1 : null;

    readonly bool exists;

    GameSettings(JsonObject root, string? problem, bool exists = true) => (this.root, Problem, this.exists) = (root, problem, exists);

    public static GameSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new([], null, exists: false);
            return JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject json ? new(json, null) : new([], $"{FileName} is not a JSON object.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new([], $"Could not read {FileName}: {e.Message}");
        }
    }

    public bool? GetBool(string key) => root[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
    public float? GetNumber(string key) => Number(root[key]);
    public string? GetString(string key) => root[key] is JsonValue v && v.TryGetValue(out string? text) ? text : null;
    public void Set(string key, bool value) => root[key] = value;
    public void Set(string key, string value) => root[key] = value;
    /// <summary>A whole value is written as an integer: the game reads its integer settings (the frame limit) strictly.</summary>
    public void Set(string key, float value) => root[key] = value == MathF.Round(value) && Math.Abs(value) < int.MaxValue ? JsonValue.Create((int)value) : JsonValue.Create(value);

    /// <summary>Whether any Tab panel slider or switch is saved.</summary>
    public bool HasGraphics => root["graphics"] is JsonObject { Count: > 0 };

    /// <summary>A Tab panel slider's saved value (a switch's: 1 on, 0 off); null when it was never saved (the game then uses the mode's default).</summary>
    public float? GetGraphics(string label) => root["graphics"] is JsonObject g ? Number(g[label]) : null;

    /// <summary>
    /// A number however it is held: read from the file (any number), or written this session as a float or, whole, an int (<see cref="Set(string, float)"/>);
    /// a value made from one type does not read back as another.
    /// </summary>
    static float? Number(JsonNode? node) => node is not JsonValue v ? null
        : v.TryGetValue(out double d) ? (float)d : v.TryGetValue(out float f) ? f : v.TryGetValue(out int i) ? i : null;

    /// <summary>Saves a slider's value, or removes it (null) so the game falls back to its default.</summary>
    public void SetGraphics(string label, float? value)
    {
        if (root["graphics"] is not JsonObject g)
        {
            if (value is null) return;
            root["graphics"] = g = [];
        }
        if (value is { } v) g[label] = v;
        else g.Remove(label);
    }

    /// <summary>Removes a top-level key, so the game takes its default.</summary>
    public void Remove(string key) => root.Remove(key);

    /// <summary>Removes every saved slider and switch, and the saved preset and look (<paramref name="keys"/>).</summary>
    public void ResetGraphics(params string?[] keys)
    {
        root.Remove("graphics");
        foreach (var key in keys)
            if (key is not null) root.Remove(key);
    }

    /// <summary>Writes the file; a new one gets <paramref name="version"/>, the layout the keys were written in.</summary>
    public bool Save(int version)
    {
        if (Problem is not null) return false;
        root["version"] ??= version;
        Directory.CreateDirectory(LauncherConfig.DataDirectory);
        // The tree writes itself (no type resolver), so this works trimmed.
        using var file = File.Create(FilePath);
        using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        root.WriteTo(writer);
        return true;
    }
}
