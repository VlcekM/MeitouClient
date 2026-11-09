using Meitou.Game.Audio;
using Meitou.Game.Rendering.Frame;

namespace Meitou.ModelViewer;

static partial class WorldApp
{
    /// <summary>
    /// Feeds the ambience and the music the frame's state the way the game's camera update does (docs/audio.md): the region under the
    /// camera's target (the game's pivot), the hour against sunrise and sunset, the eye's height above the ground, and the weather's rain and
    /// wind.
    /// </summary>
    static void UpdateAudio(AudioSystem audio, WorldGpu gpu, WorldScene scene, WorldCamera camera, float hour, double realSeconds)
    {
        var target = camera.Target;
        var eye = camera.Eye;
        var weather = gpu.Weather;
        audio.Update(weather?.World.RegionAt(target.X, target.Z), weather?.State, hour, scene.Clock.Sunrise, scene.Clock.Sunset,
            eye.Y - gpu.Terrain.HeightAt(eye.X, eye.Z), realSeconds);
    }
}
