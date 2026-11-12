using Meitou.Data.Ui;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Rendering.Ui;

/// <summary>
/// Text measuring for layouts that must fit what <see cref="Draw"/> will paint (the tooltips).
/// </summary>
public sealed partial class UiPainter
{
    /// <summary>
    /// The size of a measured text block.
    /// </summary>
    public readonly record struct TextSize(float Width, float Height);

    /// <summary>
    /// The font a TrueType font resource (<c>Kenshi_StandardFont_Medium</c> ...) is drawn with at <paramref name="scale"/> (the control's
    /// <c>TextScale</c>, else the viewport height over 1080), baked at the em pixel size the original resizes it to; null when
    /// <paramref name="fontName"/> is no such resource.
    /// </summary>
    DebugOverlay? ResourceFont(string fontName, float scale, float minimumTextPixels)
    {
        if (FontSpec(fontName) is not { } spec || FontPath(spec.Source) is not { } sourcePath) return null;

        float resolution = spec.Resolution > 0 ? spec.Resolution : 50;
        bool faithful = Mode == UiMode.Faithful;

        // Faithful: the original's size at this scale; Meitou: the 1080-line size scaled (its own look).
        float points = faithful
            ? ScaledFontSize(sourcePath, spec, scale)
            : EffectiveFontSize(sourcePath, spec.Size > 0 ? spec.Size : 20, resolution) * scale;
        float em = points * resolution / 72;
        long size64;
        uint dpi;
        if (faithful && em >= Math.Max(6, minimumTextPixels) && em <= 192)
        {
            // MyGUI's own request: fractional points at the resource's dpi (1/16 point steps, so a resized window makes a bounded number of fonts).
            size64 = Math.Max(1, (long)MathF.Round(points * 16)) * 4;
            dpi = (uint)MathF.Round(resolution);
        }
        else
        {
            // Whole pixels: Meitou's look, and the display policy's minimum size in either mode.
            size64 = Math.Clamp((int)MathF.Round(Math.Max(minimumTextPixels, em)), 6, 192) * 64L;
            dpi = 72;
        }

        return UiFontFor(fontName, spec, sourcePath, size64, dpi, faithful, faithful || !spec.Antialias);
    }

    /// <summary>
    /// The height of <paramref name="control"/>'s caption as <see cref="Draw"/> would paint it: its font, wrapped to its text area when it has
    /// <c>WordWrap</c>.
    /// </summary>
    public float TextHeight(UiControl control, int viewportHeight, float minimumTextPixels)
    {
        string fontName = control.Properties.GetValueOrDefault("FontName")
            ?? (_resources.Templates.TryGetValue(control.Skin, out var template)
                ? template.Descendants("Property").FirstOrDefault(p => (string?)p.Attribute("key") == "FontName")?.Attribute("value")?.Value
                : null)
            ?? "Kenshi_StandardFont_Medium";
        float wrap = control.Properties.GetValueOrDefault("WordWrap") == "true" ? UiTextLayout.ContentRect(_resources, control).Width : 0;
        return MeasureText(fontName, control.Caption, viewportHeight, wrap, minimumTextPixels).Height;
    }

    /// <summary>
    /// The size <see cref="Draw"/> gives <paramref name="text"/> in font resource <paramref name="fontName"/> on a viewport
    /// <paramref name="viewportHeight"/> high: wrapped to <paramref name="wrapWidth"/> when that is above zero (a control with <c>WordWrap</c> true),
    /// else one line per line break. <paramref name="minimumTextPixels"/> is the value passed to <see cref="Draw"/>.
    /// </summary>
    public TextSize MeasureText(string fontName, string text, int viewportHeight, float wrapWidth, float minimumTextPixels)
    {
        var font = ResourceFont(fontName, Scale(viewportHeight), minimumTextPixels) ?? _standardFont ?? _overlay;
        float scale = font == _standardFont || font == _overlay ? ClampedScale(viewportHeight) : 1;
        return Measure(font, text, wrapWidth, scale);
    }

    /// <summary>
    /// The lines <paramref name="text"/> wraps to at <paramref name="wrapWidth"/> and their size, in font resource <paramref name="fontName"/> as
    /// <see cref="Draw"/> paints a caption with <c>TextScale</c> <paramref name="textScale"/> and a nonzero <paramref name="minimumTextPixels"/> (a
    /// panel fitted to its text before it is drawn). The lines are plain text: colour tags are measured as the drawn text shows them, without their
    /// markup, and dropped.
    /// </summary>
    public (IReadOnlyList<string> Lines, TextSize Size) WrapCaption(string fontName, string text, float textScale, float wrapWidth,
        float minimumTextPixels)
    {
        var resourceFont = ResourceFont(fontName, textScale, minimumTextPixels);
        var font = resourceFont ?? _standardFont ?? _overlay;
        float scale = resourceFont is null ? textScale : 1;
        text = MyGuiTextTags.Strip(text.Replace("\\n", "\n", StringComparison.Ordinal));
        var lines = font.WrapText(text, wrapWidth, scale);
        float width = 0;
        foreach (string line in lines) width = Math.Max(width, font.MeasureText(line, scale));
        return (lines, new(width, lines.Count * font.LineHeight * scale));
    }

    /// <summary>
    /// Measures text exactly as a settings caption with an explicit <c>FontPixels</c> size is drawn.
    /// </summary>
    public TextSize MeasurePixelText(string text, float wrapWidth, int pixels, float resourceScale)
    {
        if (StandardSource() is { } source)
        {
            var font = UiFontFor("", source.Spec, source.Path, Math.Clamp(pixels, 8, 96) * 64L, 72, false, false);
            return Measure(font, text, wrapWidth, 1);
        }

        if (ResourceFont("Kenshi_StandardFont", resourceScale, 0) is { } resourceFont)
            return Measure(resourceFont, text, wrapWidth, 1);

        return Measure(_standardFont ?? _overlay, text, wrapWidth, resourceScale);
    }

    /// <summary>
    /// The size of <paramref name="text"/> (its tags taken out) in <paramref name="font"/> at <paramref name="scale"/>: wrapped to <paramref
    /// name="wrapWidth"/> when it is above 0, else one line per break.
    /// </summary>
    static TextSize Measure(DebugOverlay font, string text, float wrapWidth, float scale)
    {
        text = MyGuiTextTags.Strip(text.Replace("\\n", "\n", StringComparison.Ordinal)).ReplaceLineEndings("\n");
        var lines = wrapWidth > 0 ? font.WrapText(text, wrapWidth, scale) : text.Split('\n');
        float width = 0;
        foreach (string line in lines) width = Math.Max(width, font.MeasureText(line, scale));
        return new(width, Math.Max(1, lines.Count) * font.LineHeight * scale);
    }
}
