namespace Meitou.Data.Ogre;

/// <summary>
/// Ogre's ScriptCompiler error codes (CE_*), plus a few Meitou-only checks marked below.
/// </summary>
public enum OgreScriptError
{
    /// <summary>
    /// CE_STRINGEXPECTED: an <c>import</c> or <c>set</c> has too few values.
    /// </summary>
    StringExpected,
    /// <summary>
    /// CE_NUMBEREXPECTED: a number was expected; not raised by Meitou's compiler.
    /// </summary>
    NumberExpected,
    /// <summary>
    /// CE_FEWERPARAMETERSEXPECTED: an <c>import</c>, <c>set</c> or variable access has too many values.
    /// </summary>
    FewerParametersExpected,
    /// <summary>
    /// CE_VARIABLEEXPECTED: a <c>set</c> whose first value is not a <c>$variable</c>.
    /// </summary>
    VariableExpected,
    /// <summary>
    /// CE_UNDEFINEDVARIABLE: a <c>$variable</c> no enclosing object or top-level <c>set</c> defines.
    /// </summary>
    UndefinedVariable,
    /// <summary>
    /// CE_OBJECTNAMEEXPECTED: an object needs a name (a material without one).
    /// </summary>
    ObjectNameExpected,
    /// <summary>
    /// CE_OBJECTALLOCATIONERROR: the translator could not make the object; not raised by Meitou.
    /// </summary>
    ObjectAllocationError,
    /// <summary>
    /// CE_INVALIDPARAMETERS: a property's values are invalid; not raised by Meitou.
    /// </summary>
    InvalidParameters,
    /// <summary>
    /// CE_DUPLICATEOVERRIDE: an inherited child object was overridden twice.
    /// </summary>
    DuplicateOverride,
    /// <summary>
    /// CE_UNEXPECTEDTOKEN: a token that does not fit; not raised by Meitou.
    /// </summary>
    UnexpectedToken,
    /// <summary>
    /// CE_OBJECTBASENOTFOUND: a <c>:</c> base object that neither the file nor its imports define.
    /// </summary>
    ObjectBaseNotFound,
    /// <summary>
    /// CE_UNSUPPORTEDBYRENDERSYSTEM: the render system lacks a feature; not raised by Meitou.
    /// </summary>
    UnsupportedByRenderSystem,
    /// <summary>
    /// CE_REFERENCETOANONEXISTINGOBJECT: a reference to an undefined object; not raised by Meitou.
    /// </summary>
    ReferenceToANonExistingObject,

    /// <summary>
    /// Meitou only: an <c>import</c> names a file that cannot be found (Ogre ignores this silently).
    /// </summary>
    ImportNotFound,

    /// <summary>
    /// Meitou only: a <c>{</c> without its <c>}</c> (Ogre then reads the object as a property and ignores it).
    /// </summary>
    UnclosedBrace,

    /// <summary>
    /// Meitou only: a <c>}</c> with no open block.
    /// </summary>
    UnmatchedBrace,

    /// <summary>
    /// Meitou only: a material, program etc. whose name is already defined (see docs: winner Unknown).
    /// </summary>
    DuplicateDefinition,

    /// <summary>
    /// Meitou only: the file stopped compiling with an exception (lexer/parser error).
    /// </summary>
    Fatal,
}

/// <summary>
/// A compile diagnostic: its code, the file and line it was found at, and an optional detail.
/// </summary>
public sealed record OgreScriptDiagnostic(OgreScriptError Code, string File, int Line, string Message = "")
{
    public override string ToString() => $"{File}({Line}): {Code}{(Message.Length > 0 ? ": " + Message : "")}";
}

/// <summary>
/// One compiled script file: its top-level nodes after imports, inheritance and variables are processed.
/// </summary>
public sealed class OgreScriptFile
{
    /// <summary>
    /// The compiled file's name.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// The file's top-level nodes after imports, inheritance and variables.
    /// </summary>
    public List<OgreScriptNode> Nodes { get; init; } = [];
    /// <summary>
    /// Everything the compiler reported for the file, in the order found.
    /// </summary>
    public List<OgreScriptDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Top-level objects Ogre would translate (abstract ones are skipped).
    /// </summary>
    public IEnumerable<OgreScriptObject> Objects => Nodes.OfType<OgreScriptObject>().Where(o => !o.IsAbstract);
}

/// <summary>
/// Compiles Ogre scripts the way Ogre's ScriptCompiler (MIT) does before translation: concrete tree to objects and properties, then <c>import</c>,
/// then <c>:</c> inheritance (overlaying base objects), then <c>$variable</c> expansion. One instance per file, like Ogre's compile(), whose import
/// and variable state is per call.
/// </summary>
public sealed class OgreScriptCompiler
{
    #region Compiling

    /// <summary>
    /// Returns the text of a script by bare file name; null when there is none.
    /// </summary>
    readonly Func<string, string?> _importResolver;
    /// <summary>
    /// The diagnostics so far, in the order found.
    /// </summary>
    readonly List<OgreScriptDiagnostic> _errors = [];
    /// <summary>
    /// The top-level <c>set</c> variables of the file; the first set of a name wins.
    /// </summary>
    readonly Dictionary<string, string> _env = [];

    /// <param name="importResolver">Returns the text of a script by bare file name, or null if there is none.</param>
    OgreScriptCompiler(Func<string, string?> importResolver) => this._importResolver = importResolver;

    /// <summary>
    /// Compiles one file. Lexer and parser errors throw <see cref="OgreScriptException"/> (in Ogre they abort the file); everything else is reported
    /// in <see cref="OgreScriptFile.Diagnostics"/>.
    /// </summary>
    public static OgreScriptFile Compile(string text, string file, Func<string, string?>? importResolver = null)
    {
        var compiler = new OgreScriptCompiler(importResolver ?? (_ => null));
        var ast = compiler.ConvertToAst(OgreScriptParser.Parse(text, file));
        compiler.ProcessImports(ast);
        compiler.ProcessObjects(ast, ast);
        compiler.ProcessVariables(ast);
        return new OgreScriptFile { Name = file, Nodes = ast, Diagnostics = compiler._errors };
    }

    /// <summary>
    /// Reports a diagnostic.
    /// </summary>
    void AddError(OgreScriptError code, string file, int line, string message = "") => _errors.Add(new(code, file, line, message));

    #endregion

    #region Concrete tree to objects and properties

    /// <summary>
    /// Turns a parsed file's concrete tree into objects, properties, imports and variables, after reporting brace errors.
    /// </summary>
    List<OgreScriptNode> ConvertToAst(List<OgreConcreteNode> nodes)
    {
        CheckBraces(nodes);
        var result = new List<OgreScriptNode>();
        foreach (var node in nodes) Visit(node, null, result);
        return result;
    }

    /// <summary>
    /// Reports a <c>}</c> outside every block and a <c>{</c> whose block is not closed, throughout the tree.
    /// </summary>
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

    /// <summary>
    /// Turns <paramref name="node"/> into AST and adds it to <paramref name="current"/> (a property's values, an object's children) or to
    /// <paramref name="top"/>.
    /// </summary>
    void Visit(OgreConcreteNode node, OgreScriptNode? current, List<OgreScriptNode> top)
    {
        var created = Create(node, current, top);
        if (created is null) return;

        switch (current)
        {
            case OgreScriptProperty p:
                p.Values.Add(created);
                break;
            case OgreScriptObject o:
                o.Children.Add(created);
                break;
            default:
                top.Add(created);
                break;
        }
    }

    /// <summary>
    /// The AST node of a concrete node, its children visited; null when it makes none (an error, or a variable assignment).
    /// </summary>
    OgreScriptNode? Create(OgreConcreteNode node, OgreScriptNode? current, List<OgreScriptNode> top)
    {
        if (node.Type == OgreConcreteNodeType.Import && current is null) return CreateImport(node);
        if (node.Type == OgreConcreteNodeType.VariableAssign)
        {
            AssignVariable(node, current);
            return null;
        }
        if (node.Type == OgreConcreteNodeType.Variable) return CreateVariableAccess(node, current);
        if (node.Children.Count > 0) return CreateObjectOrProperty(node, current, top);

        return new OgreScriptAtom(node.Token) { File = node.File, Line = node.Line, Parent = current };
    }

    /// <summary>
    /// An <c>import</c> at the top level: its target and source, or null with an error when it has not exactly two.
    /// </summary>
    OgreScriptNode? CreateImport(OgreConcreteNode node)
    {
        if (node.Children.Count > 2)
        {
            AddError(OgreScriptError.FewerParametersExpected, node.File, node.Line);
            return null;
        }
        if (node.Children.Count < 2)
        {
            AddError(OgreScriptError.StringExpected, node.File, node.Line);
            return null;
        }
        return new OgreScriptImport(node.Children[0].Token, node.Children[1].Token) { File = node.File, Line = node.Line };
    }

    /// <summary>
    /// A <c>set $name value</c>: stored on the enclosing object, or in the file's environment at the top level.
    /// </summary>
    void AssignVariable(OgreConcreteNode node, OgreScriptNode? current)
    {
        if (node.Children.Count > 2)
        {
            AddError(OgreScriptError.FewerParametersExpected, node.File, node.Line);
            return;
        }
        if (node.Children.Count < 2)
        {
            AddError(OgreScriptError.StringExpected, node.File, node.Line);
            return;
        }
        if (node.Children[0].Type != OgreConcreteNodeType.Variable)
        {
            AddError(OgreScriptError.VariableExpected, node.Children[0].File, node.Children[0].Line);
            return;
        }
        string name = node.Children[0].Token, value = node.Children[1].Token;
        if (current is OgreScriptObject obj) obj.Variables[name] = value;
        else _env.TryAdd(name, value); // std::map::insert: the first top-level set wins
    }

    /// <summary>
    /// A <c>$variable</c> access, or null with an error when it has children.
    /// </summary>
    OgreScriptNode? CreateVariableAccess(OgreConcreteNode node, OgreScriptNode? current)
    {
        if (node.Children.Count > 0)
        {
            AddError(OgreScriptError.FewerParametersExpected, node.File, node.Line);
            return null;
        }
        return new OgreScriptVariableAccess(node.Token) { File = node.File, Line = node.Line, Parent = current };
    }

    /// <summary>
    /// A node with children: an object when they end in a <c>{ }</c> block, else a property whose children are its values.
    /// </summary>
    OgreScriptNode CreateObjectOrProperty(OgreConcreteNode node, OgreScriptNode? current, List<OgreScriptNode> top)
    {
        var last = node.Children[^1];
        var beforeLast = node.Children.Count >= 2 ? node.Children[^2] : null;
        if (last.Type == OgreConcreteNodeType.RightBrace && beforeLast?.Type == OgreConcreteNodeType.LeftBrace)
            return CreateObject(node, beforeLast, current, top);

        var prop = new OgreScriptProperty(node.Token) { File = node.File, Line = node.Line, Parent = current };
        foreach (var child in node.Children) Visit(child, prop, top);
        return prop;
    }

    /// <summary>
    /// An object: its class (the node's word, or the first child after <c>abstract</c>), name, values and bases, then its block's children.
    /// </summary>
    OgreScriptObject CreateObject(OgreConcreteNode node, OgreConcreteNode block, OgreScriptNode? current, List<OgreScriptNode> top)
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

        foreach (var child in block.Children) Visit(child, obj, top);
        return obj;
    }

    /// <summary>
    /// Object types whose first word is not a name in some contexts (Ogre's isNameExcluded).
    /// </summary>
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

    #endregion

    #region Imports

    /// <summary>
    /// Each imported file's processed nodes, by source name. Ogre keeps imports in a std::map (sorted by source name, byte order) and
    /// requests in a multimap.
    /// </summary>
    readonly SortedDictionary<string, List<OgreScriptNode>> _imports = new(StringComparer.Ordinal);
    /// <summary>
    /// What each import asked for, by source name: object names, or just <c>*</c> for the whole file.
    /// </summary>
    readonly SortedDictionary<string, List<string>> _importRequests = new(StringComparer.Ordinal);
    /// <summary>
    /// The objects imports make visible to <c>:</c> inheritance, searched after the file itself.
    /// </summary>
    readonly List<OgreScriptNode> _importTable = [];
    /// <summary>
    /// The imports being loaded now, so a cyclic import is read once.
    /// </summary>
    readonly HashSet<string> _loading = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads the imports of <paramref name="nodes"/> (each source once, processed for its own imports and inheritance), removes them, then puts each
    /// imported file's requested objects at the front of the import table, files in source name order.
    /// </summary>
    void ProcessImports(List<OgreScriptNode> nodes)
    {
        for (int i = 0; i < nodes.Count;)
        {
            if (nodes[i] is not OgreScriptImport import)
            {
                i++;
                continue;
            }

            if (!_imports.ContainsKey(import.Source) && _loading.Add(import.Source))
            {
                // Ogre recurses without a guard; a cyclic import would overflow its stack, so no working script has one.
                var imported = LoadImport(import);
                if (imported is { Count: > 0 })
                {
                    ProcessImports(imported);
                    ProcessObjects(imported, imported);
                    _imports[import.Source] = imported;
                }
                _loading.Remove(import.Source);
            }

            if (!_importRequests.TryGetValue(import.Source, out var requests))
                _importRequests[import.Source] = requests = [];
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
        foreach (var (source, imported) in _imports)
        {
            if (!_importRequests.TryGetValue(source, out var requests) || requests.Count == 0) continue;
            if (requests[0] == "*")
            {
                _importTable.InsertRange(0, imported);
                continue;
            }
            foreach (var target in requests)
            {
                var found = LocateTarget(imported, target);
                if (found is not null) _importTable.Insert(0, found);
            }
        }
    }

    /// <summary>
    /// The nodes of the imported script; null, with an error, when the resolver does not find it.
    /// </summary>
    List<OgreScriptNode>? LoadImport(OgreScriptImport import)
    {
        var text = _importResolver(import.Source);
        if (text is null)
        {
            AddError(OgreScriptError.ImportNotFound, import.File, import.Line, import.Source);
            return null;
        }
        return ConvertToAst(OgreScriptParser.Parse(text, import.Source));
    }

    /// <summary>
    /// The last top-level object with this name (Ogre keeps the last match).
    /// </summary>
    static OgreScriptObject? LocateTarget(List<OgreScriptNode> nodes, string name) =>
        nodes.OfType<OgreScriptObject>().LastOrDefault(o => o.Name == name);

    #endregion

    #region Inheritance

    /// <summary>
    /// Applies the <c>:</c> bases of every object in <paramref name="nodes"/>, depth first; a base is searched in <paramref name="top"/>, then the
    /// import table.
    /// </summary>
    void ProcessObjects(List<OgreScriptNode> nodes, List<OgreScriptNode> top)
    {
        for (int n = 0; n < nodes.Count; n++)
        {
            if (nodes[n] is not OgreScriptObject obj) continue;

            foreach (var name in obj.Bases)
            {
                // The current file first, then whatever was imported. Nothing else: other files are not searched.
                var source = LocateTarget(top, name) ?? LocateTarget(_importTable, name);
                if (source is not null) Overlay(source, obj);
                else
                    AddError(OgreScriptError.ObjectBaseNotFound, obj.File, obj.Line, $"base object named \"{name}\" not found in script definition");
            }

            ProcessObjects(obj.Children, top);

            // Inherited properties go in front of the object's own, so its own ones are applied later and win.
            obj.Children.InsertRange(0, obj.Overrides);
            obj.Overrides.Clear();
        }
    }

    /// <summary>
    /// Copies <paramref name="src"/> (the base) into <paramref name="dest"/> as Ogre's overlayObject does: base properties become pending overrides;
    /// child objects are paired by class and name, then unnamed base objects by position; paired ones are overlaid recursively and unpaired base
    /// objects are inserted as copies.
    /// </summary>
    void Overlay(OgreScriptObject src, OgreScriptObject dest)
    {
        CopyMissingVariables(src, dest);
        var overrides = CollectOverrides(src, dest);
        var minIndex = new Dictionary<OgreScriptObject, int>(ReferenceEqualityComparer.Instance);
        var matched = new HashSet<OgreScriptObject>(ReferenceEqualityComparer.Instance);

        PairByName(dest, overrides, minIndex, matched);
        PairByPosition(dest, overrides, minIndex, matched);
        OverlayPairs(dest, overrides);
    }

    /// <summary>
    /// The base's properties the destination does not set become its pending overrides.
    /// </summary>
    static void CopyMissingVariables(OgreScriptObject src, OgreScriptObject dest)
    {
        foreach (var (k, v) in src.Variables)
            if (dest.GetVariable(k) is null)
                dest.Variables[k] = v;
    }

    /// <summary>
    /// The base's child objects that overlay the destination: named ones are listed to be paired; the others are added to the destination's
    /// overrides as copies.
    /// </summary>
    static List<(OgreScriptObject Source, OgreScriptObject? Target)> CollectOverrides(OgreScriptObject src, OgreScriptObject dest)
    {
        var overrides = new List<(OgreScriptObject Source, OgreScriptObject? Target)>();
        foreach (var child in src.Children)
        {
            if (child is OgreScriptObject o)
            {
                overrides.Add((o, null));
                continue;
            }

            var copy = child.Clone();
            copy.Parent = dest;
            dest.Overrides.Add(copy);
        }

        return overrides;
    }

    /// <summary>
    /// Pairs the destination's named child objects with the overrides by class and name (a wildcard name matches several).
    /// </summary>
    void PairByName(
        OgreScriptObject dest, List<(OgreScriptObject Source, OgreScriptObject? Target)> overrides,
        Dictionary<OgreScriptObject, int> minIndex, HashSet<OgreScriptObject> matched)
    {
        int maxOverrideIndex = 0;
        for (int i = 0; i < dest.Children.Count;)
        {
            if (dest.Children[i] is not OgreScriptObject node)
            {
                i++;
                continue;
            }

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
    }

    /// <summary>
    /// Pairs the rest by position, with unnamed base objects of the same class only.
    /// </summary>
    static void PairByPosition(
        OgreScriptObject dest, List<(OgreScriptObject Source, OgreScriptObject? Target)> overrides,
        Dictionary<OgreScriptObject, int> minIndex, HashSet<OgreScriptObject> matched)
    {
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
    }

    /// <summary>
    /// Overlays the pairs; copies of unpaired base objects are inserted after the last paired one.
    /// </summary>
    void OverlayPairs(OgreScriptObject dest, List<(OgreScriptObject Source, OgreScriptObject? Target)> overrides)
    {
        int insertAt = 0;
        foreach (var (source, target) in overrides)
        {
            if (target is not null)
            {
                Overlay(source, target);
                insertAt = dest.Children.IndexOf(target) + 1;
                continue;
            }

            var copy = source.Clone();
            copy.Parent = dest;
            dest.Children.Insert(insertAt++, copy);
        }
    }

    /// <summary>
    /// Ogre's StringUtil::match, case-sensitive: <c>*</c> matches any run of characters.
    /// </summary>
    public static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    #endregion

    #region Variables

    /// <summary>
    /// Replaces every <c>$variable</c> in <paramref name="nodes"/> by its value (the nearest enclosing object's, else the file's), parsed as script;
    /// abstract objects are left alone.
    /// </summary>
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
                        if (value is null && _env.TryGetValue(access.Name, out var global)) value = global;

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

    /// <summary>
    /// The nodes above <paramref name="node"/>, nearest first.
    /// </summary>
    static IEnumerable<OgreScriptNode> Ancestors(OgreScriptNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent) yield return p;
    }

    #endregion
}