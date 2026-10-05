using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;

namespace Meitou.Tests.Vulkan;

/// <summary>The GL-on-Vulkan translation against GL's own rules: what a GL program would read back, row 0 at the bottom.</summary>
public unsafe class VkGlTests
{
    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    static void ExpectClean(VulkanDevice d) =>
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));

    const int W = 64, H = 32;

    static (uint Fbo, uint Colour) Target(IGl gl)
    {
        uint fbo = gl.GenFramebuffer(), colour = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, W, H);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        gl.Viewport(0, 0, W, H);
        return (fbo, colour);
    }

    static byte[] Read(IGl gl)
    {
        var pixels = new byte[W * H * 4];
        gl.ReadPixels<byte>(0, 0, W, H, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        return pixels;
    }

    static (byte R, byte G, byte B, byte A) At(byte[] p, int x, int y)
    {
        int i = (y * W + x) * 4;
        return (p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    // The clear colour (1, 0.5, 0, 1): 0.5 may convert to 127 or 128.
    static (byte R, byte B, byte A) Orange((byte R, byte G, byte B, byte A) c)
    {
        Assert.InRange(c.G, 127, 128);
        return (c.R, c.B, c.A);
    }

    [Fact]
    public void Clear_reads_back()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            Target(gl);
            gl.ClearColor(1, 0.5f, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            var p = Read(gl);
            Assert.Equal((255, 0, 255), Orange(At(p, 0, 0)));
            Assert.Equal((255, 0, 255), Orange(At(p, W - 1, H - 1)));

            // A scissored clear touches only its rectangle (GL's lower-left origin).
            gl.Enable(EnableCap.ScissorTest);
            gl.Scissor(0, 0, 8, 4);
            gl.ClearColor(0, 0, 1, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Disable(EnableCap.ScissorTest);
            p = Read(gl);
            Assert.Equal((0, 0, 255, 255), At(p, 0, 0));
            Assert.Equal((0, 0, 255, 255), At(p, 7, 3));
            Assert.Equal((255, 0, 255), Orange(At(p, 8, 0)));
            Assert.Equal((255, 0, 255), Orange(At(p, 0, 4)));
        }
        ExpectClean(d!);
    }

    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec3 aColour;
        uniform vec2 uOffset;
        out vec3 vColour;
        void main() { vColour = aColour; gl_Position = vec4(aPos + uOffset, 0.5, 1.0); }
        """;

    const string Fragment = """
        #version 330 core
        in vec3 vColour;
        uniform float uScale;
        out vec4 fragColour;
        void main() { fragColour = vec4(vColour * uScale, 1.0); }
        """;

    static uint Program(IGl gl)
    {
        uint vs = gl.CreateShader(ShaderType.VertexShader), fs = gl.CreateShader(ShaderType.FragmentShader);
        gl.ShaderSource(vs, Vertex);
        gl.ShaderSource(fs, Fragment);
        gl.CompileShader(vs);
        gl.CompileShader(fs);
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int ok);
        Assert.True(ok == 1, gl.GetProgramInfoLog(program));
        return program;
    }

    /// <summary>A counter-clockwise (GL front-facing) triangle over the lower-left half, drawn with back faces culled: it must show,
    /// and row 0 of the read-back must be the bottom of the picture.</summary>
    [Fact]
    public void Triangle_follows_GL_conventions()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            Target(gl);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            uint program = Program(gl);
            float[] vertices =
            [
                -1, -1, 1, 0, 0,
                 1, -1, 1, 0, 0,
                -1,  1, 1, 0, 0,
            ];
            uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer();
            gl.BindVertexArray(vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 20, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 20, (void*)8);
            gl.UseProgram(program);
            gl.Uniform2(gl.GetUniformLocation(program, "uOffset"), 0f, 0f);
            gl.Uniform1(gl.GetUniformLocation(program, "uScale"), 1f);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);
            gl.FrontFace(FrontFaceDirection.Ccw);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            var p = Read(gl);
            Assert.Equal((255, 0, 0, 255), At(p, 1, 1));            // bottom left: inside
            Assert.Equal((0, 0, 0, 255), At(p, W - 2, H - 2));     // top right: outside

            // Clockwise winding with back faces culled draws nothing.
            gl.FrontFace(FrontFaceDirection.CW);
            gl.Uniform1(gl.GetUniformLocation(program, "uScale"), 0.5f);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            p = Read(gl);
            Assert.Equal((255, 0, 0, 255), At(p, 1, 1));

            // Uniform changes between draws apply to the draws after them only.
            gl.Disable(EnableCap.CullFace);
            gl.Uniform2(gl.GetUniformLocation(program, "uOffset"), 1f, 1f);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Uniform1(gl.GetUniformLocation(program, "uScale"), 1f);
            gl.Uniform2(gl.GetUniformLocation(program, "uOffset"), 0f, 0f);
            p = Read(gl);
            Assert.InRange(At(p, W - 2, H - 2).R, 127, 128);   // the offset triangle, at half scale
            Assert.Equal((255, 0, 0, 255), At(p, 1, 1));
        }
        ExpectClean(d!);
    }

    /// <summary>A depth texture bound to a plain (non-shadow) sampler reads its depth value, as in GL (SSAO and the motion vectors do).</summary>
    [Fact]
    public void Depth_texture_reads_through_a_plain_sampler()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            uint depth = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, depth);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, W, H, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            uint depthFbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, depthFbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);
            gl.Viewport(0, 0, W, H);
            gl.ClearDepth(0.25);
            gl.Clear(ClearBufferMask.DepthBufferBit);

            Target(gl);
            uint vs = gl.CreateShader(ShaderType.VertexShader), fs = gl.CreateShader(ShaderType.FragmentShader);
            gl.ShaderSource(vs, """
                #version 330 core
                void main() { vec2 p = vec2((gl_VertexID & 1) * 4.0 - 1.0, (gl_VertexID & 2) * 2.0 - 1.0); gl_Position = vec4(p, 0.0, 1.0); }
                """);
            gl.ShaderSource(fs, """
                #version 330 core
                uniform sampler2D uDepth;
                out vec4 fragColour;
                void main() { fragColour = vec4(texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r, 0.0, 0.0, 1.0); }
                """);
            gl.CompileShader(vs);
            gl.CompileShader(fs);
            uint program = gl.CreateProgram();
            gl.AttachShader(program, vs);
            gl.AttachShader(program, fs);
            gl.LinkProgram(program);
            gl.UseProgram(program);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, depth);
            gl.Uniform1(gl.GetUniformLocation(program, "uDepth"), 0);
            gl.BindVertexArray(gl.GenVertexArray());
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Assert.InRange(At(Read(gl), W / 2, H / 2).R, 63, 65);
        }
        ExpectClean(d!);
    }
}
