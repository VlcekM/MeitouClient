namespace Meitou.Simulation;

/// <summary>
/// A character's identity in the simulation: its slot in the character table and the slot's generation, so an id kept after the
/// character is gone never names the next one in the same slot (docs/simulation.md, "World model").
/// </summary>
public readonly record struct CharacterId(int Slot, int Generation)
{
    public static readonly CharacterId None = new(-1, 0);
    public bool IsNone => Slot < 0;
    public override string ToString() => IsNone ? "none" : $"{Slot}.{Generation}";
}
