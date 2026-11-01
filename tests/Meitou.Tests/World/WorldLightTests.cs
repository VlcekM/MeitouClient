using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class WorldLightTests
{
    static GameDatabase Db(params FcsRecord[] records)
    {
        var file = new FcsFile();
        file.Records.AddRange(records);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    static FcsRecord Light(string id, float brightness, float variance, int type = 0, int diffuse = 0xFFCE5E)
    {
        var r = new FcsRecord { StringId = id, Flags = 0x10, Name = "light", RecordType = FcsRecordType.LIGHT };
        r.Floats["brightness"] = brightness;
        r.Floats["variance"] = variance;
        r.Floats["radius"] = 150;
        r.Floats["inner"] = 40;
        r.Floats["outer"] = 90;
        r.Floats["falloff"] = 1;
        r.Ints["type"] = type;
        r.Ints["diffuse"] = diffuse;
        r.Ints["effect"] = 2;
        return r;
    }

    static FcsRecord Part(string id, Vector3 offset)
    {
        var r = new FcsRecord { StringId = id, Flags = 0x10, Name = id, RecordType = FcsRecordType.BUILDING_PART };
        r.Filenames["phs or mesh"] = $".\\data\\{id}.mesh";
        r.Floats["offset X"] = offset.X;
        r.Floats["offset Y"] = offset.Y;
        r.Floats["offset Z"] = offset.Z;
        return r;
    }

    static FcsRecord Building(string id, float scale, params string[] parts)
    {
        var r = new FcsRecord { StringId = id, Flags = 0x10, Name = id, RecordType = FcsRecordType.BUILDING };
        r.Floats["scale"] = scale;
        r.References["parts"] = parts.Select(p => new FcsReference(p, 0, 0, 0)).ToList();
        return r;
    }

    static FcsInstance Instance(string id, string target, Vector3 position, Quaternion rotation) =>
        new() { Id = id, Target = target, Position = position, Rotation = rotation };

    [Fact]
    public void Each_light_draws_its_power_roll_and_a_second_roll_before_later_part_choices()
    {
        // b: p1 (a light, variance 0.4) then p2, whose own part list has one 50 % entry; the rolls go
        // light power, light second draw, p2's child roll.
        var p1 = Part("p1", Vector3.Zero);
        p1.Instances.Add(Instance("light", "l", Vector3.Zero, Quaternion.Identity));
        var p2 = Part("p2", Vector3.Zero);
        p2.References["parts"] = [new FcsReference("child", 0, 50, 0)];
        var child = Part("child", Vector3.Zero);
        var db = Db(Light("l", 0.6f, 0.4f), p1, p2, child, Building("b", 1, "p1", "p2"));

        int withChild = 0;
        for (int i = 0; i < 64; i++)
        {
            var at = new Vector3(i * 137.0f, 0, -i * 59.0f);
            var lights = new List<PlacedLight>();
            var meshes = WorldObjectLayout.Building(db, db.Find("b")!, "id", at, Quaternion.Identity, null, lights);

            var expected = new BuildingRandom(BuildingRandom.Seed(at.X, at.Z));
            float power = 0.6f + expected.NextFloat(-0.2f, 0.2f);
            expected.Next();
            bool childKept = expected.NextInt(0, 99) < 50;

            Assert.Equal(power, Assert.Single(lights).Power);
            Assert.InRange(lights[0].Power, 0.4f, 0.8f);
            Assert.Equal(childKept, meshes.Any(m => m.Source.StringId == "child"));
            // The light list does not change the meshes.
            Assert.Equal(meshes.Select(m => m.Source.StringId), WorldObjectLayout.Building(db, db.Find("b")!, "id", at, Quaternion.Identity).Select(m => m.Source.StringId));
            if (childKept) withChild++;
        }
        Assert.InRange(withChild, 1, 63);
    }

    [Fact]
    [Slow]
    public void World_lights_are_placed_on_buildings_and_layouts_around_the_hub()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var levels = WorldLevelData.LoadBaseGame(install!, includeInteriors: true);
        using var map = TerrainHeightmap.Open(install!);
        var layouts = new BuildingLayouts(db, levels.Interiors);

        var onBuildings = WorldLights.ForWorld(db, levels, map.HeightAt);
        var all = WorldLights.ForWorld(db, levels, map.HeightAt, layouts);
        // 2026-10-09 base game: 1,302 on placed buildings, 5,159 with the layouts' (3,857 of them interior).
        Assert.InRange(onBuildings.Count, 1100, 1500);
        Assert.InRange(all.Count, 4500, 5800);
        Assert.InRange(all.Count(l => l.Interior), 3300, 4400);
        Assert.All(all, l => Assert.True(l.Radius > 0 && l.Intensity > 0 && float.IsFinite(l.Position.X)));
        Assert.All(all, l => Assert.Equal(1, l.Direction.Length(), 3));

        // The Holy Street Torch about 100 units from The Hub's centre: a warm point light 50+ units above the ground.
        var hub = levels.Towns().Select(t => (Place: t, Record: db.Find(t.TownId))).First(t => t.Record?.Name == "The Hub").Place.Position;
        var torch = all.Where(l => l.BuildingId == "42309-Newwworld.mod")
            .MinBy(l => Vector2.Distance(new Vector2(l.Position.X, l.Position.Z), new Vector2(hub.X, hub.Z)))!;
        Assert.InRange(Vector2.Distance(new Vector2(torch.Position.X, torch.Position.Z), new Vector2(hub.X, hub.Z)), 50, 200);
        Assert.Equal(WorldLightType.Point, torch.Type);
        Assert.Equal(150, torch.Radius);
        Assert.Equal("42312-Newwworld.mod", torch.LightId);
        Assert.Equal(WorldLights.Colour(0xFFCE5E), torch.Colour);
        Assert.InRange(torch.Intensity, 0.65f, 0.75f);
        Assert.InRange(torch.Position.Y - map.HeightAt(torch.Position.X, torch.Position.Z), 30, 80);
        Assert.False(torch.Interior);
    }
}
