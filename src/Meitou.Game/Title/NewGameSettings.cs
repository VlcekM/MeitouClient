using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Meitou.Game.Title;

/// <summary>
/// Per-world difficulty options, saved under the original's CAMERA floats and bools, a missing key taking its default (<c>GameplayOptions</c> save
/// <c>FUN_1403ee910</c>, load <c>FUN_1403eef20</c>, docs/game/character-stats.md "New-game advanced options").
/// </summary>
internal sealed record NewGameSettings
{
    /// <summary>
    /// A slider: CAMERA float key, label, range, help line.
    /// </summary>
    internal sealed record Option(string Key, string Label, float Min, float Max, int HelpLine);

    /// <summary>
    /// The sliders, in the panel's order.
    /// </summary>
    public static readonly Option[] Options =
    [
        new("ht", "Hunger time", .25f, 8, 557),
        new("cod", "Chance of death", .5f, 4, 558),
        new("gdm", "Global damage multiplier", .5f, 4, 559),
        new("ps", "Production speed", .5f, 2, 560),
        new("rs", "Research speed", .5f, 2, 561),
        new("bs", "Building speed", .5f, 2, 562),
        new("nnm", "Number of nests multiplier", .5f, 4, 563),
    ];

    /// <summary>
    /// A tick under the sliders: CAMERA bool key, label, the loader's default, help line (-1: the original sets none).
    /// </summary>
    internal sealed record Tick(string Key, string Label, bool Default, int HelpLine);

    /// <summary>
    /// The ticks under the sliders, in the panel's order.
    /// </summary>
    public static readonly Tick[] Ticks =
    [
        new("bl", "Bandits loot the player", true, 564),

        // Nothing in the original reads it (docs/game/character-stats.md); kept and saved as it is.
        new("ep", "Easy prospecting", false, -1),
    ];

    /// <summary>
    /// Each slider's value, in the order of <see cref="Options"/>.
    /// </summary>
    readonly float[] _values;

    /// <summary>
    /// Each tick's state, in the order of <see cref="Ticks"/>.
    /// </summary>
    readonly bool[] _ticks;

    /// <summary>
    /// The defaults: every slider at 1, every tick at its loader's default.
    /// </summary>
    public NewGameSettings()
        : this(Enumerable.Repeat(1f, Options.Length).ToArray(), Ticks.Select(t => t.Default).ToArray())
    {
    }

    /// <summary>
    /// Settings holding <paramref name="values"/> and <paramref name="ticks"/> (not copied).
    /// </summary>
    NewGameSettings(float[] values, bool[] ticks)
    {
        this._values = values;
        this._ticks = ticks;
    }

    /// <summary>
    /// Slider <paramref name="index"/>'s value.
    /// </summary>
    public float this[int index] => _values[index];

    /// <summary>
    /// Whether tick <paramref name="index"/> is set.
    /// </summary>
    public bool Ticked(int index) => _ticks[index];

    /// <summary>
    /// Bandits loot the player (the first tick).
    /// </summary>
    public bool BanditsLoot => _ticks[0];

    /// <summary>
    /// A copy with slider <paramref name="index"/> at <paramref name="value"/>, clamped to its range (1 for a value that is not finite).
    /// </summary>
    public NewGameSettings Set(int index, float value)
    {
        var copy = (float[])_values.Clone();
        copy[index] = float.IsFinite(value) ? Math.Clamp(value, Options[index].Min, Options[index].Max) : 1;
        return new(copy, _ticks);
    }

    /// <summary>
    /// A copy with tick <paramref name="index"/> set to <paramref name="value"/>.
    /// </summary>
    public NewGameSettings SetTick(int index, bool value)
    {
        var copy = (bool[])_ticks.Clone();
        copy[index] = value;
        return new(_values, copy);
    }

    /// <summary>
    /// The body options of a new world: these and the Gameplay settings' <paramref name="dismemberment"/>.
    /// </summary>
    public PopulationData Apply(PopulationData data, Simulation.Bodies.Dismemberment dismemberment) => data.WithBodyOptions(data.BodyOptions with
    {
        HungerTime = this[0],
        ChanceOfDeath = this[1],
        GlobalDamageMultiplier = this[2],
        Dismemberment = dismemberment,
    });

    /// <summary>
    /// Writes the options into a save. <c>bl</c> is the world's too (<c>PlayerState.BanditsLoot</c>, which the capture writes): both hold the same
    /// value, and an import needs it here.
    /// </summary>
    public void Write(SaveCamera camera)
    {
        for (int i = 0; i < Options.Length; i++) camera.Record.Floats[Options[i].Key] = this[i];
        for (int i = 0; i < Ticks.Length; i++) camera.Record.Bools[Ticks[i].Key] = _ticks[i];

        // Earlier Meitou builds saved the sliders under keys of their own; a save made from one of theirs drops them.
        foreach (var key in camera.Record.Floats.Keys.Where(k => k.StartsWith("meitou difficulty ", StringComparison.Ordinal)).ToList())
            camera.Record.Floats.Remove(key);
    }

    /// <summary>
    /// As the original's loader: without <c>cod</c> every option keeps its default; with it <c>cod gdm ps rs bs ep</c> are read as they are (a
    /// missing one is 0 / false), <c>ht nnm bl</c> only when present. Values are not clamped.
    /// </summary>
    public static NewGameSettings Read(SaveCamera camera)
    {
        var settings = new NewGameSettings();
        var (floats, bools) = (camera.Record.Floats, camera.Record.Bools);
        if (!floats.ContainsKey("cod")) return settings;

        for (int i = 0; i < Options.Length; i++)
        {
            settings._values[i] = Options[i].Key is "ht" or "nnm"
                ? floats.GetValueOrDefault(Options[i].Key, 1)
                : floats.GetValueOrDefault(Options[i].Key);
        }

        settings._ticks[0] = bools.GetValueOrDefault("bl", true);
        settings._ticks[1] = bools.GetValueOrDefault("ep");
        return settings;
    }
}
