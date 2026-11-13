using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The character editor's view of the character being edited (<see cref="CharacterRenderer.StageRequest"/>): the character by its draw-list key, the
/// size of the picture and the camera as the editor's orbit sets it. <paramref name="Yaw"/> turns the camera round the character (0: in front of it)
/// and <paramref name="Elevation"/> lifts it, both in radians; <paramref name="Distance"/> is from the pivot, which is 12 units above the feet or,
/// with <paramref name="Head"/>, the head; <paramref name="ShiftX"/> moves the pivot sideways on the picture (normalised device units, negative:
/// left).
/// </summary>
public readonly record struct EditorStageRequest(long Key, int Width, int Height, float Yaw, float Elevation, float Distance, bool Head,
    float FieldOfView, float ShiftX);

/// <summary>
/// The character editor's stage (docs/characters.md "Character editor"): the original draws the edited character into a workspace of its own and puts
/// the plain ground texture behind it; here the character is drawn alone into a window-sized picture, transparent round it, which the interface shows
/// over the ground texture.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>
    /// The character to draw into <see cref="StageTexture"/> this frame (the host sets it before <see cref="Update"/>, which also poses the character
    /// out of range); null: none.
    /// </summary>
    public EditorStageRequest? StageRequest { get; set; }

    /// <summary>
    /// The stage picture (RGBA8 at the request's size) and its depth, remade when the size changes.
    /// </summary>
    GpuTexture? _stageColour, _stageDepth;
    /// <summary>
    /// <see cref="_stageColour"/> with a clamped bilinear sampler.
    /// </summary>
    SampledTexture? _stageSampled;

    /// <summary>
    /// The enhanced stage (<see cref="Look"/>): the half-float picture the character and the platform are drawn into, and its depth, resolved into
    /// <see cref="_stageColour"/>.
    /// </summary>
    GpuTexture? _stageHdr, _stageHdrDepth;
    /// <summary>
    /// <see cref="_stageHdr"/> sampled for the resolve.
    /// </summary>
    SampledTexture _stageHdrSampled;
    /// <summary>
    /// The enhanced stage's supersampling each way (0: none made).
    /// </summary>
    int _stageHdrScale;

    /// <summary>
    /// Backing field of <see cref="Look"/>.
    /// </summary>
    PreviewLook _look;
    /// <summary>
    /// The resolve of the enhanced stage, portraits and icons, made on first use.
    /// </summary>
    PreviewResolve? _preview;

    /// <summary>
    /// How the stage and the portraits are finished (<see cref="PreviewLook"/>); the host sets it every frame from the world's options. A change
    /// redraws every portrait (the stage is drawn every frame).
    /// </summary>
    public PreviewLook Look
    {
        get => _look;
        set
        {
            if (_look == value) return;
            _look = value;
            _portraitShown.Clear();
        }
    }

    /// <summary>
    /// The stage picture (8-bit UNORM holding the lit values as they are, as the original's; transparent where nothing is drawn; upside down as the
    /// renderer's targets are), made by <see cref="DrawStage"/>; null before the first.
    /// </summary>
    public (GpuTexture Texture, SampledTexture Sampled)? StageTexture => _stageColour is null ? null : (_stageColour, _stageSampled!.Value);

    /// <summary>
    /// Where the platform is drawn after the character (<c>WorldObjectRenderer.DrawStageProp</c>), before <see cref="ResolveStage"/>: the stage
    /// picture and its depth, or the enhanced half-float ones, and the fog distance that selects the stage light (-2 the original's, -3 unclamped);
    /// null before the first.
    /// </summary>
    public (GpuTexture Colour, GpuTexture Depth, float FogDistance)? StageDrawTarget =>
        _stageColour is null ? null : _look.Enhanced && _stageHdr is not null ? (_stageHdr, _stageHdrDepth!, -3f) : (_stageColour, _stageDepth!, -2f);

    /// <summary>
    /// The last stage drawn: its camera, the light (towards it, in the world) and where the character stands (its position and facing, no scale),
    /// which the platform's stage frame is turned half round from; null when no character was drawn.
    /// </summary>
    public (Matrix4x4 ViewProjection, Vector3 Eye, Vector3 Light, Matrix4x4 Feet)? StageView { get; private set; }

    /// <summary>
    /// The last stage's studio view (Meitou, previewsun), which the platform is lit by (<c>WorldObjectRenderer.DrawStageProp</c>); null without the
    /// studio.
    /// </summary>
    internal ViewConstants? StageStudio { get; private set; }

    /// <summary>
    /// Draws <see cref="StageRequest"/>'s character with its live pose (after <see cref="Update"/>, outside any pass), lit as the original's stage
    /// (<c>rtt.hlsl</c>, the shader's stage branch, a fog distance of -2): the light comes from the stage's (-3, 2, -3), the stage turned with the
    /// character, who stands on it turned half round (<c>FUN_1405f03c0</c>).
    /// </summary>
    public void DrawStage()
    {
        if (StageRequest is not { } r || r.Width <= 0 || r.Height <= 0) return;

        (StageView, StageStudio) = (null, null);
        bool hdr = _look.Enhanced;
        EnsureStageTargets(in r, hdr);
        int index = StageLiveIndex(r.Key);

        var target = hdr ? PassTargets.Of(_stageHdr, _stageHdrDepth) : PassTargets.Of(_stageColour, _stageDepth);
        ViewConstants? constants = null;
        Vector3 eye = default;
        if (index >= 0) constants = StageViewConstants(index, in r, hdr, in target, out eye);

        DrawStagePass(in target, constants, index, eye);
    }

    /// <summary>
    /// The stage pictures at the request's size: the 8-bit one, and with <paramref name="hdr"/> the half-float one at the stage's supersampling.
    /// </summary>
    void EnsureStageTargets(in EditorStageRequest r, bool hdr)
    {
        if (_stageColour is null || _stageColour.Desc.Width != r.Width || _stageColour.Desc.Height != r.Height)
            MakeStage(r.Width, r.Height);

        int scale = hdr ? StageScale(r.Width, r.Height) : 1;
        if (hdr && (_stageHdr is null || _stageHdr.Desc.Width != r.Width * scale || _stageHdr.Desc.Height != r.Height * scale))
        {
            DisposeStageHdr();
            (_stageHdr, _stageHdrDepth, _stageHdrSampled) = PreviewResolve.Targets(Gpu, r.Width, r.Height, _look with { Supersample = scale },
                "character stage hdr");
            _stageHdrScale = scale;
        }
    }

    /// <summary>
    /// The live index of the character with draw key <paramref name="key"/>; -1 when it is not live.
    /// </summary>
    int StageLiveIndex(long key)
    {
        int index = -1;
        for (int i = 0; i < _liveCount; i++)
        {
            if (_live[i].Key == key)
            {
                index = i;
                break;
            }
        }

        return index;
    }

    /// <summary>
    /// The stage's camera and light for live character <paramref name="index"/> (<see cref="StageView"/>), and the view constants of its draw: the
    /// studio's, its shadow and occlusion drawn first (<see cref="StageStudio"/>), or the original stage's.
    /// </summary>
    ViewConstants StageViewConstants(int index, in EditorStageRequest r, bool hdr, in PassTargets target, out Vector3 eye)
    {
        ref var l = ref _live[index];
        (eye, var pivot) = StageCamera(in l, in r);
        var view = Matrix4x4.CreateLookAt(eye, pivot, Vector3.UnitY);
        var perspective = Matrix4x4.CreatePerspectiveFieldOfView(r.FieldOfView, r.Width / (float)r.Height, 0.5f, 400);
        var projection = perspective * Matrix4x4.CreateTranslation(r.ShiftX, 0, 0);
        var light = Vector3.Normalize(Vector3.TransformNormal(StageLight, l.World));

        // The studio (previewsun): the stage's light raised to a sun, its shadow and occlusion drawn first.
        // The studio's sun, fixed to the character (the editor's camera turns round it, the shadows stay): as seen from the editor's first view
        // (yaw 0, in front: the character's +z), from the right, about 55 degrees up (a brim shades the face under it).
        if (StudioOn) light = Vector3.Normalize(Vector3.TransformNormal(StudioStageSun, l.World));

        Matrix4x4.Decompose(l.World, out _, out var facing, out _);
        StageView = (view * projection, eye, light, Matrix4x4.CreateFromQuaternion(facing) * Matrix4x4.CreateTranslation(Ground(in l)));
        StageStudio = null;

        ViewConstants constants;
        _studioCasters = StageCasters;
        try
        {
            constants = StudioOn
                ? StudioView(index, view * projection, perspective, eye, light, _stageHdr!, _stageHdrDepth!, target.Width, target.Height)
                : new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = light, FogDistance = hdr ? -3 : -2 };
        }
        finally
        {
            _studioCasters = null;
        }

        if (StudioOn) StageStudio = constants;
        return constants;
    }

    /// <summary>
    /// The stage pass into <paramref name="target"/>, cleared, with the character drawn through <paramref name="constants"/> (none: left empty).
    /// </summary>
    void DrawStagePass(in PassTargets target, ViewConstants? constants, int index, Vector3 eye)
    {
        var cmd = Gpu.BeginNative("character stage");
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        var rect = new PixelRect(0, 0, (uint)target.Width, (uint)target.Height);
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(new PixelViewport(0, 0, target.Width, target.Height, 0, 1), rect);

        if (constants is { } c)
        {
            _portraitLive = index;
            try
            {
                DrawView(c, eye, []);
            }
            finally
            {
                _portraitLive = -1;
            }
        }

        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// The enhanced stage's picture into the stage picture (after the platform is drawn into <see cref="StageDrawTarget"/>); nothing for the
    /// original's.
    /// </summary>
    public void ResolveStage()
    {
        if (!_look.Enhanced || _stageHdr is null || _stageColour is null || StageRequest is null) return;
        _preview ??= new PreviewResolve(Gpu);
        _preview.Run(_stageHdrSampled, _stageColour, _look with { Supersample = _stageHdrScale }, [new PixelRect(0, 0, (uint)_stageColour.Desc.Width,
            (uint)_stageColour.Desc.Height)]);
    }

    /// <summary>
    /// The enhanced stage's supersampling: the look's, down to 1 where the picture would pass 16.6 million pixels (twice 4K's; a window over 2880 x
    /// 1440 or so).
    /// </summary>
    int StageScale(int width, int height)
    {
        int s = Math.Max(_look.Supersample, 1);
        while (s > 1 && (long)width * height * s * s > 16_600_000) s--;
        return s;
    }

    /// <summary>
    /// The editor has closed: its half-float stage goes (window-sized and supersampled, the largest of the previews).
    /// </summary>
    void ReleaseStage() => DisposeStageHdr();

    /// <summary>
    /// Frees the enhanced stage picture and its depth.
    /// </summary>
    void DisposeStageHdr()
    {
        _stageHdr?.Dispose();
        _stageHdrDepth?.Dispose();
        (_stageHdr, _stageHdrDepth, _stageHdrScale) = (null, null, 0);
    }

    /// <summary>
    /// The stage's light direction (towards the light) in the character's frame: the original's (-3, 2, -3) on the stage, where the character faces
    /// the stage's -z.
    /// </summary>
    static readonly Vector3 StageLight = new(3, 2, 3);

    /// <summary>
    /// The studio's sun on the stage (Meitou, previewsun), in the character's frame (+z its front, the editor's yaw 0; +x the right of a camera
    /// there; the editor's first view keeps the light it had from the camera).
    /// </summary>
    static readonly Vector3 StudioStageSun = new(0.45f, 0.9f, 0.45f);

    /// <summary>
    /// The eye and the pivot it looks at, in the world: the pivot is 12 units above the feet (the original's stage root is 12 below the camera rig's
    /// pivot, whatever the character's size), or the head bone moved by the race's <c>portrait offset</c> (as the portrait camera does).
    /// </summary>
    (Vector3 Eye, Vector3 Pivot) StageCamera(in Live l, in EditorStageRequest r)
    {
        var asset = l.Asset;
        float scale = Math.Max(l.World.ScaleLength(), 1e-3f);
        Vector3 pivot;
        if (r.Head)
        {
            var race = asset.Appearance.Race;
            var offset = new Vector3(race.GetFloat("portrait offset x"), race.GetFloat("portrait offset y"), race.GetFloat("portrait offset z"));
            pivot = Vector3.Transform(PosePortrait(in l) + offset * scale, l.World);
        }
        else pivot = Ground(in l) + new Vector3(0, 12, 0);
        var local = Vector3.Transform(new Vector3(0, 0, r.Distance), Quaternion.CreateFromAxisAngle(Vector3.UnitY,
            r.Yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -r.Elevation));
        return (pivot + Vector3.TransformNormal(local, l.World) / scale, pivot);
    }

    /// <summary>
    /// The stage's eye and pivot (<see cref="StageCamera"/>) for the character listed under <paramref name="key"/> wherever it stands, from its last
    /// pose: the Meitou editor's camera in the world (<paramref name="request"/>'s key, size and shift are not used). Null when it is not listed.
    /// </summary>
    public (Vector3 Eye, Vector3 Pivot)? EditorCamera(long key, in EditorStageRequest request)
    {
        for (int i = 0; i < _liveCount; i++)
            if (_live[i].Key == key) return StageCamera(in _live[i], in request);
        return null;
    }

    /// <summary>
    /// Where the character stands: its root raised back by the body shape's lift (the renderer lowers the root so a short body's feet stay on the
    /// ground).
    /// </summary>
    static Vector3 Ground(in Live l) => l.World.Translation + new Vector3(0, l.Asset.GroundOffset, 0);

    /// <summary>
    /// Makes the stage picture and its depth at <paramref name="width"/> × <paramref name="height"/>, freeing the old ones.
    /// </summary>
    void MakeStage(int width, int height)
    {
        DisposeStage();
        _stageColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget,
            Name: "character stage"));
        _stageDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height, Use: TextureUse.DepthTarget,
            Name: "character stage depth"));
        _stageSampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear)),
            _stageColour.View());
    }

    /// <summary>
    /// Frees the stage pictures, the enhanced portraits' square and the resolve.
    /// </summary>
    void DisposeStage()
    {
        _stageColour?.Dispose();
        _stageDepth?.Dispose();
        (_stageColour, _stageDepth, _stageSampled) = (null, null, null);
        DisposeStageHdr();
        DisposePortraitHdr();
        _preview?.Dispose();
        _preview = null;
    }
}