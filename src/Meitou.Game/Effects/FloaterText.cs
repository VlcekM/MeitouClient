using System.Globalization;
using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// The "Show damage floaters" option (the original's <c>floaters</c>: Off 0, Simple info 1, Full info 2).
/// </summary>
enum FloaterMode
{
    /// <summary>
    /// 0: no damage floaters (the other floaters still show their text).
    /// </summary>
    Off,
    /// <summary>
    /// 1: the whole-number total, with the armour's share.
    /// </summary>
    Simple,
    /// <summary>
    /// 2: per part: cut (with pierce), blunt with stun, each with its armour's share.
    /// </summary>
    Full
}

/// <summary>
/// The words and colours of the floating numbers (docs/game/ui-screens.md "Damage floaters", <c>FUN_1406508d0</c>).
/// </summary>
static class FloaterText
{
    /// <summary>
    /// The game's default floater colour, and the two it tints in (0.9 alpha).
    /// </summary>
    public static readonly Vector4 Grey = new(0.9f, 0.9f, 0.9f, 0.9f), Green = new(0.11f, 0.6f, 0.11f, 0.9f), Red = new(1f, 0.31f, 0.31f, 0.9f);

    /// <summary>
    /// Simple: the whole-number total, then the armour's share in brackets when it is above 0 ("12 (-5)"). Full: the part, then cut (with pierce when
    /// above 1), and blunt with stun when the blunt is 1 or more, each with its own bracket ("Left Arm: 8 (-3) + 4"). Null when there is nothing to
    /// show.
    /// </summary>
    public static string? Format(in Floater f, FloaterMode mode)
    {
        // The eating text, machine labels, knock-out and pick results, door damage: the text as it is.
        if (f.Kind is not FloaterKind.Damage) return f.Part.Length > 0 ? f.Part : null;
        if (mode == FloaterMode.Off) return null;
        var d = f.Damage;
        var a = f.Absorbed;
        if (mode == FloaterMode.Simple)
        {
            string total = Whole(d.Cut + d.Blunt + d.Pierce + d.ExtraStun);
            int stopped = (int)(a.Cut + a.Blunt + a.Pierce);
            return stopped > 0 ? $"{total} (-{stopped})" : total;
        }
        var text = new System.Text.StringBuilder(f.Part).Append(": ");
        bool withPierce = d.Pierce > 1;
        text.Append(Whole(withPierce ? d.Pierce + d.Cut : d.Cut));
        float firstStopped = withPierce ? a.Pierce + a.Cut : a.Cut;
        if (firstStopped > 0) text.Append(" (-").Append(Whole(firstStopped)).Append(')');
        if (d.Blunt >= 1)
        {
            text.Append(" + ").Append(Whole(d.Blunt + d.ExtraStun));
            if (a.Blunt > 0) text.Append(" (-").Append(Whole(a.Blunt)).Append(')');
        }
        return text.ToString();
    }

    /// <summary>
    /// Red when a player's character is hit, green when one lands the blow (Meitou's reading of the original's two relation tints), else grey.
    /// </summary>
    public static Vector4 Colour(in Floater f) => f.Kind switch
    {
        FloaterKind.Text => White,
        FloaterKind.Label or FloaterKind.Building => LabelGrey,
        FloaterKind.StealthHit or FloaterKind.PickSuccess => StealthHit,
        FloaterKind.StealthMiss or FloaterKind.PickFailed => StealthMiss,
        _ => HitColour(f),
    };

    /// <summary>
    /// The blow's tint (<c>FUN_1406508d0</c> end, <b>Verified</b>): grey; green when the attacker's faction is the player's or the active squad's
    /// memory holds TEMP ENEMY on the target; then red, which wins, when the target's faction is the player's or the memory holds TEMP ALLY on it.
    /// </summary>
    static Vector4 HitColour(in Floater f) => f.TargetIsPlayer || f.TargetTempAlly ? Red : f.SourceIsPlayer || f.TargetTempEnemy ? Green : Grey;

    /// <summary>
    /// A stealth knock-out's result colours (<c>FUN_14034cd80</c>, <b>Verified</b> literals): "*Thunk*" (0.4, 0.8, 0.4) at 0.9 alpha, "Failed!" (0.7,
    /// 0.4, 0.4) at 0.7. A lock pick roll's "Pick success!" / "Pick failed" use the same constants (<c>FUN_140359160</c>, <b>Verified</b>).
    /// </summary>
    public static readonly Vector4 StealthHit = new(0.4f, 0.8f, 0.4f, 0.9f), StealthMiss = new(0.7f, 0.4f, 0.4f, 0.7f);

    /// <summary>
    /// The text floaters' colour: white at 0.9 alpha (<c>FUN_1405d0780</c>).
    /// </summary>
    public static readonly Vector4 White = new(1, 1, 1, 0.9f);

    /// <summary>
    /// A machine label's colour: grey 0.7 at 0.8 alpha (<c>FUN_1403567a0</c>).
    /// </summary>
    public static readonly Vector4 LabelGrey = new(0.7f, 0.7f, 0.7f, 0.8f);

    /// <summary>
    /// The value cut to a whole number, in the invariant culture.
    /// </summary>
    static string Whole(float v) => ((int)v).ToString(CultureInfo.InvariantCulture);
}