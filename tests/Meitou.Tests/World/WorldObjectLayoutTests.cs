using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class WorldObjectLayoutTests
{
    static GameReference Part(string id, int group, int chance) => new(id, new ReferenceValues(group, chance, 0));

    [Fact]
    public void Instance_transform_scales_then_rotates_then_moves()
    {
        // A quarter turn about +Y (right-handed, Y up as in Ogre) takes +X to -Z.
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var m = WorldObjectLayout.InstanceTransform(new Vector3(100, 5, -20), q, new Vector3(2));
        var p = Vector3.Transform(new Vector3(1, 0, 0), m);
        Assert.Equal(100, p.X, 3);
        Assert.Equal(5, p.Y, 3);
        Assert.Equal(-22, p.Z, 3);
        // A zero quaternion (unused rotation) acts as identity.
        Assert.Equal(new Vector3(1, 2, 3), Vector3.Transform(new Vector3(1, 2, 3), WorldObjectLayout.InstanceTransform(Vector3.Zero, default, Vector3.One)));
    }

    [Fact]
    public void Random_is_the_msvc_rand_sequence()
    {
        // MSVC rand() after srand(1), a published sequence (the CRT's default seed).
        var r = new BuildingRandom(1);
        Assert.Equal([41, 18467, 6334, 26500, 19169, 15724, 11478, 29358], Enumerable.Range(0, 8).Select(_ => r.Next()));
        // The int helper is floor(rand × n / 32768) for every possible result; the float helper stays below its maximum.
        var a = new BuildingRandom(12345);
        var b = new BuildingRandom(12345);
        for (int i = 0; i < 2000; i++)
        {
            int raw = b.Next();
            Assert.Equal(raw * 100 / 32768, a.NextInt(0, 99));
        }
        var c = new BuildingRandom(7);
        for (int i = 0; i < 2000; i++) Assert.InRange(c.NextFloat(0, 3), 0, 2.99999f);
    }

    [Fact]
    public void Seed_truncates_world_x_and_z_toward_zero()
    {
        Assert.Equal(BuildingRandom.Seed(1, 5), BuildingRandom.Seed(1.9f, 5.99f));
        Assert.Equal(BuildingRandom.Seed(-1, -5), BuildingRandom.Seed(-1.9f, -5.5f));
        Assert.NotEqual(BuildingRandom.Seed(-1, 0), BuildingRandom.Seed(-2, 0));
        Assert.NotEqual(BuildingRandom.Seed(3, 4), BuildingRandom.Seed(4, 3));
        // hash_combine(a, b) with a = x ^ 0xDEADBEEF, b = z ^ 0xDEADBEEF.
        uint x = 0xDEADBEEF, z = 0xDEADBEEF;
        Assert.Equal(unchecked(((x >> 2) + 0x9E3779B9u + x * 64 + z) ^ x), BuildingRandom.Seed(0, 0));
    }

    [Fact]
    public void Part_choice_follows_the_games_rolls()
    {
        static bool Exists(string id) => id != "missing";
        // Group 0: chance 0 keeps without a roll; chance 100 still rolls; a missing target still rolls.
        var r = new BuildingRandom(99);
        var chosen = WorldObjectLayout.ChooseParts([Part("a", 0, 0)], Exists, r);
        Assert.Equal("a", Assert.Single(chosen).TargetStringId);
        Assert.Equal(0, r.Count);
        WorldObjectLayout.ChooseParts([Part("a", 0, 100), Part("missing", 0, 50)], Exists, r);
        Assert.Equal(2, r.Count);

        // A group 0 entry is kept when the int roll 0..99 is below its chance.
        for (uint seed = 0; seed < 50; seed++)
        {
            int roll = new BuildingRandom(seed).NextInt(0, 99);
            var kept = WorldObjectLayout.ChooseParts([Part("a", 0, 40)], Exists, new BuildingRandom(seed));
            Assert.Equal(roll < 40, kept.Count == 1);
        }

        // Other groups: one roll per group with two or more usable entries, the pick is the first running total above the roll.
        for (uint seed = 0; seed < 50; seed++)
        {
            float roll = new BuildingRandom(seed).NextFloat(0, 100);
            var rng = new BuildingRandom(seed);
            var pick = WorldObjectLayout.ChooseParts([Part("c", 1, 25), Part("zero", 1, 0), Part("missing", 1, 30), Part("d", 1, 75), Part("e", 2, 10)], Exists, rng);
            Assert.Equal(1, rng.Count); // group 2 has a single entry: no roll
            Assert.Equal(["e", roll < 25 ? "c" : "d"], pick.Select(p => p.TargetStringId)); // group 2 was inserted last: first in map order
        }

        // Group order is the game's hash map order (17 buckets): new buckets go first, a key joins its bucket at the front.
        var order = WorldObjectLayout.ChooseParts([Part("g1", 1, 1), Part("g2", 2, 1), Part("g18", 18, 1), Part("z", 0, 0)], Exists, new BuildingRandom(0));
        Assert.Equal(["z", "g2", "g18", "g1"], order.Select(p => p.TargetStringId));
    }

    [Fact]
    public void Base_game_buildings_have_meshes_and_materials()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var world = WorldLevelData.Load(install!);
        var towns = new BuildingTowns(db, world);
        int placed = 0, empty = 0, foliage = 0, withDoors = 0, noMaterial = 0;
        var emptyNames = new HashSet<string>();
        foreach (var b in world.Buildings())
        {
            if (db.Find(b.BuildingId) is not { } record) continue;
            placed++;
            var state = b.StateId is not null ? world.Zones[b.Zone].Find(b.StateId) : null;
            var buildingState = new BuildingState(state?.GetBool("destroyed") ?? false, towns.MaterialOf(towns.TownOf(state, b.Position)));
            var meshes = WorldObjectLayout.Building(db, record, b.InstanceId, b.Position, b.Rotation, buildingState);
            // The same position gives the same parts.
            Assert.Equal(meshes.Select(m => m.Source.StringId), WorldObjectLayout.Building(db, record, "other id", b.Position, b.Rotation, buildingState).Select(m => m.Source.StringId));
            if (meshes.Any(m => m.Owner != record)) withDoors++;
            if (meshes.Count == 0)
            {
                if (record.GetBool("is foliage")) foliage++;
                else if (!record.GetBool("is node") && !buildingState.Destroyed) { empty++; emptyNames.Add(record.Name); }
                continue;
            }
            Assert.All(meshes, m => Assert.Contains(".mesh", m.MeshPath, StringComparison.Ordinal));
            noMaterial += meshes.Count(m => m.Material is not { Type: FcsRecordType.MATERIAL_SPEC });
        }
        // Resource buildings are foliage companions (1,335 placements); one Ramp has no parts at all (Observed 2026-10-04).
        Assert.InRange(foliage, 1300, 1400);
        Assert.True(empty <= 1, $"{empty} of {placed} placed buildings have no mesh: {string.Join(", ", emptyNames)}");
        Assert.True(withDoors > 900, $"only {withDoors} placements draw doors");
        Assert.Equal(0, noMaterial);

        var features = MapFeatureFile.Open(install!).All().Select((f, i) => WorldObjectLayout.Feature(db, f.Feature, $"{i}")).ToList();
        Assert.True(features.Count(f => f is not null) > features.Count * 0.95);
    }

    [Fact]
    public void Town_handles_and_town_materials()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var world = WorldLevelData.Load(install!);
        var towns = new BuildingTowns(db, world);
        // Buildings whose town handle matches a town state lie close to that town (Observed: median 707 units over 2,320).
        // The nearest town (the game's fallback by position) agrees for nearly all of them (Observed: 2,305 of 2,320).
        var distances = new List<float>();
        int agree = 0;
        foreach (var b in world.Buildings())
        {
            if (b.StateId is null || world.Zones[b.Zone].Find(b.StateId) is not { } state) continue;
            if (towns.TownByHandle(state) is not { } town) continue;
            distances.Add(Vector2.Distance(new(town.Position.X, town.Position.Z), new(b.Position.X, b.Position.Z)));
            if (towns.NearestTown(b.Position) is { } nearest && nearest.TownId == town.TownId) agree++;
        }
        distances.Sort();
        Assert.True(distances.Count > 2000, $"{distances.Count} handles matched");
        Assert.True(distances[distances.Count / 2] < 2000, $"median {distances[distances.Count / 2]}");
        Assert.True(agree > distances.Count * 0.95, $"nearest town agrees for {agree} of {distances.Count}");

        // A town without a material uses moor1-main; Brink's material is a collection, resolved to one of its entries.
        var hub = world.Towns().First(t => db.Find(t.TownId)?.Name == "The Hub");
        Assert.Equal(WorldObjectLayout.DefaultTownMaterial, towns.MaterialOf(hub)!.StringId);
        var brink = world.Towns().First(t => t.TownId == "962-gamedata.base");
        var collection = db.Find(db.Find(brink.TownId)!.GetReferences("material")[0].TargetStringId)!;
        Assert.Contains(towns.MaterialOf(brink)!.StringId, collection.GetReferences("material").Select(r => r.TargetStringId));
    }

    [Fact]
    public void Destroyed_buildings_swap_meshes_and_lose_upper_floors_and_doors()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var world = WorldLevelData.Load(install!);
        int checkedBuildings = 0;
        foreach (var b in world.Buildings())
        {
            if (b.StateId is null || world.Zones[b.Zone].Find(b.StateId) is not { } state || !state.GetBool("destroyed")) continue;
            if (db.Find(b.BuildingId) is not { } record) continue;
            var intact = WorldObjectLayout.Building(db, record, b.InstanceId, b.Position, b.Rotation, new BuildingState(false));
            var ruined = WorldObjectLayout.Building(db, record, b.InstanceId, b.Position, b.Rotation, new BuildingState(true));
            Assert.All(ruined, m => Assert.Equal(record, m.Owner)); // no doors
            foreach (var m in ruined)
            {
                var destroyedMesh = m.Source.GetPath("destroyed mesh");
                Assert.Equal(destroyedMesh.Length > 0 ? destroyedMesh : m.Source.GetPath("phs or mesh"), m.MeshPath);
                if (destroyedMesh.Length == 0) Assert.Equal(0, m.Source.GetInt("building floor"));
            }
            Assert.True(intact.Count >= ruined.Count);
            checkedBuildings++;
        }
        Assert.True(checkedBuildings > 200);
    }

    [Fact]
    public void Layout_items_are_placed_in_the_buildings_frame()
    {
        // A quarter turn about +Y: the item's local +X offset ends up along world -Z, and its own rotation follows the building's.
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var item = new LayoutItem("0", null!, new Vector3(10, 4, 0), turn);
        var (at, rotation) = BuildingLayouts.Place(new Vector3(100, 50, 200), turn, item);
        Assert.Equal(100, at.X, 3);
        Assert.Equal(54, at.Y, 3);
        Assert.Equal(190, at.Z, 3);
        var half = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        Assert.Equal(1, MathF.Abs(Quaternion.Dot(rotation, half)), 3);
    }

    [Fact]
    public void Exterior_layout_names_resolve_to_interiors_level_layouts()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var world = WorldLevelData.Load(install!, includeInteriors: true);
        var layouts = new BuildingLayouts(db, world.Interiors);
        int named = 0, resolved = 0, items = 0;
        foreach (var b in world.Buildings())
        {
            if (b.StateId is null || world.Zones[b.Zone].Find(b.StateId) is not { } state) continue;
            var name = state.GetString("exterior layout name");
            if (name.Length == 0 || db.Find(b.BuildingId) is not { } record) continue;
            named++;
            if (layouts.Find(record, name) is not { } layout) continue;
            resolved++;
            Assert.True(layout.GetBool("is exterior"));
            var list = layouts.Items(layout).ToList();
            if (list.Count == 0) { Assert.Empty(layout.Instances); continue; } // two layouts (EXT-727-...-Armor Sign, EXT-3384-...-No Entry) are empty
            items += list.Count;
            // Signs sit on the building: at most 500 units from its origin in the base game.
            Assert.All(list, i => Assert.InRange(i.Position.Length(), 0, 600));
        }
        // Counts of 2026-10 (docs/formats/zones.md, "Building layouts"): 325 named, 312 found (262 by the exact EXT id).
        Assert.Equal(325, named);
        Assert.Equal(312, resolved);
        Assert.True(items >= resolved);
    }
}
