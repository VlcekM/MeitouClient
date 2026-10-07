using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>Set by the host when the temporal upscalers run: the palettes of last frame are kept for <see cref="DrawMotion"/>.</summary>
    public bool Motion { get; set; }
    bool motionHooked;

    // The characters' motion for the temporal upscalers (PostProcess.ObjectMotion).

    Matrix4x4 motionViewProjection, motionUnjittered, previousUnjittered;
    Vector4[] motionFrustum = [];
    Vector3 motionEye;
    bool motionCamera, havePreviousCamera;
    int motionCalls;
    uint motionDepth;
    (SampledTexture Texture, uint Index)? motionDepthIndex;

    /// <summary>
    /// The near slice's frame as the post chain's motion pass needs it (a no-op without a temporal upscaler): hooks the characters' motion into
    /// <paramref name="post"/>'s one object-motion callback beside the grass's (once), and gives <see cref="DrawMotion"/> this frame's camera.
    /// </summary>
    public void AttachMotion(PostProcess? post, FoliageRenderer? foliage, Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye, Vector4[] frustum)
    {
        if (post is not { Temporal: true }) return;
        if (!motionHooked)
        {
            motionHooked = true;
            post.ObjectMotion = t => { foliage?.DrawGrassMotion(t); DrawMotion(t); };
        }
        SetMotionCamera(viewProjection, unjittered, eye, frustum);
    }

    /// <summary>The near slice's camera for <see cref="DrawMotion"/> (the grass's <c>SetMotionCamera</c>): this frame's view-projection as drawn (jittered) and unjittered.</summary>
    void SetMotionCamera(Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector3 eye, Vector4[] frustum)
    {
        (motionViewProjection, motionUnjittered, motionEye, motionFrustum) = (viewProjection, unjittered, eye, frustum);
        motionCamera = true;
        Motion = true;
    }

    /// <summary>
    /// The characters' own motion over the camera's, for the upscalers: the near slice's characters drawn with this and last frame's palettes and
    /// transforms into the motion target (red and green), where they are what the near depth shows. Called inside the post chain's object-motion pass.
    /// </summary>
    void DrawMotion(PostProcess.MotionTargets targets)
    {
        if (!motionCamera) return;
        motionCamera = false;
        var previous = havePreviousCamera ? previousUnjittered : motionUnjittered;
        (previousUnjittered, havePreviousCamera) = (motionUnjittered, true);
        if (liveCount == 0 || !CharacterSwitches.MotionVectors) return;
        motionCalls++;
        if (motionDepthIndex is { } known && known.Texture == targets.NearDepth) motionDepth = known.Index;
        else
        {
            if (motionDepthIndex is { } old) Gpu.Bindless.Free(BindlessKind.Texture2D, old.Index);
            motionDepthIndex = (targets.NearDepth, motionDepth = Gpu.Bindless.Register(BindlessKind.Texture2D, targets.NearDepth));
        }
        motionPass = true;
        try
        {
            DrawView(new ViewConstants { ViewProjection = motionViewProjection, PreviousViewProjection = previous, Eye = motionEye, NearPlanes = targets.NearPlanes, JitterNdc = targets.JitterNdc }, motionEye, motionFrustum);
        }
        finally { motionPass = false; }
    }
}
