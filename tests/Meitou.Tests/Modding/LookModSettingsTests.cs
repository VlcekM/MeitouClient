using Meitou.Game.Settings;
using Meitou.Modding;
using Meitou.Settings;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' look settings in the look presets (docs/modding.md "Look presets"): what <see cref="LookModSettings"/> captures, applies and
/// counts against a preset, and the preset file's <c>mods</c> through its JSON, its share code and a save that brings none.
/// </summary>
public class LookModSettingsTests
{
    sealed class Looks
    {
        [Setting("Outline", Look = true)]
        public bool Outline { get; set; }

        [Setting("Thickness", Min = 0, Max = 4, Step = 0.5, Look = true)]
        public float Thickness { get; set; } = 1;

        [Setting("Style", Options = ["Ink", "Pencil"], Look = true)]
        public string Style { get; set; } = "Ink";

        [Setting("Hotkey hint")]
        public bool NotLook { get; set; }
    }

    [Fact]
    public void A_preset_file_keeps_the_mods_values_through_its_code_and_a_save_without_them()
    {
        var data = new LookPresetData("Toon", "0.24.0", new() { ["Bloom strength"] = 0.2f })
        {
            Mods = new() { ["pza.looks"] = new() { ["Outline"] = "true", ["Thickness"] = "2.5" } },
        };
        var read = LookPresetFile.Read(LookPresetFile.Write(data))!;
        Assert.Equal("2.5", read.Mods["pza.looks"]["Thickness"]);
        Assert.Equal("true", LookPresetFile.FromShareCode(LookPresetFile.ShareCode(data))!.Mods["pza.looks"]["Outline"]);
        Assert.Empty(LookPresetFile.Read("""{ "name": "Old", "values": { "a": 1 } }""")!.Mods);
        Assert.Empty(LookPresetFile.Read("""{ "name": "Odd", "values": {}, "mods": { "m": { "k": 3 }, "n": "x" } }""")!.Mods);

        string folder = Path.Combine(Path.GetTempPath(), "meitou-look-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = LookPresetFile.Save(folder, data);
            LookPresetFile.Save(folder, new LookPresetData("Toon", "0.24.0", new() { ["Bloom strength"] = 0.1f }));   // as the launcher saves
            var kept = LookPresetFile.Read(File.ReadAllText(file))!;
            Assert.Equal(0.1f, kept.Values["Bloom strength"]);
            Assert.Equal("true", kept.Mods["pza.looks"]["Outline"]);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Look_settings_are_captured_applied_and_counted_against_a_preset()
    {
        var target = new Looks();
        var group = new ModSettingsGroup("sample", "Sample", target);
        int changedEvents = 0;
        group.Changed += _ => changedEvents++;
        var looks = new LookModSettings(() => [group]);
        var none = new Dictionary<string, Dictionary<string, string>>();

        Assert.Empty(looks.Changes(none));
        Assert.Empty(looks.Capture(none));

        target.Outline = true;
        target.Thickness = 2.5f;
        target.NotLook = true;
        Assert.Equal(["mod:sample:Outline", "mod:sample:Thickness"], looks.Changes(none));
        var other = new Dictionary<string, Dictionary<string, string>> { ["absent.mod"] = new() { ["X"] = "1" } };
        var captured = looks.Capture(other);
        Assert.Equal(new Dictionary<string, string> { ["Outline"] = "true", ["Thickness"] = "2.5" }, captured["sample"]);
        Assert.Equal("1", captured["absent.mod"]["X"]);   // a mod that is not running keeps its values
        Assert.Empty(looks.Changes(captured));

        // Meitou (no mods' values): the look settings go back to their defaults, the others stay.
        looks.Apply(none);
        Assert.False(target.Outline);
        Assert.Equal(1f, target.Thickness);
        Assert.True(target.NotLook);
        Assert.Equal(2, changedEvents);

        // A preset written by hand: snapped to the step, an unknown choice falls back to the default, and neither counts as a change.
        var hand = new Dictionary<string, Dictionary<string, string>>
        {
            ["sample"] = new() { ["Thickness"] = "2.3", ["Style"] = "Charcoal", ["Outline"] = "true", ["Gone"] = "5" },
        };
        looks.Apply(hand);
        Assert.True(target.Outline);
        Assert.Equal(2.5f, target.Thickness);
        Assert.Equal("Ink", target.Style);
        Assert.Equal(["mod:sample:Style"], looks.Changes(hand));   // the held value is not one of the setting's: it stays a difference
        target.Style = "Pencil";
        Assert.Equal(["mod:sample:Style"], looks.Changes(hand));
    }
}
