using Meitou.Content;
using Meitou.Data.Ogre;

/// <summary>Compiles every Ogre material script and reports what they contain and what fails to resolve.</summary>
static class MaterialSurvey
{
    public static int Run(GameInstall install)
    {
        var all = OgreMaterialLibrary.LoadAll(install, out var dataIndex);
        var configured = OgreMaterialLibrary.LoadConfigured(install, out var resources);

        Console.WriteLine($"All scripts under data/: {all.Files.Count} files compiled (.program + .material), {all.Diagnostics.Count(d => d.Code == OgreScriptError.Fatal)} failed");
        Console.WriteLine($"  {all.Materials.Count} materials ({all.DuplicateMaterials.Count} duplicate definitions), {all.Programs.Count} GPU programs");
        Console.WriteLine($"resources.cfg folders: {resources.Locations.Count} ({resources.Skipped.Count} skipped: {string.Join(", ", resources.Skipped.Select(s => $"{s.Type}={Path.GetFileName(s.Path)}"))})");
        Console.WriteLine($"  {configured.Files.Count} files, {configured.Materials.Count} materials, {configured.Programs.Count} GPU programs");
        var loaded = configured.Files.Select(f => Path.GetFullPath(Path.Combine(resources.Root, f.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outside = all.Files.Select(f => f.Name).Where(n => !loaded.Contains(Path.GetFullPath(Path.Combine(dataIndex.Root, n)))).ToList();
        Console.WriteLine($"  script files outside the configured folders: {string.Join(", ", outside)}");

        var techniques = all.Materials.Values.SelectMany(m => m.Techniques).ToList();
        var passes = techniques.SelectMany(t => t.Passes).ToList();
        var units = passes.SelectMany(p => p.TextureUnits).ToList();
        Console.WriteLine($"  {techniques.Count} techniques, {passes.Count} passes, {units.Count} texture units");
        Console.WriteLine($"  inheritance: {all.Materials.Values.Count(m => m.Source.Bases.Count > 0)} materials with a base, " +
                          $"{all.Programs.Values.Count(p => p.Source.Bases.Count > 0)} programs with a base; " +
                          $"set_texture_alias: {all.Materials.Values.Count(m => m.TextureAliases.Count > 0)} materials");
        Console.WriteLine("  top-level objects: " + string.Join(", ", all.TopLevelClasses.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")));

        Console.WriteLine("  property usage (material / technique / pass / texture_unit / program ref):");
        var usage = new Dictionary<string, int>();
        void Count(string scope, OgreScriptObject o)
        {
            foreach (var p in o.Properties) usage[$"{scope}.{p.Name}"] = usage.GetValueOrDefault($"{scope}.{p.Name}") + 1;
        }
        foreach (var m in all.Materials.Values) Count("material", m.Source);
        foreach (var t in techniques) Count("technique", t.Source);
        foreach (var p in passes)
        {
            Count("pass", p.Source);
            foreach (var r in p.Source.Children.OfType<OgreScriptObject>().Where(o => o.Class.EndsWith("_ref", StringComparison.Ordinal)))
                Count(r.Class, r);
        }
        foreach (var u in units) Count("texture_unit", u.Source);
        foreach (var (k, v) in usage.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"    {k,-48} {v,5}");

        var refs = passes.SelectMany(p => new[] { p.VertexProgram, p.FragmentProgram }).OfType<OgreProgramRef>().ToList();
        var missingPrograms = refs.Where(r => !all.Programs.ContainsKey(r.Name)).Select(r => r.Name).Distinct().ToList();
        Console.WriteLine($"  program refs: {refs.Count}, to undefined programs: {missingPrograms.Count} {string.Join(", ", missingPrograms.Take(10))}");

        Console.WriteLine("  diagnostics by code:");
        foreach (var g in all.Diagnostics.GroupBy(d => d.Code).OrderBy(g => g.Key))
        {
            Console.WriteLine($"    {g.Key,-28} {g.Count(),4}");
            foreach (var d in g.Take(5)) Console.WriteLine($"      {d}");
        }

        var audit = OgreMaterialAudit.Run(install, all, dataIndex);
        Console.WriteLine($"Meshes: {audit.MeshFiles} read ({audit.MeshFailures.Count} failed), {audit.SubMeshes} submeshes, {audit.SubMeshesWithTextureAliases} with texture aliases");
        Console.WriteLine($"  distinct mesh material names: {audit.MeshMaterials.Count}, defined: {audit.ResolvedMeshMaterials} " +
                          $"({Percent(audit.ResolvedMeshMaterials, audit.MeshMaterials.Count)}); submeshes resolved: {audit.ResolvedSubMeshes} ({Percent(audit.ResolvedSubMeshes, audit.SubMeshes)})");
        foreach (var (name, users) in audit.UnresolvedMaterials.OrderByDescending(kv => kv.Value.Count).Take(15))
            Console.WriteLine($"    unresolved {name,-32} {users.Count,4} meshes, e.g. {users[0]}");
        var configuredAudit = OgreMaterialAudit.Run(install, configured, resources);
        Console.WriteLine($"  with resources.cfg scripts only: {configuredAudit.ResolvedMeshMaterials} names defined ({Percent(configuredAudit.ResolvedMeshMaterials, configuredAudit.MeshMaterials.Count)})");

        Console.WriteLine($"Textures: {audit.TextureReferences} references, {audit.Textures.Count} distinct, found under data/: {audit.FoundTextures} " +
                          $"({Percent(audit.FoundTextures, audit.Textures.Count)}); units naming no file: {audit.UnitsWithoutTexture}");
        foreach (var (name, users) in audit.MissingTextures.Take(20))
            Console.WriteLine($"    missing {name,-36} used by {string.Join(", ", users.Take(3))}");
        Console.WriteLine($"  in resources.cfg folders: {configuredAudit.FoundTextures} of {configuredAudit.Textures.Count} ({Percent(configuredAudit.FoundTextures, configuredAudit.Textures.Count)})");
        return all.Diagnostics.Any(d => d.Code == OgreScriptError.Fatal) ? 1 : 0;
    }

    static string Percent(int part, int whole) =>
        whole == 0 ? "-" : (100.0 * part / whole).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
