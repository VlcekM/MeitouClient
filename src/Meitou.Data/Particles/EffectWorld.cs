using System.Numerics;

namespace Meitou.Data.Particles;

/// <summary>
/// What the groups need of the world besides the camera: the ground height (called on the main thread only: the renderer's height grid swaps as the
/// terrain streams), the area effects are placed in, and how far from the camera effects are simulated.
/// </summary>
public sealed class EffectWorld
{
    /// <summary>
    /// The ground height at (x, z); 100 everywhere by default.
    /// </summary>
    public Func<float, float, float> GroundHeight { get; set; } = static (_, _) => 100;
    /// <summary>
    /// Null: <see cref="GroundHeight"/> may be called from any thread. Otherwise it is for the render thread only and this, called there, returns a
    /// function over the heights as they are now that any thread may call (the viewer: a snapshot of the terrain's height grids).
    /// </summary>
    public Func<Func<float, float, float>>? FreezeGroundHeight { get; set; }
    /// <summary>
    /// A copy for a worker thread (a group being warmed in the background, <see cref="EffectSet"/>): the ground height frozen as it is now
    /// (<see cref="FreezeGroundHeight"/>), the rest as it is (<see cref="GroundColour"/> too, so it must be callable from any thread when set; the
    /// viewer leaves it unset). Call it on the render thread.
    /// </summary>
    public EffectWorld Frozen() => new()
    {
        GroundHeight = FreezeGroundHeight?.Invoke() ?? GroundHeight,
        Area = Area,
        ActiveRadius = ActiveRadius,
        FallbackRadius = FallbackRadius,
        GroundColour = GroundColour,
        Density = Density,
    };
    /// <summary>
    /// Null: a disc round the camera of <see cref="FallbackRadius"/>.
    /// </summary>
    public IEffectArea? Area { get; set; }
    /// <summary>
    /// How far from the camera effects exist for us (Observed: the game simulates the active zones, 3 × 3 of 4608 units, and an effect in an inactive
    /// zone only within its <c>maximum view distance</c> of them): an effect is simulated while the camera is within this plus its view distance of
    /// its bounds.
    /// </summary>
    public float ActiveRadius { get; set; } = 6912;
    /// <summary>
    /// The radius of the disc round the camera effects are placed in when <see cref="Area"/> is null.
    /// </summary>
    public float FallbackRadius { get; set; } = 8000;
    /// <summary>
    /// The biome's ground colour at a place (for <c>ground colour</c> effects); null: not used.
    /// </summary>
    public Func<float, float, Vector3>? GroundColour { get; set; }
    /// <summary>
    /// The Tab panel's "Particle density x" (Meitou setting, not in the game): multiplies every effect's emission rate, so fewer particles exist and
    /// are drawn (a storm gets thinner, not cheaper per particle).
    /// </summary>
    public float Density { get; set; } = 1;

    /// <summary>
    /// The y of the terrain normal at a point, by finite differences of the height (the game's own terrain query returns the normal).
    /// </summary>
    public float NormalY(float x, float z)
    {
        const float s = 12;
        float dx = GroundHeight(x - s, z) - GroundHeight(x + s, z), dz = GroundHeight(x, z - s) - GroundHeight(x, z + s);
        return 1 / MathF.Sqrt(1 + (dx * dx + dz * dz) / (4 * s * s));
    }

    /// <summary>
    /// A place for an effect (Verified (decompiled), FUN_1408fc3c0): a random point of the area, the ground height there, up to 10 tries for one
    /// whose height is within <c>min/max altitude</c> (skipped when both are 0) and whose terrain normal y is within <c>min/max slope</c>; after 10
    /// tries the last stands.
    /// </summary>
    public Vector3 Place(EffectRecord e, Random random, in EffectCamera camera)
    {
        var area = Area ?? new DiscArea(camera.Eye.X, camera.Eye.Z, FallbackRadius);
        float x = camera.Eye.X, z = camera.Eye.Z, y = 0;
        for (int tries = 10; tries > 0; tries--)
        {
            if (!area.TryPick(random, out x, out z))
            {
                x = camera.Eye.X;
                z = camera.Eye.Z;
            }
            y = GroundHeight(x, z);
            bool altitude = (e.MinAltitude == 0 && e.MaxAltitude == 0) || (y >= e.MinAltitude && y <= e.MaxAltitude);
            float ny = NormalY(x, z);
            if (altitude && ny >= e.MinSlope && ny <= e.MaxSlope) break;
        }
        return new Vector3(x, y, z);
    }
}
