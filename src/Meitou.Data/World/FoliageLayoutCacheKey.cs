using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>
/// The 32-byte key of the foliage layout cache (see <see cref="FoliageLayoutCache"/>): a SHA-256 over a text of everything the layout reads. The text
/// must stay byte-identical for the same inputs, or every cache on disk silently goes stale.
/// </summary>
public static class FoliageLayoutCacheKey
{
    /// <summary>
    /// The 32-byte key (see <see cref="FoliageLayoutCache"/>'s summary).
    /// </summary>
    public static byte[] Compute(GameInstall install, FoliageCatalog catalog, IEnumerable<FoliageTown> towns)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var text = new StringBuilder();
        text.Append("meitou-foliage-layout-cache|").Append(FoliageLayoutCache.FormatVersion).Append('\n');
        text.Append("layout|").Append(FoliageLayoutCache.LayoutVersion).Append('\n');
        AppendBiomes(hash, text, catalog);
        AppendTowns(text, towns);
        AppendFiles(text, install);
        Flush(hash, text);
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Hashes the text so far and empties it.
    /// </summary>
    static void Flush(IncrementalHash hash, StringBuilder text)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(text.ToString()));
        text.Clear();
    }

    /// <summary>
    /// The records: biomes in colour order, their layers, each layer's meshes (with children) and grass types, field by field; hashed per biome.
    /// </summary>
    static void AppendBiomes(IncrementalHash hash, StringBuilder text, FoliageCatalog catalog)
    {
        var done = new HashSet<GameRecord>();
        foreach (var (colour, layers) in catalog.ByBiome.OrderBy(b => b.Key))
        {
            text.Append("biome|").Append(colour).Append('|').Append(layers.Count).Append('\n');
            foreach (var layer in layers) AppendLayer(text, layer, done);
            Flush(hash, text);
        }
    }

    /// <summary>
    /// One layer: its record, its meshes with their counts, its grass types with their channels.
    /// </summary>
    static void AppendLayer(StringBuilder text, FoliageLayer layer, HashSet<GameRecord> done)
    {
        text.Append("layer|").Append(layer.StringId).Append('\n');
        if (layer.Record is { } lr && done.Add(lr)) AppendRecord(text, lr);
        foreach (var (mesh, count) in layer.Meshes)
        {
            text.Append("m|").Append(count).Append('\n');
            AppendMesh(text, mesh, done);
        }
        foreach (var (grass, channel) in layer.Grass)
        {
            text.Append("g|").Append(channel).Append('\n');
            if (grass.Record is { } gr) AppendRecord(text, gr);
            else text.Append(grass.StringId).Append('\n');
        }
    }

    /// <summary>
    /// The towns the layout keeps clear, by position and range.
    /// </summary>
    static void AppendTowns(StringBuilder text, IEnumerable<FoliageTown> towns)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var (at, range) in towns)
        {
            text.Append("town|").Append(at.X.ToString("R", inv)).Append('|').Append(at.Y.ToString("R", inv)).Append('|')
                .Append(range.ToString("R", inv)).Append('\n');
        }
    }

    /// <summary>
    /// The files the layout reads, by size and write time.
    /// </summary>
    static void AppendFiles(StringBuilder text, GameInstall install)
    {
        AppendFile(text, Path.Combine(install.DataDirectory, TerrainHeightmap.RelativePath), "heightmap");
        AppendFile(text, Path.Combine(install.DataDirectory, BlendInfoFile.RelativePath), "blend");
        AppendFile(text, Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap), "biomemap");
        var overlays = Path.Combine(install.DataDirectory, TerrainMaps.LandDirectory, "overlaymaps");
        if (!Directory.Exists(overlays)) return;
        foreach (var f in Directory.EnumerateFiles(overlays, "new_overlay.*.png").OrderBy(f => f, StringComparer.Ordinal))
            AppendFile(text, f, Path.GetFileName(f));
    }

    /// <summary>
    /// One file by name, size and write time (-1 and 0 when it is missing).
    /// </summary>
    static void AppendFile(StringBuilder text, string path, string name)
    {
        var info = new FileInfo(path);
        text.Append("file|").Append(name).Append('|').Append(info.Exists ? info.Length : -1).Append('|')
            .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
    }

    /// <summary>
    /// A mesh's record, then its children's, then its building type; a mesh met before is named again only.
    /// </summary>
    static void AppendMesh(StringBuilder text, FoliageMesh mesh, HashSet<GameRecord> done)
    {
        if (!done.Add(mesh.Record))
        {
            text.Append("again|").Append(mesh.StringId).Append('\n');
            return;
        }

        AppendRecord(text, mesh.Record);
        foreach (var child in mesh.Children) AppendMesh(text, child, done);
        if (mesh.BuildingType is { } b) text.Append("building|").Append(b.StringId).Append('\n');
    }

    /// <summary>
    /// A record's id, type and name, then every field and reference list in ordinal key order.
    /// </summary>
    static void AppendRecord(StringBuilder text, GameRecord r)
    {
        text.Append("rec|").Append(r.StringId).Append('|').Append((int)r.Type).Append('|').Append(r.Name).Append('\n');
        AppendScalars(text, r);
        AppendVectors(text, r);
        foreach (var (k, v) in r.Strings.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('s').Append(k).Append('=').Append(v).Append('\n');
        foreach (var (k, v) in r.Filenames.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('p').Append(k).Append('=').Append(v).Append('\n');
        AppendReferences(text, r);
    }

    /// <summary>
    /// The bool, float (by bits) and int fields.
    /// </summary>
    static void AppendScalars(StringBuilder text, GameRecord r)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var (k, v) in r.Bools.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append('b').Append(k).Append('=').Append(v ? 1 : 0).Append('\n');
        foreach (var (k, v) in r.Floats.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append('f').Append(k).Append('=').Append(BitConverter.SingleToInt32Bits(v).ToString(inv)).Append('\n');
        foreach (var (k, v) in r.Ints.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('i').Append(k).Append('=').Append(v).Append('\n');
    }

    /// <summary>
    /// The three- and four-component vector fields, round-trip formatted.
    /// </summary>
    static void AppendVectors(StringBuilder text, GameRecord r)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var (k, v) in r.Vector3S.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append('v').Append(k).Append('=').Append(v.X.ToString("R", inv)).Append(',').Append(v.Y.ToString("R", inv)).Append(',')
                .Append(v.Z.ToString("R", inv)).Append('\n');
        }
        foreach (var (k, v) in r.Vector4S.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append('w').Append(k).Append('=').Append(v.X.ToString("R", inv)).Append(',').Append(v.Y.ToString("R", inv)).Append(',')
                .Append(v.Z.ToString("R", inv)).Append(',').Append(v.W.ToString("R", inv)).Append('\n');
        }
    }

    /// <summary>
    /// Each reference list by name, its entries with their three values.
    /// </summary>
    static void AppendReferences(StringBuilder text, GameRecord r)
    {
        foreach (var list in r.ReferenceLists.OrderBy(l => l, StringComparer.Ordinal))
        {
            text.Append('r').Append(list).Append('\n');
            foreach (var x in r.GetReferences(list))
            {
                text.Append(' ').Append(x.TargetStringId).Append(':').Append(x.Values.Value0).Append(',').Append(x.Values.Value1).Append(',')
                    .Append(x.Values.Value2).Append('\n');
            }
        }
    }
}
