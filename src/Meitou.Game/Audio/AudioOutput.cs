using Silk.NET.OpenAL;

namespace Meitou.Game.Audio;

/// <summary>
/// The sound card, through OpenAL Soft: one streaming stereo source fed by a thread that asks <see cref="_fill"/> for the next block of
/// samples whenever OpenAL has played one. All mixing happens before (in <see cref="WwisePlayer"/>); OpenAL only plays the result.
/// </summary>
public sealed unsafe class AudioOutput : IDisposable
{
    #region Output

    /// <summary>
    /// Frames per block: 1024 at 44100 Hz is 23 ms; four queued blocks keep about 90 ms ahead.
    /// </summary>
    public const int BlockFrames = 1024;
    /// <summary>
    /// The blocks queued on the source.
    /// </summary>
    const int Blocks = 4;

    /// <summary>
    /// The OpenAL Soft context API (the device, its context and extensions).
    /// </summary>
    readonly ALContext _alc;
    /// <summary>
    /// The OpenAL Soft API (the source and buffers).
    /// </summary>
    readonly AL _al;
    /// <summary>
    /// The output device.
    /// </summary>
    readonly Device* _device;
    /// <summary>
    /// The context made current on the device.
    /// </summary>
    readonly Context* _context;
    /// <summary>
    /// The streaming stereo source.
    /// </summary>
    readonly uint _source;
    /// <summary>
    /// The <see cref="Blocks"/> buffers the source cycles through.
    /// </summary>
    readonly uint[] _buffers;
    /// <summary>
    /// Fills a block with interleaved stereo samples (the mixer, <see cref="WwisePlayer.Mix"/>): the array and its frame count.
    /// </summary>
    readonly Action<float[], int> _fill;
    /// <summary>
    /// The feeding thread (<see cref="Run"/>).
    /// </summary>
    readonly Thread _thread;
    /// <summary>
    /// Cleared by <see cref="Dispose"/> to end the feeding thread.
    /// </summary>
    volatile bool _running = true;
    /// <summary>
    /// Set once the mixer has thrown: from then on the blocks are silence.
    /// </summary>
    bool _failed;

    /// <summary>
    /// The sample rate the blocks are played at, in Hz.
    /// </summary>
    public int SampleRate { get; }
    /// <summary>
    /// The device's name as OpenAL reports it; "default" when it reports none.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// Starts the source's buffers and the feeding thread on an opened device and context.
    /// </summary>
    AudioOutput(ALContext alc, AL al, Device* device, Context* context, int sampleRate, Action<float[], int> fill)
    {
        this._alc = alc;
        this._al = al;
        this._device = device;
        this._context = context;
        this._fill = fill;
        SampleRate = sampleRate;
        DeviceName = alc.GetContextProperty(device, GetContextString.DeviceSpecifier) ?? "default";
        _source = al.GenSource();
        _buffers = al.GenBuffers(Blocks);
        _thread = new Thread(Run) { Name = "Meitou audio", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Opens the default output device; null (with the reason) when there is none or OpenAL cannot start.
    /// </summary>
    public static AudioOutput? TryOpen(int sampleRate, Action<float[], int> fill, out string? error)
    {
        error = null;
        try
        {
            var alc = ALContext.GetApi(soft: true);
            var al = AL.GetApi(soft: true);
            var device = alc.OpenDevice("");
            if (device == null)
            {
                error = "no audio output device";
                return null;
            }
            var context = alc.CreateContext(device, null);
            if (context == null || !alc.MakeContextCurrent(context))
            {
                alc.CloseDevice(device);
                error = "OpenAL could not create a context";
                return null;
            }
            return new AudioOutput(alc, al, device, context, sampleRate, fill);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or FileNotFoundException or InvalidOperationException)
        {
            error = $"OpenAL Soft could not be loaded: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// The feeding thread: queues every buffer, starts the source, then every 5 ms refills the buffers OpenAL has played (restarting the source when
    /// it starved) and checks the device is still connected; a fault in the mixer silences the sound once, with a report, and never stops the host.
    /// </summary>
    void Run()
    {
        var mix = new float[BlockFrames * 2];
        var pcm = new short[BlockFrames * 2];
        void Queue(uint buffer)
        {
            Array.Clear(mix);
            if (!_failed)
            {
                // A fault in the mixer stops the sound, never the host: report it once and play silence from then on.
                try
                {
                    _fill(mix, BlockFrames);
                }
                catch (Exception e)
                {
                    _failed = true;
                    Array.Clear(mix);
                    Console.Error.WriteLine($"audio     stopped: {e.GetType().Name}: {e.Message}");
                }
            }
            Limit(mix);
            for (int i = 0; i < mix.Length; i++) pcm[i] = (short)Math.Clamp(mix[i] * 32767f, -32768f, 32767f);
            fixed (short* p = pcm) _al.BufferData(buffer, BufferFormat.Stereo16, p, pcm.Length * sizeof(short), SampleRate);
            _al.SourceQueueBuffers(_source, 1, &buffer);
        }
        foreach (var b in _buffers) Queue(b);
        _al.SourcePlay(_source);
        while (_running)
        {
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
            while (processed-- > 0)
            {
                uint buffer;
                _al.SourceUnqueueBuffers(_source, 1, &buffer);
                Queue(buffer);
            }
            // Starved (the machine stalled for longer than the queue): start again.
            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing) _al.SourcePlay(_source);
            KeepConnected();
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Ends the feeding thread, then frees the source, the buffers, the context and the device.
    /// </summary>
    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        _thread.Join();
        _al.SourceStop(_source);
        _al.DeleteSource(_source);
        _al.DeleteBuffers(_buffers);
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
        _al.Dispose();
        _alc.Dispose();
    }

    #endregion

    #region Limiter

    /// <summary>
    /// The limiter's ceiling (about −1 dB) and its release time (seconds).
    /// </summary>
    const float LimitCeiling = 0.89f, LimitRelease = 0.15f;
    /// <summary>
    /// The limiter's envelope carried from one block to the next (0 after a non-finite one).
    /// </summary>
    float _limitEnvelope;

    /// <summary>
    /// A peak limiter on the finished block (Meitou's, in both modes; docs/audio.md "Voice cap"): the louder of the two sides is followed with an
    /// instant attack and a <see cref="LimitRelease"/> release, and the gain keeps it at <see cref="LimitCeiling"/>, so a big fight's sum is turned
    /// down smoothly instead of clipped hard by the 16-bit conversion. Below the ceiling it changes nothing.
    /// </summary>
    void Limit(float[] mix)
    {
        float release = MathF.Exp(-1f / (LimitRelease * SampleRate)), envelope = _limitEnvelope;
        for (int i = 0; i + 1 < mix.Length; i += 2)
        {
            float peak = MathF.Max(MathF.Abs(mix[i]), MathF.Abs(mix[i + 1]));
            envelope = peak > envelope ? peak : envelope * release + peak * (1 - release);
            if (envelope <= LimitCeiling) continue;
            float gain = LimitCeiling / envelope;
            mix[i] *= gain;
            mix[i + 1] *= gain;
        }
        _limitEnvelope = float.IsFinite(envelope) ? envelope : 0;
    }

    #endregion

    #region Device reconnect

    /// <summary>
    /// ALC_CONNECTED (ALC_EXT_disconnect): 0 once the device is gone (unplugged headset, switched or disabled output).
    /// </summary>
    const int AlcConnected = 0x313;
    /// <summary>
    /// Milliseconds between two device checks.
    /// </summary>
    const int ReconnectCheckMs = 500;
    /// <summary>
    /// The <see cref="Environment.TickCount64"/> of the next device check.
    /// </summary>
    long _nextReconnectCheck;
    /// <summary>
    /// Whether the lost device has been reported and not reopened since, so a device that stays lost is reported once.
    /// </summary>
    bool _reportedDisconnect;

    /// <summary>
    /// A lost device stays silent forever in OpenAL, so every <see cref="ReconnectCheckMs"/> the device is checked and, when it is gone,
    /// reopened on the current default output (ALC_SOFT_reopen_device); the context, source and queue survive the reopen.
    /// </summary>
    void KeepConnected()
    {
        long now = Environment.TickCount64;
        if (now < _nextReconnectCheck) return;
        _nextReconnectCheck = now + ReconnectCheckMs;

        int connected = 1;
        _alc.GetContextProperty(_device, (GetContextInteger)AlcConnected, 1, &connected);
        if (connected != 0) return;

        bool reopened = Reopen();
        if (reopened || !_reportedDisconnect)
        {
            Console.Error.WriteLine(reopened ? "audio     output device lost; reopened on the default device" : "audio     output device lost");
        }
        _reportedDisconnect = !reopened;
    }

    /// <summary>
    /// Reopens the device on the current default output (<c>alcReopenDeviceSOFT</c>); false when the extension is missing or the reopen fails.
    /// </summary>
    bool Reopen()
    {
        var reopen = (delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte>)_alc.GetProcAddress(_device, "alcReopenDeviceSOFT");
        return reopen != null && reopen(_device, null, null) != 0;
    }

    #endregion
}