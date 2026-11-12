using System.Numerics;

/// <summary>
/// A tiny CPU rasteriser for debug images: triangles seen from above (+Y), the highest surface wins, shaded by slope.
/// </summary>
sealed class TopDownRenderer
{
    /// <summary>
    /// The image's world origin (the lowest x and z) and its scale, world units per pixel.
    /// </summary>
    readonly float _minX, _minZ, _unitsPerPixel;

    /// <summary>
    /// The height of the surface drawn at each pixel (the highest wins); minus infinity where nothing is drawn yet.
    /// </summary>
    readonly float[] _depth;

    /// <summary>
    /// The image, three bytes per pixel, row by row.
    /// </summary>
    readonly byte[] _rgb;

    /// <summary>
    /// The image's width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// The image's height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// The image's pixels (RGB, row by row).
    /// </summary>
    public byte[] Rgb => _rgb;

    /// <summary>
    /// An image covering the world rectangle (<paramref name="minX"/>, <paramref name="minZ"/>) to (<paramref name="maxX"/>, <paramref name="maxZ"/>)
    /// at <paramref name="unitsPerPixel"/>, filled with <paramref name="background"/>.
    /// </summary>
    public TopDownRenderer(float minX, float minZ, float maxX, float maxZ, float unitsPerPixel, (byte R, byte G, byte B) background)
    {
        this._minX = minX;
        this._minZ = minZ;
        this._unitsPerPixel = unitsPerPixel;
        Width = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / unitsPerPixel));
        Height = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / unitsPerPixel));
        _depth = new float[Width * Height];
        Array.Fill(_depth, float.NegativeInfinity);
        _rgb = new byte[Width * Height * 3];
        for (int i = 0; i < Width * Height; i++)
        {
            _rgb[i * 3] = background.R;
            _rgb[i * 3 + 1] = background.G;
            _rgb[i * 3 + 2] = background.B;
        }
    }

    /// <summary>
    /// Draws a triangle (world positions); pixels where it is the highest surface so far take <paramref name="colour"/> shaded by its slope.
    /// </summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c, (byte R, byte G, byte B) colour, float biasUp = 0)
    {
        var n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        float shade = len > 1e-9f ? 0.55f + 0.45f * Math.Clamp(Math.Abs(n.Y) / len, 0, 1) : 1;
        float x0 = Math.Min(a.X, Math.Min(b.X, c.X)), x1 = Math.Max(a.X, Math.Max(b.X, c.X));
        float z0 = Math.Min(a.Z, Math.Min(b.Z, c.Z)), z1 = Math.Max(a.Z, Math.Max(b.Z, c.Z));
        int px0 = Math.Max(0, (int)MathF.Floor((x0 - _minX) / _unitsPerPixel)), px1 = Math.Min(Width - 1,
                (int)MathF.Floor((x1 - _minX) / _unitsPerPixel));
        int pz0 = Math.Max(0, (int)MathF.Floor((z0 - _minZ) / _unitsPerPixel)), pz1 = Math.Min(Height - 1,
                (int)MathF.Floor((z1 - _minZ) / _unitsPerPixel));
        float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (Math.Abs(det) < 1e-12f) return;
        for (int pz = pz0; pz <= pz1; pz++)
        {
            for (int px = px0; px <= px1; px++)
            {
                float x = _minX + (px + 0.5f) * _unitsPerPixel, z = _minZ + (pz + 0.5f) * _unitsPerPixel;
                float l0 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / det;
                float l1 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / det;
                float l2 = 1 - l0 - l1;
                const float e = -1e-4f;
                if (l0 < e || l1 < e || l2 < e) continue;
                float y = l0 * a.Y + l1 * b.Y + l2 * c.Y + biasUp;
                int i = pz * Width + px;
                if (y < _depth[i]) continue;
                _depth[i] = y;
                _rgb[i * 3] = (byte)(colour.R * shade);
                _rgb[i * 3 + 1] = (byte)(colour.G * shade);
                _rgb[i * 3 + 2] = (byte)(colour.B * shade);
            }
        }
    }

    /// <summary>
    /// Paints a square of <paramref name="radius"/> pixels round the world point (<paramref name="x"/>, <paramref name="z"/>) over everything (a
    /// marker).
    /// </summary>
    public void Pixel(float x, float z, (byte R, byte G, byte B) colour, int radius = 1)
    {
        int cx = (int)((x - _minX) / _unitsPerPixel), cz = (int)((z - _minZ) / _unitsPerPixel);
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = cx + dx, pz = cz + dz;
                if (px < 0 || pz < 0 || px >= Width || pz >= Height) continue;
                int i = (pz * Width + px) * 3;
                _rgb[i] = colour.R;
                _rgb[i + 1] = colour.G;
                _rgb[i + 2] = colour.B;
            }
        }
    }

    /// <summary>
    /// A line on the image (for polygon edges); no depth test.
    /// </summary>
    public void Line(Vector3 a, Vector3 b, (byte R, byte G, byte B) colour)
    {
        float length = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Z - a.Z)) / _unitsPerPixel;
        int steps = Math.Max(1, (int)length);
        for (int s = 0; s <= steps; s++)
        {
            var p = Vector3.Lerp(a, b, s / (float)steps);
            Pixel(p.X, p.Z, colour, 0);
        }
    }
}
