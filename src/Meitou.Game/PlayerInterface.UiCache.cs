using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The arranged interface's base (the HUD, the squad bar, the HUD text and the open screens) kept across frames while nothing it is made from changed:
/// the shown snapshot, the mouse, the window, the mode, the open screens, the speed, the modifiers and any input since. What moves with the camera or
/// real time (name tags, floaters, speech, the notice, the hover label) is added every frame on top (<see cref="ArrangeUi"/>).
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The most a base is reused (real seconds), in case something it reads changed without any of the key's parts.</summary>
    const double UiReuseSeconds = 0.05;

    readonly record struct UiBaseKey(WorldSnapshot Snapshot, Vector2 Mouse, int Width, int Height, UiMode Mode, Meitou.Localisation.GameLanguage Language,
        int Input, int Town, int? Tutorial, double Speed, bool Paused, bool Hud, bool Ctrl, bool Alt, bool ShotWithoutMenus);

    /// <summary>Counts the input the interface took (keys, clicks, text, the wheel): any of it can change what the base shows.</summary>
    internal int uiInput;
    UiBaseKey? uiBaseKey;
    string[] uiBaseScreens = [];
    List<UiControl> uiBase = [];
    long uiBaseAt;

    /// <summary>
    /// A copy of the base for this frame (the caller adds to it), made again when its key changed,
    /// something in it animates, or it is <see cref="UiReuseSeconds"/> old.
    /// </summary>
    List<UiControl> ArrangedBase(bool hud)
    {
        var key = BaseKey(hud);
        var screens = faithfulScreens.OpenScreens.ToArray();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool fresh = uiBaseKey is { } kept && kept == key && screens.AsSpan().SequenceEqual(uiBaseScreens) && !UiAnimating
            && System.Diagnostics.Stopwatch.GetElapsedTime(uiBaseAt, now).TotalSeconds < UiReuseSeconds;
        if (!fresh)
        {
            uiBase = ArrangeBase(hud);
            (uiBaseKey, uiBaseScreens, uiBaseAt) = (key, faithfulScreens.OpenScreens.ToArray(), now);
        }

        return [.. uiBase];
    }

    UiBaseKey BaseKey(bool hud) => new(session.CurrentSnapshot, mouse, Width, Height, UiMode, Meitou.Localisation.Loc.Language, uiInput, townPanel.shownTown,
        tutorialWindow.tutorialShown, session.Simulation.Speed, session.Simulation.IsPaused, hud, CtrlHeld, AltHeld, feedbackPanel.ShotWithoutMenus);

    /// <summary>The base shows something that moves on real time or with the camera: it is arranged every frame.</summary>
    bool UiAnimating => TitleOpen || characterEditor.EditorOpen || frontend.showCredits || biomeBanner.biomeShown || worldMap.mapShown || portraitBar.portraitFlashes.Count > 0 || tutorialWindow.tutorialFlashes.Count > 0
        || faithfulScreens.CapturesPointer;
}
