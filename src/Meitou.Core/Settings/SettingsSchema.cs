using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Settings;

/// <summary>
/// The settings a game release describes for the launcher: <c>settings-schema.json</c> beside its executable, written by the game
/// (<c>--settings-schema</c>) from the same names it saves under, so the launcher never repeats them (docs/launcher.md "Graphics").
/// </summary>
public sealed class SettingsSchema
{
    /// <summary>
    /// The schema's file name, beside the game's executable.
    /// </summary>
    public const string FileName = "settings-schema.json";
    /// <summary>
    /// The layout of this file. Adding properties keeps it; anything a reader would misread raises it, and the launcher then shows nothing.
    /// </summary>
    public const int CurrentFormat = 1;

    /// <summary>
    /// The layout this file was written in (<see cref="CurrentFormat"/> when written); <see cref="Read"/> refuses a newer one.
    /// </summary>
    public int Format { get; set; } = CurrentFormat;
    /// <summary>
    /// The <c>meitou.user.json</c> layout this release reads and writes (its <c>version</c> key).
    /// </summary>
    public int SettingsVersion { get; set; }
    /// <summary>
    /// The frame renderers this release contains; an older schema without the key is Vulkan-only.
    /// </summary>
    public List<string> Renderers { get; set; } = ["vulkan"];
    /// <summary>
    /// The sections, one launcher tab each, in the game's page order.
    /// </summary>
    public List<SettingsSection> Sections { get; set; } = [];
    /// <summary>
    /// The graphics quality presets; null in an older schema (no preset row).
    /// </summary>
    public SettingsPresets? Presets { get; set; }
    /// <summary>
    /// The graphics look; null in an older schema (no look row).
    /// </summary>
    public SettingsLooks? Looks { get; set; }

    /// <summary>
    /// The schema as indented camel-case JSON, nulls left out (what <c>--settings-schema</c> writes).
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, SettingsSchemaJson.Default.SettingsSchema);

    /// <summary>
    /// The file's schema, or null when it is missing, unreadable or of a newer format.
    /// </summary>
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

/// <summary>
/// One section of the settings: a launcher tab, a page of the game's menu.
/// </summary>
public sealed class SettingsSection
{
    /// <summary>
    /// The section's title, as the game's menu shows it.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The section's settings in menu order.
    /// </summary>
    public List<SettingDescription> Settings { get; set; } = [];
    /// <summary>
    /// The section's features, as the game's menu folds them; null in an older schema (every setting a row of its own).
    /// </summary>
    public List<SettingsGroup>? Groups { get; set; }
}

/// <summary>
/// A feature: one row headed by the setting <see cref="Header"/> (a key of the section), or by <see cref="Title"/> and <see cref="Description"/> when
/// nothing heads it, with <see cref="Members"/> (keys, in order) folded under it. It shows where its first setting is.
/// </summary>
public sealed class SettingsGroup
{
    /// <summary>
    /// The key of the setting that heads the row; null when the row is headed by <see cref="Title"/> alone.
    /// </summary>
    public string? Header { get; set; }
    /// <summary>
    /// The row's title when no setting heads it.
    /// </summary>
    public string Title { get; set; } = "";
    /// <summary>
    /// The row's description when no setting heads it.
    /// </summary>
    public string Description { get; set; } = "";
    /// <summary>
    /// The keys of the settings folded under the row, in order.
    /// </summary>
    public List<string> Members { get; set; } = [];
}

/// <summary>
/// The quality presets: <see cref="Key"/> is a top-level <c>meitou.user.json</c> key holding one of <see cref="Names"/>. The game saves the preset
/// its settings sit at and, at start, applies a saved one they do not sit at; <see cref="Custom"/> applies nothing. A launcher that changes one of
/// <see cref="Keys"/> (the <c>graphics</c> keys a preset sets) saves <see cref="Custom"/>, so the game keeps that change.
/// </summary>
public sealed class SettingsPresets
{
    /// <summary>
    /// The top-level <c>meitou.user.json</c> key that holds the preset's name.
    /// </summary>
    public string Key { get; set; } = "";
    /// <summary>
    /// The preset row's title.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The preset row's description.
    /// </summary>
    public string Description { get; set; } = "";
    /// <summary>
    /// The presets' names, in the order they are offered.
    /// </summary>
    public List<string> Names { get; set; } = [];
    /// <summary>
    /// The name saved when the settings sit at no preset; the game then applies nothing.
    /// </summary>
    public string Custom { get; set; } = "";
    /// <summary>
    /// The preset shown while nothing is saved (the game's start settings).
    /// </summary>
    public string Default { get; set; } = "";
    /// <summary>
    /// The <c>graphics</c> keys a preset sets; changing one of them makes the preset <see cref="Custom"/>.
    /// </summary>
    public List<string> Keys { get; set; } = [];
    /// <summary>
    /// The game's option that picks a preset for the hardware: <c>&lt;exe&gt; &lt;Detect&gt; &lt;file&gt;</c> writes a JSON object whose
    /// <c>preset</c> is one of
    /// <see cref="Names"/> and whose <c>summary</c> is a line to show. Null in an older schema (no detection).
    /// </summary>
    public string? Detect { get; set; }
}

/// <summary>
/// The graphics look: Faithful (<see cref="FaithfulKey"/>, a top-level <c>meitou.user.json</c> key, true) or a Meitou look preset (false), named by
/// <see cref="PresetKey"/>: one of <see cref="Presets"/> (the first, Meitou's own, is saved as no key) or one of the player's, a JSON file in
/// <see cref="Folder"/> beside <c>meitou.user.json</c> with its <c>name</c> and <c>values</c>. A look is a set of <c>graphics</c> values: Faithful's
/// are
/// <see cref="Faithful"/>; a preset's are <see cref="Meitou"/> with its own values over them. Choosing a look writes them.
/// </summary>
public sealed class SettingsLooks
{
    /// <summary>
    /// The look row's title.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The look row's description.
    /// </summary>
    public string Description { get; set; } = "";
    /// <summary>
    /// The top-level <c>meitou.user.json</c> key that is true for the Faithful look.
    /// </summary>
    public string FaithfulKey { get; set; } = "";
    /// <summary>
    /// The top-level <c>meitou.user.json</c> key that names the Meitou look preset; missing for Meitou's own.
    /// </summary>
    public string PresetKey { get; set; } = "";
    /// <summary>
    /// The <c>graphics</c> values of the Faithful look, by key.
    /// </summary>
    public Dictionary<string, float> Faithful { get; set; } = [];
    /// <summary>
    /// The <c>graphics</c> values of the Meitou look, which every preset starts from, by key.
    /// </summary>
    public Dictionary<string, float> Meitou { get; set; } = [];
    /// <summary>
    /// The built-in look presets, Meitou's own first.
    /// </summary>
    public List<SettingsLook> Presets { get; set; } = [];
    /// <summary>
    /// The folder beside <c>meitou.user.json</c> that holds the player's own look presets.
    /// </summary>
    public string Folder { get; set; } = "";
    /// <summary>
    /// The look's own settings (the look tabs): what a preset saves, and what differs from the look counts as a change.
    /// </summary>
    public List<string> Keys { get; set; } = [];
    /// <summary>
    /// The Meitou version a preset saved now is marked with (<c>meitou</c> in its file).
    /// </summary>
    public string Made { get; set; } = "";
}

/// <summary>
/// A look preset: its name and the <c>graphics</c> values it sets over <see cref="SettingsLooks.Meitou"/>.
/// </summary>
public sealed class SettingsLook
{
    /// <summary>
    /// The preset's name, as offered and saved under <see cref="SettingsLooks.PresetKey"/>.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The <c>graphics</c> values the preset sets over Meitou's, by key.
    /// </summary>
    public Dictionary<string, float> Values { get; set; } = [];
}

/// <summary>
/// How a setting is shown.
/// </summary>
public enum SettingKind
{
    /// <summary>
    /// A switch between two states, saved 1 on and 0 off.
    /// </summary>
    Toggle,

    /// <summary>
    /// A slider between <see cref="SettingDescription.Min"/> and <see cref="SettingDescription.Max"/>.
    /// </summary>
    Slider
}

/// <summary>
/// One setting: a top-level key of <c>meitou.user.json</c>, or (<see cref="Graphics"/>) a Tab panel slider or switch under <c>graphics</c> (a slider
/// by its label, a switch by <c>Meitou: </c> and its label, saved 1 on, 0 off).
/// </summary>
public sealed record SettingDescription
{
    /// <summary>
    /// The key the value is saved under: top-level in <c>meitou.user.json</c>, or under <c>graphics</c> when <see cref="Graphics"/>.
    /// </summary>
    public string Key { get; set; } = "";
    /// <summary>
    /// Whether the setting is a Tab panel value under <c>graphics</c> rather than a top-level key.
    /// </summary>
    public bool Graphics { get; set; }
    /// <summary>
    /// Whether the setting is a switch or a slider.
    /// </summary>
    public SettingKind Kind { get; set; }
    /// <summary>
    /// The setting's label.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The setting's explanation, shown with it.
    /// </summary>
    public string Description { get; set; } = "";
    /// <summary>
    /// How much the setting costs (No / Low / Medium / High impact, Faster with Meitou), shown beside its name; null when not ranked.
    /// </summary>
    public string? Impact { get; set; }
    /// <summary>
    /// The value when the key is missing (a toggle: 1 on, 0 off); null when it depends on the mode or the computer, and the slider then sits
    /// at
    /// <see cref="Shown"/> (a graphics toggle offers the game's default as a choice of its own).
    /// </summary>
    public float? Default { get; set; }
    /// <summary>
    /// Where the slider sits while the key is missing and <see cref="Default"/> is null.
    /// </summary>
    public float Shown { get; set; }
    /// <summary>
    /// A toggle's two states.
    /// </summary>
    public string On { get; set; } = "On";
    /// <summary>
    /// The text of a toggle's off state.
    /// </summary>
    public string Off { get; set; } = "Off";
    /// <summary>
    /// The slider's lowest value.
    /// </summary>
    public float Min { get; set; }
    /// <summary>
    /// The slider's highest value.
    /// </summary>
    public float Max { get; set; } = 1;
    /// <summary>
    /// The slider's step between values.
    /// </summary>
    public float Step { get; set; } = 1;
    /// <summary>
    /// Whether the slider moves on a logarithmic scale (the launcher does so only when <see cref="Min"/> is above 0).
    /// </summary>
    public bool Logarithmic { get; set; }
    /// <summary>
    /// A slider whose stored value falls as the setting improves (texture quality's level 0 is Maximum): drawn mirrored, so right is always more; the
    /// value stored stays the same.
    /// </summary>
    public bool Reversed { get; set; }
    /// <summary>
    /// How a slider value reads: <see cref="Labels"/>[rounded value] when given, else value x <see cref="Scale"/> in <see cref="Format"/> plus
    /// <see cref="Suffix"/>; below <see cref="OffBelow"/> it reads <see cref="OffText"/>.
    /// </summary>
    public string[]? Labels { get; set; }
    /// <summary>
    /// The .NET number format of a slider's value (after <see cref="Scale"/>).
    /// </summary>
    public string Format { get; set; } = "0";
    /// <summary>
    /// The factor a slider's value is shown at (e.g. 100 for a percentage).
    /// </summary>
    public float Scale { get; set; } = 1;
    /// <summary>
    /// The text after a slider's shown value (a unit).
    /// </summary>
    public string Suffix { get; set; } = "";
    /// <summary>
    /// The value below which the slider reads <see cref="OffText"/>; null when it never does.
    /// </summary>
    public float? OffBelow { get; set; }
    /// <summary>
    /// The text a slider shows below <see cref="OffBelow"/>.
    /// </summary>
    public string OffText { get; set; } = "Off";
    /// <summary>
    /// A slider's named values (render scale's quality modes): a drop-down of them and Custom, the slider shown only under Custom; a value that is
    /// none of them reads Custom. Null: a plain slider (an older launcher shows that either way).
    /// </summary>
    public List<SettingMode>? Modes { get; set; }
    /// <summary>
    /// Shown only while another setting of the section is at one value (the DLSS preset while anti-aliasing is DLSS); null: always (an older launcher
    /// shows it either way).
    /// </summary>
    public SettingCondition? ShownWhen { get; set; }

    /// <summary>
    /// How the slider reads at <paramref name="value"/>: <see cref="OffText"/>, a label, or the scaled, formatted value with its suffix.
    /// </summary>
    public string Text(float value)
    {
        if (OffBelow is { } off && value < off) return OffText;
        if (Labels is { Length: > 0 } labels)
            return labels[Math.Clamp((int)MathF.Round(value - Min), 0, labels.Length - 1)];
        return (value * Scale).ToString(Format, System.Globalization.CultureInfo.InvariantCulture) + Suffix;
    }
}

/// <summary>
/// One of a slider's <see cref="SettingDescription.Modes"/>: its name and the value it writes.
/// </summary>
public sealed record SettingMode
{
    /// <summary>
    /// The mode's name in the drop-down.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The slider value the mode writes.
    /// </summary>
    public float Value { get; set; }

    /// <summary>
    /// Whether <paramref name="value"/> is this mode's, within half a slider <paramref name="step"/>.
    /// </summary>
    public bool Matches(float value, float step) => MathF.Abs(value - Value) <= step / 2;
}

/// <summary>
/// A setting (<see cref="Key"/>) at one of its whole values: a choice's index from its slider's left end, a switch's 1 or 0.
/// </summary>
public sealed record SettingCondition
{
    /// <summary>
    /// The key of the setting the condition looks at, in the same section.
    /// </summary>
    public string Key { get; set; } = "";
    /// <summary>
    /// The whole value that setting must be at.
    /// </summary>
    public float Value { get; set; }

    /// <summary>
    /// Whether <paramref name="value"/> rounds to <see cref="Value"/> (within half a step either way).
    /// </summary>
    public bool Matches(float value) => MathF.Abs(value - Value) < 0.5f;
}

/// <summary>
/// The source-generated JSON context for the schema (camel case, enums as strings, nulls left out); the launcher is Native AOT.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsSchema))]
sealed partial class SettingsSchemaJson : JsonSerializerContext;