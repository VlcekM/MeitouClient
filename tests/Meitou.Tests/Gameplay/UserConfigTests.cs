using System.Text.Json.Nodes;
using Meitou.Game;

namespace Meitou.Tests.Gameplay;

/// <summary><see cref="UserConfig"/> loading (docs/launcher.md "Graphics"): one file shared by every installed version, so a release
/// must keep what it cannot read aside and what it does not know as it was.</summary>
public class UserConfigTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("meitou-config").FullName;
    string File => Path.Combine(dir, UserConfig.FileName);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    // Loads the text, saves it back, returns the config, the saved JSON and the backups beside it.
    (UserConfig Config, JsonObject Saved, string[] Backups) RoundTrip(string json)
    {
        System.IO.File.WriteAllText(File, json);
        var config = UserConfig.Load(File);
        config.Save();
        return (config, (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(File))!, Directory.GetFiles(dir, "meitou.user.*.bak.json"));
    }

    [Fact]
    public void A_value_of_the_wrong_type_takes_its_default_and_the_rest_stay_with_a_copy_kept_aside()
    {
        const string json = """{"fpsLimit": "fast", "vSync": 3, "fullScreen": false, "graphics": {"Object draw distance": "far", "Grass density x": 1.5}}""";
        var (config, _, backups) = RoundTrip(json);

        Assert.Equal(new UserConfig().FpsLimit, config.FpsLimit);
        Assert.False(config.VSync);
        Assert.False(config.FullScreen);
        Assert.Equal(1.5f, config.Graphics["Grass density x"]);
        Assert.False(config.Graphics.ContainsKey("Object draw distance"));
        Assert.Equal(json, System.IO.File.ReadAllText(Assert.Single(backups)));

        // Unparsable json gives the defaults with a copy kept aside (the earlier copy goes first: their names are to the second).
        System.IO.File.Delete(backups[0]);
        const string broken = """{"fpsLimit": 60,""";
        (config, _, backups) = RoundTrip(broken);

        Assert.Equal(new UserConfig().FpsLimit, config.FpsLimit);
        Assert.Equal(broken, System.IO.File.ReadAllText(Assert.Single(backups)));
    }

    [Fact]
    public void A_newer_release_file_keeps_its_version_and_unknown_keys()
    {
        // A file from before versions keeps its values and is saved in the current layout.
        var (old, oldSaved, oldBackups) = RoundTrip("""{"fpsLimit": 90, "graphics": {"Object draw distance": 30000}}""");

        Assert.Equal(90, old.FpsLimit);
        Assert.Equal(30000, old.Graphics["Object draw distance"]);
        Assert.Equal(UserConfig.CurrentVersion, (int)oldSaved["version"]!);
        Assert.Empty(oldBackups);

        var (config, saved, backups) = RoundTrip("""{"version": 3, "fpsLimit": 60, "brandNewKey": {"a": [1, 2]}}""");

        Assert.Equal(60, config.FpsLimit);
        Assert.Equal(3, (int)saved["version"]!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"a": [1, 2]}"""), saved["brandNewKey"]));
        Assert.Empty(backups);
    }
}
