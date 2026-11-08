using System.Numerics;

namespace Meitou.Rendering.Gi;

/// <summary>
/// Which probes the fog hides (docs/render-gi.md "Probes in the fog"). A probe only matters through the surfaces that read it: a pixel's or a
/// bounce's lookup reads the 8 probes around <c>q = p + offset</c> (<c>giCascade</c>), the offset at most 0.3 of the smaller spacing, so probe (column, layer)
/// is read from p within one spacing across (the trilinear weight is 0 beyond) and, up and down, from <c>base + (l - 0.5) sy</c> to <c>base + (l + 1.5) sy</c>
/// (the bottom layer from below too, the top layer from above too: the layer index is clamped). When the fog cull proves everything in that box hidden
/// (<see cref="FogVolumes.Covers"/>: wholly inside the eye's fog block and past its hide distance, or past the weather fog's), no pixel reads the probe.
/// </summary>
internal static class GiFogHide
{
    /// <summary>The fraction of the smaller spacing that a lookup's point is moved off the surface (<c>giCascade</c>: <c>0.3 * min(s, sy)</c>).</summary>
    public const float LookupOffset = 0.3f;

    /// <summary>
    /// The reach box of a probe column's lowest <paramref name="layers"/> probes: the column's cell widened by one spacing and the lookup's offset (and
    /// <paramref name="slack"/>) across, from <paramref name="floorY"/> up to the top of the highest layer's reach.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) ReachBox(float x, float z, float baseY, float spacing, float spacingY, int layers, float floorY, float slack)
    {
        float offset = LookupOffset * MathF.Min(spacing, spacingY), across = spacing + offset + slack;
        float top = baseY + (layers - 1 + 1.5f) * spacingY + offset;
        return (new Vector3(x - across, MathF.Min(floorY, top), z - across), new Vector3(x + across, top, z + across));
    }

    /// <summary>Set in a list entry of a hidden probe: its update is one of every <c>period</c>, so the blend uses the hysteresis of that many updates in one.</summary>
    public const uint SlowBit = 0x80000000u;

    /// <summary>Set in a list entry of a probe of the far cascade that is updated on one of every <c>farPeriod</c> rounds only (<see cref="GiProbes.FarPeriod"/>).</summary>
    public const uint FarBit = 0x40000000u;

    /// <summary>
    /// A frame's probe list: every <paramref name="phases"/>-th probe from <paramref name="phase"/> in ascending order (the shaders' old
    /// <c>gid * phases + phase</c>), but a probe whose layer is below its column's entry of <paramref name="hiddenLayers"/> (per tile, cascade-major, the
    /// column's toroidal row and column) only on one of every <paramref name="period"/> of its own rounds (<paramref name="round"/> counts this
    /// phase's updates), with <see cref="SlowBit"/> set. <paramref name="period"/> 0: no probe is hidden and the list is the old one. Offsets are the
    /// cascades' toroidal offsets of window column 0 (x, z, in <paramref name="offsets"/> as x0, z0, x1, z1). Returns the entries written.
    /// </summary>
    public static int List(Span<uint> into, int phases, int phase, uint round, int period, ReadOnlySpan<byte> hiddenLayers, params ReadOnlySpan<float> offsets) =>
        List(into, phases, phase, round, period, 0, hiddenLayers, offsets);

    /// <summary>
    /// As above, with the probes of the cascades after the first (<paramref name="farPeriod"/> above 1) listed only on one of every <paramref name="farPeriod"/> of their own rounds,
    /// keyed on the tile like the sleep rule, with <see cref="FarBit"/> set; a probe both hidden and far is a hidden one.
    /// </summary>
    public static int List(Span<uint> into, int phases, int phase, uint round, int period, int farPeriod, ReadOnlySpan<byte> hiddenLayers, ReadOnlySpan<float> offsets)
    {
        const int columns = GiProbes.Columns, layers = GiProbes.Layers;
        int n = 0;
        if (period <= 0 && farPeriod <= 1)
        {
            for (int p = phase; p < GiProbes.Probes; p += phases) into[n++] = (uint)p;
            return n;
        }
        // The layers are a multiple of the phases, so a column's probes of this phase are its layers phase, phase + phases, ... (ascending probe order).
        for (int c = 0; c < GiProbes.Cascades; c++)
        {
            int offX = (int)offsets[c * 2], offZ = (int)offsets[c * 2 + 1];
            for (int wz = 0; wz < columns; wz++)
            {
                int colZ = offZ + wz; if (colZ >= columns) colZ -= columns;
                for (int wx = 0; wx < columns; wx++)
                {
                    int colX = offX + wx; if (colX >= columns) colX -= columns;
                    int hidden = period > 0 ? hiddenLayers[(c * columns + colZ) * columns + colX] : 0, first = ((c * columns + wz) * columns + wx) * layers;
                    for (int layer = phase; layer < layers; layer += phases)
                    {
                        int p = first + layer;
                        if (layer < hidden)
                        {
                            // Keyed on the probe's tile, as the sleep rule is (giAsleep): a hidden sleeper is then listed only on rounds where it is awake (period a multiple of 8).
                            if ((round + (uint)(((c * columns + colZ) * columns + colX) * layers + layer)) % (uint)period != 0) continue;
                            into[n++] = (uint)p | SlowBit;
                        }
                        else if (c > 0 && farPeriod > 1)
                        {
                            if ((round + (uint)(((c * columns + colZ) * columns + colX) * layers + layer)) % (uint)farPeriod != 0) continue;
                            into[n++] = (uint)p | FarBit;
                        }
                        else into[n++] = (uint)p;
                    }
                }
            }
        }
        return n;
    }

    /// <summary>
    /// How many of a column's lowest probes no surface that the fog leaves visible can read: the largest L (0 to <paramref name="layerCount"/> - 1) whose
    /// reach box (<see cref="ReachBox"/>) <paramref name="covers"/> says is hidden. The top layer is never counted: its reach has no upper end.
    /// </summary>
    public static int HiddenLayers(Func<Vector3, Vector3, bool> covers, float x, float z, float baseY, float spacing, float spacingY, int layerCount, float floorY, float slack)
    {
        // Most columns are all or nothing: ask for the most layers first, then for the fewest, and only then bisect (coverage only gets harder with a taller box).
        int lo = 1, hi = layerCount - 1;
        var (min, max) = ReachBox(x, z, baseY, spacing, spacingY, hi, floorY, slack);
        if (covers(min, max)) return hi;
        (min, max) = ReachBox(x, z, baseY, spacing, spacingY, lo, floorY, slack);
        if (!covers(min, max)) return 0;
        hi--;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            (min, max) = ReachBox(x, z, baseY, spacing, spacingY, mid, floorY, slack);
            if (covers(min, max)) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
