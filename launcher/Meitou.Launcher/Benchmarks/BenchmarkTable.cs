using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Meitou.Launcher.Benchmarks;

/// <summary>
/// Builds the single framed results table for the Benchmark page: either one session's scenario groups or an overview of every tier of a
/// scenario (each row clickable to open that tier's groups).
/// </summary>
static class BenchmarkTable
{
    /// <summary>
    /// Widths (pixels) of a metric column, the tier column and the group / status columns' minimums.
    /// </summary>
    const double MetricWidth = 92, TierWidth = 110, MinLabelWidth = 140, MinStatusWidth = 140;

    /// <summary>
    /// Subtle text brushes matching <c>App.axaml</c> tokens for highlighted and quiet cells.
    /// </summary>
    static readonly IBrush PaperLight = Brush.Parse("#d9ceb8"), MainBrush = Brush.Parse("#afa68b"),
        QuietBrush = Brush.Parse("#8a8270"), BrassLight = Brush.Parse("#c4a062"), DividerBrush = Brush.Parse("#14afa68b");

    /// <summary>
    /// A framed table for <paramref name="session"/>: one row per scenario group (with Whole run highlighted last) and columns per run.
    /// </summary>
    public static Border ForSession(BenchmarkSession session)
    {
        int runCount = Math.Max(1, session.Runs.Count);
        var stack = new StackPanel();
        stack.Children.Add(Header(Tr("Group"), runCount, overview: false));

        var groups = OrderedGroups(session.Runs[^1].Groups);
        for (int i = 0; i < groups.Count; i++)
        {
            string group = groups[i];
            bool total = group == "all";
            var metrics = session.Runs.Select(run => run.Groups.FirstOrDefault(g => g.Name == group).Metrics).ToList();
            stack.Children.Add(GroupRow(BenchmarkTiers.GroupLabel(group), metrics, runCount, i, i == groups.Count - 1, total));
        }
        return new Border { Classes = { "table" }, Child = stack };
    }

    /// <summary>
    /// A framed overview table for <paramref name="sessions"/>: one row per tier with its Whole run metrics and status; clicking a row calls
    /// <paramref name="selectTier"/>.
    /// </summary>
    public static Border ForOverview(IReadOnlyList<BenchmarkSession> sessions, Func<BenchmarkSession, (string Text, bool Warn)> statusOf,
        Action<string> selectTier)
    {
        int runCount = Math.Max(1, sessions.Max(session => session.Runs.Count));
        var stack = new StackPanel();
        stack.Children.Add(Header(Tr("Tier"), runCount, overview: true));

        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var metrics = session.Runs.Select(run => run.Groups.FirstOrDefault(g => g.Name == "all").Metrics).ToList();
            var status = statusOf(session);
            stack.Children.Add(OverviewRow(session, metrics, runCount, i, i == sessions.Count - 1, status, selectTier));
        }
        return new Border { Classes = { "table" }, Child = stack };
    }

    /// <summary>
    /// <paramref name="text"/> in the launcher's language, its holes filled.
    /// </summary>
    static string Tr(string text, params object?[] args) => LauncherText.Translate(text, args);

    /// <summary>
    /// The column layout shared by the header and every row of a table.
    /// </summary>
    static ColumnDefinitions Columns(int runCount, bool overview)
    {
        var cols = new ColumnDefinitions
        {
            overview ? new ColumnDefinition(TierWidth, GridUnitType.Pixel) : new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinLabelWidth },
        };
        for (int r = 0; r < runCount; r++)
        {
            cols.Add(new ColumnDefinition(1, GridUnitType.Pixel));
            cols.Add(new ColumnDefinition(MetricWidth, GridUnitType.Pixel));
            cols.Add(new ColumnDefinition(MetricWidth, GridUnitType.Pixel));
            cols.Add(new ColumnDefinition(MetricWidth, GridUnitType.Pixel));
        }
        if (overview)
        {
            cols.Add(new ColumnDefinition(1, GridUnitType.Pixel));
            cols.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinStatusWidth });
        }
        return cols;
    }

    /// <summary>
    /// The two-tier header: Run 1 / Run 2 super-headers over the column titles.
    /// </summary>
    static Border Header(string firstColumn, int runCount, bool overview)
    {
        var grid = new Grid
        {
            ColumnDefinitions = Columns(runCount, overview),
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)],
        };

        var lead = new TextBlock
        {
            Classes = { "desc" }, Text = firstColumn, Margin = new(14, 2, 10, 8), VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetRow(lead, 1);
        grid.Children.Add(lead);

        for (int r = 0; r < runCount; r++)
        {
            int dividerCol = 1 + r * 4;
            AddDivider(grid, dividerCol, rowSpan: 2);

            string superTitle = overview ? Tr("Run {1} · Whole run", r + 1) : Tr("Run {1}", r + 1);
            var super = new TextBlock
            {
                Classes = { "caps" }, Text = superTitle.ToUpper(CultureInfo.CurrentCulture),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 7, 12, 1),
            };
            Grid.SetColumn(super, dividerCol + 1);
            Grid.SetColumnSpan(super, 3);
            grid.Children.Add(super);

            AddHeaderCell(grid, dividerCol + 1, Tr("FPS"));
            AddHeaderCell(grid, dividerCol + 2, Tr("99% ms"));
            AddHeaderCell(grid, dividerCol + 3, Tr("Over 33 ms"));
        }

        if (overview)
        {
            int statusDivider = 1 + runCount * 4;
            AddDivider(grid, statusDivider, rowSpan: 2);
            AddHeaderCell(grid, statusDivider + 1, Tr("Status"));
        }
        return new Border { Classes = { "table-head" }, Child = grid };
    }

    /// <summary>
    /// One metric column header on row 1 of the table header.
    /// </summary>
    static void AddHeaderCell(Grid grid, int column, string text)
    {
        var cell = new TextBlock
        {
            Classes = { "desc" }, Text = text, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 1, 12, 8),
        };
        Grid.SetRow(cell, 1);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// <summary>
    /// One scenario-group row of a single session's table.
    /// </summary>
    static Border GroupRow(string label, IReadOnlyList<GroupMetrics?> runs, int runCount, int index, bool last, bool total)
    {
        var grid = new Grid { ColumnDefinitions = Columns(runCount, overview: false) };
        var brush = total ? PaperLight : MainBrush;
        var weight = total ? FontWeight.SemiBold : FontWeight.Medium;
        grid.Children.Add(new TextBlock
        {
            Text = label, FontSize = 13, FontWeight = weight, Foreground = brush,
            Margin = new(14, 8, 10, 8), VerticalAlignment = VerticalAlignment.Center,
        });
        AddMetricCells(grid, runs, runCount, brush, weight);

        var row = new Border { Classes = { "table-row" }, Child = grid };
        if (total) row.Classes.Add("total");
        else if (index % 2 == 1) row.Classes.Add("alt");
        if (last && !total) row.Classes.Add("last");
        return row;
    }

    /// <summary>
    /// One tier row of the All-tiers overview table, clickable to open that tier's groups.
    /// </summary>
    static Border OverviewRow(BenchmarkSession session, IReadOnlyList<GroupMetrics?> runs, int runCount, int index, bool last,
        (string Text, bool Warn) status, Action<string> selectTier)
    {
        var grid = new Grid { ColumnDefinitions = Columns(runCount, overview: true) };
        string tierName = BenchmarkTiers.Label(session.Tier);
        grid.Children.Add(new TextBlock
        {
            Text = tierName, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = PaperLight,
            Margin = new(14, 9, 10, 9), VerticalAlignment = VerticalAlignment.Center,
        });
        AddMetricCells(grid, runs, runCount, MainBrush, FontWeight.Medium);

        int statusDivider = 1 + runCount * 4;
        AddDivider(grid, statusDivider, rowSpan: 1);
        var statusText = new TextBlock
        {
            Text = status.Text, FontSize = 12, FontWeight = FontWeight.Medium,
            Foreground = status.Warn ? BrassLight : QuietBrush, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new(10, 9, 14, 9),
        };
        Grid.SetColumn(statusText, statusDivider + 1);
        grid.Children.Add(statusText);

        var row = new Border { Classes = { "table-row", "clickable" }, Child = grid };
        if (index % 2 == 1) row.Classes.Add("alt");
        if (last) row.Classes.Add("last");
        ToolTip.SetTip(row, Tr("Click to view groups for {1}", tierName));
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) selectTier(session.Tier);
        };
        return row;
    }

    /// <summary>
    /// Fills each run's three metric cells and its leading vertical divider in <paramref name="grid"/>.
    /// </summary>
    static void AddMetricCells(Grid grid, IReadOnlyList<GroupMetrics?> runs, int runCount, IBrush foreground, FontWeight weight)
    {
        for (int r = 0; r < runCount; r++)
        {
            int dividerCol = 1 + r * 4;
            AddDivider(grid, dividerCol, rowSpan: 1);
            var metrics = r < runs.Count ? runs[r] : null;
            string[] values = metrics is null
                ? ["-", "-", "-"]
                : [Number(metrics.AvgFps, "0.0"), Number(metrics.P99Ms, "0.0"), Percent(metrics.Over33Pct)];
            for (int c = 0; c < values.Length; c++)
            {
                var cell = new TextBlock
                {
                    Text = values[c], FontSize = 13, FontWeight = weight,
                    Foreground = metrics is null ? QuietBrush : foreground,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new(8, 8, 12, 8),
                };
                Grid.SetColumn(cell, dividerCol + 1 + c);
                grid.Children.Add(cell);
            }
        }
    }

    /// <summary>
    /// A subtle vertical hairline separating run columns.
    /// </summary>
    static void AddDivider(Grid grid, int column, int rowSpan)
    {
        var line = new Border { Background = DividerBrush, Width = 1 };
        Grid.SetColumn(line, column);
        if (rowSpan > 1) Grid.SetRowSpan(line, rowSpan);
        grid.Children.Add(line);
    }

    /// <summary>
    /// The groups in their <c>result.json</c> order, with <c>all</c> (Whole run) always last as the summary row.
    /// </summary>
    static List<string> OrderedGroups(IReadOnlyList<(string Name, GroupMetrics Metrics)> groups)
    {
        var ordered = groups.Select(g => g.Name).Where(name => name != "all").ToList();
        if (groups.Any(g => g.Name == "all")) ordered.Add("all");
        return ordered;
    }

    /// <summary>
    /// <paramref name="value"/> in the current culture, a dash when missing.
    /// </summary>
    static string Number(double value, string format) => double.IsNaN(value) ? "-" : value.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>
    /// A percentage formatted in the current culture, a dash when missing.
    /// </summary>
    static string Percent(double value) => double.IsNaN(value) ? "-" : value.ToString("0.0", CultureInfo.CurrentCulture) + " %";
}
