using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The placed fog volumes of <c>fogfeatures.dat</c> (docs/formats/fogfeatures.md): the swamp's ground fog, the Fog Islands', the Vain's red
/// haze, the Skinner's Roam dome, the two beams and the rest, plus the weather's EFFECT_FOG_VOLUME spheres (the twisters' dust balls,
/// docs/formats/weather.md "Fog volumes"), which the game draws with the same classes and shaders. Each frame <see cref="Update"/> keeps every
/// volume that can show in the view (its box within the far clip and inside the camera's horizontal wedge), sorts them back to front as the
/// game's transparent queue does, and packs them into the frame block's <c>uFogVolumeData</c>, which every world shader reads through
/// <see cref="FogVolumeShaders"/>. No GPU resource of its own.
/// </summary>
internal sealed class FogVolumes
{
    /// <summary>The lowest height a block is measured at (its box): the sea is at 100 (<see cref="WorldWater.Height"/>), with room for the sea
    /// floor seen through the water.</summary>
    public const float SectionHeight = -1000f;
    /// <summary>The highest height the box reaches for a block open upwards (far above the game's camera).</summary>
    public const float HighHeight = 30000f;

    /// <summary>One volume: its packed vec4s, the box a ray must cross (culling) and the point the game's queue sorts it by (its hull's bounds centre).</summary>
    internal sealed class Volume(string name, int type)
    {
        public string Name { get; set; } = name;
        public int Type { get; } = type;
        public Vector4[] Data { get; } = new Vector4[FogVolumeShaders.Length(type)];
        public Vector3 BoxMin { get; set; }
        public Vector3 BoxMax { get; set; }
        public Vector3 SortCentre { get; set; }
    }

    /// <summary>A weather effect's fog volume this frame (<see cref="ParticleRenderer.CollectFogVolumes"/>): world position (and the column's end),
    /// the record's radius and density distance, and the effect's fade fraction (0 to 1 over <c>fog fade in duration</c>, back to 0 over
    /// <c>fog fade out duration</c>).</summary>
    public readonly record struct EffectFog(string Name, bool Cylinder, Vector3 Position, Vector3 End, float Radius, float Distance, Vector3 Colour,
        float Alpha, bool Additive, float Fade);

    readonly List<Volume> statics = [];
    readonly List<Volume> effects = [];
    readonly List<(Volume V, float Distance)> picked = [];
    readonly Vector4[] data = new Vector4[FogVolumeShaders.MaxData];
    int used;
    Vector4 eyeLight, info;

    /// <summary>Off: no volume is drawn (<c>--no-fog-volumes</c>; also while the simple sky is on).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The volumes drawn in the last <see cref="Update"/>, farthest first (statistics, tests).</summary>
    public List<string> Active { get; } = [];
    /// <summary>The volumes in view that did not fit <c>uFogVolumeData</c> in the last <see cref="Update"/> (the farthest ones; none in the base game).</summary>
    public int Dropped { get; private set; }
    /// <summary>The vec4s of <c>uFogVolumeData</c> in use after the last <see cref="Update"/>.</summary>
    public int UsedData => used;
    /// <summary>The weather effects' fog volumes handed to the last <see cref="Update"/>, and how many of them were in view and drawn.</summary>
    public int EffectVolumes { get; private set; }
    public int EffectVolumesDrawn { get; private set; }
    public IReadOnlyList<FogFeature> Features { get; }
    internal IReadOnlyList<Volume> Statics => statics;
    internal ReadOnlySpan<Vector4> Data => data.AsSpan(0, used);

    public FogVolumes(IReadOnlyList<FogFeature> features)
    {
        Features = features;
        foreach (var f in features)
            if (Build(f) is { } v) statics.Add(v);
    }

    /// <summary>With the frame globals published (the world's renderer).</summary>
    public FogVolumes(GpuContext gpu, IReadOnlyList<FogFeature> features) : this(features)
    {
        var g = gpu.Globals;
        g.PublishUniform("uFogVolumeEye", () => eyeLight);
        g.PublishUniform("uFogVolumeInfo", () => info);
        g.PublishUniformArray("uFogVolumeData", data, () => used);
    }

    /// <summary>The game's light on the blocks (post/fog.hlsl <c>fog_planes_fs</c>): <c>max(0.08, sunColour.w · 1.6 · saturate(3 sunY + 0.2))</c>.
    /// Spheres and beams take <c>sunColour.w</c> alone (<see cref="KenshiLighting.Daylight"/>).</summary>
    public static float Light(float sunY) => MathF.Max(0.08f, KenshiLighting.Daylight(sunY) * 1.6f * Math.Clamp(sunY * 3f + 0.2f, 0f, 1f));

    /// <summary>A file volume's packed form, or null for a block whose box no ray can meet.</summary>
    internal static Volume? Build(FogFeature f)
    {
        switch (f.Type)
        {
            case FogFeatureType.Block:
            {
                // The box: the sections at the lowest and the highest height (a convex block's sections in between lie within both boxes), up to its
                // highest corner, or the highest height when it is open upwards ("Volcano01").
                var low = f.SectionBounds(SectionHeight);
                var high = f.SectionBounds(HighHeight);
                if ((low ?? high) is not { } first) return null;
                var corners = f.Corners();
                float top = high is not null ? HighHeight : corners.Count > 0 ? corners.Max(c => c.Y) : HighHeight;
                var (min, max) = first;
                if (high is { } h) { min = Vector2.Min(min, h.Min); max = Vector2.Max(max, h.Max); }
                var v = new Volume(f.Name, FogVolumeShaders.BlockType)
                {
                    BoxMin = new Vector3(min.X, SectionHeight, min.Y),
                    BoxMax = new Vector3(max.X, MathF.Max(top, SectionHeight), max.Y),
                };
                // The game's node sits at the hull's corners; Ogre sorts by its bounds' centre (docs: Observed).
                v.SortCentre = corners.Count > 0 ? (corners.Aggregate(new Vector3(float.MaxValue), Vector3.Min) + corners.Aggregate(new Vector3(float.MinValue), Vector3.Max)) * 0.5f
                    : (v.BoxMin + v.BoxMax) * 0.5f;
                v.Data[0] = new Vector4(v.BoxMin, FogVolumeShaders.BlockType);
                v.Data[1] = new Vector4(v.BoxMax, f.EdgeBlur);
                v.Data[2] = new Vector4(f.Colour, f.Density);
                for (int k = 0; k < 7; k++) v.Data[3 + k] = f.Planes[k];
                return v;
            }
            case FogFeatureType.Sphere:
            {
                var v = new Volume(f.Name, FogVolumeShaders.SphereType);
                SetSphere(v, f.Position, f.Radius, f.Density, f.Colour, 1, false);
                return v;
            }
            case FogFeatureType.Beam:
            {
                var v = new Volume(f.Name, FogVolumeShaders.BeamType);
                SetBeam(v, f.Position, f.End, f.Radius, f.Density, f.EdgeBlur, f.Colour, 1, false);
                return v;
            }
            default:
                return null;
        }
    }

    /// <summary>A sphere of stored radius <paramref name="radius"/>: the hull (radius r) bounds it, the shader uses 0.96 r.</summary>
    static void SetSphere(Volume v, Vector3 centre, float radius, float density, Vector3 colour, float alpha, bool additive)
    {
        v.BoxMin = centre - new Vector3(radius);
        v.BoxMax = centre + new Vector3(radius);
        v.SortCentre = centre;
        v.Data[0] = new Vector4(centre, FogVolumeShaders.SphereType);
        v.Data[1] = new Vector4(radius * FogVolumeShaders.SphereShaderRadius, radius, alpha, additive ? 1 : 0);
        v.Data[2] = new Vector4(colour, density);
    }

    /// <summary>A beam from <paramref name="start"/> to <paramref name="end"/>: the hull (a hexagonal prism of circumradius r) bounds it, the shader uses 0.8 r.</summary>
    static void SetBeam(Volume v, Vector3 start, Vector3 end, float radius, float density, float edgeBlur, Vector3 colour, float alpha, bool additive)
    {
        var axis = end - start;
        float length = axis.Length();
        var unit = length > 0 ? axis / length : Vector3.UnitY;
        v.BoxMin = Vector3.Min(start, end) - new Vector3(radius);
        v.BoxMax = Vector3.Max(start, end) + new Vector3(radius);
        v.SortCentre = (start + end) * 0.5f;
        v.Data[0] = new Vector4(start, FogVolumeShaders.BeamType);
        v.Data[1] = new Vector4(unit, length);
        v.Data[2] = new Vector4(radius * FogVolumeShaders.BeamShaderRadius, edgeBlur, alpha, additive ? 1 : 0);
        v.Data[3] = new Vector4(colour, density);
    }

    /// <summary>
    /// An effect volume's radius and density distance at fade fraction <paramref name="fade"/> (FogController's FogFadeSphere / FogFadeCylinder):
    /// the radius grows from 0, the density distance comes down from 10 × the radius (a sphere) or 4 × its own value (a cylinder), both linearly.
    /// </summary>
    public static (float Radius, float Distance) Faded(bool cylinder, float radius, float distance, float fade)
    {
        fade = Math.Clamp(fade, 0, 1);
        float from = cylinder ? distance * 4 : radius * 10;
        return (radius * fade, from + (distance - from) * fade);
    }

    /// <summary>The game's density for a density distance (FUN_14010aea0): 1 / distance, 1 when it is not positive.</summary>
    static float Density(float distance) => distance > 0 ? 1f / distance : 1f;

    /// <summary>
    /// Picks this frame's volumes: those whose box lies within <paramref name="far"/> of <paramref name="eye"/> and inside the view's horizontal
    /// wedge (from the camera's <paramref name="forward"/>, vertical field of view and aspect; the same in x, z for the mirrored reflection camera),
    /// with the weather's <paramref name="effectFogs"/>, sorted farthest first and packed. <paramref name="far"/> is the game's far clip D, at which the
    /// shaders cap every distance (so a volume beyond it adds nothing). <paramref name="on"/> false draws none.
    /// </summary>
    public void Update(Vector3 eye, Vector3 forward, float fieldOfView, float aspect, float far, float sunY, bool on, IReadOnlyList<EffectFog>? effectFogs = null)
    {
        Active.Clear();
        picked.Clear();
        used = 0;
        Dropped = 0;
        EffectVolumes = effectFogs?.Count ?? 0;
        EffectVolumesDrawn = 0;
        if (on && Enabled)
        {
            var wedge = Wedge.From(forward, fieldOfView, aspect);
            foreach (var v in statics) Consider(v, eye, far, wedge);
            int e = 0;
            if (effectFogs is not null)
                foreach (var fog in effectFogs)
                {
                    var (radius, distance) = Faded(fog.Cylinder, fog.Radius, fog.Distance, fog.Fade);
                    if (radius <= 0 || fog.Alpha <= 0) continue;   // the game hides a volume at alpha 0 (FUN_140108f50)
                    // The game's inside test reads the node's position relative to the effect's node, so a camera inside an effect's
                    // sphere is never "inside" and the hull's back faces are culled: from inside, the sphere is not drawn (docs: Verified (decompiled)).
                    if (!fog.Cylinder && Vector3.DistanceSquared(eye, fog.Position) < radius * radius) continue;
                    if (e == effects.Count) effects.Add(new Volume("", fog.Cylinder ? FogVolumeShaders.BeamType : FogVolumeShaders.SphereType));
                    if (effects[e].Type != (fog.Cylinder ? FogVolumeShaders.BeamType : FogVolumeShaders.SphereType))
                        effects[e] = new Volume("", fog.Cylinder ? FogVolumeShaders.BeamType : FogVolumeShaders.SphereType);
                    var v = effects[e++];
                    v.Name = fog.Name;
                    if (fog.Cylinder) SetBeam(v, fog.Position, fog.End, radius, Density(distance), 1f / 300f, fog.Colour, fog.Alpha, fog.Additive);
                    else SetSphere(v, fog.Position, radius, Density(distance), fog.Colour, fog.Alpha, fog.Additive);
                    Consider(v, eye, far, wedge);
                }
            // Nearest first while they fit, then written farthest first (the game's transparent objects are sorted back to front).
            picked.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            int keep = 0;
            for (; keep < picked.Count; keep++)
            {
                int length = picked[keep].V.Data.Length;
                if (used + length > data.Length) break;
                used += length;
            }
            Dropped = picked.Count - keep;
            int at = 0;
            for (int i = keep - 1; i >= 0; i--)
            {
                var v = picked[i].V;
                v.Data.CopyTo(data, at);
                at += v.Data.Length;
                Active.Add(v.Name);
                if (effects.Contains(v)) EffectVolumesDrawn++;
            }
        }
        eyeLight = new Vector4(eye, Light(sunY));
        info = new Vector4(used, KenshiLighting.Daylight(sunY), 0, 0);
    }

    void Consider(Volume v, Vector3 eye, float far, in Wedge wedge)
    {
        var nearest = Vector3.Clamp(eye, v.BoxMin, v.BoxMax);
        if (Vector3.DistanceSquared(nearest, eye) > far * far) return;   // beyond the far clip: the game's path through it is empty
        if (!wedge.Touches(eye, v.BoxMin, v.BoxMax)) return;
        picked.Add((v, Vector3.Distance(v.SortCentre, eye)));
    }

    /// <summary>
    /// The view's extent in x, z: a wedge from the eye round the camera's horizontal direction, as wide as the frustum's corner rays reach
    /// (plus a margin). Pitch-independent in x, z, so it also holds for the water reflection's mirrored camera. None (everything passes) when
    /// the camera looks nearly straight up or down or the wedge would be wider than 170°.
    /// </summary>
    internal readonly struct Wedge
    {
        readonly Vector2 left, right;   // outward normals of the two edges, in (x, z)
        readonly bool all;

        Wedge(Vector2 left, Vector2 right, bool all) { this.left = left; this.right = right; this.all = all; }

        public static Wedge From(Vector3 forward, float fieldOfView, float aspect)
        {
            var none = new Wedge(default, default, true);
            forward = Vector3.Normalize(forward);
            var flat = new Vector2(forward.X, forward.Z);
            if (flat.Length() < 1e-3f) return none;
            flat = Vector2.Normalize(flat);
            var side = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            var up = Vector3.Cross(side, forward);
            float tanY = MathF.Tan(fieldOfView * 0.5f), tanX = tanY * aspect;
            float widest = 0;
            for (int i = 0; i < 4; i++)
            {
                var ray = forward + side * (i % 2 == 0 ? -tanX : tanX) + up * (i < 2 ? -tanY : tanY);
                var c = new Vector2(ray.X, ray.Z);
                if (c.Length() < 1e-3f * ray.Length() || Vector2.Dot(c, flat) <= 0) return none;
                widest = MathF.Max(widest, MathF.Abs(MathF.Atan2(flat.X * c.Y - flat.Y * c.X, Vector2.Dot(flat, c))));
            }
            float half = widest + 2 * MathF.PI / 180;
            if (half >= 85 * MathF.PI / 180) return none;
            return new Wedge(Rotate(flat, half + MathF.PI / 2), Rotate(flat, -half - MathF.PI / 2), false);
        }

        static Vector2 Rotate(Vector2 v, float a) => new(v.X * MathF.Cos(a) - v.Y * MathF.Sin(a), v.X * MathF.Sin(a) + v.Y * MathF.Cos(a));

        /// <summary>False when the box's footprint lies wholly outside one edge.</summary>
        public bool Touches(Vector3 eye, Vector3 min, Vector3 max)
        {
            if (all) return true;
            bool outLeft = true, outRight = true;
            for (int i = 0; i < 4; i++)
            {
                var p = new Vector2((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Z : max.Z) - new Vector2(eye.X, eye.Z);
                if (Vector2.Dot(p, left) <= 0) outLeft = false;
                if (Vector2.Dot(p, right) <= 0) outRight = false;
            }
            return !outLeft && !outRight;
        }
    }
}
