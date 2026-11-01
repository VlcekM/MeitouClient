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
    [Slow]
    public void Base_game_distant_town_meshes_are_the_merged_distant_meshes_of_their_buildings()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var world = WorldLevelData.LoadBaseGame(install!);
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
    [Slow]
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
