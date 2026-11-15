namespace Meitou.Simulation.Bodies;

/// <summary>
/// What a code mod's medical-panel bar shows for one character (docs/modding.md "Medical meters"): the bar's layers as fractions of its width from
/// the left, the number in the value column, the arrows at the bar's right end and the tooltip. Display only.
/// </summary>
/// <param name="Green">
/// The front layer (the colour of a part's health), 0 to 1.
/// </param>
/// <param name="Value">
/// The text in the value column.
/// </param>
public readonly record struct MeterReading(float Green, string Value)
{
    /// <summary>
    /// The layer behind the green (a bandage's colour), 0 to 1; 0 hides it.
    /// </summary>
    public float Yellow { get; init; }

    /// <summary>
    /// The layer behind the yellow (lost health's colour), 0 to 1; 0 hides it.
    /// </summary>
    public float Red { get; init; }

    /// <summary>
    /// The text right-aligned at the bar's end in the alert colour (the game's <c>&lt;</c> for bleeding, <c>&gt;</c> for digesting).
    /// </summary>
    public string Arrows { get; init; } = "";

    /// <summary>
    /// The bar's tooltip; null or empty: none.
    /// </summary>
    public string? Tooltip { get; init; }
}

/// <summary>
/// A bar a code mod added to the medical panel (<see cref="ModMeters"/>): its label, and the readings its systems set on characters in the tick.
/// </summary>
public sealed class ModMeter
{
    /// <summary>
    /// Makes a meter labelled <paramref name="label"/>, shown once <see cref="ModMeters.Add"/> has taken it.
    /// </summary>
    public ModMeter(string label) => Label = label;

    /// <summary>
    /// The name on the bar (translated through the game's catalog like the vanilla rows').
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// The meter's place in <see cref="ModMeters.All"/> and in <see cref="CharacterCold.Meters"/>; -1 until added.
    /// </summary>
    public int Index { get; internal set; } = -1;

    /// <summary>
    /// Shows <paramref name="reading"/> on <paramref name="cold"/>'s bar; set from the mod's system in the tick (the character's own partition in a
    /// parallel phase).
    /// </summary>
    public void Set(CharacterCold cold, MeterReading reading)
    {
        if (Index < 0) return;

        var meters = cold.Meters;
        if (meters is null || meters.Length <= Index)
        {
            Array.Resize(ref meters, ModMeters.All.Count);
            cold.Meters = meters;
        }

        meters[Index] = reading;
    }

    /// <summary>
    /// Hides the bar for <paramref name="cold"/>.
    /// </summary>
    public void Clear(CharacterCold cold)
    {
        if (cold.Meters is { } meters && (uint)Index < (uint)meters.Length) meters[Index] = null;
    }

    /// <summary>
    /// What the bar shows for <paramref name="cold"/>; null: no bar.
    /// </summary>
    public MeterReading? Of(CharacterCold cold) => cold.Meters is { } meters && (uint)Index < (uint)meters.Length ? meters[Index] : null;
}

/// <summary>
/// The medical-panel bars code mods added, in registration order, shown after Hunger. Filled at startup before the first world is built and never
/// changed while one runs; empty without mods, so the panel stays the game's.
/// </summary>
public static class ModMeters
{
    /// <summary>
    /// The most bars all mods together may add: past the game's nine rows the rows share the panel's height, and four more keep them readable.
    /// </summary>
    public const int Max = 4;

    /// <summary>
    /// The meters in registration order.
    /// </summary>
    static readonly List<ModMeter> Meters = [];

    /// <summary>
    /// The meters in registration order.
    /// </summary>
    public static IReadOnlyList<ModMeter> All => Meters;

    /// <summary>
    /// Why a meter labelled <paramref name="label"/> cannot be added after <paramref name="pending"/> more of the registering mod: a blank or long
    /// label, one another meter has (case-insensitive), or no room left; null when it can.
    /// </summary>
    public static string? Refusal(string label, IEnumerable<string> pending)
    {
        var mine = pending.ToList();
        if (string.IsNullOrWhiteSpace(label) || label.Length > 32) return "A medical meter's label is 1 to 32 characters.";
        if (Meters.Any(m => Same(m.Label, label)) || mine.Any(m => Same(m, label))) return $"Medical meter '{label}' is taken.";
        if (Meters.Count + mine.Count >= Max) return $"The medical panel takes at most {Max} mod meters.";
        return null;
    }

    /// <summary>
    /// Adds <paramref name="meter"/> after the others; throws when <see cref="Refusal"/> refuses it or it was added already.
    /// </summary>
    public static void Add(ModMeter meter)
    {
        if (meter.Index >= 0) throw new ArgumentException($"Medical meter '{meter.Label}' was added already.", nameof(meter));
        if (Refusal(meter.Label, []) is { } refusal) throw new ArgumentException(refusal, nameof(meter));

        meter.Index = Meters.Count;
        Meters.Add(meter);
    }

    /// <summary>
    /// Removes every meter (tests).
    /// </summary>
    internal static void Clear()
    {
        foreach (var meter in Meters) meter.Index = -1;
        Meters.Clear();
    }

    /// <summary>
    /// Whether two labels are the same to a player.
    /// </summary>
    static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
