using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// A mesh's generated levels on the GPU: one index buffer per part holding its levels 1 and up, bound with the part's own vertex buffer.
/// </summary>
sealed class FoliageRockLod
{
    /// <summary>
    /// All the levels the builder made (counting 0), and those it made before 2026-10-09 (shading deviation up to 0.6 rad). <see cref="Levels"/>
    /// is the former, or the latter while <see cref="Capped"/> (the lod-far switch off, so one A/B shows the whole change).
    /// </summary>
    public required int AllLevels;

    /// <summary>
    /// The levels made before 2026-10-09 (<see cref="AllLevels"/>).
    /// </summary>
    public int LegacyLevels;

    /// <summary>
    /// Only the <see cref="LegacyLevels"/> are used (the lod-far switch off).
    /// </summary>
    public bool Capped;

    /// <summary>
    /// The deviation of each level over the mesh's radius (<c>[0]</c> is 0), never decreasing.
    /// </summary>
    public required float[] Relative;

    /// <summary>
    /// Per mesh part its levels' indices.
    /// </summary>
    public required FoliagePartLod[] Parts;

    /// <summary>
    /// The levels as the builder (or the cache) made them.
    /// </summary>
    public required FoliageLodSet Set;

    /// <summary>
    /// Per level (counting 0) the mean triangle edge over the mesh's radius (<see cref="FoliageScreenLod.SubPixelDistance"/>'s input); 0 for a
    /// mesh of unknown area.
    /// </summary>
    public float[] TriangleExtent = [];

    /// <summary>
    /// The GPU bytes of the levels' index buffers.
    /// </summary>
    public long Bytes;

    /// <summary>
    /// The last <see cref="Effective"/> result and the settings it was worked out for.
    /// </summary>
    float[]? _effective;

    /// <summary>
    /// The settings <see cref="_effective"/> was worked out for.
    /// </summary>
    (float Tolerance, float Pixels, float Multiple, int Levels) _effectiveKey;

    /// <summary>
    /// The levels in use: <see cref="AllLevels"/>, or <see cref="LegacyLevels"/> while <see cref="Capped"/>.
    /// </summary>
    public int Levels => Capped ? LegacyLevels : AllLevels;

    /// <summary>
    /// The deviations with the size of the triangles counted (<see cref="FoliageScreenLod.EffectiveErrors"/>), kept until a setting changes.
    /// </summary>
    public float[] Effective(float tolerance, float triPixels, float multiple)
    {
        var key = (tolerance, triPixels, multiple, Levels);
        if (_effective is null || _effectiveKey != key)
        {
            var subPixel = new float[Levels];
            for (int k = 0; k < Levels; k++) subPixel[k] = FoliageScreenLod.SubPixelDistance(TriangleExtent[k], triPixels);
            _effective = FoliageScreenLod.EffectiveErrors(Relative.AsSpan(0, Levels).ToArray(), subPixel, tolerance, multiple);
            _effectiveKey = key;
        }
        return _effective;
    }

    /// <summary>
    /// Disposes the index buffers (freed after the frames in flight).
    /// </summary>
    public void Dispose()
    {
        foreach (var p in Parts) p.Indices.Dispose();   // freed after the frames in flight
    }
}

/// <summary>
/// One mesh part's generated levels: one index buffer holding levels 1 and up, bound with the part's own vertex buffer.
/// </summary>
sealed class FoliagePartLod
{
    /// <summary>
    /// The levels' indices.
    /// </summary>
    public required DeviceBuffer Indices;

    /// <summary>
    /// The part's vertices with <see cref="Indices"/>.
    /// </summary>
    public required MeshBindings Bindings;

    /// <summary>
    /// Per level (index 0 unused): the first index and the index count in <see cref="Indices"/>.
    /// </summary>
    public required int[] First, Count;
}
