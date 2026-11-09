using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Shadows;

/// <summary>
/// The shadow range's cap from a placed fog block (<see cref="FogVolumes.ShadowHideDistance"/>), steadied against the hide radius moving with the eye
/// (docs/render-shadows.md "Range capped by the placed fog"). The radius is padded by <see cref="Margin"/> (the receiver fades its cascades out from
/// 0.85 of the range), rounded up on a geometric grid, raised at once when it grows and lowered only when it has fallen three grid steps. A frame
/// without a radius keeps the cap while the radius is pending (<see cref="FogVolumes.ShadowHidePending"/>: the eye is in the block and the direction
/// bound is being solved on a background task, which every 300 units of the eye's travel takes a few frames; releasing then would redraw every
/// cascade twice), and for
/// <see cref="HoldFrames"/> otherwise (the eye has left the block or the view no longer sees only fog). A cap that moves redraws cascades (the splits
/// follow it), so it should move rarely.
/// </summary>
internal sealed class ShadowFogCap
{
    /// <summary>
    /// The cap is the hide radius times this: the receiver's shadow term fades to lit from 0.85 of the range, and the fog's alpha reaches 0.9998 only
    /// at the radius itself.
    /// </summary>
    public const float Margin = 1.1f;

    /// <summary>
    /// Ratio of neighbouring caps.
    /// </summary>
    public const float Step = 1.06f;

    /// <summary>
    /// The lowest cap (the first split of a view is far under it).
    /// </summary>
    public const float Floor = 1000f;

    /// <summary>
    /// Grid steps the radius must fall before the cap is lowered.
    /// </summary>
    public const int LowerSteps = 3;

    /// <summary>
    /// Frames without a radius (none pending) before the cap is released.
    /// </summary>
    public const int HoldFrames = 12;

    /// <summary>
    /// Frames a pending radius is waited for at most (a solve is a few frames; this only guards against one that never lands).
    /// </summary>
    public const int PendingFrames = 300;

    /// <summary>
    /// The cap in effect (null: none).
    /// </summary>
    float? _applied;

    /// <summary>
    /// The frames without a radius, and the frames a pending radius has been waited for.
    /// </summary>
    int _missing, _pending;

    /// <summary>
    /// The cap now (null: none).
    /// </summary>
    public float? Current => _applied;

    /// <summary>
    /// The grid value at or above <paramref name="distance"/> (times <see cref="Margin"/>).
    /// </summary>
    public static float Quantise(float distance)
    {
        float padded = distance * Margin;
        if (padded <= Floor) return Floor;
        int k = (int)MathF.Ceiling(MathF.Log(padded / Floor) / MathF.Log(Step) - 1e-3f);
        return Floor * MathF.Pow(Step, k);
    }

    /// <summary>
    /// Takes this frame's hide radius (null: none) and whether one is pending, and returns the cap to use.
    /// </summary>
    public float? Update(float? hide, bool isPending = false)
    {
        if (hide is not { } h || !(h > 0))
        {
            ReleaseWithoutRadius(isPending);
            return _applied;
        }
        _missing = _pending = 0;
        float wanted = Quantise(h);
        if (_applied is not { } now || wanted > now || wanted * MathF.Pow(Step, LowerSteps) <= now * 1.0001f) _applied = wanted;
        return _applied;
    }

    /// <summary>
    /// Drops the cap and its counts (the cap switch is off).
    /// </summary>
    public void Reset()
    {
        _applied = null;
        _missing = _pending = 0;
    }

    /// <summary>
    /// Counts a frame without a radius: a pending one is waited for, else the cap is held and then released.
    /// </summary>
    void ReleaseWithoutRadius(bool isPending)
    {
        if (isPending)
        {
            if (++_pending > PendingFrames) _applied = null;
            return;
        }

        _pending = 0;
        if (++_missing > HoldFrames) _applied = null;
    }
}