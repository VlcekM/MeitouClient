using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The rooms seen through the floor view's cut (docs/render-post.md "Rooms' exposure"): <see cref="Gi.RoomMaskPass"/> over the scene, for the
/// exposure's measure and the rooms' gain (<see cref="MergedKernels"/>).
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>
    /// The trace, made when first used.
    /// </summary>
    Gi.RoomMaskPass? _roomMaskPass;

    /// <summary>
    /// 1 where a pixel is a room's, at the render size.
    /// </summary>
    Target2D? _roomMask;

    /// <summary>
    /// The frame <see cref="_roomMask"/> was traced in.
    /// </summary>
    long _roomMaskFrame = -1;

    /// <summary>
    /// This frame's rooms, or null when none were looked for (no interior in view, no traced scene).
    /// </summary>
    Target2D? RoomMaskNow => _roomMaskFrame == _frameIndex ? _roomMask : null;

    /// <summary>
    /// Finds this frame's rooms while an interior is in view (<see cref="IndoorExposure"/>). <paramref name="scene"/> must hold this frame's
    /// traced scene. <paramref name="eyeIndoors"/>: the eye is inside a viewed building (<c>WorldObjectRenderer.EyeIndoors</c>).
    /// </summary>
    internal void RunRoomMask(Gi.GiScene? scene, bool eyeIndoors)
    {
        var (depth, surface) = (_sceneDepth, _giAlbedo);
        if (IndoorExposure <= 0 || _exposureHold is not null || !MergeExposure || !MergeSupported || !_haveScene || depth is null || surface is null
            || scene?.Top is null || !SupportsStorage(InternalFormat.R32F)) return;

        MarkScene();
        if (_roomMask is null)
        {
            using var batch = Gpu.Uploads.Begin();
            _roomMask = Make(batch, _width, _height, InternalFormat.R32F, TextureMinFilter.Nearest, "post room mask");
        }
        CloseSegment();

        var pass = _roomMaskPass ??= new Gi.RoomMaskPass(Gpu);
        float pixelSlope = 2 * MathF.Tan(_fovNow * 0.5f) / _height;
        pass.Run(scene, depth.Texture, surface.Texture, _roomMask.Texture, _width, _height, SceneToEye, _eyeNow, pixelSlope, eyeIndoors);
        _roomMaskFrame = _frameIndex;
        Stamp("room mask");
        CloseSegment();
    }
}
