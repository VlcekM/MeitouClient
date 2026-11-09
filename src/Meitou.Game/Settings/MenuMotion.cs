using System.Diagnostics;
using System.Globalization;

namespace Meitou.Game.Settings;

/// <summary>
/// The settings window's motion: its open and close fade, the page fade, hover fades and smooth scrolling.
/// </summary>
sealed class MenuMotion(SettingsMenu menu)
{
    internal float MenuProgress, ContentProgress, FrameDelta;
    internal long LastFrameAt;
    internal string? AnimationScope;
    Dictionary<string, float>? _hoverProgress;

    internal static float Ease(float t) => t * t * (3 - 2 * t);

    internal void AdvanceMotion()
    {
        long now = Stopwatch.GetTimestamp();
        FrameDelta = LastFrameAt == 0 ? 1 / 60f : Math.Clamp((float)Stopwatch.GetElapsedTime(LastFrameAt, now).TotalSeconds, 0, 0.05f);
        LastFrameAt = now;

        MenuProgress = Math.Clamp(MenuProgress + FrameDelta / (menu.MenuOpen ? 0.14f : -0.1f), 0, 1);
        ContentProgress = Math.Min(1, ContentProgress + FrameDelta / 0.12f);
        if (!menu.MenuOpen && MenuProgress == 0) menu.Player.SetSettingsOpen(false);
    }

    internal float Hover(string label, bool hovered)
    {
        var states = _hoverProgress ??= [];
        string key = $"{menu.Page}:{menu.GraphicsPage}:{AnimationScope}:{label}";

        states.TryGetValue(key, out float progress);
        progress = Math.Clamp(progress + FrameDelta / (hovered ? 0.16f : -0.16f), 0, 1);
        states[key] = progress;
        return Ease(progress);
    }

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

    internal void AdvanceScroll()
    {
        menu.ScrollTarget = Math.Clamp(menu.ScrollTarget, 0, menu.MaxScroll);
        menu.ScrollOffset += (menu.ScrollTarget - menu.ScrollOffset) * (1 - MathF.Exp(-FrameDelta * 20));

        if (MathF.Abs(menu.ScrollTarget - menu.ScrollOffset) < 0.1f) menu.ScrollOffset = menu.ScrollTarget;
        menu.ScrollOffset = Math.Clamp(menu.ScrollOffset, 0, menu.MaxScroll);
    }
}
