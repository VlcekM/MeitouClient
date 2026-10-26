using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Settings;

/// <summary>
/// The settings a game release describes for the launcher: <c>settings-schema.json</c> beside its executable, written by the game
/// (<c>--settings-schema</c>) from the same names it saves under, so the launcher never repeats them (docs/launcher.md "Graphics").
/// </summary>
public sealed class SettingsSchema
{
    public const string FileName = "settings-schema.json";
    /// <summary>The layout of this file. Adding properties keeps it; anything a reader would misread raises it, and the launcher then shows nothing.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    /// <summary>The <c>meitou.user.json</c> layout this release reads and writes (its <c>version</c> key).</summary>
    public int SettingsVersion { get; set; }
    /// <summary>The frame renderers this release contains; an older schema without the key is Vulkan-only.</summary>
    public List<string> Renderers { get; set; } = ["vulkan"];
    /// <summary>The sections, one launcher tab each, in the game's page order.</summary>
    public List<SettingsSection> Sections { get; set; } = [];
    /// <summary>The graphics quality presets; null in an older schema (no preset row).</summary>
    public SettingsPresets? Presets { get; set; }
    /// <summary>The graphics look; null in an older schema (no look row).</summary>
    public SettingsLooks? Looks { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, SettingsSchemaJson.Default.SettingsSchema);

    /// <summary>The file's schema, or null when it is missing, unreadable or of a newer format.</summary>
    public static SettingsSchema? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var schema = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsSchemaJson.Default.SettingsSchema);
            return schema is { Format: <= CurrentFormat } ? schema : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

public sealed class SettingsSection
{
    public string Name { get; set; } = "";
    public List<SettingDescription> Settings { get; set; } = [];
}

/// <summary>
/// The quality presets: <see cref="Key"/> is a top-level <c>meitou.user.json</c> key holding one of <see cref="Names"/>. The game saves the preset
/// its settings sit at and, at start, applies a saved one they do not sit at; <see cref="Custom"/> applies nothing. A launcher that changes one of
/// <see cref="Keys"/> (the <c>graphics</c> keys a preset sets) saves <see cref="Custom"/>, so the game keeps that change.
/// </summary>
public sealed class SettingsPresets
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Custom { get; set; } = "";
    /// <summary>The preset shown while nothing is saved (the game's start settings).</summary>
    public string Default { get; set; } = "";
    public List<string> Keys { get; set; } = [];
    /// <summary>
    /// The game's option that picks a preset for the hardware: <c>&lt;exe&gt; &lt;Detect&gt; &lt;file&gt;</c> writes a JSON object whose <c>preset</c> is one of
    /// <see cref="Names"/> and whose <c>summary</c> is a line to show. Null in an older schema (no detection).
    /// </summary>
    public string? Detect { get; set; }
}

/// <summary>
/// The graphics look: Faithful (<see cref="FaithfulKey"/>, a top-level <c>meitou.user.json</c> key, true) or a Meitou look preset (false), named by
/// <see cref="PresetKey"/>: one of <see cref="Names"/> (the first, Meitou's own, is saved as no key) or one of the player's, the <c>name</c> of a JSON file
/// in <see cref="Folder"/> beside <c>meitou.user.json</c>. A launcher that changes the look removes <see cref="Keys"/> (the <c>graphics</c> keys a look
/// sets); at start the game sets a look key that is not saved from the look.
/// </summary>
public sealed class SettingsLooks
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string FaithfulKey { get; set; } = "";
    public string PresetKey { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Folder { get; set; } = "";
    public List<string> Keys { get; set; } = [];
}

public enum SettingKind { Toggle, Slider }

/// <summary>One setting: a top-level key of <c>meitou.user.json</c>, or (<see cref="Graphics"/>) a Tab panel slider or switch under <c>graphics</c>
/// (a slider by its label, a switch by <c>Meitou: </c> and its label, saved 1 on, 0 off).</summary>
public sealed record SettingDescription
{
    public string Key { get; set; } = "";
    public bool Graphics { get; set; }
    public SettingKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The value when the key is missing (a toggle: 1 on, 0 off); null when it depends on the mode or the computer, and the slider then sits at
    /// <see cref="Shown"/> (a graphics toggle offers the game's default as a choice of its own).</summary>
    public float? Default { get; set; }
    public float Shown { get; set; }
    /// <summary>A toggle's two states.</summary>
    public string On { get; set; } = "On";
    public string Off { get; set; } = "Off";
    public float Min { get; set; }
    public float Max { get; set; } = 1;
    public float Step { get; set; } = 1;
    public bool Logarithmic { get; set; }
    /// <summary>A slider whose stored value falls as the setting improves (texture quality's level 0 is Maximum): drawn mirrored, so
    /// right is always more; the value stored stays the same.</summary>
    public bool Reversed { get; set; }
    /// <summary>How a slider value reads: <see cref="Labels"/>[rounded value] when given, else value x <see cref="Scale"/> in <see cref="Format"/>
    /// plus <see cref="Suffix"/>; below <see cref="OffBelow"/> it reads <see cref="OffText"/>.</summary>
    public string[]? Labels { get; set; }
    public string Format { get; set; } = "0";
    public float Scale { get; set; } = 1;
    public string Suffix { get; set; } = "";
    public float? OffBelow { get; set; }
    public string OffText { get; set; } = "Off";

    public string Text(float value)
    {
        if (OffBelow is { } off && value < off) return OffText;
        if (Labels is { Length: > 0 } labels) return labels[Math.Clamp((int)MathF.Round(value - Min), 0, labels.Length - 1)];
        return (value * Scale).ToString(Format, System.Globalization.CultureInfo.InvariantCulture) + Suffix;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsSchema))]
sealed partial class SettingsSchemaJson : JsonSerializerContext;
