using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Meitou.Content;

namespace Meitou.Modding;

/// <summary>
/// Whether a code mod has a data phase (docs/modding.md "Data phase"), read from its manifest and its assemblies' metadata without loading or running
/// any of its code, so a session in which no enabled mod has one loads its mods exactly as before (<see cref="ModHost.Load"/>).
/// </summary>
static class ModDataScan
{
    /// <summary>
    /// The namespace of <see cref="IModData"/>.
    /// </summary>
    const string ContractNamespace = "Meitou.Modding";

    /// <summary>
    /// Whether the mod's entry type, or a base class of it in the same assembly, declares <see cref="IModData"/>; false when the manifest or an
    /// assembly cannot be read (the mod's load then reports it as before).
    /// </summary>
    public static bool HasDataPhase(ModInfo mod)
    {
        try
        {
            var manifest = CodeModManifest.Read(mod.CodeManifestPath);
            return manifest.Assemblies
                .Select(a => Path.Combine(mod.CodeFolder, a))
                .Where(File.Exists)
                .Any(path => Declares(path, manifest.Entry));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the assembly at <paramref name="path"/> defines the type of full name <paramref name="entry"/> declaring <see cref="IModData"/>.
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
            if (FullName(md, type) == entry) return Implements(md, type);
        }

        return false;
    }

    /// <summary>
    /// A type's full name as <see cref="System.Reflection.Assembly.GetType(string)"/> takes it (a nested type after its declaring type and <c>+</c>).
    /// </summary>
    static string FullName(MetadataReader md, TypeDefinition type)
    {
        string name = md.GetString(type.Name);
        var outer = type.GetDeclaringType();
        if (!outer.IsNil) return FullName(md, md.GetTypeDefinition(outer)) + "+" + name;

        string space = md.GetString(type.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    /// <summary>
    /// Whether <paramref name="type"/> or a base class defined in the same assembly lists <see cref="IModData"/> among its interfaces.
    /// </summary>
    static bool Implements(MetadataReader md, TypeDefinition type)
    {
        while (true)
        {
            foreach (var handle in type.GetInterfaceImplementations())
                if (IsContract(md, md.GetInterfaceImplementation(handle).Interface)) return true;
            if (type.BaseType.Kind != HandleKind.TypeDefinition) return false;

            type = md.GetTypeDefinition((TypeDefinitionHandle)type.BaseType);
        }
    }

    /// <summary>
    /// Whether an interface reference names <see cref="IModData"/> (a reference to the game's assembly).
    /// </summary>
    static bool IsContract(MetadataReader md, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeReference) return false;

        var reference = md.GetTypeReference((TypeReferenceHandle)handle);
        return md.StringComparer.Equals(reference.Namespace, ContractNamespace) && md.StringComparer.Equals(reference.Name, nameof(IModData));
    }
}
