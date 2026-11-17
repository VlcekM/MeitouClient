using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The characters' visibility buffer in the main view (docs/character-renderer.md "Visibility buffer"): the colour pass split in two so each pixel a
/// character shows is shaded once. The id pass draws the characters as the colour pass does (its vertex program, its cut-outs, the scene's depth),
/// writing per pixel the instance row, the triangle, the shading rate and facing, and the hardware's barycentrics with their derivatives; the resolve
/// (<see cref="CharacterVisibilityShaders"/>) then runs the colour pass's shading once per pixel over the picture, into the scene's colour targets.
/// Render thread only.
/// </summary>
sealed unsafe class CharacterVisibility : IDisposable
{
    #region Programs and targets

    /// <summary>
    /// The device: device addresses of the meshes, the programs and pipelines.
    /// </summary>
    readonly GpuContext _gpu;

    /// <summary>
    /// The resolve's frame set: the colour pass's four storage buffers, then the instance rows and the rows' meshes
    /// (<see cref="CharacterVisibilityShaders.RowsBinding"/>).
    /// </summary>
    readonly NativeFrame _frame;

    /// <summary>
    /// The colour pass's frame set, which the id programs are made with.
    /// </summary>
    readonly NativeFrame _colourFrame;

    /// <summary>
    /// The id pass's programs, without and with a shading rate attachment (made when first drawn).
    /// </summary>
    ReflectedProgram? _ids, _ratedIds;

    /// <summary>
    /// The resolve's program.
    /// </summary>
    readonly GpuProgram _resolve;

    /// <summary>
    /// The resolve's pipelines by the scene's attachment formats.
    /// </summary>
    readonly Dictionary<AttachmentFormats, GpuPipeline> _pipelines = [];

    /// <summary>
    /// The id pass's own rendering, over the id targets and the scene's depth.
    /// </summary>
    readonly SceneHost _host;

    /// <summary>
    /// The id target (RGBA32UI: the row + 1, the primitive, rate and facing, two barycentrics' bits), the slopes target (RGBA32F: the two
    /// barycentrics' fine derivatives along x, then along y: what <c>dFdx</c> gives) and the coarse one (the quad's, as the implicit lookups take
    /// them), at the render size.
    /// </summary>
    GpuTexture? _idTarget, _slopeTarget, _coarseTarget;

    /// <summary>
    /// Their bindless indices.
    /// </summary>
    uint _idIndex, _slopeIndex, _coarseIndex;

    /// <summary>
    /// Whether the device has what the visibility buffer needs: Vulkan with the meshes read by address, fragment barycentrics, <c>gl_PrimitiveID</c>
    /// in a fragment program, and no late-depth workaround for discards.
    /// </summary>
    public static bool Supported(IGpuContext gpu) =>
        gpu is GpuContext { HasRayQuery: true, Device: { HasFragmentBarycentric: true, HasGeometryShader: true } }
        && !gpu.Shaders.DiscardNeedsLateDepth;

    /// <summary>
    /// Compiles the resolve (the id programs follow when first drawn).
    /// </summary>
    public CharacterVisibility(GpuContext gpu, NativeFrame colourFrame)
    {
        _gpu = gpu;
        _colourFrame = colourFrame;
        _frame = new NativeFrame(gpu, extraStorage: 6);
        string fragment = Gi.GiResolveShaders.WithAlbedo(CharacterVisibilityShaders.ResolveFragment(), inShader: true);
        _resolve = _frame.Program(CharacterVisibilityShaders.ResolveVertex(), fragment, "characters resolve");
        _host = new SceneHost(gpu);
    }

    /// <summary>
    /// The id pass's program for a rendering with (<paramref name="rated"/>) or without a shading rate attachment.
    /// </summary>
    public ReflectedProgram Ids(bool rated)
    {
        ref var p = ref rated ? ref _ratedIds : ref _ids;
        return p ??= new ReflectedProgram(_gpu, _colourFrame, CharacterShaders.IdVertex(), CharacterVisibilityShaders.IdFragment(rated),
            rated ? "characters ids rated" : "characters ids", CharacterShaders.InstanceLocation);
    }

    /// <summary>
    /// Opens the id pass over this frame's id targets and <paramref name="post"/>'s depth, with the scene's shading rate (its coarse fragments cut
    /// and discard as the colour pass's would), the targets cleared.
    /// </summary>
    public void Open(IWorldPostChain post, int stage)
    {
        var scene = post.SceneTargets;
        if (_idTarget is null || _idTarget.Desc.Width != scene.Width || _idTarget.Desc.Height != scene.Height) MakeTargets(scene.Width, scene.Height);

        var targets = post.WithShadingRate(PassTargets.Of(_idTarget, post.SceneDepth, _slopeTarget, _coarseTarget));
        _host.Open(stage, targets, new ClearColour(0, 0, 0, 0));
    }

    /// <summary>
    /// Ends the id pass (its draws run).
    /// </summary>
    public void Close() => _host.Close();

    /// <summary>
    /// The id and slope targets at <paramref name="width"/> x <paramref name="height"/>, the old ones released after the frames in flight.
    /// </summary>
    void MakeTargets(int width, int height)
    {
        FreeTargets();
        using var batch = _gpu.Uploads.Begin();
        var use = TextureUse.Sampled | TextureUse.ColourTarget;
        _idTarget = batch.Create(new TextureDesc(InternalFormat.Rgba32Ui, width, height, Use: use, Name: "characters visibility ids"));
        _slopeTarget = batch.Create(new TextureDesc(InternalFormat.Rgba32F, width, height, Use: use, Name: "characters visibility slopes"));
        _coarseTarget = batch.Create(new TextureDesc(InternalFormat.Rgba32F, width, height, Use: use, Name: "characters visibility coarse slopes"));
        _idIndex = _gpu.Bindless.Register(BindlessKind.UintTexture2D, Sampled(_idTarget));
        _slopeIndex = _gpu.Bindless.Register(BindlessKind.Texture2D, Sampled(_slopeTarget));
        _coarseIndex = _gpu.Bindless.Register(BindlessKind.Texture2D, Sampled(_coarseTarget));
    }

    /// <summary>
    /// A target read texel by texel.
    /// </summary>
    SampledTexture Sampled(GpuTexture t) =>
        new(_gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)), t.View());

    /// <summary>
    /// Releases the targets and their bindless entries.
    /// </summary>
    void FreeTargets()
    {
        if (_idTarget is null) return;

        _gpu.Bindless.Free(BindlessKind.UintTexture2D, _idIndex);
        _gpu.Bindless.Free(BindlessKind.Texture2D, _slopeIndex);
        _gpu.Bindless.Free(BindlessKind.Texture2D, _coarseIndex);
        _idTarget.Dispose();
        _slopeTarget!.Dispose();
        _coarseTarget!.Dispose();
        (_idTarget, _slopeTarget, _coarseTarget) = (null, null, null);
    }

    /// <summary>
    /// The programs and targets go.
    /// </summary>
    public void Dispose()
    {
        FreeTargets();
        _ids?.Dispose();
        _ratedIds?.Dispose();
        _gpu.Shaders.Dispose(_resolve);
        _frame.Dispose();
    }

    #endregion

    #region Resolve

    /// <summary>
    /// The resolve's push block: the colour pass's, then the targets' bindless indices.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ResolvePush
    {
        /// <summary>
        /// The colour pass's push block.
        /// </summary>
        public CharacterPush Colour;

        /// <summary>
        /// The id, slope and coarse slope targets' bindless indices.
        /// </summary>
        public uint Ids, Slopes, Coarse;
    }

    /// <summary>
    /// The id pass's view, as the colour pass would have drawn with it.
    /// </summary>
    ViewConstants _view;

    /// <summary>
    /// The resolve's storage buffers: the colour pass's four, the instance rows and the rows' meshes.
    /// </summary>
    readonly GpuBufferBinding[] _storage = new GpuBufferBinding[6];

    /// <summary>
    /// The resolve's push block.
    /// </summary>
    ResolvePush _push;

    /// <summary>
    /// An id pass was drawn this frame and waits for <see cref="Resolve"/>.
    /// </summary>
    bool _pending;

    /// <summary>
    /// What the id pass just recorded, for <see cref="Resolve"/>: its <paramref name="view"/> and push block, the colour pass's four storage buffers
    /// (<paramref name="colour"/>), the view's instance rows and its batches, whose meshes each row is given.
    /// </summary>
    public void Keep(in ViewConstants view, in CharacterPush push, ReadOnlySpan<GpuBufferBinding> colour, Transient rows,
        List<CharacterRenderer.Batch> batches)
    {
        int count = 0;
        foreach (var b in batches) count = Math.Max(count, b.Offset + b.Count);

        colour.CopyTo(_storage);
        _storage[4] = new GpuBufferBinding(rows.Handle, rows.Offset, (ulong)Math.Max(count, 1) * CharInstance.Size);
        _storage[5] = Meshes(batches, count);
        _view = view;
        _push = new ResolvePush { Colour = push, Ids = _idIndex, Slopes = _slopeIndex, Coarse = _coarseIndex };
        _pending = count > 0;
    }

    /// <summary>
    /// Each row's mesh in this frame's constants: the part's vertices and the level's first index, by device address.
    /// </summary>
    GpuBufferBinding Meshes(List<CharacterRenderer.Batch> batches, int count)
    {
        var table = _gpu.Frame.AllocateConstants((ulong)Math.Max(count, 1) * 16, 16);
        var meshes = (ulong*)table.Pointer;
        var device = _gpu.Device;
        foreach (var b in batches)
        {
            ulong vertices = device.Address(b.Part.Vertices.Handle);
            ulong indices = device.Address(b.Part.Indices.Handle) + (ulong)b.Part.Offset[b.Level] * 4;
            for (int r = b.Offset; r < b.Offset + b.Count; r++)
            {
                meshes[2 * r] = vertices;
                meshes[2 * r + 1] = indices;
            }
        }

        return table.Binding;
    }

    /// <summary>
    /// Shades the pixels the id pass kept: one triangle over <paramref name="scene"/>'s colour targets (no depth), outside any pass. Nothing when no
    /// id pass waits.
    /// </summary>
    public void Resolve(PassTargets scene)
    {
        if (!_pending) return;

        _pending = false;
        var targets = scene with { Depth = default, Formats = scene.Formats with { Depth = InternalFormat.None } };
        var state = DrawState.SceneDepth(targets.Formats, DrawState.Rgba) with { DepthTest = false, DepthWrite = false, Cull = CullMode.None };
        if (!_pipelines.TryGetValue(targets.Formats, out var pipeline))
        {
            var desc = state.Pipeline(_resolve, new VertexLayout(), PrimitiveType.Triangles, targets.Formats, "characters resolve");
            _pipelines[targets.Formats] = pipeline = _gpu.Pipelines.Get(desc);
        }

        var binding = _frame.Prepare(in _view, _storage);
        var cmd = _gpu.BeginNative("characters resolve");
        cmd.Order();   // the id pass wrote the targets
        cmd.BeginRendering(targets.Rendering);
        state.Record(cmd, targets);
        cmd.BindPipeline(pipeline);
        NativeFrame.Record(cmd, _resolve, in binding);
        var push = _push;
        cmd.PushConstants(_resolve, GpuStage.Vertex | GpuStage.Fragment, in push);
        cmd.SetViewport(new PixelViewport(0, 0, targets.Width, targets.Height));
        cmd.SetScissor(new PixelRect(0, 0, (uint)targets.Width, (uint)targets.Height));
        cmd.Draw(3, 1);
        cmd.EndRendering();
        cmd.Order();
        _gpu.EndNative(cmd);
    }

    #endregion
}
