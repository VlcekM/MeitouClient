using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;

namespace Meitou.Tests.Characters;

public class CharacterGeneratorTests
{
    static GameDatabase? s_cached;
    static readonly Lock Gate = new();

    static GameDatabase Database(GameInstall install)
    {
        lock (Gate) return s_cached ??= GameDatabase.Load(LoadOrder.BaseGame(install));
    }

    static string Limits(GameInstall install, string file) => Path.Combine(install.DataDirectory, "editor", file);

    [Fact]
    [Slow]
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
        Assert.Equal(
            ("wide_cheekbones", "narrow_cheekbones", 0, AppearanceCategory.Face),
            (cheeks.Target, cheeks.TargetOpposite, cheeks.Mid, cheeks.Category));
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
    [Slow]
    public void Dust_bandits_roll_by_the_games_rules()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = Database(install);
        var faction = CharacterAppearance.Find(db, "Dust Bandits", FcsRecordType.Faction)!;
        var limits = AppearanceLimits.Read(Limits(install, "editor_data_human.xml"), false, 0.4f)!;
        var heads = new HashSet<string>();
        var skins = new HashSet<Vector3>();
        var faces = new HashSet<int>();
        var races = new HashSet<string>();
        int crossbows = 0;
        for (int seed = 0; seed < 60; seed++)
        {
            var l = CharacterGenerator.Generate(db, install.Root, "Dust Bandit", seed, faction: faction);
            // No race of its own and no body file: one of the faction's races (Faction::chooseARace), Shek among them.
            Assert.Contains(l.Race.StringId, faction.GetReferences("races").Select(r => r.TargetStringId));
            races.Add(l.Race.Name);
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

            // The faction's hairstyles allow only "No beard" among the beards (a race without beards has none).
            if (l.Appearance.BeardId is { } beard) Assert.Equal("No beard", db.Find(beard)!.Name);
            Assert.Contains(l.Appearance.HairId!, faction.GetReferences("hairstyles").Select(r => r.TargetStringId));

            // The sliders of a race with editor_data_human.xml stay within its mid ± variation; the face is one of "morph num" faces.
            if (l.Race.GetPath("editor limits").EndsWith("editor_data_human.xml", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var r in limits.Ranges.Where(r => r.Target is null && r.Category is AppearanceCategory.Face or AppearanceCategory.Body))
                    Assert.InRange(l.Appearance.Get(r.Name, float.NaN), r.Mid - r.Variation, r.Mid + r.Variation);
            }
            Assert.InRange(l.MorphIndex, 0, 4);
            heads.Add(l.Appearance.HeadId!);
            skins.Add(l.Appearance.SkinTone!.Value);
            faces.Add(l.MorphIndex);
        }
        Assert.Contains("Greenlander", races);
        Assert.True(races.Count >= 2);
        Assert.InRange(crossbows, 2, 20); // Junkbow: absolute chance 15
        Assert.True(heads.Count >= 3);
        Assert.True(skins.Count >= 10);
        Assert.Equal(5, faces.Count);
    }
}
