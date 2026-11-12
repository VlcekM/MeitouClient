using System.Diagnostics;
using System.Globalization;

namespace Meitou.Game.Settings;

/// <summary>
/// The settings window's motion: its open and close fade, the page fade, hover fades and smooth scrolling.
/// </summary>
sealed class MenuMotion(SettingsMenu menu)
{
    /// <summary>
    /// The window's open progress (0..1), the page's fade-in progress (0..1) and the frame's seconds (at most 0.05).
    /// </summary>
    internal float MenuProgress, ContentProgress, FrameDelta;

    /// <summary>
    /// The Stopwatch timestamp of the last frame; 0 before the first.
    /// </summary>
    internal long LastFrameAt;

    /// <summary>
    /// The row whose hover states are being read (part of their keys).
    /// </summary>
    internal string? AnimationScope;

    /// <summary>
    /// Each hover's progress (0..1), by page, Graphics page, row and label.
    /// </summary>
    Dictionary<string, float>? _hoverProgress;

    /// <summary>
    /// Smoothstep of <paramref name="t"/>.
    /// </summary>
    internal static float Ease(float t) => t * t * (3 - 2 * t);

    /// <summary>
    /// Advances the frame time and the window's and page's fades; the window closes when its fade-out ends.
    /// </summary>
    internal void AdvanceMotion()
    {
        long now = Stopwatch.GetTimestamp();
        FrameDelta = LastFrameAt == 0 ? 1 / 60f : Math.Clamp((float)Stopwatch.GetElapsedTime(LastFrameAt, now).TotalSeconds, 0, 0.05f);
        LastFrameAt = now;

        MenuProgress = Math.Clamp(MenuProgress + FrameDelta / (menu.MenuOpen ? 0.14f : -0.1f), 0, 1);
        ContentProgress = Math.Min(1, ContentProgress + FrameDelta / 0.12f);
        if (!menu.MenuOpen && MenuProgress == 0) menu.Player.SetSettingsOpen(false);
    }

    /// <summary>
    /// The eased hover progress of <paramref name="label"/> in this row, moved towards <paramref name="hovered"/> over 0.16 s.
    /// </summary>
    internal float Hover(string label, bool hovered)
    {
        var states = _hoverProgress ??= [];
        string key = $"{menu.Page}:{menu.GraphicsPage}:{AnimationScope}:{label}";

        states.TryGetValue(key, out float progress);
        progress = Math.Clamp(progress + FrameDelta / (hovered ? 0.16f : -0.16f), 0, 1);
        states[key] = progress;
        return Ease(progress);
    }

    /// <summary>
    /// Multiplies the alpha of the controls from <paramref name="first"/> on by <paramref name="amount"/> (nothing at 1).
    /// </summary>
    internal void Opacity(int first, float amount)
    {
        if (amount >= 1) return;

        for (int i = first; i < menu.Controls.Count; i++)
        {
            var control = menu.Controls[i];
            string? text = control.Properties.GetValueOrDefault("Alpha");
            float old = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float alpha) ? alpha : 1;
            string value = (old * amount).ToString(CultureInfo.InvariantCulture);
            menu.Controls[i] = control with
            {
                Properties = new Dictionary<string, string>(control.Properties) { ["Alpha"] = value },
            };
        }
    }

    /// <summary>
    /// Moves the scroll towards its target (clamped), snapping within 0.1.
    /// </summary>
    internal void AdvanceScroll()
    {
        menu.ScrollTarget = Math.Clamp(menu.ScrollTarget, 0, menu.MaxScroll);
        menu.ScrollOffset += (menu.ScrollTarget - menu.ScrollOffset) * (1 - MathF.Exp(-FrameDelta * 20));

        if (MathF.Abs(menu.ScrollTarget - menu.ScrollOffset) < 0.1f) menu.ScrollOffset = menu.ScrollTarget;
        menu.ScrollOffset = Math.Clamp(menu.ScrollOffset, 0, menu.MaxScroll);
    }
}
