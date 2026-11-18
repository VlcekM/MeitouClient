using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using BepInEx.AssemblyPublicizer;

namespace Meitou.ReferencePack;

/// <summary>
/// The publicized reference assemblies of the reference pack (docs/modding.md "Reference pack"): copies of the game's assemblies in which every
/// type and member is public, so a code mod compiles against internals it reaches at run time through IgnoresAccessChecksTo. The copies are
/// compile-time only: no method body, no resource, and the [ReferenceAssembly] mark that makes the runtime refuse to load them.
/// </summary>
internal static class PublicizedSet
{
    /// <summary>
    /// The publicizer's settings: all types, methods and fields; compiler-generated members (backing fields, closures) left as they are, as
    /// they would clash with the members they serve; bodies stripped; no attribute recording the original access, which nothing reads.
    /// </summary>
    static readonly AssemblyPublicizerOptions Options = new()
    {
        Target = PublicizeTarget.All,
        PublicizeCompilerGenerated = false,
        IncludeOriginalAttributesAttribute = false,
        Strip = true,
    };

    /// <summary>
    /// Writes the publicized copy of each assembly into <paramref name="output"/> under its file name; 0, or 1 when an assembly is missing.
    /// </summary>
    public static int Write(string output, IReadOnlyList<string> assemblies)
    {
        var missing = assemblies.Where(a => !File.Exists(a)).ToList();
        foreach (var path in missing) Console.Error.WriteLine($"not found: {path}");
        if (missing.Count > 0) return 1;

        Directory.CreateDirectory(output);
        foreach (var path in assemblies) WriteOne(path, Path.Combine(output, Path.GetFileName(path)));
        return 0;
    }

    /// <summary>
    /// Publicizes and strips one assembly, drops its resources, marks it a reference assembly and writes it to <paramref name="target"/>.
    /// </summary>
    static void WriteOne(string source, string target)
    {
        var assembly = AssemblyDefinition.FromFile(source);
        var module = assembly.ManifestModule ?? throw new InvalidDataException($"{source} has no manifest module");

        AssemblyPublicizer.Publicize(assembly, Options);

        // Embedded resources (UI textures, shader sources) serve only the running game.
        module.Resources.Clear();
        MarkReference(assembly, module);

        module.Write(target);
        Console.WriteLine($"publicized {Path.GetFileName(source)} -> {target}");
    }

    /// <summary>
    /// Adds <c>System.Runtime.CompilerServices.ReferenceAssemblyAttribute</c>, referenced through the module's own core library, unless present.
    /// </summary>
    static void MarkReference(AssemblyDefinition assembly, ModuleDefinition module)
    {
        const string ns = "System.Runtime.CompilerServices";
        const string name = "ReferenceAssemblyAttribute";
        if (assembly.CustomAttributes.Any(IsReferenceMark)) return;

        var factory = module.CorLibTypeFactory;
        var type = new TypeReference(module, factory.CorLibScope, ns, name);
        var constructor = new MemberReference(type, ".ctor", MethodSignature.CreateInstance(factory.Void));
        assembly.CustomAttributes.Add(new CustomAttribute(constructor));

        static bool IsReferenceMark(CustomAttribute a) => a.Constructor?.DeclaringType?.FullName == ns + "." + name;
    }
}
