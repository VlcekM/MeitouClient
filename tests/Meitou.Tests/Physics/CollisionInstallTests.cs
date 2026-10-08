using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Physics;
using Meitou.Navigation;

namespace Meitou.Tests.Physics;

/// <summary>The collision reader against the Kenshi install (docs/formats/collision.md): counts, decoding and the placement convention.</summary>
[Slow]
public class CollisionInstallTests(ITestOutputHelper output)
{
    /// <summary>The BUILDING_PART records reachable from the base game's BUILDING records through parts, interior, interior mask and doors.</summary>
    static List<GameRecord> Parts(GameDatabase db)
    {
        var seen = new HashSet<string>();
        var result = new List<GameRecord>();
        void Visit(GameRecord holder)
        {
            foreach (var list in new[] { "parts", "interior" })
                foreach (var r in holder.GetReferences(list))
                    if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING_PART } part && seen.Add(part.StringId))
                    {
                        result.Add(part);
                        Visit(part);
                    }
        }
        foreach (var b in db.OfType(FcsRecordType.BUILDING)) Visit(b);
        return result;
    }

    [Fact]
    public void Every_xml_collision_file_in_the_install_parses_with_the_documented_counts()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var files = Directory.EnumerateFiles(install!.DataDirectory, "*.xml", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).Equals(".xml", StringComparison.OrdinalIgnoreCase)).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}gui{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}editor{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadLines(f).Take(3).Any(l => l.Contains("<NXUSTREAM2>", StringComparison.Ordinal)))
            .ToList();
        Assert.Equal(1136, files.Count);

        var kinds = new int[6];
        int convex = 0, triangle = 0;
        foreach (var f in files)
        {
            var file = CollisionFile.ReadFile(f);
            convex += file.ConvexMeshCount;
            triangle += file.TriangleMeshCount;
            foreach (var s in file.Shapes)
            {
                kinds[(int)s.Kind]++;
                if (s.Mesh is { } m)
                {
                    Assert.NotEmpty(m.Vertices);
                    if (s.Kind == CollisionShapeKind.TriangleMesh) Assert.All(m.Indices, i => Assert.InRange(i, 0, m.Vertices.Length - 1));
                    else Assert.NotEmpty(ConvexHull.Triangles(m.Vertices));
                }
                var v = new List<Vector3>();
                var idx = new List<int>();
                CollisionTriangulator.Triangulate(s, v, idx);
                Assert.All(v, p => Assert.True(float.IsFinite(p.X + p.Y + p.Z)));
            }
        }
        Assert.Equal(1335, convex);
        Assert.Equal(376, triangle);
        Assert.Equal(2066, kinds[(int)CollisionShapeKind.Box]);
        Assert.Equal(1335, kinds[(int)CollisionShapeKind.Convex]);
        Assert.Equal(376, kinds[(int)CollisionShapeKind.TriangleMesh]);
        Assert.Equal(245, kinds[(int)CollisionShapeKind.Capsule]);
        Assert.Equal(91, kinds[(int)CollisionShapeKind.Plane]);
        Assert.Equal(3, kinds[(int)CollisionShapeKind.Sphere]);
    }

    [Fact]
    public void Closed_triangle_meshes_are_counted_by_winding()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var counts = new int[3];
        foreach (var f in Directory.EnumerateFiles(install!.DataDirectory, "*.xml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}gui{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}editor{Path.DirectorySeparatorChar}"))
                     .Where(f => File.ReadLines(f).Take(3).Any(l => l.Contains("<NXUSTREAM2>", StringComparison.Ordinal))))
            foreach (var s in CollisionFile.ReadFile(f).Shapes.Where(s => s.Kind == CollisionShapeKind.TriangleMesh))
            {
                var v = new List<Vector3>();
                var idx = new List<int>();
                CollisionTriangulator.Triangulate(s, v, idx);
                counts[(int)CollisionCache.FixClosedWinding(v.Select(CollisionTriangulator.ToWorldAxes).ToArray(), idx.ToArray())]++;
            }
        output.WriteLine($"triangle meshes: {counts[0]} open, {counts[1]} closed and outward, {counts[2]} closed and flipped");
        Assert.Equal([146, 229, 1], counts);
    }

    [Fact]
    public void Records_name_546_building_parts_with_collision_and_354_foliage_meshes()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var parts = Parts(db);
        Assert.Equal(1019, parts.Count);
        var named = parts.Where(p => p.GetPath("xml collision").Length > 0).ToList();
        Assert.Equal(546, named.Count);
        var missing = named.Where(p => CollisionPaths.Resolve(install!, p.GetPath("xml collision")) is null).ToList();
        Assert.Equal(2, missing.Count);
        Assert.Equal(2, missing.Select(p => p.GetPath("xml collision").ToLowerInvariant()).Distinct().Count());
        Assert.Equal(7, parts.Count(p => p.GetPath("destroyed collision").Length > 0));
        int empty = 0;
        foreach (var p in named.Except(missing))
            if (CollisionFile.ReadFile(CollisionPaths.Resolve(install!, p.GetPath("xml collision"))!).Shapes.Count == 0) empty++;
        Assert.Equal(1, empty);

        var foliage = db.OfType(FcsRecordType.FOLIAGE_MESH).Where(f => f.GetPath("collision").Length > 0).ToList();
        Assert.Equal(354, foliage.Count);
        var foliageMissing = foliage.Where(f => CollisionPaths.Resolve(install!, f.GetPath("collision")) is null).ToList();
        Assert.Equal(3, foliageMissing.Count);
        Assert.Equal(2, foliageMissing.Select(f => f.GetPath("collision").ToLowerInvariant()).Distinct().Count());
        int foliageEmpty = 0;
        foreach (var f in foliage.Except(foliageMissing))
            if (CollisionFile.ReadFile(CollisionPaths.Resolve(install!, f.GetPath("collision"))!).Shapes.Count == 0) foliageEmpty++;
        Assert.Equal(1, foliageEmpty);
    }

    [Fact]
    public void Collision_boxes_overlap_their_render_meshes_under_the_documented_axis_map()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var documented = MeshOverlap(install!, CollisionTriangulator.ToWorldAxes);
        Assert.True(documented.Count >= 380, $"only {documented.Count} parts compared");
        float mean = documented.Average(), median = documented[documented.Count / 2];
        int above = documented.Count(x => x > 0.5f);
        Assert.True(mean > 0.55f, $"mean IoU {mean}");
        Assert.True(median > 0.62f, $"median IoU {median}");
        Assert.True(above > documented.Count * 0.6, $"{above} of {documented.Count} above 0.5");

        // The wrong axis conventions score clearly worse (docs/formats/collision.md compared twelve).
        var identity = MeshOverlap(install!, p => p);
        var flipped = MeshOverlap(install!, p => new Vector3(p.X, -p.Z, p.Y));
        Assert.True(identity.Average() < mean - 0.15f, $"identity {identity.Average()} vs {mean}");
        Assert.True(flipped.Average() < mean - 0.15f, $"flipped {flipped.Average()} vs {mean}");
    }

    /// <summary>IoU of each part's collision AABB (under <paramref name="axes"/>) with its render mesh's bounds, sorted ascending; parts with a mesh, a collision file and no offset.</summary>
    static List<float> MeshOverlap(GameInstall install, Func<Vector3, Vector3> axes)
    {
        var db = InstallData.BaseGame!;
        var meshIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(install.DataDirectory, "*.mesh", SearchOption.AllDirectories)) meshIndex.TryAdd(Path.GetFileName(f), f);

        var ious = new List<float>();
        foreach (var part in Parts(db))
        {
            string xml = part.GetPath("xml collision"), meshName = part.GetPath("phs or mesh");
            if (xml.Length == 0 || !meshName.Contains(".mesh", StringComparison.Ordinal)) continue;
            if (part.GetFloat("offset X") != 0 || part.GetFloat("offset Y") != 0 || part.GetFloat("offset Z") != 0) continue;
            if (CollisionPaths.Resolve(install, xml) is not { } xmlPath) continue;
            if (!meshIndex.TryGetValue(Path.GetFileName(meshName.Replace('\\', '/')), out var meshPath)) continue;
            var bounds = OgreMeshReader.ReadFile(meshPath).Bounds;
            if (bounds is not { } b) continue;

            var v = new List<Vector3>();
            foreach (var s in CollisionFile.ReadFile(xmlPath).Shapes) CollisionTriangulator.Triangulate(s, v, []);
            if (v.Count == 0) continue;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in v)
            {
                var w = axes(p);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }
            var lo = Vector3.Max(min, b.Min);
            var hi = Vector3.Min(max, b.Max);
            var inter = Vector3.Max(hi - lo, Vector3.Zero);
            float interVolume = inter.X * inter.Y * inter.Z;
            float union = Volume(max - min) + Volume(b.Max - b.Min) - interVolume;
            ious.Add(union > 0 ? interVolume / union : 0);
        }
        ious.Sort();
        return ious;
    }

    static float Volume(Vector3 size) => Math.Max(size.X, 0) * Math.Max(size.Y, 0) * Math.Max(size.Z, 0);
}
