namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// Sequential intra-frame phases during which a transient render target is live. Two transient targets whose
/// <see cref="TransientLifetime"/> intervals do not overlap may alias the same device-local optimal-image memory on
/// Vulkan. Never assign a cross-frame history or persistent target to a transient phase.
/// </summary>
public enum TransientPhase
{
    /// <summary>
    /// Water reflection pass (<c>ReflectionPass</c>): MSAA colour and depth targets, dead once resolved into the
    /// persistent reflection colour before the main scene begins.
    /// </summary>
    Reflection = 0,

    /// <summary>
    /// Opaque scene prepass, late terrain resolve and projected blood depth captures: dead before characters and water.
    /// </summary>
    OpaqueScene = 1,

    /// <summary>
    /// Character visibility buffer (<c>CharacterVisibility</c>): written and resolved inside character rendering.
    /// </summary>
    Characters = 2,

    /// <summary>
    /// Traced sun shadow mask and penumbra scratch: live during <c>TraceSun</c>, dead before water.
    /// </summary>
    TracedSun = 3,

    /// <summary>
    /// Water scene refraction and depth copies: captured and drawn during <c>DrawWater</c>.
    /// </summary>
    Water = 4,

    /// <summary>
    /// Deferred lamp shadow tracing and filtering in <c>FinishScene</c>.
    /// </summary>
    LampShadows = 5,

    /// <summary>
    /// Deferred character occlusion gather scratch in <c>FinishScene</c>.
    /// </summary>
    CharacterOcclusion = 6,

    /// <summary>
    /// Screen-space reflections (SSR hit and shade targets) in <c>FinishScene</c>.
    /// </summary>
    Reflections = 7,

    /// <summary>
    /// See-through glass refraction copy (<c>GlassScene</c>) in <c>FinishScene</c>.
    /// </summary>
    SeeThrough = 8,

    /// <summary>
    /// Placed fog volumes, scene layers and ambient occlusion (GTAO/SSAO) in <c>FinishScene</c>.
    /// </summary>
    FogAndAo = 9,

    /// <summary>
    /// Full- and low-resolution soft particle targets in <c>DrawParticles</c>.
    /// </summary>
    Particles = 10,

    /// <summary>
    /// Post-upscale chain (depth of field, late fog output, LDR, FXAA, SMAA) in <c>PostProcess.End</c>.
    /// </summary>
    PostUpscale = 11,
}

/// <summary>
/// Inclusive intra-frame phase range <c>[First, Last]</c> during which a transient render target holds live data.
/// </summary>
public readonly record struct TransientLifetime(TransientPhase First, TransientPhase Last)
{
    /// <summary>
    /// A transient lifetime spanning a single phase.
    /// </summary>
    public static TransientLifetime Single(TransientPhase phase) => new(phase, phase);

    /// <summary>
    /// Whether this lifetime overlaps <paramref name="other"/>.
    /// </summary>
    public bool Overlaps(TransientLifetime other) => First <= other.Last && other.First <= Last;
}
