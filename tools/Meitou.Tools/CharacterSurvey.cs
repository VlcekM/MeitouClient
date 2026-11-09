using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Impostors;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Ui;
using Meitou.Game.Rendering.Upscalers;
using Meitou.Game.Rendering.Water;

/// <summary>Builds the look of every ANIMAL_CHARACTER and of one character per person race and reports what cannot be drawn: a missing body mesh, skeleton or texture, an unreadable mesh.</summary>
static class CharacterSurvey
{
    public static int Run(GameInstall install, string[] args)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var assets = new AssetLocator(install);
        string? FindMesh(string p) => assets.Configured.Find(Path.GetFileName(p.Replace('\\', '/'))) ?? assets.Find(p);
        var rigs = new CharacterRigs(db, FindMesh, n => assets.Find(n));
        bool verbose = args.Contains("-v");
        int ok = 0, bad = 0;
        var records = db.Records.Values.Where(r => r.Type == FcsRecordType.AnimalCharacter)
            .Concat(db.Records.Values.Where(r => r.Type == FcsRecordType.Character).GroupBy(r => string.Join(",", r.GetReferences("race").Select(x => x.TargetStringId))).Select(g => g.First()))
            .ToList();
        foreach (var r in records)
        {
            var problems = new List<string>();
            CharacterAppearance? c = null;
            try { c = CharacterAppearance.Build(db, install.Root, r.StringId, new CharacterOptions { Seed = 1 }); }
            catch (Exception e) { problems.Add($"build: {e.GetType().Name}: {e.Message}"); }
            if (c is not null)
            {
                string? body = FindMesh(c.BodyMesh);
                if (body is null) problems.Add($"body mesh not found: '{c.BodyMesh}' (race {c.Race.Name})");
                else
                {
                    try
                    {
                        var mesh = OgreMeshReader.ReadFile(body);
                        if (mesh.SkeletonName is null) problems.Add($"body mesh {Path.GetFileName(body)} links no skeleton");
                        else if (rigs.Rig(mesh.SkeletonName, c) is null) problems.Add($"skeleton not found: {mesh.SkeletonName}");
                        if (mesh.SubMeshes.Count == 0) problems.Add("body mesh has no submeshes");
                        var b = mesh.Bounds;
                        if (b is not null && (b.Value.Radius < 0.01f || !float.IsFinite(b.Value.Radius))) problems.Add($"body bounds radius {b.Value.Radius}");
                        if (verbose) Console.WriteLine($"    {Path.GetFileName(body)} skeleton {mesh.SkeletonName} subs {mesh.SubMeshes.Count} bounds {(b is null ? "none" : b.Value.Radius.ToString("0.0"))} materials {string.Join(",", mesh.SubMeshes.Select(s => s.MaterialName))}");
                    }
                    catch (Exception e) { problems.Add($"body mesh {Path.GetFileName(body)}: {e.GetType().Name}: {e.Message}"); }
                }
                if (!string.IsNullOrEmpty(c.BodyTexture) && assets.Find(c.BodyTexture) is null) problems.Add($"body texture not found: {c.BodyTexture}");
                if (!string.IsNullOrEmpty(c.BodyNormal) && assets.Find(c.BodyNormal) is null) problems.Add($"body normal not found: {c.BodyNormal}");
                foreach (var p in c.Parts)
                    if (FindMesh(p.Mesh) is null) problems.Add($"part mesh not found: {p.Record.Type} '{p.Record.Name}' .{p.Field} = {p.Mesh}");
            }
            if (problems.Count == 0) { ok++; if (verbose) Console.WriteLine($"ok   {r.Type} {r.Name} [{r.StringId}]"); continue; }
            bad++;
            Console.WriteLine($"FAIL {r.Type} {r.Name} [{r.StringId}] race {(c?.Race.Name ?? "?")}");
            foreach (var p in problems.Distinct()) Console.WriteLine($"    {p}");
        }
        Console.WriteLine($"{ok} ok, {bad} failing of {records.Count}");
        return 0;
    }
}

/// <summary>Prints every field and list of the records of a name (any type).</summary>
static class RecordDump
{
    public static int Run(GameInstall install, string name)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var names = name.Split('|'); foreach (var r in db.Records.Values.Where(r => names.Contains(r.Name) || names.Contains(r.StringId)))
        {
            Console.WriteLine($"{r.Type} {r.StringId} {r.Name} defined by {r.DefinedBy}, modified by {string.Join(", ", r.ModifiedBy)}");
            foreach (var f in r.Floats.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Ints.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Strings.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Filenames.OrderBy(f => f.Key)) Console.WriteLine($"  FILE {f.Key} = {f.Value}");
            foreach (var f in r.Vector3S.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Vector4S.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Bools.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (string list in r.ReferenceLists) Console.WriteLine($"  LIST {list}: " + string.Join(", ", r.GetReferences(list).Select(x => $"{db.Find(x.TargetStringId)?.Name ?? ("?" + x.TargetStringId)}[{db.Find(x.TargetStringId)?.Type}]={x.Values.Value0},{x.Values.Value1},{x.Values.Value2}")));
        }
        return 0;
    }
}

/// <summary>Resolves the material of every worn ARMOUR and carried CONTAINER and reports what would draw untextured: no MATERIAL_SPECS record, a texture file that is not found.</summary>
static class ItemSurvey
{
    public static int Run(GameInstall install)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var assets = new AssetLocator(install);
        int ok = 0, bad = 0;
        foreach (var item in db.Records.Values.Where(r => r.Type is FcsRecordType.Armour or FcsRecordType.Container).OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            foreach (string suffix in new[] { "", " female" })
            {
                if (item.GetPath("mesh" + suffix).Length == 0) continue;
                var problems = new List<string>();
                var refs = item.GetReferences(suffix.Length > 0 && item.GetReferences("material female").Count > 0 ? "material female" : "material");
                var specs = refs.OrderByDescending(r => r.Values.Value0).Select(r => db.Find(r.TargetStringId)).Where(s => s is { Type: FcsRecordType.MaterialSpecsClothing or FcsRecordType.MaterialSpec or FcsRecordType.MaterialSpecsWeapon }).ToList();
                if (refs.Count == 0) problems.Add("no material list");
                else if (specs.Count == 0) problems.Add($"no usable material record among {string.Join(", ", refs.Select(r => (db.Find(r.TargetStringId)?.Type.ToString() ?? "missing") + ":" + r.TargetStringId))}");
                foreach (var spec in specs)
                    foreach (string field in new[] { "texture map", "normal map", "color map" })
                        if (spec!.GetPath(field) is { Length: > 0 } p)
                        { if (assets.Find(p) is null) problems.Add($"spec '{spec.Name}' {field} not found: {p}"); }
                        else if (field == "texture map") problems.Add($"spec '{spec.Name}' has no texture map");
                if (problems.Count == 0) { ok++; continue; }
                bad++;
                Console.WriteLine($"{item.Type} '{item.Name}' [{item.StringId}] by {item.DefinedBy}{suffix}: mesh {item.GetPath("mesh" + suffix)}");
                foreach (var p in problems.Distinct()) Console.WriteLine($"    {p}");
            }
        }
        Console.WriteLine($"{ok} ok, {bad} with problems");
        return 0;
    }
}

/// <summary>The paint factors of the MATERIAL_SPECS_CLOTHING records that have a colour map: base game against mods.</summary>
static class SpecSurvey
{
    public static int Run(GameInstall install)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var groups = db.Records.Values.Where(r => r.Type == FcsRecordType.MaterialSpecsClothing && r.GetPath("color map").Length > 0)
            .GroupBy(r => (r.DefinedBy.EndsWith(".base") ? "base" : "mod", r.GetFloat("paint factor 1"), r.GetFloat("paint factor 2")))
            .OrderBy(g => g.Key);
        foreach (var g in groups) Console.WriteLine($"{g.Key.Item1} f1 {g.Key.Item2} f2 {g.Key.Item3}: {g.Count()}  e.g. {string.Join(", ", g.Take(3).Select(r => r.Name))}");
        return 0;
    }
}
