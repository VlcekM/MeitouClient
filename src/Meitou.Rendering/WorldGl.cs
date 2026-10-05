using System.Numerics;
using Silk.NET.OpenGL;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>Small OpenGL helpers for the world view.</summary>
static unsafe class WorldGl
{
    public static uint Program(IGl gl, string vertex, string fragment)
    {
        uint vs = Compile(gl, ShaderType.VertexShader, vertex), fs = Compile(gl, ShaderType.FragmentShader, fragment);
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("Shader link failed: " + gl.GetProgramInfoLog(program));
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        SkyRenderer.AssignSamplerUnits(gl, program);   // the atmosphere's cube samplers off unit 0
        ShadowShaders.Bind(gl, program);               // the shadow blocks' binding points and the shadow map's unit, when it has them
        return program;
    }

    static uint Compile(IGl gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + gl.GetShaderInfoLog(shader));
        return shader;
    }

    /// <summary>System.Numerics matrices are row-major with row vectors; GL reads them column-major, the column-vector form.</summary>
    public static void Matrix(IGl gl, int location, Matrix4x4 m) => gl.UniformMatrix4(location, 1, false, (float*)&m);

    /// <summary>A 2D RGBA8 texture from top-first rows, with mipmaps.</summary>
    public static uint Texture2D(IGl gl, int width, int height, byte[] rgba, bool repeat, bool mipmaps = true)
    {
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan());
        if (mipmaps) gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        var wrap = repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        return id;
    }
}
