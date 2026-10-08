using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    // The characters' own pixels for the post chain's SSAO (PostProcess.ObjectMask): the occlusion is kept at SsaoCharacterStrength there.

    bool maskHooked, maskCamera;
    Matrix4x4 maskViewProjection;
    Vector3 maskEye;
    Vector4[] maskFrustum = [];
    uint maskDepth;
    (SampledTexture Texture, uint Index)? maskDepthIndex;

    /// <summary>
    /// The near slice's camera for <see cref="DrawMask"/> (this frame's view-projection as drawn, jittered), and the hook into <paramref name="post"/> (once).
    /// Works with and without a temporal upscaler: the mask is at the render size, like the near depth it is matched against.
    /// </summary>
    public void AttachMask(PostProcess? post, Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum)
    {
        if (post is null) return;
        if (!maskHooked)
        {
            maskHooked = true;
            post.ObjectMask = DrawMask;
        }
        (maskViewProjection, maskEye, maskFrustum, maskCamera) = (viewProjection, eye, frustum, true);
    }

    /// <summary>Draws 1 into the mask where a character is what the near depth shows (<see cref="CharacterShaders.MaskFragment"/>); LOD levels as in the colour pass, so the depths agree.</summary>
    void DrawMask(PostProcess.MotionTargets targets)
    {
        if (!maskCamera) return;
        maskCamera = false;
        if (liveCount == 0) return;
        if (maskDepthIndex is { } known && known.Texture == targets.NearDepth) maskDepth = known.Index;
        else
        {
            if (maskDepthIndex is { } old) Gpu.Bindless.Free(BindlessKind.Texture2D, old.Index);
            maskDepthIndex = (targets.NearDepth, maskDepth = Gpu.Bindless.Register(BindlessKind.Texture2D, targets.NearDepth));
        }
        maskPass = true;
        try
        {
            DrawView(new ViewConstants { ViewProjection = maskViewProjection, Eye = maskEye, NearPlanes = targets.NearPlanes, JitterNdc = targets.JitterNdc }, maskEye, maskFrustum);
        }
        finally { maskPass = false; }
    }
}
