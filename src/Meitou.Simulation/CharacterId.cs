namespace Meitou.Simulation;

/// <summary>
/// A character's identity in the simulation: its slot in the character table and the slot's generation, so an id kept after the
/// character is gone never names the next one in the same slot (docs/simulation.md, "World model").
/// </summary>
public readonly record struct CharacterId(int Slot, int Generation)
{
    /// <summary>
    /// No character.
    /// </summary>
    public static readonly CharacterId None = new(-1, 0);
    /// <summary>
    /// Whether the id names no character.
    /// </summary>
    public bool IsNone => Slot < 0;
    public override string ToString() => IsNone ? "none" : $"{Slot}.{Generation}";
}

/// <summary>
/// What an AI's ignore map names: a character (<see cref="Name"/> null) or a site, machine or loose item by its string id.
/// </summary>
public readonly record struct IgnoreKey(string? Name, CharacterId Id)
{
    /// <summary>
    /// The key of a character.
    /// </summary>
    public static IgnoreKey Of(CharacterId id) => new(null, id);
}