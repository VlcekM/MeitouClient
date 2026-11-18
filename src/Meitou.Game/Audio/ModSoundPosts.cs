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
    /// point sound distance (<see cref="CharacterSounds.EventLimit"/>, squared, from <paramref name="eye"/>).
    /// </summary>
    public void At(Vector3 at, Vector3 eye, string eventName, string? group, string? value)
    {
        if (player.HeardDistanceSquared(at, eye) >= CharacterSounds.EventLimit) return;

        ulong id = PoolBase + _next++ % PoolSize;
        player.SetPosition(id, at);
        if (group is not null && value is not null) player.SetSwitch(group, value, id);
        player.Post(eventName, id);
    }

    /// <summary>
    /// <paramref name="eventName"/> on the mods' interface object, after the optional switch.
    /// </summary>
    public void OnInterface(string eventName, string? group, string? value)
    {
        if (group is not null && value is not null) player.SetSwitch(group, value, InterfaceObject);
        player.Post(eventName, InterfaceObject);
    }
}
