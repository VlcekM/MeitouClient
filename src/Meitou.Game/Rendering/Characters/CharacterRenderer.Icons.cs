using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>An inventory icon wanted: the item's record, its <c>material sid</c> ("" none) and the COLOR_DATA it is dyed with ("" undyed), the ids the game's saved icons are named by.</summary>
public readonly record struct IconKey(string Record, string Material, string Colour = "");

/// <summary>
/// The inventory icons the game would draw from an item's mesh (docs/game/ui-screens.md "Item icons"), made here when the game's saved picture is not at hand. An icon is the item's
/// mesh drawn through the item shader into a texture of its own, from the camera <see cref="ItemIconStyle"/> describes and lit from the camera. Its mesh is read on a worker
/// (<see cref="CharacterContent.IconMeshReady"/>); it is drawn each frame until the textures it uses are resident (they stream in), then kept, and saved to
/// <see cref="IconSaveDirectory"/> as the game saves its pictures, so a later run reads the file instead of drawing it again.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>Icons being drawn at once (a small texture each); the others wait their turn.</summary>
    const int IconsPerFrame = 16;
    /// <summary>Enhanced icons being drawn at once: each holds a supersampled half-float target and its depth (up to 48 MB for a 512 x 512 icon).</summary>
    const int EnhancedIconsPerFrame = 4;
    /// <summary>Icon assets built (their mesh uploaded) in a frame at most.</summary>
    const int IconAssetsPerFrame = 8;
    /// <summary>Frames an icon is drawn waiting for its textures before it is shown with what it has.</summary>
    const int IconGiveUpFrames = 300;

    sealed class IconEntry
    {
        public required IconKey Key;
        public CharacterAsset? Asset;
        public ItemIconStyle Style = null!;
        public GpuTexture? Colour, Depth;
        public SampledTexture Sampled;
        /// <summary>The asset could not be made (no mesh): the cell keeps the name.</summary>
        public bool Failed;
        /// <summary>The textures were all resident when this frame's materials were written.</summary>
        public bool TexturesReady;
        /// <summary>Drawn once with everything resident: it is kept as is.</summary>
        public bool Settled;
        /// <summary>The image holds a picture (published to the UI thread).</summary>
        public volatile bool Drawn;
        public int Frames;
        /// <summary>The material record it is drawn with (the key's, else the item's first): the picture's file is named by it.</summary>
        public string MaterialId = "";
        /// <summary>The supersampled picture an enhanced icon is drawn into before it is resolved into <see cref="Colour"/> (<see cref="EnhancedIcons"/>).</summary>
        public GpuTexture? Large, LargeDepth;
        public SampledTexture LargeSampled;
    }

    /// <summary>Samples each way of an enhanced icon (<see cref="EnhancedIcons"/>): drawn once, so 16 samples a pixel cost nothing that matters.</summary>
    const int IconSupersample = 4;

    /// <summary>The enhanced icons' resolve: supersampled, averaged by coverage, no tone curve or grade (the icon lighting's values are the picture's).</summary>
    static readonly PreviewLook IconLook = new(true, IconSupersample, ToneMapOperator.Clamp, 0, false, false);

    bool enhancedIcons, iconsStale;

    /// <summary>
    /// Icons drawn supersampled (the <c>icons</c> switch's Meitou side) rather than as the game draws them; a change drops the icons made so far, which are drawn again as they are
    /// asked for. Any thread (the drop happens on the render thread's next update).
    /// </summary>
    public bool EnhancedIcons
    {
        get => enhancedIcons;
        set
        {
            if (enhancedIcons == value) return;
            enhancedIcons = value;
            iconsStale = true;
        }
    }

    /// <summary>
    /// Where a finished icon is saved, as <c>&lt;item&gt;.&lt;material&gt;[.&lt;colour&gt;].png</c> (the game's saved pictures' names, docs/game/ui-screens.md "Item icons"); null: not saved.
    /// The picture is the texture as drawn (a power-of-two size, rows top first, alpha kept), read back without waiting for the GPU and written on a worker.
    /// </summary>
    public string? IconSaveDirectory { get; set; }

    readonly List<(IconEntry Entry, IGpuReadback Buffer, long Frame)> iconSaves = [];

    readonly ConcurrentDictionary<IconKey, IconEntry> icons = new();
    readonly ConcurrentQueue<IconEntry> iconRequests = new();
    readonly List<IconEntry> iconsUnsettled = [];
    readonly Dictionary<CharacterAsset, IconEntry> iconByAsset = new(ReferenceEqualityComparer.Instance);
    int iconAssetsBuilt;

    /// <summary>
    /// The drawn icon of <paramref name="key"/> as a texture (drawn upside down, as the renderer's targets are: show it flipped), once it exists; asking starts it being made. Any thread.
    /// </summary>
    public bool TryGetIcon(IconKey key, out (GpuTexture Texture, SampledTexture Sampled) icon)
    {
        if (!icons.TryGetValue(key, out var entry))
        {
            entry = icons.GetOrAdd(key, k => new IconEntry { Key = k });
            iconRequests.Enqueue(entry);
        }
        icon = entry.Drawn ? (entry.Colour!, entry.Sampled) : default;
        return entry.Drawn;
    }

    /// <summary>Icons asked for and not yet final: waiting for their turn or for textures to stream in.</summary>
    public int PendingIcons => iconRequests.Count + iconsUnsettled.Count;

    /// <summary>The icon texture of <paramref name="key"/> if it has been drawn (no request made).</summary>
    public bool HasIcon(IconKey key) => icons.TryGetValue(key, out var e) && e.Drawn;

    /// <summary>
    /// Adds the icons being made to this frame's list (after the characters: they are drawn only by <see cref="DrawIcons"/>, so the world and the shadows skip them), building the
    /// asset of a new one (one a frame). Their node's matrix goes to <paramref name="attach"/> later (<see cref="PoseIcons"/>). Called in <see cref="Update"/>.
    /// </summary>
    void GatherIcons(Vector3 eye, ref int attachCount)
    {
        if (iconsStale)
        {
            iconsStale = false;
            // The queue first: a request made while the icons are dropped stays queued with its entry.
            iconRequests.Clear();
            DisposeIcons();
            iconsUnsettled.Clear();
            iconByAsset.Clear();
        }
        while (iconRequests.TryDequeue(out var requested)) iconsUnsettled.Add(requested);
        iconAssetsBuilt = 0;
        int gathered = 0;
        for (int i = 0; i < iconsUnsettled.Count && gathered < (EnhancedIcons ? EnhancedIconsPerFrame : IconsPerFrame); i++)
        {
            var entry = iconsUnsettled[i];
            if (entry.Asset is null && !entry.Failed)
            {
                // Waiting for its mesh to be read on a worker does not hold up the icons behind it.
                if (iconAssetsBuilt >= IconAssetsPerFrame || content.Database.Find(entry.Key.Record) is { } record && !content.IconMeshReady(record)) continue;
                iconAssetsBuilt++;
                BuildIconAsset(entry);
            }
            if (entry.Failed) { iconsUnsettled.RemoveAt(i--); continue; }
            if (entry.Asset is not { } asset) continue;
            entry.TexturesReady = asset.Parts[0].Material.Tex.All(t => t is null || t.Native is not null || t.State == WorldTexture.Residency.Missing);
            if (liveCount == live.Length) Array.Resize(ref live, liveCount * 2);
            ref var l = ref live[liveCount++];
            l = default;
            l.Asset = asset;
            l.World = Matrix4x4.Identity;
            l.Centre = eye;
            l.Radius = 1;
            l.PortraitOnly = true;
            l.AttachBase = attachCount;
            attachCount += 1;
            gathered++;
        }
    }

    void BuildIconAsset(IconEntry entry)
    {
        var item = content.Database.Find(entry.Key.Record);
        if (item is null) { entry.Failed = true; return; }
        var spec = content.IconMaterialOf(item, entry.Key.Material);
        entry.MaterialId = spec?.StringId ?? "";
        entry.Style = ItemIconStyle.Of(item, spec);
        entry.Asset = content.BuildIcon(item, spec, entry.Key.Colour.Length > 0 ? content.Database.Find(entry.Key.Colour) : null);
        if (entry.Asset is null) { entry.Failed = true; return; }
        iconByAsset[entry.Asset] = entry;
    }

    /// <summary>Sets each icon's node matrix in the attachment table (the part is not skinned: its model matrix is the node's). Called in <see cref="Update"/> after the poses.</summary>
    void PoseIcons()
    {
        for (int i = 0; i < liveCount; i++)
            if (live[i].Asset.IsIcon) attach[live[i].AttachBase] = iconByAsset[live[i].Asset].Style.Matrices().Model;
    }

    /// <summary>Draws the icons gathered this frame, each into its own texture (after <see cref="Update"/>, outside any pass).</summary>
    public void DrawIcons()
    {
        WriteSavedIcons();
        for (int i = 0; i < liveCount; i++)
        {
            if (!live[i].Asset.IsIcon) continue;
            var entry = iconByAsset[live[i].Asset];
            // The final drawing (everything resident) is also read back for its file.
            // An enhanced icon that gave up is drawn plainly from then on (see DrawIcon), so it is not saved among the supersampled ones.
            bool givenUp = entry.Frames > IconGiveUpFrames;
            DrawIcon(entry, i, save: entry.TexturesReady && IconSaveDirectory is not null && entry.MaterialId.Length > 0 && !(EnhancedIcons && givenUp));
            // Shown once its textures were all resident (before that it would show stand-ins), or after a while if they never are (the video memory guard keeps them out).
            if (entry.TexturesReady || ++entry.Frames > IconGiveUpFrames) entry.Drawn = true;
            if (entry.Frames > IconGiveUpFrames && entry.Large is not null)
            {
                entry.Large.Dispose();
                entry.LargeDepth?.Dispose();
                (entry.Large, entry.LargeDepth) = (null, null);
            }
            if (entry.TexturesReady)
            {
                // Final: the depth buffer is only needed while drawing.
                entry.Settled = true;
                iconsUnsettled.Remove(entry);
                entry.Depth?.Dispose();
                entry.Depth = null;
                entry.Large?.Dispose();
                entry.LargeDepth?.Dispose();
                (entry.Large, entry.LargeDepth) = (null, null);
            }
        }
    }

    void DrawIcon(IconEntry entry, int index, bool save)
    {
        var (w, h) = entry.Style.TextureSize;
        if (entry.Colour is null)
        {
            entry.Colour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h, Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.TransferSrc, Name: $"icon {entry.Key.Record}"));
            entry.Sampled = new SampledTexture(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
                TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), entry.Colour.View());
        }
        // Enhanced: drawn into a half-float picture IconSupersample times larger, then resolved into the icon (coverage-weighted, so the edges blend into the transparent background).
        // One that gave up waiting for its textures is drawn plainly: it may wait for good, and its large targets are freed.
        bool large = EnhancedIcons && entry.Frames <= IconGiveUpFrames;
        if (large && entry.Large is null)
            (entry.Large, entry.LargeDepth, entry.LargeSampled) = PreviewResolve.Targets(Gpu, w, h, IconLook, $"icon large {entry.Key.Record}");
        if (!large && entry.Depth is null)
            entry.Depth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h, Use: TextureUse.DepthTarget, Name: $"icon depth {entry.Key.Record}"));
        int s = large ? IconSupersample : 1;
        var cmd = Gpu.BeginNative("item icon");
        var target = large ? PassTargets.Of(entry.Large, entry.LargeDepth) : PassTargets.Of(entry.Colour, entry.Depth);
        cmd.BeginRendering(new RenderingDesc(target.Colour, target.Depth with { Load = LoadOp.Clear, DepthClear = 1f }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        var rect = new PixelRect(0, 0, (uint)(w * s), (uint)(h * s));
        cmd.Clear(true, default, true, 1, rect);
        Gpu.SetPassViewport(new PixelViewport(0, 0, w * s, h * s, 0, 1), rect);
        var (_, view, projection) = entry.Style.Matrices();
        var eye = new Vector3(0, 0, entry.Style.Distance);
        portraitLive = index;
        try { DrawView(new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = Vector3.UnitZ, FogDistance = -1 }, eye, []); }
        finally { portraitLive = -1; }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        if (large)
        {
            Gpu.EndNative(cmd);
            preview ??= new PreviewResolve(Gpu);
            preview.Run(entry.LargeSampled, entry.Colour, IconLook, [new PixelRect(0, 0, (uint)w, (uint)h)]);
            cmd = Gpu.BeginNative("item icon readback");
        }
        if (save)
        {
            var buffer = Gpu.CreateReadback((ulong)(w * h * 4), $"icon readback {entry.Key.Record}");
            cmd.Order();
            cmd.CopyTextureToBuffer(entry.Colour, 0, 0, new PixelRect(0, 0, (uint)w, (uint)h), buffer.Handle, 0);
            cmd.Order();
            iconSaves.Add((entry, buffer, Gpu.Frame.Number));
        }
        Gpu.EndNative(cmd);
    }

    /// <summary>Writes the icons whose read-back frame has completed (render thread; the PNG is encoded and written on a worker).</summary>
    void WriteSavedIcons()
    {
        for (int k = iconSaves.Count - 1; k >= 0; k--)
        {
            var (entry, buffer, frame) = iconSaves[k];
            if (!Gpu.FrameCompleted(frame)) continue;
            iconSaves.RemoveAt(k);
            var (w, h) = entry.Style.TextureSize;
            byte[] pixels = buffer.Read(0, (ulong)(w * h * 4)).ToArray();
            buffer.Dispose();
            string name = $"{entry.Key.Record}.{entry.MaterialId}{(entry.Key.Colour.Length > 0 ? "." + entry.Key.Colour : "")}.png";
            if (IconSaveDirectory is not { } directory) continue;
            string path = Path.Combine(directory, name);
            BackgroundWork.Run(() =>
            {
                try
                {
                    // The texture's rows start at the bottom (as every target's); a PNG's at the top.
                    var rows = new byte[pixels.Length];
                    for (int y = 0; y < h; y++) pixels.AsSpan((h - 1 - y) * w * 4, w * 4).CopyTo(rows.AsSpan(y * w * 4));
                    Directory.CreateDirectory(directory);
                    string temporary = path + ".tmp";
                    Meitou.Data.Textures.PngWriter.Write(temporary, w, h, rows);
                    File.Move(temporary, path, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // not cached: the next run draws it again
                return true;
            });
        }
    }

    void DisposeIcons()
    {
        foreach (var save in iconSaves) save.Buffer.Dispose();
        iconSaves.Clear();
        foreach (var entry in icons.Values)
        {
            entry.Colour?.Dispose();
            entry.Depth?.Dispose();
            entry.Large?.Dispose();
            entry.LargeDepth?.Dispose();
        }
        icons.Clear();
    }
}
