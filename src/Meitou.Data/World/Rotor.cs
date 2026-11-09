using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// A BUILDING_PART that turns by itself (<c>rotation function</c> 1 to 4, the original's <c>RotatingEnt</c>; docs/game/buildings-production.md
/// "Rotating parts"), as the layout made it: <paramref name="SpeedMin"/> and <paramref name="SpeedMax"/> in radians per second (max with its ±5 %
/// roll, raised to min for ROTATION_WIND_SPEED), <paramref name="WindMin"/> and <paramref name="WindMax"/> the wind speeds of <c>wind speed rotation
/// min/max</c> (max raised to min), <paramref name="Start"/> the random angle the part was placed at, <paramref name="ParentX"/> and
/// <paramref name="ParentZ"/> the world directions of its parent's X and Z (the frame a ROTATION_WIND_DIRECTION head turns in).
/// </summary>
public sealed record RotorSpec(int Function, float SpeedMin, float SpeedMax, float WindMin, float WindMax, float Start, Vector3 ParentX,
    Vector3 ParentZ)
{
    /// <summary>
    /// The <c>rotation function</c> values: 1 constant speed, 2 by the building's output, 3 by the wind speed, 4 turning to the wind's direction.
    /// </summary>
    public const int Constant = 1, Output = 2, WindSpeed = 3, WindDirection = 4;

    /// <summary>
    /// The share of a ROTATION_WIND_SPEED part's speed at wind speed <paramref name="wind"/> (<c>RotatingEnt::weatherUpdated</c>, <b>Verified</b>): 0
    /// below <paramref name="min"/>, 1 at
    /// <paramref name="max"/> or above, linear between. What a wind generator's output is multiplied by (its <c>+0x54</c>).
    /// </summary>
    public static float WindShare(float wind, float min, float max) => wind < min ? 0 : wind >= max ? 1 : (wind - min) / (max - min);

    /// <summary>
    /// A ROTATION_WIND_SPEED part's speed: <c>lerp(speed min, speed max, share)</c>, 0 below the minimum wind.
    /// </summary>
    public float WindSpeedAt(float wind) => wind < WindMin ? 0 : SpeedMin + (SpeedMax - SpeedMin) * WindShare(wind, WindMin, WindMax);

    /// <summary>
    /// The angle about its axis (in its parent's frame, as the start angle) a ROTATION_WIND_DIRECTION head aims for with the wind blowing along
    /// <paramref name="wind"/> (world x, z): the wind brought into the parent's frame, flattened, and the head's local X set to it turned by −90°
    /// about Y, so its +Z faces into the wind.
    /// </summary>
    public float HeadAngle(Vector2 wind)
    {
        var d = new Vector3(wind.X, 0, wind.Y);
        float x = Vector3.Dot(d, ParentX), z = Vector3.Dot(d, ParentZ);
        return MathF.Atan2(-x, -z);
    }
}