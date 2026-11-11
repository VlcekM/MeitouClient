namespace Meitou.Game.Audio;

/// <summary>
/// One playing WEM: reads a short clip's samples decoded once and shared (<see cref="PcmCache"/>), a long one's Ogg Vorbis stream as it goes
/// (NVorbis); loops it, resamples it to the output rate with the pitch, runs it through a one-pole low-pass and adds it to the mix with a gain per
/// side, ramped across the block so changes do not click.
/// </summary>
sealed class Voice : IDisposable
{
    /// <summary>
    /// The most auxiliary buses a voice can feed (a node lists four user-defined sends).
    /// </summary>
    public const int MaxSends = 4;
    /// <summary>
    /// The clip this voice plays.
    /// </summary>
    readonly MediaClip _clip;
    /// <summary>
    /// The source when the clip streams: its Ogg Vorbis reader, reopened on each rewind; null when the clip's samples are shared (<see cref="_pcm"/>,
    /// read from <see cref="_pcmRead"/>, interleaved).
    /// </summary>
    NVorbis.VorbisReader? _reader;
    /// <summary>
    /// The clip's shared decoded samples, interleaved (<see cref="PcmCache"/>); null when the clip streams.
    /// </summary>
    readonly float[]? _pcm;
    /// <summary>
    /// The next sample of <see cref="_pcm"/> to read.
    /// </summary>
    long _pcmRead;
    /// <summary>
    /// The decoded frames being played, interleaved: room for 4096 frames.
    /// </summary>
    readonly float[] _decoded;
    /// <summary>
    /// How many frames of <see cref="_decoded"/> hold samples.
    /// </summary>
    int _decodedFrames;
    /// <summary>
    /// The play position in <see cref="_decoded"/>, in source frames (fractional: resampling interpolates between two).
    /// </summary>
    double _position;
    /// <summary>
    /// Plays left, counting the current one; 0 forever.
    /// </summary>
    int _playsLeft;
    /// <summary>
    /// How many times the clip is played; 0 forever.
    /// </summary>
    readonly int _plays;
    /// <summary>
    /// Source frames played since the start, across the loops (what a silent voice keeps counting); owed: those skipped while silent, settled on the
    /// next mix (<see cref="CatchUp"/>).
    /// </summary>
    double _cursor, _owed;
    /// <summary>
    /// Source frames still to drop from the start (a music clip's trim), and those still to play after them (-1 no limit).
    /// </summary>
    long _toSkip, _limit;
    /// <summary>
    /// The voice has finished (no more frames); the clip's last play has been decoded.
    /// </summary>
    bool _ended, _final;
    /// <summary>
    /// The gains the last block ended at, per side: the next block ramps from them.
    /// </summary>
    float _lastLeft, _lastRight;
    /// <summary>
    /// The gains each auxiliary send's last block ended at (left and right per send).
    /// </summary>
    readonly float[] _lastSend = new float[2 * MaxSends];
    /// <summary>
    /// Whether the voice has mixed a block yet: the first takes its gains at once, without a ramp.
    /// </summary>
    bool _started;
    /// <summary>
    /// The one-pole low-pass filter's state, per side.
    /// </summary>
    readonly float[] _lowPass = new float[2];
    /// <summary>
    /// The source frames one play decodes to (the decoder's count, the WEM header's when it has none): what the silent clock counts plays in.
    /// </summary>
    readonly long _framesPerPlay;

    /// <summary>
    /// The clip this voice plays.
    /// </summary>
    public MediaClip Clip => _clip;

    /// <summary>
    /// <paramref name="plays"/>: how many times to play the clip; 0 = forever. <paramref name="skipFrames"/> source frames are dropped from the start
    /// and, when <paramref name="limitFrames"/> is not negative, only that many are played after them (music clips' trims).
    /// <paramref name="startPhase"/>: how far into the first source frame (in source frames) the first output frame lies, for a start between two
    /// output frames.
    /// </summary>
    public Voice(MediaClip clip, int plays, long skipFrames = 0, long limitFrames = -1, double startPhase = 0, float[]? pcm = null)
    {
        this._clip = clip;
        _playsLeft = this._plays = plays;
        _toSkip = skipFrames;
        _limit = limitFrames;
        _position = startPhase;
        this._pcm = pcm;
        if (pcm is not null) _framesPerPlay = pcm.Length / clip.Channels;
        else
        {
            _reader = Open();
            try
            {
                _framesPerPlay = _reader.TotalSamples;
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                _framesPerPlay = 0;
            }

            if (_framesPerPlay <= 0) _framesPerPlay = clip.Frames;
        }
        _decoded = new float[4096 * clip.Channels];
    }

    /// <summary>
    /// A fresh Ogg Vorbis reader on the clip's data, at its start.
    /// </summary>
    NVorbis.VorbisReader Open() => new(new MemoryStream(_clip.Ogg, writable: false), closeOnDispose: true);

    /// <summary>
    /// Up to <paramref name="count"/> samples (interleaved) from the source into <paramref name="buffer"/>; 0 at the end of a play.
    /// </summary>
    int ReadSamples(float[] buffer, int offset, int count)
    {
        if (_pcm is null) return _reader!.ReadSamples(buffer, offset, count);
        int n = (int)Math.Min(count, _pcm.Length - _pcmRead);
        Array.Copy(_pcm, _pcmRead, buffer, offset, n);
        _pcmRead += n;
        return n;
    }

    /// <summary>
    /// The source back at the clip's start, for the next play.
    /// </summary>
    void Rewind()
    {
        if (_pcm is not null)
        {
            _pcmRead = 0;
            return;
        }
        _reader!.Dispose();
        _reader = Open();
    }

    /// <summary>
    /// The voice has finished: no more frames.
    /// </summary>
    public bool Ended => _ended;

    /// <summary>
    /// Whether <see cref="Skip"/> can keep this voice's place: a plain clip, not a trimmed music clip.
    /// </summary>
    public bool CanSkip => _toSkip == 0 && _limit < 0 && _framesPerPlay > 0;

    /// <summary>
    /// The voice is silent for <paramref name="frames"/> output frames (the first <paramref name="first"/> not yet started): its clock runs on
    /// without decoding, and the next <see cref="Mix"/> takes it up again (<see cref="CatchUp"/>), fading in from silence; a voice that ran out
    /// meanwhile ends.
    /// </summary>
    public void Skip(int frames, double rate, int first = 0)
    {
        if (_ended) return;
        _owed += (frames - first) * rate;
        if (_plays > 0 && _cursor + _owed >= (double)_plays * _framesPerPlay) _ended = true;
    }

    /// <summary>
    /// Takes the mix up again after the frames skipped while silent, fading in: shared samples at the play and the place the voice's clock says, a
    /// stream where it stopped.
    /// </summary>
    void CatchUp()
    {
        // From silence (it faded out, or was never heard): the first block ramps up to its gains.
        _started = true;
        _lastLeft = _lastRight = 0;
        Array.Clear(_lastSend);

        // A short silence still inside the decoded frames (a sound at the cap's edge): only the place moves.
        if (_position + _owed + 1 < _decodedFrames)
        {
            _position += _owed;
            _cursor += _owed;
            _owed = 0;
            return;
        }

        // A stream is never sought: NVorbis cannot seek in a converted WEM (docs/audio.md "Voice cap"). Its SeekTo left the reader in a read that
        // never returns, which stopped the mixer and every sound until the game was restarted (issues #538, #542), or threw and ended the bed.
        if (_pcm is null)
        {
            _owed = 0;
            return;
        }

        _cursor += _owed;
        _owed = 0;
        long done = (long)(_cursor / _framesPerPlay);
        if (_plays > 0 && done >= _plays)
        {
            _ended = true;
            return;
        }

        long within = Math.Clamp((long)(_cursor - (double)done * _framesPerPlay), 0, _framesPerPlay - 1);
        _pcmRead = within * _clip.Channels;
        _playsLeft = _plays == 0 ? 0 : _plays - (int)done;
        _decodedFrames = 0;
        _position = _cursor - Math.Floor(_cursor);
        _lowPass[0] = _lowPass[1] = 0;
    }

    /// <summary>
    /// Adds <paramref name="frames"/> frames to <paramref name="mix"/> (stereo), the first <paramref name="first"/> of them left silent (a start
    /// inside the block). <paramref name="rate"/> is source frames per output frame; <paramref name="lowPass"/> the one-pole coefficient (1 = no
    /// filtering). Each of <paramref name="sends"/> receives the voice with its own gains (an auxiliary bus's input), ramped like the main ones.
    /// </summary>
    public void Mix(float[] mix, int frames, double rate, float left, float right, float lowPass, int first = 0,
        ReadOnlySpan<VoiceSend> sends = default)
    {
        if (_ended) return;
        if (_owed > 0) CatchUp();
        if (_ended) return;
        if (!_started)
        {
            _lastLeft = left;
            _lastRight = right;
            for (int s = 0; s < sends.Length; s++)
            {
                _lastSend[2 * s] = sends[s].Left;
                _lastSend[2 * s + 1] = sends[s].Right;
            }
            _started = true;
        }
        int channels = _clip.Channels;
        for (int f = first; f < frames; f++)
        {
            int i = (int)_position;
            if (i + 1 >= _decodedFrames && !Refill())
            {
                _ended = true;
                break;
            }
            i = (int)_position;
            float t = (float)(_position - i);
            bool atEnd = i + 1 >= _decodedFrames;
            float l = _decoded[i * channels] + ((atEnd ? 0 : _decoded[(i + 1) * channels]) - _decoded[i * channels]) * t;
            float r = l;
            if (channels > 1) r = _decoded[i * channels + 1] + ((atEnd ? 0 : _decoded[(i + 1) * channels + 1]) - _decoded[i * channels + 1]) * t;
            if (lowPass < 1)
            {
                this._lowPass[0] += lowPass * (l - this._lowPass[0]);
                this._lowPass[1] += lowPass * (r - this._lowPass[1]);
                l = this._lowPass[0];
                r = this._lowPass[1];
            }
            float k = (f - first + 1f) / (frames - first);
            mix[2 * f] += l * (_lastLeft + (left - _lastLeft) * k);
            mix[2 * f + 1] += r * (_lastRight + (right - _lastRight) * k);
            for (int s = 0; s < sends.Length; s++)
            {
                sends[s].Buffer[2 * f] += l * (_lastSend[2 * s] + (sends[s].Left - _lastSend[2 * s]) * k);
                sends[s].Buffer[2 * f + 1] += r * (_lastSend[2 * s + 1] + (sends[s].Right - _lastSend[2 * s + 1]) * k);
            }
            _position += rate;
            _cursor += rate;
        }
        _lastLeft = left;
        _lastRight = right;
        for (int s = 0; s < sends.Length; s++)
        {
            _lastSend[2 * s] = sends[s].Left;
            _lastSend[2 * s + 1] = sends[s].Right;
        }
    }

    /// <summary>
    /// Keeps the last decoded frame (for interpolation) and decodes more after it, looping at the end. False when the clip is done.
    /// </summary>
    bool Refill()
    {
        int channels = _clip.Channels;
        int keep = Math.Min(_decodedFrames, 1);
        int from = _decodedFrames - keep;
        if (keep > 0) Array.Copy(_decoded, from * channels, _decoded, 0, keep * channels);
        _position -= from;
        _decodedFrames = keep;
        while (_decodedFrames < _decoded.Length / channels)
        {
            int n = ReadSamples(_decoded, _decodedFrames * channels, _decoded.Length - _decodedFrames * channels);
            if (n > 0)
            {
                int got = n / channels;
                if (_toSkip > 0)
                {
                    int skip = (int)Math.Min(_toSkip, got);
                    _toSkip -= skip;
                    Array.Copy(_decoded, (_decodedFrames + skip) * channels, _decoded, _decodedFrames * channels, (got - skip) * channels);
                    got -= skip;
                }
                if (_limit >= 0)
                {
                    got = (int)Math.Min(got, _limit);
                    _limit -= got;
                }
                _decodedFrames += got;
                if (_limit == 0)
                {
                    _final = true;
                    break;
                }
                continue;
            }
            if (_playsLeft == 1)
            {
                _final = true;
                break;
            }
            if (_playsLeft > 1) _playsLeft--;
            Rewind();
        }
        // At the end the clip fades into silence over its last frame, so a clip that follows it sample for sample leaves no gap.
        return _position + 1 < _decodedFrames || (_final && _position < _decodedFrames);
    }

    /// <summary>
    /// Closes the stream's reader, if any.
    /// </summary>
    public void Dispose() => _reader?.Dispose();
}

/// <summary>
/// One auxiliary send of a voice: the bus's stereo input buffer and the gains the voice adds to it.
/// </summary>
readonly record struct VoiceSend(float[] Buffer, float Left, float Right);

/// <summary>
/// A WEM converted to Ogg Vorbis, ready to play.
/// </summary>
sealed record MediaClip(uint Id, byte[] Ogg, int Channels, int SampleRate, int Frames);

/// <summary>
/// Short clips decoded once, their samples shared by every voice that plays them (docs/audio.md "Voice cap"): opening a Vorbis stream (its codebooks)
/// costs about 1.7 ms and decoding it again more, per sound, which a big fight's hundreds of footsteps, swings and hits a second cannot afford on the
/// audio thread. A clip longer than <see cref="MaxSeconds"/> (beds, music) or past <see cref="BudgetBytes"/> streams instead. The audio thread only.
/// </summary>
sealed class PcmCache
{
    /// <summary>
    /// Clips longer than this many seconds stream instead of being decoded whole.
    /// </summary>
    public const double MaxSeconds = 20;
    /// <summary>
    /// The most bytes of decoded samples kept (256 MB); a clip that would go past it streams.
    /// </summary>
    public const long BudgetBytes = 256L << 20;
    /// <summary>
    /// The decoded samples by clip id; null for a clip that streams.
    /// </summary>
    readonly Dictionary<uint, float[]?> _clips = [];
    /// <summary>
    /// The bytes of decoded samples held.
    /// </summary>
    long _bytes;

    /// <summary>
    /// The clip's samples (interleaved), decoded the first time; null when it streams.
    /// </summary>
    public float[]? Get(MediaClip clip)
    {
        if (_clips.TryGetValue(clip.Id, out var known)) return known;
        float[]? pcm = null;
        long estimate = (long)clip.Frames * clip.Channels * sizeof(float);
        if (clip.Frames > 0 && clip.Frames <= MaxSeconds * clip.SampleRate && _bytes + estimate <= BudgetBytes) pcm = Decode(clip);
        if (pcm is not null) _bytes += (long)pcm.Length * sizeof(float);
        _clips[clip.Id] = pcm;
        return pcm;
    }

    /// <summary>
    /// The whole clip's samples, interleaved; null when it cannot be decoded (it then streams).
    /// </summary>
    static float[]? Decode(MediaClip clip)
    {
        try
        {
            using var reader = new NVorbis.VorbisReader(new MemoryStream(clip.Ogg, writable: false), closeOnDispose: true);
            var all = new float[Math.Max(clip.Frames, 1) * clip.Channels];
            int filled = 0;
            while (true)
            {
                if (filled == all.Length) Array.Resize(ref all, all.Length * 2);
                int n = reader.ReadSamples(all, filled, all.Length - filled);
                if (n <= 0) break;
                filled += n;
            }
            return filled == all.Length ? all : all[..filled];
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IOException)
        {
            return null;   // streamed as before, which reports nothing either
        }
    }
}