using System.Numerics;

namespace Meitou.Data.Particles;

// The typed view of a ParticleUniverse script (docs/formats/particle-universe.md). Defaults are the plugin's documented ones where a
// script leaves an attribute out; for Kenshi they are Observed (a missing quota, box size or velocity in a shipped script is read
// with them), not Verified.

/// <summary>
/// An emitter's type, from its <c>emitter</c> block's type name.
/// </summary>
public enum PuEmitterType
{
    /// <summary>
    /// <c>Box</c>: emits inside a box of <see cref="PuEmitterDef.BoxSize"/>.
    /// </summary>
    Box,

    /// <summary>
    /// <c>Circle</c>: emits on a circle of <see cref="PuEmitterDef.Radius"/>.
    /// </summary>
    Circle,

    /// <summary>
    /// <c>Point</c>: emits from its position.
    /// </summary>
    Point,

    /// <summary>
    /// <c>SphereSurface</c>: emits on a sphere of <see cref="PuEmitterDef.Radius"/>.
    /// </summary>
    SphereSurface,

    /// <summary>
    /// <c>Line</c>: emits along the line to <see cref="PuEmitterDef.End"/>.
    /// </summary>
    Line,

    /// <summary>
    /// <c>Slave</c>: follows another technique's particles.
    /// </summary>
    Slave,

    /// <summary>
    /// Any other type name.
    /// </summary>
    Unknown
}

/// <summary>
/// An affector's type, from its <c>affector</c> block's type name.
/// </summary>
public enum PuAffectorType
{
    /// <summary>
    /// <c>Colour</c>: sets or multiplies the colour by the particle's life.
    /// </summary>
    Colour,

    /// <summary>
    /// <c>Scale</c>: grows or shrinks the particle.
    /// </summary>
    Scale,

    /// <summary>
    /// <c>TextureRotator</c>: rotates the particle's texture.
    /// </summary>
    TextureRotator,

    /// <summary>
    /// <c>LinearForce</c>: pushes along a force vector.
    /// </summary>
    LinearForce,

    /// <summary>
    /// <c>Vortex</c>: turns particles round an axis.
    /// </summary>
    Vortex,

    /// <summary>
    /// <c>Gravity</c>: pulls towards the affector's position.
    /// </summary>
    Gravity,

    /// <summary>
    /// <c>Randomiser</c>: nudges particles by random amounts.
    /// </summary>
    Randomiser,

    /// <summary>
    /// <c>SineForce</c>: a force vector times a sine of the system's age.
    /// </summary>
    SineForce,

    /// <summary>
    /// <c>ForceField</c>; not simulated.
    /// </summary>
    ForceField,

    /// <summary>
    /// <c>Jet</c>: speeds particles up along their direction of travel.
    /// </summary>
    Jet,

    /// <summary>
    /// <c>Align</c>; not simulated.
    /// </summary>
    Align,

    /// <summary>
    /// <c>FlockCentering</c>; not simulated.
    /// </summary>
    FlockCentering,

    /// <summary>
    /// <c>GeometryRotator</c>; not simulated.
    /// </summary>
    GeometryRotator,

    /// <summary>
    /// <c>ScaleVelocity</c>; not simulated.
    /// </summary>
    ScaleVelocity,

    /// <summary>
    /// Any other type name.
    /// </summary>
    Unknown,
}

/// <summary>
/// An observer's type, from its <c>observer</c> block's type name.
/// </summary>
public enum PuObserverType
{
    /// <summary>
    /// <c>OnPosition</c>: a particle's position on one axis meets the condition (<c>position_x|y|z</c>).
    /// </summary>
    OnPosition,

    /// <summary>
    /// <c>OnTime</c>: the time meets the condition (<c>on_time</c>).
    /// </summary>
    OnTime,

    /// <summary>
    /// <c>OnClear</c>.
    /// </summary>
    OnClear,

    /// <summary>
    /// <c>OnEmission</c>.
    /// </summary>
    OnEmission,

    /// <summary>
    /// Any other type name.
    /// </summary>
    Unknown
}

/// <summary>
/// How an observer's condition compares.
/// </summary>
public enum PuCompare
{
    /// <summary>
    /// <c>less_than</c>.
    /// </summary>
    LessThan,

    /// <summary>
    /// <c>greater_than</c>.
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Any other word: equal within 0.001 where the simulation compares.
    /// </summary>
    Equals
}

/// <summary>
/// An <c>emitter</c> block: where, how often and how particles are emitted.
/// </summary>
public sealed class PuEmitterDef
{
    /// <summary>
    /// The emitter's type; <see cref="PuEmitterType.Unknown"/> for one not read here.
    /// </summary>
    public required PuEmitterType Type { get; init; }
    /// <summary>
    /// The type name as the script writes it.
    /// </summary>
    public required string TypeName { get; init; }
    /// <summary>
    /// The emitter's name; empty when it has none.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// <c>enabled</c>; default true.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// <c>emission_rate</c>: particles per second; default 10.
    /// </summary>
    public PuDynamic Rate { get; init; } = PuDynamic.Fixed(10);
    /// <summary>
    /// <c>time_to_live</c>: a particle's life in seconds; default 3.
    /// </summary>
    public PuDynamic Life { get; init; } = PuDynamic.Fixed(3);
    /// <summary>
    /// <c>velocity</c>: a particle's speed when emitted; default 100.
    /// </summary>
    public PuDynamic Velocity { get; init; } = PuDynamic.Fixed(100);
    /// <summary>
    /// Half-angle of the cone round <see cref="Direction"/> the particles leave in, degrees.
    /// </summary>
    public PuDynamic Angle { get; init; } = PuDynamic.Fixed(0);
    /// <summary>
    /// <c>mass</c>: a particle's mass; default 1.
    /// </summary>
    public PuDynamic Mass { get; init; } = PuDynamic.Fixed(1);
    /// <summary>
    /// How long the emitter works after it starts (null: for ever); then it rests for <see cref="RepeatDelay"/> and starts again (null: never).
    /// </summary>
    public PuDynamic? Duration { get; init; }
    /// <summary>
    /// <c>repeat_delay</c>: how long the emitter rests after <see cref="Duration"/>; null: it does not start again.
    /// </summary>
    public PuDynamic? RepeatDelay { get; init; }
    /// <summary>
    /// The particle's size when the emitter sets it: <c>all_particle_dimensions</c> for the three (it wins when both are given), else
    /// <c>particle_width</c> / <c>particle_height</c> / <c>particle_depth</c>.
    /// </summary>
    public PuDynamic? Width { get; init; }
    /// <summary>
    /// <c>particle_height</c>; see <see cref="Width"/>.
    /// </summary>
    public PuDynamic? Height { get; init; }
    /// <summary>
    /// <c>particle_depth</c>; see <see cref="Width"/>.
    /// </summary>
    public PuDynamic? Depth { get; init; }
    /// <summary>
    /// <c>all_particle_dimensions</c>; see <see cref="Width"/>.
    /// </summary>
    public PuDynamic? AllDimensions { get; init; }
    /// <summary>
    /// <c>position</c>: the emitter's place in the technique.
    /// </summary>
    public Vector3 Position { get; init; }
    /// <summary>
    /// <c>direction</c>: the way particles leave; default +Y.
    /// </summary>
    public Vector3 Direction { get; init; } = Vector3.UnitY;
    /// <summary>
    /// <c>colour</c>: the emitted colour; default white.
    /// </summary>
    public Vector4 Colour { get; init; } = Vector4.One;
    /// <summary>
    /// The emitted colour is picked at random between these when either is given (the missing one is <see cref="Colour"/>).
    /// </summary>
    public Vector4? ColourStart { get; init; }
    /// <summary>
    /// <c>end_colour_range</c>; see <see cref="ColourStart"/>.
    /// </summary>
    public Vector4? ColourEnd { get; init; }
    /// <summary>
    /// Box: the extent along x, y and z.
    /// </summary>
    public Vector3 BoxSize { get; init; } = new(100, 100, 100);
    /// <summary>
    /// <c>radius</c> of a Circle or SphereSurface emitter; default 100.
    /// </summary>
    public float Radius { get; init; } = 100;
    /// <summary>
    /// Circle: the angle step (radians) between emissions when not random.
    /// </summary>
    public float Step { get; init; } = 0.1f;
    /// <summary>
    /// <c>emit_random</c>: a Circle emits at random angles, else in <see cref="Step"/>s; default true.
    /// </summary>
    public bool EmitRandom { get; init; } = true;
    /// <summary>
    /// Circle / sphere: particles leave along the radius (the direction attribute is ignored).
    /// </summary>
    public bool AutoDirection { get; init; }
    /// <summary>
    /// <c>force_emission</c> as the script sets it; the simulation does not read it.
    /// </summary>
    public bool ForceEmission { get; init; }
    /// <summary>
    /// <c>keep_local</c>: the emitter's particles stay in its space.
    /// </summary>
    public bool KeepLocal { get; init; }
    /// <summary>
    /// Line: the end point (the line runs from <see cref="Position"/> to it).
    /// </summary>
    public Vector3 End { get; init; }
    /// <summary>
    /// <c>emits technique_particle|emitter_particle|system_particle name</c>: what an emitted particle becomes (not simulated in part one).
    /// </summary>
    public string? EmitsKind { get; init; }
    /// <summary>
    /// The name after the kind in <c>emits</c>; null when the emitter emits visual particles.
    /// </summary>
    public string? EmitsName { get; init; }
    /// <summary>
    /// The emitter's block as parsed, with every raw attribute.
    /// </summary>
    public required PuNode Source { get; init; }
}

/// <summary>
/// An <c>affector</c> block: what it does to particles. Every type's attributes are read; each type uses its own.
/// </summary>
public sealed class PuAffectorDef
{
    /// <summary>
    /// The affector's type; <see cref="PuAffectorType.Unknown"/> for one not read here.
    /// </summary>
    public required PuAffectorType Type { get; init; }
    /// <summary>
    /// The type name as the script writes it.
    /// </summary>
    public required string TypeName { get; init; }
    /// <summary>
    /// The affector's name; empty when it has none.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// <c>enabled</c>; default true.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// <c>position</c>: the affector's place in the technique.
    /// </summary>
    public Vector3 Position { get; init; }
    /// <summary>
    /// The <c>exclude_emitter</c> names: emitters whose particles the affector leaves alone.
    /// </summary>
    public IReadOnlyList<string> ExcludedEmitters { get; init; } = [];
    /// <summary>
    /// <c>mass_affector</c>; default 0.
    /// </summary>
    public float MassAffector { get; init; }
    // Colour
    /// <summary>
    /// Colour: the <c>time_colour</c> keys (life fraction, colour), sorted by time.
    /// </summary>
    public IReadOnlyList<(float Time, Vector4 Colour)> TimeColours { get; init; } = [];
    /// <summary>
    /// Colour: <c>colour_operation multiply</c> multiplies the particle's colour instead of setting it.
    /// </summary>
    public bool MultiplyColour { get; init; }
    // Scale (units per second added to the size, times the system scale; xyz drives all three and then x / y / z are not read)
    /// <summary>
    /// Scale: <c>x_scale</c>.
    /// </summary>
    public PuDynamic? ScaleX { get; init; }
    /// <summary>
    /// Scale: <c>y_scale</c>.
    /// </summary>
    public PuDynamic? ScaleY { get; init; }
    /// <summary>
    /// Scale: <c>z_scale</c>.
    /// </summary>
    public PuDynamic? ScaleZ { get; init; }
    /// <summary>
    /// Scale: <c>xyz_scale</c>, for all three.
    /// </summary>
    public PuDynamic? ScaleXyz { get; init; }
    /// <summary>
    /// Scale: the rates are read at the system's age instead of the particle's life fraction.
    /// </summary>
    public bool SinceStartSystem { get; init; }
    // TextureRotator
    /// <summary>
    /// TextureRotator: <c>rotation</c>, the starting rotation.
    /// </summary>
    public PuDynamic? Rotation { get; init; }
    /// <summary>
    /// TextureRotator: <c>rotation_speed</c>.
    /// </summary>
    public PuDynamic? RotationSpeed { get; init; }
    /// <summary>
    /// TextureRotator: <c>use_own_rotation</c>.
    /// </summary>
    public bool UseOwnRotation { get; init; }
    // LinearForce, SineForce (force_vector); Gravity (gravity); Jet (acceleration)
    /// <summary>
    /// <c>force_vector</c>, else <c>acceleration</c> as a vector; zero when neither is given.
    /// </summary>
    public Vector3 Force { get; init; }
    /// <summary>
    /// LinearForce: <c>force_application average</c> moves the velocity halfway to the force instead of adding it.
    /// </summary>
    public bool AverageForce { get; init; }
    /// <summary>
    /// <c>gravity</c>, else <c>acceleration</c> as a number.
    /// </summary>
    public PuDynamic? Strength { get; init; }
    /// <summary>
    /// SineForce: <c>min_frequency</c>.
    /// </summary>
    public float MinFrequency { get; init; }
    /// <summary>
    /// SineForce: <c>max_frequency</c>.
    /// </summary>
    public float MaxFrequency { get; init; }
    // Vortex
    /// <summary>
    /// Vortex: <c>rotation_axis</c>; default +Y.
    /// </summary>
    public Vector3 Axis { get; init; } = Vector3.UnitY;
    // Randomiser
    /// <summary>
    /// Randomiser: <c>max_deviation_x</c>, <c>_y</c> and <c>_z</c>.
    /// </summary>
    public Vector3 MaxDeviation { get; init; }
    /// <summary>
    /// Randomiser: <c>time_step</c>.
    /// </summary>
    public float TimeStep { get; init; }
    /// <summary>
    /// The affector's block as parsed, with every raw attribute.
    /// </summary>
    public required PuNode Source { get; init; }
}

/// <summary>
/// An <c>observer</c> block: the condition it watches and its handlers.
/// </summary>
public sealed class PuObserverDef
{
    /// <summary>
    /// The observer's type; <see cref="PuObserverType.Unknown"/> for one not read here.
    /// </summary>
    public required PuObserverType Type { get; init; }
    /// <summary>
    /// The type name as the script writes it.
    /// </summary>
    public required string TypeName { get; init; }
    /// <summary>
    /// <c>enabled</c>; default true.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// <c>observe_interval</c>: seconds between checks; 0 checks every update.
    /// </summary>
    public float Interval { get; init; }
    /// <summary>
    /// OnPosition: which axis (0 x, 1 y, 2 z), how it compares and with what. OnTime: the time in <see cref="Value"/>.
    /// </summary>
    public int Axis { get; init; } = -1;
    /// <summary>
    /// How the condition compares; see <see cref="Axis"/>.
    /// </summary>
    public PuCompare Compare { get; init; }
    /// <summary>
    /// The value the condition compares with; see <see cref="Axis"/>.
    /// </summary>
    public float Value { get; init; }
    /// <summary>
    /// <c>since_start_system</c>: the time is the system's age, not the particle's.
    /// </summary>
    public bool SinceStartSystem { get; init; }
    /// <summary>
    /// The type names of the observer's <c>handler</c> blocks, in order.
    /// </summary>
    public IReadOnlyList<string> Handlers { get; init; } = [];
    /// <summary>
    /// The observer's block as parsed, with every raw attribute.
    /// </summary>
    public required PuNode Source { get; init; }
}

/// <summary>
/// A technique's <c>renderer</c> block.
/// </summary>
public sealed class PuRendererDef
{
    /// <summary>
    /// The renderer's type name, e.g. Billboard.
    /// </summary>
    public required string TypeName { get; init; }
    /// <summary>
    /// Whether the renderer is a Billboard one.
    /// </summary>
    public bool IsBillboard => TypeName == "Billboard";
    /// <summary>
    /// point (default), oriented_common, oriented_self, oriented_shape, perpendicular_common, perpendicular_self.
    /// </summary>
    public string BillboardType { get; init; } = "point";
    /// <summary>
    /// center (default), bottom_center, top_center, center_left, center_right, bottom_left...
    /// </summary>
    public string Origin { get; init; } = "center";
    /// <summary>
    /// "texcoord" (default) or "vertex": which of the quad's rotations the TextureRotator drives. Drawn the same here.
    /// </summary>
    public string RotationType { get; init; } = "texcoord";
    /// <summary>
    /// <c>common_direction</c> of an oriented or perpendicular billboard; default +Z.
    /// </summary>
    public Vector3 CommonDirection { get; init; } = Vector3.UnitZ;
    /// <summary>
    /// <c>common_up_vector</c>; default +Y.
    /// </summary>
    public Vector3 CommonUp { get; init; } = Vector3.UnitY;
    /// <summary>
    /// <c>sorting</c>: particles are drawn back to front.
    /// </summary>
    public bool Sorting { get; init; }
    /// <summary>
    /// <c>accurate_facing</c>: each billboard faces the camera itself.
    /// </summary>
    public bool AccurateFacing { get; init; }
}

/// <summary>
/// A <c>technique</c> block: its quotas, material and defaults, renderer, emitters, affectors and observers.
/// </summary>
public sealed class PuTechniqueDef
{
    /// <summary>
    /// The technique's name; empty when it has none.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// <c>enabled</c>; default true.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// <c>visual_particle_quota</c>: the most particles alive at once; default 500.
    /// </summary>
    public int VisualQuota { get; init; } = 500;
    /// <summary>
    /// <c>emitted_emitter_quota</c>; default 50.
    /// </summary>
    public int EmittedEmitterQuota { get; init; } = 50;
    /// <summary>
    /// <c>emitted_technique_quota</c>; default 50.
    /// </summary>
    public int EmittedTechniqueQuota { get; init; } = 50;
    /// <summary>
    /// <c>material</c>: the material the particles are drawn with.
    /// </summary>
    public string Material { get; init; } = "";
    /// <summary>
    /// <c>default_particle_width</c>; default 100.
    /// </summary>
    public float DefaultWidth { get; init; } = 100;
    /// <summary>
    /// <c>default_particle_height</c>; default 100.
    /// </summary>
    public float DefaultHeight { get; init; } = 100;
    /// <summary>
    /// <c>default_particle_depth</c>; default 100.
    /// </summary>
    public float DefaultDepth { get; init; } = 100;
    /// <summary>
    /// <c>position</c>: the technique's place in the system.
    /// </summary>
    public Vector3 Position { get; init; }
    /// <summary>
    /// <c>keep_local</c>: the technique's particles stay in its space.
    /// </summary>
    public bool KeepLocal { get; init; }
    /// <summary>
    /// The technique's last <c>renderer</c> block, or a Billboard one when it has none.
    /// </summary>
    public required PuRendererDef Renderer { get; init; }
    /// <summary>
    /// The technique's emitters in script order.
    /// </summary>
    public IReadOnlyList<PuEmitterDef> Emitters { get; init; } = [];
    /// <summary>
    /// The technique's affectors in script order.
    /// </summary>
    public IReadOnlyList<PuAffectorDef> Affectors { get; init; } = [];
    /// <summary>
    /// The technique's observers in script order.
    /// </summary>
    public IReadOnlyList<PuObserverDef> Observers { get; init; } = [];
    /// <summary>
    /// The technique's block as parsed, with every raw attribute.
    /// </summary>
    public required PuNode Source { get; init; }
}

/// <summary>
/// A <c>system</c> of a ParticleUniverse script, typed: its scale, start settings and techniques, and the measures the viewer culls with.
/// </summary>
public sealed class PuSystemDef
{
    #region Definition

    /// <summary>
    /// The system's name, which effects refer to it by.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// The script file that defines the system.
    /// </summary>
    public string File { get; init; } = "";
    /// <summary>
    /// <c>category</c>; empty when not set.
    /// </summary>
    public string Category { get; init; } = "";
    /// <summary>
    /// The system's scale: it multiplies emitter extents, positions and particle sizes (Observed).
    /// </summary>
    public Vector3 Scale { get; init; } = Vector3.One;
    /// <summary>
    /// It multiplies the emitted velocities (Observed).
    /// </summary>
    public float ScaleVelocity { get; init; } = 1;
    /// <summary>
    /// <c>fast_forward time interval</c>: when the system starts it is stepped for <c>time</c> seconds in steps of <c>interval</c>.
    /// </summary>
    public (float Time, float Interval)? FastForward { get; init; }
    /// <summary>
    /// <c>iteration_interval</c>; 0 when not set.
    /// </summary>
    public float IterationInterval { get; init; }
    /// <summary>
    /// <c>keep_local</c>: the particles stay in the system's space.
    /// </summary>
    public bool KeepLocal { get; init; }
    /// <summary>
    /// The system's techniques in script order.
    /// </summary>
    public IReadOnlyList<PuTechniqueDef> Techniques { get; init; } = [];
    /// <summary>
    /// The system's block as parsed, with every raw attribute.
    /// </summary>
    public required PuNode Source { get; init; }

    #endregion

    #region Measures

    /// <summary>
    /// The largest extent of the Box emitters (width, height, depth) and the Circle emitters' diameter, times the scale's largest component (the
    /// EFFECT loader's size measure; docs/formats/weather.md).
    /// </summary>
    public float LargestEmitterExtent
    {
        get
        {
            float m = 0;
            foreach (var e in Techniques.SelectMany(t => t.Emitters))
            {
                if (e.Type == PuEmitterType.Box)
                    m = Math.Max(m, Math.Max(e.BoxSize.X * Scale.X, Math.Max(e.BoxSize.Y * Scale.Y, e.BoxSize.Z * Scale.Z)));
                else if (e.Type == PuEmitterType.Circle) m = Math.Max(m, e.Radius * 2 * Math.Max(Scale.X, Scale.Z));
            }
            return m;
        }
    }

    /// <summary>
    /// A bound on how far from the system's node a particle can get (Observed, a viewer measure for culling): the emitter's extent, plus the speed
    /// times the life, plus the linear forces' reach, plus the particle's largest size (with the scale affectors' growth), times the scale. The
    /// camera and global groups (wrapped round the camera) do not use it.
    /// </summary>
    public float BoundingRadius
    {
        get
        {
            float radius = 0;
            float scale = Math.Max(Scale.X, Math.Max(Scale.Y, Scale.Z));
            foreach (var t in Techniques)
            {
                if (!t.Enabled) continue;
                radius = TechniqueReach(t, scale, radius);
            }
            return Math.Min(radius, 30000);
        }
    }

    /// <summary>
    /// <paramref name="radius"/> raised to the reach of each of the technique's emitters (<paramref name="scale"/>: the scale's largest component).
    /// </summary>
    float TechniqueReach(PuTechniqueDef t, float scale, float radius)
    {
        float growth = ScaleGrowth(t, scale);
        float force = t.Affectors.Where(a => a.Type == PuAffectorType.LinearForce).Select(a => a.Force.Length()).DefaultIfEmpty(0).Sum();
        foreach (var e in t.Emitters)
        {
            float reach = EmitterReach(t, e, scale, growth, force);
            radius = Math.Max(radius, reach);
        }
        return radius;
    }

    /// <summary>
    /// How fast the technique's scale affectors grow a particle at most (per second, scaled).
    /// </summary>
    static float ScaleGrowth(PuTechniqueDef t, float scale)
    {
        float growth = 0;
        foreach (var a in t.Affectors.Where(a => a.Type == PuAffectorType.Scale))
            growth += (a.ScaleXyz is not null ? [a.ScaleXyz] : new[] { a.ScaleX, a.ScaleY, a.ScaleZ })
                .Where(d => d is not null).Select(d => Math.Max(Math.Abs(d!.Range.Min), Math.Abs(d.Range.Max)))
                    .DefaultIfEmpty(0).Max() * scale;
        return growth;
    }

    /// <summary>
    /// How far an emitter's particle gets: its position and extent, its speed over its life, the linear forces' reach, its size and its growth.
    /// </summary>
    float EmitterReach(PuTechniqueDef t, PuEmitterDef e, float scale, float growth, float force)
    {
        float life = e.Life.Range.Max;
        float extent = e.Type switch
        {
            PuEmitterType.Box => e.BoxSize.Length() * 0.5f * scale,
            PuEmitterType.Circle or PuEmitterType.SphereSurface => e.Radius * scale,
            PuEmitterType.Line => e.End.Length() * scale,
            _ => 0,
        };
        float speed = Math.Max(Math.Abs(e.Velocity.Range.Min), Math.Abs(e.Velocity.Range.Max)) * ScaleVelocity;
        float size = (e.AllDimensions is not null ? [e.AllDimensions] : new[] { e.Width, e.Height })
            .Where(d => d is not null).Select(d => d!.Range.Max).DefaultIfEmpty(Math.Max(t.DefaultWidth, t.DefaultHeight)).Max() * scale;
        return (t.Position + e.Position * Scale).Length() + extent + speed * life
            + 0.5f * force * life * life + size + growth * life;
    }

    #endregion

    #region Building

    /// <summary>
    /// Builds the typed system from a parsed <c>system</c> block.
    /// </summary>
    public static PuSystemDef From(PuNode system, string file = "")
    {
        var scale = system.Vec3("scale") ?? Vector3.One;
        (float, float)? ff = system.Find("fast_forward") is { Values.Length: >= 2 } f
            && PuProperty.TryFloat(f.Values[0], out float ft) && PuProperty.TryFloat(f.Values[1], out float fi)
            ? (ft, fi) : null;
        return new PuSystemDef
        {
            Name = system.Name,
            File = file,
            Source = system,
            Category = system.Word("category") ?? "",
            Scale = scale,
            ScaleVelocity = system.Float("scale_velocity", 1),
            FastForward = ff,
            IterationInterval = system.Float("iteration_interval"),
            KeepLocal = system.Bool("keep_local"),
            Techniques = [.. system.Blocks("technique").Select(Technique)],
        };
    }

    /// <summary>
    /// A <c>technique</c> block: its quotas, defaults, renderer (Billboard when it has none), emitters, affectors and observers.
    /// </summary>
    static PuTechniqueDef Technique(PuNode t)
    {
        var rendererNode = t.Blocks("renderer").LastOrDefault();
        return new PuTechniqueDef
        {
            Name = t.Name,
            Source = t,
            Enabled = t.Bool("enabled", true),
            VisualQuota = (int)t.Float("visual_particle_quota", 500),
            EmittedEmitterQuota = (int)t.Float("emitted_emitter_quota", 50),
            EmittedTechniqueQuota = (int)t.Float("emitted_technique_quota", 50),
            Material = t.Word("material") ?? "",
            DefaultWidth = t.Float("default_particle_width", 100),
            DefaultHeight = t.Float("default_particle_height", 100),
            DefaultDepth = t.Float("default_particle_depth", 100),
            Position = t.Vec3("position") ?? Vector3.Zero,
            KeepLocal = t.Bool("keep_local"),
            Renderer = rendererNode is null ? new PuRendererDef { TypeName = "Billboard" } : Renderer(rendererNode),
            Emitters = [.. t.Blocks("emitter").Select(Emitter)],
            Affectors = [.. t.Blocks("affector").Select(Affector)],
            Observers = [.. t.Blocks("observer").Select(Observer)],
        };
    }

    /// <summary>
    /// A <c>renderer</c> block.
    /// </summary>
    static PuRendererDef Renderer(PuNode r) => new()
    {
        TypeName = r.Type,
        BillboardType = r.Word("billboard_type") ?? "point",
        Origin = r.Word("billboard_origin") ?? "center",
        RotationType = r.Word("billboard_rotation_type") ?? "texcoord",
        CommonDirection = r.Vec3("common_direction") ?? Vector3.UnitZ,
        CommonUp = r.Vec3("common_up_vector") ?? Vector3.UnitY,
        Sorting = r.Bool("sorting"),
        AccurateFacing = r.Bool("accurate_facing"),
    };

    /// <summary>
    /// An <c>emitter</c> block, with the plugin's defaults for what it leaves out.
    /// </summary>
    static PuEmitterDef Emitter(PuNode e)
    {
        var type = EmitterType(e.Type);
        var emits = e.Find("emits");
        return new PuEmitterDef
        {
            Type = type,
            TypeName = e.Type,
            Name = e.Name,
            Source = e,
            Enabled = e.Bool("enabled", true),
            Rate = e.Dynamic("emission_rate") ?? PuDynamic.Fixed(10),
            Life = e.Dynamic("time_to_live") ?? PuDynamic.Fixed(3),
            Velocity = e.Dynamic("velocity") ?? PuDynamic.Fixed(100),
            Angle = e.Dynamic("angle") ?? PuDynamic.Fixed(0),
            Mass = e.Dynamic("mass") ?? PuDynamic.Fixed(1),
            Duration = e.Dynamic("duration"),
            RepeatDelay = e.Dynamic("repeat_delay"),
            Width = e.Dynamic("particle_width"),
            Height = e.Dynamic("particle_height"),
            Depth = e.Dynamic("particle_depth"),
            AllDimensions = e.Dynamic("all_particle_dimensions"),
            Position = e.Vec3("position") ?? Vector3.Zero,
            Direction = e.Vec3("direction") ?? Vector3.UnitY,
            Colour = e.Vec4("colour") ?? Vector4.One,
            ColourStart = e.Vec4("start_colour_range"),
            ColourEnd = e.Vec4("end_colour_range"),
            BoxSize = new Vector3(e.Float("box_width", 100), e.Float("box_height", 100), e.Float("box_depth", 100)),
            Radius = e.Float("radius", 100),
            Step = e.Float("step", 0.1f),
            EmitRandom = e.Bool("emit_random", true),
            AutoDirection = e.Bool("auto_direction"),
            ForceEmission = e.Bool("force_emission"),
            KeepLocal = e.Bool("keep_local"),
            End = e.Vec3("end") ?? Vector3.Zero,
            EmitsKind = emits is { Values.Length: >= 2 } ? emits.Values[0] : null,
            EmitsName = emits is { Values.Length: >= 2 } ? emits.Values[1] : null,
        };
    }

    /// <summary>
    /// The emitter type of a type name.
    /// </summary>
    static PuEmitterType EmitterType(string type) => type switch
    {
        "Box" => PuEmitterType.Box,
        "Circle" => PuEmitterType.Circle,
        "Point" => PuEmitterType.Point,
        "SphereSurface" => PuEmitterType.SphereSurface,
        "Line" => PuEmitterType.Line,
        "Slave" => PuEmitterType.Slave,
        _ => PuEmitterType.Unknown,
    };

    /// <summary>
    /// An <c>affector</c> block; the fields of every affector type are read, each type uses its own.
    /// </summary>
    static PuAffectorDef Affector(PuNode a)
    {
        var type = AffectorType(a.Type);
        var colours = TimeColours(a);
        return new PuAffectorDef
        {
            Type = type,
            TypeName = a.Type,
            Name = a.Name,
            Source = a,
            Enabled = a.Bool("enabled", true),
            Position = a.Vec3("position") ?? Vector3.Zero,
            ExcludedEmitters = [.. a.All("exclude_emitter").Where(p => p.Values.Length > 0).Select(p => p.Values[0])],
            MassAffector = a.Float("mass_affector"),
            TimeColours = colours,
            MultiplyColour = a.Word("colour_operation") == "multiply",
            ScaleX = a.Dynamic("x_scale"),
            ScaleY = a.Dynamic("y_scale"),
            ScaleZ = a.Dynamic("z_scale"),
            ScaleXyz = a.Dynamic("xyz_scale"),
            SinceStartSystem = a.Bool("since_start_system"),
            Rotation = a.Dynamic("rotation"),
            RotationSpeed = a.Dynamic("rotation_speed"),
            UseOwnRotation = a.Bool("use_own_rotation"),
            Force = a.Vec3("force_vector") ?? (a.Vec3("acceleration") ?? Vector3.Zero),
            AverageForce = a.Word("force_application") == "average",
            Strength = a.Dynamic("gravity") ?? a.Dynamic("acceleration"),
            MinFrequency = a.Float("min_frequency"),
            MaxFrequency = a.Float("max_frequency"),
            Axis = a.Vec3("rotation_axis") ?? Vector3.UnitY,
            MaxDeviation = new Vector3(a.Float("max_deviation_x"), a.Float("max_deviation_y"), a.Float("max_deviation_z")),
            TimeStep = a.Float("time_step"),
        };
    }

    /// <summary>
    /// The affector type of a type name.
    /// </summary>
    static PuAffectorType AffectorType(string type) => type switch
    {
        "Colour" => PuAffectorType.Colour,
        "Scale" => PuAffectorType.Scale,
        "TextureRotator" => PuAffectorType.TextureRotator,
        "LinearForce" => PuAffectorType.LinearForce,
        "Vortex" => PuAffectorType.Vortex,
        "Gravity" => PuAffectorType.Gravity,
        "Randomiser" => PuAffectorType.Randomiser,
        "SineForce" => PuAffectorType.SineForce,
        "ForceField" => PuAffectorType.ForceField,
        "Jet" => PuAffectorType.Jet,
        "Align" => PuAffectorType.Align,
        "FlockCentering" => PuAffectorType.FlockCentering,
        "GeometryRotator" => PuAffectorType.GeometryRotator,
        "ScaleVelocity" => PuAffectorType.ScaleVelocity,
        _ => PuAffectorType.Unknown,
    };

    /// <summary>
    /// The <c>time_colour</c> keys (time, r, g, b, a), sorted by time; keys with fewer or unreadable values are left out.
    /// </summary>
    static List<(float, Vector4)> TimeColours(PuNode a)
    {
        var colours = new List<(float, Vector4)>();
        foreach (var p in a.All("time_colour"))
            if (p.Values.Length >= 5 && PuProperty.TryFloat(p.Values[0], out float t)
                && PuProperty.TryFloat(p.Values[1], out float r) && PuProperty.TryFloat(p.Values[2], out float g)
                && PuProperty.TryFloat(p.Values[3], out float b) && PuProperty.TryFloat(p.Values[4], out float al))
                colours.Add((t, new Vector4(r, g, b, al)));
        colours.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return colours;
    }

    /// <summary>
    /// An <c>observer</c> block, with its condition and its handlers' types.
    /// </summary>
    static PuObserverDef Observer(PuNode o)
    {
        var type = ObserverType(o.Type);
        var (axis, compare, value) = Condition(o);
        return new PuObserverDef
        {
            Type = type,
            TypeName = o.Type,
            Source = o,
            Enabled = o.Bool("enabled", true),
            Interval = o.Float("observe_interval"),
            Axis = axis,
            Compare = compare,
            Value = value,
            SinceStartSystem = o.Bool("since_start_system"),
            Handlers = [.. o.Blocks("handler").Select(h => h.Type)],
        };
    }

    /// <summary>
    /// The observer type of a type name.
    /// </summary>
    static PuObserverType ObserverType(string type) => type switch
    {
        "OnPosition" => PuObserverType.OnPosition,
        "OnTime" => PuObserverType.OnTime,
        "OnClear" => PuObserverType.OnClear,
        "OnEmission" => PuObserverType.OnEmission,
        _ => PuObserverType.Unknown,
    };

    /// <summary>
    /// The observer's condition: the axis of the last <c>position_x|y|z</c> (-1 without one), and the comparison and value of the last
    /// <c>position_*</c> or <c>on_time</c>.
    /// </summary>
    static (int Axis, PuCompare Compare, float Value) Condition(PuNode o)
    {
        int axis = -1;
        PuCompare compare = PuCompare.LessThan;
        float value = 0;
        foreach (var p in o.Properties)
        {
            if (p.Name is "position_x" or "position_y" or "position_z" && p.Values.Length >= 2)
            {
                axis = p.Name[^1] - 'x';
                (compare, value) = Compare(p.Values);
            }
            else if (p.Name == "on_time" && p.Values.Length >= 2) (compare, value) = Compare(p.Values);
        }
        return (axis, compare, value);
    }

    /// <summary>
    /// A condition's comparison (<c>less_than</c>, <c>greater_than</c>, else equals) and value (0 when unreadable).
    /// </summary>
    static (PuCompare, float) Compare(string[] v)
    {
        var c = v[0] switch { "less_than" => PuCompare.LessThan, "greater_than" => PuCompare.GreaterThan, _ => PuCompare.Equals };
        return (c, PuProperty.TryFloat(v[1], out float f) ? f : 0);
    }

    #endregion
}