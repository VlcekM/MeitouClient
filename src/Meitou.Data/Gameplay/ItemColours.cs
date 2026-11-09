using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// The colour scheme (COLOR_DATA) an item instance is dyed with (docs/characters.md "Clothing colours"): a new item takes its record's <c>color</c>,
/// an item put on without one takes the wearer's scheme (its CHARACTER's <c>color</c>, else its faction's) and keeps it from then on.
/// </summary>
public static class ItemColours
{
    /// <summary>
    /// The COLOR_DATA of a record's first <c>color</c> reference (null: none).
    /// </summary>
    public static GameRecord? Of(GameDatabase db, GameRecord? record) =>
        record?.GetReferences("color") is [var first, ..] && db.Find(first.TargetStringId) is { Type: FcsRecordType.ColorData } colour
            ? colour : null;

    /// <summary>
    /// The scheme a wearer gives an uncoloured item: the CHARACTER's <c>color</c>, else the faction's.
    /// </summary>
    public static GameRecord? Scheme(GameDatabase db, GameRecord? character, GameRecord? faction) => Of(db, character) ?? Of(db, faction);

    /// <summary>
    /// The colour a worn item or fitted limb shows: its own when set, else the wearer's scheme; none for an ARMOUR or CONTAINER whose <c>dont
    /// colorise</c> is true (fcs.def defines it for those two, default true; every base record stores it). A LIMB_REPLACEMENT is never exempt.
    /// </summary>
    public static GameRecord? Shown(GameDatabase db, GameRecord item, GameRecord? own, GameRecord? character, GameRecord? faction)
    {
        if (item.Type is FcsRecordType.Armour or FcsRecordType.Container && item.GetBool("dont colorise", true))
            return null;

        return own ?? Scheme(db, character, faction);
    }
}
