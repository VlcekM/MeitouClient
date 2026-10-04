using System.Numerics;

namespace Meitou.Data.World;

/// <summary>One placed foliage mesh: where, how big and turned, from which layer (docs/formats/foliage.md, "Placing").</summary>
public readonly record struct FoliageInstance(FoliageMesh Mesh, FoliageLayer Layer, Vector3 Position, float Scale, float YawDegrees, Quaternion Orientation)
{
    public Matrix4x4 Transform => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Orientation) * Matrix4x4.CreateTranslation(Position);
}

/// <summary>A grass layer of a zone: its grass types and the zone's 129² coverage map (0..255, row = +Z) they grow by.</summary>
public sealed record FoliageGrassPatch(FoliageLayer Layer, FoliageGrass Grass, int Channel, byte[] Density, float X0, float Z0, float X1, float Z1);

/// <summary>Everything the foliage system puts into one zone.</summary>
public sealed class FoliageZone
{
    public required ZoneCoordinate Zone { get; init; }
    public List<FoliageInstance> Instances { get; } = [];
    public List<FoliageGrassPatch> Grass { get; } = [];
    /// <summary>Instances of meshes with a <c>building type</c> (mineable rocks), also in <see cref="Instances"/>.</summary>
    public int Resources { get; set; }
}

/// <summary>What the placer needs to know about a zone besides its records.</summary>
public sealed class FoliageZoneInput
{
    public required ZoneCoordinate Zone { get; init; }
    public required FoliageGround Ground { get; init; }
    /// <summary>The overlay around the zone; the placer writes grass coverage (R) and grass spots (G) into it.</summary>
    public required FoliageOverlay Overlay { get; init; }
    /// <summary>The zone's biomes (index colours), in blend-info slot order.</summary>
    public required IReadOnlyList<uint> Biomes { get; init; }
    /// <summary>Biome index colour at a world point (biomemap.png).</summary>
    public required Func<float, float, uint> BiomeAt { get; init; }
    /// <summary>The nearest town (not a nest marker) to a point: X/Z and its <c>no-foliage range</c>; null when there is none.</summary>
    public Func<float, float, (Vector2 At, float Range)?>? NearestTown { get; init; }
    /// <summary>Water surface height for <c>floating</c> meshes.</summary>
    public float WaterHeight { get; init; } = WorldWater.Height;
}

/// <summary>
/// Kenshi's foliage placement for one zone (docs/formats/foliage.md), reproduced rule by rule: per biome of the zone, per
/// FOLIAGE_LAYER of the biome, grass layers first, the layer's own Mersenne Twister seeded from the layer id and the
/// zone's corner, then per mesh as many attempts as the layer gives it, clustered or uniform, each tested for slope,
/// zone bounds, towns, height, roads, and placed with its scale, vertical offset, yaw and child meshes.
/// </summary>
public static class FoliageLayout
{
    /// <summary>The game's TreeLoader3D scale range (the bounds of its 8-bit scale).</summary>
    public const float MinScale = 0.01f, MaxScale = 10f;

    /// <summary>Base distances per FoliageVisibilityRange before × 10 and the range setting.</summary>
    static readonly float[] Ranges = [50, 100, 800, 4000];

    /// <summary>{50, 100, 800, 4000}[v] × 10 × <paramref name="setting"/> (the settings.cfg <c>foliage range</c> / <c>grass range</c>).</summary>
    public static float VisibilityRange(FoliageVisibility v, float setting = 1) => Ranges[(int)v] * 10 * setting;

    /// <summary>Overlay R + G above which a point counts as a grass area (<c>limit to grass areas</c>).</summary>
    public const int GrassAreaThreshold = 149;

    public static FoliageZone Place(FoliageZoneInput input, FoliageCatalog catalog)
    {
        var zone = new FoliageZone { Zone = input.Zone };
        var overlay = input.Overlay;
        float x0 = input.Ground.X0, z0 = input.Ground.Z0, x1 = x0 + WorldLayout.ZoneSize, z1 = z0 + WorldLayout.ZoneSize;
        bool several = input.Biomes.Count >= 2;

        // Grass coverage: R is regenerated from every grass type of every biome in the zone (the larger value wins).
        overlay.ClearGrass();
        foreach (uint biome in input.Biomes)
            foreach (var layer in catalog.LayersOf(biome))
                foreach (var (grass, _) in layer.Grass)
                    Coverage(input, grass, several ? biome : null);

        // Layers in biome order, grass layers first (each picks the first remaining grass layer, then the first remaining).
        var work = new List<(uint Biome, FoliageLayer Layer)>();
        foreach (uint biome in input.Biomes)
            foreach (var layer in catalog.LayersOf(biome)) work.Add((biome, layer));
        work = [.. work.Where(w => w.Layer.IsGrass), .. work.Where(w => !w.Layer.IsGrass)];

        var rng = new FoliageRandom(0);
        foreach (var (biome, layer) in work)
        {
            uint? filter = several ? biome : null;
            if (layer.IsGrass)
            {
                foreach (var (grass, channel) in layer.Grass) zone.Grass.Add(GrassPatch(input, layer, grass, channel, filter));
                continue;
            }
            if (layer.Meshes.Count == 0) continue;
            rng.Seed(FoliageRandom.LayerSeed(layer.StringId, x0, z0));
            var ctx = new Context(input, zone, layer, rng, x0, z0, x1, z1);
            foreach (var (mesh, count) in layer.Meshes) PlaceMesh(ctx, mesh, count, filter);
        }
        return zone;
    }

    sealed record Context(FoliageZoneInput Input, FoliageZone Zone, FoliageLayer Layer, FoliageRandom Rng, float X0, float Z0, float X1, float Z1);

    static void PlaceMesh(Context c, FoliageMesh mesh, int count, uint? biome)
    {
        var rng = c.Rng;
        int left = 0;
        float radius = 0;
        Vector2 centre = default;
        for (int n = 0; n < count; n++)
        {
            left--;
            Vector2 p;
            if (!mesh.Clustered || left < 1)
            {
                if (mesh.Clustered)
                {
                    left = (int)rng.IntUpTo((uint)(mesh.ClusterNumMax - mesh.ClusterNumMin)) + mesh.ClusterNumMin;
                    radius = rng.Float(mesh.ClusterRadiusMin, mesh.ClusterRadiusMax);
                }
                p = new Vector2(rng.Float(c.X0, c.X1), 0);
                p.Y = rng.Float(c.Z0, c.Z1);
                centre = p;
                bool give = false;
                for (int tries = 0; mesh.LimitToGrassAreas && c.Input.Overlay.GrassAt(p.X, p.Y) <= GrassAreaThreshold; tries++)
                {
                    if (tries > 20) { give = true; break; }
                    p = new Vector2(rng.Float(c.X0, c.X1), 0);
                    p.Y = rng.Float(c.Z0, c.Z1);
                    centre = p;
                }
                if (give) { left = 0; continue; }
            }
            else
            {
                float a = rng.Float(-1, 1), b = rng.Float(-1, 1);
                var dir = Normalise(new Vector2(b, a));
                p = centre + dir * rng.Float(5, radius);
            }
            if (biome is { } want && c.Input.BiomeAt(p.X, p.Y) != want)
            {
                if (left > 0) n += left;
                left = 0;
                continue;
            }
            if (!Place(c, mesh, p)) left = 0;
        }
    }

    /// <summary>Ogre's Vector2::normalise: unchanged when the length is 0.</summary>
    static Vector2 Normalise(Vector2 v)
    {
        float l = v.Length();
        return l > 1e-8f ? v / l : v;
    }

    /// <summary>One attempt at a point; false when the slope or height rules it out (which ends a cluster).</summary>
    static bool Place(Context c, FoliageMesh mesh, Vector2 p)
    {
        var input = c.Input;
        var rng = c.Rng;
        float slope = input.Ground.Slope(p.X, p.Y);
        if (slope > mesh.MaxSlope || slope < mesh.MinSlope) return false;
        if (p.X < c.X0 || p.X > c.X1 || p.Y < c.Z0 || p.Y > c.Z1) return true;
        bool rejected = false;
        if (mesh.AvoidTowns && input.NearestTown?.Invoke(p.X, p.Y) is { } town &&
            Vector2.DistanceSquared(town.At, p) < town.Range * town.Range)
            rejected = true;
        float y = input.Ground.Height(p.X, p.Y);
        if (y > mesh.MaxAltitude || y < mesh.MinAltitude) return false;
        if (mesh.Floating) y = Math.Max(y, input.WaterHeight);
        float scale = rng.Float(mesh.MinScale, mesh.MaxScale);
        float offset = mesh.VerticalOffsetMin;
        if (mesh.VerticalOffsetMin != mesh.VerticalOffsetMax) offset = rng.Float(mesh.VerticalOffsetMin, mesh.VerticalOffsetMax);
        y += offset * scale;
        if (mesh.RoadAvoidance > 0 && NearRoad(input.Overlay, p, mesh.RoadAvoidance * scale)) rejected = true;
        float yaw = rng.Float(0, 360);
        if (!rejected)
        {
            float qScale = Quantise(scale, MinScale, MaxScale);
            float qYaw = (byte)(long)(yaw / 360 * 255) * (360f / 255);
            var position = new Vector3(p.X, y, p.Y);
            c.Zone.Instances.Add(new FoliageInstance(mesh, c.Layer, position, qScale, qYaw, Orientation(mesh, input.Ground, position, qYaw)));
            if (mesh.BuildingType is not null) c.Zone.Resources++;
        }
        foreach (var child in mesh.Children)
        {
            int n = (int)rng.IntUpTo((uint)(child.ClusterNumMax - child.ClusterNumMin)) + child.ClusterNumMin;
            float r = rng.Float(child.ClusterRadiusMin, child.ClusterRadiusMax);
            for (int k = 0; k < n; k++)
            {
                float a = rng.Float(-1, 1), b = rng.Float(-1, 1);
                var dir = Normalise(new Vector2(b, a));
                Place(c, child, p + dir * (rng.Float(0, r) + child.ChildClusterRadius + mesh.ChildClusterRadius));
            }
        }
        if (!rejected && mesh.GrassSpot > 0) GrassSpot(input.Overlay, p, mesh.GrassSpot * scale);
        return true;
    }

    /// <summary>The 8-bit scale of PagedGeometry's TreeLoader3D: <c>min + trunc((s − min) / max × 255) / 255 × max</c>.</summary>
    static float Quantise(float s, float min, float max) => max / 255 * (byte)(long)((s - min) / max * 255) + min;

    /// <summary>
    /// A tree's orientation when its page is built: yaw about +Y; <c>slope align</c> turns its up axis onto the terrain normal;
    /// otherwise, unless <c>keep upright</c>, a random axis and angle from the C runtime's <c>rand()</c> seeded by the position.
    /// </summary>
    public static Quaternion Orientation(FoliageMesh mesh, FoliageGround ground, Vector3 position, float yawDegrees)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDegrees * MathF.PI / 180);
        if (mesh.SlopeAlign)
        {
            var up = Vector3.Transform(Vector3.UnitY, q);
            var n = ground.Normal(position.X, position.Z);
            return RotationTo(up, n) * q;
        }
        if (mesh.KeepUpright) return q;
        float noise = (float)FoliageNoise.Value((int)position.X, (int)position.Z);
        var rand = new BuildingRandom((uint)(long)(noise * 1000000f));
        int r1 = rand.Next(), r2 = rand.Next(), r3 = rand.Next();
        var axis = Vector3.Normalize(new Vector3(r3, r2, r1) + new Vector3(1e-9f));
        return Quaternion.CreateFromAxisAngle(axis, noise * 360 * MathF.PI / 180);
    }

    /// <summary>The shortest rotation taking direction <paramref name="from"/> onto <paramref name="to"/> (Ogre's getRotationTo).</summary>
    static Quaternion RotationTo(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        float d = Vector3.Dot(from, to);
        if (d >= 1 - 1e-6f) return Quaternion.Identity;
        if (d < 1e-6f - 1) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        float s = MathF.Sqrt((1 + d) * 2);
        var axis = Vector3.Cross(from, to) / s;
        return Quaternion.Normalize(new Quaternion(axis, s * 0.5f));
    }

    /// <summary>The game's road test: any road weight on the box's first row, its centre pixel, last row or side columns.</summary>
    static bool NearRoad(FoliageOverlay o, Vector2 p, float r)
    {
        int ix0 = o.PixelX(p.X - r), iz0 = o.PixelZ(p.Y - r), ix1 = o.PixelX(p.X + r), iz1 = o.PixelZ(p.Y + r);
        for (int i = ix0; i <= ix1; i++) if (o.Road(i, iz0) != 0) return true;
        if (ix1 == ix0 && iz0 == iz1) return false;
        if (o.Road((ix0 + ix1) / 2, (iz0 + iz1) / 2) != 0) return true;
        if (iz0 < iz1)
        {
            for (int i = ix0; i <= ix1; i++) if (o.Road(i, iz1) != 0) return true;
            for (int j = iz0 + 1; j < iz1; j++) if (o.Road(ix0, j) != 0 || o.Road(ix1, j) != 0) return true;
        }
        return false;
    }

    /// <summary>A mesh's <c>grass spot</c>: a disc of grass coverage (G) of radius <c>grass spot × scale</c> around it.</summary>
    static void GrassSpot(FoliageOverlay o, Vector2 p, float r)
    {
        int ix0 = o.PixelX(p.X - r), iz0 = o.PixelZ(p.Y - r), ix1 = o.PixelX(p.X + r), iz1 = o.PixelZ(p.Y + r);
        bool several = ix0 != ix1 || iz0 != iz1;
        float strength = Math.Clamp(r * 2 / FoliageOverlay.PixelSize, 0.3f, 1f);
        for (int i = ix0; i <= ix1; i++)
            for (int j = iz0; j <= iz1; j++)
            {
                if (i <= 0 || j <= 0 || i >= FoliageOverlay.TilePixels - 1 || j >= FoliageOverlay.TilePixels - 1) continue;
                float v = strength;
                if (several)
                {
                    var (cx, cz) = o.PixelCentre(i, j);
                    float d2 = Vector2.DistanceSquared(new Vector2(cx, cz), p);
                    v *= 1 - d2 / (r * r) * 0.3f;
                }
                o.Set(1, i, j, (byte)(long)(Math.Max(v, 0) * 96));
            }
    }

    /// <summary>
    /// Grass coverage of one GRASS type over the zone's 129² overlay pixels (R, the larger value kept): its noise (and blackout
    /// noise multiplied in) × 255, faded over 300 units outside its altitude range, 0 on dirt (B above 70), roads (A above 10)
    /// left untouched. With several biomes only the pixels of <paramref name="biome"/>.
    /// </summary>
    static void Coverage(FoliageZoneInput input, FoliageGrass grass, uint? biome)
    {
        var o = input.Overlay;
        double scale = grass.NoiseScale * (FoliageOverlay.PixelSize * 2), blackoutScale = grass.BlackoutNoiseScale * (FoliageOverlay.PixelSize * 2);
        for (int i = o.ZonePixelX; i < o.ZonePixelX + FoliageGround.Size; i++)
            for (int j = o.ZonePixelZ; j < o.ZonePixelZ + FoliageGround.Size; j++)
            {
                var (x, z) = o.PixelCentre(i, j);
                if (biome is { } want && input.BiomeAt(x, z) != want) continue;
                float h = input.Ground.Height(x, z);
                float fade = 1;
                if (h < grass.MinAltitude) fade = (h - (grass.MinAltitude - 300)) / 300;
                else if (h > grass.MaxAltitude) fade = (grass.MaxAltitude + 300 - h) / 300;
                fade = Math.Clamp(fade, 0, 1);
                float dirt = o.Get(2, i, j) > 70 ? 0 : 255;
                float n = FoliageNoise.Coverage(x, z, scale, grass.ZeroCutoff, grass.BrightnessBoost, grass.Cap);
                if (grass.Blackout) n *= FoliageNoise.Coverage(x + 50007f, z + 50007f, blackoutScale, grass.BlackoutZeroCutoff, grass.BrightnessBoost, grass.Cap);
                if (o.Get(3, i, j) > 10) continue;
                byte v = (byte)(long)(n * dirt * fade);
                if (o.Get(0, i, j) < v) o.Set(0, i, j, v);
            }
    }

    /// <summary>The density map of a grass layer: the zone's 129² of R (channel 0) or G (other channels).</summary>
    static FoliageGrassPatch GrassPatch(FoliageZoneInput input, FoliageLayer layer, FoliageGrass grass, int channel, uint? biome)
    {
        var o = input.Overlay;
        int c = channel == 0 ? 0 : 1;
        var density = new byte[FoliageGround.Size * FoliageGround.Size];
        for (int j = 0; j < FoliageGround.Size; j++)
            for (int i = 0; i < FoliageGround.Size; i++)
            {
                int pi = o.ZonePixelX + i, pj = o.ZonePixelZ + j;
                if (biome is { } want)
                {
                    var (x, z) = o.PixelCentre(pi, pj);
                    if (input.BiomeAt(x, z) != want) continue;
                }
                density[j * FoliageGround.Size + i] = o.Get(c, pi, pj);
            }
        float x0 = input.Ground.X0, z0 = input.Ground.Z0;
        return new FoliageGrassPatch(layer, grass, channel, density, x0, z0, x0 + WorldLayout.ZoneSize, z0 + WorldLayout.ZoneSize);
    }
}
