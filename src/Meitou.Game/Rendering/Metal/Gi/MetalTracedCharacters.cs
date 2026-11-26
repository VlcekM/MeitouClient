using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The characters in the Metal traced scene (docs/render-lights.md "Traced lamp shadows", docs/render-gi.md "Metal"): <see cref="TracedCharacters"/>'
/// rules with its skinning kernel (<see cref="TracedCharacters.Skin"/>): the nearest whose shadow can be in view, at the level the colour pass draws,
/// skinned with the frame's palettes into a vertex buffer of their own, each a bottom level refitted every frame it moves and built again now and then.
/// Render thread only.
/// </summary>
sealed unsafe class MetalTracedCharacters(MetalContext ctx) : IDisposable
{
    #region Constants and state

    /// <summary>
    /// The most characters, triangles in all, triangles of one part (<see cref="TracedCharacters"/>').
    /// </summary>
    const int MaxCharacters = TracedCharacters.MaxCharacters, MaxTriangles = 3_000_000, MaxGroupTriangles = 1 << 17;

    /// <summary>
    /// A structure is built again after this many refits (plus its key's low bits, so they do not all rebuild at once); unused this many updates
    /// it goes.
    /// </summary>
    const int RebuildEvery = 60, KeepFrames = 120;

    /// <summary>
    /// The skinned vertex: position at 0, texture coordinates at <see cref="GpuVertex.UvOffset"/>.
    /// </summary>
    const int Stride = 32;

    /// <summary>
    /// A held pose's part counts as moved past this shift or turn (<see cref="TracedCharacters"/>').
    /// </summary>
    const float PlacedShift = 0.05f, PlacedTurn = 1e-3f;

    /// <summary>
    /// <c>MTLAccelerationStructureInstanceOptions</c>: no facing culling.
    /// </summary>
    const uint NoCulling = 1;

    /// <summary>
    /// The structures by character key.
    /// </summary>
    readonly Dictionary<long, Entry> _entries = [];

    /// <summary>
    /// This update's candidates, the shown ones and the skinning jobs.
    /// </summary>
    readonly List<CharacterRenderer.RayCharacter> _characters = [];

    /// <summary>
    /// See <see cref="_characters"/>.
    /// </summary>
    readonly List<(CharacterRenderer.RayCharacter Character, Entry Entry, bool Fresh, bool Skin)> _shown = [];

    /// <summary>
    /// See <see cref="_characters"/>.
    /// </summary>
    readonly List<Job> _jobs = [];

    /// <summary>
    /// The pieces of the character being placed.
    /// </summary>
    readonly List<Piece> _pieces = [];

    /// <summary>
    /// The skinning kernel and its pipeline (made on first use), the triangle list 0, 1, 2... every structure indexes.
    /// </summary>
    (MetalProgram Program, GpuComputePipeline Pipeline) _skin;

    /// <summary>
    /// See <see cref="_skin"/>.
    /// </summary>
    MTLBuffer _sequence;

    /// <summary>
    /// The update count and this update's skinned triangles.
    /// </summary>
    long _frame;

    /// <summary>
    /// See <see cref="_frame"/>.
    /// </summary>
    int _jobTriangles;

    /// <summary>
    /// Whether any structure changed this update (the top level must be built again).
    /// </summary>
    public bool Moved { get; private set; }

    /// <summary>
    /// The characters in the scene this update.
    /// </summary>
    public int Shown => _shown.Count;

    /// <summary>
    /// One part of a character as built: its group, level, triangles and whether it is opaque.
    /// </summary>
    readonly record struct Piece(GpuObjectPart Group, int Level, int Triangles, bool Opaque);

    /// <summary>
    /// A character's structure: its vertex buffer, pieces, records, geometries, refit descriptor and scratch, how it was skinned.
    /// </summary>
    sealed class Entry : IDisposable
    {
        /// <summary>
        /// The structure (null before the first build), its descriptor and its refit scratch.
        /// </summary>
        public MTLAccelerationStructure? Structure;

        /// <summary>
        /// See <see cref="Structure"/>.
        /// </summary>
        public MTLPrimitiveAccelerationStructureDescriptor Descriptor;

        /// <summary>
        /// See <see cref="Structure"/>.
        /// </summary>
        public MTLBuffer RefitScratch;

        /// <summary>
        /// The skinned vertices, three a triangle.
        /// </summary>
        public required MTLBuffer Vertices;

        /// <summary>
        /// The pieces it was made for, a record and a geometry each.
        /// </summary>
        public required Piece[] Pieces;

        /// <summary>
        /// See <see cref="Pieces"/>.
        /// </summary>
        public required GeometryRecord[] Records;

        /// <summary>
        /// See <see cref="Pieces"/>.
        /// </summary>
        public required MTLAccelerationStructureTriangleGeometryDescriptor[] Geometries;

        /// <summary>
        /// Per piece the model, flags and morphs it was skinned with.
        /// </summary>
        public required (Matrix4x4 Model, uint Flags, uint Morphs)[] Skinned;

        /// <summary>
        /// The update it was last shown, the pose it was skinned in, its refits since the last build.
        /// </summary>
        public long LastUsed, SkinnedPose = long.MinValue;

        /// <summary>
        /// See <see cref="LastUsed"/>.
        /// </summary>
        public int Refits;

        /// <summary>
        /// Releases the structure, its descriptors, its scratch and its vertices.
        /// </summary>
        public void Dispose()
        {
            Structure?.Dispose();
            if (Descriptor.NativePtr != IntPtr.Zero) Descriptor.Dispose();
            foreach (var g in Geometries) g.Dispose();
            if (RefitScratch.NativePtr != IntPtr.Zero) RefitScratch.Dispose();
            Vertices.Dispose();
        }
    }

    /// <summary>
    /// A skinning job (<see cref="TracedCharacters.Skin"/>'s <c>Job</c>, 112 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Job
    {
        /// <summary>
        /// The part's model matrix.
        /// </summary>
        public Matrix4x4 Model;

        /// <summary>
        /// The source vertices, indices and the target, by address.
        /// </summary>
        public ulong Vertices, Indices, Target;

        /// <summary>
        /// Triangles, the palette's first bone, the morphs' base, the flags, the first triangle, padding.
        /// </summary>
        public uint Triangles, BoneBase, MorphBase, Flags, First, Pad1;
    }

    #endregion

    #region Update

    /// <summary>
    /// This update's characters: their pieces, each skinned where its pose or placement changed, then refitted or built into
    /// <paramref name="builds"/>.
    /// </summary>
    public void Update(MetalBuilds builds, CharacterRenderer people, Vector3 eye, float reach, float minAngle)
    {
        _frame++;
        _shown.Clear();
        _jobs.Clear();
        _jobTriangles = 0;
        Moved = false;
        people.RayCharacters(eye, MathF.Max(TracedCharacters.Range, reach), MaxCharacters, _characters, minAngle);
        int triangles = 0;

        foreach (var c in _characters)
        {
            int count = Pieces(c);
            if (triangles + count > MaxTriangles) break;

            triangles += count;
            var (entry, fresh) = Current(c);
            entry.LastUsed = _frame;
            bool skin = entry.Structure is null || entry.SkinnedPose != c.Pose || Placed(c, entry);
            Describe(c, entry);
            if (skin)
            {
                AddJobs(c, entry);
                entry.SkinnedPose = c.Pose;
            }

            _shown.Add((c, entry, fresh && skin, skin));
        }

        if (_jobs.Count > 0) SkinAndFit(builds, people);
        Evict();
    }

    /// <summary>
    /// The shown characters' instances (relative to <paramref name="origin"/>, at their anchors) and records.
    /// </summary>
    public void Emit(Action<MetalBottomLevel, Matrix4x4, byte, uint> add, List<GeometryRecord> records, Vector3 origin, int maxInstances,
        int maxRecords, Func<int> instances)
    {
        foreach (var (c, entry, _, _) in _shown)
        {
            if (entry.Structure is not { } structure) continue;
            if (instances() >= maxInstances || records.Count + entry.Records.Length > maxRecords) break;

            var level = new MetalBottomLevel(structure, entry.Records, [], [structure.NativePtr, entry.Vertices.NativePtr, _sequence.NativePtr], []);
            add(level, Matrix4x4.CreateTranslation(c.Anchor - origin), TracedSunShaders.Characters, NoCulling);
            records.AddRange(entry.Records);
        }
    }

    /// <summary>
    /// Drops every structure.
    /// </summary>
    public void Release()
    {
        _shown.Clear();
        foreach (var e in _entries.Values) ctx.DeferDelete(e.Dispose);
        _entries.Clear();
    }

    /// <summary>
    /// The pieces of <paramref name="c"/> into <see cref="_pieces"/>; their triangles.
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
    /// The character's entry for these pieces (a new one when they changed); fresh when it is to be built again.
    /// </summary>
    (Entry Entry, bool Fresh) Current(CharacterRenderer.RayCharacter c)
    {
        if (_entries.TryGetValue(c.Key, out var entry) && entry.Pieces.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(_pieces)))
            return (entry, entry.Refits >= RebuildEvery + (int)(c.Key & 15));

        if (entry is not null) ctx.DeferDelete(entry.Dispose);

        entry = NewEntry();
        _entries[c.Key] = entry;
        return (entry, true);
    }

    /// <summary>
    /// A vertex buffer for the pieces' triangles and a geometry and record per piece (the vertices three a triangle, indexed 0, 1, 2...).
    /// </summary>
    Entry NewEntry()
    {
        EnsureSequence();
        int count = _pieces.Sum(p => p.Triangles);
        var vertices = ctx.Device.NewBuffer((ulong)Math.Max(count, 1) * 3 * Stride, MTLResourceOptions.ResourceStorageModePrivate);
        vertices.Label = (NSString)"metal traced character vertices";
        var geometries = new MTLAccelerationStructureTriangleGeometryDescriptor[_pieces.Count];
        var records = new GeometryRecord[_pieces.Count];

        ulong first = 0;
        for (int i = 0; i < _pieces.Count; i++)
        {
            var p = _pieces[i];
            ulong offset = first * 3 * Stride;
            geometries[i] = new MTLAccelerationStructureTriangleGeometryDescriptor
            {
                VertexBuffer = vertices,
                VertexBufferOffset = offset,
                VertexStride = Stride,
                VertexFormat = MTLAttributeFormat.Float3,
                IndexBuffer = _sequence,
                IndexType = MTLIndexType.UInt32,
                TriangleCount = (ulong)p.Triangles,
                Opaque = p.Opaque,
            };
            records[i] = new GeometryRecord
            {
                Vertices = vertices.GpuAddress + offset, Indices = _sequence.GpuAddress, Stride = Stride, Kind = GeometryRecord.Character,
            };
            first += (ulong)p.Triangles;
        }

        return new Entry
        {
            Vertices = vertices, Pieces = [.. _pieces], Records = records, Geometries = geometries,
            Skinned = new (Matrix4x4, uint, uint)[_pieces.Count],
        };
    }

    /// <summary>
    /// Whether a held pose's part moved or changed since it was skinned.
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
    /// This update's records: the cut-out as the shadow draws make it (the channel in the diffuse index), the colour maps and tint, interior.
    /// </summary>
    static void Describe(CharacterRenderer.RayCharacter c, Entry entry)
    {
        for (int i = 0; i < c.Parts.Count; i++)
        {
            var part = c.Parts[i];
            ref var record = ref entry.Records[i];
            (record.AlphaTexture, record.AlphaThreshold, record.Texture) = (part.AlphaMap, part.AlphaCut, part.Channel);
            (record.CharacterDiffuse, record.CharacterHead, record.CharacterTint) = (part.Diffuse, part.Head, part.Tint);
            record.Interior = c.Interior ? 1u : 0u;
        }
    }

    /// <summary>
    /// A skinning job per part of <paramref name="c"/>.
    /// </summary>
    void AddJobs(CharacterRenderer.RayCharacter c, Entry entry)
    {
        for (int i = 0; i < c.Parts.Count; i++)
        {
            var part = c.Parts[i];
            var group = part.Group;
            entry.Skinned[i] = (part.Model, part.Flags, part.MorphBase);
            var vertices = ctx.BufferAt(group.Vertices.Handle);
            var indices = ctx.BufferAt(group.Indices.Handle);
            _jobs.Add(new Job
            {
                Model = part.Model,
                Vertices = vertices.GpuAddress,
                Indices = indices.GpuAddress + (ulong)group.Offset[part.Level] * 4,
                Target = entry.Records[i].Vertices,
                Triangles = (uint)entry.Pieces[i].Triangles,
                BoneBase = c.BoneBase,
                MorphBase = part.MorphBase,
                Flags = part.Flags,
                First = (uint)_jobTriangles,
            });
            _jobTriangles += entry.Pieces[i].Triangles;
        }
    }

    #endregion

    #region Skinning and structures

    /// <summary>
    /// The skinning kernel over every job, then each skinned character's structure refitted or built (after the frame's other bottom levels).
    /// </summary>
    void SkinAndFit(MetalBuilds builds, CharacterRenderer people)
    {
        builds.End();
        Skin(people);
        foreach (var (_, entry, fresh, skin) in _shown)
        {
            if (!skin) continue;

            Moved = true;
            if (fresh || entry.Structure is null) Build(builds, entry);
            else
            {
                builds.Refit(entry.Structure.Value, entry.Descriptor, entry.RefitScratch);
                entry.Refits++;
            }
        }
    }

    /// <summary>
    /// One thread a triangle over every job (<see cref="TracedCharacters.Skin"/>), the sources and targets made resident.
    /// </summary>
    void Skin(CharacterRenderer people)
    {
        _skin = _skin.Program is null ? MakeSkin() : _skin;
        var jobs = ctx.Frame.AllocateConstants((ulong)(_jobs.Count * sizeof(Job)), 256);
        CollectionsMarshal.AsSpan(_jobs).CopyTo(new Span<Job>((void*)jobs.Pointer, _jobs.Count));
        var palettes = people.RayBones;
        var morphs = people.RayMorphs is { Size: > 0 } m ? m : palettes;

        var cmd = (MetalCommandList)ctx.BeginNative("traced characters");
        cmd.BindPipeline(_skin.Pipeline);
        var e = cmd.ComputeEncoder();
        var (bones, bonesOffset) = ctx.Buffer(palettes);
        var (deltas, deltasOffset) = ctx.Buffer(morphs);
        e.SetBuffer(bones, bonesOffset, 0);
        e.SetBuffer(deltas, deltasOffset, 1);
        e.SetBuffer(ctx.BufferAt(jobs.Handle), jobs.Offset, 2);
        uint* counts = stackalloc uint[2] { (uint)_jobs.Count, (uint)_jobTriangles };
        e.SetBytes((nint)counts, 8, MslTranslator.PushConstantBinding);
        UseSources(cmd);
        cmd.Dispatch((uint)((_jobTriangles + 63) / 64));
        cmd.EndCompute();
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// The skinning kernel's sources (the parts' vertex and index buffers) and targets, reached by address, made resident.
    /// </summary>
    void UseSources(MetalCommandList cmd)
    {
        var resident = new HashSet<nint>();
        foreach (var (c, entry, _, skin) in _shown)
        {
            if (!skin) continue;

            resident.Add(entry.Vertices.NativePtr);
            foreach (var part in c.Parts)
            {
                resident.Add(ctx.BufferAt(part.Group.Vertices.Handle).NativePtr);
                resident.Add(ctx.BufferAt(part.Group.Indices.Handle).NativePtr);
            }
        }

        var list = resident.ToArray();
        cmd.UseResources(list, list.Length, MTLResourceUsage.Read | MTLResourceUsage.Write);
    }

    /// <summary>
    /// The skinning kernel, its push block named as the backend binds push constants.
    /// </summary>
    (MetalProgram, GpuComputePipeline) MakeSkin()
    {
        var shaders = (MetalShaders)ctx.Shaders;
        string glsl = TracedCharacters.Skin.Replace("uniform Count {", "uniform Push {", StringComparison.Ordinal);
        var program = shaders.ComputeDiscrete(glsl, "metal traced characters");
        return (shaders.Backend(program), ctx.Pipelines.Get(new ComputePipelineDesc(program, "metal traced characters")));
    }

    /// <summary>
    /// The triangle list 0, 1, 2... every character's structure indexes (its vertices are three a triangle).
    /// </summary>
    void EnsureSequence()
    {
        if (_sequence.NativePtr != IntPtr.Zero) return;

        _sequence = ctx.Device.NewBuffer((ulong)MaxGroupTriangles * 3 * 4, MTLResourceOptions.ResourceStorageModeShared);
        _sequence.Label = (NSString)"metal traced character indices";
        var list = new Span<uint>((void*)_sequence.Contents, MaxGroupTriangles * 3);
        for (int i = 0; i < list.Length; i++) list[i] = (uint)i;
    }

    /// <summary>
    /// A new refittable structure for <paramref name="entry"/> (the old one after the frames in flight).
    /// </summary>
    void Build(MetalBuilds builds, Entry entry)
    {
        if (entry.Structure is { } old)
        {
            var (scratch, descriptor) = (entry.RefitScratch, entry.Descriptor);
            ctx.DeferDelete(() =>
            {
                old.Dispose();
                descriptor.Dispose();
                scratch.Dispose();
            });
        }

        (entry.Structure, entry.Descriptor, entry.RefitScratch) = builds.Refittable("metal traced character", entry.Geometries);
        entry.Refits = 0;
    }

    /// <summary>
    /// Drops the structures not shown for <see cref="KeepFrames"/> updates.
    /// </summary>
    void Evict()
    {
        foreach (var key in _entries.Where(e => _frame - e.Value.LastUsed > KeepFrames).Select(e => e.Key).ToList())
        {
            ctx.DeferDelete(_entries[key].Dispose);
            _entries.Remove(key);
        }
    }

    /// <summary>
    /// A line for the log.
    /// </summary>
    public string Describe() => $"{_shown.Count} characters ({_entries.Count} structures)";

    /// <summary>
    /// Releases the structures and the triangle list.
    /// </summary>
    public void Dispose()
    {
        foreach (var e in _entries.Values) e.Dispose();
        _entries.Clear();
        if (_sequence.NativePtr != IntPtr.Zero) _sequence.Dispose();
    }

    #endregion
}
