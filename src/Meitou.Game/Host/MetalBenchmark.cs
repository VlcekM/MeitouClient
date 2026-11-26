#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using System.Globalization;
using Meitou.Game.DevTools.Benchmark;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Settings;

namespace Meitou.Game.Host;

/// <summary>
/// What the launcher benchmark (<c>--benchmark</c>, docs/bench.md "Launcher benchmark") sets on the Metal loop so a Mac's result does not depend
/// on its display: the game runs in a full-screen Space (a window is held to the display's refresh rate whatever vsync says), the scene and the
/// interface are drawn at 1920x1080 pixels whatever the screen's size, Retina or not, with no upscaler, and the tier goes over the settings the
/// Metal panel has.
/// </summary>
static class MetalBenchmark
{
    /// <summary>
    /// Makes the Disabled upscaler draw the scene and the interface at <see cref="WorldOptions.BenchmarkBaselineSize"/>, stretched to the screen
    /// with its aspect ratio kept, and the game start in its full-screen Space.
    /// </summary>
    internal static void UseFixedResolution(MetalUpscaling upscaling, UserConfig config)
    {
        upscaling.FixedSize = WorldOptions.BenchmarkBaselineSize;
        upscaling.Update();
        config.MetalUiAtScreenResolution = false;
        config.FullScreen = true;
    }

    /// <summary>
    /// Applies tier <paramref name="tier"/> to the Metal settings (<see cref="QualityTiers.ApplyBenchmarkTier"/>: the sliders and switches the
    /// Metal panel has) and returns the hash of what is set, for result.json's <c>applied</c>.
    /// </summary>
    internal static string ApplyTier(SettingsMenu settings, SettingsPanel values, int tier)
    {
        settings.Tiers.ApplyBenchmarkTier(tier);
        var sliders = values.Sliders.Select(s => (s.Label, s.Get().ToString("R", CultureInfo.InvariantCulture)));
        var switches = values.Toggles.Select(t => (SettingsMenu.SwitchKey(t.Label), t.Get() ? "on" : "off"));
        return BenchmarkSpec.AppliedHash(sliders.Concat(switches));
    }
}
#endif
