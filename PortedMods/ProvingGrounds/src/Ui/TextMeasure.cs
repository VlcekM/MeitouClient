// MyGUI getTextSize / getFontHeight for the Proving Grounds windows (task 4.5b; moved out of ArenaUI.cs in its review so every window measures one
// way). Meitou: the faithful painter's UiPainter.MeasureText (UiPainter.Tooltips.cs:71), the call the bindings' TextHeight makes for the faithful
// screens (ScreenRouter.cs:76, UiPainter.Tooltips.cs:55-64), with the window height and PlayerInterface.StandardPixels.
namespace ProvingGrounds.Ui;

/// <summary>
/// THE way a Proving Grounds window measures text (MyGUI <c>getTextSize</c>): the size the faithful screens draw a text at, through the faithful
/// painter's <c>UiPainter.MeasureText(font, text, window height, wrap width, StandardPixels)</c>. Each screen owns one instance (its caches are its
/// own) and calls <see cref="UseCurrent"/> before it measures (at the start of its Layout and Bind): a new painter, window height, em or font height
/// empties the caches. <see cref="Height"/> word-wraps to a width; <see cref="Width"/> is a text on one line (a button's natural width). Results are
/// cached per font, text and width, so the steady state allocates nothing. Without a painter (offline, PgCheck) an estimate: half a font height per
/// character, one font height per line. Window thread only.
/// </summary>
sealed class TextMeasure
{
    /// <summary>Kenshi's fonts (kenshi_templates.xml:486, :5, :465): the standard text (labels, Kenshi_WordWrap*) and the painted text (buttons).</summary>
    internal const string StandardFont = "Kenshi_StandardFont_Medium", PaintedFont = "Kenshi_PaintedTextFont_Medium";

    readonly Dictionary<(string Font, string Text, int Width), int> heights = [];
    readonly Dictionary<(string Font, string Text), int> widths = [];
    readonly Dictionary<(string Font, string Text, int Width), (int W, int H)> sizes = [];   // task 4.6a: Size
    (object? Painter, int Height, float Em, int Font) used;

    /// <summary>Measures with the interface's current painter, window height, em and font height (ScreenHost); a change empties the caches.</summary>
    internal void UseCurrent() => Use(ScreenHost.Painter, ScreenHost.ViewSize.Height, ScreenHost.StandardEm ?? 0, ScreenHost.FontHeight);

    /// <summary>Measures with this painter, window height, em and font height from now on; a change empties the caches.</summary>
    internal void Use(Meitou.Game.Rendering.Ui.UiPainter? painter, int viewHeight, float em, int fontHeight)
    {
        var now = ((object?)painter, viewHeight, em, fontHeight);
        if (ReferenceEquals(now.Item1, used.Painter) && now.viewHeight == used.Height && now.em == used.Em && now.fontHeight == used.Font) return;
        used = now;
        heights.Clear();
        widths.Clear();
        sizes.Clear();
    }

    Meitou.Game.Rendering.Ui.UiPainter? Painter => used.Painter as Meitou.Game.Rendering.Ui.UiPainter;
    int Font => used.Font > 0 ? used.Font : Core.Glue.NativeUI.DefaultFontHeight;

    /// <summary>The height of <paramref name="text"/> in <paramref name="font"/> word-wrapped to <paramref name="width"/> pixels.</summary>
    internal int Height(string font, string text, int width)
    {
        width = Math.Max(1, width);
        if (heights.TryGetValue((font, text, width), out int h)) return h;
        if (Painter is { } painter)
        {
            float m = painter.MeasureText(font, text, used.Height, width, used.Em).Height;
            h = float.IsFinite(m) && m > 0 ? (int)MathF.Ceiling(m - 0.01f) : Font;
        }
        else
        {
            int perChar = Math.Max(1, Font / 2), lines = 0;
            foreach (var part in text.Split('\n')) lines += Math.Max(1, (part.Length * perChar + width - 1) / width);
            h = lines * Font;
        }
        if (heights.Count > 4096) heights.Clear();
        heights[(font, text, width)] = h;
        return h;
    }

    /// <summary>The width of <paramref name="text"/> in <paramref name="font"/> on one line.</summary>
    internal int Width(string font, string text)
    {
        if (widths.TryGetValue((font, text), out int w)) return w;
        if (Painter is { } painter)
        {
            float m = painter.MeasureText(font, text, used.Height, 0, used.Em).Width;
            w = float.IsFinite(m) && m > 0 ? (int)MathF.Ceiling(m - 0.01f) : 0;
        }
        else w = text.Length * Math.Max(1, Font / 2);
        if (widths.Count > 512) widths.Clear();
        widths[(font, text)] = w;
        return w;
    }

    // ---- task 4.6a ----
    /// <summary>The size of <paramref name="text"/> in <paramref name="font"/> (MyGUI <c>getTextSize</c>: the widest line and the height of all lines)
    /// word-wrapped to <paramref name="wrapWidth"/> pixels; 0 (or less) = no wrapping (a TextBox). Task 4.6a's windows measure the wrapped width of
    /// an EditBox (ResultsUI.cpp:240-245) and the one-line size of a TextBox.</summary>
    internal (int Width, int Height) Size(string font, string text, int wrapWidth)
    {
        int wrap = Math.Max(0, wrapWidth);
        if (sizes.TryGetValue((font, text, wrap), out var s)) return s;
        if (Painter is { } painter)
        {
            var m = painter.MeasureText(font, text, used.Height, wrap, used.Em);
            s = (float.IsFinite(m.Width) && m.Width > 0 ? (int)MathF.Ceiling(m.Width - 0.01f) : 0,
                float.IsFinite(m.Height) && m.Height > 0 ? (int)MathF.Ceiling(m.Height - 0.01f) : Font);
        }
        else
        {
            int perChar = Math.Max(1, Font / 2), widest = 0, lines = 0;
            foreach (var part in text.Split('\n'))
            {
                int w = part.Length * perChar;
                if (wrap > 0 && w > wrap) { lines += (w + wrap - 1) / wrap; widest = Math.Max(widest, wrap); }
                else { lines++; widest = Math.Max(widest, w); }
            }
            s = (widest, lines * Font);
        }
        if (sizes.Count > 4096) sizes.Clear();
        sizes[(font, text, wrap)] = s;
        return s;
    }
    // ---- end task 4.6a ----
}
