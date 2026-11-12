using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Ui;
using Meitou.Simulation.Dialogue;

namespace Meitou.Game.Ui;

/// <summary>
/// Speech bubbles over the characters that speak (docs/game/dialogue.md "Ambient conversations", the bubble).
/// </summary>
sealed class SpeechBubbles(PlayerInterface player)
{
    /// <summary>
    /// No bubble is drawn (photo mode, unless its Dialogue row shows them).
    /// </summary>
    internal bool Hidden;

    /// <summary>
    /// Most passes of the bubbles' push-apart.
    /// </summary>
    const int BubblePasses = 10;

    /// <summary>
    /// The shown bubbles' fitted texts, so a line is wrapped and measured once rather than every frame; cleared when the screen size, UI mode or text
    /// size changes.
    /// </summary>
    readonly Dictionary<(string Text, bool Large), FittedSpeech> _fittedSpeech = [];

    /// <summary>
    /// The screen size, UI mode and text size the fitted texts were made for.
    /// </summary>
    (int Width, int Height, UiMode Mode, float MinimumPixels) _fittedFor;

    /// <summary>
    /// The bubble sits 4 units above the head and fades from opaque at 1000 units from the camera to gone at 2500, the distance counted x0.5 for the
    /// large font's events, x0.75 for the rest of the interrupting set and x1 for the others; an interrupting bubble is kept on screen
    /// (<b>Verified</b>, the original's bubble code). The box is the original's <c>Kenshi_SpeechPanel</c> layout (<b>Verified</b>): a
    /// <c>Kenshi_SpeechPanelSkin</c> at most 0.2344 of the screen wide, the centred <c>DialogueBubbleText</c> in Kenshi_StandardFont_Large or _Medium
    /// with a black shadow in its 0.0689, 0.125, 0.8556 x 0.75 box. Fitting the panel to its text is Meitou's (the original's sizing is not mapped).
    /// </summary>
    internal void AddSpeech(List<UiControl> controls)
    {
        if (Hidden) return;
        var speech = player.Session.CurrentSnapshot.Speech;
        if (speech.Count == 0 || player.UiPainter is not { } painter) return;

        float scale = player.UiScale;
        var shown = new List<(SpeechBubble Bubble, FittedSpeech Text, float Alpha, UiRect Panel)>();
        foreach (var bubble in speech)
        {
            if (player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == bubble.Speaker) is not { } who) continue;

            var head = player.StealthView.TagAnchor(who, player.Where(who));
            float distance = Vector3.Distance(player.Eye, head) * (bubble.Large ? 0.5f : bubble.Interrupting ? 0.75f : 1);
            float alpha = Math.Clamp((2500 - distance) / 1500f, 0, 1);
            if (alpha <= 0 || player.Project(head) is not { } at) continue;

            var text = SpeechText(painter, bubble);
            float w = (text.Width + 4) / 0.855556f, h = MathF.Max(text.Height / 0.75f, player.Height * 0.0222222f);
            var panel = new UiRect(at.X - w / 2, at.Y - 4 * scale - h, w, h);
            shown.Add((bubble, text, alpha, bubble.Interrupting ? OnScreen(panel) : panel));
        }

        var panels = SeparateBubbles([.. shown.Select(b => b.Panel)]);
        for (int n = 0; n < shown.Count; n++) AddBubble(controls, n, shown[n].Bubble, shown[n].Text, shown[n].Alpha, panels[n]);
    }

    /// <summary>
    /// An interrupting bubble moved inside the screen, its left and top edges winning on a screen too small for it (<c>FUN_1407221c0</c>).
    /// </summary>
    UiRect OnScreen(UiRect panel) =>
        panel with
        {
            X = MathF.Max(MathF.Min(panel.X, player.Width - panel.Width), 0),
            Y = MathF.Max(MathF.Min(panel.Y, player.Height - panel.Height), 0),
        };

    /// <summary>
    /// Adds bubble <paramref name="n"/>: its <c>Kenshi_SpeechPanelSkin</c> panel and its centred, shadowed text at <paramref name="alpha"/>.
    /// </summary>
    void AddBubble(List<UiControl> controls, int n, SpeechBubble bubble, FittedSpeech text, float alpha, UiRect panel)
    {
        float w = panel.Width, h = panel.Height;
        string a = alpha.ToString("R", CultureInfo.InvariantCulture);
        controls.Add(new($"speech-{n}", "Widget", panel, "", "", "Kenshi_SpeechPanelSkin", true,
            new Dictionary<string, string> { ["Alpha"] = a }, Vector4.Zero, Pickable: false));
        controls.Add(new($"speech-text-{n}", "TextBox", new(panel.X + w * 0.0688889f, panel.Y + h * 0.125f, w * 0.855556f, h * 0.75f), text.Lines,
            "", "", true,
            new Dictionary<string, string>
            {
                ["FontName"] = SpeechFont(bubble),
                ["TextAlign"] = "Center",
                ["TextShadow"] = "true",
                ["TextColour"] = "0.902 0.902 0.902",
                ["Alpha"] = a,
            }, Vector4.Zero, Pickable: false, TextScale: 1));
    }

    /// <summary>
    /// Overlapping bubbles push each other apart (<c>FUN_140671f50</c>, once a frame): for every pair whose integer pixel rectangles intersect (edges
    /// included) the shorter-overlap axis is used (horizontal when the width overlap is smaller) and each moves overlap / 2 + 1 pixels, the one with
    /// the smaller coordinate toward smaller; whole passes repeat while any pair moved, at most ten. Positions are recomputed from the heads every
    /// frame, so the shift does not persist.
    /// </summary>
    internal static UiRect[] SeparateBubbles(UiRect[] panels)
    {
        var r = panels.Select(p => (X: (int)MathF.Round(p.X), Y: (int)MathF.Round(p.Y), W: (int)MathF.Round(p.Width), H: (int)MathF.Round(p.Height)))
            .ToArray();
        for (int pass = 0; pass < BubblePasses; pass++)
        {
            bool moved = false;
            for (int i = 0; i < r.Length; i++)
                for (int j = i + 1; j < r.Length; j++)
                    moved |= PushApart(r, i, j);
            if (!moved) break;
        }

        return [.. r.Select(p => new UiRect(p.X, p.Y, p.W, p.H))];
    }

    /// <summary>
    /// Pushes rectangles <paramref name="i"/> and <paramref name="j"/> apart along the axis of the smaller overlap (overlap / 2 + 1 each); false when
    /// they do not overlap.
    /// </summary>
    static bool PushApart((int X, int Y, int W, int H)[] r, int i, int j)
    {
        var (a, b) = (r[i], r[j]);
        int dx = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X), dy = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
        if (dx < 0 || dy < 0) return false;

        if (dx < dy)
        {
            int shift = dx / 2 + 1, sign = a.X < b.X ? -1 : 1;
            (r[i].X, r[j].X) = (a.X + sign * shift, b.X - sign * shift);
        }
        else
        {
            int shift = dy / 2 + 1, sign = a.Y < b.Y ? -1 : 1;
            (r[i].Y, r[j].Y) = (a.Y + sign * shift, b.Y - sign * shift);
        }

        return true;
    }

    /// <summary>
    /// A bubble's text broken into lines and its size in pixels, as its text box draws it.
    /// </summary>
    readonly record struct FittedSpeech(string Lines, float Width, float Height);

    /// <summary>
    /// A bubble's font: the large standard font, else the medium.
    /// </summary>
    static string SpeechFont(SpeechBubble bubble) => bubble.Large ? "Kenshi_StandardFont_Large" : "Kenshi_StandardFont_Medium";

    /// <summary>
    /// <paramref name="bubble"/>'s text wrapped to the layout's widest box and measured in the font it is drawn with, so its advances are the drawn
    /// ones (CJK included, which breaks between characters as it has no spaces).
    /// </summary>
    FittedSpeech SpeechText(UiPainter painter, SpeechBubble bubble)
    {
        float minimumPixels = player.TitleOpen ? 0 : player.StandardPixels;
        if (_fittedFor != (player.Width, player.Height, painter.Mode, minimumPixels) || _fittedSpeech.Count > 64)
        {
            _fittedSpeech.Clear();
            _fittedFor = (player.Width, player.Height, painter.Mode, minimumPixels);
        }

        if (_fittedSpeech.TryGetValue((bubble.Text, bubble.Large), out var fitted)) return fitted;

        float maxWidth = player.Width * 0.234375f * 0.855556f - 4;
        var (lines, size) = painter.WrapCaption(SpeechFont(bubble), bubble.Text, 1, maxWidth, minimumPixels);
        fitted = new(string.Join('\n', lines), size.Width, size.Height);
        _fittedSpeech[(bubble.Text, bubble.Large)] = fitted;
        return fitted;
    }
}