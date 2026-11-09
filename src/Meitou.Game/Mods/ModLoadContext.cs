using System.Reflection;
using System.Runtime.Loader;

namespace Meitou.Modding;

/// <summary>
/// One mod's assemblies (docs/modding.md "Loading"): non-collectible, loaded once. An assembly the game ships (<c>Meitou.*</c>, the framework, Bepu,
/// <c>0Harmony</c>: everything on the trusted platform list) always comes from the game, even when the mod folder has a copy, so a mod never holds a
/// private copy of engine types or a second Harmony; next, an assembly a dependency exports (docs/modding-dependencies.md "Assembly resolution")
/// comes from that dependency's context, so a framework's types and statics exist once; anything else comes from the mod folder, so two mods may ship
/// different versions of a library.
/// </summary>
sealed class ModLoadContext(string folder, string name, IReadOnlyDictionary<string, Assembly>? imports = null)
    : AssemblyLoadContext($"mod {name}", isCollectible: false)
{
    static readonly Lazy<HashSet<string>> Shipped = new(ReadShipped);

    public string Folder { get; } = folder;

    /// <summary>
    /// The assemblies the mod's dependencies export, by simple name (case ignored); resolved before the mod folder.
    /// </summary>
    public IReadOnlyDictionary<string, Assembly> Imports { get; } = imports ?? new Dictionary<string, Assembly>();

    /// <summary>
    /// Whether the game ships an assembly of this simple name, so the default context resolves it.
    /// </summary>
    public static bool IsShipped(string simpleName) => Shipped.Value.Contains(simpleName);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } simple || IsShipped(simple)) return null;
        if (Imports.TryGetValue(simple, out var imported)) return imported;
        var path = Path.Combine(Folder, simple + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in NativeNames(unmanagedDllName))
        {
            var path = Path.Combine(Folder, candidate);
            if (File.Exists(path)) return LoadUnmanagedDllFromPath(path);
        }
        return 0;
    }

    static IEnumerable<string> NativeNames(string name)
    {
        yield return name;
        if (OperatingSystem.IsWindows()) yield return name + ".dll";
        else if (OperatingSystem.IsMacOS())
        {
            yield return name + ".dylib";
            yield return "lib" + name + ".dylib";
        }
        else
        {
            yield return name + ".so";
            yield return "lib" + name + ".so";
        }
    }

    /// <summary>
    /// The simple names on the trusted platform assembly list: the game's own assemblies and the runtime's, self-contained or not.
    /// </summary>
    static HashSet<string> ReadShipped()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
            foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                names.Add(Path.GetFileNameWithoutExtension(path));
        foreach (var loaded in Default.Assemblies)
            if (loaded.GetName().Name is { } simple) names.Add(simple);
        return names;
    }
}