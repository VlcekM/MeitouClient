namespace Meitou.Simulation.Items;

/// <summary>
/// An equipment slot a code mod adds (docs/modding.md "Equipment slots"): the inventory section that takes the ARMOUR and ITEM records whose
/// <c>slot</c> is <see cref="FcsSlot"/>, its size in cells and its place in the inventory window.
/// </summary>
/// <param name="Section">
/// The section's name, as saves store it (<c>section</c>) and the window's slot is named; not a vanilla section.
/// </param>
/// <param name="FcsSlot">
/// The FCS <c>slot</c> number its items carry: 4, 10 (ATTACH_GLOVES), 11 (ATTACH_NECK), or 15 and up; the others are the game's. A
/// <see cref="Secondary"/> slot may take any but 0 (weapons), 7 (none) and 12 (bags), the game's own included.
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
public sealed record ModSlot(string Section, int FcsSlot, int Width, int Height, int X, int Y)
{
    /// <summary>
    /// How many items it holds, 1 to <see cref="Width"/> x <see cref="Height"/>. One item lies whatever its footprint, as a clothing slot's; more
    /// lie in its cells as a weapon section's do, each at its footprint, at most this many.
    /// </summary>
    public int MaxItems { get; init; } = 1;

    /// <summary>
    /// A second section for <see cref="FcsSlot"/>'s items: it takes them only by an explicit move (a drag, the loot window) and is never where the
    /// game puts such an item itself (equipping, picking up, an NPC's kit), which stays the slot's own section (the game's, or a non-secondary mod
    /// slot's).
    /// </summary>
    public bool Secondary { get; init; }

    /// <summary>
    /// Generated characters roll one more item for this slot from their CHARACTER's <c>clothing</c> list, after the game's own slots (slots in
    /// registration order), as the game rolls a clothing slot, and wear it here.
    /// </summary>
    public bool RollFromClothing { get; init; }
}

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
    /// The non-secondary slots' sections by FCS <c>slot</c>.
    /// </summary>
    static readonly Dictionary<int, string> ByFcsSlot = [];

    /// <summary>
    /// The FCS <c>slot</c> numbers a secondary slot may not take: weapons (two sections of their own), none, bags.
    /// </summary>
    static readonly HashSet<int> NeverSecondary = [0, 7, 12];

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
    /// The section of the mod slot taking FCS <c>slot</c> <paramref name="fcsSlot"/> as its own (not a secondary one); null when no mod claimed it.
    /// </summary>
    public static string? SectionOf(int fcsSlot) => ByFcsSlot.GetValueOrDefault(fcsSlot);

    /// <summary>
    /// Why <paramref name="slot"/> cannot be added: a bad name, size or item count, a vanilla section or <c>slot</c>, or one another slot took;
    /// null when it can.
    /// </summary>
    public static string? Refusal(ModSlot slot)
    {
        if (slot.Section.Length is 0 or > 64 || !slot.Section.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return $"Slot section '{slot.Section}' must be 1 to 64 letters, digits, '_', '.' or '-'.";
        if (VanillaSections.Contains(slot.Section)) return $"Section '{slot.Section}' is the game's.";
        if (BySection.ContainsKey(slot.Section)) return $"Section '{slot.Section}' is taken.";
        if (slot.Width is < 1 or > 20 || slot.Height is < 1 or > 20) return "A slot is 1 to 20 cells wide and high.";
        if (slot.MaxItems < 1 || slot.MaxItems > slot.Width * slot.Height)
            return "A slot holds 1 item up to one per cell.";
        return slot.Secondary ? SecondaryRefusal(slot) : OwnRefusal(slot);
    }

    /// <summary>
    /// Why a non-secondary slot's FCS <c>slot</c> cannot be its own: the game's, or another slot's.
    /// </summary>
    static string? OwnRefusal(ModSlot slot)
    {
        if (slot.FcsSlot is < 0 or (< 15 and not (4 or 10 or 11))) return $"FCS slot {slot.FcsSlot} is the game's.";
        if (ByFcsSlot.TryGetValue(slot.FcsSlot, out var other)) return $"FCS slot {slot.FcsSlot} is taken by section '{other}'.";
        return null;
    }

    /// <summary>
    /// Why a secondary slot cannot take its FCS <c>slot</c>: a negative one, weapons, none or bags.
    /// </summary>
    static string? SecondaryRefusal(ModSlot slot) =>
        slot.FcsSlot < 0 || NeverSecondary.Contains(slot.FcsSlot) ? $"A secondary slot cannot take FCS slot {slot.FcsSlot}." : null;

    /// <summary>
    /// Adds <paramref name="slot"/>; throws when <see cref="Refusal"/> refuses it.
    /// </summary>
    public static void Add(ModSlot slot)
    {
        if (Refusal(slot) is { } refusal) throw new ArgumentException(refusal, nameof(slot));

        Slots.Add(slot);
        BySection[slot.Section] = slot;
        if (!slot.Secondary) ByFcsSlot[slot.FcsSlot] = slot.Section;
        if (slot.RollFromClothing) Data.Characters.ClothingRolls.Add(slot.FcsSlot, slot.Section);
    }

    /// <summary>
    /// Whether <paramref name="section"/> is a mod slot holding more than one item, laid out in cells as a weapon section is.
    /// </summary>
    public static bool IsMultiItem(string section) => Of(section) is { MaxItems: > 1 };

    /// <summary>
    /// Removes every slot (tests).
    /// </summary>
    internal static void Clear()
    {
        Slots.Clear();
        BySection.Clear();
        ByFcsSlot.Clear();
        Data.Characters.ClothingRolls.Clear();
    }
}
