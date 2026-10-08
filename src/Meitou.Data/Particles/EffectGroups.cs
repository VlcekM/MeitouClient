using System.Numerics;

namespace Meitou.Data.Particles;

/// <summary>The camera as the groups see it each frame: where the eye is and which way it looks.</summary>
public readonly record struct EffectCamera(Vector3 Eye, Vector3 Forward);

/// <summary>
/// One live effect of a weather (docs/formats/weather.md "Spawning"): a particle system and the rule that places it. The group classes
/// are keyed by the EFFECT <c>type</c>; <see cref="EffectGroups.Create"/> makes the right one. Camera groups exist; point, wandering and
/// global groups are the next part.
/// </summary>
public abstract class EffectGroup
{
    protected EffectGroup(WeatherEffectEntry entry, ParticleSimulation simulation)
    {
        Entry = entry;
        Simulation = simulation;
        Tint = entry.Effect.ColourMultiplier;
    }

    public WeatherEffectEntry Entry { get; }
    public ParticleSimulation Simulation { get; }
    /// <summary>The EFFECT's <c>colour multiplier</c> applied to the particles' rgb (Observed: "for weather effects only").</summary>
    public Vector3 Tint { get; }
    /// <summary>Multiplies every particle's alpha (a group fading in or out).</summary>
    public float Alpha { get; protected set; } = 1;
    /// <summary>Where the simulation's own coordinate origin is in the world: add it to <see cref="ParticleSimulation.Collect"/>'s positions.</summary>
    public Vector3 Anchor { get; protected set; }

    /// <summary>Steps the group by <paramref name="dt"/> seconds (0: not at all, a held-still frame).</summary>
    public abstract void Update(float dt, in EffectCamera camera, WeatherEffectInput weather);

    /// <summary>Simulates the group's start-up of <paramref name="seconds"/> so it has its particles in place (fixed steps).</summary>
    public abstract void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather);

    /// <summary>The emission multiplier from the wind: <c>min wind span rate</c> is the wind speed needed for any particles, <c>max wind span rate</c> for all of them.</summary>
    public static float WindSpanScale(EffectRecord effect, float windSpeed)
    {
        float lo = effect.MinWindSpan, hi = effect.MaxWindSpan;
        if (hi > lo) return Math.Clamp((windSpeed - lo) / (hi - lo), 0, 1);
        return lo > 0 && windSpeed < lo ? 0 : 1;
    }

    protected ParticleEnvironment Environment(WeatherEffectInput weather)
    {
        var e = Entry.Effect;
        return new ParticleEnvironment(weather.Wind, e.WindSpeedMultiplier, WindSpanScale(e, weather.Wind.Length()), e.WindAffected, e.WindDirectionEmission);
    }
}

/// <summary>
/// CAMERA, CAMERA_RAIN and CAMERA_ACID_RAIN (docs/formats/weather.md "Spawning", Verified (decompiled): the camera group's functions): the
/// system's node sits at the camera's position and moves with it; its particles live in world space and a wrapping rule keeps them in a
/// cube of edge <c>2d / 1.5</c> centred <c>d</c> ahead of the camera along its view direction, where <c>d</c> is the effect's size, the EFFECT
/// loader's 0.75 × the largest Box extent (or Circle radius) times the system's scale (so the cube's edge is that extent: 120 for
/// <c>Kenshi_Heavy_Rain</c>, which is its emitter's box). Particles are emitted round the camera and wrapped into the cube in front of it.
/// </summary>
public sealed class CameraEffectGroup : EffectGroup
{
    readonly float distance;
    bool started;

    public CameraEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed) : base(entry, new ParticleSimulation(system, seed))
    {
        float size = system.LargestEmitterExtent * 0.75f;
        distance = size > 0 ? size : 100;
    }

    /// <summary>The cube's centre's distance ahead of the camera, <c>d</c>.</summary>
    public float Distance => distance;
    /// <summary>The cube's edge, <c>2d / 1.5</c>.</summary>
    public float Edge => distance * 2 / 1.5f;

    static Vector3 Forward(in EffectCamera camera) => camera.Forward.LengthSquared() > 1e-6f ? Vector3.Normalize(camera.Forward) : -Vector3.UnitZ;

    /// <summary>The cube's centre in the world.</summary>
    public Vector3 Centre(in EffectCamera camera) => camera.Eye + Forward(camera) * distance;

    /// <summary>Puts the simulation's origin at the camera. The simulation keeps coordinates near <see cref="EffectGroup.Anchor"/>, so a far-off world position does not cost the particles' precision; the anchor follows the camera in big steps.</summary>
    void Follow(in EffectCamera camera)
    {
        if (!started) Anchor = camera.Eye;
        var local = camera.Eye - Anchor;
        if (local.Length() > 1500)
        {
            Simulation.Translate(-local);
            Anchor = camera.Eye;
            local = Vector3.Zero;
        }
        Simulation.WorldOffset = Anchor;
        Simulation.Origin = local;
    }

    void WrapIntoCube(in EffectCamera camera) => Simulation.Wrap(Centre(camera) - Anchor, Edge / 2);

    public override void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        var env = Environment(weather);
        Follow(camera);
        started = true;
        const float step = 1f / 30;
        for (float t = 0; t < seconds; t += step)
        {
            Simulation.Advance(Math.Min(step, seconds - t), env);
            WrapIntoCube(camera);
        }
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        Follow(camera);
        if (!started)
        {
            started = true;
            if (Simulation.Definition.FastForward is { } ff && ff.Time > 0) Prewarm(ff.Time, camera, weather);
        }
        if (dt > 0) Simulation.Advance(dt, Environment(weather));
        WrapIntoCube(camera);
    }
}

public static class EffectGroups
{
    /// <summary>The group for an entry, or null when its type has no group yet or its particle system is unknown.</summary>
    public static EffectGroup? Create(WeatherEffectEntry entry, ParticleLibrary library, int seed)
    {
        if (!entry.Effect.IsCameraGroup) return null;
        return library.FindSystem(entry.Effect.ParticleSystem) is { } system ? new CameraEffectGroup(entry, system, seed) : null;
    }
}
