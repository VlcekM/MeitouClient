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
    /// <summary>The turn of the sky at game hour 0 of any day, in radians (a viewer choice: puts the Milky Way's bright side over the south in the first half of the night).</summary>
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
public readonly record struct StarLayer(int Cells, float Count, float BrightestMagnitude, float FaintestMagnitude, float Enrichment, float Slope = StarField.MagnitudeSlope, float ReachPixels = 2.2f);

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
    /// the real sky has 15). The two faint layers gather towards the Milky Way.
    /// </summary>
    public static readonly StarLayer[] Layers =
    [
        new(28, 1800, -2f, 4.5f, 0, 0.35f, 20),
        new(64, 8100, 4.5f, 6.5f, 2.5f),
        new(160, 45000, 6.5f, 8.2f, 5f),
    ];

    /// <summary>The Milky Way's pull on the faint layers' density: a Gaussian of the sine of the galactic latitude of width <see cref="EnrichmentBand"/> plus <see cref="EnrichmentBulge"/> times a bump round the bulge (sharpness: e^−(1−cos)·s).</summary>
    public const float EnrichmentBand = 0.22f, EnrichmentBulge = 0.8f, EnrichmentBulgeSharpness = 8;
    /// <summary>The most the band and bulge terms add together (at the bulge's centre).</summary>
    public const float EnrichmentMax = 1 + EnrichmentBulge;

    /// <summary>How many of <see cref="Layers"/> an integrated GPU draws (the first two: the faint background is sub-pixel anyway).</summary>
    public const int IntegratedLayers = 2;

    /// <summary>The share of the sphere's stars a cell holds on average: <c>count / 4π</c> per steradian times the cell's solid angle (a cube face cell shrinks towards the corners).</summary>
    public static float CellChance(StarLayer layer, Vector2 centre)
    {
        float omega = 4f / (layer.Cells * layer.Cells) * MathF.Pow(1 + centre.X * centre.X + centre.Y * centre.Y, -1.5f);
        return layer.Count / (4 * MathF.PI) * omega;
    }

    /// <summary>The Milky Way's pull on a faint layer's star density: <c>1 + layer weight · (band + bulge)</c>, from the direction's celestial coordinates.</summary>
    public static float EnrichmentAt(StarLayer layer, Vector3 celestial)
    {
        if (layer.Enrichment <= 0) return 1;
        float gs = Vector3.Dot(celestial, MilkyWay.Pole), gc = Vector3.Dot(celestial, MilkyWay.Centre);
        return 1 + layer.Enrichment * (MathF.Exp(-gs * gs / (EnrichmentBand * EnrichmentBand)) + EnrichmentBulge * MathF.Exp(-(1 - gc) * EnrichmentBulgeSharpness));
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
        float chance = CellChance(l, centre) * EnrichmentAt(l, Vector3.Normalize(FaceDirection(face, centre)));
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

/// <summary>
/// The Milky Way of the <c>stars</c> switch (viewer design, an alien sky: not Earth's): a band round a great circle tilted against the celestial
/// equator, brightest in a bulge, lumpy with star clouds, cut by dust lanes that redden what they cover, warm in the core and cool at the edges.
/// <see cref="Radiance"/> evaluates it for a celestial direction (a 3D noise of the direction, so seamless); <see cref="Bake"/> draws it into
/// a cube map once, with mip levels. Values are in the starfield texture's units (the vanilla nebula runs about 0.1 to 0.3).
/// </summary>
public static class MilkyWay
{
    /// <summary>The band's plane is inclined by this to the celestial equator (so it reaches this declination, a little off the zenith at latitude 54°).</summary>
    public const float TiltDegrees = 62, CentreDeclinationDegrees = 15, NodeDegrees = 110;

    /// <summary>The galactic north pole, the bulge's centre and the direction of rising longitude, in celestial coordinates (x: RA 0, y: the pole).</summary>
    public static readonly Vector3 Pole, Centre, East;

    static MilkyWay()
    {
        float ra = NodeDegrees * MathF.PI / 180, tilt = TiltDegrees * MathF.PI / 180;
        var node = new Vector3(MathF.Cos(ra), 0, MathF.Sin(ra));
        var up = Vector3.UnitY;
        var q = MathF.Cos(tilt) * Vector3.Cross(up, node) + MathF.Sin(tilt) * up;
        Pole = Vector3.Normalize(Vector3.Cross(node, q));
        float lambda = MathF.Asin(MathF.Sin(CentreDeclinationDegrees * MathF.PI / 180) / MathF.Sin(tilt));
        Centre = Vector3.Normalize(MathF.Cos(lambda) * node + MathF.Sin(lambda) * q);
        East = Vector3.Normalize(Vector3.Cross(Pole, Centre));
    }

    /// <summary>Galactic latitude and longitude (radians, longitude 0 at the bulge) of a celestial direction.</summary>
    public static (float Latitude, float Longitude) Galactic(Vector3 c) =>
        (MathF.Asin(Math.Clamp(Vector3.Dot(c, Pole), -1f, 1f)), MathF.Atan2(Vector3.Dot(c, East), Vector3.Dot(c, Centre)));

    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    static float Gauss(float x, float width) => MathF.Exp(-x * x / (width * width));

    // ---- noise (Perlin gradient noise on the direction, in 3D so the cube's edges show no seam) ----

    static uint Hash(int x, int y, int z)
    {
        var (a, _, _) = StarField.Pcg3d((uint)x, (uint)y, (uint)z);
        return a;
    }

    static readonly Vector3[] Gradients =
    [
        new(1, 1, 0), new(-1, 1, 0), new(1, -1, 0), new(-1, -1, 0), new(1, 0, 1), new(-1, 0, 1),
        new(1, 0, -1), new(-1, 0, -1), new(0, 1, 1), new(0, -1, 1), new(0, 1, -1), new(0, -1, -1),
    ];

    static float Perlin(Vector3 p)
    {
        var f = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        var r = p - f;
        int x = (int)f.X, y = (int)f.Y, z = (int)f.Z;
        static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        float u = Fade(r.X), v = Fade(r.Y), w = Fade(r.Z);
        float G(int dx, int dy, int dz) => Vector3.Dot(Gradients[Hash(x + dx, y + dy, z + dz) % 12], r - new Vector3(dx, dy, dz));
        float x00 = float.Lerp(G(0, 0, 0), G(1, 0, 0), u), x10 = float.Lerp(G(0, 1, 0), G(1, 1, 0), u);
        float x01 = float.Lerp(G(0, 0, 1), G(1, 0, 1), u), x11 = float.Lerp(G(0, 1, 1), G(1, 1, 1), u);
        return float.Lerp(float.Lerp(x00, x10, v), float.Lerp(x01, x11, v), w);
    }

    /// <summary>Fractal noise, about 0..1 centred on 0.5.</summary>
    static float Fbm(Vector3 p, int octaves, int seed)
    {
        p += new Vector3(seed * 17.31f, seed * 5.77f, seed * 11.13f);
        float sum = 0, amp = 0.5f, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += amp * Perlin(p);
            norm += amp;
            p = p * 2.03f + new Vector3(3.1f, 1.7f, 5.3f);
            amp *= 0.5f;
        }
        return 0.5f + 0.9f * sum / norm;
    }

    /// <summary>The overall scale of <see cref="Radiance"/> (the band's brightest places reach about this × 2 in the starfield texture's units).</summary>
    public const float Scale = 0.07f;

    /// <summary>Stretches a direction along the galactic pole by <paramref name="k"/>, so noise sampled on it has features thinner across the band than along it (dust lanes run along the plane).</summary>
    static Vector3 Stretch(Vector3 c, float k) => c + Pole * (Vector3.Dot(c, Pole) * (k - 1));

    /// <summary>The share of the band's light that the per-pixel grain carries (the viewer adds it as a dense layer of faint stars, one per cell of <see cref="GrainCells"/> per cube face, present with chance <see cref="GrainChance"/>); the baked cube map keeps the rest as a smooth glow.</summary>
    public const float GrainShare = 0.5f, GrainChance = 0.85f, GrainSigma = 0.85f, GrainSpread = 0.6f;

    /// <summary>Cells per cube face edge of the grain's star layer when the screen is 1080p at 60 degrees vertical (0.088 degrees a cell at a face's centre, about 1.8 px at 60 degrees vertical); <see cref="GrainCellsFor"/> picks it for other views.</summary>
    public const int GrainCells = 1024;

    /// <summary>The grain cell's size in pixels the view aims for (at a face's centre, within a factor of 1.4 as the count is a power of two), and the range of the count (4K at 60 degrees takes 2048).</summary>
    public const float GrainCellPixels = 2.0f;
    public const int GrainCellsMin = 512, GrainCellsMax = 4096;

    /// <summary>The grain's cells per cube face edge for a view with <paramref name="pixelsPerRadian"/> (at the screen's centre): the power of two that makes a cell about <see cref="GrainCellPixels"/> pixels,
    /// so the grain is as fine as the screen (a fixed count would make 4K's grain a lattice of dots, 720p's a noise of single pixels). It changes only when a zoom crosses a power of two.</summary>
    public static int GrainCellsFor(float pixelsPerRadian)
    {
        float cells = 2 * MathF.Max(pixelsPerRadian, 1) / GrainCellPixels;   // a face's edge, 2 units, is 2 radians at its centre
        return Math.Clamp(1 << (int)MathF.Round(MathF.Log2(cells)), GrainCellsMin, GrainCellsMax);
    }

    /// <summary>How the per-pixel noise <c>h</c> (0..1) breaks up the baked dust's optical depth (<see cref="PixelTau"/>): the depth over <see cref="DustScale"/> gets a noise of amplitude
    /// <see cref="DustRagged"/> added and is steepened between <see cref="DustEdgeFrom"/> and <see cref="DustEdgeTo"/>; thin haze (below the rim) keeps <see cref="DustHaze"/> of its depth,
    /// a lane's core is <see cref="DustCore"/> times as dense.</summary>
    public const float DustScale = 1.2f, DustRagged = 0.3f, DustEdgeFrom = 0.3f, DustEdgeTo = 0.7f, DustHaze = 0.35f, DustCore = 2.4f;

    /// <summary>The dust's optical depth at a pixel from the baked (smooth) depth <paramref name="tau"/> and a per-pixel noise <paramref name="h"/> (0..1): crisp, ragged lane edges where the bake only has a soft ramp.</summary>
    public static float PixelTau(float tau, float h)
    {
        float s = tau / DustScale + DustRagged * (h - 0.5f);
        return tau * float.Lerp(DustHaze, DustCore, Smooth(DustEdgeFrom, DustEdgeTo, s));
    }

    /// <summary>How much of the air's reddening (<see cref="NightAtmosphere.Transmission"/>) the glow takes: the air dims the band as much as it dims the stars, but a faint cool-white
    /// glow turned fully orange-brown low in the sky, so only this share of the colour shift applies (<see cref="BandTransmission"/>).</summary>
    public const float ExtinctionColourShare = 0.35f;

    /// <summary>The air's transmission of the band's glow: the green channel's dimming for all three, plus <see cref="ExtinctionColourShare"/> of the stars' colour shift.</summary>
    public static Vector3 BandTransmission(Vector3 starTransmission) => Vector3.Lerp(new Vector3(starTransmission.Y), starTransmission, ExtinctionColourShare);

    /// <summary>The dust's transmission of the three channels for the optical depth <paramref name="tau"/>: neutral (a lane is dark, not brown), a hair less blue.</summary>
    public static Vector3 Transmit(float tau) => new(MathF.Exp(-tau), MathF.Exp(-tau), MathF.Exp(-tau * 1.04f));

    /// <summary>The Milky Way's glow (linear RGB, the starfield texture's units) before the dust, and the dust's optical depth, in the celestial direction <paramref name="c"/> (unit). This is what the cube map holds (the dust depth in its alpha).</summary>
    public static (Vector3 Light, float Tau) SmoothLight(Vector3 c)
    {
        var (b, l) = Galactic(c);
        float ellipse = MathF.Sqrt(l * l + b * b / 0.30f);   // the bulge is a flattened ellipse, twice as wide as it is high

        // The band wanders a little; its brightness varies along it.
        float wander = Fbm(c * 1.4f, 3, 1) - 0.5f, wiggle = Fbm(Stretch(c, 2f) * 4f, 4, 2) - 0.5f;
        float bw = b + 0.08f * wander + 0.03f * wiggle;
        float along = 0.30f + 0.70f * Gauss(l, 1.5f) + 0.25f * (Fbm(c * 2f, 3, 3) - 0.5f);
        float disc = Gauss(bw, 0.16f), core = Gauss(bw, 0.05f);
        float bulge = Gauss(ellipse, 0.42f), halo = Gauss(ellipse, 0.9f);

        // Structure of the haze down to what the cube map resolves (about 0.2 degrees a texel; the finest octave kept is 7 texels), a little flattened along
        // the band; the broad swells are weak, so it reads as mottling, not as clouds. The finer grain is the viewer's, per pixel (GrainShare).
        var sc = Stretch(c, 1.4f);
        float swell = Smooth(0.32f, 0.68f, Fbm(sc * 4f, 3, 4));
        float c2 = Fbm(sc * 16f, 3, 5), c3 = Fbm(sc * 45f, 2, 10);
        float grain = (0.55f + 0.9f * c2) * (0.45f + 1.1f * c3);
        float light = ((0.30f * disc + 0.55f * core) * along * (0.45f + 0.8f * swell) + 0.5f * bulge * (0.6f + 0.6f * swell) + 0.12f * halo * disc) * grain;

        // Dust: thin, dark rifts running along the plane a little off its middle (noise stretched across the band, so its features are long and thin along it,
        // warped), a thin rift through the bulge, a few tiny dark clouds. The optical depth is the smooth one; PixelTau sharpens it per pixel.
        var warp = new Vector3(Fbm(c * 3f, 3, 12) - 0.5f, Fbm(c * 3f, 3, 13) - 0.5f, Fbm(c * 3f, 3, 14) - 0.5f);
        var dc = Stretch(c, 5f);
        float ridge1 = 1 - MathF.Abs(2 * Fbm(dc * 7f + warp * 1.2f, 4, 6) - 1);
        float ridge2 = 1 - MathF.Abs(2 * Fbm(dc * 22f + warp * 2f, 3, 15) - 1);
        float lane = Smooth(0.68f, 1.0f, ridge1) * (0.6f + 0.6f * Smooth(0.4f, 0.95f, ridge2));
        float mask = Gauss(b - 0.012f - 0.03f * wander, 0.055f) * (0.5f + 0.9f * Gauss(l, 1.8f));
        float tau = 1.0f * lane * mask;
        tau += 0.9f * Gauss(b - 0.008f + 0.015f * wiggle, 0.02f) * Gauss(l, 1.0f) * Smooth(0.25f, 0.75f, Fbm(dc * 8f, 3, 9));
        tau += 0.4f * Smooth(0.72f, 0.86f, Fbm(Stretch(c, 2f) * 30f, 3, 8)) * Gauss(b, 0.12f);

        // Colour, low in saturation: cool white, a faint warm cream only in the bulge's core.
        float warm = Math.Clamp(0.6f * Gauss(ellipse, 0.32f), 0, 1);
        var tint = Vector3.Lerp(new Vector3(0.90f, 0.95f, 1.0f), new Vector3(1.0f, 0.97f, 0.91f), warm);
        light = MathF.Max(light, 0);
        light /= 1 + light / 2.2f;   // soft clip: the crowded peaks stay under 2.2
        return (tint * (light * Scale), tau);
    }

    /// <summary>The Milky Way's light (linear RGB, the starfield texture's units) in the celestial direction <paramref name="c"/> (unit): the smooth glow through the dust as baked (the viewer adds the grain and the dust's crisp edges per pixel).</summary>
    public static Vector3 Radiance(Vector3 c)
    {
        var (light, tau) = SmoothLight(c);
        return light * Transmit(tau);
    }

    // ---- the cube map ----

    /// <summary>The direction (unit) through the centre of texel (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="face"/> (Vulkan's order +X −X +Y −Y +Z −Z; rows top first) of a cube map of <paramref name="size"/>².</summary>
    public static Vector3 TexelDirection(int face, int x, int y, int size)
    {
        float sc = 2 * (x + 0.5f) / size - 1, tc = 2 * (y + 0.5f) / size - 1;
        var d = face switch
        {
            0 => new Vector3(1, -tc, -sc),
            1 => new Vector3(-1, -tc, sc),
            2 => new Vector3(sc, 1, tc),
            3 => new Vector3(sc, -1, -tc),
            4 => new Vector3(sc, -tc, 1),
            _ => new Vector3(-sc, -tc, -1),
        };
        return Vector3.Normalize(d);
    }

    /// <summary>The cube map's default face size (0.18° a texel at a face's centre).</summary>
    public const int DefaultFaceSize = 512;

    /// <summary>
    /// Bakes the cube map: <c>[level][face]</c> of RGBA half floats (the glow before the dust, and the dust's optical depth in alpha), levels down to 1×1 by box filtering. Takes about 0.1 s of a few cores at 512².
    /// </summary>
    public static Half[][][] Bake(int size = DefaultFaceSize)
    {
        int levels = 1 + (int)Math.Floor(Math.Log2(size));
        var current = new float[6][];
        for (int f = 0; f < 6; f++) current[f] = new float[size * size * 4];
        Parallel.For(0, 6 * size, i =>
        {
            int face = i / size, y = i % size;
            var data = current[face];
            for (int x = 0; x < size; x++)
            {
                var (v, tau) = SmoothLight(TexelDirection(face, x, y, size));
                int o = (y * size + x) * 4;
                data[o] = v.X; data[o + 1] = v.Y; data[o + 2] = v.Z; data[o + 3] = tau;
            }
        });
        var result = new Half[levels][][];
        for (int level = 0; level < levels; level++)
        {
            int s = Math.Max(size >> level, 1);
            result[level] = new Half[6][];
            for (int face = 0; face < 6; face++)
            {
                var src = current[face];
                var half = new Half[s * s * 4];
                for (int i = 0; i < half.Length; i++) half[i] = (Half)src[i];
                result[level][face] = half;
                if (s > 1)
                {
                    int n = s / 2;
                    var next = new float[n * n * 4];
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                            for (int ch = 0; ch < 4; ch++)
                                next[(y * n + x) * 4 + ch] = 0.25f * (src[((2 * y) * s + 2 * x) * 4 + ch] + src[((2 * y) * s + 2 * x + 1) * 4 + ch]
                                    + src[((2 * y + 1) * s + 2 * x) * 4 + ch] + src[((2 * y + 1) * s + 2 * x + 1) * 4 + ch]);
                    current[face] = next;
                }
            }
        }
        return result;
    }
}
