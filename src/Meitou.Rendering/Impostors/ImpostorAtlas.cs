using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace Meitou.Rendering.Impostors;

/// <summary>The maps of an atlas (docs/impostors.md, "Maps").</summary>
public enum ImpostorMap
{
    /// <summary>RGB: the albedo the mesh shader lights (texture x vertex colour, dual blend), premultiplied by coverage on the GPU (BC1 with a 1-bit alpha: a transparent texel decodes to black); A: the cut-out.</summary>
    Albedo = 0,
    /// <summary>RG: the shading normal in the frame's basis (x right, y up, z towards the viewer), octahedrally encoded (<see cref="ImpostorLayout.EncodeNormal"/>), x 0.5 + 0.5.</summary>
    Normal = 1,
}

/// <summary>How a map's levels are stored (and uploaded).</summary>
public enum ImpostorEncoding
{
    /// <summary>Uncompressed, 4 bytes a pixel (a debugging choice).</summary>
    Rgba8 = 0,
    /// <summary>BC3 (DXT5), 1 byte a pixel.</summary>
    Bc3 = 1,
    /// <summary>BC5 (RGTC2, two channels), 1 byte a pixel.</summary>
    Bc5 = 2,
    /// <summary>BC1 (DXT1) in its 1-bit alpha mode, half a byte a pixel: a texel is covered (colour) or transparent (black, alpha 0).</summary>
    Bc1 = 3,
}

/// <summary>One map: its encoding and its levels, level 0 first (each <c>Grid x frame / 2^level</c> pixels square, rows bottom first).</summary>
public sealed class ImpostorTexture
{
    public required ImpostorMap Map { get; init; }
    public required ImpostorEncoding Encoding { get; init; }
    public required byte[][] Levels { get; init; }

    public static int LevelBytes(ImpostorEncoding encoding, int size) => encoding switch
    {
        ImpostorEncoding.Rgba8 => size * size * 4,
        ImpostorEncoding.Bc1 => size / 4 * (size / 4) * 8,
        _ => size / 4 * (size / 4) * 16,
    };
}

/// <summary>
/// A baked impostor atlas and its file format (<c>.mimp</c>, docs/impostors.md "File format"): a small header (magic, versions, grid, frame
/// size, levels, the bounding sphere in object space, the gloss, the source's name), the per-map level sizes, then the level data, deflated,
/// with an FNV-1a checksum of the inflated data. <see cref="Read"/> returns null for anything that does not match (wrong magic, version,
/// sizes, checksum, truncation): the caller bakes again.
/// </summary>
public sealed class ImpostorAtlas
{
    public const uint Magic = 0x504D494D;   // "MIMP"
    public const int FormatVersion = 2;

    /// <summary>Bumped whenever the baker's output changes (shader, filtering, layout): part of the cache key and checked on load.</summary>
    public const int BakerVersion = 5;

    public required int Grid { get; init; }
    public required int FramePixels { get; init; }
    /// <summary>The bounding sphere the frames cover, in the mesh's object space.</summary>
    public required Vector3 Centre { get; init; }
    public required float Radius { get; init; }
    /// <summary>The coverage-weighted mean of gloss x specular over the baked frames (what the mesh passes to the lighting), 0 to 1.</summary>
    public float Gloss { get; init; } = 0.3f;
    /// <summary>What was baked (FOLIAGE_MESH name and mesh file), for messages.</summary>
    public string Name { get; init; } = "";
    public required ImpostorTexture[] Textures { get; init; }

    public int Levels => Textures.Length == 0 ? 0 : Textures[0].Levels.Length;
    public int AtlasPixels => Grid * FramePixels;
    public long Bytes => Textures.Sum(t => t.Levels.Sum(l => (long)l.Length));

    public ImpostorTexture? this[ImpostorMap map] => Textures.FirstOrDefault(t => t.Map == map);

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(BakerVersion);
        w.Write(Grid);
        w.Write(FramePixels);
        w.Write(Levels);
        w.Write(Centre.X); w.Write(Centre.Y); w.Write(Centre.Z);
        w.Write(Radius);
        w.Write(Gloss);
        w.Write(Name);
        w.Write(Textures.Length);
        foreach (var t in Textures)
        {
            w.Write((int)t.Map);
            w.Write((int)t.Encoding);
            foreach (var level in t.Levels) w.Write(level.Length);
        }
        ulong hash = Fnv.Offset;
        foreach (var t in Textures)
            foreach (var level in t.Levels) hash = Fnv.Hash(hash, level);
        w.Write(hash);
        w.Flush();
        using var z = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true);
        foreach (var t in Textures)
            foreach (var level in t.Levels) z.Write(level);
    }

    /// <summary>The atlas in <paramref name="stream"/>, or null when it is not a valid atlas of this format and baker version.</summary>
    public static ImpostorAtlas? Read(Stream stream)
    {
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != FormatVersion || r.ReadInt32() != BakerVersion) return null;
            int grid = r.ReadInt32(), frame = r.ReadInt32(), levels = r.ReadInt32();
            if (grid is < 2 or > 64 || frame is < 4 or > 4096 || !BitOperations.IsPow2(frame) || levels < 1 || frame >> (levels - 1) < 4) return null;
            var centre = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float radius = r.ReadSingle(), gloss = r.ReadSingle();
            if (!(radius > 0) || !float.IsFinite(centre.X + centre.Y + centre.Z) || !float.IsFinite(gloss)) return null;
            string name = r.ReadString();
            int count = r.ReadInt32();
            if (count is < 1 or > 8) return null;
            var specs = new (ImpostorMap Map, ImpostorEncoding Encoding, int[] Sizes)[count];
            for (int i = 0; i < count; i++)
            {
                var map = (ImpostorMap)r.ReadInt32();
                var encoding = (ImpostorEncoding)r.ReadInt32();
                if (!Enum.IsDefined(map) || !Enum.IsDefined(encoding)) return null;
                var sizes = new int[levels];
                for (int l = 0; l < levels; l++)
                {
                    sizes[l] = r.ReadInt32();
                    if (sizes[l] != ImpostorTexture.LevelBytes(encoding, grid * (frame >> l))) return null;
                }
                specs[i] = (map, encoding, sizes);
            }
            ulong expected = r.ReadUInt64();
            using var z = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            var textures = new ImpostorTexture[count];
            ulong hash = Fnv.Offset;
            for (int i = 0; i < count; i++)
            {
                var data = new byte[levels][];
                for (int l = 0; l < levels; l++)
                {
                    data[l] = new byte[specs[i].Sizes[l]];
                    z.ReadExactly(data[l]);
                    hash = Fnv.Hash(hash, data[l]);
                }
                textures[i] = new ImpostorTexture { Map = specs[i].Map, Encoding = specs[i].Encoding, Levels = data };
            }
            if (hash != expected) return null;
            return new ImpostorAtlas { Grid = grid, FramePixels = frame, Centre = centre, Radius = radius, Gloss = gloss, Name = name, Textures = textures };
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    static class Fnv
    {
        public const ulong Offset = 14695981039346656037;
        const ulong Prime = 1099511628211;

        public static ulong Hash(ulong h, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) h = (h ^ b) * Prime;
            return h;
        }
    }
}
