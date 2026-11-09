using System.Globalization;
using System.Reflection;

namespace Meitou.Modding;

/// <summary>
/// Marks a public property of an object a mod registers with <see cref="ModContext.AddSettings"/> (docs/modding.md "Settings") as a setting on the
/// Settings screen's Mods page: a <c>bool</c> is an Off | On switch, an <c>int</c>, <c>float</c> or <c>double</c> a slider over
/// <see cref="Min"/> to <see cref="Max"/>, an enum or a <c>string</c> with <see cref="Options"/> a list.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string label) : Attribute
{
    public string Label { get; } = label;

    /// <summary>
    /// The line under the label: what the setting does.
    /// </summary>
    public string Hint { get; init; } = "";

    /// <summary>
    /// A number's range; both are required for a number.
    /// </summary>
    public double Min { get; init; } = double.NaN;

    public double Max { get; init; } = double.NaN;

    /// <summary>
    /// A number's step; 0 means 1 for a whole number, a hundredth of the range otherwise.
    /// </summary>
    public double Step { get; init; }

    /// <summary>
    /// A string's choices; the chosen string is the value.
    /// </summary>
    public string[] Options { get; init; } = [];
}

public enum ModSettingKind { Switch, Slider, List }

/// <summary>
/// One setting a mod registered: how the Settings screen draws it, and the property it reads and writes. <see cref="Set"/> runs on the interface's
/// thread, outside the tick: a mod copies what its simulation needs in a serial step (see docs/modding.md "Settings").
/// </summary>
public sealed class ModSetting
{
    readonly object _target;
    readonly PropertyInfo _property;
    readonly string[] _options;

    internal ModSetting(ModSettingsGroup group, object target, PropertyInfo property, SettingAttribute about)
    {
        Group = group;
        this._target = target;
        this._property = property;
        Label = about.Label;
        Hint = about.Hint;
        var type = property.PropertyType;
        Kind = KindOf(property, type, about);
        _options = type.IsEnum ? Enum.GetNames(type) : about.Options;
        if (Kind == ModSettingKind.Slider) (Min, Max, Step) = Range(property, type, about);
        Default = Get();
        if (Kind == ModSettingKind.List && Choice < 0)
            throw new ArgumentException($"Setting '{property.Name}': its value '{Format(Default)}' is not one of its options.");
    }

    public ModSettingsGroup Group { get; }

    /// <summary>
    /// The property's name: keys the saved value.
    /// </summary>
    public string Key => _property.Name;

    public string Label { get; }
    public string Hint { get; }
    public ModSettingKind Kind { get; }
    public double Min { get; }
    public double Max { get; }
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
    /// True for an <c>int</c> slider.
    /// </summary>
    public bool Whole => _property.PropertyType == typeof(int);

    public object? Get() => _property.GetValue(_target);

    /// <summary>
    /// Sets the property (a number clamped to the range and to the step, a list value by name) and tells the group's
    /// <see cref="ModSettingsGroup.Changed"/>.
    /// </summary>
    public void Set(object value) => Apply(Convert(value));

    /// <summary>
    /// Restores <see cref="Default"/> as it was registered, not snapped to the step.
    /// </summary>
    public void Reset() => Apply(Default);

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

    public bool IsDefault => Equals(Get(), Default);

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

    static ModSettingKind KindOf(PropertyInfo property, Type type, SettingAttribute about)
    {
        if (type == typeof(bool)) return ModSettingKind.Switch;
        if (type == typeof(int) || type == typeof(float) || type == typeof(double)) return ModSettingKind.Slider;
        if (type.IsEnum || type == typeof(string) && about.Options.Length > 0) return ModSettingKind.List;
        string reason = $"a {type.Name} can't be a setting (bool, int, float, double, an enum, or a string with Options).";
        throw new ArgumentException($"Setting '{property.Name}': {reason}");
    }

    static (double Min, double Max, double Step) Range(PropertyInfo property, Type type, SettingAttribute about)
    {
        if (!double.IsFinite(about.Min) || !double.IsFinite(about.Max) || about.Max <= about.Min)
            throw new ArgumentException($"Setting '{property.Name}': a number needs Min and Max, with Max above Min.");
        double step = about.Step > 0 ? about.Step : type == typeof(int) ? 1 : (about.Max - about.Min) / 100;
        return (about.Min, about.Max, type == typeof(int) ? Math.Max(1, Math.Round(step)) : step);
    }

    object? Parse(string text) => Kind switch
    {
        ModSettingKind.Switch => bool.TryParse(text, out bool b) ? b : null,
        ModSettingKind.Slider => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double d) && double.IsFinite(d) ? d : null,
        _ => _options.FirstOrDefault(o => string.Equals(o, text, StringComparison.Ordinal)),
    };

    object Convert(object value)
    {
        var type = _property.PropertyType;
        if (Kind == ModSettingKind.Switch) return (bool)value;
        if (Kind == ModSettingKind.List)
        {
            string name = value is string s ? s : Format(value);
            if (!_options.Contains(name)) throw new ArgumentException($"Setting '{Key}': '{name}' is not one of its options.");
            return type.IsEnum ? Enum.Parse(type, name) : name;
        }
        double v = Math.Clamp(System.Convert.ToDouble(value, CultureInfo.InvariantCulture), Min, Max);
        v = Math.Clamp(Min + Math.Round((v - Min) / Step) * Step, Min, Max);
        if (type == typeof(int)) return (int)Math.Round(v);
        return type == typeof(float) ? (float)v : Math.Round(v, 10);
    }

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

    public string ModId { get; }

    /// <summary>
    /// The mod's name (its <c>.mod</c> file), the group's title.
    /// </summary>
    public string Name { get; }

    public IReadOnlyList<ModSetting> Settings { get; }

    /// <summary>
    /// After the player changed a setting (on the interface's thread).
    /// </summary>
    public event Action<ModSetting>? Changed;

    internal void RaiseChanged(ModSetting setting) => Changed?.Invoke(setting);

    ModSetting Read(object target, PropertyInfo property)
    {
        if (!property.CanRead || !property.CanWrite || property.SetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0)
            throw new ArgumentException($"Setting '{property.Name}': it needs a public get and set.");
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
}