using Silk.NET.OpenGL;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// The subset of OpenGL 3.3 the world renderers use, with Silk.NET's signatures and enums, so a renderer is ported by its type
/// alone. Implemented by <see cref="GlPassthrough"/> (forwards to OpenGL, so the pictures are OpenGL's) and by the Vulkan
/// backend (a translation of these calls; see docs/engine.md "Backend interface"). Add a member here only together with its
/// Vulkan translation.
/// </summary>
public unsafe interface IGl
{
    // State
    void Enable(EnableCap cap);
    void Disable(EnableCap cap);
    void Viewport(int x, int y, uint width, uint height);
    void Scissor(int x, int y, uint width, uint height);
    void DepthMask(bool flag);
    void DepthFunc(DepthFunction func);
    void ColorMask(bool red, bool green, bool blue, bool alpha);
    void BlendFunc(BlendingFactor sfactor, BlendingFactor dfactor);
    void CullFace(TriangleFace mode);
    void FrontFace(FrontFaceDirection mode);
    void PolygonMode(TriangleFace face, PolygonMode mode);
    void PolygonOffset(float factor, float units);
    void ClearColor(float red, float green, float blue, float alpha);
    void ClearDepth(double depth);
    void Clear(ClearBufferMask mask);
    void PixelStore(PixelStoreParameter pname, int param);
    GLEnum GetError();
    void GetInteger(GLEnum pname, out int data);
    void GetInteger(GetPName pname, out int data);
    void GetInteger(GetPName pname, int* data);
    void Finish();

    // Shaders and programs
    uint CreateShader(ShaderType type);
    void ShaderSource(uint shader, string source);
    void CompileShader(uint shader);
    void GetShader(uint shader, ShaderParameterName pname, out int @params);
    string GetShaderInfoLog(uint shader);
    void DeleteShader(uint shader);
    uint CreateProgram();
    void AttachShader(uint program, uint shader);
    void LinkProgram(uint program);
    void GetProgram(uint program, ProgramPropertyARB pname, out int @params);
    string GetProgramInfoLog(uint program);
    void DeleteProgram(uint program);
    void UseProgram(uint program);
    int GetUniformLocation(uint program, string name);
    uint GetUniformBlockIndex(uint program, string uniformBlockName);
    void UniformBlockBinding(uint program, uint uniformBlockIndex, uint uniformBlockBinding);
    void Uniform1(int location, int v0);
    void Uniform1(int location, float v0);
    void Uniform2(int location, float v0, float v1);
    void Uniform3(int location, float v0, float v1, float v2);
    void Uniform4(int location, float v0, float v1, float v2, float v3);
    void Uniform1(int location, uint count, float* value);
    void Uniform2(int location, uint count, float* value);
    void Uniform3(int location, uint count, float* value);
    void Uniform4(int location, uint count, float* value);
    void UniformMatrix4(int location, uint count, bool transpose, float* value);

    // Buffers and vertex arrays
    uint GenBuffer();
    void DeleteBuffer(uint buffer);
    void BindBuffer(BufferTargetARB target, uint buffer);
    void BindBufferBase(BufferTargetARB target, uint index, uint buffer);
    void BufferData(BufferTargetARB target, nuint size, void* data, BufferUsageARB usage);
    void BufferData<T>(BufferTargetARB target, ReadOnlySpan<T> data, BufferUsageARB usage) where T : unmanaged;
    void BufferSubData(BufferTargetARB target, nint offset, nuint size, void* data);
    uint GenVertexArray();
    void DeleteVertexArray(uint array);
    void BindVertexArray(uint array);
    void EnableVertexAttribArray(uint index);
    void VertexAttribPointer(uint index, int size, VertexAttribPointerType type, bool normalized, uint stride, void* pointer);
    void VertexAttribIPointer(uint index, int size, VertexAttribIType type, uint stride, void* pointer);
    void VertexAttribDivisor(uint index, uint divisor);

    // Draws
    void DrawArrays(PrimitiveType mode, int first, uint count);
    void DrawArraysInstanced(PrimitiveType mode, int first, uint count, uint instancecount);
    void DrawElements(PrimitiveType mode, uint count, DrawElementsType type, void* indices);
    void DrawElementsInstanced(PrimitiveType mode, uint count, DrawElementsType type, void* indices, uint instancecount);

    // Textures
    uint GenTexture();
    void DeleteTexture(uint texture);
    void ActiveTexture(TextureUnit texture);
    void BindTexture(TextureTarget target, uint texture);
    void TexParameter(TextureTarget target, TextureParameterName pname, int param);
    void TexParameter(TextureTarget target, TextureParameterName pname, float param);
    void TexParameter(TextureTarget target, TextureParameterName pname, ReadOnlySpan<float> @params);
    void TexImage2D(TextureTarget target, int level, InternalFormat internalformat, uint width, uint height, int border, PixelFormat format, PixelType type, void* pixels);
    void TexImage2D<T>(TextureTarget target, int level, InternalFormat internalformat, uint width, uint height, int border, PixelFormat format, PixelType type, ReadOnlySpan<T> pixels) where T : unmanaged;
    void TexSubImage2D(TextureTarget target, int level, int xoffset, int yoffset, uint width, uint height, PixelFormat format, PixelType type, void* pixels);
    void TexSubImage2D<T>(TextureTarget target, int level, int xoffset, int yoffset, uint width, uint height, PixelFormat format, PixelType type, ReadOnlySpan<T> pixels) where T : unmanaged;
    void CompressedTexImage2D(TextureTarget target, int level, InternalFormat internalformat, uint width, uint height, int border, uint imageSize, void* data);
    void CompressedTexImage3D(TextureTarget target, int level, InternalFormat internalformat, uint width, uint height, uint depth, int border, uint imageSize, void* data);
    void CompressedTexSubImage2D(TextureTarget target, int level, int xoffset, int yoffset, uint width, uint height, InternalFormat format, uint imageSize, void* data);
    void CompressedTexSubImage3D(TextureTarget target, int level, int xoffset, int yoffset, int zoffset, uint width, uint height, uint depth, InternalFormat format, uint imageSize, void* data);
    void GenerateMipmap(TextureTarget target);

    // Framebuffers
    uint GenFramebuffer();
    void DeleteFramebuffer(uint framebuffer);
    void BindFramebuffer(FramebufferTarget target, uint framebuffer);
    void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textarget, uint texture, int level);
    void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget renderbuffertarget, uint renderbuffer);
    GLEnum CheckFramebufferStatus(FramebufferTarget target);
    void DrawBuffer(DrawBufferMode buf);
    void ReadBuffer(ReadBufferMode src);
    void BlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, ClearBufferMask mask, BlitFramebufferFilter filter);
    void ReadPixels(int x, int y, uint width, uint height, PixelFormat format, PixelType type, void* pixels);
    void ReadPixels<T>(int x, int y, uint width, uint height, PixelFormat format, PixelType type, Span<T> pixels) where T : unmanaged;
    uint GenRenderbuffer();
    void DeleteRenderbuffer(uint renderbuffer);
    void BindRenderbuffer(RenderbufferTarget target, uint renderbuffer);
    void RenderbufferStorage(RenderbufferTarget target, InternalFormat internalformat, uint width, uint height);
    void RenderbufferStorageMultisample(RenderbufferTarget target, uint samples, InternalFormat internalformat, uint width, uint height);

    // Queries
    uint GenQuery();
    void DeleteQuery(uint id);
    void QueryCounter(uint id, QueryCounterTarget target);
    void BeginQuery(QueryTarget target, uint id);
    void EndQuery(QueryTarget target);
    void GetQueryObject(uint id, QueryObjectParameterName pname, out int @params);
    void GetQueryObject(uint id, QueryObjectParameterName pname, out ulong @params);
}
