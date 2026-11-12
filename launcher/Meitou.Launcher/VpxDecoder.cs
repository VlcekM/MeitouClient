using System.Runtime.InteropServices;

namespace Meitou.Launcher;

/// <summary>
/// VP8 and VP9 frames decoded by libvpx (BSD; built by vcpkg and linked into the published launcher, docs/launcher.md "News"), turned
/// into BGRA pixels. Only 8-bit pictures are drawn; struct layouts and ABI numbers are libvpx 1.16's (<c>vpx_decoder.h</c>, <c>vpx_image.h</c>).
/// </summary>
sealed unsafe partial class VpxDecoder : IDisposable
{
    /// <summary>
    /// The ABI version <c>vpx_codec_dec_init_ver</c> expects (VPX_DECODER_ABI_VERSION: 3 + VPX_CODEC_ABI_VERSION, 4 + VPX_IMAGE_ABI_VERSION 5).
    /// </summary>
    const int DecoderAbiVersion = 12; // VPX_DECODER_ABI_VERSION: 3 + VPX_CODEC_ABI_VERSION (4 + VPX_IMAGE_ABI_VERSION 5)

    /// <summary>
    /// <c>VPX_IMG_FMT_HIGHBITDEPTH</c>: a picture with more than 8 bits (not drawn).
    /// </summary>
    const int ImgFmtHighBitDepth = 0x800;

    /// <summary>
    /// The codec context (<c>vpx_codec_ctx_t</c>, 56 bytes on 64-bit platforms, 128 allocated).
    /// </summary>
    readonly IntPtr _context = Marshal.AllocHGlobal(128); // vpx_codec_ctx_t: 56 bytes on 64-bit platforms

    /// <summary>
    /// The context was initialised (and is destroyed with the decoder).
    /// </summary>
    bool _open;

    /// <summary>
    /// Starts a decoder for <paramref name="codec"/> (<c>V_VP8</c> or <c>V_VP9</c>), with half the processors as threads (1 to 4).
    /// </summary>
    public VpxDecoder(string codec)
    {
        new Span<byte>((void*)_context, 128).Clear();
        var iface = codec switch
        {
            "V_VP8" => vpx_codec_vp8_dx(),
            "V_VP9" => vpx_codec_vp9_dx(),
            _ => throw new NotSupportedException($"video codec {codec}"),
        };
        var config = new DecoderConfig { Threads = (uint)Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        int error = vpx_codec_dec_init_ver(_context, iface, &config, new CLong(0), DecoderAbiVersion);
        if (error != 0) throw new InvalidOperationException($"libvpx could not start ({error})");
        _open = true;
    }

    /// <summary>
    /// True when libvpx can be loaded (it is linked into published launchers; Debug builds look for it beside the executable).
    /// </summary>
    public static bool Available
    {
        get
        {
            try
            {
                return vpx_codec_vp8_dx() != 0;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Decodes one frame; when it yields a picture, writes it into <paramref name="bgra"/> (resized to width × height × 4 as needed) and
    /// returns its size. <paramref name="convert"/> false only decodes (a frame shown too late to be worth drawing).
    /// </summary>
    public (int Width, int Height)? Decode(ReadOnlySpan<byte> frame, ref byte[] bgra, bool convert = true)
    {
        fixed (byte* data = frame)
            if (vpx_codec_decode(_context, data, (uint)frame.Length, 0, new CLong(0)) != 0) return null;
        IntPtr iterator = 0;
        (int, int)? size = null;
        for (Image* image; (image = vpx_codec_get_frame(_context, &iterator)) != null;)
        {
            if (!convert || (image->Format & ImgFmtHighBitDepth) != 0) continue;
            int width = (int)image->DisplayWidth, height = (int)image->DisplayHeight;
            if (bgra.Length != width * height * 4) bgra = new byte[width * height * 4];
            ToBgra(image, bgra, width, height);
            size = (width, height);
        }
        return size;
    }

    /// <summary>
    /// Y'CbCr (BT.601, studio range, the VP8/VP9 default) to BGRA, rows split across cores.
    /// </summary>
    static void ToBgra(Image* image, byte[] bgra, int width, int height)
    {
        byte* y = image->PlaneY, u = image->PlaneU, v = image->PlaneV;
        int strideY = image->StrideY, strideU = image->StrideU, strideV = image->StrideV;
        int shiftX = (int)image->ChromaShiftX, shiftY = (int)image->ChromaShiftY;
        Parallel.For(0, height, row =>
        {
            byte* lineY = y + row * strideY, lineU = u + (row >> shiftY) * strideU, lineV = v + (row >> shiftY) * strideV;
            fixed (byte* output = bgra)
            {
                byte* o = output + row * width * 4;
                for (int x = 0; x < width; x++, o += 4)
                {
                    int c = (lineY[x] - 16) * 298, d = lineU[x >> shiftX] - 128, e = lineV[x >> shiftX] - 128;
                    o[0] = Clamp((c + 516 * d + 128) >> 8);
                    o[1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                    o[2] = Clamp((c + 409 * e + 128) >> 8);
                    o[3] = 255;
                }
            }
        });
    }

    /// <summary>
    /// <paramref name="value"/> clamped to a byte.
    /// </summary>
    static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>
    /// Destroys the codec and frees the context.
    /// </summary>
    public void Dispose()
    {
        if (_open) vpx_codec_destroy(_context);
        _open = false;
        Marshal.FreeHGlobal(_context);
    }

    /// <summary>
    /// <c>vpx_codec_dec_cfg_t</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct DecoderConfig
    {
        /// <summary>
        /// The decoding threads, and the picture size (0: from the stream).
        /// </summary>
        public uint Threads, Width, Height;
    }

    /// <summary>
    /// <c>vpx_image_t</c>, up to the planes and strides.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Image
    {
        /// <summary>
        /// The image format, colour space and range.
        /// </summary>
        public int Format, ColorSpace, Range;

        /// <summary>
        /// The picture's size and bit depth, the display and render sizes and the chroma subsampling shifts.
        /// </summary>
        public uint Width, Height, BitDepth, DisplayWidth, DisplayHeight, RenderWidth, RenderHeight, ChromaShiftX, ChromaShiftY;

        /// <summary>
        /// The planes: luma, the two chroma and alpha.
        /// </summary>
        public byte* PlaneY, PlaneU, PlaneV, PlaneAlpha;

        /// <summary>
        /// Each plane's row stride in bytes.
        /// </summary>
        public int StrideY, StrideU, StrideV, StrideAlpha;
    }

    /// <summary>
    /// The libvpx library's name.
    /// </summary>
    const string Vpx = "vpx";

    /// <summary>
    /// <c>vpx_codec_vp8_dx</c>: the VP8 decoder interface.
    /// </summary>
    [LibraryImport(Vpx)] private static partial IntPtr vpx_codec_vp8_dx();

    /// <summary>
    /// <c>vpx_codec_vp9_dx</c>: the VP9 decoder interface.
    /// </summary>
    [LibraryImport(Vpx)] private static partial IntPtr vpx_codec_vp9_dx();

    /// <summary>
    /// <c>vpx_codec_dec_init_ver</c>.
    /// </summary>
    [LibraryImport(Vpx)] private static partial int vpx_codec_dec_init_ver(IntPtr context, IntPtr iface, DecoderConfig* config, CLong flags,
            int version);

    /// <summary>
    /// <c>vpx_codec_decode</c>.
    /// </summary>
    [LibraryImport(Vpx)] private static partial int vpx_codec_decode(IntPtr context, byte* data, uint size, IntPtr userData, CLong deadline);

    /// <summary>
    /// <c>vpx_codec_get_frame</c>.
    /// </summary>
    [LibraryImport(Vpx)] private static partial Image* vpx_codec_get_frame(IntPtr context, IntPtr* iterator);

    /// <summary>
    /// <c>vpx_codec_destroy</c>.
    /// </summary>
    [LibraryImport(Vpx)] private static partial int vpx_codec_destroy(IntPtr context);
}
