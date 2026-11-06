using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>
/// The characters in the traced scene (docs/render-lights.md "Traced lamp shadows"): each frame a kernel skins the shown parts of the characters nearest the eye with this frame's
/// palettes (<see cref="Skin"/>, the vertex program's rule: morphs, bones, hidden limbs and parts), a triangle's three corners apiece, and each character's bottom level is refitted over
/// them (built again when its parts or levels change, and every <see cref="RebuildEvery"/> frames). One instance per character under <see cref="TracedSunShaders.Characters"/>, which the
/// probes' rays leave out. Render thread only.
/// </summary>
sealed unsafe class TracedCharacters : IDisposable
{
    /// <summary>The budget: characters this near the eye, the nearest this many, at most this many triangles a frame.</summary>
    public const float Range = 4000;
    public const int MaxCharacters = 128;
    const int MaxTriangles = 3_000_000;
    /// <summary>A refitted structure loses quality as the pose moves away from the one it was built over: built again after this many frames.</summary>
    const int RebuildEvery = 60;
    /// <summary>An unused character's structure is kept this many frames (it comes back without a build).</summary>
    const int KeepFrames = 120;
    /// <summary>Bytes of a skinned corner: its position at 0 and its texture coordinates at <see cref="GpuVertex.UvOffset"/>, as the hit records read them.</summary>
    const int Stride = 32;
    /// <summary>Most triangles of one mesh group (a larger one traces its first ones): the shared index buffer's size.</summary>
    const int MaxGroupTriangles = 1 << 17;

    /// <summary>
    /// One job a mesh group: its triangles' corners skinned into <c>target</c>, three a triangle in order. The vertex layout is <see cref="GpuVertex"/>'s character one: position at 0,
    /// texture coordinates at 20, colour at 36 (x the morph slot's bits, y the hide marks), bones at 52 (8 bits each), weights at 56 (16-bit unorm).
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

    [StructLayout(LayoutKind.Sequential)]
    struct Job
    {
        public Matrix4x4 Model;
        public ulong Vertices, Indices, Target;
        public uint Triangles, BoneBase, MorphBase, Flags, First, Pad1;
        public const int Size = 112;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Counts
    {
        public uint Jobs, Triangles;
    }

    /// <summary>What a character's structure was built over: per geometry its group, level, triangles and whether it is opaque.</summary>
    readonly record struct Piece(GpuObjectPart Group, int Level, int Triangles, bool Opaque);

    sealed class Entry
    {
        public required AccelerationStructure Blas;
        public required DeviceBuffer Vertices;
        public required Piece[] Pieces;
        public required GeometryRecord[] Records;
        public required TriangleGeometry[] Geometries;
        public long Built, LastUsed;
    }

    readonly GpuContext ctx;
    readonly GpuProgram program;
    readonly GpuComputePipeline pipeline;
    readonly Dictionary<long, Entry> entries = [];
    readonly List<CharacterRenderer.RayCharacter> characters = [];
    readonly List<(CharacterRenderer.RayCharacter Character, Entry Entry, bool Fresh)> shown = [];
    readonly List<Job> jobs = [];
    readonly List<Piece> pieces = [];
    readonly List<long> stale = [];
    /// <summary>The index buffer every geometry shares: 0, 1, 2, ... for its corners in order.</summary>
    readonly DeviceBuffer sequence;
    long bytes;
    int jobTriangles, built, refitted, triangles;

    public TracedCharacters(GpuContext ctx)
    {
        this.ctx = ctx;
        program = ctx.Shaders.Compute(Skin, "traced characters",
            [(0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageBuffer)]);
        pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(program, "traced characters"));
        var list = new uint[MaxGroupTriangles * 3];
        for (uint i = 0; i < list.Length; i++) list[i] = i;
        sequence = DeviceBuffer.Create(ctx, (ulong)list.Length * 4, BufferUse.Index | BufferUse.RayInput, "traced character indices");
        ctx.Uploads.Write(sequence, 0, MemoryMarshal.AsBytes(list.AsSpan()));
    }

    /// <summary>Characters in this frame's scene.</summary>
    public int Shown => shown.Count;

    /// <summary>
    /// This frame's characters near <paramref name="eye"/>, skinned and their structures refitted or built, into the scene's native list (<paramref name="open"/>). Their palettes are
    /// this update's, so the structures hold the pose the frame draws.
    /// </summary>
    public void Update(Func<IGpuCommandList> open, CharacterRenderer people, Vector3 eye, long frame)
    {
        shown.Clear();
        jobs.Clear();
        (jobTriangles, built, refitted, triangles) = (0, 0, 0, 0);
        people.RayCharacters(eye, Range, MaxCharacters, characters);
        foreach (var c in characters)
        {
            int count = Pieces(c);
            if (triangles + count > MaxTriangles) break;
            triangles += count;
            var (entry, fresh) = Current(c, frame);
            entry.LastUsed = frame;
            AddJobs(c, entry);
            shown.Add((c, entry, fresh));
        }
        if (shown.Count > 0) Record((CommandList)open(), people, frame);
        Evict(frame);
    }

    /// <summary>The character's pieces into <see cref="pieces"/>; returns their triangles.</summary>
    int Pieces(CharacterRenderer.RayCharacter c)
    {
        pieces.Clear();
        int count = 0;
        foreach (var part in c.Parts)
        {
            int n = Math.Min(part.Group.Count[part.Level] / 3, MaxGroupTriangles);
            pieces.Add(new Piece(part.Group, part.Level, n, part.AlphaCut <= 0));
            count += n;
        }
        return count;
    }

    /// <summary>The character's entry for <see cref="pieces"/>: its own when they are those it was built over, else a new one (built in <see cref="Record"/>).</summary>
    (Entry Entry, bool Fresh) Current(CharacterRenderer.RayCharacter c, long frame)
    {
        if (entries.TryGetValue(c.Key, out var entry) && entry.Pieces.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(pieces)))
            return (entry, frame - entry.Built >= RebuildEvery + (int)(c.Key & 15));
        if (entry is not null) Drop(c.Key, entry);
        int count = pieces.Sum(p => p.Triangles);
        var vertices = DeviceBuffer.Create(ctx, (ulong)count * 3 * Stride, BufferUse.Storage | BufferUse.RayInput, "traced character vertices");
        ulong address = ctx.Device.Address(vertices.Handle), indices = ctx.Device.Address(sequence.Handle);
        var geometries = new TriangleGeometry[pieces.Count];
        var records = new GeometryRecord[pieces.Count];
        ulong first = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            ulong at = address + first * 3 * Stride;
            geometries[i] = new TriangleGeometry(at, Stride, (uint)p.Triangles * 3, indices, (uint)p.Triangles, p.Opaque);
            records[i] = new GeometryRecord { Vertices = at, Indices = indices, Stride = Stride, Kind = GeometryRecord.Character };
            first += (ulong)p.Triangles;
        }
        entry = new Entry { Blas = null!, Vertices = vertices, Pieces = [.. pieces], Records = records, Geometries = geometries, Built = frame };
        entries[c.Key] = entry;
        bytes += (long)vertices.Size;
        return (entry, true);
    }

    /// <summary>A job per piece, its corners into the entry's vertices in order.</summary>
    void AddJobs(CharacterRenderer.RayCharacter c, Entry entry)
    {
        for (int i = 0; i < c.Parts.Count; i++)
        {
            var part = c.Parts[i];
            var group = part.Group;
            var g = entry.Geometries[i];
            jobs.Add(new Job
            {
                Model = part.Model, Vertices = ctx.Device.Address(group.Vertices.Handle), Indices = ctx.Device.Address(group.Indices.Handle) + (ulong)group.Offset[part.Level] * 4,
                Target = g.VertexAddress, Triangles = g.TriangleCount, BoneBase = c.BoneBase, MorphBase = part.MorphBase, Flags = part.Flags, First = (uint)jobTriangles,
            });
            jobTriangles += (int)g.TriangleCount;
            // The cut-out as the shadow draws make it; the channel rides in the record's diffuse index, which no probe ray reads for a character.
            (entry.Records[i].AlphaTexture, entry.Records[i].AlphaThreshold, entry.Records[i].Texture) = (part.AlphaMap, part.AlphaCut, part.Channel);
        }
    }

    /// <summary>The kernel over every job, then each character's structure refitted, or built when new or due.</summary>
    void Record(CommandList cmd, CharacterRenderer people, long frame)
    {
        cmd.BeginLabel("traced characters");
        var list = ctx.Frame.Constants.Write<Job>(CollectionsMarshal.AsSpan(jobs), 256);
        var palettes = people.RayBones;
        var morphs = people.RayMorphs is { Size: > 0 } m ? m : palettes;
        var infos = stackalloc DescriptorBufferInfo[3];
        infos[0] = new DescriptorBufferInfo(ctx.Device.BufferAt(palettes.Buffer.Index), palettes.Offset, palettes.Size);
        infos[1] = new DescriptorBufferInfo(ctx.Device.BufferAt(morphs.Buffer.Index), morphs.Offset, morphs.Size);
        infos[2] = new DescriptorBufferInfo(ctx.Device.BufferAt(list.Handle.Index), list.Offset, (ulong)(jobs.Count * Job.Size));
        var writes = stackalloc WriteDescriptorSet[3];
        for (int i = 0; i < 3; i++)
            writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &infos[i] };
        cmd.BindPipeline(pipeline);
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 3), GpuBindPoint.Compute);
        var count = new Counts { Jobs = (uint)jobs.Count, Triangles = (uint)jobTriangles };
        cmd.PushConstants(program, GpuStage.Compute, in count);
        cmd.Dispatch((uint)((jobTriangles + 63) / 64));
        cmd.Barrier(BarrierBatch.Full);
        foreach (var (_, entry, fresh) in shown)
        {
            if (fresh || entry.Blas is null) Build(cmd, entry, frame);
            else
            {
                entry.Blas.RefitBottom(cmd, entry.Geometries);
                refitted++;
            }
        }
        Stamp(cmd, 1);
        cmd.EndLabel();
    }

    void Build(CommandList cmd, Entry entry, long frame)
    {
        if (entry.Blas is { } old)
        {
            bytes -= (long)old.Bytes;
            old.Dispose();
        }
        entry.Blas = AccelerationStructure.BuildBottom(ctx.Device, cmd, entry.Geometries, "traced character", updatable: true, fastBuild: true);
        entry.Built = frame;
        bytes += (long)entry.Blas.Bytes;
        built++;
    }

    /// <summary>One instance per character at its anchor, after everything else, with its records.</summary>
    public void Emit(List<InstanceRecord> instances, List<GeometryRecord> records, Vector3 origin, int maxInstances, int maxRecords)
    {
        foreach (var (c, entry, _) in shown)
        {
            if (instances.Count >= maxInstances || records.Count + entry.Records.Length > maxRecords) break;
            instances.Add(InstanceRecord.Of(Matrix4x4.CreateTranslation(c.Anchor - origin), (uint)records.Count, TracedSunShaders.Characters, entry.Blas.Address));
            records.AddRange(entry.Records);
        }
    }

    void Evict(long frame)
    {
        stale.Clear();
        foreach (var (key, entry) in entries)
            if (frame - entry.LastUsed > KeepFrames) stale.Add(key);
        foreach (var key in stale) Drop(key, entries[key]);
    }

    void Drop(long key, Entry entry)
    {
        bytes -= (long)entry.Vertices.Size + (long)(entry.Blas?.Bytes ?? 0);
        entry.Blas?.Dispose();
        entry.Vertices.Dispose();
        entries.Remove(key);
    }

    /// <summary>The lamps stopped tracing: the structures go (the kernel stays).</summary>
    public void Release()
    {
        shown.Clear();
        foreach (var key in entries.Keys.ToArray()) Drop(key, entries[key]);
    }

    public string Describe() => $"{shown.Count} characters traced ({triangles / 1000}k triangles, {built} built, {refitted} refitted, {bytes / (1024 * 1024)} MB; GPU {GpuMs.Skin:0.000} ms a frame, {GpuMs.Total:0.000} with the top level, over the last {gpuSamples.Count})";

    readonly Queue<(QuerySlot Start, QuerySlot Skinned, QuerySlot End)> stamps = new();
    readonly List<(double Skin, double Total)> gpuSamples = [];
    QuerySlot startStamp, skinnedStamp;

    /// <summary>The mean GPU time over the last 60 frames that had characters: their skinning and structures, and that plus the top level built again over them.</summary>
    public (double Skin, double Total) GpuMs => gpuSamples.Count == 0 ? default : (gpuSamples.Average(s => s.Skin), gpuSamples.Average(s => s.Total));

    /// <summary>A timestamp: 0 before the characters' work, 1 after it, 2 after the top level; read a frame ring later.</summary>
    public void Stamp(CommandList cmd, int point)
    {
        var arena = ctx.Frame.Timestamps;
        var slot = arena.Allocate();
        if (!slot.IsValid) return;
        cmd.Timestamp(arena, slot);
        if (point == 0) startStamp = slot;
        else if (point == 1) skinnedStamp = slot;
        else if (startStamp.IsValid && skinnedStamp.IsValid && shown.Count > 0) stamps.Enqueue((startStamp, skinnedStamp, slot));
        while (stamps.TryPeek(out var s))
        {
            if (arena.TryRead(s.Start, out ulong a) && arena.TryRead(s.Skinned, out ulong m) && arena.TryRead(s.End, out ulong b))
            {
                gpuSamples.Add(((m - a) / 1e6, (b - a) / 1e6));
                if (gpuSamples.Count > 60) gpuSamples.RemoveAt(0);
            }
            else if (ctx.Frame.Number - s.Start.Frame < 16) break;
            stamps.Dequeue();
        }
    }

    public void Dispose()
    {
        Release();
        sequence.Dispose();
        ctx.Shaders.Dispose(program);
    }
}
