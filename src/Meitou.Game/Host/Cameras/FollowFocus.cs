using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Game.Host.Cameras;

/// <summary>
/// Meitou's follow camera offset (the Advanced page's Follow camera offset, docs/formats/camera.md "Follow camera offset"; the original has none):
/// moves the point the strategy camera looks at while it follows a character, up from the game's (the character's position +
/// <see cref="Meitou.Data.World.KenshiCamera.TrackHeight"/>) by a set raise or to the head bone's height, and sideways for a view over the
/// shoulder. The eye hangs off that point at the same distance, so the character keeps its place on the screen while the camera turns.
/// </summary>
public sealed class FollowFocus
{
    /// <summary>
    /// The raise's range and default, in world units above the game's point: 0 is the game's height, about 17 a person's head.
    /// </summary>
    public const float MinRaise = -10, MaxRaise = 30, DefaultRaise = 17;

    /// <summary>
    /// The most the point moves to either side, in world units.
    /// </summary>
    public const float MaxSide = 40;

    /// <summary>
    /// The character the last head height was seen on.
    /// </summary>
    CharacterId _headOf = CharacterId.None;

    /// <summary>
    /// How far above the game's point that character's head bone was when last drawn: kept for the frames it is not drawn, so the camera does not
    /// jump between the head's height and the raise.
    /// </summary>
    float? _headRise;

    /// <summary>
    /// The game's <paramref name="target"/> on character <paramref name="who"/> moved: up by <paramref name="raise"/>, or with
    /// <paramref name="headLock"/> to the height of <paramref name="head"/> (the head bone as last drawn; null: not drawn, its last height over the
    /// target then, the raise until it was ever seen), then <paramref name="side"/> units to the right of a camera turned <paramref name="yaw"/>
    /// (positive puts the character left of the middle).
    /// </summary>
    public Vector3 Apply(Vector3 target, float yaw, CharacterId who, float raise, float side, bool headLock, Vector3? head)
    {
        if (who != _headOf) (_headOf, _headRise) = (who, null);
        if (headLock && head is { } drawn) _headRise = drawn.Y - target.Y;

        target.Y += headLock && _headRise is { } rise ? rise : raise;

        // The camera's right on the ground: it looks along (-sin yaw, 0, -cos yaw).
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        return target + right * side;
    }
}
