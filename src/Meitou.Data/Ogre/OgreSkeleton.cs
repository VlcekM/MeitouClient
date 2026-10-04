using System.Numerics;

namespace Meitou.Data.Ogre;

/// <summary>An Ogre <c>.skeleton</c> file (Ogre's "v1" skeleton format). Layout: docs/formats/ogre-skeleton.md.</summary>
public sealed class OgreSkeleton
{
    /// <summary>Serializer version from the file header, e.g. <c>[Serializer_v1.80]</c>.</summary>
    public string Version { get; set; } = "";

    /// <summary>The stored blend mode, or null when the file has no blend-mode chunk (Ogre then uses <see cref="OgreSkeletonBlendMode.Average"/>).</summary>
    public OgreSkeletonBlendMode? StoredBlendMode { get; set; }

    public OgreSkeletonBlendMode BlendMode => StoredBlendMode ?? OgreSkeletonBlendMode.Average;

    /// <summary>Bones in file order (normally also handle order).</summary>
    public List<OgreBone> Bones { get; } = [];

    public List<OgreAnimation> Animations { get; } = [];

    /// <summary>Other skeletons whose animations this one borrows.</summary>
    public List<OgreAnimationLink> AnimationLinks { get; } = [];

    /// <summary>
    /// Animation, track and keyframe chunks whose stored length differs from the bytes they actually
    /// hold. The reader parses by structure, so these don't affect the result. (Bone chunks are not
    /// counted: their length never includes the bone name, by design of the format.)
    /// </summary>
    public int ChunkLengthMismatches { get; set; }

    public OgreBone? FindBone(ushort handle) => Bones.FirstOrDefault(b => b.Handle == handle);
}

public sealed class OgreBone
{
    public string Name { get; set; } = "";
    public ushort Handle { get; set; }

    /// <summary>Handle of the parent bone, or null for a root.</summary>
    public ushort? Parent { get; set; }

    /// <summary>Binding pose relative to the parent.</summary>
    public Vector3 Position { get; set; }

    public Quaternion Orientation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;

    /// <summary>True when the file stored a scale (the writer omits a scale of exactly 1,1,1).</summary>
    public bool HasScale { get; set; }
}

public sealed class OgreAnimation
{
    public string Name { get; set; } = "";

    /// <summary>Length in seconds.</summary>
    public float Length { get; set; }

    /// <summary>Base keyframe for additive blending, if the file has one.</summary>
    public OgreAnimationBase? Base { get; set; }

    public List<OgreAnimationTrack> Tracks { get; } = [];
}

/// <param name="AnimationName">Animation holding the base pose; empty means this animation itself.</param>
/// <param name="Time">Time of the base keyframe in that animation.</param>
public sealed record OgreAnimationBase(string AnimationName, float Time);

public sealed class OgreAnimationTrack
{
    /// <summary>Handle of the bone this track moves.</summary>
    public ushort Bone { get; set; }

    public List<OgreKeyFrame> KeyFrames { get; } = [];
}

/// <param name="Time">Seconds from the start of the animation.</param>
/// <param name="Rotation">Rotation relative to the bone's binding pose.</param>
/// <param name="Translation">Translation relative to the bone's binding pose.</param>
/// <param name="Scale">Scale; (1,1,1) when not stored.</param>
/// <param name="HasScale">True when the file stored a scale.</param>
public readonly record struct OgreKeyFrame(float Time, Quaternion Rotation, Vector3 Translation, Vector3 Scale, bool HasScale);

/// <param name="SkeletonName">File name of the other skeleton.</param>
/// <param name="Scale">Scale applied to that skeleton's translations.</param>
public sealed record OgreAnimationLink(string SkeletonName, float Scale);

public enum OgreSkeletonBlendMode : ushort
{
    /// <summary>Weights of animations on a bone are normalised to sum to at most 1.</summary>
    Average = 0,

    /// <summary>Weighted animations are added up.</summary>
    Cumulative = 1,
}

/// <summary>Chunk ids of the <c>.skeleton</c> format.</summary>
public enum OgreSkeletonChunk : ushort
{
    Header = 0x1000,
    BlendMode = 0x1010,
    Bone = 0x2000,
    BoneParent = 0x3000,
    Animation = 0x4000,
    AnimationBaseInfo = 0x4010,
    AnimationTrack = 0x4100,
    AnimationKeyFrame = 0x4110,
    AnimationLink = 0x5000,
}
