using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    // ------------------------------------------------------------------ drawing

    sealed class Batch
    {
        public required GpuObjectPart Part;
        public int Level;
        public bool DoubleSided;
        public CharInstance[] Data = new CharInstance[16];
        public int Count, Offset;

        public void Add(in CharInstance i)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = i;
        }
    }

    readonly Dictionary<(GpuObjectPart, int, bool), Batch> batchMap = [];
    readonly List<Batch> active = [];
    Transient instances;
    bool depthPass, motionPass;

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

    void DrawView(in ViewConstants view, Vector3 eye, Vector4[] frustum)
    {
        var cpu = Stopwatch.StartNew();
        DrawnCharacters = DrawnParts = DrawCalls = 0;
        DrawnTriangles = 0;
        Array.Clear(LevelParts);
        Array.Clear(LevelTriangles);
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();

        // 1. Cull, choose each part's level and group the instances by (mesh part, level, sidedness).
        for (int i = 0; i < liveCount; i++)
        {
            ref var l = ref live[i];
            if (!FrustumTests.SphereVisible(frustum, l.Centre, l.Radius)) continue;
            DrawnCharacters++;
            foreach (var part in l.Asset.Parts)
            {
                Matrix4x4 model = l.World;
                if (!part.Shared) model = attach[l.AttachBase + part.AttachIndex] * l.World;
                var mesh = part.Mesh;
                float value = MeshLod.Value(eye, Vector3.Transform(part.BoundsCentre, model), part.BoundsRadius * model.ScaleLength(), depthPass ? LodBias * ShadowLodBias : LodBias);
                int level = MeshLod.Select(mesh.Distances, value);
                bool twoSided = part.Material.DoubleSided;
                var instance = new CharInstance { Model = model, BoneBase = (uint)l.BoneBase, Material = (uint)part.MaterialSlot, MorphBase = part.Morphed ? l.Asset.MorphBase : 0 };
                if (motionPass) instance.PreviousModel = part.Shared ? l.State.PreviousWorld : l.State.PreviousAttach[part.AttachIndex] * l.State.PreviousWorld;
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
        instances = Gpu.Frame.Constants.Allocate((ulong)total * CharInstance.Size, 16);
        var target = new Span<byte>(instances.Pointer, total * CharInstance.Size);
        foreach (var b in active)
            MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * CharInstance.Size)..]);

        // 3. One draw per batch.
        var prog = motionPass ? motionProg : depthPass ? depthProg : colourProg;
        string label = motionPass ? "characters motion" : depthPass ? "characters depth" : "characters";
        var targets = Gpu.CurrentTargets();
        var state = Gpu.CurrentState() with { Cull = Vk.CullModeFlags.None };
        var job = drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Layout, job.Count) = (this, targets, state, prog.P.Layout, 0);
        job.Frame = nativeFrame.Prepare(in view, [bones.Binding, materials.Binding, content.Morphs.Binding(Gpu.Frame), previousBones.Binding]);
        job.Cull = depthPass ? Vk.CullModeFlags.None : Vk.CullModeFlags.BackBit;
        for (int a = 0; a < 9; a++) job.Rows[a] = new BufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        job.Push = new CharacterPush { DepthIndex = motionPass ? motionDepth : 0 };
        job.RowCount = motionPass ? 9 : 5;
        if (job.Draws.Length < active.Count) job.Draws = new DrawJob.Draw[Math.Max(active.Count, job.Draws.Length * 2)];
        int n = 0;
        foreach (var b in active)
        {
            var part = b.Part;
            ref var native = ref (motionPass ? ref MotionNative(part) : ref depthPass ? ref part.DepthNative : ref part.ColourNative);
            Current(ref native, part, prog);
            job.Draws[n++] = new DrawJob.Draw
            {
                Pipeline = Gpu.Pipelines.Get(state.Pipeline(prog.P, native.Layout!, Vk.PrimitiveTopology.TriangleList, targets.Formats, label)),
                Vertices = native.Vertices, Elements = new BufferBinding(native.Elements.Buffer, native.Elements.Offset + (ulong)part.Offset[b.Level] * 4),
                IndexCount = (uint)part.Count[b.Level], Instances = (uint)b.Count, FirstInstance = (uint)b.Offset, TwoSided = b.DoubleSided,
            };
            DrawCalls++;
            DrawnTriangles += (long)part.Count[b.Level] / 3 * b.Count;
            LevelParts[Math.Min(b.Level, 7)] += b.Count;
            LevelTriangles[Math.Min(b.Level, 7)] += (long)part.Count[b.Level] / 3 * b.Count;
        }
        job.Count = n;
        var timer = depthPass ? depthTimer : colourTimer;
        timer.Begin();
        Gpu.Record(label, job);
        timer.End();
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
        if (!depthPass) drawCpu.Add(LastDrawCpuMs);
    }

    readonly Dictionary<GpuObjectPart, MotionHolder> motionNatives = [];
    sealed class MotionHolder { public ObjectNativeMesh Native; }
    ref ObjectNativeMesh MotionNative(GpuObjectPart part)
    {
        if (!motionNatives.TryGetValue(part, out var h)) motionNatives[part] = h = new MotionHolder();
        return ref h.Native;
    }

    /// <summary>A part's native state for <paramref name="p"/>, made on first use: the vertex layout (the per-instance rows at 7 to 10 and the data at 11 are bound once per segment).</summary>
    static void Current(ref ObjectNativeMesh n, GpuObjectPart part, ReflectedProgram p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, Vk.Format.R32G32B32A32Sfloat, CharInstance.Size, true);
        attributes[CharacterShaders.DataLocation] = new LegacyProgram.Attribute(default, Vk.Format.R32G32B32A32Uint, CharInstance.Size, true);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.PreviousLocation + a] = new LegacyProgram.Attribute(default, Vk.Format.R32G32B32A32Sfloat, CharInstance.Size, true);
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
        public readonly BufferBinding[] Rows = new BufferBinding[9];
        public int RowCount;
        public CharacterPush Push;
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
            cmd.BindVertexBuffers(CharacterShaders.InstanceLocation, Rows.AsSpan(0, RowCount));
            var push = Push;
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
}

static class MatrixExtensions
{
    /// <summary>The largest axis scale of a transform's upper 3x3 (a bounding radius through it).</summary>
    public static float ScaleLength(this in Matrix4x4 m) =>
        MathF.Sqrt(Math.Max(new Vector3(m.M11, m.M12, m.M13).LengthSquared(), Math.Max(new Vector3(m.M21, m.M22, m.M23).LengthSquared(), new Vector3(m.M31, m.M32, m.M33).LengthSquared())));
}
