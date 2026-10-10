using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// A frame fixed to the stars, in world coordinates: <see cref="X"/> the direction of the "equinox" (where the sun rises), <see cref="Y"/> the
/// celestial pole, <see cref="Z"/> = X × Y (where the sun culminates). A world direction d has the celestial coordinates (X·d, Y·d, Z·d).
/// </summary>
public readonly record struct CelestialFrame(Vector3 X, Vector3 Y, Vector3 Z)
{
    public Vector3 ToCelestial(Vector3 world) => new(Vector3.Dot(X, world), Vector3.Dot(Y, world), Vector3.Dot(Z, world));
    public Vector3 ToWorld(Vector3 celestial) => X * celestial.X + Y * celestial.Y + Z * celestial.Z;
}

/// <summary>
/// The turning sky of the Meitou <c>stars</c> switch (the viewer's own design, not the game's: docs/formats/sky.md "Meitou night sky"). The stars turn
/// about the axis the sun's path turns about, one turn per game day, at the game's latitude. <see cref="SkyClock.SunDirection"/> puts the sun on the
/// plane spanned by (1, 0, 0) and (0, cos lat, sin lat), so its axis is their cross product, (0, −sin lat, cos lat); the celestial pole is the end
/// of it that is above the horizon, (0, sin lat, −cos lat), at the altitude of the latitude. Stars turn about it the way the sun does (from its
/// rising direction +x up and over +z), so both rise on one side and set on the other.
/// </summary>
public static class CelestialSphere
{
    /// <summary>The turn of the sky at game hour 0 of any day, in radians (a viewer choice).</summary>
    public const double PhaseOffset = -Math.PI / 3;

    /// <summary>The axis the sun's path turns about (unit): the sun turns right-handed about it. For the game's latitude (0, −sin lat, cos lat).</summary>
    public static Vector3 SunAxis(SkyClock clock)
    {
        var rise = clock.SunDirection(clock.Sunrise);
        var noon = clock.SunDirection((clock.Sunrise + clock.Sunset) * 0.5f);
        return Vector3.Normalize(Vector3.Cross(rise, noon));
    }

    /// <summary>The celestial pole: the visible end of the sun's axis (above the horizon in the northern hemisphere), (0, sin lat, −cos lat) at 54°.</summary>
    public static Vector3 Pole(SkyClock clock)
    {
        var n = SunAxis(clock);
        return n.Y > 0 ? n : -n;
    }

    /// <summary>The turn after <paramref name="day"/> days and <paramref name="hour"/> hours: one turn per game day, plus <see cref="PhaseOffset"/>, in 0..2π + offset.</summary>
    public static double Angle(int day, float hour)
    {
        double turns = (day * 24.0 + hour) / 24.0;
        turns -= Math.Floor(turns);
        return turns * 2 * Math.PI + PhaseOffset;
    }

    /// <summary>The celestial axes in world coordinates at <paramref name="day"/> and <paramref name="hour"/>: the fixed ones (equinox = the sun's rising direction, pole) turned about the sun's axis.</summary>
    public static CelestialFrame Frame(SkyClock clock, int day, float hour)
    {
        var n = SunAxis(clock);
        var q = Quaternion.CreateFromAxisAngle(n, (float)Angle(day, hour));
        var x = clock.SunDirection(clock.Sunrise);
        var y = Pole(clock);
        return new CelestialFrame(Vector3.Transform(x, q), Vector3.Transform(y, q), Vector3.Transform(Vector3.Cross(x, y), q));
    }

    /// <summary>The altitude above the horizon of a world direction, in degrees.</summary>
    public static float AltitudeDegrees(Vector3 world) => MathF.Asin(Math.Clamp(world.Y, -1f, 1f)) * 180 / MathF.PI;
}

/// <summary>
/// What the air does to a star on its way down (the <c>stars</c> switch): extinction grows with the air mass and reddens, stars vanish at the horizon,
/// and low stars twinkle. The constants are the shader's too (<c>SkyRenderer</c> writes them in).
/// </summary>
public static class NightAtmosphere
{
    /// <summary>Extinction per air mass beyond the zenith's, per colour channel (the V band loses about 0.2 magnitude per air mass, the blue 0.35, the red 0.1: e^−k).</summary>
    public static readonly Vector3 ExtinctionPerAirMass = new(0.08f, 0.13f, 0.22f);

    /// <summary>Below <see cref="HorizonLow"/> degrees nothing is seen, above <see cref="HorizonHigh"/> the horizon ramp is 1 (smoothstep between).</summary>
    public const float HorizonLow = 0.5f, HorizonHigh = 3.5f;

    /// <summary>Stars twinkle only where the air mass is above <see cref="ScintillationFreeAirMass"/> (about 33° up); the amplitude grows to <see cref="ScintillationMax"/> at the horizon.</summary>
    public const float ScintillationFreeAirMass = 1.8f, ScintillationMax = 0.5f, ScintillationRange = 10f;

    /// <summary>Kasten and Young's (1989) air mass for an altitude in degrees (1 at the zenith, 38 at the horizon).</summary>
    public static float AirMass(float altitudeDegrees)
    {
        float h = MathF.Max(altitudeDegrees, 0);
        return 1 / (MathF.Sin(h * MathF.PI / 180) + 0.50572f * MathF.Pow(h + 6.07995f, -1.6364f));
    }

    /// <summary>The share of each channel's light that arrives (relative to the zenith's), times the ramp that takes it to 0 at the horizon.</summary>
    public static Vector3 Transmission(float altitudeDegrees)
    {
        float x = AirMass(altitudeDegrees) - 1;
        return new Vector3(MathF.Exp(-ExtinctionPerAirMass.X * x), MathF.Exp(-ExtinctionPerAirMass.Y * x), MathF.Exp(-ExtinctionPerAirMass.Z * x)) * Horizon(altitudeDegrees);
    }

    /// <summary>The ramp that makes stars vanish at the horizon: 0 at <see cref="HorizonLow"/> and below, 1 from <see cref="HorizonHigh"/>.</summary>
    public static float Horizon(float altitudeDegrees)
    {
        float t = Math.Clamp((altitudeDegrees - HorizonLow) / (HorizonHigh - HorizonLow), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The relative brightness swing of a faint star's twinkle: 0 above about 33°, rising to <see cref="ScintillationMax"/> at the horizon.</summary>
    public static float Scintillation(float altitudeDegrees) =>
        Math.Clamp((AirMass(altitudeDegrees) - ScintillationFreeAirMass) / ScintillationRange, 0, 1) * ScintillationMax;
}

/// <summary>One density layer of the procedural starfield: <see cref="Cells"/> cells a side on each cube face, <see cref="Count"/> stars over the sphere between the magnitudes.</summary>
public readonly record struct StarLayer(int Cells, float Count, float BrightestMagnitude, float FaintestMagnitude, float Slope = StarField.MagnitudeSlope, float ReachPixels = 2.2f);

/// <summary>A star the cell of a layer holds (see <see cref="StarField.Candidate"/>): where on its cube face, how bright, which colour.</summary>
public readonly record struct StarCandidate(bool Present, Vector2 FaceUv, float Magnitude, float Brightness, int Colour, float Phase);

/// <summary>
/// The procedural starfield (viewer design): a direction → cube face → a grid of cells per layer; a hash of (face, layer, cell) decides whether the
/// cell holds a star, where in it, its magnitude (a power law: about 3.16 times more stars per magnitude), its colour and its twinkle phase. Three
/// layers by magnitude: bright (a thousand stars), main (naked-eye) and faint (a background). The shader (<c>SkyRenderer</c>) evaluates exactly this,
/// with the same hash and numbers; this class is the reference the tests check (counts, colours).
/// </summary>
public static class StarField
{
    /// <summary>Slope of log₁₀ N(&lt;m) against magnitude (the real sky's is about 0.5: 3.16 times more stars per magnitude).</summary>
    public const float MagnitudeSlope = 0.5f;
    /// <summary>
    /// A star's displayed brightness is <c>10^(−0.4 · γ · m)</c>: γ &lt; 1 compresses the real sky's range (a magnitude-1 star is 100 times a
    /// magnitude-6 one in light, 25 times on this screen), the usual planetarium trick, so faint stars show beside bright ones.
    /// </summary>
    public const float BrightnessGamma = 0.7f;

    /// <summary>
    /// The layers, brightest first. Counts follow the real sky past magnitude 4.5 (about 900 stars brighter than 4.5, 9000 brighter than 6.5); the bright
    /// layer has a flatter slope and twice as many stars, so a few of the brightest show on any screen (about 100 brighter than magnitude 1 over the whole sphere,
    /// the real sky has 15). The stars are spread evenly over the sphere.
    /// </summary>
    public static readonly StarLayer[] Layers =
    [
        new(28, 1800, -2f, 4.5f, 0.35f, 20),
        new(64, 8100, 4.5f, 6.5f),
        new(160, 45000, 6.5f, 8.2f),
    ];

    /// <summary>How many of <see cref="Layers"/> an integrated GPU draws (the first two: the faint background is sub-pixel anyway).</summary>
    public const int IntegratedLayers = 2;

    /// <summary>The share of the sphere's stars a cell holds on average: <c>count / 4π</c> per steradian times the cell's solid angle (a cube face cell shrinks towards the corners).</summary>
    public static float CellChance(StarLayer layer, Vector2 centre)
    {
        float omega = 4f / (layer.Cells * layer.Cells) * MathF.Pow(1 + centre.X * centre.X + centre.Y * centre.Y, -1.5f);
        return layer.Count / (4 * MathF.PI) * omega;
    }

    /// <summary>Unnormalised direction of a point of a cube face: the face's axis ±1 and the other two components (in x, y, z order) from <paramref name="uv"/>.</summary>
    public static Vector3 FaceDirection(int face, Vector2 uv)
    {
        float s = (face & 1) == 0 ? 1 : -1;
        return (face >> 1) switch { 0 => new(s, uv.X, uv.Y), 1 => new(uv.X, s, uv.Y), _ => new(uv.X, uv.Y, s) };
    }

    /// <summary>The face and the position on it (±1) of a direction: the largest component's axis, the other two over its size.</summary>
    public static int Face(Vector3 d, out Vector2 uv)
    {
        var a = Vector3.Abs(d);
        if (a.X >= a.Y && a.X >= a.Z) { uv = new Vector2(d.Y, d.Z) / a.X; return d.X > 0 ? 0 : 1; }
        if (a.Y >= a.Z) { uv = new Vector2(d.X, d.Z) / a.Y; return d.Y > 0 ? 2 : 3; }
        uv = new Vector2(d.X, d.Y) / a.Z;
        return d.Z > 0 ? 4 : 5;
    }

    /// <summary>pcg3d (Jarzynski and Olano, 2020): three well mixed words from three. The shader has the same function.</summary>
    public static (uint X, uint Y, uint Z) Pcg3d(uint x, uint y, uint z)
    {
        unchecked
        {
            x = x * 1664525u + 1013904223u; y = y * 1664525u + 1013904223u; z = z * 1664525u + 1013904223u;
            x += y * z; y += z * x; z += x * y;
            x ^= x >> 16; y ^= y >> 16; z ^= z >> 16;
            x += y * z; y += z * x; z += x * y;
        }
        return (x, y, z);
    }

    /// <summary>The one-word PCG hash (O'Neill) of a cell id: whether a cell has a star is drawn from it, so the full <see cref="Pcg3d"/> is only worked out for stars. The shader has the same function.</summary>
    public static uint Pcg1(uint v)
    {
        unchecked
        {
            uint state = v * 747796405u + 2891336453u;
            uint word = ((state >> (int)((state >> 28) + 4)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }
    }

    /// <summary>The id a cell hashes with: unique over the layers (six faces each), faces and cells (fewer than 256 a side).</summary>
    public static uint CellId(int layer, int face, int ix, int iy) => (uint)(((layer * 6 + face) * 256 + iy) * 256 + ix);

    /// <summary>The displayed brightness (peak energy before the shader's gain) of a star of magnitude <paramref name="magnitude"/>.</summary>
    public static float Brightness(float magnitude) => MathF.Pow(10, -0.4f * BrightnessGamma * magnitude);

    /// <summary>The magnitude at the fraction <paramref name="u"/> (0..1) of a layer's cumulative count: the power law inverted.</summary>
    public static float MagnitudeAt(StarLayer layer, float u)
    {
        float k = MathF.Pow(10, layer.Slope * (layer.FaintestMagnitude - layer.BrightestMagnitude));
        return layer.BrightestMagnitude + MathF.Log(1 + u * (k - 1)) / (layer.Slope * MathF.Log(10));
    }

    /// <summary>How far in from a cell's edge a star is kept (fraction of the cell), and on a cube face's outer edge: no star's spot crosses a face edge.</summary>
    public const float CellMargin = 0.1f, FaceEdgeMargin = 0.25f;

    /// <summary>The star of the cell (<paramref name="ix"/>, <paramref name="iy"/>) of <paramref name="layer"/> on <paramref name="face"/>, if it has one.</summary>
    public static StarCandidate Candidate(int layer, int face, int ix, int iy)
    {
        var l = Layers[layer];
        int n = l.Cells;
        var centre = new Vector2(2f * (ix + 0.5f) / n - 1, 2f * (iy + 0.5f) / n - 1);
        float chance = CellChance(l, centre);
        if ((Pcg1(CellId(layer, face, ix, iy)) >> 16) * (1f / 65536) >= chance) return default;
        var (hx, hy, hz) = Pcg3d((uint)ix, (uint)iy, (uint)(face + 8 * layer));
        var j = new Vector2((hy & 0xFFFFu) * (1f / 65536), (hy >> 16) * (1f / 65536));
        var lo = new Vector2(ix == 0 ? FaceEdgeMargin : CellMargin, iy == 0 ? FaceEdgeMargin : CellMargin);
        var hi = new Vector2(ix == n - 1 ? 1 - FaceEdgeMargin : 1 - CellMargin, iy == n - 1 ? 1 - FaceEdgeMargin : 1 - CellMargin);
        var pos = Vector2.Lerp(lo, hi, j);
        var uv = new Vector2(2 * (ix + pos.X) / n - 1, 2 * (iy + pos.Y) / n - 1);
        float mag = MagnitudeAt(l, (hz & 0xFFFFu) * (1f / 65536));
        int colour = (int)((hz >> 16) * (StarColours.TableSize / 65536f));
        return new StarCandidate(true, uv, mag, Brightness(mag), colour, (hx >> 16) * (1f / 65536));
    }
}

/// <summary>Star colours: B−V colour index → temperature → a blackbody's tint, kept near white (the eye and cameras see stars as pale), in a table that a uniform random number indexes.</summary>
public static class StarColours
{
    public const int TableSize = 32;
    /// <summary>How much of the blackbody's saturation is kept (0 white, 1 the full colour).</summary>
    public const float Saturation = 0.3f;

    /// <summary>Ballesteros (2012): the effective temperature in kelvin of a star of colour index <paramref name="bv"/>.</summary>
    public static float Temperature(float bv) => 4600 * (1 / (0.92f * bv + 1.7f) + 1 / (0.92f * bv + 0.62f));

    /// <summary>A blackbody's linear sRGB at <paramref name="kelvin"/> (1667..25000: Kim et al.'s fit of the Planckian locus), scaled to luminance 1, blue-white to orange; not clamped to white.</summary>
    public static Vector3 Blackbody(float kelvin)
    {
        float t = Math.Clamp(kelvin, 1667, 25000), t2 = t * t, t3 = t2 * t;
        float x = t < 4000 ? -0.2661239e9f / t3 - 0.2343589e6f / t2 + 0.8776956e3f / t + 0.179910f
                           : -3.0258469e9f / t3 + 2.1070379e6f / t2 + 0.2226347e3f / t + 0.240390f;
        float y = t < 2222 ? -1.1063814f * x * x * x - 1.34811020f * x * x + 2.18555832f * x - 0.20219683f
                : t < 4000 ? -0.9549476f * x * x * x - 1.37418593f * x * x + 2.09137015f * x - 0.16748867f
                           : 3.0817580f * x * x * x - 5.87338670f * x * x + 3.75112997f * x - 0.37001483f;
        float cx = x / y, cz = (1 - x - y) / y;   // XYZ with Y = 1
        var rgb = new Vector3(3.2406f * cx - 1.5372f - 0.4986f * cz, -0.9689f * cx + 1.8758f + 0.0415f * cz, 0.0557f * cx - 0.2040f + 1.0570f * cz);
        rgb = Vector3.Max(rgb, Vector3.Zero);
        return rgb / Luminance(rgb);
    }

    public static float Luminance(Vector3 rgb) => 0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z;

    /// <summary>The colour index distribution of the naked-eye sky, as a mixture: blue (B), white (A and F), yellow and orange (G and K giants), red (M).</summary>
    static readonly (float Weight, float Mean, float Spread)[] ColourIndexMix = [(0.12f, -0.10f, 0.10f), (0.60f, 0.32f, 0.25f), (0.20f, 0.80f, 0.20f), (0.08f, 1.40f, 0.20f)];

    /// <summary>The tint of the star at the fraction <paramref name="u"/> of the colour index distribution: its blackbody, desaturated by <see cref="Saturation"/>, luminance 1.</summary>
    public static Vector3 Tint(float u)
    {
        // Invert the mixture's CDF (normal CDFs, the logistic approximation Φ(z) ≈ 1 / (1 + e^(−1.702 z))) by bisection over the colour indices.
        float lo = -0.35f, hi = 2.0f;
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi), cdf = 0;
            foreach (var (w, mean, spread) in ColourIndexMix) cdf += w / (1 + MathF.Exp(-1.702f * (mid - mean) / spread));
            if (cdf < u) lo = mid; else hi = mid;
        }
        var c = Blackbody(Temperature(0.5f * (lo + hi)));
        c = Vector3.Lerp(Vector3.One, c, Saturation);
        return c / Luminance(c);
    }

    /// <summary>The table the shader indexes with a uniform random number: <see cref="TableSize"/> tints at the middles of equal shares of the distribution.</summary>
    public static Vector3[] Table()
    {
        var t = new Vector3[TableSize];
        for (int i = 0; i < t.Length; i++) t[i] = Tint((i + 0.5f) / TableSize);
        return t;
    }
}

