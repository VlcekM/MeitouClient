namespace ProvingGrounds.Core;

/// <summary>Pure grid layout for the town arena's challenge cards. Integers only. Signed int overflow is undefined in C++ and wraps
/// here; the golden vectors keep every product below 2^31 (golden/gen_layouts.cpp).
/// C++: TownCardLayout.h (header-only, namespace <c>TownCardLayout</c>).</summary>
public static class TownCardLayout
{
    /// <summary>C++: TownCardLayout.h:5-11 (a by-value aggregate <c>{columns, width, gap}</c> with const member functions). Grids made by
    /// <see cref="Fit"/> and <see cref="SingleRow"/> always have at least one column; a Grid with <see cref="Columns"/> == 0 makes
    /// <see cref="Column"/> and <see cref="Row"/> divide by zero (undefined in C++, a <see cref="DivideByZeroException"/> here; the
    /// C# default value is such a grid).</summary>
    public struct Grid
    {
        public int Columns, Width, Gap;

        // C++: TownCardLayout.h:7
        public readonly int Column(int index) { return index % Columns; }
        // C++: TownCardLayout.h:8
        public readonly int Row(int index) { return index / Columns; }
        // C++: TownCardLayout.h:9
        public readonly int Left(int index) { return Column(index) * (Width + Gap); }
        // C++: TownCardLayout.h:10
        public readonly int ContentWidth(int count) { return count > 0 ? count * Width + (count - 1) * Gap : 0; }
    }

    // C++: TownCardLayout.h:12-21
    public static Grid Fit(int available, int minimum, int spacing)
    {
        if (available < 1) available = 1;
        if (minimum < 1) minimum = 1;
        if (spacing < 0) spacing = 0;
        int columns = (available + spacing) / (minimum + spacing);
        if (columns < 1) columns = 1;
        if (columns > 5) columns = 5;
        Grid grid = new Grid { Columns = columns, Width = (available - (columns - 1) * spacing) / columns, Gap = spacing };
        return grid;
    }

    // C++: TownCardLayout.h:22-28
    public static Grid SingleRow(int available, int minimum, int spacing, int count)
    {
        if (spacing < 0) spacing = 0;
        if (count < 1) count = 1;
        Grid fitted = Fit(available, minimum, spacing);
        Grid grid = new Grid { Columns = count, Width = fitted.Width, Gap = spacing };
        return grid;
    }

    // C++: TownCardLayout.h:29-34
    public static int ClampViewOffset(int offset, int content, int view)
    {
        int maximum = content > view ? content - view : 0;
        int position = offset < 0 ? -offset : 0;
        if (position > maximum) position = maximum;
        return -position;
    }
}
