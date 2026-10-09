using System.Diagnostics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.ModelViewer;

/// <summary><c>--card-trim-survey [name-filter]</c>: trims every alpha-tested foliage mesh of the game data and prints what it saves (docs/render-foliage.md, "Card trimming").</summary>
static class CardTrimSurvey
{
    public static int Run(string[] args)
    {
        var install = GameInstall.Locate();
        if (install is null) { Console.Error.WriteLine("Kenshi install not found."); return 1; }
        string? filter = args.SkipWhile(a => a != "--card-trim-survey").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
        bool verbose = args.Contains("--verbose");
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var catalog = FoliageCatalog.Load(db);
        var assets = new AssetLocator(install);
        string? Find(string? name) => name is null ? null : assets.Find(Path.GetFileName(name.Replace('\\', '/'))) ?? assets.Find(name);
        if (args.Contains("--grass"))
        {
            foreach (var grass in catalog.Layers.Values.SelectMany(l => l.Grass).Select(g => g.Grass).DistinctBy(g => g.Sprite))
            {
                var path = Find(grass.Sprite);
                if (path is null || FoliageCardMask.Obtain(path, 0.6f) is not { } m) continue;
                var square = new System.Numerics.Vector2[] { new(0, 0), new(m.Width, 0), new(m.Width, m.Height), new(0, m.Height) };
                var line = $"{grass.Name,-36} {Path.GetFileName(path),-30} {m.Width}x{m.Height} cross {grass.CrossQuads} opaque {m.Coverage(0):0.00}";
                for (int level = 0; level < Math.Min(m.LevelCount, 4); level++)
                    foreach (int vertices in new[] { 4, 6 })
                    {
                        var o = FoliageCardTrimmer.Outline(square, m, level, new FoliageCardTrimOptions { MaxVertices = vertices, MinSaving = 0 }, 2);
                        line += $"  L{level}/{vertices}v " + (o is null ? "none" : o.Length == 0 ? "empty" : $"{Math.Abs(FoliageCardTrimmer.SignedArea(o)) / (m.Width * m.Height):0.00}");
                    }
                Console.WriteLine(line);
            }
            return 0;
        }
        var total = default(FoliageCardTrimStats);
        var seen = new HashSet<string>();
        var watch = Stopwatch.StartNew();
        int meshes = 0;
        foreach (var mesh in catalog.Meshes.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (filter is not null && !mesh.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !mesh.MeshPath.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (path, diffuse, normal, threshold) in new[]
            {
                (mesh.MeshPath, mesh.Texture, mesh.Normal, mesh.MaterialType == 4 ? mesh.AlphaThreshold / 255f : 0f),
                (mesh.LeavesMesh ?? "", mesh.LeavesTexture, mesh.LeavesNormal, mesh.LeavesMesh is null ? 0f : mesh.LeavesAlphaThreshold / 255f),
            })
            {
                if (path.Length == 0 || threshold <= 0 || Find(diffuse) is null || Find(normal) is not { } normalPath) continue;
                var file = Find(path);
                if (file is null || !seen.Add($"{file}|{normalPath}|{threshold}")) continue;
                Model model;
                try { model = Model.Build(OgreMeshReader.ReadFile(file), null); }
                catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException) { continue; }
                var s = FoliageCardTrimming.Apply(model, normalPath, threshold);
                if (filter is not null)
                {
                    var uvs = model.Parts.SelectMany(p => p.Vertices.Select(v => v.Uv)).ToList();
                    Console.WriteLine($"  uv range {uvs.Min(u => u.X):0.###}..{uvs.Max(u => u.X):0.###} x {uvs.Min(u => u.Y):0.###}..{uvs.Max(u => u.Y):0.###}, tile {mesh.TileX}x{mesh.TileY}, mode {mesh.MaterialType}, threshold {threshold * 255:0}");
                }
                meshes++;
                var mask = FoliageCardMask.Obtain(normalPath, threshold);
                total += s;
                if (verbose || filter is not null)
                    Console.WriteLine($"{mesh.Name,-40} {Path.GetFileName(path),-34} tris {model.Parts.Sum(p => p.Indices.Length / 3),5} -> {s.TrianglesAfter + (model.Parts.Sum(p => p.Indices.Length / 3) - s.TrianglesBefore),5}  cards {s.Units,4} trimmed {s.Trimmed,4} (removed {s.Removed}, outside {s.OutsideUv}, tiny {s.TinyCards})  area uv {Ratio(s.UvAreaAfter, s.UvAreaBefore)} world {Ratio(s.WorldAreaAfter, s.WorldAreaBefore)}  mask {mask?.Coverage():0.00}");
            }
        }
        Console.WriteLine($"meshes {meshes}  cards {total.Units}  trimmed {total.Trimmed} (removed {total.Removed}, outside uv {total.OutsideUv}, tiny {total.TinyCards})  triangles of those cards {total.TrianglesBefore} -> {total.TrianglesAfter}  uv area {Ratio(total.UvAreaAfter, total.UvAreaBefore)}  world area {Ratio(total.WorldAreaAfter, total.WorldAreaBefore)}  [{watch.ElapsedMilliseconds} ms; masks: {FoliageCardMask.Summary}]");
        return 0;
    }

    static string Ratio(double after, double before) => before > 0 ? $"{after / before:0.00}" : "-";
}
