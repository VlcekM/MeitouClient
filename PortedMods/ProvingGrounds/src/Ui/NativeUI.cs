// C++: NativeUI.h/.cpp (task 4.4). The construction helpers and the Spacing / RowHeight arithmetic are pure and live in Core/Glue/NativeUI.cs
// (LayoutBuilder calls, tested in Tests/Glue/LayoutBuilderTests.cs). This file holds the one engine read NativeUI makes: the sample label's font
// height (MyGUI TextBox::getFontHeight / getTextSize, NativeUI.cpp:51-62), here measured through Meitou's text measurer.
using System.Numerics;
using Meitou.Data.Ui;

namespace ProvingGrounds.Ui;

/// <summary>
/// The standard text's measured font height for <see cref="Core.Glue.NativeUI.Spacing"/> / <see cref="Core.Glue.NativeUI.RowHeight"/> (task 4.4). The
/// C++ asks its hidden sample label (<c>g_measure</c>, a <c>Kenshi_TextboxStandardText</c>) at run time; a layout written as XML is built before
/// it is shown, so the window side measures at bind time with the measurer the faithful screens use (<c>UiBindings.TextHeight</c>, set by
/// ScreenRouter.BindFaithfulScreen to the painter's TextHeight, ScreenRouter.cs:76; UiPainter.Tooltips.cs:55) and rebuilds a layout when the
/// height changes (the shape key carries it). Window thread only.
/// </summary>
static class NativeUIMeasure
{
    static readonly UiControl Sample = new("PG_Measure", "TextBox", new UiRect(0, 0, 100, 24), "Ag", "", "Kenshi_TextboxStandardText", true,
        new Dictionary<string, string>(StringComparer.Ordinal), Vector4.Zero);

    /// <summary>The measured height unrounded (NaN without a measurer): logged once by the window framework.</summary>
    internal static float Raw(UiBindings b) => b.TextHeight is { } measure ? measure(Sample) : float.NaN;

    /// <summary>The one-line height of the standard text in pixels, rounded; <paramref name="fallback"/> when the bindings carry no measurer or it
    /// answers nothing (offline, or before the fonts are loaded).</summary>
    internal static int FontHeight(UiBindings b, int fallback)
    {
        if (b.TextHeight is not { } measure) return fallback;
        float h = measure(Sample);
        return float.IsFinite(h) && h >= 1 ? (int)MathF.Round(h) : fallback;
    }
}
