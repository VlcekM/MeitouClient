using System.Diagnostics;
using System.Globalization;

namespace Meitou.Game.Settings;

/// <summary>The settings window's motion: its open and close fade, the page fade, hover fades and smooth scrolling.</summary>
sealed class MenuMotion(SettingsMenu menu)
{
    internal float menuProgress, contentProgress, frameDelta;
    internal long lastFrameAt;
    internal string? animationScope;
    Dictionary<string, float>? hoverProgress;

    internal static float Ease(float t) => t * t * (3 - 2 * t);

    internal void AdvanceMotion()
    {
        long now = Stopwatch.GetTimestamp();
        frameDelta = lastFrameAt == 0 ? 1 / 60f : Math.Clamp((float)Stopwatch.GetElapsedTime(lastFrameAt, now).TotalSeconds, 0, 0.05f);
        lastFrameAt = now;
        menuProgress = Math.Clamp(menuProgress + frameDelta / (menu.visible ? 0.14f : -0.1f), 0, 1);
        contentProgress = Math.Min(1, contentProgress + frameDelta / 0.12f);
        if (!menu.visible && menuProgress == 0) menu.player.SetSettingsOpen(false);
    }

    internal float Hover(string label, bool hovered)
    {
        var states = hoverProgress ??= [];
        string key = $"{menu.page}:{menu.graphicsPage}:{animationScope}:{label}";
        states.TryGetValue(key, out float progress);
        progress = Math.Clamp(progress + frameDelta / (hovered ? 0.16f : -0.16f), 0, 1);
        states[key] = progress;
        return Ease(progress);
    }

    internal void Opacity(int first, float amount)
    {
        if (amount >= 1) return;
        for (int i = first; i < menu.controls.Count; i++)
        {
            var control = menu.controls[i];
            float old = float.TryParse(control.Properties.GetValueOrDefault("Alpha"), NumberStyles.Float, CultureInfo.InvariantCulture, out float alpha) ? alpha : 1;
            menu.controls[i] = control with
            {
                Properties = new Dictionary<string, string>(control.Properties) { ["Alpha"] = (old * amount).ToString(CultureInfo.InvariantCulture) },
            };
        }
    }

    internal void AdvanceScroll()
    {
        menu.scrollTarget = Math.Clamp(menu.scrollTarget, 0, menu.maxScroll);
        menu.scroll += (menu.scrollTarget - menu.scroll) * (1 - MathF.Exp(-frameDelta * 20));
        if (MathF.Abs(menu.scrollTarget - menu.scroll) < 0.1f) menu.scroll = menu.scrollTarget;
        menu.scroll = Math.Clamp(menu.scroll, 0, menu.maxScroll);
    }
}
