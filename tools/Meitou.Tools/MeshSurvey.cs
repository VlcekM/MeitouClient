using Meitou.Content;
using Meitou.Data.Ogre;

/// <summary>Reads every .mesh under data/ and reports failures and what the files contain.</summary>
static class MeshSurvey
{
    public static int Run(GameInstall install)
    {
        var files = Directory.EnumerateFiles(install.DataDirectory, "*.mesh", SearchOption.AllDirectories).ToList();
        var failures = new List<(string File, string Error)>();
        var versions = new Dictionary<string, int>();
        var elements = new Dictionary<string, int>();
        var skipped = new Dictionary<OgreMeshChunk, int>();
        int skinned = 0, withLod = 0, shared = 0, submeshes = 0, outOfBounds = 0, noBounds = 0;
        long vertices = 0, indices = 0;
        foreach (var file in files)
        {
            try
            {
                var mesh = OgreMeshReader.ReadFile(file);
                versions[mesh.Version] = versions.GetValueOrDefault(mesh.Version) + 1;
                if (mesh.SkeletonName is not null) skinned++;
                if (mesh.Lod is not null) withLod++;
                if (mesh.SharedVertexData is not null) shared++;
                foreach (var (k, v) in mesh.SkippedChunks) skipped[k] = skipped.GetValueOrDefault(k) + v;
                foreach (var vd in mesh.SubMeshes.Select(s => s.VertexData).Append(mesh.SharedVertexData).OfType<OgreVertexData>())
                {
                    vertices += vd.VertexCount;
                    foreach (var e in vd.Elements)
                    {
                        var key = $"{e.Semantic}/{e.Type}";
                        elements[key] = elements.GetValueOrDefault(key) + 1;
                    }
                }
                if (mesh.Bounds is { } b)
                    foreach (var vd in mesh.SubMeshes.Select(s => s.VertexData).OfType<OgreVertexData>())
                        foreach (var p in vd.ReadPositions() ?? [])
                        {
                            var tol = 1e-3f * (1 + (b.Max - b.Min).Length());
                            if (p.X < b.Min.X - tol || p.Y < b.Min.Y - tol || p.Z < b.Min.Z - tol || p.X > b.Max.X + tol || p.Y > b.Max.Y + tol || p.Z > b.Max.Z + tol)
                            {
                                outOfBounds++;
                                break;
                            }
                        }
                else noBounds++;
                foreach (var sub in mesh.SubMeshes)
                {
                    submeshes++;
                    indices += sub.Indices.Indices.Length;
                    uint count = (sub.UseSharedVertices ? mesh.SharedVertexData : sub.VertexData)?.VertexCount ?? 0;
                    if (sub.Indices.Indices.Any(i => i >= count))
                        throw new FormatException($"index out of range ({count} vertices)");
                }
            }
            catch (Exception e)
            {
                failures.Add((Path.GetRelativePath(install.DataDirectory, file), e.Message));
            }
        }

        Console.WriteLine($"{files.Count} meshes, {failures.Count} failed");
        foreach (var (k, v) in versions) Console.WriteLine($"  {k}: {v}");
        Console.WriteLine($"  {submeshes} submeshes, {vertices} vertices, {indices} indices; {skinned} skinned, {withLod} with LOD, {shared} with shared vertices");
        Console.WriteLine($"  vertex buffers with a position outside the mesh bounds: {outOfBounds}; meshes without bounds: {noBounds}");
        Console.WriteLine("  skipped chunks: " + string.Join(", ", skipped.Select(kv => $"{kv.Key} x{kv.Value}")));
        Console.WriteLine("  vertex elements:");
        foreach (var (k, v) in elements.OrderByDescending(kv => kv.Value)) Console.WriteLine($"    {k,-40} {v,6}");
        foreach (var (file, error) in failures.Take(20)) Console.WriteLine($"  FAIL {file}: {error}");
        return failures.Count == 0 ? 0 : 1;
    }
}
