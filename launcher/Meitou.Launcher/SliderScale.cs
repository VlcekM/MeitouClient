using Meitou.Settings;

namespace Meitou.Launcher;

/// <summary>
/// A continuous setting's slider: its step, whether it is logarithmic, and the mapping between the setting's value and the slider's position.
/// </summary>
readonly struct SliderScale
{
    /// <summary>
    /// The setting the slider shows.
    /// </summary>
    readonly SettingDescription _s;

    /// <summary>
    /// Whether the slider moves on the logarithm of the value (a logarithmic setting with a positive minimum).
    /// </summary>
    public bool Log { get; }

    /// <summary>
    /// The value's step (1 where the setting gives none).
    /// </summary>
    public float Step { get; }

    /// <summary>
    /// The scale of <paramref name="s"/>'s slider.
    /// </summary>
    public SliderScale(SettingDescription s)
    {
        _s = s;
        Log = s.Logarithmic && s.Min > 0;
        Step = s.Step > 0 ? s.Step : 1;
    }

    /// <summary>
    /// A reversed setting's value is mirrored onto the slider (Min + Max - value; on a logarithmic one Min x Max / value), so right is always the
    /// better end.
    /// </summary>
    float Mirror(float v) => !_s.Reversed ? v : Log ? _s.Min * _s.Max / v : _s.Min + _s.Max - v;

    /// <summary>
    /// The slider's position for a value.
    /// </summary>
    public float ToSlider(float v) => Log ? MathF.Log(Mirror(v)) : Mirror(v);

    /// <summary>
    /// A value on the setting's step, within its range.
    /// </summary>
    public float Snap(float v) => Math.Clamp(MathF.Round(v / Step) * Step, _s.Min, _s.Max);

    /// <summary>
    /// The value at a slider position, on the step.
    /// </summary>
    public float FromSlider(double x) => Snap(Mirror(Log ? MathF.Exp((float)x) : (float)x));
}
