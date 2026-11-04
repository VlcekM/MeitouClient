using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The AI's one ignore map (<c>AITaskSytem.pathFailures</c>, <c>notifyPathImpossible</c> <c>FUN_140796ed0</c>, <c>isPathImpossible</c> <c>FUN_1405b0290</c>) and the original's 3x3 zone window of its
/// building finders (<c>FUN_1409fc450</c>), as Meitou's shared helpers (docs/game/ai-tasks.md "Claims", docs/game/buildings-production.md).
/// </summary>
public class IgnoreMapTests
{
    [Fact]
    public void A_site_and_a_character_are_ignored_until_their_tick_in_the_same_map()
    {
        var cold = new CharacterCold();
        var target = new CharacterId(4, 2);
        cold.NoteUnreachable("machine-1", 100);
        cold.Ignore(IgnoreKey.Of(target), 150);
        Assert.True(cold.IsUnreachable("machine-1", 99));
        Assert.False(cold.IsUnreachable("machine-1", 100));
        Assert.False(cold.IsUnreachable("machine-2", 0));
        Assert.True(cold.IsIgnored(IgnoreKey.Of(target), 149));
        Assert.False(cold.IsIgnored(IgnoreKey.Of(new CharacterId(4, 3)), 0));
        Assert.False(cold.IsIgnored(IgnoreKey.Of(target), 150));
    }

    [Fact]
    public void Noting_a_target_again_re_arms_it_instead_of_adding_a_second_entry()
    {
        var cold = new CharacterCold();
        cold.NoteUnreachable("machine-1", 100);
        cold.NoteUnreachable("machine-1", 400);
        Assert.Single(cold.Ignored!);
        Assert.True(cold.IsUnreachable("machine-1", 399));
    }

    [Fact]
    public void The_zone_window_is_the_zone_and_its_eight_neighbours()
    {
        const double z = WorldLayout.ZoneSize;
        Assert.True(WorldLayout.WithinOneZone(10, 10, 10, 10));
        Assert.True(WorldLayout.WithinOneZone(z - 1, 10, z + 1, 10));      // across a border
        Assert.True(WorldLayout.WithinOneZone(10, 10, 2 * z - 1, 2 * z - 1));   // the far corner of the next zone
        Assert.False(WorldLayout.WithinOneZone(10, 10, 2 * z + 1, 10));    // two zones away
        Assert.False(WorldLayout.WithinOneZone(10, 10, 10, -2 * z + 1));
    }
}
