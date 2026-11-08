using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Data.Characters;
using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Characters;

/// <summary>A portrait the host wants this frame: the character by its draw-list key, the atlas cell to draw it in, and the number that turns its camera.</summary>
public readonly record struct PortraitRequest(long Key, int Cell, uint Serial);

/// <summary>
/// The squad portraits (docs/character-renderer.md "Portraits"): each requested character drawn into its cell of one atlas, from the camera the original
/// places by the race's <c>portrait distance</c> and <c>portrait offset</c> round the head bone, lit by the shader's plain model (no fog, sky or shadows).
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>
    /// A cell's pitch in pixels and the atlas's cells across and down. A portrait fills <see cref="PortraitSizeFor"/> of its cell from the corner: the original's
    /// 128 px faithfully, the whole 256 px enhanced (the squad bar shows them 120 to 230 px wide from 1080 to 4K lines, so a 128 px one is stretched and soft).
    /// The pitch stays when the look changes, so the interface keeps the same atlas.
    /// </summary>
    public const int PortraitCell = 256, FaithfulPortraitSize = 128, PortraitColumns = 16, PortraitRows = 2;
    /// <summary>How much of its cell a portrait fills under <paramref name="look"/> (<see cref="PortraitCell"/>).</summary>
    public static int PortraitSizeFor(PreviewLook look) => look.Enhanced ? PortraitCell : FaithfulPortraitSize;
    /// <summary>The enhanced portraits' supersampling each way (<see cref="PreviewLook"/>): a still drawn once, so 16 samples a pixel cost nothing that matters.</summary>
    const int PortraitSupersample = 4;
    /// <summary>The vertical field of view of the portrait camera.</summary>
    const float PortraitFieldOfView = 25 * MathF.PI / 180;

    /// <summary>The portraits to draw this frame (the host fills it before <see cref="Update"/>, which also poses these characters when they are out of range).</summary>
    public List<PortraitRequest> PortraitRequests { get; } = [];
    readonly HashSet<long> portraitKeys = [];
    readonly Dictionary<long, int> liveByKey = [];
    int portraitLive = -1;
    GpuTexture? portraitColour, portraitDepth;
    SampledTexture? portraitSampled;
    // The enhanced portraits (Look): each drawn into this half-float square, then resolved into its cell of the atlas above.
    GpuTexture? portraitHdr, portraitHdrDepth;
    SampledTexture portraitHdrSampled;

    /// <summary>The atlas (sRGB, transparent where no character is), made on first use. Render thread.</summary>
    public (GpuTexture Texture, SampledTexture Sampled) PortraitAtlas
    {
        get
        {
            if (portraitColour is null)
            {
                int w = PortraitCell * PortraitColumns, h = PortraitCell * PortraitRows;
                portraitColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Srgb8Alpha8, w, h, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "portraits"));
                portraitDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h, Use: TextureUse.DepthTarget, Name: "portraits depth"));
                portraitSampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
                    TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), portraitColour.View());
                cleared = false;
                portraitShown.Clear();
            }
            return (portraitColour, portraitSampled!.Value);
        }
    }

    bool cleared;

    /// <summary>The keys <see cref="Update"/> poses even out of range.</summary>
    void GatherPortraitKeys()
    {
        portraitKeys.Clear();
        foreach (var r in PortraitRequests) portraitKeys.Add(r.Key);
        if (StageRequest is { } stage) portraitKeys.Add(stage.Key);
    }

    sealed record ShownPortrait(long Key, uint Serial, CharacterAsset Asset, float Radius,
        (GpuTexture? Image, bool Swizzled)[] Textures);
    /// <summary>The character, appearance and streamed texture images each cell last showed.</summary>
    readonly Dictionary<int, ShownPortrait> portraitShown = [];
    Matrix4x4[] portraitBones = [], portraitAttach = [];
    readonly List<ActiveLayer> portraitLayers = [];

    /// <summary>
    /// Draws the requested portraits into their cells (after <see cref="Update"/>, outside any pass). Like the original's, a portrait is a still: the character in its
    /// idle stance at its start and its posture layers, not its live animation. A cell is posed and drawn anew when its character or appearance (equipment) changes,
    /// or its size, camera serial or streamed textures change; a
    /// character not posed this frame (not in the list) keeps what its cell last showed.
    /// </summary>
    public void DrawPortraits()
    {
        if (PortraitRequests.Count == 0) return;
        var atlas = PortraitAtlas.Texture;   // made on first use, with the depth
        liveByKey.Clear();
        for (int i = 0; i < liveCount; i++) liveByKey[live[i].Key] = i;
        bool any = !cleared;
        foreach (var request in PortraitRequests)
            if (Due(request, out _))
            {
                any = true;
                break;
            }
        if (!any) return;

        if (!cleared) ClearPortraitAtlas(atlas);
        if (look.Enhanced) DrawEnhancedPortraits(atlas);
        else DrawFaithfulPortraits(atlas);
    }

    /// <summary>The atlas starts transparent, once; each cell drawn clears its own square.</summary>
    void ClearPortraitAtlas(GpuTexture atlas)
    {
        var clear = Gpu.BeginNative("portraits clear");
        var whole = PassTargets.Of(atlas, portraitDepth);
        clear.BeginRendering(new RenderingDesc(whole.Colour, whole.Depth, whole.Width, whole.Height));
        Gpu.BeginHostPass(clear, whole, DrawState.Scene(whole.Formats));
        clear.Clear(true, default, false, 1, new PixelRect(0, 0, (uint)whole.Width, (uint)whole.Height));
        Gpu.EndHostPass(clear);
        clear.EndRendering();
        Gpu.EndNative(clear);
        cleared = true;
    }

    /// <summary>Meitou: each due cell alone, PortraitSupersample times larger each way into the half-float square, then resolved into its cell (a still: drawn once).</summary>
    void DrawEnhancedPortraits(GpuTexture atlas)
    {
        var supersampled = look with { Supersample = PortraitSupersample };
        if (portraitHdr is null)
            (portraitHdr, portraitHdrDepth, portraitHdrSampled) = PreviewResolve.Targets(Gpu, PortraitCell, PortraitCell, supersampled, "portrait hdr");
        preview ??= new PreviewResolve(Gpu);

        foreach (var request in PortraitRequests)
        {
            if (!Due(request, out int index)) continue;
            var cmd = Gpu.BeginNative("portrait");
            var target = PassTargets.Of(portraitHdr, portraitHdrDepth);
            cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
            Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
            var whole = new PixelRect(0, 0, (uint)target.Width, (uint)target.Height);
            DrawPortraitCell(cmd, request, index, new PixelViewport(0, 0, target.Width, target.Height, 0, 1), whole);
            Gpu.EndHostPass(cmd);
            cmd.EndRendering();
            Gpu.EndNative(cmd);

            int x = request.Cell % PortraitColumns * PortraitCell, y = request.Cell / PortraitColumns * PortraitCell;
            preview.Run(portraitHdrSampled, atlas, supersampled, [new PixelRect(x, y, PortraitCell, PortraitCell)]);
        }
    }

    /// <summary>Faithful: every due cell in one pass over the atlas, the original's 128 px in the cell's corner, the whole cell cleared.</summary>
    void DrawFaithfulPortraits(GpuTexture atlas)
    {
        var pass = Gpu.BeginNative("portraits");
        var cells = PassTargets.Of(atlas, portraitDepth);
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

    /// <summary>Poses <paramref name="request"/>'s character still and draws it into <paramref name="rect"/> (cleared first) of the open pass.</summary>
    void DrawPortraitCell(IGpuCommandList cmd, PortraitRequest request, int index, PixelViewport viewport, PixelRect rect)
    {
        ref var l = ref live[index];
        var head = PosePortrait(in l);
        portraitShown[request.Cell] = new ShownPortrait(request.Key, request.Serial, l.Asset, l.Radius,
            l.Asset.Parts.SelectMany(p => p.Material.Tex).Select(t => (t?.Native, t?.Swizzled ?? false)).ToArray());
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(viewport, rect);
        var (eye, target) = PortraitCamera(in l, head, request.Serial);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(PortraitFieldOfView, 1, 0.5f, 400);
        // The key light from the camera's side, above and a little to the left.
        var toEye = Vector3.Normalize(eye - target);
        var left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, toEye));
        var light = Vector3.Normalize(toEye + 0.7f * Vector3.UnitY + 0.4f * left);
        // The still pose's palette in place of the live one: this view's own bone memory and attachments past the frame's, weapon sheathed.
        var (liveBones, livePrevious) = (bones, previousBones);
        var (boneBase, attachBase, weapon, ranged) = (l.BoneBase, l.AttachBase, l.WeaponDrawn, l.RangedDrawn);
        int bonesCount = l.Asset.Rig.BoneCount;
        bones = previousBones = AllocateConstants((ulong)Math.Max(bonesCount, 1) * 64, 256);
        portraitBones.AsSpan(0, bonesCount).CopyTo(new Span<Matrix4x4>(bones.Pointer, bonesCount));
        if (attach.Length < attachCount + l.Asset.AttachCount) Array.Resize(ref attach, attachCount + l.Asset.AttachCount);
        portraitAttach.AsSpan(0, l.Asset.AttachCount).CopyTo(attach.AsSpan(attachCount));
        (l.BoneBase, l.AttachBase, l.WeaponDrawn, l.RangedDrawn) = (0, attachCount, false, false);
        portraitLive = index;
        // The shader's plain model (fixed lights: a portrait is a still and does not follow the sky), unclamped either way; enhanced, the resolve finishes it.
        try { DrawView(new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = light }, eye, []); }
        finally { portraitLive = -1; (bones, previousBones) = (liveBones, livePrevious); (l.BoneBase, l.AttachBase, l.WeaponDrawn, l.RangedDrawn) = (boneBase, attachBase, weapon, ranged); }
    }

    void DisposePortraitHdr()
    {
        portraitHdr?.Dispose();
        portraitHdrDepth?.Dispose();
        (portraitHdr, portraitHdrDepth) = (null, null);
    }

    /// <summary>Whether <paramref name="request"/>'s cell needs drawing this frame: its character is in the list and the cell shows someone else or another
    /// appearance, size, camera serial or streamed texture images.</summary>
    bool Due(PortraitRequest request, out int index)
    {
        index = -1;
        if (request.Cell < 0 || request.Cell >= PortraitColumns * PortraitRows || !liveByKey.TryGetValue(request.Key, out index)) return false;
        ref var l = ref live[index];
        if (!portraitShown.TryGetValue(request.Cell, out var shown) || shown.Key != request.Key ||
            shown.Serial != request.Serial || shown.Asset != l.Asset || shown.Radius != l.Radius) return true;
        int texture = 0;
        foreach (var part in l.Asset.Parts)
            foreach (var t in part.Material.Tex)
            {
                if (texture >= shown.Textures.Length || shown.Textures[texture] != (t?.Native, t?.Swizzled ?? false)) return true;
                texture++;
            }
        return texture != shown.Textures.Length;
    }

    /// <summary>Poses <paramref name="l"/> still for its portrait (its idle stance at time 0 and its posture layers) into <see cref="portraitBones"/> and
    /// <see cref="portraitAttach"/>; returns the head bone's position in the model's space.</summary>
    Vector3 PosePortrait(in Live l)
    {
        var asset = l.Asset;
        var rig = asset.Rig;
        portraitLayers.Clear();
        if (rig.Layer(content.Database, asset.Idle, asset.Animal) is { } idle) portraitLayers.Add(new ActiveLayer(idle, 0, 1));
        foreach (var (def, time) in asset.Fixed) portraitLayers.Add(new ActiveLayer(def, time, 1));
        if (portraitBones.Length < rig.BoneCount) portraitBones = new Matrix4x4[rig.BoneCount];
        if (portraitAttach.Length < asset.AttachCount) portraitAttach = new Matrix4x4[asset.AttachCount];
        var scratch = rig.Rent();
        try
        {
            scratch.Pose(asset.Shape, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(portraitLayers), portraitBones);
            foreach (var part in asset.Parts)
                if (part.AttachIndex >= 0) portraitAttach[part.AttachIndex] = scratch.TagTransform(part.Bone, part.Offset);
            return asset.HeadBone >= 0 ? scratch.BoneTransform(asset.HeadBone).Translation : new Vector3(0, asset.Height * 0.85f, 0);
        }
        finally { rig.Return(scratch); }
    }

    /// <summary>
    /// The original's portrait camera (docs/character-renderer.md "Portraits"): it looks at the head bone moved by the race's <c>portrait offset</c> (in the model's
    /// axes, times the character's size) from <c>portrait distance</c> along the model's front, turned about the vertical by up to 45 degrees either way and about
    /// the side axis by a few degrees, both by the character's serial.
    /// </summary>
    static (Vector3 Eye, Vector3 Target) PortraitCamera(in Live l, Vector3 head, uint serial)
    {
        var race = l.Asset.Appearance.Race;
        float scale = l.World.ScaleLength();
        var offset = new Vector3(race.GetFloat("portrait offset x"), race.GetFloat("portrait offset y"), race.GetFloat("portrait offset z"));
        var target = Vector3.Transform(head, l.World) + Vector3.TransformNormal(offset, l.World);
        float yaw = (serial % 500 / 1000f - 0.25f) * MathF.PI, pitch = (serial % 252 / 1000f - 0.083f) * MathF.PI;
        var local = Vector3.Transform(new Vector3(0, 0, race.GetFloat("portrait distance", 10)), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch));
        var direction = Vector3.TransformNormal(local, l.World) / Math.Max(scale, 1e-3f);
        return (target + direction, target);
    }

    void DisposePortraits()
    {
        portraitColour?.Dispose();
        portraitDepth?.Dispose();
        (portraitColour, portraitDepth, portraitSampled) = (null, null, null);
        DisposePortraitHdr();
    }
}
