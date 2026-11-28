using Meitou.Data.World;
using Meitou.Game.DevTools.Agents;

namespace Meitou.Tests.Game;

/// <summary>
/// The control channel's building ids (docs/agent-playtest.md): a level building's <c>b:x.y:instance</c> names the zone it is looked up in.
/// </summary>
public class AgentBuildingIdTests
{
    /// <summary>
    /// A level building's id gives its zone; a player site's, a character's or a malformed id gives none. Catches an id from a reply or the town
    /// command that the channel cannot find without a <c>world</c> listing round it first.
    /// </summary>
    [Theory]
    [InlineData("b:24.27:13425-Newwworld-INGAME", 24, 27)]
    [InlineData("b:18.35:486995-zone18.35-INGAME", 18, 35)]
    [InlineData("b:Player_3", -1, -1)]
    [InlineData("c:3.1", -1, -1)]
    [InlineData("b:24:13425", -1, -1)]
    [InlineData("b:x.27:13425", -1, -1)]
    public void A_level_building_id_names_its_zone(string id, int x, int y)
    {
        var zone = AgentOrders.ZoneOfKey(id);

        Assert.Equal(x < 0 ? null : new ZoneCoordinate(x, y), zone);
    }
}
