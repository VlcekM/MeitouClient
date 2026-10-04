namespace Meitou.Data.World;

/// <summary>
/// How far placed objects are drawn (docs/formats/zones.md, "Draw distance and distant towns"): the game's
/// <c>settings.cfg</c> values and the rules built on them.
/// </summary>
public static class ObjectRanges
{
    /// <summary><c>objects view range</c> (settings.cfg, default 3000; a value of 1000 or less is replaced by 3000).</summary>
    public const float ObjectsViewRange = 3000;

    /// <summary>A part whose mesh's local bounding radius is above this and still has the plain range is drawn at any distance.</summary>
    public const float BigObjectRadius = 100;

    /// <summary><c>feature range</c> (settings.cfg, default 2; 0..6): zones around the camera that map features are loaded for.</summary>
    public const int FeatureRangeZones = 2;

    /// <summary><c>distant town range</c> (settings.cfg, default 6; 0..10): zones around the camera that distant towns are shown for.</summary>
    public const int DistantTownRangeZones = 6;

    /// <summary>The upper clamp of <see cref="DistantTownRangeZones"/>.</summary>
    public const int MaxDistantTownRangeZones = 10;

    /// <summary>BuildingFunction.BF_GENERATOR: parts of such buildings get no rendering distance (Observed: the virtual that returns it is not named).</summary>
    public const int GeneratorFunction = 5;

    /// <summary>BuildingFunction.BF_TURRET: its parts' distance is half the objects view range (Observed, as above).</summary>
    public const int TurretFunction = 12;

    /// <summary>
    /// The distance beyond which a building part is not drawn: the objects view range (half of it for turrets), unlimited
    /// for a part with a big mesh or for generators. The distance is <see cref="Ogre.MeshLod.Value"/>-like: camera to bounds
    /// centre minus the bounds' radius (Observed; Ogre's rule in the game's version is not decompiled).
    /// </summary>
    public static float PartRenderingDistance(float localRadius, int buildingFunction, float objectsViewRange = ObjectsViewRange)
    {
        if (buildingFunction == GeneratorFunction) return float.MaxValue;
        if (buildingFunction == TurretFunction) return objectsViewRange * 0.5f;
        return localRadius > BigObjectRadius ? float.MaxValue : objectsViewRange;
    }

    /// <summary>
    /// The viewer's soft edge for a distance cut: 1 up to <c>limit - band</c>, falling (smoothstep) to 0 at <paramref name="limit"/>,
    /// so nothing pops. A limit of <see cref="float.MaxValue"/> never fades.
    /// </summary>
    public static float EdgeWeight(float value, float limit, float band)
    {
        if (limit >= float.MaxValue / 2 || value <= limit - band) return 1;
        if (value >= limit) return 0;
        float x = (limit - value) / Math.Max(band, 1e-3f);
        return x * x * (3 - 2 * x);
    }

    /// <summary>The rising counterpart: 0 up to <paramref name="start"/>, 1 from <c>start + band</c>.</summary>
    public static float RiseWeight(float value, float start, float band)
    {
        if (value <= start) return 0;
        if (value >= start + band) return 1;
        float x = (value - start) / Math.Max(band, 1e-3f);
        return x * x * (3 - 2 * x);
    }
}
