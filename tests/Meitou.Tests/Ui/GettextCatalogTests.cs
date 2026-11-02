using Meitou.Localisation;

namespace Meitou.Tests.Ui;

public class GettextCatalogTests
{
    // A layout captions "SKILLS" where the catalog keys "Skills" (docs/native-ui.md "Text of a layout").
    [Fact]
    public void Layout_caption_finds_its_translation_ignoring_case()
    {
        var catalog = GettextCatalog.ReadPo("msgid \"Skills\"\nmsgstr \"技能\"\n\nmsgid \"Map\"\nmsgstr \"地图\"\n\nmsgid \"MAP\"\nmsgstr \"大地图\"\n");

        Assert.Null(catalog.Find("SKILLS"));
        Assert.Equal("技能", catalog.FindIgnoringCase("SKILLS"));
        Assert.Equal("地图", catalog.FindIgnoringCase("map"));
        Assert.Null(catalog.FindIgnoringCase("WEAPON I"));

        catalog.Add(GettextCatalog.ReadPo("msgid \"Weapon I\"\nmsgstr \"武器1\"\n"));
        Assert.Equal("武器1", catalog.FindIgnoringCase("WEAPON I"));
    }
}
