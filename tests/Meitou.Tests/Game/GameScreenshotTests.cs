using Meitou.Game;

namespace Meitou.Tests.Game;

/// <summary>Where a screenshot goes: <c>_screens/screenshot_n.format</c>, n from <c>screens.cfg</c> (<c>FUN_14082b6e0</c>, docs/game/ui-input.md "Screenshots").</summary>
public class GameScreenshotTests
{
    [Fact]
    public void The_count_is_raised_and_the_default_format_is_jpg()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, GameScreenshots.ConfigFile), "count=142\n");
            Assert.Equal(Path.Combine(root, "_screens", "screenshot_143.jpg"), GameScreenshots.Next(root));
            Assert.Equal(Path.Combine(root, "_screens", "screenshot_144.jpg"), GameScreenshots.Next(root));
            Assert.Contains("count=144", File.ReadAllText(Path.Combine(root, GameScreenshots.ConfigFile)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Without_a_config_the_first_picture_is_one_and_the_format_follows_the_file()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.EndsWith("screenshot_1.jpg", GameScreenshots.Next(root));
            File.WriteAllText(Path.Combine(root, GameScreenshots.ConfigFile), "count=7\nformat=png\n");
            Assert.EndsWith("screenshot_8.png", GameScreenshots.Next(root));
            Assert.Equal("jpg", GameScreenshots.FormatOf("PNG"));
        }
        finally { Directory.Delete(root, true); }
    }
}
