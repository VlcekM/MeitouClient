using System.Reflection;
using System.Text.Json.Nodes;
using Meitou.Game;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Settings;
using Meitou.Settings;

namespace Meitou.Tests.Gameplay;

/// <summary>
/// <see cref="LauncherSchema"/> (docs/launcher.md "Graphics"): the launcher knows the settings only from it, so every key must be
/// one the game reads, and every slider must describe its values.
/// </summary>
public class LauncherSchemaTests
{
    static readonly SettingsSchema Schema = LauncherSchema.Create();
    static IEnumerable<SettingDescription> All => Schema.Sections.SelectMany(s => s.Settings);

    [Fact]
    public void Each_top_level_key_is_a_UserConfig_setting_the_game_reads_back_as_the_launcher_writes_it()
    {
        string dir = Directory.CreateTempSubdirectory("meitou-schema").FullName;
        try
        {
            foreach (var s in All.Where(s => !s.Graphics))
            {
                // A value other than the default, written as the launcher does (a toggle as a bool, a whole number as an integer).
                int number = (int)(s.Default == s.Max ? s.Min : s.Max);
                JsonNode value = s.Kind == SettingKind.Toggle ? JsonValue.Create(!(s.Default >= 0.5f)) : JsonValue.Create(number);
                string file = Path.Combine(dir, UserConfig.FileName);
                File.WriteAllText(file, new JsonObject { [s.Key] = value.DeepClone() }.ToJsonString());
                var config = UserConfig.Load(file);
                // Kept as an unknown key would also come back unchanged: the game must know it.
                Assert.False(config.Unknown?.ContainsKey(s.Key) ?? false, $"{s.Key} is not a UserConfig setting");
                config.Save();
                var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                Assert.True(JsonNode.DeepEquals(value, saved[s.Key]), $"{s.Key}: wrote {value}, the game saved {saved[s.Key]}");
                Assert.Empty(Directory.GetFiles(dir, "*.bak.json"));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Each_graphics_key_is_one_of_the_Tab_panel_labels_WorldFrame_names()
    {
        var fields = typeof(WorldFrame).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        HashSet<string> Labels(string suffix) => fields
            .Where(f => f.Name.EndsWith(suffix) || f.Name.EndsWith(suffix + "s"))
            .SelectMany(f => f.GetValue(null) switch { string label => [label], string[] many => many, _ => Array.Empty<string>() })
            .ToHashSet();
        var sliders = Labels("Slider");
        var liveSwitches = WorldFrame.LiveSwitches(new WorldOptions(), () => null, () => null, () => null).Select(e => e.Name);
        var switches = Labels("Switch").Concat(liveSwitches)
            .Select(label => SettingsMenu.SwitchKeyPrefix + label).ToHashSet();

        foreach (var s in All.Where(s => s.Graphics)) Assert.Contains(s.Key, s.Kind == SettingKind.Toggle ? switches : sliders);
    }

    /// <summary>
    /// The presets' scope (CLAUDE.md "Graphics presets"): a new graphics setting is a tier row, the look's, or listed outside the tiers with why, so
    /// a preset never leaves one at whatever was saved by oversight; the list names only settings that exist and no tier sets; and the Faithful
    /// look sets every Faithful / Meitou switch but the performance ones, which it keeps.
    /// </summary>
    [Fact]
    public void Each_graphics_setting_is_placed_on_both_the_tiers_and_the_looks()
    {
        var faithful = Schema.Looks!.Faithful.Keys.ToHashSet();
        var kept = LookPresets.PerformanceSwitches.Select(SettingsMenu.SwitchKey).ToHashSet();
        var switches = All.Where(s => s.Graphics && s.Kind == SettingKind.Toggle).Select(s => s.Key);
        Assert.Equal(Array.Empty<string>(), switches.Where(k => !faithful.Contains(k) && !kept.Contains(k)).Order().ToArray());

        var tiers = Schema.Presets!.Keys.ToHashSet();
        var looks = Schema.Looks!.Keys.ToHashSet();
        var graphics = All.Where(s => s.Graphics).Select(s => s.Key).ToHashSet();

        var unplaced = graphics.Where(k => !tiers.Contains(k) && !looks.Contains(k) && !QualityTiers.OutsideTiers.Contains(k)).Order();
        var stale = QualityTiers.OutsideTiers.Where(k => !graphics.Contains(k) || tiers.Contains(k)).Order();
        Assert.Equal(Array.Empty<string>(), unplaced.ToArray());
        Assert.Equal(Array.Empty<string>(), stale.ToArray());
    }

    /// <summary>
    /// The launcher's Faithful look caps the technique choices (docs/launcher.md "Graphics"), which the game reads over the switches they stand for:
    /// a cap that is not the choice with every one of those switches on its Faithful side would leave the indirect light on, or turn the sun's
    /// shadows off, where the game's own Faithful does not; and a choice below its cap (the shadows off) must stay.
    /// </summary>
    [Fact]
    public void Faithful_caps_each_technique_choice_as_the_switches_it_stands_for_read_on_their_Faithful_side()
    {
        var looks = Schema.Looks!;
        var choices = All.Where(s => WorldFrame.ChoiceSliders.Contains(s.Key)).Select(s => s.Key).Order();
        Assert.Equal(choices, looks.FaithfulCaps.Keys.Order());
        foreach (var (key, cap) in looks.FaithfulCaps) Assert.Equal(WorldFrame.ChoiceFromSwitches(key, _ => false, shadows: true), cap);

        var above = looks.FaithfulCaps.ToDictionary(c => c.Key, c => c.Value + 1);
        Assert.Equal(looks.FaithfulCaps.OrderBy(c => c.Key), looks.CappedChoices(key => above[key]).OrderBy(c => c.Key));
        Assert.Empty(looks.CappedChoices(_ => 0));
    }

    [Fact]
    public void Each_setting_is_unique_and_describes_its_values()
    {
        Assert.Equal(All.Count(), All.Select(s => (s.Graphics, s.Key)).Distinct().Count());
        foreach (var s in All)
        {
            Assert.True(s.Max > s.Min, s.Key);
            Assert.InRange(s.Shown, s.Min, s.Max);
            if (s.Default is { } d) Assert.InRange(d, s.Min, s.Max);
            if (s.Labels is { } labels) Assert.Equal((int)(s.Max - s.Min) + 1, labels.Length);
            if (s.Logarithmic) Assert.True(s.Min > 0, s.Key);
        }
    }

    [Fact]
    public void The_written_file_reads_back_in_the_launcher()
    {
        string file = Path.Combine(Directory.CreateTempSubdirectory("meitou-schema").FullName, SettingsSchema.FileName);
        try
        {
            Assert.Equal(0, LauncherSchema.Write(file));
            var read = SettingsSchema.Read(file);
            Assert.NotNull(read);
            Assert.Equal(UserConfig.CurrentVersion, read.SettingsVersion);
            // Keys and kinds only: a switch's shown state reads static flags (GroundRelief) other tests change meanwhile.
            Assert.Equal(All.Select(s => (s.Key, s.Kind)), read.Sections.SelectMany(s => s.Settings).Select(s => (s.Key, s.Kind)));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
