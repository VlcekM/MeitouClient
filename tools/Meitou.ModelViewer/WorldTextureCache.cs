using Meitou.Data.Textures;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>A texture of the world view's objects: 0 until its file is decoded (in the background) and uploaded.</summary>
public sealed class WorldTexture
{
    public uint Id;
    /// <summary>Normal map stored with X in alpha and Y in green (the model viewer's heuristic, docs/viewer.md).</summary>
    public bool Swizzled;
    internal Task<LoadedTexture?>? Pending;
    internal bool Border;
}

/// <summary>
/// Texture cache for the world view: files are decoded on worker threads and uploaded on the GL thread by
/// <see cref="Pump"/>, so loading a town's few hundred textures uses every core and never stalls a frame for long.
/// Same lookup, sampling and swizzle detection as <see cref="Renderer"/>.
/// </summary>
public sealed unsafe class WorldTextureCache(GL gl, AssetLocator assets) : IDisposable
{
    readonly Dictionary<string, WorldTexture> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly List<WorldTexture> pending = [];

    public List<string> Messages { get; } = [];
    public int PendingCount => pending.Count + (steps.Count > 0 ? 1 : 0);

    public WorldTexture? Get(string? name, bool border)
    {
        if (name is null) return null;
        string key = border ? name + "|border" : name;
        if (cache.TryGetValue(key, out var t)) return t;
        cache[key] = t = new WorldTexture { Border = border };
        // The game reduces texture fields to the bare file name (runtime-materials.md); a path still works.
        var path = assets.Find(Path.GetFileName(name.Replace('\\', '/'))) ?? assets.Find(name);
        if (path is null)
        {
            Messages.Add($"texture not found: {name}");
            return t;
        }
        t.Pending = Task.Run(() =>
        {
            try { return TextureLoader.LoadFile(path); }
            catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
            {
                lock (Messages) Messages.Add($"texture {name}: {e.Message}");
                return null;
            }
        });
        pending.Add(t);
        return t;
    }

    /// <summary>Uploads finished decodes (all of them, waiting, when <paramref name="wait"/>; else steps until <paramref name="budgetMs"/> passes, at most <paramref name="max"/> textures started).</summary>
    public void Pump(bool wait, int max = 16, double budgetMs = double.MaxValue)
    {
        int started = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (steps.Count > 0)
            {
                if (!wait && watch.Elapsed.TotalMilliseconds >= budgetMs) return;
                steps.Dequeue()();
                continue;
            }
            int found = -1;
            for (int i = 0; i < pending.Count && found < 0; i++)
                if (wait || pending[i].Pending!.IsCompleted) found = i;
            if (found < 0 || (!wait && (started >= max || watch.Elapsed.TotalMilliseconds >= budgetMs))) return;
            var t = pending[found];
            var tex = t.Pending!.Result;
            if (tex is not null) QueueUpload(t, tex);
            t.Pending = null;
            pending.RemoveAt(found);
            started++;
        }
    }

    readonly Queue<Action> steps = new();
    const int SlabBytes = 1 << 20;

    /// <summary>A texture's upload as steps of about 1 MB: allocate every level, fill them in slabs of rows, then mipmaps and sampling state.</summary>
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
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, tex.Levels.Count - 1);
        if (tex.Levels.Count == 1 || tex.Levels[^1].Width > 1 || tex.Levels[^1].Height > 1)
        {
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 1000);
            gl.GenerateMipmap(TextureTarget.Texture2D);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        var wrap = t.Border ? TextureWrapMode.ClampToBorder : TextureWrapMode.Repeat;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        float[] transparent = [0, 0, 0, 0];
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, transparent.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, 8f); // max anisotropy (GL 4.6 / EXT)
        gl.GetError();
        t.Id = id;
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
        foreach (var t in cache.Values) if (t.Id != 0) gl.DeleteTexture(t.Id);
    }
}
