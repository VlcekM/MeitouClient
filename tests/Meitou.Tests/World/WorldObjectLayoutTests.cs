using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class WorldObjectLayoutTests
{
    static GameReference Part(string id, int group, int chance) => new(id, new ReferenceValues(group, chance, 0));

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
    [Slow]
    public void Base_game_buildings_have_meshes_and_materials()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var world = WorldLevelData.LoadBaseGame(install!);
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
    [Slow]
    public void Hub_bar_shows_its_interior_on_floor_0()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = InstallData.FullLoadOrder!;
        var levels = WorldLevelData.LoadBaseGame(install!, includeInteriors: true);
        // The Hub's bar: a Storm House with the "Bar" interior layout (docs/formats/zones.md "Interior view").
        var b = levels.Buildings().Single(x => x.InstanceId == "4933-Newwworld-INGAME" && Math.Abs(x.Position.X + 51181) < 5);
        var record = db.Find(b.BuildingId)!;
        var position = new System.Numerics.Vector3(b.Position.X, b.WorldY!.Value, b.Position.Z);

        var parts = WorldObjectLayout.Building(db, record, b.InstanceId, position, b.Rotation, new BuildingState { WithInterior = true });
        Assert.Contains(parts, p => p.Source.Name == "Storm_House_01_Exterior_ROOF" && p.Floor == 1 && !p.Interior);
        Assert.Contains(parts, p => p.Source.Name == "Storm_House_01_Interior" && p.Floor == 0 && p.Interior);
        // Without the interior view the parts are the outside alone, chosen by the same rolls.
        var outside = WorldObjectLayout.Building(db, record, b.InstanceId, position, b.Rotation);
        Assert.Equal(parts.Where(p => !p.Interior).Select(p => p.Source.StringId), outside.Select(p => p.Source.StringId));

        // A character standing in the bar is inside its trigger hull; one in the street outside is not.
        var volume = InteriorVolume.For(db, record, b.InstanceId, position, b.Rotation, new InteriorCollisions(install!));
        Assert.NotNull(volume);
        Assert.True(volume.Contains(new System.Numerics.Vector3(-51189, 1581, 2642), 2));
        Assert.False(volume.Contains(new System.Numerics.Vector3(-51189, 1581, 2800), 2));
        // The floor under a character: the bar room is floor 0, the roof terrace (where the beds stand) floor 1.
        Assert.Equal(0, volume.FloorAt(new System.Numerics.Vector3(-51189, position.Y + 4, 2642)));
        Assert.Equal(1, volume.FloorAt(new System.Numerics.Vector3(-51210, position.Y + 71, 2619)));

        // The bar's furniture: the beds stand on floor 1 (hidden at floor 0), the counter on floor 0.
        var layout = new BuildingLayouts(db, levels.Interiors).Find(record, "Bar", exterior: false)!;
        var items = new BuildingLayouts(db, levels.Interiors).Items(layout).ToList();
        Assert.All(items.Where(i => i.Building.Name == "Bed"), i => Assert.Equal(1, i.Floor));
        Assert.Equal(0, items.First(i => i.Building.Name == "Shop Counter").Floor);
    }
}
