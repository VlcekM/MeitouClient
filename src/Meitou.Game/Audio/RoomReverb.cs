namespace Meitou.Game.Audio;

/// <summary>
/// A stereo room reverb standing in for the banks' Wwise RoomVerb effects (docs/audio.md#reverb): the plug-in is closed source, so this is a
/// clean-room Freeverb-style network (eight damped feedback combs and four series allpasses per side, a pre-delay, a stereo width mix) driven by
/// the effect's decoded decay time, HF damping, diffusion and stereo width. The wet signal has unity power for uncorrelated input before the
/// caller's level is applied. Runs on the mixer thread: about 24 delay-line taps per sample per side, nothing allocated while mixing.
/// </summary>
sealed class RoomReverb
{
    /// <summary>
    /// The eight combs' delay lengths in samples at 44100 Hz (the classic Freeverb tuning); the right side adds <see cref="Spread"/>.
    /// </summary>
    static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    /// <summary>
    /// The four series allpasses' delay lengths in samples at 44100 Hz (the classic Freeverb tuning).
    /// </summary>
    static readonly int[] AllpassTunings = [556, 441, 341, 225];
    /// <summary>
    /// Samples the right side's delay lines add to the left's, at 44100 Hz.
    /// </summary>
    const int Spread = 23;
    /// <summary>
    /// The longest pre-delay, in seconds (the pre-delay line's length).
    /// </summary>
    const float MaxPreDelay = 0.25f;

    /// <summary>
    /// The sample rate, in Hz.
    /// </summary>
    readonly int _rate;
    /// <summary>
    /// The feedback combs of each side, run in parallel.
    /// </summary>
    readonly Comb[] _combsLeft, _combsRight;
    /// <summary>
    /// The allpasses of each side, run in series after the combs.
    /// </summary>
    readonly Allpass[] _allpassLeft, _allpassRight;
    /// <summary>
    /// The pre-delay line of the mono input, <see cref="MaxPreDelay"/> long.
    /// </summary>
    readonly float[] _preLeft;
    /// <summary>
    /// The pre-delay line's write position.
    /// </summary>
    int _preIndex;

    /// <summary>
    /// The parameters set by <see cref="Configure"/>: decay time (s), the combs' damping coefficient, diffusion 0..1, width 0..1 and pre-delay (s);
    /// the combs' feedback follows the decay time.
    /// </summary>
    float _decay = 1, _damping = 0.4f, _diffusion = 0.5f, _width = 1, _preDelaySeconds;
    /// <summary>
    /// The input gain that undoes the comb bank's power gain, so the wet level is the caller's.
    /// </summary>
    float _inputNorm = 1;

    /// <summary>
    /// Two one-pole high-passes on the input (about 250 Hz): below the first comb resonances every comb has the same gain and they add coherently (up
    /// to 25 dB more), which a real room does not do to low rumble. This is the coefficient they share.
    /// </summary>
    private readonly float _highPassA;
    /// <summary>
    /// The first high-pass's low-pass state.
    /// </summary>
    private float _hp1;
    /// <summary>
    /// The second high-pass's low-pass state.
    /// </summary>
    private float _hp2;
    /// <summary>
    /// The input gate (0 shut, 1 open) and where it ramps to over 50 ms (<see cref="Enabled"/>).
    /// </summary>
    float _enable, _enableTarget;

    /// <summary>
    /// Blocks in a row whose wet output stayed below 1e-6 (<see cref="TailDead"/>).
    /// </summary>
    int _silentBlocks;

    /// <summary>
    /// Builds the delay lines for <paramref name="sampleRate"/> (the tunings scaled from 44100 Hz) and configures a 1 s room.
    /// </summary>
    public RoomReverb(int sampleRate)
    {
        _rate = sampleRate;
        float scale = sampleRate / 44100f;
        _combsLeft = [.. CombTunings.Select(t => new Comb((int)(t * scale)))];
        _combsRight = [.. CombTunings.Select(t => new Comb((int)((t + Spread) * scale)))];
        _allpassLeft = [.. AllpassTunings.Select(t => new Allpass((int)(t * scale)))];
        _allpassRight = [.. AllpassTunings.Select(t => new Allpass((int)((t + Spread) * scale)))];
        int pre = (int)(MaxPreDelay * sampleRate) + 1;
        _preLeft = new float[pre];
        _highPassA = 1 - MathF.Exp(-2 * MathF.PI * 250f / sampleRate);
        Configure(1, 5, 50, 100, 0);
    }

    /// <summary>
    /// True when the tail has died away (several blocks of silence): with nothing going in, the caller may skip <see cref="Process"/>.
    /// </summary>
    public bool TailDead => _silentBlocks > 4;

    /// <summary>
    /// <paramref name="decaySeconds"/>: time for the tail to fall 60 dB; <paramref name="hfDampingKHz"/>: the high-frequency damping (higher
    /// frequencies die sooner above it); <paramref name="diffusionPercent"/> 0..100; <paramref name="widthDegrees"/> 0..180 (0 mono wet);
    /// <paramref name="preDelayMs"/>.
    /// </summary>
    public void Configure(float decaySeconds, float hfDampingKHz, float diffusionPercent, float widthDegrees, float preDelayMs)
    {
        _decay = Math.Clamp(decaySeconds, 0.05f, 20f);
        // One-pole low-pass in each comb's loop; the loop runs every 25-37 ms, so a cutoff of twice the stated frequency keeps the damping gentle.
        float cutoff = Math.Clamp(hfDampingKHz * 2000f, 500f, _rate * 0.45f);
        _damping = MathF.Exp(-2 * MathF.PI * cutoff / _rate);
        _diffusion = Math.Clamp(diffusionPercent / 100f, 0, 1);
        _width = Math.Clamp(widthDegrees / 180f, 0, 1);
        _preDelaySeconds = Math.Clamp(preDelayMs / 1000f, 0, MaxPreDelay - 0.001f);
        float gSum = 0;
        for (int i = 0; i < _combsLeft.Length; i++)
        {
            float g = MathF.Pow(10, -3f * _combsLeft[i].Length / _rate / _decay);
            _combsLeft[i].Feedback = _combsRight[i].Feedback = g;
            gSum += g * g;
        }
        float gMean = gSum / _combsLeft.Length;
        // A white input through a comb bank gains sqrt(mean 1 / (1 - g²)) in power; undo it so the wet level is the caller's.
        _inputNorm = MathF.Sqrt(1 - Math.Min(gMean, 0.9999f)) / MathF.Sqrt(_combsLeft.Length);
        float apGain = 0.3f + 0.4f * _diffusion;
        foreach (var a in _allpassLeft) a.Feedback = apGain;
        foreach (var a in _allpassRight) a.Feedback = apGain;
    }

    /// <summary>
    /// The effect is on (the input is let in) or bypassed (the input is shut and the tail rings out).
    /// </summary>
    public bool Enabled
    {
        get => _enableTarget > 0;
        set => _enableTarget = value ? 1 : 0;
    }

    /// <summary>
    /// Reverberates <paramref name="frames"/> frames of interleaved stereo <paramref name="input"/> and adds the wet signal times
    /// <paramref name="level"/> (linear) to <paramref name="output"/>.
    /// </summary>
    public void Process(float[] input, float[] output, int frames, float level)
    {
        float peak = 0;
        float step = 1f / (0.05f * _rate); // the input gate ramps over 50 ms
        float wet1 = (1 + _width) / 2, wet2 = (1 - _width) / 2;
        int pre = _preLeft.Length;
        int delay = (int)(_preDelaySeconds * _rate);
        for (int f = 0; f < frames; f++)
        {
            if (_enable < _enableTarget) _enable = Math.Min(_enableTarget, _enable + step);
            else if (_enable > _enableTarget) _enable = Math.Max(_enableTarget, _enable - step);
            float mono = (input[2 * f] + input[2 * f + 1]) * _enable;
            _hp1 += _highPassA * (mono - _hp1);
            float stage = mono - _hp1;
            _hp2 += _highPassA * (stage - _hp2);
            _preLeft[_preIndex] = stage - _hp2;
            int read = _preIndex - delay;
            if (read < 0) read += pre;
            if (++_preIndex == pre) _preIndex = 0;
            float x = _preLeft[read] * _inputNorm;
            float outL = 0, outR = 0;
            for (int i = 0; i < _combsLeft.Length; i++)
            {
                outL += _combsLeft[i].Tick(x, _damping);
                outR += _combsRight[i].Tick(x, _damping);
            }
            for (int i = 0; i < _allpassLeft.Length; i++)
            {
                outL = _allpassLeft[i].Tick(outL);
                outR = _allpassRight[i].Tick(outR);
            }
            float yl = (outL * wet1 + outR * wet2) * level, yr = (outR * wet1 + outL * wet2) * level;
            output[2 * f] += yl;
            output[2 * f + 1] += yr;
            peak = Math.Max(peak, Math.Max(Math.Abs(outL), Math.Abs(outR)));
        }
        if (peak < 1e-6f) _silentBlocks++;
        else _silentBlocks = 0;
    }

    /// <summary>
    /// Forgets the tail.
    /// </summary>
    public void Clear()
    {
        foreach (var c in _combsLeft) c.Clear();
        foreach (var c in _combsRight) c.Clear();
        foreach (var a in _allpassLeft) a.Clear();
        foreach (var a in _allpassRight) a.Clear();
        Array.Clear(_preLeft);
        _hp1 = _hp2 = 0;
        _silentBlocks = 0;
    }

    /// <summary>
    /// One damped feedback comb: a delay line whose output is low-passed and fed back.
    /// </summary>
    sealed class Comb(int length)
    {
        /// <summary>
        /// The delay line, at least one sample.
        /// </summary>
        readonly float[] _buffer = new float[Math.Max(1, length)];
        /// <summary>
        /// The delay line's position.
        /// </summary>
        int _index;
        /// <summary>
        /// The loop's low-pass state.
        /// </summary>
        float _store;
        /// <summary>
        /// The loop's feedback gain (from the decay time, <see cref="Configure"/>).
        /// </summary>
        public float Feedback;
        /// <summary>
        /// The delay in samples.
        /// </summary>
        public int Length => _buffer.Length;

        /// <summary>
        /// One sample: returns the delayed one and writes the input plus the damped, fed-back output.
        /// </summary>
        public float Tick(float input, float damping)
        {
            float output = _buffer[_index];
            _store = output * (1 - damping) + _store * damping;
            _buffer[_index] = input + _store * Feedback;
            if (++_index == _buffer.Length) _index = 0;
            return output;
        }

        /// <summary>
        /// Silences the delay line and the filter.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_buffer);
            _store = 0;
        }
    }

    /// <summary>
    /// One allpass of the diffusion chain.
    /// </summary>
    sealed class Allpass(int length)
    {
        /// <summary>
        /// The delay line, at least one sample.
        /// </summary>
        readonly float[] _buffer = new float[Math.Max(1, length)];
        /// <summary>
        /// The delay line's position.
        /// </summary>
        int _index;
        /// <summary>
        /// The allpass gain (from the diffusion, <see cref="Configure"/>).
        /// </summary>
        public float Feedback = 0.5f;

        /// <summary>
        /// One sample through the allpass.
        /// </summary>
        public float Tick(float input)
        {
            float delayed = _buffer[_index];
            float w = input + delayed * Feedback;
            float output = delayed - Feedback * w;
            _buffer[_index] = w;
            if (++_index == _buffer.Length) _index = 0;
            return output;
        }

        /// <summary>
        /// Silences the delay line.
        /// </summary>
        public void Clear() => Array.Clear(_buffer);
    }
}