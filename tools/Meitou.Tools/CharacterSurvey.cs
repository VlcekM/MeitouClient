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

/// <summary>
/// Builds the look of every ANIMAL_CHARACTER and of one character per person race and reports what cannot be drawn: a missing body mesh, skeleton
/// or texture, an unreadable mesh.
/// </summary>
static class CharacterSurvey
{
    /// <summary>
    /// Surveys the records (<c>-v</c> in <paramref name="args"/>: every record and body mesh listed too); prints the failing ones and the counts.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var assets = new AssetLocator(install);
        string? FindMesh(string p) => assets.Configured.Find(Path.GetFileName(p.Replace('\\', '/'))) ?? assets.Find(p);
        var rigs = new CharacterRigs(db, FindMesh, n => assets.Find(n));
        bool verbose = args.Contains("-v");
        int ok = 0, bad = 0;
        var records = SurveyedRecords(db);
        foreach (var r in records)
        {
            var problems = new List<string>();
            CharacterAppearance? c = null;
            try
            {
                c = CharacterAppearance.Build(db, install.Root, r.StringId, new CharacterOptions { Seed = 1 });
            }
            catch (Exception e)
            {
                problems.Add($"build: {e.GetType().Name}: {e.Message}");
            }

            if (c is not null) CheckLook(c, assets, FindMesh, rigs, verbose, problems);

            if (problems.Count == 0)
            {
                ok++;
                if (verbose) Console.WriteLine($"ok   {r.Type} {r.Name} [{r.StringId}]");
                continue;
            }

            bad++;
            Console.WriteLine($"FAIL {r.Type} {r.Name} [{r.StringId}] race {(c?.Race.Name ?? "?")}");
            foreach (var p in problems.Distinct()) Console.WriteLine($"    {p}");
        }

        Console.WriteLine($"{ok} ok, {bad} failing of {records.Count}");
        return 0;
    }

    /// <summary>
    /// Every ANIMAL_CHARACTER, then the first CHARACTER of each race list.
    /// </summary>
    static List<GameRecord> SurveyedRecords(GameDatabase db)
    {
        var people = db.Records.Values
            .Where(r => r.Type == FcsRecordType.Character)
            .GroupBy(r => string.Join(",", r.GetReferences("race").Select(x => x.TargetStringId)))
            .Select(g => g.First());

        return db.Records.Values
            .Where(r => r.Type == FcsRecordType.AnimalCharacter)
            .Concat(people)
            .ToList();
    }

    /// <summary>
    /// The look's problems into <paramref name="problems"/>: its body mesh (<see cref="CheckBodyMesh"/>), its body textures and its parts' meshes.
    /// </summary>
    static void CheckLook(CharacterAppearance c, AssetLocator assets, Func<string, string?> findMesh, CharacterRigs rigs, bool verbose,
        List<string> problems)
    {
        string? body = findMesh(c.BodyMesh);
        if (body is null) problems.Add($"body mesh not found: '{c.BodyMesh}' (race {c.Race.Name})");
        else CheckBodyMesh(body, c, rigs, verbose, problems);

        if (!string.IsNullOrEmpty(c.BodyTexture) && assets.Find(c.BodyTexture) is null)
            problems.Add($"body texture not found: {c.BodyTexture}");
        if (!string.IsNullOrEmpty(c.BodyNormal) && assets.Find(c.BodyNormal) is null)
            problems.Add($"body normal not found: {c.BodyNormal}");

        foreach (var p in c.Parts)
        {
            if (findMesh(p.Mesh) is null)
                problems.Add($"part mesh not found: {p.Record.Type} '{p.Record.Name}' .{p.Field} = {p.Mesh}");
        }
    }

    /// <summary>
    /// The body mesh file <paramref name="body"/>'s problems into <paramref name="problems"/>: unreadable, no skeleton or one not found, no
    /// submeshes, degenerate bounds; with <paramref name="verbose"/> its summary printed.
    /// </summary>
    static void CheckBodyMesh(string body, CharacterAppearance c, CharacterRigs rigs, bool verbose, List<string> problems)
    {
        try
        {
            var mesh = OgreMeshReader.ReadFile(body);
            if (mesh.SkeletonName is null) problems.Add($"body mesh {Path.GetFileName(body)} links no skeleton");
            else if (rigs.Rig(mesh.SkeletonName, c) is null) problems.Add($"skeleton not found: {mesh.SkeletonName}");

            if (mesh.SubMeshes.Count == 0) problems.Add("body mesh has no submeshes");

            var b = mesh.Bounds;
            if (b is not null && (b.Value.Radius < 0.01f || !float.IsFinite(b.Value.Radius)))
                problems.Add($"body bounds radius {b.Value.Radius}");

            if (verbose)
            {
                string bounds = b is null ? "none" : b.Value.Radius.ToString("0.0");
                string materials = string.Join(",", mesh.SubMeshes.Select(s => s.MaterialName));
                string file = Path.GetFileName(body);
                Console.WriteLine($"    {file} skeleton {mesh.SkeletonName} subs {mesh.SubMeshes.Count} bounds {bounds} materials {materials}");
            }
        }
        catch (Exception e)
        {
            problems.Add($"body mesh {Path.GetFileName(body)}: {e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>
/// Prints every field and list of the records of a name (any type).
/// </summary>
static class RecordDump
{
    /// <summary>
    /// Dumps the records whose name or id is one of <paramref name="name"/>'s, separated by <c>|</c>.
    /// </summary>
    public static int Run(GameInstall install, string name)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var names = name.Split('|');
        foreach (var r in db.Records.Values.Where(r => names.Contains(r.Name) || names.Contains(r.StringId)))
        {
            Console.WriteLine($"{r.Type} {r.StringId} {r.Name} defined by {r.DefinedBy}, modified by {string.Join(", ", r.ModifiedBy)}");
            foreach (var f in r.Floats.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Ints.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Strings.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Filenames.OrderBy(f => f.Key)) Console.WriteLine($"  FILE {f.Key} = {f.Value}");
            foreach (var f in r.Vector3S.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Vector4S.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (var f in r.Bools.OrderBy(f => f.Key)) Console.WriteLine($"  {f.Key} = {f.Value}");
            foreach (string list in r.ReferenceLists) Console.WriteLine(ListLine(db, r, list));
        }

        return 0;
    }

    /// <summary>
    /// A reference list's line: each reference's target name, type and values.
    /// </summary>
    static string ListLine(GameDatabase db, GameRecord r, string list) =>
        $"  LIST {list}: " + string.Join(", ", r.GetReferences(list).Select(x => ReferenceText(db, x)));

    /// <summary>
    /// A reference as its target's name (or ? and its id) and type, then its three values.
    /// </summary>
    static string ReferenceText(GameDatabase db, GameReference x)
    {
        string name = db.Find(x.TargetStringId)?.Name ?? ("?" + x.TargetStringId);
        var type = db.Find(x.TargetStringId)?.Type;

        return $"{name}[{type}]={x.Values.Value0},{x.Values.Value1},{x.Values.Value2}";
    }
}

/// <summary>
/// Resolves the material of every worn ARMOUR and carried CONTAINER and reports what would draw untextured: no MATERIAL_SPECS record, a texture
/// file that is not found.
/// </summary>
static class ItemSurvey
{
    /// <summary>
    /// Surveys the items, each mesh (and female mesh) apart; prints the ones with problems and the counts.
    /// </summary>
    public static int Run(GameInstall install)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var assets = new AssetLocator(install);
        int ok = 0, bad = 0;
        var items = db.Records.Values
            .Where(r => r.Type is FcsRecordType.Armour or FcsRecordType.Container)
            .OrderBy(r => r.Name, StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (string suffix in new[] { "", " female" })
            {
                if (item.GetPath("mesh" + suffix).Length == 0) continue;

                var problems = ItemProblems(db, assets, item, suffix);
                if (problems.Count == 0)
                {
                    ok++;
                    continue;
                }

                bad++;
                Console.WriteLine($"{item.Type} '{item.Name}' [{item.StringId}] by {item.DefinedBy}{suffix}: mesh {item.GetPath("mesh" + suffix)}");
                foreach (var p in problems.Distinct()) Console.WriteLine($"    {p}");
            }
        }

        Console.WriteLine($"{ok} ok, {bad} with problems");
        return 0;
    }

    /// <summary>
    /// What would draw untextured of <paramref name="item"/>'s mesh of <paramref name="suffix"/>: no material list, no usable material record, a
    /// spec's maps not found.
    /// </summary>
    static List<string> ItemProblems(GameDatabase db, AssetLocator assets, GameRecord item, string suffix)
    {
        var problems = new List<string>();
        string list = suffix.Length > 0 && item.GetReferences("material female").Count > 0 ? "material female" : "material";
        var refs = item.GetReferences(list);
        var specs = refs
            .OrderByDescending(r => r.Values.Value0)
            .Select(r => db.Find(r.TargetStringId))
            .Where(s => s is { Type: FcsRecordType.MaterialSpecsClothing or FcsRecordType.MaterialSpec or FcsRecordType.MaterialSpecsWeapon })
            .ToList();
        if (refs.Count == 0)
        {
            problems.Add("no material list");
        }
        else if (specs.Count == 0)
        {
            var found = refs.Select(r => (db.Find(r.TargetStringId)?.Type.ToString() ?? "missing") + ":" + r.TargetStringId);
            problems.Add($"no usable material record among {string.Join(", ", found)}");
        }

        foreach (var spec in specs) CheckSpec(spec!, assets, problems);

        return problems;
    }

    /// <summary>
    /// A material spec's texture, normal and colour maps that are not found, and a missing texture map, into <paramref name="problems"/>.
    /// </summary>
    static void CheckSpec(GameRecord spec, AssetLocator assets, List<string> problems)
    {
        foreach (string field in new[] { "texture map", "normal map", "color map" })
        {
            if (spec.GetPath(field) is { Length: > 0 } p)
            {
                if (assets.Find(p) is null) problems.Add($"spec '{spec.Name}' {field} not found: {p}");
            }
            else if (field == "texture map")
            {
                problems.Add($"spec '{spec.Name}' has no texture map");
            }
        }
    }
}

/// <summary>
/// The paint factors of the MATERIAL_SPECS_CLOTHING records that have a colour map: base game against mods.
/// </summary>
static class SpecSurvey
{
    /// <summary>
    /// Prints each (source, paint factor 1, paint factor 2) group's count and three of its records.
    /// </summary>
    public static int Run(GameInstall install)
    {
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var groups = db.Records.Values
            .Where(r => r.Type == FcsRecordType.MaterialSpecsClothing && r.GetPath("color map").Length > 0)
            .GroupBy(r => (r.DefinedBy.EndsWith(".base") ? "base" : "mod", r.GetFloat("paint factor 1"), r.GetFloat("paint factor 2")))
            .OrderBy(g => g.Key);
        foreach (var g in groups)
        {
            int count = g.Count();
            string examples = string.Join(", ", g.Take(3).Select(r => r.Name));
            Console.WriteLine($"{g.Key.Item1} f1 {g.Key.Item2} f2 {g.Key.Item3}: {count}  e.g. {examples}");
        }

        return 0;
    }
}
