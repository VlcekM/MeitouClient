using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;

namespace Meitou.Tests.Characters;

public class CharacterGeneratorTests
{
    static GameDatabase? cached;
    static readonly Lock gate = new();

    static GameDatabase Database(GameInstall install)
    {
        lock (gate) return cached ??= GameDatabase.Load(LoadOrder.FromInstall(install));
    }

    static string Limits(GameInstall install, string file) => Path.Combine(install.DataDirectory, "editor", file);

    [Fact]
    public void Rounding_goes_half_away_from_zero()
    {
        Assert.Equal(3, CharacterGenerator.Round(2.5f));
        Assert.Equal(-3, CharacterGenerator.Round(-2.5f));
        Assert.Equal(2, CharacterGenerator.Round(2.49f));
        Assert.Equal(-2, CharacterGenerator.Round(-2.49f));
    }

    [Fact]
    public void Quality_follows_the_armour_grade()
    {
        Assert.Equal([5, 20, 40, 60, 80, 95, 20], Enumerable.Range(0, 7).Select(CharacterGenerator.QualityOfGrade));
    }

    [Fact]
    public void Colour_range_blends_tenths_between_stops()
    {
        var range = new AppearanceColourRange("Skin Tone", AppearanceCategory.Body, [Vector3.One, Vector3.Zero, new Vector3(0, 0, 1)], 0, 20, 10, -1, 8);
        Assert.Equal(Vector3.One, range.ColourAt(0));
        Assert.Equal(new Vector3(0.5f), range.ColourAt(5));
        Assert.Equal(Vector3.Zero, range.ColourAt(10));
        Assert.Equal(new Vector3(0, 0, 1), range.ColourAt(20));
    }

    [Fact]
    public void Hsb_round_trips()
    {
        foreach (var rgb in new[] { new Vector3(0.6f, 0.4f, 0.2f), new Vector3(0.1f, 0.8f, 0.5f), new Vector3(0.3f, 0.2f, 0.9f), new Vector3(0.5f) })
        {
            var (h, s, v) = CharacterGenerator.ToHsb(rgb);
            Assert.True(Vector3.Distance(rgb, CharacterAppearance.FromHsb(h, s, v)) < 1e-4f, $"{rgb}");
        }
    }

    [Fact]
    public void Human_editor_limits_read_with_kenshi_defaults()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var male = AppearanceLimits.Read(Limits(install, "editor_data_human.xml"), female: false, defaultDeviation: 0.4f)!;
        var height = male.Ranges.Single(r => r.Name == "Height");
        Assert.Equal((80, 120, 100, 0, 16), (height.Min, height.Max, height.Mid, height.Group, height.Variation)); // 40 × 0.4
        var head = male.Ranges.Single(r => r.Name == "Head size");
        Assert.Equal(2, head.Variation); // random_variation 10 % of 20
        var cheeks = male.Ranges.Single(r => r.Name == "Cheekbones");
        Assert.Equal(("wide_cheekbones", "narrow_cheekbones", 0, AppearanceCategory.Face), (cheeks.Target, cheeks.TargetOpposite, cheeks.Mid, cheeks.Category));
        Assert.Equal(-1, cheeks.Group);
        var skin = male.Colours.Single(c => c.Name == "Skin Tone");
        Assert.Equal((6, 0, 50, 25), (skin.Colours.Count, skin.Min, skin.Max, skin.Mid));
        Assert.Equal(Vector3.One, skin.Colours[0]);

        var female = AppearanceLimits.Read(Limits(install, "editor_data_human.xml"), female: true, defaultDeviation: 0.4f)!;
        Assert.Contains(female.Ranges, r => r.Name == "Breast size");
        Assert.DoesNotContain(male.Ranges, r => r.Name == "Breast size");

        // A Config without a gender is the male one only.
        Assert.NotNull(AppearanceLimits.Read(Limits(install, "editor_data_mkii.xml"), false, 0.4f));
        Assert.Null(AppearanceLimits.Read(Limits(install, "editor_data_mkii.xml"), true, 0.4f));
    }

    [Fact]
    public void Same_seed_same_character_and_seeds_vary()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        string Key(Loadout l) => string.Join("|", l.Describe()) + "|" + l.Appearance.HeadId + l.Appearance.HairId + l.Appearance.BeardId
            + string.Join(",", l.Appearance.Floats.Select(f => $"{f.Key}={f.Value}")) + l.Appearance.SkinTone;
        var a = CharacterGenerator.Generate(db, install.Root, "Dust Bandit", 7);
        var b = CharacterGenerator.Generate(db, install.Root, "Dust Bandit", 7);
        Assert.Equal(Key(a), Key(b));
        var keys = Enumerable.Range(0, 10).Select(s => Key(CharacterGenerator.Generate(db, install.Root, "Dust Bandit", s))).ToHashSet();
        Assert.Equal(10, keys.Count);
    }

    [Fact]
    public void Dust_bandits_roll_by_the_games_rules()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        var faction = CharacterAppearance.Find(db, "Dust Bandits", FcsRecordType.FACTION)!;
        var limits = AppearanceLimits.Read(Limits(install, "editor_data_human.xml"), false, 0.4f)!;
        var heads = new HashSet<string>();
        var skins = new HashSet<Vector3>();
        var faces = new HashSet<int>();
        int crossbows = 0;
        for (int seed = 0; seed < 60; seed++)
        {
            var l = CharacterGenerator.Generate(db, install.Root, "Dust Bandit", seed, faction: faction);
            Assert.Equal("Greenlander", l.Race.Name);
            Assert.False(l.Female); // female chance 0
            Assert.True(l.Generated);

            // Quantity-0 entries never spawn: no Wooden Sandals, no Topper or Katana.
            var worn = l.Clothing.Select(c => c.Record.Name).ToList();
            Assert.Contains("Samurai Boots", worn);
            Assert.DoesNotContain("Wooden Sandals", worn);
            Assert.All(l.Clothing, c => Assert.Contains(c.Quality, new[] { 20, 40 })); // armour grade 1, may go up one
            Assert.All(l.Weapons, w => Assert.Equal("Horse Chopper", w.Weapon.Name));
            if (l.Crossbow is not null)
            {
                crossbows++;
                Assert.Empty(l.Weapons); // the horse chopper is too long to carry with a crossbow
            }
            else
            {
                var w = Assert.Single(l.Weapons);
                Assert.Equal("back", w.Point); // val1 = 100: the back pool
                Assert.Equal("Unknown", w.Manufacturer!.Name);
                Assert.Contains(w.Model!.Name, new[] { "Rusted Junk", "Rusting Blade" });
                Assert.Contains(w.Level, new[] { 5, 10 });
            }

            // The faction's hairstyles allow only "No beard" among the beards.
            Assert.Equal("No beard", db.Find(l.Appearance.BeardId!)!.Name);
            Assert.Contains(l.Appearance.HairId!, faction.GetReferences("hairstyles").Select(r => r.TargetStringId));

            // Sliders stay within mid ± variation; the face is one of "morph num" faces.
            foreach (var r in limits.Ranges.Where(r => r.Target is null && r.Category is AppearanceCategory.Face or AppearanceCategory.Body))
                Assert.InRange(l.Appearance.Get(r.Name, float.NaN), r.Mid - r.Variation, r.Mid + r.Variation);
            Assert.InRange(l.MorphIndex, 0, 4);
            heads.Add(l.Appearance.HeadId!);
            skins.Add(l.Appearance.SkinTone!.Value);
            faces.Add(l.MorphIndex);
        }
        Assert.InRange(crossbows, 2, 20); // Junkbow: absolute chance 15
        Assert.True(heads.Count >= 3);
        Assert.True(skins.Count >= 10);
        Assert.Equal(5, faces.Count);
    }

    [Fact]
    public void Faces_repeat_per_morph_index()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        var byFace = new Dictionary<int, string>();
        for (int seed = 0; seed < 30; seed++)
        {
            var l = CharacterGenerator.Generate(db, install.Root, "Greenlander", seed, female: true);
            string poses = string.Join(",", l.Appearance.Floats.Where(f => f.Key.Contains('_')).Select(f => $"{f.Key}={f.Value}"));
            Assert.NotEmpty(poses);
            if (byFace.TryGetValue(l.MorphIndex, out var seen)) Assert.Equal(seen, poses);
            else byFace[l.MorphIndex] = poses;
        }
        Assert.True(byFace.Values.Distinct().Count() > 1);
    }

    [Fact]
    public void Seeded_build_uses_the_loadout()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        var c = CharacterAppearance.Build(db, install.Root, "Dust Bandit", new CharacterOptions { Seed = 1, Faction = "Dust Bandits" });
        Assert.NotNull(c.Loadout);
        Assert.Null(c.BodyFile);
        Assert.NotNull(c.Body); // the generated appearance
        Assert.NotNull(c.Head);
        Assert.Equal(c.Loadout!.Appearance.HeadId, c.Head!.StringId);
        Assert.Equal(Vector3.One - c.Body!.SkinTone!.Value, c.SkinToneParameter);
        foreach (var w in c.Loadout.Weapons)
            Assert.Contains(c.Parts, p => p.Record == w.Weapon && p.Point == w.Point && p.Material == w.Model);
        foreach (var item in c.Loadout.Clothing.Where(i => i.Record.GetPath("mesh").Length > 0))
            Assert.Contains(c.Parts, p => p.Record == item.Record && p.Material == item.Material);
    }

    [Fact]
    public void Body_files_are_kept()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var l = CharacterGenerator.Generate(Database(install), install.Root, "Ruka", 3);
        Assert.False(l.Generated);
        Assert.True(l.Female);
        Assert.Equal(-1, l.MorphIndex);
        Assert.Equal(16f, l.Appearance.Get("Age"));
    }
}
