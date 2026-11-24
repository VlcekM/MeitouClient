using Meitou.Data.Audio;

namespace Meitou.Game.Audio;

/// <summary>
/// Loose sound files (docs/modding.md "Sounds"): a code mod's 16-bit PCM WAV played as one Wwise sound made for it, on a game object, through the
/// SFX bus when placed (so the SFX slider and the pause ducking take it, as the game's effects) or the master bus on the interface.
/// </summary>
public sealed partial class WwisePlayer
{
    /// <summary>
    /// The SFX bus (<c>Init.bnk</c>, docs/audio.md "Pause and speed").
    /// </summary>
    public const uint SfxBus = 0x59927CD0;

    /// <summary>
    /// The first media and object id of the loose sounds: above what bank hashes take in practice (Meitou's choice).
    /// </summary>
    const uint FirstLooseId = 0xFFF00000;

    /// <summary>
    /// The attenuation of a placed loose sound (Meitou's choice): full at the object, falling linearly to silence at
    /// <see cref="LooseReach"/> units (times <see cref="Reach"/>).
    /// </summary>
    const float LooseReach = 1000;

    /// <summary>
    /// The longest loose sound, in seconds.
    /// </summary>
    public const double MaxLooseSeconds = 20;

    /// <summary>
    /// The loose sounds' media ids by file (the audio thread's).
    /// </summary>
    readonly Dictionary<string, uint> _looseMedia = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The attenuation of the placed loose sounds.
    /// </summary>
    static readonly WwiseAttenuation LooseAttenuation = new(FirstLooseId, null, [0, -1, 0, -1],
        [new WwiseAttenuationCurve(2, [new WwiseCurvePoint(0, 0, 4), new WwiseCurvePoint(LooseReach, -1, 4)])], []);

    /// <summary>
    /// The next object id a loose sound's node takes.
    /// </summary>
    uint _nextLoose = FirstLooseId + 1;

    /// <summary>
    /// Reads a loose sound: its samples, or null when the file is not a 16-bit PCM WAV of at most <see cref="MaxLooseSeconds"/> with a frame.
    /// </summary>
    public static WwisePcm? ReadLoose(string path)
    {
        var pcm = WwisePcm.TryRead(File.ReadAllBytes(path));
        if (pcm is null || pcm.Frames == 0 || pcm.Frames > MaxLooseSeconds * pcm.SampleRate) return null;

        return pcm;
    }

    /// <summary>
    /// Plays <paramref name="sound"/> (read by <see cref="ReadLoose"/>, kept by <paramref name="key"/>, its file) once on
    /// <paramref name="gameObject"/>, <paramref name="volumeDb"/> louder or softer: placed at the object and attenuated by distance when
    /// <paramref name="placed"/>, else heard at the listener (any thread; run at the next block); tagged with <paramref name="playing"/>
    /// (<see cref="NewPlaying"/>, 0 none) for <see cref="StopPlaying"/>.
    /// </summary>
    public void PostLoose(string key, WwisePcm sound, ulong gameObject, float volumeDb, bool placed, uint playing = 0)
    {
        ObjectState(gameObject);
        _commands.Enqueue(() => PlayLoose(key, sound, gameObject, volumeDb, placed, playing));
    }

    /// <summary>
    /// On the audio thread: files the samples once, then plays them as a new sound node on the object.
    /// </summary>
    void PlayLoose(string key, WwisePcm sound, ulong gameObject, float volumeDb, bool placed, uint playing)
    {
        if (!_looseMedia.TryGetValue(key, out uint media))
        {
            media = _nextLoose++;
            _looseMedia[key] = media;
            _media[media] = new MediaClip(media, [], sound.Channels, sound.SampleRate, sound.Frames, sound.Samples);
            _objects.TryAdd(LooseAttenuation.Id, LooseAttenuation);
        }

        var node = new WwiseSound(_nextLoose++, new WwiseSource(0x00010001, 0, media, 0, 0, 0, 0, []), LooseNode(volumeDb, placed));
        _postingObject = gameObject;
        _postingPlay = playing;
        try
        {
            if (Create(node, null) is not { } instance) return;

            _roots.Add(instance);
            instance.Start();
        }
        finally
        {
            _postingObject = 0;
            _postingPlay = 0;
        }
    }

    /// <summary>
    /// A loose sound's node: its volume, the SFX bus and the loose attenuation when placed, else 2D on the master bus.
    /// </summary>
    static WwiseNodeBase LooseNode(float volumeDb, bool placed)
    {
        var values = new Dictionary<byte, uint> { [WwisePropertyId.Volume] = BitConverter.SingleToUInt32Bits(volumeDb) };
        var properties = new WwiseProperties(values, new Dictionary<byte, (float, float)>());
        var positioning = new WwisePositioning(placed ? (byte)9 : (byte)1, 0, placed ? LooseAttenuation.Id : 0, 0, 0, [], [], []);
        return new WwiseNodeBase(false, [], 0, 0, placed ? SfxBus : 0, 0, 0, properties, positioning, 0, [], 0, 0, 0, 0, 0, [], []);
    }
}
