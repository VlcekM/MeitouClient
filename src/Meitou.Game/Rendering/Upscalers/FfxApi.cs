using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Upscalers;

/// <summary>
/// Layouts of the ffx_api structs the frame-generation code shares (FidelityFX SDK 1.1.x <c>ffx_api.h</c> / <c>ffx_api_types.h</c>, MIT; our own
/// code).
/// </summary>
internal static unsafe class FfxApi
{
    /// <summary>
    /// <c>ffxApiHeader</c>: every ffx_api descriptor starts with its type and the next descriptor in its chain.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        /// <summary>
        /// The descriptor's type (a <c>FFX_API_*_DESC_TYPE_*</c> value).
        /// </summary>
        public ulong Type;

        /// <summary>
        /// The next descriptor in the chain (0: none).
        /// </summary>
        public nint Next;
    }

    /// <summary>
    /// <c>FfxApiDimensions2D</c>: a size in pixels.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Dimensions
    {
        /// <summary>
        /// The width and height.
        /// </summary>
        public uint Width, Height;
    }

    /// <summary>
    /// <c>FfxApiFloatCoords2D</c>: a pair of floats.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Coords
    {
        /// <summary>
        /// The two values.
        /// </summary>
        public float X, Y;
    }

    /// <summary>
    /// <c>FfxApiRect2D</c>: a rectangle in pixels.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        /// <summary>
        /// The left and top corner and the size.
        /// </summary>
        public int Left, Top, Width, Height;
    }

    /// <summary>
    /// <c>FfxApiResourceDescription</c>: what an image is.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ResourceDescription
    {
        /// <summary>
        /// The resource type, its format (FfxApiSurfaceFormat), size, depth, mip count, flags and usage.
        /// </summary>
        public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage;
    }

    /// <summary>
    /// <c>FfxApiResource</c>: a native image with its description and state.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Resource
    {
        /// <summary>
        /// The native image (a VkImage).
        /// </summary>
        public nint Handle;

        /// <summary>
        /// What the image is.
        /// </summary>
        public ResourceDescription Description;

        /// <summary>
        /// The image's state (<see cref="StateCommon"/>).
        /// </summary>
        public uint State;
    }

    /// <summary>
    /// <c>FFX_API_RESOURCE_STATE_COMMON</c> and <c>FFX_API_RESOURCE_TYPE_TEXTURE2D</c>.
    /// </summary>
    internal const uint StateCommon = 1, TypeTexture2D = 2;

    /// <summary>
    /// FfxApiSurfaceFormat values (the swapchain's colour formats and the engine's).
    /// </summary>
    internal const uint FormatR8G8B8A8Unorm = 10, FormatB8G8R8A8Unorm = 14;

    /// <summary>
    /// The FfxApiSurfaceFormat of an engine format (0, unknown, for one not listed).
    /// </summary>
    internal static uint SurfaceFormat(InternalFormat f) => f switch
    {
        InternalFormat.Rgba16F => 4,
        InternalFormat.Rgba32F => 3,
        InternalFormat.Rg16F => 18,
        InternalFormat.R32F => 28,
        InternalFormat.Rgba8 => FormatR8G8B8A8Unorm,
        InternalFormat.R8 => 25,
        InternalFormat.R11FG11FB10F => 16,
        _ => 0,
    };

    /// <summary>
    /// An engine texture as an FFX input: its image, in GENERAL (declared COMMON, which the backend reads as GENERAL and returns it to).
    /// </summary>
    internal static Resource Input(GpuContext ctx, GpuTexture texture)
    {
        var d = texture.Desc;
        return new Resource
        {
            Handle = (nint)ctx.Device.NativeImage(texture).Handle,
            Description = new ResourceDescription
            {
                Type = TypeTexture2D,
                Format = SurfaceFormat(d.Format),
                Width = (uint)d.Width,
                Height = (uint)d.Height,
                Depth = 1,
                MipCount = 1,
            },
            State = StateCommon,
        };
    }
}
