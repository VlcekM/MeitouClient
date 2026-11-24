using System.Globalization;
using System.Reflection;

namespace Meitou.Modding;

/// <summary>
/// Marks a public property of an object a mod registers with <see cref="ModContext.AddSettings"/> (docs/modding.md "Settings") as a setting on the
/// Settings screen's Mods page: a <c>bool</c> is an Off | On switch, an <c>int</c>, <c>float</c> or <c>double</c> a slider over
/// <see cref="Min"/> to <see cref="Max"/>, an enum or a <c>string</c> with <see cref="Options"/> a list, a <c>string</c> without options a free text
/// (at most <see cref="MaxLength"/> characters, one line), an <see cref="System.Action"/> a button that runs it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string label) : Attribute
{
    /// <summary>
    /// The setting's name on the Mods page.
    /// </summary>
    public string Label { get; } = label;

    /// <summary>
    /// The line under the label: what the setting does.
    /// </summary>
    public string Hint { get; init; } = "";

    /// <summary>
    /// A number's range; both are required for a number.
    /// </summary>
    public double Min { get; init; } = double.NaN;

    /// <summary>
    /// A number's upper bound (see <see cref="Min"/>).
    /// </summary>
    public double Max { get; init; } = double.NaN;

    /// <summary>
    /// A number's step; 0 means 1 for a whole number, a hundredth of the range otherwise.
    /// </summary>
    public double Step { get; init; }

    /// <summary>
    /// A string's choices; the chosen string is the value.
    /// </summary>
    public string[] Options { get; init; } = [];

    /// <summary>
    /// A free text's longest length, 1 to <see cref="ModSetting.MaxTextLength"/>; 0 means <see cref="ModSetting.DefaultTextLength"/>.
    /// </summary>
    public int MaxLength { get; init; }

    /// <summary>
    /// The sub-group the setting is shown under on the Mods page: a header row with the setting's rows under it (empty: the mod's group itself).
    /// </summary>
    public string SubGroup { get; init; } = "";

    /// <summary>
    /// The name of a public <c>bool</c> property of the same object: the setting is shown only while it is true (empty: always shown).
    /// </summary>
    public string VisibleWhen { get; init; } = "";

    /// <summary>
    /// A button's caption (an <see cref="System.Action"/> setting); empty: the label.
    /// </summary>
    public string Button { get; init; } = "";
}

/// <summary>
/// How the Settings screen draws a setting.
/// </summary>
public enum ModSettingKind
{
    /// <summary>
    /// A <c>bool</c>: an Off | On switch.
    /// </summary>
    Switch,
    /// <summary>
    /// An <c>int</c>, <c>float</c> or <c>double</c>: a slider from <see cref="ModSetting.Min"/> to <see cref="ModSetting.Max"/>.
    /// </summary>
    Slider,
    /// <summary>
    /// An enum, or a <c>string</c> with options: a list of choices.
    /// </summary>
    List,
    /// <summary>
    /// A <c>string</c> without options: one line of free text, typed in a box.
    /// </summary>
    Text,
    /// <summary>
    /// An <see cref="System.Action"/>: a button that runs it (<see cref="ModSetting.Press"/>); it has no value to keep.
    /// </summary>
    Action,
}

/// <summary>
/// One setting a mod registered: how the Settings screen draws it, and the property it reads and writes. <see cref="Set"/> runs on the interface's
/// thread, outside the tick: a mod copies what its simulation needs in a serial step (see docs/modding.md "Settings").
/// </summary>
public sealed class ModSetting
{
    /// <summary>
    /// A free text's length when <see cref="SettingAttribute.MaxLength"/> is 0.
    /// </summary>
    public const int DefaultTextLength = 256;

    /// <summary>
    /// The longest <see cref="SettingAttribute.MaxLength"/> a free text may have.
    /// </summary>
    public const int MaxTextLength = 4096;

    /// <summary>
    /// The object the mod registered, whose property this is.
    /// </summary>
    readonly object _target;
    /// <summary>
    /// The marked property, read and written by reflection.
    /// </summary>
    readonly PropertyInfo _property;
    /// <summary>
    /// The list's choices (an enum's names or the attribute's options); empty for the other kinds.
    /// </summary>
    readonly string[] _options;
    /// <summary>
    /// The property <see cref="Visible"/> reads; null when the setting is always shown.
    /// </summary>
    readonly PropertyInfo? _visibleWhen;
    /// <summary>
    /// Whether <see cref="Visible"/> has logged a throw from its property already (it logs once, not every frame).
    /// </summary>
    bool _visibleFailed;

    /// <summary>
    /// Reads the attribute and the property: its kind, choices and range; the value now becomes <see cref="Default"/>. Throws when the property
    /// cannot be a setting, a list's value is not one of its options, or a <c>VisibleWhen</c> is not a public <c>bool</c> property.
    /// </summary>
    internal ModSetting(ModSettingsGroup group, object target, PropertyInfo property, SettingAttribute about)
    {
        Group = group;
        this._target = target;
        this._property = property;
        Label = about.Label;
        Hint = about.Hint;
        Button = about.Button.Length > 0 ? about.Button : about.Label;
        SubGroup = about.SubGroup.Trim();
        VisibleWhen = about.VisibleWhen.Trim();
        var type = property.PropertyType;
        Kind = KindOf(property, type, about);
        _options = type.IsEnum ? Enum.GetNames(type) : about.Options;
        if (Kind == ModSettingKind.Slider) (Min, Max, Step) = Range(property, type, about);
        if (Kind == ModSettingKind.Text) MaxLength = TextLength(property, about);
        if (VisibleWhen.Length > 0) _visibleWhen = VisibleProperty(target, property, VisibleWhen);
        Default = Get();
        if (Kind == ModSettingKind.List && Choice < 0)
            throw new ArgumentException($"Setting '{property.Name}': its value '{Format(Default)}' is not one of its options.");
    }

    /// <summary>
    /// The mod's group this setting is shown in.
    /// </summary>
    public ModSettingsGroup Group { get; }

    /// <summary>
    /// The property's name: keys the saved value.
    /// </summary>
    public string Key => _property.Name;

    /// <summary>
    /// The setting's name on the Mods page.
    /// </summary>
    public string Label { get; }
    /// <summary>
    /// The line under the label: what the setting does.
    /// </summary>
    public string Hint { get; }
    /// <summary>
    /// A button's caption (<see cref="ModSettingKind.Action"/>): the attribute's, else the label.
    /// </summary>
    public string Button { get; }
    /// <summary>
    /// The sub-group the setting is shown under (empty: the mod's group itself).
    /// </summary>
    public string SubGroup { get; }
    /// <summary>
    /// The name of the property <see cref="Visible"/> reads (empty: always shown).
    /// </summary>
    public string VisibleWhen { get; }
    /// <summary>
    /// Whether the page shows the setting now: its <see cref="VisibleWhen"/> property is true, or it has none; a throw from that property is logged
    /// once and the setting stays shown.
    /// </summary>
    public bool Visible
    {
        get
        {
            if (_visibleWhen is null) return true;
            try
            {
                return _visibleWhen.GetValue(_target) is true;
            }
            catch (Exception e)
            {
                if (!_visibleFailed)
                    Console.Error.WriteLine($"mod       {Group.ModId}: setting '{Key}' VisibleWhen '{VisibleWhen}' failed: {e}");

                Content.CodeModFaults.Tell(Group.ModId, e);
                _visibleFailed = true;
                return true;
            }
        }
    }
    /// <summary>
    /// How the Settings screen draws it.
    /// </summary>
    public ModSettingKind Kind { get; }
    /// <summary>
    /// A slider's lowest value; 0 for the other kinds.
    /// </summary>
    public double Min { get; }
    /// <summary>
    /// A slider's highest value; 0 for the other kinds.
    /// </summary>
    public double Max { get; }
    /// <summary>
    /// A slider's step (from the attribute, or 1 for an <c>int</c>, a hundredth of the range otherwise).
    /// </summary>
    public double Step { get; }

    /// <summary>
    /// A list's choices: an enum's names or the attribute's options.
    /// </summary>
    public IReadOnlyList<string> Options => _options;

    /// <summary>
    /// The value when the mod registered it, before a saved value: what Reset restores.
    /// </summary>
    public object? Default { get; }

    /// <summary>
    /// A free text's longest length (0 for the other kinds).
    /// </summary>
    public int MaxLength { get; }

    /// <summary>
    /// True for an <c>int</c> slider.
    /// </summary>
    public bool Whole => _property.PropertyType == typeof(int);

    /// <summary>
    /// The property's value now.
    /// </summary>
    public object? Get() => _property.GetValue(_target);

    /// <summary>
    /// Sets the property (a number clamped to the range and to the step, a list value by name) and tells the group's
    /// <see cref="ModSettingsGroup.Changed"/>.
    /// </summary>
    public void Set(object value) => Apply(Convert(value));

    /// <summary>
    /// Restores <see cref="Default"/> as it was registered, not snapped to the step; nothing for a button.
    /// </summary>
    public void Reset()
    {
        if (Kind != ModSettingKind.Action) Apply(Default);
    }

    /// <summary>
    /// Runs a button's action (<see cref="ModSettingKind.Action"/>) as the page's click does, on the interface's thread; throws for another kind.
    /// </summary>
    public void Press()
    {
        if (Kind != ModSettingKind.Action) throw new InvalidOperationException($"Setting '{Key}' is not a button.");
        (Get() as Action)?.Invoke();
    }

    /// <summary>
    /// Sets a kept value (a preset's) as <see cref="Set"/> does, telling <see cref="ModSettingsGroup.Changed"/>; false when the text is not a value
    /// of this setting.
    /// </summary>
    internal bool Restore(string text)
    {
        if (Kind == ModSettingKind.Action || Parse(text) is not { } value) return false;

        Set(value);
        return true;
    }

    /// <summary>
    /// Sets the property and tells the group's <see cref="ModSettingsGroup.Changed"/>; nothing when the value is the same.
    /// </summary>
    void Apply(object? next)
    {
        if (Equals(next, Get())) return;
        _property.SetValue(_target, next);
        Group.RaiseChanged(this);
    }

    /// <summary>
    /// The value as saved: invariant text (true/false, a number, a choice's name).
    /// </summary>
    public string Text => Format(Get());

    /// <summary>
    /// Whether the value is the one the mod registered.
    /// </summary>
    public bool IsDefault => Kind == ModSettingKind.Action || Equals(Get(), Default);

    /// <summary>
    /// The list's chosen index, -1 when the value is not one of <see cref="Options"/>.
    /// </summary>
    public int Choice => Kind == ModSettingKind.List ? Array.IndexOf(_options, Format(Get())) : -1;

    /// <summary>
    /// Sets a saved value; false (nothing changed) when the text is not a value of this setting.
    /// </summary>
    internal bool Load(string text)
    {
        object? value = Parse(text);
        if (value is null) return false;
        _property.SetValue(_target, Convert(value));
        return true;
    }

    /// <summary>
    /// The kind a property's type makes; throws for a type that cannot be a setting.
    /// </summary>
    static ModSettingKind KindOf(PropertyInfo property, Type type, SettingAttribute about)
    {
        if (type == typeof(Action)) return ModSettingKind.Action;
        if (type == typeof(bool)) return ModSettingKind.Switch;
        if (type == typeof(int) || type == typeof(float) || type == typeof(double)) return ModSettingKind.Slider;
        if (type.IsEnum || type == typeof(string) && about.Options.Length > 0) return ModSettingKind.List;
        if (type == typeof(string)) return ModSettingKind.Text;
        throw new ArgumentException($"Setting '{property.Name}': a {type.Name} can't be a setting (bool, int, float, double, an enum, a string or an Action).");
    }

    /// <summary>
    /// A free text's longest length from the attribute; throws for one outside 0 to <see cref="MaxTextLength"/>.
    /// </summary>
    static int TextLength(PropertyInfo property, SettingAttribute about)
    {
        if (about.MaxLength is < 0 or > MaxTextLength)
            throw new ArgumentException($"Setting '{property.Name}': MaxLength must be 0 to {MaxTextLength}.");
        return about.MaxLength == 0 ? DefaultTextLength : about.MaxLength;
    }

    /// <summary>
    /// The public <c>bool</c> property a <c>VisibleWhen</c> names; throws when the object has no such property.
    /// </summary>
    static PropertyInfo VisibleProperty(object target, PropertyInfo property, string name)
    {
        var type = target.GetType();
        var visible = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        bool usable = visible is not null && visible.PropertyType == typeof(bool) && visible.CanRead && visible.GetIndexParameters().Length == 0;
        if (usable) return visible!;

        throw new ArgumentException($"Setting '{property.Name}': VisibleWhen '{name}' is not a public bool property of {type.Name}.");
    }

    /// <summary>
    /// A slider's range and step from the attribute; throws without a finite <see cref="SettingAttribute.Min"/> below
    /// <see cref="SettingAttribute.Max"/>.
    /// </summary>
    static (double Min, double Max, double Step) Range(PropertyInfo property, Type type, SettingAttribute about)
    {
        if (!double.IsFinite(about.Min) || !double.IsFinite(about.Max) || about.Max <= about.Min)
            throw new ArgumentException($"Setting '{property.Name}': a number needs Min and Max, with Max above Min.");
        double step = about.Step > 0 ? about.Step : type == typeof(int) ? 1 : (about.Max - about.Min) / 100;
        return (about.Min, about.Max, type == typeof(int) ? Math.Max(1, Math.Round(step)) : step);
    }

    /// <summary>
    /// A saved text as a value of this setting's kind (invariant culture); null when it is not one.
    /// </summary>
    object? Parse(string text) => Kind switch
    {
        ModSettingKind.Switch => bool.TryParse(text, out bool b) ? b : null,
        ModSettingKind.Slider => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d
            : null,
        ModSettingKind.Text => text.Length <= MaxLength && !text.Any(char.IsControl) ? text : null,
        _ => _options.FirstOrDefault(o => string.Equals(o, text, StringComparison.Ordinal)),
    };

    /// <summary>
    /// A value converted to the property's type: a list value checked against the choices, a number clamped to the range and snapped to the step.
    /// </summary>
    object Convert(object value)
    {
        var type = _property.PropertyType;
        if (Kind == ModSettingKind.Action) throw new InvalidOperationException($"Setting '{Key}' is a button: it has no value to set.");
        if (Kind == ModSettingKind.Switch) return (bool)value;
        if (Kind == ModSettingKind.Text) return OneLine(value as string ?? Format(value));
        if (Kind == ModSettingKind.List)
        {
            string name = value is string s ? s : Format(value);
            if (!_options.Contains(name)) throw new ArgumentException($"Setting '{Key}': '{name}' is not one of its options.");
            return type.IsEnum ? Enum.Parse(type, name) : name;
        }
        double v = Math.Clamp(System.Convert.ToDouble(value, CultureInfo.InvariantCulture), Min, Max);
        v = Math.Clamp(Min + Math.Round((v - Min) / Step) * Step, Min, Max);
        if (type == typeof(int)) return (int)Math.Round(v);
        if (type == typeof(float)) return (float)v;
        return Math.Round(v, 10);
    }

    /// <summary>
    /// A free text as kept: control characters (line breaks, tabs) as spaces, cut to <see cref="MaxLength"/>.
    /// </summary>
    string OneLine(string text)
    {
        var line = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]);
        return line.Length <= MaxLength ? line : line[..MaxLength];
    }

    /// <summary>
    /// A value as invariant text (true/false, a round-trip number, an enum's or choice's name).
    /// </summary>
    static string Format(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "",
    };
}

/// <summary>
/// The settings one mod registered, in the order of its properties; shown as one group under the mod on the Mods page.
/// </summary>
public sealed class ModSettingsGroup
{
    /// <summary>
    /// Reads the public properties of <paramref name="target"/> marked [Setting], in declaration order; throws when it has none or one cannot be
    /// a setting.
    /// </summary>
    internal ModSettingsGroup(string modId, string name, object target)
    {
        ModId = modId;
        Name = name;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        Settings = [.. target.GetType().GetProperties(flags)
            .Where(p => p.GetCustomAttribute<SettingAttribute>() is not null)
            .OrderBy(p => p.MetadataToken)
            .Select(p => Read(target, p))];
        if (Settings.Count == 0) throw new ArgumentException($"{target.GetType().Name} has no property marked [Setting].");
    }

    /// <summary>
    /// The id of the mod that registered the settings.
    /// </summary>
    public string ModId { get; }

    /// <summary>
    /// The mod's name (its <c>.mod</c> file), the group's title.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The settings, in the order of their properties.
    /// </summary>
    public IReadOnlyList<ModSetting> Settings { get; }

    /// <summary>
    /// After the player changed a setting (on the interface's thread).
    /// </summary>
    public event Action<ModSetting>? Changed;

    /// <summary>
    /// Tells <see cref="Changed"/> that a setting was changed.
    /// </summary>
    internal void RaiseChanged(ModSetting setting) => Changed?.Invoke(setting);

    /// <summary>
    /// The setting of one marked property; throws unless it has a public get and set and no index.
    /// </summary>
    ModSetting Read(object target, PropertyInfo property)
    {
        bool button = property.PropertyType == typeof(Action);
        bool settable = button || property.CanWrite && property.SetMethod?.IsPublic == true;
        if (!property.CanRead || !settable || property.GetIndexParameters().Length > 0)
            throw new ArgumentException($"Setting '{property.Name}': it needs a public get and set (a button a public get).");
        return new ModSetting(this, target, property, property.GetCustomAttribute<SettingAttribute>()!);
    }

    /// <summary>
    /// Saved values (key to text) onto the settings; returns the keys that were not a value of their setting, for the log.
    /// </summary>
    internal List<string> Load(IReadOnlyDictionary<string, string> saved)
    {
        var refused = new List<string>();
        foreach (var (key, text) in saved)
            if (Settings.FirstOrDefault(s => s.Key == key) is not { } setting || !setting.Load(text)) refused.Add(key);
        return refused;
    }

    /// <summary>
    /// The longest preset name and the most presets a group keeps.
    /// </summary>
    public const int MaxPresetName = 64, MaxPresets = 64;

    /// <summary>
    /// The named presets (docs/modding.md "Settings"): each the values of the settings as saved, in the order first saved.
    /// </summary>
    readonly List<(string Name, Dictionary<string, string> Values)> _presets = [];

    /// <summary>
    /// The mod's translations its labels, hints and choices are shown through; null: as written.
    /// </summary>
    internal Meitou.Localisation.ModTranslations? Texts { get; init; }

    /// <summary>
    /// A label, hint or choice of the group as the page shows it: in the mod's catalogues, else as written.
    /// </summary>
    internal string Shown(string text) => Texts?.Text(text) ?? text;

    /// <summary>
    /// The presets' names, in the order first saved.
    /// </summary>
    public IReadOnlyList<string> Presets => [.. _presets.Select(p => p.Name)];

    /// <summary>
    /// After a preset was saved or deleted (on the interface's thread); the game keeps the presets in <c>meitou.user.json</c>.
    /// </summary>
    public event Action<ModSettingsGroup>? PresetsChanged;

    /// <summary>
    /// Saves the settings' values now (not the buttons') as the preset <paramref name="name"/> (1 to <see cref="MaxPresetName"/> characters on one
    /// line), replacing one of that name; false, with nothing saved, for a bad name or when <see cref="MaxPresets"/> other presets exist.
    /// </summary>
    public bool SavePreset(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!ValidPresetName(name)) return false;

        int at = _presets.FindIndex(p => p.Name == name);
        if (at < 0 && _presets.Count >= MaxPresets) return false;

        var values = Settings.Where(s => s.Kind != ModSettingKind.Action).ToDictionary(s => s.Key, s => s.Text);
        if (at < 0) _presets.Add((name, values));
        else _presets[at] = (name, values);
        PresetsChanged?.Invoke(this);
        return true;
    }

    /// <summary>
    /// Sets the settings the preset <paramref name="name"/> holds to its values, each as <see cref="ModSetting.Set"/> (telling
    /// <see cref="Changed"/>); a value that is no longer one of its setting's is skipped. False when there is no such preset.
    /// </summary>
    public bool ApplyPreset(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int at = _presets.FindIndex(p => p.Name == name);
        if (at < 0) return false;

        foreach (var (key, text) in _presets[at].Values)
            Settings.FirstOrDefault(s => s.Key == key)?.Restore(text);
        return true;
    }

    /// <summary>
    /// Deletes the preset <paramref name="name"/>; false when there is none.
    /// </summary>
    public bool DeletePreset(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_presets.RemoveAll(p => p.Name == name) == 0) return false;

        PresetsChanged?.Invoke(this);
        return true;
    }

    /// <summary>
    /// The presets as kept in the config: name to setting key to text.
    /// </summary>
    internal Dictionary<string, Dictionary<string, string>> PresetValues() =>
        _presets.ToDictionary(p => p.Name, p => new Dictionary<string, string>(p.Values));

    /// <summary>
    /// Takes the presets kept in the config (<paramref name="saved"/>, null: none), dropping a bad name, a null entry and those past
    /// <see cref="MaxPresets"/>.
    /// </summary>
    internal void UsePresets(IReadOnlyDictionary<string, Dictionary<string, string>>? saved)
    {
        _presets.Clear();
        foreach (var (name, values) in saved ?? new Dictionary<string, Dictionary<string, string>>())
        {
            if (values is null || !ValidPresetName(name) || _presets.Count >= MaxPresets) continue;
            _presets.Add((name, new Dictionary<string, string>(values)));
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> can name a preset: 1 to <see cref="MaxPresetName"/> characters, not blank, no control character.
    /// </summary>
    static bool ValidPresetName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxPresetName && !name.Any(char.IsControl);
}