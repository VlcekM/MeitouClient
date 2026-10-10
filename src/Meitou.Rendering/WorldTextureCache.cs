using Meitou.Data.Textures;

using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using Texture = Meitou.Rendering.Gpu.Texture;

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
    internal Texture? Native;
    internal int ViewLevels;
    internal ComponentMapping ViewSwizzle;
    /// <summary>The bindless entries for the last two LOD biases (the upscaler's, and 0 where a pass runs without it).</summary>
    internal float BiasA, BiasB;
    /// <summary>The <see cref="SamplerCache.Generation"/> its entries were made under (the anisotropic filtering setting).</summary>
    internal int SamplerGeneration;
    internal uint IndexA, IndexB;
    internal bool HasA, HasB;

    internal enum Residency { Loading, Resident, Unloaded, Missing }
}

/// <summary>
/// Texture cache for the world view: files are decoded on worker threads and uploaded on the render thread by <see cref="Pump"/> into native
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
    readonly GpuContext gpu;
    readonly AssetLocator assets;
    /// <summary>The images' allocation name (the F12 VRAM pie groups by it).</summary>
    readonly string allocationName;
    long lastTrim;
    static readonly bool StreamLog = Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1";
    /// <summary>MEITOU_MIP_LOG=1: a line for every image loaded with more levels dropped than the quality setting's, and for every swap.</summary>
    static readonly bool MipLog = Environment.GetEnvironmentVariable("MEITOU_MIP_LOG") == "1";
    static readonly string[]? MipSkip = Environment.GetEnvironmentVariable("MEITOU_MIP_SKIP")?.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <param name="name">The images' allocation name, by owner ("object textures", "foliage textures").</param>
    public WorldTextureCache(GpuContext gpu, AssetLocator assets, string name)
    {
        this.gpu = gpu;
        this.assets = assets;
        allocationName = name;
    }

    public List<string> Messages { get; } = [];
    public int PendingCount => pending.Count + (steps.Count > 0 ? 1 : 0);

    /// <summary>GPU memory of the resident textures (all levels, RGBA8) and how many there are.</summary>
    public long ResidentBytes { get; private set; }
    public int ResidentCount { get; private set; }
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
    public double IdleSeconds { get; set; } = StreamingTuning.IdleSeconds;
    /// <summary>Above this the least recently used textures that were unused for <see cref="PressureIdleSeconds"/> go too.</summary>
    public double HighWaterMb { get; set; } = StreamingTuning.IdleSeconds > 1e8 ? double.MaxValue : TextureQuality.MemoryBudgetMb(TextureQuality.Level);
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
    public double GuardIdleSeconds { get; set; } = 2;

    public string Describe() => $"{ResidentCount} textures {ResidentBytes / 1048576.0:0} MB ({Unloads} unloaded, {Reloads} reloaded so far" +
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
        if (MipSkip is { } skip && skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) t.KeepAllMips = true;
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
    /// (<see cref="GpuContext.LodBias"/>, read where the GL code bound the texture); <paramref name="standIn"/> when it is not resident.
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
        t.IndexA = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, view, t.Native.Image));
        (t.BiasA, t.HasA) = (bias, true);
        return t.IndexA;
    }

    void Start(WorldTexture t, bool replacing = false)
    {
        string path = t.Path!, name = t.Name;
        // The levels dropped on top of the quality setting's are decided on the worker once the file's size is known, from what the owner says
        // about how near the texture is used now.
        int quality = TextureQuality.LevelsToDrop(System.IO.Path.GetFileName(path), assets.Configured.GroupOfFile(path));
        t.QualityDrop = quality;
        t.QualityLevel = TextureQuality.Level;
        float need = t.KeepAllMips ? float.PositiveInfinity : t.Need;
        bool enabled = Mips.Enabled;
        float perDistance = Mips.PixelsPerDistance, bias = Mips.Bias;
        int extra = Mips.Extra;
        if (replacing) t.Replacing = true;
        else t.State = WorldTexture.Residency.Loading;
        t.Pending = BackgroundWork.Run(() =>
        {
            try { return Load(path, quality, need, enabled, perDistance, bias, extra); }
            catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
            {
                lock (Messages) Messages.Add($"texture {name}: {e.Message}");
                return null;
            }
        });
        pending.Add(t);
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
                if (StreamLog) Gpu.UploadProfile.Take();
                gpu.EnsureFrame();
                step();
                double stepMs = one.Elapsed.TotalMilliseconds;
                stepCosts.Learn(step.Method, stepMs);
                ranSteps++;
                if (StreamLog) { string parts = Gpu.UploadProfile.Take(); if (stepMs > 3) Console.WriteLine($"slow texture step {step.Method.Name}: {stepMs:0.0} ms [{parts}]"); }
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
            if (StreamLog && trim.Elapsed.TotalMilliseconds > 3) Console.WriteLine($"slow texture trim: {trim.Elapsed.TotalMilliseconds:0.0} ms");
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
        t.Native?.Dispose();
        t.Native = null;
    }

    readonly Queue<Action> steps = new();
    readonly StepCosts stepCosts = new();
    const int SlabBytes = 512 << 10;

    /// <summary>
    /// MEITOU_UNCOMPRESSED_TEXTURES=1 decodes every texture to RGBA8 on the CPU and uploads that (the way it was before; uses 4 to 8 times the GPU memory
    /// of BC1/BC3 textures). Otherwise DDS textures in BC1, BC2, BC3, BC4 or BC5 with a full mip chain go to the GPU as they are stored (BC4 through a swizzle that shows it as grey).
    /// </summary>
    static readonly bool Uncompressed = Environment.GetEnvironmentVariable("MEITOU_UNCOMPRESSED_TEXTURES") == "1";

    static TextureData Load(string path, int dropMips, float need, bool streaming, float perDistance, float bias, int extra)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsReader.Magic)
        {
            var whole = DdsReader.Read(bytes);
            // Texture quality: the game drops top mips as it loads (docs/formats/settings.md); mip streaming drops more where nothing near uses them.
            int streamed = MipStreaming.Drop(whole.Width, whole.Height, need, streaming, perDistance, bias, extra);
            var dds = TextureQuality.DropTopMips(whole, Math.Max(dropMips, streamed));
            int dropped = whole.MipCount - dds.MipCount;
            bool droppable = TextureQuality.DropTopMips(whole, 1).MipCount < whole.MipCount;
            if (!Uncompressed && CanUploadCompressed(dds))
            {
                // Only the level the swizzle test looks at is decoded on the CPU.
                int level = Math.Max(Array.FindIndex(dds.Surfaces.ToArray(), s => s.Width <= 256 && s.Height <= 256), 0);
                return new TextureData(new LoadedTexture([DdsDecoder.Decode(dds, 0, level)], dds), dds, dropped, whole.Width, whole.Height, droppable);
            }
            return new TextureData(TextureLoader.FromDds(dds), null, dropped, whole.Width, whole.Height, droppable);
        }
        return new TextureData(new LoadedTexture([TextureLoader.LoadImage(bytes)], null), null);
    }

    static bool CanUploadCompressed(DdsFile dds) =>
        dds.Format is DdsFormat.Bc1 or DdsFormat.Bc2 or DdsFormat.Bc3 or DdsFormat.Bc4 or DdsFormat.Bc5 && !dds.IsCubemap && !dds.IsVolume && dds.ImageCount == 1 && dds.Width > 0 && dds.Height > 0
        && dds.MipCount == 1 + (int)Math.Log2(Math.Max(dds.Width, dds.Height)) && dds.Surfaces.Count == dds.MipCount;

    /// <summary>The number of levels down to 1 × 1 (the chain VkGl allocated for a texture's level 0).</summary>
    static int FullChain(int w, int h) => 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(w, h), 1)));

    /// <summary>A new image with <paramref name="levels"/> levels in GENERAL layout (the transition in the frame's upload commands, before the copies).</summary>
    Texture Create(Format format, int width, int height, int levels, bool mipmaps) =>
        Texture.Create(gpu, new TextureDesc(format, width, height, levels,
            Use: TextureUse.Sampled | TextureUse.TransferDst | (mipmaps ? TextureUse.TransferSrc : 0), Name: allocationName), gpu.Frame.PreFrame.Handle);

    /// <summary>
    /// A stored (S3TC) texture's upload: the image is made with its full chain, each level is filled whole (up to about 512 KB) or in slabs of
    /// whole block rows of about 512 KB, nothing copied twice. Same sampling state as the RGBA8 path; the mip chain is the file's own.
    /// </summary>
    void QueueCompressedUpload(WorldTexture t, TextureData data)
    {
        var dds = data.Compressed!;
        t.Swizzled = data.Swizzled;
        var format = GlConventions.VkFormat(dds.Format switch
        {
            DdsFormat.Bc1 => InternalFormat.CompressedRgbaS3TCDxt1Ext,
            DdsFormat.Bc2 => InternalFormat.CompressedRgbaS3TCDxt3Ext,
            DdsFormat.Bc4 => (InternalFormat)0x8DBB,   // COMPRESSED_RED_RGTC1
            DdsFormat.Bc5 => (InternalFormat)0x8DBD,   // COMPRESSED_RG_RGTC2
            _ => InternalFormat.CompressedRgbaS3TCDxt5Ext,
        });
        int blockBytes = BlockCompression.BlockBytes(dds.Format);
        Texture? texture = null;
        steps.Enqueue(() => texture = Create(format, dds.Width, dds.Height, FullChain(dds.Width, dds.Height), mipmaps: false));
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
                    gpu.Uploads.Write(texture!, s.Level, 0, new Rect2D(new Offset2D(0, r0 * 4), new Extent2D((uint)s.Width, height)),
                        dds.Data.AsSpan(s.Offset + r0 * rowBytes, bytes));
                });
            }
        }
        steps.Enqueue(() =>
        {
            BeginSwap(t, data);
            t.Native = texture;
            t.ViewLevels = Math.Min(dds.MipCount, texture!.Desc.Levels);
            // BC4 is one channel; the decoder (and so the RGBA8 path) shows it as grey with alpha 1.
            t.ViewSwizzle = dds.Format == DdsFormat.Bc4 ? new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.One) : default;
            long bytes = dds.Surfaces.Sum(s => (long)s.Length);
            Resident(t, bytes);
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
        Texture? texture = null;
        steps.Enqueue(() => texture = Create(Format.R8G8B8A8Unorm, top.Width, top.Height, chain, mipmaps: generate));
        for (int level = 0; level < uploaded; level++)
        {
            var img = tex.Levels[level];
            int lv = level, rowBytes = img.Width * 4;
            int rows = Math.Max(1, SlabBytes / rowBytes);
            for (int y = 0; y < img.Height; y += rows)
            {
                int y0 = y, h = Math.Min(rows, img.Height - y);
                steps.Enqueue(() => gpu.Uploads.Write(texture!, lv, 0, new Rect2D(new Offset2D(0, y0), new Extent2D((uint)img.Width, (uint)h)),
                    img.Pixels.AsSpan(y0 * rowBytes, h * rowBytes)));
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
            BeginSwap(t, data);
            t.Native = texture;
            t.ViewLevels = generate ? chain : uploaded;
            t.ViewSwizzle = default;
            Resident(t, bytes);
        });
    }

    /// <summary>Each level from the one above by a linear blit, in the frame's upload commands after the uploads (VkGl's <c>GenerateMipmap</c>).</summary>
    internal static void GenerateMipmaps(GpuContext gpu, Texture texture)
    {
        var cmd = gpu.Frame.PreFrame;
        cmd.Barrier(BarrierBatch.Full);
        for (int level = 1; level < texture.Desc.Levels; level++)
        {
            var between = new BarrierBatch();
            between.Add(PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);
            cmd.Barrier(in between);
            cmd.BlitLevel(texture, level - 1, level, Filter.Linear);
        }
        cmd.Barrier(BarrierBatch.Full);
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
        ResidentBytes += bytes;
        ResidentCount++;
    }

    void QueueUpload(WorldTexture t, TextureData data)
    {
        if (data.Compressed is not null) QueueCompressedUpload(t, data);
        else QueueUpload(t, data.Rgba, data.Swizzled, data);
    }

    /// <summary>The new image is about to replace the one the texture has (a reload with another number of top levels, <see cref="Rebalance"/>): the old
    /// one goes (its bindless entries after the frames in flight, as an unload) and its bytes leave the count; <see cref="Resident"/> adds the new.
    /// Records the levels the new image lacks.</summary>
    void BeginSwap(WorldTexture t, TextureData data)
    {
        if (t.Native is not null)
        {
            Release(t);
            ResidentBytes -= t.Bytes;
            ResidentCount--;
            t.Bytes = 0;
            if (t.State == WorldTexture.Residency.Resident) Swaps++;
        }
        if (t.Replacing && data.Dropped == t.Dropped) t.CannotDrop = true;   // nothing changed: do not ask again
        t.CannotDrop |= !data.Droppable;
        if (MipLog && (data.Dropped > t.QualityDrop || t.Dropped > t.QualityDrop))
            Console.WriteLine($"mips      {allocationName} {t.Name} {data.FileWidth}x{data.FileHeight}: {t.Dropped} -> {data.Dropped} levels dropped (quality {t.QualityDrop}), need {t.Need:0.0}");
        if (t.Refine) { t.Refine = false; Refining--; }
        t.Replacing = false;
        t.TooFineSince = 0;
        t.Dropped = data.Dropped;
        t.FileWidth = data.FileWidth;
        t.FileHeight = data.FileHeight;
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
        if (Environment.GetEnvironmentVariable("MEITOU_TEX_STATS") == "1")
        {
            var res = cache.Values.Where(t => t.State == WorldTexture.Residency.Resident).ToList();
            var byPath = res.GroupBy(t => t.Path!.ToLowerInvariant()).ToList();
            long dup = byPath.Sum(g => g.Sum(t => t.Bytes) - g.First().Bytes);
            Console.WriteLine($"texstats  {allocationName}: {res.Count} resident, {byPath.Count} distinct paths, duplicates {dup / 1048576.0:0} MB ({res.Count(t => t.Border)} border variants), keys {cache.Count}");
            foreach (var t in res.OrderByDescending(t => t.Bytes).Take(12)) Console.WriteLine($"texstats    {t.Bytes / 1048576.0:0.0} MB {t.Name} {t.Native?.Desc.Width}x{t.Native?.Desc.Height}");
            var hist = res.GroupBy(t => t.Native?.Desc.Width ?? 0).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}x{g.Sum(t => t.Bytes) / 1048576.0:0}MB");
            Console.WriteLine("texstats    by width " + string.Join(", ", hist));
        }
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
