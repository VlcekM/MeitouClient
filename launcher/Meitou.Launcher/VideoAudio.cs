using System.Runtime.InteropServices;

namespace Meitou.Launcher;

/// <summary>
/// A news video's sound: its Vorbis frames decoded by NVorbis, played through OpenAL Soft (the game's audio library, embedded like
/// Skia; <see cref="NativeLibraries"/>) as one streaming source. <see cref="Position"/> is the clock the pictures follow.
/// </summary>
sealed partial class VideoAudio : IDisposable
{
    /// <summary>
    /// The OpenAL library's name.
    /// </summary>
    const string Al = "openal";

    /// <summary>
    /// Samples per channel in one streamed block, and how many blocks are queued.
    /// </summary>
    const int BlockFrames = 2048, Blocks = 4;

    /// <summary>
    /// OpenAL enum values: the 16-bit mono and stereo formats, the processed-buffers, state and sample-offset queries and the playing state.
    /// </summary>
    const int AlFormatMono16 = 0x1101, AlFormatStereo16 = 0x1103, AlBuffersProcessed = 0x1016, AlSourceState = 0x1010, AlPlaying = 0x1012,
        AlSampleOffset = 0x1025;

    /// <summary>
    /// The OpenAL device and context.
    /// </summary>
    readonly IntPtr _device, _context;

    /// <summary>
    /// The streaming source.
    /// </summary>
    readonly uint _source;

    /// <summary>
    /// The queued buffers.
    /// </summary>
    readonly uint[] _buffers = new uint[Blocks];

    /// <summary>
    /// The Vorbis decoder of the video's sound.
    /// </summary>
    readonly NVorbis.StreamDecoder _decoder;

    /// <summary>
    /// The track's channels, and how many are played (at most two).
    /// </summary>
    readonly int _channels, _outChannels;

    /// <summary>
    /// The thread that decodes and queues the blocks.
    /// </summary>
    readonly Thread _thread;

    /// <summary>
    /// Cleared by <see cref="Dispose"/> to stop the thread.
    /// </summary>
    volatile bool _running = true;

    /// <summary>
    /// Frames in buffers OpenAL has finished with (the clock's base).
    /// </summary>
    long _playedFrames; // frames in buffers OpenAL has finished with

    /// <summary>
    /// Guards <see cref="_playedFrames"/> against the clock's reads.
    /// </summary>
    readonly Lock _clock = new();

    /// <summary>
    /// The sound's sample rate.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// The time in the video the sound starts at (its first frame at or after the time asked for).
    /// </summary>
    public TimeSpan StartTime { get; }

    /// <summary>
    /// Opens the source and its buffers on <paramref name="device"/>'s context and makes the stream's thread; <paramref name="start"/> is where the
    /// sound starts.
    /// </summary>
    VideoAudio(IntPtr device, IntPtr context, NVorbis.StreamDecoder decoder, TimeSpan start)
    {
        this._device = device;
        this._context = context;
        this._decoder = decoder;
        StartTime = start;
        _channels = decoder.Channels;
        _outChannels = Math.Min(_channels, 2);
        SampleRate = decoder.SampleRate;
        AlGenSources(1, out _source);
        AlGenBuffers(Blocks, _buffers);
        _thread = new Thread(Run) { Name = "News video sound", IsBackground = true };
    }

    /// <summary>
    /// The Vorbis track's player from <paramref name="from"/>, not yet started; null (silent video) when OpenAL or the stream cannot be opened.
    /// </summary>
    public static VideoAudio? TryOpen(WebmFile file, TimeSpan from)
    {
        if (file.AudioCodec != "A_VORBIS" || file.AudioHeaders.Count != 3) return null;
        int first = file.AudioFrames.FindIndex(f => f.Time >= from);
        if (first < 0) return null; // past the sound's end
        IntPtr device = 0, context = 0;
        try
        {
            device = AlcOpenDevice(null);
            if (device == 0) return null;
            context = alcCreateContext(device, 0);
            if (context == 0 || !AlcMakeContextCurrent(context))
                throw new InvalidOperationException("no OpenAL context");
            return new VideoAudio(device, context, Decoder(file, first), file.AudioFrames[first].Time);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or InvalidDataException
                                      or ArgumentException or IndexOutOfRangeException)
        {
            if (context != 0) alcDestroyContext(context);
            if (device != 0) AlcCloseDevice(device);
            return null;
        }
    }

    /// <summary>
    /// The Vorbis track as samples: its headers, then its frames from <paramref name="first"/> (Vorbis can start at any packet).
    /// </summary>
    internal static NVorbis.StreamDecoder Decoder(WebmFile file, int first = 0) => new(new VorbisPackets(file, first)) { ClipSamples = true };

    /// <summary>
    /// Starts playing.
    /// </summary>
    public void Start() => _thread.Start();

    /// <summary>
    /// How far the sound has played: from OpenAL's position in the playing buffer, so it is smooth between blocks.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            lock (_clock)
            {
                AlGetSourcei(_source, AlSampleOffset, out int offset);
                return StartTime + TimeSpan.FromSeconds((double)(_playedFrames + offset) / SampleRate);
            }
        }
    }

    /// <summary>
    /// True once every sample has been played (or the stream ended early).
    /// </summary>
    public bool Finished { get; private set; }

    /// <summary>
    /// The stream's thread: decodes blocks into 16-bit samples, queues them and refills the buffers OpenAL has played, until the stream ends or the
    /// sound is disposed.
    /// </summary>
    void Run()
    {
        var samples = new float[BlockFrames * _channels];
        var pcm = new short[BlockFrames * _outChannels];
        var free = new Queue<uint>(_buffers);
        bool ended = false;
        while (_running)
        {
            UnqueueProcessed(free);
            while (!ended && free.Count > 0)
            {
                int read = ReadBlock(samples);
                if (read <= 0)
                {
                    ended = true;
                    break;
                }

                int frames = read / _channels;
                ConvertToPcm(samples, pcm, frames);
                QueueBlock(free.Dequeue(), pcm, frames);
            }

            AlGetSourcei(_source, AlSourceState, out int state);
            if (state != AlPlaying)
            {
                if (ended && free.Count == Blocks)
                {
                    Finished = true;
                    return;
                }

                alSourcePlay(_source); // first start, or after running dry
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Takes the buffers the source has played back to the free queue and counts their frames.
    /// </summary>
    void UnqueueProcessed(Queue<uint> free)
    {
        lock (_clock)
        {
            AlGetSourcei(_source, AlBuffersProcessed, out int processed);
            for (int i = 0; i < processed; i++)
            {
                var done = new uint[1];
                AlSourceUnqueueBuffers(_source, 1, done);
                AlGetBufferi(done[0], 0x2004 /* AL_SIZE */, out int bytes);
                _playedFrames += bytes / (2 * _outChannels);
                free.Enqueue(done[0]);
            }
        }
    }

    /// <summary>
    /// Decodes the next block of samples; a corrupt stream ends as if it had no more samples.
    /// </summary>
    int ReadBlock(float[] samples)
    {
        try
        {
            return _decoder.Read(samples, 0, samples.Length);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Converts <paramref name="frames"/> frames of samples to 16-bit PCM in the output's channel layout.
    /// </summary>
    void ConvertToPcm(float[] samples, short[] pcm, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < _outChannels; c++)
                pcm[f * _outChannels + c] = (short)Math.Clamp(samples[f * _channels + c] * 32767f, -32768f, 32767f);
        }
    }

    /// <summary>
    /// Fills a free buffer with <paramref name="frames"/> frames of PCM and queues it on the source.
    /// </summary>
    void QueueBlock(uint buffer, short[] pcm, int frames)
    {
        lock (_clock)
        {
            AlBufferData(buffer, _outChannels == 2 ? AlFormatStereo16 : AlFormatMono16, pcm, frames * _outChannels * 2, SampleRate);
            AlSourceQueueBuffers(_source, 1, [buffer]);
        }
    }

    /// <summary>
    /// Stops the thread and the source and frees the OpenAL objects and the decoder.
    /// </summary>
    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join();
        alSourceStop(_source);
        AlDeleteSources(1, [_source]);
        AlDeleteBuffers(Blocks, _buffers);
        AlcMakeContextCurrent(0);
        alcDestroyContext(_context);
        AlcCloseDevice(_device);
        _decoder.Dispose();
    }

    /// <summary>
    /// The track's three headers, then its frames, as NVorbis packets.
    /// </summary>
    sealed class VorbisPackets(WebmFile file, int first) : NVorbis.Contracts.IPacketProvider
    {
        /// <summary>
        /// The next packet to hand out.
        /// </summary>
        int _next;

        /// <summary>
        /// The packets there are: three headers and the frames from <c>first</c> on.
        /// </summary>
        int Count => 3 + file.AudioFrames.Count - first;

        /// <summary>
        /// The packets cannot be sought.
        /// </summary>
        public bool CanSeek => false;

        /// <summary>
        /// A single stream.
        /// </summary>
        public int StreamSerial => 0;

        /// <summary>
        /// The next packet, null at the end.
        /// </summary>
        public NVorbis.Contracts.IPacket? GetNextPacket() => _next < Count ? Packet(_next++) : null;

        /// <summary>
        /// The next packet without taking it, null at the end.
        /// </summary>
        public NVorbis.Contracts.IPacket? PeekNextPacket() => _next < Count ? Packet(_next) : null;

        /// <summary>
        /// The stream's length in samples: the last frame's time at the sample rate.
        /// </summary>
        public long GetGranuleCount() => (long)((file.AudioFrames.Count > 0 ? file.AudioFrames[^1].Time.TotalSeconds : 0) * file.SampleRate);

        /// <summary>
        /// Not supported (<see cref="CanSeek"/> is false).
        /// </summary>
        public long SeekTo(long granulePos, int preRoll, NVorbis.Contracts.GetPacketGranuleCount getPacketGranuleCount) =>
            throw new NotSupportedException();

        /// <summary>
        /// Packet <paramref name="index"/>: one of the headers, else a frame; the last one ends the stream.
        /// </summary>
        Packet Packet(int index) =>
            new(index < 3 ? file.AudioHeaders[index] : file.AudioFrames[first + index - 3].Data) { IsEndOfStream = index == Count - 1 };
    }

    /// <summary>
    /// A packet over a header's or frame's bytes.
    /// </summary>
    sealed class Packet(ReadOnlyMemory<byte> data) : NVorbis.DataPacket
    {
        /// <summary>
        /// The bytes read.
        /// </summary>
        int _read;

        /// <inheritdoc/>
        protected override int TotalBits => data.Length * 8;
        protected override int ReadNextByte() => _read < data.Length ? data.Span[_read++] : -1;

        public override void Reset()
        {
            _read = 0;
            base.Reset();
        }
    }

    // OpenAL 1.1, the few calls a streaming source needs.
    /// <summary>
    /// <c>alcOpenDevice</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial IntPtr AlcOpenDevice([MarshalAs(UnmanagedType.LPStr)] string? name);

    /// <summary>
    /// <c>alcCreateContext</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial IntPtr alcCreateContext(IntPtr device, IntPtr attributes);

    /// <summary>
    /// <c>alcMakeContextCurrent</c>.
    /// </summary>
    [LibraryImport(Al)] [return: MarshalAs(UnmanagedType.U1)] private static partial bool AlcMakeContextCurrent(IntPtr context);

    /// <summary>
    /// <c>alcDestroyContext</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void alcDestroyContext(IntPtr context);

    /// <summary>
    /// <c>alcCloseDevice</c>.
    /// </summary>
    [LibraryImport(Al)] [return: MarshalAs(UnmanagedType.U1)] private static partial bool AlcCloseDevice(IntPtr device);

    /// <summary>
    /// <c>alGenSources</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlGenSources(int count, out uint source);

    /// <summary>
    /// <c>alDeleteSources</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlDeleteSources(int count, uint[] sources);

    /// <summary>
    /// <c>alGenBuffers</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlGenBuffers(int count, [Out] uint[] buffers);

    /// <summary>
    /// <c>alDeleteBuffers</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlDeleteBuffers(int count, uint[] buffers);

    /// <summary>
    /// <c>alBufferData</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlBufferData(uint buffer, int format, short[] data, int size, int frequency);

    /// <summary>
    /// <c>alGetBufferi</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlGetBufferi(uint buffer, int param, out int value);

    /// <summary>
    /// <c>alSourceQueueBuffers</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlSourceQueueBuffers(uint source, int count, uint[] buffers);

    /// <summary>
    /// <c>alSourceUnqueueBuffers</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlSourceUnqueueBuffers(uint source, int count, [Out] uint[] buffers);

    /// <summary>
    /// <c>alGetSourcei</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void AlGetSourcei(uint source, int param, out int value);

    /// <summary>
    /// <c>alSourcePlay</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void alSourcePlay(uint source);

    /// <summary>
    /// <c>alSourceStop</c>.
    /// </summary>
    [LibraryImport(Al)] private static partial void alSourceStop(uint source);
}
