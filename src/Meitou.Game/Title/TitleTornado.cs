using System.Numerics;

namespace Meitou.Game.Title;

/// <summary>
/// The title fight's easter egg (Meitou's own): one landed blow in <see cref="Odds"/>, and every blow on the special days, throws its victim up into
/// the air, spinning, inside a dust twister, and sets it down where it was. Display only: the fight's world never sees the flight.
/// </summary>
sealed class TitleTornado
{
    /// <summary>
    /// The particle system of the twister: the base game's narrow dust devil (rings 8 and 20 wide, small particles thrown up fast, a vortex).
    /// </summary>
    public const string System = "kenshi_twister_dust2";

    /// <summary>
    /// The dust's colour in place of the system's hue at the funnel's foot and at its top: a faked light (the scene's makes the dust glow orange
    /// against the title's sun), dense dust in shade at the ground brightening to sunlit sand.
    /// </summary>
    static readonly Vector3 FootColour = new(0.7f, 0.6f, 0.48f), TopColour = new(1.35f, 1.27f, 1.12f);

    /// <summary>
    /// The bursts stacked up the funnel.
    /// </summary>
    const int FunnelLevels = 16;
    /// <summary>
    /// The funnel's top above the ground, over the flight's (<see cref="Height"/>).
    /// </summary>
    const float FunnelTop = 330;
    /// <summary>
    /// The scale of the funnel's foot and of its top against the system's own (its rings, particles and speeds).
    /// </summary>
    const float FootStretch = 1, TopStretch = 6;

    /// <summary>
    /// The share of their own life the foot's particles live; a wider level's in proportion less, so every ring climbs about as far (its speeds
    /// grow with it) and the stack keeps its funnel's outline.
    /// </summary>
    const float FootLife = 0.35f;

    /// <summary>
    /// The twister's funnel: bursts of <see cref="System"/> stacked up from the ground, each its height, its scale and its particles' share of
    /// life and its colour, a thin foot flaring out to a wide top.
    /// </summary>
    public static readonly (float Height, float Stretch, float LifeScale, Vector3 Colour)[] Funnel = Enumerable.Range(0, FunnelLevels)
        .Select(FunnelLevel)
        .ToArray();

    /// <summary>
    /// The seconds a flight lasts; the twister emits as long.
    /// </summary>
    public const float FlightSeconds = 4.5f;

    /// <summary>
    /// One landed blow in this many throws its victim.
    /// </summary>
    const int Odds = 500;

    /// <summary>
    /// The top of the flight above the ground, in world units (the fighters are drawn about 45 tall).
    /// </summary>
    const float Height = 210;

    /// <summary>
    /// The shares of the flight spent rising and falling; the fighter hovers at the top between them.
    /// </summary>
    const float Rise = 0.2f, Fall = 0.3f;

    /// <summary>
    /// How far the hovering fighter bobs up and down.
    /// </summary>
    const float Bob = 12;

    /// <summary>
    /// The whole turns a flight spins, so the fighter lands facing the way it left.
    /// </summary>
    const int Turns = 6;

    /// <summary>
    /// The seconds each airborne fighter has flown, by slot.
    /// </summary>
    readonly Dictionary<int, float> _flights = [];

    /// <summary>
    /// The fighters thrown since they were last drawn: their twister starts where they are drawn next.
    /// </summary>
    readonly HashSet<int> _starting = [];

    /// <summary>
    /// The drawn ground positions of the twisters to start (<see cref="TakeTwisters"/>).
    /// </summary>
    readonly List<Vector3> _twisters = [];

    /// <summary>
    /// Whether blows throw anyone (off for screenshots, which must not depend on chance).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A blow landed on the fighter in slot <paramref name="victim"/>: it may fly, unless a fighter is in the air already (one twister at a time:
    /// several at once blend into one cloud).
    /// </summary>
    public void Hit(int victim)
    {
        if (!Enabled || _flights.Count > 0 || !Rolls(DateTime.Today)) return;

        _flights[victim] = 0;
        _starting.Add(victim);
    }

    /// <summary>
    /// Real time: the flights go on, and the ones that landed end.
    /// </summary>
    public void Advance(float seconds)
    {
        foreach (int slot in _flights.Keys.ToArray())
        {
            float flown = _flights[slot] + seconds;
            if (flown >= FlightSeconds) _flights.Remove(slot);
            else _flights[slot] = flown;
        }
    }

    /// <summary>
    /// The fighter in <paramref name="slot"/> is drawn standing at <paramref name="ground"/>: its height above it and its extra turn now (none on the
    /// ground). A fighter just thrown starts its twister there.
    /// </summary>
    public (float Lift, float Spin) Place(int slot, Vector3 ground)
    {
        if (_starting.Remove(slot)) _twisters.Add(ground);
        if (!_flights.TryGetValue(slot, out float flown)) return (0, 0);

        float u = flown / FlightSeconds;
        float spin = u * u * (3 - 2 * u);

        return (Lift(u), MathF.Tau * Turns * spin);
    }

    /// <summary>
    /// The height at share <paramref name="u"/> of the flight: a fast rise that slows to the top, a hover that bobs, then a fall that speeds up.
    /// </summary>
    static float Lift(float u)
    {
        if (u < Rise)
        {
            float r = 1 - u / Rise;
            return Height * (1 - r * r);
        }

        if (u > 1 - Fall)
        {
            float f = (u - (1 - Fall)) / Fall;
            return Height * (1 - f * f);
        }

        return Height + Bob * MathF.Sin(MathF.Tau * (u - Rise) / (1 - Rise - Fall));
    }

    /// <summary>
    /// The funnel's level <paramref name="k"/>: evenly up to <see cref="FunnelTop"/>, its scale growing with the square of its height, its colour lightening
    /// fastest near the ground.
    /// </summary>
    static (float Height, float Stretch, float LifeScale, Vector3 Colour) FunnelLevel(int k)
    {
        float t = k / (float)(FunnelLevels - 1);
        float stretch = FootStretch + (TopStretch - FootStretch) * t * t;
        return (FunnelTop * t, stretch, FootLife / stretch, Vector3.Lerp(FootColour, TopColour, MathF.Sqrt(t)));
    }

    /// <summary>
    /// The twisters to start since the last call, at their fighters' drawn ground positions.
    /// </summary>
    public Vector3[] TakeTwisters()
    {
        var twisters = _twisters.ToArray();
        _twisters.Clear();
        return twisters;
    }

    /// <summary>
    /// Whether a blow on <paramref name="day"/> throws its victim: always on the special days, else one in <see cref="Odds"/>.
    /// </summary>
    static bool Rolls(DateTime day) => SpecialDay(day) || Random.Shared.Next(Odds) == 0;

    /// <summary>
    /// The days every blow throws: 1 April and 25 October.
    /// </summary>
    static bool SpecialDay(DateTime day) =>
        day is { Month: 4, Day: 1 }
        || day is { Month: 10, Day: 25 };   // _sashkaa's irl birthday yay
}
