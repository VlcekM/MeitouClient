using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Saving;
using SimWorld = Meitou.Simulation.World;
using static Meitou.Tests.Gameplay.EconomyTests;

namespace Meitou.Tests.Simulation;

public class BuildingSaveHeightTests
{
    const string Player = "204-gamedata.base";

    sealed class Surfaces : IWalkability
    {
        public float GroundHeight(float x, float z) => 600; // A floor or water surface must not become the terrain datum.
        public float TerrainHeight(float x, float z) => 120;
        public bool IsWalkable(float x, float z) => true;
        public PathResult FindPath(Vector3 from, Vector3 to) => new([from, to]);
    }

    static BuildSite Site(string id, float y, string? parent = null) => new()
    {
        Id = id, Record = "height-fixture", Position = new Vector3(100, y, 100), Parent = parent,
        Owner = 0, Complete = true, Materials = [],
    };

    static (FcsInstance Instance, FcsRecord State) Saved(SaveGame save, string id)
    {
        var zone = Assert.Single(save.Zones).Value;
        var instance = Assert.Single(zone.BuildingList!.Instances, i => i.Id == id);
        return (instance, Assert.Single(zone.File.Records, r => instance.States.Contains(r.StringId)));
    }

    [Theory]
    [InlineData(123.25f, 3.25f)]
    public void Outdoor_instance_height_is_relative_to_terrain_while_state_height_is_absolute(float worldY, float instanceY)
    {
        using var world = new SimWorld(new WorldSettings(), new Surfaces());
        var site = Site(world.Buildings.NewId(WorldLayout.ZoneOf(100, 100)), worldY);
        world.Buildings.Add(site);
        var save = new SaveBuilder(1).Game;
        BuildingSave.Write(save, world, Player);
        var written = Saved(save, site.Id);

        Assert.Equal(instanceY, written.Instance.Position.Y);
        Assert.Equal(worldY, written.State.Floats["world Y pos"]);
    }

    [Theory]
    [InlineData(167, 22)]
    public void Indoor_offset_is_relative_to_parent_origin_even_when_child_is_written_first(float worldY, float offset)
    {
        using var world = new SimWorld(new WorldSettings(), new Surfaces());
        var parent = Site(world.Buildings.NewId(WorldLayout.ZoneOf(100, 100)), 145);
        var child = Site(world.Buildings.NewId(parent.Zone), worldY, parent.Id);
        world.Buildings.Add(child);
        world.Buildings.Add(parent);
        var save = new SaveBuilder(1).Game;
        BuildingSave.Write(save, world, Player);
        var written = Saved(save, child.Id);

        Assert.Equal(worldY, written.State.Floats["world Y pos"]);
        Assert.Equal(offset, written.State.Floats["player furniture Y offset"]);
        Assert.Equal(offset, written.Instance.Position.Y);
        Assert.Equal(Hand.Read(Saved(save, parent.Id).State, "handle"), Hand.Read(written.State, "isInsideBuilding"));
    }

    [Fact]
    public void Native_house_parent_keeps_its_zone_identity_and_height_when_only_the_furniture_is_player_built()
    {
        using var original = new SimWorld(new WorldSettings(), new Surfaces());
        var parent = Site(original.Buildings.NewId(WorldLayout.ZoneOf(100, 100)), 145);
        var child = Site(original.Buildings.NewId(parent.Zone), 167, parent.Id);
        original.Buildings.Add(parent);
        original.Buildings.Add(child);
        var save = new SaveBuilder(1).Game;
        BuildingSave.Write(save, original, Player);
        Saved(save, parent.Id).State.Strings["owner faction ID"] = "town-owner";
        Saved(save, child.Id).State.Strings.Remove(BuildingSave.MeitouParent);
        var db = Db(Rec("height-fixture", FcsRecordType.Building));
        using var loaded = new SimWorld(new WorldSettings(), new Surfaces());

        Assert.Equal(1, BuildingSave.Read(save, loaded, db, Player, 0));
        Assert.Equal(BuildingKey.Of(parent.Zone, parent.Id), Assert.Single(loaded.Buildings.All).Parent);

        for (int n = 0; n < 2; n++)
        {
            BuildingSave.Write(save, loaded, Player);
            var written = Saved(save, child.Id);

            Assert.Equal(22, written.State.Floats["player furniture Y offset"]);
            Assert.Equal(Hand.Read(Saved(save, parent.Id).State, "handle"), Hand.Read(written.State, "isInsideBuilding"));
        }
    }
}
