using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Shadows;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The terrain's shadow over the whole world for the Meitou shadows (beyond the cascades' range): for the sun's direction, per sample of the world
/// height grid (2049², 144 units apart) the height of the top of the shadow the land towards the sun casts there, and the distance to the land that
/// casts it. A point below that top is in shadow; the distance widens the penumbra. Built on the GPU by a doubling sweep (each pass looks twice as
/// far towards the sun: 12 passes cover the world) into two RG32F targets, again only when the sun has moved. The receiver reads it with one fetch
/// (<see cref="MeitouShadowShaders.Functions"/>, <c>msTerrain</c>).
/// <para>
/// Phase 8 (docs/renderer-native.md 8): the heights and the targets are native textures and the sweep renders into them in one native segment of its
/// own; the consumer (<c>ShadowPass.Meitou</c>) takes the finished map as <see cref="Sampled"/> (linear, clamped, as the GL texture was).
/// </para>
/// </summary>
sealed unsafe class TerrainShadowMap : IDisposable
{
    /// <summary>
    /// Samples along the map's side (the coarse grid's).
    /// </summary>
    readonly int _size;

    /// <summary>
    /// World units between samples.
    /// </summary>
    readonly float _spacing;

    /// <summary>
    /// The coarse heights (R16).
    /// </summary>
    readonly GpuTexture _heights;

    /// <summary>
    /// The heights with the linear, clamped sampler (the first pass's source).
    /// </summary>
    readonly SampledTexture _heightsSampled;

    /// <summary>
    /// The doubling sweep program.
    /// </summary>
    readonly LegacyProgram _sweep;

    /// <summary>
    /// The sweep's source texture slot.
    /// </summary>
    readonly SamplerSlot _sourceSlot;

    /// <summary>
    /// The sweep's uniforms: the map's side, the first pass, the step towards the sun (samples), the shadow top's drop over it and its
    /// distance (units).
    /// </summary>
    readonly UniformHandle _uSize, _uFirst, _uStep, _uDrop, _uDistance;

    /// <summary>
    /// The two RG32F targets the passes ping-pong between.
    /// </summary>
    readonly GpuTexture[] _targets = new GpuTexture[2];

    /// <summary>
    /// The targets with the linear, clamped sampler.
    /// </summary>
    readonly SampledTexture[] _targetsSampled = new SampledTexture[2];

    /// <summary>
    /// The targets' attachment formats (colour only).
    /// </summary>
    readonly AttachmentFormats _format;

    /// <summary>
    /// The sweep's draw state: no culling, depth or blending, all channels.
    /// </summary>
    readonly DrawState _state;

    /// <summary>
    /// The sweep's pipeline (made on the first build).
    /// </summary>
    GpuPipeline? _pipeline;

    /// <summary>
    /// The sun direction the map was built for.
    /// </summary>
    Vector3 _builtFor;

    /// <summary>
    /// The target holding the finished map (-1: none).
    /// </summary>
    int _result = -1;

    /// <summary>
    /// Rebuilt when the sun's direction has turned by more than this (radians; about 0.1°).
    /// </summary>
    public const float RebuildAngle = 0.0017f;

    /// <param name="coarse">The whole-world raw heights, <paramref name="size"/>² samples (WorldScene.Coarse).</param>
    public TerrainShadowMap(IGpuContext gpu, ushort[] coarse, int size)
    {
        Gpu = gpu;
        this._size = size;
        _spacing = WorldLayout.WorldSize / (float)(size - 1);

        // GL's R16 and RG32F, one level each (VkGl sampled only level 0 of them: linear without mips), linear and clamped.
        var linear = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0));
        using (var batch = gpu.Uploads.Begin())
        {
            _heights = batch.Create(new TextureDesc(InternalFormat.R16, size, size, Name: "terrain shadow map heights"));
            batch.Write(_heights, 0, 0, new PixelRect(0, 0, (uint)size, (uint)size),
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(coarse.AsSpan(0, size * size)));
            for (int i = 0; i < 2; i++)
            {
                _targets[i] = batch.Create(new TextureDesc(InternalFormat.Rg32F, size, size,
                    Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "terrain shadow map"));
            }
        }

        _heightsSampled = new SampledTexture(linear, _heights.View());
        for (int i = 0; i < 2; i++) _targetsSampled[i] = new SampledTexture(linear, _targets[i].View());

        // Native (docs/renderer-native.md 7.1, wave 3 agent B, step P): VkGl's SPIR-V and layout, made once, never inside a draw.
        _sweep = LegacyProgram.Create(gpu, ShadowShaders.FullscreenVertex, MeitouShadowShaders.SweepFragment, "terrain shadow sweep");
        (_sourceSlot, _uSize, _uFirst, _uStep, _uDrop, _uDistance) = (_sweep.Sampler("uSource"), _sweep.Uniform("uSize"), _sweep.Uniform("uFirst"),
            _sweep.Uniform("uStep"), _sweep.Uniform("uDrop"), _sweep.Uniform("uDistance"));
        _format = new AttachmentFormats(_targets[0].Desc.Format, InternalFormat.None);

        // What VkGl made of the GL state the sweep set (no culling, depth or blending; all channels; filled).
        _state = new DrawState(CullMode.None, FrontFaceDirection.Ccw, false, false, DepthFunction.Lequal, false, 0, 0, BlendState.Off,
            ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A, PolygonKind.Fill, false, false);
    }

    /// <summary>
    /// The native GPU API the map is made and drawn with.
    /// </summary>
    public IGpuContext Gpu { get; }

    /// <summary>
    /// The finished map with its sampler (linear, clamped); null before the first build or when the sun is too high for any terrain shadow.
    /// </summary>
    public SampledTexture? Sampled => _result < 0 ? null : _targetsSampled[_result];

    /// <summary>
    /// Samples along the map's side.
    /// </summary>
    public int Size => _size;

    /// <summary>
    /// World units between samples.
    /// </summary>
    public float Spacing => _spacing;

    /// <summary>
    /// GPU work of the last rebuild is not timed separately; this counts rebuilds (for the log).
    /// </summary>
    public int Builds { get; private set; }

    /// <summary>
    /// Render-thread milliseconds the last rebuild took to record.
    /// </summary>
    public double LastBuildCpuMs { get; private set; }

    /// <summary>
    /// Whether the sun is low enough to leave the land's shadows anywhere (a sun straight up casts none).
    /// </summary>
    public static bool Applies(Vector3 toSun) => toSun.X * toSun.X + toSun.Z * toSun.Z > 1e-6f;

    /// <summary>
    /// Rebuilds the map when the sun has moved since the last build: one native segment of its own (outside any pass), each doubling pass its own
    /// rendering into one target reading the other, a full barrier between passes. Leaves GL's state as it was.
    /// </summary>
    public bool Update(Vector3 toSun)
    {
        toSun = Vector3.Normalize(toSun);
        if (!Applies(toSun))
        {
            _result = -1;
            return false;
        }

        if (_result >= 0 && Vector3.Dot(toSun, _builtFor) > MathF.Cos(RebuildAngle)) return false;

        var watch = Stopwatch.StartNew();
        float horizontal = MathF.Sqrt(toSun.X * toSun.X + toSun.Z * toSun.Z);
        var direction = new Vector2(toSun.X, toSun.Z) / horizontal;   // samples (x, z) towards the sun
        float tangent = toSun.Y / horizontal;

        int passes = 1;
        while ((1 << (passes - 1)) < _size) passes++;

        int write = 0;
        _pipeline ??= Gpu.Pipelines.Get(_state.Pipeline(_sweep.Program, _sweep.VertexLayout([]), PrimitiveType.Triangles, _format,
            "terrain shadow sweep"));
        var viewport = new PixelViewport(0, 0, _size, _size, 0, 1);
        var scissor = new PixelRect(0, 0, (uint)_size, (uint)_size);
        _sweep.Set(_uSize, (float)_size);
        var cmd = Gpu.BeginNative("terrain shadow sweep");
        for (int pass = 0; pass < passes; pass++)
        {
            float step = pass == 0 ? 1 : 1 << (pass - 1);   // the first pass reaches one sample, the k-th 2^(k-1) more
            _sweep.Set(_uFirst, pass == 0 ? 1 : 0);
            _sweep.Set(_uStep, direction.X * step, direction.Y * step);
            _sweep.Set(_uDrop, step * _spacing * tangent);
            _sweep.Set(_uDistance, step * _spacing);
            _sweep.Bind(_sourceSlot, pass == 0 ? _heightsSampled : _targetsSampled[1 - write]);
            var target = _targets[write];
            cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), LoadOp.Load, default), default, _size, _size));
            cmd.SetViewport(viewport);
            cmd.SetScissor(scissor);
            cmd.SetRaster(_state.Cull, _state.Front);
            cmd.SetDepth(_state.DepthTest, _state.DepthWrite, _state.Compare);
            cmd.SetDepthBias(_state.BiasEnable, _state.BiasConstant, _state.BiasSlope);
            cmd.BindPipeline(_pipeline!.Value);
            _sweep.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            cmd.Order();   // the next pass reads what this one wrote (VkGl: a full barrier before each pass)
            write = 1 - write;
        }

        Gpu.EndNative(cmd);
        _result = 1 - write;
        _builtFor = toSun;
        Builds++;
        LastBuildCpuMs = watch.Elapsed.TotalMilliseconds;
        return true;
    }

    /// <summary>
    /// Frees the targets, the heights and the program.
    /// </summary>
    public void Dispose()
    {
        for (int i = 0; i < 2; i++) _targets[i].Dispose();
        _heights.Dispose();
        _sweep.Dispose();
    }
}
