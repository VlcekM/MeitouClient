using System.Buffers.Binary;
using System.Text;

namespace Meitou.Data.Audio;

/// <summary>
/// A Wwise Vorbis WEM (codec <c>0xFFFF</c>) and its conversion to a standard Ogg Vorbis stream (docs/formats/wwise-bnk.md#wwise-vorbis):
/// the identification and comment headers are rebuilt from the <c>fmt </c> chunk, the setup header from Wwise's compact one (codebooks by
/// index into <see cref="WwiseCodebookLibrary"/>, type fields dropped), and each audio packet gets back its packet type bit and window flags.
/// </summary>
public sealed class WwiseVorbis
{
    /// <summary>
    /// Channel count, 1..8 (<c>fmt </c> offset 2).
    /// </summary>
    public int Channels { get; private init; }
    /// <summary>
    /// Samples per second per channel (<c>fmt </c> offset 4).
    /// </summary>
    public int SampleRate { get; private init; }
    /// <summary>
    /// The average data rate the <c>fmt </c> chunk states (offset 8); the rebuilt identification header writes it as the nominal bitrate.
    /// </summary>
    public int AverageBytesPerSecond { get; private init; }
    /// <summary>
    /// The number of sample frames the sound plays.
    /// </summary>
    public int SampleCount { get; private init; }
    /// <summary>
    /// Where the setup packet starts in <see cref="Data"/>.
    /// </summary>
    public int SetupOffset { get; private init; }
    /// <summary>
    /// Where the first audio packet starts in <see cref="Data"/>.
    /// </summary>
    public int FirstAudioOffset { get; private init; }
    /// <summary>
    /// The short block size as a power of two (6..13).
    /// </summary>
    public int Blocksize0Exponent { get; private init; }
    /// <summary>
    /// The long block size as a power of two (at least <see cref="Blocksize0Exponent"/>, at most 13).
    /// </summary>
    public int Blocksize1Exponent { get; private init; }
    /// <summary>
    /// The WEM's own id from the <c>fmt </c> chunk; the Ogg stream's serial number.
    /// </summary>
    public uint Uid { get; private init; }
    /// <summary>
    /// The <c>data</c> chunk: the setup packet, then the audio packets, each after a u16 size.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; private init; }

    /// <summary>
    /// Reads the RIFF header of a WEM with the 66-byte <c>fmt </c> chunk (Wwise 2016 layout, the only one in Kenshi).
    /// </summary>
    public static WwiseVorbis Read(ReadOnlyMemory<byte> wem)
    {
        var s = wem.Span;
        if (s.Length < 12 || !s.StartsWith("RIFF"u8) || !s[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file");
        int fmt = -1, fmtLength = 0, data = -1, dataLength = 0;
        for (int p = 12; p + 8 <= s.Length;)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(s[(p + 4)..]);
            if (len < 0 || p + 8 + (long)len > s.Length)
                throw new InvalidDataException("RIFF chunk runs past the file");
            var tag = s.Slice(p, 4);
            if (tag.SequenceEqual("fmt "u8)) (fmt, fmtLength) = (p + 8, len);
            else if (tag.SequenceEqual("data"u8)) (data, dataLength) = (p + 8, len);
            p += 8 + len + (len & 1);
        }
        if (fmt < 0 || data < 0) throw new InvalidDataException("WEM without fmt or data");
        if (BinaryPrimitives.ReadUInt16LittleEndian(s[fmt..]) != 0xFFFF)
            throw new InvalidDataException("Not Wwise Vorbis");
        if (fmtLength != 66) throw new InvalidDataException($"fmt chunk of {fmtLength} bytes not decoded");
        int vorb = fmt + 0x18;
        var w = new WwiseVorbis
        {
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(s[(fmt + 2)..]),
            SampleRate = BinaryPrimitives.ReadInt32LittleEndian(s[(fmt + 4)..]),
            AverageBytesPerSecond = BinaryPrimitives.ReadInt32LittleEndian(s[(fmt + 8)..]),
            SampleCount = BinaryPrimitives.ReadInt32LittleEndian(s[vorb..]),
            SetupOffset = BinaryPrimitives.ReadInt32LittleEndian(s[(vorb + 0x10)..]),
            FirstAudioOffset = BinaryPrimitives.ReadInt32LittleEndian(s[(vorb + 0x14)..]),
            Uid = BinaryPrimitives.ReadUInt32LittleEndian(s[(vorb + 0x24)..]),
            Blocksize0Exponent = s[vorb + 0x28],
            Blocksize1Exponent = s[vorb + 0x29],
            Data = wem.Slice(data, dataLength),
        };
        if (w.Channels is < 1 or > 8 || w.Blocksize0Exponent is < 6 or > 13 || w.Blocksize1Exponent < w.Blocksize0Exponent
                || w.Blocksize1Exponent > 13)
            throw new InvalidDataException("Implausible Wwise Vorbis header");
        return w;
    }

    /// <summary>
    /// The whole sound as an Ogg Vorbis stream.
    /// </summary>
    public byte[] ToOgg(WwiseCodebookLibrary library)
    {
        var ogg = new OggWriter((int)Uid);
        ogg.Packet(IdentificationHeader(), 0, last: false);
        ogg.Flush();
        ogg.Packet(CommentHeader(), 0, last: false);
        var setup = Packet(SetupOffset, out _);
        ogg.Packet(SetupHeader(setup.Span, library, out var modeLong, out int modeBits), 0, last: false);
        ogg.Flush();

        var packets = new List<ReadOnlyMemory<byte>>();
        for (int p = FirstAudioOffset; p < Data.Length;) packets.Add(Packet(p, out p));
        int ModeOf(ReadOnlySpan<byte> packet)
        {
            var r = new VorbisBitReader(packet);
            int mode = (int)r.Read(modeBits);
            return mode < modeLong.Length ? mode : throw new InvalidDataException($"Audio packet mode {mode} of {modeLong.Length}");
        }
        var modes = packets.Select(p => ModeOf(p.Span)).ToArray();
        long granule = 0;
        int previousSize = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            bool isLong = modeLong[modes[i]];
            int size = 1 << (isLong ? Blocksize1Exponent : Blocksize0Exponent);
            if (previousSize != 0) granule += previousSize / 4 + size / 4;
            previousSize = size;
            bool last = i == packets.Count - 1;
            bool previousLong = i > 0 && modeLong[modes[i - 1]];
            bool nextLong = !last && modeLong[modes[i + 1]];
            ogg.Packet(AudioPacket(packets[i].Span, modeBits, isLong, previousLong, nextLong), last ? SampleCount : Math.Min(granule, SampleCount),
                    last);
        }
        ogg.Flush();
        return ogg.ToArray();
    }

    /// <summary>
    /// The sound decoded to interleaved float samples (<see cref="SampleCount"/> × <see cref="Channels"/>).
    /// </summary>
    public float[] Decode(WwiseCodebookLibrary library)
    {
        using var stream = new MemoryStream(ToOgg(library));
        using var reader = new NVorbis.VorbisReader(stream, closeOnDispose: false);
        var samples = new float[(long)SampleCount * Channels];
        int read = 0;
        while (read < samples.Length)
        {
            int n = reader.ReadSamples(samples, read, samples.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == samples.Length ? samples : samples[..read];
    }

    /// <summary>
    /// The packet at <paramref name="offset"/> in <see cref="Data"/> (after its u16 size); <paramref name="next"/> is where the following one starts.
    /// </summary>
    ReadOnlyMemory<byte> Packet(int offset, out int next)
    {
        var d = Data.Span;
        if (offset + 2 > d.Length) throw new InvalidDataException($"Packet header at {offset} runs past the data");
        int size = BinaryPrimitives.ReadUInt16LittleEndian(d[offset..]);
        if (offset + 2 + size > d.Length) throw new InvalidDataException($"Packet at {offset} runs past the data");
        next = offset + 2 + size;
        return Data.Slice(offset + 2, size);
    }

    /// <summary>
    /// The Vorbis identification header (packet type 1) rebuilt from the <c>fmt </c> chunk.
    /// </summary>
    byte[] IdentificationHeader()
    {
        var w = new VorbisBitWriter();
        w.Write(1, 8);
        w.WriteBytes("vorbis"u8);
        w.Write(0, 32);
        w.Write((uint)Channels, 8);
        w.Write((uint)SampleRate, 32);
        w.Write(0, 32);
        w.Write((uint)(AverageBytesPerSecond * 8), 32);
        w.Write(0, 32);
        w.Write((uint)Blocksize0Exponent, 4);
        w.Write((uint)Blocksize1Exponent, 4);
        w.Write(true);
        return w.ToArray();
    }

    /// <summary>
    /// A Vorbis comment header (packet type 3) with Meitou's vendor string and no comments.
    /// </summary>
    static byte[] CommentHeader()
    {
        var w = new VorbisBitWriter();
        w.Write(3, 8);
        w.WriteBytes("vorbis"u8);
        var vendor = Encoding.ASCII.GetBytes("Meitou WEM rebuild");
        w.Write((uint)vendor.Length, 32);
        w.WriteBytes(vendor);
        w.Write(0, 32);
        w.Write(true);
        return w.ToArray();
    }

    /// <summary>
    /// Rebuilds the setup header; <paramref name="modeLong"/> gets each mode's block flag.
    /// </summary>
    byte[] SetupHeader(ReadOnlySpan<byte> packet, WwiseCodebookLibrary library, out bool[] modeLong, out int modeBits)
    {
        var r = new VorbisBitReader(packet);
        var w = new VorbisBitWriter();
        w.Write(5, 8);
        w.WriteBytes("vorbis"u8);

        CopyCodebooks(ref r, w, library);

        w.Write(0, 6); // one time-domain transform
        w.Write(0, 16); // of type 0

        CopyFloors(ref r, w);
        CopyResidues(ref r, w);
        CopyMappings(ref r, w);
        CopyModes(ref r, w, out modeLong, out modeBits);
        w.Write(true); // framing
        if (r.Remaining >= 8) throw new InvalidDataException($"Setup packet has {r.Remaining} bits left over");
        return w.ToArray();
    }

    /// <summary>
    /// The codebooks: each a 10-bit index into <paramref name="library"/>, expanded to the standard layout.
    /// </summary>
    static void CopyCodebooks(ref VorbisBitReader r, VorbisBitWriter w, WwiseCodebookLibrary library)
    {
        int codebooks = (int)r.Read(8) + 1;
        w.Write((uint)codebooks - 1, 8);
        for (int i = 0; i < codebooks; i++)
        {
            var book = new VorbisBitReader(library.Packed((int)r.Read(10)));
            Codebook(ref book, w);
        }
    }

    /// <summary>
    /// The floors, each given back its type field (always floor 1).
    /// </summary>
    static void CopyFloors(ref VorbisBitReader r, VorbisBitWriter w)
    {
        int floors = (int)r.Read(6) + 1;
        w.Write((uint)floors - 1, 6);
        for (int i = 0; i < floors; i++)
        {
            w.Write(1, 16); // floor type 1 (the only one Wwise writes)
            int partitions = (int)r.Read(5);
            w.Write((uint)partitions, 5);
            var classes = new int[partitions];
            int maxClass = -1;
            for (int j = 0; j < partitions; j++)
            {
                classes[j] = (int)r.Read(4);
                w.Write((uint)classes[j], 4);
                maxClass = Math.Max(maxClass, classes[j]);
            }
            var dimensions = new int[maxClass + 1];
            for (int c = 0; c <= maxClass; c++)
            {
                dimensions[c] = (int)r.Read(3) + 1;
                w.Write((uint)dimensions[c] - 1, 3);
                int subclasses = (int)r.Read(2);
                w.Write((uint)subclasses, 2);
                if (subclasses != 0) w.Copy(ref r, 8);
                for (int k = 0; k < 1 << subclasses; k++) w.Copy(ref r, 8);
            }
            w.Copy(ref r, 2); // multiplier - 1
            int rangeBits = (int)r.Read(4);
            w.Write((uint)rangeBits, 4);
            foreach (var c in classes)
                for (int k = 0; k < dimensions[c]; k++) w.Copy(ref r, rangeBits);
        }
    }

    /// <summary>
    /// The residues: Wwise's 2-bit type widened to the standard 16 bits, the rest copied.
    /// </summary>
    static void CopyResidues(ref VorbisBitReader r, VorbisBitWriter w)
    {
        int residues = (int)r.Read(6) + 1;
        w.Write((uint)residues - 1, 6);
        for (int i = 0; i < residues; i++)
        {
            uint type = r.Read(2);
            if (type > 2) throw new InvalidDataException($"Residue type {type}");
            w.Write(type, 16);
            w.Copy(ref r, 24); // begin
            w.Copy(ref r, 24); // end
            w.Copy(ref r, 24); // partition size - 1
            int classifications = (int)r.Read(6) + 1;
            w.Write((uint)classifications - 1, 6);
            w.Copy(ref r, 8); // classbook
            var cascade = new uint[classifications];
            for (int c = 0; c < classifications; c++)
            {
                uint low = r.Read(3);
                w.Write(low, 3);
                bool hasHigh = r.Flag();
                w.Write(hasHigh);
                uint high = 0;
                if (hasHigh)
                {
                    high = r.Read(5);
                    w.Write(high, 5);
                }
                cascade[c] = high << 3 | low;
            }
            foreach (var c in cascade)
                for (int k = 0; k < 8; k++)
                    if ((c & (1u << k)) != 0) w.Copy(ref r, 8);
        }
    }

    /// <summary>
    /// The mappings, each given back its type field (mapping 0); the coupling steps and the channel muxes depend on <see cref="Channels"/>.
    /// </summary>
    void CopyMappings(ref VorbisBitReader r, VorbisBitWriter w)
    {
        int mappings = (int)r.Read(6) + 1;
        w.Write((uint)mappings - 1, 6);
        int channelBits = VorbisBitWriter.Log((uint)Channels - 1);
        for (int i = 0; i < mappings; i++)
        {
            w.Write(0, 16); // mapping type 0
            int submaps = 1;
            bool hasSubmaps = r.Flag();
            w.Write(hasSubmaps);
            if (hasSubmaps)
            {
                submaps = (int)r.Read(4) + 1;
                w.Write((uint)submaps - 1, 4);
            }
            bool hasCoupling = r.Flag();
            w.Write(hasCoupling);
            if (hasCoupling)
            {
                int steps = (int)r.Read(8) + 1;
                w.Write((uint)steps - 1, 8);
                for (int k = 0; k < steps; k++) w.Copy(ref r, channelBits * 2);
            }
            uint reserved = r.Read(2);
            if (reserved != 0) throw new InvalidDataException("Mapping reserved bits are not zero");
            w.Write(0, 2);
            if (submaps > 1)
                for (int c = 0; c < Channels; c++) w.Copy(ref r, 4);
            for (int k = 0; k < submaps; k++) w.Copy(ref r, 24); // time (unused), floor, residue
        }
    }

    /// <summary>
    /// The modes, each given back its window and transform types; <paramref name="modeLong"/> gets each mode's block flag and
    /// <paramref name="modeBits"/> the bits an audio packet's mode number takes.
    /// </summary>
    static void CopyModes(ref VorbisBitReader r, VorbisBitWriter w, out bool[] modeLong, out int modeBits)
    {
        int modes = (int)r.Read(6) + 1;
        w.Write((uint)modes - 1, 6);
        modeLong = new bool[modes];
        for (int i = 0; i < modes; i++)
        {
            modeLong[i] = r.Flag();
            w.Write(modeLong[i]);
            w.Write(0, 16); // window type
            w.Write(0, 16); // transform type
            w.Copy(ref r, 8); // mapping
        }
        modeBits = VorbisBitWriter.Log((uint)modes - 1);
    }

    /// <summary>
    /// Expands a packed codebook into the standard Vorbis codebook layout.
    /// </summary>
    static void Codebook(ref VorbisBitReader r, VorbisBitWriter w)
    {
        w.Write(0x564342, 24);
        uint dimensions = r.Read(4), entries = r.Read(14);
        w.Write(dimensions, 16);
        w.Write(entries, 24);
        bool ordered = r.Flag();
        w.Write(ordered);
        if (ordered)
        {
            w.Copy(ref r, 5); // initial length - 1
            uint current = 0;
            while (current < entries)
            {
                int bits = VorbisBitWriter.Log(entries - current);
                uint number = r.Read(bits);
                w.Write(number, bits);
                current += number;
            }
            if (current > entries) throw new InvalidDataException("Ordered codebook lengths run past the entries");
        }
        else
        {
            int lengthBits = (int)r.Read(3);
            bool sparse = r.Flag();
            if (lengthBits is 0 or > 5) throw new InvalidDataException($"Codeword length field of {lengthBits} bits");
            w.Write(sparse);
            for (uint e = 0; e < entries; e++)
            {
                bool present = true;
                if (sparse)
                {
                    present = r.Flag();
                    w.Write(present);
                }
                if (present) w.Write(r.Read(lengthBits), 5);
            }
        }
        uint lookup = r.Read(1);
        w.Write(lookup, 4);
        if (lookup == 1)
        {
            w.Copy(ref r, 32); // minimum
            w.Copy(ref r, 32); // delta
            int valueBits = (int)r.Read(4) + 1;
            w.Write((uint)valueBits - 1, 4);
            w.Copy(ref r, 1); // sequence
            uint values = Lookup1Values(entries, dimensions);
            for (uint k = 0; k < values; k++) w.Copy(ref r, valueBits);
        }
    }

    /// <summary>
    /// The bits a packed codebook uses (used to find and check the library).
    /// </summary>
    internal static int PackedCodebookBits(ReadOnlySpan<byte> packed)
    {
        var r = new VorbisBitReader(packed);
        Codebook(ref r, new VorbisBitWriter());
        return r.Position;
    }

    /// <summary>
    /// The largest integer v with v^dimensions &lt;= entries (Vorbis <c>lookup1_values</c>).
    /// </summary>
    static uint Lookup1Values(uint entries, uint dimensions)
    {
        if (dimensions == 0) throw new InvalidDataException("Codebook with zero dimensions");
        uint v = (uint)Math.Floor(Math.Pow(entries, 1.0 / dimensions));
        while (Pow(v + 1, dimensions) <= entries) v++;
        while (v > 0 && Pow(v, dimensions) > entries) v--;
        return v;

        static ulong Pow(uint b, uint e)
        {
            ulong r = 1;
            for (uint i = 0; i < e; i++)
            {
                r *= b;
                if (r > uint.MaxValue) return r;
            }
            return r;
        }
    }

    /// <summary>
    /// Puts back the packet type bit (0) and, for a long block, the previous and next window flags.
    /// </summary>
    static byte[] AudioPacket(ReadOnlySpan<byte> packet, int modeBits, bool isLong, bool previousLong, bool nextLong)
    {
        var r = new VorbisBitReader(packet);
        var w = new VorbisBitWriter();
        w.Write(0, 1);
        w.Copy(ref r, modeBits);
        if (isLong)
        {
            w.Write(previousLong);
            w.Write(nextLong);
        }
        w.Copy(ref r, r.Remaining);
        return w.ToArray();
    }

    /// <summary>
    /// Builds Ogg pages: packets are laced into pages of up to 255 segments; <see cref="Flush"/> ends a page.
    /// </summary>
    sealed class OggWriter(int serial)
    {
        /// <summary>
        /// The CRC-32 table of the Ogg checksum (polynomial 0x04C11DB7, no reflection).
        /// </summary>
        static readonly uint[] Table = MakeTable();

        /// <summary>
        /// The pages written so far.
        /// </summary>
        readonly MemoryStream _output = new();

        /// <summary>
        /// The open page's lacing values.
        /// </summary>
        readonly List<byte> _segments = [];

        /// <summary>
        /// The open page's body bytes.
        /// </summary>
        readonly MemoryStream _body = new();

        /// <summary>
        /// The granule position of the last packet added.
        /// </summary>
        long _granule = -1;

        /// <summary>
        /// The next page's sequence number.
        /// </summary>
        int _sequence;

        /// <summary>
        /// The open page is the stream's first; the last packet added ends the stream.
        /// </summary>
        bool _first = true, _ended;

        /// <summary>
        /// The open page continues a packet the previous page did not finish.
        /// </summary>
        bool _continuation;

        /// <summary>
        /// Adds <paramref name="packet"/> with its granule position, split into lacing segments; a page is written once it holds over 8 KiB, at 255
        /// segments, and after the <paramref name="last"/> packet.
        /// </summary>
        public void Packet(byte[] packet, long packetGranule, bool last)
        {
            int left = packet.Length, at = 0;
            do
            {
                if (_segments.Count == 255) Flush(continued: true);
                int n = Math.Min(255, left);
                _segments.Add((byte)n);
                _body.Write(packet, at, n);
                at += n;
                left -= n;
            }
            while (left > 0 || _segments[^1] == 255);
            _granule = packetGranule;
            _ended = last;
            if (_body.Length > 8192 || last) Flush();
        }

        /// <summary>
        /// Writes the open page, if any, so the next packet starts a new page.
        /// </summary>
        public void Flush() => Flush(continued: false);

        /// <summary>
        /// Writes the open page with its header and CRC; <paramref name="continued"/> when a packet goes on into the next page (granule position
        /// -1 then).
        /// </summary>
        void Flush(bool continued)
        {
            if (_segments.Count == 0) return;
            var header = new byte[27 + _segments.Count];
            "OggS"u8.CopyTo(header);
            header[5] = (byte)((_continuation ? 1 : 0) | (_first ? 2 : 0) | (_ended && !continued ? 4 : 0));
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), continued ? -1 : _granule);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), serial);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), _sequence++);
            header[26] = (byte)_segments.Count;
            for (int i = 0; i < _segments.Count; i++) header[27 + i] = _segments[i];
            var bodyBytes = _body.ToArray();
            uint crc = Crc(Crc(0, header), bodyBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), crc);
            _output.Write(header);
            _output.Write(bodyBytes);
            _segments.Clear();
            _body.SetLength(0);
            _first = false;
            _continuation = continued;
        }

        /// <summary>
        /// The pages written so far as one Ogg stream.
        /// </summary>
        public byte[] ToArray() => _output.ToArray();

        /// <summary>
        /// The Ogg CRC-32 table (polynomial 0x04C11DB7, not reflected).
        /// </summary>
        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                t[i] = c;
            }
            return t;
        }

        /// <summary>
        /// Folds <paramref name="data"/> into the running Ogg CRC <paramref name="crc"/>.
        /// </summary>
        static uint Crc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = (crc << 8) ^ Table[(crc >> 24) ^ b];
            return crc;
        }
    }
}