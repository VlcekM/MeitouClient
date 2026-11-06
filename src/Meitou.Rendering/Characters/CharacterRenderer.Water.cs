using System.Numerics;
using System.Runtime.CompilerServices;

namespace Meitou.Rendering.Characters;

internal sealed partial class CharacterRenderer
{
    /// <summary>A bone's capsule in the binding pose's model space: round the vertices it moves most (its <see cref="PickBox"/>es, all the skinned parts' together).</summary>
    readonly record struct BoneCapsule(int Bone, Vector3 A, Vector3 B, float Radius);

    readonly ConditionalWeakTable<CharacterAsset, BoneCapsule[]> boneCapsules = [];

    /// <summary>A part's bone boxes thinner than this (units) are left out: fingers, toes and the like make no wave of their own.</summary>
    const float SmallestCapsule = 0.5f;

    /// <summary>
    /// The posed body of the character <paramref name="key"/> as capsules (one per skinned bone, round its vertices), those reaching from <paramref name="below"/> units under
    /// <paramref name="water"/> to its surface added to <paramref name="into"/>. Nothing when it was not drawn in the world in the last update. Render thread, after <see cref="Update"/>.
    /// </summary>
    public void WaterCapsules(long key, float water, float below, List<WaterBody> into)
    {
        if (!states.TryGetValue(key, out var s) || s.Seen != updateStamp || !s.InWorld || s.Asset is not { IsIcon: false } asset || s.Posed < 0) return;
        foreach (var c in boneCapsules.GetValue(asset, CapsulesOf))
        {
            if (c.Bone >= s.Bones.Length) continue;
            var m = s.Bones[c.Bone] * s.World;
            var a = Vector3.Transform(c.A, m);
            var b = Vector3.Transform(c.B, m);
            float r = c.Radius * Vector3.TransformNormal(Vector3.UnitY, m).Length();
            if (MathF.Min(a.Y, b.Y) - r < water && MathF.Max(a.Y, b.Y) + r > water - below) into.Add(new WaterBody(a, b, r));
        }
    }

    /// <summary>Each bone's capsule: along its box's longest side, as thick as the mean of the other two.</summary>
    static BoneCapsule[] CapsulesOf(CharacterAsset asset)
    {
        var boxes = new Dictionary<int, (Vector3 Min, Vector3 Max)>();
        foreach (var part in asset.Parts)
        {
            if (!part.Shared) continue;
            foreach (var box in part.PickBoxes)
                if (box.Bone >= 0)
                    boxes[box.Bone] = boxes.TryGetValue(box.Bone, out var b) ? (Vector3.Min(b.Min, box.Min), Vector3.Max(b.Max, box.Max)) : (box.Min, box.Max);
        }
        var capsules = new List<BoneCapsule>();
        foreach (var (bone, (min, max)) in boxes)
        {
            var size = max - min;
            if (MathF.Max(size.X, MathF.Max(size.Y, size.Z)) < SmallestCapsule) continue;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            float length = size[axis], radius = (size.X + size.Y + size.Z - length) / 4;
            var along = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            var centre = (min + max) / 2;
            var half = along * MathF.Max(length / 2 - radius, 0);
            capsules.Add(new BoneCapsule(bone, centre - half, centre + half, radius));
        }
        return [.. capsules];
    }
}
