using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Shaders;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using Silk.NET.SPIRV;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The game's sky on Metal: the engine's own GLSL, compiled by the engine's own compiler (shaderc) and cross-compiled to
/// MSL in process, drawn with the engine's own uniform block (the std140 layout the engine's reflection reports).
/// The fullscreen triangle comes from gl_VertexID: no vertex buffers, no index buffer.
/// The simple sky (the game's colour model, no textures); the physical sky needs the game's DDS textures and the
/// atmosphere's own uniforms.
/// </summary>
public sealed class MetalSky
{
    /// <summary>
    /// The sky's render pipeline (BGRA8 colour, no depth).
    /// </summary>
    readonly MTLRenderPipelineState _pipeline;
    /// <summary>
    /// The shared buffer the uniform block is copied into for each draw.
    /// </summary>
    readonly MTLBuffer _uniforms;
    /// <summary>
    /// The fragment shader's default uniform block on the CPU.
    /// </summary>
    readonly MetalUniformBlock _block;
    /// <summary>
    /// The fragment buffer index the uniform block binds at.
    /// </summary>
    readonly int _binding;

    /// <summary>
    /// Builds the sky's pipeline from the engine's GLSL (a vertex shader and a fragment shader).
    /// </summary>
    public MetalSky(MTLDevice device, string vertexGlsl, string fragmentGlsl)
    {
        // The fragment stage's bindings start at 0: Metal gives the stages their own binding tables, so the Vulkan scheme
        // (the fragment stage at 32, to keep it apart from the vertex stage) is not needed here.
        // No DepthReversed: Metal's scene depth is conventional (DepthConvention).
        var compiler = new GlslProgramCompiler(new ShaderCompileOptions { FragmentBindingBase = 0, UseDiskCache = false, UseMemoryCache = false });
        var program = compiler.Compile(vertexGlsl, fragmentGlsl);

        var vertex = Library(device, MslTranslator.Translate(program.VertexSpirv, "vertex_main", ExecutionModel.Vertex));
        var fragment = Library(device, MslTranslator.Translate(program.FragmentSpirv, "fragment_main", ExecutionModel.Fragment));

        var descriptor = new MTLRenderPipelineDescriptor(
            new ObjectiveCClass(ObjectiveC.objc_getClass("MTLRenderPipelineDescriptor")).AllocInit());
        descriptor.VertexFunction = vertex.NewFunction((NSString)"vertex_main");
        descriptor.FragmentFunction = fragment.NewFunction((NSString)"fragment_main");
        var attachments = descriptor.ColorAttachments;
        var attachment = attachments.Object(0);
        attachment.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        attachments.SetObject(attachment, 0);
        NSError error = default;
        _pipeline = device.NewRenderPipelineState(descriptor, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"the sky's pipeline failed: {error.LocalizedDescription}");

        var info = program.Fragment.DefaultBlock ?? throw new InvalidOperationException("the sky's fragment shader has no uniform block");
        _binding = info.Binding;
        _block = new MetalUniformBlock(program.Fragment, info.Size);
        _uniforms = device.NewBuffer((ulong)_block.Size, MTLResourceOptions.ResourceStorageModeShared);
    }

    /// <summary>
    /// Draws the sky into an open render pass.
    /// </summary>
    public void Render(MTLRenderCommandEncoder encoder, in Matrix4x4 inverseViewProjection, SkyColours colours)
    {
        _block.Set("uInverseViewProjection", inverseViewProjection);
        _block.Set("uSkySun", colours.Sun);
        _block.Set("uSkyZenith", colours.Zenith);
        _block.Set("uSkyHorizon", colours.Horizon);
        _block.Set("uSkySunColour", colours.SunColour);
        _block.Set("uSkyTwilight", colours.Twilight);
        Marshal.Copy(_block.Bytes, 0, _uniforms.Contents, _block.Size);
        encoder.SetRenderPipelineState(_pipeline);
        encoder.SetFragmentBuffer(_uniforms, 0, (ulong)_binding);
        encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
    }

    /// <summary>
    /// A Metal library compiled from <paramref name="msl"/>; throws when it does not compile.
    /// </summary>
    static MTLLibrary Library(MTLDevice device, string msl)
    {
        NSError error = default;
        var library = device.NewLibrary((NSString)msl, new MTLCompileOptions(), ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"the shader did not compile: {error.LocalizedDescription}");
        return library;
    }
}
