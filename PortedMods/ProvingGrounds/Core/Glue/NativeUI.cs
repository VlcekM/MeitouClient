namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The original's construction helpers (NativeUI.h/.cpp, task 4.4) as <see cref="LayoutBuilder"/> calls: the same skins, the same
/// <c>Align::Default</c> (no align attribute) and the same text settings. Each helper OPENS its widget and leaves it open, as the C++ returns the
/// widget for the caller to add children to (ArenaUI.cpp:2537-2545 puts an EditBox inside a button); the caller closes it.
/// <para>Spacing and row height: the C++ measures the sample label's font at run time (<c>getFontHeight</c>, <c>getTextSize</c>). A layout
/// written as XML cannot measure before it is shown, so <see cref="Spacing"/> / <see cref="RowHeight"/> take the measured heights as numbers:
/// the window side measures them through Meitou's text measurer when it can (src/Ui/NativeUI.cs) and uses <see cref="DefaultFontHeight"/>
/// otherwise (docs/divergences.md "Window framework").</para>
/// </summary>
public static class NativeUI
{
    /// <summary>MyGUI's <c>getFontHeight()</c> of <c>Kenshi_TextboxStandardText</c> (font <c>Kenshi_StandardFont_Medium</c> = Exo2-SemiBold.ttf,
    /// 20 pt at 50 dpi, kenshi_fonts.xml:33-38) at 1080 lines: the MyGUI default height of 13.9 px em is 17 (ascent 14 + descent 3, hhea
    /// 1005 / -195 at 1000 units), Kenshi then re-sizes the font to that height plus <c>font size2</c> = 3 points (Meitou
    /// src/Meitou.Data/Ui/KenshiFontSize.cs, DefaultSetting), 20 pt again, whose default height is 17. (1440 lines: 20; 2160 lines: 30.)</summary>
    public const int DefaultFontHeight = 17;

    // C++: NativeUI.cpp:9-16
    /// <summary><c>createWidget&lt;TextBox&gt;("Kenshi_TextboxStandardText", bounds, Align::Default, name)</c> + <c>setCaption</c>.</summary>
    public static LayoutBuilder Label(LayoutBuilder parent, int x, int y, int w, int h, string name, string caption) =>
        parent.Open("TextBox", "Kenshi_TextboxStandardText", x, y, w, h, name).Prop("Caption", caption);

    // C++: NativeUI.cpp:18-25
    /// <summary><c>createWidget&lt;Button&gt;("Kenshi_Button1", bounds, Align::Default, name)</c> + <c>setCaption</c>.</summary>
    public static LayoutBuilder Button(LayoutBuilder parent, int x, int y, int w, int h, string name, string caption) =>
        parent.Open("Button", "Kenshi_Button1", x, y, w, h, name).Prop("Caption", caption);

    // C++: MyGUI Button::setStateSelected on a NativeUI.cpp:18-25 button (DebugMenu.cpp:290, :354; ArenaUI.cpp:684-2486)
    /// <summary>The bound properties of <c>setStateSelected(selected)</c>: MyGUI's <c>StateSelected</c> and Meitou's <c>Checked</c>, which is the
    /// one its painter reads for the skin's <c>*_checked</c> look and caption colour (MeitouClient v0.17.3
    /// src/Meitou.Game/Rendering/Ui/UiPainter.cs:830, :925). Every window binds a selected button through this, never one key alone.</summary>
    public static IReadOnlyDictionary<string, string> SelectedState(bool selected) => selected ? SelectedOn : SelectedOff;

    static readonly IReadOnlyDictionary<string, string> SelectedOn =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["StateSelected"] = "true", ["Checked"] = "true" };
    static readonly IReadOnlyDictionary<string, string> SelectedOff =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["StateSelected"] = "false", ["Checked"] = "false" };

    // C++: NativeUI.cpp:27-38
    /// <summary><c>createWidget&lt;ScrollView&gt;("Kenshi_ScrollView", bounds, Align::Default, name)</c>, <c>setVisibleHScroll(false)</c>,
    /// <c>setVisibleVScroll(true)</c>, <c>setCanvasAlign(Align::Left | Align::Top)</c> (Meitou reads VisibleVScroll, VisibleHScroll and
    /// CanvasAlign, src/Meitou.Data/Ui/UiDocument.Widgets.cs:502-508).</summary>
    public static LayoutBuilder Scroll(LayoutBuilder parent, int x, int y, int w, int h, string name) =>
        parent.Open("ScrollView", "Kenshi_ScrollView", x, y, w, h, name)
            .Prop("VisibleHScroll", "false").Prop("VisibleVScroll", "true").Prop("CanvasAlign", "Left Top");

    // C++: NativeUI.cpp:40-49
    /// <summary><c>createWidget&lt;EditBox&gt;("Kenshi_WordWrapEmpty", bounds, Align::Default, name)</c> + <c>setCaption</c>: a read-only word-wrapping
    /// label (the template itself sets WordWrap, Static and MultiLine, Kenshi data/gui/templates/kenshi_templates.xml:556-566).</summary>
    public static LayoutBuilder WrappedLabel(LayoutBuilder parent, int x, int y, int w, int h, string name, string caption) =>
        parent.Open("EditBox", "Kenshi_WordWrapEmpty", x, y, w, h, name).Prop("Caption", caption);

    // C++: NativeUI.cpp:51-55
    /// <summary>Half the font height, at least 4 (integer division).</summary>
    public static int Spacing(int fontHeight) => fontHeight / 2 > 4 ? fontHeight / 2 : 4;

    // C++: NativeUI.cpp:57-62
    /// <summary>The tallest of the sample's text height, its font height and <paramref name="imageHeight"/>, plus two <see cref="Spacing"/>s.</summary>
    public static int RowHeight(int textHeight, int fontHeight, int imageHeight)
    {
        int height = textHeight;
        if (height < fontHeight) height = fontHeight;
        if (height < imageHeight) height = imageHeight;
        return height + 2 * Spacing(fontHeight);
    }
}
