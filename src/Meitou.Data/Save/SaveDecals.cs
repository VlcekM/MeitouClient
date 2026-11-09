using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// The terrain's blood decals in the TERRAIN_DECALS record (docs/formats/save.md "Terrain decals"): <c>total</c> and one instance per cell copy, the
/// id a 1-based counter as raw int32 bytes, target <c>-</c>, the decal packed as raw bits into the position and rotation floats.
/// </summary>
public static class SaveDecals
{
    /// <summary>
    /// The decals of <paramref name="record"/>; an instance whose type is not one of the four is skipped.
    /// </summary>
    public static List<TerrainDecal> Read(FcsRecord record)
    {
        var decals = new List<TerrainDecal>(record.Instances.Count);
        foreach (var instance in record.Instances)
            if (Unpack(instance) is { } d) decals.Add(d);
        return decals;
    }

    /// <summary>
    /// Replaces <paramref name="record"/>'s instances and <c>total</c> with <paramref name="decals"/>.
    /// </summary>
    public static void Write(FcsRecord record, IEnumerable<TerrainDecal> decals)
    {
        record.Instances.Clear();
        foreach (var d in decals) record.Instances.Add(Pack(d, record.Instances.Count + 1));
        record.Ints["total"] = record.Instances.Count;
    }

    /// <summary>
    /// x = local X | local Z &lt;&lt; 16, y = half width | half height &lt;&lt; 16, z = rotation, qw = colour, qx = cell key, qy = type | variant
    /// &lt;&lt; 8 (all raw bits), qz = life. Null when the type is not one of the four.
    /// </summary>
    public static TerrainDecal? Unpack(FcsInstance instance)
    {
        uint x = Bits(instance.Position.X), y = Bits(instance.Position.Y), z = Bits(instance.Position.Z);
        var q = instance.Rotation;
        uint kind = Bits(q.Y);
        if ((kind & 0xFF) > (uint)TerrainDecalType.Smear) return null;
        return new TerrainDecal(Bits(q.X), (TerrainDecalType)(kind & 0xFF), (byte)(kind >> 8), (short)x, (short)(x >> 16), (short)y, (short)(y >> 16),
            (short)z, Bits(q.W), q.Z);
    }

    /// <summary>
    /// Packs <paramref name="d"/> into an instance with id <paramref name="id"/> (see <see cref="Unpack"/>).
    /// </summary>
    public static FcsInstance Pack(in TerrainDecal d, int id) => new()
    {
        Id = LosslessUtf8.GetString(BitConverter.GetBytes(id)),
        Target = "-",
        Position = new Vector3(Float(Pair(d.LocalX, d.LocalZ)), Float(Pair(d.HalfWidth, d.HalfHeight)), Float((ushort)d.Rotation)),
        Rotation = new Quaternion(Float(d.Key), Float((uint)d.Type | (uint)d.Variant << 8), d.Life, Float(d.Colour)),
    };

    /// <summary>
    /// Two shorts as one uint, <paramref name="low"/> in the low 16 bits.
    /// </summary>
    static uint Pair(short low, short high) => (ushort)low | (uint)(ushort)high << 16;

    /// <summary>
    /// The raw bits of a float.
    /// </summary>
    static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    /// <summary>
    /// A float with the raw bits <paramref name="bits"/>.
    /// </summary>
    static float Float(uint bits) => BitConverter.UInt32BitsToSingle(bits);
}