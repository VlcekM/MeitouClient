using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Ogre;

public class OgreSkeletonReaderTests
{
    /// <summary>Writes Ogre serializer chunks: id, length (by default the true size including the 6-byte header), body.</summary>
    sealed class Builder
    {
        readonly MemoryStream ms = new();
        readonly BinaryWriter w;
        readonly Stack<long> open = new();

        public Builder(string version)
        {
            w = new BinaryWriter(ms);
            w.Write((ushort)0x1000);
            String(version);
        }

        public Builder Begin(OgreSkeletonChunk id)
        {
            w.Write((ushort)id);
            open.Push(ms.Position);
            w.Write(0u);
            return this;
        }

        /// <param name="declared">Length to store instead of the real one (Ogre's bone chunks leave out the name).</param>
        public Builder End(int? declared = null)
        {
            long at = open.Pop(), end = ms.Position;
            ms.Position = at;
            w.Write((uint)(declared ?? (int)(end - at + 2)));
            ms.Position = end;
            return this;
        }

        /// <summary>A bone chunk as Ogre writes it: the length counts everything but the name.</summary>
        public Builder Bone(string name, ushort handle, Vector3 position, Quaternion orientation, Vector3? scale = null)
        {
            Begin(OgreSkeletonChunk.Bone).String(name).U16(handle).F32(position.X, position.Y, position.Z)
                .F32(orientation.X, orientation.Y, orientation.Z, orientation.W);
            if (scale is { } sc) F32(sc.X, sc.Y, sc.Z);
            return End(OgreSkeletonReader.BoneLengthWithoutScale + (scale is null ? 0 : 12));
        }

        public Builder KeyFrame(float time, Quaternion rotation, Vector3 translation, Vector3? scale = null)
        {
            Begin(OgreSkeletonChunk.AnimationKeyFrame).F32(time, rotation.X, rotation.Y, rotation.Z, rotation.W, translation.X, translation.Y, translation.Z);
            if (scale is { } sc) F32(sc.X, sc.Y, sc.Z);
            return End();
        }

        public Builder U16(ushort v) { w.Write(v); return this; }
        public Builder F32(params float[] v) { foreach (var f in v) w.Write(f); return this; }
        public Builder String(string s) { w.Write(Encoding.UTF8.GetBytes(s + "\n")); return this; }
        public byte[] ToArray() => ms.ToArray();
    }

    static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);

    static Builder TwoBones(string version = OgreSkeletonReader.Version1_80) =>
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
    public void Quaternions_are_stored_x_y_z_w()
    {
        var bytes = new Builder(OgreSkeletonReader.Version1_10)
            .Begin(OgreSkeletonChunk.Bone).String("b").U16(0).F32(0, 0, 0).F32(0.1f, 0.2f, 0.3f, 0.9f).End(OgreSkeletonReader.BoneLengthWithoutScale)
            .ToArray();
        var bone = Assert.Single(OgreSkeletonReader.Read(bytes).Bones);
        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), bone.Orientation);
    }

    [Fact]
    public void Bone_scale_is_decided_by_the_stored_length_not_the_name()
    {
        // A name-inclusive length would look like "has scale" to Ogre; the reader must follow Ogre and
        // read 12 more bytes. Here those bytes are the next chunk, which then breaks parsing.
        var bytes = new Builder(OgreSkeletonReader.Version1_80)
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

    [Fact]
    public void Version_1_10_without_blend_mode_defaults_to_average()
    {
        var skeleton = OgreSkeletonReader.Read(new Builder(OgreSkeletonReader.Version1_10)
            .Bone("root", 0, Vector3.Zero, Quaternion.Identity).ToArray());
        Assert.Null(skeleton.StoredBlendMode);
        Assert.Equal(OgreSkeletonBlendMode.Average, skeleton.BlendMode);
    }

    [Fact]
    public void Rejects_unknown_versions_chunks_bad_parents_and_truncated_files()
    {
        Assert.Throws<OgreFormatException>(() => OgreSkeletonReader.Read(new Builder("[Serializer_v1.20]").ToArray()));
        Assert.Throws<OgreFormatException>(() => OgreSkeletonReader.Read(TwoBones().Begin((OgreSkeletonChunk)0x6000).End().ToArray()));
        Assert.Throws<OgreFormatException>(() => OgreSkeletonReader.Read(TwoBones().Begin(OgreSkeletonChunk.BoneParent).U16(0).U16(7).End().ToArray()));
        Assert.Throws<OgreFormatException>(() => OgreSkeletonReader.Read(TwoBones().ToArray()[..^3]));
        Assert.Throws<OgreFormatException>(() => OgreSkeletonReader.Read([]));
    }

    static IEnumerable<string> SkeletonFiles(GameInstall install) =>
        Directory.EnumerateFiles(install.DataDirectory, "*.skeleton", SearchOption.AllDirectories);

    /// <summary>
    /// Every non-empty base-game skeleton parses to the last byte and is internally consistent: contiguous
    /// handles from 0, unique names, a parent forest without cycles, tracks on existing bones (one per bone),
    /// keyframe times ordered and inside the animation, unit quaternions.
    /// </summary>
    [Fact]
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
            try { skeleton = OgreSkeletonReader.ReadFile(file); }
            catch (OgreFormatException e) { problems.Add($"{name}: {e.Message}"); return; }
            foreach (var p in Check(skeleton)) problems.Add($"{name}: {p}");
        });
        Assert.True(problems.IsEmpty, string.Join(Environment.NewLine, problems.Order()));
    }

    static IEnumerable<string> Check(OgreSkeleton skeleton)
    {
        const float Tolerance = 1e-3f;
        var handles = skeleton.Bones.Select(b => (int)b.Handle).Order().ToList();
        if (!handles.SequenceEqual(Enumerable.Range(0, handles.Count)))
            yield return "bone handles are not contiguous from 0";
        if (skeleton.ChunkLengthMismatches > 0)
            yield return $"{skeleton.ChunkLengthMismatches} animation/track/keyframe chunks with a wrong stored length";
        if (skeleton.Bones.Count > 256)
            yield return $"{skeleton.Bones.Count} bones (Ogre's limit is 256)";
        if (skeleton.Bones.Select(b => b.Name).Distinct().Count() != skeleton.Bones.Count)
            yield return "duplicate bone names";

        var byHandle = skeleton.Bones.ToDictionary(b => b.Handle);
        foreach (var bone in skeleton.Bones)
        {
            var seen = new HashSet<ushort>();
            for (var b = bone; b.Parent is { } parent; b = byHandle[parent])
                if (!seen.Add(b.Handle))
                {
                    yield return $"bone {bone.Name} is in a parent cycle";
                    break;
                }
            if (MathF.Abs(bone.Orientation.Length() - 1) > Tolerance)
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
                    if (key.Time < -Tolerance || key.Time > animation.Length + Tolerance)
                        yield return $"animation {animation.Name} bone {track.Bone}: keyframe at {key.Time} outside 0..{animation.Length}";
                    if (key.Time < previous)
                        yield return $"animation {animation.Name} bone {track.Bone}: keyframe times go back ({previous} -> {key.Time})";
                    if (MathF.Abs(key.Rotation.Length() - 1) > Tolerance)
                        yield return $"animation {animation.Name} bone {track.Bone}: rotation {key.Rotation} at {key.Time} is not unit length";
                    previous = key.Time;
                }
            }
        }
    }

    /// <summary>
    /// Base-game meshes whose skeleton link doesn't hold up. Ogre only logs a failed skeleton load ("This
    /// Mesh will not be animated"), so these load anyway. Any change to this list is news.
    /// </summary>
    static readonly Dictionary<string, string> KnownBadSkeletonLinks = new(StringComparer.OrdinalIgnoreCase)
    {
        // Female armour mesh in Newwworld.mod; the linked skeleton isn't shipped.
        ["items/armour/meshes/Human_Skin_Suit.mesh"] = "not found",
        // Links "Iron Clad [feMale] Jacket.skeleton"; "Iron Clad Jacket_F.skeleton" is the file beside it.
        ["items/armour/meshes/Iron Clad Jacket_F.mesh"] = "not found",
        // Links female_skeleton.skeleton (30 bones) but assigns bones up to 54; no data file references the mesh.
        ["character/meshes/whistler/whistler.mesh"] = "bone index",
    };

    /// <summary>
    /// Every base-game mesh with a skeleton link names a skeleton that exists under data/ (Ogre finds
    /// resources by bare file name), and its bone assignments stay below that skeleton's bone count.
    /// A skeleton name found in several folders must be byte-identical copies.
    /// </summary>
    [Fact]
    public void Every_base_game_skinned_mesh_matches_its_skeleton()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var skeletons = SkeletonFiles(install!)
            .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var boneCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int BoneCount(string file) => boneCounts.GetOrAdd(file, f => OgreSkeletonReader.ReadFile(f).Bones.Count);

        var problems = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int skinned = 0;
        var meshes = Directory.EnumerateFiles(install!.DataDirectory, "*.mesh", SearchOption.AllDirectories).ToList();
        Parallel.ForEach(meshes, file =>
        {
            var mesh = OgreMeshReader.ReadFile(file);
            if (mesh.SkeletonName is null) return;
            Interlocked.Increment(ref skinned);
            var name = Path.GetRelativePath(install.DataDirectory, file).Replace('\\', '/');
            if (!skeletons.TryGetValue(Path.GetFileName(mesh.SkeletonName), out var candidates))
            {
                problems[name] = $"not found: {mesh.SkeletonName}";
                return;
            }
            var first = File.ReadAllBytes(candidates[0]);
            if (candidates.Skip(1).Any(c => !File.ReadAllBytes(c).AsSpan().SequenceEqual(first)))
                problems[name] = $"different files named {mesh.SkeletonName}";
            int maxBone = mesh.BoneAssignments.Concat(mesh.SubMeshes.SelectMany(s => s.BoneAssignments))
                .Select(a => (int)a.BoneIndex).DefaultIfEmpty(-1).Max();
            if (maxBone >= BoneCount(candidates[0]))
                problems[name] = $"bone index {maxBone}, but {mesh.SkeletonName} has {BoneCount(candidates[0])} bones";
        });
        Assert.True(skinned > 0);

        var unexpected = problems
            .Where(p => !(KnownBadSkeletonLinks.TryGetValue(p.Key, out var kind) && p.Value.StartsWith(kind, StringComparison.Ordinal)))
            .Select(p => $"{p.Key}: {p.Value}").Order().ToList();
        Assert.True(unexpected.Count == 0, string.Join(Environment.NewLine, unexpected));
        var fixedNow = KnownBadSkeletonLinks.Keys.Where(k => !problems.ContainsKey(k)).ToList();
        Assert.True(fixedNow.Count == 0, "No longer broken: " + string.Join(", ", fixedNow));
    }
}
