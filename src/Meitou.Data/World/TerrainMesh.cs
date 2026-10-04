using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Data.World;

/// <summary>
/// A rectangular window of heightmap samples in memory, taken every <see cref="Step"/>-th sample. Window
/// point (i, j) is heightmap sample (<see cref="Column0"/> + i·step, <see cref="Row0"/> + j·step); samples past
/// the heightmap's edge repeat the edge. Column follows world +X, row world +Z (docs/formats/terrain.md).
/// </summary>
public sealed class HeightWindow
{
    public HeightWindow(int column0, int row0, int step, int columns, int rows, ushort[] raw)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        if (raw.Length != columns * rows) throw new ArgumentException($"{raw.Length} samples for a {columns}x{rows} window.", nameof(raw));
        (Column0, Row0, Step, Columns, Rows, Raw) = (column0, row0, step, columns, rows, raw);
    }

    /// <summary>Heightmap column and row of window point (0, 0); may lie outside the heightmap.</summary>
    public int Column0 { get; }
    public int Row0 { get; }
    /// <summary>Heightmap samples between neighbouring window points.</summary>
    public int Step { get; }
    public int Columns { get; }
    public int Rows { get; }
    /// <summary>Raw samples, row-major.</summary>
    public ushort[] Raw { get; }

    /// <summary>World units between neighbouring window points.</summary>
    public float Spacing => Step * WorldLayout.SampleSpacing;

    /// <summary>World height at window point (i, j), clamped to the window.</summary>
    public float Height(int i, int j) =>
        WorldLayout.RawToHeight(Raw[Math.Clamp(j, 0, Rows - 1) * Columns + Math.Clamp(i, 0, Columns - 1)]);

    /// <summary>World X/Z of window point (i, j).</summary>
    public (double X, double Z) WorldOf(int i, int j) => WorldLayout.FromSample(Column0 + (double)i * Step, Row0 + (double)j * Step);

    /// <summary>World position of window point (i, j).</summary>
    public Vector3 Position(int i, int j)
    {
        var (x, z) = WorldOf(i, j);
        return new Vector3((float)x, Height(i, j), (float)z);
    }

    /// <summary>
    /// Surface normal at window point (i, j) from central differences of the window's own heights (one-sided at
    /// the window edge). For y = h(x, z) the normal is (−∂h/∂x, 1, −∂h/∂z), normalised.
    /// </summary>
    public Vector3 Normal(int i, int j)
    {
        int i0 = Math.Max(i - 1, 0), i1 = Math.Min(i + 1, Columns - 1);
        int j0 = Math.Max(j - 1, 0), j1 = Math.Min(j + 1, Rows - 1);
        float dx = (Height(i1, j) - Height(i0, j)) / Math.Max((i1 - i0) * Spacing, 1e-6f);
        float dz = (Height(i, j1) - Height(i, j0)) / Math.Max((j1 - j0) * Spacing, 1e-6f);
        return Vector3.Normalize(new Vector3(-dx, 1, -dz));
    }
}

/// <summary>The terrain renderer's vertex: world position and normal.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TerrainVertex
{
    public Vector3 Position;
    public Vector3 Normal;

    public static readonly int Size = Marshal.SizeOf<TerrainVertex>();
}

/// <summary>
/// A square piece of terrain: a (<see cref="TerrainMesh.Cells"/> + 1)² grid of window points, followed by skirt
/// vertices (the four edges again, lowered) that hide cracks where neighbouring chunks use different LODs.
/// </summary>
public sealed class TerrainChunk
{
    /// <summary>Window point of the chunk's first grid vertex.</summary>
    public required int I0 { get; init; }
    public required int J0 { get; init; }
    public required TerrainVertex[] Vertices { get; init; }
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }
    public Vector3 Center => (Min + Max) / 2;
}

/// <summary>
/// Builds chunked terrain meshes from a <see cref="HeightWindow"/>. All chunks of a mesh share one vertex layout,
/// so the index buffers (one per LOD) are shared too: LOD <c>l</c> uses every 2^l-th grid vertex.
/// </summary>
public sealed class TerrainMesh
{
    /// <param name="cells">Grid cells per chunk side; a power of two so every LOD divides it.</param>
    /// <param name="skirtDepth">How far the skirts hang below the chunk edge, in world units.</param>
    public TerrainMesh(HeightWindow window, int cells = 64, float skirtDepth = 200)
    {
        if (cells < 1 || (cells & (cells - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(cells), "must be a power of two");
        Window = window;
        Cells = cells;
        SkirtDepth = skirtDepth;
        int chunksX = Math.Max(1, (window.Columns - 1 + cells - 1) / cells);
        int chunksZ = Math.Max(1, (window.Rows - 1 + cells - 1) / cells);
        ChunksX = chunksX;
        ChunksZ = chunksZ;
        LodCount = (int)Math.Log2(cells) + 1;
        Indices = [.. Enumerable.Range(0, LodCount).Select(l => BuildIndices(cells, 1 << l))];
    }

    public HeightWindow Window { get; }
    public int Cells { get; }
    public float SkirtDepth { get; }
    public int ChunksX { get; }
    public int ChunksZ { get; }
    public int LodCount { get; }

    /// <summary>Vertices per chunk: the grid, then 4 × (cells + 1) skirt vertices.</summary>
    public int VerticesPerChunk => (Cells + 1) * (Cells + 1) + 4 * (Cells + 1);

    /// <summary>Triangle-list index buffers per LOD (0 = full detail), counter-clockwise seen from above.</summary>
    public uint[][] Indices { get; }

    /// <summary>World edge length of one chunk.</summary>
    public float ChunkSize => Cells * Window.Spacing;

    public IEnumerable<TerrainChunk> Chunks()
    {
        for (int cz = 0; cz < ChunksZ; cz++)
            for (int cx = 0; cx < ChunksX; cx++)
                yield return BuildChunk(cx, cz);
    }

    public TerrainChunk BuildChunk(int cx, int cz)
    {
        int n = Cells + 1, i0 = cx * Cells, j0 = cz * Cells;
        var vertices = new TerrainVertex[VerticesPerChunk];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var p = Window.Position(i0 + i, j0 + j);
                vertices[j * n + i] = new TerrainVertex { Position = p, Normal = Window.Normal(i0 + i, j0 + j) };
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        // Skirts: edge j = 0, edge j = cells, edge i = 0, edge i = cells, each in order of the free coordinate.
        int s = n * n;
        for (int edge = 0; edge < 4; edge++)
            for (int k = 0; k < n; k++)
            {
                var v = vertices[EdgeVertex(edge, k, n)];
                v.Position.Y -= SkirtDepth;
                vertices[s + edge * n + k] = v;
            }
        min.Y -= SkirtDepth;
        return new TerrainChunk { I0 = i0, J0 = j0, Vertices = vertices, Min = min, Max = max };
    }

    static int EdgeVertex(int edge, int k, int n) => edge switch
    {
        0 => k,                   // j = 0
        1 => (n - 1) * n + k,     // j = cells
        2 => k * n,               // i = 0
        _ => k * n + n - 1,       // i = cells
    };

    /// <summary>Index buffer for one LOD: the grid every <paramref name="stride"/> vertices, plus the skirts (both windings).</summary>
    public static uint[] BuildIndices(int cells, int stride)
    {
        int n = cells + 1;
        var list = new List<uint>();
        uint V(int i, int j) => (uint)(j * n + i);
        for (int j = 0; j < cells; j += stride)
            for (int i = 0; i < cells; i += stride)
            {
                // (i, j) -> (i, j+1) -> (i+1, j): the face normal (cross product) points up, +Y.
                list.AddRange([V(i, j), V(i, j + stride), V(i + stride, j)]);
                list.AddRange([V(i + stride, j), V(i, j + stride), V(i + stride, j + stride)]);
            }
        int s = n * n;
        for (int edge = 0; edge < 4; edge++)
            for (int k = 0; k < cells; k += stride)
            {
                uint a = (uint)EdgeVertex(edge, k, n), b = (uint)EdgeVertex(edge, k + stride, n);
                uint c = (uint)(s + edge * n + k), d = (uint)(s + edge * n + k + stride);
                list.AddRange([a, c, b, b, c, d]);
                list.AddRange([a, b, c, b, d, c]);
            }
        return [.. list];
    }

    /// <summary>Grid triangles of an index buffer (skirts excluded): the first entries, two per grid quad.</summary>
    public static int GridTriangleCount(int cells, int stride) => 2 * (cells / stride) * (cells / stride);
}
