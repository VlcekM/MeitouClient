using System.Numerics;
using Meitou.Data.Havok;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Navigation;

namespace Meitou.Tests.Save;

/// <summary>
/// Open ground (<see cref="OpenGroundWalkability"/>) that also knows the faces of the original's own saved navmesh tiles (<c>zone/tileX.Y.hkt</c> in a
/// save: the tiles the original regenerated at run time with the player's buildings in them), as the town refit's snap reads them.
/// </summary>
sealed class SavedTileWalkability : IWalkability
{
    readonly OpenGroundWalkability _ground;
    readonly List<SurfaceFace> _faces = [];

    public SavedTileWalkability(Func<float, float, float> ground, string saveDirectory, params (int X, int Z)[] zones)
    {
        _ground = new OpenGroundWalkability(ground);
        foreach (var (x, z) in zones)
        {
            var zone = new ZoneCoordinate(x, z);
            string path = Path.Combine(saveDirectory, "zone", $"tile{x}.{z}.hkt");
            if (OriginalNavTiles.Exterior(HavokTagfile.ReadFile(path), zone) is { } mesh) AddFaces(mesh);
        }
    }

    void AddFaces(OriginalNavMesh mesh)
    {
        for (int f = 0; f < mesh.Polygons.Length; f++)
        {
            var corners = mesh.Polygons[f].Select(i => mesh.Vertices[i]).ToArray();
            var open = mesh.Edges[f].Select(e => e == OriginalEdge.End).ToArray();
            _faces.Add(new SurfaceFace(corners, open, false));
        }
    }

    public float GroundHeight(float x, float z) => _ground.GroundHeight(x, z);

    public bool IsWalkable(float x, float z) => _ground.IsWalkable(x, z);

    public PathResult FindPath(Vector3 from, Vector3 to) => _ground.FindPath(from, to);

    public IReadOnlyList<SurfaceFace>? SurfaceFaces(Vector3 centre, float reach) => _faces;
}
