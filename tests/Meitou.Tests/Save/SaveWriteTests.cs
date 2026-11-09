using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using static Meitou.Tests.Save.SaveSamples;

namespace Meitou.Tests.Save;

/// <summary>
/// Writing saves: new records, derived data, and the temp-folder commit (docs/formats/save.md).
/// </summary>
public partial class SaveWriteTests
{
    static SaveGame Small(out SavePlatoon platoon)
    {
        var b = new SaveBuilder(7);
        b.Game.Camera.Day = 3;
        b.Game.Camera.Hour = 9;
        b.Game.Camera.Minute = 41;
        b.AddFaction("204-gamedata.base", "Nameless", true, 1000);
        var other = b.AddFaction("10-x", "Other", false, 1000,
                [new SaveRelation("10-x", 100, 0, 0), new SaveRelation("204-gamedata.base", -20, 0, 0)], platoonCounter: 1);

        Assert.Equal(-20, other.Relations[1].Relation);

        b.AddTown("Town state A", "9-x-INGAME", "10-x", false, new ZoneCoordinate(20, 30));
        b.AddEmptyWorldState();
        platoon = b.AddPlatoon("Nameless_0", "Nameless", "204-gamedata.base", "5-x", new Vector3(100, 20, 300), money: 12);
        b.AddCharacter(platoon, "7-x", "Beep", "204-gamedata.base", new Vector3(100, 20, 300), Quaternion.Identity, isLeader: true);
        b.AddCharacter(platoon, "8-x", "Boop", "204-gamedata.base", new Vector3(103, 20, 300), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1));
        b.Game.Camera.SetMods(["gamedata.base", "x.mod"]);
        b.Game.UpdateDerived();
        return b.Game;
    }

    [Fact]
    public void A_failed_install_puts_the_old_save_back()
    {
        var game = Small(out _);
        string root = Temp("failinstall");
        string folder = Path.Combine(root, "slot");
        try
        {
            game.Write(folder);
            var before = File.ReadAllBytes(Path.Combine(folder, "quick.save"));
            game.Camera.Day = 99;

            Assert.Throws<IOException>(() => game.Write(folder, () => throw new IOException("locked")));

            // Between the two renames the old folder was moved aside; it must be back, whole, and nothing else left.
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(folder, "quick.save")));
            Assert.False(Directory.Exists(folder + ".old"));
            Assert.DoesNotContain(Directory.GetDirectories(root), d => Path.GetFileName(d).StartsWith("_current"));

            game.Write(folder);

            Assert.Equal(99, SaveGame.Load(folder).Camera.Day);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
