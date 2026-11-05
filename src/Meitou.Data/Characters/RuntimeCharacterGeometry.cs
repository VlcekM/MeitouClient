using System.Numerics;
using Meitou.Data.Ogre;

namespace Meitou.Data.Characters;

/// <summary>
/// Optional generated geometry for one presentation variant. Its object identity is the renderer cache identity: keep the same instance while unchanged.
/// Meshes and their arrays must not be changed after publication. The original appearance still supplies the rig, shape, textures and animation.
/// </summary>
public sealed class RuntimeCharacterGeometry
{
    int retired;
    /// <summary>The producer no longer publishes this variant. Already submitted frames remain valid until the renderer retires their resources.</summary>
    public bool IsRetired => Volatile.Read(ref retired) != 0;
    public void Retire() => Interlocked.Exchange(ref retired, 1);
    /// <summary>Null keeps the appearance's body mesh. Generated meshes use the original body's skeleton and bone handles.</summary>
    public OgreMesh? BodyMesh { get; }

    /// <summary>Null keeps the original parts; otherwise this is the complete replacement list (empty removes all non-body parts).</summary>
    public IReadOnlyList<RuntimeCharacterPart>? Parts { get; }

    /// <summary>Optional complete programs, shared by identity across geometry variants.</summary>
    public RuntimeCharacterShaderSet? Shaders { get; }
    /// <summary>Preserve the generated body's float tangent and colour channels instead of interpreting them as native mesh metadata.</summary>
    public bool PreserveRawBodyVertexChannels { get; }
    public uint BodyFlags { get; }
    public uint BodyMaterialFlags { get; }
    /// <summary>Null inherits the body material's culling; true or false overrides it for this presentation variant.</summary>
    public bool? BodyDoubleSided { get; }

    public RuntimeCharacterGeometry(OgreMesh? bodyMesh = null, IEnumerable<RuntimeCharacterPart>? parts = null,
        RuntimeCharacterShaderSet? shaders = null, bool preserveRawBodyVertexChannels = false, uint bodyFlags = 0, uint bodyMaterialFlags = 0, bool? bodyDoubleSided = null)
    {
        RuntimeCharacterShaderData.ValidateFlags(bodyFlags);
        BodyMesh = bodyMesh;
        Parts = parts is null ? null : Array.AsReadOnly(parts.ToArray());
        if (Parts is not null) foreach (var part in Parts) RuntimeCharacterShaderData.ValidateFlags(part.DrawFlags);
        Shaders = shaders;
        PreserveRawBodyVertexChannels = preserveRawBodyVertexChannels;
        BodyFlags = bodyFlags;
        BodyMaterialFlags = bodyMaterialFlags;
        BodyDoubleSided = bodyDoubleSided;
    }
}

/// <summary>
/// A part with the usual attachment/material metadata and an optional generated mesh. Null Mesh loads Part.Mesh normally.
/// With a supplied Mesh, VertexColours preserves generated colour values and enables ordinary item vertex-colour shading; meshes remain immutable after publication.
/// </summary>
public sealed record RuntimeCharacterPart(CharacterPart Part, OgreMesh? Mesh = null, bool VertexColours = false)
{
    /// <summary>Optional decreasing projected-diameter thresholds in pixels, one per supplied Mesh.Lod level after full detail. The list is immutable after publication.</summary>
    public IReadOnlyList<float>? ScreenLodThresholds { get; init; }
    /// <summary>Additional instance bits; native interior/visibility bits are reserved.</summary>
    public uint DrawFlags { get; init; }
    /// <summary>Whether this generated part appears in ordinary portraits, which use the host's standard shader.</summary>
    public bool ShowInPortrait { get; init; } = true;
    /// <summary>Whether this part contributes to shadow depth passes; colour, motion and portraits are unchanged.</summary>
    public bool CastsShadows { get; init; } = true;
    float minimumScreenPixels;
    /// <summary>Hide the part below this projected actor diameter in world passes; zero keeps ordinary visibility. Portraits use ShowInPortrait separately.</summary>
    public float MinimumScreenPixels
    {
        get => minimumScreenPixels;
        init
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(MinimumScreenPixels), value, "A projected-size threshold must be finite and nonnegative.");
            minimumScreenPixels = value;
        }
    }

    /// <summary>Additional raw material bits, ORed with the ordinary material's flags.</summary>
    public uint MaterialFlags { get; init; }
    /// <summary>Null inherits the ordinary material's culling; true or false overrides it for this part.</summary>
    public bool? DoubleSided { get; init; }
    /// <summary>Use full-float tangent/colour channels for a supplied mesh, without native curvature or visibility rewriting.</summary>
    public bool PreserveRawVertexChannels { get; init; }
}

/// <summary>Six complete native GLSL stages using the character descriptor/instance ABI. Cache identity is object identity; reuse while unchanged.</summary>
public sealed class RuntimeCharacterShaderSet
{
    public string ColourVertex { get; }
    public string ColourFragment { get; }
    public string DepthVertex { get; }
    public string DepthFragment { get; }
    public string MotionVertex { get; }
    public string MotionFragment { get; }

    public RuntimeCharacterShaderSet(string colourVertex, string colourFragment, string depthVertex, string depthFragment, string motionVertex, string motionFragment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(colourVertex);
        ArgumentException.ThrowIfNullOrWhiteSpace(colourFragment);
        ArgumentException.ThrowIfNullOrWhiteSpace(depthVertex);
        ArgumentException.ThrowIfNullOrWhiteSpace(depthFragment);
        ArgumentException.ThrowIfNullOrWhiteSpace(motionVertex);
        ArgumentException.ThrowIfNullOrWhiteSpace(motionFragment);
        (ColourVertex, ColourFragment, DepthVertex, DepthFragment, MotionVertex, MotionFragment) =
            (colourVertex, colourFragment, depthVertex, depthFragment, motionVertex, motionFragment);
    }
}

/// <summary>Immutable opaque matrices immediately before the actor's BoneBase, plus additional instance flags. Matrix bits are not interpreted.</summary>
public sealed class RuntimeCharacterShaderData
{
    /// <summary>Host-owned instance bits: interior (0), hidden limbs/boots/parts (12–27), head visibility (28–29).</summary>
    public const uint ReservedInstanceFlags = 0x3FFFF001u;
    readonly Matrix4x4[] prefix;
    public IReadOnlyList<Matrix4x4> Prefix { get; }
    public uint Flags { get; }
    public RuntimeCharacterShaderData(IEnumerable<Matrix4x4> prefix, uint flags = 0)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ValidateFlags(flags);
        this.prefix = prefix.ToArray();
        Prefix = Array.AsReadOnly(this.prefix);
        Flags = flags;
    }
    public void CopyPrefixTo(Span<Matrix4x4> destination) => prefix.AsSpan().CopyTo(destination);
    internal static void ValidateFlags(uint flags)
    {
        if ((flags & ReservedInstanceFlags) != 0) throw new ArgumentOutOfRangeException(nameof(flags), "Runtime flags overlap host-owned visibility bits.");
    }
}
