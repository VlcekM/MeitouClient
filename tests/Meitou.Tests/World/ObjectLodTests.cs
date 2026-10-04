using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class ObjectLodTests
{
    static readonly float[] Distances = [0, 400, 4000, 8000];

    [Fact]
    public void Select_follows_ogres_lower_bound_over_bare_distances()
    {
        Assert.Equal(0, MeshLod.Select(Distances, -10));
        Assert.Equal(0, MeshLod.Select(Distances, 400)); // strictly above the distance
        Assert.Equal(1, MeshLod.Select(Distances, 400.5f));
        Assert.Equal(2, MeshLod.Select(Distances, 5000));
        Assert.Equal(3, MeshLod.Select(Distances, 1e6f));
        Assert.Equal(0, MeshLod.Select([0f], 1e6f));
        // The list and span overloads agree.
        var levels = Distances.Select(d => new MeshLodLevel(d, [])).ToList();
        for (float v = -10; v < 10000; v += 37) Assert.Equal(MeshLod.Select(levels, v), MeshLod.Select(Distances, v));
    }

    [Fact]
    public void Blend_is_the_games_choice_away_from_the_bands_and_continuous_across_them()
    {
        // Far from every boundary: not blending, the level the game picks.
        foreach (float v in new[] { -50f, 100f, 2000f, 6000f, 20000f })
        {
            var b = MeshLod.Blend(Distances, v);
            Assert.False(b.IsBlending, $"value {v}");
            Assert.Equal(MeshLod.Select(Distances, v), b.Lower);
        }
        // Across the band round 4000 (6 %: 240 units each side) the upper weight rises from 0 to 1 without a jump.
        float previous = 0;
        for (float v = 3700; v <= 4300; v += 2)
        {
            var b = MeshLod.Blend(Distances, v);
            float weight = b.IsBlending ? b.T : b.Lower == 2 ? 1 : 0;
            Assert.True(weight >= previous - 1e-6f && weight - previous < 0.05f, $"value {v}: {weight} after {previous}");
            previous = weight;
        }
        Assert.Equal(1, previous);
        var middle = MeshLod.Blend(Distances, 4000);
        Assert.Equal((1, 2), (middle.Lower, middle.Upper));
        Assert.Equal(0.5f, middle.T, 3);
        // Both neighbours are named at every value of the band, below and above the game's switch point.
        Assert.Equal((1, 2), (MeshLod.Blend(Distances, 3900).Lower, MeshLod.Blend(Distances, 3900).Upper));
        Assert.Equal((1, 2), (MeshLod.Blend(Distances, 4100).Lower, MeshLod.Blend(Distances, 4100).Upper));
    }

    [Fact]
    public void Blend_bands_never_overlap_when_distances_are_close()
    {
        float[] close = [0, 400, 430, 1000];
        float previousLevel = 0;
        for (float v = 300; v < 1200; v += 1)
        {
            var b = MeshLod.Blend(close, v);
            float level = b.Lower + b.T * (b.Upper - b.Lower);
            Assert.True(level >= previousLevel - 1e-4f, $"value {v}: {level} after {previousLevel}");
            Assert.InRange(b.T, 0, 1);
            previousLevel = level;
        }
    }

    [Fact]
    public void Part_rendering_distance_follows_the_games_setting_rules()
    {
        // Small parts: the objects view range; big meshes (bounds radius above 100) and generators: unlimited; turrets: half the range.
        Assert.Equal(3000, ObjectRanges.PartRenderingDistance(50, 0));
        Assert.Equal(3000, ObjectRanges.PartRenderingDistance(100, 0));
        Assert.Equal(float.MaxValue, ObjectRanges.PartRenderingDistance(100.5f, 0));
        Assert.Equal(float.MaxValue, ObjectRanges.PartRenderingDistance(5, ObjectRanges.GeneratorFunction));
        Assert.Equal(1500, ObjectRanges.PartRenderingDistance(50, ObjectRanges.TurretFunction));
        Assert.Equal(1500, ObjectRanges.PartRenderingDistance(500, ObjectRanges.TurretFunction));
        Assert.Equal(4000, ObjectRanges.PartRenderingDistance(50, 0, 4000));
    }

    [Fact]
    public void Edge_and_rise_weights_are_smooth_complements()
    {
        Assert.Equal(1, ObjectRanges.EdgeWeight(0, 1000, 100));
        Assert.Equal(1, ObjectRanges.EdgeWeight(900, 1000, 100));
        Assert.Equal(0, ObjectRanges.EdgeWeight(1000, 1000, 100));
        Assert.Equal(0.5f, ObjectRanges.EdgeWeight(950, 1000, 100), 5);
        Assert.Equal(1, ObjectRanges.EdgeWeight(1e9f, float.MaxValue, 100));
        Assert.Equal(0, ObjectRanges.RiseWeight(900, 900, 100));
        Assert.Equal(1, ObjectRanges.RiseWeight(1000, 900, 100));
        for (float v = 880; v < 1020; v += 5)
            Assert.Equal(1, ObjectRanges.EdgeWeight(v, 1000, 100) + ObjectRanges.RiseWeight(v, 900, 100), 5); // the cross-fade covers each pixel once
    }

    [Fact]
    public void Placed_distant_mesh_is_scaled_rotated_and_moved_like_the_town_batching()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var withDistant = db.OfType(FcsRecordType.BUILDING).First(b => b.GetPath("distant mesh").Length > 0 && b.GetFloat("scale", 1) != 1);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var placed = WorldObjectLayout.DistantMesh(withDistant, "id", new Vector3(10, 20, 30), q)!;
        Assert.Equal(PlacedKind.BuildingDistant, placed.Kind);
        Assert.Equal(withDistant.GetPath("distant mesh"), placed.MeshPath);
        float scale = withDistant.GetFloat("scale", 1);
        var p = Vector3.Transform(Vector3.UnitX, placed.Transform);
        Assert.Equal(10, p.X, 3);
        Assert.Equal(20, p.Y, 3);
        Assert.Equal(30 - scale, p.Z, 3); // +X goes to -Z under the quarter turn, scaled
        var none = db.OfType(FcsRecordType.BUILDING).First(b => b.GetPath("distant mesh").Length == 0 && !b.GetBool("is node"));
        Assert.Null(WorldObjectLayout.DistantMesh(none, "id", Vector3.Zero, Quaternion.Identity));
    }

    [Fact]
    public void Base_game_distant_town_meshes_are_the_merged_distant_meshes_of_their_buildings()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var world = WorldLevelData.Load(install!);
        var towns = DistantTowns.Find(install!, db, world);
        // 89 baked meshes; 88 belong to a town with the distant mesh flag (Observed 2026-10-04: 75 of 88 equal, the rest differ by buildings the nearest-town rule assigns differently).
        Assert.InRange(towns.Count, 85, 89);
        Assert.Contains(towns, t => t.Name == "The Hub" && t.Handle == 2);
        Assert.All(towns, t => Assert.True(File.Exists(t.MeshPath)));

        using var map = TerrainHeightmap.Open(install!);
        var buildingTowns = new BuildingTowns(db, world);
        var byTown = new Dictionary<string, List<(GameRecord Record, Vector3 Position)>>();
        foreach (var b in world.Buildings())
        {
            if (db.Find(b.BuildingId) is not { } record || record.GetPath("distant mesh").Length == 0) continue;
            var state = b.StateId is not null && world.Zones.TryGetValue(b.Zone, out var zone) ? zone.Find(b.StateId) : null;
            if (buildingTowns.TownOf(state, b.Position) is not { } town) continue;
            float y = b.WorldY ?? map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y;
            if (!byTown.TryGetValue(town.InstanceId, out var list)) byTown[town.InstanceId] = list = [];
            list.Add((record, new Vector3(b.Position.X, y, b.Position.Z)));
        }

        var triangles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int equal = 0, inside = 0, buildings = 0;
        foreach (var t in towns)
        {
            var mesh = OgreMeshReader.ReadFile(t.MeshPath);
            // One submesh, position + normal + uv + colour, as the game's builder lays out its vertices.
            var sub = Assert.Single(mesh.SubMeshes);
            var vd = sub.VertexData ?? mesh.SharedVertexData!;
            Assert.Equal([OgreVertexSemantic.Position, OgreVertexSemantic.Normal, OgreVertexSemantic.TextureCoordinates, OgreVertexSemantic.Diffuse], vd.Elements.OrderBy(e => e.Offset).Select(e => e.Semantic));
            int fileTriangles = sub.Indices.Indices.Length / 3, sum = 0;
            var bounds = mesh.Bounds!.Value;
            foreach (var (record, position) in byTown.GetValueOrDefault(t.InstanceId) ?? [])
            {
                string name = record.GetPath("distant mesh");
                if (!triangles.TryGetValue(name, out int n))
                {
                    var file = Path.Combine(install!.Root, name.Replace('\\', '/').TrimStart('.', '/'));
                    triangles[name] = n = File.Exists(file) ? OgreMeshReader.ReadFile(file).SubMeshes[0].Indices.Indices.Length / 3 : 0;
                }
                sum += n;
                buildings++;
                // Vertices are relative to the town's position, Y included: every building lies inside the mesh's bounds (with its size).
                var local = position - t.Position;
                if (local.X >= bounds.Min.X - 300 && local.X <= bounds.Max.X + 300 && local.Y >= bounds.Min.Y - 300 && local.Y <= bounds.Max.Y + 300 &&
                    local.Z >= bounds.Min.Z - 300 && local.Z <= bounds.Max.Z + 300) inside++;
            }
            if (sum == fileTriangles) equal++;
        }
        // The nearest-town rule reproduces the baked batches: the triangle counts agree for most towns (Observed 2026-10-04: 75 of 88 equal, the rest differ by buildings the nearest-town rule assigns differently).
        Assert.True(equal > towns.Count * 0.6, $"{equal} of {towns.Count} towns' triangle counts equal their buildings' distant meshes");
        Assert.True(inside > buildings * 0.9, $"{inside} of {buildings} buildings inside their town's mesh bounds");
        // The Hub exactly.
        var hub = towns.First(t => t.Name == "The Hub");
        Assert.Equal(OgreMeshReader.ReadFile(hub.MeshPath).SubMeshes[0].Indices.Indices.Length / 3,
            byTown[hub.InstanceId].Sum(b => triangles[b.Record.GetPath("distant mesh")]));
    }

    [Fact]
    public void Building_distant_meshes_have_vertex_colours_and_few_triangles()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var names = db.OfType(FcsRecordType.BUILDING).Select(b => b.GetPath("distant mesh")).Where(n => n.Length > 0).Distinct().ToList();
        Assert.True(names.Count >= 40, $"{names.Count} distinct distant meshes");
        foreach (var name in names)
        {
            var mesh = OgreMeshReader.ReadFile(Path.Combine(install!.Root, name.Replace('\\', '/').TrimStart('.', '/')));
            var sub = mesh.SubMeshes[0];
            var vd = sub.VertexData ?? mesh.SharedVertexData!;
            Assert.NotNull(vd.Find(OgreVertexSemantic.Diffuse)); // "Requires vertex colour" (fcs.def)
            Assert.InRange(sub.Indices.Indices.Length / 3, 1, 400);
        }
    }

    [Fact]
    public void Building_part_meshes_with_lod_levels_read_into_ascending_distances()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        int withLod = 0, checkedMeshes = 0;
        foreach (var part in db.OfType(FcsRecordType.BUILDING_PART).Take(400))
        {
            var name = part.GetPath("phs or mesh");
            if (!name.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.Combine(install!.Root, name.Replace('\\', '/').TrimStart('.', '/'));
            if (!File.Exists(file)) continue;
            var levels = MeshLod.Levels(OgreMeshReader.ReadFile(file));
            checkedMeshes++;
            if (levels.Count > 1) withLod++;
            for (int i = 1; i < levels.Count; i++) Assert.True(levels[i].Distance > levels[i - 1].Distance, $"{name}: level {i} at {levels[i].Distance}");
        }
        Assert.True(checkedMeshes > 100);
        Assert.True(withLod > 0);
    }
}
