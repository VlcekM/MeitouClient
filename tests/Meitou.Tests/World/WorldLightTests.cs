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

    static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void Light_on_a_part_hangs_under_the_parts_node_and_spots_point_down_its_rotation()
    {
        var part = Part("p", new Vector3(10, 0, 0));
        // A spot turned a quarter about +Z: −Y becomes +X in the part's frame.
        part.Instances.Add(Instance("Fspot001", "l", new Vector3(0, 5, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)));
        var db = Db(Light("l", 0.6f, 0, type: 1), part, Building("b", 1, "p"));
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2); // +X → −Z
        var lights = new List<PlacedLight>();
        var meshes = WorldObjectLayout.Building(db, db.Find("b")!, "id", new Vector3(1000, 50, 2000), turn, null, lights);

        Assert.Single(meshes);
        var p = Assert.Single(lights);
        AssertClose(new Vector3(1000, 55, 1990), p.Position);
        AssertClose(new Vector3(0, 0, -1), p.Direction);
        Assert.Equal(0.6f, p.Power);
        Assert.Equal("p", p.Holder.StringId);

        var w = WorldLights.FromPlaced(p, interior: false);
        Assert.Equal(WorldLightType.Spot, w.Type);
        Assert.Equal(WorldLightEffect.Flicker, w.Effect);
        Assert.Equal(WorldLightRule.WhenBuildingOn, w.Rule);
        AssertClose(new Vector3(1, 0xCE / 255f, 0x5E / 255f), w.Colour);
        Assert.Equal(40 * MathF.PI / 180, w.InnerAngle, 5);
        Assert.Equal(150, w.Radius);
    }

    [Fact]
    public void Building_scale_scales_the_instance_position_and_compounds_under_a_part()
    {
        // On the building itself (no part node): position × scale under the building's unscaled node.
        var building = Building("b", 2);
        building.Instances.Add(Instance("1", "l", new Vector3(0, 10, 0), Quaternion.Identity));
        // On a part: position × scale under the part's node, which carries the scale again (Ogre's inherited scale).
        var part = Part("p", Vector3.Zero);
        part.Instances.Add(Instance("1", "l", new Vector3(0, 10, 0), Quaternion.Identity));
        var withPart = Building("c", 2, "p");
        var db = Db(Light("l", 1, 0), building, part, withPart);

        var lights = new List<PlacedLight>();
        WorldObjectLayout.Building(db, db.Find("b")!, "id", new Vector3(5, 0, 5), Quaternion.Identity, null, lights);
        AssertClose(new Vector3(5, 20, 5), Assert.Single(lights).Position);
        AssertClose(-Vector3.UnitY, lights[0].Direction);

        lights.Clear();
        WorldObjectLayout.Building(db, db.Find("c")!, "id", new Vector3(5, 0, 5), Quaternion.Identity, null, lights);
        AssertClose(new Vector3(5, 40, 5), Assert.Single(lights).Position);
    }

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
    public void Attenuation_is_the_games_piecewise_curve()
    {
        Assert.Equal(1, WorldLights.Attenuation(0, 100), 5);
        Assert.Equal(0, WorldLights.Attenuation(100, 100));
        Assert.Equal(0, WorldLights.Attenuation(150, 100));
        Assert.Equal(0, WorldLights.Attenuation(10, 0));
        Assert.Equal(0.2f + 0.8f * 0.125f, WorldLights.Attenuation(50, 100), 5);
        // Continuous at both joins (to the curve's own rounding) and falling all the way.
        Assert.Equal(WorldLights.Attenuation(64.89999f, 100), WorldLights.Attenuation(64.9f, 100), 2);
        Assert.Equal(WorldLights.Attenuation(79.99999f, 100), WorldLights.Attenuation(80f, 100), 2);
        float previous = 2;
        for (float d = 0; d < 100; d += 0.5f)
        {
            float a = WorldLights.Attenuation(d, 100);
            Assert.True(a <= previous + 1e-6f, $"rises at {d}");
            previous = a;
        }
    }

    [Fact]
    public void Spot_factor_is_one_inside_the_inner_cone_and_zero_outside_the_outer()
    {
        float inner = 40 * MathF.PI / 180, outer = 90 * MathF.PI / 180;
        Assert.Equal(1, WorldLights.SpotFactor(1, inner, outer, 1));
        Assert.Equal(1, WorldLights.SpotFactor(MathF.Cos(15 * MathF.PI / 180), inner, outer, 1));
        Assert.Equal(0, WorldLights.SpotFactor(MathF.Cos(50 * MathF.PI / 180), inner, outer, 1));
        float mid = WorldLights.SpotFactor(MathF.Cos(32 * MathF.PI / 180), inner, outer, 1);
        Assert.InRange(mid, 0.01f, 0.99f);
        Assert.True(WorldLights.SpotFactor(MathF.Cos(32 * MathF.PI / 180), inner, outer, 2) < mid);
        // Cones given the wrong way round (fcs.def's default 45/40) are put in order.
        Assert.Equal(mid, WorldLights.SpotFactor(MathF.Cos(32 * MathF.PI / 180), outer, inner, 1));
    }

    [Fact]
    public void Daylight_ramps_over_five_minutes_and_night_lights_need_it_at_most_a_tenth()
    {
        Assert.Equal(0, WorldLights.DaylightFactor(4.9f, 5, 23));
        Assert.Equal(0.5f, WorldLights.DaylightFactor(5 + 1f / 24, 5, 23), 4);
        Assert.Equal(1, WorldLights.DaylightFactor(12, 5, 23));
        Assert.Equal(0.5f, WorldLights.DaylightFactor(23 - 1f / 24, 5, 23), 4);
        Assert.Equal(0, WorldLights.DaylightFactor(23.5f, 5, 23));

        var light = new WorldLight(Vector3.Zero, Vector3.One, 0.6f, 100, WorldLightType.Point, -Vector3.UnitY, 0, 0, 1,
            WorldLightEffect.None, WorldLightRule.Night, false, 0, "l", "b", "id");
        Assert.True(WorldLights.IsLit(light, 0.05f));
        Assert.False(WorldLights.IsLit(light, 0.5f));
        var always = light with { Rule = WorldLightRule.WhenBuildingOn };
        Assert.True(WorldLights.IsLit(always, 1));
        Assert.False(WorldLights.IsLit(always, 0, buildingOn: false));
        Assert.False(WorldLights.IsLit(always with { Intensity = 0 }, 0));

        Assert.Equal(1, WorldLights.EffectFactor(WorldLightEffect.Flicker, 3));
        Assert.Equal(0.5f, WorldLights.EffectFactor(WorldLightEffect.Pulse, 0));
        Assert.InRange(WorldLights.EffectFactor(WorldLightEffect.Shimmer, 1.3f), 0.9f, 1);
    }

    [Fact]
    [Slow]
    public void World_lights_are_placed_on_buildings_and_layouts_around_the_hub()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var levels = WorldLevelData.Load(install!, includeInteriors: true);
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
