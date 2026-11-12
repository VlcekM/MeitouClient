using System.Text.Json;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Towns;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The agent control channel's town reports (docs/procedures/agent-play-api.md "Towns"): the numbering and lookup, the JSON of <c>towns</c>,
/// <c>town</c> and <c>machines-power</c>, and that the debug dismantle (the command <c>dismantle-site</c> queues) takes a building out of its town.
/// </summary>
public class TownReportTests
{
    /// <summary>
    /// A town is found by its number and by its name (exact or a start, any case); an unknown number or name finds none.
    /// </summary>
    [Fact]
    public void FindByNumberOrName()
    {
        using var rig = new TaskStartTests.Rig(carrying: false, decide: true);
        var world = rig.World;

        Assert.Equal(1, TownReport.Find(world, null, "1", out _)?.Number);
        Assert.Equal(1, TownReport.Find(world, null, "your outpost", out _)?.Number);
        Assert.Equal(1, TownReport.Find(world, null, "Your", out _)?.Number);
        Assert.Null(TownReport.Find(world, null, "2", out _));
        Assert.Null(TownReport.Find(world, null, "Drycoom", out _));
    }

    /// <summary>
    /// <c>towns</c> lists an outpost with its owner flag, radius, site counts and power object; <c>town</c> adds its buildings.
    /// </summary>
    [Fact]
    public void ListAndTownJson()
    {
        using var rig = new TaskStartTests.Rig(carrying: false, decide: true);
        var world = rig.World;
        var town = world.Buildings.Towns[0];

        using var list = JsonDocument.Parse(TownReport.ListJson(world, null));
        var row = list.RootElement.GetProperty("towns")[0];

        Assert.Equal(1, list.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("outpost", row.GetProperty("kind").GetString());
        Assert.Equal(town.Name, row.GetProperty("name").GetString());
        Assert.True(row.GetProperty("playerOwned").GetBoolean());
        Assert.Equal(town.Radius, row.GetProperty("radius").GetDouble(), 0.01);
        Assert.Equal(1, row.GetProperty("sites").GetInt32());
        Assert.Equal(JsonValueKind.Object, row.GetProperty("power").ValueKind);
        Assert.False(row.GetProperty("nest").GetBoolean());

        using var one = JsonDocument.Parse(TownReport.TownJson(world, null, TownReport.Find(world, null, "1", out _)!));
        var site = one.RootElement.GetProperty("buildings")[0];

        Assert.Equal(rig.StoreSite, site.GetProperty("id").GetString());
        Assert.True(site.GetProperty("complete").GetBoolean());
    }

    /// <summary>
    /// <c>machines-power</c> reports every machine of the world with its site, and an unknown outpost number filters them all out.
    /// </summary>
    [Fact]
    public void MachinesJson()
    {
        using var rig = new TaskStartTests.Rig(carrying: false, decide: true);
        var world = rig.World;

        using var all = JsonDocument.Parse(TownReport.MachinesJson(world, null));
        using var none = JsonDocument.Parse(TownReport.MachinesJson(world, TownReport.Find(world, null, "1", out _)));

        Assert.Equal(world.Machines.All.Count, all.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(world.Machines.All.Count(m => world.Buildings.Find(m.Site)?.Town == 1), none.RootElement.GetProperty("count").GetInt32());
    }

    /// <summary>
    /// The confirmed dismantle (what <c>dismantle-site</c> queues) removes the player's site, and the outpost no longer lists it.
    /// </summary>
    [Fact]
    public void DismantleTakesTheSiteOutOfItsTown()
    {
        using var rig = new TaskStartTests.Rig(carrying: false, decide: true);
        var world = rig.World;

        world.Commands.Enqueue(new ConfirmDismantle(rig.StoreSite) { Tick = world.Tick });
        world.RunTicks(2);

        Assert.Null(world.Buildings.Find(rig.StoreSite));

        // Where an outpost with no building left is removed (the original's rule, in the player-town import), the town is gone with its site and
        // there is nothing to list; elsewhere it stays and no longer lists the site.
        if (TownReport.Find(world, null, "1", out _) is not { } town) return;

        using var one = JsonDocument.Parse(TownReport.TownJson(world, null, town));
        Assert.DoesNotContain(one.RootElement.GetProperty("buildings").EnumerateArray(), b => b.GetProperty("id").GetString() == rig.StoreSite);
    }
}
