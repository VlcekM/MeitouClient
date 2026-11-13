using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Frame;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The characters in the traced scene (docs/render-lights.md "Traced lamp shadows"): each frame a kernel skins the shown parts of the characters
/// nearest the eye with this frame's palettes (<see cref="Skin"/>, the vertex program's rule: morphs, bones, hidden limbs and parts), a triangle's
/// three corners apiece, and each character's bottom level is refitted over them (built again when its parts or levels change, and every
/// <see cref="RebuildEvery"/> refits). A character whose pose is held and whose pieces lie as they were skinned is left as it is: its structure is
/// this frame's already. One instance per character under <see cref="TracedSunShaders.Characters"/>, which the probes' rays leave out. Render
/// thread only.
/// </summary>
sealed unsafe class TracedCharacters : IDisposable
{
    #region Skinning kernel

    /// <summary>
    /// One job a mesh group: its triangles' corners skinned into <c>target</c>, three a triangle in order. The vertex layout is
    /// <see cref="GpuVertex"/>'s character one: position at 0, texture coordinates at 20, colour at 36 (x the morph slot's bits, y the hide marks),
    /// bones at 52 (8 bits each), weights at 56 (16-bit unorm).
    /// </summary>
    public static readonly string Skin = $$"""
        #version 460
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 64) in;
        layout(set = 0, binding = 0, std430) readonly buffer Palettes { mat4 bones[]; };
        layout(set = 0, binding = 1, std430) readonly buffer Morphs { vec4 deltas[]; };
        struct Job { mat4 model; uvec2 vertices; uvec2 indices; uvec2 target; uint triangles; uint boneBase; uint morphBase; uint flags; uint first; uint pad1; };
        layout(set = 0, binding = 2, std430) readonly buffer Jobs { Job jobs[]; };
        layout(push_constant) uniform Count { uint jobCount; uint triangleCount; } pc;
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Float3 { vec3 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Float2 { vec2 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Float4 { vec4 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Word { uint v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Words { uvec2 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Corners { uvec3 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) writeonly buffer OutPosition { vec3 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) writeonly buffer OutUv { vec2 v; };
        uvec2 offsetBy(uvec2 a, uint bytes) { uint lo = a.x + bytes; return uvec2(lo, a.y + (lo < a.x ? 1u : 0u)); }

        // A corner skinned as CharacterShaders.Vertex places it, relative to the character's anchor; hidden as its fragments would be discarded.
        vec3 corner(Job job, mat4 model, uint index, out vec2 uv, out bool hidden)
        {
            uvec2 at = offsetBy(job.vertices, index * {{GpuVertex.CharacterSize}}u);
            vec3 position = Float3(at).v;
            uv = Float2(offsetBy(at, 20u)).v;
            vec4 colour = Float4(offsetBy(at, 36u)).v;
            uint packedBones = Word(offsetBy(at, 52u)).v;
            uvec2 packedWeights = Words(offsetBy(at, 56u)).v;
            vec4 weights = vec4(packedWeights.x & 65535u, packedWeights.x >> 16u, packedWeights.y & 65535u, packedWeights.y >> 16u) / 65535.0;
            mat4 sk = mat4(1.0);
            if (dot(weights, vec4(1.0)) > 0.0)
            {
                uint b = job.boneBase;
                sk = bones[b + (packedBones & 255u)] * weights.x + bones[b + ((packedBones >> 8u) & 255u)] * weights.y
                   + bones[b + ((packedBones >> 16u) & 255u)] * weights.z + bones[b + (packedBones >> 24u)] * weights.w;
            }
            vec3 local = position;
            uint slot = floatBitsToUint(colour.x);
            if (job.morphBase != 0u && slot != 0u && slot < 0x1000000u) local += deltas[job.morphBase + slot].xyz;
            uint f = job.flags, marks = uint(colour.y + 0.5);
            hidden = ((marks & (f >> 12u) & 15u) | ((marks >> 4u) & (f >> 18u) & 1023u) | ((marks >> 14u) & (f >> 28u) & 1u)) != 0u;
            if ((f & {{CharacterShaders.HeadOnly}}u) != 0u) hidden = (marks & {{CharacterContent.HeadBit}}u) == 0u;
            uint side = (f >> 16u) & 3u;
            if ((side == 1u && position.x >= 0.0) || (side == 2u && position.x < 0.0)) hidden = true;
            return (model * sk * vec4(local, 1.0)).xyz;
        }

        void main()
        {
            // One invocation a triangle over every job: the job is the last whose first triangle is at or before this one.
            uint g = gl_GlobalInvocationID.x;
            if (g >= pc.triangleCount) return;
            uint lo = 0u, hi = pc.jobCount - 1u;
            while (lo < hi)
            {
                uint mid = (lo + hi + 1u) / 2u;
                if (jobs[mid].first <= g) lo = mid; else hi = mid - 1u;
            }
            Job job = jobs[lo];
            uint t = g - job.first;
            if (t >= job.triangles) return;
            mat4 model = mat4(vec4(job.model[0].xyz, 0.0), vec4(job.model[1].xyz, 0.0), vec4(job.model[2].xyz, 0.0), vec4(job.model[3].xyz, 1.0));
            uvec3 tri = Corners(offsetBy(job.indices, t * 12u)).v;
            vec3 p[3];
            vec2 uv[3];
            bool hidden = false;
            for (int k = 0; k < 3; k++)
            {
                bool h;
                p[k] = corner(job, model, tri[k], uv[k], h);
                hidden = hidden || h;
            }
            for (int k = 0; k < 3; k++)
            {
                // A triangle with a hidden corner is discarded where drawn: it collapses to a point, which no ray hits (and stays active for the refits).
                uvec2 to = offsetBy(job.target, (t * 3u + uint(k)) * {{Stride}}u);
                OutPosition(to).v = hidden ? p[0] : p[k];
                OutUv(offsetBy(to, {{GpuVertex.UvOffset}}u)).v = uv[k];
            }
        }
        """;

    /// <summary>
    /// One mesh group's job as the kernel's <c>Job</c> reads it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Job
    {
        /// <summary>
        /// Bytes of a job in the kernel's buffer.
        /// </summary>
        public const int Size = 112;

        /// <summary>
        /// The piece's model matrix, relative to the character's anchor.
        /// </summary>
        public Matrix4x4 Model;

        /// <summary>
        /// Device address of the group's vertices.
        /// </summary>
        public ulong Vertices;

        /// <summary>
        /// Device address of the level's first index.
        /// </summary>
        public ulong Indices;

        /// <summary>
        /// Device address the skinned corners go to.
        /// </summary>
        public ulong Target;

        /// <summary>
        /// Triangles the job skins.
        /// </summary>
        public uint Triangles;

        /// <summary>
        /// The character's first bone in the palettes.
        /// </summary>
        public uint BoneBase;

        /// <summary>
        /// The piece's first morph delta (0: none).
        /// </summary>
        public uint MorphBase;

        /// <summary>
        /// The piece's flags: hidden parts and limbs, side, head only.
        /// </summary>
        public uint Flags;

        /// <summary>
        /// The job's first triangle over every job.
        /// </summary>
        public uint First;

        /// <summary>
        /// Padding to the kernel's layout.
        /// </summary>
        public uint Pad1;
    }

    /// <summary>
    /// The kernel's push constants.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Counts
    {
        /// <summary>
        /// Jobs in the buffer.
        /// </summary>
        public uint Jobs;

        /// <summary>
        /// Triangles over every job.
        /// </summary>
        public uint Triangles;
    }

    #endregion

    #region Structures

    /// <summary>
    /// The budget: characters this near the eye.
    /// </summary>
    public const float Range = 4000;

    /// <summary>
    /// The budget: the nearest this many characters.
    /// </summary>
    public const int MaxCharacters = 128;

    /// <summary>
    /// The budget: at most this many triangles a frame.
    /// </summary>
    const int MaxTriangles = 3_000_000;

    /// <summary>
    /// A refitted structure loses quality as the pose moves away from the one it was built over: built again after this many refits.
    /// </summary>
    const int RebuildEvery = 60;

    /// <summary>
    /// An unused character's structure is kept this many frames (it comes back without a build).
    /// </summary>
    const int KeepFrames = 120;

    /// <summary>
    /// Bytes of a skinned corner: its position at 0 and its texture coordinates at <see cref="GpuVertex.UvOffset"/>, as the hit records read them.
    /// </summary>
    const int Stride = 32;

    /// <summary>
    /// Most triangles of one mesh group (a larger one traces its first ones): the shared index buffer's size.
    /// </summary>
    const int MaxGroupTriangles = 1 << 17;

    /// <summary>
    /// What a piece's model may differ by from the skinned one and count as the same: its place in world units.
    /// </summary>
    const float PlacedShift = 0.05f;

    /// <summary>
    /// What a piece's model may differ by from the skinned one and count as the same: its axes' entries summed.
    /// </summary>
    const float PlacedTurn = 1e-3f;

    /// <summary>
    /// The device the structures and buffers live on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The skinning kernel's program.
    /// </summary>
    readonly GpuProgram _program;

    /// <summary>
    /// The skinning kernel's pipeline.
    /// </summary>
    readonly GpuComputePipeline _pipeline;

    /// <summary>
    /// Each character's structure, by its key.
    /// </summary>
    readonly Dictionary<long, Entry> _entries = [];

    /// <summary>
    /// This frame's characters near the eye, as the renderer lists them.
    /// </summary>
    readonly List<CharacterRenderer.RayCharacter> _characters = [];

    /// <summary>
    /// The characters in this frame's scene: their entry, whether it is built this frame and whether it is skinned this frame.
    /// </summary>
    readonly List<(CharacterRenderer.RayCharacter Character, Entry Entry, bool Fresh, bool Skin)> _shown = [];

    /// <summary>
    /// This frame's skinning jobs.
    /// </summary>
    readonly List<Job> _jobs = [];

    /// <summary>
    /// The pieces of the character being placed.
    /// </summary>
    readonly List<Piece> _pieces = [];

    /// <summary>
    /// The keys of the entries unused for too long.
    /// </summary>
    readonly List<long> _stale = [];

    /// <summary>
    /// The index buffer every geometry shares: 0, 1, 2, ... for its corners in order.
    /// </summary>
    readonly DeviceBuffer _sequence;

    /// <summary>
    /// Bytes the entries' vertices and structures hold.
    /// </summary>
    long _bytes;

    /// <summary>
    /// This frame's triangles over every job.
    /// </summary>
    int _jobTriangles;

    /// <summary>
    /// Structures built this frame.
    /// </summary>
    int _built;

    /// <summary>
    /// Structures refitted this frame.
    /// </summary>
    int _refitted;

    /// <summary>
    /// Triangles of this frame's characters.
    /// </summary>
    int _triangles;

    /// <summary>
    /// Characters held this frame (their structure left as it was).
    /// </summary>
    int _held;

    /// <summary>
    /// What a character's structure was built over: per geometry its group, level, triangles and whether it is opaque.
    /// </summary>
    readonly record struct Piece(GpuObjectPart Group, int Level, int Triangles, bool Opaque);

    /// <summary>
    /// A character's structure and the skinned vertices it is built over.
    /// </summary>
    sealed class Entry
    {
        /// <summary>
        /// The bottom level (null until first built).
        /// </summary>
        public required AccelerationStructure Blas;

        /// <summary>
        /// The skinned corners, three a triangle.
        /// </summary>
        public required DeviceBuffer Vertices;

        /// <summary>
        /// What the structure was built over.
        /// </summary>
        public required Piece[] Pieces;

        /// <summary>
        /// The pieces' records for the hit shaders.
        /// </summary>
        public required GeometryRecord[] Records;

        /// <summary>
        /// The pieces' geometries over <see cref="Vertices"/>.
        /// </summary>
        public required TriangleGeometry[] Geometries;

        /// <summary>
        /// What the vertices were last skinned with: each piece's model, flags and morphs, and the pose
        /// (<see cref="CharacterRenderer.RayCharacter.Pose"/>).
        /// </summary>
        public required (Matrix4x4 Model, uint Flags, uint Morphs)[] Skinned;

        /// <summary>
        /// The frame the structure was last built.
        /// </summary>
        public long Built;

        /// <summary>
        /// The frame the character was last shown.
        /// </summary>
        public long LastUsed;

        /// <summary>
        /// The pose the vertices were last skinned in.
        /// </summary>
        public long SkinnedPose = long.MinValue;

        /// <summary>
        /// Refits since the last build.
        /// </summary>
        public int Refits;
    }

    /// <summary>
    /// The skinning kernel and the shared index buffer.
    /// </summary>
    public TracedCharacters(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(Skin, "traced characters",
            [(0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageBuffer)]);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "traced characters"));

        var list = new uint[MaxGroupTriangles * 3];
        for (uint i = 0; i < list.Length; i++) list[i] = i;
        _sequence = DeviceBuffer.Create(ctx, (ulong)list.Length * 4, BufferUse.Index | BufferUse.RayInput, "traced character indices");
        ctx.Uploads.Write(_sequence, 0, MemoryMarshal.AsBytes(list.AsSpan()));
    }

    /// <summary>
    /// Characters in this frame's scene.
    /// </summary>
    public int Shown => _shown.Count;

    /// <summary>
    /// This frame's characters near <paramref name="eye"/>, skinned and their structures refitted or built, into the scene's native list
    /// (<paramref name="open"/>). Their palettes are this update's, so the structures hold the pose the frame draws.
    /// </summary>
    public void Update(Func<IGpuCommandList> open, CharacterRenderer people, Vector3 eye, long frame, float reach = Range, float minAngle = 0)
    {
        _shown.Clear();
        _jobs.Clear();
        (_jobTriangles, _built, _refitted, _triangles, _held) = (0, 0, 0, 0, 0);
        people.RayCharacters(eye, MathF.Max(Range, reach), MaxCharacters, _characters, minAngle);

        foreach (var c in _characters)
        {
            int count = Pieces(c);
            if (_triangles + count > MaxTriangles) break;

            _triangles += count;
            var (entry, fresh) = Current(c, frame);
            entry.LastUsed = frame;

            // A held pose (frozen, thinned by its size on screen) whose pieces lie as they were skinned: the structure is this frame's already.
            bool skin = entry.Blas is null || entry.SkinnedPose != c.Pose || Placed(c, entry);
            Describe(c, entry);
            if (skin)
            {
                AddJobs(c, entry);
                entry.SkinnedPose = c.Pose;
            }
            else _held++;

            _shown.Add((c, entry, fresh && skin, skin));
        }

        if (_jobs.Count > 0) Record((CommandList)open(), people, frame);
        else if (_shown.Count > 0) Stamp((CommandList)open(), 1);

        Evict(frame);
    }

    /// <summary>
    /// One instance per character at its anchor, after everything else, with its records.
    /// </summary>
    public void Emit(List<InstanceRecord> instances, List<GeometryRecord> records, Vector3 origin, int maxInstances, int maxRecords)
    {
        foreach (var (c, entry, _, _) in _shown)
        {
            if (instances.Count >= maxInstances || records.Count + entry.Records.Length > maxRecords) break;

            var anchor = Matrix4x4.CreateTranslation(c.Anchor - origin);
            instances.Add(InstanceRecord.Of(anchor, (uint)records.Count, TracedSunShaders.Characters, entry.Blas.Address));
            records.AddRange(entry.Records);
        }
    }

    /// <summary>
    /// The characters in the scene now, as the spheres they fit in.
    /// </summary>
    public void CollectSpheres(List<(Vector3 Centre, float Radius)> into)
    {
        foreach (var (c, _, _, _) in _shown) into.Add((c.Centre, c.Radius));
    }

    /// <summary>
    /// The lamps stopped tracing: the structures go (the kernel stays).
    /// </summary>
    public void Release()
    {
        _shown.Clear();
        foreach (var key in _entries.Keys.ToArray()) Drop(key, _entries[key]);
    }

    /// <summary>
    /// This frame's characters, structures and GPU time, for the console.
    /// </summary>
    public string Describe()
    {
        string structures = $"{_shown.Count} characters traced ({_triangles / 1000}k triangles, {_built} built, {_refitted} refitted, {_held} held";
        string bytes = $"{_bytes / (1024 * 1024)} MB";
        string gpu = $"GPU {GpuMs.Skin:0.000} ms a frame, {GpuMs.Total:0.000} with the top level, over the last {_gpuSamples.Count})";

        return structures + ", " + bytes + "; " + gpu;
    }

    /// <summary>
    /// The structures, the shared index buffer and the kernel go.
    /// </summary>
    public void Dispose()
    {
        Release();
        _sequence.Dispose();
        _ctx.Shaders.Dispose(_program);
    }

    /// <summary>
    /// The character's pieces into <see cref="_pieces"/>; returns their triangles.
    /// </summary>
    int Pieces(CharacterRenderer.RayCharacter c)
    {
        _pieces.Clear();
        int count = 0;
        foreach (var part in c.Parts)
        {
            int n = Math.Min(part.Group.Count[part.Level] / 3, MaxGroupTriangles);
            _pieces.Add(new Piece(part.Group, part.Level, n, part.AlphaCut <= 0));
            count += n;
        }

        return count;
    }

    /// <summary>
    /// The character's entry for <see cref="_pieces"/>: its own when they are those it was built over, else a new one (built in
    /// <see cref="Record"/>).
    /// </summary>
    (Entry Entry, bool Fresh) Current(CharacterRenderer.RayCharacter c, long frame)
    {
        if (_entries.TryGetValue(c.Key, out var entry) && entry.Pieces.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(_pieces)))
            return (entry, entry.Refits >= RebuildEvery + (int)(c.Key & 15));

        if (entry is not null) Drop(c.Key, entry);

        entry = NewEntry(frame);
        _entries[c.Key] = entry;
        _bytes += (long)entry.Vertices.Size;

        return (entry, true);
    }

    /// <summary>
    /// A new entry over <see cref="_pieces"/>: its vertices, and per piece a geometry and a record over its run of them.
    /// </summary>
    Entry NewEntry(long frame)
    {
        int count = _pieces.Sum(p => p.Triangles);
        var vertices = DeviceBuffer.Create(_ctx, (ulong)count * 3 * Stride, BufferUse.Storage | BufferUse.RayInput, "traced character vertices");
        ulong address = _ctx.Device.Address(vertices.Handle);
        ulong indices = _ctx.Device.Address(_sequence.Handle);
        var geometries = new TriangleGeometry[_pieces.Count];
        var records = new GeometryRecord[_pieces.Count];

        ulong first = 0;
        for (int i = 0; i < _pieces.Count; i++)
        {
            var p = _pieces[i];
            ulong at = address + first * 3 * Stride;
            geometries[i] = new TriangleGeometry(at, Stride, (uint)p.Triangles * 3, indices, (uint)p.Triangles, p.Opaque);
            records[i] = new GeometryRecord { Vertices = at, Indices = indices, Stride = Stride, Kind = GeometryRecord.Character };
            first += (ulong)p.Triangles;
        }

        return new Entry
        {
            Blas = null!,
            Vertices = vertices,
            Pieces = [.. _pieces],
            Records = records,
            Geometries = geometries,
            Built = frame,
            Skinned = new (Matrix4x4, uint, uint)[_pieces.Count],
        };
    }

    /// <summary>
    /// A piece is placed, hidden or morphed otherwise than when the vertices were last skinned (a held pose that turned about its anchor, a limb
    /// lost): beyond what the placing's own rounding moves it from frame to frame.
    /// </summary>
    static bool Placed(CharacterRenderer.RayCharacter c, Entry entry)
    {
        for (int i = 0; i < c.Parts.Count; i++)
        {
            var part = c.Parts[i];
            ref readonly var last = ref entry.Skinned[i];
            if (part.Flags != last.Flags || part.MorphBase != last.Morphs) return true;

            Matrix4x4 a = part.Model, b = last.Model;
            if (Vector3.DistanceSquared(a.Translation, b.Translation) > PlacedShift * PlacedShift) return true;

            float turn = MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13)
                + MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22) + MathF.Abs(a.M23 - b.M23)
                + MathF.Abs(a.M31 - b.M31) + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33);
            if (turn > PlacedTurn) return true;
        }

        return false;
    }

    /// <summary>
    /// The pieces' records as the frame draws them (they hold no geometry: a held structure takes them too).
    /// </summary>
    static void Describe(CharacterRenderer.RayCharacter c, Entry entry)
    {
        for (int i = 0; i < c.Parts.Count; i++)
        {
            var part = c.Parts[i];
            ref var record = ref entry.Records[i];

            // The cut-out as the shadow draws make it; the channel rides in the record's diffuse index, which no probe ray reads for a character.
            (record.AlphaTexture, record.AlphaThreshold, record.Texture) = (part.AlphaMap, part.AlphaCut, part.Channel);
            (record.CharacterDiffuse, record.CharacterHead, record.CharacterTint) = (part.Diffuse, part.Head, part.Tint);
        }
    }

    /// <summary>
    /// A job per piece, its corners into the entry's vertices in order.
    /// </summary>
    void AddJobs(CharacterRenderer.RayCharacter c, Entry entry)
    {
        for (int i = 0; i < c.Parts.Count; i++)
        {
            var part = c.Parts[i];
            var group = part.Group;
            var g = entry.Geometries[i];
            entry.Skinned[i] = (part.Model, part.Flags, part.MorphBase);
            _jobs.Add(new Job
            {
                Model = part.Model,
                Vertices = _ctx.Device.Address(group.Vertices.Handle),
                Indices = _ctx.Device.Address(group.Indices.Handle) + (ulong)group.Offset[part.Level] * 4,
                Target = g.VertexAddress,
                Triangles = g.TriangleCount,
                BoneBase = c.BoneBase,
                MorphBase = part.MorphBase,
                Flags = part.Flags,
                First = (uint)_jobTriangles,
            });
            _jobTriangles += (int)g.TriangleCount;
        }
    }

    /// <summary>
    /// The kernel over every job, then each character's structure refitted, or built when new or due.
    /// </summary>
    void Record(CommandList cmd, CharacterRenderer people, long frame)
    {
        cmd.BeginLabel("traced characters");
        DispatchSkinning(cmd, people);
        cmd.Barrier(BarrierBatch.Full);
        RefitOrBuild(cmd, frame);
        Stamp(cmd, 1);
        cmd.EndLabel();
    }

    /// <summary>
    /// The kernel over every job: the jobs, the palettes and the morphs bound, one invocation a triangle.
    /// </summary>
    void DispatchSkinning(CommandList cmd, CharacterRenderer people)
    {
        var list = _ctx.Frame.Constants.Write<Job>(CollectionsMarshal.AsSpan(_jobs), 256);
        var palettes = people.RayBones;
        var morphs = people.RayMorphs is { Size: > 0 } m ? m : palettes;

        var infos = stackalloc DescriptorBufferInfo[3];
        infos[0] = new DescriptorBufferInfo(_ctx.Device.BufferAt(palettes.Buffer.Index), palettes.Offset, palettes.Size);
        infos[1] = new DescriptorBufferInfo(_ctx.Device.BufferAt(morphs.Buffer.Index), morphs.Offset, morphs.Size);
        infos[2] = new DescriptorBufferInfo(_ctx.Device.BufferAt(list.Handle.Index), list.Offset, (ulong)(_jobs.Count * Job.Size));
        var writes = stackalloc WriteDescriptorSet[3];
        for (int i = 0; i < 3; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &infos[i],
            };
        }

        cmd.BindPipeline(_pipeline);
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 3), GpuBindPoint.Compute);
        var count = new Counts { Jobs = (uint)_jobs.Count, Triangles = (uint)_jobTriangles };
        cmd.PushConstants(_program, GpuStage.Compute, in count);
        cmd.Dispatch((uint)((_jobTriangles + 63) / 64));
    }

    /// <summary>
    /// Each skinned character's structure refitted over its new corners, or built when new or due.
    /// </summary>
    void RefitOrBuild(CommandList cmd, long frame)
    {
        foreach (var (_, entry, fresh, skin) in _shown)
        {
            if (!skin) continue;

            if (fresh || entry.Blas is null) Build(cmd, entry, frame);
            else
            {
                entry.Blas.RefitBottom(cmd, entry.Geometries);
                entry.Refits++;
                _refitted++;
            }
        }
    }

    /// <summary>
    /// The entry's structure built again over its corners, the old one gone.
    /// </summary>
    void Build(CommandList cmd, Entry entry, long frame)
    {
        if (entry.Blas is { } old)
        {
            _bytes -= (long)old.Bytes;
            old.Dispose();
        }

        entry.Blas = AccelerationStructure.BuildBottom(_ctx.Device, cmd, entry.Geometries, "traced character", updatable: true, fastBuild: true);
        (entry.Built, entry.Refits) = (frame, 0);
        _bytes += (long)entry.Blas.Bytes;
        _built++;
    }

    /// <summary>
    /// The entries unused for more than <see cref="KeepFrames"/> go.
    /// </summary>
    void Evict(long frame)
    {
        _stale.Clear();
        foreach (var (key, entry) in _entries)
            if (frame - entry.LastUsed > KeepFrames) _stale.Add(key);

        foreach (var key in _stale) Drop(key, _entries[key]);
    }

    /// <summary>
    /// An entry's vertices and structure go.
    /// </summary>
    void Drop(long key, Entry entry)
    {
        _bytes -= (long)entry.Vertices.Size + (long)(entry.Blas?.Bytes ?? 0);
        entry.Blas?.Dispose();
        entry.Vertices.Dispose();
        _entries.Remove(key);
    }

    #endregion

    #region GPU timing

    /// <summary>
    /// The frames' timestamps not read yet: before the characters' work, after it and after the top level.
    /// </summary>
    readonly Queue<(QuerySlot Start, QuerySlot Skinned, QuerySlot End)> _stamps = new();

    /// <summary>
    /// The last 60 read frames' GPU times in milliseconds: the characters' work, and that plus the top level.
    /// </summary>
    readonly List<(double Skin, double Total)> _gpuSamples = [];

    /// <summary>
    /// This frame's timestamp before the characters' work.
    /// </summary>
    QuerySlot _startStamp;

    /// <summary>
    /// This frame's timestamp after the characters' work.
    /// </summary>
    QuerySlot _skinnedStamp;

    /// <summary>
    /// The mean GPU time over the last 60 frames that had characters: their skinning and structures, and that plus the top level built again over
    /// them.
    /// </summary>
    public (double Skin, double Total) GpuMs => _gpuSamples.Count == 0
        ? default
        : (_gpuSamples.Average(s => s.Skin), _gpuSamples.Average(s => s.Total));

    /// <summary>
    /// A timestamp: 0 before the characters' work, 1 after it, 2 after the top level; read a frame ring later.
    /// </summary>
    public void Stamp(CommandList cmd, int point)
    {
        var arena = _ctx.Frame.Timestamps;
        var slot = arena.Allocate();
        if (!slot.IsValid) return;

        cmd.Timestamp(arena, slot);
        if (point == 0) _startStamp = slot;
        else if (point == 1) _skinnedStamp = slot;
        else if (_startStamp.IsValid && _skinnedStamp.IsValid && _shown.Count > 0) _stamps.Enqueue((_startStamp, _skinnedStamp, slot));

        while (_stamps.TryPeek(out var s))
        {
            if (arena.TryRead(s.Start, out ulong a) && arena.TryRead(s.Skinned, out ulong m) && arena.TryRead(s.End, out ulong b))
            {
                _gpuSamples.Add(((m - a) / 1e6, (b - a) / 1e6));
                if (_gpuSamples.Count > 60) _gpuSamples.RemoveAt(0);
            }
            else if (_ctx.Frame.Number - s.Start.Frame < 16) break;

            _stamps.Dequeue();
        }
    }

    #endregion
}
