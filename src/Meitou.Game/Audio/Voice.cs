namespace Meitou.Audio;

/// <summary>
/// One playing WEM: reads a short clip's samples decoded once and shared (<see cref="PcmCache"/>), a long one's Ogg Vorbis stream as it goes (NVorbis); loops it, resamples it to the output rate with the pitch, runs it
/// through a one-pole low-pass and adds it to the mix with a gain per side, ramped across the block so changes do not click.
/// </summary>
sealed class Voice : IDisposable
{
    readonly MediaClip clip;

    public MediaClip Clip => clip;
    // The source: the shared decoded samples (pcm, read from pcmRead, interleaved), else the stream.
    NVorbis.VorbisReader? reader;
    readonly float[]? pcm;
    long pcmRead;
    readonly float[] decoded;
    int decodedFrames;
    double position;
    int playsLeft;
    readonly int plays;
    // Source frames played since the start, across the loops (what a silent voice keeps counting); owed: those skipped while silent, sought on the next mix.
    double cursor, owed;
    long toSkip, limit;
    bool ended, final;
    float lastLeft, lastRight;
    readonly float[] lastSend = new float[2 * MaxSends];
    bool started;

    /// <summary>The most auxiliary buses a voice can feed (a node lists four user-defined sends).</summary>
    public const int MaxSends = 4;
    readonly float[] lowPass = new float[2];

    /// <summary>
    /// <paramref name="plays"/>: how many times to play the clip; 0 = forever. <paramref name="skipFrames"/> source frames are dropped from the
    /// start and, when <paramref name="limitFrames"/> is not negative, only that many are played after them (music clips' trims).
    /// <paramref name="startPhase"/>: how far into the first source frame (in source frames) the first output frame lies, for a start between two output frames.
    /// </summary>
    public Voice(MediaClip clip, int plays, long skipFrames = 0, long limitFrames = -1, double startPhase = 0, float[]? pcm = null)
    {
        this.clip = clip;
        playsLeft = this.plays = plays;
        toSkip = skipFrames;
        limit = limitFrames;
        position = startPhase;
        this.pcm = pcm;
        if (pcm is not null) framesPerPlay = pcm.Length / clip.Channels;
        else
        {
            reader = Open();
            try { framesPerPlay = reader.TotalSamples; } catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException) { framesPerPlay = 0; }
            if (framesPerPlay <= 0) framesPerPlay = clip.Frames;
        }
        decoded = new float[4096 * clip.Channels];
    }

    NVorbis.VorbisReader Open() => new(new MemoryStream(clip.Ogg, writable: false), closeOnDispose: true);

    /// <summary>Up to <paramref name="count"/> samples (interleaved) from the source into <paramref name="buffer"/>; 0 at the end of a play.</summary>
    int ReadSamples(float[] buffer, int offset, int count)
    {
        if (pcm is null) return reader!.ReadSamples(buffer, offset, count);
        int n = (int)Math.Min(count, pcm.Length - pcmRead);
        Array.Copy(pcm, pcmRead, buffer, offset, n);
        pcmRead += n;
        return n;
    }

    /// <summary>The source back at <paramref name="frame"/> of the clip (0: the start of the next play).</summary>
    void Rewind(long frame = 0)
    {
        if (pcm is not null) { pcmRead = frame * clip.Channels; return; }
        reader!.Dispose();
        reader = Open();
        if (frame > 0) reader.SeekTo(frame);
    }

    public bool Ended => ended;

    /// <summary>Whether <see cref="Skip"/> can keep this voice's place: a plain clip, not a trimmed music clip.</summary>
    public bool CanSkip => toSkip == 0 && limit < 0 && framesPerPlay > 0;

    // The source frames one play decodes to (the decoder's count, the WEM header's when it has none): what the silent clock counts plays in.
    readonly long framesPerPlay;

    /// <summary>
    /// The voice is silent for <paramref name="frames"/> output frames (the first <paramref name="first"/> not yet started): its clock runs on without decoding,
    /// and the next <see cref="Mix"/> seeks to where it would be, fading in from silence; a voice that ran out meanwhile ends.
    /// </summary>
    public void Skip(int frames, double rate, int first = 0)
    {
        if (ended) return;
        owed += (frames - first) * rate;
        if (plays > 0 && cursor + owed >= (double)plays * framesPerPlay) ended = true;
    }

    /// <summary>Seeks past the frames skipped while silent: the play and the place in the clip from the voice's clock.</summary>
    void CatchUp()
    {
        // From silence (it faded out, or was never heard): the first block ramps up to its gains.
        started = true;
        lastLeft = lastRight = 0;
        Array.Clear(lastSend);
        // A short silence still inside the decoded frames (a sound at the cap's edge): only the place moves.
        if (position + owed + 1 < decodedFrames)
        {
            position += owed;
            cursor += owed;
            owed = 0;
            return;
        }
        cursor += owed;
        owed = 0;
        long done = (long)(cursor / framesPerPlay);
        if (plays > 0 && done >= plays) { ended = true; return; }
        long within = Math.Clamp((long)(cursor - (double)done * framesPerPlay), 0, framesPerPlay - 1);
        try { Rewind(within); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or IOException)
        {
            // A seek the stream does not allow ends the voice, never the mix.
            ended = true;
            return;
        }
        playsLeft = plays == 0 ? 0 : plays - (int)done;
        decodedFrames = 0;
        position = cursor - Math.Floor(cursor);
        lowPass[0] = lowPass[1] = 0;
    }

    /// <summary>
    /// Adds <paramref name="frames"/> frames to <paramref name="mix"/> (stereo), the first <paramref name="first"/> of them left silent (a
    /// start inside the block). <paramref name="rate"/> is source frames per output frame; <paramref name="lowPass"/> the one-pole
    /// coefficient (1 = no filtering). Each of <paramref name="sends"/> receives the voice with its own gains (an auxiliary bus's input), ramped like the main ones.
    /// </summary>
    public void Mix(float[] mix, int frames, double rate, float left, float right, float lowPass, int first = 0, ReadOnlySpan<VoiceSend> sends = default)
    {
        if (ended) return;
        if (owed > 0) CatchUp();
        if (ended) return;
        if (!started)
        {
            lastLeft = left;
            lastRight = right;
            for (int s = 0; s < sends.Length; s++)
            {
                lastSend[2 * s] = sends[s].Left;
                lastSend[2 * s + 1] = sends[s].Right;
            }
            started = true;
        }
        int channels = clip.Channels;
        for (int f = first; f < frames; f++)
        {
            int i = (int)position;
            if (i + 1 >= decodedFrames && !Refill())
            {
                ended = true;
                break;
            }
            i = (int)position;
            float t = (float)(position - i);
            bool atEnd = i + 1 >= decodedFrames;
            float l = decoded[i * channels] + ((atEnd ? 0 : decoded[(i + 1) * channels]) - decoded[i * channels]) * t;
            float r = channels > 1 ? decoded[i * channels + 1] + ((atEnd ? 0 : decoded[(i + 1) * channels + 1]) - decoded[i * channels + 1]) * t : l;
            if (lowPass < 1)
            {
                this.lowPass[0] += lowPass * (l - this.lowPass[0]);
                this.lowPass[1] += lowPass * (r - this.lowPass[1]);
                l = this.lowPass[0];
                r = this.lowPass[1];
            }
            float k = (f - first + 1f) / (frames - first);
            mix[2 * f] += l * (lastLeft + (left - lastLeft) * k);
            mix[2 * f + 1] += r * (lastRight + (right - lastRight) * k);
            for (int s = 0; s < sends.Length; s++)
            {
                sends[s].Buffer[2 * f] += l * (lastSend[2 * s] + (sends[s].Left - lastSend[2 * s]) * k);
                sends[s].Buffer[2 * f + 1] += r * (lastSend[2 * s + 1] + (sends[s].Right - lastSend[2 * s + 1]) * k);
            }
            position += rate;
            cursor += rate;
        }
        lastLeft = left;
        lastRight = right;
        for (int s = 0; s < sends.Length; s++)
        {
            lastSend[2 * s] = sends[s].Left;
            lastSend[2 * s + 1] = sends[s].Right;
        }
    }

    /// <summary>Keeps the last decoded frame (for interpolation) and decodes more after it, looping at the end. False when the clip is done.</summary>
    bool Refill()
    {
        int channels = clip.Channels;
        int keep = Math.Min(decodedFrames, 1);
        int from = decodedFrames - keep;
        if (keep > 0) Array.Copy(decoded, from * channels, decoded, 0, keep * channels);
        position -= from;
        decodedFrames = keep;
        while (decodedFrames < decoded.Length / channels)
        {
            int n = ReadSamples(decoded, decodedFrames * channels, decoded.Length - decodedFrames * channels);
            if (n > 0)
            {
                int got = n / channels;
                if (toSkip > 0)
                {
                    int skip = (int)Math.Min(toSkip, got);
                    toSkip -= skip;
                    Array.Copy(decoded, (decodedFrames + skip) * channels, decoded, decodedFrames * channels, (got - skip) * channels);
                    got -= skip;
                }
                if (limit >= 0)
                {
                    got = (int)Math.Min(got, limit);
                    limit -= got;
                }
                decodedFrames += got;
                if (limit == 0)
                {
                    final = true;
                    break;
                }
                continue;
            }
            if (playsLeft == 1)
            {
                final = true;
                break;
            }
            if (playsLeft > 1) playsLeft--;
            Rewind();
        }
        // At the end the clip fades into silence over its last frame, so a clip that follows it sample for sample leaves no gap.
        return position + 1 < decodedFrames || (final && position < decodedFrames);
    }

    public void Dispose() => reader?.Dispose();
}

/// <summary>One auxiliary send of a voice: the bus's stereo input buffer and the gains the voice adds to it.</summary>
readonly record struct VoiceSend(float[] Buffer, float Left, float Right);

/// <summary>A WEM converted to Ogg Vorbis, ready to play.</summary>
sealed record MediaClip(uint Id, byte[] Ogg, int Channels, int SampleRate, int Frames);

/// <summary>
/// Short clips decoded once, their samples shared by every voice that plays them (docs/audio.md "Voice cap"): opening a Vorbis stream (its codebooks) costs
/// about 1.7 ms and decoding it again more, per sound, which a big fight's hundreds of footsteps, swings and hits a second cannot afford on the audio thread.
/// A clip longer than <see cref="MaxSeconds"/> (beds, music) or past <see cref="BudgetBytes"/> streams instead. The audio thread only.
/// </summary>
sealed class PcmCache
{
    public const double MaxSeconds = 20;
    public const long BudgetBytes = 256L << 20;
    readonly Dictionary<uint, float[]?> clips = [];
    long bytes;

    /// <summary>The clip's samples (interleaved), decoded the first time; null when it streams.</summary>
    public float[]? Get(MediaClip clip)
    {
        if (clips.TryGetValue(clip.Id, out var known)) return known;
        float[]? pcm = null;
        long estimate = (long)clip.Frames * clip.Channels * sizeof(float);
        if (clip.Frames > 0 && clip.Frames <= MaxSeconds * clip.SampleRate && bytes + estimate <= BudgetBytes) pcm = Decode(clip);
        if (pcm is not null) bytes += (long)pcm.Length * sizeof(float);
        clips[clip.Id] = pcm;
        return pcm;
    }

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
