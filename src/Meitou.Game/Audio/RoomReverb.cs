namespace Meitou.Audio;

/// <summary>
/// A stereo room reverb standing in for the banks' Wwise RoomVerb effects (docs/audio.md#reverb): the plug-in is closed source, so this is a
/// clean-room Freeverb-style network (eight damped feedback combs and four series allpasses per side, a pre-delay, a stereo width mix) driven by
/// the effect's decoded decay time, HF damping, diffusion and stereo width. The wet signal has unity power for uncorrelated input before the
/// caller's level is applied. Runs on the mixer thread: about 24 delay-line taps per sample per side, nothing allocated while mixing.
/// </summary>
sealed class RoomReverb
{
    // Delay lengths in samples at 44100 Hz (the classic Freeverb tuning); the right side adds a spread.
    static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    static readonly int[] AllpassTunings = [556, 441, 341, 225];
    const int Spread = 23;
    const float MaxPreDelay = 0.25f;

    readonly int rate;
    readonly Comb[] combsLeft, combsRight;
    readonly Allpass[] allpassLeft, allpassRight;
    readonly float[] preLeft;
    int preIndex;

    // Parameters (set by Configure); the combs' feedback follows the decay time.
    float decay = 1, damping = 0.4f, diffusion = 0.5f, width = 1, preDelaySeconds;
    float inputNorm = 1;
    // Two one-pole high-passes on the input (about 250 Hz): below the first comb resonances every comb has the same gain and they add coherently
    // (up to 25 dB more), which a real room does not do to low rumble.
    float highPassA, hp1, hp2;
    float enable, enableTarget;

    int silentBlocks;

    public RoomReverb(int sampleRate)
    {
        rate = sampleRate;
        float scale = sampleRate / 44100f;
        combsLeft = [.. CombTunings.Select(t => new Comb((int)(t * scale)))];
        combsRight = [.. CombTunings.Select(t => new Comb((int)((t + Spread) * scale)))];
        allpassLeft = [.. AllpassTunings.Select(t => new Allpass((int)(t * scale)))];
        allpassRight = [.. AllpassTunings.Select(t => new Allpass((int)((t + Spread) * scale)))];
        int pre = (int)(MaxPreDelay * sampleRate) + 1;
        preLeft = new float[pre];
        highPassA = 1 - MathF.Exp(-2 * MathF.PI * 250f / sampleRate);
        Configure(1, 5, 50, 100, 0);
    }

    /// <summary>True when the tail has died away (several blocks of silence): with nothing going in, the caller may skip <see cref="Process"/>.</summary>
    public bool TailDead => silentBlocks > 4;

    /// <summary>
    /// <paramref name="decaySeconds"/>: time for the tail to fall 60 dB; <paramref name="hfDampingKHz"/>: the high-frequency damping (higher
    /// frequencies die sooner above it); <paramref name="diffusionPercent"/> 0..100; <paramref name="widthDegrees"/> 0..180 (0 mono wet);
    /// <paramref name="preDelayMs"/>.
    /// </summary>
    public void Configure(float decaySeconds, float hfDampingKHz, float diffusionPercent, float widthDegrees, float preDelayMs)
    {
        decay = Math.Clamp(decaySeconds, 0.05f, 20f);
        // One-pole low-pass in each comb's loop; the loop runs every 25-37 ms, so a cutoff of twice the stated frequency keeps the damping gentle.
        float cutoff = Math.Clamp(hfDampingKHz * 2000f, 500f, rate * 0.45f);
        damping = MathF.Exp(-2 * MathF.PI * cutoff / rate);
        diffusion = Math.Clamp(diffusionPercent / 100f, 0, 1);
        width = Math.Clamp(widthDegrees / 180f, 0, 1);
        preDelaySeconds = Math.Clamp(preDelayMs / 1000f, 0, MaxPreDelay - 0.001f);
        float gSum = 0;
        for (int i = 0; i < combsLeft.Length; i++)
        {
            float g = MathF.Pow(10, -3f * combsLeft[i].Length / rate / decay);
            combsLeft[i].Feedback = combsRight[i].Feedback = g;
            gSum += g * g;
        }
        float gMean = gSum / combsLeft.Length;
        // A white input through a comb bank gains sqrt(mean 1 / (1 - g²)) in power; undo it so the wet level is the caller's.
        inputNorm = MathF.Sqrt(1 - Math.Min(gMean, 0.9999f)) / MathF.Sqrt(combsLeft.Length);
        float apGain = 0.3f + 0.4f * diffusion;
        foreach (var a in allpassLeft) a.Feedback = apGain;
        foreach (var a in allpassRight) a.Feedback = apGain;
    }

    /// <summary>The effect is on (the input is let in) or bypassed (the input is shut and the tail rings out).</summary>
    public bool Enabled
    {
        get => enableTarget > 0;
        set => enableTarget = value ? 1 : 0;
    }

    /// <summary>
    /// Reverberates <paramref name="frames"/> frames of interleaved stereo <paramref name="input"/> and adds the wet signal times
    /// <paramref name="level"/> (linear) to <paramref name="output"/>.
    /// </summary>
    public void Process(float[] input, float[] output, int frames, float level)
    {
        float peak = 0;
        float step = 1f / (0.05f * rate); // the input gate ramps over 50 ms
        float wet1 = (1 + width) / 2, wet2 = (1 - width) / 2;
        int pre = preLeft.Length;
        int delay = (int)(preDelaySeconds * rate);
        for (int f = 0; f < frames; f++)
        {
            if (enable < enableTarget) enable = Math.Min(enableTarget, enable + step);
            else if (enable > enableTarget) enable = Math.Max(enableTarget, enable - step);
            float mono = (input[2 * f] + input[2 * f + 1]) * enable;
            hp1 += highPassA * (mono - hp1);
            float stage = mono - hp1;
            hp2 += highPassA * (stage - hp2);
            preLeft[preIndex] = stage - hp2;
            int read = preIndex - delay;
            if (read < 0) read += pre;
            if (++preIndex == pre) preIndex = 0;
            float x = preLeft[read] * inputNorm;
            float outL = 0, outR = 0;
            for (int i = 0; i < combsLeft.Length; i++)
            {
                outL += combsLeft[i].Tick(x, damping);
                outR += combsRight[i].Tick(x, damping);
            }
            for (int i = 0; i < allpassLeft.Length; i++)
            {
                outL = allpassLeft[i].Tick(outL);
                outR = allpassRight[i].Tick(outR);
            }
            float yl = (outL * wet1 + outR * wet2) * level, yr = (outR * wet1 + outL * wet2) * level;
            output[2 * f] += yl;
            output[2 * f + 1] += yr;
            peak = Math.Max(peak, Math.Max(Math.Abs(outL), Math.Abs(outR)));
        }
        if (peak < 1e-6f) silentBlocks++;
        else silentBlocks = 0;
    }

    /// <summary>Forgets the tail.</summary>
    public void Clear()
    {
        foreach (var c in combsLeft) c.Clear();
        foreach (var c in combsRight) c.Clear();
        foreach (var a in allpassLeft) a.Clear();
        foreach (var a in allpassRight) a.Clear();
        Array.Clear(preLeft);
        hp1 = hp2 = 0;
        silentBlocks = 0;
    }

    sealed class Comb(int length)
    {
        readonly float[] buffer = new float[Math.Max(1, length)];
        int index;
        float store;
        public float Feedback;
        public int Length => buffer.Length;

        public float Tick(float input, float damping)
        {
            float output = buffer[index];
            store = output * (1 - damping) + store * damping;
            buffer[index] = input + store * Feedback;
            if (++index == buffer.Length) index = 0;
            return output;
        }

        public void Clear()
        {
            Array.Clear(buffer);
            store = 0;
        }
    }

    sealed class Allpass(int length)
    {
        readonly float[] buffer = new float[Math.Max(1, length)];
        int index;
        public float Feedback = 0.5f;

        public float Tick(float input)
        {
            float delayed = buffer[index];
            float w = input + delayed * Feedback;
            float output = delayed - Feedback * w;
            buffer[index] = w;
            if (++index == buffer.Length) index = 0;
            return output;
        }

        public void Clear() => Array.Clear(buffer);
    }
}
