using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Settings;

/// <summary>
/// The settings window's position, drag, minimize / restore bar and opacity.
/// </summary>
sealed class SettingsWindow(SettingsMenu menu)
{
    #region State

    /// <summary>
    /// The minimized bar's width and height, in the menu's units.
    /// </summary>
    const float MinimizedWidth = 280, MinimizedHeight = 36;

    /// <summary>
    /// The full window's header bar's height, in the menu's units.
    /// </summary>
    const float HeaderHeight = 44;

    /// <summary>
    /// Seconds between two title-bar presses to count as a double-click (re-centre when expanded, restore when minimized).
    /// </summary>
    const double DoubleClickSeconds = 0.35;

    /// <summary>
    /// The window's offset from the screen's centre, in the menu's units.
    /// </summary>
    Vector2 _offset;

    /// <summary>
    /// While the title bar is dragged, the pointer where the drag began (pixels) and the window's offset then (menu units).
    /// </summary>
    Vector2? _dragGrab;

    /// <summary>
    /// The window's offset when the current drag began, in the menu's units.
    /// </summary>
    Vector2 _dragStartOffset;

    /// <summary>
    /// Stopwatch timestamp of the last title-bar press, for double-click detection.
    /// </summary>
    long _lastBarClick;

    /// <summary>
    /// The opacity slider shared by the header bar and the Interface page, made on first use.
    /// </summary>
    Slider? _opacitySlider;

    /// <summary>
    /// Whether the settings window is collapsed to its floating title bar (world unpaused and input passes around it).
    /// </summary>
    internal bool Minimized { get; private set; }

    /// <summary>
    /// Whether the title bar is being dragged now.
    /// </summary>
    internal bool Dragging => _dragGrab is not null;

    /// <summary>
    /// The minimized bar's bounds as the last frame drew it, pixels.
    /// </summary>
    internal UiRect MinimizedBounds { get; private set; }

    /// <summary>
    /// The window's opacity, clamped to [<see cref="UserConfig.MinSettingsOpacity"/>, 1].
    /// </summary>
    internal float Opacity => Math.Clamp(menu.Config.SettingsOpacity, UserConfig.MinSettingsOpacity, 1);

    /// <summary>
    /// The full-screen backdrop's alpha: 0.64 at full opacity, fading out to 0 at 35% opacity so the live world shows through.
    /// </summary>
    internal float BackdropAlpha => 0.64f * Math.Clamp((Opacity - 0.35f) / 0.65f, 0, 1);

    /// <summary>
    /// The opacity slider (25% to 100%, in 5% steps, default 100%).
    /// </summary>
    internal Slider OpacitySlider => _opacitySlider ??= new("Settings window opacity", UserConfig.MinSettingsOpacity, 1,
        () => Opacity, SetOpacity, "0.00", Text: v => $"{v * 100:0}%", Faithful: 1, Scale: 100, Step: 0.05f);

    /// <summary>
    /// Sets the window's opacity, rounded to 5% and clamped, into <see cref="UserConfig.SettingsOpacity"/>.
    /// </summary>
    internal void SetOpacity(float value) =>
        menu.Config.SettingsOpacity = Math.Clamp(MathF.Round(value * 20) / 20, UserConfig.MinSettingsOpacity, 1);

    /// <summary>
    /// Returns the window to the centre of the screen.
    /// </summary>
    internal void ResetPosition() => _offset = Vector2.Zero;

    /// <summary>
    /// Collapses the window to its floating title bar (unpausing the world) or restores it (re-pausing).
    /// </summary>
    internal void SetMinimized(bool value)
    {
        if (Minimized == value) return;

        Minimized = value;
        _dragGrab = null;
        menu.ActiveChoice = null;
        menu.Rebinding = null;
        menu.ResetPending = false;
        menu.PendingAction = null;
        menu.PresetNaming.Open = false;
        menu.Search.Focused = false;
        menu.ModsPage.FilterFocused = false;
        menu.EditingNumber = null;
        menu.TextEdit.Cancel();
        menu.Focus = -1;

        if (menu.MenuOpen) menu.Player.SetSettingsOpen(!value);
    }

    #endregion

    #region Layout and drag

    /// <summary>
    /// Places the full window (and the minimized bar) within a <paramref name="width"/> × <paramref name="height"/> screen at
    /// <paramref name="scale"/>, clamped so the window stays on screen, and applies the opening slide <paramref name="slideOffset"/>.
    /// </summary>
    internal UiRect Place(int width, int height, float scale, float slideOffset)
    {
        float w = MathF.Round(1120 * scale), h = MathF.Round(720 * scale);
        float centerX = (width - w) / 2, centerY = (height - h) / 2;
        float x = Math.Clamp(centerX + _offset.X * scale, 0, Math.Max(0, width - w));
        float y = Math.Clamp(centerY + _offset.Y * scale, 0, Math.Max(0, height - h));

        if (!Minimized && scale > 0) _offset = new((x - centerX) / scale, (y - centerY) / scale);

        float barW = MathF.Round(MinimizedWidth * scale), barH = MathF.Round(MinimizedHeight * scale);
        float barX = Math.Clamp(centerX + _offset.X * scale, 0, Math.Max(0, width - barW));
        float barY = Math.Clamp(centerY + _offset.Y * scale, 0, Math.Max(0, height - barH));

        if (Minimized && scale > 0) _offset = new((barX - centerX) / scale, (barY - centerY) / scale);

        MinimizedBounds = new(MathF.Round(barX), MathF.Round(barY), barW, barH);

        return new(MathF.Round(x), MathF.Round(y) + slideOffset, w, h);
    }

    /// <summary>
    /// Starts dragging the window when <paramref name="at"/> lands on its title bar (or minimized bar); a double-click re-centres an expanded
    /// window or restores a minimized one.
    /// </summary>
    internal bool BeginDrag(Vector2 at)
    {
        var bar = Minimized ? MinimizedBounds : menu.Canvas.R(0, 0, 1120, HeaderHeight);
        if (!bar.Contains(at.X, at.Y)) return false;

        long now = Stopwatch.GetTimestamp();
        bool doubleClick = _lastBarClick != 0 && Stopwatch.GetElapsedTime(_lastBarClick, now).TotalSeconds <= DoubleClickSeconds;
        _lastBarClick = doubleClick ? 0 : now;

        if (doubleClick)
        {
            if (Minimized) SetMinimized(false);
            else ResetPosition();
            return true;
        }

        _dragGrab = at;
        _dragStartOffset = _offset;
        return true;
    }

    /// <summary>
    /// Moves the window with the dragged pointer.
    /// </summary>
    internal void DragMove(Vector2 at)
    {
        if (_dragGrab is not { } grab || menu.Scale <= 0) return;

        _offset = _dragStartOffset + (at - grab) / menu.Scale;
    }

    /// <summary>
    /// Ends an active window drag; true when one was running.
    /// </summary>
    internal bool EndDrag()
    {
        bool had = _dragGrab is not null;
        _dragGrab = null;
        return had;
    }

    #endregion

    #region Drawing

    /// <summary>
    /// The compact opacity slider and the minimize button in the full window's header bar.
    /// </summary>
    internal void DrawHeaderControls()
    {
        DrawHeaderOpacity();
        DrawMinimizeButton();
    }

    /// <summary>
    /// The compact opacity slider in the header bar (between the title and the search box).
    /// </summary>
    void DrawHeaderOpacity()
    {
        var canvas = menu.Canvas;
        var slider = OpacitySlider;

        canvas.Text("Opacity", canvas.R(554, 10, 58, 24), 11, SettingsCanvas.MutedInk, "Right VCenter");

        var track = canvas.R(620, 21, 82, 3);
        canvas.Rect(track, new(0.10f, 0.08f, 0.07f, 1));
        canvas.Rect(track with { Width = track.Width * slider.Fraction }, SettingsCanvas.Brass with { W = 0.8f });

        var knob = new UiRect(track.X + slider.Fraction * track.Width - 3 * menu.Scale, track.Y - 4 * menu.Scale,
            6 * menu.Scale, 11 * menu.Scale);
        canvas.Rect(knob, SettingsCanvas.LightInk);

        string percent = ((int)MathF.Round(Opacity * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        canvas.Text(percent, canvas.R(706, 10, 40, 24), 11, SettingsCanvas.MainInk, "Right VCenter", translate: false);

        var hit = canvas.R(614, 10, 94, 24);
        menu.Hits.Add(new(hit, () => { }, slider, track, "Opacity"));
    }

    /// <summary>
    /// The minimize button in the header bar, left of the close button.
    /// </summary>
    void DrawMinimizeButton()
    {
        var canvas = menu.Canvas;
        var at = canvas.R(1030, 6, 31, 32);
        bool hovered = at.Contains(menu.Pointer.X, menu.Pointer.Y);

        canvas.Surface(at, "button", hovered && menu.PointerDown, hovered);

        float lineW = MathF.Round(13 * menu.Scale), lineH = MathF.Max(2, MathF.Round(2 * menu.Scale));
        var line = new UiRect(MathF.Round(at.X + (at.Width - lineW) / 2), MathF.Round(at.Y + at.Height * 0.62f), lineW, lineH);
        canvas.Rect(line, hovered ? SettingsCanvas.LightInk : SettingsCanvas.MainInk);

        menu.Hits.Add(new(at, () => SetMinimized(true), Label: "Minimise"));
    }

    /// <summary>
    /// The floating bar shown while minimized: title, restore button and close button.
    /// </summary>
    internal void DrawMinimized()
    {
        var canvas = menu.Canvas;
        var bar = MinimizedBounds;
        float s = menu.Scale;
        bool barHovered = bar.Contains(menu.Pointer.X, menu.Pointer.Y);

        canvas.Surface(bar, "window");

        if (!canvas.Meitou)
        {
            menu.Controls.Add(new("settings-header", "Panel",
                new(bar.X + 4 * s, bar.Y + 2 * s, bar.Width - 8 * s, bar.Height - 4 * s),
                "", "", "Kenshi_GenericWindowHeaderSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        }

        if (barHovered)
        {
            float border = MathF.Max(1, MathF.Round(s));
            var edge = SettingsCanvas.Brass with { W = 0.6f };
            canvas.Rect(bar with { Height = border }, edge);
            canvas.Rect(bar with { Y = bar.Y + bar.Height - border, Height = border }, edge);
            canvas.Rect(bar with { Width = border }, edge);
            canvas.Rect(bar with { X = bar.X + bar.Width - border, Width = border }, edge);
        }

        canvas.Text("Settings", new(bar.X + 14 * s, bar.Y + 2 * s, bar.Width - 86 * s, bar.Height - 4 * s), 16, SettingsCanvas.LightInk);

        var restore = new UiRect(MathF.Round(bar.X + bar.Width - 68 * s), MathF.Round(bar.Y + 4 * s), MathF.Round(28 * s), MathF.Round(28 * s));
        DrawRestoreButton(restore);

        var close = new UiRect(MathF.Round(bar.X + bar.Width - 34 * s), MathF.Round(bar.Y + 4 * s), MathF.Round(28 * s), MathF.Round(28 * s));
        menu.Controls.Add(new("settings-close", "Button", close, "", "settings:close", "Kenshi_CloseButtonSkin", true,
            new Dictionary<string, string>(), Vector4.Zero));
        menu.Hits.Add(new(close, () => menu.Visible = false, Label: "Close"));
    }

    /// <summary>
    /// The restore button on the minimized bar: a small window-outline glyph.
    /// </summary>
    void DrawRestoreButton(UiRect at)
    {
        var canvas = menu.Canvas;
        float s = menu.Scale;
        bool hovered = at.Contains(menu.Pointer.X, menu.Pointer.Y);

        canvas.Surface(at, "button", hovered && menu.PointerDown, hovered);

        float boxW = MathF.Round(12 * s), boxH = MathF.Round(10 * s);
        float border = MathF.Max(1, MathF.Round(1.5f * s));
        var box = new UiRect(MathF.Round(at.X + (at.Width - boxW) / 2), MathF.Round(at.Y + (at.Height - boxH) / 2), boxW, boxH);
        var ink = hovered ? SettingsCanvas.LightInk : SettingsCanvas.MainInk;

        canvas.Rect(box with { Height = MathF.Max(2, MathF.Round(2.5f * s)) }, ink);
        canvas.Rect(box with { Y = box.Y + box.Height - border, Height = border }, ink);
        canvas.Rect(box with { Width = border }, ink);
        canvas.Rect(box with { X = box.X + box.Width - border, Width = border }, ink);

        menu.Hits.Add(new(at, () => SetMinimized(false), Label: "Restore"));
    }

    #endregion
}
