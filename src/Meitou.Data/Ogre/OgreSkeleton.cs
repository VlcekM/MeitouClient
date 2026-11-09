using System.Numerics;

namespace Meitou.Data.Ogre;

/// <summary>
/// An Ogre <c>.skeleton</c> file (Ogre's "v1" skeleton format). Layout: docs/formats/ogre-skeleton.md.
/// </summary>
public sealed class OgreSkeleton
{
    /// <summary>
    /// Serializer version from the file header, e.g. <c>[Serializer_v1.80]</c>.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// The stored blend mode, or null when the file has no blend-mode chunk (Ogre then uses <see cref="OgreSkeletonBlendMode.Average"/>).
    /// </summary>
    public OgreSkeletonBlendMode? StoredBlendMode { get; set; }

    /// <summary>
    /// The blend mode in effect: <see cref="StoredBlendMode"/>, else <see cref="OgreSkeletonBlendMode.Average"/>.
    /// </summary>
    public OgreSkeletonBlendMode BlendMode => StoredBlendMode ?? OgreSkeletonBlendMode.Average;

    /// <summary>
    /// Bones in file order (normally also handle order).
    /// </summary>
    public List<OgreBone> Bones { get; } = [];

    /// <summary>
    /// The skeleton's animations in file order.
    /// </summary>
    public List<OgreAnimation> Animations { get; } = [];

    /// <summary>
    /// Other skeletons whose animations this one borrows.
    /// </summary>
    public List<OgreAnimationLink> AnimationLinks { get; } = [];

    /// <summary>
    /// Animation, track and keyframe chunks whose stored length differs from the bytes they actually hold. The reader parses by structure, so these
    /// don't affect the result. (Bone chunks are not counted: their length never includes the bone name, by design of the format.)
    /// </summary>
    public int ChunkLengthMismatches { get; set; }

    /// <summary>
    /// The bone with <paramref name="handle"/>; null when there is none.
    /// </summary>
    public OgreBone? FindBone(ushort handle) => Bones.FirstOrDefault(b => b.Handle == handle);
}

/// <summary>
/// A bone: its name, handle, parent and binding pose.
/// </summary>
public sealed class OgreBone
{
    /// <summary>
    /// The bone's name.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>
    /// The bone's handle, which tracks, parents and bone assignments refer to it by.
    /// </summary>
    public ushort Handle { get; set; }

    /// <summary>
    /// Handle of the parent bone, or null for a root.
    /// </summary>
    public ushort? Parent { get; set; }

    /// <summary>
    /// Binding pose relative to the parent.
    /// </summary>
    public Vector3 Position { get; set; }

    /// <summary>
    /// Binding orientation relative to the parent.
    /// </summary>
    public Quaternion Orientation { get; set; } = Quaternion.Identity;
    /// <summary>
    /// Binding scale relative to the parent; (1,1,1) when not stored.
    /// </summary>
    public Vector3 Scale { get; set; } = Vector3.One;

    /// <summary>
    /// True when the file stored a scale (the writer omits a scale of exactly 1,1,1).
    /// </summary>
    public bool HasScale { get; set; }
}

/// <summary>
/// A skeletal animation: its name, length, optional additive base and bone tracks.
/// </summary>
public sealed class OgreAnimation
{
    /// <summary>
    /// The animation's name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Length in seconds.
    /// </summary>
    public float Length { get; set; }

    /// <summary>
    /// Base keyframe for additive blending, if the file has one.
    /// </summary>
    public OgreAnimationBase? Base { get; set; }

    /// <summary>
    /// The animation's bone tracks in file order.
    /// </summary>
    public List<OgreAnimationTrack> Tracks { get; } = [];
}

/// <param name="AnimationName">Animation holding the base pose; empty means this animation itself.</param>
/// <param name="Time">Time of the base keyframe in that animation.</param>
public sealed record OgreAnimationBase(string AnimationName, float Time);

/// <summary>
/// The keyframes of one bone in an animation.
/// </summary>
public sealed class OgreAnimationTrack
{
    /// <summary>
    /// Handle of the bone this track moves.
    /// </summary>
    public ushort Bone { get; set; }

    /// <summary>
    /// The track's keyframes in file order.
    /// </summary>
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

/// <summary>
/// How the animations playing on a skeleton are combined (chunk 0x1010).
/// </summary>
public enum OgreSkeletonBlendMode : ushort
{
    /// <summary>
    /// Weights of animations on a bone are normalised to sum to at most 1.
    /// </summary>
    Average = 0,

    /// <summary>
    /// Weighted animations are added up.
    /// </summary>
    Cumulative = 1,
}

/// <summary>
/// Chunk ids of the <c>.skeleton</c> format.
/// </summary>
public enum OgreSkeletonChunk : ushort
{
    /// <summary>
    /// The file header with the serializer version.
    /// </summary>
    Header = 0x1000,
    /// <summary>
    /// The skeleton's blend mode.
    /// </summary>
    BlendMode = 0x1010,
    /// <summary>
    /// A bone: name, handle, position, orientation and optional scale.
    /// </summary>
    Bone = 0x2000,
    /// <summary>
    /// A bone's parent: child handle, parent handle.
    /// </summary>
    BoneParent = 0x3000,
    /// <summary>
    /// An animation: name and length, then its tracks.
    /// </summary>
    Animation = 0x4000,
    /// <summary>
    /// An animation's additive base: base animation name and time.
    /// </summary>
    AnimationBaseInfo = 0x4010,
    /// <summary>
    /// A bone track: the bone's handle, then its keyframes.
    /// </summary>
    AnimationTrack = 0x4100,
    /// <summary>
    /// A keyframe: time, rotation, translation and optional scale.
    /// </summary>
    AnimationKeyFrame = 0x4110,
    /// <summary>
    /// A link to another skeleton's animations: its file name and scale.
    /// </summary>
    AnimationLink = 0x5000,
}