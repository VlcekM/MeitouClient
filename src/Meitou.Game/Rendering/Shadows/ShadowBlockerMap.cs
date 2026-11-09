using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Shadows;

/// <summary>
/// The Meitou shadows' blocker map (contact-hardening penumbrae): per cascade, at half its resolution, the blockers' depth the receivers' search
/// reads, rebuilt for the cascades drawn in a frame.
/// </summary>
sealed class ShadowBlockerMap : IDisposable
{
    /// <summary>
    /// The context the map and its program are made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The map (one layer per cascade), its nearest-filtered sampling, and whether the receivers read it yet.
    /// </summary>
    GpuTexture? _blocker;

    /// <summary>
    /// The map with its nearest, edge-clamped sampler.
    /// </summary>
    SampledTexture _blockerSampled;

    /// <summary>
    /// The receivers read the map (from the frame it was first filled).
    /// </summary>
    bool _blockerPublished;

    /// <summary>
    /// The blocker search program and its handles, made at the first update.
    /// </summary>
    LegacyProgram? _blockerNative;

    /// <summary>
    /// The program's cascade-array sampler.
    /// </summary>
    SamplerSlot _blockerAtlasSlot;

    /// <summary>
    /// The program's <c>uAtlasSize</c> and <c>uLayer</c> uniforms.
    /// </summary>
    UniformHandle _blockerSizeHandle, _blockerLayerHandle;

    /// <summary>
    /// The blocker pass's state, as the GL version's was: no culling, depth or blending, every colour channel written.
    /// </summary>
    static readonly DrawState BlockerState = new(CullMode.None, FrontFaceDirection.Ccw, false, false, DepthFunction.Lequal, false, 0, 0,
        BlendState.Off, ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A,
        PolygonKind.Fill, false, false);

    /// <summary>
    /// A blocker map on <paramref name="gpu"/>, made at its first update.
    /// </summary>
    public ShadowBlockerMap(IGpuContext gpu) => _gpu = gpu;

    /// <summary>
    /// The half-resolution blocker map (native; null until the first frame with contact hardening).
    /// </summary>
    public GpuTexture? Map => _blocker;

    /// <summary>
    /// What the receivers sample (<c>uShadowBlocker</c>): the map once published, else the stand-in.
    /// </summary>
    public SampledTexture Published => _blockerPublished ? _blockerSampled : default;

    /// <summary>
    /// The receivers read the map from now on (the Meitou block that says so is published).
    /// </summary>
    public void MarkPublished() => _blockerPublished = true;

    /// <summary>
    /// Rebuilds the blocker map's layers of the cascades drawn this frame: one native segment with a rendering per layer (the layers not drawn keep
    /// what they hold), the cascade array (<paramref name="atlas"/>, layers of <paramref name="atlasSize"/>) read raw through a plain sampler (no
    /// comparison), the fullscreen triangle.
    /// </summary>
    public bool Update(ReadOnlySpan<bool> drawn, int count, int atlasSize, SampledTexture atlas)
    {
        int size = atlasSize / 2;
        bool fresh = false;   // a new map: every layer
        if (_blocker is null || _blocker.Desc.Width != size || _blocker.Desc.Layers != count)
        {
            _blocker?.Dispose();
            _blocker = _gpu.CreateTexture(new TextureDesc(InternalFormat.R32F, size, size, Layers: count, Kind: TextureKind.Texture2DArray,
                Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "shadow blocker map"));
            _blockerSampled = ShadowPass.Sampled(_gpu, _blocker, TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge);
            _blockerPublished = false;
            fresh = true;
        }

        if (_blockerNative is null)
        {
            // Native (docs/renderer-native.md 7.1, wave 3 agent B, step P): VkGl's SPIR-V and layout, made here once, never inside a draw.
            var p = _blockerNative = LegacyProgram.Create(_gpu, ShadowShaders.FullscreenVertex, MeitouShadowShaders.BlockerFragment, "shadow blockers");
            (_blockerAtlasSlot, _blockerSizeHandle, _blockerLayerHandle) = (p.Sampler("uAtlas"), p.Uniform("uAtlasSize"), p.Uniform("uLayer"));
        }

        RecordBlockers(drawn, count, fresh, atlasSize, atlas);
        return true;
    }

    /// <summary>
    /// Records the layers of the cascades drawn this frame (all of them in a <paramref name="fresh"/> map).
    /// </summary>
    void RecordBlockers(ReadOnlySpan<bool> drawn, int count, bool fresh, int atlasSize, SampledTexture atlas)
    {
        var p = _blockerNative!;
        var map = _blocker!;
        p.Set(_blockerSizeHandle, (float)atlasSize);
        var cmd = _gpu.BeginNative("shadow blockers");
        var formats = new AttachmentFormats(map.Desc.Format, InternalFormat.None, 1);
        int side = map.Desc.Width;
        var whole = new PixelRect(0, 0, (uint)side, (uint)side);
        var pipeline = _gpu.Pipelines.Get(BlockerState.Pipeline(p.Program, p.VertexLayout([]), PrimitiveType.Triangles, formats, "shadow blockers"));
        for (int i = 0; i < count; i++)
        {
            if (!drawn[i] && !fresh) continue;

            var target = new RenderTarget(map.Attachment(0, i), LoadOp.Load, default);
            cmd.BeginRendering(new RenderingDesc(target, default, side, side));
            BlockerState.Record(cmd, new PassTargets(target, default, formats, side, side, new PixelViewport(0, 0, side, side, 0, 1), whole));
            cmd.BindPipeline(pipeline);

            // Each layer is a new encoder: bind the atlas for it, not only once before the loop.
            p.Bind(_blockerAtlasSlot, atlas);
            p.Set(_blockerLayerHandle, i);
            p.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
        }

        _gpu.EndNative(cmd);
    }

    /// <summary>
    /// Frees the map and its program.
    /// </summary>
    public void Dispose()
    {
        _blocker?.Dispose();
        _blockerNative?.Dispose();
    }
}
