using System.Numerics;

namespace Meitou.Game.Audio;

/// <summary>
/// The code mods' posted sound events that do not go on a character's own object (docs/modding.md "Sounds"): a point event, or a character event
/// with a switch, goes on one of the mods' own pooled objects placed there, and an interface event on the mods' own unplaced object (heard at the
/// listener, as the game's GUI object is). The game's objects never get a mod's switch, so the game's own sounds stay as they were.
/// </summary>
sealed class ModSoundPosts(WwisePlayer player)
{
    /// <summary>
    /// The mods' interface object; never placed, so its sounds are heard at the listener. Above the other hosts' ranges (doors 2^40, turrets
    /// 2^41, fed bodies 2^42).
    /// </summary>
    public const ulong InterfaceObject = 1UL << 43;

    /// <summary>
    /// The first of the mods' pooled point objects (<see cref="PoolSize"/> of them, after <see cref="InterfaceObject"/>).
    /// </summary>
    public const ulong PoolBase = InterfaceObject + 1;

    /// <summary>
    /// How many pooled point objects the mods share, used round robin.
    /// </summary>
    public const int PoolSize = 8;

    /// <summary>
    /// The next pooled object (wraps; unsigned, so it never turns negative).
    /// </summary>
    uint _next;

    /// <summary>
    /// <paramref name="eventName"/> at <paramref name="at"/>: on the next pooled object placed there after the optional switch, within the game's
    /// point sound distance (<see cref="CharacterSounds.EventLimit"/>, squared, from <paramref name="eye"/>); its sounds belong to
    /// <paramref name="playing"/> (<see cref="WwisePlayer.NewPlaying"/>, 0 none).
    /// </summary>
    public void At(Vector3 at, Vector3 eye, string eventName, string? group, string? value, uint playing = 0)
    {
        if (player.HeardDistanceSquared(at, eye) >= CharacterSounds.EventLimit) return;

        ulong id = PoolBase + _next++ % PoolSize;
        player.SetPosition(id, at);
        if (group is not null && value is not null) player.SetSwitch(group, value, id);
        player.Post(eventName, id, playing);
    }

    /// <summary>
    /// A loose sound file (<see cref="WwisePlayer.PostLoose"/>) on the mods' interface object, heard at the listener.
    /// </summary>
    public void LooseOnInterface(string path, Meitou.Data.Audio.WwisePcm sound, float volumeDb, uint playing = 0) =>
        player.PostLoose(path, sound, InterfaceObject, volumeDb, placed: false, playing);

    /// <summary>
    /// A loose sound file on the next pooled object, placed at <paramref name="at"/>; nothing beyond the event limit from <paramref name="eye"/>.
    /// </summary>
    public void LooseAt(Vector3 at, Vector3 eye, string path, Meitou.Data.Audio.WwisePcm sound, float volumeDb, uint playing = 0)
    {
        if (player.HeardDistanceSquared(at, eye) >= CharacterSounds.EventLimit) return;

        ulong id = PoolBase + _next++ % PoolSize;
        player.SetPosition(id, at);
        player.PostLoose(path, sound, id, volumeDb, placed: true, playing);
    }

    /// <summary>
    /// The loose files read, by path (null: not playable).
    /// </summary>
    readonly Dictionary<string, Meitou.Data.Audio.WwisePcm?> _loose = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The samples of the loose file <paramref name="path"/>, read once; null (logged once) when it is not a short 16-bit PCM WAV.
    /// </summary>
    public Meitou.Data.Audio.WwisePcm? Loose(string path)
    {
        if (_loose.TryGetValue(path, out var known)) return known;

        Meitou.Data.Audio.WwisePcm? pcm = null;
        try
        {
            pcm = WwisePlayer.ReadLoose(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (pcm is null)
            Console.Error.WriteLine($"audio     loose sound {path}: not a 16-bit PCM WAV of at most {WwisePlayer.MaxLooseSeconds} s; plays nothing");
        _loose[path] = pcm;
        return pcm;
    }

    /// <summary>
    /// <paramref name="eventName"/> on the mods' interface object, after the optional switch.
    /// </summary>
    public void OnInterface(string eventName, string? group, string? value, uint playing = 0)
    {
        if (group is not null && value is not null) player.SetSwitch(group, value, InterfaceObject);
        player.Post(eventName, InterfaceObject, playing);
    }
}
