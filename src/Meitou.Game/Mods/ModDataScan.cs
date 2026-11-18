using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Meitou.Content;

namespace Meitou.Modding;

/// <summary>
/// Whether a code mod has a data phase (docs/modding.md "Data phase"), read from its manifest and its assemblies' metadata without loading or running
/// any of its code, so a session in which no enabled mod has one loads its mods exactly as before (<see cref="ModHost.Load"/>). Only the mods found
/// here run <see cref="IModDataPhase.EditData"/>, so whether a mod's data phase runs never depends on the other mods.
/// </summary>
static class ModDataScan
{
    /// <summary>
    /// The namespace of <see cref="IModDataPhase"/>.
    /// </summary>
    const string ContractNamespace = "Meitou.Modding";

    /// <summary>
    /// Nesting deeper than this (in a type's name) or a longer base-class chain is taken as a broken assembly: no data phase.
    /// </summary>
    const int MaxDepth = 64;

    /// <summary>
    /// Whether the mod's entry type, or a base class of it (generic or not) defined in the same assembly, declares <see cref="IModDataPhase"/>;
    /// false when the manifest or an assembly cannot be read or makes no sense (the mod's load then reports it as before). Never throws.
    /// </summary>
    public static bool HasDataPhase(ModInfo mod)
    {
        try
        {
            var manifest = CodeModManifest.Read(mod.CodeManifestPath);
            return manifest.Assemblies
                .Select(a => Path.Combine(mod.Folder, a))
                .Where(File.Exists)
                .Any(path => Declares(path, manifest.Entry));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the assembly at <paramref name="path"/> defines the type of full name <paramref name="entry"/> declaring <see cref="IModDataPhase"/>.
    /// </summary>
    static bool Declares(string path, string entry)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return false;

        var md = pe.GetMetadataReader();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if (FullName(md, type, 0) == entry) return Implements(md, handle);
        }

        return false;
    }

    /// <summary>
    /// A type's full name as <see cref="System.Reflection.Assembly.GetType(string)"/> takes it (a nested type after its declaring type and <c>+</c>);
    /// null past <see cref="MaxDepth"/> levels of nesting.
    /// </summary>
    static string? FullName(MetadataReader md, TypeDefinition type, int depth)
    {
        if (depth > MaxDepth) return null;

        string name = md.GetString(type.Name);
        var outer = type.GetDeclaringType();
        if (!outer.IsNil) return FullName(md, md.GetTypeDefinition(outer), depth + 1) is { } outerName ? outerName + "+" + name : null;

        string space = md.GetString(type.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    /// <summary>
    /// Whether the type or a base class defined in the same assembly lists <see cref="IModDataPhase"/> among its interfaces; each type is visited
    /// once.
    /// </summary>
    static bool Implements(MetadataReader md, TypeDefinitionHandle start)
    {
        var seen = new HashSet<TypeDefinitionHandle>();
        for (var handle = start; seen.Add(handle) && seen.Count <= MaxDepth;)
        {
            var type = md.GetTypeDefinition(handle);
            foreach (var implementation in type.GetInterfaceImplementations())
                if (IsContract(md, md.GetInterfaceImplementation(implementation).Interface)) return true;

            if (DefinedBase(md, type.BaseType) is not { } next) return false;

            handle = next;
        }

        return false;
    }

    /// <summary>
    /// The base class when this assembly defines it: a plain definition, or the definition a generic instance (<c>Base&lt;T&gt;</c>) is made from;
    /// null for one defined elsewhere or none.
    /// </summary>
    static TypeDefinitionHandle? DefinedBase(MetadataReader md, EntityHandle baseType)
    {
        if (baseType.Kind == HandleKind.TypeDefinition) return (TypeDefinitionHandle)baseType;
        if (baseType.Kind != HandleKind.TypeSpecification) return null;

        var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)baseType).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle) return null;

        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)generic : null;
    }

    /// <summary>
    /// Whether an interface reference names <see cref="IModDataPhase"/> (a reference to the game's assembly).
    /// </summary>
    static bool IsContract(MetadataReader md, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeReference) return false;

        var reference = md.GetTypeReference((TypeReferenceHandle)handle);
        return md.StringComparer.Equals(reference.Namespace, ContractNamespace) && md.StringComparer.Equals(reference.Name, nameof(IModDataPhase));
    }
}
