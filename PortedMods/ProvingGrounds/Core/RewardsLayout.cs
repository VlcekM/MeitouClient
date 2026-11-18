namespace ProvingGrounds.Core;

/// <summary>Pure layout rules for the rewards window (stacking, column counts, card heights, rarity index). Integers only. Signed int
/// overflow is undefined in C++ and wraps here; the golden vectors keep every product below 2^31 (golden/gen_layouts.cpp).
/// C++: RewardsLayout.h (header-only, namespace <c>RewardsLayout</c>).</summary>
public static class RewardsLayout
{
    /// <summary>The C++ helper, kept for fidelity; the same as <c>Math.Min</c> for ints.</summary>
    // C++: RewardsLayout.h:5
    public static int Min(int a, int b) { return a < b ? a : b; }
    /// <summary>The C++ helper, kept for fidelity; the same as <c>Math.Max</c> for ints.</summary>
    // C++: RewardsLayout.h:6
    public static int Max(int a, int b) { return a > b ? a : b; }

    // C++: RewardsLayout.h:8-11
    public static bool ShouldStack(int contentWidth, int bodyHeight)
    {
        return contentWidth < Max(720, bodyHeight * 50);
    }

    // C++: RewardsLayout.h:13-17
    public static int FighterWidth(int contentWidth, int bodyHeight)
    {
        int minimum = Max(160, bodyHeight * 11);
        return Min(320, Max(minimum, contentWidth * 21 / 100));
    }

    // C++: RewardsLayout.h:19-30
    public static int GridColumns(int width, int minimumCardWidth, int gap, int maximumColumns)
    {
        if (minimumCardWidth <= 0 || maximumColumns <= 0)
            return 1;
        int columns = (width + Max(0, gap)) /
            (minimumCardWidth + Max(0, gap));
        return Min(maximumColumns, Max(1, columns));
    }

    // C++: RewardsLayout.h:32-43
    public static int FillRowHeight(int viewHeight, int visibleRows, int gap, int minimumHeight)
    {
        if (visibleRows <= 0)
            return Max(1, minimumHeight);
        int safeGap = Max(0, gap);
        int usable = viewHeight - (visibleRows + 1) * safeGap;
        return Max(Max(1, minimumHeight), usable / visibleRows);
    }

    // C++: RewardsLayout.h:45-50
    public static int RarityIndex(int offeredTier, bool complete)
    {
        if (complete)
            return 3;
        return Min(3, Max(0, offeredTier));
    }

    // C++: RewardsLayout.h:52-55
    public static int CompactRowHeight(int roomyHeight, int minimumHeight)
    {
        return Max(Max(1, minimumHeight), roomyHeight / 2);
    }

    // C++: RewardsLayout.h:57-60
    public static int FightSupplyColumns(int visibleSupplyCount)
    {
        return visibleSupplyCount > 1 ? 2 : 1;
    }
}
