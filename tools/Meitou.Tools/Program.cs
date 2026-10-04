using Meitou.Content;

var install = GameInstall.Locate();
if (install is null)
{
    Console.Error.WriteLine($"Kenshi install not found. Set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
    return 1;
}

Console.WriteLine($"Kenshi install: {install.Root}");
foreach (var group in Directory.EnumerateFiles(install.DataDirectory, "*", SearchOption.AllDirectories)
             .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
             .OrderByDescending(g => g.Count()))
    Console.WriteLine($"{group.Key,-14}{group.Count(),6}");
return 0;
