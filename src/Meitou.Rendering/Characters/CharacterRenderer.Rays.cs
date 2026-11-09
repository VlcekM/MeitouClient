using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

/// <summary>The characters in the traced scene (docs/render-lights.md "Traced lamp shadows"): their shown parts as this update placed and posed them, for <see cref="Gi.TracedCharacters"/>.</summary>
internal sealed partial class CharacterRenderer
{
    /// <summary>
    /// One mesh group of a character's part as the traced scene skins it: the group and its drawn level, the part's model relative to the character's anchor, the instance flags of what the
    /// look hides (<see cref="HiddenFlags"/>), its morph base, and the cut-out the shadow draws use (map, threshold, channel: 0 none).
    /// </summary>
    internal readonly record struct RayPart(GpuObjectPart Group, int Level, Matrix4x4 Model, uint Flags, uint MorphBase, uint AlphaMap, float AlphaCut, uint Channel);

    /// <summary>A character in the traced scene: its key, where its parts are relative to (its world position), its palette's first bone, how far from the eye, and its parts.</summary>
    internal sealed class RayCharacter
    {
        public long Key;
        public Vector3 Anchor;
        /// <summary>The sphere the character's pose fits in (the lamps' moving layers).</summary>
        public Vector3 Centre;
        public float Radius;
        public uint BoneBase;
        public float Distance;
        public readonly List<RayPart> Parts = [];
    }

    readonly List<RayCharacter> rayPool = [];
    readonly List<(int Live, float Distance)> rayOrder = [];
    /// <summary>The GPU frame <see cref="bones"/> was written in (frame memory: valid in that frame only).</summary>
    long paletteFrame = -1;

    /// <summary>This update's bone palettes (the colour pass's binding 6).</summary>
    internal GpuBufferBinding RayBones => bones.Binding;

    /// <summary>The face morph offsets (the colour pass's binding 8).</summary>
    internal GpuBufferBinding RayMorphs => content.Morphs.Binding(Gpu.Frame);

    /// <summary>
    /// The characters this update placed within <paramref name="range"/> of <paramref name="eye"/> whose shadow can be in view (<see cref="Live.OffScreen"/>), nearest first, at most
    /// <paramref name="max"/>, with their shown parts at the levels the colour pass draws. The list's entries are reused. Render thread, after <see cref="Update"/>.
    /// </summary>
    internal void RayCharacters(Vector3 eye, float range, int max, List<RayCharacter> into)
    {
        into.Clear();
        if (portraitLive >= 0 || partStamp != updateStamp || paletteFrame != Gpu.Frame.Number) return;
        rayOrder.Clear();
        for (int i = 0; i < liveCount; i++)
        {
            ref readonly var l = ref live[i];
            if (l.PortraitOnly || l.OffScreen || l.Asset.IsIcon) continue;
            float d = Vector3.Distance(l.Centre, eye) - l.Radius;
            if (d <= range) rayOrder.Add((i, d));
        }
        rayOrder.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        for (int k = 0; k < rayOrder.Count && into.Count < max; k++)
        {
            if (rayPool.Count <= into.Count) rayPool.Add(new RayCharacter());
            var c = rayPool[into.Count];
            RayCharacterOf(in live[rayOrder[k].Live], rayOrder[k].Distance, c);
            if (c.Parts.Count > 0) into.Add(c);
        }
    }

    void RayCharacterOf(in Live l, float distance, RayCharacter c)
    {
        (c.Key, c.Anchor, c.BoneBase, c.Distance, c.Centre, c.Radius) = (l.Key, l.World.Translation, (uint)l.BoneBase, distance, l.Centre, l.Radius);
        c.Parts.Clear();
        // The parts are placed relative to the update's eye; relative to the anchor here, so the character's structure keeps its precision and its shape while it walks.
        var shift = partOrigin - c.Anchor;
        var parts = l.Asset.Parts;
        for (int p = 0; p < parts.Count; p++)
        {
            ref readonly var f = ref partFrames[l.PartBase + p];
            if (f.Shows) AddRayPart(in l, parts[p], in f, shift, c);
        }
    }

    void AddRayPart(in Live l, AssetPart part, in PartFrame f, Vector3 shift, RayCharacter c)
    {
        ref readonly var record = ref part.Material.Record;
        if ((record.Flags & CharacterShaders.AlphaPending) != 0) return;   // not drawn until its cut-out streams in
        var (map, cut, channel) = RayCutout(in record);
        var model = f.Model;
        model.Translation += shift;
        // The drawn level (the colour pass's rule): a surface's ray leaves the same triangles it drew, so its own body shadows it only where it really does.
        int level = !GeneratedLod ? FileLevel(part, f.FileLod) : GeneratedLevel(part, f.Lod * LodBias, depth: false);
        uint flags = HiddenFlags(part, l.Asset), morphs = part.Morphed ? l.MorphBase : 0;
        var groups = part.Mesh.Parts;
        for (int g = 0; g < groups.Count; g++)
            if (GroupLevel(groups[g], level) is int lv && groups[g].Count[lv] >= 3)
                c.Parts.Add(new RayPart(groups[g], lv, model, flags, morphs, map, cut, channel));
    }

    /// <summary>The cut-out a part's shadow draw makes (<c>CharacterShaders.DepthFragment</c>): hair by its diffuse map's alpha channel, an item clipped on its normal map's alpha at 0.6.</summary>
    static (uint Map, float Cut, uint Channel) RayCutout(in CharacterMaterialRecord r)
    {
        if (r.Shading == CharacterShaders.ShadeHair && (r.Flags & CharacterShaders.HasDiffuse) != 0 && r.AlphaThreshold > 0)
        {
            var a = r.AlphaChannel;
            uint channel = a.W >= Math.Max(a.X, Math.Max(a.Y, a.Z)) ? 3u : a.Z >= Math.Max(a.X, a.Y) ? 2u : a.Y >= a.X ? 1u : 0u;
            return (r.Tex[CharacterShaders.SlotDiffuse], r.AlphaThreshold, channel);
        }
        if (r.Shading == CharacterShaders.ShadeItem && (r.Flags & CharacterShaders.ClipNormalAlpha) != 0) return (r.Tex[CharacterShaders.SlotNormal], 0.6f, 3);
        return (0, 0, 0);
    }
}
