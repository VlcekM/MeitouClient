using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>A town with a baked distant mesh: one low-poly batch of its buildings, drawn when the town is far away.</summary>
/// <param name="Handle">The <c>handC</c> of the town's GAMESTATE_TOWN, which names the file.</param>
/// <param name="MeshPath">Full path of <c>data/meshes/distant/distant_&lt;Handle&gt;.mesh</c>.</param>
/// <param name="Position">The town's placement position; the mesh's vertices are relative to it (Y included).</param>
public sealed record DistantTown(string InstanceId, string TownId, string Name, Vector3 Position, int Handle, string MeshPath);

/// <summary>
/// Distant towns (docs/formats/zones.md, "Distant towns"): the game keeps, for each TOWN with the <c>distant mesh</c> flag,
/// one mesh merging the <c>distant mesh</c> of every building of the town, baked into <c>data/meshes/distant/</c>
/// under the town state's <c>handC</c> number.
/// </summary>
public static class DistantTowns
{
    /// <summary>Folder of the baked meshes below the install's <c>data/</c> folder.</summary>
    public const string RelativeDirectory = "meshes/distant";

    /// <summary>The texture of the game's <c>DistantTown</c> material (in <c>data/buildings/distant/</c>).</summary>
    public const string DiffuseTexture = "distant_diffuse.dds";

    /// <summary>Factor the game's <c>DistantTown</c> shader multiplies vertex colour × texture by.</summary>
    public const float AlbedoScale = 1.5f;

    public static string MeshFileName(int handle) => $"distant_{handle}.mesh";

    /// <summary>The placed towns whose record has <c>distant mesh</c> set and whose baked mesh exists in the install.</summary>
    public static List<DistantTown> Find(GameInstall install, GameDatabase db, WorldLevelData world)
    {
        var handles = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var state in world.Level.OfType(FcsRecordType.GAMESTATE_TOWN))
            if (state.Ints.TryGetValue("handC", out int c) && state.GetString("instance") is { Length: > 0 } instance)
                handles.TryAdd(instance, c);
        var result = new List<DistantTown>();
        string directory = Path.Combine(install.DataDirectory, RelativeDirectory);
        foreach (var town in world.Towns())
        {
            if (db.Find(town.TownId) is not { } record || !record.GetBool("distant mesh")) continue;
            if (!handles.TryGetValue(town.InstanceId, out int handle)) continue;
            string path = Path.Combine(directory, MeshFileName(handle));
            if (File.Exists(path)) result.Add(new DistantTown(town.InstanceId, town.TownId, record.Name, town.Position, handle, path));
        }
        return result;
    }
}
