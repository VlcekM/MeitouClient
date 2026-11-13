using Meitou.Game.Rendering.Objects;
using System.Numerics;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The characters in the traced scene (docs/render-lights.md "Traced lamp shadows"): their shown parts as this update placed and posed them, for
/// <see cref="Gi.TracedCharacters"/>.
/// </summary>
internal sealed partial class CharacterRenderer
{
    /// <summary>
    /// One mesh group of a character's part as the traced scene skins it: the group and its drawn level, the part's model relative to the
    /// character's anchor, the instance flags of what the look hides (<see cref="HiddenFlags"/>), its morph base, the cut-out the shadow draws use
    /// (map, threshold, channel: 0 none), and what a reflection shows (diffuse and head maps, packed tint).
    /// </summary>
    internal readonly record struct RayPart(GpuObjectPart Group, int Level, Matrix4x4 Model, uint Flags, uint MorphBase, uint AlphaMap,
        float AlphaCut, uint Channel, uint Diffuse, uint Head, uint Tint);

    /// <summary>
    /// A character in the traced scene: its key, where its parts are relative to (its world position), its palette's first bone, how far from the
    /// eye, and its parts.
    /// </summary>
    internal sealed class RayCharacter
    {
        /// <summary>
        /// The character's draw key.
        /// </summary>
        public long Key;

        /// <summary>
        /// The character's world position, which <see cref="Parts"/> are relative to.
        /// </summary>
        public Vector3 Anchor;

        /// <summary>
        /// The sphere the character and what it carries fit in (the areas of the lamps' per-pixel rays): the body's, grown round every shown part
        /// that hangs from a bone (a drawn or sheathed weapon, a pole on the back), which the body's own radius leaves out. Short of them the shadow
        /// of a long weapon was cut along the 64-pixel cells its lamp's pass works on.
        /// </summary>
        public Vector3 Centre;

        /// <summary>
        /// The radius of the sphere round <see cref="Centre"/>.
        /// </summary>
        public float Radius;

        /// <summary>
        /// The first bone of the character's palette.
        /// </summary>
        public uint BoneBase;

        /// <summary>
        /// How far the character's sphere is from the eye.
        /// </summary>
        public float Distance;

        /// <summary>
        /// The update its palette's pose was made in: the same while the pose is held (frozen, or thinned by its size on screen).
        /// </summary>
        public long Pose;

        /// <summary>
        /// The character's shown mesh groups.
        /// </summary>
        public readonly List<RayPart> Parts = [];
    }

    /// <summary>
    /// The traced characters handed out, reused from update to update.
    /// </summary>
    readonly List<RayCharacter> _rayPool = [];

    /// <summary>
    /// The candidates of <see cref="RayCharacters"/>: their live index and distance, sorted nearest first.
    /// </summary>
    readonly List<(int Live, float Distance)> _rayOrder = [];

    /// <summary>
    /// The GPU frame <see cref="bones"/> was written in (frame memory: valid in that frame only).
    /// </summary>
    long _paletteFrame = -1;

    /// <summary>
    /// This update's bone palettes (the colour pass's binding 6).
    /// </summary>
    internal GpuBufferBinding RayBones => _bones.Binding;

    /// <summary>
    /// The face morph offsets (the colour pass's binding 8).
    /// </summary>
    internal GpuBufferBinding RayMorphs => _content.Morphs.Binding(Gpu.Frame);

    /// <summary>
    /// The characters this update placed within <paramref name="range"/> of <paramref name="eye"/> whose shadow can be in view
    /// (<see cref="Live.OffScreen"/>), nearest first, at most <paramref name="max"/>, with their shown parts at the levels the colour pass draws.
    /// The list's entries are reused. Render thread, after <see cref="Update"/>.
    /// </summary>
    /// <param name="minAngle">Past 4000 units (the range the traced sun always keeps): only those whose radius is at least this share of their
    /// distance.</param>
    internal void RayCharacters(Vector3 eye, float range, int max, List<RayCharacter> into, float minAngle = 0)
    {
        into.Clear();
        if (_portraitLive >= 0 || _partStamp != _updateStamp || _paletteFrame != Gpu.Frame.Number) return;

        _rayOrder.Clear();
        for (int i = 0; i < _liveCount; i++)
        {
            ref readonly var l = ref _live[i];
            if (l.PortraitOnly || l.OffScreen || l.Asset.IsIcon) continue;
            float d = Vector3.Distance(l.Centre, eye) - l.Radius;
            if (d <= range && (d <= Gi.TracedCharacters.Range || l.Radius >= minAngle * d)) _rayOrder.Add((i, d));
        }

        _rayOrder.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        for (int k = 0; k < _rayOrder.Count && into.Count < max; k++)
        {
            if (_rayPool.Count <= into.Count) _rayPool.Add(new RayCharacter());
            var c = _rayPool[into.Count];
            RayCharacterOf(in _live[_rayOrder[k].Live], _rayOrder[k].Distance, c);
            if (c.Parts.Count > 0) into.Add(c);
        }
    }

    /// <summary>
    /// Fills <paramref name="c"/> with live character <paramref name="l"/>: its key, anchor, palette, pose, shown parts and the sphere they fit in.
    /// </summary>
    void RayCharacterOf(in Live l, float distance, RayCharacter c)
    {
        (c.Key, c.Anchor, c.BoneBase, c.Distance, c.Centre, c.Radius) = (l.Key, l.World.Translation, (uint)l.BoneBase, distance, l.Centre, l.Radius);
        c.Pose = l.State.Posed;
        c.Parts.Clear();

        // The parts are placed relative to the update's eye; relative to the anchor here, so the character's structure keeps its precision and its
        // shape while it walks.
        var shift = _partOrigin - c.Anchor;
        var parts = l.Asset.Parts;
        float radius = l.Radius;
        for (int p = 0; p < parts.Count; p++)
        {
            ref readonly var f = ref _partFrames[l.PartBase + p];
            if (!f.Shows) continue;
            AddRayPart(in l, parts[p], in f, shift, c);
            if (parts[p].Shared) continue;
            var at = Vector3.Transform(parts[p].Mesh.Centre, f.Model) + _partOrigin;
            radius = MathF.Max(radius, Vector3.Distance(at, l.Centre) + parts[p].Mesh.Radius * f.Scale);
        }

        c.Radius = radius * 1.1f;
    }

    /// <summary>
    /// Adds the mesh groups of <paramref name="part"/> at the level the colour pass draws, unless its cut-out has not streamed in yet.
    /// </summary>
    void AddRayPart(in Live l, AssetPart part, in PartFrame f, Vector3 shift, RayCharacter c)
    {
        ref readonly var record = ref part.Material.Record;
        if ((record.Flags & CharacterShaders.AlphaPending) != 0) return;   // not drawn until its cut-out streams in

        var (map, cut, channel) = RayCutout(in record);

        // What a reflection shows: the part's own maps, as dark as the colour pass writes them (character.hlsl and skin.hlsl DARKEN).
        bool hair = record.Shading == CharacterShaders.ShadeHair;
        uint diffuse = (record.Flags & CharacterShaders.HasDiffuse) != 0 ? record.Tex[CharacterShaders.SlotDiffuse] : 0;
        uint head = (record.Flags & CharacterShaders.HasHead) != 0 ? record.Tex[CharacterShaders.SlotHeadDiffuse] : 0;
        uint tint = RayTint(in record, hair);
        var model = f.Model;
        model.Translation += shift;

        // The drawn level (the colour pass's rule): a surface's ray leaves the same triangles it drew, so its own body shadows it only where it
        // really does.
        int level = !GeneratedLod ? FileLevel(part, f.FileLod) : GeneratedLevel(part, f.Lod * LodBias * LensLod, depth: false);
        uint flags = HiddenFlags(part, l.Asset);
        uint morphs = part.Morphed ? l.Asset.MorphBase : 0;
        var groups = part.Mesh.Parts;
        for (int g = 0; g < groups.Count; g++)
        {
            if (GroupLevel(groups[g], level) is int lv && groups[g].Count[lv] >= 3)
                c.Parts.Add(new RayPart(groups[g], lv, model, flags, morphs, map, cut, channel, diffuse, head, tint));
        }
    }

    /// <summary>
    /// The colour a reflection multiplies a part's maps by, packed as RGBA bytes: hair its own colour darkened, anything else darker by its kind;
    /// the alpha byte 255 for all but hair.
    /// </summary>
    static uint RayTint(in CharacterMaterialRecord record, bool hair)
    {
        var shade = hair
            ? new Vector3(record.HairColour.X, record.HairColour.Y, record.HairColour.Z) * 0.7f
            : new Vector3(record.Shading != 0 ? 0.7f
                : (record.Flags & CharacterShaders.Worn) != 0 ? 0.8f
                : 1f);
        return (uint)(Math.Clamp(shade.X, 0, 1) * 255) | (uint)(Math.Clamp(shade.Y, 0, 1) * 255) << 8 | (uint)(Math.Clamp(shade.Z, 0, 1) * 255) << 16
            | (hair ? 0u : 255u << 24);
    }

    /// <summary>
    /// The cut-out a part's shadow draw makes (<c>CharacterShaders.DepthFragment</c>): hair by its diffuse map's alpha channel, an item clipped on
    /// its normal map's alpha at 0.6.
    /// </summary>
    static (uint Map, float Cut, uint Channel) RayCutout(in CharacterMaterialRecord r)
    {
        if (r.Shading == CharacterShaders.ShadeHair && (r.Flags & CharacterShaders.HasDiffuse) != 0 && r.AlphaThreshold > 0)
        {
            var a = r.AlphaChannel;
            uint channel = a.W >= Math.Max(a.X, Math.Max(a.Y, a.Z)) ? 3u
                : a.Z >= Math.Max(a.X, a.Y) ? 2u
                : a.Y >= a.X ? 1u
                : 0u;
            return (r.Tex[CharacterShaders.SlotDiffuse], r.AlphaThreshold, channel);
        }

        if (r.Shading == CharacterShaders.ShadeItem && (r.Flags & CharacterShaders.ClipNormalAlpha) != 0)
            return (r.Tex[CharacterShaders.SlotNormal], 0.6f, 3);
        return (0, 0, 0);
    }
}
