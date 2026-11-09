using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Economy;

namespace Meitou.Tests.Gameplay;

/// <summary>
/// Item prices (docs/game/economy.md sections 3 and 4) on synthetic records, against the worked examples of section 4.6.
/// </summary>
public class EconomyTests
{
    const uint New = 0x10;
    static readonly PriceConstants K = PriceConstants.BaseGame;

    internal static FcsRecord Rec(string id, FcsRecordType type, Action<FcsRecord>? fill = null)
    {
        var r = new FcsRecord { StringId = id, Flags = New, Name = id, RecordType = type };
        fill?.Invoke(r);
        return r;
    }

    internal static GameDatabase Db(params FcsRecord[] records)
    {
        var file = new FcsFile();
        file.Records.AddRange(records);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    static FcsRecord Armour(string id, int cls, int material, int value, int slot) => Rec(id, FcsRecordType.Armour, r =>
    {
        r.Ints["class"] = cls;
        r.Ints["material type"] = material;
        r.Ints["value"] = value;
        r.Ints["slot"] = slot;
        r.Floats["relative price mult"] = 1;
        r.References["part coverage"] = [new FcsReference("chest-t", 100, 0, 0)];
    });

    [Fact]
    public void Weapons_and_armour_match_the_worked_examples()
    {
        var db = Db(
            Rec("katana-t", FcsRecordType.Weapon, r => r.Ints["value"] = 500),
            Rec("edgewalkers-t", FcsRecordType.WeaponManufacturer, r => r.Floats["price mod"] = 1.4f),
            Armour("samurai-t", 3, 3, 100, 5),
            Armour("chain-t", 2, 2, 12000, 8));
        var katana = db.Find("katana-t")!;
        // 4.6: quality 0 / 25 / 50 / 75 / 100, raw and with the category row. The 100 may differ by one with the game's own expf.
        int[] raw = [8804, 16142, 43750, 71357, 78696], priced = [3521, 6456, 17500, 28542, 31478];
        for (int i = 0; i < 5; i++)
        {
            int slack = i == 4 ? 1 : 0;
            Assert.InRange(ItemValue.Of(db, katana, i * 25, false, K), raw[i] - slack, raw[i] + slack);
            Assert.InRange(ItemValue.Of(db, katana, i * 25, true, K), priced[i] - slack, priced[i] + slack);
        }
        Assert.Equal(43750, ItemValue.Of(db, katana, 50, false, K));
        Assert.Equal(17500, ItemValue.Of(db, katana, 50, true, K));
        // 500 x 1.4 is 700 in float (a double model gives 699 and 61162).
        var edge = db.Find("edgewalkers-t");
        Assert.Equal(61250, ItemValue.Of(db, katana, 50, false, K, edge));
        Assert.Equal(24500, ItemValue.Of(db, katana, 50, true, K, edge));

        var samurai = db.Find("samurai-t")!;
        Assert.Equal(100, ItemValue.Of(db, samurai, 0, false, K));   // quality 0 takes the record's value
        Assert.Equal(15450, ItemValue.Of(db, samurai, 50, false, K));
        Assert.Equal(60000, ItemValue.Of(db, samurai, 100, false, K));
        Assert.Equal(60, ItemValue.Of(db, samurai, 0, true, K));
        Assert.Equal(9270, ItemValue.Of(db, samurai, 50, true, K));
        Assert.Equal(36000, ItemValue.Of(db, samurai, 100, true, K));
        var chain = db.Find("chain-t")!;
        Assert.Equal(20587, ItemValue.Of(db, chain, 50, false, K));   // slot 8, x1.5 after the truncations
        Assert.Equal(12352, ItemValue.Of(db, chain, 50, true, K));
    }

    [Fact]
    public void The_base_games_price_block_items_and_shops_match_the_docs_probe()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        Assert.Equal(PriceConstants.BaseGame, PriceConstants.FromDatabase(db));   // section 3: the base values every fallback uses
        var katana = db.OfType(FcsRecordType.Weapon).Single(r => r.Name == "Katana");
        Assert.Equal(500, katana.GetInt("value"));
        Assert.Equal(17500, ItemValue.Of(db, katana, 50, true, PriceConstants.BaseGame));
        var samurai = db.OfType(FcsRecordType.Armour).First(r => r.Name == "Samurai Armour");
        Assert.Equal(9270, ItemValue.Of(db, samurai, 50, true, PriceConstants.BaseGame));
        var goods = db.OfType(FcsRecordType.VendorList).Single(r => r.Name == "all trade goods");
        Assert.Equal(75, VendorList.From(db, goods).Entries.Count);   // 7.2: the items that get a market factor
        Assert.Contains(db.OfType(FcsRecordType.SquadTemplate), r => Data.Gameplay.SquadTemplate.From(r).HasShop);
    }
}
