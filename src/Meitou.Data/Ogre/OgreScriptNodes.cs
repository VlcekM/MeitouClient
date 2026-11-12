namespace Meitou.Data.Ogre;

/// <summary>
/// A node of a compiled Ogre script (Ogre's "abstract" syntax tree): objects (<c>material X { }</c>), properties (<c>lighting off</c>), atoms
/// (property values), imports and variable references.
/// </summary>
public abstract class OgreScriptNode
{
    /// <summary>
    /// The script file the node was read from (an imported file for imported nodes).
    /// </summary>
    public string File { get; set; } = "";
    /// <summary>
    /// The line of the node in <see cref="File"/>.
    /// </summary>
    public int Line { get; set; }
    /// <summary>
    /// The object or property this node belongs to; null at the top level.
    /// </summary>
    public OgreScriptNode? Parent { get; set; }

    /// <summary>
    /// Deep copy, as Ogre's clone(): keeps file and line, drops the parent.
    /// </summary>
    public abstract OgreScriptNode Clone();

    /// <summary>
    /// Gives <paramref name="node"/> this node's file, line and parent, and returns it (for <see cref="Clone"/>).
    /// </summary>
    protected T CopyPosition<T>(T node) where T : OgreScriptNode
    {
        node.File = File;
        node.Line = Line;
        node.Parent = Parent;
        return node;
    }
}

/// <summary>
/// A property or object value: one word or quoted phrase.
/// </summary>
public sealed class OgreScriptAtom(string value) : OgreScriptNode
{
    /// <summary>
    /// The value's text, without quotes.
    /// </summary>
    public string Value { get; set; } = value;
    public override OgreScriptNode Clone() => CopyPosition(new OgreScriptAtom(Value));
    public override string ToString() => Value;
}

/// <summary>
/// A <c>$variable</c> reference, replaced by the variable's value after compilation.
/// </summary>
public sealed class OgreScriptVariableAccess(string name) : OgreScriptNode
{
    /// <summary>
    /// Includes the <c>$</c>.
    /// </summary>
    public string Name { get; } = name;

    public override OgreScriptNode Clone() => CopyPosition(new OgreScriptVariableAccess(Name));
    public override string ToString() => Name;
}

/// <summary>
/// An <c>import target from source</c> line.
/// </summary>
public sealed class OgreScriptImport(string target, string source) : OgreScriptNode
{
    /// <summary>
    /// Object name to import, or <c>*</c> for everything.
    /// </summary>
    public string Target { get; } = target;

    /// <summary>
    /// Script file name, looked up like any resource (bare name).
    /// </summary>
    public string Source { get; } = source;

    public override OgreScriptNode Clone() => CopyPosition(new OgreScriptImport(Target, Source));
    public override string ToString() => $"import {Target} from {Source}";
}

/// <summary>
/// A property: its name and its values (<c>lighting off</c>).
/// </summary>
public sealed class OgreScriptProperty(string name) : OgreScriptNode
{
    /// <summary>
    /// The property's name, e.g. <c>lighting</c>.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Atoms (and, before variable expansion, variable references).
    /// </summary>
    public List<OgreScriptNode> Values { get; } = [];

    /// <summary>
    /// The values as text.
    /// </summary>
    public IReadOnlyList<string> Args => [.. Values.Select(v => v is OgreScriptAtom a ? a.Value : v.ToString() ?? "")];

    public override OgreScriptNode Clone()
    {
        var p = CopyPosition(new OgreScriptProperty(Name));
        foreach (var v in Values)
        {
            var c = v.Clone();
            c.Parent = p;
            p.Values.Add(c);
        }
        return p;
    }

    public override string ToString() => Values.Count == 0 ? Name : $"{Name} {string.Join(' ', Args)}";
}

/// <summary>
/// An object: <c>class name [values] [: bases] { children }</c>, e.g. a material, technique or pass.
/// </summary>
public sealed class OgreScriptObject(string cls) : OgreScriptNode
{
    /// <summary>
    /// The object type: <c>material</c>, <c>technique</c>, <c>pass</c>, <c>vertex_program</c>...
    /// </summary>
    public string Class { get; } = cls;

    /// <summary>
    /// Empty when unnamed (e.g. most techniques and passes).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Words between the name and the <c>:</c> or <c>{</c>, e.g. <c>hlsl</c> in <c>vertex_program X hlsl</c>.
    /// </summary>
    public List<OgreScriptNode> Values { get; } = [];

    /// <summary>
    /// Base objects named after <c>:</c>, in order (quoted names keep their quotes, as in Ogre).
    /// </summary>
    public List<string> Bases { get; } = [];

    /// <summary>
    /// Declared with <c>abstract</c>: usable as a base but never translated itself.
    /// </summary>
    public bool IsAbstract { get; set; }

    /// <summary>
    /// The object's properties and child objects in order, inherited ones first after compilation.
    /// </summary>
    public List<OgreScriptNode> Children { get; } = [];

    /// <summary>
    /// Variables set with <c>set $name value</c> inside this object (names include the <c>$</c>).
    /// </summary>
    public Dictionary<string, string> Variables { get; } = [];

    /// <summary>
    /// Base properties waiting to be put in front of <see cref="Children"/> (Ogre's "overrides").
    /// </summary>
    internal List<OgreScriptNode> Overrides { get; } = [];

    /// <summary>
    /// The object's properties in order.
    /// </summary>
    public IEnumerable<OgreScriptProperty> Properties => Children.OfType<OgreScriptProperty>();

    /// <summary>
    /// The child objects of class <paramref name="cls"/>, in order.
    /// </summary>
    public IEnumerable<OgreScriptObject> Objects(string cls) => Children.OfType<OgreScriptObject>().Where(o => o.Class == cls);

    /// <summary>
    /// The <see cref="Values"/> as text.
    /// </summary>
    public IReadOnlyList<string> Args => [.. Values.Select(v => v is OgreScriptAtom a ? a.Value : v.ToString() ?? "")];

    /// <summary>
    /// The last property with this name (later ones override earlier ones when Ogre translates).
    /// </summary>
    public OgreScriptProperty? Property(string name) => Children.OfType<OgreScriptProperty>().LastOrDefault(p => p.Name == name);

    /// <summary>
    /// Looks a variable up here, then in enclosing objects.
    /// </summary>
    public string? GetVariable(string name)
    {
        for (OgreScriptNode? n = this; n is not null; n = n.Parent)
            if (n is OgreScriptObject o && o.Variables.TryGetValue(name, out var v))
                return v;
        return null;
    }

    public override OgreScriptNode Clone()
    {
        var o = CopyPosition(new OgreScriptObject(Class) { Name = Name, IsAbstract = IsAbstract });
        foreach (var c in Children)
        {
            var n = c.Clone();
            n.Parent = o;
            o.Children.Add(n);
        }
        foreach (var v in Values)
        {
            var n = v.Clone();
            n.Parent = o;
            o.Values.Add(n);
        }
        foreach (var (k, v) in Variables) o.Variables[k] = v;
        // Like Ogre's clone(), bases and pending overrides are not copied.
        return o;
    }

    public override string ToString() => Name.Length == 0 ? Class : $"{Class} {Name}";
}