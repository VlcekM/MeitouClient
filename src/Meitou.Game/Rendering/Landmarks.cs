using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// Which placed objects are landmarks (docs/renderer-native.md 8.19): meshes so large in the world that they are worth drawing across the whole map
/// (the giant satellite and ring wrecks, huge skeletons, towers). A placement is one when its world bounding radius (the mesh's bounds radius, as
/// <see cref="ObjectMeshCache"/> decodes it, times the placement's largest scale) reaches <see cref="MinRadius"/>. The radius is read from the
/// <c>.mesh</c> file's bounds chunk without loading the mesh (<see cref="OgreMeshReader.TryReadBounds"/>), so a placement far outside any streaming range
/// can be classified; a file without a readable bounds chunk is read whole.
/// </summary>
sealed class LandmarkClass(AssetLocator assets)
{
    /// <summary>
    /// A placement is a landmark from this world bounding radius up (units). Chosen from a whole-world survey (docs/renderer-native.md 8.19):
    /// no building part of an ordinary building reaches 1500 (the largest, the Ancient Factory's shed, is 1488) and the usual map features stay below 1000;
    /// 2000 keeps the giants (the Skylink satellite, the rib cages, the tower cores, the ring and dangler wrecks) and leaves out the cliff blocks.
    /// </summary>
    public const float MinRadius = 2000;

    readonly ConcurrentDictionary<string, float> radii = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The mesh's local bounds radius (0: the file was not found or unreadable). Safe from any thread; cached by path.</summary>
    public float MeshRadius(string meshPath) => radii.GetOrAdd(meshPath, Probe);

    float Probe(string name)
    {
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) return 0;
        if (OgreMeshReader.TryReadBounds(path, out var b) && b.Max.X >= b.Min.X) return Math.Max((b.Max - b.Min).Length() / 2, 1e-3f);
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            if (mesh.Bounds is { } bounds && bounds.Max.X >= bounds.Min.X) return Math.Max((bounds.Max - bounds.Min).Length() / 2, 1e-3f);
            return Model.Build(mesh, null).Radius;
        }
        catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
        {
            return 0;
        }
    }

    /// <summary>The largest scale of a placement's transform (what <see cref="WorldObjectRenderer"/> multiplies a mesh's radius by).</summary>
    public static float Scale(in Matrix4x4 t) =>
        MathF.Sqrt(Math.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(), Math.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));

    /// <summary>The placement's world bounding radius.</summary>
    public float WorldRadius(PlacedMesh p) => MeshRadius(p.MeshPath) * Scale(p.Transform);

    public bool IsLandmark(PlacedMesh p) => WorldRadius(p) >= MinRadius;

    /// <summary>
    /// Every landmark placement of the world (all populated zones laid out once, in parallel: about a second), with its world radius, in zone order.
    /// Worlds are small (the base game: 26 000 placements in 913 zones), so this is cheaper than streaming zones out to a landmark distance, and it
    /// finds a landmark long before the camera is anywhere near it.
    /// </summary>
    public List<(PlacedMesh Placed, float Radius)> Collect(WorldObjects objects)
    {
        var byZone = new ConcurrentDictionary<ZoneCoordinate, List<(PlacedMesh, float)>>();
        Parallel.ForEach(objects.PopulatedZones.Where(z => z.IsInsideGrid), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 3) }, zone =>
        {
            List<(PlacedMesh, float)>? found = null;
            foreach (var p in objects.BuildZone(zone).Items)
            {
                float radius = WorldRadius(p);
                if (radius >= MinRadius) (found ??= []).Add((p, radius));
            }
            if (found is not null) byZone[zone] = found;
        });
        return [.. byZone.OrderBy(z => z.Key.X).ThenBy(z => z.Key.Y).SelectMany(z => z.Value)];
    }

}
