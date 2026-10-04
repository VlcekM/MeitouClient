using Meitou.Data.Textures;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>
/// A texture of the world view's objects: 0 until its file is decoded (in the background) and uploaded. Reading <see cref="Id"/> counts as using it
/// (the draw code reads it every frame it draws with the texture), and reading it after the cache unloaded the texture starts loading it again.
/// </summary>
public sealed class WorldTexture
{
    uint id;
    public uint Id
    {
        get
        {
            LastUsed = Environment.TickCount64;
            if (State == Residency.Unloaded) Owner?.Reload(this);
            return id;
        }
        internal set => id = value;
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
    internal Residency State;
    internal uint RawId => id;

    internal enum Residency { Loading, Resident, Unloaded, Missing }
}

/// <summary>
/// Texture cache for the world view: files are decoded on worker threads and uploaded on the GL thread by
/// <see cref="Pump"/>, so loading a town's few hundred textures uses every core and never stalls a frame for long.
/// Same lookup, sampling and swizzle detection as <see cref="Renderer"/>.
/// Textures nobody has read the id of for <see cref="IdleSeconds"/> are deleted (<see cref="Trim"/>), earlier when the cache holds more than
/// <see cref="HighWaterMb"/>: the least recently used go first, down to three quarters of it. A deleted texture comes back by itself
/// (decode and upload again) the next time something asks for its id.
/// </summary>
public sealed unsafe class WorldTextureCache(GL gl, AssetLocator assets) : IDisposable
{
    readonly Dictionary<string, WorldTexture> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly List<WorldTexture> pending = [];
    long lastTrim;
    static readonly bool StreamLog = Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1";

    public List<string> Messages { get; } = [];
    public int PendingCount => pending.Count + (steps.Count > 0 ? 1 : 0);

    /// <summary>GPU memory of the resident textures (all levels, RGBA8) and how many there are.</summary>
    public long ResidentBytes { get; private set; }
    public int ResidentCount { get; private set; }
    public int Unloads { get; private set; }
    public int Reloads { get; private set; }

    /// <summary>Unused for this long: unloaded.</summary>
    public double IdleSeconds { get; set; } = StreamingTuning.IdleSeconds;
    /// <summary>Above this the least recently used textures that were unused for <see cref="PressureIdleSeconds"/> go too.</summary>
    public double HighWaterMb { get; set; } = StreamingTuning.IdleSeconds > 1e8 ? double.MaxValue : 1024;
    public double PressureIdleSeconds { get; set; } = 8;

    public string Describe() => $"{ResidentCount} textures {ResidentBytes / 1048576.0:0} MB ({Unloads} unloaded, {Reloads} reloaded so far)";

    public WorldTexture? Get(string? name, bool border)
    {
        if (name is null) return null;
        string key = border ? name + "|border" : name;
        if (cache.TryGetValue(key, out var t))
        {
            t.LastUsed = Environment.TickCount64;
            if (t.State == WorldTexture.Residency.Unloaded) Reload(t);
            return t;
        }
        cache[key] = t = new WorldTexture { Border = border, Owner = this, Name = name, LastUsed = Environment.TickCount64 };
        // The game reduces texture fields to the bare file name (runtime-materials.md); a path still works.
        t.Path = assets.Find(System.IO.Path.GetFileName(name.Replace('\\', '/'))) ?? assets.Find(name);
        if (t.Path is null)
        {
            t.State = WorldTexture.Residency.Missing;
            Messages.Add($"texture not found: {name}");
            return t;
        }
        Start(t);
        return t;
    }

    void Start(WorldTexture t)
    {
        string path = t.Path!, name = t.Name;
        t.State = WorldTexture.Residency.Loading;
        t.Pending = BackgroundWork.Run(() =>
        {
            try { return Load(path); }
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
        Reloads++;
        Start(t);
    }

    /// <summary>Uploads finished decodes (all of them, waiting, when <paramref name="wait"/>; else steps until <paramref name="budgetMs"/> passes, at most <paramref name="max"/> textures started).</summary>
    public void Pump(bool wait, int max = 16, double budgetMs = 1.5)
    {
        int started = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (steps.Count > 0)
            {
                if (!wait && watch.Elapsed.TotalMilliseconds >= budgetMs) break;
                var step = steps.Dequeue();
                var one = System.Diagnostics.Stopwatch.StartNew();
                step();
                if (StreamLog && one.Elapsed.TotalMilliseconds > 3) Console.WriteLine($"slow texture step {step.Method.Name}: {one.Elapsed.TotalMilliseconds:0.0} ms");
                continue;
            }
            int found = -1;
            for (int i = 0; i < pending.Count && found < 0; i++)
                if (wait || pending[i].Pending!.IsCompleted) found = i;
            if (found < 0 || (!wait && (started >= max || watch.Elapsed.TotalMilliseconds >= budgetMs))) break;
            var t = pending[found];
            var data = t.Pending!.Result;
            if (data is not null) QueueUpload(t, data);
            else t.State = WorldTexture.Residency.Missing;
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

    /// <summary>Deletes textures that have been idle too long, or the least recently used ones while the cache is over its high-water mark. At most once a second.</summary>
    void Trim()
    {
        long now = Environment.TickCount64;
        if (now - lastTrim < 1000) return;
        lastTrim = now;
        bool pressure = ResidentBytes > HighWaterMb * 1048576;
        List<WorldTexture>? victims = null;
        foreach (var t in cache.Values)
        {
            if (t.State != WorldTexture.Residency.Resident) continue;
            double idle = (now - t.LastUsed) / 1000.0;
            if (idle > IdleSeconds || pressure && idle > PressureIdleSeconds) (victims ??= []).Add(t);
        }
        if (victims is null) return;
        victims.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        long lowWater = (long)(HighWaterMb * 1048576 * 0.75);
        foreach (var t in victims.Take(40))
        {
            bool old = (now - t.LastUsed) / 1000.0 > IdleSeconds;
            if (!old && ResidentBytes <= lowWater) break;
            Unload(t);
        }
    }

    void Unload(WorldTexture t)
    {
        gl.DeleteTexture(t.RawId);
        t.Id = 0;
        ResidentBytes -= t.Bytes;
        ResidentCount--;
        Unloads++;
        t.Bytes = 0;
        t.State = WorldTexture.Residency.Unloaded;
    }

    readonly Queue<Action> steps = new();
    const int SlabBytes = 512 << 10;

    /// <summary>
    /// MEITOU_UNCOMPRESSED_TEXTURES=1 decodes every texture to RGBA8 on the CPU and uploads that (the way it was before; uses 4 to 8 times the GPU memory
    /// of BC1/BC3 textures). Otherwise DDS textures in BC1, BC2 or BC3 with a full mip chain go to the GPU as they are stored (S3TC, universal on desktop GL).
    /// </summary>
    static readonly bool Uncompressed = Environment.GetEnvironmentVariable("MEITOU_UNCOMPRESSED_TEXTURES") == "1";

    static TextureData Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsReader.Magic)
        {
            var dds = DdsReader.Read(bytes);
            if (!Uncompressed && CanUploadCompressed(dds))
            {
                // Only the level the swizzle test looks at is decoded on the CPU.
                int level = Math.Max(Array.FindIndex(dds.Surfaces.ToArray(), s => s.Width <= 256 && s.Height <= 256), 0);
                return new TextureData(new LoadedTexture([DdsDecoder.Decode(dds, 0, level)], dds), dds);
            }
            return new TextureData(TextureLoader.FromDds(dds), null);
        }
        return new TextureData(new LoadedTexture([TextureLoader.LoadImage(bytes)], null), null);
    }

    static bool CanUploadCompressed(DdsFile dds) =>
        dds.Format is DdsFormat.Bc1 or DdsFormat.Bc2 or DdsFormat.Bc3 && !dds.IsCubemap && !dds.IsVolume && dds.ImageCount == 1 && dds.Width > 0 && dds.Height > 0
        && dds.MipCount == 1 + (int)Math.Log2(Math.Max(dds.Width, dds.Height)) && dds.Surfaces.Count == dds.MipCount;

    static readonly Lazy<byte[]> zeros = new(() => new byte[16 << 20]);

    /// <summary>
    /// A stored (S3TC) texture's upload: each level is allocated (from a block of zeros when it is big) and filled in slabs of whole block rows of about
    /// 512 KB. Same sampling state as the RGBA8 path; the mip chain is the file's own.
    /// </summary>
    void QueueCompressedUpload(WorldTexture t, TextureData data)
    {
        var dds = data.Compressed!;
        t.Swizzled = LooksSwizzled(data.Rgba.Levels[0]);
        var format = dds.Format switch { DdsFormat.Bc1 => InternalFormat.CompressedRgbaS3TCDxt1Ext, DdsFormat.Bc2 => InternalFormat.CompressedRgbaS3TCDxt3Ext, _ => InternalFormat.CompressedRgbaS3TCDxt5Ext };
        int blockBytes = dds.Format == DdsFormat.Bc1 ? 8 : 16;
        uint id = 0;
        steps.Enqueue(() =>
        {
            id = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, id);
        });
        foreach (var surface in dds.Surfaces)
        {
            var s = surface;
            int blocksPerRow = (s.Width + 3) / 4, blockRows = (s.Height + 3) / 4, rowBytes = blocksPerRow * blockBytes;
            int length = Math.Min(s.Length, rowBytes * blockRows);
            if (length <= SlabBytes || length > zeros.Value.Length)
            {
                steps.Enqueue(() =>
                {
                    gl.BindTexture(TextureTarget.Texture2D, id);
                    fixed (byte* p = &dds.Data[s.Offset]) gl.CompressedTexImage2D(TextureTarget.Texture2D, s.Level, format, (uint)s.Width, (uint)s.Height, 0, (uint)length, p);
                });
                continue;
            }
            steps.Enqueue(() =>
            {
                gl.BindTexture(TextureTarget.Texture2D, id);
                fixed (byte* z = zeros.Value) gl.CompressedTexImage2D(TextureTarget.Texture2D, s.Level, format, (uint)s.Width, (uint)s.Height, 0, (uint)length, z);
            });
            int rowsPerSlab = Math.Max(1, SlabBytes / rowBytes);
            for (int row = 0; row < blockRows; row += rowsPerSlab)
            {
                int r0 = row, n = Math.Min(rowsPerSlab, blockRows - row);
                steps.Enqueue(() =>
                {
                    gl.BindTexture(TextureTarget.Texture2D, id);
                    uint height = (uint)Math.Min(n * 4, s.Height - r0 * 4);
                    fixed (byte* p = &dds.Data[s.Offset + r0 * rowBytes])
                        gl.CompressedTexSubImage2D(TextureTarget.Texture2D, s.Level, 0, r0 * 4, (uint)s.Width, height, format, (uint)(n * rowBytes), p);
                });
            }
        }
        steps.Enqueue(() =>
        {
            gl.BindTexture(TextureTarget.Texture2D, id);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, dds.MipCount - 1);
            SamplingState(t);
            long bytes = dds.Surfaces.Sum(s => (long)s.Length);
            Resident(t, id, bytes);
        });
    }

    /// <summary>A texture's upload as steps of about 512 KB: allocate every level, fill them in slabs of rows, then mipmaps and sampling state.</summary>
    void QueueUpload(WorldTexture t, LoadedTexture tex)
    {
        t.Swizzled = LooksSwizzled(tex.Levels.FirstOrDefault(l => l.Width <= 256 && l.Height <= 256) ?? tex.Levels[0]);
        uint id = 0;
        steps.Enqueue(() =>
        {
            id = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, id);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            for (int level = 0; level < tex.Levels.Count; level++)
            {
                var img = tex.Levels[level];
                gl.TexImage2D(TextureTarget.Texture2D, level, InternalFormat.Rgba8, (uint)img.Width, (uint)img.Height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, null);
            }
        });
        for (int level = 0; level < tex.Levels.Count; level++)
        {
            var img = tex.Levels[level];
            int lv = level, rowBytes = img.Width * 4;
            int rows = Math.Max(1, SlabBytes / rowBytes);
            for (int y = 0; y < img.Height; y += rows)
            {
                int y0 = y, h = Math.Min(rows, img.Height - y);
                steps.Enqueue(() =>
                {
                    gl.BindTexture(TextureTarget.Texture2D, id);
                    gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                    fixed (byte* p = &img.Pixels[y0 * rowBytes])
                        gl.TexSubImage2D(TextureTarget.Texture2D, lv, 0, y0, (uint)img.Width, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                });
            }
        }
        steps.Enqueue(() =>
        {
            gl.BindTexture(TextureTarget.Texture2D, id);
            Finish(t, tex, id);
        });
    }

    void Finish(WorldTexture t, LoadedTexture tex, uint id)
    {
        long bytes = tex.Levels.Sum(l => (long)l.Width * l.Height * 4);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, tex.Levels.Count - 1);
        if (tex.Levels.Count == 1 || tex.Levels[^1].Width > 1 || tex.Levels[^1].Height > 1)
        {
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 1000);
            gl.GenerateMipmap(TextureTarget.Texture2D);
            bytes = bytes * 4 / 3;   // the full chain the driver made
        }
        SamplingState(t);
        Resident(t, id, bytes);
    }

    void SamplingState(WorldTexture t)
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        var wrap = t.Border ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        float[] transparent = [0, 0, 0, 0];
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, transparent.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, 8f); // max anisotropy (GL 4.6 / EXT)
        gl.GetError();
    }

    void Resident(WorldTexture t, uint id, long bytes)
    {
        t.Id = id;
        t.Bytes = bytes;
        t.State = WorldTexture.Residency.Resident;
        t.LastUsed = Environment.TickCount64;
        ResidentBytes += bytes;
        ResidentCount++;
    }

    void QueueUpload(WorldTexture t, TextureData data)
    {
        if (data.Compressed is not null) QueueCompressedUpload(t, data);
        else QueueUpload(t, data.Rgba);
    }

    static bool LooksSwizzled(RgbaImage image)
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
        foreach (var t in cache.Values) if (t.RawId != 0) gl.DeleteTexture(t.RawId);
    }
}

/// <summary>A decoded texture file: the RGBA8 levels (all of them, or for a stored S3TC texture only the one the swizzle test needs), and the DDS when its blocks are uploaded as they are.</summary>
sealed record TextureData(LoadedTexture Rgba, DdsFile? Compressed);
