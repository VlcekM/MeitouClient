namespace Meitou.Simulation.Items;

/// <summary>
/// An equipment slot a code mod adds (docs/modding.md "Equipment slots"): the inventory section that takes the ARMOUR and ITEM records whose
/// <c>slot</c> is <see cref="FcsSlot"/>, its size in cells and its place in the inventory window.
/// </summary>
/// <param name="Section">
/// The section's name, as saves store it (<c>section</c>) and the window's slot is named; not a vanilla section.
/// </param>
/// <param name="FcsSlot">
/// The FCS <c>slot</c> number its items carry: 4, 10 (ATTACH_GLOVES), 11 (ATTACH_NECK), or 15 and up; the others are the game's.
/// </param>
/// <param name="Width">
/// The slot's width in inventory cells.
/// </param>
/// <param name="Height">
/// The slot's height in inventory cells.
/// </param>
/// <param name="X">
/// The slot's left edge in the inventory window, in layout pixels at UI scale 1 from the window's left edge.
/// </param>
/// <param name="Y">
/// The slot's top edge in the inventory window, in layout pixels at UI scale 1 from the window's top edge.
/// </param>
public sealed record ModSlot(string Section, int FcsSlot, int Width, int Height, int X, int Y);

/// <summary>
/// The equipment slots code mods added, by section and by FCS <c>slot</c>. Filled at startup before the first world is built and never changed
/// while one runs; empty without mods, so the vanilla sections stay the only ones.
/// </summary>
public static class ModSlots
{
    /// <summary>
    /// The slots in registration order.
    /// </summary>
    static readonly List<ModSlot> Slots = [];
    /// <summary>
    /// The slots by section.
    /// </summary>
    static readonly Dictionary<string, ModSlot> BySection = new(StringComparer.Ordinal);
    /// <summary>
    /// The slots' sections by FCS <c>slot</c>.
    /// </summary>
    static readonly Dictionary<int, string> ByFcsSlot = [];

    /// <summary>
    /// The sections of the base game, which no mod slot may take.
    /// </summary>
    static readonly HashSet<string> VanillaSections = new(StringComparer.Ordinal)
    {
        "main", "backpack_content", "backpack_attach", "back", "hip", "shirt", "head", "legs", "boots", "armour", "belt",
        "left_arm_replace", "right_arm_replace", "left_leg_replace", "right_leg_replace", "in1", "in2", "out",
    };

    /// <summary>
    /// The slots in registration order.
    /// </summary>
    public static IReadOnlyList<ModSlot> All => Slots;

    /// <summary>
    /// The mod slot of <paramref name="section"/>; null for any other section.
    /// </summary>
    public static ModSlot? Of(string section) => BySection.GetValueOrDefault(section);

    /// <summary>
    /// The section of the mod slot taking FCS <c>slot</c> <paramref name="fcsSlot"/>; null when no mod claimed it.
    /// </summary>
    public static string? SectionOf(int fcsSlot) => ByFcsSlot.GetValueOrDefault(fcsSlot);

    /// <summary>
    /// Why <paramref name="slot"/> cannot be added: a bad name or size, a vanilla section or <c>slot</c>, or one another slot took; null when it can.
    /// </summary>
    public static string? Refusal(ModSlot slot)
    {
        if (slot.Section.Length is 0 or > 64 || !slot.Section.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return $"Slot section '{slot.Section}' must be 1 to 64 letters, digits, '_', '.' or '-'.";
        if (VanillaSections.Contains(slot.Section)) return $"Section '{slot.Section}' is the game's.";
        if (BySection.ContainsKey(slot.Section)) return $"Section '{slot.Section}' is taken.";
        if (slot.FcsSlot is < 0 or (< 15 and not (4 or 10 or 11))) return $"FCS slot {slot.FcsSlot} is the game's.";
        if (ByFcsSlot.TryGetValue(slot.FcsSlot, out var other)) return $"FCS slot {slot.FcsSlot} is taken by section '{other}'.";
        if (slot.Width is < 1 or > 20 || slot.Height is < 1 or > 20) return "A slot is 1 to 20 cells wide and high.";
        return null;
    }

    /// <summary>
    /// Adds <paramref name="slot"/>; throws when <see cref="Refusal"/> refuses it.
    /// </summary>
    public static void Add(ModSlot slot)
    {
        if (Refusal(slot) is { } refusal) throw new ArgumentException(refusal, nameof(slot));

        Slots.Add(slot);
        BySection[slot.Section] = slot;
        ByFcsSlot[slot.FcsSlot] = slot.Section;
    }

    /// <summary>
    /// Removes every slot (tests).
    /// </summary>
    internal static void Clear()
    {
        Slots.Clear();
        BySection.Clear();
        ByFcsSlot.Clear();
    }
}
