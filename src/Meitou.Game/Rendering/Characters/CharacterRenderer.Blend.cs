using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Rendering.Characters;

/// <summary>
/// The character editor's glide between two looks (<see cref="CharacterInstance.BlendFrom"/>): the asset of the new look is drawn with the mix of its own values and those
/// of the look it replaces, so a slider moves the character along instead of swapping it. The values are the ones a slider changes: the body shape and the feet's lift,
/// the posture sliders, the face and body poses (one mixed morph set a frame, <see cref="MorphArena.Scratch"/>) and the skin and hair colours.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>Mixes <paramref name="to"/>'s values with <paramref name="from"/>'s, <paramref name="t"/> of the way to <paramref name="to"/>; looks that differ in more than those values (another head, hair or clothes) are shown as they are.</summary>
    void ApplyBlend(CharacterAsset to, CharacterAsset from, float t)
    {
        if (to.IsIcon || from.IsIcon || to.Rig != from.Rig || to.Parts.Count != from.Parts.Count || to.Fixed.Count != from.Fixed.Count)
        {
            RestoreBlend(to);
            return;
        }
        var a = RestOf(from);
        var b = RestOf(to);
        t = Math.Clamp(t, 0, 1);
        to.Shape = Lerp(a.Shape, b.Shape, t);
        to.GroundOffset = a.GroundOffset + (b.GroundOffset - a.GroundOffset) * t;
        for (int i = 0; i < to.Fixed.Count; i++)
            to.Fixed[i] = a.Fixed[i].Def == b.Fixed[i].Def ? (b.Fixed[i].Def, a.Fixed[i].Time + (b.Fixed[i].Time - a.Fixed[i].Time) * t) : b.Fixed[i];
        for (int i = 0; i < to.Parts.Count; i++)
        {
            to.Parts[i].Material.SkinTone = Vector3.Lerp(a.Colours[i].Skin, b.Colours[i].Skin, t);
            to.Parts[i].Material.HairColour = Vector3.Lerp(a.Colours[i].Hair, b.Colours[i].Hair, t);
            to.Parts[i].Material.RecordEpoch = 0;
        }
        // The poses: the weights mixed (a pose one look does not set is at 0 there), the offsets summed as the asset's own set is.
        if (to.Morph is { } target && from.Morph is { } source && target.Poses == source.Poses && b.MorphBase != 0)
        {
            var weights = new Dictionary<string, float>(target.Weights, StringComparer.Ordinal);
            foreach (var (name, value) in source.Weights) if (!weights.ContainsKey(name)) weights[name] = 0;
            foreach (var name in weights.Keys.ToArray())
                weights[name] = (source.Weights.TryGetValue(name, out float was) ? was : 0) * (1 - t) + weights[name] * t;
            if (target.Poses.Deltas(weights, target.CutHorns) is { } deltas) to.MorphBase = content.Morphs.Scratch(deltas);
        }
        to.Blending = true;
    }

    /// <summary>Gives <paramref name="asset"/> back its own values after a blend.</summary>
    static void RestoreBlend(CharacterAsset asset)
    {
        if (!asset.Blending || asset.Rest is not { } rest) return;
        asset.Shape = rest.Shape;
        asset.GroundOffset = rest.GroundOffset;
        for (int i = 0; i < asset.Fixed.Count; i++) asset.Fixed[i] = rest.Fixed[i];
        for (int i = 0; i < asset.Parts.Count; i++)
        {
            var m = asset.Parts[i].Material;
            (m.SkinTone, m.HairColour, m.RecordEpoch) = (rest.Colours[i].Skin, rest.Colours[i].Hair, 0);
        }
        asset.MorphBase = rest.MorphBase;
        asset.Blending = false;
    }

    /// <summary>The values <paramref name="asset"/> was built with (taken when it is first blended, before anything changes them).</summary>
    static AssetRest RestOf(CharacterAsset asset) => asset.Rest ??= new AssetRest
    {
        Shape = asset.Shape, GroundOffset = asset.GroundOffset, Fixed = [.. asset.Fixed], MorphBase = asset.MorphBase,
        Colours = [.. asset.Parts.Select(p => (p.Material.SkinTone, p.Material.HairColour))],
    };

    static BodyShapeData? Lerp(BodyShapeData? a, BodyShapeData? b, float t)
    {
        if (a is null || b is null || a.BoneSize.Length != b.BoneSize.Length || a.PositionalSize.Length != b.PositionalSize.Length) return b;
        var size = new Vector3[b.BoneSize.Length];
        for (int i = 0; i < size.Length; i++) size[i] = Vector3.Lerp(a.BoneSize[i], b.BoneSize[i], t);
        var positional = new Vector3[b.PositionalSize.Length];
        for (int i = 0; i < positional.Length; i++) positional[i] = Vector3.Lerp(a.PositionalSize[i], b.PositionalSize[i], t);
        return new BodyShapeData(size, positional, a.MovementScale + (b.MovementScale - a.MovementScale) * t);
    }
}
