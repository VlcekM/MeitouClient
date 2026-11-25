using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Terrain;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// A square height-field grid of <c>cells</c> x <c>cells</c> quads of <c>spacing</c> round a centre for the Metal traced scene, as
/// <see cref="GiScene"/>'s grid (docs/render-gi.md "Scene and lifetime"): sampled on a worker (the fine grid along the drawn ground's smooth
/// surface), then built into a bottom level; sampled again when the eye leaves the middle eighth.
/// </summary>
sealed unsafe class MetalTerrainGrid(MetalContext ctx, string name, int cells, float spacing) : IDisposable
{
    /// <summary>
    /// The grid's index buffer (the same triangles for every build; null until the first).
    /// </summary>
    MTLBuffer _indices;

    /// <summary>
    /// The sampling running on a worker (null: none).
    /// </summary>
    Task<(Vector3 Centre, Vector3[] Positions)>? _job;

    /// <summary>
    /// The centre of the newest grid asked for.
    /// </summary>
    Vector3 _requested;

    /// <summary>
    /// Whether a grid was asked for yet.
    /// </summary>
    bool _hasRequested;

    /// <summary>
    /// The grid's bottom level, its vertex buffer and its record (null before the first build).
    /// </summary>
    public MetalBottomLevel? Bottom { get; private set; }

    /// <summary>
    /// The centre the bottom level was built at.
    /// </summary>
    public Vector3 Centre { get; private set; }

    /// <summary>
    /// Whether this is the fine grid (spacing under 100).
    /// </summary>
    public bool Fine => spacing < 100;

    /// <summary>
    /// Whether the last <see cref="Update"/> built a new bottom level.
    /// </summary>
    public bool Built { get; private set; }

    /// <summary>
    /// The grid's side in world units.
    /// </summary>
    float Side => cells * spacing;

    /// <summary>
    /// Builds a finished grid into <paramref name="builds"/> and starts sampling a new one when the eye left the middle eighth of the last; the first
    /// grid is made at once.
    /// </summary>
    public void Update(MetalBuilds builds, Vector3 eye, HeightSnapshot heights)
    {
        Built = false;
        if (_job is { IsCompleted: true }) Install(builds);

        var wanted = new Vector3(MathF.Round(eye.X / spacing) * spacing, 0, MathF.Round(eye.Z / spacing) * spacing);
        bool far = !_hasRequested || MathF.Max(MathF.Abs(wanted.X - _requested.X), MathF.Abs(wanted.Z - _requested.Z)) > Side / 8;
        if (!far || _job is not null) return;

        _hasRequested = true;
        _requested = wanted;
        _job = Task.Run(() => (wanted, Sample(wanted, heights)));

        // The first grid at once: the first frames see the terrain.
        if (Bottom is null) Install(builds);
    }

    /// <summary>
    /// Waits for the sampling and builds its grid.
    /// </summary>
    void Install(MetalBuilds builds)
    {
        var (centre, positions) = _job!.Result;
        _job = null;
        Build(builds, centre, positions);
    }

    /// <summary>
    /// The heights round <paramref name="centre"/>, a vertex per grid corner, relative to it.
    /// </summary>
    Vector3[] Sample(Vector3 centre, HeightSnapshot heights)
    {
        int n = cells + 1;
        var p = new Vector3[n * n];
        float half = Side / 2;

        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                float x = centre.X - half + i * spacing, z = centre.Z - half + j * spacing;
                float y = Fine ? heights.SmoothHeightAt(x, z) : heights.HeightAt(x, z);
                p[j * n + i] = new Vector3(x - centre.X, y, z - centre.Z);
            }
        }

        return p;
    }

    /// <summary>
    /// A new vertex buffer and bottom level for the grid at <paramref name="centre"/>; the old ones go after the frames in flight.
    /// </summary>
    void Build(MetalBuilds builds, Vector3 centre, Vector3[] positions)
    {
        if (_indices.NativePtr == IntPtr.Zero) _indices = NewIndices();

        var vertices = ctx.Device.NewBuffer((ulong)(positions.Length * 12), MTLResourceOptions.ResourceStorageModeShared);
        vertices.Label = (NSString)(name + " vertices");
        MemoryMarshal.AsBytes(positions.AsSpan()).CopyTo(new Span<byte>((void*)vertices.Contents, positions.Length * 12));

        var geometry = new MTLAccelerationStructureTriangleGeometryDescriptor
        {
            VertexBuffer = vertices,
            VertexStride = 12,
            VertexFormat = MTLAttributeFormat.Float3,
            IndexBuffer = _indices,
            IndexType = MTLIndexType.UInt32,
            TriangleCount = (ulong)(cells * cells * 2),
            Opaque = true,
        };
        var structure = builds.Bottom(name, [geometry]);
        var record = new GeometryRecord { Vertices = vertices.GpuAddress, Indices = _indices.GpuAddress, Stride = 12, Kind = GeometryRecord.Terrain };

        if (Bottom is { } old) ctx.DeferDelete(old.Dispose);

        Bottom = new MetalBottomLevel(structure, [record], [0], [vertices.NativePtr, _indices.NativePtr], [vertices]);
        Centre = new Vector3(centre.X, 0, centre.Z);
        Built = true;
    }

    /// <summary>
    /// The grid's triangles, two a cell, their front up (rays from under the ground pass up through it).
    /// </summary>
    MTLBuffer NewIndices()
    {
        int n = cells + 1;
        var idx = new uint[cells * cells * 6];
        int k = 0;

        for (int j = 0; j < cells; j++)
        {
            for (int i = 0; i < cells; i++)
            {
                uint a = (uint)(j * n + i), b = a + 1, c = a + (uint)n, d = c + 1;
                idx[k++] = a;
                idx[k++] = c;
                idx[k++] = b;
                idx[k++] = b;
                idx[k++] = c;
                idx[k++] = d;
            }
        }

        var buffer = ctx.Device.NewBuffer((ulong)idx.Length * 4, MTLResourceOptions.ResourceStorageModeShared);
        buffer.Label = (NSString)(name + " indices");
        MemoryMarshal.AsBytes(idx.AsSpan()).CopyTo(new Span<byte>((void*)buffer.Contents, idx.Length * 4));
        return buffer;
    }

    /// <summary>
    /// The grid's size, for the log ("none" before it is built).
    /// </summary>
    public string Describe() => Bottom is null ? "none" : $"{cells}² × {spacing:0}";

    /// <summary>
    /// Waits for the sampling and frees the structure and buffers.
    /// </summary>
    public void Dispose()
    {
        _job?.Wait();
        Bottom?.Dispose();
        if (_indices.NativePtr != IntPtr.Zero) _indices.Dispose();
    }
}
