using System.Collections.Concurrent;
using Meitou.Content;

namespace Meitou.Data.Ogre;

/// <summary>
/// Cross-checks a material library against the install: do the materials named by meshes exist, and do the
/// textures named by materials exist as files. Shared by the tests and <c>meitou-tools materials</c>.
/// </summary>
public sealed class OgreMaterialAudit
{
    public int MeshFiles { get; private set; }
    public List<(string File, string Error)> MeshFailures { get; } = [];
    public int SubMeshes { get; private set; }
    public int ResolvedSubMeshes { get; private set; }
    public int SubMeshesWithTextureAliases { get; private set; }

    /// <summary>Distinct material names used by submeshes.</summary>
    public SortedSet<string> MeshMaterials { get; } = new(StringComparer.Ordinal);

    /// <summary>Submeshes per material name.</summary>
    public SortedDictionary<string, int> SubMeshesByMaterial { get; } = new(StringComparer.Ordinal);

    /// <summary>Mesh material names the library does not define, with the meshes (relative to data/) that use them.</summary>
    public SortedDictionary<string, List<string>> UnresolvedMaterials { get; } = new(StringComparer.Ordinal);

    /// <summary>Texture files named by texture units (pairs of unit and file).</summary>
    public int TextureReferences { get; private set; }

    public SortedSet<string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Texture names with no file of that name (case-insensitive) in the index, with the materials using them.</summary>
    public SortedDictionary<string, List<string>> MissingTextures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Texture units that name no file: filled by a compositor/shadow (<c>content_type</c>) or at run time.</summary>
    public int UnitsWithoutTexture { get; private set; }

    public int ResolvedMeshMaterials => MeshMaterials.Count - UnresolvedMaterials.Count;
    public int FoundTextures => Textures.Count - MissingTextures.Count;

    /// <param name="files">Where texture names are looked up (by bare name).</param>
    public static OgreMaterialAudit Run(GameInstall install, OgreMaterialLibrary library, OgreScriptResources files)
    {
        var audit = new OgreMaterialAudit();

        var meshFiles = Directory.EnumerateFiles(install.DataDirectory, "*.mesh", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        audit.MeshFiles = meshFiles.Count;
        var meshes = new ConcurrentDictionary<string, OgreMesh>();
        var failures = new ConcurrentBag<(string, string)>();
        Parallel.ForEach(meshFiles, file =>
        {
            try { meshes[file] = OgreMeshReader.ReadFile(file); }
            catch (FormatException e) { failures.Add((Path.GetRelativePath(install.DataDirectory, file), e.Message)); }
        });
        audit.MeshFailures.AddRange(failures.OrderBy(f => f.Item1, StringComparer.Ordinal));

        foreach (var file in meshFiles)
        {
            if (!meshes.TryGetValue(file, out var mesh)) continue;
            var relative = Path.GetRelativePath(install.DataDirectory, file);
            foreach (var sub in mesh.SubMeshes)
            {
                audit.SubMeshes++;
                if (sub.TextureAliases.Count > 0) audit.SubMeshesWithTextureAliases++;
                audit.MeshMaterials.Add(sub.MaterialName);
                audit.SubMeshesByMaterial[sub.MaterialName] = audit.SubMeshesByMaterial.GetValueOrDefault(sub.MaterialName) + 1;
                if (library.Materials.ContainsKey(sub.MaterialName))
                    audit.ResolvedSubMeshes++;
                else
                {
                    if (!audit.UnresolvedMaterials.TryGetValue(sub.MaterialName, out var users))
                        audit.UnresolvedMaterials[sub.MaterialName] = users = [];
                    if (users.Count == 0 || users[^1] != relative) users.Add(relative);
                }
            }
        }

        foreach (var material in library.Materials.Values)
            foreach (var unit in material.Techniques.SelectMany(t => t.Passes).SelectMany(p => p.TextureUnits))
            {
                var names = unit.TextureFiles().ToList();
                if (names.Count == 0) audit.UnitsWithoutTexture++;
                foreach (var name in names)
                {
                    audit.TextureReferences++;
                    audit.Textures.Add(name);
                    if (files.Contains(name)) continue;
                    if (!audit.MissingTextures.TryGetValue(name, out var users))
                        audit.MissingTextures[name] = users = [];
                    if (!users.Contains(material.Name)) users.Add(material.Name);
                }
            }
        return audit;
    }
}
