using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>
/// Draws the characters of a <see cref="CharacterDrawList"/> (docs/character-renderer.md): skinned meshes with their clothing, hair,
/// head and bone-attached weapons, posed from the animation layers the host gives, lit like the world's buildings.
/// <list type="bullet">
/// <item><see cref="Update"/> once per frame, before the shadow cascades: asks the host's <see cref="Source"/> for the list, builds assets for
/// appearances not seen yet, poses every character in range in parallel into the frame's bone-palette buffer, and writes the materials of the
/// parts in view.</item>
/// <item><see cref="Draw"/> / <see cref="DrawDepth"/> for each view (the scene's near slice, a shadow cascade): cull, pick each part's mesh LOD, group
/// the instances by (mesh part, level) and record one instanced draw each.</item>
/// </list>
/// </summary>
internal sealed unsafe class CharacterRenderer : IDisposable
{
    public GpuContext Gpu { get; }
    readonly NativeFrame nativeFrame;
    readonly CharProg colourProg, depthProg;
    readonly CharacterContent content;
    readonly Dictionary<CharacterAppearance, CharacterAsset?> assets = new(ReferenceEqualityComparer.Instance);

    /// <summary>The characters of this frame: filled by <see cref="Source"/> inside <see cref="Update"/>, or by the host before it.</summary>
    public CharacterDrawList Draws { get; } = new();
    /// <summary>Called at the start of <see cref="Update"/> with the (cleared) list to fill; null: the host fills <see cref="Draws"/> itself.</summary>
    public Action<CharacterDrawList>? Source { get; set; }

    /// <summary>Characters farther than this from the eye are neither posed nor drawn.</summary>
    public float DrawDistance { get; set; } = 6000;
    /// <summary>Assets built per <see cref="Update"/> (0: all of them, for stills).</summary>
    public int LoadBudget { get; set; } = 4;
    /// <summary>Ogre's camera LOD bias as a distance factor (above 1: coarser levels sooner).</summary>
    public float LodBias { get; set; } = 1;

    // Statistics of the last Update / Draw.
    public int Posed { get; private set; }
    public int DrawnCharacters { get; private set; }
    public int DrawnParts { get; private set; }
    public long DrawnTriangles { get; private set; }
    public int DrawCalls { get; private set; }
    public double LastUpdateMs { get; private set; }
    public double LastPoseMs { get; private set; }
    public double LastDrawCpuMs { get; private set; }
    public int Assets => assets.Count(a => a.Value is not null);
    public List<string> Messages => content.Messages;

    /// <summary>One character in range this frame.</summary>
    struct Live
    {
        public CharacterAsset Asset;
        public Matrix4x4 World;
        public Vector3 Centre;
        public float Radius;
        public int BoneBase, AttachBase, LayerStart, LayerCount;
    }

    Live[] live = new Live[256];
    int liveCount;
    ActiveLayer[] layers = new ActiveLayer[1024];
    int layerCount;
    Matrix4x4[] attach = new Matrix4x4[256];
    int attachCount;
    Transient bones, materials;
    readonly List<AssetPart> slotParts = [];
    long updateStamp;
    float textureBias;
    uint textureStandIn;

    public CharacterRenderer(GpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assetLocator)
    {
        Gpu = gpu;
        nativeFrame = new NativeFrame(gpu, extraStorage: 2);
        colourProg = new CharProg(gpu, nativeFrame, CharacterShaders.Vertex(), CharacterShaders.Fragment(), "characters");
        depthProg = new CharProg(gpu, nativeFrame, CharacterShaders.DepthVertex(), CharacterShaders.DepthFragment(), "characters depth");
        content = new CharacterContent(gpu, install, db, assetLocator);
    }

    public VramGuard? Guard { get => content.Textures.Guard; set => content.Textures.Guard = value; }

    // ------------------------------------------------------------------ per frame

    /// <summary>
    /// The frame's characters: the list from <see cref="Source"/>, assets built for new appearances (at most <see cref="LoadBudget"/>), every
    /// character within <see cref="DrawDistance"/> of <paramref name="eye"/> posed in parallel into the frame's bone palettes, the textures'
    /// bindless indices and the parts' materials written. Render thread; before the frame's first <see cref="Draw"/> or <see cref="DrawDepth"/>.
    /// </summary>
    public void Update(Vector3 eye)
    {
        var watch = Stopwatch.StartNew();
        Draws.Clear();
        Source?.Invoke(Draws);
        content.Textures.Pump(wait: LoadBudget == 0, max: 8, budgetMs: 1.0);
        textureBias = Gpu.LodBias;
        textureStandIn = Gpu.StandIn2D;
        updateStamp++;

        // 1. Assets and the characters in range, with their layers resolved (the rig's caches are filled here, on this thread).
        liveCount = layerCount = attachCount = 0;
        int built = 0, boneTotal = 0;
        var items = Draws.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var inst = items[i];
            if (!assets.TryGetValue(inst.Appearance, out var asset))
            {
                if (LoadBudget > 0 && built >= LoadBudget) continue;
                asset = content.Build(inst.Appearance);
                assets[inst.Appearance] = asset;
                built++;
            }
            if (asset is null) continue;
            var centre = inst.Position + new Vector3(0, asset.Height * 0.5f, 0);
            if (Vector3.Distance(eye, centre) - asset.Radius > DrawDistance) continue;
            if (liveCount == live.Length) Array.Resize(ref live, liveCount * 2);
            ref var l = ref live[liveCount++];
            l.Asset = asset;
            l.World = Matrix4x4.CreateRotationY(inst.Yaw) * Matrix4x4.CreateTranslation(inst.Position);
            l.Centre = centre;
            l.Radius = asset.Radius;
            l.BoneBase = boneTotal;
            boneTotal += asset.Rig.BoneCount;
            l.AttachBase = attachCount;
            attachCount += asset.AttachCount;
            l.LayerStart = layerCount;
            var rig = asset.Rig;
            if (inst.Poses is { } poses)
                for (int p = 0; p < poses.Count; p++)
                    if (rig.Layer(content.Database, poses[p].Animation) is { } def) AddLayer(new ActiveLayer(def, poses[p].Time, poses[p].Weight));
            foreach (var (def, time) in asset.Fixed) AddLayer(new ActiveLayer(def, time, 1));
            l.LayerCount = layerCount - l.LayerStart;
        }
        Posed = liveCount;
        if (attach.Length < attachCount) attach = new Matrix4x4[Math.Max(attachCount, attach.Length * 2)];

        // 2. The bone palettes, posed in parallel straight into the frame's memory.
        var poseWatch = Stopwatch.StartNew();
        bones = Gpu.Frame.Constants.Allocate((ulong)Math.Max(boneTotal, 1) * 64, 256);
        nint bonePointer = (nint)bones.Pointer;
        var liveArray = live;
        var layerArray = layers;
        var attachArray = attach;
        if (liveCount > 0)
            Parallel.For(0, liveCount, i =>
            {
                ref var l = ref liveArray[i];
                var asset = l.Asset;
                var rig = asset.Rig;
                var scratch = rig.Rent();
                scratch.Pose(asset.Shape, layerArray.AsSpan(l.LayerStart, l.LayerCount), new Span<Matrix4x4>((byte*)bonePointer + (long)l.BoneBase * 64, rig.BoneCount));
                foreach (var part in asset.Parts)
                    if (part.AttachIndex >= 0) attachArray[l.AttachBase + part.AttachIndex] = part.Offset * scratch.BoneTransform(part.Bone);
                rig.Return(scratch);
            });
        LastPoseMs = poseWatch.Elapsed.TotalMilliseconds;

        // 3. The materials of the parts in range.
        slotParts.Clear();
        for (int i = 0; i < liveCount; i++)
            foreach (var part in live[i].Asset.Parts)
                if (part.MaterialFrame != updateStamp)
                {
                    part.MaterialFrame = updateStamp;
                    part.MaterialSlot = slotParts.Count;
                    slotParts.Add(part);
                }
        materials = Gpu.Frame.Constants.Allocate((ulong)Math.Max(slotParts.Count, 1) * CharacterMaterialRecord.Size, 256);
        for (int s = 0; s < slotParts.Count; s++)
            Write(slotParts[s].Material, ref *(CharacterMaterialRecord*)(materials.Pointer + (long)s * CharacterMaterialRecord.Size));
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
    }

    void AddLayer(in ActiveLayer layer)
    {
        if (layerCount == layers.Length) Array.Resize(ref layers, layerCount * 2);
        layers[layerCount++] = layer;
    }

    uint Index(WorldTexture? t, out bool present)
    {
        uint key = t?.Key ?? 0;
        present = key != 0;
        return present ? content.Textures.Index(key, textureBias, textureStandIn) : textureStandIn;
    }

    void Write(PartMaterial m, ref CharacterMaterialRecord r)
    {
        r = default;
        Span<bool> has = stackalloc bool[CharacterShaders.Slots];
        for (int i = 0; i < CharacterShaders.Slots; i++) r.Tex[i] = Index(m.Tex[i], out has[i]);
        for (int i = CharacterShaders.Slots; i < 20; i++) r.Tex[i] = textureStandIn;
        uint flags = 0;
        if (has[CharacterShaders.SlotDiffuse]) flags |= CharacterShaders.HasDiffuse;
        if (has[CharacterShaders.SlotNormal]) flags |= CharacterShaders.HasNormal;
        if (m.Swizzled || m.Tex[CharacterShaders.SlotNormal] is { Swizzled: true }) flags |= CharacterShaders.NormalSwizzled;
        if (has[CharacterShaders.SlotHeadDiffuse]) flags |= CharacterShaders.HasHead;
        if (has[CharacterShaders.SlotHeadNormal]) flags |= CharacterShaders.HasHeadNormal;
        if (has[CharacterShaders.SlotHeadMask]) flags |= CharacterShaders.HasHeadMask;
        if (has[CharacterShaders.SlotBodyMask]) flags |= CharacterShaders.HasBodyMask;
        if (has[CharacterShaders.SlotHairOverlay]) flags |= CharacterShaders.HasHair;
        if (has[CharacterShaders.SlotBeardOverlay]) flags |= CharacterShaders.HasBeard;
        if (m.ClipOnNormalAlpha && has[CharacterShaders.SlotNormal]) flags |= CharacterShaders.ClipNormalAlpha;
        if (m.Dyed && has[CharacterShaders.SlotColourMap]) flags |= CharacterShaders.Dyed;
        int vests = 0;
        for (int i = 0; i < 3; i++)
        {
            int s = CharacterShaders.SlotVest + 3 * i;
            if (has[s] && has[s + 1]) vests = i + 1;
            if (m.VestColoured[i] && has[s + 2]) flags |= CharacterShaders.Vest0Coloured << i;
        }
        r.Shading = m.Shading;
        r.Flags = flags;
        r.VestCount = (uint)vests;
        r.AlphaThreshold = m.AlphaThreshold;
        r.ShirtColour = new Vector4(m.ShirtColour, 1);
        r.SkinTone = new Vector4(m.SkinTone, 0);
        r.HairColour = new Vector4(m.HairColour, 1);
        r.HairAlpha = m.HairAlpha; r.HairMult = m.HairMult; r.BeardAlpha = m.BeardAlpha;
        r.DiffuseChannel = m.DiffuseChannel; r.AlphaChannel = m.AlphaChannel;
        r.Colour1 = m.Colour1; r.Colour2 = m.Colour2;
    }

    /// <summary>Waits for everything wanted to be built and loaded (stills): every appearance of the list, every texture.</summary>
    public void Settle(Vector3 eye, int timeoutMs = 120000)
    {
        int old = LoadBudget;
        LoadBudget = 0;
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Update(eye);
                content.Textures.Pump(wait: true);
                if (content.Textures.PendingCount == 0) break;
                Thread.Sleep(1);
            }
            Update(eye);   // the indices of what has just landed
        }
        finally { LoadBudget = old; }
    }

    public string Describe() =>
        $"{Assets} appearances, {content.MeshCount} meshes ({content.MeshBytes / 1048576.0:0.0} MB), {content.Textures.Describe()}";

    // ------------------------------------------------------------------ drawing

    sealed class Batch
    {
        public required GpuObjectPart Part;
        public int Level;
        public bool DoubleSided;
        public CharacterInstance80[] Data = new CharacterInstance80[16];
        public int Count, Offset;

        public void Add(in CharacterInstance80 i)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = i;
        }
    }

    readonly Dictionary<(GpuObjectPart, int, bool), Batch> batchMap = [];
    readonly List<Batch> active = [];
    Transient instances;
    bool depthPass;

    /// <summary>Draws the characters seen from <paramref name="eye"/> through <paramref name="frustum"/> into the open pass.</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, Vector3 light, Vector3 fogColour, float fogDistance) =>
        DrawView(new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance }, eye, frustum);

    /// <summary>Draws the characters' depth for a shadow cascade (<see cref="CharacterShaders.DepthFragment"/>); levels chosen from <paramref name="lodEye"/>.</summary>
    public void DrawDepth(Matrix4x4 worldToClip, Vector3 lodEye, Vector4[] frustum)
    {
        depthPass = true;
        try { DrawView(new ViewConstants { ViewProjection = worldToClip, Eye = lodEye }, lodEye, frustum); }
        finally { depthPass = false; }
    }

    static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
    {
        foreach (var p in planes)
            if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z)) return false;
        return true;
    }

    void DrawView(in ViewConstants view, Vector3 eye, Vector4[] frustum)
    {
        var cpu = Stopwatch.StartNew();
        DrawnCharacters = DrawnParts = DrawCalls = 0;
        DrawnTriangles = 0;
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();

        // 1. Cull, choose each part's level and group the instances by (mesh part, level, sidedness).
        for (int i = 0; i < liveCount; i++)
        {
            ref var l = ref live[i];
            if (!SphereVisible(frustum, l.Centre, l.Radius)) continue;
            DrawnCharacters++;
            foreach (var part in l.Asset.Parts)
            {
                Matrix4x4 model = l.World;
                if (!part.Shared) model = attach[l.AttachBase + part.AttachIndex] * l.World;
                var mesh = part.Mesh;
                float value = MeshLod.Value(eye, Vector3.Transform(part.BoundsCentre, model), part.BoundsRadius * model.ScaleLength(), LodBias);
                int level = MeshLod.Select(mesh.Distances, value);
                bool twoSided = part.Material.DoubleSided;
                var instance = new CharacterInstance80 { Model = model, BoneBase = (uint)l.BoneBase, Material = (uint)part.MaterialSlot };
                DrawnParts++;
                foreach (var gp in mesh.Parts)
                {
                    int lv = Math.Min(level, gp.Count.Length - 1);
                    while (lv > 0 && gp.Count[lv] == 0) lv--;
                    if (gp.Count[lv] == 0) continue;
                    var key = (gp, lv, twoSided);
                    if (!batchMap.TryGetValue(key, out var batch)) batchMap[key] = batch = new Batch { Part = gp, Level = lv, DoubleSided = twoSided };
                    if (batch.Count == 0) active.Add(batch);
                    batch.Add(in instance);
                }
            }
        }
        if (active.Count == 0) { LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds; return; }
        // Single-sided first, then the double-sided (the cull mode changes once).
        active.Sort((a, b) => a.DoubleSided.CompareTo(b.DoubleSided));

        // 2. The instances: each batch's contiguous in this view's constants (the draws reach a batch by firstInstance).
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        instances = Gpu.Frame.Constants.Allocate((ulong)total * CharacterInstance80.Size, 16);
        var target = new Span<byte>(instances.Pointer, total * CharacterInstance80.Size);
        foreach (var b in active)
            MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * CharacterInstance80.Size)..]);

        // 3. One draw per batch.
        var prog = depthPass ? depthProg : colourProg;
        string label = depthPass ? "characters depth" : "characters";
        var targets = Gpu.CurrentTargets();
        var state = Gpu.CurrentState() with { Cull = Vk.CullModeFlags.None };
        var job = drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Layout, job.Count) = (this, targets, state, prog.P.Layout, 0);
        job.Frame = nativeFrame.Prepare(in view, [bones.Binding, materials.Binding]);
        job.Cull = depthPass ? Vk.CullModeFlags.None : Vk.CullModeFlags.BackBit;
        for (int a = 0; a < 5; a++) job.Rows[a] = new BufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        if (job.Draws.Length < active.Count) job.Draws = new DrawJob.Draw[Math.Max(active.Count, job.Draws.Length * 2)];
        int n = 0;
        foreach (var b in active)
        {
            var part = b.Part;
            ref var native = ref (depthPass ? ref part.DepthNative : ref part.ColourNative);
            Current(ref native, part, prog);
            job.Draws[n++] = new DrawJob.Draw
            {
                Pipeline = Gpu.Pipelines.Get(state.Pipeline(prog.P, native.Layout!, Vk.PrimitiveTopology.TriangleList, targets.Formats, label)),
                Vertices = native.Vertices, Elements = new BufferBinding(native.Elements.Buffer, native.Elements.Offset + (ulong)part.Offset[b.Level] * 4),
                IndexCount = (uint)part.Count[b.Level], Instances = (uint)b.Count, FirstInstance = (uint)b.Offset, TwoSided = b.DoubleSided,
            };
            DrawCalls++;
            DrawnTriangles += (long)part.Count[b.Level] / 3 * b.Count;
        }
        job.Count = n;
        Gpu.Record(label, job);
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
    }

    /// <summary>A part's native state for <paramref name="p"/>, made on first use: the vertex layout (the per-instance rows at 7 to 10 and the data at 11 are bound once per segment).</summary>
    static void Current(ref ObjectNativeMesh n, GpuObjectPart part, CharProg p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, Vk.Format.R32G32B32A32Sfloat, CharacterInstance80.Size, true);
        attributes[CharacterShaders.DataLocation] = new LegacyProgram.Attribute(default, Vk.Format.R32G32B32A32Uint, CharacterInstance80.Size, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new BufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    readonly JobPool<DrawJob> drawJobs = new();

    /// <summary>One characters segment as prepared (<see cref="DrawView"/>): recorded on any thread.</summary>
    sealed class DrawJob : RecordJob
    {
        public struct Draw
        {
            public GraphicsPipeline Pipeline;
            public BufferBinding[] Vertices;
            public BufferBinding Elements;
            public uint IndexCount, Instances, FirstInstance;
            public bool TwoSided;
        }

        public CharacterRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public Vk.PipelineLayout Layout;
        public FrameBinding Frame;
        public Vk.CullModeFlags Cull;
        public readonly BufferBinding[] Rows = new BufferBinding[5];
        public Draw[] Draws = new Draw[64];
        public int Count;
        public override int Size => Count;

        public override void Record(CommandList cmd)
        {
            var (targets, state, layout) = (Targets, State, Layout);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, layout, in Frame);
            cmd.BindVertexBuffers(CharacterShaders.InstanceLocation, Rows);
            var push = new CharacterPush();
            cmd.PushConstants(layout, Vk.ShaderStageFlags.VertexBit | Vk.ShaderStageFlags.FragmentBit, in push);
            bool? twoSided = null;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                if (twoSided != d.TwoSided)
                {
                    cmd.SetRaster(d.TwoSided ? Vk.CullModeFlags.None : Cull, state.Front);
                    twoSided = d.TwoSided;
                }
                cmd.BindPipeline(d.Pipeline);
                cmd.BindVertexBuffers(0, d.Vertices);
                cmd.BindIndexBuffer(d.Elements, Vk.IndexType.Uint32);
                cmd.DrawIndexed(d.IndexCount, d.Instances, 0, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Array.Clear(Draws, 0, Count);
            Count = 0;
            Owner.drawJobs.Return(this);
        }
    }

    /// <summary>A native program with its vertex inputs from the reflection (as the objects' <c>ObjProg</c>): a disabled attribute reads GL's constant through a stride-0 binding.</summary>
    sealed class CharProg : IDisposable
    {
        readonly GpuContext ctx;
        public readonly ShaderProgram P;
        readonly int[] locations;
        readonly Meitou.Rendering.Gpu.Shaders.ScalarKind[] kinds;
        /// <summary>The program's own input locations (below the instance rows): 0 .. Own − 1.</summary>
        public readonly int Own;
        VertexLayout? last;

        public CharProg(GpuContext ctx, NativeFrame frame, string vertex, string fragment, string name)
        {
            this.ctx = ctx;
            P = frame.Program(vertex, fragment, name);
            var inputs = P.VertexReflection!.Inputs;
            locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
            kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
            Own = locations.Where(l => l < CharacterShaders.InstanceLocation).DefaultIfEmpty(-1).Max() + 1;
        }

        public VertexLayout Layout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
        {
            Span<VertexInput> inputs = stackalloc VertexInput[locations.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                int loc = locations[i];
                inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                    : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(kinds[i]), 0, false);
            }
            if (last is { } l && inputs.SequenceEqual(l.Inputs)) return l;
            return last = new VertexLayout(inputs.ToArray());
        }

        public BufferBinding[] Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, int count)
        {
            var result = new BufferBinding[count];
            for (int loc = 0; loc < count; loc++)
            {
                int input = Array.IndexOf(locations, loc);
                result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? a.Buffer
                    : new BufferBinding(ctx.Defaults.DummyVertex.Buffer, GlConventions.DummyVertexOffset(input >= 0 ? kinds[input] : Meitou.Rendering.Gpu.Shaders.ScalarKind.Float));
            }
            return result;
        }

        public void Dispose()
        {
            ctx.Pipelines.Forget(P);
            P.Dispose();
        }
    }

    public void Dispose()
    {
        colourProg.Dispose();
        depthProg.Dispose();
        nativeFrame.Dispose();
        content.Textures.Dispose();
    }
}

static class MatrixExtensions
{
    /// <summary>The largest axis scale of a transform's upper 3x3 (a bounding radius through it).</summary>
    public static float ScaleLength(this in Matrix4x4 m) =>
        MathF.Sqrt(Math.Max(new Vector3(m.M11, m.M12, m.M13).LengthSquared(), Math.Max(new Vector3(m.M21, m.M22, m.M23).LengthSquared(), new Vector3(m.M31, m.M32, m.M33).LengthSquared())));
}
