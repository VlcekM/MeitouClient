using System.Numerics;
using StbTrueTypeSharp;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// Text panels drawn over the frame (the viewer's frame statistics and key list). Text is rasterised from a monospace
/// system font with stb_truetype; without one the overlay stays off. Draws into the bound framebuffer after
/// everything else, so screenshots taken before it don't include it.
/// </summary>
public sealed unsafe class DebugOverlay : IDisposable
{
    const int FirstChar = 32, CharCount = 95, AtlasSize = 512;
    const float PixelHeight = 16;

    static readonly string[] FontCandidates =
    [
        @"C:\Windows\Fonts\consola.ttf", @"C:\Windows\Fonts\CascadiaMono.ttf", @"C:\Windows\Fonts\cour.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", "/usr/share/fonts/TTF/DejaVuSansMono.ttf",
        "/System/Library/Fonts/Menlo.ttc",
    ];

    // Vertex: position (pixels), uv (u < 0: solid), colour.
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColour;
        uniform vec2 uScreen;
        out vec2 vUv;
        out vec4 vColour;
        void main()
        {
            vUv = aUv;
            vColour = aColour;
            gl_Position = vec4(aPos / uScreen * vec2(2.0, -2.0) + vec2(-1.0, 1.0), 0.0, 1.0);
        }
        """;

    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColour;
        out vec4 fragColour;
        uniform sampler2D uAtlas;
        void main()
        {
            float a = vUv.x < 0.0 ? 1.0 : texture(uAtlas, vUv).r;
            fragColour = vec4(vColour.rgb, vColour.a * a);
        }
        """;

    readonly IGl gl;
    readonly uint program, vao, vbo, atlas;
    readonly StbTrueType.stbtt_bakedchar[] glyphs = new StbTrueType.stbtt_bakedchar[CharCount];
    readonly List<float> batch = [];
    const int Stride = 8;

    public float LineHeight { get; }
    public float CharWidth { get; }

    public bool Visible { get; set; }

    DebugOverlay(IGl gl, byte[] font)
    {
        this.gl = gl;
        var pixels = new byte[AtlasSize * AtlasSize];
        fixed (byte* f = font)
        fixed (byte* p = pixels)
        fixed (StbTrueType.stbtt_bakedchar* g = glyphs)
            StbTrueType.stbtt_BakeFontBitmap(f, 0, PixelHeight, p, AtlasSize, AtlasSize, FirstChar, CharCount, g);
        LineHeight = PixelHeight * 1.25f;
        CharWidth = glyphs['M' - FirstChar].xadvance;

        atlas = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, atlas);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = pixels)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, AtlasSize, AtlasSize, 0, PixelFormat.Red, PixelType.UnsignedByte, p);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        program = WorldGl.Program(gl, Vertex, Fragment);
        vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Stride * 4, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, Stride * 4, (void*)8);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, Stride * 4, (void*)16);
        gl.BindVertexArray(0);
    }

    /// <summary>The overlay, or null when no monospace font is found.</summary>
    public static DebugOverlay? TryCreate(IGl gl)
    {
        var path = FontCandidates.FirstOrDefault(File.Exists);
        return path is null ? null : new DebugOverlay(gl, File.ReadAllBytes(path));
    }

    /// <summary>
    /// The items of a usage text's <c>Keys:</c> section, one per line ("T textures", "Esc quit"...): split at commas and
    /// semicolons, except inside parentheses.
    /// </summary>
    public static List<string> KeyItems(string usage)
    {
        int at = usage.IndexOf("Keys:", StringComparison.Ordinal);
        var items = new List<string>();
        if (at < 0) return items;
        var keys = string.Join(' ', usage[(at + 5)..].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        int depth = 0, start = 0;
        for (int i = 0; i <= keys.Length; i++)
        {
            char c = i < keys.Length ? keys[i] : ',';
            // "Esc quit. F1 post off": a full stop before the next line's first key ends an item too.
            if (c == '.' && i + 2 < keys.Length && keys[i + 1] == ' ' && char.IsUpper(keys[i + 2])) c = ',';
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && (c == ',' || c == ';'))
            {
                // ", / . time of day": a comma right after a separator is a key, not another separator.
                if (c == ',' && i < keys.Length && keys[start..i].Trim().Length == 0) continue;
                var item = keys[start..i].Trim().TrimEnd('.');
                if (item.Length > 0) items.Add(item);
                start = i + 1;
            }
        }
        return items;
    }

    public static readonly Vector4 PanelColour = new(0.05f, 0.05f, 0.06f, 0.78f), TextColour = new(0.92f, 0.92f, 0.88f, 1f);

    /// <summary>Draws a titled panel listing <paramref name="lines"/> when <see cref="Visible"/>; see <see cref="Panel"/>.</summary>
    public float Draw(int width, int height, string title, IReadOnlyList<string> lines, float top = 0) =>
        Visible ? Panel(width, height, title, lines, top) : top;

    /// <summary>
    /// Draws a titled panel at the left edge, below <paramref name="top"/>, listing <paramref name="lines"/> in as many
    /// columns as the height left needs; returns the panel's bottom (where a next panel can start).
    /// </summary>
    public float Panel(int width, int height, string title, IReadOnlyList<string> lines, float top = 0)
    {
        if (width <= 0 || height <= 0) return top;
        const float margin = 16, pad = 12, gap = 28;
        float y = top + margin;
        int rowsFit = Math.Max((int)((height - y - margin - 2 * pad - LineHeight * 1.5f) / LineHeight), 1);
        int columns = (lines.Count + rowsFit - 1) / rowsFit;
        int rows = (lines.Count + columns - 1) / Math.Max(columns, 1);
        float columnWidth = (lines.Count == 0 ? 0 : lines.Max(l => l.Length)) * CharWidth;
        float panelW = Math.Max(columns * columnWidth + (columns - 1) * gap, title.Length * CharWidth) + 2 * pad;
        float panelH = (rows + 1.5f) * LineHeight + 2 * pad;

        Rect(margin, y, margin + panelW, y + panelH, PanelColour);
        Text(title, margin + pad, y + pad, TextColour);
        for (int i = 0; i < lines.Count; i++)
            Text(lines[i], margin + pad + i / rows * (columnWidth + gap), y + pad + (i % rows + 1.5f) * LineHeight, TextColour);
        Flush(width, height);
        return y + panelH;
    }

    /// <summary>Queues a solid rectangle (pixels, top-left origin) for the next <see cref="Flush"/>.</summary>
    public void Rect(float x0, float y0, float x1, float y1, Vector4 colour) => Quad(x0, y0, x1, y1, -1, 0, -1, 0, colour);

    /// <summary>Queues a line of text whose top is at <paramref name="top"/>; returns its width in pixels.</summary>
    public float Text(string s, float x, float top, Vector4 colour)
    {
        float start = x, baseline = top + PixelHeight;
        foreach (char c in s)
        {
            int index = c - FirstChar;
            if (index < 0 || index >= CharCount) index = '?' - FirstChar;
            var g = glyphs[index];
            float x0 = MathF.Round(x + g.xoff), y0 = MathF.Round(baseline + g.yoff);
            Quad(x0, y0, x0 + (g.x1 - g.x0), y0 + (g.y1 - g.y0),
                g.x0 / (float)AtlasSize, g.y0 / (float)AtlasSize, g.x1 / (float)AtlasSize, g.y1 / (float)AtlasSize, colour);
            x += g.xadvance;
        }
        return x - start;
    }

    /// <summary>Draws everything queued since the last flush over the bound framebuffer.</summary>
    public void Flush(int width, int height)
    {
        if (batch.Count == 0 || width <= 0 || height <= 0) { batch.Clear(); return; }
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.UseProgram(program);
        gl.Uniform2(gl.GetUniformLocation(program, "uScreen"), (float)width, height);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, atlas);
        gl.Uniform1(gl.GetUniformLocation(program, "uAtlas"), 0);
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var data = batch.ToArray();
        fixed (float* p = data) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * 4), p, BufferUsageARB.StreamDraw);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(data.Length / Stride));
        gl.BindVertexArray(0);
        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.DepthTest);
        batch.Clear();
    }

    /// <summary>Queues a solid line segment <paramref name="thickness"/> pixels wide.</summary>
    public void Line(float x0, float y0, float x1, float y1, float thickness, Vector4 colour)
    {
        var d = new Vector2(x1 - x0, y1 - y0);
        float length = d.Length();
        if (length < 1e-3f) return;
        var n = new Vector2(-d.Y, d.X) / length * (thickness * 0.5f);
        Emit(x0 + n.X, y0 + n.Y, -1, 0, colour); Emit(x1 + n.X, y1 + n.Y, -1, 0, colour); Emit(x1 - n.X, y1 - n.Y, -1, 0, colour);
        Emit(x0 + n.X, y0 + n.Y, -1, 0, colour); Emit(x1 - n.X, y1 - n.Y, -1, 0, colour); Emit(x0 - n.X, y0 - n.Y, -1, 0, colour);
    }

    void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, Vector4 c)
    {
        Emit(x0, y0, u0, v0, c); Emit(x1, y0, u1, v0, c); Emit(x1, y1, u1, v1, c);
        Emit(x0, y0, u0, v0, c); Emit(x1, y1, u1, v1, c); Emit(x0, y1, u0, v1, c);
    }

    void Emit(float x, float y, float u, float v, Vector4 c)
    {
        batch.Add(x); batch.Add(y); batch.Add(u); batch.Add(v); batch.Add(c.X); batch.Add(c.Y); batch.Add(c.Z); batch.Add(c.W);
    }

    public void Dispose()
    {
        gl.DeleteTexture(atlas);
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(program);
    }
}
