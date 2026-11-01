using System.Buffers;
using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using Meitou.Data.Textures;

using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using GpuTexture = Meitou.Rendering.Gpu.GpuTexture;

using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering;

/// <summary>
/// A texture of the world view's objects and foliage: not resident until its file is decoded (in the background) and uploaded. Reading
/// <see cref="Key"/> counts as using it (the draw code reads it every frame it draws with the texture), and reading it after
/// the cache unloaded the texture starts loading it again.
/// </summary>
public sealed class WorldTexture
{
    /// <summary>The texture's number in its cache (1-based, never reused): what <see cref="Key"/> returns while resident.</summary>
    internal uint Number;

    /// <summary>0 until resident, then the texture's number in its cache, which <see cref="WorldTextureCache.Index"/> turns into a bindless
    /// index. Counts as use.</summary>
    public uint Key
    {
        get
        {
            Touch();
            return Native is null ? 0 : Number;
        }
    }

    void Touch()
    {
        LastUsed = Environment.TickCount64;
        if (State == Residency.Unloaded) Owner?.Reload(this);
    }

    /// <summary>Normal map stored with X in alpha and Y in green (the model viewer's heuristic, docs/viewer.md).</summary>
    public bool Swizzled;
    internal Task<TextureData?>? Pending;
    internal bool Border;
    internal WorldTextureCache? Owner;
    internal string Name = "";
    internal string? Path;
    internal long LastUsed;
    internal long Bytes;
    /// <summary>The file size counted as in flight while the texture loads (<see cref="WorldTextureCache.MayStart"/>), 0 when not counted.</summary>
    internal long InFlight;
    /// <summary>The texture was resident once (a later load is a reload).</summary>
    internal bool EverResident;
    /// <summary>Mip streaming (<see cref="MipStreaming"/>): how near the texture is used, as distance over the world length of a texture-coordinate
    /// unit, smallest over its users (infinity: unknown); set by the owner of the cache before each <see cref="WorldTextureCache.Rebalance"/>.</summary>
    public float Need = float.PositiveInfinity;
    /// <summary>Never loaded with fewer mips than the quality setting's (the distant towns' atlas, whose coordinates are not the mesh's).</summary>
    public bool KeepAllMips;
    /// <summary>Top levels the loaded image lacks against its file (the quality setting's and the streaming's), the quality's own share, and
    /// the file's top size (before either).</summary>
    internal int Dropped, QualityDrop, FileWidth, FileHeight;
    /// <summary>The <see cref="TextureQuality.Level"/> the image was asked at (-1: never loaded); <see cref="WorldTextureCache.Requalify"/> reloads it when the level moves.</summary>
    internal int QualityLevel = -1;
    /// <summary>A replacement image (another number of top levels) is loading while this one is still drawn.</summary>
    internal bool Replacing;
    /// <summary>When the image started being finer than needed, <see cref="Environment.TickCount64"/> (0: it is not).</summary>
    internal long TooFineSince;
    /// <summary>The file could not be loaded with the asked number of dropped levels (a format that keeps its top mips): do not ask again.</summary>
    internal bool CannotDrop;
    /// <summary>The replacement being loaded is a finer image (something came near): counted in <see cref="WorldTextureCache.Refining"/>.</summary>
    internal bool Refine;
    /// <summary>The smallest <see cref="Need"/> offered since the last <see cref="WorldTextureCache.CommitNeeds"/>.</summary>
    internal float NeedPass = float.PositiveInfinity;

    /// <summary>A user of the texture at <paramref name="need"/> (distance over the world length of a texture-coordinate unit): this pass's smallest counts.</summary>
    public void Offer(float need) { if (need < NeedPass) NeedPass = need; }

    /// <summary>A user that just appeared (a new instance resolved): the need drops at once, before the pass that would find it.</summary>
    public void OfferNow(float need) { if (need < Need) Need = need; if (need < NeedPass) NeedPass = need; }
    internal Residency State;
    /// <summary>The image (null until resident), the levels and the swizzle it is sampled with.</summary>
    internal GpuTexture? Native;
    /// <summary>The <see cref="TextureShare"/> entry <see cref="Native"/> is, when the cache shares its images.</summary>
    internal TextureShare.Image? Shared;
    internal int ViewLevels;
    internal GpuSwizzle ViewSwizzle;
    /// <summary>The bindless entries for the last two LOD biases (the upscaler's, and 0 where a pass runs without it).</summary>
    internal float BiasA, BiasB;
    /// <summary>The <see cref="SamplerCache.Generation"/> its entries were made under (the anisotropic filtering setting).</summary>
    internal int SamplerGeneration;
    internal uint IndexA, IndexB;
    internal bool HasA, HasB;

    internal enum Residency { Loading, Resident, Unloaded, Missing }
}

/// <summary>
/// GpuTexture cache for the world view: files are decoded on worker threads and uploaded on the render thread by <see cref="Pump"/> into native
/// textures (through the <see cref="Uploader"/>), so loading a town's few hundred textures uses every core and never stalls a frame for long.
/// Same lookup, sampling and swizzle detection as <see cref="Renderer"/>, and the images, levels, views and samplers the GL textures had
/// through VkGl (docs/renderer-native.md 8, phase 8 stage 1).
/// Textures nobody has read the key of for <see cref="IdleSeconds"/> are deleted (<see cref="Trim"/>), earlier when the cache holds more than
/// <see cref="HighWaterMb"/>: the least recently used go first, down to three quarters of it. A deleted texture comes back by itself
/// (decode and upload again) the next time something asks for its key.
/// </summary>
public sealed unsafe class WorldTextureCache : IDisposable
{
    readonly Dictionary<string, WorldTexture> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly List<WorldTexture> byNumber = [];
    readonly List<WorldTexture> pending = [];
    readonly IGpuContext gpu;
    readonly AssetLocator assets;
    /// <summary>The images' owner in their allocation names (the F12 VRAM pie groups by it).</summary>
    readonly string allocationName;
    long lastTrim;

    /// <param name="name">The images' allocation name, by owner ("object textures", "foliage textures").</param>
    public WorldTextureCache(IGpuContext gpu, AssetLocator assets, string name)
    {
        this.gpu = gpu;
        this.assets = assets;
        allocationName = name;
    }

    public List<string> Messages { get; } = [];
    public int PendingCount => pending.Count + (steps.Count > 0 ? 1 : 0);
    /// <summary>
    /// <see cref="PendingCount"/> without the coarser replacements, what a view waits for: a coarsening's finer image is drawn until it lands, so nothing is
    /// missing. Over the cache's mark the governor coarsens a few textures at a time, and a town's loading screen waited 10 s for hundreds of them.
    /// </summary>
    public int AwaitedCount => pending.Count(t => !t.Replacing || t.Refine) + uploadsAwaited;
    int uploadsAwaited;
    /// <summary>Recently used textures whose initial load is still deferred (e.g. by memory pressure).
    /// Includes the once-per-second range marking; unused deferred foliage textures do not hold up a scene.</summary>
    public int UnresidentUsedCount
    {
        get
        {
            long now = Environment.TickCount64;
            return cache.Values.Count(t => t.State == WorldTexture.Residency.Unloaded && now - t.LastUsed <= 2000);
        }
    }

    /// <summary>GPU memory of the resident textures (all levels, RGBA8) and how many there are.</summary>
    public long ResidentBytes { get; private set; }
    public int ResidentCount { get; private set; }
    /// <summary>Changes whenever a texture's image comes or goes (a load, a swap, an unload), so its key and bindless index may differ: what a user that keeps them checks.</summary>
    public int Version { get; private set; }
    public int Unloads { get; private set; }
    public int Reloads { get; private set; }
    /// <summary>Mip streaming (<see cref="MipStreaming"/>, <see cref="Rebalance"/>): what the owner says about the camera, and the images replaced by
    /// one with more levels (finer, as something came near) or fewer (what nothing near needed).</summary>
    public MipStreaming Mips { get; } = new();
    public int Swaps { get; private set; }
    public int Refined { get; private set; }
    public int Coarsened { get; private set; }
    /// <summary>Resident textures waiting for a finer image (loading, uploading) while their old one is drawn.</summary>
    public int Refining { get; private set; }
    /// <summary>Seconds a texture must have been finer than needed before it is replaced by a coarser image (no sooner: a camera that turns back
    /// would have it reloaded at once); under the guard's pressure no wait.</summary>
    public double CoarsenAfterSeconds { get; set; } = 10;
    /// <summary>Replacements loading at once, finer ones and coarser ones (the worker threads are few and shared with the foliage's layouts).</summary>
    public int MaxRefines { get; set; } = 6;
    public int MaxCoarsens { get; set; } = 2;
    /// <summary>What the resident images would hold without streaming's drops (each dropped level counted as four times the one below; an estimate,
    /// taken at <see cref="Rebalance"/>).</summary>
    public long UnstreamedBytes { get; private set; }

    /// <summary>Unused for this long: unloaded.</summary>
    public double IdleSeconds { get; set; } = 60;
    /// <summary>Above this the least recently used textures that were unused for <see cref="PressureIdleSeconds"/> go too.</summary>
    public double HighWaterMb { get; set; } = TextureQuality.MemoryBudgetMb(TextureQuality.Level);
    public double PressureIdleSeconds { get; set; } = 8;
    /// <summary>The high-water mark as a share of the driver's video memory budget: the mark is the smaller of <see cref="HighWaterMb"/> and this share (the default share is what
    /// the fixed mark was on the 11.4 GB card it was tuned on), and three quarters of it under the guard's pressure.</summary>
    public double HighWaterShare { get; set; } = TextureQuality.MemoryBudgetMb(0) / 11453.0;
    /// <summary>The mark in effect (MB).</summary>
    public double MarkMb => EffectiveMark(HighWaterMb, HighWaterShare, Guard);

    internal static double EffectiveMark(double highWaterMb, double share, VramGuard? guard)
    {
        if (guard is not { BudgetBytes: > 0 } g || double.IsInfinity(highWaterMb) || highWaterMb >= double.MaxValue) return highWaterMb;
        return Math.Min(highWaterMb, g.BudgetBytes / 1048576.0 * share) * (g.Pressure ? 0.75 : 1);
    }
    /// <summary>The memory-pressure guard (<see cref="VramGuard"/>): while it is under pressure nothing new is loaded (a texture asked for stays
    /// unloaded, drawn with the stand-in, until it ends) and textures idle for <see cref="GuardIdleSeconds"/> are evicted, least recently used
    /// first, whatever the high-water mark.</summary>
    public VramGuard? Guard { get; set; }
    /// <summary>Images shared with the other caches on the same GPU (<see cref="TextureShare"/>); null: this cache's images are its own.</summary>
    public TextureShare? Share { get; init; }
    public double GuardIdleSeconds { get; set; } = 2;

    /// <summary>
    /// The resident textures for the VRAM dump (Ctrl+Shift+F12), largest first: MB, seconds since anything read the key, top levels dropped now,
    /// what mip streaming would drop at the last need (the quality setting's drop included), the need, the file's size and the file.
    /// </summary>
    public void WriteReport(TextWriter w)
    {
        long now = Environment.TickCount64;
        w.WriteLine($"{allocationName}: {Describe()}; mark {MarkMb:0} MB");
        w.WriteLine("        MB   idle s  dropped  target      need       file size  flags  file");
        foreach (var t in cache.Values.Where(t => t.State == WorldTexture.Residency.Resident).OrderByDescending(t => t.Bytes))
        {
            int target = t.KeepAllMips || t.FileWidth == 0 || !float.IsFinite(t.Need) ? t.QualityDrop : Math.Max(Mips.Drop(t.FileWidth, t.FileHeight, t.Need), t.QualityDrop);
            string flags = (t.KeepAllMips ? "k" : "") + (t.CannotDrop ? "c" : "") + (t.Replacing ? "r" : "") + (t.TooFineSince != 0 ? "f" : "") + (t.Shared is { Refs: > 1 } ? "s" : "");
            w.WriteLine($"{t.Bytes / 1048576.0,10:0.000} {(now - t.LastUsed) / 1000.0,8:0.0} {t.Dropped,8} {target,7} {t.Need,9:0.###} {t.FileWidth,9}x{t.FileHeight,-5} {flags,-5}  {t.Name}");
        }
        w.WriteLine("flags: k keeps every level, c cannot drop levels, r a replacement is loading, f finer than needed (waiting to coarsen), s the image is shared with another cache");
        w.WriteLine();
    }

    public string Describe() => $"{ResidentCount} textures {ResidentBytes / 1048576.0:0} MB ({Unloads} unloaded, {Reloads} reloaded so far{(Shares > 0 ? $", {Shares} taken from another cache" : "")}" +
        (Mips.Enabled ? $"; mip streaming: {Math.Max(UnstreamedBytes - ResidentBytes, 0) / 1048576.0:0} MB less than every mip, {Refined} refined, {Coarsened} coarsened)" : ")");

    /// <param name="deferred">The texture is not loaded now: it stays <see cref="WorldTexture.Residency.Unloaded"/> until something reads its
    /// <see cref="WorldTexture.Key"/> (the foliage makes its meshes' textures this way and reads the keys only of groups within their range).</param>
    public WorldTexture? Get(string? name, bool border, bool deferred = false)
    {
        if (name is null) return null;
        string key = border ? name + "|border" : name;
        if (cache.TryGetValue(key, out var t))
        {
            if (!deferred)
            {
                t.LastUsed = Environment.TickCount64;
                if (t.State == WorldTexture.Residency.Unloaded) Reload(t);
            }
            return t;
        }
        cache[key] = t = new WorldTexture { Border = border, Owner = this, Name = name, LastUsed = Environment.TickCount64 };
        byNumber.Add(t);
        t.Number = (uint)byNumber.Count;
        // The game reduces texture fields to the bare file name (runtime-materials.md); a path still works.
        t.Path = assets.Find(System.IO.Path.GetFileName(name.Replace('\\', '/'))) ?? assets.Find(name);
        if (t.Path is null)
        {
            t.State = WorldTexture.Residency.Missing;
            Messages.Add($"texture not found: {name}");
            return t;
        }
        if (deferred || !MayStart(t)) { t.State = WorldTexture.Residency.Unloaded; return t; }   // loaded when its key is read (and the guard lets it)
        Start(t);
        return t;
    }

    /// <summary>Whether the guard lets this texture's image be made now: not under pressure, and the file's size (the image is no larger) fits the room left (<see cref="VramGuard.Allows"/>).</summary>
    bool MayStart(WorldTexture t)
    {
        if (Guard is not { } guard) return true;
        if (!guard.Streaming) return false;
        long bytes;
        try { bytes = new FileInfo(t.Path!).Length; }
        catch (IOException) { bytes = 4 << 20; }
        // What is loading is not in the guard's sample yet (the image is made when its decode lands): it counts against the room too.
        if (!guard.Allows((ulong)bytes, (ulong)inFlight)) return false;
        (t.InFlight, inFlight) = (bytes, inFlight + bytes);
        return true;
    }
    long inFlight;

    /// <summary>
    /// The bindless index (in the 2D float array) of the texture whose <see cref="WorldTexture.Key"/> is <paramref name="key"/>, sampled as the
    /// GL texture was (trilinear, 8x anisotropic, repeat or a transparent border) with the upscaler's LOD bias <paramref name="bias"/>
    /// (<see cref="IGpuContext.LodBias"/>, read where the GL code bound the texture); <paramref name="standIn"/> when it is not resident.
    /// Render thread, in Prepare (it may register an entry).
    /// </summary>
    public uint Index(uint key, float bias, uint standIn)
    {
        if (key == 0 || key > byNumber.Count) return standIn;
        var t = byNumber[(int)key - 1];
        if (t.Native is null) return standIn;
        if (t.SamplerGeneration != gpu.Samplers.Generation)
        {
            // The anisotropic filtering changed: both entries go (after the frames in flight) and are made again with the new sampler.
            if (t.HasA) gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexA);
            if (t.HasB) gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexB);
            (t.HasA, t.HasB, t.SamplerGeneration) = (false, false, gpu.Samplers.Generation);
        }
        if (t.HasA && t.BiasA == bias) return t.IndexA;
        if (t.HasB && t.BiasB == bias)
        {
            (t.BiasA, t.IndexA, t.BiasB, t.IndexB) = (t.BiasB, t.IndexB, t.BiasA, t.IndexA);
            return t.IndexA;
        }
        // A bias not seen yet: a new entry (the older of the two goes after the frames in flight; draws recorded earlier keep theirs).
        if (t.HasB) gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexB);
        (t.BiasB, t.IndexB, t.HasB) = (t.BiasA, t.IndexA, t.HasA);
        var wrap = t.Border ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
        var sampler = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, wrap, wrap, TextureWrapMode.Repeat,
            false, DepthFunction.Lequal, transparentBorder: true, anisotropy: 8, integerFormat: false, bias));
        var view = t.Native.View(0, t.ViewLevels, 0, 1, t.ViewSwizzle);
        t.IndexA = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, view));
        (t.BiasA, t.HasA) = (bias, true);
        return t.IndexA;
    }

    /// <summary>Loads started while this is set go ahead of the other streaming work (<see cref="BackgroundWork.RunUrgent"/>): the owner sets it while it
    /// touches what a flight's arrival view uses (docs/render-objects.md "Arrival prefetch").</summary>
    public bool Urgent { get; set; }

    void Start(WorldTexture t, bool replacing = false)
    {
        string path = t.Path!, name = t.Name;
        // The levels dropped on top of the quality setting's are decided on the worker once the file's size is known, from what the owner says
        // about how near the texture is used now.
        int quality = TextureQuality.LevelsToDrop(System.IO.Path.GetFileName(path), assets.Configured.GroupOfFile(path));
        t.QualityDrop = quality;
        t.QualityLevel = TextureQuality.Level;
        float need = t.KeepAllMips ? float.PositiveInfinity : t.Need;
        if (!replacing && Shared(t, need, exact: false) is { } shared) { Adopt(t, shared); return; }
        bool enabled = Mips.Enabled;
        float perDistance = Mips.PixelsPerDistance, bias = Mips.Bias;
        int extra = Mips.Extra;
        if (replacing) t.Replacing = true;
        else t.State = WorldTexture.Residency.Loading;
        Func<TextureData?> load = () =>
        {
            try { return Load(path, quality, need, enabled, perDistance, bias, extra); }
            catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
            {
                lock (Messages) Messages.Add($"texture {name}: {e.Message}");
                return null;
            }
        };
        t.Pending = Urgent ? BackgroundWork.RunUrgent(load) : BackgroundWork.Run(load);
        pending.Add(t);
    }

    /// <summary>
    /// An image of <paramref name="t"/>'s file another user already holds at the current quality level, with no more top levels dropped than mip streaming
    /// would drop at <paramref name="need"/> (exactly as many when <paramref name="exact"/>), the one with the most dropped of those; null when none (or no <see cref="Share"/>).
    /// </summary>
    TextureShare.Image? Shared(WorldTexture t, float need, bool exact)
    {
        if (Share is null) return null;
        TextureShare.Image? best = null;
        foreach (var e in Share.Of(t.Path!))
        {
            if (e.QualityLevel != TextureQuality.Level || e == t.Shared) continue;
            int target = Math.Max(Mips.Drop(e.FileWidth, e.FileHeight, need), t.QualityDrop);
            if (exact ? e.Dropped == target : e.Dropped <= target && (best is null || e.Dropped > best.Dropped)) best = e;
        }
        return best;
    }

    /// <summary><paramref name="t"/> draws the shared image <paramref name="e"/> (in place of the one it has, if any): resident at once, nothing loaded.</summary>
    void Adopt(WorldTexture t, TextureShare.Image e, bool replaced = false)
    {
        e.Refs++;
        BeginSwap(t, e.Dropped, e.Droppable, e.FileWidth, e.FileHeight, replaced);
        (t.Native, t.Shared, t.ViewLevels, t.ViewSwizzle, t.Swizzled) = (e.Texture, e, e.ViewLevels, e.ViewSwizzle, e.Swizzled);
        Resident(t, e.Bytes);
        Shares++;
    }

    /// <summary>Images taken from another cache instead of loaded (<see cref="Share"/>).</summary>
    public int Shares { get; private set; }

    /// <summary>
    /// A loaded image becomes <paramref name="t"/>'s: when another user already holds the same file at the same levels (both loaded it at once), that
    /// one is taken and the new one goes; else the new one is offered to the others.
    /// </summary>
    void Publish(WorldTexture t, GpuTexture texture, TextureData data, int viewLevels, GpuSwizzle swizzle, long bytes)
    {
        if (Share?.Of(t.Path!).FirstOrDefault(o => o.QualityLevel == t.QualityLevel && o.Dropped == data.Dropped && o.ViewLevels == viewLevels) is { } same && same != t.Shared)
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
                Path = t.Path!, Texture = texture, ViewLevels = viewLevels, ViewSwizzle = swizzle, Swizzled = data.Swizzled, Bytes = bytes,
                Dropped = data.Dropped, QualityLevel = t.QualityLevel, FileWidth = data.FileWidth, FileHeight = data.FileHeight, Droppable = data.Droppable,
            };
            Share.Add(t.Shared);
        }
        (t.Native, t.ViewLevels, t.ViewSwizzle) = (texture, viewLevels, swizzle);
        Resident(t, bytes);
    }

    internal void Reload(WorldTexture t)
    {
        if (t.State != WorldTexture.Residency.Unloaded) return;
        if (!MayStart(t)) return;
        if (t.EverResident) Reloads++;
        Start(t);
    }

    /// <summary>Uploads finished decodes (all of them, waiting, when <paramref name="wait"/>; else steps until <paramref name="budgetMs"/> passes, at most <paramref name="max"/> textures started).</summary>
    public void Pump(bool wait, int max = 16, double budgetMs = 1.5)
    {
        int started = 0, ranSteps = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (!wait) Requalify();
        while (true)
        {
            if (steps.Count > 0)
            {
                // The first step always runs; a later one waits for the next call when steps of its kind have cost more than what is left plus StepCosts.Slack.
                if (!wait && (watch.Elapsed.TotalMilliseconds >= budgetMs || ranSteps > 0 && !stepCosts.Fits(steps.Peek().Method, watch.Elapsed.TotalMilliseconds, budgetMs))) break;
                var step = steps.Dequeue();
                var one = System.Diagnostics.Stopwatch.StartNew();
                gpu.EnsureFrame();
                step();
                double stepMs = one.Elapsed.TotalMilliseconds;
                stepCosts.Learn(step.Method, stepMs);
                ranSteps++;
                if (stepMs > 3) SpikeLog.Note($"slow texture step {step.Method.Name}", ms: stepMs);
                continue;
            }
            int found = -1;
            for (int i = 0; i < pending.Count && found < 0; i++)
                if (wait || pending[i].Pending!.IsCompleted) found = i;
            if (found < 0 || (!wait && (started >= max || watch.Elapsed.TotalMilliseconds >= budgetMs))) break;
            var t = pending[found];
            var data = t.Pending!.Result;
            if (data is not null) QueueUpload(t, data);
            else if (t.Replacing) { if (t.Refine) { t.Refine = false; Refining--; } t.Replacing = false; t.CannotDrop = true; Landed(t); }   // the old image stays
            else { t.State = WorldTexture.Residency.Missing; Landed(t); }
            t.Pending = null;
            pending.RemoveAt(found);
            started++;
        }
        if (!wait)
        {
            var trim = System.Diagnostics.Stopwatch.StartNew();
            Trim();
            if (trim.Elapsed.TotalMilliseconds > 3) SpikeLog.Note("slow texture trim", ms: trim.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>The <see cref="TextureQuality.Level"/> every resident image has been brought to (-1: not checked yet).</summary>
    int requalifiedLevel = -1;

    /// <summary>
    /// The texture quality moved (the Tab slider): resident images loaded at another level are loaded again at the new one, a few at a time
    /// (<see cref="MaxRefines"/> replacements at once), the old image drawn until the new one is in. Unloaded ones read the level when they load again.
    /// </summary>
    internal void Requalify()
    {
        int level = TextureQuality.Level;
        if (level == requalifiedLevel) return;
        int replacing = 0;
        foreach (var t in cache.Values) if (t.Replacing) replacing++;
        bool done = true;
        foreach (var t in cache.Values)
        {
            if (t.QualityLevel == level || t.State is WorldTexture.Residency.Missing or WorldTexture.Residency.Unloaded) continue;
            if (t.State != WorldTexture.Residency.Resident || t.Native is null || t.Replacing || t.Pending is not null) { done = false; continue; }
            if (TextureQuality.LevelsToDrop(System.IO.Path.GetFileName(t.Path!), assets.Configured.GroupOfFile(t.Path!), level) == t.QualityDrop || t.CannotDrop)
            {
                t.QualityLevel = level;   // this file drops as many levels at the new setting (an _HI or _LO name, an exempt group)
                continue;
            }
            if (replacing >= MaxRefines || !MayStart(t)) { done = false; continue; }
            Start(t, replacing: true);
            replacing++;
        }
        if (done) requalifiedLevel = level;
    }

    /// <summary>Deletes textures that have been idle too long, or the least recently used ones while the cache is over its high-water mark. At most once a second.</summary>
    void Trim()
    {
        long now = Environment.TickCount64;
        if (now - lastTrim < 1000) return;
        lastTrim = now;
        bool guarded = Guard?.Pressure ?? false;
        bool pressure = ResidentBytes > MarkMb * 1048576;
        List<WorldTexture>? victims = null;
        foreach (var t in cache.Values)
        {
            if (t.State != WorldTexture.Residency.Resident || t.Replacing || t.Pending is not null) continue;
            double idle = (now - t.LastUsed) / 1000.0;
            if (idle > IdleSeconds || pressure && idle > PressureIdleSeconds || guarded && idle > GuardIdleSeconds) (victims ??= []).Add(t);
        }
        if (victims is null) return;
        victims.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        long lowWater = (long)(MarkMb * 1048576 * 0.75);
        foreach (var t in victims.Take(guarded ? 120 : 40))
        {
            bool old = (now - t.LastUsed) / 1000.0 > IdleSeconds;
            if (!old && !guarded && ResidentBytes <= lowWater) break;
            Unload(t);
        }
    }

    /// <summary>Takes what the users offered since the last call as the textures' <see cref="WorldTexture.Need"/> (a texture nothing offered keeps its last).</summary>
    public void CommitNeeds()
    {
        foreach (var t in cache.Values)
        {
            if (float.IsFinite(t.NeedPass)) t.Need = t.NeedPass;
            t.NeedPass = float.PositiveInfinity;
        }
    }

    /// <summary>
    /// Mip streaming: after <see cref="CommitNeeds"/>, a resident texture whose image has more top levels dropped than it may now (something came
    /// near) is loaded again with fewer, and the old image drawn until the new one is in (<see cref="BeginSwap"/>); one that is finer than it needs to be
    /// for <see cref="CoarsenAfterSeconds"/> (at once under the guard's pressure) is replaced by a coarser. Only the textures this has been told the
    /// need of are touched (<see cref="WorldTexture.KeepAllMips"/> never).
    /// </summary>
    public void Rebalance()
    {
        long now = Environment.TickCount64;
        bool pressure = Guard?.Pressure ?? false;
        Mips.Extra = pressure ? 1 : 0;
        long unstreamed = 0;
        int replacing = 0;   // replacements being loaded: finer ones are limited so they do not crowd out the other streaming work on the few worker threads
        foreach (var t in cache.Values) if (t.Replacing) replacing++;
        foreach (var t in cache.Values)
        {
            if (t.State != WorldTexture.Residency.Resident || t.Native is null) continue;
            unstreamed += t.Bytes << (2 * Math.Max(t.Dropped - t.QualityDrop, 0));
            if (t.Replacing || t.Pending is not null || t.KeepAllMips || t.CannotDrop || t.FileWidth == 0 || !float.IsFinite(t.Need)) continue;
            int target = Math.Max(Mips.Drop(t.FileWidth, t.FileHeight, t.Need), t.QualityDrop);
            // Another cache holds the file at the target levels: taken (a coarser one after the same wait as a load), nothing loaded.
            if ((target < t.Dropped || target > t.Dropped && (pressure || t.TooFineSince != 0 && now - t.TooFineSince >= CoarsenAfterSeconds * 1000))
                && Shared(t, t.Need, exact: true) is { } shared) { Adopt(t, shared); continue; }
            if (target < t.Dropped)
            {
                if (replacing >= MaxRefines || !MayStart(t)) continue;
                Refined++;
                t.Refine = true; Refining++;
                Start(t, replacing: true);
                replacing++;
            }
            else if (target > t.Dropped)
            {
                if (t.TooFineSince == 0) t.TooFineSince = now;
                if (!pressure && now - t.TooFineSince < CoarsenAfterSeconds * 1000) continue;
                if (replacing >= MaxCoarsens) continue;
                Coarsened++;
                Start(t, replacing: true);
                replacing++;
            }
            else t.TooFineSince = 0;
        }
        UnstreamedBytes = unstreamed;
    }

    void Unload(WorldTexture t)
    {
        Release(t);
        ResidentBytes -= t.Bytes;
        ResidentCount--;
        Unloads++;
        t.Bytes = 0;
        t.State = WorldTexture.Residency.Unloaded;
    }

    /// <summary>Frees the texture's bindless entries (after the frames in flight) and its image.</summary>
    void Release(WorldTexture t)
    {
        if (t.HasA) gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexA);
        if (t.HasB) gpu.Bindless.Free(BindlessKind.Texture2D, t.IndexB);
        t.HasA = t.HasB = false;
        if (t.Shared is { } shared) { Share!.Release(shared); t.Shared = null; }
        else t.Native?.Dispose();
        t.Native = null;
        Version++;
    }

    readonly Queue<Action> steps = new();
    readonly StepCosts stepCosts = new();
    const int SlabBytes = 512 << 10;

    // DDS textures in BC1, BC2, BC3, BC4 or BC5 with a full mip chain go to the GPU as they are stored (BC4 through a swizzle that shows it as grey);
    // the rest is decoded to RGBA8 on the CPU.
    static TextureData Load(string path, int dropMips, float need, bool streaming, float perDistance, float bias, int extra)
    {
        using var file = File.OpenHandle(Meitou.Content.GameFiles.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        Span<byte> magic = stackalloc byte[4];
        if (RandomAccess.Read(file, magic, 0) == 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic) == DdsReader.Magic)
        {
            // GpuTexture quality: the game drops top mips as it loads (docs/formats/settings.md); mip streaming drops more where nothing near uses them.
            // Only the mips that are kept are read: a dropped top level is three quarters of the file, and was read, held and thrown away (docs/renderer-native.md 8.14).
            // The file's bytes are pooled (docs/engine.md, pooled buffers): an RGBA8 decode returns them here, a stored upload after its last step.
            var dds = DdsReader.ReadKept(file, w => TextureQuality.DropTopMips(w, Math.Max(dropMips, MipStreaming.Drop(w.Width, w.Height, need, streaming, perDistance, bias, extra))), out var whole, ArrayPool<byte>.Shared);
            bool handedOver = false;
            try
            {
                int dropped = whole.MipCount - dds.MipCount;
                bool droppable = TextureQuality.DropTopMips(whole, 1).MipCount < whole.MipCount;
                if (CanUploadCompressed(dds))
                {
                    // Only the level the swizzle test looks at is decoded on the CPU.
                    int level = Math.Max(Array.FindIndex(dds.Surfaces.ToArray(), s => s.Width <= 256 && s.Height <= 256), 0);
                    var compressed = new TextureData(new LoadedTexture([DdsDecoder.Decode(dds, 0, level)], null), dds, dropped, whole.Width, whole.Height, droppable);
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
        return new TextureData(new LoadedTexture([TextureLoader.LoadImage(File.ReadAllBytes(Meitou.Content.GameFiles.Resolve(path)))], null), null);
    }

    static bool CanUploadCompressed(DdsFile dds) =>
        dds.Format is DdsFormat.Bc1 or DdsFormat.Bc2 or DdsFormat.Bc3 or DdsFormat.Bc4 or DdsFormat.Bc5 or DdsFormat.Bc7 && !dds.IsCubemap && !dds.IsVolume && dds.ImageCount == 1 && dds.Width > 0 && dds.Height > 0
        && dds.MipCount == 1 + (int)Math.Log2(Math.Max(dds.Width, dds.Height)) && dds.Surfaces.Count == dds.MipCount;

    /// <summary>The number of levels down to 1 × 1 (the chain VkGl allocated for a texture's level 0).</summary>
    static int FullChain(int w, int h) => 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(w, h), 1)));

    /// <summary>
    /// A new image for <paramref name="t"/> with <paramref name="levels"/> levels in GENERAL layout (the transition in the frame's upload commands, before
    /// the copies), named "owner | file" (the VRAM pie groups by the owner, the VRAM dump lists the file).
    /// </summary>
    GpuTexture Create(WorldTexture t, InternalFormat format, int width, int height, int levels, bool mipmaps) =>
        gpu.Frame.PreFrame.CreateTexture(new TextureDesc(format, width, height, levels,
            Use: TextureUse.Sampled | TextureUse.TransferDst | (mipmaps ? TextureUse.TransferSrc : 0), Name: $"{allocationName} | {System.IO.Path.GetFileName(t.Path)}"));

    /// <summary>
    /// A stored (S3TC) texture's upload: the image is made with its full chain, each level is filled whole (up to about 512 KB) or in slabs of
    /// whole block rows of about 512 KB, nothing copied twice. Same sampling state as the RGBA8 path; the mip chain is the file's own.
    /// </summary>
    void QueueCompressedUpload(WorldTexture t, TextureData data)
    {
        var dds = data.Compressed!;
        t.Swizzled = data.Swizzled;
        var format = dds.Format switch
        {
            DdsFormat.Bc1 => InternalFormat.CompressedRgbaS3TCDxt1Ext,
            DdsFormat.Bc2 => InternalFormat.CompressedRgbaS3TCDxt3Ext,
            DdsFormat.Bc4 => InternalFormat.CompressedRedRgtc1,
            DdsFormat.Bc5 => InternalFormat.CompressedRGRgtc2,
            DdsFormat.Bc7 => InternalFormat.CompressedRgbaBptcUnorm,
            _ => InternalFormat.CompressedRgbaS3TCDxt5Ext,
        };
        int blockBytes = BlockCompression.BlockBytes(dds.Format);
        GpuTexture? texture = null;
        steps.Enqueue(() => texture = Create(t, format, dds.Width, dds.Height, FullChain(dds.Width, dds.Height), mipmaps: false));
        foreach (var surface in dds.Surfaces)
        {
            var s = surface;
            int blocksPerRow = (s.Width + 3) / 4, blockRows = (s.Height + 3) / 4, rowBytes = blocksPerRow * blockBytes;
            int length = Math.Min(s.Length, rowBytes * blockRows);
            int rowsPerSlab = length <= SlabBytes ? blockRows : Math.Max(1, SlabBytes / rowBytes);
            for (int row = 0; row < blockRows; row += rowsPerSlab)
            {
                int r0 = row, n = Math.Min(rowsPerSlab, blockRows - row);
                steps.Enqueue(() =>
                {
                    uint height = (uint)Math.Min(n * 4, s.Height - r0 * 4);
                    int bytes = Math.Min(n * rowBytes, length - r0 * rowBytes);
                    using var batch = gpu.Uploads.Begin();
                    batch.Write(texture!, s.Level, 0, new PixelRect(0, r0 * 4, (uint)s.Width, height),
                        dds.Data.AsSpan(s.Offset + r0 * rowBytes, bytes));
                });
            }
        }
        steps.Enqueue(() =>
        {
            // BC4 is one channel; the decoder (and so the RGBA8 path) shows it as grey with alpha 1.
            Publish(t, texture!, data, Math.Min(dds.MipCount, texture!.Desc.Levels), dds.Format == DdsFormat.Bc4 ? GpuSwizzle.RrrOne : default, dds.Surfaces.Sum(s => (long)s.Length));
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
        bool generate = tex.Levels.Count == 1 || tex.Levels[^1].Width > 1 || tex.Levels[^1].Height > 1;
        int uploaded = generate ? 1 : Math.Min(tex.Levels.Count, chain);
        GpuTexture? texture = null;
        steps.Enqueue(() => texture = Create(t, InternalFormat.Rgba8, top.Width, top.Height, chain, mipmaps: generate));
        for (int level = 0; level < uploaded; level++)
        {
            var img = tex.Levels[level];
            int lv = level, rowBytes = img.Width * 4;
            int rows = Math.Max(1, SlabBytes / rowBytes);
            for (int y = 0; y < img.Height; y += rows)
            {
                int y0 = y, h = Math.Min(rows, img.Height - y);
                steps.Enqueue(() =>
                {
                    using var batch = gpu.Uploads.Begin();
                    batch.Write(texture!, lv, 0, new PixelRect(0, y0, (uint)img.Width, (uint)h),
                        img.Pixels.AsSpan(y0 * rowBytes, h * rowBytes));
                });
            }
        }
        steps.Enqueue(() =>
        {
            long bytes = tex.Levels.Sum(l => (long)l.Width * l.Height * 4);
            if (generate)
            {
                GenerateMipmaps(gpu, texture!);
                bytes = bytes * 4 / 3;   // the full chain the blits made
            }
            Publish(t, texture!, data, generate ? chain : uploaded, default, bytes);
        });
    }

    /// <summary>Each level from the one above by a linear blit, in the frame's upload commands after the uploads (VkGl's <c>GenerateMipmap</c>).</summary>
    internal static void GenerateMipmaps(IGpuContext gpu, GpuTexture texture)
    {
        using var batch = gpu.Uploads.Begin();
        batch.Commands.GenerateMips(texture);
    }

    /// <summary>A texture left the loading state: its file size is no longer counted as in flight.</summary>
    void Landed(WorldTexture t)
    {
        inFlight -= t.InFlight;
        t.InFlight = 0;
    }

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

    void QueueUpload(WorldTexture t, TextureData data)
    {
        bool awaited = !t.Replacing || t.Refine;   // AwaitedCount
        if (awaited) uploadsAwaited++;
        if (data.Compressed is not null) QueueCompressedUpload(t, data);
        else QueueUpload(t, data.Rgba, data.Swizzled, data);
        if (awaited) steps.Enqueue(() => uploadsAwaited--);
    }

    /// <summary>The new image is about to replace the one the texture has (a reload with another number of top levels, <see cref="Rebalance"/>): the old
    /// one goes (its bindless entries after the frames in flight, as an unload) and its bytes leave the count; <see cref="Resident"/> adds the new.
    /// Records the levels the new image lacks.</summary>
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
        if (t.Refine) { t.Refine = false; Refining--; }
        t.Replacing = false;
        t.TooFineSince = 0;
        t.Dropped = dropped;
        t.FileWidth = fileWidth;
        t.FileHeight = fileHeight;
    }

    internal static bool LooksSwizzled(RgbaImage image)
    {
        long r = 0, g = 0, b = 0;
        var p = image.Pixels;
        for (int i = 0; i < p.Length; i += 4) { r += p[i]; g += p[i + 1]; b += p[i + 2]; }
        long n = Math.Max(p.Length / 4, 1);
        return b / n < 180 && Math.Abs(r - b) / n < 8 && Math.Abs(g - b) / n < 8;
    }

    public void Dispose()
    {
        foreach (var t in pending) t.Pending?.Wait();
        foreach (var t in cache.Values) Release(t);
    }
}

/// <summary>A decoded texture file: the RGBA8 levels (all of them, or for a stored S3TC texture only the one the swizzle test needs), and the DDS when its blocks are uploaded as they are.</summary>
sealed record TextureData(LoadedTexture Rgba, DdsFile? Compressed, int Dropped = 0, int FileWidth = 0, int FileHeight = 0, bool Droppable = false)
{
    /// <summary>Whether it looks like a "DXT5 normal" (X in alpha), tested on the worker: the full image can be megabytes.</summary>
    public bool Swizzled { get; } = WorldTextureCache.LooksSwizzled(Rgba.Levels.FirstOrDefault(l => l.Width <= 256 && l.Height <= 256) ?? Rgba.Levels[0]);
}

/// <summary>
/// The images the world's texture caches share (<see cref="WorldTextureCache.Share"/>): a file the objects, the foliage and the characters all draw is
/// in video memory once at a given number of dropped levels (the VRAM dump of 2026-11-01 held about 150 MB of such copies). An image goes when its
/// last user releases it. Each cache still counts the image's bytes as its own. Render thread only.
/// </summary>
public sealed class TextureShare
{
    internal sealed class Image
    {
        public required string Path;
        public required GpuTexture Texture;
        public int Refs = 1;
        public int ViewLevels, Dropped, QualityLevel, FileWidth, FileHeight;
        public GpuSwizzle ViewSwizzle;
        public bool Swizzled, Droppable;
        public long Bytes;
    }

    readonly Dictionary<string, List<Image>> byPath = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IGpuContext, TextureShare> shares = new();

    /// <summary>The share of the caches on <paramref name="gpu"/>.</summary>
    public static TextureShare For(IGpuContext gpu) => shares.GetValue(gpu, _ => new TextureShare());

    internal IReadOnlyList<Image> Of(string path) => byPath.TryGetValue(path, out var list) ? list : [];

    internal void Add(Image e)
    {
        if (!byPath.TryGetValue(e.Path, out var list)) byPath[e.Path] = list = [];
        list.Add(e);
    }

    internal void Release(Image e)
    {
        if (--e.Refs > 0) return;
        e.Texture.Dispose();
        if (byPath.TryGetValue(e.Path, out var list) && list.Remove(e) && list.Count == 0) byPath.Remove(e.Path);
    }
}
