using System.Diagnostics;
using System.Numerics;
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
internal sealed unsafe partial class CharacterRenderer : IDisposable
{
    GpuContext Gpu { get; }
    readonly NativeFrame nativeFrame;
    readonly ReflectedProgram colourProg, depthProg, motionProg;
    readonly CharacterContent content;
    readonly PassTimer colourTimer, depthTimer;
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
    /// <summary>Further factor on the LOD value in the shadow cascades (a shadow hides detail: coarser levels sooner).</summary>
    public float ShadowLodBias { get; set; } = 2;

    // Statistics of the last Update / Draw.
    public int Posed { get; private set; }
    public int DrawnCharacters { get; private set; }
    public int DrawnParts { get; private set; }
    public long DrawnTriangles { get; private set; }
    public int DrawCalls { get; private set; }
    /// <summary>Part instances drawn by mesh LOD level in the last view, and their triangles.</summary>
    public readonly int[] LevelParts = new int[8];
    public readonly long[] LevelTriangles = new long[8];
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
        public CharState State;
        public bool Repose;
    }

    /// <summary>What the renderer keeps of a character between frames (by <see cref="CharacterInstance.Key"/>): its last posed palette and the one before.</summary>
    sealed class CharState
    {
        public Matrix4x4[] Bones = [], Previous = [], Attach = [], PreviousAttach = [];
        public Matrix4x4 World, PreviousWorld;
        public bool HasWorld;
        public long Posed = -1, Seen;
    }

    readonly Dictionary<long, CharState> states = [];
    /// <summary>Characters farther than these distances are posed every 2nd, 4th and 8th frame (a still holds the last palette); 0: every frame.</summary>
    public float[] PoseRangeSteps { get; set; } = [120, 300, 600];

    int PoseInterval(float distance)
    {
        int interval = 1;
        foreach (float range in PoseRangeSteps)
            if (distance > range) interval *= 2;
        return interval;
    }

    Live[] live = new Live[256];
    int liveCount;
    ActiveLayer[] layers = new ActiveLayer[1024];
    int layerCount;
    Matrix4x4[] attach = new Matrix4x4[256];
    int attachCount;
    Transient bones, previousBones, materials;
    readonly List<AssetPart> slotParts = [];
    long updateStamp;
    readonly CharacterTextureBudget budget = new();

    /// <summary>Tells the character textures' mip streaming the camera: the render size in pixels and the vertical field of view (the upscaler's LOD bias is the context's).</summary>
    public void SetView(int width, int height, float fieldOfView)
    {
        content.Textures.Mips.SetView(width, height, fieldOfView, Gpu.LodBias);
        viewKnown = true;
    }

    bool viewKnown;
    /// <summary>Wait, at the next <see cref="Update"/> after <see cref="SetView"/>, for every asset and texture (stills).</summary>
    public bool SettleFirst { get; set; }
    float textureBias;
    uint textureStandIn;

    public CharacterRenderer(GpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assetLocator)
    {
        Gpu = gpu;
        nativeFrame = new NativeFrame(gpu, extraStorage: 4);
        colourProg = new ReflectedProgram(gpu, nativeFrame, CharacterShaders.Vertex(), CharacterShaders.Fragment(), "characters", CharacterShaders.InstanceLocation);
        depthProg = new ReflectedProgram(gpu, nativeFrame, CharacterShaders.DepthVertex(), CharacterShaders.DepthFragment(), "characters depth", CharacterShaders.InstanceLocation);
        motionProg = new ReflectedProgram(gpu, nativeFrame, CharacterShaders.MotionVertex(), CharacterShaders.MotionFragment(), "characters motion", CharacterShaders.InstanceLocation);
        content = new CharacterContent(gpu, install, db, assetLocator);
        colourTimer = new PassTimer(gpu);
        depthTimer = new PassTimer(gpu);
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
        if (SettleFirst && viewKnown)
        {
            SettleFirst = false;
            var settleWatch = Stopwatch.StartNew();
            Settle(eye);
            Console.WriteLine($"crowd     built and loaded: {Describe()} ({settleWatch.ElapsedMilliseconds} ms)");
            foreach (var m in Messages.Distinct().Take(20)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
        }
        PollTimers();
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
            if (!states.TryGetValue(inst.Key, out var state)) states[inst.Key] = state = new CharState();
            state.Seen = updateStamp;
            l.State = state;
            state.PreviousWorld = state.HasWorld ? state.World : l.World;
            (state.World, state.HasWorld) = (l.World, true);
            l.Repose = state.Posed < 0 || state.Bones.Length != asset.Rig.BoneCount || (updateStamp + inst.Key) % PoseInterval(Vector3.Distance(eye, centre)) == 0;
            l.LayerStart = layerCount;
            var rig = asset.Rig;
            if (inst.Poses is { } poses)
                for (int p = 0; p < poses.Count; p++)
                    if (rig.Layer(content.Database, poses[p].Animation) is { } def) AddLayer(new ActiveLayer(def, poses[p].Time, poses[p].Weight));
            foreach (var (def, time) in asset.Fixed) AddLayer(new ActiveLayer(def, time, 1));
            l.LayerCount = layerCount - l.LayerStart;
        }
        Posed = 0;
        for (int i = 0; i < liveCount; i++) if (live[i].Repose) Posed++;
        if (updateStamp % 120 == 0) EvictStates();
        if (attach.Length < attachCount) attach = new Matrix4x4[Math.Max(attachCount, attach.Length * 2)];

        // 2. The bone palettes, posed in parallel straight into the frame's memory.
        double gatherMs = watch.Elapsed.TotalMilliseconds;
        var poseWatch = Stopwatch.StartNew();
        bones = Gpu.Frame.Constants.Allocate((ulong)Math.Max(boneTotal, 1) * 64, 256);
        nint bonePointer = (nint)bones.Pointer;
        previousBones = Motion ? Gpu.Frame.Constants.Allocate((ulong)Math.Max(boneTotal, 1) * 64, 256) : bones;
        nint previousPointer = (nint)previousBones.Pointer;
        bool motion = Motion;
        var liveArray = live;
        var layerArray = layers;
        var attachArray = attach;
        long stamp = updateStamp;
        if (liveCount > 0)
            Parallel.For(0, liveCount, i =>
            {
                ref var l = ref liveArray[i];
                var asset = l.Asset;
                var rig = asset.Rig;
                var state = l.State;
                if (l.Repose)
                {
                    if (state.Bones.Length != rig.BoneCount) { state.Bones = new Matrix4x4[rig.BoneCount]; state.Previous = new Matrix4x4[rig.BoneCount]; state.Attach = new Matrix4x4[asset.AttachCount]; state.PreviousAttach = new Matrix4x4[asset.AttachCount]; state.Posed = -1; }
                    (state.Bones, state.Previous) = (state.Previous, state.Bones);
                    state.Attach.CopyTo(state.PreviousAttach, 0);
                    var scratch = rig.Rent();
                    scratch.Pose(asset.Shape, layerArray.AsSpan(l.LayerStart, l.LayerCount), state.Bones);
                    foreach (var part in asset.Parts)
                        if (part.AttachIndex >= 0) state.Attach[part.AttachIndex] = part.Offset * scratch.BoneTransform(part.Bone);
                    rig.Return(scratch);
                    if (state.Posed < 0) { state.Bones.CopyTo(state.Previous, 0); state.Attach.CopyTo(state.PreviousAttach, 0); }
                    state.Posed = stamp;
                }
                state.Bones.CopyTo(new Span<Matrix4x4>((byte*)bonePointer + (long)l.BoneBase * 64, rig.BoneCount));
                state.Attach.CopyTo(attachArray, l.AttachBase);
                if (motion)
                {
                    // Last frame's palette: what was posed before this repose, or the current one when this frame did not repose (no bone motion).
                    (l.Repose ? state.Previous : state.Bones).CopyTo(new Span<Matrix4x4>((byte*)previousPointer + (long)l.BoneBase * 64, rig.BoneCount));
                    if (!l.Repose) state.Attach.CopyTo(state.PreviousAttach, 0);
                }
            });
        LastPoseMs = poseWatch.Elapsed.TotalMilliseconds;

        // 3. The materials of the parts in range.
        slotParts.Clear();
        for (int i = 0; i < liveCount; i++)
        {
            float distance = Vector3.Distance(eye, live[i].Centre);
            foreach (var part in live[i].Asset.Parts)
            {
                if (part.MaterialFrame != updateStamp)
                {
                    part.MaterialFrame = updateStamp;
                    part.MaterialSlot = slotParts.Count;
                    part.Near = float.PositiveInfinity;
                    slotParts.Add(part);
                }
                part.Near = Math.Min(part.Near, Math.Max(distance - live[i].Radius * 0.5f, 0.5f));
            }
        }
        // Mip streaming: each texture's nearest user over how large a texel of its part is there; textures load at the size that need allows.
        foreach (var part in slotParts)
        {
            float need = part.Near / part.UvScale * budget.NeedScale;
            foreach (var t in part.Material.Tex) t?.OfferNow(need);
        }
        budget.Update(content.Textures);
        materials = Gpu.Frame.Constants.Allocate((ulong)Math.Max(slotParts.Count, 1) * CharacterMaterialRecord.Size, 256);
        for (int s = 0; s < slotParts.Count; s++)
            Write(slotParts[s].Material, ref *(CharacterMaterialRecord*)(materials.Pointer + (long)s * CharacterMaterialRecord.Size));
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
        updateCount++;
        updateCpu.Add(LastUpdateMs);
        poseCpu.Add(LastPoseMs);
        gatherCpu.Add(gatherMs);
    }

    readonly List<long> staleKeys = [];

    /// <summary>Forgets the characters not seen for 60 updates.</summary>
    void EvictStates()
    {
        staleKeys.Clear();
        foreach (var (key, state) in states)
            if (updateStamp - state.Seen > 60) staleKeys.Add(key);
        foreach (var key in staleKeys) states.Remove(key);
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
        for (int i = CharacterShaders.Slots; i < CharacterShaders.TextureSlotCount; i++) r.Tex[i] = textureStandIn;
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
        $"{Assets} appearances, {content.MeshCount} meshes ({content.MeshBytes / 1048576.0:0.0} MB), {content.Textures.Describe()}, texture need x{budget.NeedScale:0.0} (mark {content.Textures.MarkMb:0} MB)";

    public void Dispose()
    {
        if (updateCount > 0) Console.WriteLine($"characters {Statistics()}");
        colourProg.Dispose();
        depthProg.Dispose();
        motionProg.Dispose();
        nativeFrame.Dispose();
        content.Textures.Dispose();
        content.Morphs.Dispose();
    }
}
