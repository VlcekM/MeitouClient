// Port of the grade-recognition rules of upstream/ProvingGrounds/plugin/src/RewardDelivery.cpp (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core.Glue;

/// <summary>The rules RewardDelivery.cpp applies to the items a fighter holds, lifted out of its engine code (task 5.2a): which record a piece
/// is delivered as, which held copy is "the previous grade", and the older item levels it still recognises. The engine half
/// (src/Engine/Items.cs) lists a fighter's copies of a record as plain levels in the C++ search order; these functions pick an index.
/// <para>Search order. The C++ looks in four places one after the other and takes the first copy that passes (FindExactGrade
/// :230-273): <c>Inventory::getItem</c>, <c>getAllItems</c>, the equipped armour or weapons, then every inventory section. Meitou keeps one
/// item list per character (<c>Inventory.Items</c> plus the worn backpack's contents, Inventory.cs:36, :237-246), so the adapter gives the
/// copies in one list (unworn ones first, then worn ones, src/Engine/Items.cs <c>Copies</c>) and "the first that passes" is taken over it.</para></summary>
public static class RewardGrades
{
    /// <summary>C++: RewardDelivery.cpp:169-196 (<c>LevelsMatch</c>). Equal levels match; so do the older levels earlier versions of the mod
    /// wrote: the weapon colour bands 70/75/80 and 50/70 (an item made before the Grade2/3/4 mapping, or before Arena/Pit Forge used its FCS
    /// model values 55/75) and the pre-fix manufacturer indices 3/4/5 for High / Specialist / Masterwork (60 / 80 / 95), both ways.</summary>
    public static bool LevelsMatch(int have, int want)
    {
        if (have == want)
            return true;
        // Weapon colour-band migration: rewards created before the
        // Grade2/3/4 mapping used 70/75/80. Accept those older Warlord and
        // Frostcursed items as the previous grade during an upgrade.
        if ((have == 70 && want == 50) ||
            (have == 75 && want == 70) ||
            // Arena/Pit Forge now uses its FCS model values: 55/75.
            // Accept the earlier uniform 50/70 progression so an
            // existing reward can still be upgraded or replaced.
            (have == 50 && want == 55) ||
            (have == 50 && want == 75) ||
            (have == 70 && want == 55) ||
            (have == 70 && want == 75))
        {
            return true;
        }
        // Accept pre-fix manufacturer indices (3/4/5) as High/Spec/MW.
        if ((have == 3 && want == 60) || (have == 60 && want == 3))
            return true;
        if ((have == 4 && want == 80) || (have == 80 && want == 4))
            return true;
        if ((have == 5 && want == 95) || (have == 95 && want == 5))
            return true;
        return false;
    }

    /// <summary>C++: RewardDelivery.cpp:230-273 (<c>FindExactGrade</c>): the first copy whose level <see cref="LevelsMatch"/> the wanted one;
    /// -1 when none.</summary>
    public static int FindExact(IReadOnlyList<int> levels, int want)
    {
        for (int i = 0; i < levels.Count; ++i)
            if (LevelsMatch(levels[i], want))
                return i;
        return -1;
    }

    /// <summary>C++: RewardDelivery.cpp:307-357 (<c>FindPreviousGrade</c>): "Upgrades require the exact previous grade when present; fallback
    /// to any copy only for exchange once an upgrade offer is confirmed": the exact copy, else the first copy of any level; -1 when none.</summary>
    public static int FindPrevious(IReadOnlyList<int> levels, int want)
    {
        int exact = FindExact(levels, want);
        if (exact >= 0)
            return exact;
        return levels.Count > 0 ? 0 : -1;
    }

    /// <summary>C++: RewardDelivery.cpp:711-723 (<c>ResolveRewardItemData</c>): the record a piece is delivered as. The cloak is record 89
    /// (belt) always: the C++ picks 72 (neck) only when the "Extra Inventory Sections" plugin is active (:65-85, :715-719), and Meitou has no
    /// neck section (spike S13, VERIFIED by run 2026-11-11: record 72 would go to "main" and never be worn). Every other piece: its first
    /// tier's record (<see cref="ArenaRewards.FirstTier"/>; weapons start at High Grade).</summary>
    public static string ItemRecord(ArenaRewards.Piece piece)
    {
        if (piece == ArenaRewards.Piece.Cloak)
            return PgRecords.WarlordCloak;
        return ArenaRewards.Get(piece, ArenaRewards.FirstTier(piece)).StringId;
    }

    /// <summary>C++: RewardDelivery.cpp:87-93 (<c>ResolveAlternateCloakData</c>) as its callers use it (:359-394): for the cloak, the other
    /// cloak record (89 for 72, 72 for anything else), which the finders search when the primary record has no copy; null for any other piece.</summary>
    public static string? AlternateRecord(ArenaRewards.Piece piece, string primary)
    {
        if (piece != ArenaRewards.Piece.Cloak)
            return null;
        return primary == PgRecords.WarlordCloakNeck ? PgRecords.WarlordCloak : PgRecords.WarlordCloakNeck;
    }

    /// <summary>C++: RewardDelivery.cpp:359-370 (<c>FindExactRewardGrade</c>): the exact grade among the primary record's copies, else (the
    /// cloak only) among its alternate's. <c>Alternate</c> says which list the index is in; index -1 when none.</summary>
    public static (bool Alternate, int Index) FindExactReward(ArenaRewards.Piece piece, IReadOnlyList<int> primary,
        IReadOnlyList<int> alternate, int want)
    {
        int i = FindExact(primary, want);
        if (i >= 0 || piece != ArenaRewards.Piece.Cloak)
            return (false, i);
        int j = FindExact(alternate, want);
        return j >= 0 ? (true, j) : (false, -1);
    }

    /// <summary>C++: RewardDelivery.cpp:372-382 (<c>HasAnyRewardGrade</c>): any copy of the primary record, or (the cloak only) of its
    /// alternate.</summary>
    public static bool HasAnyReward(ArenaRewards.Piece piece, int primaryCount, int alternateCount) =>
        primaryCount > 0 || (piece == ArenaRewards.Piece.Cloak && alternateCount > 0);

    /// <summary>C++: RewardDelivery.cpp:383-394 (<c>FindPreviousRewardGrade</c>): <see cref="FindPrevious"/> over the primary record's
    /// copies (an exact grade, else any copy), else (the cloak only) over its alternate's. Index -1 when none.</summary>
    public static (bool Alternate, int Index) FindPreviousReward(ArenaRewards.Piece piece, IReadOnlyList<int> primary,
        IReadOnlyList<int> alternate, int want)
    {
        int i = FindPrevious(primary, want);
        if (i >= 0 || piece != ArenaRewards.Piece.Cloak)
            return (false, i);
        int j = FindPrevious(alternate, want);
        return j >= 0 ? (true, j) : (false, -1);
    }
}
