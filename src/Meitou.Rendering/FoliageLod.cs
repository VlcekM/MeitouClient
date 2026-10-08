using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Meitou.Rendering.Characters;

namespace Meitou.Rendering;

/// <summary>
/// Generated mesh levels of one foliage mesh (docs/formats/foliage.md, "Generated levels"). The game ships no LOD levels for these meshes; the levels are
/// Meitou's own, made by <see cref="MeshSimplifier"/> (so every level indexes the part's one vertex buffer) and kept with the deviation each one has from the
/// original surface, in mesh units. Level 0 is the original and is not stored.
/// </summary>
public sealed class FoliageLodSet
{
    /// <summary>Per level above 0: the part's index list (<c>[part][level - 1]</c>); parts with nothing to reduce repeat their original.</summary>
    public required uint[][][] Indices { get; init; }
    /// <summary>The deviation of each level, in mesh units (<c>Errors[0]</c> is 0): the largest distance between the original surface and the level's, both ways,
    /// over all parts, never decreasing with the level.</summary>
    public required float[] Errors { get; init; }
    /// <summary>Triangles per level, summed over the parts.</summary>
    public required int[] Triangles { get; init; }
    /// <summary>Levels including level 0.</summary>
    public int Levels => Errors.Length;
    /// <summary>Bytes of the levels' indices on the GPU.</summary>
    public long IndexBytes => Indices.Sum(p => p.Sum(l => (long)l.Length * sizeof(uint)));
}

/// <summary>
/// Makes <see cref="FoliageLodSet"/>s: <see cref="MeshSimplifier.Chain"/> to 50 %, 25 % and 10 % of the triangles (floor <see cref="Floor"/>), the deviation of each
/// level measured against the original, and levels that do not pay (hardly fewer triangles than the one before, or a deviation beyond
/// <see cref="MaxRelativeError"/> of the mesh's radius) left out.
/// </summary>
public static class FoliageLodBuilder
{
    /// <summary>Bumped when the simplifier, the fractions or the error measure change: it is part of the disk cache's key.</summary>
    public const int Version = 1;
    public static readonly float[] Fractions = [0.5f, 0.25f, 0.1f];
    /// <summary>The fewest triangles a level reduces a part to; a part with fewer than twice this is not reduced.</summary>
    public const int Floor = 48;
    /// <summary>A level whose deviation is more than this share of the mesh's radius is not kept (it could only ever show far away, where the level before is cheap enough).</summary>
    public const float MaxRelativeError = 0.25f;
    /// <summary>A level must have at most this share of the triangles of the level before it.</summary>
    public const float MinGain = 0.8f;
    const int MaxSamples = 1500;

    /// <summary>Builds the levels of a model (the parts with indices, in order). Null when no level pays.</summary>
    public static FoliageLodSet? Build(Model model, float radius)
    {
        var parts = model.Parts.Where(p => p.Indices.Length > 0).ToList();
        int steps = Fractions.Length;
        var chains = new uint[parts.Count][][];
        var errors = new float[parts.Count][];
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            if (part.Indices.Length / 3 < 2 * Floor)
            {
                chains[p] = [.. Enumerable.Repeat(part.Indices, steps)];
                errors[p] = new float[steps];
                continue;
            }
            chains[p] = MeshSimplifier.Chain(part.Vertices, part.Indices, Fractions, Floor);
            errors[p] = new float[steps];
            for (int k = 0; k < steps; k++)
                errors[p][k] = ReferenceEquals(chains[p][k], part.Indices) || chains[p][k].Length == part.Indices.Length ? 0 : Deviation(part.Vertices, part.Indices, chains[p][k]);
        }

        // Which of the steps become levels.
        var kept = new List<int>();
        long previous = parts.Sum(p => (long)p.Indices.Length / 3);
        for (int k = 0; k < steps; k++)
        {
            long triangles = 0;
            float error = 0;
            for (int p = 0; p < parts.Count; p++) { triangles += chains[p][k].Length / 3; error = Math.Max(error, errors[p][k]); }
            if (error > MaxRelativeError * radius) break;
            if (triangles > previous * MinGain) continue;
            kept.Add(k);
            previous = triangles;
        }
        if (kept.Count == 0) return null;

        var indices = new uint[parts.Count][][];
        var levelErrors = new float[kept.Count + 1];
        var levelTriangles = new int[kept.Count + 1];
        levelTriangles[0] = parts.Sum(p => p.Indices.Length / 3);
        for (int p = 0; p < parts.Count; p++) indices[p] = new uint[kept.Count][];
        float monotone = 0;
        for (int i = 0; i < kept.Count; i++)
        {
            int k = kept[i];
            float error = 0;
            for (int p = 0; p < parts.Count; p++)
            {
                indices[p][i] = chains[p][k];
                levelTriangles[i + 1] += chains[p][k].Length / 3;
                error = Math.Max(error, errors[p][k]);
            }
            monotone = Math.Max(monotone, error);
            levelErrors[i + 1] = monotone;
        }
        return new FoliageLodSet { Indices = indices, Errors = levelErrors, Triangles = levelTriangles };
    }

    // ---- the deviation of a level from the original surface ----

    /// <summary>
    /// The largest distance, both ways, between the surface of <paramref name="original"/> and of <paramref name="level"/>: from sampled points of the original
    /// (its used vertices and triangle centres) to the level's triangles, and from sampled points of the level (centres and edge midpoints) to the original's.
    /// A thin part that the level loses shows as a large distance from the first, a bridge it makes over a gap from the second.
    /// </summary>
    internal static float Deviation(Vertex[] vertices, uint[] original, uint[] level)
    {
        var originalTriangles = Triangles(vertices, original);
        var levelTriangles = Triangles(vertices, level);
        if (levelTriangles.Length == 0) return float.MaxValue;
        var fromOriginal = new List<Vector3>();
        var used = new HashSet<uint>();
        foreach (uint i in original) if (used.Add(i)) fromOriginal.Add(vertices[i].Position);
        foreach (var t in originalTriangles) fromOriginal.Add((t.A + t.B + t.C) / 3);
        var fromLevel = new List<Vector3>();
        foreach (var t in levelTriangles)
        {
            fromLevel.Add((t.A + t.B + t.C) / 3);
            fromLevel.Add((t.A + t.B) / 2);
            fromLevel.Add((t.B + t.C) / 2);
            fromLevel.Add((t.C + t.A) / 2);
        }
        float a = Farthest(Subsample(fromOriginal), levelTriangles);
        float b = Farthest(Subsample(fromLevel), originalTriangles);
        return Math.Max(a, b);
    }

    static List<Vector3> Subsample(List<Vector3> points)
    {
        if (points.Count <= MaxSamples) return points;
        var result = new List<Vector3>(MaxSamples);
        for (int i = 0; i < MaxSamples; i++) result.Add(points[(int)((long)i * points.Count / MaxSamples)]);
        return result;
    }

    readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C, Vector3 Min, Vector3 Max);

    static Tri[] Triangles(Vertex[] vertices, uint[] indices)
    {
        var result = new Tri[indices.Length / 3];
        for (int t = 0; t < result.Length; t++)
        {
            Vector3 a = vertices[indices[t * 3]].Position, b = vertices[indices[t * 3 + 1]].Position, c = vertices[indices[t * 3 + 2]].Position;
            result[t] = new Tri(a, b, c, Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)));
        }
        return result;
    }

    /// <summary>The largest over <paramref name="points"/> of the distance to the nearest of <paramref name="triangles"/>.</summary>
    static float Farthest(List<Vector3> points, Tri[] triangles)
    {
        float worst = 0;
        foreach (var p in points)
        {
            float best = float.MaxValue;
            foreach (ref readonly var t in triangles.AsSpan())
            {
                var q = Vector3.Max(Vector3.Zero, Vector3.Max(t.Min - p, p - t.Max));
                if (q.LengthSquared() >= best) continue;
                float d = DistanceSquared(p, t.A, t.B, t.C);
                if (d < best) best = d;
            }
            if (best > worst) worst = best;
        }
        return MathF.Sqrt(worst);
    }

    /// <summary>The squared distance from <paramref name="p"/> to a triangle (the closest point by the regions of its Voronoi diagram).</summary>
    internal static float DistanceSquared(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return ap.LengthSquared();
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return bp.LengthSquared();
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float v = d1 / (d1 - d3);
            return (p - (a + v * ab)).LengthSquared();
        }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return cp.LengthSquared();
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float w = d2 / (d2 - d6);
            return (p - (a + w * ac)).LengthSquared();
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            float w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return (p - (b + w * (c - b))).LengthSquared();
        }
        float denominator = 1 / (va + vb + vc);
        float vv = vb * denominator, ww = vc * denominator;
        return (p - (a + ab * vv + ac * ww)).LengthSquared();
    }
}

/// <summary>
/// The disk cache of generated levels: <c>%LOCALAPPDATA%\Meitou\lods\&lt;name&gt;_&lt;key&gt;.mlod</c> (overridden by <c>MEITOU_LOD_CACHE</c>; never inside the
/// repository), keyed by the mesh file's content hash and <see cref="FoliageLodBuilder.Version"/>. Written to a temporary name and moved into place. A mesh that
/// gets no level is cached too (an empty set), so it is not worked out again.
/// </summary>
public static class FoliageLodCache
{
    const uint Magic = 0x444F4C4D;   // "MLOD"

    public static string Root { get; set; } = Environment.GetEnvironmentVariable("MEITOU_LOD_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "lods");

    public static string PathFor(string name, byte[] fileBytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(fileBytes))[..24].ToLowerInvariant();
        var safe = new string(Path.GetFileNameWithoutExtension(name).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length > 40) safe = safe[..40];
        return Path.Combine(Root, $"{safe}_{hash}_v{FoliageLodBuilder.Version}.mlod");
    }

    /// <summary>The cached levels (null in the result's <c>Set</c>: the mesh has none), or <c>Hit</c> false when there is no readable file.</summary>
    public static (bool Hit, FoliageLodSet? Set) TryLoad(string path, int partCount)
    {
        try
        {
            if (!File.Exists(path)) return (false, null);
            using var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16));
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FoliageLodBuilder.Version) return (false, null);
            int levels = reader.ReadInt32(), parts = reader.ReadInt32();
            if (levels == 0) return (true, null);
            if (parts != partCount || levels < 2 || levels > 16) return (false, null);
            var errors = new float[levels];
            var triangles = new int[levels];
            for (int i = 0; i < levels; i++) errors[i] = reader.ReadSingle();
            for (int i = 0; i < levels; i++) triangles[i] = reader.ReadInt32();
            var indices = new uint[parts][][];
            for (int p = 0; p < parts; p++)
            {
                indices[p] = new uint[levels - 1][];
                bool wide = reader.ReadBoolean();
                for (int l = 0; l < levels - 1; l++)
                {
                    var list = new uint[reader.ReadInt32()];
                    for (int i = 0; i < list.Length; i++) list[i] = wide ? reader.ReadUInt32() : reader.ReadUInt16();
                    indices[p][l] = list;
                }
            }
            return (true, new FoliageLodSet { Indices = indices, Errors = errors, Triangles = triangles });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or OverflowException or OutOfMemoryException) { return (false, null); }
    }

    public static long Save(string path, FoliageLodSet? set, int partCount)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            using (var writer = new BinaryWriter(new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)))
            {
                writer.Write(Magic);
                writer.Write(FoliageLodBuilder.Version);
                writer.Write(set?.Levels ?? 0);
                writer.Write(partCount);
                if (set is not null)
                {
                    foreach (float e in set.Errors) writer.Write(e);
                    foreach (int t in set.Triangles) writer.Write(t);
                    foreach (var part in set.Indices)
                    {
                        bool wide = part.Any(l => l.Any(i => i > ushort.MaxValue));
                        writer.Write(wide);
                        foreach (var list in part)
                        {
                            writer.Write(list.Length);
                            foreach (uint i in list) { if (wide) writer.Write(i); else writer.Write((ushort)i); }
                        }
                    }
                }
            }
            File.Move(temporary, path, overwrite: true);
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}
