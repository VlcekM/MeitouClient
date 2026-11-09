using Meitou.Content;

namespace Meitou.Tests.Content;

public sealed class GameInstallSourcesTests : IDisposable
{
    readonly string _home = Path.Combine(Path.GetTempPath(), "meitou-installs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    string MakeInstall(params string[] parts)
    {
        var root = Path.Combine([_home, .. parts]);
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "data", "gamedata.base"), "");
        return root;
    }

    static string FirstValid(IEnumerable<string> candidates) => candidates.First(GameInstall.IsValid);

    [Fact]
    public void Heroics_list_gives_the_install_paths_and_a_broken_list_gives_none()
    {
        var kenshi = MakeInstall("Games", "Heroic", "Kenshi");
        var list = Path.Combine(_home, ".config", "heroic", "gog_store", "installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        File.WriteAllText(list, $$"""{"installed":[{"appName":"1","install_path":"{{Path.Combine(_home, "Games", "Heroic", "Other").Replace("\\", "\\\\")}}"},{"appName":"2","install_path":"{{kenshi.Replace("\\", "\\\\")}}"}]}""");
        Assert.Equal(kenshi, FirstValid(GameInstallSources.Launchers(_home)));
        File.WriteAllText(list, "{ not json");
        Assert.Empty(GameInstallSources.HeroicInstalls(list));
    }

    [Fact]
    public void A_wine_prefix_also_finds_a_steam_library_install()
    {
        var drive = Path.Combine(_home, "drive_c");
        var kenshi = MakeInstall("drive_c", "Program Files (x86)", "Steam", "steamapps", "common", "Kenshi");
        Assert.Contains(kenshi, GameInstallSources.InPrefix(drive));
    }

    [Fact]
    public void A_crossover_bottle_finds_kenshi_in_its_windows_steam_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "CrossOver bottles are a macOS source");
        var kenshi = MakeInstall("Library", "Application Support", "CrossOver", "Bottles", "Steam", "drive_c",
            "Program Files (x86)", "Steam", "steamapps", "common", "Kenshi");
        Assert.Equal(kenshi, FirstValid(GameInstallSources.Launchers(_home)));
    }

    [Fact]
    public void A_wine_prefixs_registry_names_where_gog_installed_kenshi()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Wine prefixes map Z: to a Linux root");
        var prefix = Path.Combine(_home, "prefix");
        var kenshi = MakeInstall("games", "Kenshi");
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        File.CreateSymbolicLink(Path.Combine(prefix, "dosdevices", "z:"), "/");
        // The layout GOG's installer writes into system.reg (checked on a real Heroic prefix): doubled backslashes, lower-case value names, a Z: path.
        var windows = kenshi.Replace("/", "\\\\");
        File.WriteAllText(Path.Combine(prefix, "system.reg"), "WINE REGISTRY Version 2\n\n"
            + "[Software\\\\Wow6432Node\\\\GOG.com\\\\Games\\\\1193046833] 1778854162\n#time=1dce4746dbb17ce\n"
            + "\"gameID\"=\"1193046833\"\n\"gameName\"=\"Kenshi\"\n"
            + $"\"path\"=\"Z:{windows}\"\n\"workingDir\"=\"Z:{windows}\"\n\n"
            + "[Software\\\\Wow6432Node\\\\GOG.com\\\\Games\\\\1351891846] 1778854162\n\"gameName\"=\"Jazz Jackrabbit 2\"\n"
            + "\"path\"=\"Z:\\\\nowhere\\\\Jazz\"\n");
        Assert.Equal([kenshi], GameInstallSources.WinePrefixInstalls(prefix).Distinct());
        Assert.Equal(Path.Combine("/", "mnt", "Games", "K"), GameInstallSources.ToUnixPath(prefix, @"Z:\mnt\Games\K\"));
        var bare = Path.Combine(_home, "bare");
        Assert.Equal(Path.Combine(bare, "drive_c", "GOG Games", "Kenshi"), GameInstallSources.ToUnixPath(bare, @"C:\GOG Games\Kenshi"));
        Assert.Null(GameInstallSources.ToUnixPath(prefix, "not a windows path"));
    }
}
