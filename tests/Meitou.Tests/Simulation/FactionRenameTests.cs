using Meitou.Simulation;
using Meitou.Data.World;
using SimWorld = Meitou.Simulation.World;
using Xunit;

namespace Meitou.Tests.Simulation;

public sealed class FactionRenameTests
{
    /// <summary>FUN_1404923d0 has no empty-name test: an empty name is written like any other.</summary>
    [Fact]
    public void An_empty_faction_name_is_accepted_like_the_original()
    {
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150), [new PlayerSystem()]);
        world.Player.Faction = 1;
        world.Commands.Enqueue(new RenameFactionCommand("") { Tick = 0 });
        world.ApplyPausedInterfaceCommands();
        Assert.Equal("", world.Player.FactionName);
    }
}
