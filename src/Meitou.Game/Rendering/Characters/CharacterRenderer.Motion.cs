using System.Numerics;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The characters' motion vectors for the temporal upscalers: drawn inside the post chain's object-motion pass with this and last frame's palettes,
/// transforms and cameras.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>
    /// Set by the host when the temporal upscalers run: the palettes of last frame are kept for <see cref="DrawMotion"/>.
    /// </summary>
    public bool Motion { get; set; }
    /// <summary>
    /// Whether the object-motion callback of the post chain has been set (once).
    /// </summary>
    bool _motionHooked;

    // The characters' motion for the temporal upscalers (PostProcess.ObjectMotion).

    /// <summary>
    /// This frame's view-projection as drawn (jittered) and unjittered, last frame's unjittered one, and the same three for positions relative to the
    /// eye (the rotation-only matrices).
    /// </summary>
    Matrix4x4 _motionViewProjection, _motionUnjittered, _previousUnjittered, _motionRelative, _motionRelativeUnjittered, _previousRelativeUnjittered;
    /// <summary>
    /// Last frame's eye, for the move between the relative matrices.
    /// </summary>
    Vector3 _previousEye;
    /// <summary>
    /// Whether this frame's camera came with the relative matrices.
    /// </summary>
    bool _motionHasRelative;
    /// <summary>
    /// This frame's frustum planes for the motion view.
    /// </summary>
    Vector4[] _motionFrustum = [];
    /// <summary>
    /// This frame's eye.
    /// </summary>
    Vector3 _motionEye;
    /// <summary>
    /// <c>_motionCamera</c>: this frame's camera has been given and not yet drawn with; <c>_havePreviousCamera</c>: last frame's matrices are set.
    /// </summary>
    bool _motionCamera, _havePreviousCamera;
    /// <summary>
    /// How many motion passes were drawn, for the statistics.
    /// </summary>
    int _motionCalls;
    /// <summary>
    /// The scene depth texture's bindless index for the motion pass's push constants.
    /// </summary>
    uint _motionDepth;
    /// <summary>
    /// The scene depth texture registered for the motion pass and its bindless index, registered again when the texture changes.
    /// </summary>
    (SampledTexture Texture, uint Index)? _motionDepthIndex;

    /// <summary>
    /// The scene frame as the post chain's motion pass needs it (a no-op without a temporal upscaler): hooks the characters' motion into
    /// <paramref name="post"/>'s one object-motion callback beside the grass's (once), and gives <see cref="DrawMotion"/> this frame's camera.
    /// </summary>
    public void AttachMotion(IWorldPostChain? post, FoliageRenderer? foliage, Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye,
        Vector4[] frustum,
        Matrix4x4? relative = null, Matrix4x4? relativeUnjittered = null)
    {
        if (post is not { Temporal: true }) return;
        if (!_motionHooked)
        {
            _motionHooked = true;
            post.ObjectMotion = t => { foliage?.DrawGrassMotion(t); DrawMotion(t); };
        }
        SetMotionCamera(viewProjection, unjittered, eye, frustum);
        _motionHasRelative = relative is not null && relativeUnjittered is not null;
        if (_motionHasRelative)
            (_motionRelative, _motionRelativeUnjittered) = (relative!.Value, relativeUnjittered!.Value);
    }

    /// <summary>
    /// The scene camera for <see cref="DrawMotion"/> (the grass's <c>SetMotionCamera</c>): this frame's view-projection as drawn (jittered) and
    /// unjittered.
    /// </summary>
    void SetMotionCamera(Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye, Vector4[] frustum)
    {
        (_motionViewProjection, _motionUnjittered, _motionEye, _motionFrustum) = (viewProjection, unjittered, eye, frustum);
        _motionCamera = true;
        Motion = true;
    }

    /// <summary>
    /// The characters' own motion over the camera's, for the upscalers: the scene's characters drawn with this and last frame's palettes and
    /// transforms into the motion target (red and green), where they are what the scene depth shows. Called inside the post chain's object-motion
    /// pass.
    /// </summary>
    void DrawMotion(MotionTargets targets)
    {
        if (!_motionCamera) return;
        _motionCamera = false;
        var previous = _havePreviousCamera ? _previousUnjittered : _motionUnjittered;

        // Last frame's camera for positions relative to this frame's eye: its rotation-only matrix after the move from this eye to last frame's
        // (exact in double: both eyes are floats), so the velocity carries no rounding of either eye's coordinates.
        Matrix4x4? previousRelative = null;
        if (_motionHasRelative)
        {
            var move = _havePreviousCamera ? new Vector3((float)((double)_motionEye.X - _previousEye.X),
                (float)((double)_motionEye.Y - _previousEye.Y), (float)((double)_motionEye.Z - _previousEye.Z)) : Vector3.Zero;
            previousRelative = Matrix4x4.CreateTranslation(move) * (_havePreviousCamera ? _previousRelativeUnjittered : _motionRelativeUnjittered);
            (_previousRelativeUnjittered, _previousEye) = (_motionRelativeUnjittered, _motionEye);
        }
        (_previousUnjittered, _havePreviousCamera) = (_motionUnjittered, true);
        if (_liveCount == 0) return;
        _motionCalls++;
        if (_motionDepthIndex is { } known && known.Texture == targets.Depth) _motionDepth = known.Index;
        else
        {
            if (_motionDepthIndex is { } old) Gpu.Bindless.Free(BindlessKind.Texture2D, old.Index);
            _motionDepthIndex = (targets.Depth, _motionDepth = Gpu.Bindless.Register(BindlessKind.Texture2D, targets.Depth));
        }
        _motionPass = true;

        // This draw is the "characters" stage's, not the post chain's: the lap before ends the post time so far, the one after is the characters'.
        StageClock.Lap(StageClock.Post, "post to motion");
        try
        {
            DrawView(new ViewConstants { ViewProjection = _motionViewProjection, PreviousViewProjection = previous, Eye = _motionEye,
                NearPlanes = targets.Planes, JitterNdc = targets.JitterNdc }, _motionEye, _motionFrustum,
                relative: _motionHasRelative ? _motionRelative : null, previousRelative: previousRelative);
        }
        finally
        {
            _motionPass = false;
        }

        StageClock.Lap(StageClock.Characters, "characters motion");
    }
}