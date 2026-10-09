using System.Globalization;

namespace Meitou.Rendering;

/// <summary>
/// Coarser casters in the far shadow cascades (the <c>shadow-coarse</c> A/B switch; docs/render-shadows.md "Coarse casters in the far cascades"). A far
/// cascade's texel is many world units (about 4, 9 and 20 on the swamp), so the casters' fine detail is lost in it. Each caster type picks its
/// level by the cascade's texel: objects take the mesh level the game would pick at the distance where a screen pixel is as large as
/// <see cref="ObjectTexels"/> texels, terrain nodes are no finer than the level whose grid error is <see cref="TerrainTexels"/> texels, and the
/// foliage's generated levels allow <see cref="RockTexels"/> texels (the main rule, <c>FoliageRenderer.LodShadowTolerance</c>, otherwise).
/// Cascade 0 (the near one, the texel of the picture) is left as it was. Pure arithmetic, no GPU.
/// </summary>
public static class ShadowLod
{
    /// <summary>The switch: on by default (<c>MEITOU_SHADOW_COARSE=0</c> starts it off); off draws every cascade's casters as before.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("MEITOU_SHADOW_COARSE") != "0";

    /// <summary>The first cascade the coarser casters apply to (the nearest, index 0, keeps the picture's detail).</summary>
    public const int FirstCascade = 1;

    /// <summary>Objects take the mesh level the game would pick at the distance where one pixel of the render spans this many texels; <c>MEITOU_SHADOW_OBJECT_TEXELS</c>.</summary>
    public static float ObjectTexels { get; set; } = EnvZero("MEITOU_SHADOW_OBJECT_TEXELS", 1f);

    /// <summary>The height error (texels) a terrain level's grid may have, <see cref="Meitou.Data.World.TerrainLod.Roughness"/> x its spacing; <c>MEITOU_SHADOW_TERRAIN_TEXELS</c>.</summary>
    public static float TerrainTexels { get; set; } = EnvZero("MEITOU_SHADOW_TERRAIN_TEXELS", 1f);

    /// <summary>The deviation (texels) a generated foliage level may have in a coarsened cascade, when that is more than the main rule's; <c>MEITOU_SHADOW_ROCK_TEXELS</c>.</summary>
    public static float RockTexels { get; set; } = EnvZero("MEITOU_SHADOW_ROCK_TEXELS", 4f);

    /// <summary>A foliage mesh with a resident impostor atlas casts the impostor (a flat, sun-facing cut-out) in a coarsened cascade when its world radius is at most this many texels:
    /// the atlas's sun direction is quantised (12 x 12 frames, about 7 degrees), which moves the shadow by about 0.12 radius; <c>MEITOU_SHADOW_IMPOSTOR_TEXELS</c>, 0 never.</summary>
    public static float ImpostorTexels { get; set; } = EnvZero("MEITOU_SHADOW_IMPOSTOR_TEXELS", 8f);

    /// <summary>The ground distance from the eye from which such an instance casts the impostor (the mesh nearer than that).</summary>
    public const float ImpostorFrom = 200;

    /// <summary>Whether cascade <paramref name="index"/> draws coarser casters.</summary>
    public static bool Applies(int index) => Enabled && index >= FirstCascade;

    /// <summary>
    /// The least mesh-LOD value (the game's distance, <see cref="Meitou.Data.Ogre.MeshLod.Select(ReadOnlySpan{float}, float)"/>) an object takes in a cascade of the given
    /// texel: the distance at which one pixel of the render (<paramref name="pixelScale"/> pixels per unit at distance 1) is <paramref name="texels"/> (default
    /// <see cref="ObjectTexels"/>) texels wide. 0 when the cascade is not coarsened (texel 0) or the rule is off.
    /// </summary>
    public static float ObjectLodFloor(float texel, float pixelScale, float? texels = null)
    {
        float t = texels ?? ObjectTexels;
        return texel > 0 && pixelScale > 0 && t > 0 ? texel * t * pixelScale : 0;
    }

    /// <summary>
    /// The finest quadtree level a terrain node is drawn at in a cascade of the given texel: the highest level <c>l</c> whose grid error (<paramref name="roughness"/> x the
    /// spacing of level l, <paramref name="spacing0"/> doubling per level) stays within <paramref name="texels"/> (default <see cref="TerrainTexels"/>) texels; 0 (the distance
    /// rule's) when the cascade is not coarsened (texel 0) or no level above the finest fits, at most <paramref name="levels"/> - 2 (the root is the distance rule's).
    /// </summary>
    public static int TerrainFloorLevel(float texel, double spacing0, double roughness, int levels, float? texels = null)
    {
        if (!(texel > 0) || !(spacing0 > 0)) return 0;
        double allowed = texel * (texels ?? TerrainTexels);
        int level = 0;
        while (level + 1 <= levels - 2 && roughness * spacing0 * (1L << (level + 1)) <= allowed) level++;
        return level;
    }

    /// <summary>The ground distance from which a mesh of <paramref name="worldRadius"/> casts its impostor into a coarsened cascade of the given texel: <see cref="ImpostorFrom"/> when it is at most <paramref name="texels"/> (default <see cref="ImpostorTexels"/>) texels across, else never.</summary>
    public static float ImpostorTransition(float worldRadius, float texel, float? texels = null)
    {
        float t = texels ?? ImpostorTexels;
        return t > 0 && texel > 0 && worldRadius <= t * texel ? ImpostorFrom : float.PositiveInfinity;
    }

    /// <summary>The tolerance (texels) of a generated foliage level in a cascade: <paramref name="main"/> (the main rule), or <paramref name="texels"/> (default <see cref="RockTexels"/>) where coarsened if that is more.</summary>
    public static float RockTolerance(float main, bool coarse, float? texels = null) => coarse ? Math.Max(main, texels ?? RockTexels) : main;

    static float EnvZero(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v >= 0 ? v : fallback;
}
