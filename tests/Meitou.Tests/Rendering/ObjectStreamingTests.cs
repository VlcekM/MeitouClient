using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Tests.Rendering;

/// <summary>
/// Distance-based streaming of the object textures' top mips and the meshes' finest LOD levels (docs/renderer-native.md 8.12).
/// </summary>
public class ObjectStreamingTests
{
    [Fact, Slow]
    public void Repair_replaces_the_ruin_without_removing_neighbor_instances()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var install = InstallData.Install!;
        var db = InstallData.BaseGame!;
        using var map = TerrainHeightmap.Open(install);
        using var objects = WorldObjects.Load(install, db, map, 0, 0, 0, 0);
        var house = objects.Levels.Buildings().First(p =>
            BuildingPlacements.Resolve(p, db, objects.Levels, map.HeightAt) is { Destroyed: true, Record.Name: "Storm House" });
        var replacement = house with { DestroyedOverride = false };
        var expected = objects.BuildRuntime(replacement).Items.Where(p => p.PlacementId == house.Key)
            .Select(p => p.MeshPath).Order().ToArray();

        Assert.NotEmpty(expected);

        // Get only creates mesh handles; no decode, upload or GPU device is needed for this streaming contract.
        using var meshes = new ObjectMeshCache(null!, null!, null!);
        using var streamer = new ObjectStreamer(objects, meshes);
        void Frame() => streamer.Update(house.Position, 0, 16);

        Assert.True(SpinWait.SpinUntil(() =>

        {
            Frame();
            return streamer.ZoneAt(house.Zone) is not null && streamer.Pending == 0;
        }, TimeSpan.FromSeconds(30)), "Initial zone did not finish loading");
        var zone = streamer.ZoneAt(house.Zone)!;
        var neighbors = zone.Real.Where(i => i.Placed.PlacementId != house.Key && i.Placed.ParentPlacementId != house.Key).ToArray();

        Assert.NotEmpty(neighbors);

        string[] HouseMeshes() => zone.Real.Where(i => i.Placed.PlacementId == house.Key).Select(i => i.Placed.MeshPath).Order().ToArray();

        Assert.NotEmpty(HouseMeshes());
        Assert.False(expected.SequenceEqual(HouseMeshes()));

        void CheckNeighbors()
        {
            Assert.Same(zone, streamer.ZoneAt(house.Zone));

            foreach (var neighbor in neighbors) Assert.Contains(neighbor, zone.Real);
        }
        objects.AddRuntime(replacement);
        streamer.AddRuntime(replacement);
        CheckNeighbors();

        Assert.True(SpinWait.SpinUntil(() =>

        {
            Frame();
            CheckNeighbors();
            return expected.SequenceEqual(HouseMeshes());
        }, TimeSpan.FromSeconds(30)), "Ruin was not replaced with the repaired house");
        Frame();
        CheckNeighbors();

        Assert.Equal(expected, HouseMeshes());
    }

    // 1600 x 900, 50 degrees: a pixel's footprint at distance d is at least d * cos^2(corner) / focal.
    static MipStreaming View()
    {
        var m = new MipStreaming();
        m.SetView(1600, 900, 50 * MathF.PI / 180, 0);
        return m;
    }

    [Fact]
    public void Near_users_keep_every_level_and_far_ones_lose_the_top_ones()
    {
        var m = View();

        // need = distance / world length of a texture-coordinate unit. A wall 100 units per texture seen from 50: texels are 0.024 units, pixels
        // 0.03.
        Assert.Equal(0, m.Drop(4096, 4096, 0.5f));

        int mid = m.Drop(4096, 4096, 5);
        int far = m.Drop(4096, 4096, 400);

        Assert.InRange(mid, 1, 2);
        Assert.True(far > mid);

        // Never below 512 on the larger side: 4096 loses at most 3 levels.
        Assert.Equal(3, m.Drop(4096, 4096, 1e9f));
        Assert.Equal(1, m.Drop(1024, 1024, 1e9f));
        Assert.Equal(0, m.Drop(512, 512, 1e9f));
    }

    [Fact]
    public void Texel_scale_takes_the_stretched_direction()
    {
        // A strip 10 wide and 1000 long with the coordinates running 0..1 on both: the long side is stretched 100 times as much as the short one.
        var v = new[]
        {
            new Vertex { Position = new Vector3(0, 0, 0), Uv = new Vector2(0, 0) },
                    new Vertex { Position = new Vector3(10, 0, 0), Uv = new Vector2(1, 0) },
            new Vertex { Position = new Vector3(10, 0, 1000), Uv = new Vector2(1, 1) },
                    new Vertex { Position = new Vector3(0, 0, 1000), Uv = new Vector2(0, 1) },
        };
        var part = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = v, Indices = [0, 1, 2, 0, 2, 3], HasUv = true };

        Assert.InRange(MeshTexelScale.Of(part), 1000f, 1000f * 1.19f + 1);   // not the 100 of the geometric mean
    }

    [Fact]
    public void The_level_held_follows_the_lod_value_with_the_blend_band()
    {
        float[] distances = [0, 100, 300, 900];

        Assert.Equal(0, ObjectMeshCache.LevelFor(distances, 50));
        Assert.Equal(0, ObjectMeshCache.LevelFor(distances, 105));   // still fading from level 0 just past 100 (band 6%)
        Assert.Equal(1, ObjectMeshCache.LevelFor(distances, 120));
        Assert.Equal(1, ObjectMeshCache.LevelFor(distances, 320));
        Assert.Equal(3, ObjectMeshCache.LevelFor(distances, 5000));

        // It never says finer than the viewer's own blend can draw, nor coarser.
        for (float v = 0; v < 2000; v += 3.7f)
        {
            int held = ObjectMeshCache.LevelFor(distances, v);
            var blend = MeshLod.Blend(distances, v);

            Assert.True(held <= blend.Lower, $"value {v}: held {held}, finest drawn {blend.Lower}");
        }
    }

    [Fact]
    public void A_mesh_made_for_far_users_holds_only_what_their_levels_use()
    {
        // Two triangle sets over one vertex pool: level 0 uses all of it, level 1 only the first three vertices.
        var vertices = Enumerable.Range(0, 12).Select(i => new Vertex { Position = new Vector3(i, i * 2, 0), Uv = new Vector2(i, 0) }).ToArray();
        var part = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = vertices,
                Indices = Enumerable.Range(0, 12).Select(i => (uint)i).ToArray(), HasUv = true };
        var levels = new List<MeshLodLevel> { new(0, [part.Indices]), new(500, [new uint[] { 2, 0, 1 }]) };
        DecodedObjectMesh Make(int minLevel)
        {
            var model = new Model();
            model.Parts.Add(part);
            return new DecodedObjectMesh { Model = model, Levels = levels, Manual = [], Centre = Vector3.Zero, Radius = 10, MinLevel = minLevel };
        }
        var full = ObjectMeshCache.PreparePart(Make(0), part, false);

        Assert.Equal(12, full.Part.Vertices.Length);
        Assert.Equal([0, 12], full.Offset);
        Assert.Equal([12, 3], full.Count);

        var far = ObjectMeshCache.PreparePart(Make(1), part, false);

        Assert.Equal(3, far.Part.Vertices.Length);
        Assert.Equal([0, 0], far.Offset);
        Assert.Equal([0, 3], far.Count);

        // The same triangle, through the compacted vertices: positions in the same order of corners.
        var corners = far.All.Select(i => far.Part.Vertices[i].Position.X).ToArray();

        Assert.Equal([2f, 0f, 1f], corners);
        Assert.Equal(full.UvScale, far.UvScale);   // the textures' scale comes from the whole part
    }
}
