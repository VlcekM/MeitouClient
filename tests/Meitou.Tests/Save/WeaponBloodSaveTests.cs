using System.Text;
using Meitou.Data.Save;

namespace Meitou.Tests.Save;

/// <summary>
/// The weapon blood file and the worn stains it carries (docs/game/blood-decals.md "On weapons and gear", docs/formats/save.md "Weapon blood").
/// </summary>
public class WeaponBloodSaveTests
{
    static readonly Hand Someone = new(1, 2, 3, 4, 5);

    /// <summary>
    /// A character's weapon blood and worn stains come back as written.
    /// </summary>
    [Fact]
    public void WornStainsRoundTrip()
    {
        float[] worn = [0, 0.5f, 1, 0, 0, 0.25f, 0, 0];
        var written = SaveWeaponBlood.Write([new SaveWeaponBlood.Entry(Someone, 0.4f, 0.8f, 0.5f, 0, 0, true, worn)]);

        var read = Assert.Single(SaveWeaponBlood.Read(written));
        Assert.Equal(Someone, read.Hand);
        Assert.Equal(0.4f, read.Amount, 3);
        Assert.True(read.Back);
        Assert.Equal(worn, read.Worn);
    }

    /// <summary>
    /// A character whose weapon is clean but whose clothes are stained keeps the stains (an amount of 0 is no longer dropped when there are some).
    /// </summary>
    [Fact]
    public void StainsWithoutWeaponBloodAreKept()
    {
        var written = SaveWeaponBlood.Write([new SaveWeaponBlood.Entry(Someone, 0, 0, 0, 0, 0, false, [0, 0, 0, 0.7f, 0, 0, 0, 0])]);

        var read = Assert.Single(SaveWeaponBlood.Read(written));
        Assert.Equal(0, read.Amount);
        Assert.Equal(0.7f, read.Worn![3], 3);
    }

    /// <summary>
    /// A file written before the worn stains (no <c>worn</c>) reads as it did: its weapons, no stains; and an entry with neither is dropped.
    /// </summary>
    [Fact]
    public void OlderFilesReadAsBefore()
    {
        const string json = """
            { "format": 1, "weapons": [
              { "hand": [1, 2, 3, 4, 5], "amount": 0.3, "wet": 1, "colour": [0.5, 0, 0], "back": false },
              { "hand": [6, 7, 8, 9, 10], "amount": 0, "wet": 0, "colour": [0, 0, 0], "back": false, "worn": [0, 0, 0, 0, 0, 0, 0, 0] } ] }
            """;

        var read = Assert.Single(SaveWeaponBlood.Read(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(Someone, read.Hand);
        Assert.Null(read.Worn);
    }
}
