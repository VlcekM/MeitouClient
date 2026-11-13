#if MEITOU_METAL
// Metal exists on macOS only; the file is compiled only there (the csproj's MEITOU_METAL constant).
#pragma warning disable CA1416
using Meitou.Game.Host;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game;

/// <summary>
/// The host's Metal side (macOS): the Metal loop of the run, and its renderers as the host's own.
/// </summary>
sealed partial class GameHost
{
    /// <summary>
    /// The Metal loop of this run (null until <see cref="Metal"/> starts it).
    /// </summary>
    MetalGameLoop? _metal;

    /// <summary>
    /// The Metal path (macOS): the game boots without the Vulkan renderer and draws the world through the seam's recording model; the loop is
    /// <see cref="MetalGameLoop"/> (docs/metal-backend.md).
    /// </summary>
    int Metal()
    {
        MetalRenderer = true;
        // The banks and the output device load on a worker while the window, the world and the renderers are made; the loop reads the world on
        // another behind its loading screen.
        Sound.BeginAudio();
        _metal = new MetalGameLoop(this, start);
        return _metal.Run();
    }

    /// <inheritdoc/>
    partial void OnWorldSettled() => _metal?.WorldSettled();

    /// <summary>
    /// The Metal loop's weather, lamps, objects and foliage, kept as the host's own (<see cref="ActiveWeather"/> and the rest).
    /// </summary>
    internal void KeepMetalRenderers(WorldWeather? weather, WorldLamps? lamps, WorldObjectRenderer? objects, FoliageRenderer? foliage)
    {
        _fallbackWeather = weather;
        _hostLamps = lamps;
        _hostObjects = objects;
        _hostFoliage = foliage;
    }

    /// <summary>
    /// The Metal loop's CPU height grid, kept as the host's ground (<see cref="ActiveGround"/>).
    /// </summary>
    internal void KeepMetalGround(Func<float, float, float> ground) => _hostGround = ground;

    /// <summary>
    /// The Metal loop's character renderer, kept as the host's own (<see cref="ActiveCharacters"/>).
    /// </summary>
    internal void KeepMetalCharacters(CharacterRenderer? characters) => _hostCharacters = characters;
}
#endif
