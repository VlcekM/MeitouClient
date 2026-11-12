using System.Buffers.Binary;

namespace Meitou.Launcher;

/// <summary>
/// One frame of a track: its time from the start of the file and its bytes.
/// </summary>
public readonly record struct WebmFrame(TimeSpan Time, bool Key, ReadOnlyMemory<byte> Data);

/// <summary>
/// The WebM subset (Matroska) a news video uses, read from bytes held in memory: the first video track (VP8 or VP9) and the first
/// audio track (Vorbis), every frame in file order. Element IDs and layout from the Matroska specification; docs/launcher.md, "News".
/// </summary>
public sealed class WebmFile
{
    #region Reading

    /// <summary>
    /// The Matroska element IDs read.
    /// </summary>
    const uint EbmlHeader = 0x1A45DFA3, Segment = 0x18538067, Info = 0x1549A966, TimecodeScale = 0x2AD7B1, Tracks = 0x1654AE6B,
        TrackEntry = 0xAE, TrackNumber = 0xD7, TrackType = 0x83, CodecId = 0x86, CodecPrivate = 0x63A2, VideoSettings = 0xE0,
        PixelWidth = 0xB0, PixelHeight = 0xBA, AudioSettings = 0xE1, SamplingFrequency = 0xB5, Channels = 0x9F, Cluster = 0x1F43B675,
        ClusterTimecode = 0xE7, SimpleBlock = 0xA3, BlockGroup = 0xA0, Block = 0xA1;

    /// <summary>
    /// The size of an element whose size is unknown (a live cluster).
    /// </summary>
    const long UnknownSize = -1;

    /// <summary>
    /// <c>V_VP8</c> or <c>V_VP9</c>; null without a video track.
    /// </summary>
    public string? VideoCodec { get; private set; }

    /// <summary>
    /// The video's width in pixels.
    /// </summary>
    public int Width { get; private set; }

    /// <summary>
    /// The video's height in pixels.
    /// </summary>
    public int Height { get; private set; }

    /// <summary>
    /// <c>A_VORBIS</c>, <c>A_OPUS</c>, ...; null without an audio track.
    /// </summary>
    public string? AudioCodec { get; private set; }

    /// <summary>
    /// The audio's sample rate.
    /// </summary>
    public int SampleRate { get; private set; }

    /// <summary>
    /// The audio's channel count.
    /// </summary>
    public int AudioChannels { get; private set; }

    /// <summary>
    /// The Vorbis identification, comment and setup headers (the track's CodecPrivate, Xiph-laced).
    /// </summary>
    public List<ReadOnlyMemory<byte>> AudioHeaders { get; } = [];

    /// <summary>
    /// The video frames in file order.
    /// </summary>
    public List<WebmFrame> VideoFrames { get; } = [];

    /// <summary>
    /// The audio frames in file order.
    /// </summary>
    public List<WebmFrame> AudioFrames { get; } = [];

    /// <summary>
    /// The file's bytes.
    /// </summary>
    readonly ReadOnlyMemory<byte> _data;

    /// <summary>
    /// Nanoseconds per timecode tick (the segment's TimecodeScale).
    /// </summary>
    long _timecodeScale = 1_000_000; // nanoseconds per timecode tick

    /// <summary>
    /// The track numbers of the first video and audio tracks (0: none yet).
    /// </summary>
    ulong _videoTrack, _audioTrack;

    /// <summary>
    /// Wraps the file's bytes.
    /// </summary>
    WebmFile(ReadOnlyMemory<byte> data) => this._data = data;

    /// <summary>
    /// Reads a WebM file; throws <see cref="InvalidDataException"/> when it is not one. A <paramref name="head"/> is only the file's
    /// start (for its first picture): it ends wherever it was cut, the last frame possibly incomplete.
    /// </summary>
    public static WebmFile Read(ReadOnlyMemory<byte> data, bool head = false)
    {
        var file = new WebmFile(data);
        int pos = 0;
        var (id, _, _) = file.Header(ref pos);
        if (id != EbmlHeader) throw new InvalidDataException("not a WebM file");
        pos = 0;
        try
        {
            while (pos < data.Length)
            {
                var (child, size, start) = file.Header(ref pos);
                int end = file.End(start, size);
                if (child == Segment) file.ReadSegment(start, end);
                pos = end;
            }
        }
        catch (InvalidDataException) when (head)
        {
        }
        return file;
    }

    /// <summary>
    /// Reads a segment: its info, tracks and clusters (each cluster's timecode with its blocks).
    /// </summary>
    void ReadSegment(int pos, int end)
    {
        long clusterTime = 0;
        while (pos < end)
        {
            var (id, size, start) = Header(ref pos);

            // A live-written Cluster has no size: its children follow until the next top-level element.
            if (id == Cluster && size == UnknownSize)
            {
                pos = start;
                continue;
            }
            int childEnd = End(start, size, end);
            switch (id)
            {
                case Info: ReadInfo(start, childEnd); break;
                case Tracks:
                    for (int p = start; p < childEnd;)
                    {
                        var (entry, entrySize, entryStart) = Header(ref p);
                        p = End(entryStart, entrySize, childEnd);
                        if (entry == TrackEntry) ReadTrack(entryStart, p);
                    }
                    break;
                case Cluster: break; // children read below, as they come
                case ClusterTimecode: clusterTime = (long)UInt(start, childEnd); break;
                case SimpleBlock: ReadBlock(start, childEnd, clusterTime); break;
                case BlockGroup:
                    for (int p = start; p < childEnd;)
                    {
                        var (child, childSize, childStart) = Header(ref p);
                        p = End(childStart, childSize, childEnd);
                        if (child == Block) ReadBlock(childStart, p, clusterTime, key: false);
                    }
                    break;
            }
            // A sized Cluster's children are elements of this loop too.
            pos = id == Cluster ? start : childEnd;
        }
    }

    /// <summary>
    /// Reads the segment info: the timecode scale.
    /// </summary>
    void ReadInfo(int pos, int end)
    {
        while (pos < end)
        {
            var (id, size, start) = Header(ref pos);
            pos = End(start, size, end);
            if (id == TimecodeScale) _timecodeScale = (long)UInt(start, pos);
        }
    }

    /// <summary>
    /// Reads a track entry: the first video track's codec and size and the first audio track's codec, rate, channels and headers.
    /// </summary>
    void ReadTrack(int pos, int end)
    {
        ulong number = 0, type = 0;
        string codec = "";
        ReadOnlyMemory<byte> codecPrivate = default;
        int width = 0, height = 0, channels = 1;
        double rate = 8000;
        while (pos < end)
        {
            var (id, size, start) = Header(ref pos);
            pos = End(start, size, end);
            switch (id)
            {
                case TrackNumber: number = UInt(start, pos); break;
                case TrackType: type = UInt(start, pos); break;
                case CodecId: codec = System.Text.Encoding.ASCII.GetString(_data.Span[start..pos]).TrimEnd('\0'); break;
                case CodecPrivate: codecPrivate = _data[start..pos]; break;
                case VideoSettings:
                    for (int p = start; p < pos;)
                    {
                        var (child, childSize, childStart) = Header(ref p);
                        p = End(childStart, childSize, pos);
                        if (child == PixelWidth) width = (int)UInt(childStart, p);
                        else if (child == PixelHeight) height = (int)UInt(childStart, p);
                    }
                    break;
                case AudioSettings:
                    for (int p = start; p < pos;)
                    {
                        var (child, childSize, childStart) = Header(ref p);
                        p = End(childStart, childSize, pos);
                        if (child == SamplingFrequency) rate = Float(childStart, p);
                        else if (child == Channels) channels = (int)UInt(childStart, p);
                    }
                    break;
            }
        }
        if (type == 1 && VideoCodec is null)
        {
            (_videoTrack, VideoCodec, Width, Height) = (number, codec, width, height);
        }
        else if (type == 2 && AudioCodec is null)
        {
            (_audioTrack, AudioCodec, SampleRate, AudioChannels) = (number, codec, (int)rate, channels);
            if (codec == "A_VORBIS" && codecPrivate.Length > 0)
                AudioHeaders.AddRange(Laced(codecPrivate, 1, codecPrivate.Span[0] + 1, xiph: true));
        }
    }

    /// <summary>
    /// A (Simple)Block: track number, timecode relative to the cluster, flags, then one frame or a lace of them.
    /// </summary>
    void ReadBlock(int pos, int end, long clusterTime, bool key = true)
    {
        var span = _data.Span;
        ulong track = VarInt(ref pos, stripMarker: true);
        if (track != _videoTrack && track != _audioTrack || pos + 3 > end) return;
        short relative = BinaryPrimitives.ReadInt16BigEndian(span[pos..]);
        byte flags = span[pos + 2];
        pos += 3;
        var time = TimeSpan.FromTicks((clusterTime + relative) * _timecodeScale / 100);
        if (key) key = (flags & 0x80) != 0; // a SimpleBlock says; a Block in a BlockGroup is treated as a delta frame
        var target = track == _videoTrack ? VideoFrames : AudioFrames;
        int lacing = (flags >> 1) & 3;
        if (lacing == 0)
        {
            target.Add(new WebmFrame(time, key, _data[pos..end]));
            return;
        }
        int count = span[pos] + 1;

        // Laced frames share the block's time (audio only; the player plays them back to back).
        foreach (var frame in Laced(_data[(pos + 1)..end], 0, count, xiph: lacing == 1, fixedSize: lacing == 2, ebml: lacing == 3))
            target.Add(new WebmFrame(time, key, frame));
    }

    /// <summary>
    /// Splits <paramref name="count"/> frames laced from <paramref name="offset"/>: Xiph sizes, EBML sizes or equal parts.
    /// </summary>
    IEnumerable<ReadOnlyMemory<byte>> Laced(ReadOnlyMemory<byte> lace, int offset, int count, bool xiph = false, bool fixedSize = false,
            bool ebml = false)
    {
        var span = lace.Span;
        var sizes = new long[count];
        int pos = offset;
        if (fixedSize)
        {
            for (int i = 0; i < count; i++) sizes[i] = (lace.Length - pos) / count;
        }
        else
        {
            long total = 0;
            for (int i = 0; i < count - 1; i++)
            {
                if (xiph)
                {
                    long size = 0;
                    byte b;
                    do
                    {
                        b = span[pos++];
                        size += b;
                    }
                    while (b == 255);
                    sizes[i] = size;
                }
                else if (ebml)
                {
                    // The first size is a plain number, each next one a signed difference from the one before.
                    var reader = new WebmFile(lace);
                    long raw = (long)reader.VarInt(ref pos, stripMarker: true, out int length);
                    sizes[i] = i == 0 ? raw : sizes[i - 1] + raw - ((1L << (7 * length - 1)) - 1);
                }
                total += sizes[i];
            }
            sizes[count - 1] = lace.Length - pos - total;
        }
        var frames = new List<ReadOnlyMemory<byte>>(count);
        foreach (var size in sizes)
        {
            if (size < 0 || pos + size > lace.Length) throw new InvalidDataException("bad lace");
            frames.Add(lace.Slice(pos, (int)size));
            pos += (int)size;
        }
        return frames;
    }

    #endregion

    #region EBML values

    /// <summary>
    /// An element's header at <paramref name="pos"/>: its ID, its size (<see cref="UnknownSize"/> when all ones) and where its data starts.
    /// </summary>
    (uint Id, long Size, int Start) Header(ref int pos)
    {
        uint id = (uint)VarInt(ref pos, stripMarker: false);
        ulong size = VarInt(ref pos, stripMarker: true, out int length);
        return (id, size == (1UL << (7 * length)) - 1 ? UnknownSize : (long)size, pos);
    }

    /// <summary>
    /// Where an element's data ends: its size, else the parent's end; never past the file (a file cut short).
    /// </summary>
    int End(int start, long size, int parentEnd = int.MaxValue)
    {
        long end = size == UnknownSize ? Math.Min(parentEnd, _data.Length) : start + size;
        if (end > _data.Length) end = _data.Length; // a file cut short: read what is there
        return (int)Math.Min(end, parentEnd);
    }

    /// <summary>
    /// A variable-length integer at <paramref name="pos"/>.
    /// </summary>
    ulong VarInt(ref int pos, bool stripMarker) => VarInt(ref pos, stripMarker, out _);

    /// <summary>
    /// An EBML variable-length number: the leading zero bits of its first byte give its length.
    /// </summary>
    ulong VarInt(ref int pos, bool stripMarker, out int length)
    {
        var span = _data.Span;
        if (pos >= span.Length) throw new InvalidDataException("WebM cut short");
        byte first = span[pos];
        length = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
        if (length is < 1 or > 8 || pos + length > span.Length) throw new InvalidDataException("bad EBML number");
        ulong value = stripMarker ? first & (0xFFu >> length) : first;
        for (int i = 1; i < length; i++) value = (value << 8) | span[pos + i];
        pos += length;
        return value;
    }

    /// <summary>
    /// A big-endian unsigned integer over the bytes.
    /// </summary>
    ulong UInt(int start, int end)
    {
        ulong value = 0;
        foreach (var b in _data.Span[start..end]) value = (value << 8) | b;
        return value;
    }

    /// <summary>
    /// A big-endian float of 4 or 8 bytes; 0 for any other length.
    /// </summary>
    double Float(int start, int end) => (end - start) switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(_data.Span[start..]),
        8 => BinaryPrimitives.ReadDoubleBigEndian(_data.Span[start..]),
        _ => 0,
    };

    #endregion
}
