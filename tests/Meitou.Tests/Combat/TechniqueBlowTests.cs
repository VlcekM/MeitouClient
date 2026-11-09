using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Combat;

namespace Meitou.Tests.Combat;

/// <summary>
/// The technique reader keeps every blow its record states (FUN_1406684c0), and none for 0.
/// </summary>
public class TechniqueBlowTests
{
    static CombatTechnique Read(Action<FcsRecord> fill)
    {
        var r = new FcsRecord { StringId = "1-t", Flags = 0x10, Name = "t", RecordType = FcsRecordType.CombatTechnique };
        fill(r);
        var file = new FcsFile();
        file.Records.Add(r);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return CombatTechnique.From(db.OfType(FcsRecordType.CombatTechnique).Single());
    }

    [Fact]
    public void Every_stated_blow_is_kept()
    {
        var t = Read(r =>
        {
            r.Ints["num techniques"] = 4;
            r.Ints["attack direction 3"] = 2;
            r.Ints["power 3"] = 150;
            r.Ints["limb 3"] = 3;
            r.Floats["anim blocked frame 3"] = 0.6f;
            r.Floats["anim stop frame 3"] = 0.7f;
            r.Ints["limb 4"] = 1;
        });
        Assert.Equal(4, t.Blows);
        Assert.Equal((2, 150, 3), (t.DirectionOf(3), t.PowerOf(3), t.LimbOf(3)));
        Assert.Equal(0.6f, t.StrikeProgress(3), 5);
        Assert.Equal(0.7f, t.StopProgress(3), 5);
        Assert.Equal((0, 100, 1), (t.DirectionOf(4), t.PowerOf(4), t.LimbOf(4)));
    }
}
