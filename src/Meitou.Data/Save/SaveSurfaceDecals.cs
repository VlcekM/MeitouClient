using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// The blood on walls, floors and objects (Meitou-only, docs/formats/save.md "Terrain decals") as keys of the TERRAIN_DECALS record, which the
/// original reads by name and so ignores: per mark i from 0, the vec3 <c>meitou sd&lt;i&gt;</c> (centre), the vec4 <c>meitou sdS&lt;i&gt;</c> (angle,
/// size, life left, 0) and the ints <c>meitou sdK&lt;i&gt;</c> (type | variant &lt;&lt; 8 | floor &lt;&lt; 16) and <c>meitou sdC&lt;i&gt;</c> (colour
/// ABGR, raw bits). Read until a centre is missing.
/// </summary>
public static class SaveSurfaceDecals
{
    /// <summary>
    /// The key prefixes of a mark's centre, shape, kind and colour.
    /// </summary>
    const string Centre = "meitou sd", Shape = "meitou sdS", Kind = "meitou sdK", Colour = "meitou sdC";

    /// <summary>
    /// The marks of <paramref name="record"/>; one whose type is not a decal type is skipped.
    /// </summary>
    public static List<SurfaceBloodDecal> Read(FcsRecord record)
    {
        var decals = new List<SurfaceBloodDecal>();
        for (int i = 0; record.Vector3S.TryGetValue(Centre + i, out var centre); i++)
        {
            var shape = record.Vector4S.GetValueOrDefault(Shape + i);
            int kind = record.Ints.GetValueOrDefault(Kind + i);
            if ((kind & 0xFF) > (int)TerrainDecalType.Smear) continue;
            decals.Add(new SurfaceBloodDecal((TerrainDecalType)(kind & 0xFF), (byte)(kind >> 8), centre, shape.X, shape.Y, shape.Z,
                unchecked((uint)record.Ints.GetValueOrDefault(Colour + i)), (kind >> 16 & 1) != 0));
        }
        return decals;
    }

    /// <summary>
    /// Replaces the marks in <paramref name="record"/> with <paramref name="decals"/>.
    /// </summary>
    public static void Write(FcsRecord record, IEnumerable<SurfaceBloodDecal> decals)
    {
        foreach (var key in record.Vector3S.Keys.Where(k => k.StartsWith(Centre, StringComparison.Ordinal)).ToList()) record.Vector3S.Remove(key);
        foreach (var key in record.Vector4S.Keys.Where(k => k.StartsWith(Centre, StringComparison.Ordinal)).ToList()) record.Vector4S.Remove(key);
        foreach (var key in record.Ints.Keys.Where(k => k.StartsWith(Centre, StringComparison.Ordinal)).ToList()) record.Ints.Remove(key);
        int i = 0;
        foreach (var d in decals)
        {
            record.Vector3S[Centre + i] = d.Centre;
            record.Vector4S[Shape + i] = new Vector4(d.Angle, d.Size, d.Life, 0);
            record.Ints[Kind + i] = (int)d.Type | d.Variant << 8 | (d.Floor ? 1 << 16 : 0);
            record.Ints[Colour + i] = unchecked((int)d.Colour);
            i++;
        }
    }
}