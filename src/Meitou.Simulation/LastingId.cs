namespace Meitou.Simulation;

/// <summary>
/// A character's lasting id (docs/modding.md "Character ids"): given once, when a code mod first asks (<see cref="LastingIds.GetOrGive"/>), and
/// kept for the character's life across saves and loads, squad moves, recruiting, renames and its squad leaving the world and coming back, unlike
/// <see cref="CharacterId"/>, which is new for every world. Unique within one game's line of saves. Meitou-only, not in the original.
/// </summary>
public readonly record struct LastingId(ulong Value)
{
    /// <summary>
    /// No id (a character no mod asked about).
    /// </summary>
    public static readonly LastingId None = default;
    /// <summary>
    /// Whether this is no id.
    /// </summary>
    public bool IsNone => Value == 0;
    /// <summary>
    /// The id as 16 hex digits, or "none".
    /// </summary>
    public override string ToString() => IsNone ? "none" : Value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}
