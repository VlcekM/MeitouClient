using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>
/// The character editor's view of the character being edited (<see cref="CharacterRenderer.StageRequest"/>): the character by its draw-list key, the size of the picture
/// and the camera as the editor's orbit sets it. <paramref name="Yaw"/> turns the camera round the character (0: in front of it) and <paramref name="Elevation"/> lifts it,
/// both in radians; <paramref name="Distance"/> is from the pivot, which is 12 units above the feet or, with <paramref name="Head"/>, the head;
/// <paramref name="ShiftX"/> moves the pivot sideways on the picture (normalised device units, negative: left).
/// </summary>
public readonly record struct EditorStageRequest(long Key, int Width, int Height, float Yaw, float Elevation, float Distance, bool Head, float FieldOfView, float ShiftX);

/// <summary>
/// The character editor's stage (docs/characters.md "Character editor"): the original draws the edited character into a workspace of its own and puts the plain
/// ground texture behind it; here the character is drawn alone into a window-sized picture, transparent round it, which the interface shows over the ground texture.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>The character to draw into <see cref="StageTexture"/> this frame (the host sets it before <see cref="Update"/>, which also poses the character out of range); null: none.</summary>
    public EditorStageRequest? StageRequest { get; set; }

    GpuTexture? stageColour, stageDepth;
    SampledTexture? stageSampled;
    // The enhanced stage (Look): the half-float picture the character and the platform are drawn into, resolved into stageColour.
    GpuTexture? stageHdr, stageHdrDepth;
    SampledTexture stageHdrSampled;
    int stageHdrScale;

    PreviewLook look;
    PreviewResolve? preview;

    /// <summary>
    /// How the stage and the portraits are finished (<see cref="PreviewLook"/>); the host sets it every frame from the world's options. A change redraws every portrait
    /// (the stage is drawn every frame).
    /// </summary>
    public PreviewLook Look
    {
        get => look;
        set
        {
            if (look == value) return;
            look = value;
            portraitShown.Clear();
        }
    }

    /// <summary>The stage picture (8-bit UNORM holding the lit values as they are, as the original's; transparent where nothing is drawn; upside down as the renderer's targets are), made by <see cref="DrawStage"/>; null before the first.</summary>
    public (GpuTexture Texture, SampledTexture Sampled)? StageTexture => stageColour is null ? null : (stageColour, stageSampled!.Value);

    /// <summary>
    /// Where the platform is drawn after the character (<c>WorldObjectRenderer.DrawStageProp</c>), before <see cref="ResolveStage"/>: the stage picture and its depth, or the
    /// enhanced half-float ones, and the fog distance that selects the stage light (-2 the original's, -3 unclamped); null before the first.
    /// </summary>
    public (GpuTexture Colour, GpuTexture Depth, float FogDistance)? StageDrawTarget =>
        stageColour is null ? null : look.Enhanced && stageHdr is not null ? (stageHdr, stageHdrDepth!, -3f) : (stageColour, stageDepth!, -2f);

    /// <summary>
    /// The last stage drawn: its camera, the light (towards it, in the world) and where the character stands (its position and facing, no scale), which the platform's
    /// stage frame is turned half round from; null when no character was drawn.
    /// </summary>
    public (Matrix4x4 ViewProjection, Vector3 Eye, Vector3 Light, Matrix4x4 Feet)? StageView { get; private set; }

    /// <summary>
    /// Draws <see cref="StageRequest"/>'s character with its live pose (after <see cref="Update"/>, outside any pass), lit as the original's stage (<c>rtt.hlsl</c>, the shader's stage
    /// branch, a fog distance of -2): the light comes from the stage's (-3, 2, -3), the stage turned with the character, who stands on it turned half round (<c>FUN_1405f03c0</c>).
    /// </summary>
    public void DrawStage()
    {
        if (StageRequest is not { } r || r.Width <= 0 || r.Height <= 0) return;
        StageView = null;
        if (stageColour is null || stageColour.Desc.Width != r.Width || stageColour.Desc.Height != r.Height) MakeStage(r.Width, r.Height);
        bool hdr = look.Enhanced;
        int scale = hdr ? StageScale(r.Width, r.Height) : 1;
        if (hdr && (stageHdr is null || stageHdr.Desc.Width != r.Width * scale || stageHdr.Desc.Height != r.Height * scale))
        {
            DisposeStageHdr();
            (stageHdr, stageHdrDepth, stageHdrSampled) = PreviewResolve.Targets(Gpu, r.Width, r.Height, look with { Supersample = scale }, "character stage hdr");
            stageHdrScale = scale;
        }
        int index = -1;
        for (int i = 0; i < liveCount; i++)
            if (live[i].Key == r.Key) { index = i; break; }
        var cmd = Gpu.BeginNative("character stage");
        var target = hdr ? PassTargets.Of(stageHdr, stageHdrDepth) : PassTargets.Of(stageColour, stageDepth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        var rect = new PixelRect(0, 0, (uint)target.Width, (uint)target.Height);
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(new PixelViewport(0, 0, target.Width, target.Height, 0, 1), rect);
        if (index >= 0)
        {
            ref var l = ref live[index];
            var (eye, pivot) = StageCamera(in l, in r);
            var view = Matrix4x4.CreateLookAt(eye, pivot, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(r.FieldOfView, r.Width / (float)r.Height, 0.5f, 400) * Matrix4x4.CreateTranslation(r.ShiftX, 0, 0);
            var light = Vector3.Normalize(Vector3.TransformNormal(StageLight, l.World));
            Matrix4x4.Decompose(l.World, out _, out var facing, out _);
            StageView = (view * projection, eye, light, Matrix4x4.CreateFromQuaternion(facing) * Matrix4x4.CreateTranslation(Ground(in l)));
            portraitLive = index;
            try { DrawView(new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = light, FogDistance = hdr ? -3 : -2 }, eye, []); }
            finally { portraitLive = -1; }
        }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>The enhanced stage's picture into the stage picture (after the platform is drawn into <see cref="StageDrawTarget"/>); nothing for the original's.</summary>
    public void ResolveStage()
    {
        if (!look.Enhanced || stageHdr is null || stageColour is null || StageRequest is null) return;
        preview ??= new PreviewResolve(Gpu);
        preview.Run(stageHdrSampled, stageColour, look with { Supersample = stageHdrScale }, [new PixelRect(0, 0, (uint)stageColour.Desc.Width, (uint)stageColour.Desc.Height)]);
    }

    /// <summary>The enhanced stage's supersampling: the look's, down to 1 where the picture would pass 16.6 million pixels (twice 4K's; a window over 2880 x 1440 or so).</summary>
    int StageScale(int width, int height)
    {
        int s = Math.Max(look.Supersample, 1);
        while (s > 1 && (long)width * height * s * s > 16_600_000) s--;
        return s;
    }

    /// <summary>The editor has closed: its half-float stage goes (window-sized and supersampled, the largest of the previews).</summary>
    void ReleaseStage() => DisposeStageHdr();

    void DisposeStageHdr()
    {
        stageHdr?.Dispose();
        stageHdrDepth?.Dispose();
        (stageHdr, stageHdrDepth, stageHdrScale) = (null, null, 0);
    }

    /// <summary>The stage's light direction (towards the light) in the character's frame: the original's (-3, 2, -3) on the stage, where the character faces the stage's -z.</summary>
    static readonly Vector3 StageLight = new(3, 2, 3);

    /// <summary>
    /// The eye and the pivot it looks at, in the world: the pivot is 12 units above the feet (the original's stage root is 12 below the camera rig's pivot, whatever the
    /// character's size), or the head bone moved by the race's <c>portrait offset</c> (as the portrait camera does).
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
        var local = Vector3.Transform(new Vector3(0, 0, r.Distance), Quaternion.CreateFromAxisAngle(Vector3.UnitY, r.Yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -r.Elevation));
        return (pivot + Vector3.TransformNormal(local, l.World) / scale, pivot);
    }

    /// <summary>
    /// The stage's eye and pivot (<see cref="StageCamera"/>) for the character listed under <paramref name="key"/> wherever it stands, from its last pose: the Meitou editor's
    /// camera in the world (<paramref name="request"/>'s key, size and shift are not used). Null when it is not listed.
    /// </summary>
    public (Vector3 Eye, Vector3 Pivot)? EditorCamera(long key, in EditorStageRequest request)
    {
        for (int i = 0; i < liveCount; i++)
            if (live[i].Key == key) return StageCamera(in live[i], in request);
        return null;
    }

    /// <summary>Where the character stands: its root raised back by the body shape's lift (the renderer lowers the root so a short body's feet stay on the ground).</summary>
    static Vector3 Ground(in Live l) => l.World.Translation + new Vector3(0, l.Asset.GroundOffset, 0);

    void MakeStage(int width, int height)
    {
        DisposeStage();
        stageColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "character stage"));
        stageDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height, Use: TextureUse.DepthTarget, Name: "character stage depth"));
        stageSampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), stageColour.View());
    }

    void DisposeStage()
    {
        stageColour?.Dispose();
        stageDepth?.Dispose();
        (stageColour, stageDepth, stageSampled) = (null, null, null);
        DisposeStageHdr();
        DisposePortraitHdr();
        preview?.Dispose();
        preview = null;
    }
}
