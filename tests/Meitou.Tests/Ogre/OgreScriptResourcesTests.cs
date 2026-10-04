using Meitou.Data.Ogre;

namespace Meitou.Tests.Ogre;

public sealed class OgreScriptResourcesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "meitou-res-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(root, recursive: true);

    string Touch(string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return Path.GetFullPath(path);
    }

    OgreScriptResources Config(string text)
    {
        File.WriteAllText(Path.Combine(root, "resources.cfg"), text);
        return OgreScriptResources.ReadConfig(Path.Combine(root, "resources.cfg"), root);
    }

    [Fact]
    public void Sections_register_in_ordinal_order_and_land_is_rewritten()
    {
        Touch("data/newland/land/t.dds");
        Touch("data/gui/g.material");
        Touch("data/materials/m.material");
        var res = Config("[General]\nFileSystem=./data/materials\n[GUI]\nFileSystem=./data/gui\n[Landscape]\nFileSystem=./data/land\n[Bootstrap]\nZip=./data/x.zip\n");

        Assert.Equal(["GUI", "General", "Landscape"], res.Locations.Select(l => l.Group));
        Assert.EndsWith(Path.Combine("newland", "land"), res.Locations[2].Path);
        Assert.Contains(res.Skipped, s => s.Type == "Zip");
        Assert.Equal(["Autodetect", "Bootstrap", "GUI", "General", "Internal", "Landscape"], res.Groups);
    }

    [Fact]
    public void Mod_folders_join_the_matching_group_and_win_by_name()
    {
        var baseTexture = Touch("data/items/armour/a.dds");
        Touch("data/gui/b.dds");
        var res = Config("[General]\nFileSystem=./data/items/armour\n[GUI]\nFileSystem=./data/gui\n");

        var mod = Path.Combine(root, "mods", "M");
        Touch("mods/M/M.mod");
        Touch("mods/M/_preview.png");
        var modTexture = Touch("mods/M/items/armour/a.dds");
        Touch("mods/M/gui/skins/c.dds");
        Touch("mods/M/leveldata/x.level");
        Touch("mods/M/locale/de/LC_MESSAGES/x.po");
        Assert.Equal(baseTexture, res.Find("a.dds"));

        res.AddModFolder(mod);
        Assert.Equal(modTexture, res.Find("a.dds"));
        Assert.Equal("GUI", res.Locations.Single(l => l.Path.EndsWith("skins")).Group);
        Assert.Equal("General", res.Locations.Single(l => l.Path.StartsWith(Path.GetFullPath(mod)) && l.Path.EndsWith("armour")).Group);
        Assert.DoesNotContain(res.Locations, l => l.Path == Path.GetFullPath(mod)); // only .mod and _ files at the root
        Assert.DoesNotContain(res.Locations, l => l.Path.Contains("leveldata") || l.Path.Contains("locale"));
    }

    [Fact]
    public void Find_prefers_the_requested_group_then_ordinal_order()
    {
        var gui = Touch("data/gui/x.dds");
        var general = Touch("data/materials/x.dds");
        var res = Config("[General]\nFileSystem=./data/materials\n[GUI]\nFileSystem=./data/gui\n");

        Assert.Equal(gui, res.Find("x.dds"));
        Assert.Equal(general, res.Find("x.dds", "General"));
        Assert.Equal(["GUI", "General"], res.ScriptFiles("*.dds").Select(f => f == gui ? "GUI" : "General"));
    }
}
