using System.Numerics;

namespace Meitou.Rendering;

/// <summary>A world-space sphere supplied by a presentation producer after posing. It is not scaled or shifted by the character's bind height.</summary>
public readonly record struct CharacterWorldBounds
{
    public Vector3 Centre { get; }
    public float Radius { get; }
    /// <summary>The default struct is invalid and falls back to ordinary character bounds.</summary>
    public bool IsValid => float.IsFinite(Centre.X) && float.IsFinite(Centre.Y) && float.IsFinite(Centre.Z) && float.IsFinite(Radius) && Radius > 0;
    public CharacterWorldBounds(Vector3 centre, float radius)
    {
        if (!float.IsFinite(centre.X) || !float.IsFinite(centre.Y) || !float.IsFinite(centre.Z)) throw new ArgumentOutOfRangeException(nameof(centre));
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        (Centre, Radius) = (centre, radius);
    }
}
