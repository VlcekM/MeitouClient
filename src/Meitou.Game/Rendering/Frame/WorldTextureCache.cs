using System.Buffers;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Profiling;
using Silk.NET.Vulkan;
using GpuTexture = Meitou.Game.Rendering.Gpu.GpuTexture;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// A texture of the world view's objects and foliage: not resident until its file is decoded (in the background) and uploaded. Reading
/// <see cref="Key"/> counts as using it (the draw code reads it every frame it draws with the texture), and reading it after
/// the cache unloaded the texture starts loading it again.
/// </summary>
public sealed class WorldTexture
{
    /// <summary>
    /// The texture's number in its cache (1-based, never reused): what <see cref="Key"/> returns while resident.
    /// </summary>
    internal uint Number;

    /// <summary>
    /// 0 until resident, then the texture's number in its cache, which <see cref="WorldTextureCache.Index"/> turns into a bindless index. Counts as
    /// use.
    /// </summary>
    public uint Key
    {
        get
        {
            Touch();
            return Native is null ? 0 : Number;
        }
    }

    /// <summary>
    /// Marks the texture used now, and starts loading it again when the cache had unloaded it.
    /// </summary>
    void Touch()
    {
        LastUsed = Environment.TickCount64;
        if (State == Residency.Unloaded) Owner?.Reload(this);
    }

    /// <summary>
    /// Normal map stored with X in alpha and Y in green (the model viewer's heuristic, docs/viewer.md).
    /// </summary>
    public bool Swizzled;

    /// <summary>
    /// The background decode of the file; null when none is running.
    /// </summary>
    internal Task<TextureData?>? Pending;

    /// <summary>
    /// Sampled with a transparent clamp-to-border edge instead of repeating.
    /// </summary>
    internal bool Border;

    /// <summary>
    /// The cache the texture belongs to.
    /// </summary>
    internal WorldTextureCache? Owner;

    /// <summary>
    /// The name the texture was asked for, as the material gives it.
    /// </summary>
    internal string Name = "";

    /// <summary>
    /// The file found for <see cref="Name"/>; null when it is missing.
    /// </summary>
    internal string? Path;

    /// <summary>
    /// When something last read <see cref="Key"/>, <see cref="Environment.TickCount64"/>.
    /// </summary>
    internal long LastUsed;

    /// <summary>
    /// The video memory the resident image takes, in bytes (0 when not resident).
    /// </summary>
    internal long Bytes;

    /// <summary>
    /// The file size counted as in flight while the texture loads (<see cref="WorldTextureCache.MayStart"/>), 0 when not counted.
    /// </summary>
    internal long InFlight;

    /// <summary>
    /// The texture was resident once (a later load is a reload).
    /// </summary>
    internal bool EverResident;

    /// <summary>
    /// Mip streaming (<see cref="MipStreaming"/>): how near the texture is used, as distance over the world length of a texture-coordinate unit,
    /// smallest over its users (infinity: unknown); set by the owner of the cache before each <see cref="WorldTextureCache.Rebalance"/>.
    /// </summary>
    public float Need = float.PositiveInfinity;

    /// <summary>
    /// Never loaded with fewer mips than the quality setting's (the distant towns' atlas, whose coordinates are not the mesh's).
    /// </summary>
    public bool KeepAllMips;

    /// <summary>
    /// Top levels the loaded image lacks against its file (the quality setting's and the streaming's), the quality's own share, and the file's top
    /// size (before either).
    /// </summary>
    internal int Dropped, QualityDrop, FileWidth, FileHeight;

    /// <summary>
    /// The <see cref="TextureQuality.Level"/> the image was asked at (-1: never loaded); <see cref="WorldTextureCache.Requalify"/> reloads it when
    /// the level moves.
    /// </summary>
    internal int QualityLevel = -1;

    /// <summary>
    /// A replacement image (another number of top levels) is loading while this one is still drawn.
    /// </summary>
    internal bool Replacing;

    /// <summary>
    /// When the image started being finer than needed, <see cref="Environment.TickCount64"/> (0: it is not).
    /// </summary>
    internal long TooFineSince;

    /// <summary>
    /// The file could not be loaded with the asked number of dropped levels (a format that keeps its top mips): do not ask again.
    /// </summary>
    internal bool CannotDrop;

    /// <summary>
    /// The replacement being loaded is a finer image (something came near): counted in <see cref="WorldTextureCache.Refining"/>.
    /// </summary>
    internal bool Refine;

    /// <summary>
    /// The image was loaded lean (<see cref="WorldTextureCache.LeanSize"/>) under the guard's pressure: <see cref="WorldTextureCache.Rebalance"/>
    /// refines it once the pressure ends, whether or not its need is known.
    /// </summary>
    internal bool Lean;

    /// <summary>
    /// The smallest <see cref="Need"/> offered since the last <see cref="WorldTextureCache.CommitNeeds"/>.
    /// </summary>
    internal float NeedPass = float.PositiveInfinity;

    /// <summary>
    /// A user of the texture at <paramref name="need"/> (distance over the world length of a texture-coordinate unit): this pass's smallest counts.
    /// </summary>
    public void Offer(float need) { if (need < NeedPass) NeedPass = need; }

    /// <summary>
    /// A user that just appeared (a new instance resolved): the need drops at once, before the pass that would find it.
    /// </summary>
    public void OfferNow(float need)
    {
        if (need < Need) Need = need;
        if (need < NeedPass) NeedPass = need;
    }

    /// <summary>
    /// Where the texture is: loading, resident, unloaded (comes back when used) or missing.
    /// </summary>
    internal Residency State;

    /// <summary>
    /// The image (null until resident), the levels and the swizzle it is sampled with.
    /// </summary>
    internal GpuTexture? Native;

    /// <summary>
    /// The <see cref="TextureShare"/> entry <see cref="Native"/> is, when the cache shares its images.
    /// </summary>
    internal TextureShare.Image? Shared;

    /// <summary>
    /// The image's view: the levels sampled.
    /// </summary>
    internal int ViewLevels;

    /// <summary>
    /// The image's view: the component swizzle sampled with.
    /// </summary>
    internal GpuSwizzle ViewSwizzle;

    /// <summary>
    /// The bindless entries for the last two LOD biases (the upscaler's, and 0 where a pass runs without it).
    /// </summary>
    internal float BiasA, BiasB;

    /// <summary>
    /// The <see cref="SamplerCache.Generation"/> its entries were made under (the anisotropic filtering setting).
    /// </summary>
    internal int SamplerGeneration;

    /// <summary>
    /// The bindless indices for the two LOD biases <see cref="BiasA"/> and <see cref="BiasB"/>.
    /// </summary>
    internal uint IndexA, IndexB;

    /// <summary>
    /// Whether <see cref="IndexA"/> and <see cref="IndexB"/> are held.
    /// </summary>
    internal bool HasA, HasB;

    /// <summary>
    /// A texture's residency: loading (a decode or upload is running), resident, unloaded (by the cache; comes back when used) or missing (no file,
    /// or none that decodes).
    /// </summary>
    internal enum Residency
    {
        /// <summary>
        /// The file is being decoded or its image uploaded.
        /// </summary>
        Loading,

        /// <summary>
        /// The image is in video memory and drawn with.
        /// </summary>
        Resident,

        /// <summary>
        /// Not loaded (unloaded by the cache, or deferred): the next read of <see cref="Key"/> loads it.
        /// </summary>
        Unloaded,

        /// <summary>
        /// No file was found for the name, or the file could not be decoded.
        /// </summary>
        Missing,
    }
}

/// <summary>
/// GpuTexture cache for the world view: files are decoded on worker threads and uploaded on the render thread by <see cref="Pump"/> into native
/// textures (through the <see cref="Uploader"/>), so loading a town's few hundred textures uses every core and never stalls a frame for long. Same
/// lookup, sampling and swizzle detection as <see cref="Renderer"/>, and the images, levels, views and samplers the GL textures had through VkGl
/// (docs/renderer-native.md 8, phase 8 stage 1). Textures nobody has read the key of for <see cref="IdleSeconds"/> are deleted (<see cref="Trim"/>),
/// earlier when the cache, with the caches it shares images with (<see cref="TextureShare"/>), holds more than <see cref="HighWaterMb"/>: the least
/// recently used go first, down to three quarters of it. A deleted texture comes back by itself (decode and upload again) the next time something
/// asks for its key.
/// </summary>
public sealed unsafe class WorldTextureCache : IDisposable
{
    #region Cache state

    /// <summary>
    /// The textures by name (case-insensitive).
    /// </summary>
    readonly Dictionary<string, WorldTexture> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The textures by number (<see cref="WorldTexture.Number"/> - 1).
    /// </summary>
    readonly List<WorldTexture> _byNumber = [];

    /// <summary>
    /// The textures whose decode is running, in the order they were started.
    /// </summary>
    readonly List<WorldTexture> _pending = [];

    /// <summary>
    /// The device the images are made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// Where the texture files are found.
    /// </summary>
    readonly AssetLocator _assets;

    /// <summary>
    /// The images' owner in their allocation names (the F12 VRAM pie groups by it).
    /// </summary>
    readonly string _allocationName;

    /// <summary>
    /// When <see cref="Trim"/> last ran, <see cref="Environment.TickCount64"/> (it runs at most once a second).
    /// </summary>
    long _lastTrim;

    /// <summary>
    /// An empty cache of the textures <paramref name="assets"/> finds, made on <paramref name="gpu"/>.
    /// </summary>
    /// <param name="name">The images' allocation name, by owner ("object textures", "foliage textures").</param>
    public WorldTextureCache(IGpuContext gpu, AssetLocator assets, string name)
    {
        this._gpu = gpu;
        this._assets = assets;
        _allocationName = name;
        _governor = new MipGovernor(this, assets);
    }

    /// <summary>
    /// The cache's warnings (missing files, failed decodes), for the host to show.
    /// </summary>
    public List<string> Messages { get; } = [];

    /// <summary>
    /// The textures still loading, plus one while uploads are queued.
    /// </summary>
    public int PendingCount => _pending.Count + (_steps.Count > 0 ? 1 : 0);

    /// <summary>
    /// <see cref="PendingCount"/> without the coarser replacements, what a view waits for: a coarsening's finer image is drawn until it lands, so
    /// nothing is missing. Over the cache's mark the governor coarsens a few textures at a time, and a town's loading screen waited 10 s for hundreds
    /// of them.
    /// </summary>
    public int AwaitedCount => _pending.Count(t => !t.Replacing || t.Refine) + _uploadsAwaited;

    /// <summary>
    /// Queued uploads a view waits for (counted in <see cref="AwaitedCount"/>).
    /// </summary>
    int _uploadsAwaited;

    /// <summary>
    /// Recently used textures whose initial load is still deferred (e.g. by memory pressure). Includes the once-per-second range marking; unused
    /// deferred foliage textures do not hold up a scene.
    /// </summary>
    public int UnresidentUsedCount
    {
        get
        {
            long now = Environment.TickCount64;
            return _cache.Values.Count(t => t.State == WorldTexture.Residency.Unloaded && now - t.LastUsed <= 2000);
        }
    }

    /// <summary>
    /// GPU memory of the resident textures (all levels, RGBA8) and how many there are.
    /// </summary>
    public long ResidentBytes
    {
        get => _residentBytes;
        private set
        {
            if (Share is { } share) share.ResidentBytes += value - _residentBytes;
            _residentBytes = value;
        }
    }

    /// <summary>
    /// The backing field of <see cref="ResidentBytes"/>.
    /// </summary>
    long _residentBytes;

    /// <summary>
    /// How many textures are resident.
    /// </summary>
    public int ResidentCount { get; private set; }

    /// <summary>
    /// Changes whenever a texture's image comes or goes (a load, a swap, an unload), so its key and bindless index may differ: what a user that keeps
    /// them checks.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Textures unloaded so far.
    /// </summary>
    public int Unloads { get; private set; }

    /// <summary>
    /// Unloaded textures loaded again so far.
    /// </summary>
    public int Reloads { get; private set; }

    /// <summary>
    /// Mip streaming (<see cref="MipStreaming"/>, <see cref="Rebalance"/>): what the owner says about the camera.
    /// </summary>
    public MipStreaming Mips { get; } = new();

    /// <summary>
    /// Resident images replaced by another (<see cref="BeginSwap"/>): one with more levels (finer, as something came near) or fewer (what nothing
    /// near needed).
    /// </summary>
    public int Swaps { get; private set; }

    /// <summary>
    /// The replacements mip streaming and a texture quality change make (<see cref="Rebalance"/>, <see cref="Pump"/>) and their counts.
    /// </summary>
    readonly MipGovernor _governor;

    /// <summary>
    /// Resident textures waiting for a finer image (<see cref="MipGovernor.Refining"/>).
    /// </summary>
    public int Refining => _governor.Refining;

    /// <summary>
    /// Seconds a texture must have been finer than needed before it is replaced by a coarser image (<see cref="MipGovernor.CoarsenAfterSeconds"/>).
    /// </summary>
    public double CoarsenAfterSeconds { get => _governor.CoarsenAfterSeconds; set => _governor.CoarsenAfterSeconds = value; }

    /// <summary>
    /// Coarser replacements loading at once (<see cref="MipGovernor.MaxCoarsens"/>).
    /// </summary>
    public int MaxCoarsens { get => _governor.MaxCoarsens; set => _governor.MaxCoarsens = value; }

    /// <summary>
    /// Every texture of the cache, for <see cref="MipGovernor"/> (a value collection: its enumerator allocates nothing).
    /// </summary>
    internal Dictionary<string, WorldTexture>.ValueCollection Textures => _cache.Values;

    /// <summary>
    /// Unused for this long: unloaded.
    /// </summary>
    public double IdleSeconds { get; set; } = 20;

    /// <summary>
    /// Above this the least recently used textures that were unused for <see cref="PressureIdleSeconds"/> go too. With a <see cref="Share"/> the mark
    /// is for all the sharing caches together (<see cref="TextureShare.ResidentBytes"/>), not for each.
    /// </summary>
    public double HighWaterMb { get; set; } = TextureQuality.MemoryBudgetMb(TextureQuality.Level);

    /// <summary>
    /// Unused for this long, a texture goes when the cache is over its mark.
    /// </summary>
    public double PressureIdleSeconds { get; set; } = 8;

    /// <summary>
    /// The high-water mark as a share of the driver's video memory budget: the mark is the smaller of <see cref="HighWaterMb"/> and this share (the
    /// default share is what the fixed mark was on the 11.4 GB card it was tuned on), and three quarters of it under the guard's pressure.
    /// </summary>
    public double HighWaterShare { get; set; } = TextureQuality.MemoryBudgetMb(0) / 11453.0;

    /// <summary>
    /// The mark in effect (MB).
    /// </summary>
    public double MarkMb => EffectiveMark(HighWaterMb, HighWaterShare, Guard, Areas);

    /// <summary>
    /// Places kept loaded ("Keep textures at characters": the eye's and each merged kept point's, docs/render-objects.md "Kept points"): the mark is
    /// this many times larger, its share of the memory budget at most <see cref="MaxAreaShare"/> (never below the unscaled share).
    /// </summary>
    public int Areas { get; set; } = 1;

    /// <summary>
    /// The largest share of the driver's memory budget a mark scaled by <c>Areas</c> may take.
    /// </summary>
    internal const double MaxAreaShare = 0.5;

    /// <summary>
    /// The mark (MB) for <paramref name="highWaterMb"/> and <paramref name="share"/> of the guard's budget, scaled by <paramref name="areas"/> (the
    /// share to at most <paramref name="maxShare"/>): the smaller of the two, and three quarters of it under pressure. Infinite marks stay infinite;
    /// without a known budget only the scaled mark counts.
    /// </summary>
    internal static double EffectiveMark(double highWaterMb, double share, VramGuard? guard, int areas = 1, double maxShare = MaxAreaShare)
    {
        if (double.IsInfinity(highWaterMb) || highWaterMb >= double.MaxValue) return highWaterMb;
        areas = Math.Max(areas, 1);
        highWaterMb *= areas;
        share = Math.Max(share, Math.Min(share * areas, maxShare));
        if (guard is not { BudgetBytes: > 0 } g) return highWaterMb;
        return Math.Min(highWaterMb, g.BudgetBytes / 1048576.0 * share) * (g.Pressure ? 0.75 : 1);
    }

    /// <summary>
    /// The memory-pressure guard (<see cref="VramGuard"/>): while it is under pressure no texture is refined and a texture something draws loads lean
    /// (at most <see cref="LeanSize"/>, refined when the pressure ends), and textures idle for <see cref="GuardIdleSeconds"/> are evicted, least
    /// recently used first, whatever the high-water mark.
    /// </summary>
    public VramGuard? Guard { get; set; }

    /// <summary>
    /// Images shared with the other caches on the same GPU (<see cref="TextureShare"/>); null: this cache's images are its own.
    /// </summary>
    public TextureShare? Share { get; init; }

    /// <summary>
    /// Unused for this long under the guard's pressure, a texture goes whatever the mark.
    /// </summary>
    public double GuardIdleSeconds { get; set; } = 2;

    /// <summary>
    /// The largest side of a texture loaded under the guard's pressure (a 256² BC3 image is 87 KB): blurry, not missing.
    /// </summary>
    public const int LeanSize = 256;

    /// <summary>
    /// What a lean load is counted as before its file is read (a 256² RGBA8 image with its levels).
    /// </summary>
    const long LeanBytes = 512 << 10;

    #endregion

    #region Reporting

    /// <summary>
    /// The resident textures for the VRAM dump (Ctrl+Shift+F12), largest first: MB, seconds since anything read the key, top levels dropped now,
    /// what mip streaming would drop at the last need (the quality setting's drop included), the need, the file's size and the file.
    /// </summary>
    public void WriteReport(TextWriter w)
    {
        long now = Environment.TickCount64;
        w.WriteLine($"{_allocationName}: {Describe()}; mark {MarkMb:0} MB");
        w.WriteLine("        MB   idle s  dropped  target      need       file size  flags  file");
        foreach (var t in _cache.Values.Where(t => t.State == WorldTexture.Residency.Resident).OrderByDescending(t => t.Bytes))
        {
            int target = t.KeepAllMips || t.FileWidth == 0 || !float.IsFinite(t.Need) ? t.QualityDrop : Math.Max(Mips.Drop(t.FileWidth, t.FileHeight,
                t.Need), t.QualityDrop);
            string flags = (t.KeepAllMips ? "k" : "") + (t.CannotDrop ? "c" : "") + (t.Replacing ? "r" : "")
                + (t.TooFineSince != 0 ? "f" : "") + (t.Shared is { Refs: > 1 } ? "s" : "");
            w.WriteLine($"{t.Bytes / 1048576.0,10:0.000} {(now - t.LastUsed) / 1000.0,8:0.0} {t.Dropped,8} {target,7} {t.Need,9:0.###} "
                + $"{t.FileWidth,9}x{t.FileHeight,-5} {flags,-5}  {t.Name}");
        }
        w.WriteLine("flags: k keeps every level, c cannot drop levels, r a replacement is loading, "
            + "f finer than needed (waiting to coarsen), s the image is shared with another cache");
        w.WriteLine();
    }

    /// <summary>
    /// The statistics line: resident count and memory, unloads, reloads and shares, and what mip streaming saves.
    /// </summary>
    public string Describe()
    {
        string counts = $"{ResidentCount} textures {ResidentBytes / 1048576.0:0} MB ({Unloads} unloaded, {Reloads} reloaded so far"
            + $"{(Shares > 0 ? $", {Shares} taken from another cache" : "")}";
        double lessMb = Math.Max(_governor.UnstreamedBytes - ResidentBytes, 0) / 1048576.0;
        string streaming = Mips.Enabled
            ? $"; mip streaming: {lessMb:0} MB less than every mip, {_governor.Refined} refined, {_governor.Coarsened} coarsened)"
            : ")";
        return counts + streaming;
    }

    /// <summary>
    /// Vanilla references to files the game never shipped (docs/formats/dds.md "Dead references"): not warned about.
    /// </summary>
    static readonly HashSet<string> DeadVanillaTextures = new(StringComparer.OrdinalIgnoreCase)
        { "Geobased_Rockloop_DIF_HI.dds", "Geobased_Rockloop_NML_HI.dds", "Rockloop09_DIF_HI.dds", "Rockloop09_NML_HI.dds" };
    /// <summary>
    /// Missing names already warned about by any cache (each cache resolves the same foliage names): one warning per name per run.
    /// </summary>
    static readonly HashSet<string> WarnedMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether to warn about a missing <paramref name="name"/>: once per file name, and never for the dead vanilla references.
    /// </summary>
    static bool ShouldWarnMissing(string name)
    {
        string file = Path.GetFileName(name.Replace('\\', '/'));
        if (DeadVanillaTextures.Contains(file)) return false;
        lock (WarnedMissing) return WarnedMissing.Add(file);
    }

    #endregion

    #region Lookup

    /// <summary>
    /// The file sizes counted as in flight across the loading textures (<see cref="WorldTexture.InFlight"/>), for the guard's room.
    /// </summary>
    long _inFlight;

    /// <summary>
    /// The texture <paramref name="name"/> (with a border when <paramref name="border"/>), one per name; null for no name.
    /// </summary>
    /// <param name="deferred">The texture is not loaded now: it stays <see cref="WorldTexture.Residency.Unloaded"/> until something reads its
    /// <see cref="WorldTexture.Key"/> (the foliage makes its meshes' textures this way and reads the keys only of groups within their range).</param>
    public WorldTexture? Get(string? name, bool border, bool deferred = false)
    {
        if (name is null) return null;
        string key = border ? name + "|border" : name;
        if (_cache.TryGetValue(key, out var t))
        {
            if (!deferred)
            {
                t.LastUsed = Environment.TickCount64;
                if (t.State == WorldTexture.Residency.Unloaded) Reload(t);
            }
            return t;
        }
        _cache[key] = t = new WorldTexture { Border = border, Owner = this, Name = name, LastUsed = Environment.TickCount64 };
        _byNumber.Add(t);
        t.Number = (uint)_byNumber.Count;

        // The game reduces texture fields to the bare file name (runtime-materials.md); a path still works.
        t.Path = _assets.Find(Path.GetFileName(name.Replace('\\', '/'))) ?? _assets.Find(name);
        if (t.Path is null)
        {
            t.State = WorldTexture.Residency.Missing;
            if (ShouldWarnMissing(name)) Messages.Add($"texture not found: {name}");
            return t;
        }

        // loaded when its key is read (and the guard lets it)
        if (deferred || !MayStart(t, out bool lean))
        {
            t.State = WorldTexture.Residency.Unloaded;
            return t;
        }

        Start(t, lean: lean);
        return t;
    }

    /// <summary>
    /// Whether the guard lets this texture's image be made now: not under pressure, and the file's size (the image is no larger) fits the room left
    /// (<see cref="VramGuard.Allows"/>).
    /// </summary>
    internal bool MayStart(WorldTexture t) => MayStart(t, out _, wanted: false);

    /// <summary>
    /// <see cref="MayStart(WorldTexture)"/> for a texture something reads now: refused the whole image, it may still load <paramref name="lean"/>
    /// (top side at most <see cref="LeanSize"/>) while that fits under the guard's <see cref="VramGuard.AllowsLean"/>, so what is drawn is never left
    /// without one.
    /// </summary>
    bool MayStart(WorldTexture t, out bool lean, bool wanted = true)
    {
        lean = false;
        if (Guard is not { } guard) return true;
        if (guard.Streaming)
        {
            long bytes;
            try
            {
                bytes = new FileInfo(t.Path!).Length;
            }
            catch (IOException)
            {
                bytes = 4 << 20;
            }

            // What is loading is not in the guard's sample yet (the image is made when its decode lands): it counts against the room too.
            if (guard.Allows((ulong)bytes, (ulong)_inFlight))
            {
                (t.InFlight, _inFlight) = (bytes, _inFlight + bytes);
                return true;
            }
        }
        if (!wanted || t.KeepAllMips || !guard.AllowsLean((ulong)LeanBytes, (ulong)_inFlight)) return false;
        (t.InFlight, _inFlight, lean) = (LeanBytes, _inFlight + LeanBytes, true);
        return true;
    }

    /// <summary>
    /// The bindless index (in the 2D float array) of the texture whose <see cref="WorldTexture.Key"/> is <paramref name="key"/>, sampled as the
    /// GL texture was (trilinear, 8x anisotropic, repeat or a transparent border) with the upscaler's LOD bias <paramref name="bias"/>
    /// (<see cref="IGpuContext.LodBias"/>, read where the GL code bound the texture); <paramref name="standIn"/> when it is not resident.
    /// Render thread, in Prepare (it may register an entry).
    /// </summary>
    public uint Index(uint key, float bias, uint standIn)
    {
        if (key == 0 || key > _byNumber.Count) return standIn;
        var t = _byNumber[(int)key - 1];
        if (t.Native is null) return standIn;
        if (t.SamplerGeneration != _gpu.Samplers.Generation)
        {
            // The anisotropic filtering changed: both entries go (after the frames in flight) and are made again with the new sampler.
            if (t.HasA) _gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexA);
            if (t.HasB) _gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexB);
            (t.HasA, t.HasB, t.SamplerGeneration) = (false, false, _gpu.Samplers.Generation);
        }
        if (t.HasA && t.BiasA == bias) return t.IndexA;
        if (t.HasB && t.BiasB == bias)
        {
            (t.BiasA, t.IndexA, t.BiasB, t.IndexB) = (t.BiasB, t.IndexB, t.BiasA, t.IndexA);
            return t.IndexA;
        }

        // A bias not seen yet: a new entry (the older of the two goes after the frames in flight; draws recorded earlier keep theirs).
        if (t.HasB) _gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexB);
        (t.BiasB, t.IndexB, t.HasB) = (t.BiasA, t.IndexA, t.HasA);
        var wrap = t.Border ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
        var sampler = _gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, wrap, wrap,
            TextureWrapMode.Repeat,
            false, DepthFunction.Lequal, transparentBorder: true, anisotropy: 8, integerFormat: false, bias));
        var view = t.Native.View(0, t.ViewLevels, 0, 1, t.ViewSwizzle);
        t.IndexA = _gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, view));
        (t.BiasA, t.HasA) = (bias, true);
        return t.IndexA;
    }

    #endregion

    #region Streaming

    /// <summary>
    /// Loads started while this is set go ahead of the other streaming work (<see cref="BackgroundWork.RunUrgent"/>): the owner sets it while it
    /// touches what a flight's arrival view uses (docs/render-objects.md "Arrival prefetch").
    /// </summary>
    public bool Urgent { get; set; }

    /// <summary>
    /// Starts loading <paramref name="t"/>: adopts a shared image of the same file when one fits, else decodes it on a streaming thread (<paramref
    /// name="replacing"/>: as a replacement while the old image is drawn; <paramref name="lean"/>: at most <see cref="LeanSize"/>).
    /// </summary>
    internal void Start(WorldTexture t, bool replacing = false, bool lean = false)
    {
        string path = t.Path!, name = t.Name;

        // The levels dropped on top of the quality setting's are decided on the worker once the file's size is known, from what the owner says
        // about how near the texture is used now.
        int quality = TextureQuality.LevelsToDrop(Path.GetFileName(path), _assets.Configured.GroupOfFile(path));
        t.QualityDrop = quality;
        t.QualityLevel = TextureQuality.Level;
        float need = t.KeepAllMips ? float.PositiveInfinity : t.Need;
        t.Lean = false;   // an image taken from another cache is not lean
        if (!replacing && Shared(t, need, exact: false) is { } shared)
        {
            Adopt(t, shared);
            return;
        }

        bool enabled = Mips.Enabled;
        float perDistance = Mips.PixelsPerDistance, bias = Mips.Bias;
        int extra = Mips.Extra, leanSize = lean ? LeanSize : 0;
        t.Lean = lean;
        if (replacing) t.Replacing = true;
        else t.State = WorldTexture.Residency.Loading;
        Func<TextureData?> load = () =>
        {
            TextureData data;
            try
            {
                data = Load(path, quality, need, enabled, perDistance, bias, extra, leanSize);
            }
            catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
            {
                lock (Messages) Messages.Add($"texture {name}: {e.Message}");
                return null;
            }

            // The image is made here too: a large one gets memory of its own (given back the moment it is unloaded, so it leaves no hole), and
            // vkAllocateMemory takes from 0.3 ms to 50, which on the render thread would be a frame's.
            try
            {
                data.Image = _gpu.CreateTextureOffThread(DescOf(path, data));
            }
            catch (VulkanException)
            {
                // no memory now: the render thread makes it (or fails) as it did
            }

            return data;
        };
        t.Pending = Urgent ? BackgroundWork.RunUrgent(load) : BackgroundWork.Run(load);
        _pending.Add(t);
    }

    /// <summary>
    /// An image of <paramref name="t"/>'s file another user already holds at the current quality level, with no more top levels dropped than mip
    /// streaming would drop at <paramref name="need"/> (exactly as many when <paramref name="exact"/>), the one with the most dropped of those; null
    /// when none (or no <see cref="Share"/>).
    /// </summary>
    internal TextureShare.Image? Shared(WorldTexture t, float need, bool exact)
    {
        if (Share is null) return null;
        TextureShare.Image? best = null;
        foreach (var e in Share.Of(t.Path!))
        {
            if (e.QualityLevel != TextureQuality.Level || e == t.Shared) continue;
            int target = Math.Max(Mips.Drop(e.FileWidth, e.FileHeight, need), t.QualityDrop);
            if (exact ? e.Dropped == target : e.Dropped <= target && (best is null || e.Dropped > best.Dropped))
                best = e;
        }
        return best;
    }

    /// <summary>
    /// <paramref name="t"/> draws the shared image <paramref name="e"/> (in place of the one it has, if any): resident at once, nothing loaded.
    /// </summary>
    internal void Adopt(WorldTexture t, TextureShare.Image e, bool replaced = false)
    {
        e.Refs++;
        BeginSwap(t, e.Dropped, e.Droppable, e.FileWidth, e.FileHeight, replaced);
        (t.Native, t.Shared, t.ViewLevels, t.ViewSwizzle, t.Swizzled) = (e.Texture, e, e.ViewLevels, e.ViewSwizzle, e.Swizzled);
        Resident(t, e.Bytes);
        Shares++;
    }

    /// <summary>
    /// Images taken from another cache instead of loaded (<see cref="Share"/>).
    /// </summary>
    public int Shares { get; private set; }

    /// <summary>
    /// A loaded image becomes <paramref name="t"/>'s: when another user already holds the same file at the same levels (both loaded it at once), that
    /// one is taken and the new one goes; else the new one is offered to the others.
    /// </summary>
    void Publish(WorldTexture t, GpuTexture texture, TextureData data, int viewLevels, GpuSwizzle swizzle, long bytes)
    {
        if (Share?.Of(t.Path!).FirstOrDefault(o => o != t.Shared && o.QualityLevel == t.QualityLevel
            && o.Dropped == data.Dropped && o.ViewLevels == viewLevels) is { } same)
        {
            texture.Dispose();
            Adopt(t, same, replaced: t.Replacing);
            return;
        }
        BeginSwap(t, data.Dropped, data.Droppable, data.FileWidth, data.FileHeight, replaced: t.Replacing);
        if (Share is not null)
        {
            t.Shared = new TextureShare.Image
            {
                Path = t.Path!,
                Texture = texture,
                ViewLevels = viewLevels,
                ViewSwizzle = swizzle,
                Swizzled = data.Swizzled,
                Bytes = bytes,
                Dropped = data.Dropped,
                QualityLevel = t.QualityLevel,
                FileWidth = data.FileWidth,
                FileHeight = data.FileHeight,
                Droppable = data.Droppable,
            };
            Share.Add(t.Shared);
        }
        (t.Native, t.ViewLevels, t.ViewSwizzle) = (texture, viewLevels, swizzle);
        Resident(t, bytes);
    }

    /// <summary>
    /// Loads an unloaded texture again (a read of its key), when the guard lets it start.
    /// </summary>
    internal void Reload(WorldTexture t)
    {
        if (t.State != WorldTexture.Residency.Unloaded) return;
        if (!MayStart(t, out bool lean)) return;
        if (t.EverResident) Reloads++;
        Start(t, lean: lean);
    }

    /// <summary>
    /// Uploads finished decodes (all of them, waiting, when <paramref name="wait"/>; else steps until <paramref name="budgetMs"/> passes, at most
    /// <paramref name="max"/> textures started).
    /// </summary>
    public void Pump(bool wait, int max = 16, double budgetMs = 1.5)
    {
        int started = 0, ranSteps = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (!wait) _governor.Requalify();
        while (true)
        {
            if (_steps.Count > 0)
            {
                // The first step always runs; a later one waits for the next call when steps of its kind have cost more than what is left plus
                // StepCosts.Slack.
                if (!wait && (watch.Elapsed.TotalMilliseconds >= budgetMs || ranSteps > 0 && !_stepCosts.Fits(_steps.Peek().Method,
                        watch.Elapsed.TotalMilliseconds, budgetMs))) break;
                var step = _steps.Dequeue();
                var one = System.Diagnostics.Stopwatch.StartNew();
                _gpu.EnsureFrame();
                step();
                double stepMs = one.Elapsed.TotalMilliseconds;
                _stepCosts.Learn(step.Method, stepMs);
                ranSteps++;
                if (stepMs > 3) SpikeLog.Note($"slow texture step {step.Method.Name}", ms: stepMs);
                continue;
            }
            int found = -1;
            for (int i = 0; i < _pending.Count && found < 0; i++)
                if (wait || _pending[i].Pending!.IsCompleted) found = i;
            if (found < 0 || (!wait && (started >= max || watch.Elapsed.TotalMilliseconds >= budgetMs))) break;
            var t = _pending[found];
            var data = t.Pending!.Result;
            if (data is not null) QueueUpload(t, data);
            else if (t.Replacing)
            {
                // the old image stays
                if (t.Refine)
                {
                    t.Refine = false;
                    _governor.Refining--;
                }
                t.Replacing = false;
                t.CannotDrop = true;
                Landed(t);
            }
            else
            {
                t.State = WorldTexture.Residency.Missing;
                Landed(t);
            }

            t.Pending = null;
            _pending.RemoveAt(found);
            started++;
        }
        if (!wait)
        {
            var trim = System.Diagnostics.Stopwatch.StartNew();
            Trim();
            if (trim.Elapsed.TotalMilliseconds > 3)
                SpikeLog.Note("slow texture trim", ms: trim.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Deletes textures that have been idle too long, or the least recently used ones while the cache is over its high-water mark. At most once a
    /// second.
    /// </summary>
    void Trim()
    {
        long now = Environment.TickCount64;
        if (now - _lastTrim < 1000) return;
        _lastTrim = now;
        bool guarded = Guard?.Pressure ?? false;
        long held = Share?.ResidentBytes ?? ResidentBytes;
        bool pressure = held > MarkMb * 1048576;
        List<WorldTexture>? victims = null;
        foreach (var t in _cache.Values)
        {
            if (t.State != WorldTexture.Residency.Resident || t.Replacing || t.Pending is not null) continue;
            double idle = (now - t.LastUsed) / 1000.0;
            if (idle > IdleSeconds || pressure && idle > PressureIdleSeconds || guarded && idle > GuardIdleSeconds)
                (victims ??= []).Add(t);
        }
        if (victims is null) return;
        victims.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        long lowWater = (long)(MarkMb * 1048576 * 0.75);
        foreach (var t in victims.Take(guarded ? 120 : 40))
        {
            bool old = (now - t.LastUsed) / 1000.0 > IdleSeconds;
            if (!old && !guarded && held <= lowWater) break;
            long before = ResidentBytes;
            Unload(t);
            held -= before - ResidentBytes;
        }
    }

    /// <summary>
    /// Takes what the users offered since the last call as the textures' <see cref="WorldTexture.Need"/> (a texture nothing offered keeps its last).
    /// </summary>
    public void CommitNeeds()
    {
        foreach (var t in _cache.Values)
        {
            if (float.IsFinite(t.NeedPass)) t.Need = t.NeedPass;
            t.NeedPass = float.PositiveInfinity;
        }
    }

    /// <summary>
    /// Mip streaming's replacements after <see cref="CommitNeeds"/> (<see cref="MipGovernor.Rebalance"/>).
    /// </summary>
    public void Rebalance() => _governor.Rebalance();

    /// <summary>
    /// Releases a resident texture's image and entries and takes its bytes off the count; it comes back when used.
    /// </summary>
    void Unload(WorldTexture t)
    {
        Release(t);
        ResidentBytes -= t.Bytes;
        ResidentCount--;
        Unloads++;
        t.Bytes = 0;
        t.State = WorldTexture.Residency.Unloaded;
    }

    /// <summary>
    /// Frees the texture's bindless entries (after the frames in flight) and its image.
    /// </summary>
    void Release(WorldTexture t)
    {
        if (t.HasA) _gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexA);
        if (t.HasB) _gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexB);
        t.HasA = t.HasB = false;
        if (t.Shared is { } shared)
        {
            Share!.Release(shared);
            t.Shared = null;
        }
        else t.Native?.Dispose();
        t.Native = null;
        Version++;
    }

    #endregion

    #region Loading and decoding

    /// <summary>
    /// The upload steps queued for <see cref="Pump"/>, run in order.
    /// </summary>
    readonly Queue<Action> _steps = new();

    /// <summary>
    /// What each kind of upload step has cost, so <see cref="Pump"/> stops before its budget runs out.
    /// </summary>
    readonly StepCosts _stepCosts = new();

    /// <summary>
    /// The most bytes one upload step copies (512 KB): a larger level is split into slabs of whole rows.
    /// </summary>
    const int SlabBytes = 512 << 10;

    /// <summary>
    /// Reads and decodes the file at <paramref name="path"/>: DDS textures in BC1, BC2, BC3, BC4 or BC5 with a full mip chain go to the GPU as they
    /// are stored (BC4 through a swizzle that shows it as grey); the rest is decoded to RGBA8 on the CPU.
    /// </summary>
    /// <param name="leanSize">0, or the largest top side to keep (a lean load under the guard's pressure).</param>
    static TextureData Load(string path, int dropMips, float need, bool streaming, float perDistance, float bias, int extra, int leanSize = 0)
    {
        using var file = File.OpenHandle(Content.GameFiles.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        Span<byte> magic = stackalloc byte[4];
        if (RandomAccess.Read(file, magic, 0) == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic) == DdsReader.Magic)
        {
            // GpuTexture quality: the game drops top mips as it loads (docs/formats/settings.md); mip streaming drops more where nothing near uses
            // them. Only the mips that are kept are read: a dropped top level is three quarters of the file, and was read, held and thrown away
            // (docs/renderer-native.md 8.14). The file's bytes are pooled (docs/engine.md, pooled buffers): an RGBA8 decode returns them here, a
            // stored upload after its last step.
            var dds = DdsReader.ReadKept(file, w => TextureQuality.DropTopMips(w, Math.Max(Math.Max(dropMips, MipStreaming.Drop(w.Width, w.Height,
                need, streaming, perDistance, bias, extra)),
                LevelsAbove(w.Width, w.Height, leanSize))), out var whole, ArrayPool<byte>.Shared);
            bool handedOver = false;
            try
            {
                int dropped = whole.MipCount - dds.MipCount;
                bool droppable = TextureQuality.DropTopMips(whole, 1).MipCount < whole.MipCount;
                if (CanUploadCompressed(dds))
                {
                    // Only the level the swizzle test looks at is decoded on the CPU.
                    int level = Math.Max(Array.FindIndex(dds.Surfaces.ToArray(), s => s.Width <= 256 && s.Height <= 256), 0);
                    var compressed = new TextureData(new LoadedTexture([DdsDecoder.Decode(dds, 0, level)], null), dds, dropped, whole.Width,
                        whole.Height, droppable);
                    handedOver = true;
                    return compressed;
                }
                return new TextureData(TextureLoader.FromDds(dds) with { Source = null }, null, dropped, whole.Width, whole.Height, droppable);
            }
            finally
            {
                if (!handedOver) ArrayPool<byte>.Shared.Return(dds.Data);
            }
        }
        return new TextureData(new LoadedTexture([TextureLoader.LoadImage(File.ReadAllBytes(Content.GameFiles.Resolve(path)))], null), null);
    }

    /// <summary>
    /// Top levels to drop for the larger side to be at most <paramref name="size"/> (0: none).
    /// </summary>
    static int LevelsAbove(int width, int height, int size)
    {
        int n = 0;
        if (size > 0) while (Math.Max(width, height) >> n > size) n++;
        return n;
    }

    /// <summary>
    /// Whether a DDS file's blocks can go to the GPU as stored: BC1-BC5 or BC7, one 2D image, a full mip chain down to 1 x 1.
    /// </summary>
    static bool CanUploadCompressed(DdsFile dds) =>
        dds.Format is DdsFormat.Bc1 or DdsFormat.Bc2 or DdsFormat.Bc3 or DdsFormat.Bc4 or DdsFormat.Bc5 or DdsFormat.Bc7
        && !dds.IsCubemap && !dds.IsVolume && dds.ImageCount == 1 && dds.Width > 0 && dds.Height > 0
        && dds.MipCount == 1 + (int)Math.Log2(Math.Max(dds.Width, dds.Height)) && dds.Surfaces.Count == dds.MipCount;

    /// <summary>
    /// The number of levels down to 1 × 1 (the chain VkGl allocated for a texture's level 0).
    /// </summary>
    static int FullChain(int w, int h) => 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(w, h), 1)));

    /// <summary>
    /// The image a decoded file is uploaded into: a stored (S3TC) one with its format and full chain; an RGBA8 one with the full chain, its levels
    /// made by blits when the file's do not reach 1 × 1. Named "owner | file" (the VRAM pie groups by the owner, the VRAM dump lists the file). Any
    /// thread.
    /// </summary>
    TextureDesc DescOf(string path, TextureData data)
    {
        string name = $"{_allocationName} | {Path.GetFileName(path)}";
        if (data.Compressed is { } dds)
        {
            return new TextureDesc(FormatOf(dds.Format), dds.Width, dds.Height, FullChain(dds.Width, dds.Height),
                    Use: TextureUse.Sampled | TextureUse.TransferDst, Name: name, Streamed: true);
        }

        var levels = data.Rgba.Levels;
        var top = levels[0];
        return new TextureDesc(InternalFormat.Rgba8, top.Width, top.Height, FullChain(top.Width, top.Height),
            Use: TextureUse.Sampled | TextureUse.TransferDst | (GeneratesMips(data.Rgba) ? TextureUse.TransferSrc : 0), Name: name, Streamed: true);
    }

    /// <summary>
    /// The RGBA8 levels do not reach 1 × 1: the chain is made from level 0 by blits.
    /// </summary>
    static bool GeneratesMips(LoadedTexture tex) => tex.Levels.Count == 1 || tex.Levels[^1].Width > 1 || tex.Levels[^1].Height > 1;

    /// <summary>
    /// The block-compressed format of a DDS format (BC3 for anything not listed).
    /// </summary>
    static InternalFormat FormatOf(DdsFormat format) => format switch
    {
        DdsFormat.Bc1 => InternalFormat.CompressedRgbaS3TcDxt1Ext,
        DdsFormat.Bc2 => InternalFormat.CompressedRgbaS3TcDxt3Ext,
        DdsFormat.Bc4 => InternalFormat.CompressedRedRgtc1,
        DdsFormat.Bc5 => InternalFormat.CompressedRgRgtc2,
        DdsFormat.Bc7 => InternalFormat.CompressedRgbaBptcUnorm,
        _ => InternalFormat.CompressedRgbaS3TcDxt5Ext,
    };

    /// <summary>
    /// The image for <paramref name="t"/>'s upload in GENERAL layout (the transition in the frame's upload commands, before the copies): the one the
    /// worker made (<see cref="IGpuContext.CreateTextureOffThread"/>), else a new one.
    /// </summary>
    GpuTexture Create(WorldTexture t, TextureData data)
    {
        if (data.Image is { } image)
        {
            data.Image = null;
            _gpu.Frame.PreFrame.InitTexture(image);
            return image;
        }
        return _gpu.Frame.PreFrame.CreateTexture(DescOf(t.Path!, data));
    }

    /// <summary>
    /// A stored (S3TC) texture's upload: the image is made with its full chain, each level is filled whole (up to about 512 KB) or in slabs of
    /// whole block rows of about 512 KB, nothing copied twice. Same sampling state as the RGBA8 path; the mip chain is the file's own.
    /// </summary>
    void QueueCompressedUpload(WorldTexture t, TextureData data)
    {
        var dds = data.Compressed!;
        t.Swizzled = data.Swizzled;
        int blockBytes = BlockCompression.BlockBytes(dds.Format);
        GpuTexture? texture = null;
        _steps.Enqueue(() => texture = Create(t, data));
        foreach (var surface in dds.Surfaces)
        {
            var s = surface;
            int blocksPerRow = (s.Width + 3) / 4, blockRows = (s.Height + 3) / 4, rowBytes = blocksPerRow * blockBytes;
            int length = Math.Min(s.Length, rowBytes * blockRows);
            int rowsPerSlab = length <= SlabBytes ? blockRows : Math.Max(1, SlabBytes / rowBytes);
            for (int row = 0; row < blockRows; row += rowsPerSlab)
            {
                int r0 = row, n = Math.Min(rowsPerSlab, blockRows - row);
                _steps.Enqueue(() =>
                {
                    uint height = (uint)Math.Min(n * 4, s.Height - r0 * 4);
                    int bytes = Math.Min(n * rowBytes, length - r0 * rowBytes);
                    using var batch = _gpu.Uploads.Begin();
                    batch.Write(texture!, s.Level, 0, new PixelRect(0, r0 * 4, (uint)s.Width, height),
                        dds.Data.AsSpan(s.Offset + r0 * rowBytes, bytes));
                });
            }
        }
        _steps.Enqueue(() =>
        {
            // BC4 is one channel; the decoder (and so the RGBA8 path) shows it as grey with alpha 1.
            Publish(t, texture!, data, Math.Min(dds.MipCount, texture!.Desc.Levels), dds.Format == DdsFormat.Bc4 ? GpuSwizzle.RrrOne : default,
                dds.Surfaces.Sum(s => (long)s.Length));
            ArrayPool<byte>.Shared.Return(dds.Data);   // rented by Load; every slab above has been copied to staging
        });
    }

    /// <summary>
    /// A texture's upload as steps of about 512 KB: the image with its full chain, the levels filled in slabs of rows, then the mipmaps. Without
    /// a chain down to 1 × 1 the levels are made from level 0 by linear blits, as VkGl's <c>GenerateMipmap</c> made them (so only level 0 is
    /// uploaded: the blits overwrite the others).
    /// </summary>
    void QueueUpload(WorldTexture t, LoadedTexture tex, bool swizzled, TextureData data)
    {
        t.Swizzled = swizzled;
        var top = tex.Levels[0];
        int chain = FullChain(top.Width, top.Height);
        bool generate = GeneratesMips(tex);
        int uploaded = generate ? 1 : Math.Min(tex.Levels.Count, chain);
        GpuTexture? texture = null;
        _steps.Enqueue(() => texture = Create(t, data));
        for (int level = 0; level < uploaded; level++)
        {
            var img = tex.Levels[level];
            int lv = level, rowBytes = img.Width * 4;
            int rows = Math.Max(1, SlabBytes / rowBytes);
            for (int y = 0; y < img.Height; y += rows)
            {
                int y0 = y, h = Math.Min(rows, img.Height - y);
                _steps.Enqueue(() =>
                {
                    using var batch = _gpu.Uploads.Begin();
                    batch.Write(texture!, lv, 0, new PixelRect(0, y0, (uint)img.Width, (uint)h),
                        img.Pixels.AsSpan(y0 * rowBytes, h * rowBytes));
                });
            }
        }
        _steps.Enqueue(() =>
        {
            long bytes = tex.Levels.Sum(l => (long)l.Width * l.Height * 4);
            if (generate)
            {
                GenerateMipmaps(_gpu, texture!);
                bytes = bytes * 4 / 3;   // the full chain the blits made
            }
            Publish(t, texture!, data, generate ? chain : uploaded, default, bytes);
        });
    }

    /// <summary>
    /// Each level from the one above by a linear blit, in the frame's upload commands after the uploads (VkGl's <c>GenerateMipmap</c>).
    /// </summary>
    internal static void GenerateMipmaps(IGpuContext gpu, GpuTexture texture)
    {
        using var batch = gpu.Uploads.Begin();
        batch.Commands.GenerateMips(texture);
    }

    /// <summary>
    /// A texture left the loading state: its file size is no longer counted as in flight.
    /// </summary>
    void Landed(WorldTexture t)
    {
        _inFlight -= t.InFlight;
        t.InFlight = 0;
    }

    /// <summary>
    /// A loaded image became the texture's: resident from now, counted, and the version moved.
    /// </summary>
    void Resident(WorldTexture t, long bytes)
    {
        Landed(t);
        t.EverResident = true;
        t.Bytes = bytes;
        t.State = WorldTexture.Residency.Resident;
        t.LastUsed = Environment.TickCount64;
        Version++;
        ResidentBytes += bytes;
        ResidentCount++;
    }

    /// <summary>
    /// Queues <paramref name="data"/>'s upload, stored blocks or RGBA8, counting it as awaited unless it is a coarser replacement.
    /// </summary>
    void QueueUpload(WorldTexture t, TextureData data)
    {
        bool awaited = !t.Replacing || t.Refine;   // AwaitedCount
        if (awaited) _uploadsAwaited++;
        if (data.Compressed is not null) QueueCompressedUpload(t, data);
        else QueueUpload(t, data.Rgba, data.Swizzled, data);
        if (awaited) _steps.Enqueue(() => _uploadsAwaited--);
    }

    /// <summary>
    /// The new image is about to replace the one the texture has (a reload with another number of top levels, <see cref="Rebalance"/>): the old one
    /// goes (its bindless entries after the frames in flight, as an unload) and its bytes leave the count; <see cref="Resident"/> adds the new.
    /// Records the levels the new image lacks.
    /// </summary>
    void BeginSwap(WorldTexture t, int dropped, bool droppable, int fileWidth, int fileHeight, bool replaced = false)
    {
        if (t.Native is not null)
        {
            Release(t);
            ResidentBytes -= t.Bytes;
            ResidentCount--;
            t.Bytes = 0;
            if (t.State == WorldTexture.Residency.Resident) Swaps++;
        }
        if (replaced && dropped == t.Dropped) t.CannotDrop = true;   // nothing changed: do not ask again
        t.CannotDrop |= !droppable;
        if (t.Refine)
        {
            t.Refine = false;
            _governor.Refining--;
        }

        t.Replacing = false;
        t.TooFineSince = 0;
        t.Dropped = dropped;
        t.FileWidth = fileWidth;
        t.FileHeight = fileHeight;
    }

    /// <summary>
    /// Whether an RGBA8 normal map looks like a "DXT5 normal" (X in alpha): red, green and blue all near the same mean, below 180.
    /// </summary>
    internal static bool LooksSwizzled(RgbaImage image)
    {
        long r = 0, g = 0, b = 0;
        var p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            r += p[i];
            g += p[i + 1];
            b += p[i + 2];
        }

        long n = Math.Max(p.Length / 4, 1);
        return b / n < 180 && Math.Abs(r - b) / n < 8 && Math.Abs(g - b) / n < 8;
    }

    /// <summary>
    /// Waits for the running decodes (dropping images made for them), then releases every texture's image.
    /// </summary>
    public void Dispose()
    {
        foreach (var t in _pending)
        {
            t.Pending?.Wait();
            if (t.Pending is { IsCompletedSuccessfully: true, Result.Image: { } unused }) unused.Dispose();   // made on the worker, never uploaded
        }
        foreach (var t in _cache.Values) Release(t);
        ResidentBytes = 0;
    }

    #endregion
}

/// <summary>
/// A decoded texture file: the RGBA8 levels (all of them, or for a stored S3TC texture only the one the swizzle test needs), and the DDS when its
/// blocks are uploaded as they are.
/// </summary>
sealed record TextureData(LoadedTexture Rgba, DdsFile? Compressed, int Dropped = 0, int FileWidth = 0, int FileHeight = 0, bool Droppable = false)
{
    /// <summary>
    /// The image the worker made for it (<see cref="IGpuContext.CreateTextureOffThread"/>), until the upload takes it; null: made on the render
    /// thread.
    /// </summary>
    public GpuTexture? Image { get; set; }

    /// <summary>
    /// Whether it looks like a "DXT5 normal" (X in alpha), tested on the worker: the full image can be megabytes.
    /// </summary>
    public bool Swizzled { get; } = WorldTextureCache.LooksSwizzled(
        Rgba.Levels.FirstOrDefault(l => l.Width <= 256 && l.Height <= 256) ?? Rgba.Levels[0]);
}

/// <summary>
/// The images the world's texture caches share (<see cref="WorldTextureCache.Share"/>): a file the objects, the foliage and the characters all draw
/// is in video memory once at a given number of dropped levels (the VRAM dump of 2026-11-01 held about 150 MB of such copies). An image goes when its
/// last user releases it. Each cache still counts the image's bytes as its own. Render thread only.
/// </summary>
public sealed class TextureShare
{
    /// <summary>
    /// One shared image: its file, texture and users, and what it was loaded as.
    /// </summary>
    internal sealed class Image
    {
        /// <summary>
        /// The file the image was loaded from.
        /// </summary>
        public required string Path;

        /// <summary>
        /// The image.
        /// </summary>
        public required GpuTexture Texture;

        /// <summary>
        /// How many textures use the image; it goes when the last releases it.
        /// </summary>
        public int Refs = 1;

        /// <summary>
        /// The image's view levels, the top levels dropped, the quality level it was asked at, and the file's top size.
        /// </summary>
        public int ViewLevels, Dropped, QualityLevel, FileWidth, FileHeight;

        /// <summary>
        /// The swizzle the image is sampled with.
        /// </summary>
        public GpuSwizzle ViewSwizzle;

        /// <summary>
        /// Whether the image is a swizzled normal map, and whether its file can drop levels.
        /// </summary>
        public bool Swizzled, Droppable;

        /// <summary>
        /// The video memory the image takes, in bytes.
        /// </summary>
        public long Bytes;
    }

    /// <summary>
    /// The shared images by file (case-insensitive).
    /// </summary>
    readonly Dictionary<string, List<Image>> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The share of each device's caches.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IGpuContext, TextureShare> Shares = new();

    /// <summary>
    /// The share of the caches on <paramref name="gpu"/>.
    /// </summary>
    public static TextureShare For(IGpuContext gpu) => Shares.GetValue(gpu, _ => new TextureShare());

    /// <summary>
    /// What the sharing caches hold together (each counts a shared image as its own), against which their one high-water mark is kept.
    /// </summary>
    public long ResidentBytes { get; internal set; }

    /// <summary>
    /// The shared images of <paramref name="path"/> (empty when none).
    /// </summary>
    internal IReadOnlyList<Image> Of(string path) => _byPath.TryGetValue(path, out var list) ? list : [];

    /// <summary>
    /// Adds a new image (one user) to the share.
    /// </summary>
    internal void Add(Image e)
    {
        if (!_byPath.TryGetValue(e.Path, out var list)) _byPath[e.Path] = list = [];
        list.Add(e);
    }

    /// <summary>
    /// Drops one user of <paramref name="e"/>: the last one disposes the image and forgets it.
    /// </summary>
    internal void Release(Image e)
    {
        if (--e.Refs > 0) return;
        e.Texture.Dispose();
        if (_byPath.TryGetValue(e.Path, out var list) && list.Remove(e) && list.Count == 0) _byPath.Remove(e.Path);
    }
}