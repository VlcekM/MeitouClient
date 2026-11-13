using Meitou.Game.Rendering.Water;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// One bone of a posed body as a capsule in world space, for the ground trails: its ends, its radius, the direction its bone points in (zero:
/// none) and its limb (<see cref="CharacterRenderer"/>'s <c>LimbOf</c>: the capsules of one foot, heel and toes, share it).
/// </summary>
public readonly record struct BodyCapsule(Vector3 A, Vector3 B, float Radius, Vector3 Forward, int Limb);

internal sealed partial class CharacterRenderer
{
    /// <summary>
    /// A bone's capsule in the binding pose's model space: round the vertices it moves most (its <see cref="PickBox"/>es, all the skinned parts'
    /// together).
    /// </summary>
    readonly record struct BoneCapsule(int Bone, Vector3 A, Vector3 B, float Radius, int Child, int Limb);

    /// <summary>
    /// A bone's limb reaches up its chain this share of the body's size in the binding pose: a foot takes in its toes, but not the knee, and never
    /// the other leg.
    /// </summary>
    const float LimbShare = 0.17f;

    /// <summary>
    /// A part's bone boxes thinner than this (units) are left out: fingers, toes and the like make no wave of their own.
    /// </summary>
    const float SmallestCapsule = 0.5f;

    /// <summary>
    /// Each asset's bone capsules (<see cref="CapsulesOf"/>), made at the first ask.
    /// </summary>
    readonly ConditionalWeakTable<CharacterAsset, BoneCapsule[]> _boneCapsules = [];

    /// <summary>
    /// The posed body of the character <paramref name="key"/> as capsules (one per skinned bone, round its vertices), those reaching from
    /// <paramref name="below"/> units under <paramref name="water"/> to its surface added to <paramref name="into"/>. Nothing when it was not
    /// drawn in the world in the last update. Render thread, after <see cref="Update"/>.
    /// </summary>
    public void WaterCapsules(long key, float water, float below, List<WaterBody> into)
    {
        if (!_states.TryGetValue(key, out var s) || s.Seen != _updateStamp || !s.InWorld) return;
        if (s.Asset is not { IsIcon: false } asset || s.Posed < 0) return;

        foreach (var c in _boneCapsules.GetValue(asset, CapsulesOf))
        {
            if (c.Bone >= s.Bones.Length) continue;

            var m = s.Bones[c.Bone] * s.World;
            var a = Vector3.Transform(c.A, m);
            var b = Vector3.Transform(c.B, m);
            float r = c.Radius * Vector3.TransformNormal(Vector3.UnitY, m).Length();
            if (MathF.Min(a.Y, b.Y) - r < water && MathF.Max(a.Y, b.Y) + r > water - below) into.Add(new WaterBody(a, b, r));
        }
    }

    /// <summary>
    /// The posed body of the character <paramref name="key"/> as capsules for the ground trails (docs/render-terrain.md "Ground trails"): every
    /// capsule of <see cref="WaterCapsules"/>, in the asset's fixed order (so a capsule keeps its place from frame to frame; one whose bone is not
    /// posed has radius 0), each with the direction its bone points in the skeleton (to its toe for a foot: heel to toe; to its first child bone),
    /// zero for a bone with no child. Nothing added when it was not drawn in the world in the last update. Render thread, after
    /// <see cref="Update"/>.
    /// </summary>
    public void ContactCapsules(long key, List<BodyCapsule> into)
    {
        if (!_states.TryGetValue(key, out var s) || s.Seen != _updateStamp || !s.InWorld) return;
        if (s.Asset is not { IsIcon: false } asset || s.Posed < 0) return;

        foreach (var c in _boneCapsules.GetValue(asset, CapsulesOf))
        {
            if (c.Bone >= s.Bones.Length)
            {
                into.Add(default);
                continue;
            }

            var m = s.Bones[c.Bone] * s.World;
            var forward = Vector3.Zero;
            if (c.Child >= 0 && c.Child < s.Bones.Length)
            {
                var tip = Vector3.Transform(asset.Rig.BindPosition[c.Child], s.Bones[c.Child] * s.World);
                forward = tip - Vector3.Transform(asset.Rig.BindPosition[c.Bone], m);
            }

            var a = Vector3.Transform(c.A, m);
            var b = Vector3.Transform(c.B, m);
            float r = c.Radius * Vector3.TransformNormal(Vector3.UnitY, m).Length();
            into.Add(new BodyCapsule(a, b, r, forward, c.Limb));
        }
    }

    /// <summary>
    /// Each bone's capsule: along its box's longest side, as thick as the mean of the other two.
    /// </summary>
    static BoneCapsule[] CapsulesOf(CharacterAsset asset)
    {
        var boxes = CapsuleBoneBoxes(asset);
        var capsules = new List<BoneCapsule>();
        Vector3 low = new(float.MaxValue), high = new(float.MinValue);
        foreach (var at in asset.Rig.BindPosition) (low, high) = (Vector3.Min(low, at), Vector3.Max(high, at));
        var extent = high - low;
        float bodySize = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));

        foreach (var (bone, (min, max)) in boxes)
        {
            var size = max - min;
            if (MathF.Max(size.X, MathF.Max(size.Y, size.Z)) < SmallestCapsule) continue;

            int axis = size.X >= size.Y && size.X >= size.Z ? 0
                : size.Y >= size.Z ? 1
                : 2;
            float length = size[axis], radius = (size.X + size.Y + size.Z - length) / 4;
            var along = axis == 0 ? Vector3.UnitX
                : axis == 1 ? Vector3.UnitY
                : Vector3.UnitZ;
            var centre = (min + max) / 2;
            var half = along * MathF.Max(length / 2 - radius, 0);
            capsules.Add(new BoneCapsule(bone, centre - half, centre + half, radius, ChildOf(asset, bone), LimbOf(asset, bone, bodySize)));
        }

        return [.. capsules];
    }

    /// <summary>
    /// The bounds of each bone's pick boxes over the asset's skinned parts.
    /// </summary>
    static Dictionary<int, (Vector3 Min, Vector3 Max)> CapsuleBoneBoxes(CharacterAsset asset)
    {
        var boxes = new Dictionary<int, (Vector3 Min, Vector3 Max)>();
        foreach (var part in asset.Parts)
        {
            if (!part.Shared) continue;

            foreach (var box in part.PickBoxes)
            {
                if (box.Bone < 0) continue;

                boxes[box.Bone] = boxes.TryGetValue(box.Bone, out var b)
                    ? (Vector3.Min(b.Min, box.Min), Vector3.Max(b.Max, box.Max))
                    : (box.Min, box.Max);
            }
        }

        return boxes;
    }

    /// <summary>
    /// The bone a bone points to: its child named for a toe, else its first child; -1 for a bone with none.
    /// </summary>
    static int ChildOf(CharacterAsset asset, int bone)
    {
        int first = -1;
        foreach (var b in asset.Rig.Skeleton.Bones)
        {
            if (asset.Rig.Parent[b.Handle] != bone) continue;
            if (b.Name.Contains("toe", StringComparison.OrdinalIgnoreCase)) return b.Handle;
            if (first < 0) first = b.Handle;
        }

        return first;
    }

    /// <summary>
    /// The bone a capsule's limb goes by: its furthest ancestor within <see cref="LimbShare"/> of the body's size. From the skeleton alone, for
    /// any body: a foot, a paw, a stick leg.
    /// </summary>
    static int LimbOf(CharacterAsset asset, int bone, float size)
    {
        var rig = asset.Rig;
        int limb = bone;
        for (int up = rig.Parent[bone]; up >= 0 && Vector3.Distance(rig.BindPosition[up], rig.BindPosition[bone]) < LimbShare * size;
            up = rig.Parent[up])
        {
            limb = up;
        }

        return limb;
    }
}
