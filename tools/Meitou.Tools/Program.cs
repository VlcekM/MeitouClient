using Meitou.Content;
using Meitou.Data.Fcs;

return args switch
{
    ["formats"] => WithInstall(Formats),
    ["fcs", var path] => Fcs(path),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        meitou-tools formats       count file types under the install's data/ folder
        meitou-tools fcs <file>    summarize a .mod/.base file (path, or a name inside data/)
        """);
    return 2;
}

static int WithInstall(Func<GameInstall, int> run)
{
    var install = GameInstall.Locate();
    if (install is null)
    {
        Console.Error.WriteLine($"Kenshi install not found. Set {GameInstall.EnvironmentVariable} or create {GameInstall.LocalConfigFile}.");
        return 1;
    }
    return run(install);
}

static int Formats(GameInstall install)
{
    Console.WriteLine($"Kenshi install: {install.Root}");
    foreach (var group in Directory.EnumerateFiles(install.DataDirectory, "*", SearchOption.AllDirectories)
                 .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                 .OrderByDescending(g => g.Count()))
        Console.WriteLine($"{group.Key,-14}{group.Count(),6}");
    return 0;
}

static int Fcs(string path)
{
    if (!File.Exists(path) && GameInstall.Locate() is { } install)
        path = Path.Combine(install.DataDirectory, path);

    var file = FcsReader.ReadFile(path);
    Console.WriteLine($"{Path.GetFileName(path)}: format {(int)file.FileType}, version {file.Version}, {file.Records.Count} records");
    Console.WriteLine($"  dependencies: {string.Join(", ", file.Dependencies)}");
    Console.WriteLine($"  references:   {string.Join(", ", file.References)}");
    if (file.HeaderTail.Length > 0)
        Console.WriteLine($"  header tail:  {Convert.ToHexString(file.HeaderTail)}");

    Console.WriteLine("  records by type:");
    foreach (var g in file.Records.GroupBy(r => r.Type).OrderByDescending(g => g.Count()))
        Console.WriteLine($"    {g.Key,4} {g.Count(),7}  e.g. {g.First().Name}");

    Console.WriteLine("  flags:");
    foreach (var g in file.Records.GroupBy(r => r.Flags).OrderByDescending(g => g.Count()))
        Console.WriteLine($"    0x{g.Key:X8} {g.Count(),7}");

    // U+FFFD means a string wasn't valid UTF-8 and won't survive a round-trip.
    var strings = file.Records.SelectMany(r => r.Strings.Values.Prepend(r.Name)).ToList();
    int nonAscii = strings.Count(s => s.Any(c => c >= 0x80));
    int invalid = strings.Count(s => s.Contains('�'));
    Console.WriteLine($"  strings: {strings.Count}, non-ASCII: {nonAscii}, invalid UTF-8: {invalid}");
    return 0;
}
