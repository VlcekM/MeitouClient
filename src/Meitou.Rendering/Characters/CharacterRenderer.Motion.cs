using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>Set by the host when the temporal upscalers run: the palettes of last frame are kept for <see cref="DrawMotion"/>.</summary>
    public bool Motion { get; set; }
    bool motionHooked;

    // The characters' motion for the temporal upscalers (PostProcess.ObjectMotion).

    Matrix4x4 motionViewProjection, motionUnjittered, previousUnjittered, motionRelative, motionRelativeUnjittered, previousRelativeUnjittered;
    Vector3 previousEye;
    bool motionHasRelative;
    Vector4[] motionFrustum = [];
    Vector3 motionEye;
    bool motionCamera, havePreviousCamera;
    int motionCalls;
    uint motionDepth;
    (SampledTexture Texture, uint Index)? motionDepthIndex;

    /// <summary>
    /// The scene frame as the post chain's motion pass needs it (a no-op without a temporal upscaler): hooks the characters' motion into
    /// <paramref name="post"/>'s one object-motion callback beside the grass's (once), and gives <see cref="DrawMotion"/> this frame's camera.
    /// </summary>
    public void AttachMotion(IWorldPostChain? post, FoliageRenderer? foliage, Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye, Vector4[] frustum,
        Matrix4x4? relative = null, Matrix4x4? relativeUnjittered = null)
    {
        if (post is not { Temporal: true }) return;
        if (!motionHooked)
        {
            motionHooked = true;
            post.ObjectMotion = t => { foliage?.DrawGrassMotion(t); DrawMotion(t); };
        }
        SetMotionCamera(viewProjection, unjittered, eye, frustum);
        motionHasRelative = relative is not null && relativeUnjittered is not null;
        if (motionHasRelative) (motionRelative, motionRelativeUnjittered) = (relative!.Value, relativeUnjittered!.Value);
    }

    /// <summary>The scene camera for <see cref="DrawMotion"/> (the grass's <c>SetMotionCamera</c>): this frame's view-projection as drawn (jittered) and unjittered.</summary>
    void SetMotionCamera(Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye, Vector4[] frustum)
    {
        (motionViewProjection, motionUnjittered, motionEye, motionFrustum) = (viewProjection, unjittered, eye, frustum);
        motionCamera = true;
        Motion = true;
    }

    /// <summary>
    /// The characters' own motion over the camera's, for the upscalers: the scene's characters drawn with this and last frame's palettes and
    /// transforms into the motion target (red and green), where they are what the scene depth shows. Called inside the post chain's object-motion pass.
    /// </summary>
    void DrawMotion(MotionTargets targets)
    {
        if (!motionCamera) return;
        motionCamera = false;
        var previous = havePreviousCamera ? previousUnjittered : motionUnjittered;
        // Last frame's camera for positions relative to this frame's eye: its rotation-only matrix after the move from this eye to last frame's (exact in double: both
        // eyes are floats), so the velocity carries no rounding of either eye's coordinates.
        Matrix4x4? previousRelative = null;
        if (motionHasRelative)
        {
            var move = havePreviousCamera ? new Vector3((float)((double)motionEye.X - previousEye.X), (float)((double)motionEye.Y - previousEye.Y), (float)((double)motionEye.Z - previousEye.Z)) : Vector3.Zero;
            previousRelative = Matrix4x4.CreateTranslation(move) * (havePreviousCamera ? previousRelativeUnjittered : motionRelativeUnjittered);
            (previousRelativeUnjittered, previousEye) = (motionRelativeUnjittered, motionEye);
        }
        (previousUnjittered, havePreviousCamera) = (motionUnjittered, true);
        if (liveCount == 0) return;
        motionCalls++;
        if (motionDepthIndex is { } known && known.Texture == targets.Depth) motionDepth = known.Index;
        else
        {
            if (motionDepthIndex is { } old) Gpu.Bindless.Free(BindlessKind.Texture2D, old.Index);
            motionDepthIndex = (targets.Depth, motionDepth = Gpu.Bindless.Register(BindlessKind.Texture2D, targets.Depth));
        }
        motionPass = true;
        // This draw is the "characters" stage's, not the post chain's: the lap before ends the post time so far, the one after is the characters'.
        StageClock.Lap(StageClock.Post, "post to motion");
        try
        {
            DrawView(new ViewConstants { ViewProjection = motionViewProjection, PreviousViewProjection = previous, Eye = motionEye, NearPlanes = targets.Planes, JitterNdc = targets.JitterNdc }, motionEye, motionFrustum,
                relative: motionHasRelative ? motionRelative : null, previousRelative: previousRelative);
        }
        finally { motionPass = false; }
        StageClock.Lap(StageClock.Characters, "characters motion");
    }
}
