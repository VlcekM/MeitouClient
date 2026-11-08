using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The blood on the ground (docs/game/blood-decals.md): the decal cells nearest the eye within the game's <c>Decal Range</c>, up to <see cref="FaithfulSlots"/> or <see cref="MaxSlots"/>, each drawn into
/// a layer of a <c>Decal Resolution</c>² texture array (the game's <c>Kenshi_Decal_Texture_n</c>), which the terrain's colour shader lays over its albedo
/// (<see cref="Apply"/>). A layer is drawn again only when its cell changes or a new cell takes it. Each decal is its texture × its colour, alpha blended into the
/// colour and added into the alpha, as the game's <c>Quad2DMaterial</c> draws it: one instanced draw per layer, each decal's texture by its bindless index.
/// </summary>
public sealed unsafe partial class TerrainDecalRenderer : IDisposable
{
    /// <summary>The cells drawn at once: the game's eight decal textures (<see cref="Meitou"/> off), up to <see cref="MaxSlots"/> with it on.</summary>
    public const int FaithfulSlots = 8, MaxSlots = 32;

    /// <summary>The most memory the Meitou layers take (<see cref="MaxSlots"/> at 1024², 16 at 2048²); the game's 8 whatever it costs.</summary>
    const long LayerBudget = 256L << 20;

    const string Vertex = """
        #version 450
        layout(location = 0) in vec4 aQuad;     // the centre and the width axis, in clip space
        layout(location = 1) in vec4 aColour;   // its alpha at most the life
        layout(location = 2) in vec2 aAxisH;    // the height axis
        layout(location = 3) in uint aSprite;   // the texture's bindless index
        layout(location = 0) out vec2 vUv;
        layout(location = 1) out vec4 vColour;
        layout(location = 2) flat out uint vSprite;
        void main()
        {
            vec2 q = vec2(gl_VertexIndex & 1, gl_VertexIndex >> 1);
            vUv = q;
            vColour = aColour;
            vSprite = aSprite;
            gl_Position = vec4(aQuad.xy + (q.x - 0.5) * aQuad.zw + (q.y - 0.5) * aAxisH, 0.0, 1.0);
        }
        """;

    static readonly string Fragment = "#version 450\n" + BindlessTable.Declarations(NativeShaders.BindlessSet) + """
        layout(location = 0) in vec2 vUv;
        layout(location = 1) in vec4 vColour;
        layout(location = 2) flat in uint vSprite;
        layout(location = 0) out vec4 fragColour;
        void main() { fragColour = texture(textures2D[nonuniformEXT(vSprite)], vUv) * vColour; }
        """;

    /// <summary>One decal's instance row (<see cref="Vertex"/>'s inputs).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct DecalInstance
    {
        public const int Size = 48;
        public Vector4 Quad, Colour;
        public Vector2 AxisH;
        public uint Sprite, Pad;
    }

    static readonly VertexLayout Rows = new(
        new VertexInput(0, InternalFormat.Rgba32f, DecalInstance.Size, true),
        new VertexInput(1, InternalFormat.Rgba32f, DecalInstance.Size, true),
        new VertexInput(2, InternalFormat.RG32f, DecalInstance.Size, true),
        new VertexInput(3, InternalFormat.R32ui, DecalInstance.Size, true));

    // The colour alpha blended (red, green, blue), the alpha added: the game's separate blend in one pass.
    static readonly BlendState Blend = new(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
        AlphaSrc: BlendingFactor.One, AlphaDst: BlendingFactor.One);

    readonly IGpuContext gpu;
    readonly NativeFrame frame;
    readonly GpuProgram program;
    GpuPipeline? pipeline;
    readonly SampledImage?[][] sprites;
    // Each sprite's bindless index (uint.MaxValue: none), registered with the samplers of spriteBias (the upscaler's LOD bias, as the images give them).
    readonly uint[][] spriteIndex;
    float spriteBias = float.NaN;
    DecalInstance[] instances = new DecalInstance[256];
    // Each layer's cell, the cell version it shows, the rewrite it was cleared at and how many of the cell's appended decals it holds since (TerrainDecalStore.CellRewrite).
    readonly (uint Key, int Version, int Rewrite, int Appended)[] slots = new (uint, int, int, int)[MaxSlots];
    readonly List<(float Distance, uint Key)> near = [];
    readonly HashSet<uint> drawn = [];
    readonly Vector4[] cells = new Vector4[MaxSlots];
    GpuTexture? target;
    int layers;
    (GpuTexture Texture, uint Index)? bindless;
    int count;

    TerrainDecalRenderer(IGpuContext gpu, SampledImage?[][] sprites)
    {
        this.gpu = gpu;
        this.sprites = sprites;
        frame = new NativeFrame(gpu);
        program = frame.Program(Vertex, Fragment, "terrain decals");
        spriteIndex = [.. sprites.Select(list => Enumerable.Repeat(uint.MaxValue, list.Length).ToArray())];
    }

    /// <summary>The renderer for the decal textures <paramref name="files"/> (per <see cref="TerrainDecalType"/>, its variants in order); a file that cannot be read draws nothing.</summary>
    public static TerrainDecalRenderer Create(IGpuContext gpu, IReadOnlyList<IReadOnlyList<string>> files)
    {
        var sprites = new SampledImage?[4][];
        for (int t = 0; t < 4; t++)
        {
            var list = t < files.Count ? files[t] : [];
            sprites[t] = new SampledImage?[list.Count];
            for (int v = 0; v < list.Count; v++)
            {
                try { sprites[t][v] = SampledImage.FromDds(gpu, DdsReader.ReadFile(list[v]), repeat: false, $"blood {(TerrainDecalType)t} {v}"); }
                catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException) { Console.Error.WriteLine($"blood texture skipped: {list[v]}: {e.Message}"); }
            }
        }
        return new TerrainDecalRenderer(gpu, sprites);
    }

    /// <summary>The game's <c>Decal Resolution</c>: the side of each cell's texture (512, 1024 or 2048); a change draws every cell again.</summary>
    public int Resolution { get; set; } = 1024;

    /// <summary>The game's <c>Decal Range</c>: how far from the eye (X/Z, to the cell's square) cells are drawn.</summary>
    public float Range { get; set; } = 1000;

    /// <summary>
    /// The <c>blood</c> switch: every cell within <see cref="Range"/> is drawn, up to <see cref="MaxSlots"/> (fewer at a large <see cref="Resolution"/>), the array
    /// growing by 8 layers as more are near; off: the game's <see cref="FaithfulSlots"/> nearest, so a battle's blood ends at a cell edge.
    /// </summary>
    public bool Meitou { get; set; } = true;

    /// <summary>Whether <paramref name="key"/>'s cell is on a layer now (its decals then do not expire: <see cref="TerrainDecalStore.Age"/>).</summary>
    public bool Drawn(uint key) => drawn.Contains(key);

    /// <summary>Picks the cells near <paramref name="eye"/> and draws the layers whose cell changed. Before the scene (outside a pass).</summary>
    public void Record(TerrainDecalStore store, Vector3 eye)
    {
        RegisterSprites();
        near.Clear();
        float range = MathF.Max(Range, 0);
        foreach (uint key in store.Cells)
        {
            var (x0, z0) = TerrainDecalCells.Origin(key);
            float dx = MathF.Max(MathF.Max(x0 - eye.X, eye.X - (x0 + TerrainDecalCells.Size)), 0);
            float dz = MathF.Max(MathF.Max(z0 - eye.Z, eye.Z - (z0 + TerrainDecalCells.Size)), 0);
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d <= range) near.Add((d, key));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        int resolution = Math.Clamp(Resolution, 64, 4096);
        int limit = Meitou ? (int)Math.Clamp(LayerBudget / ((long)resolution * resolution * 4) / 8 * 8, FaithfulSlots, MaxSlots) : FaithfulSlots;
        if (near.Count > limit) near.RemoveRange(limit, near.Count - limit);

        // Meitou: the array grows by 8 layers when more cells are near than it holds (every layer drawn again); it does not shrink.
        int need = Math.Max(FaithfulSlots, (near.Count + 7) / 8 * 8);
        if (near.Count > 0 && (target is null || target.Desc.Width != resolution || layers < need))
        {
            if (bindless is { } old) gpu.Bindless.Free(BindlessKind.Texture2DArray, old.Index);
            bindless = null;
            target?.Dispose();
            layers = target is null || target.Desc.Width != resolution ? need : Math.Max(layers, need);
            target = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, resolution, resolution, Layers: layers, Kind: TextureKind.Texture2DArray,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "terrain decals"));
            Array.Clear(slots);
        }

        // A cell keeps its layer while it stays near; the others go to the layers left free.
        drawn.Clear();
        Span<bool> taken = stackalloc bool[MaxSlots];
        Span<int> layerOf = stackalloc int[MaxSlots];
        for (int i = 0; i < near.Count; i++)
        {
            layerOf[i] = -1;
            for (int s = 0; s < layers; s++)
                if (!taken[s] && slots[s].Version != 0 && slots[s].Key == near[i].Key) { layerOf[i] = s; taken[s] = true; break; }
        }
        for (int i = 0; i < near.Count; i++)
        {
            if (layerOf[i] >= 0) continue;
            for (int s = 0; s < layers; s++)
                if (!taken[s]) { layerOf[i] = s; taken[s] = true; slots[s] = (near[i].Key, 0, 0, 0); break; }
        }

        IGpuCommandList? cmd = null;
        count = 0;
        for (int i = 0; i < near.Count; i++)
        {
            uint key = near[i].Key;
            int layer = layerOf[i];
            drawn.Add(key);
            var (column, row) = TerrainDecalCells.Cell(key);
            cells[count++] = new Vector4(column, row, layer, 0);
            int version = store.CellVersion(key);
            if (slots[layer].Version == version) continue;
            cmd ??= gpu.BeginNative("terrain decals");
            // Only decals added since the layer was drawn: those drawn over it (a battle's cells change every few ticks); any gone: the layer cleared and drawn again.
            int rewrite = store.CellRewrite(key);
            var appended = store.Appended(key);
            var slot = slots[layer];
            if (slot.Version != 0 && slot.Rewrite == rewrite && slot.Appended <= appended.Length) DrawLayer(cmd, layer, store, appended[slot.Appended..], clear: false);
            else DrawLayer(cmd, layer, store, store.In(key), clear: true);
            slots[layer] = (key, version, rewrite, appended.Length);
        }
        if (cmd is not null)
        {
            cmd.Order();
            gpu.EndNative(cmd);
        }
        if (count > 0 && target is not null && (bindless is not { } known || known.Texture != target))
            bindless = (target, gpu.Bindless.Register(BindlessKind.Texture2DArray, new SampledTexture(Linear(), target.View())));
    }

    /// <summary>
    /// Clears <paramref name="layer"/> when <paramref name="clear"/> and draws <paramref name="decals"/> into it (<c>FUN_1408dec40</c>): a quad centred on the decal's
    /// place in the cell, its sides the half sizes / 10240 in clip space along the rotation, the texture × the colour (its alpha at most the life). The colour is alpha
    /// blended (red, green, blue only) and the alpha added, the game's separate blend; one instanced draw in the decals' order.
    /// </summary>
    void DrawLayer(IGpuCommandList cmd, int layer, TerrainDecalStore store, ReadOnlySpan<TerrainDecal> decals, bool clear)
    {
        if (!clear && decals.IsEmpty) return;
        if (instances.Length < decals.Length) instances = new DecalInstance[Math.Max(decals.Length, instances.Length * 2)];
        int n = 0;
        foreach (var d in decals)
        {
            var indices = spriteIndex[(int)d.Type];
            if (d.Variant >= indices.Length || indices[d.Variant] == uint.MaxValue) continue;
            float angle = d.Rotation / 1000f, s = MathF.Sin(angle), c = MathF.Cos(angle);
            uint abgr = d.Colour;
            instances[n++] = new DecalInstance
            {
                Quad = new Vector4(d.LocalX * 2 / 1000f - 1, d.LocalZ * 2 / 1000f - 1, d.HalfWidth / 10240f * s, d.HalfWidth / 10240f * c),
                AxisH = new Vector2(d.HalfHeight / 10240f * c, -d.HalfHeight / 10240f * s),
                Colour = new Vector4((abgr & 0xFF) / 255f, (abgr >> 8 & 0xFF) / 255f, (abgr >> 16 & 0xFF) / 255f, MathF.Min((abgr >> 24) / 255f, store.LifeLeft(d))),
                Sprite = indices[d.Variant],
            };
        }
        var t = PassTargets.Of(target, null);
        var colourTarget = new RenderTarget(target!.Attachment(0, layer), clear ? LoadOp.Clear : LoadOp.Load,
            clear ? new ClearColour(0f, 0f, 0f, 0f) : default);
        t = t with { Colour = colourTarget };
        cmd.BeginRendering(t.Rendering);
        if (n > 0)
        {
            var rows = gpu.Frame.AllocateConstants((ulong)(n * DecalInstance.Size), 16);
            MemoryMarshal.AsBytes(instances.AsSpan(0, n)).CopyTo(new Span<byte>(rows.Pointer, n * DecalInstance.Size));
            var state = DrawState.For(t.Formats, gpu.DepthClamp, blend: Blend);
            state.Record(cmd, t);
            cmd.BindPipeline(pipeline ??= gpu.Pipelines.Get(state.Pipeline(program, Rows, PrimitiveType.TriangleStrip, t.Formats, "terrain decals")));
            frame.Bind(cmd, program, default);
            Span<GpuBufferBinding> buffers = [new(rows.Handle, rows.Offset), new(rows.Handle, rows.Offset + 16), new(rows.Handle, rows.Offset + 32), new(rows.Handle, rows.Offset + 40)];
            cmd.BindVertexBuffers(0, buffers);
            cmd.Draw(4, (uint)n);
        }
        cmd.EndRendering();
    }

    /// <summary>The sprites into the bindless table, again with new samplers when the LOD bias they are sampled with changed.</summary>
    void RegisterSprites()
    {
        if (gpu.LodBias == spriteBias) return;
        spriteBias = gpu.LodBias;
        for (int t = 0; t < sprites.Length; t++)
            for (int v = 0; v < sprites[t].Length; v++)
            {
                if (sprites[t][v] is not { } image) continue;
                if (spriteIndex[t][v] == uint.MaxValue) spriteIndex[t][v] = gpu.Bindless.Register(BindlessKind.Texture2D, image.Sampled());
                else gpu.Bindless.Update(BindlessKind.Texture2D, spriteIndex[t][v], image.Sampled());
            }
    }

    /// <summary>The cells for the terrain's colour shader (<c>uBlood</c>, <c>uBloodCount</c>, <c>uBloodCellSize</c>, <c>uBloodCells</c>): none until a cell is drawn.</summary>
    internal void Apply(ref TerrainConstants c)
    {
        if (count == 0 || bindless is not { } b) return;
        c.Blood = b.Index;
        c.BloodCount = count;
        c.BloodCellSize = TerrainDecalCells.Size;
        for (int i = 0; i < count; i++) c.BloodCells[i] = cells[i];
    }

    GpuSampler Linear() => gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
        TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

    public void Dispose()
    {
        DisposeSurfaces();
        if (bindless is { } b) gpu.Bindless.Free(BindlessKind.Texture2DArray, b.Index);
        target?.Dispose();
        foreach (var list in spriteIndex)
            foreach (uint index in list)
                if (index != uint.MaxValue) gpu.Bindless.Free(BindlessKind.Texture2D, index);
        foreach (var list in sprites)
            foreach (var image in list) image?.Dispose();
        gpu.Pipelines.Forget(program);
        gpu.Shaders.Dispose(program);
        frame.Dispose();
    }
}
