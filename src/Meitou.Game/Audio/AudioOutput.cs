using Silk.NET.OpenAL;

namespace Meitou.Game.Audio;

/// <summary>
/// The sound card, through OpenAL Soft: one streaming stereo source fed by a thread that asks <see cref="fill"/> for the next block of
/// samples whenever OpenAL has played one. All mixing happens before (in <see cref="WwisePlayer"/>); OpenAL only plays the result.
/// </summary>
public sealed unsafe class AudioOutput : IDisposable
{
    /// <summary>Frames per block: 1024 at 44100 Hz is 23 ms; four queued blocks keep about 90 ms ahead.</summary>
    public const int BlockFrames = 1024;
    const int Blocks = 4;

    readonly ALContext alc;
    readonly AL al;
    readonly Device* device;
    readonly Context* context;
    readonly uint source;
    readonly uint[] buffers;
    readonly Action<float[], int> fill;
    readonly Thread thread;
    volatile bool running = true;
    bool failed;

    public int SampleRate { get; }
    public string DeviceName { get; }

    AudioOutput(ALContext alc, AL al, Device* device, Context* context, int sampleRate, Action<float[], int> fill)
    {
        this.alc = alc;
        this.al = al;
        this.device = device;
        this.context = context;
        this.fill = fill;
        SampleRate = sampleRate;
        DeviceName = alc.GetContextProperty(device, GetContextString.DeviceSpecifier) ?? "default";
        source = al.GenSource();
        buffers = al.GenBuffers(Blocks);
        thread = new Thread(Run) { Name = "Meitou audio", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Opens the default output device; null (with the reason) when there is none or OpenAL cannot start.</summary>
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

    /// <summary>The limiter's ceiling (about −1 dB) and its release time (seconds).</summary>
    const float LimitCeiling = 0.89f, LimitRelease = 0.15f;
    float limitEnvelope;

    /// <summary>
    /// A peak limiter on the finished block (Meitou's, in both modes; docs/audio.md "Voice cap"): the louder of the two sides is
    /// followed with an instant attack and a <see cref="LimitRelease"/> release, and the gain keeps it at <see cref="LimitCeiling"/>, so a big fight's sum is
    /// turned down smoothly instead of clipped hard by the 16-bit conversion. Below the ceiling it changes nothing.
    /// </summary>
    void Limit(float[] mix)
    {
        float release = MathF.Exp(-1f / (LimitRelease * SampleRate)), envelope = limitEnvelope;
        for (int i = 0; i + 1 < mix.Length; i += 2)
        {
            float peak = MathF.Max(MathF.Abs(mix[i]), MathF.Abs(mix[i + 1]));
            envelope = peak > envelope ? peak : envelope * release + peak * (1 - release);
            if (envelope <= LimitCeiling) continue;
            float gain = LimitCeiling / envelope;
            mix[i] *= gain;
            mix[i + 1] *= gain;
        }
        limitEnvelope = float.IsFinite(envelope) ? envelope : 0;
    }

    void Run()
    {
        var mix = new float[BlockFrames * 2];
        var pcm = new short[BlockFrames * 2];
        void Queue(uint buffer)
        {
            Array.Clear(mix);
            if (!failed)
            {
                // A fault in the mixer stops the sound, never the host: report it once and play silence from then on.
                try { fill(mix, BlockFrames); }
                catch (Exception e)
                {
                    failed = true;
                    Array.Clear(mix);
                    Console.Error.WriteLine($"audio     stopped: {e.GetType().Name}: {e.Message}");
                }
            }
            Limit(mix);
            for (int i = 0; i < mix.Length; i++) pcm[i] = (short)Math.Clamp(mix[i] * 32767f, -32768f, 32767f);
            fixed (short* p = pcm) al.BufferData(buffer, BufferFormat.Stereo16, p, pcm.Length * sizeof(short), SampleRate);
            al.SourceQueueBuffers(source, 1, &buffer);
        }
        foreach (var b in buffers) Queue(b);
        al.SourcePlay(source);
        while (running)
        {
            al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out int processed);
            while (processed-- > 0)
            {
                uint buffer;
                al.SourceUnqueueBuffers(source, 1, &buffer);
                Queue(buffer);
            }
            // Starved (the machine stalled for longer than the queue): start again.
            al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing) al.SourcePlay(source);
            KeepConnected();
            Thread.Sleep(5);
        }
    }

    /// <summary>ALC_CONNECTED (ALC_EXT_disconnect): 0 once the device is gone (unplugged headset, switched or disabled output).</summary>
    const int AlcConnected = 0x313;
    const int ReconnectCheckMs = 500;
    long nextReconnectCheck;
    bool reportedDisconnect;

    /// <summary>
    /// A lost device stays silent forever in OpenAL, so every <see cref="ReconnectCheckMs"/> the device is checked and, when it is gone,
    /// reopened on the current default output (ALC_SOFT_reopen_device); the context, source and queue survive the reopen.
    /// </summary>
    void KeepConnected()
    {
        long now = Environment.TickCount64;
        if (now < nextReconnectCheck) return;
        nextReconnectCheck = now + ReconnectCheckMs;

        int connected = 1;
        alc.GetContextProperty(device, (GetContextInteger)AlcConnected, 1, &connected);
        if (connected != 0) return;

        bool reopened = Reopen();
        if (reopened || !reportedDisconnect)
        {
            Console.Error.WriteLine(reopened ? "audio     output device lost; reopened on the default device" : "audio     output device lost");
        }
        reportedDisconnect = !reopened;
    }

    bool Reopen()
    {
        var reopen = (delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte>)alc.GetProcAddress(device, "alcReopenDeviceSOFT");
        return reopen != null && reopen(device, null, null) != 0;
    }

    public void Dispose()
    {
        if (!running) return;
        running = false;
        thread.Join();
        al.SourceStop(source);
        al.DeleteSource(source);
        al.DeleteBuffers(buffers);
        alc.MakeContextCurrent(null);
        alc.DestroyContext(context);
        alc.CloseDevice(device);
        al.Dispose();
        alc.Dispose();
    }
}
