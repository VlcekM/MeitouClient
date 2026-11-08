using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Upscalers;

/// <summary>Layouts of the ffx_api structs the frame-generation code shares (FidelityFX SDK 1.1.x <c>ffx_api.h</c> / <c>ffx_api_types.h</c>, MIT; our own code).</summary>
internal static unsafe class FfxApi
{
    [StructLayout(LayoutKind.Sequential)] internal struct Header { public ulong Type; public nint Next; }
    [StructLayout(LayoutKind.Sequential)] internal struct Dimensions { public uint Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct Coords { public float X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct ResourceDescription { public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage; }
    [StructLayout(LayoutKind.Sequential)] internal struct Resource { public nint Handle; public ResourceDescription Description; public uint State; }

    internal const uint StateCommon = 1, TypeTexture2D = 2;
    /// <summary>FfxApiSurfaceFormat values (the swapchain's colour formats and the engine's).</summary>
    internal const uint FormatR8G8B8A8Unorm = 10, FormatB8G8R8A8Unorm = 14;

    internal static uint SurfaceFormat(InternalFormat f) => f switch
    {
        InternalFormat.Rgba16f => 4,
        InternalFormat.Rgba32f => 3,
        InternalFormat.RG16f => 18,
        InternalFormat.R32f => 28,
        InternalFormat.Rgba8 => FormatR8G8B8A8Unorm,
        InternalFormat.R8 => 25,
        InternalFormat.R11fG11fB10f => 16,
        _ => 0,
    };

    /// <summary>An engine texture as an FFX input: its image, in GENERAL (declared COMMON, which the backend reads as GENERAL and returns it to).</summary>
    internal static Resource Input(GpuContext ctx, GpuTexture texture)
    {
        var d = texture.Desc;
        return new Resource
        {
            Handle = (nint)ctx.Device.NativeImage(texture).Handle,
            Description = new ResourceDescription { Type = TypeTexture2D, Format = SurfaceFormat(d.Format), Width = (uint)d.Width, Height = (uint)d.Height, Depth = 1, MipCount = 1 },
            State = StateCommon,
        };
    }
}
