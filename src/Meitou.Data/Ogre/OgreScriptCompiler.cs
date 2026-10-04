namespace Meitou.Data.Ogre;

/// <summary>Ogre's ScriptCompiler error codes (CE_*), plus a few Meitou-only checks marked below.</summary>
public enum OgreScriptError
{
    StringExpected,
    NumberExpected,
    FewerParametersExpected,
    VariableExpected,
    UndefinedVariable,
    ObjectNameExpected,
    ObjectAllocationError,
    InvalidParameters,
    DuplicateOverride,
    UnexpectedToken,
    ObjectBaseNotFound,
    UnsupportedByRenderSystem,
    ReferenceToANonExistingObject,

    /// <summary>Meitou only: an <c>import</c> names a file that cannot be found (Ogre ignores this silently).</summary>
    ImportNotFound,

    /// <summary>Meitou only: a <c>{</c> without its <c>}</c> (Ogre then reads the object as a property and ignores it).</summary>
    UnclosedBrace,

    /// <summary>Meitou only: a <c>}</c> with no open block.</summary>
    UnmatchedBrace,

    /// <summary>Meitou only: a material, program etc. whose name is already defined (see docs: winner Unknown).</summary>
    DuplicateDefinition,

    /// <summary>Meitou only: the file stopped compiling with an exception (lexer/parser error).</summary>
    Fatal,
}

public sealed record OgreScriptDiagnostic(OgreScriptError Code, string File, int Line, string Message = "")
{
    public override string ToString() => $"{File}({Line}): {Code}{(Message.Length > 0 ? ": " + Message : "")}";
}

/// <summary>One compiled script file: its top-level nodes after imports, inheritance and variables are processed.</summary>
public sealed class OgreScriptFile
{
    public string Name { get; init; } = "";
    public List<OgreScriptNode> Nodes { get; init; } = [];
    public List<OgreScriptDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Top-level objects Ogre would translate (abstract ones are skipped).</summary>
    public IEnumerable<OgreScriptObject> Objects => Nodes.OfType<OgreScriptObject>().Where(o => !o.IsAbstract);
}

/// <summary>
/// Compiles Ogre scripts the way Ogre's ScriptCompiler (MIT) does before translation: concrete tree to objects and
/// properties, then <c>import</c>, then <c>:</c> inheritance (overlaying base objects), then <c>$variable</c>
/// expansion. One instance per file, like Ogre's compile(), whose import and variable state is per call.
/// </summary>
public sealed class OgreScriptCompiler
{
    readonly Func<string, string?> importResolver;
    readonly List<OgreScriptDiagnostic> errors = [];
    readonly Dictionary<string, string> env = [];

    // Ogre keeps imports in a std::map (sorted by source name, byte order) and requests in a multimap.
    readonly SortedDictionary<string, List<OgreScriptNode>> imports = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, List<string>> importRequests = new(StringComparer.Ordinal);
    readonly List<OgreScriptNode> importTable = [];
    readonly HashSet<string> loading = new(StringComparer.Ordinal);

    /// <param name="importResolver">Returns the text of a script by bare file name, or null if there is none.</param>
    OgreScriptCompiler(Func<string, string?> importResolver) => this.importResolver = importResolver;

    /// <summary>
    /// Compiles one file. Lexer and parser errors throw <see cref="OgreScriptException"/> (in Ogre they abort the file);
    /// everything else is reported in <see cref="OgreScriptFile.Diagnostics"/>.
    /// </summary>
    public static OgreScriptFile Compile(string text, string file, Func<string, string?>? importResolver = null)
    {
        var compiler = new OgreScriptCompiler(importResolver ?? (_ => null));
        var ast = compiler.ConvertToAst(OgreScriptParser.Parse(text, file));
        compiler.ProcessImports(ast);
        compiler.ProcessObjects(ast, ast);
        compiler.ProcessVariables(ast);
        return new OgreScriptFile { Name = file, Nodes = ast, Diagnostics = compiler.errors };
    }

    void AddError(OgreScriptError code, string file, int line, string message = "") => errors.Add(new(code, file, line, message));

    // ---------------------------------------------------------------- concrete tree to objects and properties

    List<OgreScriptNode> ConvertToAst(List<OgreConcreteNode> nodes)
    {
        CheckBraces(nodes);
        var result = new List<OgreScriptNode>();
        foreach (var node in nodes) Visit(node, null, result);
        return result;
    }

    void CheckBraces(List<OgreConcreteNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Type == OgreConcreteNodeType.RightBrace && node.Parent is null)
                AddError(OgreScriptError.UnmatchedBrace, node.File, node.Line);
            if (node.Type == OgreConcreteNodeType.LeftBrace)
            {
                var siblings = node.Parent?.Children;
                int at = siblings?.IndexOf(node) ?? -1;
                if (siblings is null || at + 1 >= siblings.Count || siblings[at + 1].Type != OgreConcreteNodeType.RightBrace)
                    AddError(OgreScriptError.UnclosedBrace, node.File, node.Line, node.Parent is null ? "" : $"block of '{node.Parent.Token}'");
            }
            CheckBraces(node.Children);
        }
    }

    void Visit(OgreConcreteNode node, OgreScriptNode? current, List<OgreScriptNode> top)
    {
        OgreScriptNode? created = null;

        if (node.Type == OgreConcreteNodeType.Import && current is null)
        {
            if (node.Children.Count > 2) { AddError(OgreScriptError.FewerParametersExpected, node.File, node.Line); return; }
            if (node.Children.Count < 2) { AddError(OgreScriptError.StringExpected, node.File, node.Line); return; }
            created = new OgreScriptImport(node.Children[0].Token, node.Children[1].Token) { File = node.File, Line = node.Line };
        }
        else if (node.Type == OgreConcreteNodeType.VariableAssign)
        {
            if (node.Children.Count > 2) { AddError(OgreScriptError.FewerParametersExpected, node.File, node.Line); return; }
            if (node.Children.Count < 2) { AddError(OgreScriptError.StringExpected, node.File, node.Line); return; }
            if (node.Children[0].Type != OgreConcreteNodeType.Variable)
            {
                AddError(OgreScriptError.VariableExpected, node.Children[0].File, node.Children[0].Line);
                return;
            }
            string name = node.Children[0].Token, value = node.Children[1].Token;
            if (current is OgreScriptObject obj) obj.Variables[name] = value;
            else env.TryAdd(name, value); // std::map::insert: the first top-level set wins
        }
        else if (node.Type == OgreConcreteNodeType.Variable)
        {
            if (node.Children.Count > 0) { AddError(OgreScriptError.FewerParametersExpected, node.File, node.Line); return; }
            created = new OgreScriptVariableAccess(node.Token) { File = node.File, Line = node.Line, Parent = current };
        }
        else if (node.Children.Count > 0)
        {
            var last = node.Children[^1];
            var beforeLast = node.Children.Count >= 2 ? node.Children[^2] : null;
            if (last.Type == OgreConcreteNodeType.RightBrace && beforeLast?.Type == OgreConcreteNodeType.LeftBrace)
            {
                var parts = new List<OgreConcreteNode>();
                bool isAbstract = node.Token == "abstract";
                if (!isAbstract) parts.Add(node);
                parts.AddRange(node.Children);

                int i = 0;
                var obj = new OgreScriptObject(parts[i++].Token) { File = node.File, Line = node.Line, Parent = current, IsAbstract = isAbstract };
                if (i < parts.Count && parts[i].Type is OgreConcreteNodeType.Word or OgreConcreteNodeType.Quote &&
                    !IsNameExcluded(obj.Class, current))
                    obj.Name = parts[i++].Token;
                for (; i < parts.Count && parts[i].Type is not (OgreConcreteNodeType.Colon or OgreConcreteNodeType.LeftBrace); i++)
                {
                    OgreScriptNode value = parts[i].Type == OgreConcreteNodeType.Variable
                        ? new OgreScriptVariableAccess(parts[i].Token)
                        : new OgreScriptAtom(parts[i].Token);
                    value.File = parts[i].File;
                    value.Line = parts[i].Line;
                    value.Parent = obj;
                    obj.Values.Add(value);
                }
                if (i < parts.Count && parts[i].Type == OgreConcreteNodeType.Colon)
                    obj.Bases.AddRange(parts[i].Children.Select(c => c.Token));

                foreach (var child in beforeLast.Children) Visit(child, obj, top);
                created = obj;
            }
            else
            {
                var prop = new OgreScriptProperty(node.Token) { File = node.File, Line = node.Line, Parent = current };
                foreach (var child in node.Children) Visit(child, prop, top);
                created = prop;
            }
        }
        else
        {
            created = new OgreScriptAtom(node.Token) { File = node.File, Line = node.Line, Parent = current };
        }

        if (created is null) return;
        switch (current)
        {
            case OgreScriptProperty p: p.Values.Add(created); break;
            case OgreScriptObject o: o.Children.Add(created); break;
            default: top.Add(created); break;
        }
    }

    /// <summary>Object types whose first word is not a name in some contexts (Ogre's isNameExcluded).</summary>
    static bool IsNameExcluded(string cls, OgreScriptNode? parent)
    {
        string? container = cls switch
        {
            "emitter" or "affector" => "particle_system",
            "pass" => "compositor",
            "texture_source" => "texture_unit",
            _ => null,
        };
        if (container is null) return false;
        for (var p = parent; p is OgreScriptObject o; p = o.Parent)
            if (o.Class == container) return true;
        return false;
    }

    // ---------------------------------------------------------------- imports

    void ProcessImports(List<OgreScriptNode> nodes)
    {
        for (int i = 0; i < nodes.Count;)
        {
            if (nodes[i] is not OgreScriptImport import) { i++; continue; }

            if (!imports.ContainsKey(import.Source) && loading.Add(import.Source))
            {
                // Ogre recurses without a guard; a cyclic import would overflow its stack, so no working script has one.
                var imported = LoadImport(import);
                if (imported is { Count: > 0 })
                {
                    ProcessImports(imported);
                    ProcessObjects(imported, imported);
                    imports[import.Source] = imported;
                }
                loading.Remove(import.Source);
            }

            if (!importRequests.TryGetValue(import.Source, out var requests))
                importRequests[import.Source] = requests = [];
            if (import.Target == "*")
            {
                requests.Clear();
                requests.Add("*");
            }
            else if (requests.Count == 0 || requests[0] != "*")
                requests.Add(import.Target);

            nodes.RemoveAt(i);
        }

        // Every imported file (sorted by name) goes to the front of the import table: whole, or just the requested objects.
        foreach (var (source, imported) in imports)
        {
            if (!importRequests.TryGetValue(source, out var requests) || requests.Count == 0) continue;
            if (requests[0] == "*")
            {
                importTable.InsertRange(0, imported);
                continue;
            }
            foreach (var target in requests)
            {
                var found = LocateTarget(imported, target);
                if (found is not null) importTable.Insert(0, found);
            }
        }
    }

    List<OgreScriptNode>? LoadImport(OgreScriptImport import)
    {
        var text = importResolver(import.Source);
        if (text is null)
        {
            AddError(OgreScriptError.ImportNotFound, import.File, import.Line, import.Source);
            return null;
        }
        return ConvertToAst(OgreScriptParser.Parse(text, import.Source));
    }

    /// <summary>The last top-level object with this name (Ogre keeps the last match).</summary>
    static OgreScriptObject? LocateTarget(List<OgreScriptNode> nodes, string name) =>
        nodes.OfType<OgreScriptObject>().LastOrDefault(o => o.Name == name);

    // ---------------------------------------------------------------- inheritance

    void ProcessObjects(List<OgreScriptNode> nodes, List<OgreScriptNode> top)
    {
        for (int n = 0; n < nodes.Count; n++)
        {
            if (nodes[n] is not OgreScriptObject obj) continue;

            foreach (var name in obj.Bases)
            {
                // The current file first, then whatever was imported. Nothing else: other files are not searched.
                var source = LocateTarget(top, name) ?? LocateTarget(importTable, name);
                if (source is not null) Overlay(source, obj);
                else AddError(OgreScriptError.ObjectBaseNotFound, obj.File, obj.Line, $"base object named \"{name}\" not found in script definition");
            }

            ProcessObjects(obj.Children, top);

            // Inherited properties go in front of the object's own, so its own ones are applied later and win.
            obj.Children.InsertRange(0, obj.Overrides);
            obj.Overrides.Clear();
        }
    }

    /// <summary>
    /// Copies <paramref name="src"/> (the base) into <paramref name="dest"/> as Ogre's overlayObject does: base properties
    /// become pending overrides; child objects are paired by class and name, then unnamed base objects by position;
    /// paired ones are overlaid recursively and unpaired base objects are inserted as copies.
    /// </summary>
    void Overlay(OgreScriptObject src, OgreScriptObject dest)
    {
        foreach (var (k, v) in src.Variables)
            if (dest.GetVariable(k) is null)
                dest.Variables[k] = v;

        var overrides = new List<(OgreScriptObject Source, OgreScriptObject? Target)>();
        foreach (var child in src.Children)
        {
            if (child is OgreScriptObject o) overrides.Add((o, null));
            else
            {
                var copy = child.Clone();
                copy.Parent = dest;
                dest.Overrides.Add(copy);
            }
        }

        var minIndex = new Dictionary<OgreScriptObject, int>(ReferenceEqualityComparer.Instance);
        var matched = new HashSet<OgreScriptObject>(ReferenceEqualityComparer.Instance);

        // Pair by name.
        int maxOverrideIndex = 0;
        for (int i = 0; i < dest.Children.Count;)
        {
            if (dest.Children[i] is not OgreScriptObject node) { i++; continue; }
            minIndex[node] = maxOverrideIndex;
            bool hasWildcard = node.Name.Contains('*');

            for (int j = 0; j < overrides.Count; j++)
            {
                var temp = overrides[j].Source;
                bool wildcardMatch = hasWildcard &&
                    (WildcardMatch(temp.Name, node.Name) || (node.Name.Length == 1 && temp.Name.Length == 0));
                if (temp.Class != node.Class || node.Name.Length == 0 || (temp.Name != node.Name && !wildcardMatch))
                    continue;

                if (overrides[j].Target is null)
                {
                    var current = node;
                    if (wildcardMatch)
                    {
                        // A wildcard object is copied once per base object it matches, taking that object's name.
                        current = (OgreScriptObject)node.Clone();
                        current.Parent = dest;
                        current.Name = temp.Name;
                        dest.Children.Insert(i, current);
                        i++;
                    }
                    overrides[j] = (temp, current);
                    maxOverrideIndex = Math.Max(j, maxOverrideIndex);
                    minIndex[current] = maxOverrideIndex;
                    matched.Add(current);
                }
                else
                {
                    AddError(OgreScriptError.DuplicateOverride, node.File, node.Line);
                }

                if (!wildcardMatch) break;
            }

            if (hasWildcard) dest.Children.RemoveAt(i);
            else i++;
        }

        // Pair the rest by position, with unnamed base objects of the same class only.
        foreach (var child in dest.Children)
        {
            if (child is not OgreScriptObject node || matched.Contains(node)) continue;
            for (int j = minIndex.GetValueOrDefault(node); j < overrides.Count; j++)
            {
                var temp = overrides[j].Source;
                if (temp.Name.Length == 0 && temp.Class == node.Class && overrides[j].Target is null)
                {
                    overrides[j] = (temp, node);
                    break;
                }
            }
        }

        // Overlay the pairs; insert copies of unpaired base objects after the last paired one.
        int insertAt = 0;
        foreach (var (source, target) in overrides)
        {
            if (target is not null)
            {
                Overlay(source, target);
                insertAt = dest.Children.IndexOf(target) + 1;
            }
            else
            {
                var copy = source.Clone();
                copy.Parent = dest;
                dest.Children.Insert(insertAt++, copy);
            }
        }
    }

    /// <summary>Ogre's StringUtil::match, case-sensitive: <c>*</c> matches any run of characters.</summary>
    public static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (p < pattern.Length && pattern[p] == text[t]) { p++; t++; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    // ---------------------------------------------------------------- variables

    void ProcessVariables(List<OgreScriptNode> nodes)
    {
        for (int i = 0; i < nodes.Count;)
        {
            switch (nodes[i])
            {
                case OgreScriptObject obj:
                    if (!obj.IsAbstract)
                    {
                        ProcessVariables(obj.Children);
                        ProcessVariables(obj.Values);
                    }
                    i++;
                    break;
                case OgreScriptProperty prop:
                    ProcessVariables(prop.Values);
                    i++;
                    break;
                case OgreScriptVariableAccess access:
                {
                    var scope = Ancestors(access).OfType<OgreScriptObject>().FirstOrDefault();
                    string? value = scope?.GetVariable(access.Name);
                    if (value is null && env.TryGetValue(access.Name, out var global)) value = global;

                    nodes.RemoveAt(i);
                    if (value is not null)
                    {
                        var expanded = new List<OgreScriptNode>();
                        foreach (var c in OgreScriptParser.ParseChunk(OgreScriptLexer.Tokenize(value, access.File)))
                            Visit(c, null, expanded);
                        foreach (var e in expanded) e.Parent = access.Parent;
                        ProcessVariables(expanded);
                        nodes.InsertRange(i, expanded);
                        i += expanded.Count;
                    }
                    else
                    {
                        AddError(OgreScriptError.UndefinedVariable, access.File, access.Line, access.Name);
                    }
                    break;
                }
                default:
                    i++;
                    break;
            }
        }
    }

    static IEnumerable<OgreScriptNode> Ancestors(OgreScriptNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent) yield return p;
    }
}
