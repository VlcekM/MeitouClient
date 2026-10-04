using System.Numerics;
using Meitou.Content;
using Meitou.Data;
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
    public void Parts_keep_group_zero_by_chance_and_pick_one_per_other_group()
    {
        GameReference[] parts = [Part("a", 0, 100), Part("b", 0, 0), Part("c", 1, 50), Part("d", 1, 50), Part("e", 2, 0), Part("f", 2, 10)];
        for (uint seed = 0; seed < 50; seed++)
        {
            var chosen = WorldObjectLayout.ChooseParts(parts, seed).Select(p => p.TargetStringId).ToList();
            Assert.Contains("a", chosen);
            Assert.DoesNotContain("b", chosen);
            Assert.Single(chosen, id => id is "c" or "d");
            Assert.Equal("f", Assert.Single(chosen, id => id is "e" or "f"));
            Assert.Equal(chosen, WorldObjectLayout.ChooseParts(parts, seed).Select(p => p.TargetStringId).ToList());
        }
        var picks = Enumerable.Range(0, 200).Select(s => WorldObjectLayout.ChooseParts(parts, (uint)s).Any(p => p.TargetStringId == "c")).Count(x => x);
        Assert.InRange(picks, 60, 140);
    }

    [Fact]
    public void Base_game_buildings_have_meshes()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.FromInstall(install!));
        var world = WorldLevelData.Load(install!);
        int placed = 0, empty = 0;
        var emptyNames = new HashSet<string>();
        foreach (var b in world.Buildings())
        {
            if (db.Find(b.BuildingId) is not { } record) continue;
            placed++;
            var meshes = WorldObjectLayout.Building(db, record, b.InstanceId, b.Position, b.Rotation);
            if (meshes.Count == 0)
            {
                if (record.GetReferences("parts").Count > 0 && !record.GetBool("is node")) { empty++; emptyNames.Add(record.Name); }
                continue;
            }
            Assert.All(meshes, m => Assert.EndsWith(".mesh", m.MeshPath, StringComparison.OrdinalIgnoreCase));
        }
        // Records without parts (resource markers, a ramp: 1,336 placements, Observed 2026-10-04) draw nothing; all others do.
        Assert.True(empty == 0, $"{empty} of {placed} placed buildings have no mesh: {string.Join(", ", emptyNames)}");

        var features = MapFeatureFile.Open(install!).All().Select((f, i) => WorldObjectLayout.Feature(db, f.Feature, $"{i}")).ToList();
        Assert.True(features.Count(f => f is not null) > features.Count * 0.95);
    }
}
