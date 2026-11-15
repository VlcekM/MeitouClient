using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Physics;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The hash of the inputs every zone's mesh shares (<see cref="NavMeshCache.ContentHash"/>): each file by name, size and write time (not
/// contents), a missing one as missing, so that a mod update that changes any of them builds the meshes again.
/// </summary>
internal sealed class NavContentHash
{
    /// <summary>
    /// The FNV-1a state.
    /// </summary>
    uint _hash = 2166136261;

    /// <summary>
    /// The hash of <paramref name="install"/>'s inputs under <paramref name="db"/> and <paramref name="levels"/>: the data files in load order,
    /// the seed, height, blend, biome, map feature and overlay files, every level file of every layer, every collision file a record names, and
    /// the DotRecast version.
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
    /// The game-data files in load order (record contents: buildings, layouts' parts, foliage, map features).
    /// </summary>
    void AddDataFiles(GameInstall install, GameDatabase db)
    {
        var mods = ModList.FindMods(install);
        foreach (var name in db.Files)
        {
            Stamp(LoadOrder.BaseFiles.Contains(name) ? Path.Combine(install.DataDirectory, name)
                : mods.TryGetValue(Path.GetFileNameWithoutExtension(name), out var mod) ? mod : name);
        }
    }

    /// <summary>
    /// The seed, height, blend, biome, map feature and overlay files the gather and the foliage placer read.
    /// </summary>
    void AddMapFiles(GameInstall install)
    {
        foreach (var rel in new[] { NavSeeds.RelativePath, TerrainHeightmap.RelativePath, BlendInfoFile.RelativePath, TerrainMaps.BiomeMap,
            MapFeatureFile.RelativePath })
            Stamp(Path.Combine(install.DataDirectory, rel));

        if (Path.GetDirectoryName(TerrainMaps.OverlayTile(install, "new_overlay", 0, 0)) is not { } overlays || !Directory.Exists(overlays))
            return;

        foreach (var tile in Directory.GetFiles(overlays, "new_overlay.*").Order(StringComparer.OrdinalIgnoreCase))
            Stamp(tile);
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
            if (CollisionPaths.Resolve(install, path) is not { } file)
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
