using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// Runtime sole masks from the lowest vertices of the equipped footwear. Reading and baking run off the render thread.
/// </summary>
sealed class FootprintSoles(IGpuContext gpu) : IDisposable
{
    /// <summary>
    /// The two baked masks per model. The cap keeps texture and bindless use bounded when many mods add footwear.
    /// </summary>
    const int MaxModels = 24;

    /// <summary>
    /// A sole ready for rendering, with world half extents.
    /// </summary>
    internal sealed class Sprite(SampledImage image, float width, float length)
    {
        /// <summary>
        /// The uploaded mask.
        /// </summary>
        public readonly SampledImage Image = image;
        /// <summary>
        /// Half extents in world units.
        /// </summary>
        public readonly float Width = width, Length = length;
        /// <summary>
        /// The bindless entry, registered only when drawn.
        /// </summary>
        public uint Index = uint.MaxValue;
        /// <summary>
        /// The sampler's current LOD bias.
        /// </summary>
        public float Bias = float.NaN;
    }

    /// <summary>
    /// CPU mask and its world half extents.
    /// </summary>
    sealed record Baked(byte[] Rgba, float Width, float Length);

    /// <summary>
    /// A model being read or ready, and the two sides already uploaded.
    /// </summary>
    sealed class Entry(string path, bool bare)
    {
        /// <summary>
        /// The masks in left, right order.
        /// </summary>
        public readonly Task<Baked?[]> Source = Task.Run(() => Read(path, bare));
        /// <summary>
        /// Uploaded masks in left, right order.
        /// </summary>
        public readonly Sprite?[] Sprites = new Sprite?[2];
    }

    /// <summary>
    /// Entries by resolved asset path, including unavailable meshes so they are tried only once.
    /// </summary>
    readonly Dictionary<string, Entry> _models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Starts loading the model before the walker leaves the pool: footwear, or a <paramref name="bare"/> body whose feet print.
    /// </summary>
    public void Prepare(string path, bool bare = false)
    {
        string key = Key(path, bare);
        if (_models.Count < MaxModels && !_models.ContainsKey(key)) _models.Add(key, new Entry(path, bare));
    }

    /// <summary>
    /// Returns an available mask without waiting for file reads. Null uses the generic sole.
    /// </summary>
    public Sprite? For(string path, bool right, bool bare = false)
    {
        Prepare(path, bare);
        if (!_models.TryGetValue(Key(path, bare), out var entry) || !entry.Source.IsCompletedSuccessfully) return null;

        int side = right ? 1 : 0;
        if (entry.Sprites[side] is { } sprite) return sprite;
        if (entry.Source.Result[side] is not { } baked) return null;

        var image = SampledImage.Rgba8(gpu, FootprintTexture.Width, FootprintTexture.Height, baked.Rgba,
            repeat: false, mipmaps: true, name: "shoe sole");
        return entry.Sprites[side] = new Sprite(image, baked.Width, baked.Length);
    }

    /// <summary>
    /// A bindless index using the renderer's current sampler bias.
    /// </summary>
    public uint Index(Sprite sprite)
    {
        if (sprite.Index == uint.MaxValue) sprite.Index = gpu.Bindless.Register(BindlessKind.Texture2D, sprite.Image.Sampled());
        else if (sprite.Bias != gpu.LodBias) gpu.Bindless.Update(BindlessKind.Texture2D, sprite.Index, sprite.Image.Sampled());
        sprite.Bias = gpu.LodBias;
        return sprite.Index;
    }

    /// <summary>
    /// A model's entry key: a body mesh read for its bare feet is another mask than the same file read as footwear.
    /// </summary>
    static string Key(string path, bool bare) => bare ? "bare|" + path : path;

    /// <summary>
    /// Collects positions from the original Ogre submeshes. Unknown or broken models can still use a generic sole.
    /// </summary>
    static Baked?[] Read(string path, bool bare)
    {
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            if (bare)
            {
                // A body whose feet give no skin sole (a skeleton's frame) prints their outline as footwear does, never the drawn bare mask.
                var triangles = Triangles(mesh);
                var all = Positions(mesh);
                // A fitted Human or Hive leg's mesh is only the cap above the knee: no foot of its own, the body's prints.
                if (all.Length == 0 || all.Min(p => p.Y) > NoFootAbove) return [null, null];

                return [BakeBare(triangles, false) ?? Bake(all, false), BakeBare(triangles, true) ?? Bake(all, true)];
            }

            var positions = Positions(mesh);
            return [Bake(positions, false), Bake(positions, true)];
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException
            or IndexOutOfRangeException or KeyNotFoundException or UnauthorizedAccessException or OverflowException)
        {
            return [null, null];
        }
    }

    /// <summary>
    /// Every vertex position of the mesh's own and shared vertex data.
    /// </summary>
    static Vector3[] Positions(OgreMesh mesh) =>
        [.. mesh.SubMeshes
            .Select(part => part.UseSharedVertices ? mesh.SharedVertexData : part.VertexData)
            .Distinct()
            .SelectMany(data => data?.ReadPositions() ?? [])];

    /// <summary>
    /// Uses the shoe's bottom band in model space, where positive X is the left side and the toes point towards positive Z.
    /// </summary>
    static Baked? Bake(Vector3[] positions, bool right)
    {
        var side = positions.Where(p => OnSide(p, right)).ToArray();
        if (side.Length < 3) return null;

        float bottom = side.Min(p => p.Y);
        float height = side.Max(p => p.Y) - bottom;
        float band = Math.Clamp(height * .14f, .5f, .75f);
        var hull = InteriorVolume.ConvexHull2([.. side.Where(p => p.Y <= bottom + band).Select(p => new Vector2(p.X, -p.Z))]);
        if (hull.Length < 3) return null;

        var min = new Vector2(hull.Min(p => p.X), hull.Min(p => p.Y));
        var max = new Vector2(hull.Max(p => p.X), hull.Max(p => p.Y));
        var size = max - min;
        if (size.X is < .4f or > 4 || size.Y is < 1.2f or > 7) return null;

        // A thin border around the outline keeps the alpha transparent at the texture's edges.
        var half = size * .54f;
        var centre = (min + max) * .5f;
        for (int i = 0; i < hull.Length; i++) hull[i] = (hull[i] - centre) / half;
        return new Baked(FootprintTexture.BakeBoot(hull, flat: height < 1.6f), half.X, half.Y);
    }

    /// <summary>
    /// How high (units) above a body's lowest point a triangle still belongs to its sole: the skin that meets the ground, not the foot's sides.
    /// </summary>
    const float BareBand = .45f;

    /// <summary>
    /// A mesh whose lowest point is higher than this (units over the model's feet) has no foot: a limb's cap, not a leg (the Human Leg's lies at 7.4).
    /// </summary>
    const float NoFootAbove = 2;

    /// <summary>
    /// Whether the mesh was read and has no foot of its own (<see cref="NoFootAbove"/>): its wearer's prints come from another mesh.
    /// </summary>
    public bool Footless(string path) => _models.TryGetValue(Key(path, true), out var entry)
        && entry.Source.IsCompletedSuccessfully && entry.Source.Result is [null, null];

    /// <summary>
    /// A bare sole at least this many times as long as wide; a squatter one (a band catching part of a skeleton's foot) takes the generic mask.
    /// </summary>
    const float MinBareAspect = 1.6f;

    /// <summary>
    /// Every triangle of the mesh in model space.
    /// </summary>
    static List<(Vector3 A, Vector3 B, Vector3 C)> Triangles(OgreMesh mesh)
    {
        var triangles = new List<(Vector3, Vector3, Vector3)>();
        foreach (var part in mesh.SubMeshes)
        {
            if ((part.UseSharedVertices ? mesh.SharedVertexData : part.VertexData)?.ReadPositions() is not { } positions) continue;

            var indices = part.Indices.Indices;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if (a < positions.Length && b < positions.Length && c < positions.Length) triangles.Add((positions[a], positions[b], positions[c]));
            }
        }
        return triangles;
    }

    /// <summary>
    /// A bare foot's mask: the body's triangles lying wholly in the bottom band of that foot (the sole, toes and heel as modelled), seen from below
    /// in the same model axes as the footwear.
    /// </summary>
    static Baked? BakeBare(List<(Vector3 A, Vector3 B, Vector3 C)> triangles, bool right)
    {
        var side = triangles.Where(t => OnSide((t.A + t.B + t.C) / 3, right)).ToArray();
        if (side.Length == 0) return null;

        float bottom = side.Min(t => MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)));
        var sole = side
            .Where(t => MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)) <= bottom + BareBand)
            .Select(t => (A: Flat(t.A), B: Flat(t.B), C: Flat(t.C)))
            .ToArray();
        if (sole.Length == 0) return null;

        var points = sole.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        var min = new Vector2(points.Min(p => p.X), points.Min(p => p.Y));
        var max = new Vector2(points.Max(p => p.X), points.Max(p => p.Y));
        var size = max - min;
        // A Hive's foot is a pointed tip, about 0.4 x 0.85 units: small soles count.
        if (size.X is < .2f or > 4 || size.Y is < .5f or > 7 || size.Y < MinBareAspect * size.X) return null;

        var half = size * .54f;
        var centre = (min + max) * .5f;
        var normalized = sole.Select(t => ((t.A - centre) / half, (t.B - centre) / half, (t.C - centre) / half)).ToArray();
        var hull = InteriorVolume.ConvexHull2([.. normalized.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 })]);
        return new Baked(FootprintTexture.BakeBare(normalized, hull), half.X, half.Y);
    }

    /// <summary>
    /// A model point on the mask's plane, as the footwear's hull takes it.
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, -p.Z);

    /// <summary>
    /// A finite vertex on the shoe's own half: negative X is the right shoe.
    /// </summary>
    static bool OnSide(Vector3 p, bool right) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && (right ? p.X < 0 : p.X >= 0);

    /// <summary>
    /// Drops uploaded masks and their bindings. In-flight tasks only produce CPU data.
    /// </summary>
    public void Dispose()
    {
        foreach (var model in _models.Values)
        foreach (var sprite in model.Sprites)
        {
            if (sprite is null) continue;
            if (sprite.Index != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, sprite.Index);
            sprite.Image.Dispose();
        }
        _models.Clear();
    }
}
