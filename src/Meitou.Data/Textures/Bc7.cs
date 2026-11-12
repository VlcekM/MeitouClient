namespace Meitou.Data.Textures;

/// <summary>
/// BC7 (BPTC) block decoding to RGBA8, written from the public format description (Microsoft, "BC7 Format"; Khronos Data
/// Format Specification, "BPTC"): eight modes, 1 to 3 subsets, optional rotation and index selection. The partition and
/// anchor tables are the published ones (docs/formats/dds.md).
/// </summary>
static class Bc7
{
    /// <summary>
    /// Per mode: subsets, partition bits, rotation bits, index-selection bit, colour bits, alpha bits, p-bits (0 none, 1 per endpoint, 2 shared per
    /// subset), index bits, second index bits.
    /// </summary>
    static readonly (int Subsets, int PartBits, int RotBits, int SelBits, int ColorBits, int AlphaBits, int PBits, int IdxBits, int Idx2Bits)[] Modes =
    [
        (3, 4, 0, 0, 4, 0, 1, 3, 0),
        (2, 6, 0, 0, 6, 0, 2, 3, 0),
        (3, 6, 0, 0, 5, 0, 0, 2, 0),
        (2, 6, 0, 0, 7, 0, 1, 2, 0),
        (1, 0, 2, 1, 5, 6, 0, 2, 3),
        (1, 0, 2, 0, 7, 8, 0, 2, 2),
        (1, 0, 0, 0, 7, 7, 1, 4, 0),
        (2, 6, 0, 0, 5, 5, 1, 2, 0),
    ];

    /// <summary>
    /// The interpolation weights (out of 64) of 2-bit indices, from the BC7 specification.
    /// </summary>
    static readonly byte[] Weights2 = [0, 21, 43, 64];

    /// <summary>
    /// The interpolation weights (out of 64) of 3-bit indices.
    /// </summary>
    static readonly byte[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];

    /// <summary>
    /// The interpolation weights (out of 64) of 4-bit indices.
    /// </summary>
    static readonly byte[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>
    /// Subset of each pixel, 2 bits per pixel (pixel 0 in the low bits), for each partition.
    /// </summary>
    static readonly uint[] Subsets2 = [
        0x50505050, 0x40404040, 0x54545454, 0x54505040, 0x50404000, 0x55545450, 0x55545040, 0x54504000,
        0x50400000, 0x55555450, 0x55544000, 0x54400000, 0x55555440, 0x55550000, 0x55555500, 0x55000000,
        0x55150100, 0x00004054, 0x15010000, 0x00405054, 0x00004050, 0x15050100, 0x05010000, 0x40505054,
        0x00404050, 0x05010100, 0x14141414, 0x05141450, 0x01155440, 0x00555500, 0x15014054, 0x05414150,
        0x44444444, 0x55005500, 0x11441144, 0x05055050, 0x05500550, 0x11114444, 0x41144114, 0x44111144,
        0x15055054, 0x01055040, 0x05041050, 0x05455150, 0x14414114, 0x50050550, 0x41411414, 0x00141400,
        0x00041504, 0x00105410, 0x10541000, 0x04150400, 0x50410514, 0x41051450, 0x05415014, 0x14054150,
        0x41050514, 0x41505014, 0x40011554, 0x54150140, 0x50505500, 0x00555050, 0x15151010, 0x54540404,
    ];
    /// <summary>
    /// The subset of each pixel in the same packing, for each 3-subset partition.
    /// </summary>
    static readonly uint[] Subsets3 = [
        0xAA685050, 0x6A5A5040, 0x5A5A4200, 0x5450A0A8, 0xA5A50000, 0xA0A05050, 0x5555A0A0, 0x5A5A5050,
        0xAA550000, 0xAA555500, 0xAAAA5500, 0x90909090, 0x94949494, 0xA4A4A4A4, 0xA9A59450, 0x2A0A4250,
        0xA5945040, 0x0A425054, 0xA5A5A500, 0x55A0A0A0, 0xA8A85454, 0x6A6A4040, 0xA4A45000, 0x1A1A0500,
        0x0050A4A4, 0xAAA59090, 0x14696914, 0x69691400, 0xA08585A0, 0xAA821414, 0x50A4A450, 0x6A5A0200,
        0xA9A58000, 0x5090A0A8, 0xA8A09050, 0x24242424, 0x00AA5500, 0x24924924, 0x24499224, 0x50A50A50,
        0x500AA550, 0xAAAA4444, 0x66660000, 0xA5A0A5A0, 0x50A050A0, 0x69286928, 0x44AAAA44, 0x66666600,
        0xAA444444, 0x54A854A8, 0x95809580, 0x96969600, 0xA85454A8, 0x80959580, 0xAA141414, 0x96960000,
        0xAAAA1414, 0xA05050A0, 0xA0A5A5A0, 0x96000000, 0x40804080, 0xA9A8A9A8, 0xAAAAAA44, 0x2A4A5254,
    ];
    /// <summary>
    /// The anchor pixel of subset 1 in each 2-subset partition (subset 0's is pixel 0); its index is stored one bit shorter.
    /// </summary>
    static readonly byte[] Anchor2 = [
        15, 15, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15,
        2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15,
        2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2,
        15, 15, 15, 15, 15, 2, 2, 15,
    ];
    /// <summary>
    /// The anchor pixel of subset 1 in each 3-subset partition.
    /// </summary>
    static readonly byte[] Anchor3A = [
        3, 3, 15, 15, 8, 3, 15, 15,
        8, 8, 6, 6, 6, 5, 3, 3,
        3, 3, 8, 15, 3, 3, 6, 10,
        5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15,
        15, 3, 15, 5, 15, 15, 15, 15,
        3, 15, 5, 5, 5, 8, 5, 10,
        5, 10, 8, 13, 15, 12, 3, 3,
    ];
    /// <summary>
    /// The anchor pixel of subset 2 in each 3-subset partition.
    /// </summary>
    static readonly byte[] Anchor3B = [
        15, 8, 8, 3, 15, 15, 3, 8,
        15, 15, 15, 15, 15, 15, 15, 8,
        15, 8, 15, 3, 15, 8, 15, 8,
        3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10,
        6, 15, 8, 15, 3, 6, 6, 8,
        15, 3, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15, 3, 15, 15, 8,
    ];

    /// <summary>
    /// Decodes one 16-byte block into 16 RGBA pixels (<paramref name="rgba"/>, 64 bytes, row by row).
    /// </summary>
    public static void DecodeBlock(ReadOnlySpan<byte> src, Span<byte> rgba)
    {
        ulong lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(src);
        ulong hi = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(src[8..]);
        int pos = 0;
        int Read(int n)
        {
            if (n == 0) return 0;
            ulong v = pos < 64 ? lo >> pos : hi >> (pos - 64);
            if (pos < 64 && pos + n > 64) v |= hi << (64 - pos);
            pos += n;
            return (int)(v & ((1UL << n) - 1));
        }

        int mode = 0;
        while (mode < 8 && Read(1) == 0) mode++;
        if (mode >= 8) { rgba[..64].Clear(); return; }   // a reserved mode decodes to transparent black

        var m = Modes[mode];
        int partition = Read(m.PartBits), rotation = Read(m.RotBits), selection = Read(m.SelBits);
        int endpoints = m.Subsets * 2;
        Span<int> ep = stackalloc int[3 * 2 * 4];   // [subset * 2 + end][channel], 4 channels each
        Span<int> raw = stackalloc int[6 * 4];
        for (int ch = 0; ch < 3; ch++)
            for (int e = 0; e < endpoints; e++) raw[e * 4 + ch] = Read(m.ColorBits);
        for (int e = 0; e < endpoints; e++) raw[e * 4 + 3] = m.AlphaBits > 0 ? Read(m.AlphaBits) : 0;
        Span<int> pbit = stackalloc int[6];
        if (m.PBits == 1) for (int e = 0; e < endpoints; e++) pbit[e] = Read(1);
        else if (m.PBits == 2) for (int s = 0; s < m.Subsets; s++) pbit[s * 2] = pbit[s * 2 + 1] = Read(1);
        for (int e = 0; e < endpoints; e++)
            for (int ch = 0; ch < 4; ch++)
            {
                int bits = ch < 3 ? m.ColorBits : m.AlphaBits;
                if (bits == 0) { ep[e * 4 + ch] = 255; continue; }
                int v = raw[e * 4 + ch];
                if (m.PBits != 0) { v = v << 1 | pbit[e]; bits++; }
                ep[e * 4 + ch] = v << (8 - bits) | v >> (2 * bits - 8);
            }

        uint subsets = m.Subsets == 1 ? 0 : m.Subsets == 2 ? Subsets2[partition] : Subsets3[partition];
        int Subset(int i) => (int)(subsets >> (2 * i)) & 3;
        int Anchor(int subset) => subset == 0 ? 0 : m.Subsets == 2 ? Anchor2[partition] : subset == 1 ? Anchor3A[partition] : Anchor3B[partition];

        Span<int> idx = stackalloc int[16], idx2 = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            int s = Subset(i);
            idx[i] = Read(Anchor(s) == i ? m.IdxBits - 1 : m.IdxBits);
        }
        if (m.Idx2Bits > 0)
            for (int i = 0; i < 16; i++) idx2[i] = Read(i == 0 ? m.Idx2Bits - 1 : m.Idx2Bits);

        for (int i = 0; i < 16; i++)
        {
            int s = Subset(i);
            int colorIdx = idx[i], colorBits = m.IdxBits, alphaIdx = idx[i], alphaBits = m.IdxBits;
            if (m.Idx2Bits > 0)
            {
                // Mode 4 can swap which stream drives colour; mode 5 always has separate colour and alpha streams.
                if (selection == 0) { alphaIdx = idx2[i]; alphaBits = m.Idx2Bits; }
                else { colorIdx = idx2[i]; colorBits = m.Idx2Bits; alphaIdx = idx[i]; alphaBits = m.IdxBits; }
            }
            int e0 = s * 2 * 4, e1 = e0 + 4;
            int wc = Weight(colorBits, colorIdx), wa = Weight(alphaBits, alphaIdx);
            int r = Mix(ep[e0], ep[e1], wc), g = Mix(ep[e0 + 1], ep[e1 + 1], wc);
            int b = Mix(ep[e0 + 2], ep[e1 + 2], wc), a = Mix(ep[e0 + 3], ep[e1 + 3], wa);
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }
            rgba[i * 4] = (byte)r; rgba[i * 4 + 1] = (byte)g; rgba[i * 4 + 2] = (byte)b; rgba[i * 4 + 3] = (byte)a;
        }
    }

    /// <summary>
    /// The weight of <paramref name="index"/> in the table for <paramref name="bits"/>-bit indices.
    /// </summary>
    static int Weight(int bits, int index) => bits switch { 2 => Weights2[index], 3 => Weights3[index], _ => Weights4[index] };

    /// <summary>
    /// Interpolates the endpoint values <paramref name="a"/> and <paramref name="b"/> at weight <paramref name="w"/> (0..64), rounded.
    /// </summary>
    static int Mix(int a, int b, int w) => (a * (64 - w) + b * w + 32) >> 6;
}