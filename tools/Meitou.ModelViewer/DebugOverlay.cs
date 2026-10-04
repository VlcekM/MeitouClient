using Silk.NET.OpenGL;
using StbTrueTypeSharp;

namespace Meitou.ModelViewer;

/// <summary>
/// A text panel drawn over the frame (the key list, toggled with <c>?</c>). Text is rasterised from a monospace
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

    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUv;
        uniform vec2 uScreen;
        out vec2 vUv;
        void main()
        {
            vUv = aUv;
            gl_Position = vec4(aPos / uScreen * vec2(2.0, -2.0) + vec2(-1.0, 1.0), 0.0, 1.0);
        }
        """;

    const string Fragment = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uAtlas;
        uniform vec4 uColour;
        uniform int uSolid;
        void main()
        {
            float a = uSolid == 1 ? 1.0 : texture(uAtlas, vUv).r;
            fragColour = vec4(uColour.rgb, uColour.a * a);
        }
        """;

    readonly GL gl;
    readonly uint program, vao, vbo, atlas;
    readonly StbTrueType.stbtt_bakedchar[] glyphs = new StbTrueType.stbtt_bakedchar[CharCount];
    readonly List<float> text = [];
    readonly float lineHeight, charWidth;

    public bool Visible { get; set; }

    DebugOverlay(GL gl, byte[] font)
    {
        this.gl = gl;
        var pixels = new byte[AtlasSize * AtlasSize];
        fixed (byte* f = font)
        fixed (byte* p = pixels)
        fixed (StbTrueType.stbtt_bakedchar* g = glyphs)
            StbTrueType.stbtt_BakeFontBitmap(f, 0, PixelHeight, p, AtlasSize, AtlasSize, FirstChar, CharCount, g);
        lineHeight = PixelHeight * 1.25f;
        charWidth = glyphs['M' - FirstChar].xadvance;

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
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 16, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 16, (void*)8);
        gl.BindVertexArray(0);
    }

    /// <summary>The overlay, or null when no monospace font is found.</summary>
    public static DebugOverlay? TryCreate(GL gl)
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

    /// <summary>Draws a titled panel listing <paramref name="lines"/> in as many columns as the height needs.</summary>
    public void Draw(int width, int height, string title, IReadOnlyList<string> lines)
    {
        if (!Visible || width <= 0 || height <= 0) return;
        const float margin = 16, pad = 12, gap = 28;
        int rowsFit = Math.Max((int)((height - 2 * margin - 2 * pad - lineHeight * 1.5f) / lineHeight), 1);
        int columns = (lines.Count + rowsFit - 1) / rowsFit;
        int rows = (lines.Count + columns - 1) / Math.Max(columns, 1);
        float columnWidth = (lines.Count == 0 ? 0 : lines.Max(l => l.Length)) * charWidth;
        float panelW = Math.Max(columns * columnWidth + (columns - 1) * gap, title.Length * charWidth) + 2 * pad;
        float panelH = (rows + 1.5f) * lineHeight + 2 * pad;

        text.Clear();
        AddText(title, margin + pad, margin + pad);
        for (int i = 0; i < lines.Count; i++)
            AddText(lines[i], margin + pad + i / rows * (columnWidth + gap), margin + pad + (i % rows + 1.5f) * lineHeight);

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

        // Panel background.
        Span<float> panel = stackalloc float[24];
        Quad(panel, margin, margin, margin + panelW, margin + panelH, 0, 0, 0, 0);
        fixed (float* p = panel) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(panel.Length * 4), p, BufferUsageARB.StreamDraw);
        gl.Uniform1(gl.GetUniformLocation(program, "uSolid"), 1);
        gl.Uniform4(gl.GetUniformLocation(program, "uColour"), 0.05f, 0.05f, 0.06f, 0.78f);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // Text.
        var data = text.ToArray();
        fixed (float* p = data) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * 4), p, BufferUsageARB.StreamDraw);
        gl.Uniform1(gl.GetUniformLocation(program, "uSolid"), 0);
        gl.Uniform4(gl.GetUniformLocation(program, "uColour"), 0.92f, 0.92f, 0.88f, 1f);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(data.Length / 4));

        gl.BindVertexArray(0);
        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.DepthTest);
    }

    void AddText(string s, float x, float top)
    {
        float baseline = top + PixelHeight;
        Span<float> quad = stackalloc float[24];
        foreach (char c in s)
        {
            int index = c - FirstChar;
            if (index < 0 || index >= CharCount) index = '?' - FirstChar;
            var g = glyphs[index];
            float x0 = MathF.Round(x + g.xoff), y0 = MathF.Round(baseline + g.yoff);
            Quad(quad, x0, y0, x0 + (g.x1 - g.x0), y0 + (g.y1 - g.y0),
                g.x0 / (float)AtlasSize, g.y0 / (float)AtlasSize, g.x1 / (float)AtlasSize, g.y1 / (float)AtlasSize);
            foreach (var v in quad) text.Add(v);
            x += g.xadvance;
        }
    }

    static void Quad(Span<float> o, float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1)
    {
        ReadOnlySpan<float> v = [x0, y0, u0, v0, x1, y0, u1, v0, x1, y1, u1, v1, x0, y0, u0, v0, x1, y1, u1, v1, x0, y1, u0, v1];
        v.CopyTo(o);
    }

    public void Dispose()
    {
        gl.DeleteTexture(atlas);
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(program);
    }
}
