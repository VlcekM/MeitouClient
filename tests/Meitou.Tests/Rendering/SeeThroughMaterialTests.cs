using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Tests.Rendering;

public class SeeThroughMaterialTests
{
    /// <summary>
    /// A clothing material record of <paramref name="type"/> (ItemShader), with an opacity map when <paramref name="opacity"/> names one.
    /// </summary>
    static GameRecord Spec(int type, string? opacity)
    {
        var record = new FcsRecord { StringId = "1-t", Flags = 0x10, Name = "glass", RecordType = FcsRecordType.MaterialSpecsClothing };
        record.Ints["material type"] = type;
        record.Filenames["texture map"] = "./data/bottle.dds";
        record.Filenames["normal map"] = "./data/bottle_n.dds";
        if (opacity is not null) record.Filenames[OpacityDither.MapField] = opacity;

        var file = new FcsFile();
        file.Records.Add(record);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db.Find("1-t")!;
    }

    [Fact]
    public void A_clothing_material_is_see_through_only_with_an_opacity_map()
    {
        var plain = MaterialResolver.Clothing(Spec(0, null));
        Assert.Equal(AlphaSource.None, plain.Alpha);
        Assert.Null(plain.Opacity);

        var cutOut = MaterialResolver.Clothing(Spec(1, null));
        Assert.Equal(AlphaSource.NormalAlpha, cutOut.Alpha);
        Assert.Equal(0.5f, cutOut.AlphaThreshold);

        // The opacity map is dithered, not cut at a threshold, and takes the ALPHA type's place.
        var glass = MaterialResolver.Clothing(Spec(1, "./data/bottle_o.dds"));
        Assert.Equal(AlphaSource.Opacity, glass.Alpha);
        Assert.Equal("./data/bottle_o.dds", glass.Opacity);
        Assert.Equal(0f, glass.AlphaThreshold);
    }
}
