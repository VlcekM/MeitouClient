namespace Meitou.Data.World;

/// <summary>
/// The grass blades of one GRASS patch over a square page of its zone (docs/formats/foliage.md, "Grass blades"), by the game
/// grass loader's rule: <c>density × 0.005 × area × grass density setting</c> candidates, each at a uniform random point;
/// kept when the ground's slope there is at most the grass's <c>max slope</c>, a random number is below the density map
/// (0..1) there, and (when either altitude limit is set) the height is inside them. A kept blade gets one random scale
/// (0..1, used for both width and height) and a random yaw. The game's page layout and random numbers depend on its
/// camera; this field is seeded by page and grass instead, so it is deterministic but not the game's exact blades.
/// </summary>
public static class FoliageGrassField
{
    /// <summary>Floats per blade: x, y, z, scale (0..1), yaw (radians).</summary>
    public const int Stride = 5;

    /// <summary>Each worker thread's blade buffer: a page's blades are collected here and copied out at their exact size (a list sized for the candidates was mostly empty and on the large object heap).</summary>
    [ThreadStatic] static List<float>? scratch;
    /// <summary>Each worker thread's generator, reseeded per page and patch (its 2.5 KB state was a new array every call).</summary>
    [ThreadStatic] static FoliageRandom? random;

    /// <summary>Checkpoints in <see cref="BladesWithPrefixes"/>: the blade count after each 1/<see cref="PrefixSteps"/> of the candidates.</summary>
    public const int PrefixSteps = 64;

    /// <summary>
    /// The blades in the page from (<paramref name="x0"/>, <paramref name="z0"/>) of side <paramref name="size"/>, as
    /// <see cref="Stride"/> floats each.
    /// </summary>
    public static float[] Blades(FoliageGrassPatch patch, FoliageGround ground, float x0, float z0, float size, float densitySetting = 1) =>
        BladesWithPrefixes(patch, ground, x0, z0, size, out _, densitySetting);

    /// <summary>
    /// <see cref="Blades"/> plus <paramref name="prefixes"/> (<see cref="PrefixSteps"/> + 1 entries): how many blades the first
    /// i/<see cref="PrefixSteps"/> of the candidates gave. The candidates come from one random sequence in order, so the blades
    /// for a lower density setting are exactly the first ones of a higher setting's, and a page generated at the highest
    /// setting can show any lower one by drawing a prefix (<see cref="PrefixCount"/>).
    /// </summary>
    public static float[] BladesWithPrefixes(FoliageGrassPatch patch, FoliageGround ground, float x0, float z0, float size, out int[] prefixes, float densitySetting = 1) =>
        Generate(patch, ground, x0, z0, size, out prefixes, densitySetting) is { } blades ? [.. blades] : [];

    /// <summary>
    /// <see cref="BladesWithPrefixes"/> into an array rented from <see cref="System.Buffers.ArrayPool{T}.Shared"/> (empty, not rented, without blades): its first
    /// <paramref name="floats"/> hold the blades. The caller returns it.
    /// </summary>
    public static float[] RentBladesWithPrefixes(FoliageGrassPatch patch, FoliageGround ground, float x0, float z0, float size, out int[] prefixes, out int floats, float densitySetting = 1)
    {
        var blades = Generate(patch, ground, x0, z0, size, out prefixes, densitySetting);
        floats = blades?.Count ?? 0;
        if (floats == 0) return [];
        var rented = System.Buffers.ArrayPool<float>.Shared.Rent(floats);
        blades!.CopyTo(rented);
        return rented;
    }

    // The blades into this thread's scratch list (valid until its next call); null when the page can have none.
    static List<float>? Generate(FoliageGrassPatch patch, FoliageGround ground, float x0, float z0, float size, out int[] prefixes, float densitySetting)
    {
        var grass = patch.Grass;
        prefixes = new int[PrefixSteps + 1];
        int candidates = (int)(grass.PerSquareUnit * densitySetting * size * size);
        if (candidates <= 0) return null;
        // Where the patch's density is 0 throughout the page every candidate is dropped (it keeps a blade only for a random number below the density), so none is drawn:
        // most pages of a zone's patches, and most of a town's loading-screen grass time.
        if (!AnyDensity(patch, x0, z0, size)) return null;
        uint seed = FoliageNoise.Hash((int)MathF.Floor(x0), (int)MathF.Floor(z0)) ^ (uint)FoliageRandom.LeadingInteger(grass.StringId) * 2654435761u ^ (uint)patch.Channel;
        var rng = random ??= new FoliageRandom(seed);
        rng.Seed(seed);
        bool limits = grass.MinAltitude != 0 || grass.MaxAltitude != 0;
        float lo = grass.MinAltitude == 0 ? float.NegativeInfinity : grass.MinAltitude;
        float hi = grass.MaxAltitude == 0 ? float.PositiveInfinity : grass.MaxAltitude;
        var result = scratch ??= new List<float>(1 << 16);
        result.Clear();
        int step = 1;
        for (int n = 0; n < candidates; n++)
        {
            while (step <= PrefixSteps && (long)n * PrefixSteps >= (long)step * candidates) prefixes[step++] = result.Count / Stride;
            float x = x0 + size * rng.Float(0, 1);
            float z = z0 + size * rng.Float(0, 1);
            if (ground.Slope(x, z) > grass.MaxSlope) continue;
            float r = rng.Float(0, 1);
            if (DensityAt(patch, x, z) <= r) continue;
            float y = ground.Height(x, z);
            if (limits && (y < lo || y > hi)) continue;
            result.Add(x);
            result.Add(y);
            result.Add(z);
            result.Add(rng.Float(0, 1));
            result.Add(rng.Float(0, MathF.Tau));
        }
        while (step <= PrefixSteps) prefixes[step++] = result.Count / Stride;
        return result;
    }

    /// <summary>The number of leading blades that a density setting of <paramref name="fraction"/> (0..1) of the generated one shows.</summary>
    public static int PrefixCount(int[] prefixes, float fraction)
    {
        float at = Math.Clamp(fraction, 0, 1) * PrefixSteps;
        int i = Math.Min((int)at, PrefixSteps - 1);
        return prefixes[i] + (int)((prefixes[i + 1] - prefixes[i]) * (at - i));
    }

    /// <summary>Whether a sample of the patch's density map that <see cref="DensityAt"/> can read anywhere in the square is above 0 (a cell's margin on each side).</summary>
    static bool AnyDensity(FoliageGrassPatch patch, float x0, float z0, float size)
    {
        const int n = FoliageGround.Size;
        if (x0 + size < patch.X0 || x0 > patch.X1 || z0 + size < patch.Z0 || z0 > patch.Z1) return false;
        float su = (n - 1) / (patch.X1 - patch.X0), sv = (n - 1) / (patch.Z1 - patch.Z0);
        int i0 = Math.Clamp((int)MathF.Floor((x0 - patch.X0) * su) - 1, 0, n - 1), i1 = Math.Clamp((int)MathF.Floor((x0 + size - patch.X0) * su) + 2, 0, n - 1);
        int j0 = Math.Clamp((int)MathF.Floor((z0 - patch.Z0) * sv) - 1, 0, n - 1), j1 = Math.Clamp((int)MathF.Floor((z0 + size - patch.Z0) * sv) + 2, 0, n - 1);
        var d = patch.Density;
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
                if (d[j * n + i] != 0) return true;
        return false;
    }

    /// <summary>The patch's density (0..1) at a world point, bilinear over its 129² map; 0 outside the zone.</summary>
    public static float DensityAt(FoliageGrassPatch patch, float x, float z)
    {
        const int n = FoliageGround.Size;
        if (x < patch.X0 || x > patch.X1 || z < patch.Z0 || z > patch.Z1) return 0;
        float u = (x - patch.X0) / (patch.X1 - patch.X0) * (n - 1), v = (z - patch.Z0) / (patch.Z1 - patch.Z0) * (n - 1);
        int i = Math.Min((int)u, n - 2), j = Math.Min((int)v, n - 2);
        float fu = u - i, fv = v - j;
        var d = patch.Density;
        float top = d[j * n + i] * (1 - fu) + d[j * n + i + 1] * fu;
        float bottom = d[(j + 1) * n + i] * (1 - fu) + d[(j + 1) * n + i + 1] * fu;
        return (top * (1 - fv) + bottom * fv) / 255f;
    }
}
