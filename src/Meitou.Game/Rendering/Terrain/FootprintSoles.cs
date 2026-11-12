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
    sealed class Entry(string path)
    {
        /// <summary>
        /// The masks in left, right order.
        /// </summary>
        public readonly Task<Baked?[]> Source = Task.Run(() => Read(path));
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
    /// Starts loading the model before the walker leaves the pool.
    /// </summary>
    public void Prepare(string path)
    {
        if (_models.Count < MaxModels && !_models.ContainsKey(path)) _models.Add(path, new Entry(path));
    }

    /// <summary>
    /// Returns an available mask without waiting for file reads. Null uses the generic sole.
    /// </summary>
    public Sprite? For(string path, bool right)
    {
        Prepare(path);
        if (!_models.TryGetValue(path, out var entry) || !entry.Source.IsCompletedSuccessfully) return null;

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
    /// Collects positions from the original Ogre submeshes. Unknown or broken footwear can still use a generic tread.
    /// </summary>
    static Baked?[] Read(string path)
    {
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            var positions = mesh.SubMeshes
                .Select(part => part.UseSharedVertices ? mesh.SharedVertexData : part.VertexData)
                .Distinct()
                .SelectMany(data => data?.ReadPositions() ?? [])
                .ToArray();
            return [Bake(positions, false), Bake(positions, true)];
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException
            or IndexOutOfRangeException or KeyNotFoundException or UnauthorizedAccessException or OverflowException)
        {
            return [null, null];
        }
    }

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
