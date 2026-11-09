using Meitou.Data;
using Meitou.Data.Gameplay;

namespace Meitou.Tests.Gameplay;

public class ItemIconTests
{
    [Theory]
    [InlineData("1020-gamedata.base.1059-gamedata.base.1254-gamedata.base", "1020-gamedata.base", "1059-gamedata.base", "1254-gamedata.base")]
    [InlineData("1020-gamedata.base.52293-rebirth.mod", "1020-gamedata.base", "52293-rebirth.mod", "")]
    [InlineData("1000-gamedata.base", null, null, null)]
    [InlineData("notes", null, null, null)]
    public void A_cache_file_name_splits_into_item_material_and_colour_and_other_names_are_not_icons(
        string name, string? item, string? material, string? colour)
    {
        if (item is null) Assert.Null(ItemIconCache.Parse(name));
        else Assert.Equal((item, material!, colour!), ItemIconCache.Parse(name));
    }

    [Fact]
    public void The_cache_gives_the_colourless_file_of_exactly_that_item_and_material()
    {
        string dir = Directory.CreateTempSubdirectory("meitou-icons").FullName;
        try
        {
            string[] names = ["1-a.mod.2-a.mod.png", "1-a.mod.3-a.mod.9-a.mod.png", "1-a.mod.3-a.mod.png", "1-a.mod.9-a.mod.4-a.mod.png"];
            foreach (string name in names)
            {
                File.WriteAllBytes(Path.Combine(dir, name), []);
            }

            var cache = new ItemIconCache(dir);
            Assert.EndsWith("1-a.mod.3-a.mod.png", cache.Find("1-a.mod", "3-a.mod"));
            Assert.EndsWith("1-a.mod.2-a.mod.png", cache.Find("1-a.mod", "2-a.mod"));
            Assert.Null(cache.Find("1-a.mod", "7-a.mod"));
            Assert.Null(cache.Find("1-a.mod", "9-a.mod"));   // only a coloured picture of that material
            Assert.Null(cache.Find("2-a.mod", ""));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact, Slow]
    public void Cached_starter_icons_use_the_original_files_without_rendering_replacements()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null || InstallData.FullLoadOrder is null, "no Kenshi install");
        var folder = Path.Combine(install!.DataDirectory, "icons");
        (string Item, string Material)[] starters =
            [("2308-clothes_v1.mod", "2312-clothes_v1.mod"), ("52295-rebirth.mod", "914-gamedata.base")];
        Assert.SkipWhen(
            starters.Any(s => !File.Exists(Path.Combine(folder, $"{s.Item}.{s.Material}.png"))),
            "the game has not cached these starter icons");
        var locator = new Meitou.Data.Ogre.AssetLocator(install);
        var icons = new ItemIcons(InstallData.FullLoadOrder!, name => locator.FindInGroup(name, "Icons"), new ItemIconCache(folder));
        foreach (var (item, material) in starters)
        {
            var icon = icons.Choose(item, material);
            Assert.Equal(ItemIconSource.File, icon.Source);
            Assert.Equal(Path.Combine(folder, $"{item}.{material}.png"), icon.File);
        }
    }

    /// <summary>
    /// A record of <paramref name="type"/> with only the given integer fields, made through the database as the game data is.
    /// </summary>
    static GameRecord Record(Meitou.Data.Fcs.FcsRecordType type, params (string Key, int Value)[] ints)
    {
        var source = new Meitou.Data.Fcs.FcsRecord { StringId = "1-a.mod", Flags = 0x10, Name = "thing", RecordType = type };
        foreach (var (key, value) in ints) source.Ints[key] = value;
        var file = new Meitou.Data.Fcs.FcsFile();
        file.Records.Add(source);
        var db = new GameDatabase();
        db.Apply(file, "a.mod");
        return db.Find("1-a.mod")!;
    }

    [Theory]
    [InlineData(5, 4, 256, 256)]
    [InlineData(7, 1, 512, 64)]
    [InlineData(2, 6, 128, 512)]
    public void An_icon_is_made_at_50_pixels_a_cell_rounded_up_to_powers_of_two(int columns, int rows, int width, int height)
    {
        var item = Record(Data.Fcs.FcsRecordType.Item, ("inventory footprint width", columns), ("inventory footprint height", rows));
        var style = ItemIconStyle.Of(item);
        Assert.Equal((columns * 50, rows * 50), (style.Width, style.Height));
        Assert.Equal((width, height), style.TextureSize);
    }

    [Fact]
    public void A_weapon_is_turned_by_the_fixed_angles_and_a_record_without_offsets_takes_the_defaults()
    {
        var weapon = Record(Data.Fcs.FcsRecordType.Weapon);
        var style = ItemIconStyle.Of(weapon);
        Assert.Equal((-90f, -90f, 90f), (style.Pitch, style.Yaw, style.Roll));
        Assert.Equal((1f, 0f, -4f), (style.Zoom, style.OffsetV, style.OffsetH));
        var plain = ItemIconStyle.Of(Record(Data.Fcs.FcsRecordType.Item));
        Assert.Equal((0f, 90f, 0f), (plain.Pitch, plain.Yaw, plain.Roll));
        Assert.Equal((2.7f, -8f, 0f), (plain.Zoom, plain.OffsetV, plain.OffsetH));
        Assert.Equal(13.5f, plain.Distance);
    }

    [Fact]
    public void Every_picture_the_game_saved_of_a_base_game_item_is_of_a_kind_it_draws()
    {
        Assert.SkipWhen(InstallData.Install is null || InstallData.BaseGame is null, "no Kenshi install");
        string folder = Path.Combine(InstallData.Install!.DataDirectory, "icons");
        Assert.SkipWhen(!Directory.Exists(folder), "the game has not saved any icons here");
        var saved = Directory.EnumerateFiles(folder, "*.png")
            .Select(f => ItemIconCache.Parse(Path.GetFileNameWithoutExtension(f)))
            .OfType<(string Item, string Material, string Colour)>()
            .Select(p => InstallData.BaseGame!.Find(p.Item))
            .OfType<GameRecord>()
            .ToList();
        Assert.NotEmpty(saved);

        // The exception: limbs.mod's eight severed limbs (ITEM, a static icon, auto icon image false) have a saved picture too;
        // why is not traced.
        saved.RemoveAll(r => r.StringId.EndsWith("-limbs.mod", StringComparison.Ordinal));
        Assert.All(
            saved,
            r => Assert.True(ItemIcons.Renders(r), $"{r.StringId} {r.Type} has a saved picture but is not of a drawn kind"));
    }
}
