using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;

return args switch
{
    ["formats"] => WithInstall(Formats),
    ["fcs", var path] => Fcs(path),
    ["load"] => WithInstall(Load),
    ["meshes"] => WithInstall(MeshSurvey.Run),
    ["skeletons"] => WithInstall(SkeletonSurvey.Run),
    ["materials"] => WithInstall(MaterialSurvey.Run),
    ["fcs-types"] => WithInstall(FcsTypeMatcher.Run),
    ["fcs-records", var type] => WithInstall(i => FcsRecordDump.Run(i, int.Parse(type), 3)),
    ["world"] => WithInstall(WorldSurvey.Run),
    ["image-diff", var a, var b] => ImageDiff.Run(a, b, null),
    ["image-diff", var a, var b, var d] => ImageDiff.Run(a, b, d),
    ["draw-log-diff", var a, var b] => DrawLogDiff.Run(a, b),
    ["draw-log-diff", var a, var b, "--keep-handles"] => DrawLogDiff.Run(a, b, keepHandles: true),
    ["world-map", var png] => WithInstall(i => WorldSurvey.RenderMap(i, png)),
    ["world-map", var png, var step] => WithInstall(i => WorldSurvey.RenderMap(i, png, int.Parse(step))),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        meitou-tools formats       count file types under the install's data/ folder
        meitou-tools fcs <file>    summarize a .mod/.base file (path, or a name inside data/)
        meitou-tools load          apply the load order and summarize the merged game data
        meitou-tools meshes        read every .mesh in data/ and report failures and contents
        meitou-tools skeletons     read every .skeleton in data/ and report failures and contents
        meitou-tools materials     compile every material script and cross-check mesh materials and textures
        meitou-tools fcs-types     match record type numbers to fcs.def type names
        meitou-tools fcs-records N show the fields of the first records of type N in the base game
        meitou-tools world         read the heightmap, zone/level files and features.dat and cross-check them
        meitou-tools image-diff <a.png> <b.png> [diff.png]  compare two screenshots: mean difference, share of pixels over 12/255
        meitou-tools draw-log-diff <a> <b> [--keep-handles]  compare two MEITOU_DRAW_LOG files draw by draw (handles renamed by first use)
        meitou-tools world-map <png> [step]  render a top-down world map (every step-th height sample, default 16)
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
    if (file.Merge is { } merge)
        Console.WriteLine($"  save counter: {merge.SaveCounter}, last merge resolve: {merge.LastMergeResolve}, merged mods: {merge.Merged.Count}");
    if (file.DeleteRequests is { } deletes)
        Console.WriteLine($"  delete requests: {deletes.Count}");
    Console.WriteLine($"  records: {file.Records.Count(r => !r.IsModified)} new, {file.Records.Count(r => r.IsModified)} modifying ({file.Records.Count(r => r.IsRenamed)} renaming), {file.Records.Count(r => r.IsRemoved)} removing");
    if (file.HeaderTail.Length > 0)
        Console.WriteLine($"  header tail:  {Convert.ToHexString(file.HeaderTail)}");

    Console.WriteLine("  records by type:");
    foreach (var g in file.Records.GroupBy(r => r.Type).OrderByDescending(g => g.Count()))
        Console.WriteLine($"    {g.Key,4} {(Enum.IsDefined((FcsRecordType)g.Key) ? (FcsRecordType)g.Key : "?"),-30} {g.Count(),7}  e.g. {g.First().Name}");

    Console.WriteLine("  flags:");
    foreach (var g in file.Records.GroupBy(r => r.Flags).OrderByDescending(g => g.Count()))
        Console.WriteLine($"    0x{g.Key:X8} {g.Count(),7}");

    // The reader rejects invalid UTF-8, so non-ASCII strings here are known-good samples.
    var strings = file.Records.SelectMany(r => r.Strings.Values.Prepend(r.Name)).ToList();
    Console.WriteLine($"  strings: {strings.Count}, non-ASCII: {strings.Count(s => s.Any(c => c >= 0x80))}");
    return 0;
}

static int Load(GameInstall install)
{
    var order = LoadOrder.FromInstall(install);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var db = GameDatabase.Load(order);
    Console.WriteLine($"Loaded {order.Entries.Count} files in {sw.ElapsedMilliseconds} ms: {string.Join(", ", order.Entries.Select(e => e.Name))}");
    Console.WriteLine($"  {db.Records.Count} records, {db.Records.Values.Count(r => r.ModifiedBy.Count > 0)} changed by a later file");
    foreach (var g in db.Records.Values.GroupBy(r => r.Type).OrderByDescending(g => g.Count()).Take(10))
        Console.WriteLine($"    {g.Key,-30} {g.Count(),7}");
    var dangling = db.Records.Values.SelectMany(r => r.ReferenceLists.SelectMany(l => r.GetReferences(l)))
        .Count(x => db.Find(x.TargetStringId) is null);
    Console.WriteLine($"  {dangling} references to records that don't exist");
    Console.WriteLine($"  {db.Issues.Count} issues");
    foreach (var g in db.Issues.GroupBy(i => (i.Kind, i.File)))
        Console.WriteLine($"    {g.Key.File}: {g.Key.Kind} x{g.Count()}, e.g. {g.First()}");
    return 0;
}
