using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Dialogue;

/// <summary>
/// BFrizz's Extra Extensions' own dialogue conditions (its <c>fcs.def</c> <c>DialogConditionEnum</c>, docs/game/dialogue.md "Extra Extensions
/// conditions"): the numbers the plugin claims above the game's.
/// </summary>
public static class ExtraConditions
{
    /// <summary>
    /// <c>DC_IS_SLEEPING</c>: the checked character is in a bed.
    /// </summary>
    public const int IsSleeping = 1000;
    /// <summary>
    /// <c>DC_HAS_SHORT_TERM_TAG</c>: the checked character's squad holds the short-term tag <c>tag</c> on the other.
    /// </summary>
    public const int HasShortTermTag = 1001;
    /// <summary>
    /// <c>DC_IS_ALLY_BECAUSE_OF_DISGUISE</c>: the checked character regards the other as an ally only through the other's working disguise.
    /// </summary>
    public const int IsAllyBecauseOfDisguise = 1002;
    /// <summary>
    /// <c>DC_STAT_LEVEL_UNMODIFIED</c>: the checked character's stat <c>tag</c>, raw.
    /// </summary>
    public const int StatLevelUnmodified = 1003;
    /// <summary>
    /// <c>DC_STAT_LEVEL_MODIFIED</c>: the checked character's stat <c>tag</c>, as its injuries, hunger and gear leave it.
    /// </summary>
    public const int StatLevelModified = 1004;
    /// <summary>
    /// <c>DC_WEAPON_LEVEL</c>: the checked character's weapon level, -1 with none.
    /// </summary>
    public const int WeaponLevel = 1005;
    /// <summary>
    /// <c>DC_ARMOUR_LEVEL</c>: the checked character's worn armour levels, -1 with none.
    /// </summary>
    public const int ArmourLevel = 1006;

    /// <summary>
    /// Whether <paramref name="code"/> is one of the plugin's conditions.
    /// </summary>
    public static bool Contains(int code) => code is >= IsSleeping and <= ArmourLevel;

    /// <summary>
    /// Whether the plugin tests <paramref name="code"/> in its <c>checkTags</c> hook (the ones that read the DIALOG_ACTION's <c>tag</c>); the others
    /// it tests in its <c>checkConditions</c> hook.
    /// </summary>
    public static bool ReadsTag(int code) => code is HasShortTermTag or StatLevelUnmodified or StatLevelModified;
}

/// <summary>
/// Which of BFrizz's Extra Extensions' item actions a <see cref="ItemAction"/> is.
/// </summary>
public enum ItemActionKind
{
    /// <summary>
    /// <c>take item</c>: from the target into the speaker's inventory.
    /// </summary>
    Take,
    /// <summary>
    /// <c>take item from squad</c>: as <see cref="Take"/>, repeated once per member of the target's squad near the speaker.
    /// </summary>
    TakeFromSquad,
    /// <summary>
    /// <c>destroy item</c>: from the target, destroyed.
    /// </summary>
    Destroy,
    /// <summary>
    /// <c>destroy item from squad</c>: as <see cref="Destroy"/>, repeated once per member of the target's squad near it.
    /// </summary>
    DestroyFromSquad,
}

/// <summary>
/// One BFrizz's Extra Extensions item action of a line (docs/game/dialogue.md "Extra Extensions item actions"): the kind, the referenced ITEM-like
/// record's string id and the reference's val0, the count.
/// </summary>
public readonly record struct ItemAction(ItemActionKind Kind, string Item, int Count);

/// <summary>
/// Reads BFrizz's Extra Extensions' item action lists of a DIALOGUE / DIALOGUE_LINE / WORD_SWAPS record.
/// </summary>
public static class ItemActionData
{
    /// <summary>
    /// The four lists in the order the plugin's <c>_doActions</c> hook runs them.
    /// </summary>
    static readonly (string List, ItemActionKind Kind)[] Lists =
    [
        ("take item", ItemActionKind.Take),
        ("take item from squad", ItemActionKind.TakeFromSquad),
        ("destroy item", ItemActionKind.Destroy),
        ("destroy item from squad", ItemActionKind.DestroyFromSquad),
    ];

    /// <summary>
    /// Every reference of the four lists, list by list in the hook's order, each list in data order.
    /// </summary>
    public static IReadOnlyList<ItemAction> Read(GameRecord record)
    {
        var actions = new List<ItemAction>();
        foreach (var (list, kind) in Lists)
        {
            foreach (var r in record.GetReferences(list)) actions.Add(new ItemAction(kind, r.TargetStringId, r.Values.Value0));
        }
        return actions;
    }
}
