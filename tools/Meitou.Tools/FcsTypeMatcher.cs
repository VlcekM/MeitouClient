using Meitou.Content;
using Meitou.Data.Fcs;

/// <summary>
/// Matches FCS record type numbers to fcs.def type names by comparing the field names records actually
/// use with the fields each schema type declares.
/// </summary>
static class FcsTypeMatcher
{
    static readonly string[] BaseFiles = ["gamedata.base", "Newwworld.mod", "Dialogue.mod", "rebirth.mod"];

    public static int Run(GameInstall install)
    {
        var schema = FcsSchema.Load(Path.Combine(install.Root, "fcs.def"));

        // type number -> field name -> (data kind, records using it)
        var used = new SortedDictionary<int, Dictionary<string, (string Kind, int Count)>>();
        var recordCounts = new Dictionary<int, int>();
        foreach (var name in BaseFiles)
            foreach (var record in FcsReader.ReadFile(Path.Combine(install.DataDirectory, name)).Records)
            {
                recordCounts[record.Type] = recordCounts.GetValueOrDefault(record.Type) + 1;
                if (!used.TryGetValue(record.Type, out var fields)) used[record.Type] = fields = [];
                foreach (var (key, kind) in KeysOf(record))
                    fields[key] = (kind, fields.GetValueOrDefault(key).Count + 1);
            }

        Console.WriteLine("type records fields  best match (fields found, schema size) | runner-up");
        var best = new Dictionary<int, string>();
        foreach (var (type, fields) in used)
        {
            var ranked = schema.TypeNames
                .Select(t => (Name: t, Hit: fields.Keys.Count(k => schema.FindField(t, k) is not null), Size: schema.FieldsOf(t).Count))
                .Where(s => s.Hit > 0)
                .OrderByDescending(s => s.Hit).ThenBy(s => s.Size)
                .ToList();
            string Fmt(int i) => i < ranked.Count ? $"{ranked[i].Name} ({ranked[i].Hit}/{fields.Count}, {ranked[i].Size})" : "-";
            Console.WriteLine($"{type,4} {recordCounts[type],7} {fields.Count,6}  {Fmt(0)} | {Fmt(1)}");
            if (ranked.Count == 0) continue;

            best[type] = ranked[0].Name;
            var missing = fields.Keys.Where(k => schema.FindField(ranked[0].Name, k) is null).ToList();
            if (missing.Count > 0)
                Console.WriteLine($"{"",20}not in schema: {string.Join(", ", missing.Take(8).Select(k => $"'{k}'"))}");
        }

        // Cross-check: the list a field is stored in vs. the kind fcs.def gives it.
        Console.WriteLine();
        Console.WriteLine("schema kind -> stored in (field occurrences):");
        var kindPairs = new Dictionary<(FcsFieldKind, string), int>();
        foreach (var (type, fields) in used)
            if (best.TryGetValue(type, out var typeName))
                foreach (var (key, (kind, count)) in fields)
                    if (schema.FindField(typeName, key) is { } def)
                        kindPairs[(def.Kind, kind)] = kindPairs.GetValueOrDefault((def.Kind, kind)) + count;
        foreach (var ((schemaKind, dataKind), count) in kindPairs.OrderBy(p => p.Key.Item1).ThenByDescending(p => p.Value))
            Console.WriteLine($"  {schemaKind,-10} -> {dataKind,-9} {count,8}");

        Console.WriteLine();
        Console.WriteLine("schema fields of unknown kind: " + string.Join(", ", schema.TypeNames
            .SelectMany(schema.FieldsOf).Where(f => f.Kind == FcsFieldKind.Unknown)
            .Select(f => $"'{f.Name}: {f.Default}'").Distinct()));
        Console.WriteLine("schema types matched by no type number: " + string.Join(", ", schema.TypeNames.Where(t => !best.ContainsValue(t))));
        Console.WriteLine("type names matched by several numbers: " + string.Join(", ", best.GroupBy(kv => kv.Value)
            .Where(g => g.Count() > 1).Select(g => $"{g.Key} ({string.Join(", ", g.Select(kv => kv.Key))})")));
        Console.WriteLine("'REMOVED' key: " + string.Join(", ", used.Where(u => u.Value.ContainsKey("REMOVED"))
            .Select(u => $"{u.Key}:{u.Value["REMOVED"].Kind}x{u.Value["REMOVED"].Count}")));
        return 0;
    }
    static IEnumerable<(string Key, string Kind)> KeysOf(FcsRecord r) =>
        r.Bools.Keys.Select(k => (k, "bool"))
            .Concat(r.Floats.Keys.Select(k => (k, "float")))
            .Concat(r.Ints.Keys.Select(k => (k, "int")))
            .Concat(r.Vector3s.Keys.Select(k => (k, "vec3")))
            .Concat(r.Vector4s.Keys.Select(k => (k, "vec4")))
            .Concat(r.Strings.Keys.Select(k => (k, "string")))
            .Concat(r.Filenames.Keys.Select(k => (k, "filename")))
            .Concat(r.References.Keys.Select(k => (k, "reference")));
}

static class FcsRecordDump
{
    public static int Run(GameInstall install, int type, int limit)
    {
        int shown = 0;
        foreach (var name in new[] { "gamedata.base", "Newwworld.mod", "Dialogue.mod", "rebirth.mod" })
            foreach (var r in FcsReader.ReadFile(Path.Combine(install.DataDirectory, name)).Records.Where(r => r.Type == type))
            {
                if (shown++ >= limit) return 0;
                Console.WriteLine($"{r.StringId}  \"{r.Name}\"  flags 0x{r.Flags:X}");
                Console.WriteLine($"  bool:   {string.Join(", ", r.Bools.Keys)}");
                Console.WriteLine($"  float:  {string.Join(", ", r.Floats.Keys)}");
                Console.WriteLine($"  int:    {string.Join(", ", r.Ints.Keys)}");
                Console.WriteLine($"  string: {string.Join(", ", r.Strings.Keys)}  file: {string.Join(", ", r.Filenames.Keys)}");
                Console.WriteLine($"  vec:    {string.Join(", ", r.Vector3s.Keys.Concat(r.Vector4s.Keys))}");
                Console.WriteLine($"  refs:   {string.Join(", ", r.References.Select(kv => $"{kv.Key}[{kv.Value.Count}]"))}  instances: {r.Instances.Count}");
            }
        return 0;
    }
}
