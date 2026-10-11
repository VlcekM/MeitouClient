using System.Numerics;

namespace Meitou.Data.World;

/// <summary>Meitou's procedural cloud volume; engine choices, not original-game constants.</summary>
public static class VolumetricClouds
{
    public const float Bottom = 12000, Top = 19000, MaxDistance = 500000;
    public const float PatternPeriod = 120000, ShapePeriod = 40000, DetailPeriod = 10000;
    public const float Extinction = 0.0012f, ShadowSpan = 320000;
    public const int NoiseSize = 64, TileSize = NoiseSize + 2, AtlasSize = TileSize * 8;

    /// <summary>Intersection with the horizontal cloud slab, including views inside or above it.</summary>
    public static bool Intersect(float eyeHeight, float rayY, out Vector2 interval)
    {
        interval = default;
        if (MathF.Abs(rayY) < 1e-6f)
        {
            if (eyeHeight <= Bottom || eyeHeight >= Top) return false;
            interval = new(0, MaxDistance);
            return true;
        }
        float a = (Bottom - eyeHeight) / rayY, b = (Top - eyeHeight) / rayY;
        float near = MathF.Max(MathF.Min(a, b), 0), far = MathF.Min(MathF.Max(a, b), MaxDistance);
        if (far <= near) return false;
        interval = new(near, far);
        return true;
    }

    static float Hash(int x, int y, int z, int period)
    {
        uint h = (uint)(((x % period + period) % period) * 73856093)
               ^ (uint)(((y % period + period) % period) * 19349663)
               ^ (uint)(((z % period + period) % period) * 83492791);
        h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        return (h & 0xffffff) / 16777215f;
    }

    /// <summary>Periodic smooth value noise, shared by all generated channels.</summary>
    public static float Noise(Vector3 p, int period)
    {
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        var f = p - new Vector3(x, y, z);
        f *= f * (new Vector3(3) - 2 * f);
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(Hash(x,y,z,period), Hash(x+1,y,z,period), f.X), L(Hash(x,y+1,z,period), Hash(x+1,y+1,z,period), f.X), f.Y),
                 L(L(Hash(x,y,z+1,period), Hash(x+1,y,z+1,period), f.X), L(Hash(x,y+1,z+1,period), Hash(x+1,y+1,z+1,period), f.X), f.Y), f.Z);
    }

    static float Worley(Vector3 p, int period)
    {
        var cell = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        float nearest = 4;
        for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
        {
            var c = cell + new Vector3(x, y, z);
            int cx = (int)c.X, cy = (int)c.Y, cz = (int)c.Z;
            var feature = c + new Vector3(Hash(cx,cy,cz,period), Hash(cx+17,cy+11,cz+7,period), Hash(cx+5,cy+23,cz+13,period));
            nearest = MathF.Min(nearest, Vector3.DistanceSquared(p, feature));
        }
        return 1 - Math.Clamp(MathF.Sqrt(nearest), 0, 1);
    }

    /// <summary>A padded 8x8 slice atlas. R: four-octave shape noise; G: cellular erosion; B: broad weather noise.</summary>
    public static byte[] CreateAtlas()
    {
        var volume = new byte[NoiseSize * NoiseSize * NoiseSize * 4];
        Parallel.For(0, NoiseSize, z =>
        {
            for (int y = 0; y < NoiseSize; y++) for (int x = 0; x < NoiseSize; x++)
            {
                var p = new Vector3(x, y, z) / NoiseSize;
                float shape = 0.55f * Noise(p * 4, 4) + 0.25f * Noise(p * 8, 8) + 0.13f * Noise(p * 16, 16) + 0.07f * Noise(p * 32, 32);
                int i = ((z * NoiseSize + y) * NoiseSize + x) * 4;
                volume[i] = (byte)(Math.Clamp(shape, 0, 1) * 255);
                volume[i+1] = (byte)(Worley(p * 8, 8) * 255);
                volume[i+2] = (byte)(Noise(p * 4, 4) * 255);
                volume[i+3] = 255;
            }
        });
        var atlas = new byte[AtlasSize * AtlasSize * 4];
        for (int z = 0; z < NoiseSize; z++) for (int y = 0; y < TileSize; y++) for (int x = 0; x < TileSize; x++)
        {
            int sx = (x - 1 + NoiseSize) % NoiseSize, sy = (y - 1 + NoiseSize) % NoiseSize;
            int src = ((z * NoiseSize + sy) * NoiseSize + sx) * 4;
            int dst = (((z / 8 * TileSize + y) * AtlasSize) + z % 8 * TileSize + x) * 4;
            volume.AsSpan(src, 4).CopyTo(atlas.AsSpan(dst, 4));
        }
        return atlas;
    }
}