using System.Runtime.CompilerServices;
using Meitou.Data.Characters;
using Meitou.Simulation;

namespace Meitou.Game.Host;

/// <summary>
/// The Hide hats setting (Advanced settings; optional, off by default): where a character's hat is left out of its drawing.
/// </summary>
enum HideHats
{
    /// <summary>
    /// Hats are drawn everywhere, as in the game.
    /// </summary>
    Off,
    /// <summary>
    /// Hats are left out of the interface's portraits only.
    /// </summary>
    PortraitsOnly,
    /// <summary>
    /// Hats are left out of the portraits and of the world.
    /// </summary>
    On
}

/// <summary>
/// The looks of characters without their hats (<see cref="UserConfig.HideHats"/>), made once per look: drawn in the world when the setting is on,
/// and listed hidden under a key of their own for the portraits when it is portraits only.
/// </summary>
sealed class HiddenHatLooks(GameHost host)
{
    /// <summary>
    /// The draw key's bit of a character's hidden hatless instance (bit 54 is its falling head's, bit 55 its hidden next look's).
    /// </summary>
    internal const long KeyBit = 1L << 53;

    /// <summary>
    /// Each look's look without its hat; the look itself when it wears none.
    /// </summary>
    readonly ConditionalWeakTable<CharacterAppearance, CharacterAppearance> _bare = new();

    /// <summary>
    /// The draw keys of the characters whose hatless instance was listed for their portrait in the last draw list.
    /// </summary>
    readonly HashSet<long> _portraits = [];

    /// <summary>
    /// A new draw list begins: no character has a portrait instance yet.
    /// </summary>
    internal void Begin() => _portraits.Clear();

    /// <summary>
    /// The look <paramref name="c"/> is drawn with in the world: without its hat when the setting is on and it applies to the character, once the
    /// renderer has prepared that look (or has not prepared the plain one either). <paramref name="hidden"/>: the hatless look to list hidden,
    /// for the renderer to prepare it or for the portrait (<see cref="ListHidden"/>).
    /// </summary>
    internal CharacterAppearance WorldLook(CharacterSnapshot c, out CharacterAppearance? hidden)
    {
        hidden = null;
        var look = c.Appearance!;
        if (!Applies(c) || Bare(look) is not { } bare || ReferenceEquals(bare, look)) return look;

        bool everywhere = host.Config.HideHats == HideHats.On;
        if (everywhere && (host.Gpu.Characters is not { } characters || characters.IsPrepared(bare) || !characters.IsPrepared(look))) return bare;

        // Portraits only: the hatless look is listed for a character with a portrait alone, not built for everyone in reach.
        if (everywhere || host.PortraitWanted(c.Id)) hidden = bare;
        return look;
    }

    /// <summary>
    /// Lists <paramref name="instance"/>'s character once more, hidden, with its <paramref name="hatless"/> look under the key of its own; with
    /// portraits only, the character's portrait is drawn from it.
    /// </summary>
    internal void ListHidden(List<Rendering.Frame.CharacterInstance> into, in Rendering.Frame.CharacterInstance instance, CharacterAppearance hatless)
    {
        into.Add(instance with { Key = instance.Key ^ KeyBit, Appearance = hatless, Hidden = true, BlendFrom = null });
        if (host.Config.HideHats == HideHats.PortraitsOnly) _portraits.Add(instance.Key);
    }

    /// <summary>
    /// The draw key a portrait of the character with <paramref name="key"/> is drawn from: its hatless instance's when the last draw list had one.
    /// </summary>
    internal long PortraitKey(long key) => _portraits.Contains(key) ? key ^ KeyBit : key;

    /// <summary>
    /// Whether the setting hides <paramref name="c"/>'s hat somewhere: the player's characters, and the others with
    /// <see cref="UserConfig.HideHatsOfOthers"/>.
    /// </summary>
    bool Applies(CharacterSnapshot c) => host.Config.HideHats != HideHats.Off && (c.IsPlayer || host.Config.HideHatsOfOthers);

    /// <summary>
    /// <paramref name="look"/> without its hat, made once; null without a database.
    /// </summary>
    CharacterAppearance? Bare(CharacterAppearance look)
    {
        if (_bare.TryGetValue(look, out var bare)) return bare;
        if (host.Scene.Database is not { } db) return null;

        try
        {
            bare = look.WithoutHat(db, host.Install.Root);
        }
        catch (Exception e) when (e is IOException or Data.Fcs.FcsFormatException or KeyNotFoundException or InvalidDataException
            or UnauthorizedAccessException)
        {
            bare = look;
        }

        _bare.Add(look, bare);
        return bare;
    }
}
