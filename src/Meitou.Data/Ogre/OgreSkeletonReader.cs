using static Meitou.Data.Ogre.OgreSkeletonChunk;

namespace Meitou.Data.Ogre;

/// <summary>
/// Reads Ogre <c>.skeleton</c> files, serializer versions 1.80 and 1.10 (everything Kenshi ships).
/// Follows the structure of Ogre's own SkeletonSerializer (MIT): chunks are parsed field by field.
/// Bone chunk lengths never count the bone's name, so they can't be used to step over a bone; Ogre
/// only compares them against a fixed size to tell whether a scale follows.
/// </summary>
public static class OgreSkeletonReader
{
    public const string Version1_80 = "[Serializer_v1.80]";
    public const string Version1_10 = "[Serializer_v1.10]";

    /// <summary>Chunk header, handle, position and orientation; the name is not counted. Ogre's <c>calcBoneSizeWithoutScale</c>.</summary>
    public const int BoneLengthWithoutScale = OgreStream.ChunkHeaderSize + 2 + 3 * 4 + 4 * 4;

    /// <summary>Chunk header, time, rotation and translation. Ogre's <c>calcKeyFrameSizeWithoutScale</c>.</summary>
    public const int KeyFrameLengthWithoutScale = OgreStream.ChunkHeaderSize + 4 + 4 * 4 + 3 * 4;

    public static OgreSkeleton ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static OgreSkeleton Read(byte[] data)
    {
        var s = new OgreStream(data);
        try
        {
            var skeleton = new OgreSkeleton { Version = s.ReadHeader() };
            if (skeleton.Version is not (Version1_80 or Version1_10))
                throw new OgreFormatException($"Unsupported skeleton version {skeleton.Version}.", 0);

            var bones = new Dictionary<ushort, OgreBone>();
            while (!s.Eof)
            {
                int start = s.Position;
                var (id, length) = s.ReadChunk();
                switch ((OgreSkeletonChunk)id)
                {
                    case BlendMode:
                        skeleton.StoredBlendMode = (OgreSkeletonBlendMode)s.ReadUInt16();
                        break;
                    case Bone:
                        var bone = ReadBone(s, length);
                        if (!bones.TryAdd(bone.Handle, bone))
                            throw new OgreFormatException($"Duplicate bone handle {bone.Handle}.", start);
                        skeleton.Bones.Add(bone);
                        break;
                    case BoneParent:
                        ushort child = s.ReadUInt16(), parent = s.ReadUInt16();
                        if (!bones.TryGetValue(child, out var childBone) || !bones.ContainsKey(parent))
                            throw new OgreFormatException($"Bone parent {child} -> {parent} names a missing bone.", start);
                        if (childBone.Parent is not null)
                            throw new OgreFormatException($"Bone {child} has two parents.", start);
                        childBone.Parent = parent;
                        break;
                    case Animation:
                        skeleton.Animations.Add(ReadAnimation(s, skeleton));
                        CheckLength(s, skeleton, start, length);
                        break;
                    case AnimationLink:
                        skeleton.AnimationLinks.Add(new OgreAnimationLink(s.ReadString(), s.ReadFloat()));
                        break;
                    default:
                        // Ogre ignores an unknown id without skipping its body, which would misread the rest.
                        throw new OgreFormatException($"Unexpected top-level chunk 0x{id:X4}.", start);
                }
            }
            return skeleton;
        }
        catch (IndexOutOfRangeException e)
        {
            throw new OgreFormatException("Unexpected end of file.", s.Position, e);
        }
        catch (ArgumentException e)
        {
            throw new OgreFormatException("Unexpected end of file.", s.Position, e);
        }
    }

    static OgreBone ReadBone(OgreStream s, int length)
    {
        var bone = new OgreBone
        {
            Name = s.ReadString(),
            Handle = s.ReadUInt16(),
            Position = s.ReadVector3(),
            Orientation = s.ReadQuaternion(),
        };
        if (length > BoneLengthWithoutScale)
        {
            bone.Scale = s.ReadVector3();
            bone.HasScale = true;
        }
        return bone;
    }

    static OgreAnimation ReadAnimation(OgreStream s, OgreSkeleton skeleton)
    {
        var animation = new OgreAnimation { Name = s.ReadString(), Length = s.ReadFloat() };

        // Optional base info, only as the first child.
        int start = s.Position;
        if (s.TryReadChunk([AnimationBaseInfo], out _, out int length))
        {
            animation.Base = new OgreAnimationBase(s.ReadString(), s.ReadFloat());
            CheckLength(s, skeleton, start, length);
        }

        start = s.Position;
        while (s.TryReadChunk([AnimationTrack], out _, out length))
        {
            var track = new OgreAnimationTrack { Bone = s.ReadUInt16() };
            int keyStart = s.Position;
            while (s.TryReadChunk([AnimationKeyFrame], out _, out int keyLength))
            {
                float time = s.ReadFloat();
                var rotation = s.ReadQuaternion();
                var translation = s.ReadVector3();
                bool hasScale = keyLength > KeyFrameLengthWithoutScale;
                var scale = hasScale ? s.ReadVector3() : System.Numerics.Vector3.One;
                track.KeyFrames.Add(new OgreKeyFrame(time, rotation, translation, scale, hasScale));
                CheckLength(s, skeleton, keyStart, keyLength);
                keyStart = s.Position;
            }
            animation.Tracks.Add(track);
            CheckLength(s, skeleton, start, length);
            start = s.Position;
        }
        return animation;
    }

    static void CheckLength(OgreStream s, OgreSkeleton skeleton, int start, int length)
    {
        if (s.Position - start != length) skeleton.ChunkLengthMismatches++;
    }
}
