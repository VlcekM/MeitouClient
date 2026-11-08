namespace Meitou.Rendering;

/// <summary>
/// The shadow range's cap from a placed fog block (<see cref="FogVolumes.ShadowHideDistance"/>), steadied against the hide radius moving with the eye
/// (docs/render-shadows.md "Range capped by the placed fog"). The radius is padded by <see cref="Margin"/> (the receiver fades its cascades out from 0.85
/// of the range), rounded up on a geometric grid, raised at once when it grows and lowered only when it has fallen three grid steps. A frame without a
/// radius keeps the cap while the radius is pending (<see cref="FogVolumes.ShadowHidePending"/>: the eye is in the block and the direction bound is being
/// solved on a background task, which every 300 units of the eye's travel takes a few frames; releasing then would redraw every cascade twice), and for
/// <see cref="HoldFrames"/> otherwise (the eye has left the block or the view no longer sees only fog). A cap that moves redraws cascades (the splits
/// follow it), so it should move rarely.
/// </summary>
internal sealed class ShadowFogCap
{
    /// <summary>The cap is the hide radius times this: the receiver's shadow term fades to lit from 0.85 of the range, and the fog's alpha reaches 0.9998 only at the radius itself.</summary>
    public const float Margin = 1.1f;
    /// <summary>Ratio of neighbouring caps.</summary>
    public const float Step = 1.06f;
    /// <summary>The lowest cap (the first split of a view is far under it).</summary>
    public const float Floor = 1000f;
    /// <summary>Grid steps the radius must fall before the cap is lowered.</summary>
    public const int LowerSteps = 3;
    /// <summary>Frames without a radius (none pending) before the cap is released.</summary>
    public const int HoldFrames = 12;
    /// <summary>Frames a pending radius is waited for at most (a solve is a few frames; this only guards against one that never lands).</summary>
    public const int PendingFrames = 300;

    float? applied;
    int missing, pending;

    /// <summary>The cap now (null: none).</summary>
    public float? Current => applied;

    /// <summary>The grid value at or above <paramref name="distance"/> (times <see cref="Margin"/>).</summary>
    public static float Quantise(float distance)
    {
        float padded = distance * Margin;
        if (padded <= Floor) return Floor;
        int k = (int)MathF.Ceiling(MathF.Log(padded / Floor) / MathF.Log(Step) - 1e-3f);
        return Floor * MathF.Pow(Step, k);
    }

    /// <summary>Takes this frame's hide radius (null: none) and whether one is pending, and returns the cap to use.</summary>
    public float? Update(float? hide, bool isPending = false)
    {
        if (hide is not { } h || !(h > 0))
        {
            if (isPending) { if (++pending > PendingFrames) applied = null; }
            else { pending = 0; if (++missing > HoldFrames) applied = null; }
            return applied;
        }
        missing = pending = 0;
        float wanted = Quantise(h);
        if (applied is not { } now || wanted > now || wanted * MathF.Pow(Step, LowerSteps) <= now * 1.0001f) applied = wanted;
        return applied;
    }

    public void Reset() { applied = null; missing = pending = 0; }
}
