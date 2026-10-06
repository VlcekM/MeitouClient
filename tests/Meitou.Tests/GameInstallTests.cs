using Meitou.Content;

namespace Meitou.Tests;

public class GameInstallTests
{
    [Fact]
    public void Rejects_directory_without_gamedata()
    {
        var dir = Directory.CreateTempSubdirectory("meitou-").FullName;
        try
        {
            Assert.False(GameInstall.IsValid(dir));
            Assert.Throws<DirectoryNotFoundException>(() => GameInstall.Open(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    [Slow]
    public void Opens_real_install_when_configured()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        Assert.True(File.Exists(Path.Combine(install!.DataDirectory, "gamedata.base")));
    }
}
