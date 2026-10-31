using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Saving;

namespace Meitou.Tests.Save;

/// <summary>A save's town platoon the game never made (docs/formats/save.md "NPC platoons", Observed on one save): which ones become stand-ins, and which come in at load.</summary>
public class UnmadePlatoonTests
{
    static SavePlatoon Platoon(bool neverActivated = true, int charCount = 8, int homeType = (int)FcsRecordType.BUILDING, string contentFile = "platoon/")
    {
        var r = new FcsRecord { Type = (int)FcsRecordType.PLATOON, StringId = "Shinobi Thieves_0" };
        r.Bools["never been activated"] = neverActivated;
        r.Bools["persistent"] = true;
        r.Bools["is resident"] = false;
        r.Ints["char count"] = charCount;
        r.Filenames["content file"] = contentFile;
        new Hand(homeType, 2133, 11111, 5, 750000000).Write(r, "homebuilding");
        return new SavePlatoon(r, contentFile.Length > "platoon/".Length ? new SaveFile() : null);
    }

    [Fact]
    public void Only_an_unfiled_never_activated_platoon_with_members_and_a_home_building_is_unmade()
    {
        Assert.True(SaveLoader.Unmade(Platoon()));
        Assert.False(SaveLoader.Unmade(Platoon(neverActivated: false)));
        Assert.False(SaveLoader.Unmade(Platoon(charCount: 0)));                       // the save's Holy Nation Outlaws that never appear
        Assert.False(SaveLoader.Unmade(Platoon(homeType: Hand.NullType)));           // roaming platoons: no home building
        Assert.False(SaveLoader.Unmade(Platoon(homeType: (int)FcsRecordType.TOWN))); // a home that is not a building
        Assert.False(SaveLoader.Unmade(Platoon(contentFile: "platoon/Shinobi Thieves_0.platoon")));   // a file: its own characters
    }

    static Meitou.Simulation.Platoon Stand(bool unmade, Vector2 at) =>
        new() { Id = 1, Origin = 0, TemplateId = "51709-rebirth.mod", Key = 1, Unmade = unmade, Position = at };

    [Fact]
    public void An_unmade_platoon_comes_in_at_load_only_in_the_player_box()
    {
        var box = new HashSet<ZoneCoordinate> { new(20, 32), new(21, 32) };
        // Position is X and Z: (20, 32) is the player's zone, (20, 33) a zone of the camera block outside the box.
        var inBox = Stand(true, new Vector2(-51039.9f, 3656.3f));
        var outside = Stand(true, new Vector2(-51727.4f, 4880.7f));
        var ordinary = Stand(false, new Vector2(-51727.4f, 4880.7f));
        Assert.Equal(new ZoneCoordinate(20, 33), WorldLayout.ZoneOf(outside.Position.X, outside.Position.Y));
        Assert.True(PopulationSystem.ActivatesAtLoad(inBox, box));
        Assert.False(PopulationSystem.ActivatesAtLoad(outside, box));
        Assert.True(PopulationSystem.ActivatesAtLoad(ordinary, box));
        Assert.Equal(5, outside.DwellLeft);   // it waits its dwell from 5 s
    }
}
