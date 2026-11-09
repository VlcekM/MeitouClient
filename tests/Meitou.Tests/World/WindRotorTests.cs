using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Tests.Gameplay;

namespace Meitou.Tests.World;

/// <summary>
/// A wind generator's rotor is read only from the parts its placement spawns (FUN_1404cbd90 over the created parts).
/// </summary>
public class WindRotorTests
{
    [Fact]
    public void Wind_rotor_follows_the_parts_the_position_chooses()
    {
        var db = EconomyTests.Db(
            EconomyTests.Rec("building", FcsRecordType.Building, r => r.References["parts"] = [new("rotor", 1, 50, 0), new("plain", 1, 50, 0)]),
            EconomyTests.Rec("rotor", FcsRecordType.BuildingPart, r =>
            {
                r.Filenames["phs or mesh"] = "rotor.mesh";
                r.Ints["rotation function"] = RotorSpec.WindSpeed;
                r.Floats["wind speed rotation min"] = 3;
                r.Floats["wind speed rotation max"] = 2;
            }),
            EconomyTests.Rec("plain", FcsRecordType.BuildingPart, r => r.Filenames["phs or mesh"] = "plain.mesh"));
        var building = db.Find("building")!;
        int withRotor = 0;
        for (int i = 0; i < 40; i++)
        {
            var at = new Vector3(i * 37.5f, 0, i * 11.25f);
            var chosen = WorldObjectLayout.Building(db, building, "b", at, Quaternion.Identity).Any(m => m.Source.StringId == "rotor");
            var rotor = WorldObjectLayout.WindRotor(db, building, at);
            Assert.Equal(chosen, rotor is not null);
            if (rotor is { } range)
            {
                Assert.Equal((3f, 3f), range);
                withRotor++;
            }
        }
        Assert.InRange(withRotor, 1, 39);
    }
}
