using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Ogre;

public class OgreSkeletonReaderTests
{
    /// <summary>
    /// Writes Ogre serializer chunks: id, length (by default the true size including the 6-byte header), body.
    /// </summary>
    sealed class Builder
    {
        readonly MemoryStream _ms = new();
        readonly BinaryWriter _w;
        readonly Stack<long> _open = new();

        public Builder(string version)
        {
            _w = new BinaryWriter(_ms);
            _w.Write((ushort)0x1000);
            String(version);
        }

        public Builder Begin(OgreSkeletonChunk id)
        {
            _w.Write((ushort)id);
            _open.Push(_ms.Position);
            _w.Write(0u);
            return this;
        }

        /// <param name="declared">Length to store instead of the real one (Ogre's bone chunks leave out the name).</param>
        public Builder End(int? declared = null)
        {
            long at = _open.Pop(), end = _ms.Position;
            _ms.Position = at;
            _w.Write((uint)(declared ?? (int)(end - at + 2)));
            _ms.Position = end;
            return this;
        }

        /// <summary>
        /// A bone chunk as Ogre writes it: the length counts everything but the name.
        /// </summary>
        public Builder Bone(string name, ushort handle, Vector3 position, Quaternion orientation, Vector3? scale = null)
        {
            Begin(OgreSkeletonChunk.Bone).String(name).U16(handle).F32(position.X, position.Y, position.Z)
                .F32(orientation.X, orientation.Y, orientation.Z, orientation.W);
            if (scale is { } sc) F32(sc.X, sc.Y, sc.Z);

            return End(OgreSkeletonReader.BoneLengthWithoutScale + (scale is null ? 0 : 12));
        }

        public Builder KeyFrame(float time, Quaternion rotation, Vector3 translation, Vector3? scale = null)
        {
            Begin(OgreSkeletonChunk.AnimationKeyFrame).F32(time, rotation.X, rotation.Y, rotation.Z, rotation.W, translation.X, translation.Y,
                translation.Z);
            if (scale is { } sc) F32(sc.X, sc.Y, sc.Z);

            return End();
        }

        public Builder U16(ushort v)
        {
            _w.Write(v);
            return this;
        }

        public Builder F32(params float[] v)
        {
            foreach (var f in v) _w.Write(f);

            return this;
        }

        public Builder String(string s)
        {
            _w.Write(Encoding.UTF8.GetBytes(s + "\n"));
            return this;
        }

        public byte[] ToArray() => _ms.ToArray();
    }

    static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);

    static Builder TwoBones(string version = OgreSkeletonReader.Version180) =>
        new Builder(version)
            .Begin(OgreSkeletonChunk.BlendMode).U16(1).End()
            .Bone("Bip01 with a long name", 0, new Vector3(1, 2, 3), Turn)
            .Bone("Bip01 Spine", 1, new Vector3(0, 1, 0), Quaternion.Identity, new Vector3(2, 2, 2))
            .Begin(OgreSkeletonChunk.BoneParent).U16(1).U16(0).End();

    [Fact]
    public void Reads_bones_with_and_without_scale_and_their_parents()
    {
        var skeleton = OgreSkeletonReader.Read(TwoBones().ToArray());

        Assert.Equal(OgreSkeletonBlendMode.Cumulative, skeleton.BlendMode);
        Assert.Equal(2, skeleton.Bones.Count);

        var root = skeleton.Bones[0];
        Assert.Equal("Bip01 with a long name", root.Name);
        Assert.Equal((ushort)0, root.Handle);
        Assert.Null(root.Parent);
        Assert.Equal(new Vector3(1, 2, 3), root.Position);
        Assert.Equal(Turn, root.Orientation);
        Assert.False(root.HasScale);
        Assert.Equal(Vector3.One, root.Scale);

        var spine = skeleton.FindBone(1)!;
        Assert.Equal("Bip01 Spine", spine.Name);
        Assert.Equal((ushort)0, spine.Parent);
        Assert.True(spine.HasScale);
        Assert.Equal(new Vector3(2, 2, 2), spine.Scale);
    }

    [Fact]
    public void Bone_scale_is_decided_by_the_stored_length_not_the_name()
    {
        // A name-inclusive length would look like "has scale" to Ogre; the reader must follow Ogre and
        // read 12 more bytes. Here those bytes are the next chunk, which then breaks parsing.
        var bytes = new Builder(OgreSkeletonReader.Version180)
            .Begin(OgreSkeletonChunk.Bone).String("Bip01").U16(0).F32(0, 0, 0, 0, 0, 0, 1).End()
            .Begin(OgreSkeletonChunk.Bone).String("Bip02").U16(1).F32(0, 0, 0, 0, 0, 0, 1).End(OgreSkeletonReader.BoneLengthWithoutScale)
            .ToArray();
        Assert.Throws<OgreFormatException>(() => OgreSkeletonReader.Read(bytes));
    }

    [Fact]
    public void Reads_animation_tracks_keyframes_base_info_and_links()
    {
        var skeleton = OgreSkeletonReader.Read(TwoBones()
            .Begin(OgreSkeletonChunk.Animation).String("walk").F32(2)
                .Begin(OgreSkeletonChunk.AnimationBaseInfo).String("").F32(0.5f).End()
                .Begin(OgreSkeletonChunk.AnimationTrack).U16(1)
                    .KeyFrame(0, Quaternion.Identity, Vector3.Zero)
                    .KeyFrame(2, Turn, new Vector3(0, 0, 1), new Vector3(1, 3, 1))
                .End()
                .Begin(OgreSkeletonChunk.AnimationTrack).U16(0).End()
            .End()
            .Begin(OgreSkeletonChunk.Animation).String("idle").F32(1).End()
            .Begin(OgreSkeletonChunk.AnimationLink).String("other.skeleton").F32(0.5f).End()
            .ToArray());

        Assert.Equal(2, skeleton.Animations.Count);
        var walk = skeleton.Animations[0];
        Assert.Equal("walk", walk.Name);
        Assert.Equal(2f, walk.Length);
        Assert.Equal(new OgreAnimationBase("", 0.5f), walk.Base);
        Assert.Equal(2, walk.Tracks.Count);
        Assert.Equal((ushort)1, walk.Tracks[0].Bone);
        Assert.Equal(
            [new OgreKeyFrame(0, Quaternion.Identity, Vector3.Zero, Vector3.One, false),
             new OgreKeyFrame(2, Turn, new Vector3(0, 0, 1), new Vector3(1, 3, 1), true)],
            walk.Tracks[0].KeyFrames);
        Assert.Empty(walk.Tracks[1].KeyFrames);

        Assert.Equal("idle", skeleton.Animations[1].Name);
        Assert.Empty(skeleton.Animations[1].Tracks);
        Assert.Equal(new OgreAnimationLink("other.skeleton", 0.5f), Assert.Single(skeleton.AnimationLinks));
        Assert.Equal(0, skeleton.ChunkLengthMismatches);
    }

    static IEnumerable<string> SkeletonFiles(GameInstall install) =>
        Directory.EnumerateFiles(install.DataDirectory, "*.skeleton", SearchOption.AllDirectories);

    /// <summary>
    /// Every non-empty base-game skeleton parses to the last byte and is internally consistent: contiguous
    /// handles from 0, unique names, a parent forest without cycles, tracks on existing bones (one per bone),
    /// keyframe times ordered and inside the animation, unit quaternions.
    /// </summary>
    [Fact]
    [Slow]
    public void Reads_every_base_game_skeleton()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var files = SkeletonFiles(install!).ToList();
        Assert.NotEmpty(files);

        // The base game ships one zero-byte skeleton (items/armour/meshes/trousers_m.mesh.skeleton); Ogre
        // can't load it either. Anything else empty would be news.
        var empty = files.Where(f => new FileInfo(f).Length == 0).Select(f => Path.GetFileName(f)).ToList();
        Assert.Equal(["trousers_m.mesh.skeleton"], empty);

        var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(files.Where(f => new FileInfo(f).Length > 0), file =>
        {
            var name = Path.GetRelativePath(install!.DataDirectory, file);
            OgreSkeleton skeleton;

            try
            {
                skeleton = OgreSkeletonReader.ReadFile(file);
            }
            catch (OgreFormatException e)
            {
                problems.Add($"{name}: {e.Message}");
                return;
            }

            foreach (var p in Check(skeleton)) problems.Add($"{name}: {p}");
        });
        Assert.True(problems.IsEmpty, string.Join(Environment.NewLine, problems.Order()));
    }

    static IEnumerable<string> Check(OgreSkeleton skeleton)
    {
        const float tolerance = 1e-3f;
        var handles = skeleton.Bones.Select(b => (int)b.Handle).Order().ToList();
        if (!handles.SequenceEqual(Enumerable.Range(0, handles.Count)))
            yield return "bone handles are not contiguous from 0";
        if (skeleton.ChunkLengthMismatches > 0)
            yield return $"{skeleton.ChunkLengthMismatches} animation/track/keyframe chunks with a wrong stored length";
        if (skeleton.Bones.Count > 256) yield return $"{skeleton.Bones.Count} bones (Ogre's limit is 256)";
        if (skeleton.Bones.Select(b => b.Name).Distinct().Count() != skeleton.Bones.Count)
            yield return "duplicate bone names";

        var byHandle = skeleton.Bones.ToDictionary(b => b.Handle);

        foreach (var bone in skeleton.Bones)
        {
            var seen = new HashSet<ushort>();

            for (var b = bone; b.Parent is { } parent; b = byHandle[parent])
            {
                if (!seen.Add(b.Handle))
                {
                    yield return $"bone {bone.Name} is in a parent cycle";
                    break;
                }
            }

            if (MathF.Abs(bone.Orientation.Length() - 1) > tolerance)
                yield return $"bone {bone.Name} orientation {bone.Orientation} is not unit length";
        }

        foreach (var animation in skeleton.Animations)
        {
            if (animation.Tracks.Select(t => t.Bone).Distinct().Count() != animation.Tracks.Count)
                yield return $"animation {animation.Name} has two tracks for one bone";

            foreach (var track in animation.Tracks)
            {
                if (!byHandle.ContainsKey(track.Bone))
                    yield return $"animation {animation.Name} has a track for missing bone {track.Bone}";

                float previous = float.NegativeInfinity;

                foreach (var key in track.KeyFrames)
                {
                    if (key.Time < -tolerance || key.Time > animation.Length + tolerance)
                        yield return $"animation {animation.Name} bone {track.Bone}: keyframe at {key.Time} outside 0..{animation.Length}";
                    if (key.Time < previous)
                        yield return $"animation {animation.Name} bone {track.Bone}: keyframe times go back ({previous} -> {key.Time})";
                    if (MathF.Abs(key.Rotation.Length() - 1) > tolerance)
                        yield return $"animation {animation.Name} bone {track.Bone}: rotation {key.Rotation} at {key.Time} is not unit length";

                    previous = key.Time;
                }
            }
        }
    }
}
