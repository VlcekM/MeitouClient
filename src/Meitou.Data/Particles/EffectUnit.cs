using System.Numerics;

namespace Meitou.Data.Particles;

/// <summary>
/// How an effect's particles are kept near its node.
/// </summary>
public enum UnitWrap
{
    /// <summary>
    /// The particles go where they move.
    /// </summary>
    None,

    /// <summary>
    /// They wrap round a cube (the camera groups).
    /// </summary>
    Cube,

    /// <summary>
    /// They are brought back into a sphere (the global groups).
    /// </summary>
    Sphere
}

/// <summary>
/// One live effect (the game's "effect handler"): a particle system at a place. It exists as a record (position, life) all the time and holds a
/// simulation only while the camera is near enough to see it (<see cref="Simulation"/> is null otherwise: Observed, the plugin's own
/// <c>nonvisible_update_timeout</c> is the same idea). The main thread moves it (<see cref="EffectGroup.Update"/>) and queues time in
/// <see cref="Pending"/>; <see cref="Advance"/> runs the simulation and may run on any thread, one unit per thread.
/// </summary>
public sealed class EffectUnit
{
    /// <summary>
    /// A unit of <paramref name="effect"/> running <paramref name="system"/>, its simulation seeded with <paramref name="seed"/> once it
    /// is activated.
    /// </summary>
    public EffectUnit(EffectRecord effect, PuSystemDef system, int seed)
    {
        Effect = effect;
        System = system;
        Seed = seed;
        LongestLife = ParticleSimulation.LongestLife(system);
    }

    /// <summary>
    /// The EFFECT record the unit runs.
    /// </summary>
    public EffectRecord Effect { get; }
    /// <summary>
    /// The particle system the unit simulates.
    /// </summary>
    public PuSystemDef System { get; }
    /// <summary>
    /// The seed of the unit's simulation; the same unit makes the same particles each time it is activated.
    /// </summary>
    public int Seed { get; }
    /// <summary>
    /// The longest particle life of the system's emitters, in seconds: how much history an activated unit catches up on.
    /// </summary>
    public float LongestLife { get; }
    /// <summary>
    /// The unit's simulation while it is active (near the camera); null otherwise.
    /// </summary>
    public ParticleSimulation? Simulation { get; set; }
    /// <summary>
    /// The node's place in the world.
    /// </summary>
    public Vector3 Position { get; set; }
    /// <summary>
    /// Where the simulation's coordinates start in the world (kept near the node so a far-off place does not cost the particles precision).
    /// </summary>
    public Vector3 Anchor { get; set; }
    /// <summary>
    /// The rgb multiplier of the particles: the effect's colour multiplier, times the ground colour when the effect asks.
    /// </summary>
    public Vector3 Tint { get; set; } = Vector3.One;
    /// <summary>
    /// The multiplier of the particles' alpha when drawn.
    /// </summary>
    public float Alpha { get; set; } = 1;
    /// <summary>
    /// The node's turn (<see cref="ParticleSimulation.Orientation"/>).
    /// </summary>
    public Quaternion Orientation { get; set; } = Quaternion.Identity;
    /// <summary>
    /// The particles' size and life factors (<see cref="ParticleSimulation.Size"/>, <see cref="ParticleSimulation.LifeScale"/>).
    /// </summary>
    public float Size { get; set; } = 1;
    /// <summary>
    /// See <see cref="Size"/>.
    /// </summary>
    public float LifeScale { get; set; } = 1;
    /// <summary>
    /// The node's bounding sphere for culling (<see cref="float.PositiveInfinity"/>: always drawn).
    /// </summary>
    public float Radius { get; set; }
    /// <summary>
    /// Seconds of life left (<see cref="float.PositiveInfinity"/>: never ends).
    /// </summary>
    public float Life { get; set; } = float.PositiveInfinity;
    /// <summary>
    /// Seconds since the unit was made.
    /// </summary>
    public float Age { get; set; }
    /// <summary>
    /// The life is over: the emitters make nothing more, the particles live out (Verified (decompiled), the handlers' stop).
    /// </summary>
    public bool Stopped { get; set; }
    /// <summary>
    /// Seconds since the unit stopped, for the fog fade out.
    /// </summary>
    public float StoppedFor { get; set; }
    /// <summary>
    /// The wind and emission scale its simulation reads this frame; set by the group.
    /// </summary>
    public ParticleEnvironment Environment { get; set; } = ParticleEnvironment.None;
    /// <summary>
    /// The node's distance to the camera at the last update.
    /// </summary>
    public float DistanceToCamera { get; set; }
    // wandering
    /// <summary>
    /// Wandering: the unit's direction of travel in x and z, unit length.
    /// </summary>
    public Vector2 Heading { get; set; }
    /// <summary>
    /// Wandering: seconds until the heading turns again.
    /// </summary>
    public float TurnTimer { get; set; }
    /// <summary>
    /// Wandering: seconds until the height is put on the ground again.
    /// </summary>
    public float GroundTimer { get; set; }
    // wrapping
    /// <summary>
    /// How the particles are kept near the node.
    /// </summary>
    public UnitWrap Wrap { get; set; }
    /// <summary>
    /// The wrap's centre in the world: the cube's centre ahead of the camera, or the global node.
    /// </summary>
    public Vector3 WrapCentre { get; set; }
    /// <summary>
    /// The cube's edge, or the sphere's radius.
    /// </summary>
    public float WrapSize { get; set; }
    /// <summary>
    /// Sphere wrap: the highest a particle may be (infinity: no limit).
    /// </summary>
    public float WrapMaxHeight { get; set; } = float.PositiveInfinity;
    /// <summary>
    /// Sphere wrap: the camera's direction of movement this frame (unit length, zero when it stands still); a particle that leaves the sphere is
    /// first moved along it.
    /// </summary>
    public Vector3 WrapAhead { get; set; }
    /// <summary>
    /// Seconds of simulation to run on the next <see cref="Advance"/>.
    /// </summary>
    public float Pending { get; set; }
    /// <summary>
    /// Simulation steps one <see cref="Advance"/> call runs at most.
    /// </summary>
    public const int MaxStepsPerFrame = 40;

    /// <summary>
    /// Counts the sphere wraps, so each one's random places differ.
    /// </summary>
    int _wrapSalt;

    /// <summary>
    /// Whether the unit has a simulation.
    /// </summary>
    public bool Active => Simulation is not null;
    /// <summary>
    /// Whether the unit has stopped and has no particles left to show.
    /// </summary>
    public bool Done => Stopped && (Simulation is null || Simulation.ParticleCount == 0);

    /// <summary>
    /// The fog volumes' fade fraction: up over <c>fog fade in duration</c> from the unit's start, down over <c>fog fade out duration</c> from its
    /// stop (0 = no fade). The game scales the volume with it, not its opacity: the radius from 0 and the density distance from 10 radii (a cylinder:
    /// 4 times its own) linearly (FogController's FogFadeSphere / FogFadeCylinder; <c>FogVolumes.Faded</c>).
    /// </summary>
    public float FogFade
    {
        get
        {
            float f = Effect.FogFadeIn > 0 ? Math.Clamp(Age / Effect.FogFadeIn, 0, 1) : 1;
            if (Stopped) f *= Effect.FogFadeOut > 0 ? Math.Clamp(1 - StoppedFor / Effect.FogFadeOut, 0, 1) : 0;
            return f;
        }
    }

    /// <summary>
    /// How much of the unit is shown by its distance (Meitou, <c>ParticleRenderer.RangeFade</c>): 1 out to 85 % of the distance it is simulated
    /// within, falling to 0 at that distance, so it is out of sight when it starts being simulated (a cloud of its whole age at once) and when it
    /// stops (all its particles gone).
    /// </summary>
    public float RangeFade { get; set; } = 1;

    /// <summary>
    /// Makes the unit's simulation, with no particles, at its current state and anchors its coordinates at the node.
    /// </summary>
    public void Activate()
    {
        Simulation = new ParticleSimulation(System, Seed)
        {
            EmissionStopped = Stopped,
            Orientation = Orientation,
            Size = Size,
            LifeScale = LifeScale,
        };
        Anchor = Position;
    }

    /// <summary>
    /// Drops the simulation and the queued time.
    /// </summary>
    public void Deactivate()
    {
        Simulation = null;
        Pending = 0;
    }

    /// <summary>
    /// Runs the queued time: <see cref="Pending"/> in steps of at most 1/30 s, wrapping the particles after each.
    /// </summary>
    public void Advance()
    {
        var sim = Simulation;
        if (sim is null)
        {
            Pending = 0;
            return;
        }
        var local = Position - Anchor;
        if (local.LengthSquared() > 1500f * 1500f)
        {
            sim.Translate(-local);
            Anchor = Position;
            local = Vector3.Zero;
        }
        sim.WorldOffset = Anchor;
        sim.Origin = local;
        sim.Orientation = Orientation;   // a steered burst turns with what it follows (ParticleRenderer.MoveBurst)
        sim.EmissionStopped = Stopped;
        float left = Pending;
        Pending = 0;
        // A backlog (a unit just woken, or a frame hitch) is caught up in coarser steps (1/10 s) and over several frames: at most MaxStepsPerFrame
        // steps per call, the rest stays in Pending. Observed choice; the game simulates what it activates at once.
        const float substep = 1f / 30;
        for (int steps = 0; left > 1e-5f; steps++)
        {
            if (steps >= MaxStepsPerFrame)
            {
                Pending = left;
                break;
            }
            float s = Math.Min(left, left > 3f ? 0.1f : substep);
            left -= s;
            sim.Advance(s, Environment);
            ApplyWrap(sim);
        }
    }

    /// <summary>
    /// True while the unit is still catching up on a backlog of more than a second (it is not drawn until it has).
    /// </summary>
    public bool CatchingUp => Pending > 1f;

    /// <summary>
    /// Wraps the simulation's particles by <see cref="Wrap"/>, in the simulation's own coordinates.
    /// </summary>
    void ApplyWrap(ParticleSimulation sim)
    {
        switch (Wrap)
        {
            case UnitWrap.Cube:
                sim.Wrap(WrapCentre - Anchor, WrapSize / 2);
                break;
            case UnitWrap.Sphere:
                sim.WrapSphere(WrapCentre - Anchor, WrapSize, WrapMaxHeight - Anchor.Y, WrapAhead, _wrapSalt++);
                break;
        }
    }
}
