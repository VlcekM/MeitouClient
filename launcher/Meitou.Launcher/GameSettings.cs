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
    /// <summary>
    /// The game's settings file name.
    /// </summary>
    public const string FileName = "meitou.user.json";

    /// <summary>
    /// The game's settings file: in the launcher's data folder, the game's working directory.
    /// </summary>
    public static string FilePath => Path.Combine(LauncherConfig.DataDirectory, FileName);

    /// <summary>
    /// The file's JSON, edited in place.
    /// </summary>
    readonly JsonObject _root;

    /// <summary>
    /// Why the file could not be read; while set, <see cref="Save"/> writes nothing, so the game's file is never replaced by a guess.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// The file's layout (the game's <c>UserConfig.CurrentVersion</c>; no key: 1); null when there is no file yet.
    /// </summary>
    public int? Version => _exists ? _root["version"] is JsonValue v && v.TryGetValue(out int n) ? n : 1 : null;

    /// <summary>
    /// The file existed when it was loaded.
    /// </summary>
    readonly bool _exists;

    /// <summary>
    /// Wraps a loaded (or new) file.
    /// </summary>
    GameSettings(JsonObject root, string? problem, bool exists = true) => (this._root, Problem, this._exists) = (root, problem, exists);

    /// <summary>
    /// Reads the settings file: an empty one when there is none, else the file, or an empty one with <see cref="Problem"/> set when it cannot be
    /// read.
    /// </summary>
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

    /// <summary>
    /// A top-level true or false; null when the key is missing or not a boolean.
    /// </summary>
    public bool? GetBool(string key) => _root[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>
    /// A top-level number; null when the key is missing or not a number.
    /// </summary>
    public float? GetNumber(string key) => Number(_root[key]);

    /// <summary>
    /// A top-level string; null when the key is missing or not a string.
    /// </summary>
    public string? GetString(string key) => _root[key] is JsonValue v && v.TryGetValue(out string? text) ? text : null;

    /// <summary>
    /// A list of strings (non-string entries skipped); empty when the key is missing.
    /// </summary>
    public List<string> GetStrings(string key) =>
        _root[key] is JsonArray list ? [.. list.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).OfType<string>()] : [];

    /// <summary>
    /// Sets a top-level key to a list of strings.
    /// </summary>
    public void Set(string key, IEnumerable<string> values) => _root[key] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

    /// <summary>
    /// Sets a top-level key to a boolean.
    /// </summary>
    public void Set(string key, bool value) => _root[key] = value;

    /// <summary>
    /// Sets a top-level key to a string.
    /// </summary>
    public void Set(string key, string value) => _root[key] = value;

    /// <summary>
    /// A whole value is written as an integer: the game reads its integer settings (the frame limit) strictly.
    /// </summary>
    public void Set(string key, float value) => _root[key] = value == MathF.Round(value)
            && Math.Abs(value) < int.MaxValue ? JsonValue.Create((int)value) : JsonValue.Create(value);

    /// <summary>
    /// Whether any Tab panel slider or switch is saved.
    /// </summary>
    public bool HasGraphics => _root["graphics"] is JsonObject { Count: > 0 };

    /// <summary>
    /// A Tab panel slider's saved value (a switch's: 1 on, 0 off); null when it was never saved (the game then uses the mode's default).
    /// </summary>
    public float? GetGraphics(string label) => _root["graphics"] is JsonObject g ? Number(g[label]) : null;

    /// <summary>
    /// A number however it is held: read from the file (any number), or written this session as a float or, whole, an int
    /// (<see cref="Set(string, float)"/>); a value made from one type does not read back as another.
    /// </summary>
    static float? Number(JsonNode? node) => node is not JsonValue v ? null
        : v.TryGetValue(out double d) ? (float)d : v.TryGetValue(out float f) ? f : v.TryGetValue(out int i) ? i : null;

    /// <summary>
    /// Saves a slider's value, or removes it (null) so the game falls back to its default.
    /// </summary>
    public void SetGraphics(string label, float? value)
    {
        if (_root["graphics"] is not JsonObject g)
        {
            if (value is null) return;
            _root["graphics"] = g = [];
        }
        if (value is { } v) g[label] = v;
        else g.Remove(label);
    }

    /// <summary>
    /// Removes a top-level key, so the game takes its default.
    /// </summary>
    public void Remove(string key) => _root.Remove(key);

    /// <summary>
    /// Removes every saved slider and switch, and the saved preset and look (<paramref name="keys"/>).
    /// </summary>
    public void ResetGraphics(params string?[] keys)
    {
        _root.Remove("graphics");
        foreach (var key in keys)
            if (key is not null) _root.Remove(key);
    }

    /// <summary>
    /// Writes the file; a new one gets <paramref name="version"/>, the layout the keys were written in.
    /// </summary>
    public bool Save(int version)
    {
        if (Problem is not null) return false;
        _root["version"] ??= version;
        Directory.CreateDirectory(LauncherConfig.DataDirectory);

        // The tree writes itself (no type resolver), so this works trimmed.
        using var file = File.Create(FilePath);
        using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        _root.WriteTo(writer);
        return true;
    }
}
