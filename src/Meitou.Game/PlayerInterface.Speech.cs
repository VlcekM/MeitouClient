using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game;

/// <summary>Speech bubbles over the characters that speak (docs/game/dialogue.md "Ambient conversations", the bubble).</summary>
sealed partial class PlayerInterface
{
    /// <summary>
    /// The bubble sits 4 units above the head and fades from opaque at 1000 units from the camera to gone at 2500, the distance counted x0.5 for the large font's events and
    /// x0.75 for the others (<b>Observed</b>, the original's bubble code). The box is the original's <c>Kenshi_SpeechPanel</c> layout (<b>Verified</b>): a
    /// <c>Kenshi_SpeechPanelSkin</c> at most 0.2344 of the screen wide, the centred <c>DialogueBubbleText</c> in Kenshi_StandardFont_Large or _Medium with a black shadow
    /// in its 0.0689, 0.125, 0.8556 x 0.75 box. Fitting the panel to its text is Meitou's (the original's sizing is not mapped).
    /// </summary>
    void AddSpeech(List<UiControl> controls)
    {
        var speech = session.CurrentSnapshot.Speech;
        if (speech.Count == 0 || uiPainter is not { } painter) return;
        float scale = Height / 1080f;
        int n = 0;
        foreach (var bubble in speech)
        {
            if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == bubble.Speaker) is not { } who) continue;
            var head = where(who) + new Vector3(0, CharacterHeight + 4, 0);
            float distance = Vector3.Distance(Eye, head) * (bubble.Large ? 0.5f : 0.75f);
            float alpha = Math.Clamp((2500 - distance) / 1500f, 0, 1);
            if (alpha <= 0 || Project(head) is not { } at) continue;
            // Kenshi_StandardFont_Large / _Medium: 24 / 20 points at resolution 50, i.e. size x 50 / 72 pixels on a 1080 high screen.
            float em = (bubble.Large ? 24 : 20) * 50f / 72f * scale, lineHeight = em * 1.25f;
            // The overlay font's advance for a CJK glyph is about half the width the game font draws it at, so those count full width here.
            float Measure(string text)
            {
                float sum = 0;
                foreach (var rune in text.EnumerateRunes()) sum += FullWidth(rune.Value) ? em : painter.MeasureStandard(rune.ToString(), em);
                return sum;
            }
            var lines = Wrap(bubble.Text, Measure, Width * 0.234375f * 0.855556f);
            float textWidth = lines.Max(Measure) + 4, textHeight = lines.Count * lineHeight;
            float w = textWidth / 0.855556f, h = MathF.Max(textHeight / 0.75f, Height * 0.0222222f);
            var panel = new UiRect(at.X - w / 2, at.Y - 4 * scale - h, w, h);
            string a = alpha.ToString("R", CultureInfo.InvariantCulture);
            controls.Add(new($"speech-{n}", "Widget", panel, "", "", "Kenshi_SpeechPanelSkin", true, new Dictionary<string, string> { ["Alpha"] = a }, Vector4.Zero, Pickable: false));
            controls.Add(new($"speech-text-{n++}", "TextBox", new(panel.X + w * 0.0688889f, panel.Y + h * 0.125f, w * 0.855556f, h * 0.75f), string.Join('\n', lines), "", "", true,
                new Dictionary<string, string>
                {
                    ["FontName"] = bubble.Large ? "Kenshi_StandardFont_Large" : "Kenshi_StandardFont_Medium", ["TextAlign"] = "Center", ["TextShadow"] = "true",
                    ["TextColour"] = "0.902 0.902 0.902", ["Alpha"] = a,
                }, Vector4.Zero, Pickable: false, TextScale: 1));
        }
    }

    /// <summary>
    /// The words of <paramref name="text"/> in lines no wider than <paramref name="maxWidth"/> pixels. A word wider than a line is broken between characters: Chinese,
    /// Japanese and Korean are written without spaces, so the whole sentence would otherwise be one word on one line, wider than the bubble and clipped.
    /// </summary>
    static List<string> Wrap(string text, Func<string, float> measure, float maxWidth)
    {
        var lines = new List<string>();
        string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string tried = current.Length == 0 ? word : current + " " + word;
            if (measure(tried) <= maxWidth || current.Length == 0 && measure(word) <= maxWidth) { current = tried; continue; }
            if (current.Length > 0) { lines.Add(current); current = ""; }
            foreach (var rune in word.EnumerateRunes())
            {
                string piece = rune.ToString();
                if (current.Length > 0 && measure(current + piece) > maxWidth) { lines.Add(current); current = piece; }
                else current += piece;
            }
        }
        if (current.Length > 0 || lines.Count == 0) lines.Add(current);
        return lines;
    }

    /// <summary>Whether a rune is drawn full width (CJK, kana, hangul, full-width forms).</summary>
    static bool FullWidth(int codePoint) =>
        codePoint is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xA000 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x20000 and <= 0x3FFFD;
}
