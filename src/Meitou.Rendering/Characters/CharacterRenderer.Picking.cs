using System.Numerics;

namespace Meitou.Rendering.Characters;

/// <summary>
/// A box round part of a mesh for picking, in the bind pose's model space: the vertices whose strongest weight is <see cref="Bone"/> (-1: an unskinned
/// part's whole mesh). Posed by that bone's palette matrix, the boxes follow the character's animation, so a ray finds the body where it is drawn.
/// </summary>
internal readonly record struct PickBox(int Bone, Vector3 Min, Vector3 Max)
{
    /// <summary>The boxes of <paramref name="model"/>'s vertices (any thread).</summary>
    public static PickBox[] From(Model model)
    {
        var boxes = new Dictionary<int, (Vector3 Min, Vector3 Max)>();
        foreach (var part in model.Parts)
            foreach (ref readonly var v in part.Vertices.AsSpan())
            {
                int bone = -1;
                if (part.Skinned)
                {
                    float best = 0;
                    for (int k = 0; k < 4; k++)
                        if (v.Weights[k] > best) (best, bone) = (v.Weights[k], (int)(v.Bones >> (8 * k)) & 0xFF);
                }
                boxes[bone] = boxes.TryGetValue(bone, out var b) ? (Vector3.Min(b.Min, v.Position), Vector3.Max(b.Max, v.Position)) : (v.Position, v.Position);
            }
        return [.. boxes.Select(b => new PickBox(b.Key, b.Value.Min, b.Value.Max))];
    }
}

internal sealed partial class CharacterRenderer
{
    /// <summary>How much a pick box is grown on every side, in its own units (a limb's few vertices make a thin box; animation stretches skin between bones).</summary>
    const float PickPadding = 0.4f;

    /// <summary>
    /// Where the ray (<paramref name="origin"/>, unit <paramref name="direction"/>) first meets the character <paramref name="key"/>'s posed pick boxes (its distance),
    /// <see cref="float.PositiveInfinity"/> when it misses, or null when the character is not drawn in the world this frame (the caller then has nothing to test).
    /// Render thread, after <see cref="Update"/>.
    /// </summary>
    /// <summary>
    /// Where the crossbow in the character <paramref name="key"/>'s hands fires from: the place of its first loaded bolt (the original's muzzle, <c>barrel pos</c> in the crossbow's
    /// frame), in the world, as last posed; null when it holds no crossbow or was not posed. Render thread, after <see cref="Update"/>.
    /// </summary>
    public Vector3? Muzzle(long key)
    {
        if (!states.TryGetValue(key, out var s) || !s.RangedDrawn || s.Asset is not { IsIcon: false } asset || s.Posed < 0) return null;
        foreach (var part in asset.Parts)
            if (part.Muzzle && part.AttachIndex >= 0 && part.AttachIndex < s.Attach.Length) return (s.Attach[part.AttachIndex] * s.World).Translation;
        return null;
    }

    /// <summary>Where the bone <paramref name="bone"/> of the character <paramref name="key"/> is in the world, as last posed; null when it was not drawn in the world this frame or has no such bone. Render thread, after <see cref="Update"/>.</summary>
    public Vector3? BonePosition(long key, string bone)
    {
        if (!states.TryGetValue(key, out var s) || s.Seen != updateStamp || !s.InWorld || s.Asset is not { IsIcon: false } asset || s.Posed < 0) return null;
        if (asset.Rig.Handle(bone) is not int h || h >= s.Bones.Length) return null;
        // The skin matrix carries a point of the binding pose to its posed place: the bone's binding position to where the bone is now.
        return Vector3.Transform(asset.Rig.BindPosition[h], s.Bones[h] * s.World);
    }

    /// <summary>
    /// How the character <paramref name="key"/> was last drawn in the world: its placement, the look and where its <paramref name="bone"/> was; null when it was not drawn in the
    /// world in the last update. Render thread, between updates (a cut-off head starts from it, <see cref="CharacterInstance.FreezeFrom"/>).
    /// </summary>
    public (Matrix4x4 World, Meitou.Data.Characters.CharacterAppearance Look, Vector3 Bone)? LastDrawn(long key, string bone)
    {
        if (!states.TryGetValue(key, out var s) || s.Seen != updateStamp || !s.InWorld || s.Asset is not { IsIcon: false, Appearance: { } look } || BonePosition(key, bone) is not { } at) return null;
        return (s.World, look, at);
    }

    public float? RayHit(long key, Vector3 origin, Vector3 direction)
    {
        if (!states.TryGetValue(key, out var s) || s.Seen != updateStamp || !s.InWorld || s.Asset is not { IsIcon: false } asset || s.Posed < 0) return null;
        // The bounding sphere first: most characters are nowhere near the ray.
        var toCentre = s.Centre - origin;
        float along = Vector3.Dot(toCentre, direction);
        if ((toCentre - along * direction).LengthSquared() > s.Radius * s.Radius * 1.2f) return float.PositiveInfinity;
        float best = float.PositiveInfinity;
        foreach (var part in asset.Parts)
        {
            if (!part.ShowsWith(s.WeaponDrawn, s.RangedDrawn, back: s.BackDrawn) || part.PickBoxes.Length == 0) continue;
            if (!part.Shared && (part.AttachIndex < 0 || part.AttachIndex >= s.Attach.Length)) continue;
            var model = part.Shared ? s.World : s.Attach[part.AttachIndex] * s.World;
            foreach (var box in part.PickBoxes)
            {
                var m = part.Shared && box.Bone >= 0 && box.Bone < s.Bones.Length ? s.Bones[box.Bone] * model : model;
                if (!Matrix4x4.Invert(m, out var inverse)) continue;
                if (Slab(Vector3.Transform(origin, inverse), Vector3.TransformNormal(direction, inverse), box.Min - new Vector3(PickPadding), box.Max + new Vector3(PickPadding)) is { } t && t < best)
                    best = t;
            }
        }
        return best;
    }

    /// <summary>The ray's entry distance into the box (0 from inside), or null; the direction's length carries the world scale, so the distance is the world's.</summary>
    static float? Slab(Vector3 o, Vector3 d, Vector3 min, Vector3 max)
    {
        float near = 0, far = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float origin = o[a], step = d[a], lo = min[a], hi = max[a];
            if (MathF.Abs(step) < 1e-9f)
            {
                if (origin < lo || origin > hi) return null;
                continue;
            }
            float t0 = (lo - origin) / step, t1 = (hi - origin) / step;
            if (t0 > t1) (t0, t1) = (t1, t0);
            near = MathF.Max(near, t0);
            far = MathF.Min(far, t1);
            if (near > far) return null;
        }
        return near;
    }
}
