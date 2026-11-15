using System.Diagnostics;

namespace Meitou.Game.Ui;

/// <summary>
/// The popup fades of <c>SmoothShow</c> combo boxes (Verified against MyGUI 3.2.2 <c>ComboBox::showList</c> / <c>hideList</c> and
/// <c>ControllerFadeAlpha</c>; docs/formats/mygui.md "Widget runtime"): a list fades in from alpha 0 and out to 0 by 4 a second, from the alpha it
/// has. Fading out it takes no input and is hidden at 0, when it takes input again; opened again before that, the fade-out never ends, so the list
/// keeps refusing input until a later fade-out ends (MyGUI drops the replaced controller without its end handler).
/// </summary>
sealed class ComboListFade
{
    /// <summary>
    /// The alpha change a second (<c>COMBO_ALPHA_COEF</c>).
    /// </summary>
    const float Coefficient = 4;

    /// <summary>
    /// One list's fade: the alpha it started from, when (Stopwatch), whether it fades in, and whether the list refuses input (a fade-out started and
    /// has not ended).
    /// </summary>
    sealed record Fade(float From, long Start, bool Showing, bool Disabled)
    {
        /// <summary>
        /// The alpha now, clamped to 0..1.
        /// </summary>
        public float Alpha()
        {
            float travelled = (float)Stopwatch.GetElapsedTime(Start).TotalSeconds * Coefficient;
            return Math.Clamp(Showing ? From + travelled : From - travelled, 0, 1);
        }
    }

    /// <summary>
    /// The lists that fade or were shown by a fade, by screen and combo box.
    /// </summary>
    readonly Dictionary<(string Screen, string Combo), Fade> _fades = [];

    /// <summary>
    /// Fades the list of <paramref name="combo"/> in from the alpha it shows (0 when hidden).
    /// </summary>
    public void Show((string Screen, string Combo) combo)
    {
        var old = _fades.GetValueOrDefault(combo);
        _fades[combo] = new(old?.Alpha() ?? 0, Stopwatch.GetTimestamp(), true, old?.Disabled ?? false);
    }

    /// <summary>
    /// Fades the list of <paramref name="combo"/> out from the alpha it shows; it refuses input from now on.
    /// </summary>
    public void Hide((string Screen, string Combo) combo)
    {
        float from = _fades.GetValueOrDefault(combo)?.Alpha() ?? 1;
        _fades[combo] = new(from, Stopwatch.GetTimestamp(), false, true);
    }

    /// <summary>
    /// Drops the fades of screen <paramref name="screen"/> (the screen closed).
    /// </summary>
    public void Forget(string screen)
    {
        foreach (var key in _fades.Keys.Where(k => k.Screen == screen).ToArray()) _fades.Remove(key);
    }

    /// <summary>
    /// The lists shown on <paramref name="screen"/> with their alpha and whether they take input: the open one (<paramref name="open"/>) and those
    /// still fading out. A fade-out that reached 0 ends here: the list is hidden and takes input again.
    /// </summary>
    public IEnumerable<(string Combo, float Alpha, bool Enabled)> Shown(string screen, (string Screen, string Combo)? open)
    {
        if (open is { } o && o.Screen == screen)
        {
            var fade = _fades.GetValueOrDefault(o);
            yield return (o.Combo, fade?.Alpha() ?? 1, fade is not { Disabled: true });
        }

        foreach (var (key, fade) in _fades.Where(f => f.Key.Screen == screen && !f.Value.Showing).ToArray())
        {
            if (key == open) continue;

            float alpha = fade.Alpha();
            if (alpha > 0) yield return (key.Combo, alpha, false);
            else _fades.Remove(key);
        }
    }
}
