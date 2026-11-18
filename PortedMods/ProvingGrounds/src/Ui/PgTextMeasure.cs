// Task 4.6a: the text measure of the Results and Leaderboard windows (MyGUI getTextSize of their labels and buttons): an IPgTextMeasure adapter
// over the shared TextMeasure (src/Ui/TextMeasure.cs, THE way a window measures text). Window thread only.
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Ui;

/// <summary><see cref="IPgTextMeasure"/> over a screen's <see cref="TextMeasure"/>: standard-font text (Kenshi_TextboxStandardText, the client of
/// Kenshi_WordWrapEmpty and Kenshi_WordWrap) and a Kenshi_Button1 caption in the painted font.</summary>
sealed class PgTextMeasure(TextMeasure text) : IPgTextMeasure
{
    public (int Width, int Height) Standard(string s, int wrapWidth) => text.Size(TextMeasure.StandardFont, s, wrapWidth);
    public int ButtonWidth(string caption) => text.Width(TextMeasure.PaintedFont, caption);
}
