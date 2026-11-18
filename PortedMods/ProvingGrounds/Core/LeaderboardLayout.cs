using System.Runtime.CompilerServices;

namespace ProvingGrounds.Core;

/// <summary>Pure geometry for the leaderboard window and the match-results ceremony: column widths, vertical bands, ceremonial portrait
/// sizes, title rails and the podium. Measured text enters from MyGUI as plain ints; nothing here touches a widget.
/// C++: LeaderboardLayout.h (header-only, namespace <c>LeaderboardLayout</c>).
/// <para>Integers only, no floats. Signed int overflow is undefined in C++ and wraps here (the project is unchecked); the golden vectors
/// keep every input within +-1,000,000 so it never happens (see golden/gen_layouts.cpp).</para></summary>
public static class LeaderboardLayout
{
    /// <summary>C++: LeaderboardLayout.h:6 <c>enum Column { Rank, Fighter, Rating, Record, Matches, ColumnCount };</c> Members and values as
    /// written (there is no shared prefix to drop); <see cref="ColumnCount"/> is the sentinel, 5.</summary>
    public enum Column { Rank = 0, Fighter = 1, Rating = 2, Record = 3, Matches = 4, ColumnCount = 5 }

    /// <summary>The C++ <c>int left[ColumnCount]</c> / <c>int width[ColumnCount]</c> inside <see cref="Columns"/>: a fixed array copied
    /// with the struct (an <c>int[]</c> field would alias between copies). The length is <c>(int)Column.ColumnCount</c> = 5
    /// (checked by <c>LeaderboardLayoutTests.Column_count_is_five</c>: an attribute argument cannot name the enum member's value without a constant of its own).</summary>
    [InlineArray(5)]
    public struct ColumnInts
    {
        int _element0;
    }

    /// <summary>C++: LeaderboardLayout.h:7-16 (returned by value). The default (all zeros, <c>stacked</c> false) is the C++
    /// constructor's. Do not call Equals/GetHashCode/Assert.Equal on it (.NET 10 throws NotSupportedException for a struct holding an
    /// InlineArray, and the C++ has no operator==): compare fields, or <c>((ReadOnlySpan&lt;int&gt;)x.Left).SequenceEqual(...)</c> for the
    /// arrays.</summary>
    public struct Columns
    {
        public bool Stacked;
        public ColumnInts Left;
        public ColumnInts Width;
    }

    /// <summary>The C++ takes <c>const int* minimum</c> and reads indices 0..ColumnCount-1; a shorter span than 5 is undefined behaviour
    /// there and an <see cref="IndexOutOfRangeException"/> here.</summary>
    // C++: LeaderboardLayout.h:18-34
    public static Columns FitColumns(int availableWidth, int gap, ReadOnlySpan<int> minimum)
    {
        const int count = (int)Column.ColumnCount;
        Columns result = default;
        int required = gap * (count - 1);
        for (int i = 0; i < count; ++i) required += minimum[i];
        result.Stacked = availableWidth < required;
        if (result.Stacked) return result;
        int x = 0;
        for (int i = 0; i < count; ++i)
        {
            result.Left[i] = x;
            result.Width[i] = minimum[i];
            if (i == (int)Column.Fighter) result.Width[i] += availableWidth - required;
            x += result.Width[i] + gap;
        }
        return result;
    }

    // C++: LeaderboardLayout.h:36-40
    public static int ClampOffset(int offset, int contentHeight, int viewHeight)
    {
        int maximum = contentHeight > viewHeight ? contentHeight - viewHeight : 0;
        return offset < 0 ? 0 : offset > maximum ? maximum : offset;
    }

    /// <summary>C++: LeaderboardLayout.h:42-48 (returned by value; all zeros by default).</summary>
    public struct Vertical
    {
        public int ContextHeight, HeadingTop, HeadingHeight, ScrollTop, ScrollHeight;
        public int CloseTop, CloseHeight;
    }

    // C++: LeaderboardLayout.h:50-67
    public static Vertical FitVertical(int height, int gap, int body, int contextHeight, int closeHeight)
    {
        Vertical result = default;
        if (height <= 0) return result;
        result.CloseHeight = closeHeight < height ? closeHeight : height;
        result.CloseTop = height - gap - result.CloseHeight;
        if (result.CloseTop < 0) result.CloseTop = 0;
        int usable = result.CloseTop > gap ? result.CloseTop - gap : 0;
        result.ContextHeight = contextHeight < usable / 4 ? contextHeight : usable / 4;
        result.HeadingTop = 2 * gap + result.ContextHeight;
        if (result.HeadingTop > usable) result.HeadingTop = usable;
        int headerRoom = usable - result.HeadingTop - gap;
        result.HeadingHeight = headerRoom > 0 ? (body < headerRoom ? body : headerRoom) : 0;
        result.ScrollTop = result.HeadingTop + result.HeadingHeight + gap;
        if (result.ScrollTop > usable) result.ScrollTop = usable;
        result.ScrollHeight = usable - result.ScrollTop;
        return result;
    }

    /// <summary>C++: LeaderboardLayout.h:69-74 (returned by value; all zeros by default).</summary>
    public struct CeremonialMetrics
    {
        public int ChampionPortrait, OtherPortrait, RowPortrait, RowGap, Edge;
    }

    // C++: LeaderboardLayout.h:76-85
    public static CeremonialMetrics FitCeremonialMetrics(int body, int gap)
    {
        CeremonialMetrics result = default;
        result.ChampionPortrait = body * 5 > 96 ? body * 5 : 96;
        result.OtherPortrait = body * 4 > 72 ? body * 4 : 72;
        result.RowPortrait = body * 2 > 40 ? body * 2 : 40;
        result.RowGap = gap / 2 > 2 ? gap / 2 : 2;
        result.Edge = gap / 4 > 1 ? gap / 4 : 1;
        return result;
    }

    /// <summary>C++: LeaderboardLayout.h:87-92 (returned by value; all zeros by default).</summary>
    public struct TitleRails
    {
        public int TitleLeft, TitleWidth, LeftWidth, RightLeft, RightWidth;
    }

    // C++: LeaderboardLayout.h:94-121
    public static TitleRails FitTitleRails(int availableWidth, int gap, int measuredTitleWidth)
    {
        TitleRails result = default;
        if (availableWidth <= 0)
            return result;
        int maximumTitle = availableWidth > 2 * gap
            ? availableWidth - 2 * gap : availableWidth;
        result.TitleWidth = measuredTitleWidth < maximumTitle
            ? measuredTitleWidth : maximumTitle;
        if (result.TitleWidth < 0)
            result.TitleWidth = 0;
        result.TitleLeft = (availableWidth - result.TitleWidth) / 2;
        int railGap = 2 * gap;
        result.LeftWidth = result.TitleLeft > railGap
            ? result.TitleLeft - railGap : 0;
        result.RightLeft = result.TitleLeft + result.TitleWidth + railGap;
        result.RightWidth = result.RightLeft < availableWidth
            ? availableWidth - result.RightLeft : 0;
        if (result.LeftWidth == 0 || result.RightWidth == 0)
        {
            result.LeftWidth = 0;
            result.RightWidth = 0;
        }
        return result;
    }

    /// <summary>C++: LeaderboardLayout.h:123-128 (all zeros by default).</summary>
    public struct PodiumSlot
    {
        public int SourceIndex, Rank, Left, Top, Width, Portrait;
    }

    /// <summary>The C++ <c>PodiumSlot slots[3]</c> inside <see cref="Podium"/>: a fixed array copied with the struct.</summary>
    [InlineArray(3)]
    public struct PodiumSlots
    {
        PodiumSlot _element0;
    }

    /// <summary>C++: LeaderboardLayout.h:130-136 (returned by value; all zeros by default; slots past <see cref="Count"/> stay zero).
    /// Do not call Equals/GetHashCode/Assert.Equal on it (.NET 10 throws NotSupportedException for a struct holding an InlineArray, and the
    /// C++ has no operator==): compare fields, or each <c>x.Slots[i]</c> (a PodiumSlot holds no arrays, so its own Equals works).</summary>
    public struct Podium
    {
        public bool Stacked;
        public int Count, ContentHeight;
        public PodiumSlots Slots;
    }

    // C++: LeaderboardLayout.h:138-197
    public static Podium FitPodium(int availableWidth, int gap, int standingsCount, int championPortrait, int otherPortrait)
    {
        Podium result = default;
        result.Count = standingsCount < 0 ? 0 :
            standingsCount > 3 ? 3 : standingsCount;
        if (result.Count == 0 || availableWidth <= 0)
            return result;

        int minimumCell = championPortrait + 8 * gap;
        int required = result.Count * minimumCell +
            (result.Count - 1) * gap;
        result.Stacked = availableWidth < required;

        if (result.Stacked)
        {
            int top = 0;
            for (int i = 0; i < result.Count; ++i)
            {
                ref PodiumSlot slot = ref result.Slots[i];
                slot.SourceIndex = i;
                slot.Rank = i + 1;
                slot.Left = 0;
                slot.Top = top;
                slot.Width = availableWidth;
                slot.Portrait = i == 0 ? championPortrait : otherPortrait;
                top += slot.Portrait + 8 * gap;
                if (i + 1 < result.Count) top += gap;
            }
            result.ContentHeight = top;
            return result;
        }

        int maximumCell = championPortrait + 20 * gap;
        int cell = (availableWidth - (result.Count - 1) * gap) /
            result.Count;
        if (cell > maximumCell) cell = maximumCell;
        int used = result.Count * cell + (result.Count - 1) * gap;
        int left = (availableWidth - used) / 2;
        ReadOnlySpan<int> sourceOrder = [1, 0, 2];
        ReadOnlySpan<int> rankOrder = [2, 1, 3];
        for (int i = 0; i < result.Count; ++i)
        {
            ref PodiumSlot slot = ref result.Slots[i];
            slot.SourceIndex = result.Count == 3 ? sourceOrder[i] : i;
            slot.Rank = result.Count == 3 ? rankOrder[i] : i + 1;
            slot.Left = left;
            slot.Top = 0;
            slot.Width = cell;
            slot.Portrait = slot.Rank == 1
                ? championPortrait : otherPortrait;
            left += cell + gap;
        }
        result.ContentHeight = championPortrait + 8 * gap;
        return result;
    }
}
