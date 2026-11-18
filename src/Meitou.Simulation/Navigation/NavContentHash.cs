using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Physics;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The hash of the inputs every zone's mesh shares (<see cref="NavMeshCache.ContentHash"/>): each file by name, size and write time (not
/// contents), a missing one as missing, so that a mod update that changes any of them builds the meshes again; and the heightmap fix's repairs
/// (<see cref="HeightmapRepair.StampFor"/>), while it is on.
/// </summary>
internal sealed class NavContentHash
{
    /// <summary>
    /// The FNV-1a state.
    /// </summary>
    uint _hash = 2166136261;

    /// <summary>
    /// The hash of <paramref name="install"/>'s inputs under <paramref name="db"/> and <paramref name="levels"/>: the data files in load order,
    /// the seed, height, blend, biome, map feature and overlay files, the heightmap fix's repairs, every level file of every layer, every collision
    /// file a record names, and the DotRecast version.
    /// </summary>
    public static uint Of(GameInstall install, GameDatabase db, WorldLevelData levels)
    {
        var h = new NavContentHash();
        h.AddDataFiles(install, db);
        h.AddMapFiles(install);
        h.AddLevelFiles(levels);
        h.AddCollisionFiles(install, db);
        h.Add(typeof(DotRecast.Recast.RcBuilder).Assembly.GetName().Version?.ToString() ?? "");
        return h._hash;
    }

    /// <summary>
    /// The game-data files in load order (record contents: buildings, layouts' parts, foliage, map features), then the fingerprints of what code
    /// changed in the records after them (<see cref="GameDatabase.CodeEdits"/>).
    /// </summary>
    void AddDataFiles(GameInstall install, GameDatabase db)
    {
        var mods = ModList.FindMods(install);
        foreach (var name in db.Files)
        {
            Stamp(LoadOrder.BaseFiles.Contains(name) ? Path.Combine(install.DataDirectory, name)
                : mods.TryGetValue(Path.GetFileNameWithoutExtension(name), out var mod) ? mod : name);
        }

        // What code changed in the records after the files (a code mod's data phase); none without one, so the hash is as before.
        foreach (var edit in db.CodeEdits)
            Add(edit);
    }

    /// <summary>
    /// The seed, height, blend, biome, map feature and overlay files the gather and the foliage placer read, and the heightmap fix's repairs.
    /// </summary>
    void AddMapFiles(GameInstall install)
    {
        foreach (var path in new[] { NavSeeds.PathOf(install), TerrainHeightmap.PathOf(install), BlendInfoFile.PathOf(install) })
            Stamp(path);
        Stamp(TerrainMaps.BiomeMapPath(install));
        Stamp(MapFeatureFile.PathOf(install));

        // The tiles the gather reads (a mod's copy where one is enabled).
        for (int x = 0; x < TerrainMaps.OverlayTiles; x++)
            for (int z = 0; z < TerrainMaps.OverlayTiles; z++)
                Stamp(TerrainMaps.OverlayTile(install, TerrainMaps.OverlayKind, x, z));

        // The heightmap fix changes the ground the gather reads.
        if (HeightmapRepair.StampFor(TerrainHeightmap.PathOf(install)) is { Length: > 0 } repair) Add(repair);
    }

    /// <summary>
    /// Every layer's zone, level and interiors files, layer by layer (placements, building states, towns and layouts the gather and the foliage
    /// placer read; a zone or layer a mod adds or drops changes the list).
    /// </summary>
    void AddLevelFiles(WorldLevelData levels)
    {
        foreach (var layer in levels.Layers)
        {
            Add(layer.Name);

            // A folder removed since the levels were read (a mod unsubscribed while the game runs).
            if (!Directory.Exists(layer.Directory))
            {
                Add("-");
                continue;
            }

            foreach (var file in layer.Files())
                Stamp(file);
        }
    }

    /// <summary>
    /// Every collision file (<c>.xml</c>) a record names, as the install resolves it (the file it finds, a mod's or the base game's), in one
    /// order whatever the records' order; one that resolves to nothing by its record path.
    /// </summary>
    void AddCollisionFiles(GameInstall install, GameDatabase db)
    {
        foreach (var path in CollisionRecordPaths(db))
        {
            // Lower case: the set keeps whichever spelling came first, and record order is not fixed.
            Add(path.ToLowerInvariant());
            if (CollisionPaths.ResolveCollision(install, path) is not { } file)
            {
                Add("-");
                continue;
            }

            Add(file);
            Stamp(file);
        }
    }

    /// <summary>
    /// The distinct paths of every record's file fields the gather reads as collision (<c>xml collision</c>, <c>destroyed collision</c>,
    /// <c>collision</c>, <c>destroyed boundary</c>) and any other <c>.xml</c> one, forward slashes, no leading <c>./</c>, sorted ignoring case.
    /// </summary>
    static SortedSet<string> CollisionRecordPaths(GameDatabase db)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in db.Records.Values)
        {
            foreach (var (field, path) in record.Filenames)
            {
                if (path.Length > 0 && (IsCollisionField(field) || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                    paths.Add(path.Replace('\\', '/').TrimStart('.', '/'));
            }
        }

        return paths;
    }

    /// <summary>
    /// Whether a file field holds a collision file whatever its extension (<c>... collision</c>, <c>destroyed boundary</c>).
    /// </summary>
    static bool IsCollisionField(string field) =>
        field.Contains("collision", StringComparison.OrdinalIgnoreCase) || field.Contains("boundary", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mixes a file's name and its size and write time (or "-" when it does not exist) into the hash.
    /// </summary>
    void Stamp(string path)
    {
        var file = new FileInfo(GameFiles.Resolve(path));
        Add(file.Name);
        Add(file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "-");
    }

    /// <summary>
    /// Mixes <paramref name="text"/> and a separator into the hash.
    /// </summary>
    void Add(string text)
    {
        foreach (char c in text)
            _hash = (_hash ^ c) * 16777619;

        _hash = (_hash ^ 0xff) * 16777619;
    }
}
