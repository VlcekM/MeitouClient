using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// A portrait the host wants this frame: the character by its draw-list key, the atlas cell to draw it in, and the number that turns its camera.
/// </summary>
public readonly record struct PortraitRequest(long Key, int Cell, uint Serial);

/// <summary>
/// The squad portraits (docs/character-renderer.md "Portraits"): each requested character drawn into its cell of one atlas, from the camera the
/// original places by the race's <c>portrait distance</c> and <c>portrait offset</c> round the head bone, lit by the shader's plain model (no fog,
/// sky or shadows).
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>
    /// A cell's pitch in pixels and the atlas's cells across and down. A portrait fills <see cref="PortraitSizeFor"/> of its cell from the corner:
    /// the original's 128 px faithfully, the whole 256 px enhanced (the squad bar shows them 120 to 230 px wide from 1080 to 4K lines, so a 128 px
    /// one is stretched and soft). The pitch stays when the look changes, so the interface keeps the same atlas.
    /// </summary>
    public const int PortraitCell = 256, FaithfulPortraitSize = 128, PortraitColumns = 16, PortraitRows = 2;
    /// <summary>
    /// How much of its cell a portrait fills under <paramref name="look"/> (<see cref="PortraitCell"/>).
    /// </summary>
    public static int PortraitSizeFor(PreviewLook look) => look.Enhanced ? PortraitCell : FaithfulPortraitSize;
    /// <summary>
    /// The enhanced portraits' supersampling each way (<see cref="PreviewLook"/>): a still drawn once, so 16 samples a pixel cost nothing that
    /// matters.
    /// </summary>
    const int PortraitSupersample = 4;
    /// <summary>
    /// The vertical field of view of the portrait camera.
    /// </summary>
    const float PortraitFieldOfView = 25 * MathF.PI / 180;

    /// <summary>
    /// The portraits to draw this frame (the host fills it before <see cref="Update"/>, which also poses these characters when they are out of
    /// range).
    /// </summary>
    public List<PortraitRequest> PortraitRequests { get; } = [];
    /// <summary>
    /// The characters this update poses even out of range: the portrait requests' and the stage's (<see cref="GatherPortraitKeys"/>).
    /// </summary>
    readonly HashSet<long> _portraitKeys = [];
    /// <summary>
    /// This frame's live index of each character, by key, for the portrait requests.
    /// </summary>
    readonly Dictionary<long, int> _liveByKey = [];
    /// <summary>
    /// The live index drawn alone into a portrait, icon, stage or hover mask (-1: the world's views are drawn).
    /// </summary>
    int _portraitLive = -1;
    /// <summary>
    /// The portrait atlas (sRGB, <see cref="PortraitColumns"/> × <see cref="PortraitRows"/> cells) and its depth, made on first use.
    /// </summary>
    GpuTexture? _portraitColour, _portraitDepth;
    /// <summary>
    /// <see cref="_portraitColour"/> with a clamped bilinear sampler.
    /// </summary>
    SampledTexture? _portraitSampled;

    // The enhanced portraits (Look): each drawn into this half-float square, then resolved into its cell of the atlas above.
    /// <summary>
    /// The enhanced portraits' half-float square and its depth (<see cref="PortraitSupersample"/> times a cell's size).
    /// </summary>
    GpuTexture? _portraitHdr, _portraitHdrDepth;
    /// <summary>
    /// <see cref="_portraitHdr"/> sampled for the resolve.
    /// </summary>
    SampledTexture _portraitHdrSampled;

    /// <summary>
    /// The atlas (sRGB, transparent where no character is), made on first use. Render thread.
    /// </summary>
    public (GpuTexture Texture, SampledTexture Sampled) PortraitAtlas
    {
        get
        {
            if (_portraitColour is null)
            {
                int w = PortraitCell * PortraitColumns, h = PortraitCell * PortraitRows;
                _portraitColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Srgb8Alpha8, w, h,
                    Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "portraits"));
                _portraitDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h, Use: TextureUse.DepthTarget,
                    Name: "portraits depth"));
                _portraitSampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear)),
                    _portraitColour.View());
                _cleared = false;
                _portraitShown.Clear();
            }
            return (_portraitColour, _portraitSampled!.Value);
        }
    }

    /// <summary>
    /// Whether the atlas has been cleared since it was made.
    /// </summary>
    bool _cleared;

    /// <summary>
    /// The keys <see cref="Update"/> poses even out of range.
    /// </summary>
    void GatherPortraitKeys()
    {
        _portraitKeys.Clear();
        foreach (var r in PortraitRequests) _portraitKeys.Add(r.Key);
        if (StageRequest is { } stage) _portraitKeys.Add(stage.Key);
    }

    /// <summary>
    /// What a cell last showed: the character's key and the serial of its look, its asset, its radius and its textures' images, so an unchanged cell
    /// is not drawn again.
    /// </summary>
    sealed record ShownPortrait(long Key, uint Serial, CharacterAsset Asset, float Radius,
        (GpuTexture? Image, bool Swizzled)[] Textures);
    /// <summary>
    /// The character, appearance and streamed texture images each cell last showed.
    /// </summary>
    readonly Dictionary<int, ShownPortrait> _portraitShown = [];
    /// <summary>
    /// The still pose's bone palette and attach matrices (<see cref="PosePortrait"/>), grown as needed.
    /// </summary>
    Matrix4x4[] _portraitBones = [], _portraitAttach = [];
    /// <summary>
    /// Scratch: the still pose's layers (the idle at time 0 and the posture layers).
    /// </summary>
    readonly List<ActiveLayer> _portraitLayers = [];

    /// <summary>
    /// Draws the requested portraits into their cells (after <see cref="Update"/>, outside any pass). Like the original's, a portrait is a still: the
    /// character in its idle stance at its start and its posture layers, not its live animation. A cell is posed and drawn anew when its character or
    /// appearance (equipment) changes, or its size, camera serial or streamed textures change; a character not posed this frame (not in the list)
    /// keeps what its cell last showed.
    /// </summary>
    public void DrawPortraits()
    {
        if (PortraitRequests.Count == 0) return;
        var atlas = PortraitAtlas.Texture;   // made on first use, with the depth
        _liveByKey.Clear();
        for (int i = 0; i < _liveCount; i++) _liveByKey[_live[i].Key] = i;
        bool any = !_cleared;
        foreach (var request in PortraitRequests)
        {
            if (Due(request, out _))
            {
                any = true;
                break;
            }
        }

        if (!any) return;

        if (!_cleared) ClearPortraitAtlas(atlas);
        if (_look.Enhanced) DrawEnhancedPortraits(atlas);
        else DrawFaithfulPortraits(atlas);
    }

    /// <summary>
    /// The atlas starts transparent, once; each cell drawn clears its own square.
    /// </summary>
    void ClearPortraitAtlas(GpuTexture atlas)
    {
        var clear = Gpu.BeginNative("portraits clear");
        var whole = PassTargets.Of(atlas, _portraitDepth);
        clear.BeginRendering(new RenderingDesc(whole.Colour, whole.Depth, whole.Width, whole.Height));
        Gpu.BeginHostPass(clear, whole, DrawState.Scene(whole.Formats));
        clear.Clear(true, default, false, 1, new PixelRect(0, 0, (uint)whole.Width, (uint)whole.Height));
        Gpu.EndHostPass(clear);
        clear.EndRendering();
        Gpu.EndNative(clear);
        _cleared = true;
    }

    /// <summary>
    /// Meitou: each due cell alone, PortraitSupersample times larger each way into the half-float square, then resolved into its cell (a still: drawn
    /// once).
    /// </summary>
    void DrawEnhancedPortraits(GpuTexture atlas)
    {
        var supersampled = _look with { Supersample = PortraitSupersample };
        if (_portraitHdr is null)
        {
            (_portraitHdr, _portraitHdrDepth, _portraitHdrSampled) = PreviewResolve.Targets(Gpu, PortraitCell, PortraitCell, supersampled,
                    "portrait hdr");
        }

        _preview ??= new PreviewResolve(Gpu);

        foreach (var request in PortraitRequests)
        {
            if (!Due(request, out int index)) continue;
            var pose = BeginPortraitPose(request, index);
            try
            {
                DrawPortraitHdr(index, in pose);
            }
            finally
            {
                EndPortraitPose(index, pose);
            }

            int x = request.Cell % PortraitColumns * PortraitCell, y = request.Cell / PortraitColumns * PortraitCell;
            _preview.Run(_portraitHdrSampled, atlas, supersampled, [new PixelRect(x, y, PortraitCell, PortraitCell)]);
        }
    }

    /// <summary>
    /// <paramref name="index"/>'s posed character into the whole half-float square, through the studio when it is on.
    /// </summary>
    void DrawPortraitHdr(int index, in PortraitPose pose)
    {
        int size = _portraitHdr!.Desc.Width;
        var constants = new ViewConstants { ViewProjection = pose.View * pose.Projection, Eye = pose.Eye, LightDir = pose.Light };

        // The studio (previewsun): its sun from the front on the camera's right, 35 degrees up, its shadow and occlusion drawn first.
        if (StudioOn) constants = PortraitStudioView(index, in pose, constants.ViewProjection, size);

        var cmd = Gpu.BeginNative("portrait");
        var target = PassTargets.Of(_portraitHdr, _portraitHdrDepth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width,
            target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        var whole = new PixelRect(0, 0, (uint)target.Width, (uint)target.Height);
        DrawPortraitView(cmd, index, new PixelViewport(0, 0, target.Width, target.Height, 0, 1), whole, constants, pose.Eye);
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// The studio's shadow and occlusion for a portrait, its sun from the front on the camera's right; returns the colour draw's view constants.
    /// </summary>
    ViewConstants PortraitStudioView(int index, in PortraitPose pose, Matrix4x4 viewProjection, int size)
    {
        // Level: the camera is at the head's height, above the body's centre, so the way to it already climbs.
        var toEye = Vector3.Normalize(new Vector3(pose.Eye.X - _live[index].Centre.X, 0, pose.Eye.Z - _live[index].Centre.Z));
        var right = Vector3.Normalize(Vector3.Cross(toEye, Vector3.UnitY));
        var toSun = 0.6f * toEye - 0.35f * right + 0.49f * Vector3.UnitY;
        return StudioView(index, viewProjection, pose.Projection, pose.Eye, toSun, _portraitHdr!, _portraitHdrDepth!, size, size);
    }

    /// <summary>
    /// Faithful: every due cell in one pass over the atlas, the original's 128 px in the cell's corner, the whole cell cleared.
    /// </summary>
    void DrawFaithfulPortraits(GpuTexture atlas)
    {
        var pass = Gpu.BeginNative("portraits");
        var cells = PassTargets.Of(atlas, _portraitDepth);
        pass.BeginRendering(new RenderingDesc(cells.Colour, cells.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, cells.Width, cells.Height));
        Gpu.BeginHostPass(pass, cells, DrawState.Scene(cells.Formats));
        foreach (var request in PortraitRequests)
        {
            if (!Due(request, out int index)) continue;
            int x = request.Cell % PortraitColumns * PortraitCell, y = request.Cell / PortraitColumns * PortraitCell;
            var viewport = new PixelViewport(x, y, FaithfulPortraitSize, FaithfulPortraitSize, 0, 1);
            DrawPortraitCell(pass, request, index, viewport, new PixelRect(x, y, PortraitCell, PortraitCell));
        }
        Gpu.EndHostPass(pass);
        pass.EndRendering();
        Gpu.EndNative(pass);
    }

    /// <summary>
    /// Poses <paramref name="request"/>'s character still and draws it into <paramref name="rect"/> (cleared first) of the open pass.
    /// </summary>
    void DrawPortraitCell(IGpuCommandList cmd, PortraitRequest request, int index, PixelViewport viewport, PixelRect rect)
    {
        var pose = BeginPortraitPose(request, index);
        try
        {
            var constants = new ViewConstants { ViewProjection = pose.View * pose.Projection, Eye = pose.Eye, LightDir = pose.Light };
            DrawPortraitView(cmd, index, viewport, rect, constants, pose.Eye);
        }
        finally
        {
            EndPortraitPose(index, pose);
        }
    }

    /// <summary>
    /// The still pose and camera of a portrait being drawn, and what <see cref="EndPortraitPose"/> puts back.
    /// </summary>
    readonly record struct PortraitPose(Vector3 Eye, Matrix4x4 View, Matrix4x4 Projection, Vector3 Light,
        Transient Bones, Transient PreviousBones, int BoneBase, int AttachBase, bool Weapon, bool Ranged);

    /// <summary>
    /// Poses <paramref name="index"/>'s character still for <paramref name="request"/>'s portrait (this view's own bone memory and attachments past
    /// the frame's, weapon sheathed) and places its camera; <see cref="EndPortraitPose"/> restores the live pose.
    /// </summary>
    PortraitPose BeginPortraitPose(PortraitRequest request, int index)
    {
        ref var l = ref _live[index];
        var head = PosePortrait(in l);
        _portraitShown[request.Cell] = new ShownPortrait(request.Key, request.Serial, l.Asset, l.Radius,
            l.Asset.Parts.SelectMany(p => p.Material.Tex).Select(t => (t?.Native, t?.Swizzled ?? false)).ToArray());
        var (eye, target) = PortraitCamera(in l, head, request.Serial);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(PortraitFieldOfView, 1, 0.5f, 400);

        // The key light from the camera's side, above and a little to the left.
        var toEye = Vector3.Normalize(eye - target);
        var left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toEye));
        var light = Vector3.Normalize(toEye + 0.7f * Vector3.UnitY + 0.4f * left);

        // The still pose's palette in place of the live one, the live one kept in the pose for EndPortraitPose.
        var pose = new PortraitPose(eye, view, projection, light, _bones, _previousBones, l.BoneBase, l.AttachBase, l.WeaponDrawn, l.RangedDrawn);
        int bonesCount = l.Asset.Rig.BoneCount;
        _bones = _previousBones = AllocateConstants((ulong)Math.Max(bonesCount, 1) * 64, 256);
        _portraitBones.AsSpan(0, bonesCount).CopyTo(new Span<Matrix4x4>(_bones.Pointer, bonesCount));
        if (_attach.Length < _attachCount + l.Asset.AttachCount)
            Array.Resize(ref _attach, _attachCount + l.Asset.AttachCount);
        _portraitAttach.AsSpan(0, l.Asset.AttachCount).CopyTo(_attach.AsSpan(_attachCount));
        (l.BoneBase, l.AttachBase, l.WeaponDrawn, l.RangedDrawn) = (0, _attachCount, false, false);

        return pose;
    }

    /// <summary>
    /// Puts back the live pose <see cref="BeginPortraitPose"/> replaced.
    /// </summary>
    void EndPortraitPose(int index, in PortraitPose pose)
    {
        ref var l = ref _live[index];
        (_bones, _previousBones) = (pose.Bones, pose.PreviousBones);
        (l.BoneBase, l.AttachBase, l.WeaponDrawn, l.RangedDrawn) = (pose.BoneBase, pose.AttachBase, pose.Weapon, pose.Ranged);
    }

    /// <summary>
    /// The posed portrait character (<see cref="_portraitLive"/>) into <paramref name="rect"/> (cleared first) of the open pass, with
    /// <paramref name="constants"/>.
    /// </summary>
    void DrawPortraitView(IGpuCommandList cmd, int index, PixelViewport viewport, PixelRect rect, in ViewConstants constants, Vector3 eye)
    {
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(viewport, rect);

        // The shader's plain model or the studio (fixed lights: a portrait is a still and does not follow the sky), unclamped either way; enhanced,
        // the resolve finishes it.
        _portraitLive = index;
        try
        {
            DrawView(constants, eye, []);
        }
        finally
        {
            _portraitLive = -1;
        }
    }

    /// <summary>
    /// Frees the enhanced portraits' square and its depth.
    /// </summary>
    void DisposePortraitHdr()
    {
        _portraitHdr?.Dispose();
        _portraitHdrDepth?.Dispose();
        (_portraitHdr, _portraitHdrDepth) = (null, null);
    }

    /// <summary>
    /// Whether <paramref name="request"/>'s cell needs drawing this frame: its character is in the list and the cell shows someone else or another
    /// appearance, size, camera serial or streamed texture images.
    /// </summary>
    bool Due(PortraitRequest request, out int index)
    {
        index = -1;
        if (request.Cell < 0 || request.Cell >= PortraitColumns * PortraitRows || !_liveByKey.TryGetValue(request.Key, out index))
            return false;
        ref var l = ref _live[index];
        if (!_portraitShown.TryGetValue(request.Cell, out var shown) || shown.Key != request.Key ||
            shown.Serial != request.Serial || shown.Asset != l.Asset || shown.Radius != l.Radius) return true;
        int texture = 0;
        foreach (var part in l.Asset.Parts)
        {
            foreach (var t in part.Material.Tex)
            {
                if (texture >= shown.Textures.Length || shown.Textures[texture] != (t?.Native, t?.Swizzled ?? false))
                    return true;
                texture++;
            }
        }

        return texture != shown.Textures.Length;
    }

    /// <summary>
    /// Poses <paramref name="l"/> still for its portrait (its idle stance at time 0 and its posture layers) into <see cref="_portraitBones"/> and
    /// <see cref="_portraitAttach"/>; returns the head bone's position in the model's space.
    /// </summary>
    Vector3 PosePortrait(in Live l)
    {
        var asset = l.Asset;
        var rig = asset.Rig;
        _portraitLayers.Clear();
        if (rig.Layer(_content.Database, asset.Idle, asset.Animal) is { } idle)
            _portraitLayers.Add(new ActiveLayer(idle, 0, 1));
        foreach (var (def, time) in asset.Fixed) _portraitLayers.Add(new ActiveLayer(def, time, 1));
        if (_portraitBones.Length < rig.BoneCount) _portraitBones = new Matrix4x4[rig.BoneCount];
        if (_portraitAttach.Length < asset.AttachCount) _portraitAttach = new Matrix4x4[asset.AttachCount];
        var scratch = rig.Rent();
        try
        {
            scratch.Pose(asset.Shape, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_portraitLayers), _portraitBones);
            foreach (var part in asset.Parts)
            {
                if (part.AttachIndex >= 0)
                    _portraitAttach[part.AttachIndex] = scratch.TagTransform(part.Bone, part.Offset);
            }

            return asset.HeadBone >= 0 ? scratch.BoneTransform(asset.HeadBone).Translation : new Vector3(0, asset.Height * 0.85f, 0);
        }
        finally
        {
            rig.Return(scratch);
        }
    }

    /// <summary>
    /// The original's portrait camera (docs/character-renderer.md "Portraits"): it looks at the head bone moved by the race's <c>portrait offset</c>
    /// (in the model's axes, times the character's size) from <c>portrait distance</c> along the model's front, turned about the vertical by up to 45
    /// degrees either way and about the side axis by a few degrees, both by the character's serial.
    /// </summary>
    static (Vector3 Eye, Vector3 Target) PortraitCamera(in Live l, Vector3 head, uint serial)
    {
        var race = l.Asset.Appearance.Race;
        float scale = l.World.ScaleLength();
        var offset = new Vector3(race.GetFloat("portrait offset x"), race.GetFloat("portrait offset y"), race.GetFloat("portrait offset z"));
        var target = Vector3.Transform(head, l.World) + Vector3.TransformNormal(offset, l.World);
        float yaw = (serial % 500 / 1000f - 0.25f) * MathF.PI, pitch = (serial % 252 / 1000f - 0.083f) * MathF.PI;
        var local = Vector3.Transform(new Vector3(0, 0, race.GetFloat("portrait distance", 10)), Quaternion.CreateFromAxisAngle(Vector3.UnitY,
            yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch));
        var direction = Vector3.TransformNormal(local, l.World) / Math.Max(scale, 1e-3f);
        return (target + direction, target);
    }

    /// <summary>
    /// Frees the atlas, its depth and the enhanced square.
    /// </summary>
    void DisposePortraits()
    {
        _portraitColour?.Dispose();
        _portraitDepth?.Dispose();
        (_portraitColour, _portraitDepth, _portraitSampled) = (null, null, null);
        DisposePortraitHdr();
    }
}