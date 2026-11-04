using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>The Display page's render scale: a choice of the upscalers' quality modes, with the free slider under Custom.</summary>
sealed partial class SettingsMenu
{
    /// <summary>The quality modes, lowest scale first (FSR's and DLSS's ratios; Ultra quality is the Meitou default), then Custom.</summary>
    static readonly (string Name, float Scale)[] RenderScaleModes =
    [
        ("Ultra performance", 1 / 3f), ("Performance", 0.5f), ("Normal", 1 / 1.7f), ("Quality", 1 / 1.5f),
        ("Ultra quality (0.83)", UpscaleOptions.MeitouDefaultScale), ("DLAA", 1),
    ];

    /// <summary>Custom was picked: the slider stays shown even when its value matches a mode.</summary>
    bool customRenderScale;

    /// <summary>The render scale rows replacing the slider's own row: the mode choice, and the slider itself under Custom.</summary>
    IEnumerable<Entry> RenderScaleEntries(Entry sliderRow)
    {
        var slider = sliderRow.Slider!;
        string[] names = [.. RenderScaleModes.Select(m => m.Name), "Custom"];
        yield return sliderRow with
        {
            Slider = null,
            Choice = new(() => names, () => RenderScaleMode(slider), i => ChooseRenderScale(slider, i)),
        };
        if (RenderScaleMode(slider) == RenderScaleModes.Length) yield return sliderRow with { Label = "Custom scale", Depth = 1 };
    }

    int RenderScaleMode(Slider slider)
    {
        if (customRenderScale) return RenderScaleModes.Length;
        float value = slider.Get();
        int mode = Array.FindIndex(RenderScaleModes, m => MathF.Abs(MathF.Round(m.Scale * 100) / 100 - value) < 0.005f);
        return mode < 0 ? RenderScaleModes.Length : mode;
    }

    void ChooseRenderScale(Slider slider, int index)
    {
        customRenderScale = index >= RenderScaleModes.Length;
        if (!customRenderScale) slider.Set(RenderScaleModes[index].Scale);
    }
}
